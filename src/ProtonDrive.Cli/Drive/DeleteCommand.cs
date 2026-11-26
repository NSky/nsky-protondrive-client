using NSkyLabs.ProtonDrive.Core.Diagnostics;
using NSkyLabs.ProtonDrive.Core.Auth;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Sync;
using Proton.Drive.Sdk.Nodes;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

/// <summary>
/// <c>delete &lt;remote-path&gt;... [-r] [--permanent] [--yes]</c>: moves files and folders to the Proton Drive trash,
/// or deletes them permanently.
/// </summary>
/// <remarks>
/// Every path is resolved before anything is changed: if one is missing, or is a folder and --recursive was not
/// given, nothing is deleted. Unless --yes is given,
/// the items are listed and the user confirms on a terminal; without a terminal, --yes is required.
/// </remarks>
internal static class DeleteCommand
{
    public static string Usage => $"Usage: {AppInfo.CommandName} delete <remote-path>... [-r|--recursive] [--permanent] [-y|--yes]";

    public static async Task<int> RunAsync(
        AuthenticatedContext context,
        IReadOnlyList<string> pathArguments,
        bool recursive,
        bool permanent,
        bool assumeYes,
        CancellationToken cancellationToken)
    {
        var paths = pathArguments.Select(RemotePath.Parse).DistinctBy(p => p.ToString(), StringComparer.Ordinal).ToList();

        var root = paths.FirstOrDefault(p => p.IsRoot);
        if (root is not null)
        {
            Console.Error.WriteLine("Refusing to delete the root of My files.");
            return 2;
        }

        var nested = paths.FirstOrDefault(p => paths.Any(other => other != p && IsInside(p, other)));
        if (nested is not null)
        {
            Console.Error.WriteLine($"{nested} is inside another path given; list only the outer one.");
            return 2;
        }

        if (!assumeYes && (Console.IsInputRedirected || Console.IsErrorRedirected))
        {
            Console.Error.WriteLine("Not asking for confirmation without a terminal: add --yes to delete.");
            return 2;
        }

        var browser = DriveBrowser.Create(context);
        var targets = new List<Target>();

        foreach (var path in paths)
        {
            var ancestors = new List<Node>();
            var node = await browser.ResolveAsync(path, cancellationToken, ancestors).ConfigureAwait(false);
            targets.Add(new Target(path, node, ancestors.Select(a => a.Uid.ToString()).ToHashSet(StringComparer.Ordinal)));
        }

        var folders = targets.Where(t => t.Node is FolderNode).ToList();
        if (folders.Count > 0 && !recursive)
        {
            foreach (var folder in folders)
            {
                Console.Error.WriteLine($"{folder.Path} is a folder.");
            }

            Console.Error.WriteLine("Nothing deleted. To delete folders with everything in them, add --recursive (-r).");
            return 2;
        }

        var verb = permanent ? "Permanently delete" : "Move to the Proton Drive trash";
        Console.Error.WriteLine($"{verb}:");
        foreach (var target in targets)
        {
            Console.Error.WriteLine(target.Node is FolderNode ? $"  {target.Path}/ (folder, with everything in it)" : $"  {target.Path}");
        }

        foreach (var warning in GetSyncWarnings(targets))
        {
            Console.Error.WriteLine(warning);
        }

        if (permanent)
        {
            Console.Error.WriteLine("This cannot be undone.");
        }

        if (!assumeYes)
        {
            var answer = ConsolePrompt.ReadLine(permanent ? "Delete permanently? [y/N] " : "Move to trash? [y/N] ");
            if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) && !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Nothing deleted.");
                return 1;
            }
        }

        var uids = targets.Select(t => t.Node.Uid).ToList();
        var byUid = targets.ToDictionary(t => t.Node.Uid.ToString(), StringComparer.Ordinal);

        using var activity = Activity.Begin(permanent ? "Deleting on Proton Drive" : "Moving to the Proton Drive trash");
        var trashFailures = await RunAsync(browser.Client.TrashNodesAsync(uids, cancellationToken), byUid, "move to trash").ConfigureAwait(false);

        var failures = trashFailures;
        if (permanent)
        {
            var trashed = uids.Where(uid => !trashFailures.Contains(uid.ToString())).ToList();
            if (trashed.Count > 0)
            {
                var deleteFailures = await RunAsync(browser.Client.DeleteNodesAsync(trashed, cancellationToken), byUid, "delete").ConfigureAwait(false);
                failures = [.. trashFailures, .. deleteFailures];
            }
        }

        foreach (var target in targets.Where(t => !failures.Contains(t.Node.Uid.ToString())))
        {
            Console.WriteLine(permanent ? $"Deleted {target.Path}" : $"Moved {target.Path} to the trash");
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine($"{failures.Count} of {targets.Count} could not be deleted.");
            return 1;
        }

        return 0;
    }

    /// <summary>Runs a batch node operation and returns the UIDs that failed, reporting each failure.</summary>
    private static async Task<HashSet<string>> RunAsync(
        IAsyncEnumerable<NodeActionResult> results,
        Dictionary<string, Target> byUid,
        string what)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var result in results.ConfigureAwait(false))
        {
            if (result.Result.TryGetError(out var error))
            {
                var uid = result.NodeUid.ToString();
                failed.Add(uid);
                var name = byUid.TryGetValue(uid, out var target) ? target.Path.ToString() : uid;
                Console.Error.WriteLine($"Could not {what} {name}: {error.Message}");
            }
        }

        return failed;
    }

    /// <summary>Explains what deleting the targets means for the user's synced folders.</summary>
    private static IEnumerable<string> GetSyncWarnings(List<Target> targets)
    {
        IReadOnlyList<SyncPair> pairs;
        try
        {
            pairs = SyncPairStore.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TimeoutException)
        {
            yield break;
        }

        foreach (var pair in pairs)
        {
            foreach (var target in targets)
            {
                if (target.Node.Uid.ToString() == pair.RemoteRootUid)
                {
                    yield return $"Note: {target.Path} is synced with {pair.LocalPath}. Syncing that folder will stop until the "
                        + "remote folder is restored or the pair is removed; local files are not touched.";
                }
                else if (target.AncestorUids.Contains(pair.RemoteRootUid))
                {
                    yield return $"Note: {target.Path} is inside a folder synced with {pair.LocalPath}. The next sync moves "
                        + "the local copy to that folder's local trash (.protondrive/trash).";
                }
            }
        }
    }

    private static bool IsInside(RemotePath path, RemotePath folder) =>
        path.Segments.Count > folder.Segments.Count && folder.Segments.SequenceEqual(path.Segments.Take(folder.Segments.Count), StringComparer.Ordinal);

    private sealed record Target(RemotePath Path, Node Node, HashSet<string> AncestorUids);
}
