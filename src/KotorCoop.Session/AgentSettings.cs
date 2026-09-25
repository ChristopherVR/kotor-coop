using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Owns the coop.ini-derived knobs <see cref="CoopAgent"/> and its pumps read every
/// tick — cadences, sync toggles, and the escape-hatch flags for features still being
/// live-verified. Split out of CoopAgent.cs so the settings surface can be read without
/// pulling in the whole pump scheduler.</summary>
public sealed record AgentSettings
{
    /// <summary>How often the game's memory is sampled. Matches the network tick by default.</summary>
    public int PollRate { get; init; } = ProtocolConstants.DefaultTickRate;

    /// <summary>Mirror a peer's pause into this game (coop.ini pause_sync=0 opts out).</summary>
    public bool SyncPause { get; init; } = true;

    /// <summary>
    /// coop.ini <c>perf_trace</c> — emit <see cref="PerfTrace"/>'s once-a-second pump/network/
    /// phantom line into the agent log, and switch on
    /// <see cref="KotorCoop.Memory.MemoryTelemetry"/>'s cross-process call counting. OFF by
    /// default and meant to stay that way outside a measurement run: the counting itself is
    /// nearly free, but a line a second forever is not something a player's log wants.
    /// </summary>
    public bool PerfTrace { get; init; }

    /// <summary>
    /// ⚠ This is a LATENCY knob, not a freshness knob. A world-state pass still costs
    /// hundreds of milliseconds when a cache expires inside it, and it shares this thread
    /// with the position pump — so every pass is a gap in the peer's movement. Measured, those
    /// gaps are what makes a running peer's gait flicker between run and idle, because the
    /// engine only pushes an animation on change and the pump sees no movement across a stall.
    /// A second between passes keeps doors and enemies converging fine while leaving movement
    /// alone the rest of the time.
    /// </summary>
    public int HeavyPumpIntervalMs { get; init; } = 1000;

    /// <summary>
    /// How often the combat channels run: enemy presence, phantom damage both ways, and the
    /// party mirror. These used to ride the heavy pass, which meant a mirrored enemy moved
    /// on one-second-old truth — it covered a full second of running in one hop, and the
    /// plugin's smoother turned that hop into a visible glide across the room. Forwarded
    /// damage waited the same second. None of these reads is a world scan on the scale the
    /// heavy gate exists for: the enemy census is one pass over the creature table, the
    /// rest are shared-block reads, so a few times a second is affordable where the
    /// hundreds-of-milliseconds world-state passes are not.
    /// </summary>
    public int CombatPumpIntervalMs { get; init; } = 150;

    /// <summary>
    /// When true, remote players in a different module are not written into the game.
    /// KOTOR has exactly one module loaded at a time, so a peer on Taris has no
    /// meaningful position in your copy of the Endar Spire.
    /// </summary>
    public bool FilterByModule { get; init; } = true;

    /// <summary>
    /// While a peer's <c>PlayerState.FlagInMinigame</c> is set (a swoop race or a forced
    /// turret sequence), park their phantom instead of mirroring it — the same "not
    /// meaningfully in this world right now" treatment <see cref="FilterByModule"/> already
    /// gives a peer standing somewhere else. coop.ini <c>minigame_hide_peer</c> (default on).
    /// </summary>
    public bool HidePeersInMinigame { get; init; } = true;

    /// <summary>
    /// While a PEER is inside a minigame, hold this instance's own player still and show
    /// "&lt;name&gt; is racing - waiting" — and refuse to follow them onto the (single-seat)
    /// track module. The product rule the swoop race needs: one player races, the rest wait.
    /// coop.ini <c>minigame_lock_peers</c> (default on). See
    /// <see cref="KotorCoop.Protocol.MinigameLockRule"/> and docs/research/minigames.md §24.
    /// </summary>
    public bool LockPeersDuringMinigame { get; init; } = true;

