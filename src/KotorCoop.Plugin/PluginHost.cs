using System.Runtime.CompilerServices;
using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Runs inside swkotor.exe.
///
/// Two threads, with a strict division that matters:
///   - the WORKER thread only touches the shared block (heartbeat, diagnostics). It must
///     never call an engine function; the engine is not reentrant.
///   - the FRAME HOOK runs on the game's own thread and is the only place engine calls
///     happen: spawning phantoms and stepping them along.
///
/// Split into partials by concern: this file owns process-wide STATE, the EnsureStarted
/// entry point and logging. PluginHost.Startup.cs owns the hook-install sequence,
/// PluginHost.Controllers.cs owns construction of the controller set, PluginHost.Frame.cs
/// owns the two per-frame schedules (MenuFrame/OnFrame), and PluginHost.SessionLifecycle.cs
/// owns what happens when a co-op session connects/releases and when the world changes
/// under every controller at once.
/// </summary>
internal static unsafe partial class PluginHost
{
    private static readonly object Gate = new();
    private static bool _started;
    private static string _logPath = "kotorcoop-plugin.log";

    /// <summary>Guards every write AND every rotate against "kotorcoop-*.log": <see cref="Log"/>
    /// does a <see cref="File.AppendAllText(string,string)"/>, <see cref="RotateLogsIfNeeded"/>
    /// and <see cref="RotateLogsIfNeededMidSession"/> do a <see cref="File.Move(string,string,bool)"/>
    /// on the same paths, and interleaving an append with a rename could either lose the line
    /// (written to the path just as it is renamed out from under it) or resurrect it into the
    /// freshly-rotated file. Startup rotation takes this lock too even though nothing can be
    /// contending for it yet, so the discipline never has an exception.</summary>
    private static readonly object LogFileGate = new();

    /// <summary>Set by <see cref="RotateLogsIfNeededMidSession"/>; 0 until the worker loop's
    /// first pass. See <see cref="LogRotation.ShouldCheckPeriodically"/>.</summary>
    private static long _lastPeriodicLogCheckMs;

    /// <summary>Per-subsystem logs are this + channel + ".log" (see <see cref="Log"/>).</summary>
    private static string _channelPathPrefix = "kotorcoop-";
    private static SharedBlock* _block;
    private static SharedBlockView? _view;
    private static PhantomController? _controller;
    private static ItemController? _items;
    private static FocusKeeper? _focus;
    private static CombatController? _combat;
    private static LivenessReconcileController? _liveness;
    private static EnemyPresenceController? _enemyPresence;
    private static PartyPresenceController? _partyPresence;
    private static PartyFollowController? _partyFollow;
    private static IncomingDamageController? _incomingDamage;
    private static PeerEffectController? _peerEffects;

    private static PeerDamageController? _peerDamage;
    private static CompanionDamageController? _companionDamage;
    private static TestDriveController? _testDrive;
    private static CheatController? _cheats;
    private static DoorController? _doors;
    private static PlaceableController? _placeables;
    private static LootController? _loot;
    private static TravelController? _travel;
    private static PauseController? _pause;
    private static PauseKeyController? _pauseKey;
    private static JournalController? _journal;
    private static MapMarkerController? _mapMarkers;
    private static MapRevealController? _mapReveal;
    private static PortraitController? _portraits;
    private static BarkController? _barks;
    private static ItemTemplateSpawnController? _itemTemplateSpawns;
    private static LocalVarController? _localVars;
    private static InteractionLockController? _interactionLocks;
    private static PartyRosterController? _partyRoster;
    private static PartySelectSuppressionController? _partySelectSuppression;
    private static CompanionEquipSuppressionController? _companionEquipSuppression;
    private static ControlReturnController? _controlReturn;
    private static MenuLoadController? _menuLoad;

    /// <summary>Watches for the character retail chargen writes, for a joiner who has no save
    /// to carry one out of (coop.ini <c>join_mode = newcharacter</c>).</summary>
    private static NewCharacterCaptureController? _newCharacterCapture;

    private static PostAdoptEquipRepairController? _postAdoptEquipRepair;
    private static CarriedPinReleaseController? _carriedPinRelease;
    private static GhostSweepController? _ghostSweep;
    private static EnemyScalingController? _enemyScaling;

    /// <summary>The `newgame` / `chargen quick` / `chargen accept` verbs — the engine's own
    /// character-creation button handlers, called rather than clicked.</summary>
    private static CharGenDriveController? _chargenDrive;
    private static ConnectMenuController? _connectMenu;
    private static ChatInputController? _chatInput;
    private static GamepadController? _gamepad;
    private static PasswordPromptController? _passwordPrompt;
    private static ServerBrowserController? _browser;
    private static NativeBrowserController? _nativeBrowser;
    private static DeathController? _death;
    private static SpectateController? _spectate;
    private static ChatOverlayController? _chatOverlay;
    private static ConsoleAssistController? _consoleAssist;
    private static DialogueSpectateController? _dialogueSpectate;
    private static EncounterController? _encounters;
    private static TriggerRelayController? _triggerRelay;
    private static MidModuleMovieController? _movieRelay;
    private static GalaxyDepartureController? _galaxyDeparture;
    private static ClassGrantController? _classGrant;
    private static JediChoicePromptController? _jediChoicePrompt;
    private static EnemyEffectController? _enemyEffects;
    private static PlacedMineController? _placedMines;

    /// <summary>The party-placed mine controller, for the cheat verbs (placemine/minestate/placedmineaction).</summary>
    internal static PlacedMineController? PlacedMines => _placedMines;
    private static AgentLivenessController? _agentLiveness;
    private static ConnectionIndicatorController? _connectionIndicator;
    private static MinigameNoticeController? _minigameNotice;
    private static SoloSegmentController? _soloSegment;
    private static SoloSegmentSpectateController? _soloSegmentSpectate;
    private static MinigameLockController? _minigameLock;
    private static Thread? _worker;
    private static volatile bool _running = true;

