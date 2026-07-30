using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class PacketCodecTests
{
    private static readonly PlayerState SampleState =
        new(new Vector3f(12.5f, -3.25f, 0.75f), 1.75f, "end_m01aa", 87, 42);

    [Fact]
    public void HelloRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HelloPacket original = new("Revan", "end_m01aa", 12345);

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
        Assert.Equal(SampleState.Module, decoded.State.Module);
        Assert.Equal(SampleState.Health, decoded.State.Health);
        Assert.Equal(SampleState.Animation, decoded.State.Animation);
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

        WorldSnapshotPacket original = new(555L, 9, 4, entries);
        int length = PacketCodec.Write(buffer, original);

        WorldSnapshotPacket? decoded = PacketCodec.ReadWorldSnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(555L, decoded.ServerTimeMs);
        Assert.Equal(9u, decoded.FlagVersion);
        Assert.Equal(4u, decoded.ItemVersion);
        Assert.Equal(3, decoded.Entries.Count);
        Assert.Equal("Malak", decoded.Entries[1].Name);
        Assert.Equal("tar_m02aa", decoded.Entries[1].State.Module);
        Assert.Equal(12, decoded.Entries[1].State.Health);
        Assert.Equal(new Vector3f(1f, 2f, 3f), decoded.Entries[2].State.Position);
    }

    [Fact]
    public void FullPeerSnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<SnapshotEntry> entries = [.. Enumerable.Range(1, ProtocolConstants.MaxPeers)
            .Select(i => new SnapshotEntry(
                (uint)i,
                new string('n', ProtocolConstants.MaxStringLength),
                SampleState with { Module = new string('m', ProtocolConstants.MaxStringLength) }))];

        int length = PacketCodec.Write(buffer, new WorldSnapshotPacket(1, 1, 1, entries));

        // A snapshot that does not fit would be silently dropped every tick.
        Assert.True(length > 0, "worst-case snapshot must fit inside one packet");
        Assert.True(length <= ProtocolConstants.MaxPacketSize);
    }

    [Fact]
    public void FlagPacketsRoundTrip()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        FlagOpPacket op = new(4, 99, 1001, 1);
        int opLength = PacketCodec.Write(buffer, op);
        Assert.Equal(op, PacketCodec.ReadFlagOp(buffer[..opLength]));

        // Negative values must survive: KOTOR's global numbers are signed bytes.
        FlagOpPacket negative = new(4, 100, 1002, -7);
        int negativeLength = PacketCodec.Write(buffer, negative);
        Assert.Equal(negative, PacketCodec.ReadFlagOp(buffer[..negativeLength]));

        List<KeyValuePair<uint, sbyte>> flags = [new(1, 1), new(2, 0), new(9999, 42)];
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

    [Fact]
    public void FullItemSnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<GroundItem> items = [.. Enumerable.Range(1, ProtocolConstants.MaxGroundItems)
            .Select(i => new GroundItem(
                (uint)i,
                new string('r', ProtocolConstants.MaxResrefLength),
                new string('m', ProtocolConstants.MaxModuleLength),
                new Vector3f(1f, 2f, 3f)))];

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

        List<EnemyState> enemies =
        [
            new(0x14, 15, 15, false),
            new(0x17, 0, 12, true),
            new(0x29, 7, 15, false),
        ];

        int length = PacketCodec.Write(buffer, new EnemySnapshotPacket(1234, enemies));
        EnemySnapshotPacket? decoded = PacketCodec.ReadEnemySnapshot(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(1234, decoded.ServerTimeMs);
        Assert.Equal(enemies, decoded.Enemies);
    }

    [Fact]
    public void FullEnemySnapshotFitsInOneDatagram()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        List<EnemyState> enemies = [.. Enumerable.Range(1, ProtocolConstants.MaxEnemies)
            .Select(i => new EnemyState((uint)i, (ushort)i, 99, i % 2 == 0))];

        int length = PacketCodec.Write(buffer, new EnemySnapshotPacket(1, enemies));

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
        int length = PacketCodec.Write(buffer, new HelloPacket("Revan", "end_m01aa", 1));

        buffer[2] = ProtocolConstants.Version + 1;

        Assert.Equal(PacketType.None, PacketCodec.PeekType(buffer[..length]));
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

        int length = PacketCodec.Write(buffer, new HelloPacket(huge, huge, 1));
        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(ProtocolConstants.MaxNameLength, decoded.PlayerName.Length);
        Assert.Equal(ProtocolConstants.MaxModuleLength, decoded.Module.Length);
    }

    [Fact]
    public void NonAsciiNamesSurvive()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        HelloPacket original = new("Zaalbaräö", "end_m01aa", 1);

        int length = PacketCodec.Write(buffer, original);
        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(original.PlayerName, decoded.PlayerName);
    }
}
