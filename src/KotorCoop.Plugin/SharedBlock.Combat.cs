namespace KotorCoop.Plugin;

// Enemy replication/presence/one-shot-capture, combat participation, damage-a-peer-dealt,
// client-triggered encounters, peer-effect sync, creature-liveness reconciliation, co-op
// death handling and one-shot animations. See SharedBlock.cs for the struct header.
internal partial struct SharedBlock
{
    // v83: one complete, sequenced per-peer damage publication. 8 peers x 128 enemies.
    /// <summary>Version 86: native damage process incarnation, stable across module resets.</summary>
    public const int OffsetDamageDealtIncarnation = 0xB980;

    public const int OffsetAttributedDamageSequence = 0x8700;
    public const int OffsetAttributedDamageEpoch = 0x8704;
    public const int OffsetAttributedDamageCount = 0x8708;
    public const int OffsetAttributedDamageWorldSerial = 0x870C;
    public const int OffsetAttributedDamageSlots = 0x8710;
    public const int AttributedDamageCapacity = SlotCapacity * DamageLedgerCapacity;
    public const int AttributedDamageStride = 12;

    // ---- enemy replication (version 3) -----------------------------------------
    //
    // The peer that owns enemies publishes their health; every other instance mirrors it
    // and is stopped from killing anything locally. Written as a plain array rather than
    // slots, because unlike items there is nothing to allocate: an enemy is identified by
    // its engine object id, which is the same number in both instances.

    /// <summary>Agent -&gt; plugin. How many enemy entries are valid.</summary>
    public const int OffsetEnemyCount = 0x488;

    /// <summary>
    /// Agent -&gt; plugin. objectId u32 @0, current u16 @4, max u16 @6, flags u32 @8.
    ///
    /// RELOCATED (version 80, "no cap really" — docs/research/creature-liveness.md): the old
    /// home, 0x490..0x690, had no room to grow (the next used field, <see cref="OffsetLocalDowned"/>,
    /// starts at 0x1020 but the ground-item/door/equipment regions in between were already
    /// spoken for) so the array moved into the new region at 0x6000 the same way every other
    /// enemy-count-scaled table in this file did this round. 0x490..0x690 is now DEAD — like the
    /// old bark rings' hole, documented rather than reused, so a stale reader finds zeros there
    /// rather than half a record.
    /// </summary>
    public const int OffsetEnemySlots = 0x6000;

    public const int EnemySlotStride = 0x10;

    /// <summary>
    /// Enemies one instance can publish (or mirror) at once. Raised from 32 to 128 (version 80,
    /// "no cap really" — docs/research/creature-liveness.md, docs/review-seamless-2026-09-18.md):
    /// a live GIT-file survey of all 117 shipped modules (tools/kotorfiles.py, 2026-09-24) found
    /// the largest static <c>Creature List</c> to be 51 (manm27aa, Manaan — Sith Base; the
    /// largest static+worst-case-encounter-spawn combined figure is also 51, since manm27aa has
    /// no encounters at all), so 128 is 2.5x headroom over the hardest real content this game
    /// ships. A live population that somehow still exceeds this (a debug <c>spawncreature</c>
    /// spree, not real content) is handled by <c>KotorCoop.Memory.EnemyCensusPage</c>'s
    /// rotation (agent side; this project cannot reference it) rather than a silent, permanent
    /// drop — see that type's own doc comment.
    /// </summary>
    public const int EnemyCapacity = 128;

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
    ///
    /// RELOCATED (version 80, same "no cap really" pass as <see cref="OffsetEnemySlots"/>):
    /// the old home, 0x1410..0x1910, could not grow either (<see cref="OffsetEnemyOneShotTable"/>
    /// starts right at the old boundary, 0x1920), so this moved into the new region too.
    /// 0x1410..0x1910 is now DEAD, same reasoning as 0x490..0x690. Occupies
    /// 0x6800..0x7C00 at the new <see cref="EnemyCapacity"/>.</summary>
    public const int OffsetEnemyPresenceSlots = 0x6800;

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


    // ---- per-attacker phantom-damage attribution (version 62, regions 0x4900..0x4A00 and 0x4AA0..0x4AC0) ----
    //
    // Extends the two channels above with a per-attacker breakdown, so IncomingDamageController
    // can absorb/apply a delta PER ATTACKER instead of billing a whole pump window's damage to
    // whichever id landed last — the bug the single-attacker fields above cannot fix: a window
    // mixing a locally-fought attacker with a mirrored one had to be billed wholesale to one or
    // the other, either double-counting or dropping the other's contribution. The legacy scalar
    // fields (OffsetPhantomHitSlots' cumulative/attacker/serial, OffsetIncomingDamage*) stay
    // live alongside this — an older reader still works off them alone. See
    // docs/research/peer-damage-ledger.md §7.
    //
    // Both tables share one entry shape: attackerId u32 @0, cumulative damage u32 @4, id
    // written last (the same "payload then key" discipline every fixed table in this block
    // uses) so a reader never pairs a fresh id with the previous entry's stale total. An id of
    // 0 marks an empty entry.

