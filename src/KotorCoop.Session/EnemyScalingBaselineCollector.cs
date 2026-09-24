using System.Collections.ObjectModel;

namespace KotorCoop.Session;

/// <summary>A raw native-controller baseline. Tag and template are deliberately absent:
/// they must be joined from the exact saved archive, never guessed from a live object.</summary>
public readonly record struct EnemyScalingLatchRecord(uint ObjectId, int BaseMaximum, int AppliedPercent);

/// <summary>One page from a complete native latch snapshot. All pages of an export carry the
/// same session, module, module generation, export generation, page count, and completion bit.</summary>
public sealed record EnemyScalingLatchBatch(
    ulong ExactLoadSessionId,
    string Module,
    ulong ModuleGeneration,
    ulong ExportGeneration,
    int PageIndex,
    int PageCount,
    bool SnapshotComplete,
    IReadOnlyList<EnemyScalingLatchRecord> Records);

/// <summary>Module-qualified raw baselines accumulated for one exact loaded-save session.</summary>
public readonly record struct CollectedEnemyScalingBaseline(
    string Module,
    uint ObjectId,
    int BaseMaximum,
    int AppliedPercent);

/// <summary>An immutable copy of all complete module snapshots observed for one exact load.</summary>
public sealed class EnemyScalingBaselineCollectionSnapshot
{
    internal EnemyScalingBaselineCollectionSnapshot(
        ulong exactLoadSessionId,
        IReadOnlyList<CollectedEnemyScalingBaseline> records)
    {
        ExactLoadSessionId = exactLoadSessionId;
        Records = records;
    }

    public ulong ExactLoadSessionId { get; }
    public IReadOnlyList<CollectedEnemyScalingBaseline> Records { get; }
}

/// <summary>
/// Collects complete, bounded native latch snapshots across module epochs. A module snapshot
/// replaces the previous snapshot for that module only after every page validates; other
/// visited modules are retained. This is collection state only: it does not read archives,
/// create sidecars, or feed the native controller.
/// </summary>
public sealed class EnemyScalingBaselineCollector
{
    public const int MaximumRecordsPerModule = 256;
    public const int MaximumRecordsPerPage = 64;
    public const int MaximumPagesPerModule = 4;
    public const int MaximumTotalRecords = 16_384;
    public const int MaximumModules = 256;

    private const uint InvalidObjectId = 0x7F00_0000;
    private const uint PlayerObjectId = 0x7FFF_FFFF;

    private readonly Dictionary<string, ModuleState> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleSnapshot> _completed = new(StringComparer.OrdinalIgnoreCase);
    private ulong _exactLoadSessionId;
    private bool _capacityExceeded;

    private sealed class ModuleState(ulong generation)
    {
        public ulong ExpectedGeneration { get; set; } = generation;
        public ulong LatestExportGeneration { get; set; }
        public StagedExport? Staged { get; set; }
        public bool Poisoned { get; set; }
    }

    private sealed class StagedExport(
        ulong moduleGeneration,
        ulong exportGeneration,
        int pageCount,
        bool snapshotComplete)
    {
        public ulong ModuleGeneration { get; } = moduleGeneration;
        public ulong ExportGeneration { get; } = exportGeneration;
        public int PageCount { get; } = pageCount;
        public bool SnapshotComplete { get; } = snapshotComplete;
        public Dictionary<int, EnemyScalingLatchRecord[]> Pages { get; } = [];
    }

    private sealed record ModuleSnapshot(
        ulong ModuleGeneration,
        ulong ExportGeneration,
        int PageCount,
        bool SnapshotComplete,
        IReadOnlyList<EnemyScalingLatchRecord> Records,
        IReadOnlyDictionary<int, EnemyScalingLatchRecord[]> Pages);

    /// <summary>Starts a new exact loaded-save session and discards every previous module
    /// snapshot. Zero is reserved for an unknown/unbound session and is refused.</summary>
    public bool ResetForLoadSession(ulong exactLoadSessionId)
    {
        _exactLoadSessionId = 0;
        _modules.Clear();
        _completed.Clear();
        _capacityExceeded = false;
        if (exactLoadSessionId == 0)
        {
            return false;
        }

        _exactLoadSessionId = exactLoadSessionId;
        return true;
    }

