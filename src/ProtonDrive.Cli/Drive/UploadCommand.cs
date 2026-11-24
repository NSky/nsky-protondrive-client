using NSkyLabs.ProtonDrive.Core.Auth;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Sdk;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

internal sealed class UploadTargetException(string message) : Exception(message);

/// <summary>
/// <c>upload &lt;local-path&gt; [remote-folder]</c>.
/// Uploads a local file, or a local folder with everything in it, into an existing remote folder.
/// Nothing on the server is ever overwritten: every name is checked against the server before the first byte is
/// sent, and the upload is refused if anything would collide. Existing remote folders with the same name as a
/// local folder are reused, so new files can be added to them.
/// </summary>
internal static class UploadCommand
{
    private const int MaxParallelFiles = 4;
    private const int MaxAttemptsPerFile = 4;
    private const int MaxConflictsShown = 20;

    public static async Task<int> RunAsync(
        AuthenticatedContext context,
        string localPathArgument,
        string? remoteFolderArgument,
        bool showProgress,
        CancellationToken cancellationToken)
    {
        var localPath = ValidateLocalPath(localPathArgument);
        var remotePath = RemotePath.Parse(remoteFolderArgument);

        var browser = DriveBrowser.Create(context);
        var target = await browser.ResolveAsync(remotePath, cancellationToken).ConfigureAwait(false) as FolderNode
            ?? throw new UploadTargetException($"{remotePath} is not a folder.");

        Console.Error.WriteLine($"Checking {remotePath} for existing items ...");

        var plan = await PlanAsync(browser, localPath, target, remotePath, cancellationToken).ConfigureAwait(false);

        foreach (var skipped in plan.Skipped)
        {
            Console.Error.WriteLine($"Skipping {skipped}");
        }

        if (plan.Conflicts.Count > 0)
        {
            Console.Error.WriteLine($"{plan.Conflicts.Count} item(s) already exist on Proton Drive:");
            foreach (var conflict in plan.Conflicts.Take(MaxConflictsShown))
            {
                Console.Error.WriteLine($"  {conflict}");
            }

            if (plan.Conflicts.Count > MaxConflictsShown)
            {
                Console.Error.WriteLine($"  ... and {plan.Conflicts.Count - MaxConflictsShown} more");
            }

            Console.Error.WriteLine("Nothing was uploaded.");
            return 1;
        }

        await CreateFoldersAsync(browser.Client, plan, cancellationToken).ConfigureAwait(false);

        return await UploadAllAsync(browser.Client, plan, remotePath, showProgress, cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateLocalPath(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new UploadTargetException("The local path must not be empty.");
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(argument));

        if (Directory.Exists(fullPath))
        {
            if (Path.GetPathRoot(fullPath) == fullPath)
            {
                throw new UploadTargetException($"Refusing to upload the file system root ({fullPath}).");
            }

            return fullPath;
        }

        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        throw new UploadTargetException($"{fullPath} does not exist.");
    }

    /// <summary>
    /// Walks the local tree and compares it with the server. Remote folders are only listed where the local tree
    /// meets an existing remote folder; below a folder that will be created everything is new.
    /// </summary>
    private static async Task<UploadPlan> PlanAsync(
        DriveBrowser browser,
        string localPath,
        FolderNode targetFolder,
        RemotePath targetPath,
        CancellationToken cancellationToken)
    {
        var plan = new UploadPlan();
        var targetDisplayPath = targetPath.IsRoot ? string.Empty : targetPath.ToString();
        var root = new RemoteFolder(targetDisplayPath, targetFolder.Uid, Existing: targetFolder);

        // Pending local folders, with the remote folder their contents go into.
        var pending = new Queue<(IReadOnlyList<FileSystemInfo> Entries, RemoteFolder Parent)>();
        pending.Enqueue(([File.Exists(localPath) ? new FileInfo(localPath) : new DirectoryInfo(localPath)], root));

        while (pending.TryDequeue(out var current))
        {
            var remoteChildren = current.Parent.Existing is { } existingFolder
                ? await ListByNameAsync(browser, existingFolder, cancellationToken).ConfigureAwait(false)
                : null;

            foreach (var entry in current.Entries)
            {
                var name = entry.Name;
                var remoteDisplayPath = $"{current.Parent.DisplayPath}/{name}";

                if (entry.LinkTarget is not null)
                {
                    plan.Skipped.Add($"{entry.FullName} (symbolic link)");
                    continue;
                }

                Node? existing = null;
                remoteChildren?.TryGetValue(name, out existing);

                switch (entry)
                {
                    case FileInfo file:
                        if (existing is not null)
                        {
                            plan.Conflicts.Add(DescribeConflict(remoteDisplayPath, existing));
                            continue;
                        }

                        plan.Files.Add(new FileToUpload(file, name, current.Parent, remoteDisplayPath));
                        plan.TotalBytes += file.Length;
                        break;

                    case DirectoryInfo directory:
                        RemoteFolder folder;
                        if (existing is null)
                        {
                            folder = new RemoteFolder(remoteDisplayPath, Uid: null, Existing: null)
                            {
                                Parent = current.Parent,
                                Name = name,
                                LastModificationTime = directory.LastWriteTimeUtc,
                            };
                            plan.FoldersToCreate.Add(folder);
                        }
                        else if (existing is FolderNode existingChild && existingChild.TrashTime is null)
                        {
                            // Merge into the existing folder: only its files are checked for collisions.
                            folder = new RemoteFolder(remoteDisplayPath, existingChild.Uid, existingChild);
                        }
                        else
                        {
                            plan.Conflicts.Add(DescribeConflict(remoteDisplayPath, existing));
                            continue;
                        }

                        IReadOnlyList<FileSystemInfo> children;
                        try
                        {
                            children = directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
                        }
                        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                        {
                            throw new UploadTargetException($"Cannot read {directory.FullName}: {ex.Message}");
                        }

                        pending.Enqueue((children, folder));
                        break;

                    default:
                        plan.Skipped.Add($"{entry.FullName} (not a regular file or folder)");
                        break;
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Lists a remote folder's children by exact name, trashed items included: a trashed item can still block a
    /// name, and reporting it is safer than letting the upload fail halfway.
    /// </summary>
    private static async Task<Dictionary<string, Node>> ListByNameAsync(DriveBrowser browser, FolderNode folder, CancellationToken cancellationToken)
    {
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);

        await foreach (var child in browser.ListChildrenAsync(folder, cancellationToken, includeTrashed: true).ConfigureAwait(false))
        {
            if (child.Name.TryGetValue(out var name))
            {
                // Prefer a live item over a trashed one with the same name.
                if (!byName.TryGetValue(name, out var previous) || previous.TrashTime is not null)
                {
                    byName[name] = child;
                }
            }
        }

        return byName;
    }

    private static string DescribeConflict(string remoteDisplayPath, Node existing)
    {
        var kind = existing is FolderNode ? "folder" : "file";
        return existing.TrashTime is null ? $"{remoteDisplayPath} ({kind})" : $"{remoteDisplayPath} ({kind} in the trash)";
    }

    private static async Task CreateFoldersAsync(ProtonDriveClient client, UploadPlan plan, CancellationToken cancellationToken)
    {
        // Parents come before their children, so each parent's UID is known when the child is created.
        foreach (var folder in plan.FoldersToCreate)
        {
            var parentUid = folder.Parent!.Uid ?? throw new InvalidOperationException($"Parent of {folder.DisplayPath} was not created.");

            try
            {
                var created = await client.CreateFolderAsync(parentUid, folder.Name!, folder.LastModificationTime, cancellationToken)
                    .ConfigureAwait(false);
                folder.Uid = created.Uid;
            }
            catch (NodeWithSameNameExistsException)
            {
                throw new UploadTargetException(
                    $"{folder.DisplayPath} appeared on Proton Drive after the check. Stopped; nothing was overwritten.");
            }
        }

        if (plan.FoldersToCreate.Count > 0)
        {
            Console.Error.WriteLine($"Created {plan.FoldersToCreate.Count} folder(s).");
        }
    }

    private static async Task<int> UploadAllAsync(
        ProtonDriveClient client,
        UploadPlan plan,
        RemotePath target,
        bool showProgress,
        CancellationToken cancellationToken)
    {
        var failures = 0;

        await using (var progress = new TransferProgress("Uploaded", plan.Files.Count, plan.TotalBytes, showProgress))
        {
            await Parallel.ForEachAsync(
                plan.Files,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    var transfer = progress.StartFile(item.RemoteDisplayPath, item.File.Length);
                    try
                    {
                        await UploadFileAsync(client, item, transfer, progress, ct).ConfigureAwait(false);
                        progress.FinishFile(transfer, succeeded: true);
                    }
                    catch (NodeWithSameNameExistsException)
                    {
                        Interlocked.Increment(ref failures);
                        progress.FinishFile(transfer, succeeded: false);
                        progress.WriteLine($"Failed {item.RemoteDisplayPath}: it appeared on Proton Drive after the check; not overwritten");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref failures);
                        progress.FinishFile(transfer, succeeded: false);
                        progress.WriteLine($"Failed {item.RemoteDisplayPath}: {ex.Message}");
                    }
                }).ConfigureAwait(false);

            var succeeded = plan.Files.Count - failures;
            var summary = $"Uploaded {succeeded} of {plan.Files.Count} file(s), {TransferProgress.FormatBytes(progress.TransferredBytes)} " +
                $"in {TransferProgress.FormatDuration(progress.Elapsed)} to {target}";
            if (plan.Skipped.Count > 0)
            {
                summary += $"; skipped {plan.Skipped.Count}";
            }

            if (failures > 0)
            {
                summary += $"; {failures} failed";
            }

            progress.WriteLine(summary);
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task UploadFileAsync(
        ProtonDriveClient client,
        FileToUpload item,
        TransferProgress.FileTransfer transfer,
        TransferProgress progress,
        CancellationToken cancellationToken)
    {
        var parentUid = item.Parent.Uid ?? throw new InvalidOperationException($"Parent of {item.RemoteDisplayPath} was not created.");

        item.File.Refresh();
        var metadata = new FileUploadMetadata { LastModificationTime = item.File.LastWriteTimeUtc };

        using var uploader = await client.GetFileUploaderAsync(
                parentUid,
                item.Name,
                MediaTypes.FromFileName(item.Name),
                item.File.Length,
                metadata,
                overrideExistingDraftByOtherClient: false,
                cancellationToken)
            .ConfigureAwait(false);

        var controller = uploader.UploadFromFile(
            item.File.FullName,
            thumbnails: [],
            (uploaded, _) => transfer.Report(uploaded),
            expectedSha1Provider: null,
            cancellationToken);

        await using (controller.ConfigureAwait(false))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await controller.Completion.ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (controller.IsPaused && attempt < MaxAttemptsPerFile && !cancellationToken.IsCancellationRequested)
                {
                    // The SDK pauses uploads that failed in a resumable way (e.g. a network error).
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    progress.WriteLine($"Retrying {item.RemoteDisplayPath} in {delay.TotalSeconds:0}s ({ex.Message})");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    controller.Resume();
                }
            }
        }
    }

    /// <summary>A remote folder that files and folders go into: either an existing one or one to be created.</summary>
    private sealed record RemoteFolder(string DisplayPath, NodeUid? Uid, FolderNode? Existing)
    {
        public NodeUid? Uid { get; set; } = Uid;

        public RemoteFolder? Parent { get; init; }

        public string? Name { get; init; }

        public DateTimeOffset? LastModificationTime { get; init; }
    }

    private sealed record FileToUpload(FileInfo File, string Name, RemoteFolder Parent, string RemoteDisplayPath);

    private sealed class UploadPlan
    {
        public List<RemoteFolder> FoldersToCreate { get; } = [];

        public List<FileToUpload> Files { get; } = [];

        public List<string> Conflicts { get; } = [];

        public List<string> Skipped { get; } = [];

        public long TotalBytes { get; set; }
    }
}
