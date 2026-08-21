namespace KotorCoop.Agent;

/// <summary>
/// The states the persistent connection indicator (item 18, roadmap-seamless.md) shows on
/// screen. Packs into the LOW BYTE of the shared block's ConnectStatus field — see
/// <see cref="ConnectionSnapshot.Pack"/> — reusing the existing 0/1/2/3 sentinels
/// (<c>SharedBlock.ConnectStatusIdle/Connecting/Connected/Failed</c>) exactly, so nothing
/// that already compared the raw field against one of those four values sees anything
/// different; <see cref="Reconnecting"/> is the one genuinely new value.
/// </summary>
public enum ConnectionIndicatorState : byte
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Failed = 3,
    Reconnecting = 4,
}

/// <summary>
/// What the indicator currently shows: a state, plus — only meaningful while
/// <see cref="State"/> is <see cref="ConnectionIndicatorState.Connected"/> — how many OTHER
/// players share the session.
/// </summary>
public readonly record struct ConnectionSnapshot(ConnectionIndicatorState State, int PeerCount)
{
    /// <summary>
    /// Packs into one uint for the shared block: state in the low byte, peer count (clamped
    /// to a byte — nobody is running a 256-peer session) in the next byte up. The two bytes
    /// above stay zero. No new block field and no version bump: this rides the same word
    /// <c>SharedBlock.OffsetConnectStatus</c> already carries, whose only prior writer set it
    /// to a bare 0-3 and whose only reader (the plugin's own getter) had none — see
    /// docs/research/connect-flow.md's 2026-08-21 note.
    /// </summary>
    public uint Pack() => (byte)State | ((uint)Math.Clamp(PeerCount, 0, 255) << 8);
}

/// <summary>
/// Decides the persistent connection line and reposts it only on change — the same shape
/// <c>CompatibilityAnnouncer</c> and <c>PeerAnnouncer</c> already use, so a stable session
/// says nothing every tick and a player is not drowned in repeats of what they already know.
///
/// WHY THIS EXISTS. Before it, the ONLY way to see connection state in-game was typing
/// <c>status</c> at the <c>~</c> console — nothing was ever shown unasked. A player who is not
/// actively typing has no idea their partner's link dropped until something else goes
/// visibly wrong (a phantom freezes, a door stops responding). This closes that gap with the
/// cheapest surface already in the plugin: the same queued-text path
/// <c>CompatibilityAnnouncer</c> posts through (<c>Reply</c>, which reaches both the console
/// reply line and the always-on chat overlay), reposted on change rather than drawn per frame.
/// </summary>
public sealed class ConnectionStatusAnnouncer
{
    private ConnectionSnapshot? _last;

    /// <summary>
    /// The pure part: what to say for a given snapshot, independent of anything observed
    /// before it. Kept separate from <see cref="Observe"/> so "what changed -&gt; which line"
    /// is testable on its own, with no tick loop, no session and no clock.
    /// </summary>
    public static string Line(ConnectionSnapshot snapshot) => snapshot.State switch
    {
        ConnectionIndicatorState.Disconnected => "co-op: disconnected",
        ConnectionIndicatorState.Connecting => "co-op: connecting...",
        ConnectionIndicatorState.Reconnecting => "co-op: reconnecting...",
        ConnectionIndicatorState.Failed => "co-op: connection failed",
        ConnectionIndicatorState.Connected => snapshot.PeerCount switch
        {
            <= 0 => "co-op: connected, alone",
            1 => "co-op: connected, 1 player",
            _ => $"co-op: connected, {snapshot.PeerCount} players",
        },
        _ => "co-op: unknown",
    };

    /// <summary>
    /// Diffs <paramref name="snapshot"/> against the last observation and announces only when
    /// it actually changed — state, peer count, or both (record-struct value equality, so
    /// "connected, 1 player" -&gt; "connected, 2 players" is a change even though the state
    /// byte alone did not move). Null means "no session at all"; passing it is itself a state
    /// (distinct from every real snapshot), so leaving announces exactly once rather than
    /// staying silent because nothing is left to compare against.
    /// </summary>
    public void Observe(ConnectionSnapshot? snapshot, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (snapshot == _last)
        {
            return;
        }

        _last = snapshot;
        if (snapshot is { } s)
        {
            announce(Line(s));
        }
    }

    /// <summary>The session went away: forget the last line, so a fresh session announces its
    /// own state from scratch instead of staying quiet because it happens to start the same
    /// way the old one ended.</summary>
    public void Reset() => _last = null;
}
