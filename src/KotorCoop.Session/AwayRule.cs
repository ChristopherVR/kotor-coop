namespace KotorCoop.Session;

/// <summary>
/// Decides when a run of failed local-state reads means the player is genuinely AWAY — the
/// main menu, a load screen, the character/export screen, anywhere there is simply no live
/// player object for <c>CoopAgent.PumpOnce</c>'s <c>TryReadLocalState</c> to sample — as
/// opposed to the ordinary single-tick blip a live memory read always risks (a module
/// mid-store, an object freed between two reads). Pure so the threshold is tested without a
/// game, a session, or a clock; consumed from <c>CoopAgent.PumpOnce</c>, which is the only
/// thing that already counts consecutive read failures.
///
/// Before this rule existed, a read failure just meant "keep publishing whatever we read
/// last" forever: the session's snapshot buffer kept re-sending the last known avatar with no
/// signal attached, so a partner saw a frozen statue at the last position with no way to tell
/// "briefly stalled" from "quit to the main menu five minutes ago". See
/// docs/research/disconnect-reconnect.md.
/// </summary>
public static class AwayRule
{
    /// <summary>
    /// Consecutive failed reads, AT THE DEFAULT 30 Hz POLL RATE, before the player is
    /// considered away: about one second. Long enough that a single bad tick during a save
    /// load or a module transition — which recovers within a frame or two — never flips it;
    /// short enough that a partner is not left staring at a frozen statue for several seconds
    /// wondering whether the link died. <see cref="Threshold"/> scales this figure to
    /// whatever poll rate is actually configured, so the WALL-CLOCK window stays ~1 s rather
    /// than the tick count.
    /// </summary>
    public const int DefaultThreshold = 30;

    /// <summary>
    /// The consecutive-failure count that means "away" at <paramref name="pollRate"/> ticks
    /// per second — <see cref="DefaultThreshold"/> scaled to keep the WALL-CLOCK window ~1 s
    /// however fast (or slow) the pump actually runs, the same reasoning
    /// <c>CoopAgent</c>'s own cadence gates use elsewhere (tying a threshold to tick count
    /// instead of wall time was a real, measured bug there — see its heavy-pass remarks).
    /// Never less than 1, so a pathological near-zero poll rate still requires at least one
    /// failed read rather than firing immediately.
    /// </summary>
    public static int Threshold(int pollRate) =>
        pollRate > 0 ? Math.Max(1, pollRate * DefaultThreshold / 30) : DefaultThreshold;

    /// <summary>
    /// True once <paramref name="consecutiveReadFailures"/> has reached the away threshold for
    /// <paramref name="pollRate"/>. Consecutive means "since the last successful read" — the
    /// caller is expected to reset its counter to zero the moment <c>TryReadLocalState</c>
    /// succeeds, so a single stray failure years into a stable session cannot accumulate
    /// toward this.
    /// </summary>
    public static bool IsAway(int consecutiveReadFailures, int pollRate) =>
        consecutiveReadFailures >= Threshold(pollRate);
}
