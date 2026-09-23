using KotorCoop.Protocol;
using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class EnemyScalingBaselineTests
{
    private static readonly EnemyScalingSaveKey SaveKey = new(0, "000001 - AUTOSAVE", 0x1234_5678);
    private static readonly EnemyScalingBaselineRecord Record = new(
        "lev_m40aa", 0x2A, "sithsoldier001", "n_sithsoldier001", 15, 150);
    private const string ArchiveHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void MatchingSavedMaximumSelectsVanillaBaselineAndDoesNotCompound()
    {
        EnemyScalingBaselineDocument document = Document(Record);

        Assert.True(EnemyScalingBaselineRule.TrySelect(
            document, SaveKey, ArchiveHash, "LEV_M40AA", 0x2A,
            "sithsoldier001", "N_SITHSOLDIER001", storedMaximum: 23, out var selected));
        Assert.Equal(Record, selected);

        Assert.Equal(23, EnemyScalingBaselineRule.ScaleMaximum(selected!.BaseMaximum, selected.AppliedPercent));
        Assert.Equal(30, EnemyScalingBaselineRule.ScaleMaximum(selected.BaseMaximum, 200));
        Assert.NotEqual(35, EnemyScalingBaselineRule.ScaleMaximum(selected.BaseMaximum, 150));
    }

    [Theory]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 1, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000002 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x87654321u, "lev_m40aa", 0x2Au, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40ab", 0x2Au, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Bu, "sithsoldier001", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "different-tag", "n_sithsoldier001", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "sithsoldier001", "different_template", 23)]
    [InlineData(ArchiveHash, 0, "000001 - AUTOSAVE", 0x12345678u, "lev_m40aa", 0x2Au, "sithsoldier001", "n_sithsoldier001", 24)]
    public void RejectsAnySaveOrCreatureIdentityMismatch(
        string hash,
        int slot,
        string name,
        uint saveIdentity,
        string module,
        uint objectId,
        string tag,
        string template,
        int storedMaximum)
    {
        Assert.False(EnemyScalingBaselineRule.TrySelect(
            Document(Record),
            new EnemyScalingSaveKey(slot, name, saveIdentity),
            hash,
            module,
            objectId,
            tag,
            template,
            storedMaximum,
            out var selected));
        Assert.Null(selected);
    }

    [Fact]
    public void RejectsDuplicateCreatureIdentityAndInvalidPercent()
    {
        Assert.False(EnemyScalingBaselineRule.IsValidDocument(
            Document(Record, Record with { Tag = "other-tag" }), requireHash: true));
        Assert.False(EnemyScalingBaselineRule.IsValidRecord(Record with { AppliedPercent = 401 }));
        Assert.False(EnemyScalingBaselineRule.IsValidRecord(Record with { BaseMaximum = 0 }));
    }

    [Fact]
    public void MaximumArithmeticMatchesTheNativeScalingRule()
    {
        int[] maxima = [1, 2, 15, 23, 32_767, ushort.MaxValue];
        int[] percents = [50, 100, 115, 150, 200, 250, 400];
        foreach (int maximum in maxima)
        {
            foreach (int percent in percents)
            {
                EnemyScalingRule.ScaledHitPoints(maximum, maximum, percent,
                    out _, out int nativeMaximum);
                Assert.Equal(nativeMaximum, EnemyScalingBaselineRule.ScaleMaximum(maximum, percent));
            }
        }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x7F000000u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(uint.MaxValue)]
    public void RejectsNonEnemySentinelsEvenWhenSavedAndRequestedIdentitiesAgree(uint objectId)
    {
        EnemyScalingBaselineRecord record = Record with { ObjectId = objectId };
        Assert.False(EnemyScalingBaselineRule.IsValidRecord(record));
        Assert.False(EnemyScalingBaselineRule.IsValidDocument(Document(record), requireHash: true));
        Assert.False(EnemyScalingBaselineRule.TrySelect(
            Document(record), SaveKey, ArchiveHash, record.Module, objectId,
            record.Tag, record.Template, storedMaximum: 23, out var selected));
        Assert.Null(selected);
    }

    private static EnemyScalingBaselineDocument Document(params EnemyScalingBaselineRecord[] records) =>
        new(EnemyScalingBaselineDocument.CurrentSchemaVersion,
            SaveKey.Slot, SaveKey.Name, SaveKey.SaveIdentity, ArchiveHash, records);
}

public sealed class EnemyScalingBaselineStoreTests
{
    private static readonly EnemyScalingSaveKey SaveKey = new(2, "natural-lev-rescue", 0x2468_1357);

