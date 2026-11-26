namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>
/// Decides what to do by comparing, for every path, the local state, the remote state and the last synced state
/// (the base). Pure logic: no I/O, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// Rules, per path:
/// <list type="bullet">
/// <item>Only one side changed since the base: apply that change to the other side (create, update, delete).</item>
/// <item>Both changed: an edit wins over a deletion; two folders merge; two different files are both kept, the
/// local one under a conflict name (the executor first checks whether their content is in fact identical).</item>
/// <item>Deleting a folder only happens if nothing inside it changed on the side being deleted from; otherwise the
/// folder is re-created on the other side and only the unchanged items in it are deleted.</item>
/// <item>A node moved or renamed on the server is moved locally instead of downloaded again.</item>
/// <item>An item moved or renamed locally is moved on the server instead of uploaded again (see
/// <see cref="PlanLocalMoves"/> for how a local move is recognized).</item>
/// </list>
/// Deletions are never permanent: the executor moves items to the Proton trash or to a local trash folder.
/// </remarks>
internal static class SyncPlanner
{
    public static SyncPlan Plan(
        IReadOnlyDictionary<string, LocalItem> local,
        IReadOnlyDictionary<string, RemoteItem> remote,
        IReadOnlyDictionary<string, BaseItem> baseItems)
    {
        var localMap = new Dictionary<string, LocalItem>(local, StringComparer.Ordinal);
        var baseMap = new Dictionary<string, BaseItem>(baseItems, StringComparer.Ordinal);
        var actions = new List<SyncAction>();

        var remoteMap = new Dictionary<string, RemoteItem>(remote, StringComparer.Ordinal);

        PlanRemoteMoves(localMap, remote, baseMap, actions);
        PlanLocalMoves(localMap, remoteMap, baseMap, actions);

        var decisions = new SortedDictionary<string, List<SyncAction>>(StringComparer.Ordinal);
        var paths = new SortedSet<string>(localMap.Keys, StringComparer.Ordinal);
        paths.UnionWith(remoteMap.Keys);
        paths.UnionWith(baseMap.Keys);

        foreach (var path in paths)
        {
            localMap.TryGetValue(path, out var l);
            remoteMap.TryGetValue(path, out var r);
            baseMap.TryGetValue(path, out var b);

            var pathActions = Decide(path, l, r, b);
            if (pathActions.Count > 0)
            {
                decisions[path] = pathActions;
            }
        }

        ResolveFolderDeletions(decisions);

        actions.AddRange(decisions.Values.SelectMany(a => a));

        int CountDeleted(Func<SyncAction, bool> side) => actions
            .Where(side)
            .Sum(a => baseMap.Keys.Count(p => SyncPaths.IsSameOrUnder(p, a.Path)));

        // Stable sort: moves keep the order they were planned in (each one assumes the earlier ones are done) and come
        // first in their phase; otherwise, within a phase, paths sort parents first.
        var ordered = actions
            .Select((action, index) => (action, index))
            .OrderBy(x => x.action.Phase)
            .ThenBy(x => x.action.IsMove ? 0 : 1)
            .ThenBy(x => x.action.IsMove ? string.Empty : x.action.Path, StringComparer.Ordinal)
            .ThenBy(x => x.index)
            .Select(x => x.action)
            .ToList();

        return new SyncPlan
        {
            Actions = ordered,
            LocalDeletedItemCount = CountDeleted(a => a.IsLocalDeletion),
            RemoteDeletedItemCount = CountDeleted(a => a.IsRemoteDeletion),
            BaseItemCount = baseItems.Count,
        };
    }

