using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Turns every peer's running total of damage TAKEN by one of its RELEASED companions into
/// ONE monotonic total per NPC roster slot, for the host's own engine to spend as real damage
/// on its authoritative companion body — the companion-damage sibling of
/// <see cref="PeerDamageLedger"/>. Read docs/research/peer-damage-ledger.md first: the short
/// version is that a companion's HEALTH cannot carry this either, for the identical reason an
/// enemy's could not — it is a level, sampled, and the host's own mirror write
/// (<c>PartyPresenceController.DriveOne</c>) overwrites the client's copy with the host's
/// higher figure on the very next pass, so a client-side hit had to win a race against that
/// write to be seen at all. A cumulative sum of damage TAKEN cannot be raced, because nothing
/// ever reads it back out of either game.
///
/// Two rules, both borrowed from <see cref="PeerDamageLedger"/> and for the same reasons:
///
/// 1. <b>Bank the INCREASE, per (peer, roster slot).</b> Summing a peer's current total
///    directly would let a stale or reordered datagram walk the bank backwards.
/// 2. <b>Ignore a DECREASE, unless the sender's own GENERATION says otherwise.</b> The
///    generation is the sender's own reset marker — the plugin's companion-damage watch
///    restarting (a module change, a re-attach, a fresh release after being reclaimed) — and
///    without it, rule 1 would read that restart as staleness and ignore everything the
///    companion does from then on.
///
/// UNLIKE <see cref="PeerDamageLedger"/>, there is no MODULE concept here and so no epoch to
/// publish back: an NPC roster slot names the same companion identity for the whole session,
/// module transition or not — <c>PartyPresenceController</c> already resolves companions by
/// roster slot for exactly that reason (identical across two instances of the same save). A
/// module change on the CLIENT bumps ITS OWN generation (the companion is reclaimed the moment
/// the world changes — see docs/research/companion-state-sync.md §13), which rule 2 already
/// handles; there is nothing module-shaped for the host side to track.
/// </summary>
public sealed class CompanionDamageLedger
{
    /// <summary>The last total seen from one peer for one roster slot, and the generation it
    /// belonged to.</summary>
    private readonly Dictionary<(uint Peer, uint RosterSlotPlusOne), (uint Generation, uint Total)> _seen = [];

    /// <summary>Everything every peer has ever dealt to one companion's roster slot.</summary>
    private readonly Dictionary<uint, uint> _owed = [];

    /// <summary>Total damage banked against one roster slot, for tests and diagnostics.</summary>
    public uint OwedFor(uint rosterSlotPlusOne) =>
        _owed.TryGetValue(rosterSlotPlusOne, out uint owed) ? owed : 0;

    /// <summary>
    /// Folds this tick's reports in and returns the full per-roster-slot owed table.
    ///
    /// The result is a LEVEL, not a delta: the plugin keeps its own applied total and spends
    /// the difference, so republishing the same table costs nothing and a pass the plugin
    /// misses is picked up whole by the next one.
    /// </summary>
    public IReadOnlyList<CompanionDamageEntry> Merge(IReadOnlyList<PeerCompanionDamage> reports)
    {
        foreach (PeerCompanionDamage report in reports)
        {
            if (report.RosterSlotPlusOne == 0)
            {
                continue;
            }

            (uint Peer, uint RosterSlotPlusOne) key = (report.PeerId, report.RosterSlotPlusOne);

            uint previous = 0;
            if (_seen.TryGetValue(key, out (uint Generation, uint Total) seen))
            {
                // Same run: only the rise is new. A different generation means the sender
                // restarted counting, so everything it now reports is new damage.
                previous = seen.Generation == report.Generation ? seen.Total : 0;
            }

            _seen[key] = (report.Generation, report.CumulativeDamage);

            if (report.CumulativeDamage <= previous)
            {
                continue;
            }

            uint delta = report.CumulativeDamage - previous;
            uint owed = OwedFor(report.RosterSlotPlusOne);

            // Saturating, not wrapping — a wrap would read as a reset on the plugin's side and
            // hand the whole fight back to it a second time.
            _owed[report.RosterSlotPlusOne] = delta > uint.MaxValue - owed ? uint.MaxValue : owed + delta;
        }

        if (_owed.Count == 0)
        {
            return [];
        }

        List<CompanionDamageEntry> table = new(_owed.Count);
        foreach (KeyValuePair<uint, uint> entry in _owed)
        {
            table.Add(new CompanionDamageEntry(entry.Key, entry.Value));
        }

        return table;
    }

    // ⚠ DELIBERATELY NO "forget this peer" method. CoopHost.Connection.cs's own Bye handler
    // comment (the "AN IDENTITY MUST LIVE AS LONG AS THE COUNTERS KEYED ON IT" reasoning next
    // to ForgetPhantomDamageFor) found this the hard way for the sibling channels: a peer that
    // quits to the menu and reconnects under the SAME peer id and the SAME generation must have
    // its report read as "nothing new" against what THIS ledger already banked, not as a fresh
    // identity with no history — forgetting the seen row here would double-credit the whole of
    // its prior contribution the moment it reconnects. A genuine reset always carries its own
    // NEW generation (the plugin's watch restarting), which rule 2 already handles without any
    // help from a peer-departure hook.
}
