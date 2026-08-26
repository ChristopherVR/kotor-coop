namespace KotorCoop.Protocol;

// The per-tick avatar state channel: a client's own StateUpdate and the host's
// broadcast WorldSnapshot of every peer.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, StateUpdatePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StateUpdate);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.Sequence);
        w.WriteInt64(packet.ClientTimeMs);
        w.WritePlayerState(packet.State);
        return w.Failed ? 0 : w.Position;
    }

    public static StateUpdatePacket? ReadStateUpdate(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StateUpdate)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint sequence = r.ReadUInt32();
        long clientTime = r.ReadInt64();
        PlayerState state = r.ReadPlayerState();
        return r.Failed ? null : new StateUpdatePacket(peerId, sequence, clientTime, state);
    }

    public static int Write(Span<byte> buffer, WorldSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldSnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.FlagVersion);
        w.WriteUInt32(packet.ItemVersion);
        w.WriteUInt32(packet.JournalVersion);
        w.WriteUInt32(packet.LootVersion);
        w.WriteUInt64(packet.FlagDigest);
        w.WriteUInt64(packet.ItemDigest);
        w.WriteUInt64(packet.JournalDigest);
        w.WriteUInt64(packet.LootDigest);

        int count = Math.Min(packet.Entries.Count, ProtocolConstants.MaxPeers);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            SnapshotEntry entry = packet.Entries[i];
            w.WriteUInt32(entry.PeerId);
            w.WriteName(entry.Name);
            w.WritePlayerState(entry.State);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static WorldSnapshotPacket? ReadWorldSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldSnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        uint flagVersion = r.ReadUInt32();
        uint itemVersion = r.ReadUInt32();
        uint journalVersion = r.ReadUInt32();
        uint lootVersion = r.ReadUInt32();
        ulong flagDigest = r.ReadUInt64();
        ulong itemDigest = r.ReadUInt64();
        ulong journalDigest = r.ReadUInt64();
        ulong lootDigest = r.ReadUInt64();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxPeers)
        {
            return null;
        }

        List<SnapshotEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            uint peerId = r.ReadUInt32();
            string name = r.ReadString();
            PlayerState state = r.ReadPlayerState();
            if (r.Failed)
            {
                return null;
            }

            entries.Add(new SnapshotEntry(peerId, name, state));
        }

        return new WorldSnapshotPacket(
            serverTime, flagVersion, itemVersion, journalVersion, lootVersion,
            flagDigest, itemDigest, journalDigest, lootDigest, entries);
    }
}
