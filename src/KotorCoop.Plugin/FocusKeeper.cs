namespace KotorCoop.Plugin;

/// <summary>
/// Keeps an unfocused instance simulating.
///
/// KOTOR pauses itself when its window loses focus: the WM_ACTIVATEAPP handler calls
/// `Global::DeactivateRenderWindow` (`0x00401D90`), which calls
/// `CServerExoApp::SetPauseState(2, 1)`, which calls `CWorldTimer::PauseWorldTimer`. For
/// single player that is a courtesy. For co-op it is fatal, because only one of the two
/// windows can be focused and the other one stops dead — its player would freeze from
/// everyone else's point of view.
///
/// This clears that one pause reason, once per frame, through the engine's own setter, so
/// the world timer restarts and the state change is announced normally. Only bit
/// <see cref="Odyssey.PauseReasonUnfocused"/> is touched: a real pause the player asked
/// for, or one a script imposed, uses different bits and is left alone.
///
/// Input is deliberately NOT re-enabled. `DeactivateRenderWindow` also calls
/// `SetInputActive(0)`, and that part is correct — an unfocused instance should keep
/// simulating but must not consume keystrokes meant for the other window.
/// </summary>
internal sealed class FocusKeeper(SharedBlockView block)
{
    /// <summary>
    /// Frames between checks. The pause is cleared within a frame or two of the window
    /// losing focus, which is imperceptible, and this keeps a pointer chase off the
    /// ~317 Hz path.
    /// </summary>
    private const int CheckIntervalFrames = 30;

    private int _countdown;
    private uint _cleared;

    /// <summary>Activate the render window on launch so the instance is not black until it
    /// is clicked. Off restores the stock behaviour for anyone who wants it.</summary>
    private readonly bool _activateRender = PluginConfig.ReadBool("activate_render_on_start", true);

    private int _activationsLogged;

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = CheckIntervalFrames;

        // Before the KeepAliveUnfocused opt-in, because this is not about staying alive
        // while unfocused — it is about ever drawing at all. A launched instance comes up
        // with its render window unactivated and stays black until the player clicks into
        // it, which with two windows means clicking both every single launch. Measured: the
        // flag is a latch, so this fires once and then costs a compare per pass.
        if (_activateRender && Odyssey.EnsureRenderWindowActive() && _activationsLogged < 5)
        {
            // The load chain deactivates and this re-activates several times before the
            // world settles, which is working as intended but is not worth five lines a
            // launch, let alone a line every time it ever happens.
            _activationsLogged++;
            PluginHost.Log("render window activated (it comes up black until something does)");
        }

        if (!block.KeepAliveUnfocused)
        {
            return;
        }

        // Same opt-in covers auto-pause: two instances pausing themselves on their own
        // combat events is a desync generator. Cheap to re-assert, and a no-op once the
        // bits are already clear.
        if (Odyssey.SuppressAutoPause())
        {
            PluginHost.Log(
                "auto-pause and tutorial windows cleared (they desynchronise two instances,"
                + " and a tutorial box stalls a fight until someone clicks it)");
        }

        if (Odyssey.ClearPauseReason(Odyssey.PauseReasonUnfocused))
        {
            _cleared++;
            block.PausesCleared = _cleared;

            // Only the first few, or a session spent alt-tabbing would fill the log.
            if (_cleared <= 5)
            {
                PluginHost.Log($"cleared focus-loss pause (#{_cleared}); instance keeps simulating");
            }
        }
    }
}
