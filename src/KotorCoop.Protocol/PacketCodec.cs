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
        w.WriteUInt32(packet.LootVersion);
        w.WriteUInt64(packet.FlagDigest);
        w.WriteUInt64(packet.ItemDigest);
        w.WriteUInt64(packet.JournalDigest);
        w.WriteUInt64(packet.LootDigest);

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
        w.WriteUInt32(packet.Serial);
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

        // 13 bytes per flag (FlagId 4 + Value 1 + Serial 4 + OriginPeerId 4, protocol 31 adds
        // the last two); stay inside one datagram.
        int max = (ProtocolConstants.MaxPacketSize - 12) / 13;
        int count = Math.Min(packet.Flags.Count, max);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            FlagSnapshotEntry flag = packet.Flags[i];
            w.WriteUInt32(flag.FlagId);
            w.WriteUInt8((byte)flag.Value);
            w.WriteUInt32(flag.Serial);
            w.WriteUInt32(flag.OriginPeerId);
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

    public static int Write(Span<byte> buffer, LootSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.LootSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        w.WriteUInt64(packet.KnownDigest);
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
        return r.Failed ? null : new LootSyncRequestPacket(peerId, version, digest);
    }

    public static int Write(Span<byte> buffer, MaterialisedLootPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.MaterialisedLoot);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt32(packet.OwnerEnemyId);

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

        return r.Failed ? null : new MaterialisedLootPacket(origin, opSeq, ownerEnemyId, items);
    }

    public static int Write(Span<byte> buffer, MaterialisedLootAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.MaterialisedLootAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
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
        return r.Failed ? null : new MaterialisedLootAckPacket(peerId, opSeq);
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
            w.WriteUInt16(enemy.Appearance);
            w.WriteUInt16(enemy.Faction);
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
            ushort appearance = r.ReadUInt16();
            ushort faction = r.ReadUInt16();

            if (r.Failed)
            {
                return null;
            }

            enemies.Add(new EnemyState(
                objectId, current, max, dead, position, facing, animation,
                oneShotSerial, oneShotAnimation, plot, appearance, faction));
        }

        return new EnemySnapshotPacket(serverTime, module, enemies);
    }

    public static int Write(Span<byte> buffer, EnemyDamagePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemyDamage);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Generation);

        int count = Math.Min(packet.Damage.Count, ProtocolConstants.MaxEnemyDamageEntries);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            EnemyDamageEntry entry = packet.Damage[i];
            w.WriteUInt32(entry.ObjectId);
            w.WriteUInt32(entry.CumulativeDamage);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EnemyDamagePacket? ReadEnemyDamage(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemyDamage)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint generation = r.ReadUInt32();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEnemyDamageEntries)
        {
            return null;
        }

        List<EnemyDamageEntry> damage = new(count);
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            uint cumulative = r.ReadUInt32();

            if (r.Failed)
            {
                return null;
            }

            damage.Add(new EnemyDamageEntry(objectId, cumulative));
        }

        return new EnemyDamagePacket(peerId, module, generation, damage);
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

    public static int Write(Span<byte> buffer, PeerEffectPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PeerEffect);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteUInt32(packet.TargetPeerId);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.DurationMs);
        w.WriteUInt32(packet.Magnitude);
        w.WriteUInt32(packet.Sequence);

        return w.Failed ? 0 : w.Position;
    }

    public static PeerEffectPacket? ReadPeerEffect(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PeerEffect)
        {
            return null;
        }

        uint from = r.ReadUInt32();
        uint target = r.ReadUInt32();
        PeerEffectKind kind = (PeerEffectKind)r.ReadUInt8();
        uint durationMs = r.ReadUInt32();
        uint magnitude = r.ReadUInt32();
        uint sequence = r.ReadUInt32();

        return r.Failed ? null : new PeerEffectPacket(from, target, kind, durationMs, magnitude, sequence);
    }

    public static int Write(Span<byte> buffer, BarkPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Bark);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Speaker);
        w.WriteUInt32(packet.Flags);
        w.WriteUInt32(packet.Serial);

        // ⚠ RAW BYTES, NOT WriteString. The line comes out of the game's own TLK in the
        // engine's 8-bit encoding; WriteString is UTF-8, so an accented character would
        // expand and a 200-byte line could overrun its single length byte and be truncated
        // mid-sentence. The resref is a fixed CResRef char[16] and is not a string at all.
        byte[] text = packet.Text ?? [];
        int length = Math.Min(text.Length, BarkPacket.MaxTextBytes);
        w.WriteUInt8((byte)length);
        w.WriteBytes(text.AsSpan(0, length));

        byte[] sound = packet.SoundResRef ?? [];
        byte[] resRef = new byte[BarkPacket.ResRefBytes];
        sound.AsSpan(0, Math.Min(sound.Length, BarkPacket.ResRefBytes)).CopyTo(resRef);
        w.WriteBytes(resRef);

        return w.Failed ? 0 : w.Position;
    }

    public static BarkPacket? ReadBark(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Bark)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint speaker = r.ReadUInt32();
        uint flags = r.ReadUInt32();
        uint serial = r.ReadUInt32();

        int length = r.ReadUInt8();
        if (length > BarkPacket.MaxTextBytes)
        {
            return null; // a length this side never writes: treat the datagram as malformed
        }

        byte[] text = new byte[length];
        r.ReadBytes(text);

        byte[] resRef = new byte[BarkPacket.ResRefBytes];
        r.ReadBytes(resRef);

        return r.Failed ? null : new BarkPacket(peerId, module, speaker, text, resRef, flags, serial);
    }

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

    public static int Write(Span<byte> buffer, InteractionClaimPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.InteractionClaim);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteString(packet.Tag, ProtocolConstants.MaxTagLength);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt8(packet.Active ? (byte)1 : (byte)0);
        w.WriteUInt32(packet.TargetId); // version 37: InteractionKind.Container's placeable id
        return w.Failed ? 0 : w.Position;
    }

    public static InteractionClaimPacket? ReadInteractionClaim(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.InteractionClaim)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        string tag = r.ReadString();
        byte kind = r.ReadUInt8();
        byte active = r.ReadUInt8();
        uint targetId = r.ReadUInt32();

        // ⚠ An unknown kind is DROPPED rather than coerced. A lock whose release rules this
        // build does not know is worse than no lock at all, because it is the OWNER that
        // would hold the body and never learn when to stop.
        if (r.Failed || kind > (byte)InteractionKind.Container)
        {
            return null;
        }

        return new InteractionClaimPacket(peerId, module, tag, (InteractionKind)kind, active != 0, targetId);
    }

    public static int Write(Span<byte> buffer, InteractionLockPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.InteractionLock);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.Locks.Count, ProtocolConstants.MaxInteractionLocks);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            InteractionLock entry = packet.Locks[i];
            w.WriteUInt32(entry.PeerId);
            w.WriteString(entry.Tag, ProtocolConstants.MaxTagLength);
            w.WriteUInt8((byte)entry.Kind);
            w.WriteUInt32(entry.TargetId); // version 37
        }

        return w.Failed ? 0 : w.Position;
    }

    public static InteractionLockPacket? ReadInteractionLock(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.InteractionLock)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        string module = r.ReadString();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxInteractionLocks)
        {
            return null;
        }

        List<InteractionLock> locks = new(count);
        for (int i = 0; i < count; i++)
        {
            uint peerId = r.ReadUInt32();
            string tag = r.ReadString();
            byte kind = r.ReadUInt8();
            uint targetId = r.ReadUInt32();

            if (r.Failed || kind > (byte)InteractionKind.Container)
            {
                return null;
            }

            locks.Add(new InteractionLock(peerId, tag, (InteractionKind)kind, targetId));
        }

        return r.Failed ? null : new InteractionLockPacket(serverTimeMs, module, locks);
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
                    pickLog, nodeIndex, nodeSerial));
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

    public static int Write(Span<byte> buffer, ProgressionReportPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ProgressionReport);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt32(packet.GoldEarned);
        w.WriteUInt32(packet.GoldSpent);
        w.WriteUInt32(packet.Experience);

        return w.Failed ? 0 : w.Position;
    }

    public static ProgressionReportPacket? ReadProgressionReport(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ProgressionReport)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint generation = r.ReadUInt32();
        uint earned = r.ReadUInt32();
        uint spent = r.ReadUInt32();
        uint experience = r.ReadUInt32();

        return r.Failed
            ? null
            : new ProgressionReportPacket(peerId, generation, earned, spent, experience);
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
        w.WriteInt32(packet.NodeIndex);
        w.WriteUInt32(packet.NodeSerial);

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
                nodeIndex, nodeSerial);
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

    public static int Write(Span<byte> buffer, JournalOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.JournalOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.QuestTag, ProtocolConstants.MaxJournalTagLength);
        w.WriteInt32(packet.State);
        w.WriteUInt32(packet.Serial);
        w.WriteUInt8((byte)(packet.Removed ? 1 : 0));
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
        uint serial = r.ReadUInt32();
        bool removed = r.ReadUInt8() != 0;
        return r.Failed || tag.Length == 0
            ? null
            : new JournalOpPacket(origin, opSeq, tag, state, serial, removed);
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
            JournalSnapshotEntry entry = packet.Entries[i];
            w.WriteString(entry.QuestTag, ProtocolConstants.MaxJournalTagLength);
            w.WriteInt32(entry.State);
            w.WriteUInt32(entry.Serial);
            w.WriteUInt32(entry.OriginPeerId);
            w.WriteUInt8((byte)(entry.Removed ? 1 : 0));
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

        List<JournalSnapshotEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            string tag = r.ReadString();
            int state = r.ReadInt32();
            uint serial = r.ReadUInt32();
            uint origin = r.ReadUInt32();
            bool removed = r.ReadUInt8() != 0;
            if (r.Failed || tag.Length == 0)
            {
                return null;
            }

            entries.Add(new JournalSnapshotEntry(tag, state, serial, origin, removed));
        }

        return new JournalSnapshotPacket(version, entries);
    }

    private static void WriteGroundItem(ref PacketWriter w, GroundItem item)
    {
        w.WriteUInt32(item.ItemId);
        w.WriteString(item.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteString(item.Module, ProtocolConstants.MaxModuleLength);
        w.WriteVector3(item.Position);

        // Nine fixed bytes of per-instance state, always present. A variable-length
        // encoding would save eight bytes on an unknown identity and buy a second way for
        // the two sides to disagree about how long an item entry is; the worst-case
        // snapshot still fits one datagram with the fixed form (asserted by a test).
        w.WriteUInt16(item.Identity.StackSize);
        w.WriteUInt8(item.Identity.Charges);
        w.WriteUInt8(item.Identity.MaxCharges);
        w.WriteUInt32(item.Identity.Upgrades);
        w.WriteUInt8(item.Identity.Flags);
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
        uint lootVersion = r.ReadUInt32();
        ulong flagDigest = r.ReadUInt64();
        ulong itemDigest = r.ReadUInt64();
        ulong journalDigest = r.ReadUInt64();
        ulong lootDigest = r.ReadUInt64();
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

        return new WorldSnapshotPacket(
            serverTime, flagVersion, itemVersion, journalVersion, lootVersion,
            flagDigest, itemDigest, journalDigest, lootDigest, entries);
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
        uint serial = r.ReadUInt32();
        return r.Failed ? null : new FlagOpPacket(origin, opSeq, flagId, value, serial);
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

        List<FlagSnapshotEntry> flags = new(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            sbyte value = (sbyte)r.ReadUInt8();
            uint serial = r.ReadUInt32();
            uint origin = r.ReadUInt32();
            if (r.Failed)
            {
                return null;
            }

            flags.Add(new FlagSnapshotEntry(id, value, serial, origin));
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

        ushort stackSize = r.ReadUInt16();
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();

        return r.Failed
            ? null
            : new GroundItem(
                itemId, resref, module, position,
                new ItemIdentity(stackSize, charges, maxCharges, upgrades, flags));
    }

    public static int Write(Span<byte> buffer, PartyRosterPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyRoster);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt16(packet.Roster.AvailableMask);
        w.WriteUInt16(packet.Roster.PartyMask);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyRosterPacket? ReadPartyRoster(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyRoster)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        ushort available = r.ReadUInt16();
        ushort party = r.ReadUInt16();

        if (r.Failed)
        {
            return null;
        }

        // ⚠ BITS ABOVE THE NINTH TAKE THE WHOLE PACKET DOWN, they are not masked off.
        //
        // A slot index is fed straight to the engine's own AddPartyMember/RemovePartyMember,
        // which validate it (0x00565648: `cmp edi,9 / jge refuse`) — but the reason to refuse
        // HERE is that a tenth bit means the sender is not the build it claims to be, and the
        // nine bits we would keep are then no more trustworthy than the one we threw away.
        // Quietly masking would apply a roster we have evidence against.
        if ((available & ~PartyRoster.SlotMask) != 0 || (party & ~PartyRoster.SlotMask) != 0)
        {
            return null;
        }

        // A companion cannot be in the party without having been recruited; the engine will not
        // produce this and will not accept it (AddMember tests +0x30 at 0x00565651 before it
        // touches anything). Dropping it stops a corrupt level from making a client spend every
        // pass attempting an add its own engine refuses.
        if ((party & ~available) != 0)
        {
            return null;
        }

        return new PartyRosterPacket(serverTimeMs, new PartyRoster(available, party));
    }

    public static int Write(Span<byte> buffer, CreatureLivenessPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CreatureLiveness);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt64(packet.Digest);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt8(packet.PageIndex);
        w.WriteUInt8(packet.PageCount);

        IReadOnlyList<CreatureLivenessEntry> entries = packet.Entries;
        int count = Math.Min(entries.Count, CreatureLivenessPacket.MaxEntriesPerPacket);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            CreatureLivenessEntry entry = entries[i];
            w.WriteUInt32(entry.ObjectId);
            w.WriteUInt8((byte)entry.State);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CreatureLivenessPacket? ReadCreatureLiveness(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CreatureLiveness)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        string module = r.ReadString();
        ulong digest = r.ReadUInt64();
        uint generation = r.ReadUInt32();
        byte pageIndex = r.ReadUInt8();
        byte pageCount = r.ReadUInt8();

        int count = r.ReadUInt8();
        if (r.Failed || count > CreatureLivenessPacket.MaxEntriesPerPacket)
        {
            // A count this side never writes: treat the datagram as malformed rather than
            // allocating on a number a peer chose.
            return null;
        }

        CreatureLivenessEntry[] entries = new CreatureLivenessEntry[count];
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            byte state = r.ReadUInt8();

            // An unrecognised state byte from a future build: treat it as Alive rather than
            // failing the whole page. Alive is the "do nothing" verdict on the receiving side
            // (see LivenessRule), which is the safe direction to fail towards — never resurrect,
            // never destroy a body on a state this build cannot interpret.
            entries[i] = new CreatureLivenessEntry(
                objectId, Enum.IsDefined((CreatureLivenessState)state)
                    ? (CreatureLivenessState)state
                    : CreatureLivenessState.Alive);
        }

        return r.Failed ? null : new CreatureLivenessPacket(
            serverTimeMs, module, digest, generation, pageIndex, pageCount, entries);
    }
}
