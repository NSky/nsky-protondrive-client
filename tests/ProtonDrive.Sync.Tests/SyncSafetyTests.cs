using static NSkyLabs.ProtonDrive.Sync.SyncActionKind;

namespace NSkyLabs.ProtonDrive.Sync.Tests;

public sealed class SyncSafetyTests
{
    private readonly Dictionary<string, LocalItem> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteItem> _remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BaseItem> _base = new(StringComparer.Ordinal);

    [Fact]
    public void Mass_deletion_on_the_server_is_applied_locally_without_confirmation()
    {
        SyncedFiles(30);
        for (var i = 0; i < 25; i++)
        {
            _remote.Remove($"f{i}.txt");
        }

        var decision = Check();

        Assert.Null(decision.Refusal);
        Assert.Empty(decision.HeldDeletions);
        Assert.Equal(25, decision.Plan.Count(TrashLocal));
    }

    [Fact]
    public void Mass_deletion_locally_is_held_back_and_everything_else_still_syncs()
    {
        SyncedFiles(30);
        for (var i = 0; i < 25; i++)
        {
            _local.Remove($"f{i}.txt");
        }

        _remote["new-on-server.txt"] = new RemoteItem("new-on-server.txt", false, "n-new", "r-new", 5, null, null);
        _local["new-locally.txt"] = new LocalItem("new-locally.txt", false, 5, 1);

        var decision = Check();

        Assert.Null(decision.Refusal);
        Assert.Equal(25, decision.HeldDeletions.Count);
        Assert.DoesNotContain(decision.Plan.Actions, a => a.IsRemoteDeletion);
        Assert.Equal([(UploadNew, "new-locally.txt"), (Download, "new-on-server.txt")], decision.Plan.Actions.Select(a => (a.Kind, a.Path)).ToList());
        Assert.Equal(0, decision.Plan.RemoteDeletedItemCount);
    }

    [Fact]
    public void A_few_local_deletions_are_applied()
    {
        SyncedFiles(30);
        for (var i = 0; i < 10; i++)
        {
            _local.Remove($"f{i}.txt");
        }

        var decision = Check();

        Assert.Empty(decision.HeldDeletions);
        Assert.Equal(10, decision.Plan.Count(TrashRemote));
    }

    [Fact]
    public void Local_deletion_of_a_large_folder_counts_its_contents()
    {
        SyncedFiles(5);
        _base["big"] = new BaseItem("big", true, "big", null, 0, 0);
        _remote["big"] = new RemoteItem("big", true, "big", null, 0, null, null);
        for (var i = 0; i < 20; i++)
        {
            var path = $"big/f{i}.txt";
            _base[path] = new BaseItem(path, false, $"b{i}", $"rb{i}", 10, 1000);
            _remote[path] = new RemoteItem(path, false, $"b{i}", $"rb{i}", 10, null, null);
        }

        var decision = Check();

        var held = Assert.Single(decision.HeldDeletions);
        Assert.Equal((TrashRemote, "big"), (held.Kind, held.Path));
    }

    [Fact]
    public void A_replaced_item_waits_together_with_its_held_deletion()
    {
        SyncedFiles(30);
        for (var i = 0; i < 25; i++)
        {
            _local.Remove($"f{i}.txt");
        }

        // f29.txt became a folder locally: the remote file must be trashed before the folder can be created.
        _local["f29.txt"] = new LocalItem("f29.txt", true, 0, 0);

        var decision = Check();

        Assert.Contains(decision.HeldDeletions, a => a is { Kind: TrashRemoteEarly, Path: "f29.txt" });
        Assert.DoesNotContain(decision.Plan.Actions, a => a.Path == "f29.txt");
    }

    [Fact]
    public void Empty_local_folder_refuses_everything()
    {
        SyncedFiles(3);
        _local.Clear();
        _remote["new.txt"] = new RemoteItem("new.txt", false, "n", "r", 1, null, null);

        var decision = Check();

        Assert.NotNull(decision.Refusal);
        Assert.Empty(decision.Plan.Actions);
    }

    [Fact]
    public void Empty_remote_folder_keeps_the_local_files_and_blocks_the_folder()
    {
        SyncedFiles(3);
        _base["docs"] = new BaseItem("docs", true, "d", null, 0, 0);
        _local["docs"] = new LocalItem("docs", true, 0, 0);
        _base["docs/a.txt"] = new BaseItem("docs/a.txt", false, "a", "ra", 10, 1000);
        _local["docs/a.txt"] = new LocalItem("docs/a.txt", false, 10, 1000);
        _remote.Clear();
        _local["new.txt"] = new LocalItem("new.txt", false, 5, 1);

        var decision = Check();

        Assert.Null(decision.Refusal);
        Assert.False(decision.HeldOnServer);
        Assert.All(decision.HeldDeletions, a => Assert.True(a.IsLocalDeletion));
        Assert.Equal(["docs", "f0.txt", "f1.txt", "f2.txt"], decision.HeldDeletions.Select(a => a.Path).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(5, decision.HeldItemCount);
        // An upload would make the remote folder non-empty, and the next pass would then delete without asking.
        Assert.Empty(decision.Plan.Actions);
    }

    [Fact]
    public void Empty_remote_folder_with_nothing_to_delete_locally_is_not_held()
    {
        SyncedFiles(2);
        _remote.Clear();
        _local.Clear();
        _local["new.txt"] = new LocalItem("new.txt", false, 5, 1);

        var decision = Check();

        Assert.Empty(decision.HeldDeletions);
        Assert.Contains(decision.Plan.Actions, a => a is { Kind: UploadNew, Path: "new.txt" });
    }

    private SafetyDecision Check()
    {
        var plan = SyncPlanner.Plan(_local, _remote, _base);
        return SyncSafety.Check(plan, _local.Count, _remote.Count);
    }

    private void SyncedFiles(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = $"f{i}.txt";
            _local[path] = new LocalItem(path, false, 10, 1000);
            _remote[path] = new RemoteItem(path, false, $"n{i}", $"r{i}", 10, null, null);
            _base[path] = new BaseItem(path, false, $"n{i}", $"r{i}", 10, 1000);
        }
    }
}
