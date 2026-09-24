using System.Runtime.InteropServices;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Shared-block version 76: the enemy-health baseline channel's layout, and a round trip of
/// every channel through the REAL plugin writer/reader (<see cref="SharedBlockView"/>) and the
/// agent's decoder over one native buffer, so neither half can drift from the other.
/// </summary>
public sealed unsafe class EnemyScalingBaselineChannelTests
{
    [Fact]
    public void ChannelFitsTheReservedRegionWithoutOverlap()
    {
        Assert.Equal(0x5800, SharedBlock.TotalSize);
        Assert.Equal(EnemyScalingBaselineChannel.RegionEnd, SharedBlock.TotalSize);
        Assert.Equal(EnemyScalingBaselineChannel.OffsetExportSequence, SharedBlock.OffsetEnemyBaselineExportSequence);
        Assert.Equal(EnemyScalingBaselineChannel.OffsetExportAck, SharedBlock.OffsetEnemyBaselineExportAck);
        Assert.Equal(EnemyScalingBaselineChannel.OffsetImportSequence, SharedBlock.OffsetEnemyBaselineImportSequence);
        Assert.Equal(EnemyScalingBaselineChannel.OffsetImportAck, SharedBlock.OffsetEnemyBaselineImportAck);
        Assert.Equal(EnemyScalingBaselineChannel.OffsetSaveSequence, SharedBlock.OffsetEnemyBaselineSaveSequence);

        (int Start, int End)[] ranges =
        [
            (EnemyScalingBaselineChannel.OffsetExportSequence, EnemyScalingBaselineChannel.ExportPayloadStart + EnemyScalingBaselineChannel.ExportPayloadLength),
            (EnemyScalingBaselineChannel.OffsetExportAck, EnemyScalingBaselineChannel.OffsetExportAck + 4),
            (EnemyScalingBaselineChannel.OffsetImportSequence, EnemyScalingBaselineChannel.ImportPayloadStart + EnemyScalingBaselineChannel.ImportPayloadLength),
            (EnemyScalingBaselineChannel.OffsetImportAck, EnemyScalingBaselineChannel.OffsetImportAck + 4),
            (EnemyScalingBaselineChannel.OffsetSaveSequence, EnemyScalingBaselineChannel.SavePayloadStart + EnemyScalingBaselineChannel.SavePayloadLength),
        ];

        for (int i = 0; i < ranges.Length; i++)
        {
            Assert.True(ranges[i].Start >= 0x5400 && ranges[i].End <= 0x5800, $"range {i} outside the reservation");
            Assert.True(ranges[i].Start % 4 == 0, $"range {i} not dword aligned");
            for (int j = i + 1; j < ranges.Length; j++)
            {
                Assert.True(ranges[i].End <= ranges[j].Start || ranges[j].End <= ranges[i].Start, $"ranges {i} and {j} overlap");
            }
        }

        Assert.Equal(0x5640, EnemyScalingBaselineChannel.ExportPayloadStart + EnemyScalingBaselineChannel.ExportPayloadLength);
        Assert.Equal(0x5760, EnemyScalingBaselineChannel.ImportPayloadStart + EnemyScalingBaselineChannel.ImportPayloadLength);
        Assert.Equal(0x57C0, EnemyScalingBaselineChannel.SavePayloadStart + EnemyScalingBaselineChannel.SavePayloadLength);
        Assert.Equal(0, EnemyScalingBaselineChannel.OffsetSaveFileTime % 8);
    }

    [Theory]
    [InlineData(7, "Game7", "000007 - Game7")]
    [InlineData(0, "QUICKSAVE", "000000 - QUICKSAVE")]
    [InlineData(-1, "000001 - AUTOSAVE", "000001 - AUTOSAVE")]
    public void DirectoryNamesFollowTheEngineFormat(int slot, string name, string expected) =>
        Assert.Equal(expected, EnemyScalingBaselineChannel.SaveDirectoryName(slot, name));

