using NSkyLabs.ProtonDrive.Core.Diagnostics;
using NSkyLabs.ProtonDrive.Core.Auth;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Sdk;

namespace NSkyLabs.ProtonDrive.Cli.Drive;

internal sealed class DownloadTargetException(string message) : Exception(message);

/// <summary>
/// <c>download &lt;remote-path&gt; &lt;local-folder&gt;</c>.
/// A file is saved into the local folder (created if missing, never overwriting).
/// A folder's contents are mirrored into the local folder, which must be empty or not exist yet.
/// </summary>
internal static class DownloadCommand
{
    private const int MaxParallelFiles = 4;
    private const int MaxAttemptsPerFile = 4;
    private const string PartialSuffix = ".protondrive-part";

    private static readonly HashSet<string> ContentlessMediaTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.proton.doc",
        "application/vnd.proton.sheet",
    };

    public static async Task<int> RunAsync(
        AuthenticatedContext context,
        string remotePathArgument,
        string localFolderArgument,
        bool showProgress,
        CancellationToken cancellationToken)
    {
        var remotePath = RemotePath.Parse(remotePathArgument);
        var localFolder = ValidateLocalFolder(localFolderArgument);

        var browser = DriveBrowser.Create(context);
        var node = await browser.ResolveAsync(remotePath, cancellationToken).ConfigureAwait(false);

        var plan = node switch
        {
            FileNode file => PlanSingleFile(file, remotePath, localFolder),
            FolderNode folder => await PlanFolderAsync(browser, folder, remotePath, localFolder, cancellationToken).ConfigureAwait(false),
            _ => throw new DownloadTargetException($"{remotePath}: this kind of item cannot be downloaded"),
        };

        foreach (var skipped in plan.Skipped)
        {
            Console.Error.WriteLine($"Skipping {skipped}");
        }

        return await DownloadAllAsync(browser.Client, plan, showProgress, cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateLocalFolder(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new DownloadTargetException("The local folder must not be empty.");
        }

        var fullPath = Path.GetFullPath(argument);
        if (Path.GetPathRoot(fullPath) == fullPath)
        {
            throw new DownloadTargetException($"Refusing to use the file system root ({fullPath}) as the download folder.");
        }

        if (File.Exists(fullPath))
        {
            throw new DownloadTargetException($"{fullPath} is a file, not a folder.");
        }

        return fullPath;
    }

    private static DownloadPlan PlanSingleFile(FileNode file, RemotePath remotePath, string localFolder)
    {
        var plan = new DownloadPlan(localFolder);

        if (ContentlessMediaTypes.Contains(file.MediaType))
        {
            throw new DownloadTargetException($"{remotePath} is a Proton Docs/Sheets document, which has no file content to download.");
        }

        if (!file.Name.TryGetValue(out var remoteName))
        {
            throw new DownloadTargetException($"{remotePath}: the file name could not be decrypted.");
        }

        var destination = Path.Combine(localFolder, LocalNames.ToLocalName(remoteName));
        LocalNames.EnsureInside(localFolder, destination);

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new DownloadTargetException($"{destination} already exists.");
        }

        Directory.CreateDirectory(localFolder);
        plan.Files.Add(new FileToDownload(file, remotePath.ToString(), destination));
        return plan;
    }

    private static async Task<DownloadPlan> PlanFolderAsync(
        DriveBrowser browser,
        FolderNode root,
        RemotePath remotePath,
        string localFolder,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(localFolder) && Directory.EnumerateFileSystemEntries(localFolder).Any())
        {
            throw new DownloadTargetException($"{localFolder} is not empty. Folders can only be downloaded into an empty or new folder.");
        }

        Console.Error.WriteLine($"Scanning {remotePath} ...");

        var plan = new DownloadPlan(localFolder);
        Directory.CreateDirectory(localFolder);

        // Level by level, with the details of each level fetched in large batches (see RemoteTreeWalker).
        using var activity = Activity.Begin($"Listing {remotePath} on Proton Drive", "items");
        var nameComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        await RemoteTreeWalker.WalkAsync(
            browser.Client,
            root,
            new LocalTarget(remotePath.IsRoot ? string.Empty : remotePath.ToString(), localFolder, new HashSet<string>(nameComparer)),
            (child, parent) =>
            {
                activity.Increment();
                if (!child.Name.TryGetValue(out var remoteName))
                {
                    plan.Skipped.Add($"{parent.RemoteDisplayPath}/{DriveBrowser.GetName(child)}");
                    return null;
                }

                var childRemotePath = $"{parent.RemoteDisplayPath}/{remoteName}";
                var localName = MakeUnique(LocalNames.ToLocalName(remoteName), parent.UsedLocalNames);
                var childLocalPath = Path.Combine(parent.LocalPath, localName);
                LocalNames.EnsureInside(localFolder, childLocalPath);

                switch (child)
                {
                    case FolderNode:
                        Directory.CreateDirectory(childLocalPath);
                        activity.SetDetail(childRemotePath);
                        return new LocalTarget(childRemotePath, childLocalPath, new HashSet<string>(nameComparer));

                    case FileNode file when ContentlessMediaTypes.Contains(file.MediaType):
                        plan.Skipped.Add($"{childRemotePath} (Proton Docs/Sheets document, no file content)");
                        return null;

                    case FileNode file:
                        plan.Files.Add(new FileToDownload(file, childRemotePath, childLocalPath));
                        return null;

                    default:
                        plan.Skipped.Add($"{childRemotePath} (unsupported item type)");
                        return null;
                }
            },
            cancellationToken).ConfigureAwait(false);

        // Items arrive in no particular order within a level; list and download in path order.
        plan.Files.Sort((a, b) => string.CompareOrdinal(a.RemotePath, b.RemotePath));
        plan.Skipped.Sort(StringComparer.Ordinal);
        return plan;
    }

    /// <summary>A remote folder being listed, the local folder its contents go into, and the local names used there.</summary>
    private sealed record LocalTarget(string RemoteDisplayPath, string LocalPath, HashSet<string> UsedLocalNames);

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task<int> DownloadAllAsync(ProtonDriveClient client, DownloadPlan plan, bool showProgress, CancellationToken cancellationToken)
    {
        var totalBytes = plan.Files.Sum(f => GetExpectedSize(f.Node));
        var failures = 0;

        await using (var progress = new TransferProgress("Downloaded", plan.Files.Count, totalBytes, showProgress))
        {
            await Parallel.ForEachAsync(
                plan.Files,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFiles, CancellationToken = cancellationToken },
                async (item, ct) =>
                {
                    var transfer = progress.StartFile(item.RemotePath, GetExpectedSize(item.Node));
                    try
                    {
                        await DownloadFileAsync(client, item, transfer, progress, ct).ConfigureAwait(false);
                        progress.FinishFile(transfer, succeeded: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref failures);
                        progress.FinishFile(transfer, succeeded: false);
                        progress.WriteLine($"Failed {item.RemotePath}: {ex.Message}");
                    }
                }).ConfigureAwait(false);

            var succeeded = plan.Files.Count - failures;
            var summary = $"Downloaded {succeeded} of {plan.Files.Count} file(s), {TransferProgress.FormatBytes(progress.TransferredBytes)} " +
                $"in {TransferProgress.FormatDuration(progress.Elapsed)} to {plan.LocalFolder}";
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

    private static async Task DownloadFileAsync(
        ProtonDriveClient client,
        FileToDownload item,
        TransferProgress.FileTransfer transfer,
        TransferProgress progress,
        CancellationToken cancellationToken)
    {
        var partialPath = item.LocalPath + PartialSuffix;

        try
        {
            using (var downloader = await client.GetFileDownloaderAsync(item.Node.ActiveRevision.Uid, cancellationToken).ConfigureAwait(false))
            {
                var controller = downloader.DownloadToFile(
                    partialPath,
                    (written, _) => transfer.Report(written),
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
                            // The SDK pauses downloads that failed in a resumable way (e.g. a network error).
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                            progress.WriteLine($"Retrying {item.RemotePath} in {delay.TotalSeconds:0}s ({ex.Message})");
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                            controller.Resume();
                        }
                    }
                }
            }

            File.Move(partialPath, item.LocalPath, overwrite: false);

            var revision = item.Node.ActiveRevision;
            var modified = revision.ClaimedModificationTime?.UtcDateTime ?? DateTime.SpecifyKind(revision.CreationTime, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(item.LocalPath, modified);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static long GetExpectedSize(FileNode file) => file.ActiveRevision.ClaimedSize ?? file.ActiveRevision.StorageSize;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed record FileToDownload(FileNode Node, string RemotePath, string LocalPath);

    private sealed class DownloadPlan(string localFolder)
    {
        public string LocalFolder { get; } = localFolder;

        public List<FileToDownload> Files { get; } = [];

        public List<string> Skipped { get; } = [];
    }
}
