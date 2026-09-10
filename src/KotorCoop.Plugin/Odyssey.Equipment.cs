// Odyssey.Equipment.cs - creature appearance and equipment slot read/apply.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- appearance --------------------------------------------------------------

    // CSWSCreature::UpdateAppearanceDependantInfo(this, int bStore) — rebuilds modeltype, the
    // per-body-part model CExoStrings and the sibling appearance field from the appearance.2da
    // row at creature+0xA60. The engine runs this after EVERY appearance-row change (Disguise,
    // the replication message sites: `mov [esi+0xA60],cx; push 1; mov ecx,esi; call 0x4ED850`).
    // A direct field poke that skips it leaves the body-part strings allocated for the OLD
    // appearance, so a later equip's appearance rebuild reads a dangling CExoString and crashes
    // in CompareNoCase. See docs/research/phantom-equip-crash.md.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> UpdateAppearanceDependantInfoFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004ED850;

    /// <summary>Rebuilds a creature's appearance-derived state after its appearance.2da row was
    /// changed directly, so nothing downstream holds a stale model string. MAIN THREAD ONLY.</summary>
    public static void RebuildAppearance(nint creature)
    {
        if (creature != 0)
        {
            UpdateAppearanceDependantInfoFn(creature, 1);
        }
    }

    // ---- equipment ---------------------------------------------------------------

    /// <summary>CreateItemOnObject(string sTemplate, object oTarget, int nStackSize) -&gt; item.</summary>
    private const nint ExpectedCreateItemOnObject = 0x0052F570;

    /// <summary>ActionEquipItem(object oItem, int nInventorySlot, int bInstant).</summary>
    private const nint ExpectedActionEquipItem = 0x005355F0;

    private static int _routineCreateItemOnObject = -1;
    private static int _routineActionEquipItem = -1;

    /// <summary>How many repository unlinks <see cref="EquipItemFromTemplateDirect"/> has
    /// logged — bounded, the same way PartyPresenceController bounds its own equip lines.</summary>
    private static int _equipUnlinksLogged;

    /// <summary>nwscript inventory slots for the visible gear.</summary>
    public const int InventorySlotBody = 1;
    public const int InventorySlotRightWeapon = 4;
    public const int InventorySlotLeftWeapon = 5;

    /// <summary>
    /// Gives a creature an item by blueprint and equips it into a slot, through the
    /// engine's own NWScript pair: CreateItemOnObject then ActionEquipItem (instant).
    /// A resref this install does not have fails at the create step and reports failure
    /// 7 — the correct soft behaviour when peers have different mods.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool EquipItemFromTemplate(uint creatureObjectId, byte* resrefAscii, int resrefLength, int inventorySlot, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (_routineCreateItemOnObject < 0)
        {
            _routineCreateItemOnObject = FindRoutine(table, ExpectedCreateItemOnObject);
            _routineActionEquipItem = FindRoutine(table, ExpectedActionEquipItem);
        }

        if (_routineCreateItemOnObject < 0 || _routineActionEquipItem < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // CreateItemOnObject(string sTemplate, object oTarget, int nStackSize),
        // pushed in reverse declaration order.
        if (StackPushInteger(vm, 1) == 0
            || StackPushObject(vm, creatureObjectId) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return false;
        }

        var create = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnObject;

        // See GiveItemToInventory's identical bracket for why: this is the only point that can
        // tell ItemTemplateSpawnHook "this creation is ours" (docs/research/party-pool-sync.md
        // §20).
        ItemTemplateSpawnHook.NextSpawnIsCheat = true;
        int createResult;
        try
        {
            createResult = create(implementer, _routineCreateItemOnObject, 3);
        }
        finally
        {
            ItemTemplateSpawnHook.NextSpawnIsCheat = false;
        }

        if (createResult != 0)
        {
            failure = 6;
            return false;
        }

        uint itemId = ObjectInvalid;
        if (StackPopObject(vm, &itemId) == 0)
        {
            failure = 7;
            return false;
        }

        if (itemId == ObjectInvalid)
        {
            // The blueprint does not exist in this install (peer has a mod we lack).
            failure = 7;
            return false;
        }

        // ActionEquipItem applies to the VM's caller object; redirect OBJECT_SELF to
        // the phantom, exactly as the move and pickup dispatches do.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            // ActionEquipItem(object oItem, int nInventorySlot, int bInstant), reversed.
            if (StackPushInteger(vm, 1) == 0
                || StackPushInteger(vm, inventorySlot) == 0
                || StackPushObject(vm, itemId) == 0)
            {
                failure = 8;
                return false;
            }

            var equip = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionEquipItem;
            if (equip(implementer, _routineActionEquipItem, 3) != 0)
            {
                failure = 9;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 10;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    /// <summary>
    /// Gives a creature an item by blueprint straight into its inventory, through
    /// CreateItemOnObject alone — no ActionEquipItem, so it lands in the backpack rather
    /// than a visible slot. The first half of <see cref="EquipItemFromTemplate"/>, split out
    /// because a corpse's loot has no slot to equip into; it only has to be there to loot.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint GiveItemToInventory(
        uint creatureObjectId, byte* resrefAscii, int resrefLength, int stackSize, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return ObjectInvalid;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return ObjectInvalid;
        }

        if (_routineCreateItemOnObject < 0)
        {
            _routineCreateItemOnObject = FindRoutine(table, ExpectedCreateItemOnObject);
        }

        if (_routineCreateItemOnObject < 0)
        {
            failure = 3;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return ObjectInvalid;
        }

        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // CreateItemOnObject(string sTemplate, object oTarget, int nStackSize),
        // pushed in reverse declaration order.
        if (StackPushInteger(vm, Math.Max(1, stackSize)) == 0
            || StackPushObject(vm, creatureObjectId) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        var create = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnObject;

        // ItemTemplateSpawnHook's own detour fires SYNCHRONOUSLY, inside this one call, whenever
        // it actually creates an item — this is the ONLY point that can tell the hook "this
        // creation is OURS" (a cheat/apply/sync call), since the hook site itself cannot
        // distinguish a script's own CreateItemOnObject from this exact call (see
        // ItemTemplateSpawnHook.NextSpawnIsCheat's own doc comment, docs/research/
        // party-pool-sync.md §20). Bracketed tightly around the call alone, restored in a
        // finally so a refusal on the line below never leaves it stuck true.
        ItemTemplateSpawnHook.NextSpawnIsCheat = true;
        int createResult;
        try
        {
            createResult = create(implementer, _routineCreateItemOnObject, 3);
        }
        finally
        {
            ItemTemplateSpawnHook.NextSpawnIsCheat = false;
        }

        if (createResult != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint itemId = ObjectInvalid;
        if (StackPopObject(vm, &itemId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        if (itemId == ObjectInvalid)
        {
            // The blueprint does not exist in this install (peer has a mod we lack).
            failure = 8;
        }

        return itemId;
    }

    // ---- direct (non-queued) equip, for a live AI-active creature ----------------------

    /// <summary>
    /// CSWSCreature::EquipItem(int nInternalSlot, object oItem, int p3, int p4, int p5) —
    /// the engine's own SYNCHRONOUS "put this on now" primitive (0x004FEB90; confirmed by name
    /// in tools/kotor1_0_3.db, __thiscall, param_size 20 bytes = 5 dwords). Fully disassembled
    /// this round (0x004FEB90-0x004FECDC, single entry/exit, no early return): calls
    /// <c>CSWInventory::GetItemInInventory(item)</c> (0x5A4950 — DB name, but its actual
    /// behaviour, read from the disassembly, is "is this exact item object already sitting in
    /// one of my 18 equip slots", NOT a can-equip/proficiency gate — the earlier version of
    /// this comment calling it <c>CanEquipWeapon</c> was wrong), and if true calls
    /// <c>UnequipItem(creature, item)</c> (0x4FAA70) first so the same item is not left
    /// double-referenced when moved between slots; then unconditionally calls
    /// <c>CSWSItem::ApplyItemProperties</c> (0x553BB0, gated on p3), <c>CSWInventory::
    /// PutItemInSlot(slot, item)</c> (0x5A4BE0), a stat recompute
    /// (<c>ComputeArmourClass</c>/<c>UpdateCombatInformation</c>) and a client-update broadcast
    /// — VERIFIED. **The function has exactly one `ret` and it always sets eax=1**: there is no
    /// refusal path, no CanEquip/proficiency/two-handed check, and no handling for "the target
    /// slot already holds a DIFFERENT item" (only the incoming item's own prior slot, if any,
    /// is vacated) — this is a bare, unchecked "put this in this slot" primitive, matching how
    /// its only other callers (<c>ForceEquipClothing</c>/<c>EquipMostDamagingMeleeWeapon</c>/
    /// etc., 15 xrefs total, all inside those two AI-helper families) use it: on state they
    /// already know is clean, calling exactly the internal slot value the engine itself
    /// computes (see <see cref="EquipSlotMap"/>), never a caller-supplied guess.
    ///
    /// **<c>PutItemInSlot</c>'s own write is conditional and silent**: it resolves the slot
    /// argument through a private helper (0x5A49C0, disassembled and its jump tables read as
    /// data this round — see <see cref="EquipSlotMap"/>'s doc comment for the full bit-flag
    /// table) and, if that resolves to a NULL pointer (an unrecognised slot value), skips the
    /// write entirely with no signal to the caller — <c>EquipItem</c> still runs to completion
    /// and still returns 1. This is the actual mechanism behind the "equip reports success but
    /// the raw slot never changes" symptom docs/research/companion-state-sync.md §10.2 found:
    /// the OLD <see cref="EquipSlotMap"/> passed 5/6 for the weapon slots, both of which hit
    /// this helper's invalid path. Checking <c>EquipItem</c>'s return value (§10.2's leading
    /// hypothesis) cannot detect this — the return is unconditionally 1 regardless of whether
    /// the write landed; <see cref="EquipItemFromTemplateDirect"/> instead reads the raw slot
    /// back after the call to confirm it, the same "verify a researched offset live" rule
    /// docs/ARCHITECTURE.md's engine-write rules already require.
    ///
    /// This is deliberately NOT what <see cref="EquipItemFromTemplate"/> uses.
    /// <c>ActionEquipItem</c>'s VM dispatch (0x005355F0) does not equip synchronously either:
    /// it calls <c>CSWSCreature::AddEquipItemActions</c> (0x004F0420), which appends a node
    /// (action-type byte 6) onto the creature's OWN <c>CSWSCombatRound</c> pending list
    /// (<c>creature+0x9C8</c> -&gt; round, node list at <c>round+0x9B0</c>,
    /// <c>CSWSCombatRound::AddEquipAction</c> 0x004D3C30) — VERIFIED by disassembly. A queued
    /// action needs the engine's own per-tick round processing
    /// (<c>UpdateCombatRoundTimer</c>/<c>CSWVirtualMachineCommands::ExecuteCommandActionAttack</c>'s
    /// own arming calls, see <c>Odyssey.Combat.Attack.cs</c>'s <c>QueueAttackAction</c>) to ever
    /// drain it — nothing in <c>EquipItemFromTemplate</c>'s call chain arms that, and a
    /// mirrored companion's round is repeatedly touched every
    /// <c>PartyPresenceController.DriveOne</c> pass by <see cref="ClaimCreature"/>'s call into
    /// <c>ClearAllActionsFn</c> -&gt; <c>CSWSCombatRound::ClearAllAttacks</c> (0x004D37E0,
    /// VERIFIED to loop the round's five maneuver sub-structures, NOT <c>round+0x9B0</c>
    /// itself — so the node is not directly wiped, but nothing else ever arms the round to
    /// drain it either). A phantom is never claimed at all (<c>PhantomController</c> never
    /// calls <see cref="ClaimCreature"/>), which is the likeliest reason the identical VM call
    /// has shipped and been observed working for a phantom but not for a live party member —
    /// NOT the caller-object redirect, which this round's disassembly of both
    /// <c>CreateItemOnObject</c> (0x0052F570) and <c>ExecuteCommandActionEquipItem</c>
    /// (0x005355F0) confirms correctly targets the explicit creature id all the way down to
    /// <c>CSWSCreature::AcquireItem</c>/<c>AddEquipAction</c>, with no dependency on VM
    /// caller-object state surviving past the call that reads it. See
    /// docs/research/companion-state-sync.md for the full writeup.
    ///
    /// This primitive sidesteps the whole action-queue/combat-round question: no queue, no
    /// arming, immune to <see cref="ClaimCreature"/>'s per-pass wipe, because nothing is left
    /// pending after the call returns.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE — the call itself has never been exercised against a
    /// running game; see companion-state-sync.md's live test plan.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, int, int, int, int> EquipItemFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, int, int, int, int>)0x004FEB90;

    /// <summary>
    /// The companion-safe counterpart to <see cref="EquipItemFromTemplate"/>: creates the item
    /// on the target exactly the same way (<see cref="GiveItemToInventory"/>, itself unchanged
    /// and already VERIFIED target-based — no caller-object dependency at all, confirmed by
    /// disassembly of <c>CreateItemOnObject</c> this round), then equips it through the
    /// synchronous <see cref="EquipItemFn"/> instead of the queued VM action.
    ///
    /// <paramref name="nwscriptSlot"/> is the same 1/4/5 convention every other equip caller in
    /// this codebase already uses (<see cref="InventorySlotBody"/> etc.); mapped through
    /// <see cref="EquipSlotMap"/> to the engine's own BIT-FLAG internal slot value (2/0x10/0x20)
    /// — VERIFIED by disassembly this round (see <see cref="EquipSlotMap"/>'s own doc comment).
    /// The OLD "byte offset / 4" mapping (2/5/6) silently no-opped both weapon slots — 5 and 6
    /// are not valid bit-flags, so <c>CSWInventory</c>'s own slot resolver returned NULL and the
    /// write never happened, while <see cref="EquipItemFn"/> still reported success.
    ///
    /// Never trusts <see cref="EquipItemFn"/>'s own return value — VERIFIED by full disassembly
    /// to be unconditionally 1 regardless of whether the write landed (see its own doc comment).
    /// Instead this reads the raw <c>CSWInventory</c> slot back
    /// (<see cref="EquipSlotMap.ToSlotByteOffset"/>) after the call and confirms it now holds
    /// the item id <see cref="GiveItemToInventory"/> just created, so an unresolved slot or any
    /// other silent engine-side no-op is reported to the caller as a genuine failure instead of
    /// a false "equipped" — never silently fail.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE — the corrected slot values and the readback verification
    /// have never been exercised against a running game; see companion-state-sync.md's live
    /// test plan.
    /// </summary>
    public static bool EquipItemFromTemplateDirect(
        uint creatureObjectId, byte* resrefAscii, int resrefLength, int nwscriptSlot, out int failure)
    {
        failure = 0;

        int internalSlot = EquipSlotMap.ToInternalSlot(nwscriptSlot);
        int slotByteOffset = EquipSlotMap.ToSlotByteOffset(nwscriptSlot);
        if (internalSlot < 0 || slotByteOffset < 0)
        {
            failure = 20;
            return false;
        }

        uint itemId = GiveItemToInventory(creatureObjectId, resrefAscii, resrefLength, 1, out int giveFailure);
        if (itemId == ObjectInvalid)
        {
            failure = giveFailure != 0 ? giveFailure : 21;
            return false;
        }

        nint creature = ResolveServerObject(creatureObjectId);
        if (creature == 0)
        {
            failure = 22;
            return false;
        }

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            failure = 23;
            return false;
        }

        // Unlink the freshly created body from the creature's item repository FIRST — exactly
        // what the engine's own ForceEquipClothing does right before its EquipItem call
        // (0x00501D4D `Contains(item)` -> 0x00501D59 `CItemRepository::RemoveItem(item)` ->
        // 0x00501D69 EquipItem, VERIFIED by disassembly 2026-09-02). EquipItem itself never
        // touches the repository, and GiveItemToInventory files the body wherever
        // CSWSCreature::GetItemRepository(1) points — for a live party member (every mirrored
        // companion a client dresses) that is the SHARED PARTY POOL. Without this step the
        // equipped body stays linked into the stash: every pool scan counted the companion's
        // clothes+pistol as a surplus and the client deposited copies to the host after every
        // rejoin (live 2026-09-01, shared-inventory-v2.md §17), and a later pool reconcile could
        // "remove" the body from under the slot it is worn in. RemoveItem is a documented no-op
        // when the item is not linked (see Odyssey.PartyPool.cs's RepositoryRemoveItemFn).
        //
        // The possessor too, in the engine's own order (0x00501D43 SetPossessor(item,
        // creatureId, 0, 0, OBJECT_INVALID) comes first): filing into the pool stamped the
        // body with the pool's parent — the PC — so without this the wearer's gear reads as
        // the PC's in every possessor-keyed view (ReadPossessedItems, the `items` listing).
        SetPossessorFn(item, creatureObjectId, 0, 0, ObjectInvalid);
        nint repo = ResolveCreatureItemRepo(creature);
        int unlinked = repo != 0 ? RepositoryRemoveItemFn(repo, item) : 0;
        if (unlinked != 0 && _equipUnlinksLogged < 8)
        {
            _equipUnlinksLogged++;
            PluginHost.Log(
                $"equip: unlinked 0x{itemId:X} from 0x{creatureObjectId:X}'s repository "
                + "0x" + repo.ToString("X") + " before wearing it (repo count now "
                + $"{*(int*)(repo + RepositoryItemCount)})");
        }

        // p3/p4/p5 read as (1, 0, 0) at every clean call site this round's disassembly found
        // (CSWSCreature::ForceEquipClothing's own 0x00501D69). NEEDS-LIVE: their exact meaning
        // is inferred from position, not confirmed against a name.
        int result = EquipItemFn(creature, internalSlot, item, 1, 0, 0);
        if (result == 0)
        {
            // Never observed in this build's disassembly — EquipItem has a single,
            // unconditional `mov eax,1` return (see EquipItemFn's own doc comment) — but the
            // delegate is typed as int and the check is free, so a future build (or a call
            // path this round's static pass did not reach) that DOES refuse is still caught
            // here rather than silently reported as success.
            failure = 24;
            return false;
        }

        // The real gate: EquipItem's own return is unconditionally 1 even when
        // CSWInventory::PutItemInSlot's internal write silently no-ops on an unresolved slot
        // (see EquipItemFn's own doc comment). Read the raw slot back — creature+0xA2C is the
        // creature's CSWInventory*, VERIFIED and already load-bearing elsewhere in this file
        // (Odyssey.Combat.Attack.cs, Odyssey.PartyPool.cs) — and confirm the write actually
        // landed before reporting success. docs/ARCHITECTURE.md's engine-write rules: verify a
        // researched offset live before shipping the write.
        nint inventory = *(nint*)(creature + 0xA2C);
        if (inventory == 0)
        {
            failure = 25;
            return false;
        }

        uint slotContent = *(uint*)(inventory + slotByteOffset);
        if (slotContent != itemId)
        {
            failure = 26;
            return false;
        }

        return true;
    }

    /// <summary>How many starting-gear equips this process has logged — bounded, the same
    /// convention <see cref="EquipItemFromTemplateDirect"/>'s own unlink logging and every
    /// other equip logger in this project already follows.</summary>
    private static int _startingGearEquipsLogged;

    /// <summary>How wide a net <see cref="EquipUnequippedStartingGear"/> casts around the
    /// creature's own object id for a starting item that is not linked into either
    /// <c>CItemRepository</c> it already checks — LIVE-MEASURED 2026-09-10: chargen mints a
    /// character's starting items in the same creation burst as the creature itself (ids handed
    /// out sequentially), and every candidate this project's live rig has seen sat within a
    /// handful of ids of the creature's own. Generous rather than tight on purpose: every
    /// candidate this window produces is still vetted by <see cref="ResolveItemBase"/> (vtable
    /// AND id re-checked before it is trusted at all) and by its own Possessor field matching
    /// this exact creature before anything is touched, so a wider window costs a few wasted
    /// reads, never a wrong write.</summary>
    private const uint StartingGearIdWindow = 32;

    /// <summary>
    /// Repairs a first-time joiner's OWN starting gear: finds the items chargen granted that are
    /// not yet in an equip slot, and equips each one whose blueprint prefix
    /// <see cref="StartingGearSlotRule"/> recognises through the same direct, readback-verified
    /// primitive <see cref="EquipItemFromTemplateDirect"/> uses — never the queued VM action
    /// (<c>ActionEquipItem</c>), which is exactly the mechanism suspected of leaving these items
    /// unequipped in the first place: <c>ActionEquipItem</c>'s VM dispatch only APPENDS a node
    /// onto the creature's own combat-round pending list (see <see cref="EquipItemFn"/>'s own
    /// doc comment) and nothing observed in this project's `join_mode = newcharacter` path ever
    /// drains it before <c>NewCharacterCaptureController</c> quicksaves the character —
    /// LIVE-CONFIRMED 2026-09-10 (docs/research/world-adoption.md's addendum): a fresh
    /// character's own <c>CSWInventory</c> body/right-weapon slot dwords read zero, unchanged,
    /// 90+ seconds after joining, while the exact items the class package granted sat possessed
    /// (not equipped) the whole time.
    ///
    /// Candidates come from THREE places, because chargen's own items were not consistently
    /// findable in any one of them across this round's live passes: the creature's own personal
    /// backpack (<c>CreatureItemRepo</c>, +0xA30 — LIVE-MEASURED empty every single run), the
    /// repository <see cref="ResolveCreatureItemRepo"/>'s engine accessor returns (which, per
    /// <see cref="GiveItemToInventory"/>'s own doc comment, is the SHARED PARTY POOL for a live
    /// party member — the local player always is one), and a bounded id-window scan
    /// (<see cref="StartingGearIdWindow"/>) around the creature's own id for a straggler linked
    /// into neither.
    ///
    /// ⚠ PARTIAL FIX, LIVE-CONFIRMED 2026-09-10 (docs/research/world-adoption.md's addendum,
    /// six live joins against one class package): the starting WEAPON is reliably found — every
    /// single run — through the party-pool accessor repository, and this method equips it
    /// correctly (readback-verified). The starting CLOTHES item was NEVER found by any of the
    /// three candidate sources in any run, including with <see cref="StartingGearIdWindow"/>
    /// widened and <c>NewCharacterCaptureController.EquipRepairExtraMs</c> extended to 15
    /// seconds — despite the SAME item being confirmed genuinely possessed by this same creature
    /// moments later (the `items` console verb's own committed-memory scan finds it, tag
    /// "g_a_clothes01", every time). Ruled out: item-tag caching (the agent-side sweep this
    /// mirrors resolves it fine once possible), classification (this rule handles the exact tag
    /// string — see <see cref="StartingGearSlotRule"/>'s own tests), and pure timing (widening
    /// the retry window past ~6s changed nothing). NOT yet identified: what actually creates or
    /// links the clothing item, or why it differs from the weapon's own path. A host still sees
    /// the joiner's phantom carrying its weapon but wearing default/bare clothing until this is
    /// closed.
    ///
    /// Deliberately conservative: a slot that already holds an item is never displaced (this
    /// only ever fills an EMPTY slot), a candidate whose own Possessor field is not this exact
    /// creature is ignored (the window scan's own safety net), and a tag
    /// <see cref="StartingGearSlotRule"/> does not recognise is left exactly where it sat. Meant
    /// to be called every poll for a bounded window right before the quicksave
    /// <c>NewCharacterCaptureController</c> captures for carrying: a normal (already-established)
    /// character's own populated equip slots are never touched, because every candidate this
    /// loop finds already occupies its slot and the emptiness guard skips it.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE: the body/clothing gap above, and — beyond the one class
    /// package this round's own live rig exercised — a package that grants more than one weapon,
    /// a shield, or an off-hand item; see <see cref="StartingGearSlotRule"/>'s own doc comment
    /// for why the off hand is not attempted at all.
    /// </summary>
    public static int EquipUnequippedStartingGear(uint creatureObjectId)
    {
        nint creature = ResolveServerObject(creatureObjectId);
        if (creature == 0)
        {
            return 0;
        }

        nint inventory = *(nint*)(creature + 0xA2C);
        if (inventory == 0)
        {
            return 0;
        }

        HashSet<uint> candidates = [];

        CollectRepositoryCandidates(*(nint*)(creature + CreatureItemRepo), candidates);
        CollectRepositoryCandidates(ResolveCreatureItemRepo(creature), candidates);

        for (uint delta = 1; delta <= StartingGearIdWindow; delta++)
        {
            uint above = creatureObjectId + delta;
            if (above < 0x7F000000)
            {
                candidates.Add(above);
            }

            if (creatureObjectId > delta)
            {
                candidates.Add(creatureObjectId - delta);
            }
        }

        int equipped = 0;
        foreach (uint itemId in candidates)
        {
            nint item = ResolveItemBase(itemId);
            if (item == 0 || *(uint*)(item + ItemPossessor) != creatureObjectId)
            {
                // Not a live item, or a live item this creature does not actually possess (the
                // id-window scan's own candidates are frequently this — vetted, never trusted).
                continue;
            }

            int nwscriptSlot = StartingGearSlotRule.Classify(ReadTagUpperBound(item));
            if (nwscriptSlot < 0)
            {
                continue;
            }

            int internalSlot = EquipSlotMap.ToInternalSlot(nwscriptSlot);
            int slotByteOffset = EquipSlotMap.ToSlotByteOffset(nwscriptSlot);
            if (internalSlot < 0 || slotByteOffset < 0
                || *(uint*)(inventory + slotByteOffset) != 0)
            {
                // Already occupied (or an unrecognised slot) - never displace what is there.
                continue;
            }

            SetPossessorFn(item, creatureObjectId, 0, 0, ObjectInvalid);

            // Whichever repository (if any) this item is actually linked into — a safe no-op
            // for the ones the id-window scan found that are not linked into either (see
            // RepositoryRemoveItemFn's own doc comment: zero return, nothing touched, when the
            // item was never in the repository passed).
            nint personalRepo = *(nint*)(creature + CreatureItemRepo);
            if (personalRepo != 0)
            {
                RepositoryRemoveItemFn(personalRepo, item);
            }

            nint poolRepo = ResolveCreatureItemRepo(creature);
            if (poolRepo != 0 && poolRepo != personalRepo)
            {
                RepositoryRemoveItemFn(poolRepo, item);
            }

            if (EquipItemFn(creature, internalSlot, item, 1, 0, 0) == 0)
            {
                continue;
            }

            if (*(uint*)(inventory + slotByteOffset) != itemId)
            {
                continue;
            }

            equipped++;
            if (_startingGearEquipsLogged < 8)
            {
                _startingGearEquipsLogged++;
                PluginHost.Log(
                    $"newchar: equipped starting item 0x{itemId:X} into nwscript slot {nwscriptSlot} "
                    + "(was sitting unequipped in the fresh character's own inventory)");
            }
        }

        return equipped;
    }

    /// <summary>Adds every id a <c>CItemRepository</c>'s own array currently holds to
    /// <paramref name="candidates"/>. A no-op for a null or empty repository.</summary>
    private static void CollectRepositoryCandidates(nint repo, HashSet<uint> candidates)
    {
        if (repo == 0)
        {
            return;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint idsArray = *(nint*)(repo + RepositoryItemArray);
        if (idsArray == 0 || count is <= 0 or > 4096)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            candidates.Add(*(uint*)(idsArray + (nint)(i * 4)));
        }
    }

    /// <summary>The item's Tag, cut at the first null (the CExoString length trap this project
    /// has hit before — a reported length routinely includes the terminator), capped the same
    /// 64 bytes <see cref="TagEqualsIgnoreCase"/> already caps at.</summary>
    private static string ReadTagUpperBound(nint item)
    {
        nint text = *(nint*)(item + ItemTag);
        uint length = *(uint*)(item + ItemTag + 4);
        if (text == 0 || length == 0)
        {
            return string.Empty;
        }

        byte* tagBytes = (byte*)text;
        int cap = (int)Math.Min(length, 64u);
        int tagLength = 0;
        while (tagLength < cap && tagBytes[tagLength] != 0)
        {
            tagLength++;
        }

        return System.Text.Encoding.ASCII.GetString(tagBytes, tagLength);
    }
}
