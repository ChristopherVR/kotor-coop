# Manaan: Hrakert Station, the Sea Floor and the Rift (envirosuit, solo by design) (2026-09-18)

Review blocker #5, roadmap item 39. RESEARCH ONLY: no code changed, no game launched, no rig
script run. Method and routine table are the ones in [leviathan.md](leviathan.md) §1 (whole
`InitializeCommands` store run resolved, scripts disassembled with a scratchpad port of
`NcsReader`, wiring read with `tools/kotorfiles.py`). Tags: **VERIFIED** (read out of game
data or the exe), **INFERRED**, **NEEDS-LIVE**.

## 1. The modules (confirmed from `module.ifo` / `.are` `Name` strrefs)

| Module | Area | `OnEnter` | Notes |
| --- | --- | --- | --- |
| `manm26aa`..`26ae` | Ahto West / East / West Central / Docking Bay / East Central | `k_pman_26a_area` etc. | the surface town; `k_pman_sub01` / `k_pman_to28a` enter the station |
| `manm27aa` | Sith Base | `k_pman_27_area` | not part of the rift |
| `manm28aa` | **Hrakert Station** | `k_pdan_28a_area`, `Mod_OnModLoad = k_pman_mod28a` | Unescapable=1; the suit locker and the airlock doors |
| `manm28ab` | **Sea Floor** | `k_pman_28b_area` | underwater walkway, firixa fish |
| `manm28ac` | **Kolto Control** | `k_pman_28c_area`, heartbeat `k_pman_area_28a` | three `EnvrSuit` placeables (`k_pman_suituse`), the press doors, the kolto choice |
| `manm28ad` | **Hrakert Rift** | `k_pman_28ad_area` | the star map, the firixa/sea-beast death trigger |

`k_pman_28b_area` and `k_pman_28ad_area` both call `SetRestrictMode(1)` (routine 58, handler
`CSWVirtualMachineCommands::ExecuteCommandSetRestrictMode 0x00543450` -> `CSWSArea::
SetPlayerRestrictMode 0x00506B90`, which stores the flag at `CSWSArea+0x2B0` and calls
`CSWPartyTable::UnstealthParty 0x00563C60` when it turns on); `k_pdan_28a_area` and
`k_pman_28c_area` call `SetRestrictMode(0)`. **VERIFIED.** Restrict mode is the engine's "no
swapping / no party actions" flag; readers of `+0x2B0` outside script: `SaveProperties`,
`LoadProperties`, `LoadAreaHeader`, `GetPlayerRestrictMode 0x0053CF70`.

## 2. What the scripts do

* **The suit.** `k_pman_suituse` (`OnUsed` of the `EnvrSuit` placeables in 28aa/28ac):
  loop over `GetPartyMemberCount()` party members; for each, if `GetItemPossessedBy(member,
  "man28_envirosuit")` is invalid, `CreateItemOnObject("man28_envirosuit", member, 1)`; then
  `SetPlotFlag(placeable, FALSE)` + `DestroyObject`. `man28_envirosuit.uti`: `BaseItem 64`
  (Plot_Useable_Items), `Plot 1`, no item properties, `ModelVariation 21`. **Every party
  member gets one** (the loop is over the whole party, not the PC). The suit is worn by the
  engine's own clothing rule: appearance row 181 `EnviromentSuit_01`, race `watersuit`, walkdist
  1.2 / rundist 2.3 versus 1.6 / 3.96 for the normal appearances (`appearance.2da`, **VERIFIED**).
  `k_pman_airlock01` is the dialogue condition "PCSpeaker holds `man28_envirosuit`".
* **The airlock.** `man28_airlock.dlg` -> `k_pman_airlock11` (28aa) / `k_pman_airlock02` (28ac)
  / `airlock03`. In 28aa: loop over party indices 1..2; for each non-PC member call
  `SaveNPCState`, then `StartNewModule("manm28ad" if tag=="28d" else "manm28ab", waypoint
  "from"+tag)`, then **`SetPartyLeader(-1)`** and a second loop over NPC slots 0..8
  (`IsNPCPartyMember`): `RemovePartyMember(slot)` with the first slot saved to
  `SetGlobalNumber("K_PARTY_STORE1")` and the second to `K_PARTY_STORE2`, both setting
  `SetGlobalBoolean("K_PARTY_STORED", 1)`. So the party is **stored in two globals and removed**;
  the PC walks the sea floor alone. `k_pman_mod28a` (`Mod_OnModLoad` of 28aa) reads the two
  globals and `AddPartyMember`s them back when `K_PARTY_STORED` is set, then zeroes all three.
  In 28ac `k_pman_airlock02` does the same removal loop (slots 0..8) then `StartNewModule
  ("manm28ab")`, then applies effect 181 (`EffectComplex(181)`, the disguise effect, routine
  463) to each party member via `ApplyEffectToObject`.
