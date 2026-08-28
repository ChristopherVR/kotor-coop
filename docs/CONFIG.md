# Configuration

`coop.ini` lives beside the game executable and is parsed by two independent readers that
never talk to each other: the external agent (`src/KotorCoop.Session/CoopConfig.cs`) and the
in-process plugin (`src/KotorCoop.Plugin/PluginConfig.cs` plus direct per-controller reads).
A few keys are read by both sides. The agent's `KotorCoop.Agent` CLI also takes its own
flags, independent of `coop.ini`.

**Behaviour-flag negotiation (2026-08-27).** Twenty of the keys below (and two CLI-only ones,
`--no-party-sync` and `--require-identical-save`) are now compared between the two peers at
join time — `coop.ini`/CLI settings used to run purely from local disk with no way to tell a
player their partner had a different value, unlike the game-content fingerprint further down
this doc. A difference is always a **warning** (`W_CFG <key> host=<v> you=<v>`, once per key,
through the same stdout/`~`-console/in-game-overlay path a content warning uses) — never a
refusal, never a dropped channel. For nine keys that gate a channel the HOST already drives by
design, a CLIENT additionally **adopts the host's value at runtime** rather than keep running
its own: `party_sync`, `effect_sync`, `trigger_relay`, `liveness_sync`, `map_reveal_sync`,
`script_item_sync`, `dialogue_item_sync`, `dialogue_recruit_sync`, `movie_relay`. Four of those
nine (`party_sync`, `script_item_sync`, `dialogue_item_sync`, `movie_relay`) are live-consulted
directly by the `KotorCoop.Session` pump that runs the channel; the other five
(`effect_sync`, `trigger_relay`, `liveness_sync`, `map_reveal_sync`, `dialogue_recruit_sync`)
are gated entirely inside the separate PLUGIN process, so adoption for them crosses that
process boundary instead — the agent pushes the adopted bits into the plugin's shared block
every tick they change, and each of the five plugin controllers resolves its effective value
from there instead of its own construction-time `coop.ini` read. All nine are host-adopted
end to end as of 2026-08-27. See [mod-compatibility.md](research/mod-compatibility.md)'s
"Behaviour-flag negotiation" section for the full design and the exact wire shape. Nothing
here changes what a key is READ as locally (`CoopConfig`/`AgentSettings` still describe this
install's own coop.ini/CLI exactly as before); adoption lives in a separate, session-scoped
`SessionOverrides` layer.

Every default below is the CODE's own fallback — what applies if the key is absent from
`coop.ini` entirely. **The file that actually ships** (`profiles/coop.ini.example`, staged
verbatim as `coop.ini` by `tools/package.ps1`) sets several keys explicitly, which overrides
the code fallback for anyone using the packaged release. Where the shipped file's value
differs from the code fallback, a footnote says so — that is the value a real install
actually runs with.

## Agent-side keys (`CoopConfig.cs`)

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `mode` | `join` unless `host` | `host` a session or `join` one | [connect-flow.md](research/connect-flow.md) |
| `host` | `127.0.0.1`, unset | server address to join | [connect-flow.md](research/connect-flow.md) |
| `port` | `27015`⁸ | UDP port | [network-topology.md](research/network-topology.md) |
| `password` | empty | session password | [connect-flow.md](research/connect-flow.md) |
| `name` | unset | player/character name shown to peers | — |
| `server_name` | falls back to `name` | name shown in the server browser | [server-browser.md](research/server-browser.md) |
| `menu_button` | ON⁷ | show the in-game Co-op menu button | [menu-frame-hook.md](research/menu-frame-hook.md) |
| `tick_rate` | `0` (keeps the built-in rate) | state send rate in Hz, clamped 5-120 | [lag-audit.md](research/lag-audit.md) |
| `interp_ms` | `-1` (keeps the built-in delay) | how far in the past peers render, ms, clamped 0-500 | [realtime-creature-movement.md](research/realtime-creature-movement.md) |
| `pause_sync` | ON | mirror a peer's pause into this game | — |
| `minigame_hide_peer` | ON | park a peer's phantom while they race/man a turret | [minigames.md](research/minigames.md) |
| `minigame_spectate` | OFF | hand the camera to a racing/turret peer (not yet wired) | [minigames.md](research/minigames.md) |
| `pazaak_detect` | ON⁹ | treat a running Pazaak hand as "in a minigame" too (live-verified 2026-08-28) | [minigames.md](research/minigames.md) |
| `chat_overlay` | ON | show the always-on chat overlay | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_opacity` | `75` | overlay opacity, 0-100 | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_anchor` | `bottom-left` | overlay corner | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_lines` | `5` | lines shown at once | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_fade_ms` | `9000` | ms a line stays before it fades (0 = forever) | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `connect_timeout_ms` | `0` (keeps the built-in 15000) | ms a connect attempt waits before giving up | [connect-flow.md](research/connect-flow.md) |
| `status_toasts` | ON | the persistent connection indicator | [disconnect-reconnect.md](research/disconnect-reconnect.md) |
| `dialogue_item_sync` | ON | forward a reward node's item grant to the other player | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `script_item_sync` | OFF³ | forward a script (OnEnter/OnDeath/heartbeat, outside any conversation) item grant to the other player | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `dialogue_fast_join` | ON | pump the dialogue channel on the real-time path, not the 150ms cadence | [dialogue-skip-desync.md](research/dialogue-skip-desync.md) |
| `party_fast_pump` | ON | pump the party channel on the real-time path | [lag-audit.md](research/lag-audit.md) |
| `container_fast_pump` | OFF | pump placeable open/lock state on the real-time path | [container-sync.md](research/container-sync.md) |
| `session_save_host_refusal` | OFF | tear the session down when the host's own save-load watch fires | [session-save.md](research/session-save.md) |
| `client_load_rejoin` | ON | a client's own Load Game tears down and rejoins the session | [session-save.md](research/session-save.md) |
| `host_load_readopt` | ON | a host's own Load Game has every client rejoin instead of disconnecting | [session-save.md](research/session-save.md) |
| `transition_announce` | ON | a follower acts on an announced transition immediately | [transition-smoothness.md](research/transition-smoothness.md) |
| `client_door_refusal` | ON¹ | refuse a client's own premature door crossing while following | [transition-smoothness.md](research/transition-smoothness.md) |
| `follow_lead` | `any` | who may lead a transition the other follows: `any` or `host` | [transition-smoothness.md](research/transition-smoothness.md) |
| `movie_relay` | ON² | relay a mid-module `PlayMovie` to peers (agent's own copy) | [cinematics.md](research/cinematics.md) |
| `companion_xp_sync` | OFF³ | share each active companion's experience the same way the PC's own is (live-verified 2026-08-27) | [companion-state-sync.md](research/companion-state-sync.md) |
| `companion_gear_sync` | ON (all three slots — body, right weapon, left weapon — live-confirmed 2026-08-28) | read each companion's real equipped gear and hand it to the plugin (agent's own copy) — the 2026-08-27 live-verified FAILURE (apply lands on the PLAYER, not the companion) was root-caused and fixed; a follow-up round found and fixed a weapon-slot-specific `EquipSlotMap` bit-flag encoding bug, then live-confirmed both weapon slots and the body slot, with mid-session propagation under 3s | [companion-state-sync.md](research/companion-state-sync.md) |
| `party_pool_sync` | OFF (live rig, 2026-08-28: a native-side defect found — a freshly-inserted pool item is not reliably found by the very next local read, independent of the C# sync logic) | sync the shared party item pool (`CSWPartyTable+0x118`) both ways | [party-pool-sync.md](research/party-pool-sync.md) |
| `solo_segment_spectate` | ON¹¹ | the OWNER's fight, the other player spectates during a story-forced solo segment (Bastila/Malak duels — live-verified 2026-08-28) | [star-forge-finale.md](research/star-forge-finale.md) |
| `store_sync` | OFF (live rig, 2026-08-28: wipe-prevention/buy/deplete/infinite-stock/blackout-resync confirmed clean, `buy` affordability-guarded; a 2026-08-28/29 pass found `sell` reports success without moving the item, root-caused and fixed 2026-08-29 via full disassembly of `CSWGuiStore::SellItem`/`CSWSStore::AcquireItem` — NEEDS-LIVE, not yet re-tested) | sync merchant store stock (`CSWSStore+0x230`) both ways — reads only while a store's GUI is open | [store-sync.md](research/store-sync.md) |
| `stun_servers` | two well-known public servers¹² | HOST only: STUN servers (`host:port`, comma-separated) asked for the host's public IP:port; empty disables discovery (roadmap item 24, NEEDS-LIVE) | [network-topology.md](research/network-topology.md) |
| `upnp` | ON | HOST only: attempt a UPnP IGD port mapping for the session (and world-transfer) port; fails soft (roadmap item 24, NEEDS-LIVE) | [network-topology.md](research/network-topology.md) |

¹ The property's own C# doc comment used to claim this was OFF by default while the actual
parse logic (`!ContainsKey(...) || IsTruthy(...)`) already defaulted it ON, matching the
shipped `coop.ini.example`; the comment has since been corrected to say ON.
² The plugin reads the same key independently and also defaults it ON (see below). Until this
was fixed, the agent's own copy (`CoopConfig.MovieRelayEnabled`) defaulted OFF when the key was
absent, so the two readers disagreed the moment `movie_relay` was removed from `coop.ini`
entirely; both now default ON, matching each other and the shipped file's explicit `=1`.

## Plugin-side keys (`PluginConfig.cs` + per-controller reads)

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `auto_start_agent` | ON | plugin launches/attaches the agent itself | — |
| `dinput8_next` | unset | file (relative to the game dir) the plugin forwards DirectInput calls to instead of the system `dinput8.dll` — overrides the `dinput8.chain` order; see TROUBLESHOOTING.md "Another mod already uses dinput8.dll" | [Exports.cs](../src/KotorCoop.Plugin/Exports.cs) |
| `menu_button` | ON | plugin's own copy of the menu-button key | [menu-frame-hook.md](research/menu-frame-hook.md) |
| `status_indicator` | ON | persistent on-screen connection summary (separate from the agent's `status_toasts`) | [connect-flow.md](research/connect-flow.md) |
| `bark_replay` | ON | replay a peer's captured NPC bark line | [bark-replay.md](research/bark-replay.md) |
| `bark_sync` | ON | capture and publish this instance's own barks | [bark-replay.md](research/bark-replay.md) |
| `item_template_spawn_sync` | ON | capture the real blueprint resref of every item `CreateItemOnObject` creates (vanilla scripts, `giveloot`/`scriptgive`/`storeinsert`/`partypooladd`), so `script_item_sync` can forward it instead of a possibly-diverging Tag | [item-identity.md §13](research/item-identity.md) |
| `combat_state_sync` | OFF | RETIRED 2026-08-27 (no-op, kept only so the key still parses) — see [combat-participation.md](research/combat-participation.md)'s dated section | [combat-participation.md](research/combat-participation.md) |
| `control_return` | ON | return control to your own character on connect | [control-return.md](research/control-return.md) |
| `client_encounters` | ON | a client's own encounter trigger fires it on the host too | [client-encounters.md](research/client-encounters.md) |
| `enemy_drive` | ON | drive a mirrored enemy copy to the owner's live position | [enemy-presence-sync.md](research/enemy-presence-sync.md) |
| `mirror_smooth_ms` | `150` | smoothing window for enemy/party mirroring, ms, clamped 0-600 | [lag-audit.md](research/lag-audit.md) |
| `local_combat` | ON | let a mirrored enemy fight a client through the client's own engine | [combat-participation.md](research/combat-participation.md) |
| `local_combat_diag_undriven_id` | `0` (off) | debug: force one id undriven, to isolate a bug | — |
| `local_combat_bypass_id` | `0` (off) | debug: bypass id for local combat | — |
| `local_combat_bisect_mask` | `0` (off) | debug: bisection mask for isolating a local-combat bug | — |
| `local_combat_range` | `20` | max distance (units) to release a local-combat enemy | — |
| `local_combat_engage_range` | `11` | engage distance for a released local-combat enemy | — |
| `local_combat_timeout_s` | `25` | seconds before a released enemy reclaims to the mirror | — |
| `local_combat_targeting_us_fresh_s` | `2` | freshness window for targeting data, seconds | — |
| `local_combat_deferred_notice` | ON | HUD line for other hostiles waiting on the one local-combat exclusion slot | [combat-participation.md](research/combat-participation.md) |
| `activate_render_on_start` | ON | force-activate rendering when the plugin starts | — |
| `phantom_targetable` | ON | let enemies target a peer's phantom | [combat-participation.md](research/combat-participation.md) |
| `interaction_locks` | ON | container/corpse open-close animation + use-lock | [container-sync.md](research/container-sync.md) |
| `dialogue_auto_join` | `off` (mode: off/near/always) | auto-pull the other player into an ORDINARY conversation you start (`near`/`always`); a FORCED conversation — an ambush, a scripted cutscene — always auto-joins regardless of this setting | [dialogue-sync.md](research/dialogue-sync.md) |
| `dialogue_auto_join_range` | `20` | distance for `near` auto-join mode | [dialogue-sync.md](research/dialogue-sync.md) |
| `journal_remove_sync` | ON | replicate a journal entry's removal | [quest-sync.md](research/quest-sync.md) |
| `liveness_sync` | OFF | reconcile a body alive on one screen, dead on the other | [creature-liveness.md](research/creature-liveness.md) |
| `map_reveal_sync` | ON¹⁰ | reveal fog-of-war around a peer's live position | [map-reveal.md](research/map-reveal.md) |
| `enemy_materialise` | ON | build a mirror body for an enemy the mirror has none for yet | [enemy-materialisation.md](research/enemy-materialisation.md) |
| `movement` | direct placement unless `walk` | phantom movement: direct placement (seamless) or pathfinding walk | [realtime-creature-movement.md](research/realtime-creature-movement.md) |
| `smooth_ms` | `60` | phantom position-smoothing time constant, ms, clamped 0-400 | [phantom-locomotion.md](research/phantom-locomotion.md) |
| `movie_relay` | ON² | relay a mid-module `PlayMovie` to peers (plugin's own copy) | [cinematics.md](research/cinematics.md) |
| `party_follow` | ON | host-driven companion follow-AI | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_finelog` | OFF | verbose per-write follow-AI diagnostic logging | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_pokestep` | ON | follow-AI fix: poke the step accumulator | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_advance_destnode` | ON | follow-AI fix: advance a stalled path destination node | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_advance_destnode_phase6` | OFF | follow-AI fix candidate, unconfirmed live | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_advance_movingtopath` | ON | follow-AI fix: advance the moving-to-path state | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_force_walking` | ON | follow-AI fix: force the walking state | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_accept_final_node` | OFF | follow-AI fix candidate: accept the final path node early | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_refresh_final_node` | OFF | follow-AI fix candidate, live-refuted (no effect) | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_extend_final_node` | OFF | follow-AI fix candidate, unconfirmed live | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_phase2_kick` | OFF | follow-AI fix candidate: force TeleportForward out of a phase=2 idle stall, not yet live-tested | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_phase7_release` | OFF | follow-AI fix candidate: reset a mirrored companion's frozen follow-FSM record on unmirror, not yet live-tested | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_drive` | ON | drive companion positions every frame | [party-reconciliation.md](research/party-reconciliation.md) |
| `companion_gear_sync` | ON (see the agent-side row above) | dress a mirrored companion in its real equipped gear (plugin's own copy) | [companion-state-sync.md](research/companion-state-sync.md) |
| `companion_local_combat` | OFF³ | release a nearby companion to fight a client through its own engine | [party-reconciliation.md](research/party-reconciliation.md) |
| `companion_local_combat_range` | `10` | release distance for companion local combat | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_roster_sync` | ON | mirror the party roster across instances | [party-reconciliation.md](research/party-reconciliation.md) |
| `dialogue_recruit_sync` | OFF³ | recruit a companion through the real network path on the peer | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `party_together_fix` | ON | fix for the party-together gate blocking a legitimate crossing | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_select_host_only` | ON (NEEDS-LIVE) | suppress a client's own copy of the party-selection screen; the host picks | [galaxy-map-and-party-select.md](research/galaxy-map-and-party-select.md) |
| `pause_button_hook` | ON | hook the in-game pause button | — |
| `effect_sync` | ON⁴ | forward non-damage effects (stun, slow, knockdown, ...) | [effect-sync.md](research/effect-sync.md) |
| `dialog_state_watch` | OFF | opt-in diagnostic: logs every writer of the dialogue global-state flag (`CGuiInGame+0xB4`), to find why a wedged instance's conversation flag comes straight back | [dialogue-sync.md](research/dialogue-sync.md) |
| `minimap_peers` | ON | show peers on the minimap | [minimap-peers.md](research/minimap-peers.md) |
| `frame_stats` | OFF | frame-time histogram diagnostic (pairs with the `lagstats` verb) | [lag-audit.md](research/lag-audit.md) |
| `log_max_mb` | `8` | plugin log rotation cap, MB | — |
| `hide_party_portraits` | ON | hide companion portraits (control lock) | [client-control-lock.md](research/client-control-lock.md) |
| `spectate_on_down` | ON | hand the camera to a partner while downed | [spectate-camera.md](research/spectate-camera.md) |
| `transition_announce_fade` | OFF | fade during an announced transition | [transition-smoothness.md](research/transition-smoothness.md) |
| `transition_announce_scripted` | ON | also announce non-door transitions (galaxy-map departures, Leviathan/Star Forge stage changes, any other scripted `StartNewModule()`) through the same channel, tagged `Script`/`GalaxyMap` for the log only (NEEDS-LIVE) | [transition-smoothness.md](research/transition-smoothness.md) §11 |
| `trigger_relay` | ON⁵ | relay a generic trigger's `OnEnter` to peers | [trigger-relay.md](research/trigger-relay.md) |
| `trigger_relay_patterns` | `k_p*_*` | comma-separated globs matched against a trigger's OnEnter script | [trigger-relay.md](research/trigger-relay.md) |
| `dialogue_join_teleport` | ON | teleport a late joiner into a conversation instead of walking | [dialogue-join-latency.md](research/dialogue-join-latency.md) |
| `dialogue_exclusive` | ON | one conversation at a time (the exclusivity lock itself) | [dialogue-sync.md](research/dialogue-sync.md) |
| `container_locks` | ON | one-at-a-time container/corpse open lock | [container-sync.md](research/container-sync.md) |
| `container_lock_arbitration` | ON | host-arbitrated container lock request/response, closing the race where two near-simultaneous clicks both proceed before the eventually-consistent claim lands; fails safe to the pre-arbitration behaviour after a 1000ms host-answer timeout (raised from 300ms 2026-08-28 — the real plugin→agent→host round trip never fit in the original budget; live-verified 2026-08-28, correct outcome AND fast-path resolution both confirmed) | [container-sync.md](research/container-sync.md) |
| `pazaak_detect` | ON⁹ | plugin's own copy: gates the in-process Pazaak edge log/`MinigameKind`, not the wire flag | [minigames.md](research/minigames.md) |
| `minigame_wait_notice` | ON | "&lt;name&gt; is in a minigame - waiting" HUD line for a parked peer | [minigames.md](research/minigames.md) |
| `solo_segment_spectate` | ON¹¹ | plugin's own copy: gates both `SoloSegmentController` (owner-side detection) and `SoloSegmentSpectateController` (spectator-side input lock/camera/damage gate) | [star-forge-finale.md](research/star-forge-finale.md) |
| `solo_segment_modules` | unset (base set only) | comma-separated extra module resrefs appended to `SoloSegmentCatalogue`'s base set (`sta_m45ac`, `sta_m45ad`) | [star-forge-finale.md](research/star-forge-finale.md) |
| `solo_segment_any_module` | OFF | plugin-only: drops the module gate on `RemovePartyMemberHook`'s own burst trigger ONLY (≥2 `RemovePartyMember` calls within ~1s begins a segment in ANY module, not just the catalogue) — the roster-drop-edge trigger stays catalogue-gated regardless | [star-forge-finale.md](research/star-forge-finale.md) §11 |

³ The code fallback (used whenever the key itself is absent — whether `coop.ini` is missing
entirely or just that one line is) is OFF for `companion_local_combat`, `dialogue_recruit_sync`,
`script_item_sync` and `companion_xp_sync`. For the latter two this is a real inconsistency
worth flagging precisely: their own C# doc comments and record-property initializers
(`CoopConfig.ScriptItemSync`/`CompanionXpSync`, `AgentSettings.ScriptItemSync`/`SyncCompanionXp`)
all say "ON by default", but `AgentComposition.Compose` builds `AgentSettings` with
`ScriptItemSync = config?.ScriptItemSync ?? false` and `SyncCompanionXp = config?.CompanionXpSync
?? false` — an explicit `?? false`, not `?? true` — so the property's own "ON" default is never
actually reachable through the real construction path; an absent key resolves OFF regardless.
The shipped `profiles/coop.ini.example` explicitly sets all four keys to `1`. A packaged install
therefore ships with all four ON; only a hand-edited `coop.ini` with the line deleted (or no
`coop.ini` at all) falls back to OFF, silently, with no warning that the "ON by default" comments
promise.
(2026-08-28: `script_item_sync` and `companion_xp_sync` were then fixed to default ON when the line is absent, matching their documented default; only the no-coop.ini-at-all fallback in `AgentComposition` stays conservative.)
⁴ Until 2026-08-27 the shipped `coop.ini.example` set `effect_sync=0` with a stale "OFF until
live-verified" comment; it now ships `=1`, matching the code fallback and the live-verified
state. A `coop.ini` installed before then keeps the old value (the installer never overwrites
an existing one).
⁵ Same history: `trigger_relay` shipped `=0` with a "not yet run against the live game"
comment; now `=1`, matching the plugin's ON fallback.
⁷ Same history, in the direction that matters most for a first run: the shipped file set
`menu_button=0` with a comment describing the menu rebind as "awaiting live verification"
long after it had been verified; it now ships `=1`. An upgraded install with an older
`coop.ini` still needs `menu_button = 1` set by hand (see TROUBLESHOOTING.md).
⁸ The shipped `coop.ini.example` sets `port = 27140`, not the code's own 27015. Two peers
using the packaged release without editing `coop.ini` will actually connect on 27140 — that
is the real shipped default, not 27015. `CoopConfig.cs`'s own header-comment example also
shows `port = 27140` (it is illustrating the shipped file, not the code fallback), and now
says so explicitly alongside it rather than leaving the 27015/27140 gap unexplained.
⁹ `pazaak_detect` is read TWICE off the same key, by two independent readers, the same split
`movie_relay`² has: `CoopConfig.PazaakDetect` (agent side) is what actually makes
`KotorGameBridge.IsInMinigame` — and therefore the wire's `PlayerState.FlagInMinigame` bit a
peer parks on — true for a Pazaak hand; `PluginConfig.ReadBool("pazaak_detect", ...)` (plugin
side, in-process) only gates that process's own local edge log and `MinigameKind` labelling.
Both now default ON and the shipped `coop.ini.example` sets it to `1`, so the two readers never
disagree in practice; the two are still independent fields, and a hand-edited `coop.ini` that
sets one without the other would let them diverge. A 2026-08-28 live pass ran the positive path
for the first time (a real Pazaak hand started via the `pazaak` console verb): the wire flag
latched, the peer's phantom parked, and the "in a minigame - waiting" HUD toast all fired
correctly, and a real bug (the plugin-side edge detector's own pointer-shape guard rejected the
field's actual bare-`1`/`0` values) was found and fixed. `pazaak_detect` was flipped to `1` (ON)
in both `PluginConfig.ReadBool`'s fallback and `profiles/coop.ini.example` as a result — before
this fix both readers defaulted OFF and the shipped file left it at `0`. Ending a hand while it
is "running" (e.g. via a module transition) still does not clear the flag and remains NEEDS-LIVE,
see `minigames.md`'s 2026-08-28 entry.
¹⁰ Live-verified 2026-08-27: the engine call fires correctly, follows a peer's real position,
survives a module transition with no crash, and costs nothing measurable in frame time. Flipped
ON in both `PluginConfig.ReadBool`'s own fallback and `profiles/coop.ini.example`. The exact
on-screen M-key appearance was not visually confirmed this round (screenshot tooling is broken
on the rig) — see `docs/research/map-reveal.md`'s "Live pass" section. ⚠ `CoopConfig`'s own
digest-only copy (`CoopConfig.MapRevealSync`, read purely so this install's `BehaviourDigest` can
compare it against a peer's) has the same `IsTruthy(...)`-with-no-`ContainsKey`-guard gap footnote³
describes for `script_item_sync`/`companion_xp_sync`, so it falls back to OFF whenever the key
line is absent — even though `PluginConfig`'s real gate stays ON. `map_reveal_sync` is one of the
nine host-adopted keys (see "Behaviour-flag negotiation" above), so a HOST who deletes just that
one line advertises OFF in the digest while its own plugin keeps running the channel ON — a real
negotiation mismatch, not just a doc gap.
¹¹ `solo_segment_spectate` is read TWICE off the same key, by two independent readers, the same
split `pazaak_detect`⁹ and `movie_relay`² already use: `CoopConfig.SoloSegmentSpectateEnabled`
(agent side) is what makes `CoopAgent.PumpOnce`'s own peer-visibility filter hide every remote
peer while the local player owns an active segment; `PluginConfig.ReadBool("solo_segment_spectate",
...)` (plugin side, in-process) gates both the owner-side detection that sets the wire flag in
the first place AND the spectator-side reaction (input lock, camera, damage gate) on the OTHER
player's instance. Both default ON as of 2026-08-28 (live-verified via the script-agnostic
`RemovePartyMemberHook` burst trigger — docs/research/star-forge-finale.md §12); setting it to 0
in `coop.ini` turns every half off together. The one remaining unverified half is whether the
input lock stops a real PHYSICAL WASD press — injected keystrokes never reach this game, so only
the levers engaging (the log line, the phantom hide, the parked camera) could be confirmed.
¹² `stun.l.google.com:19302,stun1.l.google.com:19302` — this is `CoopConfig`'s fallback when
the key is entirely absent; a `stun_servers = ` line present but empty is what actually turns
discovery off (see `PublicAddressDiscovery.ParseServers` and `network-topology.md`'s privacy
note — every query is a UDP datagram to a third party).

## CLI flags (`KotorCoop.Agent`, `AgentOptions.cs`)

Commands: `launch`, `load`, `autotrack`, `track`, `run`, `scan`, `info`, `profiles`,
`install-datamods`, `serve-world`, `adopt`, `craft-save`. `run` drives the co-op loop; the
others are setup, discovery or offline utilities. `agent` with no arguments prints help.

| Flag | Default | Meaning | Command(s) |
| --- | --- | --- | --- |
| `--pid <n>` | none | attach by process id | target selection (most commands) |
| `--process <name>` | none | attach by process name, no `.exe` | target selection |
| `--wait` | off | wait for the target process instead of failing immediately | target selection |
| `--profile <name>` | `kotor1` | offset profile to use | `run` |
| `--name <text>` | `player<random>` | player name shown to peers | `run` |
| `--host` | off | host a session | `run` |
| `--join <host[:port]>` | off | join a session | `run` |
| `--port <n>` | `27015`⁶ | listen/target port | `run` |
| `--tick <n>` | `30` | poll and network rate, Hz, clamped 1-60 | `run` |
| `--read-only` | off | attach without write access | `run` |
| `--game-path <path>` | `C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe` | game executable (or directory, for `install`/`uninstall`, which auto-detect the GOG install when omitted) | `launch`, `install`, `uninstall` |
| `--windowed` | off | set `FullScreen=0` in `swkotor.ini` first (backed up) | `launch` |
| `--via-steam` | off | launch via `steam://rungameid/32370` instead of the exe | `launch` |
| `--instance <n>` | `1` | co-op instance number, clamped 1-9 (mutex renamed for 2+) | `launch` |
| `--load-save <n>` | `0` (stay at main menu) | load save `n` (1 = top of list) with no input | `launch` |
| `--kotor` | off | use the KOTOR-specific bridge (vtable scan, phantom bodies) | `run` |
| `--no-module-filter` | filter is on | show peers regardless of module | `run` |
| `--no-follow` | follow is on | do not auto-travel to the host's module on join | `run` |
| `--load-via-menu` | off | load saves via menu clicks instead of the in-process replay | `launch`/`load` |
| `--require-identical-save` | off | refuse a peer whose save differs, instead of degrading channels | `run` |
| `--allow-different-save` | n/a | no-op, kept for old scripts (now the default) | `run` |
| `--password <text>` | empty | session password | `run` |
| `--no-party-sync` | sync is on | do not reconcile the party | `run` |
| `--no-progression-sync` | sync is on | do not share gold/XP | `run` |
| `--menu` | off | wait for the in-game Co-op menu button before connecting | `run` |
| `--drop-file <path>` | none | watch a path for a scripted item-drop request | `run` |
| `--log <path>` | none | append a compact status log | `run` |
| `--phantoms <n>` | `4` | idle creatures to claim as remote-player avatars, clamped 0-8 | `run` |
| `--enemy-census-cap <n>` | `0` (built-in 32) | DEBUG: shrink the enemy-census cap, clamped 1-32 | `run` |
| `--source <dir>` | none | save directory to copy from | `craft-save` |
| `--dest <dir>` | none | save directory to create (must not exist) | `craft-save` |
| `--bool <NAME[=0\|1]>` | none, repeatable | force a global boolean; bare `NAME` means `=1` | `craft-save` |
| `--number <NAME=N>` | none, repeatable | force a global number, -128..127 | `craft-save` |

⁶ Applies to a scripted `--host`/`--join` run with no `--port`. An auto-started (`--menu`)
session instead takes its port from `coop.ini`'s own `port` key when `--port` was not passed
(`AgentOptions.PortExplicit` tracks whether the flag was actually given, since `27015` is also
a value someone could type on purpose) — before this was fixed, `--menu` runs silently ignored
`coop.ini`'s `port =` line and always bound `27015`. An explicit `--port` always wins over
`coop.ini`, the same as every other CLI-vs-`coop.ini` precedence in this doc. (The CLI's own
`--tick` help text used to say "(default 20)", stale against the actual
`ProtocolConstants.DefaultTickRate = 30` default; that has since been corrected too.)
