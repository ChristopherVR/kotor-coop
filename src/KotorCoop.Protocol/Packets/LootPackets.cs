namespace KotorCoop.Protocol;

// Shared-container loot and owner-authoritative enemy corpse loot. Matches
// PacketCodec.Loot.cs.

/// <summary>
/// One container-loot removal: item <paramref name="ItemId"/> is no longer inside container
/// <paramref name="ContainerId"/>. Both are engine object ids, usable as cross-instance keys
/// because both games load the same save and the engine assigns ids deterministically in GIT
/// order — the same property doors and enemies rely on.
/// </summary>
public readonly record struct ContainerLootRemoval(uint ContainerId, uint ItemId);

/// <summary>
/// A container-loot removal op: the host took <paramref name="ItemId"/> out of container
/// <paramref name="ContainerId"/>, so it must vanish from that container on the client too (or
/// it could be picked up twice). Reliable via retransmit-until-ack, exactly like
/// <see cref="ItemOpPacket"/>, but broadcast host -&gt; clients rather than client -&gt; host.
/// </summary>
public sealed record ContainerLootPacket(
    uint OriginPeerId, uint OpSequence, uint ContainerId, uint ItemId,
    string Module = "");

/// <summary>
/// A client's acknowledgement of a <see cref="ContainerLootPacket"/>. Carries the acking
/// peer id so the host, which broadcasts to several clients, can tell whose ack this is.
/// </summary>
public sealed record ContainerLootAckPacket(uint PeerId, uint OpSequence, string Module = "");

/// <summary>
/// client -&gt; host: my container-loot version or digest does not match the host's latest
/// <see cref="WorldSnapshotPacket"/>, please resync. <paramref name="KnownVersion"/> and
/// <paramref name="KnownDigest"/> are carried for the same reason the other three sync-request
/// packets carry a known version — diagnostic, not consulted by the handler, which always
/// re-offers everything it knows rather than trying to compute a partial diff.
/// </summary>
public sealed record LootSyncRequestPacket(
    uint PeerId, uint KnownVersion, ulong KnownDigest, string Module = "");

/// <summary>One item off a materialised enemy's real corpse, by resref plus the same identity
/// fields <see cref="GroundItem"/> carries. No object id: the receiving side spawns a fresh
/// local item, it does not need to name the sender's.</summary>
public readonly record struct MaterialisedLootItem(string Resref, ItemIdentity Identity);

/// <summary>
/// The full real loot of one owner-authoritative enemy that just died, for a mirroring peer's
/// materialised stand-in to receive. See <see cref="PacketType.MaterialisedLoot"/> for why this
/// is host -&gt; clients only and reliable. <paramref name="OwnerEnemyId"/> is the id in the
/// HOST's own id space — the same id <c>CheatController.GiveLoot</c> already expects and
/// translates via <c>MaterialisedEnemies.ToLocal</c>.
/// </summary>
public sealed record MaterialisedLootPacket(
    uint OriginPeerId, uint OpSequence, uint OwnerEnemyId,
    IReadOnlyList<MaterialisedLootItem> Items, string Module = "")
{
    /// <summary>A corpse's worth of loot fits comfortably inside one datagram at this cap —
    /// asserted by <c>PacketCodecTests</c>. A count past it is truncated at capture time, not
    /// silently dropped whole.</summary>
    public const int MaxItems = 8;
}

/// <summary>A client's acknowledgement of a <see cref="MaterialisedLootPacket"/>. Carries the
/// acking peer id so the host, which broadcasts to several clients, can tell whose ack this
/// is — exactly like <see cref="ContainerLootAckPacket"/>.</summary>
public sealed record MaterialisedLootAckPacket(uint PeerId, uint OpSequence, string Module = "");
