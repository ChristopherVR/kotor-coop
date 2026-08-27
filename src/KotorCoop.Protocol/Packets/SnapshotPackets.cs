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
///
/// A full lobby's aggregate entry size can exceed one datagram, so one tick's broadcast may be
/// split across several packets sharing the same <paramref name="ServerTimeMs"/> — which
/// already IS a per-tick id, computed once in <c>CoopHost.Tick()</c> and stamped on every page
/// of that tick, so it costs no extra wire bytes as a grouping key the way
/// <see cref="CreatureLivenessPacket"/>'s separate <c>Generation</c> counter does.
/// <paramref name="PageIndex"/>/<paramref name="PageCount"/> say where this packet sits
/// (0-based index, total count). Unlike <see cref="CreatureLivenessPacket"/>'s pages, EACH
/// PAGE HERE IS SELF-CONTAINED: every entry in it is a complete, ready-to-apply
/// <see cref="SnapshotEntry"/>, so a receiver applies a page's positions the instant it
/// arrives rather than waiting to accumulate the whole tick — a peer named only on a late or
/// lost page 1 must not freeze everyone whose entry landed safely on page 0.
///
/// <paramref name="FlagVersion"/>/<paramref name="ItemVersion"/>/<paramref
/// name="JournalVersion"/>/<paramref name="LootVersion"/> and their four digests ride ON PAGE
/// 0 ONLY — <see cref="KotorCoop.Protocol.PacketCodec"/> does not even put them on the wire for
/// a later page, and a decoded page whose <see cref="PageIndex"/> is not 0 reports all seven as
/// 0/default. A receiver must never read these off anything but page 0; if page 0 itself is
/// lost, the tripwire simply waits for next tick's fresh page 0, the same "a dropped update is
/// superseded next tick" tolerance the rest of this channel already relies on.
/// </summary>
public sealed record WorldSnapshotPacket(
    long ServerTimeMs,
    byte PageIndex,
    byte PageCount,
    uint FlagVersion,
    uint ItemVersion,
    uint JournalVersion,
    uint LootVersion,
    ulong FlagDigest,
    ulong ItemDigest,
    ulong JournalDigest,
    ulong LootDigest,
    IReadOnlyList<SnapshotEntry> Entries)
{
    /// <summary>See <see cref="ProtocolConstants.MaxSnapshotEntriesPerPage"/>.</summary>
    public const int MaxEntriesPerPage = ProtocolConstants.MaxSnapshotEntriesPerPage;

    /// <summary>
    /// A tick never plans for more pages than this. <see cref="ProtocolConstants.MaxPeers"/> at
    /// <see cref="MaxEntriesPerPage"/> per page already needs at most two, so four is
    /// generous headroom rather than an observed need — a tick that still cannot fit within it
    /// drops the excess entries and logs, exactly like the old whole-snapshot guard did, rather
    /// than growing the send unboundedly.
    /// </summary>
    public const int MaxPages = 4;
}
