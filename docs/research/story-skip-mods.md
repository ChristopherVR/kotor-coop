# Story-skip mods: Super Skip Taris & Endar Spire, Jedi From The Start (2026-09-29)

Static review of the two mods' shipped source (`Lit Ridl _Super_Skip_Taris.zip`,
`Jedi from the Start v4.0.0.7z`) against the co-op code. Tags: **READ** (read from the mod's
files or our code), **INFERRED** (reasoned, not run), **NEEDS-LIVE**.

## Current follow-up status (2026-09-30)

- Negative XP is accepted as resolved for this follow-up. The signed merge fix remains;
  no further live negative-XP injection is required by the user. Earlier observations
  below describe the pre-fix runs.
- The three LAN beacon/browser assertions were removed from `tools/verify.ps1` at the
  user's request. Historical 46 PASS / 3 FAIL totals below still include them; removal
  is not evidence of a networking fix.
- Shared block v82 distinguishes explicit `warp`/`moviewarp`/`loadgame` commands from
  automatic requests. Automatic requests first seen after a >45-second stall still
  receive status 9; explicit commands retain their intent and existing engine gates.
  Manual `loadgame` still has a 90-second reply timeout, so the engine can execute it
  after that timeout if it was blocked longer. Focused rule/layout tests cover the
  distinction; the combined host-new-game repro and 20-second announce expiry remain
  **NEEDS-LIVE**.
- Verification restores temporary health, positions and personal purse edits, observes
  dialogue locks earlier, and reports unmet prerequisites as SKIP separately from FAIL.
  Real travel away and back now requires `-IncludeTransitionCheck` and a different
  `-TransitionModule`; the old same-module warp was acknowledged without a reload.
  It therefore did not prove roster survival or explain the historical `owed 3 -> 3`.
  Kills, opened doors and journal changes still require a fresh baseline for full
  coverage. Two successive live runs have not been repeated for this update.
- The detection-only profiles do not supply companion or campaign rules. These mods
  retain stock recruitable identities; no custom mapping was justified. JFTS detection
  was checked against `Jedi from the Start-1-4-0-0`, not a verified `v4.0.0.7` install.
