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
    public const int TotalSize = 0x9C0;

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
