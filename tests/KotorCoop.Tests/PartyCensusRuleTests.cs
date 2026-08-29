using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Who the enemy census is allowed to publish, and what a mirror is allowed to build a body
/// for. The defect behind every case here is one shape: the census asked the party-FOLLOW
/// pointer (<c>CSWSCreature+0x4C0</c>) whether a creature was one of ours, and that pointer is
/// attached some time AFTER the body exists — so a companion crossing a module transition was
/// published as a hostile and materialised on the peer as a companion-shaped stand-in
/// (appearance 378 = Trask). See docs/research/name-plates.md, "the census leak".
/// </summary>
public class PartyCensusRuleTests
{
    private const uint TraskAppearance = 378;

    [Fact]
    public void TheRosterSentinelsAreNotIds()
    {
        // Both are values a slot holds when there is no body: 0 for "nothing written here",
        // OBJECT_INVALID for the engine's own "none". Matching a live creature against either
        // would exclude a real enemy that happens to be numbered 0 — or, worse, treat the
        // sentinel as a member and quietly widen the exclusion by one arbitrary id.
        Assert.False(PartyCensusRule.IsRosterId(0));
        Assert.False(PartyCensusRule.IsRosterId(PartyCensusRule.ObjectInvalid));
        Assert.True(PartyCensusRule.IsRosterId(0x127));
    }

    [Fact]
    public void ACompanionWhoseFollowPointerIsNotAttachedIsStillOurs()
    {
        // THE BUG, stated once. Trask, mid-transition, follow pointer null — the exact state
        // PartyFollowController and FollowPhaseReleaseRule exist because of. The old test said
        // "not a party member" and the host published him as an enemy.
        Assert.True(PartyCensusRule.IsOwnParty(
            0x127, isPlayer: false, followPointerAttached: false, rosterIds: new HashSet<uint> { 0x127 }));
    }

    [Fact]
    public void TheFollowPointerStillCountsOnItsOwn()
    {
        // It is never wrong when TRUE, only when false — so it stays, as a widening belt, for
        // any body running follow-AI that the roster read could not see (no server, no table).
        Assert.True(PartyCensusRule.IsOwnParty(
            0x50, isPlayer: false, followPointerAttached: true, rosterIds: []));
    }

    [Fact]
    public void ThePlayerIsAlwaysOurs()
    {
        Assert.True(PartyCensusRule.IsOwnParty(
            0x7FFFFFFF, isPlayer: true, followPointerAttached: false, rosterIds: null));
    }

    [Fact]
    public void AnOrdinaryHostileIsNotOurs()
    {
        // The whole point of the census: a Sith soldier standing next to the party is still an
        // enemy, and widening the exclusion until it swallows him would be the opposite bug.
        Assert.False(PartyCensusRule.IsOwnParty(
            0x50, isPlayer: false, followPointerAttached: false,
            rosterIds: new HashSet<uint> { 0x127, 0x128 }));
    }

    [Fact]
    public void ANullRosterChangesNothing()
    {
        // "Could not read the party table" must degrade to exactly the behaviour that shipped
        // before this rule — the is-player and follow-pointer tests standing alone — and never
        // to "exclude nobody" OR "exclude everybody".
        Assert.False(PartyCensusRule.IsOwnParty(
            0x127, isPlayer: false, followPointerAttached: false, rosterIds: null));
        Assert.True(PartyCensusRule.IsOwnParty(
            0x127, isPlayer: false, followPointerAttached: true, rosterIds: null));
    }

    [Fact]
    public void ASentinelPublishedAsAnEnemyIdIsNotMatchedAgainstTheRoster()
    {
        // A roster full of empty slots reads as a set of sentinels in a careless implementation;
        // IsRosterId is what stops the census matching a real creature against one.
        Assert.False(PartyCensusRule.IsOwnParty(
            PartyCensusRule.ObjectInvalid, isPlayer: false, followPointerAttached: false,
            rosterIds: new HashSet<uint> { PartyCensusRule.ObjectInvalid }));
    }

    [Fact]
    public void AStandInIsRefusedForOneOfOurOwnRosterBodies()
    {
        Assert.False(PartyCensusRule.MayMaterialise(ownerIdIsOwnRosterBody: true, coincidesWithOwnCompanion: false));
    }

    [Fact]
    public void AStandInIsRefusedForABodyStandingOnOurOwnCompanion()
    {
        // The id half cannot catch this on its own: party bodies are spawned by the party table
        // at load rather than placed in the GIT, so the two engines number them independently
        // and the owner's Trask can arrive here under an id that is nobody's.
        Assert.True(PartyCensusRule.CoincidesWithOwnCompanion(
            TraskAppearance, companionAppearance: TraskAppearance, distanceSquared: 0.25f));
        Assert.False(PartyCensusRule.MayMaterialise(
            ownerIdIsOwnRosterBody: false, coincidesWithOwnCompanion: true));
    }

    [Fact]
    public void AGenuineEnemyThatMerelyLooksLikeACompanionIsStillMaterialised()
    {
        // ⚠ The guard that keeps the guard safe. Refusing on appearance ALONE would make a real
        // hostile invisible while it could still hurt us — the exact failure the materialiser
        // exists to remove. A leaked companion is published AT our own copy's feet; a look-alike
        // across the room is not.
        Assert.False(PartyCensusRule.CoincidesWithOwnCompanion(
            TraskAppearance, companionAppearance: TraskAppearance, distanceSquared: 400f));
        Assert.True(PartyCensusRule.MayMaterialise(
            ownerIdIsOwnRosterBody: false, coincidesWithOwnCompanion: false));
    }

    [Fact]
    public void AnUnpublishedAppearanceNeverCoincides()
    {
        // 0 is "the owner has not published a row", not a row — the same reading
        // PeerIdentityRule applies one channel over. Treating it as a match would suppress every
        // stand-in whose census slot arrived a tick early.
        Assert.False(PartyCensusRule.CoincidesWithOwnCompanion(
            appearance: 0, companionAppearance: 0, distanceSquared: 0f));
    }
}
