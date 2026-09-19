using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// The stunt-leg replay (docs/research/cinematics.md, 2026-09-18 section): a leader's scripted
// StartNewModule into a STUNT_* module — the Taris vision, the Bastila/Revan visions, the
// romance payoffs, the credits chain — is relayed through the ordinary transition announce
// (the plugin's move_to_module_pending edge already names the destination and entry waypoint)
// and the follower REPLAYS it on its own engine through the same travel request every other
// follow uses, instead of refusing it and later teleporting past the whole scene. Same shape
// as the galaxy-map departure relay (TransitionPump.GalaxyDeparture.cs), generalised to every
// script-driven leg. No new engine surface: the replay is Odyssey.TravelToModule, which is the
// same CServerExoAppInternal::StartNewModule funnel the leader's own script went through.
internal sealed partial class TransitionPump
{
    /// <summary>
    /// True while THIS engine is inside a stunt leg it entered on purpose — a replay it
    /// requested, its own scripted departure (own announce into a stunt module), or simply
    /// having been observed standing in one. While set: every follow decision is held (the
    /// leg's own scripts are the exit — <see cref="StuntLegFollowRule.ShouldHoldFollowsInOwnLeg"/>),
    /// every further stunt announce from the leader is the same chain seen from the other
    /// seat and is dropped, and the leader's transition movies for the chain are dropped too
    /// (this engine plays them natively as its own hops run). Ends when this engine is next
    /// seen in an ordinary module after having been in a stunt one, or on the hop timeout.
    /// </summary>
    private bool _stuntLegActive;

    /// <summary>When the current hop began here — the replay request, or the moment a NEW
    /// stunt module was observed locally. Restarted per hop so a six-hop credits chain is
    /// bounded hop by hop (<see cref="StuntLegFollowRule.HopWindowMs"/>).</summary>
    private long _stuntLegHopStartedAtMs;

    /// <summary>Whether this engine has actually been observed inside a stunt module since
    /// the leg began — distinguishes "the replay is still loading" from "the leg is over".</summary>
    private bool _stuntLegSawStuntLocally;

    /// <summary>The stunt module the current hop clock was started for, so a chain's next hop
    /// (a different module) restarts the clock and the same module does not.</summary>
    private string _stuntLegCurrentModule = string.Empty;

    /// <summary>The stunt module whose hop overran <see cref="StuntLegFollowRule.HopWindowMs"/>
    /// here, so the released follows are not re-held for it on the very next tick. Cleared the
    /// moment this engine is back in an ordinary module.</summary>
    private string _stuntLegGaveUpOn = string.Empty;

    /// <summary>
    /// Stunt modules this engine has been through recently (replayed, announced by its own
    /// engine, or observed locally) and when — the per-leg de-dup. A stunt announce naming one
    /// of these inside <see cref="StuntLegFollowRule.HopWindowMs"/> is dropped: it is either the
    /// echo of our own replay warp coming back off a peer (the plugin announces every co-op
    /// warp) or a hop of a chain this engine already ran itself.
    /// </summary>
    private readonly Dictionary<string, long> _stuntModulesRecentlyThrough = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stunt module the slow path last refused, so the refusal logs once per leg
    /// rather than at the follow cadence.</summary>
    private string _lastRefusedStuntModule = string.Empty;

    private bool InStuntLeg => _stuntLegActive;

