# Live limitation checks — 2026-09-30

Continuation of [story-skip-mods.md](story-skip-mods.md). The initial fixes were
committed as `a12b404` before this live work. Game assets, configuration and saves
were backed up under `D:\kotor-live-backup-20260930\game1`, `game2` and `game3`.
The isolated rig uses published agents so source builds cannot replace running DLLs.

## Stalled requests and the new-game hijack

The first combined test reproduced a remaining race on shared-block version 82:
the joiner resumed inside a frame and executed its queued `end_m01ab` crossing
before the next main-loop freshness check. Its fresh announcement reached the
host during the opening movie. The host's delayed follow request was refused,
but the joiner's stale crossing should never have executed.

Version 83 checks freshness immediately before travel execution too, including
previously observed pending requests. In the combined retest, the joiner was
suspended for 55 seconds with an automatic crossing queued while the host
started a new game. The crossing was acknowledged with status **9**, without
travelling. After the opening movie both games remained in `END_M01AA`.
An automatic load queued during a separate 50-second suspension also returned
status **9**. A typed `warp end_m01ab` queued during a 50-second suspension
executed with status **0** and reached that module.

An isolated announcement-expiry test injected destination metadata while the
host was in Trask's conversation. After 66,657 ms, closing the conversation
produced `dropping a client's announce ... no longer current`; the host stayed
in `END_M01AA`. This proves the held-announcement path, not a physical crossing.

Typed `loadgame` exposed an additional pre-request problem: discovering the
plugin required a changing heartbeat, so it failed during the freeze before
publishing the manual request. Explicit loads now use the already attached,
validated block and cached save path. Unknown discovery still requires a live
heartbeat; an invalid version, guard or address refuses before publishing.
Ten focused loader tests passed. In the rebuilt live retest, request 2 was
visibly pending (`applied 1`) during the suspension, then applied with status
**0** after 50 seconds and loaded the requested Endar Spire baseline.

## Dedicated saves and compatibility audit

The native `save Co-op` probe created `000061 - Co-op`, including a 594,034-byte
`SAVEGAME.sav`, party table, globals and save metadata. The Quick Save SHA-256
was unchanged. Automatic JFTS refresh uses this same save request rather than
a delayed Quick Save that could run after character restoration.

The expanded audit read **15,336 files** in each stock install and reported
**zero differences**. The warmed comparison took 9.7 seconds. It covers the KEY,
every referenced BIF, module and Override content, and nested movie/audio/lip
files. Same-size, same-timestamp archive edits are covered by regression tests.
Archive/media differences conservatively disable gameplay sharing, including
cosmetic edits inside base archives; matching files still do not certify scripts.

## Verifier findings

Earlier 45/44-pass reports contained non-terminating XP conversion errors and
must not be treated as clean runs. Failed writes now terminate the battery.
The rebuilt pair confirmed actual XP `0 -> 5`, a host write of `-20`, the
joiner's retained XP of `5`, and restoration of the host to `5`.

A save-mismatch adoption during a battery changed the client's real player ID
to `0x7FFFFFFE`. The verifier had hardcoded `0x7FFFFFFF` and cached old phantom
addresses. It now resolves the engine's current player and object array.
Door/container collectors also reject intact freed objects with reused IDs.

Native door close/open and container close/re-lock passed. Door unlock/re-lock
passed on the settled rebuilt pair, but failed during the battery's staging;
the full repeatability check remains pending. A later run also began before the
client connected, exposing the rig's fixed eight-second startup assumption.
Readiness must be confirmed before counting feature results.

The expanded fast suite passed **5,159 / 5,159** tests after correcting two
historical layout-size assertions. Live evidence remains separate from this result.

The v84 follow-up fast suite passed **5,163 / 5,163** tests, including the door
apply acknowledgement and same-process item reconnect regression. A fresh live
battery caught additional staging errors: legacy shared-block position and module
header fields are not published. The verifier must use the native current player
and server module instead. These aborted runs are not counted as feature passes.


## Eight-player stock rig

