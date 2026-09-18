using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Drives <see cref="PartyPoolPumpV2"/>/<see cref="StorePumpV2"/> DIRECTLY — a stub session and a
/// scriptable fake bridge, no sockets, no real <c>CoopAgent</c> loop, no wall-clock waiting. This
/// is deliberately a different vehicle from <see cref="SharedInventoryV2LoopbackTests"/>: that
/// suite proves the two halves converge end to end, and by construction can only ever observe
/// what the ENGINE ends up holding. Every defect the three live rig rounds recorded
/// (docs/research/shared-inventory-v2.md §6, §8, §10) was instead a bookkeeping decision INSIDE
/// one pump tick — a scan that failed, a belief that never advanced, a suppressed stack merging —
/// which a loopback rig with a perfect fake bridge can neither produce nor see. These tests pin
/// those decisions one tick at a time.
///
/// See §12 for the round that wrote them.
/// </summary>
public class SharedInventoryV2PumpTests
{
    private const SessionCapabilities Caps = SessionCapabilities.PartyPool | SessionCapabilities.Store;

    private const uint StoreA = 0x74;

    private static readonly PartyPoolStackKey Medpac =
        new("g_i_medeqpmnt01", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey StoreIon =
        new(StoreA, "g_w_ionblstr01", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey StoreBelt =
        new(StoreA, "g_i_belt006", 0, 0, 0, ItemIdentity.FlagKnown);

    /// <summary>Scriptable stand-in for <c>KotorGameBridge</c>'s two scan surfaces: every failure
    /// mode the real bridge documents is reachable from a test, which the loopback suite's own
    /// always-succeeds fake never was.</summary>
    private sealed class ScriptedBridge : IGameBridge
    {
        public Dictionary<PartyPoolStackKey, int> Pool { get; set; } = [];

        public Dictionary<StoreStackKey, int> Store { get; set; } = [];

        /// <summary>When true, <see cref="TryScanPartyPoolTotals"/> answers exactly the way the
        /// real bridge does before its first successful sweep, or after a failed pointer read:
        /// FALSE, with an EMPTY dictionary. See KotorGameBridge.PartyPool.cs.</summary>
        public bool PoolScanFails { get; set; }

        /// <summary>When true, <see cref="TryScanPartyPoolTotals"/> answers the way the real
        /// bridge does when it CANNOT vouch for what it walked — an unresolvable id, an
        /// unreadable pointer chain: it hands back the LAST GOOD sweep and returns TRUE, with
        /// <see cref="LastPartyPoolScanWasStale"/> saying so. See
        /// IPartyPoolBridge.LastPartyPoolScanWasStale for why the difference matters.</summary>
        public bool PoolScanStale { get; set; }

        public bool LastPartyPoolScanWasStale { get; private set; }

        private Dictionary<PartyPoolStackKey, int> _lastGoodPool = [];

        /// <summary>0 = no store GUI open here this tick (the ordinary case).</summary>
        public uint OpenStoreId { get; set; }

        public int PoolScanEpoch { get; set; }

        public int PoolCacheInvalidations { get; private set; }

        public string Description => "shared-inventory-v2-pump-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartyPool => true;

        public bool SupportsStore => true;

        public int PartyPoolScanEpoch => PoolScanEpoch;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            if (PoolScanFails)
            {
                LastPartyPoolScanWasStale = true;
                totals = new Dictionary<PartyPoolStackKey, int>();
                return false;
            }

            if (PoolScanStale)
            {
                LastPartyPoolScanWasStale = true;
                totals = new Dictionary<PartyPoolStackKey, int>(_lastGoodPool);
                return true;
            }

            LastPartyPoolScanWasStale = false;
            _lastGoodPool = new Dictionary<PartyPoolStackKey, int>(Pool);
            totals = new Dictionary<PartyPoolStackKey, int>(Pool);
            return true;
        }

        public void InvalidatePartyPoolScanCache() => PoolCacheInvalidations++;

        public bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
        {
            storeObjectId = 0;
            totals = new Dictionary<StoreStackKey, int>();

            if (OpenStoreId == 0)
            {
                return false;
            }

            Dictionary<StoreStackKey, int> scan = [];
            foreach ((StoreStackKey key, int count) in Store)
            {
                if (key.StoreObjectId == OpenStoreId)
                {
                    scan[key] = count;
                }
            }

            storeObjectId = OpenStoreId;
            totals = scan;
            return true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A client session: it never publishes a snapshot, it only receives one and records
    /// whatever intents the pump decides to send.</summary>
    private sealed class StubClientSession : ICoopSession
    {
        private uint _nextSerial = 1;

        public bool IsEnemyAuthority => false;

        public Dictionary<PartyPoolStackKey, int> PoolSnapshot { get; set; } = [];

        public bool PoolSnapshotHeard { get; set; } = true;

        public Dictionary<StoreStackKey, int> StoreSnapshot { get; set; } = [];

        public bool StoreSnapshotHeard { get; set; } = true;

        public HashSet<uint> KnownStores { get; } = [];

        public List<(PartyPoolOpKind Kind, PartyPoolStackKey Key, ushort Count)> PoolIntents { get; } = [];

        public List<(StoreOpKind Kind, StoreStackKey Key, ushort Count)> StoreIntents { get; } = [];

        public bool TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
        {
            totals = new Dictionary<PartyPoolStackKey, int>(PoolSnapshot);
            return PoolSnapshotHeard;
        }

        public uint PublishPartyPoolIntentV2(PartyPoolOpKind kind, PartyPoolStackKey key, ushort count)
        {
            PoolIntents.Add((kind, key, count));
            return _nextSerial++;
        }

        public bool TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> totals)
        {
            totals = new Dictionary<StoreStackKey, int>(StoreSnapshot);
            return StoreSnapshotHeard;
        }

        public uint PublishStoreIntentV2(StoreOpKind kind, StoreStackKey key, ushort count)
        {
            StoreIntents.Add((kind, key, count));
            return _nextSerial++;
        }

        public bool IsStoreKnownV2(uint storeObjectId) => KnownStores.Contains(storeObjectId);

        // ---- everything below is ICoopSession surface these pumps never touch ------------

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state) { }

        public void SetFlag(ulong flagId, sbyte value) { }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId) { }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies) { }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

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

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    /// <summary>Drains every apply the pump has queued and applies it to the bridge's own engine
    /// state, exactly as the single-slot cheat channel eventually would — the "landed" case.
    /// Returns how many landed.</summary>
    private static int DrainPoolApplies(PartyPoolPumpV2 pump, ScriptedBridge bridge)
    {
        int landed = 0;
        while (pump.TryDequeuePendingApply(out CoopAgent.PendingPartyPoolApply next))
        {
            int delta = next.Kind == PartyPoolOpKind.Add ? next.Count : -next.Count;
            int now = bridge.Pool.GetValueOrDefault(next.Key) + delta;
            if (now > 0)
            {
                bridge.Pool[next.Key] = now;
            }
            else
            {
                bridge.Pool.Remove(next.Key);
            }

            landed++;
        }

        return landed;
    }

