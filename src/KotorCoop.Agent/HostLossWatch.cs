namespace KotorCoop.Agent;

/// <summary>What the host-loss watch wants the agent to do this tick.</summary>
public enum HostLossAction
{
    None,

    /// <summary>Say once that the host has stopped responding and how to play on alone.</summary>
    Warn,

    /// <summary>Stop retrying: drop to Disconnected and say how to try again.</summary>
    GiveUp,
}

/// <summary>
/// How long a client keeps silently retrying a host that has gone. Before this a client that
/// lost the host showed only "co-op: reconnecting..." forever. Counts from the moment the
/// client itself started re-Helloing (<c>SessionStats.Reconnecting</c>, set ~5 s after the
/// last packet), so the quick cases are untouched: a crashed host releases the client within
/// seconds (that is the session's own host-loss release, not this) and a host loading a save
/// has the client rejoining in 10-20 s, far inside <see cref="WarnAfterMs"/>.
/// Pure and clock-agnostic: the caller passes the time, so tests drive it with a fake clock.
/// </summary>
public sealed class HostLossWatch
{
    /// <summary>Silence before the one "host has stopped responding" announcement.</summary>
    public const long WarnAfterMs = 60_000;

    /// <summary>Silence before the client stops retrying and drops to Disconnected.</summary>
    public const long GiveUpAfterMs = 300_000;

    public const string WarnLine =
        "Host not responding. Still trying. F9 > Connection help.";

    public const string GiveUpLine =
        "Stopped trying after 5 min. F9 > Retry last connection.";

    private long _sinceMs = -1;
    private bool _warned;

    /// <summary>
    /// Feed one observation per tick. <paramref name="reconnecting"/> is true only while a
    /// client that had a session is re-Helloing; anything else (connected, no session, a host)
    /// resets the watch so the next outage gets its own full countdown.
    /// </summary>
    public HostLossAction Observe(bool reconnecting, long nowMs)
    {
        if (!reconnecting)
        {
            Reset();
            return HostLossAction.None;
        }

        if (_sinceMs < 0)
        {
            _sinceMs = nowMs;
        }

        long elapsed = nowMs - _sinceMs;
        if (elapsed >= GiveUpAfterMs)
        {
            Reset();
            return HostLossAction.GiveUp;
        }

        if (!_warned && elapsed >= WarnAfterMs)
        {
            _warned = true;
            return HostLossAction.Warn;
        }

        return HostLossAction.None;
    }

    public void Reset()
    {
        _sinceMs = -1;
        _warned = false;
    }
}
