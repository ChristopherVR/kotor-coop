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

    /// <summary>Runtime item-state flag: the engine has marked this body for deletion.</summary>
    public const uint BitDeleting = 1u << 8;

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
    /// Bit 2 (mask <c>0x4</c>) of <see cref="BitFlags"/> — NOT one of the three bits
    /// <see cref="Decode"/> carries onto the wire, and not a persisted item field at all (it is
    /// absent from <c>SaveItem</c>/<c>LoadDataFromGff</c>'s own field list). This is a STORE-STOCK
    /// property: the .utm store template's per-entry <c>Infinite</c> GFF field, VERIFIED this
    /// pass (docs/research/store-sync.md §1.5) from fresh disassembly of <c>CSWSStore::SellItem
    /// 0x005C6F70</c> and <c>CSWSStore::AcquireItem 0x005C7610</c> — both test exactly this bit
    /// to decide whether a store-stock entry's quantity may change. Public here (rather than
    /// private, like the three bits above) because the store-sync bridge/plugin need to test it
    /// directly on an item outside a store's repository too — a store-stock item is still a
    /// plain <c>CSWSItem</c> at this same offset.
    /// </summary>
    public const uint BitInfinite = 1u << 2;

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

        // ⚠ A CHARGE COUNT THAT DOES NOT FIT IN A BYTE IS NOT A CHARGE COUNT.
        //
        // Both fields are persisted as GFF bytes (SaveItem 0x0055CE27 and 0x0055CE3D each
        // read a single byte), so the engine can never have MEANT a value above 255 — such a
        // value is uninitialised memory, and clamping it to 255 would turn garbage into a
        // plausible number and write that onto a peer's item.
        //
        // It is not hypothetical. `CSWSItem::Constructor` 0x005530A0 writes Charges = 50
        // (0x00553189, `mov dword [esi+0x258], 0x32`) and NEVER touches MaxCharges — the only
        // writers of +0x25C inside CSWSItem are LoadDataFromGff (0x0056004E) and CopyItem
        // (0x0055DB0F). So any item whose template load did not run carries an uninitialised
        // MaxCharges. Measured live on 2026-08-15: `charges 50/1768092` on a body
        // CreateItemOnFloor produced for a resref that does not exist.
        //
        // The repair is the ENGINE'S OWN default, not one we invented: at 0x00560016 the
        // loader passes the Charges value it has just read as the default for MaxCharges, so
        // "MaxCharges I cannot read" means "MaxCharges equals Charges". That is also what
        // every base-game blueprint gets — none of the 557 .uti files declares a MaxCharges
        // field at all, so all 557 take that default.
        byte safeCharges = charges <= byte.MaxValue ? (byte)charges : (byte)0;
        byte safeMaxCharges = maxCharges <= byte.MaxValue ? (byte)maxCharges : safeCharges;

        // The stack is a word to the engine (SaveItem reads `word [esi+0x28C]`), and a stack
        // of zero is not something it produces — but this window can also be a stale read of
        // freed memory, and the stack size is the one field that ends up written into a real
        // item on the far side.
        return new ItemIdentity(
            (ushort)Math.Clamp(stack, 1, ushort.MaxValue),
            safeCharges,
            safeMaxCharges,
            upgrades,
            flags);
    }

    /// <summary>Returns whether the live flag dword marks the item for deletion. This runtime
    /// bookkeeping is intentionally not copied into <see cref="ItemIdentity"/> on the wire.</summary>
    public static bool IsDeleting(ReadOnlySpan<byte> window) =>
        window.Length >= (BitFlags - WindowStart) + sizeof(uint)
        && (ReadUInt32(window, BitFlags) & BitDeleting) != 0;

    private static uint ReadUInt32(ReadOnlySpan<byte> window, int itemOffset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(window[(itemOffset - WindowStart)..]);
}
