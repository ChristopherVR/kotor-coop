namespace KotorCoop.Protocol;

// The shared party item-pool channel (CSWPartyTable+0x118 -> CItemRepository on the engine
// side; docs/research/party-pool-sync.md). Matches PacketCodec.PartyPool.cs.
//
// Unlike ContainerLootRemoval (object-id keyed, because both instances load the same save and
// therefore already share GIT-placed ids), a party-pool item is created by hand on ONE
// instance and never gets the same engine object id on the other — there is no shared id to
// key on. So this channel is keyed by CONTENT instead: a resref plus the four ItemIdentity
// fields that make one stack distinguishable from another of the same blueprint (charges, max
// charges, installed upgrades, the identified/stolen/non-equippable bits) — everything
// ItemIdentity carries EXCEPT StackSize, which becomes the op's own count delta. Two
// content-identical stacks are, by construction, indistinguishable to a peer either way, so
// aggregating them under one key loses nothing a player could tell apart.

/// <summary>
/// Everything about a party-pool stack that is NOT its quantity: the blueprint plus the four
/// <see cref="ItemIdentity"/> fields that decide whether two items of the same resref are
/// really the same stack (a plain vibroblade and an upgraded one never merge). Value-equal by
/// construction (a record struct), so it is usable as a dictionary key as-is.
/// </summary>
public readonly record struct PartyPoolStackKey(
    string Resref, byte Charges, byte MaxCharges, uint Upgrades, byte Flags)
{
    /// <summary>The four non-stack fields of a live item's identity, keyed the same way this
    /// struct is — the bridge-side helper that turns a scanned <see cref="ItemIdentity"/> into
    /// a wire key.
    ///
    /// ⚠ The flags byte is NARROWED to <see cref="ItemIdentity.StackIdentityFlagMask"/> on the
    /// way in, so a key can never encode a distinction the engine's own merge predicate does not
    /// make (see that constant's remarks for the disassembly, and
    /// docs/research/shared-inventory-v2.md §13.2/§14 for the live failure that motivated it).
    /// Normalising HERE, at construction, rather than in a custom equality is deliberate: both
    /// instances then derive the byte-identical key for the same stack, which keeps
    /// <c>EntryDigest</c> and every dictionary lookup deterministic across the wire.</summary>
    public static PartyPoolStackKey From(string resref, ItemIdentity identity) =>
        new(resref, identity.Charges, identity.MaxCharges, identity.Upgrades,
            identity.StackIdentityFlags);
}

public enum PartyPoolOpKind : byte
{
    /// <summary>This many more units of this stack entered the party pool. Applied ADDITIVELY
    /// against the receiver's current total.</summary>
    Add = 1,

    /// <summary>This many units of this stack left the party pool. Applied ADDITIVELY
    /// (subtracted, floored at zero) against the receiver's current total.</summary>
    Remove = 2,

    /// <summary>
    /// The pool holds EXACTLY <see cref="PartyPoolOpPacket.CountDelta"/> units of this stack —
    /// an ABSOLUTE declaration, never additive. Host-only, used exclusively for resync
    /// (<c>ResyncPartyPool</c>): a peer whose digest disagrees may already hold a different
    /// nonzero total for this key, so replaying the history as more <see cref="Add"/>s would
    /// double-count it. There is no per-contributor history to replay anyway (the store only
    /// keeps aggregate totals) — a resync is always a synthetic "the pool holds N of this, full
    /// stop" fact, authored under the host's own peer id.
    /// </summary>
    Set = 3,
}

/// <summary>
/// A party-pool change: <paramref name="CountDelta"/> units of <paramref name="Key"/> were
/// added to, removed from (or, for a <see cref="PartyPoolOpKind.Set"/> resync, now equal) the
/// shared stash. Reliable via retransmit-until-ack, the same shape as
/// <see cref="ContainerLootPacket"/>, but — unlike a container removal — an Add/Remove is NOT
/// collapsible against another op for the same key: two adds of the same stack are two
/// separate, cumulative events (someone deposited five, then three more), so a sender must not
/// deduplicate in flight the way <see cref="ContainerLootPacket"/>'s does.
/// </summary>
public sealed record PartyPoolOpPacket(
    uint OriginPeerId, uint OpSequence, PartyPoolOpKind Kind, PartyPoolStackKey Key, ushort CountDelta);

/// <summary>A peer's acknowledgement of a <see cref="PartyPoolOpPacket"/>. Carries the acking
/// peer id so the host, which broadcasts to several clients, can tell whose ack this is —
/// exactly like <see cref="ContainerLootAckPacket"/>.</summary>
public sealed record PartyPoolAckPacket(uint PeerId, uint OpSequence);

/// <summary>
/// client -&gt; host: my party-pool version or digest does not match the host's latest
/// <see cref="WorldSnapshotPacket"/>, please resync. Mirrors <see cref="LootSyncRequestPacket"/>
/// exactly, for the same reason: this channel has no separate snapshot packet, so its "full
/// resync" is re-offering every currently-held op.
/// </summary>
public sealed record PartyPoolSyncRequestPacket(uint PeerId, uint KnownVersion, ulong KnownDigest);