    /// <summary>
    /// Runs every tick at the top of <see cref="PumpTravel"/>, ahead of its early returns:
    /// notes stunt modules this engine passes through, starts a hop clock for each new one,
    /// and ends the leg when the engine is back in an ordinary module (or the leg overran).
    /// </summary>
    private void ObserveStuntLeg(in PlayerState local)
    {
        long now = SessionClock.NowMs;
        string module = local.Module;

        if (StuntModuleRule.IsStuntModule(module))
        {
            _stuntModulesRecentlyThrough[module] = now;

            if (string.Equals(module, _stuntLegGaveUpOn, StringComparison.OrdinalIgnoreCase))
            {
                // The hop overran its window and the follows were released; do not re-arm the
                // hold for the same module the next tick (that would make the window a no-op).
                return;
            }

            bool newHop = !_stuntLegActive
                || !_stuntLegSawStuntLocally
                || !_stuntModulesRecentlyThrough.ContainsKey(module)
                || !string.Equals(module, _stuntLegCurrentModule, StringComparison.OrdinalIgnoreCase);

            if (!_stuntLegActive)
            {
                // Entered a stunt module with no replay and no own announce ahead of it (the
                // announce channel off, or a leg that began before this agent attached): still
                // our own leg — nothing good ever comes from being warped OUT of a cutscene
                // module mid-scene, and its scripts exit by themselves.
                _stuntLegActive = true;
                log($"stunt leg: in '{module}' - holding every follow until its scripts exit "
                    + $"(hop window {StuntLegFollowRule.HopWindowMs / 1000} s)");
            }
            else if (newHop)
            {
                log($"stunt leg: now in '{module}' (next hop, clock restarted)");
            }

            if (newHop)
            {
                _stuntLegHopStartedAtMs = now;
                _stuntLegCurrentModule = module;
            }

            _stuntLegSawStuntLocally = true;

            if (now - _stuntLegHopStartedAtMs > StuntLegFollowRule.HopWindowMs)
            {
                log($"stunt leg: '{module}' has run {StuntLegFollowRule.HopWindowMs / 1000} s without exiting "
                    + "- the ordinary follow rules take over (NEEDS-LIVE: which scene did this)");
                _stuntLegActive = false;
                _stuntLegSawStuntLocally = false;
                _stuntLegGaveUpOn = module;
            }

            return;
        }

        if (module.Length == 0 || ModuleNameGuard.IsFallbackModuleName(module))
        {
            return;
        }

        // Back in an ordinary module: a hop we gave up on is behind us.
        _stuntLegGaveUpOn = string.Empty;

        if (!_stuntLegActive)
        {
            return;
        }

        if (_stuntLegSawStuntLocally)
        {
            log($"stunt leg: over - landed in '{module}'; the ordinary follow rules apply again");
            _stuntLegActive = false;
            _stuntLegSawStuntLocally = false;
            return;
        }

        if (now - _stuntLegHopStartedAtMs > StuntLegFollowRule.ExpiryMs)
        {
            log($"stunt leg: the replay never reached a stunt module within {StuntLegFollowRule.ExpiryMs / 1000} s "
                + $"(still in '{module}') - giving the leg up");
            _stuntLegActive = false;
        }
    }

    /// <summary>Marks the start of a leg this engine is about to run (a replay just requested,
    /// or our own engine's announce into a stunt module) so echoes and later hops are dropped
    /// from the first tick, before the local module has any chance to read as stunt.</summary>
    private void BeginStuntLeg(string module)
    {
        long now = SessionClock.NowMs;
        _stuntModulesRecentlyThrough[module] = now;

        if (_stuntLegActive)
        {
            return;
        }

        _stuntLegActive = true;
        _stuntLegSawStuntLocally = false;
        _stuntLegHopStartedAtMs = now;
    }

    /// <summary>
    /// While follows are held inside our own leg, a leader announce naming ANOTHER stunt module
    /// is a later hop of the chain this engine is already running (or will reach through its
    /// own scripts, or will never reach because its globals branch differently) — spent here,
    /// on the tick it is seen, never kept for after the landing: a stale later hop replayed
    /// after this engine has already landed would send it back into the middle of a chain the
    /// leader has finished. A NON-stunt announce (the leader's exit, or a door it took after
    /// landing) is deliberately kept: it is spent on landing if we land in the same module, and
    /// it is the reunion if we do not.
    /// </summary>
    private void DropLaterHopAnnounceWhileHeld(IReadOnlyList<RemotePeer> peers)
    {
        if (_pendingAnnounce is not { } held || !StuntModuleRule.IsStuntModule(held.Module))
        {
            return;
        }

        _stuntModulesRecentlyThrough[held.Module] = SessionClock.NowMs;
        log($"{AnnouncerLabel(held, peers)}'s announce into '{held.Module}' arrived while this engine is inside "
            + "its own stunt leg - a later hop of the same chain, not replayed");
        _pendingAnnounce = null;
    }

    private bool RecentlyThroughStuntModule(string module, long now) =>
        _stuntModulesRecentlyThrough.TryGetValue(module, out long at)
        && now - at <= StuntLegFollowRule.HopWindowMs;

