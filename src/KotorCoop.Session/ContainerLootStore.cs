using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Every (container, item) pair anyone has looted, so the item is removed from this instance's
/// copy of each shared container and cannot be taken twice. Both sides keep one: a container is
/// a private copy in each process, so whoever empties theirs first has to tell everyone else.
///
/// Same shape as <see cref="GroundItemStore"/>'s op handling: a removal op is applied exactly
/// once (UDP retransmits the same op several times), and the accumulated set is what the agent
/// re-declares to the game each tick. Applying a removal in-game is idempotent — once the item
/// is gone from the container, re-writing the same removal resolves to a no-op — so keeping a
/// removal in the set after it has been applied is harmless.
///
/// The set is also what stops a removal echoing between the two instances. Applying a peer's
/// removal deletes the item from a container here, which this instance's own scanner then sees
/// as an item that left a container — indistinguishable from this player looting it. Because
/// <see cref="Add"/> reports a removal already in the set as not-new, the announce path drops
/// it and the echo dies at the first hop.
/// </summary>
public sealed class ContainerLootStore
{
    private const int MaxRememberedOps = 4096;

    /// <summary>How many removals to retain. The oldest are long since applied on the game
    /// side, so dropping them past this bound loses nothing but keeps the set from growing
    /// without limit over a long session.</summary>
    private const int MaxRemovals = 512;

    private readonly List<ContainerLootRemoval> _removals = [];
    private readonly HashSet<ContainerLootRemoval> _set = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _removals.Count;
            }
        }
    }

    /// <summary>
    /// Applies a removal op exactly once. Returns true only when this op was new AND it
    /// introduced a removal the set did not already hold.
    /// </summary>
    public bool ApplyOp(ContainerLootPacket op)
    {
        lock (_gate)
        {
            (uint, uint) key = (op.OriginPeerId, op.OpSequence);

            if (!_appliedOps.Add(key))
            {
                return false;
            }

            _opHistory.Enqueue(key);
            while (_opHistory.Count > MaxRememberedOps)
            {
                _appliedOps.Remove(_opHistory.Dequeue());
            }

            return Remember(new ContainerLootRemoval(op.ContainerId, op.ItemId));
        }
    }

    /// <summary>
    /// Records a removal this instance discovered for itself — this player emptied a container
    /// here — rather than one a peer announced. Returns true only when the pair is new to the
    /// set, which is the whole anti-echo mechanism: a removal that arrived from a peer is
    /// already in the set by the time the scanner rediscovers it, so it is not announced back.
    /// </summary>
    public bool Add(ContainerLootRemoval removal)
    {
        lock (_gate)
        {
            return Remember(removal);
        }
    }

    /// <summary>Whether the set already holds this pair. Caller must hold <see cref="_gate"/>
    /// for a decision that has to stay true; this is for reporting only.</summary>
    public bool Contains(ContainerLootRemoval removal)
    {
        lock (_gate)
        {
            return _set.Contains(removal);
        }
    }

    private bool Remember(ContainerLootRemoval removal)
    {
        if (!_set.Add(removal))
        {
            return false;
        }

        _removals.Add(removal);
        if (_removals.Count > MaxRemovals)
        {
            _set.Remove(_removals[0]);
            _removals.RemoveAt(0);
        }

        return true;
    }

    /// <summary>The removals to re-declare to the game, most recent last.</summary>
    public List<ContainerLootRemoval> Snapshot()
    {
        lock (_gate)
        {
            return [.. _removals];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _removals.Clear();
            _set.Clear();
            _appliedOps.Clear();
            _opHistory.Clear();
        }
    }
}
