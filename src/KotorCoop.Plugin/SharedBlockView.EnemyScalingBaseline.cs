using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

// Writers/readers for the version 76 enemy-health baseline channel. See
// SharedBlock.EnemyScalingBaseline.cs and KotorCoop.Protocol.EnemyScalingBaselineChannel.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Plugin -&gt; agent. Publishes one world/export page: odd sequence, payload,
    /// then the next even sequence, so the agent's bracketing reads never accept a torn page.
    /// Returns the published sequence.</summary>
    public uint PublishEnemyBaselinePage(
        uint currentSequence,
        uint worldGeneration,
        EnemyScalingWorldOrigin origin,
        uint originProvenance,
        uint originFlags,
        uint worldLoadSerial,
        EnemyScalingHealthMode mode,
        uint exportGeneration,
        uint previousWorldGeneration,
        uint previousFinalExportGeneration,
        int pageIndex,
        int pageCount,
        ushort flags,
        ReadOnlySpan<byte> module,
        ReadOnlySpan<EnemyScalingLatchEntry> records)
    {
        uint published = EnemyScalingBaselineChannel.NextSequence(currentSequence, out uint odd);
        int* sequence = (int*)U32(EnemyScalingBaselineChannel.OffsetExportSequence);
        System.Threading.Interlocked.Exchange(ref *sequence, unchecked((int)odd));

        *U32(EnemyScalingBaselineChannel.OffsetExportWorldGeneration) = worldGeneration;
        *U32(EnemyScalingBaselineChannel.OffsetExportOrigin) = (uint)origin;
        *U32(EnemyScalingBaselineChannel.OffsetExportOriginProvenance) = originProvenance;
        *U32(EnemyScalingBaselineChannel.OffsetExportOriginFlags) = originFlags;
        *U32(EnemyScalingBaselineChannel.OffsetExportWorldLoadSerial) = worldLoadSerial;
        *U32(EnemyScalingBaselineChannel.OffsetExportMode) = (uint)mode;
        *U32(EnemyScalingBaselineChannel.OffsetExportGeneration) = exportGeneration;
        *U32(EnemyScalingBaselineChannel.OffsetExportPreviousWorldGeneration) = previousWorldGeneration;
        *U32(EnemyScalingBaselineChannel.OffsetExportPreviousFinalGeneration) = previousFinalExportGeneration;
        *U32(EnemyScalingBaselineChannel.OffsetExportPageInfo) = EnemyScalingBaselineChannel.PackPageInfo(pageIndex, pageCount);
        *U32(EnemyScalingBaselineChannel.OffsetExportRecordInfo) = (uint)records.Length | ((uint)flags << 16);

        Span<byte> moduleField = new(baseAddress + EnemyScalingBaselineChannel.OffsetExportModule,
            EnemyScalingBaselineChannel.ModuleNameCapacity);
        moduleField.Clear();
        module[..Math.Min(module.Length, moduleField.Length)].CopyTo(moduleField);

        Span<byte> recordField = new(baseAddress + EnemyScalingBaselineChannel.OffsetExportRecords,
            EnemyScalingBaselineChannel.ExportRecordsPerPage * EnemyScalingBaselineChannel.RecordStride);
        recordField.Clear();
        for (int i = 0; i < records.Length && i < EnemyScalingBaselineChannel.ExportRecordsPerPage; i++)
        {
            EnemyScalingBaselineChannel.WriteRecord(
                recordField.Slice(i * EnemyScalingBaselineChannel.RecordStride, EnemyScalingBaselineChannel.RecordStride),
                records[i]);
        }

        System.Threading.Volatile.Write(ref *sequence, unchecked((int)published));
        return published;
    }

    /// <summary>Agent -&gt; plugin. The export sequence the agent last consumed.</summary>
    public uint EnemyBaselineExportAck =>
        unchecked((uint)System.Threading.Volatile.Read(ref *(int*)U32(EnemyScalingBaselineChannel.OffsetExportAck)));

    /// <summary>Agent -&gt; plugin. Copies one stable verdict/import page. False while the
    /// agent is mid-write, when nothing was ever published, or when the page did not hold
    /// still across the copy.</summary>
    public bool TryReadEnemyBaselineImport(Span<byte> payload, out uint sequence)
    {
        int* cell = (int*)U32(EnemyScalingBaselineChannel.OffsetImportSequence);
        sequence = unchecked((uint)System.Threading.Volatile.Read(ref *cell));
        if (sequence == 0 || (sequence & 1) != 0 || payload.Length < EnemyScalingBaselineChannel.ImportPayloadLength)
        {
            return false;
        }

        new ReadOnlySpan<byte>(baseAddress + EnemyScalingBaselineChannel.ImportPayloadStart,
            EnemyScalingBaselineChannel.ImportPayloadLength).CopyTo(payload);
        return unchecked((uint)System.Threading.Volatile.Read(ref *cell)) == sequence;
    }

    /// <summary>Plugin -&gt; agent. The import sequence this plugin consumed.</summary>
    public uint EnemyBaselineImportAck
    {
        get => *U32(EnemyScalingBaselineChannel.OffsetImportAck);
        set => System.Threading.Volatile.Write(
            ref *(int*)U32(EnemyScalingBaselineChannel.OffsetImportAck), unchecked((int)value));
    }

    /// <summary>Plugin -&gt; agent. One native save request, published from the game thread
    /// at the entry of <c>CServerExoAppInternal::SaveGame</c>. Returns the published sequence.</summary>
    public uint PublishEnemyBaselineSaveRequest(
        uint currentSequence, int slot, uint worldGeneration, long fileTimeUtc, ReadOnlySpan<byte> name)
    {
        uint published = EnemyScalingBaselineChannel.NextSequence(currentSequence, out uint odd);
        int* sequence = (int*)U32(EnemyScalingBaselineChannel.OffsetSaveSequence);
        System.Threading.Interlocked.Exchange(ref *sequence, unchecked((int)odd));

        *I32(EnemyScalingBaselineChannel.OffsetSaveSlot) = slot;
        *U32(EnemyScalingBaselineChannel.OffsetSaveWorldGeneration) = worldGeneration;
        *U32(EnemyScalingBaselineChannel.OffsetSaveNameLength) = (uint)Math.Min(name.Length, EnemyScalingBaselineChannel.SaveNameCapacity);
        *(long*)(baseAddress + EnemyScalingBaselineChannel.OffsetSaveFileTime) = fileTimeUtc;
        Span<byte> field = new(baseAddress + EnemyScalingBaselineChannel.OffsetSaveName, EnemyScalingBaselineChannel.SaveNameCapacity);
        field.Clear();
        name[..Math.Min(name.Length, field.Length)].CopyTo(field);

        System.Threading.Volatile.Write(ref *sequence, unchecked((int)published));
        return published;
    }
}
