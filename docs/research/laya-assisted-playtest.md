# Laya-assisted playtesting: can it replace the human gates?

Append-only. Question from the owner (2026-10-08): the release gates in
[RELEASE-READINESS.md](../RELEASE-READINESS.md) that "need people" — can Laya do them?

## 2026-10-08: assessment

### What Laya is here

Laya 0.3.28 is a local model behind the `laya-kotor-pilot` stdio MCP server that Codex
registered on 2026-10-07 (venv and checkpoints under the git-ignored `artifacts/laya-pilot/`).
Its eight tools (`laya_status`, `laya_route`, `laya_predict`, `laya_predict_batch`,
`laya_route_batch`, `laya_shortlist`, `laya_preset`, `laya_decide`) take a **text/JSON state**
and a question with named choices, and return a choice plus a confidence and an abstention
flag. CPU only, about 400 ms per call warm. It has **no image input**.

`tools/laya_playtest.py` (Codex, uncommitted at the time of writing) wraps it: it reads the
host's engine state out of process (module, position, nearby creatures/doors, conversation and
reply count), builds one to three legal actions with fixed rules (`candidates()`), asks Laya to
pick one, and executes it through the plugin's console mailbox (`input`, `attack`, `talktag`,
`opendoor`, `usedoor`, `walkto`, `screenshot`).

### What the evidence says

- Classification smoke (`artifacts/laya-pilot/report.txt`): 3 of 5 labelled cases right on both
  checkpoints; it called a desync and a stuck player "healthy". A 0.7 confidence gate flagged
  the wrong answer but still returned `choice=healthy`, so a consumer that reads only `choice`
  is misled.
- The 22-step "clean playthrough" (`artifacts/laya-pilot/evidence/clean-playthrough/report.json`)
  never left `end_m01aa`. Every step Laya "decided" had a single sensible option (press Space to
  continue a voiced line, or one numbered reply with no reply text available); the other five
  steps fell back to the fixed rules. The rule generator did the playing; Laya added no
  information.

### What the human gates actually need

The open P0 human items (named save, dialogue over the host's equipment menu, Trask's
recruit/switch/door, subtitles and skipping, the enemy-sighted banner, manual pause on both
peers) need two things:

1. **Real retail input**, through the same paths a keyboard and mouse use.
2. **Judging what is on screen**: a dialogue overlay, a subtitle, a banner, the right body.

Laya cannot do (2) at all. (1) is not a model problem. The real gap was that the plugin's
own polled keys (`NativeMethods.IsKeyDownHere`: Enter-to-chat, the pause key, F9, console Tab,
spectate cycling) read `GetAsyncKeyState` on a foreground window, which the ghosted rig windows
never are, so those paths could only be pressed by a person.

### What was done instead (and worked)

- `IsKeyDownHere` now also honours a key held by `input key <dik> down` when
  `isolate_real_input` is on (rig only; `NativeMethods.RigKeyHeld`, set at startup). Real
  players are unaffected.
