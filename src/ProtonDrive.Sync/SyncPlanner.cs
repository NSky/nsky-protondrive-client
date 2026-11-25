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

        PlanRemoteMoves(localMap, remote, baseMap, actions);

        var decisions = new SortedDictionary<string, List<SyncAction>>(StringComparer.Ordinal);
        var paths = new SortedSet<string>(localMap.Keys, StringComparer.Ordinal);
        paths.UnionWith(remote.Keys);
        paths.UnionWith(baseMap.Keys);

        foreach (var path in paths)
        {
            localMap.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
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

        // Stable sort: moves keep their shallow-first order; within a phase, paths sort parents first.
        var ordered = actions
            .Select((action, index) => (action, index))
            .OrderBy(x => x.action.Phase)
            .ThenBy(x => x.action.Phase == 0 ? string.Empty : x.action.Path, StringComparer.Ordinal)
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
