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
    public const int TotalSize = 0x3800;

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

    // 0x1E5C..0x1E60 is spare inside this region.

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
