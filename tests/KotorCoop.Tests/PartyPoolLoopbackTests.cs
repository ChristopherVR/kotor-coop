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
    /// Run against a DELIBERATELY REVERTED copy of root cause 2's fix (comment out the
    /// <c>justDetected</c>/<c>_localDetectExpiry</c> block in <c>PartyPoolPump.cs</c>), this
    /// test fails: step 1 misreads the glitch as two FURTHER genuine local changes (a real
    /// Remove then a real Add), leaving <see cref="PartyPoolStore.Version"/> at 3 instead of 1 —
    /// the exact "republish my own recent local action" shape root cause 1 already named for a
    /// different trigger (a landed AUTOMATIC apply). Against the current (fixed) code, the
    /// glitch is absorbed and <c>Version</c> stays 1.
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

            Assert.Equal(3, host.PartyPoolTotals.GetValueOrDefault(Blaster));
            Assert.Equal(
                1u,
                host.PartyPool.Version);
        }
        finally
        {
            host.Dispose();
        }
    }
}