    /// <summary>
    /// How long a minigame peer-lock may stand before it releases itself regardless of what
    /// the owner's flag still says. A peer whose agent died mid-race leaves its last published
    /// state behind forever, and "frozen for the rest of the session" must not be reachable.
    /// coop.ini <c>minigame_lock_timeout_s</c> (default 600 = ten minutes, comfortably longer
    /// than any race in the game). Zero disables the timeout.
    /// </summary>
    public int MinigameLockTimeoutMs { get; init; } = 600_000;

    /// <summary>
    /// Extra module names to treat as single-seat minigame tracks, on top of
    /// <see cref="KotorCoop.Protocol.MinigameModuleRule.BuiltIn"/> — for a mod that ships its
    /// own. coop.ini <c>minigame_modules</c> (comma-separated; empty by default).
    /// </summary>
    public IReadOnlyList<string> ExtraMinigameModules { get; init; } = [];

    /// <summary>
    /// When true, a client automatically travels to whatever module the host is in, the
    /// way a summoned phantom follows the world's owner. The host never follows anyone —
    /// it owns enemies and encounters, so its module is where the shared world happens.
    /// </summary>
    public bool FollowHostModule { get; init; } = true;

    /// <summary>How long to wait before re-requesting the same module, in milliseconds.
    /// A module load takes several seconds; re-sending during one would queue a second
    /// transition on arrival.</summary>
    public int FollowRetryMs { get; init; } = 15_000;

    /// <summary>
    /// How long a module must have been ours before the follow may take us out of it again.
    ///
    /// This is a CRASH guard, not a tuning knob: a transition issued 0.6 s after the last one
    /// completed killed the client in
    /// <c>CSWCMessage::HandleServerToPlayerCreatureUpdate_Add</c>, which dereferences
    /// <c>CClientExoApp::GetModule()</c> with no null check while creature-add messages for
    /// the world we just left are still being serviced. Zero disables it.
    /// </summary>
    public int ModuleSettleMs { get; init; } = 5000;

    /// <summary>
    /// When true, the party companions are reconciled: the host drives them and clients
    /// freeze and mirror. Uses the engine's solo-mode flag, whose full behaviour is
    /// inferred rather than measured, so it is an escape-hatchable default.
    /// </summary>
    public bool SyncParty { get; init; } = true;

    /// <summary>
    /// How near a peer must be for their pause to pause you too, in world units. A pause is
    /// for a situation you are both in; a partner across the module stopping to read their
    /// inventory should not freeze your fight. 0 shares every pause, whatever the distance.
    /// </summary>
    public float PauseShareRangeUnits { get; init; } = 30f;

    /// <summary>
    /// When true, party gold and PC experience are shared: the host publishes its absolute
    /// values and clients overwrite their own, so both parties level and spend together.
    /// Opinionated (it changes a client's own totals), so it is escape-hatchable.
    /// </summary>
    public bool SyncProgression { get; init; } = true;

    /// <summary>
    /// When true, an item a reward node places into the LOCAL conversation owner's inventory
    /// is diffed and published so the other peer's engine receives the same item through the
    /// give-loot cheat channel — docs/research/dialogue-consequences.md §3. coop.ini
    /// <c>dialogue_item_sync</c> (default on).
    /// </summary>
    public bool DialogueItemSync { get; init; } = true;

    /// <summary>
    /// When true, <see cref="Pumps.ScriptItemGrantPump"/> samples the local PC's inventory
    /// whenever this instance is NOT in a conversation and forwards whatever a script (an
    /// OnEnter trigger, an OnDeath script, a cutscene, a heartbeat script) just placed into it
    /// — the gap <see cref="DialogueItemSync"/> leaves, since that channel only ever diffs
    /// while THIS instance owns a conversation. coop.ini <c>script_item_sync</c>. ON by default
    /// since 2026-08-27 — docs/research/dialogue-consequences.md's script-item-sync section has
    /// the design and the live-test results: all three required controls (a genuine script
    /// grant, a manual local giveloot, and container/corpse loot — the one that actually failed
    /// on its first live run and needed a real fix, see <see cref="CoopAgent.PumpOnce"/>'s
    /// remarks next to its <c>PumpContainerLoot</c> call) are now live-confirmed suppressed
    /// correctly. Unlike every other item channel here, a false positive on this one (an
    /// ordinary ground/container pickup misread as a script grant) would mint a real duplicate
    /// item on the peer's engine, not just a cosmetic desync — which is exactly what the
    /// suppression ledger and its live controls exist to rule out before defaulting this on.
    /// </summary>
    public bool ScriptItemSync { get; init; } = true;

