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
///
/// ⚠ DELIBERATELY NOT PRUNED WHEN A PEER LEAVES (docs/review-seamless-2026-09-18.md §2 asked
/// whether <see cref="_seen"/> "grows per rejoin"). It does not: a rejoining player reclaims
/// its peer id (<c>CoopHost.HandleHello</c>'s retired-identity reclaim, and the long argument
/// in <c>CoopHost.HandleBye</c>), so its rows are the SAME keys, overwritten in place. Dropping
/// a departed peer's rows would reintroduce the exact bug that argument exists to prevent: a
/// player who quits to the menu and comes back in the same module reports the same cumulative
/// total under the same generation, and with no baseline row left, rule 1 would bank all of it
/// as new damage a second time. The table is bounded by (peers ever admitted this module) ×
/// (enemies they hit this module) and cleared on every module change — a few kilobytes at the
/// very worst — which is the right trade against a silent double-credit.
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

    /// <summary>
    /// The largest rise one peer's total against one enemy may show between two ticks.
    /// Ticks are ~100 ms apart and a blackout of a minute at the game's best sustained damage
    /// stays in the low thousands, while an enemy that could absorb this much would be dead
    /// many times over — so a bigger jump is a misread effect on the peer's side. The number
    /// is deliberately generous: refusing a real hit is the failure this channel exists to
    /// remove, refusing a corrupt one is the failure the 2026-09-02 Vulkar showed.
    /// </summary>
    public const uint MaxPlausibleRise = 10_000;

    /// <summary>How many rises were refused as implausible, for the pump's one-line notice.</summary>
    public uint RefusedRises { get; private set; }

    /// <summary>The most recent refusal — who reported it, against what, by how much.</summary>
    public (uint Peer, uint ObjectId, uint Delta) LastRefused { get; private set; }

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

            // ⚠ A rise no fight could produce is a corrupt number, not damage (2026-09-02: a
            // client's row for one Vulkar rose by 1,634,994,432 between two ticks; banked, the
            // owner's per-pass clamp fed it to the creature 200 at a time until it died of
            // nothing). The baseline above is still moved to the reported total, so a real
            // hit after the corrupt one is credited from there, and the refusal is counted
            // for the pump to say so once. The peer-side hook has the same gate at the source.
            if (delta > MaxPlausibleRise)
            {
                RefusedRises++;
                LastRefused = (report.PeerId, report.ObjectId, delta);
                continue;
            }

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
