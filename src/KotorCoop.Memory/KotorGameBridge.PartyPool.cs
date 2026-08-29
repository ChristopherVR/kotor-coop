// Shared party item-pool sync: reads CSWPartyTable+0x118's CItemRepository (the same object
// model a placeable's container loot uses — see KotorGameBridge.ContainerLoot.cs) and
// aggregates its live items by content key. See docs/research/party-pool-sync.md.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- party pool --------------------------------------------------------------
    //
    // CSWPartyTable+0x118 (pt_item_repo) -> CItemRepository*, VERIFIED against
    // tools/kotor1_0_3.db (offset 280 = 0x118) and docs/research/progression-sync.md §3. The
    // repository itself is the SAME class a placeable's container uses (see
    // KotorGameBridge.ContainerLoot.cs's PlaceableRepositoryField): items_list at +0x0C, a flat
    // array of item OBJECT IDS; item_count at +0x10. No vtable scan needed here, unlike a
    // container: the party table's own address is reached by a fixed pointer chain the
    // journal/progression channels already use (EngineGlobals.TryGetServerInternal +
    // PartyTableOffset), so the repo pointer is a single dereference away.

    // PartyTableOffset (0x1B770, CServerExoAppInternal + this = the embedded CSWPartyTable) is
    // already declared in KotorGameBridge.Journal.cs and shared here — same partial class.
    private const int PartyTableItemRepo = 0x118;

    /// <summary>Minimum gap between full party-pool sweeps — a sweep walks every live item the
    /// repository names and resolves its resref/identity, which is not free on a fast pump
    /// path. Matches ContainerLoot's own 1 Hz throttle.</summary>
    private const int PartyPoolScanIntervalMs = 1000;

    private long _nextPartyPoolScanTicks;

    /// <summary>The last successful sweep's result, returned again on a throttled tick so the
    /// caller always sees a stable snapshot rather than an empty one between sweeps.</summary>
    private Dictionary<PartyPoolStackKey, int> _partyPoolLastScan = [];

    private bool _partyPoolEverScanned;

    /// <summary>
    /// How recent a <see cref="TryGetItemCreationObservation"/> fact has to be, relative to the
    /// scan that first notices this item, for that item to be treated as ambient-script noise
    /// rather than sync-relevant party-pool content — see
    /// <see cref="IsAmbientPartyPoolNoise"/>'s own remarks and docs/research/party-pool-sync.md
    /// §20. KOTOR's own <c>k_*</c> heartbeat/plot scripts can deposit into this exact
    /// repository via <c>CreateItemOnObject(..., GetFirstPC())</c>, and BOTH instances run the
    /// same scripts — a naive publish of that deposit as a local player action would double it
    /// once the peer's own independently-created copy is added to the merged total. A stack a
    /// human genuinely drags into the pool via the GUI, or deposits through <c>pooladd</c>, was
    /// never freshly CREATED at that moment (it already existed in a backpack, possibly for a
    /// long time) — or, for <c>pooladd</c>'s own fresh spawn, IS marked cheat — so this window
    /// only ever catches the pattern this section exists for: created and pooled in the same
    /// breath, by something other than our own code.
    /// </summary>
    private const long PartyPoolAmbientSuppressionWindowMs = 1000;

    /// <summary>
    /// Per-item id, whether <see cref="IsAmbientPartyPoolNoise"/> decided this item is ambient
    /// noise — decided ONCE, the first time an item id is ever seen by a party-pool scan, and
    /// never re-evaluated afterward. This is deliberate, not a missed refinement: re-checking
    /// the time window on every later scan would mean an item excluded on tick 1 (correctly
    /// suppressed, freshly created) starts being COUNTED once <see cref="PartyPoolAmbientSuppressionWindowMs"/>
    /// elapses — reproducing, on a delay, the exact double-count bug this section exists to
    /// prevent, since a delayed "sudden appearance" reads as a genuine new local change to
    /// <c>Pumps.PartyPoolPump</c>'s own step-1 diff exactly the way the original unsuppressed
    /// deposit did. A decision, once made, is therefore permanent for the item's lifetime —
    /// consistent with this channel's own design (docs/research/party-pool-sync.md §2.1): an
    /// ambient item that is never counted is, from the sync layer's perspective, simply not
    /// part of the multiset at all, on either side, forever — which is correct, since both
    /// instances independently hold their own equivalent copy without any wire op ever being
    /// needed.
    ///
    /// Cleared on module change (engine object ids are only meaningful within the module that
    /// minted them — the same reasoning <see cref="ClearItemCreationObservationCache"/>'s own
    /// remarks give) and defensively capped past the repository's own hard count limit (4096) so
    /// a very long session cannot grow this without bound even across many modules whose
    /// per-module clear somehow did not fire.
    /// </summary>
    private readonly HashSet<uint> _partyPoolSuppressionDecided = [];

    private readonly HashSet<uint> _partyPoolAmbientSuppressed = [];

    private const int PartyPoolSuppressionCacheCapacity = 4096;

    /// <summary>How a suppressed item is kept OUT of the reported totals — by per-key BASELINE,
    /// never by skipping the item. See <see cref="PartyPoolAmbientBaseline"/>'s own remarks for
    /// the stack-merge artifact that made skipping actively wrong.</summary>
    private readonly PartyPoolAmbientBaseline _partyPoolAmbient = new();

    private int _partyPoolScanEpoch;

    /// <summary>See <see cref="IPartyPoolBridge.PartyPoolScanEpoch"/>. Bumped by
    /// <see cref="ClearPartyPoolSuppressionCache"/> — i.e. on a module change, the one moment the
    /// ambient baseline above is dropped wholesale and the totals this bridge reports can
    /// therefore step for a reason that is not engine movement.</summary>
    public int PartyPoolScanEpoch => _partyPoolScanEpoch;

    /// <summary>See <see cref="IPartyPoolBridge.LastPartyPoolScanWasStale"/>. Set by every
    /// <see cref="TryScanPartyPoolTotals"/> path that hands back <see cref="_partyPoolLastScan"/>
    /// without having read the repository this call — the throttle's own sub-second replay is
    /// deliberately NOT one of them, since that IS the current observation, only cached.</summary>
    public bool LastPartyPoolScanWasStale { get; private set; }

    /// <summary>
    /// True when <paramref name="itemId"/> should be excluded from a party-pool scan's
    /// aggregate — see <see cref="_partyPoolSuppressionDecided"/>'s own remarks for why this is
    /// a one-time, permanent decision per id rather than a live re-check.
    ///
    /// <paramref name="nowMs"/> is threaded through explicitly (rather than this method reading
    /// <see cref="Environment.TickCount64"/> itself) so <c>PartyPoolAmbientSuppressionTests</c>
    /// (KotorCoop.Tests, granted access via <c>InternalsVisibleTo</c>) can pin the window's exact
    /// boundary deterministically — the same reasoning
    /// <c>Pumps.PartyPoolLoopbackTests</c>' own <c>ThrottledFakeBridge</c> gives for a
    /// test-controlled clock over a real timer. <see cref="TryScanPartyPoolTotals"/> is the only
    /// real caller and always passes the true wall clock.
    /// </summary>
    internal bool IsAmbientPartyPoolNoise(uint itemId, long nowMs)
    {
        if (_partyPoolSuppressionDecided.Contains(itemId))
        {
            return _partyPoolAmbientSuppressed.Contains(itemId);
        }

        if (_partyPoolSuppressionDecided.Count >= PartyPoolSuppressionCacheCapacity)
        {
            // Past the failure, not a limit expected to bite in ordinary play (the repository
            // itself is capped at 4096 live items) — a full reset costs nothing but re-deciding
            // a handful of ids, never a wrong answer, since the decision rule itself is
            // unchanged.
            _partyPoolSuppressionDecided.Clear();
            _partyPoolAmbientSuppressed.Clear();
        }

        _partyPoolSuppressionDecided.Add(itemId);

        bool suppress = TryGetItemCreationObservation(itemId, out bool isCheat, out long observedAtMs)
            && !isCheat
            && nowMs - observedAtMs < PartyPoolAmbientSuppressionWindowMs;

        if (suppress)
        {
            _partyPoolAmbientSuppressed.Add(itemId);
        }

        return suppress;
    }

    /// <summary>Drops every party-pool suppression decision. Called on module change, alongside
    /// <see cref="ClearItemCreationObservationCache"/> — see this cache's own remarks on why a
    /// stale decision would be actively wrong (a reused id in a new module) rather than merely
    /// uninformative.</summary>
    private void ClearPartyPoolSuppressionCache()
    {
        _partyPoolSuppressionDecided.Clear();
        _partyPoolAmbientSuppressed.Clear();
        _partyPoolAmbient.Clear();

        // Dropping the baseline can make the very next scan's totals step UP for a key whose
        // ambient content was previously offset — movement no engine action caused. A
        // delta-based consumer must re-seed rather than read that as a deposit; the epoch is how
        // it finds out. See IPartyPoolBridge.PartyPoolScanEpoch.
        _partyPoolScanEpoch++;
    }

    /// <summary>Read-only; no plugin needed. The apply half (creating/destroying items) goes
    /// through the staging-cheat channel (<see cref="ICheatBridge"/>), which does need one, but
    /// reading the pool's current contents is a pure external memory read. The plugin-fed
    /// ambient-noise suppression above is a best-effort ENHANCEMENT on top of that read, not a
    /// requirement: with no plugin attached, <see cref="TryGetItemCreationObservation"/> never
    /// has an answer, <see cref="IsAmbientPartyPoolNoise"/> always returns false, and every
    /// item counts exactly as it did before this section existed.</summary>
    public bool SupportsPartyPool => true;

    public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        long now = Environment.TickCount64;
        if (_partyPoolEverScanned && now < _nextPartyPoolScanTicks)
        {
            // The throttle's replay is the CURRENT observation, at most PartyPoolScanIntervalMs
            // old, so it is not stale in the sense LastPartyPoolScanWasStale means: nothing was
            // unreadable, there was simply no reason to look again yet.
            totals = _partyPoolLastScan;
            return true;
        }

        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
            LastPartyPoolScanWasStale = true;
            totals = _partyPoolLastScan;
            return _partyPoolEverScanned;
        }

        _nextPartyPoolScanTicks = now + PartyPoolScanIntervalMs;

        nint partyTable = serverInternal + PartyTableOffset;

        if (!_memory.TryReadPointer(partyTable + PartyTableItemRepo, out nint repo) || repo == 0)
        {
            // Never populated yet (a fresh game where nobody has used the party stash) — an
            // empty pool, not a read failure. CSWPartyTable::GetPartyItems constructs the
            // repository lazily on first use; reading a null pointer here is the honest,
            // ordinary "nothing in it yet" case, not a resolve failure.
            _partyPoolLastScan = [];
            _partyPoolEverScanned = true;
            LastPartyPoolScanWasStale = false;
            totals = _partyPoolLastScan;
            return true;
        }

        if (!_memory.TryReadInt32(repo + RepositoryItemCount, out int count)
            || count is < 0 or > 4096
            || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids)
            || (count > 0 && ids == 0))
        {
            LastPartyPoolScanWasStale = true;
            totals = _partyPoolLastScan;
            return _partyPoolEverScanned;
        }

        Dictionary<PartyPoolStackKey, int> raw = [];
        int unresolved = 0;
        bool forcedRebuild = false;

        for (int i = 0; i < count; i++)
        {
            if (!_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId)
                || itemId is 0 or >= 0x7F000000)
            {
                unresolved++;
                continue;
            }

            // ⚠ A MISS IS NOT AN ITEM WORTH ZERO — it is a gap in what this pass could read, and
            // it is COUNTED rather than shrugged off. See ItemAggregateScanRule for the live
            // round (docs/research/shared-inventory-v2.md §14) where every id in the pool missed
            // at once and the resulting EMPTY aggregate was published as "the shared stash is
            // empty", emptying the other instance's pool.
            //
            // One forced sweep per scan is spent trying to close the gap first: TryGetItemResref
            // resolves an unknown id by rebuilding the item-tag cache, and EnsureItemKnown
            // refuses to rebuild for ItemTagRebuildMs (15s) after the last one — so without the
            // force, a pool whose bodies are newer than the last rebuild stays unreadable for a
            // quarter of a minute. Bounded to once because the rebuild is a full item sweep.
            if (!TryGetItemResref(itemId, out string resref) || resref.Length == 0)
            {
                if (!forcedRebuild)
                {
                    forcedRebuild = true;
                    TryGetItemResref(itemId, out resref, force: true);
                }

                if (resref.Length == 0)
                {
                    unresolved++;
                    continue;
                }
            }

            if (!TryReadItemIdentity(itemId, out ItemIdentity identity) || !identity.IsKnown
                || identity.StackSize == 0)
            {
                unresolved++;
                continue;
            }

            PartyPoolStackKey key = PartyPoolStackKey.From(resref, identity);
            raw[key] = raw.GetValueOrDefault(key) + identity.StackSize;

            // Ambient-script noise (an item created and pooled by a vanilla k_* script, not by
            // player action) is kept out of the sync-relevant aggregate — but by BASELINE, not by
            // skipping the item, so a later stack merge cannot surface it as movement. See
            // PartyPoolAmbientBaseline's own remarks, IsAmbientPartyPoolNoise's, and
            // docs/research/party-pool-sync.md §20.
            if (IsAmbientPartyPoolNoise(itemId, now))
            {
                _partyPoolAmbient.NoteSuppressed(itemId, key, identity.StackSize);
            }
        }

        // A partial walk is not a statement about the whole repository. Hand back the last GOOD
        // scan — the same "stale but honest" answer the pointer-read failures above already give
        // — and leave _partyPoolLastScan untouched so a later tick still has it. See
        // ItemAggregateScanRule for why a partial aggregate is a removal order in disguise.
        if (!ItemAggregateScanRule.IsObservation(count, unresolved))
        {
            LastPartyPoolScanNote = ItemAggregateScanRule.Explain("party pool", count, unresolved);
            LastPartyPoolScanWasStale = true;
            totals = _partyPoolLastScan;
            return _partyPoolEverScanned;
        }

        LastPartyPoolScanNote = string.Empty;
        LastPartyPoolScanWasStale = false;

        Dictionary<PartyPoolStackKey, int> scan = _partyPoolAmbient.Net(raw);

        _partyPoolLastScan = scan;
        _partyPoolEverScanned = true;
        totals = _partyPoolLastScan;
        return true;
    }

    /// <summary>Why the last party-pool scan refused to publish what it built, in words, or
    /// empty when it published normally. Same "a guard that fails safe still has to say what it
    /// saw" discipline as <c>Odyssey.LastPartyPoolNote</c> — this class of failure was inferred
    /// from log timestamps across five live rounds before it was finally watched happen.</summary>
    public string LastPartyPoolScanNote { get; private set; } = string.Empty;

    /// <summary>See the interface doc (<see cref="IPartyPoolBridge.InvalidatePartyPoolScanCache"/>).
    /// Resets the throttle deadline so the very next <see cref="TryScanPartyPoolTotals"/> call
    /// bypasses <see cref="_partyPoolLastScan"/> and reads the repository fresh, regardless of
    /// how recently the last real sweep ran. Does not touch <see cref="_partyPoolLastScan"/>
    /// itself or <see cref="_partyPoolEverScanned"/> — a caller that invalidates without ever
    /// following up with a scan still gets the last good snapshot, exactly like today, just no
    /// longer stale on the NEXT ask.</summary>
    public void InvalidatePartyPoolScanCache()
    {
        _nextPartyPoolScanTicks = 0;
    }
}
