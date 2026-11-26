using NSkyLabs.ProtonDrive.Core.Auth;
using NSkyLabs.ProtonDrive.Core.Diagnostics;
using NSkyLabs.ProtonDrive.Sync;
using Proton.Drive.Sdk.Nodes;
using Proton.Drive.Sdk.Nodes.Move;
using Proton.Sdk;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

/// <summary>
/// <c>move &lt;source&gt;... &lt;destination&gt;</c>: moves or renames files and folders on Proton Drive, like <c>mv</c>.
/// </summary>
/// <remarks>
/// <para>If the destination is an existing folder, the sources are moved into it and keep their names. Otherwise there
/// must be one source, and the destination is its new path: the parent folder must exist, and the last part is the
/// new name (a rename when the parent is the same).</para>
/// <para>Nothing is ever overwritten. Every path and every target name is checked first; if anything is wrong (a
/// missing source, a name already taken, a folder moved into itself), nothing is moved.</para>
/// </remarks>
internal static class MoveCommand
{
    public static string Usage => $"Usage: {AppInfo.CommandName} move <source>... <destination>";

    public static async Task<int> RunAsync(AuthenticatedContext context, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var sources = arguments.Take(arguments.Count - 1).Select(RemotePath.Parse).DistinctBy(p => p.ToString(), StringComparer.Ordinal).ToList();
        var destinationPath = RemotePath.Parse(arguments[^1]);

        if (sources.FirstOrDefault(p => p.IsRoot) is not null)
        {
            Console.Error.WriteLine("The root of My files cannot be moved.");
            return 2;
        }

        var browser = DriveBrowser.Create(context);

        // The destination: an existing folder to move into, or (one source) a new path.
        var destinationAncestors = new List<Node>();
        FolderNode targetFolder;
        string? newName = null;
        RemotePath targetFolderPath;

        var destination = await TryResolveAsync(browser, destinationPath, destinationAncestors, cancellationToken).ConfigureAwait(false);
        if (destination is FolderNode folder)
        {
            targetFolder = folder;
            targetFolderPath = destinationPath;
            destinationAncestors.Add(folder);
        }
        else if (destination is not null)
        {
            Console.Error.WriteLine($"{destinationPath} already exists and is not a folder. Nothing was moved; nothing is ever overwritten.");
            return 1;
        }
        else if (sources.Count > 1)
        {
            Console.Error.WriteLine($"{destinationPath} does not exist. To move several items, the destination must be an existing folder.");
            return 1;
        }
        else
        {
            targetFolderPath = Parent(destinationPath);
            destinationAncestors.Clear();
            var parent = await TryResolveAsync(browser, targetFolderPath, destinationAncestors, cancellationToken).ConfigureAwait(false);
            if (parent is not FolderNode parentFolder)
            {
                Console.Error.WriteLine(parent is null
                    ? $"{targetFolderPath} does not exist. Create the folder first."
                    : $"{targetFolderPath} is not a folder.");
                return 1;
            }

            targetFolder = parentFolder;
            newName = destinationPath.Segments[^1];
            destinationAncestors.Add(parentFolder);
        }

        // The sources.
        var items = new List<Item>();
        foreach (var source in sources)
        {
            var ancestors = new List<Node>();
            var node = await browser.ResolveAsync(source, cancellationToken, ancestors).ConfigureAwait(false);
            if (!node.Name.TryGetValue(out var name))
            {
                Console.Error.WriteLine($"{source}: the name cannot be decrypted, so it cannot be moved. Nothing was moved.");
                return 1;
            }

            items.Add(new Item(source, node, name, newName ?? name, ancestors.Select(a => a.Uid.ToString()).ToHashSet(StringComparer.Ordinal)));
        }

        // Check everything before moving anything.
        var problems = await FindProblemsAsync(browser, items, targetFolder, targetFolderPath, destinationAncestors, cancellationToken).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            Console.Error.WriteLine("Nothing was moved.");
            return 1;
        }

        var destinationAncestorUids = destinationAncestors.Select(a => a.Uid.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var note in GetSyncNotes(items, destinationAncestorUids))
        {
            Console.Error.WriteLine(note);
        }

