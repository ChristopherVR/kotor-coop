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
    /// live store.</summary>
    private static nint ResolveStoreRepo(uint storeObjectId)
    {
        nint store = ResolveStoreBase(storeObjectId);
        return store == 0 ? 0 : store + StoreItemRepo;
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
}