    /// <summary>
    /// Detects nodes whose remote path differs from their base path while the local item is still unchanged at the
    /// old path, and plans a local move. The local and base maps are rewritten as if the move had happened, so the
    /// per-path comparison that follows sees the moved items at their new paths.
    /// </summary>
    private static void PlanRemoteMoves(
        Dictionary<string, LocalItem> local,
        IReadOnlyDictionary<string, RemoteItem> remote,
        Dictionary<string, BaseItem> baseMap,
        List<SyncAction> actions)
    {
        var remoteByUid = remote.Values.ToDictionary(r => r.NodeUid, StringComparer.Ordinal);

        while (true)
        {
            var move = baseMap.Values
                .Where(b => remoteByUid.TryGetValue(b.NodeUid, out var r) && r.Path != b.Path && r.IsFolder == b.IsFolder)
                .Select(b => (Base: b, Remote: remoteByUid[b.NodeUid]))
                .Where(m => IsMovable(m.Base, m.Remote, local, baseMap))
                .OrderBy(m => SyncPaths.Depth(m.Base.Path))
                .ThenBy(m => m.Base.Path, StringComparer.Ordinal)
                .FirstOrDefault();

            if (move.Base is null)
            {
                return;
            }

            var from = move.Base.Path;
            var to = move.Remote.Path;
            actions.Add(new SyncAction(SyncActionKind.MoveLocal, from) { TargetPath = to, Local = local[from], Remote = move.Remote, Base = move.Base });

            foreach (var path in local.Keys.Where(p => SyncPaths.IsSameOrUnder(p, from)).ToList())
            {
                var item = local[path];
                local.Remove(path);
                var newPath = SyncPaths.Rebase(path, from, to);
                local[newPath] = item with { Path = newPath };
            }

            foreach (var path in baseMap.Keys.Where(p => SyncPaths.IsSameOrUnder(p, from)).ToList())
            {
                var item = baseMap[path];
                baseMap.Remove(path);
                var newPath = SyncPaths.Rebase(path, from, to);
                baseMap[newPath] = item with { Path = newPath };
            }
        }
    }

    /// <summary>
    /// Detects items moved or renamed locally and plans a move on the server, so they are not trashed there and
    /// uploaded again. The local file system gives items no lasting identity, so a move is recognized by what
    /// disappeared and what appeared: a synced item missing at its old path (and unchanged on the server), and a new
    /// local item that is not on the server.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A file matches when its size and modification time (to the tick) are the same, and no other missing or new
    /// file has that size and time. Moving or renaming keeps a file's modification time. Empty files are not matched:
    /// they are as cheap to upload again.</item>
    /// <item>A folder matches when at least half of the files it held are found unchanged, at the same place, inside
    /// one new folder, and no other folder matches better. What differs inside is then synced as usual.</item>
    /// </list>
    /// As with <see cref="PlanRemoteMoves"/>, the maps are rewritten as if the moves had happened, and folders are
    /// matched before files, so a file renamed inside a moved folder is recognized too.
    /// </remarks>
    private static void PlanLocalMoves(
        Dictionary<string, LocalItem> local,
        Dictionary<string, RemoteItem> remote,
        Dictionary<string, BaseItem> baseMap,
        List<SyncAction> actions)
    {
        for (var pass = 0; pass < 10; pass++)
        {
            var moves = FindLocalFolderMoves(local, remote, baseMap);
            var moved = ApplyLocalMoves(moves, local, remote, baseMap, actions);

            moves = FindLocalFileMoves(local, remote, baseMap);
            moved += ApplyLocalMoves(moves, local, remote, baseMap, actions);

            if (moved == 0)
            {
                return;
            }
        }
    }

    /// <summary>A synced item missing locally at its path, while the server still has it there unchanged.</summary>
    private static bool IsGoneLocally(BaseItem b, Dictionary<string, LocalItem> local, Dictionary<string, RemoteItem> remote) =>
        !local.ContainsKey(b.Path)
        && remote.TryGetValue(b.Path, out var r)
        && r.NodeUid == b.NodeUid
        && r.IsFolder == b.IsFolder
        && (b.IsFolder || r.RevisionUid == b.RevisionUid);

    /// <summary>A local item that is neither synced nor on the server.</summary>
    private static bool IsNewLocally(LocalItem l, Dictionary<string, RemoteItem> remote, Dictionary<string, BaseItem> baseMap) =>
        !baseMap.ContainsKey(l.Path) && !remote.ContainsKey(l.Path);

