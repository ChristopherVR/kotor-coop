using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Either player's pause pauses both worlds. Each peer publishes its OWN pause in its state
/// flags (mirrored pauses are excluded at the source); this side asks the plugin to hold the
/// engine's pause while any peer's flag is up. The pauser is the only one who can release it —
/// a local space-bar press against a mirrored pause is re-asserted by the plugin.
/// </summary>
internal sealed class PausePump(IGameBridge bridge, AgentSettings settings, Action<string> log)
{
    private bool _remotePauseHeld;

    /// <summary>Serial handed to the plugin; a change is the release.</summary>
    private uint _remotePauseReleaseSerial;

    /// <summary>Whether a peer's release flag is currently being announced, so the level on
    /// the wire is consumed as a single edge.</summary>
    private bool _peerPauseReleaseSeen;

    public void PumpPause(in PlayerState local, IReadOnlyList<RemotePeer> peersHere)
    {
        if (!settings.SyncPause || !bridge.SupportsPauseSync)
        {
            return;
        }

        // ⚠ ONLY PEERS IN THE VICINITY. A pause is for the situation you are BOTH in — the
        // fight you are both standing in, the decision you are both making. Freezing a
        // partner on the other side of the module because you stopped to think is not shared
        // pause, it is one player taking the other's game away, and it is the opposite of
        // seamless. Beyond this range the two of you are not interacting, so there is nothing
        // to keep in step and each world runs on its own.
        //
        // Either of you can still release it: PauseController honours a local unpause and
        // does not re-assert (see "released locally"), so whoever is ready to move on can.
        float rangeSquared = settings.PauseShareRangeUnits * settings.PauseShareRangeUnits;

        bool anyPeerPaused = false;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (!peersHere[i].State.IsPaused)
            {
                continue;
            }

            // Range of 0 shares every pause regardless of distance (coop.ini escape hatch
            // for anyone who wants the old all-or-nothing behaviour back).
            if (settings.PauseShareRangeUnits <= 0f
                || peersHere[i].State.Position.DistanceSquaredTo(local.Position) <= rangeSquared)
            {
                anyPeerPaused = true;
                break;
            }
        }

        if (anyPeerPaused != _remotePauseHeld && bridge.TrySetRemotePause(anyPeerPaused))
        {
            _remotePauseHeld = anyPeerPaused;
            log(anyPeerPaused ? "a peer paused; pausing with them" : "peer pause released");
        }

        // EITHER player may end a shared pause. A peer that released one it did not start
        // says so with FlagPauseReleased, because a release cannot be inferred from the
        // absence of FlagPaused — a mirroring player never had that flag set.
        uint peerReleases = 0;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (peersHere[i].State.HasReleasedPause)
            {
                peerReleases++;
            }
        }

        if (peerReleases > 0 && !_peerPauseReleaseSeen)
        {
            _peerPauseReleaseSeen = true;
            _remotePauseReleaseSerial++;

            if (bridge.TrySignalRemotePauseRelease(_remotePauseReleaseSerial))
            {
                log("a peer ended the shared pause; releasing ours too");
            }
        }
        else if (peerReleases == 0)
        {
            // Re-arm only once the flag has cleared, so one release ends one pause however
            // many ticks the peer keeps announcing it.
            _peerPauseReleaseSeen = false;
        }
    }
}
