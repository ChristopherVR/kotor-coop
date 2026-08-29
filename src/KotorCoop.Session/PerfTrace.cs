using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>One flush period's worth of <see cref="PerfTrace"/> counters, ready to format.</summary>
public readonly record struct PerfTraceSnapshot(
    long PeriodMs,
    long Ticks,
    double TickAverageMs,
    double TickPeakMs,
    double PositionPathAverageMs,
    double HeavyPassAverageMs,
    double HeavyPassPeakMs,
    long HeavyPasses,
    MemoryTelemetrySnapshot Memory,
    long PacketsSent,
    long PacketsReceived,
    long BytesSent,
    long BytesReceived,
    long PhantomMoves,
    double PhantomGapAverageMs,
    double PhantomGapPeakMs,
    double PhantomStepAverageUnits,
    double PhantomStepPeakUnits);

/// <summary>
/// The once-a-second measurement line behind <c>coop.ini perf_trace</c>.
///
/// <para>Built for one question the existing instruments could not answer. <c>lagstats</c>
/// reports the agent pump's average and peak cost and the last eight send/receive gaps, and the
/// plugin's <c>frame_stats</c> reports frame time — between them they proved the game's frame
/// rate and the pump's own budget were fine while players still reported a peer moving in
/// bursts. What nobody was counting was the shape of the stream ACTUALLY REACHING THE PHANTOM:
/// how often the position written into the game changed at all, how far it moved when it did,
/// and how much of the pump's time went to the real-time path versus the world scans sharing
/// its thread. A stream that updates every 33 ms by 0.13 units and one that updates every 66 ms
/// by 0.26 units have identical averages, identical pump costs and identical packet counts —
/// and the second is the bug. <see cref="PhantomGapPeakMs"/> against
/// <see cref="PhantomGapAverageMs"/> is what separates them.</para>
///
/// <para>Nothing here is on by default and nothing here allocates per tick: the counters are
/// fields, the per-peer bookkeeping is a small dictionary keyed by peer id, and the single
/// string is built once per flush. See docs/research/lag-audit.md.</para>
/// </summary>
public sealed class PerfTrace
{
    /// <summary>Below this a phantom did not move, it was re-written with the same number —
    /// the same "jitter, not walking" judgement <c>MotionSmoother.MovementEpsilon</c> makes,
    /// and the reason a tick that applies an unchanged position is not counted as an update.
    /// Without it every tick would count as a move and the gap metric, the whole point of this
    /// instrument, would read a flat 33 ms no matter how bursty the stream was.</summary>
    private const float MovementEpsilonUnits = 0.001f;

    private sealed class PeerMotion
    {
        public Vector3f LastPosition;
        public long LastMoveMs;
        public bool HasPosition;
    }

    private readonly Dictionary<uint, PeerMotion> _motion = [];

    private long _periodStartMs;
    private long _ticks;
    private double _tickSumMs;
    private double _tickPeakMs;
    private double _positionSumMs;
    private long _heavyPasses;
    private double _heavySumMs;
    private double _heavyPeakMs;

    private long _phantomMoves;
    private double _phantomGapSumMs;
    private double _phantomGapPeakMs;
    private double _phantomStepSumUnits;
    private double _phantomStepPeakUnits;

    private long _lastPacketsSent;
    private long _lastPacketsReceived;
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private bool _networkBaselined;

    /// <summary>Records one completed pump tick. <paramref name="positionPathMs"/> is the cost
    /// of the real-time half alone (read the local player, publish, sample peers, write the
    /// phantoms) — the part whose cadence a watching player can actually see.</summary>
    public void NoteTick(double totalMs, double positionPathMs)
    {
        _ticks++;
        _tickSumMs += totalMs;
        _positionSumMs += positionPathMs;

        if (totalMs > _tickPeakMs)
        {
            _tickPeakMs = totalMs;
        }
    }

    /// <summary>Records one heavy world-state pass — separately from the tick that carried it,
    /// because a pass costing hundreds of milliseconds on the pump thread is a hole in the
    /// position stream and averaging it into every tick hides exactly that.</summary>
    public void NoteHeavyPass(double ms)
    {
        _heavyPasses++;
        _heavySumMs += ms;

        if (ms > _heavyPeakMs)
        {
            _heavyPeakMs = ms;
        }
    }

    /// <summary>
    /// Records the position actually written into the game for one peer this tick. Only a
    /// genuine change counts: the gap since that peer's PREVIOUS change, and how far it moved.
    /// </summary>
    public void NotePhantomWrite(uint peerId, in Vector3f position, long nowMs)
    {
        if (!_motion.TryGetValue(peerId, out PeerMotion? motion))
        {
            motion = new PeerMotion();
            _motion[peerId] = motion;
        }

        if (!motion.HasPosition)
        {
            motion.HasPosition = true;
            motion.LastPosition = position;
            motion.LastMoveMs = nowMs;
            return;
        }

        float dx = position.X - motion.LastPosition.X;
        float dy = position.Y - motion.LastPosition.Y;
        float dz = position.Z - motion.LastPosition.Z;
        double step = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

        if (step < MovementEpsilonUnits)
        {
            return;
        }

        double gapMs = nowMs - motion.LastMoveMs;

        motion.LastPosition = position;
        motion.LastMoveMs = nowMs;

        _phantomMoves++;
        _phantomGapSumMs += gapMs;
        _phantomStepSumUnits += step;

        if (gapMs > _phantomGapPeakMs)
        {
            _phantomGapPeakMs = gapMs;
        }

        if (step > _phantomStepPeakUnits)
        {
            _phantomStepPeakUnits = step;
        }
    }

