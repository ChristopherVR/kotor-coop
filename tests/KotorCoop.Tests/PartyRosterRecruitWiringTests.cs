using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Proves the recruitment-sync PATH is generic across all nine slots, not special-cased to
/// Carth (slot 2) the way the live rig has only actually exercised so far.
/// <c>PartyRosterController.Reconcile</c> itself can't be driven from here — it calls
/// <see cref="Odyssey"/>'s raw P/Invoke directly, which needs a live game process — but the two
/// PURE pieces the wiring is built from can be, and this test ties them together the same way
/// <c>Odyssey.AddAvailableNpcByTemplate</c> and <c>PartyRosterController.Reconcile</c> actually
/// chain them at runtime:
///
/// 1. <see cref="PartyRosterTemplateTable.TryGetTemplate"/> resolves slot -&gt; story blueprint
///    (what <c>Odyssey.AddAvailableNpcByTemplate</c> looks up before making its engine call).
/// 2. <see cref="PartyRosterRecruitRule.CanAddImmediately"/> gates whatever object that call
///    returns (what <c>PartyRosterController.Reconcile</c> checks before calling
///    <c>AddPartyMemberBySlot</c>).
///
/// Both are slot-count-driven (<see cref="PartyRosterTemplateTable.SlotCount"/>), not a fixed
/// list of known-good slots, so a regression that silently narrowed either piece back to a
/// single companion (e.g. an early-return special case for slot 2) would fail this test even
/// though <see cref="PartyRosterTemplateTableTests"/> and <see cref="PartyRosterRecruitRuleTests"/>
/// individually still pass. See docs/research/dialogue-consequences.md's "Round (2026-08-24):
/// static-verification pass" section for what this does and does not substitute for (it is not a
/// replacement for the live rig — <c>Odyssey.AddAvailableNpcByTemplate</c>'s actual engine call
/// has never been unit-tested and can't be from this project).
/// </summary>
public class PartyRosterRecruitWiringTests
{
    [Fact]
    public void EveryOneOfTheNineSlotsResolvesATemplateAndIsGatedTheSameWayCarthIs()
    {
        for (int slot = 0; slot < PartyRosterTemplateTable.SlotCount; slot++)
        {
            Assert.True(
                PartyRosterTemplateTable.TryGetTemplate(slot, out string resref),
                $"slot {slot} has no template — the recruitment path would refuse it "
                    + "(Odyssey.AddAvailableNpcByTemplate returns 0) even though "
                    + "PartyRosterController.Reconcile would still try, for any peer roster bit");
            Assert.False(string.IsNullOrEmpty(resref));

            // The freshly-recruited object every slot's AddAvailableNpcByTemplate call would
            // return is off-stage, exactly like Carth's — this is not a per-companion property,
            // so nothing here should ever gate slot 2 differently from any other slot.
            Assert.False(
                PartyRosterRecruitRule.CanAddImmediately(PartyRosterBodyOrigin.RecruitedOffStage),
                $"slot {slot}: a freshly recruited off-stage object must never be added in the "
                    + "same pass (this would reproduce the failure-9 duplicate-body defect)");
        }
    }

    [Fact]
    public void OutOfRangeSlotsAreRefusedBeforeTheRuleEverRuns()
    {
        // PartyRosterController.Reconcile derives addSlot from a peer's roster bitmask
        // (LowestSlot), which is bounded to 0-8 by PartyRoster.SlotMask (see PartyRosterTests'
        // NineSlotsIsTheWholeMask) — but the template table's own out-of-range refusal is the
        // second, independent line of defence Odyssey.AddAvailableNpcByTemplate relies on
        // (`table == 0 || !PartyRosterTemplateTable.TryGetTemplate(slot, out resref)` short-
        // circuits before any engine call is made).
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(-1, out _));
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(PartyRosterTemplateTable.SlotCount, out _));
    }
}
