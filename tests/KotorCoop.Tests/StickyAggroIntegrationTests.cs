using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// THREAT_RETARGET stage 1+2 (docs/research/peer-damage-ledger.md §9, sticky-aggro plan): with
/// the HOST's own PC/party damage now banked into <see cref="ThreatTable"/> alongside a remote
/// peer's (<c>PhantomHitHook.WatchThreatAttackers</c>/<c>DrainHostThreat</c>,
/// <c>PeerDamageController.BankThreat</c>/<c>ReassertThreat</c>), the current target's own
/// threat is no longer always 0 — so <see cref="ThreatRetargetRule.Decide"/>'s margin check is
/// a genuine comparison between two real contributions, not "any peer damage at all vs zero".
///
/// These tests drive <see cref="ThreatTable"/> and <see cref="ThreatRetargetRule"/> together —
/// the exact two pure pieces <c>PeerDamageController</c>'s engine-touching code wires up — to
/// pin the end-to-end DECISION shape that the engine-dependent controller itself cannot be unit
/// tested through (see <c>ThreatRetargetRuleTests.TheControllerTakesItsOptionsFromDefaultNotFromNew</c>
/// for why the controller's own source is scanned instead of exercised directly).
///
/// The host's own attacker id used throughout is 0x7FFFFFFF, the engine's real, permanent
/// self-id for whichever creature a client currently drives (see
/// <c>NewCharacterCaptureController</c>'s own remarks on <c>Odyssey.GetPlayerObjectId</c>) —
/// the SAME value <c>Odyssey.GetCurrentAttackTargetId</c> reads back out of a creature's live
/// round when that creature is fighting the host's PC, which is why banking the host's own
/// damage under this exact id is what makes <c>currentTargetThreat</c> stop reading 0.
/// </summary>
public sealed class StickyAggroIntegrationTests
{
    private const uint HostAttackerId = 0x7FFFFFFF;
    private const uint PeerAttackerId = 0x139;
    private const uint CreatureId = 0x141;

    private static ThreatRetargetDecision DecideFor(
        ThreatTable table, uint currentTargetId, double nowSeconds) =>
        Decide(table, currentTargetId, nowSeconds, out _, out _);

    private static ThreatRetargetDecision Decide(
        ThreatTable table, uint currentTargetId, double nowSeconds, out uint topAttackerId, out double topThreat)
    {
        table.TryGetTopAttacker(CreatureId, nowSeconds, out topAttackerId, out topThreat);
        double currentTargetThreat = table.GetThreat(CreatureId, currentTargetId, nowSeconds);
        double sinceLast = table.SecondsSinceLastRetarget(CreatureId, nowSeconds);

        return ThreatRetargetRule.Decide(
            currentTargetId, topAttackerId, topThreat, currentTargetThreat, sinceLast, ThreatRetargetOptions.Default);
    }

    [Fact]
    public void BeforeTheHostsOwnDamageIsTrackedAnyPeerHitAboveTheFloorWouldHaveRetargeted()
    {
        // The bug §9.2/§11 documented: with the host's own hits never banked, the current
        // target's own threat reads 0 regardless of how hard the host has actually been
        // hitting — so the margin check (top >= current * 1.5) is trivially satisfied by any
        // peer damage clearing the floor alone. Pin the OLD (still legal, still reachable if a
        // creature's current target is never itself a tracked attacker) shape here so the
        // contrast with the fixed tests below is explicit, not just asserted in prose.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, PeerAttackerId, 16, nowSeconds: 0);

        // currentTargetId names the host, but the host's own damage was never banked (this
        // test never calls AddThreat for HostAttackerId) — exactly what every pre-fix pass did.
        ThreatRetargetDecision decision = DecideFor(table, currentTargetId: HostAttackerId, nowSeconds: 0);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void OnceTheHostsOwnDamageIsTrackedItTakesAGenuineMarginToRetarget()
    {
        // Same shape as above, but now the host's own PC has genuinely been fighting too
        // (BankThreat's own job under the fix). The peer (25) is still the top attacker, but
        // 25 does not clear 1.5x the host's own 20 (30) — a real margin instead of a
        // rubber-stamp against an untracked 0.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 20, nowSeconds: 0);
        table.AddThreat(CreatureId, PeerAttackerId, 25, nowSeconds: 0);

        ThreatRetargetDecision decision = DecideFor(table, currentTargetId: HostAttackerId, nowSeconds: 0);

        Assert.Equal(ThreatRetargetDecision.BelowMargin, decision);
    }

    [Fact]
    public void ThePeerClearlyOutDamagingTheHostStillRetargets()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 10, nowSeconds: 0);
        table.AddThreat(CreatureId, PeerAttackerId, 40, nowSeconds: 0); // 40 >= 10*1.5 and >= floor

