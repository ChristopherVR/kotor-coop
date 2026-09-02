using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
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

        /// <summary>What <see cref="ReadMaterialisedOwnerIds"/> answers - null (the interface's
        /// own default, and every other test file in this suite that does not touch it) unless
        /// a test sets one, so the filtering tests below can drive both the "unsure" and the
        /// "known" halves of MaterialisedLootFilterRule's contract without a real plugin.</summary>
        public IReadOnlySet<uint>? MaterialisedOwnerIds { get; set; }

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public IReadOnlySet<uint>? ReadMaterialisedOwnerIds() => MaterialisedOwnerIds;

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

        /// <summary>Dialogue-grant packets waiting to be handed to the agent - rides the SAME
        /// pending-give queue as materialised loot, but targets the local PC directly and must
        /// never be filtered by MaterialisedLootFilterRule (see that type's own doc).</summary>
        public List<DialogueGrantPacket> IncomingDialogueGrants { get; } = [];

        public IReadOnlyList<DialogueGrantPacket> DrainDialogueGrants()
        {
            List<DialogueGrantPacket> ready = [.. IncomingDialogueGrants];
            IncomingDialogueGrants.Clear();
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
        (CoopAgent agent, _, StubSession session) = BuildWithBridge();
        return (agent, session);
    }

    private static (CoopAgent Agent, MinimalBridge Bridge, StubSession Session) BuildWithBridge()
    {
        MinimalBridge bridge = new();
        StubSession session = new();

        // combatPumpMs: 0 forces the materialised-loot pump (which rides the combat cadence,
        // same as PumpEnemies/PumpPhantomDamage) to run on every PumpOnce call, so a test does
        // not need to wait out a real wall clock.
        CoopAgent agent = new(bridge, session, new AgentSettings { CombatPumpIntervalMs = 0 });

        return (agent, bridge, session);
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

    // ---- 2026-09-02: filter a give whose owner id has no local materialised stand-in, ----
    // ---- BEFORE it costs a giveloot round trip (docs/research/enemy-materialisation.md) --

    /// <summary>The default/pre-existing behaviour, pinned: a bridge that does not answer
    /// <c>ReadMaterialisedOwnerIds</c> (every OTHER test in this file, and any bridge older
    /// than version 65) reads as UNSURE, and an unsure give is always queued - this is the
    /// same shape <see cref="EveryItemOnAPacketIsQueued"/> already exercises, restated here to
    /// pin the "no known table at all" half of the contract explicitly.</summary>
    [Fact]
    public void WithNoKnownMaterialisedTableEveryGiveIsStillQueued()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = BuildWithBridge();
        bridge.MaterialisedOwnerIds = null;

        session.Incoming.Add(Packet(0xE, Item("g_w_blstrpstl001")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(0xEu, give.TargetObjectId);
    }

    /// <summary>The whole point of the feature: a real, if empty, table means this instance has
    /// no local stand-in for ANY owner id right now, so nothing is queued and the single-slot
    /// giveloot channel is never spent on a refusal.</summary>
    [Fact]
    public void AnOwnerIdMissingFromAKnownEmptyTableIsNeverQueued()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = BuildWithBridge();
        bridge.MaterialisedOwnerIds = new HashSet<uint>();

        session.Incoming.Add(Packet(0xE, Item("g_w_blstrpstl001"), Item("g_i_medeqpmnt01")));
        agent.PumpOnce();

        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    /// <summary>The positive case sitting right next to the negative one above: an owner id the
    /// table DOES map is queued exactly as before, even while other ids in the same pump are
    /// filtered out.</summary>
    [Fact]
    public void AnOwnerIdPresentInTheTableIsStillQueued()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = BuildWithBridge();
        bridge.MaterialisedOwnerIds = new HashSet<uint> { 0x35 };

        session.Incoming.Add(Packet(0xE, Item("skipped-owner-not-mapped")));
        session.Incoming.Add(Packet(0x35, Item("g_w_lghtsbr01")));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(0x35u, give.TargetObjectId);
        Assert.Equal("g_w_lghtsbr01", give.Item.Resref);

        // 0xE's items never reached the queue at all.
        Assert.False(agent.TryDequeuePendingLoot(out _));
    }

    /// <summary>Several items behind one filtered owner id cost exactly ONE log line, not one
    /// per item - the whole reason this filter exists is to stop the round-trip noise, and a
    /// per-item log would just move the noise from the plugin's log to the agent's.</summary>
    [Fact]
    public void AFilteredOwnerCostsOneLogLineNotOnePerItem()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = BuildWithBridge();
        bridge.MaterialisedOwnerIds = new HashSet<uint>();

        List<string> logs = [];
        agent.Log = logs.Add;

        session.Incoming.Add(Packet(0xE, Item("a"), Item("b"), Item("c")));
        agent.PumpOnce();

        Assert.Equal(1, logs.Count(l => l.Contains("0xE", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The dialogue-grant half of the SAME queue must never be filtered - it targets
    /// the local PC directly (<c>AgentWellKnownIds.LocalPlayerObjectId</c>), needs no owner-id
    /// translation, and is not what MaterialisedEnemies' table is even about. Proven here
    /// against a table that maps NOTHING, which would filter out any ordinary owner id.</summary>
    [Fact]
    public void ADialogueGrantIsNeverFilteredEvenAgainstAnEmptyMaterialisedTable()
    {
        (CoopAgent agent, MinimalBridge bridge, StubSession session) = BuildWithBridge();
        bridge.MaterialisedOwnerIds = new HashSet<uint>();

        session.IncomingDialogueGrants.Add(new DialogueGrantPacket(
            OriginPeerId: 3, OpSequence: 1, PartnerTag: "npc_carth",
            Items: [Item("g_w_blstrpstl001")]));
        agent.PumpOnce();

        Assert.True(agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give));
        Assert.Equal(AgentWellKnownIds.LocalPlayerObjectId, give.TargetObjectId);
        Assert.Equal("g_w_blstrpstl001", give.Item.Resref);
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

    // ---- concurrency audit (2026-08-28, docs/research/lag-audit.md): LootPump._pendingLoot
    // carries the identical unguarded-Queue<T> shape party-pool-sync.md §24's root cause 5
    // found and fixed in PartyPoolPump, and flagged this class as sharing. Now fixed the same
    // way (LootPump.cs's own _pendingGate); this pins the fix. ------------------------------

    /// <summary>
    /// Races <see cref="CoopAgent.PumpOnce"/> (the producer, exactly <c>CoopAgent.RunAsync</c>'s
    /// own tick loop shape) against <see cref="CoopAgent.TryDequeuePendingLoot"/> spun on a
    /// genuinely separate OS thread (matching <c>CoopCommandDispatcher.RunAsync</c>'s own
    /// independent scheduling), with no poll delay on the consumer to maximise exposure to the
    /// dangerous internal-array-resize window. Every one of 8,000 distinct-keyed items queued by
    /// the producer must be dequeued exactly once — nothing lost, nothing duplicated.
    /// </summary>
    [Fact]
    public void ThePendingLootQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries()
    {
        (CoopAgent agent, StubSession session) = Build();

        List<string> logs = [];
        agent.Log = logs.Add;

        agent.PumpOnce(); // baseline, nothing queued yet

        System.Collections.Concurrent.ConcurrentQueue<CoopAgent.PendingMaterialisedLootGive> observed = new();
        using CancellationTokenSource stop = new();

        Thread consumer = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive next))
                {
                    observed.Enqueue(next);
                }
            }
        })
        { IsBackground = true };
        consumer.Start();

        const int Iterations = 8_000;

        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                // One packet, one item, per producer tick - exactly PumpMaterialisedLoot's own
                // per-packet enqueue shape, just driven fast enough to race the consumer. Unlike
                // PartyPoolPump's own version of this test (whose producer tick does far more
                // unrelated work per call, giving the consumer time to keep pace), this pump's
                // PumpOnce is cheap enough that the producer genuinely can outrun the consumer
                // and legitimately trip the 256-entry cap a few times over 8,000 iterations -
                // that is MaxPendingLoot doing its job, not a correctness bug, so this test
                // accounts for legitimate drops explicitly (below) rather than asserting none.
                session.Incoming.Add(Packet((uint)i, Item($"item{i:D6}")));
                agent.PumpOnce();
            }

            // Production (and every drop decision, which happens synchronously in Pump on THIS
            // thread) is complete at this point - the only thing still moving is the consumer
            // thread draining whatever survived.
            int droppedSoFar = logs.Count(l => l.Contains("dropping materialised loot", StringComparison.Ordinal));
            int expectedObserved = Iterations - droppedSoFar;

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (observed.Count < expectedObserved && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(15);
            }
        }
        finally
        {
            stop.Cancel();
            consumer.Join(TimeSpan.FromSeconds(5));
        }

        // Drain any stragglers left after the consumer thread stopped.
        while (agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive leftover))
        {
            observed.Enqueue(leftover);
        }

        List<CoopAgent.PendingMaterialisedLootGive> all = [.. observed];

        // Nothing duplicated - a torn Queue<T> read under a genuine race can hand back the SAME
        // entry twice (see LootPump.cs's own _pendingGate remarks).
        HashSet<string> observedResrefs = new(StringComparer.Ordinal);
        foreach (CoopAgent.PendingMaterialisedLootGive entry in all)
        {
            Assert.True(observedResrefs.Add(entry.Item.Resref), $"'{entry.Item.Resref}' was dequeued more than once");
        }

        // Every legitimately-dropped item was reported exactly once too - a torn read on the
        // COUNT CHECK side of the lock could otherwise double-drop (or silently under-drop) an
        // item without either enqueueing OR logging it.
        HashSet<string> droppedResrefs = new(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in logs
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, "^dropping materialised loot '([^']+)'"))
            .Where(m => m.Success))
        {
            Assert.True(droppedResrefs.Add(m.Groups[1].Value), $"'{m.Groups[1].Value}' was reported dropped more than once");
        }

        // Never both delivered and dropped.
        Assert.Empty(observedResrefs.Intersect(droppedResrefs));

        // Nothing vanished silently - every produced item is accounted for exactly once, either
        // delivered or dropped.
        HashSet<string> expected = new(Enumerable.Range(0, Iterations).Select(i => $"item{i:D6}"), StringComparer.Ordinal);
        Assert.True(
            expected.SetEquals(observedResrefs.Union(droppedResrefs)),
            $"expected {Iterations} items accounted for; observed {observedResrefs.Count}, dropped {droppedResrefs.Count}");
    }
}
