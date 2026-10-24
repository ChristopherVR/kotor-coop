namespace KotorCoop.Plugin;

/// <summary>A close belongs to the allowed open attempt it ended, not a later deferred
/// reopen. Native close and defer hooks can both fire before the next controller frame.</summary>
internal sealed class ContainerCloseRelease
{
    public uint TargetId { get; private set; }
    private uint _supersededTarget;
    private uint _supersededAttempt;

    public void BeginDeferred(uint targetId, uint lastAllowedAttempt)
    {
        _supersededTarget = targetId;
        _supersededAttempt = lastAllowedAttempt;
        if (TargetId == targetId) TargetId = 0;
    }

    public void ObserveClose(uint targetId, uint allowedAttempt)
    {
        if (targetId == _supersededTarget && allowedAttempt == _supersededAttempt) return;
        TargetId = targetId;
    }

    public void ClearRelease() => TargetId = 0;

    public void Reset()
    {
        TargetId = 0;
        _supersededTarget = 0;
        _supersededAttempt = 0;
    }
}
