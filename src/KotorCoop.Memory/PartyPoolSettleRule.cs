using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// A NEW world's party pool is not an observation until it has stood still for one sweep
/// interval. Pure state machine behind <c>KotorGameBridge.TryScanPartyPoolTotals</c>, kept
/// separate so the rule can be pinned without an engine.
///
/// Live 2026-09-02 (docs/research/shared-inventory-v2.md §17): the host's very first sweep
/// after its own in-world reload — the same second its plugin logged "the world is up and on
/// screen" — resolved FIVE pool entries, two of them grenade stacks no pool on either side ever
/// held; the next sweep, and every reload of the same save before and after, showed three. That
/// first reading went out as the host snapshot and the client reconciled a phantom ion grenade
/// into its own pool. The engine mechanism is unproven (a load-time pass of saved items through
/// the repository is the obvious candidate) and this rule is deliberately mechanism-free.
/// </summary>
internal sealed class PartyPoolSettleRule
{
    private Dictionary<PartyPoolStackKey, int>? _first;

    /// <summary>True from <see cref="Reset"/> until a sweep has been confirmed.</summary>
    public bool Settling { get; private set; }

    /// <summary>True once a first sweep is being held — the caller must keep the confirming
    /// sweep a full interval away rather than re-reading immediately.</summary>
    public bool Holding => Settling && _first is not null;

    /// <summary>The world was replaced; forget any held sweep and start over.</summary>
    public void Reset()
    {
        Settling = true;
        _first = null;
    }

    /// <summary>Offer a complete sweep. True when it may be published as the current
    /// observation; false (with <paramref name="note"/> saying why) when it is being held.</summary>
    public bool Accept(Dictionary<PartyPoolStackKey, int> scan, out string note)
    {
        if (!Settling)
        {
            note = string.Empty;
            return true;
        }

        Dictionary<PartyPoolStackKey, int>? previous = _first;
        _first = scan;
        if (previous is null)
        {
            note = "party pool: first sweep of a new world - holding it until a second sweep agrees";
            return false;
        }

        if (!SameTotals(previous, scan))
        {
            note = "party pool: the new world's pool is still changing between sweeps - not an observation yet";
            return false;
        }

        Settling = false;
        _first = null;
        note = string.Empty;
        return true;
    }

    private static bool SameTotals(Dictionary<PartyPoolStackKey, int> a, Dictionary<PartyPoolStackKey, int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<PartyPoolStackKey, int> kv in a)
        {
            if (!b.TryGetValue(kv.Key, out int other) || other != kv.Value)
            {
                return false;
            }
        }

        return true;
    }
}
