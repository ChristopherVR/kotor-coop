using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What makes two items of the same resref "the same stack" — pinned on BOTH sides of the
/// wire, because the two halves are only correct together.
///
/// docs/research/shared-inventory-v2.md §13.2 watched `g_i_medeqpmnt01` sit in a live party pool
/// under two <see cref="PartyPoolStackKey"/>s at the same instant — `/f1`
/// (<see cref="ItemIdentity.FlagKnown"/>) for the units a cheat/apply had created and `/f3`
/// (<c>Known|Identified</c>) for the save's own — and the engine merged the two stacks anyway.
/// A merge then migrated units from one key to the other, which the sync channel could only
/// read as a fabricated REMOVE on the losing key plus a fabricated ADD on the winning one. Both
/// were published; the host applied both; the two engines split (client 12, host 10, for three
/// real deposits).
///
/// The authority for what identity SHOULD be is the retail engine's own "may these two stacks
/// combine?" predicate at <c>0x00553CF0</c> — the first call <c>CSWSItem::MergeItem</c>
/// 0x00553F90 makes. Its ENTIRE flags comparison is
/// <c>mov eax,[ebp+0x288]; xor eax,[ebx+0x288]; test al,0x20; jne no-merge</c> at
/// <c>0x00553D60</c>-<c>0x00553D6E</c>: one bit, engine bit 5, Stolen. Identified (bit 0) and
/// NonEquippable (bit 6) are never read. The same predicate DOES compare <c>+0x294</c>
/// (Upgrades) and <c>+0x258</c> (Charges) in full, which is why those stay in the key.
///
/// ⚠ The agent-side narrowing (<see cref="ItemIdentity.StackIdentityFlagMask"/>) and the
/// plugin-side one (<see cref="PartyPoolIdentityMatcher.StackIdentityFlags"/>) must move
/// together. §13.2's own warning: masking on one side alone trades a fabricated deposit for a
/// silently-failing withdrawal.
/// </summary>
public class StackIdentityKeyingTests
{
    private const string Medpac = "g_i_medeqpmnt01";

    private static ItemIdentity Identity(byte flags, byte charges = 0, byte maxCharges = 0, uint upgrades = 0) =>
        new(StackSize: 1, charges, maxCharges, upgrades, flags);

    // ---- agent side: the key ------------------------------------------------------------