        ThreatRetargetDecision decision = DecideFor(table, currentTargetId: HostAttackerId, nowSeconds: 0);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void EqualContributionsAreATieAndNeitherSideBumpsTheOther()
    {
        // Host and peer have dealt the identical amount: 1.0x never clears the 1.5x margin in
        // either direction, so a tie never causes a switch — whichever id TryGetTopAttacker's
        // own (unspecified) tie-break happens to name, the decision is either AlreadyTarget
        // (the tie-break landed on the incumbent) or BelowMargin (it landed on the other side);
        // never Retarget either way. That "never Retarget on a tie" is the actual hysteresis
        // property this test pins — which exact non-Retarget outcome is an implementation
        // detail of the tie-break, not something this rule promises.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 30, nowSeconds: 0);
        table.AddThreat(CreatureId, PeerAttackerId, 30, nowSeconds: 0);

        Assert.NotEqual(ThreatRetargetDecision.Retarget, DecideFor(table, HostAttackerId, nowSeconds: 0));
        Assert.NotEqual(ThreatRetargetDecision.Retarget, DecideFor(table, PeerAttackerId, nowSeconds: 0));
    }

    [Fact]
    public void ALeaderSwitchThatTheEngineFlipsBackIsCaughtOnTheVeryNextEligiblePass()
    {
        // The exact §11 shape: the order fires, the engine's own AI pulls the creature back to
        // the host on the host's next landed hit (simulated here as nothing more than
        // `currentTargetId` reverting to the host — this test never re-calls AddThreat for the
        // host, i.e. the host dealt NO new damage that pass), and the fix's whole point (stage
        // 2, ReassertThreat) is that the very next pass the cooldown allows re-issues the order
        // WITHOUT needing a fresh peer hit to trigger the look.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 20, nowSeconds: 0);
        table.AddThreat(CreatureId, PeerAttackerId, 40, nowSeconds: 0);

        // Pass 1: peer clearly ahead, order fires; the controller then calls MarkRetargeted.
        Assert.Equal(ThreatRetargetDecision.Retarget, DecideFor(table, HostAttackerId, nowSeconds: 0));
        table.MarkRetargeted(CreatureId, nowSeconds: 0);

        // The engine flips it back to the host mid-cooldown (no new damage banked either
        // side): still refused, correctly — not because the peer stopped leading, but because
        // the cooldown has not cleared yet.
        Assert.Equal(ThreatRetargetDecision.OnCooldown, DecideFor(table, HostAttackerId, nowSeconds: 2));

        // Cooldown clears (4s) with the peer's threat only having decayed a little and still
        // comfortably ahead: ReassertThreat's whole reason to exist is that this check runs on
        // schedule, not only because a fresh peer hit happened to land exactly now.
        ThreatRetargetDecision reassert = DecideFor(table, HostAttackerId, nowSeconds: 4);
        Assert.Equal(ThreatRetargetDecision.Retarget, reassert);
    }

    [Fact]
    public void TheFormerLeaderGenuinelyRetakingTheLeadFlipsTheDecisionBothWays()
    {
        // A real back-and-forth fight, not just a stale engine flip: the host keeps landing
        // hits and eventually reclaims a genuine margin over the peer's decaying threat.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, PeerAttackerId, 30, nowSeconds: 0);
        Assert.Equal(ThreatRetargetDecision.Retarget, DecideFor(table, HostAttackerId, nowSeconds: 0));
        table.MarkRetargeted(CreatureId, nowSeconds: 0);

        // 4s later (cooldown clear) the peer's 30 has decayed to 30*(1-4/10)=18; the host lands
        // a big hit of its own, clearing 1.5x over the peer's remaining threat.
        table.AddThreat(CreatureId, HostAttackerId, 40, nowSeconds: 4);
        ThreatRetargetDecision backToHost = DecideFor(table, currentTargetId: PeerAttackerId, nowSeconds: 4);
        Assert.Equal(ThreatRetargetDecision.Retarget, backToHost);
    }

    [Fact]
    public void AttackerGoingAwayLetsTheOtherSideWinOnDecayAlone()
    {
        // "Going away" (disconnects, or simply stops swinging) is not a distinct rule — it is
        // what threat decay already models: an attacker who deals no further damage loses
        // priority over the full 10s window with no special-casing required.
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 100, nowSeconds: 0);

        // No further host damage from here — the host has "gone away". The peer starts fresh
        // at t=8: host's 100 has decayed to 100*(1-8/10)=20, comfortably below the peer's 40.
        table.AddThreat(CreatureId, PeerAttackerId, 40, nowSeconds: 8);

        ThreatRetargetDecision decision = DecideFor(table, currentTargetId: HostAttackerId, nowSeconds: 8);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void TopAttackerIsAlwaysWhicheverSideCurrentlyLeadsRegardlessOfWhichWasBankedFirst()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(CreatureId, HostAttackerId, 12, nowSeconds: 0);
        table.AddThreat(CreatureId, PeerAttackerId, 50, nowSeconds: 0);

        Assert.True(table.TryGetTopAttacker(CreatureId, nowSeconds: 0, out uint topId, out double topThreat));
        Assert.Equal(PeerAttackerId, topId);
        Assert.Equal(50, topThreat);
    }
}
