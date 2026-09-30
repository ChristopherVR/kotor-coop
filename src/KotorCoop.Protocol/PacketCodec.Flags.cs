namespace KotorCoop.Protocol;

// The world-flag channel: retransmit-until-acked ops plus the full-resync snapshot.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, FlagOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt64(packet.FlagId);
        w.WriteUInt8((byte)packet.Value);
        w.WriteUInt32(packet.Serial);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt32(packet.HostLoadSerial);
        w.WriteUInt32(packet.WorldRevision);
        return w.Failed ? 0 : w.Position;
    }

    public static FlagOpPacket? ReadFlagOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.FlagOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        ulong flagId = r.ReadUInt64();
        sbyte value = (sbyte)r.ReadUInt8();
        uint serial = r.ReadUInt32();
        uint hostSession = r.ReadUInt32();
        uint hostLoad = r.ReadUInt32();
        uint worldRevision = r.ReadUInt32();
        return r.Failed ? null : new FlagOpPacket(origin, opSeq, flagId, value, serial, hostSession, hostLoad, worldRevision);
    }

    public static int Write(Span<byte> buffer, FlagAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagAck);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static FlagAckPacket? ReadFlagAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.FlagAck)
        {
            return null;
        }

        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new FlagAckPacket(opSeq);
    }

    public static int Write(Span<byte> buffer, FlagSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        return w.Failed ? 0 : w.Position;
    }

    public static FlagSyncRequestPacket? ReadFlagSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.FlagSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        return r.Failed ? null : new FlagSyncRequestPacket(peerId, version);
    }

    public static int Write(Span<byte> buffer, FlagSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagSnapshot);
        w.WriteUInt32(packet.Version);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt32(packet.HostLoadSerial);
        w.WriteUInt32(packet.WorldRevision);

        // 17 bytes per flag (FlagId 8 + Value 1 + Serial 4 + OriginPeerId 4; protocol 31 added
        // the last two, protocol 53 widened the id); stay inside one datagram.
        //
        // ⚠ THE CALLER MUST PAGE. This still clamps — a datagram is a datagram — but a clamp
        // here is now a bug in the caller, not a documented trade: see
        // ProtocolConstants.MaxFlagEntriesPerPacket for what silently dropping the tail of this
        // store actually cost (a host-opened door that could never reach a client). CoopHost
        // sends one packet per page, the way it already does for the journal.
        int count = Math.Min(packet.Flags.Count, ProtocolConstants.MaxFlagEntriesPerPacket);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            FlagSnapshotEntry flag = packet.Flags[i];
            w.WriteUInt64(flag.FlagId);
            w.WriteUInt8((byte)flag.Value);
            w.WriteUInt32(flag.Serial);
            w.WriteUInt32(flag.OriginPeerId);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static FlagSnapshotPacket? ReadFlagSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.FlagSnapshot)
        {
            return null;
        }

        uint version = r.ReadUInt32();
        uint hostSession = r.ReadUInt32();
        uint hostLoad = r.ReadUInt32();
        uint worldRevision = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        if (r.Failed || count > ProtocolConstants.MaxFlagEntriesPerPacket)
        {
            return null;
        }

        List<FlagSnapshotEntry> flags = new(count);
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            sbyte value = (sbyte)r.ReadUInt8();
            uint serial = r.ReadUInt32();
            uint origin = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            flags.Add(new FlagSnapshotEntry(id, value, serial, origin));
        }

        return new FlagSnapshotPacket(version, flags, hostSession, hostLoad, worldRevision);
    }
}
