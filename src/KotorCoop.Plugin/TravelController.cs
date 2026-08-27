using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Executes the session's travel requests: when the agent asks this instance to follow a
/// peer into another module, the move goes through the engine's own move-to-module path —
/// the machinery the shipped `warp` cheat uses — so party collection, the save-out of the
/// old area, the loading screen and entry placement all happen exactly as a normal
/// transition would.
///
/// The dangerous part is not the warp, it is the exit: StoreCurrentModule serialises the
/// outgoing area and skips only party members, so anything the plugin spawned (phantom
/// bodies, replicated ground items) would be baked into the stored module as permanent
/// residents and shift the object-id counter for the rest of the playthrough. PluginHost
/// therefore flushes every spawned body BEFORE calling <see cref="Execute"/>.
/// </summary>
internal sealed unsafe class TravelController(SharedBlockView block)
{
    /// <summary>
    /// Movie names waiting to ride the next travel request, armed by the <c>moviewarp</c>
    /// staging verb (<see cref="CheatController"/>).
    ///
    /// It lives here, on the path that consumes it, because WHERE the queue is written is the
    /// whole design: `CServerExoAppInternal::StartNewModule 0x004BA920` — the function the
    /// engine reaches from the move-to-module pending flag this controller raises — is also
    /// the one that drains `move_to_module_movies`. So a movie staged immediately before
    /// <c>TravelToModule</c> is played by the peer's own transition, through the engine's own
    /// code, with no new blocking call and no new hook (docs/research/cinematics.md §3.2.2).
    /// The debug verb is only the first producer; the real feature — a peer replaying the
    /// movies the owner's transition queued — writes to exactly this arm.
    ///
    /// Static because it outlives no world and belongs to no instance: PluginHost holds one
    /// TravelController, and the arm is the state of a staging run, not of a request.
    /// </summary>
    public static readonly MovieStagingArm StagedMovies = new();

    /// <summary>A request is pending while the agent's serial is ahead of ours.</summary>
    public bool HasPendingRequest => block.TravelSerial != block.TravelApplied;

    /// <summary>
    /// True from the moment THIS instance's own commanded warp (<see cref="Execute"/> below)
    /// successfully calls <c>Odyssey.TravelToModule</c> until <c>PluginHost</c> resets it at the
    /// same two points it resets <c>_transitionPending</c>/<c>_travelAnnounced</c> (world torn
    /// down, or a fresh epoch observed). Exists for one reason: the scripted-transition watch
    /// (docs/research/transition-smoothness.md, 2026-08-27) polls
    /// <c>Odyssey.IsMoveToModulePending()</c> on frames AFTER this method has already returned
    /// (that field only rises once <see cref="Execute"/> calls <c>TravelToModule</c>, which is
    /// the LAST thing this method does before returning) — so a single-frame flag like
    /// <c>PluginHost._travelAnnounced</c> (reset every epoch-valid frame, re-armed only while
    /// <see cref="HasPendingRequest"/> is still true) has already gone false again by the frame
    /// the scripted watch would observe the pending flag. This one is set exactly when
    /// <c>Odyssey.TravelToModule</c> succeeds and stays true across that gap, so the scripted
    /// watch never re-announces (with the wrong kind, on top of a request this class already
    /// announced explicitly, §3.2) a warp this instance issued on the session's own authority.
    /// </summary>
    public bool OwnWarpPending { get; private set; }

    /// <summary>Called by <c>PluginHost</c> at the same two points it resets
    /// <c>_transitionPending</c>/<c>_travelAnnounced</c>.</summary>
    public void ClearOwnWarpPending() => OwnWarpPending = false;

    /// <summary>
    /// Task 2b (docs/research/transition-smoothness.md): the serial <see cref="MaybeStartFade"/>
    /// has already faded for, so a pending request that spans several frames of flush is faded
    /// exactly once rather than restarted every frame.
    /// </summary>
    private uint _fadedForSerial;

