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
    public const int TotalSize = 0x2000;

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

    /// <summary>Un-killable by module rule (a cutscene actor): drive its presence, but the
    /// combat mirror must not pin, heal or kill it.</summary>
    public const uint EnemyFlagPlot = 2;

    // ---- enemy presence (version 20) --------------------------------------------
    //
    // The owner's position, facing and animation per enemy, so the mirroring instance
    // drives its copies to the same spot instead of letting two AIs run two private
    // versions of the same fight. Slot i pairs with enemy slot i (same pump writes both);
    // the id is repeated here, written last, so a half-written slot never drives the
    // wrong creature.

    /// <summary>Agent -&gt; plugin. How many presence entries are valid.</summary>
    public const int OffsetEnemyPresenceCount = 0x1400;

    /// <summary>Agent -&gt; plugin. id u32 @0, position 3xf32 @4, facing f32 @0x10,
    /// animation u32 @0x14, flags u32 @0x18 (bit 0 dead), one-shot serial u32 @0x1C,
    /// one-shot animation u32 @0x20. Occupies 0x1410..0x1910.</summary>
    public const int OffsetEnemyPresenceSlots = 0x1410;

    public const int EnemyPresenceStride = 0x28;

    // ---- enemy one-shot capture (version 20) ------------------------------------
    //
    // Plugin -> agent: fire-and-forget animations (swings, parries, draws) captured per
    // creature by the animation hook, so the enemy owner can stream them. Fixed table
    // keyed by object id; a serial per entry makes repeats visible. The agent matches
    // ids against its enemy census and ignores the rest (companions, neutral NPCs).

    /// <summary>Plugin -&gt; agent. id u32 @0, animation u32 @4, serial u32 @8. Occupies
    /// 0x1920..0x1C20.</summary>
    public const int OffsetEnemyOneShotTable = 0x1920;

    public const int EnemyOneShotStride = 0x10;

    public const int EnemyOneShotCapacity = 48;

    // ---- party presence (version 20) --------------------------------------------
    //
    // The host's companions, driven on the mirroring instance by the plugin instead of
    // the agent's raw external position writes (which never touched the client twin, so
    // the rendered body trailed on the engine's 512 ms ease and stepped between ticks).
    // Keyed by NPC roster slot — identical across two instances of the same save, while
    // runtime object ids are not; the mirroring plugin resolves the slot against its OWN
    // party table.

    /// <summary>Agent -&gt; plugin. How many companion presence entries are valid.</summary>
    public const int OffsetPartyPresenceCount = 0x1C20;

    /// <summary>Agent -&gt; plugin. roster slot + 1 u32 @0 (0 = empty, written last),
    /// position 3xf32 @4, facing f32 @0x10, animation u32 @0x14, health i32 @0x18,
    /// one-shot serial u32 @0x1C, one-shot animation u32 @0x20. Occupies 0x1C30..0x1CD0.</summary>
    public const int OffsetPartyPresenceSlots = 0x1C30;

    public const int PartyPresenceStride = 0x28;

    public const int PartyPresenceCapacity = 4;

    // ---- combat participation (version 21) --------------------------------------
    //
    // Damage that landed on a peer's phantom HERE, so it can be applied to that peer's
    // real character in THEIR game — and the same figure arriving for the local player.
    // Cumulative totals, never events: the receiver applies the difference between the
    // total and what it has already taken, so a lost packet costs a tick of staleness
    // rather than a missed hit (the enemy channel's rule).

    /// <summary>Plugin -&gt; agent. Per phantom slot: phantomId u32 @0, cumulative damage
    /// u32 @4, last attacker id u32 @8, hit serial u32 @0xC. Occupies 0x1CD0..0x1D50.</summary>
    public const int OffsetPhantomHitSlots = 0x1CD0;

    public const int PhantomHitStride = 0x10;

    /// <summary>Agent -&gt; plugin. Cumulative damage the local player owes for hits taken on
    /// their phantom elsewhere, and who last landed one.</summary>
    public const int OffsetIncomingDamage = 0x1D50;

    public const int OffsetIncomingDamageAttacker = 0x1D54;

    /// <summary>Plugin -&gt; agent. Cumulative damage actually applied, so a restart or a
    /// re-attach cannot replay the whole history onto the player.</summary>
    public const int OffsetIncomingDamageApplied = 0x1D58;

    /// <summary>
    /// Agent -&gt; plugin, TEST HOOK. Applies a real engine damage effect to a phantom, the
    /// same way <see cref="OffsetPickUpRequest"/> drives a real pickup: staging a genuine
    /// fight on demand is unreliable (Handoff 7.0 — the Endar Spire's Sith are inert
    /// scripted placements), so this exercises the true damage path — engine effect,
    /// OnApplyDamage, the watch, the forward — without needing enemy AI to cooperate.
    /// Slot index @0, amount @4, serial last @8.
    /// </summary>
    public const int OffsetTestHitSlot = 0x1D5C;
    public const int OffsetTestHitAmount = 0x1D60;
    public const int OffsetTestHitSerial = 0x1D64;

    /// <summary>
    /// Agent -&gt; plugin, TEST HOOKS for driving the local player without injected input
    /// (which is a focus lottery — traps 22/25). "Talk" runs exactly what a click on an
    /// NPC runs, so it exercises the real conversation path including the dialogue gate;
    /// "walk" queues the engine's own ActionMoveToLocation.
    /// </summary>
    public const int OffsetTestTalkTag = 0x1D70;   // 32 bytes of ascii tag
    public const int OffsetTestTalkSerial = 0x1D90;
    public const int OffsetTestWalkX = 0x1D94;
    public const int OffsetTestWalkY = 0x1D98;
    public const int OffsetTestWalkSerial = 0x1D9C;
    public const int TestTalkTagLength = 32;

    /// <summary>Agent -&gt; plugin, TEST HOOK. Orders <c>attacker</c> to attack <c>target</c>
    /// through the engine's own ActionAttack, so a genuine fight can be staged (the test
    /// save's enemies are inert scripted placements — Handoff 7.0).</summary>
    public const int OffsetTestAttacker = 0x1DA0;
    public const int OffsetTestAttackTarget = 0x1DA4;
    public const int OffsetTestAttackSerial = 0x1DA8;

    // ---- kills a PEER earned (version 22) ---------------------------------------
    //
    // Agent -> plugin, on the ENEMY OWNER. A player fighting away from the owner could wear
    // an enemy down to one hit point and no further: the health merge floors at 1 and a
    // report saying "it is dead" was discarded, so with nobody else coming to finish it the
    // room never cleared. This is how the last point of damage gets delivered — the owner
    // runs the engine's own death for a creature a peer actually killed, so the kill is a
    // real one (loot, XP, scripts, corpse) rather than a health poke to zero.
    //
    // Ids first, then the count, then the serial LAST, so the plugin never reads a half
    // written list.

    public const int OffsetKillRequestIds = 0x1DB0;   // 8 x u32

    public const int KillRequestCapacity = 8;

    public const int OffsetKillRequestCount = 0x1DD0;

    public const int OffsetKillRequestSerial = 0x1DD4;

    // ---- why forwarded damage cannot finish a player off (region 0x1F60..0x1FA0) ---
    //
    // A player worn down in someone else's game should go down in their own. When they do
    // not, the honest answer is usually not a co-op bug: KOTOR's own modules pin the PC
    // with min-1-hp for whole scenes (the Endar Spire command deck, the Taris duel ring),
    // and inside such a scene NOBODY can be downed — not the remote player, not the local
    // one, not in a normal playthrough either. Rather than let that read as "forwarded
    // damage is broken", the plugin measures the flags and names the floor.
    // See docs/research/min-one-hitpoint.md.

    /// <summary>Plugin -&gt; agent. A <c>DownBlockReason</c>: 0 none, 1 min-1-hp, 2 plot,
    /// 3 the difficulty MinHP1 rule. A level, sampled — not an event.</summary>
    public const int OffsetDownBlockReason = 0x1F60;

    /// <summary>Plugin -&gt; agent. Bumped whenever the reason CHANGES, so the agent can
    /// surface it once per transition instead of once per frame.</summary>
    public const int OffsetDownBlockSerial = 0x1F64;

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

    /// <summary>Plugin -&gt; agent. Current line, null-terminated ascii. Version 17 moved it
    /// to the 0x1100 region: real dialogue lines run to ~290 characters and the original
    /// 64-byte slot cut them mid-sentence. (0xCA8..0xCE8 is now unused.)</summary>
    public const int OffsetLocalDialogueLine = 0x1100;

    public const int LocalDialogueLineLength = 192; // occupies 0x1100..0x11C0

    /// <summary>Plugin -&gt; agent. Bumped when the speaker/line pair changes; written last.</summary>
    public const int OffsetLocalDialogueSerial = 0xCE8;

    // ---- dialogue follow (version 18, region 0x1200..0x1300) ----------------------
    //
    // Spectating is the game's OWN dialogue UI on the non-owner's screen, not a transcript:
    // the spectator opens the identical conversation against its own copy of the NPC and
    // replays every reply the owner picks, so both screens sit on the same line. Only the
    // owner may choose; the spectator's ability to commit a reply is switched off while it
    // follows. See DialogueSpectateController and docs/research/dialogue-sync.md.

    /// <summary>Plugin -&gt; agent. Tag of the NPC the local player is talking to. A TAG, not
    /// an object id: ids are assigned per module load and the peer's copy of the same
    /// creature can hold a different one.</summary>
    public const int OffsetLocalDialoguePartnerTag = 0x1200;

    public const int LocalDialoguePartnerTagLength = 32; // occupies 0x1200..0x1220

    /// <summary>Plugin -&gt; agent. Bumped on every reply the local player picks.</summary>
    public const int OffsetLocalDialogueReplySerial = 0x1220;

    /// <summary>Plugin -&gt; agent. The reply SLOT the local player clicked. Slot rather than
    /// node index: both instances run the same .dlg, so the same slot walks the same
    /// branch, and the slot is what the engine's own reply call takes.</summary>
    public const int OffsetLocalDialogueReplyIndex = 0x1224;

    /// <summary>Agent -&gt; plugin. Nonzero while a PEER owns a conversation to follow.</summary>
    public const int OffsetRemoteDialogueActive = 0x1228;

    /// <summary>Agent -&gt; plugin. The owner's reply serial; a change means "replay a pick".</summary>
    public const int OffsetRemoteDialogueReplySerial = 0x122C;

    /// <summary>Agent -&gt; plugin. The slot to replay.</summary>
    public const int OffsetRemoteDialogueReplyIndex = 0x1230;

    /// <summary>Agent -&gt; plugin. Tag of the NPC to open the conversation against.</summary>
    public const int OffsetRemoteDialoguePartnerTag = 0x1240;

    public const int RemoteDialoguePartnerTagLength = 32; // occupies 0x1240..0x1260

    // ---- dialogue pick log (version 19, region 0x1260..0x13E0) --------------------
    //
    // A reply pick is only meaningful ON THE LINE IT WAS PICKED FROM: replaying "slot 1"
    // while the two screens show different nodes walks the spectator down the wrong branch,
    // and a spectator that opened late (the owner had already picked twice) can never catch
    // up from a single latest-pick value. So the owner publishes a small LOG of its recent
    // picks, each stamped with the FNV hash of the line it was made on, and the spectator
    // replays them in order, each one once its own screen reaches that line.

    /// <summary>Entries: u32 serial, u32 slot, u32 lineHash. Serial 0 = empty.</summary>
    public const int DialoguePickStride = 12;

    public const int DialoguePickCapacity = 8;

    /// <summary>Plugin -&gt; agent. The local player's recent picks. 0x1260..0x12C0.</summary>
    public const int OffsetLocalDialoguePicks = 0x1260;

    /// <summary>Agent -&gt; plugin. The conversation owner's recent picks. 0x12C0..0x1320.</summary>
    public const int OffsetRemoteDialoguePicks = 0x12C0;

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

    /// <summary>Non-zero when the host requires a password; the row shows a lock marker so
    /// the prompt that follows Connect is never a surprise.</summary>
    public const int BrowserEntryLocked = 0x3A;

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

    // ---- one-shot animations (version 16, region 0x1050..0x1080) ------------------
    //
    // Weapon draws, attack swings, flourishes and item use are FIRE-AND-FORGET: dispatched,
    // played and finished between two polls, and some never touch an object field at all.
    // They are captured by hooking the engine's dispatch (AnimationHook) and carried as an
    // EDGE — an id plus a sequence, so a repeated identical flourish still replicates —
    // rather than as a level to be sampled. See docs/research/animation-and-minimap.md.

    /// <summary>Plugin -&gt; agent. The local player's most recent one-shot animation id.</summary>
    public const int OffsetLocalOneShotAnim = 0x1050;

    /// <summary>Plugin -&gt; agent. Bumped per dispatch; a repeat of the same id still counts.</summary>
    public const int OffsetLocalOneShotSerial = 0x1054;

    /// <summary>Agent -&gt; plugin. Per remote slot: { u32 animation, u32 serial }. A parallel
    /// array rather than a RemoteSlot field because the 0x40-byte slot is full.</summary>
    public const int OffsetRemoteOneShot = 0x1060;

    public const int RemoteOneShotStride = 0x08;
    public const int RemoteOneShotAnim = 0x00;
    public const int RemoteOneShotSerial = 0x04;

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