    /// <summary>
    /// coop.ini <c>inventory_mode</c> (personal/shared). When true (the default, "personal"):
    /// each engine's own party backpack stays its own — <see cref="Pumps.PartyPoolPumpV2"/>
    /// mirrors only a quest item (<see cref="QuestItemCatalog"/>) between peers, so a pickup,
    /// purchase, sale or drop of anything else is never mirrored. When false ("shared"): every
    /// stack still mirrors, byte-for-byte today's behaviour. HOST-AUTHORITATIVE — see
    /// <see cref="BehaviourDigest"/>'s <c>InventoryPersonal</c> flag and
    /// docs/research/personal-inventory.md.
    /// </summary>
    public bool InventoryPersonal { get; init; } = true;

    /// <summary>
    /// coop.ini <c>credits_mode</c> (personal/shared). When true (default when
    /// <see cref="InventoryPersonal"/> is also true — see <see cref="CoopConfig"/>'s parse):
    /// each side's own gold stays its own; <c>Pumps.PartyPump.PumpProgression</c> never folds a
    /// peer's earn/spend into a shared purse. When false: one merged purse, today's behaviour.
    /// HOST-AUTHORITATIVE, same reasoning as <see cref="InventoryPersonal"/>.
    /// </summary>
    public bool CreditsPersonal { get; init; } = true;

    /// <summary>
    /// Relay a class a script adds to one player's character (Dorak's Jedi path choice,
    /// <c>AddMultiClass</c>) to every other player's character through the same engine
    /// handler — docs/research/class-and-power-grants.md, coop.ini <c>class_grant_sync</c>.
    /// OFF by default: the plugin's VM-handler splice and the replay have never run on a live
    /// process (NEEDS-LIVE). The plugin reads the same key for its own half; both must agree
    /// (docs/CONFIG.md footnote ³'s rule).
    /// </summary>
    public bool ClassGrantSync { get; init; }

    /// <summary>
    /// Give every OTHER player their OWN class's lightsaber colour when any one player finishes
    /// Zhar's saber-construction reward, instead of leaving them with nothing (the reward
    /// equips into a weapon slot the same script tick it is created, so the generic dialogue/
    /// script item-grant diff never sees it — docs/research/class-and-power-grants.md §11
    /// records the live-confirmed miss). Detection is a <c>PlayerState</c> equip-slot edge;
    /// delivery is the existing world-progression flag channel (no new packet); the build/equip
    /// is <c>Pumps.FirstLightsaberBuildPump</c>'s own automatic <c>equip</c> cheat, run locally
    /// on each OTHER player's own engine from their OWN class — never a copy of the builder's
    /// colour. Purely local otherwise: no plugin/native change, no wire format change beyond
    /// one new flag id in the existing flag store. Coop.ini <c>first_saber_color_sync</c>. OFF
    /// by default (NEEDS-LIVE) — with it off, behaviour is exactly what it was before this
    /// setting existed.
    /// </summary>
    public bool FirstSaberColorSync { get; init; }

    /// <summary>
    /// Relay a non-damage effect a client casts on a mirrored enemy to the host, which replays
    /// it on the real body — docs/research/enemy-effects.md, coop.ini <c>enemy_effect_sync</c>.
    /// OFF by default (NEEDS-LIVE). The plugin reads the same key for its capture and apply
    /// halves; both machines must agree.
    /// </summary>
    public bool EnemyEffectSync { get; init; }