- Remaining live checks, including JFTS saved-world refresh, same-world script edits,
  actual turret shooting and story-skip sessions with three or more players, are in
  [PLAYTEST.md](../PLAYTEST.md#story-skip-follow-up-checks-2026-09-30).

The restored game directories and retained `es_baseline` / `jfts_after` saves were not
changed by this follow-up. HoloPatcher `--uninstall` failed during the earlier mod test;
restore from verified file backups for the next install test.

Validation for this follow-up: native x86 plugin publish with `-WarnAsError` passed;
`verify.ps1` parsed without errors, and its PASS/FAIL/SKIP accounting was checked in
isolation. A broad automated run passed 5,222 tests before the refresh command was
simplified to use player-written saves. The final implementation then passed all
5,108 tests in `tools/test-fast.ps1`, including refresh, adoption, rejoin and mailbox
coverage. No live verification run or game-folder deployment was performed.

## How they work

### Super Skip Taris & Endar Spire (TSLPatcher, one GFF edit + three scripts)

- `end_trask01.dlg` gets three reply options under EntryList 55 (Skip Endar Spire, Light Side
  skip Taris, Dark Side skip Taris), each running a script (`skip_spire`, `ls_skip_taris`,
  `ds_skip_taris`) from `Override\`. Nothing in `modules\` changes. **READ**
- `ls_skip_taris.nss`, in one script run, on `GetFirstPC()`:
  `GiveXPToCreature(35320)`, `GiveGoldToCreature(3284)`, `RemoveAvailableNPC(0)`,
  `AddAvailableNPCByTemplate` for slots 0, 1, 2, 6, 7, 8 (Bastila, Canderous, Carth, Mission,
  T3-M4, Zaalbar), about 60 `SetGlobalBoolean` and 60 `SetGlobalNumber` calls,
  `K_CURRENT_PLANET = 15`, alignment, roughly 20 `AddJournalQuestEntry` and
  `RemoveJournalQuestEntry` calls, then `StartNewModule("STUNT_06", waypoint "05")`, then about
  100 `CreateItemOnObject` calls that run after the module change was queued. **READ**
- The start module stays `end_m01aa`; the player leaves it through a scripted STUNT module
  (the Taris-destruction cutscene chain). **READ**
- Its own header note says Endar Spire's items and Taris's are given together, and it never
  gives the tutorial `Min1HP` pin release. Known report elsewhere: invincibility after use
  (the tutorial one-hit-point pin). **INFERRED**, already covered by
  `CarriedPinReleaseController` for a joiner's own capture.

### Jedi From The Start (HoloPatcher, 18 module `.mod` replacements)

- Replaces whole modules in `Modules\`: `end_m01aa`, `end_m01ab`, `danm13`, `ebo_m12aa`,
  `tar_m02ab/ae/af`, `tar_m03ae/af`, `tar_m04aa`, `tar_m05ab`, `tar_m09aa/ab`, `tar_m10ac`,
  `kas_m24aa`, `korr_m36aa`, `manm28aa`, `tat_m18ac`. Also `append.tlk`, `baseitems.2da`,
  two new rows in `globalcat.2da` (`JFTS_JEDI`, `JFTS_INS`), loose `.dlg`/`.ncs`/`.uti`/`.utc`. **READ**
- New-game flow: `m01aa_c01.dlg` (class selector) runs `jfts_a_cjedic/g/s`:
  `AddMultiClass(CLASS_TYPE_JEDICONSULAR, GetFirstPC())` then `ShowLevelUpGUI()`. Then
  `jfts_a_m01aa` empties the two lockers, creates a fake Trask corpse
  (`CreateObject` creature + placeable), destroys real Trask after 0.5 s, and one second later
  `jfts_a_starteq` creates the class-specific starting equipment in the lockers. **READ**
- Dantooine: `danm13.mod` and `k_pdan_cut*`/`k_pdan_zhar03` replaced; training chain removed,
  saber built at the Endar Spire bench (`jfts_a_bench*`). **READ**

## What that means for co-op

| # | Effect | Status |
|---|---|---|
| 1 | **Digest handshake.** `ModulesCensus` (name + size of every `modules\` file) and `TwoDaDigest` (full bytes of `Override\*.2da`) already exist, so a pair where only one player has JFTS gets `W_MODULES`/`W_OVERRIDE`. A pair that both have it gets no warning even if the two installs differ by version. The census is name+size only; two different JFTS versions with equal sizes would not be caught. | READ |
| 2 | **GIT compatibility.** JFTS replaces whole modules, so the shipped GIT the id-prediction uses (`ModuleGit` predicted off the pristine `.mod`/`.rim`) must read the `.mod` first. `mod-compatibility.md` says resolution is `.mod`-first; the module-replacement case has not been run live for JFTS-sized edits. | NEEDS-LIVE |
| 3 | **Class grant.** `AddMultiClassHook` captures EVERY `AddMultiClass` on the local player, not only Dorak's, so the host's JFTS class choice would publish and a connected joiner would get the Jedi-choice popup. Two gaps: (a) the joiner arrives by forced first-join chargen or the host's staged save and never sees the `m01aa_c01` selector; (b) `ShowLevelUpGUI` runs only on the choosing engine. The popup path was designed for Dorak's conversation and has never been watched in-game. | INFERRED |
| 4 | **Start-of-game equipment.** Lockers are emptied and refilled on a 1 s delay after the class choice. Locker contents are containers (container sync), created by script (`script_item_sync`), on the host only; a joiner connected during that second sees either the old or new set. | INFERRED |
| 5 | **Skip: bulk globals.** About 120 globals set inside one script run. The global channel reads the live catalogue (`GlobalCatalogue`, digest) so JFTS's two extra rows are handled, but a single-frame burst of that size has not been exercised. | NEEDS-LIVE |
| 6 | **Skip: recruits.** `AddAvailableNPCByTemplate` x6 plus `RemoveAvailableNPC(0)` from a dialogue script. `dialogue_recruit_sync` and the roster recovery cover recruits made in dialogue; six at once in one script, and the removal of slot 0, are new. A joiner gets them as mirrors (the documented "recruit is a separate catch"). | NEEDS-LIVE |
| 7 | **Skip: items after `StartNewModule`.** About 100 `CreateItemOnObject` calls land after the module change is queued, so they may be created in the new world or lost. `script_item_sync` has a same-frame world-invalidation edge that this hits. | INFERRED |
| 8 | **Skip: STUNT_06.** A scripted `StartNewModule` to a STUNT module from inside a dialogue script. `follow_stunt` is default ON and the transition announce covers scripted starts, but a STUNT module started while the dialogue token is held has not been run. | NEEDS-LIVE |
| 9 | **Skip: XP.** 35,320 XP in one call puts the PC at level 8 or so with a pending level-up. XP is max-merged and level-up is per player. The joiner would then also jump. Whether the joiner's own level-up screen appears is unchecked. | NEEDS-LIVE |
| 10 | **Start-module assumptions are fine.** `end_m01aa` stays the start module for both mods, so enemy scaling `NewGame` classification, the store-stock marker reset and `CharGenDriveController` are unaffected. (An earlier note in this session suggested otherwise; that was wrong.) | READ |
| 11 | **A skip changes what the joiner's forced first-join chargen leads into.** After Taris is skipped the host is in Dantooine or a STUNT scene; the joiner's character is created against `END_M01AA` and adopted into a world that is far past it. Starting gear (`f58c92c`) and the `Min1HP` release are covered; level and class are not. | INFERRED |

## Recommended work, in order

1. Live-run the Super Skip on the rig: host talks to Trask, picks "Light Side skip Taris",
   joiner connected. Watch: bulk globals, six recruits, item burst, the STUNT_06 hop, the
   joiner following. Fix whatever breaks (5-9).
2. Live-run the JFTS class selector as host with a connected joiner: does the popup appear;
   does the joiner's `ShowLevelUpGUI` flow work (3).
3. Tighten the mod-compat detection: add a checked-in profile for each mod under
   `profiles/mods/` (id, detect files, `campaignModules`) so the pair gets a named
   `[super-skip-taris]`/`[jedi-from-the-start]` line instead of a generic `W_MODULES`, and hash
   the replaced module files' content for these profiles (1).
4. Document both mods in `docs/limitations.md` under mod compatibility once (1)-(2) are done.

## Live results (2026-09-29, two-instance rig, mods installed with HoloPatcher CLI)

Both game copies had byte-identical mod files. The host started a real new game through
`newgame` / `chargen quick 0` / `chargen accept` while a joiner was connected. Both installs were
restored from a full backup afterwards.

### Two joiner crashes found first, both fixed (`Odyssey.Menu.cs`, `CharGenDriveController.cs`)

Every joiner first-join (`join_mode` auto/newcharacter) crashed the joiner's game at character
creation. Two stale-pointer reads of the main menu object, which the engine frees when character
creation replaces it: `GetCoopMenuButton` (`mainMenu + 0x1248`) and `FindPanel`'s fallback walk
(`menu + MenuGuiManager`). A "looks like a heap pointer" test passed on the stale address. Both now
require `NativeMethods.IsReadable` first. Verified: first-join chargen, then the joiner pulled into
the host's world, repeatedly. Symbolizing needs the DLL and PDB from the same build (a stale
installed pair named the wrong function once; `demo.ps1` does not deploy the plugin).

### Super Skip Taris (host drives the Trask conversation, "Light Side skip Taris")

| Check | Result |
|---|---|
| Mod options appear in Trask's conversation (node 55, options 3-5) | Yes, after the m01aa_c01 cutscene and Trask's first lines |
| Script effects on the host | XP pool 35,320, credits +3,284, available-NPC mask `0x1C7` (exactly the six recruits), journal `tar_escape`/`tar_planetinfo`, about 120 globals set |
| Cutscene chain | STUNT_06 (conversation), movies `06a`+`05_1C`, STUNT_07, then the turret ambush `m12ab`, then `ebo_m12aa`. The joiner followed every hop |
| Joiner globals | 1,185 globals compared between host and joiner saves: 3 differences (`k_vis_Dantooine`, `K_CURRENT_PLANET` 15 vs 5, `K_LAST_LOCATION`). The 120-global burst carried over |
| Joiner journal | Identical |
| Joiner credits | The peer's purse received the same +3,284 (`credit grant: ... requesting the peer's own purse receive the same amount`) |
| Joiner party availability / XP pool | Not carried: the joiner's party table showed none of the six NPCs available and its own XP pool. Companions reach a joiner as mirrors of the host's active party, and that party was empty at that point; not judged a defect |
| Turret ambush | An unattended gunner loses after about 66 s and the game drops to the main menu (vanilla). `mgkill all` clears it. A real run needs shooting |

Not tested: a joiner present in the Endar Spire when the skip is chosen. The host cannot save
during the opening dialogue, so a joiner cannot adopt that world until after it; a joiner in
limbo at that moment followed the whole chain from a different world without trouble.

### Jedi From The Start (host drives the class selector)

- **The HoloPatcher CLI silently skipped the `m01aa_c01.dlg` replacement** (`!ReplaceFile=1` into
  `end_m01aa.mod`); it reported 0 errors. The class selector therefore never appeared until the
  file was placed by hand. This is an installer-automation problem, not a co-op one. Players using
  the mod's own installer are not affected. The other `!ReplaceFile` targets
  (`jfts_bench01.dlg`, `dan13_practice*.uti`) were not checked.
- With the selector in place: the host chose Jedi Consular; `AddMultiClass` was captured
  (`a script added class 4 to the local player - published as grant #1`), the level-up screen
  opened on the host.
- The joiner received the grant, and **its own popup "The Force is strong in you. Choose the
  training you will pursue" appeared** (first time anyone has watched it). Choosing Guardian
  produced `cls 2:[2@1,3@0]` on the joiner, a different path from the host's. It stood in the same
  Endar Spire world beside the host.
- **Timing risk found:** the joiner was playing the host's relayed opening movie (`01A`, about two
  minutes, blocking the game thread) and could not answer the plugin's check. The Jedi-choice
  request retries 10 times at about 12 s intervals and succeeded on attempt 8. A slightly longer
  blocking movie or load would exhaust the attempts and the choice would be dropped.

