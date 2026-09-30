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
/// Both the host's own PC/party hits and every peer's accepted damage feed this table.
/// Peer identities survive the agent merge and resolve to their current phantom body.
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
    private double _lastPruneSeconds = double.NegativeInfinity;

    /// <summary>How many creatures currently hold a row — for tests and the live dump.</summary>
    public int CreatureCount => _byCreature.Count;

    /// <summary>
    /// Copies every creature id currently holding at least one row into <paramref
    /// name="destination"/>, stopping at its length, and returns how many were copied. Lets a
    /// caller re-evaluate every tracked creature on a pass — not only the ones a fresh hit
    /// happened to touch that same pass — which is what turns a retarget order into something
    /// that re-asserts itself promptly once the engine's own AI flips a creature back, rather
    /// than waiting for the next peer hit to notice (docs/research/peer-damage-ledger.md §9,
    /// stage 2 of the sticky-aggro plan; see <c>PeerDamageController.ReassertThreat</c>). No
    /// allocation: <c>Dictionary&lt;TKey,TValue&gt;.KeyCollection</c>'s enumerator is a struct.
    /// </summary>
    public int CopyCreatureIds(Span<uint> destination)
    {
        int count = 0;
        foreach (uint creatureId in _byCreature.Keys)
        {
            if (count >= destination.Length)
            {
                break;
            }

            destination[count++] = creatureId;
        }

        return count;
    }

    /// <summary>Banks <paramref name="amount"/> of threat for <paramref name="attackerId"/>
    /// against <paramref name="creatureId"/>, decaying whatever was already there first. A
    /// zero id or non-positive amount is ignored rather than creating a dead row. Also the
    /// table's housekeeping moment: at most once per window it drops every creature whose
    /// rows have all decayed to zero (<see cref="PruneDecayed"/>), so a long module with many
    /// fights does not keep a row per creature ever hit — <see cref="Forget"/> exists for the
    /// same purpose but nothing in the controllers calls it, and a self-bounding table needs
    /// no caller to remember to.</summary>
    public void AddThreat(uint creatureId, uint attackerId, double amount, double nowSeconds)
    {
        if (creatureId == 0 || attackerId == 0 || amount <= 0)
        {
            return;
        }

        if (nowSeconds - _lastPruneSeconds >= _windowSeconds)
        {
            _lastPruneSeconds = nowSeconds;
            PruneDecayed(nowSeconds);
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
    public bool TryGetTopAttacker(uint creatureId, double nowSeconds, out uint attackerId, out double threat, Func<uint, bool>? eligible = null)
    {
        attackerId = 0;
        threat = 0;

        if (!_byCreature.TryGetValue(creatureId, out Dictionary<uint, AttackerEntry>? byAttacker))
        {
            return false;
        }

        foreach ((uint id, AttackerEntry entry) in byAttacker)
        {
            if (eligible is not null && !eligible(id)) continue;
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

    /// <summary>
    /// Drops every creature whose every attacker row has fully decayed AND whose retarget
    /// cooldown (if it ever had one) has also run out — rows that read as 0 / "no cooldown"
    /// anyway, so removing them changes no answer this table gives, only its size. Returns how
    /// many creatures were dropped. Called from <see cref="AddThreat"/> at most once per
    /// window; public so the bound is unit-tested directly.
    /// </summary>
    public int PruneDecayed(double nowSeconds)
    {
        List<uint>? dead = null;
        foreach ((uint creatureId, Dictionary<uint, AttackerEntry> byAttacker) in _byCreature)
        {
            bool anyLive = false;
            foreach (AttackerEntry entry in byAttacker.Values)
            {
                if (Decayed(entry.Threat, entry.LastSeconds, nowSeconds) > 0)
                {
                    anyLive = true;
                    break;
                }
            }

            if (anyLive || SecondsSinceLastRetarget(creatureId, nowSeconds) < _windowSeconds)
            {
                continue;
            }

            (dead ??= []).Add(creatureId);
        }

        if (dead is null)
        {
            return 0;
        }

        foreach (uint creatureId in dead)
        {
            Forget(creatureId);
        }

        return dead.Count;
    }

    /// <summary>Drops everything — a module change reassigns every object id, so nothing here
    /// still refers to the creature or attacker it named.</summary>
    public void Clear()
    {
        _byCreature.Clear();
        _lastRetargetSeconds.Clear();
        _lastPruneSeconds = double.NegativeInfinity;
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
