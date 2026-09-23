using System.Buffers.Binary;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decode half of item identity, against a synthetic <c>CSWSItem</c> window.
///
/// There is no game here and there does not need to be: the offsets came out of
/// <c>CSWSItem::SaveItem</c> / <c>LoadDataFromGff</c> (docs/research/item-identity.md), and
/// what a test can still prove is that the decoder reads the fields it claims to read, at
/// the offsets it claims, and clamps the ones that would otherwise be written back into a
/// real item on the far side.
/// </summary>
public class ItemIdentityTests
{
    /// <summary>Builds the 0x258..0x298 window the decoder is handed, in CSWSItem coordinates.</summary>
    private static byte[] Window(
        uint charges = 0, uint maxCharges = 0, uint bits = 0, uint stack = 1, uint upgrades = 0)
    {
        byte[] window = new byte[ItemIdentityLayout.WindowLength];
        Put(window, ItemIdentityLayout.Charges, charges);
        Put(window, ItemIdentityLayout.MaxCharges, maxCharges);
        Put(window, ItemIdentityLayout.BitFlags, bits);
        Put(window, ItemIdentityLayout.StackSize, stack);
        Put(window, ItemIdentityLayout.Upgrades, upgrades);
        return window;

        static void Put(byte[] window, int itemOffset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(
                window.AsSpan(itemOffset - ItemIdentityLayout.WindowStart), value);
    }

    [Fact]
    public void TheWindowCoversExactlyTheFieldsItClaims()
    {
        Assert.Equal(0x258, ItemIdentityLayout.WindowStart);
        Assert.Equal(0x40, ItemIdentityLayout.WindowLength);
        Assert.True(ItemIdentityLayout.WindowStart + ItemIdentityLayout.WindowLength
            <= ItemIdentityLayout.ItemSize);
    }

    [Fact]
    public void ReadsStackChargesAndUpgradesFromTheirOwnOffsets()
    {
        ItemIdentity identity = ItemIdentityLayout.Decode(
            Window(charges: 7, maxCharges: 50, stack: 20, upgrades: 0b1011));

        Assert.True(identity.IsKnown);
        Assert.Equal(20, identity.StackSize);
        Assert.Equal(7, identity.Charges);
        Assert.Equal(50, identity.MaxCharges);
        Assert.Equal(0b1011u, identity.Upgrades);
    }

    /// <summary>
    /// The three bits that live in the one flag dword. Bit 0 is the one
    /// <c>CSWSItem::SetIdentified</c> (0x00554170) toggles; bits 5 and 6 are what
    /// <c>SaveItem</c> writes out as Stolen and NonEquippable.
    /// </summary>
    [Theory]
    [InlineData(1u << 0, ItemIdentity.FlagIdentified)]
    [InlineData(1u << 5, ItemIdentity.FlagStolen)]
    [InlineData(1u << 6, ItemIdentity.FlagNonEquippable)]
    public void EachEngineBitBecomesItsOwnWireFlag(uint engineBit, byte wireFlag)
    {
        ItemIdentity identity = ItemIdentityLayout.Decode(Window(bits: engineBit));

        Assert.Equal(ItemIdentity.FlagKnown | wireFlag, identity.Flags);
    }

    /// <summary>Bits 3, 4, 7 and 8 are dropable / pickpocketable / new-item / DELETING —
    /// real bits in the same dword that this channel deliberately does not carry, and must
    /// not leak into the ones it does.</summary>
    [Fact]
    public void TheUncarriedBitsAreIgnored()
    {
        ItemIdentity identity = ItemIdentityLayout.Decode(
            Window(bits: (1u << 3) | (1u << 4) | (1u << 7) | (1u << 8)));

        Assert.Equal(ItemIdentity.FlagKnown, identity.Flags);
    }

    [Fact]
    public void DeletingIsAvailableForLocalFilteringButNeverEntersWireIdentity()
    {
        byte[] window = Window(bits: ItemIdentityLayout.BitDeleting | (1u << 0));

        Assert.True(ItemIdentityLayout.IsDeleting(window));
        Assert.Equal(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified,
            ItemIdentityLayout.Decode(window).Flags);
        Assert.False(ItemIdentityLayout.IsDeleting(Window(bits: 1u << 7)));
    }

    [Fact]
    public void TruncatedWindowCannotClaimDeletingFlag()
    {
        byte[] window = Window(bits: ItemIdentityLayout.BitDeleting);

        Assert.False(ItemIdentityLayout.IsDeleting(window.AsSpan(0, 0x33)));
    }

    /// <summary>
    /// A stack size of zero is not something the engine produces, and it is the one decoded
    /// field that gets written back into a live item on the other machine. A stale read of
    /// reused memory must not be able to turn a peer's item into an unpickable stack of
    /// nothing.
    /// </summary>
    [Fact]
    public void GarbageIsClampedRatherThanTrusted()
    {
        Assert.Equal(1, ItemIdentityLayout.Decode(Window(stack: 0)).StackSize);
        Assert.Equal(
            ushort.MaxValue, ItemIdentityLayout.Decode(Window(stack: 0x00FF_FFFF)).StackSize);
    }

    /// <summary>
    /// ⚠ AN UNINITIALISED MaxCharges MUST NOT BECOME 255.
    ///
    /// Both charge fields are persisted as GFF bytes (SaveItem 0x0055CE27 / 0x0055CE3D each
    /// read one byte), so the engine can never have meant a value above 255 — anything larger
    /// is memory nobody wrote. `CSWSItem::Constructor` 0x005530A0 sets Charges to 50 and never
    /// touches MaxCharges at all: the only writers of +0x25C inside the class are
    /// LoadDataFromGff (0x0056004E) and CopyItem (0x0055DB0F). So any item whose template load
    /// did not run carries garbage there, and clamping it to 255 would write a fabricated
    /// maximum onto a peer's item.
    ///
    /// Measured live on 2026-08-15: a body CreateItemOnFloor produced for a resref that does
    /// not exist read `charges 50/1768092`.
    ///
    /// The repair is the engine's own default, not one we invented: at 0x00560016 the loader
    /// passes the Charges it has just read as the default for MaxCharges. Every base-game
    /// blueprint takes that path — none of the 557 .uti files declares a MaxCharges field.
    /// </summary>
    [Fact]
    public void AnUnreadableMaxChargesFallsBackToChargesTheWayTheLoaderDoes()
    {
        ItemIdentity identity =
            ItemIdentityLayout.Decode(Window(charges: 50, maxCharges: 1_768_092));

        Assert.Equal(50, identity.Charges);
        Assert.Equal(50, identity.MaxCharges);
    }

    /// <summary>A charges value that cannot be a byte is not a charges value either. Zero
    /// rather than 255, because the fabrication is the thing to avoid — and MaxCharges then
    /// follows it, keeping the pair consistent.</summary>
    [Fact]
    public void AnUnreadableChargesCountIsNotFabricated()
    {
        ItemIdentity identity = ItemIdentityLayout.Decode(Window(charges: 4000, maxCharges: 4000));

        Assert.Equal(0, identity.Charges);
        Assert.Equal(0, identity.MaxCharges);
    }

    /// <summary>A real charged item — a droid shield is Charges 10 with no MaxCharges field —
    /// still travels exactly as it reads.</summary>
    [Fact]
    public void ARealChargeCountIsCarriedUntouched()
    {
        ItemIdentity identity = ItemIdentityLayout.Decode(Window(charges: 10, maxCharges: 10));

        Assert.Equal(10, identity.Charges);
        Assert.Equal(10, identity.MaxCharges);
    }

    /// <summary>A short read is a read that failed, not a stock item.</summary>
    [Fact]
    public void ATruncatedWindowDecodesAsUnknown()
    {
        Assert.Equal(ItemIdentity.Unknown, ItemIdentityLayout.Decode([1, 2, 3]));
        Assert.False(ItemIdentityLayout.Decode([]).IsKnown);
    }

    /// <summary>
    /// The plugin reads the identity back out of one packed dword in the shared block's item
    /// slot, so the packing the agent writes and the unpacking the plugin does have to be
    /// the same arithmetic. The plugin's half is unsafe pointer code that cannot be
    /// referenced here; this pins the agent's half against the layout both sides document.
    /// </summary>
    [Fact]
    public void TheBlockPackingSurvivesARoundTrip()
    {
        ItemIdentity identity = new(
            20, 7, 50, 0xDEAD_BEEF,
            ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified | ItemIdentity.FlagStolen);

        uint packed = identity.Charges
            | ((uint)identity.MaxCharges << 8)
            | ((uint)identity.Flags << 16);

        Assert.Equal(7u, packed & 0xFF);
        Assert.Equal(50u, (packed >> 8) & 0xFF);
        Assert.Equal(identity.Flags, (byte)((packed >> 16) & 0xFF));
    }
}
