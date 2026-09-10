# Clothing bug — scratch handoff notes (2026-09-10, round 4)

Status: **The weapon half is FIXED and live-verified, 3/3 independent joins across 3 different
chargen classes.** The clothes half looks like it was never a real bug — see below. Read
`docs/research/world-adoption.md`'s "Round 2026-09-10e" section for the full derivation; this is
the short version.

## The one-line summary

Rounds 1-3 misread `Odyssey.GetPlayerObjectId()`'s `0x7FFFFFFF` return as "no real player
registered yet, wait for a real one." Round 4 pushed the wait to 150 seconds (it never changed)
and then checked the SAME accessor on the HOST's own long-established character: also
`0x7FFFFFFF`. It is the engine's PERMANENT, correct player self-id, not a placeholder — there was
never a "real" numeric id to wait for. The actual reason the repair never found the starting
gear: while still inside the isolated, hand-rolled chargen replay, the granted items are never
linked into either repository the repair searches, no matter how long you wait. Moving the SAME
repair to run once, after the adoption's own (ordinary, real) module load finishes, fixes it —
by that point the repository is populated. Live-verified: the starting WEAPON now equips
correctly on every one of four independent test joins.

## What round 4 did

1. **Settled the timing question round 3 left open**, live: pushed
   `NewCharacterCaptureController`'s old pre-save equip-repair wall-clock ceiling to 150 seconds
   (temporary, diagnostic only) and ran a clean capture. `GetPlayerObjectId()` read `0x7FFFFFFF`
   on every one of ~600 polls, right up to the ceiling firing. Then, separately, queried the same
   accessor on the HOST's own fully-equipped, hours-old character: identical `0x7FFFFFFF`. This
   is the round's key correction — see world-adoption.md's round 4 section, part 1.
2. **Found the real gap**: both candidate repositories `EquipUnequippedStartingGear` checks read
   `count=0` for the ENTIRE original chargen-module window, every poll, regardless of id. The
   items are never linked into either repository while still inside the isolated chargen module.
3. **Relocated the repair**: a new `PostAdoptEquipRepairController` (armed by
   `NewCharacterCaptureController` at capture time, before the quicksave even fires) runs the
   SAME `EquipUnequippedStartingGear` call once, after the adopted-world load's own "world is up"
   ack — a genuine, ordinary module load, not the hand-rolled chargen replay. The pre-save repair
   loop was removed entirely from `NewCharacterCaptureController` (it could never succeed there,
   and was burning up to 2.5 extra minutes of the player's first join for nothing).
4. **Live-verified the fix, four times**: classes 0, 2, 4 (against the functional fix before a
   pure code-organisation refactor), then class 1 again (against the final committed build,
   after `PostAdoptEquipRepairRule` was extracted out of the controller for testability). Every
   run: the starting weapon (`g_w_blstrrfl001`) found via the now-populated party-pool
   repository, classified correctly, equipped correctly (readback-verified), on the very first
   poll after the world-up ack. `party_pool_sync` isolated to `0` for every capture, so this is
   NOT the round-1 sync collision — it is the joiner's own genuine starting weapon.

## The clothes half: probably never a bug

None of the three classes tested (0, 2, 4) ever produced a `g_a_`-prefixed (clothes) candidate,
from any of the three search sources, with the search mechanism now provably working (it finds
the weapon every time). All three granted the exact same three items: a stim, a medpac, and a
weapon — nothing else, no clothing item, ever. Working conclusion: KOTOR's premade "Quick
Character" chargen packages simply do not include a separate wearable clothes item — the class's
own default appearance already looks dressed. If that holds, the original bug report's "clothes"
half was never really about a missing/unequipped item.

**Not exhaustively confirmed**: only 3 of the 6 quick-chargen portraits were tested, and a
full/custom (non-Quick) character creation was not tested at all (this rig's automation only
drives the Quick Character button). If a future round finds a package that DOES grant a `g_a_`
item, the fix here should equip it with zero code changes — `StartingGearSlotRule.Classify`
already handles the prefix generically and is unit-tested; this is a verification gap, not a
known code gap.

## Concrete next steps for a future round

1. Test the remaining 3 quick-chargen portraits (classes 3, 5, and whichever of 0/2/4 wasn't
   already covered by symmetry) and, ideally, a full/custom character creation, to either close
   or reopen the clothes question definitively.
2. A longer post-join play session has not been specifically soak-tested; nothing found this
   round suggests instability, but it is worth a look before calling this fully closed.

## Rig status at time of writing

Rig was free at the start of this round (`clothing-fix-round-4` claimed it via `rig.lock`), used
for four live captures, released at the end (`demo.ps1 -Stop`, lock file removed).
`party_pool_sync` was restored to `2` (the shipped default) on both installs after the isolated
tests above.
