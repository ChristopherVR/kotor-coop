using System.Text;

namespace KotorCoop.Plugin;

// Movie/cinematic verbs: MoviePlay (fires OdysseyMovies.PlayMovieNow through the deferred
// pre-MainLoop callback) and StageMovie (arms/queues/clears the transition movie queue),
// plus the engine-queue helpers both use.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// TEST HOOK (docs/research/cinematics.md §10.9): plays
    /// <see cref="SharedBlock.OffsetCheatText"/> (a bare resref) through
    /// <see cref="OdysseyMovies.PlayMovieNow"/> — the SAME engine entry
    /// (<c>CClientExoApp::PlayMovie 0x005EDB30</c>) a mid-module script's own <c>PlayMovie</c>
    /// reaches, and the one <see cref="MoviePlayHook"/> watches. Exists because
    /// docs/research/cinematics.md §10.7 found no isolated way to fire that hook on the host
    /// without waiting for a story trigger: <c>moviewarp</c> (<see cref="StageMovie"/>) only
    /// ever writes the engine's TRANSITION queue, a different path entirely that
    /// <see cref="MoviePlayHook"/> does not watch (§10.1's call-graph argument).
    ///
    /// Gated by the SAME <see cref="MovieStaging.DecidePlayNow"/> check
    /// <see cref="MidModuleMovieController"/>'s own <c>PlayNow</c> uses before calling the
    /// identical function, so a refusal here reads exactly like a refusal there.
    ///
    /// ⚠⚠ §10.8 found calling <see cref="OdysseyMovies.PlayMovieNow"/> RIGHT HERE — inside
    /// <see cref="CheatController.OnFrame"/>, which runs on the <see cref="FrameHook"/> callback,
    /// itself nested inside the hooked <c>GDI32!SwapBuffers</c> call — hangs the engine
    /// indefinitely (two different real movies, both killed after 60+ seconds / 4+ minutes with
    /// no completion). §10.9 root-caused it: neither retail caller of <c>PlayMovie</c> ever runs
    /// from inside <c>SwapBuffers</c> (one is mid-<c>MainLoop</c> script execution, the other a
    /// GUI callback off the message pump), so this verb now only DECIDES and ARMS here — the
    /// actual engine call is deferred to <see cref="PendingMoviePlay"/>, which runs from
    /// <see cref="MainLoopHook"/>'s callback, immediately BEFORE <c>MainLoop</c> for the next
    /// frame: outside render, outside <c>SwapBuffers</c>, the same territory the retail call
    /// sites already stand in. That call still BLOCKS THE MAIN THREAD for the length of the film
    /// — that stall, and the OTHER instance's <c>movie-relay:</c> log lines once
    /// <c>movie_relay=1</c>, are the actual test — it just does so one frame later and from a
    /// different call site than before.
    /// </summary>
    private void MoviePlay()
    {
        string resref = ReadCheatText();
        if (!MovieStaging.IsMovieName(resref))
        {
            Answer(
                SharedBlock.CheatStatusRefused,
                resref.Length == 0
                    ? "movieplay needs a resref, e.g. movieplay 01c"
                    : $"'{resref}' is not a movie name (letters, digits, _; max {MovieStaging.MaxNameLength})");
            return;
        }

        int queued = Odyssey.QueuedTransitionMovieCount();
        MovieStagingVerdict verdict = MovieStaging.DecidePlayNow(
            Odyssey.HasClient, Odyssey.IsAreaTransitionPending(), Odyssey.IsMoviePlaying(), queued);

        if (verdict != MovieStagingVerdict.Stage)
        {
            string reason = MovieStaging.Explain(verdict, queued);
            PluginHost.Log($"movieplay: refused '{resref}' - {reason}");
            Answer(SharedBlock.CheatStatusRefused, $"'{resref}' refused - {reason}");
            return;
        }

        // Heap, not stackalloc: this buffer has to survive into NEXT frame's MainLoopHook
        // callback, well past this method's own return - a stackalloc'd span does not.
        byte[] buffer = new byte[MovieStaging.MaxNameLength];
        int length = Math.Min(resref.Length, MovieStaging.MaxNameLength);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)resref[i];
        }

        uint serial = block.CheatSerial;
        _deferredMovieSerial = serial;

        PluginHost.Log(
            $"movieplay: '{resref}' staged - deferred to the pre-MainLoop callback "
            + "(docs/research/cinematics.md §10.9), plays next frame");

        PendingMoviePlay.Arm(() =>
        {
            // Re-checked, not reused: one frame separates the decision above from the engine
            // call here, and MovieStaging.DecidePlayNow is cheap - the same "distrust a stale
            // decision" posture §10.7 already documents for this exact check.
            int recheckQueued = Odyssey.QueuedTransitionMovieCount();
            MovieStagingVerdict recheck = MovieStaging.DecidePlayNow(
                Odyssey.HasClient, Odyssey.IsAreaTransitionPending(), Odyssey.IsMoviePlaying(), recheckQueued);

            if (recheck != MovieStagingVerdict.Stage)
            {
                string reason = MovieStaging.Explain(recheck, recheckQueued);
                PluginHost.Log($"movieplay: '{resref}' no longer safe by the time it was due - {reason}");
                Answer(SharedBlock.CheatStatusRefused, $"'{resref}' refused - {reason}");
                _deferredMovieSerial = 0;
                block.CheatApplied = serial;
                return;
            }

            PluginHost.Log($"movieplay: playing '{resref}' now - the main thread stalls for its length");

            // BLOCKS. Everything the plugin does resumes on the far side of this call, exactly
            // as it does for a real mid-module PlayMovie (docs/research/cinematics.md §1.3) -
            // just from MainLoopHook's callback rather than FrameHook's (§10.9).
            fixed (byte* p = buffer)
            {
                bool started = OdysseyMovies.PlayMovieNow(p, length);

                Answer(
                    started ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                    started
                        ? $"played '{resref}' (returned from the block)"
                        : $"'{resref}' did not start (no client, or name too long)");
            }

            _deferredMovieSerial = 0;
            block.CheatApplied = serial;
        });
    }

    /// <summary>
    /// Stages a cinematic onto a module transition. The instrument that makes the highest-value
    /// unknown in docs/research/cinematics.md testable without a human playing the escape-pod
    /// conversation: it puts movie names into the engine's own <c>move_to_module_movies</c>
    /// queue, which <c>CServerExoAppInternal::StartNewModule</c> drains on the next module load.
    ///
    /// It has four modes, and only two of them touch the engine:
    ///
    /// <list type="bullet">
    /// <item><b>warp</b> — ARM the names; <see cref="TravelController"/> writes them into the
    ///   queue on the frame it fires the warp, after every plugin-spawned body has been
    ///   flushed. Nothing is written here, so a warp that never happens leaves no trace on the
    ///   engine.</item>
    /// <item><b>queue</b> — write them NOW and leave them, so the next transition of ANY kind
    ///   carries them. This is the honest reproduction of the risk case: a transition the
    ///   plugin did not initiate and therefore did not flush ahead of.</item>
    /// <item><b>clear</b> — empty the queue and disarm. The way out.</item>
    /// <item><b>show</b> — read the queue back and change nothing.</item>
    /// </list>
    ///
    /// Every refusal names what it saw (<see cref="MovieStaging.Explain"/>). This is the
    /// verb where a silent guard would be worst: "nothing happened" and "the movie played and
    /// the plugin was unreachable for six seconds" leave almost the same evidence.
    /// </summary>
    private void StageMovie()
    {
        int mode = block.CheatAmount;
        int queued = Odyssey.QueuedTransitionMovieCount();

        if (mode == MovieStaging.ModeShow)
        {
            Answer(
                SharedBlock.CheatStatusApplied,
                $"queued: {DescribeQueue(queued)}; armed: {TravelController.StagedMovies.Describe()}");
            return;
        }

        if (mode == MovieStaging.ModeClear)
        {
            bool forgot = TravelController.StagedMovies.Forget("cleared by hand", out string dropped);
            bool cleared = Odyssey.ClearQueuedTransitionMovies();

            if (forgot)
            {
                PluginHost.Log($"cinematic: {dropped}");
            }

            Answer(
                cleared ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                cleared
                    ? $"transition movie queue emptied (held {queued}); armed: nothing"
                    : "no client application here, so there is no queue to empty");
            return;
        }

        if (!MovieStaging.TrySplit(ReadCheatText(), out string[] names, out string error))
        {
            Answer(SharedBlock.CheatStatusRefused, error);
            return;
        }

        MovieStagingVerdict verdict = MovieStaging.Decide(
            hasClient: Odyssey.HasClient,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            alreadyQueued: queued,
            nameCount: names.Length);

        if (verdict != MovieStagingVerdict.Stage)
        {
            Answer(SharedBlock.CheatStatusRefused, MovieStaging.Explain(verdict, queued));
            return;
        }

        if (mode == MovieStaging.ModeQueueOnly)
        {
            int accepted = QueueMoviesNow(names);
            Answer(
                accepted == names.Length ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                accepted == names.Length
                    ? $"the engine queued {accepted}: {string.Join(", ", names)} - the NEXT transition plays them"
                    : $"the engine took only {accepted} of {names.Length} (missing .bik?)");
            return;
        }

        string previous = TravelController.StagedMovies.Arm(names, Environment.TickCount64);

        PluginHost.Log(
            $"cinematic: armed {string.Join(", ", names)} for the next co-op warp "
            + $"(previously armed: {previous})");

        Answer(
            SharedBlock.CheatStatusApplied,
            $"armed {string.Join(", ", names)} for the next warp (was: {previous})");
    }

    /// <summary>
    /// Writes each name into the engine's queue and counts how many the ENGINE took. It
    /// refuses a name it has no <c>.bik</c> for (see <c>Odyssey.QueueTransitionMovie</c>), and
    /// that refusal is the difference between "the cinematic did not play because the design
    /// is wrong" and "because the file is not in this install" — worth one log line each.
    /// </summary>
    private static int QueueMoviesNow(string[] names)
    {
        int accepted = 0;
        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength + 1];

        foreach (string name in names)
        {
            int length = name.Length <= MovieStaging.MaxNameLength ? name.Length : MovieStaging.MaxNameLength;
            for (int i = 0; i < length; i++)
            {
                buffer[i] = (byte)name[i];
            }

            bool ok = Odyssey.QueueTransitionMovie(buffer, length);
            if (ok)
            {
                accepted++;
            }

            PluginHost.Log(ok
                ? $"cinematic: the engine accepted '{name}' onto the transition movie queue"
                : $"cinematic: the engine REFUSED '{name}' - no such movie in this install");
        }

        return accepted;
    }

    /// <summary>The engine's queue in words, for an answer a reader can act on.</summary>
    private static string DescribeQueue(int queued)
    {
        if (queued <= 0)
        {
            return "nothing";
        }

        StringBuilder names = new();
        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength + 1];

        for (int i = 0; i < queued; i++)
        {
            int length = Odyssey.ReadQueuedTransitionMovie(i, buffer, MovieStaging.MaxNameLength);
            if (length <= 0)
            {
                continue;
            }

            if (names.Length > 0)
            {
                names.Append(", ");
            }

            names.Append(Encoding.ASCII.GetString(buffer, length));
        }

        return names.Length > 0 ? names.ToString() : $"{queued} unreadable";
    }
}
