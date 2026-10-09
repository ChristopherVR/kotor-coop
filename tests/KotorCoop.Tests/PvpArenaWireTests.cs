using System.Net;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arena parts of the wire and the setting (docs/research/pvp-plan.md, phases 3 and 4): the
/// <c>pvp = arena</c> value, its behaviour bit, the new packet shapes, the console word, the lines
/// a player reads, and a real host and client walking a round through the arena over loopback.
/// </summary>
public class PvpArenaWireTests
{
    // ---- pvp = arena ------------------------------------------------------------------------

    [Fact]
    public void ArenaIsOffByDefaultAndHasItsOwnBit()
    {
        Assert.False(BehaviourDigest.Default.Has(BehaviourFlags.PvpArena));
        Assert.Equal(1u << 23, (uint)BehaviourFlags.PvpArena);
        Assert.False(new CoopConfig().PvpArena);
    }

    [Theory]
    [InlineData("arena", true, true)]
    [InlineData("ARENA", true, true)]
    [InlineData("duel", true, false)]
    [InlineData("off", false, false)]
    [InlineData("", false, false)]
    [InlineData("arenas", false, false)]
    public void TheIniValueGivesDuelsAndArenaTheRightWay(string value, bool duel, bool arena)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"pvp = {value}\n");

            CoopConfig? config = CoopConfig.TryLoad(path);

            Assert.NotNull(config);
            Assert.Equal(duel, config!.Pvp);
            Assert.Equal(arena, config.PvpArena);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADigestCarriesTheArenaBitAndADifferenceIsHostAuthoritative()
    {
        BehaviourDigest arena = Digest(pvp: true, pvpArena: true);
        BehaviourDigest duel = Digest(pvp: true, pvpArena: false);

        Assert.True(arena.Has(BehaviourFlags.PvpArena));
        Assert.False(duel.Has(BehaviourFlags.PvpArena));
        Assert.NotEqual(arena.Hash, duel.Hash);

        BehaviourDifference diff = Assert.Single(BehaviourCompatibility.Compare(arena, duel).Differences);
        Assert.Equal("pvp_arena", diff.Key);
        Assert.Equal("1", diff.HostValue);
        Assert.True(diff.HostAuthoritative);
        Assert.True(BehaviourCompatibility.Compare(arena, arena).IsEmpty);
    }

    private static BehaviourDigest Digest(bool pvp, bool pvpArena) => BehaviourDigest.Compute(
        localCombat: false, companionLocalCombat: false, partyFastPump: false, partySync: true,
        clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
        effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
        dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
        mapRevealSync: true, requireIdenticalSave: false, interpolationMs: 100, followLead: "any",
        pvp: pvp, pvpArena: pvpArena);

    // ---- packets ----------------------------------------------------------------------------

    [Theory]
    [InlineData(PvpOp.ChallengeArena)]
    [InlineData(PvpOp.AwareArena)]
    public void TheNewIntentsRoundTrip(PvpOp op)
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpIntentPacket sent = new(77, 5, 123456, op, 9);

        int length = PacketCodec.Write(buffer, sent);

        Assert.Equal(sent, PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void AnIntentPastTheLastOpIsStillRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new PvpIntentPacket(1, 2, 3, PvpOp.Accept, 0));
        buffer[length - 5] = (byte)PvpOp.AwareArena + 1;

        Assert.Null(PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length)));
    }

    [Theory]
    [InlineData(PvpPhase.Entering)]
    [InlineData(PvpPhase.Leaving)]
    public void TheArenaPhasesAndFlagRoundTrip(PvpPhase phase)
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpStatePacket sent = new(
            77, phase, 4, 1, 5, [new PvpNotice(10, 5, PvpNoticeCode.ArenaEntering, 1)], Arena: true);

        int length = PacketCodec.Write(buffer, sent);
        PvpStatePacket? read = PacketCodec.ReadPvpState(buffer.AsSpan(0, length));

        Assert.NotNull(read);
        Assert.Equal(phase, read!.Phase);
        Assert.True(read.Arena);
        Assert.Equal(sent.Notices, read.Notices);
    }

    [Fact]
    public void AStateFromABuildWithoutArenasReadsAsAPlainDuel()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new PvpStatePacket(1, PvpPhase.Live, 2, 1, 3, [], Arena: true));

        // The older shape simply ends before the flag.
        PvpStatePacket? read = PacketCodec.ReadPvpState(buffer.AsSpan(0, length - 1));

        Assert.NotNull(read);
        Assert.False(read!.Arena);
        Assert.Equal(PvpPhase.Live, read.Phase);
    }

    [Fact]
    public void APhasePastLeavingIsRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new PvpStatePacket(1, PvpPhase.Idle, 0, 0, 0, []));
        // Header (magic 2, version 1, type 1), session id (4), then the phase byte.
        buffer[8] = (byte)PvpPhase.Leaving + 1;

        Assert.Null(PacketCodec.ReadPvpState(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void ANewNoticeCodePastTheLastIsRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer,
            new PvpStatePacket(1, PvpPhase.Idle, 0, 0, 0, [new PvpNotice(1, 2, PvpNoticeCode.ArenaFailed, 3)]));
        Assert.NotNull(PacketCodec.ReadPvpState(buffer.AsSpan(0, length)));

        buffer[length - 6] = (byte)PvpNoticeCode.ArenaFailed + 1;
        Assert.Null(PacketCodec.ReadPvpState(buffer.AsSpan(0, length)));
    }

    // ---- words and lines --------------------------------------------------------------------

    [Fact]
    public void ArenaIsAWordAPlayerCanType()
    {
        CoopCommand command = ConsoleCommand.Parse("arena Aria");
        Assert.Equal(CoopCommandKind.DuelArena, command.Kind);
        Assert.Equal("Aria", command.Address);

        CoopCommand spaced = ConsoleCommand.Parse("arena Darth Malak");
        Assert.Equal("Darth Malak", spaced.Address);

        CoopCommand bare = ConsoleCommand.Parse("arena");
        Assert.Equal(CoopCommandKind.Invalid, bare.Kind);
        Assert.Contains("accept", bare.Error);

        Assert.Contains(ConsoleVerbCatalog.Entries, entry => entry.Verb == "arena");
    }

    [Fact]
    public void TheArenaLinesSayWhatToDo()
    {
        Assert.Contains("pvp = arena", PvpText.Format(PvpNoticeCode.ArenaNotEnabled, string.Empty));

        string self = PvpText.Format(PvpNoticeCode.ArenaMissing, "Aria", aboutSelf: true);
        Assert.StartsWith("You do not have the arena", self);
        Assert.Contains("setup", self);

        string other = PvpText.Format(PvpNoticeCode.ArenaMissing, "Aria");
        Assert.StartsWith("Aria does not have the arena", other);

        Assert.Contains("Aria", PvpText.Format(PvpNoticeCode.ArenaHostApart, "Aria"));
        Assert.Contains("Aria", PvpText.Format(PvpNoticeCode.ArenaEntering, "Aria"));
        Assert.Contains("yield", PvpText.Format(PvpNoticeCode.ArenaEntering, "Aria"));
        Assert.Contains("Nothing was lost", PvpText.Format(PvpNoticeCode.ArenaFailed, "Aria"));
        Assert.Contains("back", PvpText.Format(PvpNoticeCode.ArenaReturning, "Aria"));
        Assert.Contains("wait", PvpText.SavingBlockedInArenaLine, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the whole walk over loopback -------------------------------------------------------

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static BehaviourDigest Behaviour(bool arena) => BehaviourDigest.FromFields(
        BehaviourDigest.Default.Flags | (uint)BehaviourFlags.Pvp | (arena ? (uint)BehaviourFlags.PvpArena : 0u),
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
        public Pair(bool hostArena, bool clientReady)
        {
            Host = new CoopHost(new SessionSettings
            {
                PlayerName = "Revan", Port = 0, TickRate = 30, Behaviour = Behaviour(hostArena), PvpArenaReady = true,
            });
            IPEndPoint endpoint = new(IPAddress.Loopback, Host.ListenEndPoint.Port);
            Client = new CoopClient(
                new SessionSettings
                {
                    PlayerName = "Bastila", TickRate = 30, Behaviour = Behaviour(hostArena), PvpArenaReady = clientReady,
                },
                endpoint);
            Cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            HostTask = Host.RunAsync(Cts.Token);
            ClientTask = Client.RunAsync(Cts.Token);
        }

        public CoopHost Host { get; }

        public CoopClient Client { get; }

        public CancellationTokenSource Cts { get; }

        public Task HostTask { get; }

        public Task ClientTask { get; }

        public string HostModule { get; set; } = "tar_m02aa";

        public string ClientModule { get; set; } = "tar_m02aa";

        public int ClientHealth { get; set; } = 100;

        public void Publish()
        {
            Host.PublishLocalState(new PlayerState(new Vector3f(10f, 20f, 0f), 0f, HostModule, 100, 0, MaxHealth: 100));
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
    public async Task AnArenaRoundWalksInFightsAndWalksBack()
    {
        using Pair pair = new(hostArena: true, clientReady: true);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;
        Assert.True(pair.Client.PvpArenaEnabled);

        // The host asks for an arena fight once the client has said it has the arena.
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
                        pair.Host.RequestPvp(PvpOp.ChallengeArena, clientId);
                    }
                }),
            "the arena challenge must go out once the client has said it has the arena");

        Assert.True(
            await WaitForAsync(() => pair.Client.DrainPvpNotices().Any(n => n.Code == PvpNoticeCode.Challenged), Timeout, pair.Publish));
        pair.Client.RequestPvp(PvpOp.Accept);

        // Accepting sends the party in: both see the walk-in, flagged as an arena round, nobody pinned.
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Entering && pair.Client.PvpState.Phase == PvpPhase.Entering,
                Timeout,
                pair.Publish),
            "both games must see the walk in");
        Assert.True(pair.Client.PvpState.Arena);
        Assert.True(pair.Client.PvpState.ArenaActive);
        Assert.False(pair.Client.PvpState.IsFighting(clientId));

        // The host's warp lands everyone in the arena module; after the settle the countdown starts.
        pair.HostModule = PvpDuelRound.ArenaModule;
        pair.ClientModule = PvpDuelRound.ArenaModule;
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Countdown && pair.Client.PvpState.Phase == PvpPhase.Countdown,
                Timeout,
                pair.Publish),
            "the countdown must follow the arrival");
        Assert.True(pair.Client.PvpState.IsFighting(clientId));

        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Live && pair.Client.PvpState.Phase == PvpPhase.Live,
                Timeout,
                pair.Publish),
            "the fight must begin");

        // The fight ends at 25 percent; the round then goes to the way back rather than idle.
        pair.ClientHealth = 24;
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Leaving && pair.Client.PvpState.Phase == PvpPhase.Leaving,
                Timeout,
                pair.Publish),
            "the host must send everyone back");
        Assert.False(pair.Client.PvpState.IsFighting(clientId));
        Assert.True(pair.Client.PvpState.ArenaActive);

        // Back in the original area, the round lets go.
        pair.HostModule = "tar_m02aa";
        pair.ClientModule = "tar_m02aa";
        pair.ClientHealth = 100;
        Assert.True(
            await WaitForAsync(
                () => pair.Host.PvpState.Phase == PvpPhase.Idle && pair.Client.PvpState.Phase == PvpPhase.Idle,
                Timeout,
                pair.Publish),
            "the round must end once everyone is back");
        Assert.False(pair.Client.PvpState.ArenaActive);
    }

    [Fact]
    public async Task AnArenaChallengeIsTurnedAwayForAClientWithoutTheArena()
    {
        using Pair pair = new(hostArena: true, clientReady: false);
        await Admit(pair);
        uint clientId = pair.Client.LocalPeerId;

        // The client says plain "duels" (no arena module), so the host names it.
        List<PvpNotice> hostSaid = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    hostSaid.AddRange(pair.Host.DrainPvpNotices());
                    return hostSaid.Any(n => n.Code == PvpNoticeCode.ArenaMissing);
                },
                Timeout,
                () =>
                {
                    pair.Publish();
                    pair.Host.RequestPvp(PvpOp.ChallengeArena, clientId);
                }),
            "the host must refuse naming the player without the arena");

        PvpNotice refusal = hostSaid.First(n => n.Code == PvpNoticeCode.ArenaMissing);
        Assert.Equal(clientId, refusal.Other);
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }

    [Fact]
    public async Task AnArenaChallengeIsRefusedWhenTheHostOnlyAllowsDuels()
    {
        using Pair pair = new(hostArena: false, clientReady: true);
        await Admit(pair);
        Assert.False(pair.Client.PvpArenaEnabled);

        pair.Client.RequestPvp(PvpOp.ChallengeArena, CoopHost.HostPeerId);

        List<PvpNotice> said = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    said.AddRange(pair.Client.DrainPvpNotices());
                    return said.Any(n => n.Code == PvpNoticeCode.ArenaNotEnabled);
                },
                Timeout,
                pair.Publish),
            "the client must be told the arena is off without a round trip");
        Assert.Equal(PvpPhase.Idle, pair.Host.PvpState.Phase);
    }
}
