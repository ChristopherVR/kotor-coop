using System.Net;
using System.Reflection;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Host spawns only: the host's script-spawned creature blueprints reach a joiner.</summary>
public sealed class SpawnTemplatesTests
{
    private static SpawnTemplatesPacket Packet(uint session = 123, string module = "tar_m02aa") =>
        new(session, module, 4, [new(0x12D, "n_commm"), new(0x140, "tar02_sithsold21")]);

    [Fact]
    public void ThePacketRoundTrips()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, Packet());
        SpawnTemplatesPacket? read = PacketCodec.ReadSpawnTemplates(buffer.AsSpan(0, length));

        Assert.NotNull(read);
        Assert.Equal(123u, read.HostSessionId);
        Assert.Equal("tar_m02aa", read.Module);
        Assert.Equal(4u, read.NativeEpoch);
        Assert.Equal(Packet().Entries, read.Entries);
    }

    [Fact]
    public void AFullListFitsOneDatagram()
    {
        List<SpawnTemplateEntry> entries = [.. Enumerable.Range(0, SpawnTemplatesPacket.MaxEntries)
            .Select(i => new SpawnTemplateEntry((uint)(0x100 + i), "abcdefghijklmnop"))];
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];

        Assert.True(PacketCodec.Write(buffer, new SpawnTemplatesPacket(1, "tar_m02aa", 1, entries)) > 0);
    }

    private static CoopClient Client()
    {
        CoopClient client = new(new SessionSettings(), new IPEndPoint(IPAddress.Loopback, 27140));
        typeof(CoopClient).GetField("_hostSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, 123u);
        return client;
    }

    [Fact]
    public void AJoinerKeepsTheAdmittedHostsListForThatModuleOnly()
    {
        using CoopClient client = Client();
        client.ApplySpawnTemplates(Packet());

        Assert.Equal(2, client.GetSpawnTemplates("TAR_M02AA").Count);
        Assert.Empty(client.GetSpawnTemplates("tar_m02ab"));
    }

    [Fact]
    public void AnotherHostSessionsListIsIgnored()
    {
        using CoopClient client = Client();
        client.ApplySpawnTemplates(Packet(session: 99));

        Assert.Empty(client.GetSpawnTemplates("tar_m02aa"));
    }
}
