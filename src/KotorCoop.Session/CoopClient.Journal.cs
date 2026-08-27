using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Quest-journal replication: local merges (including a removal tombstone) applied
/// straight away and queued to the reliable sender, plus the host's acks and
/// snapshot merges.
/// </summary>
public sealed partial class CoopClient
{
    private readonly ReliableJournalSender _journalSender = new();

    public JournalStore Journal { get; } = new();

    public void SetJournalEntry(string questTag, int state)
    {
        // Applied locally straight away — the local player just watched the quest
        // advance (or, since protocol 31, genuinely retreat — a decrease with override, or
        // a re-add after a removal); the host's version confirms and redistributes it.
        if (!Journal.Merge(questTag, state, _sessionPeerId))
        {
            return;
        }

        // Queued while disconnected too — see SetFlag.
        if (_sessionPeerId == 0 || !Journal.TryGetStamp(questTag, out uint serial, out _))
        {
            return;
        }

        // Null means the reliable sender refused the op (its pending table is at its cap,
        // logged there) — see RetransmitBackoff's remarks. The local store already has the
        // advance, so this player's own game is still correct even though nothing goes out.
        if (_journalSender.Enqueue(_sessionPeerId, questTag, state, SessionClock.NowMs, serial) is { } op
            && IsConnected)
        {
            SendJournalOp(op);
        }
    }

    /// <summary>
    /// The removal twin of <see cref="SetJournalEntry"/>: this instance's engine ran
    /// `RemoveJournalQuestEntry` for <paramref name="questTag"/>. Tombstones locally straight
    /// away and, once connected, announces the removal reliably so the host (and, through it,
    /// every other peer) takes the tag out too — see JournalStore's remarks for why a
    /// tombstone, not a deletion.
    /// </summary>
    public void RemoveJournalEntry(string questTag)
    {
        if (!Journal.Remove(questTag, _sessionPeerId))
        {
            return;
        }

        if (_sessionPeerId == 0 || !Journal.TryGetStamp(questTag, out uint serial, out _))
        {
            return;
        }

        if (_journalSender.Enqueue(
                _sessionPeerId, questTag, 0, SessionClock.NowMs, serial, removed: true) is { } op
            && IsConnected)
        {
            SendJournalOp(op);
        }
    }

    private void SendJournalOp(JournalOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleJournalAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadJournalAck(span) is { } journalAck)
        {
            _journalSender.Acknowledge(journalAck.OpSequence);
        }
    }

    private void HandleJournalSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadJournalSnapshot(span) is { } journalSnapshot)
        {
            // Merge, never replace: each entry (including a removal tombstone)
            // applies last-writer-wins by its own (Serial, OriginPeerId) — see
            // JournalStore's remarks.
            Journal.MergeSnapshot(journalSnapshot.Entries, journalSnapshot.Version);
        }
    }
}
