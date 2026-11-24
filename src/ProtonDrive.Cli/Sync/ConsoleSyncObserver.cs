using System.Collections.Concurrent;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Sync;

namespace NSkyLabs.ProtonDrive.Cli.Sync;

/// <summary>Renders sync activity on stderr: a plan summary, live transfer progress and one line per change.</summary>
/// <param name="showProgress">Show the live transfer block on a terminal.</param>
/// <param name="quietWhenUpToDate">Watch mode: say nothing about passes that found nothing to do.</param>
internal sealed class ConsoleSyncObserver(bool showProgress, bool quietWhenUpToDate) : ISyncObserver
{
    private readonly ConcurrentDictionary<SyncAction, TransferProgress.FileTransfer> _transfers = new();
    private readonly ConcurrentDictionary<string, bool> _shownWarnings = new(StringComparer.Ordinal);
    private TransferProgress? _progress;

    public void PassStarted(SyncPair pair)
    {
    }

    public void Warning(SyncPair pair, string message)
    {
        // Watch mode runs many passes; repeat a warning only once per process.
        if (_shownWarnings.TryAdd($"{pair.Id}:{message}", true))
        {
            Write($"Warning: {message}");
        }
    }

    public void PlanReady(SyncPair pair, SyncPlan plan, bool dryRun)
    {
        Write($"{Describe(pair)}: {Summarize(plan)}");

        if (dryRun)
        {
            foreach (var action in plan.Actions)
            {
                Console.Error.WriteLine($"  {DescribePlanned(action)}");
            }

            return;
        }

        var transfers = plan.Actions.Where(a => a.IsTransfer).ToList();
        _progress = new TransferProgress("Synced", transfers.Count, transfers.Sum(a => a.TransferSize), showProgress);
    }

    public Action<long> TransferStarted(SyncAction action)
    {
        if (_progress is null)
        {
            return _ => { };
        }

        var transfer = _progress.StartFile(
            (action.IsDownload ? "↓ " : "↑ ") + action.Path,
            action.TransferSize,
            action.IsDownload ? "Downloaded" : "Uploaded");

        _transfers[action] = transfer;
        return transfer.Report;
    }

    public void ActionSucceeded(SyncAction action, string? note = null)
    {
        if (_transfers.TryRemove(action, out var transfer))
        {
            _progress!.FinishFile(transfer, succeeded: true);
            return;
        }

        if (DescribeDone(action, note) is { } line)
        {
            Write(line);
        }
    }

    public void ActionFailed(SyncAction action, Exception error)
    {
        if (_transfers.TryRemove(action, out var transfer))
        {
            _progress!.FinishFile(transfer, succeeded: false);
        }

        var message = error is ChangedDuringSyncException
            ? $"Skipped {action.Path}: {error.Message}; it will be synced on the next run"
            : $"Failed: {DescribePlanned(action)}: {error.Message}";

        Write(message);
    }

    public void PassFinished(SyncPair pair, SyncPassResult result)
    {
        if (_progress is not null)
        {
            _progress.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _progress = null;
        }

        switch (result.Outcome)
        {
            case SyncPassOutcome.UpToDate when !quietWhenUpToDate:
                Console.Error.WriteLine($"{Describe(pair)}: up to date");
                break;

            case SyncPassOutcome.Completed:
                Console.Error.WriteLine(result.Failed == 0
                    ? $"{Describe(pair)}: synced ({result.Succeeded} change(s))"
                    : $"{Describe(pair)}: {result.Succeeded} change(s) done, {result.Failed} failed or skipped (retried on the next run)");
                break;

            case SyncPassOutcome.DryRun:
                Console.Error.WriteLine("Dry run: nothing was changed.");
                break;

            case SyncPassOutcome.Busy:
                Console.Error.WriteLine($"{Describe(pair)}: skipped, {result.Message}");
                break;

            case SyncPassOutcome.Refused:
                Console.Error.WriteLine($"{Describe(pair)}: refused. {result.Message}");
                Console.Error.WriteLine($"Nothing was changed. If this is intended, run '{AppInfo.CommandName} sync --force {pair.LocalPath}'.");
                break;
        }
    }