    /// <summary>
    /// Replicate party-placed floor mines — docs/research/mines.md §14, coop.ini
    /// <c>placed_mine_sync</c>. OFF by default (NEEDS-LIVE). The plugin reads the same key for its
    /// capture, spawn and fire-gate halves; both machines must agree.
    /// </summary>
    public bool PlacedMineSync { get; init; }

    /// <summary>How often <see cref="Pumps.ScriptItemGrantPump"/> re-samples the local PC's
    /// inventory once it has a baseline, in milliseconds — independent of
    /// <see cref="HeavyPumpIntervalMs"/> for the same reason
    /// <c>DialoguePump.DialogueGrantSampleIntervalMs</c> is: this is a full possessed-items
    /// sweep (<see cref="KotorCoop.Memory.IItemBridge.ReadPossessedItems"/>'s own doc calls it
    /// "not free"), and tying it to the heavy pass would mean an unrelated change to that
    /// interval silently changes how quickly a script grant is caught. Not a coop.ini key —
    /// exposed here purely so a test can zero it and observe a diff on the very next call
    /// instead of waiting on a real clock, the same reason <see cref="CombatPumpIntervalMs"/>
    /// and <see cref="HeavyPumpIntervalMs"/> are themselves settable.</summary>
    public int ScriptItemGrantSampleIntervalMs { get; init; } = 1000;

    /// <summary>
    /// When true, the dialogue channel (<see cref="Pumps.DialoguePump.PumpDialogue"/>) runs on
    /// the real-time (every-poll-tick) path instead of the <see cref="CombatPumpIntervalMs"/>
    /// cadence, and an unchanged read is deduped against a resend rather than republished on
    /// a fixed timer — see <see cref="KotorCoop.Protocol.DialogueTickGate"/>. coop.ini
    /// <c>dialogue_fast_join</c>, default ON: a live A/B on the exact skip-desync repro found
    /// the 150 ms cadence genuinely DROPS content on a rapid-skip burst, where the fast path
    /// caught every node. See docs/research/dialogue-join-latency.md and
    /// docs/research/dialogue-skip-desync.md.
    /// </summary>
    public bool DialogueFastJoin { get; init; } = true;

    /// <summary>
    /// When true, <see cref="Pumps.PartyPump.PumpParty"/> (and
    /// <see cref="Pumps.PartyPump.PumpPartyRoster"/>, same call site) runs on the real-time
    /// (every-poll-tick) path instead of waiting for <see cref="CombatPumpIntervalMs"/> — the
    /// same move <see cref="DialogueFastJoin"/> already made, removing one of two stacked
    /// 150 ms windows on the companion-mirror path (this pump, then
    /// <c>PartyPresenceController</c>'s own <c>mirror_smooth_ms</c> smoother). coop.ini
    /// <c>party_fast_pump</c>, default ON since a live A/B against a genuinely-walking
    /// companion measured a real ~45-55% cut in companion-position lag with no frame-time
    /// regression — see docs/research/lag-audit.md.
    /// </summary>
    public bool PartyFastPump { get; init; } = true;

    /// <summary>
    /// When true, <see cref="Pumps.WorldStatePump.PumpPlaceables"/> (the durable
    /// container/door open-or-locked STATE, as opposed to
    /// <see cref="Pumps.WorldStatePump.PumpInteractionLocks"/>'s open/close animation and
    /// use-lock, which already runs on <see cref="CombatPumpIntervalMs"/>) runs on the
    /// real-time (every-poll-tick) path instead of waiting for <see cref="HeavyPumpIntervalMs"/>
    /// — the same move <see cref="DialogueFastJoin"/> and <see cref="PartyFastPump"/> already
    /// made. Without this, a container's visible close (fast) and its durable closed STATE
    /// (slow) can disagree for up to a full heavy-pass cycle — see
    /// docs/research/container-sync.md. coop.ini <c>container_fast_pump</c> (default off, same
    /// A/B rollout shape as the other two).
    /// </summary>
    public bool ContainerFastPump { get; init; }

