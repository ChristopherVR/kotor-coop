using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>One remote player, as published to the game. 0x40 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x40)]
internal struct RemoteSlot
{
    [FieldOffset(0x00)] public uint Active;
    [FieldOffset(0x04)] public uint PeerId;
    [FieldOffset(0x08)] public float PositionX;
    [FieldOffset(0x0C)] public float PositionY;
    [FieldOffset(0x10)] public float PositionZ;
    [FieldOffset(0x14)] public float Facing;
    [FieldOffset(0x18)] public int Health;
    [FieldOffset(0x1C)] public uint Animation;

    // 24 bytes of ascii name follow at 0x20; accessed as raw bytes rather than a fixed
    // buffer so the struct stays trivially blittable.

    /// <summary>appearance.2da row of the remote player, so their phantom looks like them.</summary>
    [FieldOffset(0x38)] public uint Appearance;

    /// <summary>PlayerState flags: 1 in-dialogue, 2 paused, 4 in-combat.</summary>
    [FieldOffset(0x3C)] public uint Flags;
}

/// <summary>
/// The plugin's contract with the external agent, allocated inside swkotor.exe.
///
/// The agent finds this by scanning the game's private committed memory for the magic
/// signature, which is exactly how it already finds the mock harness's block. That reuse
/// is deliberate: the tested KotorCoop.Memory scanner and ProfileGameBridge drive this
/// with no new code on the .NET side, and profiles/plugin.json is a near copy of
/// profiles/mockgame.json.
///
/// Layout is byte-identical to KotorCoop.MockGame's GameStateBlock. Keep them in sync.
///
///   game  -> plugin -> block -> agent : local player state
///   agent -> block  -> plugin -> game : remote player state
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = TotalSize)]
internal struct SharedBlock
{
    /// <summary>
    /// Bytes 0x000-0x24F are byte-identical to KotorCoop.MockGame's GameStateBlock so the
    /// existing offset profiles keep working. The plugin control fields live AFTER that
    /// range, which is why the block is larger than the profile knows about.
    /// </summary>
    public const int TotalSize = 0x1100;

    public const int ProfileVisibleSize = 0x250;

    // ---- plugin control block (beyond the profile-visible range) --------------

    /// <summary>Plugin -&gt; agent. Frames observed by the frame hook. Proves it is installed.</summary>
    public const int OffsetFrameCounter = 0x250;

    /// <summary>Agent -&gt; plugin. 0 = idle, 1 = spawn and drive phantoms.</summary>
    public const int OffsetCommand = 0x254;

    /// <summary>
    /// Agent -&gt; plugin. Pointer to the local player's CSWSCreature.
    ///
    /// Discovery stays in the external agent, which already locates the player by scanning
    /// for the creature vtable and testing the is_pc flag. The plugin only needs somewhere
    /// valid to spawn near, and duplicating the scan in-process would be redundant.
    /// </summary>
    public const int OffsetPlayerCreature = 0x258;

    /// <summary>Plugin -&gt; agent. How many phantoms currently exist.</summary>
    public const int OffsetPhantomCount = 0x25C;

    /// <summary>Plugin -&gt; agent. Last failure code from a spawn attempt, 0 = fine.</summary>
    public const int OffsetLastStatus = 0x260;

    /// <summary>Plugin -&gt; agent. Object ids of the spawned phantoms, 8 x uint32.</summary>
    public const int OffsetPhantomIds = 0x264;

    // ---- ground items (version 2) ----------------------------------------------
    //
    // Desired-state reconciliation rather than a command queue. The agent writes the set
    // of items that SHOULD be on the ground; the plugin spawns what is missing, destroys
    // what is no longer wanted, and reports per-slot status back. An item that vanishes
    // on its own (the local player picked it up) is reported as ItemStateVanished until
    // the agent stops wanting it.

    /// <summary>Agent -&gt; plugin. 8 slots of <see cref="ItemSlotStride"/> bytes each.</summary>
    public const int OffsetItemSlots = 0x290;

    /// <summary>wanted u32 @0, itemId u32 @4, position 3xf32 @8, resref char[16] @0x14.</summary>
    public const int ItemSlotStride = 0x30;

    /// <summary>Plugin -&gt; agent. 8 slots: itemId u32 @0, objectId u32 @4, state u32 @8, error i32 @0xC.</summary>
    public const int OffsetItemStatus = 0x410;

    public const int ItemStatusStride = 0x10;

    /// <summary>
    /// Agent -&gt; plugin. Non-zero means "do not let this instance pause just because its
    /// window is not focused". Opt-in rather than automatic, because suppressing that
    /// pause is wrong for ordinary single-player use of the plugin.
    /// </summary>
    public const int OffsetKeepAliveUnfocused = 0x268;

