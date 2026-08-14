using System.Buffers.Binary;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// Where a live <c>CSWSItem</c> keeps the state that belongs to the INSTANCE rather than to
/// its blueprint, and how to turn that into an <see cref="ItemIdentity"/>.
///
/// Every offset here was read out of the engine's own persistence routines rather than
/// guessed, which is why they can be trusted without a live probe:
/// <c>CSWSItem::SaveItem</c> (0x0055CCD0) and <c>CSWSItem::LoadDataFromGff</c> (0x0055FCD0)
/// are a matched pair that name each field with its GFF label, and
/// <c>CSWSItem::SetIdentified</c> (0x00554170) is eight instructions that do nothing but
/// toggle bit 0 of the flags dword. Full provenance in docs/research/item-identity.md.
///
/// This lives in the Memory assembly and takes a plain byte window on purpose: the same
/// decode has to serve the external agent (ReadProcessMemory) and be testable without a
/// game, and an offset table that only exists inside a pointer dereference is an offset
/// table nothing can check.
/// </summary>
public static class ItemIdentityLayout
{
    /// <summary>The <c>CSWSItem</c> vtable — the only proof that a pointer really is an item
    /// before fields 0x258 bytes in are believed. Same constant the floor-state probe uses.</summary>
    public const uint ItemVtable = 0x00748DA0;

    /// <summary>Total size of <c>CSWSItem</c>, per the symbol DB.</summary>
    public const int ItemSize = 0x298;

    /// <summary>int. Saved as the GFF byte <c>Charges</c>; defaults to 50 when absent.</summary>
    public const int Charges = 0x258;

    /// <summary>int. Saved as the GFF byte <c>MaxCharges</c>.</summary>
    public const int MaxCharges = 0x25C;

    /// <summary>Bitfield dword. Bit 0 identified, bit 3 dropable, bit 4 pickpocketable,
    /// bit 5 stolen, bit 6 non-equippable, bit 7 new-item, bit 8 deleting.</summary>
    public const int BitFlags = 0x288;

    /// <summary>Stack size. Written as a dword whose high half is kept zero (the loader
    /// zero-extends a 16-bit GFF word into it), and read back as a word by the saver.</summary>
    public const int StackSize = 0x28C;

    /// <summary>Installed-upgrade MASK, one bit per upgrade slot — not a level and not a
    /// list. <c>CSWSItem::HasUpgrade</c> (0x004EADA0) is literally
    /// <c>[this+0x294] &amp; (1 &lt;&lt; n)</c>.</summary>
    public const int Upgrades = 0x294;

    /// <summary>First byte of the window <see cref="Decode"/> needs.</summary>
    public const int WindowStart = Charges;

    /// <summary>Length of that window: 0x258 through the end of the upgrade mask.</summary>
    public const int WindowLength = (Upgrades + 4) - WindowStart;

    private const uint BitIdentified = 1u << 0;
    private const uint BitStolen = 1u << 5;
    private const uint BitNonEquippable = 1u << 6;

    /// <summary>
    /// Decodes <see cref="WindowLength"/> bytes read from <c>item + <see cref="WindowStart"/></c>.
    ///
    /// Always returns a KNOWN identity — the caller has the bytes, so there is nothing left
    /// to be unsure about. Deciding whether the pointer was an item at all belongs upstream,
    /// where the vtable can be checked.
    /// </summary>
    public static ItemIdentity Decode(ReadOnlySpan<byte> window)
    {
        if (window.Length < WindowLength)
        {
            return ItemIdentity.Unknown;
        }

        uint charges = ReadUInt32(window, Charges);
        uint maxCharges = ReadUInt32(window, MaxCharges);
        uint bits = ReadUInt32(window, BitFlags);
        uint stack = ReadUInt32(window, StackSize);
        uint upgrades = ReadUInt32(window, Upgrades);

        byte flags = ItemIdentity.FlagKnown;
        if ((bits & BitIdentified) != 0)
        {
            flags |= ItemIdentity.FlagIdentified;
        }

        if ((bits & BitStolen) != 0)
        {
            flags |= ItemIdentity.FlagStolen;
        }

        if ((bits & BitNonEquippable) != 0)
        {
            flags |= ItemIdentity.FlagNonEquippable;
        }

        // Clamped rather than trusted. The engine keeps charges inside a byte (both are
        // saved as GFF bytes) and the stack inside a word, but this window can also be a
        // stale read of freed memory, and a garbage stack size is the one field that would
        // be written back into a real item on the far side.
        return new ItemIdentity(
            (ushort)Math.Clamp(stack, 1, ushort.MaxValue),
            (byte)Math.Min(charges, byte.MaxValue),
            (byte)Math.Min(maxCharges, byte.MaxValue),
            upgrades,
            flags);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> window, int itemOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(window[(itemOffset - WindowStart)..]);
}
