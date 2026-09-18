using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Concurrency audit (2026-08-28, docs/research/lag-audit.md): <c>Pumps.StorePump</c>'s own
/// <c>_pendingApplies</c> carried the identical unguarded-<see cref="Queue{T}"/> shape
/// docs/research/party-pool-sync.md §24's root cause 5 found and fixed in
/// <c>Pumps.PartyPoolPump</c> (and flagged <c>Pumps.LootPump</c> as sharing) — this audit pass
/// found the store channel carries it too. Now fixed the same way (<c>StorePump.cs</c>'s own
/// <c>_pendingGate</c>); this pins the fix with the identical stress shape
/// <c>PartyPoolLoopbackTests.ThePendingApplyQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries</c>
/// uses, driven through a minimal <see cref="ICoopSession"/> stub (rather than a full real
/// host/client rig) so the race can be forced at high volume without real-network timing noise.
/// </summary>
public class StorePendingApplyQueueConcurrencyTests
{
    private const uint StoreId = 0x74;
    private const string Module = "tar_m02aa";

    private sealed class MinimalStoreBridge : IGameBridge
    {
        public string Description => "store-pending-apply-concurrency-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsStore => true;

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

    /// <summary>
    /// Every member below with no default in its own role interface (<see cref="IStoreSession"/>
    /// itself needs none — only <see cref="DrainStoreOps"/> is overridden, for control) —
    /// matches <c>MaterialisedLootQueueTests.StubSession</c>'s own required set exactly, since
    /// both implement the same <see cref="ICoopSession"/> union.
    /// </summary>
    private sealed class StubSession : ICoopSession
    {
        /// <summary>Ops waiting to be handed to the agent, as if they had just arrived off the
        /// wire via the real <c>HandleStoreOp</c> path. Only ever touched by the single producer
        /// thread (immediately before each <see cref="CoopAgent.PumpOnce"/> call), so this list
        /// itself needs no locking of its own.</summary>
        public List<StoreOpPacket> Incoming { get; } = [];

        public IReadOnlyList<StoreOpPacket> DrainStoreOps()
        {
            List<StoreOpPacket> ready = [.. Incoming];
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

    private static StoreStackKey Key(int i) =>
        new(StoreId, $"g_i_stress{i:D6}", 0, 0, 0, ItemIdentity.FlagKnown);

    /// <summary>
    /// Races <see cref="CoopAgent.PumpOnce"/> (the producer, exactly <c>CoopAgent.RunAsync</c>'s
    /// own tick loop shape — <see cref="Pumps.StorePump.Pump"/>'s step 2 drains
    /// <see cref="StubSession.DrainStoreOps"/> and enqueues one apply per op) against
    /// <see cref="CoopAgent.TryDequeuePendingStoreApply"/> spun on a genuinely separate OS thread
    /// (matching <c>CoopCommandDispatcher.RunAsync</c>'s own independent scheduling), with no
    /// poll delay on the consumer to maximise exposure to the dangerous internal-array-resize
    /// window. Every one of 8,000 distinct-keyed Add ops fed to the producer must be dequeued
    /// exactly once — nothing lost, nothing duplicated. Each key is used exactly once so the
    /// loop breaker (10 ops per key per 10s window) never has a reason to trip.
    /// </summary>
    [Fact]
    public void ThePendingApplyQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries()
    {
        MinimalStoreBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(
            bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });

        List<string> logs = [];
        agent.Log = logs.Add;

        agent.PumpOnce(); // baseline, nothing queued yet

        System.Collections.Concurrent.ConcurrentQueue<CoopAgent.PendingStoreApply> observed = new();
        using CancellationTokenSource stop = new();

        Thread consumer = new(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (agent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
                {
                    observed.Enqueue(next);
                }
                else
                {
                    // A two-core CI runner: a hard spin starves the producer. Yield instead.
                    Thread.Yield();
                }
            }
        })
        { IsBackground = true };
        consumer.Start();

        const int Iterations = 8_000;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        try
        {
            for (int i = 0; i < Iterations; i++)
            {
                // Keep the in-flight count under the pump's 256-entry cap: the consumer is a
                // plain OS thread, and on a loaded machine (2026-09-03: two builds and a
                // test run sharing the box) it was descheduled long enough for the producer to
                // overrun the cap and log 364 "dropping apply" lines - the cap doing its job,
                // not the race this test exists for. The two threads still overlap on every
                // enqueue/dequeue; only the backlog is bounded.
                while (i - observed.Count >= 128 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(1);
                }

                // A fresh key every iteration - a genuine remote Add this instance's engine owes,
                // exactly the shape session.DrainStoreOps() hands to step 2 in production.
                session.Incoming.Add(new StoreOpPacket(
                    OriginPeerId: 1, OpSequence: (uint)i, StoreOpKind.Add, Key(i), CountDelta: 1));
                agent.PumpOnce();
            }

            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (observed.Count < Iterations && DateTime.UtcNow < deadline)
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
        while (agent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply leftover))
        {
            observed.Enqueue(leftover);
        }

        List<CoopAgent.PendingStoreApply> all = [.. observed];

        int dropped = logs.Count(line => line.Contains("dropping apply", StringComparison.Ordinal));
        Assert.Equal(0, dropped); // the consumer frees a slot the instant it dequeues one - the
                                  // cap should never legitimately bite in this shape.

        // Nothing lost.
        Assert.Equal(Iterations, all.Count);

        // Nothing duplicated - a torn Queue<T> read under a genuine race can hand back the SAME
        // entry twice (see StorePump.cs's own _pendingGate remarks).
        HashSet<string> seen = new(Iterations, StringComparer.Ordinal);
        foreach (CoopAgent.PendingStoreApply entry in all)
        {
            Assert.True(seen.Add(entry.Key.Resref), $"'{entry.Key.Resref}' was dequeued more than once");
        }
    }
}