    [Fact]
    public void PluginWorldPageRoundTripsThroughTheAgentDecoder()
    {
        using NativeBlock block = new();
        EnemyScalingLatchEntry[] records = [new(0x2A, 15, 150), new(0x2B, 12, 150)];
        uint sequence = block.View.PublishEnemyBaselinePage(
            0, 5, EnemyScalingWorldOrigin.Load, 6, EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld, 3,
            EnemyScalingHealthMode.Imported, 4, 4, 9, 0, 1, EnemyScalingBaselineChannel.ExportFlagSnapshotComplete,
            "end_m01aa"u8, records);

        Assert.Equal(2u, sequence);
        Assert.True(block.TryReadWorldPage(out EnemyScalingWorldPage? page));
        Assert.Equal(5u, page!.WorldGeneration);
        Assert.Equal(EnemyScalingWorldOrigin.Load, page.Origin);
        Assert.Equal(6u, page.OriginProvenanceGeneration);
        Assert.Equal(EnemyScalingBaselineChannel.OriginFlagRequestWithoutWorld, page.OriginFlags);
        Assert.Equal(3u, page.WorldLoadSerialAtWorldUp);
        Assert.Equal(EnemyScalingHealthMode.Imported, page.Mode);
        Assert.Equal(4u, page.ExportGeneration);
        Assert.Equal(4u, page.PreviousWorldGeneration);
        Assert.Equal(9u, page.PreviousFinalExportGeneration);
        Assert.True(page.SnapshotComplete);
        Assert.False(page.Overflow);
        Assert.Equal("end_m01aa", page.Module);
        Assert.Equal(records, page.Records);

        // A second publish advances the seqlock; a torn (odd) sequence is never decoded.
        Assert.Equal(4u, block.View.PublishEnemyBaselinePage(
            sequence, 5, EnemyScalingWorldOrigin.Load, 6, 0, 3, EnemyScalingHealthMode.Refused, 5, 4, 9, 0, 1, 0,
            "end_m01aa"u8, []));
        block.WriteU32(EnemyScalingBaselineChannel.OffsetExportSequence, 5);
        Assert.False(block.TryReadWorldPage(out _));
    }

    [Fact]
    public void AgentImportPagesAssembleInThePluginGate()
    {
        using NativeBlock block = new();
        EnemyScalingLatchEntry[] all = Enumerable.Range(1, 40)
            .Select(i => new EnemyScalingLatchEntry((uint)(0x100 + i), 10 + i, 150))
            .ToArray();
        EnemyScalingImportGate gate = new();
        gate.Reset(9);
        byte[] payload = new byte[EnemyScalingBaselineChannel.ImportPayloadLength];

        int pages = EnemyScalingBaselineChannel.ImportPageCount(all.Length);
        Assert.Equal(2, pages);
        EnemyScalingImportStep step = EnemyScalingImportStep.Ignored;
        for (int page = 0; page < pages; page++)
        {
            int start = page * EnemyScalingBaselineChannel.ImportRecordsPerPage;
            int count = Math.Min(EnemyScalingBaselineChannel.ImportRecordsPerPage, all.Length - start);
            block.WriteImport(9, EnemyScalingVerdict.Import, page, pages, all.Length, all.AsSpan(start, count));
            Assert.True(block.View.TryReadEnemyBaselineImport(payload, out _));
            step = gate.Accept(payload);
        }

        Assert.Equal(EnemyScalingImportStep.Complete, step);
        Assert.Equal(EnemyScalingVerdict.Import, gate.Verdict);
        Assert.Equal(all, gate.Entries.ToArray());
    }

    [Fact]
    public void PluginSaveRequestRoundTripsThroughTheAgentDecoder()
    {
        using NativeBlock block = new();
        long now = DateTime.UtcNow.ToFileTimeUtc();
        uint sequence = block.View.PublishEnemyBaselineSaveRequest(0, 7, 3, now, "Game7"u8);
        Assert.Equal(2u, sequence);
        Assert.True(block.TryReadSaveRequest(out EnemyScalingSaveRequest? request));
        Assert.Equal(7, request!.Slot);
        Assert.Equal(3u, request.WorldGeneration);
        Assert.Equal(now, request.RequestFileTimeUtc);
        Assert.Equal("000007 - Game7", request.DirectoryName);

        block.View.PublishEnemyBaselineSaveRequest(sequence, 1, 3, now, "bad/name"u8);
        Assert.False(block.TryReadSaveRequest(out _));
    }

