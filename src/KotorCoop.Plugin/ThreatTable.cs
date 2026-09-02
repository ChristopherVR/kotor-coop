namespace KotorCoop.Plugin;

/// <summary>
/// Pure per-creature threat bookkeeping for THREAT_RETARGET (coop.ini <c>threat_retarget</c>,
/// docs/research/peer-damage-ledger.md §9 — the follow-up to §8's finding that the engine's
/// own <c>+0x158</c>/<c>+0x160</c> write reaches no native retargeting logic for an
/// already-engaged creature). No engine calls, no unsafe pointers, no allocation on a hot
/// path: the only caller is <see cref="PeerDamageController.ApplyOwed"/>'s existing per-row
/// loop, which already runs at most every 10 frames and only when a peer's forwarded damage
/// has actually landed — the same cadence the "combat: applied N of a peer's damage" log line
/// already runs at.
///
/// A creature's threat is kept PER ATTACKER, decayed LINEARLY to zero over a fixed window
/// (default 10s) rather than an exponential half-life: a straight ramp-down is simplest to
/// reason about and to unit test with fixed clock ticks, and the choice of curve only affects
/// how quickly an attacker who stops swinging loses priority — not whether the feature works.
///
/// ⚠ WHAT THIS DOES NOT TRACK. Only damage this instance can attribute to a specific attacker
/// id feeds the table — in practice, a peer's forwarded damage via
/// <see cref="SharedBlockView.SoleRemotePhantomId"/> (unambiguous only with exactly one remote
/// peer connected, the same limit <c>damage_creator_attribution</c> already documents — see
/// peer-damage-ledger.md §8.6/§8.9). The HOST's own local PC/party damage to the same
/// creature is NOT recorded here: no cheap existing ground-truth read for "how much did the
/// local player just deal to this specific enemy" was found on the host side (
/// <c>PhantomHitHook.Dealt</c> is only ever populated while <c>block.DamageDealtEnabled</c> is
/// true, which is precisely the signal that THIS instance is NOT the fight's owner — see
/// <c>PeerDamageController.RefreshAttackers</c>'s own remarks). So the current target's own
/// threat reads 0 unless it happens to also be a tracked peer attacker id, which in practice
/// means the retarget decision reduces to "does the top peer attacker clear the absolute
/// minimum", not a true margin over the incumbent's own contribution. Documented rather than
/// guessed at — see peer-damage-ledger.md §9 for the write-up.
/// </summary>
internal sealed class ThreatTable(double windowSeconds = ThreatTable.DefaultWindowSeconds)
{
    public const double DefaultWindowSeconds = 10.0;

    private sealed class AttackerEntry
    {
        public double Threat;
        public double LastSeconds;
    }

    private readonly double _windowSeconds = windowSeconds;
    private readonly Dictionary<uint, Dictionary<uint, AttackerEntry>> _byCreature = new();
    private readonly Dictionary<uint, double> _lastRetargetSeconds = new();

    /// <summary>Banks <paramref name="amount"/> of threat for <paramref name="attackerId"/>
    /// against <paramref name="creatureId"/>, decaying whatever was already there first. A
    /// zero id or non-positive amount is ignored rather than creating a dead row.</summary>
    public void AddThreat(uint creatureId, uint attackerId, double amount, double nowSeconds)
    {
        if (creatureId == 0 || attackerId == 0 || amount <= 0)
        {
            return;
        }

        if (!_byCreature.TryGetValue(creatureId, out Dictionary<uint, AttackerEntry>? byAttacker))
        {
            byAttacker = new Dictionary<uint, AttackerEntry>();
            _byCreature[creatureId] = byAttacker;
        }

        if (!byAttacker.TryGetValue(attackerId, out AttackerEntry? entry))
        {
            entry = new AttackerEntry();
            byAttacker[attackerId] = entry;
        }

        entry.Threat = Decayed(entry.Threat, entry.LastSeconds, nowSeconds) + amount;
        entry.LastSeconds = nowSeconds;
    }

    /// <summary>The current (decayed) threat one attacker holds against one creature. 0 for
    /// an attacker never recorded, or one whose window has fully elapsed.</summary>
    public double GetThreat(uint creatureId, uint attackerId, double nowSeconds)
    {
        if (creatureId != 0 && attackerId != 0
            && _byCreature.TryGetValue(creatureId, out Dictionary<uint, AttackerEntry>? byAttacker)
            && byAttacker.TryGetValue(attackerId, out AttackerEntry? entry))
        {
            return Decayed(entry.Threat, entry.LastSeconds, nowSeconds);
        }

        return 0;
    }

    /// <summary>The attacker with the highest CURRENT (decayed) threat against a creature.
    /// False when the creature has no tracked attacker at all, or every one has decayed to
    /// zero.</summary>
    public bool TryGetTopAttacker(uint creatureId, double nowSeconds, out uint attackerId, out double threat)
    {
        attackerId = 0;
        threat = 0;

        if (!_byCreature.TryGetValue(creatureId, out Dictionary<uint, AttackerEntry>? byAttacker))
        {
            return false;
        }

        foreach ((uint id, AttackerEntry entry) in byAttacker)
        {
            double t = Decayed(entry.Threat, entry.LastSeconds, nowSeconds);
            if (t > threat)
            {
                threat = t;
                attackerId = id;
            }
        }

        return attackerId != 0;
    }

    /// <summary>Seconds since this creature was last retargeted through this table, or
    /// <see cref="double.PositiveInfinity"/> if it never has been — so a fresh creature is
    /// never held back by a cooldown it has no history to justify.</summary>
    public double SecondsSinceLastRetarget(uint creatureId, double nowSeconds) =>
        _lastRetargetSeconds.TryGetValue(creatureId, out double last)
            ? nowSeconds - last
            : double.PositiveInfinity;

    /// <summary>Records that a retarget was just issued for this creature, arming the cooldown
    /// <see cref="SecondsSinceLastRetarget"/> reports back.</summary>
    public void MarkRetargeted(uint creatureId, double nowSeconds) => _lastRetargetSeconds[creatureId] = nowSeconds;

    /// <summary>Drops one creature's whole row — its fight is over (death, or the id left the
    /// owner's world). Never required for correctness (a stale row simply decays to zero and
    /// sits idle), but keeps the table from growing for the rest of a long module.</summary>
    public void Forget(uint creatureId)
    {
        _byCreature.Remove(creatureId);
        _lastRetargetSeconds.Remove(creatureId);
    }

    /// <summary>Drops everything — a module change reassigns every object id, so nothing here
    /// still refers to the creature or attacker it named.</summary>
    public void Clear()
    {
        _byCreature.Clear();
        _lastRetargetSeconds.Clear();
    }

    private double Decayed(double threat, double lastSeconds, double nowSeconds)
    {
        double elapsed = nowSeconds - lastSeconds;
        if (elapsed <= 0)
        {
            return threat;
        }

        if (elapsed >= _windowSeconds)
        {
            return 0;
        }

        return threat * (1.0 - (elapsed / _windowSeconds));
    }
}
