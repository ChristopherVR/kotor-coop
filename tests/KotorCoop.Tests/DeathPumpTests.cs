using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The co-op death rules at the pump level: who gets revived, and when a party wipe is
/// allowed to end the game. The engine half (the two byte-patch suppressions, the
/// Resurrection effect) lives in the plugin and can only be exercised live; what is testable
/// here is the decision, and the decision is the part that must never be wrong — a bad
/// verdict either strands a player at the main menu mid-session or revives someone who
/// should have stayed down.
/// </summary>
public class DeathPumpTests
{
    private sealed class DeathBridge : IGameBridge
    {
        /// <summary>Every suppression change, as (suppressGameOver, suppressSwap).</summary>
        public List<(bool GameOver, bool Swap)> Suppressions { get; } = [];

        public List<int> Revives { get; } = [];

        public bool GameOverAttempted { get; set; }

        public PlayerState Local { get; set; } =
            new(Vector3f.Zero, 0f, "end_m01aa", 20, 0);

        public string Description => "death-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsDeathHandling => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap)
        {
            Suppressions.Add((suppressGameOver, suppressSwap));
            return true;
        }

        public bool TryTakeGameOverAttempt()
        {
            bool attempted = GameOverAttempted;
            GameOverAttempted = false;
            return attempted;
        }

        public bool RequestRevive(int hitPoints = 0)
        {
            Revives.Add(hitPoints);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class DeathSession : ICoopSession
    {
        public List<RemotePeer> Peers { get; } = [];

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(IReadOnlyList<PartyMemberState> members) { }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

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

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public bool IsEnemyAuthority => false;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(uint flagId, sbyte value) { }

        public uint DropItem(string resref, string module, Vector3f position) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies() => [];

        public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemyReports(string module) => [];

        public void SetJournalEntry(string questTag, int state) { }

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }
    }

    private static PlayerState State(Vector3f position, bool downed = false, string module = "end_m01aa") =>
        new(position, 0f, module, downed ? 0 : 20, 0, Flags: downed ? PlayerState.FlagDowned : 0u);

    private static RemotePeer Peer(uint id, Vector3f position, bool downed = false, string module = "end_m01aa") =>
        new(id, $"peer{id}", State(position, downed, module));

    private static (DeathBridge Bridge, DeathSession Session, CoopAgent Agent) Rig()
    {
        DeathBridge bridge = new();
        DeathSession session = new();
        return (bridge, session, new CoopAgent(bridge, session, new AgentSettings()));
    }

    [Fact]
    public void SuppressionIsAssertedOnceWhileEveryoneIsAlive()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();
        session.Peers.Add(Peer(1, new Vector3f(10f, 0f, 0f)));

        agent.PumpOnce();
        agent.PumpOnce();

        // Edge-triggered, and while anyone still stands the game-over stays held.
        Assert.Equal([(true, true)], bridge.Suppressions);
    }

    [Fact]
    public void ANearbyStandingPeerRevivesADownedPlayer()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, new Vector3f(2f, 0f, 0f)));

        agent.PumpOnce();

        Assert.Single(bridge.Revives);
        Assert.True(bridge.Revives[0] > 0);
    }

    [Fact]
    public void ADistantPeerDoesNotRevive()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, new Vector3f(40f, 0f, 0f)));

        agent.PumpOnce();

        Assert.Empty(bridge.Revives);
    }

    [Fact]
    public void ADownedPeerCannotRevive()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, new Vector3f(1f, 0f, 0f), downed: true));

        agent.PumpOnce();

        Assert.Empty(bridge.Revives);
    }

    [Fact]
    public void ReviveIsRequestedOnlyOncePerKnockDown()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, new Vector3f(1f, 0f, 0f)));

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(bridge.Revives);

        // Back on their feet and downed again: a second rescue is allowed.
        bridge.Local = State(Vector3f.Zero);
        agent.PumpOnce();
        bridge.Local = State(Vector3f.Zero, downed: true);
        agent.PumpOnce();

        Assert.Equal(2, bridge.Revives.Count);
    }

    [Fact]
    public void GameOverIsReleasedOnlyWhenEveryPlayerIsDown()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, new Vector3f(40f, 0f, 0f)));
        agent.PumpOnce();

        // A peer still stands: the wipe is not unanimous, so the screen stays held.
        Assert.Equal([(true, true)], bridge.Suppressions);

        session.Peers[0] = Peer(1, new Vector3f(40f, 0f, 0f), downed: true);
        agent.PumpOnce();

        // Now everyone is down: the suppression lifts so both games end together.
        Assert.Equal([(true, true), (false, true)], bridge.Suppressions);
    }

    [Fact]
    public void APeerInAnotherModuleCountsAsStanding()
    {
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        // Downed here, and the only peer is elsewhere in the world — they can still walk
        // back and help, so this is not a session wipe.
        bridge.Local = State(Vector3f.Zero, downed: true);
        session.Peers.Add(Peer(1, Vector3f.Zero, module: "end_m01ab"));

        agent.PumpOnce();

        Assert.Equal([(true, true)], bridge.Suppressions);
        Assert.Empty(bridge.Revives);
    }

    [Fact]
    public void ASoloSessionNeverReleasesTheGameOver()
    {
        // No peers at all (everyone else disconnected): a lone player's party wipe must not
        // be treated as a session-wide wipe decided by an empty peer list.
        (DeathBridge bridge, DeathSession session, CoopAgent agent) = Rig();

        bridge.Local = State(Vector3f.Zero, downed: true);
        agent.PumpOnce();

        Assert.Equal([(true, true)], bridge.Suppressions);
    }

    [Fact]
    public void ABridgeWithoutDeathSupportIsLeftAlone()
    {
        // A profile bridge (no plugin) must not have death mediation attempted on it.
        DeathSession session = new();
        NoDeathBridge bridge = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        agent.PumpOnce();

        Assert.False(bridge.Touched);
    }

    private sealed class NoDeathBridge : IGameBridge
    {
        public bool Touched { get; private set; }

        public string Description => "no-death";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 0, 0, Flags: PlayerState.FlagDowned);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool SetDeathSuppression(bool suppressGameOver, bool suppressSwap)
        {
            Touched = true;
            return true;
        }

        public bool RequestRevive(int hitPoints = 0)
        {
            Touched = true;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