    private static List<(string From, string To)> FindLocalFileMoves(
        Dictionary<string, LocalItem> local,
        Dictionary<string, RemoteItem> remote,
        Dictionary<string, BaseItem> baseMap)
    {
        var gone = baseMap.Values
            .Where(b => !b.IsFolder && b.Size > 0 && IsGoneLocally(b, local, remote))
            .GroupBy(b => (b.Size, b.ModifiedTicks))
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Path);

        if (gone.Count == 0)
        {
            return [];
        }

        return local.Values
            .Where(l => !l.IsFolder && IsNewLocally(l, remote, baseMap) && gone.ContainsKey((l.Size, l.ModifiedTicks)))
            .GroupBy(l => (l.Size, l.ModifiedTicks))
            .Where(g => g.Count() == 1)
            .Select(g => (gone[g.Key], g.Single().Path))
            .OrderBy(m => m.Item1, StringComparer.Ordinal)
            .ToList();
    }

    private static List<(string From, string To)> FindLocalFolderMoves(
        Dictionary<string, LocalItem> local,
        Dictionary<string, RemoteItem> remote,
        Dictionary<string, BaseItem> baseMap)
    {
        var goneFolders = baseMap.Values
            .Where(b => b.IsFolder && IsGoneLocally(b, local, remote))
            .Select(b => b.Path)
            .ToHashSet(StringComparer.Ordinal);

        if (goneFolders.Count == 0)
        {
            return [];
        }

        var newFolders = local.Values
            .Where(l => l.IsFolder && IsNewLocally(l, remote, baseMap))
            .Select(l => l.Path)
            .ToHashSet(StringComparer.Ordinal);

        if (newFolders.Count == 0)
        {
            return [];
        }

        var newFilesByKey = local.Values
            .Where(l => !l.IsFolder && l.Size > 0 && IsNewLocally(l, remote, baseMap))
            .ToLookup(l => (l.Size, l.ModifiedTicks), l => l.Path);

        // For each missing folder: how many of the files it held are found, at the same place, in each new folder.
        var fileCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        foreach (var file in baseMap.Values.Where(b => !b.IsFolder && b.Size > 0))
        {
            for (var folder = SyncPaths.GetParent(file.Path); folder.Length > 0; folder = SyncPaths.GetParent(folder))
            {
                if (!goneFolders.Contains(folder))
                {
                    continue;
                }

                fileCounts[folder] = fileCounts.GetValueOrDefault(folder) + 1;
                var relative = file.Path[folder.Length..];

                foreach (var candidate in newFilesByKey[(file.Size, file.ModifiedTicks)])
                {
                    if (candidate.Length > relative.Length && candidate.EndsWith(relative, StringComparison.Ordinal)
                        && newFolders.Contains(candidate[..^relative.Length]))
                    {
                        var target = candidate[..^relative.Length];
                        if (!votes.TryGetValue(folder, out var forFolder))
                        {
                            votes[folder] = forFolder = new Dictionary<string, int>(StringComparer.Ordinal);
                        }

                        forFolder[target] = forFolder.GetValueOrDefault(target) + 1;
                    }
                }
            }
        }

        var best = new List<(string From, string To, int Score)>();
        foreach (var (folder, forFolder) in votes)
        {
            var ranked = forFolder.OrderByDescending(v => v.Value).ToList();
            var top = ranked[0];
            if ((ranked.Count == 1 || ranked[1].Value < top.Value) && top.Value * 2 >= fileCounts[folder] && !SyncPaths.IsUnder(top.Key, folder))
            {
                best.Add((folder, top.Key, top.Value));
            }
        }

        // A new folder chosen by several missing folders is ambiguous unless one matches it best.
        return best
            .GroupBy(m => m.To, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(m => m.Score).ToList())
            .Where(g => g.Count == 1 || g[1].Score < g[0].Score)
            .Select(g => (g[0].From, g[0].To))
            .OrderBy(m => SyncPaths.Depth(m.From))
            .ThenBy(m => m.From, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Plans the moves that still hold once the earlier ones are applied, and rewrites the maps.</summary>
    private static int ApplyLocalMoves(
        List<(string From, string To)> moves,
        Dictionary<string, LocalItem> local,
        Dictionary<string, RemoteItem> remote,
        Dictionary<string, BaseItem> baseMap,
        List<SyncAction> actions)
    {
        var applied = 0;
        foreach (var (from, to) in moves)
        {
            // An earlier move may have taken the item along (a folder inside a moved folder).
            if (!baseMap.TryGetValue(from, out var b) || !IsGoneLocally(b, local, remote)
                || !local.TryGetValue(to, out var l) || !IsNewLocally(l, remote, baseMap))
            {
                continue;
            }

            var parent = SyncPaths.GetParent(to);
            var parentExists = parent.Length == 0 || (remote.TryGetValue(parent, out var p) && p.IsFolder);

            actions.Add(new SyncAction(SyncActionKind.MoveRemote, from)
            {
                TargetPath = to,
                TargetParentIsNew = !parentExists,
                Local = l,
                Remote = remote[from],
                Base = b,
            });

            Rebase(remote, from, to, (item, path) => item with { Path = path });
            Rebase(baseMap, from, to, (item, path) => item with { Path = path });
            applied++;
        }

        return applied;
    }

    private static void Rebase<T>(Dictionary<string, T> map, string from, string to, Func<T, string, T> withPath)
    {
        foreach (var path in map.Keys.Where(p => SyncPaths.IsSameOrUnder(p, from)).ToList())
        {
            var item = map[path];
            map.Remove(path);
            var newPath = SyncPaths.Rebase(path, from, to);
            map[newPath] = withPath(item, newPath);
        }
    }

    private static bool IsMovable(BaseItem b, RemoteItem r, Dictionary<string, LocalItem> local, Dictionary<string, BaseItem> baseMap)
    {
        if (!local.TryGetValue(b.Path, out var l) || IsLocalChanged(l, b))
        {
            return false;
        }

        if (local.ContainsKey(r.Path) || baseMap.ContainsKey(r.Path))
        {
            return false;
        }

        // A folder cannot be moved into itself; the new parent must not be a local file.
        if (SyncPaths.IsUnder(r.Path, b.Path))
        {
            return false;
        }

        var parent = SyncPaths.GetParent(r.Path);
        return parent.Length == 0 || !local.TryGetValue(parent, out var localParent) || localParent.IsFolder;
    }

    private static bool IsLocalChanged(LocalItem? l, BaseItem? b)
    {
        if (b is null)
        {
            return l is not null;
        }

        return l is null
            || l.IsFolder != b.IsFolder
            || (!l.IsFolder && (l.Size != b.Size || l.ModifiedTicks != b.ModifiedTicks));
    }

    private static bool IsRemoteChanged(RemoteItem? r, BaseItem? b)
    {
        if (b is null)
        {
            return r is not null;
        }

        return r is null
            || r.IsFolder != b.IsFolder
            || r.NodeUid != b.NodeUid
            || (!r.IsFolder && r.RevisionUid != b.RevisionUid);
    }

    private static List<SyncAction> Decide(string path, LocalItem? l, RemoteItem? r, BaseItem? b)
    {
        var localChanged = IsLocalChanged(l, b);
        var remoteChanged = IsRemoteChanged(r, b);
        SyncAction A(SyncActionKind kind) => new(kind, path) { Local = l, Remote = r, Base = b };

        if (!localChanged && !remoteChanged)
        {
            return [];
        }

        if (localChanged && !remoteChanged)
        {
            if (l is null)
            {
                return [A(SyncActionKind.TrashRemote)];
            }

            if (r is null)
            {
                return [A(l.IsFolder ? SyncActionKind.CreateRemoteFolder : SyncActionKind.UploadNew)];
            }

            if (l.IsFolder != r.IsFolder)
            {
                // The new item replaces the trashed one, so it does not expect a remote item at the path.
                return [A(SyncActionKind.TrashRemoteEarly), A(l.IsFolder ? SyncActionKind.CreateRemoteFolder : SyncActionKind.UploadNew) with { Remote = null }];
            }

            return l.IsFolder ? [] : [A(SyncActionKind.UploadRevision)];
        }

        if (!localChanged && remoteChanged)
        {
            if (r is null)
            {
                return [A(SyncActionKind.TrashLocal)];
            }

            if (l is null)
            {
                return [A(r.IsFolder ? SyncActionKind.CreateLocalFolder : SyncActionKind.Download)];
            }

            if (l.IsFolder != r.IsFolder)
            {
                // The new item replaces the trashed one, so it does not expect a local item at the path.
                return [A(SyncActionKind.TrashLocalEarly), A(r.IsFolder ? SyncActionKind.CreateLocalFolder : SyncActionKind.Download) with { Local = null }];
            }

            // Folders: the remote folder was replaced by another one with the same name; record its new node.
            return l.IsFolder ? [A(SyncActionKind.RecordBase)] : [A(SyncActionKind.Download)];
        }

        // Both sides changed.
        if (l is null && r is null)
        {
            return [A(SyncActionKind.ForgetBase)];
        }

        if (l is null)
        {
            // Deleted locally, changed remotely: keep the remote change.
            return [A(r!.IsFolder ? SyncActionKind.CreateLocalFolder : SyncActionKind.Download)];
        }

        if (r is null)
        {
            // Deleted remotely, changed locally: keep the local change.
            return [A(l.IsFolder ? SyncActionKind.CreateRemoteFolder : SyncActionKind.UploadNew)];
        }

        if (l.IsFolder && r.IsFolder)
        {
            return [A(SyncActionKind.RecordBase)];
        }

        if (!l.IsFolder && !r.IsFolder)
        {
            return [A(SyncActionKind.ResolveFileConflict)];
        }

        return [A(SyncActionKind.RenameLocalForConflict)];
    }

    /// <summary>
    /// A folder deleted on one side is only deleted on the other side if nothing inside it changed there.
    /// Otherwise it is re-created, so the changed items have somewhere to go.
    /// </summary>
    private static void ResolveFolderDeletions(SortedDictionary<string, List<SyncAction>> decisions)
    {
        // Parents sort before their children, so an outer folder is settled before the folders inside it.
        foreach (var path in decisions.Keys.ToList())
        {
            if (!decisions.TryGetValue(path, out var pathActions))
            {
                continue; // Removed as part of an outer folder's deletion.
            }

            var trash = pathActions.FirstOrDefault(a => a.Kind is SyncActionKind.TrashLocal or SyncActionKind.TrashRemote);
            if (trash is null || !IsFolderAction(trash))
            {
                continue;
            }

            var descendants = decisions.Keys.Where(p => SyncPaths.IsUnder(p, path)).ToList();
            var keepsFolder = trash.Kind == SyncActionKind.TrashLocal
                ? descendants.Any(p => decisions[p].Any(a => NeedsLocalContent(a.Kind)))
                : descendants.Any(p => decisions[p].Any(a => NeedsRemoteContent(a.Kind)));

            if (keepsFolder)
            {
                var recreate = trash.Kind == SyncActionKind.TrashLocal ? SyncActionKind.CreateRemoteFolder : SyncActionKind.CreateLocalFolder;
                decisions[path] = [trash with { Kind = recreate }];
            }
            else
            {
                foreach (var descendant in descendants)
                {
                    decisions.Remove(descendant);
                }
            }
        }
    }

    private static bool IsFolderAction(SyncAction action) => action.Local?.IsFolder ?? action.Remote?.IsFolder ?? action.Base?.IsFolder ?? false;

    /// <summary>Actions that keep or send local content: a local folder holding them must not be trashed.</summary>
    private static bool NeedsLocalContent(SyncActionKind kind) => kind is SyncActionKind.UploadNew
        or SyncActionKind.UploadRevision
        or SyncActionKind.CreateRemoteFolder
        or SyncActionKind.ResolveFileConflict
        or SyncActionKind.RenameLocalForConflict
        or SyncActionKind.TrashRemoteEarly;

    /// <summary>Actions that keep or fetch remote content: a remote folder holding them must not be trashed.</summary>
    private static bool NeedsRemoteContent(SyncActionKind kind) => kind is SyncActionKind.Download
        or SyncActionKind.CreateLocalFolder
        or SyncActionKind.ResolveFileConflict
        or SyncActionKind.RenameLocalForConflict
        or SyncActionKind.TrashLocalEarly;
}
