namespace NSkyLabs.ProtonDrive.Sync.Tests;

public sealed class ConflictMatchTests
{
    private static readonly DateTimeOffset Time = new(2026, 1, 31, 15, 45, 0, 123, TimeSpan.Zero);
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static LocalItem Local(long size, DateTimeOffset modified) => new("a.txt", false, size, modified.UtcTicks);

    private static RemoteItem Remote(long size, string? sha1, DateTimeOffset? modified) => new("a.txt", false, "n", "r", size, sha1, modified);

    private static string Match(LocalItem local, RemoteItem remote, string? localSha1) =>
        SyncExecutor.CompareConflictingFiles(local, remote, localSha1).ToString();

    [Fact]
    public void Same_hash_is_the_same_content()
    {
        Assert.Equal("SameContent", Match(Local(10, Time), Remote(10, Hash, Time.AddDays(-3)), Hash.ToUpperInvariant()));
    }

    [Fact]
    public void Different_hash_is_a_conflict_even_when_size_and_time_match()
    {
        Assert.Equal("Different", Match(Local(10, Time), Remote(10, Hash, Time), "ffffffffffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void Without_a_server_hash_same_size_and_time_match()
    {
        Assert.Equal("SameSizeAndTime", Match(Local(10, Time), Remote(10, null, Time)));
    }

    [Fact]
    public void Without_a_server_hash_whole_second_times_still_match()
    {
        var truncated = new DateTimeOffset(2026, 1, 31, 15, 45, 0, TimeSpan.Zero);
        Assert.Equal("SameSizeAndTime", Match(Local(10, Time), Remote(10, null, truncated)));
    }

    [Fact]
    public void Without_a_server_hash_a_different_time_is_a_conflict()
    {
        Assert.Equal("Different", Match(Local(10, Time), Remote(10, null, Time.AddSeconds(5))));
    }

    [Fact]
    public void Without_a_server_hash_or_time_it_is_a_conflict()
    {
        Assert.Equal("Different", Match(Local(10, Time), Remote(10, null, null)));
    }

    [Fact]
    public void Different_size_is_a_conflict()
    {
        Assert.Equal("Different", Match(Local(10, Time), Remote(11, null, Time)));
    }

    private static string Match(LocalItem local, RemoteItem remote) => Match(local, remote, null);
}

public sealed class ConflictPreviewTests
{
    private static readonly DateTimeOffset Time = new(2026, 1, 31, 15, 45, 0, TimeSpan.Zero);

    private static string Preview(long localSize, long remoteSize, string? sha1, DateTimeOffset? remoteTime, SyncActionKind kind = SyncActionKind.ResolveFileConflict) =>
        SyncExecutor.PreviewConflict(new SyncAction(kind, "a.txt")
        {
            Local = new LocalItem("a.txt", false, localSize, Time.UtcTicks),
            Remote = new RemoteItem("a.txt", false, "n", "r", remoteSize, sha1, remoteTime),
        }).ToString();

    [Fact]
    public void Different_sizes_are_a_conflict() => Assert.Equal("Conflict", Preview(10, 11, "abc", Time));

    [Fact]
    public void Same_size_with_a_server_checksum_is_compared_when_syncing() => Assert.Equal("Compare", Preview(10, 10, "abc", Time.AddDays(-1)));

    [Fact]
    public void Same_size_and_time_without_a_checksum_is_identical() => Assert.Equal("Same", Preview(10, 10, null, Time));

    [Fact]
    public void Same_size_different_time_without_a_checksum_is_a_conflict() => Assert.Equal("Conflict", Preview(10, 10, null, Time.AddMinutes(5)));

    [Fact]
    public void File_against_folder_is_a_conflict() => Assert.Equal("Conflict", Preview(10, 10, "abc", Time, SyncActionKind.RenameLocalForConflict));
}
