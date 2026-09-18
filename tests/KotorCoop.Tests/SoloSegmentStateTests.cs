using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The solo-segment peer filter end to end through CoopAgent.PumpOnce: the local flag
/// propagates onto the published PlayerState (so a peer's instance can react), and while it is
/// set every remote peer is dropped from what gets written into THIS game — the "owner filters
/// the peer out" half of docs/research/star-forge-finale.md's design, mirroring
/// AwayStateTests.cs's own end-to-end shape for PlayerState.FlagAway.
/// </summary>
public class SoloSegmentStateTests
{
    private sealed class SoloSegmentBridge : IGameBridge
    {
        public bool LocalInSoloSegment;

        public string Description => "solo-segment-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public List<RemotePeer> LastWrittenPeers { get; } = [];

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(
                Vector3f.Zero, 0f, "sta_m45ac", 20, 0,
                Flags: LocalInSoloSegment ? PlayerState.FlagSoloSegment : 0);
            return true;
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

    private sealed class SoloSegmentSession : ICoopSession
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

    private static RemotePeer PresentPeer(uint id) =>
        new(id, $"peer{id}", new PlayerState(Vector3f.Zero, 0f, "sta_m45ac", 20, 0));

    [Fact]
    public void TheLocalFlagPropagatesOntoThePublishedPlayerState()
    {
        SoloSegmentBridge bridge = new() { LocalInSoloSegment = true };
        SoloSegmentSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { SoloSegmentSpectateEnabled = true });

        agent.PumpOnce();

        PlayerState published = Assert.Single(session.PublishedStates);
        Assert.True(published.IsInSoloSegment);
    }

    [Fact]
    public void OwningAnActiveSegmentDropsEveryPeerFromWhatIsWrittenIntoThisGame()
    {
        SoloSegmentBridge bridge = new() { LocalInSoloSegment = true };
        SoloSegmentSession session = new();
        session.Peers.Add(PresentPeer(3));
        session.Peers.Add(PresentPeer(4));

        CoopAgent agent = new(bridge, session, new AgentSettings { SoloSegmentSpectateEnabled = true });

        agent.PumpOnce();

        Assert.Empty(bridge.LastWrittenPeers);
    }

    [Fact]
    public void NotOwningASegmentWritesPeersNormally()
    {
        SoloSegmentBridge bridge = new() { LocalInSoloSegment = false };
        SoloSegmentSession session = new();
        session.Peers.Add(PresentPeer(3));

        CoopAgent agent = new(bridge, session, new AgentSettings { SoloSegmentSpectateEnabled = true });

        agent.PumpOnce();

        Assert.Contains(bridge.LastWrittenPeers, p => p.PeerId == 3);
    }

    [Fact]
    public void TheToggleOffKeepsWritingPeersEvenWhileTheLocalFlagIsSet()
    {
        // coop.ini solo_segment_spectate = 0 (the default): the local flag can still be
        // published (the plugin has its own independent copy of the same key and, by
        // hypothesis here, has it on) but this agent must not act on it.
        SoloSegmentBridge bridge = new() { LocalInSoloSegment = true };
        SoloSegmentSession session = new();
        session.Peers.Add(PresentPeer(3));

        CoopAgent agent = new(bridge, session, new AgentSettings { SoloSegmentSpectateEnabled = false });

        agent.PumpOnce();

        Assert.Contains(bridge.LastWrittenPeers, p => p.PeerId == 3);
    }
}
