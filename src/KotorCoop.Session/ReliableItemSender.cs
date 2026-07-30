using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for ground-item ops, the item twin of
/// <see cref="ReliableFlagSender"/>. "A lightsaber is lying there" is world truth, not a
/// disposable position sample, so the op is repeated until the host confirms it.
/// </summary>
public sealed class ReliableItemSender(int retransmitIntervalMs = 250, int maxAttempts = 60)
{
    private sealed class Pending
    {
        public required ItemOpPacket Packet;
        public long LastSentMs;
        public int Attempts;
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

    public ItemOpPacket Enqueue(uint originPeerId, ItemOpKind kind, GroundItem item, long nowMs)
    {
        lock (_gate)
        {
            uint sequence = _nextSequence++;
            ItemOpPacket packet = new(originPeerId, sequence, kind, item);

            _pending[sequence] = new Pending
            {
                Packet = packet,
                LastSentMs = nowMs,
                Attempts = 1,
            };

            return packet;
        }
    }

    public void Acknowledge(uint sequence)
    {
        lock (_gate)
        {
            _pending.Remove(sequence);
        }
    }

    /// <summary>Returns ops due for another attempt, and marks them as sent.</summary>
    public List<ItemOpPacket> CollectDue(long nowMs)
    {
        List<ItemOpPacket> due = [];

        lock (_gate)
        {
            List<uint>? exhausted = null;

            foreach ((uint sequence, Pending pending) in _pending)
            {
                if (nowMs - pending.LastSentMs < retransmitIntervalMs)
                {
                    continue;
                }

                if (pending.Attempts >= maxAttempts)
                {
                    // Give up rather than retransmit forever into a dead link. The
                    // version-mismatch resync path will repair the state if it returns.
                    (exhausted ??= []).Add(sequence);
                    continue;
                }

                pending.Attempts++;
                pending.LastSentMs = nowMs;
                due.Add(pending.Packet);
            }

            if (exhausted is not null)
            {
                foreach (uint sequence in exhausted)
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
