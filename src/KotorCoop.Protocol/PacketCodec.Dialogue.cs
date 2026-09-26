namespace KotorCoop.Protocol;

// The conversation-ownership token (snapshot + request) and the reward-grant relay.
// Owns the pick-log read/write helper and the node-index sanity bound both the
// snapshot and request readers clamp against.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    /// <summary>The largest dialogue entry index the wire will carry. Far above any real
    /// .dlg — its job is to reject a corrupt sender, not to bound the game.</summary>
    private const int MaxDialogueNodeIndex = 4095;

    /// <summary>The dialogue pick log: a single presence byte, then exactly
    /// <see cref="DialogueState.PickLogBytes"/> raw bytes. Opaque to the wire — only the
    /// two plugins interpret the entries.</summary>
    private static void WritePickLog(ref PacketWriter w, byte[]? pickLog)
    {
        bool present = pickLog is { Length: DialogueState.PickLogBytes };
        w.WriteUInt8(present ? (byte)1 : (byte)0);
        if (present)
        {
            w.WriteBytes(pickLog);
        }
    }

    private static byte[]? ReadPickLog(ref PacketReader r)
    {
        byte present = r.ReadUInt8();
        if (r.Failed || present == 0)
        {
            return null;
        }

        byte[] log = new byte[DialogueState.PickLogBytes];
        r.ReadBytes(log);
        return r.Failed ? null : log;
    }

    public static int Write(Span<byte> buffer, DialogueSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.DialogueSnapshot);
        w.WriteInt64(packet.ServerTimeMs);

        DialogueState s = packet.State;
        w.WriteBool(s.Active);
        w.WriteUInt32(s.OwnerPeerId);
        w.WriteUInt32(s.Serial);
        w.WriteName(s.SpeakerName);
        w.WriteString(s.Line, ProtocolConstants.MaxDialogueLineLength);
        w.WriteString(s.PartnerTag, ProtocolConstants.MaxTagLength);
        w.WriteUInt32(s.ReplySerial);
        w.WriteInt32(s.ReplyIndex);
        WritePickLog(ref w, s.PickLog);
        w.WriteInt32(s.NodeIndex);
        w.WriteUInt32(s.NodeSerial);
        // docs/research/dialogue-sync.md's Item 1: appended after every existing field, same
        // convention as every prior version bump on this packet.
        w.WriteBool(s.Forced);
        w.WriteString(s.DialogResref, 16);

        return w.Failed ? 0 : w.Position;
    }

    public static DialogueSnapshotPacket? ReadDialogueSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.DialogueSnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        bool active = r.ReadBool();
        uint owner = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        string speaker = r.ReadString();
        string line = r.ReadString(ProtocolConstants.MaxDialogueLineLength);
        string partnerTag = r.ReadString();
        uint replySerial = r.ReadUInt32();
        int replyIndex = r.ReadInt32();
        byte[]? pickLog = ReadPickLog(ref r);
        int nodeIndex = r.ReadInt32();
        uint nodeSerial = r.ReadUInt32();
        bool forced = r.ReadBool();
        string dialogResref = r.ReadString(16);

        // Anything other than a real entry index or the explicit "unknown" is treated as
        // unknown. A negative index that is not -1, or one past any conceivable .dlg, can only
        // come from a corrupt or mismatched sender — and the cost of trusting it is a follower
        // driven to a node that is not the owner's.
        if (nodeIndex < DialogueState.NoNode || nodeIndex > MaxDialogueNodeIndex)
        {
            nodeIndex = DialogueState.NoNode;
        }

        return r.Failed
            ? null
            : new DialogueSnapshotPacket(
                serverTime,
                new DialogueState(
                    active, owner, serial, speaker, line, partnerTag, replySerial, replyIndex,
                    pickLog, nodeIndex, nodeSerial, forced, dialogResref));
    }

    public static int Write(Span<byte> buffer, DialogueRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.DialogueRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteBool(packet.Active);
        w.WriteName(packet.SpeakerName);
        w.WriteString(packet.Line, ProtocolConstants.MaxDialogueLineLength);
        w.WriteString(packet.PartnerTag, ProtocolConstants.MaxTagLength);
        w.WriteUInt32(packet.ReplySerial);
        w.WriteInt32(packet.ReplyIndex);
        WritePickLog(ref w, packet.PickLog);
        w.WriteInt32(packet.NodeIndex);
        w.WriteUInt32(packet.NodeSerial);
        w.WriteBool(packet.Forced);
        w.WriteString(packet.DialogResref, 16);

        return w.Failed ? 0 : w.Position;
    }

    public static DialogueRequestPacket? ReadDialogueRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.DialogueRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        bool active = r.ReadBool();
        string speaker = r.ReadString();
        string line = r.ReadString(ProtocolConstants.MaxDialogueLineLength);
        string partnerTag = r.ReadString();
        uint replySerial = r.ReadUInt32();
        int replyIndex = r.ReadInt32();
        byte[]? pickLog = ReadPickLog(ref r);
        int nodeIndex = r.ReadInt32();
        uint nodeSerial = r.ReadUInt32();
        bool forced = r.ReadBool();
        string dialogResref = r.ReadString(16);

        // Same clamp as the snapshot: an index that is neither real nor the explicit
        // "unknown" is treated as unknown, so a corrupt claim cannot drive a follower.
        if (nodeIndex < DialogueState.NoNode || nodeIndex > MaxDialogueNodeIndex)
        {
            nodeIndex = DialogueState.NoNode;
        }

        return r.Failed
            ? null
            : new DialogueRequestPacket(
                peerId, active, speaker, line, partnerTag, replySerial, replyIndex, pickLog,
                nodeIndex, nodeSerial, forced, dialogResref);
    }

    public static int Write(Span<byte> buffer, DialogueGrantPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.DialogueGrant);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.PartnerTag, ProtocolConstants.MaxTagLength);

        IReadOnlyList<MaterialisedLootItem> items = packet.Items;
        int count = Math.Min(items.Count, DialogueGrantPacket.MaxItems);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            MaterialisedLootItem item = items[i];
            w.WriteString(item.Resref, ProtocolConstants.MaxResrefLength);
            w.WriteUInt16(item.Identity.StackSize);
            w.WriteUInt8(item.Identity.Charges);
            w.WriteUInt8(item.Identity.MaxCharges);
            w.WriteUInt32(item.Identity.Upgrades);
            w.WriteUInt8(item.Identity.Flags);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static DialogueGrantPacket? ReadDialogueGrant(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.DialogueGrant)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        string partnerTag = r.ReadString();

        int count = r.ReadUInt8();
        if (count > DialogueGrantPacket.MaxItems)
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

        return r.Failed ? null : new DialogueGrantPacket(origin, opSeq, partnerTag, items);
    }

    public static int Write(Span<byte> buffer, DialogueGrantAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.DialogueGrantAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static DialogueGrantAckPacket? ReadDialogueGrantAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.DialogueGrantAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new DialogueGrantAckPacket(peerId, origin, opSeq);
    }
}
