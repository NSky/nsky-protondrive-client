namespace NSkyLabs.ProtonDrive.Sync.Tests;

public sealed class SyncAttentionTests
{
    [Theory]
    [InlineData("LocalFolderEmpty")]
    [InlineData("RemoteFolderEmpty")]
    [InlineData("DeletionsHeld")]
    public void A_quick_check_keeps_problems_only_a_comparison_can_resolve(string currentName)
    {
        // Regression: a watch-mode check that compared nothing used to count as "up to date" and removed the notice.
        Assert.Equal(AttentionUpdate.Keep, SyncAttention.DecideUpdate(SyncPassOutcome.NotChecked, Enum.Parse<AttentionKind>(currentName)));
    }

    [Theory]
    [InlineData("Failing")]
    [InlineData("SessionEnded")]
    [InlineData("FolderUnavailable")]
    public void A_quick_check_clears_problems_its_success_disproves(string currentName)
    {
        Assert.Equal(AttentionUpdate.Clear, SyncAttention.DecideUpdate(SyncPassOutcome.NotChecked, Enum.Parse<AttentionKind>(currentName)));
    }

    [Fact]
    public void A_quick_check_without_a_problem_changes_nothing()
    {
        Assert.Equal(AttentionUpdate.Keep, SyncAttention.DecideUpdate(SyncPassOutcome.NotChecked, null));
    }

    [Theory]
    [InlineData("UpToDate")]
    [InlineData("Completed")]
    [InlineData("Refused")]
    public void A_pass_that_compared_both_sides_decides(string outcomeName)
    {
        Assert.Equal(AttentionUpdate.Evaluate, SyncAttention.DecideUpdate(Enum.Parse<SyncPassOutcome>(outcomeName), AttentionKind.LocalFolderEmpty));
    }

    [Theory]
    [InlineData("Busy")]
    [InlineData("DryRun")]
    public void Busy_and_dry_runs_change_nothing(string outcomeName)
    {
        Assert.Equal(AttentionUpdate.Keep, SyncAttention.DecideUpdate(Enum.Parse<SyncPassOutcome>(outcomeName), AttentionKind.Failing));
    }
}
