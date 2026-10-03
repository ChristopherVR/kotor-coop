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
public sealed class ReliableLootSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 1024)
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

    /// <summary>Latched so a sustained overload logs once, not once per refused op — see
    /// <see cref="ReliableFlagSender"/>'s twin field. Only the CAP refusal below sets this;
    /// the routine dedup-in-flight refusal is not logged, since it happens constantly and
    /// harmlessly under normal play.</summary>
    private bool _capRefusalLogged;

    public bool HasCapacity { get { lock (_gate) return _pending.Count < maxPending; } }

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
    /// Queues a removal for reliable broadcast, or refuses it. See
    /// <see cref="RetransmitBackoff"/>'s remarks for the enqueue-past-cap contract every
    /// reliable sender in this file family shares. Also returns null when an op for the same
    /// (container, item) is already in flight, so a placeable rescan that still sees the item
    /// missing does not enqueue a duplicate — that case is not the cap contract and is not
    /// logged, since it is routine.
    /// </summary>
    public ContainerLootPacket? Enqueue(
        uint originPeerId, uint containerId, uint itemId, long nowMs, string module = "")
    {
        lock (_gate)
        {
            foreach (Pending existing in _pending.Values)
            {
                if (existing.Packet.ContainerId == containerId && existing.Packet.ItemId == itemId
                    && string.Equals(existing.Packet.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (_pending.Count >= maxPending)
            {
                // The bound exists so a session that never sees another ack cannot grow
                // without limit. Refusing the NEW op rather than evicting an old one is
                // deliberate: the old ones are the removals nobody has confirmed yet.
                if (!_capRefusalLogged)
                {
                    _capRefusalLogged = true;
                    Console.Error.WriteLine(
                        $"[coop] container-loot removal refused: pending table already holds "
                        + $"{maxPending} unacked ops — the link has likely been down a long time.");
                }

                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            ContainerLootPacket packet = new(originPeerId, sequence, containerId, itemId, module);

            // LastSentMs = 0 so CollectDue emits it on the very next tick.
            _pending[sequence] = new Pending { Packet = packet, LastSentMs = 0, Attempts = 0 };
            return packet;
        }
    }

    public void Acknowledge(uint peerId, uint sequence, string module = "")
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(sequence, out Pending? pending)
                && string.Equals(pending.Packet.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                pending.Acked.Add(peerId);
            }
        }
    }

    /// <summary>
    /// Returns ops due for another broadcast, and marks them sent. An op every current
    /// recipient has acked is retired here.
    ///
    /// ⚠ AN EMPTY RECIPIENT LIST IS NOT "EVERYONE HAS ACKED". It used to be, and that was the
    /// single worst disconnect bug in this layer: a client whose link dropped is removed from
    /// the host's peer table after five seconds, at which point the recipient list goes empty
    /// and every unacked removal — every chest the host emptied during the outage — was
    /// retired UNSENT. The client came back, was never told, and could loot the same items a
    /// second time, which is precisely the duplication this whole channel exists to prevent.
    /// Ops now wait for somebody to send them to.
    /// </summary>
    public List<ContainerLootPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<ContainerLootPacket> due = [];

        lock (_gate)
        {
            List<uint>? finished = null;

            foreach ((uint sequence, Pending pending) in _pending)
            {
                if (recipients.Count == 0)
                {
                    // Nobody to tell yet. Hold it: this is world truth, and the peer that
                    // needs it may simply be reconnecting.
                    continue;
                }

                bool allAcked = true;
                foreach (uint recipient in recipients)
                {
                    if (!pending.Acked.Contains(recipient))
                    {
                        allAcked = false;
                        break;
                    }
                }

                if (allAcked)
                {
                    (finished ??= []).Add(sequence);
                    continue;
                }

                // Always emit an op that has never been sent (Attempts == 0), so a freshly
                // enqueued removal goes out on the very next tick; only re-sends wait, and
                // they back off so a long outage costs a trickle rather than 4 packets a
                // second per unacked removal for as long as it lasts.
                if (pending.Attempts > 0
                    && nowMs - pending.LastSentMs < RetransmitBackoff.IntervalMs(
                        pending.Attempts, retransmitIntervalMs, maxIntervalMs))
                {
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

    /// <summary>
    /// Makes every pending op due again. Called when a peer is admitted, so a rejoining
    /// player is brought up to date on the next tick instead of waiting out a backoff that
    /// grew while there was nobody to talk to.
    /// </summary>
    public void ResetBackoff()
    {
        lock (_gate)
        {
            foreach (Pending pending in _pending.Values)
            {
                pending.Attempts = 0;
                pending.LastSentMs = 0;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
