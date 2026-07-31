using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for container-loot removals, the host-broadcast twin of
/// <see cref="ReliableItemSender"/>. "The host took this item out of that chest" is world
/// truth, not a disposable sample, so the op is repeated until every connected client has
/// confirmed it.
///
/// Unlike the client senders (which target the single host), this broadcasts to a set of
/// clients, so an op is retired only once every currently-connected peer has acked it. A peer
/// that already acked and still receives a retransmit ignores the duplicate (the client store
/// dedups by op sequence), so the host can simply broadcast each due op to everyone rather
/// than tracking per-peer send addresses.
/// </summary>
public sealed class ReliableLootSender(int retransmitIntervalMs = 250, int maxAttempts = 120)
{
    private sealed class Pending
    {
        public required ContainerLootPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Lock _gate = new();
    private uint _nextSequence = 1;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Queues a removal for reliable broadcast. Returns null when an op for the same
    /// (container, item) is already in flight, so a placeable rescan that still sees the item
    /// missing does not enqueue a duplicate.
    /// </summary>
    public ContainerLootPacket? Enqueue(uint originPeerId, uint containerId, uint itemId, long nowMs)
    {
        lock (_gate)
        {
            foreach (Pending existing in _pending.Values)
            {
                if (existing.Packet.ContainerId == containerId && existing.Packet.ItemId == itemId)
                {
                    return null;
                }
            }

            uint sequence = _nextSequence++;
            ContainerLootPacket packet = new(originPeerId, sequence, containerId, itemId);

            // LastSentMs = 0 so CollectDue emits it on the very next tick.
            _pending[sequence] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    public void Acknowledge(uint peerId, uint sequence)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(sequence, out Pending? pending))
            {
                pending.Acked.Add(peerId);
            }
        }
    }

    /// <summary>
    /// Returns ops due for another broadcast, and marks them sent. An op that every current
    /// recipient has acked (or that has no recipients left) is retired here.
    /// </summary>
    public List<ContainerLootPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<ContainerLootPacket> due = [];

        lock (_gate)
        {
            List<uint>? finished = null;

            foreach ((uint sequence, Pending pending) in _pending)
            {
                bool allAcked = true;
                foreach (uint recipient in recipients)
                {
                    if (!pending.Acked.Contains(recipient))
                    {
                        allAcked = false;
                        break;
                    }
                }

                if (recipients.Count == 0 || allAcked)
                {
                    (finished ??= []).Add(sequence);
                    continue;
                }

                // Always emit an op that has never been sent (Attempts == 0), so a freshly
                // enqueued removal goes out on the very next tick; only re-sends wait for the
                // interval.
                if (pending.Attempts > 0 && nowMs - pending.LastSentMs < retransmitIntervalMs)
                {
                    continue;
                }

                if (pending.Attempts >= maxAttempts)
                {
                    // Give up rather than retransmit forever into a dead link.
                    (finished ??= []).Add(sequence);
                    continue;
                }

                pending.Attempts++;
                pending.LastSentMs = nowMs;
                due.Add(pending.Packet);
            }

            if (finished is not null)
            {
                foreach (uint sequence in finished)
                {
                    _pending.Remove(sequence);
                }
            }
        }

        return due;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
