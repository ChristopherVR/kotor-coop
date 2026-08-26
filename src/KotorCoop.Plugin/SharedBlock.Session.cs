namespace KotorCoop.Plugin;

// Engine-resolved player, shared pause + journal, menu save/load, the in-game Connect flow,
// adopted-world login, world-load edge, mid-session load requests and the agent liveness
// heartbeat. See SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // ---- engine-resolved player (version 38, region 0x2260..0x2268) ---------------
    //
    // Placed in the free space after the DEAD bark rings (0x2120..0x2260), never inside
    // them: a stale agent still writes barks at the old offsets, and these fields must
    // not be where those writes land.

    /// <summary>
    /// Plugin -&gt; agent. The engine's own <c>CServerExoApp::GetPlayerCreature()</c>,
    /// republished every frame (0 while no world is up).
    ///
    /// The agent finds the player by scanning for creature vtables, and that scan can
    /// latch a FREED COPY of the player object: the copy keeps its vtable, its stats
    /// pointer and its is_pc flag, so every cheap validation passes — while its position
    /// and health stay frozen at whatever the copy died with. Measured on tar_m03aa: the
    /// agent published the PC standing at its spawn point for minutes while the real
    /// character walked across the map, so the phantom never moved and every encounter
    /// the real player stood in was declined against a body somewhere else. The engine's
    /// answer costs one call and cannot name a freed object.
    /// </summary>
    public const int OffsetEnginePlayerPointer = 0x2260;

    /// <summary>Plugin -&gt; agent. The object id at <c>player + 0x04</c>, written AFTER the
    /// pointer so a torn read pairs an old id with an old pointer, never a new pointer
    /// with garbage.</summary>
    public const int OffsetEnginePlayerId = 0x2264;


    // ---- shared pause + journal (version 7) ---------------------------------------
    //
    // Pause: either player's pause pauses both instances. The agent writes "a remote
    // peer holds a pause"; the plugin applies it through the engine's own
    // SetPauseState(1, ...) so CWorldTimer actually stops, and reports back whether the
    // pause bit it holds is on the peers' behalf — which the agent needs so a mirrored
    // pause is not republished as this player's own (two instances would latch each
    // other paused forever).
    //
    // Journal: quest entries merge upward across the session. The agent reads entries
    // externally; applying one must run in-process because CSWSJournal::SetState is what
    // refreshes the GUI. One request at a time — journal updates are rare.

    /// <summary>Agent -&gt; plugin. Nonzero while any remote peer holds a pause.</summary>
    public const int OffsetRemotePauseDesired = 0x96C;

    /// <summary>Plugin -&gt; agent. Nonzero while the plugin holds the pause bit on the
    /// peers' behalf.</summary>
    public const int OffsetRemotePauseApplied = 0x970;

    /// <summary>Agent -&gt; plugin. Bumped once per journal request; written LAST.</summary>
    public const int OffsetJournalSerial = 0x974;

    /// <summary>Agent -&gt; plugin. Quest tag, null-terminated ascii.</summary>
    public const int OffsetJournalTag = 0x978;

    /// <summary>Agent -&gt; plugin. Desired quest state (plot stage).</summary>
    public const int OffsetJournalState = 0x998;

    /// <summary>Plugin -&gt; agent. Serial of the last journal request acted on.</summary>
    public const int OffsetJournalApplied = 0x99C;

    /// <summary>Plugin -&gt; agent. 0 = ok, otherwise the failure code.</summary>
    public const int OffsetJournalStatus = 0x9A0;

    public const int JournalTagLength = 32;


    // ---- menu save load (version 7, tail padding) ---------------------------------
    //
    // Loads a save from the MAIN MENU with no input injection: the agent copies the
    // wanted save over the autosave slot on disk ("000000 - AUTOSAVE" — a save is just a
    // directory), then bumps the serial. The plugin, on a frame where no server exists
    // yet (i.e. the main menu), replays the GUI's own load path: construct
    // CSWGuiSaveLoad and call LoadPCAutoSave, which builds its own save-info from the
    // AUTOSAVE literals and drives CServerExoApp::ExecuteLoadModule. This replaces menu
    // clicking, which proved intermittently and unexplainably dead (trap 25).

    /// <summary>Agent -&gt; plugin. Bumped once per load request; written last.</summary>
    public const int OffsetMenuLoadSerial = 0x9A4;

    /// <summary>Plugin -&gt; agent. Serial of the last request acted on.</summary>
    public const int OffsetMenuLoadApplied = 0x9A8;

    /// <summary>Plugin -&gt; agent. 0 = ok, otherwise the failure code.</summary>
    public const int OffsetMenuLoadStatus = 0x9AC;

    /// <summary>Agent -&gt; plugin. Module the staged save stands in, null-terminated
    /// ascii — the engine's LoadModule takes this name as its argument.</summary>
    public const int OffsetMenuLoadModule = 0x9B0;

    public const int MenuLoadModuleLength = 16;


    // ---- in-game Connect flow (version 8) -----------------------------------------
    //
    // The plugin rebinds the dormant main-menu BTN_WARP to "Co-op". When the player
    // clicks it — or enters a host IP in the reused save-name panel — the plugin bumps
    // the connect serial (and, for the interactive panel, writes the typed host). The
    // agent, attached and waiting, sees the serial change and starts the session from
    // coop.ini (with the typed host overriding coop.ini's if present).

    /// <summary>Plugin -&gt; agent. Bumped when the player asks to connect.</summary>
    public const int OffsetConnectSerial = 0x9C0;

    /// <summary>Plugin -&gt; agent. Host IP typed in-game, null-terminated ascii; empty means
    /// use coop.ini's value.</summary>
    public const int OffsetConnectHost = 0x9C4;

    public const int ConnectHostLength = 40;

    /// <summary>
    /// Agent -&gt; plugin. Low byte: 0 idle, 1 connecting, 2 connected, 3 failed, 4
    /// reconnecting — for the menu to show a status line. Since item 18
    /// (roadmap-seamless.md, 2026-08-21) the NEXT byte up also carries the current peer
    /// count, for the persistent in-game connection indicator
    /// (<c>KotorCoop.Agent.ConnectionStatusAnnouncer</c>). No new field: this is the same
    /// word the connect-flow write always used, whose only prior value was a bare 0-3 and
    /// whose only reader — the plugin's own getter below — had none, so the wider bytes were
    /// free. A reader that only ever compared the whole field against one of the four
    /// original sentinels is unaffected exactly when the peer count is 0 (the state each of
    /// those four is entered in): idle/connecting/failed always have no peer to count, and
    /// "just connected, alone" packs to the same bare 2 it always did.
    /// </summary>
    public const int OffsetConnectStatus = 0x9EC;

    public const uint ConnectStatusIdle = 0;
    public const uint ConnectStatusConnecting = 1;
    public const uint ConnectStatusConnected = 2;
    public const uint ConnectStatusFailed = 3;

    /// <summary>Live session, but the underlying link is currently down and recovering on its
    /// own — see docs/research/disconnect-reconnect.md. Distinct from
    /// <see cref="ConnectStatusFailed"/>, which is a REFUSED join (wrong password, incompatible
    /// save) that never became a session at all.</summary>
    public const uint ConnectStatusReconnecting = 4;

    /// <summary>Agent -&gt; plugin. Nonzero FORCES the main-menu Co-op button on. No longer a
    /// prerequisite: the plugin shows the button by itself (coop.ini <c>menu_button</c>, read
    /// plugin-side, default on), so the button exists with no agent attached at all. This
    /// field survives as a diagnostics override — an agent write of 1 un-hides a button that
    /// coop.ini turned off; a write of 0 (or a zeroed block) simply defers to coop.ini.</summary>
    public const int OffsetConnectEnable = 0x9F0;


    // ---- adopted-world login (version 35, region 0x3320..0x3328) ------------------
    //
    // ⚠ The declared extent used to read 0x3320..0x3330, which the cheat region below now
    // overlaps by eight bytes — harmless today because only 0x3320 and 0x3324 are used, and a
    // silent collision the moment anyone adds the next adopt field. Trimmed to what this
    // region actually owns.
    //
    // Which flavour of the engine's character login the next menu load takes. The chain
    // forks on DoingStartNewModule (serverInternal+0x1007C), and MenuLoadController clears
    // it every frame of the load window so RunModule takes LoadPrimaryPlayer and reads the
    // PC out of the loaded module's own module.ifo.
    //
    // That is right when the world and the character came from the SAME save, which until
    // now they always did. A joiner adopting the host's world is the case that breaks it:
    // the staged world is the host's, so its module.ifo holds the HOST's character, and
    // LoadPrimaryPlayer would bring the joiner up in the host's body. Leaving the flag SET
    // routes the login through LoadCharacterFromIFO(-1) instead, which reads Temp\pifo.ifo —
    // the joiner's own character, written there by PlayerIfo before the load fires.
    //
    // ⚠ Only ever set this when a pifo has actually been staged. Retail's own reason for
    // never taking this path with a manual save is that a missing pifo fails SILENTLY: the
    // 0xF login branch ignores LoadCharacterFromIFO's return value, login is confirmed
    // anyway, and InitiateModuleForPlayer manufactures a blank 1/1-hit-point creature at the
    // module start point. See docs/research/character-login-chain.md.

    /// <summary>Agent -&gt; plugin. Nonzero = the next menu load is adopting somebody else's
    /// world, so leave DoingStartNewModule alone and let the login read Temp\pifo.ifo.
    /// Read once per load and not latched, so an ordinary load after an adopted one behaves
    /// normally without anything having to clear it.</summary>
    public const int OffsetAdoptWorldLogin = 0x3320;

    /// <summary>Plugin -&gt; agent. Which flavour the last load actually took: 0 nothing yet,
    /// 1 the saved character (LoadPrimaryPlayer), 2 the carried character (TEMP:pifo). The
    /// one field that says whose body the player came up in, which is otherwise only visible
    /// by looking at the character.</summary>
    public const int OffsetAdoptWorldApplied = 0x3324;

    public const uint AdoptLoginSavedCharacter = 1;
    public const uint AdoptLoginCarriedCharacter = 2;


    // ---- world-load edge (version 49, region 0x396C..0x3970) ----------------------
    //
    // Roadmap item 14 phase 2 (docs/research/session-save.md's "Phase 2" section): a save
    // and a load look identical from the save-identity DIGEST alone (both are "the watched
    // file changed"), because KOTOR autosaves through every ordinary module transition too
    // (shared-state-audit.md round 53's live false-positive). This field is the piece the
    // digest cannot provide: PluginHost already tracks Odyssey.TryGetWorldEpoch every frame
    // to invalidate world-scoped state, and separately tracks whether an area transition was
    // ANNOUNCED first (Odyssey.IsAreaTransitionPending, the trans_pending byte a door/trigger
    // sets roughly half a second before StoreCurrentModule runs). An ordinary transition is
    // always preceded by that announcement; a Load Game action (main menu or the in-game
    // pause menu) goes straight from CSWGuiSaveLoad's LOAD branch into ExecuteLoadModule with
    // no such precursor — INFERRED from the engine facts both watches already rest on, not
    // measured live (see WorldLoadRule.cs's own doc comment and session-save.md's Phase 2
    // NEEDS-LIVE list). Placed in the free tail the mid-module movie region's own comment
    // already measured (0x396C..0x3A00, 0x94 bytes free; this uses 4). No TotalSize growth.
    //
    // OUT (plugin -> agent) only — nothing reads this back the other way.

    /// <summary>Plugin -&gt; agent. Bumped once per world teardown PluginHost judged
    /// UNANNOUNCED (<c>WorldLoadRule.IsUnannouncedTeardown</c>) — an ordinary door/trigger
    /// transition never bumps this, only a discontinuity trans_pending never saw coming. The
    /// agent's own edge detector (<c>KotorCoop.Session.SerialEdgeWatch</c>) treats any change
    /// as one load event; the exact count carries no meaning beyond "at least one more since
    /// last observed". Rides to a peer too, unmodified, as <c>WorldDigest.LoadSerial</c> —
    /// see that type's own doc comment for why a raw counter, not a bool, survives a lost
    /// datagram on a 1 Hz channel without losing the edge.</summary>
    public const int OffsetWorldLoadSerial = 0x396C;


    // ---- mid-session load request (version 52, region 0x3F24..0x3F28, no TotalSize growth) --
    //
    // OffsetMenuLoadSerial's own request only ever worked from the MAIN MENU (no server
    // exists yet): CSWGuiSaveLoad::LoadGame's bFromMainMenu==0 branch additionally calls
    // CAppManager::DestroyServer + CClientExoApp::UnloadModule before CreateServer, which the
    // main-menu-only replay never needed and never did. This flag, set alongside the SAME
    // OffsetMenuLoadSerial request, tells MenuLoadController to run that extra teardown first
    // and source guiManager from [clientInternal+0x274] instead of the (nonexistent,
    // mid-session) main-menu object — see docs/research/session-save.md's dated entry on the
    // `loadgame` verb for the full disassembly trail. Rides in the free tail after the
    // dialogue-forced-conversation region (0x3F20 + 4 = 0x3F24, TotalSize is 0x4000, leaving
    // 0xDC bytes free before this; this region uses 4 of them). No TotalSize growth, no
    // relayout of anything else.

    /// <summary>Agent -&gt; plugin. Nonzero when the paired <see cref="OffsetMenuLoadSerial"/>
    /// request should be treated as a genuine MID-SESSION (pause-menu-equivalent) load rather
    /// than the ordinary main-menu-only replay: <c>MenuLoadController</c> tears the existing
    /// server down first (<c>CAppManager::DestroyServer</c> + <c>CClientExoApp::UnloadModule</c>
    /// — exactly what retail's own <c>CSWGuiSaveLoad::LoadGame</c> does before
    /// <c>CreateServer</c> when NOT called from the main menu) and sources <c>guiManager</c>
    /// from <c>[clientInternal+0x274]</c> rather than a main-menu object that does not exist
    /// mid-session. Written alongside <see cref="OffsetMenuLoadSerial"/>, BEFORE it — the same
    /// "everything the plugin reads must already be there" rule
    /// <see cref="OffsetAdoptWorldLogin"/>'s own comment states.</summary>
    public const int OffsetMenuLoadInSession = 0x3F24;


    // ---- agent liveness heartbeat (version 43, region 0x3780..0x3788) -------------
    //
    // The plugin's own Heartbeat field at 0x4C (below) proves the BLOCK is live — the
    // worker thread bumps it every ~16ms whether or not any agent is attached, so it can
    // never tell "an agent is pumping" from "an agent stopped responding an hour ago".
    // This is the other half: the AGENT'S OWN clock, stamped every dispatcher tick, so the
    // plugin can watch it stall and say so instead of quietly driving phantoms off a
    // picture nobody is updating any more. See AgentLiveness and AgentLivenessController.
    //
    // The value is Environment.TickCount64 (Windows' system-uptime counter), not a
    // sequence number: both this process and the plugin's read the SAME OS clock
    // independently, so the plugin computes staleness directly (now - lastBeat) with no
    // handshake and no risk of two processes' Stopwatches drifting apart. 0 means "no
    // agent has ever pumped this block" — TickCount64 is essentially never exactly zero
    // once written, so it doubles as the "ever live" sentinel with no separate flag.
    //
    // Placed in the gap the browser-details array leaves between itself and the OLD block
    // end (0x3400 + 8*0x70 = 0x3780, TotalSize was 0x3800 before version 42): the version
    // 42 comment above already establishes that gap is real and was deliberately left
    // alone rather than reused ("the browser details array runs to 0x3780 ... so neither
    // gap could hold two six-name movie lists" — the OTHER new region in that pass,
    // OffsetCinematicSerial, starts exactly at the old 0x3800 boundary instead). Confirmed
    // free by grepping every Offset* constant in this file for the range: nothing else
    // claims 0x3780..0x3800.

    /// <summary>Agent -&gt; plugin. Environment.TickCount64 at the agent's last dispatcher
    /// tick. 0 = no agent has ever pumped this block.</summary>
    public const int OffsetAgentHeartbeat = 0x3780;

}
