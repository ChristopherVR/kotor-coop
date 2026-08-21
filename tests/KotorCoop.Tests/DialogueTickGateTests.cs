using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind <c>coop.ini dialogue_fast_join</c>: whether the agent's dialogue
/// channel does its work THIS poll tick. See docs/research/dialogue-join-latency.md for the
/// measured timeline this replaces and <see cref="CoopAgent.PumpDialogue"/> for where it is
/// wired in. Two rules, each its own past-defect-shaped failure mode if it were missing:
/// a CHANGE fires on the very next tick (event-driven), and an UNCHANGED read is not
/// re-published every tick just because the loop ran again (dedup), while a heartbeat still
/// forces a periodic resend so a late joiner or a dropped packet converges in bounded time.
/// </summary>
public sealed class DialogueTickGateTests
{
    /// <summary>The very first call has nothing to compare against, so it must fire — a gate
    /// that started closed would silently swallow the first thing it was ever asked to report,
    /// which is exactly the state a fresh follow (or a fresh conversation) starts in.</summary>
    [Fact]
    public void TheFirstCallAlwaysFires()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);

        Assert.True(gate.ShouldPump(nowMs: 1000, fingerprint: 42));
    }

    /// <summary>The event-driven half: a changed fingerprint fires immediately, however little
    /// time has passed — this is the entire latency win over the old fixed 150 ms cadence.</summary>
    [Fact]
    public void AChangedFingerprintFiresImmediatelyEvenWellInsideTheHeartbeat()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 1);

        Assert.True(gate.ShouldPump(nowMs: 1001, fingerprint: 2));
    }

    /// <summary>The dedup half: the same fingerprint, well inside the heartbeat, must not
    /// re-fire — "every tick" must not mean "resend the identical payload every tick".</summary>
    [Fact]
    public void AnUnchangedFingerprintDoesNotFireBeforeTheHeartbeat()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 7);

        Assert.False(gate.ShouldPump(nowMs: 1001, fingerprint: 7));
        Assert.False(gate.ShouldPump(nowMs: 1100, fingerprint: 7));
    }

    /// <summary>The heartbeat still forces a resend once the interval elapses, unchanged or
    /// not — a late-joining peer or a dropped packet must still converge in bounded time,
    /// exactly the argument <see cref="DialogueFollowTimings"/> makes one layer up.</summary>
    [Fact]
    public void AnUnchangedFingerprintFiresAgainOnceTheHeartbeatElapses()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 7);

        Assert.False(gate.ShouldPump(nowMs: 1100, fingerprint: 7));
        Assert.True(gate.ShouldPump(nowMs: 1150, fingerprint: 7));
    }

    /// <summary>The heartbeat clock restarts from the last time the gate actually fired, not
    /// from the last time it was merely asked — otherwise a caller polling faster than the
    /// heartbeat would fire on every call once the window opened, rather than once per window.</summary>
    [Fact]
    public void TheHeartbeatClockRestartsFromTheLastFireNotTheLastAsk()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 7); // fires (first call)

        Assert.True(gate.ShouldPump(nowMs: 1150, fingerprint: 7)); // heartbeat due -> fires, resets clock
        Assert.False(gate.ShouldPump(nowMs: 1151, fingerprint: 7)); // one ms later: not due again yet
        Assert.False(gate.ShouldPump(nowMs: 1299, fingerprint: 7));
        Assert.True(gate.ShouldPump(nowMs: 1300, fingerprint: 7));
    }

    /// <summary>A change resets the heartbeat clock too — the next resend after a real change
    /// is measured from that change, not from whatever unrelated earlier tick last fired.</summary>
    [Fact]
    public void AChangeAlsoRestartsTheHeartbeatClock()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 1);
        gate.ShouldPump(nowMs: 1140, fingerprint: 2); // a change, fires, resets the clock to 1140

        Assert.False(gate.ShouldPump(nowMs: 1200, fingerprint: 2)); // only 60 ms since the reset
        Assert.True(gate.ShouldPump(nowMs: 1290, fingerprint: 2)); // 150 ms since 1140
    }

    /// <summary>Reset makes the next call behave like the very first ever call — a fingerprint
    /// from a previous epoch (a module change, a different conversation entirely) must not be
    /// compared against.</summary>
    [Fact]
    public void ResetMakesTheNextCallBehaveLikeTheFirst()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 7);
        Assert.False(gate.ShouldPump(nowMs: 1001, fingerprint: 7));

        gate.Reset();

        // Same fingerprint as before the reset, called immediately after — this must still
        // fire, because the reset gate has nothing recorded to compare it against.
        Assert.True(gate.ShouldPump(nowMs: 1002, fingerprint: 7));
    }

    /// <summary>A rapid back-and-forth between two values is still every one of them a
    /// "change" from the gate's point of view — it has no notion of "already saw this value
    /// once before", only "differs from what was last acted on".</summary>
    [Fact]
    public void OscillatingBetweenTwoValuesFiresOnEveryFlip()
    {
        DialogueTickGate gate = new(heartbeatIntervalMs: 150);
        gate.ShouldPump(nowMs: 1000, fingerprint: 1);

        Assert.True(gate.ShouldPump(nowMs: 1001, fingerprint: 2));
        Assert.True(gate.ShouldPump(nowMs: 1002, fingerprint: 1));
        Assert.True(gate.ShouldPump(nowMs: 1003, fingerprint: 2));
    }
}
