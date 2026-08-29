namespace KotorCoop.Plugin;

/// <summary>
/// When the character the player just created in retail chargen is safe to copy.
///
/// THE FILE THIS WATCHES. <c>CSWCCreature::SaveCharGenCreature 0x6123E0</c> — the chargen half
/// of docs/research/character-login-chain.md §2's exhaustive <c>TEMP:pifo</c> writer list —
/// emits <c>&lt;game&gt;\Temp\pifo.ifo</c> when the player accepts their new character, and that
/// file is byte-for-byte what world adoption already carries (<c>PlayerIfo.BuildPifo</c> emits
/// the same shape: an <c>IFO </c> GFF holding a single-entry <c>Mod_PlayerList</c>). So the
/// whole of "let a first-time joiner make a character" reduces to noticing that one file and
/// copying it somewhere the adoption load will not destroy.
///
/// THREE CONDITIONS, and each of them is a bug that was reasoned about rather than met:
/// <list type="number">
/// <item>NEWER THAN THE ARM. <c>Temp\pifo.ifo</c> survives across runs, so an install that has
/// ever loaded an autosave has a stale one on disk. Capturing that would carry a character
/// from a previous session — silently, and with no symptom until the player noticed they were
/// someone else.</item>
/// <item>BIG ENOUGH. A zero-byte pifo is not "no file", it is the exact wreckage
/// <c>LoadPCAutoSave</c>'s source-less <c>FileCopy</c> leaves behind (world-adoption.md §7),
/// and the engine's login treats it as a blank level-1 stranger rather than an error. A real
/// one measured 11,918 bytes.</item>
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
    /// The floor a captured pifo must clear. A real chargen character measured 11,918 bytes and
    /// the smallest thing that could still be a valid one-entry <c>Mod_PlayerList</c> is far
    /// above this — 256 is deliberately generous, because the job here is to reject ZERO and
    /// obvious rubble, not to second-guess the GFF parser that runs later.
    /// </summary>
    public const long MinimumBytes = 256;

    /// <summary>How often the file is looked at while armed. Milliseconds, not frames: the
    /// main menu renders unthrottled, so a frame count is not a duration (the same trap
    /// <c>MenuLoadController</c>'s own phase-2 grace hit).</summary>
    public const int PollIntervalMs = 250;

    /// <summary>
    /// Whether this poll should copy the file.
    /// </summary>
    /// <param name="fileExists">Whether <c>Temp\pifo.ifo</c> is there at all.</param>
    /// <param name="bytes">Its length this poll.</param>
    /// <param name="previousBytes">Its length on the previous poll, or -1 if this is the first
    /// poll since arming (which can therefore never capture — one extra poll interval is a
    /// quarter of a second, against a character that is wrong forever).</param>
    /// <param name="writtenAtTicksUtc">Its last-write time, as UTC ticks.</param>
    /// <param name="armedAtTicksUtc">When the capture was armed, as UTC ticks.</param>
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
