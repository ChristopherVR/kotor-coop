using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Says, in the log, what the engine did during the seconds nobody was calling us.
///
/// WHY THIS EXISTS. A pre-rendered KOTOR movie does not run alongside the game — it REPLACES
/// it. `CClientExoAppInternal::WaitForMovie` (0x005F6F20) spins the main thread in a loop at
/// 0x005F7063-0x005F70B5 that does nothing but pump Windows messages until the Bink player
/// reports the movie finished. `CServerExoApp::MainLoop` is never reached from
/// `Global::WinMain` in that window, GDI `SwapBuffers` is never called, and so NEITHER of the
/// plugin's frame hooks fires: not the SwapBuffers <see cref="FrameHook"/> (which is
/// additionally gated behind `!IsMoviePlaying`), and not <see cref="MainLoopHook"/>, whose
/// stub runs immediately BEFORE the MainLoop call that then blocks.
///
/// The consequence for a live test is blunt: during a cinematic the plugin is not merely idle,
/// it is unreachable, and a session that came apart across a movie leaves a hole in the log
/// exactly where the evidence should be. This class fills the hole from both sides of it —
/// what was queued going in, and how long the world was gone.
///
/// It publishes three things, and nothing else:
///
/// <list type="number">
/// <item>the <b>move-to-module movie queue</b> (`CClientExoAppInternal+0x2D8`) the moment a
///   script fills it, WITH the movie names. NWScript `StartNewModule(module, waypoint,
///   movie1..movie6)` writes that list and the module load drains it, so this is the one
///   pre-warning the engine gives that a cinematic is about to take the thread away — and it
///   arrives on a frame where we are still running;</item>
/// <item>the <b>IsMoviePlaying edges</b> we can actually see. In-world that is almost never
///   (see above); at the main menu, whose looping movie plays asynchronously, it is the normal
///   state, and saying so once settles the last INFERRED claim in menu-frame-hook.md;</item>
/// <item>the <b>main-thread stall</b> — the wall-clock gap between consecutive callbacks. For a
///   blocking movie that gap IS the cinematic, measured, and it is the only direct evidence of
///   one that this process can produce.</item>
/// </list>
///
/// Deliberately READ-ONLY, and deliberately NOT wired to the shared block. A "a movie is
/// playing" flag for the agent would be worthless: the plugin cannot write one while a movie
/// is playing, because it is not running. What the agent can see — the frame counter freezing
/// while the heartbeat keeps ticking — it can already see today.
///
/// Runs on <see cref="MainLoopHook"/> rather than the SwapBuffers hook, for two reasons: that
/// hook fires at the main menu too (where a movie IS observable), and its stub sits immediately
/// before `CClientExoApp::MainLoop`, so the gap it measures brackets exactly the client tick,
/// the server tick and the present — which is precisely the span a movie swallows.
/// </summary>
internal static unsafe class CinematicWatch
{
    /// <summary>
    /// A gap longer than this is not a slow frame, it is the world going away. A settled frame
    /// is single-digit milliseconds and even a heavy one is tens; the shortest base-game
    /// transition movie is seconds. Half a second is far enough above the noise that a module
    /// load — which also stalls the thread, and which is worth logging for the same reason —
    /// is the only other thing that trips it.
    /// </summary>
    private const long StallThresholdMs = 500;

    /// <summary>Longest movie resref we will echo. Engine resrefs are 16 characters.</summary>
    private const int NameCapacity = 32;

    private static long _lastFrameMs;
    private static bool _wasPlaying;
    private static int _lastQueued;

    /// <summary>Transition movies the engine holds queued as of the last callback. Nonzero means
    /// a module move has committed: the movie plays inside the load, with the main thread
    /// gone until the next world is up.</summary>
    public static int QueuedMovieCount => _lastQueued;

    /// <summary>The block to publish the queue into, or null before one exists. Set on every
    /// callback rather than held, because this class outlives no session and owns nothing.</summary>
    private static SharedBlockView? _block;

    /// <summary>
    /// One line per menu visit, not one per frame: the menu movie is playing on every one of
    /// hundreds of frames, and the fact is worth stating once.
    /// </summary>
    private static bool _reportedMenuMovie;

    /// <summary>
    /// Told, by <see cref="TravelController"/>, that it has just written <paramref name="count"/>
    /// movies into the transition queue on this very frame.
    ///
    /// This is not bookkeeping, it is the difference between a readable log and a misleading
    /// one. The queue watch below polls from <see cref="MainLoopHook"/>, whose stub runs at the
    /// CLIENT MainLoop call site — but a staged warp writes the queue from the SwapBuffers hook
    /// INSIDE that client MainLoop and raises the pending flag with it, and the SERVER MainLoop
    /// that immediately follows drains the queue and blocks on the movie. So the rising edge
    /// happens entirely between two callbacks and this watch never sees it: without this, the
    /// stall line that follows would report "0 transition movie(s) were queued going in" for a
    /// stall that was a cinematic, which is exactly the wrong answer to the only question the
    /// line exists to answer.
    ///
    /// It sets the level rather than logging, deliberately: the staging path has already said
    /// what it wrote, and a second sentence claiming to have OBSERVED it would be a lie about
    /// where the fact came from.
    /// </summary>
    public static void NoteStaged(int count)
    {
        if (count > 0)
        {
            _lastQueued = count;
        }
    }

