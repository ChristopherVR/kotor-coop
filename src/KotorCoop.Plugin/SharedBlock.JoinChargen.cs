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

    // ---- driving the chargen screens from the console (0x4790..0x47A0) --------------------
    //
    // The `newgame` / `chargen quick` / `chargen accept` verbs. These call the engine's OWN
    // button handlers — the same functions a real click reaches — rather than synthesising
    // mouse input, which on this rig is intermittently and undiagnosably dead (the trap that
    // made MenuLoadController exist at all) and which, measured, drops clicks in the middle of
    // a scripted sequence.
    //
    // ⚠ NOT a cheat kind, deliberately. CheatController refuses every request with no player
    // loaded (CheatController.Execute's own first guard) and runs behind PluginHost.OnFrame's
    // world-epoch gate, and this whole feature lives at the main menu where neither holds.
    // Same reason the menu-load request has its own region.

    /// <summary>Agent -&gt; plugin. Bump to run <see cref="OffsetChargenDriveKind"/>. Written
    /// LAST, after the kind, like every other request region here.</summary>
    public const int OffsetChargenDriveRequest = 0x4790;

    /// <summary>Agent -&gt; plugin. One of the <c>ChargenDrive*</c> constants below.</summary>
    public const int OffsetChargenDriveKind = 0x4794;

    /// <summary>Plugin -&gt; agent. Echoes the serial acted on.</summary>
    public const int OffsetChargenDriveApplied = 0x4798;

    /// <summary>Plugin -&gt; agent. 0 = done, nonzero = a refusal code (see
    /// <c>CharGenDriveController</c>, which logs the reason in words as well).</summary>
    public const int OffsetChargenDriveStatus = 0x479C;

    /// <summary>Open the game's own character-creation screens from the main menu — replays
    /// <c>CSWGuiMainMenu::OnNewGamePicked</c>'s body.</summary>
    public const uint ChargenDriveNewGame = 1;

    /// <summary>Pick a class and apply the optimised template — the "Quick Character" route.
    /// The class index rides in the high 16 bits of the kind word.</summary>
    public const uint ChargenDriveQuick = 2;

    /// <summary>Press Play: <c>CSWGuiMainCharGen::BeginPlayGame</c>, which is what writes the
    /// character to <c>TEMP:pifo</c>.</summary>
    public const uint ChargenDriveAccept = 3;

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