    /// <summary>Plugin -&gt; agent. Per PHANTOM SLOT (paired with <see cref="OffsetPhantomHitSlots"/>
    /// by index): up to <see cref="PhantomAttackerCapacity"/> (attackerId, cumulative) pairs
    /// PhantomHitHook captured landing on that phantom, least-total eviction when a fifth
    /// attacker needs a slot. Occupies 0x4900..0x4A00 (8 slots x 4 entries x 8 bytes).</summary>
    public const int OffsetPhantomAttackerSlots = 0x4900;

    public const int PhantomAttackerSlotStride = 0x20;

    public const int PhantomAttackerCapacity = 4;

    public const int PhantomAttackerEntryStride = 0x08;

    /// <summary>Agent -&gt; plugin. The LOCAL PLAYER's merged owed-per-attacker breakdown —
    /// summed across every peer that has hit their phantom elsewhere (KotorCoop.Protocol's
    /// <c>PhantomAttackerMerge</c>). One target only (unlike the OUT table above, which is
    /// per phantom slot), so this needs only <see cref="PhantomAttackerCapacity"/> entries.
    /// Occupies 0x4AA0..0x4AC0 — NOT the 0x4A00 the OUT table's end would suggest: the
    /// companion-damage channel (version 63, SharedBlock.CompanionDamage.cs) claimed
    /// 0x4A00..0x4A90 on a parallel branch the same day, so this sits past it.</summary>
    public const int OffsetIncomingDamageAttackers = 0x4AA0;

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
    ///
    /// RELOCATED (version 80, "no cap really" pass): raising <see cref="DamageLedgerCapacity"/>
    /// past 32 needed room the old home (0x1E60..0x1F60, wedged between the encounter-request
    /// region and <see cref="OffsetDownBlockReason"/>) did not have, so this moved into the new
    /// region with every other enemy-scaled table. 0x1E60..0x1F60 is now DEAD, same reasoning
    /// as <see cref="OffsetEnemySlots"/>'s own old home. Occupies 0x7E00..0x8200.</summary>
    public const int OffsetDamageDealtSlots = 0x7E00;

    /// <summary>Agent -&gt; plugin. Same shape, the other way: the total all peers have dealt
    /// to each of this instance's enemies.
    ///
    /// RELOCATED (version 80): the old home, 0x2018..0x2118, backed onto
    /// <see cref="OffsetDamageDealtEnabled"/> (0x2118) with no free bytes to grow into, so this
    /// moved too. 0x2018..0x2118 is now DEAD. Occupies 0x8200..0x8600.</summary>
    public const int OffsetDamageOwedSlots = 0x8200;

    public const int DamageLedgerStride = 0x08;

    /// <summary>
    /// Enemies one instance can carry damage for at a time. Raised from 32 to
    /// <see cref="EnemyCapacity"/> (version 80, "no cap really" —
    /// docs/research/creature-liveness.md): the ledger only holds creatures actually damaged
    /// and not yet seen dead, so it is a live-fight bound, and a fight can now legitimately
    /// involve as many creatures as the census itself carries. Matches
    /// <c>ProtocolConstants.MaxEnemyDamageEntries</c>.
    /// </summary>
    public const int DamageLedgerCapacity = EnemyCapacity;

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

    // ---- N-slot local combat (version 61, region 0x4800..0x4900) ------------------
    //
    // Widens local_combat from one locally-fought enemy to up to LocalCombatSlotCapacity (3):
    // an array of excluded ids alongside the single OffsetLocalCombatExcludedId scalar above.
    // The scalar stays written too — mirroring slot 0 — for one release of backward
    // compatibility, but every consumer in THIS repo reads the array; the scalar's only reason
    // to keep existing is a still-attached agent binary older than this pass, which cannot
    // happen in practice (KotorGameBridge.PluginSignature embeds the block version byte-exact
    // in the scan pattern, so an agent built against a different version simply never finds
    // this block at all rather than reading it half-right). See
    // docs/research/combat-participation.md §11.

    /// <summary>How many simultaneous local-combat exclusion slots the shared block carries.
    /// Pinned by the array width below, not by coop.ini — <c>local_combat_slots</c> (coop.ini,
    /// default 3) only decides how many of these THIS instance actually arms, clamped to this
    /// capacity by <see cref="EnemyPresenceController"/>'s own clamp.</summary>
    public const int LocalCombatSlotCapacity = 3;

    /// <summary>Plugin -&gt; agent. <see cref="LocalCombatSlotCapacity"/> owner-space ids (u32
    /// each, slot i at this offset + i*4), the N-wide sibling of
    /// <see cref="OffsetLocalCombatExcludedId"/> — same meaning per slot, same self-healing
    /// level semantics, same "the plugin clears its own slot back to 0 once that one fight
    /// ends" rule. A zero slot licenses nothing. Unused slots (this instance runs fewer than
    /// <see cref="LocalCombatSlotCapacity"/>) are always zero, never written otherwise, which is
    /// exactly what makes <c>local_combat_slots=1</c> reproduce the pre-N-slot single-scalar
    /// behaviour bit for bit — slots 1 and 2 simply never move.</summary>
    public const int OffsetLocalCombatExcludedIds = 0x4800;


