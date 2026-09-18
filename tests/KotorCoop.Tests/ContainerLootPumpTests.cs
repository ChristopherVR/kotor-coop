using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Item 2 of docs/roadmap-seamless.md: <c>CoopAgent.PumpContainerLoot</c> used to detect and
/// publish local removals, and re-apply the merged set, only when
/// <see cref="ICoopSession.IsEnemyAuthority"/> was true — a client emptying a chest told
/// nobody, and a removal a client DID report never got applied back to the host's own copy of
/// the container (the host branch returned right after publishing). The wire and the
/// session-side store were already fully symmetric on <c>CoopHost</c>/<c>CoopClient</c>
/// (see ContainerLootTests.cs); these tests pin the orchestration in <c>CoopAgent</c> itself,
/// with a stub session standing in for either role.
/// </summary>
public class ContainerLootPumpTests
{
    private const string Module = "tar_m03aa";

    private sealed class LootBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public string Description => "container-loot-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsContainerLoot => true;

        /// <summary>Set by the test to simulate "this instance's own scan found these removals
        /// since the last call" — one-shot, like the real bridge's diff.</summary>
        public List<ContainerLootRemoval> LocalRemovalsToReport { get; set; } = [];

        public List<ContainerLootRemoval> LastWrittenRemovals { get; private set; } = [];

        public int WriteCallCount { get; private set; }

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<ContainerLootRemoval> CollectContainerLootRemovals()
        {
            List<ContainerLootRemoval> result = LocalRemovalsToReport;
            LocalRemovalsToReport = [];
            return result;
        }

