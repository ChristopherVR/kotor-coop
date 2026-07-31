namespace KotorCoop.Plugin;

/// <summary>
/// Mirrors a remote peer's pause into this instance, so either player pausing pauses
/// both worlds — the alternative is one player acting freely in a world the other
/// believes is frozen, which is a desync generator with no repair path.
///
/// The mirror uses the engine's own player-pause bit through SetPauseState, so
/// CWorldTimer genuinely stops and the GUI shows the pause. The engine has ONE bit for
/// "player paused", so ownership is a protocol convention: the agent only publishes a
/// pause this player initiated (it excludes the mirror via <c>RemotePauseApplied</c>),
/// and while a peer holds the pause this controller re-asserts it — a local space-bar
/// press cannot release someone else's pause, only the pauser can.
/// </summary>
internal sealed class PauseController(SharedBlockView block)
{
    /// <summary>Frames between passes; ~16 checks a second at the hook's ~317 Hz, so a
    /// locally-cleared mirrored pause is re-asserted faster than a keypress repeats.</summary>
    private const int IntervalFrames = 20;

    private int _countdown;
    private bool _applied;
    private int _logged;

    /// <summary>
    /// Module transitions clear every pause reason on load, and holding a pause through
    /// a loading screen would deadlock it. Drop the mirror; the agent re-requests it
    /// within a tick if the peer is still paused.
    /// </summary>
    public void Invalidate()
    {
        _applied = false;
        block.RemotePauseApplied = 0;
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        bool desired = block.RemotePauseDesired;

        if (desired)
        {
            // ⚠ ORDER IS LOAD-BEARING: claim the pause as MIRRORED before setting the engine
            // bit, and release the bit before disclaiming it. The engine has one "player
            // paused" bit, and the agent decides whether it is this player's own pause by
            // reading that bit together with this flag. Setting the bit first left a window
            // in which the agent saw "paused, and nobody says it is mirrored" and concluded
            // the local player had paused — so it published a pause of its own, the peer
            // mirrored THAT, and both ends latched each other paused with no way back. That
            // is the reported "stuck paused, can never unpause".
            if (!_applied)
            {
                _applied = true;
                block.RemotePauseApplied = 1;
                Odyssey.SetPlayerPause(true);
                LogFew("pause: mirroring a peer's pause");
                return;
            }

            // Re-asserted every pass after that: local input can clear the shared bit, but
            // the pause belongs to the peer who took it.
            if (Odyssey.SetPlayerPause(true))
            {
                LogFew("pause: re-asserted (local input tried to clear a peer's pause)");
            }

            return;
        }

        if (_applied)
        {
            Odyssey.SetPlayerPause(false);
            _applied = false;
            block.RemotePauseApplied = 0;
            LogFew("pause: peer released their pause");
        }
    }

    private void LogFew(string message)
    {
        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
