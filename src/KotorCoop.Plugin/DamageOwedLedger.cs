namespace KotorCoop.Plugin;

/// <summary>
/// The owner half of the damage channel: how much of the peers' cumulative damage against
/// each enemy has already been turned into real damage here.
///
/// The agent writes a total per enemy ("every peer together has dealt 27 to 0xE"); this
/// remembers what it has applied and hands back the difference. That shape is what makes
/// the channel safe: it is order-independent, a repeated datagram applies nothing, a lost
/// one costs a tick of staleness rather than a hit, and — unlike the health merge it
/// replaced — nothing in the game can erase it, because the number is never read back out
/// of the game.
///
/// Pure arithmetic on purpose. The engine call it feeds is the dangerous part; this is the
/// part that has to be right, so it is separated and tested on its own.
/// </summary>
internal sealed class DamageOwedLedger
{
    /// <summary>
    /// The most one application may deliver. A desync, a re-attached agent with a stale
    /// total, or a garbage read must not one-shot a creature the players have not actually
    /// beaten; anything bigger arrives over successive passes instead, which also reads
    /// better in play. Overkill still kills — this only limits how much per pass.
    /// </summary>
    public const int MaxPerApplication = 200;

    private readonly uint[] _id;
    private readonly uint[] _applied;
    private readonly int _maxPerApplication;

    public DamageOwedLedger(int capacity, int maxPerApplication = MaxPerApplication)
    {
        _id = new uint[capacity];
        _applied = new uint[capacity];
        _maxPerApplication = maxPerApplication;
    }

    /// <summary>Which run of object ids these applied totals belong to (the agent's module
    /// epoch). Zero until the first one arrives.</summary>
    public uint Epoch { get; private set; }

    /// <summary>Enemies the ledger had no room to track. They are not applied at all rather
    /// than applied untracked, which would repeat the same damage every pass forever.</summary>
    public uint Dropped { get; private set; }

    public uint AppliedTo(uint objectId)
    {
        int slot = Find(objectId);
        return slot < 0 ? 0 : _applied[slot];
    }

    /// <summary>
    /// Adopts the agent's epoch, clearing the applied totals when it changes.
    ///
    /// ⚠ Both halves have to restart together. The agent zeroes its owed totals on a module
    /// change; if this side kept a high applied total against them, every point of a peer's
    /// damage in the new module would be swallowed until it exceeded the previous module's
    /// running total. Returns true when it reset.
    /// </summary>
    public bool AdoptEpoch(uint epoch)
    {
        if (epoch == Epoch)
        {
            return false;
        }

        Epoch = epoch;
        Clear();
        return true;
    }

    /// <summary>
    /// How much damage to apply to <paramref name="objectId"/> right now, given the total
    /// the peers say they have dealt to it. Zero means nothing to do.
    ///
    /// A total SMALLER than what we have applied is a peer's counter restarting, not a
    /// negative hit: it is adopted silently. That is the same rule the incoming-phantom
    /// damage channel uses, and for the same reason — a fresh session must not replay a
    /// whole fight, and it must not go permanently deaf either.
    /// </summary>
    public int Pending(uint objectId, uint owed)
    {
        if (objectId == 0)
        {
            return 0;
        }

        int slot = Find(objectId);

        if (slot < 0)
        {
            if (owed == 0)
            {
                return 0;
            }

            slot = Claim(objectId);
            if (slot < 0)
            {
                // No slot means no way to remember what we applied, and applying untracked
                // would re-apply the same damage on every pass until the creature died of
                // it. Refusing is the only safe answer; the debt survives on the peer.
                Dropped++;
                return 0;
            }
        }

        uint applied = _applied[slot];

        if (owed <= applied)
        {
            _applied[slot] = owed;
            return 0;
        }

        uint outstanding = owed - applied;
        return (int)Math.Min(outstanding, (uint)_maxPerApplication);
    }

    /// <summary>Records damage that actually reached the engine. Only ever called with the
    /// amount <see cref="Pending"/> returned, and only after the apply succeeded — a
    /// refused apply must stay owed or the hit is lost for good.</summary>
    public void Commit(uint objectId, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        int slot = Find(objectId);
        if (slot < 0)
        {
            slot = Claim(objectId);
            if (slot < 0)
            {
                return;
            }
        }

        _applied[slot] += (uint)amount;
    }

    public void Clear()
    {
        Array.Clear(_id);
        Array.Clear(_applied);
        Dropped = 0;
    }

    private int Find(uint objectId)
    {
        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] == objectId)
            {
                return i;
            }
        }

        return -1;
    }

    private int Claim(uint objectId)
    {
        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] == 0)
            {
                _id[i] = objectId;
                _applied[i] = 0;
                return i;
            }
        }

        return -1;
    }
}
