using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Roadmap item 24: <see cref="PublicAddressDiscovery"/>'s server-list parsing and its
/// end-to-end query against a fake STUN responder on loopback, run through the SAME
/// <see cref="UdpChannel"/> a real host's session socket would use.</summary>
public class PublicAddressDiscoveryTests
{
    [Fact]
    public void ParseServersSplitsAndTrims()
    {
        IReadOnlyList<string> servers = PublicAddressDiscovery.ParseServers(" a:1 , b:2 ,c:3");
        Assert.Equal<string[]>(["a:1", "b:2", "c:3"], [.. servers]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOrNullInputParsesToAnEmptyList(string? value)
    {
        Assert.Empty(PublicAddressDiscovery.ParseServers(value));
    }

    [Fact]
    public void DefaultServersIsTwoEntries()
    {
        // The task deliberately ships a redundant pair, not one server - so one being briefly
        // unreachable does not read as "STUN doesn't work".
        Assert.Equal(2, PublicAddressDiscovery.ParseServers(PublicAddressDiscovery.DefaultServers).Count);
    }

    [Fact]
    public async Task QueryOnceAsyncFindsThePublicEndpointThroughAFakeStunServer()
    {
        using Socket fakeServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        fakeServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)fakeServer.LocalEndPoint!;

        using CancellationTokenSource cts = new();
        Task serverTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[512];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult result = await fakeServer.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token);
            byte[] transactionId = buffer.AsSpan(8, 12).ToArray();
            byte[] response = BuildBindingSuccessWithXorMappedAddress(transactionId, IPAddress.Parse("203.0.113.5"), 27015);
            await fakeServer.SendToAsync(response, result.RemoteEndPoint, cts.Token);
        });

        using UdpChannel channel = new(0);

        // See StunClientTests' own note on this: the STUN-reply interception lives inside
        // UdpChannel.ReceiveAsync's loop, which a real CoopHost pumps continuously - a
        // standalone test has to run that same pump itself.
        Task pump = RunReceivePumpAsync(channel, cts.Token);

        PublicAddressDiscovery discovery = new(channel, [$"127.0.0.1:{serverEndpoint.Port}"]);

        await discovery.QueryOnceAsync(cts.Token);
        await cts.CancelAsync();
        await serverTask;
        await AwaitPumpAsync(pump);

        Assert.NotNull(discovery.PublicEndPoint);
        Assert.Equal(IPAddress.Parse("203.0.113.5"), discovery.PublicEndPoint!.Address);
        Assert.Equal(27015, discovery.PublicEndPoint.Port);

        discovery.Dispose();
    }

    [Fact]
    public async Task QueryOnceAsyncMovesOnToTheNextServerWhenTheFirstNeverAnswers()
    {
        using Socket deadServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deadServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int deadPort = ((IPEndPoint)deadServer.LocalEndPoint!).Port;

        using Socket liveServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        liveServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var liveEndpoint = (IPEndPoint)liveServer.LocalEndPoint!;

        using CancellationTokenSource cts = new();
        Task serverTask = Task.Run(async () =>
        {
            byte[] buffer = new byte[512];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult result = await liveServer.ReceiveFromAsync(buffer, SocketFlags.None, any, cts.Token);
            byte[] transactionId = buffer.AsSpan(8, 12).ToArray();
            byte[] response = BuildBindingSuccessWithXorMappedAddress(transactionId, IPAddress.Parse("198.51.100.1"), 5000);
            await liveServer.SendToAsync(response, result.RemoteEndPoint, cts.Token);
        });

        using UdpChannel channel = new(0);
        Task pump = RunReceivePumpAsync(channel, cts.Token);

        PublicAddressDiscovery discovery = new(
            channel, [$"127.0.0.1:{deadPort}", $"127.0.0.1:{liveEndpoint.Port}"]);

        await discovery.QueryOnceAsync(cts.Token);
        await cts.CancelAsync();
        await serverTask;
        await AwaitPumpAsync(pump);

        Assert.NotNull(discovery.PublicEndPoint);
        Assert.Equal(IPAddress.Parse("198.51.100.1"), discovery.PublicEndPoint!.Address);

        discovery.Dispose();
    }

    private static Task RunReceivePumpAsync(UdpChannel channel, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await channel.ReceiveAsync(cancellationToken);
            }
        }, CancellationToken.None);

    private static async Task AwaitPumpAsync(Task pump)
    {
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public void StartIsANoOpWithNoConfiguredServers()
    {
        using UdpChannel channel = new(0);
        PublicAddressDiscovery discovery = new(channel, []);

        discovery.Start(); // must not throw and must never produce an endpoint
        Assert.Null(discovery.PublicEndPoint);

        discovery.Dispose();
    }

    private static byte[] BuildBindingSuccessWithXorMappedAddress(byte[] transactionId, IPAddress address, ushort port)
    {
        const uint magicCookie = 0x2112A442;

        byte[] attrValue = new byte[8];
        attrValue[1] = 0x01;
        ushort xorPort = (ushort)(port ^ (magicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(attrValue.AsSpan(2, 2), xorPort);

        uint addressValue = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint xorAddress = addressValue ^ magicCookie;
        BinaryPrimitives.WriteUInt32BigEndian(attrValue.AsSpan(4, 4), xorAddress);

        byte[] message = new byte[20 + 4 + attrValue.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), 0x0101);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), (ushort)(4 + attrValue.Length));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4, 4), magicCookie);
        transactionId.CopyTo(message.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20, 2), 0x0020);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22, 2), (ushort)attrValue.Length);
        attrValue.CopyTo(message.AsSpan(24));

        return message;
    }
}