    /// <summary>
    /// When true, <see cref="Pumps.WorldStatePump.PumpMines"/> publishes GIT-placed floor
    /// mines that vanished locally (disarmed, recovered, detonated) as mine-typed world flags.
    /// coop.ini <c>mine_sync</c>, default ON since 2026-09-19 (docs/research/mines.md 9). The
    /// peer-removal APPLY half is gated separately in the plugin by the same key.
    /// </summary>
    public bool MineSync { get; init; }

    /// <summary>
    /// When true, <see cref="Pumps.WorldStatePump.PumpLocalVars"/> publishes the allowlisted
    /// per-object local-variable writes the plugin captured and hands the session's values
    /// back for the plugin to apply. coop.ini <c>local_var_sync</c>, default OFF until
    /// live-verified (docs/research/quest-sync.md §8.3). The plugin gates its hook and apply
    /// on the same key.
    /// </summary>
    public bool LocalVarSync { get; init; }

    /// When true, a follower acts on a host's <see cref="TransitionAnnouncePacket"/> the
    /// instant it arrives — starting its own warp toward the announced destination without
    /// waiting for the host's published <c>PlayerState.Module</c> to change or for
    /// <see cref="ModuleSettleMs"/> to elapse. Off by default, live-verification pending:
    /// docs/research/transition-smoothness.md's live-test script is what turns this on for a
    /// measured run. When off, or whenever no usable announce has arrived, the existing
    /// host-module-based follow (above) is unaffected — it is the fallback this was built to
    /// have, not a path this flag disables. coop.ini <c>transition_announce</c>.
    /// </summary>
    public bool TransitionAnnounceEnabled { get; init; }

    /// <summary>
    /// When true, a leader's announced transition INTO a <c>STUNT_*</c> module (the Taris
    /// vision, the Bastila/Revan visions, the romance payoffs, the credits chain) is REPLAYED on
    /// this engine — the same <c>StartNewModule</c> to the same module and entry waypoint,
    /// through the ordinary travel request — instead of refused, so this player sees the scene
    /// on their own engine and its scripts carry them to wherever the leader lands
    /// (<see cref="StuntLegFollowRule"/>, docs/research/cinematics.md's 2026-09-18 section).
    /// Needs <see cref="TransitionAnnounceEnabled"/> (the announce is what proves the leader's
    /// own engine began the leg; the slow path keeps refusing a stunt module regardless).
    /// coop.ini <c>follow_stunt</c>, default ON in every reader; NEEDS-LIVE.
    /// </summary>
    public bool FollowStunt { get; init; }

    /// <summary>
    /// When true, a CLIENT's own engine-native door crossing (the player walking into a
    /// linked door, as opposed to a session-commanded warp) is refused early — before the
    /// load starts — whenever <see cref="FollowHostModule"/> is on and the host has not been
    /// seen transitioning recently. Off by default, live-verification pending (the
    /// cancellation itself is best-effort — see <c>EngineHandles.TryClearTransitionPending</c>
    /// — safe either way, but unproven). coop.ini <c>client_door_refusal</c>.
    /// </summary>
    public bool ClientDoorRefusalEnabled { get; init; }

    /// <summary>
    /// coop.ini <c>follow_lead</c> — docs/research/transition-smoothness.md's "Lead-swap"
    /// entry. <see cref="FollowLeadMode.Any"/> (the default) lets EITHER player's committed
    /// door crossing become an announce the other follows: a client's crossing is now
    /// eligible to be followed by the host too, mirrored through the same announce channel and
    /// the same follow fast path, gated by <see cref="LeadFollowRule.HostMaySafelyFollow"/> so
    /// the host is never pulled out of its own transition, a conversation, or a real fight.
    /// <see cref="FollowLeadMode.Host"/> restores exactly today's behaviour — the host never
    /// follows anyone, and <see cref="ClientDoorRefusalEnabled"/> refuses every premature
    /// client crossing as before.
    /// </summary>
    public FollowLeadMode FollowLeadMode { get; init; } = FollowLeadMode.Any;

