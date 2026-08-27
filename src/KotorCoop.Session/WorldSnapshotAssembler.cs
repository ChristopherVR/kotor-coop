using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient's per-tick accumulator for a paged <see cref="WorldSnapshotPacket"/>. Pure and
/// side-effect-free, exactly like <see cref="WorldSnapshotPaging"/> on the sending side, so the
/// out-of-order/partial-arrival arithmetic can be unit-tested without a socket. See
/// docs/research/lag-audit.md.
///
/// Pages of one tick share <see cref="WorldSnapshotPacket.ServerTimeMs"/> as their tick id —
/// the host computes it once per <c>Tick()</c> call and stamps every page of that tick with the
/// same value, so it is already a free grouping key with no extra wire bytes. A page's own
/// entries are ready to use the instant it arrives: <see cref="Apply"/> hands THAT page's
/// entries straight back, unconditionally, never gated on any sibling page of the same tick —
/// so a peer named only on a late or lost page 1 never holds page 0's peers hostage. Only the
/// roster-wide decisions — pruning a peer who is genuinely gone, trusting the flag/item/
/// journal/loot tripwires — wait for the fuller picture <see cref="TickComplete"/> reports, and
/// even those simply skip a tick rather than block forever if a page never arrives: the very
/// next tick starts a brand new cycle regardless.
/// </summary>
public sealed class WorldSnapshotAssembler
{
    private long _tickId;
    private bool _hasTick;
    private byte _pageCount = 1;
    private readonly HashSet<byte> _pagesSeen = [];
    private readonly HashSet<uint> _present = [];

    /// <summary>True once the local player's own entry has been seen on some page of the
    /// current tick.</summary>
    public bool SelfListed { get; private set; }

    /// <summary>True once every page 0..PageCount-1 of the current tick has arrived at least
    /// once. A page that never arrives simply leaves this false for the rest of that tick —
    /// see the type's remarks.</summary>
    public bool TickComplete { get; private set; }

    /// <summary>Peer ids (never including the local player) seen across every page of the
    /// CURRENT tick so far. Meaningful for a roster prune only once <see cref="TickComplete"/>
    /// is true — before that, a peer simply not yet seen on an unarrived page would read as
    /// gone.</summary>
    public IReadOnlyCollection<uint> PresentPeerIds => _present;

    /// <summary>
    /// Feeds one received page in. Returns that page's own entries, unfiltered and ready to
    /// apply immediately — see the type's remarks on why this never waits for a sibling page.
    /// A page whose <see cref="WorldSnapshotPacket.ServerTimeMs"/> differs from whatever this
    /// accumulator last saw starts a brand new cycle, discarding whatever partial picture the
    /// previous tick left behind.
    /// </summary>
    public IReadOnlyList<SnapshotEntry> Apply(WorldSnapshotPacket page, uint localPeerId)
    {
        if (!_hasTick || page.ServerTimeMs != _tickId)
        {
            _hasTick = true;
            _tickId = page.ServerTimeMs;
            _pageCount = (byte)Math.Max((byte)1, page.PageCount);
            _pagesSeen.Clear();
            _present.Clear();
            SelfListed = false;
        }

        _pagesSeen.Add(page.PageIndex);

        foreach (SnapshotEntry entry in page.Entries)
        {
            if (entry.PeerId == localPeerId)
            {
                SelfListed = true;
                continue;
            }

            _present.Add(entry.PeerId);
        }

        TickComplete = _pagesSeen.Count >= _pageCount;
        return page.Entries;
    }
}
