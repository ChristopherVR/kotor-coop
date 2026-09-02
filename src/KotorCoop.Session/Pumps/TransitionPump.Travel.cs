using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Keeps a client in the host's module (or, under lead-swap, keeps the host with a leading
// client) — the Souls-seamless rule that the world follows its owner. See
// docs/research/transition-smoothness.md.
internal sealed partial class TransitionPump
{
    /// <summary>The minigame-track module this pump last refused to follow into, so the
    /// refusal logs once per race rather than at the follow cadence for the whole of it.
    /// Cleared the moment a follow decision reaches a normal destination again.</summary>
    private string _lastRefusedMinigameModule = string.Empty;

    /// <summary>
    /// Keeps a client in the host's module. When the host crosses into another area, the
    /// client warps after it through the game's own transition path; a client whose own
    /// door leads somewhere the host is not gets brought back the same way. That is the
    /// Souls-seamless rule — the world follows its owner — and it is one-directional by
    /// construction, so two instances can never chase each other in a loop.
    ///
    /// <paramref name="changedModule"/> and <paramref name="moduleSettledAtMs"/> are
    /// CoopAgent's own module-settle bookkeeping (computed once per tick, ahead of every
    /// pump, so the module-change/world-adopted events and this settle window are always
    /// judging the same read) — not owned here because they also drive events raised
    /// elsewhere in the schedule.
    /// </summary>
    public void PumpTravel(
        in PlayerState local, IReadOnlyList<RemotePeer> peers, bool changedModule, long moduleSettledAtMs)
    {
        // Computed and re-sent every tick, ahead of every early return below: the plugin has
        // no notion of host/client and simply trusts whatever this says right now, so a stale
        // "armed" left behind by an early return here would outlive whatever justified it.
        bool isClient = !session.IsEnemyAuthority;
        bool isHost = !isClient;
        bool hostRecentlyTransitioning =
            SessionClock.NowMs - _lastHostTransitionSignalAtMs < HostTransitionSignalWindowMs;

        // Lead-swap: under follow_lead=any the client's own crossing is only refused while the
        // HOST could not safely follow it right now - read straight off the host's own
        // published PlayerState flags on its RemotePeer, the same way this method already
        // reads everything else about the host. Only ever consulted on a client
        // (FindHostPeer(peers) is meaningless called as the host, since the host is never in
        // its own remote-peer list).
        RemotePeer? hostPeerForRefusal = isClient ? FindHostPeer(peers) : null;
        bool hostBusyForRefusal = hostPeerForRefusal is { } refusalHost
            && (refusalHost.State.IsInDialogue || refusalHost.State.IsInCombat);

        bridge.TryArmClientDoorRefusal(ClientDoorRefusalRule.Decide(
            isClient, settings.FollowHostModule, settings.ClientDoorRefusalEnabled,
            hostRecentlyTransitioning, settings.FollowLeadMode, hostBusyForRefusal));

        if (!settings.FollowHostModule
            || !bridge.SupportsTravel
            || local.Module.Length == 0)
        {
            return;
        }

        // ⚠ AND NOT WHILE WE DO NOT KNOW WHERE WE ARE.
        //
        // "areaXX" is the bridge's fallback when the module NAME is unreadable, and the name
        // is unreadable for exactly one reason: a transition is in progress here. The target
        // side of this method has always rejected that value; the LOCAL side did not, so
        // mid-load we compared a placeholder against the host's real module, concluded we
        // were separated, and fired a second transition into the middle of the first.
        //
        // Measured, twice: two travels 1.1 s apart, then the client dead in
        // CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D, which dereferences
        // CClientExoApp::GetModule() with no null check while the previous world's
        // creature-add messages are still being serviced.
        if (ModuleNameGuard.IsFallbackModuleName(local.Module))
        {
            return;
        }

        // Never yank the player out of a conversation; the module mismatch survives
        // until the next pump anyway.
        if (bridge.IsInDialogue)
        {
            return;
        }

        // ---- the fast path: an announced transition, ahead of the settle debounce below ---
        //
        // "Announced transitions": the host confirmed its own load began BEFORE its published
        // module had any chance to change, so this can race ahead of everything below it — the
        // settle window this player's own last transition opened, and the "does the host's
        // published module already differ from ours" check the slow path uses instead. Both
        // exist to protect against firing on stale or unreliable information; an announce is
        // neither, and racing them is the entire point (both loading screens overlapping
        // instead of running back to back).
        //
        // Gated on the coop.ini flag: when off, _pendingAnnounce is never populated at all
        // (PumpTransitionAnnounce's own gate), so this is always a no-op and every following
        // module change is decided purely by the pre-existing path below, unchanged.
        //
        // isHost matters here too, and only here: under lead-swap (FollowLeadMode.Any) this is
        // now the ONLY way the host ever travels on a peer's behalf — see
        // TryFollowAnnouncedTransition's host branch and LeadFollowRule for the
        // eligibility/safety gates that apply to it.
        if (settings.TransitionAnnounceEnabled
            && TryFollowAnnouncedTransition(local, peers, isHost, changedModule, moduleSettledAtMs))
        {
            return;
        }

        // The host has no SLOW path. Unlike a client, it never compares a peer's published
        // module against its own to decide whether to leave — the host never follows anyone
        // holds for everything below this line, exactly as it always has. The fast path above
        // is the ONLY way the host ever travels on a peer's behalf, and only when FollowLeadMode
        // allows it and LeadFollowRule judges it safe; when the fast path found nothing to do
        // (no pending announce, an ineligible one, or the host currently unsafe to interrupt),
        // the host simply does nothing further this tick.
        if (isHost)
        {
            return;
        }

        // ⚠ LET A MODULE SETTLE BEFORE LEAVING IT AGAIN. A transition issued moments after
        // the last one finished killed the client outright, measured:
        //
        //   07:48:17.766  travel: moving to module 'end_m01ab'      (this player's own move)
        //   07:48:19.604  travel: moving to module 'END_M01AA'      (the follow, 1.8 s later)
        //   07:48:20.266  CRASH at CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D
        //
        // That instruction is `mov ebp,[eax+0x48]` on the result of
        // CClientExoApp::GetModule, dereferenced with no null check — so the client was
        // still being handed creature-add messages for a world it no longer had. The load
        // had already COMPLETED (the module name read fine, which is how the follow fired at
        // all), so gating on "loading" would not have caught it; what overlaps is the burst
        // of object messages that follows a world coming up.
        //
        // Retail never reaches this because its transitions are player-driven and never
        // back-to-back. Follow-the-host can be, and a client walking through its own door is
        // exactly how: it arrives somewhere the host is not and is immediately brought back.
        if (changedModule && SessionClock.NowMs - moduleSettledAtMs < settings.ModuleSettleMs)
        {
            return;
        }

        RemotePeer? host = FindHostPeer(peers);
        string target = host?.State.Module ?? string.Empty;

        if (target.Length == 0 || host!.IsInModule(local.Module))
        {
            // Together (or the host is unreadable): clear the debounce so the next real
            // separation acts immediately.
            _lastTravelRequest = string.Empty;

            // And drop any accumulated cinematic — but ONLY if the host was actually seen
            // somewhere else at some point since we started accumulating. The host announces a
            // movie the instant its engine accepts it onto the transition queue, which is
            // BEFORE its main thread blocks to play it and therefore before its published
            // module has any chance to change; for that whole window "together" just means the
            // host has not left yet, not that the movies are stale. Only having actually
            // observed the host away in between means this "together" is a genuine reunion and
            // these really are leftovers from an earlier, already-resolved transition.
            if (_pendingFollowMovies.Count > 0 && _pendingFollowMoviesSawHostAway)
            {
                log($"dropping {_pendingFollowMovies.Count} pending transition movie(s): "
                    + "we are already where the host is");
                _pendingFollowMovies.Clear();
                _pendingFollowMoviesSawHostAway = false;
            }

            return;
        }

        // We just established the host is NOT in our module — regardless of what happens next
        // (a stunt module, an unreadable name, or a real follow), this is the observation that
        // makes a LATER "together" trustworthy rather than the same announce-before-departure
        // race the guard above exists to ignore.
        if (_pendingFollowMovies.Count > 0)
        {
            _pendingFollowMoviesSawHostAway = true;
        }

        // Stunt modules stage cutscenes. The host will be dumped back into a real module
        // when its scene ends; following it in would trap this player in someone else's
        // cinematic. See StuntModuleRule for the same test shared with the state publishers.
        if (StuntModuleRule.IsStuntModule(target))
        {
            return;
        }

        // ⚠ AND NEVER ONTO A MINIGAME TRACK, for the same reason a stunt module is refused
        // one line above and then some: a swoop/turret area is SINGLE-SEAT, and the
        // 2026-08-29 live round watched this exact follow (into tar_m03mg) be the last thing
        // the following instance did before it died. Two gates, either sufficient — the peer
        // publishing FlagInMinigame (works for a track module this build has never heard of)
        // and the destination naming a known one (works one poll EARLIER, before the flag has
        // been read). See MinigameModuleRule and docs/research/minigames.md §24. Deliberately
        // a plain `return`, not a "spent" clear: the peer comes back off the track by
        // themselves and the ordinary reunion path then applies unchanged.
        if (MinigameModuleRule.ShouldRefuseFollow(
                settings.LockPeersDuringMinigame,
                host!.State.IsInMinigame,
                target,
                settings.ExtraMinigameModules))
        {
            if (!string.Equals(target, _lastRefusedMinigameModule, StringComparison.OrdinalIgnoreCase))
            {
                _lastRefusedMinigameModule = target;
                log($"not following to '{target}': a peer is in a single-seat minigame there "
                    + "— waiting for them instead");
            }

            return;
        }

        _lastRefusedMinigameModule = string.Empty;

        // "areaXX" is the bridge's fallback when the module NAME could not be read (a
        // transition in flight on the host's side). It is an object id, not a module, and
        // asking the engine to load it would fail at best. Wait for the real name.
        if (ModuleNameGuard.IsFallbackModuleName(target))
        {
            return;
        }

        long now = SessionClock.NowMs;
        if (string.Equals(target, _lastTravelRequest, StringComparison.OrdinalIgnoreCase)
            && now < _travelRetryAtMs)
        {
            return;
        }

        // The cinematic the host's transitions announced on the way here rides with the warp:
        // TravelController queues these immediately before Odyssey.TravelToModule, which is
        // the same path a movie was measured playing on for real (15,531 ms, run A).
        string[] movies = [.. _pendingFollowMovies];

        if (bridge.TryRequestTravel(target, string.Empty, movies))
        {
            _lastTravelRequest = target;
            _travelRetryAtMs = now + settings.FollowRetryMs;

            // Spent, not kept. A retry of the SAME follow re-sends them (the request is
            // rewritten from scratch each time), but they must not survive into a later,
            // unrelated transition.
            _pendingFollowMovies.Clear();
            _pendingFollowMoviesSawHostAway = false;

            log(movies.Length > 0
                ? $"following the host to '{target}' with {movies.Length} transition movie(s): "
                  + string.Join(", ", movies)
                : $"following the host to '{target}'");
        }
    }