### Follow-ups

1. **Done** (`44df090`): the Jedi-choice check no longer spends an attempt when the plugin is silent, so a blocking movie or load cannot exhaust it.
2. **Done 2026-09-30 (see "JFTS live check" below):** locker refill and Trask corpse reach a joiner that loads the host's post-choice save.
3. A joiner present in the Endar Spire during the skip is unreachable without a save. Decision: the host cannot save inside the opening dialogue, so this stays a documented limit (`docs/limitations.md`); a joiner in another world followed the whole chain without trouble.
4. **Done:** `profiles/mods/super-skip-taris.json` and `jedi-from-the-start.json` name each mod when its scripts are present (`agent mods` lists them). They carry no companion rules; content hashing of the replaced files was already done by the digest. Super Skip's markers match the compiled scripts in the parked backup; JFTS's `jfts_a_*` scripts live inside `end_m01aa.mod` (not Override), so its markers are `jfts_c_jedi.ncs`, `jfts_c_jfts.ncs`, `jfts_ins.ncs`, checked against a real HoloPatcher install.

### New-game hijack (found 2026-09-29, fixed)

While the host sat through the two-minute opening movie of a new game, a joiner's door crossing (its
own announce, `follow_lead = any`) was kept for the host because the host was "busy". When the movie
ended the host replayed it and was pulled out of the new game into the joiner's module. A client's
announce now expires after 20 s of waiting (`LeadFollowRule.HostAnnounceExpiryMs`); the stunt-leg
path already had a 60 s expiry, the ordinary path had none. Unit-tested; not re-run live.

