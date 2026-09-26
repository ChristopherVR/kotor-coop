using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump loop on each side — the
/// full production shape for the merchant/store stock channel (docs/research/store-sync.md), minus
/// only the actual game process (a <see cref="FakeStoreBridge"/> stands in for
/// <c>KotorGameBridge</c>).
///
/// These tests pin the 2026-08-28 "Bug 4" fix (docs/research/store-sync.md's own write-up): merely
/// OPENING a virgin (never-transacted) store on two <c>store_sync=1</c> instances, with zero
/// buy/sell activity, used to empty that store's entire stock on BOTH sides — the reconcile step
/// read a pristine key's absence from the wire's (necessarily empty) "desired" truth as "the
/// session desires zero of this", floor-and-remove being indistinguishable from "never
/// transacted". Before the fix, EVERY test below except <see cref="InfiniteStockBuyProducesNoOp"/>
/// fails (the FIRST one — <see cref="OpeningAVirginStoreOnBothSidesLeavesStockUnchanged"/> — is the
/// most direct reproduction: the old code drove both sides' stock to zero within a tick or two of
/// opening, with no buy ever issued).
/// </summary>
[Trait("Category", "Slow")]
public class StoreLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const uint StoreId = 0x74;
    private const uint OtherStoreId = 0x2000;
    private const string Module = "tar_m02aa";

    /// <summary>A SECOND module, distinct from <see cref="Module"/>, for the module-scoping
    /// tests below — object ids are assigned per-GIT-load, so the engine is free to reuse the
    /// SAME numeric id (<see cref="StoreId"/>) for an entirely unrelated store once this module
    /// loads.</summary>
    private const string OtherModule = "end_m01aa";

    private static readonly StoreStackKey Blaster =
        new(StoreId, "g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey Spike =
        new(StoreId, "k_computer_spike", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey InfiniteAmmo =
        new(StoreId, "g_i_ammoclip01", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey OtherStoreItem =
        new(OtherStoreId, "g_a_class7001", 0, 0, 0, ItemIdentity.FlagKnown);

    /// <summary>
    /// Stands in for <c>KotorGameBridge.Store.cs</c>: <see cref="TrueEngineState"/> is exactly
    /// what a real buy/sell (or a landed automatic apply) leaves in a store's <c>CItemRepository</c>
    /// — spans EVERY store this fake bridge knows about, sliced by <see cref="OpenStoreId"/> the
    /// same way <see cref="IStoreBridge.TryScanOpenStore"/>'s own contract requires (only the
    /// currently-open store's contents are ever reported; 0 or absent means "not open"). No
    /// artificial scan-result caching — the bug under test here is in the reconcile/apply logic,
    /// not in a bridge-side cache race (that hazard is the PARTY POOL loopback tests' own
    /// territory).
    /// </summary>
    private sealed class FakeStoreBridge : IGameBridge
    {
        public Dictionary<StoreStackKey, int> TrueEngineState { get; } = [];

        /// <summary>0 = no store GUI open on this instance right now.</summary>
        public uint OpenStoreId { get; set; }

        /// <summary>The module <see cref="TryReadLocalState"/> reports. Mutable (unlike a bare
        /// constant) so a test can drive a module CHANGE — <see cref="StorePump"/> reads this off
        /// <c>PlayerState.Module</c> every tick and clears its per-module belief when it changes
        /// (docs/research/store-sync.md's own module-scoping fix); defaults to this file's own
        /// shared <see cref="Module"/> constant so every EXISTING test (none of which cares about
        /// module changes) is unaffected.</summary>
        public string LocalModule { get; set; } = Module;

        public string Description => "store-loopback-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsStore => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, LocalModule, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
        {
            // The test driver can switch shops while this pump thread scans. Capture the
            // selected ID once, matching the production scanner, so the returned ID and
            // inventory slice cannot describe different shops.
            storeObjectId = OpenStoreId;
            if (storeObjectId == 0)
            {
                totals = EmptyTotals;
                return false;
            }

            // A store's own scan is always a COMPLETE snapshot (ContainerLoot/PartyPool's own
            // convention, docs/research/store-sync.md §2.3) — a fully-depleted stack is UNLINKED
            // from the real item_list, so it is simply absent here, never present-at-zero.
            Dictionary<StoreStackKey, int> slice = [];
            foreach ((StoreStackKey key, int value) in TrueEngineState)
            {
                if (key.StoreObjectId == storeObjectId && value > 0)
                {
                    slice[key] = value;
                }
            }

            totals = slice;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static readonly Dictionary<StoreStackKey, int> EmptyTotals = [];

    /// <summary>
    /// Drives one side's <see cref="CoopAgent"/> pump loop on a background task, at a much faster
    /// cadence than production, with <c>HeavyPumpIntervalMs = 0</c> so <see cref="Pumps.StorePump"/>
    /// runs on every tick. After every <c>PumpOnce</c>, drains every queued automatic apply
    /// straight into <see cref="FakeStoreBridge.TrueEngineState"/> — the same job
    /// <c>CoopCommandDispatcher.Store.cs</c>'s <c>DrainAutomaticStore</c> does through the real
    /// cheat channel, just landing instantly instead of through a native engine call (the race
    /// that single-slot channel's own timing introduces is the PARTY POOL loopback tests'
    /// territory, not this bug's).
    /// </summary>
    private sealed class AgentLoop : IDisposable
    {
        public FakeStoreBridge Bridge { get; }

        public CoopAgent Agent { get; }

        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;

        public System.Collections.Concurrent.ConcurrentQueue<string> Logs { get; } = new();

        public AgentLoop(ICoopSession session, string name = "")
        {
            Bridge = new FakeStoreBridge();
            Agent = new CoopAgent(
                Bridge, session, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });
            Agent.Log = msg => Logs.Enqueue($"[{name}] {msg}");

            _task = Task.Run(() => RunAsync(_cts.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Agent.PumpOnce();

                    while (Agent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
                    {
                        int before = Bridge.TrueEngineState.GetValueOrDefault(next.Key);
                        int after = next.Kind == StoreOpKind.Add
                            ? before + next.Count
                            : Math.Max(0, before - next.Count);

                        if (after > 0)
                        {
                            Bridge.TrueEngineState[next.Key] = after;
                        }
                        else
                        {
                            Bridge.TrueEngineState.Remove(next.Key);
                        }
                    }
                }
                catch (Exception)
                {
                    // A stopped session between ticks (disposed channel) is not this test's
                    // concern; the outer test asserts on final state, not on this loop's survival.
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

    /// <summary>Holds a condition for a further stretch of real time so a regression that appears
    /// one or two ticks AFTER first convergence cannot slip past a check that only samples once —
    /// mirrors <c>PartyPoolLoopbackTests.StaysAsync</c> exactly.</summary>
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

        AgentLoop hostAgent = new(host, "host");
        AgentLoop clientAgent = new(client, "client");

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        return (new Rig(host, client, hostAgent, clientAgent), cts, hostTask, clientTask);
    }

    /// <summary>
    /// The direct reproduction of Bug 4: BOTH instances open the SAME never-transacted store, with
    /// no buy/sell issued by anyone. Before the fix, this alone drove every stocked key's quantity
    /// to zero on both sides within a tick or two.
    /// </summary>
    [Fact]
    public async Task OpeningAVirginStoreOnBothSidesLeavesStockUnchanged()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 12;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Spike] = 12;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 12
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 12
                        && rig.Host.Store.Version == 0
                        && rig.Client.Store.Version == 0,
                    TimeSpan.FromSeconds(4)),
                "merely opening a virgin store wiped or otherwise touched its stock - "
                + $"host: blaster={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"spike={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}; "
                + $"client: blaster={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"spike={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}; "
                + $"host.Store.Version={rig.Host.Store.Version}, client.Store.Version={rig.Client.Store.Version}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task BuyOnHostDecrementsClientsCopy()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            // A second, NEVER-transacted stack in the SAME store — present so a wipe-shaped
            // regression (which touches EVERY key, not just the one bought) shows up here too,
            // rather than this test only ever exercising the one key that happens to pass through
            // its expected value transiently while a wipe decrements it the rest of the way to
            // zero (docs/research/store-sync.md's Bug 4 — merely opening a virgin store drove
            // every stocked key toward zero, not just a transacted one).
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 12;
            rig.ClientAgent.Bridge.TrueEngineState[Spike] = 12;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            // Let both sides establish their silent baseline first — design rule (a): a virgin
            // store's first local read publishes nothing.
            await Task.Delay(300);

            // The host buys one unit off the shelf.
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 39;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39, Timeout),
                "client's own copy of the store never decremented; got "
                + $"{rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 12
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 12,
                    TimeSpan.FromSeconds(5)),
                "the buy over- or under-corrected after first converging, or touched the OTHER, "
                + "never-transacted stack in the same store - "
                + $"host blaster={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"client blaster={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"host spike={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}, "
                + $"client spike={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}");

            // The host's OWN wire-side bookkeeping should also now report the true absolute total
            // (39, not 0 and not floored/lost) — StoreStore.Record's baseline-seeding fix.
            Assert.Equal(39, rig.Host.Store.Get(Blaster));

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task DepletingAStackOnHostDrivesItToZeroOnClient()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 2;
            rig.ClientAgent.Bridge.TrueEngineState[Spike] = 2;

            // A second, NEVER-transacted stack in the SAME store — see BuyOnHostDecrementsClientsCopy's
            // own remarks for why this is needed to make a wipe-shaped regression visible here.
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // The host buys out the entire stack in one shot — the engine unlinks it entirely
            // (§1.5), so the fake bridge models that as removing the key outright.
            rig.HostAgent.Bridge.TrueEngineState.Remove(Spike);

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 0, Timeout),
                "client's copy never hit zero; got "
                + $"{rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}");

            Assert.True(
                await StaysAsync(
                    () => !rig.HostAgent.Bridge.TrueEngineState.ContainsKey(Spike)
                        && !rig.ClientAgent.Bridge.TrueEngineState.ContainsKey(Spike)
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                    TimeSpan.FromSeconds(5)),
                "the depletion did not stay converged at zero on both sides, or touched the OTHER, "
                + "never-transacted stack in the same store - "
                + $"host blaster={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"client blaster={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// A reconnecting client's resync must converge the store it actually transacted in WITHOUT
    /// doubling anything, while leaving a completely separate, never-transacted store (whose
    /// pristine stock the client independently opened and baselined) untouched — the "resync
    /// leaves untransacted stores alone" half of design rule (c)/(d), exercised end to end
    /// alongside the "leaves TRANSACTED stores correctly converged" half.
    /// </summary>
    [Fact]
    public async Task ReconnectResyncLeavesUntransactedStoresAlone()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            try
            {
                // Both instances open BOTH a store they will transact in (StoreId) and a completely
                // separate one they never will (OtherStoreId) — establishing a pristine local baseline
                // for each, exactly like a player who has looked into two different shops.
                rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
                rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;
                rig.HostAgent.Bridge.TrueEngineState[OtherStoreItem] = 100;
                rig.ClientAgent.Bridge.TrueEngineState[OtherStoreItem] = 100;

                rig.HostAgent.Bridge.OpenStoreId = StoreId;
                rig.ClientAgent.Bridge.OpenStoreId = StoreId;
                await Task.Delay(300);

                rig.HostAgent.Bridge.OpenStoreId = OtherStoreId;
                rig.ClientAgent.Bridge.OpenStoreId = OtherStoreId;
                await Task.Delay(300);

                // Back to the transacted store, then the host buys one unit — converges to the client
                // exactly like BuyOnHostDecrementsClientsCopy.
                rig.HostAgent.Bridge.OpenStoreId = StoreId;
                rig.ClientAgent.Bridge.OpenStoreId = StoreId;
                await Task.Delay(300);

                rig.HostAgent.Bridge.TrueEngineState[Blaster] = 39;

                Assert.True(
                    await WaitForAsync(
                        () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39, Timeout),
                    "client never converged on the host's buy before the reconnect");

                uint originalId = rig.Client.LocalPeerId;

                // A real blackout past the peer timeout — the client drops off the host's roster
                // entirely and has to rejoin, exactly HandleHello's resync-on-(re)admission path
                // (ResyncStore) firing again.
                rig.Client.Network.StartBlackout(6_000);

                Assert.True(
                    await WaitForAsync(() => !rig.Client.IsConnected, TimeSpan.FromSeconds(10)),
                    "client never noticed the blackout");
                Assert.True(
                    await WaitForAsync(() => rig.Host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(10)),
                    "host never expired the blacked-out peer");

                Assert.True(
                    await WaitForAsync(() => rig.Client.IsConnected, TimeSpan.FromSeconds(30)),
                    "client never rejoined after the blackout ended");
                Assert.Equal(originalId, rig.Client.LocalPeerId);

                // The TRANSACTED store: resync must leave it correctly converged (39), not double it,
                // not zero it.
                Assert.True(
                    await StaysAsync(
                        () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39
                            && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 39,
                        TimeSpan.FromSeconds(5)),
                    "the reconnect resync disturbed the already-converged transacted store - "
                    + $"host={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                    + $"client={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");

                // The UNTRANSACTED store: resync must never touch it at all - neither instance's own
                // pristine engine copy moves, and the host's own wire-side bookkeeping never learned
                // any of its keys (nobody ever transacted there to make it "known").
                Assert.True(
                    rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(OtherStoreItem) == 100
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(OtherStoreItem) == 100,
                    "the reconnect resync touched a store nobody ever transacted in - "
                    + $"host={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(OtherStoreItem)}, "
                    + $"client={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(OtherStoreItem)}");
                Assert.False(rig.Host.StoreKnownKeys.Contains(OtherStoreItem));
                Assert.Equal(0, rig.Host.Store.Get(OtherStoreItem));

            }
            finally
            {
                // Always stop the session tasks, including when an assertion fails before the
                // normal success-path cancellation. Otherwise disposing the rig leaves both
                // socket loops alive against disposed sessions and can contaminate later tests.
                await cts.CancelAsync();
                await Task.WhenAll(hostTask, clientTask);
            }
        }
    }

    /// <summary>
    /// §1.5's own engine-level guarantee (docs/research/store-sync.md): an Infinite-flagged
    /// entry's stock NEVER changes from either a buy or a sell — the engine spawns a fresh copy for
    /// a buy and silently no-ops a matching sell, so a scan taken before and after a "buy" against
    /// one shows the SAME quantity. This test does not model the copy-spawn itself (that is a
    /// player's OWN inventory gaining an item, not this store's stock) — it models exactly what
    /// <see cref="IStoreBridge.TryScanOpenStore"/>'s own contract guarantees a real bridge would
    /// report: no change, ever, for such an entry. No op should ever be published for it.
    /// </summary>
    /// <summary>
    /// The mirror image of <see cref="BuyOnHostDecrementsClientsCopy"/>: a SELL — a local ADD
    /// that introduces a key the store never had before. This is the scenario
    /// docs/research/store-sync.md's own live passes repeatedly reported as "no wire-mirror op
    /// published" for a real `sell` — this test drives the exact same detect path
    /// (<see cref="Pumps.StorePump"/> step 1, <see cref="StoreDiffRule"/>) through the FAKE
    /// bridge, which has none of a real engine's Infinite-stock/`item_count` quirks, to settle
    /// whether the SESSION/PROTOCOL layer itself has a defect independent of those engine-level
    /// gotchas.
    /// </summary>
    [Fact]
    public async Task SellOnHostAddingABrandNewKeyMirrorsToClient()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            // The store already stocks Blaster (so there is something for the store to be
            // "open" against) but has NEVER held Spike — the key this sell introduces.
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            // Let both sides establish their silent baseline first - design rule (a).
            await Task.Delay(300);

            // The host SELLS one unit of an item the store never stocked before - a brand new
            // key appears in the store's true engine state with count 1.
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 1, Timeout),
                "client's own copy of the store never gained the sold item; got "
                + $"{rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}. "
                + $"host.Store.Version={rig.Host.Store.Version}, host Spike={rig.Host.Store.Get(Spike)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 1
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 1
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                    TimeSpan.FromSeconds(5)),
                "the sell over- or under-corrected after first converging, or touched the OTHER "
                + "stack in the same store\n"
                + string.Join('\n', rig.HostAgent.Logs) + "\n---client---\n"
                + string.Join('\n', rig.ClientAgent.Logs));

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// The other half of the sell scenario: selling INTO an existing stack (the store already
    /// stocks this exact resref+identity) merges as a plain quantity increase rather than a
    /// brand-new key - must publish exactly the delta, not the full post-sell total.
    /// </summary>
    [Fact]
    public async Task SellOnHostMergingIntoAnExistingStackMirrorsToClient()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 3;
            rig.ClientAgent.Bridge.TrueEngineState[Spike] = 3;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // The host sells one more unit of a resref the store already stocks - a merge, not
            // a new key.
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 4;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 4, Timeout),
                "client's own copy of the store never picked up the merged sell; got "
                + $"{rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 4
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 4,
                    TimeSpan.FromSeconds(5)),
                "the merged sell over- or under-corrected after first converging");

            // The host's own wire-side bookkeeping must show the TRUE absolute total (4), not
            // merely the delta (1) - StoreStore.Record's baseline-seeding behaviour.
            Assert.Equal(4, rig.Host.Store.Get(Spike));

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// The exact rig-round finding docs/research/store-sync.md's 2026-08-28 live pass reported:
    /// a host sells a BRAND NEW key, it mirrors to the client, and the client IMMEDIATELY buys it
    /// back (before the client's own pump has independently confirmed, via a clean scan, that the
    /// mirrored insert landed on its own). Before the 2026-08-29 engineBelief/wireTarget fix, this
    /// genuine local removal could be silently absorbed into (or permanently excluded by) the
    /// echo-suppression guard armed for the just-applied insert — the live symptom was the host's
    /// own re-buy of the "sold" item succeeding minutes later, because the host was never told the
    /// client had bought it back.
    ///
    /// Drives the CLIENT's own agent pump manually (not through the shared auto-draining
    /// <see cref="AgentLoop"/>) so this test can force the exact race: the queued apply for the
    /// mirrored insert and the client's own buy-back land in the SAME tick, so the pump's first
    /// ever fresh scan of the key already reflects the buy, never observing the insert's target
    /// "land cleanly" on its own — the scenario docs/research/store-sync.md's own hypothesis names
    /// as the one the old code could never recover from.
    /// </summary>
    [Fact]
    public async Task ClientBuyBackOfAMirroredKeyIsDetectedAndPublished()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        using AgentLoop hostAgent = new(host, "host");

        FakeStoreBridge clientBridge = new();
        CoopAgent clientAgent = new(
            clientBridge, client, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });
        List<string> clientLogs = [];
        clientAgent.Log = msg => clientLogs.Add(msg);

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        static void DrainApply(FakeStoreBridge b, CoopAgent.PendingStoreApply next)
        {
            int before = b.TrueEngineState.GetValueOrDefault(next.Key);
            int after = next.Kind == StoreOpKind.Add
                ? before + next.Count
                : Math.Max(0, before - next.Count);

            if (after > 0)
            {
                b.TrueEngineState[next.Key] = after;
            }
            else
            {
                b.TrueEngineState.Remove(next.Key);
            }
        }

        hostAgent.Bridge.TrueEngineState[Blaster] = 40;
        clientBridge.TrueEngineState[Blaster] = 40;
        hostAgent.Bridge.OpenStoreId = StoreId;
        clientBridge.OpenStoreId = StoreId;

        // Baseline both sides (design rule (a): a virgin store's first read publishes nothing).
        for (int i = 0; i < 20; i++)
        {
            clientAgent.PumpOnce();
            while (clientAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
            {
                DrainApply(clientBridge, next);
            }

            await Task.Delay(15);
        }

        // The host sells a brand-new key the store never stocked before.
        hostAgent.Bridge.TrueEngineState[Spike] = 1;

        // Pump the client, auto-draining every OTHER key as usual, until the wire mirror for
        // Spike has been queued as a pending apply — captured WITHOUT applying it yet.
        CoopAgent.PendingStoreApply? queued = null;
        for (int i = 0; i < 400 && queued is null; i++)
        {
            clientAgent.PumpOnce();
            while (clientAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
            {
                if (next.Key.Equals(Spike))
                {
                    queued = next;
                }
                else
                {
                    DrainApply(clientBridge, next);
                }
            }

            if (queued is null)
            {
                await Task.Delay(5);
            }
        }

        Assert.True(
            queued.HasValue,
            "client never queued an automatic apply for the mirrored sell - client logs:\n"
            + string.Join('\n', clientLogs));

        // THE RACE: land the queued apply AND the client's own buy-back in the same instant,
        // before calling PumpOnce again - the pump's very first fresh scan of this key already
        // reflects the buy, never observing the mirrored insert's own target land cleanly on its
        // own first.
        DrainApply(clientBridge, queued.Value);
        clientBridge.TrueEngineState.Remove(Spike);

        // Let the client keep pumping normally; the fix under test must still detect and publish
        // this genuine local removal even though it raced the pending apply's own confirmation.
        for (int i = 0; i < 400; i++)
        {
            clientAgent.PumpOnce();
            while (clientAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
            {
                DrainApply(clientBridge, next);
            }

            await Task.Delay(5);
        }

        Assert.True(
            await WaitForAsync(
                () => !hostAgent.Bridge.TrueEngineState.ContainsKey(Spike), TimeSpan.FromSeconds(10)),
            "the host's copy never reflected the client's buy-back of the just-mirrored key - "
            + $"host Spike={hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}. Client logs:\n"
            + string.Join('\n', clientLogs));

        Assert.True(
            await StaysAsync(
                () => !hostAgent.Bridge.TrueEngineState.ContainsKey(Spike)
                    && !clientBridge.TrueEngineState.ContainsKey(Spike)
                    && hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                TimeSpan.FromSeconds(3)),
            "the buy-back over- or under-corrected after first converging, or touched the "
            + "unrelated Blaster stack");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The SAME player sells twice, back to back, before the first sell's mirror has necessarily
    /// even reached the peer — both units must be reflected on the client, not just one (a lost
    /// second op) and not more than two (a double-counted first op).
    /// </summary>
    [Fact]
    public async Task SellThenImmediateResellOnHostConvergesToTheCumulativeTotal()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // Two rapid sells of a brand-new key, back to back, with only a couple of
            // milliseconds between them - deliberately faster than either side's own ~1Hz-class
            // scan cadence would need to treat them as two independent events.
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 1;
            await Task.Delay(5);
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 2;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2, Timeout),
                "client's copy never reflected BOTH rapid sells; got "
                + $"{rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}. "
                + string.Join('\n', rig.HostAgent.Logs));

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                    TimeSpan.FromSeconds(5)),
                "the double sell over- or under-corrected after first converging");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// Both players sell into the SAME brand-new key at roughly the same time — the final total
    /// on both sides must be the SUM of both contributions, not just one (a lost contributor) and
    /// not double either one.
    /// </summary>
    [Fact]
    public async Task BothSidesSellingTheSameKeyConvergesToTheSum()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // Host sells 1, client sells 1 of the same never-before-stocked key, within
            // milliseconds of each other.
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 1;
            rig.ClientAgent.Bridge.TrueEngineState[Spike] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2,
                    Timeout),
                "the two sides' sells of the same key never converged to the sum - "
                + $"host={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}, "
                + $"client={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}");

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 2,
                    TimeSpan.FromSeconds(5)),
                "the summed total over- or under-corrected after first converging");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// A reconnect exercised right after a buy-back scenario has settled: the depleted key must
    /// stay correctly empty on the rejoining client, not resurrect the item the host never
    /// actually still has (the SAME "resync must not double or resurrect" guarantee
    /// <see cref="ReconnectResyncLeavesUntransactedStoresAlone"/> pins for a still-stocked key,
    /// exercised here for a key that ended at ZERO instead).
    /// </summary>
    [Fact]
    public async Task ReconnectAfterABuyBackStaysConvergedAtZero()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // Host sells a brand-new key, mirrors to the client, then the client buys it right
            // back out - the ordinary (non-raced) shape of the buy-back, which the auto-draining
            // AgentLoop rig can exercise directly since the fix no longer depends on winning a
            // tight race to behave correctly.
            rig.HostAgent.Bridge.TrueEngineState[Spike] = 1;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 1, Timeout),
                "client never mirrored the host's sell before the buy-back leg");

            rig.ClientAgent.Bridge.TrueEngineState.Remove(Spike);

            Assert.True(
                await WaitForAsync(
                    () => !rig.HostAgent.Bridge.TrueEngineState.ContainsKey(Spike), Timeout),
                "host never learned about the client's buy-back before the reconnect");

            uint originalId = rig.Client.LocalPeerId;

            rig.Client.Network.StartBlackout(6_000);

            Assert.True(
                await WaitForAsync(() => !rig.Client.IsConnected, TimeSpan.FromSeconds(10)),
                "client never noticed the blackout");
            Assert.True(
                await WaitForAsync(() => rig.Host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(10)),
                "host never expired the blacked-out peer");
            Assert.True(
                await WaitForAsync(() => rig.Client.IsConnected, TimeSpan.FromSeconds(30)),
                "client never rejoined after the blackout ended");
            Assert.Equal(originalId, rig.Client.LocalPeerId);

            Assert.True(
                await StaysAsync(
                    () => !rig.HostAgent.Bridge.TrueEngineState.ContainsKey(Spike)
                        && !rig.ClientAgent.Bridge.TrueEngineState.ContainsKey(Spike)
                        && rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                    TimeSpan.FromSeconds(5)),
                "the reconnect resync resurrected the bought-back item, or disturbed the "
                + "unrelated Blaster stack - "
                + $"host Spike={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}, "
                + $"client Spike={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// The loop breaker: a single key racking up far more distinct local changes than any
    /// plausible human buy/sell rate within one rolling window must freeze rather than keep
    /// republishing forever, and must still re-converge (via the resync it requests) once the key
    /// settles down.
    /// </summary>
    [Fact]
    public async Task LoopBreakerFreezesARunawayKeyThenReconvergesOnceItSettles()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[Blaster] = 40;
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // Far more distinct changes to the SAME key than the breaker's own threshold allows,
            // well within one rolling window - a feedback loop (or a pathological macro), not
            // genuine play.
            for (int i = 0; i < 25; i++)
            {
                rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40 - (i % 2);
                await Task.Delay(20);
            }

            // Let the key settle on a single final value.
            rig.ClientAgent.Bridge.TrueEngineState[Blaster] = 40;

            Assert.True(
                await WaitForAsync(
                    () => rig.ClientAgent.Logs.Any(l => l.Contains("loop breaker tripped")),
                    TimeSpan.FromSeconds(10)),
                "the loop breaker never tripped for a runaway key - client logs:\n"
                + string.Join('\n', rig.ClientAgent.Logs));

            // Both sides must eventually agree again — not necessarily on 40 specifically (the
            // freeze may have cut off publishing before the final settle was itself observed),
            // but on THE SAME value as each other, once the breaker's own resync has run its
            // course.
            Assert.True(
                await WaitForAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)
                        == rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster),
                    TimeSpan.FromSeconds(25)),
                "the runaway key never re-converged after the loop breaker froze it - "
                + $"host={rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
                + $"client={rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster)}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    [Fact]
    public async Task InfiniteStockBuyProducesNoOp()
    {
        (Rig rig, CancellationTokenSource cts, Task hostTask, Task clientTask) = await BuildConnectedRigAsync();
        using (rig)
        {
            rig.HostAgent.Bridge.TrueEngineState[InfiniteAmmo] = 99;
            rig.ClientAgent.Bridge.TrueEngineState[InfiniteAmmo] = 99;
            rig.HostAgent.Bridge.OpenStoreId = StoreId;
            rig.ClientAgent.Bridge.OpenStoreId = StoreId;

            await Task.Delay(300);

            // Several "buys" against the infinite entry - the engine's own SellItem never touches
            // its stock (§1.5), so a real scan would report the SAME 99 every time.
            for (int i = 0; i < 5; i++)
            {
                rig.HostAgent.Bridge.TrueEngineState[InfiniteAmmo] = 99;
                await Task.Delay(30);
            }

            Assert.True(
                await StaysAsync(
                    () => rig.HostAgent.Bridge.TrueEngineState.GetValueOrDefault(InfiniteAmmo) == 99
                        && rig.ClientAgent.Bridge.TrueEngineState.GetValueOrDefault(InfiniteAmmo) == 99
                        && rig.Host.Store.Version == 0
                        && rig.Client.Store.Version == 0,
                    TimeSpan.FromSeconds(3)),
                "an infinite-stock item's unchanged scan produced a spurious op - "
                + $"host.Store.Version={rig.Host.Store.Version}, client.Store.Version={rig.Client.Store.Version}");

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }

    /// <summary>
    /// The module-scoping fix (docs/research/store-sync.md's own "discrepancy" write-up, task 2):
    /// a store's object id is only meaningful within the module that placed it, and the engine is
    /// free to reuse the SAME numeric id for an entirely unrelated store once a DIFFERENT module
    /// loads. Before this fix, <see cref="Pumps.StorePump"/> kept its per-key belief
    /// (<c>_engineBelief</c>/<c>_wireTarget</c>) keyed purely by id, with no module boundary at
    /// all — so a fresh module's genuinely pristine stock (never transacted, never desired to be
    /// anything but what it is) could be misread as a DELTA against a totally unrelated belief
    /// left over from the module just departed, and published/mirrored as a spurious op linking
    /// two stores that have nothing to do with each other.
    /// </summary>
    [Fact]
    public async Task AModuleChangeClearsStaleStoreBeliefRatherThanMisreadingTheNewModulesStock()
    {
        // Driven MANUALLY (both sides), not the auto-draining Rig/AgentLoop pair every other test
        // in this file uses — deliberately. The scenario needs the module label and the fresh
        // "new module" stock value to become visible to a pump in the SAME instant (the first
        // pump against the new module must never see the module changed but the stock still
        // reading the old module's post-transaction value, or vice versa); a background task
        // pumping concurrently on its own 3 ms cadence cannot be trusted not to interleave a
        // Pump() call between those two field writes, which produced a genuine test-harness race
        // (an early, spurious re-baseline against the still-old stock value) distinct from the
        // production defect this test exists to catch. Full manual control removes it.
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        FakeStoreBridge hostBridge = new();
        CoopAgent hostAgent = new(
            hostBridge, host, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });
        FakeStoreBridge clientBridge = new();
        CoopAgent clientAgent = new(
            clientBridge, client, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        static void DrainApply(FakeStoreBridge b, CoopAgent.PendingStoreApply next)
        {
            int before = b.TrueEngineState.GetValueOrDefault(next.Key);
            int after = next.Kind == StoreOpKind.Add
                ? before + next.Count
                : Math.Max(0, before - next.Count);

            if (after > 0)
            {
                b.TrueEngineState[next.Key] = after;
            }
            else
            {
                b.TrueEngineState.Remove(next.Key);
            }
        }

        async Task PumpBothOnceAsync(int delayMs = 10)
        {
            hostAgent.PumpOnce();
            while (hostAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
            {
                DrainApply(hostBridge, next);
            }

            clientAgent.PumpOnce();
            while (clientAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply next))
            {
                DrainApply(clientBridge, next);
            }

            await Task.Delay(delayMs);
        }

        hostBridge.TrueEngineState[Blaster] = 40;
        clientBridge.TrueEngineState[Blaster] = 40;
        hostBridge.OpenStoreId = StoreId;
        clientBridge.OpenStoreId = StoreId;

        for (int i = 0; i < 20; i++)
        {
            await PumpBothOnceAsync(15);
        }

        // A genuine transaction in the FIRST module, converged on both sides — establishes a
        // real, non-zero belief (39) for (StoreId, Blaster) that a module-unaware pump would
        // otherwise carry forward.
        hostBridge.TrueEngineState[Blaster] = 39;

        for (int i = 0; i < 400 && clientBridge.TrueEngineState.GetValueOrDefault(Blaster) != 39; i++)
        {
            await PumpBothOnceAsync(5);
        }

        Assert.Equal(39, clientBridge.TrueEngineState.GetValueOrDefault(Blaster));

        uint versionBeforeTransition = host.Store.Version;

        // The module changes UNDERNEATH the still-open store — deliberately NOT toggling
        // OpenStoreId through 0 first (a plain close/reopen already resets
        // _openStoreBaselined on its own, per the pre-existing "no store open" branch; that path
        // is not what this test is for). This is the case that path does NOT cover: the bridge's
        // own "is a store open" read staying nonzero straight through the transition (a fast
        // module load, or a pump tick that simply does not land inside the gap) — the ONLY
        // signal StorePump has left that anything changed is the module name itself. The NEW
        // module happens to place an entirely unrelated store at the SAME object id (StoreId),
        // with its own pristine, never-transacted stock of 40 — NOT 39 — and this becomes true in
        // the SAME instant the module label does, so no pump can ever observe one without the
        // other: without the module-clear fix, both sides' engine belief still holds "39" from
        // the departed module, so this fresh scan (40) would read as a genuine local Add of 1 and
        // be published/mirrored — a spurious op linking two stores that share nothing but a
        // reused numeric id.
        hostBridge.LocalModule = OtherModule;
        clientBridge.LocalModule = OtherModule;
        hostBridge.TrueEngineState.Clear();
        hostBridge.TrueEngineState[Blaster] = 40;
        clientBridge.TrueEngineState.Clear();
        clientBridge.TrueEngineState[Blaster] = 40;

        for (int i = 0; i < 30; i++)
        {
            await PumpBothOnceAsync(10);
        }

        Assert.True(
            hostBridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                && clientBridge.TrueEngineState.GetValueOrDefault(Blaster) == 40
                && host.Store.Version == versionBeforeTransition
                && client.Store.Version == versionBeforeTransition,
            "the new module's pristine stock was misread as a delta against the OLD "
            + "module's belief and published/mirrored - "
            + $"host={hostBridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
            + $"client={clientBridge.TrueEngineState.GetValueOrDefault(Blaster)}, "
            + $"host.Store.Version {versionBeforeTransition}->{host.Store.Version}, "
            + $"client.Store.Version {versionBeforeTransition}->{client.Store.Version}");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The other half of the same fix: a REMOTE op still queued (but not yet applied through the
    /// single-slot cheat channel) for the OLD module's store id must not land against whatever
    /// object the NEW module now happens to hold that same id — the module change must drop the
    /// pump's own optimistic apply queue, not merely its belief dictionaries.
    /// </summary>
    [Fact]
    public async Task AModuleChangeDropsAnyApplyStillQueuedForTheOldModule()
    {
        CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        using AgentLoop hostAgent = new(host, "host");

        FakeStoreBridge clientBridge = new();
        CoopAgent clientAgent = new(
            clientBridge, client, new AgentSettings { HeavyPumpIntervalMs = 0, StoreSyncEnabled = true, StoreSyncMode = 1 });

        Assert.True(
            await WaitForAsync(() => client.IsConnected && host.GetRemotePeers().Count == 1, Timeout),
            "host/client never connected");

        hostAgent.Bridge.TrueEngineState[Blaster] = 40;
        clientBridge.TrueEngineState[Blaster] = 40;
        hostAgent.Bridge.OpenStoreId = StoreId;
        clientBridge.OpenStoreId = StoreId;

        // Baseline the client manually (PumpOnce, not the auto-draining AgentLoop) so the
        // queued-apply race below can be forced deterministically.
        for (int i = 0; i < 20; i++)
        {
            clientAgent.PumpOnce();
            await Task.Delay(15);
        }

        // The host sells a brand-new key. The client's next pump queues the mirrored insert as a
        // PendingStoreApply WITHOUT draining it — exactly ClientBuyBackOfAMirroredKeyIsDetectedAndPublished's
        // own race-forcing shape, reused here to leave an apply genuinely in flight.
        hostAgent.Bridge.TrueEngineState[Spike] = 1;

        bool queued = false;
        for (int i = 0; i < 400 && !queued; i++)
        {
            clientAgent.PumpOnce();
            if (clientAgent.TryDequeuePendingStoreApply(out CoopAgent.PendingStoreApply _))
            {
                // Do not apply it - leave the pump believing one more is still in flight isn't
                // possible (TryDequeuePendingApply already removed it from the pump's own
                // queue), so instead re-arm by requeuing nothing: the DEQUEUE ITSELF is what
                // this test needs to have happened, proving the channel had something to apply.
                queued = true;
            }

            if (!queued)
            {
                await Task.Delay(5);
            }
        }

        Assert.True(queued, "the client never queued an automatic apply for the mirrored sell");

        // The module changes NOW, before the dequeued apply (already handed to the single-slot
        // cheat channel in a real run) would have landed in the engine. Without the fix, the
        // client's own _wireTarget[Spike] entry (armed when the apply above was queued) survives
        // the module change untouched.
        clientBridge.LocalModule = OtherModule;
        clientBridge.OpenStoreId = 0;
        clientAgent.PumpOnce();

        // Re-open "the same" numeric StoreId in the NEW module — a totally unrelated, pristine
        // store that has never heard of Spike at all. Without the fix, StorePump's per-key visit
        // loop unions in every key _wireTarget still holds for this store id (docs/research/
        // store-sync.md's own "the exact buy-back race" comment on that union) — including the
        // now-orphaned Spike entry — computes excess = actual(0, Spike is absent from this
        // module's scan) - target(1) = -1, and PUBLISHES A SPURIOUS REMOVE for a key this
        // module's store never stocked, silently zeroing the HOST's own genuine sale of it.
        clientBridge.TrueEngineState.Clear();
        clientBridge.TrueEngineState[Blaster] = 12;
        clientBridge.OpenStoreId = StoreId;

        for (int i = 0; i < 20; i++)
        {
            clientAgent.PumpOnce();
            await Task.Delay(15);
        }

        Assert.True(
            await StaysAsync(
                () => hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Spike) == 1
                    && hostAgent.Bridge.TrueEngineState.GetValueOrDefault(Blaster) == 40,
                TimeSpan.FromSeconds(2)),
            "a stale apply/wire-target entry left over from the OLD module reached the host as a "
            + "spurious op against the NEW module's unrelated store - host state: "
            + string.Join(", ", hostAgent.Bridge.TrueEngineState.Select(kv => $"{kv.Key.Resref}={kv.Value}")));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
