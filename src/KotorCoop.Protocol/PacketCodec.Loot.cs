namespace KotorCoop.Protocol;

// Shared-container loot and owner-authoritative enemy corpse loot: removal ops,
// their acks, and the resync request.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, ContainerLootPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ContainerLoot);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt32(packet.ContainerId);
        w.WriteUInt32(packet.ItemId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        return w.Failed ? 0 : w.Position;
    }

    public static ContainerLootPacket? ReadContainerLoot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ContainerLoot)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        uint containerId = r.ReadUInt32();
        uint itemId = r.ReadUInt32();
        string module = r.ReadString();
        return r.Failed ? null : new ContainerLootPacket(origin, opSeq, containerId, itemId, module);
    }

    public static int Write(Span<byte> buffer, ContainerLootAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ContainerLootAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        return w.Failed ? 0 : w.Position;
    }

    public static ContainerLootAckPacket? ReadContainerLootAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ContainerLootAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        string module = r.ReadString();
        return r.Failed ? null : new ContainerLootAckPacket(peerId, opSeq, module);
    }

    public static int Write(Span<byte> buffer, LootSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.LootSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        w.WriteUInt64(packet.KnownDigest);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        return w.Failed ? 0 : w.Position;
    }

    public static LootSyncRequestPacket? ReadLootSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.LootSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        ulong digest = r.ReadUInt64();
        string module = r.ReadString();
        return r.Failed ? null : new LootSyncRequestPacket(peerId, version, digest, module);
    }

    public static int Write(Span<byte> buffer, MaterialisedLootPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.MaterialisedLoot);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt32(packet.OwnerEnemyId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        IReadOnlyList<MaterialisedLootItem> items = packet.Items;
        int count = Math.Min(items.Count, MaterialisedLootPacket.MaxItems);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            MaterialisedLootItem item = items[i];
            w.WriteString(item.Resref, ProtocolConstants.MaxResrefLength);

            // Same nine fixed bytes WriteGroundItem uses, for the same reason: a fixed shape
            // for one item entry means the two sides can never disagree about how long it is.
            w.WriteUInt16(item.Identity.StackSize);
            w.WriteUInt8(item.Identity.Charges);
            w.WriteUInt8(item.Identity.MaxCharges);
            w.WriteUInt32(item.Identity.Upgrades);
            w.WriteUInt8(item.Identity.Flags);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static MaterialisedLootPacket? ReadMaterialisedLoot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.MaterialisedLoot)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        uint ownerEnemyId = r.ReadUInt32();
        string module = r.ReadString();

        int count = r.ReadUInt8();
        if (count > MaterialisedLootPacket.MaxItems)
        {
            // A count this side never writes: treat the datagram as malformed rather than
            // allocating on a number a peer chose.
            return null;
        }

        MaterialisedLootItem[] items = new MaterialisedLootItem[count];
        for (int i = 0; i < count; i++)
        {
            string resref = r.ReadString();
            ushort stack = r.ReadUInt16();
            byte charges = r.ReadUInt8();
            byte maxCharges = r.ReadUInt8();
            uint upgrades = r.ReadUInt32();
            byte flags = r.ReadUInt8();
            items[i] = new MaterialisedLootItem(
                resref, new ItemIdentity(stack, charges, maxCharges, upgrades, flags));
        }

        return r.Failed ? null : new MaterialisedLootPacket(origin, opSeq, ownerEnemyId, items, module);
    }

    public static int Write(Span<byte> buffer, MaterialisedLootAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.MaterialisedLootAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        return w.Failed ? 0 : w.Position;
    }

    public static MaterialisedLootAckPacket? ReadMaterialisedLootAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.MaterialisedLootAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        string module = r.ReadString();
        return r.Failed ? null : new MaterialisedLootAckPacket(peerId, opSeq, module);
    }
}
