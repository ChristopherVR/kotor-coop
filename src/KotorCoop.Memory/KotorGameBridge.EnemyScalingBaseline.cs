using System.Buffers.Binary;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// The agent's half of the shared-block version 76 enemy-health baseline channel
/// (<see cref="EnemyScalingBaselineChannel"/>, docs/research/enemy-health-persistence.md).
/// Implemented by <see cref="KotorGameBridge"/>; faked by the tests that drive
/// <c>EnemyScalingPersistence</c>.
/// </summary>
public interface IEnemyScalingBaselinePort
{
    /// <summary>One stable read of the plugin's world/export page. False = no page, a torn
    /// read, or a malformed one (all transient from the caller's point of view).</summary>
    bool TryReadEnemyScalingWorldPage(out EnemyScalingWorldPage? page);

    /// <summary>Acknowledges the page whose sequence the caller consumed; the plugin then
    /// publishes the next page of the export, if any.</summary>
    void AcknowledgeEnemyScalingExport(uint sequence);

    /// <summary>The last verdict/import sequence the plugin consumed.</summary>
    bool TryReadEnemyScalingImportAck(out uint sequence);

    /// <summary>Publishes one verdict/import page under the next seqlock value; returns the
    /// published (even) sequence the plugin will acknowledge.</summary>
    bool TryWriteEnemyScalingImportPage(
        uint worldGeneration,
        EnemyScalingVerdict verdict,
        int pageIndex,
        int pageCount,
        int totalRecords,
        ReadOnlySpan<EnemyScalingLatchEntry> records,
        out uint sequence);

    /// <summary>One stable read of the plugin's native save-request mailbox.</summary>
    bool TryReadEnemyScalingSaveRequest(out EnemyScalingSaveRequest? request);

    /// <summary>The v74 load-request provenance mailbox (read-only).</summary>
    bool TryReadLoadProvenance(out LoadProvenance? provenance);
}

public sealed partial class KotorGameBridge : IEnemyScalingBaselinePort
{
    // Enemy-health baseline channel (shared block version 76, region 0x5400..0x5800,
    // TotalSize grown to 0x5800). Mirrors SharedBlock.OffsetEnemyBaseline* in
    // SharedBlock.EnemyScalingBaseline.cs; both equal EnemyScalingBaselineChannel's offsets
    // (SharedBlockNewRegionOffsetMirrorTests and EnemyScalingBaselineChannelTests).
    private const int BlockEnemyBaselineExportSequence = 0x5400;
    private const int BlockEnemyBaselineExportAck = 0x5640;
    private const int BlockEnemyBaselineImportSequence = 0x5648;
    private const int BlockEnemyBaselineImportAck = 0x5760;
    private const int BlockEnemyBaselineSaveSequence = 0x5768;

    private uint _enemyBaselineImportSequence;

    public bool TryReadEnemyScalingWorldPage(out EnemyScalingWorldPage? page)
    {
        page = null;
        nint block = _pluginBlock;
        if (block == 0)
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[EnemyScalingBaselineChannel.ExportPayloadLength];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_memory.TryReadUInt32(block + BlockEnemyBaselineExportSequence, out uint before)
                || before == 0 || (before & 1) != 0
                || !_memory.TryRead(block + EnemyScalingBaselineChannel.ExportPayloadStart, payload)
                || !_memory.TryReadUInt32(block + BlockEnemyBaselineExportSequence, out uint after))
            {
                continue;
            }

            if (EnemyScalingBaselineChannel.TryDecodeWorldPage(before, payload, after, out page))
            {
                return true;
            }
        }

        return false;
    }

    public void AcknowledgeEnemyScalingExport(uint sequence)
    {
        if (_pluginBlock != 0)
        {
            _memory.TryWrite(_pluginBlock + BlockEnemyBaselineExportAck, sequence);
        }
    }

    public bool TryReadEnemyScalingImportAck(out uint sequence)
    {
        sequence = 0;
        return _pluginBlock != 0 && _memory.TryReadUInt32(_pluginBlock + BlockEnemyBaselineImportAck, out sequence);
    }

    public bool TryWriteEnemyScalingImportPage(
        uint worldGeneration,
        EnemyScalingVerdict verdict,
        int pageIndex,
        int pageCount,
        int totalRecords,
        ReadOnlySpan<EnemyScalingLatchEntry> records,
        out uint sequence)
    {
        sequence = 0;
        nint block = _pluginBlock;
        if (block == 0)
        {
            return false;
        }

        // Resume from whatever the block already holds, so an agent restart never reuses a
        // sequence the plugin has already acknowledged.
        if (_enemyBaselineImportSequence == 0
            && _memory.TryReadUInt32(block + BlockEnemyBaselineImportSequence, out uint existing))
        {
            _enemyBaselineImportSequence = existing & ~1u;
        }

        Span<byte> payload = stackalloc byte[EnemyScalingBaselineChannel.ImportPayloadLength];
        EnemyScalingBaselineChannel.EncodeImportPage(
            payload, worldGeneration, verdict, pageIndex, pageCount, totalRecords, records);

        uint published = EnemyScalingBaselineChannel.NextSequence(_enemyBaselineImportSequence, out uint odd);
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(word, odd);
        if (!_memory.TryWrite(block + BlockEnemyBaselineImportSequence, (ReadOnlySpan<byte>)word)
            || !_memory.TryWrite(block + EnemyScalingBaselineChannel.ImportPayloadStart, (ReadOnlySpan<byte>)payload))
        {
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(word, published);
        if (!_memory.TryWrite(block + BlockEnemyBaselineImportSequence, (ReadOnlySpan<byte>)word))
        {
            return false;
        }

        _enemyBaselineImportSequence = published;
        sequence = published;
        return true;
    }

    public bool TryReadEnemyScalingSaveRequest(out EnemyScalingSaveRequest? request)
    {
        request = null;
        nint block = _pluginBlock;
        if (block == 0)
        {
            return false;
        }

        Span<byte> payload = stackalloc byte[EnemyScalingBaselineChannel.SavePayloadLength];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!_memory.TryReadUInt32(block + BlockEnemyBaselineSaveSequence, out uint before)
                || before == 0 || (before & 1) != 0
                || !_memory.TryRead(block + EnemyScalingBaselineChannel.SavePayloadStart, payload)
                || !_memory.TryReadUInt32(block + BlockEnemyBaselineSaveSequence, out uint after))
            {
                continue;
            }

            if (EnemyScalingBaselineChannel.TryDecodeSaveRequest(before, payload, after, out request))
            {
                return true;
            }
        }

        return false;
    }
}
