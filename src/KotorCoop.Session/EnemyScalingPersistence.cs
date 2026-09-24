using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The agent's half of exact enemy-health baseline persistence
/// (docs/research/enemy-health-persistence.md): one process-lifetime state machine, ticked
/// from the command loop, that
///
/// <list type="number">
/// <item>follows the plugin's world generations and keeps a LINEAGE — the chain of worlds
/// since the last proven load or new game — with, per module, whether its saved creature
/// state is proven (a complete set of scaled baselines, possibly empty) or unknown;</item>
/// <item>answers every world-up with a verdict (fresh / import these baselines / refuse) the
/// plugin must receive before it writes any health;</item>
/// <item>collects the plugin's latch exports through <see cref="EnemyScalingBaselineCollector"/>
/// and retires each module with its final export when the world changes;</item>
/// <item>after a native save request, waits for the archive to settle, joins the lineage's
/// baselines against the saved creature table, and writes the sidecar next to the archive;</item>
/// <item>binds a native load (provenance + a later load edge) to that exact archive and its
/// sidecar before the loaded world's verdict.</item>
/// </list>
///
/// Everything fails closed: an unknown lineage, an uncovered saved module, a torn export, a
/// missed world or an unreadable archive makes that module's health stay exactly as loaded.
/// </summary>
public sealed class EnemyScalingPersistence
{
    /// <summary>How long the archive's size and timestamp must stay unchanged, while no
    /// writer holds it open, before it counts as settled.</summary>
    public const long SettleQuietMs = 1500;

    /// <summary>A save request whose archive never settles (a held save that was dropped, a
    /// disk-space refusal) is abandoned after this long.</summary>
    public const long CaptureTimeoutMs = 60_000;

    /// <summary>File-system timestamp tolerance when comparing the archive's write time with
    /// the plugin's request time.</summary>
    public static readonly TimeSpan ArchiveClockSlack = TimeSpan.FromSeconds(2);

    private enum LineageKind
    {
        Unknown,
        NewGame,
        Loaded,
    }

    private sealed record Coverage(bool Covered, EnemyScalingLatchEntry[] Records)
    {
        public static readonly Coverage Unknown = new(false, []);
    }

    private sealed class PendingImport(uint generation, EnemyScalingVerdict verdict, EnemyScalingLatchEntry[] records)
    {
        public uint Generation { get; } = generation;
        public EnemyScalingVerdict Verdict { get; } = verdict;
        public EnemyScalingLatchEntry[] Records { get; } = records;
        public int PageCount { get; } = verdict == EnemyScalingVerdict.Import
            ? EnemyScalingBaselineChannel.ImportPageCount(records.Length) : 1;
        public int NextPage { get; set; }
        public uint AwaitingSequence { get; set; }
    }

    private sealed class PendingCapture(EnemyScalingSaveRequest request, long firstSeenMs)
    {
        public EnemyScalingSaveRequest Request { get; } = request;
        public long FirstSeenMs { get; } = firstSeenMs;
        public (long Length, long WriteTicks) Observation { get; set; } = (-1, -1);
        public long StableSinceMs { get; set; }
    }

    private readonly IEnemyScalingBaselinePort _port;
    private readonly string _savesDirectory;
    private readonly Action<string>? _log;
    private readonly EnemyScalingBaselineCollector _collector = new();
    private readonly Dictionary<string, Coverage> _coverage = new(StringComparer.OrdinalIgnoreCase);

    private LineageKind _lineage = LineageKind.Unknown;
    private ulong _lineageId;
    private uint _worldGeneration;
    private string _worldModule = string.Empty;
    private EnemyScalingVerdict _worldVerdict;
    private EnemyScalingHealthMode _lastMode;
    private uint _lastExportSeen;
    private uint _lastAckedExportSequence;
    private PendingImport? _import;
    private uint _lastSaveSequence;
    private bool _saveSequencePrimed;
    private PendingCapture? _capture;

    public EnemyScalingPersistence(IEnemyScalingBaselinePort port, string savesDirectory, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        _port = port;
        _savesDirectory = savesDirectory;
        _log = log;
    }

