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
internal sealed class SyncEngine(ProtonDriveClient client, ISyncObserver observer)
{
    /// <summary>Plans that would delete more than this share of the synced items are refused without --force.</summary>
    private const double MaxDeletedShare = 0.5;

    /// <summary>Deletions up to this many items are always allowed, whatever their share.</summary>
    private const int DeletionsAlwaysAllowed = 10;

    /// <summary>Conflict renames need one more pass to finish; this bounds how many follow-up passes run.</summary>
    private const int MaxPassesPerRun = 3;

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

        SyncPassResult result = new(SyncPassOutcome.UpToDate);
        var succeeded = 0;
        var failed = 0;

        for (var pass = 1; pass <= MaxPassesPerRun; pass++)
        {
            var (outcome, needsAnotherPass) = await RunPassAsync(pair, state, options with { LocalMayHaveChanged = options.LocalMayHaveChanged || pass > 1 }, cancellationToken)
                .ConfigureAwait(false);

            succeeded += outcome.Succeeded;
            failed += outcome.Failed;
            result = outcome.Outcome == SyncPassOutcome.UpToDate && pass > 1
                ? new SyncPassResult(SyncPassOutcome.Completed, succeeded, failed)
                : outcome with { Succeeded = succeeded, Failed = failed };

            if (!needsAnotherPass || options.DryRun)
            {
                break;
            }
        }

        state.SetMeta("last_pass_finished", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        state.SetMeta("last_pass_result", Describe(result));
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

        if (!options.Force && CheckSafety(plan, local.Items.Count, remote.Items.Count) is { } refusal)
        {
            observer.PlanReady(pair, plan, dryRun: true);
            return (new SyncPassResult(SyncPassOutcome.Refused, Message: refusal), false);
        }

        observer.PlanReady(pair, plan, options.DryRun);
        if (options.DryRun)
        {
            return (new SyncPassResult(SyncPassOutcome.DryRun), false);
        }

        var executor = new SyncExecutor(client, pair, state, observer, remote);
        var (succeeded, failed) = await executor.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);

        return (new SyncPassResult(SyncPassOutcome.Completed, succeeded, failed), executor.NeedsAnotherPass);
    }

    /// <summary>
    /// Refuses plans that look like an accident rather than a user's intent: one side suddenly empty, or a large
    /// share of everything synced being deleted.
    /// </summary>
    private static string? CheckSafety(SyncPlan plan, int localCount, int remoteCount)
    {
        if (plan.BaseItemCount > 0 && localCount == 0)
        {
            return $"The local folder is empty, but {plan.BaseItemCount} items were synced before. Syncing now would delete them on the server.";
        }

        if (plan.BaseItemCount > 0 && remoteCount == 0)
        {
            return $"The remote folder is empty, but {plan.BaseItemCount} items were synced before. Syncing now would delete them locally.";
        }

        if (plan.DeletedItemCount > DeletionsAlwaysAllowed && plan.DeletedItemCount > plan.BaseItemCount * MaxDeletedShare)
        {
            return $"This sync would delete {plan.DeletedItemCount} of {plan.BaseItemCount} synced items.";
        }

        return null;
    }

    private static Dictionary<string, T> Without<T>(IReadOnlyDictionary<string, T> items, IReadOnlyList<string> excluded)
    {
        return excluded.Count == 0
            ? new Dictionary<string, T>(items, StringComparer.Ordinal)
            : items.Where(x => !excluded.Any(e => SyncPaths.IsSameOrUnder(x.Key, e))).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    private static string Describe(SyncPassResult result) => result.Outcome switch
    {
        SyncPassOutcome.Completed => $"completed: {result.Succeeded} done, {result.Failed} failed",
        SyncPassOutcome.Refused => $"refused: {result.Message}",
        SyncPassOutcome.UpToDate => "up to date",
        SyncPassOutcome.DryRun => "dry run",
        _ => result.Outcome.ToString().ToLowerInvariant(),
    };
}
