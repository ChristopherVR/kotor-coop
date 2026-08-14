using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The peer identity is what the host keys a peer slot on, so the properties that matter are
/// that it survives the process and that it never collides with the "mint me one" sentinel.
/// </summary>
public class PeerIdentityTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kotorcoop-id-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // A leftover temp directory is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AnIdentityIsMintedOnceAndReadBackAfterwards()
    {
        uint first = PeerIdentity.ForDirectory(_directory);
        uint second = PeerIdentity.ForDirectory(_directory);

        Assert.NotEqual(0u, first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(_directory, PeerIdentity.FileName)));
    }

    [Fact]
    public void TwoInstallsGetDifferentIdentities()
    {
        // The case that matters: two game directories on one machine must not merge into one
        // peer slot, which is precisely what a machine-derived identity would have done.
        string other = _directory + "-p2";
        try
        {
            Assert.NotEqual(PeerIdentity.ForDirectory(_directory), PeerIdentity.ForDirectory(other));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Theory]
    [InlineData("1A2B3C4D", 0x1A2B3C4Du)]
    [InlineData("0x1a2b3c4d", 0x1A2B3C4Du)]
    [InlineData("  1A2B3C4D \r\n", 0x1A2B3C4Du)]
    public void AHandEditedFileIsRead(string contents, uint expected)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, PeerIdentity.FileName);
        File.WriteAllText(path, contents);

        Assert.True(PeerIdentity.TryRead(path, out uint identity));
        Assert.Equal(expected, identity);
        Assert.Equal(expected, PeerIdentity.ForDirectory(_directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a number")]
    [InlineData("00000000")]
    public void AnUnusableFileIsReplacedRatherThanTrusted(string contents)
    {
        // Zero is the "no stable identity" sentinel, so a file holding it must not be adopted:
        // a client sending nonce 0 would match every other identity-less peer.
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, PeerIdentity.FileName);
        File.WriteAllText(path, contents);

        Assert.False(PeerIdentity.TryRead(path, out _));

        uint minted = PeerIdentity.ForDirectory(_directory);
        Assert.NotEqual(0u, minted);
        Assert.Equal(minted, PeerIdentity.ForDirectory(_directory));
    }

    [Fact]
    public void AnUnwritableLocationStillYieldsAWorkingIdentity()
    {
        // A read-only install costs stability, not the session.
        uint identity = PeerIdentity.ForDirectory(string.Empty);
        Assert.NotEqual(0u, identity);
    }

    [Fact]
    public async Task ARelaunchedClientReclaimsItsPeerSlotRatherThanBecomingASecondPlayer()
    {
        // The behaviour the value exists for. A relaunched agent — and the console's
        // `reconnect`, which builds a fresh CoopClient through SessionManager.Connect — used to
        // mint a NEW nonce and therefore land in a NEW peer slot, while the old one (that
        // player's phantom, gear, damage totals and dialogue token) lingered until it timed
        // out. Staged as the host sees it: the first client is still in the peer table, having
        // told nobody it was going, when the replacement dials in.
        //
        // ⚠ The reclaim window is the peer SLOT's lifetime, not forever — the host matches a
        // returning Hello against its live peers. AFarewellIsStillAFarewell is the other half.
        uint identity = PeerIdentity.Mint();

        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(cts.Token);

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        using CoopClient first = new(Settings(identity), endpoint);
        using CoopClient second = new(Settings(identity), endpoint);

        using CancellationTokenSource clientCts = new();
        Task firstTask = first.RunAsync(clientCts.Token);

        Assert.True(
            await WaitAsync(() => first.IsConnected, () => Publish(host, state, first)),
            "the first client never joined");
        uint firstId = first.LocalPeerId;

        // No Bye, no timeout: the slot is still live and the identity is the only thing that
        // can say the newcomer is the same player.
        Task secondTask = second.RunAsync(clientCts.Token);

        Assert.True(
            await WaitAsync(() => second.IsConnected, () => Publish(host, state, first, second)),
            "the relaunched client never joined");

        Assert.NotEqual(0u, firstId);
        Assert.Equal(firstId, second.LocalPeerId);
        Assert.Equal(1, host.GetStats().PeerCount);

        await clientCts.CancelAsync();
        await Task.WhenAll(firstTask, secondTask);
        await cts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task AFarewellIsStillAFarewell()
    {
        // A stable identity must not resurrect a player who LEFT. A clean Bye retires the peer
        // slot, and coming back afterwards is a fresh join — which is what the session telling
        // everyone "so-and-so left" already means.
        uint identity = PeerIdentity.Mint();

        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(cts.Token);

        IPEndPoint endpoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);
        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        uint firstId;
        using (CoopClient first = new(Settings(identity), endpoint))
        {
            using CancellationTokenSource firstCts = new();
            Task firstTask = first.RunAsync(firstCts.Token);

            Assert.True(
                await WaitAsync(() => first.IsConnected, () => Publish(host, state, first)),
                "the first client never joined");
            firstId = first.LocalPeerId;

            await firstCts.CancelAsync();
            await firstTask; // sends Bye on the way out
        }

        Assert.True(
            await WaitAsync(() => host.GetStats().PeerCount == 0, () => host.PublishLocalState(state)),
            "the host never retired the departed peer");

        using CoopClient second = new(Settings(identity), endpoint);
        using CancellationTokenSource secondCts = new();
        Task secondTask = second.RunAsync(secondCts.Token);

        Assert.True(
            await WaitAsync(() => second.IsConnected, () => Publish(host, state, second)),
            "the returning client never joined");

        Assert.NotEqual(0u, firstId);
        Assert.NotEqual(firstId, second.LocalPeerId);

        await secondCts.CancelAsync();
        await secondTask;
        await cts.CancelAsync();
        await hostTask;
    }

    private static SessionSettings Settings(uint identity) =>
        new() { PlayerName = "Malak", TickRate = 30, PeerIdentity = identity };

    private static void Publish(CoopHost host, in PlayerState state, params CoopClient[] clients)
    {
        host.PublishLocalState(state);
        foreach (CoopClient client in clients)
        {
            client.PublishLocalState(state);
        }
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, Action each)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            each();
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }
}
