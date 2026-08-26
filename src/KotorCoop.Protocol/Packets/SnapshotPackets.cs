namespace KotorCoop.Protocol;

// The per-tick avatar state channel. Matches PacketCodec.Snapshot.cs.

public sealed record StateUpdatePacket(uint PeerId, uint Sequence, long ClientTimeMs, PlayerState State);

/// <summary>One peer's entry inside a <see cref="WorldSnapshotPacket"/>.</summary>
public sealed record SnapshotEntry(uint PeerId, string Name, PlayerState State);

/// <summary>
/// The host's authoritative view, broadcast every tick. <paramref name="FlagVersion"/>,
/// <paramref name="ItemVersion"/>, <paramref name="JournalVersion"/> and
/// <paramref name="LootVersion"/> are the cheap tripwires that tell a client one of its world
/// stores is stale, so full state only crosses the wire when it has actually diverged.
///
/// Each version rides beside its store's <c>Digest</c> — <paramref name="FlagDigest"/>,
/// <paramref name="ItemDigest"/>, <paramref name="JournalDigest"/>,
/// <paramref name="LootDigest"/> — an order-independent content fingerprint (see
/// <c>KotorCoop.Session.EntryDigest</c>). The version alone has a blind spot: it counts local
/// CHANGES, not content, so two stores that diverged by the same count during an outage can
/// tie and never ask for a resync (docs/research/disconnect-reconnect.md §4.1). The digest
/// depends on what is actually held, so a mismatch there is caught even when the counters
/// agree. Container loot did not carry a version at all before this — see §4.8 — so
/// <paramref name="LootVersion"/> and <paramref name="LootDigest"/> are both new.
/// </summary>
public sealed record WorldSnapshotPacket(
    long ServerTimeMs,
    uint FlagVersion,
    uint ItemVersion,
    uint JournalVersion,
    uint LootVersion,
    ulong FlagDigest,
    ulong ItemDigest,
    ulong JournalDigest,
    ulong LootDigest,
    IReadOnlyList<SnapshotEntry> Entries);
