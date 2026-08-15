namespace KotorCoop.Plugin;

/// <summary>What the staging layer is allowed to do about a request to queue a cinematic.</summary>
internal enum MovieStagingVerdict
{
    /// <summary>Everything checks out: write the names into the engine's transition queue.</summary>
    Stage,

    /// <summary>No client application object — the main menu, or a torn-down world.</summary>
    RefuseNoWorld,

    /// <summary>
    /// The engine has already raised its area-transition-pending flag. Writing into a
    /// transition that is already in flight is the exact shape of every defect in
    /// transition-window-races.md, so the answer is no, with the reason.
    /// </summary>
    RefuseTransitionPending,

    /// <summary>A movie is up right now. In-world this should be unreachable (the main thread
    /// is inside WaitForMovie and no callback runs), so seeing it is itself a finding.</summary>
    RefuseMoviePlaying,

    /// <summary>The engine's own queue is not empty: the GAME has staged a cinematic and we
    /// would be piling ours on top of it, changing what the player is about to watch.</summary>
    RefuseQueueBusy,

    /// <summary>No movie names survived parsing.</summary>
    RefuseNoNames,

    /// <summary>More names than the engine's own ceiling.</summary>
    RefuseTooMany,
}

/// <summary>
/// The rules for staging a cinematic onto the next module transition, as pure arithmetic over
/// facts the caller has already measured — no engine pointers, no shared block — so it
/// compiles into the 64-bit test project and the guard can be asserted rather than described.
/// Same shape, and the same reason, as <see cref="CheatAuthority"/>.
///
/// WHY A GUARD AT ALL. `CClientExoApp::AddMoveToModuleMovie 0x005EDB50` appends to
/// `CClientExoAppInternal+0x2D8` (`move_to_module_movies`), which
/// `CServerExoAppInternal::StartNewModule 0x004BA920` drains during the next module load by
/// calling `PlayLoadModuleMovies` — and playing a movie parks the MAIN THREAD inside
/// `WaitForMovie 0x005F6F20` for the length of the film, during which neither of this
/// plugin's frame hooks fires and nothing it does happens at all (docs/research/cinematics.md).
/// So this write does not merely add a video: it decides that the process will be unreachable
/// for several seconds across a module transition, which is the single most dangerous window
/// this codebase has. It is a deliberate test instrument and it must be impossible to reach by
/// accident — hence a verb the completion list spells in full, an explicit destination, and
/// every one of the refusals above naming what it saw.
/// </summary>
internal static class MovieStaging
{
    /// <summary>
    /// The engine's own ceiling. NWScript <c>StartNewModule(module, waypoint, movie1..movie6)</c>
    /// (`ExecuteCommandStartNewModule 0x00544390`) calls `AddMoveToModuleMovie` exactly six
    /// times, so six is the largest queue the retail game can ever build and there is no
    /// reason for a staging verb to exceed it.
    /// </summary>
    public const int MaxMovies = 6;

    /// <summary>An engine resref is 16 characters; the base-game movie names are 2 to 5
    /// (<c>01c</c>, <c>05_1c</c>, <c>LIVE_1a</c>).</summary>
    public const int MaxNameLength = 16;

    /// <summary>
    /// How long a comma-joined name list may be, because it travels in the cheat channel's
    /// 32-byte text field (<c>SharedBlock.CheatTextLength</c>) and a silently truncated movie
    /// name would be refused by the engine for a reason nobody could see. Two names fit
    /// comfortably, which covers every two-movie transition the base game has
    /// (<c>06a</c>+<c>05_1C</c>, <c>17a</c>+<c>11a</c>).
    /// </summary>
    public const int MaxJoinedLength = 31;

    // ---- request modes -------------------------------------------------------------
    //
    // Mirrored in ConsoleCommand.StageMovieModes on the session side (the plugin cannot
    // reference it), the same way CheatKinds mirrors SharedBlock's kind constants.

    /// <summary>Arm the names, then warp — the movies are written into the engine's queue on
    /// the very frame the travel path fires, and nowhere else.</summary>
    public const int ModeWarpWithMovies = 0;

