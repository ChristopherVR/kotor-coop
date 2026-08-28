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

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly Random _random = new();

        private long _cheatLandsAtMs = -1;
        private CoopAgent.PendingPartyPoolApply? _inFlightPool;
        private CoopAgent.PendingStoreApply? _inFlightStore;

        public AgentLoopV2(ICoopSession session, int bridgeScanIntervalMs, int heavyPumpIntervalMs = 0)
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
                    long now = Environment.TickCount64;

                    if (_cheatLandsAtMs >= 0)
                    {
                        if (now >= _cheatLandsAtMs)
                        {
                            Land();
                            _cheatLandsAtMs = -1;
                            _inFlightPool = null;
                            _inFlightStore = null;
                        }
                    }
                    else if (Agent.TryDequeuePendingPartyPoolApplyV2(out CoopAgent.PendingPartyPoolApply poolNext))
                    {
                        _inFlightPool = poolNext;
                        _cheatLandsAtMs = now + 30 + _random.Next(220);
                    }
                    else if (Agent.TryDequeuePendingStoreApplyV2(out CoopAgent.PendingStoreApply storeNext))
                    {
                        _inFlightStore = storeNext;
                        _cheatLandsAtMs = now + 30 + _random.Next(220);
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

    private static async Task<(Rig Rig, CancellationTokenSource Cts)> BuildConnectedRigAsync()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(90));
        _ = host.RunAsync(cts.Token);
        _ = client.RunAsync(cts.Token);

        AgentLoopV2 hostAgent = new(host, bridgeScanIntervalMs: 150);
        AgentLoopV2 clientAgent = new(client, bridgeScanIntervalMs: 150);

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
}
