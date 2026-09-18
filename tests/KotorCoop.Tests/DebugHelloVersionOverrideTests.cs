using System.Net;
using System.Net.Sockets;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Live-verifying E_PROTO on the two-instance rig needs one side to lie about its protocol
/// version — but <c>tools/demo.ps1</c> launches host and client from the SAME build, so there
/// was no way to do that until <see cref="SessionSettings.DebugHelloVersionOverride"/>
/// (coop.ini <c>debug_hello_version</c>) existed. These tests prove the three things the live
/// recipe in docs/research/disconnect-reconnect.md §12 depends on: the override touches only
/// the Hello, a real host answers a debug-overridden Hello with a typed <c>E_PROTO</c> Reject,
/// and the client that sent it stops retrying instead of hanging out
/// <see cref="ProtocolConstants.DefaultConnectTimeoutMs"/>.
/// </summary>
public class DebugHelloVersionOverrideTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    /// <summary>
    /// The override must change ONLY the Hello's own version byte — every packet the client
    /// sends after it (here, the first post-Welcome state update) still carries the real
    /// <see cref="ProtocolConstants.Version"/>. A raw socket stands in for the host so the
    /// bytes can be inspected directly rather than through <see cref="CoopHost"/>'s own strict
    /// reader, which would just refuse the mismatched Hello outright.
    /// </summary>
    [Fact]
    public async Task TheOverrideChangesOnlyTheHellosVersionByte()
    {
        using UdpClient fakeHost = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint fakeHostEndPoint = (IPEndPoint)fakeHost.Client.LocalEndPoint!;

        byte overrideVersion = (byte)(ProtocolConstants.Version - 1);
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "OldRevan", TickRate = 30, DebugHelloVersionOverride = overrideVersion,
            },
            fakeHostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task clientTask = client.RunAsync(cts.Token);

        using CancellationTokenSource helloTimeout = new(Timeout);
        UdpReceiveResult hello;
        try
        {
            hello = await fakeHost.ReceiveAsync(helloTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the client never sent its Hello");
            throw;
        }

        // Byte 2 is the version (magic[2] + version[1] + type[1] — PacketWriter.WriteHeader).
        Assert.Equal(overrideVersion, hello.Buffer[2]);
        Assert.Equal(PacketType.Hello, PacketCodec.PeekTypeAnyVersion(hello.Buffer, out byte helloVersion));
        Assert.Equal(overrideVersion, helloVersion);

        // Admit the client with a real Welcome, written in the REAL protocol version — a real
        // host would never write anything else (WriteHeader always stamps
        // ProtocolConstants.Version; CoopHost.HandleHello never touches the version byte).
        Span<byte> welcomeBuffer = new byte[ProtocolConstants.MaxPacketSize];
        int welcomeLength = PacketCodec.Write(
            welcomeBuffer,
            new WelcomePacket(
                AssignedPeerId: 7, SessionId: 1, TickRate: 30, MaxPeers: (byte)ProtocolConstants.MaxPeers,
                ContentDigest.Unknown));
        Assert.True(welcomeLength > 0);
        await fakeHost.SendAsync(welcomeBuffer[..welcomeLength].ToArray(), hello.RemoteEndPoint);

        // The client's very first action once connected is SendStateUpdate (CoopClient.Tick) —
        // that is the "later packet" this test needs to see the real version on.
        //
        // ⚠ HELLO RETRIES ARE SKIPPED, not asserted on (2026-09-18): the client re-sends its Hello
        // every HelloIntervalMs (500 ms) until the Welcome is PROCESSED, and under a loaded full
        // suite run (locally with the rig up, and on CI) the Welcome sent above can land after a
        // retry has already gone out — the fake host then received a Hello (override version)
        // as the "packet after Welcome" and this test failed with 51 != 52. A retry is still
        // the Hello, which the assertions above already cover; the claim here is about every
        // NON-Hello packet, so those are the ones waited for.
        using CancellationTokenSource stateTimeout = new(Timeout);
        UdpReceiveResult afterWelcome;
        while (true)
        {
            try
            {
                afterWelcome = await fakeHost.ReceiveAsync(stateTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("the client never sent anything but Hello retries after Welcome");
                throw;
            }

            if (PacketCodec.PeekTypeAnyVersion(afterWelcome.Buffer, out byte retryVersion) != PacketType.Hello)
            {
                break;
            }

            Assert.Equal(overrideVersion, retryVersion);
        }


        Assert.Equal(ProtocolConstants.Version, afterWelcome.Buffer[2]);
        Assert.NotEqual(PacketType.None, PacketCodec.PeekType(afterWelcome.Buffer));

        await cts.CancelAsync();
        await clientTask;
    }

    /// <summary>
    /// The end-to-end recipe with a REAL host and a REAL client — not a hand-crafted socket —
    /// exercised purely through the debug key, the way the live rig recipe
    /// (docs/research/disconnect-reconnect.md §12) actually runs it. The host's Reject is
    /// written in ITS real (and here, matching) <see cref="ProtocolConstants.Version"/>, so —
    /// unlike <c>ProtocolVersionMismatchTests</c>' genuinely-cross-build scenarios — the client
    /// reads it back through the ORDINARY strict Reject path (<c>HandleReject</c>), not the
    /// lenient <c>TryHandleVersionMismatchReject</c> fallback: <c>debug_hello_version</c> fakes
    /// only the Hello, so both processes here are still the one real build underneath. That
    /// distinction is exactly why this test exists alongside, not instead of,
    /// <c>ProtocolVersionMismatchTests</c>, which hand-crafts a genuinely foreign version to
    /// reach the lenient path.
    /// </summary>
    [Fact]
    public async Task AHostAnswersADebugOverriddenHelloWithATypedEProtoReject()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(hostCts.Token);

        byte overrideVersion = (byte)(ProtocolConstants.Version - 1);
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "OldRevan", TickRate = 30, DebugHelloVersionOverride = overrideVersion,
            },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource clientCts = new(Timeout + TimeSpan.FromSeconds(10));
        Task clientTask = client.RunAsync(clientCts.Token);

        bool rejected = await WaitForAsync(
            () => client.GetStats().Status.Contains("E_PROTO", StringComparison.Ordinal), Timeout);
        Assert.True(rejected, $"client never reported E_PROTO; status was: {client.GetStats().Status}");

        string status = client.GetStats().Status;
        Assert.StartsWith("rejected by host: E_PROTO", status);
        Assert.Contains($"v{ProtocolConstants.Version}", status);
        Assert.Contains($"v{overrideVersion}", status);
        Assert.False(client.IsConnected);

        await clientCts.CancelAsync();
        await clientTask;
        await hostCts.CancelAsync();
        await hostTask;
    }

    /// <summary>
    /// Once the Reject lands the client must stop re-Helloing rather than spin silently until
    /// <see cref="ProtocolConstants.DefaultConnectTimeoutMs"/> (15 s) — the exact symptom
    /// docs/limitations.md's E_PROTO row exists to rule out. <see cref="CoopClient.Tick"/>
    /// gates <c>SendHello</c> on <c>_rejectReason.Length == 0</c>, and reading the Reject sets
    /// that field the instant it lands — so no Hello should reach the host after the one that
    /// triggered the reject, well within the 500 ms retry interval that would otherwise produce
    /// several more.
    /// </summary>
    [Fact]
    public async Task TheClientStopsRetryingAfterAnEProtoReject()
    {
        using UdpClient fakeHost = new(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint fakeHostEndPoint = (IPEndPoint)fakeHost.Client.LocalEndPoint!;

        byte overrideVersion = (byte)(ProtocolConstants.Version - 1);
        using CoopClient client = new(
            new SessionSettings
            {
                PlayerName = "OldRevan", TickRate = 30, DebugHelloVersionOverride = overrideVersion,
            },
            fakeHostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task clientTask = client.RunAsync(cts.Token);

        using CancellationTokenSource firstHelloTimeout = new(Timeout);
        UdpReceiveResult firstHello;
        try
        {
            firstHello = await fakeHost.ReceiveAsync(firstHelloTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("the client never sent its first Hello");
            throw;
        }

        Assert.Equal(overrideVersion, firstHello.Buffer[2]);

        // Answer exactly the way CoopHost.HandleVersionMismatch does: a Reject written in the
        // (real, matching) current version.
        Span<byte> rejectBuffer = new byte[ProtocolConstants.MaxPacketSize];
        int rejectLength = PacketCodec.Write(
            rejectBuffer,
            new RejectPacket($"E_PROTO: host is v{ProtocolConstants.Version}, you are v{overrideVersion}"));
        Assert.True(rejectLength > 0);
        await fakeHost.SendAsync(rejectBuffer[..rejectLength].ToArray(), firstHello.RemoteEndPoint);

        Assert.True(
            await WaitForAsync(
                () => client.GetStats().Status.Contains("E_PROTO", StringComparison.Ordinal), Timeout),
            $"client never reported the reject; status was: {client.GetStats().Status}");

        // Hello retries fire every 500 ms; give it several intervals' worth of silence and
        // confirm nothing more arrives.
        using CancellationTokenSource retryWindow = new(TimeSpan.FromSeconds(2));
        try
        {
            UdpReceiveResult extra = await fakeHost.ReceiveAsync(retryWindow.Token);
            Assert.Fail($"the client kept re-Helloing after the reject: {extra.Buffer.Length} more bytes arrived");
        }
        catch (OperationCanceledException)
        {
            // Expected: silence.
        }

        await cts.CancelAsync();
        await clientTask;
    }
}
