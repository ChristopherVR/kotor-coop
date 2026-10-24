using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The plugin-side deferral state machine (docs/roadmap-seamless.md Tier 1 item 4): click -&gt;
/// pending -&gt; grant/deny/timeout -&gt; re-issue/refuse, exercised directly rather than only
/// through the live rig — see docs/CONTRIBUTING.md's "plugin compile-include trick".
/// </summary>
public class ContainerLockArbiterTests
{
    private const uint ContainerId = 0x131;
    private const uint RequestSerial = 42;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OnlyAPositiveGrantInvokesTheNativeResumeBoundary(bool answered, bool granted)
    {
        ContainerLockPollOutcome expected = !answered ? ContainerLockPollOutcome.TimedOut
            : granted ? ContainerLockPollOutcome.Granted : ContainerLockPollOutcome.Denied;
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, 1000);
        List<uint> resumed = [];
        Assert.Equal(expected, arbiter.PollAndResume(1000 + ContainerLockArbiter.TimeoutMs,
            answered, granted, resumed.Add));
        Assert.Equal(expected == ContainerLockPollOutcome.Granted ? new[] { ContainerId } : [], resumed);
        arbiter.PollAndResume(3000, true, true, resumed.Add);
        Assert.Equal(expected == ContainerLockPollOutcome.Granted ? 1 : 0, resumed.Count);
    }

    [Fact]
    public void CancelledRequestAndItsLateGrantNeverResume()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, 1000);
        arbiter.Reset();
        int calls = 0;
        arbiter.PollAndResume(1001, true, true, _ => calls++);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void StartsIdle()
    {
        ContainerLockArbiter arbiter = new();

        Assert.True(arbiter.IsIdle);
    }

    [Fact]
    public void BeginRequestMovesToPending()
    {
        ContainerLockArbiter arbiter = new();

        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        Assert.False(arbiter.IsIdle);
        Assert.Equal(ContainerId, arbiter.PendingTargetId);
        Assert.Equal(RequestSerial, arbiter.PendingSerial);
    }

    [Fact]
    public void BeginRequestRefusesAZeroTargetId()
    {
        ContainerLockArbiter arbiter = new();

        arbiter.BeginRequest(targetId: 0, RequestSerial, nowMs: 1000);

        Assert.True(arbiter.IsIdle);
    }

    [Fact]
    public void BeginRequestRefusesAZeroSerial()
    {
        // 0 is reserved wire-wide for "no request pending" — see
        // InteractionClaimPacket.RequestSerial's own remarks.
        ContainerLockArbiter arbiter = new();

        arbiter.BeginRequest(ContainerId, requestSerial: 0, nowMs: 1000);

        Assert.True(arbiter.IsIdle);
    }

    [Fact]
    public void PollWhileIdleAlwaysWaits()
    {
        ContainerLockArbiter arbiter = new();

        Assert.Equal(ContainerLockPollOutcome.Waiting, arbiter.Poll(1000, responseArrived: true, granted: true));
    }

    [Fact]
    public void PollWithNoResponseYetKeepsWaiting()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        Assert.Equal(
            ContainerLockPollOutcome.Waiting,
            arbiter.Poll(1000 + ContainerLockArbiter.TimeoutMs - 1, responseArrived: false, granted: false));
        Assert.False(arbiter.IsIdle);
    }

    [Fact]
    public void AGrantedResponseTransitionsToGrantedAndReturnsToIdle()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        ContainerLockPollOutcome outcome = arbiter.Poll(1050, responseArrived: true, granted: true);

        Assert.Equal(ContainerLockPollOutcome.Granted, outcome);
        Assert.True(arbiter.IsIdle);
    }

    [Fact]
    public void ADeniedResponseTransitionsToDeniedAndReturnsToIdle()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        ContainerLockPollOutcome outcome = arbiter.Poll(1050, responseArrived: true, granted: false);

        Assert.Equal(ContainerLockPollOutcome.Denied, outcome);
        Assert.True(arbiter.IsIdle);
    }

    /// <summary>
    /// The fail-safe default this whole feature ships behind: a lost request or a lost
    /// response must not leave a container permanently unusable, so a request that never hears
    /// back within the deadline falls back to "proceed as before this feature existed" rather
    /// than refusing.
    /// </summary>
    [Fact]
    public void NoAnswerBeforeTheDeadlineTimesOutAndReturnsToIdle()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        ContainerLockPollOutcome outcome = arbiter.Poll(
            1000 + ContainerLockArbiter.TimeoutMs, responseArrived: false, granted: false);

        Assert.Equal(ContainerLockPollOutcome.TimedOut, outcome);
        Assert.True(arbiter.IsIdle);
    }

    [Fact]
    public void OnceIdleAgainANewRequestCanStart()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);
        arbiter.Poll(1000 + ContainerLockArbiter.TimeoutMs, responseArrived: false, granted: false);
        Assert.True(arbiter.IsIdle);

        arbiter.BeginRequest(0x999, RequestSerial + 1, nowMs: 2000);

        Assert.False(arbiter.IsIdle);
        Assert.Equal(0x999u, arbiter.PendingTargetId);
        Assert.Equal(RequestSerial + 1, arbiter.PendingSerial);
    }

    [Fact]
    public void ResetDropsAnInFlightRequestWithoutActingOnIt()
    {
        // A module change reassigns every object id — see the class's own remarks.
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        arbiter.Reset();

        Assert.True(arbiter.IsIdle);
        Assert.Equal(
            ContainerLockPollOutcome.Waiting,
            arbiter.Poll(1000, responseArrived: true, granted: true));
    }

    /// <summary>A response for a request that never started (already idle, or answering some
    /// earlier request) must never be misread as answering the current one —
    /// <c>ContainerLockArbiter.Poll</c> only ever consults its OWN pending serial, which the
    /// caller is responsible for matching
    /// against the session's answer before calling in. This test documents that Poll itself has
    /// no serial-matching logic of its own to get wrong: it trusts <c>responseArrived</c>
    /// completely, which is exactly why the caller must gate it.</summary>
    [Fact]
    public void PollTrustsTheCallersOwnSerialMatch()
    {
        ContainerLockArbiter arbiter = new();
        arbiter.BeginRequest(ContainerId, RequestSerial, nowMs: 1000);

        // The caller correctly determined the arriving response does NOT match PendingSerial
        // and passes responseArrived: false — Poll must keep waiting, not guess.
        Assert.Equal(
            ContainerLockPollOutcome.Waiting,
            arbiter.Poll(1050, responseArrived: false, granted: true));
        Assert.False(arbiter.IsIdle);
    }
}
