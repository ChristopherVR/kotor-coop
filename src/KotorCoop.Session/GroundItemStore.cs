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
    private ulong _digest = EntryDigest.Empty;

    public uint Version { get; private set; }

    /// <summary>Order-independent content fingerprint, beside <see cref="Version"/> for the
    /// same reason <see cref="EventFlagStore.Digest"/> exists — see <see cref="EntryDigest"/>.</summary>
    public ulong Digest
    {
        get
        {
            lock (_gate)
            {
                return _digest;
            }
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void ReplaceEntry(GroundItem item)
    {
        if (_items.TryGetValue(item.ItemId, out GroundItem? existing))
        {
            _digest ^= EntryDigest.GroundItem(existing.ItemId, existing.Resref, existing.Identity);
        }

        _items[item.ItemId] = item;
        _digest ^= EntryDigest.GroundItem(item.ItemId, item.Resref, item.Identity);
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void RemoveEntry(uint itemId)
    {
        if (_items.Remove(itemId, out GroundItem? removed))
        {
            _digest ^= EntryDigest.GroundItem(removed.ItemId, removed.Resref, removed.Identity);
        }
    }

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

    /// <summary>
    /// Adds a dropped item. Returns true only when the set actually changed.
    ///
    /// An id already on the ground is normally left alone — that is what makes a
    /// retransmitted drop harmless. The one exception is the item's IDENTITY, which merges
    /// upward like every other shared fact here: a re-announcement that KNOWS the stack
    /// size, charges and upgrades replaces one that did not, and never the other way round.
    /// Identity can only ever be learned, so an instance that heard the blueprint first (a
    /// snapshot, a harness drop) is repaired by the real thing arriving later, while a
    /// stock re-announcement can never strip an upgraded weapon back down.
    /// </summary>
    public bool Drop(GroundItem item)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(item.ItemId, out GroundItem? existing))
            {
                if (!item.Identity.IsKnown || existing.Identity.IsKnown)
                {
                    return false;
                }

                ReplaceEntry(existing with { Identity = item.Identity });
                Version++;
                return true;
            }

            if (_items.Count >= ProtocolConstants.MaxGroundItems)
            {
                return false;
            }

            ReplaceEntry(item);
            Version++;
            return true;
        }
    }

    /// <summary>Removes a picked-up item. Returns true only when it was present.</summary>
    public bool Remove(uint itemId)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(itemId))
            {
                return false;
            }

            RemoveEntry(itemId);
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

    /// <summary>
    /// Replaces the whole set from an authoritative host snapshot.
    ///
    /// The host decides WHICH items are on the ground and where. It does not get to forget
    /// an identity: an entry whose identity is unknown carries no claim about the item, so a
    /// locally known one for the same id survives the replacement. Without this rule a
    /// client that received the drop op (identity and all) and then the host's snapshot
    /// would have the upgrades taken straight back off the item.
    /// </summary>
    public void ReplaceAll(IReadOnlyList<GroundItem> items, uint version)
    {
        lock (_gate)
        {
            Dictionary<uint, GroundItem> previous = new(_items);
            _items.Clear();
            _digest = EntryDigest.Empty;

            foreach (GroundItem item in items)
            {
                GroundItem toStore = !item.Identity.IsKnown
                    && previous.TryGetValue(item.ItemId, out GroundItem? known)
                    && known.Identity.IsKnown
                        ? item with { Identity = known.Identity }
                        : item;

                ReplaceEntry(toStore);
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
