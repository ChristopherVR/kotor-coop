using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// The galaxy-map departure relay: a player's accept click on the galaxy map runs a scripted
// interplanetary leg (confirm script → take-off movie → StartNewModule into a STUNT_* cutscene →
// landing movie → a FRESH Ebon Hawk) with the main thread frozen for its whole 17–45 s, so
// nothing about it is ever announced through the door-transition machinery — the follower used
// to be left standing in a stale ship, silently (docs/research/galaxy-map-and-party-select.md,
// 2026-09-03). The plugin publishes the click itself (GalaxyMapConfirmHook → block region
// 0x4C00), this relays it, and the follower REPLAYS the same click on its own engine
// (Odyssey.DepartToPlanet) so both arrive in the same fresh ship natively.
internal sealed partial class TransitionPump
{
    /// <summary>One eligible peer departure awaiting replay here, or null. Replaced wholesale
    /// by a fresher one (there is only ever one live destination), never merged.</summary>
    private readonly record struct PendingGalaxyDeparture(string Module, int Planet, uint Serial, uint PeerId, long SeenAtMs);

    private PendingGalaxyDeparture? _pendingGalaxyDeparture;

    /// <summary>The departure serial this agent has already announced. Baselined on the FIRST
    /// read rather than starting at zero: the block outlives sessions in the same process, so a
    /// leftover serial from an earlier session must not be re-announced the moment the agent
    /// attaches — every peer in the same module would replay a departure nobody clicked.</summary>
    private uint _announcedGalaxyDepartureSerial;

    private bool _galaxyDepartureBaselined;

    /// <summary>Same redundancy shape as <see cref="_announceRepeatsLeft"/>.</summary>
    private int _galaxyDepartureRepeatsLeft;

    /// <summary>The module this agent stood in when its own click was first seen — captured at
    /// the edge, because the repeats outlive the moment and the block's module name is the one
    /// thing about the click that can change under them.</summary>
    private string _ownGalaxyDepartureModule = string.Empty;

    private int _ownGalaxyDeparturePlanet = -1;

    /// <summary>The last departure serial taken from each peer.</summary>
    private readonly Dictionary<uint, uint> _seenGalaxyDepartureSerials = [];

    // ---- a replay in flight ----------------------------------------------------------

    private bool _galaxyReplayInFlight;

    private long _galaxyReplayRequestedAtMs;

    /// <summary>The plugin's "applied" echo BEFORE our request went in; the ack is that value
    /// changing.</summary>
    private uint _galaxyReplayAppliedBaseline;

    private int _galaxyReplayAttempts;

    private long _galaxyReplayRetryAtMs;

    /// <summary>
    /// Until when this instance is inside an interplanetary leg of its own — its own click, or
    /// a replay it accepted. While set, the leader's transition-movie announces for the SAME
    /// leg are not accumulated for a later follow-warp: this engine plays them itself, natively,
    /// as part of the leg, and hoarding them would only surface later as a misleading
    /// "dropping N pending transition movie(s)" on the reunion (or, worse, ride along on the
    /// next unrelated door). Generous: the leader's own leg runs up to ~45 s, and a follower's
    /// starts a few seconds later.
    /// </summary>
    private long _galaxyLegUntilMs = -1_000_000_000L;

    private const int GalaxyLegWindowMs = 90_000;

    private bool InGalaxyLeg => SessionClock.NowMs < _galaxyLegUntilMs;

    /// <summary>
    /// Announces this player's own galaxy-map departure (the plugin's confirm hook published
    /// it at the click), takes every other peer's, and replays an eligible one here. Runs every
    /// tick ahead of <see cref="PumpTravel"/>: a departure taken this tick must count as "the
    /// host is transitioning right now" for this tick's door-refusal and follow decisions.
    /// </summary>
    public void PumpGalaxyDeparture(in PlayerState local, IReadOnlyList<RemotePeer> peers, bool changedModule, long moduleSettledAtMs)
    {
        PublishOwnGalaxyDeparture(local);
        TakePeerGalaxyDepartures();

        if (_galaxyReplayInFlight)
        {
            PollGalaxyReplay(peers);
            return;
        }

        TryReplayGalaxyDeparture(local, peers, changedModule, moduleSettledAtMs);
    }

