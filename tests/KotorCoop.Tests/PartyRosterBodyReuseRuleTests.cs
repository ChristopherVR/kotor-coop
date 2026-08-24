using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision half of <c>PartyRosterController.Reconcile</c>'s "does this instance already
/// have the peer's own pre-recruit body standing in the world" check
/// (docs/research/dialogue-consequences.md's 2026-08-24 "stale mirrored body" entry): before
/// this fix, a companion who was ALSO a placed NPC (Zaalbar, <c>tar_m05aa</c>, live-confirmed)
/// kept a second, motionless copy of himself standing at his pre-recruit spot on a peer whose
/// own recruit dialogue never ran, because the peer always rebuilt a fresh body from the story
/// template instead of ever checking for one that was already there.
/// </summary>
public class PartyRosterBodyReuseRuleTests
{
    [Fact]
    public void AZeroObjectIdIsNotWorthLookingUp()
    {
        // 0 is the "no id published" sentinel — either an older peer build that never wrote
        // this field, or the peer genuinely has no live body for the slot right now.
        Assert.False(PartyRosterBodyReuseRule.IsWorthLookingUp(0));
    }

    [Fact]
    public void ObjectInvalidIsNotWorthLookingUp()
    {
        // The engine's own OBJECT_INVALID sentinel is never a real id; a peer that published it
        // is not the build it claims to be, the same reasoning PacketCodec's own roster reader
        // already applies to the two masks.
        Assert.False(PartyRosterBodyReuseRule.IsWorthLookingUp(PartyRosterBodyReuseRule.ObjectInvalid));
    }

    [Fact]
    public void AnOrdinaryObjectIdIsWorthLookingUp()
    {
        Assert.True(PartyRosterBodyReuseRule.IsWorthLookingUp(0x2));
    }

    [Fact]
    public void ReuseIsRefusedWhenTheIdWasNeverWorthLookingUp()
    {
        // Even if the caller (incorrectly) claims a body was "found" for a 0/invalid id, this
        // rule never authorises a reuse from one — there is nothing a real lookup could ever
        // have found under a sentinel.
        Assert.False(PartyRosterBodyReuseRule.ShouldReuse(0, foundLocally: true));
        Assert.False(PartyRosterBodyReuseRule.ShouldReuse(PartyRosterBodyReuseRule.ObjectInvalid, foundLocally: true));
    }

    [Fact]
    public void ReuseIsRefusedWhenNothingResolvedLocally()
    {
        // The headline "no GIT-identical body" case: a companion who was never a placed NPC to
        // begin with (found via a minigame, a crate, a cutscene reveal) — the id is real and
        // worth looking up, but CreatureFinder.FindByObjectId simply never finds a match here.
        // Reconcile must fall back to the ordinary off-stage recruit + spawn path, unchanged.
        Assert.False(PartyRosterBodyReuseRule.ShouldReuse(0x2, foundLocally: false));
    }

    [Fact]
    public void ReuseIsGrantedForTheExactZaalbarShape()
    {
        // The live-observed defect: the peer's own party table holds object id 0x2 for Zaalbar's
        // slot (its own placed body, converted in place by the real recruit dialogue), and this
        // instance's independently-loaded copy of the same GIT-placed module also has a live
        // creature at 0x2 — so the lookup found it, and the reuse is authorised.
        Assert.True(PartyRosterBodyReuseRule.ShouldReuse(0x2, foundLocally: true));
    }
}
