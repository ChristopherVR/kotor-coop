using System.Linq;
using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump loop on each side, a
/// simulated single-slot staging-cheat channel under real contention — the same production shape
/// <see cref="PartyPoolLoopbackTests"/>/<see cref="StoreLoopbackTests"/> already established for
/// mode 1, driving mode 2 (<c>party_pool_sync=2</c>/<c>store_sync=2</c>,
/// docs/research/shared-inventory-v2.md) instead. Where mode 1's own suites needed thousands of
/// lines chasing seven successive root causes in a bidirectional op-log, these tests exist to
/// demonstrate the structural property docs/research/shared-inventory-v2.md §2.2 argues: nothing
/// here needs a loop-breaker, because there is no loop to break — a host publishes, a client
/// requests, and reconciliation always targets an ABSOLUTE snapshot rather than an accumulated
/// delta history.
/// </summary>
public class SharedInventoryV2LoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly PartyPoolStackKey Blaster =
        new("g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly PartyPoolStackKey Thermal =
        new("g_w_thermldet01", 0, 0, 0, ItemIdentity.FlagKnown);

    private const uint TestStoreId = 0x74;

    private static readonly StoreStackKey StoreBlaster =
        new(TestStoreId, "g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    /// <summary>A shop's own PRISTINE stock — the stock both instances loaded out of the same
    /// module, present in both engines before anybody does anything. §12 (2026-08-29) made this
    /// distinction load-bearing on the client's reconcile side: the FIRST snapshot naming a store
    /// is that store's baseline, not a gap for the client to close by blind-inserting the whole
    /// shop into a copy that already had it (docs/research/shared-inventory-v2.md §10.2's own
    /// 432-insert cascade). A test that wants to exercise a host-side CHANGE has to establish
    /// that baseline first, exactly as a real session does.</summary>
    private static readonly StoreStackKey StorePristine =
        new(TestStoreId, "g_i_belt006", 0, 0, 0, ItemIdentity.FlagKnown);

    private const string Module = "end_m01aa";

    /// <summary>
    /// Stands in for <c>KotorGameBridge.PartyPool.cs</c>/<c>.Store.cs</c>: <see cref="TruePartyPool"/>/
    /// <see cref="TrueStore"/> are exactly what a human's drag-and-drop, a `pooladd`/`buy`/`sell`
    /// console verb, or a landed cheat-channel apply would leave in the engine's own repository —
    /// but the two <c>TryScan*</c> methods never hand the pump that live truth directly. Each
    /// reproduces the real bridge's own throttled scan cache line for line (short interval,
    /// milliseconds not the real 1000, purely so a test does not wait out real wall-clock
    /// seconds). <see cref="OpenStoreId"/> stands in for "a local player has this store's GUI
    /// open" (0 = no store open) — the same GUI-gating limitation v1 and v2 both carry.
    /// </summary>
    private sealed class FakeBridgeV2(int scanIntervalMs) : IGameBridge
    {
        public Dictionary<PartyPoolStackKey, int> TruePartyPool { get; } = [];

        public Dictionary<StoreStackKey, int> TrueStore { get; } = [];

        public uint OpenStoreId;

        private Dictionary<PartyPoolStackKey, int> _lastPoolScan = [];
        private bool _poolEverScanned;
        private long _nextPoolScanTicks;

        private Dictionary<StoreStackKey, int> _lastStoreScan = [];
        private uint _lastStoreScanId;
        private bool _storeEverScanned;
        private long _nextStoreScanTicks;

        public string Description => "shared-inventory-v2-loopback-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartyPool => true;

        public bool SupportsStore => true;

        // This fixture models both peers loaded into the same deterministic save. Initial
        // party-pool reconciliation now requires a positive matching save identity before the
        // client consumes the host snapshot; 0 means "unknown" and correctly keeps the gate
        // closed (as it does for an unresolved real save).
        public uint SaveIdentityDigest => 0x504F4F4Cu;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            long now = Environment.TickCount64;
            if (_poolEverScanned && now < _nextPoolScanTicks)
            {
                totals = _lastPoolScan;
                return true;
            }

            _nextPoolScanTicks = now + scanIntervalMs;
            _lastPoolScan = new Dictionary<PartyPoolStackKey, int>(TruePartyPool);
            _poolEverScanned = true;
            totals = _lastPoolScan;
            return true;
        }

        public bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
        {
            storeObjectId = 0;
            totals = new Dictionary<StoreStackKey, int>();

            if (OpenStoreId == 0)
            {
                return false;
            }

            long now = Environment.TickCount64;
            if (_storeEverScanned && now < _nextStoreScanTicks && _lastStoreScanId == OpenStoreId)
            {
                storeObjectId = _lastStoreScanId;
                totals = _lastStoreScan;
                return true;
            }

            _nextStoreScanTicks = now + scanIntervalMs;
            Dictionary<StoreStackKey, int> scan = [];
            foreach ((StoreStackKey key, int count) in TrueStore)
            {
                if (key.StoreObjectId == OpenStoreId)
                {
                    scan[key] = count;
                }
            }

            _lastStoreScan = scan;
            _lastStoreScanId = OpenStoreId;
            _storeEverScanned = true;
            storeObjectId = OpenStoreId;
            totals = scan;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A real <see cref="CoopAgent"/> pump loop plus a simulated single-slot staging-cheat
    /// channel — mirrors <c>PartyPoolLoopbackTests.AgentLoop</c>'s own shape exactly, widened to
    /// drain BOTH v2 apply queues (party pool AND store) through the identical single-slot
    /// arbitration a real <c>CoopCommandDispatcher</c> provides (at most one apply "in flight" at
    /// a time, landing after a randomised tens-to-low-hundreds-of-ms delay).
    /// </summary>
    private sealed class AgentLoopV2 : IDisposable
    {
        public FakeBridgeV2 Bridge { get; }

        public CoopAgent Agent { get; }

        /// <summary>Every line this side's <see cref="Agent"/> has logged, oldest first — the
        /// same "capture for the failure message, not the console" convention
        /// <c>StoreLoopbackTests.AgentLoop.Logs</c> already established, so a failing assertion
        /// can show exactly what each side's pump actually did without needing
        /// <c>--logger detailed</c>.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; } = new();

        /// <summary>§7 (2026-08-29), item 4: how many times THIS side's own
        /// <see cref="CoopAgent.TryDequeuePendingPartyPoolApplyV2"/> has ever returned true — i.e.
        /// how many times this instance's own pump queued an engine apply at all, whether or not
        /// it has landed yet. On a HOST-role agent this must stay exactly 0 unless a client has
        /// genuinely published an accepted intent — the host's own scan/publish branch has no
        /// path that queues one on its own.</summary>
        public int PoolApplyDequeueCount;

        /// <summary>Store's mirror of <see cref="PoolApplyDequeueCount"/>.</summary>
        public int StoreApplyDequeueCount;

        /// <summary>How many times a queued party-pool apply has actually LANDED against
        /// <see cref="Bridge"/>'s <see cref="FakeBridgeV2.TruePartyPool"/> (i.e. <see cref="Land"/>
        /// ran for it) — strictly less than or equal to <see cref="PoolApplyDequeueCount"/>.</summary>
        public int PoolApplyLandings;

        /// <summary>Store's mirror of <see cref="PoolApplyLandings"/>.</summary>
        public int StoreApplyLandings;

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly Random _random = new();

        /// <summary>Counts one per loop iteration (one <see cref="Agent.PumpOnce"/> call) — the
        /// "tick" unit <see cref="_cheatLandsAtTick"/> is measured in. §9 (2026-08-29, item 1):
        /// the cheat-channel landing delay used to be wall-clock (30-250ms), which — combined
        /// with the fake bridge's own ~150ms scan-cache throttle — meant a queued reconcile's own
        /// landing and the pump's next few detect passes rarely interleaved tightly enough to
        /// exercise the exact live-observed race (docs/research/shared-inventory-v2.md §8.1: a
        /// client's own reconcile-apply landing gets re-detected as a fresh local intent).
        /// Measuring the delay in PUMP TICKS instead — "1-3 ticks late, one per tick" per this
        /// pass's own task brief — guarantees several detect passes always run WHILE a reconcile
        /// is genuinely in flight, and at least one runs on the very tick it lands, regardless of
        /// wall-clock scheduling jitter.</summary>
        private long _tick;

        private long _cheatLandsAtTick = -1;
        private long _cheatLandsAtMs = -1;
        private CoopAgent.PendingPartyPoolApply? _inFlightPool;
        private CoopAgent.PendingStoreApply? _inFlightStore;

        /// <summary>Fixed WALL-CLOCK milliseconds a dequeued cheat apply takes to land, or 0 for
        /// the ordinary randomised 1-3 ticks. A test that needs an apply to still be IN FLIGHT
        /// across a whole host scan + broadcast cycle — docs/research/shared-inventory-v2.md
        /// §15.5's wobble, where the live single-slot arbitrated channel took seconds — sets this
        /// explicitly. Deliberately wall-clock, not ticks: a tick is however long one pump
        /// iteration happens to take on the machine running the suite, which made the same number
        /// mean 2s on one box and 10s on another.</summary>
        public int CheatLandDelayMs { get; init; }


        /// <param name="trace">Turns on both mode-2 pumps' per-tick diagnostic lines (coop.ini
        /// <c>party_pool_trace</c>) into <see cref="Logs"/>, so a failing assertion can print the
        /// same actual/belief/snap/outstanding/target evidence a rig round reads out of the agent
        /// log. A CONSTRUCTOR parameter, not an init property: the CoopAgent (and with it both
        /// pumps' trace flag) is built right here, before any object initializer would run.</param>
        public AgentLoopV2(
            ICoopSession session, int bridgeScanIntervalMs, int heavyPumpIntervalMs = 0, bool trace = false)
        {
            Bridge = new FakeBridgeV2(bridgeScanIntervalMs);
            Agent = new CoopAgent(
                Bridge,
                session,
                new AgentSettings
                {
                    HeavyPumpIntervalMs = heavyPumpIntervalMs,
                    PartyPoolSyncMode = 2,
                    StoreSyncMode = 2,
                    PartyPoolTrace = trace,
                    // This suite exercises the underlying host-authoritative mirror channel
                    // itself (shared-inventory-v2.md), with ordinary non-quest resrefs
                    // ("g_i_medeqpmnt01" etc.) — inventory_mode's new personal-by-default
                    // filter (docs/research/personal-inventory.md) would otherwise hide every
                    // one of them from the wire. SharedInventoryV2PumpTests.cs's own
                    // "PersonalMode..." tests cover the filter itself.
                    InventoryPersonal = false,
                    CreditsPersonal = false,
                });

            Agent.Log = msg => Logs.Enqueue(msg);

            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Agent.PumpOnce();
                    _tick++;

                    if (_cheatLandsAtTick >= 0)
                    {
                        if (_cheatLandsAtMs >= 0
                            ? Environment.TickCount64 >= _cheatLandsAtMs
                            : _tick >= _cheatLandsAtTick)
                        {
                            Land();
                            _cheatLandsAtTick = -1;
                            _cheatLandsAtMs = -1;
                            _inFlightPool = null;
                            _inFlightStore = null;
                        }
                    }
                    else if (Agent.TryDequeuePendingPartyPoolApplyV2(out CoopAgent.PendingPartyPoolApply poolNext))
                    {
                        Interlocked.Increment(ref PoolApplyDequeueCount);
                        _inFlightPool = poolNext;
                        _cheatLandsAtTick = _tick + 1 + _random.Next(3); // lands 1-3 ticks later
                        _cheatLandsAtMs = CheatLandDelayMs > 0 ? Environment.TickCount64 + CheatLandDelayMs : -1;
                    }
                    else if (Agent.TryDequeuePendingStoreApplyV2(out CoopAgent.PendingStoreApply storeNext))
                    {
                        Interlocked.Increment(ref StoreApplyDequeueCount);
                        _inFlightStore = storeNext;
                        _cheatLandsAtTick = _tick + 1 + _random.Next(3); // lands 1-3 ticks later
                        _cheatLandsAtMs = CheatLandDelayMs > 0 ? Environment.TickCount64 + CheatLandDelayMs : -1;
                    }
                }
                catch (Exception)
                {
                    // Same "outer test asserts on final state" tolerance as
                    // PartyPoolLoopbackTests.AgentLoop's own loop.
                }

                await Task.Delay(3, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private void Land()
        {
            if (_inFlightPool is { } p)
            {
                int before = Bridge.TruePartyPool.GetValueOrDefault(p.Key);
                int after = p.Kind == PartyPoolOpKind.Add ? before + p.Count : Math.Max(0, before - p.Count);
                if (after > 0)
                {
                    Bridge.TruePartyPool[p.Key] = after;
                }
                else
                {
                    Bridge.TruePartyPool.Remove(p.Key);
                }

                Interlocked.Increment(ref PoolApplyLandings);
            }
            else if (_inFlightStore is { } s)
            {
                int before = Bridge.TrueStore.GetValueOrDefault(s.Key);
                int after = s.Kind == StoreOpKind.Add ? before + s.Count : Math.Max(0, before - s.Count);
                if (after > 0)
                {
                    Bridge.TrueStore[s.Key] = after;
                }
                else
                {
                    Bridge.TrueStore.Remove(s.Key);
                }

                Interlocked.Increment(ref StoreApplyLandings);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _task.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _cts.Dispose();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Holds a condition for a further stretch of real time so a duplicate/creeping
    /// total that appears one or two windows AFTER the first convergence cannot slip past a
    /// check that only samples once — the direct test of docs/research/shared-inventory-v2.md
    /// §2's "cannot amplify" claim.</summary>
    private static async Task<bool> StaysAsync(Func<bool> condition, TimeSpan hold)
    {
        DateTime deadline = DateTime.UtcNow + hold;
        while (DateTime.UtcNow < deadline)
        {
            if (!condition())
            {
                return false;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Waits for <paramref name="read"/> to stop CHANGING for a full <paramref name="quietFor"/>
    /// window and returns the value it settled at, or throws if it never goes quiet within
    /// <paramref name="timeout"/>. For a channel like the ambient-noise scenario where a value's
    /// FIRST observed reading is not necessarily its FINAL one (a documented, bounded, possibly
    /// delayed second bump), capturing "whatever the first sample was" and then asserting
    /// stability from that instant is the wrong test — a legitimate, expected LATER transition
    /// reads as a false "amplification" failure. This waits out every transition first, so the
    /// "does it then stay put" assertion that follows is only ever checking for a genuine,
    /// unbounded creep, never a merely-delayed one-time settle.
    /// </summary>
    private static async Task<int> QuiesceAsync(Func<int> read, TimeSpan quietFor, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        int last = read();
        DateTime quietSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(15).ConfigureAwait(false);
            int current = read();

            if (current != last)
            {
                last = current;
                quietSince = DateTime.UtcNow;
                continue;
            }

            if (DateTime.UtcNow - quietSince >= quietFor)
            {
                return last;
            }
        }

        throw new TimeoutException(
            $"value never went quiet for {quietFor} within {timeout} — last seen {last}");
    }

    private readonly record struct Rig(
        CoopHost Host, CoopClient Client, AgentLoopV2 HostAgent, AgentLoopV2 ClientAgent) : IDisposable
    {
        public void Dispose()
        {
            HostAgent.Dispose();
            ClientAgent.Dispose();
            Host.Dispose();
            Client.Dispose();
        }
    }

    /// <param name="bridgeScanIntervalMs">The fake bridge's own scan-cache window. 150 (the
    /// default) is the ordinary case. A test that needs the host to genuinely RE-SCAN a store
    /// while an accepted intent's engine apply is still queued behind the cheat channel — the
    /// §15.5 wobble's own condition — passes 0, so a scan happens on every tick rather than only
    /// every 50th.</param>
    /// <param name="cheatLandDelayMs">See <see cref="AgentLoopV2.CheatLandDelayMs"/> — 0 keeps
    /// the ordinary randomised 1-3 ticks.</param>
    /// <param name="hostHeavyPumpIntervalMs">The HOST agent's heavy-pass interval. 0 (every
    /// tick) is the ordinary case. A test that needs a host snapshot BROADCAST to fall between an
    /// accepted intent's belief write-through and the host's next store SCAN — the exact race
    /// docs/research/shared-inventory-v2.md §15.5's wobble hinges on, since the scan is what
    /// would otherwise overwrite the write-through within a millisecond — widens it past the
    /// 1000ms broadcast interval so the ordering is certain rather than a coin flip.</param>
    private static async Task<(Rig Rig, CancellationTokenSource Cts)> BuildConnectedRigAsync(
        int bridgeScanIntervalMs = 150, int cheatLandDelayMs = 0, bool trace = false,
        int hostHeavyPumpIntervalMs = 0)
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
        _ = host.RunAsync(cts.Token);
        _ = client.RunAsync(cts.Token);

        AgentLoopV2 hostAgent = new(host, bridgeScanIntervalMs, hostHeavyPumpIntervalMs, trace)
        {
            CheatLandDelayMs = cheatLandDelayMs,
        };
        AgentLoopV2 clientAgent = new(client, bridgeScanIntervalMs, trace: trace);

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        return (new Rig(host, client, hostAgent, clientAgent), cts);
    }

    [Fact]
    public async Task BaselineEmptyPoolConvergesWithNoSpuriousIntent()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            Assert.True(
                await StaysAsync(
                    () => rig.Client.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> t)
                        && t.Count == 0,
                    TimeSpan.FromSeconds(2)),
                "an empty pool should converge to an empty (but HEARD-FROM) snapshot, not linger unheard");
        }
    }

    [Fact]
    public async Task HostDepositConvergesToExactlyOneCopyOnBothSides()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // The human drags 5 into the party stash on the HOST's screen — an ordinary local
            // engine change, exactly like an ambient script's own write; the host never
            // "detects" this, it is simply what the next scan reads.
            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 5;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> t)
                        && t.GetValueOrDefault(Blaster) == 5,
                    Timeout),
                "client never converged on the host's deposit");

            // The wire-level snapshot converging is not the same claim as the CLIENT's own
            // engine having reconciled up to it yet (that still needs its own apply through the
            // simulated cheat channel) — wait for the engine to actually catch up BEFORE
            // asserting it then stays put, or a StaysAsync sampled too early reads "still 0" as
            // "already crept past 5".
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 5,
                    Timeout),
                "client's own engine never reconciled up to 5");

            Assert.True(
                await StaysAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 5,
                    TimeSpan.FromSeconds(4)),
                "client's own engine must STAY at exactly 5, not creep past it");
        }
    }

    /// <summary>
    /// Live 2026-09-02 (docs/research/death-handling.md §6.4): a rejoin after a session wipe
    /// builds a FRESH CoopClient that reclaims the same peer id (the nonce is the stable
    /// PeerIdentity). Its intent serials used to restart at 1, so its first two intents collided
    /// with the (peer, serial) pairs the host still remembered from the first join: acked as
    /// replays, applied never — and the client, taking the acks as landings, reconciled its own
    /// two carried stacks away to match the host. The second lifetime's deposit must land.
    /// </summary>
    [Fact]
    public async Task ARejoinedClientsIntentsAreNotDedupedAgainstItsFirstLifetime()
    {
        const uint identity = 0xC0FFEE42;
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
        _ = host.RunAsync(cts.Token);
        using AgentLoopV2 hostAgent = new(host, 150);

        static CoopClient Join(CoopHost host, CancellationToken token)
        {
            CoopClient client = new(
                new SessionSettings { PlayerName = "Malak", TickRate = 30, PeerIdentity = identity },
                new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
            _ = client.RunAsync(token);
            return client;
        }

        using (host)
        {
            // First lifetime: one deposit, serial 1 on the wire, lands on the host.
            CoopClient first = Join(host, cts.Token);
            AgentLoopV2 firstAgent = new(first, 150);
            Assert.True(
                await WaitForAsync(() => first.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "first lifetime never connected");
            await Task.Delay(300);

            firstAgent.Bridge.TruePartyPool[Blaster] = 1;
            Assert.True(
                await WaitForAsync(() => hostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 1, Timeout),
                "the first lifetime's deposit never landed on the host");

            // The wipe: the client's session ends, the host reloads a save that never had the
            // deposit, and the client rejoins with a new CoopClient under the same identity.
            firstAgent.Dispose();
            first.Dispose();
            hostAgent.Bridge.TruePartyPool.Clear();

            CoopClient second = Join(host, cts.Token);
            using AgentLoopV2 secondAgent = new(second, 150);
            using (second)
            {
                Assert.True(
                    await WaitForAsync(() => second.IsConnected, Timeout),
                    "second lifetime never connected");
                await Task.Delay(300);

                // The carried character still holds the stack the host's reload lost; it is a
                // local surplus against the host's snapshot and goes out as this lifetime's
                // very first intent.
                secondAgent.Bridge.TruePartyPool[Blaster] = 1;

                Assert.True(
                    await WaitForAsync(() => hostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 1, Timeout),
                    "the rejoined client's deposit was deduped against its first lifetime's serial and never applied");
                Assert.True(
                    await StaysAsync(() => secondAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 1,
                        TimeSpan.FromSeconds(3)),
                    "the rejoined client must keep its own stack, not reconcile it away");
            }
        }
    }

    [Fact]
    public async Task ThreeRapidClientDepositsConvergeToExactlySumOnBothSides()
    {
        // docs/research/party-pool-sync.md §27's own recipe, re-run against v2: three rapid
        // same-key deposits, no wait between them. v1 amplified this past 500 units; v2 must
        // converge to exactly 3.
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.ClientAgent.Bridge.TruePartyPool[Thermal] = 1;
            rig.ClientAgent.Bridge.TruePartyPool[Thermal] = 2;
            rig.ClientAgent.Bridge.TruePartyPool[Thermal] = 3;

            Assert.True(
                await WaitForAsync(() => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 3, Timeout),
                $"host never converged to exactly 3; got {rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}");

            // The client's OWN local deposit (this test's origin) already sits at 3 directly —
            // but it may briefly reconcile away and back via the pump's own detect/reconcile
            // passes before settling, so wait for it to genuinely be at 3 before asserting it
            // then stays there (see HostDepositConvergesToExactlyOneCopyOnBothSides's identical
            // remarks on why StaysAsync must never be the FIRST check of a value that has not
            // yet had a chance to converge).
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 3, Timeout),
                $"client's own engine never settled at exactly 3; got "
                + $"{rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 3
                        && rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 3,
                    TimeSpan.FromSeconds(5)),
                $"both sides must STAY at exactly 3 - no amplification, ever. "
                + $"Host={rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)} "
                + $"Client={rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}");
        }
    }

    [Fact]
    public async Task ThreeRapidHostChangesReachClientExactlyViaSnapshotAlone()
    {
        // The host's side of §27's recipe: three rapid direct engine changes (a human's own
        // pooladd equivalent, or ambient scripts) with no wait between them. The host never
        // "detects" or "publishes" any of this as an event — it is simply what the next scan
        // reads, unconditionally (docs/research/shared-inventory-v2.md §2.4).
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TruePartyPool[Thermal] = 1;
            rig.HostAgent.Bridge.TruePartyPool[Thermal] = 2;
            rig.HostAgent.Bridge.TruePartyPool[Thermal] = 6;

            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 6, Timeout),
                $"client never converged to exactly 6; got {rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}");

            Assert.True(
                await StaysAsync(
                    () => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 6
                        && rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == 6,
                    TimeSpan.FromSeconds(5)),
                "no round trip back through the client should ever perturb the host's own total");
        }
    }

    [Fact]
    public async Task IdenticalAmbientNoiseOnBothSidesConvergesWithoutASecondRoundOfAmplification()
    {
        // docs/research/shared-inventory-v2.md §2.4's own worked example: both instances run the
        // identical ambient script content independently, bumping BOTH TrueEngineState
        // dictionaries by the same amount in the same tick — exactly party-pool-sync.md §27's
        // own scenario. The client's own detect step MAY publish one spurious intent for its own
        // half of the ambient bump (a documented, accepted, BOUNDED residual risk — §2.2/§5#1),
        // but the net effect must be a single, bounded overcount, never an escalating loop.
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TruePartyPool[Thermal] = 2;
            rig.ClientAgent.Bridge.TruePartyPool[Thermal] = 2;

            // Converges somewhere at or above the genuine +2 (a spurious client-side intent can
            // add at most one more +2 on top, per the worked example) but must SETTLE, bounded —
            // never the 178x-583x runaway v1's own op-log produced.
            //
            // The FIRST reading to reach 2 (the genuine ambient bump, on both engines
            // independently) is not necessarily the FINAL one — a spurious client-side intent
            // for its own half of the SAME ambient bump can arrive and land a little later,
            // bumping the host to 4. Capturing "settled" from the first sample and immediately
            // asserting stability would read that legitimate, documented, bounded transition as
            // a false "amplification" failure — QuiesceAsync waits out every transition first,
            // so only a genuine, unbounded creep past whatever it actually settles at fails the
            // assertion that follows.
            int settled = await QuiesceAsync(
                () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal),
                quietFor: TimeSpan.FromSeconds(4),
                timeout: Timeout);

            Assert.True(settled is 2 or 4, $"expected the bounded 2 or 4 outcome, got {settled}");

            // The client's own engine reconciles toward whatever the host settled at
            // asynchronously (through the simulated cheat channel) — wait for it to genuinely
            // arrive before asserting it then stays, or a StaysAsync sampled too early reads
            // "still catching up" as "already crept past the settled value".
            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == settled, Timeout),
                $"client's own engine never converged to the settled value ({settled}); got "
                + $"{rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == settled
                        && rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == settled,
                    TimeSpan.FromSeconds(6)),
                $"both sides must STAY at the settled value ({settled}) — a creeping total here is exactly "
                + "the amplification this design exists to make impossible");
        }
    }

    [Fact]
    public async Task DepositWithdrawThenFreshDepositNetsToTheCorrectRunningTotal()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 5;
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 5, Timeout));

            rig.HostAgent.Bridge.TruePartyPool.Remove(Blaster);
            Assert.True(
                await WaitForAsync(() => !rig.ClientAgent.Bridge.TruePartyPool.ContainsKey(Blaster), Timeout),
                "withdrawal never converged on the client");

            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 3;
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 3, Timeout));

            Assert.True(
                await StaysAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 3,
                    TimeSpan.FromSeconds(4)),
                "the fresh deposit must settle at exactly 3, not 8 (the withdrawn 5 leaking back in)");
        }
    }

    [Fact]
    public async Task DepleteToZeroConvergesToAbsenceNotALingeringZeroEntry()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 4;
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 4, Timeout));

            rig.HostAgent.Bridge.TruePartyPool.Remove(Blaster);

            Assert.True(
                await WaitForAsync(() => !rig.ClientAgent.Bridge.TruePartyPool.ContainsKey(Blaster), Timeout),
                "full depletion never converged on the client");
            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> t)
                        && !t.ContainsKey(Blaster),
                    Timeout),
                "the wire-level snapshot must also drop the key entirely, not carry it at 0");
        }
    }

    [Fact]
    public async Task StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // The host "opens" the store and a sell lands in it — an ordinary local engine
            // change, GUI-gated only for DETECTION, exactly like v1.
            rig.HostAgent.Bridge.OpenStoreId = TestStoreId;
            rig.HostAgent.Bridge.TrueStore[StoreBlaster] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StoreBlaster) == 1,
                    Timeout),
                "client never learned of the host's sell");

            // The client opens the SAME store and buys it back — reconciliation must first bring
            // the client's own engine up to 1 (matching the host), and the buy-back must reach
            // the host WITHOUT needing the host's own GUI to still be open (applying an intent
            // never needs the GUI, only detecting a local change does).
            rig.ClientAgent.Bridge.OpenStoreId = TestStoreId;

            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StoreBlaster) == 1, Timeout),
                "client's own engine never reconciled up to the host's mirrored sell");

            // §9 (2026-08-29): a real settle window, not just a poll-succeeded instant. Item 2's
            // own fix lets the reconcile-up above land entirely BLIND (the client's store GUI was
            // closed the whole time it was in flight — see the class remarks) — the WaitForAsync
            // just above is satisfied the moment the landed value shows up in the raw engine
            // dictionary, which can win the race against this pump's own FIRST live scan of the
            // now-open GUI ever running at all. Without giving that first live scan a genuine
            // chance to happen, a buy-back performed immediately afterward can land AND be
            // reversed before the pump's belief-tracking ever observed the "1" in between — not a
            // detection bug, a real "both halves of the deposit-then-withdraw pair landed inside
            // the same never-yet-sampled window" case, the identical shape
            // docs/research/shared-inventory-v2.md's own root-cause-6/Bug-4 lineage already
            // documents, just wider here because BLIND reconcile removes the old "only while the
            // GUI is open" bound on how long that window can be. A human opening a store and then
            // clicking sell takes far longer than one scan cycle; this delay reflects that.
            await Task.Delay(300);

            // The buy-back: the client's own engine drops the item, and the host's OWN GUI is
            // closed at the moment the intent arrives — applying an accepted intent never needed
            // the GUI open (docs/research/shared-inventory-v2.md §2.6), only detecting a fresh
            // local change does.
            rig.ClientAgent.Bridge.TrueStore.Remove(StoreBlaster);
            rig.HostAgent.Bridge.OpenStoreId = 0;

            Assert.True(
                await WaitForAsync(
                    () => !rig.HostAgent.Bridge.TrueStore.ContainsKey(StoreBlaster)
                        || rig.HostAgent.Bridge.TrueStore[StoreBlaster] == 0,
                    Timeout),
                $"the client's buy-back never reached the host's own engine; host still shows "
                + $"{rig.HostAgent.Bridge.TrueStore.GetValueOrDefault(StoreBlaster)}");
        }
    }


    /// <summary>
    /// docs/research/shared-inventory-v2.md §15.5's store wobble, reproduced end to end and
    /// pinned closed. The one clause of the store recipe that did not come out clean on the rig:
    /// two seconds after the client published its buy, the client RE-INSERTED the item it had
    /// just bought, then removed it again three seconds later — two spurious engine writes for
    /// one transaction, self-healing but real.
    ///
    /// The condition the earlier <see cref="StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost"/>
    /// deliberately does not create is the host's OWN store GUI still being open while the
    /// client's intent is in flight. The host accepts the intent and write-throughs its belief
    /// (so the very next broadcast says "0"), but the engine apply that write-through predicts is
    /// still queued behind the single-slot cheat channel — so the host's next scan reads the
    /// PRE-apply stock and republishes "1". That regression is what the client read as the host's
    /// truth: credit retired, target back to 1, insert.
    ///
    /// The fix is a host-side invariant (<c>CoopHost._storeIntentEffectV2</c>): a published
    /// snapshot is MONOTONE with respect to accepted intents — once the host has said a key moved
    /// because it accepted an intent, it may not say otherwise until it has genuinely re-observed
    /// the key at the value it predicted. So this asserts the thing the rig measured: the CLIENT
    /// lands ZERO engine applies of its own for the whole transaction.
    /// </summary>
    [Fact]
    public async Task AClientBuyWithTheHostsOwnStoreGuiOpenNeverReInsertsWhatItJustBought()
    {
        // The live timings, reproduced. The host broadcasts its snapshot every 1000ms
        // (StoreV2BroadcastIntervalMs); its store scan cache is ~1s on the real bridge, and the
        // single-slot arbitrated cheat channel took SECONDS to land an apply. So: a scan window
        // just over one broadcast (1500ms) means the write-through's "0" reaches the client on
        // the next broadcast BEFORE any scan has run, and the scan after that still reads the
        // pre-apply "1" and republishes it — the regression. A 2400ms landing delay (800 ticks of
        // the 3ms loop) keeps the apply in flight across both. With the default 150ms/1-3-tick
        // settings the apply always lands first and none of this can happen.
        (Rig rig, _) = await BuildConnectedRigAsync(
            bridgeScanIntervalMs: 1500, cheatLandDelayMs: 3000, trace: true,
            hostHeavyPumpIntervalMs: 2000);
        using (rig)
        {
            // Both instances loaded the same module, so both hold the same pristine stock, and
            // BOTH have the shop open — the §15.5 condition.
            rig.HostAgent.Bridge.TrueStore[StorePristine] = 3;
            rig.HostAgent.Bridge.TrueStore[StoreBlaster] = 1;
            rig.ClientAgent.Bridge.TrueStore[StorePristine] = 3;
            rig.ClientAgent.Bridge.TrueStore[StoreBlaster] = 1;
            rig.HostAgent.Bridge.OpenStoreId = TestStoreId;
            rig.ClientAgent.Bridge.OpenStoreId = TestStoreId;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StoreBlaster) == 1
                        && t.GetValueOrDefault(StorePristine) == 3,
                    Timeout),
                "the client never received the shop's pristine stock");

            // Let both sides settle on that baseline before anybody buys anything.
            await Task.Delay(600);
            int clientAppliesBefore = Volatile.Read(ref rig.ClientAgent.StoreApplyLandings);

            // The buy: the client's own engine drops the item. The host's GUI stays OPEN.
            rig.ClientAgent.Bridge.TrueStore.Remove(StoreBlaster);

            // §15.5's wobble took ~5s from publish to the compensating remove. Sample the wire
            // from the buy onward, so a REGRESSION in what the host publishes (1 -> 0 -> 1) is
            // visible in the failure message rather than having to be inferred.
            List<string> wire = [];
            DateTime deadline = DateTime.UtcNow + Timeout;
            DateTime? settleUntil = null;
            string lastWire = "1";
            while (DateTime.UtcNow < deadline && (settleUntil is null || DateTime.UtcNow < settleUntil))
            {
                if (settleUntil is null
                    && (!rig.HostAgent.Bridge.TrueStore.ContainsKey(StoreBlaster)
                        || rig.HostAgent.Bridge.TrueStore[StoreBlaster] == 0))
                {
                    // The apply landed on the host. Keep watching for §15.5's own compensating
                    // remove, which came ~3s after the spurious insert.
                    settleUntil = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                }

                rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t);
                string nowWire = $"{t.GetValueOrDefault(StoreBlaster)}";
                if (nowWire != lastWire)
                {
                    wire.Add($"{DateTime.UtcNow:HH:mm:ss.fff} snap={nowWire}");
                    lastWire = nowWire;
                }

                await Task.Delay(10);
            }

            Assert.True(
                !rig.HostAgent.Bridge.TrueStore.ContainsKey(StoreBlaster)
                    || rig.HostAgent.Bridge.TrueStore[StoreBlaster] == 0,
                "the client's buy never reached the host's own engine");

            int clientApplies = Volatile.Read(ref rig.ClientAgent.StoreApplyLandings) - clientAppliesBefore;
            Assert.True(
                clientApplies == 0,
                $"the client applied {clientApplies} engine write(s) of its own for a buy it "
                + "performed itself — §15.5's re-insert. Client store now: "
                + string.Join(
                    ", ",
                    rig.ClientAgent.Bridge.TrueStore.Select(kv => $"{kv.Key.Resref}={kv.Value}"))
                + "\nWIRE:\n" + string.Join("\n", wire)
                + "\nCLIENT:\n"
                + string.Join("\n", rig.ClientAgent.Logs.Where(l => l.Contains("store")))
                + "\nHOST:\n"
                + string.Join("\n", rig.HostAgent.Logs.Where(l => l.Contains("store"))));

            Assert.False(rig.ClientAgent.Bridge.TrueStore.ContainsKey(StoreBlaster));
            Assert.Equal(3, rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StorePristine));
            Assert.Equal(3, rig.HostAgent.Bridge.TrueStore.GetValueOrDefault(StorePristine));
        }
    }

    [Fact]
    public async Task BlackoutThenReconnectConvergesWithNoDoubleApplyOrMisroute()
    {
        // The direct regression pin for docs/research/store-sync.md's own "blackout+reconnect
        // resync double-applies a mirrored sell, misrouting the duplicate into the client's own
        // inventory" finding — see docs/research/shared-inventory-v2.md §2.7's structural
        // argument for why this cannot recur under v2.
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 5;
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 5, Timeout));

            uint originalId = rig.Client.LocalPeerId;
            rig.Client.Network.StartBlackout(6_000);

            Assert.True(
                await WaitForAsync(() => !rig.Client.IsConnected, TimeSpan.FromSeconds(10)),
                "client never noticed the blackout");
            Assert.True(
                await WaitForAsync(() => rig.Host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(10)),
                "host never expired the blacked-out peer");

            // A further host-side change WHILE the client is dark — the reconnecting client's
            // very first snapshot cycle must already carry this, with no separate resync
            // mechanism to get wrong.
            rig.HostAgent.Bridge.TruePartyPool[Blaster] = 8;

            Assert.True(
                await WaitForAsync(() => rig.Client.IsConnected, TimeSpan.FromSeconds(30)),
                "client never rejoined after the blackout ended");
            Assert.Equal(originalId, rig.Client.LocalPeerId);

            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 8, Timeout),
                "reconnecting client never converged to the host's latest total");

            Assert.True(
                await StaysAsync(
                    () => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 8
                        && rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 8,
                    TimeSpan.FromSeconds(5)),
                "no double-apply, no misroute — both sides must settle at exactly 8 and stay there");
        }
    }

    // ---- §7 (2026-08-29): regression coverage for the defect this pass found and fixed —
    // see docs/research/shared-inventory-v2.md §7 for the full write-up. ------------------------

    /// <summary>
    /// item 4's own host-only harness: a host with NO connected peer that ever publishes
    /// anything, whose engine receives a fresh "ambient" party-pool change every pump tick.
    /// <c>PumpHost</c> must do exactly two things — scan and publish — and NEVER queue an engine
    /// apply of its own; <see cref="AgentLoopV2.PoolApplyDequeueCount"/> must stay exactly 0 for
    /// the entire run, while a bare observer (a connected <see cref="CoopClient"/> with no
    /// <see cref="AgentLoopV2"/> of its own, so nothing on that side ever reconciles or publishes
    /// anything back) still sees the snapshot track every ambient change, proving the publish
    /// half keeps working.
    /// </summary>
    [Fact]
    public async Task HostOnlyPartyPoolAmbientChurnNeverQueuesAHostSideEngineApply()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient observer = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        _ = host.RunAsync(cts.Token);
        _ = observer.RunAsync(cts.Token);

        using AgentLoopV2 hostAgent = new(host, bridgeScanIntervalMs: 50);

        try
        {
            Assert.True(
                await WaitForAsync(() => observer.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "observer never connected");

            for (int i = 1; i <= 40; i++)
            {
                hostAgent.Bridge.TruePartyPool[Thermal] = i;
                await Task.Delay(20);

                Assert.True(hostAgent.PoolApplyDequeueCount == 0,
                    $"host queued a party-pool engine apply of its own after {i} pure-ambient "
                    + "ticks with no client ever connected — a host must only ever "
                    + "ApplyClientIntent(serial), never anything derived from its own scan");
            }

            Assert.True(
                await WaitForAsync(
                    () => observer.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> t)
                        && t.GetValueOrDefault(Thermal) == 40,
                    Timeout),
                "the observer never saw the host's snapshot track the ambient churn to its final value");

            Assert.Equal(0, hostAgent.PoolApplyDequeueCount);
        }
        finally
        {
            host.Dispose();
            observer.Dispose();
        }
    }

    /// <summary>Store's mirror of <see cref="HostOnlyPartyPoolAmbientChurnNeverQueuesAHostSideEngineApply"/>.</summary>
    [Fact]
    public async Task HostOnlyStoreAmbientChurnNeverQueuesAHostSideEngineApply()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient observer = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        _ = host.RunAsync(cts.Token);
        _ = observer.RunAsync(cts.Token);

        using AgentLoopV2 hostAgent = new(host, bridgeScanIntervalMs: 50);
        hostAgent.Bridge.OpenStoreId = TestStoreId;

        try
        {
            Assert.True(
                await WaitForAsync(() => observer.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "observer never connected");

            for (int i = 1; i <= 40; i++)
            {
                hostAgent.Bridge.TrueStore[StoreBlaster] = i;
                await Task.Delay(20);

                Assert.True(hostAgent.StoreApplyDequeueCount == 0,
                    $"host queued a store engine apply of its own after {i} pure-ambient ticks "
                    + "with no client ever connected");
            }

            Assert.True(
                await WaitForAsync(
                    () => observer.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StoreBlaster) == 40,
                    Timeout),
                "the observer never saw the host's store snapshot track the ambient churn");

            Assert.Equal(0, hostAgent.StoreApplyDequeueCount);
        }
        finally
        {
            host.Dispose();
            observer.Dispose();
        }
    }

    /// <summary>
    /// item 4's two-pump harness: the REAL host/client role split, with the CLIENT's engine
    /// (never the host's) scripted with repeated ambient-style bumps, one every ~120ms — the
    /// exact recurring-noise shape the live rig round's own doubling symptom looked like. Totals
    /// on both sides must land on exactly the final value the client's engine reached (no
    /// amplification across many rounds, not just one), and the host's own apply count must stay
    /// tightly bounded to roughly the number of distinct client-detected deltas, never anything
    /// exponential.
    /// </summary>
    [Fact]
    public async Task RepeatedClientAmbientChurnConvergesExactlyWithBoundedHostApplies()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            const int Rounds = 10;
            for (int i = 1; i <= Rounds; i++)
            {
                rig.ClientAgent.Bridge.TruePartyPool[Thermal] = i;
                await Task.Delay(120);
            }

            string Diagnostics() =>
                $"\nHOST LOG:\n{string.Join('\n', rig.HostAgent.Logs)}"
                + $"\nCLIENT LOG:\n{string.Join('\n', rig.ClientAgent.Logs)}";

            Assert.True(
                await WaitForAsync(
                    () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == Rounds, Timeout),
                $"host never converged to exactly {Rounds}; got "
                + $"{rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}{Diagnostics()}");

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == Rounds, Timeout),
                $"client's own engine never settled at exactly {Rounds}{Diagnostics()}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == Rounds
                        && rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal) == Rounds,
                    TimeSpan.FromSeconds(5)),
                $"both sides must STAY at exactly {Rounds} across repeated ambient-style churn — "
                + $"no amplification, ever. host={rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)} "
                + $"client={rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Thermal)}{Diagnostics()}");

            // Bounded, never exponential: 10 genuinely distinct deltas should produce, at most, a
            // small multiple of 10 host-side applies — a doubling defect of the kind this pass
            // fixed would blow this bound by orders of magnitude within the same run.
            Assert.True(rig.HostAgent.PoolApplyDequeueCount <= Rounds * 3,
                $"host applied {rig.HostAgent.PoolApplyDequeueCount} times for only {Rounds} "
                + "distinct client-side deltas — not bounded");
        }
    }

    /// <summary>
    /// item 4's own explicit requirement: a client buy-back intent must reach the host and apply
    /// EXACTLY once — never a second time once the snapshot has caught up and the client's own
    /// outstanding entry has been retired. Extends
    /// <see cref="StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost"/>'s own scenario
    /// with an explicit apply-count assertion, held stable for several seconds after landing.
    /// </summary>
    [Fact]
    public async Task ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.OpenStoreId = TestStoreId;
            rig.HostAgent.Bridge.TrueStore[StoreBlaster] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StoreBlaster) == 1,
                    Timeout),
                "client never learned of the host's sell");

            rig.ClientAgent.Bridge.OpenStoreId = TestStoreId;
            Assert.True(
                await WaitForAsync(() => rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StoreBlaster) == 1, Timeout),
                "client's own engine never reconciled up to the host's mirrored sell");

            // §9 (2026-08-29): see StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost's
            // identical remarks — gives this pump's first live scan of the now-open GUI a genuine
            // chance to run before the buy-back yanks the item away again.
            await Task.Delay(300);

            int applyCountBeforeBuyBack = rig.HostAgent.StoreApplyLandings;

            rig.ClientAgent.Bridge.TrueStore.Remove(StoreBlaster);
            rig.HostAgent.Bridge.OpenStoreId = 0;

            Assert.True(
                await WaitForAsync(
                    () => !rig.HostAgent.Bridge.TrueStore.ContainsKey(StoreBlaster)
                        || rig.HostAgent.Bridge.TrueStore[StoreBlaster] == 0,
                    Timeout),
                "the client's buy-back never reached the host's own engine");

            Assert.True(
                await WaitForAsync(
                    () => rig.HostAgent.StoreApplyLandings == applyCountBeforeBuyBack + 1, Timeout),
                $"expected exactly one new host-side store apply for the buy-back; got "
                + $"{rig.HostAgent.StoreApplyLandings - applyCountBeforeBuyBack} so far");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.StoreApplyLandings == applyCountBeforeBuyBack + 1,
                    TimeSpan.FromSeconds(5)),
                "the buy-back must never apply to the host's engine a second time");
        }
    }

    /// <summary>
    /// item 3/4: a store the HOST has never opened this session is UNKNOWN, not zero — a client
    /// with real, pristine stock in such a store must never report it as a fabricated "local
    /// deposit", and the client's own engine must stay untouched. The host's
    /// <c>OpenStoreId</c> is never set to <see cref="UnknownStoreId"/> anywhere in this test.
    /// </summary>
    [Fact]
    public async Task ClientStoreNeverOpenedByHostStaysUnknownAndUntouched()
    {
        const uint UnknownStoreId = 0x99;
        StoreStackKey unknownKey = new(UnknownStoreId, "g_w_thermldet01", 0, 0, 0, ItemIdentity.FlagKnown);

        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.ClientAgent.Bridge.TrueStore[unknownKey] = 7;
            rig.ClientAgent.Bridge.OpenStoreId = UnknownStoreId;

            // Plenty of ticks for the client's own pump to have mis-detected this as a local
            // deposit against a false zero baseline, if it were going to.
            await Task.Delay(2_500);

            Assert.False(
                rig.Client.IsStoreKnownV2(UnknownStoreId),
                "the client must never learn a store is known without the host ever having named it");

            Assert.False(
                rig.Host.IsStoreKnownV2(UnknownStoreId),
                "the host must never come to know a store solely from a client's own local scan");

            Assert.Equal(7, rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(unknownKey));

            Assert.True(
                await StaysAsync(
                    () => rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(unknownKey) == 7,
                    TimeSpan.FromSeconds(3)),
                "an absent-on-host store's client-side stock must stay untouched, never reconciled "
                + "down against a false zero baseline nor reported as a phantom deposit");

            Assert.Equal(0, rig.HostAgent.StoreApplyDequeueCount);
        }
    }

    // ---- §9 (2026-08-29): regression coverage for the two defects this pass found and fixed —
    // see docs/research/shared-inventory-v2.md §9 for the full write-up. --------------------------

    /// <summary>
    /// Item 1's own explicit repro/fix requirement: docs/research/shared-inventory-v2.md §8.1's
    /// live-observed defect — a client's own reconcile-apply, converging its engine toward a
    /// HOST-DRIVEN change, gets mis-detected by the client's OWN detect step as a fresh LOCAL
    /// action and re-published as a spurious intent, permanently diverging the host upward by the
    /// re-published amount. The client here never touches this key itself at all — every
    /// movement it ever observes on its own engine is caused entirely by its own reconcile
    /// applies converging toward the host's snapshot — across several rounds, so its detect step
    /// must NEVER report a fresh intent for it. Relies on <see cref="AgentLoopV2"/>'s tick-based
    /// asynchronous cheat channel (this pass's own harness change, replacing a wall-clock delay
    /// that rarely interleaved detect passes tightly enough against an in-flight reconcile to
    /// exercise this race) to genuinely run several detect passes WHILE each reconcile is in
    /// flight, including the exact tick it lands.
    ///
    /// <b>Honest reproduction note (§9, 2026-08-29)</b>: this is a genuine race, not a
    /// deterministic one — re-run several times against the pre-§9 code (`_reconcileInFlight` +
    /// an absolute `snapshot + outstanding` detect target), it failed with the client's own
    /// engine overshooting the expected total (observed: expected 12, got 14 — a spurious extra
    /// report landing on the host) on SOME runs and passed cleanly on others, exactly the kind of
    /// timing-dependent defect the live rig itself only caught once per session in §8.1's own
    /// round. Against the fixed (post-§9) code below, it has passed cleanly on every run tried.
    /// Kept as a real regression guard for the mechanism either way — it is the closest a
    /// low-latency loopback harness gets to §8.1's own live timing without literally reintroducing
    /// wall-clock jitter, and a passing run here is meaningful even though a passing run against
    /// the OLD code would not have been surprising on its own.
    /// </summary>
    [Fact]
    public async Task ClientReconcileLandingIsNeverReDetectedAsASpuriousIntent()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // §8.1's own live precondition, reproduced here: the client's OWN outstanding ledger
            // for this key is already in play (a genuine local deposit it detected and published
            // itself) BEFORE the host-driven reconciles start — the exact combination the pre-§9
            // design's detect step (an ABSOLUTE `snapshot + outstanding` target) could read as a
            // fresh local change the moment a same-key reconcile's own exclusion window closed,
            // even though nothing genuinely new happened. A test that never touches the client's
            // own outstanding ledger for this key at all (this test's own first version) does not
            // exercise that interaction and passes even against the pre-§9 code.
            rig.ClientAgent.Bridge.TruePartyPool[Blaster] = 2;

            // Wait for the HOST to confirm receipt of the client's own +2 before starting the
            // host-driven rounds below — this fake bridge's own host-side writes below are
            // ABSOLUTE assignments (simulating ambient host-side content independent of any
            // client report), while the host's own ACCEPT-INTENT path applies a client's report
            // as a DELTA on top of whatever `TruePartyPool` currently holds
            // (`AgentLoopV2.Land`'s `after = before + count`); racing an absolute host-side write
            // against that still-in-flight delta apply double-counts by TEST CONSTRUCTION, not by
            // any pump defect — confirmed by reproducing it first against the FIXED pump code too
            // (a version of this test that omitted this wait failed identically either way).
            // Deliberately NOT waiting any LONGER than that confirmation, though — the client's
            // own outstanding entry for its +2 is still genuinely fresh (unacked/unretired) the
            // moment the FIRST host-driven reconcile lands, maximising overlap between the two
            // ledgers §8.1's own mechanism hinges on. Ten rapid host-driven rounds, fired
            // back-to-back with no per-round wait (mirroring §27/
            // ThreeRapidHostChangesReachClientExactlyViaSnapshotAlone's own "no wait between
            // them" recipe), each landing while the client's own reconcile chain for an earlier
            // round may still be draining through the single-slot cheat channel.
            Assert.True(
                await WaitForAsync(() => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == 2, Timeout),
                "host never learned of the client's own initial deposit");

            int running = 2;
            foreach (int bump in new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 })
            {
                running += bump;
                rig.HostAgent.Bridge.TruePartyPool[Blaster] = running;
            }

            Assert.True(
                await WaitForAsync(() => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == running, Timeout),
                $"host never converged to exactly {running}; got "
                + $"{rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == running, Timeout),
                $"client's own engine never reconciled up to {running}; got "
                + $"{rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster)}");

            // A full window for any lingering in-flight reconcile to either land cleanly or, if
            // the old bug were still present, get mis-detected and re-published — pushing the
            // host's own total past `running`.
            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == running
                        && rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster) == running,
                    TimeSpan.FromSeconds(6)),
                $"both sides must STAY at exactly {running} — a client re-detecting its own "
                + "reconcile landing as a fresh local action would diverge the host upward past "
                + $"this. Host={rig.HostAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster)} "
                + $"Client={rig.ClientAgent.Bridge.TruePartyPool.GetValueOrDefault(Blaster)}");

            // Only the client's own initial +2 is ever a genuine local action — every later
            // movement is caused entirely by its own reconcile converging toward the host's
            // snapshot, so at most ONE "local ... detected" line for this key is ever legitimate.
            string[] spurious = [.. rig.ClientAgent.Logs.Where(l =>
                l.Contains("party pool: local", StringComparison.Ordinal)
                && l.Contains($"'{Blaster.Resref}'", StringComparison.Ordinal))];

            Assert.True(spurious.Length <= 1,
                $"the client made exactly ONE genuine local change to '{Blaster.Resref}' itself "
                + "(the initial +2) — every later movement it observed was its own reconcile "
                + $"converging toward the host's snapshot — yet its detect step published "
                + $"{spurious.Length} local-change report(s) for it:\n{string.Join('\n', spurious)}");
        }
    }

    /// <summary>
    /// Item 2's own explicit repro/fix requirement: <c>Odyssey.Store.cs</c>'s
    /// <c>InsertItemIntoStore</c>/<c>RemoveStoreStack</c> (the primitives the <c>StoreInsert</c>/
    /// <c>StoreRemove</c> cheats drive) resolve their target store purely by object id
    /// (<c>GetStoreByGameObjectID</c>) — they need no GUI open at all. Only READING a store
    /// (<c>TryScanOpenStore</c>) needs the GUI open. A host sell must therefore still reach the
    /// client's own engine even though the client's store GUI is NEVER opened for the whole
    /// test — the pre-§9 GUI-gated early return in <c>StorePumpV2.PumpClient</c> used to skip
    /// reconcile ENTIRELY whenever no store was open on the client, which this test would have
    /// caught (see <see cref="StoreHostSellMirrorsToClientAndClientBuyBackReachesTheHost"/> for
    /// the pre-existing scenario this narrows by never opening the client's GUI at all).
    /// </summary>
    [Fact]
    public async Task StoreHostSellMirrorsToClientEvenWithTheClientsGuiClosed()
    {
        (Rig rig, _) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // Both engines hold the shop's own pristine stock, because both loaded the same
            // module — establish that baseline first (§12), then sell into it. Without this the
            // host's sell IS the store's first-ever snapshot, which is indistinguishable from
            // pristine content and correctly seeds rather than mirrors.
            rig.HostAgent.Bridge.TrueStore[StorePristine] = 4;
            rig.ClientAgent.Bridge.TrueStore[StorePristine] = 4;
            rig.HostAgent.Bridge.OpenStoreId = TestStoreId;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StorePristine) == 4,
                    Timeout),
                "client never learned the shop exists at all");

            await Task.Delay(300);

            rig.HostAgent.Bridge.TrueStore[StoreBlaster] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.Client.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> t)
                        && t.GetValueOrDefault(StoreBlaster) == 1,
                    Timeout),
                "client never learned of the host's sell");

            // rig.ClientAgent.Bridge.OpenStoreId is NEVER set anywhere in this test — the
            // client's own store GUI stays closed for the whole run.
            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StoreBlaster) == 1, Timeout),
                "client's own engine never reconciled up to the host's mirrored sell despite its "
                + "GUI staying closed the whole test — reconcile must never need the GUI open, "
                + "only detecting a fresh LOCAL change does");

            Assert.True(
                await StaysAsync(
                    () => rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StoreBlaster) == 1,
                    TimeSpan.FromSeconds(4)),
                "the client's engine must STAY at exactly 1 with its GUI closed the whole time");

            // ...and the shop's own pristine stock must not have been touched either way — a
            // blind reconcile that "converges" content both sides already agreed on is the
            // §10.2 defect, not the feature.
            Assert.Equal(4, rig.ClientAgent.Bridge.TrueStore.GetValueOrDefault(StorePristine));
            Assert.Equal(4, rig.HostAgent.Bridge.TrueStore.GetValueOrDefault(StorePristine));

            Assert.Equal(0u, rig.ClientAgent.Bridge.OpenStoreId);
        }
    }
}
