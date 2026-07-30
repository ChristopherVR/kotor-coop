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

        public void PublishEnemies(IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies() => [];

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, TravelBridge Bridge, StubSession Session) Build(
        bool follow = true, int retryMs = 15_000)
    {
        TravelBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            FollowHostModule = follow,
            FollowRetryMs = retryMs,
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
