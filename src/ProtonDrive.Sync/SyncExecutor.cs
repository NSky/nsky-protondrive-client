using System.Collections.Concurrent;
using System.Security.Cryptography;
using NSkyLabs.ProtonDrive.Sync.State;
using Proton.Drive.Sdk;
using Proton.Drive.Sdk.Nodes;
using Proton.Drive.Sdk.Nodes.Upload;

namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>An action that was skipped because the item changed after the scan; the next pass picks it up.</summary>
internal sealed class ChangedDuringSyncException(string message) : Exception(message);

/// <summary>
/// Carries out a plan. Each action records its result in the base as soon as it succeeds, so an interrupted pass
/// loses nothing; a failed action leaves the base untouched and is planned again by the next pass.
/// </summary>
/// <remarks>
/// Safety rules: a local file is only replaced if it is unchanged since the scan, and deletions are moves to the
/// Proton trash or to the local trash folder (<c>.protondrive/trash</c> in the sync folder).
/// </remarks>
internal sealed class SyncExecutor
{
    private const int MaxParallelTransfers = 4;

    private const int MaxParallelFolderCreations = 8;
    private const int MaxTransferAttempts = 4;

    private readonly ProtonDriveClient _client;
    private readonly SyncPair _pair;
    private readonly SyncStateStore _state;
    private readonly ISyncObserver _observer;
    private readonly ConcurrentDictionary<string, string> _folderUids;
    private readonly string _trashFolder;
    private readonly string _tempFolder;
    private readonly string _host = Environment.MachineName;
    private readonly DateTimeOffset _startTime = DateTimeOffset.Now;

    private int _succeeded;
    private int _failed;

