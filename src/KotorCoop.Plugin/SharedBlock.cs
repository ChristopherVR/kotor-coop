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
    /// <summary>
    /// Grown from 0x2000 for the damage ledger's owner half (0x2000..0x2118): the peer half
    /// fills the last free 256 bytes below 0x2000 exactly, and cramming both into it would
    /// have halved how many enemies a fight can carry damage for — which is the one failure
    /// this channel exists to remove. Growing costs a page of the game's own heap and
    /// nothing else; the agent finds the block by signature and reads fixed offsets.
    /// </summary>
    /// <summary>Grown from 0x2300 for the widened bark rings (version 31): a bark has to
    /// carry its TEXT and sound resref, not two pointers into the producing process.</summary>
    /// <summary>Grown from 0x3300 for the companion roster (version 34). The 0x2120..0x2260
    /// hole left by the old bark rings is deliberately NOT reused — it is documented as dead
    /// so a stale reader finds zeros there rather than half a record, and quietly repurposing
    /// it would turn that promise into a trap for the next person who reads the comment.</summary>
    /// <summary>Grown from 0x3400 for the browser details array (version 36): the server rows
    /// at 0xE20 cannot widen, because the death region begins exactly where they end. Grown
    /// again for the load-screen texture in that array (version 37).</summary>
    /// <summary>Grown from 0x3800 for the transition-cinematic channel (version 42): the
    /// browser details array runs to 0x3780 and the cheat region's tail to 0x33C0, so neither
    /// gap could hold two six-name movie lists. Same reasoning as the growth to 0x3800.</summary>
    /// <summary>
    /// Grown from 0x3A00 for the peer-effect channel (version 44): the tail past
    /// <see cref="OffsetTravelMovieNames"/> (0x3884..0x38E4) only has 0x11C free bytes before
    /// 0x3A00 — not enough for an 8-entry ring in each direction — and version 43 landed first
    /// (<see cref="OffsetAgentHeartbeat"/>, 0x3780..0x3788, squeezed into the browser-details
    /// tail's own leftover gap). This region starts cleanly at 0x3A00, past both. See
    /// docs/research/effect-sync.md.
    /// </summary>
    /// <summary>
    /// Grown from 0x3C00 for the creature-liveness reconciliation channel (version 45): the
    /// peer-effect region's own tail (0x3B88) leaves only 0x78 free bytes before 0x3C00 — not
    /// enough for a 96-entry array — so this region starts cleanly at the old boundary
    /// instead, the same choice version 42 and 44 both made. Rounded up from the region's
    /// actual end (0x3F08) to the next clean 0x100 boundary, matching every previous growth
    /// here landing on a round number. See docs/research/creature-liveness.md.
    /// </summary>
    public const int TotalSize = 0x4000;

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

    /// <summary>
    /// Plugin -&gt; agent. Object id of the phantom in slot 0 ONLY.
    ///
    /// ⚠ This was declared as "8 x uint32" and it never was: the very next dword, 0x268,
    /// is <see cref="OffsetKeepAliveUnfocused"/>, 0x26C is <see cref="OffsetPausesCleared"/>
    /// and 0x280 is <see cref="OffsetPickUpRequest"/>. With one remote player only index 0
    /// is ever written, so the collision was invisible; the SECOND phantom — i.e. the third
    /// player — writes its object id straight over the agent's keep-alive flag, and the
    /// focus keeper then writes its pause counter over what the agent reads back as
    /// "phantom 2's object id". The real table lives at <see cref="OffsetPhantomIdTable"/>;
    /// this stays as the slot-0 mirror so an agent built against the old layout still sees
    /// the first phantom.
    /// </summary>
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

    /// <summary>
    /// wanted u32 @0, itemId u32 @4, position 3xf32 @8, resref char[16] @0x14, then the
    /// per-instance identity (block version 39) in the tail the stride always had spare:
    /// upgrades u32 @0x24, stackSize u32 @0x28, and charges/maxCharges/flags packed into
    /// one dword @0x2C (byte 0 charges, byte 1 max charges, byte 2 flags).
    ///
    /// Packed into a dword rather than laid out as bytes because the agent writes each field
    /// with a separate WriteProcessMemory and the plugin reads the slot on its own frames:
    /// one write is one thing the plugin can see half of, and three bytes written
    /// individually are three. The `wanted` flag is still published last, which is what
    /// actually makes the whole slot atomic enough.
    /// </summary>
    public const int ItemSlotStride = 0x30;

    /// <summary>Identity flags byte inside the slot's packed dword — mirrors
    /// <c>ItemIdentity.Flag*</c> on the wire. Bit 0 KNOWN gates every other bit and every
    /// other identity field: zero means the agent had nothing to say, and the plugin must
    /// leave the item the engine just created exactly as it is.</summary>
    public const uint ItemIdentityKnown = 1 << 0;

    public const uint ItemIdentityIdentified = 1 << 1;
    public const uint ItemIdentityStolen = 1 << 2;
    public const uint ItemIdentityNonEquippable = 1 << 3;

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
    /// animation u32 @0x14, flags u32 @0x18 (bit 0 dead, bit 1 plot), one-shot serial
    /// u32 @0x1C, one-shot animation u32 @0x20, appearance.2da row u16 @0x24 and
    /// repute.2da faction u16 @0x26 (version 26 — for materialising an enemy this
    /// instance has no copy of; 0 appearance = unknown, do not materialise).
    /// Occupies 0x1410..0x1910.</summary>
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

    // 0x1DB0..0x1DD8 was the kill-request channel (version 22): a peer asking the enemy
    // owner to run the engine's death for a creature the peer had beaten down to the
    // health floor. Retired 2026-08-07 with the health min-merge it belonged to — the
    // owner now applies a peer's damage as real damage, so a kill falls out of the
    // arithmetic and needs no separate "it is dead" message to trust. Reused below.

    /// <summary>
    /// Agent -&gt; plugin, TEST HOOK. Makes a named creature bark a known line, through the
    /// engine's own BarkString and WITHOUT the replay guard, so the capture hook sees it
    /// exactly as it sees a bark the game produced.
    ///
    /// Same reasoning as the attack hook next door: provoking a real one is unreliable.
    /// Barks come from scripts and AI actions, the starting area's droids bark on triggers
    /// that automation cannot reach, and clicking one opens a conversation instead. Without
    /// this the bark channel could only be tested by a human playing the game — which is how
    /// it shipped broken twice, most recently with the capture stub reading the wrong stack
    /// slots and nothing to notice.
    ///
    /// Tag @0 (32 bytes of ascii), serial last @0x20.
    /// </summary>
    public const int OffsetTestBarkTag = 0x1DB0;
    public const int OffsetTestBarkSerial = 0x1DD0;
    public const int TestBarkTagLength = 32;

    // ---- damage a PEER dealt (version 23) ---------------------------------------
    //
    // The channel that replaced the health min-merge, in both directions. Read
    // docs/research/peer-damage-ledger.md; the short version is that HEALTH is a level
    // that has to be sampled and that the owner's own mirror write can erase before the
    // sample is ever taken, while DAMAGE DEALT is a monotonic sum that nothing in the
    // game can touch, because it is never read back out of the game.
    //
    // Peer side (plugin -> agent): the damage hook accumulates, per TARGET object id, the
    // raw damage the local player and party have dealt since the ids were last assigned.
    // Owner side (agent -> plugin): the total every peer has dealt to each of the owner's
    // enemies, which the plugin turns into real engine damage — its own copy of that
    // creature then dies its own real death, corpse, loot, OnDeath and all.
    //
    // Both directions are CUMULATIVE TOTALS, never events. Applying the difference against
    // what has already been applied makes the channel order-independent, immune to a
    // duplicate, and costs one tick of staleness rather than a lost hit when a datagram
    // goes missing. Same rule as the phantom-hit channel above.

    /// <summary>Plugin -&gt; agent. objectId u32 @0, cumulative raw damage u32 @4.
    /// Occupies 0x1E60..0x1F60 exactly — 32 entries, the wire cap.</summary>
    public const int OffsetDamageDealtSlots = 0x1E60;

    /// <summary>Agent -&gt; plugin. Same shape, the other way: the total all peers have dealt
    /// to each of this instance's enemies. Occupies 0x2018..0x2118.</summary>
    public const int OffsetDamageOwedSlots = 0x2018;

    public const int DamageLedgerStride = 0x08;

    /// <summary>Enemies one instance can carry damage for at a time. The ledger only holds
    /// creatures actually damaged and not yet seen dead, so this is a live-fight bound, not
    /// a module one; matches <c>ProtocolConstants.MaxEnemyDamageEntries</c>.</summary>
    public const int DamageLedgerCapacity = 32;

    /// <summary>Plugin -&gt; agent. How many damage-dealt entries are valid.</summary>
    public const int OffsetDamageDealtCount = 0x2000;

    /// <summary>Plugin -&gt; agent. Hits the ledger had no free slot for. Diagnostic, but a
    /// non-zero value means a peer's damage is being silently under-reported.</summary>
    public const int OffsetDamageDealtDropped = 0x2004;

    /// <summary>
    /// Plugin -&gt; agent. Bumped whenever the ledger restarts (a module change, a re-attach).
    ///
    /// ⚠ This is what lets the owner tell a RESET from a stale datagram. Without it, a peer
    /// that leaves the module and comes back sends totals that are *smaller* than the ones
    /// the owner last saw, the owner reads that as "nothing new", and everything the peer
    /// does from then on is ignored forever.
    /// </summary>
    public const int OffsetDamageDealtGeneration = 0x2008;

    /// <summary>
    /// Agent -&gt; plugin. The owner's own ledger epoch (the FNV hash of the module the ids
    /// were read in). The plugin clears its applied totals when this changes, so the two
    /// halves restart together instead of the plugin holding a high applied total against
    /// an agent that has already gone back to zero — which would swallow the first
    /// module's worth of a peer's damage in the new one.
    /// </summary>
    public const int OffsetDamageOwedEpoch = 0x200C;

    /// <summary>Agent -&gt; plugin. How many damage-owed entries are valid.</summary>
    public const int OffsetDamageOwedCount = 0x2010;

    /// <summary>Plugin -&gt; agent. How many damage applications have run, for the dashboard
    /// and for telling "nothing is arriving" from "arriving and being refused".</summary>
    public const int OffsetDamageOwedApplied = 0x2014;

    /// <summary>
    /// Agent -&gt; plugin. Non-zero on an instance that MIRRORS enemies, i.e. one whose damage
    /// has to be reported to somebody else.
    ///
    /// Off by default, which matters more than it looks: the accumulation lives inside the
    /// engine's damage hook, so on the fight's owner — where every point of damage in the
    /// game is already authoritative and nobody will ever read the ledger — this saves a
    /// table scan per damage event, and stops a table that is never pruned there from
    /// filling up and reporting drops that mean nothing.
    /// </summary>
    public const int OffsetDamageDealtEnabled = 0x2118;

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

    // ---- NPC barks (version 27) --------------------------------------------------
    //
    // The one-liners an NPC throws into a bubble without ever opening the dialogue system
    // (the utility droid's beep). They reach no other channel, so one player got a line and
    // everybody else got silence. Captured by BarkHook, replayed by BarkController.
    //
    // Both directions are a small ring of (speaker, three verbatim engine arguments), with
    // the SERIAL WRITTEN LAST per entry so a half-written slot is never acted on — the same
    // discipline the enemy and damage regions use.

    /// <summary>
    /// Plugin -&gt; agent. One captured bark per slot:
    ///   +0x00 speaker (SERVER object id, u32)
    ///   +0x04 flags   (u32, the engine's ignore-range argument)
    ///   +0x08 textLen (u32)
    ///   +0x10 resref  (16 raw bytes — a CResRef is char[16], not a string)
    ///   +0x20 text    (up to <see cref="BarkTextMax"/> bytes, engine encoding, no NUL)
    ///   +0xFC serial  (u32) — LAST DWORD OF THE SLOT, so "serial written last" still holds
    /// Occupies 0x2300..0x2B00 (8 x 0x100).
    ///
    /// ⚠ The old 0x14 slot carried four opaque dwords, two of which were POINTERS into the
    /// producing process. That is what killed the engine on replay; the contents have to
    /// travel, and they do not fit in 0x14. See docs/research/bark-replay.md.
    /// The old rings at 0x2120..0x2260 are left dead rather than reused, so a stale agent
    /// reading the previous layout finds zeros instead of half a record.
    /// </summary>
    public const int OffsetLocalBarkSlots = 0x2300;

    /// <summary>Agent -&gt; plugin. Same shape: barks a peer produced, for this instance to
    /// replay. Occupies 0x2B00..0x3300.</summary>
    public const int OffsetRemoteBarkSlots = 0x2B00;

    public const int BarkStride = 0x100;

    /// <summary>Bytes of bark text carried per slot. Matches <c>BarkHook.TextMax</c>.</summary>
    public const int BarkTextMax = 200;

    /// <summary>A CResRef is a fixed char[16].</summary>
    public const int BarkResRefBytes = 16;

    // Field offsets within a bark slot.
    public const int BarkSpeakerOffset = 0x00;
    public const int BarkFlagsOffset = 0x04;
    public const int BarkTextLengthOffset = 0x08;
    public const int BarkResRefOffset = 0x10;
    public const int BarkTextOffset = 0x20;
    public const int BarkSerialOffset = 0xFC;

    /// <summary>Barks carried per direction. They are occasional and the pump drains them
    /// several times a second; matches <c>BarkHook.Capacity</c>.</summary>
    public const int BarkCapacity = 8;

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

    // ---- phantom id table (relocated, region 0x1F80..0x1FA0) ---------------------
    //
    // The real per-slot phantom object ids. See OffsetPhantomIds for why they could not
    // stay at 0x264: that "array" only ever had room for one entry before the next field
    // began. Placed in the block's unused tail so no existing offset moves — an agent that
    // has not been rebuilt keeps reading slot 0 from 0x264, which the plugin still mirrors.

    /// <summary>Plugin -&gt; agent. Object ids of the spawned phantoms, one per remote slot,
    /// <see cref="SlotCapacity"/> x uint32. 0 = no body in that slot. Occupies
    /// 0x1F80..0x1FA0.</summary>
    public const int OffsetPhantomIdTable = 0x1F80;
    // ---- client-triggered encounters (0x1DE0..0x1E60) ------------------------------
    //
    // A client's encounters are disarmed (an encounter spawn there would advance that
    // instance's object-id counter and break the id identity every other channel keys on),
    // so walking into an ambush used to do nothing at all until the host walked in too.
    // Now the client REPORTS where it stands and the host's engine does the spawn — one
    // engine ever spawns, ids stay identical. See docs/research/client-encounters.md.
    //
    // Both halves are levels, not events: the watch republishes the current set every pass
    // and the request region is rewritten every pump, so a missed read costs a tick.

    /// <summary>Agent -&gt; plugin. 1 only on a peer that does NOT own enemies. The owner's
    /// engine fires its own encounters, so the watch is dead weight there.</summary>
    public const int OffsetEncounterWatch = 0x1DE0;

    /// <summary>Plugin -&gt; agent. How many entries of <see cref="OffsetEncounterInside"/>
    /// are valid.</summary>
    public const int OffsetEncounterInsideCount = 0x1DE4;

    /// <summary>Plugin -&gt; agent. Bumped whenever the set changes, so the agent can log a
    /// transition without diffing.</summary>
    public const int OffsetEncounterInsideSerial = 0x1DE8;

    /// <summary>Plugin -&gt; agent. Object ids of the encounters whose activate area contains
    /// the local player. Occupies 0x1DEC..0x1DFC.</summary>
    public const int OffsetEncounterInside = 0x1DEC;

    public const int EncounterInsideCapacity = 4;

    /// <summary>Agent -&gt; plugin. How many request slots are valid.</summary>
    public const int OffsetEncounterRequestCount = 0x1DFC;

    /// <summary>Agent -&gt; plugin. Bumped after the slots are written, and the only thing the
    /// plugin re-reads: a half-written request list is never acted on.</summary>
    public const int OffsetEncounterRequestSerial = 0x1E00;

    /// <summary>Agent -&gt; plugin. encounter object id u32 @0, requesting peer id u32 @4.
    /// Occupies 0x1E04..0x1E44.</summary>
    public const int OffsetEncounterRequests = 0x1E04;

    public const int EncounterRequestStride = 0x08;

    public const int EncounterRequestCapacity = 8;

    /// <summary>Plugin -&gt; agent. How many encounters this instance has actually started on
    /// a peer's behalf, and how many requests it turned down, for the log and the battery.</summary>
    public const int OffsetEncounterFired = 0x1E44;
    public const int OffsetEncounterRefused = 0x1E48;

    /// <summary>Plugin -&gt; agent. Why the last refusal happened
    /// (<see cref="EncounterController"/>'s status codes). 0 = nothing refused yet.</summary>
    public const int OffsetEncounterStatus = 0x1E4C;

    /// <summary>
    /// Agent -&gt; plugin (version 29). Nonzero while a REMOTE peer standing in this player's
    /// module has its engine genuinely in combat.
    ///
    /// ⚠ Remote peers only, never this player's own state. The plugin uses this as a claim
    /// to impose combat locally and then release it again, so if it included the local
    /// player the release would cut short a fight this engine is genuinely running: the
    /// host, fighting alone, would publish its own combat, hold itself in it, and then force
    /// itself out the moment the flag dropped.
    ///
    /// This exists because a client's engine cannot work it out for itself and never will:
    /// <see cref="Odyssey.MarkAsMirroredEnemy"/> clears the attack-permitted bit on every
    /// mirrored enemy, on purpose, so the client's copies never swing and its combat state
    /// never rises. Measured live: host creature +0x4E0 = 1, client = 0, same room, same
    /// second. Combat state gates round timing, the HUD, combat music and auto-pause, so
    /// leaving it false on the client is not a cosmetic gap.
    /// </summary>
    public const int OffsetSessionInCombat = 0x1E50;

    /// <summary>
    /// Plugin -&gt; agent (version 30). Bumped each time this player releases a pause that was
    /// MIRRORED from a peer, so the agent can tell everyone the shared pause is over.
    ///
    /// A serial rather than a flag: the release is an edge, and a flag would either be missed
    /// between polls or need a clear-after-read handshake across two processes.
    /// </summary>
    public const int OffsetLocalPauseReleaseSerial = 0x1E54;

    /// <summary>
    /// Agent -&gt; plugin (version 30). Bumped when a PEER releases the shared pause, so this
    /// instance drops its own pause even though it is the one that started it.
    ///
    /// This is what makes either player able to end a shared pause. Without it only the
    /// originator could, and the other player's pause key unfroze their game alone.
    /// </summary>
    public const int OffsetRemotePauseReleaseSerial = 0x1E58;

    /// <summary>
    /// ROUND 39: the owner id (in the OWNER's id space — the census/phantom-damage space) of
    /// the mirrored enemy this instance is currently letting fight the local player for real,
    /// under `local_combat`, or 0 for none. Plugin-written, agent-read: the agent filters this
    /// id out of what it writes back into `EnemyPresenceCount`'s array on every pump, which is
    /// what actually removes the creature from the census (round 37/38's proven mechanism —
    /// genuinely absent from the published set, not merely un-driven). A level, not an edge:
    /// self-healing if a packet is lost, and the plugin clears it back to 0 itself once the
    /// local fight ends (the enemy dies, the player disengages, or a bounded timeout), which
    /// lets the creature rejoin the census on the very next pump with no separate "un-exclude"
    /// message needed. See docs/research/shared-state-audit.md §2, round 39.
    /// </summary>
    public const int OffsetLocalCombatExcludedId = 0x1E5C;

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

    // ---- dialogue node sync (version 32, region 0x1320..0x1330) -------------------
    //
    // ⚠ THIS REPLACES THE PICK LOG ABOVE AS THE THING THE FOLLOWER IS DRIVEN BY. The pick log
    // is still published, and is now DIAGNOSTIC ONLY — it says what the owner pressed, which
    // is worth having in a log, and nothing reads it to decide anything.
    //
    // Replaying the owner's reply SLOT cannot converge, because a slot is not a property of
    // the conversation but of one instance's evaluation of it: the starting entry, the set of
    // replies offered and a reply's outgoing link are each chosen by scripts that run
    // separately on each machine (Odyssey's node-sync section carries the full argument and
    // the addresses). So the owner publishes its POSITION instead — the index of the entry on
    // screen, a plain array index into the .dlg both instances loaded — and the follower is
    // driven onto that entry through the engine's own producer. The engine agrees this is the
    // quantity that matters: CSWSDialog::HandleReply refuses a reply whose claimed entry
    // index does not match the live one.
    //
    // -1 means "no conversation, or the index could not be trusted", and is never driven.

    /// <summary>Plugin -&gt; agent. The entry index the local conversation is showing, or -1.</summary>
    public const int OffsetLocalDialogueNode = 0x1320;

    /// <summary>Plugin -&gt; agent. Bumped when the local node changes; written last, so a
    /// half-written pair reads as the previous node rather than as a torn one.</summary>
    public const int OffsetLocalDialogueNodeSerial = 0x1324;

    /// <summary>Agent -&gt; plugin. The entry index the conversation's OWNER is showing, or -1.</summary>
    public const int OffsetRemoteDialogueNode = 0x1328;

    /// <summary>Agent -&gt; plugin. Bumped when the owner's node changes.</summary>
    public const int OffsetRemoteDialogueNodeSerial = 0x132C;

    // ---- per-object interaction locks (version 33, region 0x1330..0x13E0) -------------
    //
    // "Hold this one still, I am using it." Ownership in this mod is one-directional
    // (docs/research/shared-state-audit.md §4): the host owns companions, enemies, doors and
    // containers, so a client that walks up to Trask and talks to him is talking to a puppet
    // while the HOST's AI — never told — keeps walking the real one around. That is the
    // reported "Trask is still moving around on the host's and other players' screens".
    //
    // The lock is claimed the moment a player starts interacting, which is EARLIER than the
    // conversation token can be granted, and that is what makes it fair: the host's own
    // dialogue claim runs the arbiter in-process at zero latency while a client's costs a
    // round trip, so without this the host wins every contested NPC by construction.
    //
    // A LEASE, re-sent while held. There is no release message to lose, so a client that dies
    // mid-conversation cannot strand an NPC for the rest of the session.

    /// <summary>Plugin -&gt; agent. Tag of the object the local player is interacting with, or
    /// empty. A TAG, not an object id — ids are per module load.</summary>
    public const int OffsetLocalInteractionTag = 0x1330;

    public const int LocalInteractionTagLength = 32; // occupies 0x1330..0x1350

    /// <summary>Plugin -&gt; agent. What kind of interaction (see InteractionKind).</summary>
    public const int OffsetLocalInteractionKind = 0x1350;

    /// <summary>Plugin -&gt; agent. Nonzero while the claim is live. A LEVEL, not an edge.</summary>
    public const int OffsetLocalInteractionActive = 0x1354;

    /// <summary>Agent -&gt; plugin. How many lock slots below are valid.</summary>
    public const int OffsetInteractionLockCount = 0x1358;

    /// <summary>Agent -&gt; plugin. Entries: peerId u32 @0, kind u32 @4, tag[32] @8. Occupies
    /// 0x1360..0x13E0 (4 x 0x28 — the protocol's MaxInteractionLocks).</summary>
    public const int OffsetInteractionLocks = 0x1360;

    public const int InteractionLockStride = 0x28;

    public const int InteractionLockCapacity = 4;

    public const int InteractionLockTagLength = 32;

    // ---- companion roster reconciliation (version 34, region 0x3300..0x3320) ----------
    //
    // The channel AddPartyMember / RemovePartyMember never had. A conversation that recruits or
    // dismisses a companion runs its scripts on ONE instance, so the other instance's party
    // table never hears — and the party table is what the whole companion channel keys on
    // (PartyPresenceController resolves the host's "NPC slot 2" against its OWN table). After a
    // divergence the mirror drives the wrong body, or none.
    //
    // ⚠ PACKED INTO ONE DWORD PER DIRECTION ON PURPOSE. These words are read by one process
    // while the other writes them. Two separate fields for "recruited" and "in the party" could
    // be sampled half-updated, which would need a serial and a re-read to detect (the
    // discipline the dialogue node pair uses). A single aligned dword store cannot tear on x86,
    // so the question does not arise. Nine slots is the engine's own bound — CSWPartyTable::
    // AddMember at 0x00565648 refuses nNPC >= 9 — so both masks fit in one word twice over.
    //
    // ⚠ BIT 31 IS NOT "SOMETHING IS RECRUITED", IT IS "SOMEBODY WROTE THIS". A game at the main
    // menu has no party table and leaves the word at zero, which is bit-identical to a real
    // roster with nothing recruited. Without the bit, a host would publish "nobody is in your
    // party" during a loading screen and its client would obediently dismiss everyone.

    /// <summary>Plugin -&gt; agent. This instance's roster, republished every frame from its own
    /// party table. Bits 0..8 recruited, bits 16..24 in the active party, bit 31 valid.</summary>
    public const int OffsetLocalPartyRoster = 0x3300;

    /// <summary>Agent -&gt; plugin. The HOST's roster, same packing. Zero (not merely
    /// all-masks-clear, but bit 31 clear) means "nobody is telling this instance what its
    /// roster should be" — the host itself, a client before the first level arrives, a
    /// disconnected agent, a disabled channel — and nothing is reconciled.</summary>
    public const int OffsetHostPartyRoster = 0x3304;

    /// <summary>Plugin -&gt; agent. Roster adds this instance has performed. Diagnostic, and
    /// the one that says the channel WORKED rather than merely arrived.</summary>
    public const int OffsetPartyRosterAdds = 0x3308;

    /// <summary>Plugin -&gt; agent. Roster removes this instance has performed.</summary>
    public const int OffsetPartyRosterRemoves = 0x330C;

    /// <summary>Plugin -&gt; agent. The last refusal this instance hit, so a reconciliation that
    /// keeps being refused is visible without a log dive. 0 = nothing refused.</summary>
    public const int OffsetPartyRosterStatus = 0x3310;

    public const int PartyRosterAvailableShift = 0;
    public const int PartyRosterPartyShift = 16;
    public const uint PartyRosterSlotMask = 0x1FF;
    public const uint PartyRosterValid = 0x8000_0000;

    /// <summary>Roster slots. The engine's bound, not a chosen cap: CSWPartyTable::AddMember
    /// (0x00565620) refuses nNPC &lt; 0 or &gt;= 9, and availability at party table +0x30 is
    /// int[9].</summary>
    public const int PartyRosterSlots = 9;

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

    // ---- co-op cheats (version 41, region 0x3328..0x33C0) ------------------------
    //
    // Version 40, not 39: this landed in the same integration as item identity, which took
    // 39 first. Two builds that disagree about whether this region exists must fail to find
    // each other rather than read each other's fields as noise, so the number has to move.
    //
    // Version 41 grew the region's tail by four dwords (0x33B0..0x33C0) for the `setitem`
    // staging cheat — the four identity fields that do not fit the generic target/amount
    // pair. Nothing already in the region moved, and a mixed pair would technically survive
    // (an old plugin answers "unknown cheat kind 7" and an old agent never sends it), but
    // the rule here is that the layout number moves whenever the layout does: a channel whose
    // meaning is decided by which build you are running is not a channel anyone can debug.
    //
    // The staging surface: one serial-edged request the agent writes and the plugin runs on
    // the game thread, because every one of these ends in an engine call and engine calls
    // only ever happen there.
    //
    // It carries a DETAIL string back, which is not decoration. A cheat that cannot be
    // applied has to say WHAT IT SAW — "that id is a phantom", "no creature 0x2A here",
    // "the module pins you at one hit point" — or a guard that fails safe is
    // indistinguishable from a guard that silently does nothing, and the whole point of
    // staging commands is that a live session can trust the answer without a human watching
    // the screen. The plugin logs the same sentence.
    //
    // Deliberately ONE request in flight. These are typed by a human a few times a minute;
    // a ring would add ordering questions to a channel whose whole value is that its answer
    // arrives before the next line is typed.

    /// <summary>Agent -&gt; plugin. Bumped LAST, after every other field of the request, so
    /// the plugin can never observe a half-written cheat.</summary>
    public const int OffsetCheatSerial = 0x3328;

    /// <summary>Agent -&gt; plugin. One of the <c>Cheat*</c> kind constants below.</summary>
    public const int OffsetCheatKind = 0x332C;

    /// <summary>Agent -&gt; plugin. Engine object id the cheat aims at; 0 = "the local player"
    /// for the self cheats, or "resolve <see cref="OffsetCheatText"/> as a tag" for slay.</summary>
    public const int OffsetCheatTarget = 0x3330;

    /// <summary>Agent -&gt; plugin. The numeric argument: hit points for a top-up, stack size
    /// for an item, 1/0 for a toggle.</summary>
    public const int OffsetCheatAmount = 0x3334;

    /// <summary>Agent -&gt; plugin. Destination for a teleport (world coordinates).</summary>
    public const int OffsetCheatX = 0x3338;
    public const int OffsetCheatY = 0x333C;
    public const int OffsetCheatZ = 0x3340;

    /// <summary>Agent -&gt; plugin. A tag or a blueprint resref, null-terminated ASCII.</summary>
    public const int OffsetCheatText = 0x3344;

    public const int CheatTextLength = 32;

    /// <summary>Plugin -&gt; agent. The serial the plugin has finished; equal to
    /// <see cref="OffsetCheatSerial"/> means the answer below is for the current request.</summary>
    public const int OffsetCheatApplied = 0x3364;

    /// <summary>Plugin -&gt; agent. One of the <c>CheatStatus*</c> constants.</summary>
    public const int OffsetCheatStatus = 0x3368;

    /// <summary>Plugin -&gt; agent. What actually happened, in words, null-terminated ASCII.
    /// Occupies 0x336C..0x33AC, inside the region.</summary>
    public const int OffsetCheatDetail = 0x336C;

    public const int CheatDetailLength = 64;

    // The four identity fields `setitem` can poke that have nowhere else to ride. Stack size
    // reuses OffsetCheatAmount (0 = leave it alone), because a stack of zero is not a thing
    // the engine produces; these four have no such spare value, so each carries its own
    // "leave alone" sentinel instead of overloading a legal one.

    /// <summary>Agent -&gt; plugin. New charges for <see cref="CheatKindSetItem"/>, or
    /// <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatCharges = 0x33B0;

    /// <summary>Agent -&gt; plugin. New max charges, or <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatMaxCharges = 0x33B4;

    /// <summary>Agent -&gt; plugin. New installed-upgrade MASK, or
    /// <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatUpgrades = 0x33B8;

    /// <summary>Agent -&gt; plugin. New identity flag bits (the <c>ItemIdentity*</c> wire
    /// flags, not the engine's own bit numbers), or <see cref="CheatFieldUnchanged"/>.</summary>
    public const int OffsetCheatItemFlags = 0x33BC;

    /// <summary>"Do not touch this field." Chosen as all-ones because every one of the four
    /// is a value the engine keeps far below it — charges and max charges are persisted as
    /// GFF bytes, and the upgrade mask indexes upgrade SLOTS, of which there are single
    /// digits. A caller that really did mean 0xFFFFFFFF upgrades is asking for every one of
    /// 32 slots at once, which no item has.</summary>
    public const uint CheatFieldUnchanged = 0xFFFF_FFFF;

    /// <summary>
    /// <see cref="OffsetCheatTarget"/> sentinel meaning "the LOCAL player, not a materialised
    /// stand-in" for <see cref="CheatKindGiveLoot"/> — the dialogue item-grant channel's give
    /// (docs/research/dialogue-consequences.md §3.4) targets the speaker directly rather than
    /// a corpse's stand-in, so it must never be resolved through
    /// <c>MaterialisedEnemies.ToLocal</c>. Distinct from <see cref="CheatFieldUnchanged"/>
    /// (0xFFFF_FFFF) so the two "not a real value" sentinels can never be confused with each
    /// other on a field that could plausibly carry either. Equal by VALUE (not by reference —
    /// <c>KotorCoop.Session</c> cannot reference this NativeAOT-only project) to
    /// <c>CoopAgent.LocalPlayerObjectId</c>, which is itself the engine's own verified player
    /// id (docs/re/object-model.md: "Player object id is exactly 0x7FFFFFFF") — chosen
    /// deliberately rather than an arbitrary sentinel, so the "local player" target never needs
    /// translation at all: it already names the same creature on every instance.
    /// </summary>
    public const uint CheatTargetLocalPlayer = 0x7FFFFFFF;

    /// <summary>Toggle the local player's min-1-hit-point pin. Amount 1 on, 0 off, -1 report.</summary>
    public const uint CheatKindGod = 1;

    /// <summary>Set the local player's hit points (amount &lt;= 0 means "to maximum").</summary>
    public const uint CheatKindTopUp = 2;

    /// <summary>Stand the local player back up out of the downed state.</summary>
    public const uint CheatKindStandUp = 3;

    /// <summary>Kill a creature, through whichever authority path this instance owns.</summary>
    public const uint CheatKindSlay = 4;

    /// <summary>Snap the local player to (x, y, z) inside the current area.</summary>
    public const uint CheatKindTeleport = 5;

    /// <summary>Put a blueprint on the floor at the local player's feet.</summary>
    public const uint CheatKindDropItem = 6;

    /// <summary>
    /// Stage a cinematic onto a module transition — the one verb here that is a TEST
    /// INSTRUMENT rather than a convenience, because the thing it arranges (a movie playing
    /// across a transition) is the window in which this plugin is not running at all.
    /// <see cref="OffsetCheatAmount"/> carries the mode (<c>MovieStaging.Mode*</c>) and
    /// <see cref="OffsetCheatText"/> the comma-separated movie names.
    ///
    /// A NEW KIND NEEDS NO VERSION BUMP: the fields it uses all exist, and a plugin older
    /// than this answers <c>unknown cheat kind 7</c> — which is the correct degradation, out
    /// loud, rather than a silent no-op.
    /// </summary>
    public const uint CheatKindStageMovie = 7;
    /// Read — and optionally poke — one live item's per-instance identity, through the very
    /// same <c>Odyssey.ApplyItemIdentity</c> the ground-item channel uses on a peer's dropped
    /// item.
    ///
    /// This is a staging cheat and says so, but it is also the only way the apply path can be
    /// exercised without a human: KOTOR has no drop UI and no workbench a script can reach, so
    /// there was no way to produce an item carrying a stack of twenty or an upgrade mask —
    /// which meant the whole feature's live check could never start (docs/live-verification.md,
    /// 2026-08-15). Staging one here makes the sender's item genuinely non-stock, and the
    /// answer reports the fields READ BACK out of the item, so it is a test of the writer
    /// rather than a test of our intentions.
    /// </summary>
    public const uint CheatKindSetItem = 8;
    /// Report, ON THE GAME THREAD, everything this engine knows about one object id.
    ///
    /// A diagnostic rather than a cheat, and it exists because two lookups that are supposed
    /// to be the same lookup disagreed: the external agent's transcription of
    /// CGameObjectArray::GetGameObject said an id was held, and the plugin's in-process
    /// ResolveServerObject said it was not — same process, same id. Nothing outside the game
    /// can settle that, because every outside view is a different moment. This asks both
    /// questions in one frame and writes the whole chain to the plugin log.
    ///
    /// ⚠ 9, NOT 7. Kinds 7 and 8 belong to work developed in parallel (StageMovie, SetItem)
    /// and were already merged; this one was renumbered by hand at integration, in two files,
    /// which is exactly the manual step the "keep in sync" warning above says is dangerous.
    /// Take the next free number rather than the next number you see, and leave 7 and 8 alone
    /// even if they look absent from the branch you are on.
    /// </summary>
    public const uint CheatKindResolve = 9;

    /// <summary>
    /// Make the local player USE a door — the second half of the cinematic instrument, and
    /// the only one of the two that can test the pre-store flush hypothesis.
    ///
    /// <c>moviewarp</c> stages a movie onto a transition the PLUGIN starts, and on that path
    /// the plugin has already flushed every spawned body before it raises anything, so the
    /// flush window is never needed. A door transition is the opposite: the ENGINE raises
    /// <c>CSWSArea::trans_pending</c> and gives us half a second of fade to destroy those
    /// bodies in — and a movie playing over that half second is exactly the case nobody has
    /// ever been able to stage. <see cref="OffsetCheatText"/> carries the door's tag, or
    /// <see cref="OffsetCheatTarget"/> its object id; both empty means "the nearest module
    /// transition door". See docs/research/cinematics.md §8.
    ///
    /// ⚠ 10, because 7, 8 and 9 are taken. Take the next FREE number, not the next number you
    /// see on your branch — three parallel agents already collided on 7 once.
    /// </summary>
    public const uint CheatKindUseDoor = 10;

    /// <summary>
    /// Gives a materialised enemy's local stand-in one item out of its real corpse's actual
    /// inventory — the fix for the loot gap docs/research/enemy-materialisation.md §5 names
    /// ("its container id exists only locally"). <see cref="OffsetCheatTarget"/> is the
    /// OWNER's enemy object id, not a local one: only this instance's own
    /// <c>MaterialisedEnemies.ToLocal</c> can translate it, so the caller (an external agent,
    /// or eventually the network path) cannot resolve it itself and must not try. The one
    /// exception is <see cref="CheatTargetLocalPlayer"/>, which names THIS instance's own
    /// player directly — the dialogue item-grant channel's reuse of this same give path
    /// (docs/research/dialogue-consequences.md §3.4).
    /// <see cref="OffsetCheatText"/> carries the item resref, and
    /// Amount/Charges/MaxCharges/Upgrades/ItemFlags its identity — exactly the same four
    /// fields <see cref="CheatKindSetItem"/> already carries for an existing item.
    ///
    /// A stand-in this instance does not have for that owner id is REFUSED, not a silent
    /// no-op: the common case is a peer holding the REAL creature (same GIT id), which
    /// already rolled its own equivalent loot independently when it died, and applying this
    /// there too would double it.
    ///
    /// Staged manually for now (this is the instrument, not the automatic wire): nothing yet
    /// detects a materialised enemy's owner dying and issues this on its own. See
    /// docs/research/enemy-materialisation.md §5 for what that still needs.
    ///
    /// ⚠ 11, because 7-10 are taken. NO VERSION BUMP: every field it uses already exists.
    /// </summary>
    public const uint CheatKindGiveLoot = 11;

    /// <summary>
    /// STRATEGY 2 DIAGNOSTIC (2026-08-21): peeks the creature's +0x260 script slot (reads the
    /// raw resref, does NOT fire it) when <see cref="SharedBlock.CheatAmount"/> is 0, or fires
    /// it through the engine's own RunScript dispatcher (see Odyssey.cs's block comment on
    /// FireCreatureScriptSlot0x260) when nonzero — a single verb for both the safe read and
    /// the live test, so nobody fires blind into an unverified field. Uses
    /// <see cref="SharedBlock.CheatTarget"/> for the creature id; no new fields, no version
    /// bump.
    /// </summary>
    public const uint CheatKindFireScript = 12;

    /// <summary>
    /// ROUND 36 TEMPORARY DIAGNOSTIC (2026-08-21): snaps <see cref="SharedBlock.CheatTarget"/>
    /// to a hardcoded position near END_M01AA's `sithsoldier 0xE` (29.74, 144.46), so a
    /// genuinely non-census creature (a party member, never touched by
    /// EnemyPresenceController at all) can have a valid, in-range hostile to test
    /// <c>+0x260</c> firing against — the census-membership question needs a real target, not
    /// just "no target found." Uses <c>CheatTarget</c> for the creature id; no new fields, no
    /// version bump. See docs/research/shared-state-audit.md §2, round 36. Remove once this
    /// question is settled.
    /// </summary>
    public const uint CheatKindMoveNear = 13;

    /// <summary>
    /// STAGING VERB (2026-08-22): run one of the peer-effect channel's non-damage constructors
    /// directly, through the SAME <c>OdysseyEffects.ApplyEffect</c> path a peer's forwarded
    /// effect calls on arrival — so this doubles as a live constructor test AND, aimed at a
    /// peer's phantom (the default when <see cref="OffsetCheatTarget"/> is 0), as the trigger
    /// for docs/research/effect-sync.md's live list, which had never run because no reachable
    /// test module has a Force-using hostile and nothing before this could apply a non-damage
    /// effect on demand at all. <see cref="OffsetCheatAmount"/> carries the effect kind (the
    /// <c>PeerEffectKindValues</c>/<c>PeerEffectKind</c> numeric identity, 1..9);
    /// <see cref="OffsetCheatCharges"/> and <see cref="OffsetCheatMaxCharges"/> are reused for
    /// duration (ms) and magnitude — no new fields, no version bump, same discipline
    /// <see cref="CheatKindFireScript"/> and <see cref="CheatKindMoveNear"/> already established
    /// for a kind that fits inside the existing slot.
    ///
    /// STAGING ONLY, LOCAL APPLY: this never crosses the network by itself. What
    /// <c>PhantomEffectHook</c> does with it after it lands on a peer's phantom — capture,
    /// publish to the OUT ring, and the ordinary channel forwarding it for real — is the actual
    /// test; see docs/research/console-cheats.md's co-op rule for this whole verb family.
    ///
    /// ⚠ 14, because 7-13 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindApplyEffect = 14;

    /// <summary>
    /// TEST HOOK (2026-08-22): plays <see cref="OffsetCheatText"/> (a bare resref, e.g.
    /// <c>01c</c> or <c>biologo</c>) RIGHT NOW on the local instance, through
    /// <c>OdysseyMovies.PlayMovieNow</c> — the exact engine entry (<c>CClientExoApp::PlayMovie
    /// 0x005EDB30</c>) both a script's mid-module <c>PlayMovie</c> AND
    /// <see cref="MidModuleMovieController"/>'s own <c>PlayNow</c> path call, and the one
    /// <c>MoviePlayHook</c> watches. Exists because docs/research/cinematics.md §10.7 named no
    /// isolated way to fire that hook on the host without waiting for a story trigger
    /// (`moviewarp` only ever writes the TRANSITION queue, a different path entirely). Follows
    /// <see cref="CheatKindApplyEffect"/>'s pattern exactly: no new fields, the existing 32-byte
    /// <see cref="OffsetCheatText"/> slot, gated by the SAME <c>MovieStaging.DecidePlayNow</c>
    /// check the relay's own receiver uses before calling the same function, so a refusal here
    /// reads identically to a refusal there.
    ///
    /// ⚠⚠ LIVE-VERIFIED HANG (2026-08-22): calling this with `biologo` AND separately with `0a`
    /// — two different real `.bik` files, both present on disk — both hung the main thread
    /// INDEFINITELY (4+ minutes and 60+ seconds respectively, ~100% single-core CPU the whole
    /// time, never returned). Because <see cref="MidModuleMovieController"/>'s own `PlayNow`
    /// calls the IDENTICAL underlying function, `movie_relay` stays OFF pending a root cause —
    /// see docs/research/cinematics.md §10.8 for the full repro and the reentrancy/environment
    /// hypotheses. This verb still ships (its capture-side plumbing is not implicated), but
    /// expect it to hang the game rather than answer quickly.
    ///
    /// ⚠ 15, because 7-14 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindMoviePlay = 15;

    /// <summary>
    /// STAGING VERB (2026-08-22): destroys a creature outright through
    /// <c>Odyssey.DestroyObjectById</c> — the SAME engine call
    /// <c>LivenessReconcileController</c>'s own Destroy action already wraps
    /// (docs/research/creature-liveness.md §5). Built because that channel's live passes
    /// (2026-08-22, both END_M01AA and tat_m20aa) found no console verb that could force a
    /// creature into the "Gone" state — <c>slay</c> only ever calls
    /// <c>Odyssey.KillCreature</c> — leaving the Destroy dispatch, the mechanism the whole
    /// channel exists to ship, completely unexercised. <see cref="OffsetCheatTarget"/> is the
    /// object id; no tag form and no "nearest" fallback, on purpose — this stages a SPECIFIC
    /// id a `census` read already named as out-of-census.
    ///
    /// Follows <see cref="CheatKindApplyEffect"/>'s wiring exactly and <see cref="CheatKindSlay"/>'s
    /// authority rules exactly (refuses a player, a phantom, a companion, a plot creature — see
    /// <c>CheatAuthority.ForDestroy</c>) but is HOST-ONLY where slay forwards: a mirroring
    /// instance (this engine does not own the fight) is refused outright rather than forwarded,
    /// because there is no destroy-ledger to spend through — carrying a destroy TO a mirroring
    /// instance is the creature-liveness channel's own job, not this verb's.
    ///
    /// ⚠ 16, because 7-15 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindDestroyObject = 16;

    /// <summary>
    /// ROUND 57 STAGING VERB (2026-08-22): opens an ORDINARY door — one <c>usedoor</c> refuses
    /// by design, since it must be incapable of doing anything except starting a module
    /// transition (see <see cref="CheatKindUseDoor"/>'s own doc comment). Built because
    /// docs/research/shared-state-audit.md round 56 found the non-transition case genuinely
    /// untested: no working `walk`/click primitive in the rig could reach one, and `usedoor`
    /// refuses every door `end_m01aa`'s GIT names that is not linked to a module.
    ///
    /// Calls the SAME <c>Odyssey.OpenDoorFully</c> the transition path already uses — the
    /// engine's own door-use action, not a poke of the open-state byte — so the ordinary sync
    /// path (<c>DoorController</c>'s generic open-state mirror) is what actually gets
    /// exercised. <see cref="OffsetCheatTarget"/> is the door's object id, <see cref="OffsetCheatText"/>
    /// its tag; both empty means "the nearest door" (no linked-only filter — that filter is
    /// `usedoor`'s guard, not a fact about doors in general). Never walks the player through
    /// it: an ordinary door has nowhere to transition to.
    ///
    /// ⚠ 17, because 7-16 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindOpenDoor = 17;

    /// <summary>
    /// CONTAINER-SYNC STAGING VERB (2026-08-22): makes the local player USE a placeable through
    /// <c>Odyssey.UseObject</c> — the engine's own client-to-server "I clicked that" input pipe,
    /// so the real <c>AIActionUseObject</c>/<c>CSWSPlaceable::EventHandler</c>/<c>OpenInventory</c>
    /// path runs exactly as a real click would (docs/research/container-sync.md), unlike
    /// <see cref="CheatKindOpenDoor"/>'s door-primitive-based dance: a container needs no walk,
    /// no walkmesh coupling, just the use action. <see cref="OffsetCheatTarget"/> is the
    /// placeable's object id, <see cref="OffsetCheatText"/> its tag; both empty means "the
    /// nearest CONTAINER placeable" (has_inventory set — a switch/lever is skipped).
    ///
    /// This is the ONLY caller-side instrument the live-test script needs: the container-use
    /// GATE and the open/close mirror both live entirely inside the normal per-frame plugin
    /// machinery (<c>ContainerUseHook</c>, <c>InteractionLockController</c>) and need no staging
    /// verb of their own — they react to whatever a real click OR this verb produces.
    ///
    /// ⚠ 18, because 7-17 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindUsePlaceable = 18;

    /// <summary>
    /// CONTAINER-SYNC LIVE-PASS ADDITION (2026-08-22): the close-side counterpart to
    /// <see cref="CheatKindUsePlaceable"/>, added when the live-verify pass found no way to
    /// close a container's loot panel unattended — <c>useplaceable</c>'s own
    /// <c>Odyssey.UseObject</c> send only replicates the network HALF of a real click
    /// (<c>CSWCMessage::SendPlayerToServerInput_UseObject</c>); the client-side menu code that
    /// actually shows the GUI panel (<c>CSWCPlaceable::UsePlaceable</c>'s own wrapper, see
    /// Odyssey.cs's remarks on <c>SendUseObjectInputFn</c>) is deliberately NOT replicated, so
    /// there is no panel for an Escape keystroke to close and no click for a real player to
    /// send. This verb calls <c>CSWSPlaceable::CloseInventory 0x00587560</c> directly (VERIFIED
    /// full disasm, docs/research/container-sync.md's live-pass section) with arg2 == 0 (skips
    /// the item-transfer bookkeeping — nothing to give the player through a programmatic close)
    /// and arg1 == the closing player's own object id, NOT 0: a first live attempt at arg1 == 0
    /// replied but never actually cleared <c>+0x338</c> — arg2 == 0 only skips the player-
    /// RESOLVE step, not the unconditional tail every path (including a real close) falls into,
    /// which pushes arg1 RAW into a second message dispatch before the close animation and the
    /// field clear. Fresh disasm of the real call site (<c>AIActionUseObject</c>'s open call,
    /// <c>tools/disasm.py 0x57EE80 0x50</c>) shows a real click passes the acting creature's own
    /// object id here, and using that live-confirmed the field clear.
    /// <see cref="OffsetCheatTarget"/> is the placeable's object id, <see cref="OffsetCheatText"/>
    /// its tag; both empty means "whatever <c>useplaceable</c> bare would have found", i.e. the
    /// nearest container.
    ///
    /// ⚠ 19, because 7-18 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindClosePlaceable = 19;

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (2026-08-22, rewired Round 70 2026-08-22): switches the LOCAL
    /// player's controlled character through <c>Odyssey.SwitchControlToPartySlot</c>/
    /// <c>SwitchControlToPc</c> — as of Round 70 (docs/research/control-return.md) these call
    /// the ENGINE'S OWN <c>ChangeCharacter</c> funnel (0x005F6B60), the exact function a real
    /// Tab press and portrait click reach, in place of the raw field poke earlier rounds used.
    /// Built because docs/research/control-return.md's live pass needed a way to put a client
    /// onto a party-roster companion BEFORE connecting — the one manual step
    /// <c>control-return-live.ps1</c> could not automate (injected keystrokes never reach this
    /// game). <see cref="OffsetCheatAmount"/> is the party-roster slot (0-8), or -1 for "your
    /// own PC". Requires the target to already be a live CSWParty member (in the active party,
    /// not merely recruited). Moves the camera too, as a side effect of the real engine call —
    /// no separate follow-up needed, though calling
    /// <c>SetCameraFollowServerObject</c>/<c>RestoreCameraToControlledCreature</c> afterward is
    /// harmless.
    ///
    /// ⚠ 20, because 7-19 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindLeader = 20;

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE (2026-08-23, round 75 of docs/research/party-reconciliation.md):
    /// forces NPC roster slot <see cref="OffsetCheatAmount"/> (0-8) into the LOCAL active party
    /// without a real recruitment conversation — <c>Odyssey.TrySetSlotAvailable</c> (poke the
    /// availability flag if the slot is not already recruited), then
    /// <c>Odyssey.SpawnAvailableNpc</c> (if the slot has no live body) at the player's own
    /// position, then <c>Odyssey.AddPartyMemberBySlot</c> — the exact three-call sequence
    /// <c>PartyRosterController.Reconcile</c> already runs for a peer's roster, run here on
    /// demand for a slot this instance names directly. Built because the natural in-story path
    /// to a second companion (a full dialogue tree, GUI-only, no console equivalent) could not
    /// be driven from an unattended test, and party-reconciliation.md's round 75 needed a real
    /// two-companion party to test whether solo-companion following is a narrow edge case or a
    /// general defect. This closes <c>TrySetSlotAvailable</c>'s own long-standing NEEDS-LIVE
    /// note ("whether the flag ALONE is enough for SpawnAvailableNpc to resolve a template
    /// afterward") the same pass that uses it.
    ///
    /// Never crosses the network — local roster state only, same as <see cref="CheatKindLeader"/>.
    /// Refuses (does not silently reuse) a slot already occupied by a live, in-party companion.
    /// ⚠ 21, because 7-20 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindRecruit = 21;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-23, docs/research/dialogue-skip-desync.md): forces the
    /// local player's OWNED conversation's current line/VO to finish immediately, the same
    /// way a human's retail skip keypress/click does — in-process, no OS input involved. See
    /// <c>Odyssey.SkipDialogueLine</c> for the VERIFIED mechanism (a flag on the dialogue
    /// owner's <c>CSWSObject</c>, at the same offset the engine's own
    /// <c>CServerExoApp::SkipPastDialog</c> writes, consumed once by
    /// <c>CSWSObject::IsDialogDelay</c>). Built because two prior rounds established that
    /// injected OS input never reaches this game at all — the same wall every other console
    /// verb in this file was built to route around. Meaningful only as the conversation OWNER;
    /// a no-op (not a refusal) when there is no active conversation to skip.
    ///
    /// ⚠ 22, because 7-21 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindSkipLine = 22;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-23, docs/research/dialogue-skip-desync.md): commits reply
    /// slot <see cref="OffsetCheatAmount"/> on the local player's OWNED conversation, through
    /// <c>Odyssey.SelectDialogueReply</c> — a fully verified, self-contained call
    /// (<c>CSWGuiDialog::SelectReply</c>, through the panel's own vtable slot) that already
    /// existed, unused, from an earlier "replay the owner's pick" design dialogue-sync.md's
    /// node-sync section replaced as the DRIVE mechanism (a reply slot cannot converge across
    /// two independently-evaluated trees). Reusing it LOCALLY, on your own conversation, has
    /// none of that hazard — it is exactly what a real click on a reply does. Built because
    /// this round's skip live-test needs a way to navigate PAST a reply-choice screen into a
    /// narration stretch without real OS input (skip itself is meaningless on a reply screen —
    /// see <c>CheatKindSkipLine</c>'s own remarks on the panel's +0x1DF8 flag).
    ///
    /// ⚠ 23, because 7-22 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindSelectReply = 23;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-23, docs/research/minigames.md round): queues a real
    /// <c>ActionMoveToLocation</c> for the local player through <c>Odyssey.MoveToLocation</c> —
    /// the same call <see cref="CheatKindUseDoor"/>'s walk already uses, but to a bare point
    /// (<see cref="OffsetCheatX"/>/<see cref="OffsetCheatY"/>/<see cref="OffsetCheatZ"/>) rather
    /// than a door threshold. Built because a subarea trigger's <c>OnEnter</c> only fires for a
    /// MOVING creature (docs/research/trigger-relay.md §2) — <see cref="CheatKindTeleport"/>'s
    /// snap crosses nothing — and there was no walk primitive that did not require a door.
    ///
    /// ⚠ 24, because 7-23 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindWalkTo = 24;

    /// <summary>The request was carried out here.</summary>
    public const int CheatStatusApplied = 1;

    /// <summary>The request was handed to the owning peer rather than applied locally —
    /// the honest answer for a client slaying an enemy it does not own.</summary>
    public const int CheatStatusForwarded = 2;

    /// <summary>No world (main menu), or no local player creature yet.</summary>
    public const int CheatStatusNoWorld = -1;

    /// <summary>The target named by the request does not exist in this instance.</summary>
    public const int CheatStatusNoTarget = -2;

    /// <summary>The co-op authority rules refuse it; the detail says which rule.</summary>
    public const int CheatStatusRefused = -3;

    /// <summary>The engine refused the call; the detail carries its failure code.</summary>
    public const int CheatStatusEngineRefused = -4;

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

    // ---- transition cinematics (version 42, region 0x3800..0x3A00) ----------------
    //
    // Both players should see the same cinematic. Measured on 2026-08-15: the owner's
    // transition stalled its main thread for 7,407 ms playing `01c` while the follower logged
    // no `cinematic:` line at all and simply loaded in. The follower is not GATED — its
    // move_to_module_movies queue is empty, and the engine plays what is in the queue.
    //
    // Two directions, because the two halves of the fix are different facts:
    //
    //   plugin -> agent   "the transition I am about to make plays these movies". The one
    //                     frame of warning the engine gives: NWScript StartNewModule fills
    //                     CClientExoAppInternal+0x2D8 and the module load drains it, and
    //                     CinematicWatch is reading that list on the frame the script writes
    //                     it. Published as a level plus a serial, not an event — a follower
    //                     that misses a tick must not miss the cinematic.
    //
    //   agent -> plugin   "queue these before the pending travel". Consumed by
    //                     TravelController immediately before Odyssey.TravelToModule, which
    //                     is the same path run A proved plays a movie for real (15,531 ms).
    //
    // ⚠ THE NAMES ARE STRIPPED TO RESREFS, and that is load-bearing rather than tidy. The
    // engine does not store what the script passed: AddMovieToExoArrayList resolves the name
    // to a path and stores THAT, so the queue reads `.\movies\01c.bik`. Feeding that back to
    // AddMoveToModuleMovie fails — the string contains no ':', so the no-colon branch builds
    // `MOVIES:.\movies\01c.bik`, cannot open it, and refuses the name. Forwarding the raw
    // stored string would degrade the whole feature to "the engine refused every movie",
    // silently, on every transition. See MovieStaging.StripToResRef.

    /// <summary>Plugin -&gt; agent. Bumped when this instance observes its own transition queue
    /// fill. Written LAST, after the names, so a half-written list is never visible.</summary>
    public const int OffsetCinematicSerial = 0x3800;

    /// <summary>Plugin -&gt; agent. How many of <see cref="OffsetCinematicNames"/> are valid.</summary>
    public const int OffsetCinematicCount = 0x3804;

    /// <summary>Plugin -&gt; agent. <see cref="CinematicCapacity"/> movie resrefs, each
    /// <see cref="CinematicNameLength"/> bytes of null-padded ASCII. Occupies
    /// 0x3808..0x3868.</summary>
    public const int OffsetCinematicNames = 0x3808;

    /// <summary>Agent -&gt; plugin. How many movies the pending travel request should queue.
    /// Written BEFORE <see cref="OffsetTravelSerial"/>, like every other travel field.</summary>
    public const int OffsetTravelMovieCount = 0x3880;

    /// <summary>Agent -&gt; plugin. The movie resrefs for the pending travel, same layout as
    /// <see cref="OffsetCinematicNames"/>. Occupies 0x3884..0x38E4.</summary>
    public const int OffsetTravelMovieNames = 0x3884;

    /// <summary>Engine resrefs are 16 characters; the names here are already stripped to
    /// resrefs, so this is the exact width rather than a guess.</summary>
    public const int CinematicNameLength = 16;

    /// <summary>The engine's own ceiling: NWScript <c>StartNewModule</c> takes exactly six
    /// movie arguments, so six is the longest queue the game can ever build.</summary>
    public const int CinematicCapacity = 6;

    // ---- transition announce (version 47, region 0x38E4..0x3928) ------------------
    //
    // The faster, earlier sibling of the cinematic channel above: not the film, but where a
    // follower should go, resolved the instant this instance's own engine has COMMITTED to a
    // transition — the rising edge of Odyssey.IsAreaTransitionPending() for a real door, or
    // the frame immediately before TravelController calls Odyssey.TravelToModule for a
    // plugin-initiated warp. Both are frames on which the destination module and entry
    // waypoint are already known, and the movie queue is not always filled (a plain door with
    // no cinematic queues nothing at all), so this cannot ride the cinematic channel's own
    // serial. See docs/research/transition-smoothness.md.
    //
    // Placed in the gap TravelMovieNames leaves before the peer-effect region (0x38E4..0x3A00,
    // 0x11C bytes; the version-44 comment above already establishes it as free and too small
    // for an 8-entry ring in each direction — this uses 0x44 of it, leaving 0xB0 still free).
    //
    // OUT (plugin -> agent): the destination this instance's own transition is heading to.
    // IN (agent -> plugin): whether the client-door-refusal watch is armed right now.

    /// <summary>Plugin -&gt; agent. Bumped when this instance's engine commits to a
    /// transition's destination. Written LAST, after the module and waypoint, so a
    /// half-written destination is never visible.</summary>
    public const int OffsetTransitionAnnounceSerial = 0x38E4;

    /// <summary>Plugin -&gt; agent. Null-terminated destination module name, 32 bytes.
    /// Occupies 0x38E8..0x3908.</summary>
    public const int OffsetTransitionAnnounceModule = 0x38E8;

    /// <summary>Plugin -&gt; agent. Null-terminated entry waypoint tag, 32 bytes (empty when
    /// the source resolved none). Occupies 0x3908..0x3928.</summary>
    public const int OffsetTransitionAnnounceWaypoint = 0x3908;

    /// <summary>Agent -&gt; plugin. Nonzero arms the refusal of a CLIENT-initiated real door
    /// transition (coop.ini <c>client_door_refusal</c>). A LEVEL, re-sent every agent tick —
    /// the plugin has no notion of "host" or "client" on its own, so it trusts whatever this
    /// says right now.</summary>
    public const int OffsetClientDoorRefusalArmed = 0x3928;

    // ---- mid-module movie relay (version 48, region 0x392C..0x396C) ---------------
    //
    // A script's own PlayMovie call with NO transition involved (the Leviathan capture, the
    // Star Forge arrival, the Taris bombardment, the Dantooine vision) — the DETECTION half
    // docs/research/trigger-relay.md §6 shipped MovieRelayQueue (the pure decision) without:
    // "nothing yet captures WHICH movie a mid-module PlayMovie call played". MoviePlayHook is
    // that capture, on CClientExoAppInternal::PlayMovie 0x00602CF0 — a DIFFERENT engine
    // function from the transition queue's own CClientExoAppInternal+0x2D8 the cinematic
    // region above already reads, so this needs its own record rather than reusing that one.
    // See docs/research/cinematics.md's 2026-08-22 section.
    //
    // Placed in the free gap OffsetClientDoorRefusalArmed leaves before the peer-effect region
    // (0x392C..0x3A00, 0xD4 bytes — the version-47 comment already establishes this gap is
    // real). This region uses 0x40 of it (0x392C..0x396C); version 49 below takes 4 more
    // (0x396C..0x3970), leaving 0x90 bytes still free. No TotalSize growth, no relayout of
    // anything that already exists.
    //
    // OUT (plugin -> agent, 0x392C..0x394C, 0x20 bytes): the resref this instance's own
    // MoviePlayHook most recently captured, already excluded against the engine's own
    // transition queue.
    //
    // IN (agent -> plugin, 0x394C..0x396C): a peer's mid-module movie, relayed by the session,
    // for THIS instance to act on — play now, or defer to this instance's own next transition
    // — per MovieRelayQueue.Decide.

    /// <summary>Plugin -&gt; agent. Bumped when <c>MoviePlayHook</c> captures a new mid-module
    /// PlayMovie call. Written LAST, after the resref, so a half-written name is never
    /// visible.</summary>
    public const int OffsetMidModuleMovieSerial = 0x392C;

    /// <summary>Plugin -&gt; agent. Null-terminated resref, 16 bytes. Occupies
    /// 0x3930..0x3940. 12 bytes (0x3940..0x394C) reserved.</summary>
    public const int OffsetMidModuleMovieResRef = 0x3930;

    public const int MidModuleMovieNameLength = 16;

    /// <summary>Agent -&gt; plugin. Bumped when a peer's mid-module movie is ready to act on.
    /// A request is pending while this is ahead of <see cref="OffsetMidModulePlayApplied"/>.
    /// </summary>
    public const int OffsetMidModulePlayRequestSerial = 0x394C;

    /// <summary>Agent -&gt; plugin. Null-terminated resref, 16 bytes. Occupies
    /// 0x3950..0x3960.</summary>
    public const int OffsetMidModulePlayRequestResRef = 0x3950;

    /// <summary>Plugin -&gt; agent. Echoes <see cref="OffsetMidModulePlayRequestSerial"/> once
    /// this instance has decided what to do with the request (whatever it decided — this is
    /// "handled", not "played").</summary>
    public const int OffsetMidModulePlayApplied = 0x3960;

    /// <summary>Plugin -&gt; agent. The verdict for the log — see
    /// <c>MidModulePlayStatusCode</c>. 0 = nothing handled yet. Region ends here, at 0x3968;
    /// 0x3968..0x396C reserved.</summary>
    public const int OffsetMidModulePlayStatus = 0x3964;

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

    // ---- peer effect sync (version 44, region 0x3A00..0x3B88) ---------------------
    //
    // Non-damage effects that land on a peer's phantom (stun, paralyze, knockdown, slow,
    // frightened, sleep, choke, Force push/whirlwind), mirrored to that peer's real character
    // — the gap PhantomHitHook's plain-damage channel deliberately does not cover. See
    // docs/research/effect-sync.md.
    //
    // Two independent rings, not a level: unlike phantom damage (a cumulative running total,
    // where "what changed since last time" is the only question that matters), an effect
    // application is a distinct EVENT with its own duration — two Stuns two seconds apart are
    // two things that happened, not one bigger stun. Each slot carries its own serial, written
    // last, exactly like the bark rings.
    //
    // OUT (plugin -> agent): what PhantomEffectHook captured landing on one of THIS instance's
    // phantoms, for the agent to address to the peer that phantom stands for.
    //
    // IN (agent -> plugin): an effect a peer's hook captured landing on THIS player's phantom
    // over there, for the plugin to replay on the real character here through OdysseyEffects.

    /// <summary>Plugin -&gt; agent. Bumped whenever the hook captures something new. Diagnostic
    /// only — the agent tells "new" from "already sent" by each slot's own serial, the same as
    /// the bark rings.</summary>
    public const int OffsetPeerEffectSerial = 0x3A00;

    /// <summary>Plugin -&gt; agent. What landed on our phantoms, captured by
    /// <c>PhantomEffectHook</c>. Slot layout: phantomId u32 @0, kind u32 @4 (a
    /// <c>PeerEffectKind</c>), durationMs u32 @8, magnitude u32 @0xC, reserved u32 @0x10,
    /// serial u32 @0x14 (written last). Occupies 0x3A08..0x3AC8.</summary>
    public const int OffsetPeerEffectOutSlots = 0x3A08;

    /// <summary>Agent -&gt; plugin. An effect a peer captured landing on THIS player's phantom,
    /// for the plugin to replay here. Same slot layout, +0x00 is the originating peer id
    /// instead of a phantom id. Occupies 0x3AC8..0x3B88.</summary>
    public const int OffsetPeerEffectInSlots = 0x3AC8;

    public const int PeerEffectStride = 0x18;

    public const int PeerEffectCapacity = 8;

    // Field offsets within a peer-effect slot (both directions share the shape).
    public const int PeerEffectId = 0x00;
    public const int PeerEffectKindOffset = 0x04;
    public const int PeerEffectDurationMsOffset = 0x08;
    public const int PeerEffectMagnitudeOffset = 0x0C;
    public const int PeerEffectSerialOffset = 0x14;

    // ---- creature liveness reconciliation (version 45, region 0x3C00..0x3F08) -----
    //
    // The host publishes every creature its engine currently knows about in the loaded area
    // — not just the 32-slot combat census above — so a mirroring peer can notice a body left
    // alive on one side and dead or destroyed on the other (beyond the census cap, killed by
    // script, killed while the peers were apart) and correct it. See
    // docs/research/creature-liveness.md. This region starts cleanly at the old TotalSize
    // (0x3C00), past the peer-effect region's own tail (0x3B88) — same "start at the old
    // boundary rather than hunt for a sliver" reasoning version 42 and 44 both used.
    //
    // Agent -> plugin only: the host never reconciles against itself, so only a mirroring
    // instance's agent ever writes this region (translated, from what it received over the
    // wire, into this process's own consumption).

    /// <summary>Agent -&gt; plugin. How many of <see cref="OffsetLivenessSlots"/> are valid
    /// this pump. Zeroed by the plugin on a module transition like the enemy regions.</summary>
    public const int OffsetLivenessCount = 0x3C00;

    /// <summary>Agent -&gt; plugin. id u32 @0 (written last), state u32 @4 (0 alive, 1 dead —
    /// corpse present, 2 gone — destroyed, matching <c>Protocol.CreatureLivenessState</c> by
    /// value; Protocol is not referenced from the plugin, see that type's own doc). Capacity
    /// <see cref="LivenessCapacity"/>; when the peer's accumulated set is larger the caller
    /// orders dead/gone entries first and rotates the alive tail across pumps — see
    /// docs/research/creature-liveness.md. Occupies 0x3C04..0x3F04.</summary>
    public const int OffsetLivenessSlots = 0x3C04;

    public const int LivenessSlotStride = 0x08;

    public const int LivenessCapacity = 96;

    public const uint LivenessStateAlive = 0;
    public const uint LivenessStateDead = 1;
    public const uint LivenessStateGone = 2;

    /// <summary>Plugin -&gt; agent. Cumulative count of "host says alive, but this instance's
    /// own engine already had it dead or gone" anomalies <c>LivenessReconcileController</c> has
    /// noticed since it started. Never reset by a module change; never acted on beyond
    /// counting — the right fix for a genuine anomaly is upstream, never a forced
    /// resurrection.</summary>
    public const int OffsetLivenessAnomalyCount = 0x3F04;

    // ---- container-lock target ids (version 50, region 0x3F08..0x3F1C, no TotalSize growth) --
    //
    // InteractionKind.Container (docs/research/container-sync.md) keys on a placeable OBJECT
    // ID rather than the tag InteractionKind.Conversation already carries at
    // OffsetLocalInteractionTag/OffsetInteractionLocks — the base game routinely places several
    // placeables with the SAME tag (every generic footlocker in a module, typically), so a
    // container claim needs the id instead. Rather than widen InteractionLockStride (0x28,
    // every one of whose four bytes is already spoken for by peerId/kind/tag) this rides in the
    // free tail after the creature-liveness region: OffsetLivenessAnomalyCount ends at 0x3F04 +
    // 4 = 0x3F08, and TotalSize is 0x4000, leaving 0xF8 bytes free — this region uses 20 of
    // them. No TotalSize growth, no relayout of anything else.

    /// <summary>Plugin -&gt; agent. The object id of the placeable the local player is claiming
    /// via a Container-kind interaction claim, or 0. Meaningless (and left 0) for any other
    /// kind, which uses <see cref="OffsetLocalInteractionTag"/> instead.</summary>
    public const int OffsetLocalInteractionTargetId = 0x3F08;

    /// <summary>Agent -&gt; plugin. Parallel array to <see cref="OffsetInteractionLocks"/>, SAME
    /// index correspondence, one u32 per slot: the placeable object id a peer's Container-kind
    /// lock names. 0 for a slot that is empty or names a non-Container kind. Occupies
    /// 0x3F0C..0x3F1C.</summary>
    public const int OffsetInteractionLockTargetIds = 0x3F0C;

    public const int InteractionLockTargetIdStride = 0x04;

    // ---- dialogue forced-conversation bit (version 51, region 0x3F1C..0x3F24, no TotalSize
    // growth) -------------------------------------------------------------------------------
    //
    // docs/research/dialogue-sync.md's 2026-08-23 Item 1: whether the LOCAL/REMOTE conversation
    // is one the vanilla engine itself forces regardless of distance (an ambush, a scripted
    // cutscene conversation — NWScript's ActionStartConversation with bIgnoreStartRange=TRUE),
    // as opposed to an ordinary player-clicked chat. DialogueAutoJoinRule uses this to
    // auto-join UNCONDITIONALLY, ignoring the configured mode and range, matching what vanilla
    // would already have forced onto a single-player's PC rather than inventing new behaviour.
    // Rides in the free tail after the container-lock-target-ids region (0x3F0C + 0x10 = 0x3F1C,
    // TotalSize is 0x4000, leaving 0xE4 bytes free before this; this region uses 8 of them). No
    // TotalSize growth, no relayout of anything else.

    /// <summary>Plugin -&gt; agent. Nonzero while the local player's own conversation is one
    /// the engine itself forced. Latched for the conversation's whole duration (set once, at
    /// the moment a new local conversation is detected — see
    /// <see cref="DialogueForceFlagHook"/>) and cleared alongside the partner tag when the
    /// conversation ends.</summary>
    public const int OffsetLocalDialogueForced = 0x3F1C;

    /// <summary>Agent -&gt; plugin. Nonzero while the PEER's conversation being followed is
    /// forced. Only meaningful alongside <see cref="OffsetRemoteDialogueActive"/> — read
    /// together with <see cref="OffsetRemoteDialoguePartnerTag"/>, which names WHICH
    /// conversation this bit describes (the session's single arbitrated token).</summary>
    public const int OffsetRemoteDialogueForced = 0x3F20;

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

    // ---- generic trigger relay (0x3B88..0x3BD8, no TotalSize growth) --------------
    //
    // A GENERIC trigger's OnEnter (a CSWSTrigger whose script starts a cutscene, dialogue or
    // scripted event — not a trap, not an area transition) already fires correctly on
    // whichever engine the real player is standing in; the gap is that firing there never
    // touches THIS instance's object-id counter, so a script that CreateObject/DestroyObject
    // (a plot actor, most commonly) drifts the two instances' ids apart for the rest of the
    // module. This region is the same shape as the encounter region above for the same
    // reason: a client REPORTS where it stands, and the host's engine re-runs the OnEnter
    // script against that peer's phantom. See docs/research/trigger-relay.md.
    //
    // Placed in the 0x78 bytes of genuine padding OffsetPeerEffectInSlots leaves before
    // TotalSize (0x3AC8 + 0x18*8 = 0x3B88, TotalSize = 0x3C00) — the tail-slack the version-44
    // comment above already measures and does not use. This region ends at 0x3BD8, so 40
    // bytes of that slack are still free after it. No TotalSize growth, no relayout of
    // anything that already existed: existing fields keep their offsets untouched.

    /// <summary>Agent -&gt; plugin. 1 on a peer that is not enemy-authority (mirrors
    /// <see cref="OffsetEncounterWatch"/>'s reasoning exactly): that instance's watch reports
    /// which generic triggers the local player stands in, for the host to fire on its
    /// phantom's behalf. Dead weight on the host, which fires its own.</summary>
    public const int OffsetTriggerWatch = 0x3B88;

    /// <summary>Plugin -&gt; agent. How many entries of <see cref="OffsetTriggerInside"/> are
    /// valid.</summary>
    public const int OffsetTriggerInsideCount = 0x3B8C;

    /// <summary>Plugin -&gt; agent. Bumped whenever the set changes.</summary>
    public const int OffsetTriggerInsideSerial = 0x3B90;

    /// <summary>Plugin -&gt; agent. Object ids of the generic triggers whose polygon contains
    /// the local player AND whose OnEnter script matched the coop.ini allowlist pattern (the
    /// plugin's watch excludes everything else before it ever reaches this array). Occupies
    /// 0x3B94..0x3BA4.</summary>
    public const int OffsetTriggerInside = 0x3B94;

    public const int TriggerInsideCapacity = 4;

    /// <summary>Agent -&gt; plugin. How many request slots are valid.</summary>
    public const int OffsetTriggerRequestCount = 0x3BA4;

    /// <summary>Agent -&gt; plugin. Bumped after the slots are written; the plugin only acts
    /// when this moves.</summary>
    public const int OffsetTriggerRequestSerial = 0x3BA8;

    /// <summary>Agent -&gt; plugin. trigger object id u32 @0, requesting peer id u32 @4.
    /// Occupies 0x3BAC..0x3BCC. Capacity 4, not the encounter region's 8: unlike an
    /// encounter fire, a trigger fire is NOT free to repeat (no engine one-shot flag), so
    /// TriggerRelayStore hands out only genuinely-new requests and 4 is already the wire
    /// packet's own per-tick cap (<see cref="ProtocolConstants.MaxTriggerRelayEntries"/> in
    /// KotorCoop.Protocol).</summary>
    public const int OffsetTriggerRequests = 0x3BAC;

    public const int TriggerRequestStride = 0x08;

    public const int TriggerRequestCapacity = 4;

    /// <summary>Plugin -&gt; agent. How many triggers this instance has actually fired on a
    /// peer's behalf, and how many requests it turned down, for the log.</summary>
    public const int OffsetTriggerFired = 0x3BCC;

    public const int OffsetTriggerRefused = 0x3BD0;

    /// <summary>Plugin -&gt; agent. Why the last refusal happened
    /// (<c>OdysseyTriggers.TriggerFireResult</c>'s status codes). 0 = nothing refused yet.
    /// Region ends here, at 0x3BD8.</summary>
    public const int OffsetTriggerStatus = 0x3BD4;

    // ---- generic trigger relay: native fires (protocol 36, region 0x3BD8..0x3BF0) ----
    //
    // The REVERSE direction docs/research/trigger-relay.md's symmetry pass closes: the region
    // above only ever carried a client's own WATCH report (currently standing in). This one
    // carries the SAME instance's own engine having NATIVELY fired a generic trigger for its
    // own real character — observed by TriggerFireHook's detour on
    // CSWSTrigger::EventHandler's generic-script RunScript call site, not by a per-frame
    // polygon test — so it fires exactly once per module per genuinely-new trigger id, already
    // allowlist- and eligibility-filtered by TriggerRelayController the same way the watch is.
    //
    // Placed in the 40 bytes of tail-slack OffsetTriggerStatus's own comment already measured
    // and did not use (0x3BD8..0x3C00). This region ends at 0x3BF0, so 16 bytes of that slack
    // are still free after it. No TotalSize growth.

    /// <summary>Plugin -&gt; agent. How many entries of <see cref="OffsetTriggerNativeFired"/>
    /// are valid.</summary>
    public const int OffsetTriggerNativeFiredCount = 0x3BD8;

    /// <summary>Plugin -&gt; agent. Bumped whenever the set changes.</summary>
    public const int OffsetTriggerNativeFiredSerial = 0x3BDC;

    /// <summary>Plugin -&gt; agent. Object ids of the generic triggers THIS instance's own
    /// engine has natively fired for its own real character, allowlist- and
    /// eligibility-filtered, capacity <see cref="TriggerNativeFiredCapacity"/>. Occupies
    /// 0x3BE0..0x3BF0. Region ends here, at 0x3BF0.</summary>
    public const int OffsetTriggerNativeFired = 0x3BE0;

    public const int TriggerNativeFiredCapacity = 4;

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
