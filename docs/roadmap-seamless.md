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
| 1 | 🎮 **`local_combat`: PROVEN, live, real lethality confirmed (round 53).** Round 52 proved the mechanism lands real hits, session-confound controlled out, but left natural aggro, real lethality and an outright kill open. Round 53 ran a full ordinary fight on `tar_m10aa` (a module with no scripted min-1-hp pin) against a GIT-placed, non-plot Black Vulkar (`0x33`, 22 hp): 26 ground-truth-confirmed local hits landed (raw sum 178), entirely through `local_combat`'s own mechanism with zero forwarded-channel activity in the window at all — the cleanest isolation yet. The player floored at 1 hp mid-fight not from this area (which has none) but from a STALE min-1-hp status effect carried over from the Endar Spire (`SetMinOneHP` is a persistent effect, only ever cleared by walking through `end_m01ab` — this round's warp skipped it); clearing it live (`cheat god 0`) revealed the true total was already deeply negative (`hp -11/12`), and the engine's own death path fired: `death: the local player is down`, with the down state correctly synced to the host's own screen (`console 'peers'` → `Malak tar_m10aa [down]`). **Natural aggro: still zero** — the third distinct creature/module pair to show it. **The outright-kill half is now narrowed to a specific point of failure, not just "unproven":** the client's own damage-dealt ledger populated correctly (`id 0x33 cumulative 163`, read directly out of the shared block), but the host's `DamageOwed*` fields never moved for the whole fight (no `combat: applied` line, not even the documented unresolvable-id fallback) — the report never reaches `PeerDamageController.ApplyOwed`; malformed packets, netsim, peer registration and the module label were all ruled out live, narrowing the break to somewhere between `CoopClient.ReportEnemyDamage`'s send and `CoopHost.GetEnemyDamage`'s merge. One real bug found and fixed along the way: the just-shipped roadmap item 14 host-refusal false-positived on an ordinary module transition (KOTOR's own engine autosaves on every transition, tripping the same digest watch a real different-save load would) — gated behind new coop.ini `session_save_host_refusal` (default OFF) rather than left to disconnect every host on their first door. | Verified-live | `PhantomHitHook.cs`, `EnemyPresenceController.cs`, `IncomingDamageController.cs`, `Odyssey.cs`, `CoopConfig.cs`, `CoopCommandDispatcher.cs`; `shared-state-audit.md` §2 round 53 |
| 2 | **Client container/corpse loot never reaches the host.** `PumpContainerLoot` only publishes removals when `IsEnemyAuthority`; a client emptying a chest leaves the host's copy full — duplication. | Partial (bug) | `CoopAgent.cs:1666-1688` |
| 3 | 🎮 **Non-damage combat effects cross no wire**: stun, paralysis, knockdown, slow, fear, enemy Force powers on a phantom. `PhantomEffectHook` now hooks the engine's generic effect dispatcher (`OnEffectApplied`, not a second per-type detour) and forwards the whole allowlist; Slow/Choke/Whirlwind/Frightened/Stun(droid) have a real apply-side engine call, Knockdown/Paralyze/Sleep are captured but not yet replayed (unverified `EffectComplex` signature; two `OnApplySetState` sub-codes never matched to an English name — see `effect-sync.md` §3-4). `coop.ini effect_sync=0` gates the whole channel off pending a live pass. | Partial (not live-verified) | `PhantomEffectHook.cs`, `PeerEffectController.cs`, `OdysseyEffects.cs`, `docs/research/effect-sync.md` |
| 4 | **Mirrored companions cannot swing on a client** (action queue cleared every pass); only the host's companions fight. Built 2026-08-21: `CompanionLocalFightRule` releases the ONE companion standing within `companion_local_combat_range` of BOTH this player and the enemy `local_combat` (item 1) is already fighting locally, to its own engine AI — gated on the flag, the roster freeze, and the triple-proximity check; `PartyPresenceController` wires the release/reclaim edges, restoring the host-authoritative health mirror on reclaim. `PeerDamageController`'s existing roster-wide credit set needed no change for a released companion's hits to forward. Design question (whose companions fight, against what, and why the gate is this tight) answered in `docs/research/party-reconciliation.md`'s dated 2026-08-21 entry. `coop.ini companion_local_combat=0` (OFF by default) — unit-tested only, no live pass yet. | Implemented-untested | `CompanionLocalFightRule.cs`, `PartyPresenceController.cs`, `docs/research/party-reconciliation.md` |
| 5 | **Logs grow without bound** (170 MB, 158 restarts concatenated) and the per-second combat diag line is unthrottled. | Bug | `PluginHost.cs:1112-1144` |
| 6 | **No agent liveness check.** Agent death = phantoms frozen forever, silently. No in-game message for port-in-use, host-unreachable, or agent-failed-to-start. | Missing | `SharedBlock.cs`, `AgentLauncher.cs`, `UdpChannel.cs:48`, `SessionManager.cs` |

## Tier 2 — real gaps a player hits in a normal playthrough

| # | Item | Status | Where |
|---|---|---|---|
| 7 | 🎮 HUD hourglass pauses but cannot unpause (second click no-ops, even locally). | Bug | `PauseKeyController.cs`; audit §6 |
| 8 | 🎮 Minigames (swoop, turret, Pazaak) have zero handling; `CSWCModule::GetMiniGame` (0x00641980) is never read. Other player's experience unmeasured. | Missing | new `MinigameController`, reuse `SpectateController` |
| 9 | `PartyPresenceController` has no `IsPartyRosterFrozen` guard (roster controller does) — presence mirroring chases companions the engine scripted away. | Partial | `PartyPresenceController.cs` |
| 10 | 🎮 Dialogue consequences (item/alignment grants on a reward node) reach only the owner. XP and journal already merge; alignment is per-character and correctly diverges. Item grants and recruitment-availability are now built (2026-08-21, see the "Shipped" section of [dialogue-consequences.md](research/dialogue-consequences.md)) — item grants diff-detect and give through the existing giveloot cheat channel (`dialogue_item_sync`, on by default); recruitment pokes the party-table availability flag directly (`dialogue_recruit_sync`, OFF by default — the flag-alone-resolves-a-template question is NEEDS-LIVE). No part of either path has been run against the real game yet. | Implemented-untested | `DialogueGrantSender.cs`, `CoopHost.cs`/`CoopClient.cs`/`CoopAgent.cs`, `CheatController.cs`, `PartyRosterController.cs`, `Odyssey.cs` |
| 11 | Materialised-loot: dedup set not reset on module change (fixed 2026-08-21). Resync for a peer that reconnects after an op retired: built 2026-08-21 — a bounded per-module history (`MaterialisedLootHistory`) replayed to a (re)joining peer through the same reliable sender, from the same `HandleHello` hook container loot's resync already uses; the client's existing dedup makes a re-offer to a peer that already has it a no-op. No protocol change. NOT live-verified — code-level fix + loopback-socket tests only, not run against the two-instance rig. | Implemented-untested | `MaterialisedLootHistory.cs`, `CoopHost.cs` (`PublishMaterialisedLoot`/`PublishEnemies`/`ResyncMaterialisedLoot`), `enemy-materialisation.md` §6 |
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
