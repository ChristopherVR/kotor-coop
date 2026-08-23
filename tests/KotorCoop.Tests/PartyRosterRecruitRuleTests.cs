using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision half of <c>PartyRosterController.Reconcile</c>'s same-pass-add question,
/// covering the defect a live pass found 2026-08-23
/// (docs/research/dialogue-consequences.md): <c>Odyssey.AddAvailableNpcByTemplate</c> returns a
/// real, registered engine object that was never placed in an area, so
/// <c>Odyssey.AddPartyMemberBySlot</c> refuses it with failure 9 forever — not a timing race, a
/// structural mismatch (see <see cref="PartyRosterRecruitRule"/>'s own doc comment for the
/// disassembled reason). The old code fell through to the add anyway in the same pass, which
/// always failed and left that object behind, unreferenced, while the very next pass built a
/// SECOND, different object through <c>Odyssey.SpawnAvailableNpc</c> that succeeded — two
/// engine objects built for one recruit. This rule is the fix: only an object that has actually
/// been placed in an area (<see cref="PartyRosterBodyOrigin.LiveInWorld"/>) may be handed to
/// <c>AddPartyMemberBySlot</c> in the pass that produced it.
/// </summary>
public class PartyRosterRecruitRuleTests
{
    [Fact]
    public void AFreshlyRecruitedOffStageCompanionMayNotBeAddedImmediately()
    {
        // This is the exact case the live pass caught: AddAvailableNpcByTemplate just returned
        // an object id, and the old code tried AddPartyMemberBySlot on it in the same pass.
        Assert.False(PartyRosterRecruitRule.CanAddImmediately(PartyRosterBodyOrigin.RecruitedOffStage));
    }

    [Fact]
    public void ABodyThatHasActuallyBeenPlacedInTheWorldMayBeAddedImmediately()
    {
        // SpawnAvailableNpc (or an object ResolveServerObject already resolved) really has been
        // AddToArea'd — this is the ordinary, already-correct "spawn then add in the same pass"
        // path this rule must not disturb.
        Assert.True(PartyRosterRecruitRule.CanAddImmediately(PartyRosterBodyOrigin.LiveInWorld));
    }
}
