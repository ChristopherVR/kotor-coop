using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>A joiner's post-load "Item(s) Received" box goes away by itself (2026-10-11).</summary>
public sealed class LoadSummaryDismissRuleTests
{
    [Fact]
    public void AJoinersBoxRightAfterALoadIsDismissedOnceSeen()
    {
        Assert.False(LoadSummaryDismissRule.ShouldDismiss(true, true, 1500, LoadSummaryDismissRule.ShownForMs - 1));
        Assert.True(LoadSummaryDismissRule.ShouldDismiss(true, true, 1500, LoadSummaryDismissRule.ShownForMs));
    }

    [Fact]
    public void TheHostsBoxStays()
    {
        Assert.False(LoadSummaryDismissRule.ShouldDismiss(false, true, 1500, 5000));
    }

    [Fact]
    public void ABoxLongAfterTheLoadWaitsForThePlayer()
    {
        Assert.False(LoadSummaryDismissRule.ShouldDismiss(true, true, LoadSummaryDismissRule.AfterLoadWindowMs + 1, 5000));
    }

    [Fact]
    public void NoBoxNothingToDo()
    {
        Assert.False(LoadSummaryDismissRule.ShouldDismiss(true, false, 1500, 5000));
    }
}
