using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Roadmap item 24 ("public address discovery"): pure protocol coverage for
/// <see cref="StunClient"/> (build/parse, no socket), plus integration coverage that runs a
/// fake STUN responder on loopback UDP and exercises <see cref="UdpChannel.SendStunRequestAsync"/>
/// end to end — the same socket that would carry ordinary game traffic.
/// </summary>
public class StunClientTests
{
    [Fact]
    public void BuildBindingRequestProducesA20ByteHeaderWithNoAttributes()
    {
        byte[] request = StunClient.BuildBindingRequest(out byte[] transactionId);

        Assert.Equal(StunClient.HeaderLength, request.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(0, 2))); // Binding Request
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2, 2)));       // no attributes
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4)));
        Assert.Equal(StunClient.TransactionIdLength, transactionId.Length);
        Assert.True(request.AsSpan(8, StunClient.TransactionIdLength).SequenceEqual(transactionId));
    }

    [Fact]
    public void TwoRequestsGetDifferentTransactionIds()
    {
        StunClient.BuildBindingRequest(out byte[] first);
        StunClient.BuildBindingRequest(out byte[] second);

        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void LooksLikeStunMessageChecksTheMagicCookiePosition()
    {
        byte[] request = StunClient.BuildBindingRequest(out _);
        Assert.True(StunClient.LooksLikeStunMessage(request));

        Assert.False(StunClient.LooksLikeStunMessage(new byte[19])); // too short
        Assert.False(StunClient.LooksLikeStunMessage(new byte[20])); // all zero, no cookie
    }

    [Fact]
    public void ParsesAHandBuiltXorMappedAddressResponse()
    {
        byte[] transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);

        byte[] response = BuildBindingSuccessWithXorMappedAddress(
            transactionId, IPAddress.Parse("203.0.113.42"), 27015);

        IPEndPoint? mapped = StunClient.TryParseBindingResponse(response, transactionId);

        Assert.NotNull(mapped);
        Assert.Equal(IPAddress.Parse("203.0.113.42"), mapped!.Address);
        Assert.Equal(27015, mapped.Port);
    }

    [Fact]
    public void ParsesTheOlderPlainMappedAddressFormAsAFallback()
    {
        byte[] transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);

        byte[] response = BuildBindingSuccessWithMappedAddress(
            transactionId, IPAddress.Parse("198.51.100.7"), 4000);

        IPEndPoint? mapped = StunClient.TryParseBindingResponse(response, transactionId);

        Assert.NotNull(mapped);
        Assert.Equal(IPAddress.Parse("198.51.100.7"), mapped!.Address);
        Assert.Equal(4000, mapped.Port);
    }

    [Fact]
    public void AMismatchedTransactionIdIsRejected()
    {
        byte[] transactionId = new byte[12];
        Random.Shared.NextBytes(transactionId);
        byte[] otherTransactionId = new byte[12];
        Random.Shared.NextBytes(otherTransactionId);

        byte[] response = BuildBindingSuccessWithXorMappedAddress(
            transactionId, IPAddress.Parse("203.0.113.42"), 27015);

        Assert.Null(StunClient.TryParseBindingResponse(response, otherTransactionId));
    }

    [Fact]
    public void GarbageIsRejectedWithoutThrowing()
    {
        byte[] transactionId = new byte[12];
        Assert.Null(StunClient.TryParseBindingResponse([1, 2, 3], transactionId));
        Assert.Null(StunClient.TryParseBindingResponse(new byte[20], transactionId)); // wrong type, zero cookie
    }

    /// <summary>
    /// End to end against a fake STUN server: a plain UDP socket on loopback that answers
    /// whatever Binding Request it receives with a Success Response carrying a known
    /// XOR-MAPPED-ADDRESS. Exercises the SAME path production uses —
    /// <see cref="UdpChannel.SendStunRequestAsync"/> — including the receive-loop interception
    /// that must hand the reply to the waiting probe instead of surfacing it as a malformed
    /// game packet.
    /// </summary>
    [Fact]
    public async Task SendStunRequestAsyncGetsTheFakeServersMappedAddressBackOnTheGameSocket()
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

            // Echo back the SAME transaction id the request carried, with a canned mapped address.
            byte[] transactionId = buffer.AsSpan(8, 12).ToArray();
            byte[] response = BuildBindingSuccessWithXorMappedAddress(
                transactionId, IPAddress.Parse("198.51.100.9"), 5000);
            await fakeServer.SendToAsync(response, result.RemoteEndPoint, cts.Token);
        });

        using UdpChannel channel = new(0); // ephemeral port, like a real game socket would use in a test

        // The interception that hands a STUN reply to SendStunRequestAsync lives INSIDE
        // ReceiveAsync's own loop (see UdpChannel's doc comment on it) - exactly like a real
        // CoopHost/CoopClient's continuous receive loop pumps it in production. A standalone
        // caller of SendStunRequestAsync has to run that same pump itself, or the reply just
        // sits unread in the socket's OS buffer forever.
        Task pump = RunReceivePumpAsync(channel, cts.Token);

        byte[] request = StunClient.BuildBindingRequest(out byte[] requestTransactionId);
        byte[]? reply = await channel.SendStunRequestAsync(
            serverEndpoint, request, requestTransactionId, timeoutMs: 3000, cts.Token);

        await cts.CancelAsync();
        await serverTask;
        await AwaitPumpAsync(pump);

        Assert.NotNull(reply);
        IPEndPoint? mapped = StunClient.TryParseBindingResponse(reply!, requestTransactionId);
        Assert.NotNull(mapped);
        Assert.Equal(IPAddress.Parse("198.51.100.9"), mapped!.Address);
        Assert.Equal(5000, mapped.Port);
    }

    [Fact]
    public async Task SendStunRequestAsyncTimesOutWhenNothingAnswers()
    {
        using UdpChannel channel = new(0);
        using Socket deadServer = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        deadServer.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var deadEndpoint = (IPEndPoint)deadServer.LocalEndPoint!;
        // Never read from deadServer's socket, so no reply is ever sent - a realistic
        // "server unreachable / firewalled" simulation, not just a bogus port.

        byte[] request = StunClient.BuildBindingRequest(out byte[] transactionId);
        byte[]? reply = await channel.SendStunRequestAsync(
            deadEndpoint, request, transactionId, timeoutMs: 300, CancellationToken.None);

        Assert.Null(reply);
    }

    /// <summary>
    /// A STUN reply must never surface through the ordinary game-datagram receive path, since
    /// it carries neither the co-op wire's magic nor a valid packet type and would otherwise
    /// count as a malformed/garbage packet.
    /// </summary>
    [Fact]
    public async Task AInterceptedStunReplyDoesNotCountAsAMalformedGamePacket()
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
            byte[] response = BuildBindingSuccessWithXorMappedAddress(
                transactionId, IPAddress.Parse("198.51.100.9"), 5000);
            await fakeServer.SendToAsync(response, result.RemoteEndPoint, cts.Token);
        });

        using UdpChannel channel = new(0);
        Task pump = RunReceivePumpAsync(channel, cts.Token);

        byte[] request = StunClient.BuildBindingRequest(out byte[] transactionIdOut);
        byte[]? reply = await channel.SendStunRequestAsync(serverEndpoint, request, transactionIdOut, 3000, cts.Token);

        await cts.CancelAsync();
        await serverTask;
        await AwaitPumpAsync(pump);

        Assert.NotNull(reply);

        Assert.Equal(0, channel.MalformedPackets);
    }

    /// <summary>
    /// Simulates a real host/client's continuous receive loop, which is what actually drives
    /// the STUN-reply interception inside <see cref="UdpChannel.ReceiveAsync"/> — a standalone
    /// caller of <see cref="UdpChannel.SendStunRequestAsync"/> (as these tests are) has to pump
    /// it itself, exactly as production code always does via <c>CoopHost</c>/<c>CoopClient</c>'s
    /// own receive loop.
    /// </summary>
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

    private static byte[] BuildBindingSuccessWithXorMappedAddress(byte[] transactionId, IPAddress address, ushort port)
    {
        const uint magicCookie = 0x2112A442;

        byte[] attrValue = new byte[8];
        attrValue[1] = 0x01; // IPv4
        ushort xorPort = (ushort)(port ^ (magicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(attrValue.AsSpan(2, 2), xorPort);

        uint addressValue = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint xorAddress = addressValue ^ magicCookie;
        BinaryPrimitives.WriteUInt32BigEndian(attrValue.AsSpan(4, 4), xorAddress);

        return BuildBindingSuccess(transactionId, attrType: 0x0020, attrValue);
    }

    private static byte[] BuildBindingSuccessWithMappedAddress(byte[] transactionId, IPAddress address, ushort port)
    {
        byte[] attrValue = new byte[8];
        attrValue[1] = 0x01; // IPv4
        BinaryPrimitives.WriteUInt16BigEndian(attrValue.AsSpan(2, 2), port);
        address.GetAddressBytes().CopyTo(attrValue.AsSpan(4, 4));

        return BuildBindingSuccess(transactionId, attrType: 0x0001, attrValue);
    }

    private static byte[] BuildBindingSuccess(byte[] transactionId, ushort attrType, byte[] attrValue)
    {
        const uint magicCookie = 0x2112A442;

        byte[] message = new byte[20 + 4 + attrValue.Length];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(0, 2), 0x0101); // Binding Success Response
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2, 2), (ushort)(4 + attrValue.Length));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4, 4), magicCookie);
        transactionId.CopyTo(message.AsSpan(8, 12));

        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20, 2), attrType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22, 2), (ushort)attrValue.Length);
        attrValue.CopyTo(message.AsSpan(24));

        return message;
    }
}
