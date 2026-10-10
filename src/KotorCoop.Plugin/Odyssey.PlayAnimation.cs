namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>NWScript <c>PlayAnimation(int nAnimation, float fSpeed, float fSeconds)</c>.</summary>
    private const int RoutinePlayAnimation = 300;

    /// <summary>
    /// <c>CSWVirtualMachineCommands::ExecuteCommandPlayAnimation</c>, the one handler behind both
    /// <c>ActionPlayAnimation</c> (40) and <c>PlayAnimation</c> (300). Read at 0x00540550: it pops
    /// an int and two floats, needs a caller object, turns the script's animation constant into
    /// an engine animation id through the jump table at 0x00540C44 (constants 0 to 0x78), and
    /// then, when the routine number it was called with is 300 (<c>cmp [esp+0x44],0x12C</c> at
    /// 0x00540B60), calls <c>CSWSObject::ClearAllActions(1)</c> (0x004CCD80) and
    /// <c>AddActionToFront</c> (0x004CCB00) with action 6. That is what makes it show on a
    /// creature in the middle of a fight: its attack is dropped and the animation goes first.
    /// Called with 40 it only appends, behind whatever the creature is doing. <c>ret 8</c>.
    /// </summary>
    private const nint ExpectedPlayAnimation = 0x00540550;

    /// <summary>
    /// Makes a creature play one of the script animation constants now, on the server side, the
    /// way a script's <c>PlayAnimation</c> does: whatever it was doing is dropped, the animation
    /// plays, and its own AI picks up again afterwards. <paramref name="seconds"/> is how long a
    /// looping animation is held; a one-shot ignores it.
    ///
    /// Failure: 1 no VM, 2 the routine table does not hold the handler read above, 3 a script is
    /// running, 4 a push failed, 5 the handler refused, 6 the stack came back uneven (the call
    /// still happened).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PlayAnimationNow(uint objectId, int scriptAnimation, float seconds, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0 || objectId == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0 || *(nint*)(table + (RoutinePlayAnimation * 4)) != ExpectedPlayAnimation)
        {
            failure = 2;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 3;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // OBJECT_SELF is the creature that plays it.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);
        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = objectId;

        try
        {
            // Last-declared first: fSeconds, fSpeed, nAnimation.
            if (StackPushFloat(vm, Math.Max(seconds, 0f)) == 0
                || StackPushFloat(vm, 1f) == 0
                || StackPushInteger(vm, scriptAnimation) == 0)
            {
                failure = 4;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedPlayAnimation;
            if (dispatch(implementer, RoutinePlayAnimation, 3) != 0)
            {
                failure = 5;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 6;
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
