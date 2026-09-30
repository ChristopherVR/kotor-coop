namespace KotorCoop.Plugin;

/// <summary>Each peer spends its accepted damage once, independently of slot order or body id.
/// Body ids may change when a player reconnects; the session peer identity remains stable.</summary>
internal sealed class AttributedDamageOwedLedger(int enemyCapacity)
{
    private readonly Dictionary<uint, DamageOwedLedger> _peers = [];
    public uint Epoch { get; private set; }
    public bool AdoptEpoch(uint epoch)
    {
        if (Epoch == epoch) return false;
        Epoch = epoch;
        Clear();
        return true;
    }
    public void Clear() => _peers.Clear();
    private DamageOwedLedger For(uint peer)
    {
        if (!_peers.TryGetValue(peer, out var ledger))
            _peers[peer] = ledger = new DamageOwedLedger(enemyCapacity);
        return ledger;
    }
    public int Pending(uint peer, uint enemy, uint owed) => peer == 0 ? 0 : For(peer).Pending(enemy, owed);
    public void Commit(uint peer, uint enemy, int amount) => For(peer).Commit(enemy, amount);
}

internal readonly record struct AttributedDamageRow(uint Peer, uint Enemy, uint Total);
