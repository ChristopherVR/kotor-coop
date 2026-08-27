# Roadmap: the remainder of "seamless"

Rewritten 2026-08-27 from a six-domain, code-verified audit (combat/companions, shared
world, session/travel/reliability, dialogue/cinematics/minigames, protocol/tests/quality,
player experience) run against the refactored tree. The 2026-08-21 version of this file was
snapshotted before same-day fixes landed and had drifted badly: ten of its rows described
work that was already done. Every row below was checked against the code, not the docs.

Status words: **Verified-live** / **Built (NEEDS-LIVE)** / **Partial** / **Missing** /
**Decision needed**.

Rig rule: there is ONE live two-instance rig. Items marked 🎮 need it and run one at a
time; everything else builds and unit-tests in parallel.

## Closed since the 2026-08-21 version (for the record)

| Old # | Item | Where it closed |
|---|---|---|
| 1 | `local_combat` real hits, lethality, client kill, natural aggro, immediate trigger | Verified-live round 55, `LocalFightCandidateRule.cs` |
| 2 | Client container/corpse loot never reached the host | `1324f93` 2026-08-21, `Pumps/LootPump.cs:126-144` is symmetric |
| 3 | Effect sync | **9/9** kinds live-verified both directions (`effect-sync.md` §9, 2026-08-24) |
| 4 | Mirrored companions can't swing on a client | `companion_local_combat` live-verified with two companions 2026-08-24; ON in the shipped ini |
| 5 | Logs grow without bound | `LogRotation.cs` (startup) + mid-session rotation (2026-08-27) |
| 6 | No agent liveness / no in-game failure messages | `AgentLiveness*.cs`, `PortInUseException`, `SessionManager.TimeoutConnect` |
| 7 | HUD hourglass can't unpause | `PauseButtonHook.cs` + `PauseSanityPatch.cs` |
| 9 | `PartyPresenceController` lacked `IsPartyRosterFrozen` guard | `a7b8f0d`, `PartyFreezeRule` |
| 11 | Materialised-loot dedup reset + reconnect resync | `MaterialisedLootHistory.cs` (NEEDS-LIVE) |
| 13 | Client door refusal / host does not follow | `party_together_fix`, `follow_lead=any`, verified-live |
| 14 | Mid-session save/load | Phase 1 + phase 2 built; `client_load_rejoin`/`host_load_readopt` ON, live-confirmed 2026-08-24 (`session-save.md`) |
| 15 | Container loot has no version tripwire | `680c866`, `ContainerLootStore` digest + `LootSyncRequestPacket` (NEEDS-LIVE) |
| 16 | Packaging + CI | `.github/workflows/ci.yml`, `release.yml`, `tools/package.ps1` |
| 17 | Reliable*Sender tests | `ReliableSenderTests.cs` covers all five senders |
| 18 | Persistent connection indicator | `ConnectionIndicatorController` — was a fading toast before. Live-verified 2026-08-27 (partial): peer-count tracking and the wire/decode data path confirmed correct; the on-screen render itself and `status_indicator=0` unconfirmed (shot.ps1 still solid-black); `ConnectionIndicatorState.Reconnecting` found unreachable in the current wiring (`CoopHost.Connected` is hardcoded true, `CoopClient.IsConnected` clears atomically with the reconnect-phase flip) — see connect-flow.md's 2026-08-27 section |

## Tier 1 — what a normal two-player playthrough still hits

