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
| 18 | Persistent connection indicator | `ConnectionIndicatorController` (2026-08-27, NEEDS-LIVE) — was a fading toast before |

## Tier 1 — what a normal two-player playthrough still hits

| # | Item | Status | Where |
|---|---|---|---|
| 1 | **Party shared-loot pool (`pt_item_repo`, `CSWPartyTable+0x118`) is two unrelated pools.** Gear stashed "in the party" on one instance never exists on the other. Offsets known, no channel. | Missing | new `PartyLootController` + packet; `Odyssey.Party.cs` |
| 2 | **Items granted by scripts outside dialogue** (OnEnter, OnDeath, cutscene, heartbeat — most quest items) reach only the engine that ran the script. `script_item_sync` pump built 2026-08-27, OFF until live-verified. | Built (NEEDS-LIVE) 🎮 | `Pumps/ScriptItemGrantPump.cs`, `ScriptItemGrantRule` |
| 3 | 🎮 **Progression never live-verified** against a real vendor purchase (gold ledger) or a cross-side kill (XP max-merge). Code is complete. | Built (NEEDS-LIVE) | `Pumps/PartyPump.cs:159-282` |
| 4 | 🎮 **Container-loot race**: two clicks on the same chest within one RTT can both hand out loot before either removal lands (`ContainerUseRule` is eventually consistent). Never exercised. | Partial | `Pumps/WorldStatePump.cs:24-52`, `ContainerUseRule.cs` |
| 5 | **A peer at the main menu / load screen looked like a frozen statue forever.** `FlagAway` built 2026-08-27: agent publishes Away after sustained read failure, peer parks the phantom and toasts. Plugin-side hide is NEEDS-LIVE. | Built (NEEDS-LIVE) 🎮 | `CoopAgent.cs`, `AwayRule`, `PhantomController*.cs` |
| 6 | **Protocol-version mismatch used to be silence** (the 15 s "no reply" timeout). Now a typed `E_PROTO` Reject both ways (2026-08-27). | Built | `CoopHost.cs`, `CoopClient.cs`, `PacketCodec.PeekTypeAnyVersion` |
| 7 | **`coop.ini` divergence between peers is unchecked.** Every behaviour flag (`local_combat`, `party_fast_pump`, the load-rejoin gates, `follow_lead`, interpolation) runs from local disk with no negotiation or warning, unlike game content which is fingerprinted per channel. Tick rate is now adopted from the host's Welcome (2026-08-27). | Partial | `CoopConfig.cs`, `Packets/SessionPackets.cs`, `ContentCompatibility.cs` |
| 8 | **Fog-of-war reveal** (`map_reveal_sync`) — built against the statically verified `SetWorldPointExplored`, OFF, never run. | Built (NEEDS-LIVE) 🎮 | `MapRevealController.cs`, `MapRevealRule.cs` |
| 9 | **Pazaak is invisible to the mod** (GUI panel, not `GetMiniGame`). `pazaak_detect` read built 2026-08-27, OFF until live-verified; "partner is in a minigame — waiting" HUD line built. Swoop/turret park-and-hide unchanged; co-visible swoop never observed. | Built (NEEDS-LIVE) 🎮 | `MinigameController.cs`, `MinigameNoticeRule` |
| 10 | **Companion follow-AI last-waypoint stall** — two fix candidates (`party_follow_refresh_final_node`, `party_follow_extend_final_node`), both OFF, A/B inconclusive. Now documented in `coop.ini.example`. | Partial 🎮 | `PartyFollowController.cs` |
| 11 | 🎮 **Companion released under `companion_local_combat` fights with the client's stale copy of its gear** — no equipment channel for companions. Unmeasured. | Missing | `PartyPresenceController.cs`, `Odyssey.Equipment.cs` |
| 12 | **Companion XP** is outside the progression channel (only the local PC's `CSWSCreatureStats+0x068`). | Missing | `KotorGameBridge.Party.cs:154-186` |
| 13 | **Mid-dialogue partner transition**: whether the spectator's copy of the conversation is left orphaned is NEEDS-LIVE. | Partial 🎮 | `DialogueSpectateController.cs`, `Pumps/TransitionPump*.cs` |

## Tier 2 — structural limits worth stating to players (design decisions, not bugs)

| # | Item | Status |
|---|---|---|
| 14 | **One real fight per client at a time** (`_locallyFightingId` is a single slot; other engaged enemies stay mirrored while their forwarded damage still lands) and **last-attacker-only attribution** on the damage wire. Documented internally, not to players. | Decision needed — document in README limitations, log when a second candidate is deferred |
| 15 | **Only the token owner picks dialogue replies**; alignment is per character by design; romance/companion arcs belong to whoever opens them. | By design — state it in README |
| 16 | **Doors/placeables are monotonic** — re-locking/re-sealing by a script replicates nowhere. | By design |
| 17 | **`combat_state_sync` is superseded** by `local_combat` (engine re-derives the in-combat dword in 0–2 ms); a client fighting only mirrored enemies gets no combat music/auto-pause. | Decision needed — retire the flag or document |
| 18 | **Galaxy map / pre-departure party-select** have zero co-op awareness; each instance runs the vanilla UI locally. | Decision needed |
| 19 | **3+ peers** are architecturally supported (`MaxPeers=8`) but every live round has been two instances. | 🎮 L |

## Tier 3 — needs new reverse-engineering

| # | Item |
|---|---|
| 20 | Merchant/shop inventory (`CSWSStore` layout unknown to the project) |
| 21 | Swoop race — dispatch chain never fires; blocked on a debugger (`minigames.md`) |
| 22 | Creature-liveness scan freshness (`DestroyObject`'d bodies) — `liveness_sync` stays OFF |
| 23 | Per-object local variables, time-of-day, floor mines (deliberately excluded from the trigger relay) |
| 24 | Internet play: discovery past LAN broadcast, NAT traversal/relay, WAN jitter tuning (`interp_ms` is LAN-tuned), host migration |

## Execution order

Wave A (done 2026-08-27, no rig): config truth (`movie_relay` agent default, `coop.ini port=`
honoured by the menu agent, undocumented keys), `E_PROTO` reject, Welcome tick-rate adoption,
Away flag, persistent indicator, `script_item_sync`, mid-session log rotation, `MinigameRule`
wired, Pazaak detect + waiting notice, sender cap contract, missing unit tests.

Wave B (rig, serial): Tier 1 items 3, 2, 5, 8, 9, 4 as one verification pass each; then 13, 11.

Wave C (after B): item 1 (party pool), item 7 (config negotiation), item 12 (companion XP).
