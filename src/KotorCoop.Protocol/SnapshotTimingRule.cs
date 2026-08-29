namespace KotorCoop.Protocol;

/// <summary>What <see cref="SnapshotTimingRule"/> needs to remember between two arrivals for
/// one peer: when the last ACCEPTED sample actually arrived, and what playback timestamp it was
/// given. Kept as a value so the rule stays pure and testable without a buffer.</summary>
public readonly record struct SnapshotTimingState(long LastArrivalMs, long LastAssignedMs, bool HasPrevious)
{
    public static readonly SnapshotTimingState Fresh = new(0, 0, false);

    /// <summary>The state after <paramref name="assignedMs"/> was handed to a sample that
    /// arrived at <paramref name="arrivalMs"/>.</summary>
    public SnapshotTimingState Accept(long arrivalMs, long assignedMs) => new(arrivalMs, assignedMs, true);
}

/// <summary>
/// Decides what time a received snapshot is treated as having HAPPENED at, which is not the
/// same question as when it arrived.
///
/// <para>Why this exists. Every sender runs two independent timers at the same nominal rate:
/// <c>CoopAgent.PumpOnce</c> samples the game and calls <c>PublishLocalState</c>, and
/// <c>CoopHost.Tick</c>/<c>CoopClient.Tick</c> put whatever was last published on the wire.
/// Nothing synchronises them. Two free-running 30 Hz clocks BEAT against each other: over a
/// few seconds the send tick slides through the pump tick's phase, and while it does, the
/// stream alternates between sending the SAME sample twice (the send fired twice between two
/// pumps) and skipping one (the pump fired twice between two sends). The receiver used to
/// stamp each arrival with its own wall clock, so a duplicate looked like "the peer stood
/// still for a tick" and the sample after it looked like "the peer covered two ticks of ground
/// in one" — and <c>SnapshotBuffer</c> faithfully interpolated exactly that: a pause, then a
/// dash at double speed, repeating at the beat frequency. That is the "moves in bursts /
/// catches up" report, and it is manufactured entirely on our side of the wire; a perfectly
/// lossless localhost link produces it just as reliably as a bad one. Localhost UDP coalescing
/// (two datagrams delivered in the same millisecond, the measured <c>16, 0, 16, 0</c> receive
/// pattern in docs/research/lag-audit.md) adds the same distortion from the other end.</para>
///
/// <para>The fix is to stop believing arrival times. A sender emitting at a known period
/// produces samples that are, in truth, evenly spaced; the jitter is all transport. So the
/// inter-arrival gap is QUANTISED to a whole number of send periods and the playback timeline
/// is advanced by exactly that — a 17 ms gap and a 49 ms gap are both one period, and a
/// coalesced 0 ms gap is one period too, because a sample that exists was sampled some period
/// apart from the one before it whatever the network did with it afterwards. The result is a
/// timeline whose spacing reflects the sender's cadence rather than the link's mood, which is
/// what makes interpolating between two samples produce the peer's real velocity.</para>
///
/// <para>Two escapes stop the regularised clock from becoming a fiction:
/// <see cref="ResyncGapMs"/> (a gap this long is a stall, a load screen or a reconnect, not a
/// late packet — believe the arrival) and <see cref="MaxDriftMs"/> (the quantiser rounds, so a
/// sender whose real rate differs from the assumed period accumulates error; past this bound
/// the timeline is re-pegged to arrivals rather than allowed to drift without limit).</para>
///
/// <para>⚠ This does NOT extrapolate and is not a substitute for having samples: it corrects
/// WHEN a sample is played, never invents one. See <c>SnapshotBuffer</c>, which owns the
/// interpolation itself.</para>
/// </summary>
public static class SnapshotTimingRule
{
    /// <summary>
    /// An inter-arrival gap longer than this is not jitter. A load screen, a heavy pass on the
    /// sender's pump thread, a dropped link, a peer standing still long enough that
    /// <see cref="ShouldAccept"/> suppressed everything in between — in all of those the
    /// regularised timeline has nothing to say, and pretending the missing time was a whole
    /// number of tidy periods would place the sample far from where it belongs. Comfortably
    /// longer than any plausible burst of loss at 30 Hz and shorter than
    /// <c>SnapshotBuffer.ContinuityGapMs</c>, so the two escalate in the right order: this one
    /// re-pegs the clock, that one throws the history away.
    /// </summary>
    public const int ResyncGapMs = 400;