    /// <summary>
    /// Gated OFF by default (coop.ini <c>transition_announce_fade</c>) — see
    /// <c>EngineHandles.TryFadeToBlack</c>'s own doc for why: the fade DIRECTION is INFERRED,
    /// not read off the binary, so this ships opt-in pending live verification. Getting it
    /// wrong cannot crash and is self-timed (no input lock to undo either way), which is why
    /// it is safe to leave callable even while off — the flag alone decides whether it fires.
    /// </summary>
    private static readonly bool FadeEnabled = PluginConfig.ReadBool("transition_announce_fade", false);

    /// <summary>How long the best-effort fade runs. Roughly the door's own 0.5 s fade
    /// convention (docs/research/cinematics.md §8.2), long enough to read as deliberate,
    /// short enough that a false start (an aborted travel request) does not linger.</summary>
    private const float FadeLengthSeconds = 0.5f;

    /// <summary>
    /// Called every frame a travel request is pending, BEFORE the flush loop — so the fade
    /// covers the whole wait, not just the warp itself — but only actually fades once per
    /// serial. See the type doc and <see cref="FadeEnabled"/> for what is and is not proven.
    /// </summary>
    public void MaybeStartFade()
    {
        if (!FadeEnabled)
        {
            return;
        }

        uint serial = block.TravelSerial;
        if (serial == _fadedForSerial)
        {
            return;
        }

        _fadedForSerial = serial;
        if (EngineHandles.TryFadeToBlack(FadeLengthSeconds))
        {
            PluginHost.Log("travel: fading to black for the incoming transition (INFERRED direction - NEEDS-LIVE)");
        }
    }

    /// <summary>
    /// The travel REQUEST is deliberately not forgotten on a module change: one that raced a
    /// transition stays pending (its serial is still ahead), and the next stable frame either
    /// executes it or acknowledges it as already satisfied.
    ///
    /// A staged CINEMATIC is the opposite, and drops. It was armed against a world that no
    /// longer exists, and an arm that survives an epoch would attach a movie to some later,
    /// unrelated transition — the same class of mistake as every stale pointer in
    /// transition-window-races.md, just with a six-second stall instead of a crash.
    /// </summary>
    public void Invalidate()
    {
        if (StagedMovies.Forget("the world changed under them", out string note))
        {
            PluginHost.Log($"cinematic: {note}");
        }
    }

