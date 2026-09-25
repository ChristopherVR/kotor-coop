// Module travel: warp requests, transition movies/announcements, mid-module movie
// relay, and the client door-refusal arm.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface ITravelBridge
{
    /// <summary>False when this bridge cannot warp the local player between modules.</summary>
    bool SupportsTravel => false;

    /// <summary>
    /// Sends the local player to another module through the game's own transition path.
    /// This is what keeps a client in the host's module — travelling together is the
    /// difference between shared co-op and two parallel campaigns.
    /// </summary>
    /// <param name="movies">
    /// Movie resrefs to queue on the engine's transition list immediately before the warp, so
    /// a follower plays the same cinematic the owner did. Null or empty for the ordinary case.
    /// Written BEFORE the request serial, like the module and the waypoint — and the count is
    /// written even when there are none, so a previous request's movies can never ride along
    /// with this one.
    /// </param>
    bool TryRequestTravel(string module, string waypoint = "", IReadOnlyList<string>? movies = null) => false;

    /// <summary>
    /// The movie resrefs this instance's own imminent transition is about to play, with the
    /// serial the plugin stamped them with. A readable serial-zero snapshot is returned with
    /// an empty list so a new managed agent can baseline the persistent shared block.
    ///
    /// A LEVEL plus a serial rather than a queue: the plugin gets exactly one frame to write
    /// this (the module load drains the engine's queue, and a playing movie then takes its
    /// main thread away entirely), so the agent must be able to read it late and still be
    /// right. De-duplicate on the serial.
    /// </summary>
    bool TryReadTransitionMovies(out uint serial, out string[] movies)
    {
        serial = 0;
        movies = [];
        return false;
    }

    /// <summary>
    /// The destination of this instance's own imminent module transition — module and entry
    /// waypoint — with the serial the plugin stamped it with when its engine committed to it,
    /// or an empty module when it has announced none. A readable serial-zero snapshot is
    /// returned as true so a new managed agent can baseline the persistent shared block.
    ///
    /// The faster, earlier sibling of <see cref="TryReadTransitionMovies"/>: this is knowable
    /// before the load starts (a door's fade window, or the frame immediately before a
    /// plugin-initiated warp), while the movie queue only fills once the destination is
    /// already committed. A LEVEL plus a serial, same shape as the movie channel and for the
    /// same reason: the plugin gets one frame to write this before the load — or the movie —
    /// takes over, so the agent must be able to read it late and still be right.
    /// </summary>
    bool TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint)
    {
        serial = 0;
        module = string.Empty;
        waypoint = string.Empty;
        return false;
    }

    /// <summary>
    /// Arms (or disarms) the plugin's refusal of a CLIENT-initiated real door transition —
    /// coop.ini <c>client_door_refusal</c>. While armed, this instance's own engine-native
    /// door crossing (as opposed to a session-commanded warp, which never touches this path)
    /// is refused early, before the load: the walk-through is cancelled and a line is posted
    /// naming the door, its destination, and (2026-08-22) WHY —
    /// <see cref="ClientDoorRefusalReason.HostMustLead"/> under <c>follow_lead=host</c> (today's
    /// exact behaviour) or <see cref="ClientDoorRefusalReason.HostBusy"/> under
    /// <c>follow_lead=any</c> when the host cannot safely follow right now. A LEVEL, computed
    /// and re-sent by the agent every tick from role, settings and whether the host has been
    /// seen transitioning recently — the plugin has no notion of "host" or "client" on its own.
    /// <see cref="ClientDoorRefusalReason.None"/> means not armed. Default false when this
    /// bridge has no plugin to arm.
    /// </summary>
    bool TryArmClientDoorRefusal(ClientDoorRefusalReason reason) => false;

    /// <summary>
    /// The resref this instance's own <c>MoviePlayHook</c> most recently captured from a
    /// mid-module <c>PlayMovie</c> call (no transition involved), with the serial the plugin
    /// stamped it with — already excluded, on the plugin side, against a name the engine's own
    /// transition queue already carries. A readable serial-zero snapshot is returned with an
    /// empty resref so a new managed agent can baseline the persistent shared block.
    ///
    /// The DETECTION half docs/research/trigger-relay.md §6 shipped without a hook for; see
    /// docs/research/cinematics.md's 2026-08-22 section. De-duplicate on the serial, the same
    /// convention every other plugin -&gt; agent announcement in this file uses.
    /// </summary>
    bool TryReadMidModuleMovie(out uint serial, out string resRef)
    {
        serial = 0;
        resRef = string.Empty;
        return false;
    }

    /// <summary>
    /// Asks the plugin to act on a PEER's mid-module movie: play it right now, or defer it
    /// onto this instance's own next transition, per <c>MovieRelayQueue.Decide</c> — the
    /// plugin makes the final call using engine state only it can read (idle / in dialogue /
    /// in combat / the transition queue). The caller is expected to have already filtered to
    /// the local module and a name that survived parsing; the plugin does not re-check either.
    /// Default false when this bridge has no plugin to ask.
    /// </summary>
    bool TryRequestMidModulePlay(string resRef) => false;

    /// <summary>
    /// What the plugin decided about the last <see cref="TryRequestMidModulePlay"/> request:
    /// <paramref name="applied"/> echoes the request's own serial once handled, and
    /// <paramref name="status"/> is the plugin's <c>MidModulePlayStatusCode</c> verdict. False
    /// when this bridge has no plugin to ask.
    /// </summary>
    bool TryReadMidModulePlayStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    // ---- galaxy-map departure relay ----------------------------------------------

    /// <summary>
    /// The interplanetary departure THIS instance's own player most recently confirmed on the
    /// galaxy map: <paramref name="serial"/> bumps once per accept click (0 = never), and
    /// <paramref name="planet"/> is the <c>planetary.2da</c> row it chose. Published by the
    /// plugin at the click itself — the confirm script, take-off movie and
    /// <c>StartNewModule</c> that follow run inside a frozen main thread, so nothing else
    /// about the leg is observable from the frame hook. De-duplicate on the serial. Default
    /// false when this bridge has no plugin.
    /// </summary>
    bool TryReadGalaxyDeparture(out uint serial, out int planet)
    {
        serial = 0;
        planet = -1;
        return false;
    }

    /// <summary>
    /// Asks the plugin to replay a PEER's galaxy-map departure to <paramref name="planet"/> on
    /// this instance — unlock + select + open + accept, the same scripted leg the leader ran,
    /// so both arrive in the same fresh Ebon Hawk natively. The plugin acks through
    /// <see cref="TryReadGalaxyDepartStatus"/>. Default false when there is no plugin.
    /// </summary>
    bool TryRequestGalaxyDepart(int planet) => false;

    /// <summary>
    /// What the plugin did with the last <see cref="TryRequestGalaxyDepart"/> request:
    /// <paramref name="applied"/> echoes the request's own serial once handled, and
    /// <paramref name="status"/> is the plugin's <c>GalaxyDepartStatusCode</c> (1 departed,
    /// 2 engine refused, 3 bad planet). False when there is no plugin.
    /// </summary>
    bool TryReadGalaxyDepartStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    // ---- party-select relay -------------------------------------------------------

    /// <summary>
    /// The party-select request THIS instance's own player most recently captured (a client's
    /// own suppressed <c>CSWGuiPartySelection</c> panel, captured before being swept):
    /// <paramref name="serial"/> bumps once per capture (0 = never), <paramref name="exitScript"/>
    /// and the forced NPC slots are the panel's own <c>ShowPartySelectionGUI</c> parameters.
    /// De-duplicate on the serial. Default false when this bridge has no plugin.
    /// </summary>
    bool TryReadPartySelectClientCapture(
        out uint serial, out string exitScript, out int forceNpc1, out int forceNpc2)
    {
        serial = 0;
        exitScript = string.Empty;
        forceNpc1 = -1;
        forceNpc2 = -1;
        return false;
    }

    /// <summary>
    /// HOST only: asks the plugin to open this instance's own party-selection screen with the
    /// given exit script and forced NPCs — the native call a peer's request asked for. The
    /// plugin acks through <see cref="TryReadPartySelectHostStatus"/>. Default false when there
    /// is no plugin.
    /// </summary>
    bool TryRequestPartySelectHostOpen(string exitScript, int forceNpc1, int forceNpc2) => false;

    /// <summary>
    /// What the plugin did with the last <see cref="TryRequestPartySelectHostOpen"/> request:
    /// <paramref name="applied"/> echoes the request's own serial once handled, and
    /// <paramref name="status"/> is the plugin's <c>PartySelectHostStatusCode</c> (1 opened,
    /// 2 refused). False when there is no plugin.
    /// </summary>
    bool TryReadPartySelectHostStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;
        return false;
    }

    /// <summary>
    /// HOST only: what happened to the screen this instance most recently opened via
    /// <see cref="TryRequestPartySelectHostOpen"/>, once it closed: <paramref name="serial"/>
    /// bumps once per finished screen, and <paramref name="outcome"/> is the plugin's
    /// <c>PartySelectOutcomeCode</c> (1 confirmed, 2 cancelled). De-duplicate on the serial.
    /// False when there is no plugin.
    /// </summary>
    bool TryReadPartySelectHostOutcome(out uint serial, out uint outcome)
    {
        serial = 0;
        outcome = 0;
        return false;
    }
}
