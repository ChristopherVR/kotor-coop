# Configuration

`coop.ini` lives beside the game executable and is parsed by two independent readers that
never talk to each other: the external agent (`src/KotorCoop.Session/CoopConfig.cs`) and the
in-process plugin (`src/KotorCoop.Plugin/PluginConfig.cs` plus direct per-controller reads).
A few keys are read by both sides. The agent's `KotorCoop.Agent` CLI also takes its own
flags, independent of `coop.ini`.

**This page has two tables.** [Player settings](#player-settings) is the entire shipped
`profiles/coop.ini.example` — every key a player would ever want to change, with a plain-
language explanation. [Developer switches](#developer-switches-do-not-change) is everything
else either reader consumes: tuning knobs and bisect levers built for the test rig and for
isolating a specific bug, each one **shipped at its own verified value** and not something a
player needs to touch. Setting one by hand is unsupported — it may reintroduce a bug that was
fixed by flipping it, or regress a measured latency win. Both readers log a line
(`coop.ini: developer switch 'X = value' is set (not a player setting; see docs/CONFIG.md)`)
whenever a coop.ini they load sets one of these, and a similar line for any key neither reader
recognises at all, so a stray or copy-pasted line is never silently in effect with nothing
pointing at it.

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
`SessionOverrides` layer. All nine of these are developer switches below, not player settings.

Every default below is the CODE's own fallback — what applies if the key is absent from
`coop.ini` entirely. **The file that actually ships** (`profiles/coop.ini.example`, staged
verbatim as `coop.ini` by `tools/package.ps1`) sets several keys explicitly, which overrides
the code fallback for anyone using the packaged release. Where the shipped file's value
differs from the code fallback, a footnote says so — that is the value a real install
actually runs with.

**Retired keys.** `combat_state_sync` (a no-op since 2026-08-27 — see
[combat-participation.md](research/combat-participation.md)'s dated section) was removed
entirely from `coop.ini.example`, both readers, and this doc as part of the 2026-09
"player settings vs. developer switches" cleanup; see git history for the old stub
(`CombatStateController.cs`) if you need the archaeology.

## Player settings

Every key in the shipped `profiles/coop.ini.example`, grouped the same way the file itself
is. The Default column is the value the shipped file actually sets, not the bare code
fallback (they agree here — `CoopIniExampleDocSyncTests` checks it).

### Connecting

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `mode` | `join` | `host` a session or `join` one | [connect-flow.md](research/connect-flow.md) |
| `host` | example address, unset in code | for `mode = join`: the host's address | [connect-flow.md](research/connect-flow.md) |
| `port` | `27140`⁸ | UDP port; host and client must match | [network-topology.md](research/network-topology.md) |
| `password` | empty | session password | [connect-flow.md](research/connect-flow.md) |
| `name` | unset | your name/character name shown to peers | — |
| `server_name` | falls back to `name` | name shown in the server browser | [server-browser.md](research/server-browser.md) |
| `join_mode` | `auto` | how a JOINER gets a character into the host's world. `auto`: the first join opens the game's own character creation and keeps the result as your co-op character (`saves\kotorcoop-character.*`), used on every join after. `newcharacter`: make a new one even though one exists. `save`: carry the character from your own most recent save | [world-adoption.md](research/world-adoption.md) |
| `auto_adopt_world` | ON | a joiner whose world does not match the host's adopts the host's world instead of playing degraded | [world-adoption.md](research/world-adoption.md) |
| `world_transfer_udp_fallback` | ON (since 2026-09-18; was OFF) | a joiner falls back to a UDP-only world-transfer when TCP genuinely can't reach the host (a UDP-only tunnel/NAT path); live-verified 2026-09-10 across two real machines over a public-internet tunnel | [network-topology.md](research/network-topology.md) |
| `menu_button` | ON⁷ | show the main-menu "Co-op" button | [menu-frame-hook.md](research/menu-frame-hook.md) |

### Your character

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `follow_lead` | `any` | who may LEAD a transition the other player follows: `any` or `host` | [transition-smoothness.md](research/transition-smoothness.md) |
| `dialogue_auto_join` | `off` | be pulled into an ORDINARY conversation another player starts: `off`, `near`, or `always`. A conversation the game itself forces on both players always auto-joins regardless | [dialogue-sync.md](research/dialogue-sync.md) |
| `dialogue_auto_join_range` | `20` | the range (game units) `dialogue_auto_join = near` auto-joins within | [dialogue-sync.md](research/dialogue-sync.md) |
| `spectate_on_down` | ON | hand your camera to the other player while you are downed | [spectate-camera.md](research/spectate-camera.md) |
| `control_return` | ON | return control to your own character the moment you connect | [control-return.md](research/control-return.md) |
| `control_return_diag_only` | OFF | rig-only: keep `control_return`'s detection/logging live but skip the actual hand-back, so a caught race stays latched instead of self-correcting | [dialogue-sync.md](research/dialogue-sync.md) |

### Screen

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `hide_party_portraits` | ON | on a client, hide the HUD's companion portraits (they show state this instance does not own) | [client-control-lock.md](research/client-control-lock.md) |
| `status_toasts` | ON | a short line posted only when connection state changes | [disconnect-reconnect.md](research/disconnect-reconnect.md) |
| `status_indicator` | ON | a separate, always-on one-line connection summary | [connect-flow.md](research/connect-flow.md) |
| `chat_overlay` | ON | the always-on chat overlay | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_opacity` | `75` | overlay opacity, 0-100 | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_anchor` | `bottom-left` | overlay corner | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_lines` | `5` | lines shown at once | [chat-input-scroll.md](research/chat-input-scroll.md) |
| `chat_overlay_fade_ms` | `9000` | ms a chat line stays before it fades (0 = forever) | [chat-input-scroll.md](research/chat-input-scroll.md) |

### Controller

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `gamepad` | ON | play with an Xbox-style (XInput) controller; the pad drives the game's own key bindings and cursor. 0 turns it off entirely | [gamepad.md](research/gamepad.md) |
| `gamepad_cursor_speed` | `1.0` | cursor speed multiplier for the sticks, 0.1 - 5 | [gamepad.md](research/gamepad.md) |

### Internet play

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `steam_invites` | ON | Steam friends-list "Join Game"; inert without your own `steam_api64.dll`, Steam running, and owning app 32370 | [steam-support.md](research/steam-support.md) |
| `stun_servers` | two well-known public servers¹² | HOST only: STUN servers asked for the host's public IP:port; empty disables discovery | [network-topology.md](research/network-topology.md) |
| `upnp` | ON | HOST only: attempt a UPnP router port mapping; fails soft | [network-topology.md](research/network-topology.md) |

### Advanced

| Key | Default (shipped) | Meaning | Doc |
| --- | --- | --- | --- |
| `log_max_mb` | `8` | plugin/agent log rotation cap, MB | — |
| `log_keep` | `3` | rotated log generations kept, clamped 1-10 | — |
| `auto_start_agent` | ON | the plugin launches/attaches the agent itself; set 0 only if you run the agent yourself | — |
| `dinput8_next` | unset | file (relative to the game dir) the plugin forwards DirectInput calls to instead of the system `dinput8.dll` | [Exports.cs](../src/KotorCoop.Plugin/Exports.cs) |

## Developer switches (do not change)

Everything else either reader consumes. Every one of these ships at its own verified
setting — tuned or bisected across many recorded live-test rounds under `docs/research/` —
for the test rig and for isolating a specific bug. A player never needs any of them, and
hand-editing one is unsupported.

### Connection & network tuning

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `connect_timeout_ms` | `0` (keeps the built-in 15000) | ms a connect attempt waits before giving up | [connect-flow.md](research/connect-flow.md) |
| `host_loss_release` | ON | a CLIENT that loses its host releases every plugin-side lock and plays solo instead of staying locked out | [disconnect-reconnect.md](research/disconnect-reconnect.md) |
| `host_loss_timeout_ms` | `0` (keeps the built-in 5000) | ms the host may stay silent before `host_loss_release` fires | [disconnect-reconnect.md](research/disconnect-reconnect.md) |
| `host_away_timeout_ms` | `20000` | ms the host may stay flagged AWAY (main menu, load screen — still sending) before that too counts as host loss and `host_loss_release` fires; `0` disables just this trigger (2026-09-18, `HostAwayRule`) | [disconnect-reconnect.md §13](research/disconnect-reconnect.md) |
| `tick_rate` | `0` (keeps the built-in rate) | state send rate in Hz, clamped 5-120 | [lag-audit.md](research/lag-audit.md) |
| `interp_ms` | `-1` (keeps the built-in delay) | how far in the past peers render, ms, clamped 0-500 | [realtime-creature-movement.md](research/realtime-creature-movement.md) |
| `dejitter` | OFF | regularise received avatar snapshots onto a playback timeline instead of raw arrival times | [lag-audit.md](research/lag-audit.md) |
| `perf_trace` | OFF | once-a-second pump/network/phantom telemetry into the agent log (rig measurement) | [lag-audit.md](research/lag-audit.md) |
| `pause_sync` | ON | mirror a peer's pause into this game | — |

### Combat

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `local_combat` | ON | let a mirrored enemy fight a client through the client's own engine | [combat-participation.md](research/combat-participation.md) |
| `local_combat_slots` | `3` | how many hostiles a client may fight simultaneously, clamped 1-3 | [combat-participation.md §11](research/combat-participation.md) |
| `local_combat_diag_undriven_id` | `0` (off) | debug: force one id undriven, to isolate a bug | — |
| `local_combat_bypass_id` | `0` (off) | debug: bypass id for local combat | — |
| `local_combat_bisect_mask` | `0` (off) | debug: bisection mask for isolating a local-combat bug | — |
| `local_combat_range` | `20` | max distance (units) to release a local-combat enemy | — |
| `local_combat_engage_range` | `11` | engage distance for a released local-combat enemy | — |
| `local_combat_timeout_s` | `25` | seconds of no activity before a released enemy reclaims to the mirror | [combat-participation.md §13](research/combat-participation.md) |
| `local_combat_targeting_us_fresh_s` | `2` | freshness window for targeting data, seconds | — |
| `local_combat_deferred_notice` | ON | HUD line for other hostiles waiting on a free local-combat exclusion slot | [combat-participation.md](research/combat-participation.md) |
| `local_combat_engage_fallback` | ON | drive an idle armed creature's attack directly when its script fire leaves it with no target | [combat-participation.md §12](research/combat-participation.md) |
| `damage_creator_attribution` | OFF | pass a real creator id through every forwarded-damage apply, so aggro/last-attacker bookkeeping sees who hit it | [peer-damage-ledger.md §8](research/peer-damage-ledger.md) |
| `threat_retarget` | ON (since 2026-09-03; was OFF) | HOST only: order a creature onto the peer whose forwarded damage clearly outweighs its current target's | [peer-damage-ledger.md §9, §11](research/peer-damage-ledger.md) |
| `phantom_targetable` | ON | let enemies target a peer's phantom | [combat-participation.md](research/combat-participation.md) |
| `effect_sync` | ON⁴ | forward non-damage effects (stun, slow, knockdown, heal, ...) that land on a peer's phantom to their real character | [effect-sync.md](research/effect-sync.md) |
| `client_encounters` | ON | a client's own encounter trigger fires it on the host too | [client-encounters.md](research/client-encounters.md) |
| `liveness_sync` | ON (code default since 2026-09-18 — the 2026-09-03 flip only set the template line, which the player-settings cleanup then removed, so shipped installs ran OFF until 2026-09-18; see footnote ³) | reconcile a body alive on one screen, dead on the other | [creature-liveness.md](research/creature-liveness.md) |
| `enemy_drive` | ON | drive a mirrored enemy copy to the owner's live position | [enemy-presence-sync.md](research/enemy-presence-sync.md) |
| `enemy_materialise` | ON | build a mirror body for an enemy the mirror has none for yet | [enemy-materialisation.md](research/enemy-materialisation.md) |
| `mirror_smooth_ms` | `150` | smoothing window for enemy/party mirroring, ms, clamped 0-600 | [lag-audit.md](research/lag-audit.md) |
| `movement` | direct placement unless `walk` | phantom movement: direct placement (seamless) or pathfinding walk | [realtime-creature-movement.md](research/realtime-creature-movement.md) |
| `smooth_ms` | `60` | phantom position-smoothing time constant, ms, clamped 0-400 | [phantom-locomotion.md](research/phantom-locomotion.md) |

### Companions & party roster

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `companion_local_combat` | OFF³ | release a nearby companion to fight a client through its own engine | [party-reconciliation.md](research/party-reconciliation.md) |
| `companion_local_combat_range` | `10` | release distance for companion local combat | [party-reconciliation.md](research/party-reconciliation.md) |
| `companion_damage_sync` | ON | forward damage taken by a released companion to the host | [companion-state-sync.md §13](research/companion-state-sync.md) |
| `companion_gear_sync` | ON | dress a mirrored companion in its real equipped gear | [companion-state-sync.md](research/companion-state-sync.md) |
| `companion_xp_sync` | ON (since 2026-08-28; was OFF)³ | share each active companion's experience the same way the PC's own is | [companion-state-sync.md](research/companion-state-sync.md) |
| `party_roster_sync` | ON | reconcile the party's ACTIVE-TWO seats across instances so a partial/aborted conversation doesn't leave them disagreeing. Does **not** by itself make a companion the peer just recruited available on this instance — see `dialogue_recruit_sync` below | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_select_host_only` | ON (NEEDS-LIVE — in particular whether a CLIENT-led departure through the swept panel still runs its exit script; see docs/review-seamless-2026-09-18.md §2) | suppress a client's own copy of the party-selection screen; the host picks | [galaxy-map-and-party-select.md](research/galaxy-map-and-party-select.md) |
| `party_together_fix` | ON | fix for the party-together gate blocking a legitimate crossing | [party-reconciliation.md](research/party-reconciliation.md) |
| `galaxy_map_host_only` | ON (NEEDS-LIVE) | on a CLIENT under `follow_lead=host`, refuse the galaxy map's own accept-button click | [galaxy-map-and-party-select.md](research/galaxy-map-and-party-select.md) |
| `dialogue_recruit_sync` | ON³ (since 2026-09-18; was OFF) | a companion recruited in a conversation on one instance is made available on every other one. **With it off, only the recruiting player keeps the companion** (the pre-2026-09-18 shipped state, which split every roster after Taris — docs/review-seamless-2026-09-18.md). On, it recruits the peer's extra slot locally through the real engine call (`AddAvailableNpcByTemplate`) instead of only logging and skipping it | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `map_reveal_sync` | ON¹⁰ | reveal fog-of-war around a peer's live position | [map-reveal.md](research/map-reveal.md) |
| `journal_remove_sync` | ON | replicate a journal entry's removal | [quest-sync.md](research/quest-sync.md) |
| `minimap_peers` | ON | show peers on the minimap | [minimap-peers.md](research/minimap-peers.md) |
| `party_drive` | ON | drive companion positions every frame | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_fast_pump` | ON | pump the party channel on the real-time path, not the 150ms cadence | [lag-audit.md](research/lag-audit.md) |

#### Companion follow-AI fix candidates

Two of these (`party_follow_stall_recovery` and its method) are ON by default and live-verified; the rest are OFF and kept for a future live A/B — see [party-reconciliation.md](research/party-reconciliation.md).

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
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
| `party_follow_phase2_kick` | OFF | follow-AI fix candidate: force TeleportForward out of a phase=2 idle stall | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_phase7_release` | OFF | follow-AI fix candidate: reset a mirrored companion's frozen follow-FSM record on unmirror | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_stall_recovery` | ON | the RUNTIME stall detector, live-verified against the phase-7 `PathWaitDest` wedge | [party-reconciliation.md](research/party-reconciliation.md) |
| `party_follow_stall_recovery_method` | `3` | which recovery `party_follow_stall_recovery` applies (1-6, see the doc) | [party-reconciliation.md](research/party-reconciliation.md) |

### Dialogue & conversations

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `dialogue_item_sync` | ON | forward a reward node's item grant to the other player | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `script_item_sync` | ON (since 2026-08-28; was OFF)³ | forward a script (OnEnter/OnDeath/heartbeat) item grant to the other player | [dialogue-consequences.md](research/dialogue-consequences.md) |
| `dialogue_fast_join` | ON | pump the dialogue channel on the real-time path, not the 150ms cadence | [dialogue-skip-desync.md](research/dialogue-skip-desync.md) |
| `dialogue_join_teleport` | ON | teleport a late joiner into a conversation instead of walking | [dialogue-join-latency.md](research/dialogue-join-latency.md) |
| `dialogue_exclusive` | ON | one conversation at a time (the exclusivity lock itself) | [dialogue-sync.md](research/dialogue-sync.md) |
| `dialog_state_watch` | OFF | opt-in diagnostic: logs every writer of the dialogue global-state flag | [dialogue-sync.md](research/dialogue-sync.md) |
| `movie_relay` | ON² | relay a mid-module `PlayMovie` to peers | [cinematics.md](research/cinematics.md) |
| `item_template_spawn_sync` | ON | capture the real blueprint resref of every item `CreateItemOnObject` creates, for `script_item_sync` | [item-identity.md §13](research/item-identity.md) |

### Transitions & world state

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `transition_announce` | ON | a follower acts on an announced transition immediately | [transition-smoothness.md](research/transition-smoothness.md) |
| `transition_announce_fade` | OFF | fade during an announced transition | [transition-smoothness.md](research/transition-smoothness.md) |
| `transition_announce_scripted` | ON | also announce non-door transitions (galaxy-map departures, Leviathan/Star Forge stage changes) | [transition-smoothness.md §11](research/transition-smoothness.md) |
| `transition_close_client_world` | ON | BISECT LEVER: close the client's world in the transition window where the server's module is already gone | [travel-map-init-crash.md](research/travel-map-init-crash.md) |
| `client_door_refusal` | ON¹ | refuse a client's own premature door crossing while following | [transition-smoothness.md](research/transition-smoothness.md) |
| `client_load_rejoin` | ON | a client's own Load Game tears down and rejoins the session | [session-save.md](research/session-save.md) |
| `host_load_readopt` | ON | a host's own Load Game has every client rejoin instead of disconnecting | [session-save.md](research/session-save.md) |
| `session_save_host_refusal` | OFF | tear the session down when the host's own save-load watch fires | [session-save.md](research/session-save.md) |

### Minigames (swoop races, turrets, Pazaak)

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `minigame_hide_peer` | ON | park a peer's phantom while they race/man a turret | [minigames.md](research/minigames.md) |
| `minigame_lock_peers` | ON | one player races, everybody else waits, and cannot be follow-warped onto the track | [minigames.md](research/minigames.md) §24 |
| `minigame_lock_timeout_s` | `600` | seconds a minigame lock may stand before releasing itself (0 = no timeout) | [minigames.md](research/minigames.md) §24 |
| `minigame_modules` | unset (four stock tracks only) | extra module resrefs to treat as single-seat minigame tracks | [minigames.md](research/minigames.md) §24 |
| `minigame_spectate` | OFF | hand the camera to a racing/turret peer (not yet wired) | [minigames.md](research/minigames.md) |
| `minigame_wait_notice` | ON | "&lt;name&gt; is in a minigame - waiting" HUD line for a parked peer | [minigames.md](research/minigames.md) |
| `pazaak_detect` | ON⁹ | treat a running Pazaak hand as "in a minigame" too | [minigames.md](research/minigames.md) |
| `solo_segment_spectate` | ON¹¹ | the OWNER's fight, the other player spectates during a story-forced solo segment | [star-forge-finale.md](research/star-forge-finale.md) |
| `solo_segment_modules` | unset (base set only) | extra module resrefs appended to the solo-segment catalogue | [star-forge-finale.md](research/star-forge-finale.md) |
| `solo_segment_any_module` | OFF | drop the module gate on the script-agnostic solo-segment burst trigger | [star-forge-finale.md §11](research/star-forge-finale.md) |

### Shared inventory (party pool / stores)

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `party_pool_sync` | `2` (ON — flipped 2026-08-29) | sync the shared party item pool both ways — `0` off, `1` the disqualified op-log (kept for config compatibility), `2` the host-authoritative redesign and shipped default | [party-pool-sync.md](research/party-pool-sync.md), [shared-inventory-v2.md](research/shared-inventory-v2.md) |
| `party_pool_trace` | OFF | rig-only per-tick diagnostics for both mode-2 shared-inventory pumps | [shared-inventory-v2.md §12](research/shared-inventory-v2.md) |
| `store_sync` | `2` (ON — flipped 2026-08-29) | sync merchant store stock both ways — same `0`/`1`/`2` shape as `party_pool_sync` | [store-sync.md](research/store-sync.md), [shared-inventory-v2.md](research/shared-inventory-v2.md) |

### Interaction & world objects

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `interaction_locks` | ON | container/corpse open-close animation + use-lock | [container-sync.md](research/container-sync.md) |
| `container_locks` | ON | one-at-a-time container/corpse open lock | [container-sync.md](research/container-sync.md) |
| `container_lock_arbitration` | ON | host-arbitrated container lock request/response, closing a same-RTT double-open race | [container-sync.md](research/container-sync.md) |
| `container_fast_pump` | OFF | pump placeable open/lock state on the real-time path | [container-sync.md](research/container-sync.md) |
| `trigger_relay` | ON⁵ | relay a generic trigger's `OnEnter` to peers | [trigger-relay.md](research/trigger-relay.md) |
| `trigger_relay_patterns` | `k_p*_*` | comma-separated globs matched against a trigger's OnEnter script | [trigger-relay.md](research/trigger-relay.md) |
| `removeparty_hook` | ON¹³ | DIAGNOSTIC-only kill key: install the `RemovePartyMember` detour at all | [transition-crashes.md](research/transition-crashes.md) |
| `behaviour_override_reader` | ON¹³ | DIAGNOSTIC-only kill key: whether the five host-adoptable controllers read the shared block's override region at all | [transition-crashes.md](research/transition-crashes.md) |

### Always-on plumbing

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `activate_render_on_start` | ON | force-activate rendering when the plugin starts | — |
| `bark_replay` | ON | replay a peer's captured NPC bark line | [bark-replay.md](research/bark-replay.md) |
| `bark_sync` | ON | capture and publish this instance's own barks | [bark-replay.md](research/bark-replay.md) |
| `pause_button_hook` | ON | let the mod own the HUD pause button | — |
| `client_temp_death_suppress` | OFF (verified live 2026-09-02) | on a CLIENT, make game-over/auto-revive/gather-teleport host-authoritative decisions | [death-handling.md](research/death-handling.md) |

### Diagnostics (rig-only)

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `frame_stats` | OFF | frame-time histogram diagnostic (pairs with the `lagstats` verb) | [lag-audit.md](research/lag-audit.md) |
| `delay_probe` | OFF | DIAGNOSTIC-only: install `DelayEventProbeHook`'s read-only splices, log-only | [minigames.md §24](research/minigames.md) |
| `delay_probe_events` | OFF | DIAGNOSTIC-only, with `delay_probe`: also log every delayed-event enqueue/dispatch (high volume) | [minigames.md §24](research/minigames.md) |
| `gui_probe` | OFF | DIAGNOSTIC-only: splice Show/HideSWInGameGui and watch `CGuiInGame+0x30`, log-only | [minigames.md §24](research/minigames.md) |

## Debug / test-only keys

Not part of normal play — omitted from the tables above so they never look like something a
player should set. Each exists to make an otherwise-unreachable code path reachable on the rig.

| Key | Default (code) | Meaning | Doc |
| --- | --- | --- | --- |
| `debug_hello_version` (added 2026-09-02) | `0` (off) | CLIENT only: overrides the version byte written into this client's Hello packet, and that packet alone | [disconnect-reconnect.md §12](research/disconnect-reconnect.md) |

¹ The property's own C# doc comment used to claim this was OFF by default while the actual
parse logic (`!ContainsKey(...) || IsTruthy(...)`) already defaulted it ON, matching the
shipped `coop.ini.example`; the comment has since been corrected to say ON.
² The plugin reads the same key independently and also defaults it ON (see below). Until this
was fixed, the agent's own copy (`CoopConfig.MovieRelayEnabled`) defaulted OFF when the key was
absent, so the two readers disagreed the moment `movie_relay` was removed from `coop.ini`
entirely; both now default ON, matching each other and the shipped file's explicit `=1`.
³ **Corrected — this footnote used to claim `profiles/coop.ini.example` explicitly sets all four
keys below to `1`. It does not; none of the four appear in the shipped file at all**, so all four
run on whatever their own code fallback is. That fallback now differs between the two pairs:

- `script_item_sync` and `companion_xp_sync` were fixed 2026-08-28 to default **ON** when the key
  is absent — `CoopConfig`'s own parse (`!ContainsKey(...) || IsTruthy(...)`) and its record-property
  initializers agree, and `AgentComposition.Compose` now reads `config?.ScriptItemSync ?? true` /
  `config?.CompanionXpSync ?? true`. A packaged install therefore really does ship with both ON,
  just via the code default, not an explicit `coop.ini` line.
- `dialogue_recruit_sync` was fixed the same way on **2026-09-18** (plugin `ReadBool(..., true)`,
  `CoopConfig` record default and parse, `AgentComposition.Compose`'s `?? true`): the
  seamless-playthrough review ([review-seamless-2026-09-18.md](review-seamless-2026-09-18.md))
  found that shipping it OFF split every roster after Taris, while the evidence the OFF default
  was waiting for (all nine slot templates statically verified, two live) had been in hand since
  2026-08-24. `liveness_sync` and `map_reveal_sync` had the mirror-image bug the same day: the
  PLUGIN default was ON (or the template line said 1) while `CoopConfig`'s digest default was
  OFF, and because both peers' digests agreed, no `W_CFG` ever fired — for `liveness_sync` that
  meant every shipped install between 2026-09-03 and 2026-09-18 ran without it. **Rule since
  2026-09-18: a digest-only key's default in `CoopConfig` must equal the plugin's
  `PluginConfig.ReadBool` fallback**, and `AgentComposition.Compose`'s `??` fallback (used when
  coop.ini is missing) must equal both.
- `companion_local_combat` still defaults **OFF** everywhere that reads it
  (`PluginConfig.ReadBool("companion_local_combat", false)`, `AgentComposition.Compose`'s
  `?? false`) and is not in the shipped template, so **a real packaged install ships with it
  genuinely OFF** — the documented, on-purpose state (see its row above). Turning it on is a
  one-line `coop.ini` edit and is exactly what the live-verified rounds cited in the status table
  were run with.
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
¹³ Added 2026-08-29 as part of a DLL-level bisect of always-installed plugin pieces
(`docs/research/transition-crashes.md`) — these two, plus the pre-existing
`item_template_spawn_sync`/`status_indicator`/`minigame_wait_notice`/`party_select_host_only`
keys, are every always-on hook/controller/reader added to the plugin since 2026-08-27 that
runs regardless of any other coop.ini feature flag. Diagnostic-only: default ON, meant to stay
ON in a real install, exist purely so a bisection round can isolate one piece at a time.

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
| `--steam-join <s>` | none | join the session named by a Steam rich-presence connect string (`kc1/<host>:<port>[/<token>]`) — what Steam's "Join Game" passes. Decoded into the same host/port/password a typed `--join` would set; a malformed string prints help rather than dialling. Needs no Steam client: it is only a decoder | `run` |
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