    // ---- materialised-enemy local stand-ins (version 65, region 0x4B00..0x4B80) ---
    //
    // The set of OWNER enemy ids MaterialisedEnemies currently has a local stand-in BODY for —
    // published so the agent can tell, BEFORE spending a giveloot round trip on the single-slot
    // cheat channel, whether an arriving materialised-loot item's owner id names one of our own
    // stand-ins or (the overwhelmingly common case) a peer holding the real, GIT-identical
    // creature, which already rolled its own equivalent loot. Measured live 2026-09-02: six
    // wasted round trips in one second on a client rejoining after a wipe reload, each one
    // occupying the channel and costing two log lines for a refusal the client could have
    // skipped entirely. See docs/research/enemy-materialisation.md's 2026-09-02 section.
    //
    // Same shape as OffsetLocalCombatExcludedIds just above: a fixed EnemyCapacity-wide array
    // of owner ids, 0 marking an unused slot, no separate count field —
    // MaterialisedEnemies.OwnerIdsSnapshot already IS this table (unpacked, in place), so
    // publishing it is a straight copy of the existing array rather than a rebuild.

    /// <summary>Plugin -&gt; agent. EnemyCapacity owner-space ids (u32 each, slot i at this
    /// offset + i*4) — every owner id <c>MaterialisedEnemies</c> currently maps to a local
    /// stand-in body. 0 marks an unused slot, the same convention
    /// <see cref="OffsetLocalCombatExcludedIds"/> uses.
    ///
    /// RELOCATED (version 80, "no cap really" pass): the old home, 0x4B00..0x4B80, had exactly
    /// 0x80 bytes (the old 32-entry width) and nothing free past it before
    /// <see cref="OffsetThreatDebugCount"/> at 0x4B80, so this moved into the new region at the
    /// new, wider <see cref="EnemyCapacity"/>. 0x4B00..0x4B80 is now DEAD. Occupies
    /// 0x7C00..0x7E00.</summary>
    public const int OffsetMaterialisedOwnerIds = 0x7C00;


    // ---- threat-retarget diagnostics (version 66, region 0x4B80..0x4C00) ----------
    //
    // NOT part of the feature itself — coop.ini threat_retarget's own decision runs entirely
    // inside PeerDamageController.ApplyOwed against ThreatTable, a plain C# Dictionary with
    // nothing worth publishing to the AGENT. This region exists purely so `tools/blockpoke.ps1
    // threat` (the dev verb docs/research/peer-damage-ledger.md §9's live recipe uses) can see
    // what the table currently believes without attaching a debugger — the exact same
    // "publish plugin-internal state for scripted verification" reasoning the `ledger`/`combat`
    // verbs already rest on for PhantomHitHook/DamageOwedLedger. Plugin -> (blockpoke only);
    // the agent never reads this region.
    //
    // Fits the 0x80 bytes the materialised-owner-id table's own tail leaves before 0x4C00
    // exactly (4 + 4 + 6*0x14 = 0x80), so TotalSize did not need to grow again this round.

    /// <summary>Plugin -&gt; (blockpoke only). How many of <see cref="ThreatDebugCapacity"/> rows
    /// below are valid this pass. Rewritten every <c>PeerDamageController.ApplyOwed</c> pass
    /// that runs with <c>coop.ini threat_retarget</c> (<see cref="Odyssey.ThreatRetargetEnabled"/>)
    /// on, even to 0 (an empty table reads as 0 rows, not stale ones from a fight that ended).</summary>
    public const int OffsetThreatDebugCount = 0x4B80;

    /// <summary>Plugin -&gt; (blockpoke only). Bumped every publish, so a poller can tell a fresh
    /// read from a repeat of the last one — same idiom as every other generation dword in this
    /// file.</summary>
    public const int OffsetThreatDebugGeneration = 0x4B84;

    /// <summary>How many rows <see cref="OffsetThreatDebugRows"/> carries. Small: this is a
    /// diagnostic snapshot of whichever creatures this pass's owed loop touched, not an
    /// exhaustive dump of every creature <c>ThreatTable</c> has ever seen.</summary>
    public const int ThreatDebugCapacity = 6;

    public const int ThreatDebugRowStride = 0x14;

    /// <summary>Plugin -&gt; (blockpoke only). <see cref="ThreatDebugCapacity"/> rows of
    /// <see cref="ThreatDebugRowStride"/> bytes each: creature id (u32 @0), its current
    /// round+0x9CC attack target id (u32 @4), the table's top attacker id for it (u32 @8), the
    /// top attacker's threat *100 as a u32 (@0xC), and the current target's own threat *100 as
    /// a u32 (@0x10) — fixed-point rather than a float so blockpoke's existing byte-oriented U32
    /// reader needs no new helper. Occupies 0x4B88..0x4C00.</summary>
    public const int OffsetThreatDebugRows = 0x4B88;


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

}