    /// <summary>Write the names into the queue NOW and leave them there, so whatever
    /// transition happens next — a door, a script, the escape pod — carries them.</summary>
    public const int ModeQueueOnly = 1;

    /// <summary>Empty the queue again (`RemoveMoveToModuleMovies 0x005EE380`).</summary>
    public const int ModeClear = 2;

    /// <summary>Report what the engine currently has queued, and change nothing.</summary>
    public const int ModeShow = 3;

    /// <summary>
    /// Splits the wire form (comma-separated, no spaces) into movie names and validates each
    /// one against the engine's resref shape.
    ///
    /// The validation is not politeness. `AddMovieToExoArrayList 0x005FBBF0` refuses an empty
    /// name outright and, for a name with no <c>:</c> in it, builds <c>MOVIES:%s</c> and tries
    /// to OPEN the file — returning 0 and adding nothing when it cannot. So a malformed name
    /// silently produces a transition with no cinematic, which is exactly the outcome this
    /// verb exists to distinguish from a successful one.
    /// </summary>
    public static bool TrySplit(string? text, out string[] names, out string error)
    {
        names = [];
        error = string.Empty;

        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "no movie named";
            return false;
        }

        if (trimmed.Length > MaxJoinedLength)
        {
            error = $"the movie list is {trimmed.Length} characters; it travels in a "
                + $"{MaxJoinedLength}-character field, so name fewer movies";
            return false;
        }