    /// <summary>The last sidecar outcome, for the status line and tests.</summary>
    public string LastCaptureResult { get; private set; } = "none";

    public uint WorldGeneration => _worldGeneration;

    public EnemyScalingVerdict WorldVerdict => _worldVerdict;

    public string Lineage => _lineage.ToString();

    public void Tick(long nowMs)
    {
        if (_port.TryReadEnemyScalingWorldPage(out EnemyScalingWorldPage? page) && page is not null)
        {
            if (page.WorldGeneration != _worldGeneration)
            {
                OnWorldUp(page);
            }

            OnExportPage(page);
        }

        PumpImport();
        PumpSaveCapture(nowMs);
    }

    /// <summary>One line for the console status verb.</summary>
    public string Describe() =>
        $"lineage {_lineage} (id {_lineageId}), world {_worldGeneration} '{_worldModule}' "
        + $"verdict {_worldVerdict} mode {_lastMode} export {_lastExportSeen}, "
        + $"{_coverage.Count(static x => x.Value.Covered)} covered / {_coverage.Count(static x => !x.Value.Covered)} unknown module(s); "
        + $"last capture: {LastCaptureResult}";

    // ---- world lineage ------------------------------------------------------------------

    private void OnWorldUp(EnemyScalingWorldPage page)
    {
        bool continuous = _worldGeneration != 0
            && page.PreviousWorldGeneration == _worldGeneration
            && page.WorldGeneration == unchecked(_worldGeneration + 1);

        if (_worldGeneration != 0)
        {
            if (continuous)
            {
                RetireWorld(page.PreviousFinalExportGeneration);
            }
            else
            {
                MarkUnknown($"world {page.WorldGeneration} does not follow world {_worldGeneration}");
            }
        }

        _worldGeneration = page.WorldGeneration;
        _worldModule = page.Module;
        _lastMode = page.Mode;
        _lastExportSeen = page.ExportGeneration;
        _import = null;

        switch (page.Origin)
        {
            case EnemyScalingWorldOrigin.NewGame:
                StartLineage(LineageKind.NewGame, []);
                _log?.Invoke($"enemy-baseline: world {page.WorldGeneration} '{page.Module}' is a new game; "
                    + "every module scales fresh until it is saved");
                break;

            case EnemyScalingWorldOrigin.Load:
                if (TryBindLoad(page, out Dictionary<string, Coverage>? loaded, out string bound))
                {
                    StartLineage(LineageKind.Loaded, loaded!);
                    _log?.Invoke($"enemy-baseline: world {page.WorldGeneration} '{page.Module}' bound to {bound}");
                }
                else
                {
                    _log?.Invoke($"enemy-baseline: world {page.WorldGeneration} '{page.Module}' load not bound: {bound}");
                    MarkUnknown($"load not bound: {bound}");
                }

                break;

            case EnemyScalingWorldOrigin.Transition:
                if (!continuous)
                {
                    MarkUnknown("transition into a world whose predecessor this agent never saw");
                }

                break;

            default:
                MarkUnknown($"world {page.WorldGeneration} came up with an unproven origin");
                break;
        }

        (EnemyScalingVerdict verdict, EnemyScalingLatchEntry[] records) = DecideVerdict(page.Module);
        _worldVerdict = verdict;
        if (_lineage != LineageKind.Unknown && EnemyScalingBaselineRule.IsValidModule(page.Module))
        {
            _collector.ExpectModule(page.Module, page.WorldGeneration);
        }

        _import = new PendingImport(page.WorldGeneration, verdict, records);
        _log?.Invoke($"enemy-baseline: world {page.WorldGeneration} '{page.Module}' ({page.Origin}) -> "
            + $"{verdict}{(verdict == EnemyScalingVerdict.Import ? $" {records.Length} baseline(s)" : string.Empty)}");
    }

