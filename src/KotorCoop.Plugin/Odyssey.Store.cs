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
    // docs/research/store-sync.md §1.6/§6 read ExecuteCommandOpenStore (0x00540300) far enough
    // to find it resolves oStore (§1.2's GetStoreByGameObjectID) and calls CGuiInGame::
    // ShowStoreGui (0x0062E310), but deliberately declined to build a verb on that reading
    // alone: "the call chain from there... was not traced far enough to be confident it is a
    // direct call rather than a queued client/server GUI message." This pass finished the trace
    // (fresh disasm, both functions, this session):
    //
    //   * ShowStoreGui takes ONLY `this` (CGuiInGame*) — no stack args at all. It checks three
    //     gates (+0x108 initialised, +0x30 clear, +0xB4 clear — the SAME dialogue-active flag
    //     GuiConversationActive already names in Odyssey.Dialogue.Spectate.cs), then allocates
    //     (or reuses) the CSWGuiStore panel at CGuiInGame+0x84. It does NOT read a store id
    //     anywhere in its own body.
    //   * ExecuteCommandOpenStore writes the resolved store's bonus markup/markdown ints
    //     (+0x250/+0x24C — correcting store-sync.md §1.6's own "different resolve chain"
    //     inference: fresh disasm this pass shows `eax` at the write site IS the SAME
    //     vtable-cast CSWSStore* GetStoreByGameObjectID already returns, not a separate
    //     structure) DIRECTLY INTO THE STORE, then calls ShowStoreGui, THEN separately
    //     allocates a 0x34-byte message (tagged 0x16) and posts it through 0x4AEA40 — a second,
    //     unrelated step this pass did not chase further (very likely the retail client/server
    //     message-bus family docs/research/container-sync.md already documents for
    //     OpenInventory/CloseInventory, not something ShowStoreGui itself needs).
    //   * The store id that CSWGuiStore::PopulateStore (0x006C0610, §1.2) reads back off
    //     CSWGuiStore+0x227C is never written by ShowStoreGui or by the VM handler in the
    //     window this pass read — so OpenStore below writes it directly (the SAME field
    //     PopulateStore's own disasm names) and calls PopulateStore directly to force the
    //     refresh, rather than depend on whatever the 0x34-byte message eventually does.
    //
    // See docs/research/store-sync.md's 2026-08-27 addendum for the full disassembly.

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

    /// <summary>CSWGuiStore + this = the currently-open store's OBJECT ID. VERIFIED, fresh
    /// disasm of CSWGuiStore::PopulateStore 0x006C0610 (docs/research/store-sync.md §1.2).</summary>
    private const int StorePanelObjectIdOffset = 0x227C;

    /// <summary>CSWSStore + this = the session-scoped bonus mark-up/mark-down ints
    /// ExecuteCommandOpenStore's VM handler writes (both clamped [-100,100] there — this call
    /// writes 0/0, matching a bare <c>OpenStore(oStore, oPC)</c> with no bonus args). VERIFIED,
    /// fresh disasm this pass — see the section header above for the correction to
    /// store-sync.md §1.6's own inference.</summary>
    private const int StoreBonusMarkUp = 0x250;

    private const int StoreBonusMarkDown = 0x24C;

    /// <summary>CGuiInGame::ShowStoreGui(this) -&gt; void. VERIFIED, fresh disasm this pass
    /// (0x0062E310): thiscall, no stack parameters read anywhere in the function body. See the
    /// section header above for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ShowStoreGuiFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x0062E310;

    /// <summary>CSWGuiStore::PopulateStore(this) -&gt; void. VERIFIED, docs/research/store-sync.md
    /// §1.2 — reads the panel's own <see cref="StorePanelObjectIdOffset"/>, resolves it through
    /// the SAME safe, type-checked accessor <see cref="ResolveStoreBase"/> wraps, and refreshes
    /// the displayed item list.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PopulateStoreFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006C0610;

    /// <summary>
    /// Opens the store GUI for the local player against <paramref name="store"/> (a base
    /// CSWSStore* — see <see cref="ResolveStoreBase"/> or <see cref="CreatureFinder.FindStoreByTag"/>)
    /// — the C++ entry point <c>ExecuteCommandOpenStore</c>'s own VM handler ultimately reaches
    /// (<c>ShowStoreGui</c>), called directly here instead of through the VM's own
    /// argument-popping machinery. See the section header above for the full reasoning.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool OpenStore(nint store, out string reason)
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

        ShowStoreGuiFn(gui);

        nint panel = *(nint*)(gui + InGameGuiStoreOffset);
        if (panel == 0)
        {
            reason = "ShowStoreGui did not produce a panel (allocation failed)";
            return false;
        }

        uint storeObjectId = *(uint*)(store + 0x04); // CSWSObject + this = the object id.
        *(uint*)(panel + StorePanelObjectIdOffset) = storeObjectId;

        PopulateStoreFn(panel);
        return true;
    }
}