    /// <summary>Declares that a module epoch is expected. This barrier prevents an empty
    /// collector from being frozen before its active module has reported a complete snapshot.
    /// A newer epoch supersedes the pending snapshot for this module; older epochs are ignored.</summary>
    public bool ExpectModule(string module, ulong moduleGeneration)
    {
        if (_exactLoadSessionId == 0 || !TryNormalizeModule(module, out string normalized)
            || moduleGeneration == 0)
        {
            return false;
        }

        if (_modules.TryGetValue(normalized, out ModuleState? state))
        {
            if (moduleGeneration < state.ExpectedGeneration)
            {
                return false;
            }

            if (moduleGeneration == state.ExpectedGeneration)
            {
                return true;
            }

            state.ExpectedGeneration = moduleGeneration;
            state.LatestExportGeneration = 0;
            state.Staged = null;
            state.Poisoned = false;
            return true;
        }

        if (_modules.Count >= MaximumModules)
        {
            _capacityExceeded = true;
            return false;
        }

        _modules.Add(normalized, new ModuleState(moduleGeneration));
        return true;
    }

    /// <summary>Accepts a page from the currently expected module epoch. Stale load-session,
    /// module-generation, and export-generation pages are rejected without poisoning newer
    /// work. A malformed or conflicting current export blocks freezing until a newer complete
    /// export for that module or an exact-load reset.</summary>
    public bool Accept(EnemyScalingLatchBatch batch)
    {
        if (batch is null || batch.ExactLoadSessionId == 0
            || batch.ExactLoadSessionId != _exactLoadSessionId
            || !TryNormalizeModule(batch.Module, out string module)
            || batch.ModuleGeneration == 0 || batch.ExportGeneration == 0
            || !_modules.TryGetValue(module, out ModuleState? state))
        {
            return false;
        }

        if (batch.ModuleGeneration < state.ExpectedGeneration
            || batch.ModuleGeneration > state.ExpectedGeneration
            || batch.ExportGeneration < state.LatestExportGeneration)
        {
            return false;
        }

        if (batch.PageCount is < 1 or > MaximumPagesPerModule
            || batch.PageIndex < 0 || batch.PageIndex >= batch.PageCount
            || batch.Records is null || batch.Records.Count > MaximumRecordsPerPage)
        {
            state.LatestExportGeneration = Math.Max(state.LatestExportGeneration, batch.ExportGeneration);
            Poison(state);
            return false;
        }

        if (state.Poisoned && batch.ExportGeneration <= state.LatestExportGeneration)
        {
            return false;
        }

        if (batch.ExportGeneration > state.LatestExportGeneration)
        {
            state.LatestExportGeneration = batch.ExportGeneration;
            state.Staged = new StagedExport(
                batch.ModuleGeneration,
                batch.ExportGeneration,
                batch.PageCount,
                batch.SnapshotComplete);
            state.Poisoned = false;
        }

        StagedExport? staged = state.Staged;
        if (staged is null
            && _completed.TryGetValue(module, out ModuleSnapshot? completed)
            && completed.ModuleGeneration == batch.ModuleGeneration
            && completed.ExportGeneration == batch.ExportGeneration)
        {
            if (completed.PageCount == batch.PageCount
                && completed.SnapshotComplete == batch.SnapshotComplete
                && completed.Pages.TryGetValue(batch.PageIndex, out EnemyScalingLatchRecord[]? savedPage)
                && savedPage.AsSpan().SequenceEqual(batch.Records.ToArray()))
            {
                return true;
            }

            Poison(state);
            return false;
        }

        if (staged is null
            || staged.ModuleGeneration != batch.ModuleGeneration
            || staged.ExportGeneration != batch.ExportGeneration)
        {
            return false;
        }

        if (staged.PageCount != batch.PageCount || staged.SnapshotComplete != batch.SnapshotComplete)
        {
            Poison(state);
            return false;
        }

        EnemyScalingLatchRecord[] page = batch.Records.ToArray();
        if (!ValidPage(page))
        {
            Poison(state);
            return false;
        }

        if (staged.Pages.TryGetValue(batch.PageIndex, out EnemyScalingLatchRecord[]? priorPage))
        {
            if (!priorPage.AsSpan().SequenceEqual(page))
            {
                Poison(state);
                return false;
            }

            return true;
        }

        staged.Pages.Add(batch.PageIndex, page);
        if (!staged.SnapshotComplete || staged.Pages.Count != staged.PageCount)
        {
            return true;
        }

        if (!TryCommit(module, staged))
        {
            Poison(state);
            return false;
        }

        state.Staged = null;
        state.Poisoned = false;
        return true;
    }

