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

    /// <summary>Read-only; no plugin needed. The apply half (creating/destroying items) goes
    /// through the staging-cheat channel (<see cref="ICheatBridge"/>), which does need one, but
    /// reading the pool's current contents is a pure external memory read.</summary>
    public bool SupportsPartyPool => true;

    public bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        long now = Environment.TickCount64;
        if (_partyPoolEverScanned && now < _nextPartyPoolScanTicks)
        {
            totals = _partyPoolLastScan;
            return true;
        }

        if (!EngineGlobals.TryGetServerInternal(_memory, out nint serverInternal))
        {
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
            totals = _partyPoolLastScan;
            return true;
        }

        if (!_memory.TryReadInt32(repo + RepositoryItemCount, out int count)
            || count is < 0 or > 4096
            || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids)
            || (count > 0 && ids == 0))
        {
            totals = _partyPoolLastScan;
            return _partyPoolEverScanned;
        }

        Dictionary<PartyPoolStackKey, int> scan = [];

        for (int i = 0; i < count; i++)
        {
            if (!_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId)
                || itemId is 0 or >= 0x7F000000)
            {
                continue;
            }

            // A miss (id not yet in the tag cache, or genuinely unreadable) simply cannot be
            // aggregated this pass — the same "leave the far side alone" fallback item-identity
            // reads use elsewhere. It reappears once the next scheduled cache rebuild sees it.
            if (!TryGetItemResref(itemId, out string resref) || resref.Length == 0)
            {
                continue;
            }

            if (!TryReadItemIdentity(itemId, out ItemIdentity identity) || !identity.IsKnown
                || identity.StackSize == 0)
            {
                continue;
            }

            PartyPoolStackKey key = PartyPoolStackKey.From(resref, identity);
            scan[key] = scan.GetValueOrDefault(key) + identity.StackSize;
        }

        _partyPoolLastScan = scan;
        _partyPoolEverScanned = true;
        totals = _partyPoolLastScan;
        return true;
    }
}
