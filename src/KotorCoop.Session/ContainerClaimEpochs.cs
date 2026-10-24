namespace KotorCoop.Session;

/// <summary>Associates a real close with the request that opened that container. Callers
/// hold their session gate. A cached release keeps its captured epoch even when a later
/// request is sent, so delayed UDP levels cannot release the later reservation.</summary>
internal sealed class ContainerClaimEpochs
{
    private readonly Dictionary<(string Module, uint TargetId), uint> _requests = [];

    public void Record(string module, uint targetId, uint requestSerial) =>
        _requests[((module ?? string.Empty).ToLowerInvariant(), targetId)] = requestSerial;

    public uint ForRelease(string module, uint targetId) =>
        _requests.GetValueOrDefault(((module ?? string.Empty).ToLowerInvariant(), targetId));
}
