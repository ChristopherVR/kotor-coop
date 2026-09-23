using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PersistentSaveMismatchRule"/> — the level-driven fallback trigger for
/// <c>client_load_rejoin</c>, built for the round 2026-08-24 live finding that a debug-cheat
/// module warp produces a save-identity mismatch with no EDGE on either signal
/// <see cref="RejoinTrigger.OwnLoad"/>/<see cref="RejoinTrigger.HostReload"/> watch, so the
/// "rejoin to adopt" message's own corrective action never actually ran. A compatibility
/// mismatch alone is not proof of a loaded-world change: ordinary saves can diverge while both
/// players remain in the same in-memory world. Only a correlated load edge authorizes the
/// fallback, including a digest that arrives during the classifier's sampling window.
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
    public void OrdinarySaveMismatchNeverTriggersAdoption()
    {
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: true, rejoinInFlight: false, nowMs: 1000);

        Assert.False(shouldTrigger);
        Assert.True(next.WasMismatched);
        Assert.Null(next.LastAttemptAtMs);
        Assert.False(next.LoadAuthorizedEpisode);
    }

    [Fact]
    public void LoadEdgeAuthorizesExistingMismatchImmediately()
    {
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1000, loadEdge: true);

        Assert.True(shouldTrigger);
        Assert.True(next.LoadAuthorizedEpisode);
        Assert.Equal(1000, next.LastAttemptAtMs);
    }

    [Fact]
    public void LoadEdgeBeforeDigestMismatchAuthorizesWithinSamplingWindow()
    {
        (_, MismatchRejoinWatchState edgeSeen) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: false, rejoinInFlight: false,
            nowMs: 1000, loadEdge: true);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            edgeSeen, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1000 + PersistentSaveMismatchRule.LoadCorrelationWindowMs);

        Assert.True(shouldTrigger);
        Assert.Equal(1, next.Attempts);
        Assert.True(next.LoadAuthorizedEpisode);
    }

    [Fact]
    public void LoadEdgeExpiresBeforeLaterOrdinaryMismatch()
    {
        (_, MismatchRejoinWatchState edgeSeen) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: false, rejoinInFlight: false,
            nowMs: 1000, loadEdge: true);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            edgeSeen, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1001 + PersistentSaveMismatchRule.LoadCorrelationWindowMs);

        Assert.False(shouldTrigger);
        Assert.False(next.LoadAuthorizedEpisode);
    }

    [Fact]
    public void HealedAuthorizedEpisodeCannotReuseItsEdgeForANewOrdinarySave()
    {
        (_, MismatchRejoinWatchState authorized) = PersistentSaveMismatchRule.Observe(
            MismatchRejoinWatchState.Initial, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1000, loadEdge: true);

        (_, MismatchRejoinWatchState healed) = PersistentSaveMismatchRule.Observe(
            authorized, mismatchedNow: false, rejoinInFlight: false, nowMs: 1100);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            healed, mismatchedNow: true, rejoinInFlight: false, nowMs: 1200);

        Assert.False(shouldTrigger);
        Assert.False(next.LoadAuthorizedEpisode);
        Assert.Null(next.LastAttemptAtMs);
    }

    [Fact]
    public void AMismatchAlreadyInFlightIsNeverDuplicated()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000,
            Attempts: 1, LoadAuthorizedEpisode: true);

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
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000,
            Attempts: 1, LoadAuthorizedEpisode: true);

        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            afterFirstEdge, mismatchedNow: true, rejoinInFlight: false,
            nowMs: 1000 + PersistentSaveMismatchRule.RetryIntervalMs - 1);

        Assert.False(shouldTrigger);
        Assert.Equal(1000, next.LastAttemptAtMs);
    }

    [Fact]
    public void AStillMismatchedConditionRetriesOnceTheCooldownElapses()
    {
        MismatchRejoinWatchState afterFirstEdge = new(WasMismatched: true, LastAttemptAtMs: 1000,
            Attempts: 1, LoadAuthorizedEpisode: true);
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
            new MismatchRejoinWatchState(WasMismatched: true, LastAttemptAtMs: 1000,
                Attempts: 1, LoadAuthorizedEpisode: true),
            mismatchedNow: false, rejoinInFlight: false, nowMs: 1100);

        // A brand new mismatch, well within what would have been the old cooldown window,
        // must still be treated as a fresh edge rather than throttled by the stale timer.
        (bool shouldTrigger, MismatchRejoinWatchState next) = PersistentSaveMismatchRule.Observe(
            healed, mismatchedNow: true, rejoinInFlight: false, nowMs: 1200, loadEdge: true);

        Assert.True(shouldTrigger);
        Assert.Equal(1200, next.LastAttemptAtMs);
    }
}