    /// <summary>
    /// The five player-count scaling SETTINGS from coop.ini (<c>enemy_health</c>,
    /// <c>enemy_damage</c>, <c>xp_rate</c>, <c>store_stock</c>, <c>store_prices</c> — docs/research/player-count-
    /// scaling.md), each <see cref="PlayerCountScalingRule.Auto"/> or a fixed percent. What
    /// THIS install asked for; a client's session may override any of them with the host's
    /// (<see cref="SessionOverrides"/>), and <see cref="PlayerCountScaling"/> resolves the
    /// effective setting to a percent from the live player count every tick; store-price auto
    /// is currently neutral at 100%.
    /// </summary>
    public PlayerCountScalingSettings Scaling { get; init; } = PlayerCountScalingSettings.Default;

    /// <summary>
    /// The game install directory, when the agent knows it (null for the test fakes and a
    /// session whose install could not be resolved). Only used for the <c>store_stock</c>
    /// once-per-store marker file under <c>saves\</c> (<see cref="StoreStockScaleLatch"/>): the
    /// topped-up stock is baked into every save the host writes afterwards, so the marker has
    /// to outlive the process or a reloaded save would be topped up again.
    /// </summary>
    public string? GameDirectory { get; init; }

    /// <summary>
    /// When true, a mid-module <c>PlayMovie</c> call (no transition involved) captured by this
    /// instance's own <c>MoviePlayHook</c> is announced to peers, and an announcement received
    /// from a peer is forwarded into the plugin for it to act on. ON by default (an absent
    /// <c>coop.ini</c> key enables it; <c>movie_relay = 0</c> disables), matching the plugin's
    /// own copy of this flag — live-verified end to end for both the play-now and defer paths.
    /// See docs/research/trigger-relay.md §6 and docs/research/cinematics.md.
    /// </summary>
    public bool MovieRelayEnabled { get; init; }

    /// <summary>
    /// Turns a module resref into the name shown on screen ("Taris - Lower City" rather than
    /// "tar_m03aa"), for the "following &lt;host&gt; to &lt;module&gt;" line
    /// <see cref="TransitionAnnounceEnabled"/>'s fast path posts. Null (the default) leaves the
    /// resref as-is — <see cref="CoopAgent"/> is deliberately game-agnostic and has no reader
    /// for a game's files of its own; <c>KotorCoop.Agent.Program</c> wires this to
    /// <c>KotorCoop.Memory.GameFiles.ModuleNames.TryRead</c>, which does. A failed lookup must
    /// still show something, so the delegate is expected to fall back to its own input.
    /// </summary>
    public Func<string, string>? ModuleDisplayName { get; init; }

    /// <summary>
    /// When true, each active companion's experience is shared the same way the PC's already
    /// is (<see cref="SyncProgression"/>): the host's per-slot reading — merged upward by max
    /// against every client's own reading, exactly like the PC's experience — overwrites each
    /// instance's own companion stats block. OFF by default, NEEDS-LIVE
    /// (docs/research/companion-state-sync.md): both engines already grant kill XP to every
    /// active party member independently, so this channel matters mainly for script-granted
    /// XP. Live-verified 2026-08-27 (docs/research/companion-state-sync.md §6): a host-side
    /// grant converged to the client and a client-side grant converged to the host, each
    /// within one heavy-pump cycle, never reverting the higher side. coop.ini
    /// <c>companion_xp_sync</c>.
    /// </summary>
    public bool SyncCompanionXp { get; init; } = true;

