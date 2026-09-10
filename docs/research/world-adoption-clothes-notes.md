# Clothing bug — scratch handoff notes (2026-09-10, round 3)

Status: NOT FIXED. The bug is now understood far more precisely than rounds 1-2 believed, and
the headline claim from round 1 ("the starting weapon reliably equips") is **RETRACTED** — see
below. Read `docs/research/world-adoption.md`'s "Round 2026-09-10d" section for the full
derivation; this is the short version plus concrete next steps.

## The one-line summary

For a first-time `join_mode = newcharacter` joiner, the freshly-created character's creature
object carries NO real, queryable numeric id anywhere this plugin can read one — not through
`Odyssey.GetPlayerObjectId()`, not through the client-side party (`GetControlledCreatureObjectId()`,
added this round), not even read directly off the chargen panel's own creature pointer before
`BeginPlayGame` runs — for the ENTIRE equip-repair window (measured: 90+ seconds, bounded only by
the repair's own wall-clock ceiling). `EquipUnequippedStartingGear`'s candidate search is
entirely id-based, so it can never find genuine starting gear (weapon OR clothes) during this
window: there is no real id yet to match a Possessor field against.

## What rounds 1-2 got wrong

Round 1's "the weapon reliably equips across six clean joins" was **a misattribution**. This
rig's shipped default is `party_pool_sync=2` (host-authoritative co-op inventory sync). When a
joiner connects, the HOST's own inventory items get synced into the joiner's shared party-pool
repository automatically (`CheatController.PartyPool.cs`'s `PartyPoolAdd`, logged as
`cheat: added '<resref>' x<n> to the party pool`). The engine stamps a freshly-linked synced
item's `Possessor` field with whatever `GetPlayerCreature()` currently reports — which, per this
round's finding, is ALSO the broken placeholder. `EquipUnequippedStartingGear`'s candidacy check
(`possessor == creatureObjectId`) then matches by COINCIDENCE — both sides equal the same wrong
placeholder — and a weapon synced from the HOST's own inventory gets equipped onto the joiner's
creature, which looks exactly like "the joiner's own starting weapon worked." **Live-isolated
this round** (`party_pool_sync` temporarily set to `0` on both installs, restored to `2`
afterward): with the sync confound removed, a clean capture found **zero** real candidates for
the entire repair window — personal repo count=0, pool repo count=0, all 32 of the id-window
scan's candidates unresolvable. Neither weapon nor clothes was ever a genuine candidate.

This means the equip-repair mechanism may never have actually equipped a joiner's own genuine
chargen-granted starting gear, in any round's testing — every prior "success" was the sync
collision above.

## What round 3 tried, live, and falsified

1. Added the requested cross-check log line (`NewCharacterCaptureController.cs`, at the
   `EquipUnequippedStartingGear` call site): prints `GetPlayerObjectId()` next to
   `SyncServerPlayerToLeader`'s last-known `registered` value. **Confirmed the 0x7FFFFFFF bug is
   STRUCTURAL, not intermittent**: it fired on 3/3 live captures this round, every single poll
   of the whole window (previously seen once, in round 2). `SyncServerPlayerToLeader` never ran
   even once in any of these captures — a first-time joiner has no prior leader to switch FROM,
   so the mechanism that would otherwise keep the server's player slot in step never has a
   reason to fire.
2. Hypothesised a fix: `Odyssey.GetControlledCreatureObjectId()` (new this round,
   `Odyssey.Menu.cs`) reads the CLIENT-side `CSWParty` member[0] id directly, independent of the
   server-side wiring gap above. **LIVE-FALSIFIED, twice**: it reads the exact same class of
   placeholder (`0x7FFFFFFF`, or `0x7F000000` — `OBJECT_INVALID` — before any client party even
   exists) for the entire repair window too. Reverted to a diagnostic-only cross-check; the
   actual repair still uses `GetPlayerObjectId()` (no behavioural change from round 2's
   baseline).
3. Went one step further: added a diagnostic read of the chargen-time creature pointer's own id
   field (`CharGenDriveController.Accept()`, before `BeginPlayGame` even runs — the earliest
   possible read of the object). **Also `0x7F000000`.** So no accessor this project has, at any
   point from class selection through 90+ seconds into the new module, ever sees a real numeric
   id for this creature.
4. Timing correlation across all 3 captures: the real numeric id (`0x127`/`0x128`-style, seen via
   `CSWPartyTable`, the "party table probe" log line) first appears **2-3 seconds AFTER** the
   equip-repair's own save fires — i.e., right around when the AGENT picks up the save and starts
   adopting the character into the HOST's world (a fresh module load, which assigns ids from
   scratch). This is suggestive, not proven: it is NOT yet established whether the joiner's
   creature ever gets a real id inside the ORIGINAL Endar Spire module at all, or only receives
   one as a side effect of the save/adopt round-trip into a different module entirely.

## Concrete next steps (in order)

1. **Settle the timing question above.** Extend `EquipRepairMaxWallClockMs` past 90s (try
   150000+) for one diagnostic-only capture (do not ship this — it would make every join take
   that much longer) and watch whether `GetPlayerObjectId()`/`GetControlledCreatureObjectId()`
   EVER stop reading a placeholder before the module ends, or whether they stay wrong for the
   entire Endar Spire prologue regardless of how long you wait. This is the fork in the road:
   - If a real id eventually appears in the original module: the fix is to make the repair
     actually wait for it (poll for a real id rather than counting a fixed budget), not to widen
     `StartingGearIdWindow` or improve classification, both of which are moot while
     `creatureObjectId` itself is a placeholder.
   - If it never appears there: the whole "capture and repair equip pre-save" design is
     structurally wrong for this join path. The repair would need to move to AFTER adoption,
     running in the HOST's own session against the joiner's real post-load id, not before the
     character is ever saved out of the chargen module.
2. Once a real id is obtainable (by whichever path #1 settles), THEN revisit the original
   questions: does clothes appear as a candidate at all (the original round 1/2 question), and if
   not, widen the id-window/repo scan; if it appears but misclassifies, check
   `StartingGearSlotRule.Classify` against its real tag. Both are still completely unanswered —
   every capture to date has run against a placeholder id, so the "personal repo empty, pool
   repo empty" reads from this round say nothing about whether a CORRECTLY-ADDRESSED search would
   find the items; they only prove the id used so far was never correct.
3. Any future live pass MUST isolate `party_pool_sync` (set to `0` for the duration of the test,
   restore to whatever the shipped default is afterward — do not commit a changed default) to
   avoid re-introducing the round-1/2 misattribution. A "success" without this isolation proves
   nothing about the joiner's own starting gear.

## Rig status at time of writing

Rig was free at the start of this round (`clothing-fix-round-3` claimed it via `rig.lock`), used
for three live captures, released at the end (`demo.ps1 -Stop`, lock file removed).
`party_pool_sync` was restored to `2` (the shipped default) on both installs after the isolated
test in step 3 above.