    /// <summary>Dequeues without applying — the apply is "on its way" through the cheat channel
    /// but has not landed yet, which is what the pump's own expiry bookkeeping is about.</summary>
    private static int DispatchPoolAppliesWithoutLanding(PartyPoolPumpV2 pump)
    {
        int dispatched = 0;
        while (pump.TryDequeuePendingApply(out _))
        {
            dispatched++;
        }

        return dispatched;
    }

    private static int DrainStoreApplies(StorePumpV2 pump, ScriptedBridge bridge)
    {
        int landed = 0;
        while (pump.TryDequeuePendingApply(out CoopAgent.PendingStoreApply next))
        {
            int delta = next.Kind == StoreOpKind.Add ? next.Count : -next.Count;
            int now = bridge.Store.GetValueOrDefault(next.Key) + delta;
            if (now > 0)
            {
                bridge.Store[next.Key] = now;
            }
            else
            {
                bridge.Store.Remove(next.Key);
            }

            landed++;
        }

        return landed;
    }

    private static int CountStoreApplies(StorePumpV2 pump)
    {
        int seen = 0;
        while (pump.TryDequeuePendingApply(out _))
        {
            seen++;
        }

        return seen;
    }

    // ---- defect A: the store's ~10s re-insertion loop -----------------------------------

