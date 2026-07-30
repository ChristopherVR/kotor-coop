using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Minimal application-level reliability for world flag changes. Position updates are
/// disposable; "the door is now open" is not, so those ops are retransmitted until the
/// host acknowledges them.
/// </summary>
public sealed class ReliableFlagSender(int retransmitIntervalMs = 250, int maxAttempts = 60)
{
    private sealed class Pending
    {
        public required FlagOpPacket Packet;
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

    public FlagOpPacket Enqueue(uint originPeerId, uint flagId, sbyte value, long nowMs)
    {
        lock (_gate)
        {
            uint sequence = _nextSequence++;
            FlagOpPacket packet = new(originPeerId, sequence, flagId, value);

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
    public List<FlagOpPacket> CollectDue(long nowMs)
    {
        List<FlagOpPacket> due = [];

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
