using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of the companion-roster channel: what a peer owes when its roster and the
/// authority's disagree. The plugin performs the same two mask operations against its own party
/// table before it calls the engine, so these are the rules the reconciliation actually follows.
/// </summary>
public class PartyRosterTests
{
    /// <summary>Slot indices, as the engine numbers them (the slot IS the companion).</summary>
    private const int Bastila = 0;
    private const int Carth = 1;
    private const int Mission = 5;

    private static PartyRoster Roster(int[] available, int[] party)
    {
        ushort a = 0;
        ushort p = 0;
        foreach (int slot in available)
        {
            a |= (ushort)(1 << slot);
        }

        foreach (int slot in party)
        {
            p |= (ushort)(1 << slot);
        }

        return new PartyRoster(a, p);
    }

    [Fact]
    public void AnIdenticalRosterOwesNothing()
    {
        PartyRoster roster = Roster([Bastila, Carth], [Carth]);

        Assert.Equal(0, roster.AddsAgainst(roster));
        Assert.Equal(0, roster.RemovesAgainst(roster));
    }

    [Fact]
    public void ARecruitmentTheOtherInstanceMissedIsAnAdd()
    {
        // The headline case: a conversation on the authority added Carth to the party; the peer
        // ran nothing and still has an empty party.
        PartyRoster authority = Roster([Bastila, Carth], [Carth]);
        PartyRoster local = Roster([Bastila, Carth], []);

        Assert.Equal(1 << Carth, authority.AddsAgainst(local));
        Assert.Equal(0, authority.RemovesAgainst(local));
    }

    [Fact]
    public void ADismissalTheOtherInstanceMissedIsARemove()
    {
        PartyRoster authority = Roster([Bastila, Carth], []);
        PartyRoster local = Roster([Bastila, Carth], [Carth]);

        Assert.Equal(0, authority.AddsAgainst(local));
        Assert.Equal(1 << Carth, authority.RemovesAgainst(local));
    }

    [Fact]
    public void ASwapIsBothDirectionsAtOnce()
    {
        // Why the plugin runs removes FIRST: the party holds at most two companions
        // (CSWPartyTable::AddMember refuses at a count of 2), so with two already seated the add
        // below cannot succeed until the remove has freed a seat. Done the other way round a
        // straight swap fails on every pass, forever.
        PartyRoster authority = Roster([Bastila, Carth, Mission], [Bastila, Mission]);
        PartyRoster local = Roster([Bastila, Carth, Mission], [Bastila, Carth]);

        Assert.Equal(1 << Mission, authority.AddsAgainst(local));
        Assert.Equal(1 << Carth, authority.RemovesAgainst(local));
    }

    [Fact]
    public void AvailabilityAloneOwesNothing()
    {
        // Recruited on the authority but not in its party, and unknown to the peer. There is
        // nothing to do: this channel reconciles the party SEAT, and recruitment does not
        // replicate (the blueprint the script named is not recoverable from the roster).
        PartyRoster authority = Roster([Bastila, Carth], [Bastila]);
        PartyRoster local = Roster([Bastila], [Bastila]);

        Assert.Equal(0, authority.AddsAgainst(local));
        Assert.Equal(0, authority.RemovesAgainst(local));
    }

    [Fact]
    public void AnAddIsOwedEvenWhereTheCompanionWasNeverRecruitedLocally()
    {
        // Deliberately NOT filtered here. Whether the local engine can honour an add is the
        // local engine's business — it refuses on its own, and the plugin turns that into one
        // log line naming the slot. Masking it out in the diff would make the divergence
        // invisible instead of merely unfixable.
        PartyRoster authority = Roster([Bastila, Carth], [Carth]);
        PartyRoster local = Roster([Bastila], []);

        Assert.Equal(1 << Carth, authority.AddsAgainst(local));
    }

    [Fact]
    public void NineSlotsIsTheWholeMask()
    {
        // The engine's bound, not a chosen cap: AddMember refuses nNPC >= 9 and availability at
        // party table +0x30 is int[9].
        Assert.Equal(ProtocolConstants.PartyRosterSlots, System.Numerics.BitOperations.PopCount(PartyRoster.SlotMask));
    }
}
