// Odyssey.Store.cs - merchant store stock (CSWSStore+0x230's CItemRepository). See
// docs/research/store-sync.md for the full research this implements.
//
// Reuses RepositoryRemoveItemFn/RepositoryInsertItemFn/SetPossessorFn/CreatureItemRepo declared
// in Odyssey.PartyPool.cs UNQUALIFIED — this is one partial class, and GetGameObjectFn
// (Odyssey.Core.cs) is already reused the same way across a dozen other Odyssey.*.cs files. Not
// redeclared under new names here: a second `private const int CreatureItemRepo = ...` in this
// file would be a duplicate-member COMPILE ERROR, not a style choice.

using System.Text;

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

        // Identified/NonEquippable come from the blueprint the engine has just loaded, NOT from
        // the wire key (which can no longer carry them) — see IdentityFlagsForCreatedItem in
        // Odyssey.PartyPool.cs for why forcing them here would manufacture a split stack key.
        if (!ApplyItemIdentity(
                itemId, upgrades, (uint)count, charges, maxCharges,
                IdentityFlagsForCreatedItem(flags, spawned), out after))
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

    /// <summary>
    /// CGuiInGame + this = nonzero while the FULL-SCREEN in-game GUI is up — the
    /// equipment/inventory/character/abilities/journal/map/options screen the menu key raises,
    /// not "some panel exists". VERIFIED as a precondition read by fresh disasm of ShowStoreGui
    /// (<c>0x0062E339</c>) and <c>ShowGalaxyMapGui</c> (<c>0x0062D069</c>), which gate on it
    /// identically.
    ///
    /// ⚠ NAMED, not guessed, as of docs/research/shared-inventory-v2.md §14: the symbol DB has no
    /// member at CGuiInGame+0x30, and a byte scan of the GUI code range for disp8 stores to
    /// <c>[reg+0x30]</c> (which <c>tools/fieldwrites.py</c> deliberately refuses — the offset fits
    /// a disp8, so its own disp32 scan would miss it) finds exactly two writers in the whole
    /// class, a matched pair: <c>CSWGuiInGame::ShowSWInGameGui</c> sets it to 1 at
    /// <c>0x0062CAFB</c> and <c>CSWGuiInGame::HideSWInGameGui</c> clears it at <c>0x0062CC0D</c>.
    /// This matters because it makes the refusal ACTIONABLE: it is not an unknown modal, it is
    /// that one screen, and the same <c>HideSWInGameGui</c> 0x0062CBA0 this plugin already calls
    /// from <c>MenuLoadController</c> is what takes it back down. Four consecutive live store
    /// rounds lost the client half to this refusal (§8.2, §10.2, §13.1).
    /// </summary>
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
    /// CSWGuiInGame::HideSWInGameGui(int) 0x0062CBA0 — the engine's own teardown for the
    /// full-screen in-game GUI, and the ONLY thing that clears
    /// <see cref="InGameGuiBusyOffset"/> (it does so at 0x0062CC0D; its counterpart
    /// <c>ShowSWInGameGui</c> sets it at 0x0062CAFB, and those two are the whole set of writers
    /// in the class).
    ///
    /// Not a new primitive: <c>MenuLoadController</c>'s main-menu and mid-session load phases
    /// already call this exact address with this exact argument (<c>0</c>), live-proven across
    /// every join round this project has run. It is redeclared here because
    /// <see cref="CloseFullScreenGui"/> needs it beside the offsets that explain WHY, and
    /// <c>MenuLoadController</c>'s copy is private to that class.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> HideSWInGameGuiFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x0062CBA0;

    /// <summary>CGuiInGame + this = <c>CSWGuiManager*</c> (symbol DB: <c>CGuiInGame.manager</c>,
    /// byte offset 56). The same object <c>CClientExoApp::GetSWGuiManager</c> 0x005EDA40
    /// returns, which is how <see cref="HideSWInGameGuiFn"/> reaches it.</summary>
    private const int InGameGuiManagerOffset = 0x38;

    /// <summary>
    /// CSWGuiManager + this = the ENTRY COUNT of <c>modal_stack</c> (symbol DB:
    /// <c>CSWGuiManager.modal_stack</c>, a <c>GuiManagerModalStack</c> at byte offset 148 /
    /// <c>0x94</c>; this is its second dword, the count beside the array pointer).
    ///
    /// This is the gate <c>HideSWInGameGui</c> bails on at <c>0x0062CBC9</c>-<c>0x0062CBD1</c>
    /// (<c>mov eax,[edi+0x98]; test eax,eax; jne bail</c>, <c>edi</c> = the GUI manager), so it
    /// is the one number that says whether the full-screen GUI can be taken down at all.
    ///
    /// MEASURED LIVE, 2026-08-29 (docs/research/shared-inventory-v2.md §14), and it is the
    /// four-round store blocker in one line: on the HOST <c>CGuiInGame+0x30</c> = 0 and this
    /// count = 0, so <c>openstore</c> succeeds first try; on the CLIENT, in END_M01AA, with no
    /// warp and no conversation, <c>CGuiInGame+0x30</c> = 1 and this count = <b>1</b> — a modal
    /// panel really is pushed, and the engine's own teardown therefore refuses. The old refusal
    /// text guessed "galaxy map / party select"; both of those pointers are non-null on BOTH
    /// instances (they are allocated panels, not a "showing" flag), which is why four rounds of
    /// guessing from them got nowhere. The modal-stack count is the signal.
    /// </summary>
    private const int GuiManagerModalStackCountOffset = 0x98;

    /// <summary>The number of panels on the GUI manager's modal stack, or -1 when the manager
    /// could not be resolved. See <see cref="GuiManagerModalStackCountOffset"/>.</summary>
    private static int ReadModalStackCount(nint gui)
    {
        nint manager = *(nint*)(gui + InGameGuiManagerOffset);
        return manager == 0 ? -1 : *(int*)(manager + GuiManagerModalStackCountOffset);
    }

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

        // A guard that fails safe still has to say what it saw. Naming the screen and printing
        // the raw field turns "another modal screen is up" — the message four live store rounds
        // could do nothing with — into a refusal a player or a rig can act on: close the
        // equipment/map/journal screen and try again. See InGameGuiBusyOffset's own remarks for
        // the two engine writers that settle what the field is.
        int fullScreenGui = *(int*)(gui + InGameGuiBusyOffset);
        if (fullScreenGui != 0)
        {
            reason = "the full-screen in-game GUI is up (equipment / inventory / character / "
                + $"map / journal / options) — close it first, e.g. `closegui` (CGuiInGame+0x30 = "
                + $"{fullScreenGui}, GUI-manager modal stack = {ReadModalStackCount(gui)})";
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

    /// <summary>
    /// Takes the full-screen in-game GUI back down through the engine's own
    /// <c>HideSWInGameGui</c> — the `closegui` live-test primitive, and the counterpart to the
    /// refusal <see cref="OpenStore"/> raises on <see cref="InGameGuiBusyOffset"/>.
    ///
    /// This exists because that refusal has cost FOUR consecutive live store rounds
    /// (docs/research/shared-inventory-v2.md §8.2, §10.2, §13.1): the client's own
    /// <c>openstore</c> refused six times in a row with no conversation active and nothing
    /// visibly modal, and the documented <c>skipline</c>/<c>selectreply 0</c>/<c>partyselect
    /// ok</c> retry loop cannot clear it, because none of those three touches
    /// <c>CGuiInGame+0x30</c>. The field's two writers (see
    /// <see cref="InGameGuiBusyOffset"/>) say the only thing that clears it is this call.
    ///
    /// Deliberately a NO-OP when the screen is not up: <c>HideSWInGameGui</c> tears down
    /// whatever panel index <c>CGuiInGame+0x2C</c> currently names, which is not something to
    /// run speculatively. Reports what it saw either way, before and after, rather than
    /// claiming success — the engine has its own early bail-outs (an uninitialised HUD, a
    /// modal message box at the GUI manager's <c>+0x98</c>) that this cannot see past.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool CloseFullScreenGui(out string detail)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            detail = "the HUD is not initialised yet (still loading)";
            return false;
        }

        int modalsBefore = ReadModalStackCount(gui);

        int before = *(int*)(gui + InGameGuiBusyOffset);
        if (before == 0)
        {
            detail = "the full-screen in-game GUI is already closed (CGuiInGame+0x30 = 0, "
                + $"GUI-manager modal stack = {modalsBefore})";
            return true;
        }

        HideSWInGameGuiFn(gui, 0);

        int after = *(int*)(gui + InGameGuiBusyOffset);
        detail = $"HideSWInGameGui called — CGuiInGame+0x30 {before} -> {after}, "
            + $"GUI-manager modal stack {modalsBefore} -> {ReadModalStackCount(gui)}"
            + (after == 0
                ? string.Empty
                : " (the engine refused its own teardown — it bails at 0x0062CBD1 while the "
                    + "GUI manager's modal stack is not empty; that stack has to be popped first)");
        return after == 0;
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
    //
    // UPDATE (2026-08-29): `SellToStore` no longer calls `GuiSellItemFn` (this trace's own
    // "SellItem" above) — a live pass found it silently no-ops, root-caused to one of its own
    // internal re-resolutions failing. The trace above is kept as-is because it is still an
    // accurate, disassembly-verified description of what the real click handler does, and
    // `BuyFromStore`/`GuiBuyItemFn` (confirmed working) is UNCHANGED by this. See the
    // 2026-08-29 section further below for the sell-specific fix.

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
    /// this pass — see the section header above for the full trace. NOT used by
    /// <see cref="SellToStore"/> any more (see the 2026-08-29 root-cause section, below the
    /// next one) — kept declared because it is still the documented, disassembly-verified
    /// shape of the real click handler, referenced there.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> GuiSellItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006C0D80;

    // ---- root cause + fix (2026-08-28): `sell` walked the WRONG repository ----------------
    //
    // `sell` refused "no matching item in inventory" for an item three independent readers
    // (the store's own `sell` lookup, `item <id>`, and `items`) agreed was genuinely present —
    // docs/research/store-sync.md's own live pass flagged this as unconfirmed and asked the
    // next round to compare `SellToStore`'s repo pointer against `items`' at the same instant.
    // Root-caused by fresh disasm of `CSWSCreature::GetItemRepository 0x004EF770` instead
    // (`__thiscall(this, int wantShared) -> CItemRepository*`, `ret 4`) — the SAME accessor
    // `CSWGuiStore::PopulateInventoryItemListBox 0x006C0850` (the sell list's own population
    // routine) and `CSWSCreature::AcquireItem 0x005158E0` (the routine `CSWSStore::SellItem`
    // hands a bought item to via, at every one of its three return paths) both call with the
    // literal argument 1 — never `player + CreatureItemRepo` (0xA30) directly, which is what
    // `SellToStore` used to do.
    //
    // VERIFIED, full disasm of `GetItemRepository` this pass: when `arg == 1` (every caller
    // above always passes 1) AND the creature's own `+0xA88` flag is nonzero — true for a live,
    // in-party PC, which is virtually always the case during real play — it does NOT read
    // `+0xA30` at all. It resolves `CServerExoApp::GetPartyTable` and tail-calls
    // `CSWPartyTable::GetPartyItems 0x00563340` — the SHARED PARTY-WIDE item repository at
    // `CSWPartyTable+0x118`, the EXACT SAME repository `Odyssey.PartyPool.cs`'s
    // `ResolvePartyPoolRepo` already reaches directly. A second branch (reached when `+0xA88`
    // is clear) ALSO falls through to `GetPartyItems` unless a chain of other gates fail (an
    // "is server mode"/"is this creature the effective party leader" check via
    // `CSWPartyTable::GetIsAvailable 0x563790`, then a GUI-state check via
    // `CGuiInGame::GetPartyAccessPanelUp 0x62B4D0`). ONLY when every one of those checks fails
    // does the function fall back to a bare `mov eax, [esi+0xA30]` read. In other words:
    // **KOTOR 1's own inventory model is party-shared, not per-creature** — a real player's
    // "own" backpack IS the party pool for all practical purposes, and `CSWSCreature+0xA30` is
    // the individual-creature fallback the engine itself only reaches for a creature that is
    // NOT the live, in-party PC. `SellToStore`'s old direct `+0xA30` read was walking a
    // repository that, for a real player mid-game, the engine's own code never populates —
    // matching the observed symptom exactly (a permanently "empty" lookup that a bought/given
    // item never appears in, however many readers agree it exists).
    //
    // This is independently confirmed by `CSWSCreature::GetGold 0x004EDD60`/`SetGold
    // 0x004EDD90` (below): BOTH gate on the identical `+0xA88` check and, when set, read/write
    // `CSWPartyTable+0xFC` instead of a per-creature field — i.e. gold ALSO lives on the party
    // table, not the creature, for exactly the same reason. Same engine, same "the party is one
    // shared wallet/backpack" model, two independent fields confirming it.
    //
    // The fix: `SellToStore` now calls `GetItemRepositoryFn(player, 1)` — the real accessor,
    // mirroring `PopulateInventoryItemListBox`'s own enumeration exactly — instead of reading
    // `+0xA30` itself. `Odyssey.PartyPool.cs`'s `AddItemToPartyPool` and this file's own
    // `InsertItemIntoStore` still read `+0xA30` directly for the PLAYER'S side of their own
    // spawn-then-relink step; they are NOT touched by this pass (out of the task's scope, and
    // both are unit/loopback-tested rather than live-reproduced the way `sell` was) but very
    // likely share this exact same defect for a live party member — flagged here, and again in
    // docs/research/store-sync.md, for whoever next touches the sync-apply path. Both live
    // behind `store_sync`'s existing default-OFF gate.

    /// <summary>CSWSCreature::GetItemRepository(this, int wantShared) 0x004EF770, ret 4.
    /// VERIFIED, fresh disasm this pass — see the section header above for the full trace and
    /// root-cause finding. Always call this with <paramref name="wantShared"/> = 1 to match
    /// every other caller in the retail binary (the GUI's own sell-list population, and the
    /// buy-deposit path) — it resolves the SHARED PARTY POOL for a live, in-party creature,
    /// falling back to that creature's own individual repository only when it genuinely is
    /// not one.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint> GetItemRepositoryFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint>)0x004EF770;

    /// <summary>CSWSCreature::GetGold(this) 0x004EDD60, ret (no stack cleanup — the function
    /// takes no arguments beyond <c>this</c>). VERIFIED, fresh disasm this pass: gates on the
    /// SAME creature <c>+0xA88</c> flag <see cref="GetItemRepositoryFn"/> does and, when set,
    /// returns <c>CSWPartyTable+0xFC</c> (the party-wide wallet) rather than a per-creature
    /// field — see the section header above. Also documented in docs/research/console-cheats.md
    /// (the retail cheat-function table, independently cross-checked against this pass's own
    /// disasm).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> GetGoldFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004EDD60;

    /// <summary>CSWSCreature::SetGold(this, int amount) 0x004EDD90, ret 4. VERIFIED, fresh
    /// disasm this pass: clamps the INCOMING value to an UPPER bound of 999,999,999
    /// (<c>0x3B9AC9FF</c>) only — there is no lower-bound/underflow guard anywhere in the
    /// function, confirming docs/research/store-sync.md's own live-observed finding (an
    /// unaffordable `buy`, called before this pass added <see cref="StoreAffordabilityRule"/>,
    /// drove this exact call negative and wrapped the party's plain <c>uint</c> gold to just
    /// under <c>UINT32_MAX</c>). Routes to the same party-shared wallet <see cref="GetGoldFn"/>
    /// reads. This is the primitive the <c>setgold</c> dev verb below uses to repair a save
    /// poisoned that way.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetGoldFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004EDD90;

    /// <summary>CSWGuiStore::GetItemBuyValue(panel, item) 0x006C0790, ret 4 — VERIFIED, fresh
    /// disasm of <c>BuyItem</c>'s own price lookup (the section header's "buy/sell" trace,
    /// above). The price <see cref="BuyFromStore"/>'s own affordability guard compares against
    /// <see cref="GetGoldFn"/> before ever calling <see cref="GuiBuyItemFn"/> — <c>BuyItem</c>
    /// itself has NO such guard (see the section header's disasm-backed note on
    /// <see cref="SetGoldFn"/>): the real Buy button relies entirely on the GUI graying itself
    /// out when unaffordable, a precondition calling the click handler directly bypasses.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> GetItemBuyValueFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x006C0790;

    /// <summary>
    /// Sets the party's gold directly to <paramref name="amount"/> through the real engine
    /// setter (<see cref="SetGoldFn"/>, clamped server-side to 999,999,999 like any other
    /// caller). Built to repair a save whose gold was driven negative-then-wrapped by an
    /// unguarded <c>buy</c> before this pass (docs/research/store-sync.md's 2026-08-28 live
    /// pass; docs/live-verification.md). <c>grantgold</c> (a signed DELTA written directly by
    /// the agent, <c>KotorCoop.Agent</c>'s own <c>CoopCommandDispatcher.Progression.cs</c>)
    /// cannot cleanly zero a poisoned near-<c>UINT32_MAX</c> reading without first reading it
    /// back exactly; this sets an ABSOLUTE target instead, through the same call the real GUI
    /// uses.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool SetGold(nint player, int amount, out int before, out int after)
    {
        before = 0;
        after = 0;
        LastStoreNote = string.Empty;

        if (player == 0)
        {
            LastStoreNote = "no player resolved";
            return false;
        }

        before = GetGoldFn(player);
        SetGoldFn(player, amount);
        after = GetGoldFn(player);
        return true;
    }

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
    /// <paramref name="player"/> (the buyer's <c>CSWSCreature*</c>) is used ONLY for this
    /// method's own affordability guard (<see cref="StoreAffordabilityRule"/>) — the engine's
    /// own <c>BuyItem</c> re-resolves the acting player internally and never sees this pointer.
    /// <c>BuyItem</c> itself has NO affordability check (VERIFIED, fresh disasm — see
    /// <see cref="SetGoldFn"/>'s own doc comment): a real click is guarded only by the GUI
    /// graying its own Buy button out, a precondition calling the handler directly bypasses.
    /// This guard reproduces that check before every single unit, refusing (with the price and
    /// current gold named) rather than letting <see cref="SetGoldFn"/> drive the party's gold
    /// negative and silently wrap — the exact failure docs/research/store-sync.md's 2026-08-28
    /// live pass observed.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool BuyFromStore(
        nint player, uint storeObjectId, byte* resrefAscii, int resrefLength, int count, out int bought)
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

        if (player == 0)
        {
            LastStoreNote = "no player resolved";
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

            int price = GetItemBuyValueFn(panel, item);
            int gold = GetGoldFn(player);
            if (!StoreAffordabilityRule.CanAfford(price, gold))
            {
                LastStoreNote = $"cannot afford: price {price}, gold {gold}";
                break;
            }

            GuiBuyItemFn(panel, item);
            bought++;
        }

        if (bought == 0)
        {
            if (LastStoreNote.Length == 0)
            {
                LastStoreNote = $"no stock matching that resref in store 0x{storeObjectId:X}";
            }

            return false;
        }

        return true;
    }

    // ---- root cause + fix (2026-08-29): `sell` reported success but never moved the item ---
    //
    // docs/research/store-sync.md's 2026-08-29 live pass ran the exact clean re-test the prior
    // (2026-08-28) pass called for — a `scriptgive`d resref (untracked by the party-pool
    // insert-visibility bug, ruling that out as a cause) staged, `sell` called, then an
    // independent `items` sweep two minutes later still showed the item `yours`, never `held
    // by` the store, and `grantgold 0` showed no price ever credited. `sell` itself answered
    // "applied" (status 1) throughout — a full silent no-op, not a refusal.
    //
    // Full VERIFIED disasm of `CSWGuiStore::SellItem` 0x006C0D80 THIS PASS (the task this
    // section exists to close): the function's FIRST four gates, in order — item (the one
    // stack argument) non-null, its OWN re-resolved acting player non-null
    // (`GetPlayerCreatureId 0x4AEA40` -> `GetCreatureByGameObjectID 0x4AE770`), its OWN
    // re-resolved store non-null (panel `+0x2278` -> `GetStoreByGameObjectID 0x4AE7B0`), and
    // `GetItemRepositoryFn(that re-resolved player, 1)` non-null — ALL run, and any one of them
    // failing jumps straight to the function's tail with NO OTHER WORK DONE, before the sell
    // price is even looked up, let alone gold changed or the item moved. This is decisive
    // together with the live pass's own "gold never changed" observation: `SetGold` (the
    // function's step 2, AFTER all four gates) never ran, so the 2026-08-29 sell demonstrably
    // died at one of these four early, silent bail-outs — not deeper in the function (the
    // Plot-item refusal inside `AcquireItem`, chased below, happens even later and would still
    // have let gold change first).
    //
    // WHICH of the four gates failed was not pinned down this pass (no live rig available —
    // see docs/CONTRIBUTING.md's own "No rig" note for this pass) — `GetItemRepositoryFn`'s own
    // fresh disasm (0x004EF770, re-verified this pass) shows its main branch resolves a fixed,
    // creature-independent singleton (`CSWPartyTable::GetPartyItems 0x00563340`) whenever the
    // PASSED creature's own `+0xA88` flag is set, which is true for essentially any live party
    // member — making a repo-resolve mismatch between `SellItem`'s own internal player and
    // `SellToStore`'s C#-supplied one unlikely, but not ruled out, and the player/store
    // resolve calls were not independently instrumented live either.
    //
    // THE FIX does not chase which gate it was, because it does not need to: `SellToStore`
    // already independently resolves and validates its OWN player (the caller's, threaded in),
    // its OWN store (`ResolveStoreBase`, matched against the open panel's own store id), and
    // its OWN repo (`GetItemRepositoryFn(player, 1)` — successful, since it is what FOUND the
    // item being sold in the first place). Rather than hand the item to `SellItem` and hope
    // its OWN, separately-resolved copies of the same three things agree, `SellToStore` now
    // drives the same underlying primitives `SellItem` itself calls (its OWN §-cited trace,
    // above), directly, with its own already-good pointers:
    //   1. Refuse a Plot-flagged item itself (`StoreSellGuard.IsPlotItem`) — `AcquireItem`
    //      (0x005C7610) refuses one unconditionally as the SECOND thing it does (VERIFIED,
    //      fresh disasm this pass: right after its own item-null check — `AcquireItem` takes
    //      only the item as an argument, so unlike `SellItem` there is no separate player/store
    //      null check to run first) — reproduced here so a doomed sell is reported with a
    //      specific reason instead of the store silently declining deeper in.
    //   2. Price via `GetItemSellValueFn` (0x006C07F0, `SellItem`'s own step 1).
    //   3. Unlink from the player's repo (or split off 1 unit of a stack via `SplitItemFn`,
    //      0x0055F280 — `SellItem`'s own finite-stack tail, VERIFIED fresh disasm this pass).
    //   4. `AcquireItemFn` (0x005C7610, `SellItem`'s own step 3) moves it into the store.
    //   5. Credit gold (`SetGoldFn`, `SellItem`'s own step 2 — reordered to come AFTER
    //      `AcquireItem`, matching the order these steps' OWN dependency requires: the price was
    //      already looked up before the item moved, same as retail) — and write the SAME new
    //      total into the panel's own cached currency-label int (`StorePanelGoldCacheOffset`,
    //      `+0x2270`) that `BuyItem` itself reads as ITS OWN arithmetic base rather than a fresh
    //      `GetGold` call (VERIFIED, both functions' disasm) — otherwise a follow-up buy on the
    //      same open panel would compute against a stale pre-sell gold figure.
    //   6. Refresh both on-screen lists once after the whole loop (the same two calls
    //      `SellItem`'s own tail makes per-unit) so the GUI matches reality without needing
    //      `SellItem` itself in the loop at all.
    //
    // `AcquireItem`'s own return value is NOT checked (deliberately — see its own doc comment:
    // one of its three exit paths does not set an explicit success flag, so trusting it blind
    // would be worse than not checking at all). A live-verified positive check (e.g. re-reading
    // some engine-side "did the item actually move" signal) was considered and deliberately NOT
    // added this pass: with no rig to validate a candidate check against, shipping an unverified
    // guess as if it were a verified guard risks being WRONG in a way that is worse than no
    // check — this codebase's own `buy` (confirmed working) sets the precedent of trusting the
    // engine call the same way, checking only what can be ruled out ahead of time (the Plot-item
    // case, above). Whoever next has rig access should watch specifically for a store entry
    // silently vanishing (an `AcquireItem` refusal past the Plot check that this pass could not
    // rule out) as part of the live re-test below.
    //
    // NEEDS-LIVE: this pass had no rig. The live re-test docs/research/store-sync.md's own
    // 2026-08-29 section already spelled out — `scriptgive g_w_blstrpstl001`, `openstore
    // Larrim_Store` on both, `sell Larrim_Store g_w_blstrpstl001`, then an `items` sweep
    // confirming it is `held by` the store and gone from the player, and a buy-back on the
    // OTHER instance — still needs to be run by whoever next has rig access. See that doc's own
    // dated section for the full write-up this comment summarizes.

    /// <summary>CSWSItem + this = the CSWSObject::plot field. VERIFIED, tools/kotor1_0_3.db:
    /// CSWSItem.server_object embeds a CSWSObject at +0x010, and CSWSObject.plot sits at
    /// +0x0F8, so 0x010 + 0x0F8 = 0x108. <c>CSWSStore::AcquireItem</c> tests this field and
    /// refuses — unconditionally, right after its own item-null check — when it is nonzero.
    /// See <see cref="StoreSellGuard"/> and the section header above.</summary>
    private const int ItemPlotOffset = 0x108;

    /// <summary>CSWGuiStore + this = the panel's own cached "gold currently on the currency
    /// label" int (see the "CORRECTION" note above `ShowStoreGuiFn`'s own section). `BuyItem`
    /// reads this SAME field — not a fresh `GetGold` call — as its own arithmetic base, so a
    /// sell that updates the player's real gold without also updating this cache would desync
    /// the NEXT buy on the same open panel. VERIFIED, fresh disasm (both BuyItem and SellItem
    /// write it).</summary>
    private const int StorePanelGoldCacheOffset = 0x2270;

    /// <summary>CSWGuiStore::GetItemSellValue(panel, item) 0x006C07F0, ret 4. VERIFIED, fresh
    /// disasm this pass — <c>SellItem</c>'s own price lookup, the sell-side mirror of
    /// <see cref="GetItemBuyValueFn"/>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> GetItemSellValueFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x006C07F0;

    /// <summary>CSWSStore::AcquireItem(store, item) 0x005C7610, ret 4. VERIFIED, full fresh
    /// disasm this pass (the section header above): the store's own "take an item from the
    /// seller" primitive — refuses (returns 0, no side effect) on a null item, a Plot-flagged
    /// item (<see cref="ItemPlotOffset"/>), or (deeper in the function) an unresolvable current
    /// possessor; otherwise unlinks the item from its CURRENT possessor via the item's OWN
    /// <c>+0x268</c> possessor id (NOT via any repository pointer the caller supplies) and
    /// links it into the store's stock, merging into a matching finite entry (StackSize++,
    /// discarding any extra units on the incoming item — this is exactly why a stacked source
    /// item must be split to exactly 1 unit first, see <see cref="SplitItemFn"/>) or appending
    /// a fresh entry. Its return value is NOT reliable across every path (the "append a fresh
    /// entry" tail falls through to whatever <c>AddItemToInventory</c> returned rather than
    /// setting an explicit success flag) — <see cref="SellToStore"/> deliberately does not
    /// check it, the same way the retail GUI handler itself never does; see the section header
    /// above for why a live-verified alternative check was not added this pass.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> AcquireItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x005C7610;

    /// <summary>Splits exactly 1 unit off a stacked item, returning the NEW split-off item
    /// (StackSize 1) and decrementing the source item's own StackSize in place — VERIFIED,
    /// fresh disasm this pass of <c>SellItem</c>'s own finite-stack tail (`push 1; ecx=item;
    /// call 0x0055F280`), which hands the split-off unit to <see cref="AcquireItemFn"/> the
    /// same way this file's own <see cref="SellToStore"/> now does. Despite
    /// tools/kotor1_0_3.db attributing the address to CSWSCreature (<c>SplitItem_2</c>), the
    /// disassembled calling convention is unambiguous: <c>this</c> is the ITEM being split, not
    /// a creature — the DB's class attribution is trusted here for the address only.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint> SplitItemFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint>)0x0055F280;

    /// <summary>CSWGuiStore::PopulateStoreItemListBox(panel) 0x006C1840, ret 0 (this-only).
    /// Refreshes the on-screen STORE list. VERIFIED safe to call directly outside a GUI click:
    /// it is part of <c>BuyItem</c>'s own tail, and <c>buy</c> is confirmed working through
    /// that exact call. <see cref="SellToStore"/> calls it once after its whole loop rather
    /// than once per unit (a cosmetic refresh, not a correctness requirement).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PopulateStoreItemListBoxFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006C1840;

    /// <summary>CSWGuiStore::PopulateInventoryItemListBox(panel) 0x006C0850, ret 0. Refreshes
    /// the on-screen PLAYER INVENTORY list — the sell side's counterpart to
    /// <see cref="PopulateStoreItemListBoxFn"/>, also part of <c>SellItem</c>'s own tail.
    /// Also the SAME accessor (<c>GetItemRepositoryFn(player, 1)</c>, per the 2026-08-28
    /// section above) this file's own sell path resolves its repo through, confirming that
    /// repo is genuinely what a real click's own inventory list would show.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PopulateInventoryItemListBoxFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006C0850;

    /// <summary>
    /// Sells up to <paramref name="count"/> units of the first item in
    /// <paramref name="player"/>'s own inventory whose tag matches
    /// <paramref name="resrefAscii"/> to store <paramref name="storeObjectId"/>. Requires the
    /// store's own GUI to already be open and matching <paramref name="storeObjectId"/>.
    ///
    /// As of the 2026-08-29 fix (see the section header above) this drives the underlying
    /// engine primitives (<see cref="GetItemSellValueFn"/>, <see cref="AcquireItemFn"/>,
    /// <see cref="SplitItemFn"/>, gold) directly with THIS method's own already-validated
    /// player/store/repo, instead of handing the item to the real <c>CSWGuiStore::SellItem</c>
    /// click handler and trusting its own separately-resolved copies of the same three things
    /// to agree — which is what silently no-op'd in the 2026-08-29 live pass.
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

        nint store = ResolveStoreBase(storeObjectId);
        if (store == 0)
        {
            return false;
        }

        // BUG FIX (2026-08-28, live-found): this used to read `player + CreatureItemRepo`
        // (`+0xA30`) directly. VERIFIED, fresh disasm this pass (see the section header above,
        // "root cause + fix"): a live, in-party PC's actual inventory is the SHARED PARTY POOL
        // repository, reached only through `GetItemRepositoryFn(player, 1)` — the same accessor
        // `PopulateInventoryItemListBox` (the sell list's own population routine) calls to
        // enumerate what a real click would show. Reading `+0xA30` directly walked a repository
        // the engine itself never populates for a real party member, which is why `sell`
        // refused "no matching item in inventory" for an item every other reader agreed was
        // present.
        nint repo = GetItemRepositoryFn(player, 1);
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

            uint plot = *(uint*)(item + ItemPlotOffset);
            if (StoreSellGuard.IsPlotItem(plot))
            {
                string resrefText = Encoding.ASCII.GetString(resrefAscii, resrefLength);
                LastStoreNote = $"'{resrefText}' is a plot item and cannot be sold";
                break;
            }

            int price = GetItemSellValueFn(panel, item);
            int goldAfter = GetGoldFn(player) + price;

            ItemIdentityFields fields = ReadItemFields(item);
            uint stackSize = fields.StackSize == 0 ? 1u : fields.StackSize;

            nint toAcquire;
            if (stackSize > 1)
            {
                // Mirrors SellItem's own finite-stack tail: split off exactly 1 unit rather
                // than handing the whole stack to AcquireItem, which only ever increments a
                // matching store entry by 1 regardless of the incoming item's own StackSize
                // (see AcquireItemFn's own doc comment) — handing it the full stack would
                // silently discard every unit past the first.
                toAcquire = SplitItemFn(item, 1);
                if (toAcquire == 0)
                {
                    LastStoreNote = "failed to split a stacked item";
                    break;
                }
            }
            else
            {
                RepositoryRemoveItemFn(repo, item);
                toAcquire = item;
            }

            // AcquireItem's own return value is not reliably checkable (see AcquireItemFn's own
            // doc comment) and — like the retail GUI handler itself, and like this codebase's
            // own already-confirmed-working BuyFromStore, which does not check the native
            // SellItem call's outcome either — this is trusted to have succeeded once the ONE
            // refusal this codebase CAN rule out ahead of time (the Plot-item check, above) has
            // already been cleared. See the section header above for why a live-verified
            // post-hoc check was deliberately not added this pass (no rig to validate one
            // against).
            AcquireItemFn(store, toAcquire);

            SetGoldFn(player, goldAfter);
            *(int*)(panel + StorePanelGoldCacheOffset) = goldAfter;
            sold++;
        }

        if (sold > 0)
        {
            PopulateStoreItemListBoxFn(panel);
            PopulateInventoryItemListBoxFn(panel);
        }

        if (sold == 0)
        {
            if (LastStoreNote.Length == 0)
            {
                LastStoreNote = "no matching item in inventory";
            }

            return false;
        }

        return true;
    }
}
