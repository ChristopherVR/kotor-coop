namespace KotorCoop.Session;

/// <summary>The cinematic staging verbs: moviewarp, movieplay.</summary>
public static partial class ConsoleCommand
{
    // The staging instrument. Spelled in full, with no alias and no short form, deliberately:
    // what it arranges is a movie playing across a module transition, and a movie parks the
    // game's main thread for its whole length — several seconds in which this plugin is not
    // slow, it is UNREACHABLE, and every invariant it maintains is unattended. That is a thing
    // to ask for on purpose.
    /// <summary>
    /// Parses the <c>moviewarp</c> sub-grammar — the one verb here that exists to arrange a
    /// multi-second window in which the in-game plugin does not run at all.
    ///
    /// Validated hard, in the parser, for the reason <c>netsim</c> is: a typo in a staging
    /// command must not silently arm nothing. It must also not silently arm SOMETHING ELSE —
    /// the engine's own queue add (`AddMovieToExoArrayList 0x005FBBF0`) builds
    /// <c>MOVIES:&lt;name&gt;</c> and quietly refuses a file it cannot open, so a mistyped
    /// movie produces a transition with no cinematic that looks exactly like the defect this
    /// verb was typed to measure.
    ///
    /// <c>moviewarp &lt;module&gt; &lt;movie&gt; [movie...]</c> stages and warps ·
    /// <c>moviewarp queue &lt;movie&gt; [movie...]</c> stages only, so the next transition of
    /// ANY kind carries it · <c>moviewarp clear</c> disarms · <c>moviewarp show</c> reports.
    /// </summary>
    private static CoopCommand ParseStageMovie(string[] rest)
    {
        const string usage =
            "moviewarp <module> <movie> [movie...] | moviewarp queue <movie> [movie...] | "
            + "moviewarp clear | moviewarp show";

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string first = rest[0].ToLowerInvariant();

        if (first is "clear" or "off")
        {
            return new CoopCommand(CoopCommandKind.StageMovie, Amount: StageMovieModes.Clear);
        }

        if (first is "show" or "status")
        {
            return new CoopCommand(CoopCommandKind.StageMovie, Amount: StageMovieModes.Show);
        }

        bool queueOnly = first == "queue";
        string module = queueOnly ? string.Empty : first;

        if (!queueOnly && !IsResRef(first, maxLength: 16))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a module name (letters, digits, _; max 16)");
        }

        if (rest.Length < 2)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: queueOnly
                    ? "moviewarp queue needs a movie, e.g. moviewarp queue 01c"
                    : "moviewarp needs a movie as well as a module, e.g. moviewarp tar_m02af 01c "
                      + "(plain `warp` moves with no cinematic)");
        }

        // Commas or spaces, because both are natural to type and the wire form is commas.
        List<string> names = [];
        foreach (string word in rest[1..])
        {
            foreach (string piece in word.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(piece);
            }
        }

        if (names.Count == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: "moviewarp needs at least one movie name");
        }

        // ⚠ These three limits mirror MovieStaging on the plugin side, which cannot be
        // referenced from here (it compiles for win-x86 inside the game). MaxMovies is the
        // engine's own ceiling — NWScript StartNewModule takes exactly six movie arguments —
        // and MaxJoined is the shared block's 32-byte cheat text field, one byte reserved for
        // the terminator. MovieStagingTests asserts the two halves still agree.
        const int maxMovies = 6;
        const int maxJoined = 31;

        if (names.Count > maxMovies)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"{names.Count} movies named; the engine's own queue takes at most {maxMovies}");
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (!IsResRef(names[i], maxLength: 16))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{names[i]}' is not a movie name (letters, digits, _; max 16)");
            }

            // Refused rather than de-duplicated, because the ENGINE de-duplicates silently
            // (the queue add is CExoArrayList<CExoString>::AddUnique), which would leave the
            // reply claiming two movies where one was staged.
            for (int j = 0; j < i; j++)
            {
                if (names[i].Equals(names[j], StringComparison.OrdinalIgnoreCase))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{names[i]}' is named twice; the engine's queue add is unique-only");
                }
            }
        }

        string joined = string.Join(',', names);
        if (joined.Length > maxJoined)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"the movie list is {joined.Length} characters; it travels in a "
                    + $"{maxJoined}-character field, so name fewer movies");
        }

        return new CoopCommand(
            CoopCommandKind.StageMovie,
            Address: module,
            Password: joined,
            Amount: queueOnly ? StageMovieModes.QueueOnly : StageMovieModes.WarpWithMovies);
    }

    // TEST HOOK: fires MoviePlayHook on demand, on the HOST, without waiting for a story
    // trigger to reach a mid-module PlayMovie call. See CoopCommandKind.MoviePlay.
    private static CoopCommand ParseMoviePlay(string[] rest)
    {
        if (rest.Length != 1 || !IsResRef(rest[0], maxLength: 16))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "movieplay <resref>  (e.g. movieplay 01c, or movieplay biologo)");
        }

        return new CoopCommand(CoopCommandKind.MoviePlay, Address: rest[0]);
    }
}
