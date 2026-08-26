using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Quest-journal replication: host-authoritative, like flags — merge locally,
/// clients pick up the change (including a removal tombstone) through the journal
/// version/digest carried in every world snapshot.
/// </summary>
public sealed partial class CoopHost
{
    public JournalStore Journal { get; } = new();

    public void SetJournalEntry(string questTag, int state)
    {
        // Host-authoritative, like flags: merge locally, clients pick it up through the
        // journal version in the next world snapshot.
        Journal.Merge(questTag, state, HostPeerId);
    }

    public void RemoveJournalEntry(string questTag)
    {
        // Host-authoritative, like SetJournalEntry: remove locally, clients pick up the
        // tombstone through the journal version/digest tripwire in the next world snapshot.
        Journal.Remove(questTag, HostPeerId);
    }

    private void HandleJournalOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadJournalOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Journal.ApplyRemote(op.QuestTag, op.State, op.Serial, op.OriginPeerId, op.Removed);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new JournalAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleJournalSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadJournalSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        // A journal longer than one datagram goes out as several packets, each carrying
        // the full version. If a part is lost the client's version still matches and the
        // gap survives until the next journal change bumps the version — the same
        // truncation trade the flag snapshot already makes, acceptable because ops are
        // the primary path and snapshots only repair.
        List<JournalSnapshotEntry> entries = Journal.SnapshotEntries();
        uint version = Journal.Version;

        for (int start = 0; ; start += ProtocolConstants.MaxJournalEntriesPerPacket)
        {
            List<JournalSnapshotEntry> part =
                [.. entries.Skip(start).Take(ProtocolConstants.MaxJournalEntriesPerPacket)];

            int length = PacketCodec.Write(_replyBuffer, new JournalSnapshotPacket(version, part));
            if (length > 0)
            {
                _channel.Send(from, _replyBuffer.AsSpan(0, length));
            }

            if (start + ProtocolConstants.MaxJournalEntriesPerPacket >= entries.Count)
            {
                break;
            }
        }
    }
}
