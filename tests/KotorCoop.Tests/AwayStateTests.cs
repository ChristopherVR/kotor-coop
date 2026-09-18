using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The away transition end to end through CoopAgent.PumpOnce: a sustained run of failed local
/// reads (not a single blip) publishes the last known state with PlayerState.FlagAway set and
/// keeps republishing it every tick, an away PEER is filtered out of what gets written into
/// this game (the same "simply absent" idiom PlayerState.FlagInMinigame already uses), and a
/// resumed read clears the flag on the very next successful tick. See
/// docs/research/disconnect-reconnect.md.
/// </summary>
public class AwayStateTests
{
    private sealed class AwayBridge : IGameBridge
    {
        public bool ReadShouldSucceed = true;

        public string Description => "away-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public List<RemotePeer> LastWrittenPeers { get; } = [];

        public bool TryReadLocalState(out PlayerState state)
        {
            if (ReadShouldSucceed)
            {
                state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
                return true;
            }

            state = default;
            return false;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
        {
            LastWrittenPeers.Clear();
            LastWrittenPeers.AddRange(peers);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class AwaySession : ICoopSession
    {
        public List<RemotePeer> Peers { get; } = [];

        public List<PlayerState> PublishedStates { get; } = [];

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
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

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

        public void PublishLocalState(in PlayerState state) => PublishedStates.Add(state);

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

        public void SetFlag(ulong flagId, sbyte value)
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

        public void SetJournalEntry(string questTag, int state)
        {
        }

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static RemotePeer AwayPeer(uint id) => new(
        id, $"peer{id}", new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0, Flags: PlayerState.FlagAway));

    private static RemotePeer PresentPeer(uint id) =>
        new(id, $"peer{id}", new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0));

    [Fact]
    public void ABriefReadBlipDoesNotPublishAwayOrFlipIsAway()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        agent.PumpOnce(); // primes LastLocalState with a real read
        session.PublishedStates.Clear();

        bridge.ReadShouldSucceed = false;
        agent.PumpOnce(); // one bad tick, well under AwayRule's ~1s threshold

        Assert.False(agent.IsAway);
        Assert.Empty(session.PublishedStates);
    }

    /// <summary>
    /// Models the fixed real bridge's contract for the 2026-08-27 FlagAway live failure
    /// (docs/research/disconnect-reconnect.md §10): before the fix, TryReadLocalState
    /// "succeeded" against a transitional-named ghost creature reacquired right after a quit to
    /// the main menu, so this consecutive-failure counter never advanced. After the fix
    /// (KotorGameBridge.Player.cs, gated on the shared KotorGameBridge.IsTransitionalModuleName —
    /// see TransitionalModuleNameTests for that check in isolation), a ghost read is exactly a
    /// failed TryReadLocalState from CoopAgent's point of view, which is what AwayBridge with
    /// ReadShouldSucceed = false models here. One such tick, well under AwayRule's ~1s
    /// threshold, must not flip IsAway — a ghost seen for a single tick is indistinguishable
    /// from any other transient blip and must recover silently.
    /// </summary>
    [Fact]
    public void AGhostCreatureReadCountsAsAFailureButOneTickAloneDoesNotTripAway()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        agent.PumpOnce(); // primes LastLocalState with a real read
        session.PublishedStates.Clear();

        bridge.ReadShouldSucceed = false; // stands in for one ghost-shaped failed read
        agent.PumpOnce();

        Assert.False(agent.IsAway);
        Assert.Empty(session.PublishedStates);
    }

    /// <summary>
    /// An ordinary module transition also makes TryReadLocalState's module read momentarily
    /// fall back to a transitional "areaXX" name (the very same shape a genuine ghost creature
    /// has) before the new module settles — that is WHY IsTransitionalModuleName is shared
    /// between the encounter-disarm path and TryReadLocalState rather than only ever firing off
    /// a quit-to-menu. A real transition settles in well under AwayRule's ~1s threshold, so a
    /// burst of failures shorter than the threshold, followed by a resumed read, must never
    /// latch Away — exactly the "briefly stalled" case the whole design exists to distinguish
    /// from "quit twenty minutes ago".
    /// </summary>
    [Fact]
    public void ANormalTransitionUnderOneSecondDoesNotTripAway()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        AgentSettings settings = new();
        CoopAgent agent = new(bridge, session, settings);