    private void PublishOwnGalaxyDeparture(in PlayerState local)
    {
        // ⚠ The bridge reports "nothing" for a serial of zero, and zero is what a fresh block
        // holds — so the baseline is taken from the FIRST read whatever it says. Baselining
        // only on a successful read made the first real click the baseline, and the first
        // live departure went unannounced (2026-09-03 run 1).
        bool readable = bridge.TryReadGalaxyDeparture(out uint serial, out int planet);
        if (!_galaxyDepartureBaselined)
        {
            _galaxyDepartureBaselined = true;
            _announcedGalaxyDepartureSerial = readable ? serial : 0;
            return;
        }

        if (!readable)
        {
            return;
        }

        if (serial != _announcedGalaxyDepartureSerial)
        {
            _announcedGalaxyDepartureSerial = serial;
            _galaxyDepartureRepeatsLeft = AnnounceRepeats;
            _ownGalaxyDepartureModule = local.Module;
            _ownGalaxyDeparturePlanet = planet;
            _lastOwnTransitionSignalAtMs = SessionClock.NowMs;
            _galaxyLegUntilMs = SessionClock.NowMs + GalaxyLegWindowMs;

            // A departure of our own supersedes anything a peer asked us to replay.
            _pendingGalaxyDeparture = null;

            string destination = GalaxyPlanetNames.Display(planet);
            log($"announcing galaxy-map departure #{serial} to {destination} from '{local.Module}'");

            // Renders when this engine's main thread is next free — i.e. on arrival, as the
            // first line in the fresh ship. That is the useful moment anyway: it confirms the
            // click was relayed, and names who is on their way.
            bridge.PostChatLine($"set course for {destination} — peers are following");
        }

        if (_galaxyDepartureRepeatsLeft > 0)
        {
            _galaxyDepartureRepeatsLeft--;
            session.PublishGalaxyDeparture(_ownGalaxyDepartureModule, serial, _ownGalaxyDeparturePlanet);
        }
    }

