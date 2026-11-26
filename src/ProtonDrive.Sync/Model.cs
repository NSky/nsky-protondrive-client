namespace NSkyLabs.ProtonDrive.Sync;

// Paths in the sync engine are relative to the sync root, use '/' as separator, have no leading or trailing slash,
// and compare ordinally (case-sensitive). The root itself is "".

/// <summary>A file or folder found by the local scan.</summary>
/// <param name="ModifiedTicks">Last write time (UTC ticks). Folders: 0, their times are not compared.</param>
internal sealed record LocalItem(string Path, bool IsFolder, long Size, long ModifiedTicks);

/// <summary>A file or folder in the remote tree, as known from the last scan plus events.</summary>
internal sealed record RemoteItem(
    string Path,
    bool IsFolder,
    string NodeUid,
    string? RevisionUid,
    long Size,
    string? Sha1,
    DateTimeOffset? Modified);

/// <summary>
/// The last synced state of a path: what both sides agreed on after the last successful action on it.
/// Comparing each side with this tells which side changed.
/// </summary>
internal sealed record BaseItem(string Path, bool IsFolder, string NodeUid, string? RevisionUid, long Size, long ModifiedTicks);

/// <summary>Kinds of sync actions, in the order the executor runs them.</summary>
internal enum SyncActionKind
{
    /// <summary>A node was moved or renamed on the server: move the local item along (phase 0).</summary>
    MoveLocal,

    /// <summary>Both sides changed incompatibly: rename the local item to a conflict name (phase 0).</summary>
    RenameLocalForConflict,

    /// <summary>The local item changed between file and folder: trash the remote one before replacing it (phase 1).</summary>
    TrashRemoteEarly,

    /// <summary>The remote item changed between file and folder: trash the local one before replacing it (phase 1).</summary>
    TrashLocalEarly,

    CreateLocalFolder,
    CreateRemoteFolder,

    /// <summary>An item was moved or renamed locally: move the node on the server along (phase 2, with the folders).</summary>
    MoveRemote,

    Download,
    UploadNew,
    UploadRevision,

    /// <summary>Both sides changed the file: compare content; if different, keep both copies.</summary>
    ResolveFileConflict,

    TrashLocal,
    TrashRemote,

    /// <summary>Both sides already agree: just record the synced state.</summary>
    RecordBase,

    /// <summary>The item is gone on both sides: forget it.</summary>
    ForgetBase,
}

internal sealed record SyncAction(SyncActionKind Kind, string Path)
{
    /// <summary>The destination of <see cref="SyncActionKind.MoveLocal"/> and <see cref="SyncActionKind.MoveRemote"/>.</summary>
    public string? TargetPath { get; init; }

    /// <summary>
    /// For <see cref="SyncActionKind.MoveRemote"/>: the destination's parent folder is created by this plan, so the
    /// move waits for it; otherwise the move runs before any folder is created.
    /// </summary>
    public bool TargetParentIsNew { get; init; }

    public bool IsMove => Kind is SyncActionKind.MoveLocal or SyncActionKind.MoveRemote;

    public LocalItem? Local { get; init; }

    public RemoteItem? Remote { get; init; }

    public BaseItem? Base { get; init; }

    public bool IsTransfer => Kind is SyncActionKind.Download or SyncActionKind.UploadNew or SyncActionKind.UploadRevision;

    public bool IsDownload => Kind is SyncActionKind.Download;

    /// <summary>Deletes on the server (a local deletion being applied there).</summary>
    public bool IsRemoteDeletion => Kind is SyncActionKind.TrashRemote or SyncActionKind.TrashRemoteEarly;

    /// <summary>Deletes locally (a deletion on the server being applied here).</summary>
    public bool IsLocalDeletion => Kind is SyncActionKind.TrashLocal or SyncActionKind.TrashLocalEarly;

    public long TransferSize => Kind switch
    {
        SyncActionKind.Download => Remote?.Size ?? 0,
        SyncActionKind.UploadNew or SyncActionKind.UploadRevision => Local?.Size ?? 0,
        _ => 0,
    };

    public int Phase => Kind switch
    {
        SyncActionKind.MoveLocal or SyncActionKind.RenameLocalForConflict => 0,
        SyncActionKind.TrashRemoteEarly or SyncActionKind.TrashLocalEarly => 1,
        SyncActionKind.CreateLocalFolder or SyncActionKind.CreateRemoteFolder or SyncActionKind.MoveRemote => 2,
        SyncActionKind.Download or SyncActionKind.UploadNew or SyncActionKind.UploadRevision or SyncActionKind.ResolveFileConflict => 3,
        SyncActionKind.TrashLocal or SyncActionKind.TrashRemote => 4,
        _ => 5,
    };

    public override string ToString() => IsMove ? $"{Kind} {Path} -> {TargetPath}" : $"{Kind} {Path}";
}

internal sealed class SyncPlan
{
    public required IReadOnlyList<SyncAction> Actions { get; init; }

    /// <summary>Synced items (from the base) this plan deletes locally, including folder contents.</summary>
    public required int LocalDeletedItemCount { get; init; }

    /// <summary>Synced items (from the base) this plan deletes on the server, including folder contents.</summary>
    public required int RemoteDeletedItemCount { get; init; }

    /// <summary>Number of items in the base when the plan was made.</summary>
    public required int BaseItemCount { get; init; }

    public bool IsEmpty => Actions.Count == 0;

    public int Count(SyncActionKind kind) => Actions.Count(a => a.Kind == kind);

    /// <summary>The same plan without <paramref name="removed"/>; their deletions no longer count.</summary>
    public SyncPlan Without(IReadOnlyCollection<SyncAction> removed)
    {
        var remaining = Actions.Where(a => !removed.Contains(a)).ToList();
        var removedLocal = removed.Any(a => a.IsLocalDeletion);
        var removedRemote = removed.Any(a => a.IsRemoteDeletion);

        return new SyncPlan
        {
            Actions = remaining,
            LocalDeletedItemCount = removedLocal && !remaining.Any(a => a.IsLocalDeletion) ? 0 : LocalDeletedItemCount,
            RemoteDeletedItemCount = removedRemote && !remaining.Any(a => a.IsRemoteDeletion) ? 0 : RemoteDeletedItemCount,
            BaseItemCount = BaseItemCount,
        };
    }
}