    /// <summary>
    /// docs/research/shared-inventory-v2.md §10.2, the single most reproducible defect this
    /// family has produced: with the client's own store GUI closed, EVERY known key of a store
    /// was re-inserted into the client's engine roughly every 10 seconds, forever (432 inserts
    /// across 18 keys in 13 minutes; the host's log clean at zero).
    ///
    /// The mechanism, one tick at a time: <c>_belief[key]</c> was only ever written inside the
    /// <c>liveThisTick</c> branch, so for a closed store it stayed 0 permanently; the reconcile
    /// was held off re-firing by its expected-movement entry ALONE; and that entry expired on a
    /// blind wall clock even though, with no scan possible, NOTHING could ever consume it. The
    /// moment it expired, <c>projected</c> collapsed back to <c>belief</c> and the identical
    /// insert was queued again.
    ///
    /// Runs with a deliberately tiny expiry so the loop, if present, fires many times inside a
    /// second rather than once every ten. Against the pre-fix pump this queues an insert per
    /// expiry window; it must queue exactly ONE, ever.
    /// </summary>
    [Fact]
    public void AShopThisClientHasNeverOpenedIsNeverBlindlyRefilledFromTheHostsSnapshot()
    {
        // The client's own copy of the shop already holds the same pristine stock the host just
        // published — both instances loaded the same module. The client cannot SEE that (no GUI
        // open, ever, which is exactly what happened on the rig), so the only thing that keeps it
        // from "converging" a shop that was already correct is treating a store's FIRST snapshot
        // as its baseline rather than as a gap to close.
        ScriptedBridge bridge = new() { OpenStoreId = 0 };
        bridge.Store[StoreIon] = 2;
        bridge.Store[StoreBelt] = 5;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreIon] = 2;
        session.StoreSnapshot[StoreBelt] = 5;

        StorePumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 50);

        int applies = 0;
        for (int i = 0; i < 10; i++)
        {
            pump.Pump(Caps);
            applies += CountStoreApplies(pump);
            Thread.Sleep(20);
        }

        Assert.Equal(0, applies);
        Assert.Empty(session.StoreIntents);
        Assert.Equal(2, bridge.Store.GetValueOrDefault(StoreIon));
        Assert.Equal(5, bridge.Store.GetValueOrDefault(StoreBelt));
    }

    [Fact]
    public void AReconcileForAStoreNobodyCanScanIsNeverQueuedTwice()
    {
        ScriptedBridge bridge = new() { OpenStoreId = 0 };
        bridge.Store[StoreBelt] = 5;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreBelt] = 5;

        StorePumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 50);

        // Tick 1 baselines the shop against its own pristine stock: nothing to do.
        pump.Pump(Caps);
        Assert.Equal(0, CountStoreApplies(pump));

        // NOW the host genuinely sells something into it — a real change to mirror, and the one
        // and only apply this client may ever queue for it.
        session.StoreSnapshot[StoreIon] = 1;
        pump.Pump(Caps);
        Assert.Equal(1, CountStoreApplies(pump));

        // Ten more ticks spread across many expiry windows. The store still cannot be scanned, so
        // nothing new can be known and nothing more may be asked for. Against the pre-fix pump
        // this queues another insert every time the expected-movement entry blindly expires.
        int extra = 0;
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(30);
            pump.Pump(Caps);
            extra += CountStoreApplies(pump);
        }

        Assert.Equal(0, extra);
    }

    /// <summary>The same closed-GUI reconcile must still MIRROR a genuine later host-side change
    /// (item 2 of §9's own requirement) — the fix above must not have frozen the channel.</summary>
    [Fact]
    public void AClosedStoreStillMirrorsAFurtherHostSideChange()
    {
        ScriptedBridge bridge = new() { OpenStoreId = 0 };
        bridge.Store[StoreIon] = 1;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreIon] = 1;

        StorePumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 50);

        pump.Pump(Caps);
        Assert.Equal(0, DrainStoreApplies(pump, bridge));

        // The host sells a second one into the same shop.
        session.StoreSnapshot[StoreIon] = 2;
        pump.Pump(Caps);
        Assert.Equal(1, DrainStoreApplies(pump, bridge));
        Assert.Equal(2, bridge.Store.GetValueOrDefault(StoreIon));

        // ...and a third, minutes later.
        session.StoreSnapshot[StoreIon] = 3;
        pump.Pump(Caps);
        Assert.Equal(1, DrainStoreApplies(pump, bridge));
        Assert.Equal(3, bridge.Store.GetValueOrDefault(StoreIon));

        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(30);
            pump.Pump(Caps);
            Assert.Equal(0, CountStoreApplies(pump));
        }
    }

    /// <summary>A belief this pump only ASSUMED (blind, from the host's snapshot) must be
    /// re-seeded by the first real scan of that store, never diffed against — the difference
    /// between a guess and reality is not a player action, and reporting it as one is a phantom
    /// sale landing on the host's canonical engine.</summary>
    [Fact]
    public void TheFirstRealScanOfAStoreReSeedsAnAssumedBeliefInsteadOfReportingTheGap()
    {
        ScriptedBridge bridge = new() { OpenStoreId = 0 };
        bridge.Store[StoreIon] = 1;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreIon] = 3;

        StorePumpV2 pump = new(bridge, session, _ => { });

        // Blind: belief is assumed to be 3, the engine actually holds 1, and the queued reconcile
        // is never drained (the cheat channel is busy).
        pump.Pump(Caps);
        pump.Pump(Caps);

        // The player finally opens the shop. The gap between the assumption and reality must not
        // read as "someone sold two of these here".
        bridge.OpenStoreId = StoreA;
        pump.Pump(Caps);

        Assert.Empty(session.StoreIntents);
    }

    /// <summary>
    /// The other half of §10.2/§6.4: opening a shop the host has already published must not read
    /// that shop's own pristine stock as a fresh local DEPOSIT and report it. Both instances
    /// loaded the same module; the stock is standing shared content, not anybody's sale. Before
    /// the first-sight seeding fix, <c>belief</c> started at 0 and the entire shop was published
    /// as an intent the host would then have applied on top of its own copy.
    /// </summary>
    [Fact]
    public void OpeningAShopTheHostAlreadyPublishedNeverReportsItsPristineStockAsADeposit()
    {
        ScriptedBridge bridge = new() { OpenStoreId = StoreA };
        bridge.Store[StoreIon] = 2;
        bridge.Store[StoreBelt] = 5;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreIon] = 2;
        session.StoreSnapshot[StoreBelt] = 5;

        StorePumpV2 pump = new(bridge, session, _ => { });

        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.StoreIntents);
        Assert.Equal(0, CountStoreApplies(pump));
    }

    /// <summary>...but a genuine local surplus BEYOND what the host knows is still reported on
    /// the very first tick, so first-sight seeding cannot be used to swallow a real sale.</summary>
    [Fact]
    public void ALocalSurplusBeyondTheHostsSnapshotIsStillReportedOnFirstSight()
    {
        ScriptedBridge bridge = new() { OpenStoreId = StoreA };
        bridge.Store[StoreIon] = 3;

        StubClientSession session = new();
        session.KnownStores.Add(StoreA);
        session.StoreSnapshot[StoreIon] = 1;

        StorePumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);

        Assert.Single(session.StoreIntents);
        Assert.Equal(StoreOpKind.Add, session.StoreIntents[0].Kind);
        Assert.Equal(2, session.StoreIntents[0].Count);
    }

    // ---- defect B1: a failed party-pool scan is NOT an empty pool ------------------------

    /// <summary>
    /// <c>PartyPoolPumpV2.PumpClient</c> ignored <c>TryScanPartyPoolTotals</c>'s return value.
    /// The bridge's own contract could not be clearer — "False when there is no world loaded yet
    /// (main menu, loading screen) or the read otherwise failed — the caller must not treat that
    /// as 'the pool is empty'" — and in that case it hands back an EMPTY dictionary. So a failed
    /// scan read as every key at once dropping to zero: a phantom REMOVE per key, published to
    /// the host and applied to the host's own canonical engine, followed by the mirror-image
    /// phantom ADD once the scan started working again. That is the exact fabricated-movement
    /// shape §10.1's "local add of 6x nobody asked for" took.
    /// </summary>
    [Fact]
    public void AFailedPartyPoolScanIsNoObservationNotAnEmptyPool()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 4;

        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 4;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });

        pump.Pump(Caps);
        Assert.Empty(session.PoolIntents);

        // The world goes away for a few ticks (a transition, a loading screen, a transiently
        // unreadable pointer chain) — the bridge says so, honestly, every time.
        bridge.PoolScanFails = true;
        for (int i = 0; i < 5; i++)
        {
            pump.Pump(Caps);
        }

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));

        // ...and when it comes back, nothing has changed, so still nothing to report.
        bridge.PoolScanFails = false;
        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(4, bridge.Pool.GetValueOrDefault(Medpac));
    }

    // ---- defect B3 (first sight) --------------------------------------------------------

    /// <summary>
    /// §9 replaced the old "seed silently, defer a tick" first-sight branch with a flat belief of
    /// 0, which publishes a key's ENTIRE standing quantity as a fresh local deposit the first
    /// time the pump ever looks at it. Both instances load the same save and hold the same pool,
    /// so on a real rig that is a guaranteed publish-then-double of every stack in the stash.
    /// Seeding to <c>min(actual, snapshot)</c> is what splits standing shared content from a
    /// genuine local surplus with no heuristic at all.
    /// </summary>
    [Fact]
    public void StandingContentTheHostAlreadyAgreesWithIsNeverPublishedAsADeposit()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 6;

        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 6;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));
    }

    /// <summary>The regression §9's own removal of the seed branch was protecting (its
    /// <c>ThreeRapidClientDeposits</c> case): a genuine local deposit the host has NOT got must
    /// still be published immediately on first sight, not silently absorbed.</summary>
    [Fact]
    public void AGenuineLocalDepositTheHostHasNotGotIsStillPublishedOnFirstSight()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 3;

        StubClientSession session = new();

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);

        Assert.Single(session.PoolIntents);
        Assert.Equal(PartyPoolOpKind.Add, session.PoolIntents[0].Kind);
        Assert.Equal(3, session.PoolIntents[0].Count);
    }

    /// <summary>Partial agreement: the host knows about 2 of the 5 sitting here, so exactly the
    /// 3-unit surplus is a local action and the other 2 never were.</summary>
    [Fact]
    public void OnlyTheSurplusBeyondTheHostsOwnSnapshotIsPublishedOnFirstSight()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 5;

        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 2;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);

        Assert.Single(session.PoolIntents);
        Assert.Equal(PartyPoolOpKind.Add, session.PoolIntents[0].Kind);
        Assert.Equal(3, session.PoolIntents[0].Count);
    }

    // ---- defect B2: the scan cache -------------------------------------------------------

    /// <summary>
    /// docs/research/shared-inventory-v2.md §2.8 claimed mode 2 invalidates the bridge's scan
    /// cache "the moment a mode-2 pump queues a reconcile apply, for the identical reason mode 1
    /// calls it". It never did — the call appears nowhere in either v2 pump. With the real
    /// bridge's 1-second throttle that means a landed reconcile can be read out of a stale cache
    /// up to a full window later, i.e. on a tick whose expected-movement bookkeeping has already
    /// moved on, which is precisely how a reconcile's own landing gets mistaken for a fresh local
    /// action (§8.1's live finding).
    /// </summary>
    [Fact]
    public void QueuingAReconcileInvalidatesTheBridgeScanCache()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 2;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });

        Assert.Equal(0, bridge.PoolCacheInvalidations);
        pump.Pump(Caps);
        Assert.True(bridge.PoolCacheInvalidations >= 1, "a queued reconcile must force the next scan fresh");
    }

    /// <summary>The detect half of the same rule — mode 1's own <c>PublishDetected</c> has always
    /// done this so a second rapid change is not masked behind the rest of a stale window.</summary>
    [Fact]
    public void PublishingADetectedLocalChangeInvalidatesTheBridgeScanCache()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 3;
        StubClientSession session = new();

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);

        Assert.Single(session.PoolIntents);
        Assert.True(bridge.PoolCacheInvalidations >= 1);
    }

    // ---- defect B3 (echo guard robustness) ----------------------------------------------

    /// <summary>
    /// An expected-movement entry whose apply has not even left this pump's own queue yet cannot
    /// possibly have landed, so "presumed resolved, re-evaluating fresh" would be a lie — and a
    /// costly one: the gap re-fires and a second, identical apply is queued behind the first,
    /// which the engine then applies TWICE. A burst drains one apply per dispatcher poll, so a
    /// wall clock started at QUEUE time expires the tail of any long burst while it is still
    /// sitting there. The clock now starts at dispatch.
    /// </summary>
    [Fact]
    public void AnExpectedMovementNeverExpiresWhileItsOwnApplyIsStillQueued()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 3;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 30);

        pump.Pump(Caps);

        // Nothing drains the apply queue at all — the dispatcher is busy with a human's own
        // console verb, exactly the single-slot contention root cause 7 documented.
        for (int i = 0; i < 8; i++)
        {
            Thread.Sleep(20);
            pump.Pump(Caps);
        }

        Assert.Equal(1, DispatchPoolAppliesWithoutLanding(pump));
    }

    /// <summary>...but once it HAS been dispatched and still never shows up in a scan, the
    /// timeout must still fire — that self-heal (§7's own Bug 4) is what stops a swallowed
    /// landing from stalling a key forever.</summary>
    [Fact]
    public void AnExpectedMovementStillExpiresOnceDispatchedAndNeverObserved()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 3;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 30);

        pump.Pump(Caps);
        Assert.Equal(1, DispatchPoolAppliesWithoutLanding(pump));

        // The cheat channel swallowed it: dispatched, never landed, never observable.
        Thread.Sleep(80);
        pump.Pump(Caps);

        Assert.Equal(1, DispatchPoolAppliesWithoutLanding(pump));
    }

    /// <summary>A reconcile that DOES land is netted out against the expected ledger and never
    /// republished as a local action — the §8.1 client-echo case, pinned at tick granularity
    /// rather than inferred from log timestamps.</summary>
    [Fact]
    public void AReconcileLandingIsNeverRepublishedAsALocalChange()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 3;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });

        pump.Pump(Caps);
        Assert.Equal(1, DrainPoolApplies(pump, bridge));
        Assert.Equal(3, bridge.Pool.GetValueOrDefault(Medpac));

        for (int i = 0; i < 6; i++)
        {
            pump.Pump(Caps);
            DrainPoolApplies(pump, bridge);
        }

        Assert.Empty(session.PoolIntents);
        Assert.Equal(3, bridge.Pool.GetValueOrDefault(Medpac));
    }

    // ---- the bridge-side reset (module change) ------------------------------------------

    /// <summary>
    /// A module change drops the bridge's ambient-noise baseline, so the same unchanged pool can
    /// report LARGER totals than it did a tick before. That step is not engine movement; read as
    /// one it is a fabricated multi-unit deposit published to the host — and the host applies it,
    /// permanently diverging the two engines (§8.1/§10.1's own shape). The bridge now reports the
    /// reset through <see cref="IPartyPoolBridge.PartyPoolScanEpoch"/> and the pump re-seeds.
    /// </summary>
    [Fact]
    public void ABridgeSideBookkeepingResetIsReSeededNotDetectedAsADeposit()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 2;

        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 2;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);
        Assert.Empty(session.PoolIntents);

        // The module changed: the bridge dropped its suppression baseline, so six units of
        // ambient content it had been netting out are suddenly part of the raw total on BOTH
        // instances at once.
        bridge.PoolScanEpoch++;
        bridge.Pool[Medpac] = 8;
        session.PoolSnapshot[Medpac] = 8;

        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));
    }

    // ---- the trace channel (item C) ------------------------------------------------------

    /// <summary>`party_pool_trace=1` must actually produce the per-key belief/actual/expected/
    /// published line the next live round is meant to diagnose from, and must stay silent when
    /// off (it is several lines a second on a busy pool).</summary>
    [Fact]
    public void TheTraceChannelPrintsPerKeyBookkeepingOnlyWhenItIsOn()
    {
        ScriptedBridge bridge = new();
        bridge.Pool[Medpac] = 3;

        StubClientSession quiet = new();
        List<string> quietLines = [];
        new PartyPoolPumpV2(bridge, quiet, quietLines.Add).Pump(Caps);
        Assert.DoesNotContain(quietLines, l => l.Contains("party pool trace:", StringComparison.Ordinal));

        StubClientSession loud = new();
        List<string> loudLines = [];
        new PartyPoolPumpV2(bridge, loud, loudLines.Add, trace: true).Pump(Caps);

        string line = Assert.Single(
            loudLines, l => l.Contains("party pool trace:", StringComparison.Ordinal));
        Assert.Contains("g_i_medeqpmnt01", line, StringComparison.Ordinal);
        Assert.Contains("actual=3", line, StringComparison.Ordinal);
        Assert.Contains("belief=0", line, StringComparison.Ordinal);
        Assert.Contains("published=3", line, StringComparison.Ordinal);
    }

    // ---- defect E: one resref under two stack keys (§13.2) --------------------------------

    /// <summary>Aggregates a pool the way <c>KotorGameBridge.PartyPool.cs</c> does — key each
    /// live item by <see cref="PartyPoolStackKey.From"/> and sum the stack sizes. Going through
    /// the real key derivation is the point: if the flags narrowing ever regresses, these item
    /// lists stop collapsing to one key and the test sees it.</summary>
    private static Dictionary<PartyPoolStackKey, int> Aggregate(
        params (string Resref, byte Flags, int Count)[] items)
    {
        Dictionary<PartyPoolStackKey, int> totals = [];
        foreach ((string resref, byte flags, int count) in items)
        {
            PartyPoolStackKey key = PartyPoolStackKey.From(
                resref, new ItemIdentity((ushort)count, 0, 0, 0, flags));
            totals[key] = totals.GetValueOrDefault(key) + count;
        }

        return totals;
    }

    /// <summary>
    /// docs/research/shared-inventory-v2.md §13.2, reproduced at the altitude it actually lives
    /// at. The pool holds two medpac bodies the engine considers ONE stack: the save's own, which
    /// are identified (`/f3`), and units a cheat/apply created, which are not (`/f1`). The engine
    /// then merges them — an ordinary thing for it to do, since its own merge predicate at
    /// 0x00553CF0 never looks at the identified bit — and every unit ends up on a single body.
    ///
    /// NOTHING LEFT OR ENTERED THE POOL. Five units before, five after. The pump must therefore
    /// publish nothing at all. Against the pre-fix keying this tick was a fabricated
    /// <c>Remove</c> of the losing key plus a fabricated <c>Add</c> of the winning one — both
    /// published, both applied by the host, which is how §13.2's three real deposits became
    /// "client 12, host 10".
    /// </summary>
    [Fact]
    public void AStackMergingAcrossTheIdentifiedBitPublishesNoMovementAtAll()
    {
        const string Resref = "g_i_medeqpmnt01";
        const byte Identified = ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified;
        const byte Plain = ItemIdentity.FlagKnown;

        ScriptedBridge bridge = new();
        StubClientSession session = new();

        bridge.Pool = Aggregate((Resref, Identified, 2), (Resref, Plain, 3));
        session.PoolSnapshot = Aggregate((Resref, Identified, 2), (Resref, Plain, 3));

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);

        // The merge lands: all five units now sit on the identified body, none on the other.
        bridge.Pool = Aggregate((Resref, Identified, 5));

        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));

        // ...and the mirror image, which is the same defect seen from the other end: a merge can
        // destroy EITHER body, so the reverse migration must be equally invisible.
        bridge.Pool = Aggregate((Resref, Plain, 5));

        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));
    }

    /// <summary>The narrowing must not have made the pump BLIND: a genuine deposit onto the very
    /// key a merge migrates within is still detected and published exactly once, and the merge
    /// that follows it adds nothing.</summary>
    [Fact]
    public void AGenuineDepositOnTheMergedKeyIsStillPublishedExactlyOnce()
    {
        const string Resref = "g_i_medeqpmnt01";
        const byte Identified = ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified;
        const byte Plain = ItemIdentity.FlagKnown;

        ScriptedBridge bridge = new();
        StubClientSession session = new();

        bridge.Pool = Aggregate((Resref, Identified, 2));
        session.PoolSnapshot = Aggregate((Resref, Identified, 2));

        PartyPoolPumpV2 pump = new(bridge, session, _ => { });
        pump.Pump(Caps);
        Assert.Empty(session.PoolIntents);

        // A real local deposit of 3, which arrives (as a cheat-created body always did) without
        // the identified bit — and merges into the save's stack a tick later.
        bridge.Pool = Aggregate((Resref, Identified, 2), (Resref, Plain, 3));
        pump.Pump(Caps);

        (PartyPoolOpKind kind, PartyPoolStackKey key, ushort count) =
            Assert.Single(session.PoolIntents);
        Assert.Equal(PartyPoolOpKind.Add, kind);
        Assert.Equal(Resref, key.Resref);
        Assert.Equal(ItemIdentity.FlagKnown, key.Flags);
        Assert.Equal(3, count);

        bridge.Pool = Aggregate((Resref, Identified, 5));
        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Single(session.PoolIntents);
    }
    // ---- defect F: a blind window is not elapsed time -----------------------------------

    /// <summary>
    /// docs/research/shared-inventory-v2.md §15, watched happen 20 seconds into a live session.
    ///
    /// The client's reconcile queued ONE add of a frag grenade and the plugin applied it
    /// correctly ("added 'g_w_fraggren01' x1 to the party pool"). But the item had just been
    /// CREATED, and a fresh id cannot be resolved to a resref until the item-tag cache is
    /// rebuilt — which <c>EnsureItemKnown</c> refuses to do for 15 seconds after the last
    /// rebuild. Every scan in that window walked an id it could not resolve, refused to publish a
    /// partial aggregate (§14.2, correctly) and handed back the LAST GOOD sweep — which still
    /// said zero. The pump's own expected-movement entry expires after 10s, which is SHORTER, so
    /// it was "presumed resolved" while it had in fact landed, re-queued, and applied a second
    /// time: two grenades for one reconcile, then published back to the host as a local deposit.
    ///
    /// The rule this pins: a tick on which nothing could be OBSERVED is not elapsed time for any
    /// expiry clock. Same fix and same reasoning as §12.1's own SlideExpectedTimers on the store
    /// side, which had the identical shape for an unscannable shop.
    /// </summary>
    [Fact]
    public void AReconcileIsNeverPresumedLostWhileTheScanCannotSeeAnythingAtAll()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 1;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 50);

        // Tick 1: the host says 1, the client's engine holds 0 -> one reconcile add, dispatched
        // into the cheat channel.
        pump.Pump(Caps);
        Assert.Equal(1, DispatchPoolAppliesWithoutLanding(pump));

        // It LANDS in the engine — but the item is brand new, so every scan from here on walks an
        // id it cannot resolve and hands back the last good sweep, which still says zero.
        bridge.Pool[Medpac] = 1;
        bridge.PoolScanStale = true;

        int extra = 0;
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(20);
            pump.Pump(Caps);
            extra += DispatchPoolAppliesWithoutLanding(pump);
        }

        // Against the pre-fix pump the entry expires on the very first of those windows and the
        // identical add is queued again — the live round's second grenade.
        Assert.Equal(0, extra);
        Assert.Empty(session.PoolIntents);

        // The moment the item-tag cache does rebuild and the scan can vouch for what it walked,
        // the landing is seen, the entry is consumed, and NOTHING is reported as a local action.
        bridge.PoolScanStale = false;
        pump.Pump(Caps);
        pump.Pump(Caps);

        Assert.Empty(session.PoolIntents);
        Assert.Equal(0, DispatchPoolAppliesWithoutLanding(pump));
        Assert.Equal(1, bridge.Pool.GetValueOrDefault(Medpac));
    }

    /// <summary>A genuinely dropped apply must still self-heal — the slide above must not turn
    /// the expiry off, only pause it for as long as nothing can be seen.</summary>
    [Fact]
    public void AReconcileThatTrulyNeverLandsStillExpiresOnceTheScanWorksAgain()
    {
        ScriptedBridge bridge = new();
        StubClientSession session = new();
        session.PoolSnapshot[Medpac] = 1;

        PartyPoolPumpV2 pump = new(bridge, session, _ => { }, expectedTimeoutMs: 50);

        pump.Pump(Caps);
        Assert.Equal(1, DispatchPoolAppliesWithoutLanding(pump));

        // The apply is lost. The scan is fine throughout, so the clock runs.
        int requeued = 0;
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(30);
            pump.Pump(Caps);
            requeued += DispatchPoolAppliesWithoutLanding(pump);
        }

        Assert.True(requeued > 0, "a dropped apply must be re-queued once its expiry genuinely elapses");
    }
}
