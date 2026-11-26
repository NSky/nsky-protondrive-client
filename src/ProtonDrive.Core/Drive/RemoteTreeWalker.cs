using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;

namespace NSkyLabs.ProtonDrive.Core.Drive;

/// <summary>
/// Lists a whole remote folder tree, one level at a time.
/// </summary>
/// <remarks>
/// Listing a folder takes two kinds of requests: its children's IDs, then the children's details (names, sizes,
/// keys). Folder by folder that is two round trips per folder, and the SDK fetches details one batch at a time, so a
/// large tree takes minutes. Here the children's IDs of every folder in a level are listed several folders at once,
/// and then the details of the whole level are fetched together, up to 50 per request. A level of 500 folders
/// then costs about 10 detail requests instead of 500.
/// </remarks>
internal static class RemoteTreeWalker
{
    /// <summary>How many folders' children are listed at once: fast, while staying polite to the API.</summary>
    public const int ListingParallelism = 8;

    /// <summary>
    /// Calls <paramref name="visit"/> for every item under <paramref name="root"/> (not for the root itself), with
    /// the context its parent folder was given. For a folder, <paramref name="visit"/> returns the context for its
    /// children, or <see langword="null"/> to skip what is inside it. Calls come one at a time, parents before their
    /// children; the order within a level is not defined. Trashed items are skipped unless
    /// <paramref name="includeTrashed"/> is set.
    /// </summary>
    public static async Task WalkAsync<TContext>(
        ProtonDriveClient client,
        FolderNode root,
        TContext rootContext,
        Func<Node, TContext, TContext?> visit,
        CancellationToken cancellationToken,
        bool includeTrashed = false)
        where TContext : class
    {
        var level = new List<(FolderNode Folder, TContext Context)> { (root, rootContext) };

        while (level.Count > 0)
        {
            // 1. The children's IDs of every folder in this level, several folders at once.
            var parentOfChild = new Dictionary<string, TContext>(StringComparer.Ordinal);
            var childUids = new List<NodeUid>();
            var mapLock = new Lock();

            await Parallel.ForEachAsync(
                level,
                new ParallelOptions { MaxDegreeOfParallelism = ListingParallelism, CancellationToken = cancellationToken },
                async (folder, ct) =>
                {
                    var uids = new List<NodeUid>();
                    await foreach (var uid in client.EnumerateFolderChildrenNodeUidsAsync(folder.Folder.Uid, ct).ConfigureAwait(false))
                    {
                        uids.Add(uid);
                    }

                    lock (mapLock)
                    {
                        foreach (var uid in uids)
                        {
                            if (parentOfChild.TryAdd(uid.ToString(), folder.Context))
                            {
                                childUids.Add(uid);
                            }
                        }
                    }
                }).ConfigureAwait(false);

            // 2. The details of the whole level, in large batches.
            var next = new List<(FolderNode, TContext)>();
            await foreach (var node in client.EnumerateNodesAsync(childUids.ToAsyncEnumerable(), cancellationToken).ConfigureAwait(false))
            {
                if ((!includeTrashed && node.TrashTime is not null) || !parentOfChild.TryGetValue(node.Uid.ToString(), out var parentContext))
                {
                    continue;
                }

                if (visit(node, parentContext) is { } childContext && node is FolderNode folder)
                {
                    next.Add((folder, childContext));
                }
            }

            level = next;
        }
    }
}
