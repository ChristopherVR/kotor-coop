using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The follow-the-host rule: a client travels to whatever module the host is in, the host
/// never follows anyone, and the request is debounced so a module load in progress is not
/// interrupted by a duplicate. Pure decision logic — the engine side is the plugin's
/// TravelController, which cannot run outside the game.
/// </summary>
public class TravelPumpTests
{
    private const string HostModule = "tar_m02aa";
    private const string ClientModule = "end_m01aa";

    private sealed class TravelBridge : IGameBridge
    {
        public List<(string Module, string Waypoint)> Requests { get; } = [];

        public bool InDialogue { get; set; }

        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        public string Description => "travel-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsTravel => true;

        public bool IsInDialogue => InDialogue;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryRequestTravel(string module, string waypoint = "")
        {
            Requests.Add((module, waypoint));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public List<RemotePeer> Peers { get; } = [];

        public bool EnemyAuthority { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(uint flagId, sbyte value)
        {
        }

        public uint DropItem(string resref, string module, Vector3f position) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemyReports(string module) => [];

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(System.Collections.Generic.IReadOnlyList<PartyMemberState> members) { }

        public System.Collections.Generic.IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience) { }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text) { }

        public System.Collections.Generic.IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, TravelBridge Bridge, StubSession Session) Build(
        bool follow = true, int retryMs = 15_000, int settleMs = 0)
    {
        TravelBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            FollowHostModule = follow,
            FollowRetryMs = retryMs,

            // These tests are about the DEBOUNCE. The post-arrival settle window is a
            // separate rule with its own test below, and leaving it on here would mean
            // every case had to wait out a wall clock nothing else in the suite depends on.
            ModuleSettleMs = settleMs,
        });

        return (agent, bridge, session);
    }

    private static RemotePeer HostPeer(string module) =>
        new(CoopHost.HostPeerId, "host", new PlayerState(Vector3f.Zero, 0f, module, 20, 0));

    [Fact]
    public void ClientFollowsTheHostIntoAnotherModule()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        (string module, string waypoint) = Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, module);
        Assert.Equal(string.Empty, waypoint);
    }

    [Fact]
    public void RepeatedPumpsDoNotRepeatTheRequestWhileTheLoadIsInFlight()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void SameModuleMeansNoRequest()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(ClientModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void TheHostNeverFollowsAnyone()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.Peers.Add(new RemotePeer(2, "client",
            new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0)));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void StuntModulesAreNeverFollowedInto()
    {
        // Stunt modules stage cutscenes; the host comes back out on its own.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer("STUNT_06"));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void FallbackAreaNamesAreNeverFollowedInto()
    {
        // "area1F" is the bridge's stand-in when the host's module name was unreadable
        // (a transition in flight); it names nothing on disk. A real module that merely
        // starts with "area" would be fine — only the pure hex-suffix form is held back.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer("area1F"));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void NoRequestWhileTheLocalPlayerIsInDialogue()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        bridge.InDialogue = true;
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void FollowCanBeDisabled()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(follow: false);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void ArrivingResetsTheDebounceSoTheNextSeparationActsImmediately()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        Assert.Single(bridge.Requests);

        // The client arrives: local module now matches the host's.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0);
        agent.PumpOnce();

        // The host moves on again; the follow must fire without waiting out the retry.
        session.Peers.Clear();
        session.Peers.Add(HostPeer("dan_m13aa"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("dan_m13aa", bridge.Requests[1].Module);
    }

    [Fact]
    public void ArrivingSomewhereHoldsTheNextFollowUntilTheWorldHasSettled()
    {
        // The crash guard: leaving a module moments after arriving in it killed the client
        // in the engine's creature-add handler, which dereferences CClientExoApp::GetModule
        // unchecked while the previous world's object messages are still being serviced.
        // A client that walks through its own door is brought straight back by the follow,
        // so this pair is ordinary play rather than a contrived case.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(settleMs: 60_000);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        Assert.Single(bridge.Requests); // the FIRST follow is not held: we never transitioned

        // We arrive somewhere of our own — a different module from both.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, "dan_m13aa", 20, 0);
        agent.PumpOnce();

        // The host is still elsewhere, so the follow wants to fire immediately. It must not.
        agent.PumpOnce();
        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void NoFollowWhileOurOwnModuleNameIsTheMidTransitionFallback()
    {
        // "areaXX" means the module name could not be read, which happens for exactly one
        // reason: we are mid-transition. Comparing that placeholder against the host's real
        // module reads as separation and fires a SECOND transition into the middle of the
        // first — which is what killed the client in the engine's creature-add handler.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, "area1F", 20, 0);

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);

        // Once the real name resolves, the follow behaves normally.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, ClientModule, 20, 0);
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, bridge.Requests[0].Module);
    }

    [Fact]
    public void TheSettleWindowDoesNotHoldTheFirstFollowOfASession()
    {
        // Starting a session already in a different module from the host is not a transition,
        // and making the player wait for one that never happened would look like the follow
        // was broken.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(settleMs: 60_000);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, bridge.Requests[0].Module);
    }

    [Fact]
    public void ADifferentTargetBypassesTheDebounce()
    {
        // The host changed its mind mid-flight (double transition). The new destination
        // must not wait out the previous request's retry window.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        session.Peers.Clear();
        session.Peers.Add(HostPeer("dan_m13aa"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("dan_m13aa", bridge.Requests[1].Module);
    }
}
