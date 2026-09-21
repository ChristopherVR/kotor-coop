using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Per-module full history plus a bounded, fairly delivered game window.</summary>
public sealed class ContainerLootStore
{
    private const int MaxRememberedOps = 4096;
    private const int MaxWindow = 512;

    private sealed class ModuleLedger
    {
        public readonly HashSet<ContainerLootRemoval> Known = [];
        public readonly List<ContainerLootRemoval> History = [];
        public readonly List<ContainerLootRemoval> Window = [];
        public readonly HashSet<(uint Origin, uint Sequence)> AppliedOps = [];
        public readonly Queue<(uint Origin, uint Sequence)> OpHistory = new();
        public ulong Digest = EntryDigest.Empty;
        public uint Version;
    }

    private readonly Dictionary<string, ModuleLedger> _modules =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private string _module = string.Empty;

    public string Module { get { lock (_gate) return _module; } }
    public int Count { get { lock (_gate) return Current().Window.Count; } }
    public uint Version { get { lock (_gate) return Current().Version; } }
    public ulong Digest { get { lock (_gate) return Current().Digest; } }

    /// <summary>Moves the active view without deleting another module's history. Blank names
    /// during a transition are ignored, so stale packets cannot become current.</summary>
    public void SetModule(string module)
    {
        if (module.Length == 0) return;
        lock (_gate) { _module = module; _ = Current(); }
    }

    public bool ApplyOp(ContainerLootPacket op)
    {
        lock (_gate)
        {
            // A packet can legitimately arrive just after a load transition.  Keep it in the
            // ledger named by the packet so the operation is acknowledged and can be replayed
            // if that module becomes active again, but never make the packet's identity current.
            // An empty packet identity is only valid while the local session is also unscoped.
            if (op.Module.Length == 0 && _module.Length > 0) return false;
            string module = op.Module;
            ModuleLedger ledger = Get(module);
            (uint, uint) key = (op.OriginPeerId, op.OpSequence);
            if (!ledger.AppliedOps.Add(key)) return false;
            ledger.OpHistory.Enqueue(key);
            while (ledger.OpHistory.Count > MaxRememberedOps)
                ledger.AppliedOps.Remove(ledger.OpHistory.Dequeue());
            return Remember(ledger, new ContainerLootRemoval(op.ContainerId, op.ItemId));
        }
    }

    public bool Add(ContainerLootRemoval removal)
    {
        lock (_gate) return Remember(Current(), removal);
    }

    public bool Contains(ContainerLootRemoval removal)
    {
        lock (_gate) return Current().Known.Contains(removal);
    }

    public bool AcceptModule(string module) =>
        (_module.Length == 0 && module.Length == 0)
        || (_module.Length > 0 && module.Length > 0
            && string.Equals(_module, module, StringComparison.OrdinalIgnoreCase));

    public bool IsActiveModule(string module) => AcceptModule(module);

    private ModuleLedger Current()
    {
        return Get(_module);
    }

    private ModuleLedger Get(string module)
    {
        if (!_modules.TryGetValue(module, out ModuleLedger? ledger))
        {
            ledger = new ModuleLedger();
            _modules[module] = ledger;
        }
        return ledger;
    }

    private static bool Remember(ModuleLedger ledger, ContainerLootRemoval removal)
    {
        if (!ledger.Known.Add(removal)) return false;
        ledger.History.Add(removal);
        ledger.Window.Add(removal);
        ledger.Digest ^= EntryDigest.ContainerLootRemoval(removal.ContainerId, removal.ItemId);
        if (ledger.Window.Count > MaxWindow) ledger.Window.RemoveAt(0);
        ledger.Version++;
        return true;
    }

    public List<ContainerLootRemoval> Snapshot()
    {
        lock (_gate) return [.. Current().Window];
    }

    public List<ContainerLootRemoval> HistorySnapshot(string? module = null)
    {
        lock (_gate)
        {
            string name = module ?? _module;
            return _modules.TryGetValue(name, out ModuleLedger? ledger)
                ? [.. ledger.History] : [];
        }
    }

    public void Clear()
    {
        lock (_gate) { _modules.Clear(); _module = string.Empty; }
    }
}
