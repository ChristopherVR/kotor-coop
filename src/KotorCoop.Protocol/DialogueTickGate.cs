namespace KotorCoop.Protocol;

/// <summary>
/// Decides, once per agent poll tick, whether the dialogue channel should actually do its
/// work this tick — pure arithmetic over a change fingerprint and a timestamp, lifted out of
/// <c>CoopAgent</c> so the decision (and the two failure modes it exists to avoid) is testable
/// without a live game, a socket, or a 150 ms clock nobody wants to sleep through in a test.
///
/// docs/research/dialogue-join-latency.md has the measured timeline this exists to shorten.
/// The short version: the dialogue channel used to run only on the agent's ~150 ms combat
/// cadence (<c>CoopAgent.CombatPumpIntervalMs</c>), on BOTH the owner's publish and the
/// follower's read of it — two hops of up to 150 ms each, on top of everything else already on
/// the wire, for a channel whose own reads are a handful of small block fields, not a world
/// scan. <c>dialogue_fast_join</c> lets the agent check every poll tick instead (as fast as the
/// position path already runs) and this type is the two rules that make "every tick" safe and
/// worth doing rather than just noisy:
///
///   * a CHANGE (a new line, a new node, the conversation starting or ending, the peer's token
///     moving) is acted on the very next tick — this is the entire latency win, and it is what
///     "event-driven, not only on cadence" means here;
///   * an UNCHANGED read is not re-published just because the tick loop happened to run again —
///     without this, "every tick" would mean publishing (and, for a client, sending) the
///     identical payload thirty times a second for the entire length of every conversation,
///     which is the "dedup of redundant sends" half of the same fix. A heartbeat interval still
///     forces a resend periodically regardless of change, because UDP drops packets and a late
///     join or a lost packet must still converge in bounded time — see
///     <see cref="DialogueFollowTimings"/> and this type's own heartbeat parameter for the same
///     shape of argument applied one layer up.
///
/// Turning <c>dialogue_fast_join</c> off is a true rollback, not an approximation of one: the
/// caller simply does not construct or consult this type, and the legacy fixed-interval gate in
/// <c>CoopAgent.PumpOnce</c> runs exactly as it always has.
/// </summary>
public sealed class DialogueTickGate(long heartbeatIntervalMs)
{
    private readonly long _heartbeatIntervalMs = heartbeatIntervalMs;

    /// <summary>Whether <see cref="ShouldPump"/> has been called at least once. The very first
    /// call always fires — there is nothing to compare the fingerprint against yet, and a gate
    /// that started closed would silently swallow the first thing it was ever asked to report.</summary>
    private bool _hasFingerprint;

    private long _lastFingerprint;
    private long _lastPumpAtMs;

    /// <summary>
    /// Whether the dialogue channel should do its work now.
    ///
    /// True exactly when: this is the first call; or <paramref name="fingerprint"/> differs from
    /// the last one that was acted on (a genuine change — the event-driven path); or the
    /// heartbeat interval has elapsed since the last time this returned true (the dedup floor,
    /// so an unchanging conversation is still resent often enough for a late joiner or a lost
    /// packet to converge).
    ///
    /// The fingerprint is the caller's job to build (typically a combined hash of the local
    /// speaker/line/partner tag, the local and remote node/reply serials, and whether a
    /// conversation is active at all) — this type only ever compares it for equality, so it
    /// never needs to know what a dialogue actually is.
    /// </summary>
    public bool ShouldPump(long nowMs, long fingerprint)
    {
        bool changed = !_hasFingerprint || fingerprint != _lastFingerprint;
        bool heartbeatDue = !_hasFingerprint || nowMs - _lastPumpAtMs >= _heartbeatIntervalMs;

        if (!changed && !heartbeatDue)
        {
            return false;
        }

        _hasFingerprint = true;
        _lastFingerprint = fingerprint;
        _lastPumpAtMs = nowMs;
        return true;
    }

    /// <summary>Forget everything, so the next call behaves like the first ever call — a
    /// changed epoch (a module load, a fresh conversation) must not be judged against a
    /// fingerprint from a completely different one.</summary>
    public void Reset()
    {
        _hasFingerprint = false;
        _lastFingerprint = 0;
        _lastPumpAtMs = 0;
    }
}
