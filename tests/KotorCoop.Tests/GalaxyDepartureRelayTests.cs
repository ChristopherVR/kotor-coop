using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The galaxy-map departure relay (docs/research/galaxy-map-and-party-select.md, 2026-09-03
/// relay addendum): the wire codec, the pure follow rule, the planet-name table, and
/// <see cref="CoopAgent.PumpOnce"/> carrying it end to end through a minimal fake bridge and
/// session (the same shape <c>MidModuleMovieRelayTests</c> uses) — own-click announce with
/// startup baselining, dedup by (peer, serial), the eligibility gate, the same-module rule,
/// the replay request + ack + retry loop, the on-screen lines, and the movie-hoard suppression.
/// </summary>
public class GalaxyDepartureRelayTests
{
    // ---- codec ------------------------------------------------------------------------

    [Fact]
    public void TheDepartureRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        GalaxyDeparturePacket original = new(PeerId: 3, "ebo_m12aa", Serial: 9, Planet: 4);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        GalaxyDeparturePacket? read = PacketCodec.ReadGalaxyDeparture(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(original, read);
    }

    [Fact]
    public void APlanetOutOfRangeIsRefusedOnRead()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new GalaxyDeparturePacket(3, "ebo_m12aa", 1, Planet: 4));

        // Corrupt the planet in place: the codec's own reader is the last line of defence
        // before the plugin's own range check.
        int planetAt = length - 4;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer[planetAt..], 99);

        Assert.Null(PacketCodec.ReadGalaxyDeparture(buffer[..length]));
    }

    [Fact]
    public void AWrongPacketTypeIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new MidModuleMoviePacket(1, "ebo_m12aa", 1, "08"));

        Assert.Null(PacketCodec.ReadGalaxyDeparture(buffer[..length]));
    }

    [Fact]
    public void ThePacketTypeIsWhereTheChangelogSaysItIs()
    {
        Assert.Equal(72, (byte)PacketType.GalaxyDeparture);
    }

    // ---- planet names (planetary.2da row order) ----------------------------------------

    [Theory]
    [InlineData(1, "Taris")]
    [InlineData(3, "Dantooine")]
    [InlineData(4, "Tatooine")]
    [InlineData(5, "Kashyyyk")]
    [InlineData(6, "Manaan")]
    [InlineData(7, "Korriban")]
    [InlineData(9, "the Unknown World")]
    [InlineData(10, "the Star Forge")]
    public void PlanetNamesFollowThe2daRowOrder(int planet, string expected)
    {
        Assert.Equal(expected, GalaxyPlanetNames.Display(planet));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(15)]
    [InlineData(-1)]
    public void AnUnnamedIndexFallsBackToTheNumber(int planet)
    {
        Assert.Equal($"planet {planet}", GalaxyPlanetNames.Display(planet));
    }

    // ---- the pure follow rule ----------------------------------------------------------

    [Fact]
    public void SameModuleAndIdleReplays()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Replay, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "EBO_M12AA", inDialogue: false, inCombat: false, localMidTransition: false, ageMs: 0));
    }

    [Fact]
    public void ADifferentModuleDrops()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Drop, GalaxyDepartureFollowRule.Decide(
            "tat_m17ab", "ebo_m12aa", false, false, false, 0));
    }

    [Fact]
    public void AnUnreadableLocalModuleWaits()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Wait, GalaxyDepartureFollowRule.Decide(
            "area1234", "ebo_m12aa", false, false, false, 0));
        Assert.Equal(GalaxyDepartureFollowPath.Wait, GalaxyDepartureFollowRule.Decide(
            string.Empty, "ebo_m12aa", false, false, false, 0));
    }

    [Fact]
    public void MidTransitionWaits()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Wait, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "ebo_m12aa", false, false, localMidTransition: true, 0));
    }

    [Fact]
    public void DialogueAndCombatWait()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Wait, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "ebo_m12aa", inDialogue: true, false, false, 0));
        Assert.Equal(GalaxyDepartureFollowPath.Wait, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "ebo_m12aa", false, inCombat: true, false, 0));
    }

    [Fact]
    public void AnOldDepartureDrops()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Drop, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "ebo_m12aa", false, false, false, GalaxyDepartureFollowRule.ExpiryMs + 1));
    }

    [Fact]
    public void ADepartureFromNowhereDrops()
    {
        Assert.Equal(GalaxyDepartureFollowPath.Drop, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", string.Empty, false, false, false, 0));
        Assert.Equal(GalaxyDepartureFollowPath.Drop, GalaxyDepartureFollowRule.Decide(
            "ebo_m12aa", "area77", false, false, false, 0));
    }

    // ---- end to end through CoopAgent --------------------------------------------------

    private const string Ship = "ebo_m12aa";

    private sealed class DepartureBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Ship, 20, 0);

        public string Description => "galaxy-departure-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsTravel => true;

        public bool InDialogue { get; set; }

        public bool IsInDialogue => InDialogue;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        /// <summary>This instance's own confirm-hook publication (serial, planet).</summary>
        public (uint Serial, int Planet)? OwnDeparture { get; set; }

        public bool TryReadGalaxyDeparture(out uint serial, out int planet)
        {
            if (OwnDeparture is { } own)
            {
                (serial, planet) = own;
                return true;
            }

            serial = 0;
            planet = -1;
            return false;
        }

        /// <summary>Every planet the agent asked the plugin to replay, in order.</summary>
        public List<int> Requests { get; } = [];

        /// <summary>What the fake plugin answers to the NEXT request: (applied bumps by one,
        /// status). Null leaves the request unanswered.</summary>
        public uint? NextStatus { get; set; } = 1;

        private uint _applied;

        private uint _status;

        public bool TryRequestGalaxyDepart(int planet)
        {
            Requests.Add(planet);
            if (NextStatus is { } status)
            {
                _applied++;
                _status = status;
            }

            return true;
        }

        public bool TryReadGalaxyDepartStatus(out uint applied, out uint status)
        {
            applied = _applied;
            status = _status;
            return true;
        }

        public List<(string Module, string Waypoint)> TravelRequests { get; } = [];

        public List<string[]> TravelMovies { get; } = [];

        public bool TryRequestTravel(string module, string waypoint = "", IReadOnlyList<string>? movies = null)
        {
            TravelRequests.Add((module, waypoint));
            TravelMovies.Add(movies is null ? [] : [.. movies]);
            return true;
        }

        public List<ClientDoorRefusalReason> DoorRefusalReasons { get; } = [];

        public bool TryArmClientDoorRefusal(ClientDoorRefusalReason reason)
        {
            DoorRefusalReasons.Add(reason);
            return true;
        }

        public List<string> ChatLines { get; } = [];

        public bool PostChatLine(string line)
        {
            ChatLines.Add(line);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class DepartureSession : ICoopSession
    {
        public List<GalaxyDeparturePacket> Incoming { get; } = [];

        public IReadOnlyList<GalaxyDeparturePacket> DrainGalaxyDepartures()
        {
            List<GalaxyDeparturePacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

        public List<(string Module, uint Serial, int Planet)> Published { get; } = [];

        public void PublishGalaxyDeparture(string module, uint serial, int planet) =>
            Published.Add((module, serial, planet));

        public List<TransitionMoviePacket> IncomingMovies { get; } = [];

        public IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies()
        {
            List<TransitionMoviePacket> ready = [.. IncomingMovies];
            IncomingMovies.Clear();
            return ready;
        }

        public List<RemotePeer> Peers { get; } = [];

        public bool EnemyAuthority { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(uint flagId, sbyte value)
        {
        }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        public void PublishParty(IReadOnlyList<PartyMemberState> members)
        {
        }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
        }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience)
        {
        }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, DepartureBridge Bridge, DepartureSession Session) Build(
        bool host = false, FollowLeadMode followLeadMode = FollowLeadMode.Any, bool follow = true,
        bool clientDoorRefusal = false)
    {
        DepartureBridge bridge = new();
        DepartureSession session = new() { EnemyAuthority = host };
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            FollowHostModule = follow,
            ModuleSettleMs = 0,
            FollowLeadMode = followLeadMode,
            CombatPumpIntervalMs = 0,
            ClientDoorRefusalEnabled = clientDoorRefusal,
        });

        return (agent, bridge, session);
    }

    private static RemotePeer HostPeer(string module = Ship) =>
        new(CoopHost.HostPeerId, "Chris", new PlayerState(Vector3f.Zero, 0f, module, 20, 0));

    private static GalaxyDeparturePacket HostDeparture(uint serial = 1, int planet = 4, string module = Ship) =>
        new(CoopHost.HostPeerId, module, serial, planet);

    // ---- own click: announce ------------------------------------------------------------

    [Fact]
    public void TheFirstReadIsABaselineNotAnAnnouncement()
    {
        // The block outlives sessions: a serial left by an earlier session's click must not
        // become "everyone replay this" the moment the agent attaches.
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        bridge.OwnDeparture = (3, 5);

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(session.Published);
        Assert.Empty(bridge.ChatLines);
    }

    [Fact]
    public void AClickAfterTheBaselineIsAnnouncedWithTheModuleAndRepeated()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        bridge.OwnDeparture = (0, -1);
        agent.PumpOnce(); // baseline (serial 0)

        bridge.OwnDeparture = (1, 4);
        for (int i = 0; i < 8; i++)
        {
            agent.PumpOnce();
        }

        Assert.Equal(5, session.Published.Count); // AnnounceRepeats
        Assert.All(session.Published, p => Assert.Equal((Ship, 1u, 4), p));
        Assert.Contains(bridge.ChatLines, l => l.Contains("Tatooine"));
    }

    [Fact]
    public void OurOwnClickIsNeverReplayedBackOntoOurselves()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        bridge.OwnDeparture = (0, -1);
        agent.PumpOnce();
        bridge.OwnDeparture = (1, 4);
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    // ---- a peer's click: replay ---------------------------------------------------------

    [Fact]
    public void AClientReplaysTheHostsDepartureFromTheSameModule()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture(planet: 4));

        agent.PumpOnce();

        Assert.Equal([4], bridge.Requests);
        string line = Assert.Single(bridge.ChatLines);
        Assert.Contains("Chris", line);
        Assert.Contains("Tatooine", line);
        Assert.Contains("following", line);
    }

    [Fact]
    public void TheSameDepartureSerialIsReplayedOnlyOnce()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture(serial: 1));
        agent.PumpOnce();

        // The sender repeats for redundancy; a second copy must not become a second leg.
        session.Incoming.Add(HostDeparture(serial: 1));
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void ADepartureFromAnotherModuleIsDropped()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        bridge.Local = bridge.Local with { Module = "tat_m17ab" };
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
        Assert.Empty(bridge.ChatLines);
    }

    [Fact]
    public void ADepartureWaitsOutADialogueThenReplays()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());
        bridge.InDialogue = true;

        agent.PumpOnce();
        Assert.Empty(bridge.Requests);

        bridge.InDialogue = false;
        agent.PumpOnce();
        Assert.Equal([4], bridge.Requests);
    }

    [Fact]
    public void FollowHostModuleOffReplaysNothing()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build(follow: false);
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void TheHostFollowsAClientsDepartureUnderFollowLeadAny()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build(host: true);
        session.Peers.Add(new RemotePeer(2, "Sam", new PlayerState(Vector3f.Zero, 0f, Ship, 20, 0)));
        session.Incoming.Add(new GalaxyDeparturePacket(2, Ship, 1, 5));

        agent.PumpOnce();

        Assert.Equal([5], bridge.Requests);
        Assert.Contains(bridge.ChatLines, l => l.Contains("Sam") && l.Contains("Kashyyyk"));
    }

    [Fact]
    public void TheHostIgnoresAClientsDepartureUnderFollowLeadHost()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build(host: true, followLeadMode: FollowLeadMode.Host);
        session.Peers.Add(new RemotePeer(2, "Sam", new PlayerState(Vector3f.Zero, 0f, Ship, 20, 0)));
        session.Incoming.Add(new GalaxyDeparturePacket(2, Ship, 1, 5));

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void AClientStillFollowsTheHostUnderFollowLeadHost()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build(followLeadMode: FollowLeadMode.Host);
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());

        agent.PumpOnce();

        Assert.Equal([4], bridge.Requests);
    }

    // ---- the ack / retry loop -----------------------------------------------------------

    [Fact]
    public void ARefusedReplayIsNotRetriedOnTheVeryNextTick()
    {
        // The retry spacing is RetryMs (2 s); within one test's few ticks that means exactly
        // one attempt — and no chat line for a refusal that is still being retried.
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());
        bridge.NextStatus = 2;

        for (int i = 0; i < 5; i++)
        {
            agent.PumpOnce();
        }

        Assert.Single(bridge.Requests);
        Assert.Single(bridge.ChatLines);
    }

    [Fact]
    public void ABadPlanetAnswerGivesUpAndSaysSo()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());
        bridge.NextStatus = 3;

        agent.PumpOnce(); // request
        agent.PumpOnce(); // ack read
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
        Assert.Contains(bridge.ChatLines, l => l.StartsWith("could not follow"));
    }

    [Fact]
    public void ASecondDepartureAfterASuccessfulLegIsNotReplayedMidLeg()
    {
        // While our own engine is inside the leg it accepted, a repeat/another copy of the
        // same moment must not queue a second leg on top of it.
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture(serial: 1));
        agent.PumpOnce();
        agent.PumpOnce();

        session.Incoming.Add(HostDeparture(serial: 2));
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
    }

    // ---- interplay with the transition-movie hoard ----------------------------------------

    [Fact]
    public void TheLeadersLegMoviesAreNotHoardedForALaterFollowWarp()
    {
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build();
        session.Peers.Add(HostPeer());
        session.Incoming.Add(HostDeparture());
        agent.PumpOnce(); // replay requested and acked as departed
        agent.PumpOnce();

        // The leader's take-off movie announce lands while we are mid-leg ourselves...
        session.IncomingMovies.Add(new TransitionMoviePacket(CoopHost.HostPeerId, Ship, 1, ["08"]));
        agent.PumpOnce();

        // ...and a later, unrelated follow-warp must not carry it.
        session.Peers[0] = HostPeer("tat_m17ab");
        bridge.Local = bridge.Local with { Module = "tat_m17aa" };
        for (int i = 0; i < 3; i++)
        {
            agent.PumpOnce();
        }

        Assert.NotEmpty(bridge.TravelRequests);
        Assert.All(bridge.TravelMovies, movies => Assert.Empty(movies));
    }

    [Fact]
    public void AHostDepartureCountsAsTheHostTransitioningForTheDoorRefusalArm()
    {
        // Same signal a door announce gives: under follow_lead=host the client's own door is
        // refused (HostMustLead) - until the host is known to be transitioning, when the
        // client crossing the same door must be let through. Read back through the reason
        // the travel pump writes every tick.
        (CoopAgent agent, DepartureBridge bridge, DepartureSession session) = Build(
            followLeadMode: FollowLeadMode.Host, clientDoorRefusal: true);
        session.Peers.Add(HostPeer());

        agent.PumpOnce();
        Assert.Equal(ClientDoorRefusalReason.HostMustLead, bridge.DoorRefusalReasons[^1]);

        session.Incoming.Add(HostDeparture());
        agent.PumpOnce();
        Assert.Equal(ClientDoorRefusalReason.None, bridge.DoorRefusalReasons[^1]);
    }
}
