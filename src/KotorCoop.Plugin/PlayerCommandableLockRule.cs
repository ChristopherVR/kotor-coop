namespace KotorCoop.Plugin;

[Flags]
internal enum PlayerCommandableLockOwner
{
    None = 0,
    SoloSegment = 1,
    Minigame = 2,
}

/// <summary>
/// Pure ownership bookkeeping for the two plugin input locks that temporarily clear the
/// local PC's commandable flag. The first owner records the pre-lock value; overlapping
/// owners share that original value, so the first release cannot undo the other lock.
/// </summary>
internal sealed class PlayerCommandableLockState
{
    private int _owners;
    private int _originalValue;
    private bool _suspendedForSave;

    public bool IsOwned => _owners != 0;

    public bool IsSuspendedForSave => _suspendedForSave;

    /// <summary>Acquires one owner. The caller supplies the current value only for the first
    /// owner; later owners cannot replace the original snapshot.</summary>
    public bool Acquire(PlayerCommandableLockOwner owner, int currentValue, out int valueToWrite)
    {
        int bit = (int)owner;
        if (bit == 0 || (bit & (bit - 1)) != 0)
        {
            valueToWrite = currentValue;
            return false;
        }

        if (_owners == 0)
        {
            _originalValue = currentValue;
            _suspendedForSave = false;
        }

        _owners |= bit;
        valueToWrite = 0;
        return !_suspendedForSave;
    }

    /// <summary>Releases one owner. Only the last owner restores the captured original value.
    /// While a save has temporarily restored that value, release leaves it in place.</summary>
    public bool Release(PlayerCommandableLockOwner owner, out int valueToWrite)
    {
        int bit = (int)owner;
        if (bit == 0 || (_owners & bit) == 0)
        {
            valueToWrite = 0;
            return false;
        }

        _owners &= ~bit;
        if (_owners != 0)
        {
            valueToWrite = 0;
            return false;
        }

        if (_suspendedForSave)
        {
            Clear();
            valueToWrite = 0;
            return false;
        }

        valueToWrite = _originalValue;
        Clear();
        return true;
    }

    /// <summary>Temporarily exposes the pre-lock commandable value for serialization.</summary>
    public bool SuspendForSave(out int valueToWrite)
    {
        if (_owners == 0 || _suspendedForSave)
        {
            valueToWrite = 0;
            return false;
        }

        _suspendedForSave = true;
        valueToWrite = _originalValue;
        return true;
    }

    /// <summary>After the save store window, reasserts the lock if at least one owner still
    /// holds it. If all owners released during the window, it leaves the original value.</summary>
    public bool ResumeAfterSave(out int valueToWrite)
    {
        if (!_suspendedForSave)
        {
            valueToWrite = 0;
            return false;
        }

        _suspendedForSave = false;
        if (_owners != 0)
        {
            valueToWrite = 0;
            return true;
        }

        valueToWrite = _originalValue;
        Clear();
        return true;
    }

    /// <summary>Forgets a lock whose world-owned creature is being torn down. No native write
    /// is made because the saved object pointer may already be stale.</summary>
    public void Forget(PlayerCommandableLockOwner owner)
    {
        _owners &= ~(int)owner;
        if (_owners == 0)
        {
            Clear();
        }
    }

    private void Clear()
    {
        _owners = 0;
        _originalValue = 0;
        _suspendedForSave = false;
    }
}
