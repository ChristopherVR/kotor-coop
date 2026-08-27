using System.Net;
using System.Net.Sockets;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A joining player running a different co-op build used to get nothing back at all: the
/// strict header check (<see cref="PacketCodec.PeekType"/>) folds a version-mismatched Hello
/// into <see cref="PacketType.None"/> indistinguishably from garbage, so
/// <see cref="CoopHost.HandleVersionMismatch"/>'s typed <see cref="RejectPacket"/> is the fix
/// — and a client running the mismatched build must be able to read it back
/// (<see cref="CoopClient.TryHandleVersionMismatchReject"/>), even though it is written in
/// the HOST's current wire format, not the client's.
/// </summary>
public class ProtocolVersionMismatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? each = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            each?.Invoke();

            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    /// <summary>
    /// A real host, a hand-crafted Hello with a version byte that is not ours, sent over a
    /// raw socket standing in for a client on a different build. Before this pass the host
    /// answered with nothing and the sender was left to its own 15 s "no reply from host"
    /// timeout; now it gets a typed Reject that names both versions.
    /// </summary>
    [Fact]
    public async Task AVersionMismatchedHelloGetsATypedRejectFromTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);

        using UdpClient oldClient = new();
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        Span<byte> buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer,
            new HelloPacket("OldRevan", "end_m01aa", ClientNonce: 42, ContentDigest.Unknown, SessionPassword.None));
        Assert.True(length > 0);

        // The version byte (magic, then this) — see PacketWriter.WriteHeader / the
        // PacketCodecTests fixture this mirrors.
        byte foreignVersion = (byte)(ProtocolConstants.Version - 1);
        buffer[2] = foreignVersion;

        await oldClient.SendAsync(buffer[..length].ToArray(), hostEndPoint);

        using CancellationTokenSource receiveTimeout = new(Timeout);
        UdpReceiveResult reply;
        try
        {
            reply = await oldClient.ReceiveAsync(receiveTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the host never answered a version-mismatched Hello");
            throw;
        }

        // The Reject is written in the HOST's (current) version, which this test process
        // also is, so the strict reader parses it normally.
        Assert.Equal(PacketType.Reject, PacketCodec.PeekType(reply.Buffer));
        RejectPacket? reject = PacketCodec.ReadReject(reply.Buffer);
        Assert.NotNull(reject);
        Assert.Contains("E_PROTO", reject!.Reason);
        Assert.Contains($"v{ProtocolConstants.Version}", reject.Reason);
        Assert.Contains($"v{foreignVersion}", reject.Reason);

        await cts.CancelAsync();
        await hostTask;
    }

    /// <summary>
    /// The reverse leg: a real client dials a raw socket standing in for a host on a
    /// different build. That "host" answers with a Reject written in ITS version, which the
    /// client's strict reader cannot parse — the client must recover the mismatch through
    /// the lenient fallback and surface it exactly where an ordinary Reject would.
    /// </summary>
    [Fact]
    public async Task AClientReceivingAForeignVersionRejectReportsTheMismatch()
    {
        using UdpClient fakeHost = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint fakeHostEndPoint = (IPEndPoint)fakeHost.Client.LocalEndPoint!;

        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 }, fakeHostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task clientTask = client.RunAsync(cts.Token);

        // Wait for the client's own Hello so we know its source port (an ephemeral port it
        // picked itself) to answer.
        using CancellationTokenSource receiveTimeout = new(Timeout);
        UdpReceiveResult hello;
        try
        {
            hello = await fakeHost.ReceiveAsync(receiveTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the client never sent a Hello to the fake host");
            throw;
        }

        Assert.Equal(PacketType.Hello, PacketCodec.PeekType(hello.Buffer));

        byte foreignVersion = (byte)(ProtocolConstants.Version + 1);
        Span<byte> buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new RejectPacket("should never be parsed as text"));
        Assert.True(length > 0);
        buffer[2] = foreignVersion;

        await fakeHost.SendAsync(buffer[..length].ToArray(), hello.RemoteEndPoint);

        string expected = $"protocol mismatch: host is v{foreignVersion}, you are v{ProtocolConstants.Version}";
        Assert.True(
            await WaitForAsync(() => client.GetStats().Status.Contains(expected), Timeout),
            $"the client never reported the version mismatch; status was: {client.GetStats().Status}");

        Assert.False(client.IsConnected);

        await cts.CancelAsync();
        await clientTask;
    }
}
