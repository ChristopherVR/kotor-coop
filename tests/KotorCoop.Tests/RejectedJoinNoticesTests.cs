using System.Net;
using System.Net.Sockets;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="RejectedJoinNotices"/> — the host's side of a refused join. Pins the per-address
/// rate limit (one line per address per interval, measured from the last line shown), the
/// wording for an older and a newer build, that distinct addresses are independent, that a
/// drain empties the queue, and — over a real socket — that <see cref="CoopHost.HandleVersionMismatch"/>
/// actually queues one when a mismatched Hello arrives.
/// </summary>
public sealed class RejectedJoinNoticesTests
{
    private static readonly IPEndPoint Alice = new(IPAddress.Parse("192.168.1.20"), 51000);
    private static readonly IPEndPoint AliceAgain = new(IPAddress.Parse("192.168.1.20"), 51001);
    private static readonly IPEndPoint Bob = new(IPAddress.Parse("192.168.1.21"), 51000);

    [Fact]
    public void RateLimitIsPerAddressAndMeasuredFromTheLastLineShown()
    {
        Dictionary<string, long> last = [];

        Assert.True(RejectedJoinNotices.ShouldAnnounce(last, "a", nowMs: 1000, intervalMs: 500));
        Assert.False(RejectedJoinNotices.ShouldAnnounce(last, "a", nowMs: 1400, intervalMs: 500));
        // The refused call above must not have moved the window: 1500 is 500 past the LINE.
        Assert.True(RejectedJoinNotices.ShouldAnnounce(last, "a", nowMs: 1500, intervalMs: 500));
        Assert.True(RejectedJoinNotices.ShouldAnnounce(last, "b", nowMs: 1500, intervalMs: 500));
    }

    [Fact]
    public void ARetryingClientIsAnnouncedOncePerIntervalWhateverPortItUses()
    {
        RejectedJoinNotices notices = new();

        notices.NoteVersionMismatch(Alice, peerVersion: 50, ourVersion: 52, nowMs: 1000);
        notices.NoteVersionMismatch(Alice, peerVersion: 50, ourVersion: 52, nowMs: 2000);
        notices.NoteVersionMismatch(AliceAgain, peerVersion: 50, ourVersion: 52, nowMs: 3000);

        IReadOnlyList<string> lines = notices.Drain();
        string line = Assert.Single(lines);
        Assert.Contains("192.168.1.20", line);
        Assert.Contains("an older build", line);
        Assert.Contains("v50 vs your v52", line);

        Assert.Empty(notices.Drain());

        notices.NoteVersionMismatch(Alice, 50, 52, nowMs: 1000 + RejectedJoinNotices.RepeatIntervalMs);
        Assert.Single(notices.Drain());
    }

    [Fact]
    public void ANewerBuildIsCalledNewer()
    {
        RejectedJoinNotices notices = new();
        notices.NoteVersionMismatch(Bob, peerVersion: 60, ourVersion: 52, nowMs: 0);

        Assert.Contains("a newer build", Assert.Single(notices.Drain()));
    }

    [Fact]
    public void DistinctAddressesAreIndependentAndAWrongPasswordHasItsOwnLine()
    {
        RejectedJoinNotices notices = new();

        notices.NoteVersionMismatch(Alice, 50, 52, nowMs: 0);
        notices.NoteWrongPassword(Bob, nowMs: 0);
        notices.NoteWrongPassword(Bob, nowMs: 1);

        IReadOnlyList<string> lines = notices.Drain();
        Assert.Equal(2, lines.Count);
        Assert.Contains("wrong password", lines[1]);
        Assert.Contains("192.168.1.21", lines[1]);
    }

    [Fact]
    public void AFloodOfAddressesStaysBounded()
    {
        RejectedJoinNotices notices = new();
        for (int i = 0; i < 500; i++)
        {
            notices.NoteWrongPassword(new IPEndPoint(IPAddress.Parse($"10.0.{i / 250}.{i % 250 + 1}"), 5), nowMs: i);
        }

        Assert.Equal(RejectedJoinNotices.MaxPendingLines, notices.Drain().Count);
    }

    /// <summary>The wiring: a version-mismatched Hello over a real socket leaves a notice for
    /// the agent to drain, alongside the typed Reject <c>ProtocolVersionMismatchTests</c> pins.</summary>
    [Fact]
    public async Task AMismatchedHelloOverTheWireLeavesANoticeOnTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(cts.Token);

        using UdpClient oldClient = new();
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer,
            new HelloPacket("OldRevan", "end_m01aa", ClientNonce: 42, ContentDigest.Unknown, SessionPassword.None));
        Assert.True(length > 0);
        byte foreignVersion = (byte)(ProtocolConstants.Version - 1);
        buffer[2] = foreignVersion;

        await oldClient.SendAsync(buffer.AsMemory(0, length), hostEndPoint);

        using CancellationTokenSource receiveTimeout = new(TimeSpan.FromSeconds(15));
        await oldClient.ReceiveAsync(receiveTimeout.Token);

        IReadOnlyList<string> notices = host.DrainRejectedJoinNotices();
        string line = Assert.Single(notices);
        Assert.Contains($"v{foreignVersion} vs your v{ProtocolConstants.Version}", line);
        Assert.Contains("127.0.0.1", line);

        await cts.CancelAsync();
        await hostTask;
    }
}
