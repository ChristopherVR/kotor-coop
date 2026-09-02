// Odyssey.DoorCross.cs - the door-crossing event primitive: builds and posts the SAME
// CScriptEvent (type 0x1E) the engine's own movement code posts when a creature crosses into a
// door's sub-area, WITHOUT a walk. See CheatController.Doors.cs's CrossDoor and
// docs/research/cinematics.md's 2026-09-03 addendum for the full story.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- door crossing: CSWSCreature::AIActionMoveToPoint's own event construction --------
    //
    // usedoor (CheatController.Doors.cs) established that a KOTOR module transition is fired
    // by event 0x1E on the door, built ONLY by movement code, and walks the player through the
    // door so the engine's own movement update builds and posts that event. That walk has
    // stalled 1.8-5.9 m short of the door on every live attempt across three separate rounds
    // (docs/research/cinematics.md §8.3b), so the "a real crossing reaches the transition arm"
    // half of the warp-crash fix has never actually been observed. This primitive removes the
    // pathfinder from the equation entirely: it posts the identical event, built EXACTLY the
    // way AIActionMoveToPoint builds it, straight onto the AI master's own queue.
    //
    // Fresh disassembly against the live GOG 1.0.3 exe (`python tools/disasm.py 0x005200A0 300`,
    // re-run for this file rather than trusted from an earlier pass), starting from the
    // `[door+0x2CC] != 0` (door already open) branch AIActionMoveToPoint takes once the walk
    // has actually crossed the threshold:
    //
    //   0x005200C9  mov al, [ecx+0x2CC]          ; the door's own committed open state
    //   0x005200CF  test al, al
    //   0x005200D1  push 0x34                    ; sizeof(CScriptEvent) - shared by both branches
    //   0x005200D3  jne 0x52013e                 ; nonzero (open) -> the 0x1E branch below
    //   0x0052013E  call 0x6FA7E6                ; operator new(0x34), CDECL, caller pops (add esp,4)
    //   0x0052015B  call 0x4D7540                ; CScriptEvent::CScriptEvent(this) - thiscall, no args, bare ret
    //   0x00520166  mov word ptr [edi], 0x1E     ; event TYPE = the door-crossing event
    //   0x0052016E  push creatureId              ; [esi+4], the standard CSWSObject-derived id-at-+4 convention
    //   0x0052016F  push 0                       ; index
    //   0x00520171  mov ecx, edi                 ; ecx = the event
    //   0x0052017E  call 0x4D7780                ; CScriptEvent::SetObjectID(index=0, objectId) - thiscall, ret 8
    //   0x00520183  mov edx, [0x7A39FC]          ; AppManagerPtr - the same global BarkHook,
    //   0x00520189  mov eax, [0x7441DC]          ;   DialogueSkipHook, PauseSanityPatch and a
    //   0x0052018E  mov ecx, [edx+8]             ;   dozen other detours in this plugin already
    //                                            ;   read; +8 = the CServerExoApp; eax = the LIVE event id
    //   0x00520195  push edi                     ; data = the event
    //   0x00520196  push eax                     ; eventId = [0x7441DC], read fresh, never hardcoded
    //   0x00520197  mov eax, [esi+4]             ; reload creatureId (eax was clobbered by the eventId read)
    //   0x0052019A  push edx                     ; targetId = the DOOR's object id
    //   0x0052019B  push eax                     ; sourceId = the CREATURE's object id
    //   0x0052019C  push 0                       ; time
    //   0x0052019E  push 0                       ; days
    //   0x005201A0  call 0x4AED80                ; CServerExoApp::GetServerAIMaster(this=ecx) - thiscall,
    //                                            ;   no stack args, bare ret -> eax = CServerAIMaster*
    //   0x005201A5  mov ecx, eax
    //   0x005201A7  call 0x4B08D0                ; CServerAIMaster::AddEventDeltaTime(this, days, time,
    //                                            ;   sourceId, targetId, eventId, data) - thiscall, ret 0x18
    //                                            ;   (6 stack dwords - VERIFIED both by this disasm's own
    //                                            ;   push count and independently, at this exact address,
    //                                            ;   by docs/research/creature-liveness.md §... and
    //                                            ;   docs/research/combat-participation.md's unrelated
    //                                            ;   delayed-event read, which agrees on the same shape)
    //
    // The pushed-argument ORDER (data, eventId, targetId, sourceId, time, days) is the mirror
    // image of AddEventDeltaTime's own declared parameter order (days, time, sourceId,
    // targetId, eventId, data) - standard right-to-left stack-argument construction, confirmed
    // by matching each push against what the callee's own `ret 0x18` (6 x 4 bytes) consumes.
    //
    // The AI master OWNS the allocation from the moment AddEventDeltaTime returns: it frees it
    // later through its own DeleteEventData (0x4B0AB0, docs/research/creature-liveness.md),
    // which is why the allocation MUST come from the engine's own operator new (0x6FA7E6)
    // rather than anything a managed/.NET allocator would produce - the engine's delete would
    // corrupt a foreign heap block.
    //
    // ⚠ The retail code path does NOT guard the allocation failing: at 0x520164, a null
    // constructor result flows straight into `mov word ptr [edi], 0x1E` at 0x520166 with
    // edi == 0, an unconditional null-pointer write. PostDoorCrossingEvent refuses instead.

    private const int DoorCrossEventSize = 0x34;
    private const ushort DoorCrossEventType = 0x1E;

    // AppManagerPtr/AppServerOffset are already declared in Odyssey.Core.cs (this same partial
    // class) - reused here rather than redeclared, the same global/offset pair BarkHook,
    // DialogueSkipHook, PauseSanityPatch, RemovePartyMemberHook and Odyssey.Combat.ScriptFire
    // already read for other purposes.

    // The live event id CServerAIMaster::AddEventDeltaTime is called with for this exact event
    // family. Read FRESH at call time, never hardcoded: docs/research/combat-participation.md
    // independently observed this global holding 10 for an unrelated call site, but this
    // primitive has no reason to assume that value is fixed and every reason to ask the engine
    // for whatever it is actually using right now.
    private const nint DoorCrossEventIdGlobal = 0x7441DC;

    private static readonly delegate* unmanaged[Cdecl]<nuint, nint> DoorCrossOperatorNewFn =
        (delegate* unmanaged[Cdecl]<nuint, nint>)0x006FA7E6;

    /// <summary>
    /// CScriptEvent::CScriptEvent 0x004D7540 — <c>__thiscall</c>, no arguments beyond
    /// <c>this</c> (the fresh allocation), bare <c>ret</c>. The same constructor
    /// AIActionMoveToPoint's own event build calls (0x0052015B, this file's own header
    /// disassembly).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ScriptEventCtorFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004D7540;

    /// <summary>
    /// CScriptEvent::SetObjectID 0x004D7780 — <c>__thiscall</c>, two stack args (<c>int
    /// index</c>, <c>uint objectId</c>), <c>ret 8</c>. AIActionMoveToPoint calls this with
    /// index 0 and the crossing creature's own object id (0x0052016E-0x0052017E).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, uint, void> ScriptEventSetObjectIdFn =
        (delegate* unmanaged[Thiscall]<nint, int, uint, void>)0x004D7780;

    /// <summary>
    /// CServerExoApp::GetServerAIMaster 0x004AED80 — <c>__thiscall</c> on the CServerExoApp
    /// resolved through <c>AppManagerPtr</c>+<c>AppServerOffset</c> (Odyssey.Core.cs), no
    /// stack arguments, bare <c>ret</c>. Returns the CServerAIMaster* every delayed/scripted
    /// event in this engine is ultimately queued through.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetServerAIMasterFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AED80;

    /// <summary>
    /// CServerAIMaster::AddEventDeltaTime 0x004B08D0 — <c>__thiscall</c>, six stack args
    /// (<c>int days</c>, <c>int time</c>, <c>uint sourceId</c>, <c>uint targetId</c>,
    /// <c>int eventId</c>, <c>void* data</c>), <c>ret 0x18</c>.
    ///
    /// VERIFIED against fresh disasm of AIActionMoveToPoint's own call site (0x005201A7, this
    /// file's own header comment) AND already independently documented at this same address
    /// for a different delayed-event path in docs/research/creature-liveness.md and
    /// docs/research/combat-participation.md — two unrelated call sites agreeing on the same
    /// six-argument, ret-0x18 shape.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, uint, uint, int, nint, void> AddEventDeltaTimeFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, uint, uint, int, nint, void>)0x004B08D0;

    /// <summary>
    /// Posts the exact door-crossing event (type <c>0x1E</c>) a real walk THROUGH the door
    /// would post once the movement update notices the crossing — see this file's own header
    /// for the full disassembly this replicates. <c>CheatController.Doors.CrossDoor</c> calls
    /// this instead of queuing a walk, so the door's own transition arm
    /// (<c>CSWSDoor::EventHandler</c> 0x0058B850, the arm at 0x0058C0BE) can be exercised
    /// without depending on the pathfinder ever actually reaching the threshold — the exact
    /// dependency that has stalled a real crossing 1.8-5.9 m short on every live <c>usedoor</c>
    /// attempt to date.
    ///
    /// Refuses (returns false with <paramref name="detail"/> set) rather than posting a
    /// half-built event: when the engine's app-manager global or the resolved AI master are
    /// null, or when the engine's own allocator itself returns null — a failure the retail code
    /// path does NOT guard (see this file's own header, the note on 0x520164/0x520166).
    ///
    /// MAIN THREAD ONLY. Fires-and-forgets, matching the real code exactly: the AI master's
    /// own per-tick drain runs the event and frees it through <c>DeleteEventData</c>
    /// (0x4B0AB0, docs/research/creature-liveness.md) — nothing here owns the event's lifetime
    /// past this call returning.
    /// </summary>
    public static bool PostDoorCrossingEvent(uint creatureId, uint doorId, out string detail)
    {
        nint appManager = Deref(AppManagerPtr);
        if (!LooksLikeHeapPointer(appManager))
        {
            detail = "the engine's app-manager global is not resolvable right now";
            return false;
        }

        nint server = Deref(appManager + AppServerOffset);
        if (!LooksLikeHeapPointer(server))
        {
            detail = "the engine's CServerExoApp is not resolvable right now";
            return false;
        }

        nint aiMaster = GetServerAIMasterFn(server);
        if (aiMaster == 0)
        {
            detail = "GetServerAIMaster returned null - no AI master to queue the event on";
            return false;
        }

        nint eventObj = DoorCrossOperatorNewFn((nuint)DoorCrossEventSize);
        if (eventObj == 0)
        {
            detail = "the engine's own allocator refused the CScriptEvent (out of memory?)";
            return false;
        }

        ScriptEventCtorFn(eventObj);
        *(ushort*)eventObj = DoorCrossEventType;
        ScriptEventSetObjectIdFn(eventObj, 0, creatureId);

        int eventId = *(int*)DoorCrossEventIdGlobal;
        AddEventDeltaTimeFn(aiMaster, 0, 0, creatureId, doorId, eventId, eventObj);

        detail = $"posted event 0x{DoorCrossEventType:X} (id {eventId}) from 0x{creatureId:X} to door 0x{doorId:X}";
        return true;
    }
}
