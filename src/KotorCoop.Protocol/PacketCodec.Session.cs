namespace KotorCoop.Protocol;

// Connection lifecycle and handshake: Hello/Welcome/Reject, Ping/Pong/Bye, and the
// per-peer WorldDigest exchange. Owns the shared ContentDigest read/write helpers,
// used only by Hello and Welcome.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, HelloPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Hello);
        w.WriteName(packet.PlayerName);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.ClientNonce);
        w.WriteUInt32(packet.PasswordHash);
        WriteDigest(ref w, packet.Digest);
        WriteBehaviourDigest(ref w, packet.Behaviour);
        return w.Failed ? 0 : w.Position;
    }

    public static HelloPacket? ReadHello(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Hello)
        {
            return null;
        }

        string name = r.ReadString();
        string module = r.ReadString();
        uint nonce = r.ReadUInt32();
        uint passwordHash = r.ReadUInt32();
        ContentDigest digest = ReadDigest(ref r);
        BehaviourDigest behaviour = ReadBehaviourDigest(ref r);
        return r.Failed ? null : new HelloPacket(name, module, nonce, digest, passwordHash, behaviour);
    }

    public static int Write(Span<byte> buffer, WelcomePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Welcome);
        w.WriteUInt32(packet.AssignedPeerId);
        w.WriteUInt32(packet.SessionId);
        w.WriteUInt8(packet.TickRate);
        w.WriteUInt8(packet.MaxPeers);
        WriteDigest(ref w, packet.Digest);
        WriteBehaviourDigest(ref w, packet.Behaviour);
        return w.Failed ? 0 : w.Position;
    }

    public static WelcomePacket? ReadWelcome(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Welcome)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint sessionId = r.ReadUInt32();
        byte tickRate = r.ReadUInt8();
        byte maxPeers = r.ReadUInt8();
        ContentDigest digest = ReadDigest(ref r);
        BehaviourDigest behaviour = ReadBehaviourDigest(ref r);
        return r.Failed ? null : new WelcomePacket(peerId, sessionId, tickRate, maxPeers, digest, behaviour);
    }

    /// <summary>Version 40: appended after the existing Hello/Welcome fields, same reason
    /// every prior handshake addition (e.g. the content digest itself) was appended rather
    /// than interleaved — a strict version check already rejects a mismatched peer before
    /// either side tries to parse the other's body, so there is no old-shape reader to keep
    /// working here the way <see cref="Write(Span{byte}, WorldDigestPacket)"/>'s comment
    /// discusses for a periodic packet.</summary>
    private static void WriteBehaviourDigest(ref PacketWriter w, in BehaviourDigest digest)
    {
        w.WriteUInt8(BehaviourDigest.CurrentScheme);
        w.WriteUInt32(digest.Flags);
        w.WriteInt32(digest.InterpolationMs);
        w.WriteString(digest.FollowLead, MaxFollowLeadLength);

        // Scheme 2 (protocol 53): the four player-count scaling SETTINGS, as int32 each — 0 is
        // "auto", otherwise a percent (docs/research/player-count-scaling.md). Ints rather
        // than bytes so the range can widen without another scheme bump.
        w.WriteInt32(digest.EnemyHealth);
        w.WriteInt32(digest.EnemyDamage);
        w.WriteInt32(digest.XpRate);
        w.WriteInt32(digest.StoreStock);
    }

    private static BehaviourDigest ReadBehaviourDigest(ref PacketReader r)
    {
        byte scheme = r.ReadUInt8();
        uint flags = r.ReadUInt32();
        int interp = r.ReadInt32();
        string followLead = r.ReadString();

        // An unrecognised scheme means a build we cannot interpret the fields of — treat it
        // as "no information" (everything off, matching BehaviourDigest's zero value) rather
        // than misreading, the same rule ReadDigest already follows for ContentDigest.
        if (scheme != BehaviourDigest.CurrentScheme)
        {
            return default;
        }

        int enemyHealth = r.ReadInt32();
        int enemyDamage = r.ReadInt32();
        int xpRate = r.ReadInt32();
        int storeStock = r.ReadInt32();

        return BehaviourDigest.FromFields(flags, interp, followLead, enemyHealth, enemyDamage, xpRate, storeStock);
    }

    /// <summary>follow_lead is either "any" or "host" today (<see
    /// cref="FollowLeadMode"/>) — bounded generously above that so a future mode fits without
    /// another wire bump.</summary>
    private const int MaxFollowLeadLength = 16;

    private static void WriteDigest(ref PacketWriter w, in ContentDigest digest)
    {
        w.WriteUInt8(ContentDigest.CurrentScheme);
        w.WriteUInt16(digest.KnownMask);
        w.WriteUInt32(digest.ExeDigest);
        w.WriteUInt32(digest.OverrideCensus);
        w.WriteUInt16(digest.OverrideCount);
        w.WriteUInt32(digest.TwoDaDigest);
        w.WriteUInt32(digest.AppearanceDigest);
        w.WriteUInt16(digest.AppearanceRows);
        w.WriteUInt32(digest.TlkDigest);
        w.WriteUInt32(digest.TlkEntryCount);
        w.WriteUInt32(digest.JournalDigest);
        w.WriteUInt32(digest.ModulesCensus);
    }

    private static ContentDigest ReadDigest(ref PacketReader r)
    {
        byte scheme = r.ReadUInt8();
        ushort knownMask = r.ReadUInt16();
        uint exe = r.ReadUInt32();
        uint overrideCensus = r.ReadUInt32();
        ushort overrideCount = r.ReadUInt16();
        uint twoDa = r.ReadUInt32();
        uint appearance = r.ReadUInt32();
        ushort appearanceRows = r.ReadUInt16();
        uint tlk = r.ReadUInt32();
        uint tlkCount = r.ReadUInt32();
        uint journal = r.ReadUInt32();
        uint modules = r.ReadUInt32();

        // An unrecognised scheme means a build we cannot interpret the fields of; treat it
        // as "no information" rather than misreading, so it warns instead of corrupting.
        if (scheme != ContentDigest.CurrentScheme)
        {
            return ContentDigest.Unknown;
        }

        return new ContentDigest(
            knownMask, exe, overrideCensus, overrideCount, twoDa, appearance, appearanceRows,
            tlk, tlkCount, journal, modules);
    }

    public static int Write(Span<byte> buffer, RejectPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Reject);
        w.WriteString(packet.Reason);
        return w.Failed ? 0 : w.Position;
    }

    public static RejectPacket? ReadReject(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Reject)
        {
            return null;
        }

        string reason = r.ReadString();
        return r.Failed ? null : new RejectPacket(reason);
    }

    public static int Write(Span<byte> buffer, PingPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Ping);
        w.WriteUInt32(packet.PeerId);
        w.WriteInt64(packet.SentTimeMs);
        return w.Failed ? 0 : w.Position;
    }

    public static PingPacket? ReadPing(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Ping)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        long sent = r.ReadInt64();
        return r.Failed ? null : new PingPacket(peerId, sent);
    }

    public static int Write(Span<byte> buffer, PongPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Pong);
        w.WriteInt64(packet.OriginalSentTimeMs);
        w.WriteInt64(packet.ServerTimeMs);
        return w.Failed ? 0 : w.Position;
    }

    public static PongPacket? ReadPong(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Pong)
        {
            return null;
        }

        long original = r.ReadInt64();
        long serverTime = r.ReadInt64();
        return r.Failed ? null : new PongPacket(original, serverTime);
    }

    public static int Write(Span<byte> buffer, ByePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Bye);
        w.WriteUInt32(packet.PeerId);
        return w.Failed ? 0 : w.Position;
    }

    public static ByePacket? ReadBye(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Bye)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        return r.Failed ? null : new ByePacket(peerId);
    }

    public static int Write(Span<byte> buffer, HostByePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.HostBye);
        w.WriteUInt32(packet.SessionId);
        return w.Failed ? 0 : w.Position;
    }

    public static HostByePacket? ReadHostBye(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.HostBye)
        {
            return null;
        }

        uint sessionId = r.ReadUInt32();
        return r.Failed ? null : new HostByePacket(sessionId);
    }

    public static int Write(Span<byte> buffer, WorldDigestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldDigest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.Digest.GlobalsCatalogue);
        w.WriteUInt32(packet.Digest.SaveIdentity);
        w.WriteUInt32(packet.Digest.ModuleGit);
        w.WriteString(packet.Digest.Module, ProtocolConstants.MaxModuleLength);
        // Roadmap item 14 phase 2 (docs/research/session-save.md): the sender's own
        // world-load edge counter, so a receiver can classify a peer's SaveIdentity change
        // as a save or a load without a new packet — see WorldDigest.LoadSerial's own doc
        // comment. Appended after the existing fields so a peer running the previous wire
        // shape (protocol version bump handles the mismatch at Hello) is never mis-parsed.
        w.WriteUInt32(packet.Digest.LoadSerial);
        return w.Failed ? 0 : w.Position;
    }

    public static WorldDigestPacket? ReadWorldDigest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.WorldDigest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint globals = r.ReadUInt32();
        uint saveIdentity = r.ReadUInt32();
        uint moduleGit = r.ReadUInt32();
        string module = r.ReadString();
        uint loadSerial = r.ReadUInt32();
        return r.Failed
            ? null
            : new WorldDigestPacket(peerId, new WorldDigest(globals, saveIdentity, module, moduleGit, loadSerial));
    }
}