### Live attempt 2026-09-30 (did not confirm the fix)

Tried to re-run the hijack on the rig: both instances in END_M01AA, host `quittomenu` + `newgame`,
joiner `warp end_m01ab` during the wait. **The 20 s announce expiry is a hypothesis, not a proven
cause.** What the run showed instead:

- The joiner's main thread was gone for 120 s (`cinematic: the main thread was gone for 120516 ms`,
  0 movies queued going in) starting right after the rig came up, so a console `warp` sent at
  05:49:16 sat queued and ran at 05:50:10 as a plain `travel: moving to module 'end_m01ab'`. That is
  the "queued reload replayed after a blocked main thread" shape, on the joiner.
- By then the host had quit to the menu. The queued travel became a mid-session load teardown
  (`menu: tearing down the existing server for a mid-session load`) and the joiner died 0.2 s later:
  `EXITPROCESS(0xC0000005) called from 0x6FB0C7`, thread 1. Not symbolised.
- A door crossing while the host is in dialogue or combat is refused on the joiner
  (`ClientDoorRefusalRule`), so the "host busy" announce hold only matters for states that rule does
  not cover, such as the new-game movie. The remaining doubt: a joiner load is not obviously
  announced as a transition, so the exact replay path that moved the host is still unproven.

Next: symbolise the `0x6FB0C7` crash with the matching DLL/PDB, find what blocked the joiner for two
minutes at rig start, and re-run the new-game case on a rig where the joiner is not blocked.

