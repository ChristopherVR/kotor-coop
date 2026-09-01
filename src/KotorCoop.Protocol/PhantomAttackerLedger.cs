namespace KotorCoop.Protocol;

/// <summary>
/// One attacker's contribution to a cumulative damage total: an engine object id (the same
/// number in every instance) and the running cumulative amount attributed to it. Carried on
/// <see cref="PhantomDamagePacket"/> (up to <see cref="ProtocolConstants.MaxPhantomAttackerEntries"/>
/// per packet) and by every fixed-capacity table that tracks it — the plugin's native capture
/// (<c>PhantomAttackerTable</c>) and this file's own applied-side ledger.
/// </summary>
public readonly record struct PhantomAttackerEntry(uint AttackerId, uint CumulativeDamage);

/// <summary>
/// Fixed-capacity (<see cref="Capacity"/>) per-attacker APPLIED ledger for one victim: for
/// each attacker's cumulative OWED total, tracks how much has already been accounted for (
/// applied as real damage, or deliberately absorbed), and answers "how much is newly owed
/// right now" per attacker so the caller can decide, PER ATTACKER, whether to absorb it (a
/// local fight already covers it) or apply it for real.
///
/// This is what replaces <c>PhantomHitHook</c>'s single "last attacker" billing on the VICTIM
/// side: with one id per delta, a window that mixes a locally-fought attacker with a mirrored
/// one had to bill the WHOLE delta to one or the other — either double-counting (billed to
/// the local one, which is also swinging for real) or dropping (billed to the mirrored one,
/// absorbed, while the local one's own contribution never gets applied at all). Splitting the
/// delta per attacker id removes the ambiguity entirely. See
/// docs/research/peer-damage-ledger.md §7.
///
/// ⚠ MONOTONIC PER ATTACKER, LIKE EVERY OTHER CUMULATIVE CHANNEL IN THIS CODEBASE. An owed
/// total that regresses for one attacker id (that sender's own ledger restarted — a module
/// change, a re-attach) is never billed as negative damage: <see cref="Reconcile"/> adopts the
/// lower number as the new baseline for that id alone, the same "restart, don't refuse
/// forever" rule <c>IncomingDamageController</c>'s legacy scalar path already applies
/// channel-wide.
///
/// Eviction, when full and a genuinely new attacker id needs a slot: the entry with the
/// SMALLEST applied total is replaced — protecting the biggest known contributors, the same
/// trade the plugin's own capture-side table and <c>DamageDealtTable</c>'s larger capacity
/// both make.
///
/// No allocation after construction: every method touches only the two fixed arrays, so this
/// is safe to hold as a long-lived field on the plugin's per-frame controller.
/// </summary>
public sealed class PhantomAttackerAppliedLedger
{
    public const int Capacity = 4;

    private readonly uint[] _id = new uint[Capacity];
    private readonly uint[] _applied = new uint[Capacity];

    /// <summary>
    /// For each of <paramref name="owed"/>'s entries, writes how much is newly owed THIS PASS
    /// into the matching slot of <paramref name="deltas"/> (owed - already accounted, floored
    /// at 0) — without marking anything accounted yet. The caller classifies each entry
    /// (absorb or apply) and calls <see cref="MarkAccounted"/> only for the ones it actually
    /// acted on this pass; an entry the caller skips (a conversation hold, a downed player)
    /// keeps its full delta owed for the next pass.
    ///
    /// A sender's own total restarting BELOW what this ledger already has recorded for that id
    /// is a RESET, not a regression: <see cref="MarkAccounted"/> adopts the lower number
    /// immediately (delta 0 this pass) so future growth from the new, lower baseline counts,
    /// rather than either replaying the id's whole history or refusing every future hit from it.
    /// </summary>
    public void Reconcile(IReadOnlyList<PhantomAttackerEntry> owed, Span<uint> deltas)
    {
        int count = Math.Min(owed.Count, deltas.Length);
        for (int i = 0; i < count; i++)
        {
            uint id = owed[i].AttackerId;
            uint cumulative = owed[i].CumulativeDamage;

            if (id == 0)
            {
                deltas[i] = 0;
                continue;
            }

            uint applied = AppliedFor(id);

            if (cumulative < applied)
            {
                MarkAccounted(id, cumulative);
                deltas[i] = 0;
                continue;
            }

            deltas[i] = cumulative - applied;
        }
    }

    /// <summary>How much of <paramref name="attackerId"/>'s owed total this ledger has already
    /// accounted for. 0 for an id it has never seen (or id 0, which is never a real attacker).</summary>
    public uint AppliedFor(uint attackerId)
    {
        if (attackerId == 0)
        {
            return 0;
        }

        for (int i = 0; i < Capacity; i++)
        {
            if (_id[i] == attackerId)
            {
                return _applied[i];
            }
        }

        return 0;
    }

    /// <summary>
    /// Marks <paramref name="attackerId"/>'s owed total accounted for UP TO
    /// <paramref name="accountedCumulative"/> — call for what was actually applied as real
    /// damage AND for what was deliberately absorbed (both settle the debt so the same delta
    /// is never re-offered next pass); never call with a smaller figure than
    /// <see cref="AppliedFor"/> already returns, or a real delta will be silently discarded.
    /// </summary>
    public void MarkAccounted(uint attackerId, uint accountedCumulative)
    {
        if (attackerId == 0)
        {
            return;
        }

        for (int i = 0; i < Capacity; i++)
        {
            if (_id[i] == attackerId)
            {
                _applied[i] = accountedCumulative;
                return;
            }
        }

        int slot = FindSlotFor();
        _id[slot] = attackerId;
        _applied[slot] = accountedCumulative;
    }

    private int FindSlotFor()
    {
        int free = -1;
        int evict = 0;
        uint evictTotal = uint.MaxValue;

        for (int i = 0; i < Capacity; i++)
        {
            if (_id[i] == 0 && free < 0)
            {
                free = i;
            }

            if (_applied[i] < evictTotal)
            {
                evictTotal = _applied[i];
                evict = i;
            }
        }

        return free >= 0 ? free : evict;
    }

    /// <summary>Drops every tracked attacker. Called on a module change/re-attach, the same
    /// moment the legacy scalar applied total is dropped — a carried-over per-attacker total
    /// would be credited against a completely different session.</summary>
    public void Clear()
    {
        Array.Clear(_id);
        Array.Clear(_applied);
    }
}
