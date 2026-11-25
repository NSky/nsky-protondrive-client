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
    private readonly ConcurrentDictionary<string, string> _shownHeld = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _heldLocally = new(StringComparer.Ordinal);
    private TransferProgress? _progress;
    private bool _heldShownThisPass;

    private const int MaxHeldShown = 20;

    public void PassStarted(SyncPair pair)
    {
        _heldShownThisPass = false;
    }

    public void DeletionsHeld(SyncPair pair, SafetyDecision decision)
    {
        var held = decision.HeldDeletions;
        _heldLocally[pair.Id] = !decision.HeldOnServer;

        // Watch mode plans again on every change; show the same pending deletions only once.
        var key = string.Join('\n', held.Select(a => a.Kind + " " + a.Path));
        if (quietWhenUpToDate && _shownHeld.TryGetValue(pair.Id, out var shown) && shown == key)
        {
            return;
        }

        _shownHeld[pair.Id] = key;
        _heldShownThisPass = true;

        if (decision.HeldOnServer)
        {
            Console.Error.WriteLine($"Warning: {Describe(pair)}: not deleting on Proton Drive yet. {decision.HeldReason}");
            Console.Error.WriteLine("Because that many deletions could be an accident, these wait for your confirmation (everything else keeps syncing):");
        }
        else
        {
            Console.Error.WriteLine($"Warning: {Describe(pair)}: not deleting local files. {decision.HeldReason}");
            Console.Error.WriteLine("This could be an accident, so the local copies are kept, and nothing is synced for this folder until you restore or confirm:");
        }

        foreach (var action in held.Take(MaxHeldShown))
        {
            var isFolder = action.Local?.IsFolder ?? action.Remote?.IsFolder ?? action.Base?.IsFolder ?? false;
            var path = decision.HeldOnServer ? RemotePathOf(pair, action.Path) : Path.Combine(pair.LocalPath, action.Path);
            Console.Error.WriteLine($"  {path}{(isFolder ? "/ (folder)" : string.Empty)}");
        }

        if (held.Count > MaxHeldShown)
        {
            Console.Error.WriteLine($"  ... and {held.Count - MaxHeldShown} more");
        }

        if (decision.HeldOnServer)
        {
            Console.Error.WriteLine($"To delete them on Proton Drive too (they go to its trash): {AppInfo.CommandName} sync --force {pair.LocalPath}");
            Console.Error.WriteLine($"To get them back locally instead, download them again, e.g. {AppInfo.CommandName} download <remote-path> <local-folder>");
        }
        else
        {
            Console.Error.WriteLine($"If they were deleted on Proton Drive on purpose, move the local copies to the local trash with: {AppInfo.CommandName} sync --force {pair.LocalPath}");
            Console.Error.WriteLine("If not, restore them from the Proton Drive trash (web app); syncing then resumes by itself.");
        }
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
            case SyncPassOutcome.UpToDate when result.HeldDeletions > 0 && (!quietWhenUpToDate || _heldShownThisPass):
                Console.Error.WriteLine(_heldLocally.TryGetValue(pair.Id, out var local) && local
                    ? $"{Describe(pair)}: paused, the remote folder is empty ({result.HeldDeletions} local item(s) kept)"
                    : $"{Describe(pair)}: up to date, except {result.HeldDeletions} deletion(s) waiting for confirmation");
                break;

            case SyncPassOutcome.UpToDate when !quietWhenUpToDate && result.HeldDeletions == 0:
                Console.Error.WriteLine($"{Describe(pair)}: up to date");
                break;

            case SyncPassOutcome.Completed:
                var held = result.HeldDeletions > 0 ? $"; {result.HeldDeletions} deletion(s) waiting for confirmation" : string.Empty;
                Console.Error.WriteLine(result.Failed == 0
                    ? $"{Describe(pair)}: synced ({result.Succeeded} change(s)){held}"
                    : $"{Describe(pair)}: {result.Succeeded} change(s) done, {result.Failed} failed or skipped (retried on the next run){held}");
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

    private static string RemotePathOf(SyncPair pair, string path) => pair.RemotePath.TrimEnd('/') + "/" + path;

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