    private static RemotePeer? FindHostPeer(IReadOnlyList<RemotePeer> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == CoopHost.HostPeerId)
            {
                return peers[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The fast path itself: consumes <see cref="_pendingAnnounce"/> if it names somewhere
    /// worth going, through the SAME standing gates the slow path applies to whatever module
    /// it is about to warp to (stunt, fallback name, the retry debounce) — an announced
    /// destination skips the WAIT, never the safety checks. Returns true when it has fully
    /// handled this tick's travel decision (consumed the announce, whether or not that meant
    /// actually warping) so the slow path below is skipped; false hands the tick to it
    /// unchanged.
    ///
    /// <paramref name="isHost"/> selects which rule judges the pending announce: a client
    /// always uses <see cref="TransitionAnnounceRule.Decide"/> unchanged (it only ever follows
    /// the host, exactly as before lead-swap). A host uses <see cref="LeadFollowRule"/> instead,
    /// which additionally gates on eligibility (<see cref="AgentSettings.FollowLeadMode"/>) and
    /// safety (the host must not be mid its own transition, in dialogue, or in real combat) —
    /// see that class's own doc for why a client's decision does not need either.
    ///
    /// A host-side "not safe right now" is deliberately NOT the same as "stale": the announce is
    /// KEPT (not cleared) so a client's lead is retried once the host frees up, rather than lost
    /// to a moment's bad timing — the announce channel already repeats the underlying packet
    /// five ticks for exactly this kind of transient miss, and this extends the same idea to a
    /// slower-moving cause.
    /// </summary>
    private bool TryFollowAnnouncedTransition(
        in PlayerState local, IReadOnlyList<RemotePeer> peers, bool isHost, bool changedModule, long moduleSettledAtMs)
    {
        if (_pendingAnnounce is not { } announce)
        {
            return false;
        }

        if (isHost)
        {
            if (announce.Module.Length == 0
                || string.Equals(announce.Module, local.Module, StringComparison.OrdinalIgnoreCase))
            {
                // Nothing to do (or decoded to nothing): spent, not kept — the same rule the
                // client-side path below applies to its own reunion.
                _pendingAnnounce = null;
                return false;
            }

            // "Mid our own transition" needs more than IsFallbackModuleName: that only catches
            // the LOAD itself, not the ~0.5 s fade window before the module name blanks, during
            // which our own outbound announce/movie may already be under way — see
            // _lastOwnTransitionSignalAtMs's own doc.
            bool hostMidTransition = ModuleNameGuard.IsFallbackModuleName(local.Module)
                || SessionClock.NowMs - _lastOwnTransitionSignalAtMs < OwnTransitionSignalWindowMs
                || (changedModule && SessionClock.NowMs - moduleSettledAtMs < settings.ModuleSettleMs);

            if (!LeadFollowRule.HostMaySafelyFollow(hostMidTransition, bridge.IsInDialogue, local.IsInCombat))
            {
                // Merely busy right now - KEEP the announce (see this method's own doc) and
                // retry next tick rather than dropping a lead that is still worth taking.
                return false;
            }
        }
        else if (TransitionAnnounceRule.Decide(true, announce.Module, local.Module) != TransitionFollowPath.Announced)
        {
            // Named our own module (or decoded to nothing): spent, not kept — the same rule
            // that clears the movie accumulation on a genuine reunion.
            _pendingAnnounce = null;
            return false;
        }

        // The stunt gate and the fallback-name guard apply to an announced destination
        // exactly as they do to the host's published module in the slow path below — an
        // announcement is earlier information, not different rules.
        if (StuntModuleRule.IsStuntModule(announce.Module)
            || ModuleNameGuard.IsFallbackModuleName(announce.Module))
        {
            _pendingAnnounce = null;
            return false;
        }

        // ⚠ AND NEVER ONTO A MINIGAME TRACK. A swoop/turret area is single-seat: following the
        // racer in gets this player a camera bolted to a bike they cannot drive, and the
        // 2026-08-29 live round watched the follow-warp into tar_m03mg be the last thing the
        // following instance did before it died. Same shape as the stunt gate above, same
        // "spent, not kept" treatment — the announce named somewhere we are never going.
        // See MinigameModuleRule and docs/research/minigames.md §24.
        if (MinigameModuleRule.ShouldRefuseFollow(
                settings.LockPeersDuringMinigame,
                ownerInMinigame: false,
                announce.Module,
                settings.ExtraMinigameModules))
        {
            log($"not following the announced transition to '{announce.Module}': "
                + "that is a single-seat minigame track");
            _pendingAnnounce = null;
            return false;
        }

        long now = SessionClock.NowMs;
        if (string.Equals(announce.Module, _lastTravelRequest, StringComparison.OrdinalIgnoreCase)
            && now < _travelRetryAtMs)
        {
            // Already following this exact destination — the slow path (or a previous
            // announce) already fired it. Consumed, no second request.
            _pendingAnnounce = null;
            return true;
        }

        // Same cinematic-riding rule as the slow path: whatever the host's transitions
        // announced on the way here rides with this warp too.
        string[] movies = [.. _pendingFollowMovies];

        if (!bridge.TryRequestTravel(announce.Module, announce.Waypoint, movies))
        {
            return true; // consumed either way: retrying the same stale announce next tick
                         // would just be the slow path's job now
        }

        _lastTravelRequest = announce.Module;
        _travelRetryAtMs = now + settings.FollowRetryMs;
        _pendingFollowMovies.Clear();
        _pendingFollowMoviesSawHostAway = false;
        _pendingAnnounce = null;

        // Who we just followed — "the host" for a host-led announce (unaffected by lead-swap),
        // or the leading client's own name/peer id when the HOST followed a client's lead.
        bool announcerIsHost = announce.PeerId == CoopHost.HostPeerId;
        string leaderLabel = peers.FirstOrDefault(p => p.PeerId == announce.PeerId)?.Name is { Length: > 0 } peerName
            ? peerName
            : announcerIsHost ? "the host" : $"peer {announce.PeerId}";

        log($"following {leaderLabel} to '{announce.Module}' IMMEDIATELY (announced transition "
            + $"serial {announce.Serial}, not waiting for the module-change settle)"
            + (movies.Length > 0
                ? $" with {movies.Length} transition movie(s): " + string.Join(", ", movies)
                : string.Empty));

        // The on-screen line. Best-effort: PostChatLine returns false, harmlessly, on a bridge
        // with no overlay — the warp itself does not depend on it.
        string display = settings.ModuleDisplayName?.Invoke(announce.Module) ?? announce.Module;
        bridge.PostChatLine($"following {leaderLabel} to {display}");

        return true;
    }
}
