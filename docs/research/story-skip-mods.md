# Story-skip mods: Super Skip Taris & Endar Spire, Jedi From The Start (2026-09-29)

Static review of the two mods' shipped source (`Lit Ridl _Super_Skip_Taris.zip`,
`Jedi from the Start v4.0.0.7z`) against the co-op code. Tags: **READ** (read from the mod's
files or our code), **INFERRED** (reasoned, not run), **NEEDS-LIVE**.

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
2. Confirm `jfts_a_starteq` (locker refill on a 1 s delay) and the Trask corpse objects on a joiner. Still needs a live run.
3. A joiner present in the Endar Spire during the skip is unreachable without a save. Decision: the host cannot save inside the opening dialogue, so this stays a documented limit (`docs/limitations.md`); a joiner in another world followed the whole chain without trouble.
4. **Done:** `profiles/mods/super-skip-taris.json` and `jedi-from-the-start.json` name each mod when its scripts are present (`agent mods` lists them). They carry no companion rules; content hashing of the replaced files was already done by the digest. Marker names come from the mods' source scripts; confirm the compiled `.ncs` names on a real install.

### New-game hijack (found 2026-09-29, fixed)

While the host sat through the two-minute opening movie of a new game, a joiner's door crossing (its
own announce, `follow_lead = any`) was kept for the host because the host was "busy". When the movie
ended the host replayed it and was pulled out of the new game into the joiner's module. A client's
announce now expires after 20 s of waiting (`LeadFollowRule.HostAnnounceExpiryMs`); the stunt-leg
path already had a 60 s expiry, the ordinary path had none. Unit-tested; not re-run live.