    /// <summary>A zeroed native block the size of the real one, with the agent-side reads
    /// done exactly as <c>KotorGameBridge.EnemyScalingBaseline.cs</c> does them.</summary>
    private sealed class NativeBlock : IDisposable
    {
        private readonly nint _memory = Marshal.AllocHGlobal(SharedBlock.TotalSize);

        public NativeBlock()
        {
            new Span<byte>((void*)_memory, SharedBlock.TotalSize).Clear();
            View = new SharedBlockView((byte*)_memory);
        }

        internal SharedBlockView View { get; }

        public void WriteU32(int offset, uint value) => *(uint*)(_memory + offset) = value;

        public bool TryReadWorldPage(out EnemyScalingWorldPage? page)
        {
            uint before = *(uint*)(_memory + EnemyScalingBaselineChannel.OffsetExportSequence);
            ReadOnlySpan<byte> payload = new((void*)(_memory + EnemyScalingBaselineChannel.ExportPayloadStart),
                EnemyScalingBaselineChannel.ExportPayloadLength);
            uint after = *(uint*)(_memory + EnemyScalingBaselineChannel.OffsetExportSequence);
            return EnemyScalingBaselineChannel.TryDecodeWorldPage(before, payload, after, out page);
        }

        public bool TryReadSaveRequest(out EnemyScalingSaveRequest? request)
        {
            uint before = *(uint*)(_memory + EnemyScalingBaselineChannel.OffsetSaveSequence);
            ReadOnlySpan<byte> payload = new((void*)(_memory + EnemyScalingBaselineChannel.SavePayloadStart),
                EnemyScalingBaselineChannel.SavePayloadLength);
            return EnemyScalingBaselineChannel.TryDecodeSaveRequest(before, payload, before, out request);
        }

        private uint _importSequence;

        public void WriteImport(
            uint generation, EnemyScalingVerdict verdict, int page, int pages, int total, ReadOnlySpan<EnemyScalingLatchEntry> records)
        {
            uint published = EnemyScalingBaselineChannel.NextSequence(_importSequence, out uint odd);
            WriteU32(EnemyScalingBaselineChannel.OffsetImportSequence, odd);
            EnemyScalingBaselineChannel.EncodeImportPage(
                new Span<byte>((void*)(_memory + EnemyScalingBaselineChannel.ImportPayloadStart), EnemyScalingBaselineChannel.ImportPayloadLength),
                generation, verdict, page, pages, total, records);
            WriteU32(EnemyScalingBaselineChannel.OffsetImportSequence, published);
            _importSequence = published;
        }

        public void Dispose() => Marshal.FreeHGlobal(_memory);
    }
}

public sealed class EnemyScalingImportGateTests
{
    private static byte[] Page(
        uint generation, EnemyScalingVerdict verdict, int page, int pages, int total, params EnemyScalingLatchEntry[] records)
    {
        byte[] payload = new byte[EnemyScalingBaselineChannel.ImportPayloadLength];
        EnemyScalingBaselineChannel.EncodeImportPage(payload, generation, verdict, page, pages, total, records);
        return payload;
    }

    [Theory]
    [InlineData(EnemyScalingVerdict.Fresh)]
    [InlineData(EnemyScalingVerdict.Refuse)]
    public void SinglePageVerdictsAreFinal(EnemyScalingVerdict verdict)
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(3);
        Assert.Equal(EnemyScalingImportStep.Complete, gate.Accept(Page(3, verdict, 0, 1, 0)));
        Assert.Equal(verdict, gate.Verdict);
        Assert.True(gate.Entries.IsEmpty);

