namespace KotorCoop.Plugin;

/// <summary>Captured arguments for one delayed open. A stale response cannot reopen a
/// different click or survive world invalidation, and an accepted response is consumed once.</summary>
internal sealed class DeferredContainerOpen
{
    private uint _target, _serial, _actor;
    private int _feedback;

    public void Capture(uint target, uint serial, uint actor, int feedback)
    {
        _target = target;
        _serial = serial;
        _actor = actor;
        _feedback = feedback;
    }

    public bool TryTake(uint target, uint serial, out uint actor, out int feedback)
    {
        actor = 0;
        feedback = 0;
        if (target == 0 || serial == 0 || target != _target || serial != _serial)
            return false;
        actor = _actor;
        feedback = _feedback;
        Clear();
        return true;
    }

    public void Clear() { _target = _serial = _actor = 0; _feedback = 0; }
}