    /// <summary>
    /// When true, a client dresses its own copy of a host-driven companion in that companion's
    /// REAL equipped gear (body armor, right weapon, left weapon) through
    /// <c>Odyssey.EquipItemFromTemplateDirect</c> (the same primitive the phantom equipment
    /// channel's queued path was replaced by for a live party member — see
    /// docs/research/companion-state-sync.md §7). Live-verified 2026-08-28 (§12): both weapon
    /// slots and the body slot all confirmed working, mid-session propagation under 3s. Default
    /// ON. coop.ini <c>companion_gear_sync</c>.
    /// </summary>
    public bool SyncCompanionGear { get; init; } = true;

    /// <summary>
    /// When true, a client reports cumulative damage its own engine's pipeline has actually
    /// dealt to each companion <c>companion_local_combat</c> has released to it, and the host
    /// spends the rise as real damage on its own authoritative body (roadmap Tier 1: "a
    /// released companion is effectively invulnerable"). Read from and applied through
    /// <c>CombatPump.PumpCompanionDamage</c> on the same 150ms cadence
    /// <see cref="Pumps.CombatPump.PumpEnemies"/> uses, since both are the same shape of
    /// channel (a client's own damage pipeline, forwarded as a cumulative total). Default ON —
    /// safe because the whole channel is INERT unless <c>companion_local_combat</c> (itself OFF
    /// by default) has actually released a companion: with no release, the plugin's watch never
    /// starts, nothing is ever recorded, and an older plugin build (which does not know the
    /// block region this reads) simply returns an empty report. See
    /// docs/research/companion-state-sync.md §13. coop.ini <c>companion_damage_sync</c>.
    /// </summary>
    public bool SyncCompanionDamage { get; init; } = true;

    /// When true, <see cref="Pumps.PartyPoolPump"/> runs on the heavy pass: syncing the shared
    /// party item pool (<c>CSWPartyTable+0x118</c>) both ways — detecting a local player's own
    /// deposit/withdrawal and applying a peer's, through the staging-cheat channel. ON by
    /// default since 2026-08-29 (coop.ini <c>party_pool_sync</c>), at mode 2 — see
    /// <see cref="CoopConfig.PartyPoolSyncMode"/> for the live evidence. Note that the mode-1
    /// pump this sentence describes is NOT what runs by default; <see cref="Pumps.PartyPoolPumpV2"/>
    /// is. See docs/research/party-pool-sync.md and docs/research/shared-inventory-v2.md.
    /// </summary>
    public bool PartyPoolSyncEnabled { get; init; } = true;

    /// <summary><c>party_pool_sync</c> as a small integer — see
    /// <see cref="CoopConfig.PartyPoolSyncMode"/>'s identical remarks. Independent of
    /// <see cref="PartyPoolSyncEnabled"/> above; use <see cref="EffectivePartyPoolSyncMode"/> to
    /// read the two reconciled. Defaults to 2 since 2026-08-29 — see
    /// <see cref="CoopConfig.PartyPoolSyncMode"/>.</summary>
    public int PartyPoolSyncMode { get; init; } = 2;

    /// <summary>See <see cref="CoopConfig.PartyPoolTrace"/> — rig-only per-tick diagnostics for
    /// both mode-2 shared-inventory pumps.</summary>
    public bool PartyPoolTrace { get; init; }

    /// <summary>The mode <see cref="CoopAgent.PumpOnce"/> actually dispatches on: this mode if it
    /// was explicitly set to something other than 0, otherwise <see cref="PartyPoolSyncEnabled"/>
    /// mapped to 1/0 — so code (and every existing test) that only ever set the bool keeps
    /// selecting v1 exactly as before, and only an explicit <see cref="PartyPoolSyncMode"/> of 2
    /// opts into docs/research/shared-inventory-v2.md's redesign.</summary>
    public int EffectivePartyPoolSyncMode => PartyPoolSyncMode != 0 ? PartyPoolSyncMode : (PartyPoolSyncEnabled ? 1 : 0);

