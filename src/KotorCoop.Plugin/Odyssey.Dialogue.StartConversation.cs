// Odyssey.Dialogue.StartConversation.cs - the `startdialog` dev verb: drives
// ActionStartConversation (NWScript routine 204) through the SAME VM routine-table dispatch
// Odyssey.ActionQueue.cs's MoveToLocation already established for ActionMoveToLocation
// (routine 21), instead of hand-crafting CSWSObject::StartDialog's own three-argument call.
//
// Why NOT call CSWSObject::StartDialog (0x004CF490) directly, the way Odyssey.Store.cs's
// OpenStore calls ShowStoreGui directly: fresh disasm this pass of StartDialog's one caller,
// CSWVirtualMachineCommands::ExecuteCommandBeginConversation (0x0052E9F0), found its 2nd
// argument is a POINTER TO A CALLER-OWNED CExoString (the "private conversation" override,
// tested empty/non-empty and, if empty, replaced with a value read off the SPEAKER through a
// VIRTUAL CALL at vtable+0x80) and its 3rd argument is read off ANOTHER resolved participant's
// own +0x180 field earlier in BeginConversation's body — neither is a bare id or a value this
// plugin can safely fabricate without risking exactly the kind of mis-modelled-struct crash
// docs/research/store-sync.md's 2026-08-28 "Live pass" entry hit for the store panel. Going
// through the VM's own routine table instead reuses primitives ALREADY VERIFIED LIVE
// (MoveToLocation's own StackPush*/dispatch shape) and lets the ENGINE build every one of those
// structs itself, exactly as it would for a real script's ActionStartConversation call -
// the only difference is where the values on the VM's own parameter stack came from.
//
// See docs/research/dialogue-sync.md's 2026-08-28 addendum for the full trace (BeginConversation
// disasm, the StartDialog-args investigation this design deliberately steps around, and why the
// LOCAL PLAYER — not the NPC — must be the queuing/OBJECT_SELF object).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>ActionStartConversation. Table id 204, handler
    /// <c>CSWVirtualMachineCommands::ExecuteCommandActionStartConversation</c> 0x0052D5B0 —
    /// VERIFIED by docs/research/star-forge-finale.md §1 (the <c>InitializeCommands</c>
    /// routine-table scan, cross-checked against the `[ebx+0x330]` store its own handler makes,
    /// and against two independently-known ids from the same scan). The routine-table match is
    /// re-checked live by <see cref="StartConversation"/> itself before every dispatch, exactly
    /// like <see cref="MoveToLocation"/> already does for routine 21 — a stale/wrong id here
    /// refuses instead of calling through to whatever else now lives at table[204].</summary>
    private const int RoutineActionStartConversation = 204;

    private const nint ExpectedActionStartConversation = 0x0052D5B0;

    /// <summary>
    /// Queues <c>ActionStartConversation(object oObject, string sDialogResRef)</c> on
    /// <paramref name="playerObjectId"/> (the redirected caller/OBJECT_SELF — see
    /// <see cref="MoveToLocation"/>'s own identical redirect), naming
    /// <paramref name="npcObjectId"/> as the argument object. This is deliberately the PLAYER
    /// as actor, not the NPC: docs/research/dialogue-sync.md's own "Implementation" section
    /// (round tapping <c>AIActionDialogObject</c>) documents "a real click queues it on the PC"
    /// as the established engine convention, and <c>DialogueSpectateController.PublishLocal</c>
    /// detects a NEW local conversation off the LOCAL PLAYER's own <c>obj+0x34</c> going
    /// non-null — queuing on the NPC instead would leave the player's own field untouched and
    /// the existing spectate/lock machinery would never see it.
    ///
    /// <paramref name="dialogResref"/> empty pushes ONLY the mandatory object argument
    /// (<c>argCount=1</c>) — NWScript's own optional <c>sDialogResRef</c> semantics, so
    /// <c>StartDialog</c> falls back to whatever the NPC's own template <c>Conversation</c>
    /// field names. Non-empty pushes it as the 2nd declared parameter (<c>argCount=2</c>,
    /// pushed FIRST per the reverse-declaration-order convention <see cref="MoveToLocation"/>'s
    /// own comment documents), overriding it — exactly a bare
    /// <c>ActionStartConversation(oObject, sDialogResRef)</c> call from a script.
    ///
    /// The action is QUEUED, not run inline (same as <see cref="MoveToLocation"/>): it executes
    /// once the player's own action queue reaches it, in practice the very next tick if nothing
    /// is queued ahead of it. Runs through the REAL, unmodified
    /// action-queue -> <c>AIActionDialogObject</c> -> <c>BeginConversation</c> ->
    /// <c>CSWSObject::StartDialog</c> chain, so every existing dialogue hook
    /// (<c>DialogueGateHook</c>, <c>DialogStateWatchHook</c>) observes it exactly as it would a
    /// real click or a scripted trigger.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool StartConversation(
        uint playerObjectId, uint npcObjectId, string dialogResref, out int failure,
        bool ignoreStartRange = false)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0
            || *(nint*)(table + (RoutineActionStartConversation * 4)) != ExpectedActionStartConversation)
        {
            failure = 2;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // Redirect OBJECT_SELF to the player, then put it back exactly as it was — the same
        // dance MoveToLocation already does.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = playerObjectId;

        try
        {
            int argCount;

            if (dialogResref.Length == 0 && !ignoreStartRange)
            {
                // ActionStartConversation(object oObject) - the one mandatory argument.
                if (StackPushObject(vm, npcObjectId) == 0)
                {
                    failure = 5;
                    return false;
                }

                argCount = 1;
            }
            else
            {
                byte* ascii = stackalloc byte[32];
                int length = 0;
                foreach (char c in dialogResref)
                {
                    if (length == 31)
                    {
                        break;
                    }

                    ascii[length++] = (byte)c;
                }

                ascii[length] = 0;
                ExoString resref = new() { CString = (nint)ascii, Length = (uint)length };

                // ActionStartConversation(object oObject, string sDialogResRef) - pushed in
                // reverse declaration order: sDialogResRef (2nd declared) first, oObject last.
                // nwscript.nss: object, resref, private, cinematic type, ignore range.
                // Reverse order, as with all VM routine dispatches. Forced scenes must
                // bypass the engine's walk/range gate, just as the owner's script does.
                if ((ignoreStartRange && (StackPushInteger(vm, 1) == 0
                        || StackPushInteger(vm, 0) == 0 || StackPushInteger(vm, 0) == 0))
                    || StackPushString(vm, &resref) == 0 || StackPushObject(vm, npcObjectId) == 0)
                {
                    failure = 5;
                    return false;
                }

                argCount = ignoreStartRange ? 5 : 2;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionStartConversation;
            int status = dispatch(implementer, RoutineActionStartConversation, argCount);

            if (status != 0)
            {
                failure = 6;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 9;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }
}
