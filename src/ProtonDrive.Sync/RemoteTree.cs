using NSkyLabs.ProtonDrive.Core.Diagnostics;
using NSkyLabs.ProtonDrive.Sync.State;
using Proton.Sdk;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Events;
using Proton.Drive.Sdk.Nodes;

namespace NSkyLabs.ProtonDrive.Sync;

internal sealed class SyncRootUnavailableException(string message) : Exception(message);

/// <summary>Result of <see cref="RemoteTree.RefreshAsync"/>.</summary>
/// <param name="FullScan">The whole tree was listed (first run, or the events feed lost continuity).</param>
/// <param name="ChangedNodes">Nodes added, changed or removed through events.</param>
internal readonly record struct RemoteRefreshResult(bool FullScan, int ChangedNodes)
{
    public bool HasChanges => FullScan || ChangedNodes > 0;
}

/// <summary>The remote snapshot used for planning, plus what could not be included.</summary>
/// <param name="UnknownNodeUids">Nodes whose name could not be decrypted. Their paths are unknown, so items synced
/// under them before must be left alone rather than treated as deleted.</param>
internal sealed record RemoteSnapshot(
    IReadOnlyDictionary<string, RemoteItem> Items,
    IReadOnlyDictionary<string, string> FolderUidsByPath,
    IReadOnlySet<string> UnknownNodeUids,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Keeps a copy of the remote folder tree in the state database. The first run lists every folder; after that only
/// the volume's events since the stored cursor are fetched, and only nodes inside the tree are looked up.
/// </summary>
internal sealed class RemoteTree(ProtonDriveClient client, SyncStateStore state, string rootUid)
{
    private const string CursorKey = "remote_cursor";

    private static readonly HashSet<string> ContentlessMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.proton.doc",
        "application/vnd.proton.sheet",
    };

    public async Task<RemoteRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        using var activity = Activity.Begin("Checking Proton Drive for changes", "events");
        var root = await GetRootAsync(cancellationToken).ConfigureAwait(false);
        var cursor = state.GetMeta(CursorKey);

        if (cursor is null)
        {
            await FullScanAsync(root, cancellationToken).ConfigureAwait(false);
            return new RemoteRefreshResult(FullScan: true, ChangedNodes: 0);
        }

        var known = state.LoadRemoteUids();
        var toFetch = new HashSet<string>(StringComparer.Ordinal);
        var toDelete = new HashSet<string>(StringComparer.Ordinal);
        string? lastEventId = null;

        await foreach (var driveEvent in client.EnumerateEventsAsync(root.TreeEventScopeId, (DriveEventId)cursor, cancellationToken).ConfigureAwait(false))
        {
            lastEventId = driveEvent.Id.ToString();
            activity.Increment();

            switch (driveEvent)
            {
                case EventsContinuityLostEvent:
                    await FullScanAsync(root, cancellationToken).ConfigureAwait(false);
                    return new RemoteRefreshResult(FullScan: true, ChangedNodes: 0);

                case EventsScopeAccessLostEvent:
                    throw new SyncRootUnavailableException("Access to the remote folder's volume was lost.");

                case NodeDeletedEvent deleted:
                {
                    var uid = deleted.NodeUid.ToString();
                    if (known.Contains(uid) || toFetch.Contains(uid))
                    {
                        toFetch.Remove(uid);
                        toDelete.Add(uid);
                    }

                    break;
                }

                case NodeUpdatedEvent updated:
                {
                    // The feed covers the whole volume; only nodes in (or moving into) the tree matter.
                    var uid = updated.NodeUid.ToString();
                    var parent = updated.ParentNodeUid?.ToString();
                    if (known.Contains(uid) || toFetch.Contains(uid) || (parent is not null && (known.Contains(parent) || toFetch.Contains(parent))))
                    {
                        toDelete.Remove(uid);
                        toFetch.Add(uid);
                    }

                    break;
                }
            }
        }

        if (toFetch.Count == 0 && toDelete.Count == 0)
        {
            if (lastEventId is not null && lastEventId != cursor)
            {
                state.SetMeta(CursorKey, lastEventId);
            }

            return new RemoteRefreshResult(FullScan: false, ChangedNodes: 0);
        }

        var upserts = new List<RemoteNodeRow>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        var newFolders = new List<FolderNode>();

        using var fetching = Activity.Begin("Fetching changed items from Proton Drive", "items");
        fetching.SetTotal(toFetch.Count);
        await foreach (var node in client.EnumerateNodesAsync(toFetch.Select(NodeUid.Parse).ToAsyncEnumerable(), cancellationToken).ConfigureAwait(false))
        {
            var uid = node.Uid.ToString();
            found.Add(uid);
            upserts.Add(ToRow(node));
            fetching.Increment();

            // A folder moved into the tree (or restored from the trash) brings children that produced no events.
            if (node is FolderNode folder && node.TrashTime is null && !known.Contains(uid))
            {
                newFolders.Add(folder);
            }
        }

        foreach (var folder in newFolders)
        {
            await ScanSubtreeAsync(folder, upserts, cancellationToken).ConfigureAwait(false);
        }

        // Nodes that could not be fetched no longer exist (or are no longer accessible).
        toDelete.UnionWith(toFetch.Where(uid => !found.Contains(uid)));

        using var saving = Activity.Begin("Saving the changes to the sync state");
        state.ApplyRemoteChanges(upserts, toDelete, lastEventId);
        PruneUnreachable();

        return new RemoteRefreshResult(FullScan: false, ChangedNodes: toFetch.Count + toDelete.Count);
    }

    /// <summary>Forgets the cursor so the next refresh lists the whole tree again.</summary>
    public void ForceFullScan() => state.SetMeta(CursorKey, null);

    public RemoteSnapshot GetSnapshot(IgnoreRules ignoreRules)
    {
        var rows = state.LoadRemote();
        var children = rows.Where(r => r.ParentUid is not null).ToLookup(r => r.ParentUid!, StringComparer.Ordinal);

        var items = new Dictionary<string, RemoteItem>(StringComparer.Ordinal);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = rootUid };
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();

        var pending = new Stack<(string Uid, string Path)>();
        pending.Push((rootUid, string.Empty));

        while (pending.TryPop(out var current))
        {
            foreach (var row in children[current.Uid].OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                if (row.IsTrashed)
                {
                    continue;
                }

                if (row.Name is null)
                {
                    unknown.Add(row.Uid);
                    warnings.Add($"Skipping an item in /{current.Path} whose name could not be decrypted");
                    continue;
                }

                var path = SyncPaths.Combine(current.Path, row.Name);

                if (!SyncPaths.IsPortableName(row.Name))
                {
                    warnings.Add($"Skipping remote /{path}: the name is not valid on this system");
                    continue;
                }

                if (ignoreRules.IsIgnored(row.Name) || (current.Path.Length == 0 && SyncPaths.IsEngineEntry(row.Name)))
                {
                    continue;
                }

                if (!row.IsFolder && row.MediaType is not null && ContentlessMediaTypes.Contains(row.MediaType))
                {
                    continue; // Proton Docs/Sheets have no file content to sync.
                }

                if (items.ContainsKey(path))
                {
                    warnings.Add($"Skipping remote /{path}: another item has the same name");
                    continue;
                }

                items[path] = new RemoteItem(
                    path,
                    row.IsFolder,
                    row.Uid,
                    row.RevisionUid,
                    row.Size,
                    row.Sha1,
                    row.ModifiedUnixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);

                if (row.IsFolder)
                {
                    folders[path] = row.Uid;
                    pending.Push((row.Uid, path));
                }
            }
        }

        return new RemoteSnapshot(items, folders, unknown, warnings);
    }

    /// <summary>Converts a node to its stored form.</summary>
    public static RemoteNodeRow ToRow(Node node)
    {
        var name = node.Name.TryGetValue(out var decrypted) ? decrypted : null;

        return node switch
        {
            FileNode file => new RemoteNodeRow(
                file.Uid.ToString(),
                file.ParentUid?.ToString(),
                name,
                IsFolder: false,
                file.ActiveRevision.Uid.ToString(),
                file.ActiveRevision.ClaimedSize ?? file.ActiveRevision.StorageSize,
                file.ActiveRevision.ClaimedDigests.Sha1 is { } sha1 ? Convert.ToHexStringLower(sha1.Span) : null,
                (file.ActiveRevision.ClaimedModificationTime ?? new DateTimeOffset(DateTime.SpecifyKind(file.ActiveRevision.CreationTime, DateTimeKind.Utc)))
                    .ToUnixTimeMilliseconds(),
                file.MediaType,
                file.TrashTime is not null),
            _ => new RemoteNodeRow(
                node.Uid.ToString(),
                node.ParentUid?.ToString(),
                name,
                IsFolder: node is FolderNode,
                RevisionUid: null,
                Size: 0,
                Sha1: null,
                ModifiedUnixMs: null,
                MediaType: null,
                node.TrashTime is not null),
        };
    }

    private async Task<FolderNode> GetRootAsync(CancellationToken cancellationToken)
    {
        var node = await client.GetNodeAsync(NodeUid.Parse(rootUid), cancellationToken).ConfigureAwait(false);
        return node switch
        {
            null => throw new SyncRootUnavailableException("The remote folder no longer exists."),
            { TrashTime: not null } => throw new SyncRootUnavailableException("The remote folder is in the trash."),
            FolderNode folder => folder,
            _ => throw new SyncRootUnavailableException("The remote sync root is not a folder."),
        };
    }

    private async Task FullScanAsync(FolderNode root, CancellationToken cancellationToken)
    {
        // Take the cursor first: anything that changes during the scan is replayed by the next refresh.
        using var activity = Activity.Begin("Reading the Proton Drive events cursor");
        string? cursor = null;
        await foreach (var driveEvent in client.EnumerateEventsAsync(root.TreeEventScopeId, cursorEventId: null, cancellationToken).ConfigureAwait(false))
        {
            cursor = driveEvent.Id.ToString();
        }

        if (cursor is null)
        {
            throw new InvalidOperationException("The server did not return an events cursor.");
        }

        var rows = new List<RemoteNodeRow> { ToRow(root) with { ParentUid = null } };
        await ScanSubtreeAsync(root, rows, cancellationToken).ConfigureAwait(false);

        using var saving = Activity.Begin($"Saving the remote listing ({rows.Count:N0} items) to the sync state");
        state.ReplaceRemote(rows, cursor);
    }

    private async Task ScanSubtreeAsync(FolderNode folder, List<RemoteNodeRow> rows, CancellationToken cancellationToken)
    {
        using var activity = Activity.Begin("Listing the remote folder on Proton Drive", "items");
        var pending = new Queue<(FolderNode Folder, string Path)>();
        pending.Enqueue((folder, string.Empty));

        while (pending.TryDequeue(out var current))
        {
            activity.SetDetail(current.Path);
            var childUids = client.EnumerateFolderChildrenNodeUidsAsync(current.Folder.Uid, cancellationToken);
            await foreach (var child in client.EnumerateNodesAsync(childUids, cancellationToken).ConfigureAwait(false))
            {
                if (child.TrashTime is not null)
                {
                    continue;
                }

                activity.Increment();
                rows.Add(ToRow(child));
                if (child is FolderNode childFolder)
                {
                    var name = child.Name.TryGetValue(out var decrypted) ? decrypted : "?";
                    pending.Enqueue((childFolder, SyncPaths.Combine(current.Path, name)));
                }
            }
        }
    }

    /// <summary>Removes rows that no longer connect to the root (moved out of the tree, or orphaned by a deletion).</summary>
    private void PruneUnreachable()
    {
        var rows = state.LoadRemote();
        var byUid = rows.ToDictionary(r => r.Uid, StringComparer.Ordinal);
        var reachable = new Dictionary<string, bool>(StringComparer.Ordinal) { [rootUid] = true };

        bool IsReachable(string uid)
        {
            var chain = new List<string>();
            var current = uid;
            bool result;

            while (true)
            {
                if (reachable.TryGetValue(current, out var known))
                {
                    result = known;
                    break;
                }

                if (!byUid.TryGetValue(current, out var row) || row.ParentUid is null || chain.Count > 1000)
                {
                    result = false;
                    break;
                }

                chain.Add(current);
                current = row.ParentUid;
            }

            foreach (var item in chain)
            {
                reachable[item] = result;
            }

            return result;
        }

        var unreachable = rows.Where(r => !IsReachable(r.Uid)).Select(r => r.Uid).ToList();
        if (unreachable.Count > 0)
        {
            state.ApplyRemoteChanges([], unreachable, cursor: null);
        }
    }
}