    /// <summary>Plugin -&gt; agent. How many focus-loss pauses have been cleared.</summary>
    public const int OffsetPausesCleared = 0x26C;

    /// <summary>
    /// Agent -&gt; plugin. Item slot index (1-based) whose item the local player should walk
    /// over and pick up, 0 for none. A test hook: it drives the engine's own
    /// ActionPickUpItem so the real pickup path can be exercised without needing a
    /// pixel-accurate mouse click on a small object on the floor.
    /// </summary>
    public const int OffsetPickUpRequest = 0x280;

    /// <summary>Plugin -&gt; agent. Increments each time a pickup action is issued.</summary>
    public const int OffsetPickUpIssued = 0x284;

    // ---- enemy replication (version 3) -----------------------------------------
    //
    // The peer that owns enemies publishes their health; every other instance mirrors it
    // and is stopped from killing anything locally. Written as a plain array rather than
    // slots, because unlike items there is nothing to allocate: an enemy is identified by
    // its engine object id, which is the same number in both instances.

    /// <summary>Agent -&gt; plugin. How many enemy entries are valid.</summary>
    public const int OffsetEnemyCount = 0x488;

    /// <summary>Agent -&gt; plugin. objectId u32 @0, current u16 @4, max u16 @6, flags u32 @8.</summary>
    public const int OffsetEnemySlots = 0x490;

    public const int EnemySlotStride = 0x10;

    public const int EnemyCapacity = 32;

    /// <summary>Set in an enemy slot's flags when the owning peer says it is dead.</summary>
    public const uint EnemyFlagDead = 1;

    // ---- door replication (version 4) -------------------------------------------
    //
    // The agent writes the DESIRED state of doors in the local module: open state and
    // whether the door has been unlocked, both of which only ever advance (a door sync
    // value is monotonic so the reliable flag channel's max-merge applies cleanly). The
    // plugin applies them through the engine's own CSWSDoor::SetOpenState so the model
    // animates and the walkmesh follows — a raw field write moves the walkmesh but NOT
    // the model, giving a door you can walk through that still looks shut.

    /// <summary>Agent -&gt; plugin. How many door entries are valid.</summary>
    public const int OffsetDoorCount = 0x690;

    /// <summary>Agent -&gt; plugin. objectId u32 @0, openState u8 @4, unlocked u8 @5.</summary>
    public const int OffsetDoorSlots = 0x698;

    public const int DoorSlotStride = 0x08;

    public const int DoorCapacity = 32;

    // ---- equipment replication (version 5) --------------------------------------
    //
    // Per remote peer, the blueprint resrefs of the visibly equipped gear: body armor,
    // right weapon, left weapon. The plugin gives the phantom the real items through
    // CreateItemOnObject + ActionEquipItem, so armor changes the body model and weapons
    // appear in hand exactly as the engine normally renders them.

    /// <summary>Agent -&gt; plugin. 8 peers x 3 resrefs x 16 ascii bytes.</summary>
    public const int OffsetEquipSlots = 0x7A0;

    public const int EquipSlotStride = 0x30;

    /// <summary>Resref order within a peer's equip slot.</summary>
    public const int EquipBody = 0;
    public const int EquipRightWeapon = 1;
    public const int EquipLeftWeapon = 2;
    public const int EquipCount = 3;

    // ---- module travel (version 6) -----------------------------------------------
    //
    // Seamless travel: when the session decides this instance should follow a peer into
    // another module, the agent writes the destination and bumps the serial. The plugin
    // executes the move through the engine's own warp path (SetMoveToModuleString +
    // SetMoveToModulePending, the machinery the shipped `warp` cheat uses) — after
    // flushing everything it has spawned, because StoreCurrentModule serialises the live
    // area on the way out and a leftover phantom would become a permanent NPC there.

    /// <summary>Agent -&gt; plugin. Bumped once per travel request; written last.</summary>
    public const int OffsetTravelSerial = 0x920;

    /// <summary>Agent -&gt; plugin. Destination module name, null-terminated ascii.</summary>
    public const int OffsetTravelModule = 0x924;

    /// <summary>Agent -&gt; plugin. Optional start waypoint tag, null-terminated ascii.</summary>
    public const int OffsetTravelWaypoint = 0x944;

    /// <summary>Plugin -&gt; agent. Serial of the last request acted on.</summary>
    public const int OffsetTravelApplied = 0x964;

    /// <summary>Plugin -&gt; agent. 0 = ok, otherwise the failure code of the last request.</summary>
    public const int OffsetTravelStatus = 0x968;

    public const int TravelNameLength = 32;

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

