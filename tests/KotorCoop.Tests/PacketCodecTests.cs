using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class PacketCodecTests
{
    private static readonly PlayerState SampleState =
        new(new Vector3f(12.5f, -3.25f, 0.75f), 1.75f, "end_m01aa", 87, 42,
            Flags: PlayerState.FlagInDialogue | PlayerState.FlagInCombat | PlayerState.FlagInMinigame);

    [Fact]
    public void JournalPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        JournalOpPacket op = new(2, 17, "k_swg_bastila", 30, Serial: 4);
        int opLength = PacketCodec.Write(buffer, op);
        Assert.Equal(op, PacketCodec.ReadJournalOp(buffer[..opLength]));

        JournalOpPacket removeOp = new(2, 18, "k_swg_bastila", 0, Serial: 5, Removed: true);
        int removeLength = PacketCodec.Write(buffer, removeOp);
        Assert.Equal(removeOp, PacketCodec.ReadJournalOp(buffer[..removeLength]));

        JournalAckPacket ack = new(17);
        int ackLength = PacketCodec.Write(buffer, ack);
        Assert.Equal(ack, PacketCodec.ReadJournalAck(buffer[..ackLength]));

        JournalSyncRequestPacket request = new(2, 5);
        int requestLength = PacketCodec.Write(buffer, request);
        Assert.Equal(request, PacketCodec.ReadJournalSyncRequest(buffer[..requestLength]));
    }

    [Fact]
    public void FullJournalSnapshotPartFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<JournalSnapshotEntry> entries = [.. Enumerable
            .Range(0, ProtocolConstants.MaxJournalEntriesPerPacket)
            .Select(i => new JournalSnapshotEntry(
                new string('q', ProtocolConstants.MaxJournalTagLength) + i, i * 10,
                Serial: (uint)i, OriginPeerId: 2, Removed: i % 2 == 0))];

        int length = PacketCodec.Write(buffer, new JournalSnapshotPacket(9, entries));
        Assert.True(length > 0, "worst-case journal snapshot part must fit inside one packet");

        JournalSnapshotPacket? decoded = PacketCodec.ReadJournalSnapshot(buffer[..length]);
        Assert.NotNull(decoded);
        Assert.Equal(9u, decoded.Version);
        Assert.Equal(ProtocolConstants.MaxJournalEntriesPerPacket, decoded.Entries.Count);
    }

    [Fact]
    public void WorldDigestRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        WorldDigestPacket original = new(
            2, new WorldDigest(0xDEADBEEF, 0x1234_5678, "end_m01aa", 0x9ABC_DEF0));

        int length = PacketCodec.Write(buffer, original);
        Assert.Equal(original, PacketCodec.ReadWorldDigest(buffer[..length]));
    }

    [Fact]
    public void WorldDigestLoadSerialRoundTrips()
    {
        // Roadmap item 14 phase 2 (docs/research/session-save.md): LoadSerial rides the
        // existing world-digest channel unmodified so a receiver can classify a peer's
        // SaveIdentity change as a save or a load without a new packet.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        WorldDigestPacket original = new(
            4, new WorldDigest(0xDEADBEEF, 0x1234_5678, "end_m01aa", 0x9ABC_DEF0, LoadSerial: 42));

        int length = PacketCodec.Write(buffer, original);
        WorldDigestPacket? decoded = PacketCodec.ReadWorldDigest(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(42u, decoded.Digest.LoadSerial);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void AnEmptyWorldDigestRoundTripsWithoutLosingItsEmptiness()
    {
        // A digest whose fields are all unresolved must arrive as exactly that — the
        // receiver's Unknown-is-not-mismatch handling depends on the zeros surviving.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        WorldDigestPacket original = new(3, WorldDigest.Empty);

        int length = PacketCodec.Write(buffer, original);
        WorldDigestPacket? decoded = PacketCodec.ReadWorldDigest(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.True(decoded.Digest.IsEmpty);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void PartySnapshotRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PartySnapshotPacket original = new(999L,
        [
            new PartyMemberState(0, new Vector3f(10f, 20f, -1f), 1.5f, 0x2712, 25, 4, 0x271B),
            new PartyMemberState(3, new Vector3f(-4f, 8f, 0f), -2.1f, 0x2714, 12),
        ]);

        int length = PacketCodec.Write(buffer, original);
        PartySnapshotPacket? decoded = PacketCodec.ReadPartySnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(999L, decoded.ServerTimeMs);
        Assert.Equal(original.Members, decoded.Members);
    }

    [Fact]
    public void EnemyDamageRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        EnemyDamagePacket original = new(2, "end_m01aa", 4,
            [new EnemyDamageEntry(0x14, 27), new EnemyDamageEntry(0x17, 3)]);

        int length = PacketCodec.Write(buffer, original);
        EnemyDamagePacket? decoded = PacketCodec.ReadEnemyDamage(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(2u, decoded.PeerId);
        Assert.Equal("end_m01aa", decoded.Module);
        Assert.Equal(4u, decoded.Generation);
        Assert.Equal(original.Damage, decoded.Damage);
    }

    [Fact]
    public void ProgressionReportRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        ProgressionReportPacket original = new(
            PeerId: 2, Generation: 0xDEADBEEF, GoldEarned: 400, GoldSpent: 250, Experience: 1500);

        int length = PacketCodec.Write(buffer, original);
        ProgressionReportPacket? decoded = PacketCodec.ReadProgressionReport(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ATruncatedProgressionReportIsRejected()
    {
        // A half-read ledger would be applied as a real contribution: garbage that survives
        // decoding here becomes gold appearing in or vanishing from the party's purse.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new ProgressionReportPacket(2, 1, 400, 250, 1500));

        Assert.Null(PacketCodec.ReadProgressionReport(buffer[..(length - 1)]));
    }

    [Fact]
    public void AFullEnemyDamageReportFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<EnemyDamageEntry> damage = [];
        for (int i = 0; i < ProtocolConstants.MaxEnemyDamageEntries; i++)
        {
            damage.Add(new EnemyDamageEntry((uint)(0x100 + i), uint.MaxValue));
        }

        int length = PacketCodec.Write(
            buffer, new EnemyDamagePacket(uint.MaxValue, new string('m', ProtocolConstants.MaxModuleLength),
                uint.MaxValue, damage));

        Assert.InRange(length, 1, ProtocolConstants.MaxPacketSize);
        Assert.Equal(damage, PacketCodec.ReadEnemyDamage(buffer[..length])!.Damage);
    }

    [Fact]
    public void HelloRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HelloPacket original = new("Revan", "end_m01aa", 12345, ContentDigest.Unknown, 0xABCD);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void StateUpdateRoundTripsExactly()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        StateUpdatePacket original = new(7, 900, 1234567890L, SampleState);

        int length = PacketCodec.Write(buffer, original);
        StateUpdatePacket? decoded = PacketCodec.ReadStateUpdate(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.PeerId, decoded.PeerId);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.ClientTimeMs, decoded.ClientTimeMs);

        // Floats must survive bit-exact: these are game coordinates, not display values.
        Assert.Equal(SampleState.Position, decoded.State.Position);
        Assert.Equal(SampleState.Facing, decoded.State.Facing);
        Assert.Equal(SampleState.Flags, decoded.State.Flags);
        Assert.Equal(SampleState.Module, decoded.State.Module);
        Assert.Equal(SampleState.Health, decoded.State.Health);
        Assert.Equal(SampleState.Animation, decoded.State.Animation);

        // FlagInMinigame is the newest bit in the flags dword (roadmap item 8): a peer's
        // instance parks their phantom on this, so it must survive the wire intact.
        Assert.True(decoded.State.IsInMinigame);
    }

    [Fact]
    public void MinigameFlagRoundTripsIndependentlyOfTheOtherPresenceFlags()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PlayerState notInMinigame = SampleState with
        {
            Flags = PlayerState.FlagInDialogue | PlayerState.FlagInCombat,
        };

        int length = PacketCodec.Write(buffer, new StateUpdatePacket(1, 1, 1, notInMinigame));
        StateUpdatePacket? decoded = PacketCodec.ReadStateUpdate(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.False(decoded.State.IsInMinigame);
        Assert.True(decoded.State.IsInDialogue);
        Assert.True(decoded.State.IsInCombat);
    }

    [Fact]
    public void WorldSnapshotRoundTripsAllEntries()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<SnapshotEntry> entries =
        [
            new(1, "Revan", SampleState),
            new(2, "Malak", SampleState with { Module = "tar_m02aa", Health = 12 }),
            new(3, "Bastila", SampleState with { Position = new Vector3f(1f, 2f, 3f) }),
        ];

        WorldSnapshotPacket original = new(
            555L, 9, 4, 7, 2,
            0x1122334455667788UL,
            0x99AABBCCDDEEFF00UL,
            0x0102030405060708UL,
            0xFEDCBA9876543210UL,
            entries);
        int length = PacketCodec.Write(buffer, original);

        WorldSnapshotPacket? decoded = PacketCodec.ReadWorldSnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(555L, decoded.ServerTimeMs);
        Assert.Equal(9u, decoded.FlagVersion);
        Assert.Equal(4u, decoded.ItemVersion);
        Assert.Equal(7u, decoded.JournalVersion);
        Assert.Equal(2u, decoded.LootVersion);
        Assert.Equal(0x1122334455667788UL, decoded.FlagDigest);
        Assert.Equal(0x99AABBCCDDEEFF00UL, decoded.ItemDigest);
        Assert.Equal(0x0102030405060708UL, decoded.JournalDigest);
        Assert.Equal(0xFEDCBA9876543210UL, decoded.LootDigest);
        Assert.Equal(3, decoded.Entries.Count);
        Assert.Equal("Malak", decoded.Entries[1].Name);
        Assert.Equal("tar_m02aa", decoded.Entries[1].State.Module);
        Assert.Equal(12, decoded.Entries[1].State.Health);
        Assert.Equal(new Vector3f(1f, 2f, 3f), decoded.Entries[2].State.Position);
    }

    /// <summary>
    /// ⚠ This test used to lie. It filled the name and module to their caps and called that
    /// the worst case, but it built every entry from a PlayerState whose three equipment
    /// resrefs are empty strings — 3 bytes where the worst case is 51. It measured 857 bytes
    /// against a 1200-byte limit and passed, while the real full-lobby snapshot was 1241 and
    /// was silently dropped every single tick (and, until this change, took every other
    /// broadcast down with it — see CoopHost.BroadcastState).
    ///
    /// Every field is now filled to its own cap, so the number this asserts is the number the
    /// wire actually has to carry.
    /// </summary>
    [Fact]
    public void FullPeerSnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        PlayerState worstCase = SampleState with
        {
            Module = new string('m', ProtocolConstants.MaxModuleLength),
            EquippedBody = new string('b', ProtocolConstants.MaxResrefLength),
            EquippedRightWeapon = new string('r', ProtocolConstants.MaxResrefLength),
            EquippedLeftWeapon = new string('l', ProtocolConstants.MaxResrefLength),
        };

        List<SnapshotEntry> entries = [.. Enumerable.Range(1, ProtocolConstants.MaxPeers)
            .Select(i => new SnapshotEntry(
                (uint)i,
                new string('n', ProtocolConstants.MaxNameLength),
                worstCase))];

        int length = PacketCodec.Write(
            buffer, new WorldSnapshotPacket(1, 1, 1, 1, 1, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, entries));

        // A snapshot that does not fit is dropped every tick, so nobody moves.
        Assert.True(length > 0, "worst-case snapshot must fit inside one packet");
        Assert.True(length <= ProtocolConstants.MaxPacketSize);

        // And it round-trips at that size: a receive buffer that is one byte short shows up
        // as a null decode, not as a short read.
        WorldSnapshotPacket? decoded = PacketCodec.ReadWorldSnapshot(buffer[..length]);
        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxPeers, decoded.Entries.Count);
        Assert.Equal(worstCase.EquippedRightWeapon, decoded.Entries[^1].State.EquippedRightWeapon);
    }

    [Fact]
    public void FlagPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        FlagOpPacket op = new(4, 99, 1001, 1, Serial: 6);
        int opLength = PacketCodec.Write(buffer, op);
        Assert.Equal(op, PacketCodec.ReadFlagOp(buffer[..opLength]));

        // Negative values must survive: KOTOR's global numbers are signed bytes.
        FlagOpPacket negative = new(4, 100, 1002, -7, Serial: 7);
        int negativeLength = PacketCodec.Write(buffer, negative);
        Assert.Equal(negative, PacketCodec.ReadFlagOp(buffer[..negativeLength]));

        List<FlagSnapshotEntry> flags =
            [new(1, 1, 1, 4), new(2, 0, 2, 4), new(9999, 42, 3, 1)];
        int snapshotLength = PacketCodec.Write(buffer, new FlagSnapshotPacket(17, flags));
        FlagSnapshotPacket? decoded = PacketCodec.ReadFlagSnapshot(buffer[..snapshotLength]);

        Assert.NotNull(decoded);
        Assert.Equal(17u, decoded.Version);
        Assert.Equal(flags, decoded.Flags);
    }

    [Fact]
    public void AppearanceSurvivesTheStateRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PlayerState state = SampleState with { Appearance = 174 };

        int length = PacketCodec.Write(buffer, new StateUpdatePacket(1, 1, 1, state));
        StateUpdatePacket? decoded = PacketCodec.ReadStateUpdate(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(174u, decoded.State.Appearance);
    }

    [Fact]
    public void ItemPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        GroundItem item = new(0x20001, "g_w_lghtsbr01", "end_m01aa", new Vector3f(10f, 20f, 1.5f));
        ItemOpPacket op = new(2, 7, ItemOpKind.Drop, item);

        int opLength = PacketCodec.Write(buffer, op);
        Assert.Equal(op, PacketCodec.ReadItemOp(buffer[..opLength]));

        int ackLength = PacketCodec.Write(buffer, new ItemAckPacket(7));
        Assert.Equal(7u, PacketCodec.ReadItemAck(buffer[..ackLength])!.OpSequence);

        int requestLength = PacketCodec.Write(buffer, new ItemSyncRequestPacket(2, 5));
        Assert.Equal(new ItemSyncRequestPacket(2, 5), PacketCodec.ReadItemSyncRequest(buffer[..requestLength]));
    }

    /// <summary>
    /// The whole point of protocol 24: what makes an item THIS item has to survive the wire.
    /// A stack of twenty half-spent charges with three upgrades bolted in must decode as
    /// exactly that, and an identity nobody read must decode as UNKNOWN rather than as a
    /// plausible-looking stock item.
    /// </summary>
    [Fact]
    public void ItemIdentityRoundTripsAndUnknownStaysUnknown()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        ItemIdentity identity = new(
            StackSize: 20,
            Charges: 7,
            MaxCharges: 50,
            Upgrades: 0b1011,
            Flags: ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified | ItemIdentity.FlagStolen);

        GroundItem item = new(
            0x20001, "g_w_lghtsbr01", "end_m01aa", new Vector3f(1f, 2f, 3f), identity);

        int length = PacketCodec.Write(buffer, new ItemOpPacket(2, 7, ItemOpKind.Drop, item));
        ItemOpPacket? decoded = PacketCodec.ReadItemOp(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(identity, decoded.Item.Identity);
        Assert.True(decoded.Item.Identity.IsKnown);
        Assert.True(decoded.Item.Identity.Identified);
        Assert.True(decoded.Item.Identity.Stolen);
        Assert.False(decoded.Item.Identity.NonEquippable);

        GroundItem stock = new(0x20002, "g_i_medeqpmnt01", "end_m01aa", Vector3f.Zero);
        int stockLength = PacketCodec.Write(buffer, new ItemOpPacket(2, 8, ItemOpKind.Drop, stock));
        ItemOpPacket? stockDecoded = PacketCodec.ReadItemOp(buffer[..stockLength]);

        Assert.NotNull(stockDecoded);
        Assert.False(stockDecoded.Item.Identity.IsKnown);
        Assert.Equal(ItemIdentity.Unknown, stockDecoded.Item.Identity);
    }

    [Fact]
    public void ItemAckAndSyncRequestRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        int ackLength = PacketCodec.Write(buffer, new ItemAckPacket(7));
        Assert.Equal(7u, PacketCodec.ReadItemAck(buffer[..ackLength])!.OpSequence);

        int requestLength = PacketCodec.Write(buffer, new ItemSyncRequestPacket(2, 5));
        Assert.Equal(new ItemSyncRequestPacket(2, 5), PacketCodec.ReadItemSyncRequest(buffer[..requestLength]));
    }

    [Fact]
    public void ContainerLootPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        ContainerLootPacket op = new(1, 42, 0x0053, 0x00C9);
        int opLength = PacketCodec.Write(buffer, op);
        Assert.Equal(op, PacketCodec.ReadContainerLoot(buffer[..opLength]));

        ContainerLootAckPacket ack = new(3, 42);
        int ackLength = PacketCodec.Write(buffer, ack);
        Assert.Equal(ack, PacketCodec.ReadContainerLootAck(buffer[..ackLength]));
    }

    [Fact]
    public void LootSyncRequestRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        LootSyncRequestPacket request = new(3, 12, 0xDEADBEEFCAFEBABEUL);
        int length = PacketCodec.Write(buffer, request);

        Assert.Equal(request, PacketCodec.ReadLootSyncRequest(buffer[..length]));
    }

    [Fact]
    public void MaterialisedLootPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        MaterialisedLootItem[] items =
        [
            new MaterialisedLootItem("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            new MaterialisedLootItem(
                "g_a_class4001",
                new ItemIdentity(20, 3, 50, 0x5, ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified)),
        ];

        MaterialisedLootPacket op = new(1, 42, 0xE, items);
        int opLength = PacketCodec.Write(buffer, op);
        MaterialisedLootPacket? read = PacketCodec.ReadMaterialisedLoot(buffer[..opLength]);

        Assert.NotNull(read);
        Assert.Equal(op.OriginPeerId, read.OriginPeerId);
        Assert.Equal(op.OpSequence, read.OpSequence);
        Assert.Equal(op.OwnerEnemyId, read.OwnerEnemyId);
        Assert.Equal(items, read.Items);

        MaterialisedLootAckPacket ack = new(3, 42);
        int ackLength = PacketCodec.Write(buffer, ack);
        Assert.Equal(ack, PacketCodec.ReadMaterialisedLootAck(buffer[..ackLength]));
    }

    [Fact]
    public void MaterialisedLootFitsInOneDatagramAtMaxItems()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Every resref at its cap length, which is the true worst case for one death.
        MaterialisedLootItem[] items = [.. Enumerable.Range(1, MaterialisedLootPacket.MaxItems)
            .Select(i => new MaterialisedLootItem(
                new string('a', ProtocolConstants.MaxResrefLength),
                new ItemIdentity((ushort)i, 3, 50, 0xFFFFFFFF, 0xFF)))];

        MaterialisedLootPacket op = new(1, 1, 0xE, items);
        int length = PacketCodec.Write(buffer, op);

        Assert.True(length > 0);
        MaterialisedLootPacket? read = PacketCodec.ReadMaterialisedLoot(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(MaterialisedLootPacket.MaxItems, read.Items.Count);
    }

    [Fact]
    public void DialogueGrantPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        MaterialisedLootItem[] items =
        [
            new MaterialisedLootItem("g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown)),
            new MaterialisedLootItem(
                "g_a_class4001",
                new ItemIdentity(20, 3, 50, 0x5, ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified)),
        ];

        DialogueGrantPacket op = new(7, 42, "end_trask01", items);
        int opLength = PacketCodec.Write(buffer, op);
        DialogueGrantPacket? read = PacketCodec.ReadDialogueGrant(buffer[..opLength]);

        Assert.NotNull(read);
        Assert.Equal(op.OriginPeerId, read.OriginPeerId);
        Assert.Equal(op.OpSequence, read.OpSequence);
        Assert.Equal(op.PartnerTag, read.PartnerTag);
        Assert.Equal(items, read.Items);

        DialogueGrantAckPacket ack = new(3, 7, 42);
        int ackLength = PacketCodec.Write(buffer, ack);
        Assert.Equal(ack, PacketCodec.ReadDialogueGrantAck(buffer[..ackLength]));
    }

    [Fact]
    public void DialogueGrantFitsInOneDatagramAtMaxItems()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        MaterialisedLootItem[] items = [.. Enumerable.Range(1, DialogueGrantPacket.MaxItems)
            .Select(i => new MaterialisedLootItem(
                new string('a', ProtocolConstants.MaxResrefLength),
                new ItemIdentity((ushort)i, 3, 50, 0xFFFFFFFF, 0xFF)))];

        DialogueGrantPacket op = new(1, 1, new string('b', ProtocolConstants.MaxTagLength), items);
        int length = PacketCodec.Write(buffer, op);

        Assert.True(length > 0);
        DialogueGrantPacket? read = PacketCodec.ReadDialogueGrant(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(DialogueGrantPacket.MaxItems, read.Items.Count);
    }

    [Fact]
    public void CreatureLivenessPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        CreatureLivenessEntry[] entries =
        [
            new CreatureLivenessEntry(0x14, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead),
            new CreatureLivenessEntry(0x99, CreatureLivenessState.Gone),
        ];

        CreatureLivenessPacket packet = new(12345, "tar_m03aa", 0xC0FFEEUL, 7, 1, 3, entries);
        int length = PacketCodec.Write(buffer, packet);
        CreatureLivenessPacket? read = PacketCodec.ReadCreatureLiveness(buffer[..length]);

        Assert.NotNull(read);
        Assert.Equal(packet.ServerTimeMs, read.ServerTimeMs);
        Assert.Equal(packet.Module, read.Module);
        Assert.Equal(packet.Digest, read.Digest);
        Assert.Equal(packet.Generation, read.Generation);
        Assert.Equal(packet.PageIndex, read.PageIndex);
        Assert.Equal(packet.PageCount, read.PageCount);
        Assert.Equal(entries, read.Entries);
    }

    [Fact]
    public void CreatureLivenessFitsInOneDatagramAtMaxEntries()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        CreatureLivenessEntry[] entries = [.. Enumerable.Range(0, CreatureLivenessPacket.MaxEntriesPerPacket)
            .Select(i => new CreatureLivenessEntry((uint)i, (CreatureLivenessState)(i % 3)))];

        CreatureLivenessPacket packet = new(
            long.MaxValue, new string('a', ProtocolConstants.MaxModuleLength), ulong.MaxValue,
            uint.MaxValue, 254, 255, entries);
        int length = PacketCodec.Write(buffer, packet);

        Assert.True(length > 0);
        CreatureLivenessPacket? read = PacketCodec.ReadCreatureLiveness(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(CreatureLivenessPacket.MaxEntriesPerPacket, read.Entries.Count);
    }

    [Fact]
    public void CreatureLivenessRejectsAnOversizedEntryCount()
    {
        // A count this side never writes: PacketWriter caps at MaxEntriesPerPacket, so the
        // only way to see a bigger one is a corrupt or hostile datagram, and it must fail
        // closed rather than allocate on a number a peer chose.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CreatureLiveness);
        w.WriteInt64(0);
        w.WriteString("mod", ProtocolConstants.MaxModuleLength);
        w.WriteUInt64(0);
        w.WriteUInt32(0);
        w.WriteUInt8(0);
        w.WriteUInt8(1);
        w.WriteUInt8((byte)(CreatureLivenessPacket.MaxEntriesPerPacket + 1));

        Assert.Null(PacketCodec.ReadCreatureLiveness(buffer[..w.Position]));
    }

    [Fact]
    public void DialogueGrantWithTooManyItemsIsRejectedRatherThanAllocatingOnAPeersNumber()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Hand-write a header + an item count past MaxItems, so the reader must refuse it
        // rather than trust a malicious/corrupt peer's byte.
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.DialogueGrant);
        w.WriteUInt32(1);
        w.WriteUInt32(1);
        w.WriteString("tag", ProtocolConstants.MaxTagLength);
        w.WriteUInt8((byte)(DialogueGrantPacket.MaxItems + 1));

        Assert.Null(PacketCodec.ReadDialogueGrant(buffer[..w.Position]));
    }

    [Fact]
    public void FullItemSnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Every string at its cap AND every identity carried, which is the true worst case
        // now that an item is more than a blueprint name.
        List<GroundItem> items = [.. Enumerable.Range(1, ProtocolConstants.MaxGroundItems)
            .Select(i => new GroundItem(
                (uint)i,
                new string('r', ProtocolConstants.MaxResrefLength),
                new string('m', ProtocolConstants.MaxModuleLength),
                new Vector3f(1f, 2f, 3f),
                new ItemIdentity(ushort.MaxValue, 0xFF, 0xFF, uint.MaxValue, 0x0F)))];

        int length = PacketCodec.Write(buffer, new ItemSnapshotPacket(3, items));

        Assert.True(length > 0, "worst-case item snapshot must fit inside one packet");

        ItemSnapshotPacket? decoded = PacketCodec.ReadItemSnapshot(buffer[..length]);
        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxGroundItems, decoded.Items.Count);
        Assert.Equal(items[5], decoded.Items[5]);
    }

    [Fact]
    public void EnemySnapshotRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Presence (position, facing, animation, one-shot swings) rides the snapshot: the
        // mirroring instance drives its copies to these, so they must survive the wire
        // exactly.
        List<EnemyState> enemies =
        [
            new(0x14, 15, 15, false, new Vector3f(46.78f, 33.27f, -1.27f), 1.57f, 0x2714, 3, 0x271B),
            new(0x17, 0, 12, true),
            new(0x29, 7, 15, false, new Vector3f(-3.5f, 120.25f, 0f), -2.9f, 0x2710),
            // A cutscene actor: presence rides, and the plot flag must survive the wire so
            // the mirroring side's combat half knows to leave it alone.
            new(0x33, 8, 8, false, new Vector3f(12f, 34f, 0f), 0f, 0x2710, Plot: true),
            // An encounter spawn the mirror will have to MATERIALISE: the appearance row
            // and faction are the whole recipe, so they must survive the wire exactly.
            new(0x2001, 22, 22, false, new Vector3f(5f, 6f, 0f), 0.5f, 0x2712,
                Appearance: 187, Faction: 1),
        ];

        int length = PacketCodec.Write(buffer, new EnemySnapshotPacket(1234, "end_m01aa", enemies));
        EnemySnapshotPacket? decoded = PacketCodec.ReadEnemySnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(1234, decoded.ServerTimeMs);
        Assert.Equal("end_m01aa", decoded.Module);
        Assert.Equal(enemies, decoded.Enemies);
    }

    [Fact]
    public void PhantomDamageRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PhantomDamagePacket original = new(FromPeerId: 3, TargetPeerId: 7, CumulativeDamage: 142, AttackerId: 0x14);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadPhantomDamage(buffer[..length]));
    }

    [Fact]
    public void PeerEffectRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PeerEffectPacket original = new(
            FromPeerId: 3, TargetPeerId: 7, Kind: PeerEffectKind.Stun, DurationMs: 4500, Magnitude: 0, Sequence: 9);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadPeerEffect(buffer[..length]));
    }

    [Fact]
    public void PeerEffectRoundTripsWithMagnitude()
    {
        // Slow is the one allowlist member that actually carries a nonzero magnitude
        // (percent speed decrease) — worth its own case so a codec bug that only shows up
        // with a nonzero value cannot hide behind PeerEffectRoundTrips' zero.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        PeerEffectPacket original = new(
            FromPeerId: 1, TargetPeerId: 2, Kind: PeerEffectKind.Slow, DurationMs: 8000, Magnitude: 50, Sequence: 1);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadPeerEffect(buffer[..length]));
    }

    [Fact]
    public void BarkRoundTrips()
    {
        // The bark's CONTENTS travel, not the engine's pointers to them — so the codec has to
        // preserve the exact bytes, including a resref that is a fixed char[16] rather than a
        // string, and text in the game's own 8-bit encoding rather than UTF-8.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        byte[] text = System.Text.Encoding.Latin1.GetBytes("Beep beep boopé");
        byte[] resRef = new byte[BarkPacket.ResRefBytes];
        System.Text.Encoding.ASCII.GetBytes("n_droid_bark01").CopyTo(resRef, 0);

        BarkPacket original = new(
            PeerId: 3, "end_m01aa", Speaker: 0x25, Text: text, SoundResRef: resRef,
            Flags: 1, Serial: 7);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        BarkPacket? decoded = PacketCodec.ReadBark(buffer[..length]);
        Assert.NotNull(decoded);
        Assert.Equal(original.Speaker, decoded.Speaker);
        Assert.Equal(original.Flags, decoded.Flags);
        Assert.Equal(original.Serial, decoded.Serial);
        Assert.Equal(text, decoded.Text);
        Assert.Equal(resRef, decoded.SoundResRef);
    }

    [Fact]
    public void BarkTextIsNotMangledByUtf8Expansion()
    {
        // ⚠ The regression this guards: WriteString is UTF-8 with a single length byte, so a
        // full-length line of 8-bit engine text would expand past the prefix and be truncated
        // mid-sentence. The bark codec writes raw bytes for exactly this reason.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        byte[] text = new byte[BarkPacket.MaxTextBytes];
        Array.Fill(text, (byte)0xE9); // 'é' in the engine's encoding: two bytes in UTF-8

        BarkPacket original = new(
            PeerId: 1, "end_m01aa", Speaker: 9, Text: text,
            SoundResRef: new byte[BarkPacket.ResRefBytes], Flags: 0, Serial: 1);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        BarkPacket? decoded = PacketCodec.ReadBark(buffer[..length]);
        Assert.NotNull(decoded);
        Assert.Equal(BarkPacket.MaxTextBytes, decoded.Text.Length);
        Assert.Equal(text, decoded.Text);
    }

    [Fact]
    public void InteractionClaimRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        InteractionClaimPacket original =
            new(PeerId: 5, "end_m01aa", "end_trask", InteractionKind.Conversation, Active: true);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadInteractionClaim(buffer[..length]));
    }

    [Fact]
    public void InteractionClaimContainerRoundTripsWithTheTargetIdAndAnEmptyTag()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        InteractionClaimPacket original = new(
            PeerId: 5, "end_m01aa", Tag: string.Empty, InteractionKind.Container, Active: true, TargetId: 0x131);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);
        Assert.Equal(original, PacketCodec.ReadInteractionClaim(buffer[..length]));
    }

    [Fact]
    public void InteractionClaimRejectsAnUnknownKind()
    {
        // A lock whose release rules this build does not know is worse than no lock: it is
        // the OWNER that would hold the body and never learn when to let go.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer,
            new InteractionClaimPacket(5, "end_m01aa", "end_trask", InteractionKind.Conversation, true));

        buffer[length - 6] = 0x7F; // the kind byte: TargetId(4) + Active(1) trail it now

        Assert.Null(PacketCodec.ReadInteractionClaim(buffer[..length]));
    }

    [Fact]
    public void InteractionLockRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        InteractionLockPacket original = new(
            ServerTimeMs: 1234,
            "end_m01aa",
            [new InteractionLock(2, "end_trask", InteractionKind.Conversation)]);

        int length = PacketCodec.Write(buffer, original);
        InteractionLockPacket? decoded = PacketCodec.ReadInteractionLock(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.ServerTimeMs, decoded.ServerTimeMs);
        Assert.Equal(original.Module, decoded.Module);
        Assert.Equal(original.Locks, decoded.Locks);
    }

    [Fact]
    public void InteractionLockRoundTripsAContainerEntryWithItsTargetId()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        InteractionLockPacket original = new(
            ServerTimeMs: 1234,
            "end_m01aa",
            [new InteractionLock(2, Tag: string.Empty, InteractionKind.Container, TargetId: 0x131)]);

        int length = PacketCodec.Write(buffer, original);
        InteractionLockPacket? decoded = PacketCodec.ReadInteractionLock(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.Locks, decoded.Locks);
    }

    [Fact]
    public void InteractionLockTruncatesBeyondTheCap()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        InteractionLock[] tooMany =
        [
            .. Enumerable.Range(1, ProtocolConstants.MaxInteractionLocks + 3)
                .Select(i => new InteractionLock((uint)i, $"npc_{i}", InteractionKind.Conversation)),
        ];

        int length = PacketCodec.Write(buffer, new InteractionLockPacket(0, "end_m01aa", tooMany));
        InteractionLockPacket? decoded = PacketCodec.ReadInteractionLock(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxInteractionLocks, decoded.Locks.Count);
    }

    [Fact]
    public void EncounterTriggerRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        EncounterTriggerPacket original = new(PeerId: 5, "tar_m03aa", [0x11B, 0x11C]);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        EncounterTriggerPacket? decoded = PacketCodec.ReadEncounterTrigger(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(5u, decoded.PeerId);
        Assert.Equal("tar_m03aa", decoded.Module);
        Assert.Equal<uint[]>([0x11B, 0x11C], [.. decoded.EncounterIds]);
    }

    [Fact]
    public void EncounterTriggerTruncatesBeyondTheCap()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Overlapping activate areas are the only way to be in several at once and the
        // engine authors them disjoint; the cap bounds the packet rather than expressing a
        // real limit, so exceeding it must truncate, never fail to send.
        uint[] tooMany = [.. Enumerable.Range(1, ProtocolConstants.MaxEncounterTriggers + 3).Select(i => (uint)i)];

        int length = PacketCodec.Write(buffer, new EncounterTriggerPacket(1, "korr_m34aa", tooMany));
        EncounterTriggerPacket? decoded = PacketCodec.ReadEncounterTrigger(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxEncounterTriggers, decoded.EncounterIds.Count);
    }

    [Fact]
    public void TheTriggerRelayChannelIsWhereTheChangelogSaysItIsAnd47IsReserved()
    {
        // 47 is skipped on purpose (PacketType.cs's own comment) so this pass does not
        // collide with a concurrent worktree agent's block-version-45 liveness work; the
        // NoTwoPacketTypesShareANumber test in TransitionMovieTests.cs already covers
        // duplicates in general, this pins the one value this pass actually claims.
        Assert.Equal(48, (byte)PacketType.TriggerRelay);
    }

    [Fact]
    public void TriggerRelayClientReportRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        TriggerRelayPacket original = new(PeerId: 5, "end_m01aa", [0x50, 0x51], []);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(5u, decoded.PeerId);
        Assert.Equal("end_m01aa", decoded.Module);
        Assert.Equal<uint[]>([0x50, 0x51], [.. decoded.TriggerIds]);
        Assert.Empty(decoded.FiredIds);
    }

    [Fact]
    public void TriggerRelayHostBroadcastRoundTrips()
    {
        // PeerId 0 is the host's own broadcast sentinel — no real peer ever holds it (see
        // the packet's own doc comment) — carrying the fired-set instead of a report.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        TriggerRelayPacket original = new(PeerId: 0, "end_m01aa", [], [0x50, 0x51, 0x52]);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(0u, decoded.PeerId);
        Assert.Empty(decoded.TriggerIds);
        Assert.Equal<uint[]>([0x50, 0x51, 0x52], [.. decoded.FiredIds]);
    }

    [Fact]
    public void TriggerRelayTruncatesBothListsBeyondTheirCaps()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        uint[] tooManyTriggers =
            [.. Enumerable.Range(1, ProtocolConstants.MaxTriggerRelayEntries + 3).Select(i => (uint)i)];
        uint[] tooManyFired =
            [.. Enumerable.Range(1, ProtocolConstants.MaxTriggerFiredIds + 3).Select(i => (uint)(i + 100))];

        int length = PacketCodec.Write(
            buffer, new TriggerRelayPacket(1, "end_m01aa", tooManyTriggers, tooManyFired));
        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxTriggerRelayEntries, decoded.TriggerIds.Count);
        Assert.Equal(ProtocolConstants.MaxTriggerFiredIds, decoded.FiredIds.Count);
    }

    [Fact]
    public void TriggerRelayNativeFireReportRoundTrips()
    {
        // Protocol 36, peer->host direction: a peer's own engine natively fired these; origin
        // is implicit (the sender), so NativeFiredOriginPeerIds travels empty.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        TriggerRelayPacket original =
            new(PeerId: 5, "end_m01aa", [], [], NativeFiredIds: [0x26], NativeFiredOriginPeerIds: []);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(5u, decoded.PeerId);
        Assert.Equal<uint[]>([0x26], [.. decoded.NativeFiredIds]);
        Assert.Empty(decoded.NativeFiredOriginPeerIds);
    }

    [Fact]
    public void TriggerRelayNativeFireBroadcastRoundTripsWithOrigins()
    {
        // Protocol 36, host->all direction: the merged set, 1:1 matched against who fired it.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        TriggerRelayPacket original = new(
            PeerId: 0, "end_m01aa", [], [],
            NativeFiredIds: [0x26, 0x30], NativeFiredOriginPeerIds: [1, 5]);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal<uint[]>([0x26, 0x30], [.. decoded.NativeFiredIds]);
        Assert.Equal<uint[]>([1, 5], [.. decoded.NativeFiredOriginPeerIds]);
    }

    [Fact]
    public void TriggerRelayPreProtocol36CallersStillCompileAndRoundTrip()
    {
        // The 4-arg constructor kept for every call site that predates native fires.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        TriggerRelayPacket original = new(5, "end_m01aa", [0x50], []);

        int length = PacketCodec.Write(buffer, original);
        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Empty(decoded.NativeFiredIds);
        Assert.Empty(decoded.NativeFiredOriginPeerIds);
    }

    [Fact]
    public void TriggerRelayNativeFireListsAreCappedToo()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        uint[] tooMany =
            [.. Enumerable.Range(1, ProtocolConstants.MaxTriggerNativeFires + 3).Select(i => (uint)i)];
        uint[] tooManyOrigins =
            [.. Enumerable.Range(1, ProtocolConstants.MaxTriggerNativeFires + 3).Select(_ => 1u)];

        int length = PacketCodec.Write(
            buffer, new TriggerRelayPacket(0, "end_m01aa", [], [], tooMany, tooManyOrigins));
        TriggerRelayPacket? decoded = PacketCodec.ReadTriggerRelay(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxTriggerNativeFires, decoded.NativeFiredIds.Count);
        Assert.Equal(ProtocolConstants.MaxTriggerNativeFires, decoded.NativeFiredOriginPeerIds.Count);
    }

    [Fact]
    public void TheEnemyDamageDirectionCarriesNeitherHealthNorPresence()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // A peer's direction carries damage DEALT and nothing else. No health, no presence:
        // the owner's own engine is the authority on both, and a peer's copy of either is
        // whatever its mirrored, min-1-hp-pinned local body happens to show.
        int length = PacketCodec.Write(
            buffer, new EnemyDamagePacket(7, "end_m01aa", 1, [new EnemyDamageEntry(0x14, 6)]));
        EnemyDamagePacket? decoded = PacketCodec.ReadEnemyDamage(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(new EnemyDamageEntry(0x14, 6), decoded.Damage[0]);
    }

    [Fact]
    public void FullEnemySnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<EnemyState> enemies = [.. Enumerable.Range(1, ProtocolConstants.MaxEnemies)
            .Select(i => new EnemyState((uint)i, (ushort)i, 99, i % 2 == 0))];

        int length = PacketCodec.Write(buffer, new EnemySnapshotPacket(1, "end_m01aa", enemies));

        Assert.True(length > 0, "worst-case enemy snapshot must fit inside one packet");
        Assert.Equal(ProtocolConstants.MaxEnemies, PacketCodec.ReadEnemySnapshot(buffer[..length])!.Enemies.Count);
    }

    [Fact]
    public void HealthAndMaxHealthSurviveTheStateRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Regression guard: current health used to be read from +0xE0, which is the stored
        // MAX, so the wire carried a health that was never the player's actual vitality.
        PlayerState state = SampleState with { Health = 25, MaxHealth = 28 };

        int length = PacketCodec.Write(buffer, new StateUpdatePacket(1, 1, 1, state));
        StateUpdatePacket? decoded = PacketCodec.ReadStateUpdate(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(25, decoded.State.Health);
        Assert.Equal(28, decoded.State.MaxHealth);
    }

    [Fact]
    public void ForeignTrafficIsRejected()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];
        Assert.Equal(PacketType.None, PacketCodec.PeekType(garbage));
        Assert.Null(PacketCodec.ReadHello(garbage));
    }

    [Fact]
    public void WrongProtocolVersionIsRejected()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new HelloPacket("Revan", "end_m01aa", 1, ContentDigest.Unknown, SessionPassword.None));

        buffer[2] = ProtocolConstants.Version + 1;

        Assert.Equal(PacketType.None, PacketCodec.PeekType(buffer[..length]));

        // But the lenient peek still recognises it as a Hello, so the host can answer a
        // version-mismatched client with a typed Reject instead of silence.
        Assert.Equal(PacketType.Hello, PacketCodec.PeekTypeAnyVersion(buffer[..length], out byte version));
        Assert.Equal(ProtocolConstants.Version + 1, version);
    }

    [Fact]
    public void TruncatedPacketIsRejectedRatherThanMisread()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new StateUpdatePacket(1, 1, 1, SampleState));

        // A datagram cut short mid-field must fail, not decode into garbage coordinates.
        Assert.Null(PacketCodec.ReadStateUpdate(buffer[..(length - 3)]));
    }

    [Fact]
    public void OverlongStringsAreTruncatedNotCorrupted()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        string huge = new('x', 500);

        int length = PacketCodec.Write(buffer, new HelloPacket(huge, huge, 1, ContentDigest.Unknown, SessionPassword.None));
        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxNameLength, decoded.PlayerName.Length);
        Assert.Equal(ProtocolConstants.MaxModuleLength, decoded.Module.Length);
    }

    [Fact]
    public void PartyRosterRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        // Bastila (0), Carth (1) and Mission (5) recruited; Carth and Mission walking.
        PartyRosterPacket original = new(4321, new PartyRoster(0b0_0010_0011, 0b0_0010_0010));

        int length = PacketCodec.Write(buffer, original);
        PartyRosterPacket? decoded = PacketCodec.ReadPartyRoster(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.ServerTimeMs, decoded.ServerTimeMs);
        Assert.Equal(original.Roster, decoded.Roster);
        Assert.True(decoded.Roster.IsInParty(1));
        Assert.False(decoded.Roster.IsInParty(0));
        Assert.True(decoded.Roster.IsAvailable(0));
    }

    [Fact]
    public void EmptyPartyRosterRoundTrips()
    {
        // The Endar Spire before Trask joins is a real, legitimate all-zero roster, and it has
        // to survive the wire as one — a codec that treated "nothing recruited" as "nothing to
        // say" would leave a joiner with no level at all, and a client with no level never
        // reconciles.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        int length = PacketCodec.Write(buffer, new PartyRosterPacket(1, PartyRoster.Empty));
        PartyRosterPacket? decoded = PacketCodec.ReadPartyRoster(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(PartyRoster.Empty, decoded.Roster);
    }

    [Fact]
    public void PartyRosterRejectsSlotsTheEngineDoesNotHave()
    {
        // Nine slots is the engine's own bound (CSWPartyTable::AddMember refuses nNPC >= 9), so
        // a tenth bit means the sender is not the build it claims to be — and the nine bits we
        // would keep are then no more trustworthy than the one thrown away.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer, new PartyRosterPacket(1, new PartyRoster(0xFFFF, 0)));

        Assert.Null(PacketCodec.ReadPartyRoster(buffer[..length]));
    }

    [Fact]
    public void PartyRosterRejectsAPartyMemberWhoWasNeverRecruited()
    {
        // The engine cannot produce this and will not accept it: AddMember tests availability
        // before it touches anything. Dropping it stops a corrupt level from making a client
        // spend every pass attempting an add its own engine refuses.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer, new PartyRosterPacket(1, new PartyRoster(AvailableMask: 0b1, PartyMask: 0b10)));

        Assert.Null(PacketCodec.ReadPartyRoster(buffer[..length]));
    }

    [Fact]
    public void NonAsciiNamesSurvive()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HelloPacket original = new("Zaalbaräö", "end_m01aa", 1, ContentDigest.Unknown, 0x99);

        int length = PacketCodec.Write(buffer, original);
        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.PlayerName, decoded.PlayerName);
    }
}
