using System.Globalization;
using NSkyLabs.ProtonDrive.Core.IO;
using NSkyLabs.ProtonDrive.Sync.State;
using Proton.Drive.Sdk;

namespace NSkyLabs.ProtonDrive.Sync;

internal sealed record SyncPassOptions
{
    /// <summary>Only report the plan.</summary>
    public bool DryRun { get; init; }

    /// <summary>Skip the safety checks on large deletions and empty sides.</summary>
    public bool Force { get; init; }

    /// <summary>
    /// When false (watch mode, no local file events), the pass ends early if the server reports no changes either.
    /// </summary>
    public bool LocalMayHaveChanged { get; init; } = true;
}

/// <summary>
/// Runs sync passes for one pair: refresh the remote tree from events, scan the local folder, plan, check, execute.
/// A one-shot <c>sync</c> runs a pass; watch mode runs passes when something changes.
/// </summary>
internal sealed class SyncEngine(ProtonDriveClient client, ISyncObserver observer, SyncAttention attention)
{
    /// <summary>Failures in a row (network, server) before the user is alerted; single failures are usually transient.</summary>
    private const int FailuresBeforeAttention = 3;

    private const int MaxListedItems = 50;

    private const string FailureCountKey = "consecutive_failures";
    private const string FirstFailureKey = "first_failure_at";

    /// <summary>Conflict renames need one more pass to finish; this bounds how many follow-up passes run.</summary>
    private const int MaxPassesPerRun = 3;

    /// <summary>State key: server deletions held back by the last pass (shown by 'sync status').</summary>
    public const string HeldDeletionsKey = "held_deletions";

    /// <summary>State key: "server" or "local", the side where the held deletions would happen.</summary>
    public const string HeldSideKey = "held_side";

    private readonly IgnoreRules _ignoreRules = IgnoreRules.Default;

