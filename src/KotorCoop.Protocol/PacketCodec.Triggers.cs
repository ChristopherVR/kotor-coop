namespace KotorCoop.Protocol;

// Client-standing-in-trigger reports the host acts on: spawn-eligible encounters, and
// generic OnEnter triggers (both directions, including native host/client fires).
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, EncounterTriggerPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EncounterTrigger);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.EncounterIds.Count, ProtocolConstants.MaxEncounterTriggers);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            w.WriteUInt32(packet.EncounterIds[i]);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EncounterTriggerPacket? ReadEncounterTrigger(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EncounterTrigger)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEncounterTriggers)
        {
            return null;
        }

        List<uint> ids = new(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            ids.Add(id);
        }

        return new EncounterTriggerPacket(peerId, module, ids);
    }

    public static int Write(Span<byte> buffer, TriggerRelayPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.TriggerRelay);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int triggerCount = Math.Min(packet.TriggerIds.Count, ProtocolConstants.MaxTriggerRelayEntries);
        w.WriteUInt8((byte)triggerCount);
        for (int i = 0; i < triggerCount; i++)
        {
            w.WriteUInt32(packet.TriggerIds[i]);
        }

        int firedCount = Math.Min(packet.FiredIds.Count, ProtocolConstants.MaxTriggerFiredIds);
        w.WriteUInt8((byte)firedCount);
        for (int i = 0; i < firedCount; i++)
        {
            w.WriteUInt32(packet.FiredIds[i]);
        }

        // Protocol 36. NativeFiredOriginPeerIds is written only up to however many entries
        // NativeFiredIds itself carries — a peer->host report never fills it (origin is
        // implicit), and the host->all direction always keeps the two lists the same length
        // (TriggerRelayStore.GetNativeFires builds them together).
        int nativeCount = Math.Min(packet.NativeFiredIds.Count, ProtocolConstants.MaxTriggerNativeFires);
        w.WriteUInt8((byte)nativeCount);
        for (int i = 0; i < nativeCount; i++)
        {
            w.WriteUInt32(packet.NativeFiredIds[i]);
        }

        int originCount = Math.Min(
            Math.Min(packet.NativeFiredOriginPeerIds.Count, nativeCount),
            ProtocolConstants.MaxTriggerNativeFires);
        w.WriteUInt8((byte)originCount);
        for (int i = 0; i < originCount; i++)
        {
            w.WriteUInt32(packet.NativeFiredOriginPeerIds[i]);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static TriggerRelayPacket? ReadTriggerRelay(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.TriggerRelay)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        byte triggerCount = r.ReadUInt8();

        if (r.Failed || triggerCount > ProtocolConstants.MaxTriggerRelayEntries)
        {
            return null;
        }

        List<uint> triggerIds = new(triggerCount);
        for (int i = 0; i < triggerCount; i++)
        {
            uint id = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            triggerIds.Add(id);
        }

        byte firedCount = r.ReadUInt8();
        if (r.Failed || firedCount > ProtocolConstants.MaxTriggerFiredIds)
        {
            return null;
        }

        List<uint> firedIds = new(firedCount);
        for (int i = 0; i < firedCount; i++)
        {
            uint id = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            firedIds.Add(id);
        }

        byte nativeCount = r.ReadUInt8();
        if (r.Failed || nativeCount > ProtocolConstants.MaxTriggerNativeFires)
        {
            return null;
        }

        List<uint> nativeFiredIds = new(nativeCount);
        for (int i = 0; i < nativeCount; i++)
        {
            uint id = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            nativeFiredIds.Add(id);
        }

        byte originCount = r.ReadUInt8();
        if (r.Failed || originCount > ProtocolConstants.MaxTriggerNativeFires)
        {
            return null;
        }

        List<uint> nativeFiredOrigins = new(originCount);
        for (int i = 0; i < originCount; i++)
        {
            uint id = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            nativeFiredOrigins.Add(id);
        }

        return new TriggerRelayPacket(
            peerId, module, triggerIds, firedIds, nativeFiredIds, nativeFiredOrigins);
    }
}
