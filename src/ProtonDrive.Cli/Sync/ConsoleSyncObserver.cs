using System.Collections.Concurrent;
using NSkyLabs.ProtonDrive.Cli.Terminal;
using NSkyLabs.ProtonDrive.Sync;

namespace NSkyLabs.ProtonDrive.Cli.Sync;

/// <summary>Renders sync activity on stderr: a plan summary, live transfer progress and one line per change.</summary>
/// <param name="showProgress">Show the live transfer block on a terminal.</param>
/// <param name="quietWhenUpToDate">Watch mode: say nothing about passes that found nothing to do.</param>
/// <param name="onPassFinished">Called after each pass (used to refresh the systemd status line).</param>
internal sealed class ConsoleSyncObserver(bool showProgress, bool quietWhenUpToDate, Action? onPassFinished = null) : ISyncObserver
{
    private readonly ConcurrentDictionary<SyncAction, TransferProgress.FileTransfer> _transfers = new();
    private readonly ConcurrentDictionary<string, bool> _shownWarnings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _shownHeld = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _heldLocally = new(StringComparer.Ordinal);
    private TransferProgress? _progress;
    private bool _heldShownThisPass;
    private int _conflictsThisPass;

    private const int MaxHeldShown = 20;

    public void PassStarted(SyncPair pair)
    {
        _heldShownThisPass = false;
        Interlocked.Exchange(ref _conflictsThisPass, 0);
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
            Console.Error.WriteLine(Ansi.Conflict($"Warning: {Describe(pair)}: not deleting on Proton Drive yet. {decision.HeldReason}"));
            Console.Error.WriteLine("Because that many deletions could be an accident, these wait for your confirmation (everything else keeps syncing):");
        }
        else
        {
            Console.Error.WriteLine(Ansi.Conflict($"Warning: {Describe(pair)}: not deleting local files. {decision.HeldReason}"));
            Console.Error.WriteLine("This could be an accident, so the local copies are kept, and nothing is synced for this folder until you restore or confirm:");
        }

