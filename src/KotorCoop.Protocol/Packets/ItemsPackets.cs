namespace KotorCoop.Protocol;

// The ground-item channel, plus the per-instance ItemIdentity/GroundItem shapes it and
// the loot channels share. Matches PacketCodec.Items.cs.

/// <summary>
/// What is true of ONE item instance rather than of its blueprint.
///
/// A resref names a stock item. Everything a player has done to their copy of it — spending
/// its charges, stacking twenty of them, having it identified, bolting three upgrades into a
/// lightsaber — lives on the live <c>CSWSItem</c> and used to be dropped on the floor of the
/// sending instance and never picked up again. The receiving side re-created the blueprint
/// and handed the other player a pristine one.
///
/// The four fields are the four the engine itself persists per instance and re-applies when
/// it rebuilds an item from a template: <c>SaveItem</c> (0x0055CCD0) writes exactly
/// StackSize / Charges / MaxCharges / Upgrades / the Identified bit alongside the blueprint
/// name, and the upgrade workbench (0x006C5620) rebuilds an upgraded weapon by calling
/// <c>LoadFromTemplate</c> and then copying the Upgrades dword and the Stolen bit onto the
/// new body. Reproducing that sequence is therefore the engine's own idiom, not an
/// invention. See docs/research/item-identity.md.
///
/// ⚠ <see cref="Flags"/> bit 0 is <see cref="FlagKnown"/> and it is load-bearing: an
/// identity nobody managed to read must be distinguishable from a genuinely stock item, or
/// "I could not read it" would silently mean "stack of zero, no charges, unidentified". A
/// zeroed identity therefore means UNKNOWN and the receiver leaves the fresh item exactly as
/// the engine made it.
/// </summary>
public readonly record struct ItemIdentity(
    ushort StackSize, byte Charges, byte MaxCharges, uint Upgrades, byte Flags)
{
    /// <summary>This identity was actually read off a live item. Without it every other
    /// field means nothing — see the type remarks.</summary>
    public const byte FlagKnown = 1 << 0;

    /// <summary><c>CSWSItem+0x288</c> bit 0, the bit <c>SetIdentified</c> (0x00554170) toggles.</summary>
    public const byte FlagIdentified = 1 << 1;

    /// <summary><c>CSWSItem+0x288</c> bit 5, saved as the GFF <c>Stolen</c> field.</summary>
    public const byte FlagStolen = 1 << 2;

    /// <summary><c>CSWSItem+0x288</c> bit 6, saved as the GFF <c>NonEquippable</c> field.</summary>
    public const byte FlagNonEquippable = 1 << 3;

    /// <summary>Nothing was read. The receiver must not apply any of it.</summary>
    public static ItemIdentity Unknown => default;

    /// <summary>
    /// The ONLY <see cref="Flags"/> bits that may take part in deciding whether two items of the
    /// same resref are "the same stack" — <see cref="FlagKnown"/> (meta: an identity nobody read
    /// is not the same fact as one that was) plus <see cref="FlagStolen"/>.
    ///
    /// ⚠ <see cref="FlagIdentified"/> and <see cref="FlagNonEquippable"/> are DELIBERATELY absent,
    /// and the authority is the engine's own merge predicate rather than taste. `CSWSItem::MergeItem`
    /// 0x00553F90's first act is a call to the "may these two stacks combine?" test at
    /// <c>0x00553CF0</c>, whose entire flags comparison is two instructions
    /// (<c>0x00553D60</c>-<c>0x00553D6E</c>):
    ///
    /// <code>
    /// mov  eax, dword ptr [ebp + 0x288]   ; the other item's flags dword
    /// xor  eax, dword ptr [ebx + 0x288]   ; XOR with this item's
    /// test al, 0x20                       ; ...and test ONE bit: 0x20 == bit 5 == Stolen
    /// jne  &lt;no merge&gt;
    /// </code>
    ///
    /// Bit 0 (Identified) and bit 6 (NonEquippable) are never looked at, so the engine merges two
    /// stacks that differ in them — and a key that encodes them claims a distinction the game does
    /// not make. Live evidence that this is not academic:
    /// docs/research/shared-inventory-v2.md §13.2 watched `g_i_medeqpmnt01` sit in the party pool
    /// under `/f1` and `/f3` at the same time (a save's items are identified, a freshly-created
    /// one was not), the engine merge units between the two, and the sync channel read that
    /// migration as a fabricated remove on one key and a fabricated add on the other.
    ///
    /// Charges/MaxCharges/Upgrades are NOT flags and are unaffected by this mask; the same
    /// predicate does compare <c>+0x294</c> (Upgrades) and <c>+0x258</c> (Charges) exactly.
    /// (It does not compare <c>+0x25C</c>, MaxCharges — recorded in
    /// docs/research/shared-inventory-v2.md §14 as a known, unobserved residual rather than
    /// changed on speculation, since removing it from the key would also remove it from the
    /// apply payload.)
    /// </summary>
    public const byte StackIdentityFlagMask = FlagKnown | FlagStolen;

    /// <summary><see cref="Flags"/> reduced to <see cref="StackIdentityFlagMask"/> — what a
    /// stack key may carry. See that constant's remarks for the engine evidence.</summary>
    public byte StackIdentityFlags => (byte)(Flags & StackIdentityFlagMask);

    public bool IsKnown => (Flags & FlagKnown) != 0;

    public bool Identified => (Flags & FlagIdentified) != 0;

    public bool Stolen => (Flags & FlagStolen) != 0;

    public bool NonEquippable => (Flags & FlagNonEquippable) != 0;
}

/// <summary>
/// An item lying on the ground, shared by every player in the module. The id is minted by
/// the dropping peer as (peerId &lt;&lt; 16 | counter) so ids never collide across peers.
///
/// <paramref name="Identity"/> is what makes the item THIS item rather than a fresh one off
/// the blueprint. It defaults to <see cref="ItemIdentity.Unknown"/> because a drop can be
/// announced by something that never held the live object (the test harness names a
/// blueprint), and "unknown" has to stay a first-class answer.
/// </summary>
public sealed record GroundItem(
    uint ItemId, string Resref, string Module, Vector3f Position,
    ItemIdentity Identity = default);

public enum ItemOpKind : byte
{
    Drop = 1,
    Remove = 2,
}

/// <summary>
/// A ground-item change: someone dropped an item, or someone picked one up. Reliable via
/// retransmit-until-ack, exactly like <see cref="FlagOpPacket"/>. For a Remove, only
/// <paramref name="ItemId"/> matters and the item fields are empty.
/// </summary>
public sealed record ItemOpPacket(
    uint OriginPeerId,
    uint OpSequence,
    ItemOpKind Kind,
    GroundItem Item);

public sealed record ItemAckPacket(uint OpSequence);

public sealed record ItemSyncRequestPacket(uint PeerId, uint KnownVersion);

public sealed record ItemSnapshotPacket(uint Version, IReadOnlyList<GroundItem> Items);
