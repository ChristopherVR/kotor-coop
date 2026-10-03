namespace KotorCoop.Plugin;

/// <summary>Accepted engine deletion requests can remain serializable until a later tick.</summary>
internal sealed class PendingSpawnDeletion
{
    private readonly Dictionary<uint, nint> _bodies = [];
    public void Track(uint id, nint body) => _bodies[id] = body;
    public void Clear() => _bodies.Clear();
    public int CountStanding(Func<uint, nint> resolve)
    {
        if (_bodies.Count == 0) return 0;
        foreach ((uint id, nint body) in _bodies.ToArray())
            if (resolve(id) != body) _bodies.Remove(id);
        return _bodies.Count;
    }
}
