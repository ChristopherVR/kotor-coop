namespace KotorCoop.Plugin;

/// <summary>
/// Per-phantom-slot capture: a fixed 4-entry table of (attackerId, cumulativeDamage) pairs,
/// touched from inside <c>PhantomHitHook.OnDamage</c> — the same "no allocation, no engine
/// calls" hot path <c>DamageDealtTable</c> already runs on, just capacity 4 instead of 32
/// because this rides the SharedBlock and the wire PER PHANTOM SLOT (up to
/// <see cref="SharedBlock.SlotCapacity"/> of these exist at once), not once per instance.
///
/// Eviction, when full and a genuinely new attacker lands a hit: the entry with the SMALLEST
/// current total is replaced. A window with four established attackers and a fifth, glancing
/// one drops the newcomer's contribution from THIS breakdown (it still counts inside the
/// legacy scalar cumulative <c>PhantomHitHook</c> keeps alongside this, which this table can
/// never regress below) rather than evicting whoever has actually been fighting longest —
/// protecting the biggest known contributors, the same trade <c>DamageDealtTable</c>'s own
/// (much larger) capacity limit makes.
/// </summary>
internal sealed class PhantomAttackerTable
{
    public const int Capacity = 4;

    private readonly uint[] _id = new uint[Capacity];
    private readonly uint[] _total = new uint[Capacity];

    public uint IdAt(int index) => _id[index];

    public uint TotalAt(int index) => _total[index];

    /// <summary>Adds one hit's amount to attacker id's running total, creating an entry (or
    /// evicting the current smallest one) if this attacker is not already tracked.</summary>
    public void Add(uint attackerId, uint amount)
    {
        if (attackerId == 0 || amount == 0)
        {
            return;
        }

        int free = -1;
        int evict = 0;
        uint evictTotal = uint.MaxValue;

        for (int i = 0; i < Capacity; i++)
        {
            if (_id[i] == attackerId)
            {
                // Saturating, not wrapping — same reasoning as DamageDealtTable.Add: a wrap
                // would read as a fresh, tiny attacker rather than the established one it is.
                _total[i] = amount > uint.MaxValue - _total[i] ? uint.MaxValue : _total[i] + amount;
                return;
            }

            if (_id[i] == 0 && free < 0)
            {
                free = i;
            }

            if (_total[i] < evictTotal)
            {
                evictTotal = _total[i];
                evict = i;
            }
        }

        int slot = free >= 0 ? free : evict;
        _id[slot] = attackerId;
        _total[slot] = amount;
    }

    /// <summary>Drops every entry — a phantom re-registered under a fresh id (a respawn, a
    /// module change), so the old attacker breakdown belongs to a body that no longer exists.</summary>
    public void Clear()
    {
        Array.Clear(_id);
        Array.Clear(_total);
    }
}