Eight actual game processes each reached seven native current peer bodies. Four
module changes between end_m01aa and end_m01ab included travel led by the host,
the second instance and the eighth instance. Native where replies from all eight
confirmed each destination; readiness independently resolved every phantom body
through the current engine object array. Evidence is under artifacts/live-eight-v84b.
This is selected stock travel coverage, not an eight-player campaign or combat proof.


The final v85 fast suite passed **5,186 / 5,186** tests, and the x86 native AOT
build passed with warnings treated as errors. Shared version85 adds a separate
placeable apply acknowledgement bank; version77 remains the wire protocol.
Durable item journals now preserve host-scoped item and operation counters plus
unacknowledged operations across process restart. A deterministic loopback test
also covers removal after an empty stale snapshot and switching host namespaces.
Live repeatability remains under test; these unit/build results do not certify it.

## Follow-up checks

The subsequent fast suite passed **5,198 / 5,198**, including actual door completion
rules. The separate slow suite passed **114 / 114** in 9 minutes 16 seconds.
These results precede the later damage-incarnation and input-lock changes.

A fresh v85 stock battery passed 48 checks, failed one client-attack fixture
mapping check and skipped one inapplicable case. It confirmed actual native door
close/open/relock, container close/relock and item pickup. The next run confirmed
the corrected client attack reduced the host enemy pool from 988 to 985, but
caught a genuine repeated container-close issue: an older canonical open tuple
could reopen it before the one-second native scanner observed the local close.
The battery is not yet certified repeatable.

The private stock turret pair won by actual synthetic A/D/W/S aiming and repeated
Space presses. All six native fighters moved from 100 hit points to -20 and
reported dead; no hit-point, aim-field or kill writes were used. Attempt four
finished shooting at 24,998 ms with host health 2,610/3,000. With no explicit warp
after the win, the stock script and movie returned the host to `ebo_m12aa`.
Read-only current-player and native server-module checks confirmed both players
there, and real W presses moved both afterwards. Evidence lives under
`artifacts/turret-live-v85` (`shoot4-proof.json`, `fighter-census4.json`,
`scripted-return4.json`, `host-positive4.json`).

The parked player's keyboard guard failed that same test: with native
`MinigameLockActive=1` before and after a two-second W press, its real player
moved 0.4254 units. After release it moved 1.519 units. The commandable flag and
periodic action clearing alone do not block keyboard input. A DirectInput guard
is being added and requires a rebuilt live retest.

The JFTS installer reported success while leaving a 16,226-byte stock
`m01aa_c01.dlg` in the effective module archive. Its shipped replacement is
52,815 bytes. The private three-player installation was repaired using that
replacement with the six installed TLK references verified, and the effective
archive was read back on all three copies. A fresh host process then displayed
the real Guardian/Sentinel/Consular selector. This is an installer limitation
observed with the local archive and HoloPatcher version, not evidence that every
installation of the mod behaves this way.

The combined shared86/wire78 build passed native x86 AOT and agent publication
with warnings treated as errors; the fast suite passed **5,233 / 5,233**.
This build adds a Hello-bound native process incarnation with ordered damage
generations, persistent item-removal tombstones, native local container edges,
a keyboard/mouse gameplay lock and a reader for the private Jedi popup.
The popup uses invalid GUI participant IDs while its own player still owns a
valid dialog; its old watcher timed out while the prompt was visibly open and
never applied the player's selection. Rebuilt live checks are underway.


### Latest combined slow suite and private-prompt failure

The ABI86/wire78 build passed all 116 slow tests in 9 minutes 18 seconds. The earlier 114 count belongs to a previous build. The three-player JFTS live run revealed that the dialogue follower's empty-shell cleanup still tears down private class prompts: peer2 logged StartDialog at 20:50:01 and shell cleanup at 20:50:04, followed by a closed-without-pick result. The narrow private-dialogue reader observes the prompt, but publishing suppression alone does not protect it from following logic. Class preservation and automatic refresh remain unproven until this fault is fixed and the real choices complete.

### ABI86 real turret win and movement lock

