namespace KotorCoop.Plugin;

/// <summary>
/// Converts rendered-frame intervals into the millisecond step value consumed by the native
/// follow continuation. A fixed 16 ms step over-advances an uncapped render loop; fractional
/// milliseconds are carried so high callback rates do not lose or multiply time.
/// </summary>
internal static class FollowStepTimingRule
{
    /// <summary>Do not feed a long render hitch as one giant native follow step.</summary>
    public const int MaxStepMilliseconds = 100;

    /// <summary>
    /// Consumes one measured interval. The returned whole milliseconds are bounded for one
    /// native call; excess from a long hitch is discarded rather than emitted as a teleport-sized
    /// step. Invalid or one-second-plus gaps reset the fractional carry and return zero.
    /// </summary>
    public static int Consume(ref double fractionalMilliseconds, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0.0 || seconds >= 1.0)
        {
            fractionalMilliseconds = 0.0;
            return 0;
        }

        double total = fractionalMilliseconds + (seconds * 1000.0);
        int step = (int)Math.Min(Math.Floor(total), MaxStepMilliseconds);
        if (step >= MaxStepMilliseconds)
        {
            fractionalMilliseconds = 0.0;
            return MaxStepMilliseconds;
        }

        fractionalMilliseconds = total - step;
        return step;
    }
}
