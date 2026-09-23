using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class EnemyScalingBaselineCollectorTests
{
    [Fact]
    public void RequiresAnExpectedModuleAndAcceptsACompleteEmptySnapshot()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.False(collector.TryFreeze(out _));

        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.False(collector.TryFreeze(out _));
        Assert.True(collector.Accept(Batch(
            "end_m01aa", 1, 1, 0, 1, Array.Empty<EnemyScalingLatchRecord>(), complete: true)));
        Assert.True(collector.Accept(Batch(
            "end_m01aa", 1, 1, 0, 1, Array.Empty<EnemyScalingLatchRecord>(), complete: true)));

        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot));
        Assert.Empty(snapshot!.Records);
    }

    [Fact]
    public void CommitsPagesOutOfOrderOnlyWhenWholeCompleteExportArrivesAndPreservesOtherModules()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        EnemyScalingLatchRecord[] first = Records(3, 1);
        EnemyScalingLatchRecord[] second = Records(2, 40);
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 2, 1, 2, second)));
        Assert.False(collector.TryFreeze(out _));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 2, 0, 2, first)));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? firstSnapshot));
        Assert.Equal(5, firstSnapshot!.Records.Count);

        Assert.True(collector.ExpectModule("tar_m02aa", 2));
        Assert.False(collector.TryFreeze(out _));
        Assert.True(collector.Accept(Batch("tar_m02aa", 2, 1, 0, 1, Records(1, 80))));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? merged));
        Assert.Equal(6, merged!.Records.Count);
        Assert.Contains(merged.Records, r => r.Module == "end_m01aa" && r.ObjectId == 1);
        Assert.Contains(merged.Records, r => r.Module == "tar_m02aa" && r.ObjectId == 80);
    }

    [Fact]
    public void NewModuleGenerationReplacesThatModuleSnapshotAndDoesNotMergeReusedIds()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 1, 0, 1, Records(2, 5))));
        Assert.True(collector.ExpectModule("end_m01aa", 2));
        Assert.False(collector.TryFreeze(out _));

        // Same object id in a new module epoch is the current full snapshot, not the old creature.
        Assert.True(collector.Accept(Batch("end_m01aa", 2, 1, 0, 1, Records(1, 5, baseMaximum: 30))));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot));
        Assert.Single(snapshot!.Records);
        Assert.Equal(30, snapshot.Records[0].BaseMaximum);
    }

    [Fact]
    public void StaleExactLoadSessionAndOldModuleGenerationDoNotPoisonCurrentSnapshot()
    {
        EnemyScalingBaselineCollector collector = NewCollector(session: 9);
        Assert.True(collector.ExpectModule("end_m01aa", 2));
        Assert.False(collector.Accept(Batch("end_m01aa", 2, 1, 0, 1, Records(1, 1), session: 8)));
        Assert.False(collector.Accept(Batch("end_m01aa", 1, 1, 0, 1, Records(1, 2), session: 9)));
        Assert.True(collector.Accept(Batch("end_m01aa", 2, 2, 0, 1, Records(1, 3), session: 9)));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot));
        Assert.Equal(3u, snapshot!.Records[0].ObjectId);
    }

    [Fact]
    public void IncompleteSnapshotBlocksFreezeUntilSupersededByCompleteExport()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 1, 0, 1, Records(1, 1), complete: false)));
        Assert.False(collector.TryFreeze(out _));

        Assert.True(collector.Accept(Batch("end_m01aa", 1, 2, 0, 1, Records(1, 2), complete: true)));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot));
        Assert.Single(snapshot!.Records);
        Assert.Equal(2u, snapshot.Records[0].ObjectId);
    }

    [Fact]
    public void ConflictingDuplicatePagePoisonsCurrentExportUntilNewerCompleteExport()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 1, 0, 2, Records(1, 1))));
        Assert.False(collector.Accept(Batch("end_m01aa", 1, 1, 0, 2, Records(1, 2))));
        Assert.False(collector.TryFreeze(out _));
        Assert.False(collector.Accept(Batch("end_m01aa", 1, 1, 1, 2, Records(1, 3))));

        Assert.True(collector.Accept(Batch("end_m01aa", 1, 2, 0, 1, Records(1, 4))));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? snapshot));
        Assert.Equal(4u, snapshot!.Records[0].ObjectId);
    }

    [Fact]
    public void RejectsDuplicateIdsAcrossPagesAndMalformedCapacityWithoutFreezing()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 1, 0, 2, Records(1, 7))));
        Assert.False(collector.Accept(Batch("end_m01aa", 1, 1, 1, 2, Records(1, 7))));
        Assert.False(collector.TryFreeze(out _));

        Assert.False(collector.Accept(Batch("end_m01aa", 1, 2, 0, 5, Records(1, 1))));
        Assert.False(collector.TryFreeze(out _));
    }

    [Fact]
    public void AggregateCapacityOverflowPoisonsTheOverflowingModuleOnlyUntilSuperseded()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        for (int moduleIndex = 0; moduleIndex < 64; moduleIndex++)
        {
            string module = $"m{moduleIndex:000}";
            Assert.True(collector.ExpectModule(module, 1));
            Assert.True(collector.Accept(Batch(module, 1, 1, 0, 4, Records(64, (uint)(moduleIndex * 300 + 1)))));
            Assert.True(collector.Accept(Batch(module, 1, 1, 1, 4, Records(64, (uint)(moduleIndex * 300 + 65)))));
            Assert.True(collector.Accept(Batch(module, 1, 1, 2, 4, Records(64, (uint)(moduleIndex * 300 + 129)))));
            Assert.True(collector.Accept(Batch(module, 1, 1, 3, 4, Records(64, (uint)(moduleIndex * 300 + 193)))));
        }

        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? full));
        Assert.Equal(EnemyScalingBaselineCollector.MaximumTotalRecords, full!.Records.Count);

        Assert.True(collector.ExpectModule("overflow", 1));
        Assert.False(collector.Accept(Batch("overflow", 1, 1, 0, 1, Records(1, 50))));
        Assert.False(collector.TryFreeze(out _));
        Assert.True(collector.Accept(Batch("overflow", 1, 2, 0, 1, Array.Empty<EnemyScalingLatchRecord>())));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? replaced));
        Assert.Equal(EnemyScalingBaselineCollector.MaximumTotalRecords, replaced!.Records.Count);
    }

    [Fact]
    public void ResetForExactLoadClearsVisitedModulesAndImmutableSnapshotIsDetached()
    {
        EnemyScalingBaselineCollector collector = NewCollector(session: 10);
        Assert.True(collector.ExpectModule("end_m01aa", 1));
        Assert.True(collector.Accept(Batch("end_m01aa", 1, 1, 0, 1, Records(1, 1), session: 10)));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? oldSnapshot));
        Assert.True(collector.ResetForLoadSession(11));
        Assert.False(collector.TryFreeze(out _));

        Assert.True(collector.ExpectModule("tar_m02aa", 1));
        Assert.True(collector.Accept(Batch("tar_m02aa", 1, 1, 0, 1, Records(1, 5), session: 11)));
        Assert.True(collector.TryFreeze(out EnemyScalingBaselineCollectionSnapshot? current));
        Assert.Equal(11ul, current!.ExactLoadSessionId);
        Assert.Single(current.Records);
        Assert.Equal("tar_m02aa", current.Records[0].Module);
        Assert.Equal("end_m01aa", oldSnapshot!.Records[0].Module);
        Assert.Throws<NotSupportedException>(() => ((IList<CollectedEnemyScalingBaseline>)current.Records)[0] = default);
    }

    [Fact]
    public void ModuleCapacityOverflowAndUnknownResetClearFailClosedUntilValidReset()
    {
        EnemyScalingBaselineCollector collector = NewCollector();
        for (int i = 0; i < EnemyScalingBaselineCollector.MaximumModules; i++)
        {
            string module = $"m{i:000}";
            Assert.True(collector.ExpectModule(module, 1));
            Assert.True(collector.Accept(Batch(module, 1, 1, 0, 1, Array.Empty<EnemyScalingLatchRecord>())));
        }

        Assert.True(collector.TryFreeze(out _));
        Assert.False(collector.ExpectModule("overflow", 1));
        Assert.False(collector.TryFreeze(out _));
        Assert.False(collector.ResetForLoadSession(0));
        Assert.False(collector.TryFreeze(out _));
        Assert.True(collector.ResetForLoadSession(2));
        Assert.False(collector.TryFreeze(out _));
    }

    private static EnemyScalingBaselineCollector NewCollector(ulong session = 1)
    {
        EnemyScalingBaselineCollector collector = new();
        Assert.True(collector.ResetForLoadSession(session));
        return collector;
    }

    private static EnemyScalingLatchBatch Batch(
        string module,
        ulong moduleGeneration,
        ulong exportGeneration,
        int pageIndex,
        int pageCount,
        IReadOnlyList<EnemyScalingLatchRecord> records,
        bool complete = true,
        ulong session = 1) => new(
            session, module, moduleGeneration, exportGeneration, pageIndex, pageCount, complete, records);

    private static EnemyScalingLatchRecord[] Records(int count, uint firstId, int baseMaximum = 20)
    {
        EnemyScalingLatchRecord[] result = new EnemyScalingLatchRecord[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new EnemyScalingLatchRecord(firstId + (uint)i, baseMaximum, 150);
        }

        return result;
    }
}
