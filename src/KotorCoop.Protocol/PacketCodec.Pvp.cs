namespace KotorCoop.Protocol;

// Duels (docs/research/pvp-plan.md): a player's request and the host's round. Do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PvpIntentPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PvpIntent);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.Serial);
        w.WriteUInt8((byte)packet.Op);
        w.WriteUInt32(packet.TargetPeerId);
        return w.Failed ? 0 : w.Position;
    }

    public static PvpIntentPacket? ReadPvpIntent(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PvpIntent)
        {
            return null;
        }

        uint session = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint serial = r.ReadUInt32();
        byte op = r.ReadUInt8();
        uint target = r.ReadUInt32();
        if (r.Failed || op is 0 or > (byte)PvpOp.Aware)
        {
            return null;
        }

        return new PvpIntentPacket(session, origin, serial, (PvpOp)op, target);
    }

    public static int Write(Span<byte> buffer, PvpStatePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PvpState);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt8((byte)packet.Phase);
        w.WriteUInt32(packet.RoundId);
        w.WriteUInt32(packet.Challenger);
        w.WriteUInt32(packet.Target);

        int count = Math.Min(packet.Notices.Count, PvpStatePacket.MaxNotices);
        w.WriteUInt8((byte)count);
        for (int i = 0; i < count; i++)
        {
            PvpNotice notice = packet.Notices[i];
            w.WriteUInt32(notice.Serial);
            w.WriteUInt32(notice.ForPeer);
            w.WriteUInt8((byte)notice.Code);
            w.WriteUInt32(notice.Other);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PvpStatePacket? ReadPvpState(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PvpState)
        {
            return null;
        }

        uint session = r.ReadUInt32();
        byte phase = r.ReadUInt8();
        uint round = r.ReadUInt32();
        uint challenger = r.ReadUInt32();
        uint target = r.ReadUInt32();
        byte count = r.ReadUInt8();
        if (r.Failed || phase > (byte)PvpPhase.Live || count > PvpStatePacket.MaxNotices)
        {
            return null;
        }

        List<PvpNotice> notices = new(count);
        for (int i = 0; i < count; i++)
        {
            uint serial = r.ReadUInt32();
            uint forPeer = r.ReadUInt32();
            byte code = r.ReadUInt8();
            uint other = r.ReadUInt32();
            if (r.Failed || code == 0 || code > (byte)PvpNoticeCode.NothingToDo)
            {
                return null;
            }

            notices.Add(new PvpNotice(serial, forPeer, (PvpNoticeCode)code, other));
        }

        return new PvpStatePacket(session, (PvpPhase)phase, round, challenger, target, notices);
    }
}
