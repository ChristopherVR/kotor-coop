using System.Linq;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <c>WorldStatePump.PumpItems</c>'s vanish-detection edge against a fake bridge, driven
/// through <c>CoopAgent.PumpOnce</c> the same way <c>ContainerLootPumpTests</c> drives
/// container removals — the live rig round this test follows from (docs/live-verification.md,
/// the "an item picked up here vanishes on the peer" `verify.ps1` finding) needed a bench pin
/// for exactly the failure mode named as a suspect: the ground-item bridge's own
/// <c>_spawnedItemResrefs</c> cache (item-identity.md §12.2, warmed the moment a slot first
/// reports its body alive) or <c>RecentlySyncedItems</c>'s script-item suppression ledger
/// silently swallowing the removal instead of forwarding it. Neither does, and this test
/// exists to keep it that way: a bridge that reports an id ALIVE and then VANISHED — even with
/// both caches warm — must yield exactly one <c>session.RemoveItem</c> call, and the pump must
/// not re-report the same vanish on the next tick once the bridge stops naming it.
/// </summary>
public class WorldStatePumpItemsTests
{
    private const string Module = "end_m01aa";

    private sealed class ItemsBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public string Description => "items-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsGroundItems => true;

        /// <summary>What <c>TryWriteGroundItems</c> was last asked to materialise — the
        /// session's own belief about what should be on the ground, mirrored back so the test
        /// can assert on it without a real plugin block.</summary>
        public List<GroundItem> LastWrittenItems { get; private set; } = [];

        public int WriteCallCount { get; private set; }

        /// <summary>One-shot per <see cref="CollectVanishedItems"/> call, like the real
        /// bridge's own per-slot probe: the test arms this before a pump call and it drains on
        /// read, exactly as <c>KotorGameBridge.CollectVanishedItems</c> reports a slot only
        /// while its status stays <c>ItemStateVanished</c> and the caller keeps asking every
        /// tick until the store stops wanting it.</summary>
        public List<uint> VanishedToReport { get; set; } = [];

        public int CollectVanishedItemsCallCount { get; private set; }

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryWriteGroundItems(IReadOnlyList<GroundItem> items)
        {
            WriteCallCount++;
            LastWrittenItems = [.. items];

            // Mirrors KotorGameBridge.TryWriteGroundItems's own "withdraw slots whose item is
            // no longer in the desired set" step: once the session's own store has dropped an
            // id (this pump's earlier RemoveItem call already took it out), the real bridge's
            // slot clears and CollectVanishedItems stops naming it — the whole reason a real
            // vanish announcement is "harmless" to repeat is that it stops on its own once the
            // caller stops wanting the item, not because the bridge magically calls RemoveItem
            // only once.
            HashSet<uint> stillWanted = [.. items.Select(i => i.ItemId)];
            VanishedToReport = [.. VanishedToReport.Where(stillWanted.Contains)];
            return true;
        }

        public List<uint> CollectVanishedItems()
        {
            CollectVanishedItemsCallCount++;
            return VanishedToReport;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Stands in for <c>CoopHost</c>/<c>CoopClient</c>'s item handling — the ONE
    /// interesting member is <see cref="RemoveItem"/>, which mirrors the real
    /// <c>CoopClient.RemoveItem</c>/<c>CoopHost.RemoveItem</c> shape (remove from the real
    /// <see cref="GroundItemStore"/>, record that it happened) closely enough to prove the
    /// pump's own orchestration without re-testing either session's wire half.</summary>
    private sealed class StubSession : ICoopSession
    {
        public List<uint> RemoveItemCalls { get; } = [];

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority { get; set; }

        public void RemoveItem(uint itemId)
        {
            RemoveItemCalls.Add(itemId);
            Items.Remove(itemId);
        }

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

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

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        public void PublishParty(IReadOnlyList<PartyMemberState> members)
        {
        }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
        }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience)
        {
        }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, ItemsBridge Bridge, StubSession Session) Build()
    {
        ItemsBridge bridge = new();
        StubSession session = new();

        // HeavyPumpIntervalMs: 0 forces PumpItems (which rides the heavy-pump cadence) to run
        // on every PumpOnce call — the same trick ContainerLootPumpTests/MaterialisedLootQueueTests
        // use so a test does not need to wait out a real wall clock.
        CoopAgent agent = new(bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0 });

        return (agent, bridge, session);
    }

    /// <summary>
    /// The edge the live-rig round named as a suspect, pinned on the bench: a bridge slot that
    /// goes alive-&gt;vanished must be forwarded as EXACTLY ONE removal, even though the exact
    /// same call also warms <see cref="RecentlySyncedItems"/> for that resref — recording a
    /// suppression-ledger entry must never gate or duplicate the removal itself. The ledger is
    /// write-only from this pump's own point of view (<c>ScriptItemGrantPump</c> is the only
    /// reader), so this is also a regression pin against ever wiring that ledger into the
    /// removal decision.
    /// </summary>
    [Fact]
    public void AVanishedSlotForwardsExactlyOneRemoval()
    {
        (CoopAgent agent, ItemsBridge bridge, StubSession session) = Build();

        // Seed the session's own belief: an item is on the ground and the local player already
        // possesses no knowledge of it vanishing yet.
        session.Items.Drop(new GroundItem(0x20001, "g_i_medeqpmnt01", Module, Vector3f.Zero));

        // First tick: the bridge reports the item still alive (nothing vanished). The pump
        // must not remove it and must have declared it wanted.
        bridge.VanishedToReport = [];
        agent.PumpOnce();

        Assert.Empty(session.RemoveItemCalls);
        Assert.True(session.Items.TryGet(0x20001, out _));

        // Second tick: the bridge now reports the slot vanished (the local player picked it
        // up) - the same transition CollectVanishedItems reports once ItemController's probe
        // flips a slot's status to ItemStateVanished.
        bridge.VanishedToReport = [0x20001];
        agent.PumpOnce();

        Assert.Equal([0x20001u], session.RemoveItemCalls);
        Assert.False(session.Items.TryGet(0x20001, out _));

        // Third tick: the store no longer has the item, but a real bridge keeps reporting the
        // slot Vanished every tick until the agent stops wanting it (KotorGameBridge.GroundItems.cs's
        // own doc: "Announced every pump until the store removes them, which is harmless
        // because removal is idempotent"). RemoveItem must not be called again.
        agent.PumpOnce();

        Assert.Equal([0x20001u], session.RemoveItemCalls);
    }

    /// <summary>
    /// Two independent items vanishing on the SAME tick must both be forwarded — the removal
    /// loop must not stop after the first, and must not conflate the two ids.
    /// </summary>
    [Fact]
    public void TwoSimultaneousVanishesBothForward()
    {
        (CoopAgent agent, ItemsBridge bridge, StubSession session) = Build();

        session.Items.Drop(new GroundItem(0x30001, "g_i_medeqpmnt01", Module, Vector3f.Zero));
        session.Items.Drop(new GroundItem(0x30002, "g_w_blstrpstl001", Module, Vector3f.Zero));

        bridge.VanishedToReport = [0x30001, 0x30002];
        agent.PumpOnce();

        Assert.Contains(0x30001u, session.RemoveItemCalls);
        Assert.Contains(0x30002u, session.RemoveItemCalls);
        Assert.Equal(2, session.RemoveItemCalls.Count);
        Assert.False(session.Items.TryGet(0x30001, out _));
        Assert.False(session.Items.TryGet(0x30002, out _));
    }
}