    /// <summary>Forgets a peer's motion bookkeeping — a rejoin or a module change is not a
    /// teleport worth reporting as a 900-unit step.</summary>
    public void Forget(uint peerId) => _motion.Remove(peerId);

    /// <summary>
    /// Hands back the accumulated period and resets, once <paramref name="flushIntervalMs"/>
    /// has elapsed. <paramref name="stats"/> supplies the cumulative network counters this
    /// diffs into per-period rates.
    /// </summary>
    public bool TryDrain(long nowMs, long flushIntervalMs, SessionStats stats, out PerfTraceSnapshot snapshot)
    {
        snapshot = default;

        if (_periodStartMs == 0)
        {
            _periodStartMs = nowMs;
            BaselineNetwork(stats);
            return false;
        }

        long periodMs = nowMs - _periodStartMs;
        if (periodMs < flushIntervalMs)
        {
            return false;
        }

        snapshot = new PerfTraceSnapshot(
            PeriodMs: periodMs,
            Ticks: _ticks,
            TickAverageMs: _ticks == 0 ? 0 : _tickSumMs / _ticks,
            TickPeakMs: _tickPeakMs,
            PositionPathAverageMs: _ticks == 0 ? 0 : _positionSumMs / _ticks,
            HeavyPassAverageMs: _heavyPasses == 0 ? 0 : _heavySumMs / _heavyPasses,
            HeavyPassPeakMs: _heavyPeakMs,
            HeavyPasses: _heavyPasses,
            Memory: MemoryTelemetry.Drain(),
            PacketsSent: stats.PacketsSent - _lastPacketsSent,
            PacketsReceived: stats.PacketsReceived - _lastPacketsReceived,
            BytesSent: stats.BytesSent - _lastBytesSent,
            BytesReceived: stats.BytesReceived - _lastBytesReceived,
            PhantomMoves: _phantomMoves,
            PhantomGapAverageMs: _phantomMoves == 0 ? 0 : _phantomGapSumMs / _phantomMoves,
            PhantomGapPeakMs: _phantomGapPeakMs,
            PhantomStepAverageUnits: _phantomMoves == 0 ? 0 : _phantomStepSumUnits / _phantomMoves,
            PhantomStepPeakUnits: _phantomStepPeakUnits);

        _periodStartMs = nowMs;
        _ticks = 0;
        _tickSumMs = 0;
        _tickPeakMs = 0;
        _positionSumMs = 0;
        _heavyPasses = 0;
        _heavySumMs = 0;
        _heavyPeakMs = 0;
        _phantomMoves = 0;
        _phantomGapSumMs = 0;
        _phantomGapPeakMs = 0;
        _phantomStepSumUnits = 0;
        _phantomStepPeakUnits = 0;
        BaselineNetwork(stats);

        return true;
    }

    private void BaselineNetwork(SessionStats stats)
    {
        _networkBaselined = true;
        _lastPacketsSent = stats.PacketsSent;
        _lastPacketsReceived = stats.PacketsReceived;
        _lastBytesSent = stats.BytesSent;
        _lastBytesReceived = stats.BytesReceived;
    }

    /// <summary>Whether the network counters have a baseline to diff against yet — false only
    /// before the very first drain.</summary>
    public bool NetworkBaselined => _networkBaselined;

    /// <summary>
    /// The log line for one drained period. Pure, so the arithmetic and the wording are
    /// testable without a session — the same split every other stats helper here uses.
    /// </summary>
    public static string Format(in PerfTraceSnapshot s)
    {
        double seconds = s.PeriodMs <= 0 ? 1 : s.PeriodMs / 1000.0;

        return $"perf: {s.Ticks / seconds:F1} ticks/s, tick {s.TickAverageMs:F2}/{s.TickPeakMs:F1} ms avg/peak, "
            + $"position path {s.PositionPathAverageMs:F2} ms, "
            + $"heavy {s.HeavyPasses}x {s.HeavyPassAverageMs:F1}/{s.HeavyPassPeakMs:F1} ms avg/peak | "
            + $"rpm {s.Memory.Reads / seconds:F0} reads/s ({s.Memory.ReadBytes / seconds / 1024.0:F1} KiB/s, "
            + $"{s.Memory.FailedReads} failed), {s.Memory.Writes / seconds:F0} writes/s "
            + $"({s.Memory.WriteBytes / seconds / 1024.0:F1} KiB/s) | "
            + $"net out {s.PacketsSent / seconds:F0} pkt/s {s.BytesSent / seconds / 1024.0:F1} KiB/s, "
            + $"in {s.PacketsReceived / seconds:F0} pkt/s {s.BytesReceived / seconds / 1024.0:F1} KiB/s | "
            + $"phantom {s.PhantomMoves / seconds:F1} moves/s, gap {s.PhantomGapAverageMs:F0}/{s.PhantomGapPeakMs:F0} ms "
            + $"avg/peak, step {s.PhantomStepAverageUnits:F3}/{s.PhantomStepPeakUnits:F3} u avg/peak";
    }
}
