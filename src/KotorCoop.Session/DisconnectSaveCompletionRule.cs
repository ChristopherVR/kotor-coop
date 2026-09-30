namespace KotorCoop.Session;

/// <summary>A posted save or readable partial files cannot certify disconnect capture.</summary>
public sealed class DisconnectSaveCompletionRule
{
    private string? _hash;
    private long _stableSince;

    public bool Observe(bool acknowledged, bool completed, bool currentWorld, bool ready,
        string? outputsHash, long nowMs)
    {
        if (!acknowledged || !completed || !currentWorld || !ready || string.IsNullOrEmpty(outputsHash))
        {
            _hash = null;
            _stableSince = 0;
            return false;
        }
        if (_hash != outputsHash)
        {
            _hash = outputsHash;
            _stableSince = nowMs;
            return false;
        }
        return nowMs - _stableSince >= 750;
    }
}