* **Returning.** `k_pman_28b_at01/at02` (locked-door fail scripts on the sea floor):
  `StartNewModule("manm28aa", "from"+tag)`, then remove the suit effect from each party member
  (`GetEffect`/`GetEffectType`/`RemoveEffect`). `k_pman_at01/at02` in the Rift do the same for
  `manm28ac`.
* **The Rift.** `manm28ad`: trigger `man28d_death` -> `k_pman_dthbyfish`, `man28_safetrig` ->
  `k_pman_safe01`, `man28_fish.dlg` -> `k_pman_fish01..04`: **`k_pman_fish01` loops
  `RemovePartyMember(0..8)`** then starts `man28_seabeast`'s attack (`ExecuteScript
  ("k_pman_fish05")` -> `SignalEvent` on `man28_death`); `k_pman_dthbyfish` is the "eaten"
  scene: `SetGlobalFadeOut`, `SetPartyLeader(-1)`, party jumps, `SetGlobalFadeIn`, and a
  conversation. The star map (`man28_starmap`, `k_pman_starmap05/06`) sets `K_STAR_MAP`,
  `K_SWG_BASTILA = 99`, `K_CAPTURED_LEV = 5`, `K_KOTOR_MASTER = 30`, `K_STAR_MAP_MANAAN/
  KASHYYYK/KORRIBAN/TATOOINE`, `MAN_STARMAP_FOUND`. `k_pman_28ad_area` reads `MAN_VOLUME_A/B`
  (the puzzle) and both fish scripts flip factions with `ChangeToStandardFaction` (28aa/28ab
  `k_def_firixa_9`).
* **Firaxa / Selkath / kolto.** The firaxa are spawned creatures (`man28_firixa*`,
  `FactionID 1`, `k_def_*` default AI, `ScriptSpawn = k_def_firixa_9`), plain enemies; the
  sea-beast `man28_seabeast` (`FactionID 5`) is Rift-only and scripted. The Selkath/kolto
  choice is in `manm28ac`: `man28_sur3.dlg` -> `k_pman_press03`/`press05`/`sur13`, computer
  `man28ac_comp01` -> `k_pman_press01`, `k_pman_press04` and `k_pman_press06`
  (`MAN_PRESS_ON`, `MAN_LIVEB_STATE` = 2/3/4, door `man28ac_door01` lock/plot flags,
  `SetPartyLeader(-1)` + a four-way `ActionJumpToObject` party gather, then two
  `ActionStartConversation`s). **VERIFIED** as script shape; the exact branch (shut the press
  down vs let the kolto flow, `MAN_PLANET_PLOT` values) was not unrolled.
* The rest is `MAN_*` globals (`MAN_AREA1`, `MAN_GAS1/2`, `MAN_HRAKERT_START/DONE`,
  `MAN_SHORTCUT`, `MAN_PLANET_PLOT`, `MAN_VOLUME_A/B`), all ordinary globals.

## 3. The literal calls, for checking

| script | routine | args |
| --- | --- | --- |
| `manm28aa/k_pdan_28a_area` @3485 | `SetRestrictMode` | `(0)` |
| `manm28ab/k_pman_28b_area` @3972 | `SetRestrictMode` | `(1)` |
| `manm28ac/k_pman_28c_area` @3566 | `SetRestrictMode` | `(0)` |
| `manm28ad/k_pman_28ad_area` @3485 | `SetRestrictMode` | `(1)` |
| `manm28aa/k_pman_airlock11` @3737/3805 | `StartNewModule` | `("manm28ad")` / `("manm28ab")` with waypoint `"from"+tag` |
| `..k_pman_airlock11` @4557/4775/4880 | `SetPartyLeader` / `RemovePartyMember` x2 | `(-1)`, `(slot var)` |
| `..k_pman_airlock11` @4812/4917/4861/4966 | `SetGlobalNumber` / `SetGlobalBoolean` | `("K_PARTY_STORE1"|"K_PARTY_STORE2", slot)`, `("K_PARTY_STORED", 1)` |
| `manm28aa/k_pman_mod28a` @3712/3897/3958/3989/4018 | `AddPartyMember` x2 / `SetGlobalNumber` x2 / `SetGlobalBoolean` | `(slot, var)`, stores reset to -1, `("K_PARTY_STORED", 0)` |
| `manm28ac/k_pman_airlock02` @3510/3642/3710/3756/3852 | `RemovePartyMember` / `StartNewModule` x2 / `EffectComplex` / `ApplyEffectToObject` | slot loop 0..8, `("manm28ad")`/`("manm28ab")`, `(181)`, `(effect, member, 2)` |
| `manm28ac/k_pman_suituse` @86/256/324/413/442 | `IsObjectPartyMember` / `GetItemPossessedBy` / `CreateItemOnObject` / `SetPlotFlag` / `DestroyObject` | `("man28_envirosuit", member)`, `(item, 1)` |
| `manm28ad/k_pman_fish01` @89/183 | `RemovePartyMember` / `ExecuteScript` | loop `0..8`, `("k_pman_fish05")` on `man28_seabeast` |
| `manm28ad/k_pman_dthbyfish` @3885/3896/4546 | `SetGlobalFadeOut` / `SetPartyLeader` / `SetGlobalFadeIn` | `(0,0,0,0,0)`, `(-1)`, `(0,0,0,2.0,0.5)` |
| `manm28ab/k_pman_28b_at01` @3502 | `StartNewModule` | `("manm28aa", "from"+tag)` |
| `manm28ac/k_pman_press04, 06` @3883, 4229 | `SetPartyLeader` | `(-1)` |
| `manm28ad/k_pman_starmap06` @3924/4037 | `SetGlobalNumber` | `("K_CAPTURED_LEV", 5)`, `("K_KOTOR_MASTER", 30)` |

## 4. Co-op design

Nothing here is a `STUNT` module, a minigame or a `RemovePartyMember` burst at the moment the
follower would need it: the airlock is an ordinary `StartNewModule` from a dialogue, so the
announce edge (`CServerExoAppInternal+0x10080`) makes the follower travel, and the follower's
engine independently runs `k_pman_28b_area`/`k_pman_28ad_area` (`SetRestrictMode(1)`) and its
OWN copy of `k_pman_airlock11` if it drives the airlock dialogue. Since the dialogue token is
the host's, the follower's node scripts do not run (the `dialogue-consequences.md` §2
assumption, the same first-thing-to-measure as the Leviathan). Two real problems:

1. **The follower arrives unsuited.** The suit is given by `k_pman_suituse` on a placeable
   `OnUsed`. Only the engine that used it gets the item. `PartyRoster`/`ItemOpKind` item
   channels carry loose-item grants (`DialogueGrantSender`), not `OnUsed` grants; the follower
   walks the sea floor in normal armour with `walkdist 1.6`, and any
   `GetItemPossessedBy(.., "man28_envirosuit")` gate on its engine (`k_pman_airlock01`) fails,
   so ITS airlock dialogue offers no way through.
2. **The party store is per engine.** `K_PARTY_STORE1/2` are globals (merge upward, and are
   reset to -1 by `k_pman_mod28a` on return): a `SetGlobalNumber` from the host's strip and
   the joiner's own strip converge to the same slots, fine, but the `RemovePartyMember` +
   `AddPartyMember` pair on return must agree; `PartyRosterController` reconciles by slot from
   the host's `PartyRosterPacket`.

Option A (recommended for v1): **the joiner stays behind and spectates**, like the swoop lock.
The host owns the walk (it is the engine running the suit dialogue). The joiner is parked in
`manm28aa` (Hrakert Station) or `manm26*` with `PlayerState.FlagSoloSegment` set by the host
while in `manm28ab`/`manm28ad`, and the joiner spectates the host's phantom via the existing
`SoloSegmentSpectateController` -- but the follower's module-follow (`follow_lead`) must NOT
carry it into `manm28ab`/`manm28ad`. Simplest: add `manm28ab` and `manm28ad` (the two
`SetRestrictMode(1)` modules) to the follow-refusal set the way `MinigameModuleRule` refuses
minigame modules, so the joiner stays in `manm28aa` and waits (the "waiting" notice
machinery). **NEEDS-LIVE**: whether a follower left in `manm28aa` with its own copy of
`k_pman_28a_area` OnEnter (which `SetRestrictMode(0)`) is stable.

Option B: suit the joiner too. Needs (1) the joiner to receive `man28_envirosuit` and equip it
(`GiveItem`/`ActionEquipItem`, plus appearance row 181 applied by the engine's clothing rule);
(2) `EffectComplex(181)` disguise mirrored (`OnApplyDisguise` is not among the 12 forwarded
effect kinds -- review §2 "Disguises don't mirror"); (3) the joiner's phantom to carry the suit
appearance, walk speed 2.3, and the sea floor's restrict-mode; (4) `k_pman_fish01`'s strip and
the beast scene to not double-fire. That is at least a disguise/appearance channel plus an
item grant relay: **L**.

## 5. Missing surfaces (RE targets)

1. Follow refusal for `manm28ab`/`manm28ad` (pure rule, a catalogue like `MinigameModuleRule`).
   **S**.
2. Restrict-mode edge: `CSWSArea+0x2B0` (written only by `SetPlayerRestrictMode 0x00506B90`,
   `LoadAreaHeader 0x005091F1`, `Constructor 0x0050D29A`) as a solo-segment trigger the way
   `partyTable+0xF0` is proposed in leviathan.md §5.3 #1. Reader address list above.
   **S-M**.
3. Suit grant relay (Option B only): hook `CSWVirtualMachineCommands::ExecuteCommandCreateItem
   OnObject 0x0052F570` (routine 31) or the `k_pman_suituse` `OnUsed`; disguise mirror
   (`EffectComplex` routine 463 / effect 181, handler `0x005313F0` per the routine table, not
   resolved). **L, RE**.
4. Not investigated (no evidence): the movement-speed writer behind `walkdist 1.2`/`rundist 2.3`
   (appearance-driven, read by the creature stats on equip; whether the phantom uses the same
   rows is unknown) and the exact kolto-choice branch.

## 6. Rig recipe (first live pass)

1. `warp manm28aa` on the HOST (both land; `k_pman_mod28a` OnModLoad runs on load). `partystate`
   for the roster.
2. `useplaceable EnvrSuit` (tag `EnvrSuit`, `(99.8, 106.5, 5.1)`) on the host; confirm the host's
   party got `man28_envirosuit` and the joiner did NOT (`inventory` on both).
3. `startdialog <door 28ab id> man28_airlock` on the host, `selectreply 0` to `k_pman_airlock11`
   (or `opendoor 28aw`, whose `OnFailToOpen = k_pman_airlock12`); watch: the host's roster
   empties (`K_PARTY_STORED = 1`), the announce edge fires, whether the joiner follows into
   `manm28ab` (Option A refusal should stop it), the joiner's `SetRestrictMode(1)` log.
4. Return via door `28bw`; confirm `K_PARTY_STORE1/2` reset and both rosters restored.
No `firescript` on doors.

## Option A shipped and verified (2026-09-19)

`MinigameModuleRule.BuiltIn` now lists `manm28ab` (Sea Floor) and `manm28ad` (Hrakert Rift) beside the four minigame modules, so the existing single-seat machinery (refuse the follow, hold the joiner, wait notice) covers the solo underwater section with no new code path. The joiner stays in the station (`manm28aa`/`manm28ac`, both left OrdinaryModules by test) while the suited host is under water.

Live, host + joiner on the Endar Spire deck, host `warp manm28ab`: the joiner's agent logged `host announced transition to 'manm28ab'` -> `not following the announced transition to 'manm28ab': that is a single-seat minigame track` -> `not following to 'manm28ab': a peer is in a single-seat minigame there - waiting for them instead`, and the joiner stayed in its own world. The on-screen notice was reworded to `<name> is in a solo section - waiting` (it said `is racing`, which was wrong for the turret and here).

**Airlock walk since verified live (see the last section).** Not yet verified: the walk on the sea floor and the return via `k_pman_28b_at01`, and the Rift's death trigger. A `warp` skips the airlock scripts, so the host arrived unsuited; that proves the refusal, not the story path. The joiner-suited option (Option B: relay the suit item and the disguise effect) is unchanged and still needs the item and disguise relay.

## The suited airlock walk, driven live (2026-09-19)

The section that used to sit here said the airlock could not be driven from the console. It can, with two console changes and one bug fix found on the way.

**Recipe (host, rig).** `-ClientAutoCharacter` on the `000004 - manaan station` save (both in `manm28aa`), then on the host:

1. `scriptgive man28_envirosuit` (the airlock's own condition `k_pman_airlock01` is "holds the suit").
2. `startdialog man28_pdroid man28_airlock` (the doors carry the dialogue, but any creature can speak it; `startdialog` now also accepts a door or placeable tag).
3. The conversation runs entries 0 -> 1 -> 6 -> 7 on its own; at entry 7 `selectreply 0` picks reply 8 (the suit branch), the rest auto-advances (4 -> 5) and reply 6 fires `k_pman_airlock11`.

**Result, live (VERIFIED).** The script's `StartNewModule` ran: the host was in `manm28ab` with an empty party (`party table probe: count 0`), the plugin logged `world: unannounced teardown - treating as a load`, and the joiner's agent logged `not following to 'manm28ab': a peer is in a single-seat minigame there - waiting for them instead` and stayed in `manm28aa`. That is the real story path, not the `warp` shortcut. `warp manm28aa` on the host then brought it back: the host announced the transition, both were in `manm28aa` (`peers-here 1` each) and the party table came back with one member.

**The crash on the way (FIXED).** The first two attempts killed the host at `selectreply`. Cause: `DialogueReplyHook` relocated only five bytes of `CSWGuiDialog::SelectReply` (0x6A7110), cutting the 4-byte `mov edx,[esp+4]` after two bytes, so the stub executed the stray `8B 54` fused with its own `jmp` opcode. Only the BASE panel (computer terminals and computer-type conversations such as this one, `ConversationType 1`) commits through that function; story conversations use the cinematic one, which is why no earlier live test hit it. It is a player-facing crash on every computer terminal reply, now fixed (7 bytes, two whole instructions). Symptom to recognise: eip inside a stub block, return address at stub+0x23.

**Still open here.** Walking the suited host along the sea floor and back through `k_pman_28b_at01`, the Rift death trigger, and the joiner-suited option (B). After the host's `warp` back the joiner agent restarted its join from scratch and adopted the host's stale staged autosave (`END_M01AA`); that is the documented staged-autosave behaviour of a rejoin on this rig, not the airlock.


## Real return and one-rejoin recovery (2026-09-23)

With `c4bb5dd` native save readiness, `68f47f0` bounded adoption retries, and
`79c9492` consuming the owned load edge, the host took the shipped airlock
conversation into `manm28ab` at 07:01:23. The joiner stayed in `manm28aa`.
Host `walkto 127.3 97.5 6.2` and ordinary `usedoor 28bw` ran the actual return
OnFail script, returning at 07:01:44. The host was downed by combat before the
return save could become ready; serving remained blocked. For this staged
recovery check, god mode and a safe-position teleport were used at 07:02:01–03.

The joiner captured its own character at 07:01:50. Its first adoption attempt
was refused because no fresh host return save was ready. The host then wrote
a fresh save at 07:02:08 in 141 ms. The scheduled retry adopted identity
`75C1DD5C` at 07:02:10, carried the 07:01:50 character, and completed a single
rejoin at 07:02:15. Shared enemies, doors, items and party returned; no immediate
OwnLoad replay occurred. Both processes remained alive, and named
`campaign_return_fixed_host` / `campaign_return_fixed_joiner` saves were retained.

The earlier 06:45 return on the preceding managed build recovered but repeated
one unnecessary OwnLoad rejoin. That repeat is the regression addressed by
`79c9492`. These are staged return checks, not a natural underwater campaign:
Rift story/death branches, combat recovery without staging, and option B remain
outside this proof.