    /// <summary>
    /// When true, a story-forced solo segment (the Star Forge's Bastila and Malak duels — see
    /// <see cref="KotorCoop.Protocol.SoloSegmentCatalogue"/> and
    /// docs/research/star-forge-finale.md §3.3/§3.5) makes the OWNER's agent hide every remote
    /// peer from what it writes locally (<see cref="KotorCoop.Protocol.SoloSegmentRule.ShouldHideAllPeers"/>,
    /// applied in <see cref="CoopAgent.PumpOnce"/>'s own peer-visibility filter) while the
    /// plugin's own copy of this same coop.ini key gates the OWNER-side detection
    /// (<c>SoloSegmentController</c>) and the SPECTATOR-side reaction (input lock, camera
    /// follow, damage gate — <c>SoloSegmentSpectateController</c>). OFF by default,
    /// NEEDS-LIVE — nothing here has been exercised against a real Star Forge save yet. coop.ini
    /// <c>solo_segment_spectate</c>.
    /// </summary>
    public bool SoloSegmentSpectateEnabled { get; init; }
    /// When true, <see cref="Pumps.StorePump"/> runs on the heavy pass: syncing merchant store
    /// stock both ways — detecting a local buy/sell while a store's GUI is open, and applying a
    /// peer's, through the staging-cheat channel, for any store this instance has learned an
    /// object id for. ON by default since 2026-08-29 (coop.ini <c>store_sync</c>), at mode 2 —
    /// see <see cref="CoopConfig.StoreSyncMode"/> for the live evidence. As with the party pool,
    /// the mode-1 pump this sentence describes is NOT what runs by default;
    /// <see cref="Pumps.StorePumpV2"/> is. See docs/research/store-sync.md.
    /// </summary>
    public bool StoreSyncEnabled { get; init; } = true;

    /// <summary><c>store_sync</c> as a small integer — see <see cref="PartyPoolSyncMode"/>'s
    /// identical remarks, store's own mirror. Defaults to 2 since 2026-08-29.</summary>
    public int StoreSyncMode { get; init; } = 2;

    /// <summary>See <see cref="EffectivePartyPoolSyncMode"/>'s identical remarks, store's own
    /// mirror.</summary>
    public int EffectiveStoreSyncMode => StoreSyncMode != 0 ? StoreSyncMode : (StoreSyncEnabled ? 1 : 0);

    /// <summary>
    /// Whether <see cref="CoopAgent.ApplyMenuGuard"/> runs at all — coop.ini
    /// <c>menu_guard_sync</c>, ON by default. Gates whether THIS instance watches for a local
    /// full-screen menu (inventory, map, character sheet, journal, equipment, options) and
    /// publishes <see cref="PlayerState.FlagMenuGuard"/> for the bounded window
    /// <see cref="MenuGuardCapMs"/>/<see cref="MenuGuardCooldownMs"/> describe. Read separately
    /// by the plugin (same key, <c>PluginConfig.ReadBool("menu_guard_sync", true)</c>), which
    /// decides whether a peer's stand-in honours a bit it receives — the same "one coop.ini key,
    /// two files" pattern <see cref="SoloSegmentSpectateEnabled"/> uses. See docs/limitations.md's
    /// "Pausing" row and docs/research/effect-sync.md §13.
    /// </summary>
    public bool MenuGuardSyncEnabled { get; init; } = true;

    /// <summary>How long a single menu opening stays protected before the shield lapses even if
    /// the menu is still open — coop.ini <c>menu_guard_cap_ms</c>. See
    /// <see cref="CoopConfig.MenuGuardCapMs"/> for the full reasoning.</summary>
    public int MenuGuardCapMs { get; init; } = MenuGuardRule.DefaultCapMs;

    /// <summary>How long after a capped-out opening before the next one can be protected again —
    /// coop.ini <c>menu_guard_cooldown_ms</c>. See <see cref="CoopConfig.MenuGuardCooldownMs"/>
    /// for the full reasoning.</summary>
    public int MenuGuardCooldownMs { get; init; } = MenuGuardRule.DefaultCooldownMs;
}
