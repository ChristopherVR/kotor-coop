using System.Net;
using System.Reflection;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LivenessAdmissionTests
{
    private delegate void SpanHandler(IPEndPoint from, ReadOnlySpan<byte> payload, long now);
    private static void DeliverWelcome(CoopClient client)
    {
        byte[] data = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(data, new WelcomePacket(2, 123, 30, 8, ContentDigest.Unknown));
        var handler = (SpanHandler)typeof(CoopClient).GetMethod("HandleWelcome", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(SpanHandler), client);
        handler(new IPEndPoint(IPAddress.Loopback, 27140), data.AsSpan(0, length), 1);
    }
    private static CoopClient Client()
    {
        CoopClient client = new(new SessionSettings(), new IPEndPoint(IPAddress.Loopback, 27140));
        Set(client, "_hostSessionId", 123u);
        Set(client, "_hostWorld", new WorldDigest(1, 1, "end_m01aa", 1, 7));
        return client;
    }
    private static void Set(CoopClient client, string name, object value) =>
        typeof(CoopClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, value);
    private static CreatureLivenessPacket Page(uint session = 123, uint load = 7, uint generation = 10,
        string module = "end_m01aa", uint id = 300) =>
        new(1, module, 0, generation, 0, 1, [new(id, CreatureLivenessState.Gone)])
            { HostSessionId = session, HostLoadSerial = load };

    [Fact]
    public void OldHostSessionOldLoadAndReorderedCyclesCannotReplaceCurrentSnapshot()
    {
        using var client = Client();
        client.ApplyCreatureLiveness(Page());
        Assert.Equal(300u, Assert.Single(client.GetCreatureLiveness("end_m01aa")).ObjectId);
        client.ApplyCreatureLiveness(Page(session: 122, generation: 11, id: 301));
        client.ApplyCreatureLiveness(Page(load: 6, generation: 11, id: 302));
        client.ApplyCreatureLiveness(Page(generation: 9, id: 303));
        Assert.Equal(300u, Assert.Single(client.GetCreatureLiveness("end_m01aa")).ObjectId);
        Set(client, "_hostWorld", new WorldDigest(1, 1, "end_m01aa", 1, 8));
        Assert.Empty(client.GetCreatureLiveness("end_m01aa"));
        client.ApplyCreatureLiveness(Page(load: 7, generation: 12));
        Assert.Empty(client.GetCreatureLiveness("end_m01aa"));
        client.ApplyCreatureLiveness(Page(load: 8, generation: 12, id: 304));
        Assert.Equal(304u, Assert.Single(client.GetCreatureLiveness("end_m01aa")).ObjectId);
    }

    [Fact]
    public void AViewIsCompleteOnlyOnceEveryPageOfItsCycleArrived()
    {
        // LocalOnlyHostileTracker judges only a complete view: a partial one can lack ids the host has.
        using var client = Client();
        CreatureLivenessPacket PageOf(byte index, uint id, uint generation = 10) =>
            new(1, "end_m01aa", 0, generation, index, 2, [new(id, CreatureLivenessState.Alive)])
                { HostSessionId = 123, HostLoadSerial = 7 };

        client.ApplyCreatureLiveness(PageOf(0, 300));
        Assert.Null(client.GetCreatureLivenessView("end_m01aa").CompleteAtMs);

        client.ApplyCreatureLiveness(PageOf(1, 301));
        Assert.NotNull(client.GetCreatureLivenessView("end_m01aa").CompleteAtMs);
        Assert.Equal(2, client.GetCreatureLivenessView("end_m01aa").Entries.Count);

        client.ApplyCreatureLiveness(PageOf(0, 300, generation: 11));
        Assert.Null(client.GetCreatureLivenessView("end_m01aa").CompleteAtMs);
    }

    [Fact]
    public void LocalLoadQuarantineIsStickyAcrossDuplicateWelcomeIdentityAndPackets()
    {
        using var client = Client();
        client.ApplyCreatureLiveness(Page());
        client.InvalidateCreatureLivenessForLocalLoad();
        DeliverWelcome(client);
        DeliverWelcome(client); // actual duplicate Welcome cannot release it
        client.ApplyCreatureLiveness(Page(generation: 11));
        Assert.Empty(client.GetCreatureLiveness("end_m01aa"));
        using var newlyAdmitted = Client();
        newlyAdmitted.ApplyCreatureLiveness(Page(generation: 11));
        Assert.Single(newlyAdmitted.GetCreatureLiveness("end_m01aa"));
    }

    [Fact]
    public void ModuleChangeCannotRollBackHighWaterAndSerialWrapIsOrdered()
    {
        using var client = Client();
        client.ApplyCreatureLiveness(Page(generation: uint.MaxValue));
        Set(client, "_hostWorld", new WorldDigest(1, 1, "end_m01ab", 1, 7));
        client.ApplyCreatureLiveness(Page(generation: uint.MaxValue - 1, module: "end_m01ab"));
        Assert.Empty(client.GetCreatureLiveness("end_m01ab"));
        client.ApplyCreatureLiveness(Page(generation: 1, module: "end_m01ab"));
        Assert.Single(client.GetCreatureLiveness("end_m01ab"));
    }

    [Fact]
    public void OrdinaryTransitionWaitsForNewCycleAndThenResumesWithoutNewClient()
    {
        using var client = Client();
        client.ApplyCreatureLiveness(Page());
        client.InvalidateCreatureLivenessForWorldTransition();
        client.ApplyCreatureLiveness(Page());
        Assert.Empty(client.GetCreatureLiveness("end_m01aa"));
        Set(client, "_hostWorld", new WorldDigest(1, 1, "end_m01ab", 1, 7, NativeWorldEpoch: 2));
        client.ApplyCreatureLiveness(Page(generation: 11, module: "end_m01ab") with { HostNativeEpoch = 2 });
        Assert.Single(client.GetCreatureLiveness("end_m01ab"));
    }

    [Fact]
    public void AcknowledgedReloadWithUnchangedLoadSerialRejectsOldNativeEpochPackets()
    {
        using var client = Client();
        client.ApplyCreatureLiveness(Page());
        Set(client, "_hostWorld", new WorldDigest(1, 1, "end_m01aa", 1, 7, NativeWorldEpoch: 2));
        client.ApplyCreatureLiveness(Page(generation: 11));
        Assert.Empty(client.GetCreatureLiveness("end_m01aa"));
        client.ApplyCreatureLiveness(Page(generation: 12) with { HostNativeEpoch = 2 });
        Assert.Single(client.GetCreatureLiveness("end_m01aa"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    public void InvalidPageBoundsAndOldWireVersionAreRefused(byte index, byte count)
    {
        byte[] bytes = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(bytes, Page() with { PageIndex = index, PageCount = count });
        Assert.Null(PacketCodec.ReadCreatureLiveness(bytes.AsSpan(0, length)));
        length = PacketCodec.Write(bytes, Page());
        bytes[2] = 78;
        Assert.Null(PacketCodec.ReadCreatureLiveness(bytes.AsSpan(0, length)));
    }
}
