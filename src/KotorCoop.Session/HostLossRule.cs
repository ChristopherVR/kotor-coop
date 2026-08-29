namespace KotorCoop.Session;

/// <summary>
/// The decision half of client-side host-loss detection, separated from
/// <see cref="CoopClient"/>'s own bookkeeping so the threshold is unit-tested without a socket,
/// a session, or a clock — the same split <see cref="AwayRule"/> and <c>SoloSegmentRule</c> use
/// for their own timing decisions.
///
/// Two independent triggers declare a host lost (<c>CoopClient.HostLost</c>): sustained silence
/// past a configured timeout (<see cref="ShouldDeclare"/>, the ordinary "the host crashed or the
/// network dropped" case), or an explicit <c>HostByePacket</c> (a clean host shutdown, handled
/// directly by <c>CoopClient.HandleHostBye</c> rather than through this rule — there is no
/// timing decision to make there, only "did one arrive"). Both are gated on
/// <see cref="SessionSettings.HostLossReleaseEnabled"/>: OFF means neither trigger ever declares
/// anything, restoring the pre-existing behaviour where locks are released only by a full manual
/// disconnect.
/// </summary>
public static class HostLossRule
{
    /// <summary>
    /// True the instant sustained silence since the last inbound member-traffic packet
    /// (<paramref name="silenceMs"/>) exceeds <paramref name="timeoutMs"/>, provided the
    /// feature is enabled and the client has not already declared host loss.
    /// <paramref name="alreadyLost"/> makes this idempotent per outage: once declared, it stays
    /// declared until the caller clears it (a fresh Welcome), so calling this every tick never
    /// re-fires anything.
    /// </summary>
    public static bool ShouldDeclare(bool enabled, bool alreadyLost, long silenceMs, int timeoutMs) =>
        enabled && !alreadyLost && silenceMs > timeoutMs;
}
