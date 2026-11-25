using static NSkyLabs.ProtonDrive.Sync.SyncActionKind;

namespace NSkyLabs.ProtonDrive.Sync.Tests;

public sealed class SyncPlannerTests
{
    private readonly Dictionary<string, LocalItem> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteItem> _remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseItem> _base = new(StringComparer.Ordinal);

    [Fact]
    public void Nothing_changed_plans_nothing()
    {
        Synced("a.txt", "n1", "r1");
        SyncedFolder("docs", "n2");
        Synced("docs/b.txt", "n3", "r3");

        Assert.Empty(Plan().Actions);
    }

    [Fact]
    public void New_local_items_are_uploaded_parents_first()
    {
        LocalFolder("docs");
        LocalFile("docs/a.txt");
        LocalFile("top.txt");

        Assert.Equal([(CreateRemoteFolder, "docs"), (UploadNew, "docs/a.txt"), (UploadNew, "top.txt")], Kinds(Plan()));
    }

    [Fact]
    public void New_remote_items_are_downloaded()
    {
        RemoteFolder("docs", "n1");
        RemoteFile("docs/a.txt", "n2", "r2");

        Assert.Equal([(CreateLocalFolder, "docs"), (Download, "docs/a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Local_edit_uploads_a_new_revision()
    {
        Synced("a.txt", "n1", "r1");
        _local["a.txt"] = _local["a.txt"] with { Size = 99 };

        Assert.Equal([(UploadRevision, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Remote_edit_downloads_and_expects_the_unchanged_local_file()
    {
        Synced("a.txt", "n1", "r1");
        _remote["a.txt"] = _remote["a.txt"] with { RevisionUid = "r2" };

        var action = Assert.Single(Plan().Actions);
        Assert.Equal(Download, action.Kind);
        Assert.Equal(_local["a.txt"], action.Local);
    }

    [Fact]
    public void Local_deletion_trashes_remote()
    {
        Synced("a.txt", "n1", "r1");
        _local.Remove("a.txt");

        Assert.Equal([(TrashRemote, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Remote_deletion_trashes_local()
    {
        Synced("a.txt", "n1", "r1");
        _remote.Remove("a.txt");

        Assert.Equal([(TrashLocal, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Edit_wins_over_deletion()
    {
        Synced("edited-locally.txt", "n1", "r1");
        _local["edited-locally.txt"] = _local["edited-locally.txt"] with { Size = 5 };
        _remote.Remove("edited-locally.txt");

        Synced("edited-remotely.txt", "n2", "r2");
        _remote["edited-remotely.txt"] = _remote["edited-remotely.txt"] with { RevisionUid = "r3" };
        _local.Remove("edited-remotely.txt");

        Assert.Equal([(UploadNew, "edited-locally.txt"), (Download, "edited-remotely.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Both_sides_changed_is_a_conflict()
    {
        Synced("a.txt", "n1", "r1");
        _local["a.txt"] = _local["a.txt"] with { Size = 5 };
        _remote["a.txt"] = _remote["a.txt"] with { RevisionUid = "r2" };

        Assert.Equal([(ResolveFileConflict, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Same_new_file_on_both_sides_is_resolved_by_content_comparison()
    {
        LocalFile("a.txt");
        RemoteFile("a.txt", "n1", "r1");

        Assert.Equal([(ResolveFileConflict, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Same_new_folder_on_both_sides_is_merged()
    {
        LocalFolder("docs");
        LocalFile("docs/local.txt");
        RemoteFolder("docs", "n1");
        RemoteFile("docs/remote.txt", "n2", "r2");

        // Transfers come before recording the merged folder; the folder exists on both sides already.
        Assert.Equal([(UploadNew, "docs/local.txt"), (Download, "docs/remote.txt"), (RecordBase, "docs")], Kinds(Plan()));
    }

    [Fact]
    public void File_on_one_side_and_folder_on_the_other_renames_the_local_item()
    {
        LocalFile("thing");
        RemoteFolder("thing", "n1");

        Assert.Equal([(RenameLocalForConflict, "thing")], Kinds(Plan()));
    }

    [Fact]
    public void Gone_on_both_sides_is_forgotten()
    {
        Synced("a.txt", "n1", "r1");
        _local.Remove("a.txt");
        _remote.Remove("a.txt");

        Assert.Equal([(ForgetBase, "a.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Deleted_remote_folder_is_trashed_locally_as_a_whole()
    {
        SyncedFolder("docs", "n1");
        Synced("docs/a.txt", "n2", "r2");
        SyncedFolder("docs/sub", "n3");
        Synced("docs/sub/b.txt", "n4", "r4");
        RemoveRemoteSubtree("docs");

        var plan = Plan();

        Assert.Equal([(TrashLocal, "docs")], Kinds(plan));
        Assert.Equal(4, plan.LocalDeletedItemCount);
        Assert.Equal(0, plan.RemoteDeletedItemCount);
    }

    [Fact]
    public void Deleted_remote_folder_with_local_changes_is_recreated_and_only_unchanged_items_trashed()
    {
        SyncedFolder("docs", "n1");
        Synced("docs/unchanged.txt", "n2", "r2");
        Synced("docs/edited.txt", "n3", "r3");
        LocalFile("docs/new.txt");
        _local["docs/edited.txt"] = _local["docs/edited.txt"] with { Size = 42 };
        RemoveRemoteSubtree("docs");

        Assert.Equal(
            [(CreateRemoteFolder, "docs"), (UploadNew, "docs/edited.txt"), (UploadNew, "docs/new.txt"), (TrashLocal, "docs/unchanged.txt")],
            Kinds(Plan()));
    }

    [Fact]
    public void Deleted_local_folder_with_new_remote_content_is_recreated_locally()
    {
        SyncedFolder("docs", "n1");
        Synced("docs/old.txt", "n2", "r2");
        RemoteFile("docs/new.txt", "n3", "r3");
        _local.Remove("docs");
        _local.Remove("docs/old.txt");

        Assert.Equal([(CreateLocalFolder, "docs"), (Download, "docs/new.txt"), (TrashRemote, "docs/old.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Remote_rename_moves_the_local_file_instead_of_downloading_it()
    {
        Synced("old.txt", "n1", "r1");
        var item = _remote["old.txt"];
        _remote.Remove("old.txt");
        _remote["new.txt"] = item with { Path = "new.txt" };

        var action = Assert.Single(Plan().Actions);
        Assert.Equal((MoveLocal, "old.txt", "new.txt"), (action.Kind, action.Path, action.TargetPath));
    }

    [Fact]
    public void Remote_folder_move_moves_the_whole_local_folder()
    {
        SyncedFolder("a", "n1");
        Synced("a/x.txt", "n2", "r2");
        SyncedFolder("b", "n3");
        MoveRemote("a", "b/a");

        var plan = Plan();

        var action = Assert.Single(plan.Actions);
        Assert.Equal((MoveLocal, "a", "b/a"), (action.Kind, action.Path, action.TargetPath));
    }

    [Fact]
    public void Remote_move_of_a_locally_edited_file_is_not_a_move()
    {
        Synced("old.txt", "n1", "r1");
        _local["old.txt"] = _local["old.txt"] with { Size = 7 };
        MoveRemote("old.txt", "new.txt");

        // The edit is kept as a new file at the old path; the moved file is downloaded at its new path.
        Assert.Equal([(Download, "new.txt"), (UploadNew, "old.txt")], Kinds(Plan()));
    }

    [Fact]
    public void Remote_file_replaced_by_folder_trashes_the_local_file_first()
    {
        Synced("thing", "n1", "r1");
        _remote["thing"] = new RemoteItem("thing", IsFolder: true, "n9", null, 0, null, null);

        var plan = Plan();

        Assert.Equal([(TrashLocalEarly, "thing"), (CreateLocalFolder, "thing")], Kinds(plan));
        Assert.Null(plan.Actions[1].Local);
    }

    [Fact]
    public void Deletion_count_covers_folder_contents()
    {
        SyncedFolder("docs", "n1");
        Synced("docs/a.txt", "n2", "r2");
        Synced("keep.txt", "n3", "r3");
        _local.Remove("docs");
        _local.Remove("docs/a.txt");

        var plan = Plan();

        Assert.Equal(2, plan.RemoteDeletedItemCount);
        Assert.Equal(0, plan.LocalDeletedItemCount);
        Assert.Equal(3, plan.BaseItemCount);
    }

    private SyncPlan Plan() => SyncPlanner.Plan(_local, _remote, _base);

    private static List<(SyncActionKind, string)> Kinds(SyncPlan plan) => plan.Actions.Select(a => (a.Kind, a.Path)).ToList();

    private void LocalFile(string path, long size = 10, long ticks = 1000) => _local[path] = new LocalItem(path, IsFolder: false, size, ticks);

    private void LocalFolder(string path) => _local[path] = new LocalItem(path, IsFolder: true, 0, 0);

    private void RemoteFile(string path, string uid, string revision, long size = 10) =>
        _remote[path] = new RemoteItem(path, IsFolder: false, uid, revision, size, Sha1: null, Modified: null);

    private void RemoteFolder(string path, string uid) => _remote[path] = new RemoteItem(path, IsFolder: true, uid, null, 0, null, null);

    private void Synced(string path, string uid, string revision)
    {
        LocalFile(path);
        RemoteFile(path, uid, revision);
        _base[path] = new BaseItem(path, IsFolder: false, uid, revision, 10, 1000);
    }

    private void SyncedFolder(string path, string uid)
    {
        LocalFolder(path);
        RemoteFolder(path, uid);
        _base[path] = new BaseItem(path, IsFolder: true, uid, null, 0, 0);
    }

    private void RemoveRemoteSubtree(string path)
    {
        foreach (var key in _remote.Keys.Where(k => SyncPaths.IsSameOrUnder(k, path)).ToList())
        {
            _remote.Remove(key);
        }
    }

    private void MoveRemote(string from, string to)
    {
        foreach (var key in _remote.Keys.Where(k => SyncPaths.IsSameOrUnder(k, from)).ToList())
        {
            var item = _remote[key];
            _remote.Remove(key);
            var newPath = SyncPaths.Rebase(key, from, to);
            _remote[newPath] = item with { Path = newPath };
        }
    }
}