    /// <summary>
    /// The fast path's stunt branch: an eligible leader announced a transition INTO a stunt
    /// module. Returns true when this tick's travel decision is fully handled (replayed,
    /// waiting, or spent), false to hand the tick to the slow path exactly as the pre-existing
    /// refusal did (which then refuses the same destination, unchanged).
    /// </summary>
    private bool TryReplayStuntLeg(
        in PendingHostAnnounce announce, in PlayerState local, IReadOnlyList<RemotePeer> peers,
        bool changedModule, long moduleSettledAtMs)
    {
        long now = SessionClock.NowMs;

        // Same "mid our own transition" reading TryFollowAnnouncedTransition's host branch and
        // the galaxy replay use: the load itself, the fade window before the name blanks, and
        // the settle window after an arrival.
        bool midTransition = ModuleNameGuard.IsFallbackModuleName(local.Module)
            || now - _lastOwnTransitionSignalAtMs < OwnTransitionSignalWindowMs
            || (changedModule && now - moduleSettledAtMs < settings.ModuleSettleMs);

        // "Already in it" also covers an interplanetary leg this engine is still ABOUT to
        // replay (the departure packet arrives at the click, its STUNT_* hop's announce can
        // arrive before a busy follower has run the departure): that leg's stunt module is
        // reached by DepartToPlanet, never by a travel request of its own.
        bool alreadyInThisLeg = _stuntLegActive
            || InGalaxyLeg
            || _pendingGalaxyDeparture is not null
            || _galaxyReplayInFlight
            || RecentlyThroughStuntModule(announce.Module, now);

        // leaderInitiated is true by construction here: the announce channel only carries a
        // transition the announcer's OWN engine committed to, and LeadFollowRule.IsEligibleLeader
        // already judged the announcer before the announce was stored.
        StuntLegFollowPath path = StuntLegFollowRule.Decide(
            settings.FollowStunt, leaderInitiated: true, alreadyInThisLeg,
            bridge.IsInDialogue, local.IsInCombat, midTransition, now - announce.SeenAtMs);

        string leader = AnnouncerLabel(announce, peers);

        switch (path)
        {
            case StuntLegFollowPath.Refuse:
                if (!string.Equals(announce.Module, _lastRefusedStuntModule, StringComparison.OrdinalIgnoreCase))
                {
                    _lastRefusedStuntModule = announce.Module;
                    log($"not following {leader} into '{announce.Module}': follow_stunt is off, "
                        + "so the scene plays on their engine only");
                }

                _pendingAnnounce = null;
                return false;

            case StuntLegFollowPath.Drop:
                log(alreadyInThisLeg
                    ? $"{leader}'s announce into '{announce.Module}' is a leg this engine is already running "
                      + "(our own replay echoed back, or a later hop of the same chain) - not replayed"
                    : $"{leader}'s announce into '{announce.Module}' is {now - announce.SeenAtMs} ms old - not replayed");
                _pendingAnnounce = null;
                return false;

            case StuntLegFollowPath.Wait:
                // Kept, not spent: the follower frees up by itself, and the announce's own age
                // bounds how long that is worth waiting for.
                return true;
        }

        if (string.Equals(announce.Module, _lastTravelRequest, StringComparison.OrdinalIgnoreCase)
            && now < _travelRetryAtMs)
        {
            _pendingAnnounce = null;
            return true;
        }

        // The first hop's own movie (01c for the escape pod) rides the replay exactly as it
        // rides any other follow: TravelController queues it immediately before
        // Odyssey.TravelToModule. The movies of every LATER hop play here natively, from this
        // engine's own scripts, and PumpTransitionMovies drops the leader's copies for the
        // whole leg.
        string[] movies = [.. _pendingFollowMovies];

        if (!bridge.TryRequestTravel(announce.Module, announce.Waypoint, movies))
        {
            log($"could not hand {leader}'s stunt leg into '{announce.Module}' to the plugin (no block?)");
            _pendingAnnounce = null;
            return true;
        }

        // _lastOwnTransitionSignalAtMs is deliberately NOT set here: the plugin announces the
        // warp it is about to run (TravelController.Execute publishes every co-op warp), and
        // PumpTransitionAnnounce's own rising edge sets it from that on the next tick, the same
        // way it does for every other follow. The retry debounce above covers the gap.
        _lastTravelRequest = announce.Module;
        _travelRetryAtMs = now + settings.FollowRetryMs;
        _pendingFollowMovies.Clear();
        _pendingFollowMoviesSawHostAway = false;
        _pendingAnnounce = null;
        BeginStuntLeg(announce.Module);

        log($"replaying {leader}'s scripted leg into '{announce.Module}'"
            + (announce.Waypoint.Length > 0 ? $" at waypoint '{announce.Waypoint}'" : string.Empty)
            + $" on this engine (announced transition serial {announce.Serial}; follow_stunt)"
            + (movies.Length > 0
                ? $" with {movies.Length} transition movie(s): " + string.Join(", ", movies)
                : string.Empty));

        string display = settings.ModuleDisplayName?.Invoke(announce.Module) ?? announce.Module;
        bridge.PostChatLine($"following {leader} into the {display} scene");
        return true;
    }

    private static string AnnouncerLabel(in PendingHostAnnounce announce, IReadOnlyList<RemotePeer> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == announce.PeerId && peers[i].Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return announce.PeerId == CoopHost.HostPeerId ? "the host" : $"peer {announce.PeerId}";
    }
}
