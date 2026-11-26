using NSkyLabs.ProtonDrive.Core.Auth;
using NSkyLabs.ProtonDrive.Core.Diagnostics;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Sdk;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

/// <summary>
/// <c>mkdir &lt;remote-path&gt;... [-p]</c>: creates folders on Proton Drive, like <c>mkdir</c>.
/// </summary>
/// <remarks>
/// Without <c>--parents</c>, the parent folder must exist and the path must not. With it, missing parent folders are
/// created too, and a folder that already exists is fine. A name taken by a file is never replaced. Items in the
/// trash do not hold their names: a folder can be created where a trashed item had the same name.
/// </remarks>
internal static class MkdirCommand
{
    public static string Usage => $"Usage: {AppInfo.CommandName} mkdir <remote-path>... [-p|--parents]";

    public static async Task<int> RunAsync(AuthenticatedContext context, IReadOnlyList<string> arguments, bool parents, CancellationToken cancellationToken)
    {
        var paths = arguments.Select(RemotePath.Parse).DistinctBy(p => p.ToString(), StringComparer.Ordinal).ToList();
        if (paths.Any(p => p.IsRoot))
        {
            Console.Error.WriteLine("The root of My files already exists.");
            return 1;
        }

        var browser = DriveBrowser.Create(context);
        var client = browser.Client;
        var root = await client.GetMyFilesFolderAsync(cancellationToken).ConfigureAwait(false);

        // Folders known to exist, by path, so paths with a common parent are looked up once.
        var folders = new Dictionary<string, NodeUid>(StringComparer.Ordinal) { ["/"] = root.Uid };
        var failed = 0;

        using var activity = Activity.Begin("Creating folders on Proton Drive", "folders");
        activity.SetTotal(paths.Count);

        foreach (var path in paths)
        {
            try
            {
                await CreateAsync(client, path, parents, folders, cancellationToken).ConfigureAwait(false);
            }
            catch (MkdirException ex)
            {
                failed++;
                Console.Error.WriteLine(ex.Message);
            }

            activity.Increment();
        }

        if (failed > 0)
        {
            Console.Error.WriteLine($"{failed} of {paths.Count} folder(s) could not be created.");
            return 1;
        }

        return 0;
    }

    private static async Task CreateAsync(
        ProtonDriveClient client,
        RemotePath path,
        bool parents,
        Dictionary<string, NodeUid> folders,
        CancellationToken cancellationToken)
    {
        var parentUid = folders["/"];
        var current = "";

        for (var i = 0; i < path.Segments.Count; i++)
        {
            var name = path.Segments[i];
            var isLast = i == path.Segments.Count - 1;
            current += "/" + name;

            if (folders.TryGetValue(current, out var known))
            {
                if (isLast && !parents)
                {
                    throw new MkdirException($"{current} already exists.");
                }

                parentUid = known;
                continue;
            }

            var existing = await FindChildAsync(client, parentUid, name, cancellationToken).ConfigureAwait(false);
            switch (existing)
            {
                case FolderNode folder:
                    if (isLast && !parents)
                    {
                        throw new MkdirException($"{current} already exists.");
                    }

                    folders[current] = folder.Uid;
                    parentUid = folder.Uid;
                    continue;

                case not null:
                    throw new MkdirException($"{current} already exists and is a file.");
            }

            if (!isLast && !parents)
            {
                throw new MkdirException($"{current} does not exist. Use --parents (-p) to create missing parent folders.");
            }

            try
            {
                var created = await client.CreateFolderAsync(parentUid, name, lastModificationTime: null, cancellationToken).ConfigureAwait(false);
                folders[current] = created.Uid;
                parentUid = created.Uid;
                Console.WriteLine($"Created {current}");
            }
            catch (NodeWithSameNameExistsException)
            {
                throw new MkdirException($"{current} appeared on Proton Drive meanwhile. Nothing was replaced.");
            }
        }
    }

    /// <summary>The child with this name that is not in the trash, or null.</summary>
    private static async Task<Node?> FindChildAsync(ProtonDriveClient client, NodeUid folderUid, string name, CancellationToken cancellationToken)
    {
        var childUids = client.EnumerateFolderChildrenNodeUidsAsync(folderUid, cancellationToken);

        await foreach (var child in client.EnumerateNodesAsync(childUids, cancellationToken).ConfigureAwait(false))
        {
            if (child.TrashTime is null && child.Name.TryGetValue(out var childName) && string.Equals(childName, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    private sealed class MkdirException(string message) : Exception(message);
}