    private (EnemyScalingVerdict, EnemyScalingLatchEntry[]) DecideVerdict(string module)
    {
        if (_lineage == LineageKind.Unknown || !EnemyScalingBaselineRule.IsValidModule(module))
        {
            return (EnemyScalingVerdict.Refuse, []);
        }

        if (_coverage.TryGetValue(module, out Coverage? coverage))
        {
            return coverage.Covered
                ? (EnemyScalingVerdict.Import, coverage.Records)
                : (EnemyScalingVerdict.Refuse, []);
        }

        // Neither in the loaded archive nor visited since the load or new game: the engine
        // builds this module from its pristine GIT, and no creature in it has been scaled.
        return (EnemyScalingVerdict.Fresh, []);
    }

    /// <summary>The world that just went away is final: its module's coverage becomes the
    /// complete snapshot of its LAST export (the generation the plugin names in the new page),
    /// or unknown when that export was never completely collected.</summary>
    private void RetireWorld(uint finalExportGeneration)
    {
        if (_lineage == LineageKind.Unknown || !EnemyScalingBaselineRule.IsValidModule(_worldModule))
        {
            return;
        }

        Coverage? retired = WorldCoverage(finalExportGeneration, out bool ready);
        _coverage[_worldModule] = ready && retired is not null ? retired : Coverage.Unknown;
        if (!ready || retired is null || !retired.Covered)
        {
            _log?.Invoke($"enemy-baseline: '{_worldModule}' left without a complete final baseline "
                + $"(export {finalExportGeneration}); its saved health is unknown from now on");
        }
    }

    /// <summary>The current world's coverage as of <paramref name="exportGeneration"/>. Not
    /// ready = the plugin has published a newer export than the collector has completed.</summary>
    private Coverage? WorldCoverage(uint exportGeneration, out bool ready)
    {
        ready = true;
        switch (_lastMode)
        {
            case EnemyScalingHealthMode.Pending when exportGeneration == _lastExportSeen:
                // No verdict was applied, so no health was written: the module holds exactly
                // what it was loaded with, which is what the verdict was computed from.
                return _coverage.TryGetValue(_worldModule, out Coverage? before)
                    ? before
                    : new Coverage(true, []);

            case EnemyScalingHealthMode.Refused:
                return Coverage.Unknown;

            default:
                if (_collector.TryGetCommittedSnapshot(
                        _worldModule, _worldGeneration, out ulong committed, out IReadOnlyList<EnemyScalingLatchRecord> records)
                    && committed == exportGeneration)
                {
                    return new Coverage(true, records
                        .Select(static r => new EnemyScalingLatchEntry(r.ObjectId, r.BaseMaximum, r.AppliedPercent))
                        .ToArray());
                }

                ready = false;
                return null;
        }
    }

    private void StartLineage(LineageKind kind, Dictionary<string, Coverage> coverage)
    {
        _lineage = kind;
        _lineageId++;
        _collector.ResetForLoadSession(_lineageId);
        _coverage.Clear();
        foreach ((string module, Coverage value) in coverage)
        {
            _coverage[module] = value;
        }
    }

    private void MarkUnknown(string reason)
    {
        if (_lineage != LineageKind.Unknown)
        {
            _log?.Invoke($"enemy-baseline: lineage lost ({reason}); health scaling stays off for saved "
                + "creatures until the next exact load or new game");
        }

        _lineage = LineageKind.Unknown;
        _coverage.Clear();
        _collector.ResetForLoadSession(0);
    }

    private void OnExportPage(EnemyScalingWorldPage page)
    {
        if (page.WorldGeneration == _worldGeneration)
        {
            _lastMode = page.Mode;
            _lastExportSeen = page.ExportGeneration;

            if (_lineage != LineageKind.Unknown && page.ExportGeneration != 0
                && string.Equals(page.Module, _worldModule, StringComparison.OrdinalIgnoreCase))
            {
                _collector.Accept(new EnemyScalingLatchBatch(
                    _lineageId,
                    page.Module,
                    page.WorldGeneration,
                    page.ExportGeneration,
                    page.PageIndex,
                    page.PageCount,
                    page.SnapshotComplete && !page.Overflow
                        && page.Mode is EnemyScalingHealthMode.Fresh or EnemyScalingHealthMode.Imported,
                    page.Records.Select(static r => new EnemyScalingLatchRecord(r.ObjectId, r.BaseMaximum, r.AppliedPercent)).ToArray()));
            }
        }

        if (page.Sequence != _lastAckedExportSequence)
        {
            _lastAckedExportSequence = page.Sequence;
            _port.AcknowledgeEnemyScalingExport(page.Sequence);
        }
    }

