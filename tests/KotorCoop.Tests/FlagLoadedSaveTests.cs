using System.Net;
using System.Reflection;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class FlagLoadedSaveTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private delegate void Handler(IPEndPoint from, ReadOnlySpan<byte> data, long now);
    private static void Deliver(object session, string method, byte[] bytes, int length, IPEndPoint from) =>
        ((Handler)session.GetType().GetMethod(method, Private)!.CreateDelegate(typeof(Handler), session))
            (from, bytes.AsSpan(0, length), SessionClock.NowMs);
    private static readonly ulong Door = DoorFlags.MakeKey(DoorFlags.HashModule("end_m01aa"), 0x47);
    private static readonly ulong Chest = PlaceableFlags.MakeKey(PlaceableFlags.HashModule("end_m01ab"), 0xC6);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CheckpointRetiresBothVisitedModuleObjectsAndQueuedOldIntents(bool unannounced)
    {
        using CoopHost host = new(new SessionSettings { Port = 0 });
        WorldDigest before = new(1, 1, "end_m01aa", 1, 3, NativeWorldEpoch: 4);
        host.PublishWorldDigest(before);
        host.SetFlag(Door, 1); host.SetFlag(Chest, 1); host.SetFlag(42, 7);
        IPEndPoint from = new(IPAddress.Loopback, 9400);
        byte[] bytes = new byte[ProtocolConstants.MaxPacketSize];
        Deliver(host, "HandleHello", bytes, PacketCodec.Write(bytes, new HelloPacket("peer", "end_m01aa", 44, default, 0)), from);
        uint session = (uint)typeof(CoopHost).GetField("_sessionId", Private)!.GetValue(host)!;
        ReliableFlagSender pending = new();
        FlagOpPacket old = pending.Enqueue(2, Door, 1, 0, 99, session, 3, 0)!;
        if (unannounced) host.NotifyWorldLoadEdge(4);
        else host.PublishWorldDigest(before with { Module = string.Empty, NativeWorldEpoch = 5 });
        host.PublishWorldDigest(before with { LoadSerial = unannounced ? 4u : 3u, NativeWorldEpoch = 5 });
        Assert.Equal(1u, host.FlagLoadRevision);
        Assert.DoesNotContain(host.Flags.Snapshot(), f => ObjectKeyedFlags.IsObjectKeyed(f.Key));
        Assert.Contains(host.Flags.Snapshot(), f => f.Key == 42 && f.Value == 7);
        host.SetFlag(Door, 0); // settled native checkpoint baseline
        void Op(FlagOpPacket op) => Deliver(host, "HandleFlagOp", bytes, PacketCodec.Write(bytes, op), from);
        Op(Assert.Single(pending.CollectDue(1000))); // retransmission keeps original authority
        Assert.Contains(host.Flags.Snapshot(), f => f.Key == Door && f.Value == 0);
        Op(old with { OpSequence = 2, HostLoadSerial = unannounced ? 4u : 3u }); // cannot retag revision
        Assert.Contains(host.Flags.Snapshot(), f => f.Key == Door && f.Value == 0);
        Op(old with { OpSequence = 3, HostLoadSerial = unannounced ? 4u : 3u, WorldRevision = 1 });
        Assert.Contains(host.Flags.Snapshot(), f => f.Key == Door && f.Value == 1);
    }

    [Fact]
    public void OrdinaryTravelRetainsVisitedModuleStateAndAuthorityRevision()
    {
        using CoopHost host = new(new SessionSettings { Port = 0 });
        host.PublishWorldDigest(new(1, 1, "end_m01aa", 1, 3, NativeWorldEpoch: 4));
        host.SetFlag(Door, 1); host.SetFlag(Chest, 1);
        host.PublishWorldDigest(new(1, 1, "end_m01ab", 1, 3, NativeWorldEpoch: 5));
        Assert.Equal(0u, host.FlagLoadRevision);
        Assert.Equal(2, host.Flags.Snapshot().Count);
        host.PublishWorldDigest(new(1, 1, "end_m01aa", 1, 3, NativeWorldEpoch: 6));
        Assert.Equal(0u, host.FlagLoadRevision);
        Assert.Equal(2, host.Flags.Snapshot().Count);
    }

    [Fact]
    public void NewSnapshotClearsOldObjectsOnceAndLateOldPagesCannotReopenCheckpoint()
    {
        using CoopClient client = new(new SessionSettings(), new(IPAddress.Loopback, 27140));
        typeof(CoopClient).GetField("_hostSessionId", Private)!.SetValue(client, 10u);
        typeof(CoopClient).GetField("_hostWorld", Private)!.SetValue(client, new WorldDigest(1, 1, "end_m01aa", 1, 3));
        client.Flags.Set(Door, 1, 2); client.Flags.Set(Chest, 1, 2);
        byte[] bytes = new byte[ProtocolConstants.MaxPacketSize];
        void Snapshot(FlagSnapshotPacket snapshot) => Deliver(client, "HandleFlagSnapshot", bytes,
            PacketCodec.Write(bytes, snapshot), new(IPAddress.Loopback, 27140));
        Snapshot(new(8, [new(Door, 0, 8, 1)], 10, 3, 1));
        Assert.Single(client.Flags.Snapshot());
        Snapshot(new(8, [new(Chest, 0, 8, 1)], 10, 3, 1)); // later page must not clear first
        Assert.Equal(2, client.Flags.Snapshot().Count);
        Snapshot(new(99, [new(Door, 1, 99, 1)], 10, 3, 0));
        Snapshot(new(99, [new(Door, 1, 99, 1)], 11, 3, 1));
        Assert.Contains(client.Flags.Snapshot(), f => f.Key == Door && f.Value == 0);
    }

    [Fact]
    public void FlagNamespaceSurvivesCodecAndRetransmitAndFullPageFitsDatagram()
    {
        byte[] bytes = new byte[ProtocolConstants.MaxPacketSize];
        FlagOpPacket op = new(2, 3, Door, -1, 7, 10, 20, 30);
        int n = PacketCodec.Write(bytes, op);
        Assert.Equal(op, PacketCodec.ReadFlagOp(bytes.AsSpan(0, n)));
        var entries = Enumerable.Range(0, ProtocolConstants.MaxFlagEntriesPerPacket).Select(i => new FlagSnapshotEntry((ulong)i, 1, 1, 1)).ToArray();
        FlagSnapshotPacket snapshot = new(1, entries, 10, 20, 30);
        n = PacketCodec.Write(bytes, snapshot);
        Assert.True(n > 0);
        var parsed = PacketCodec.ReadFlagSnapshot(bytes.AsSpan(0, n))!;
        Assert.Equal(30u, parsed.WorldRevision);
        Assert.Equal(entries.Length, parsed.Flags.Count);
    }
}
