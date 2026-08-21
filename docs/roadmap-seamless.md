# Roadmap: the remainder of "seamless"

Written 2026-08-21 from a five-domain audit of the codebase and research trail (combat,
shared world, travel/lifecycle, dialogue/cinematics/minigames, reliability/tooling). Each
item below was checked against the code, not just the docs — two audit claims were dropped
because the code had moved past the doc (transition movies are wired and live-verified,
`b0841bf`; the host's ghost-player wipe suppression is fixed, `CoopHost.cs` "has ever
published an avatar"). Status words: **Verified-live** / **Implemented-untested** /
**Partial** / **Missing**.

Rig rule: there is ONE live two-instance rig. Items marked 🎮 need it and run one at a time;
everything else builds and unit-tests in parallel.

## Tier 1 — blocks "seamless" today

| # | Item | Status | Where |
|---|---|---|---|
| 1 | 🎮 **`local_combat`: a previously-mirrored GIT enemy never lands a hit** (frozen at point-blank, no swing). Works for a never-claimed creature (round 37). Round 46 restored `ObjCommandable`/`CreMovePermitted`/`CreRunFlag`/`CreMoveState` to their pre-claim state on exclusion and fixed a real, separate bug (`ClaimCreatureForFight`'s conditional `ClearAllActions` call disarms all 5 of the creature's own combat-round sub-structures — disassembly-confirmed) — live-tested, the freeze is UNCHANGED; both leads refuted with a live memory probe (the `AIActionPhysicalAttacks` gate bits were already set; the round sub-structures still never arm). Next concrete step: trace `RunScript`'s real dispatcher (`0x005D45D0`) rather than more `CSWSCreature` field pokes. | Partial | `EnemyPresenceController.cs`, `Odyssey.cs`; `shared-state-audit.md` §2 rounds 44-46 |
| 2 | **Client container/corpse loot never reaches the host.** `PumpContainerLoot` only publishes removals when `IsEnemyAuthority`; a client emptying a chest leaves the host's copy full — duplication. | Partial (bug) | `CoopAgent.cs:1666-1688` |
| 3 | **Non-damage combat effects cross no wire**: stun, paralysis, knockdown, slow, fear, enemy Force powers on a phantom, grenade/AoE non-damage components. Only `OnApplyDamage` is hooked. | Missing | `PhantomHitHook.cs`, new hook near effect-apply |
| 4 | **Mirrored companions cannot swing on a client** (action queue cleared every pass); only the host's companions fight. | Missing | `PartyPresenceController.cs:164-265` |
| 5 | **Logs grow without bound** (170 MB, 158 restarts concatenated) and the per-second combat diag line is unthrottled. | Bug | `PluginHost.cs:1112-1144` |
| 6 | **No agent liveness check.** Agent death = phantoms frozen forever, silently. No in-game message for port-in-use, host-unreachable, or agent-failed-to-start. | Missing | `SharedBlock.cs`, `AgentLauncher.cs`, `UdpChannel.cs:48`, `SessionManager.cs` |

## Tier 2 — real gaps a player hits in a normal playthrough

| # | Item | Status | Where |
|---|---|---|---|
| 7 | 🎮 HUD hourglass pauses but cannot unpause (second click no-ops, even locally). | Bug | `PauseKeyController.cs`; audit §6 |
| 8 | 🎮 Minigames (swoop, turret, Pazaak) have zero handling; `CSWCModule::GetMiniGame` (0x00641980) is never read. Other player's experience unmeasured. | Missing | new `MinigameController`, reuse `SpectateController` |
| 9 | `PartyPresenceController` has no `IsPartyRosterFrozen` guard (roster controller does) — presence mirroring chases companions the engine scripted away. | Partial | `PartyPresenceController.cs` |
| 10 | Dialogue consequences (item/alignment grants on a reward node) reach only the owner. XP and journal already merge; items and alignment do not. Design: [dialogue-consequences.md](research/dialogue-consequences.md). | Missing | `DialogueSpectateController.cs` |
| 11 | Materialised-loot: dedup set not reset on module change; no resync for a peer that reconnects after an op retired. | Partial | `enemy-materialisation.md` §6 |
| 12 | 🎮 Progression never live-verified against a real shop (gold ledger) or cross-side kill (XP max). | Implemented-untested | `CoopAgent.cs:815-909` |
| 13 | 🎮 Client-walks-through-door-first pull-back path has no live pass. | Implemented-untested | `CoopAgent.cs:1434-1494` |
| 14 | Mid-session save/load. Design: [session-save.md](research/session-save.md). Phase 1 shipped 2026-08-21: a client's progressed character survives to the next session (a "resume tomorrow" sidecar, preferred automatically at the next join) and a host loading a different save mid-session is detected and refused on screen rather than served silently stale. Not live-verified. Phase 2 (deferred): client-side mid-session load auto-rejoin (needs a way to tell a load from an ordinary save), host-side full re-adoption instead of refusal, session-driven (unattended) auto-save. | Partial (phase 1, unit-tested only) | `ClientCharacterPersistence.cs`, `SessionSaveWatch.cs`, `WorldAdoption.cs`; `session-save.md` "Phase 1 shipped" |
| 15 | Container loot has no version tripwire (retransmit only); tripwires are counters not digests. | Partial | `ReliableLootSender.cs`, `disconnect-reconnect.md` §4 |

## Tier 3 — polish and confidence

| # | Item | Status |
|---|---|---|
| 16 | Packaging script (drop-in zip) and CI (`dotnet test` on push). | Missing |
| 17 | Tests for the four `Reliable*Sender` classes (retry/ack logic, zero direct tests). | Missing |
| 18 | Persistent on-screen connection indicator (today: `status` console verb only). | Missing |
| 19 | Fog-of-war reveal sync (`SetWorldPointExplored`), merchant inventory, trigger-mines, per-object local variables, time-of-day, alignment sync, host migration, internet play. | Missing — documented out of scope until Tiers 1-2 close |

## Execution order

Wave A (parallel, no rig): items 2, 5, 6, 9, 11, 16, 17.
Wave B (rig, serial): item 1 first; then 7, 12, 13 as one verification pass; then 8.
Wave C (after 1 closes): items 3 and 4 — both need the combat path to be trustworthy first.