    public async Task<SyncPassResult> RunAsync(SyncPair pair, SyncPassOptions options, CancellationToken cancellationToken)
    {
        using var pairLock = FileLock.TryAcquire(pair.LockPath);
        if (pairLock is null)
        {
            var busy = new SyncPassResult(SyncPassOutcome.Busy, Message: "another process is syncing this folder");
            observer.PassFinished(pair, busy);
            return busy;
        }

        using var state = SyncStateStore.Open(pair.DatabasePath);

        try
        {
            var result = await RunPassesAsync(pair, state, options, cancellationToken).ConfigureAwait(false);
            UpdateAttention(pair, state, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RecordFailure(pair, state, ex);
            throw;
        }
    }

    private async Task<SyncPassResult> RunPassesAsync(SyncPair pair, SyncStateStore state, SyncPassOptions options, CancellationToken cancellationToken)
    {
        SyncPassResult result = new(SyncPassOutcome.UpToDate);
        var succeeded = 0;
        var failed = 0;

        for (var pass = 1; pass <= MaxPassesPerRun; pass++)
        {
            var (outcome, needsAnotherPass) = await RunPassAsync(pair, state, options with { LocalMayHaveChanged = options.LocalMayHaveChanged || pass > 1 }, cancellationToken)
                .ConfigureAwait(false);

            succeeded += outcome.Succeeded;
            failed += outcome.Failed;
            // Each pass plans from scratch, so the last pass's held deletions are the current ones.
            result = outcome.Outcome == SyncPassOutcome.UpToDate && pass > 1
                ? new SyncPassResult(SyncPassOutcome.Completed, succeeded, failed, HeldDeletions: outcome.HeldDeletions, Held: outcome.Held)
                : outcome with { Succeeded = succeeded, Failed = failed };

            if (!needsAnotherPass || options.DryRun)
            {
                break;
            }
        }

        state.SetMeta("last_pass_finished", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        state.SetMeta("last_pass_result", Describe(result));
        if (result.Outcome is SyncPassOutcome.UpToDate or SyncPassOutcome.Completed)
        {
            state.SetMeta(HeldDeletionsKey, result.HeldDeletions.ToString(CultureInfo.InvariantCulture));
        }

        observer.PassFinished(pair, result);
        return result;
    }

    private async Task<(SyncPassResult Result, bool NeedsAnotherPass)> RunPassAsync(
        SyncPair pair,
        SyncStateStore state,
        SyncPassOptions options,
        CancellationToken cancellationToken)
    {
        observer.PassStarted(pair);

        if (!Directory.Exists(pair.LocalPath))
        {
            // Never re-create the folder: a missing folder usually means an unmounted disk, and syncing would then
            // delete everything on the server.
            throw new SyncRootUnavailableException($"The local folder {pair.LocalPath} does not exist. Is the disk mounted?");
        }

        var remoteTree = new RemoteTree(client, state, pair.RemoteRootUid);
        var refresh = await remoteTree.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (!options.LocalMayHaveChanged && !refresh.HasChanges)
        {
            return (new SyncPassResult(SyncPassOutcome.UpToDate), false);
        }

        var remote = remoteTree.GetSnapshot(_ignoreRules);
        var local = LocalScanner.Scan(pair.LocalPath, _ignoreRules);
        var baseItems = state.LoadBase();

        foreach (var warning in remote.Warnings.Concat(local.Warnings).Distinct())
        {
            observer.Warning(pair, warning);
        }

        if (local.ExcludedPaths.Contains(string.Empty))
        {
            throw new SyncRootUnavailableException($"The local folder {pair.LocalPath} cannot be read.");
        }

        // Paths whose state is unknown on one side are left out of the comparison entirely, so they are neither
        // deleted nor overwritten.
        var excluded = local.ExcludedPaths
            .Concat(baseItems.Values.Where(b => remote.UnknownNodeUids.Contains(b.NodeUid)).Select(b => b.Path))
            .ToList();

        var plan = SyncPlanner.Plan(
            Without(local.Items, excluded),
            Without(remote.Items, excluded),
            Without(baseItems, excluded));

        if (plan.IsEmpty)
        {
            return (new SyncPassResult(SyncPassOutcome.UpToDate), false);
        }

        var decision = options.Force
            ? SafetyDecision.Allow(plan)
            : SyncSafety.Check(plan, local.Items.Count, remote.Items.Count);

        if (decision.Refusal is { } refusal)
        {
            observer.PlanReady(pair, plan, dryRun: true);
            return (new SyncPassResult(SyncPassOutcome.Refused, Message: refusal), false);
        }

        var held = decision.HeldItemCount;
        if (decision.HeldDeletions.Count > 0)
        {
            observer.DeletionsHeld(pair, decision);
        }

        state.SetMeta(HeldSideKey, decision.HeldDeletions.Count == 0 ? null : decision.HeldOnServer ? "server" : "local");

        var runnable = decision.Plan;
        if (runnable.IsEmpty)
        {
            return (new SyncPassResult(SyncPassOutcome.UpToDate, HeldDeletions: held, Held: decision), false);
        }

        observer.PlanReady(pair, runnable, options.DryRun);
        if (options.DryRun)
        {
            return (new SyncPassResult(SyncPassOutcome.DryRun, HeldDeletions: held, Held: decision), false);
        }

        var executor = new SyncExecutor(client, pair, state, observer, remote);
        var (succeeded, failed) = await executor.ExecuteAsync(runnable, cancellationToken).ConfigureAwait(false);

        return (new SyncPassResult(SyncPassOutcome.Completed, succeeded, failed, HeldDeletions: held, Held: decision), executor.NeedsAnotherPass);
    }

    /// <summary>Raises or clears the folder's notice after a pass that ran to the end.</summary>
    private void UpdateAttention(SyncPair pair, SyncStateStore state, SyncPassResult result)
    {
        if (result.Outcome is SyncPassOutcome.Busy or SyncPassOutcome.DryRun)
        {
            return;
        }

        state.SetMeta(FailureCountKey, null);
        state.SetMeta(FirstFailureKey, null);

        var notice = result switch
        {
            { Outcome: SyncPassOutcome.Refused } => new AttentionNotice(
                AttentionKind.LocalFolderEmpty,
                "sync stopped: the local folder is empty, but items were synced before",
                $"""
                {result.Message}
                Nothing is synced for this folder until this is resolved.

                - If the folder is on a disk that is not mounted, mount it; syncing resumes by itself.
                - If you emptied the folder on purpose and want the items deleted on Proton Drive too (they go to its
                  trash), run: {AppInfo.CommandName} sync --force {pair.LocalPath}
                """),
            { Held: { HeldOnServer: true } held } => new AttentionNotice(
                AttentionKind.DeletionsHeld,
                $"{result.HeldDeletions} local deletion(s) wait for confirmation before being applied on Proton Drive",
                $"""
                {held.HeldReason}
                That many deletions could be an accident, so they were not applied on Proton Drive.
                Everything else keeps syncing.

                Deleted locally, still on Proton Drive:
                {ListItems(held.HeldDeletions.Select(a => pair.RemotePath.TrimEnd('/') + "/" + a.Path))}

                - To delete them on Proton Drive too (they go to its trash), run:
                  {AppInfo.CommandName} sync --force {pair.LocalPath}
                - To get them back locally instead, download them again, e.g.:
                  {AppInfo.CommandName} download <remote-path> <local-folder>
                """),
            { Held: { HeldOnServer: false, HeldDeletions.Count: > 0 } held } => new AttentionNotice(
                AttentionKind.RemoteFolderEmpty,
                $"sync paused: the remote folder is empty, {result.HeldDeletions} local item(s) kept",
                $"""
                {held.HeldReason}
                This could be an accident, so the local copies are kept, and nothing is synced for this folder
                until you resolve it.

                Kept locally, no longer on Proton Drive:
                {ListItems(held.HeldDeletions.Select(a => a.Path))}

                - If the items were deleted by mistake, restore them from the Proton Drive trash (in the web app);
                  syncing then resumes by itself.
                - If they were deleted on purpose, move the local copies to the local trash by running:
                  {AppInfo.CommandName} sync --force {pair.LocalPath}
                """),
            _ => null,
        };

        if (notice is null)
        {
            attention.Clear(pair);
        }
        else
        {
            attention.Raise(pair, notice);
        }
    }

    /// <summary>Alerts at once for problems retrying cannot fix, and after a few failures in a row for the rest.</summary>
    private void RecordFailure(SyncPair pair, SyncStateStore state, Exception ex)
    {
        switch (ex)
        {
            case SyncRootUnavailableException:
                attention.Raise(pair, new AttentionNotice(
                    AttentionKind.FolderUnavailable,
                    $"sync stopped: {ex.Message.TrimEnd('.')}",
                    $"""
                    {ex.Message}
                    Nothing is synced for this folder until this is resolved; syncing resumes by itself afterwards.
                    To stop syncing this folder instead, run: {AppInfo.CommandName} sync remove {pair.LocalPath}
                    """));
                return;

            case Proton.Sdk.Api.ProtonApiException { Code: 10013 } or Core.Auth.NotLoggedInException:
                attention.Raise(pair, SyncAttention.SessionEndedNotice);
                return;
        }

        var count = int.TryParse(state.GetMeta(FailureCountKey), CultureInfo.InvariantCulture, out var previous) ? previous + 1 : 1;
        state.SetMeta(FailureCountKey, count.ToString(CultureInfo.InvariantCulture));
        if (state.GetMeta(FirstFailureKey) is null)
        {
            state.SetMeta(FirstFailureKey, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        if (count >= FailuresBeforeAttention)
        {
            attention.Raise(pair, new AttentionNotice(
                AttentionKind.Failing,
                $"sync is failing: {ex.Message.TrimEnd('.')}",
                $"""
                Syncing this folder has failed {count} times in a row since {state.GetMeta(FirstFailureKey)}.
                Last error: {ex.Message}

                It keeps retrying. If the error is about the network, check the connection; otherwise see the log:
                  journalctl --user -u protondrive-sync-watch
                """));
        }
    }

    private static string ListItems(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var lines = list.Take(MaxListedItems).Select(p => "  " + p).ToList();
        if (list.Count > MaxListedItems)
        {
            lines.Add($"  ... and {list.Count - MaxListedItems} more");
        }

        return string.Join("\n", lines);
    }

    private static Dictionary<string, T> Without<T>(IReadOnlyDictionary<string, T> items, IReadOnlyList<string> excluded)
    {
        return excluded.Count == 0
            ? new Dictionary<string, T>(items, StringComparer.Ordinal)
            : items.Where(x => !excluded.Any(e => SyncPaths.IsSameOrUnder(x.Key, e))).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    private static string Describe(SyncPassResult result) => (result.Outcome switch
    {
        SyncPassOutcome.Completed => $"completed: {result.Succeeded} done, {result.Failed} failed",
        SyncPassOutcome.Refused => $"refused: {result.Message}",
        SyncPassOutcome.UpToDate => "up to date",
        SyncPassOutcome.DryRun => "dry run",
        _ => result.Outcome.ToString().ToLowerInvariant(),
    }) + (result.HeldDeletions > 0 ? $"; {result.HeldDeletions} deletion(s) held back" : string.Empty);
}
