# Research

68 write-ups behind every feature in the top-level README, plus the reverse-engineering they
stand on. Each is verified against the retail GOG `swkotor.exe` v1.0.3 (disk == memory, no
ASLR) or the game files themselves, not folklore.

## How these docs are organised

Most are **append-only logs**: a doc gets a new dated section every time a round of work
touches its subject, rather than being rewritten in place, so the history of what was tried
and what was found stays readable. [shared-state-audit.md](shared-state-audit.md) is the
**master log** — the place to start for what actually replicates today versus what only
looks like it does. A doc marked **superseded** is kept for its history even though a later
doc now owns the subject; do not delete it and do not treat its earlier claims as current.
Status below is read off each file's own most recent section, not guessed.

Tag legend used inside the docs themselves: **VERIFIED** = read out of the binary or asserted
by a unit test. **NEEDS-LIVE** = reasoned but not yet observed in a running game — every one
fails safe. **INFERRED** = a reasonable deduction, not directly proven.

## Presence & movement

| Doc | Subject | Status |
| --- | --- | --- |
| [name-plates.md](name-plates.md) | Getting a phantom's name to actually render | Solved, fixed, live-verified |
| [map-presence.md](map-presence.md) | Peer pins on the M-screen map | Solved, implemented, live-verified |
| [animation-sync.md](animation-sync.md) | Weapon-draw/flourish/attack animation replication | Superseded by [animation-and-minimap.md](animation-and-minimap.md) |
| [animation-and-minimap.md](animation-and-minimap.md) | Animation replication + minimap arrow, both bugs | Fixed, live-verified; supersedes animation-sync.md §1.2 |
| [minimap-peers.md](minimap-peers.md) | A peer's arrow on the minimap (not just the M-screen) | Implemented, live-verified (screenshot), ON by default |
| [realtime-creature-movement.md](realtime-creature-movement.md) | The per-frame position-drive recipe every mirror channel uses | RE reference; the recipe is live-verified through its callers |
| [phantom-locomotion.md](phantom-locomotion.md) | Why a phantom slides instead of walking/running | Root-caused, fixed, live-verified, 0% idle-while-moving |

## Travel & transitions

| Doc | Subject | Status |
| --- | --- | --- |
| [transition-window-races.md](transition-window-races.md) | Four crash/desync races found by warping a live pair | Root-caused, fixed; established the project's four safe-write patterns |
| [transition-smoothness.md](transition-smoothness.md) | Closing the gap between the host's door and the follower's warp | Implemented, live-verified, ~9x faster follower warp, ON by default |
| [world-adoption.md](world-adoption.md) | A joiner plays the host's world with their own character | Implemented, live-verified (`agent adopt`) |
| [character-login-chain.md](character-login-chain.md) | Why an in-process load spawns a default-level-0 PC | Root-caused, fixed |
| [ingame-gui-transition.md](ingame-gui-transition.md) | Why an in-process load never leaves the main menu | Root-caused, fixed |
| [cinematics.md](cinematics.md) | What a mid-module movie does to a live pair | Implemented, live-verified, ON by default |
| [session-save.md](session-save.md) | Mid-session save/load: rejoin gates and the `loadgame` verb | Design → implemented, live-verified both directions, ON by default |

## Combat

