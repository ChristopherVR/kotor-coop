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

        public AgentLoop(ICoopSession session, int bridgeScanIntervalMs, int heavyPumpIntervalMs = 0)
        {
            Bridge = new FakeBridge(bridgeScanIntervalMs);
            Agent = new CoopAgent(
                Bridge,
                session,
                new AgentSettings { HeavyPumpIntervalMs = heavyPumpIntervalMs, PartyPoolSyncEnabled = true });

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

                    long now = Environment.TickCount64;

                    if (_cheatLandsAtMs >= 0)
                    {
                        // A cheat request is already "in flight" (the real shared-block round
                        // trip to the plugin and back) — nothing further drains until it lands,
                        // matching _pendingCheatSerial's real gate.
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
}
