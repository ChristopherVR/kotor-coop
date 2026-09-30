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


## Native88 ordinary travel and browser follow-ups

Native88/wire80 initially exposed a real ordinary-travel regression on private games4/5:
after END_M01AA to tar_m10aa, the joiner's actual native damage ledger recorded37 against
GIT enemy0x38, but the host accepted none. The publisher could combine a retained PC module
with a fresh native epoch, incorrectly quarantining the new world or binding its generation
to an older digest. An unreadable epoch0 could also look like a same-area load.

Commit2c579f3 samples coherent module/load/epoch around the fingerprint and retains the
last resolved local namespace through unavailable samples. Genuine same-area reloads still
quarantine old local reports. Native88 plus Agent81 from that commit passed the same live
ordinary warp with local_combat=1: peer2 dealt20, host accepted20, native HP1021 to1001,
and five application calls. After a quiet interval and actual disconnect/reconnect, the
accepted total, HP and calls were unchanged. A fresh attack on the resolved GIT creature
added13, reduced HP1001 to988 and advanced calls by4; a later quiet sample remained stable.
Evidence: `artifacts/default-combat-v88/coherent-proof.json` and its six native snapshots.
This checks a stock GIT enemy, not every materialised scripted encounter or threat policy.

The actual empty-list browser test after quitting to the menu exposed an independent
native exception. The preserved native88 PDB mapped RVA0xE69F1 to WithResRef+0x31:
ShowDetails(-1) left portrait names null, and the resource-reference encoder dereferenced
one. Commit281c4c1 supplies empty portrait names, a null-safe16-byte encoder and bounded
failure cleanup to prevent repeated unregistered panel allocations.

Native88b SHA256 `EC506615C4E47511AAC0F2FB5E0407C916A0AED09260905060621557D0F06CE6`
and coherent Agent81 then passed live on fresh original stock copies33944/58408.
The real browser/address modal rendered after quitting the loaded world; typing
`localhost:27140` and pressing its OK button published the connect request and actually
returned the joiner to the host's Endar world, with one native remote and phantom each.
The earlier IPv6 localhost choice is fixed by preferring IPv4. Captures:
`artifacts/stock-live-v88b/address-typed.png` and `address-connected-world.png`.
The generated address sidecar was archived and its previous absence restored.

Full fast validation of committed88b sources:5343 passed, zero failed. Two stale packet-size
assertions were corrected for the added flag namespace; the environment-dependent LAN
broadcast smoke check was removed as requested. Deterministic discovery tests remain.

## Fresh eight-player stop, quiet and return checks

With native87 CancelCombat fix and Agent87b, all seven joiners each landed a new accepted hit through the bounded real-attack runner. The measured accepted delta9 equalled native HP loss9 (1000 to991), and application calls advanced7. After confirmed active-round cancellation,14 samples over10.50 seconds found all counters and HP unchanged. A real peer6 disconnect/reconnect rebuilt its host phantom303 to316 and its local enemy mirror138 to13D. Quiet totals and native health stayed unchanged; a fresh real hit on the resolved13D increased only peer6 by1, reduced host HP991 to990, and advanced application calls by1. The immediate-hit sample establishes this equality. A much later snapshot was deliberately excluded: the staged ten-minute temporary-HP effect had expired by then, invalidating a long-delay health comparison. Evidence: artifacts/eight-combat-v87b/executor/hits-proof.json,result.json, reconnect-quiet-proof.json, reconnected-native-mirror.json and fresh-hit-immediate-proof.json.

Afterwards the host entered MANM28AB alone and returned to END_M01AA with a genuinely changed origin map from the spawned enemy. All seven parked joiners requested their own dedicated Co-op saves, confirmed exact fresh captures, and entered SoloReturn adoption. The host initially was not save-ready, then its fresh return save landed after1210ms; the joiners' first download attempts safely refused and automatically retried20seconds later. All seven actually rejoined the updated Endar world, keeping their own Scoundrel class. Twelve existing QuickSave files(game7/8) retained hashes and UTC modification times; game1..6 had no QuickSave files before or after. This closes the previously unit-only client return-capture case on this staged path. Evidence: solo-return-proof.json and return-quicksave-before/after.json in the same owned run. The rig was stopped through its recorded PID/start/path ownership.


## October 1 continuation: verification and request identity

The frozen native88b source snapshot passed all5343 fast tests and all116 Slow tests
(the Slow run took9m18s). A later live battery found that a newly adopted joiner could
restart its flag sequence at1, while the host deduplicated only by peer and sequence.
Commit f7f2286 includes the host session, load, flag revision and immutable operation
identity in duplicate detection; exact packet retransmissions remain idempotent.