    private void PumpImport()
    {
        PendingImport? import = _import;
        if (import is null)
        {
            return;
        }

        if (import.AwaitingSequence != 0)
        {
            if (!_port.TryReadEnemyScalingImportAck(out uint ack) || ack != import.AwaitingSequence)
            {
                return;
            }

            import.AwaitingSequence = 0;
            import.NextPage++;
        }

        if (import.NextPage >= import.PageCount)
        {
            _import = null;
            return;
        }

        int start = import.NextPage * EnemyScalingBaselineChannel.ImportRecordsPerPage;
        int count = Math.Min(EnemyScalingBaselineChannel.ImportRecordsPerPage, import.Records.Length - start);
        ReadOnlySpan<EnemyScalingLatchEntry> slice = import.Verdict == EnemyScalingVerdict.Import && count > 0
            ? import.Records.AsSpan(start, count)
            : [];
        if (_port.TryWriteEnemyScalingImportPage(
                import.Generation, import.Verdict, import.NextPage, import.PageCount,
                import.Verdict == EnemyScalingVerdict.Import ? import.Records.Length : 0,
                slice, out uint sequence))
        {
            import.AwaitingSequence = sequence;
        }
    }

    // ---- load binding -------------------------------------------------------------------

    private bool TryBindLoad(EnemyScalingWorldPage page, out Dictionary<string, Coverage>? coverage, out string diagnostic)
    {
        coverage = null;
        if (!_port.TryReadLoadProvenance(out LoadProvenance? provenance) || provenance is null
            || provenance.Generation != page.OriginProvenanceGeneration)
        {
            diagnostic = $"load request {page.OriginProvenanceGeneration} is no longer the published one";
            return false;
        }

        if (provenance.Status is not (LoadProvenanceStatus.Captured or LoadProvenanceStatus.CapturedDirectory))
        {
            diagnostic = $"load request {provenance.Generation} was not captured ({provenance.Status})";
            return false;
        }

        // A request alone proves nothing: the world must have been REPLACED after it. An
        // in-world request is followed by the plugin's unannounced-teardown serial bump; a
        // request made with no world standing (main menu) is followed by this very world-up.
        bool serialEdge = unchecked((int)(page.WorldLoadSerialAtWorldUp - provenance.WorldLoadSerialAtRequest)) > 0;
        bool withoutWorld = (page.OriginFlags & EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld) != 0;
        if (!serialEdge && !withoutWorld)
        {
            diagnostic = $"load request {provenance.Generation} has no successful-load edge "
                + $"(serial {provenance.WorldLoadSerialAtRequest} -> {page.WorldLoadSerialAtWorldUp})";
            return false;
        }

        string? directory = provenance.Source == LoadProvenanceSource.MainMenuDirectDirectory
            ? provenance.DirectoryName
            : provenance.SaveName is { } name ? EnemyScalingBaselineChannel.SaveDirectoryName(provenance.Slot, name) : null;
        if (!TryResolveSaveDirectory(directory, out string saveDirectory))
        {
            diagnostic = $"load request {provenance.Generation} names no save directory";
            return false;
        }

        if (!SaveDigest.TryReadActiveModuleName(saveDirectory, out string active)
            || !string.Equals(active, page.Module, StringComparison.OrdinalIgnoreCase))
        {
            diagnostic = $"'{directory}' is not the archive of the loaded module '{page.Module}'";
            return false;
        }

        if (!EnemyScalingArchiveReader.TryRead(saveDirectory, out EnemyScalingArchiveContents? contents, out string readDiagnostic)
            || contents is null)
        {
            diagnostic = $"'{directory}': {readDiagnostic}";
            return false;
        }

        if (!SaveDigest.TryComputeSaveIdentity(saveDirectory, out uint identity) || identity == 0)
        {
            diagnostic = $"'{directory}': save identity unreadable";
            return false;
        }

        coverage = new Dictionary<string, Coverage>(StringComparer.OrdinalIgnoreCase);
        foreach (string module in contents.Modules.Keys)
        {
            coverage[module] = Coverage.Unknown;
        }

        string sidecarPath = Path.Combine(saveDirectory, EnemyScalingBaselineStore.SidecarFileName);
        if (!EnemyScalingBaselineStore.TryReadForArchiveContent(
                sidecarPath, Path.Combine(saveDirectory, "SAVEGAME.sav"), identity,
                out EnemyScalingBaselineDocument? document, out string sidecarDiagnostic)
            || document is null
            || !string.Equals(document.ArchiveSha256, contents.Sha256, StringComparison.Ordinal))
        {
            diagnostic = $"'{directory}' (sha256 {contents.Sha256[..12]}), no usable baseline sidecar "
                + $"({sidecarDiagnostic}); its {contents.Modules.Count} saved module(s) stay unscaled";
            return true;
        }

        int covered = 0;
        foreach (string module in EnemyScalingBaselineRule.CoveredModules(document))
        {
            if (!contents.Modules.TryGetValue(module, out EnemyScalingArchiveModule? saved))
            {
                continue;
            }

            EnemyScalingBaselineRecord[] records = document.Records
                .Where(r => string.Equals(r.Module, module, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (EnemyScalingArchiveJoin.VerifyImported(records, saved, out EnemyScalingLatchEntry[] entries, out string why))
            {
                coverage[module] = new Coverage(true, entries);
                covered++;
            }
            else
            {
                _log?.Invoke($"enemy-baseline: '{module}' in '{directory}' not proven: {why}");
            }
        }

        diagnostic = $"'{directory}' (sha256 {contents.Sha256[..12]}, identity 0x{identity:X8}): "
            + $"{covered} of {contents.Modules.Count} saved module(s) proven";
        return true;
    }

    private bool TryResolveSaveDirectory(string? directoryName, out string saveDirectory)
    {
        saveDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryName) || string.IsNullOrWhiteSpace(_savesDirectory)
            || directoryName.IndexOfAny(['/', '\\', ':']) >= 0 || directoryName is "." or "..")
        {
            return false;
        }

        try
        {
            string saves = Path.GetFullPath(_savesDirectory);
            string full = Path.GetFullPath(Path.Combine(saves, directoryName));
            if (!full.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(full))
            {
                return false;
            }

            saveDirectory = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    // ---- save capture -------------------------------------------------------------------

    private void PumpSaveCapture(long nowMs)
    {
        if (_port.TryReadEnemyScalingSaveRequest(out EnemyScalingSaveRequest? request) && request is not null)
        {
            if (!_saveSequencePrimed)
            {
                // A request already in the mailbox when this agent started belongs to a save
                // whose lineage this process never saw; never write a sidecar for it.
                _saveSequencePrimed = true;
                _lastSaveSequence = request.Sequence;
            }
            else if (request.Sequence != _lastSaveSequence)
            {
                _lastSaveSequence = request.Sequence;
                _capture = new PendingCapture(request, nowMs);
            }
        }
        else if (!_saveSequencePrimed && _port.TryReadEnemyScalingWorldPage(out _))
        {
            // The plugin is there and has never published a save request: every later one is new.
            _saveSequencePrimed = true;
        }

        PendingCapture? capture = _capture;
        if (capture is null)
        {
            return;
        }

        string directoryName = capture.Request.DirectoryName;
        if (nowMs - capture.FirstSeenMs > CaptureTimeoutMs)
        {
            FinishCapture($"'{directoryName}': abandoned, the archive never settled");
            return;
        }

        if (!TryResolveSaveDirectory(directoryName, out string saveDirectory)
            || !IsArchiveSettled(capture, saveDirectory, nowMs))
        {
            return;
        }

        if (_lineage == LineageKind.Unknown)
        {
            FinishCapture($"'{directoryName}': no sidecar, the lineage of this world is unknown");
            return;
        }

        if (!EnemyScalingArchiveReader.TryRead(saveDirectory, out EnemyScalingArchiveContents? contents, out string readDiagnostic)
            || contents is null)
        {
            FinishCapture($"'{directoryName}': no sidecar, {readDiagnostic}");
            return;
        }

        List<string> coveredModules = [];
        List<EnemyScalingBaselineRecord> records = [];
        List<string> unproven = [];
        foreach ((string module, EnemyScalingArchiveModule saved) in contents.Modules.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            Coverage? coverage;
            if (string.Equals(module, _worldModule, StringComparison.OrdinalIgnoreCase))
            {
                coverage = WorldCoverage(_lastExportSeen, out bool ready);
                if (!ready)
                {
                    // The live latch has an export the collector has not finished; the next
                    // ticks complete it (the archive stays settled meanwhile).
                    return;
                }
            }
            else
            {
                coverage = _coverage.GetValueOrDefault(module);
            }

            if (coverage is null || !coverage.Covered)
            {
                unproven.Add(module);
                continue;
            }

            if (!EnemyScalingArchiveJoin.TryJoinModule(module, coverage.Records, saved, records, out string why))
            {
                _log?.Invoke($"enemy-baseline: '{module}' in '{directoryName}' not joined: {why}");
                unproven.Add(module);
                continue;
            }

            coveredModules.Add(module);
        }

        if (!SaveDigest.TryComputeSaveIdentity(saveDirectory, out uint identity) || identity == 0)
        {
            FinishCapture($"'{directoryName}': no sidecar, save identity unreadable");
            return;
        }

        EnemyScalingSaveKey key = new(capture.Request.Slot, directoryName, identity);
        string archivePath = Path.Combine(saveDirectory, "SAVEGAME.sav");
        if (EnemyScalingBaselineStore.TryWriteForArchive(
                Path.Combine(saveDirectory, EnemyScalingBaselineStore.SidecarFileName),
                archivePath, key, records, out string writeDiagnostic, coveredModules, contents.Sha256))
        {
            FinishCapture($"'{directoryName}': sidecar written, {coveredModules.Count} module(s) proven "
                + $"({records.Count} scaled creature(s)), {unproven.Count} unknown"
                + (unproven.Count > 0 ? $" [{string.Join(", ", unproven)}]" : string.Empty));
        }
        else
        {
            FinishCapture($"'{directoryName}': sidecar NOT written ({writeDiagnostic})");
        }
    }

    /// <summary>The settled-archive rule: SAVEGAME.sav and savenfo.res exist and were written
    /// no earlier than the request (less clock slack); no other handle holds the archive open
    /// for writing; and its length and write time have not changed for
    /// <see cref="SettleQuietMs"/>. The store then hashes it before and after preparing the
    /// sidecar and refuses to publish if the bytes moved.</summary>
    private static bool IsArchiveSettled(PendingCapture capture, string saveDirectory, long nowMs)
    {
        try
        {
            DateTime requested = DateTime.FromFileTimeUtc(capture.Request.RequestFileTimeUtc) - ArchiveClockSlack;
            FileInfo archive = new(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            FileInfo info = new(Path.Combine(saveDirectory, "savenfo.res"));
            if (!archive.Exists || !info.Exists
                || archive.LastWriteTimeUtc < requested || info.LastWriteTimeUtc < requested)
            {
                return false;
            }

            using (new FileStream(archive.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
            }

            archive.Refresh();
            (long, long) observation = (archive.Length, archive.LastWriteTimeUtc.Ticks);
            if (observation != capture.Observation)
            {
                capture.Observation = observation;
                capture.StableSinceMs = nowMs;
                return false;
            }

            return nowMs - capture.StableSinceMs >= SettleQuietMs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private void FinishCapture(string result)
    {
        _capture = null;
        LastCaptureResult = result;
        _log?.Invoke($"enemy-baseline: save {result}");
    }
}

/// <summary>
/// Pure joins between baselines and a saved creature table, used by
/// <see cref="EnemyScalingPersistence"/> at capture (latch → sidecar records) and at load
/// (sidecar records → verified import).
/// </summary>
public static class EnemyScalingArchiveJoin
{
    /// <summary>
    /// Joins one module's latch entries against its saved creatures. Per entry: absent from
    /// the save → dropped (it no longer exists there); saved maximum equal to the scaled
    /// value → kept with the saved tag/template; saved maximum equal to the original → dropped
    /// (it was scaled after the archive was written, or rounding made no change; the loaded
    /// creature is unscaled and a first sighting will scale it once); anything else → the
    /// module is unproven. A kept entry whose saved tag or template is not a valid identity
    /// also makes the module unproven.
    /// </summary>
    public static bool TryJoinModule(
        string module,
        IReadOnlyList<EnemyScalingLatchEntry> entries,
        EnemyScalingArchiveModule saved,
        List<EnemyScalingBaselineRecord> output,
        out string diagnostic)
    {
        if (saved.Creatures is null)
        {
            diagnostic = "saved module state unreadable or ambiguous";
            return false;
        }

        List<EnemyScalingBaselineRecord> joined = [];
        foreach (EnemyScalingLatchEntry entry in entries)
        {
            if (!saved.Creatures.TryGetValue(entry.ObjectId, out EnemyScalingArchiveCreature? creature))
            {
                continue;
            }

            int scaled = EnemyScalingBaselineRule.ScaleMaximum(entry.BaseMaximum, entry.AppliedPercent);
            if (scaled != 0 && creature.HitPoints == scaled)
            {
                if (!EnemyScalingBaselineRule.IsValidTag(creature.Tag)
                    || !EnemyScalingBaselineRule.IsValidTemplate(creature.Template))
                {
                    diagnostic = $"0x{entry.ObjectId:X} has no usable saved tag/template";
                    return false;
                }

                joined.Add(new EnemyScalingBaselineRecord(
                    module, entry.ObjectId, creature.Tag, creature.Template, entry.BaseMaximum, entry.AppliedPercent));
                continue;
            }

            if (creature.HitPoints == entry.BaseMaximum)
            {
                continue;
            }

            diagnostic = $"0x{entry.ObjectId:X} saved max {creature.HitPoints} is neither {entry.BaseMaximum} "
                + $"nor {scaled} ({entry.AppliedPercent}%)";
            return false;
        }

        output.AddRange(joined);
        diagnostic = "joined";
        return true;
    }

    /// <summary>
    /// Verifies a sidecar module's records against the archive being loaded: every record's
    /// object must be saved with the recorded tag and template and a stored maximum equal to
    /// its baseline scaled by its recorded percentage. One failure proves nothing about the
    /// module, so it is refused whole.
    /// </summary>
    public static bool VerifyImported(
        IReadOnlyList<EnemyScalingBaselineRecord> records,
        EnemyScalingArchiveModule saved,
        out EnemyScalingLatchEntry[] entries,
        out string diagnostic)
    {
        entries = [];
        if (saved.Creatures is null)
        {
            diagnostic = "saved module state unreadable or ambiguous";
            return false;
        }

        if (records.Count > EnemyScalingBaselineChannel.LatchCapacity)
        {
            diagnostic = $"{records.Count} baselines exceed the native latch";
            return false;
        }

        List<EnemyScalingLatchEntry> verified = new(records.Count);
        foreach (EnemyScalingBaselineRecord record in records)
        {
            if (!saved.Creatures.TryGetValue(record.ObjectId, out EnemyScalingArchiveCreature? creature)
                || !string.Equals(creature.Tag, record.Tag, StringComparison.Ordinal)
                || !string.Equals(creature.Template, record.Template, StringComparison.OrdinalIgnoreCase)
                || creature.HitPoints != EnemyScalingBaselineRule.ScaleMaximum(record.BaseMaximum, record.AppliedPercent))
            {
                diagnostic = $"0x{record.ObjectId:X} does not match its saved creature";
                return false;
            }

            verified.Add(new EnemyScalingLatchEntry(record.ObjectId, record.BaseMaximum, record.AppliedPercent));
        }

        entries = verified.ToArray();
        diagnostic = "verified";
        return true;
    }
}
