using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The boss fight over a real loopback socket: a host and one joiner, the round walking in,
/// a power reaching both games, the boss going down and everyone walking back. The games
/// themselves are stood in for by the module name each side publishes.
/// </summary>
[Trait("Category", "Slow")]
public class ChungusLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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

    private sealed class Pair : IDisposable
    {
        public Pair(bool clientHasBoss)
        {
            // The pvp setting is left off on purpose: the boss fight is a cheat and must not need it.
            Host = new CoopHost(new SessionSettings
            {
                PlayerName = "Revan", Port = 0, TickRate = 30, PvpArenaReady = true, BossReady = true,
            });
            IPEndPoint endpoint = new(IPAddress.Loopback, Host.ListenEndPoint.Port);
            Client = new CoopClient(
                new SessionSettings
                {
                    PlayerName = "Bastila", TickRate = 30, PvpArenaReady = true, BossReady = clientHasBoss,
                },
                endpoint);
            Cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            HostTask = Host.RunAsync(Cts.Token);
            ClientTask = Client.RunAsync(Cts.Token);
        }

        public CoopHost Host { get; }

        public CoopClient Client { get; }

        public CancellationTokenSource Cts { get; }

        public Task HostTask { get; }

        public Task ClientTask { get; }

        public string Module { get; set; } = "tar_m02aa";

        public void Publish()
        {
            Host.PublishLocalState(new PlayerState(new Vector3f(10f, 20f, 0f), 0f, Module, 100, 0, MaxHealth: 100));
            Client.PublishLocalState(new PlayerState(new Vector3f(11f, 20f, 0f), 0f, Module, 100, 0, MaxHealth: 100));
            WorldDigest world = new(1, 1, "tar_m02aa", 1, NativeWorldEpoch: 1);
            Host.PublishWorldDigest(world);
            Client.PublishWorldDigest(world);
            Host.PublishPartyRoster(new PartyRoster(1, 1, HostNativeEpoch: 1));
        }

        public void Dispose()
        {
            Cts.Cancel();
            Client.Dispose();
            Host.Dispose();
        }
    }

    private static async Task Admit(Pair pair)
    {
        Assert.True(
            await WaitForAsync(
                () => pair.Client.IsConnected && pair.Client.RemoteWorldDigest.NativeWorldEpoch == 1
                    && pair.Host.GetRemotePeers().Count == 1,
                Timeout,
                pair.Publish),
            "client must be admitted");
    }

    [Fact]
    public async Task TheBossFightWalksInPlaysAPowerOnBothGamesAndWalksBack()
    {
        using Pair pair = new(clientHasBoss: true);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;
        Assert.False(pair.Host.PvpEnabled);

        // The host asks until the joiner has said it can load the boss.
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Entering,
                Timeout,
                () =>
                {
                    pair.Publish();
                    pair.Host.DrainPvpNotices();
                    if (pair.Host.PvpState.Phase == PvpPhase.Idle)
                    {
                        pair.Host.RequestPvp(PvpOp.Chungus);
                    }
                }),
            "the fight must start once the joiner has said it has the boss");

        Assert.True(
            await WaitForAsync(() => pair.Client.PvpState is { Phase: PvpPhase.Entering, Boss: true, Arena: true }, Timeout, pair.Publish),
            "the joiner must see the walk in, as a boss round");
        Assert.True(pair.Client.PvpState.ArenaActive);
        Assert.False(pair.Client.PvpState.IsFighting(clientId));
        Assert.Contains(pair.Client.DrainPvpNotices(), n => n.Code == PvpNoticeCode.BossEntering);

        // Everyone lands in the arena; after the settle and the countdown the fight is on and
        // every player is pinned, with no opponent.
        pair.Module = PvpDuelRound.ArenaModule;
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Live && pair.Client.PvpState.Phase == PvpPhase.Live,
                Timeout,
                pair.Publish),
            "the fight must go live after the arrival and the countdown");
        Assert.Equal((1u, 0u), PvpPumpRule.Levels(pair.Client.PvpState, clientId, pair.Client.PvpEnabled));
        Assert.Equal((1u, 0u), PvpPumpRule.Levels(pair.Host.PvpState, pair.Host.LocalPeerId, pair.Host.PvpEnabled));

        // A power: both games get it to play out, once.
        BossCue cue = new(BossPower.Blast, 30, (ushort)clientId);
        pair.Host.AnnounceBossCue(cue);
        Assert.Equal([cue], pair.Host.DrainBossCues());
        List<BossCue> heard = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    heard.AddRange(pair.Client.DrainBossCues());
                    return heard.Count > 0;
                },
                Timeout,
                pair.Publish),
            "the joiner must be told about the power");
        await Task.Delay(1_200);
        pair.Publish();
        heard.AddRange(pair.Client.DrainBossCues());
        Assert.Equal([cue], heard);
        Assert.True(heard[0].Hits(clientId));
        Assert.False(heard[0].Hits(pair.Host.LocalPeerId));

        // The boss goes down: told to both, then the way home, then idle.
        pair.Host.ReportBossDown();
        List<PvpNotice> clientSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    clientSaid.AddRange(pair.Client.DrainPvpNotices());
                    return pair.Client.PvpState.Phase == PvpPhase.Leaving && clientSaid.Any(n => n.Code == PvpNoticeCode.BossWon);
                },
                Timeout,
                pair.Publish),
            "the joiner must be told the boss is beaten and see the way back");
        Assert.False(pair.Client.PvpState.IsFighting(clientId));
        Assert.False(PvpPumpRule.SaveAllowed(pair.Client.PvpState, clientId));

        pair.Module = "tar_m02aa";
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Idle && pair.Client.PvpState.Phase == PvpPhase.Idle,
                Timeout,
                pair.Publish),
            "the round must let go once everyone is home");
        Assert.True(PvpPumpRule.SaveAllowed(pair.Client.PvpState, clientId));
        Assert.Contains("beaten", pair.Host.PvpLastEnding);
    }

    [Fact]
    public async Task AJoinerWithoutTheBossKeepsEveryoneHome()
    {
        using Pair pair = new(clientHasBoss: false);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;

        // Give the handshake time to say what it is going to say, then ask.
        await WaitForAsync(() => false, TimeSpan.FromSeconds(3), pair.Publish);
        pair.Host.DrainPvpNotices();
        pair.Host.RequestPvp(PvpOp.Chungus);

        IReadOnlyList<PvpNotice> said = pair.Host.DrainPvpNotices();
        PvpNotice refusal = Assert.Single(said);
        Assert.Equal((PvpNoticeCode.BossNotReady, clientId), (refusal.Code, refusal.Other));
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }

    [Fact]
    public async Task AJoinerCannotStartItUnlessTheHostAllowsArenaFights()
    {
        using Pair pair = new(clientHasBoss: true);
        await Admit(pair);

        pair.Client.RequestPvp(PvpOp.Chungus);

        List<PvpNotice> said = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    said.AddRange(pair.Client.DrainPvpNotices());
                    return said.Count > 0;
                },
                Timeout,
                pair.Publish),
            "the joiner must be told why not");
        Assert.Equal(PvpNoticeCode.BossHostOnly, said[0].Code);
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }
}
