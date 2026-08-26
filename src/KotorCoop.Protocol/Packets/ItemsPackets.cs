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
