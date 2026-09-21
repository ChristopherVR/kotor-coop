# Seamless-playthrough review (2026-09-18)

**Reading this on 2026-09-21:** the ranked tables below preserve the original review.
Use the dated running log and the current remaining-work table at the end for present status;
several original blockers have since been implemented or live-tested.

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
| 7 | Manual/quick saves bake session spawns into the save. | BUG (likely) | Host presses F4 with the partner beside them: stand-ins, materialised enemies, replicated ground items and pins are serialised. Pre-store flush runs only during door transitions / session warps; no quicksave/save-panel hook; no load-time sweep by the `coop_phantom` tag. The "6/6 clean host reload" rounds reloaded the transition autosave, which IS flushed. | `PluginHost.Frame.cs:511-527,615-635`, `Odyssey.Menu.cs:189`, `PluginHost.SessionLifecycle.cs:110`, `cinematics.md` §7.7 | **done 2026-09-18 (load-time sweep, live-verified: 26/59 → 25/58; save-time flush still open)** |
| 8 | Joiner's co-op character likely carries the Endar Spire min-1-HP pin forever. | BUG (needs one live check) | Character is captured by quicksave right after `end_m01aa` loads, where `k_pend_area01` pins the PC; only `end_m01ab`'s OnEnter clears it; the extractor does not strip `Min1HP`; nothing clears it on adoption. The joiner can then never be downed. | `NewCharacterCaptureController.cs` (capture flow), `min-one-hitpoint.md` §2, `death-handling.md` §5 #7 | **done 2026-09-18, live-verified (joiner 9 → -11 hp, downed)** |
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
- **Internet play without a tunnel is a reproduced failure.** `network-topology.md` §16 (2026-09-10): status line said "UPnP mapped", port unreachable (CGNAT/double NAT suspected). No reachability probe, no relay, no hole-punch; the only working path (playit.gg) needed `world_transfer_udp_fallback=1` by hand. ~~No in-game address box exists although `site/play.html:94` and `package-README.txt:93,253` describe one; the joiner types `connect host:port` in the console.~~ **Address box built 2026-09-18** (the browser's last row, "Connect to an address...", opens the engine's text panel pre-filled with the last address and publishes `connect host:port` through the console channel; `server-browser.md` "The address box", NEEDS-LIVE). S probe + default, L relay.
- **Windows Firewall never handled.** Agent starts windowless, so the prompt lands behind a fullscreen game; no installer rule. S.
- **Door/chest keys can collide across a long game.** 8-bit module hash + low 16 bits of object id in a never-pruned `EventFlagStore`; over about 60 modules a collision is near-certain and max-merge would open a later chest. No rig run exceeded three modules. `DoorFlags.cs:6-30`, `PlaceableFlags.cs:25`. M. **Done 2026-09-18**: 64-bit key with the full 32-bit module hash (`ObjectKeyedFlags.cs`), protocol 53, asserted collision-free over all 117 shipped modules; `EventFlagStore.Prune` added but not wired to transitions (peers apart need other modules' entries) — container-sync.md §12.
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

- 2026-09-18: step 1 done (defaults, digest, rejoin gates, doc reconciliation; commit 0f8e9de). Blocker #8 confirmed offline (rig sidecar `Min1HP = 1`) and closed: `PlayerIfo.BuildPifo` strips the pin, `CarriedPinReleaseController` releases one that rode in; live: the joiner's `hurt 20` read `9 -> -11 hp (downed)` on the Endar Spire deck, which was impossible before (min-one-hitpoint.md, 2026-09-18 section). Blocker #7 confirmed live (host quicksave with the joiner present: 26 creatures / 59 waypoints vs 25 / 58) and closed on the load side: `GhostSweepController` + tagged map pins (save-ghosts.md); live: both sides loading that save swept 1 stand-in + 1 pin, and a re-save with no peer read 25 / 58. The first sweep build crashed on a stale vtable match (rva 0xB69AA, `CopyAsciiBounded`) — candidates are now verified through the engine's object table first. Save-time flush (roadmap #36 second half) still open. Site restyled (c779524). CI artifact quota: the account's other repositories hold ~15.5 GB of live artifacts (AbioticEditor 7.8 GB, pptx-viewer 5.8 GB, kotor-mod-manager 1.4 GB, iptv 0.5 GB); this repo holds none — the block is account-level and needs those cleared (user decision).
- 2026-09-18 (§2 address box): built, not yet live. The browser's listbox gets a permanent last row, "Connect to an address..."; confirming it (Connect, or Enter/A on the row) floats the same `CSWGuiSaveNamePanel` the password ask uses (`PasswordEntryModal`, new `Ask.Address`), pre-filled with the last address (sidecar `kotorcoop-last-address.txt`, else coop.ini `host`/`port`; coop.ini is never written). OK runs `ConnectAddressRule` (pure, 40 xUnit cases incl. agreement with `CoopCommand.TryResolveEndpoint` and `SteamConnectStringRule`): a valid `host[:port]` / `[v6]:port` becomes the console line `connect host:port`, published only after the browser sweep, so the agent takes the verified typed-`connect` path (E_PASSWORD → serial → same modal over the menu); invalid text re-captions the panel title and stays open; empty is cancel. Gamepad: the title reads "Host address (keyboard only)". NEEDS-LIVE checklist in `server-browser.md`. Docs: limitations/site/package README/README/CONFIG/status/roadmap #46 now describe the real control.
- Still to run from step 2: client-led party-select departure, Sith-fighter turret ambush.
- 2026-09-18: §2 door/chest key collision (roadmap #47) closed statically. The old 8-bit fold does collide over the real 117-module list (pinned by a test, not just the birthday estimate). Flag ids are 64-bit from protocol 53: `tag8 | moduleHash32 | objectId24`, globals unchanged in the low 32 bits; op 21 -> 25 bytes, snapshot entry 13 -> 17, page 106 -> 81 entries. `TryApplyWorldState` now refuses object keys before the enum cast (a truncated 64-bit key could have landed on a real global). Per-module `EventFlagStore.Prune` exists but nothing calls it on a transition — a peer in another module still needs the entries. container-sync.md §12.

## Wave 2 (2026-09-19): nine parallel agents against this document

Nine agents worked the list in isolated worktrees (one on the live rig). Merged into main in one
pass, reconciled by hand; build `-warnaserror` clean (solution and plugin project). Everything
below marked NEEDS-LIVE is built and unit-tested but has not run in the game.

**Version reconciliation.** Four agents each claimed "the next" protocol version and two claimed
shared-block version 68 at the same offset. Final numbers: protocol 53 = 64-bit door/placeable
flag keys, 54 = `FlagStealth` + effect kinds, 55 = `ClassGrant`/`ClassGrantAck` packets,
56 = `BehaviourDigest` scheme 2 (scaling percentages). Shared block 68 = class-grant relay at
0x4C20..0x4C38, 69 = scaling words moved to 0x4C38..0x4C40 (the two had collided at 0x4C20);
plugin signature byte 0x45, `TotalSize` unchanged.

| Item | Status | Where |
|---|---|---|
| #1 Jedi class for the joiner | Partly built, NEEDS-LIVE. `AddMultiClass` relay (routine 389, handler 0x0052DDC0, splice 0x0052DDF6) behind `class_grant_sync`, default OFF, plus an `addclass` dev verb. Feats and Force powers are NOT covered: K1 has no GrantFeat/GrantSpell routines, those come from the level-up screen and still need their own channel | `research/class-and-power-grants.md` |
| #4 Leviathan | Researched, no code. Rescuer swap is `SwitchPlayerCharacter` (routine 11, changes the player object id twice); host owns the brig, joiner is a parked spectator; the solo-segment catalogue cannot detect it (no `RemovePartyMember` burst). RE targets: handler 0x00544910 / `CSWPartyTable::SwitchPlayerCharacter` 0x005667C0, party table +0xF0 | `research/leviathan.md` |
| #5 Manaan Rift | Researched, no code. Airlock strips the party into `K_PARTY_STORE1/2`; suit comes from `k_pman_suituse`. Recommended: refuse the follow into `manm28ab`/`manm28ad`, park the joiner in `manm28aa` | `research/manaan-hrakert-rift.md` |
| #11 Dark side | Researched, no code. `test3` flips Jolee and Juhani hostile via `ChangeToStandardFaction` (handler 0x0052F2F0); `PartyMemberState` carries no faction; Bastila's join uses NPC slots 15..19 outside the 16-bit mask | `research/dark-side-branch.md` |
| #6 STUNT/credits chain | Built, NEEDS-LIVE. `follow_stunt` (default ON): a leader's announced StartNewModule into a STUNT module is replayed on the follower's engine; first hop only, follows held inside the leg, echo de-dup | `research/cinematics.md` §12 |
| #7 Manual-save ghosts, save-time half | Built OFF, NEEDS-LIVE. `SaveFlushHook` on `CServerExoAppInternal::SaveGame` 0x4B58A0 (F4 and the save panel both reach it), `save_flush` default OFF; `tools/saveinspect.py` added | `research/save-ghosts.md` §4 |
| #9 Player-count scaling | Built, NEEDS-LIVE. `enemy_health`/`enemy_damage` default 100 (off), `xp_rate`/`store_stock` default `auto` (health/stock 150/200/250, damage 115/125/135 at 2/3/4+ players). `store_prices` not built (needs RE) | `research/player-count-scaling.md` |
| #10 Turret ambush | Partial, live. The client refuses `m12ab` and parks (works); the unattended host tore down to the main menu after 66 s, so completion and convergence are unverified | `research/minigames.md` |
| §2 party-select departure | Still open, live attempt inconclusive. No PC ever crossed `ebn12_ebonexit`; the exit script `k_pebn_leavhawk` has no party-select call and needs `K_CURRENT_PLANET > 0`. A long `walkto` crashed the client engine in `CSWRoomSurfaceMesh::GetTriangleAdjacency`. Retry with a post-Endar-Spire save and short hops | `research/galaxy-map-and-party-select.md` |
| §2 fear/stun/poison/disguise/stealth | Built. Fear and general Stun forward by default; poison forwards once per application; stealth sets a flag that holds the stealthed stand-in out of enemy targeting (`stealth_sync` ON). `poison_sync` (apply half), `disguise_sync`, `stealth_visual_sync` default OFF, NEEDS-LIVE | `research/effect-sync.md` §12 |
| §2 door/chest key collisions (#47) | Closed statically: 64-bit key with the full module hash; 117 shipped modules, 239,616 keys all distinct | `research/container-sync.md` §12 |
| §2 address box (#46) | Built, NEEDS-LIVE: a permanent "Connect to an address..." row at the bottom of the browser list opens the engine text panel | `research/server-browser.md` |
| §2 reachability probe, firewall rule, rejected-join notice, host-away, long-session bounds | Built, NEEDS-LIVE. `ReachabilityCheck` (router WAN IP vs STUN address plus hairpin datagram) drives an honest status line; installer adds the Windows Firewall rule; host announces a rejected old-build peer; `host_away_timeout_ms` (20 s) treats a host parked at the menu as host loss; `ThreatTable` prune, SaveMismatch backoff capped at 5, agent log mid-session rotation, `DialogueGrantSender` cap 256 to 1024 | `research/network-topology.md` §17 |

Not addressed this wave: mines, duel-ring parking, client-cast crowd control on enemies, healing
or buffing the partner, per-object locals, monotonic re-locks, Pazaak cards on rejoin, Linux/Deck,
physical pad, mod matrix, and the Artifact-quota/private-repo download blocker (needs a user
decision).

### Live proof pass (2026-09-19)

One short rig session on current main (protocol 56): both instances connect and load; the class-grant, save-flush and scaling hooks install and byte-verify on both. **#7 save-time half closed**: with the joiner beside the host, `save` held 4 frames, destroyed 2 bodies, re-issued, and the file read 25 creatures / 58 waypoints with 0 ghosts (pass-through with no partner also clean); `save_flush` now defaults ON. **#1 partly proven**: host `addclass 3` -> joiner applied the same class (`[1,-1] -> [1,3]`) with no echo; `class_grant_sync` now defaults ON, feats and Force powers still open. **#9 health proven**: 15 -> 23 hp at two players on both instances; `enemy_health` now defaults `auto`, `enemy_damage` stays 100 until a hit is observed. Still unproven: STUNT follow, address box, effect kinds, companion equip, and the party-select and turret rig checks.

### Live proof pass 2 (2026-09-19)

Fear and Stunned proven both directions; the Disguise kind proven (receiver applied it, the sender's copy of that stand-in repainted in 470 ms; `disguise_sync` stays OFF); poison local smoke only. `enemy_damage` proven (a hostile's hit on the host 4 -> 5 at two players, host only) and now defaults `auto`; the first twelve rewrites are logged. The player-count flap is closed by a 10 s fall debounce (`PlayerCountSettleRule`). Regression: the galaxy-map departure is unaffected by `follow_stunt` (both worlds changed within 8 ms, the mid-leg duplicate de-duplicated). Full suite 3,921 / 3,921. Still unproven: the STUNT-follow trigger itself, stealth, the address box, companion equip, the party-select and turret checks, and the real Dantooine class-grant script path.

**#1 closed apart from the real script path (2026-09-19).** The feat and power grants listed as open are by design: `AddMultiClass` only opens the class slot at level 0, and each player picks feats and powers on their own level-up screen (class-and-power-grants.md, last section). What remains is playing Dorak's real Dantooine conversation with two players.

### Live proof pass 3 (2026-09-19)

Manaan: the joiner refuses the follow into `manm28ab` (live); the wait notice now says "is in a solo section". Stealth targeting proven through the flag path (host neutralises the stand-in on the byte, releases it on clear). Poison: captured once and not replayed with the key off; damage ticks not observed (inconclusive, `poison_sync` stays off). **Leviathan brig (#4) trigger built**: a `+0xF0` edge in `lev_m40aa` begins a solo segment; wiring live-verified with `tools/partyctl.ps1` (host began/ended, joiner agent spectated); the real `SwitchPlayerCharacter`, the rescuer's body on the joiner's screen and gear return remain. **Dark side (#11)**: no code; a faction flip cannot be produced on the rig, so it needs the temple script or a flipped companion first. Not proven: the suited Manaan walk (a `warp` skips the airlock scripts), STUNT-follow's own trigger, the address box, companion equip.

**Crash tooling (§2, closed 2026-09-19).** `KotorCoop.Agent.exe logs` zips the plugin and agent logs (with rotated copies), the crash record, a `coop.ini` with `password` and `host` blanked and a system note into one file on the Desktop; 17 unit tests, run against the rig's install. The dev-only `crashbin.ps1`/`crashlog.ps1` are no longer needed for a player report.

**Live verification remains, alongside missing implementation.** [PLAYTEST.md](PLAYTEST.md) lists the story paths, UI and hardware checks. Player-placed mines, partner healing, per-object locals, story re-locks, Pazaak card persistence and dark-side faction handling still require code or research; a playtest checklist does not close those gaps. Later entries below supersede earlier status claims.

### Live proof pass 4 (2026-09-19)

**Suited Manaan airlock walk (#Manaan): VERIFIED live** with `scriptgive man28_envirosuit` + `startdialog man28_pdroid man28_airlock` + `selectreply 0` (recipe in `research/manaan-hrakert-rift.md`): the script's own `StartNewModule` moved the host alone into `manm28ab`, the joiner refused ("single-seat minigame ... waiting for them"), and a `warp manm28aa` brought the host back with its party. **Found and fixed a player-facing crash on the way:** `DialogueReplyHook` relocated 5 bytes of the base `SelectReply`, cutting an instruction in half, so committing ANY reply on a computer terminal or computer-type conversation crashed the host; story conversations use the cinematic function and never hit it. An audit of all 37 inline detours found no other cut instruction (three cosmetic un-NOPed residues remain). Named saves (`save <name>` / `loadgame <name>`) are in, so a warp-generated later save can be reused.

Three RE agents reported (branches not yet merged at the time of writing): **mines** built behind `mine_sync` (OFF), party-placed mines not built; **enemy crowd control** (client casts on mirrored enemies) built behind `enemy_effect_sync` (OFF, protocol 56 -> 58); **partner heal/buff** premise mostly wrong (medpacs heal only their user, Heal/Cure/Recovery loop the roster with no faction test; only Valor uses `GetIsFriend`) - `partner_buffs` (OFF) moves the stand-in to Friendly_1 so Valor can reach it; healing the partner needs a mirror design (`research/partner-buffs.md` section 8). All three are NEEDS-LIVE.

**STUNT-follow trigger: VERIFIED live** (host `warp STUNT_50a`, joiner replays the leg on its own engine, echo de-dup fires, both land in `sta_m45ac`; `research/cinematics.md` 12.9). Still unproven: a multi-hop scripted chain and the other STUNT modules.

**Crowd control (#2): capture round trip VERIFIED for Stunned** (client cast -> client agent -> host agent -> host plugin applies it to the enemy body; `research/enemy-effects.md` 7.1-7.2). **Mines: VERIFIED both directions** (destroy, walk-on detonation each way, no false removal across four warps; `research/mines.md` 9) and `mine_sync` is now ON by default; party-placed mines are still not built. `enemy_effect_sync` stays OFF until a real Force-power cast has been seen to stop an enemy.


### Recovery and hardening pass (2026-09-21)

The starting checkout was `3509e13` on `main`, with only the untracked `scratchpad_pids.txt`.
Two older worktrees still contain unmerged work: `agent-a88027634bb33b578` (live evidence and
an uncommitted HUD correction) and `agent-aede2af70aa6bd907` (network recovery and an uncommitted
Bye-identity test). Their files and branches are preserved; reviewed changes are recovered into
this checkout without replacing newer main features or claiming that either entire branch merged.

- Floor-mine removal was already merged and enabled (`386c1af`), with two-way live evidence in
  mines.md §9. This pass adds apply-side rejection of party-placed mines and fair shared-slot
  rotation so mine removals and ordinary placeables both progress beyond slot capacity. These
  new safeguards are statically tested; they do not implement player-placed mine replication.
- The old mine off-switch test accidentally set `mine_sync = 1`; it now exercises `= 0`.
- Recovered network changes isolate caller-thread packet encoding, match returning players by
  nonce instead of reused endpoints, decouple reconnect resync by channel, and bound unreliable
  relay queues. Network simulation now exercises duplicate datagrams and deterministic loss.
- Expired companion-damage transport reports are pruned; cumulative ledger baselines remain.
- Full-history container-loot replay is **not integrated**. Review found two further barriers:
  the game bridge accepts only the newest 24 removals, and the wire carries container/item IDs
  without module identity or a module-change reset. Expanding replay could delete an item whose
  IDs were reused in another module. The older worktree's full-set digest change is therefore
  also excluded: it would advertise history the current replay path cannot recover. This needs
  module-scoped wire identity plus fair, acknowledged game-side delivery before the 512-entry
  retention gap can be closed.
- The store loopback fixture now captures its selected shop ID once per scan, avoiding a race
  where a test-driven shop switch returned a different shop's inventory. Reconnect test teardown
  now cancels and awaits both session tasks even on assertion failure.
- Recovered the 2026-09-17 three-player soak evidence: eight doors, four warps, about 30 minutes,
  all three processes alive and all six peer bodies dressed. This is historical evidence from
  commit `5aafa27`, not a new live run of today's build. The saved HUD change moves the connection
  line clear of the minimap at the measured 800x600 layout; other resolutions remain unverified.
- Corrected reachability claims: a local UDP hairpin test is not an outside connection test.
  The status now reports local success with internet access unconfirmed, or possible additional
  NAT. Address disagreement does not establish ISP CGNAT or rule out upstream port forwarding.
- Earlier commit `cbaa0bf` already added companion equipment suppression, companion-heal
  forwarding, XP join baselines and periodic character capture. The original §2 bullets were
  stale; the remaining companion equipment and save-cadence checks are live verification.

#### Current remaining work, in dependency order

| Order | Work | Completion criterion |
|---|---|---|
| 1 | Verify today's mine/slot and network changes in a two-player game, then repeat three-player door/rejoin soak | Real disarm/recover/detonation, busy module, reconnect and no unintended removal or missing state |
| 2 | Player-placed mines | Authoritative spawn serial mapped to each engine's dynamic object ID, single item consumption and single authoritative enemy damage; mines.md §7 |
| 3 | Real client crowd-control casts and partner buffs/heals | Confirm enemy behaviour changes, not just effect delivery; confirm real Valor targeting; design partner healing separately; keep unproven opt-ins off |
| 4 | Early story/UI checks | Address box, companion equip suppression, client-led party selection, turret completion, Dorak's actual class-grant dialogue |
| 5 | Duel-ring parking and dark-side faction handling | Trace real script transitions and implement safe ownership/parking/faction changes; observation alone cannot close missing channels |
| 6 | Later story completion | Leviathan rescue/gear return, real Manaan return trigger and full multi-hop ending chain, both players converged afterwards |
| 7 | Persistent world/character gaps | Per-object locals, story re-locks, Pazaak cards on adoption; module-scoped container-loot identity and delivery through the real 24-slot game buffer are prerequisites for safe long-session loot recovery |
| 8 | Broader compatibility | Difficulty agreement, census overflow, mod matrix, localized executables, Linux/Deck, physical pad, resolution/layout and multi-hour soak |
| 9 | Distribution | Recheck CI/release availability; private-repo visibility or deleting unrelated repositories' artifacts remains a separate user decision |

No end-to-end seamless full-playthrough claim is justified until the story and missing-channel
rows above close. [PLAYTEST.md](PLAYTEST.md) supplies live steps and explicitly distinguishes
implemented features from exploratory checks.

#### Validation for this pass

- Broad integration snapshot: **4,082 passed**, no failures (9 m 27 s),
  `tests/KotorCoop.Tests/TestResults/integration-final.trx`. This run preceded the final exclusion
  of the provisional loot-history extension and the final test-fixture cleanup.
- Final focused build with warnings as errors: **154 passed**, including mine selection,
  caller-thread sends, peer identity, resync rules, network simulation, relay bounds,
  reachability, companion pruning, Bye identity, baseline loot rules and the corrected store
  reconnect fixture (`final-focused.trx` beside the integration result).
- Explicit opt-in three-peer network soak: **passed**, 120 s churn plus convergence (2 m 2 s),
  `TestResults/network-soak-120s.trx`. Loss, jitter, duplication, outages and repeated rejoins
  use real loopback UDP sockets. This is not a game session or the full ten-minute soak.
- Current NativeAOT plugin: `src/KotorCoop.Plugin/build.ps1 -WarnAsError` passed;
  emitted DLL verified x86, 2,497,024 bytes. No installation or release publication performed.
- `git diff --check` passed. Both original worktrees retain their original uncommitted edits;
  the user's `scratchpad_pids.txt` is untouched. Changes in this checkout are uncommitted.

Two initial broad runs aborted unexpectedly; another integration attempt also exited its
host process. The diagnostic rerun completed successfully. The abort cause was not established,
so this pass does not claim to have fixed a test-host crash. One initial store assertion failure
led to the concrete fixture race/cleanup corrections above; final focused coverage passed.

### Continued implementation and live checks (2026-09-21)

This section supersedes the earlier recovery-pass exclusions where noted. Work is being
committed in reviewed groups; a full campaign remains unverified.

- Door state now carries current lock state, including a closed/locked zero value. The plugin
  applies each desired-state edge once so an old snapshot cannot undo a new local script change.
  Live in `sta_m45ac`, door `0x3E` opened on both instances; a host lock and client unlock each
  propagated to the other instance. Closing through a real story script remains to be checked.
- Pazaak collection and side-deck lists travel as private character metadata, are restored into
  the adopted world's party table, and are stripped from the engine-facing character IFO.
  File-level and TCP adoption regressions pass. A real card grant changed the joining player's
  saved collection; its full in-game adoption/reload check is still pending.
- Taris duel parking now observes the retail script's player min-one-HP edge, covering zero
  and one companion. Korriban's corresponding pin belongs to an NPC; its smaller-party case
  remains open rather than using the Taris assumption.
- Partner Cure/Heal mirroring is implemented behind `partner_buffs` (still off by default),
  using the existing heal event path and deduplicating the local roster's effects per cast.
  Pure rule checks pass; a real Force-power cast remains required before enabling it by default.
- Minigame locking releases when the host becomes away, even if its last in-world snapshot
  retained the minigame bit. Loopback coverage includes this game-over/menu path.
- The two-player mine regression removed mine `0x75` from both instances after host removal,
  detonated `0x74` from the client with a real short walk (HP 9 to -11), and retained untouched
  mine `0x76` across a `tar_m10aa` / `tar_m10ab` round trip. These checks do not implement
  player-placed mines or prove inventory recovery/disarm rewards.
- A release job now fails explicitly if its upstream run has no build packages. The repository
  visibility and account artifact quota are unchanged.
- Module-scoped container and materialised-loot recovery is now integrated (protocol 59),
  superseding the earlier exclusion. Full container history feeds an acknowledged 24-slot
  mailbox, with module and epoch guards; delayed materialised loot waits for its owner mapping
  instead of expiring after a few pump ticks. Engine-refused party-pool transfers now return a
  result, retry a bounded number of times, and retain the client's local item with a diagnostic.
  This addresses the live refusal of a Manaan-only item blueprint after travel to Star Forge.
- Current focused integration snapshot: **192 passed**, warnings-as-errors build,
  `tests/KotorCoop.Tests/TestResults/commit-focused.trx`. Additional inventory and control
  corrections made after that snapshot need their own final verification.

**Live regression reported by the user:** the joining player's portrait showed Trask and input
could not move the player. `resolve party` at 21:59:56 showed a valid server PC (`0x7FFFFFFE`,
9 HP) absent from all three client party slots: Trask occupied slot 0 and slots 1/2 were stale.
The earlier full-capacity search cannot recover an entry that is absent. A verified ID-only
repair plus the updated agent restored the portrait and movement, confirmed by the user.
Automatic plugin recovery and a guard against clamping out the PC are committed in `217dab2`;
the exact missing-entry fallback still needs an automatic reproduction. Erratic companion
following remains under investigation.

**Recurring frame stalls:** the user reported freezes approximately every second. A read-only
frame-counter sample confirmed 220–400 ms gaps, remaining around 220 ms after orphan test hosts
were stopped. Client agent telemetry stayed around 30 Hz with ordinary update costs below 1 ms.
Making gait diagnostics opt-in reduced the restarted client's observed gaps to about 140–160 ms,
but did not eliminate them. This is a partial improvement, not a closed performance defect;
per-controller frame timing is being added to locate the remaining cost.

Reviewed work is saved in local commits `d0253f6`, `e55f87f`, `f6e97fe`, `934c18e`, `d7d12ea`,
`97be0e7`, `74c131f`, `217dab2`, and `2acd4cd`. Nothing has been pushed or released.
Further validation: **67 persistence tests**, **119 control/inventory/mine tests**, and
**189 integrated channel/gait tests** passed in separate focused snapshots. The network snapshot
passed 185 tests with one source-location fixture failure when binaries were placed outside the
repository; the fixture now locates its source via `CallerFilePath`, and its mine suite passed
in the later integrated run. The current native plugin builds as x86 with warnings as errors.