    /// <summary>
    /// Applies the pending request, once. Called by PluginHost on the game's thread after
    /// every plugin-spawned object has been flushed.
    /// </summary>
    public void Execute()
    {
        uint serial = block.TravelSerial;
        if (serial == block.TravelApplied)
        {
            return;
        }

        byte* module = block.TravelModule;
        int length = 0;
        while (length < SharedBlock.TravelNameLength - 1 && module[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            block.TravelStatus = -1;
            block.TravelApplied = serial;
            return;
        }

        // Already there: acknowledge without touching the engine. Executing anyway would
        // reload the module everyone is standing in.
        if (Odyssey.ModuleNameEquals(module, length))
        {
            block.TravelStatus = 0;
            block.TravelApplied = serial;
            return;
        }

        byte* waypoint = block.TravelWaypoint;
        int waypointLength = 0;
        while (waypointLength < SharedBlock.TravelNameLength - 1 && waypoint[waypointLength] != 0)
        {
            waypointLength++;
        }

        // The staged cinematic, immediately before the warp and nowhere else. This is the
        // last frame on which the plugin is certainly running: the moment the pending flag
        // below is consumed, StartNewModule drains the queue we just wrote and parks the main
        // thread inside WaitForMovie for the length of the film, during which no callback of
        // ours fires at all.
        QueueStagedMovies();

        string name = Encoding.ASCII.GetString(module, length);
        string waypointName = waypointLength > 0 ? Encoding.ASCII.GetString(waypoint, waypointLength) : string.Empty;

        // "Announced transitions" (docs/research/transition-smoothness.md): this instance's
        // OWN plugin-initiated warp is the OTHER place the destination is known before the
        // load starts — no door sweep needed here, the module/waypoint bytes are already in
        // hand. Covers a host being pulled along by a peer, or the `moviewarp` debug verb;
        // the far more common case (a real door, a human walking) is announced from
        // PluginHost's trans_pending edge instead, because a co-op warp like this one never
        // raises trans_pending at all (docs/research/cinematics.md §8.2).
        block.PublishTransitionAnnounce(name, waypointName);

        bool ok = Odyssey.TravelToModule(module, length, waypoint, waypointLength, out int failure);
        if (ok)
        {
            OwnWarpPending = true;
        }

        block.TravelStatus = ok ? 0 : failure;
        block.TravelApplied = serial;

        PluginHost.Log(ok
            ? $"travel: moving to module '{name}' on the session's authority"
            : $"travel: move to '{name}' refused (failure {failure})");
    }

    /// <summary>
    /// Writes whatever is armed into the engine's transition movie queue, and says what the
    /// engine made of each name. A no-op — silently, and on the overwhelming majority of
    /// warps — when nothing is armed.
    ///
    /// The one thing it refuses to do is pile onto a queue the GAME filled: if the engine has
    /// already staged a cinematic for this transition, ours would change what the player
    /// watches and, worse, make the resulting stall un-attributable. Refusing keeps the arm,
    /// which then expires on its own and says so.
    /// </summary>
    private void QueueStagedMovies()
    {
        // The SESSION's list first, the debug arm second. They are the same mechanism with
        // different producers: the session's names came from whichever peer actually reached
        // the trigger (docs/research/cinematics.md §3.2.2), the arm from `moviewarp`. A
        // session-driven follow is the real feature and must win, so that a staging arm left
        // over from a manual test cannot silently replace the cinematic a peer is owed.
        string[]? names = ReadSessionMovies();
        string source = "the session";

        if (names is null)
        {
            if (!StagedMovies.IsArmed)
            {
                return;
            }

            names = StagedMovies.Take(Environment.TickCount64);
            source = "the staging arm";

            if (names is null)
            {
                PluginHost.Log("cinematic: the staged movie(s) went stale before this warp; nothing was written");
                return;
            }
        }

        int alreadyQueued = Odyssey.QueuedTransitionMovieCount();
        if (alreadyQueued > 0)
        {
            PluginHost.Log(
                $"cinematic: NOT staging {string.Join(", ", names)} from {source} - the game "
                + $"has already queued {alreadyQueued} movie(s) for this transition");
            return;
        }

        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength + 1];
        int accepted = 0;

        foreach (string movie in names)
        {
            int length = movie.Length <= MovieStaging.MaxNameLength ? movie.Length : MovieStaging.MaxNameLength;
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (byte)movie[i];
            }

            if (Odyssey.QueueTransitionMovie(buffer, length))
            {
                accepted++;
            }
            else
            {
                PluginHost.Log($"cinematic: the engine REFUSED '{movie}' - no such movie in this install");
            }
        }

        // Tell the watch what we wrote, because it cannot see it: the queue rises and is
        // drained between two of its callbacks (see CinematicWatch.NoteStaged), so without
        // this the stall that follows would be reported as a load rather than a cinematic.
        CinematicWatch.NoteStaged(accepted);

        PluginHost.Log(
            $"cinematic: staged {accepted} of {names.Length} movie(s) from {source} onto this "
            + "transition: " + string.Join(", ", names));
    }

    /// <summary>
    /// The movie resrefs the session attached to this travel request, or null when it
    /// attached none — which is the ordinary case for every transition that carries no
    /// cinematic.
    ///
    /// Read here rather than remembered, on the frame the travel executes: the agent writes
    /// these BEFORE it bumps the travel serial, exactly as it writes the module and the
    /// waypoint, so by the time this runs they belong to this request and no other.
    /// </summary>
    private string[]? ReadSessionMovies()
    {
        int count = block.TravelMovieCount;
        if (count <= 0)
        {
            return null;
        }

        List<string> names = new(count);

        for (int i = 0; i < count; i++)
        {
            byte* slot = block.TravelMovie(i);
            int length = 0;
            while (length < SharedBlock.CinematicNameLength && slot[length] != 0)
            {
                length++;
            }

            if (length > 0)
            {
                names.Add(Encoding.ASCII.GetString(slot, length));
            }
        }

        return names.Count > 0 ? [.. names] : null;
    }
}
