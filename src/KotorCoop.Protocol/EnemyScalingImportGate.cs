namespace KotorCoop.Protocol;

/// <summary>What a percentage reconciliation of one imported creature asks the caller to do.</summary>
public enum EnemyScalingRescaleResult
{
    /// <summary>The stored numbers already represent the requested percentage.</summary>
    Unchanged,

    /// <summary>Write the returned maximum, then the returned current hit points.</summary>
    Write,

    /// <summary>The creature is dead (current at or below zero): nothing is written, so a
    /// corpse is never resurrected by a maximum change.</summary>
    Dead,

    /// <summary>The live stored maximum is not what the baseline says it should be; the
    /// baseline is not proof of anything about this creature and nothing is written.</summary>
    Mismatch,
}

/// <summary>
/// Pure arithmetic for an imported (saved) scaled creature whose percentage changed since
/// the save. The new maximum is always computed from the preserved ORIGINAL maximum, never
/// from the already-scaled stored one, and the current hit points keep their ratio against
/// the old stored maximum (half-up rounding, at least 1 while alive, never above the new
/// maximum). Shared by the plugin (<c>EnemyScalingController</c>) and the tests.
/// </summary>
public static class EnemyScalingReconcileRule
{
    public static EnemyScalingRescaleResult Rescale(
        int baseMaximum,
        int fromPercent,
        int toPercent,
        int current,
        int storedMaximum,
        out int newCurrent,
        out int newMaximum)
    {
        newCurrent = current;
        newMaximum = storedMaximum;

        int expected = EnemyScalingBaselineRule.ScaleMaximum(baseMaximum, fromPercent);
        int target = EnemyScalingBaselineRule.ScaleMaximum(baseMaximum, toPercent);
        if (expected == 0 || target == 0 || storedMaximum != expected)
        {
            return EnemyScalingRescaleResult.Mismatch;
        }

        if (current <= 0)
        {
            return EnemyScalingRescaleResult.Dead;
        }

        if (target == storedMaximum)
        {
            return EnemyScalingRescaleResult.Unchanged;
        }

        long ratio = (((long)Math.Min(current, storedMaximum) * target * 2) + storedMaximum) / (2L * storedMaximum);
        newMaximum = target;
        newCurrent = (int)Math.Clamp(ratio, 1, target);
        return EnemyScalingRescaleResult.Write;
    }
}

/// <summary>Result of offering one verdict/import page to <see cref="EnemyScalingImportGate"/>.</summary>
public enum EnemyScalingImportStep
{
    /// <summary>For another world generation, malformed, or after a final verdict: acknowledge
    /// and otherwise ignore.</summary>
    Ignored,

    /// <summary>A page of a multi-page import was stored; more are needed.</summary>
    Incomplete,

    /// <summary>The verdict is complete: read <see cref="EnemyScalingImportGate.Verdict"/>.</summary>
    Complete,

    /// <summary>The import contradicted itself (duplicate ids, changing page count, invalid
    /// record): the world must be refused.</summary>
    Failed,
}

/// <summary>
/// The plugin's allocation-free assembler for the agent's verdict pages. One instance per
/// controller; <see cref="Reset"/> at every world-up. Only pages for the reset generation are
/// considered, every page of one import must agree on verdict, page count and total, and ids
/// must be unique across the whole import. The first final outcome (complete or failed)
/// wins for the generation; later pages are ignored.
/// </summary>
public sealed class EnemyScalingImportGate
{
    private readonly EnemyScalingLatchEntry[] _entries = new EnemyScalingLatchEntry[EnemyScalingBaselineChannel.LatchCapacity];
    private uint _generation;
    private EnemyScalingVerdict _verdict;
    private int _pageCount;
    private int _total;
    private uint _receivedMask;
    private bool _final;

    public uint Generation => _generation;

    public EnemyScalingVerdict Verdict => _final ? _verdict : EnemyScalingVerdict.None;

    public bool IsFinal => _final;

    /// <summary>The complete imported set; empty until <see cref="Verdict"/> is Import.</summary>
    public ReadOnlySpan<EnemyScalingLatchEntry> Entries =>
        _final && _verdict == EnemyScalingVerdict.Import ? _entries.AsSpan(0, _total) : [];

    public void Reset(uint generation)
    {
        _generation = generation;
        _verdict = EnemyScalingVerdict.None;
        _pageCount = 0;
        _total = 0;
        _receivedMask = 0;
        _final = false;
    }

    public EnemyScalingImportStep Accept(ReadOnlySpan<byte> payload)
    {
        if (_final || _generation == 0
            || !EnemyScalingBaselineChannel.TryDecodeImportHeader(
                payload, out uint generation, out EnemyScalingVerdict verdict,
                out int pageIndex, out int pageCount, out int recordCount, out int total)
            || generation != _generation)
        {
            return EnemyScalingImportStep.Ignored;
        }

        if (verdict != EnemyScalingVerdict.Import)
        {
            _verdict = verdict;
            _total = 0;
            _final = true;
            return EnemyScalingImportStep.Complete;
        }

        if (_receivedMask == 0)
        {
            _verdict = verdict;
            _pageCount = pageCount;
            _total = total;
        }
        else if (_verdict != verdict || _pageCount != pageCount || _total != total)
        {
            return Fail();
        }

        uint bit = 1u << pageIndex;
        ReadOnlySpan<byte> records = payload[(EnemyScalingBaselineChannel.OffsetImportRecords
            - EnemyScalingBaselineChannel.ImportPayloadStart)..];
        int start = pageIndex * EnemyScalingBaselineChannel.ImportRecordsPerPage;

        if ((_receivedMask & bit) != 0)
        {
            // A repeated page must be byte-identical in meaning to the stored one.
            for (int i = 0; i < recordCount; i++)
            {
                EnemyScalingLatchEntry again = EnemyScalingBaselineChannel.ReadRecord(
                    records.Slice(i * EnemyScalingBaselineChannel.RecordStride, EnemyScalingBaselineChannel.RecordStride));
                if (again != _entries[start + i])
                {
                    return Fail();
                }
            }

            return EnemyScalingImportStep.Incomplete;
        }

        for (int i = 0; i < recordCount; i++)
        {
            EnemyScalingLatchEntry entry = EnemyScalingBaselineChannel.ReadRecord(
                records.Slice(i * EnemyScalingBaselineChannel.RecordStride, EnemyScalingBaselineChannel.RecordStride));
            if (!EnemyScalingBaselineChannel.IsValidEntry(entry))
            {
                return Fail();
            }

            _entries[start + i] = entry;
        }

        _receivedMask |= bit;
        if (_receivedMask != (1u << _pageCount) - 1)
        {
            return EnemyScalingImportStep.Incomplete;
        }

        for (int i = 0; i < _total; i++)
        {
            for (int j = i + 1; j < _total; j++)
            {
                if (_entries[i].ObjectId == _entries[j].ObjectId)
                {
                    return Fail();
                }
            }
        }

        _final = true;
        return EnemyScalingImportStep.Complete;
    }

    private EnemyScalingImportStep Fail()
    {
        _verdict = EnemyScalingVerdict.Refuse;
        _total = 0;
        _final = true;
        return EnemyScalingImportStep.Failed;
    }
}
