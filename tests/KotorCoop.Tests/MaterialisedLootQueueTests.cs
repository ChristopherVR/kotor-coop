using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The receiving half of docs/research/enemy-materialisation.md §6's still-open loot-sync
/// gap: a client's <see cref="CoopAgent"/> drains materialised-loot packets off the wire into
/// a local queue, one give-request at a time, so whatever already arbitrates the single-slot
/// staging-cheat channel (a human's typed <c>giveloot</c> shares it) can submit them without
/// ever filing two requests at once. Pure decision logic — actually applying an item to the
/// game needs the plugin, which cannot run outside it.
/// </summary>
public class MaterialisedLootQueueTests
{
    private const string ClientModule = "end_m01aa";

    private sealed class MinimalBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        public string Description => "materialised-loot-queue-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        /// <summary>Packets waiting to be handed to the agent, as if they had just arrived
        /// off the wire.</summary>
        public List<MaterialisedLootPacket> Incoming { get; } = [];

        public IReadOnlyList<MaterialisedLootPacket> DrainMaterialisedLoot()
        {
            List<MaterialisedLootPacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

        /// <summary>How many times the agent has told this session to forget its dedup set -
        /// should be exactly once per genuine module change, never once per pump.</summary>
        public int ResetDedupCallCount { get; private set; }

        public void ResetMaterialisedLootDedup() => ResetDedupCallCount++;

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

    private static (CoopAgent Agent, StubSession Session) Build()
    {
        MinimalBridge bridge = new();
        StubSession session = new();

        // combatPumpMs: 0 forces the materialised-loot pump (which rides the combat cadence,
        // same as PumpEnemies/PumpPhantomDamage) to run on every PumpOnce call, so a test does
        // not need to wait out a real wall clock.
        CoopAgent agent = new(bridge, session, new AgentSettings { CombatPumpIntervalMs = 0 });

        return (agent, session);
    }

    private static MaterialisedLootItem Item(string resref, uint stack = 1) =>
        new(resref, new ItemIdentity((ushort)stack, Charges: 0, MaxCharges: 0, Upgrades: 0, Flags: 0));

    private static MaterialisedLootPacket Packet(uint ownerEnemyId, params MaterialisedLootItem[] items) =>
        new(OriginPeerId: 0, OpSequence: 0, ownerEnemyId, items);

    [Fact]
    public void NothingQueuedUntilAPacketArrives()
    {
        (CoopAgent agent, StubSession session) = Build();

        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    [Fact]
    public void EveryItemOnAPacketIsQueued()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(0xE, Item("g_w_blstrpstl001"), Item("g_i_medeqpmnt01")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal(0xEu, first.TargetObjectId);
        Assert.Equal("g_w_blstrpstl001", first.Item.Resref);

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal("g_i_medeqpmnt01", second.Item.Resref);

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    [Fact]
    public void QueueDrainsFirstInFirstOutAcrossPackets()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(1, Item("a")));
        session.Incoming.Add(Packet(2, Item("b")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal("a", first.Item.Resref);
        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal("b", second.Item.Resref);
    }

    /// <summary>The whole point of queueing rather than applying inline: a burst arriving in
    /// one pump does not have to drain in one pump. Nothing is lost between calls.</summary>
    [Fact]
    public void DequeueingLessThanQueuedLeavesTheRestForNextTime()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(1, Item("a"), Item("b"), Item("c")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal("a", first.Item.Resref);

        // A whole other pump tick passes (nothing new arrives) - "b" and "c" are still owed.
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal("b", second.Item.Resref);
        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive third));
        Assert.Equal("c", third.Item.Resref);
        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    /// <summary>A stuck drain (or a malformed peer) must not grow the queue without bound -
    /// the excess is dropped, loudly, not silently accumulated forever.</summary>
    [Fact]
    public void QueueIsBoundedRatherThanGrowingForever()
    {
        (CoopAgent agent, StubSession session) = Build();

        // Flood far past any real encounter's loot count in one packet.
        MaterialisedLootItem[] flood = [.. Enumerable.Range(0, 500).Select(i => Item($"item{i}"))];
        session.Incoming.Add(Packet(1, flood));
        agent.PumpOnce();

        int drained = 0;
        while (agent.TryDequeuePendingLoot(out _))
        {
            drained++;
        }

        Assert.True(drained > 0);
        Assert.True(drained < flood.Length, "the flood should have been capped, not fully queued");
    }

    /// <summary>Identity fields ride along uninterpreted - queueing does not know or care
    /// whether the target is a real materialised stand-in; only the plugin's translation
    /// (MaterialisedEnemies.ToLocal) can answer that, and it runs later, in-game.</summary>
    [Fact]
    public void FullIdentityFieldsSurviveTheQueue()
    {
        (CoopAgent agent, StubSession session) = Build();

        MaterialisedLootItem item = new(
            "g_w_lghtsbr01", new ItemIdentity(1, Charges: 3, MaxCharges: 50, Upgrades: 0x5, Flags: 0x3));
        session.Incoming.Add(Packet(0x35, item));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(0x35u, give.TargetObjectId);
        Assert.Equal(3, give.Item.Identity.Charges);
        Assert.Equal(50, give.Item.Identity.MaxCharges);
        Assert.Equal(0x5u, give.Item.Identity.Upgrades);
        Assert.Equal(0x3, give.Item.Identity.Flags);
    }

    // ---- item 11(a): the client's dedup set must be reset on a module change ------------

    /// <summary>docs/research/enemy-materialisation.md §6: owner enemy ids are reassigned per
    /// module load, so the client's dedup set (keyed on owner enemy id) has to be told to
    /// forget whenever the local module changes - otherwise an id reused in the next module is
    /// silently treated as "already seen" and that enemy's real loot never reaches the
    /// queue.</summary>
    [Fact]
    public void ModuleChangeTellsTheSessionToForgetItsDedupSet()
    {
        (CoopAgent agent, StubSession session) = Build();

        agent.PumpOnce(); // arriving at ClientModule for the first time - one reset to seed it
        Assert.Equal(1, session.ResetDedupCallCount);

        agent.PumpOnce(); // still ClientModule - no further reset
        agent.PumpOnce();
        Assert.Equal(1, session.ResetDedupCallCount);

        MinimalBridge bridge = new() { Local = new PlayerState(Vector3f.Zero, 0f, "tar_m03aa", 20, 0) };
        CoopAgent movedAgent = new(bridge, session, new AgentSettings { CombatPumpIntervalMs = 0 });
        movedAgent.PumpOnce(); // a genuine module change - reset again

        Assert.Equal(2, session.ResetDedupCallCount);
    }

    /// <summary>A transition-window fallback label ("areaXXXXXXXX") must never fire a reset -
    /// it is not a real module, and resetting on every flicker through it would be pure churn,
    /// same reasoning as every other IsFallbackModuleName gate in CoopAgent.</summary>
    [Fact]
    public void AFallbackModuleLabelDoesNotTriggerAReset()
    {
        MinimalBridge bridge = new() { Local = new PlayerState(Vector3f.Zero, 0f, "area1234", 20, 0) };
        StubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings { CombatPumpIntervalMs = 0 });

        agent.PumpOnce();

        Assert.Equal(0, session.ResetDedupCallCount);
    }
}