    public SyncExecutor(ProtonDriveClient client, SyncPair pair, SyncStateStore state, ISyncObserver observer, RemoteSnapshot remote)
    {
        _client = client;
        _pair = pair;
        _state = state;
        _observer = observer;
        _folderUids = new ConcurrentDictionary<string, string>(remote.FolderUidsByPath, StringComparer.Ordinal);

        var engineFolder = Path.Combine(pair.LocalPath, SyncPaths.EngineFolderName);
        _trashFolder = Path.Combine(engineFolder, "trash", _startTime.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        _tempFolder = Path.Combine(engineFolder, "tmp");
    }

    /// <summary>Set when local items were renamed to conflict names: another pass uploads them and fetches the remote versions.</summary>
    public bool NeedsAnotherPass { get; private set; }

    public async Task<(int Succeeded, int Failed)> ExecuteAsync(SyncPlan plan, CancellationToken cancellationToken)
    {
        foreach (var phase in plan.Actions.GroupBy(a => a.Phase).OrderBy(g => g.Key))
        {
            if (phase.Key == 2)
            {
                // Folders: one level at a time, so parents exist before their children; the folders of one level are
                // created several at once, since a remote creation is bound by round trips to the server.
                foreach (var level in phase.GroupBy(a => a.Path.Count(c => c == '/')).OrderBy(g => g.Key))
                {
                    await Parallel.ForEachAsync(
                        level,
                        new ParallelOptions { MaxDegreeOfParallelism = MaxParallelFolderCreations, CancellationToken = cancellationToken },
                        async (action, ct) => await RunAsync(action, ct).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }
            else if (phase.Key == 3)
            {
                await Parallel.ForEachAsync(
                    phase,
                    new ParallelOptions { MaxDegreeOfParallelism = MaxParallelTransfers, CancellationToken = cancellationToken },
                    async (action, ct) => await RunAsync(action, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }
            else
            {
                foreach (var action in phase)
                {
                    await RunAsync(action, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        TryDeleteEmptyFolder(_tempFolder);
        return (_succeeded, _failed);
    }

    private async Task RunAsync(SyncAction action, CancellationToken cancellationToken)
    {
        try
        {
            var note = await DispatchAsync(action, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _succeeded);
            _observer.ActionSucceeded(action, note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _failed);
            _observer.ActionFailed(action, ex);
        }
    }

    private Task<string?> DispatchAsync(SyncAction action, CancellationToken cancellationToken) => action.Kind switch
    {
        SyncActionKind.MoveLocal => Sync(() => MoveLocal(action)),
        SyncActionKind.RenameLocalForConflict => Sync(() => RenameForConflict(action.Path)),
        SyncActionKind.TrashLocal or SyncActionKind.TrashLocalEarly => Sync(() => TrashLocal(action)),
        SyncActionKind.TrashRemote or SyncActionKind.TrashRemoteEarly => TrashRemoteAsync(action, cancellationToken),
        SyncActionKind.CreateLocalFolder => Sync(() => CreateLocalFolder(action)),
        SyncActionKind.CreateRemoteFolder => CreateRemoteFolderAsync(action, cancellationToken),
        SyncActionKind.Download => DownloadAsync(action, cancellationToken),
        SyncActionKind.UploadNew or SyncActionKind.UploadRevision => UploadAsync(action, cancellationToken),
        SyncActionKind.ResolveFileConflict => ResolveFileConflictAsync(action, cancellationToken),
        SyncActionKind.RecordBase => Sync(() => RecordBase(action)),
        SyncActionKind.ForgetBase => Sync(() => ForgetBase(action)),
        _ => throw new InvalidOperationException($"Unknown action {action.Kind}"),
    };

    private static Task<string?> Sync(Func<string?> action) => Task.FromResult(action());

    private string FullPath(string path) => SyncPaths.ToLocalPath(_pair.LocalPath, path);

    private string? MoveLocal(SyncAction action)
    {
        var from = FullPath(action.Path);
        var to = FullPath(action.TargetPath!);

        if (File.Exists(to) || Directory.Exists(to))
        {
            throw new ChangedDuringSyncException($"{action.TargetPath} already exists locally");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        if (action.Local!.IsFolder)
        {
            Directory.Move(from, to);
        }
        else
        {
            EnsureUnchanged(action.Path, action.Local);
            File.Move(from, to, overwrite: false);
        }

        _state.MoveBaseSubtree(action.Path, action.TargetPath!);
        return null;
    }

    private string? RenameForConflict(string path)
    {
        var fullPath = FullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var name = SyncPaths.GetName(path);

        var conflictName = SyncPaths.GetConflictName(name, _host, _startTime);
        for (var i = 2; File.Exists(Path.Combine(directory, conflictName)) || Directory.Exists(Path.Combine(directory, conflictName)); i++)
        {
            conflictName = SyncPaths.GetConflictName(name, $"{_host} {i}", _startTime);
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Move(fullPath, Path.Combine(directory, conflictName));
        }
        else
        {
            File.Move(fullPath, Path.Combine(directory, conflictName), overwrite: false);
        }

        NeedsAnotherPass = true;
        return $"kept the local version as {conflictName}";
    }

    private string? TrashLocal(SyncAction action)
    {
        var fullPath = FullPath(action.Path);
        var destination = Path.Combine(_trashFolder, action.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        if (action.Local is { IsFolder: false } expected)
        {
            EnsureUnchanged(action.Path, expected);
            File.Move(fullPath, destination, overwrite: false);
        }
        else if (Directory.Exists(fullPath))
        {
            Directory.Move(fullPath, destination);
        }
        else if (File.Exists(fullPath))
        {
            throw new ChangedDuringSyncException($"{action.Path} changed locally during the sync");
        }

        _state.DeleteBaseSubtree(action.Path);
        return null;
    }

    private async Task<string?> TrashRemoteAsync(SyncAction action, CancellationToken cancellationToken)
    {
        var uid = action.Remote?.NodeUid ?? action.Base!.NodeUid;

        await foreach (var result in _client.TrashNodesAsync([NodeUid.Parse(uid)], cancellationToken).ConfigureAwait(false))
        {
            if (result.Result.TryGetError(out var error))
            {
                throw error;
            }
        }

        _state.SetRemoteTrashed(uid);
        _state.DeleteBaseSubtree(action.Path);
        return null;
    }

    private string? CreateLocalFolder(SyncAction action)
    {
        var fullPath = FullPath(action.Path);
        if (File.Exists(fullPath))
        {
            throw new ChangedDuringSyncException($"{action.Path} exists locally as a file");
        }

        Directory.CreateDirectory(fullPath);
        _state.UpsertBase(new BaseItem(action.Path, IsFolder: true, action.Remote!.NodeUid, RevisionUid: null, Size: 0, ModifiedTicks: 0));
        return null;
    }

    private async Task<string?> CreateRemoteFolderAsync(SyncAction action, CancellationToken cancellationToken)
    {
        var parentUid = GetParentFolderUid(action.Path);
        var fullPath = FullPath(action.Path);
        var modified = Directory.Exists(fullPath) ? Directory.GetLastWriteTimeUtc(fullPath) : (DateTime?)null;

        var folder = await _client.CreateFolderAsync(
                NodeUid.Parse(parentUid),
                SyncPaths.GetName(action.Path),
                modified is { } time ? new DateTimeOffset(time) : null,
                cancellationToken)
            .ConfigureAwait(false);

        var uid = folder.Uid.ToString();
        _folderUids[action.Path] = uid;
        _state.UpsertRemote(RemoteTree.ToRow(folder));
        _state.UpsertBase(new BaseItem(action.Path, IsFolder: true, uid, RevisionUid: null, Size: 0, ModifiedTicks: 0));
        return null;
    }

    private async Task<string?> DownloadAsync(SyncAction action, CancellationToken cancellationToken)
    {
        var remote = action.Remote!;
        var target = FullPath(action.Path);
        Directory.CreateDirectory(_tempFolder);
        var temp = Path.Combine(_tempFolder, Guid.NewGuid().ToString("N") + SyncPaths.PartialSuffix);

        try
        {
            var report = _observer.TransferStarted(action);

            using (var downloader = await _client.GetFileDownloaderAsync(RevisionUid.Parse(remote.RevisionUid!), cancellationToken).ConfigureAwait(false))
            {
                var controller = downloader.DownloadToFile(temp, (done, _) => report(done), cancellationToken);
                await using (controller.ConfigureAwait(false))
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await controller.Completion.ConfigureAwait(false);
                            break;
                        }
                        catch (Exception) when (controller.IsPaused && attempt < MaxTransferAttempts && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                            controller.Resume();
                        }
                    }
                }
            }

            // Replace the local file only if it is still what the plan was based on.
            if (action.Local is { } expected)
            {
                EnsureUnchanged(action.Path, expected);
            }
            else if (File.Exists(target) || Directory.Exists(target))
            {
                throw new ChangedDuringSyncException($"{action.Path} appeared locally during the sync");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: action.Local is not null);

            if (remote.Modified is { } modified)
            {
                File.SetLastWriteTimeUtc(target, modified.UtcDateTime);
            }

            var written = LocalScanner.Stat(_pair.LocalPath, action.Path)!;
            _state.UpsertBase(new BaseItem(action.Path, IsFolder: false, remote.NodeUid, remote.RevisionUid, written.Size, written.ModifiedTicks));
            return null;
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private async Task<string?> UploadAsync(SyncAction action, CancellationToken cancellationToken)
    {
        var fullPath = FullPath(action.Path);
        var name = SyncPaths.GetName(action.Path);
        var parentUid = GetParentFolderUid(action.Path);

        // Record what was read before the upload: if the file changes during the upload, the next pass sees a
        // different size or time and uploads it again.
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new ChangedDuringSyncException($"{action.Path} no longer exists locally");
        }

        var size = file.Length;
        var modified = file.LastWriteTimeUtc;
        var mediaType = MediaTypes.FromFileName(name);
        var metadata = new FileUploadMetadata { LastModificationTime = new DateTimeOffset(modified) };

        var report = _observer.TransferStarted(action);

        using var uploader = action.Kind == SyncActionKind.UploadRevision
            ? await _client.GetFileRevisionUploaderAsync(RevisionUid.Parse(action.Remote!.RevisionUid!), size, metadata, cancellationToken).ConfigureAwait(false)
            : await _client.GetFileUploaderAsync(NodeUid.Parse(parentUid), name, mediaType, size, metadata, overrideExistingDraftByOtherClient: false, cancellationToken)
                .ConfigureAwait(false);

        var controller = uploader.UploadFromFile(fullPath, thumbnails: [], (done, _) => report(done), expectedSha1Provider: null, cancellationToken);

        UploadResult result;
        await using (controller.ConfigureAwait(false))
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    result = await controller.Completion.ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (controller.IsPaused && attempt < MaxTransferAttempts && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                    controller.Resume();
                }
            }
        }

        var nodeUid = result.NodeUid.ToString();
        var revisionUid = result.RevisionUid.ToString();

        // Record the new node in the remote tree right away, so the next pass does not depend on the event arriving first.
        _state.UpsertRemote(new RemoteNodeRow(
            nodeUid,
            parentUid,
            name,
            IsFolder: false,
            revisionUid,
            size,
            Sha1: null,
            new DateTimeOffset(modified).ToUnixTimeMilliseconds(),
            mediaType,
            IsTrashed: false));

        _state.UpsertBase(new BaseItem(action.Path, IsFolder: false, nodeUid, revisionUid, size, modified.Ticks));
        return null;
    }

    private async Task<string?> ResolveFileConflictAsync(SyncAction action, CancellationToken cancellationToken)
    {
        var local = action.Local!;
        var remote = action.Remote!;

        var localSha1 = local.Size == remote.Size && remote.Sha1 is not null
            ? await ComputeSha1Async(FullPath(action.Path), cancellationToken).ConfigureAwait(false)
            : null;

        switch (CompareConflictingFiles(local, remote, localSha1))
        {
            case FileMatch.SameContent:
                _state.UpsertBase(new BaseItem(action.Path, IsFolder: false, remote.NodeUid, remote.RevisionUid, local.Size, local.ModifiedTicks));
                return "same content on both sides";
            case FileMatch.SameSizeAndTime:
                _state.UpsertBase(new BaseItem(action.Path, IsFolder: false, remote.NodeUid, remote.RevisionUid, local.Size, local.ModifiedTicks));
                return "same size and modification time on both sides (the server has no checksum for it)";
            default:
                return RenameForConflict(action.Path);
        }
    }

    /// <summary>What a file found on both sides is likely to turn out as, judged without reading file contents.</summary>
    internal enum ConflictPreview
    {
        /// <summary>Certainly different (or a file on one side and a folder on the other): both versions are kept.</summary>
        Conflict,

        /// <summary>Same size, and the server has a checksum: the checksum decides when syncing.</summary>
        Compare,

        /// <summary>The server has no checksum, but size and modification time match: recorded as in sync.</summary>
        Same,
    }

    /// <summary>
    /// Previews <see cref="ResolveFileConflictAsync"/> for a dry run, without reading the local file: only a
    /// checksum comparison could tell more.
    /// </summary>
    internal static ConflictPreview PreviewConflict(SyncAction action)
    {
        if (action.Kind != SyncActionKind.ResolveFileConflict || action.Local is not { } local || action.Remote is not { } remote)
        {
            return ConflictPreview.Conflict;
        }

        if (local.Size != remote.Size)
        {
            return ConflictPreview.Conflict;
        }

        if (remote.Sha1 is not null)
        {
            return ConflictPreview.Compare;
        }

        return CompareConflictingFiles(local, remote, localSha1: null) == FileMatch.SameSizeAndTime
            ? ConflictPreview.Same
            : ConflictPreview.Conflict;
    }

    /// <summary>How a file found on both sides compares, when deciding whether it really conflicts.</summary>
    internal enum FileMatch
    {
        Different,

        /// <summary>Same size and SHA-1.</summary>
        SameContent,

        /// <summary>The server has no SHA-1 for the file, but size and modification time match.</summary>
        SameSizeAndTime,
    }

    /// <summary>Largest modification time difference still treated as equal: other tools often keep only whole seconds.</summary>
    internal static readonly TimeSpan ModificationTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Decides whether a file present on both sides is the same file. The SHA-1 decides when the server has one
    /// (<paramref name="localSha1"/> is then the local file's hash, or null when sizes differ). Without one, a
    /// matching size and modification time is taken as the same file, so that adding a folder already copied by
    /// another tool does not produce conflict copies of every file.
    /// </summary>
    internal static FileMatch CompareConflictingFiles(LocalItem local, RemoteItem remote, string? localSha1)
    {
        if (local.Size != remote.Size)
        {
            return FileMatch.Different;
        }

        if (remote.Sha1 is not null)
        {
            return localSha1 is not null && string.Equals(localSha1, remote.Sha1, StringComparison.OrdinalIgnoreCase)
                ? FileMatch.SameContent
                : FileMatch.Different;
        }

        if (remote.Modified is not { } remoteModified)
        {
            return FileMatch.Different;
        }

        var localModified = new DateTimeOffset(local.ModifiedTicks, TimeSpan.Zero);
        return (localModified - remoteModified).Duration() <= ModificationTimeTolerance
            ? FileMatch.SameSizeAndTime
            : FileMatch.Different;
    }

    private string? RecordBase(SyncAction action)
    {
        var remote = action.Remote!;
        var local = action.Local!;
        _state.UpsertBase(new BaseItem(action.Path, remote.IsFolder, remote.NodeUid, remote.RevisionUid, local.Size, local.ModifiedTicks));
        return null;
    }

    private string? ForgetBase(SyncAction action)
    {
        _state.DeleteBase(action.Path);
        return null;
    }

    private string GetParentFolderUid(string path)
    {
        var parent = SyncPaths.GetParent(path);
        return _folderUids.TryGetValue(parent, out var uid)
            ? uid
            : throw new ChangedDuringSyncException($"the remote folder /{parent} does not exist (yet)");
    }

    private void EnsureUnchanged(string path, LocalItem expected)
    {
        var current = LocalScanner.Stat(_pair.LocalPath, path);
        if (current is null || current.IsFolder != expected.IsFolder || current.Size != expected.Size || current.ModifiedTicks != expected.ModifiedTicks)
        {
            throw new ChangedDuringSyncException($"{path} changed locally during the sync");
        }
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static void TryDeleteEmptyFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
