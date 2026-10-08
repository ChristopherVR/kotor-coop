using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlaytestInteractionRegressionTests
{
    [Fact]
    public void ContainerOpenPreservesArgumentsAndIsConsumedOnce()
    {
        var open = new DeferredContainerOpen();
        open.Capture(198, 7, 0x7FFFFFFF, 1);
        Assert.False(open.TryTake(199, 7, out _, out _));
        Assert.False(open.TryTake(198, 6, out _, out _));
        Assert.True(open.TryTake(198, 7, out uint actor, out int feedback));
        Assert.Equal(0x7FFFFFFFu, actor);
        Assert.Equal(1, feedback);
        Assert.False(open.TryTake(198, 7, out _, out _));
    }

    [Fact]
    public void OldGrantCannotReplayANewerClickOrCrossWorldInvalidation()
    {
        var open = new DeferredContainerOpen();
        open.Capture(198, 7, 10, 0);
        open.Capture(199, 8, 11, 1);
        Assert.False(open.TryTake(198, 7, out _, out _));
        Assert.True(open.TryTake(199, 8, out uint actor, out _));
        Assert.Equal(11u, actor);
        open.Capture(199, 9, 11, 1);
        open.Clear();
        Assert.False(open.TryTake(199, 9, out _, out _));
        Assert.False(open.TryTake(0, 0, out _, out _));
    }

    [Theory]
    [InlineData(true, 1, 1, true)]
    [InlineData(false, 1, 1, false)]
    [InlineData(true, 0, 1, false)]
    [InlineData(true, 1, 4, false)] // manual/shared pause
    [InlineData(true, 1, 6, false)] // status summary
    [InlineData(true, 1, 0, false)] // ordinary GUI pause
    public void EnemyBannerGatePreservesOtherPausePaths(bool coop, int paused, int reason, bool expected)
        => Assert.Equal(expected, PausePresentationRule.SuppressEnemyBanner(coop, paused, reason));

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void ReconcilerLeavesIntentionalUiTimersAlone(bool gui, bool conversation, bool modal, bool expected)
        => Assert.Equal(expected, PausePresentationRule.CanReconcile(gui, conversation, modal));

    // A conversation never chooses "timer stopped, no reason bit, no banner" (SetPauseState sets a
    // reason first), so that one repair runs during a conversation; menus and modals stay hands-off.
    // Live 2026-10-08: the joiner's world sat frozen with no banner for 32 s inside a conversation swap.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void AStrayStoppedTimerIsRepairedEvenDuringAConversation(bool gui, bool modal, bool expected)
        => Assert.Equal(expected, PausePresentationRule.CanRepairStrayTimer(gui, modal));

    [Theory]
    [InlineData(true, 0, true, false, false, false, true)] // reported save/menu return
    [InlineData(false, 0, true, false, false, false, false)]
    [InlineData(true, 1, true, false, false, false, false)] // manual or peer mask
    [InlineData(true, 4, true, false, false, false, false)] // script mask
    [InlineData(true, 0, false, false, false, false, false)]
    [InlineData(true, 0, true, true, false, false, false)] // real GUI pause to adopt
    [InlineData(true, 0, true, false, true, false, false)]
    [InlineData(true, 0, true, false, false, true, false)]
    public void OrphanedPanelCleanupPreservesEveryRealPause(bool coop, int mask, bool intent,
        bool belief, bool clientTimer, bool serverTimer, bool expected)
        => Assert.Equal(expected, PausePresentationRule.ClearOrphanedPanel(coop, mask, intent,
            belief, clientTimer, serverTimer));
}
