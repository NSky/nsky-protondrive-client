namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>Receives what the engine is doing; the console app renders it. Calls may come from several threads.</summary>
internal interface ISyncObserver
{
    void PassStarted(SyncPair pair);

    void Warning(SyncPair pair, string message);

    /// <summary>
    /// Deletions that look like an accident were held back (see <see cref="SyncSafety"/>); they wait for
    /// <c>sync --force</c>. Called before <see cref="PlanReady"/>, on every pass while they are pending.
    /// </summary>
    void DeletionsHeld(SyncPair pair, SafetyDecision decision);

    /// <summary>A plan was made. Not called when there is nothing to do.</summary>
    void PlanReady(SyncPair pair, SyncPlan plan, bool dryRun);

    /// <summary>A transfer started; the returned callback receives the bytes transferred so far.</summary>
    Action<long> TransferStarted(SyncAction action);

    void ActionSucceeded(SyncAction action, string? note = null);

    void ActionFailed(SyncAction action, Exception error);

    void PassFinished(SyncPair pair, SyncPassResult result);
}

internal enum SyncPassOutcome
{
    /// <summary>Nothing changed on either side.</summary>
    UpToDate,

    /// <summary>
    /// A quick check in watch mode: the server reported no changes and there were no local file events, so the
    /// folders were not compared. Says nothing about held deletions or other problems found by earlier passes.
    /// </summary>
    NotChecked,

    /// <summary>The plan was carried out (some actions may have failed; see the counts).</summary>
    Completed,

    /// <summary>Only the plan was shown (<c>--dry-run</c>).</summary>
    DryRun,

    /// <summary>Another process is syncing this pair.</summary>
    Busy,

    /// <summary>The plan was refused by a safety check (one side suddenly empty); nothing was changed.</summary>
    Refused,
}

/// <param name="HeldDeletions">Synced items whose deletion (on either side) waits for --force.</param>
/// <param name="Held">The safety decision that held them, if any.</param>
internal sealed record SyncPassResult(
    SyncPassOutcome Outcome,
    int Succeeded = 0,
    int Failed = 0,
    string? Message = null,
    int HeldDeletions = 0,
    SafetyDecision? Held = null);
