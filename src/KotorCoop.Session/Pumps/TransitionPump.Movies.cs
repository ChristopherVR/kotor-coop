using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Mid-module movies (a script's own PlayMovie call, no transition involved — the Leviathan
// capture, the Star Forge arrival, the Taris bombardment, the Dantooine vision) and transition
// cinematics/announces (both players should see the same film, and the destination itself).
// See docs/research/trigger-relay.md §6, docs/research/cinematics.md §3.2.2/§9.
internal sealed partial class TransitionPump
{
    public void PumpMidModuleMovies(in PlayerState local)
    {
        if (!settings.MovieRelayEnabled)
        {
            return;
        }

        // Announce our own. Cheap block reads either way (nothing new most ticks), gated the
        // same as every other block-read pump here.
        if (bridge.TryReadMidModuleMovie(out uint serial, out string movie)
            && serial != _announcedMidModuleMovieSerial)
        {
            _announcedMidModuleMovieSerial = serial;
            log($"announcing mid-module movie '{movie}'");
            session.PublishMidModuleMovie(local.Module, serial, movie);
        }

        // Take everyone else's. Filtered to the local module HERE, cheaply, before ever
        // writing a request into the plugin block — the plugin's own decision
        // (MovieRelayQueue.Decide) trusts sameModule/hasMovieName are already true.
        foreach (MidModuleMoviePacket packet in session.DrainMidModuleMovies())
        {
            if (_seenMidModuleMovieSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenMidModuleMovieSerials[packet.PeerId] = packet.Serial;

            if (packet.Movie.Length == 0)
            {
                continue;
            }

            if (!string.Equals(packet.Module, local.Module, StringComparison.OrdinalIgnoreCase))
            {
                continue; // not our module - nothing here to act on
            }

            if (bridge.TryRequestMidModulePlay(packet.Movie))
            {
                log($"peer's mid-module movie '{packet.Movie}' handed to the plugin");
            }
        }
    }

    /// <summary>
    /// Announces our own imminent transition's destination (the plugin's
    /// <c>TransitionAnnounceWatch</c> resolves it; this only relays it), and takes the host's
    /// (or, under lead-swap, a client's). Gated on <see cref="AgentSettings.TransitionAnnounceEnabled"/>
    /// so a disabled feature puts nothing new on the wire at all.
    /// </summary>
    public void PumpTransitionAnnounce(in PlayerState local)
    {
        if (!settings.TransitionAnnounceEnabled)
        {
            return;
        }

        if (bridge.TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint))
        {
            if (serial != _announcedTransitionSerial)
            {
                _announcedTransitionSerial = serial;
                _announceRepeatsLeftForTransition = AnnounceRepeats;
                _lastOwnTransitionSignalAtMs = SessionClock.NowMs;
                log($"announcing transition to '{module}'"
                    + (waypoint.Length > 0 ? $" at waypoint '{waypoint}'" : string.Empty));
            }

            if (_announceRepeatsLeftForTransition > 0)
            {
                _announceRepeatsLeftForTransition--;
                session.PublishTransitionAnnounce(module, waypoint, serial);
            }
        }

        bool weAreHost = session.IsEnemyAuthority;

        foreach (TransitionAnnouncePacket packet in session.DrainTransitionAnnounce())
        {
            bool announcerIsHost = packet.PeerId == CoopHost.HostPeerId;
            if (announcerIsHost)
            {
                _lastHostTransitionSignalAtMs = SessionClock.NowMs;
            }

            if (_seenTransitionAnnounceSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenTransitionAnnounceSerials[packet.PeerId] = packet.Serial;

            if (packet.Module.Length == 0)
            {
                continue;
            }

            // Lead-swap: a client always follows the host (unaffected); the HOST follows a
            // client's announce only when FollowLeadMode allows it — LeadFollowRule.IsEligibleLeader
            // is the single place that decision is made, so PumpTravel's later safety gate and
            // this storage gate can never disagree about who is a candidate leader.
            if (!LeadFollowRule.IsEligibleLeader(weAreHost, announcerIsHost, settings.FollowLeadMode))
            {
                continue;
            }

            _pendingAnnounce = new PendingHostAnnounce(packet.Module, packet.Waypoint, packet.Serial, packet.PeerId);
            log($"{(announcerIsHost ? "host" : "client")} announced transition to '{packet.Module}'"
                + (packet.Waypoint.Length > 0 ? $" at waypoint '{packet.Waypoint}'" : string.Empty));
        }
    }

    public void PumpTransitionMovies(in PlayerState local)
    {
        // Announce our own. Deliberately NOT gated on the module being readable: this fires
        // on the frame a transition is being set up, which is exactly when the module label
        // is least trustworthy, and the names do not depend on it.
        if (bridge.TryReadTransitionMovies(out uint serial, out string[] movies))
        {
            if (serial != _announcedCinematicSerial)
            {
                _announcedCinematicSerial = serial;
                _announceRepeatsLeft = AnnounceRepeats;
                _lastOwnTransitionSignalAtMs = SessionClock.NowMs;
                log($"announcing {movies.Length} transition movie(s): {string.Join(", ", movies)}");
            }

            if (_announceRepeatsLeft > 0)
            {
                _announceRepeatsLeft--;
                session.PublishTransitionMovies(local.Module, serial, movies);
            }
        }

        // Take everyone else's.
        foreach (TransitionMoviePacket packet in session.DrainTransitionMovies())
        {
            if (packet.PeerId == CoopHost.HostPeerId)
            {
                // Same "the host is transitioning right now" signal a TransitionAnnounce
                // gives. A movie-carrying transition with no announce (the feature disabled,
                // or a dropped datagram) must still disarm the client-door refusal for the
                // door the host just used.
                _lastHostTransitionSignalAtMs = SessionClock.NowMs;
            }

            if (_seenCinematicSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenCinematicSerials[packet.PeerId] = packet.Serial;
            AccumulateFollowMovies(packet.Movies);
        }
    }

    /// <summary>Adds names to the pending set, in order, without duplicates and without
    /// exceeding the engine's own six-movie queue.</summary>
    private void AccumulateFollowMovies(string[] movies)
    {
        if (_pendingFollowMovies.Count == 0)
        {
            _pendingFollowMoviesSawHostAway = false;
        }

        foreach (string movie in movies)
        {
            if (movie.Length == 0 || _pendingFollowMovies.Contains(movie, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_pendingFollowMovies.Count >= TransitionMoviePacket.MaxMovies)
            {
                log($"dropping transition movie '{movie}': already holding "
                    + $"{TransitionMoviePacket.MaxMovies}, the engine's own queue limit");
                return;
            }

            _pendingFollowMovies.Add(movie);
        }

        log($"transition movies pending for the next follow: {string.Join(", ", _pendingFollowMovies)}");
    }
}
