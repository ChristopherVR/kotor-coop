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
/// Every report is bound to the authenticated process incarnation, the current native
/// world and its verified occupant generation. A real load or occupant replacement
/// retires the old run and clears its owed damage/healing before another body can use
/// the slot. Ordinary retransmission preserves the cumulative high-water mark.
///
/// HEALS (2026-09-18) ride the same channel as a second cumulative total per roster slot,
/// keyed by <see cref="CompanionDamageSlots.HealSlot"/> — bit 7 of the slot field, which a
/// 1..9 roster slot never uses. A medpac or Force heal a client lands on its released
/// companion is a RISE in that total; the arithmetic below does not care which kind a key
/// names, so both are banked, de-duplicated, generation-reset and plausibility-gated by the
/// identical code. The host's plugin decodes the key and spends a heal row through
/// <c>EffectHeal</c> instead of <c>EffectDamage</c>. The reason a heal is not a NEGATIVE
/// damage delta: the damage total is rise-only by rule 1, and a decrease is exactly what a
/// stale datagram looks like — a signed net would have to trust ordering the wire does not
/// give it.
/// </summary>
public sealed class CompanionDamageLedger
{
    /// <summary>The last total seen from one peer for one roster slot, and the generation it
    /// belonged to.</summary>
    private readonly Dictionary<(uint Peer, uint Incarnation, uint RosterSlotPlusOne), (uint Generation, uint Total)> _seen = [];

    /// <summary>Everything every peer has ever dealt to one companion's roster slot.</summary>
    private readonly Dictionary<uint, uint> _owed = [];
    private PartyRoster? _roster;
    private readonly DamageGenerationWatch _generationWatch = new();

    public void ObserveRoster(PartyRoster roster)
    {
        if (_roster is { } before)
        {
            if (before.HostLoadSerial != roster.HostLoadSerial || before.HostNativeEpoch != roster.HostNativeEpoch)
            { _generationWatch.RetireCurrentWorld(); _seen.Clear(); _owed.Clear(); }
            else for (int slot = 0; slot < 9; slot++)
                if (before.OccupantFor(slot).Generation != roster.OccupantFor(slot).Generation)
                {
                    _generationWatch.RetireCurrentWorld();
                    uint key = (uint)slot + 1;
                    _owed.Remove(key); _owed.Remove(CompanionDamageSlots.HealSlot(key));
                    foreach (var seen in _seen.Keys.Where(k => CompanionDamageSlots.RosterSlotPlusOne(k.RosterSlotPlusOne) == key).ToArray()) _seen.Remove(seen);
                }
        }
        _roster = roster;
    }

    /// <summary>How many rises were refused as implausible (PeerDamageLedger.MaxPlausibleRise).</summary>
    public uint RefusedRises { get; private set; }

    /// <summary>The most recent refusal — who reported it, which roster slot, by how much.</summary>
    public (uint Peer, uint RosterSlotPlusOne, uint Delta) LastRefused { get; private set; }

    /// <summary>Total damage banked against one roster slot, for tests and diagnostics.</summary>
    public uint OwedFor(uint rosterSlotPlusOne) =>
        _owed.TryGetValue(rosterSlotPlusOne, out uint owed) ? owed : 0;

    /// <summary>Total HEALING banked against one roster slot — the heal-flagged key's total.</summary>
    public uint HealOwedFor(uint rosterSlotPlusOne) => OwedFor(CompanionDamageSlots.HealSlot(rosterSlotPlusOne));

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
            if (_roster is { } roster && (report.OccupantGeneration != roster.OccupantFor((int)CompanionDamageSlots.RosterSlotPlusOne(report.RosterSlotPlusOne) - 1).Generation
                || report.HostLoadSerial != roster.HostLoadSerial || report.HostNativeEpoch != roster.HostNativeEpoch)) continue;
            if (!_generationWatch.Bind(report.PeerId, report.Incarnation)
                || !_generationWatch.Observe(report.PeerId, report.Incarnation, report.Generation)) continue;
            // A heal row still has to name a roster slot: the flag alone is no companion.
            if (CompanionDamageSlots.RosterSlotPlusOne(report.RosterSlotPlusOne) == 0)
            {
                continue;
            }

            (uint Peer, uint Incarnation, uint RosterSlotPlusOne) key = (report.PeerId, report.Incarnation, report.RosterSlotPlusOne);

            uint previous = 0;
            if (_seen.TryGetValue(key, out (uint Generation, uint Total) seen))
            {
                // Same run: only the rise is new. A different generation means the sender
                // restarted counting, so everything it now reports is new damage.
                previous = seen.Generation == report.Generation ? seen.Total : 0;
            }

            if (report.CumulativeDamage > previous || seen.Generation != report.Generation)
                _seen[key] = (report.Generation, report.CumulativeDamage);

            if (report.CumulativeDamage <= previous)
            {
                continue;
            }

            uint delta = report.CumulativeDamage - previous;

            // Same gate as PeerDamageLedger, same reason: a rise no fight could produce is a
            // misread effect, and banked against a companion it would wipe the host's copy.
            if (delta > PeerDamageLedger.MaxPlausibleRise)
            {
                RefusedRises++;
                LastRefused = (report.PeerId, report.RosterSlotPlusOne, delta);
                continue;
            }

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
            table.Add(new CompanionDamageEntry(entry.Key, entry.Value, _roster?.OccupantFor((int)CompanionDamageSlots.RosterSlotPlusOne(entry.Key) - 1).Generation ?? 0));
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