    /// <summary>
    /// How far the regularised timeline may sit from real arrivals before it is re-pegged.
    /// Quantising rounds, and rounding the same way repeatedly is a systematic error: a sender
    /// genuinely running at 27 Hz against an assumed 30 Hz period would have every gap rounded
    /// down to one period and the timeline would fall behind arrivals for ever, adding
    /// unbounded latency to a peer who is merely on a slightly different clock. This bounds
    /// that in either direction. Chosen against the two failure modes it sits between: too
    /// tight and every burst of quantisation noise (±half a period, accumulating as a random
    /// walk) re-pegs the clock and the regularisation stops doing anything; too loose and a
    /// timeline running ahead of arrivals silently ADDS that much playback latency, which is
    /// the very thing the interpolation delay is kept small for. Roughly four send periods at
    /// 30 Hz is comfortably above the noise and still under half the deliberate 80 ms
    /// interpolation buffer's own worst case.
    /// </summary>
    public const int MaxDriftMs = 120;

    /// <summary>
    /// Whether a newly-arrived state earns a sample of its own.
    ///
    /// A state byte-identical to the newest one we hold carries no new observation — it is
    /// either the sender's send tick firing twice between two of its pump ticks (the beat
    /// described on this class) or a peer genuinely standing still. Both want the same
    /// treatment: do not lay down a sample. Recording one would tell
    /// <c>SnapshotBuffer.Interpolate</c> that the peer demonstrably stood still for a period,
    /// which is what makes the NEXT sample — carrying two periods of movement — get played
    /// back over one period at double speed.
    ///
    /// ⚠ The caller must still refresh its liveness stamp for a suppressed arrival: a peer
    /// standing perfectly still is not a peer who has gone away, and dropping them for a
    /// timeout because they were motionless would be a far worse bug than the one this fixes.
    /// </summary>
    public static bool ShouldAccept(in PlayerState newest, in PlayerState arriving) => newest != arriving;

    /// <summary>
    /// The playback timestamp <paramref name="arrivalMs"/>'s sample should be filed under.
    ///
    /// <paramref name="periodMs"/> is the sender's nominal send interval (1000 / tick rate).
    /// A non-positive period disables regularisation entirely and hands back the arrival,
    /// which is the pre-existing behaviour and the correct fallback for a caller that does not
    /// know the peer's rate.
    /// </summary>
    public static long Assign(in SnapshotTimingState state, long arrivalMs, int periodMs)
    {
        if (!state.HasPrevious || periodMs <= 0)
        {
            return arrivalMs;
        }

        long gapMs = arrivalMs - state.LastArrivalMs;

        // ⚠ STRICTLY negative. A gap of exactly ZERO is the coalescing case this rule exists
        // for — two datagrams handed up in the same millisecond, the measured `16, 0, 16, 0`
        // receive pattern — and it must fall through to be credited its one period like any
        // other. Only a clock that actually went backwards lands here.
        if (gapMs < 0)
        {
            return Math.Max(arrivalMs, state.LastAssignedMs + 1);
        }

        if (gapMs > ResyncGapMs)
        {
            return arrivalMs;
        }

        // Round to the nearest whole period, never below one: a sample exists, so at least one
        // period passed at the sender however the transport bunched it up.
        long periods = ((2 * gapMs) + periodMs) / (2 * periodMs);
        if (periods < 1)
        {
            periods = 1;
        }

        long assigned = state.LastAssignedMs + (periods * periodMs);

        if (assigned < arrivalMs - MaxDriftMs || assigned > arrivalMs + MaxDriftMs)
        {
            return arrivalMs;
        }

        return assigned;
    }
}
