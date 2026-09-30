// Odyssey.Party.cs - the companion roster: CSWPartyTable field offsets, reading the
// availability/membership masks, and the four NWScript roster routines (AddPartyMember,
// RemovePartyMember, RemoveAvailableNPC, SpawnAvailableNPC) dispatched the same way the
// engine's own scripts do.
// Also the raw availability poke used only by the dev recruit cheat, and the template-based
// recruit path used by real dialogue-driven recruitment sync.
// See docs/research/party-reconciliation.md for the full investigation history.

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

    // CSWVirtualMachineCommands::ExecuteCommandRemoveAvailableNPC(nCommandId, nParams).
    //
    // int RemoveAvailableNPC(int nNPC). The shipped handler at 0x00541B40 pops one integer,
    // resolves the party table, calls CSWPartyTable::RemoveNPC at 0x005646D0, and pushes that
    // bool result back on the VM stack. The handler is `ret 8`, matching the other one-argument
    // roster commands above. RemoveNPC only clears CSWPartyTable +0x30 (the availability bit);
    // it does not remove an active party seat, so reconciliation always removes a seat first.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandRemoveAvailableNPC =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x00541B40;

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
    private static int _routineRemoveAvailableNpc = -1;
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
    /// Whether this instance's own party table marks NPC slot <paramref name="slot"/> as
    /// recruited (<c>table+0x30+slot*4</c>, the flag <see cref="AddAvailableNpcByTemplate"/>
    /// sets). False when there is no table or the slot is out of range.
    ///
    /// ⚠ There is deliberately NO raw write for this flag any more. Until 2026-09-03 a
    /// <c>TrySetSlotAvailable</c> poke existed for the <c>recruit</c> dev verb and for
    /// <c>PartyRosterController</c>'s GIT-body reuse, on the theory that "all AddMember needs is
    /// the +0x30 gate". docs/research/transition-crashes.md (2026-09-03, "the two-companion host
    /// warp crash") shows why that is not enough: <c>CSWPartyTable::CreateParty</c> rebuilds every
    /// party member after a module move from the per-slot <c>AVAILNPC&lt;slot&gt;</c> resource in
    /// <c>GAMEINPROGRESS:</c>, and a member whose slot was only ever flag-poked came back as
    /// OBJECT_INVALID from <c>GetNPCObject</c> after the move, so <c>PositionParty</c> called
    /// <c>CSWSObject::SetPosition</c> on NULL — a reliable access violation on the first module
    /// move with that companion in the party. The only way to mark a slot recruited is the
    /// engine's own <c>AddNPC</c>, which writes that cache itself.
    /// </summary>
    public static bool IsSlotAvailable(int slot)
    {
        nint table = GetPartyTable();
        if (table == 0 || slot is < 0 or >= PartyRosterSlots)
        {
            return false;
        }

        return *(int*)(table + PartyAvailable + (slot * 4)) != 0;
    }

    // CSWPartyTable::AddNPC_2(int nNPC, CExoString* sTemplate) -> object oCreature. VERIFIED by
    // disassembly: CSWVirtualMachineCommands::ExecuteCommandAddAvailableNPCByTemplate
    // (0x0052DCD0, the NWScript AddAvailableNPCByTemplate handler) pops an int then a CExoString
    // (StackPopInteger 0x005D1000, StackPopString 0x005D1080), resolves the party table via
    // CServerExoApp::GetPartyTable (0x004AEE70), and calls exactly this function
    // (0x0052DD65 -> 0x005645F0) with (nNPC, &sTemplate). Internally: refuses if the slot is
    // already available (the +0x30 guard IsSlotAvailable reads); otherwise
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
    // missing, if this function is skipped) — and, last, GetFilename ("AVAILNPC%d") +
    // CResGFF::CreateGFFFile / CSWSCreature::SaveCreature / CResGFF::WriteGFFFile
    // (0x00564502-0x00564590): the companion's character sheet is written to
    // GAMEINPROGRESS:AVAILNPC<slot>.utc RIGHT THERE. AddNPC returns that WriteGFFFile result
    // (esi -> eax at 0x005645B7), and AddNPC_2 then DELETES its temporary creature (vtable slot 0
    // with flag 1 at 0x005646B2) and returns AddNPC's value — so the function returns a success
    // flag (live logs show "0x1"), NOT an object id, and leaves no engine object behind. Its
    // durable products are the +0x30 flag, the faction membership, the inventory transfer, and
    // the AVAILNPC<slot> cache file CreateParty rebuilds the member from after every module move.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte*, uint> AddNpcByTemplateFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte*, uint>)0x005645F0;

    /// <summary>
    /// Recruits NPC slot <paramref name="slot"/> the way the engine's own
    /// <c>AddAvailableNPCByTemplate</c> does — <see cref="AddNpcByTemplateFn"/>'s own doc comment
    /// has the disassembly. This is the ONLY way this plugin marks a slot recruited (see
    /// <see cref="IsSlotAvailable"/> for why the raw flag poke that used to sit beside it was
    /// removed). Used by the dialogue recruitment-sync path
    /// (docs/research/dialogue-consequences.md §4.2, coop.ini <c>dialogue_recruit_sync</c>) and
    /// by the <c>recruit</c> dev verb.
    ///
    /// Builds the CExoString with the engine's own constructor
    /// (<see cref="CExoStringCtorFn"/>/<see cref="CExoStringDtorFn"/>, the same live-verified
    /// pattern <see cref="SetControlText"/> and <see cref="PlayBark"/> already use) rather than
    /// hand-laying the struct out, for the same reason those two do: getting the length
    /// convention right by hand is a standing hazard this codebase avoids everywhere else.
    ///
    /// Returns true when the engine recruited the slot (flag set, faction joined, starting
    /// inventory transferred, <c>AVAILNPC&lt;slot&gt;.utc</c> written); false if the slot was out
    /// of range, ALREADY recruited, or the template failed to resolve. MAIN THREAD ONLY.
    ///
    /// ⚠ THIS PRODUCES NO BODY. AddNPC_2 deletes the creature it built from the template as soon
    /// as AddNPC has saved it. (Until 2026-09-03 this doc claimed an off-stage object was left
    /// behind and that the returned value was its id — it is AddNPC's success flag; the "failure
    /// 9 forever" the old text described was <see cref="AddPartyMemberBySlot"/> being handed the
    /// number 1 as an object id.) To put the companion in the world afterwards, either
    /// <see cref="SpawnAvailableNpc"/> (which rebuilds a body from the cache this call just
    /// wrote) or hand <see cref="AddPartyMemberBySlot"/> a body that is already standing in the
    /// module (the companion's own GIT-placed creature — what the game's own recruit scripts do).
    ///
    /// The slot -&gt; resref mapping itself lives in <see cref="PartyRosterTemplateTable"/> (split
    /// out so it can be unit-tested); all nine entries are independently re-derived and confirmed
    /// by disassembly there.
    /// </summary>
    public static bool AddAvailableNpcByTemplate(int slot)
    {
        RecruitmentFailureReason = "the engine refused the template";
        nint table = GetPartyTable();
        byte* moduleName = stackalloc byte[32];
        int moduleLength = ReadCurrentModuleName(moduleName, 32);
        string module = moduleLength > 0
            ? System.Text.Encoding.ASCII.GetString(moduleName, moduleLength) : string.Empty;
        if (table == 0 || !RecruitmentProfileValid
            || (RecruitmentProfile.Id.Length > 0 && moduleLength <= 0))
        {
            RecruitmentFailureReason = table == 0 ? "the party table is unavailable"
                : !RecruitmentProfileValid ? "the mod compatibility profile is invalid"
                : "the module identity is unavailable";
            return false;
        }
        if (!PartyRosterTemplateTable.TryGetTemplate(slot, module, RecruitmentProfile, out string resref, RecruitmentNumberReader))
        {
            RecruitmentFailureReason = "the mod profile holds recovery until the companion identity is known";
            return false;
        }

        return AddAvailableNpcByVerifiedTemplate(slot, resref);
    }

    /// <summary>Uses an exact observed successful recruitment template, never a guessed tag.</summary>
    public static bool AddAvailableNpcByVerifiedTemplate(int slot, string resref)
    {
        nint table = GetPartyTable();
        if (table == 0 || slot is < 0 or >= 9 || resref.Length is < 1 or > 16
            || !PartyOccupantTable.ValidTemplate(System.Text.Encoding.ASCII.GetBytes(resref))) return false;
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
        uint recruited = AddNpcByTemplateFn(table, slot, exoString);
        CExoStringDtorFn(exoString);
        return recruited != 0;
    }

    public static string RecruitmentFailureReason { get; private set; } = string.Empty;

    private static KotorCoop.Protocol.ModSupportProfile RecruitmentProfile =
        KotorCoop.Protocol.ModSupportProfile.Empty;
    private static bool RecruitmentProfileValid = true;
    private static readonly Func<string, int?> RecruitmentNumberReader = ReadRecruitmentNumber;

    public static void LoadRecruitmentProfile(string directory)
    {
        RecruitmentProfileValid = KotorCoop.Protocol.ModSupportProfile.TryLoad(
            directory, out RecruitmentProfile, out string error);
        if (!RecruitmentProfileValid)
            PluginHost.Log($"mod support: {error}; automatic template recruitment held");
        else if (RecruitmentProfile.Id.Length > 0)
            PluginHost.Log($"mod support: recruitment profile '{RecruitmentProfile.Id}' loaded");
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

    /// <summary>Whether a script has the PLAYER under a companion right now (<c>CSWPartyTable+0xF0</c>
    /// is not -1). Unlike <see cref="IsPartyRosterFrozen"/> this is false, not true, when there is no
    /// party table — a missing table must never read as "a segment began".</summary>
    public static bool IsPlayerUnderCompanionControl()
    {
        nint table = GetPartyTable();
        return table != 0 && *(int*)(table + PartyControlledNpc) != -1;
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
    /// Clears NPC slot <paramref name="slot"/>'s recruited/available flag through the engine's
    /// own <c>RemoveAvailableNPC(int nNPC)</c> VM command. This is the availability operation
    /// used by the game's scripts; it is deliberately separate from
    /// <see cref="RemovePartyMemberBySlot"/>, which only vacates an active party seat.
    ///
    /// The engine implementation is <c>CSWPartyTable::RemoveNPC</c> at <c>0x005646D0</c> and
    /// only clears <c>CSWPartyTable + 0x30 + slot*4</c>. It does not destroy or detach a live
    /// body, so callers must remove an active seat first when both bits differ.
    /// MAIN THREAD ONLY. Failure codes use the same dispatch/stack contract as the other roster
    /// wrappers: 1 no VM, 2 no routine table, 3 routine not found, 4 a script is running,
    /// 5 a push was refused, 6 the handler refused, 7 the return could not be popped,
    /// 8 the stack was left skewed, 9 the engine returned "not removed", 10 bad slot.
    /// </summary>
    public static bool RemoveAvailableNpc(int slot, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots)
        {
            failure = 10;
            return false;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandRemoveAvailableNPC, ref _routineRemoveAvailableNpc,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return false;
        }

        if (StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return false;
        }

        if (ExecuteCommandRemoveAvailableNPC(implementer, _routineRemoveAvailableNpc, 1) != 0)
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
