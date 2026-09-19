using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Everything about getting a peer's engine to follow the world's owner across a transition:
/// the cinematics that ride along (mid-module movies and transition movies), the destination
/// announce that lets a follower start warping before the leader's published module even
/// changes, and the follow/travel decision itself. Kept as ONE class across three files
/// (TransitionPump.cs / .Movies.cs / .Travel.cs) rather than split into separate pump classes
/// because the travel decision directly reads and consumes state the movie/announce methods
/// populate (<see cref="_pendingAnnounce"/>, <see cref="_pendingFollowMovies"/>,
/// <see cref="_lastHostTransitionSignalAtMs"/>, <see cref="_lastOwnTransitionSignalAtMs"/>) —
/// splitting the state across two independently-constructed pumps would mean either duplicating
/// it or wiring a back-reference between them for no real separation of concerns.
///
/// See docs/research/cinematics.md and docs/research/transition-smoothness.md.
/// </summary>
internal sealed partial class TransitionPump(IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    // ---- mid-module movie relay ----------------------------------------------------------

    /// <summary>The mid-module movie serial this agent has already announced, so a standing
    /// capture is not re-announced every tick.</summary>
    private uint _announcedMidModuleMovieSerial;

    /// <summary>The last mid-module movie serial taken from each peer, so a repeated
    /// announcement is not re-requested every tick.</summary>
    private readonly Dictionary<uint, uint> _seenMidModuleMovieSerials = [];

    // ---- transition cinematics -------------------------------------------------------

    /// <summary>The cinematic serial this agent has already announced, so the same
    /// transition is published once however long its names sit in the block.</summary>
    private uint _announcedCinematicSerial;

    /// <summary>How many more ticks to repeat the current announcement. Cheap redundancy in
    /// place of an ack: the channel is unreliable by design, and a single dropped datagram
    /// would otherwise cost the follower its cinematic. There are seconds of slack between
    /// the announcement and the follow, so a handful of ticks is ample.</summary>
    private int _announceRepeatsLeft;

    private const int AnnounceRepeats = 5;

    /// <summary>
    /// The movies a peer's transitions have announced since this agent last travelled —
    /// ACCUMULATED, not replaced.
    ///
    /// The accumulation is the whole point on the canonical chain. Endar Spire → Taris is two
    /// transitions through the stunt module STUNT_00, and the follow logic correctly refuses
    /// to enter a `stunt*` module. So the follower declines the first transition (whose movie
    /// is <c>01c</c>), declines nothing at the second, and finally warps to <c>tar_m02af</c>
    /// (whose movie is <c>01f</c>) — and the transition it actually makes must carry BOTH.
    ///
    /// Bounded by three things, none of which is a timeout: it is spent when a travel request
    /// is issued, cleared when this player is already with the host (nothing is pending), and
    /// capped at the engine's own six. A timeout was considered and rejected — the stunt
    /// vision it exists to span runs for a minute or more, so any timeout short enough to
    /// bound a hoard is also short enough to drop <c>01c</c> before <c>01f</c> arrives, which
    /// is precisely the case the accumulation was written for.
    /// </summary>
    private readonly List<string> _pendingFollowMovies = [];

    /// <summary>
    /// Whether we have observed the host NOT in our module at least once since
    /// <see cref="_pendingFollowMovies"/> started filling from empty.
    ///
    /// The host announces a movie the instant its own engine accepts it onto the transition
    /// queue — which is BEFORE the host's main thread blocks to play it, and therefore before
    /// the host's published <c>PlayerState.Module</c> has any chance to change. For the entire
    /// window between "announced" and "the host's module update finally lands", this player
    /// and the host still read as being in the SAME module. Comparing OUR OWN module against a
    /// snapshot of itself cannot tell that window apart from a later, genuine reunion — we
    /// rarely move in between, so the snapshot trivially still matches either way. What
    /// actually distinguishes them is whether the HOST was ever seen elsewhere in between: if
    /// not, "together" just means the host has not left yet; if so, a later "together" means it
    /// came back and the movies are truly stale.
    /// </summary>
    private bool _pendingFollowMoviesSawHostAway;

    /// <summary>The last cinematic serial taken from each peer, so a repeated announcement is
    /// accumulated once rather than five times.</summary>
    private readonly Dictionary<uint, uint> _seenCinematicSerials = [];

    /// <summary>
    /// The most recent moment ANY signal from the host that "the host is transitioning right
    /// now" was seen — a <see cref="TransitionAnnouncePacket"/> or a
    /// <see cref="TransitionMoviePacket"/>, whichever arrives. Feeds
    /// <see cref="ClientDoorRefusalRule"/>: a client crossing the SAME door the host just led
    /// through must not be refused, and this is how the arm decision tells that case apart
    /// from a genuinely premature crossing.
    ///
    /// Initialised far in the past rather than <see cref="long.MinValue"/> deliberately:
    /// <c>SessionClock.NowMs - _lastHostTransitionSignalAtMs</c> is plain unchecked
    /// subtraction, and NowMs sitting near zero against MinValue overflows. A million seconds
    /// (~11.5 days) is "never happened" for any session that has ever run, with no overflow
    /// risk against any realistic clock value.
    /// </summary>
    private long _lastHostTransitionSignalAtMs = -1_000_000_000L;

    /// <summary>
    /// How long <see cref="_lastHostTransitionSignalAtMs"/> keeps disarming the client-door
    /// refusal after the host's last transition signal. Generous rather than tight: the
    /// signal is a fade window or a movie announce, but the host's OWN transition (movie
    /// included) can run well past 25 s (docs/research/cinematics.md's 01c measured
    /// 15,531-25,000 ms), and disarming the refusal a moment too early defeats the whole
    /// feature on exactly the case it exists for.
    /// </summary>
    private const int HostTransitionSignalWindowMs = 45_000;

    // ---- transition announce (the destination, sent before transition-movies' film) --

    /// <summary>One pending leader announce, or null when none is outstanding — replaced
    /// wholesale by a fresher one, never merged (unlike the movie accumulation, there is only
    /// ever one live destination). <see cref="PeerId"/> is who announced it: the host (always
    /// eligible) or, under <see cref="AgentSettings.FollowLeadMode"/> = <see cref="FollowLeadMode.Any"/>,
    /// a client — see <see cref="LeadFollowRule"/>. <see cref="SeenAtMs"/> is when it arrived:
    /// only the stunt-leg replay (<see cref="StuntLegFollowRule.ExpiryMs"/>) reads it — an
    /// ordinary announce has no expiry, exactly as before.</summary>
    private readonly record struct PendingHostAnnounce(string Module, string Waypoint, uint Serial, uint PeerId, long SeenAtMs);

    private PendingHostAnnounce? _pendingAnnounce;

    /// <summary>
    /// The most recent moment THIS instance's own engine committed to (or is still playing) a
    /// transition of its own — set whenever our own outbound <see cref="TransitionAnnouncePacket"/>
    /// or <see cref="TransitionMoviePacket"/> serial changes, the same rising-edge moment the
    /// movie/announce pumps already log "announcing ..." for. Feeds the host-side lead-swap
    /// safety gate (<see cref="LeadFollowRule.HostMaySafelyFollow"/>): a client's announce must
    /// never be acted on while the HOST's own crossing is already under way — including the
    /// ~0.5 s fade window BEFORE the module name blanks, which <see cref="ModuleNameGuard.IsFallbackModuleName"/>
    /// alone cannot see.
    /// </summary>
    private long _lastOwnTransitionSignalAtMs = -1_000_000_000L;

    /// <summary>
    /// How long <see cref="_lastOwnTransitionSignalAtMs"/> keeps the host-side safety gate
    /// closed after OUR OWN transition begins. Deliberately much shorter than
    /// <see cref="HostTransitionSignalWindowMs"/> (45 s): that constant guards a DIFFERENT
    /// concern (disarming a client's door refusal for a transition that can run tens of seconds
    /// once it starts). This one only needs to bridge the short window between "announced" and
    /// "the module name actually blanks" — the door's own fade is 0.5 s (cinematics.md §8.2) —
    /// after which <see cref="ModuleNameGuard.IsFallbackModuleName"/> and the post-arrival
    /// settle window (<see cref="AgentSettings.ModuleSettleMs"/>) take over.
    /// </summary>
    private const int OwnTransitionSignalWindowMs = 3_000;

    /// <summary>The transition-announce serial this agent has already published, so a
    /// standing destination is not re-announced every tick.</summary>
    private uint _announcedTransitionSerial;

    /// <summary>Same redundancy shape as <see cref="_announceRepeatsLeft"/>, for the
    /// transition-announce channel.</summary>
    private int _announceRepeatsLeftForTransition;

    /// <summary>The last transition-announce serial taken from each peer.</summary>
    private readonly Dictionary<uint, uint> _seenTransitionAnnounceSerials = [];

    // ---- travel ------------------------------------------------------------------------

    /// <summary>The module we last requested a travel to, and when the retry window for it
    /// expires — a module load takes several seconds, and re-sending during one would queue a
    /// second transition on arrival.</summary>
    private string _lastTravelRequest = string.Empty;

    private long _travelRetryAtMs;
}
