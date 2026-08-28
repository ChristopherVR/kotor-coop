namespace KotorCoop.Protocol;

// The host-authoritative v2 redesign of the party pool and store channels
// (party_pool_sync=2/store_sync=2), replacing the bidirectional op-log
// (PartyPoolPackets.cs/StorePackets.cs, still used unchanged by mode 1) with an absolute
// snapshot the host alone publishes plus client-originated intents the host alone applies. See
// docs/research/shared-inventory-v2.md §2/§3.1. Matches PacketCodec.SharedInventoryV2.cs.
//
// Both the party-pool and store shapes below reuse PartyPoolStackKey/StoreStackKey and
// PartyPoolOpKind/StoreOpKind byte-for-byte from the v1 files (docs/research/shared-inventory-v2.md
// §2.3/§3.1) — identity and "which direction" do not change meaning between the op-log and the
// intent/snapshot design, only who is allowed to assert what.

/// <summary>One key's absolute count as the host's own engine currently holds it.</summary>
public readonly record struct PartyPoolSnapshotEntry(PartyPoolStackKey Key, int Count);

/// <summary>
/// host -&gt; clients: the party pool's ABSOLUTE stock, paged like <see cref="CreatureLivenessPacket"/>
/// but deliberately simpler (docs/research/shared-inventory-v2.md §3.1): no cross-page digest — a
/// receiver accumulates pages by <paramref name="Generation"/> and commits the union only once
/// every <c>PageIndex</c> from 0 to <c>PageCount-1</c> for that generation has arrived, discarding
/// a part-collected generation outright the moment a NEWER one starts arriving. Safe to simplify
/// this way because §2.7 already makes a stale/incomplete reassembly harmless: the very next cycle
/// is always a full, current resend regardless. Never relayed and never carries an origin peer —
/// the host is the only party that ever publishes one.
/// </summary>
public sealed record PartyPoolSnapshotPacketV2(
    long ServerTimeMs, uint Generation, byte PageIndex, byte PageCount,
    IReadOnlyList<PartyPoolSnapshotEntry> Entries)
{
    /// <summary>See <see cref="ProtocolConstants.MaxPartyPoolSnapshotEntriesPerPacket"/>.</summary>
    public const int MaxEntriesPerPacket = ProtocolConstants.MaxPartyPoolSnapshotEntriesPerPacket;
}

/// <summary>
/// client -&gt; host ONLY: a bounded, one-shot REQUEST about a transaction the sending client's own
/// engine already performed — NOT a claim about the pool's current total.
/// <paramref name="Kind"/> is restricted in practice to <see cref="PartyPoolOpKind.Add"/>/
/// <see cref="PartyPoolOpKind.Remove"/> — <see cref="PartyPoolOpKind.Set"/> is a v1-only,
/// op-log-relative concept with no meaning for a bounded request and is refused if ever sent.
/// <paramref name="IntentSerial"/> is minted by the SENDING client (a plain per-client counter,
/// unrelated to and never colliding with any other peer's — the host dedups by
/// (<paramref name="PeerId"/>, <paramref name="IntentSerial"/>), the same bounded-ring shape
/// <see cref="PartyPoolOpPacket"/>'s own dedup already uses). Reliable via retransmit-until-ack;
/// the host applies it to its OWN engine at most once and its effect reaches every peer, including
/// the sender, only through the next <see cref="PartyPoolSnapshotPacketV2"/> — never relayed. See
/// docs/research/shared-inventory-v2.md §2.2 for why this structurally cannot amplify.
/// </summary>
public sealed record PartyPoolIntentPacketV2(
    uint PeerId, uint IntentSerial, PartyPoolOpKind Kind, PartyPoolStackKey Key, ushort Count);

/// <summary>host -&gt; the sending client: acks (accepted) or refuses one
/// <see cref="PartyPoolIntentPacketV2.IntentSerial"/>. The client drops it from its own
/// "still outstanding" reconcile-target adjustment either way — a refusal is not retried under
/// the same serial (docs/research/shared-inventory-v2.md §2.5).</summary>
public sealed record PartyPoolIntentAckPacketV2(uint PeerId, uint IntentSerial, bool Accepted);

// ---- the store's own mirror, widened by StoreObjectId exactly like StoreStackKey widens
// PartyPoolStackKey (docs/research/store-sync.md §2.1) ------------------------------------

/// <summary>One (store, key) pair's absolute count as the host's own engine currently holds it.</summary>
public readonly record struct StoreSnapshotEntry(StoreStackKey Key, int Count);

/// <summary>host -&gt; clients: every store's ABSOLUTE stock the host currently has a belief for
/// (docs/research/shared-inventory-v2.md §2.6 — a store neither known by a prior host scan nor by
/// an accepted intent contributes nothing), one cycle possibly naming several stores since each
/// entry's own key already carries its <c>StoreObjectId</c>. Same paging/digest-free shape as
/// <see cref="PartyPoolSnapshotPacketV2"/>.</summary>
public sealed record StoreSnapshotPacketV2(
    long ServerTimeMs, uint Generation, byte PageIndex, byte PageCount,
    IReadOnlyList<StoreSnapshotEntry> Entries)
{
    /// <summary>See <see cref="ProtocolConstants.MaxStoreSnapshotEntriesPerPacket"/>.</summary>
    public const int MaxEntriesPerPacket = ProtocolConstants.MaxStoreSnapshotEntriesPerPacket;
}

/// <summary>client -&gt; host ONLY: "I bought/sold this many units of this stack in this store" —
/// the store's mirror of <see cref="PartyPoolIntentPacketV2"/>, identical reasoning
/// (<paramref name="Kind"/> restricted to <see cref="StoreOpKind.Add"/>/
/// <see cref="StoreOpKind.Remove"/>; <see cref="StoreOpKind.Set"/> is refused).</summary>
public sealed record StoreIntentPacketV2(
    uint PeerId, uint IntentSerial, StoreOpKind Kind, StoreStackKey Key, ushort Count);

/// <summary>host -&gt; the sending client: acks (or refuses) one
/// <see cref="StoreIntentPacketV2.IntentSerial"/> — the store's mirror of
/// <see cref="PartyPoolIntentAckPacketV2"/>.</summary>
public sealed record StoreIntentAckPacketV2(uint PeerId, uint IntentSerial, bool Accepted);
