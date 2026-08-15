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

        bool ok = Odyssey.TravelToModule(module, length, waypoint, waypointLength, out int failure);
        block.TravelStatus = ok ? 0 : failure;
        block.TravelApplied = serial;

        string name = Encoding.ASCII.GetString(module, length);
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
