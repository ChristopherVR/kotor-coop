using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient's per-tick accumulator for a paged <see cref="EnemySnapshotPacket"/>. Pure and
/// side-effect-free, exactly like <see cref="WorldSnapshotAssembler"/> on the identical wire
/// shape, so the out-of-order/partial-arrival arithmetic can be unit-tested without a socket.
///
/// Pages of one tick share <see cref="EnemySnapshotPacket.ServerTimeMs"/> as their tick id — the
/// host stamps every page of one <c>BroadcastEnemies</c> call with the same value, so it is
/// already a free grouping key with no extra wire bytes. A page's own entries are ready to use
/// the instant it arrives: <see cref="Apply"/> hands THAT page's entries straight back,
/// unconditionally, never gated on any sibling page of the same tick — an enemy named only on a
/// late or lost page 1 never holds page 0's enemies hostage. Only the roster-wide decision —
/// pruning an enemy that has genuinely left the census (out of combat, out of range, dead and
/// aged out, the owner changed module) — waits for the fuller picture <see cref="TickComplete"/>
/// reports, and even that simply skips a tick rather than blocking forever if a page never
/// arrives: the very next tick starts a brand new cycle regardless.
/// </summary>
public sealed class EnemySnapshotAssembler
{
    private long _tickId;
    private bool _hasTick;
    private byte _pageCount = 1;
    private readonly HashSet<byte> _pagesSeen = [];
    private readonly HashSet<uint> _present = [];

    /// <summary>True once every page 0..PageCount-1 of the current tick has arrived at least
    /// once. A page that never arrives simply leaves this false for the rest of that tick — see
    /// the type's remarks.</summary>
    public bool TickComplete { get; private set; }

    /// <summary>Enemy object ids seen across every page of the CURRENT tick so far. Meaningful
    /// for a prune only once <see cref="TickComplete"/> is true — before that, an enemy simply
    /// not yet seen on an unarrived page would read as gone.</summary>
    public IReadOnlyCollection<uint> PresentIds => _present;

    /// <summary>
    /// Feeds one received page in. Returns that page's own entries, unfiltered and ready to
    /// apply immediately — see the type's remarks on why this never waits for a sibling page. A
    /// page whose <see cref="EnemySnapshotPacket.ServerTimeMs"/> differs from whatever this
    /// accumulator last saw starts a brand new cycle, discarding whatever partial picture the
    /// previous tick left behind.
    /// </summary>
    public IReadOnlyList<EnemyState> Apply(EnemySnapshotPacket page)
    {
        if (!_hasTick || page.ServerTimeMs != _tickId)
        {
            _hasTick = true;
            _tickId = page.ServerTimeMs;
            _pageCount = Math.Max((byte)1, page.PageCount);
            _pagesSeen.Clear();
            _present.Clear();
        }

        _pagesSeen.Add(page.PageIndex);

        foreach (EnemyState entry in page.Enemies)
        {
            _present.Add(entry.ObjectId);
        }

        TickComplete = _pagesSeen.Count >= _pageCount;
        return page.Enemies;
    }
}
