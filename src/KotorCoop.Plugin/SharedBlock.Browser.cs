namespace KotorCoop.Plugin;

// In-game console, chat overlay (+clear), the server browser and its details array. See
// SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // ---- in-game console (version 9) ----------------------------------------------
    //
    // The ~ console writes a full command line ("connect 1.2.3.4", "disconnect",
    // "reconnect", "connect 1.2.3.4 secret") which the agent parses and runs through the
    // session manager. The agent echoes status/log lines back for the console to render.

    /// <summary>Plugin -&gt; agent. Bumped when a console command is entered; written last.</summary>
    public const int OffsetConsoleSerial = 0x9F4;

    /// <summary>Plugin -&gt; agent. The command line, null-terminated ascii.</summary>
    public const int OffsetConsoleCommand = 0x9F8;

    public const int ConsoleCommandLength = 96;

    /// <summary>Agent -&gt; plugin. Bumped when a status/log line is posted for the console.</summary>
    public const int OffsetConsoleReplySerial = 0xA58;

    /// <summary>Agent -&gt; plugin. The latest status line, null-terminated ascii.</summary>
    public const int OffsetConsoleReply = 0xA5C;

    public const int ConsoleReplyLength = 96; // occupies 0xA5C..0xABC


    // ---- chat overlay (version 10) ------------------------------------------------
    //
    // An always-on, semi-transparent chat readout, independent of the ~ console. The agent
    // pushes each chat line here and writes the coop.ini overlay config once; the plugin
    // accumulates the lines and renders them with a fade-out.

    /// <summary>Agent -&gt; plugin. Nonzero shows the overlay.</summary>
    public const int OffsetChatOverlayEnabled = 0xAC0;

    /// <summary>Agent -&gt; plugin. 0..100; applied once an alpha-capable text draw is wired.</summary>
    public const int OffsetChatOverlayOpacity = 0xAC4;

    /// <summary>Agent -&gt; plugin. Screen corner: 0=top-left, 1=top-right, 2=bottom-left, 3=bottom-right.</summary>
    public const int OffsetChatOverlayAnchor = 0xAC8;

    /// <summary>Agent -&gt; plugin. Maximum lines shown at once.</summary>
    public const int OffsetChatOverlayMaxLines = 0xACC;

    /// <summary>Agent -&gt; plugin. Milliseconds a line stays before it fades; 0 = never fade.</summary>
    public const int OffsetChatOverlayFadeMs = 0xAD0;

    /// <summary>Agent -&gt; plugin. Bumped when a new chat line is posted; written last.</summary>
    public const int OffsetChatSerial = 0xAD4;

    /// <summary>Agent -&gt; plugin. The latest chat line, null-terminated ascii.</summary>
    public const int OffsetChatLine = 0xAD8;

    public const int ChatLineLength = 128; // occupies 0xAD8..0xB58


    // ---- chat overlay clear (version 12) -----------------------------------------

    /// <summary>Agent -&gt; plugin. Bumped to wipe the on-screen overlay log.</summary>
    public const int OffsetChatClearSerial = 0xC60;


    // ---- server browser (version 13, region 0xE00..0x1100) ------------------------
    //
    // A real in-game browser: the agent publishes the LAN servers it has discovered, the plugin
    // draws them as a panel (it can render at the main menu, where the engine's own GUI is not
    // safely extensible) and lets the player pick one. The pick travels back as a request serial
    // plus the chosen index, so the agent does the connecting through the same SessionManager
    // path as a typed `connect`.

    /// <summary>Plugin -&gt; agent. Nonzero while the browser panel is on screen.</summary>
    public const int OffsetBrowserOpen = 0xE00;

    /// <summary>Plugin -&gt; agent. The highlighted row (0-based).</summary>
    public const int OffsetBrowserSelection = 0xE04;

    /// <summary>Agent -&gt; plugin. How many server entries are valid.</summary>
    public const int OffsetBrowserCount = 0xE08;

    /// <summary>Plugin -&gt; agent. Bumped when the player confirms a row; the agent connects to
    /// <see cref="OffsetBrowserSelection"/>. Written last, after the selection.</summary>
    public const int OffsetBrowserConnectSerial = 0xE0C;

    /// <summary>Agent -&gt; plugin. Bumped whenever the server list changes, so the panel can
    /// redraw without diffing every entry.</summary>
    public const int OffsetBrowserListSerial = 0xE10;

    /// <summary>
    /// Agent -&gt; plugin. Bumped to ask the GAME to prompt for a password: the plugin opens the
    /// developer console pre-filled with <c>password&#160;</c> and the player types the rest.
    ///
    /// ⚠ This ran the other way when it was written (plugin -&gt; agent, "ask for a prompt"), and
    /// that direction cannot work — the agent is a console window behind the game and has no way
    /// to take a keystroke from a player looking at the main menu. It also never had a single
    /// caller on either side, so the browser's Connect on a locked server dialled with whatever
    /// coop.ini held, was rejected, and the player was never asked for anything. Only the AGENT
    /// knows a password is needed (the host is the one that rejects), and only the PLUGIN can
    /// ask for it, so the request travels agent -&gt; plugin.
    /// </summary>
    public const int OffsetBrowserPasswordSerial = 0xE14;

    /// <summary>Plugin -&gt; agent. Bumped when the player chooses HOST in the browser panel,
    /// so both halves of starting a session live in the same in-game screen.</summary>
    public const int OffsetBrowserHostSerial = 0xE18;

    /// <summary>
    /// Agent -&gt; plugin. How many peers the SESSION has admitted right now — written every
    /// dispatcher tick, independent of whether the local player has resolved. This is the
    /// seamless-arrival gate's signal: <c>RemoteCount</c> (0x48) cannot serve, because it is
    /// only written once the agent's player resolve succeeds — a full-memory-scan operation
    /// rate-limited to 1 Hz — which measured ~1.7 s AFTER the world was ready, exactly the
    /// window in which the player watched the co-op "pop in". Zero when no session exists.
    /// (0xE1C was never read or written before this field; no version bump needed.)
    /// </summary>
    public const int OffsetSessionPeers = 0xE1C;

    /// <summary>Agent -&gt; plugin. Server entries: name[32], address[24], players u8, max u8.</summary>
    public const int OffsetBrowserEntries = 0xE20;

    public const int BrowserEntryStride = 0x40;
    public const int BrowserEntryName = 0x00;
    public const int BrowserEntryNameLength = 32;
    public const int BrowserEntryAddress = 0x20;
    public const int BrowserEntryAddressLength = 24;
    public const int BrowserEntryPlayers = 0x38;
    public const int BrowserEntryMaxPlayers = 0x39;

    /// <summary>Non-zero when the host requires a password; the row shows a lock marker so
    /// the prompt that follows Connect is never a surprise.</summary>
    public const int BrowserEntryLocked = 0x3A;

    /// <summary>Agent -&gt; plugin: non-zero when the agent ALREADY HOLDS a password for the
    /// next join (coop.ini, --password, or a previously answered prompt). The plugin cannot
    /// see the agent's configuration, so without this a "prompt on locked" would ask players
    /// who configured a password; Connect prompts only when
    /// <see cref="BrowserEntryLocked"/> is set AND this is clear. Same value on every row —
    /// the password is per-agent, not per-server. Occupies previously unused padding between
    /// <see cref="BrowserEntryLocked"/> and the stride end (verified: nothing ever read or
    /// wrote 0x3B), so no PluginSignature bump.</summary>
    public const int BrowserEntryPasswordHeld = 0x3B;

    /// <summary>Rows the panel can show; occupies 0xE20..0x1020, inside the region.</summary>
    public const int BrowserCapacity = 8;


    // ---- browser details (version 36, region 0x3400..0x3600) ---------------------
    //
    // What the panel's DETAILS pane draws for the highlighted row: the world the host is
    // standing in, and the party's faces.
    //
    // A parallel array rather than a wider row, because the row above is boxed in — it runs
    // 0xE20..0x1020 and the death region starts exactly there, so the stride cannot grow
    // without moving every field a working reader already knows. Same shape the enemy
    // one-shot table uses beside the presence slots, and indexed the same way: details[i]
    // belongs to entry[i].
    //
    // ⚠ Portraits are RESREFS, not portraits.2da row ids. CSWSObject keeps the resref at
    // +0x20 (the id at +0x30 is a different thing), the panel's image label takes a resref,
    // so nothing between the host's creature and the drawn face has to consult a table —
    // and two installs with differently-modded portraits.2da cannot disagree about which
    // face a number means.

    public const int OffsetBrowserDetails = 0x3400;

    public const int BrowserDetailStride = 0x70;

    /// <summary>The world the host is standing in, ALREADY READABLE ("Taris - Lower City").
    /// The agent resolves the resref through the area file and dialog.tlk before writing, so
    /// the plugin draws what it is given. 40 bytes because the resolved names are sentences,
    /// not resrefs — the longest base-game area name measured is 28 characters.</summary>
    public const int BrowserDetailModule = 0x00;

    public const int BrowserDetailModuleLength = 40;

    /// <summary>Three portrait resrefs: the PC first, then the active companions.</summary>
    public const int BrowserDetailPortraits = 0x28;

    public const int BrowserDetailPortraitLength = 16;

    public const int BrowserDetailPortraitCount = 3;

    /// <summary>
    /// The module's loading-screen texture, which the details pane draws where the save panel
    /// draws a save's screenshot — "where the host is", in the game's own art.
    ///
    /// The engine names these <c>load_&lt;module&gt;</c> (the format string "load_%s" lives at
    /// file offset 0x345580) and ships them in TexturePacks\swpc_tex_gui.erf: load_end_m01aa,
    /// load_tar_m03aa, 97 of them. The AGENT composes the name, so a future install whose
    /// convention differs is one function to change rather than a plugin rebuild. The area's
    /// own LoadScreenID is NOT the source — it reads 0 on every base-game module measured.
    /// </summary>
    public const int BrowserDetailLoadScreen = 0x58;

    public const int BrowserDetailLoadScreenLength = 16;

}
