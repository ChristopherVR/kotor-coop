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

    /// <summary>How many isolated creations <see cref="EquipItemFromTemplateDirect"/> has
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

    // CSWSItem::RemoveFromArea(this), GOG 0x5538A0: no stack arguments, bare ret.
    // Detaches the fresh floor item from the area's list and clears its area/position.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> EquipmentItemRemoveFromAreaFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005538A0;

    /// <summary>
    /// Creates an isolated item and synchronously equips it, verifying the native slot.
    /// CreateItemOnObject on a party companion merges into the shared party inventory:
    /// live 2026-10-01, a player's clothes stack of three became the companion's stack
    /// of four when the merged result was unlinked. CreateItemOnFloor bypasses that
    /// repository. The item is immediately detached through the engine's RemoveFromArea
    /// and assigned to its wearer before any frame returns; no AcquireItem or pool merge
    /// occurs. Missing blueprints are rejected before equipping their empty native body.
    /// MAIN THREAD ONLY.
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

        nint creature = ResolveServerObject(creatureObjectId);
        if (creature == 0 || !NativeMethods.IsReadable(creature, 0xA30)
            || !PartyCreatureIdentity.IsLiveMatch(*(nint*)creature, *(uint*)(creature + 4), creatureObjectId))
        {
            failure = 22;
            return false;
        }

        Vec3 position = GetPosition(creature);
        if (!Vec3.IsWorldPlausible(position))
        {
            failure = 22;
            return false;
        }
        uint itemId = SpawnItemOnFloor(resrefAscii, resrefLength, position, out int spawnFailure);
        if (itemId is 0 or ObjectInvalid)
        {
            failure = spawnFailure is > 0 and < 8 ? spawnFailure : 21;
            return false;
        }
        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            failure = 23;
            return false;
        }
        EquipmentItemRemoveFromAreaFn(item);
        if (!TryReadItemIdentityFields(itemId, out ItemIdentityFields created) || created.LooksUnloaded)
        {
            DestroyObjectById(itemId, out _);
            failure = EquipRetryRule.BlueprintMissing;
            return false;
        }
        SetPossessorFn(item, creatureObjectId, 0, 0, ObjectInvalid);
        if (_equipUnlinksLogged < 8)
        {
            _equipUnlinksLogged++;
            PluginHost.Log($"equip: isolated item 0x{itemId:X} stack {created.StackSize} for 0x{creatureObjectId:X}; party repository untouched");
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

    /// <summary>Which creature <see cref="DiagnoseCandidates"/> is currently reporting on — a
    /// second chargen in the same process (a second join inside one test session) gets its own
    /// clean dump rather than being silently swallowed by the first one's already-logged set.</summary>
    private static uint _diagnosticCreatureId;

    /// <summary>Candidate ids already dumped for <see cref="_diagnosticCreatureId"/>, so the
    /// verbose per-candidate line below fires once per NEW id discovered across the whole retry
    /// window (<c>PostAdoptEquipRepairController.MaxPolls</c>) instead of once per poll for the
    /// whole set every time.</summary>
    private static readonly HashSet<uint> _diagnosedCandidateIds = [];

    /// <summary>Hard cap on <see cref="DiagnoseCandidates"/>'s own log lines, the same bounded-log
    /// convention every other logger in this file already follows.</summary>
    private static int _diagnosticLinesLogged;

    /// <summary>How many <see cref="DiagnosePollSummary"/> lines have been logged for
    /// <see cref="_diagnosticCreatureId"/> — separate budget from
    /// <see cref="_diagnosticLinesLogged"/> since this one is meant to fire on EVERY poll (to show
    /// the repository-population TIMELINE across the retry window, which the once-per-id dedup in
    /// <see cref="DiagnoseCandidates"/> cannot show), not just once per new id.</summary>
    private static int _diagnosticPollsLogged;

    private static long _diagnosticFirstPollMs;

    /// <summary>
    /// DIAGNOSTIC ONLY — logs one line per poll (bounded) giving the repository-population
    /// TIMELINE the id-dedup'd <see cref="DiagnoseCandidates"/> log cannot show: how many ids each
    /// of the personal backpack and party-pool repositories held, and the elapsed time since the
    /// first poll for this creature, every single call. Exists to answer a question
    /// <see cref="DiagnoseCandidates"/> alone leaves open: whether the starting items simply have
    /// not been created/linked YET at a given poll (repo counts climb over the window) or whether
    /// the repositories never hold them at all during the whole retry window (counts stay flat),
    /// which "did this id ever appear" cannot distinguish once a poll's candidate set repeats.
    /// </summary>
    private static void DiagnosePollSummary(uint creatureObjectId, nint personalRepo, nint poolRepo, int candidateCount)
    {
        if (creatureObjectId != _diagnosticCreatureId)
        {
            // DiagnoseCandidates resets its own state for a new creature above; this mirrors it
            // for the poll-summary budget so a second chargen in one process gets a fresh timeline.
            _diagnosticPollsLogged = 0;
            _diagnosticFirstPollMs = 0;
        }

        if (_diagnosticPollsLogged >= 80)
        {
            return;
        }

        long nowMs = Environment.TickCount64;
        if (_diagnosticFirstPollMs == 0)
        {
            _diagnosticFirstPollMs = nowMs;
        }

        int personalCount = personalRepo != 0 ? *(int*)(personalRepo + RepositoryItemCount) : -1;
        int poolCount = poolRepo != 0 ? *(int*)(poolRepo + RepositoryItemCount) : -1;

        _diagnosticPollsLogged++;
        PluginHost.Log(
            $"newchar-diag: poll {_diagnosticPollsLogged} at +{nowMs - _diagnosticFirstPollMs}ms - "
            + $"personal repo 0x{personalRepo:X} count={personalCount}, pool repo 0x{poolRepo:X} "
            + $"count={poolCount}, total distinct candidates so far={candidateCount}");
    }

    /// <summary>
    /// DIAGNOSTIC ONLY — never touches an item, never equips anything, exists purely to answer
    /// docs/research/world-adoption.md's open question: does the starting CLOTHES item's id ever
    /// show up among <see cref="EquipUnequippedStartingGear"/>'s own candidates at all, or does
    /// the search itself never find it? Logs every NEW candidate id this call's three sources
    /// produced (bounded, once per id per creature): whether it resolves to a live
    /// <c>CSWSItem</c> at all (and <see cref="LastItemResolveNote"/> if not), its Tag, its
    /// Possessor, and what <see cref="StartingGearSlotRule"/> would classify it as. Cross-
    /// reference against the `items` console verb's independent memory-scan-based listing (run on
    /// the AGENT side) taken at roughly the same moment: an id this logs as "did not resolve"
    /// but that `items` reports as live and possessed by this exact creature means the plugin's
    /// own id-indexed lookup (<c>GetGameObjectFn</c>/<see cref="ResolveItemBase"/>) and the
    /// agent's committed-memory vtable sweep disagree about the SAME object — the one distinction
    /// nothing before this round could make. An id that never appears in this log at all, ever,
    /// across the whole retry window, while `items` reports it the whole time, means none of the
    /// three candidate SOURCES ever produce it — a search-coverage gap rather than a resolve or
    /// classify failure.
    /// </summary>
    private static void DiagnoseCandidates(uint creatureObjectId, HashSet<uint> candidates)
    {
        if (creatureObjectId != _diagnosticCreatureId)
        {
            _diagnosticCreatureId = creatureObjectId;
            _diagnosedCandidateIds.Clear();
            _diagnosticLinesLogged = 0;
        }

        foreach (uint candidateId in candidates)
        {
            if (_diagnosticLinesLogged >= 64 || !_diagnosedCandidateIds.Add(candidateId))
            {
                continue;
            }

            _diagnosticLinesLogged++;

            nint item = ResolveItemBase(candidateId);
            if (item == 0)
            {
                PluginHost.Log(
                    $"newchar-diag: candidate 0x{candidateId:X} did NOT resolve "
                    + $"({LastItemResolveNote})");
                continue;
            }

            uint possessor = *(uint*)(item + ItemPossessor);
            string tag = ReadTagUpperBound(item);
            int classified = StartingGearSlotRule.Classify(tag);
            PluginHost.Log(
                $"newchar-diag: candidate 0x{candidateId:X} tag='{tag}' possessor=0x{possessor:X} "
                + $"(creature is 0x{creatureObjectId:X}) classify={classified}");
        }
    }

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
    /// (<c>ActionEquipItem</c>), which is exactly the mechanism that leaves these items
    /// unequipped in the first place: <c>ActionEquipItem</c>'s VM dispatch only APPENDS a node
    /// onto the creature's own combat-round pending list (see <see cref="EquipItemFn"/>'s own
    /// doc comment) and nothing in this project's `join_mode = newcharacter` path ever drains it.
    ///
    /// Candidates come from THREE places: the creature's own personal backpack
    /// (<c>CreatureItemRepo</c>, +0xA30), the repository <see cref="ResolveCreatureItemRepo"/>'s
    /// engine accessor returns (the SHARED PARTY POOL for a live party member — the local player
    /// always is one, per <see cref="GiveItemToInventory"/>'s own doc comment), and a bounded
    /// id-window scan (<see cref="StartingGearIdWindow"/>) around the creature's own id for a
    /// straggler linked into neither.
    ///
    /// ⚠ CALLER MATTERS MORE THAN THIS METHOD DOES — ROUND 4, 2026-09-10e. This method's own
    /// logic has not changed since round 1 and was never the bug. What changed is WHEN it gets
    /// called: <c>PostAdoptEquipRepairController</c> now calls it once, after a first-time
    /// joiner's adopted-world load finishes (a genuine, ordinary engine module load), instead of
    /// <c>NewCharacterCaptureController</c> calling it repeatedly, for up to 150 seconds, INSIDE
    /// the isolated single-player chargen module <c>CharGenDriveController</c> replays. Round 4
    /// LIVE-CONFIRMED both repository sources here read <c>count=0</c> for the WHOLE original
    /// chargen-module window regardless of the id or how long the wait — chargen's own granted
    /// items are simply never linked into either repository while still inside that isolated
    /// session — and separately confirmed <see cref="Odyssey.GetPlayerObjectId"/>'s
    /// <c>0x7FFFFFFF</c> return (what <paramref name="creatureObjectId"/> always is for the local
    /// player, including post-adopt) is the engine's PERMANENT, correctly-resolving player self-id,
    /// not an unassigned placeholder — items genuinely possessed by the player get their own
    /// <c>Possessor</c> stamped with that SAME value, so the exact-match check below was always
    /// correct; it just never had a populated repository to search inside the old caller's window.
    /// See <c>PostAdoptEquipRepairController</c>'s own doc comment for the full mechanism and
    /// docs/research/world-adoption.md's "Round 2026-09-10e" for the live evidence trail.
    ///
    /// LIVE-CONFIRMED 2026-09-10e, four independent joins against four different `quick`
    /// chargen classes (indices 0, 1, 2, 4): in every run the starting WEAPON
    /// (`g_w_blstrrfl001`, classify 4) was found through the party-pool repository (post-adopt,
    /// populated) and equipped correctly (readback-verified) — 4/4. NO run, in any of the four
    /// classes tested, ever produced a clothes-prefixed (`g_a_`) candidate from any of the three
    /// sources — only the same three items every time (an adrenaline stim, a medpac, and the
    /// weapon), across classes that should differ. The working theory, not yet exhaustively
    /// confirmed across all six quick-chargen portraits: KOTOR's premade "Quick Character"
    /// packages do not grant a separate wearable clothes item at all — the class's default
    /// appearance already looks dressed without one — which would mean the original "clothes"
    /// half of this bug report was never a missing-equip bug to begin with. See
    /// world-adoption-clothes-notes.md for the short version.
    ///
    /// Deliberately conservative: a slot that already holds an item is never displaced (this
    /// only ever fills an EMPTY slot), a candidate whose own Possessor field is not this exact
    /// creature is ignored (the window scan's own safety net), and a tag
    /// <see cref="StartingGearSlotRule"/> does not recognise is left exactly where it sat. A
    /// normal (already-established) character's own populated equip slots are never touched,
    /// because every candidate this loop finds already occupies its slot and the emptiness guard
    /// skips it — safe to call after ANY module load, not just a first-time joiner's.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE: beyond the four class packages round 4's own live rig
    /// exercised, a package that grants more than one weapon, a shield, an off-hand item, or
    /// (per the theory above) genuinely does grant a separate clothes item; see
    /// <see cref="StartingGearSlotRule"/>'s own doc comment for why the off hand is not attempted
    /// at all.
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

        nint personalRepoForDiag = *(nint*)(creature + CreatureItemRepo);
        nint poolRepoForDiag = ResolveCreatureItemRepo(creature);

        CollectRepositoryCandidates(personalRepoForDiag, candidates);
        CollectRepositoryCandidates(poolRepoForDiag, candidates);

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

        DiagnoseCandidates(creatureObjectId, candidates);
        DiagnosePollSummary(creatureObjectId, personalRepoForDiag, poolRepoForDiag, candidates.Count);

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