    /// <summary>Returns an immutable copy only when at least one module has been explicitly
    /// expected, every expected module has a complete current-generation snapshot, and no
    /// partial or poisoned export remains. A complete zero-record snapshot is valid.</summary>
    public bool TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot)
    {
        snapshot = null;
        if (_exactLoadSessionId == 0 || _capacityExceeded || _modules.Count == 0 || _completed.Count == 0)
        {
            return false;
        }

        foreach ((string module, ModuleState state) in _modules)
        {
            if (state.Poisoned || state.Staged is not null
                || !_completed.TryGetValue(module, out ModuleSnapshot? completed)
                || completed.ModuleGeneration != state.ExpectedGeneration)
            {
                return false;
            }
        }

        List<CollectedEnemyScalingBaseline> records = new(_completed.Values.Sum(static x => x.Records.Count));
        foreach ((string module, ModuleSnapshot completed) in _completed.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (EnemyScalingLatchRecord record in completed.Records)
            {
                records.Add(new CollectedEnemyScalingBaseline(
                    module,
                    record.ObjectId,
                    record.BaseMaximum,
                    record.AppliedPercent));
            }
        }

        snapshot = new EnemyScalingBaselineCollectionSnapshot(
            _exactLoadSessionId,
            new ReadOnlyCollection<CollectedEnemyScalingBaseline>(records));
        return true;
    }

    /// <summary>The last complete snapshot committed for exactly this module epoch, with the
    /// export generation it came from. The caller decides whether that export is the final
    /// or latest one it needs (<c>EnemyScalingPersistence</c> compares it with the plugin's
    /// published export generation), so a newer partial export never hides the committed one
    /// and a committed one is never mistaken for a newer latch state.</summary>
    public bool TryGetCommittedSnapshot(
        string module,
        ulong moduleGeneration,
        out ulong exportGeneration,
        out IReadOnlyList<EnemyScalingLatchRecord> records)
    {
        exportGeneration = 0;
        records = [];
        if (_exactLoadSessionId == 0 || _capacityExceeded
            || !TryNormalizeModule(module, out string normalized)
            || !_completed.TryGetValue(normalized, out ModuleSnapshot? completed)
            || completed.ModuleGeneration != moduleGeneration
            || !completed.SnapshotComplete)
        {
            return false;
        }

        exportGeneration = completed.ExportGeneration;
        records = completed.Records;
        return true;
    }

    private bool TryCommit(string module, StagedExport staged)
    {
        List<EnemyScalingLatchRecord> records = new();
        HashSet<uint> ids = [];
        foreach (EnemyScalingLatchRecord[] page in staged.Pages.OrderBy(static x => x.Key).Select(static x => x.Value))
        {
            foreach (EnemyScalingLatchRecord record in page)
            {
                if (!ids.Add(record.ObjectId))
                {
                    return false;
                }

                records.Add(record);
            }
        }

        if (records.Count > MaximumRecordsPerModule)
        {
            return false;
        }

        int priorCount = _completed.TryGetValue(module, out ModuleSnapshot? previous)
            ? previous.Records.Count : 0;
        int totalAfterReplace = _completed.Values.Sum(static x => x.Records.Count) - priorCount + records.Count;
        if (totalAfterReplace > MaximumTotalRecords)
        {
            return false;
        }

        _completed[module] = new ModuleSnapshot(
            staged.ModuleGeneration,
            staged.ExportGeneration,
            staged.PageCount,
            staged.SnapshotComplete,
            Array.AsReadOnly(records.ToArray()),
            new ReadOnlyDictionary<int, EnemyScalingLatchRecord[]>(
                staged.Pages.ToDictionary(static x => x.Key, static x => x.Value.ToArray())));
        return true;
    }

    private static bool ValidPage(EnemyScalingLatchRecord[] records)
    {
        HashSet<uint> ids = [];
        foreach (EnemyScalingLatchRecord record in records)
        {
            if (record.ObjectId is 0 or InvalidObjectId or PlayerObjectId or uint.MaxValue
                || record.BaseMaximum is <= 0 or > ushort.MaxValue
                || record.AppliedPercent is < 50 or > 400
                || !ids.Add(record.ObjectId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryNormalizeModule(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 32)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        normalized = value.ToLowerInvariant();
        return true;
    }

    private static void Poison(ModuleState state)
    {
        state.Poisoned = true;
        state.Staged = null;
    }

}
