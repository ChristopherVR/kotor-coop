using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins the pure timing decisions behind item 6's liveness gate: when the on-screen
/// warning fires and remote-peer writes freeze (5 s), when AgentLauncher will consider a
/// relaunch (15 s), and the once-a-minute relaunch cooldown. No SharedBlockView, no
/// pointers — see KotorCoop.Tests.csproj's plugin-file include list.
/// </summary>
public class AgentLivenessTests
{
    [Fact]
    public void ZeroHeartbeatIsNeverLive()
    {
        // The field's zero-initialised state: no agent has ever pumped this block, so
        // there is nothing to warn about and nothing to freeze.
        Assert.Equal(AgentLivenessState.NeverLive, AgentLiveness.Evaluate(0, now: 999_999));
    }

    [Fact]
    public void RecentHeartbeatIsLive()
    {
        Assert.Equal(AgentLivenessState.Live, AgentLiveness.Evaluate(lastBeat: 10_000, now: 10_100));
    }

    [Fact]
    public void ExactlyAtTheStaleThresholdIsStaleNotLive()
    {
        long lastBeat = 10_000;
        Assert.Equal(
            AgentLivenessState.Stale,
            AgentLiveness.Evaluate(lastBeat, now: lastBeat + AgentLiveness.StaleAfterMs));
    }

    [Fact]
    public void JustUnderTheStaleThresholdIsStillLive()
    {
        long lastBeat = 10_000;
        Assert.Equal(
            AgentLivenessState.Live,
            AgentLiveness.Evaluate(lastBeat, now: lastBeat + AgentLiveness.StaleAfterMs - 1));
    }

    [Fact]
    public void ExactlyAtTheDeadThresholdIsDeadNotStale()
    {
        long lastBeat = 10_000;
        Assert.Equal(
            AgentLivenessState.Dead,
            AgentLiveness.Evaluate(lastBeat, now: lastBeat + AgentLiveness.DeadAfterMs));
    }

    [Fact]
    public void JustUnderTheDeadThresholdIsStillStale()
    {
        long lastBeat = 10_000;
        Assert.Equal(
            AgentLivenessState.Stale,
            AgentLiveness.Evaluate(lastBeat, now: lastBeat + AgentLiveness.DeadAfterMs - 1));
    }

    [Fact]
    public void RecoveryReadsLiveAgainOnceTheHeartbeatResumes()
    {
        // A stale-then-resumed agent: the caller only ever passes the LATEST heartbeat
        // value, so recovery needs no special handling — it falls straight out of the age
        // computation the moment a fresh stamp arrives.
        Assert.Equal(AgentLivenessState.Live, AgentLiveness.Evaluate(lastBeat: 50_000, now: 50_050));
    }

    [Fact]
    public void ShouldRelaunchRequiresDeadState()
    {
        long lastBeat = 10_000;

        // Stale, not yet Dead: too early for a relaunch, however long the launcher's own
        // cooldown has been clear.
        Assert.False(AgentLiveness.ShouldRelaunch(
            lastBeat, now: lastBeat + AgentLiveness.StaleAfterMs,
            startedByLauncher: true, lastAttemptAtMs: 0));

        Assert.True(AgentLiveness.ShouldRelaunch(
            lastBeat, now: lastBeat + AgentLiveness.DeadAfterMs,
            startedByLauncher: true, lastAttemptAtMs: 0));
    }

    [Fact]
    public void ShouldRelaunchNeverFiresForAnAgentThisLauncherDidNotStart()
    {
        // The player running the agent by hand (or coop.ini auto_start_agent=0): never
        // taken over, no matter how stale the heartbeat.
        Assert.False(AgentLiveness.ShouldRelaunch(
            lastBeat: 0, now: 1_000_000, startedByLauncher: false, lastAttemptAtMs: 0));
    }

    [Fact]
    public void ShouldRelaunchRespectsTheCooldown()
    {
        long lastBeat = 10_000;
        long now = lastBeat + AgentLiveness.DeadAfterMs + 5_000;
        long lastAttempt = now - AgentLiveness.RelaunchCooldownMs + 1; // just inside the cooldown

        Assert.False(AgentLiveness.ShouldRelaunch(lastBeat, now, startedByLauncher: true, lastAttempt));

        long attemptLongEnoughAgo = now - AgentLiveness.RelaunchCooldownMs;
        Assert.True(AgentLiveness.ShouldRelaunch(lastBeat, now, startedByLauncher: true, attemptLongEnoughAgo));
    }

    [Fact]
    public void ShouldRelaunchTreatsZeroLastAttemptAsNeverAttempted()
    {
        // 0 is not "the epoch", it is the sentinel for "no relaunch has been tried yet" —
        // must not be mistaken for a relaunch that just happened at agent-clock zero.
        long lastBeat = 10_000;
        Assert.True(AgentLiveness.ShouldRelaunch(
            lastBeat, now: lastBeat + AgentLiveness.DeadAfterMs,
            startedByLauncher: true, lastAttemptAtMs: 0));
    }
}
