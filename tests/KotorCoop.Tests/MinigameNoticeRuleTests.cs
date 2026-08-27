using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind item 8 task 3's "&lt;name&gt; is in a minigame — waiting" HUD
/// line: while a remote slot is Active, the notice mirrors <c>RemoteView.InMinigame</c>
/// exactly; while it is not Active, the notice PRESERVES whatever it last showed rather than
/// trusting a stale read — see <see cref="MinigameNoticeRule"/>'s own doc comment for why
/// that preserve behaviour, not a guess, is the only safe thing to do here.
/// </summary>
public class MinigameNoticeRuleTests
{
    [Fact]
    public void MirrorsTheFlagExactlyWhileActive()
    {
        Assert.True(MinigameNoticeRule.NextWaiting(wasWaiting: false, active: true, peerInMinigame: true));
        Assert.False(MinigameNoticeRule.NextWaiting(wasWaiting: true, active: true, peerInMinigame: false));
    }

    [Fact]
    public void ActiveWithTheFlagOffClearsAPreviouslyShowingNotice()
    {
        Assert.False(MinigameNoticeRule.NextWaiting(wasWaiting: true, active: true, peerInMinigame: false));
    }

    [Fact]
    public void InactivePreservesWhateverTheNoticeLastShowed()
    {
        // The block leaves Flags untouched for an excluded slot (KotorGameBridge.WriteToPlugin
        // only zeroes Active), so an inactive read of InMinigame is not trustworthy either way
        // — the rule must not treat "inactive" as "flag off" or as "flag on".
        Assert.True(MinigameNoticeRule.NextWaiting(wasWaiting: true, active: false, peerInMinigame: false));
        Assert.True(MinigameNoticeRule.NextWaiting(wasWaiting: true, active: false, peerInMinigame: true));
        Assert.False(MinigameNoticeRule.NextWaiting(wasWaiting: false, active: false, peerInMinigame: false));
        Assert.False(MinigameNoticeRule.NextWaiting(wasWaiting: false, active: false, peerInMinigame: true));
    }

    [Fact]
    public void ANewlyActiveSlotWithNoPriorHistoryStartsFromTheFlagAlone()
    {
        // The first frame a fresh peer occupies a slot: wasWaiting is whatever the array's
        // default (false) left it at, and the flag decides from there exactly as any other
        // active frame would.
        Assert.False(MinigameNoticeRule.NextWaiting(wasWaiting: false, active: true, peerInMinigame: false));
        Assert.True(MinigameNoticeRule.NextWaiting(wasWaiting: false, active: true, peerInMinigame: true));
    }
}
