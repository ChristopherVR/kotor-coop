namespace KotorCoop.Plugin;

/// <summary>
/// Watches the agent-written heartbeat (SharedBlock.OffsetAgentHeartbeat) and turns a
/// stalled or never-started agent into something a player can actually see and act on,
/// instead of silently frozen phantoms with nothing on screen to explain why.
///
/// Runs on MainLoopHook (menu AND in-world — the same reason ConsoleAssistController
/// does), because both failure modes it reports can happen before a single SwapBuffers
/// frame ever fires: the agent can fail to start while the player is still looking at the
/// menu movie, and a hung agent freezes phantoms whether the player is in a world or not.
///
/// The on-screen line reuses the same queued-text render surface every other overlay in
/// this plugin uses (Odyssey.PostTextLine, re-posted every frame per
/// docs/research/text-render-queued.md) — there is no separate render path to invent.
/// </summary>
internal sealed unsafe class AgentLivenessController(SharedBlockView block)
{
    // Below the console's own rows (0-2) and ConsoleAssistController's hint row (3); well
    // clear of ChatOverlayController's shallowest anchor row (11) — see its anchor notes.
    private const int MessageColumn = 3;
    private const int MessageRow = 5;
    private const float MessageAlpha = 0.9f;

    // A short life is fine (and correct): the line is re-posted every frame this controller
    // runs, so it stays crisp; if OnFrame ever stopped running the message would fade out
    // in well under a second rather than linger as a stale warning forever.
    private const float MessageLife = 0.5f;

    private const string StaleText =
        "co-op agent stopped responding - phantoms frozen; type reconnect";

    private AgentLivenessState _lastState = AgentLivenessState.NeverLive;
    private string? _lastMessageText;
    private byte[] _messageBytes = [];

    /// <summary>
    /// True while the agent is judged Stale or Dead. <see cref="PhantomController"/>
    /// consults this and skips spawning, destroying and driving remote-peer bodies for the
    /// frame, rather than keep acting on a shared-block snapshot nobody is updating any
    /// more. Recovers to false automatically the moment the heartbeat resumes.
    /// </summary>
    public static bool RemoteWritesFrozen { get; private set; }

    public void OnFrame()
    {
        long now = Environment.TickCount64;
        long lastBeat = block.AgentHeartbeat;
        AgentLivenessState state = AgentLiveness.Evaluate(lastBeat, now);

        if (state != _lastState)
        {
            // Logged once per transition, not once per frame — this fires maybe twice in a
            // whole session (down, then recovered), never floods the log.
            PluginHost.Log($"agent: liveness {_lastState} -> {state}");
            _lastState = state;
        }

        RemoteWritesFrozen = state is AgentLivenessState.Stale or AgentLivenessState.Dead;

        if (state == AgentLivenessState.Dead)
        {
            // Cheap to call every frame: AgentLauncher gates the actual relaunch on the
            // cooldown and on having started the process in the first place, so this is a
            // no-op on every frame but the rare one that truly fires.
            AgentLauncher.TryRelaunch(lastBeat, now);
        }

        string? message = state switch
        {
            AgentLivenessState.Stale or AgentLivenessState.Dead => StaleText,

            // A relaunch (or the player starting one by hand) can still land after this —
            // once the heartbeat moves at all the state leaves NeverLive and the line stops
            // on its own, so nothing needs to clear it explicitly here.
            AgentLivenessState.NeverLive when AgentLauncher.StartupFailure is { } reason =>
                $"co-op agent did not start: {reason}",

            _ => null,
        };

        if (message is not null)
        {
            Draw(message);
        }
    }

    private void Draw(string text)
    {
        if (text != _lastMessageText)
        {
            _lastMessageText = text;
            _messageBytes = EncodeAscii(text);
        }

        if (_messageBytes.Length == 0)
        {
            return;
        }

        Odyssey.SetQueuedTextAlpha(MessageAlpha);
        fixed (byte* p = _messageBytes)
        {
            Odyssey.PostTextLine(p, MessageColumn, MessageRow, MessageLife);
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    private static byte[] EncodeAscii(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        byte[] bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        bytes[^1] = 0;
        return bytes;
    }
}
