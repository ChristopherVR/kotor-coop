using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// A client's record of which (container, item) pairs the host has looted, so those items are
/// removed from the client's copy of each shared container and cannot be taken twice.
///
/// Same shape as <see cref="GroundItemStore"/>'s op handling: a removal op is applied exactly
/// once (UDP retransmits the same op several times), and the accumulated set is what the agent
/// re-declares to the game each tick. Applying a removal in-game is idempotent — once the item
/// is gone from the container, re-writing the same removal resolves to a no-op — so keeping a
/// removal in the set after it has been applied is harmless.
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

            ContainerLootRemoval removal = new(op.ContainerId, op.ItemId);
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
