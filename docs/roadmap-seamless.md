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
| 1 | 🎮 **`local_combat`: whether it contributes a real hit inside a live session is still unproven** — but round 51 found the "frozen at point-blank, no swing" framing itself was built on a diagnostic that doesn't detect success. Round 51 measured PLAYER HP (`console 'where'`) instead of the `is fight action` proxy every prior round trusted, and found a real, independently-verified hit (`12/12 → 1/12`, the documented min-1-hp floor) landing on a confirmed-hostile creature within ~30s in TWO clean, unconfounded, session-free trials — one on each install (client `swkotor-gog-p2`, host `GOG Games\...`) — while `is fight action: True` still read `False` for all 6000 sampled frames in both. This refutes the client install/instance as the cause of anything: the client build fights fine standalone. A third trial with a live session also showed the player take real damage, but is confounded by the standing forwarded-damage channel (host's own unmirrored copy fighting the client's phantom, a separate pre-existing mechanism), which this area's min-1-hp scene pin makes sufficient on its own to explain the reading — so whether `local_combat`'s own client-side mechanism contributes anything once a session is live remains open. Static disassembly this round also corrected round 27: the per-participant arming call (`0x004D5A50`) is NOT gated by the `sub+0x5c` check at all — both branches reach it unconditionally; `sub+0x5c=4` is `StartCombatRound`'s own "no maneuver selected yet" sentinel, not a broken state. Next: a session trial with the forwarding-channel confound controlled out (keep the phantom outside the host's own perception range, or neutral-hold the host's copy), bracketed with `console 'where'` on both sides. | Partial | `EnemyPresenceController.cs`, `CombatController.cs`, `Odyssey.cs`; `shared-state-audit.md` §2 round 51 |
| 2 | **Client container/corpse loot never reaches the host.** `PumpContainerLoot` only publishes removals when `IsEnemyAuthority`; a client emptying a chest leaves the host's copy full — duplication. | Partial (bug) | `CoopAgent.cs:1666-1688` |
| 3 | 🎮 **Non-damage combat effects cross no wire**: stun, paralysis, knockdown, slow, fear, enemy Force powers on a phantom. `PhantomEffectHook` now hooks the engine's generic effect dispatcher (`OnEffectApplied`, not a second per-type detour) and forwards the whole allowlist; Slow/Choke/Whirlwind/Frightened/Stun(droid) have a real apply-side engine call, Knockdown/Paralyze/Sleep are captured but not yet replayed (unverified `EffectComplex` signature; two `OnApplySetState` sub-codes never matched to an English name — see `effect-sync.md` §3-4). `coop.ini effect_sync=0` gates the whole channel off pending a live pass. | Partial (not live-verified) | `PhantomEffectHook.cs`, `PeerEffectController.cs`, `OdysseyEffects.cs`, `docs/research/effect-sync.md` |
| 4 | **Mirrored companions cannot swing on a client** (action queue cleared every pass); only the host's companions fight. | Missing | `PartyPresenceController.cs:164-265` |
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