    /// <summary>Forgets everything but keeps the clock honest. Called on a world epoch change
    /// so a stall measured in the old world is never attributed to the new one.</summary>
    public static void Invalidate()
    {
        _wasPlaying = false;
        _lastQueued = 0;
        _reportedMenuMovie = false;

        // NOT _lastFrameMs: the gap is a property of this process's main thread, not of any
        // world, and zeroing it here would silently swallow the stall of the very transition
        // that caused the invalidation.
    }

    /// <summary>
    /// One pass. MAIN THREAD ONLY; every read below either resolves fresh from the app-manager
    /// global or touches nothing but our own statics, so there is no pointer here that can
    /// survive a module transition (transition-window rule 1).
    /// </summary>
    public static void OnFrame(SharedBlockView? block = null)
    {
        _block = block;

        long now = Environment.TickCount64;
        long previous = _lastFrameMs;
        _lastFrameMs = now;

        // A stall is reported before anything else, because it is the one observation whose
        // meaning depends on nothing: however the engine got there, the main thread was gone.
        if (previous != 0 && now - previous >= StallThresholdMs)
        {
            ReportStall(now - previous);
        }

        ReportQueue();
        ReportPlaying();
    }

    /// <summary>
    /// The queue as it fills. Logged on the RISING edge only — the list stays populated for
    /// the whole load and would otherwise repeat every frame — and the names come with it,
    /// because "a movie is queued" and "the escape pod movie is queued" are different facts to
    /// a reader trying to decide whether both machines saw the same cinematic.
    /// </summary>
    private static void ReportQueue()
    {
        int queued;

        try
        {
            queued = Odyssey.QueuedTransitionMovieCount();
        }
        catch
        {
            return;
        }

        if (queued == _lastQueued)
        {
            return;
        }

        int previous = _lastQueued;
        _lastQueued = queued;

        if (queued <= previous)
        {
            // Draining is the normal end of a transition and says nothing a reader needs.
            return;
        }

        StringBuilder names = new();
        byte* buffer = stackalloc byte[NameCapacity];

        // Stripped to resrefs as they are read, because the engine stores a PATH
        // (`.\movies\01c.bik`) and only the resref can be handed back to
        // AddMoveToModuleMovie — see MovieStaging.TryStripToResRef for what happens if it
        // is not. The log keeps the raw form, which is what the engine really holds.
        string[] resrefs = new string[queued];
        int resrefCount = 0;

        for (int i = 0; i < queued; i++)
        {
            int length = Odyssey.ReadQueuedTransitionMovie(i, buffer, NameCapacity);
            if (length <= 0)
            {
                continue;
            }

            string stored = Encoding.ASCII.GetString(buffer, length);

            if (names.Length > 0)
            {
                names.Append(", ");
            }

            names.Append(stored);

            if (MovieStaging.TryStripToResRef(stored, out string resref))
            {
                resrefs[resrefCount++] = resref;
            }
            else
            {
                PluginHost.Log(
                    $"cinematic: cannot reduce '{stored}' to a movie resref - a peer will not "
                    + "be able to replay it");
            }
        }

        PluginHost.Log(names.Length > 0
            ? $"cinematic: the module transition has queued {queued} movie(s): {names}"
            : $"cinematic: the module transition has queued {queued} movie(s) (names unreadable)");

        // Published so the session can hand these to whoever follows us. This is the ONE
        // frame it can be done on: the module load drains the queue, and if a movie plays
        // the main thread is gone before anything else of ours runs again.
        if (_block is { } block && resrefCount > 0)
        {
            block.PublishCinematic(resrefs[..resrefCount]);
            PluginHost.Log(
                $"cinematic: published {resrefCount} movie resref(s) for the session: "
                + string.Join(", ", resrefs[..resrefCount]));
        }
    }

    /// <summary>
    /// The playing edges. In-world these should essentially never fire — if they do, this
    /// instance found a movie the main thread did NOT block on, which is a genuinely new fact
    /// and worth the line it costs.
    /// </summary>
    private static void ReportPlaying()
    {
        bool playing;

        try
        {
            playing = Odyssey.IsMoviePlaying();
        }
        catch
        {
            return;
        }

        if (playing == _wasPlaying)
        {
            return;
        }

        _wasPlaying = playing;

        if (!Odyssey.HasServer)
        {
            // The main menu. Its looping movie is why the SwapBuffers hook is dormant here;
            // one line settles that, and repeating it every menu frame would drown the log.
            if (playing && !_reportedMenuMovie)
            {
                _reportedMenuMovie = true;
                PluginHost.Log("cinematic: a movie is playing with no world up (the main menu's own loop)");
            }

            return;
        }

        PluginHost.Log(playing
            ? "cinematic: a movie is playing and the frame hook still ran — it did not block the main thread"
            : "cinematic: the movie ended");
    }

    /// <summary>
    /// The stall, with everything a reader needs to tell a cinematic from a load: whether a
    /// movie was queued going in, whether one is still up coming out, and which module we
    /// surfaced in. The module name is read live rather than remembered, because a stall is
    /// exactly the window in which a remembered one becomes a claim about a dead world.
    /// </summary>
    private static void ReportStall(long stalledMs)
    {
        string where;

        try
        {
            where = Odyssey.HasServer ? "in a world" : "at the menu";
        }
        catch
        {
            where = "unknown";
        }

        PluginHost.Log(
            $"cinematic: the main thread was gone for {stalledMs} ms ({where}; "
            + $"{_lastQueued} transition movie(s) were queued going in) — no frame callback ran, "
            + "so nothing this plugin does happened in that window");
    }
}
