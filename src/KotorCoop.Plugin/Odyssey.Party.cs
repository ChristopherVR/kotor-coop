// Odyssey.Party.cs - the companion roster and follow-stall recovery.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- the companion roster -------------------------------------------------------
    //
    // KOTOR keeps two separate facts about a companion, and conflating them is the first
    // mistake available here:
    //
    //   AVAILABLE — the story has handed this companion over. Party table +0x30, int[9];
    //     CSWPartyTable::AddNPC writes the literal 1 at 0x00564350. Every roster operation
    //     refuses a slot whose entry is 0.
    //   IN THE PARTY — one of the at most TWO companions currently walking behind the player.
    //     Party table +0x00 is the count (AddMember refuses at 2: `cmp dword ptr [esi],2 /
    //     je refuse`, 0x00565633) and +0x04 is int[2] of NPC SLOT INDICES — slot indices, not
    //     object ids, which is the trap docs/research/party-reconciliation.md records.
    //
    // The layout below is that document's CORRECTED map, not the symbol DB's: the DB's
    // CSWPartyTable offsets are shifted (it calls +0x04 an int[11] of "member ids"), and the
    // shipped PartyPresenceController has been reading +0x0C as the object-id array against a
    // live game since it was written. Everything here is re-derived from the binary anyway,
    // with the address of the instruction that proves each one.
    //
    // ⚠ EVERY WRITE GOES THROUGH THE ENGINE'S OWN NWSCRIPT ROUTINES. Poking +0x04 and +0x0C
    // directly would produce a party table that looks right and a game that is not: AddMember
    // alone touches the object's in-party flag, the follow state, the GUI portraits and the
    // client-side party list, and RemovePartyMember additionally clears solo mode. This is the
    // repo's standing rule (rebuild derived state through engine calls) and here it is not a
    // preference — there is no reachable list of what else would need rebuilding.

    // The party table itself is reached with the file's existing PartyTableOffset (0x1B770 off
    // CServerExoAppInternal), verified in the journal section from CServerExoApp::GetPartyTable
    // 0x004AEE70 — the same accessor every roster routine in the engine opens with.
    // PartyPresenceController keeps its own copy of these constants; that file is not this
    // change's to edit, and the two agree.

    /// <summary>CSWPartyTable + this = the count of companions in the active party (0..2).</summary>
    private const int PartyMemberCount = 0x00;

    /// <summary>CSWPartyTable + this = int[2] of NPC SLOT INDICES, one per active companion.
    /// AddMember writes `[esi + count*4 + 4] = nNPC` at 0x005656B5.</summary>
    private const int PartyMemberSlots = 0x04;

    /// <summary>CSWPartyTable + this = int[9] spawned server object id per NPC slot.
    /// AddMember writes `[esi + nNPC*4 + 0xC] = oCreature` at 0x005656B9.</summary>
    private const int PartyObjectIds = 0x0C;

    /// <summary>CSWPartyTable + this = int[9] availability. AddMember refuses a zero entry at
    /// 0x00565651, RemoveMember at 0x00565581, AddNPC sets it to 1 at 0x00564350.</summary>
    private const int PartyAvailable = 0x30;

    /// <summary>
    /// CSWPartyTable + this = the "a script is driving one specific party member" field, -1
    /// when nothing is.
    ///
    /// ⚠ THE FIRST THING BOTH ROSTER ROUTINES TEST. AddMember (0x00565625) and RemoveMember
    /// (0x00565565) each open with `cmp dword ptr [esi+0xF0], -1 / jne refuse`, before they
    /// look at anything else. Reconciling during one of those scenes is not dangerous, it is
    /// simply refused — so the caller checks it here and waits, rather than spending a VM
    /// dispatch per pass on a call the engine has already decided to reject.
    /// </summary>
    private const int PartyControlledNpc = 0xF0;

    /// <summary>Roster slots. The engine's bound: AddMember refuses nNPC &lt; 0 (0x00565640)
    /// or &gt;= 9 (0x00565648).</summary>
    public const int PartyRosterSlots = 9;

    /// <summary>Companions in the active party at once. AddMember refuses at 2 (0x00565633).</summary>
    public const int PartyMaxCompanions = 2;

    // CSWVirtualMachineCommands::ExecuteCommandAddPartyMember(nCommandId, nParams).
    //
    // int AddPartyMember(int nNPC, object oCreature). Read off the handler rather than off a
    // header: it pops an INTEGER (CVirtualMachine::StackPopInteger 0x005D1000) at 0x0052DE7E
    // and then an OBJECT (StackPopObject 0x005D10C0) at 0x0052DE9D, and hands them to
    // CSWPartyTable::AddMember(nNPC, oCreature) at 0x0052DEC4 in that order. A VM pop yields
    // arguments in DECLARATION order, so the pushes below go in reverse.
    //
    // ⚠ Declared as a literal-address cast, not via a named constant, so tools/callaudit.py's
    // scanner actually sees it: it matches `)0xADDR` and would silently skip a declaration
    // written the other way. The callee is `ret 8` — two stack arguments past `this` — which is
    // what this declaration passes.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandAddPartyMember =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x0052DE70;

    // CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember(nCommandId, nParams).
    //
    // int RemovePartyMember(int nNPC). One integer pop at 0x00541C0C, then
    // CSWPartyTable::GetNPCObject(nNPC, 0, 1) at 0x00541C3F and RemoveMember(nNPC) at
    // 0x00541C4D. Also `ret 8`.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandRemovePartyMember =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x00541C00;

    // CSWVirtualMachineCommands::ExecuteCommandSpawnAvailableNPC(nCommandId, nParams).
    //
    // object SpawnAvailableNPC(int nNPC, location lLoc). Integer pop at 0x00543EDE, then an
    // ENGINE STRUCTURE of type 2 (location) at 0x00543EF4 via StackPopEngineStructure
    // 0x005D10A0; calls CSWPartyTable::SpawnNPC at 0x00543F2A and pushes the resulting object
    // back with StackPushObject at 0x00543F36. Also `ret 8`.
    //
    // This is the engine's own "put a recruited companion into the world" entry point, and it
    // is here because AddPartyMember needs a live object: CSWPartyTable::AddMember validates
    // the id through CServerExoApp::GetGameObject (0x00565666) and refuses an unresolvable
    // one. The GUI does exactly this pair — CSWGuiPartySelection::AcceptParty calls SpawnNPC
    // at 0x006BE90C and AddMember at 0x006BEA25 — so the sequence below is the shipped one.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandSpawnAvailableNPC =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x00543ED0;

    private static int _routineAddPartyMember = -1;
    private static int _routineRemovePartyMember = -1;
    private static int _routineSpawnAvailableNpc = -1;

    /// <summary>The live CSWPartyTable, or 0 before a world exists (at the main menu, and
    /// through a module transition).</summary>
    private static nint GetPartyTable()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint serverInternal = Deref(server + AppServerOffsetInternal);
        return serverInternal == 0 ? 0 : serverInternal + PartyTableOffset;
    }

    /// <summary>
    /// Reads this instance's roster as two nine-bit masks. False when there is no party table
    /// to read, which is NOT an empty roster and must never be published as one.
    /// </summary>
    public static bool TryReadPartyRoster(out uint availableMask, out uint partyMask)
    {
        availableMask = 0;
        partyMask = 0;

        nint table = GetPartyTable();
        if (table == 0)
        {
            return false;
        }

        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            if (*(int*)(table + PartyAvailable + (slot * 4)) != 0)
            {
                availableMask |= 1u << slot;
            }
        }

        // The membership list is walked rather than indexed: it is a DENSE list of slot
        // indices (members[0..count-1]), not a per-slot flag, so entry i says nothing about
        // slot i. Reading it as a flag array is the same category of mistake as reading +0x04
        // as object ids.
        int count = *(int*)(table + PartyMemberCount);
        if (count is < 0 or > PartyMaxCompanions)
        {
            // A count outside the engine's own bound means this is not a party table (a
            // transition frame, a pointer that has moved). Refuse the whole reading rather
            // than publish a partial one.
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int slot = *(int*)(table + PartyMemberSlots + (i * 4));
            if (slot is >= 0 and < PartyRosterSlots)
            {
                partyMask |= 1u << slot;
            }
        }

        return true;
    }

    /// <summary>
    /// Raw-pokes this instance's own availability flag for one NPC slot — the write half of
    /// <see cref="TryReadPartyRoster"/>'s read.
    ///
    /// Mirrors the literal store <c>AddAvailableNPCByTemplate</c> performs at the same offset
    /// (<c>*(int*)(table + PartyAvailable + slot*4) = 1</c> at <c>0x00564350</c> — VERIFIED, the
    /// same evidence <see cref="PartyAvailable"/>'s own doc comment already cites:
    /// <c>AddMember</c>/<c>RemoveMember</c> testing this exact array for a zero entry). **The
    /// flag ALONE is NOT enough for <see cref="SpawnAvailableNpc"/> to resolve a template
    /// afterward — LIVE-CONFIRMED, not just NEEDS-LIVE**: docs/research/party-reconciliation.md
    /// Round 75's <c>CheatKindRecruit</c> dev primitive runs exactly this poke, then
    /// <see cref="SpawnAvailableNpc"/>, and the spawn refuses every time ("spawn failed, failure
    /// 0" — a genuine engine refusal, not a plumbing bug). Root cause, from this round's own
    /// disassembly of <c>CSWPartyTable::GetNPCObject</c> (0x00564700, <see cref="SpawnAvailableNpc"/>'s
    /// own resolver): when the cached object-id slot (<see cref="PartyObjectIds"/>) still holds
    /// its default sentinel (0x7F000000, OBJECT_INVALID), it rebuilds from a resource literally
    /// named <c>"AVAILNPC%d"</c> (the format string at 0x007490B4, read via
    /// <c>CSWPartyTable::GetFilename</c> 0x00563620) — a per-slot SAVE-GAME CACHE of that
    /// companion's off-stage character sheet, only ever populated as a side effect of a real
    /// <c>AddNPC_2</c>/<c>AddNPC</c> call (see <see cref="AddAvailableNpcByTemplate"/>), never by
    /// this raw poke. A slot this raw poke marks available therefore has no "AVAILNPC&lt;N&gt;"
    /// resource to rebuild from and <see cref="SpawnAvailableNpc"/> has nothing to spawn.
    /// <see cref="AddAvailableNpcByTemplate"/> is the fix used by the dialogue recruitment-sync
    /// path now (docs/research/dialogue-consequences.md §4.2, coop.ini
    /// <c>dialogue_recruit_sync</c>): it goes through the real STORY template resref instead
    /// (which does not depend on that cache at all), the same way the engine's own recruitment
    /// conversations do. This raw poke is kept only for
    /// <see cref="CheatController.Recruit"/>'s dev-only test primitive, where the documented
    /// engine refusal — and its own tag-lookup fallback against an already-placed body — is the
    /// point.
    /// </summary>
    public static bool TrySetSlotAvailable(int slot)
    {
        nint table = GetPartyTable();
        if (table == 0 || slot is < 0 or >= PartyRosterSlots)
        {
            return false;
        }

        *(int*)(table + PartyAvailable + (slot * 4)) = 1;
        return true;
    }

    // CSWPartyTable::AddNPC_2(int nNPC, CExoString* sTemplate) -> object oCreature. VERIFIED by
    // disassembly this round: CSWVirtualMachineCommands::ExecuteCommandAddAvailableNPCByTemplate
    // (0x0052DCD0, the NWScript AddAvailableNPCByTemplate handler) pops an int then a CExoString
    // (StackPopInteger 0x005D1000, StackPopString 0x005D1080), resolves the party table via
    // CServerExoApp::GetPartyTable (0x004AEE70), and calls exactly this function
    // (0x0052DD65 -> 0x005645F0) with (nNPC, &sTemplate). Internally: refuses if the slot is
    // already available (same +0x30 guard TrySetSlotAvailable's own poke sets); otherwise
    // allocates sizeof(CSWSCreature)=0xAC8 (Global::operator_new_2 0x006FA7E6), constructs it
    // (CSWSCreature::Constructor-equivalent 0x004F7A10), builds a CResRef from the template
    // string (0x00406D60) and loads the blueprint via CSWSCreature::LoadFromTemplate (0x005026D0
    // — the SAME load path docs/re/spawn.md's CreateObject trace uses, so any resref confirmed
    // in templates.bif there works here), then calls CSWPartyTable::AddNPC (0x00564300) on the
    // fresh object: sets the +0x30 flag AND runs the derived bookkeeping the raw poke skips —
    // CSWSFaction::AddMember (0x005BFA70, faction membership; low-risk in practice since
    // docs/re/spawn.md's own parse of a companion .utc (p_carth) already shows
    // FactionID=2=Friendly_1 baked into the blueprint) and CSWPartyTable::TransferInventory
    // (0x005641E0, merges starting inventory into the shared party pool — also run a second time
    // by AddMember itself when the caller adds the body to the active party, so redundant, not
    // missing, if this function is skipped). Returns the new object id, or 0/OBJECT_INVALID on
    // refusal (bad slot, already available, or the template failed to resolve).
    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte*, uint> AddNpcByTemplateFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte*, uint>)0x005645F0;

    /// <summary>
    /// Recruits NPC slot <paramref name="slot"/> the way the engine's own
    /// <c>AddAvailableNPCByTemplate</c> does — <see cref="AddNpcByTemplateFn"/>'s own doc comment
    /// has exactly what this buys over <see cref="TrySetSlotAvailable"/>'s raw poke it replaces
    /// in <c>PartyRosterController</c> (that poke is LIVE-CONFIRMED insufficient — see
    /// <see cref="TrySetSlotAvailable"/>'s own doc comment). Used by the dialogue
    /// recruitment-sync path (docs/research/dialogue-consequences.md §4.2, coop.ini
    /// <c>dialogue_recruit_sync</c>).
    ///
    /// Builds the CExoString with the engine's own constructor
    /// (<see cref="CExoStringCtorFn"/>/<see cref="CExoStringDtorFn"/>, the same live-verified
    /// pattern <see cref="SetControlText"/> and <see cref="PlayBark"/> already use) rather than
    /// hand-laying the struct out, for the same reason those two do: getting the length
    /// convention right by hand is a standing hazard this codebase avoids everywhere else.
    ///
    /// Returns the new object id, or 0/<see cref="ObjectInvalid"/> if the slot was out of range,
    /// already available, or the template failed to resolve. MAIN THREAD ONLY.
    ///
    /// ⚠ THE RETURNED OBJECT IS OFF-STAGE, NOT A LIVE IN-WORLD BODY. <c>AddNpcByTemplateFn</c>'s
    /// own doc comment traces the call chain (<c>AddNPC_2</c> -&gt; <c>AddNPC</c>) and it never
    /// calls <c>CSWSCreature::AddToArea</c> — unlike <c>CreateObject</c>'s own trace
    /// (docs/re/spawn.md §3.6 step 9), which does. NWScript's own <c>AddAvailableNPCByTemplate</c>
    /// is declared <c>void</c> for exactly this reason: no script is meant to treat this id as an
    /// addable body either. Do NOT pass this id to <see cref="AddPartyMemberBySlot"/> — it will
    /// refuse with failure 9 forever, not just this pass (see
    /// <see cref="PartyRosterRecruitRule"/>'s doc comment for the disassembled reason: the object
    /// never gains the in-world flag bit <c>AddMember</c>'s own typed live-body check requires).
    /// Only <see cref="SpawnAvailableNpc"/> — which reads the off-stage cache this call populates
    /// — produces a body that check will accept.
    ///
    /// The slot -&gt; resref mapping itself lives in <see cref="PartyRosterTemplateTable"/> (split
    /// out so it can be unit-tested) — see that class's own doc comment for the 2026-08-24 round
    /// that independently re-derived and confirmed all nine entries by disassembly, closing the
    /// "not independently re-derived... for the other eight" gap this comment used to carry.
    /// </summary>
    public static uint AddAvailableNpcByTemplate(int slot)
    {
        nint table = GetPartyTable();
        if (table == 0 || !PartyRosterTemplateTable.TryGetTemplate(slot, out string resref))
        {
            return 0;
        }

        byte* exoString = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            exoString[i] = 0;
        }

        byte* templateName = stackalloc byte[32];
        int count = resref.Length < 31 ? resref.Length : 31;
        for (int i = 0; i < count; i++)
        {
            templateName[i] = (byte)resref[i];
        }

        templateName[count] = 0;

        CExoStringCtorFn(exoString, templateName);
        uint objectId = AddNpcByTemplateFn(table, slot, exoString);
        CExoStringDtorFn(exoString);
        return objectId;
    }

    /// <summary>This instance's spawned object id for an NPC slot, or 0 / OBJECT_INVALID when
    /// that companion has no body in the world right now.</summary>
    public static uint GetPartyRosterObjectId(int slot)
    {
        nint table = GetPartyTable();
        return table == 0 || slot is < 0 or >= PartyRosterSlots
            ? 0
            : *(uint*)(table + PartyObjectIds + (slot * 4));
    }

    /// <summary>
    /// True while a script owns one specific party member and the engine will refuse every
    /// roster change (party table +0xF0 != -1; tested first by both AddMember and RemoveMember).
    /// Also true when there is no party table at all, because "cannot change the roster" is the
    /// answer in both cases.
    /// </summary>
    public static bool IsPartyRosterFrozen()
    {
        nint table = GetPartyTable();
        return table == 0 || *(int*)(table + PartyControlledNpc) != -1;
    }

    /// <summary>
    /// Dispatches a zero-return NWScript roster routine, with the same guard structure as
    /// <see cref="SpawnCreature"/>: the routine index is found by VALUE in the live routine
    /// table (never by a guessed nwscript number), the VM must not be mid-script, and the whole
    /// sequence must be stack-neutral.
    ///
    /// Resolves everything and hands the pieces back; the caller pushes its own arguments and
    /// dispatches, because the argument count is part of the call and passing a wrong nParams
    /// makes the handler pop a stack that is not there.
    /// </summary>
    private static bool DispatchRosterRoutine(
        delegate* unmanaged[Thiscall]<nint, int, int, int> handler,
        ref int cachedRoutine,
        out nint vm,
        out nint internalVm,
        out int stackPointer,
        out nint implementer,
        out int failure)
    {
        vm = 0;
        internalVm = 0;
        stackPointer = 0;
        failure = 0;

        implementer = GetImplementer(out vm, out internalVm);
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

        if (cachedRoutine < 0)
        {
            cachedRoutine = FindRoutine(table, (nint)handler);
        }

        if (cachedRoutine < 0)
        {
            failure = 3;
            return false;
        }

        // ⚠ Never dispatch while a script is executing — the same guard SpawnCreature carries,
        // and it matters more here: a recruitment conversation's own script is EXACTLY what is
        // running when the roster changes, and re-entering the VM underneath it would corrupt
        // the stack it is mid-way through using.
        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        stackPointer = *(int*)(internalVm + StackPointerOffset);
        return true;
    }

    /// <summary>
    /// Puts NPC slot <paramref name="slot"/> into the active party through the engine's own
    /// AddPartyMember(int nNPC, object oCreature).
    ///
    /// <paramref name="objectId"/> must be a LIVE creature: AddMember resolves it through
    /// CServerExoApp::GetGameObject at 0x00565666 and refuses an id that does not resolve. Use
    /// <see cref="SpawnAvailableNpc"/> first when the slot has no body.
    ///
    /// MAIN THREAD ONLY. Failure codes: 1 no VM, 2 no routine table, 3 routine not found,
    /// 4 a script is running, 5 a push was refused, 6 the handler refused, 7 the return could
    /// not be popped, 8 the stack was left skewed, 9 the engine returned "not added".
    /// </summary>
    public static bool AddPartyMemberBySlot(int slot, uint objectId, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots || objectId is 0 or ObjectInvalid)
        {
            failure = 10;
            return false;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandAddPartyMember, ref _routineAddPartyMember,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return false;
        }

        // Push in REVERSE declaration order: AddPartyMember(int nNPC, object oCreature), so
        // the object goes down first and the slot index on top. Confirmed against the handler,
        // which pops the integer BEFORE the object (0x0052DE7E then 0x0052DE9D).
        if (StackPushObject(vm, objectId) == 0 || StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return false;
        }

        if (ExecuteCommandAddPartyMember(implementer, _routineAddPartyMember, 2) != 0)
        {
            failure = 6;
            return false;
        }

        // The routine returns int, and the handler pushes it back onto the VM stack. It MUST
        // be popped even when it says "refused", or the next dispatch inherits a stack with an
        // extra word on it — which is how a channel that merely does not work becomes a
        // channel that corrupts the next script to run.
        int added = 0;
        if (StackPopInteger(vm, &added) == 0)
        {
            failure = 7;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
            return false;
        }

        if (added == 0)
        {
            failure = 9;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes NPC slot <paramref name="slot"/> out of the active party through the engine's own
    /// RemovePartyMember(int nNPC). The companion stays RECRUITED — this is the party seat, not
    /// the roster entry.
    ///
    /// MAIN THREAD ONLY. Same failure codes as <see cref="AddPartyMemberBySlot"/>.
    /// </summary>
    public static bool RemovePartyMemberBySlot(int slot, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots)
        {
            failure = 10;
            return false;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandRemovePartyMember, ref _routineRemovePartyMember,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return false;
        }

        if (StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return false;
        }

        if (ExecuteCommandRemovePartyMember(implementer, _routineRemovePartyMember, 1) != 0)
        {
            failure = 6;
            return false;
        }

        int removed = 0;
        if (StackPopInteger(vm, &removed) == 0)
        {
            failure = 7;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
            return false;
        }

        if (removed == 0)
        {
            failure = 9;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts a recruited companion's body into the world at <paramref name="position"/> through
    /// the engine's own SpawnAvailableNPC(int nNPC, location lLoc), and returns its object id
    /// (or <see cref="ObjectInvalid"/>).
    ///
    /// Only needed when a slot is available here but has no body — the case a client reaches
    /// when it learns about a recruitment it did not itself perform. The body is placed at the
    /// caller's chosen point and then driven to wherever the owner has it by the companion
    /// mirror on the next pass, so the position is a starting point, not a claim.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint SpawnAvailableNpc(int slot, Vec3 position, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots)
        {
            failure = 10;
            return ObjectInvalid;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandSpawnAvailableNPC, ref _routineSpawnAvailableNpc,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return ObjectInvalid;
        }

        ScriptLocation location = new()
        {
            Position = position,
            Orientation = new Vec3 { X = 1f, Y = 0f, Z = 0f },
        };

        // Reverse declaration order again: SpawnAvailableNPC(int nNPC, location lLoc).
        if (StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0
            || StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        if (ExecuteCommandSpawnAvailableNPC(implementer, _routineSpawnAvailableNpc, 2) != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint objectId = ObjectInvalid;
        if (StackPopObject(vm, &objectId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return objectId;
    }

}
