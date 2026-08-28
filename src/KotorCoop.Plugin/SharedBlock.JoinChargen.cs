namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- new-character join: the chargen capture handshake (region 0x4780..0x47A0) --------
    //
    // coop.ini join_mode = newcharacter. The joiner has no save and therefore no character to
    // carry into the host's world, so they go through the game's OWN New Game /
    // character-creation screens; the engine's chargen writes the finished character to
    // TEMP:pifo (CSWCCreature::SaveCharGenCreature 0x6123E0 — docs/research/character-login-chain.md
    // §2's exhaustive writer list), which is byte-for-byte the file world adoption already
    // carries. All this region does is let the agent say "watch for that file" and let the
    // plugin say "it appeared, and I have stashed it somewhere the loader will not truncate".
    //
    // ⚠ WHY THE PLUGIN AND NOT THE AGENT. The agent could poll Temp\pifo.ifo from outside the
    // process just as easily — but the file it must survive is the one LoadPCAutoSave
    // TRUNCATES TO ZERO on the adoption load that follows (world-adoption.md §7's measured
    // bug), and the stash that survives it is written and restored on the game's own thread by
    // MenuLoadController.RestoreCarriedCharacter. Capturing on the same thread, into the same
    // file that path already knows about, keeps one owner for the whole carried-character
    // lifetime instead of two racing ones.
    //
    // Rides in the free tail after the item-template-spawn ring (OffsetItemTemplateSpawnCursor
    // 0x4700..0x4704) and before TotalSize 0x4800. No TotalSize growth needed.

    /// <summary>Agent -&gt; plugin. A serial: bump it to ARM the capture. Written last, after
    /// everything the plugin will read, exactly like <see cref="OffsetMenuLoadSerial"/>.
    /// </summary>
    public const int OffsetJoinChargenRequest = 0x4780;

    /// <summary>Plugin -&gt; agent. Echoes the request serial the plugin has acted on, so a
    /// second arm can never be mistaken for the first one still running.</summary>
    public const int OffsetJoinChargenApplied = 0x4784;

    /// <summary>Plugin -&gt; agent. One of the <c>JoinChargenState*</c> constants below.
    /// </summary>
    public const int OffsetJoinChargenState = 0x4788;

    /// <summary>Plugin -&gt; agent. How many bytes the captured character weighs. Nonzero is
    /// the honest evidence that a real file was copied — the state field alone would also read
    /// "captured" for a zero-byte pifo, which is precisely the failure world-adoption.md §7
    /// spent a live round chasing.</summary>
    public const int OffsetJoinChargenBytes = 0x478C;

    // 0x4790..0x47A0 reserved for this region (a captured-name string, if the on-screen line
    // ever wants to say WHO was created).

    /// <summary>Nothing has been asked for.</summary>
    public const uint JoinChargenStateIdle = 0;

    /// <summary>Armed: watching for the character chargen writes.</summary>
    public const uint JoinChargenStateArmed = 1;

    /// <summary>The character was captured into <c>Temp\kcoop_carried.ifo</c>. The agent may
    /// now adopt the host's world.</summary>
    public const uint JoinChargenStateCaptured = 2;

    /// <summary>The capture was armed but could not be completed — the file could not be
    /// copied. Reported rather than retried forever, so the player gets a reason.</summary>
    public const uint JoinChargenStateFailed = 3;
}
