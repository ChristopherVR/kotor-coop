namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- new-character join: the chargen capture handshake (region 0x4780..0x47A0) --------
    //
    // coop.ini join_mode = newcharacter. The joiner has no save and therefore no character to
    // carry into the host's world, so they go through the game's OWN New Game /
    // character-creation screens — and this region is how the agent asks for the character that
    // produces and how the plugin hands it back.
    //
    // ⚠ THE ORIGINAL DESIGN OF THIS REGION WAS BUILT ON A FALSE PREMISE, and the correction is
    // why it now reports a SAVE DIRECTORY rather than a captured file. character-login-chain.md
    // §2 attributed TEMP:pifo to chargen (CSWCCreature::SaveCharGenCreature 0x6123E0), so this
    // region was a "watch for that file / I stashed it" handshake. Measured live 2026-08-29:
    // the file is never written. Disassembled afterwards, and the string xrefs say it outright
    // — SaveCharGenCreature does not reference "pifo" anywhere; its own tail writes
    // TEMP:temp.bic (restype 0x7DF, CResGFF::WriteGFFFile at 0x613434). The three real writers
    // of TEMP:pifo are StorePlayerCharacters, DoPCAutosave and LoadPCAutoSave, none of which is
    // on the chargen path.
    //
    // So the character is obtained the only way KOTOR makes one portable: the new game runs to
    // its first module, the plugin asks the engine to SAVE (Odyssey.RequestQuickSave), and the
    // agent reads the PC out of that save's <module>.sav -> module.ifo -> Mod_PlayerList with
    // PlayerIfo.TryExtractPlayer — the identical extraction join_mode = save has always used.
    // The plugin's job is now the two things only it can do: notice that a world came up, and
    // ask the engine to save on its own thread.
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

    /// <summary>Plugin -&gt; agent. How many bytes the produced save's <c>SAVEGAME.sav</c>
    /// weighs. Nonzero is the honest evidence that a real save was written — the state field
    /// alone would also read "captured" for an empty directory, which is exactly the shape of
    /// the failure world-adoption.md §7 spent a live round chasing (a zero-byte carried
    /// character produces a blank level-1 stranger, not an error).</summary>
    public const int OffsetJoinChargenBytes = 0x478C;

    /// <summary>
    /// Plugin -&gt; agent. The NAME of the save directory the engine just wrote, NUL-padded
    /// ASCII, relative to <c>saves\</c> (e.g. <c>"000000 - QUICKSAVE"</c>).
    ///
    /// ⚠ THE PLUGIN NAMES IT RATHER THAN THE AGENT GUESSING. Both sides can see
    /// <c>saves\</c>, and "the newest directory" would usually be right — but the plugin is the
    /// side that knows exactly when the save was asked for, so it is the side that can tell a
    /// directory this session produced from one that was already there. Two processes each
    /// picking "the newest" is two chances to pick differently at the one moment it matters.
    ///
    /// 32 bytes, at this stream's own reserved offsets (0x47C0..0x47DF); 0x47A0..0x47BF belong
    /// to another concurrent stream. A save directory name is
    /// <c>sprintf("%06d - %s", number, name)</c> (CServerExoAppInternal::SaveGame 0x4B58A0,
    /// format literal 0x7454D4), so 32 is comfortable for every name the engine itself emits.
    /// </summary>
    public const int OffsetJoinChargenSaveDir = 0x47C0;

    /// <summary>Capacity of <see cref="OffsetJoinChargenSaveDir"/>, including the terminator.
    /// </summary>
    public const int JoinChargenSaveDirLength = 32;

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

    /// <summary>Press Play: <c>CSWGuiMainCharGen::BeginPlayGame</c>, which starts the new game
    /// and loads its first module. (It does NOT write <c>TEMP:pifo</c>, as this comment used to
    /// claim — see the correction at the top of this file.)</summary>
    public const uint ChargenDriveAccept = 3;

    /// <summary>Nothing has been asked for.</summary>
    public const uint JoinChargenStateIdle = 0;

    /// <summary>Armed: waiting for the player to finish character creation and for the new
    /// game's first module to come up.</summary>
    public const uint JoinChargenStateArmed = 1;

    /// <summary>A save has been written and named in
    /// <see cref="OffsetJoinChargenSaveDir"/>. The agent may now extract the character from it
    /// and adopt the host's world.</summary>
    public const uint JoinChargenStateCaptured = 2;

    /// <summary>The capture was armed but could not be completed. Reported rather than retried
    /// forever, so the player gets a reason.</summary>
    public const uint JoinChargenStateFailed = 3;

    /// <summary>The world is up and the engine has been asked to save; waiting for the save
    /// directory to appear on disk. Distinct from <see cref="JoinChargenStateArmed"/> so a log
    /// or a stuck join says WHICH half is stuck — the player still in chargen, or a save
    /// request the engine refused (<c>CSWSPlayer::GetIsAllowedToSave</c> is a real gate, and a
    /// module's opening cutscene is exactly when it might be shut).</summary>
    public const uint JoinChargenStateSaving = 4;
}