        agent.PumpOnce(); // primes LastLocalState

        bridge.ReadShouldSucceed = false;
        for (int i = 0; i < AwayRule.Threshold(settings.PollRate) - 1; i++)
        {
            agent.PumpOnce(); // the transition's own transitional-name window
        }

        Assert.False(agent.IsAway);

        bridge.ReadShouldSucceed = true; // the new module settles
        session.PublishedStates.Clear();
        agent.PumpOnce();

        Assert.False(agent.IsAway);
        PlayerState published = Assert.Single(session.PublishedStates);
        Assert.False(published.IsAway);
    }

    /// <summary>Same shape as ASustainedReadDroughtPublishesAwayAndKeepsRepublishingIt, framed
    /// explicitly for the ghost-creature case this section documents: a player who quit to the
    /// main menu and stayed there produces a ghost-shaped failed read every tick, forever, not
    /// just for one transition-sized burst — and that must trip and hold Away.</summary>
    [Fact]
    public void ASustainedGhostCreatureReadTripsAway()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        AgentSettings settings = new();
        CoopAgent agent = new(bridge, session, settings);

        agent.PumpOnce();

        bridge.ReadShouldSucceed = false; // the ghost never resolves back to the real player
        for (int i = 0; i < AwayRule.Threshold(settings.PollRate); i++)
        {
            agent.PumpOnce();
        }

        Assert.True(agent.IsAway);
    }

    [Fact]
    public void ASustainedReadDroughtPublishesAwayAndKeepsRepublishingIt()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        AgentSettings settings = new(); // PollRate = ProtocolConstants.DefaultTickRate (30)
        CoopAgent agent = new(bridge, session, settings);

        agent.PumpOnce(); // primes LastLocalState

        bridge.ReadShouldSucceed = false;
        for (int i = 0; i < AwayRule.Threshold(settings.PollRate) - 1; i++)
        {
            agent.PumpOnce();
        }

        Assert.False(agent.IsAway); // still one short

        session.PublishedStates.Clear();
        agent.PumpOnce(); // crosses the threshold

        Assert.True(agent.IsAway);
        PlayerState published = Assert.Single(session.PublishedStates);
        Assert.True(published.IsAway);

        // Keeps republishing on every subsequent tick, not just the crossing one — peers must
        // keep hearing from an away player, not merely once.
        session.PublishedStates.Clear();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(2, session.PublishedStates.Count);
        Assert.All(session.PublishedStates, s => Assert.True(s.IsAway));
    }

    [Fact]
    public void AResumedReadClearsAwayOnTheVeryNextTick()
    {
        AwayBridge bridge = new();
        AwaySession session = new();
        AgentSettings settings = new();
        CoopAgent agent = new(bridge, session, settings);

        agent.PumpOnce();

        bridge.ReadShouldSucceed = false;
        for (int i = 0; i < AwayRule.Threshold(settings.PollRate); i++)
        {
            agent.PumpOnce();
        }

        Assert.True(agent.IsAway);

        bridge.ReadShouldSucceed = true;
        session.PublishedStates.Clear();
        agent.PumpOnce();

        Assert.False(agent.IsAway);
        PlayerState published = Assert.Single(session.PublishedStates);
        Assert.False(published.IsAway);
    }

    [Fact]
    public void AnAwayPeerIsOmittedFromWhatGetsWrittenIntoThisGame()
    {
        // The same "simply absent from TryWriteRemotePeers" idiom PlayerState.FlagInMinigame
        // already uses — see CoopAgent.PumpOnce's peer-visibility filter.
        AwayBridge bridge = new();
        AwaySession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        session.Peers.Add(AwayPeer(3));
        session.Peers.Add(PresentPeer(4));

        agent.PumpOnce();

        Assert.DoesNotContain(bridge.LastWrittenPeers, p => p.PeerId == 3);
        Assert.Contains(bridge.LastWrittenPeers, p => p.PeerId == 4);
    }
}