        public bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals)
        {
            WriteCallCount++;
            LastWrittenRemovals = [.. removals];
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Stands in for either <c>CoopHost</c> or <c>CoopClient</c>'s loot handling —
    /// <see cref="IsEnemyAuthority"/> is settable so the same test shape covers both roles.
    /// Mirrors <c>ContainerLootStore</c>'s dedup-by-pair semantics closely enough to prove the
    /// agent's orchestration without re-testing the store itself.</summary>
    private sealed class StubSession : ICoopSession
    {
        private readonly HashSet<ContainerLootRemoval> _set = [];
        private readonly List<ContainerLootRemoval> _order = [];

        public List<(uint ContainerId, uint ItemId)> PublishCalls { get; } = [];

        public bool IsEnemyAuthority { get; set; }

        public void PublishContainerLoot(uint containerId, uint itemId)
        {
            // Mirrors CoopHost/CoopClient.PublishContainerLoot: only a pair new to the store
            // actually goes out (ContainerLootStore.Add returning false is the real echo
            // guard), so PublishCalls records what would hit the wire, not every invocation.
            if (Remember(new ContainerLootRemoval(containerId, itemId)))
            {
                PublishCalls.Add((containerId, itemId));
            }
        }

        /// <summary>Test seam: simulates a removal that arrived from ANOTHER peer over the wire
        /// and is already folded into the session's store — the real store folds these in the
        /// same way (<c>CoopHost.HandleContainerLoot</c> / the client's receive path), never
        /// through the local-detection loop this test also exercises.</summary>
        public void SeedPeerRemoval(uint containerId, uint itemId) =>
            Remember(new ContainerLootRemoval(containerId, itemId));

        private bool Remember(ContainerLootRemoval removal)
        {
            if (!_set.Add(removal))
            {
                return false;
            }

            _order.Add(removal);
            return true;
        }

        public IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => _order;

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

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

    private static (CoopAgent Agent, LootBridge Bridge, StubSession Session) Build(bool isEnemyAuthority)
    {
        LootBridge bridge = new();
        StubSession session = new() { IsEnemyAuthority = isEnemyAuthority };

        // HeavyPumpIntervalMs: 0 forces PumpContainerLoot (which rides the heavy-pump cadence)
        // to run on every PumpOnce call, so a test does not need to wait out a real wall clock -
        // same trick MaterialisedLootQueueTests uses for the combat cadence.
        CoopAgent agent = new(bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0 });

        return (agent, bridge, session);
    }

    [Fact]
    public void HostPublishesItsOwnLocalRemoval()
    {
        (CoopAgent agent, LootBridge bridge, StubSession session) = Build(isEnemyAuthority: true);

        bridge.LocalRemovalsToReport = [new ContainerLootRemoval(0x53, 0xC9)];
        agent.PumpOnce();

        Assert.Contains((0x53u, 0xC9u), session.PublishCalls);
    }

    [Fact]
    public void ClientPublishesItsOwnLocalRemovalTooNotOnlyTheHost()
    {
        // This is the duplication bug itself: before the fix, a client's own detected removal
        // was never published because PumpContainerLoot only called PublishContainerLoot when
        // IsEnemyAuthority was true.
        (CoopAgent agent, LootBridge bridge, StubSession session) = Build(isEnemyAuthority: false);

        bridge.LocalRemovalsToReport = [new ContainerLootRemoval(0x53, 0xC9)];
        agent.PumpOnce();

        Assert.Contains((0x53u, 0xC9u), session.PublishCalls);
    }

    [Fact]
    public void HostAppliesARemovalAPeerReported()
    {
        // The other half of the same bug: the host branch used to return immediately after
        // publishing its own removals and never called TryWriteContainerLoot at all, so a
        // removal a CLIENT reported sat in the host's session store forever without ever being
        // deleted from the host's own copy of the container.
        (CoopAgent agent, LootBridge bridge, StubSession session) = Build(isEnemyAuthority: true);

        session.SeedPeerRemoval(0x60, 0x61);
        agent.PumpOnce();

        Assert.Equal(1, bridge.WriteCallCount);
        Assert.Contains(new ContainerLootRemoval(0x60, 0x61), bridge.LastWrittenRemovals);
    }

    [Fact]
    public void ClientAppliesTheMergedSetIncludingItsOwnAndAPeersRemoval()
    {
        (CoopAgent agent, LootBridge bridge, StubSession session) = Build(isEnemyAuthority: false);

        bridge.LocalRemovalsToReport = [new ContainerLootRemoval(0x53, 0xC9)];
        session.SeedPeerRemoval(0x60, 0x61);
        agent.PumpOnce();

        Assert.Contains(new ContainerLootRemoval(0x53, 0xC9), bridge.LastWrittenRemovals);
        Assert.Contains(new ContainerLootRemoval(0x60, 0x61), bridge.LastWrittenRemovals);
    }

    /// <summary>A removal this instance just published must not be re-detected as a fresh
    /// local removal on the very next pump — the anti-echo property
    /// <c>ContainerLootStore.Add</c> documents, exercised here through the agent's own
    /// orchestration rather than the store directly.</summary>
    [Fact]
    public void ARemovalAlreadyKnownIsNotPublishedAgainOnTheNextPump()
    {
        (CoopAgent agent, LootBridge bridge, StubSession session) = Build(isEnemyAuthority: true);

        bridge.LocalRemovalsToReport = [new ContainerLootRemoval(0x53, 0xC9)];
        agent.PumpOnce();
        Assert.Single(session.PublishCalls);

        // The bridge's own scanner rediscovers the same (container, item) pair - exactly what
        // happens in the real game once the removal has actually been applied and the next
        // scan diffs against it again.
        bridge.LocalRemovalsToReport = [new ContainerLootRemoval(0x53, 0xC9)];
        agent.PumpOnce();

        Assert.Single(session.PublishCalls);
    }

    [Fact]
    public void NoWriteWhenBridgeDoesNotSupportContainerLoot()
    {
        // SupportsContainerLoot defaults to false on a bridge that does not override it, which
        // must short-circuit the whole pump before either the publish or the apply half runs.
        StubSession session = new() { IsEnemyAuthority = true };
        session.SeedPeerRemoval(0x60, 0x61);

        UnsupportingBridge gated = new();
        CoopAgent gatedAgent = new(gated, session, new AgentSettings { HeavyPumpIntervalMs = 0 });
        gatedAgent.PumpOnce();

        Assert.Empty(session.PublishCalls);
    }

    private sealed class UnsupportingBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, Module, 20, 0);

        public string Description => "unsupporting-bridge";

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
}