        // The first final verdict wins for the generation.
        Assert.Equal(EnemyScalingImportStep.Ignored, gate.Accept(Page(3, EnemyScalingVerdict.Import, 0, 1, 0)));
        Assert.Equal(verdict, gate.Verdict);
    }

    [Fact]
    public void PagesForAnotherWorldAreIgnored()
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(4);
        Assert.Equal(EnemyScalingImportStep.Ignored, gate.Accept(Page(3, EnemyScalingVerdict.Fresh, 0, 1, 0)));
        Assert.Equal(EnemyScalingVerdict.None, gate.Verdict);
        Assert.False(gate.IsFinal);
    }

    [Fact]
    public void AnEmptyImportIsCompleteAndProvesNothingNeedsSeeding()
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(4);
        Assert.Equal(EnemyScalingImportStep.Complete, gate.Accept(Page(4, EnemyScalingVerdict.Import, 0, 1, 0)));
        Assert.Equal(EnemyScalingVerdict.Import, gate.Verdict);
        Assert.True(gate.Entries.IsEmpty);
    }

    [Fact]
    public void DuplicateIdsAcrossPagesRefuseTheWorld()
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(4);
        EnemyScalingLatchEntry[] first = Enumerable.Range(0, 32).Select(i => new EnemyScalingLatchEntry((uint)(0x10 + i), 20, 150)).ToArray();
        Assert.Equal(EnemyScalingImportStep.Incomplete, gate.Accept(Page(4, EnemyScalingVerdict.Import, 0, 2, 33, first)));
        Assert.Equal(EnemyScalingImportStep.Failed, gate.Accept(Page(4, EnemyScalingVerdict.Import, 1, 2, 33, new EnemyScalingLatchEntry(0x10, 20, 150))));
        Assert.Equal(EnemyScalingVerdict.Refuse, gate.Verdict);
        Assert.True(gate.Entries.IsEmpty);
    }

    [Fact]
    public void ContradictoryPagesRefuseTheWorld()
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(4);
        EnemyScalingLatchEntry[] first = Enumerable.Range(0, 32).Select(i => new EnemyScalingLatchEntry((uint)(0x10 + i), 20, 150)).ToArray();
        Assert.Equal(EnemyScalingImportStep.Incomplete, gate.Accept(Page(4, EnemyScalingVerdict.Import, 0, 2, 33, first)));

        // Same page again but different content.
        first[3] = first[3] with { BaseMaximum = 21 };
        Assert.Equal(EnemyScalingImportStep.Failed, gate.Accept(Page(4, EnemyScalingVerdict.Import, 0, 2, 33, first)));
    }

    [Fact]
    public void MalformedHeadersNeverDecode()
    {
        byte[] payload = Page(4, EnemyScalingVerdict.Import, 0, 1, 1, new EnemyScalingLatchEntry(0x10, 20, 150));
        Assert.True(EnemyScalingBaselineChannel.TryDecodeImportHeader(payload, out _, out _, out _, out _, out _, out _));

        // A record count that disagrees with the total.
        byte[] wrong = Page(4, EnemyScalingVerdict.Import, 0, 1, 2, new EnemyScalingLatchEntry(0x10, 20, 150));
        Assert.False(EnemyScalingBaselineChannel.TryDecodeImportHeader(wrong, out _, out _, out _, out _, out _, out _));

        // A Fresh verdict that carries records.
        byte[] fresh = Page(4, EnemyScalingVerdict.Fresh, 0, 1, 1, new EnemyScalingLatchEntry(0x10, 20, 150));
        Assert.False(EnemyScalingBaselineChannel.TryDecodeImportHeader(fresh, out _, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void AnInvalidRecordRefusesTheWorld()
    {
        EnemyScalingImportGate gate = new();
        gate.Reset(4);
        Assert.Equal(EnemyScalingImportStep.Failed,
            gate.Accept(Page(4, EnemyScalingVerdict.Import, 0, 1, 1, new EnemyScalingLatchEntry(0x7FFFFFFF, 20, 150))));
    }
}

public sealed class EnemyScalingReconcileRuleTests
{
    [Fact]
    public void AChangedPercentRecomputesFromTheOriginalMaximumAndKeepsTheRatio()
    {
        // Saved at 150%: 15 -> 23, wounded to 12/23. Reloaded at 200%: max 30, never 23*2.
        Assert.Equal(EnemyScalingRescaleResult.Write,
            EnemyScalingReconcileRule.Rescale(15, 150, 200, 12, 23, out int current, out int max));
        Assert.Equal(30, max);
        Assert.Equal(16, current); // 12/23 of 30 = 15.65 -> 16

        // Back to vanilla: the original maximum, same ratio.
        Assert.Equal(EnemyScalingRescaleResult.Write,
            EnemyScalingReconcileRule.Rescale(15, 150, 100, 23, 23, out current, out max));
        Assert.Equal((15, 15), (current, max));
    }

    [Fact]
    public void TheSamePercentNeverWrites()
    {
        Assert.Equal(EnemyScalingRescaleResult.Unchanged,
            EnemyScalingReconcileRule.Rescale(15, 150, 150, 20, 23, out int current, out int max));
        Assert.Equal((20, 23), (current, max));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ADeadCreatureIsNeverResurrected(int current)
    {
        Assert.Equal(EnemyScalingRescaleResult.Dead,
            EnemyScalingReconcileRule.Rescale(15, 150, 200, current, 23, out int newCurrent, out int newMax));
        Assert.Equal((current, 23), (newCurrent, newMax));
    }

    [Fact]
    public void AStoredMaximumThatIsNotTheRecordedScaleIsAMismatch()
    {
        // 35 = 23 * 150%: the compounding the whole design exists to prevent.
        Assert.Equal(EnemyScalingRescaleResult.Mismatch,
            EnemyScalingReconcileRule.Rescale(15, 150, 200, 30, 35, out _, out _));
        Assert.Equal(EnemyScalingRescaleResult.Mismatch,
            EnemyScalingReconcileRule.Rescale(15, 150, 200, 10, 15, out _, out _));
    }

    [Fact]
    public void ALivingCreatureKeepsAtLeastOneHitPointAndNeverExceedsTheNewMaximum()
    {
        Assert.Equal(EnemyScalingRescaleResult.Write,
            EnemyScalingReconcileRule.Rescale(200, 400, 50, 1, 800, out int current, out int max));
        Assert.Equal(100, max);
        Assert.Equal(1, current);
    }
}

public sealed class ScaledObjectLatchImportTests
{
    [Fact]
    public void ImportedEntriesAreLatchedUnverifiedAndOverflowIsRecorded()
    {
        ScaledObjectLatch latch = new(2);
        Assert.True(latch.AddImported(0x10, 15, 150));
        int index = latch.IndexOf(0x10);
        Assert.True(latch.IsImported(index));
        Assert.False(latch.IsVerified(index));
        latch.MarkVerified(index);
        Assert.True(latch.IsVerified(index));

        Assert.True(latch.Add(0x11, 12, 150));
        Assert.False(latch.IsImported(latch.IndexOf(0x11)));

        Assert.False(latch.Overflowed);
        Assert.False(latch.AddImported(0x12, 12, 150));
        Assert.True(latch.Overflowed);

        latch.SetPercent(index, 200);
        Assert.True(latch.TryGetBaseline(0x10, out int baseMaximum, out int percent));
        Assert.Equal((15, 200), (baseMaximum, percent));

        latch.Clear();
        Assert.False(latch.Overflowed);
        Assert.Equal(0, latch.Count);
    }

    [Fact]
    public void ARepeatedImportIsAnOverflowNotASecondEntry()
    {
        ScaledObjectLatch latch = new(4);
        Assert.True(latch.AddImported(0x10, 15, 150));
        Assert.False(latch.AddImported(0x10, 15, 150));
        Assert.True(latch.Overflowed);
        Assert.Equal(1, latch.Count);
    }
}
