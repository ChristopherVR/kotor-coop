using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The receiving half of docs/research/dialogue-consequences.md §3: a
/// <see cref="DialogueGrantPacket"/> off the wire is queued into the SAME single-slot
/// give-loot queue <see cref="MaterialisedLootQueueTests"/> already covers, tagged with the
/// well-known local-player sentinel rather than a translated owner-enemy id — the shape the
/// design doc's own steer named as the thing to reuse.
/// </summary>
public class DialogueGrantQueueTests
{
    private const string ClientModule = "end_m01aa";

    /// <summary>Mirrors <c>SharedBlock.CheatTargetLocalPlayer</c> / <c>CoopAgent.LocalPlayerObjectId</c>
    /// by value — the same cross-project agreement those two constants document.</summary>
    private const uint LocalPlayerObjectId = 0x7FFFFFFF;

    private sealed class MinimalBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        public string Description => "dialogue-grant-queue-test";

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

        // combatPumpMs: 0 forces the materialised-loot pump (which the dialogue-grant drain
        // rides alongside, same cadence) to run on every PumpOnce call.
        CoopAgent agent = new(bridge, session, new AgentSettings { CombatPumpIntervalMs = 0 });

        return (agent, session);
    }

    private static MaterialisedLootItem Item(string resref, uint stack = 1) =>
        new(resref, new ItemIdentity((ushort)stack, Charges: 0, MaxCharges: 0, Upgrades: 0, Flags: 0));

    private static DialogueGrantPacket Packet(uint originPeerId, params MaterialisedLootItem[] items) =>
        new(originPeerId, OpSequence: 1, "carth", items);

    [Fact]
    public void NothingQueuedUntilAGrantArrives()
    {
        (CoopAgent agent, StubSession session) = Build();

        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    /// <summary>The whole point of the sentinel: a dialogue-grant give never needs the
    /// materialised-enemy translation, because it always names THIS instance's own player.</summary>
    [Fact]
    public void EveryItemOnAGrantIsQueuedUnderTheLocalPlayerSentinel()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(7, Item("g_w_blstrpstl001"), Item("g_i_medeqpmnt01")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal(LocalPlayerObjectId, first.TargetObjectId);
        Assert.Equal("g_w_blstrpstl001", first.Item.Resref);

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal(LocalPlayerObjectId, second.TargetObjectId);
        Assert.Equal("g_i_medeqpmnt01", second.Item.Resref);

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    [Fact]
    public void QueueDrainsFirstInFirstOutAcrossGrants()
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

    [Fact]
    public void DequeueingLessThanQueuedLeavesTheRestForNextTime()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(1, Item("a"), Item("b"), Item("c")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive first));
        Assert.Equal("a", first.Item.Resref);

        agent.PumpOnce(); // a whole other tick, nothing new arrives - "b" and "c" still owed

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive second));
        Assert.Equal("b", second.Item.Resref);
        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive third));
        Assert.Equal("c", third.Item.Resref);
        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    /// <summary>Identity fields ride along uninterpreted, exactly like a materialised-loot
    /// give — queueing does not care what happens downstream, only the plugin's cheat channel
    /// does.</summary>
    [Fact]
    public void FullIdentityFieldsSurviveTheQueue()
    {
        (CoopAgent agent, StubSession session) = Build();

        MaterialisedLootItem item = new(
            "g_w_lghtsbr01", new ItemIdentity(1, Charges: 3, MaxCharges: 50, Upgrades: 0x5, Flags: 0x3));
        session.Incoming.Add(Packet(9, item));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(LocalPlayerObjectId, give.TargetObjectId);
        Assert.Equal(3, give.Item.Identity.Charges);
        Assert.Equal(50, give.Item.Identity.MaxCharges);
        Assert.Equal(0x5u, give.Item.Identity.Upgrades);
        Assert.Equal(0x3, give.Item.Identity.Flags);
    }

    /// <summary>The queue is shared with materialised loot; a burst from both channels in the
    /// same tick drains together, first-in-first-out, exactly like two materialised-loot
    /// packets would.</summary>
    [Fact]
    public void SharesTheSameQueueAsMaterialisedLoot()
    {
        (CoopAgent agent, StubSession session) = Build();

        session.Incoming.Add(Packet(1, Item("dialogue-item")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(LocalPlayerObjectId, give.TargetObjectId);
        Assert.Equal("dialogue-item", give.Item.Resref);
    }

    /// <summary>A stuck drain must not grow the shared queue without bound.</summary>
    [Fact]
    public void QueueIsBoundedRatherThanGrowingForever()
    {
        (CoopAgent agent, StubSession session) = Build();

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
}
