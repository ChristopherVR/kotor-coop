namespace KotorCoop.Session;

/// <summary>Bind a process incarnation at admission, then order its native generation
/// counter with uint serial arithmetic. Neither an old run nor an unseen older generation
/// can reopen a damage baseline. An incarnation is opaque; only Hello may replace it.</summary>
public sealed class DamageGenerationWatch
{
    private sealed class PeerState(uint incarnation)
    {
        public uint Incarnation = incarnation;
        public uint Generation;
        public bool HasGeneration;
        public bool AwaitFreshGeneration;
        public readonly HashSet<uint> RetiredIncarnations = [];
    }
    private readonly Dictionary<uint, PeerState> _peers = [];

    public bool Bind(uint peer, uint incarnation)
    {
        if (!_peers.TryGetValue(peer, out PeerState? state))
        {
            _peers[peer] = new(incarnation);
            return true;
        }
        if (state.Incarnation == incarnation) return true;
        if (state.RetiredIncarnations.Contains(incarnation)) return false;
        state.RetiredIncarnations.Add(state.Incarnation);
        state.Incarnation = incarnation;
        state.HasGeneration = false;
        state.AwaitFreshGeneration = false;
        return true;
    }

    public bool Observe(uint peer, uint incarnation, uint generation)
    {
        if (!_peers.TryGetValue(peer, out PeerState? state) || state.Incarnation != incarnation) return false;
        if (state.AwaitFreshGeneration && state.HasGeneration && generation == state.Generation) return false;
        if (state.HasGeneration && generation != state.Generation
            && unchecked((int)(generation - state.Generation)) <= 0) return false;
        state.Generation = generation;
        state.HasGeneration = true;
        state.AwaitFreshGeneration = false;
        return true;
    }

    public bool IsBound(uint peer, uint incarnation) =>
        _peers.TryGetValue(peer, out PeerState? state) && state.Incarnation == incarnation;

    /// <summary>World turnover retires each sender run even if its last report was removed
    /// on disconnect or aged out. Re-Hello of that same process cannot reopen it.</summary>
    public void RetireCurrentWorld()
    {
        foreach (PeerState state in _peers.Values) state.AwaitFreshGeneration = state.HasGeneration;
    }

    public void Clear() => _peers.Clear();
}
