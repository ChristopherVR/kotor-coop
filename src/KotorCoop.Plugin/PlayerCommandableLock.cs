namespace KotorCoop.Plugin;

/// <summary>Main-thread adapter that applies the pure lock ownership transitions to the
/// current local player creature. SaveFlush holds its frame while this state is suspended,
/// so the pointer remains in the same world until the store finishes.</summary>
internal static unsafe class PlayerCommandableLock
{
    private static readonly PlayerCommandableLockState State = new();
    private static nint _creature;
    private static uint _objectId;

    public static bool IsOwned => State.IsOwned;

    public static void Acquire(PlayerCommandableLockOwner owner, nint creature)
    {
        if (creature == 0)
        {
            return;
        }

        // A respawn can replace the local creature without changing the module. Forget the
        // old pointer without touching it; each still-active controller will reacquire the new
        // local body on its next pass.
        if (_creature != 0 && _creature != creature)
        {
            ForgetAll();
        }

        if (_creature == 0)
        {
            uint objectId = *(uint*)(creature + 0x04);
            if (objectId is 0 or Odyssey.ObjectInvalid || Odyssey.ResolveServerObject(objectId) != creature)
            {
                return;
            }

            _creature = creature;
            _objectId = objectId;
        }

        int current = *(int*)(creature + Odyssey.ObjCommandable);
        if (State.Acquire(owner, current, out int valueToWrite))
        {
            *(int*)(creature + Odyssey.ObjCommandable) = valueToWrite;
        }
    }

    public static void Release(PlayerCommandableLockOwner owner)
    {
        if (_creature == 0)
        {
            State.Forget(owner);
            return;
        }

        if (!TryGetCurrentLockedCreature(out nint live))
        {
            ForgetAll();
            return;
        }

        if (State.Release(owner, out int valueToWrite))
        {
            *(int*)(live + Odyssey.ObjCommandable) = valueToWrite;
        }

        if (!State.IsOwned)
        {
            ForgetAll();
        }
    }

    public static void Forget(PlayerCommandableLockOwner owner)
    {
        State.Forget(owner);
        if (!State.IsOwned)
        {
            _creature = 0;
            _objectId = 0;
        }
    }

    public static bool SuspendForSave()
    {
        if (!TryGetCurrentLockedCreature(out nint live) || !State.SuspendForSave(out int valueToWrite))
        {
            return false;
        }

        *(int*)(live + Odyssey.ObjCommandable) = valueToWrite;
        return true;
    }

    public static void ResumeAfterSave()
    {
        if (!State.IsSuspendedForSave)
        {
            return;
        }

        if (!TryGetCurrentLockedCreature(out nint live))
        {
            ForgetAll();
            return;
        }

        if (State.ResumeAfterSave(out int valueToWrite))
        {
            *(int*)(live + Odyssey.ObjCommandable) = valueToWrite;
        }

        if (!State.IsOwned)
        {
            ForgetAll();
        }
    }

    private static bool TryGetCurrentLockedCreature(out nint live)
    {
        live = 0;
        if (_creature == 0)
        {
            return false;
        }

        nint current = Odyssey.GetPlayerCreaturePointer();
        if (current == 0 || current != _creature || _objectId is 0 or Odyssey.ObjectInvalid)
        {
            return false;
        }

        uint currentId = *(uint*)(current + 0x04);
        if (currentId != _objectId || Odyssey.ResolveServerObject(_objectId) != current)
        {
            return false;
        }

        live = current;
        return true;
    }

    private static void ForgetAll()
    {
        State.Forget(PlayerCommandableLockOwner.SoloSegment);
        State.Forget(PlayerCommandableLockOwner.Minigame);
        _creature = 0;
        _objectId = 0;
    }
}