    /// <summary>Agent -&gt; plugin. 0 idle, 1 connecting, 2 connected, 3 failed — for the
    /// menu to show a status line.</summary>
    public const int OffsetConnectStatus = 0x9EC;

    public const uint ConnectStatusIdle = 0;
    public const uint ConnectStatusConnecting = 1;
    public const uint ConnectStatusConnected = 2;
    public const uint ConnectStatusFailed = 3;

    /// <summary>Agent -&gt; plugin. Nonzero opts into the main-menu Co-op button. Off by
    /// default (the block is zero-initialised), so the GUI rebind — engine-call code that
    /// wants live verification — never runs unless coop.ini enables it.</summary>
    public const int OffsetConnectEnable = 0x9F0;

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

    // ---- placeable state (version 11) --------------------------------------------
    //
    // Chests, corpses, switches and containers. Mirrors the door channel one-for-one: the
    // agent writes the desired locked/open state per placeable id for the local module, and
    // the plugin applies it. Unlike doors, placeables have no walkmesh coupling, so the
    // apply is a plain field write with no engine call.

    /// <summary>Agent -&gt; plugin. How many placeable entries are valid.</summary>
    public const int OffsetPlaceableCount = 0xB58;

    /// <summary>Agent -&gt; plugin. objectId u32 @0, open u8 @4, unlocked u8 @5.</summary>
    public const int OffsetPlaceableSlots = 0xB60;

    public const int PlaceableSlotStride = 0x08;

    public const int PlaceableCapacity = 32; // occupies 0xB60..0xC60

    // ---- chat overlay clear (version 12) -----------------------------------------

    /// <summary>Agent -&gt; plugin. Bumped to wipe the on-screen overlay log.</summary>
    public const int OffsetChatClearSerial = 0xC60;

    // ---- dialogue spectate (version 12, region 0xC80..0xD00) ----------------------
    //
    // Plugin -> agent. On the token owner's instance the DialogueSpectateController reads
    // the current conversation's speaker + line off the live CSWSDialog each frame and
    // writes them here (see docs/research/dialogue-sync.md). The agent hands them to the
    // session's dialogue token so the OTHER player can be shown what the owner is hearing.
    // Reads are all guarded; an unresolvable line is written empty, never guessed.

    /// <summary>Plugin -&gt; agent. Current speaker, null-terminated ascii.</summary>
    public const int OffsetLocalDialogueSpeaker = 0xC80;

    public const int LocalDialogueSpeakerLength = 40; // occupies 0xC80..0xCA8

    /// <summary>Plugin -&gt; agent. Current line, null-terminated ascii.</summary>
    public const int OffsetLocalDialogueLine = 0xCA8;

    public const int LocalDialogueLineLength = 64; // occupies 0xCA8..0xCE8

    /// <summary>Plugin -&gt; agent. Bumped when the speaker/line pair changes; written last.</summary>
    public const int OffsetLocalDialogueSerial = 0xCE8;

    // ---- container-loot removal (region 0xD00..0xE00) -----------------------------
    //
    // Chests and corpses are shared, so an item the host loots out of a container must vanish
    // from the client's copy too or it could be taken twice. The agent declares the desired
    // (container, item) removals for the local module; the plugin resolves each container and
    // deletes the matching item through CSWSPlaceable::RemoveItem.

    /// <summary>Agent -&gt; plugin. How many container-loot removals are valid.</summary>
    public const int OffsetLootCount = 0xD00;

    /// <summary>Agent -&gt; plugin. containerId u32 @0, itemId u32 @4.</summary>
    public const int OffsetLootSlots = 0xD08;

    public const int LootSlotStride = 0x08;

    public const int LootCapacity = 24; // occupies 0xD08..0xDC8, inside the 0xD00..0xE00 region

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

    /// <summary>Plugin -&gt; agent. Bumped to ask for a password prompt for the selection.</summary>
    public const int OffsetBrowserPasswordSerial = 0xE14;

    /// <summary>Plugin -&gt; agent. Bumped when the player chooses HOST in the browser panel,
    /// so both halves of starting a session live in the same in-game screen.</summary>
    public const int OffsetBrowserHostSerial = 0xE18;

    /// <summary>Agent -&gt; plugin. Server entries: name[32], address[24], players u8, max u8.</summary>
    public const int OffsetBrowserEntries = 0xE20;

    public const int BrowserEntryStride = 0x40;
    public const int BrowserEntryName = 0x00;
    public const int BrowserEntryNameLength = 32;
    public const int BrowserEntryAddress = 0x20;
    public const int BrowserEntryAddressLength = 24;
    public const int BrowserEntryPlayers = 0x38;
    public const int BrowserEntryMaxPlayers = 0x39;

