using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rule behind the 2026-08-22 control-return regression: a client who was already
/// controlling a party-roster companion (Trask, say) at the moment of connect got locked onto
/// it, because <c>PartyRosterController.SyncClientPartyTogetherState</c>'s CSWParty count clamp
/// trims whichever member sits at the END of the array — ordinarily always a companion, but
/// with the array reversed by a pre-connect character switch, the last slot is the PC's own.
///
/// Every case here checks the same property: the clamp is exactly the door-transition fix it
/// always was EXCEPT while the player is still driving a companion this instance mirrors, where
/// it must refuse outright regardless of the count comparison, deferring to control-return.
/// See docs/research/control-return.md for the full chain.
/// </summary>
public class PartyTogetherClampRuleTests
{
    [Fact]
    public void TheOrdinaryCaseStillClampsWhenClientCountReadsHigher()
    {
        // PC + one companion (2) against the table's own companion-only count (1) — the
        // everyday case the clamp exists for, unaffected by this fix.
        Assert.True(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: false, clientCount: 2, tableCount: 1));
    }

    [Fact]
    public void AgreementNeverClamps()
    {
        Assert.False(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: false, clientCount: 1, tableCount: 1));
    }

    [Fact]
    public void TheClampNeverLowersBelowTheTableCount()
    {
        // clientCount < tableCount would mean CSWParty reads LOWER than the table, which is
        // never what this clamp is for (it only ever shrinks staleness/PC-counting, never
        // invents members) — the rule must not fire here either.
        Assert.False(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: false, clientCount: 0, tableCount: 1));
    }

    [Fact]
    public void ControllingACompanionRefusesTheClampEvenWhenTheCountsWouldOtherwiseTrigger()
    {
        // THE REGRESSION CASE. Same counts as the ordinary case above (2 against 1) — the only
        // difference is who the player is driving. That alone must flip the answer, because the
        // member the clamp would trim in this state is the PC's own slot, not a companion's.
        Assert.False(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: true, clientCount: 2, tableCount: 1));
    }

    [Fact]
    public void ControlComingBackResumesClampingImmediately()
    {
        // The moment ControlReturnController lands control back on the PC, the deferral lifts
        // on the very next pass — no cooldown, no extra state, because the ordinary case is
        // exactly as safe as it always was.
        Assert.True(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: false, clientCount: 2, tableCount: 1));
    }

    [Fact]
    public void DeferringNeverDependsOnTheCountsAlone()
    {
        // Even a wildly stale-looking count must not override the deferral: control-return is
        // the higher-priority concern whenever it applies, full stop.
        Assert.False(PartyTogetherClampRule.ShouldClamp(
            controllingSessionOwnedCompanion: true, clientCount: 9, tableCount: 0));
    }
}
