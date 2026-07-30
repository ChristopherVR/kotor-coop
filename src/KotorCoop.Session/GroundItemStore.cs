using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The shared "what is lying on the ground" channel. An item dropped by one player must
/// appear in every instance, and vanish everywhere the moment someone picks it up.
///
/// Same shape as <see cref="EventFlagStore"/>: every change bumps <see cref="Version"/>,
/// the host stamps that version into each world snapshot, and a client detects divergence
/// from a 4-byte field rather than by shipping the item list every tick.
/// </summary>
public sealed class GroundItemStore
{
    private const int MaxRememberedOps = 4096;

    private readonly Dictionary<uint, GroundItem> _items = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();

    public uint Version { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool TryGet(uint itemId, out GroundItem? item)
    {
        lock (_gate)
        {
            return _items.TryGetValue(itemId, out item);
        }
    }

    /// <summary>Adds a dropped item. Returns true only when the set actually changed.</summary>
    public bool Drop(GroundItem item)
    {
        lock (_gate)
        {
            if (_items.ContainsKey(item.ItemId) || _items.Count >= ProtocolConstants.MaxGroundItems)
            {
                return false;
            }

            _items[item.ItemId] = item;
            Version++;
            return true;
        }
    }

    /// <summary>Removes a picked-up item. Returns true only when it was present.</summary>
    public bool Remove(uint itemId)
    {
        lock (_gate)
        {
            if (!_items.Remove(itemId))
            {
                return false;
            }

            Version++;
            return true;
        }
    }

    /// <summary>
    /// Applies a remote op exactly once. UDP means the same op can arrive several times
    /// through retransmission, and a drop applied twice would duplicate the item.
    /// </summary>
    public bool ApplyOp(ItemOpPacket op)
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
        }

        return op.Kind == ItemOpKind.Drop ? Drop(op.Item) : Remove(op.Item.ItemId);
    }

    /// <summary>Replaces the whole set from an authoritative host snapshot.</summary>
    public void ReplaceAll(IReadOnlyList<GroundItem> items, uint version)
    {
        lock (_gate)
        {
            _items.Clear();
            foreach (GroundItem item in items)
            {
                _items[item.ItemId] = item;
            }

            Version = version;
        }
    }

    public List<GroundItem> Snapshot()
    {
        lock (_gate)
        {
            return [.. _items.Values];
        }
    }
}
