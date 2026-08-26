namespace KotorCoop.Protocol;

// Cinematic pre-announcements: a transition's movie queue, its destination/waypoint,
// and a mid-module PlayMovie call.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, TransitionMoviePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.TransitionMovies);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Serial);

        string[] movies = packet.Movies ?? [];
        int count = Math.Min(movies.Length, TransitionMoviePacket.MaxMovies);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            w.WriteString(movies[i], TransitionMoviePacket.MaxMovieLength);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static TransitionMoviePacket? ReadTransitionMovies(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.TransitionMovies)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint serial = r.ReadUInt32();

        int count = r.ReadUInt8();
        if (count > TransitionMoviePacket.MaxMovies)
        {
            // A count this side never writes: treat the datagram as malformed rather than
            // allocating on a number a peer chose.
            return null;
        }

        string[] movies = new string[count];
        for (int i = 0; i < count; i++)
        {
            movies[i] = r.ReadString();
        }

        return r.Failed ? null : new TransitionMoviePacket(peerId, module, serial, movies);
    }

    public static int Write(Span<byte> buffer, TransitionAnnouncePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.TransitionAnnounce);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteString(packet.Waypoint, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Serial);
        return w.Failed ? 0 : w.Position;
    }

    public static TransitionAnnouncePacket? ReadTransitionAnnounce(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.TransitionAnnounce)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        string waypoint = r.ReadString();
        uint serial = r.ReadUInt32();

        return r.Failed ? null : new TransitionAnnouncePacket(peerId, module, waypoint, serial);
    }

    public static int Write(Span<byte> buffer, MidModuleMoviePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.MidModuleMovie);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Serial);
        w.WriteString(packet.Movie, MidModuleMoviePacket.MaxMovieLength);
        return w.Failed ? 0 : w.Position;
    }

    public static MidModuleMoviePacket? ReadMidModuleMovie(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.MidModuleMovie)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint serial = r.ReadUInt32();
        string movie = r.ReadString();

        return r.Failed ? null : new MidModuleMoviePacket(peerId, module, serial, movie);
    }
}
