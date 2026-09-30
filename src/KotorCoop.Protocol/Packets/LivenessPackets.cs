namespace KotorCoop.Protocol;

// The full per-module creature liveness publish. Matches PacketCodec.Liveness.cs.

/// <summary>
/// One creature's liveness as the host's engine currently sees it — not health, just whether
/// a body exists to be seen. The reconciliation this exists for
/// (docs/research/creature-liveness.md) only ever cares about three things: is there a live
/// body, a corpse, or nothing at all.
/// </summary>
public enum CreatureLivenessState : byte
{
    /// <summary>The engine still owns a live, non-corpse body under this id.</summary>
    Alive = 0,

    /// <summary>The engine owns the body but its hit points read at or below zero — a
    /// corpse, still resolvable, exactly what <see cref="EnemyState.Dead"/> already means for
    /// the 32-slot combat census.</summary>
    Dead = 1,

    /// <summary>Nothing answers to this id here any more (<c>DestroyObject</c>, a cutscene
    /// that removed the actor, or a script kill that also destroyed the body) — no corpse to
    /// mirror, unlike <see cref="Dead"/>.</summary>
    Gone = 2,
}

/// <summary>
/// One entry of a <see cref="CreatureLivenessPacket"/>: an id in the HOST's own id space.
/// Always GIT-identical — a receiving peer translates a materialised stand-in's LOCAL id via
/// its own <c>MaterialisedEnemies.ToLocal</c> before comparing against this, never the other
/// way around (the host never sees a peer's stand-in ids at all).
/// </summary>
public readonly record struct CreatureLivenessEntry(uint ObjectId, CreatureLivenessState State);

/// <summary>
/// host -&gt; clients: every creature the host's engine currently knows about in
/// <paramref name="Module"/> — not just the 32-slot combat census <see cref="EnemySnapshotPacket"/>
/// carries — so a mirroring peer can notice a body left alive on one side and dead or
/// destroyed on the other. See docs/research/creature-liveness.md.
///
/// A module's full creature list can exceed one datagram, so a publish cycle may be split
/// across several packets sharing the same <paramref name="Generation"/>:
/// <paramref name="PageIndex"/>/<paramref name="PageCount"/> say where this packet sits
/// (0-based index, total count), and <paramref name="Digest"/> is the SAME
/// order-independent fingerprint over every entry in the WHOLE cycle (computed the way
/// <c>KotorCoop.Session.EntryDigest</c> computes every other store's — Protocol itself takes
/// no dependency on Session, so this type only ever carries the already-computed value) on
/// every page, so a receiver that has gathered every page can confirm its accumulated set is
/// complete rather than trusting the last packet blindly.
/// </summary>
public sealed record CreatureLivenessPacket(
    long ServerTimeMs, string Module, ulong Digest, uint Generation,
    byte PageIndex, byte PageCount, IReadOnlyList<CreatureLivenessEntry> Entries)
{
    public uint HostSessionId { get; init; }
    public uint HostLoadSerial { get; init; }
    public uint HostNativeEpoch { get; init; }
    /// <summary>See <see cref="ProtocolConstants.MaxCreatureLivenessEntries"/>.</summary>
    public const int MaxEntriesPerPacket = ProtocolConstants.MaxCreatureLivenessEntries;
}
