namespace KotorCoop.Session;

/// <summary>
/// The third way a CLIENT declares its host lost, beside <see cref="HostLossRule"/>'s silence
/// and the explicit <c>HostBye</c>: the host is still SENDING, but what it sends says it is not
/// in the world. A host parked at the main menu, a load screen or the character screen keeps
/// its agent and session alive — every world snapshot still arrives, so neither existing
/// trigger ever fires — while its published <see cref="KotorCoop.Protocol.PlayerState.FlagAway"/>
/// (<see cref="AwayRule"/>) tells every joiner exactly that. Before this rule, the joiners sat
/// in a frozen world holding every plugin-side lock (control lock, follow mirroring, dialogue
/// spectate, ...) for as long as the host stayed parked — docs/review-seamless-2026-09-18.md §2.
///
/// Sustained away (longer than <see cref="DefaultTimeoutMs"/>, coop.ini
/// <c>host_away_timeout_ms</c>) drives the SAME <c>HostLost</c> rising edge the silence path
/// uses, so the release (<c>CoopAgent.PumpOnce</c> → <c>ClearCoopState</c>) is the one already
/// proven live, not a second copy — and the host's flag clearing drives the falling edge, so
/// mirroring resumes exactly as it does when a silent host returns. The grace period is long
/// on purpose: a load screen or a save-menu visit is a normal ten-second away, and releasing
/// every lock for those would be far more disruptive than the frozen world it prevents.
///
/// Pure, like its two siblings, so the timing is tested without a socket or a clock.
/// </summary>
public static class HostAwayRule
{
    /// <summary>coop.ini <c>host_away_timeout_ms</c> default: 20 s. A module load on a slow
    /// disk is well under this; a host who has genuinely gone to the main menu is well over it.</summary>
    public const int DefaultTimeoutMs = 20_000;

    /// <summary>
    /// Advances the "away since" stamp: 0 while the host is in the world, the time it was first
    /// seen away once it is (held, not bumped, on later ticks), and back to 0 the moment the
    /// flag clears — so a host that flickers away for one snapshot never accumulates toward the
    /// timeout across separate short absences.
    /// </summary>
    public static long Track(long awaySinceMs, bool hostAwayNow, long nowMs) =>
        hostAwayNow ? (awaySinceMs == 0 ? nowMs : awaySinceMs) : 0;

    /// <summary>
    /// True the instant the host has been continuously away for longer than
    /// <paramref name="timeoutMs"/>, provided the release feature is enabled (the same
    /// <see cref="SessionSettings.HostLossReleaseEnabled"/> gate the silence path honours), the
    /// timeout is positive (0 disables this trigger alone), and host loss is not already
    /// declared by any path. Idempotent per absence, like <see cref="HostLossRule.ShouldDeclare"/>.
    /// </summary>
    public static bool ShouldDeclare(bool enabled, int timeoutMs, bool alreadyLost, long awaySinceMs, long nowMs) =>
        enabled && timeoutMs > 0 && !alreadyLost && awaySinceMs != 0 && nowMs - awaySinceMs > timeoutMs;

    /// <summary>
    /// True when host loss was declared BY THIS RULE and the host is back in the world — the
    /// falling edge. A loss declared by silence or by <c>HostBye</c> is never restored here;
    /// those clear on the fresh Welcome that readmits the client, and a host that went silent
    /// while flagged away must not be "restored" by a stale flag reading.
    /// </summary>
    public static bool ShouldRestore(bool lostByAway, bool hostAwayNow) => lostByAway && !hostAwayNow;
}
