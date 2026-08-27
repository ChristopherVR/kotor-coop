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
    /// active party member independently, so this channel is expected to matter mainly for
    /// script-granted XP, but nothing here has been observed against the real game yet.
    /// coop.ini <c>companion_xp_sync</c>.
    /// </summary>
    public bool SyncCompanionXp { get; init; }

    /// <summary>
    /// When true, a client dresses its own copy of a host-driven companion in that companion's
    /// REAL equipped gear (body armor, right weapon, left weapon) through the same
    /// <c>Odyssey.EquipItemFromTemplate</c> path the phantom equipment channel already uses.
    /// OFF by default, NEEDS-LIVE — see docs/research/companion-state-sync.md. coop.ini
    /// <c>companion_gear_sync</c>.
    /// </summary>
    public bool SyncCompanionGear { get; init; }
    /// When true, <see cref="Pumps.PartyPoolPump"/> runs on the heavy pass: syncing the shared
    /// party item pool (<c>CSWPartyTable+0x118</c>) both ways — detecting a local player's own
    /// deposit/withdrawal and applying a peer's, through the staging-cheat channel. Off by
    /// default (coop.ini <c>party_pool_sync</c>) — the offset chain is VERIFIED against the
    /// symbol database and the disassembly, and the engine calls are real, verified primitives,
    /// but neither has been watched land on a running game yet. See
    /// docs/research/party-pool-sync.md for the full design and the NEEDS-LIVE items.
    /// </summary>
    public bool PartyPoolSyncEnabled { get; init; }

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
    /// object id for. Off by default (coop.ini <c>store_sync</c>) — the offset chain and the
    /// engine calls are VERIFIED against the symbol database and the disassembly, but neither
    /// has been watched land on a running game yet. See docs/research/store-sync.md.
    /// </summary>
    public bool StoreSyncEnabled { get; init; }
}