    private void TakePeerGalaxyDepartures()
    {
        bool weAreHost = session.IsEnemyAuthority;

        foreach (GalaxyDeparturePacket packet in session.DrainGalaxyDepartures())
        {
            bool announcerIsHost = packet.PeerId == CoopHost.HostPeerId;
            if (announcerIsHost)
            {
                // The host is (about to be) inside a leg with its main thread frozen: the same
                // "transitioning right now" signal a door announce gives, for the same
                // door-refusal disarm.
                _lastHostTransitionSignalAtMs = SessionClock.NowMs;
            }

            if (_seenGalaxyDepartureSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenGalaxyDepartureSerials[packet.PeerId] = packet.Serial;

            // Same eligibility gate as a transition announce: a client follows the host; the
            // host follows a client only under follow_lead=any (the default).
            if (!LeadFollowRule.IsEligibleLeader(weAreHost, announcerIsHost, settings.FollowLeadMode))
            {
                log($"ignoring {(announcerIsHost ? "the host" : $"peer {packet.PeerId}")}'s galaxy-map departure "
                    + $"to {GalaxyPlanetNames.Display(packet.Planet)} (follow_lead={settings.FollowLeadMode})");
                continue;
            }

            if (InGalaxyLeg)
            {
                // Our own leg (or one we already accepted) is under way; the peer's click is
                // the same moment seen from the other seat.
                log($"peer {packet.PeerId}'s galaxy-map departure #{packet.Serial} arrived mid-leg - already on our way");
                continue;
            }

            _pendingGalaxyDeparture = new PendingGalaxyDeparture(
                packet.Module, packet.Planet, packet.Serial, packet.PeerId, SessionClock.NowMs);
            _galaxyReplayAttempts = 0;
            _galaxyReplayRetryAtMs = 0;

            log($"{(announcerIsHost ? "host" : "client")} {packet.PeerId} set course for "
                + $"{GalaxyPlanetNames.Display(packet.Planet)} from '{packet.Module}' (departure #{packet.Serial})");
        }
    }

    private void TryReplayGalaxyDeparture(in PlayerState local, IReadOnlyList<RemotePeer> peers, bool changedModule, long moduleSettledAtMs)
    {
        if (_pendingGalaxyDeparture is not { } pending)
        {
            return;
        }

        if (!settings.FollowHostModule || !bridge.SupportsTravel)
        {
            return;
        }

        long now = SessionClock.NowMs;
        if (now < _galaxyReplayRetryAtMs)
        {
            return;
        }

        // Same "mid our own transition" reading TryFollowAnnouncedTransition's host branch
        // uses: the load itself, the fade window before the name blanks, and the settle window
        // after an arrival.
        bool midTransition = ModuleNameGuard.IsFallbackModuleName(local.Module)
            || now - _lastOwnTransitionSignalAtMs < OwnTransitionSignalWindowMs
            || (changedModule && now - moduleSettledAtMs < settings.ModuleSettleMs);

        GalaxyDepartureFollowPath path = GalaxyDepartureFollowRule.Decide(
            local.Module, pending.Module, bridge.IsInDialogue, local.IsInCombat, midTransition, now - pending.SeenAtMs);

        switch (path)
        {
            case GalaxyDepartureFollowPath.Wait:
                return;

            case GalaxyDepartureFollowPath.Drop:
                log($"not replaying {LeaderLabel(pending, peers)}'s galaxy-map departure to "
                    + $"{GalaxyPlanetNames.Display(pending.Planet)}: it was made from '{pending.Module}' "
                    + $"and we are in '{local.Module}' (or it is {now - pending.SeenAtMs} ms old)");
                _pendingGalaxyDeparture = null;
                return;
        }

        string leader = LeaderLabel(pending, peers);
        string destination = GalaxyPlanetNames.Display(pending.Planet);

        if (_galaxyReplayAttempts == 0)
        {
            // Posted BEFORE the request: from the frame the plugin acts on it this engine's
            // main thread is frozen for the whole leg, so a line posted after the ack would
            // only render on arrival.
            bridge.PostChatLine($"{leader} set course for {destination} — following");
        }

        _galaxyReplayAppliedBaseline = bridge.TryReadGalaxyDepartStatus(out uint applied, out _) ? applied : 0;

        if (!bridge.TryRequestGalaxyDepart(pending.Planet))
        {
            log($"could not hand {leader}'s galaxy-map departure to the plugin (no block?)");
            _pendingGalaxyDeparture = null;
            return;
        }

        _galaxyReplayAttempts++;
        _galaxyReplayInFlight = true;
        _galaxyReplayRequestedAtMs = now;
        log($"replaying {leader}'s galaxy-map departure #{pending.Serial} to {destination} "
            + $"(attempt {_galaxyReplayAttempts}/{GalaxyDepartureFollowRule.MaxAttempts})");
    }

    private void PollGalaxyReplay(IReadOnlyList<RemotePeer> peers)
    {
        if (_pendingGalaxyDeparture is not { } pending)
        {
            _galaxyReplayInFlight = false;
            return;
        }

        long now = SessionClock.NowMs;
        bool acked = bridge.TryReadGalaxyDepartStatus(out uint applied, out uint status)
            && applied != _galaxyReplayAppliedBaseline;

        if (!acked)
        {
            if (now - _galaxyReplayRequestedAtMs < GalaxyDepartureFollowRule.AckTimeoutMs)
            {
                return;
            }

            log($"the plugin did not answer the galaxy-map replay request within "
                + $"{GalaxyDepartureFollowRule.AckTimeoutMs} ms - treating it as refused");
            status = 2;
        }

        _galaxyReplayInFlight = false;
        string leader = LeaderLabel(pending, peers);
        string destination = GalaxyPlanetNames.Display(pending.Planet);

        switch (status)
        {
            case 1: // departed
                _pendingGalaxyDeparture = null;
                _galaxyLegUntilMs = now + GalaxyLegWindowMs;
                _lastOwnTransitionSignalAtMs = now;

                // The leader's take-off/landing movies for THIS leg play here natively; anything
                // accumulated for a follow-warp is that same leg seen twice.
                if (_pendingFollowMovies.Count > 0)
                {
                    log($"clearing {_pendingFollowMovies.Count} pending transition movie(s): "
                        + "they belong to the interplanetary leg this engine is now running itself");
                    _pendingFollowMovies.Clear();
                    _pendingFollowMoviesSawHostAway = false;
                }

                log($"following {leader} to {destination}: the engine accepted the galaxy-map departure");
                return;

            case 3: // bad planet — nothing a retry changes
                log($"the plugin refused {leader}'s galaxy-map departure: planet {pending.Planet} is out of range");
                bridge.PostChatLine($"could not follow {leader} to {destination}");
                _pendingGalaxyDeparture = null;
                return;

            default: // engine refused (or silence): retry, a few times
                if (_galaxyReplayAttempts >= GalaxyDepartureFollowRule.MaxAttempts)
                {
                    log($"giving up on {leader}'s galaxy-map departure to {destination} after "
                        + $"{_galaxyReplayAttempts} refused attempts");
                    bridge.PostChatLine($"could not follow {leader} to {destination} — use the galaxy map yourself");
                    _pendingGalaxyDeparture = null;
                    return;
                }

                _galaxyReplayRetryAtMs = now + GalaxyDepartureFollowRule.RetryMs;
                log($"the engine refused the galaxy-map replay to {destination} - retrying in "
                    + $"{GalaxyDepartureFollowRule.RetryMs} ms");
                return;
        }
    }

    private static string LeaderLabel(in PendingGalaxyDeparture pending, IReadOnlyList<RemotePeer> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == pending.PeerId && peers[i].Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return pending.PeerId == CoopHost.HostPeerId ? "the host" : $"peer {pending.PeerId}";
    }
}