    /// <summary>The swap-hook frame count last seen from MenuFrame, and how many MainLoop
    /// ticks it has sat unchanged — the "is the swap hook dormant?" probe the seamless
    /// hold's fallback heartbeat uses.</summary>
    private static long _heldFrameSample;
    private static int _heldStallTicks;

    public static nint BlockAddress => (nint)_block;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            string directory = NativeMethods.GetGameDirectory();
            _logPath = Path.Combine(directory, "kotorcoop-plugin.log");
            _channelPathPrefix = Path.Combine(directory, "kotorcoop-");

            // Before any controller is constructed: several read their settings from coop.ini
            // in their field initialisers.
            PluginConfig.UseDirectory(directory);

            // Before the first Log() call, so a session that rotates does not write its own
            // banner and then immediately move it. See LogRotation for why: AppendAllText
            // never truncates, and a live test rig's combined log reached 170 MB across 158
            // sessions before this existed.
            RotateLogsIfNeeded(directory);

            Log($"=== KotorCoop plugin loaded into pid {NativeMethods.GetCurrentProcessId()} ===");

            // Linux/Proton: NEEDS-LIVE, no Wine box exists to confirm this against — see
            // KotorCoop.Memory.WineDetection's own doc comment for the detection method.
            if (KotorCoop.Memory.WineDetection.IsWine)
            {
                string? host = KotorCoop.Memory.WineDetection.HostDescription;
                Log(host is null
                    ? $"running under Wine {KotorCoop.Memory.WineDetection.Version}"
                    : $"running under Wine {KotorCoop.Memory.WineDetection.Version} ({host})");
            }

            // One-time diagnostic: name any coop.ini key that is not a player setting (a
            // developer switch, or something no reader recognises at all) — see
            // PluginConfig.LogNonPlayerKeys and docs/CONFIG.md's "Developer switches" table.
            PluginConfig.LogNonPlayerKeys(Log);

            // So a subscriber exception PluginEvents catches on a later raise still reaches
            // the log instead of being dropped silently. Wired before a single hook or
            // controller exists, same discipline as CrashProbe just below: nothing here can
            // raise an event yet, but nothing after this point should be able to raise one
            // without a working log sink either.
            PluginEvents.LogFailure = Log;

            // FIRST, before a single hook or controller exists. A fault during startup is
            // exactly as fatal as one during play, and the probe is what turns "the game
            // vanished" into a line naming the exception.
            InstallCrashProbe(directory);

            // The in-game ~ console needs the engine's dev console, which is gated by
            // EnableCheats in swkotor.ini. Enabling it is the game's own sanctioned surface.
            EnsureCheatsEnabled(directory);

            if (!AllocateBlock())
            {
                Log("FATAL: could not allocate the shared block");
                return;
            }

            ConstructControllers(directory);

