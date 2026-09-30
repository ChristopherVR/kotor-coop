namespace KotorCoop.Session;

/// <summary>Bounded native capture-serial replay window. Kept across reconnects and world loads;
/// authenticated process incarnation alone starts a new run. Aged-out captures cannot resurrect.</summary>
public sealed class EnemyEffectReplayWindow
{
    private sealed class Run(uint incarnation)
    {
        public uint Incarnation = incarnation, High;
        public readonly HashSet<uint> Seen = [];
    }
    private readonly Dictionary<uint, Run> _runs = [];
    public void Retain(IReadOnlySet<uint> peers)
    {
        foreach (uint peer in _runs.Keys.Where(peer => !peers.Contains(peer)).ToArray()) _runs.Remove(peer);
    }
    public bool Observe(uint peer, uint incarnation, uint serial)
    {
        const uint mask = 0xFFFFFF, half = 0x800000, width = 512;
        if (peer == 0 || incarnation == 0 || serial == 0 || serial > mask) return false;
        if (!_runs.TryGetValue(peer, out Run? run) || run.Incarnation != incarnation)
            _runs[peer] = run = new Run(incarnation);
        if (run.High == 0) run.High = serial;
        uint ahead = (serial - run.High) & mask;
        if (ahead != 0 && ahead < half)
        {
            run.High = serial;
            run.Seen.RemoveWhere(value => ((serial - value) & mask) >= width);
        }
        else if (((run.High - serial) & mask) >= width) return false;
        return run.Seen.Add(serial);
    }
}
