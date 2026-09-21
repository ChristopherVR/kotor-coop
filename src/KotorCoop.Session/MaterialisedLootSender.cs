using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for a dead enemy's captured loot, the host-broadcast twin of
/// <see cref="ReliableLootSender"/> and built to the same shape. "This enemy died holding
/// these items" is world truth, not a disposable sample — a dropped packet must not mean the
/// receiving side's stand-in stays empty forever — so the op repeats until every connected
/// client has confirmed it.
///
/// Deduplicated by owner enemy id, not by item: a creature dies exactly once, so a second
/// capture attempt for the same id (the death-detection loop firing twice on a slow tick) must
/// not queue a second broadcast.
/// </summary>
public sealed class MaterialisedLootSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 1024)
{
    private sealed class Pending
    {
        public required MaterialisedLootPacket Packet;
        public long LastSentMs;
        public int Attempts;
        public readonly HashSet<uint> Acked = [];
    }

    private readonly Dictionary<uint, Pending> _pending = [];
    private readonly Lock _gate = new();
    private uint _nextSequence = 1;

    /// <summary>Latched so a sustained overload logs once, not once per refused op — see
    /// <see cref="ReliableFlagSender"/>'s twin field. Only the CAP refusal below sets this;
    /// the routine same-owner dedup refusal is not logged.</summary>
    private bool _capRefusalLogged;

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
    /// Queues one enemy's captured loot for reliable broadcast, or refuses it. Returns null
    /// when an op for the same owner enemy id is already in flight, so a death-detection loop
    /// that fires twice for the same creature does not enqueue a duplicate (routine, not
    /// logged), and also once the pending table is at its cap — see
    /// <see cref="RetransmitBackoff"/>'s remarks for the enqueue-past-cap contract every
    /// reliable sender in this file family shares.
    /// </summary>
    public MaterialisedLootPacket? Enqueue(
        uint originPeerId, uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items, long nowMs,
        string module = "")
    {
        lock (_gate)
        {
            foreach (Pending existing in _pending.Values)
            {
                if (existing.Packet.OwnerEnemyId == ownerEnemyId
                    && string.Equals(existing.Packet.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (_pending.Count >= maxPending)
            {
                // The bound exists so a session that never sees another ack cannot grow
                // without limit. Refusing the NEW op rather than evicting an old one is
                // deliberate: the old ones are the deaths nobody has confirmed yet.
                if (!_capRefusalLogged)
                {
                    _capRefusalLogged = true;
                    Console.Error.WriteLine(
                        $"[coop] materialised-loot capture refused: pending table already holds "
                        + $"{maxPending} unacked ops — the link has likely been down a long time.");
                }

                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            MaterialisedLootPacket packet = new(originPeerId, sequence, ownerEnemyId, items, module);

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
    /// An empty recipient list is not "everyone has acked" — same rule as
    /// <see cref="ReliableLootSender.CollectDue"/>, for the same reconnect-outage reason.
    /// </summary>
    public List<MaterialisedLootPacket> CollectDue(IReadOnlyCollection<uint> recipients, long nowMs)
    {
        List<MaterialisedLootPacket> due = [];

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
                // enqueued death goes out on the very next tick; only re-sends wait, and they
                // back off so a long outage costs a trickle rather than a steady drumbeat per
                // unacked death for as long as it lasts.
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
