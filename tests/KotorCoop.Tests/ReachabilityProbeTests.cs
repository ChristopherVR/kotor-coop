using System.Net;
using System.Net.Sockets;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ReachabilityProbe"/>'s datagram shape and <see cref="UdpChannel.SendReachabilityProbeAsync"/>'s
/// interception: a probe sent to the channel's own address comes back as "received" without
/// ever counting as a game packet, a probe with the wrong nonce does not complete the waiter,
/// and the marker can never be mistaken for a STUN message or a session packet.
/// </summary>
public sealed class ReachabilityProbeTests
{
    [Fact]
    public void BuildProducesAMarkedDatagramCarryingItsNonce()
    {
        byte[] probe = ReachabilityProbe.Build(out byte[] nonce);

        Assert.Equal(ReachabilityProbe.Length, probe.Length);
        Assert.Equal(ReachabilityProbe.NonceLength, nonce.Length);
        Assert.True(ReachabilityProbe.LooksLikeProbe(probe));
        Assert.True(ReachabilityProbe.Matches(probe, nonce));
    }

    [Fact]
    public void ADifferentNonceDoesNotMatch()
    {
        byte[] probe = ReachabilityProbe.Build(out _);
        ReachabilityProbe.Build(out byte[] otherNonce);

        Assert.True(ReachabilityProbe.LooksLikeProbe(probe));
        Assert.False(ReachabilityProbe.Matches(probe, otherNonce));
        Assert.False(ReachabilityProbe.Matches(probe, nonce: []));
    }

    [Fact]
    public void WrongLengthOrMarkerIsNotAProbe()
    {
        byte[] probe = ReachabilityProbe.Build(out _);

        Assert.False(ReachabilityProbe.LooksLikeProbe(probe.AsSpan(0, probe.Length - 1)));
        Assert.False(ReachabilityProbe.LooksLikeProbe([.. probe, 0]));

        byte[] wrongMarker = (byte[])probe.Clone();
        wrongMarker[0] ^= 0xFF;
        Assert.False(ReachabilityProbe.LooksLikeProbe(wrongMarker));
    }

    [Fact]
    public void TheMarkerIsNeitherAStunMessageNorASessionPacket()
    {
        byte[] probe = ReachabilityProbe.Build(out _);

        Assert.False(StunClient.LooksLikeStunMessage(probe));
        Assert.NotEqual(ProtocolConstants.Magic, BitConverter.ToUInt16(probe, 0));
        Assert.Equal(PacketType.None, PacketCodec.PeekType(probe));
    }

    [Fact]
    public async Task AProbeSentToTheChannelsOwnAddressIsReceivedAndNeverCountedAsAPacket()
    {
        using UdpChannel channel = new(0);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        // The receive loop has to be running for the interception to happen at all.
        Task<Datagram?> receive = channel.ReceiveAsync(cts.Token);

        IPEndPoint self = new(IPAddress.Loopback, channel.LocalEndPoint.Port);
        bool? result = await channel.SendReachabilityProbeAsync(self, timeoutMs: 5000, cts.Token);

        Assert.True(result);
        Assert.Equal(0, channel.PacketsReceived);
        Assert.Equal(0, channel.MalformedPackets);

        await cts.CancelAsync();
        await receive;
    }

    [Fact]
    public async Task AProbeNobodyAnsweredTimesOutAsFalse()
    {
        using UdpChannel channel = new(0);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task<Datagram?> receive = channel.ReceiveAsync(cts.Token);

        // A black hole: a bound socket nobody reads from, so the probe never comes back.
        using UdpClient sink = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint nowhere = (IPEndPoint)sink.Client.LocalEndPoint!;

        bool? result = await channel.SendReachabilityProbeAsync(nowhere, timeoutMs: 300, cts.Token);

        Assert.False(result);

        await cts.CancelAsync();
        await receive;
    }

    [Fact]
    public async Task AStrayProbeWithNoWaiterIsDroppedSilently()
    {
        using UdpChannel channel = new(0);
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        Task<Datagram?> receive = channel.ReceiveAsync(cts.Token);

        using UdpClient stranger = new();
        await stranger.SendAsync(
            ReachabilityProbe.Build(out _), new IPEndPoint(IPAddress.Loopback, channel.LocalEndPoint.Port));

        // Give the loop a moment; the datagram must not surface as a received packet.
        await Task.Delay(200);
        Assert.False(receive.IsCompleted);
        Assert.Equal(0, channel.PacketsReceived);

        await cts.CancelAsync();
        await receive;
    }
}
