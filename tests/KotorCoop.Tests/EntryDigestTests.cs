using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="EntryDigest"/> is the order-independent, incrementally-maintained content
/// fingerprint every world-progression store XOR-sums into. The property under test
/// throughout is the one the class exists for: two peers that applied the same set of changes
/// in a different order must land on the same digest, and XOR-ing an entry's own hash back out
/// must exactly undo adding it — see the class remarks for why (docs/research/disconnect-
/// reconnect.md §4.1).
/// </summary>
public class EntryDigestTests
{
    // ---- Empty ------------------------------------------------------------------------

    [Fact]
    public void EmptyIsTheXorIdentity()
    {
        // XOR-ing anything with the empty digest must be a no-op, and XOR-ing a value with
        // itself must return to Empty — both are exactly what "0" as the identity means.
        ulong flag = EntryDigest.Flag(flagId: 7, value: 1);

        Assert.Equal(flag, EntryDigest.Empty ^ flag);
        Assert.Equal(EntryDigest.Empty, flag ^ flag);
    }

    [Fact]
    public void EmptyIsLiterallyZero()
    {
        Assert.Equal(0UL, EntryDigest.Empty);
    }

    // ---- Order independence --------------------------------------------------------------

    [Fact]
    public void FlagDigestIsOrderIndependent()
    {
        ulong a = EntryDigest.Flag(1, 10);
        ulong b = EntryDigest.Flag(2, -5);
        ulong c = EntryDigest.Flag(3, 0);

        ulong forward = EntryDigest.Empty ^ a ^ b ^ c;
        ulong backward = EntryDigest.Empty ^ c ^ b ^ a;
        ulong shuffled = EntryDigest.Empty ^ b ^ a ^ c;

        Assert.Equal(forward, backward);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void JournalDigestIsOrderIndependent()
    {
        ulong a = EntryDigest.Journal("end_m01aa", 10);
        ulong b = EntryDigest.Journal("end_m02aa", 20);

        Assert.Equal(EntryDigest.Empty ^ a ^ b, EntryDigest.Empty ^ b ^ a);
    }

    [Fact]
    public void GroundItemDigestIsOrderIndependent()
    {
        ItemIdentity identity = new(StackSize: 3, Charges: 1, MaxCharges: 5, Upgrades: 0xAB, Flags: 1);
        ulong a = EntryDigest.GroundItem(1, "g_w_lghtsbr01", identity);
        ulong b = EntryDigest.GroundItem(2, "g_w_blstrpstl001", default);

        Assert.Equal(EntryDigest.Empty ^ a ^ b, EntryDigest.Empty ^ b ^ a);
    }

    [Fact]
    public void ContainerLootRemovalDigestIsOrderIndependent()
    {
        ulong a = EntryDigest.ContainerLootRemoval(containerId: 1, itemId: 2);
        ulong b = EntryDigest.ContainerLootRemoval(containerId: 3, itemId: 4);

        Assert.Equal(EntryDigest.Empty ^ a ^ b, EntryDigest.Empty ^ b ^ a);
    }

    [Fact]
    public void CreatureLivenessDigestIsOrderIndependent()
    {
        ulong a = EntryDigest.CreatureLiveness(1, CreatureLivenessState.Alive);
        ulong b = EntryDigest.CreatureLiveness(2, CreatureLivenessState.Dead);
        ulong c = EntryDigest.CreatureLiveness(3, CreatureLivenessState.Gone);

        Assert.Equal(EntryDigest.Empty ^ a ^ b ^ c, EntryDigest.Empty ^ c ^ a ^ b);
    }

    // ---- Add/remove symmetry: XOR-ing an entry's own hash back out undoes it -------------

    [Fact]
    public void AddThenRemoveTheSameFlagReturnsToEmpty()
    {
        ulong digest = EntryDigest.Empty;

        digest ^= EntryDigest.Flag(42, 5);
        Assert.NotEqual(EntryDigest.Empty, digest);

        digest ^= EntryDigest.Flag(42, 5);
        Assert.Equal(EntryDigest.Empty, digest);
    }

    [Fact]
    public void ChangingAFlagsValueIsMixOutOldMixInNew()
    {
        // Exactly the incremental-update pattern the class remarks describe:
        // digest ^= Hash(key, A); digest ^= Hash(key, B);
        ulong digest = EntryDigest.Empty ^ EntryDigest.Flag(9, 1);

        digest ^= EntryDigest.Flag(9, 1); // mix out the old value
        digest ^= EntryDigest.Flag(9, 2); // mix in the new one

        Assert.Equal(EntryDigest.Empty ^ EntryDigest.Flag(9, 2), digest);
    }

    [Fact]
    public void RemovingEveryEntryOneAtATimeInAnyOrderReturnsToEmpty()
    {
        ulong[] entries =
        [
            EntryDigest.Flag(1, 1),
            EntryDigest.Journal("end_m01aa", 5),
            EntryDigest.GroundItem(10, "g_w_lghtsbr01", default),
            EntryDigest.ContainerLootRemoval(2, 3),
            EntryDigest.CreatureLiveness(99, CreatureLivenessState.Dead),
        ];

        ulong digest = EntryDigest.Empty;
        foreach (ulong entry in entries)
        {
            digest ^= entry;
        }

        Assert.NotEqual(EntryDigest.Empty, digest);

        // Remove in a DIFFERENT order than they were added — XOR does not care.
        foreach (ulong entry in entries.Reverse())
        {
            digest ^= entry;
        }

        Assert.Equal(EntryDigest.Empty, digest);
    }

    // ---- Case-insensitive string mixing (Journal keys, GroundItem resrefs) ---------------

    [Fact]
    public void JournalDigestIsCaseInsensitiveOnTheQuestTag()
    {
        // JournalStore keys with StringComparer.OrdinalIgnoreCase — two peers naming the same
        // tag in different casing must hash identically, or the digest would disagree about a
        // store the two sides actually agree on.
        Assert.Equal(
            EntryDigest.Journal("end_m01aa", 5),
            EntryDigest.Journal("END_M01AA", 5));

        Assert.Equal(
            EntryDigest.Journal("End_M01Aa", 5),
            EntryDigest.Journal("eND_m01AA", 5));
    }

    [Fact]
    public void GroundItemDigestIsCaseInsensitiveOnTheResRef()
    {
        Assert.Equal(
            EntryDigest.GroundItem(1, "g_w_lghtsbr01", default),
            EntryDigest.GroundItem(1, "G_W_LGHTSBR01", default));
    }

    // ---- Distinctness: different content must (almost always) hash differently -----------

    [Fact]
    public void DifferentFlagValuesHashDifferently()
    {
        Assert.NotEqual(EntryDigest.Flag(1, 1), EntryDigest.Flag(1, 2));
    }

    [Fact]
    public void DifferentFlagIdsHashDifferently()
    {
        Assert.NotEqual(EntryDigest.Flag(1, 5), EntryDigest.Flag(2, 5));
    }

    [Fact]
    public void DifferentJournalStatesHashDifferently()
    {
        Assert.NotEqual(
            EntryDigest.Journal("end_m01aa", 5),
            EntryDigest.Journal("end_m01aa", 6));
    }

    [Fact]
    public void DifferentQuestTagsHashDifferently()
    {
        Assert.NotEqual(
            EntryDigest.Journal("end_m01aa", 5),
            EntryDigest.Journal("end_m02aa", 5));
    }

    [Fact]
    public void GroundItemDigestDependsOnEveryIdentityField()
    {
        ItemIdentity baseline = new(StackSize: 1, Charges: 0, MaxCharges: 0, Upgrades: 0, Flags: 0);
        ulong reference = EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline);

        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline with { StackSize = 2 }));
        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline with { Charges = 1 }));
        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline with { MaxCharges = 1 }));
        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline with { Upgrades = 1 }));
        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_lghtsbr01", baseline with { Flags = 1 }));
        Assert.NotEqual(reference, EntryDigest.GroundItem(2, "g_w_lghtsbr01", baseline));
        Assert.NotEqual(reference, EntryDigest.GroundItem(1, "g_w_blstrpstl001", baseline));
    }

    [Fact]
    public void ContainerLootRemovalDependsOnBothIds()
    {
        ulong reference = EntryDigest.ContainerLootRemoval(1, 2);

        Assert.NotEqual(reference, EntryDigest.ContainerLootRemoval(9, 2));
        Assert.NotEqual(reference, EntryDigest.ContainerLootRemoval(1, 9));
    }

    [Fact]
    public void CreatureLivenessDigestDependsOnState()
    {
        Assert.NotEqual(
            EntryDigest.CreatureLiveness(1, CreatureLivenessState.Alive),
            EntryDigest.CreatureLiveness(1, CreatureLivenessState.Dead));

        Assert.NotEqual(
            EntryDigest.CreatureLiveness(1, CreatureLivenessState.Dead),
            EntryDigest.CreatureLiveness(1, CreatureLivenessState.Gone));
    }

    // ---- Determinism: same input, same output, every time ---------------------------------

    [Fact]
    public void TheSameInputsAlwaysHashTheSame()
    {
        Assert.Equal(EntryDigest.Flag(3, -1), EntryDigest.Flag(3, -1));
        Assert.Equal(
            EntryDigest.Journal("end_m01aa", 7),
            EntryDigest.Journal("end_m01aa", 7));
        Assert.Equal(
            EntryDigest.GroundItem(1, "g_w_lghtsbr01", default),
            EntryDigest.GroundItem(1, "g_w_lghtsbr01", default));
    }
}