        foreach (var action in held.Take(MaxHeldShown))
        {
            var isFolder = action.Local?.IsFolder ?? action.Remote?.IsFolder ?? action.Base?.IsFolder ?? false;
            var path = decision.HeldOnServer ? RemotePathOf(pair, action.Path) : Path.Combine(pair.LocalPath, action.Path);
            Console.Error.WriteLine(Ansi.Warning($"  {path}{(isFolder ? "/ (folder)" : string.Empty)}"));
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
            Write(Ansi.Warning($"Warning: {message}"));
        }
    }

    public void PlanReady(SyncPair pair, SyncPlan plan, bool dryRun)
    {
        Write($"{Describe(pair)}: {Summarize(plan)}");

        if (dryRun)
        {
            foreach (var action in plan.Actions)
            {
                Console.Error.WriteLine("  " + Colorize(action, DescribePlanned(action, preview: true)));
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

        if (IsConflict(action, note))
        {
            Interlocked.Increment(ref _conflictsThisPass);
        }

        if (DescribeDone(action, note) is { } line)
        {
            Write(IsConflict(action, note) ? Ansi.Conflict(line) : action.Kind == SyncActionKind.ResolveFileConflict ? Ansi.Dim(line) : Colorize(action, line));
        }
    }

    public void ActionFailed(SyncAction action, Exception error)
    {
        if (_transfers.TryRemove(action, out var transfer))
        {
            _progress!.FinishFile(transfer, succeeded: false);
        }

        var message = error is ChangedDuringSyncException
            ? Ansi.Warning($"Skipped {action.Path}: {error.Message}; it will be synced on the next run")
            : Ansi.Error($"Failed: {DescribePlanned(action)}: {error.Message}");

        Write(message);
    }

    public void PassFinished(SyncPair pair, SyncPassResult result)
    {
        onPassFinished?.Invoke();

        if (_progress is not null)
        {
            _progress.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _progress = null;
        }

        switch (result.Outcome)
        {
            case SyncPassOutcome.UpToDate when result.HeldDeletions > 0 && (!quietWhenUpToDate || _heldShownThisPass):
                Console.Error.WriteLine(Ansi.Conflict(_heldLocally.TryGetValue(pair.Id, out var local) && local
                    ? $"{Describe(pair)}: paused, the remote folder is empty ({result.HeldDeletions} local item(s) kept)"
                    : $"{Describe(pair)}: up to date, except {result.HeldDeletions} deletion(s) waiting for confirmation"));
                break;

            case SyncPassOutcome.UpToDate when !quietWhenUpToDate && result.HeldDeletions == 0:
                Console.Error.WriteLine(Ansi.Success($"{Describe(pair)}: up to date"));
                break;

            case SyncPassOutcome.Completed:
                var held = result.HeldDeletions > 0 ? Ansi.Conflict($"; {result.HeldDeletions} deletion(s) waiting for confirmation") : string.Empty;
                var conflicts = Volatile.Read(ref _conflictsThisPass);
                var conflictNote = conflicts > 0
                    ? Ansi.Conflict($"; {conflicts} conflict(s): both versions kept, the local one as \"name (conflict …)\"")
                    : string.Empty;
                Console.Error.WriteLine(result.Failed == 0
                    ? Ansi.Success($"{Describe(pair)}: synced ({result.Succeeded} change(s))") + conflictNote + held
                    : Ansi.Error($"{Describe(pair)}: {result.Succeeded} change(s) done, {result.Failed} failed or skipped (retried on the next run)") + conflictNote + held);
                break;

            case SyncPassOutcome.DryRun:
                Console.Error.WriteLine(Ansi.Dim("Dry run: nothing was changed."));
                break;

            case SyncPassOutcome.Busy:
                Console.Error.WriteLine($"{Describe(pair)}: skipped, {result.Message}");
                break;

            case SyncPassOutcome.Refused:
                Console.Error.WriteLine(Ansi.Error($"{Describe(pair)}: refused. {result.Message}"));
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

        void Add(int count, string text, Func<string, string>? color = null)
        {
            if (count > 0)
            {
                var part = $"{count} {text}";
                parts.Add(color is null ? part : color(part));
            }
        }

        Add(plan.Count(SyncActionKind.Download), "to download", Ansi.ToLocal);
        Add(plan.Count(SyncActionKind.UploadNew) + plan.Count(SyncActionKind.UploadRevision), "to upload", Ansi.ToRemote);
        Add(plan.Count(SyncActionKind.CreateLocalFolder) + plan.Count(SyncActionKind.CreateRemoteFolder), "folder(s) to create");
        Add(plan.Count(SyncActionKind.MoveLocal), "to move locally", Ansi.ToLocal);
        Add(plan.Count(SyncActionKind.TrashLocal) + plan.Count(SyncActionKind.TrashLocalEarly), "to move to the local trash", Ansi.Danger);
        Add(plan.Count(SyncActionKind.TrashRemote) + plan.Count(SyncActionKind.TrashRemoteEarly), "to move to the Proton Drive trash", Ansi.Danger);
        var onBothSides = plan.Actions.Where(a => a.Kind is SyncActionKind.ResolveFileConflict or SyncActionKind.RenameLocalForConflict)
            .Select(SyncExecutor.PreviewConflict)
            .ToList();
        Add(onBothSides.Count(p => p == SyncExecutor.ConflictPreview.Conflict), "conflict(s)", Ansi.Conflict);
        Add(onBothSides.Count(p => p == SyncExecutor.ConflictPreview.Compare), "on both sides, compared by checksum when syncing", Ansi.Warning);
        Add(onBothSides.Count(p => p == SyncExecutor.ConflictPreview.Same), "identical on both sides", Ansi.Dim);
        Add(plan.Count(SyncActionKind.RecordBase) + plan.Count(SyncActionKind.ForgetBase), "to record");

        return string.Join(", ", parts);
    }

    /// <summary>A real conflict: two different versions, kept side by side.</summary>
    private static bool IsConflict(SyncAction action, string? note) =>
        action.Kind == SyncActionKind.RenameLocalForConflict
        || (action.Kind == SyncActionKind.ResolveFileConflict && note is not null && note.StartsWith("kept", StringComparison.Ordinal));

    /// <summary>Colors a line about an action by where the change goes.</summary>
    private static string Colorize(SyncAction action, string line) => action.Kind switch
    {
        SyncActionKind.RenameLocalForConflict => Ansi.Conflict(line),
        SyncActionKind.ResolveFileConflict => SyncExecutor.PreviewConflict(action) switch
        {
            SyncExecutor.ConflictPreview.Conflict => Ansi.Conflict(line),
            SyncExecutor.ConflictPreview.Compare => Ansi.Warning(line),
            _ => Ansi.Dim(line),
        },
        SyncActionKind.TrashLocal or SyncActionKind.TrashLocalEarly or SyncActionKind.TrashRemote or SyncActionKind.TrashRemoteEarly => Ansi.Danger(line),
        SyncActionKind.UploadNew or SyncActionKind.UploadRevision or SyncActionKind.CreateRemoteFolder => Ansi.ToRemote(line),
        SyncActionKind.Download or SyncActionKind.CreateLocalFolder or SyncActionKind.MoveLocal => Ansi.ToLocal(line),
        _ => Ansi.Dim(line),
    };

    private static string DescribePlanned(SyncAction action, bool preview = false) => action.Kind switch
    {
        SyncActionKind.MoveLocal => $"move {action.Path} -> {action.TargetPath} (moved on the server)",
        SyncActionKind.RenameLocalForConflict => $"CONFLICT {action.Path}: a file on one side, a folder on the other; both will be kept",
        SyncActionKind.TrashRemoteEarly or SyncActionKind.TrashRemote => $"trash /{action.Path} on Proton Drive",
        SyncActionKind.TrashLocalEarly or SyncActionKind.TrashLocal => $"move {action.Path} to the local trash",
        SyncActionKind.CreateLocalFolder => $"create folder {action.Path} locally",
        SyncActionKind.CreateRemoteFolder => $"create folder /{action.Path} on Proton Drive",
        SyncActionKind.Download => $"download {action.Path}",
        SyncActionKind.UploadNew => $"upload {action.Path}",
        SyncActionKind.UploadRevision => $"upload new version of {action.Path}",
        SyncActionKind.ResolveFileConflict when !preview => $"{action.Path} changed on both sides: keep both unless identical",
        SyncActionKind.ResolveFileConflict => SyncExecutor.PreviewConflict(action) switch
        {
            SyncExecutor.ConflictPreview.Conflict => $"CONFLICT {action.Path}: different on both sides ({FormatSizes(action)}); both versions will be kept",
            SyncExecutor.ConflictPreview.Compare => $"{action.Path} is on both sides with the same size: compared by checksum when syncing, both kept if different",
            _ => $"{action.Path} is identical on both sides (same size and time): recorded as in sync",
        },
        SyncActionKind.RecordBase => $"record {action.Path} as in sync",
        SyncActionKind.ForgetBase => $"forget {action.Path} (gone on both sides)",
        _ => action.ToString(),
    };

    private static string FormatSizes(SyncAction action) =>
        $"local {TransferProgress.FormatBytes(action.Local!.Size)}, Proton Drive {TransferProgress.FormatBytes(action.Remote!.Size)}";

    private static string? DescribeDone(SyncAction action, string? note) => action.Kind switch
    {
        SyncActionKind.MoveLocal => $"Moved {action.Path} -> {action.TargetPath}",
        SyncActionKind.RenameLocalForConflict => $"CONFLICT {action.Path}: a file on one side, a folder on the other; {note}",
        SyncActionKind.ResolveFileConflict when IsConflict(action, note) => $"CONFLICT {action.Path}: different on both sides; {note}",
        SyncActionKind.ResolveFileConflict => $"{action.Path} is on both sides: {note}",
        SyncActionKind.TrashRemoteEarly or SyncActionKind.TrashRemote => $"Trashed /{action.Path} on Proton Drive",
        SyncActionKind.TrashLocalEarly or SyncActionKind.TrashLocal => $"Moved {action.Path} to the local trash",
        SyncActionKind.CreateLocalFolder => $"Created folder {action.Path}",
        SyncActionKind.CreateRemoteFolder => $"Created folder /{action.Path} on Proton Drive",
        _ => null,
    };
}
