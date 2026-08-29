namespace KotorCoop.Memory;

/// <summary>
/// Process-wide counters for cross-process memory traffic — how many
/// <c>ReadProcessMemory</c>/<c>WriteProcessMemory</c> calls the agent makes and how many bytes
/// they move.
///
/// This exists because "the pump is slow" and "the pump is doing too much I/O" are different
/// bugs with different fixes, and until this was counted the only visible number was the tick
/// time, which cannot tell them apart. A cross-process read is a syscall: a tick that issues
/// two thousand four-byte reads costs far more than one that issues twenty thousand bytes in
/// forty reads, and the two are indistinguishable from the outside.
///
/// ⚠ STATIC ON PURPOSE, and gated on <see cref="Enabled"/>. The counters live here rather than
/// on <see cref="ProcessMemory"/> because the consumer (<c>CoopAgent</c>'s perf trace) is three
/// assemblies away from the handle and has no reference to the instance; the gate is what keeps
/// the cost at one relaxed static bool read per call when the trace is off, which is always
/// except on a rig measuring itself. Reads are <see cref="Interlocked"/> because the pump thread
/// and the bridge's own background work both issue them.
/// </summary>
public static class MemoryTelemetry
{
    private static long _reads;
    private static long _readBytes;
    private static long _failedReads;
    private static long _writes;
    private static long _writeBytes;

    /// <summary>Whether <see cref="NoteRead"/>/<see cref="NoteWrite"/> actually count. Off by
    /// default: every <c>TryRead</c> in the process tests this, so it must stay a plain
    /// non-volatile static bool read — a torn value here would cost one miscounted call on the
    /// tick the trace was switched on, which is not worth a memory barrier per read.</summary>
    public static bool Enabled;

    public static void NoteRead(int bytes, bool ok)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _readBytes, bytes);

        if (!ok)
        {
            Interlocked.Increment(ref _failedReads);
        }
    }

    public static void NoteWrite(int bytes)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _writes);
        Interlocked.Add(ref _writeBytes, bytes);
    }

    /// <summary>Everything counted since the last <see cref="Drain"/>, and resets. Draining
    /// rather than reading cumulative totals is what lets the caller report a per-second rate
    /// without keeping its own previous-value bookkeeping.</summary>
    public static MemoryTelemetrySnapshot Drain() => new(
        Interlocked.Exchange(ref _reads, 0),
        Interlocked.Exchange(ref _readBytes, 0),
        Interlocked.Exchange(ref _failedReads, 0),
        Interlocked.Exchange(ref _writes, 0),
        Interlocked.Exchange(ref _writeBytes, 0));
}

/// <summary>One drained period's worth of <see cref="MemoryTelemetry"/> counters.</summary>
public readonly record struct MemoryTelemetrySnapshot(
    long Reads, long ReadBytes, long FailedReads, long Writes, long WriteBytes);
