using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Turns every peer's running total of damage dealt into ONE monotonic total per enemy for
/// the owner's game to spend.
///
/// This is the merge that replaced taking <c>min()</c> of what each instance said an
/// enemy's HEALTH was. Health could not carry a peer's contribution: it had to be sampled,
/// and the owner's own mirror wrote its value back onto the peer's copy between samples, so
/// a hit had to win a race to be reported at all — measured live, a client attacking one
/// soldier for ninety seconds took it from 15 hit points to 11 and no further. It also
/// could not say WHO dealt what, which is the thing this makes possible.
///
/// Three rules, and all three exist because of a specific way the naive version breaks:
///
/// 1. <b>Sum the INCREASES, per peer.</b> Adding up the peers' current totals would mean a
///    peer dropping out (or its report going stale) instantly un-hurts the enemy. Each
///    peer's rise is banked into a per-enemy total that only ever goes up.
/// 2. <b>Ignore a DECREASE, unless the generation says otherwise.</b> Datagrams are
///    unreliable and unordered, so a smaller number is usually an older one.
/// 3. <b>A new generation resets that peer's history.</b> A peer that leaves the module and
///    comes back restarts its own counting at zero. Without rule 3, rule 2 would read that
///    as permanently stale and everything it did from then on would be ignored — the
///    quietest possible way to lose a whole player's fight.
///
/// Module-scoped, because engine object ids are assigned per module load: a total collected
/// in <c>tar_m02aa</c> would be spent on whatever creature holds that number in
/// <c>end_m01aa</c>.
/// </summary>
public sealed class PeerDamageLedger
{
    /// <summary>The last total seen from one peer for one enemy, and the generation it
    /// belonged to.</summary>
    private readonly Dictionary<(uint Peer, uint ObjectId), (uint Generation, uint Total)> _seen = [];

    /// <summary>Everything every peer has ever dealt to one enemy, in this module.</summary>
    private readonly Dictionary<uint, uint> _owed = [];

    private string _module = string.Empty;

    /// <summary>
    /// The module these totals were collected in, and the epoch handed to the plugin so
    /// both halves restart together.
    /// </summary>
    public string Module => _module;

    public uint Epoch { get; private set; }

    /// <summary>Total damage banked against one enemy, for tests and diagnostics.</summary>
    public uint OwedFor(uint objectId) => _owed.TryGetValue(objectId, out uint owed) ? owed : 0;

    /// <summary>
    /// Folds this tick's reports in and returns the full per-enemy owed table.
    ///
    /// The result is a LEVEL, not a delta: the plugin keeps its own applied total and
    /// spends the difference, so republishing the same table costs nothing and a pass the
    /// plugin misses is picked up by the next one.
    /// </summary>
    public IReadOnlyList<EnemyDamageEntry> Merge(string module, IReadOnlyList<PeerEnemyDamage> reports)
    {
        EnterModule(module);

        foreach (PeerEnemyDamage report in reports)
        {
            if (report.ObjectId == 0)
            {
                continue;
            }

            (uint Peer, uint ObjectId) key = (report.PeerId, report.ObjectId);

            uint previous = 0;
            if (_seen.TryGetValue(key, out (uint Generation, uint Total) seen))
            {
                // Same run of ids: only the rise is new. A different run means the peer
                // started counting again, so everything it now reports is new damage.
                previous = seen.Generation == report.Generation ? seen.Total : 0;
            }

            _seen[key] = (report.Generation, report.CumulativeDamage);

            if (report.CumulativeDamage <= previous)
            {
                continue;
            }

            uint delta = report.CumulativeDamage - previous;
            uint owed = OwedFor(report.ObjectId);

            // Saturating. A wrap would read as a reset on the plugin's side and hand the
            // peers' whole fight back to it a second time.
            _owed[report.ObjectId] = delta > uint.MaxValue - owed ? uint.MaxValue : owed + delta;
        }

        if (_owed.Count == 0)
        {
            return [];
        }

        // ⚠ Ordered so that anything a peer is reporting RIGHT NOW comes first.
        //
        // The owed table only grows: an enemy stays in it for the rest of the module even
        // once it is dead and paid for, while the plugin's side of it is a fixed 32 slots.
        // Left in dictionary order, a long session in one module would eventually push the
        // fight actually happening past the truncation and quietly stop counting it — which
        // is precisely the failure this whole channel exists to remove. A peer only reports
        // enemies it has damaged and not yet seen die, so "reported this tick" is exactly
        // the set that still matters.
        HashSet<uint> live = [];
        foreach (PeerEnemyDamage report in reports)
        {
            live.Add(report.ObjectId);
        }

        List<EnemyDamageEntry> table = new(_owed.Count);

        foreach (KeyValuePair<uint, uint> entry in _owed)
        {
            if (live.Contains(entry.Key))
            {
                table.Add(new EnemyDamageEntry(entry.Key, entry.Value));
            }
        }

        foreach (KeyValuePair<uint, uint> entry in _owed)
        {
            if (!live.Contains(entry.Key))
            {
                table.Add(new EnemyDamageEntry(entry.Key, entry.Value));
            }
        }

        return table;
    }

    /// <summary>
    /// Drops everything when the ids stop meaning what they meant. Unknown ("") is treated
    /// as its own module rather than as "no opinion": this side WRITES to the game, and
    /// spending a total against ids we cannot vouch for is exactly the mistake the module
    /// gate exists to prevent.
    /// </summary>
    private void EnterModule(string module)
    {
        if (string.Equals(module, _module, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _module = module;
        _seen.Clear();
        _owed.Clear();
        Epoch = EpochOf(module);
    }

    /// <summary>
    /// FNV-1a of the module name, as the epoch the plugin compares against. A hash rather
    /// than a counter so that an agent restarting mid-module lands on the same value and
    /// does not needlessly wipe the plugin's applied totals; 0 is reserved for "no epoch
    /// yet", so a name that hashes to it is nudged.
    /// </summary>
    public static uint EpochOf(string module)
    {
        uint hash = 2166136261;

        foreach (char c in module)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619;
        }

        return hash == 0 ? 1 : hash;
    }
}
