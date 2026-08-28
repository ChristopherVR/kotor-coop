using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump loop on each side —
/// the full production shape for the shared party item-pool channel
/// (docs/research/party-pool-sync.md), minus only the actual game process (a
/// <see cref="FakeBridge"/> stands in for <c>KotorGameBridge</c>, reproducing its own
/// PartyPool 1-second scan-result cache faithfully, just on a short test-controlled interval).
///
/// Root cause 1 (PartyPoolPump re-detecting its own just-landed apply as a fresh local action,
/// doubling the RECEIVING side) is fixed — see PartyPoolPump.cs's own doc comment on the
/// reordered block, and the "before this fix, all four of these tests fail" evidence recorded
/// in docs/research/party-pool-sync.md §8. These tests also deliberately stress the leading
/// (self-described "unconfirmed") hypothesis for the SEPARATE, still-open "depositing side"
/// duplicate that same live round reported — <c>KotorGameBridge.PartyPool.cs</c>'s 1-second
/// scan-result cache interacting with this pump's per-tick tracking, reproduced here via
/// <see cref="FakeBridge"/> plus a simulated single-slot cheat round trip in
/// <see cref="AgentLoop"/> — and did NOT reproduce it. See docs/research/party-pool-sync.md §8
/// for the full write-up of both results and what is still open.
///
/// Root cause 3 (docs/research/party-pool-sync.md §11/§12, 2026-08-28): the OPPOSITE defect —
/// rapid same-key local changes silently under-counted, and a landed relay racing a further
/// local change on the same key could lock it out of sync indefinitely — both caused by the
/// same blanket "exclude this key from detection for up to 2s" guard that fixed root cause 2.
/// <see cref="ThreeRapidSameKeyLocalDetectionsAreAllPublishedNotSwallowed"/>,
/// <see cref="ADepositImmediatelyFollowedByAWithdrawalOfTheSameKeyNetsToZeroNotSwallowed"/> and
/// <see cref="APeerApplyLandingInTheSameTickAsAGenuineLocalDepositOfTheSameKeyCountsBothAsTwo"/>
/// are the regression pins for the three under-count/lockout shapes found live; the guard is now
/// count-aware (compare against the expected delta a publish/queued-apply owes, not a blanket
/// key exclusion) — see <c>PartyPoolPump.cs</c>'s own doc comment.
/// </summary>
public class PartyPoolLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly PartyPoolStackKey Blaster =
        new("g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private const string Module = "end_m01aa";

    /// <summary>
    /// Stands in for <c>KotorGameBridge.PartyPool.cs</c>: <see cref="TrueEngineState"/> is
    /// exactly what a human's drag-and-drop (or a landed cheat-channel apply) would leave in
    /// <c>CItemRepository</c> — but <see cref="TryScanPartyPoolTotals"/> NEVER hands the pump
    /// that live truth directly. It reproduces the real bridge's own throttle line for line
    /// (<c>KotorGameBridge.PartyPool.cs</c>'s <c>_nextPartyPoolScanTicks</c>/
    /// <c>_partyPoolLastScan</c> fields): a fresh scan snapshots <see cref="TrueEngineState"/>
    /// once, then every call within <paramref name="scanIntervalMs"/> of it hands back that
    /// SAME stale snapshot again, no matter how many times the caller asks. Short interval
    /// (milliseconds, not the real 1000) purely so the test does not need to wait out a real
    /// wall-clock second per scan; the mechanism under test is the caching, not its duration.
    /// </summary>
    private sealed class FakeBridge(int scanIntervalMs) : IGameBridge
    {
        public Dictionary<PartyPoolStackKey, int> TrueEngineState { get; } = [];

        private Dictionary<PartyPoolStackKey, int> _lastScan = [];
        private bool _everScanned;
        private long _nextScanTicks;

        public string Description => "party-pool-loopback-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartyPool => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            long now = Environment.TickCount64;
            if (_everScanned && now < _nextScanTicks)
            {
                totals = _lastScan;
                return true;
            }

            _nextScanTicks = now + scanIntervalMs;
            _lastScan = new Dictionary<PartyPoolStackKey, int>(TrueEngineState);
            _everScanned = true;
            totals = _lastScan;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A DETERMINISTIC stand-in for <c>KotorGameBridge.PartyPool.cs</c>'s own throttled scan
    /// cache, used only by
    /// <see cref="ASecondRapidLocalChangeIsNotMaskedBehindTheRestOfAnAlreadyStaleScanWindow"/>:
    /// reproduces the real cache/throttle mechanism line for line, exactly like
    /// <see cref="FakeBridge"/>, but driven by a test-controlled <see cref="Clock"/> field instead
    /// of <c>Environment.TickCount64</c> and a real timer — so a test can advance "time" by exact,
    /// tiny increments deterministically, with no real thread, no <c>Task.Delay</c>, and no risk
    /// of a background pump tick consuming the fix's one-shot invalidation before the test gets a
    /// chance to observe it (which real wall-clock timing could not rule out — see
    /// docs/research/party-pool-sync.md §14).
    /// </summary>
    private sealed class ThrottledFakeBridge(long scanIntervalTicks) : IGameBridge
    {
        public Dictionary<PartyPoolStackKey, int> TrueEngineState { get; } = [];

        /// <summary>The test-controlled "now" — advanced explicitly between <c>PumpOnce()</c>
        /// calls, never by real elapsed time.</summary>
        public long Clock;

        private Dictionary<PartyPoolStackKey, int> _lastScan = [];
        private bool _everScanned;
        private long _nextScanAt;

        public string Description => "party-pool-loopback-throttled-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartyPool => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            if (_everScanned && Clock < _nextScanAt)
            {
                totals = _lastScan;
                return true;
            }

            _nextScanAt = Clock + scanIntervalTicks;
            _lastScan = new Dictionary<PartyPoolStackKey, int>(TrueEngineState);
            _everScanned = true;
            totals = _lastScan;
            return true;
        }

        /// <summary>Mirrors <c>KotorGameBridge.PartyPool.cs</c>'s own fix exactly: forces the
        /// NEXT call above to bypass the throttle.</summary>
        public void InvalidatePartyPoolScanCache()
        {
            _nextScanAt = long.MinValue;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A DETERMINISTIC stand-in for the bridge, used only by
    /// <see cref="AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning"/>:
    /// each call to <see cref="TryScanPartyPoolTotals"/> returns the NEXT dictionary from a
    /// scripted sequence (no wall-clock throttling at all), so a test can drive
    /// <c>PartyPoolPump.Pump()</c> through an EXACT, reproducible sequence of engine-scan
    /// results — including a transient "the deposit briefly vanishes from the scan" glitch that
    /// a real timing-based rig could only ever hit by luck. Once the script is exhausted, the
    /// last entry repeats forever (a steady state, matching what a real converged engine would
    /// keep reporting).
    /// </summary>
    private sealed class ScriptedBridge(IReadOnlyList<Dictionary<PartyPoolStackKey, int>> script) : IGameBridge
    {
        private int _index;

        public string Description => "party-pool-loopback-scripted";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartyPool => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            Dictionary<PartyPoolStackKey, int> entry = script[Math.Min(_index, script.Count - 1)];
            if (_index < script.Count)
            {
                _index++;
            }

            totals = entry;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Drives one side's <see cref="CoopAgent"/> pump loop on a background task, at a much
    /// faster cadence than production (a few ms) and with <c>HeavyPumpIntervalMs = 0</c> so
    /// <see cref="Pumps.PartyPoolPump"/> runs on every tick — the worst case for the bridge-
    /// cache interaction under test, since it maximises how many pump ticks land inside a
    /// single bridge scan-cache window. After every <c>PumpOnce</c>, drains any queued
    /// automatic apply straight into the bridge's <see cref="FakeBridge.TrueEngineState"/> —
    /// the same job <c>CoopCommandDispatcher.PartyPool.cs</c>'s <c>DrainAutomaticPartyPool</c>
    /// does through the real cheat channel, just landing instantly instead of through a native
    /// engine call.
    /// </summary>
    private sealed class AgentLoop : IDisposable
    {
        public FakeBridge Bridge { get; }

        public CoopAgent Agent { get; }

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;

        /// <summary>
        /// False for a rig where a SEPARATE <see cref="DispatcherLoop"/> owns the single-slot
        /// cheat channel instead — this loop then does nothing but call <c>PumpOnce</c>, exactly
        /// like production's <c>CoopAgent.RunAsync</c>, which never itself drains
        /// <c>_pendingApplies</c> (that is <c>CoopCommandDispatcher</c>'s job, on its OWN
        /// independently-scheduled task). See <see cref="DispatcherLoop"/>'s own remarks for why
        /// that separation — not this class's original single-loop shape, which the existing
        /// four tests above still use — is what a HOST-originated <c>pooladd</c> repro needs.
        /// </summary>
        private readonly bool _autoDrain;

        public AgentLoop(
            ICoopSession session, int bridgeScanIntervalMs, int heavyPumpIntervalMs = 0, bool autoDrain = true)
        {
            Bridge = new FakeBridge(bridgeScanIntervalMs);
            Agent = new CoopAgent(
                Bridge,
                session,
                new AgentSettings { HeavyPumpIntervalMs = heavyPumpIntervalMs, PartyPoolSyncEnabled = true });
            _autoDrain = autoDrain;

            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>
        /// Set while a simulated cheat round-trip is "in flight" — the same single-slot
        /// arbitration <c>DrainAutomaticPartyPool</c>'s <c>_pendingCheatSerial != 0</c> gate
        /// provides in production: at most ONE dequeue happens per real drain opportunity, and
        /// nothing else drains until it "lands". <see cref="Pumps.PartyPoolPump"/> can still
        /// keep queueing MORE entries into its own <c>_pendingApplies</c> in the meantime
        /// (bounded by <c>MaxPendingApplies</c>) — exactly like production.
        /// </summary>
        private long _cheatLandsAtMs = -1;
        private CoopAgent.PendingPartyPoolApply _inFlight;
        private readonly Random _random = new();

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Agent.PumpOnce();

                    if (_autoDrain)
                    {
                        long now = Environment.TickCount64;

                        if (_cheatLandsAtMs >= 0)
                        {
                            // A cheat request is already "in flight" (the real shared-block round
                            // trip to the plugin and back) — nothing further drains until it
                            // lands, matching _pendingCheatSerial's real gate.
                            if (now >= _cheatLandsAtMs)
                            {
                                int before = Bridge.TrueEngineState.GetValueOrDefault(_inFlight.Key);
                                int after = _inFlight.Kind == PartyPoolOpKind.Add
                                    ? before + _inFlight.Count
                                    : Math.Max(0, before - _inFlight.Count);
                                Bridge.TrueEngineState[_inFlight.Key] = after;
                                _cheatLandsAtMs = -1;
                            }
                        }
                        else if (Agent.TryDequeuePendingPartyPoolApply(out CoopAgent.PendingPartyPoolApply next))
                        {
                            _inFlight = next;

                            // A real staging-cheat round trip is not instantaneous: the shared
                            // block is polled, the plugin processes it on the game's own thread,
                            // and (for an Add) the engine performs a genuine spawn-then-transfer.
                            // Modelled here as a randomised tens-to-low-hundreds-of-ms delay,
                            // deliberately uncorrelated with both the bridge's own scan-cache
                            // interval and the heavy-pump cadence, so the three clocks interleave
                            // the way three independently-scheduled real mechanisms would.
                            _cheatLandsAtMs = now + 30 + _random.Next(220);
                        }
                    }
                }
                catch (Exception)
                {
                    // A stopped session between ticks (disposed channel) is not this test's
                    // concern; the outer test asserts on final state, not on this loop's
                    // survival.
                }

                await Task.Delay(3, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Stands in for <c>CoopCommandDispatcher.RunAsync</c>'s OWN 150ms poll loop — a task
    /// scheduled ENTIRELY independently of <see cref="AgentLoop"/>'s (production: two separate
    /// <c>async</c> loops, <c>CoopAgent.RunAsync</c> and <c>CoopCommandDispatcher.RunAsync</c>,
    /// with no shared synchronization context — see <c>AgentComposition.cs</c>'s
    /// <c>dispatcher.RunAsync</c>/<c>SessionManager</c>'s <c>agent.RunAsync</c> call sites).
    ///
    /// This is the piece §8's own <c>AgentLoop</c> could not exercise: that harness folded the
    /// cheat round trip INTO the same loop iteration as <c>PumpOnce</c>, so <c>PartyPoolPump</c>'s
    /// step 1/step 2 and the cheat-drain always ran in the same strict order every tick, on one
    /// thread — a race between them was structurally impossible to observe. Here, the single-slot
    /// arbitrated cheat channel (<c>_pendingCheatSerial</c>'s real gate — a human's own console
    /// verb OR the automatic drain, never both in flight at once, a human's own request always
    /// preempting per <c>SendCheat</c>'s unconditional <c>_afterCheat.Clear()</c>) lives on this
    /// SEPARATE thread, racing freely against <see cref="AgentLoop"/>'s own
    /// <c>Agent.PumpOnce()</c> loop — exactly production's shape.
    /// </summary>
    private sealed class DispatcherLoop : IDisposable
    {
        private readonly CoopAgent _agent;
        private readonly FakeBridge _bridge;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;
        private readonly Random _random = new();
        private readonly Lock _gate = new();

        private long _cheatLandsAtMs = -1;
        private CoopAgent.PendingPartyPoolApply _inFlight;
        private CoopAgent.PendingPartyPoolApply? _humanRequest;

        public DispatcherLoop(CoopAgent agent, FakeBridge bridge)
        {
            _agent = agent;
            _bridge = bridge;
            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        /// <summary>The "human" types <c>pooladd &lt;resref&gt; [stack]</c> — reaches
        /// <see cref="CoopCommandDispatcher.SendCheat"/> DIRECTLY, never through
        /// <c>PartyPoolPump</c>'s own <c>_pendingApplies</c> queue (see
        /// <c>CoopCommandDispatcher.cs</c>'s <c>CoopCommandKind.PoolAdd</c> verb-table entry).
        /// Preempts whatever the automatic drain was doing, same as <c>SendCheat</c>'s own
        /// unconditional <c>_afterCheat.Clear()</c> — this test never exercises that overlap
        /// (nothing else is mid-command when it fires), but the single shared slot is modelled
        /// faithfully regardless.</summary>
        public void SubmitHumanPoolAdd(PartyPoolStackKey key, int count)
        {
            lock (_gate)
            {
                _humanRequest = new CoopAgent.PendingPartyPoolApply(PartyPoolOpKind.Add, key, (ushort)count);
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    long now = Environment.TickCount64;

                    if (_cheatLandsAtMs >= 0)
                    {
                        if (now >= _cheatLandsAtMs)
                        {
                            int before = _bridge.TrueEngineState.GetValueOrDefault(_inFlight.Key);
                            int after = _inFlight.Kind == PartyPoolOpKind.Add
                                ? before + _inFlight.Count
                                : Math.Max(0, before - _inFlight.Count);
                            _bridge.TrueEngineState[_inFlight.Key] = after;
                            _cheatLandsAtMs = -1;
                        }
                    }
                    else
                    {
                        CoopAgent.PendingPartyPoolApply? human;
                        lock (_gate)
                        {
                            human = _humanRequest;
                            _humanRequest = null;
                        }

                        if (human is { } h)
                        {
                            _inFlight = h;
                            _cheatLandsAtMs = now + 30 + _random.Next(220);
                        }
                        else if (_agent.TryDequeuePendingPartyPoolApply(out CoopAgent.PendingPartyPoolApply next))
                        {
                            _inFlight = next;
                            _cheatLandsAtMs = now + 30 + _random.Next(220);
                        }
                    }
                }
                catch (Exception)
                {
                    // Same "outer test asserts on final state" tolerance as AgentLoop's own loop.
                }

                // CoopCommandDispatcher.RunAsync's real cadence — see its own PeriodicTimer.
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Deposits stay converged, not just momentarily correct: holds the condition for
    /// a further stretch of real time (several bridge-cache windows and several heavy-pump
    /// ticks on both sides) so a duplicate that appears one or two windows AFTER the first
    /// convergence — exactly the shape a cache-interaction bug would take — cannot slip past a
    /// check that only samples once.</summary>
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

    private readonly record struct Rig(
        CoopHost Host, CoopClient Client, AgentLoop HostAgent, AgentLoop ClientAgent) : IDisposable
    {
        public void Dispose()
        {
            HostAgent.Dispose();
            ClientAgent.Dispose();
            Host.Dispose();
            Client.Dispose();
        }
    }

    private static async Task<(Rig Rig, CancellationTokenSource Cts, Task HostTask, Task ClientTask)>
        BuildConnectedRigAsync()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        // Bridge-cache interval (150ms) deliberately shorter than the AgentLoop's own 3ms pump
        // cadence's period is NOT the point — the point is that it is UNCORRELATED with
        // anything the wire does, exactly like the real bridge's 1000ms throttle is
        // uncorrelated with HeavyPumpIntervalMs's own 1000ms in production (two independent
        // ~1Hz clocks, not one gating the other).
        AgentLoop hostAgent = new(host, bridgeScanIntervalMs: 150);
        AgentLoop clientAgent = new(client, bridgeScanIntervalMs: 150);

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        return (new Rig(host, client, hostAgent, clientAgent), cts, hostTask, clientTask);
    }

    [Fact]
    public async Task HostDepositConvergesToExactlyOneCopyOnBothSides()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            // Let both sides establish their baseline scan (empty pool) before the "human"
            // deposits, exactly like PartyPoolPump's own first-scan-is-silent rule.
            await Task.Delay(300);

            // The human drags 5 into the party stash on the HOST's screen.
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 5;

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"host never recorded its own deposit; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"client never converged on the host's deposit; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            // The load-bearing assertion: it must STAY at 5 - not creep to 10 a cache window
            // or two later, on EITHER side.
            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5,
                    TimeSpan.FromSeconds(8)),
                $"a deposit doubled after first converging - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            // And the DEPOSITING side's own engine (the FakeBridge standing in for the human's
            // game) must show exactly one converged stack too - never a second, independently
            // spawned copy from a queued apply this instance wrongly owed itself.
            Assert.Equal(5, rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster));

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task ClientDepositConvergesToExactlyOneCopyOnBothSides()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // The human drags 5 into the party stash on the CLIENT's screen this time -
            // symmetry check, no host/client branch anywhere in this channel per the design doc.
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 5;

            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"client never recorded its own deposit; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"host never converged on the client's deposit; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5,
                    TimeSpan.FromSeconds(8)),
                $"a deposit doubled after first converging - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.Equal(5, rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster));

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task ADepositSurvivesALateHostAckWithoutDoublingEitherSide()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // Drop everything the client RECEIVES for a stretch right as it deposits - the
            // host's ack (and the host's self-relay echo) go missing, forcing
            // ReliablePartyPoolSender to retransmit the op from BOTH ends before the ack
            // finally lands. A one-sided, timed blackout (not a manual SetLoss/reset pair) so
            // it ends on its own even if this test never gets back around to clearing it.
            rig.Client.Network.StartBlackout(700, NetSimDirection.Inbound);

            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 5;

            // Long enough for the blackout to end and several retransmit-backoff rounds
            // (250ms base interval) to have fired while the client was deaf to them.
            await Task.Delay(900);

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"host never converged after the late ack; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout),
                $"client never converged after the late ack; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5,
                    TimeSpan.FromSeconds(8)),
                $"the retransmit storm doubled the deposit - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task AReconnectAfterADepositDoesNotDoubleIt()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 5;

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout));
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5, Timeout));

            uint originalId = rig.Client.LocalPeerId;

            // A real blackout past the peer timeout - the client drops off the host's roster
            // entirely and has to rejoin, which is exactly HandleHello's resync-on-(re)admission
            // path (ResyncPartyPool, §2.5) firing again for an already-converged stack.
            rig.Client.Network.StartBlackout(10_000);

            Assert.True(
                await WaitForAsync(() => !rig.Client.IsConnected, TimeSpan.FromSeconds(10)),
                "client never noticed the blackout");
            Assert.True(
                await WaitForAsync(() => rig.Host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(10)),
                "host never expired the blacked-out peer");

            Assert.True(
                await WaitForAsync(() => rig.Client.IsConnected, Timeout),
                "client never rejoined after the blackout ended");
            Assert.Equal(originalId, rig.Client.LocalPeerId);

            Assert.True(
                await WaitForAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5,
                    Timeout),
                $"resync did not converge - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 5
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 5,
                    TimeSpan.FromSeconds(8)),
                $"the reconnect resync doubled the deposit - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// System-level confidence (real sockets, real store/sender/relay) that rapid same-key
    /// deposits converge correctly end to end: host deposits three times, then — once
    /// converged — the client deposits three more of the same key, and both sides reach +6.
    /// The writes are spaced 300ms apart, comfortably past the bridge's own 150ms scan cache
    /// (so each is independently observed) but that is ALSO comfortably longer than a step-1
    /// entry's own near-instant self-confirm cycle under either the old or new pump logic — so,
    /// unlike <see cref="ThreeRapidSameKeyLocalDetectionsAreAllPublishedNotSwallowed"/> below,
    /// this test does not reliably land INSIDE the old exclusion window and is not itself a
    /// regression pin for the bug (it passes against the pre-fix code too — the old guard's
    /// exclusion is only a few milliseconds wide in practice for a self-detected key with
    /// nothing else touching it, since the very next tick's scan already reconfirms and clears
    /// it). It stays in the suite as end-to-end assurance that the store/wire/relay layers this
    /// fix does not touch keep working when the underlying detection mechanism changes.
    /// </summary>
    [Fact]
    public async Task RapidSameKeyDepositsOnEitherSideAreAllCountedNotSwallowed()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            // Three rapid deposits on the HOST - each spaced past the bridge's 150ms scan cache
            // (so each is independently observed) but all well inside the old 2000ms grace
            // window that used to exclude a pending key from further detection entirely.
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 1;
            await Task.Delay(300);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 2;
            await Task.Delay(300);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 3;

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"host under-counted its own rapid deposits; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"client under-counted the host's rapid deposits; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3,
                    TimeSpan.FromSeconds(5)),
                $"rapid deposits drifted after converging - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            // Symmetry: the CLIENT now deposits three more of the SAME key. The client's own
            // engine already sits at 3 (reconciled from the host's deposits above), so this
            // pushes it to 6.
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 4;
            await Task.Delay(300);
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 5;
            await Task.Delay(300);
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 6;

            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 6, Timeout),
                $"client under-counted its own rapid deposits; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 6, Timeout),
                $"host under-counted the client's rapid deposits; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 6
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 6,
                    TimeSpan.FromSeconds(5)),
                $"rapid deposits drifted after converging - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    // ---- root cause 4: the unbounded ping-pong (docs/research/party-pool-sync.md §21-§22) ---

    /// <summary>Ceiling for the two "converges with bounded ops" tests below — the task's own
    /// ask is "≤ ~6" for three deposits; a small margin is kept here for the odd extra
    /// excess-driven op a real overlapping-tick race can legitimately produce, while staying a
    /// world away from §21's observed 615/1068/906/1938.</summary>
    private const uint MaxOpsForThreeDeposits = 8;

    private const uint MaxOpsForSixDeposits = 16;

    /// <summary>
    /// THE root cause 4 reproduction. Three rapid same-key HOST deposits, spaced far tighter
    /// (15ms) than both the bridge's own 150ms scan-cache window and the simulated cheat-
    /// channel apply latency (30-250ms — see <see cref="AgentLoop"/>'s own remarks), so the
    /// client's own catch-up apply for the first deposit is still very likely in flight when
    /// the second and third land — exactly the overlap docs/research/party-pool-sync.md §21's
    /// live rig hit with three rapid <c>pooladd</c>s of the same resref.
    ///
    /// Against the pre-fix design (a per-key <c>_pendingApplyTarget</c> VALUE captured once
    /// when an apply was queued and never revisited) this either fails to converge to 3 within
    /// the timeout, or does so only after publishing far more than a handful of ops — see
    /// <c>PartyPoolPump.cs</c>'s own "root cause 4" class remarks for exactly why a frozen
    /// snapshot compared against a wire that keeps moving amplifies instead of converging. The
    /// fix (<c>engineBelief</c>/<c>wireTotal</c>, both re-read fresh every tick, plus the hard
    /// loop-breaker backstop) converges through a small, bounded number of ops instead.
    /// </summary>
    [Fact]
    public async Task RapidSameKeyHostDepositsConvergeWithBoundedOpsNotAnUnboundedPingPong()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 1;
            await Task.Delay(15);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 2;
            await Task.Delay(15);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 3;

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"host never converged on its own rapid deposits; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"client never converged on the host's rapid deposits; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3,
                    TimeSpan.FromSeconds(5)),
                $"convergence did not hold - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            // THE load-bearing assertion: convergence happened through a BOUNDED number of wire
            // ops, not the unbounded 1, 2, 4, 12, 40, 180, 615, 1068, 906-then-a-Remove-of-1938
            // ping-pong docs/research/party-pool-sync.md §21 recorded live.
            Assert.True(
                rig.Host.PartyPool.Version <= MaxOpsForThreeDeposits,
                $"host ops blew up: Version={rig.Host.PartyPool.Version}");
            Assert.True(
                rig.Client.PartyPool.Version <= MaxOpsForThreeDeposits,
                $"client ops blew up: Version={rig.Client.PartyPool.Version}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// The symmetric shape of the reproduction above: three rapid same-key deposits on EACH
    /// side (host first, then client), converging to +6 on both — the task's own "rapid
    /// same-key ×3 each side (+6 both, bounded ops)" ask. Same tight (15ms) spacing so each
    /// burst overlaps the other side's own catch-up apply.
    /// </summary>
    [Fact]
    public async Task RapidSameKeyDepositsOnBothSidesConvergeToSixWithBoundedOps()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            await Task.Delay(300);

            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 1;
            await Task.Delay(15);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 2;
            await Task.Delay(15);
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 3;

            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout));
            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout));

            // Wait for the CLIENT'S OWN ENGINE (not just its store) to have actually caught up
            // to 3 before writing a further "local" deposit straight into TrueEngineState below
            // — the store can already read 3 while the client's catch-up apply is still in
            // flight through the simulated cheat channel (AgentLoop's own 30-250ms landing
            // delay), and overwriting the engine mid-flight would fabricate a spurious "excess"
            // that has nothing to do with the pump's own correctness, only this test's own
            // choreography racing the async apply.
            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 3, Timeout),
                "client's own engine never caught up to its converged store before the second "
                + $"round; got {rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");

            // Three more rapid deposits push it to 6.
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 4;
            await Task.Delay(15);
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 5;
            await Task.Delay(15);
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 6;

            Assert.True(
                await WaitForAsync(() => rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 6, Timeout),
                $"client under-counted its own rapid deposits; got {rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 6, Timeout),
                $"host under-counted the client's rapid deposits; got {rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster) == 6
                        && rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster) == 6,
                    TimeSpan.FromSeconds(5)),
                $"convergence did not hold - host={rig.Host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client={rig.Client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            Assert.True(
                rig.Host.PartyPool.Version <= MaxOpsForSixDeposits,
                $"host ops blew up: Version={rig.Host.PartyPool.Version}");
            Assert.True(
                rig.Client.PartyPool.Version <= MaxOpsForSixDeposits,
                $"client ops blew up: Version={rig.Client.PartyPool.Version}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// A queued apply that takes TWO ticks (not one) to land — the ordinary case in production,
    /// where the apply travels through the arbitrated single-slot staging-cheat channel rather
    /// than landing on the very next scan. Deterministic <see cref="ScriptedBridge"/>: a peer's
    /// op seeds the store (exactly what <c>CoopHost</c>/<c>CoopClient</c>'s own
    /// <c>HandlePartyPoolOp</c> does), reconcile queues an apply, two ticks pass with NO engine
    /// movement (the apply still in flight), then the engine jumps straight to the full owed
    /// amount. Because <c>wireTotal</c> is re-read fresh every tick rather than compared against
    /// a value captured once, the landing — however many ticks late — is still recognised
    /// exactly and adopted silently: <see cref="PartyPoolStore.Version"/> stays at 1 (only the
    /// original seeded peer op), never publishing anything for the late landing itself.
    /// </summary>
    [Fact]
    public void AQueuedApplyLandingTwoTicksLateIsAdoptedSilentlyNotRepublished()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick1: baseline (silent, empty pool)
                [], // tick2: still 0 - reconcile notices the peer's seeded total and queues an apply
                [], // tick3: STILL 0 - one tick of latency with nothing to see yet
                new() { [Blaster] = 4 }, // tick4: the apply finally lands, TWO ticks after queuing
                new() { [Blaster] = 4 }, // tick5: steady
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            agent.PumpOnce(); // tick1: baseline

            // A peer's op already landed in this instance's own store — exactly what
            // CoopHost/CoopClient's HandlePartyPoolOp does before the pump looks again.
            host.PartyPool.Record(PartyPoolOpKind.Add, Blaster, 4);

            agent.PumpOnce(); // tick2: reconcile queues the apply toward the now-known wire gap
            agent.PumpOnce(); // tick3: still not landed - nothing should publish
            agent.PumpOnce(); // tick4: lands, two ticks after being queued - must be silent
            agent.PumpOnce(); // tick5: steady

            Assert.Equal(4, host.PartyPoolTotals.GetValueOrDefault(Blaster));
            Assert.Equal(1u, host.PartyPool.Version); // only the seeded peer op - nothing else published
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The hard backstop itself (docs/research/party-pool-sync.md §22), independent of whether
    /// root cause 4's own reasoning has a gap: a key that publishes more than
    /// <c>PartyPoolPump</c>'s own <c>MaxPublishesPerWindow</c> (8) ops within its 10s rolling
    /// window is frozen — further movement on that key publishes nothing until the freeze
    /// lifts, and the trip logs exactly once. Deterministic <see cref="ScriptedBridge"/>: 20
    /// genuine alternating movements of the SAME key (no peer involved at all, so every single
    /// one would otherwise be published in full) on 20 consecutive ticks.
    /// </summary>
    [Fact]
    public void ALoopBreakerFreezesAKeyAfterExcessivePublishesWithinTheRollingWindow()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script = [[]]; // tick1: baseline
            for (int i = 0; i < 20; i++)
            {
                int value = i % 2 == 0 ? 1 : 0;
                script.Add(new Dictionary<PartyPoolStackKey, int> { [Blaster] = value });
            }

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            List<string> logs = [];
            agent.Log = logs.Add;

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            // 20 genuine movements were driven, but the breaker caps published ops for this key
            // at exactly MaxPublishesPerWindow (8) — the 9th attempt trips the freeze instead of
            // publishing, and every movement after that is silently suppressed too.
            Assert.Equal(8u, host.PartyPool.Version);

            List<string> breakerLines =
                logs.Where(line => line.Contains("loop breaker tripped", StringComparison.Ordinal)).ToList();
            Assert.Single(breakerLines);
            Assert.Contains(Blaster.Resref, breakerLines[0]);
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The AGENT-side half of docs/research/party-pool-sync.md §14's two freshness bugs:
    /// <c>KotorGameBridge.PartyPool.cs</c>'s own scan-result cache (reproduced exactly by
    /// <see cref="ThrottledFakeBridge"/>) can serve a snapshot up to <c>scanIntervalTicks</c>
    /// stale. Before the fix, once ONE real (non-cached) scan ran — whether from the throttle
    /// naturally expiring or (after this fix) from an explicit invalidation — it unconditionally
    /// re-armed a FRESH full-length cache window from that scan's own timestamp, regardless of
    /// whether anything actually changed. A second genuine local change landing shortly after the
    /// first was detected would then be served the FIRST change's now-stale value for up to
    /// another full window — i.e. the third `pooladd` in a rapid burst could easily land inside
    /// the window the SECOND's own confirming scan had just re-armed, reading as if only the
    /// first ever happened. <c>PartyPoolPump.cs</c> now calls
    /// <c>bridge.InvalidatePartyPoolScanCache()</c> the instant it detects (or queues an apply
    /// for) a change, forcing the VERY NEXT scan to be genuine no matter how recently the cache
    /// was last armed.
    ///
    /// Fully deterministic: <see cref="ThrottledFakeBridge.Clock"/> is advanced by exactly ONE
    /// tick between the two deposits — deliberately far short of another full
    /// <c>scanIntervalTicks</c> window — so this fails against the pre-fix code (the second
    /// deposit would need <c>Clock</c> to reach <c>1000 + scanIntervalTicks</c> before the
    /// re-armed cache would ever let it through) and passes only because the detection of the
    /// first deposit itself forced the second scan to be real.
    /// </summary>
    [Fact]
    public void ASecondRapidLocalChangeIsNotMaskedBehindTheRestOfAnAlreadyStaleScanWindow()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            ThrottledFakeBridge bridge = new(scanIntervalTicks: 1000);
            CoopAgent agent = new(
                bridge, host, new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            // tick 1 @ clock 0: baseline scan (empty pool) - silent, per the pump's own
            // first-scan-is-silent rule.
            agent.PumpOnce();

            // The first genuine deposit. Advance the clock past the cache's own natural window
            // so this is observed the ordinary way (nothing about THIS read depends on the fix).
            bridge.TrueEngineState[Blaster] = 1;
            bridge.Clock = 1000;
            agent.PumpOnce();

            Assert.Equal(1, host.PartyPoolTotals.GetValueOrDefault(Blaster));

            // A SECOND genuine deposit, one tick later - nowhere near another full
            // scanIntervalTicks window from the confirming scan above. Without the fix this
            // reads as the still-cached value of 1 (no diff, nothing published); with it, the
            // first deposit's own detection already forced this scan to be real.
            bridge.TrueEngineState[Blaster] = 2;
            bridge.Clock = 1001;
            agent.PumpOnce();

            Assert.Equal(
                2, host.PartyPoolTotals.GetValueOrDefault(Blaster));
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The actual regression pin for the under-count bug's first mechanism (docs/research/
    /// party-pool-sync.md §11/§12): three genuine, DISTINCT local deposits of the SAME key,
    /// each landing well inside the OLD <c>LocalDetectGraceMs</c> (2000ms) exclusion window
    /// relative to the one before it, must each be individually detected and published — not
    /// silently swallowed because an earlier one on the same key was still "pending" when the
    /// next one happened. Deterministic <see cref="ScriptedBridge"/>, one call per tick, so the
    /// second and third deposits land on the tick immediately after the previous one — no
    /// wall-clock margin for the old guard's near-instant self-confirm to clear the key first
    /// (see <see cref="RapidSameKeyDepositsOnEitherSideAreAllCountedNotSwallowed"/> above for why
    /// the async, real-timing version of this scenario cannot reliably hit the window).
    ///
    /// Against the OLD blanket-exclusion guard, this fails: the moment the first deposit is
    /// detected, the key is excluded from step 1 entirely until either a matching scan or the 2s
    /// grace expires — so the second and third deposits' own movement is invisible to step 1 the
    /// whole time, and <c>_lastScan</c> (updated unconditionally every tick regardless of
    /// exclusion) silently advances past them, leaving no residual diff to ever detect. The
    /// total sticks at whatever the first grace-protected value was.
    /// </summary>
    [Fact]
    public void ThreeRapidSameKeyLocalDetectionsAreAllPublishedNotSwallowed()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick 1: baseline (silent)
                new() { [Blaster] = 1 }, // tick 2: first deposit
                new() { [Blaster] = 2 }, // tick 3: second deposit, before the first is confirmed
                new() { [Blaster] = 3 }, // tick 4: third deposit, before the second is confirmed
                new() { [Blaster] = 3 }, // tick 5: steady
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            Assert.Equal(3, host.PartyPoolTotals.GetValueOrDefault(Blaster));
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The other half of the same under-count bug (docs/research/party-pool-sync.md §11): a
    /// deposit detected and published, then — BEFORE any confirming scan can reconfirm it landed
    /// — a genuine withdrawal of the SAME key. Under the old blanket-exclusion guard this key
    /// stayed excluded from detection for up to 2000ms REGARDLESS of what a fresh scan showed
    /// (only an EXACT match to the original target cleared it early), so a withdrawal landing
    /// inside that window was invisible: <c>_lastScan</c> silently advanced to the post-
    /// withdrawal value every tick regardless (it is updated unconditionally), so once the grace
    /// window finally expired there was no residual diff left to detect at all — the withdrawal
    /// was gone without a trace, leaving a phantom deposit in the store forever. This uses a
    /// <see cref="ScriptedBridge"/> (deterministic, no wall-clock luck needed) to force the
    /// withdrawal into the tick immediately after the deposit, before any reconfirming scan.
    /// </summary>
    [Fact]
    public void ADepositImmediatelyFollowedByAWithdrawalOfTheSameKeyNetsToZeroNotSwallowed()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick1: baseline (silent, empty pool)
                new() { [Blaster] = 5 }, // tick2: deposit detected & published
                [], // tick3: withdrawal lands BEFORE any confirming scan - back to 0
                [], // tick4: steady at 0 - nothing further should move
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            Assert.Equal(0, host.PartyPoolTotals.GetValueOrDefault(Blaster));
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The second, permanent-lockout half of the same under-count bug (docs/research/party-
    /// pool-sync.md §11's mechanism 2): a peer's op has already updated THIS instance's store
    /// (<see cref="ICoopSession.PartyPoolTotals"/>) and a reconcile apply is queued/pending for
    /// it, when a genuine LOCAL deposit of the SAME key lands in the SAME scan window as the
    /// queued apply's own effect. The two are indistinguishable in the raw engine number (one
    /// combined jump), so the fix must publish exactly the EXCESS beyond what the pending apply
    /// alone explains — not the full observed jump (double-counts the peer's own contribution,
    /// already in the store) and not nothing (drops the genuine local deposit, and — under the
    /// OLD code — permanently locks the key out of further detection/reconcile, since the
    /// pending entry never matches a fresh scan that also carries the extra local change).
    /// Deterministic <see cref="ScriptedBridge"/>: no client, no wire — this isolates the pure
    /// pump mechanism, seeding the peer's own contribution directly into the store the way
    /// <c>CoopClient.HandlePartyPoolOp</c>/<c>CoopHost.HandlePartyPoolOp</c> would.
    /// </summary>
    [Fact]
    public void APeerApplyLandingInTheSameTickAsAGenuineLocalDepositOfTheSameKeyCountsBothAsTwo()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick1: baseline (silent, empty pool) - seeds _lastScan = empty
                [], // tick2: still 0 in the engine - the queued apply has not landed yet. This is
                    // the tick where step 2's reconcile notices the store (seeded with the peer's
                    // Add below, just before this PumpOnce) disagrees with the engine and queues
                    // an apply, registering a pending target of 1.
                new() { [Blaster] = 2 }, // tick3: BOTH the peer apply's own +1 AND a genuine local
                                          // +1 deposit land together, observed as one combined
                                          // jump from 0 to 2.
                new() { [Blaster] = 2 }, // tick4: steady - nothing further should move.
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            agent.PumpOnce(); // tick1: baseline

            // Simulate a peer's op having already been applied to this instance's own store -
            // exactly what CoopHost/CoopClient's HandlePartyPoolOp does before the pump ever
            // looks at PartyPoolTotals again.
            host.PartyPool.Record(PartyPoolOpKind.Add, Blaster, 1);

            agent.PumpOnce(); // tick2: reconcile queues the apply, registers the pending target
            agent.PumpOnce(); // tick3: the combined jump - must publish only the excess (+1)
            agent.PumpOnce(); // tick4: steady

            Assert.Equal(2, host.PartyPoolTotals.GetValueOrDefault(Blaster));
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// The depositing-side duplicate live-reproduced 2026-08-28
    /// (docs/research/party-pool-sync.md §9): a HOST-originated <c>pooladd</c>, applied through
    /// the REAL single-slot staging-cheat round trip, ends up applied to the host's own engine
    /// TWICE — a genuine double-count (a second, independently spawned stack), not a cosmetic
    /// duplicate log line.
    ///
    /// Unlike <see cref="HostDepositConvergesToExactlyOneCopyOnBothSides"/> above (which
    /// simulates a GUI-dragged deposit by writing straight into
    /// <see cref="FakeBridge.TrueEngineState"/> — instantaneous, and never queued through
    /// <c>_pendingApplies</c> at all), this test drives the deposit through
    /// <see cref="DispatcherLoop.SubmitHumanPoolAdd"/> — the same asynchronous, non-instant,
    /// single-slot cheat channel <c>pooladd</c> actually uses — on a task genuinely independent
    /// of <see cref="AgentLoop"/>'s own <c>PartyPoolPump</c> tick loop, with BOTH the bridge's
    /// scan-result cache and the heavy-pump cadence at their real 1000ms production values (the
    /// exact ratio §8's own "production ratio" experiment used and could not force a double
    /// under — the missing ingredient there was the single combined loop, not the timing).
    /// </summary>
    [Fact]
    public async Task HostOriginatedPoolAddThroughTheRealCheatChannelDoesNotDoubleApply()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        // Production values, not the compressed/stress ones the other tests here use — see the
        // class doc comment for why the timing was never the missing ingredient.
        using AgentLoop hostAgent = new(
            host, bridgeScanIntervalMs: 1000, heavyPumpIntervalMs: 1000, autoDrain: false);
        using AgentLoop clientAgent = new(client, bridgeScanIntervalMs: 1000, heavyPumpIntervalMs: 1000);
        using DispatcherLoop hostDispatcher = new(hostAgent.Agent, hostAgent.Bridge);

        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
                "host/client never connected");

            // Let both sides establish their baseline scan (empty pool) before the "human"
            // deposits, exactly like PartyPoolPump's own first-scan-is-silent rule.
            await Task.Delay(1200);

            // The host types `pooladd g_w_thermldet01 3` — §9's exact live repro.
            hostDispatcher.SubmitHumanPoolAdd(Blaster, 3);

            Assert.True(
                await WaitForAsync(() => hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 3, Timeout),
                $"the simulated cheat round trip never landed; got "
                + $"{hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"host never recorded its own deposit; got {host.PartyPoolTotals.GetValueOrDefault(Blaster)}");
            Assert.True(
                await WaitForAsync(() => client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3, Timeout),
                $"client never converged on the host's deposit; got {client.PartyPoolTotals.GetValueOrDefault(Blaster)}");

            // Held well past §9's own observed 1.34s gap between the two applies, and past
            // several full scan-cache/heavy-pump windows on both sides.
            Assert.True(
                await StaysAsync(
                    () => hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 3
                        && host.PartyPoolTotals.GetValueOrDefault(Blaster) == 3
                        && client.PartyPoolTotals.GetValueOrDefault(Blaster) == 3,
                    TimeSpan.FromSeconds(12)),
                "a HOST-originated pooladd doubled - "
                + $"host engine={hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"host store={host.PartyPoolTotals.GetValueOrDefault(Blaster)}, "
                + $"client store={client.PartyPoolTotals.GetValueOrDefault(Blaster)}");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
            host.Dispose();
            client.Dispose();
        }
    }

    /// <summary>
    /// The mechanism root cause 2's fix (<c>PartyPoolPump.cs</c>'s <c>_localDetectExpiry</c>)
    /// closes, isolated and made fully deterministic — no timing luck required, unlike
    /// <see cref="HostOriginatedPoolAddThroughTheRealCheatChannelDoesNotDoubleApply"/> above
    /// (which drives the real asynchronous cheat round trip and, being timing-dependent, is not
    /// guaranteed to hit the exact window every run — see its own remarks and
    /// docs/research/party-pool-sync.md §9/§10 for why this narrower, scripted test exists
    /// alongside it).
    ///
    /// A HOST deposit is detected and published once (engine 0→3). The NEXT two scans then
    /// transiently disagree with what was just published (3→0→0) — standing in for ANY
    /// momentary engine-read inconsistency after a local detection (a stale scan-cache read, an
    /// item whose resref briefly failed to resolve — see docs/research/party-pool-sync.md's
    /// KotorGameBridge.PartyPool.cs remarks) — before recovering (0→3→3).
    ///
    /// ⚠ SUPERSEDED, 2026-08-28 (docs/research/party-pool-sync.md §12, root cause 3): this
    /// test's original assertion (<c>Version</c> staying at 1 — the whole 3→0→0→3→3 wobble
    /// silently absorbed as noise) depended on <c>LocalDetectGraceMs</c>'s blanket "exclude this
    /// key from ANY further detection for 2s" guard — the exact mechanism §11 found causes a
    /// genuine, same-key, same-window local change (a real withdrawal, not a glitch) to be
    /// silently dropped instead, because the guard cannot tell the two apart without ALSO
    /// swallowing real data. The replacement (count-aware: compare only against a queued apply's
    /// own expected value, publish any further difference immediately, no time-based exclusion
    /// for a self-detected change at all) no longer distinguishes "a transient scan glitch that
    /// will self-heal" from "a genuine change that happens to net back to the pre-deposit value"
    /// — nothing can, without either window (both are literally the same sequence of numbers).
    /// Given that choice, this codebase's own multiset design philosophy (§2.1: "a store's stock
    /// genuinely goes up and down repeatedly... a one-way ratchet is wrong here too") makes NOT
    /// losing genuine data the right default, even at the cost of two extra (Remove-then-Add)
    /// wire ops on the rare occasion a real scan glitch happens to net back to the exact
    /// pre-deposit value. The FINAL total (what actually matters — no data is lost or doubled)
    /// still converges correctly; only the intermediate op count changed. See
    /// <see cref="ADepositImmediatelyFollowedByAWithdrawalOfTheSameKeyNetsToZeroNotSwallowed"/>
    /// for the regression test covering the case this trade was made for.
    /// </summary>
    [Fact]
    public void AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick 1: baseline (silent, pre-existing pool contents — none here)
                new() { [Blaster] = 3 }, // tick 2: the deposit already landed before this scan
                [], // tick 3: transient glitch — the deposit "vanishes" from the scan
                [], // tick 4: glitch persists one more tick
                new() { [Blaster] = 3 }, // tick 5: recovered
                new() { [Blaster] = 3 }, // tick 6: steady — nothing further should move
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            for (int i = 0; i < script.Count; i++)
            {
                agent.PumpOnce();
            }

            // The FINAL total is still exactly right — nothing lost, nothing doubled — even
            // though the wobble is no longer silently absorbed (Version is now 3: Add 3, the
            // glitch's own Remove 3, then the recovery's own Add 3 — see the class remarks
            // above for why this trade was made).
            Assert.Equal(3, host.PartyPoolTotals.GetValueOrDefault(Blaster));
            Assert.Equal(
                3u,
                host.PartyPool.Version);
        }
        finally
        {
            host.Dispose();
        }
    }

    // ---- suspicious stack size diagnostic (docs/research/party-pool-sync.md §19/§20:
    // ambient-script sentinel/overflow stack sizes like x65535/x6783/x38937) ------------------

    /// <summary>
    /// The exact pathological values docs/research/party-pool-sync.md §19.1 observed live from
    /// the module's own duros-registration script — logged ONCE, not once per tick, even though
    /// the pump scans (and therefore would otherwise re-observe) the same steady total on every
    /// subsequent pass.
    /// </summary>
    [Fact]
    public void ASuspiciouslyLargeStackLogsExactlyOnceNotOnEveryTick()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick 1: baseline (silent)
                new() { [Blaster] = 65535 }, // tick 2: the pathological deposit
                new() { [Blaster] = 65535 }, // tick 3: steady — already logged, must not repeat
                new() { [Blaster] = 65535 }, // tick 4: steady again
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            List<string> logs = [];
            agent.Log = logs.Add;

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            List<string> suspiciousLines =
                logs.Where(line => line.Contains("suspiciously large stack", StringComparison.Ordinal)).ToList();

            Assert.Single(suspiciousLines);
            Assert.Contains("65535", suspiciousLines[0]);
            Assert.Contains(Blaster.Resref, suspiciousLines[0]);
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>An ordinary, plausible deposit (a handful of grenades, medpacs, whatever) never
    /// trips the diagnostic — this is a "say something odd happened" line, not routine
    /// noise.</summary>
    [Fact]
    public void AnOrdinaryStackSizeNeverLogsTheSuspiciousWarning()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick 1: baseline
                new() { [Blaster] = 5 }, // tick 2: ordinary deposit
                new() { [Blaster] = 5 }, // tick 3: steady
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            List<string> logs = [];
            agent.Log = logs.Add;

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            Assert.DoesNotContain(logs, line => line.Contains("suspiciously large stack", StringComparison.Ordinal));
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>Two DIFFERENT keys each get their own one-time warning — the "logged once"
    /// budget is per key, not a single global latch that the first oversized stack exhausts for
    /// every other key that follows.</summary>
    [Fact]
    public void EachDistinctOversizedKeyGetsItsOwnOneTimeWarning()
    {
        PartyPoolStackKey saber = new("g_w_dblsbr001", 0, 0, 0, ItemIdentity.FlagKnown);

        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        try
        {
            List<Dictionary<PartyPoolStackKey, int>> script =
            [
                [], // tick 1: baseline
                new() { [Blaster] = 65535 }, // tick 2: first oversized key
                new() { [Blaster] = 65535, [saber] = 38937 }, // tick 3: second oversized key joins
            ];

            CoopAgent agent = new(
                new ScriptedBridge(script),
                host,
                new AgentSettings { HeavyPumpIntervalMs = 0, PartyPoolSyncEnabled = true });

            List<string> logs = [];
            agent.Log = logs.Add;

            foreach (Dictionary<PartyPoolStackKey, int> _ in script)
            {
                agent.PumpOnce();
            }

            List<string> suspiciousLines =
                logs.Where(line => line.Contains("suspiciously large stack", StringComparison.Ordinal)).ToList();

            Assert.Equal(2, suspiciousLines.Count);
            Assert.Contains(suspiciousLines, line => line.Contains(Blaster.Resref, StringComparison.Ordinal));
            Assert.Contains(suspiciousLines, line => line.Contains(saber.Resref, StringComparison.Ordinal));
        }
        finally
        {
            host.Dispose();
        }
    }
}
