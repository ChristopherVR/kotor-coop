using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PersistentSaveMismatchRule"/> — the level-driven fallback trigger for
/// <c>client_load_rejoin</c>, built for the round 2026-08-24 live finding that a debug-cheat
/// module warp produces a save-identity mismatch with no EDGE on either signal
/// <see cref="RejoinTrigger.OwnLoad"/>/<see cref="RejoinTrigger.HostReload"/> watch, so the
/// "rejoin to adopt" message's own corrective action never actually ran. Pins: a clean tick
/// never triggers, a fresh mismatch triggers immediately (edge), an in-flight rejoin is never
/// duplicated, a still-mismatched condition retries after the cooldown, and healing (or a fresh
/// mismatch after healing) resets the state cleanly.
/// </summary>
public sealed class PersistentSaveMismatchRuleTests
{
    [Fact]
    public void NoMismatchNeverTriggers()
    {
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: false, rejoinInFlight: false, nowMs: 1000);

        Assert.False(shouldTrigger);
        Assert.False(next.WasMismatched);
        Assert.Null(next.LastAttemptAtMs);
    }

    [Fact]
    public void AFreshMismatchTriggersImmediatelyAsAnEdge()
    {
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: true, rejoinInFlight: false, nowMs: 1000);

        Assert.True(shouldTrigger);
        Assert.True(next.WasMismatched);
        Assert.Equal(1000, next.LastAttemptAtMs);
    }

    [Fact]
    public void AMismatchAlreadyInFlightIsNeverDuplicated()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            afterFirstEdge, mismatchedNow: true, rejoinInFlight: true, nowMs: 1500);

        Assert.False(shouldTrigger);
        Assert.True(next.WasMismatched);
        // The in-flight attempt's own start time is preserved, not bumped by the swallowed call.
        Assert.Equal(1000, next.LastAttemptAtMs);
    }

    [Fact]
    public void AStillMismatchedConditionDoesNotRetryBeforeTheCooldownElapses()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            afterFirstEdge, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1000 + PersistentSaveMismatchRule.RetryIntervalMs - 1);

        Assert.False(shouldTrigger);
        Assert.Equal(1000, next.LastAttemptAtMs);
    }

    [Fact]
    public void AStillMismatchedConditionRetriesOnceTheCooldownElapses()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000);
        long retryAt = 1000 + PersistentSaveMismatchRule.RetryIntervalMs;

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            afterFirstEdge, mismatchedNow: true, rejoinInFlight: false, nowMs: retryAt);

        Assert.True(shouldTrigger);
        Assert.Equal(retryAt, next.LastAttemptAtMs);
    }

    [Fact]
    public void HealingResetsTheStateCompletely()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            afterFirstEdge, mismatchedNow: false, rejoinInFlight: false, nowMs: 1100);

        Assert.False(shouldTrigger);
        Assert.False(next.WasMismatched);
        Assert.Null(next.LastAttemptAtMs);
    }

    [Fact]
    public void AFreshMismatchAfterHealingTriggersAgainImmediately()
    {
        // Heal first.
        (_, MismatchRejoinWatchState healed) = PersistentSaveMismatchRule.Observe(
            new MismatchRejoinWatchState(WasMismatched: true, LastAttemptAtMs: 1000),
            mismatchedNow: false, rejoinInFlight: false, nowMs: 1100);

        // A brand new mismatch, well within what would have been the old cooldown window,
        // must still be treated as a fresh edge rather than throttled by the stale timer.
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            healed, mismatchedNow: true, rejoinInFlight: false, nowMs: 1200);

        Assert.True(shouldTrigger);
        Assert.Equal(1200, next.LastAttemptAtMs);
    }
}