- The screen is judged by a multimodal reviewer (Claude reading the plugin's `screenshot <name>
  [front]` BMPs). Note the back buffer reads black on some full-screen GUIs; retake with `front`.
- Engine state is the oracle wherever one exists: the save folder and its `savenfo.res`
  `SAVEGAMENAME`, `tools/pausestate.ps1`'s mask, positions from `laya_playtest.py --snapshot`.

Results on the rig, 2026-10-08, merged main:

- **Named save, return to play, enemy-sighted panel.** Esc → Save Game → New Slot → Save →
  `input text ClaudeSave1` → `input char enter` landed `000901 - Game900` with
  `SAVEGAMENAME = ClaudeSave1`; Cancel/Close returned to play with a hostile Sith soldier in
  view, no ENEMY SIGHTED panel, pause mask 0.
- **Enter is not stolen by chat in the save-name popup.** `input key 0x1C down` +
  `input char enter` + `input key 0x1C up` (both halves of a physical press) saved
  `000902 - Game901` and opened no `say` box. Control: the same held Enter in play opened the
  `say` box.
- **Minigame hold with the new `gamepad = 0` default.** Host `setglobal K_RANDOM_MINI_GAME true`
  + `warp m12ab`; the joiner logged `minigame lock: a peer is racing`, and two seconds of held W
  moved it 0.000 units. After the host returned (`minigame lock: the minigame ended`) the same
  input moved it about 10 units.

### Recommendation

Do not use Laya as a pass/fail judge or as the player for the human gates. Script each gate as
real-input steps with engine-state assertions, capture screenshots at each step, and have a
multimodal reviewer look at them. Laya could still be useful later for **exploration**
(choosing among many genuinely different legal actions over long runs to find crashes), once
the candidate generator offers real alternatives and reply text is exposed. Even then, measured
checks own PASS/FAIL.

What still needs a person: whether things look and feel right in motion (animation, camera,
subtitle timing with real sound), a second real PC and network, and the full campaign.

## 2026-10-08 (later): the harness, `tools/coop_playtest.py`

Built on `laya_playtest.Game` after the owner asked for a harness that plays the game. Both
players act each step; Laya (or fixed rules) picks among legal actions; a referee compares the
two games after every step and turns a lasting difference into a finding with both screenshots.

New observation surface (all read out of process, no plugin change):

- **Reply text.** `CSWGuiDialog` reply items are inline: item i's text is a CExoString at
  panel + 0x158 + i * 0x140. The engine numbers them ("1. Okay."), and the reader rejects a slot
  whose number does not match. Found by walking pointers from the panel on Trask's tutorial
  replies.
- **Open menus.** `CGuiInGame+0x30` misses popups (a container window left it 0). The active
  panel list does not: `CSWGuiManager` (`CGuiInGame+0x38`) keeps a `CExoArrayList` of active
  panels at +0x88. With nothing open it holds `main_interface` plus one unnamed panel; the
  Equipment screen replaces the HUD with `equip` + `menu`. Panel pointers in `CGuiInGame` stay
  allocated when hidden, so a non-null field means nothing on its own.
- **Closing a menu.** `closegui` (HideSWInGameGui) closed Equipment and left both games running
  (pause mask 0). An injected DirectInput Escape did not close it. The container popup has
  Get Items (399,391) and Close (399,441).

Three Laya runs on the Endar Spire (10, 10 and 15 minutes):

- Laya's share of real decisions rose from 8 of 118 to 49 of 100 once replies carried text and
  the confidence bar was 0.4. Its picks were sensible (answer, close the menu, head for a door).
- The referee's two findings were both the harness's own fault: an open container blocks a
  conversation join, and the menu check missed the popup. With the menu fixed, a queued join
  completed as soon as the container closed. No mod bug was confirmed by the harness yet.
- Navigation is the limit. Drive keys hit walls; `usedoor <id>` pathfinds and opened nearby
  doors but refuses past 40 m; `walkto` toward a far door was accepted ("walking to x,y") and
  never moved the player, consistent with the tutorial's story-locked route. The Endar Spire
  tutorial needs the player to equip gear before Trask opens the way, which no action offers.

Next steps if this is pursued: start from saves past the tutorial (Taris, Dantooine) where
areas are open, add equip/level-up handling, stage hostiles for the combat checks, and keep the
referee as the only judge.

## 2026-10-08 (evening): playing the Endar Spire

The harness was extended so the opening can be played without staging verbs. Retail clicks
measured at 800x600 (constants in `tools/coop_playtest.py`):

- Equip: HUD Equipment icon (565,20), right-weapon slot (370,325), body slot (320,275), second
  list row (550,263), OK (400,485), then `closegui`.
- Popups: status/XP notice OK (399,316); message box OK (399,329); container Get Items
  (399,391) / Close (399,441); Party Selection OK (398,485), first portrait (176,210), Add (270,449).
- Character swap: the HUD's second portrait (94,577), both directions. `controlled_tag` in the
  snapshot is the controlled creature's tag ("" for the player).
- Level up: HUD Character icon (625,20), Auto Level Up (268,297), confirmation is a message box.
- New verb `unlockdoor <id>`: the door target menu's Security action
  (`SendPlayerToServerInput_UnlockObject` 0x00677DE0, door id + OBJECT_INVALID, as 0x00683E50
  sends it). Reach 80 m: the server walks the creature over before it rolls (seen from 11 m).
- `usedoor` by object id now reaches 100 m (`DoorUse.MaxUseDistanceById`); by tag it stays 40 m.
- Attack orders run the creature to a target anywhere in the area (seen from 74 m).

What the unattended runs reached, from a clean `es_baseline`, with Laya choosing most actions:
footlocker and gear, Trask's tutorial and recruitment, Party Selection, the lockdown door (swap
to Trask), the Security tutorial door, the room fights and body loot, Auto Level Up for both
players, the level-up gate (`end_door15`, script `k_pend_door15`, needs `END_ROOM7_DEAD`, i.e.
room 7 cleared), the Jedi cutscene (`end_door16` -> `end_jedicut`), the reinforcements, and the
bridge-sealed beat (`END_BRIDGE_SEALED = 1`). Endar Spire story door facts from the game data:
`end_door05`/`end_door06`/`end_door08` are key-required plot doors that never open.

Stopped at `end_door19` (unlocked, OnOpen `k_pend_traskdie1`, conversation `end_door01`), the
door between the player and the open transition `end_door10` -> `end_m01ab`. A native
ChangeDoorState and a door conversation start were both accepted and did nothing; `usedoor`'s
transition path walks with `ActionMoveToLocation`, which never moves the player character in
this build (failure 8). Next: a real "walk the player" input (drive control) or the door's
click path, then end_m01ab (Carth, the escape pods).

Referee results: one census finding (two host reinforcements absent on the joiner) was a harness
false positive: script-spawned enemies reach the joiner as materialised stand-ins built from the
co-op blueprint, tagged coop_phantom. The census now counts those. No mod bug was confirmed by
the harness in these runs. Side observation: the host's status line printed the host's public
IP address on screen ("co-op: hosting on port 27140 - public address 103.95.113..."), which a
stream or screenshot would expose.

## 2026-10-08 (night): the referee compares synced state

The referee now also compares what the mod promises to keep in step, each with its own settle
window (`SETTLE` in `tools/coop_playtest.py`), each a pure function of the two snapshots with
offline tests. Inventory and credits are personal by default and are not compared.

| Check | Read (out of process) | Rule |
| --- | --- | --- |
| door | door `+0x2CC` open byte, `+0x2C4` locked | matched by tag + position (0.1 m); open vs closed, locked vs unlocked agree |
| container | placeable `+0x260` locked, `+0x334` has_inventory, `+0x338` open | containers only |
| enemyhp | creature `+0xDC` hp, `+0xE0` max | same object id and tag, hostile, not party/coop; within max(3, 15% of max); dead on one = dead on both |
| party | party flag `+0xA88` | non-empty non-coop party tags equal on both (the PC's tag is empty) |
| peerhp | `RemoteSlot[8]` at block `+0x50` (0x40 each: health `+0x18`, max hp `flags >> 16`) | slot health vs the peer's own hp; the coop-tagged body nearest the slot vs clamp(slot health, 1, max) |
| journal | party table `+0x11C` -> journal `{data, size, cap}`, 0x3C entries, tag `+0x18`, state `+0x20` | same tags, same states |
| globals | server internal `+0x100FC` catalogue; booleans `+0xA668` (113 bytes, MSB first), numbers `+0xA6D9` (500 signed bytes) | same non-zero values by name; the bridge's four per-player names and `k_vis_*` left out |
| xp | player `+0xA74` -> stats `+0x68` | equal (max-merge on both ends); skipped while a companion is controlled |

## 2026-10-08 (late): Claude plays the Endar Spire for the story

`--policy claude` (Sonnet, headless, budgeted) on `000002 - es_host`, five runs, each from a fresh
load. It is asked only for real choices, and the rules handle popups, menus and plain fights.

| Run | Reached | Ended by | What it taught the harness |
|---|---|---|---|
| 1 | Trask's door open, journal `end_attack` 1 -> 20, Trask in the party | stopped at 25 min to investigate | A `level_up` with nothing to level left the character sheet over a bark; "continue" pressed Space under it, which toggles pause, and the joiner sat frozen for 13 min. Menus over a conversation are closed first now, a paused game is unpaused, and the level gate stays shut past level 20 (a 7.3M-XP veteran joiner). |
| 2 | nothing: 200 steps on the start room's locked doors | stopped | Trask only opens the door once you equip the footlocker's gear, and the journal never says so. The footlocker was offered as "Search the end_locker01". `kotor_lore.placeable_names` now labels it "Footlocker (end_locker01), holds items", and the goal says a stuck story door usually wants gear first. |
| 3 | footlocker, equip, Trask, door, journal 20, the cut2 corridor | **host crash** 0x58063B | The `attack` verb snaps the attacker to melee range: it put the host 25 m away inside the sealed cutscene room behind a window, and the engine's pathing faulted. Fights now walk the walkmesh route first. |
| 4 | the same | **host crash** 0x58063B again | The room graph had a "route" ending at the window; the walk stopped short and the snap still fired. The harness now re-reads the distance after walking and attacks only from within reach. |
| 5 | past the cut2 corridor, about (44, 70), journal 20 | 50 min, no crash | (see open findings) |

Mod bugs the runs found and fixed the same night:

* **Liveness "gone" verdicts expired after 15 s.** The joiner pauses reconciliation in a conversation
  (cutscenes are conversations), so nine `end_cut2_*` actors the host destroyed stayed on the joiner for
  good. Fixed in `LivenessPump`/`LivenessBlockPage`; live-verified (status-engineering.md).
* **The Leviathan host crash** turned out to be a retail over-read under the rig's full page heap
  (`WaypointGuardHook`, status-engineering.md).

Open findings (evidence in the run logs under `artifacts/coop-playtest/es-claude-20261008-run*`):

* **Joiner-only reinforcements.** Run 5, 23:32: the joiner had `end_reinforce3`/`4` (0x16A, 0x16E, hostile,
  full health) that never existed on the host. The joiner had just stood in trigger 0x53 and relayed it;
  the host fired 0x57 for the joiner's body. Likely the joiner's own engine ran a spawn the host did not.
* **Companion mirror flapping while the host controls a companion.** Run 3: after the host took control
  of Trask, the joiner's log alternated `party drive: mirroring 0/1 of the host's companions` about
  3 times a second for minutes. A static read did not pin it; log which `DriveOne` return fires and
  compare the party snapshot's `OccupantGeneration` with the roster row's.
* **Conversation join dropped.** Run 1: the joiner joined the host's Trask conversation, then left it
  within 2 s while the host stayed in it. That was while the host controlled Trask and the lockdown door's
  script had started the conversation. The plain case (host talks to Trask, joiner clicks him) joins
  within 1 s and follows node changes within 1 s.

A staging-verb caveat for players: the dev console's `attack` (and any snap/teleport verb) can put a
character somewhere the engine cannot path from, and the retail engine may crash there. That is not
reachable without the console.