The clean sixth controller attempt killed all six original native fighters by real keyboard aiming and repeated fire presses in 11,513 ms. The host retained 2700/3000 HP. No health or native aim writes, forced kills, or explicit return warp were used. The stock script/movie returned both games to ebo_m12aa. A two-second joiner W hold at lock=1 left the same native character and position unchanged; after lock=0 the same input moved 1.04041 units. The host also moved 2.120 units afterwards. Evidence is artifacts/turret-live-v86/shoot6-proof.json and host-positive6.json. These checks cover the stock fight and parked keyboard path, not a full story-skip campaign or swoop/Star Forge duel locks.

### Three-player JFTS automatic refresh, preserved classes and inventory

Fresh rig artifacts/jfts-live/live-three-v86b used the rebuilt private-prompt protection and managed v86b. The opening 01A movie held frame callbacks for 121 seconds; it then recovered normally. After real GUI choices, native stats read host [2@1,3@1], peer2 [2@1,4@0], peer3 [2@1,5@0]. Both joiners requested own-character saves at 21:02:07 and rejoined automatically at 21:02:08/09 for the post-JFTS revision. After reloading those same different classes remained. Native census files v86b-host-after.json and v86b-peer2/peer3-after.json show jfts_bench, jfts_trask_corpse and jfts_trask_remains on all three, with identical eight-item end_locker01 and seven-item FootLker contents. Their class choices stayed at the stock grant level zero until their own level-up.

Personal fixture items were created before the class choice: peer2 received two g_i_medeqpmnt02, peer3 three g_i_secspike01. Automatic adoption logs captured one personal repository item each. After refresh, adding the same amounts again yielded own pool stacks four and six respectively (native items100 at 21:04:05/06), proving the first stacks survived without copying the other player's item. More than a minute after completion neither joiner repeated the script-world refresh. Host first requested a save during the immediate post-choice GUI boundary, refused safe capture, then confirmed a stable dedicated save after level-up finished at 21:01:27. Manual connect commands were issued while prompts were already up, so this run establishes automatic post-choice refresh and per-character preservation, not entirely hands-off recovery during the preceding long menu.

Explicit save Co-op then reconnect world exposed a false refusal: the periodic sidecar capture at 21:03:15 was newer than the same save written at 21:03:14. The manual path treated 'already captured' as a failed capture and safely left the session connected. Manual success remains open until that path is corrected and tested.

### Repeatable stock verification after independent container-lock fix

Native86, wire78, managed86c completed two full verify runs on the same unchanged running pair: each 49 PASS, zero FAIL, one SKIP (the opt-in destructive transition). Evidence: artifacts/live-verify-v86c-run1.txt and run2.txt. Both runs covered native doors and containers opening, closing, unlocking and relocking, real phantom damage, actual enemy native HP loss, XP guard, unique item pickup/removal and advancing revive serials. The earlier dirty-start door failures disappeared when baseline900 loaded before agents connected. The container failures required real source fixes: captured close intent protects only its open edge, while current lock/trap changes still publish independently and do not get swallowed into shadow state. A natural campaign story relock remains a broader route check.

### Three-player real damage attribution and quiet reconnect

The JFTS three-instance rig also exercised two real joiners attacking one host-owned native Sith fixture, with host DamageDealtEnabled zero. At the final quiet baseline, peer2 accepted cumulative was 41, peer3 was 36; native enemy base2 plus temporary922 was exactly 77 below base2 plus temporary999. Global AppliedEffects was 69 because some calls carried bundled debt; it is not an HP counter. After real peer3 disconnect/reconnect, its host phantom changed349 to351, while both accepted totals, native health and application count remained unchanged for seven seconds. Personal classes stayed host3@1, peer2class4@0, peer3class5@0. Evidence: artifacts/combat3-A-real.json, B-real.json, quiet2-before/after.json and quiet-reconnect-after.json.

