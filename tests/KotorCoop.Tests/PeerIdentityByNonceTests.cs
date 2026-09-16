using System.Net;
using System.Net.Sockets;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What the host matches a returning player ON.
///
/// ⚠ AN EPHEMERAL PORT IS NOT AN IDENTITY. The Hello handler used to match a returning peer by
/// its nonce and, failing that, by its SOURCE ADDRESS. The nonce already covers everything the
/// fallback was written for — a Hello retransmitted until the Welcome lands, and a client that
/// dropped and came back, possibly from a new port, both carry it. The only case the address
/// fallback added was "same address, different nonce", and that is not a returning player: it
/// is a brand new client that the OS handed a port a departed one was using moments ago, which
/// on loopback and on a busy NAT is routine rather than exotic.
///
/// The cost of getting it wrong is not cosmetic. Peer ids key every cumulative total in the
/// session — the enemy-damage ledger, the incoming phantom-damage rows, the progression
/// ledger — so a stranger admitted under someone else's id inherits their running fight, and
/// the enemy takes the departed player's whole contribution a second time. That is the exact
/// double-count the nonce scheme exists to prevent, reached from the other direction.
///
/// Raw sockets rather than two <see cref="CoopClient"/> objects, because the whole point is
/// two different clients speaking from ONE source port, which two real clients cannot be made
/// to do.
/// </summary>
public class PeerIdentityByNonceTests
{
    private static async Task<uint> HelloAsync(Socket socket, IPEndPoint host, uint nonce, string name)
    {
        byte[] send = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            send,
            new HelloPacket(name, "end_m01aa", nonce, default, SessionPassword.Hash(string.Empty)));
        Assert.True(length > 0);

        byte[] receive = new byte[ProtocolConstants.MaxPacketSize];
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await socket.SendToAsync(send.AsMemory(0, length), SocketFlags.None, host);

            // The host also broadcasts world snapshots to anyone on its roster, so this
            // socket sees more than the reply it asked for — take the first Welcome and
            // ignore the rest.
            try
            {
                using CancellationTokenSource attempt = new(TimeSpan.FromMilliseconds(500));
                while (!attempt.IsCancellationRequested)
                {
                    SocketReceiveFromResult result = await socket.ReceiveFromAsync(
                        receive, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), attempt.Token);

                    ReadOnlySpan<byte> span = receive.AsSpan(0, result.ReceivedBytes);
                    if (PacketCodec.PeekType(span) == PacketType.Welcome
                        && PacketCodec.ReadWelcome(span) is { } welcome)
                    {
                        return welcome.AssignedPeerId;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // No Welcome this round; say Hello again.
            }
        }

        return 0;
    }

    [Fact]
    public async Task TwoNoncesFromOneSourcePortAreTwoDifferentPlayers()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint hostEnd = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        uint first = await HelloAsync(socket, hostEnd, nonce: 0xA1A1A1A1, name: "Malak");
        Assert.NotEqual(0u, first);

        // The same player again — the Hello-retransmit case — must come back as itself.
        Assert.Equal(first, await HelloAsync(socket, hostEnd, nonce: 0xA1A1A1A1, name: "Malak"));

        // A DIFFERENT player from the very same port must not.
        uint second = await HelloAsync(socket, hostEnd, nonce: 0xB2B2B2B2, name: "Carth");
        Assert.NotEqual(0u, second);
        Assert.NotEqual(first, second);

        // And the slot the first nonce held is gone rather than sitting on the roster as a
        // ghost until its peer timeout: its socket was demonstrably reissued, so it cannot
        // still be there.
        Assert.Equal(1, host.GetStats().PeerCount);

        // ...while its identity is still recoverable by the thing that actually identifies it.
        Assert.Equal(first, await HelloAsync(socket, hostEnd, nonce: 0xA1A1A1A1, name: "Malak"));

        await cts.CancelAsync();
        await hostTask;
    }
}