| # | Item | Status | Where |
|---|---|---|---|
| 1 | **Party shared-loot pool (`pt_item_repo`, `CSWPartyTable+0x118`) is two unrelated pools.** Gear stashed "in the party" on one instance never exists on the other. Content-keyed op-log channel built 2026-08-27 (`party_pool_sync`, OFF until live-verified); add/remove both ways, host-relayed, `Set`-based resync on reconnect. | Built (NEEDS-LIVE) 🎮 | `Pumps/PartyPoolPump.cs`, `PartyPoolStore.cs`, `Odyssey.PartyPool.cs`, `KotorGameBridge.PartyPool.cs`; docs: [party-pool-sync.md](research/party-pool-sync.md) |
| 2 | **Items granted by scripts outside dialogue** (OnEnter, OnDeath, cutscene, heartbeat — most quest items) reach only the engine that ran the script. `script_item_sync` pump built 2026-08-27. Live-verified 2026-08-27, full pass via `scriptgive`/`loottake` test primitives: the core grant, the `giveloot` suppression control, AND the container/corpse-loot control (flagged as most likely to fail) all confirmed clean end-to-end. The container/corpse-loot control DID double-report on its first run — root cause: `PumpContainerLoot` and the script-item sample ran on independently-clocked ~1s timers with no ordering guarantee, so a suppression entry could land after the report it was meant to prevent. Fixed by moving `PumpContainerLoot` onto the fast per-tick path, ordered immediately ahead of the script-item sample (`CoopAgent.PumpOnce`); re-run confirmed clean. Flipped ON by default. | Live-verified ✅ | `Pumps/ScriptItemGrantPump.cs`, `ScriptItemGrantRule`, `Pumps/LootPump.cs`, `CoopAgent.cs` |
| 3 | 🎮 **Progression never live-verified** against a real vendor purchase (gold ledger) or a cross-side kill (XP max-merge). Code is complete. | Built (NEEDS-LIVE) | `Pumps/PartyPump.cs:159-282` |
| 4 | 🎮 **Container-loot race**: two clicks on the same chest within one RTT can both hand out loot before either removal lands (`ContainerUseRule` is eventually consistent). Attempted 2026-08-27: staged both players on a known footlocker position (`end_m01aa`, 10.8/21.87) and fired `useplaceable` on both within ~200ms via `blockpoke.ps1` — refused on both sides, "none close enough / none with inventory", most likely because this container was already emptied by an earlier test round using the same persistent save. Race itself still never exercised. | Partial | `Pumps/WorldStatePump.cs:24-52`, `ContainerUseRule.cs` |
| 5 | **A peer at the main menu / load screen looked like a frozen statue forever.** `FlagAway` built 2026-08-27: agent publishes Away after sustained read failure, peer parks the phantom and toasts. Live-verified 2026-08-27: **FAILED** — quitting to the main menu (via the new `quittomenu` test primitive) leaves the local read succeeding against a transitional-looking creature (module `'area1'`, the engine's own known mid-transition placeholder — `IsTransitionalModuleName` already exists in `KotorGameBridge.Encounters.cs` but is not applied to `TryReadLocalState`), so `AwayRule`'s failure counter never advances and the flag never latches. Root cause located; fix not shipped. See `disconnect-reconnect.md` §10. | Live-verified: FAILED 🎮 | `CoopAgent.cs`, `AwayRule`, `KotorGameBridge.Player.cs`, `KotorGameBridge.Encounters.cs` |
| 6 | **Protocol-version mismatch used to be silence** (the 15 s "no reply" timeout). Now a typed `E_PROTO` Reject both ways (2026-08-27). | Built | `CoopHost.cs`, `CoopClient.cs`, `PacketCodec.PeekTypeAnyVersion` |
| 7 | **`coop.ini` divergence between peers is unchecked.** ~~Every behaviour flag...~~ Twenty keys (`local_combat`, `party_fast_pump`, the load-rejoin gates, `follow_lead`, `interp_ms` and 16 more) now ride a `BehaviourDigest` in Hello/Welcome (Version 40, 2026-08-27): every difference warns (`W_CFG`) through the same announcer path content warnings use; nine host-authoritative keys (`party_sync`, `effect_sync`, `trigger_relay`, `liveness_sync`, `map_reveal_sync`, `script_item_sync`, `dialogue_item_sync`, `dialogue_recruit_sync`, `movie_relay`) are additionally adopted by the client at runtime via `SessionOverrides` — four of those nine (`party_sync`, `script_item_sync`, `dialogue_item_sync`, `movie_relay`) are live-consulted by their pump; the other five are recorded but not yet read by anything (the gap is the separate plugin process, not reached by this round — see mod-compatibility.md). Tick rate's own adoption (2026-08-27, separate mechanism) is unaffected. | Built (NEEDS-LIVE) 🎮 | `BehaviourDigest.cs`, `BehaviourCompatibility.cs`, `SessionOverrides.cs`, `CoopConfig.cs`, `Packets/SessionPackets.cs`, `BehaviourAnnouncer.cs` |
| 8 | **Fog-of-war reveal** (`map_reveal_sync`) — live-verified 2026-08-27: engine call fires correctly, follows peer movement, survives a module transition with no crash, negligible frame cost. Default ON. Only the on-screen M-key appearance is unconfirmed (screenshot tooling broken). | Verified-live (partial — data path only) | `MapRevealController.cs`, `MapRevealRule.cs` |
| 9 | **Pazaak is invisible to the mod** (GUI panel, not `GetMiniGame`). `pazaak_detect` read built 2026-08-27; live-verified 2026-08-27 as a NEGATIVE check only — sat in the Taris Upper City cantina (`tar_m03aa`) with the flag on for ~1 minute, zero false-positive `pazaak:` lines, but no hand was actually started (needs a click this rig cannot inject) so the positive detection path remains unrun. Stays OFF. "partner is in a minigame — waiting" HUD line built. Swoop/turret park-and-hide unchanged; co-visible swoop never observed. | Live-verified: negative only 🎮 | `MinigameController.cs`, `MinigameNoticeRule` |
| 10 | **Companion follow-AI last-waypoint stall** — two fix candidates (`party_follow_refresh_final_node`, `party_follow_extend_final_node`), both OFF, A/B inconclusive. Now documented in `coop.ini.example`. | Partial 🎮 | `PartyFollowController.cs` |
| 11 | 🎮 **Companion released under `companion_local_combat` fights with the client's stale copy of its gear.** `companion_gear_sync` built 2026-08-27: the host's real per-companion equip resrefs ride `PartySnapshotPacket`/`PartyMemberState`, a new shared-block region carries them to the plugin, and `PartyPresenceController.ApplyCompanionEquipment` dresses the mirrored body through the existing `Odyssey.EquipItemFromTemplate` path. OFF by default, never run against the live game. | Built (NEEDS-LIVE) 🎮 | `PartyPresenceController.cs`, `SharedBlock.Party.cs`, `KotorGameBridge.Party.cs`, docs/research/companion-state-sync.md |
| 12 | **Companion XP** is outside the progression channel (only the local PC's `CSWSCreatureStats+0x068`). `companion_xp_sync` built 2026-08-27: per-slot companion XP read/written through the identical `stats+0x068` field poke, published/reported/merged (max) the same shape as the PC's own XP. Kill XP is expected to already agree independently on both sides (`DistributeExperience` grants every active member the same full amount); the channel is expected to matter mainly for script-granted XP. OFF by default, never run against the live game. | Built (NEEDS-LIVE) 🎮 | `KotorGameBridge.Party.cs`, `Pumps/PartyPump.cs`, `CompanionExperienceMerge.cs`, docs/research/companion-state-sync.md |
| 13 | **Mid-dialogue partner transition**: whether the spectator's copy of the conversation is left orphaned is NEEDS-LIVE. | Partial 🎮 | `DialogueSpectateController.cs`, `Pumps/TransitionPump*.cs` |
| 26 | **Solo-segment spectate** (see Tier 3 item 25's own entry below for the gap this closes): the product decision has been made — the OWNER's fight, the other human SPECTATES — and built as `solo_segment_spectate` 2026-08-27. `SoloSegmentMachine`/`SoloSegmentCatalogue`/`SoloSegmentRule` (pure, unit-tested) detect the roster-drop edge in `sta_m45ac`/`sta_m45ad` and gate the reaction; `SoloSegmentController` (owner-side, publishes `PlayerState.FlagSoloSegment`) and `SoloSegmentSpectateController` (spectator-side: input lock via `commandable`+`HoldCreatureStill`, camera follow reusing `SpectateController`'s mechanism, a `PeerDamageController` gate) do the engine work; `CoopAgent.PumpOnce`'s peer filter hides every peer from the owner's own world while the flag is set. OFF by default, NEEDS-LIVE: nothing here has been exercised against a real Star Forge save — needs `warp sta_m45ad` (or `sta_m45ac`) + `firescript`ing the door script per docs/research/star-forge-finale.md's own dated live-test-plan section. The input lock in particular is a best-effort, unverified pair of levers (see `SoloSegmentSpectateController`'s own doc comment) — confirming it actually stops a real WASD press is the single most important thing the live pass has to answer. | Built (NEEDS-LIVE) 🎮 | `SoloSegmentMachine.cs`, `SoloSegmentCatalogue.cs`, `SoloSegmentRule.cs`, `SoloSegmentController.cs`, `SoloSegmentSpectateController.cs`, `CoopAgent.cs`, `KotorGameBridge.SoloSegment.cs`, `SoloSegmentAnnouncer.cs`, docs/research/star-forge-finale.md |

## Tier 2 — structural limits worth stating to players (design decisions, not bugs)

| # | Item | Status |
|---|---|---|
| 14 | **One real fight per client at a time** (`_locallyFightingId` is a single slot; other engaged enemies stay mirrored while their forwarded damage still lands) and **last-attacker-only attribution** on the damage wire. Documented internally, not to players. | Decision needed — document in README limitations, log when a second candidate is deferred |
| 15 | **Only the token owner picks dialogue replies**; alignment is per character by design; romance/companion arcs belong to whoever opens them. | By design — state it in README |
| 16 | **Doors/placeables are monotonic** — re-locking/re-sealing by a script replicates nowhere. | By design |
| 17 | **`combat_state_sync` is superseded** by `local_combat` (engine re-derives the in-combat dword in 0–2 ms); a client fighting only mirrored enemies gets no combat music/auto-pause. | Decision needed — retire the flag or document |
| 18 | **Galaxy map / pre-departure party-select** have zero co-op awareness; each instance runs the vanilla UI locally. Research + design done (`galaxy-map-and-party-select.md`); recommendation #1 (extend the transition announce to `move_to_module_pending`, covering both galaxy-map legs plus every other non-door `StartNewModule()`) shipped 2026-08-27 behind `transition_announce_scripted` (default ON, NEEDS-LIVE — see `transition-smoothness.md` §11). Recommendations #2-4 (the "peer is choosing a destination" flag, party-select suppression) remain undone. | Decision needed on #2-4; #1 needs a live pass |
| 19 | **3+ peers** are architecturally supported (`MaxPeers=8`) but every live round has been two instances. | 🎮 L |
| 25 | **No gamepad/controller support.** The co-op-specific UI (server browser, password entry, the `~` console) is mouse/keyboard only, same as the rest of this project's own input surfaces (in-game menu automation, live probes). Now stated plainly in README.md/package-README.md. | By design — document, no fix planned |

## Tier 3 — needs new reverse-engineering

| # | Item |
|---|---|
| 20 | Merchant/shop inventory (`CSWSStore` layout unknown to the project) |
| 21 | Swoop race — dispatch chain never fires; blocked on a debugger (`minigames.md`) |
| 22 | Creature-liveness scan freshness (`DestroyObject`'d bodies) — `liveness_sync` stays OFF |
| 23 | Per-object local variables, time-of-day, floor mines (deliberately excluded from the trigger relay) |
| 24 | Internet play: discovery past LAN broadcast, NAT traversal/relay, WAN jitter tuning (`interp_ms` is LAN-tuned), host migration |
| 25 | ~~**The Star Forge finale has RE coverage now** (`star-forge-finale.md`) but its headline gap isn't new RE — it's a product decision: neither the stunt-module refusal nor `MinigameController`'s park/hide engages for the Bastila-chamber/Malak-duel doors (ordinary same-module `RemovePartyMember`, not a `StartNewModule` funnel), so whether a second real player can walk into the game's one-on-one Malak duel is currently undecided by this mod's own rules, not merely untested.~~ Decided and built 2026-08-27 as `solo_segment_spectate` — see Tier 1 item 26. No RE gap remains; item 26 is a live-verification gap, not a research one. |

## Execution order

Wave A (done 2026-08-27, no rig): config truth (`movie_relay` agent default, `coop.ini port=`
honoured by the menu agent, undocumented keys), `E_PROTO` reject, Welcome tick-rate adoption,
Away flag, persistent indicator, `script_item_sync`, mid-session log rotation, `MinigameRule`
wired, Pazaak detect + waiting notice, sender cap contract, missing unit tests.

Wave B (rig, serial): Tier 1 items 3, 2, 5, 8, 9, 4 as one verification pass each; then 13,
11 and 12 (`companion_gear_sync`/`companion_xp_sync`, both built 2026-08-27, both OFF pending
their own live pass — docs/research/companion-state-sync.md has the test plan), item 7
(behaviour-flag negotiation, built 2026-08-27 — NEEDS-LIVE list in mod-compatibility.md's
"Behaviour-flag negotiation" section), and item 1 (party pool, built 2026-08-27 — see
[party-pool-sync.md](research/party-pool-sync.md)'s own live test plan).

Wave C (after B): whatever B refutes.