    public static string Describe(SyncPair pair) => $"{pair.LocalPath} ⇄ {pair.RemotePath}";

    private void Write(string line)
    {
        if (_progress is not null)
        {
            _progress.WriteLine(line);
        }
        else
        {
            Console.Error.WriteLine(line);
        }
    }

    private static string Summarize(SyncPlan plan)
    {
        var parts = new List<string>();

        void Add(int count, string text)
        {
            if (count > 0)
            {
                parts.Add($"{count} {text}");
            }
        }

        Add(plan.Count(SyncActionKind.Download), "to download");
        Add(plan.Count(SyncActionKind.UploadNew) + plan.Count(SyncActionKind.UploadRevision), "to upload");
        Add(plan.Count(SyncActionKind.CreateLocalFolder) + plan.Count(SyncActionKind.CreateRemoteFolder), "folder(s) to create");
        Add(plan.Count(SyncActionKind.MoveLocal), "to move locally");
        Add(plan.Count(SyncActionKind.TrashLocal) + plan.Count(SyncActionKind.TrashLocalEarly), "to move to the local trash");
        Add(plan.Count(SyncActionKind.TrashRemote) + plan.Count(SyncActionKind.TrashRemoteEarly), "to move to the Proton Drive trash");
        Add(plan.Count(SyncActionKind.ResolveFileConflict) + plan.Count(SyncActionKind.RenameLocalForConflict), "changed on both sides");
        Add(plan.Count(SyncActionKind.RecordBase) + plan.Count(SyncActionKind.ForgetBase), "to record");

        return string.Join(", ", parts);
    }

    private static string DescribePlanned(SyncAction action) => action.Kind switch
    {
        SyncActionKind.MoveLocal => $"move {action.Path} -> {action.TargetPath} (moved on the server)",
        SyncActionKind.RenameLocalForConflict => $"keep both versions of {action.Path} (file and folder)",
        SyncActionKind.TrashRemoteEarly or SyncActionKind.TrashRemote => $"trash /{action.Path} on Proton Drive",
        SyncActionKind.TrashLocalEarly or SyncActionKind.TrashLocal => $"move {action.Path} to the local trash",
        SyncActionKind.CreateLocalFolder => $"create folder {action.Path} locally",
        SyncActionKind.CreateRemoteFolder => $"create folder /{action.Path} on Proton Drive",
        SyncActionKind.Download => $"download {action.Path}",
        SyncActionKind.UploadNew => $"upload {action.Path}",
        SyncActionKind.UploadRevision => $"upload new version of {action.Path}",
        SyncActionKind.ResolveFileConflict => $"{action.Path} changed on both sides: keep both unless identical",
        SyncActionKind.RecordBase => $"record {action.Path} as in sync",
        SyncActionKind.ForgetBase => $"forget {action.Path} (gone on both sides)",
        _ => action.ToString(),
    };

    private static string? DescribeDone(SyncAction action, string? note) => action.Kind switch
    {
        SyncActionKind.MoveLocal => $"Moved {action.Path} -> {action.TargetPath}",
        SyncActionKind.RenameLocalForConflict or SyncActionKind.ResolveFileConflict => $"{action.Path} changed on both sides: {note}",
        SyncActionKind.TrashRemoteEarly or SyncActionKind.TrashRemote => $"Trashed /{action.Path} on Proton Drive",
        SyncActionKind.TrashLocalEarly or SyncActionKind.TrashLocal => $"Moved {action.Path} to the local trash",
        SyncActionKind.CreateLocalFolder => $"Created folder {action.Path}",
        SyncActionKind.CreateRemoteFolder => $"Created folder /{action.Path} on Proton Drive",
        _ => null,
    };
}
