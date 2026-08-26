namespace KotorCoop.Protocol;

// The quest-journal channel: retransmit-until-acked ops plus the full-resync snapshot.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, JournalOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.QuestTag, ProtocolConstants.MaxJournalTagLength);
        w.WriteInt32(packet.State);
        w.WriteUInt32(packet.Serial);
        w.WriteUInt8((byte)(packet.Removed ? 1 : 0));
        return w.Failed ? 0 : w.Position;
    }

    public static JournalOpPacket? ReadJournalOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.JournalOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        string tag = r.ReadString();
        int state = r.ReadInt32();
        uint serial = r.ReadUInt32();
        bool removed = r.ReadUInt8() != 0;
        return r.Failed || tag.Length == 0
            ? null
            : new JournalOpPacket(origin, opSeq, tag, state, serial, removed);
    }

    public static int Write(Span<byte> buffer, JournalAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalAck);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static JournalAckPacket? ReadJournalAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.JournalAck)
        {
            return null;
        }

        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new JournalAckPacket(opSeq);
    }

    public static int Write(Span<byte> buffer, JournalSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        return w.Failed ? 0 : w.Position;
    }

    public static JournalSyncRequestPacket? ReadJournalSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.JournalSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        return r.Failed ? null : new JournalSyncRequestPacket(peerId, version);
    }

    public static int Write(Span<byte> buffer, JournalSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalSnapshot);
        w.WriteUInt32(packet.Version);

        int count = Math.Min(packet.Entries.Count, ProtocolConstants.MaxJournalEntriesPerPacket);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            JournalSnapshotEntry entry = packet.Entries[i];
            w.WriteString(entry.QuestTag, ProtocolConstants.MaxJournalTagLength);
            w.WriteInt32(entry.State);
            w.WriteUInt32(entry.Serial);
            w.WriteUInt32(entry.OriginPeerId);
            w.WriteUInt8((byte)(entry.Removed ? 1 : 0));
        }

        return w.Failed ? 0 : w.Position;
    }

    public static JournalSnapshotPacket? ReadJournalSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.JournalSnapshot)
        {
            return null;
        }

        uint version = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        if (r.Failed || count > ProtocolConstants.MaxJournalEntriesPerPacket)
        {
            return null;
        }

        List<JournalSnapshotEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            string tag = r.ReadString();
            int state = r.ReadInt32();
            uint serial = r.ReadUInt32();
            uint origin = r.ReadUInt32();
            bool removed = r.ReadUInt8() != 0;
            if (r.Failed || tag.Length == 0)
            {
                return null;
            }

            entries.Add(new JournalSnapshotEntry(tag, state, serial, origin, removed));
        }

        return new JournalSnapshotPacket(version, entries);
    }
}