That battery also exposed revive request serial reuse. The managed publisher now reads
both native requested/applied serials before allocating a fresh one, and commit15421d9
publishes the serial only after its HP payload write succeeds. Focused continuity tests
include a real split-page write refusal. The live verifier places its receiving player
beyond automatic proximity rescue range so a manual request is measured separately.

Combat-runner cancellation now validates the current player independently of a former
enemy that has died or disappeared. Attack admission still requires the original living,
resolved target in the unchanged native world. This permits safe cancellation after a
fight without loosening attack guards.

The first battery's short-lived Sith fixture invalidated its later enemy comparisons.
Its native base HP was2; one physical host hit removed both its base and temporary HP.
This is not established as a co-op defect or an effect-expiry event. Repeat verification
uses a clearly staged stock Sith UTC clone with a unique tag, blank script callbacks,
class level20, Constitution30 and500 stored base/max HP. The engine gives this fixture
700 current HP without a temporary-HP effect. Earlier500-HP variants also died abruptly;
their script callbacks are not established as the cause. Evidence and failures are
retained separately from successful runs.

## Three-player target and down follow-up

The atomic81 three-player Taris run staged genuine temporary HP on the current GIT enemy
in all three games, retained native commandability, and used real stock blaster weapons.
Actual native targets switched between the two mapped living peer phantoms. This does not
isolate whether the plugin's retarget command or ordinary enemy AI caused each switch.
A later attempt observed actual LocalDowned1 and host peer flag8, with the victim separated
from proximity rescue. The post-down window was only about3.3seconds, shorter than the
four-second retarget cooldown, so it does not establish living-target fallback failure or
success. A later sample initially suspected of targeting a downed peer was after revival;
it is expressly excluded from a bug claim.

Independent source review found that threat eligibility used only native down/HP checks
on stand-ins, whose native HP is deliberately pinned positive. Commit59846f4 also checks
current remote health/down flags, activity and away/minigame state before selecting and
ordering a target. Phantom targetability and down pose honor the same explicit down bit.
A regression selects the remaining contributor when the leading peer has1HP plus flag8,
then covers zeroHP, inactive, away, minigame and genuine revival. All61 focused threat and
phantom-target tests passed, and Plugin Release built with warnings treated as errors.
The longer live fallback rerun remains outstanding. A subsequent native89c run kept the
victim genuinely down for about51seconds, with the host observing flag8. The other
contributor's threat had decayed below the selection threshold while moving outside
rescue range; no eligible living fallback remained. The enemy cleared its target.
This neither proves nor disproves retargeting to a qualifying living contributor.

The frozen inactive-companion snapshot2ceeb10 (native89/wire82) passed all5366 fast tests
and all116 Slow tests (9m16s), with clean warning-as-error native and managed publishes.
Its exact recruitment-template hook and occupant generation fencing are implementation
coverage; live BoS replacement verification is tracked separately. Active replacements
are deliberately held in this snapshot, including after a load; save-transfer identity
binding is the next implementation step and is not credited to89/82.

## Native89d repeated live battery

Frozen snapshot ff90207 passed all5374 fast tests. Native89d and its matching Agent82
then completed two full verification batteries in the same original stock game instances,
without a reload or restart between runs: each49PASS,0FAIL and1opt-in transition SKIP.
Evidence: `artifacts/live-verify-v89-relockfix-run1.txt` and `run2.txt`.

The actual loot GUI needed its real Close button; the development close primitive's ACK
did not establish that the GUI had closed. The verifier now clicks that observed button
and measures both native container states. A genuine delayed-snapshot race then overwrote
an immediate local relock before its collector could publish it. Commit ff90207 defers
that conflicting snapshot until the local lock edge has been published. Actual close,
immediate relock, unlock and reopen passed in both batteries, alongside repeated item
pickup/removal, damage, down and revive checks.

After both batteries, an actual host dedicated save/load moved both native epochs from1
to2. The same saved enemy retained680HP with no temporary HP after ten settled seconds,
so earlier damage did not replay. A fresh real client attack then reduced host HP680 to677.
Both Quick Save trees retained their SHA256 fingerprints. The first checkpoint probe
incorrectly expected a new directory rather than an overwritten dedicated slot; its
timeout is preserved, and the corrected probe checks the actual SAVEGAME timestamp.
Evidence: `artifacts/stock-live-v89-relockfix/checkpoint-debt-evidence.txt`.

The actual1024x768 framebuffer also passed a separate visible check. The joiner stayed at
15.4248,20.1215,-1.275 while the host moved to35,22.2,-1.275. Its M map revealed the new
corridor, with distinct host/joiner pins and a readable connection line. Both original
game configurations were restored byte-for-byte with their original timestamps after
the owned pair stopped. Images: `artifacts/stock-map-1024/map-client-before.png` and
`map-client-after.png`.
