namespace KotorCoop.Protocol;

// Cinematic pre-announcements. Matches PacketCodec.Movies.cs.

/// <summary>
/// any -&gt; all: "the transition I am about to make plays these movies".
///
/// <paramref name="Module"/> is the module the announcer is LEAVING, not the destination —
/// at the moment the queue fills, the destination has been written to the engine's
/// move-to-module fields but the announcer has not moved, and the follower does not need it
/// (it decides where to go from the owner's published module, as it always has). It rides
/// along for the log and for telling two announcements apart.
///
/// <paramref name="Movies"/> are RESREFS, stripped of the path and extension the engine
/// stores. That is a correctness requirement, not tidiness: <c>AddMovieToExoArrayList</c>
/// resolves a name to <c>.\movies\01c.bik</c> and stores THAT, and feeding the stored form
/// back in makes the engine build <c>MOVIES:.\movies\01c.bik</c>, fail to open it and refuse
/// the movie — silently, on every transition. See <c>MovieStaging.TryStripToResRef</c>.
/// </summary>
public sealed record TransitionMoviePacket(uint PeerId, string Module, uint Serial, string[] Movies)
{
    /// <summary>The engine's own ceiling: NWScript <c>StartNewModule</c> takes exactly six
    /// movie arguments, so six is the longest queue the game can build.</summary>
    public const int MaxMovies = 6;

    /// <summary>An Odyssey resref is 16 characters.</summary>
    public const int MaxMovieLength = 16;
}

/// <summary>
/// any -&gt; all: "the transition I am about to make goes to THIS module, entering at THIS
/// waypoint" — sent the instant this instance's own engine has committed to the destination,
/// which is BEFORE the load starts.
///
/// Unlike <see cref="TransitionMoviePacket"/>, whose <c>Module</c> is what the announcer is
/// LEAVING, <paramref name="Module"/> here is the DESTINATION — the whole point of this
/// channel is to let a follower start its own warp toward the right place immediately,
/// instead of waiting for the mover's published <see cref="PlayerState.Module"/> to catch up
/// (which can lag by the length of a movie: the mover's main thread is inside
/// <c>WaitForMovie</c> and publishes nothing for the duration) plus the follow logic's own
/// settle debounce.
///
/// <paramref name="Waypoint"/> is the entry point the engine's own move-to-module fields
/// carry (<c>door-&gt;linked_to</c> for a real door transition), so the follower's warp lands
/// where the mover is actually headed rather than the destination module's default entry.
/// Empty when the source could not resolve one (a plugin-initiated warp with no waypoint) —
/// the engine's own move-to-module path already treats an empty waypoint as "use the
/// default", so this degrades exactly like an ordinary warp.
/// </summary>
public sealed record TransitionAnnouncePacket(uint PeerId, string Module, string Waypoint, uint Serial);

/// <summary>
/// any -&gt; all: "my engine just played this mid-module movie" — a script's own
/// <c>PlayMovie</c> call with no transition involved. See <see cref="PacketType.MidModuleMovie"/>
/// for why this is a separate shape from <see cref="TransitionMoviePacket"/>.
///
/// <paramref name="Module"/> is where the announcer is standing (there is no transition in
/// progress to make it ambiguous, unlike <see cref="TransitionMoviePacket.Module"/>'s "leaving"
/// sense) — a receiver compares it against its own current module before ever asking the
/// plugin to act. <paramref name="Movie"/> is a single resref, stripped of any path/extension
/// exactly like <see cref="TransitionMoviePacket.Movies"/>'s entries.
/// </summary>
public sealed record MidModuleMoviePacket(uint PeerId, string Module, uint Serial, string Movie)
{
    /// <summary>An Odyssey resref is 16 characters.</summary>
    public const int MaxMovieLength = 16;
}

/// <summary>
/// any -&gt; all (relayed by the host): "my player just confirmed <paramref name="Planet"/> on
/// the galaxy map" — see <see cref="PacketType.GalaxyDeparture"/> for why this is its own
/// channel rather than a <see cref="TransitionAnnouncePacket"/>.
///
/// <paramref name="Module"/> is where the announcer was standing when it clicked (an Ebon
/// Hawk module, in vanilla) — a receiver replays the leg only from the SAME module, since the
/// departure script assumes the ship. <paramref name="Planet"/> is the <c>planetary.2da</c>
/// row the party table had selected (<c>CSWPartyTable+0xE0</c>), 0-15.
/// <paramref name="Serial"/> is the announcer's own per-click counter, for de-duplication
/// across the pump's repeated sends.
/// </summary>
public sealed record GalaxyDeparturePacket(uint PeerId, string Module, uint Serial, int Planet)
{
    public const int MaxPlanet = 15;
}
