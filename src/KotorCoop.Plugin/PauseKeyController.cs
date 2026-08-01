namespace KotorCoop.Plugin;

/// <summary>
/// Fallback for the pause key.
///
/// With <see cref="PauseSanityPatch"/> installed the engine's own space-bar route works
/// again (it always toggled the pause on; the sanity check was reverting it within one
/// tick). This controller exists for the case where that route is dead anyway — measured
/// on some plugin-loaded sessions before the patch, and cheap insurance after it.
///
/// On a rising space edge it does NOT act immediately: it arms a grace window and lets the
/// engine handle the press. Only if the pause state has not moved within the window does it
/// toggle via <see cref="Odyssey.SetPlayerPause"/> — the same call the shared-pause mirror
/// uses. This can never fight the native path: if the engine acted, the state changed, and
/// the fallback disarms without doing anything.
///
/// Runs on the SwapBuffers frame hook (main thread).
/// </summary>
internal sealed class PauseKeyController
{
    /// <summary>How long the engine gets to handle the press before the fallback fires.
    /// The engine's own handling, when it happens at all, lands within a few ticks
    /// (measured single-digit ms); in practice the loopback session never handles it and
    /// the fallback is the pause key, so keep its latency low.</summary>
    private const long GraceMs = 200;

    private bool _spaceWasDown;
    private bool _pending;
    private bool _pausedAtPress;
    private long _pressedAtMs;
    private int _logged;

    public void OnFrame()
    {
        if (!Odyssey.HasPlayer || !Odyssey.IsInGame)
        {
            _pending = false;
            return;
        }

        bool paused = Odyssey.IsPlayerPaused;

        if (_pending)
        {
            if (paused != _pausedAtPress)
            {
                _pending = false; // the engine handled the press; nothing to do
            }
            else if (Environment.TickCount64 - _pressedAtMs >= GraceMs)
            {
                _pending = false;
                Odyssey.SetPlayerPause(!paused);

                if (_logged < 10)
                {
                    _logged++;
                    PluginHost.Log($"pause: space fallback -> {(!paused ? "paused" : "unpaused")}");
                }
            }
        }

        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkSpace);
        bool rising = down && !_spaceWasDown;
        _spaceWasDown = down;

        if (rising && !Odyssey.IsConsoleOpen && !Odyssey.IsConversationActive)
        {
            _pending = true;
            _pausedAtPress = paused;
            _pressedAtMs = Environment.TickCount64;
        }
    }
}
