using System.Security.Cryptography;

namespace KotorCoop.Plugin;

/// <summary>
/// A running total of the damage this instance has dealt to each creature, keyed by engine
/// object id.
///
/// This is the peer half of the damage channel. The owner of a fight cannot learn a peer's
/// contribution from HEALTH: the owner writes its own value back onto the peer's copy
/// several times a second, so a hit had to win a race against the next mirror pass to be
/// seen at all — measured live, a client attacking one soldier for ninety seconds moved it
/// from 15 hit points to 11 and no further, while its screen showed a real fight. A sum of
/// damage DEALT cannot be raced, because nothing ever reads it back out of the game.
///
/// ⚠ THIS IS TOUCHED FROM INSIDE THE ENGINE'S DAMAGE HOOK, which runs for every point of
/// damage in the game. Nothing here allocates, throws, sorts or calls the engine: it is a
/// linear scan of at most 32 uints and one add. Keep it that way.
/// </summary>
internal sealed class DamageDealtTable
{
    private readonly uint[] _id;
    private readonly uint[] _total;

    public DamageDealtTable(int capacity)
    {
        _id = new uint[capacity];
        _total = new uint[capacity];
        // The stable peer identity survives a full game-process restart. A fresh
        // process must not reuse its predecessor's damage namespace and high-water mark.
        // Seed once outside the damage hook; subsequent module resets remain allocation-free.
        Span<byte> seed = stackalloc byte[sizeof(uint)];
        do
        {
            RandomNumberGenerator.Fill(seed);
            Incarnation = BitConverter.ToUInt32(seed);
        } while (Incarnation == 0);
        Generation = 1;
    }

    public uint Incarnation { get; }

    public int Capacity => _id.Length;

    /// <summary>Hits there was no free slot for. Non-zero means a peer's damage is being
    /// under-reported, which is exactly the failure this channel exists to remove, so it is
    /// published rather than swallowed.</summary>
    public uint Dropped { get; private set; }

    /// <summary>
    /// Which run of ids these totals belong to within the stable process Incarnation. Bumped by <see cref="Clear"/>.
    ///
    /// ⚠ The owner needs this to tell a RESET from a stale datagram. Totals only ever grow,
    /// so the owner ignores a number smaller than the last one it saw — and without a
    /// generation, a peer that leaves the module and comes back (totals restarting at zero)
    /// would look permanently stale and everything it did from then on would be ignored.
    /// </summary>
    public uint Generation { get; private set; }

    public uint IdAt(int index) => _id[index];

    public uint TotalAt(int index) => _total[index];

    /// <summary>The current cumulative total for one target, or 0 if nothing has ever been
    /// added for it — the read half of <see cref="Add"/>, for a caller (local_combat's
    /// inactivity timer, docs/research/combat-participation.md §13) that needs one id's own
    /// total rather than this ledger's whole array shape.</summary>
    public uint TotalFor(uint objectId)
    {
        if (objectId == 0)
        {
            return 0;
        }

        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] == objectId)
            {
                return _total[i];
            }
        }

        return 0;
    }

    /// <summary>
    /// Adds one hit. Returns false when the table was full and the damage was dropped.
    /// </summary>
    public bool Add(uint objectId, uint amount)
    {
        if (objectId == 0 || amount == 0)
        {
            return true;
        }

        int free = -1;

        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] == objectId)
            {
                // Saturating, not wrapping. A wrap would read as a reset on the owner's
                // side and hand a peer's whole fight back to it a second time; a total
                // this large is impossible in play, so pinning at the ceiling is the safe
                // way to be wrong.
                _total[i] = amount > uint.MaxValue - _total[i] ? uint.MaxValue : _total[i] + amount;
                return true;
            }

            if (free < 0 && _id[i] == 0)
            {
                free = i;
            }
        }

        if (free < 0)
        {
            Dropped++;
            return false;
        }

        _id[free] = objectId;
        _total[free] = amount;
        return true;
    }

    /// <summary>
    /// Frees the slot for a creature whose fight is over.
    ///
    /// ⚠ Only ever call this for a creature the OWNER has said is dead. The slot going back
    /// to zero means the owner's last-seen total for that id is now higher than anything we
    /// will send, so any further damage against it is ignored — which is right for a corpse
    /// and wrong for anything else.
    /// </summary>
    public void Forget(uint objectId)
    {
        for (int i = 0; i < _id.Length; i++)
        {
            if (_id[i] == objectId)
            {
                _id[i] = 0;
                _total[i] = 0;
                return;
            }
        }
    }

    /// <summary>Drops everything and starts a new generation. Object ids are assigned per
    /// module load, so a total carried across a transition would be credited against a
    /// completely different creature.</summary>
    public void Clear()
    {
        Array.Clear(_id);
        Array.Clear(_total);
        Dropped = 0;
        Generation++;
    }
}
