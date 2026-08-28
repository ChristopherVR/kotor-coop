namespace KotorCoop.Protocol;

// The host-authoritative v2 party-pool/store channels: absolute snapshots (paged, no
// cross-page digest — see PartyPoolSnapshotPacketV2's own remarks) and client intents/acks.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PartyPoolSnapshotPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolSnapshotV2);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt8(packet.PageIndex);
        w.WriteUInt8(packet.PageCount);

        IReadOnlyList<PartyPoolSnapshotEntry> entries = packet.Entries;
        int count = Math.Min(entries.Count, PartyPoolSnapshotPacketV2.MaxEntriesPerPacket);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            PartyPoolSnapshotEntry entry = entries[i];
            w.WriteString(entry.Key.Resref, ProtocolConstants.MaxResrefLength);
            w.WriteUInt8(entry.Key.Charges);
            w.WriteUInt8(entry.Key.MaxCharges);
            w.WriteUInt32(entry.Key.Upgrades);
            w.WriteUInt8(entry.Key.Flags);
            w.WriteInt32(entry.Count);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolSnapshotPacketV2? ReadPartyPoolSnapshotV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolSnapshotV2)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        uint generation = r.ReadUInt32();
        byte pageIndex = r.ReadUInt8();
        byte pageCount = r.ReadUInt8();

        int count = r.ReadUInt8();
        if (r.Failed || count > PartyPoolSnapshotPacketV2.MaxEntriesPerPacket)
        {
            return null;
        }

        PartyPoolSnapshotEntry[] entries = new PartyPoolSnapshotEntry[count];
        for (int i = 0; i < count; i++)
        {
            string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
            byte charges = r.ReadUInt8();
            byte maxCharges = r.ReadUInt8();
            uint upgrades = r.ReadUInt32();
            byte flags = r.ReadUInt8();
            int entryCount = r.ReadInt32();
            entries[i] = new PartyPoolSnapshotEntry(
                new PartyPoolStackKey(resref, charges, maxCharges, upgrades, flags), entryCount);
        }

        return r.Failed ? null : new PartyPoolSnapshotPacketV2(
            serverTimeMs, generation, pageIndex, pageCount, entries);
    }

    public static int Write(Span<byte> buffer, PartyPoolIntentPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolIntentV2);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.IntentSerial);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteString(packet.Key.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteUInt8(packet.Key.Charges);
        w.WriteUInt8(packet.Key.MaxCharges);
        w.WriteUInt32(packet.Key.Upgrades);
        w.WriteUInt8(packet.Key.Flags);
        w.WriteUInt16(packet.Count);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolIntentPacketV2? ReadPartyPoolIntentV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolIntentV2)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();
        ushort intentCount = r.ReadUInt16();

        if (r.Failed
            || kind is not ((byte)PartyPoolOpKind.Add or (byte)PartyPoolOpKind.Remove)
            || intentCount == 0)
        {
            return null;
        }

        PartyPoolStackKey key = new(resref, charges, maxCharges, upgrades, flags);
        return new PartyPoolIntentPacketV2(peerId, serial, (PartyPoolOpKind)kind, key, intentCount);
    }

    public static int Write(Span<byte> buffer, PartyPoolIntentAckPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolIntentAckV2);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.IntentSerial);
        w.WriteBool(packet.Accepted);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolIntentAckPacketV2? ReadPartyPoolIntentAckV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolIntentAckV2)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        bool accepted = r.ReadBool();
        return r.Failed ? null : new PartyPoolIntentAckPacketV2(peerId, serial, accepted);
    }

    public static int Write(Span<byte> buffer, StoreSnapshotPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreSnapshotV2);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt8(packet.PageIndex);
        w.WriteUInt8(packet.PageCount);

        IReadOnlyList<StoreSnapshotEntry> entries = packet.Entries;
        int count = Math.Min(entries.Count, StoreSnapshotPacketV2.MaxEntriesPerPacket);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            StoreSnapshotEntry entry = entries[i];
            w.WriteUInt32(entry.Key.StoreObjectId);
            w.WriteString(entry.Key.Resref, ProtocolConstants.MaxResrefLength);
            w.WriteUInt8(entry.Key.Charges);
            w.WriteUInt8(entry.Key.MaxCharges);
            w.WriteUInt32(entry.Key.Upgrades);
            w.WriteUInt8(entry.Key.Flags);
            w.WriteInt32(entry.Count);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static StoreSnapshotPacketV2? ReadStoreSnapshotV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreSnapshotV2)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        uint generation = r.ReadUInt32();
        byte pageIndex = r.ReadUInt8();
        byte pageCount = r.ReadUInt8();

        int count = r.ReadUInt8();
        if (r.Failed || count > StoreSnapshotPacketV2.MaxEntriesPerPacket)
        {
            return null;
        }

        StoreSnapshotEntry[] entries = new StoreSnapshotEntry[count];
        for (int i = 0; i < count; i++)
        {
            uint storeObjectId = r.ReadUInt32();
            string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
            byte charges = r.ReadUInt8();
            byte maxCharges = r.ReadUInt8();
            uint upgrades = r.ReadUInt32();
            byte flags = r.ReadUInt8();
            int entryCount = r.ReadInt32();
            entries[i] = new StoreSnapshotEntry(
                new StoreStackKey(storeObjectId, resref, charges, maxCharges, upgrades, flags), entryCount);
        }

        return r.Failed ? null : new StoreSnapshotPacketV2(
            serverTimeMs, generation, pageIndex, pageCount, entries);
    }

    public static int Write(Span<byte> buffer, StoreIntentPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreIntentV2);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.IntentSerial);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.Key.StoreObjectId);
        w.WriteString(packet.Key.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteUInt8(packet.Key.Charges);
        w.WriteUInt8(packet.Key.MaxCharges);
        w.WriteUInt32(packet.Key.Upgrades);
        w.WriteUInt8(packet.Key.Flags);
        w.WriteUInt16(packet.Count);
        return w.Failed ? 0 : w.Position;
    }

    public static StoreIntentPacketV2? ReadStoreIntentV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreIntentV2)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        uint storeObjectId = r.ReadUInt32();
        string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();
        ushort intentCount = r.ReadUInt16();

        if (r.Failed
            || kind is not ((byte)StoreOpKind.Add or (byte)StoreOpKind.Remove)
            || intentCount == 0)
        {
            return null;
        }

        StoreStackKey key = new(storeObjectId, resref, charges, maxCharges, upgrades, flags);
        return new StoreIntentPacketV2(peerId, serial, (StoreOpKind)kind, key, intentCount);
    }

    public static int Write(Span<byte> buffer, StoreIntentAckPacketV2 packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreIntentAckV2);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.IntentSerial);
        w.WriteBool(packet.Accepted);
        return w.Failed ? 0 : w.Position;
    }

    public static StoreIntentAckPacketV2? ReadStoreIntentAckV2(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreIntentAckV2)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        bool accepted = r.ReadBool();
        return r.Failed ? null : new StoreIntentAckPacketV2(peerId, serial, accepted);
    }
}
