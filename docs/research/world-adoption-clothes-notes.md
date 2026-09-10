# Clothing bug — scratch handoff notes (2026-09-10, round 2)

Status: NOT FIXED. Root cause for the clothes-specific gap is still unidentified.
This file exists so the next session doesn't have to re-derive the state from
`docs/research/world-adoption.md`'s "Round 2026-09-10b" section. Read that section
first — this is just the short version plus concrete next steps.

## What is actually true right now

- The **weapon** fix (`b42c1c8`, merged `32a5be4`) is real, committed, and confirmed live
  across six clean joins in the prior round.
- The **clothes** item has never been found by `EquipUnequippedStartingGear` in any of those
  six runs, regardless of retry window (0-15000ms tried). Widening the wait time does not
  help clothes specifically.
- This round added diagnostic-only instrumentation (`DiagnoseCandidates`/`DiagnosePollSummary`
  in `Odyssey.Equipment.cs`) to answer: does the clothes item's id ever even appear among the
  three candidate sources? **Still unanswered** — the one live capture this round hit a
  DIFFERENT, upstream bug instead (see below) that invalidated the read.

## The separate bug this round's live capture actually caught

`NewCharacterCaptureController.cs:271` calls
`Odyssey.EquipUnequippedStartingGear(Odyssey.GetPlayerObjectId())`. In one joiner-chargen
capture, `GetPlayerObjectId()` returned `0x7FFFFFFF` (the engine's own "no real player
registered yet" placeholder — same value `Odyssey.ControlSwitch.cs` has logged before) for the
ENTIRE repair window, not the joiner's real creature id. `ResolveServerObject` still resolved
it to *a* valid object, so the repair ran to completion against the wrong object the whole
time — its only candidate was an adrenaline stim (`g_i_adrnaline003`, possessor=0x7FFFFFFF),
neither weapon nor clothes ever a candidate. Evidence: `kotorcoop-plugin.log` on
`D:\Games\swkotor-gog-p2`, 16:23:10-16:23:16 2026-09-10, `newchar-diag:` lines.

This does NOT explain the weapon-vs-clothes asymmetry from the prior round (if
`creatureObjectId` were wrong there too, the weapon couldn't have been found either) — it looks
like an intermittent, separate race layered on top. Real bug in its own right: when it fires,
the WHOLE repair silently no-ops, so a joiner could end up with neither weapon nor clothes.

## Also landed this round (real fix, unverified live)

`NewCharacterCaptureController.cs`: replaced the wall-clock `EquipRepairExtraMs` retry
deadline with a REAL-POLL-COUNTED budget (`EquipRepairMaxAttempts`, `EquipRepairMaxWallClockMs
= 90000` safety ceiling). Reasoning (see the doc comment in the diff): a wall-clock deadline
armed once, relative to whatever poll is running when `WorldSettleMs` first expires, gets
silently consumed whole by a single main-thread stall straddling that moment (measured this
round: a 122-SECOND stall from the Endar Spire opening cinematic). A poll-count budget survives
stalls — they delay when polls land but can't consume them. Widened again later this round,
100 → 400, after further live observation that 100 wasn't consistently enough (see
`docs/research/world-adoption.md`'s "Round 2026-09-10c" — same commit that fixed the
`PersistentSaveMismatchRule` chargen-race crash, `37d7c18`). This is a plausible general fix for
"the repair only gets one real attempt," but it has **not been live-verified this round** (the
only live capture hit the 0x7FFFFFFF bug above instead, before this logic could be meaningfully
exercised) and does not by itself explain or fix the clothes-specific gap.

## What is NOT done

1. The core clothes question — does its item id ever show up in the three candidate sources —
   is still open. Need a live capture where `GetPlayerObjectId()` is confirmed correct
   (cross-check against `SyncServerPlayerToLeader`'s `registered` value or the `items` verb's
   creature id) at call time, so the `newchar-diag:` lines mean something.
2. The 0x7FFFFFFF placeholder bug itself is undiagnosed beyond "it happened once" — don't know
   how often it fires, or what should re-point the server player slot for a first-time joiner
   going through `join_mode = newcharacter` chargen (a genuine "New Game" flow presumably does
   this somewhere this project's join path doesn't reproduce).
3. No fix has been attempted for either of the above. The poll-count change is a plausible
   improvement to the retry mechanism, not a fix for either identified gap.

## Concrete next steps (in order)

1. Add a one-line log at `NewCharacterCaptureController.cs:271` printing
   `Odyssey.GetPlayerObjectId()`'s raw value next to whatever `SyncServerPlayerToLeader` last
   logged as `registered`, so a future live pass can tell in one glance whether this run hit
   the 0x7FFFFFFF bug before trusting any `newchar-diag:` output from that same run.
2. Re-run the same live joiner-chargen capture (two-GOG-copy rig, `-ClientNewCharacter`,
   `chargen newgame`/`quick 2`/`accept` blockpoke verbs) once the shared rig is free. Check the
   new log line first; if `GetPlayerObjectId()` is correct this time, read the
   `newchar-diag:` lines for a clean answer to the core question.
3. If clothes never appears as a candidate even with a correct creature id: widen
   `StartingGearIdWindow` and/or add a wider fallback scan (e.g. scan by Tag/BaseItem type
   across a broader id range, or search a module-wide object list rather than only the two
   known repositories) — the search-coverage-gap branch of the hypothesis in the removed
   `EquipRepairExtraMs` doc comment.
4. If clothes DOES appear as a candidate but doesn't get classified/equipped: check
   `StartingGearSlotRule.Classify` against the actual Tag the diagnostic logs for it — may be a
   simple tag-pattern miss.

## Rig status at time of writing

Another agent stream ("npc-position-facing-fix") is actively using the shared rig
(real `swkotor`/`KotorCoop.Agent` processes running). Per the shared-rig-lock rule, do not
interfere. This round's diagnostic code and the poll-count fix are committed uncommitted-to-
live-verified; next session needs rig time to make progress on step 2 above.
