// Merchant/store stock: reading whichever CSWSStore a local player currently has open. See
// docs/research/store-sync.md §1.3/§2.3.
//
// Unlike the party pool (a single, always-reachable pointer chain — KotorGameBridge.PartyPool.cs)
// no per-area "every store" enumeration was found this pass, so this reads only the store a
// local player's GUI is currently showing, via CGuiInGame+0x84 (the "store" member, VERIFIED
// against tools/kotor1_0_3.db) -> CSWGuiStore+0x2278 (the open store's object id — corrected
// 2026-08-28 from the original, mistaken +0x227C; see StoreGuiObjectId's own doc comment below).
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- the chain to "which store, if any, is open" ------------------------------------
    //
    // appMgr = *(u32*)EngineGlobals.AppManagerPtr; client = *(u32*)(appMgr + 0x04);
    // clientInternal = *(u32*)(client + 0x04); gui = *(u32*)(clientInternal + InGameGuiOffset).
    // EngineGlobals.TryGetClientInternal already performs the first three steps — the SAME
    // chain src/KotorCoop.Plugin/EngineHandles.cs's GetInGameGui walks in-process, reused here
    // for the external agent.

    /// <summary>CClientExoAppInternal + this = CGuiInGame*. Matches
    /// <c>EngineHandles.ClientInternalGuiOffset</c> — VERIFIED elsewhere in this codebase
    /// (docs/research/ingame-gui-transition.md).</summary>
    private const int InGameGuiOffset = 0x40;

    /// <summary>CGuiInGame + this = CSWGuiStore*, non-null exactly while the store panel is
    /// open; cleared by <c>CGuiInGame::HideStoreGui</c>. VERIFIED, tools/kotor1_0_3.db
    /// (<c>CGuiInGame.store</c> @ decimal 132) cross-checked against fresh disassembly of
    /// <c>HideStoreGui 0x0062E4A0</c> — docs/research/store-sync.md §1.3.</summary>
    private const int InGameGuiStorePanel = 0x84;

    /// <summary>CSWGuiStore + this = the currently-open store's object id.
    ///
    /// BUG FIX (2026-08-28, live-found): this was <c>0x227C</c> — the field §1.2's ORIGINAL
    /// disassembly of <c>CSWGuiStore::PopulateStore</c> attributed to the store id. docs/
    /// research/store-sync.md's own later "Root cause and fix" pass (the `ShowStoreGui`
    /// argument-count fix) corrected this for the PLUGIN side
    /// (<c>Odyssey.Store.cs</c>'s <c>StorePanelObjectIdOffset</c>): <c>ShowStoreGui</c> writes
    /// its FIRST argument (the store id) to panel <c>+0x2278</c>, and its SECOND argument (the
    /// shopping PC's id) to <c>+0x227C</c> — the two were swapped from what the original
    /// disassembly guessed. This agent-side mirror was never updated to match, so it kept
    /// reading the PC's own object id here instead of the store's — <c>TryReadStoreRepo</c>
    /// then searched for a `CSWSStore` whose id equalled the PLAYER's id, always failed to
    /// match, and <c>TryScanOpenStore</c> silently reported "no store open" even with a real
    /// store GUI on screen. This is exactly the cross-process mirror-sync hazard
    /// docs/CONTRIBUTING.md's "Adding a feature end-to-end" step 5 warns about ("the two are
    /// transcribed by hand and a mismatch is a silent 'plugin not found'") — live-confirmed as
    /// the actual root cause of `store_sync` never detecting a single local buy/sell across a
    /// live two-instance session (no `store: local ... detected` log line, ever, on either
    /// side, even after the other two independently-found repo-pointer dereference bugs in
    /// this same file and in <c>Odyssey.Store.cs</c> were already fixed).</summary>
    private const int StoreGuiObjectId = 0x2278;

    /// <summary>CSWSStore + this = CItemRepository* — the SAME class the party pool and every
    /// placeable's container use (<c>items_list</c>/<c>item_count</c> at the repository's own
    /// <c>+0xC</c>/<c>+0x10</c>, <see cref="RepositoryItemArray"/>/<see cref="RepositoryItemCount"/>).
    /// VERIFIED, tools/kotor1_0_3.db (<c>CSWSStore.item_list</c> @ decimal 560).</summary>
    private const int StoreItemRepo = 0x230;

    /// <summary>Minimum gap between full open-store sweeps — matches ContainerLoot/PartyPool's
    /// own 1 Hz throttle.</summary>
    private const int StoreScanIntervalMs = 1000;

    private long _nextStoreScanTicks;
    private uint _storeLastScanId;
    private Dictionary<StoreStackKey, int> _storeLastScan = [];
    private bool _storeEverScanned;

    /// <summary>Read-only; no plugin needed for the scan itself. The apply half (inserting/
    /// removing store stock) goes through the staging-cheat channel (<see cref="ICheatBridge"/>).</summary>
    public bool SupportsStore => true;

    public bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        storeObjectId = 0;
        totals = new Dictionary<StoreStackKey, int>();

        long now = Environment.TickCount64;
        if (_storeEverScanned && now < _nextStoreScanTicks)
        {
            if (_storeLastScanId == 0)
            {
                return false;
            }

            storeObjectId = _storeLastScanId;
            totals = _storeLastScan;
            return true;
        }

        _nextStoreScanTicks = now + StoreScanIntervalMs;

        if (!EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
            || !_memory.TryReadPointer(clientInternal + InGameGuiOffset, out nint gui) || gui == 0
            || !_memory.TryReadPointer(gui + InGameGuiStorePanel, out nint storePanel) || storePanel == 0)
        {
            // No store GUI open — the honest, ordinary case, not a read failure.
            _storeLastScanId = 0;
            _storeLastScan = [];
            _storeEverScanned = true;
            return false;
        }

        if (!_memory.TryReadUInt32(storePanel + StoreGuiObjectId, out uint id) || id is 0 or >= 0x7F000000)
        {
            _storeLastScanId = 0;
            _storeLastScan = [];
            _storeEverScanned = true;
            return false;
        }

        // The store's own CGameObject is resolved the SAME way KotorGameBridge already
        // resolves any other object id externally (its own scan/cache path per feature); a
        // store has none of those already, so this reads its item_list straight through the
        // engine's general object array the way ContainerLoot resolves a placeable — see
        // TryReadStoreRepo below.
        if (!TryReadStoreRepo(id, out nint repo))
        {
            _storeLastScanId = 0;
            _storeLastScan = [];
            _storeEverScanned = true;
            return false;
        }

        if (!_memory.TryReadInt32(repo + RepositoryItemCount, out int count)
            || count is < 0 or > 4096
            || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids)
            || (count > 0 && ids == 0))
        {
            storeObjectId = _storeLastScanId;
            totals = _storeLastScan;
            return _storeEverScanned && _storeLastScanId != 0;
        }

        Dictionary<StoreStackKey, int> scan = [];

        for (int i = 0; i < count; i++)
        {
            if (!_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId)
                || itemId is 0 or >= 0x7F000000)
            {
                continue;
            }

            if (!TryGetItemResref(itemId, out string resref) || resref.Length == 0)
            {
                continue;
            }

            if (!TryReadItemIdentity(itemId, out ItemIdentity identity) || !identity.IsKnown
                || identity.StackSize == 0)
            {
                continue;
            }

            // Infinite entries never change in either direction (docs/research/store-sync.md
            // §1.5, VERIFIED) — excluded from the diffable snapshot entirely, not branched on
            // later, because there is provably never a delta to replicate for one.
            if (_itemAddresses.TryGetValue(itemId, out nint itemAddress)
                && _memory.TryReadUInt32(itemAddress + ItemIdentityLayout.BitFlags, out uint rawFlags)
                && (rawFlags & ItemIdentityLayout.BitInfinite) != 0)
            {
                continue;
            }

            StoreStackKey key = StoreStackKey.From(id, resref, identity);
            scan[key] = scan.GetValueOrDefault(key) + identity.StackSize;
        }

        _storeLastScanId = id;
        _storeLastScan = scan;
        _storeEverScanned = true;
        storeObjectId = id;
        totals = scan;
        return true;
    }

    /// <summary>
    /// Resolves a store's <c>CItemRepository*</c> from its object id, purely by
    /// <c>ReadProcessMemory</c> — the external agent has no engine call, unlike
    /// <c>Odyssey.Store.cs</c>'s write-side <c>GetStoreByGameObjectIDFn</c>.
    ///
    /// BUG FIX (2026-08-28, live-found): this used to set <c>repo = address + StoreItemRepo</c> —
    /// the ADDRESS of the <c>item_list</c> pointer FIELD, not the <c>CItemRepository*</c> VALUE
    /// that field holds (<c>item_list</c> is documented as a pointer type, docs/research/
    /// store-sync.md §1.1). The exact same mistake was independently made in the plugin's own
    /// <c>Odyssey.Store.cs</c>'s <c>ResolveStoreRepo</c> — see that fix's own comment for the
    /// full story.
    ///
    /// ROOT CAUSE (2026-08-29, docs/research/store-sync.md's own "discrepancy" write-up): a
    /// SECOND, independent defect the bug above was masking. This used to find the store by a
    /// raw <c>PatternScanner</c> sweep of committed memory for the <c>CSWSStore</c> vtable word,
    /// trusting the FIRST address whose id field happened to match — exactly the "ghost object"
    /// hazard this codebase already found and fixed for creatures
    /// (docs/research/enemy-presence-sync.md, <see cref="LiveObjectGate"/>): <c>operator
    /// delete</c>/a freed-but-not-yet-reused heap block leaves the vtable word and the object id
    /// exactly where they were, so a scan cannot tell a live <c>CSWSStore</c> from the intact
    /// bytes of one that no longer belongs to the current world (a re-entered module, a stale
    /// allocation left over from an earlier load). A ghost hit reads a PLAUSIBLE, VALID-LOOKING
    /// <c>item_list</c> pointer — its own last-known-good snapshot — so <see cref="TryScanOpenStore"/>
    /// never sees an error, never returns false, and just silently reports the SAME stale stock
    /// forever: a live client buy against the REAL store (resolved by the engine's own
    /// <c>GetStoreByGameObjectID</c>, which this raw scan never asked) genuinely moves the item,
    /// but this bridge's own scan of a DIFFERENT, ghost copy of the same object id never reflects
    /// it — the exact shape of the live-observed symptom ("35+ seconds with zero detection... the
    /// host's own re-buy of the 'sold' item succeeded", store-sync.md's rig-round write-up),
    /// indistinguishable from a hung pump because nothing ever fails loudly.
    ///
    /// Fixed the SAME way the creature census was: resolve the id through the engine's OWN
    /// per-app object registry (<see cref="EngineGlobals.TryResolveServerObject"/>, a
    /// transcription of <c>CGameObjectArray::GetGameObject 0x004D8230</c> — the SAME hash-table
    /// walk <c>CServerExoAppInternal::GetStoreByGameObjectID 0x004B1AB0</c> itself performs
    /// internally, docs/research/store-sync.md §1.2's own disasm: <c>mov ecx,[ecx+0x1005c] ...
    /// call 0x4d8230</c>) rather than a raw byte-pattern guess. This guarantees the read side
    /// resolves the EXACT SAME live <c>CSWSStore*</c> the write side
    /// (<c>Odyssey.Store.cs</c>'s <c>GetStoreByGameObjectIDFn</c>) and the retail engine's own
    /// <c>BuyItem</c>/<c>SellItem</c> handlers do — there is no longer a second "repo instance" a
    /// mirrored insert could land in that this scan does not also see, and a store this instance
    /// has never actually resolved (module unloaded, id genuinely gone) fails safe (empty scan,
    /// no crash) exactly like <c>GetStoreByGameObjectID</c>'s own type-checked accessor. The
    /// vtable re-check below reproduces that same type-tag discipline (a store id that resolves
    /// to some OTHER live object type is refused, not mis-read as a repository pointer).
    ///
    /// <see cref="EngineGlobals.TryLookupObject"/> (the bucket-walk this ultimately calls) is
    /// already unit-tested in isolation (<c>LiveObjectGateTests</c>) against a synthetic address
    /// space — this reuses that already-proven primitive rather than re-implementing a scan, the
    /// same "one true lookup" discipline every other object-id-keyed read in this bridge already
    /// follows (<c>KotorGameBridge.Attach.cs</c>'s census gate). NEEDS-LIVE for the full pointer
    /// chain (<see cref="EngineGlobals.AppManagerPtr"/> onward), the same as every other method in
    /// this file — nothing about the ARITHMETIC is new.
    /// </summary>
    private bool TryReadStoreRepo(uint storeObjectId, out nint repo)
    {
        repo = 0;

        if (!EngineGlobals.TryResolveServerObject(_memory, storeObjectId, out nint address) || address == 0)
        {
            return false;
        }

        // Type-tag discipline, matching CServerExoAppInternal::GetStoreByGameObjectID's own
        // `cmp al,[type_constant]` check (docs/research/store-sync.md §1.2): the object array
        // holds every game object, not just stores, so a resolved id must still be confirmed as
        // a genuine CSWSStore — read directly off the vtable word at the object's own +0x00,
        // rather than trusted from the byte pattern that used to find it in the first place.
        if (!_memory.TryReadUInt32(address, out uint vtable) || vtable != StoreVtable)
        {
            return false;
        }

        return _memory.TryReadPointer(address + StoreItemRepo, out repo) && repo != 0;
    }

    /// <summary>CSWSStore vtable. tools/kotor1_0_3.db: <c>CSWSStore</c> vtable = <c>0x0074BDB8</c>.</summary>
    private const uint StoreVtable = 0x0074BDB8;
}