        string[] parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "no movie named";
            return false;
        }

        if (parts.Length > MaxMovies)
        {
            error = $"{parts.Length} movies named; the engine's own queue takes at most {MaxMovies}";
            return false;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            if (!IsMovieName(parts[i]))
            {
                error = $"'{parts[i]}' is not a movie name (letters, digits, _; max {MaxNameLength})";
                return false;
            }

            // A duplicate is refused rather than dropped, because the engine drops it
            // SILENTLY: the list is appended through `CExoArrayList<CExoString>::AddUnique
            // 0x005F7C10`, so "01c,01c" would queue one movie and report two.
            for (int j = 0; j < i; j++)
            {
                if (string.Equals(parts[i], parts[j], StringComparison.OrdinalIgnoreCase))
                {
                    error = $"'{parts[i]}' is named twice; the engine's queue add is unique-only";
                    return false;
                }
            }
        }

        names = parts;
        return true;
    }

    /// <summary>True for a resref-shaped movie name: 1..16 chars of ASCII letter/digit/_.</summary>
    public static bool IsMovieName(string value)
    {
        if (value.Length is < 1 or > MaxNameLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decides whether the measured state of the engine permits a staging write. Every
    /// argument is something the caller read this frame — nothing here re-reads, and nothing
    /// here is remembered, because a fact about the engine from a previous frame is exactly
    /// the poison a transition window turns it into.
    /// </summary>
    public static MovieStagingVerdict Decide(
        bool hasClient, bool transitionPending, bool moviePlaying, int alreadyQueued, int nameCount)
    {
        if (!hasClient)
        {
            return MovieStagingVerdict.RefuseNoWorld;
        }

        if (nameCount <= 0)
        {
            return MovieStagingVerdict.RefuseNoNames;
        }

        if (nameCount > MaxMovies)
        {
            return MovieStagingVerdict.RefuseTooMany;
        }

        if (transitionPending)
        {
            return MovieStagingVerdict.RefuseTransitionPending;
        }

        if (moviePlaying)
        {
            return MovieStagingVerdict.RefuseMoviePlaying;
        }

        if (alreadyQueued > 0)
        {
            return MovieStagingVerdict.RefuseQueueBusy;
        }

        return MovieStagingVerdict.Stage;
    }

    /// <summary>The sentence a refusal answers with — what was SEEN, not "refused".</summary>
    public static string Explain(MovieStagingVerdict verdict, int alreadyQueued) => verdict switch
    {
        MovieStagingVerdict.Stage => "staging",
        MovieStagingVerdict.RefuseNoWorld => "no client application here (main menu?)",
        MovieStagingVerdict.RefuseNoNames => "no movie named",
        MovieStagingVerdict.RefuseTooMany => $"more than {MaxMovies} movies named",
        // ⚠ Under the cheat channel's 63-character detail cap, every one of them: a refusal
        // whose reason runs past it arrives as half a reason.
        MovieStagingVerdict.RefuseTransitionPending =>
            "a transition is already pending; nothing was written",
        MovieStagingVerdict.RefuseMoviePlaying =>
            "a movie is playing - and the frame hook still ran, which is news",
        MovieStagingVerdict.RefuseQueueBusy =>
            $"the game has already queued {alreadyQueued} movie(s) here; refusing",
        _ => $"refused ({verdict})",
    };
}

/// <summary>
/// Holds the movie names between "the player asked" and "the travel path fires", so the
/// engine write lands exactly where the real feature will put it — immediately before
/// <c>Odyssey.TravelToModule</c>, on the frame after every plugin-spawned body has been
/// flushed (docs/research/cinematics.md §3.2.2).
///
/// It is a class rather than statics so a test can own one, and it EXPIRES: an arm that is
/// never taken (the warp was refused, the world went away, the agent died between the two
/// halves) must not sit there waiting to attach a cinematic to some unrelated transition
/// minutes later. Expiry says so in words rather than clearing itself quietly — a staging
/// instrument that silently disarms is indistinguishable from one that silently fires.
/// </summary>
internal sealed class MovieStagingArm
{
    /// <summary>
    /// How long an arm survives unspent. The agent files the travel within one 150 ms
    /// dispatcher tick of the plugin's answer and the pre-travel flush is a handful of
    /// frames, so this is two orders of magnitude of slack — chosen to be obviously long
    /// enough that an expiry means something went wrong, not that the machine was busy.
    /// </summary>
    public const long LifetimeMs = 15_000;

    private string[] _names = [];
    private long _armedAtMs;

    public bool IsArmed => _names.Length > 0;

    /// <summary>What is armed, for a log line or a cheat answer.</summary>
    public string Describe() => IsArmed ? string.Join(", ", _names) : "nothing";

    /// <summary>Replaces whatever was armed. The previous arm, if any, is returned so the
    /// caller can say it was overwritten rather than leaving two truths in the log.</summary>
    public string Arm(string[] names, long nowMs)
    {
        string previous = Describe();
        _names = names;
        _armedAtMs = nowMs;
        return previous;
    }

    /// <summary>
    /// Takes the armed names, or null when nothing is armed or the arm has gone stale. A
    /// stale arm is dropped here as well as by <see cref="Expire"/>, so the one caller that
    /// matters — the travel path — can never spend an arm older than its lifetime.
    /// </summary>
    public string[]? Take(long nowMs)
    {
        if (!IsArmed)
        {
            return null;
        }

        if (nowMs - _armedAtMs > LifetimeMs)
        {
            _names = [];
            return null;
        }

        string[] taken = _names;
        _names = [];
        return taken;
    }

    /// <summary>
    /// Drops a stale arm, once, and reports it. Called every frame; returns false on all the
    /// frames where there is nothing to say.
    /// </summary>
    public bool Expire(long nowMs, out string note)
    {
        note = string.Empty;

        if (!IsArmed || nowMs - _armedAtMs <= LifetimeMs)
        {
            return false;
        }

        note = $"the staged movie(s) {Describe()} expired unused after {LifetimeMs / 1000}s "
            + "- no travel took them, and nothing was written to the engine";
        _names = [];
        return true;
    }

    /// <summary>
    /// Forgets the arm, and says so, naming <paramref name="reason"/>. Two callers, two
    /// reasons: a world epoch change (names staged against a world that no longer exists are
    /// the same poison as any other value that crossed a transition) and the operator asking
    /// for it. Both have to be distinguishable in the log, because one is a fault and the
    /// other is somebody tidying up.
    /// </summary>
    public bool Forget(string reason, out string note)
    {
        note = string.Empty;

        if (!IsArmed)
        {
            return false;
        }

        note = $"the staged movie(s) {Describe()} were dropped: {reason}";
        _names = [];
        return true;
    }
}
