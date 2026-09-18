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

    /// <summary>Latched so a sustained overload logs once, not once per refused op — same
    /// idea as <c>CoopHost</c>'s <c>_snapshotOverflowLogged</c>. Reset the moment there is
    /// room again, so a second bad episode is reported too.</summary>
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

    private void LogCapRefusal(string what)
    {
        if (_capRefusalLogged)
        {
            return;
        }

        _capRefusalLogged = true;
        Console.Error.WriteLine(
            $"[coop] {what} refused: pending table already holds {maxPending} unacked ops — "
            + "the link has likely been down a long time.");
    }

    /// <summary>
    /// Mints and tracks a flag op, or refuses it. See <see cref="RetransmitBackoff"/>'s remarks
    /// for the enqueue-past-cap contract every reliable sender in this file family shares.
    /// </summary>
    public FlagOpPacket? Enqueue(uint originPeerId, ulong flagId, sbyte value, long nowMs, uint serial = 0)
    {
        lock (_gate)
        {
            // The cap is the only thing that discards world truth, and it takes thousands of
            // unacked changes to reach — a session that far gone is not coming back.
            if (_pending.Count >= maxPending)
            {
                LogCapRefusal("flag op");
                return null;
            }

            _capRefusalLogged = false;

            uint sequence = _nextSequence++;
            FlagOpPacket packet = new(originPeerId, sequence, flagId, value, serial);
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
/// <remarks>
/// ENQUEUE-PAST-CAP CONTRACT, shared by every reliable sender that backs off through this
/// class (<see cref="ReliableFlagSender"/>, <see cref="ReliableItemSender"/>,
/// <see cref="ReliableJournalSender"/>, <see cref="ReliableLootSender"/>,
/// <see cref="MaterialisedLootSender"/>, <see cref="DialogueGrantSender"/>): an
/// <c>Enqueue*</c> call made while the pending table is already at its cap REFUSES the op —
/// it returns <c>null</c> (<c>false</c> for <see cref="DialogueGrantSender.EnqueueRelay"/>,
/// which has no packet of its own to hand back) rather than minting and returning a packet
/// the sender is not tracking.
///
/// This used to be inconsistent: the three simplest senders (Flag/Item/Journal) still built
/// and returned a packet on a full table, so the caller sent it once anyway — a datagram
/// that looks delivered to whichever peer receives it, but that nothing here will ever
/// retransmit or report if it is lost, because the sender never added it to
/// <c>_pending</c>. That is strictly worse than refusing outright, so every sender in this
/// family now agrees: a caller MUST treat a null/false result as "not sent, and not going to
/// be retried on its own" and must not construct or forward the would-be packet itself. The
/// cap takes thousands of unacked ops to reach — a session that far gone is not coming
/// back — so refusing here is the correct behaviour, not a missing feature; see the ⚠ note
/// on <see cref="ReliableFlagSender"/> for why the retry policy underneath the cap never
/// gives up on its own.
/// </remarks>
public static class RetransmitBackoff
{
    public static int IntervalMs(int attempts, int baseIntervalMs, int maxIntervalMs)
    {
        int shift = Math.Clamp(attempts - 1, 0, 16);
        long interval = (long)baseIntervalMs << shift;
        return (int)Math.Min(interval, maxIntervalMs);
    }
}
