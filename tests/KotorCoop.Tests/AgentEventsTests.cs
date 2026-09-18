using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// CoopAgent's typed event surface (KotorCoop.Session.Events): ModuleChanged/WorldAdopted,
/// raised from the same module-settle bookkeeping PumpOnce already runs, and
/// PeerJoined/PeerLeft, raised from a diff over the peer set PumpOnce already reads. These
/// are purely additive — nothing about the underlying pump behaviour is exercised here beyond
/// what the existing *PumpTests already cover.
/// </summary>
public class AgentEventsTests
{
    private sealed class EventsBridge : IGameBridge
    {
        public string Module = "end_m01aa";

        public string Description => "events-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void Dispose()
        {
        }
    }

    private sealed class EventsSession : ICoopSession
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

        public void PublishLocalState(in PlayerState state)
        {
        }

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

    private static RemotePeer Peer(uint id) =>
        new(id, $"peer{id}", new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0));

    [Fact]
    public void FirstModuleEverSeenRaisesWorldAdoptedNotModuleChanged()
    {
        EventsBridge bridge = new();
        EventsSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        List<string> worldAdopted = [];
        List<(string Previous, string Current)> moduleChanged = [];
        agent.WorldAdopted += module => worldAdopted.Add(module);
        agent.ModuleChanged += (previous, current) => moduleChanged.Add((previous, current));

        agent.PumpOnce();

        Assert.Equal(["end_m01aa"], worldAdopted);
        Assert.Empty(moduleChanged);
    }

    [Fact]
    public void ALaterGenuineModuleChangeRaisesModuleChangedOnceNotWorldAdopted()
    {
        EventsBridge bridge = new();
        EventsSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        List<string> worldAdopted = [];
        List<(string Previous, string Current)> moduleChanged = [];
        agent.WorldAdopted += module => worldAdopted.Add(module);
        agent.ModuleChanged += (previous, current) => moduleChanged.Add((previous, current));

        agent.PumpOnce(); // adopts end_m01aa

        bridge.Module = "tar_m02aa";
        agent.PumpOnce(); // genuine transition

        // Pumping again with the same module must not re-raise anything.
        agent.PumpOnce();

        Assert.Equal(["end_m01aa"], worldAdopted);
        Assert.Equal([("end_m01aa", "tar_m02aa")], moduleChanged);
    }

    [Fact]
    public void APeerAppearingRaisesPeerJoinedOnce()
    {
        EventsBridge bridge = new();
        EventsSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        List<uint> joined = [];
        agent.PeerJoined += id => joined.Add(id);

        session.Peers.Add(Peer(1));
        agent.PumpOnce();
        agent.PumpOnce(); // still there: must not re-fire

        Assert.Equal([1u], joined);
    }

    [Fact]
    public void APeerDisappearingRaisesPeerLeftOnce()
    {
        EventsBridge bridge = new();
        EventsSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        List<uint> left = [];
        agent.PeerLeft += id => left.Add(id);

        session.Peers.Add(Peer(1));
        agent.PumpOnce();

        session.Peers.Clear();
        agent.PumpOnce();
        agent.PumpOnce(); // still gone: must not re-fire

        Assert.Equal([1u], left);
    }

    [Fact]
    public void LocalStateReadFiresOnEverySuccessfulRead()
    {
        EventsBridge bridge = new();
        EventsSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings());

        List<PlayerState> reads = [];
        agent.LocalStateRead += state => reads.Add(state);

        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Equal(2, reads.Count);
        Assert.All(reads, s => Assert.Equal("end_m01aa", s.Module));
    }
}
