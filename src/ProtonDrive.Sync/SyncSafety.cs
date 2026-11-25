namespace NSkyLabs.ProtonDrive.Sync;

/// <summary>What the safety check decided about a plan.</summary>
/// <param name="Refusal">Set when nothing at all may run.</param>
/// <param name="Plan">The part of the plan that may run (empty when refused).</param>
/// <param name="HeldDeletions">Deletions held back until the user confirms them with --force. They are all on the
/// same side: all on the server, or all local.</param>
/// <param name="HeldItemCount">Synced items those deletions would remove, including folder contents.</param>
/// <param name="HeldReason">Why they were held.</param>
internal sealed record SafetyDecision(
    string? Refusal,
    SyncPlan Plan,
    IReadOnlyList<SyncAction> HeldDeletions,
    int HeldItemCount,
    string? HeldReason)
{
    public static SafetyDecision Allow(SyncPlan plan) => new(null, plan, [], 0, null);

    /// <summary>True if the held deletions are on the server (local deletions not yet applied there).</summary>
    public bool HeldOnServer => HeldDeletions.Count > 0 && HeldDeletions[0].IsRemoteDeletion;
}

/// <summary>
/// Guards against plans that look like an accident rather than the user's intent.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The local folder suddenly empty although items were synced before (an unmounted disk, a broken scan):
/// nothing runs.</item>
/// <item>The remote folder suddenly empty although items were synced before (emptied by mistake, a broken scan):
/// the local deletions are held back, so the local files stay, and nothing else syncs for the folder either.</item>
/// <item>Many local deletions about to be applied on the server (more than <see cref="DeletionsAlwaysAllowed"/>
/// items and more than half of the synced items): those deletions are held back; everything else still syncs.</item>
/// <item>Otherwise deletions made on the server are applied locally without a limit: the server is the
/// reference, and the local copies go to the local trash folder.</item>
/// </list>
/// Held deletions run once the user confirms them with <c>--force</c>.
/// </remarks>
internal static class SyncSafety
{
    /// <summary>Server deletions above this share of the synced items are held back.</summary>
    public const double MaxRemoteDeletedShare = 0.5;

    /// <summary>Up to this many server deletions are always allowed, whatever their share.</summary>
    public const int DeletionsAlwaysAllowed = 10;

    public static SafetyDecision Check(SyncPlan plan, int localCount, int remoteCount)
    {
        if (plan.BaseItemCount > 0 && localCount == 0)
        {
            return new SafetyDecision(
                $"The local folder is empty, but {plan.BaseItemCount} items were synced before. Syncing now would delete them on the server.",
                plan.Without(plan.Actions.ToList()),
                [],
                0,
                null);
        }

        if (plan.BaseItemCount > 0 && remoteCount == 0 && plan.LocalDeletedItemCount > 0)
        {
            // Nothing else may run either: an upload would make the remote folder non-empty, and the next pass would
            // then apply the deletions without asking. So the folder stays blocked until the user restores the remote
            // items (from the Proton Drive trash) or confirms the deletions with --force.
            var hold = Hold(plan, local: true, $"The remote folder is empty, but {plan.BaseItemCount} items were synced before.");
            return hold with { Plan = plan.Without(plan.Actions.ToList()) };
        }

        if (plan.RemoteDeletedItemCount > DeletionsAlwaysAllowed && plan.RemoteDeletedItemCount > plan.BaseItemCount * MaxRemoteDeletedShare)
        {
            return Hold(
                plan,
                local: false,
                $"{plan.RemoteDeletedItemCount} of {plan.BaseItemCount} synced items were deleted locally.");
        }

        return SafetyDecision.Allow(plan);
    }

    private static SafetyDecision Hold(SyncPlan plan, bool local, string reason)
    {
        var held = plan.Actions.Where(a => local ? a.IsLocalDeletion : a.IsRemoteDeletion).ToList();

        // An item replaced on the other side (file <-> folder) is trashed first and then re-created; without the
        // trash the new item would collide with it, so the replacement waits too.
        var earlyKind = local ? SyncActionKind.TrashLocalEarly : SyncActionKind.TrashRemoteEarly;
        var replacedPaths = held.Where(a => a.Kind == earlyKind).Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
        var removed = plan.Actions.Where(a => held.Contains(a) || replacedPaths.Contains(a.Path)).ToList();

        var count = local ? plan.LocalDeletedItemCount : plan.RemoteDeletedItemCount;
        return new SafetyDecision(null, plan.Without(removed), held, count, reason);
    }
}
