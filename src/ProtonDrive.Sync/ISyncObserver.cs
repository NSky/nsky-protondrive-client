namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>Receives what the engine is doing; the console app renders it. Calls may come from several threads.</summary>
internal interface ISyncObserver
{
    void PassStarted(SyncPair pair);

    void Warning(SyncPair pair, string message);

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

    /// <summary>The plan was carried out (some actions may have failed; see the counts).</summary>
    Completed,

    /// <summary>Only the plan was shown (<c>--dry-run</c>).</summary>
    DryRun,

    /// <summary>Another process is syncing this pair.</summary>
    Busy,

    /// <summary>The plan was refused by a safety check; nothing was changed.</summary>
    Refused,
}

internal sealed record SyncPassResult(SyncPassOutcome Outcome, int Succeeded = 0, int Failed = 0, string? Message = null);
