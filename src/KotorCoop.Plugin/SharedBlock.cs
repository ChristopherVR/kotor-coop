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

    /// <summary>Low 16 bits: PlayerState flags (1 in-dialogue, 2 paused, 4 in-combat, ...).
    /// High 16 bits: the peer's MAX hit points, so the stand-in can carry the peer's
    /// current/max ratio and the engine's own injured-walk rule applies to it (0 = unknown,
    /// an older agent). See RemoteView.FlagsMask.</summary>
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
internal partial struct SharedBlock
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
    /// <summary>
    /// Grown from 0x4000 for the companion-equipment channel (version 54,
    /// docs/research/companion-state-sync.md): the party-roster body identity region's own tail
    /// (0x3F70) leaves only 0x90 free bytes before 0x4000 — not enough for the 4-slot x 3-resref
    /// x 16-byte array <see cref="SharedBlock.OffsetCompanionEquipSlots"/> needs (0xC0) — so
    /// this region starts cleanly at the old boundary, the same choice every earlier growth in
    /// this file made.
    /// </summary>
    /// <summary>
    /// Grown from 0x4100 for the item-template-spawn ring (version 58,
    /// docs/research/item-identity.md §13): the container-lock arbitration region's own tail
    /// (0x40E0) leaves only 0x20 free bytes before 0x4100 — nowhere near the 64 x 0x14 ring plus
    /// its 4-byte cursor (0x504) <see cref="OffsetItemTemplateSpawnRing"/> needs — so this
    /// region starts cleanly at the old boundary (0x4100..0x4604) and TotalSize is rounded up
    /// to the next clean 0x100 boundary past that (0x4700), the same choice every earlier
    /// growth in this file made.
    /// </summary>
    /// <summary>
    /// Grown from 0x4700 for the item-template-spawn ring's per-entry cheat flag (version 59,
    /// docs/research/party-pool-sync.md §20): widening each entry from 0x14 to 0x18 pushes the
    /// ring itself to 0x4100..0x4700 and the cursor to 0x4700..0x4704, so this rounds up to the
    /// next clean 0x100 boundary past that, the same choice every earlier growth in this file
    /// made.
    /// </summary>
    /// <summary>
    /// Grown from 0x4800 to 0x4B00 in one pass by the 2026-09-02 combat wave, three parallel
    /// branches each claiming its own 0x100 range so the merge stayed textually clean: version
    /// 60's own additions (minigame lock, join-chargen handshake, the exe-image digest) run to
    /// 0x47C8 and filled what 0x4800 left after the item-template-spawn cursor, so each region
    /// starts at a clean boundary — the same choice every earlier growth in this file made.
    ///   0x4800..0x4900  version 61, N-slot local_combat excluded-id array (uses 0xC bytes),
    ///                   docs/research/combat-participation.md §11.
    ///   0x4900..0x4A00  version 62, per-attacker phantom-damage attribution OUT table (per
    ///                   phantom slot), docs/research/peer-damage-ledger.md §7.
    ///   0x4A00..0x4B00  version 63, companion damage TAKEN channel (uses 0x90 bytes),
    ///                   docs/research/companion-state-sync.md §13.
    ///   0x4AA0..0x4AC0  version 62's IN table (the local player's merged owed-per-attacker
    ///                   breakdown), relocated here at merge time because 63 had already
    ///                   claimed 0x4A00 on its branch.
    /// </summary>
    /// <summary>
    /// Grown from 0x4B00 to 0x4C00 for the materialised-owner-id table (version 65,
    /// docs/research/enemy-materialisation.md's 2026-09-02 section): the tail past the
    /// per-attacker phantom-damage IN table (0x4AC0) leaves only 0x40 free bytes before
    /// 0x4B00 — not enough for the EnemyCapacity-wide (32 x 4 = 0x80 byte) array
    /// <see cref="OffsetMaterialisedOwnerIds"/> needs — so this region starts cleanly at the
    /// old boundary, the same choice every earlier growth in this file made.
    ///
    /// TotalSize did NOT grow for the threat-retarget diagnostics region (version 66,
    /// docs/research/peer-damage-ledger.md §9): the materialised-owner-id table's own tail
    /// (0x4B80) leaves exactly 0x80 free bytes before 0x4C00, and
    /// <see cref="OffsetThreatDebugRows"/>'s 6-row table plus its count/generation dwords fits
    /// that gap precisely (4 + 4 + 6*0x14 = 0x80) — the first growth in this file's history that
    /// did not need a new 0x100 boundary.
    ///
    /// Grown from 0x4C00 to 0x4D00 for the galaxy-map departure relay (version 67,
    /// <see cref="OffsetGalaxyDepartureSerial"/>, docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 relay addendum): version 66 left no free bytes before 0x4C00 at all, so the
    /// region starts cleanly at the old boundary and uses 0x18 of the new 0x100.
    /// </summary>
    public const int TotalSize = 0x4D00;

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
    /// apply and the per-peer flags dword. Version 54 (2026-08-27) added
    /// <see cref="OffsetCompanionEquipSlots"/> — see SharedBlock.Party.cs. Version 55
    /// (2026-08-27) added <see cref="OffsetBehaviourOverrideMask"/>/
    /// <see cref="OffsetBehaviourOverrideValues"/> — see SharedBlock.BehaviourOverrides.cs.
    /// (2026-08-27, v56) added <see cref="OffsetLocalInSoloSegment"/> — see
    /// SharedBlock.SoloSegment.cs. Version 57 (2026-08-27) added the container-lock explicit
    /// arbitration channel — see SharedBlock.Dialogue.cs. Version 58 (2026-08-28) added
    /// <see cref="OffsetItemTemplateSpawnRing"/>/<see cref="OffsetItemTemplateSpawnCursor"/> —
    /// see SharedBlock.ItemTemplateSpawns.cs and docs/research/item-identity.md §13. Version 59
    /// (2026-08-28) widened each item-template-spawn ring entry with
    /// <see cref="ItemTemplateSpawnIsCheatOffset"/> — see SharedBlock.ItemTemplateSpawns.cs and
    /// docs/research/party-pool-sync.md §20. 60 (2026-08-29) added the minigame peer lock and
    /// new-character join-chargen handshake — see PluginHost.cs's own remarks. Version 62
    /// (2026-09-02) added <see cref="OffsetPhantomAttackerSlots"/>/<see cref="OffsetIncomingDamageAttackers"/>
    /// — see SharedBlock.Combat.cs and docs/research/peer-damage-ledger.md §7. Version 63
    /// (same day) added <see cref="OffsetCompanionDamageSlots"/>/<see cref="OffsetCompanionDamageOwedSlots"/>
    /// — see SharedBlock.CompanionDamage.cs and docs/research/companion-state-sync.md §13.
    /// Version 64 (same day) widened the item-template-spawn ring's flags dword with the
    /// per-entry stack count — see SharedBlock.ItemTemplateSpawns.cs and
    /// docs/research/shared-inventory-v2.md §17. Version 65 (same day) added
    /// <see cref="OffsetMaterialisedOwnerIds"/> — see SharedBlock.Combat.cs and
    /// docs/research/enemy-materialisation.md's 2026-09-02 section. Version 66 (2026-09-03)
    /// added <see cref="OffsetThreatDebugCount"/>/<see cref="OffsetThreatDebugGeneration"/>/
    /// <see cref="OffsetThreatDebugRows"/> — diagnostics only (the <c>threat</c> blockpoke
    /// verb), never read by the agent — see SharedBlock.Combat.cs and
    /// docs/research/peer-damage-ledger.md §9. Version 67 (2026-09-03) added the galaxy-map
    /// departure relay (SharedBlock.GalaxyDeparture.cs). Version 68 (2026-09-18) added the
    /// class-grant relay in that region's free tail — see SharedBlock.ClassGrant.cs and
    /// docs/research/class-and-power-grants.md.
    /// docs/research/peer-damage-ledger.md §9. Version 69 (2026-09-18) added
    /// <see cref="OffsetScalingCombat"/>/<see cref="OffsetScalingEconomy"/> — the agent's
    /// resolved player-count scaling percents — see SharedBlock.PlayerCountScaling.cs and
    /// docs/research/player-count-scaling.md.
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

    // ---- co-op cheat kind values -------------------------------------------------
    //
    // Kept in THIS file, not SharedBlock.Cheats.cs with the rest of the cheat-channel
    // fields: CheatKindSyncTests.cs reads them straight out of src/KotorCoop.Plugin/
    // SharedBlock.cs by path and regex, cross-checking them against the agent's own
    // CheatKinds mirror (KEEP IN SYNC - see that test's own header for the collision
    // this guards against). See SharedBlock.Cheats.cs for every other cheat-channel
    // field (CheatTarget, CheatAmount, CheatStatus*, etc).

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

    /// <summary>A <see cref="CheatKindResolve"/> target that is not an object at all: dump
    /// CSWParty's member array instead (the `resolve party` verb). Above every real id
    /// (creatures top out far below, and 0x7F000000 is the engine's own invalid marker) and
    /// deliberately NOT 0xFFFFFFFF, which is what CSWParty member[0] holds for the PC
    /// (0x7FFFFFFF | ControlPartyNpcFlag) — a resolve of that exact id must stay a resolve.</summary>
    public const uint CheatTargetParty = 0xFFFFFFFE;

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
    /// LIVE-TEST/DIAGNOSTIC PRIMITIVE (docs/research/shared-state-audit.md §2, round 36): snaps
    /// <see cref="SharedBlock.CheatTarget"/> next to the LOCAL PLAYER by default (a fixed +X
    /// offset — see <c>MoveNearDestinationRule</c>), so a genuinely non-census creature (a party
    /// member, never touched by EnemyPresenceController at all) can be put somewhere useful to
    /// test against without depending on what the current module happens to contain. Uses
    /// <c>CheatTarget</c> for the creature id and <see cref="OffsetCheatX"/>/
    /// <see cref="OffsetCheatY"/>/<see cref="OffsetCheatZ"/> for an optional explicit
    /// destination override; no new fields, no version bump.
    ///
    /// Round 36's original build snapped to a hardcoded position near END_M01AA's
    /// `sithsoldier 0xE` (29.74, 144.46) regardless of module. 2026-09-02: measured live in
    /// `tar_m10aa`, that same hardcoded point sent a live Vulkar off the map — see
    /// docs/research/shared-state-audit.md §2, round 36's dated addendum.
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
    /// STAGING VERB (docs/research/shared-state-audit.md §2, round 56/57): opens an ORDINARY
    /// door — one <c>usedoor</c> refuses by design, since it must be incapable of doing
    /// anything except starting a module transition (see <see cref="CheatKindUseDoor"/>'s own
    /// doc comment). Built because the non-transition case was genuinely untested: no working
    /// `walk`/click primitive in the rig could reach one, and `usedoor` refuses every door
    /// `end_m01aa`'s GIT names that is not linked to a module.
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

    /// <summary>
    /// Adds <see cref="OffsetCheatAmount"/> units of a resref+identity stack to the shared
    /// party item pool (<c>CSWPartyTable+0x118</c>). <see cref="OffsetCheatText"/> is the
    /// resref, Amount the stack size, Charges/MaxCharges/Upgrades/ItemFlags the rest of the
    /// stack identity — the same five fields <see cref="CheatKindGiveLoot"/> and
    /// <see cref="CheatKindSetItem"/> already carry, reused rather than duplicated.
    /// <see cref="OffsetCheatTarget"/> is unused. See docs/research/party-pool-sync.md.
    ///
    /// ⚠ 25, because 7-24 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindPartyPoolAdd = 25;

    /// <summary>
    /// Removes <see cref="OffsetCheatAmount"/> units of a resref+identity stack from the shared
    /// party item pool. Same field layout as <see cref="CheatKindPartyPoolAdd"/>. See
    /// docs/research/party-pool-sync.md.
    ///
    /// ⚠ 26, because 7-25 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindPartyPoolRemove = 26;

    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/disconnect-reconnect.md §9): tears the
    /// world down and shows the main menu, through
    /// <c>CClientExoAppInternal::ShutDownToMainMenu</c> (0x005FCBD0) — the SAME function
    /// <c>CClientExoAppInternal::StallServerCode</c> (0x00602CDE) calls once
    /// <c>CSWGuiInGameOptions::ReallyQuit</c>'s <c>SetEndGamePending(1,0,1)</c> is noticed on a
    /// later frame, i.e. exactly what a real "Quit Game -&gt; Yes" click ultimately reaches.
    /// Built because reaching a genuine "no player object at all" state (the Away flag's third
    /// shape — main menu / load screen / character-export screen) by navigating the vanilla
    /// pause menu with injected mouse clicks is a documented dead end on a demo.ps1-repositioned
    /// window (docs/research/session-save.md's "Attempted, not achieved" section: 36 candidate
    /// clicks, zero landed, and blind keyboard navigation once wedged an instance requiring a
    /// full rig restart). Calling the engine function directly, the same way <c>usedoor</c>/
    /// <c>skipline</c> route around missing OS input, needs no coordinates and cannot wedge
    /// anything. Takes no arguments; local only, never crosses the network.
    ///
    /// ⚠ 27, because 7-26 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindQuitToMenu = 27;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/dialogue-consequences.md's
    /// script-item-sync round): gives an item straight into THIS instance's own player's
    /// inventory through <c>Odyssey.GiveItemToInventory</c> with NO agent-side suppression-ledger
    /// entry recorded, ON PURPOSE — simulating exactly what an OnEnter/OnDeath/heartbeat script
    /// granting an item looks like to <c>ScriptItemGrantPump</c>'s sampling diff, which this
    /// codebase otherwise has no console primitive to trigger on demand. <c>CheatText</c> is the
    /// resref, <c>CheatAmount</c> the stack size. See <c>CheatController.Items.cs</c>'s own
    /// <c>ScriptGive</c> doc comment for the full reasoning and why <c>giveloot</c>'s existing
    /// local-player sentinel cannot be reused for this (it IS suppressed, on purpose — it is the
    /// feature's own negative control).
    ///
    /// ⚠ 28, because 7-27 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindScriptGive = 28;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/dialogue-consequences.md's script-item-sync
    /// round, Controls 2/3): removes the target container/corpse's first item through the real
    /// <c>CSWSPlaceable::RemoveItem</c> call (<c>Odyssey.TryTakeFirstContainerItem</c> — the same
    /// primitive <c>TryWriteContainerLoot</c>'s apply path already uses) and gives an equivalent
    /// copy to the local player via <c>Odyssey.GiveItemToInventory</c>, reproducing BOTH halves
    /// of a real container/corpse take (a removal AND a new inventory row) through two verified
    /// engine calls, since a GUI click cannot be delivered reliably on this rig. See
    /// <c>CheatController.World.cs</c>'s own <c>LootTake</c> doc comment. Same target fields as
    /// <c>CheatKindUsePlaceable</c> (<c>CheatTarget</c> = object id, or <c>CheatText</c> = tag,
    /// blank = nearest container).
    ///
    /// ⚠ 29, because 7-28 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindLootTake = 29;

    /// <summary>
    /// Inserts a resref+identity stack into a store's stock. Mirrors
    /// <c>IGameBridge.CheatKinds.StoreInsert</c> exactly — see that constant's own doc comment
    /// for the full reasoning. <c>CheatTarget</c> is the store's object id, <c>CheatText</c> the
    /// resref, <c>CheatAmount</c> the stack size, Charges/MaxCharges/Upgrades/ItemFlags the rest
    /// of the stack identity. See docs/research/store-sync.md.
    /// </summary>
    public const uint CheatKindStoreInsert = 30;

    /// <summary>
    /// Removes a resref+identity stack from a store's stock. Mirrors
    /// <c>IGameBridge.CheatKinds.StoreRemove</c> exactly. Same field layout as
    /// <see cref="CheatKindStoreInsert"/>.
    ///
    /// 
    /// </summary>
    public const uint CheatKindStoreRemove = 31;

    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/companion-state-sync.md's live test plan
    /// §5): pokes <c>CSWSCreatureStats::experience</c> (<c>creature+0xA74</c> then <c>+0x068</c>,
    /// the same offset chain <c>progression-sync.md</c>/<c>companion-state-sync.md</c> already
    /// verified) on the creature named by <c>CheatTarget</c> by adding <c>CheatAmount</c>
    /// (signed) to its current experience, clamped at 0 — simulating exactly what a
    /// conversation/stealth-award/<c>GivePlotXP</c> script grant aimed at one creature only
    /// looks like to <c>companion_xp_sync</c>'s merge, which this codebase otherwise has no way
    /// to trigger on demand against a companion body specifically (kill XP already agrees
    /// without the channel per that doc's §1.2). <c>CheatTarget</c> = 0 means the local player.
    ///
    /// ⚠ 32, because 7-31 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindGrantXp = 32;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/companion-state-sync.md's live test plan
    /// §5): re-equips the creature named by <c>CheatTarget</c> with a real item, through
    /// <c>Odyssey.EquipItemFromTemplate</c> — the exact call the phantom equipment channel
    /// already ships and live-verified, aimed at an ordinary party-roster companion body
    /// instead, which that doc names as the one untested case (the call takes a bare object id
    /// and never inspects what kind of body it is). <c>CheatText</c> is the resref,
    /// <c>CheatAmount</c> the NWScript inventory slot (1 = body, 4 = right weapon,
    /// 5 = left weapon — <c>Odyssey.InventorySlotBody/RightWeapon/LeftWeapon</c>; blank/0
    /// defaults to the right-weapon slot, the visible, easy-to-confirm case the live test plan
    /// actually asks for). <c>CheatTarget</c> = 0 means the local player.
    ///
    /// ⚠ 33, because 7-32 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindEquip = 33;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/quest-sync.md's 2026-08-27 addendum):
    /// writes a script global by NAME through the engine's own type-checked setter
    /// (<c>CSWGlobalVariableTable::SetValueBoolean</c> 0x00529570 /
    /// <c>SetValueNumber</c> 0x00529680) — the SAME two functions
    /// <c>CSWVirtualMachineCommands::ExecuteCommandSetGlobalNumber</c> (0x00542B60) calls once
    /// it has popped its own two VM arguments, called directly here instead of through that
    /// machinery (the same reasoning <see cref="CheatKindOpenStore"/> uses for
    /// <c>ShowStoreGui</c>). Built so the rig can drive a galaxy-map leg (or any other
    /// global-gated dispatch, e.g. <c>firescript k_pebn_galaxy</c>) without a GUI click.
    /// <see cref="OffsetCheatText"/> is the global's name (max 22 chars — the catalogue's own
    /// identifier length), <see cref="OffsetCheatAmount"/> the signed value,
    /// <see cref="OffsetCheatItemFlags"/> 1 for the boolean setter / 0 for the number setter
    /// (reused rather than a new field — this cheat never touches an item).
    ///
    /// ⚠ 34, because 7-33 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindSetGlobal = 34;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/minigames.md's 2026-08-27 addendum):
    /// starts a Pazaak hand through <c>CClientExoApp::StartPazaakGame</c> (0x005EDDC0) — the
    /// SAME function <c>CSWVirtualMachineCommands::ExecuteCommandPlayPazaak</c> (0x00540FD0,
    /// NOT 0x0053B1E0 — that address is <c>ExecuteCommandGetLastPazaakResult</c>, a correction
    /// to this doc's own earlier reading) calls once it has popped its five VM arguments,
    /// called directly instead so <c>pazaak_detect</c> can finally be verified POSITIVELY (the
    /// prior live pass — docs/roadmap-seamless.md item 9 — only ever confirmed the negative
    /// case, since nothing could inject the click that starts a hand). No wager
    /// (<c>nMaxWager=0</c>, so the wager screen never shows and no credits move — the script
    /// header's own documented behaviour) and no tutorial mode.
    /// <see cref="OffsetCheatTarget"/> is the opponent NPC's object id, or
    /// <see cref="OffsetCheatText"/> its tag (both empty means the engine's own default
    /// opponent, <c>OBJECT_INVALID</c>); <see cref="OffsetCheatAmount"/> is the opponent's
    /// pazaak-deck index (default 0 when the request field is 0 — NEEDS-LIVE which index is
    /// "correct" for a given NPC; no per-creature deck lookup was found this pass, see the
    /// research doc).
    ///
    /// ⚠ 35, because 7-34 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindPazaak = 35;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-27, docs/research/store-sync.md's 2026-08-27 addendum):
    /// opens the store GUI against a store resolved by object id or tag, through
    /// <c>CGuiInGame::ShowStoreGui</c> (0x0062E310) called directly — the C++ entry
    /// <c>ExecuteCommandOpenStore</c> (0x00540300) itself reaches, but through its own VM
    /// arg-popping machinery store-sync.md's original pass deliberately declined to build a
    /// verb against (§1.6/§6 there: "the call chain... was not traced far enough"). This pass
    /// traced the remaining hop (<c>ShowStoreGui</c> takes only <c>this</c>; the open store's
    /// object id is written separately into the new panel's own <c>+0x227C</c> field, then
    /// <c>CSWGuiStore::PopulateStore</c> 0x006C0610 is called directly to force the refresh) so
    /// <c>store_sync</c> can finally be verified without a manual click.
    /// <see cref="OffsetCheatTarget"/> is the store's object id, or <see cref="OffsetCheatText"/>
    /// its tag; one of the two is required — there is no "nearest store" sweep.
    /// </summary>
    public const uint CheatKindOpenStore = 36;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-28, docs/research/galaxy-map-and-party-select.md's
    /// escape-pod addendum): confirms the native party-selection screen
    /// (<c>CSWGuiPartySelection</c>, reached from <c>CGuiInGame+0x078</c>, the same offset
    /// <see cref="PartySelectSuppressionController"/> already reads) with its current
    /// selection, through <c>CSWGuiPartySelection::ConfirmPartySelection</c> (0x006BEC90) —
    /// the panel's own OK/Done handler, the same one a real click reaches — after poking the
    /// shared <c>CSWGuiMessageBox+0x64</c> bit-0 precondition it gates on (the same
    /// <c>msgboxpoke</c> trap <c>loadgame</c> already needs on a fresh all-console session; see
    /// docs/research/session-save.md). Refuses if no party-selection panel is on screen, or if
    /// its vtable does not match (guarded, never a blind write). Takes no arguments; local
    /// only, no <see cref="SharedBlockView"/> field needed beyond the plain cheat channel.
    ///
    /// ⚠ 37, because 7-36 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindPartySelectOk = 37;

    /// <summary>
    /// LIVE-TEST/DIAGNOSTIC PRIMITIVE (2026-08-28, docs/research/combat-participation.md §10's
    /// own ranked follow-up list, item 1): reads the engine's own hostility verdict for one
    /// creature pairing, read-only, no engine write. <c>CheatTarget</c> is the creature to
    /// query (0 = local player); <c>CheatAmount</c> is the "versus" id (0 = local player).
    /// Reports <c>CSWSObject::GetAIStateReputation</c> (0x0057CD90,
    /// <see cref="Odyssey.GetAIStateReputation"/>, the SAME call
    /// <c>EnemyPresenceController.SelectImmediateCandidate</c> already gates local-fight
    /// candidacy on) in both directions, plus the raw <c>CSWSCreatureStats::faction_id</c> dword
    /// at <c>stats+0x78</c> for each side — built specifically to settle round 89's own open
    /// question ("does the client's own local player read hostile to a GIT-identical Sith
    /// trooper the way the host's does") without needing to add ad-hoc logging to
    /// <c>EnemyPresenceController</c> itself.
    ///
    /// ⚠ 38, because 7-37 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindFaction = 38;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-28, docs/research/minigames.md's live-pass entry): ends the
    /// current Pazaak hand through <c>CClientExoApp::EndPazaakGame</c> (0x005EDDD0, a thin
    /// thunk — <c>mov ecx,[ecx+4]; jmp CClientExoAppInternal::EndPazaakGame</c>, the same
    /// <c>this</c>-forwarding shape <c>StartPazaakGameFn</c> already uses) so
    /// <c>FlagInMinigame</c> can finally be watched clearing without needing a real wagered hand
    /// played to completion through the (uninjectable) GUI. Fresh disasm this pass of
    /// <c>CClientExoAppInternal::EndPazaakGame</c> (0x005F3950, confirmed by name in
    /// <c>tools/kotor1_0_3.db</c>) found a 4-stack-arg thiscall (<c>ret 0x10</c>): arg1/arg2
    /// (this pass calls both 0) are written to <c>this+0x80</c>/<c>+0x7C</c> unconditionally and
    /// otherwise only feed a gold/opponent-payout branch gated on <c>arg2 &gt; 0</c> (skipped by
    /// passing 0 — there is nothing to pay out for a <c>nMaxWager=0</c> console-started hand);
    /// arg3 (also 0) selects between re-starting a new hand (<c>CClientExoAppInternal::
    /// StartPazaakGame</c>, skipped) and running the hand's own end-script (a documented no-op —
    /// <see cref="Odyssey.StartPazaak"/> always starts with an EMPTY end-script) — arg4 is only
    /// read on the skipped branch. <c>this+0x70</c> (the SAME field <c>IsPazaakRunning</c> reads
    /// as "a hand is running") is unconditionally cleared to 0 near the top, before any of the
    /// above. Refuses cleanly if no hand is running. Takes no arguments; local only.
    ///
    /// ⚠ 39, because 7-38 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindPazaakEnd = 39;

    /// <summary>
    /// DEV VERB (2026-08-28, docs/research/store-sync.md's 2026-08-28 addendum): buys up to
    /// <see cref="OffsetCheatAmount"/> units of a resref from a store, through the REAL
    /// <c>CSWGuiStore::BuyItem</c> click handler (<c>Odyssey.BuyFromStore</c>) — gold and item
    /// both move through the engine's own path, the same call a real click makes. Requires the
    /// target store's own GUI to already be open (<c>openstore</c> first) — refuses otherwise.
    ///
    /// <see cref="OffsetCheatTarget"/> nonzero = the store's object id, and
    /// <see cref="OffsetCheatText"/> is then the bare resref. <see cref="OffsetCheatTarget"/>
    /// zero means the store was given as a TAG: <see cref="OffsetCheatText"/> then packs
    /// <c>"&lt;tag&gt;|&lt;resref&gt;"</c> (a pipe, never a legal tag/resref character) —
    /// <see cref="OffsetCheatText"/> is the only string field this channel has, and a store's
    /// identity and the item's resref are both needed at once, unlike every existing
    /// single-string verb (<c>giveloot</c>'s owner id is always numeric; <c>openstore</c> needs
    /// only one string). Same packing convention <see cref="CheatKindStoreSell"/> uses.
    ///
    /// ⚠ 40, because 7-39 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindStoreBuy = 40;

    /// <summary>
    /// DEV VERB (2026-08-28, docs/research/store-sync.md's 2026-08-28 addendum): sells up to
    /// <see cref="OffsetCheatAmount"/> units of a resref from the local player's own inventory
    /// to a store, through the REAL <c>CSWGuiStore::SellItem</c> click handler
    /// (<c>Odyssey.SellToStore</c>). Same field layout, same open-GUI requirement and same
    /// tag-packing convention as <see cref="CheatKindStoreBuy"/> — see that constant's own doc
    /// comment.
    ///
    /// ⚠ 41, because 7-40 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindStoreSell = 41;

    /// <summary>
    /// DEV VERB (2026-08-28, docs/research/dialogue-sync.md's 2026-08-28 addendum): starts a
    /// conversation between the local player and an NPC through the engine's own
    /// <c>ActionStartConversation</c> VM routine (id 204), dispatched the SAME verified
    /// routine-table path <c>Odyssey.MoveToLocation</c> already established for
    /// <c>ActionMoveToLocation</c> — not a hand-crafted <c>CSWSObject::StartDialog</c> call
    /// (that function's own 2nd/3rd arguments are caller-owned structs this plugin cannot
    /// safely fabricate; see <c>Odyssey.StartConversation</c>'s own header comment). Runs
    /// through the real action-queue -&gt; <c>BeginConversation</c> -&gt; <c>StartDialog</c>
    /// chain, so the existing dialogue hooks observe it exactly as a real click.
    ///
    /// <see cref="OffsetCheatTarget"/> nonzero = the NPC's object id, and
    /// <see cref="OffsetCheatText"/> is then the (optional) dialog resref override, or empty to
    /// use the NPC's own default. <see cref="OffsetCheatTarget"/> zero means the NPC was given
    /// as a TAG: <see cref="OffsetCheatText"/> then packs <c>"&lt;tag&gt;"</c> or
    /// <c>"&lt;tag&gt;|&lt;dlg-resref&gt;"</c> — the same one-string-field packing convention
    /// <see cref="CheatKindStoreBuy"/> uses, for the identical reason (an NPC's identity and an
    /// optional dialog override are two independent pieces of text).
    ///
    /// ⚠ 42, because 7-41 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindStartDialog = 42;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-08-28, docs/research/star-forge-finale.md §11): calls
    /// the engine's own <c>RemovePartyMember(int nNPC)</c> VM routine (id 575) through
    /// <c>Odyssey.RemovePartyMemberBySlot</c> — the SAME parameter-stack dispatch pattern
    /// <c>startdialog</c> already established (push the argument onto the VM's own stack,
    /// dispatch through the routine table by verified handler address, pop the return) — so
    /// <c>RemovePartyMemberHook</c>'s own detour can be exercised WITHOUT reaching
    /// <c>k_sta_bastila.dlg</c>'s entry 70 (§9.3/§9.4's own one-shot-precondition maze). Not a
    /// staging primitive for the roster itself in the way <c>recruit</c> is — this exists purely
    /// to fire the real engine call the detector watches for, on demand.
    ///
    /// <see cref="OffsetCheatAmount"/> is the NPC roster slot (0-8, the same range
    /// <c>recruit</c>/<c>Odyssey.PartyRosterSlots</c> already use). Local only — never crosses
    /// the network by itself.
    ///
    /// ⚠ 43, because 7-42 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindRemoveParty = 43;

    /// <summary>
    /// DEV VERB (2026-08-28, docs/research/store-sync.md's live pass): sets the party's gold
    /// directly to <see cref="OffsetCheatAmount"/> through <c>CSWSCreature::SetGold 0x4EDD90</c>
    /// (<c>Odyssey.SetGold</c>) — clamped server-side to 999,999,999, same as any other caller
    /// of that engine function. Built to repair a save whose gold was driven
    /// negative-then-wrapped by an unguarded <c>buy</c> (before this pass added
    /// <c>Odyssey.BuyFromStore</c>'s own affordability guard) — see
    /// docs/live-verification.md for the shared rig save's own poisoned reading. Unlike
    /// <c>grantgold</c> (a signed DELTA the agent writes directly, no plugin round trip), this
    /// sets an ABSOLUTE target, which is what repairing a specific poisoned value needs. Local
    /// only — never crosses the network by itself.
    ///
    /// ⚠ 44, because 1-43 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindSetGold = 44;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-08-28/29, docs/research/party-reconciliation.md Round
    /// 96's own "concrete next step" (b)): dumps the named companion's follow-record fields
    /// (phase, attempts, target point, current path node) to the plugin log, then forces
    /// <c>record+4 = 10</c> (<c>TeleportForward</c>) through
    /// <c>Odyssey.TryForceTeleportForward</c> UNCONDITIONALLY — regardless of the companion's
    /// current phase, unlike the automatic <c>party_follow_phase2_kick</c> pass (which only
    /// fires from phase 2). Built to answer Round 96's own open question live: does poking
    /// <c>TeleportForward</c> also resume the phase-7 near-target freeze that round found, not
    /// just the phase-2 stall the shipped fix targets.
    ///
    /// <see cref="OffsetCheatAmount"/> is the party-roster slot (0-8, same range
    /// <c>recruit</c>/<c>leader</c> already use). Local only — never crosses the network.
    ///
    /// ⚠ 45, because 1-44 are taken. Take the next FREE number.
    /// </summary>
    public const uint CheatKindFollowKick = 45;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-08-29, docs/research/party-reconciliation.md Round
    /// 105): dumps the named companion's follow-record wedge signature (phase, attempts,
    /// pathNode/destNode/writeCursor, and the two squared distances <c>PathWaitDest</c>'s own
    /// accept test compares) to the plugin log, then applies ONE of
    /// <c>FollowStallRule</c>'s recoveries through <c>Odyssey.TryRecoverFollowStall</c>
    /// UNCONDITIONALLY — no wedge check, no stall timer, no cooldown, unlike the automatic
    /// <c>party_follow_stall_recovery</c> pass.
    ///
    /// Exists so a live A/B can isolate all four candidate recoveries inside ONE game session:
    /// the automatic pass reads its method from coop.ini at plugin construction, so testing four
    /// candidates through it alone would mean four full rig restarts.
    ///
    /// <see cref="OffsetCheatAmount"/> is the party-roster slot (0-8, same range
    /// <c>recruit</c>/<c>leader</c>/<c>followkick</c> already use);
    /// <see cref="OffsetCheatTarget"/> is the recovery method (1 phasereset, 2 followleader,
    /// 3 positionparty, 4 priortie; 0 means "whatever coop.ini selected"). Local only — never
    /// crosses the network.
    ///
    /// ⚠ 56, from this stream's own reserved range (56-58); 46-55 belong to other concurrent
    /// streams. Take the next FREE number in YOUR range.
    /// </summary>
    public const uint CheatKindFollowStall = 56;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-29, docs/research/dialogue-skip-desync.md): presses skip on
    /// the dialogue panel THROUGH THE ENGINE'S OWN INPUT DISPATCH —
    /// <c>CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)</c> via the panel's vtable slot 0x3C,
    /// which both dialogue panel classes share.
    ///
    /// ⚠ THIS IS NOT <see cref="CheatKindSkipLine"/> WITH A DIFFERENT NAME. <c>skipline</c>
    /// pokes <c>CSWSObject+0x1F4</c> on the resolved conversation OWNER and never enters
    /// <c>HandleInputEvent</c>, so it cannot exercise <c>DialogueSkipHook</c> — the spectator
    /// gag spliced into that function — and it refuses outright on a spectator, which is the
    /// only instance the gag is about. Measured live 2026-08-29: <c>skipline</c> left the
    /// hook's swallow count flat on both instances, proving nothing. This verb enters the input
    /// path, so <c>DialogueSkipHook.SwallowedCount</c> becomes the honest oracle — it must
    /// increment on the spectator (with nothing advancing anywhere) and stay flat on the owner.
    ///
    /// Local only, never crosses the network. Answer text carries the swallow count before and
    /// after the call, so one console reply says which of the two happened.
    ///
    /// ⚠ 59, from this stream's own reserved range (59-62); 57-58 belong to another concurrent
    /// stream. Take the next FREE number in YOUR range.
    /// </summary>
    public const uint CheatKindSkipKey = 59;

    /// <summary>
    /// Saves the game through the engine's OWN save path —
    /// <c>CClientExoAppInternal::DoQuickSave 0x005F4B50</c>, the function retail's quicksave
    /// binding calls, which builds the "QUICKSAVE" name and hands it to
    /// <c>CClientExoApp::SendSaveGameRequest</c>. From there it is the ordinary client -&gt;
    /// server save message, <c>CSWSPlayer::GetIsAllowedToSave</c> gate and all.
    ///
    /// WHY THE PLUGIN NEEDS A SAVE VERB AT ALL. <c>join_mode = newcharacter</c>
    /// (docs/research/world-adoption.md) has to obtain the character a first-time joiner just
    /// created, and the only portable form of a KOTOR character is the one adoption already
    /// reads: <c>&lt;module&gt;.sav -&gt; module.ifo -&gt; Mod_PlayerList</c>, inside a save.
    /// Retail chargen does NOT write one (see that document's 2026-08-29 correction: the
    /// chargen writer emits <c>Temp	emp.bic</c>, not <c>TEMP:pifo</c>), so the character has
    /// to be saved once, in-world, before it can be carried anywhere. This is that save.
    ///
    /// Also a live-test primitive in its own right: producing a genuinely fresh save on demand
    /// is something several rig scripts have wanted (docs/research/session-save.md's rounds
    /// staged saves by copying directories about because there was no way to ask for one).
    ///
    /// ⚠ 60, from this stream's own reserved range (59-62).
    /// </summary>
    public const uint CheatKindQuickSave = 60;

    /// LIVE-TEST PRIMITIVE (2026-08-29, docs/research/shared-inventory-v2.md §14): takes the
    /// full-screen in-game GUI (equipment / inventory / character / abilities / journal / map /
    /// options) back down through the engine's own <c>CSWGuiInGame::HideSWInGameGui</c>
    /// 0x0062CBA0 — <c>Odyssey.CloseFullScreenGui</c>.
    ///
    /// Exists because <c>CGuiInGame+0x30</c>, which that screen sets, is the precondition
    /// <c>ShowStoreGui</c> (and so <see cref="CheatKindOpenStore"/>) refuses on, and that
    /// refusal has blocked the CLIENT half of every live store round this project has run —
    /// four consecutive rounds, six refusals in the last one. The documented
    /// <c>skipline</c>/<c>selectreply 0</c>/<c>partyselect ok</c> retry loop cannot clear it:
    /// none of those three touches that field, and its only two writers in the whole class are
    /// <c>ShowSWInGameGui</c> (sets 1) and <c>HideSWInGameGui</c> (clears).
    ///
    /// Takes no arguments. Local only — never crosses the network, and a no-op (reported as
    /// such) when the screen is not up.
    ///
    /// ⚠ 53, from this stream's own reserved range (53-55). Take the next FREE number in YOUR
    /// range.
    /// </summary>
    public const uint CheatKindCloseGui = 53;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-29, docs/research/shared-inventory-v2.md §15): pops panels
    /// off the GUI manager's own modal stack through <c>CSWGuiManager::PopModalPanel</c>
    /// 0x0040BE00 — <c>Odyssey.PopModalPanels</c>.
    ///
    /// The other half of <see cref="CheatKindCloseGui"/>, and the one that actually unblocks the
    /// client. <c>HideSWInGameGui</c> bails at 0x0062CBD1 while <c>CSWGuiManager+0x98</c> is
    /// non-zero, so `closegui` on its own could never clear <c>CGuiInGame+0x30</c> — which is the
    /// field <c>ShowStoreGui</c> refuses on. §14 measured the blocker and named this call; this
    /// is it.
    ///
    /// <see cref="CheatAmount"/> is how many panels to pop at most (default 1, clamped 1..8).
    /// Local only, and a deliberate no-op — reported as success — on an already-empty stack.
    ///
    /// ⚠ 54, from this stream's own reserved range (54-55). Take the next FREE number in YOUR
    /// range.
    /// </summary>
    public const uint CheatKindPopModal = 54;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-09-02, docs/research/death-handling.md's dated
    /// follow-up): applies REAL damage through the same VM path <c>slay</c>'s
    /// <c>Odyssey.ApplyDamageEffect</c> already uses — <c>EffectDamage</c> +
    /// <c>ApplyEffectToObject</c>, the pipeline a genuine hit runs — so the co-op down/revive
    /// pipeline can be reached without waiting for a real fight to drop someone to 0 hp. Runs
    /// through <c>CSWSCreature::DoDamage</c>/<c>CSWSObject::DoDamage</c> exactly like a live
    /// swing: difficulty scaling, temp-HP (shield) absorption and BOTH of the engine's own
    /// 1-hp floors (a <c>min1hp</c> pin — module or <c>god</c> — and the difficulty
    /// <c>MinHP1</c> rule, statically dead in retail per <c>IsDifficultyMinOneHitPointOn</c>)
    /// apply exactly as they would to a real hit — this is the floor <see cref="CheatKindDown"/>
    /// deliberately bypasses.
    ///
    /// <see cref="CheatAmount"/> is the damage amount (required, clamped 1..10000 by the VM
    /// handler itself). <see cref="CheatTarget"/> is <see cref="CheatTargetLocalPlayer"/> for
    /// yourself (the console default when no slot is typed) or a party-roster slot 0-8 — the
    /// only two kinds of body the engine's temp-death machinery ever applies to
    /// (<c>CSWSCreature::GetDead</c> hard-returns 0 only for a client-party member; anyone
    /// else just dies outright, which is not what this verb is for). Local only, never crosses
    /// the network.
    ///
    /// ⚠ 63, next free after every noted reserved range through 62 as of 2026-09-02. This
    /// wave owns the whole cheat-kind counter — see docs/CONTRIBUTING.md's parallel-session
    /// hazards for why that matters.
    /// </summary>
    public const uint CheatKindHurt = 63;

    /// <summary>
    /// DEV-ONLY LIVE-TEST PRIMITIVE (2026-09-02, docs/research/death-handling.md's dated
    /// follow-up): forces the SAME genuine temp-death state a real lethal hit produces — not a
    /// fake "downed" flag — by clearing any <c>min1hp</c> pin the target currently holds (the
    /// same order <c>slay</c> uses before a real kill), applying enough lethal damage through
    /// <c>Odyssey.ApplyDamageEffect</c> to certainly cross zero, then restoring the pin
    /// afterward — a party member never truly dies from this
    /// (<c>CSWSCreature::GetDead</c> hard-returns 0 for a party member); it enters exactly the
    /// temp-dead state <c>TempDeathUpdate</c>'s own auto-revive already knows how to recover
    /// from. THIS IS THE FLOOR <c>hurt</c> RESPECTS AND <c>down</c> BYPASSES: a module's
    /// <c>min1hp</c> pin (Endar Spire command deck, Taris duel ring — min-one-hitpoint.md)
    /// would otherwise convert the lethal effect into "set hp 1" and no temp-death would ever
    /// fire, defeating the entire point of a verb built to reach the pipeline on demand. The
    /// difficulty <c>MinHP1</c> floor is left alone — nothing needs to bypass it, since it is
    /// statically dead in retail (<c>IsDifficultyMinOneHitPointOn</c>).
    ///
    /// <see cref="CheatTarget"/> is <see cref="CheatTargetLocalPlayer"/> for yourself (the
    /// console default) or a party-roster slot 0-8, the same addressing <see cref="CheatKindHurt"/>
    /// uses. <see cref="CheatAmount"/> is unused. A target already down is a reported no-op,
    /// not a re-application. Local only, never crosses the network.
    ///
    /// ⚠ 64, next free after every noted reserved range through 62 as of 2026-09-02 (63 taken
    /// immediately above by <see cref="CheatKindHurt"/>).
    /// </summary>
    public const uint CheatKindDown = 64;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (Tier 2 item 18 task 2's own "Live test plan" follow-up,
    /// docs/research/galaxy-map-and-party-select.md's 2026-09-03 addendum): opens this
    /// instance's own galaxy-map GUI through <c>CGuiInGame::ShowGalaxyMapGui</c>, called
    /// directly (see <c>Odyssey.GalaxyMap.cs</c>) — the same entry point
    /// <c>k_pebn_galaxy.nss</c>'s <c>infoterm001</c> console reaches, so
    /// <see cref="GalaxyMapConfirmHook"/>'s refusal can finally be exercised without a human
    /// clicking. Takes no arguments. Local only.
    ///
    /// ⚠ 65, next free after every noted reserved range through 64 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapOpen = 65;

    /// <summary>The counterpart to <see cref="CheatKindGalaxyMapOpen"/>, through
    /// <c>CGuiInGame::HideGalaxyMapGui</c>. Takes no arguments; already-closed answers APPLIED,
    /// same reasoning <see cref="CheatKindCloseGui"/>'s own doc comment gives. Local only.
    ///
    /// ⚠ 66, next free after every noted reserved range through 65 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapClose = 66;

    /// <summary>
    /// Reports <see cref="GalaxyMapConfirmHook"/>'s own <c>RefusedCount</c>/<c>AllowedCount</c>
    /// counters and whether the splice installed — no engine call, a pure read of two static
    /// counters and one static bool. This is what the research doc's own "Live test plan" named
    /// as the missing piece: the counters existed but nothing on the console could read them.
    /// Takes no arguments. Local only.
    ///
    /// ⚠ 67, next free after every noted reserved range through 66 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapStats = 67;

    /// <summary>
    /// <c>spawncreature &lt;template&gt; [count] [x y [z]]</c> (alias <c>spawn</c>) — DEV-ONLY
    /// TEST PRIMITIVE (docs/research/creature-liveness.md's 2026-09-03 checklist item 6, and
    /// docs/research/peer-damage-ledger.md §9.5's own combat-staging need). Spawns
    /// <c>CheatAmount</c> hostile creatures (default 1, max 32) from the .utc template resref in
    /// <c>CheatText</c> through the engine's own CreateObject path (<c>Odyssey.SpawnCreature</c>
    /// — the same call <c>MaterialisedEnemies</c>/<c>PhantomController</c> already make for their
    /// own stand-ins), spread in a small grid (<see cref="SpawnCreatureGridRule"/>) next to the
    /// local player by default, or anchored on <c>CheatPosition</c> when it is not the origin
    /// sentinel — same convention <see cref="CheatKindMoveNear"/> already uses. Faction and every
    /// other field comes from the template itself; nothing is forced. Local only, and only the
    /// local instance spawns: on a CLIENT the spawned creatures are local-only bodies this
    /// instance owns, not the host's, so peer sync will not carry them — the intended use is on
    /// the HOST.
    ///
    /// ⚠ 68, next free after every noted reserved range through 67 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindSpawnCreature = 68;

    /// <summary>
    /// <c>crossdoor</c> — STAGING VERB (docs/research/cinematics.md's 2026-09-03 addendum): posts the SAME
    /// door-crossing event (type <c>0x1E</c>) the engine's own movement code posts when a
    /// creature's WalkUpdateLocation/AIActionMoveToPoint notices it has crossed into a door's
    /// sub-area — built exactly the way <c>CSWSCreature::AIActionMoveToPoint</c> builds it
    /// (0x005200C5-0x005201A7, see <c>Odyssey.PostDoorCrossingEvent</c>'s own header for the
    /// full disassembly), instead of queuing a walk and hoping the pathfinder reaches the
    /// threshold. Resolves and opens the door exactly like <see cref="CheatKindUseDoor"/> does
    /// (same tag/id/nearest-transition-door search, same "open it first if shut" step — a real
    /// crossing needs an open door exactly as much as a walk does, and gathers the party the
    /// same way, since <c>CSWSDoor::EventHandler</c>'s own <c>IsPartyTogether</c> gate at
    /// 0x0058C228 applies to this event exactly as it does to a walked one), then instead of
    /// queuing <c>ActionMoveToLocation</c> it constructs the <c>CScriptEvent</c> and hands it
    /// straight to <c>CServerAIMaster::AddEventDeltaTime</c> (0x004B08D0) itself.
    ///
    /// Built because <see cref="CheatKindUseDoor"/>'s walk has stalled 1.8-5.9 m short of the
    /// door on every live attempt across three separate rounds (docs/research/cinematics.md
    /// §8.3b), so the "a real crossing reaches the door's own transition arm" half of the
    /// warp-crash fix (docs/research/warp-crash-and-snapshot-lesson.md) has never actually been
    /// observed — this verb removes the pathfinder as a dependency entirely, exercising
    /// <c>CSWSDoor::EventHandler</c>'s transition arm (0x0058C0BE) the same way a real crossing
    /// would, without needing the walk to ever land. <see cref="OffsetCheatText"/> carries the
    /// door's tag, or <see cref="OffsetCheatTarget"/> its object id; both empty means "the
    /// nearest module transition door", identical to <see cref="CheatKindUseDoor"/>.
    ///
    /// ⚠ 69, next free after every noted reserved range through 68 (CheatKindSpawnCreature) as of 2026-09-03 — the branch had claimed 65, which the galaxy-map verbs took first.
    /// </summary>
    public const uint CheatKindCrossDoor = 69;

    /// <summary>
    /// <c>galaxyaccept</c> — LIVE-TEST PRIMITIVE (docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 `galaxyaccept` addendum): presses Accept on the galaxy map through the
    /// engine's own input dispatch (<c>CSWGuiInGameGalaxyMap::HandleInputEvent(panel, 0x27,
    /// 1)</c>, vtable slot 0x3C), the SAME shape <see cref="CheatKindSkipKey"/> already uses for
    /// the dialogue panel — the only route that reaches <see cref="GalaxyMapConfirmHook"/>'s
    /// splice, since <see cref="CheatKindGalaxyMapOpen"/>/<see cref="CheatKindGalaxyMapClose"/>
    /// only ever touch <c>ShowGalaxyMapGui</c>/<c>HideGalaxyMapGui</c> and never enter
    /// <c>HandleInputEvent</c> at all. Takes no arguments. Local only.
    ///
    /// ⚠ 70, next free after every noted reserved range through 69 (CheatKindCrossDoor) as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyAccept = 70;

    /// <summary>
    /// <c>galaxymap select &lt;n&gt;</c> — the planet-selection half
    /// <see cref="CheatKindGalaxyAccept"/> needs to have anything to confirm: writes
    /// <c>CSWPartyTable::SetSelectedPlanet(n)</c> (0x00563B60, VERIFIED full disasm) directly,
    /// independent of whether the map screen is open. <see cref="SharedBlock.CheatAmount"/>
    /// carries the planet index (a `planetary.2da` row, 0-15); out of range or
    /// unavailable/unselectable is reported as a no-op rather than applied — see
    /// <c>Odyssey.SetGalaxyMapSelectedPlanet</c>. Local only.
    ///
    /// ⚠ 71, next free after every noted reserved range through 70 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapSelect = 71;

    /// <summary>
    /// <c>galaxymap unlock &lt;n&gt;</c> — STAGING VERB: writes <c>CSWPartyTable</c>'s
    /// <c>available_planets[n]</c> (+0x60) and <c>selectable_planets[n]</c> (+0xA0) to 1 — the
    /// two arrays <c>SetSelectedPlanet</c> (0x00563B60, VERIFIED full disasm) gates on — so a
    /// following <see cref="CheatKindGalaxyMapSelect"/> succeeds on a save where the story has
    /// not unlocked that planet. Exists only so the real scripted departure can be exercised on
    /// the rig's Endar Spire saves; nothing in the shipped flow calls it. <see cref="CheatAmount"/>
    /// carries the planet index, 0-15. Local only.
    ///
    /// ⚠ 72, next free after every noted reserved range through 71 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapUnlock = 72;

    /// <summary>
    /// <c>galaxymap depart &lt;n&gt;</c> — runs the whole interplanetary leg to planet n on THIS
    /// instance in one frame: <see cref="CheatKindGalaxyMapUnlock"/> +
    /// <see cref="CheatKindGalaxyMapSelect"/> + <see cref="CheatKindGalaxyMapOpen"/> +
    /// <see cref="CheatKindGalaxyAccept"/> composed (<c>Odyssey.DepartToPlanet</c>), with
    /// <c>GalaxyMapConfirmHook</c>'s replay marker armed so the accept is neither refused under
    /// <c>follow_lead=host</c> nor republished as this player's own departure. The same call the
    /// session's follower path (<c>GalaxyDepartureController</c>) makes when a peer's departure
    /// arrives; exposed as a verb so the replay can be exercised on a single instance before
    /// trusting the relay. <see cref="CheatAmount"/> carries the planet index, 0-15. Local only.
    ///
    /// ⚠ 73, next free after every noted reserved range through 72 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGalaxyMapDepart = 73;

    /// <summary>
    /// <c>gamepad [status|on|off|cursor|sim ...|sim off]</c> — the controller layer's verb
    /// (docs/research/gamepad.md §5). The argument string arrives in the cheat text and
    /// <c>GamepadController.HandleConsole</c> answers with its status line, returned as the
    /// detail so the console prints it. Handled by <c>CheatController.Execute</c> BEFORE the
    /// "no player loaded" gate: the pad must be inspectable on the main menu. Local only.
    ///
    /// ⚠ 74, next free after every noted reserved range through 73 as of 2026-09-03.
    /// </summary>
    public const uint CheatKindGamepad = 74;

    /// <summary>
    /// <c>addclass &lt;class&gt;</c> — DEV VERB / LIVE-TEST PRIMITIVE for the class-grant relay
    /// (docs/research/class-and-power-grants.md): runs the engine's own <c>AddMultiClass</c> VM
    /// handler against the local player with <see cref="OffsetCheatAmount"/> as the
    /// <c>classes.2da</c> row, exactly as Dorak's <c>k_pdan_makejedi</c> would — NOT flagged as a
    /// replay, so <c>AddMultiClassHook</c> captures it and, under <c>class_grant_sync</c>, the
    /// session relays it to every peer. Local only.
    ///
    /// ⚠ 75, next free after 74 as of 2026-09-18.
    /// </summary>
    public const uint CheatKindAddClass = 75;
}