            InstallHooks();

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KotorCoop plugin",
            };

            _worker.Start();
            Log("worker thread started");

            // Last, once everything the agent looks for exists: start the agent for this
            // instance so installing the plugin is the whole installation. Opt-out in
            // coop.ini; a missing agent executable is simply skipped.
            AgentLauncher.EnsureRunning(directory);
        }
        catch (Exception ex)
        {
            // Never let plugin failure take the game down: the export must still forward.
            Log($"FATAL during startup: {ex}");
        }
    }

    private static bool AllocateBlock()
    {
        // VirtualAlloc rather than a managed allocation: the block must sit at a fixed
        // address in a MEM_PRIVATE committed region for the whole session, because the
        // external agent locates it by scanning that memory for the signature.
        nint memory = NativeMethods.VirtualAlloc(
            0,
            SharedBlock.TotalSize,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageReadWrite);

        if (memory == 0)
        {
            return false;
        }

        _block = (SharedBlock*)memory;
        Unsafe.InitBlockUnaligned(_block, 0, SharedBlock.TotalSize);

        _block->Magic = SharedBlock.MagicValue;
        // ⚠ ONE NUMBER IN TWO FILES. KotorGameBridge.PluginSignature embeds this byte in the
        // pattern the agent scans for, so bumping only one end reads as "plugin shared block
        // not found" — which looks like a broken install rather than a version skew.
        // 37 = the load-screen texture in the browser details array (SharedBlock, version 37).
        // 38 = the engine-resolved player pointer.
        // 39 = per-instance item identity in the item slot's tail (SharedBlock, version 39).
        // 41 = the four `setitem` identity fields at the cheat region's tail (version 41).
        // 42 = the transition-cinematic channel, and the block grown to 0x3A00 to hold it
        //      (SharedBlock, version 42) — so both players can see the same movie.
        // 43 = the agent-written liveness heartbeat (SharedBlock.OffsetAgentHeartbeat).
        // 44 = the peer-effect channel (stun/paralyze/knockdown/slow/frightened/sleep/choke/
        //      Force push/whirlwind forwarded to a peer's phantom), block grown to 0x3C00 with
        //      its own ring at 0x3A00 — see docs/research/effect-sync.md.
        // 45 = the creature-liveness reconciliation channel (every creature the enemy owner's
        //      engine knows about, not just the 32-slot combat census), block grown to 0x4000
        //      with its own 96-entry array at 0x3C00 — see docs/research/creature-liveness.md.
        // ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves now fails the build on
        // a half-bump; it did not exist for 41.
        // 46 = the generic trigger relay region at 0x3B88 — see docs/research/trigger-relay.md.
        // 47 = the transition-announce channel + client-door-refusal arm at 0x38E4..0x3928 —
        //      see docs/research/transition-smoothness.md.
        // 48 = the mid-module movie relay region at 0x392C..0x396C (the DETECTION half
        //      docs/research/trigger-relay.md §6 shipped without a hook for) — see
        //      docs/research/cinematics.md's 2026-08-22 section.
        // 49 = the world-load edge at 0x396C (roadmap item 14 phase 2's save-vs-load
        //      detection primitive) — see docs/research/session-save.md's Phase 2 section.
        // 50 = container-lock target ids at 0x3F08..0x3F1C, InteractionKind.Container's own
        //      object-id key parallel to the existing tag-keyed interaction lock region — see
        //      docs/research/container-sync.md.
        // 51 = the local/remote dialogue-forced bit at 0x3F1C..0x3F24 — whether this
        //      conversation is one the engine itself forces regardless of range, distinct
        //      from a player's own click — see docs/research/dialogue-sync.md.
        // 52 = the mid-session-load-request flag at 0x3F24 (the `loadgame` verb) — tells
        //      MenuLoadController to replay CSWGuiSaveLoad::LoadGame's bFromMainMenu==0
        //      branch (DestroyServer + UnloadModule before CreateServer, guiManager from
        //      [clientInternal+0x274]) instead of acking-and-mooting when a server already
        //      exists — see docs/research/session-save.md's dated entry.
        // 53 = party-roster body identity at 0x3F28..0x3F70 (two nine-slot object-id arrays,
        //      local and host) — lets a peer recognise its own already-placed, GIT-identical
        //      copy of a just-recruited companion and reuse it instead of spawning a duplicate
        //      body next to it. See docs/research/dialogue-consequences.md's 2026-08-24 "stale
        //      mirrored body" entry. NEEDS-LIVE.
        // 54 = companion equipment at 0x4000..0x40C0 (block grown 0x4000 -> 0x4100): the host's
        //      real per-companion gear (body/right weapon/left weapon resrefs), keyed by
        //      presence slot index, so a client mirroring a companion under
        //      companion_local_combat dresses it correctly instead of wearing whatever it had
        //      when the two instances last agreed. See docs/research/companion-state-sync.md.
        //      NEEDS-LIVE.
        // 55 = the effective-behaviour-override flags at 0x40C0..0x40C8 (no TotalSize growth —
        //      the companion-equipment array's own tail leaves 0x40 free bytes before 0x4100):
        //      lets a client's adopted host-authoritative overrides for effect_sync,
        //      trigger_relay, liveness_sync, map_reveal_sync and dialogue_recruit_sync actually
        //      reach the plugin controllers that gate on them, closing the gap
        //      docs/research/mod-compatibility.md's "Behaviour-flag negotiation" section named.
        //      NEEDS-LIVE.
        // 56 = solo-segment spectate at 0x40C8 (one dword level, "this instance's local player
        //      is the active owner of a story-forced solo segment"). See
        //      SharedBlock.SoloSegment.cs and docs/research/star-forge-finale.md. NEEDS-LIVE.
        // 57 = container-lock explicit arbitration at 0x40D0..0x40E0 (no TotalSize growth): a
        //      plugin -> agent request (target id + serial) and an agent -> plugin response
        //      (serial + granted bit), closing the container-loot race by asking the host to
        //      arbitrate a fresh click BEFORE the engine's own OpenInventory runs, instead of
        //      leaving it to the eventually-consistent per-tick claim/lock level to converge.
        //      See docs/roadmap-seamless.md Tier 1 item 4 and docs/research/container-sync.md's
        //      container_lock_arbitration section. NEEDS-LIVE.
        // 58 = the item-template-spawn ring at 0x4100..0x4604 (TotalSize grown to 0x4700): a
        //      bounded (id, resref) ring with a write cursor, fed by ItemTemplateSpawnHook's
        //      detour on CSWVirtualMachineCommands::ExecuteCommandCreateItemOnObject
        //      (0x0052F570) — closes the case a live CSWSItem can never answer on its own
        //      (docs/research/item-identity.md §12: no TemplateResRef field exists), for any
        //      item a vanilla script (or scriptgive/giveloot) creates directly in inventory.
        //      See SharedBlock.ItemTemplateSpawns.cs and docs/research/item-identity.md §13.
        //      NEEDS-LIVE.
        // 59 = widened each item-template-spawn ring entry (0x14 -> 0x18) with a per-entry
        //      "created by our own cheat, not a vanilla script" flag, set by
        //      ItemTemplateSpawnHook.NextSpawnIsCheat around the two GiveItemToInventory/
        //      EquipItemFromTemplate call sites that reach CreateItemOnObject. Feeds
        //      party_pool_sync's ambient-script-noise suppression — see
        //      SharedBlock.ItemTemplateSpawns.cs and docs/research/party-pool-sync.md §20.
        //      NEEDS-LIVE, flag stays OFF.
        // 60 = 2026-08-29 seamless wave: minigame peer lock (0x4704/0x4708,
        //      SharedBlock.MinigameLock.cs), new-character join capture handshake (0x4780..
        //      and 0x47C0, SharedBlock.JoinChargen.cs), cheat kinds 53 closegui, 56 followstall,
        //      59 skipkey, 60 quicksave. Live-verified — docs/live-verification.md (2026-08-29).
        // 61 = 2026-09-02 combat wave: N-slot local_combat — the excluded-id array at
        //      0x4800..0x4900 (block grown to 0x4B00), SharedBlock.OffsetLocalCombatExcludedIds —
        //      widens local_combat from one locally-fought enemy to up to
        //      LocalCombatSlotCapacity (3), coop.ini local_combat_slots. See
        //      docs/research/combat-participation.md §11. NEEDS-LIVE.
        // 62 = same wave: per-attacker phantom-damage attribution (0x4900..0x4A00 plus
        //      0x4AA0..0x4AC0): a 4-entry, least-total-eviction table per phantom slot of
        //      (attackerId, cumulative) pairs (SharedBlock.OffsetPhantomAttackerSlots, plugin ->
        //      agent — what hit OUR phantoms), and a 4-entry merged owed-per-attacker table for
        //      the LOCAL player (SharedBlock.OffsetIncomingDamageAttackers, agent -> plugin), so
        //      IncomingDamageController can absorb/apply a delta PER ATTACKER instead of billing
        //      a whole pump window's damage to whichever id landed last. The legacy scalar
        //      total/last-attacker fields next to each stay live as the fallback for an older
        //      reader. NEEDS-LIVE. See docs/research/peer-damage-ledger.md §7.
        // 63 = same wave: companion damage TAKEN at 0x4A00..0x4A90. PhantomHitHook's companion
        //      watch + CompanionDamageController close the gap
        //      docs/research/companion-state-sync.md §13 names: a companion
        //      companion_local_combat releases to fight on a client took real hits with no
        //      channel back to the host, so its authoritative body never saw any of it and the
        //      next mirror pass overwrote the lower figure — effectively invulnerable, and a
        //      downed companion popped back up. See SharedBlock.CompanionDamage.cs. NEEDS-LIVE.
        //      The three regions were developed on parallel branches, each claiming its own
        //      0x100 range so the merge stayed textually clean; 63 is the union.
        // 64 = 2026-09-02: the item-template-spawn ring's flags dword now also carries the
        //      stack count each CreateItemOnObject call asked for (bits 8..23,
        //      SharedBlock.ItemTemplateSpawnStackCountShift). No layout change — same stride,
        //      same offsets — but the agent's party-pool ambient suppression now subtracts
        //      that count instead of the whole stack, and an older plugin's 0 there would
        //      read as "unknown"; severing the pair is the honest outcome. LIVE-VERIFIED the
        //      same day. See docs/research/shared-inventory-v2.md §17.
        // 65 = 2026-09-02: the materialised-owner-id table at 0x4B00..0x4B80 (TotalSize grown
        //      to 0x4C00), SharedBlock.OffsetMaterialisedOwnerIds — MaterialisedEnemies' own
        //      owner-id table, published verbatim, so the agent can tell BEFORE spending a
        //      giveloot round trip whether an owner id names one of our own stand-ins or (the
        //      overwhelmingly common case) a peer holding the real, GIT-identical creature.
        //      Closes the wasted-round-trip noise measured live the same day: six refused
        //      gives in one second on a client rejoining after a wipe reload. NEEDS-LIVE. See
        //      docs/research/enemy-materialisation.md's 2026-09-02 section.
        // 66 = 2026-09-03: the threat-retarget diagnostics region at 0x4B80..0x4C00
        //      (SharedBlock.OffsetThreatDebugCount/OffsetThreatDebugGeneration/
        //      OffsetThreatDebugRows) — fits the materialised-owner-id table's own leftover
        //      tail, so TotalSize stayed 0x4C00. Diagnostics only: dumps what
        //      PeerDamageController's ThreatTable currently believes for `tools/blockpoke.ps1
        //      threat`; the agent never reads this region. coop.ini `threat_retarget` itself
        //      (default OFF, NEEDS-LIVE) needs no wire/protocol change — see
        //      docs/research/peer-damage-ledger.md §9.
        // 67 = 2026-09-03: the galaxy-map departure relay at 0x4C00..0x4C18 (TotalSize grown
        //      to 0x4D00), SharedBlock.OffsetGalaxyDeparture*/OffsetGalaxyDepart*. The leader's
        //      accept click publishes the planet (GalaxyMapConfirmHook, the one moment of an
        //      interplanetary leg the frozen main thread leaves observable); the follower
        //      replays the same scripted leg natively (GalaxyDepartureController ->
        //      Odyssey.DepartToPlanet). Closes docs/limitations.md #15's "the client cannot
        //      follow a galaxy-map departure". NEEDS-LIVE at the time of the bump. See
        //      docs/research/galaxy-map-and-party-select.md's 2026-09-03 relay addendum.
        // 68 = 2026-09-18: the class-grant relay at 0x4C20..0x4C38 (fits the galaxy region's
        //      free tail, TotalSize stays 0x4D00), SharedBlock.OffsetClassGrant*. A script's
        //      AddMultiClass(nClass, GetPC()) — Dorak's Jedi path choice, danm13 — is captured
        //      at the VM handler (AddMultiClassHook), relayed (ClassGrantPacket) and replayed
        //      on the peer's own player through the same handler (Odyssey.AddMultiClass,
        //      ClassGrantController). coop.ini class_grant_sync, default OFF. Closes
        //      docs/review-seamless-2026-09-18.md blocker #1 statically; NEEDS-LIVE. See
        //      docs/research/class-and-power-grants.md.
        //  69: the resolved player-count scaling words (SharedBlock.PlayerCountScaling.cs,
        //      0x4C38..0x4C40, no TotalSize growth): the agent resolves coop.ini
        //      enemy_health/enemy_damage/xp_rate/store_stock/store_prices to percents and pushes
        //      the plugin-side combat and store price words. NEEDS-LIVE at
        //      the time of the bump. See docs/research/player-count-scaling.md.
        //  70: the client-cast enemy-effect relay (SharedBlock.EnemyEffect.cs, 0x4C60..0x4C80, no
        //      TotalSize growth): two 12-byte mailboxes and two ack dwords. A non-damage effect a
        //      client casts on a mirrored enemy is captured at the effect dispatcher
        //      (EnemyEffectCapture, off PhantomEffectHook), sent to the host (EnemyEffectPacket,
        //      protocol 58) and applied to the enemy's real body (EnemyEffectController ->
        //      OdysseyEffects). coop.ini enemy_effect_sync, default OFF; NEEDS-LIVE. See
        //      docs/research/enemy-effects.md.
        //  71: companion presence slots use their existing +0x24 padding as a publication
        //      sequence. Readers accept only stable even generations, so a position and
        //      facing cannot come from different agent updates. No size/offset growth.
        // 72 adds the sampled runtime difficulty fields in SharedBlock.Difficulty.cs.
        // 73 adds the agent-published local session role word at 0x4E44.
        // 74 adds the read-only exact native load-request provenance mailbox at 0x4E50.
        // 75 adds the resolved store price percent at 0x4C40 and the item-spawn provenance arrays.
        // 76 adds the exact enemy-health baseline channel (world/export page, verdict/import
        //    page, native save-request mailbox) at 0x5400..0x57C0 and grows TotalSize to 0x5800.
        //    See SharedBlock.EnemyScalingBaseline.cs and docs/research/enemy-health-persistence.md.
        // 77 adds per-object local-variable replication (SharedBlock.LocalVars.cs,
        //    0x5800..0x5A00, TotalSize 0x5A00): a 16-row capture ring LocalVarSyncHook fills
        //    from ExecuteCommandSetLocalValue and a 28-row seqlocked apply list for
        //    LocalVarController. coop.ini local_var_sync, default OFF; NEEDS-LIVE. See
        //    docs/research/quest-sync.md §8.3.
        // 78 adds party-placed floor-mine
        //      replication at 0x5A00..0x5BB8 (SharedBlock.PlacedMines.cs; TotalSize grown to
        //      0x5C00 for the wave): capture and gone rings, the agent's roster level and a
        //      spawn mailbox. coop.ini placed_mine_sync, default OFF; NEEDS-LIVE. See
        //      docs/research/mines.md §14.
        // 79 is another in-flight stream's own reservation (0x5C00..0x6000, left untouched
        //      here on purpose — see SharedBlock.TotalSize's remarks).
        // 80 (2026-09-24, "no cap really") relocates and widens the enemy census: EnemyCapacity
        //      rises from 32 to 128 (a live GIT-file survey of all 117 shipped modules found the
        //      largest at 51, manm27aa — 2.5x headroom), and the five capacity-scaled tables
        //      (enemy slots, enemy presence, materialised-owner ids, damage-dealt and
        //      damage-owed ledgers) move into a new region at 0x6000..0x8600, past the reserved
        //      gap. See SharedBlock.Combat.cs, SharedBlock.TotalSize's own remarks and
        //      docs/research/creature-liveness.md.
        _block->Version = 80;

        // A guard dword after the version. Magic plus version alone was not selective
        // enough: a scan once matched something else at a lower address and the agent
        // silently anchored on it. 16 bytes makes a coincidental match implausible.
        *(uint*)((byte*)_block + 0x0C) = SharedBlock.GuardValue;

        // ⚠ The dialogue nodes start at -1, not at the zero the block was just filled with.
        // Zero is a VALID entry index — the first line of a conversation — so a block that has
        // never held one would otherwise be indistinguishable from one sitting at the top of a
        // dialogue. Caught by the node probe on the first live run: an instance that had never
        // talked to anybody reported node 0 with an empty partner tag.
        *(int*)((byte*)_block + SharedBlock.OffsetLocalDialogueNode) = -1;
        *(int*)((byte*)_block + SharedBlock.OffsetRemoteDialogueNode) = -1;

        WriteModuleName("kotor");

        Log($"shared block at 0x{memory:X} ({SharedBlock.TotalSize} bytes)");

        // ⚠ HERE, and nowhere later: InstallHooks() has not run, so .text is still the
        // pristine image the loader (or SteamStub) produced. See SharedBlock.ExeImage.cs.
        PublishExeImageDigest();
        return true;
    }

    /// <summary>
    /// Hashes the loaded executable image and publishes it for the agent's compatibility
    /// handshake. This is what lets a Steam player and a DRM-free player share a session:
    /// their FILES differ (SteamStub encrypts .text at rest), their running images do not.
    /// docs/research/steam-support.md §8.
    ///
    /// Best effort. A failure leaves the "known" flag at zero, which downgrades the
    /// executable check to a warning — never to a false match, and never to a crash: this
    /// runs inside the game's startup and a bad read here must not be what takes it down.
    /// </summary>
    private static void PublishExeImageDigest()
    {
        try
        {
            nint imageBase = NativeMethods.GetModuleHandle(null);
            if (imageBase == 0)
            {
                Log("exe image digest: GetModuleHandle(null) returned 0; executable identity unchecked");
                return;
            }

            ReadOnlySpan<byte> headers = new((void*)imageBase, ExeImageDigestRule.HeaderWindow);
            if (!ExeImageDigestRule.TryLocateText(
                    headers, out uint textRva, out uint textSize, out uint headerHash, out string diagnostic))
            {
                Log($"exe image digest: {diagnostic}; executable identity unchecked");
                return;
            }

            ReadOnlySpan<byte> text = new((void*)(imageBase + (nint)textRva), (int)textSize);
            uint digest = ExeImageDigestRule.HashText(headerHash, text);

            *(uint*)((byte*)_block + SharedBlock.OffsetExeImageDigest) = digest;
            *(uint*)((byte*)_block + SharedBlock.OffsetExeImageDigestKnown) = 1;

            Log($"exe image digest {digest:X8} over .text 0x{imageBase + (nint)textRva:X}"
                + $"..0x{imageBase + (nint)textRva + (nint)textSize:X} ({textSize} bytes), taken before any hook");
        }
        catch (Exception ex)
        {
            Log($"exe image digest failed ({ex.Message}); executable identity unchecked");
        }
    }

    private static void WriteModuleName(string value)
    {
        byte* field = (byte*)_block + 0x20;
        Unsafe.InitBlockUnaligned(field, 0, SharedBlock.ModuleNameLength);

        int count = Math.Min(value.Length, SharedBlock.ModuleNameLength - 1);
        for (int i = 0; i < count; i++)
        {
            char c = value[i];
            field[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }
    }

    /// <summary>
    /// Ensures <c>[Game Options] EnableCheats=1</c> in swkotor.ini so the grave/tilde key
    /// opens the engine's developer console (which the co-op ~ overlay reads). Idempotent and
    /// conservative: it touches only that one key, and does nothing if the ini is absent
    /// (the game recreates it with cheats off, but never silently — the log says so).
    /// </summary>
    private static void EnsureCheatsEnabled(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "swkotor.ini");
            if (!File.Exists(path))
            {
                Log("swkotor.ini not found; set [Game Options] EnableCheats=1 to use the ~ console");
                return;
            }

            List<string> lines = [.. File.ReadAllLines(path)];
            int section = -1;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    if (trimmed.Equals("[Game Options]", StringComparison.OrdinalIgnoreCase))
                    {
                        section = i;
                    }
                    else if (section >= 0 && keyLine < 0)
                    {
                        break; // left the section without finding the key
                    }
                }
                else if (section >= 0 && trimmed.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase))
                {
                    keyLine = i;
                    break;
                }
            }

            if (keyLine >= 0)
            {
                if (lines[keyLine].Trim().EndsWith('1'))
                {
                    return; // already enabled
                }

                lines[keyLine] = "EnableCheats=1";
            }
            else if (section >= 0)
            {
                lines.Insert(section + 1, "EnableCheats=1");
            }
            else
            {
                lines.Add("[Game Options]");
                lines.Add("EnableCheats=1");
            }

            File.WriteAllLines(path, lines);
            Log("enabled the developer console in swkotor.ini ([Game Options] EnableCheats=1)");
        }
        catch (Exception ex)
        {
            Log($"could not enable cheats in swkotor.ini: {ex.Message}");
        }
    }

    /// <summary><see cref="Environment.TickCount64"/> as of the worker thread's last pass
    /// (it sleeps 16 ms between passes, so this is never more than a few tens of milliseconds
    /// old while the worker is healthy). Read from the GAME thread by the frame hook's own
    /// half of the stall watchdog — see <c>PluginHost.Frame</c>'s <c>WatchWorkerThread</c> —
    /// so that "the worker died" and "the worker is fine and quiet" are distinguishable in the
    /// log instead of looking identical. 0 until the worker's first pass.
    ///
    /// ⚠ ALWAYS ACCESSED THROUGH <see cref="Interlocked"/>, never a plain read/write. This is
    /// an 8-byte field crossing threads on an x86 (32-bit) process, where a bare read/write of
    /// a <c>long</c> is not atomic — a plain read on the game thread could observe a torn value
    /// (half the old tick, half the new one) while the worker is mid-write. The two threads are
    /// otherwise strictly divided (the worker only touches the shared block; see
    /// docs/ARCHITECTURE.md); this field is the one exception, so the access itself has to be
    /// safe rather than relying on the division.</summary>
    internal static long WorkerLastTickMs;

    private static void WorkerLoop()
    {
        uint lastRemoteCount = 0;
        uint lastPhantomCount = 0;
        long lastFrames = 0;
        int quietTicks = 0;
        long quietSinceMs = 0;

        while (_running)
        {
            try
            {
                // The other half of the two-sided stall watchdog: the frame hook reads this to
                // tell "the worker stopped" apart from "the worker is running and has nothing
                // to say". Written before anything below can throw, so a worker that is alive
                // but erroring still counts as alive. Interlocked.Exchange, not a plain store —
                // see the field's own doc comment: a bare 64-bit write is not atomic on x86.
                Interlocked.Exchange(ref WorkerLastTickMs, Environment.TickCount64);

                _block->Heartbeat++;

                // The crash probe's handler is machine code and cannot format a log line
                // itself (see CrashProbe: the managed version of it WAS the crash). It leaves
                // a record behind; this is where that record becomes words.
                CrashProbe.DrainReports();

                // Throttled internally to ~once per LogRotation.PeriodicCheckIntervalMs; a
                // no-op call on every other pass through this ~16ms loop.
                RotateLogsIfNeededMidSession();

                if (_view is { } view)
                {
                    uint remotes = view.RemoteCount;
                    if (remotes != lastRemoteCount)
                    {
                        lastRemoteCount = remotes;
                        Log($"agent reports {remotes} remote peer(s)");
                    }

                    uint phantoms = view.PhantomCount;
                    if (phantoms != lastPhantomCount)
                    {
                        lastPhantomCount = phantoms;
                        Log($"{phantoms} phantom(s) alive");
                    }

                    // Report a stalled frame hook once rather than every tick. If the hook
                    // is installed but never fires, everything else silently does nothing,
                    // and that is worth saying out loud.
                    //
                    // This is the ONE thing still moving during a cinematic: a movie blocks
                    // the main thread inside WaitForMovie, so the swap never happens and this
                    // warning is the expected — not anomalous — reading for its whole
                    // duration. The line says so, because a reader who does not know that
                    // will spend the length of a movie chasing a hook that is fine.
                    // See docs/research/cinematics.md and CinematicWatch.
                    long frames = FrameHook.FrameCount;
                    if (FrameHook.IsInstalled && frames == lastFrames)
                    {
                        if (++quietTicks == 120)
                        {
                            quietSinceMs = Environment.TickCount64 - 2000;
                            Log("WARNING: frame hook installed but has not fired for ~2s "
                                + "(normal while a movie or a module load holds the main "
                                + "thread, or while the game's window is inactive and the "
                                + "engine has parked its own main loop)");
                        }
                    }
                    else
                    {
                        // ⚠ SAY WHEN IT CAME BACK, AND FOR HOW LONG IT WAS GONE. The warning
                        // above used to be the whole story, and a one-shot line with no
                        // closing bracket cannot answer the question anyone actually asks of
                        // it — "was the plugin dead, or was there simply nothing to log?".
                        // 2026-09-16 that ambiguity cost a live session: this file went
                        // completely silent from 23:10 to 02:09 (the machine's displays slept
                        // and the engine parked its main loop), and the silence was read as a
                        // stalled or crashed plugin worker, which it was not — the console
                        // still answered and the peer's phantom was still being driven the
                        // moment the window came back. A resume line with a duration makes
                        // that a one-grep answer instead of a reproduction attempt.
                        if (quietSinceMs != 0)
                        {
                            Log("frame hook: firing again after "
                                + $"{(Environment.TickCount64 - quietSinceMs) / 1000.0:F1}s "
                                + "of silence (the plugin worker never stopped; nothing below "
                                + "this gap ran on the game thread while it lasted)");
                            quietSinceMs = 0;
                        }

                        quietTicks = 0;
                        lastFrames = frames;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"worker error: {ex.Message}");
            }

            Thread.Sleep(16);
        }

        Log("worker stopping");
    }

    /// <summary>
    /// The subsystems that get their own log file.
    ///
    /// One 15 MB file with every subsystem interleaved is unreadable when you are chasing
    /// one of them, which is the whole complaint. Rather than retro-fit a channel argument
    /// onto ~200 call sites, this uses the prefix convention the messages ALREADY follow
    /// ("dialogue: ...", "pause: ...", "combat: ..."), so a line files itself.
    ///
    /// The list is explicit on purpose: a free-form prefix would spawn a new file for every
    /// typo and every message that merely happens to contain a colon.
    /// </summary>
    private static readonly string[] LogChannels =
    [
        "dialogue", "pause", "combat", "bark", "phantom", "item", "loot", "door",
        "journal", "minimap", "death", "transition", "epoch", "party", "enemy",
        "chat", "travel", "portrait", "encounter", "hook", "agent", "session",
        "cinematic",
    ];

    /// <summary>Prefixes that name the same subsystem as one of the channels above.</summary>
    private static string CanonicalChannel(string prefix) => prefix switch
    {
        "death handling" => "death",
        "phantoms" => "phantom",
        "barks" => "bark",
        "items" => "item",
        "doors" => "door",
        "frame hook" or "menu frame hook" or "console interp" => "hook",
        _ => prefix,
    };

    /// <summary>
    /// Files a line by its own prefix. Returns "general" for anything unprefixed — startup
    /// banners, fatals, and the handful of messages that never adopted the convention.
    /// </summary>
    private static string ChannelFor(string message)
    {
        int colon = message.IndexOf(':');

        // A timestamp or an address would otherwise look like a prefix; a real one is a
        // short lowercase word or two, and never contains a digit.
        if (colon <= 0 || colon > 20)
        {
            return "general";
        }

        string prefix = CanonicalChannel(message[..colon].ToLowerInvariant());

        return Array.IndexOf(LogChannels, prefix) >= 0 ? prefix : "general";
    }

    /// <summary>The `gamepad` console verb, from CheatController (main thread, the cheat
    /// drain). Returns the one-line reply the console prints.</summary>
    public static string OnGamepadConsole(string arguments)
    {
        try
        {
            if (_gamepad is { } gamepad)
            {
                return gamepad.HandleConsole(arguments);
            }

            const string NotReady = "gamepad: the plugin has not finished starting; try again in a moment";
            Log(NotReady);
            return NotReady;
        }
        catch (Exception ex)
        {
            string failed = $"gamepad: console verb failed: {ex.Message}";
            Log(failed);
            return failed;
        }
    }

    /// <summary>The `input` console verb, from CheatController (main thread, the cheat
    /// drain). Returns the one-line reply the console prints. Unlike
    /// <see cref="OnGamepadConsole"/> this needs no per-instance state, so it calls straight
    /// into the static <see cref="InputInjectionController"/>.</summary>
    public static string OnInputConsole(string arguments)
    {
        try
        {
            return InputInjectionController.HandleConsole(arguments);
        }
        catch (Exception ex)
        {
            string failed = $"input: console verb failed: {ex.Message}";
            Log(failed);
            return failed;
        }
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

            // Same lock RotateLogsIfNeeded/RotateLogsIfNeededMidSession take around their
            // File.Move: without it, a rotate racing this append could lose the line or
            // write it into the file the rotate is in the middle of renaming away. See
            // LogFileGate's doc comment.
            lock (LogFileGate)
            {
                // The combined file stays the timeline: it is what you read when the
                // question is "what happened around 14:48", and what verify.ps1 and every
                // existing note grep. The split files are for reading ONE subsystem end to
                // end.
                File.AppendAllText(_logPath, line, Encoding.ASCII);
                File.AppendAllText(_channelPathPrefix + ChannelFor(message) + ".log", line, Encoding.ASCII);
            }
        }
        catch
        {
            // Logging must never throw into the game's call stack.
        }
    }

    /// <summary>
    /// Rotates every "kotorcoop-*.log" this process writes (the combined file and every
    /// channel split) that has grown past coop.ini's <c>log_max_mb</c> (default
    /// <see cref="LogRotation.DefaultCapMb"/>): the existing file becomes "*.1.log" and every
    /// generation already sitting there shifts up one (".1.log" -&gt; ".2.log", etc.) through
    /// coop.ini's <c>log_keep</c> (default <see cref="LogRotation.DefaultKeepGenerations"/>),
    /// dropping whatever sat in the oldest slot, so that many prior sessions' tails survive
    /// instead of only one and the fresh session starts a clean file. A file at or under the
    /// cap is left alone — sessions keep concatenating into it until it next crosses the line.
    ///
    /// Called once, here, before <see cref="Log"/> is ever called — nothing else can be
    /// contending for <see cref="LogFileGate"/> yet, this just establishes the discipline the
    /// mid-session sweep (<see cref="RotateLogsIfNeededMidSession"/>) relies on for the rest
    /// of the session.
    /// </summary>
    private static void RotateLogsIfNeeded(string directory)
    {
        try
        {
            lock (LogFileGate)
            {
                RotateLogFilesOverCap(directory);
            }
        }
        catch
        {
            // Best-effort, same as Log() itself: a rotation failure must not stop the
            // plugin from starting, it just means this session's log grows a bit more.
        }
    }

    /// <summary>
    /// The periodic sibling of <see cref="RotateLogsIfNeeded"/>, called from
    /// <see cref="WorkerLoop"/> roughly every <see cref="LogRotation.PeriodicCheckIntervalMs"/>
    /// (see <see cref="LogRotation.ShouldCheckPeriodically"/>) so a session that runs long
    /// enough to cross <c>log_max_mb</c> on its own is capped before the NEXT process start
    /// rather than only then — a live test rig's combined log once reached 170 MB across 158
    /// sessions specifically because rotation only ran at startup.
    ///
    /// Safe to run mid-session, unlike a naive move: it takes the exact same
    /// <see cref="LogFileGate"/> lock <see cref="Log"/> takes around its
    /// <see cref="File.AppendAllText(string,string)"/> calls, so a rotate here can never
    /// interleave with an in-flight write to the same path. A tool tailing the file externally
    /// (tools/verify.ps1) can still observe the rename as a gap, but that risk is accepted in
    /// exchange for a session that no longer grows unbounded between restarts.
    /// </summary>
    private static void RotateLogsIfNeededMidSession()
    {
        long now = Environment.TickCount64;
        if (!LogRotation.ShouldCheckPeriodically(_lastPeriodicLogCheckMs, now, LogRotation.PeriodicCheckIntervalMs))
        {
            return;
        }

        _lastPeriodicLogCheckMs = now;

        try
        {
            string? directory = Path.GetDirectoryName(_logPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            lock (LogFileGate)
            {
                RotateLogFilesOverCap(directory);
            }
        }
        catch
        {
            // Best-effort, same as Log() itself.
        }
    }

    /// <summary>
    /// The actual sweep shared by <see cref="RotateLogsIfNeeded"/> (once, at startup) and
    /// <see cref="RotateLogsIfNeededMidSession"/> (periodically, during play): every
    /// "kotorcoop-*.log" over cap has its generations shifted up via
    /// <see cref="LogRotation.ShiftPlan"/>/<see cref="LogRotation.ApplyShiftPlan"/> so
    /// <c>log_keep</c> prior generations survive instead of only one. Callers must already
    /// hold <see cref="LogFileGate"/>.
    ///
    /// Skips every already-rotated generation file (anything matching "*.N.log") — only a
    /// live "*.log" file is ever a rotation SOURCE, the shift plan is what produces the rest —
    /// and "kotorcoop-agent.log" on purpose: that file belongs to the agent process
    /// AgentLauncher spawns, and it rotates it itself right before opening it (see
    /// AgentLauncher.RotateAgentLogIfNeeded), not as part of this directory-wide sweep. It
    /// cannot be rotated mid-session by either side: see the comment on
    /// AgentLauncher.Launch's StartupInfo.StdOutput assignment.
    /// </summary>
    private static void RotateLogFilesOverCap(string directory)
    {
        long cap = LogRotation.CapBytes(PluginConfig.ReadInt("log_max_mb", LogRotation.DefaultCapMb));
        int keep = LogRotation.ClampKeepGenerations(
            PluginConfig.ReadInt("log_keep", LogRotation.DefaultKeepGenerations));

        foreach (string path in Directory.EnumerateFiles(directory, "kotorcoop-*.log"))
        {
            string name = Path.GetFileName(path);
            if (IsRotatedGenerationFileName(name)
                || name.Equals("kotorcoop-agent.log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FileInfo info = new(path);
            if (!info.Exists || !LogRotation.ShouldRotate(info.Length, cap))
            {
                continue;
            }

            LogRotation.ApplyShiftPlan(LogRotation.ShiftPlan(path, keep));
        }
    }

    /// <summary>True for a name already in "*.N.log" shape (e.g. "kotorcoop-plugin.2.log") —
    /// the segment before the final ".log" parses as a positive integer. Used to keep the
    /// directory sweep above from treating an already-rotated generation as a fresh rotation
    /// source; the shift plan is what moves those, never the enumeration itself.</summary>
    private static bool IsRotatedGenerationFileName(string name)
    {
        if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string withoutExtension = name[..^".log".Length];
        int dot = withoutExtension.LastIndexOf('.');
        return dot >= 0
            && int.TryParse(withoutExtension.AsSpan(dot + 1), out int generation)
            && generation > 0;
    }
}
