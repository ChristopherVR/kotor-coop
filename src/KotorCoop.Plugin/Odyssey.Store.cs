// Odyssey.Store.cs - merchant store stock (CSWSStore+0x230's CItemRepository). See
// docs/research/store-sync.md for the full research this implements.
//
// Reuses RepositoryRemoveItemFn/RepositoryInsertItemFn/SetPossessorFn/CreatureItemRepo declared
// in Odyssey.PartyPool.cs UNQUALIFIED — this is one partial class, and GetGameObjectFn
// (Odyssey.Core.cs) is already reused the same way across a dozen other Odyssey.*.cs files. Not
// redeclared under new names here: a second `private const int CreatureItemRepo = ...` in this
// file would be a duplicate-member COMPILE ERROR, not a style choice.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- store identity: resolve by object id, no GUI required ----------------------------
    //
    // CServerExoApp::GetStoreByGameObjectID 0x004AE7B0 (a thin thunk, ecx=[ecx+4], tail-jumping
    // to CServerExoAppInternal::GetStoreByGameObjectID 0x004B1AB0) — VERIFIED full disassembly,
    // docs/research/store-sync.md §1.2: resolves a CSWSStore* from a bare object id through the
    // app's general per-instance object registry, with a runtime type-tag check that fails safe
    // (returns 0) on a mismatch. Requires no GUI, no OpenStore call — available any time the
    // module holding the store is loaded, exactly like GetGameObjectFn is for any other object.

    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetStoreByGameObjectIDFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x004AE7B0;

    /// <summary>CSWSStore + this = CItemRepository* (<c>item_list</c>). VERIFIED,
    /// tools/kotor1_0_3.db (<c>CSWSStore.item_list</c> @ decimal 560 = 0x230). The SAME
    /// <c>CItemRepository</c> class the party pool and every placeable's container use.</summary>
    private const int StoreItemRepo = 0x230;

    /// <summary>Bit 2 (mask 0x4) of an item's +0x288 flags dword — the Infinite bit a store
    /// template's per-entry <c>Infinite</c> GFF field becomes. VERIFIED, docs/research/
    /// store-sync.md §1.5: the engine's own <c>SellItem</c>/<c>AcquireItem</c> never change an
    /// Infinite entry's stock in either direction, so this feature never diffs one into an op —
    /// this constant exists purely as a defence-in-depth guard on the apply side (§2.4), so a
    /// wire op that somehow named an Infinite entry (a stale/buggy peer) cannot touch it.</summary>
    private const uint ItemBitInfinite = 1u << 2;

    /// <summary>Why the last store call refused, in words. Same "a guard that fails safe still
    /// has to say what it saw" discipline as <see cref="LastPartyPoolNote"/>.</summary>
    public static string LastStoreNote = string.Empty;

    /// <summary>Resolves a store's BASE pointer by object id (unlike <see cref="ResolveStoreRepo"/>,
    /// which returns the <c>+0x230</c> repository offset — <see cref="OpenStore"/> needs the base
    /// pointer itself, for the bonus markup/markdown fields and the object-id field). Returns 0
    /// (with <see cref="LastStoreNote"/> set) when there is no server, or the id does not resolve
    /// to a live store.</summary>
    internal static nint ResolveStoreBase(uint storeObjectId)
    {
        nint server = GetServer();
        if (server == 0)
        {
            LastStoreNote = "no server (not in a world)";
            return 0;
        }

        nint store = GetStoreByGameObjectIDFn(server, storeObjectId);
        if (store == 0)
        {
            LastStoreNote = $"0x{storeObjectId:X} did not resolve to a live store";
        }

        return store;
    }

    /// <summary>Resolves a store's repository pointer by object id. Returns 0 (with
    /// <see cref="LastStoreNote"/> set) when there is no server, or the id does not resolve to a
    /// live store.
    ///
    /// BUG FIX (2026-08-28, live-found): this used to return <c>store + StoreItemRepo</c> — the
    /// ADDRESS of the <c>item_list</c> pointer FIELD, not the <c>CItemRepository*</c> VALUE that
    /// field holds. `docs/research/store-sync.md` §1.1 documents `item_list` as a pointer type
    /// (<c>CItemRepository *</c>), the same shape `Odyssey.Items.cs`'s container resolve
    /// (`*(nint*)(placeable + PlaceableRepository)`) already dereferences correctly — the store
    /// path was the one place in this file that took the field's address instead of its value.
    /// Silent in every prior pass because nothing had exercised a store buy/sell live yet
    /// (`store-sync.md`'s own "Live pass" — items 2-4 never reached): every repo consumer
    /// (`InsertItemIntoStore`, `RemoveStoreStack`, `BuyFromStore`/`SellToStore`'s
    /// `FindFirstMatchingItem`) read `count`/`ids` from garbage bytes past the real
    /// `CSWSStore` struct tail, tripped the `count &lt;= 0 or &gt; 4096` guard, and failed safe
    /// with "no stock matching"/"store is empty" instead of finding anything — never a crash,
    /// which is exactly why it went unnoticed until a live `buy` actually walked the array.</summary>
    private static nint ResolveStoreRepo(uint storeObjectId)
    {
        nint store = ResolveStoreBase(storeObjectId);
        return store == 0 ? 0 : *(nint*)(store + StoreItemRepo);
    }

    /// <summary>
    /// Inserts <paramref name="count"/> units of a resref+identity stack into store
    /// <paramref name="storeObjectId"/>'s stock: spawns the item straight into the local
    /// player's own inventory (the same <c>CreateItemOnObject</c> VM routine
    /// <see cref="AddItemToPartyPool"/> already uses — there is still no VM path that targets an
    /// arbitrary repository directly), applies its identity, then unlinks it from the player's
    /// own backpack and links it into the RESOLVED STORE's repository. Mirrors
    /// <see cref="AddItemToPartyPool"/>'s three-step shape exactly, retargeted at a store's
    /// repository instead of the party table's — see docs/research/store-sync.md §2.4.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool InsertItemIntoStore(
        uint playerObjectId, uint storeObjectId, byte* resrefAscii, int resrefLength, int count,
        uint upgrades, uint charges, uint maxCharges, uint flags,
        out uint itemId, out ItemIdentityFields after)
    {
        itemId = 0;
        after = default;
        LastStoreNote = string.Empty;

        nint repo = ResolveStoreRepo(storeObjectId);
        if (repo == 0)
        {
            return false;
        }

        itemId = GiveItemToInventory(playerObjectId, resrefAscii, resrefLength, count, out int failure);
        if (itemId is 0 or ObjectInvalid)
        {
            LastStoreNote = $"did not spawn (failure {failure})";
            return false;
        }

        if (!TryReadItemIdentityFields(itemId, out ItemIdentityFields spawned) || spawned.LooksUnloaded)
        {
            LastStoreNote = $"0x{itemId:X} spawned EMPTY — not a blueprint here";
            return false;
        }

        if (!ApplyItemIdentity(
                itemId, upgrades, (uint)count, charges, maxCharges,
                flags | SharedBlock.ItemIdentityKnown, out after))
        {
            LastStoreNote = $"identity apply failed for 0x{itemId:X}: {LastItemResolveNote}";
            return false;
        }

        nint player = GetGameObjectFn(GetServer(), playerObjectId);
        nint playerRepo = player == 0 ? 0 : *(nint*)(player + CreatureItemRepo);
        nint item = ResolveItemBase(itemId);

        if (playerRepo == 0 || item == 0)
        {
            LastStoreNote = $"0x{itemId:X} spawned but could not resolve player backpack/item base";
            return false;
        }

        // Unlink from the player's own backpack, link into the store. Deliberately NOT calling
        // SetPossessor — same reasoning AddItemToPartyPool's own remarks give: the item's
        // possessor is left as the spawning player's id rather than fought back to
        // OBJECT_INVALID. NEEDS-LIVE, docs/research/store-sync.md.
        RepositoryRemoveItemFn(playerRepo, item);
        RepositoryInsertItemFn(repo, item, -1);

        return true;
    }

    /// <summary>
    /// Removes <paramref name="countToRemove"/> units of a resref+identity stack from store
    /// <paramref name="storeObjectId"/>'s stock, greedily over whichever live items currently
    /// match (by Tag and identity), reducing an oversized stack in place or unlinking a fully
    /// consumed one — mirrors <see cref="RemovePartyPoolStack"/>'s logic exactly, retargeted at
    /// a resolved store's repository. A candidate whose Infinite bit is set is skipped: a
    /// defence-in-depth guard that should never actually fire (docs/research/store-sync.md §2.3
    /// never diffs an Infinite entry into an op in the first place), matching the engine's own
    /// refusal to let one shrink.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool RemoveStoreStack(
        uint storeObjectId, byte* resrefAscii, int resrefLength, int countToRemove,
        uint charges, uint maxCharges, uint upgrades, uint flags, out int removed)
    {
        removed = 0;
        LastStoreNote = string.Empty;

        nint repo = ResolveStoreRepo(storeObjectId);
        if (repo == 0)
        {
            return false;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            LastStoreNote = $"store 0x{storeObjectId:X} is empty";
            return false;
        }

        // Snapshot the ids first: RepositoryRemoveItemFn mutates the array (shifts entries
        // down), so walking it live while removing from it would skip or re-visit entries.
        uint[] snapshot = new uint[Math.Min(count, 4096)];
        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = *(uint*)(ids + (nint)(i * 4));
        }

        foreach (uint id in snapshot)
        {
            if (removed >= countToRemove)
            {
                break;
            }

            nint item = ResolveItemBase(id);
            if (item == 0 || !TagEqualsIgnoreCase(item, resrefAscii, resrefLength))
            {
                continue;
            }

            ItemIdentityFields fields = ReadItemFields(item);
            if ((fields.BitFlags & ItemBitInfinite) != 0)
            {
                continue;
            }

            if (!StackIdentityMatches(fields, charges, maxCharges, upgrades, flags))
            {
                continue;
            }

            int stackSize = Math.Max(1, (int)fields.StackSize);
            int take = Math.Min(stackSize, countToRemove - removed);

            if (take >= stackSize)
            {
                RepositoryRemoveItemFn(repo, item);
                SetPossessorFn(item, ObjectInvalid, 1, 1, ObjectInvalid);
            }
            else
            {
                *(uint*)(item + ItemStackSize) = (uint)(stackSize - take);
            }

            removed += take;
        }

        if (removed == 0)
        {
            LastStoreNote = $"no matching stack found in store 0x{storeObjectId:X}";
            return false;
        }

        return true;
    }

    // ---- openstore: opens the store GUI directly, bypassing ExecuteCommandOpenStore --------
    //
    // 2026-08-28: root-caused the PopulateStoreItemListBox crash (docs/research/store-sync.md's
    // "Live pass" section) by finishing the disassembly the 2026-08-27 addendum below left
    // incomplete. THE ADDENDUM'S OWN CLAIM WAS WRONG: "ShowStoreGui takes ONLY `this`... no
    // stack argument anywhere in the function's body carries a store id." Fresh disasm this
    // pass shows `CGuiInGame::ShowStoreGui 0x0062E310` is
    // `__thiscall(CGuiInGame* this, uint storeObjectId, uint pcObjectId)`, ret 8 (two stack
    // dwords, confirmed by the trailing `ret 8` and by `[esp+0x30]`/`[esp+0x34]` resolving to
    // `[ESP_entry+4]`/`[ESP_entry+8]` -- the two caller-pushed stack args -- once the function's
    // own prologue displacement (0x2C bytes: SEH push x3 + `sub esp,0x1C` + `push esi`) is worked
    // out). After the three precondition gates, it allocates (or reuses)
    // `CGuiInGame+0x84`'s `CSWGuiStore` panel, then -- VERIFIED, this is the load-bearing part --
    // writes `storeObjectId` into the panel's `+0x2278` and `pcObjectId` into `+0x227C`, THEN
    // calls `AddPanel 0x40BC70(gm, panel, 3, 1)`.
    //
    // `AddPanel` synchronously invokes the panel's own `OnPanelAdded` vtable override
    // (docs/research/native-browser-panel.md documents this dispatch for the analogous
    // CSWGuiSaveLoad panel). `CSWGuiStore::OnPanelAdded 0x006C2170` -- VERIFIED, fresh disasm --
    // is 3 calls + a tail jump: `InitializeStoreType(this)`, `PopulateStore(this)`, base
    // `OnPanelAdded 0x40B870(this)`, then an unconditional `jmp ShowBuyGUI 0x6C1A50` (which
    // itself tail-jumps into `PopulateStoreItemListBox 0x6C1840` at its end). `InitializeStoreType
    // 0x6C1B50` reads the panel's `+0x2278` -- NOT `+0x227C` -- resolves it through
    // `GetStoreByGameObjectID 0x4AE7B0` (the exact function already verified, type-checked, fails
    // safe to 0), reads the resolved store's `+0x256` buy_sell_flag to pick
    // ShowBuyGUI/ShowSellGUI, both of which tail-jump into `PopulateStoreItemListBox`.
    // `PopulateStoreItemListBox 0x6C1840` ALSO reads `+0x2278` (not `+0x227C`), calls the SAME
    // `GetStoreByGameObjectID`, and immediately dereferences the result: `mov ebx, [eax+0x230]`
    // at `0x6C1872` -- this is the exact crashing instruction. `PopulateStore 0x6C0610`
    // (separately) reads `+0x227C` and resolves it through `GetCreatureByGameObjectID 0x4AE770`
    // -- corrected finding, replacing store-sync.md's prior mis-read of that field: `+0x227C`
    // holds the shopping CREATURE's (the PC's) object id, not the store's -- `PopulateStore`
    // uses it only to build a description string. `CSWGuiStore::Constructor 0x6C1C00` zeroes
    // `+0x2270`/`+0x2274` but deliberately leaves `+0x2278`/`+0x227C` untouched -- they are the
    // caller's job (ShowStoreGui's two stack args), same as `CSWGuiSaveLoad`'s ctor leaving
    // save-row population to its own caller-independent `PopulateGameList` self-call.
    //
    // Root cause of the crash, in one sentence: the old `ShowStoreGuiFn` delegate was declared
    // `this`-only, so the native call pushed ZERO stack args against a callee that reads two and
    // pops 8 bytes on return (`ret 8`) -- `CGuiInGame::ShowStoreGui` wrote whatever stale bytes
    // happened to be on the native stack into the fresh panel's `+0x2278`/`+0x227C`, then its own
    // internal `AddPanel` call synchronously ran `PopulateStoreItemListBox` against that garbage
    // store id, which `GetStoreByGameObjectID` failed safe (or resolved to garbage) on, producing
    // the null/bad-pointer deref at `0x6C1872` -- ALL of this happens inside the
    // `ShowStoreGuiFn(gui)` call itself, before the verb's own old follow-up code (which wrote the
    // store id to the WRONG field, `+0x227C` instead of `+0x2278`, and called `PopulateStore` a
    // third time) ever ran. The fix below passes the real store id and player id as ShowStoreGui's
    // own two arguments -- the exact values the engine's own `ExecuteCommandOpenStore`/click path
    // would supply -- so `AddPanel`'s internal `OnPanelAdded` populates the item list exactly as
    // retail does, and the verb's own redundant (and wrongly-targeted) manual field write + extra
    // PopulateStore call are removed.
    //
    // See docs/research/store-sync.md's 2026-08-28 root-cause section for the full disassembly.

    /// <summary>CGuiInGame + this = nonzero once the HUD is built. VERIFIED, already relied on
    /// unnamed at this literal offset in Odyssey.PauseReconciliation.cs/Odyssey.Travel.cs; named
    /// <c>InGameGuiInitialized</c> in MenuLoadController.MainMenuPhase.cs (a different class).
    /// ShowStoreGui's own first precondition check.</summary>
    private const int InGameGuiInitializedOffset = 0x108;

    /// <summary>CGuiInGame + this = nonzero while another modal screen (galaxy map, party
    /// select, ...) is up. VERIFIED, fresh disasm this pass of ShowStoreGui's own second
    /// precondition check.</summary>
    private const int InGameGuiBusyOffset = 0x30;

    /// <summary>CGuiInGame + this = CSWGuiStore* while a store panel is open, 0 otherwise.
    /// VERIFIED, docs/research/store-sync.md §1.3 (fresh disasm of HideStoreGui 0x0062E4A0).</summary>
    private const int InGameGuiStoreOffset = 0x84;

    /// <summary>CSWGuiStore + this = the currently-open store's OBJECT ID, read by
    /// <c>InitializeStoreType</c> and <c>PopulateStoreItemListBox</c> — the item-list
    /// population path. VERIFIED, fresh disasm this pass of both. NOT the field
    /// <c>PopulateStore</c> reads (that is <see cref="StorePanelPcIdOffset"/>) — this codebase's
    /// own prior pass conflated the two; see the section header above for the correction.
    /// Written by <c>ShowStoreGui</c> itself (its first stack arg), not by this verb — writing
    /// it manually after the fact is exactly the bug that caused the crash.</summary>
    private const int StorePanelObjectIdOffset = 0x2278;

    /// <summary>CSWGuiStore + this = the shopping CREATURE's (the PC's) object id, read by
    /// <c>PopulateStore</c> (0x006C0610) via <c>GetCreatureByGameObjectID 0x4AE770</c> to build
    /// a description string — NOT the store's id (this codebase's prior pass's mistaken
    /// reading). VERIFIED, fresh disasm this pass. Written by <c>ShowStoreGui</c> itself (its
    /// second stack arg).</summary>
    private const int StorePanelPcIdOffset = 0x227C;

    /// <summary>CSWSStore + this = the session-scoped bonus mark-up/mark-down ints
    /// ExecuteCommandOpenStore's VM handler writes (both clamped [-100,100] there — this call
    /// writes 0/0, matching a bare <c>OpenStore(oStore, oPC)</c> with no bonus args). VERIFIED,
    /// fresh disasm — see the section header above for the correction to store-sync.md §1.6's
    /// own inference.</summary>
    private const int StoreBonusMarkUp = 0x250;

    private const int StoreBonusMarkDown = 0x24C;

    /// <summary>CGuiInGame::ShowStoreGui(this, uint storeObjectId, uint pcObjectId) -&gt; void,
    /// ret 8. VERIFIED, fresh disasm this pass (0x0062E310) — corrects this codebase's own prior
    /// pass, which mis-read this as taking no stack args at all. Writes storeObjectId into the
    /// (possibly freshly constructed) panel's <see cref="StorePanelObjectIdOffset"/> and
    /// pcObjectId into <see cref="StorePanelPcIdOffset"/>, THEN calls <c>AddPanel</c>, which
    /// synchronously runs <c>CSWGuiStore::OnPanelAdded</c> — <c>InitializeStoreType</c> ->
    /// <c>ShowBuyGUI</c>/<c>ShowSellGUI</c> -> <c>PopulateStoreItemListBox</c>, then
    /// <c>PopulateStore</c> — so by the time this call returns the item list and description are
    /// already populated exactly as a real click would leave them. See the section header above
    /// for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, uint, void> ShowStoreGuiFn =
        (delegate* unmanaged[Thiscall]<nint, uint, uint, void>)0x0062E310;

    /// <summary>
    /// Opens the store GUI for the local player against <paramref name="store"/> (a base
    /// CSWSStore* — see <see cref="ResolveStoreBase"/> or <see cref="CreatureFinder.FindStoreByTag"/>)
    /// — the C++ entry point <c>ExecuteCommandOpenStore</c>'s own VM handler ultimately reaches
    /// (<c>ShowStoreGui</c>), called directly here with the same two arguments the engine's own
    /// open path supplies, instead of through the VM's own argument-popping machinery. See the
    /// section header above for the full reasoning, including why the previous (`this`-only)
    /// call was the root cause of the <c>PopulateStoreItemListBox</c> crash.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool OpenStore(nint store, uint playerObjectId, out string reason)
    {
        reason = string.Empty;

        if (store == 0)
        {
            reason = "no store resolved";
            return false;
        }

        nint gui = GetInGameGui();
        if (gui == 0)
        {
            reason = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            reason = "the HUD is not initialised yet (still loading)";
            return false;
        }

        if (*(int*)(gui + InGameGuiBusyOffset) != 0)
        {
            reason = "another modal screen is up (galaxy map / party select / ...)";
            return false;
        }

        if (*(int*)(gui + GuiConversationActive) != 0)
        {
            reason = "a conversation is active";
            return false;
        }

        *(int*)(store + StoreBonusMarkUp) = 0;
        *(int*)(store + StoreBonusMarkDown) = 0;

        uint storeObjectId = *(uint*)(store + 0x04); // CSWSObject + this = the object id.

        // The two arguments ARE the fix — see the section header above. ShowStoreGui writes
        // storeObjectId into the panel's own StorePanelObjectIdOffset and playerObjectId into
        // StorePanelPcIdOffset itself, then its own internal AddPanel call populates the item
        // list and description through the exact same OnPanelAdded path a real click uses. No
        // manual field write or extra PopulateStore call is needed (or correct) after this.
        ShowStoreGuiFn(gui, storeObjectId, playerObjectId);

        nint panel = *(nint*)(gui + InGameGuiStoreOffset);
        if (panel == 0)
        {
            reason = "ShowStoreGui did not produce a panel (allocation failed)";
            return false;
        }

        return true;
    }

    // ---- buy/sell: drive the real GUI button handlers directly (2026-08-28 addendum) -------
    //
    // Task: `buy`/`sell` dev verbs that move gold through the ENGINE'S OWN path, not a bare
    // store-repository poke. `CSWSStore::SellItem`/`AcquireItem` (§1.4/§1.5 above) do NOT touch
    // gold themselves (§1.7's own open question) — fresh disasm this pass of their ONE caller
    // each (`tools/xrefs.py 0x5c6f70` / `0x5c7610`, already cited in §1.4) settles it:
    // `CSWGuiStore::BuyItem 0x006C0BE0` / `CSWGuiStore::SellItem 0x006C0D80` (both named in
    // `tools/kotor1_0_3.db`, VERIFIED) are the GUI panel's own Buy/Sell button click handlers,
    // both `__thiscall(panel, item) -> void`, `ret 4` (ONE stack arg: the store-stock/inventory
    // item pointer to move).
    //
    // Full VERIFIED trace of BOTH (fresh disasm, this pass): each independently re-resolves
    // the ACTING PLAYER through `CServerExoApp::GetPlayerCreatureId 0x4AEA40` ->
    // `GetCreatureByGameObjectID 0x4AE770`, and the OPEN STORE through the panel's own
    // `+0x2278` (`StorePanelObjectIdOffset`, already declared above) ->
    // `GetStoreByGameObjectID 0x4AE7B0` — i.e. NEITHER the player NOR the store is a parameter;
    // the panel and the item are the only inputs. Both then, in order:
    //   1. `CSWGuiStore::GetItemBuyValue 0x6C0790` / `GetItemSellValue 0x6C07F0` (panel
    //      methods, one arg = the item) price the item;
    //   2. `CSWSCreature::SetGold 0x4EDD90` (one arg, ABSOLUTE not delta) writes the player's
    //      new total, computed against a RUNNING CACHED INT at panel `+0x2270` — see the
    //      correction below;
    //   3. the store's own native `SellItem 0x5C6F70` / `AcquireItem 0x5C7610` actually moves
    //      the item (buy: `push item; push player; ecx=store; call 0x5c6f70`; sell, the
    //      finite-stack tail: `push splitOrRemovedItem; ecx=store; call 0x5c7610`);
    //   4. `PopulateStoreItemListBox 0x6C1840` / `PopulateInventoryItemListBox 0x6C0850`
    //      refresh the two on-screen lists, and the cached gold int is written back to the
    //      currency label.
    // Gold changes through the SAME call a real click makes (step 2) — this closes §1.7/§2.5's
    // own "no gold movement found inside SellItem/AcquireItem themselves" note: it was never
    // supposed to be there, it is one level up, in the GUI handler.
    //
    // CORRECTION to §1.2/§1.7's own NEEDS-LIVE item ("CSWGuiStore+0x2270 = a cached resolved
    // pointer, exact identity unconfirmed"): it is not a pointer of any kind. `PopulateStore`
    // (0x006C0610, already partially quoted in §1.2) calls `CSWSCreature::GetGold 0x4EDD60` on
    // the resolved PC and stores the plain INT result at `+0x2270`; `BuyItem`/`SellItem` both
    // read-modify-write that same int (subtract the buy price / add the sell price) before
    // handing it to `SetGold`. It is the panel's own cached "gold currently displayed on the
    // currency label" int, settled by this pass's fresh disasm without needing a live process.
    //
    // One `BuyItem`/`SellItem` call moves exactly ONE unit of the given stack — the store's own
    // native decrement-by-1-or-unlink logic (§1.5) still runs underneath. Buying/selling
    // `count` units means calling it `count` times, RE-RESOLVING a matching item pointer fresh
    // before each call (a full unlink invalidates the previous one; `FindFirstMatchingItem`
    // below re-walks the live repository every time, exactly like `RemoveStoreStack` already
    // does for the sync channel).
    //
    // Both handlers need the TARGET store's own GUI already open — there is no store parameter
    // to hand them, only whatever `panel+0x2278` already names — so both verbs below refuse
    // (rather than silently acting on the wrong store) unless the currently open panel's own
    // store id matches the one asked for. This is the task's own "refuse if the store GUI isn't
    // open" rule, made precise: it is not merely "a store" that must be open, it is THIS one.

    /// <summary>CGuiInGame::ShowStoreGui already declares <see cref="InGameGuiStoreOffset"/>
    /// and <see cref="StorePanelObjectIdOffset"/>; resolves the currently open store panel, or
    /// 0 with <see cref="LastStoreNote"/> set.</summary>
    private static nint ResolveOpenStorePanel()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            LastStoreNote = "no in-game GUI resolved (not in a loaded module)";
            return 0;
        }

        nint panel = *(nint*)(gui + InGameGuiStoreOffset);
        if (panel == 0)
        {
            LastStoreNote = "no store GUI is open";
        }

        return panel;
    }

    /// <summary>CSWGuiStore::BuyItem(panel, item) 0x006C0BE0, ret 4. VERIFIED, fresh disasm
    /// this pass — see the section header above for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> GuiBuyItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006C0BE0;

    /// <summary>CSWGuiStore::SellItem(panel, item) 0x006C0D80, ret 4. VERIFIED, fresh disasm
    /// this pass — see the section header above for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> GuiSellItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006C0D80;

    /// <summary>Walks a <c>CItemRepository</c> for the first live item whose Tag matches
    /// (the same resref proxy <see cref="TagEqualsIgnoreCase"/> already relies on), or 0.
    /// Re-walked fresh by every caller rather than cached: a prior buy/sell in the same loop
    /// can unlink or resize the very entry a stale index would have pointed at.</summary>
    private static nint FindFirstMatchingItem(nint repo, byte* resrefAscii, int resrefLength)
    {
        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return 0;
        }

        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(ids + (nint)(i * 4));
            nint item = ResolveItemBase(id);
            if (item != 0 && TagEqualsIgnoreCase(item, resrefAscii, resrefLength))
            {
                return item;
            }
        }

        return 0;
    }

    /// <summary>
    /// Buys up to <paramref name="count"/> units of the first store-stock item whose tag
    /// matches <paramref name="resrefAscii"/> from store <paramref name="storeObjectId"/>,
    /// through the REAL <c>CSWGuiStore::BuyItem</c> click handler — gold and item both move
    /// through the engine's own path (see the section header above). Requires the store's own
    /// GUI to already be open and matching <paramref name="storeObjectId"/>. Infinite-stock
    /// items are valid targets here (unlike the sync channel's own scan) — a manual buy against
    /// one is exactly what a real click would do (§1.5: the engine spawns a copy, the store's
    /// own stock never moves).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool BuyFromStore(
        uint storeObjectId, byte* resrefAscii, int resrefLength, int count, out int bought)
    {
        bought = 0;
        LastStoreNote = string.Empty;

        nint panel = ResolveOpenStorePanel();
        if (panel == 0)
        {
            return false;
        }

        uint openStoreId = *(uint*)(panel + StorePanelObjectIdOffset);
        if (openStoreId != storeObjectId)
        {
            LastStoreNote =
                $"the open store is 0x{openStoreId:X}, not the requested 0x{storeObjectId:X} - open it first";
            return false;
        }

        nint repo = ResolveStoreRepo(storeObjectId);
        if (repo == 0)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            nint item = FindFirstMatchingItem(repo, resrefAscii, resrefLength);
            if (item == 0)
            {
                break;
            }

            GuiBuyItemFn(panel, item);
            bought++;
        }

        if (bought == 0)
        {
            LastStoreNote = $"no stock matching that resref in store 0x{storeObjectId:X}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Sells up to <paramref name="count"/> units of the first item in
    /// <paramref name="player"/>'s own inventory whose tag matches
    /// <paramref name="resrefAscii"/> to store <paramref name="storeObjectId"/>, through the
    /// REAL <c>CSWGuiStore::SellItem</c> click handler. Requires the store's own GUI to already
    /// be open and matching <paramref name="storeObjectId"/>.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool SellToStore(
        nint player, uint storeObjectId, byte* resrefAscii, int resrefLength, int count, out int sold)
    {
        sold = 0;
        LastStoreNote = string.Empty;

        nint panel = ResolveOpenStorePanel();
        if (panel == 0)
        {
            return false;
        }

        uint openStoreId = *(uint*)(panel + StorePanelObjectIdOffset);
        if (openStoreId != storeObjectId)
        {
            LastStoreNote =
                $"the open store is 0x{openStoreId:X}, not the requested 0x{storeObjectId:X} - open it first";
            return false;
        }

        if (player == 0)
        {
            LastStoreNote = "no player resolved";
            return false;
        }

        nint repo = *(nint*)(player + CreatureItemRepo);
        if (repo == 0)
        {
            LastStoreNote = "player has no item repository";
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            nint item = FindFirstMatchingItem(repo, resrefAscii, resrefLength);
            if (item == 0)
            {
                break;
            }

            GuiSellItemFn(panel, item);
            sold++;
        }

        if (sold == 0)
        {
            LastStoreNote = "no matching item in inventory";
            return false;
        }

        return true;
    }
}
