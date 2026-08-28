# Research

74 write-ups behind every feature in the top-level README, plus the reverse-engineering they
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
| [transition-smoothness.md](transition-smoothness.md) | Closing the gap between the host's door and the follower's warp | Door path implemented, live-verified, ~9x faster follower warp, ON by default; §11 extends the announce to every non-door `StartNewModule()` (galaxy map, Leviathan, Star Forge), ON by default; §12 (2026-08-27) live-verified the de-dup gate on a real door crossing, the galaxy-map/escape-pod legs still NEEDS-LIVE |
| [world-adoption.md](world-adoption.md) | A joiner plays the host's world with their own character | Implemented, live-verified (`agent adopt`) |
| [character-login-chain.md](character-login-chain.md) | Why an in-process load spawns a default-level-0 PC | Root-caused, fixed |
| [ingame-gui-transition.md](ingame-gui-transition.md) | Why an in-process load never leaves the main menu | Root-caused, fixed |
| [cinematics.md](cinematics.md) | What a mid-module movie does to a live pair | Implemented, live-verified, ON by default |
| [session-save.md](session-save.md) | Mid-session save/load: rejoin gates and the `loadgame` verb | ON by default; the `host_load_readopt` regression and the STUNT_00 escape-pod crash share one root cause — a dangling-pointer write storm in `PartySelectSuppressionController` (re-detecting and re-tearing-down the same stale panel address hundreds of times a second). Fixed 2026-08-28; host-loadgame → client-re-adopt is DIRECTLY live-verified working post-fix, and the STUNT_00 crash did not reproduce across two further attempts (pre- and post-fix) on top of an earlier 3/3 clean run — both regressions CLOSED |
| [galaxy-map-and-party-select.md](galaxy-map-and-party-select.md) | Galaxy map travel and the pre-departure party-select screen (roadmap Tier 2 #18) | Party-select suppression shipped (`party_select_host_only`, default ON). The `CGuiInGame+0x78` always-non-null finding was root-caused 2026-08-28 as a real dangling-pointer write storm (70,000+ suppress/sweep pairs in under 8 minutes) and fixed by touching a panel at most once per module; the same fix closes the STUNT_00 escape-pod crash (`0x40D386`) — see session-save.md's row |
| [star-forge-finale.md](star-forge-finale.md) | Lehon through the Star Forge and both endings: every forced solo/party-split beat, the Malak drain mechanic | RE coverage complete; the headline gap (Bastila/Malak duel co-op behaviour) is decided and BUILT as `solo_segment_spectate` (§8). §9 (static-only) resolves the wipe chain: `Sta_BastTalk` gates `StartingList`, only the `==0` branch (entry 61) reaches the wipe via a 7-hop linear chain. §10 (2026-08-28, rig round) live-confirmed entry 61 is reliably reachable (`setglobal`+`startdialog`) exactly as predicted, but the chain stalls immediately after despite 7 clean `selectreply` calls — the roster never wipes via the dialogue path. §11 built a SECOND, script-agnostic trigger instead — `RemovePartyMemberHook`, a detour on the engine's own `RemovePartyMember` VM handler, plus a dev-only `removeparty` verb, reaching the segment without the dialogue at all. §12 (2026-08-28, rig round) live-verified it end to end: splice installs, a burst begins a segment (catalogued and, with `solo_segment_any_module=1`, uncatalogued modules), the roster genuinely empties, the peer's phantom hides, the spectator's input parks and camera follows, a real refill ends it. **`solo_segment_spectate` flipped ON by default.** Remaining gap: whether the input lock stops a real PHYSICAL WASD press is still unprovable (injected keystrokes never reach this game) |

## Combat

| Doc | Subject | Status |
| --- | --- | --- |
| [combat-participation.md](combat-participation.md) | Making owner-side enemies threaten the phantom safely | Design + implemented, live-verified; some §7 items remain NEEDS-LIVE. §10 (2026-08-28) root-caused the "client can't hit an Endar Spire Sith" report with exact `faction`-verb reads: `end_sith2` genuinely starts in a FRIENDLY faction — not a client/host asymmetry, not a co-op bug |
| [combat-when-apart.md](combat-when-apart.md) | What happens when the two players are not together | Reviewed; encounters-only-fire-for-host gap intentionally NOT fixed (structural) |
| [enemy-presence-sync.md](enemy-presence-sync.md) | One visible fight instead of two private ones | Design over verified research; implemented, live-verified |
| [enemy-materialisation.md](enemy-materialisation.md) | Building a mirror body for an enemy the mirror has none for | Implemented, live-verified |
| [peer-damage-ledger.md](peer-damage-ledger.md) | Carrying a peer's damage as DEALT, not sampled health | Implemented, live-verified |
| [client-encounters.md](client-encounters.md) | Making a client's own ambush trigger fire for the host | Implemented, live-verified, ON by default |
| [min-one-hitpoint.md](min-one-hitpoint.md) | Why a player floors at 1 hp and never goes down | Root-caused (KOTOR's own tutorial script); one related stale-GUI bug logged, open |
| [death-handling.md](death-handling.md) | The death pipeline, control-swap, game-over, revival | RE reference; downed/revive implemented and live-verified elsewhere |
| [spectate-camera.md](spectate-camera.md) | The camera handoff to a downed player's partner | Implemented, live-verified, ON by default |
| [creature-liveness.md](creature-liveness.md) | Reconciling a body alive on one screen, dead on the other | Design → live-verified for the first time; OFF by default; one sub-theory open, blocked on a missing `spawncreature` verb |
| [effect-sync.md](effect-sync.md) | The generic non-damage effect dispatcher | Implemented, live-verified both directions; ON by default in code and (since 2026-08-27) in the shipped `coop.ini.example` |

## Dialogue

| Doc | Subject | Status |
| --- | --- | --- |
| [dialogue-sync.md](dialogue-sync.md) | Synchronizing conversations across two instances | Implemented, live-verified |
| [dialogue-join-latency.md](dialogue-join-latency.md) | Why a joiner's subtitles lag | Measured; this doc's own A/B found no confirmed improvement (superseded in practice by dialogue-skip-desync.md's decisive case) |
| [dialogue-skip-desync.md](dialogue-skip-desync.md) | Rapid mid-sentence skip desyncing a peer | Root-caused, fixed, live-verified; `dialogue_fast_join=1` flipped ON the same day |
| [dialogue-consequences.md](dialogue-consequences.md) | Getting a reward node's effect and a recruit onto the peer | Design → implemented; recruit sync live-confirmed for two companions, code default OFF (shipped ini ON). `script_item_sync` live-verified 2026-08-27, full pass (core grant + all three controls, including container/corpse loot after a real ordering-race fix) — flipped ON by default |
| [click-to-join-and-vo.md](click-to-join-and-vo.md) | Click-to-join annulment, and a voice-over gag | Both root-caused, fixed |
| [bark-replay.md](bark-replay.md) | Why replaying an NPC bark crashed the process | Root-caused, fixed, live-verified |

## Party, companions & progression

| Doc | Subject | Status |
| --- | --- | --- |
| [party-reconciliation.md](party-reconciliation.md) | Companion follow-AI, drift, and local combat — the master thread | Multiple stalls root-caused and fixed across many rounds; the remaining phase=2 idle stall taken from the engine side, 2026-08-28 (static pass) — a third, previously undocumented engine exit (`TestDirectLine==-3` → `TeleportForward`) found by disassembly, and a new candidate `party_follow_phase2_kick` built on it (pure rule + tests). A same-day rig round (96) attempted the live A/B but could not reproduce the fix's own target precondition in six attempts — a different, consistently-reproducible near-target freeze (phase=7) dominated instead; genuinely inconclusive, stays off by default |
| [client-control-lock.md](client-control-lock.md) | Pinning a client to its own PC, never a shared companion | RE reference; implemented |
| [control-return.md](control-return.md) | Regression: a client locked onto a companion at connect | Root-caused, fixed, live-verified |
| [companion-combat-credit-plan.md](companion-combat-credit-plan.md) | Live-test plan: does a released companion's swing land as credited damage? | Run; decisive result on the solo-mode hypothesis; superseded in practice by later party-reconciliation.md rounds |
| [progression-sync.md](progression-sync.md) | Sharing party gold and character XP/level | Gold convergence live-verified 2026-08-27, ON by default; GUI-repaint/policy questions remain open |
| [companion-state-sync.md](companion-state-sync.md) | Per-companion experience and equipment (roadmap items 11/12) | `companion_xp_sync` PASS, ON by default. `companion_gear_sync` PASS, ON by default (2026-08-28, §12): the direct-equip fix, the resref-cache/wire propagation fix, and the `EquipSlotMap` bit-flag encoding fix (§11 — the internal slot value was `CSWInventory` byte-offset÷4 instead of the engine's own bit-flag scheme) are all live-confirmed for all three slots (body, right weapon, left weapon), with mid-session propagation to the client under 3s |
| [party-pool-sync.md](party-pool-sync.md) | Syncing the shared party item pool (`CSWPartyTable+0x118`) | 2026-08-28 (§13, live rig): §12's C# echo-guard fix (bench-only) could not be confirmed — the live rig instead found a more basic NATIVE-side defect, a freshly-inserted pool item is not reliably found by the very next `poolremove`/scan, independent of `PartyPoolPump.cs` entirely (three independent readers disagree about the same item in the same frame-or-two); `party_pool_sync` stays OFF |

## World state

| Doc | Subject | Status |
| --- | --- | --- |
| [placeables-containers.md](placeables-containers.md) | Extending door-style sync to placeables, containers, traps | Design + implemented (unit-tested); NOT live-verified on the two-instance rig |
| [container-sync.md](container-sync.md) | Container/corpse open-close sync and the use-lock | Implemented, live-verified, ON by default; `container_fast_pump` off pending a live A/B; `container_lock_arbitration` (closes the container-loot race) live-verified 2026-08-28: correct outcome (exactly one winner) confirmed, ON by default; A/B against `=0` reproduces the old race live. A 2026-08-28 pass found the fast-path request/response itself always timing out at 300ms (falling back to the older, still-correct mechanism); raising the budget to 1000ms was re-verified the same day to resolve through the fast path with no fallback — no follow-up item remains |
| [item-identity.md](item-identity.md) | What a `CSWSItem` carries that its blueprint does not | Implemented; apply path live-tested via the console item-identity loop; §13's item-template-spawn ring (closes the `k_computer_spike`/`g_i_progspike01` gap) is CONFIRMED WORKING end to end as of a 2026-08-28 live pass |
| [quest-sync.md](quest-sync.md) | Non-monotonic globals, journal removal, the Lamport-clock fix | Implemented, live-verified, ON by default |
| [map-reveal.md](map-reveal.md) | Fog-of-war sync around a peer's position | Implemented; live-verified 2026-08-27 (engine call, peer tracking, transition survival, frame cost), default ON. On-screen M-key appearance unconfirmed (screenshot tooling broken) |
| [trigger-relay.md](trigger-relay.md) | Making a generic trigger's `OnEnter` fire for both players | Implemented, live-verified, protocol 36; ON by default in code and (since 2026-08-27) in the shipped `coop.ini.example` |
| [store-sync.md](store-sync.md) | Syncing merchant store stock (`CSWSStore+0x230`) both ways, roadmap item 20 | RE'd and built 2026-08-27; the `openstore` crash and the virgin-store wipe bug were both root-caused and fixed by 2026-08-28. A later 2026-08-28 live pass confirmed wipe-prevention/buy/deplete/infinite-stock/blackout-resync all clean, but found `sell` never finds an item three independent readers confirm exists, and `buy` has no affordability guard (a live overbuy underflowed the party's gold to near `UINT32_MAX`) — `store_sync` stays OFF |

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
| [connect-flow.md](connect-flow.md) | The in-game Connect panel and password entry | Implemented; connect/password flow live-verified. Persistent connection indicator live-verified 2026-08-27 (partial) — data path and peer tracking correct, on-screen render/`status_indicator=0` unconfirmed (shot.ps1 broken), `Reconnecting` state found unreachable |
| [disconnect-reconnect.md](disconnect-reconnect.md) | What happens when the link between two players fails | Implemented; link-outage recovery live-verified. FlagAway (main menu / no player object) live-verified 2026-08-27 as FAILED — the local read masks the failure with transitional data (§10), root cause located, fix not yet shipped |
| [network-topology.md](network-topology.md) | Peer-to-peer vs. a dedicated host; internet play (STUN + UPnP, roadmap item 24) | STUN/UPnP built and unit-tested (2026-08-27) — NEEDS-LIVE against a real router; relay/hole-punching not planned |
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
| [minigames.md](minigames.md) | Swoop races, forced turret sequences, and Pazaak | Swoop-race entry gate found and solved; the terminal transition call is BLOCKED, needs a real debugger this project doesn't have; turret sequence confirmed working as designed. `pazaak_detect` live-verified 2026-08-28: latch/park/toast all PASS on a real hand, a real edge-detector bug (rejecting the field's bare-`1` value) found and fixed — flipped ON by default; ending a hand mid-session still doesn't clear the flag, NEEDS-LIVE |

## Mods

| Doc | Subject | Status |
| --- | --- | --- |
| [mod-compatibility.md](mod-compatibility.md) | Mod compatibility audit and the digest-handshake design | Design + implemented; live-exercised 7 of 8 mismatch categories; behaviour-flag negotiation live-verified 2026-08-27 (core mechanism PASS) |

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
| [playthrough-endar-spire.md](playthrough-endar-spire.md) | A scripted, dev-verb-driven two-player smoke through the Endar Spire opening, shipped defaults | 2026-08-27: PASS on chat/pause-mirror/dialogue/doors/journal/blackout-reconnect/away-quittomenu; one live bug found and fixed (`attack`'s melee-range snap never resolved the local-player sentinel); open items: a client landing real hits on a mirrored hostile, script_item_sync losing a tag-diverged quest item, the escape-pod party-select gate (human-only) |
| [symmetry.md](symmetry.md) | Auditing that every shared action is symmetric regardless of host/client | Audited; no new asymmetry found, click-to-join symmetry live-verified for all four `dialogue_auto_join` modes |
| [lag-audit.md](lag-audit.md) | Static + live audit of every per-frame cost and latency budget | `party_fast_pump` shipped ON by default off a measured win, its heavy-combat frame-time regression found and closed; `WorldSnapshotPacket` now pages a full lobby across several datagrams instead of silently dropping the whole tick (protocol v43), loopback-verified for 8 peers, NEEDS-LIVE against real `swkotor.exe` |
