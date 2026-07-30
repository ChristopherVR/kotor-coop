namespace KotorCoop.Protocol;

/// <summary>
/// Hand-written binary serialisation. Explicit rather than reflective so the wire format
/// is auditable and stable, and so a corrupt datagram can never do more than fail a read.
/// </summary>
public static class PacketCodec
{
    // ---- write ----------------------------------------------------------------

    public static int Write(Span<byte> buffer, HelloPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Hello);
        w.WriteName(packet.PlayerName);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.ClientNonce);
        WriteDigest(ref w, packet.Digest);
        return w.Failed ? 0 : w.Position;
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
        return w.Failed ? 0 : w.Position;
    }

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

    public static int Write(Span<byte> buffer, WorldSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.WorldSnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.FlagVersion);
        w.WriteUInt32(packet.ItemVersion);
        w.WriteUInt32(packet.JournalVersion);

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

    public static int Write(Span<byte> buffer, FlagOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt32(packet.FlagId);
        w.WriteUInt8((byte)packet.Value);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, FlagAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagAck);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, FlagSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, FlagSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.FlagSnapshot);
        w.WriteUInt32(packet.Version);

        // 5 bytes per flag; stay inside one datagram.
        int max = (ProtocolConstants.MaxPacketSize - 12) / 5;
        int count = Math.Min(packet.Flags.Count, max);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            KeyValuePair<uint, sbyte> flag = packet.Flags[i];
            w.WriteUInt32(flag.Key);
            w.WriteUInt8((byte)flag.Value);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, PingPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Ping);
        w.WriteUInt32(packet.PeerId);
        w.WriteInt64(packet.SentTimeMs);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, PongPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Pong);
        w.WriteInt64(packet.OriginalSentTimeMs);
        w.WriteInt64(packet.ServerTimeMs);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, ByePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Bye);
        w.WriteUInt32(packet.PeerId);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, ItemOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt8((byte)packet.Kind);
        WriteGroundItem(ref w, packet.Item);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, ItemAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemAck);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, ItemSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, ItemSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemSnapshot);
        w.WriteUInt32(packet.Version);

        int count = Math.Min(packet.Items.Count, ProtocolConstants.MaxGroundItems);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            WriteGroundItem(ref w, packet.Items[i]);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static int Write(Span<byte> buffer, EnemySnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemySnapshot);
        w.WriteInt64(packet.ServerTimeMs);

        int count = Math.Min(packet.Enemies.Count, ProtocolConstants.MaxEnemies);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            EnemyState enemy = packet.Enemies[i];
            w.WriteUInt32(enemy.ObjectId);
            w.WriteUInt16(enemy.CurrentHealth);
            w.WriteUInt16(enemy.MaxHealth);
            w.WriteBool(enemy.Dead);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EnemySnapshotPacket? ReadEnemySnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemySnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEnemies)
        {
            return null;
        }

        List<EnemyState> enemies = new(count);
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            ushort current = r.ReadUInt16();
            ushort max = r.ReadUInt16();
            bool dead = r.ReadBool();

            if (r.Failed)
            {
                return null;
            }

            enemies.Add(new EnemyState(objectId, current, max, dead));
        }

        return new EnemySnapshotPacket(serverTime, enemies);
    }

    public static int Write(Span<byte> buffer, EnemyReportPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemyReport);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.Enemies.Count, ProtocolConstants.MaxEnemies);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            EnemyState enemy = packet.Enemies[i];
            w.WriteUInt32(enemy.ObjectId);
            w.WriteUInt16(enemy.CurrentHealth);
            w.WriteUInt16(enemy.MaxHealth);
            w.WriteBool(enemy.Dead);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EnemyReportPacket? ReadEnemyReport(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemyReport)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEnemies)
        {
            return null;
        }

        List<EnemyState> enemies = new(count);
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            ushort current = r.ReadUInt16();
            ushort max = r.ReadUInt16();
            bool dead = r.ReadBool();

            if (r.Failed)
            {
                return null;
            }

            enemies.Add(new EnemyState(objectId, current, max, dead));
        }

        return new EnemyReportPacket(peerId, module, enemies);
    }

    public static int Write(Span<byte> buffer, JournalOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.QuestTag, ProtocolConstants.MaxJournalTagLength);
        w.WriteInt32(packet.State);
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
        return r.Failed || tag.Length == 0 ? null : new JournalOpPacket(origin, opSeq, tag, state);
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
            KeyValuePair<string, int> entry = packet.Entries[i];
            w.WriteString(entry.Key, ProtocolConstants.MaxJournalTagLength);
            w.WriteInt32(entry.Value);
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

        List<KeyValuePair<string, int>> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            string tag = r.ReadString();
            int state = r.ReadInt32();
            if (r.Failed || tag.Length == 0)
            {
                return null;
            }

            entries.Add(new KeyValuePair<string, int>(tag, state));
        }

        return new JournalSnapshotPacket(version, entries);
    }

    private static void WriteGroundItem(ref PacketWriter w, GroundItem item)
    {
        w.WriteUInt32(item.ItemId);
        w.WriteString(item.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteString(item.Module, ProtocolConstants.MaxModuleLength);
        w.WriteVector3(item.Position);
    }

    // ---- read -----------------------------------------------------------------

    /// <summary>Peeks the type without consuming the body. Returns None for foreign traffic.</summary>
    public static PacketType PeekType(ReadOnlySpan<byte> buffer)
    {
        PacketReader reader = new(buffer);
        PacketType type = reader.ReadHeader();
        return reader.Failed ? PacketType.None : type;
    }

    /// <summary>
    /// Reads magic and type WITHOUT rejecting on a version mismatch, so a peer running a
    /// different co-op version can be answered with a typed Reject instead of silence. The
    /// version is returned separately; <paramref name="version"/> differing from
    /// <see cref="ProtocolConstants.Version"/> is exactly the case the strict
    /// <see cref="PeekType"/> hides. Still returns <see cref="PacketType.None"/> for
    /// non-ours traffic (wrong magic).
    /// </summary>
    public static PacketType PeekTypeAnyVersion(ReadOnlySpan<byte> buffer, out byte version)
    {
        PacketReader reader = new(buffer);
        ushort magic = reader.ReadUInt16();
        version = reader.ReadUInt8();
        byte type = reader.ReadUInt8();

        if (reader.Failed || magic != ProtocolConstants.Magic)
        {
            return PacketType.None;
        }

        return Enum.IsDefined((PacketType)type) ? (PacketType)type : PacketType.None;
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
        ContentDigest digest = ReadDigest(ref r);
        return r.Failed ? null : new HelloPacket(name, module, nonce, digest);
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
        return r.Failed ? null : new WelcomePacket(peerId, sessionId, tickRate, maxPeers, digest);
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

        return new WorldSnapshotPacket(serverTime, flagVersion, itemVersion, journalVersion, entries);
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
        uint flagId = r.ReadUInt32();
        sbyte value = (sbyte)r.ReadUInt8();
        return r.Failed ? null : new FlagOpPacket(origin, opSeq, flagId, value);
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

    public static FlagSnapshotPacket? ReadFlagSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.FlagSnapshot)
        {
            return null;
        }

        uint version = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        if (r.Failed)
        {
            return null;
        }

        List<KeyValuePair<uint, sbyte>> flags = new(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            sbyte value = (sbyte)r.ReadUInt8();
            if (r.Failed)
            {
                return null;
            }

            flags.Add(new KeyValuePair<uint, sbyte>(id, value));
        }

        return new FlagSnapshotPacket(version, flags);
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

    public static ItemOpPacket? ReadItemOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        GroundItem? item = ReadGroundItem(ref r);

        if (r.Failed || item is null || kind is not ((byte)ItemOpKind.Drop or (byte)ItemOpKind.Remove))
        {
            return null;
        }

        return new ItemOpPacket(origin, opSeq, (ItemOpKind)kind, item);
    }

    public static ItemAckPacket? ReadItemAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemAck)
        {
            return null;
        }

        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new ItemAckPacket(opSeq);
    }

    public static ItemSyncRequestPacket? ReadItemSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        return r.Failed ? null : new ItemSyncRequestPacket(peerId, version);
    }

    public static ItemSnapshotPacket? ReadItemSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemSnapshot)
        {
            return null;
        }

        uint version = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        if (r.Failed || count > ProtocolConstants.MaxGroundItems)
        {
            return null;
        }

        List<GroundItem> items = new(count);
        for (int i = 0; i < count; i++)
        {
            GroundItem? item = ReadGroundItem(ref r);
            if (r.Failed || item is null)
            {
                return null;
            }

            items.Add(item);
        }

        return new ItemSnapshotPacket(version, items);
    }

    private static GroundItem? ReadGroundItem(ref PacketReader r)
    {
        uint itemId = r.ReadUInt32();
        string resref = r.ReadString();
        string module = r.ReadString();
        Vector3f position = r.ReadVector3();
        return r.Failed ? null : new GroundItem(itemId, resref, module, position);
    }
}
