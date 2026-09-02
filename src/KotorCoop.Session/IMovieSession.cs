// Cinematics: movies staged before a transition, the destination announced ahead of a
// transition's load, and movies played mid-module with no transition involved.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IMovieSession
{
    /// <summary>
    /// Announces the movies this instance's imminent module transition is about to play, so
    /// whoever follows can queue the same ones and see the same cinematic.
    ///
    /// Sent BEFORE the transition, because there is no after: the queue is drained by the
    /// load and a playing movie parks the announcer's main thread for seconds. Repeat calls
    /// with the same <paramref name="serial"/> are the sender's own redundancy against a
    /// dropped datagram and are de-duplicated by the receiver.
    /// </summary>
    void PublishTransitionMovies(string module, uint serial, string[] movies) { }

    /// <summary>Transition-movie announcements from other peers since the last call, oldest
    /// first. NOT module-filtered, unlike <see cref="DrainBarks"/>: these name no object ids,
    /// and the announcer is by definition about to leave the module it announced from.</summary>
    IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies() => [];

    /// <summary>
    /// Announces the DESTINATION of this instance's imminent module transition — module and
    /// entry waypoint — the instant the engine has committed to it, before the load starts.
    ///
    /// The earlier, faster sibling of <see cref="PublishTransitionMovies"/>: that channel
    /// carries the film, this one carries where a follower should go, early enough that the
    /// follower's own warp can start immediately rather than waiting for this instance's
    /// published module to actually change (which a movie can delay by its whole length) plus
    /// the settle debounce. Repeat calls with the same <paramref name="serial"/> are the
    /// sender's own redundancy against a dropped datagram, de-duplicated by the receiver.
    /// </summary>
    void PublishTransitionAnnounce(string module, string waypoint, uint serial) { }

    /// <summary>Transition-announce packets from other peers since the last call, oldest
    /// first. Not module-filtered, for the same reason <see cref="DrainTransitionMovies"/>
    /// is not: the announcer is by definition about to leave whatever module it is standing
    /// in, so there is no local module to filter against.</summary>
    IReadOnlyList<TransitionAnnouncePacket> DrainTransitionAnnounce() => [];

    /// <summary>
    /// Announces a mid-module movie this instance's own script just played — no transition
    /// involved, unlike <see cref="PublishTransitionMovies"/>. Sent the instant
    /// <c>MoviePlayHook</c> captures the resref; repeat calls with the same
    /// <paramref name="serial"/> are the sender's own redundancy, de-duplicated by the
    /// receiver.
    /// </summary>
    void PublishMidModuleMovie(string module, uint serial, string movie) { }

    /// <summary>Mid-module movie announcements from other peers since the last call, oldest
    /// first. UNLIKE <see cref="DrainTransitionMovies"/>, this one names a module the announcer
    /// is still IN (there is no transition to make it ambiguous), so the caller filters by
    /// module before acting — see <c>CoopAgent.PumpMidModuleMovies</c>.</summary>
    IReadOnlyList<MidModuleMoviePacket> DrainMidModuleMovies() => [];

    /// <summary>
    /// "My player just confirmed <paramref name="planet"/> on the galaxy map, standing in
    /// <paramref name="module"/>" — the galaxy-map departure relay
    /// (<see cref="PacketType.GalaxyDeparture"/>). Published by <c>TransitionPump</c> on the
    /// plugin's own per-click serial edge; repeat calls with the same <paramref name="serial"/>
    /// are the sender's own redundancy, de-duplicated by the receiver.
    /// </summary>
    void PublishGalaxyDeparture(string module, uint serial, int planet) { }

    /// <summary>Galaxy-map departures announced by other peers since the last call, oldest
    /// first. Like <see cref="DrainMidModuleMovies"/>, the module named is where the announcer
    /// STOOD when it clicked; the caller replays only from the same module.</summary>
    IReadOnlyList<GalaxyDeparturePacket> DrainGalaxyDepartures() => [];
}