    /// <summary>The literal §13.2 split: the save's identified medpacs and a freshly-created
    /// unidentified one are ONE stack to the engine, and must be one key here.</summary>
    [Fact]
    public void AnIdentifiedAndAnUnidentifiedCopyOfTheSameResrefShareOneKey()
    {
        PartyPoolStackKey fromSave = PartyPoolStackKey.From(
            Medpac, Identity(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified));
        PartyPoolStackKey fromCheat = PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown));

        Assert.Equal(fromSave, fromCheat);
        Assert.Equal(fromSave.GetHashCode(), fromCheat.GetHashCode());
    }

    /// <summary>NonEquippable is engine bit 6, equally absent from the merge predicate's own
    /// <c>test al,0x20</c>.</summary>
    [Fact]
    public void TheNonEquippableBitDoesNotSplitAKeyEither()
    {
        Assert.Equal(
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown)),
            PartyPoolStackKey.From(
                Medpac, Identity(ItemIdentity.FlagKnown | ItemIdentity.FlagNonEquippable)));
    }

    /// <summary>Stolen IS the one bit the engine compares (<c>test al,0x20</c>, engine bit 5), so
    /// it must still keep two stacks apart — this is the guard against "fix it by masking
    /// everything".</summary>
    [Fact]
    public void TheStolenBitStillSplitsAKey()
    {
        Assert.NotEqual(
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown)),
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown | ItemIdentity.FlagStolen)));
    }

    /// <summary>An identity nobody managed to read is not the same fact as a stock one — the
    /// whole reason <see cref="ItemIdentity.FlagKnown"/> exists — so it survives the mask.</summary>
    [Fact]
    public void TheKnownBitSurvivesTheMask()
    {
        Assert.NotEqual(
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown)),
            PartyPoolStackKey.From(Medpac, Identity(0)));
    }

    /// <summary>Charges and Upgrades are compared in full by the same engine predicate
    /// (<c>+0x258</c>, <c>+0x294</c>) and genuinely do keep stacks apart.</summary>
    [Theory]
    [InlineData((byte)49, (byte)0, 0u)]
    [InlineData((byte)0, (byte)0, 1u)]
    public void ChargesAndUpgradesStillSplitAKey(byte charges, byte maxCharges, uint upgrades)
    {
        Assert.NotEqual(
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown)),
            PartyPoolStackKey.From(Medpac, Identity(ItemIdentity.FlagKnown, charges, maxCharges, upgrades)));
    }

    /// <summary>The store key is derived by the identical rule — §13.2's mechanism is not
    /// party-pool-specific, a merchant's stock merges the same way.</summary>
    [Fact]
    public void TheStoreKeyIsNarrowedTheSameWay()
    {
        Assert.Equal(
            StoreStackKey.From(0x74, Medpac, Identity(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified)),
            StoreStackKey.From(0x74, Medpac, Identity(ItemIdentity.FlagKnown)));

        Assert.NotEqual(
            StoreStackKey.From(0x74, Medpac, Identity(ItemIdentity.FlagKnown)),
            StoreStackKey.From(0x75, Medpac, Identity(ItemIdentity.FlagKnown)));
    }

    /// <summary>The narrowing happens at CONSTRUCTION, so both instances derive the
    /// byte-identical key for the same stack — which is what keeps <c>EntryDigest</c> and every
    /// dictionary lookup deterministic across the wire. A custom equality would have left the
    /// stored key dependent on whichever variant was inserted first.</summary>
    [Fact]
    public void TheNarrowedFlagsAreStoredOnTheKeyNotJustIgnoredByEquality()
    {
        Assert.Equal(
            ItemIdentity.FlagKnown,
            PartyPoolStackKey.From(
                Medpac, Identity(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified)).Flags);
    }

    // ---- plugin side: the withdrawal matcher ---------------------------------------------

    /// <summary>
    /// The other half of §13.2, and the line it could not explain for two rounds:
    /// <c>party pool v2: submitting remove of 2x ... the engine refused it: 'g_i_medeqpmnt01'
    /// remove failed: no matching stack found</c>. The agent asked to withdraw a key whose flags
    /// no longer matched anything live, because the plugin's own matcher compared the identified
    /// bit. A withdrawal must now find the merged stack whichever way the merge went.
    /// </summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)] // SharedBlock.ItemIdentityIdentified
    [InlineData(8u)] // SharedBlock.ItemIdentityNonEquippable
    public void AWithdrawalFindsTheMergedStackWhateverItsIdentifiedBitSaysNow(uint liveWireFlags)
    {
        // What a scan-derived PartyPoolStackKey now carries for a plain pooled item: Known only.
        const uint RequestedFromKey = 1;

        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges: 0, itemMaxCharges: 0, itemUpgrades: 0, itemWireFlags: liveWireFlags,
            requestedCharges: 0, requestedMaxCharges: 0, requestedUpgrades: 0,
            requestedWireFlags: RequestedFromKey));
    }

    /// <summary>Stolen still refuses, in both directions — the plugin half of
    /// <see cref="TheStolenBitStillSplitsAKey"/>.</summary>
    [Fact]
    public void AWithdrawalStillRefusesOnTheStolenBit()
    {
        Assert.False(PartyPoolIdentityMatcher.Matches(
            itemCharges: 0, itemMaxCharges: 0, itemUpgrades: 0, itemWireFlags: 0,
            requestedCharges: 0, requestedMaxCharges: 0, requestedUpgrades: 0,
            requestedWireFlags: 4));

        Assert.False(PartyPoolIdentityMatcher.Matches(
            itemCharges: 0, itemMaxCharges: 0, itemUpgrades: 0, itemWireFlags: 4,
            requestedCharges: 0, requestedMaxCharges: 0, requestedUpgrades: 0,
            requestedWireFlags: 1));
    }

    /// <summary>A wildcard is the ABSENCE of a demand, not a set of bits: narrowing it would
    /// turn it into a demand for zero and break a human's bare <c>poolremove &lt;resref&gt;</c>
    /// all over again (docs/research/party-pool-sync.md §19.2/§20).</summary>
    [Fact]
    public void TheWildcardSurvivesNarrowing()
    {
        Assert.Equal(
            PartyPoolIdentityMatcher.Wildcard,
            PartyPoolIdentityMatcher.NormalizeWireFlags(PartyPoolIdentityMatcher.Wildcard));

        Assert.True(PartyPoolIdentityMatcher.Matches(
            itemCharges: 0, itemMaxCharges: 0, itemUpgrades: 0, itemWireFlags: 4,
            requestedCharges: 0, requestedMaxCharges: 0, requestedUpgrades: 0,
            requestedWireFlags: PartyPoolIdentityMatcher.Wildcard));
    }

    /// <summary>The two masks are the same rule expressed twice (the NativeAOT win-x86 plugin
    /// cannot reference the x64 agent's assemblies), and the ONE bit they must agree on is
    /// Stolen. The plugin's own mask deliberately omits <see cref="ItemIdentity.FlagKnown"/>,
    /// which is a wire meta-flag no live item carries.</summary>
    [Fact]
    public void TheAgentAndPluginMasksAgreeOnTheOneEngineBit()
    {
        Assert.Equal(
            ItemIdentity.FlagStolen,
            (byte)(ItemIdentity.StackIdentityFlagMask & PartyPoolIdentityMatcher.StackIdentityFlags));

        Assert.Equal(
            (byte)(ItemIdentity.FlagKnown | ItemIdentity.FlagStolen),
            ItemIdentity.StackIdentityFlagMask);

        Assert.Equal(0u, PartyPoolIdentityMatcher.StackIdentityFlags & ItemIdentity.FlagKnown);
    }
}