| Doc | Subject | Status |
| --- | --- | --- |
| [combat-participation.md](combat-participation.md) | Making owner-side enemies threaten the phantom safely | Design + implemented, live-verified; some §7 items remain NEEDS-LIVE |
| [combat-when-apart.md](combat-when-apart.md) | What happens when the two players are not together | Reviewed; encounters-only-fire-for-host gap intentionally NOT fixed (structural) |
| [enemy-presence-sync.md](enemy-presence-sync.md) | One visible fight instead of two private ones | Design over verified research; implemented, live-verified |
| [enemy-materialisation.md](enemy-materialisation.md) | Building a mirror body for an enemy the mirror has none for | Implemented, live-verified |
| [peer-damage-ledger.md](peer-damage-ledger.md) | Carrying a peer's damage as DEALT, not sampled health | Implemented, live-verified |
| [client-encounters.md](client-encounters.md) | Making a client's own ambush trigger fire for the host | Implemented, live-verified, ON by default |
| [min-one-hitpoint.md](min-one-hitpoint.md) | Why a player floors at 1 hp and never goes down | Root-caused (KOTOR's own tutorial script); one related stale-GUI bug logged, open |
| [death-handling.md](death-handling.md) | The death pipeline, control-swap, game-over, revival | RE reference; downed/revive implemented and live-verified elsewhere |
| [spectate-camera.md](spectate-camera.md) | The camera handoff to a downed player's partner | Implemented, live-verified, ON by default |
| [creature-liveness.md](creature-liveness.md) | Reconciling a body alive on one screen, dead on the other | Design → live-verified for the first time; OFF by default; one sub-theory open, blocked on a missing `spawncreature` verb |
| [effect-sync.md](effect-sync.md) | The generic non-damage effect dispatcher | Implemented, live-verified both directions; code default ON, shipped `coop.ini.example` OFF |

## Dialogue

| Doc | Subject | Status |
| --- | --- | --- |
| [dialogue-sync.md](dialogue-sync.md) | Synchronizing conversations across two instances | Implemented, live-verified |
| [dialogue-join-latency.md](dialogue-join-latency.md) | Why a joiner's subtitles lag | Measured; this doc's own A/B found no confirmed improvement (superseded in practice by dialogue-skip-desync.md's decisive case) |
| [dialogue-skip-desync.md](dialogue-skip-desync.md) | Rapid mid-sentence skip desyncing a peer | Root-caused, fixed, live-verified; `dialogue_fast_join=1` flipped ON the same day |
| [dialogue-consequences.md](dialogue-consequences.md) | Getting a reward node's effect and a recruit onto the peer | Design → implemented; recruit sync live-confirmed for two companions, code default OFF (shipped ini ON) |
| [click-to-join-and-vo.md](click-to-join-and-vo.md) | Click-to-join annulment, and a voice-over gag | Both root-caused, fixed |
| [bark-replay.md](bark-replay.md) | Why replaying an NPC bark crashed the process | Root-caused, fixed, live-verified |

## Party, companions & progression

| Doc | Subject | Status |
| --- | --- | --- |
| [party-reconciliation.md](party-reconciliation.md) | Companion follow-AI, drift, and local combat — the master thread | Multiple stalls root-caused and fixed across many rounds; one narrow terminal-node stall remains, fix candidates unconfirmed live, off by default |
| [client-control-lock.md](client-control-lock.md) | Pinning a client to its own PC, never a shared companion | RE reference; implemented |
| [control-return.md](control-return.md) | Regression: a client locked onto a companion at connect | Root-caused, fixed, live-verified |
| [companion-combat-credit-plan.md](companion-combat-credit-plan.md) | Live-test plan: does a released companion's swing land as credited damage? | Run; decisive result on the solo-mode hypothesis; superseded in practice by later party-reconciliation.md rounds |
| [progression-sync.md](progression-sync.md) | Sharing party gold and character XP/level | Research/design; several policy and GUI-repaint questions remain open, NEEDS-LIVE |

## World state

| Doc | Subject | Status |
| --- | --- | --- |
| [placeables-containers.md](placeables-containers.md) | Extending door-style sync to placeables, containers, traps | Design + implemented (unit-tested); NOT live-verified on the two-instance rig |
| [container-sync.md](container-sync.md) | Container/corpse open-close sync and the use-lock | Implemented, live-verified, ON by default; `container_fast_pump` off pending a live A/B |
| [item-identity.md](item-identity.md) | What a `CSWSItem` carries that its blueprint does not | Implemented; apply path live-tested via the console item-identity loop |
| [quest-sync.md](quest-sync.md) | Non-monotonic globals, journal removal, the Lamport-clock fix | Implemented, live-verified, ON by default |
| [map-reveal.md](map-reveal.md) | Fog-of-war sync around a peer's position | Implemented; pinned VERIFIED by static RE, OFF by default pending a live pass |
| [trigger-relay.md](trigger-relay.md) | Making a generic trigger's `OnEnter` fire for both players | Implemented, live-verified, protocol 36; code default ON, shipped `coop.ini.example` OFF |

## Session, network, menu & console

| Doc | Subject | Status |
| --- | --- | --- |
| [console-overlay.md](console-overlay.md) | The `~` console's buffer/render side | Superseded by [text-render-queued.md](text-render-queued.md) |
| [text-render-queued.md](text-render-queued.md) | Queued screen text, rendered from the frame hook | RE reference, implemented; supersedes the earlier direct-call rendering approach |
| [menu-button-runtime.md](menu-button-runtime.md) | Forcing the dormant BTN_WARP visible as the Co-op button | RE reference, implemented, live-verified |
| [menu-frame-hook.md](menu-frame-hook.md) | The per-frame hook point that fires at the main menu | RE reference, implemented; the real hook menu-button-runtime.md and text-render-queued.md both need at the menu |
| [console-register.md](console-register.md) | Registering a native `~`-console command | RE reference, implemented |
| [console-open-safe.md](console-open-safe.md) | Programmatically opening the `~` console | RE reference, implemented |
| [console-cheats.md](console-cheats.md) | Console assist plus the co-op staging cheats | Implemented, live-verified; missing the `recruit` verb (added after this doc's last verb-table update) |
| [chat-input-scroll.md](chat-input-scroll.md) | In-game chat input and overlay scrolling | RE reference, implemented |
| [text-color-alpha.md](text-color-alpha.md) | Screen text with color/alpha | RE reference, implemented |
| [native-browser-panel.md](native-browser-panel.md) | Repurposing `CSWGuiSaveLoad` as the server browser | RE reference, implemented |
| [server-browser.md](server-browser.md) | The main-menu scrollable server list | RE reference, implemented |
| [connect-flow.md](connect-flow.md) | The in-game Connect panel and password entry | Implemented; some on-screen display specifics remain NEEDS-LIVE |
| [disconnect-reconnect.md](disconnect-reconnect.md) | What happens when the link between two players fails | Implemented; some live-rig specifics remain NEEDS-LIVE |
| [network-topology.md](network-topology.md) | Peer-to-peer vs. a dedicated host | Reference / design discussion, no build implied |
| [mouse-cursor-input.md](mouse-cursor-input.md) | Where the engine's mouse cursor lives and how to click | RE reference; one resolution caveat NEEDS-LIVE |

## Engine reverse-engineering

| Doc | Subject | Status |
| --- | --- | --- |
| [engine-api.md](engine-api.md) | Every engine function address the plugin calls | RE reference, kept in sync by `tools/callaudit.py` |
| [override-surfaces.md](override-surfaces.md) | Override/game-file precedence vs. the plugin | RE reference; informs the mod-compatibility rules |
| [no-re-features.md](no-re-features.md) | What's addable from data-driven/modding surfaces alone, zero new RE | RE reference / scope notes |

## Minigames

| Doc | Subject | Status |
| --- | --- | --- |
| [minigames.md](minigames.md) | Swoop races, forced turret sequences, and Pazaak | Swoop-race entry gate found and solved; the terminal transition call is BLOCKED, needs a real debugger this project doesn't have; turret sequence confirmed working as designed |

## Mods

| Doc | Subject | Status |
| --- | --- | --- |
| [mod-compatibility.md](mod-compatibility.md) | Mod compatibility audit and the digest-handshake design | Design + implemented; live-exercised 7 of 8 mismatch categories |

## Crashes

| Doc | Subject | Status |
| --- | --- | --- |
| [phantom-equip-crash.md](phantom-equip-crash.md) | Access violation in `CExoString::CompareNoCase_2` on equip | Root-caused, fixed; one specific caller left NEEDS-LIVE |
| [world-up-faction-crash.md](world-up-faction-crash.md) | `GetSingletonParty(null)` crash in the object-update writer | Root-caused, fixed |
| [travel-map-init-crash.md](travel-map-init-crash.md) | `InitializeMap` crash on a moduleless server | Root-caused, fixed |

## Meta

| Doc | Subject | Status |
| --- | --- | --- |
| [shared-state-audit.md](shared-state-audit.md) | What actually replicates, and what only looks like it does | **Master append-only log** — live-verified findings across many rounds |
| [symmetry.md](symmetry.md) | Auditing that every shared action is symmetric regardless of host/client | Audited; no new asymmetry found, click-to-join symmetry live-verified for all four `dialogue_auto_join` modes |
| [lag-audit.md](lag-audit.md) | Static + live audit of every per-frame cost and latency budget | `party_fast_pump` shipped ON by default off a measured win; its heavy-combat frame-time regression found and closed |
