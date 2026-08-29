using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="MinigameLockRule"/> — "one player races, everybody else waits"
/// (docs/research/minigames.md §24).
///
/// The failure mode this feature actually has is a lock that never RELEASES: a player frozen
/// for the rest of the session with no way out, which is strictly worse than the bug the
/// feature fixes. So every release path gets its own test — the owner finishing, the owner
/// disconnecting, the timeout, and the local player turning out to be in a minigame too.
/// </summary>
public class MinigameLockRuleTests
{
    private static readonly MinigameLockRule Rule = new(Enabled: true, TimeoutMs: 600_000);

    private static MinigameLockPeer Racing(uint id, string name = "Malak") => new(id, name, true);

    private static MinigameLockPeer Idle(uint id, string name = "Malak") => new(id, name, false);

    [Fact]
    public void APeerEnteringAMinigameLocksThisPlayerAndNamesThem()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Racing(7, "Malak")], currentOwnerId: 0, lockStartedAtMs: 0, nowMs: 1000);

        Assert.True(decision.Locked);
        Assert.Equal(7u, decision.OwnerId);
        Assert.Equal("Malak", decision.OwnerName);
        Assert.Contains("Malak", decision.Reason);
    }

    [Fact]
    public void NobodyRacingIsNoLock()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Idle(7)], currentOwnerId: 0, lockStartedAtMs: 0, nowMs: 1000);

        Assert.False(decision.Locked);
        Assert.Equal(0u, decision.OwnerId);
    }

    [Fact]
    public void TheRacerIsNeverLockedByTheirOwnRace()
    {
        // The local player is the one in the minigame; a peer's flag (however it got set)
        // must not hold them.
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: true, [Racing(7)], currentOwnerId: 0, lockStartedAtMs: 0, nowMs: 1000);

        Assert.False(decision.Locked);
    }

    [Fact]
    public void AnExistingLockReleasesWhenThisPlayerEntersAMinigameToo()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: true, [Racing(7)], currentOwnerId: 7, lockStartedAtMs: 0, nowMs: 1000);

        Assert.False(decision.Locked);
        Assert.Contains("in a minigame too", decision.Reason);
    }

    [Fact]
    public void TheLockHoldsWhileTheOwnerIsStillRacing()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Racing(7)], currentOwnerId: 7, lockStartedAtMs: 1000, nowMs: 60_000);

        Assert.True(decision.Locked);
        Assert.Equal(7u, decision.OwnerId);

        // Steady state says nothing: the caller logs only on the edges.
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Fact]
    public void TheLockReleasesWhenTheOwnersFlagClears()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Idle(7, "Malak")], currentOwnerId: 7, lockStartedAtMs: 1000, nowMs: 60_000);

        Assert.False(decision.Locked);
        Assert.Equal(0u, decision.OwnerId);
        Assert.Contains("finished", decision.Reason);
    }

    [Fact]
    public void TheLockReleasesWhenTheOwnerLeavesTheSession()
    {
        // The racer's agent died or they disconnected mid-race: their flag is not "false",
        // they are simply gone from the roster, which an owner-flag check alone never sees.
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Racing(9, "Bastila")], currentOwnerId: 7,
            lockStartedAtMs: 1000, nowMs: 60_000);

        Assert.False(decision.Locked);
        Assert.Contains("left the session", decision.Reason);
    }

    [Fact]
    public void TheLockReleasesOnTimeoutEvenThoughTheOwnerStillClaimsToBeRacing()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Racing(7)], currentOwnerId: 7,
            lockStartedAtMs: 1000, nowMs: 1000 + 600_000);

        Assert.False(decision.Locked);
        Assert.Contains("600s", decision.Reason);
    }

    [Fact]
    public void ATimeoutOfZeroNeverFires()
    {
        MinigameLockRule noTimeout = new(Enabled: true, TimeoutMs: 0);

        MinigameLockDecision decision = noTimeout.Decide(
            localInMinigame: false, [Racing(7)], currentOwnerId: 7,
            lockStartedAtMs: 0, nowMs: 86_400_000);

        Assert.True(decision.Locked);
    }

    [Fact]
    public void OwnershipIsStickyRatherThanRecomputedPerTick()
    {
        // Peer 9 also enters a minigame while peer 7 already owns the lock. The notice must
        // keep naming 7 — a name flickering between two racers reads as a bug.
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false,
            [Racing(9, "Bastila"), Racing(7, "Malak")],
            currentOwnerId: 7,
            lockStartedAtMs: 1000,
            nowMs: 2000);

        Assert.True(decision.Locked);
        Assert.Equal(7u, decision.OwnerId);
        Assert.Equal("Malak", decision.OwnerName);
    }

    [Fact]
    public void SimultaneousStartersBreakTheTieOnTheLowestPeerId()
    {
        // Roster ORDER is not stable across ticks; peer ids are. Deliberately listing the
        // higher id first so a "first in the list" implementation fails this.
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false,
            [Racing(9, "Bastila"), Racing(7, "Malak")],
            currentOwnerId: 0,
            lockStartedAtMs: 0,
            nowMs: 1000);

        Assert.Equal(7u, decision.OwnerId);
    }

    [Fact]
    public void DisabledNeverLocks()
    {
        MinigameLockRule off = new(Enabled: false, TimeoutMs: 600_000);

        Assert.False(off.Decide(false, [Racing(7)], 0, 0, 1000).Locked);
        Assert.False(off.Decide(false, [Racing(7)], 7, 0, 1000).Locked);
    }

    [Fact]
    public void AnUnnamedPeerStillProducesAReadableReason()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [Racing(7, string.Empty)], currentOwnerId: 0,
            lockStartedAtMs: 0, nowMs: 1000);

        Assert.True(decision.Locked);
        Assert.StartsWith("A peer", decision.Reason);
    }

    [Fact]
    public void AnEmptyRosterReleasesAnExistingLock()
    {
        MinigameLockDecision decision = Rule.Decide(
            localInMinigame: false, [], currentOwnerId: 7, lockStartedAtMs: 0, nowMs: 1000);

        Assert.False(decision.Locked);
        Assert.Contains("left the session", decision.Reason);
    }
}
