using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end (through a real <see cref="CoopAgent"/> tick, the same shape
/// <see cref="DialogueGrantQueueTests"/> already uses) coverage of
/// docs/research/class-and-power-grants.md §10's wiring: <c>LootPump</c> applying
/// <see cref="FirstLightsaberColorRule"/> to an incoming Zhar saber-construction grant, behind
/// <see cref="AgentSettings.FirstSaberColorSync"/>.
/// </summary>
public class FirstLightsaberColorSyncPumpTests
{
    private const string ClientModule = "danm13";
    private const uint LocalPlayerObjectId = 0x7FFFFFFF;

    private sealed class ClassAwareBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        /// <summary>What this player's own <c>classes.2da</c> rows currently are.</summary>
        public List<int> ClassIds { get; set; } = [];

        /// <summary>What this player's own possessed items already look like (the "already
        /// has a first saber" check reads this).</summary>
        public List<(string Resref, ItemIdentity Identity)> Possessed { get; set; } = [];

        public string Description => "first-saber-color-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public IReadOnlyList<int> ReadLocalClassIds() => ClassIds;

        public IReadOnlyList<(uint Possessor, string Resref, ItemIdentity Identity)> ReadPersonalPossessedItems(
            IReadOnlySet<uint> possessorIds) =>
            [.. Possessed.Select(p => (LocalPlayerObjectId, p.Resref, p.Identity))];

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        public List<DialogueGrantPacket> Incoming { get; } = [];

        public IReadOnlyList<DialogueGrantPacket> DrainDialogueGrants()
        {
            List<DialogueGrantPacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => false;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

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

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, ClassAwareBridge Bridge, StubSession Session) Build(bool firstSaberColorSync)
    {
        ClassAwareBridge bridge = new();
        StubSession session = new();

        CoopAgent agent = new(
            bridge, session,
            new AgentSettings { CombatPumpIntervalMs = 0, FirstSaberColorSync = firstSaberColorSync });

        return (agent, bridge, session);
    }

    private static DialogueGrantPacket SaberGrant(string resref) =>
        new(OriginPeerId: 1, OpSequence: 1, "zhar",
            [new MaterialisedLootItem(resref, new ItemIdentity(1, 0, 0, 0, 0))]);

    [Fact]
    public void OffByDefaultLeavesTheGrantExactlyAsAnnounced()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: false);
        bridge.ClassIds = [5]; // Sentinel - would want yellow

        session.Incoming.Add(SaberGrant("g_w_lghtsbr01")); // builder built blue
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal("g_w_lghtsbr01", give.Item.Resref); // unchanged - the toggle is off
    }

    [Fact]
    public void SubstitutesTheReceiversOwnClassColourWhenItDiffersFromTheBuilders()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5]; // Sentinel

        session.Incoming.Add(SaberGrant("g_w_lghtsbr01")); // builder built blue (Guardian)
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal("g_w_lghtsbr04", give.Item.Resref); // Sentinel's own colour, not blue
    }

    [Fact]
    public void PassesThroughUnchangedWhenTheReceiversOwnClassAlreadyMatches()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [3]; // Guardian

        session.Incoming.Add(SaberGrant("g_w_lghtsbr01")); // already the Guardian colour
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal("g_w_lghtsbr01", give.Item.Resref);
    }

    [Fact]
    public void DropsTheGiveEntirelyWhenTheReceiverAlreadyHasAFirstSaber()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];
        bridge.Possessed = [("g_w_lghtsbr04", new ItemIdentity(1, 0, 0, 0, 0))];

        session.Incoming.Add(SaberGrant("g_w_lghtsbr01"));
        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    [Fact]
    public void AnUnrelatedItemInTheSameGrantIsNeverTouched()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];

        session.Incoming.Add(new DialogueGrantPacket(
            OriginPeerId: 1, OpSequence: 1, "zhar",
            [
                new MaterialisedLootItem("g_w_lghtsbr01", new ItemIdentity(1, 0, 0, 0, 0)),
                new MaterialisedLootItem("g_i_medeqpmnt01", new ItemIdentity(1, 0, 0, 0, 0)),
            ]));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal("g_w_lghtsbr04", first.Item.Resref);

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal("g_i_medeqpmnt01", second.Item.Resref);
    }

    [Fact]
    public void ARepeatedGrantAfterTheFirstAppliedOneIsSkippedNotDoubled()
    {
        (CoopAgent agent, ClassAwareBridge bridge, StubSession session) = Build(firstSaberColorSync: true);
        bridge.ClassIds = [5];

        session.Incoming.Add(SaberGrant("g_w_lghtsbr01"));
        agent.PumpOnce();
        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal("g_w_lghtsbr04", give.Item.Resref);

        // Simulate the give having actually landed on this player's own engine (a real rejoin/
        // resync would re-read this from the live inventory).
        bridge.Possessed = [("g_w_lghtsbr04", new ItemIdentity(1, 0, 0, 0, 0))];

        // A resend of the SAME grant (resync, retransmit, late join backlog).
        session.Incoming.Add(SaberGrant("g_w_lghtsbr01"));
        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }
}
