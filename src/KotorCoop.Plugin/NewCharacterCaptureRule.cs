namespace KotorCoop.Plugin;

/// <summary>
/// When the save holding the character the player just created is safe to hand to the agent.
///
/// THE FILE THIS WATCHES is a save directory's <c>SAVEGAME.sav</c>, written by the engine's own
/// save path after the new game reached its first module
/// (<see cref="NewCharacterCaptureController"/>). ⚠ It used to be <c>Temp\pifo.ifo</c>, on
/// docs/research/character-login-chain.md §2's claim that retail chargen writes one; measured
/// live and then disassembled, it does not — see that controller's header for the correction.
/// The three conditions below survived the change unaltered, because they were never about the
/// pifo specifically: they are about reading a file another thread is still writing, and about
/// a file that was already there before this join started.
///
/// THREE CONDITIONS, and each of them is a bug that was reasoned about rather than met:
/// <list type="number">
/// <item>NEWER THAN THE CUTOFF. An install has old saves on disk (and once had a stale
/// <c>Temp\pifo.ifo</c> that survived across runs). Carrying a character out of one of those
/// would silently join with somebody the player did not create — no symptom until they noticed
/// they were someone else.</item>
/// <item>BIG ENOUGH. A zero-byte file is not "no file" — it is a write that has been opened
/// and not yet filled, and every downstream reader here treats a characterless input as a
/// blank level-1 stranger rather than an error (world-adoption.md §7). A real
/// <c>SAVEGAME.sav</c> measured 584 KB; a real pifo, 11,918 bytes.</item>
/// <item>STABLE. The engine writes the file from its own thread while this polls from the
/// frame loop; a length read mid-write is a truncated character. Two consecutive polls
/// agreeing is cheap and rules it out without any file locking.</item>
/// </list>
///
/// Pure so it can be tested without a game or a file system, like every other
/// <c>*Rule</c> here.
/// </summary>
internal static class NewCharacterCaptureRule
{
    /// <summary>
    /// The floor a captured file must clear. A real SAVEGAME.sav measured 584 KB and a real
    /// chargen character 11,918 bytes, so 256 is deliberately generous: the job here is to
    /// reject ZERO and obvious rubble, not to second-guess the archive and GFF parsers that
    /// run later.
    /// </summary>
    public const long MinimumBytes = 256;

    /// <summary>How often the file is looked at while armed - which is also the gap between
    /// the two agreeing length reads the stability rule needs. Milliseconds, not frames: the
    /// main menu renders unthrottled, so a frame count is not a duration (the same trap
    /// <c>MenuLoadController</c>'s own phase-2 grace hit).</summary>
    public const int PollIntervalMs = 250;

    /// <summary>
    /// Whether this poll may act on the file.
    /// </summary>
    /// <param name="fileExists">Whether the watched file is there at all.</param>
    /// <param name="bytes">Its length this poll.</param>
    /// <param name="previousBytes">Its length on the previous poll, or -1 if this is the first
    /// poll since arming (which can therefore never capture — one extra poll interval is a
    /// quarter of a second, against a character that is wrong forever).</param>
    /// <param name="writtenAtTicksUtc">Its last-write time, as UTC ticks.</param>
    /// <param name="armedAtTicksUtc">The freshness cutoff, as UTC ticks - anything written
    /// before it belongs to a previous session or a previous world, not to this join.</param>
    public static bool ShouldCapture(
        bool fileExists, long bytes, long previousBytes, long writtenAtTicksUtc, long armedAtTicksUtc)
    {
        if (!fileExists || bytes < MinimumBytes)
        {
            return false;
        }

        if (writtenAtTicksUtc < armedAtTicksUtc)
        {
            return false;
        }

        return previousBytes == bytes;
    }
}