### Crash symbolised and a stale-request guard added (2026-09-30, later)

`kotorcoop-crash.bin` for the joiner crash: access violation at `0x64C20D`, `mov ebp,[eax+0x48]` on
the result of `CClientExoApp::GetModule` (`0x5ED530`) inside `HandleServerToPlayerCreatureUpdate_Add`
- the same null-`GetModule` fault `transition-crashes.md` already records. `0x6FB0C7` in the earlier
line is only the engine's top-level exception handler, so it names every crash. The trigger was a
rejoin load request filed before a 120 s main-thread stall, run after it against a world the host had
since replaced, and the agent had stopped polling it at 90 s.

Fix: `MenuLoadController` refuses (status 9, `StatusStaleAfterStall`) a request it first sees after a
stall longer than 45 s, and logs `request #N was pending across a N s stall - refused as stale`. The
agent re-requests against the current world. Verified live with a new `blockpoke menuload` verb: the
joiner process suspended 50 s with a request filed gave `applied 2 status 9`; an ordinary
`loadgame 1` afterwards ran normally ("the world is up and on screen"). The host's new-game
sequence then ran three times on the rig with the joiner connected and the host stayed in its new
game each time. Not a clean repro of the original hijack: no run produced a joiner door announce
that reached a host in the movie (the agent log files append across days, so timestamps in them
must be matched against the run's own start).

### Endar Spire baseline for verify.ps1 (2026-09-30, partial)

Made `000059 - es_baseline` on the host (same folder copied into the client's saves, both newest so
`demo.ps1 -Save 1` loads it): Endar Spire start, 500 credits, `end_sith2` and `end_soldier2` parked 2
units from the PC. `verify.ps1` against it: 36 PASS, 13 FAIL, versus 11 FAIL on the old "Co-op" save
the same day, so it is **not yet a clean baseline**. The failures are the same families on both
saves: no enemy picked as the swing/damage target although 26 are in the presence list, XP and gold
merge checks (the tutorial PC sits at -20 XP, `4294967276` unsigned, and the `grantxp` written before
saving did not survive the reload), the tutorial Min1HP pin against the phantom/revive checks, and 4
engine call signatures. Next: read why the enemy-target loop finds no id (presence slot layout vs
`verify.ps1` line 470), and give the save a positive XP by a real kill rather than `grantxp`.

**Update, same day:** `verify.ps1` read the enemy presence slots from their old home (`0x1410`); the
plugin moved them to `0x6800` (`SharedBlock.OffsetEnemyPresenceSlots`), so both swing/damage checks
reported "no enemy published to attack" on every save. Fixed in `tools/verify.ps1`; with the fix and
`es_baseline` rebuilt from the clean `es_host` start (XP zeroed with `grantxp me 20`, 500 credits, two
enemies parked beside the PC) the run is 38-40 PASS, 9-11 FAIL. The failures that remain are not
enemy-related: the phantom Min1HP pin (tutorial), the LAN beacon checks (flaky, one run passed), the
4 engine call signatures, the per-frame-spam line, and gold + PC experience reaching the client
(host 500 -> 637 and host XP +5 both read back unchanged on the client's engine even though
`PumpProgression` writes `max(host, own)`; the host PC's experience reads `4294967276`, i.e. -20,
in `verify.ps1` before any of its writes, so the XP half may be a sign artefact and the gold half a
real gap). Not diagnosed further.

**Update 2 (same day):** `verify.ps1` XP checks were silent no-ops on a PC whose experience reads
`0xFFFFFFEC`: `[int]4294967281` throws in PowerShell instead of wrapping, so no XP write happened.
They now wrap through `AsI32`. The gold check assumed a shared purse, but `credits_mode` defaults to
personal, so it now reads the client's `coop.ini` and asserts "does not follow" on a stock install.
Result against `es_baseline`: **42 PASS, 7 FAIL**. Remaining, none diagnosed to a code defect:
the phantom "not a sink" attack-bit check and "targetable decoy" faction check (the plugin holds
phantoms Neutral under `local_combat` + damage-dealt and re-permits the attack bit for the
combat-mirror visual, so both readings depend on the parked enemies putting the peer in combat;
the checks predate that design), three LAN-beacon checks (environment-flaky, one run passed),
the 4 engine call signatures, and the per-frame `threat:` log line (6 in one second).

**Update 3 (same day):** `demo.ps1` now rotates each agent log to `.prev` at launch, `verify.ps1` scopes
its per-frame-spam scan to the current plugin process (it read every earlier session's log), the
`sync12c` phantom diagnostic is capped at 8 lines instead of 60 (it filled 54 in one second at every
world load and tripped that check for real), `callaudit.py` carries a `KNOWN_EXCEPTIONS` table for
the four call declarations it flags but that were read by hand (one two-argument function whose
early-out returns with `ret 4`, two tail-jump thunks, one live-proven no-argument call), and the
phantom "not a sink"/"decoy" checks accept the designed Neutral hold and the combat-visual attack bit.
Last run on `es_baseline`: **42 PASS, 7 FAIL**. Still failing, all judged environment or state, none
a confirmed code defect: the three LAN-beacon/browser checks (no broadcast reaches the second
instance on this machine), the door-mirror check (passed on the previous run, failed here), "the peer
really loses those hit points" (hp 6 -> 1 on one run and 6 -> 6 on another: the tutorial Min1HP pin
and a mid-run kill decide it) and the two XP guards (the baseline PC reads XP `0xFFFFFFEC`/`0xFFFFFFF6`
after every reload, whatever `grantxp` wrote before saving, so kill XP arriving mid-run moves the
target under the check). A clean baseline needs a PC with positive XP that survives a save/load;
`grantxp` before `save` did not achieve that and the cause was not found.

### JFTS live check (2026-09-30, HoloPatcher CLI install on both game folders, restored afterwards)

- The CLI install (3,910 patches) put the modded `m01aa_c01.dlg` inside `end_m01aa.mod` this time (52,815
  bytes, identical to the shipped file), so the earlier "silently skipped" note does not reproduce with
  this archive (`Jedi from the Start-1-4-0-0`). The class selector appeared after the opening movie and
  Trask's lines; picking Guardian opened the Level Up screen (`you are now a Jedi Guardian`).
- Host afterwards: `jfts_trask_corpse` creature at (42, 112) and 79 items, 12 not stock (the refilled
  lockers). A joiner that then loaded the host's saved world (`loadgame` of the same save) showed the same
  corpse and the same 79/12, so `jfts_a_starteq` output and the corpse objects are in the saved world.
- Jedi-choice relay: the joiner's request went out on attempt 1 four times while its plugin was blocked
  (`attempt 1/10` each time, no attempts spent) and then the joiner got its own popup ("being asked to
  choose a Jedi class of their own"). This is the first live confirmation of the silent-plugin fix.
- Gap seen: a joiner that was already connected in an OLDER world of its own did not adopt the host's world
  after the choice (`save identity differs after a save on one side ... same world, not rejoining`,
  logged twice; `reconnect` did not change that), so it kept the real Trask and 61 items until it loaded
  the host's save by hand. Whether a joiner standing in the SAME world would have the corpse and
  lockers mirrored live was not tested; the corpse is script-created, host-owned.
- Both game folders restored from `D:\kotor-story-skip-backup2` (Override 3 files, modules 234, dialog.tlk).
  HoloPatcher's own `--uninstall` did not restore them (it reported paths outside the game folder).

### Baseline XP cause found and es_baseline rebuilt (2026-09-30, last)

The negative XP that kept coming back was the joiner's, not the save's: an earlier `verify.ps1` run had
left the client process at `0xFFFFFFF6`, `PumpProgressionHost` max-merges experience as unsigned, and the
host adopted that huge "maximum" after every load (`grantxp` and the save that followed both lost to it;
the saved `es_baseline` read `4294967286` in `Mod_PlayerList`). `es_host` and `es_client` read 0. The
baseline was rebuilt by loading those two clean saves on a fresh rig, `grantgold 500`, `save`; the
saved XP is 0. On a fresh rig with one run of `verify.ps1`: **45 PASS, 4 FAIL** (three LAN
beacon/browser checks that never see a broadcast on this machine, plus "the peer really loses those hit
points", now relaxed to "at least 3, or floored at 1" because enemy damage is scaled 115% for two players
and the Min1HP pin floors the PC). A second `verify.ps1` in the same session drops to about 42 (the
"owed 3 -> 3", interaction-lock and door checks assume untouched state), as its own comments warn.

The unsigned max-merge is only reachable by an artificial negative write today, but a legitimately
negative XP (the Endar Spire PC can read -20 in some states) would poison it the same way.

Adoption note from the JFTS run: the joiner adopted the host's new-game START world (Trask alive, 61
items) at chargen accept, then never re-adopted, so script effects the host makes afterwards inside that
world (Trask destroyed, corpse created, lockers refilled) do not reach it: `PumpProgression`-style
channels cover items on the ground, containers and hostile creatures, not a script-created friendly
creature or a script destroy of one. Written up for players in `docs/limitations.md`.

### New-game hijack: the likelier path, fixed (2026-09-30, last)

`TravelController.Execute` **announces** every plugin-run travel (`PublishTransitionAnnounce`) just before
warping. A joiner whose plugin was blocked (the relayed 2-minute opening movie, or any long stall) and
that had a travel request queued (a follow, a `warp`) ran it the moment the thread came back, in a module
the world had already left, and announced it as a FRESH transition. The host, under `follow_lead = any`
and no longer busy, followed it: out of the new game into the joiner's old module. The 20 s announce
expiry (earlier) cannot catch that, because the announce is fresh on arrival. The stale request itself
is now refused: `TravelController.OnFrame` (every frame) refuses, with status 9, a travel request first
seen after the main thread was gone more than 45 s (`travel: request #N was pending across a N s stall -
refused as stale`); the agent re-asks against the current world. Verified live: joiner suspended 61 s
with `blockpoke travel end_m01ab` filed gave `applied 1 status 9` and it stayed in END_M01AA; a normal
`warp end_m01ab` afterwards ran. The full host-new-game-with-blocked-joiner sequence was not re-run.

### Explicit script-world refresh (2026-09-30)

`reconnect world` now provides a supported recovery for a joiner that adopted the host's
pre-choice JFTS world. After finishing the class conversation and level-up, both players
must save. The joiner then types `reconnect world` within 30 seconds of their own save,
while both players are out of movies, conversations and combat. The command captures the
joiner's exact current saved character and fetches the host's latest saved world through
the existing adoption path. The joiner's own class, personal backpack and personal purse
are carried; the host's saved Trask replacement and locker contents arrive through a load
screen. A plain `reconnect` continues to reconnect without forcing a world load.

The command rejects active saves/loads/chargen, a stale or unreadable local archive, and an
archive whose module or save identity differs from the joiner's current world. It confirms
that local module, load serial and archive timestamp stayed unchanged during capture. It
never requests a native quicksave or moves the user's quicksave aside: an engine save can
be delayed across a blocked thread, making premature holdback restoration unsafe. Explicit
refresh is allowed even when automatic world adoption is disabled.

Verified in automated tests: command parsing, busy-state and same-module load safety, fresh
archive age, host refusal without replacing the session, and existing character/world
adoption coverage. The full JFTS command round trip has not been run live with the mod
installed. This is a manual saved-world recovery; script-created friendly creatures and
locker additions still do not mirror continuously.
