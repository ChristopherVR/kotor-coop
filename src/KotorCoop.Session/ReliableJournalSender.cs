using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Retransmit-until-ack for journal ops, exactly like <see cref="ReliableFlagSender"/>.
/// A quest advance is world truth: a lost position update is superseded 50 ms later, a
/// lost "the quest moved on" is a player permanently behind.
/// </summary>
public sealed class ReliableJournalSender(int retransmitIntervalMs = 250, int maxAttempts = 60)
{
    private sealed class Pending
    {
        public required JournalOpPacket Packet;
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

    public JournalOpPacket Enqueue(uint originPeerId, string questTag, int state, long nowMs)
    {
        lock (_gate)
        {
            uint sequence = _nextSequence++;
            JournalOpPacket packet = new(originPeerId, sequence, questTag, state);

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
    public List<JournalOpPacket> CollectDue(long nowMs)
    {
        List<JournalOpPacket> due = [];

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
                    // Give up rather than retransmit forever into a dead link; the
                    // version-mismatch resync repairs the state if the link returns.
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