    [Fact]
    public void WritesAndReadsOnlyForTheExactArchiveAndSaveKey()
    {
        using TemporaryDirectory temp = new();
        string archive = temp.PathFor("SAVEGAME.sav");
        string sidecar = temp.PathFor("kotorcoop-enemy-health.json");
        File.WriteAllBytes(archive, [1, 2, 3, 4, 5, 6]);
        EnemyScalingBaselineRecord record = new("lev_m40aa", 42, "guard", "n_sithsoldier001", 15, 150);

        Assert.True(EnemyScalingBaselineStore.TryWriteForArchive(sidecar, archive, SaveKey, [record], out string writeDiagnostic), writeDiagnostic);
        Assert.True(EnemyScalingBaselineStore.TryReadForArchive(sidecar, archive, SaveKey, out var read, out string readDiagnostic), readDiagnostic);
        Assert.NotNull(read);
        Assert.True(EnemyScalingBaselineRule.IsValidHash(read!.ArchiveSha256));
        Assert.Equal(record, Assert.Single(read.Records));

        Assert.False(EnemyScalingBaselineStore.TryReadForArchive(
            sidecar, archive, SaveKey with { SaveIdentity = 0xDEAD_BEEFu }, out var mismatchedKey, out _));
        Assert.Null(mismatchedKey);

        File.WriteAllBytes(archive, [1, 2, 3, 4, 5, 7]);
        Assert.False(EnemyScalingBaselineStore.TryReadForArchive(sidecar, archive, SaveKey, out var mismatchedArchive, out string diagnostic));
        Assert.Null(mismatchedArchive);
        Assert.Equal("save archive content mismatch", diagnostic);
    }

    [Fact]
    public void RefusesMalformedAndUnknownSchemaSidecars()
    {
        using TemporaryDirectory temp = new();
        string archive = temp.PathFor("SAVEGAME.sav");
        string sidecar = temp.PathFor("kotorcoop-enemy-health.json");
        File.WriteAllBytes(archive, [8, 7, 6]);
        Assert.True(EnemyScalingBaselineStore.TryWriteForArchive(
            sidecar, archive, SaveKey,
            [new("lev_m40aa", 42, "guard", "n_sithsoldier001", 15, 150)], out _));

        string valid = File.ReadAllText(sidecar);
        File.WriteAllText(sidecar, valid.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":99", StringComparison.Ordinal));
        Assert.False(EnemyScalingBaselineStore.TryReadForArchive(sidecar, archive, SaveKey, out var unsupported, out _));
        Assert.Null(unsupported);

        File.WriteAllText(sidecar, valid.TrimEnd('}') + ",\"Unexpected\":true}");
        Assert.False(EnemyScalingBaselineStore.TryReadForArchive(sidecar, archive, SaveKey, out var unknownField, out _));
        Assert.Null(unknownField);

        File.WriteAllText(sidecar, valid.Replace(
            "\"SchemaVersion\":1,",
            "\"SchemaVersion\":1,\"SchemaVersion\":1,",
            StringComparison.Ordinal));
        Assert.False(EnemyScalingBaselineStore.TryReadForArchive(sidecar, archive, SaveKey, out var duplicateField, out _));
        Assert.Null(duplicateField);
    }

    [Fact]
    public void InvalidRecordsDoNotReplaceAnExistingSidecar()
    {
        using TemporaryDirectory temp = new();
        string archive = temp.PathFor("SAVEGAME.sav");
        string sidecar = temp.PathFor("kotorcoop-enemy-health.json");
        File.WriteAllBytes(archive, [9, 8, 7]);
        EnemyScalingBaselineRecord valid = new("lev_m40aa", 42, "guard", "n_sithsoldier001", 15, 150);
        byte[] archiveBefore = File.ReadAllBytes(archive);
        Assert.False(EnemyScalingBaselineStore.TryWriteForArchive(archive, archive, SaveKey, [valid], out _));
        Assert.Equal(archiveBefore, File.ReadAllBytes(archive));

        Assert.True(EnemyScalingBaselineStore.TryWriteForArchive(sidecar, archive, SaveKey, [valid], out _));
        byte[] before = File.ReadAllBytes(sidecar);

        Assert.False(EnemyScalingBaselineStore.TryWriteForArchive(
            sidecar, archive, SaveKey, [valid with { AppliedPercent = 0 }], out _));
        Assert.Equal(before, File.ReadAllBytes(sidecar));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "kotorcoop-scaling-baseline-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string PathFor(string name) => Path.Combine(_path, name);

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
