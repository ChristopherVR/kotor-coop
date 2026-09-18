# Seamless-playthrough review (2026-09-18)

Eight parallel read-only reviews, one per slice (session lifecycle, world and movement, combat,
dialogue and story, party and inventory, platforms and networking, a planet-by-planet walk of
KOTOR 1's scripted mechanics, and verification/test coverage), each cross-checking the docs'
claims against `src/` and `tests/`. The load-bearing claims below were re-checked by hand
afterwards (config defaults, the absent scaling feature, the character-capture path, the
solo-segment catalogue, repo visibility, the `latest` release commit).

**Verdict.** Endar Spire and Taris are playable co-op and well verified. From Dantooine onward
the mod has never been run on the rig, and four story mechanics have no co-op surface at all
(the joiner's Jedi class, the Leviathan, Manaan's Hrakert Rift, the STUNT/credits chain). Two
shipped config defaults silently turned off channels a full run depends on. Nobody outside the
repo can download a build with the 2026-09-10/09-17 fixes.

Status classes: BUG / MISSING / DESIGN (a deliberate limit) / UNVERIFIED (built, never observed
live) / DOC (documentation contradicts code). Effort S/M/L; "RE" = needs new reverse engineering.
Items marked **done 2026-09-18** were fixed in the same pass that produced this document; see
§3 and §5 for the running log.

## 1. Blockers for a full playthrough (ranked)

| # | Gap | Class | How a run hits it | Evidence | Effort |
|---|---|---|---|---|---|
| 1 | The joiner never becomes a Jedi. Zhar's class choice runs `AddMultiClass` on the driver's engine only; no code or doc handles class, feat or Force-power grants for the other player. | MISSING | Dantooine, every run. Joiner keeps XP (max-merged) but no Jedi class, saber feat or powers; every later `GetLevelByClass` gate fails on their engine. | `dialogue-consequences.md` §1.2 scanned seven routines, none class/feat; grep `AddMultiClass`/`GrantFeat` over src+docs is empty | L, RE |
| 2 | Companion recruitment off by default. | DOC + default | First dialogue recruit (Bastila, Mission/Zaalbar, Canderous, T3, Juhani, Jolee, HK-47): the non-driver's roster cannot seat the companion; never mirrored on their screen. | `PartyRosterController.cs:81` `ReadBool(..., false)`, `CoopConfig.cs:939`; template has no line; `dialogue-consequences.md` L1408 says "flipped ON 2026-08-24" | S, **done 2026-09-18** (default ON) |
| 3 | Liveness sync silently off. | BUG + DOC | Any scripted `DestroyObject` or out-of-census kill diverges between screens. Both digests default OFF so no `W_CFG` fires; a client that sets 1 is host-overridden back to 0. | `LivenessReconcileController.cs:38`, `CoopConfig.cs:527,940`; template line removed by `d3369b4`; CONFIG.md said "ON since 2026-09-03" | S, **done 2026-09-18** (default ON) |
| 4 | Leviathan capture, brig, escape unresearched. | UNVERIFIED (high risk) | PC removed from party, one companion under script control, Bastila removed, `m12ab` turret, Saul's `GiveItem`. Both engines run the module scripts with their own PC, so two independent brig sequences run; `PartyFreezeRule` only pauses mirroring. | `party-reconciliation.md:186`, `minigames.md:305`; no doc reads the `lev40` scripts | L, RE + live |
| 5 | Manaan Hrakert Rift (envirosuit, solo by design) has no surface. | MISSING | The underwater module is an ordinary door transition: the joiner follows in unsuited; firaxa/Selkath OnEnter scripts double-fire. | grep envirosuit/manm28/Hrakert over src+docs is empty | L, RE |
| 6 | Follower refused from every STUNT module except the galaxy-map leg. | DESIGN, now MISSING | Taris vision, every Bastila/Revan vision, romance payoffs (STUNT_42/44), the ending chain STUNT_50a to 57: the joiner never sees the ending. Movie carry-over capped at the engine's six; a 5-6 hop credits chain never tested. | `TransitionPump.Travel.cs:180`, `cinematics.md` §4.3, `star-forge-finale.md` §3.6, §6 #3 | M (generalise the galaxy-map replay) |
| 7 | Manual/quick saves bake session spawns into the save. | BUG (likely) | Host presses F4 with the partner beside them: stand-ins, materialised enemies, replicated ground items and pins are serialised. Pre-store flush runs only during door transitions / session warps; no quicksave/save-panel hook; no load-time sweep by the `coop_phantom` tag. The "6/6 clean host reload" rounds reloaded the transition autosave, which IS flushed. | `PluginHost.Frame.cs:511-527,615-635`, `Odyssey.Menu.cs:189`, `PluginHost.SessionLifecycle.cs:110`, `cinematics.md` §7.7 | M (S for a world-up sweep) |
| 8 | Joiner's co-op character likely carries the Endar Spire min-1-HP pin forever. | BUG (needs one live check) | Character is captured by quicksave right after `end_m01aa` loads, where `k_pend_area01` pins the PC; only `end_m01ab`'s OnEnter clears it; the extractor does not strip `Min1HP`; nothing clears it on adoption. The joiner can then never be downed. | `NewCharacterCaptureController.cs` (capture flow), `min-one-hitpoint.md` §2, `death-handling.md` §5 #7 | S |
| 9 | Player-count scaling not built. | MISSING | `enemy_damage`, `enemy_health`, `xp_rate`, `store_stock`, `store_prices` (v1 scope, 2026-09-03) appear nowhere in src/docs/tests/profiles. Every fight is vanilla-balanced for two PCs. | grep empty | M (+L for prices, RE) |
| 10 | Sith-fighter turret ambushes (`m12ab`) never completed by two players. | UNVERIFIED | Mandatory several times per run. Both engines auto-enter, the partner's stand-in is destroyed, an unattended run tore the host down to the main menu with the client parked. | `MinigameModuleRule`, `minigames.md` §9.2/§9.4 | M + rig |
| 11 | Dark-side branch has no surface. | MISSING | Temple-summit companion faction flips and the DS ending are not a synced channel; mirrored companions keep held AI, so the joiner cannot fight them or fights them twice. | `star-forge-finale.md` §3.1 INFERRED, roadmap #15 | M-L |
| 12 | Nobody can download the mod. | Infra | Repo PRIVATE, so Pages never deploys and download links need a login. `latest` = `c534a83` (2026-09-06) because the artifact quota blocked packaging from about 09-16; none of the 09-10/09-17 fixes is in any package. Green CI = managed code compiles + pure tests pass. | `pages.yml:4-5`, `site/install.html:33`, runs 35159515076/35160894578/35176302674 red, release run 35214206452 "nothing to publish" | S |

## 2. Gaps a run will notice but can play through

- **Mines/floor traps not synced.** Trigger relay deliberately skips the mine branch; only placeable `trap_flag` disarm mirrors. A mine disarmed on one side stays live on the other; a placed mine exists only locally. `TriggerFireHook.cs:16`, `OdysseyTriggers.cs:142-243`, `placeables-containers.md` §3 (state bytes NEEDS-LIVE). M, RE.
- **Disguises don't mirror.** `OnApplyDisguise` is not among the 12 forwarded effect kinds: Sith uniform, Sand People robes, envirosuit. M.
- **Duel rings have no parking.** `SoloSegmentCatalogue` lists only `sta_m45ac/ad`; `solo_segment_any_module` OFF. Taris ring / Korriban student duels: the partner's targetable stand-in stands in the ring and its damage forwards. S + rig.
- **Client-cast non-damage effects on enemies are cosmetic.** `PhantomEffectHook` captures only `target == phantom`; the enemy-side wire carries damage only. Stasis/Stun/Horror/Whirlwind/Push/Slow/Plague and grenade riders from the client do nothing on the host's authoritative body. L, RE. Also: enemy Stun (sub-code 4) and Fear (2) on a stand-in are named but not on the `PeerEffectRules` allowlist (S).
- **No way to heal or buff your partner.** Medpacs and party-targeted powers cannot target the stand-in (`effect-sync.md` §10.1, §11.4). limitations.md overstated this. M-L.
- **Joiner healing/equipping a mirrored companion.** Companion HP and gear are host-only: a medpac is consumed and overwritten; an item equipped on Carth by the joiner leaves the pool and is gone after the next gear publish (`PartyPackets.cs:15-25`, `CompanionDamageLedger.cs:19`). S to block, M to forward.
- **Per-object locals never sync.** `SetLocalBoolean/Number` (talked-to flags, placeable state machines) are per instance; the joiner restarts an NPC from the first-meeting branch. `quest-sync.md` §8. M-L, RE.
- **Doors/placeables are monotonic.** Story re-locks (Sith base, Leviathan, Star Forge) leave the joiner's copy open. `DoorFlags.cs` max-merge. M.
- **Client-led departure through a party-select screen never observed.** Whether the swept panel's exit script still runs is the open question in `PartySelectSuppressionController.cs:20-27`; fallback #4 undone. If it does not run, only the host can leave the Ebon Hawk. M, live first.
- **Movie-first scripted legs** (escape pod, Leviathan capture, Star Forge) rely on the slow "host's module changed" settle path; follower arrives late into forced dialogue. `transition-smoothness.md` §11.7. S-M.
- **Host parked at the main menu / load screen** leaves joiners in a frozen world with every lock held; only silence or `HostBye` releases them (`HostLossRule.cs`, `CoopAgent.cs:404-420`). S-M.
- **Stealth not synced.** No stealth flag in `PlayerState`; the stand-in stays visible and targetable. S-M, small RE.
- **Pazaak cards lost on every rejoin.** `AddItem` converts cards into PARTYTABLE, which adoption replaces; nothing in src mentions cards. M, RE.
- **Internet play without a tunnel is a reproduced failure.** `network-topology.md` §16 (2026-09-10): status line said "UPnP mapped", port unreachable (CGNAT/double NAT suspected). No reachability probe, no relay, no hole-punch; the only working path (playit.gg) needed `world_transfer_udp_fallback=1` by hand. No in-game address box exists although `site/play.html:94` and `package-README.txt:93,253` describe one; the joiner types `connect host:port` in the console. S probe + default, M box, L relay.
- **Windows Firewall never handled.** Agent starts windowless, so the prompt lands behind a fullscreen game; no installer rule. S.
- **Door/chest keys can collide across a long game.** 8-bit module hash + low 16 bits of object id in a never-pruned `EventFlagStore`; over about 60 modules a collision is near-certain and max-merge would open a later chest. No rig run exceeded three modules. `DoorFlags.cs:6-30`, `PlaceableFlags.cs:25`. M.
- **Co-op character captured only at door autosaves and clean disconnects.** `CaptureCharacterOnDisconnect` reads the newest save without saving first; a crash after the last door loses levels and can dupe/lose gear. S.
- **XP max-merge lifts the host** when a veteran co-op character joins an earlier host save (`PartyPump.cs:219`). S.
- **Sticky aggro is alternation.** Host-side damage is not recorded in `ThreatTable`; one remote peer attributable; 4 s cooldown flips the enemy back. M.
- **Enemy census is the 32 nearest**; beyond-cap creatures run private fights on the client while forwarded host damage still applies (possible double damage). M.
- **Difficulty setting is per instance, never compared.** S.
- **Auto-pause / tutorial windows force-disabled** under `keep_alive_unfocused`; menu-open pauses are local. Undocumented. S.
- **Rejoin poll gives up at 60 s and reconnects anyway** (`TriggerRejoin`, no failure branch). S.
- **Rejoin gates read OFF when coop.ini is missing** (`ClientLoadRejoin`/`HostLoadReadopt` `?? false`; code default true). S.
- **Multi-peer rejoin after a host reload**: serial world serving, untested. S test / M.
- **Localised or patched exes**: no whole-image identity gate; hooks refuse individually, dozens of absolute reads don't. S gate, L localised RE.
- **Linux/Steam Deck**: ten open items in `proton.md` §6; CI's linux-install skipped since the quota hit. Needs a Linux box.
- **Controller**: XInput only, no typing, Steam Input untested, no physical pad ever tried.
- **WAN tolerance**: `interp_ms` fixed 80 ms, no RTT-adaptive delay; IPv6 parsed but sockets bind IPv4 only.
- **Mod matrix**: never tested against K1R/K1CP/BoS/high-res menus; Co-op button depends on retail `mainmenu.gui`.
- **Crash tooling is developer-only**; no "collect logs" verb; `install.ps1` needs pwsh 7.
- **Overlay/chat/indicator layout** unverified off the rig's resolution.
- **Long-session growth**: `ThreatTable.Forget` has no callers; `PeerDamageLedger._seen` grows per rejoin; SaveMismatch rejoin has no attempt cap; `ContainerLootStore` evicts after 512 removals; `DialogueGrantSender` REFUSES grants when 256 are pending; agent log rotates only at construction.

## 3. Documentation corrections (2026-09-18)

- `companion_local_combat`: roadmap closed #4 said "ON in the shipped ini"; code default is OFF.
- `dialogue_recruit_sync`: research doc said ON, code and CONFIG.md said OFF. Default flipped ON in code; docs reconciled.
- `liveness_sync`: CONFIG.md/status said ON since 09-03; code default was OFF and the template line was gone. Default flipped ON in code; docs reconciled.
- `companion_damage_sync` and `local_combat_slots` still tagged NEEDS-LIVE in status-engineering.md Appendix; both verified 2026-09-02 (`live-verification.md:2781-2996`).
- Galaxy-map REFUSE case: roadmap Tier 2 #18 said NEEDS-LIVE; verified 2026-09-17.
- Internet play: status/limitations said "never watched against a real router"; the direct path was tried and FAILED on 2026-09-10 (§16).
- v0.10.0 tagged 2026-09-04; README/limitations still said v0.9.0.
- limitations.md: "Your character comes from your latest save" contradicts `join_mode=auto` (sidecar).
- limitations.md: pausing row implied all pauses are shared; menu-open pauses are local.
- limitations.md: "Stun, slow, choke, heal … land on your partner" overstated; party-targeted heals/buffs cannot reach the stand-in.
- limitations.md: story re-locks called "rare, never blocks progress"; softened.
- Stale code comments: `Odyssey.Journal.cs:250` (journal removal "OFF"), `PartyPump.cs:348`/`IPartySession.cs:68` (`companion_xp_sync` OFF), `PartyRosterController.cs:41-45` ("one live data point").
- roadmap-seamless.md: added the 09-10/09-17 fixes and the new open items from this review.

## 4. Verification state

- `live-verification.md` stopped at 2026-09-03; later passes (09-10, 09-17) live in `docs/research/*.md` and the status tables.
- All live evidence: Endar Spire + Taris saves plus `warp`s into single modules (tat_m20aa, tat_m17ac, sta_m45ac, unk_m44aa, kas_m24aa, lev_m40aa). Dantooine, Manaan and Korriban modules have never been rig-loaded. No playthrough doc exists past Taris.
- About 2,850 pure-rule tests; 215 of 301 plugin files (every Controller/Hook/Odyssey wrapper) have no matching test by design; the NativeAOT plugin is built in CI but never executed.
- `verify.ps1` unchanged since 2026-08-22: 62 checks, none for galaxy map, party pool, recruit, companion gear, threat, solo segment, swoop, pazaak, wipe/rejoin. Nothing automated crosses more than one door.
- Still unverified: physical keyboard vs the swoop/duel lock, 3+ peer door soak, Linux/Deck, physical pad, on-screen look of the connection line and map reveal, `MaterialisedLootHistory` reconnect resync, the three `party_follow_*` A/B flags.

## 5. Order of work and running log

1. Same day: flip `dialogue_recruit_sync` and `liveness_sync` ON, default `world_transfer_udp_fallback` ON, rejoin-gate and rejoin-poll fixes, free the CI artifact quota so a package exists, doc reconciliation.
2. One live afternoon: min-1-HP pin on the co-op character, manual-save ghost, client-led party-select departure, turret ambush.
3. Research, in the order a run hits them: Jedi class/power grants for the joiner; the Leviathan; the STUNT/credits chain; Manaan's Rift; the dark-side branch.

Log entries are appended below as items close.
