using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// End-to-end agent behaviour of exact enemy-health baseline persistence
/// (docs/research/enemy-health-persistence.md) against a faked plugin block and REAL save
/// archives on disk: scale once, save, reload, and the reloaded world imports the baseline
/// instead of multiplying again; plus every fail-closed branch of the lineage and the join.
/// </summary>
public sealed class EnemyScalingPersistenceTests : IDisposable
{
    private readonly string _saves = Path.Combine(Path.GetTempPath(), "kotorcoop-baseline-saves-" + Guid.NewGuid().ToString("N"));
    private readonly FakePort _port = new();
    private readonly List<string> _log = [];
    private long _now = 10_000;

    public EnemyScalingPersistenceTests() => Directory.CreateDirectory(_saves);

    public void Dispose() => Directory.Delete(_saves, recursive: true);

    private EnemyScalingPersistence NewAgent() => new(_port, _saves, _log.Add);

    [Fact]
    public void ScaleOnceSaveReloadImportsTheBaselineInsteadOfMultiplyingAgain()
    {
        EnemyScalingPersistence agent = NewAgent();
        Tick(agent); // primes the save mailbox (nothing published yet)

        // New game: the world is fresh and the plugin is told so.
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Pending, 1);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Fresh, _port.LastVerdict(1));

        // The plugin scales 0x2 (15 -> 23 at 150%) and exports its latch.
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 2,
            complete: true, records: [new(0x2, 15, 150)]);
        Tick(agent);

        // The player saves into slot 1 "Game1"; the archive lands and settles.
        string saveDir = WriteSave("000001 - Game1", "end_m01aa",
            ("end_m01aa", [(0x2u, "sith", "n_sith", 23), (0x3u, "droid", "n_droid", 10)]));
        _port.SaveRequest(1, "Game1");
        SettleCapture(agent);

        Assert.StartsWith("'000001 - Game1': sidecar written, 1 module(s) proven (1 scaled creature(s))", agent.LastCaptureResult);
        EnemyScalingBaselineDocument document = ReadSidecar(saveDir);
        Assert.Equal(["end_m01aa"], document.CoveredModules);
        Assert.Equal(new EnemyScalingBaselineRecord("end_m01aa", 0x2, "sith", "n_sith", 15, 150), Assert.Single(document.Records));

        // Reload that exact save in-world: the provenance names it, the teardown bumped the
        // load serial, and the new world imports the baseline — the plugin will latch 0x2 and
        // never run a first-sighting multiplication on it.
        _port.Provenance = new LoadProvenance(4, 1, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 0, Encoding.ASCII.GetBytes("Game1"));
        _port.World(2, "end_m01aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 4, serial: 1, previous: 1, previousFinal: 2);
        TickUntilImportDrained(agent);

        Assert.Equal(EnemyScalingVerdict.Import, agent.WorldVerdict);
        EnemyScalingImportGate gate = _port.AssembleImport(2);
        Assert.Equal(EnemyScalingVerdict.Import, gate.Verdict);
        EnemyScalingLatchEntry imported = Assert.Single(gate.Entries.ToArray());
        Assert.Equal(new EnemyScalingLatchEntry(0x2, 15, 150), imported);

        ScaledObjectLatch latch = new(EnemyScalingBaselineChannel.LatchCapacity);
        Assert.True(latch.AddImported(imported.ObjectId, imported.BaseMaximum, imported.AppliedPercent));
        Assert.True(latch.Contains(0x2)); // a first sighting skips it
        Assert.Equal(EnemyScalingRescaleResult.Unchanged,
            EnemyScalingReconcileRule.Rescale(15, 150, 150, 23, 23, out _, out _)); // stored 23 is not 23*1.5
        Assert.False(latch.Contains(0x3)); // the unscaled 10-hp droid still scales once, fresh
    }

    [Fact]
    public void ASaveWithoutASidecarRefusesItsSavedModulesButNewModulesScale()
    {
        EnemyScalingPersistence agent = NewAgent();
        WriteSave("000002 - Old", "tar_m02aa", ("tar_m02aa", [(0x5u, "rakghoul", "n_rakghoul", 40)]), ("end_m01aa", []));
        _port.Provenance = new LoadProvenance(2, LoadProvenance.NoSaveSlot, LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, 0, Encoding.ASCII.GetBytes("000002 - Old"));

        _port.World(1, "tar_m02aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 2, flags: EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(1));

        _port.World(2, "tar_m03aa", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 1, previousFinal: 1);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Fresh, _port.LastVerdict(2));

        _port.World(3, "end_m01aa", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 2, previousFinal: 1);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(3));
    }

    [Fact]
    public void ARevisitedModuleImportsWhatItsLastVisitScaled()
    {
        EnemyScalingPersistence agent = NewAgent();
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Pending, 1);
        Tick(agent);
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 3,
            complete: true, records: [new(0x2, 15, 150), new(0x4, 12, 150)]);
        Tick(agent);

        _port.World(2, "end_m01ab", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 1, previousFinal: 3);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Fresh, _port.LastVerdict(2));

        _port.World(3, "end_m01aa", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 2, previousFinal: 1);
        TickUntilImportDrained(agent);
        Assert.Equal(EnemyScalingVerdict.Import, agent.WorldVerdict);
        Assert.Equal([new(0x2, 15, 150), new(0x4, 12, 150)], _port.AssembleImport(3).Entries.ToArray());
    }

    [Fact]
    public void AFinalExportTheAgentNeverCompletedMakesTheModuleUnknown()
    {
        EnemyScalingPersistence agent = NewAgent();
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Pending, 1);
        Tick(agent);
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 2,
            complete: true, records: [new(0x2, 15, 150)]);
        Tick(agent);

        // The plugin scaled one more enemy (export 3) just before the door; the agent never read it.
        _port.World(2, "end_m01ab", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 1, previousFinal: 3);
        Tick(agent);
        _port.World(3, "end_m01aa", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 2, previousFinal: 1);
        Tick(agent);

        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(3));
        Assert.Contains(_log, line => line.Contains("left without a complete final baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissedWorldLosesTheLineage()
    {
        EnemyScalingPersistence agent = NewAgent();
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Pending, 1);
        Tick(agent);
        _port.World(3, "end_m01ac", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 2, previousFinal: 1);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(3));
        Assert.Equal("Unknown", agent.Lineage);
    }

    [Fact]
    public void AnAgentThatStartsMidPlaythroughTrustsNothingUntilALoadOrNewGame()
    {
        EnemyScalingPersistence agent = NewAgent();
        _port.World(7, "tar_m02aa", EnemyScalingWorldOrigin.Transition, EnemyScalingHealthMode.Pending, 1,
            previous: 6, previousFinal: 4);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(7));
    }

    [Fact]
    public void ALoadRequestWithoutASuccessfulLoadEdgeIsNotBound()
    {
        EnemyScalingPersistence agent = NewAgent();
        WriteSave("000003 - Game3", "end_m01aa", ("end_m01aa", []));
        _port.Provenance = new LoadProvenance(6, 3, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 5, Encoding.ASCII.GetBytes("Game3"));

        // Same serial as the request and made with a world standing: no evidence the world
        // was actually replaced by that load.
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 6, serial: 5);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(1));
        Assert.Contains(_log, line => line.Contains("no successful-load edge", StringComparison.Ordinal));
    }

    [Fact]
    public void ALoadOfADifferentModulesArchiveIsNotBound()
    {
        EnemyScalingPersistence agent = NewAgent();
        WriteSave("000003 - Game3", "tar_m02aa", ("tar_m02aa", []));
        _port.Provenance = new LoadProvenance(6, 3, LoadProvenanceSource.ServerLoadRequest,
            LoadProvenanceStatus.Captured, 5, Encoding.ASCII.GetBytes("Game3"));
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 6, serial: 6);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(1));
    }

    [Fact]
    public void TheSidecarFollowsAByteIdenticalStagedCopy()
    {
        EnemyScalingPersistence agent = NewAgent();
        Tick(agent);
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 2,
            complete: true, records: [new(0x2, 15, 150)]);
        Tick(agent);
        string saveDir = WriteSave("000004 - Game4", "end_m01aa", ("end_m01aa", [(0x2u, "sith", "n_sith", 23)]));
        _port.SaveRequest(4, "Game4");
        SettleCapture(agent);

        // PluginMenuLoader stages every file of the save into 000001 - AUTOSAVE.
        string staged = Path.Combine(_saves, "000001 - AUTOSAVE");
        Directory.CreateDirectory(staged);
        foreach (string file in Directory.GetFiles(saveDir))
        {
            File.Copy(file, Path.Combine(staged, Path.GetFileName(file)));
        }

        _port.Provenance = new LoadProvenance(8, LoadProvenance.NoSaveSlot, LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, 0, Encoding.ASCII.GetBytes("000001 - AUTOSAVE"));
        _port.World(2, "end_m01aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 8, flags: EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld, previous: 1, previousFinal: 2);
        TickUntilImportDrained(agent);
        Assert.Equal(EnemyScalingVerdict.Import, agent.WorldVerdict);

        // Change one byte of the staged archive: the sidecar no longer describes it.
        byte[] bytes = File.ReadAllBytes(Path.Combine(staged, "SAVEGAME.sav"));
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(staged, "SAVEGAME.sav"), bytes);
        _port.Provenance = _port.Provenance with { Generation = 10 };
        _port.World(3, "end_m01aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 10, flags: EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld, previous: 2, previousFinal: 1);
        Tick(agent);
        Assert.Equal(EnemyScalingVerdict.Refuse, _port.LastVerdict(3));
    }

    /// <summary>A real save's GIT creature record has a Tag but no TemplateResRef (0 of 3,221
    /// across 40 GOG saves). Live 2026-09-24 the join refused every scaled module on that alone
    /// ("0xE has no usable saved tag/template"), so no save could ever prove its health.</summary>
    [Fact]
    public void TheJoinAcceptsARealSavedRecordWithNoTemplate()
    {
        EnemyScalingArchiveModule saved = new("danm14aa", new Dictionary<uint, EnemyScalingArchiveCreature>
        {
            [0xE] = new(0xE, "g_kathhound", string.Empty, 60),
        });

        List<EnemyScalingBaselineRecord> records = [];
        Assert.True(EnemyScalingArchiveJoin.TryJoinModule("danm14aa", [new(0xE, 40, 150)], saved, records, out _));
        EnemyScalingBaselineRecord record = Assert.Single(records);
        Assert.Equal(string.Empty, record.Template);
        Assert.True(EnemyScalingBaselineRule.IsValidRecord(record));
        Assert.False(EnemyScalingBaselineRule.IsValidTemplate(null));
        Assert.False(EnemyScalingBaselineRule.IsValidTemplate("bad name"));
    }

    [Fact]
    public void TheJoinDropsCreaturesScaledAfterTheStoreAndRefusesContradictions()
    {
        EnemyScalingArchiveModule saved = new("end_m01aa", new Dictionary<uint, EnemyScalingArchiveCreature>
        {
            [0x2] = new(0x2, "sith", "n_sith", 23),  // scaled before the store
            [0x3] = new(0x3, "droid", "n_droid", 10), // scaled after the store
        });

        List<EnemyScalingBaselineRecord> records = [];
        Assert.True(EnemyScalingArchiveJoin.TryJoinModule(
            "end_m01aa", [new(0x2, 15, 150), new(0x3, 10, 150), new(0x9, 8, 150)], saved, records, out _));
        Assert.Equal([new EnemyScalingBaselineRecord("end_m01aa", 0x2, "sith", "n_sith", 15, 150)], records);

        records.Clear();
        EnemyScalingArchiveModule contradicted = new("end_m01aa", new Dictionary<uint, EnemyScalingArchiveCreature>
        {
            [0x2] = new(0x2, "sith", "n_sith", 35), // 23 * 150%: compounded
        });
        Assert.False(EnemyScalingArchiveJoin.TryJoinModule("end_m01aa", [new(0x2, 15, 150)], contradicted, records, out _));
        Assert.Empty(records);

        Assert.False(EnemyScalingArchiveJoin.TryJoinModule(
            "end_m01aa", [new(0x2, 15, 150)], new EnemyScalingArchiveModule("end_m01aa", null), records, out _));
    }

    [Fact]
    public void AnUncoveredModuleIsLeftOutOfTheNextSidecar()
    {
        EnemyScalingPersistence agent = NewAgent();
        Tick(agent);
        WriteSave("000002 - Old", "tar_m02aa", ("tar_m02aa", [(0x5u, "rakghoul", "n_rakghoul", 40)]));
        _port.Provenance = new LoadProvenance(2, LoadProvenance.NoSaveSlot, LoadProvenanceSource.MainMenuDirectDirectory,
            LoadProvenanceStatus.CapturedDirectory, 0, Encoding.ASCII.GetBytes("000002 - Old"));
        _port.World(1, "tar_m02aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Pending, 1,
            provenance: 2, flags: EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld);
        Tick(agent);
        _port.World(1, "tar_m02aa", EnemyScalingWorldOrigin.Load, EnemyScalingHealthMode.Refused, 2,
            provenance: 2, flags: EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld);
        Tick(agent);

        string saveDir = WriteSave("000005 - Game5", "tar_m02aa", ("tar_m02aa", [(0x5u, "rakghoul", "n_rakghoul", 40)]));
        _port.SaveRequest(5, "Game5");
        SettleCapture(agent);

        Assert.Contains("0 module(s) proven", agent.LastCaptureResult, StringComparison.Ordinal);
        EnemyScalingBaselineDocument document = ReadSidecar(saveDir);
        Assert.Empty(document.CoveredModules!);
        Assert.Empty(document.Records);
    }

    [Fact]
    public void ASaveRequestAlreadyPublishedWhenTheAgentStartsIsNeverCaptured()
    {
        _port.SaveRequest(1, "Game1");
        EnemyScalingPersistence agent = NewAgent();
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 1, complete: true);
        WriteSave("000001 - Game1", "end_m01aa", ("end_m01aa", []));
        SettleCapture(agent);
        Assert.Equal("none", agent.LastCaptureResult);
    }

    [Fact]
    public void AnArchiveThatNeverSettlesIsAbandoned()
    {
        EnemyScalingPersistence agent = NewAgent();
        Tick(agent);
        _port.World(1, "end_m01aa", EnemyScalingWorldOrigin.NewGame, EnemyScalingHealthMode.Fresh, 1, complete: true);
        Tick(agent);
        _port.SaveRequest(9, "Never");
        for (int i = 0; i < 50; i++)
        {
            _now += 2000;
            agent.Tick(_now);
        }

        Assert.Contains("abandoned", agent.LastCaptureResult, StringComparison.Ordinal);
    }

    [Fact]
    public void TheArchiveReaderReadsTheStoredMaximumPerSavedModule()
    {
        string saveDir = WriteSave("000006 - Read", "end_m01aa",
            ("end_m01aa", [(0x2u, "sith", "n_sith", 23)]), ("tar_m02aa", [(0x7u, "duros", "n_duros", 8)]));
        Assert.True(EnemyScalingArchiveReader.TryRead(saveDir, out EnemyScalingArchiveContents? contents, out _));
        Assert.Equal(2, contents!.Modules.Count);
        Assert.Equal(23, contents.Modules["end_m01aa"].Creatures![0x2].HitPoints);
        Assert.Equal("n_duros", contents.Modules["TAR_M02AA"].Creatures![0x7].Template);
        Assert.True(EnemyScalingBaselineRule.IsValidHash(contents.Sha256));
    }

    // ---- helpers ------------------------------------------------------------------------

    private void Tick(EnemyScalingPersistence agent)
    {
        _now += 150;
        agent.Tick(_now);
    }

    private void TickUntilImportDrained(EnemyScalingPersistence agent)
    {
        for (int i = 0; i < 20; i++)
        {
            Tick(agent);
        }
    }

    private void SettleCapture(EnemyScalingPersistence agent)
    {
        for (int i = 0; i < 30; i++)
        {
            _now += 200;
            agent.Tick(_now);
        }
    }

    private static EnemyScalingBaselineDocument ReadSidecar(string saveDir)
    {
        Assert.True(SaveDigest.TryComputeSaveIdentity(saveDir, out uint identity));
        Assert.True(EnemyScalingBaselineStore.TryReadForArchiveContent(
            Path.Combine(saveDir, EnemyScalingBaselineStore.SidecarFileName),
            Path.Combine(saveDir, "SAVEGAME.sav"), identity, out EnemyScalingBaselineDocument? document, out string diagnostic),
            diagnostic);
        return document!;
    }

    private string WriteSave(
        string directoryName,
        string activeModule,
        params (string Module, (uint Id, string Tag, string Template, int HitPoints)[] Creatures)[] modules)
    {
        string dir = Path.Combine(_saves, directoryName);
        Directory.CreateDirectory(dir);

        List<(string, int, byte[])> members = [("GLOBALVARS", 0, [0xAA, 0xBB])];
        foreach ((string module, (uint Id, string Tag, string Template, int HitPoints)[] creatures) in modules)
        {
            GffWriter ifo = new("IFO ");
            ifo.Root.SetDword("Mod_NextObjId0", 300);
            ifo.Root.SetDword("Mod_NextObjId1", 0);

            GffWriter git = new("GIT ");
            GffWriterList list = git.Root.AddList("Creature List");
            foreach ((uint id, string tag, string template, int hitPoints) in creatures)
            {
                list.Add(4)
                    .SetDword("ObjectId", id)
                    .SetString("Tag", tag)
                    .SetResRef("TemplateResRef", template)
                    .SetShort("HitPoints", (short)hitPoints);
            }

            byte[] inner = BuildErf("SAV ", ("Module", 2014, ifo.ToBytes()), (module, 2023, git.ToBytes()));
            members.Add((module, 2057, inner));
        }

        File.WriteAllBytes(Path.Combine(dir, "SAVEGAME.sav"), BuildErf("SAV ", members.ToArray()));
        GffWriter nfo = new("NFO ");
        nfo.Root.SetString("LASTMODULE", activeModule);
        File.WriteAllBytes(Path.Combine(dir, "savenfo.res"), nfo.ToBytes());
        return dir;
    }

    private static byte[] BuildErf(string fileType, params (string Resref, int Type, byte[] Data)[] members)
    {
        int count = members.Length;
        int keysOffset = 160;
        int valuesOffset = keysOffset + (24 * count);
        int dataOffset = valuesOffset + (8 * count);

        byte[] file = new byte[dataOffset + members.Sum(m => m.Data.Length)];
        Encoding.ASCII.GetBytes(fileType.PadRight(4)).CopyTo(file, 0);
        Encoding.ASCII.GetBytes("V1.0").CopyTo(file, 4);
        BitConverter.GetBytes((uint)count).CopyTo(file, 0x10);
        BitConverter.GetBytes((uint)keysOffset).CopyTo(file, 0x18);
        BitConverter.GetBytes((uint)valuesOffset).CopyTo(file, 0x1C);

        int offset = dataOffset;
        for (int i = 0; i < count; i++)
        {
            (string resref, int type, byte[] data) = members[i];
            Encoding.ASCII.GetBytes(resref).CopyTo(file, keysOffset + (24 * i));
            BitConverter.GetBytes((uint)i).CopyTo(file, keysOffset + (24 * i) + 16);
            file[keysOffset + (24 * i) + 20] = (byte)type;
            file[keysOffset + (24 * i) + 21] = (byte)(type >> 8);
            BitConverter.GetBytes((uint)offset).CopyTo(file, valuesOffset + (8 * i));
            BitConverter.GetBytes((uint)data.Length).CopyTo(file, valuesOffset + (8 * i) + 4);
            data.CopyTo(file, offset);
            offset += data.Length;
        }

        return file;
    }

    /// <summary>The plugin half, faked: one published world page, an import mailbox the
    /// "plugin" acknowledges as soon as it is written, a save mailbox and the provenance.</summary>
    private sealed class FakePort : IEnemyScalingBaselinePort
    {
        private uint _worldSequence;
        private uint _importSequence;
        private uint _saveSequence;
        private EnemyScalingWorldPage? _page;
        private EnemyScalingSaveRequest? _save;

        public LoadProvenance? Provenance { get; set; }

        public List<(uint Generation, EnemyScalingVerdict Verdict, int Page, int Pages, int Total, EnemyScalingLatchEntry[] Records)> Imports { get; } = [];

        public void World(
            uint generation,
            string module,
            EnemyScalingWorldOrigin origin,
            EnemyScalingHealthMode mode,
            uint exportGeneration,
            bool complete = false,
            EnemyScalingLatchEntry[]? records = null,
            uint provenance = 0,
            uint flags = 0,
            uint serial = 0,
            uint previous = 0,
            uint previousFinal = 0)
        {
            _worldSequence += 2;
            _page = new EnemyScalingWorldPage(
                _worldSequence, generation, origin, provenance, flags, serial, mode, exportGeneration,
                previous, previousFinal, 0, 1, complete, false, module, records ?? []);
        }

        public void SaveRequest(int slot, string name)
        {
            _saveSequence += 2;
            _save = new EnemyScalingSaveRequest(_saveSequence, slot, 1, DateTime.UtcNow.AddSeconds(-1).ToFileTimeUtc(), name);
        }

        public EnemyScalingVerdict LastVerdict(uint generation) =>
            Imports.LastOrDefault(x => x.Generation == generation).Verdict;

        public EnemyScalingImportGate AssembleImport(uint generation)
        {
            EnemyScalingImportGate gate = new();
            gate.Reset(generation);
            byte[] payload = new byte[EnemyScalingBaselineChannel.ImportPayloadLength];
            foreach (var import in Imports.Where(x => x.Generation == generation))
            {
                EnemyScalingBaselineChannel.EncodeImportPage(
                    payload, import.Generation, import.Verdict, import.Page, import.Pages, import.Total, import.Records);
                gate.Accept(payload);
            }

            return gate;
        }

        public bool TryReadEnemyScalingWorldPage(out EnemyScalingWorldPage? page)
        {
            page = _page;
            return page is not null;
        }

        public void AcknowledgeEnemyScalingExport(uint sequence)
        {
        }

        public bool TryReadEnemyScalingImportAck(out uint sequence)
        {
            sequence = _importSequence;
            return true;
        }

        public bool TryWriteEnemyScalingImportPage(
            uint worldGeneration, EnemyScalingVerdict verdict, int pageIndex, int pageCount, int totalRecords,
            ReadOnlySpan<EnemyScalingLatchEntry> records, out uint sequence)
        {
            _importSequence += 2;
            sequence = _importSequence;
            Imports.Add((worldGeneration, verdict, pageIndex, pageCount, totalRecords, records.ToArray()));
            return true;
        }

        public bool TryReadEnemyScalingSaveRequest(out EnemyScalingSaveRequest? request)
        {
            request = _save;
            return request is not null;
        }

        public bool TryReadLoadProvenance(out LoadProvenance? provenance)
        {
            provenance = Provenance;
            return provenance is not null;
        }
    }
}
