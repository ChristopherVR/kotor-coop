namespace KotorCoop.Protocol;

// The merchant/store stock channel (CSWSStore+0x230's CItemRepository; docs/research/
// store-sync.md). Matches PacketCodec.Store.cs.
//
// Widens PartyPoolStackKey (docs/research/party-pool-sync.md) by one leading field: a store is
// a real, GIT-placed CGameObject with a stable object id shared by both instances (VERIFIED,
// docs/research/store-sync.md §1.2 — CServerExoAppInternal::GetStoreByGameObjectID resolves one
// by bare id at any time), so the op-log key is (store object id, resref+identity) rather than
// content alone. Quantity per key is still an aggregate int, not object-id keyed per item — a
// store's stock is a multiset exactly like the party pool's, for the identical reason (a stack
// can grow AND shrink, repeatedly, from either instance).

/// <summary>
/// Everything about a store's stock entry that is NOT its quantity: which store, plus the four
/// <see cref="ItemIdentity"/> fields that decide whether two items of the same resref are really
/// the same stack. Value-equal by construction (a record struct), usable as a dictionary key.
/// </summary>
public readonly record struct StoreStackKey(
    uint StoreObjectId, string Resref, byte Charges, byte MaxCharges, uint Upgrades, byte Flags)
{
    /// <summary>The four non-stack fields of a live item's identity, keyed the same way this
    /// struct is — the bridge-side helper that turns a scanned <see cref="ItemIdentity"/> into a
    /// wire key for a specific store.</summary>
    public static StoreStackKey From(uint storeObjectId, string resref, ItemIdentity identity) =>
        new(storeObjectId, resref, identity.Charges, identity.MaxCharges, identity.Upgrades, identity.Flags);
}

public enum StoreOpKind : byte
{
    /// <summary>This many more units of this stack entered the store (a sell). Applied
    /// ADDITIVELY against the receiver's current total.</summary>
    Add = 1,

    /// <summary>This many units of this stack left the store (a buy). Applied ADDITIVELY
    /// (subtracted, floored at zero) against the receiver's current total.</summary>
    Remove = 2,

    /// <summary>The store holds EXACTLY <see cref="StoreOpPacket.CountDelta"/> units of this
    /// stack — an ABSOLUTE declaration, never additive. Host-only, used exclusively for resync
    /// (<c>ResyncStore</c>) — see <see cref="PartyPoolOpKind.Set"/>'s identical reasoning, which
    /// applies unchanged here: there is no per-contributor history to replay, and a peer being
    /// resynced may already hold a different total for this key.</summary>
    Set = 3,
}

/// <summary>
/// A store stock change: <paramref name="CountDelta"/> units of <paramref name="Key"/> were
/// added to, removed from (or, for a <see cref="StoreOpKind.Set"/> resync, now equal) the named
/// store's stock. Reliable via retransmit-until-ack. Not collapsible against another op for the
/// same key in flight — two sells of the same stack are two distinct, cumulative events — the
/// identical non-dedup rule <see cref="PartyPoolOpPacket"/> follows.
/// </summary>
public sealed record StoreOpPacket(
    uint OriginPeerId, uint OpSequence, StoreOpKind Kind, StoreStackKey Key, ushort CountDelta);

/// <summary>A peer's acknowledgement of a <see cref="StoreOpPacket"/>. Carries the acking peer id
/// so a broadcaster with several recipients can tell whose ack this is.</summary>
public sealed record StoreAckPacket(uint PeerId, uint OpSequence);

/// <summary>
/// client -&gt; host: my store version/digest does not match the host's, please resync. Mirrors
/// <see cref="PartyPoolSyncRequestPacket"/> exactly — this channel has no separate snapshot
/// packet either, so its "full resync" is re-offering every currently-held op.
/// </summary>
public sealed record StoreSyncRequestPacket(uint PeerId, uint KnownVersion, ulong KnownDigest);
