using NSkyLabs.ProtonDrive.Core.Auth;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Sdk;

namespace NSkyLabs.ProtonDrive.Core.Drive;

internal sealed class RemotePathNotFoundException(RemotePath path, string reason)
    : Exception($"{path}: {reason}");

/// <summary>
/// Read-only navigation of the "My files" tree by path.
/// </summary>
internal sealed class DriveBrowser(ProtonDriveClient client)
{
    public ProtonDriveClient Client => client;

    public static DriveBrowser Create(AuthenticatedContext context)
    {
        var session = context.Session;

        // The Drive cache is kept separate from the session cache: sharing one repository with a different
        // value format version would wipe the account key passphrases.
        var client = new ProtonDriveClient(
            session.CreateHttpClientFactory(),
            context.AccountClient,
            cacheRepository: null,
            session.ClientConfiguration.FeatureFlagProvider,
            session.ClientConfiguration.Telemetry);

        return new DriveBrowser(client);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to a node, walking one folder level at a time. If <paramref name="ancestors"/>
    /// is given, the folders walked through (from the root of My files down to the parent) are added to it.
    /// </summary>
    public async Task<Node> ResolveAsync(RemotePath path, CancellationToken cancellationToken, List<Node>? ancestors = null)
    {
        Node current = await client.GetMyFilesFolderAsync(cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < path.Segments.Count; i++)
        {
            ancestors?.Add(current);

            if (current is not FolderNode)
            {
                throw new RemotePathNotFoundException(path, $"'{GetName(current)}' is not a folder");
            }

            var segment = path.Segments[i];
            Node? match = null;

            await foreach (var child in ListChildrenAsync((FolderNode)current, cancellationToken).ConfigureAwait(false))
            {
                if (child.Name.TryGetValue(out var name) && string.Equals(name, segment, StringComparison.Ordinal))
                {
                    match = child;
                    break;
                }
            }

            current = match ?? throw new RemotePathNotFoundException(path, $"'{segment}' not found");
        }

        return current;
    }

    /// <summary>Lists the direct children of a folder, skipping trashed items unless <paramref name="includeTrashed"/> is set.</summary>
    public async IAsyncEnumerable<Node> ListChildrenAsync(
        FolderNode folder,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        bool includeTrashed = false)
    {
        var childUids = client.EnumerateFolderChildrenNodeUidsAsync(folder.Uid, cancellationToken);

        await foreach (var child in client.EnumerateNodesAsync(childUids, cancellationToken).ConfigureAwait(false))
        {
            if (includeTrashed || child.TrashTime is null)
            {
                yield return child;
            }
        }
    }

    public static string GetName(Node node)
    {
        return node.Name.TryGetValueElseError(out var name, out var error)
            ? name
            : $"<undecryptable name: {error.Message}>";
    }
}
