using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Minimal application-level reliability for world flag changes. Position updates are
/// disposable; "the door is now open" is not, so those ops are retransmitted until the
/// host acknowledges them.
///
/// ⚠ AN UNACKED OP IS NEVER ABANDONED. It used to be — sixty attempts at a fixed 250 ms, so
/// fifteen seconds of silence and the change was dropped on the floor with a comment saying
/// the resync path would repair it. It does not: the client's own advance is the thing the
/// host has never heard of, and the resync path runs the other way. The bridge only reports
/// globals that CHANGED, so once the store forgot the flag nothing ever re-announced it, and
/// a door opened during an outage stayed shut for the other player for the rest of the
/// playthrough. What the give-up was really protecting against — unbounded memory and a flood
/// of retransmits into a hole — is handled properly instead, by a doubling backoff and a hard
/// cap on the queue.
/// </summary>
public sealed class ReliableFlagSender(
    int retransmitIntervalMs = 250, int maxIntervalMs = 4000, int maxPending = 4096)
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

    public FlagOpPacket Enqueue(uint originPeerId, uint flagId, sbyte value, long nowMs, uint serial = 0)
    {
        lock (_gate)
        {
            uint sequence = _nextSequence++;
            FlagOpPacket packet = new(originPeerId, sequence, flagId, value, serial);

            // The cap is the only thing that discards world truth, and it takes thousands of
            // unacked changes to reach — a session that far gone is not coming back.
            if (_pending.Count < maxPending)
            {
                _pending[sequence] = new Pending
                {
                    Packet = packet,
                    LastSentMs = nowMs,
                    Attempts = 1,
                };
            }

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
            foreach (Pending pending in _pending.Values)
            {
                if (nowMs - pending.LastSentMs < RetransmitBackoff.IntervalMs(
                        pending.Attempts, retransmitIntervalMs, maxIntervalMs))
                {
                    continue;
                }

                pending.Attempts++;
                pending.LastSentMs = nowMs;
                due.Add(pending.Packet);
            }
        }

        return due;
    }

    /// <summary>
    /// Makes every pending op due immediately and resets its backoff. Called the moment a
    /// dropped link comes back: the ops that went unanswered during the outage have backed
    /// off to the cap, and a reconnected player should not stand around for four seconds
    /// waiting for the world to catch up.
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

/// <summary>
/// The retransmit schedule shared by every reliable sender: double the interval each attempt
/// up to a cap.
///
/// Flat 250 ms was fine while the link was up and wrong while it was down — during a blackout
/// each unacked op cost four datagrams a second, forever, into a hole, and the only defence
/// was giving up on it. Doubling means a ten-second outage costs about six attempts per op
/// instead of forty, and an op can be kept alive indefinitely for the price of one datagram
/// every few seconds.
/// </summary>
public static class RetransmitBackoff
{
    public static int IntervalMs(int attempts, int baseIntervalMs, int maxIntervalMs)
    {
        int shift = Math.Clamp(attempts - 1, 0, 16);
        long interval = (long)baseIntervalMs << shift;
        return (int)Math.Min(interval, maxIntervalMs);
    }
}