This establishes independent real senders and no quiet reconnect replay. The fresh post-reconnect attack was safely refused because the old local enemy no longer resolved; its new mirror did not rematerialise during the observed window. Threat switching and downed-target fallback remain open: the unarmed level-zero joiners did not meet the15-threat threshold and the paralyzed fixture was not commandable. Do not count diagnostic threat changes as native target proof.

### Automatic solo-return Quick Save overwrite found

A read-only audit following the three-agent restart found two automatic return paths still calling CheatKinds.QuickSave without the held redirect used by periodic/disconnect capture. BeginSoloReturnCapture and BeginHostReturnFreshSave can overwrite the player's Quick Save, contrary to the player-facing guarantee. Their newest-save completion lookup can also mistake an unrelated newer save for the requested result. The scoped repair will use a dedicated Co-op slot and exact-source completion checks. Explicit player save/Quick Save commands retain their intended behavior. The earlier isolated dedicated Co-op save preserved Quick Save; that result does not establish protection in these separate return paths.

Configuration qualification: all three JFTS installs had local_combat=1. The three-player exact accepted-damage/HP result is real mixed-ownership combat evidence, not the forwarding-only local_combat=0 recipe in peer-combat-probe.md. The planned eight-player controlled probe disables joiner local combat and resolves every owner/local mapping afresh. The missing enemy mirror after peer3 reconnect remains a separate observed gap in mixed-ownership mode.

## ABI87 manual refresh recovery

Commit6eff86c plus e58b038 was built as native87/wire79 with warnings treated as errors; 5306 fast tests passed. The three private JFTS games restarted from their dedicated Co-op saves. After automatic startup recovery settled, host and both joiners saved Co-op again. Both joiner background captures completed before the manual command. At21:59:16 both accepted reconnect world, re-extracted their exact fresh archive, and rejoined END_M01AA at21:59:20/22. Consular class4@0 and Sentinel class5@0 remained distinct; their own medeqpmnt02 stack4 and secspike01 stack6 survived the actual reload. All17 QuickSave files across the three installs retained both SHA256 and modification ticks. Evidence: artifacts/jfts-live/live-three-v87/agent2.log,agent3.log, peer2-after.json,peer3-after.json and quicksave-before/after.json. An earlier command during startup adoption was safely refused because the joiners were already loading, and was retried after connection settled.


The exact-comparison restoration helper was then exercised on all three real private mod installs: 259 selected stock files verified per install, with 46/47/47 added test files removed. Saves were explicitly restored too. The current native co-op binary was retained. This provides a reproducible recovery path where HoloPatcher uninstall previously failed; it does not change HoloPatcher itself.


## Eight real combat contributors

Eight private stock games on native87/wire79 each resolved seven peer creatures. Host local_combat=1, all seven joiners local_combat=0, damage_creator_attribution=1, and host DamageDealtEnabled=0. One actual spawned Sith soldier was staged with temporary HP and paralysis before the baseline. All seven distinct joiners then landed real engine attack rolls on verified local mirrors. Accepted totals for peers2..8 were7,6,20,7,13,18,46. Their sum117 exactly matched native base-plus-temporary health falling1001 to884; global application calls advanced112 (bundled damage is possible). The offline checker passed. Evidence: artifacts/eight-combat-v87/settled-before.json, all-seven-after.json and all-seven-proof.json. Some contributors overlapped; this proves independent contributors and exact aggregate damage, not ordered windows or a complete eight-player campaign.

The attempted quiet phase exposed a dev attack-stop bug: its queue clear left the engine's active combat round intact and some players continued landing real attacks. No quiet/reconnect claim is made from this attempt. The native cancel-combat fix and a fresh bounded rerun are in progress.


Native87 automatic host return saves also completed after three actual Ebon Hawk/Manaan solo cycles, with confirmed dedicated-save ACKs and exact-slot archive stability after 1623/1310/1656 ms. All six QuickSave files on each of the two private installs kept hashes, lengths and UTC modification times. The joining player's dedicated automatic solo capture did not trigger because its adopted origin map stayed identical; this remains a live coverage gap, despite passing integration coverage. Evidence: artifacts/return-save-proof-v87/result.json and associated ACK/return/hash files.

