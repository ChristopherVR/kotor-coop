using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The journal pump at the agent level: local advances are announced to the session, the
/// session's entries are offered to the bridge — and the local read always comes FIRST,
/// because the bridge's shadow must be primed from the game before store entries are
/// judged against it. The engine half (SetState, the plugin's already-current skip) lives
/// in the plugin and can only be exercised live.
/// </summary>
public class JournalPumpTests
{
    private sealed class JournalBridge : IGameBridge
    {
        public List<string> Calls { get; } = [];

        public List<KeyValuePair<string, int>> LocalAdvances { get; set; } = [];

        public List<(string Tag, int State)> OfferedApplies { get; } = [];

        public string Description => "journal-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsJournalSync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<KeyValuePair<string, int>> CollectChangedJournal()
        {
            Calls.Add("collect");
            List<KeyValuePair<string, int>> advances = LocalAdvances;
            LocalAdvances = [];
            return advances;
        }

        public bool TryApplyJournalEntry(string questTag, int state)
        {
            Calls.Add("apply");
            OfferedApplies.Add((questTag, state));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class JournalSession : ICoopSession
    {
        public List<(string Tag, int State)> Announced { get; } = [];

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
            int nodeIndex = -1, uint nodeSerial = 0) { }

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

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

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

        public void SetJournalEntry(string questTag, int state)
        {
            Announced.Add((questTag, state));
            Journal.Merge(questTag, state);
        }

        public SessionCapabilities Capabilities => SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void TheLocalJournalIsReadBeforeStoreEntriesAreOffered()
    {
        // The whole join fix rests on this ordering: the bridge primes its shadow from
        // the collect, and only then judges the store's entries against it. Applying
        // first would judge against nothing and replay the store through the engine.
        JournalBridge bridge = new();
        JournalSession session = new();
        session.Journal.Merge("end_attack", 20);

        CoopAgent agent = new(bridge, session, new AgentSettings());
        agent.PumpOnce();

        Assert.Equal("collect", bridge.Calls.First());
        Assert.Contains("apply", bridge.Calls);
        int lastCollect = bridge.Calls.LastIndexOf("collect");
        int firstApply = bridge.Calls.IndexOf("apply");
        Assert.True(lastCollect < firstApply, "every collect must precede the first apply within a pump");
    }

    [Fact]
    public void LocalAdvancesAreAnnouncedToTheSession()
    {
        JournalBridge bridge = new();
        JournalSession session = new();
        bridge.LocalAdvances = [new("k_swg_bastila", 10)];

        CoopAgent agent = new(bridge, session, new AgentSettings());
        agent.PumpOnce();

        Assert.Contains(("k_swg_bastila", 10), session.Announced);
        Assert.Contains(new KeyValuePair<string, int>("k_swg_bastila", 10), session.Journal.Snapshot());
    }

    [Fact]
    public void EveryStoreEntryIsOfferedAndTheBridgeDecides()
    {
        // The pump deliberately offers the whole store each heavy tick; idempotence is
        // the bridge's job (the shadow), not the pump's. This pins the seam so a future
        // "optimisation" in the pump does not silently strand a quest.
        JournalBridge bridge = new();
        JournalSession session = new();
        session.Journal.Merge("quest_a", 10);
        session.Journal.Merge("quest_b", 20);

        CoopAgent agent = new(bridge, session, new AgentSettings());
        agent.PumpOnce();

        Assert.Contains(("quest_a", 10), bridge.OfferedApplies);
        Assert.Contains(("quest_b", 20), bridge.OfferedApplies);
    }
}
