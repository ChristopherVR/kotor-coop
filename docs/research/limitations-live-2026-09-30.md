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