    /// <summary>Rows the panel can show; occupies 0xE20..0x1020, inside the region.</summary>
    public const int BrowserCapacity = 8;

    // ---- co-op death handling (version 14, region 0x1020..0x1080) -----------------
    //
    // KOTOR party members never truly die: at 0 HP they go "temp dead" and the engine
    // itself stands them up once no hostile has been near for a few seconds. Two engine
    // reflexes break co-op, and both are suppressed by byte-patch guards (DeathController):
    // control auto-swapping to another party member, and the all-party-dead game-over that
    // would drop ONE instance to the main menu while the peer plays on. This region carries
    // the downed state to the agent, and the peers' verdicts back.
    // See docs/research/death-handling.md.

    /// <summary>Plugin -&gt; agent. Nonzero while the local PC is temp-dead (downed).</summary>
    public const int OffsetLocalDowned = 0x1020;

    /// <summary>Plugin -&gt; agent. Bumped each time the local PC goes down (edge, not level).</summary>
    public const int OffsetLocalDownedSerial = 0x1024;

    /// <summary>Plugin -&gt; agent. Bumped when the engine tried to raise game-over and we
    /// swallowed it — the agent decides whether the whole session is wiped.</summary>
    public const int OffsetGameOverSerial = 0x1028;

    /// <summary>Agent -&gt; plugin. Nonzero suppresses the game-over screen (default on in a
    /// session). Cleared to let a genuine, synchronised party wipe through.</summary>
    public const int OffsetSuppressGameOver = 0x102C;

    /// <summary>Agent -&gt; plugin. Nonzero suppresses control auto-swap on PC death (and the
    /// Tab/portrait swap generally — the client control lock).</summary>
    public const int OffsetSuppressSwap = 0x1030;

    /// <summary>Agent -&gt; plugin. Bumped to revive the local PC (a peer reached them).</summary>
    public const int OffsetReviveSerial = 0x1034;

    /// <summary>Plugin -&gt; agent. Echoes the revive serial once applied.</summary>
    public const int OffsetReviveApplied = 0x1038;

    /// <summary>Plugin -&gt; agent. 0 = ok, else the revive failure code.</summary>
    public const int OffsetReviveStatus = 0x103C;

    /// <summary>Agent -&gt; plugin. Hit points to stand the PC up with (0 = engine default).</summary>
    public const int OffsetReviveHitPoints = 0x1040;

    public const int ItemSlotCapacity = 8;

    public const int ItemResrefLength = 16;

    public const uint ItemStateNone = 0;
    public const uint ItemStateAlive = 1;
    public const uint ItemStateVanished = 2;

    public const uint CommandIdle = 0;
    public const uint CommandSpawnAndDrive = 1;
    public const int SlotCapacity = 8;
    public const int SlotStride = 0x40;
    public const int SlotsOffset = 0x50;
    public const int NameLength = 24;
    public const int ModuleNameLength = 32;

    /// <summary>"KOCOOP01". The agent's signature is this plus <see cref="Version"/>.</summary>
    [FieldOffset(0x00)] public ulong Magic;

    /// <summary>
    /// Part of the agent's 16-byte scan signature, so bumping this intentionally severs
    /// old agents: version 2 added the ground-item slots and the appearance field,
    /// version 3 the enemy array, version 4 the door slots, version 5 the equipment
    /// resrefs, version 6 the module-travel request, version 7 shared pause, journal
    /// apply and the per-peer flags dword.
    /// </summary>
    [FieldOffset(0x08)] public uint Version;
    [FieldOffset(0x0C)] public uint Reserved;

    // Plugin -> agent.
    [FieldOffset(0x10)] public float LocalPositionX;
    [FieldOffset(0x14)] public float LocalPositionY;
    [FieldOffset(0x18)] public float LocalPositionZ;
    [FieldOffset(0x1C)] public float LocalFacing;

    // 32 bytes of module name at 0x20.

    [FieldOffset(0x40)] public int LocalHealth;
    [FieldOffset(0x44)] public uint LocalAnimation;

    // Agent -> plugin.
    [FieldOffset(0x48)] public uint RemoteCount;

    /// <summary>
    /// Incremented every plugin tick. This is how the agent tells a live plugin from a
    /// stale block left behind in freed memory, which is a real hazard given the scanner
    /// works by signature.
    /// </summary>
    [FieldOffset(0x4C)] public uint Heartbeat;

    // RemoteSlot[8] at 0x50.

    /// <summary>Guard dword written at +0x0C, part of the 16-byte signature.</summary>
    public const uint GuardValue = 0xC0FFEE01;

    /// <summary>"KOCOOP01" as a little-endian ulong.</summary>
    public static ulong MagicValue => 0x3130_504F_4F43_4F4BUL;
}
