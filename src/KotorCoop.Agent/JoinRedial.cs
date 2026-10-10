using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// A command-line join (<c>--join</c>) keeps dialing a host that has not answered yet.
///
/// The joiner's agent starts with its game, often before the host's game has finished loading
/// its world and opened the port. One unanswered connect ends in "no reply" after 15 s and the
/// manager goes back to Idle, and nothing dialed again: the joiner sat at the main menu while
/// the host came up a minute later (four launches out of six on the K1 Full build, 2026-10-10,
/// where the host's load is slow). Redialing stops for good once the session has connected (the
/// manager's own reconnect owns everything after that), when the host refuses the join (a wrong
/// password or version is not going to change), when the player disconnects, or after
/// <see cref="MaxRedials"/> tries.
/// </summary>
public sealed class JoinRedial
{
    /// <summary>About ten minutes of 15 s attempts.</summary>
    public const int MaxRedials = 40;

    /// <summary>The pause between an unanswered attempt and the next.</summary>
    public const long PauseMs = 3000;

    private bool _connectedOnce;
    private bool _leftIdle;
    private bool _done;
    private int _redials;
    private long? _idleSinceMs;

    public int Redials => _redials;

    /// <summary>One observation of the manager's phase; true when it is time to dial again.</summary>
    public bool ShouldRedial(SessionPhase phase, long nowMs)
    {
        if (_done)
        {
            return false;
        }

        switch (phase)
        {
            case SessionPhase.Connected:
                _connectedOnce = true;
                _done = true;
                return false;
            case SessionPhase.Failed:
            case SessionPhase.Disconnected:
                _done = true;
                return false;
            case SessionPhase.Connecting:
                _leftIdle = true;
                _idleSinceMs = null;
                return false;
        }

        // Idle: before the first dial has been picked up, or after an attempt went unanswered.
        if (!_leftIdle || _connectedOnce)
        {
            return false;
        }

        _idleSinceMs ??= nowMs;
        if (nowMs - _idleSinceMs.Value < PauseMs)
        {
            return false;
        }

        if (_redials >= MaxRedials)
        {
            _done = true;
            return false;
        }

        _redials++;
        _idleSinceMs = null;
        _leftIdle = false;
        return true;
    }

    /// <summary>Watches <paramref name="manager"/> and calls <paramref name="dial"/> again while the
    /// host stays silent. Runs until the join settles one way or the other.</summary>
    public static async Task RunAsync(SessionManager manager, Action dial, Action<string> say, CancellationToken token)
    {
        JoinRedial rule = new();
        while (!token.IsCancellationRequested && !rule._done)
        {
            if (rule.ShouldRedial(manager.Phase, Environment.TickCount64))
            {
                say($"join: the host has not answered yet - dialing again ({rule.Redials} of {MaxRedials})");
                dial();
            }

            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
