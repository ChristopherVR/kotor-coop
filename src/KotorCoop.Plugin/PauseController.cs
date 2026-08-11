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

    /// <summary>The local player unpaused while a peer's pause was mirrored; honour it.</summary>
    private bool _releasedLocally;

    /// <summary>The peer release serial already acted on, so one release ends one pause.</summary>
    private uint _appliedReleaseSerial;


    private int _logged;

    /// <summary>
    /// Module transitions clear every pause reason on load, and holding a pause through
    /// a loading screen would deadlock it. Drop the mirror; the agent re-requests it
    /// within a tick if the peer is still paused.
    /// </summary>
    public void Invalidate()
    {
        _applied = false;
        _releasedLocally = false;
        block.RemotePauseApplied = 0;

        // Adopt whatever the agent's counter is at rather than resetting to zero: it keeps
        // counting across a module change, and a reset would make the next read look like a
        // release and unpause the new world.
        _appliedReleaseSerial = block.RemotePauseReleaseSerial;
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // Level-triggered repair for the edge-only engine state (see Odyssey.ReconcilePause):
        // one missed edge otherwise leaves a permanent PAUSED banner and a dead pause key.
        // Runs whether or not a session is active — the stuck state comes from the login
        // chain and from mirror races, and both exist before a peer connects.
        if (Odyssey.HasPlayer && Odyssey.ReconcilePause(out string what))
        {
            LogFew($"pause: reconciler {what}");
        }

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // A peer has ended the shared pause. This runs BEFORE the desired/applied logic and
        // regardless of who started it: the originator's pause is its own engine pause, held
        // by nothing this controller tracks, so honouring the release means clearing it here.
        // ⚠ NO "prime on first change" HERE. An earlier version skipped the first change on
        // the theory that it might be a serial inherited from before we were listening — but
        // the block is zeroed when the plugin allocates it and the agent's counter starts at
        // zero too, so the first change IS the first real release, and priming swallowed it.
        // Measured: the peer announced the release and this side never acted on it.
        // Staleness is handled by adopting the current value on Invalidate instead.
        uint release = block.RemotePauseReleaseSerial;
        if (release != _appliedReleaseSerial)
        {
            _appliedReleaseSerial = release;

            if (Odyssey.IsPlayerPaused)
            {
                Odyssey.SetPlayerPause(false);
                _applied = false;
                _releasedLocally = false;
                block.RemotePauseApplied = 0;
                LogFew("pause: a peer released the shared pause; releasing ours");
                return;
            }
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
                _releasedLocally = false;
                block.RemotePauseApplied = 1;
                Odyssey.SetPlayerPause(true);
                LogFew("pause: mirroring a peer's pause");
                return;
            }

            if (_releasedLocally)
            {
                return;
            }

            // EITHER player may release a shared pause.
            //
            // This used to re-assert the pause every pass, on the principle that it belongs
            // to whoever took it. In play that reads as broken: you press the pause key, the
            // game unpauses for ~50 ms, and the mirror slams it back on, so the second player
            // simply cannot unpause and the banner appears stuck. Being unable to un-pause
            // your own game is worse than the two games briefly disagreeing.
            //
            // So a local clear is taken as intent and honoured for the rest of this episode;
            // the mirror re-engages next time the peer pauses afresh.
            if (!Odyssey.IsPlayerPaused)
            {
                _releasedLocally = true;
                block.RemotePauseApplied = 0;

                // ...and TELL THE SESSION, so the player who started the pause comes out of
                // it too. Honouring the release only locally is what made "unpausing does
                // not unpause both" — reproduced: the originator held its pause after the
                // other player released. Either player may end a shared pause; that is the
                // whole point of it being shared.
                block.LocalPauseReleaseSerial++;
                LogFew("pause: released locally; telling the peer to release theirs too");
                return;
            }

            Odyssey.SetPlayerPause(true);
            return;
        }

        if (_applied)
        {
            // Only force an unpause if WE are still holding the mirror. If the local player
            // already released it, their own pause state is theirs to keep.
            if (!_releasedLocally)
            {
                Odyssey.SetPlayerPause(false);
                LogFew("pause: peer released their pause");
            }

            _applied = false;
            _releasedLocally = false;
            block.RemotePauseApplied = 0;
        }
    }

    private void LogFew(string message)
    {
        // Raised from 20 while the dead-pause-button hunt is open (Handoff 0u): the
        // reconciler can spend that budget in three seconds, and the lines that matter are
        // the ones around a button press, which happens long after the cap would have hit.
        if (_logged < 200)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
