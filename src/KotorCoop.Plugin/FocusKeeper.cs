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

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = CheckIntervalFrames;

        if (!block.KeepAliveUnfocused)
        {
            return;
        }

        // Same opt-in covers auto-pause: two instances pausing themselves on their own
        // combat events is a desync generator. Cheap to re-assert, and a no-op once the
        // bits are already clear.
        if (Odyssey.SuppressAutoPause())
        {
            PluginHost.Log("auto-pause options cleared (they desynchronise two instances)");
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
