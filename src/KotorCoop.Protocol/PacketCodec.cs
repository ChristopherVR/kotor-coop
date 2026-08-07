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
        w.WriteUInt32(packet.PasswordHash);
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

    public static int Write(Span<byte> buffer, ContainerLootPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ContainerLoot);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt32(packet.ContainerId);
        w.WriteUInt32(packet.ItemId);
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
        return r.Failed ? null : new ContainerLootPacket(origin, opSeq, containerId, itemId);
    }

    public static int Write(Span<byte> buffer, ContainerLootAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ContainerLootAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
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
        return r.Failed ? null : new ContainerLootAckPacket(peerId, opSeq);
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
            w.WriteVector3(enemy.Position);
            w.WriteSingle(enemy.Facing);
            w.WriteUInt32(enemy.Animation);
            w.WriteUInt32(enemy.OneShotSerial);
            w.WriteUInt16(enemy.OneShotAnimation);
            w.WriteBool(enemy.Plot);
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
            Vector3f position = r.ReadVector3();
            float facing = r.ReadSingle();
            uint animation = r.ReadUInt32();
            uint oneShotSerial = r.ReadUInt32();
            ushort oneShotAnimation = r.ReadUInt16();
            bool plot = r.ReadBool();

            if (r.Failed)
            {
                return null;
            }

            enemies.Add(new EnemyState(
                objectId, current, max, dead, position, facing, animation,
                oneShotSerial, oneShotAnimation, plot));
        }

        return new EnemySnapshotPacket(serverTime, module, enemies);
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

    public static int Write(Span<byte> buffer, PhantomDamagePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PhantomDamage);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteUInt32(packet.TargetPeerId);
        w.WriteUInt32(packet.CumulativeDamage);
        w.WriteUInt32(packet.AttackerId);

        return w.Failed ? 0 : w.Position;
    }

    public static PhantomDamagePacket? ReadPhantomDamage(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PhantomDamage)
        {
            return null;
        }

        uint from = r.ReadUInt32();
        uint target = r.ReadUInt32();
        uint cumulative = r.ReadUInt32();
        uint attacker = r.ReadUInt32();

        return r.Failed ? null : new PhantomDamagePacket(from, target, cumulative, attacker);
    }

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

    public static int Write(Span<byte> buffer, PartySnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartySnapshot);
        w.WriteInt64(packet.ServerTimeMs);

        int count = Math.Min(packet.Members.Count, ProtocolConstants.MaxPartyMembers);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            PartyMemberState m = packet.Members[i];
            w.WriteUInt8(m.SlotIndex);
            w.WriteVector3(m.Position);
            w.WriteSingle(m.Facing);
            w.WriteUInt32(m.Animation);
            w.WriteInt32(m.Health);
            w.WriteUInt32(m.OneShotSerial);
            w.WriteUInt16(m.OneShotAnimation);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PartySnapshotPacket? ReadPartySnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartySnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxPartyMembers)
        {
            return null;
        }

        List<PartyMemberState> members = new(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = r.ReadUInt8();
            Vector3f position = r.ReadVector3();
            float facing = r.ReadSingle();
            uint animation = r.ReadUInt32();
            int health = r.ReadInt32();
            uint oneShotSerial = r.ReadUInt32();
            ushort oneShotAnimation = r.ReadUInt16();

            if (r.Failed)
            {
                return null;
            }

            members.Add(new PartyMemberState(
                slot, position, facing, animation, health, oneShotSerial, oneShotAnimation));
        }

        return new PartySnapshotPacket(serverTime, members);
    }

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

        return r.Failed
            ? null
            : new DialogueSnapshotPacket(
                serverTime,
                new DialogueState(active, owner, serial, speaker, line, partnerTag, replySerial, replyIndex, pickLog));
    }

    public static int Write(Span<byte> buffer, ProgressionSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ProgressionSnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.Gold);
        w.WriteUInt32(packet.Experience);

        return w.Failed ? 0 : w.Position;
    }

    public static ProgressionSnapshotPacket? ReadProgressionSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ProgressionSnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        uint gold = r.ReadUInt32();
        uint xp = r.ReadUInt32();

        return r.Failed ? null : new ProgressionSnapshotPacket(serverTime, gold, xp);
    }

    public static int Write(Span<byte> buffer, ChatPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Chat);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteName(packet.Name);
        w.WriteString(packet.Text);

        return w.Failed ? 0 : w.Position;
    }

    public static ChatPacket? ReadChat(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Chat)
        {
            return null;
        }

        uint fromPeerId = r.ReadUInt32();
        string name = r.ReadString();
        string text = r.ReadString();

        return r.Failed || text.Length == 0 ? null : new ChatPacket(fromPeerId, name, text);
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

        return r.Failed
            ? null
            : new DialogueRequestPacket(peerId, active, speaker, line, partnerTag, replySerial, replyIndex, pickLog);
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
        return r.Failed
            ? null
            : new WorldDigestPacket(peerId, new WorldDigest(globals, saveIdentity, module, moduleGit));
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
        uint passwordHash = r.ReadUInt32();
        ContentDigest digest = ReadDigest(ref r);
        return r.Failed ? null : new HelloPacket(name, module, nonce, digest, passwordHash);
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