        var failed = 0;
        var byUid = items.ToDictionary(i => i.Node.Uid.ToString(), StringComparer.Ordinal);
        using (var activity = Activity.Begin("Moving on Proton Drive", "items"))
        {
            activity.SetTotal(items.Count);
            var requests = items.Select(i => new NodeMoveItem(i.Node.Uid, i.Node.ParentUid!.Value, i.Name, i.TargetName));

            await foreach (var result in browser.Client.MoveNodesAsync(requests, targetFolder.Uid, cancellationToken).ConfigureAwait(false))
            {
                activity.Increment();
                var item = byUid[result.NodeUid.ToString()];
                var target = Combine(targetFolderPath, item.TargetName);

                if (result.Result.TryGetError(out var error))
                {
                    failed++;
                    Console.Error.WriteLine($"Could not move {item.Path} to {target}: {error.Message}");
                }
                else
                {
                    Console.WriteLine($"Moved {item.Path} -> {target}");
                }
            }
        }

        if (failed > 0)
        {
            Console.Error.WriteLine($"{failed} of {items.Count} could not be moved.");
            return 1;
        }

        return 0;
    }

    private static async Task<List<string>> FindProblemsAsync(
        DriveBrowser browser,
        List<Item> items,
        FolderNode targetFolder,
        RemotePath targetFolderPath,
        List<Node> destinationAncestors,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var destinationChain = destinationAncestors.Select(a => a.Uid.ToString()).ToHashSet(StringComparer.Ordinal);

        // Names in the target folder, trashed items included: a trashed item still holds its name.
        var taken = new Dictionary<string, Node>(StringComparer.Ordinal);
        await foreach (var child in browser.ListChildrenAsync(targetFolder, cancellationToken, includeTrashed: true).ConfigureAwait(false))
        {
            if (child.Name.TryGetValue(out var name))
            {
                taken[name] = child;
            }
        }

        foreach (var item in items)
        {
            var target = Combine(targetFolderPath, item.TargetName);
            var uid = item.Node.Uid.ToString();

            if (item.Node is FolderNode && destinationChain.Contains(uid))
            {
                problems.Add($"{item.Path} cannot be moved into itself ({target}).");
            }
            else if (taken.TryGetValue(item.TargetName, out var existing))
            {
                if (existing.Uid == item.Node.Uid)
                {
                    problems.Add($"{item.Path} is already {target}.");
                }
                else
                {
                    var kind = existing is FolderNode ? "a folder" : "a file";
                    problems.Add(existing.TrashTime is null
                        ? $"{target} already exists ({kind})."
                        : $"{target} is taken by {kind} in the trash: empty the trash or restore and rename it first.");
                }
            }
        }

        foreach (var duplicate in items.GroupBy(i => i.TargetName, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{string.Join(", ", duplicate.Select(i => i.Path))} would all become {Combine(targetFolderPath, duplicate.Key)}.");
        }

        return problems;
    }

    /// <summary>Explains what the move means for the user's synced folders.</summary>
    private static IEnumerable<string> GetSyncNotes(List<Item> items, HashSet<string> destinationChain)
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
            foreach (var item in items)
            {
                var isRoot = item.Node.Uid.ToString() == pair.RemoteRootUid;
                var fromInside = item.AncestorUids.Contains(pair.RemoteRootUid);
                var toInside = destinationChain.Contains(pair.RemoteRootUid);

                if (isRoot)
                {
                    yield return $"Note: {item.Path} is synced with {pair.LocalPath}; it stays synced after the move.";
                }
                else if (fromInside && toInside)
                {
                    yield return $"Note: {item.Path} is in the folder synced with {pair.LocalPath}; the next sync moves the local copy too.";
                }
                else if (fromInside)
                {
                    yield return $"Note: {item.Path} leaves the folder synced with {pair.LocalPath}; the next sync moves the local copy to "
                        + "that folder's local trash (.protondrive/trash).";
                }
                else if (toInside)
                {
                    yield return $"Note: {item.Path} moves into the folder synced with {pair.LocalPath}; the next sync downloads it there.";
                }
            }
        }
    }

    private static async Task<Node?> TryResolveAsync(DriveBrowser browser, RemotePath path, List<Node> ancestors, CancellationToken cancellationToken)
    {
        try
        {
            return await browser.ResolveAsync(path, cancellationToken, ancestors).ConfigureAwait(false);
        }
        catch (RemotePathNotFoundException)
        {
            return null;
        }
    }

    private static RemotePath Parent(RemotePath path) => RemotePath.Parse(string.Join('/', path.Segments.Take(path.Segments.Count - 1)));

    private static string Combine(RemotePath folder, string name) => folder.IsRoot ? "/" + name : $"{folder}/{name}";

    private sealed record Item(RemotePath Path, Node Node, string Name, string TargetName, HashSet<string> AncestorUids);
}
