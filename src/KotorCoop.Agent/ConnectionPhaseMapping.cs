using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Decides which <see cref="ConnectionIndicatorState"/> the persistent indicator and the
/// connection toast show, given <see cref="SessionManager.Phase"/> (the MANAGED lifecycle) and
/// the live <see cref="SessionStats"/> underneath it. Pure, so the state machine is testable
/// without sockets or a clock — see <c>ConnectionPhaseMappingTests</c>.
///
/// WHY THIS EXISTS. Before it, <c>CoopCommandDispatcher.AnnounceConnectionStatus</c> matched
/// <c>SessionPhase.Connecting</c> straight to <see cref="ConnectionIndicatorState.Connecting"/>
/// with no other check — and <c>SessionManager.Poll</c> sets exactly that phase the instant a
/// client's live <c>stats.Connected</c> goes false for ANY reason, including the ordinary
/// giving-up-and-re-Helloing that follows a genuine mid-session outage
/// (<c>CoopClient.ForgetSession</c>, docs/research/disconnect-reconnect.md). Live-verified
/// 2026-08-27 (docs/research/connect-flow.md): a real `netsim blackout` past the 5 s peer
/// timeout showed the CLIENT "co-op: connecting..." — the identical wording as the very first
/// handshake — because by the time <c>stats.Connected</c> read false, <c>manager.Phase</c> had
/// already left the branch that even looked at it.
/// <see cref="ConnectionIndicatorState.Reconnecting"/> was consequently dead code: nothing
/// could ever produce it.
///
/// The fix is <see cref="SessionStats.Reconnecting"/> — CLIENT ONLY, true from the instant
/// <c>ForgetSession</c> gives up and re-Hellos the same nonce until a fresh Welcome answers —
/// checked AHEAD of the raw phase-Connecting arm, so a genuine reconnect attempt reads
/// Reconnecting regardless of what the coarser phase currently says, while a brand-new,
/// never-yet-connected client (where <c>Reconnecting</c> is always false) still reads
/// Connecting exactly as before. A host's own <c>Reconnecting</c> is always false (it has no
/// single link of its own to lose — an individual peer dropping only ever moves the peer
/// count), so a host is never shown Reconnecting for itself, matching the design in
/// docs/research/connect-flow.md ("host = listening/hosting with N peers").
/// </summary>
public static class ConnectionPhaseMapping
{
    public static ConnectionIndicatorState Resolve(SessionPhase phase, SessionStats stats) => phase switch
    {
        // A definitive outcome always wins, even mid-reconnect (e.g. the host's password
        // changed while this client was re-Helloing after an outage).
        SessionPhase.Failed => ConnectionIndicatorState.Failed,
        SessionPhase.Disconnected => ConnectionIndicatorState.Disconnected,

        // THE FIX: checked before SessionPhase.Connecting, which is what SessionManager.Poll
        // sets the instant stats.Connected goes false for any reason — this is what tells
        // "giving up and re-Helloing a known session apart" from "the very first handshake".
        _ when stats.Reconnecting => ConnectionIndicatorState.Reconnecting,

        SessionPhase.Connecting => ConnectionIndicatorState.Connecting,

        // Anything else (in practice only SessionPhase.Connected, since Idle never co-occurs
        // with a live session — see SessionManager.TimeoutConnect/Connect). CoopClient flips
        // Connected and Reconnecting together, atomically, inside ForgetSession, so this arm's
        // "else" is not reachable for a client today — it is the defensive fallback: trust the
        // live link flag over the coarser managed phase rather than assuming the two can never
        // drift apart.
        _ => stats.Connected ? ConnectionIndicatorState.Connected : ConnectionIndicatorState.Reconnecting,
    };
}
