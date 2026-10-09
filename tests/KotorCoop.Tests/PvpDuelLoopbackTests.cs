using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A real host and client over loopback UDP, playing out a duel (docs/research/pvp-plan.md):
/// challenge, accept, countdown, the fight, the host ending it at 25%, and a client that refuses
/// cleanly when the host has duels off.
/// </summary>
public class PvpDuelLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static BehaviourDigest WithPvp(bool pvp) => BehaviourDigest.FromFields(
        pvp ? BehaviourDigest.Default.Flags | (uint)BehaviourFlags.Pvp : BehaviourDigest.Default.Flags,
        BehaviourDigest.Default.InterpolationMs, BehaviourDigest.Default.FollowLead);

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
        public Pair(bool hostPvp, bool clientPvp)
        {
            Host = new CoopHost(new SessionSettings
            {
                PlayerName = "Revan", Port = 0, TickRate = 30, Behaviour = WithPvp(hostPvp),
            });
            IPEndPoint endpoint = new(IPAddress.Loopback, Host.ListenEndPoint.Port);
            Client = new CoopClient(
                new SessionSettings { PlayerName = "Bastila", TickRate = 30, Behaviour = WithPvp(clientPvp) }, endpoint);
            Cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            HostTask = Host.RunAsync(Cts.Token);
            ClientTask = Client.RunAsync(Cts.Token);
        }

        public CoopHost Host { get; }

        public CoopClient Client { get; }

        public CancellationTokenSource Cts { get; }

        public Task HostTask { get; }

        public Task ClientTask { get; }

        public int HostHealth { get; set; } = 100;

        public int ClientHealth { get; set; } = 100;

        public string ClientModule { get; set; } = "tar_m02aa";

        public void Publish()
        {
            Host.PublishLocalState(new PlayerState(new Vector3f(10f, 20f, 0f), 0f, "tar_m02aa", HostHealth, 0, MaxHealth: 100));
            Client.PublishLocalState(
                new PlayerState(new Vector3f(11f, 20f, 0f), 0f, ClientModule, ClientHealth, 0, MaxHealth: 100));
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
    public async Task ADuelRunsFromChallengeToTheHostEndingItAtTwentyFivePercent()
    {
        using Pair pair = new(hostPvp: true, clientPvp: true);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;
        Assert.True(pair.Client.PvpEnabled);

        // The host challenges. The client's "my game knows about duels" reaches the host within
        // a couple of seconds; until it does the host says the other game is too old.
        List<PvpNotice> hostSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    hostSaid.AddRange(pair.Host.DrainPvpNotices());
                    return hostSaid.Any(n => n.Code == PvpNoticeCode.ChallengeSent);
                },
                Timeout,
                () =>
                {
                    pair.Publish();
                    if (pair.Host.PvpState.Phase == PvpPhase.Idle)
                    {
                        pair.Host.RequestPvp(PvpOp.Challenge, clientId);
                    }
                }),
            "the challenge must go out once the client has said it knows about duels");

        // The client hears about it and accepts.
        List<PvpNotice> clientSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    clientSaid.AddRange(pair.Client.DrainPvpNotices());
                    return clientSaid.Any(n => n.Code == PvpNoticeCode.Challenged);
                },
                Timeout,
                pair.Publish),
            "the client must be told it was challenged");
        Assert.Equal(CoopHost.HostPeerId, clientSaid.First(n => n.Code == PvpNoticeCode.Challenged).Other);

        pair.Client.RequestPvp(PvpOp.Accept);

        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Countdown && pair.Client.PvpState.Phase == PvpPhase.Countdown,
                Timeout,
                pair.Publish),
            "both games must see the countdown");
        Assert.True(pair.Client.PvpState.IsFighting(clientId));
        Assert.Equal(CoopHost.HostPeerId, pair.Client.PvpState.OpponentOf(clientId));

        // Five seconds later it is live, on both sides.
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Live && pair.Client.PvpState.Phase == PvpPhase.Live,
                Timeout,
                pair.Publish),
            "both games must see the fight begin");

        // The client's health crosses the line: the host ends it and both are told who won.
        pair.ClientHealth = 24;
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Idle && pair.Client.PvpState.Phase == PvpPhase.Idle,
                Timeout,
                pair.Publish),
            "the host must end the round at 25 percent");

        hostSaid.AddRange(pair.Host.DrainPvpNotices());
        Assert.Contains(hostSaid, n => n.Code == PvpNoticeCode.Won);

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    clientSaid.AddRange(pair.Client.DrainPvpNotices());
                    return clientSaid.Any(n => n.Code == PvpNoticeCode.Lost);
                },
                Timeout,
                pair.Publish),
            "the loser must be told");
    }

    [Fact]
    public async Task AClientCanChallengeAndTheHostAnswers()
    {
        using Pair pair = new(hostPvp: true, clientPvp: true);
        await Admit(pair);

        List<PvpNotice> clientSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    clientSaid.AddRange(pair.Client.DrainPvpNotices());
                    return clientSaid.Any(n => n.Code == PvpNoticeCode.ChallengeSent);
                },
                Timeout,
                () =>
                {
                    pair.Publish();
                    if (pair.Host.PvpState.Phase == PvpPhase.Idle)
                    {
                        pair.Client.RequestPvp(PvpOp.Challenge, CoopHost.HostPeerId);
                    }
                }),
            "the host must take the client's challenge");

        pair.Host.RequestPvp(PvpOp.Decline);

        Assert.True(
            await WaitForAsync(
                () =>
                {
                    clientSaid.AddRange(pair.Client.DrainPvpNotices());
                    return clientSaid.Any(n => n.Code == PvpNoticeCode.Declined);
                },
                Timeout,
                pair.Publish),
            "the challenger must be told no");
    }

    [Fact]
    public async Task AClientWhoSwitchedDuelsOffIsNotChallengeable()
    {
        using Pair pair = new(hostPvp: true, clientPvp: true);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;

        pair.Client.RequestPvp(PvpOp.OptOut);

        // The request is repeated for two seconds; a second is ample for the first copy to land.
        // (A challenge fired in the same breath can honestly win the race, and a host that has not
        // heard "duel off" yet has nothing to refuse with.)
        DateTime settle = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        await WaitForAsync(() => DateTime.UtcNow >= settle, TimeSpan.FromSeconds(3), pair.Publish);

        List<PvpNotice> hostSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    hostSaid.AddRange(pair.Host.DrainPvpNotices());
                    return hostSaid.Any(n => n.Code == PvpNoticeCode.NotAccepting);
                },
                Timeout,
                () =>
                {
                    pair.Publish();
                    pair.Host.RequestPvp(PvpOp.Challenge, clientId);
                }),
            $"the host's challenge must be turned away (heard: {string.Join(",", hostSaid.Select(n => n.Code).Distinct())})");
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }

    [Fact]
    public async Task AClientRefusesCleanlyWhenTheHostHasDuelsOff()
    {
        using Pair pair = new(hostPvp: false, clientPvp: true);
        await Admit(pair);

        // The host's value wins: the client adopts "off" the moment it is admitted.
        Assert.True(await WaitForAsync(() => !pair.Client.PvpEnabled, Timeout, pair.Publish));

        pair.Client.RequestPvp(PvpOp.Challenge, CoopHost.HostPeerId);

        PvpNotice said = Assert.Single(pair.Client.DrainPvpNotices());
        Assert.Equal(PvpNoticeCode.NotEnabled, said.Code);
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }

    [Fact]
    public async Task TheHostRefusesCleanlyWhenItHasDuelsOff()
    {
        using Pair pair = new(hostPvp: false, clientPvp: false);
        await Admit(pair);

        pair.Host.RequestPvp(PvpOp.Challenge, pair.Client.LocalPeerId);

        PvpNotice said = Assert.Single(pair.Host.DrainPvpNotices());
        Assert.Equal(PvpNoticeCode.NotEnabled, said.Code);
    }

    [Fact]
    public async Task AClientThatLeavesTheAreaCallsTheDuelOff()
    {
        using Pair pair = new(hostPvp: true, clientPvp: true);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;

        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Challenged,
                Timeout,
                () =>
                {
                    pair.Publish();
                    pair.Host.DrainPvpNotices();
                    if (pair.Host.PvpState.Phase == PvpPhase.Idle)
                    {
                        pair.Host.RequestPvp(PvpOp.Challenge, clientId);
                    }
                }));

        pair.Client.RequestPvp(PvpOp.Accept);
        Assert.True(
            await WaitForAsync(() => pair.Host.PvpState.Phase == PvpPhase.Countdown, Timeout, pair.Publish));

        pair.ClientModule = "tar_m03aa";

        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Idle && pair.Client.PvpState.Phase == PvpPhase.Idle,
                Timeout,
                pair.Publish),
            "an area change must call the duel off on both sides");
    }
}
