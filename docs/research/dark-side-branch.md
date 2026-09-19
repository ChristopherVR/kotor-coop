# The dark-side branch: the Lehon temple summit and the DS ending (2026-09-18)

Review blocker #11, roadmap item 43, `star-forge-finale.md` §3.1 (which marked it INFERRED).
RESEARCH ONLY: no code, no game, no rig script. Method as in [leviathan.md](leviathan.md) §1.
Tags: **VERIFIED**, **INFERRED**, **NEEDS-LIVE**.

## 1. Where the choice is (confirmed)

`unk_m44ac` (Unknown World - Temple Summit): `OnEnter = k_punk_44ac_en`, entry `(57.3, 59.9,
13.8)`. Trigger `unk44_bastcs` (`k_punk_bastcs_en`) fires the Bastila scene; the creature
`unk44_evilbast` (tag `Bastilae`, `FactionID 5`, `Min1HP`, `ScriptUserDefine = k_punk_bast_ud2`)
carries `unk44_evilbast.dlg` (`unk44_evilbast2.dlg` for the Juhani-only variant). The Star Forge
is not where the summit choice is made: `k_psta_darkside`/`lightside` (`sta_m45ac`) write
`G_FinalChoice` for the Star Forge duel, and `k_punk_end`/`k_punk_end2`/`k_punk_evilpc`/
`k_punk_goodpc` (`unk_m41aa`) write and read it on the Lehon beach. The summit is the one
whose `G_FinalChoice` write is a player choice: `k_punk_bastjoin` sets `G_FinalChoice = 1`
(dark), `k_punk_bastesc` sets `G_FinalChoice = 2` (light) and `PlayMovie("05_8E")`.

## 2. The dialogue and the scripts (VERIFIED, GFF + bytecode)

Dark route through `unk44_evilbast.dlg` (reply text read from `dialog.tlk`): the player answers
"Yes! Together we can rule the galaxy!" (R65, `k_act_darksml`) -> "Kill him!" entries E60..E68
(Bastila tells the player to strike Jolee/Juhani) -> the join chain **E62 `k_punk_bastadd`**
(`SetGlobalNumber("K_SWG_BASTILA", 13)`) ... **E63 `k_punk_bastjoin`** -> E64 `k_punk_bastjourn`
(journal) -> the fight. Light route: R58 `k_punk_goodfinal` (`AdjustAlignment` x5) / R70
`k_act_lighthigh` -> R60 `k_punk_bastesc` ("I reject the dark side"). The Bastila fight
(`k_punk_bastatt`, the 74 KB AI package; entries E1..E28 alternate `k_punk_bastatt02` = party
effects and `bastatt07`) is entered from R195 (`Script = k_punk_bastatt`) when the player refuses
her; `k_act_atkonend` is the "attack on end of conversation" one-liner.

**The two companion flips.**
* `k_punk_bast_ud2` (Bastila's `OnUserDefined`, event 1006 = "attacked"): when Bastila loses
  more than 150 HP it `EffectResurrection`s the PC, `ClearAllActions`, then
  **`ChangeToStandardFaction(Bastila, NEUTRAL=5)`** ... `CancelCombat` on the PC, Jolee and
  Juhani, `ClearAllEffects`, **`SetPartyLeader(-1)`**, then **`RemovePartyMember(4)` and
  `RemovePartyMember(5)`** (Jolee, Juhani leave the party) and starts a conversation. The
  plain `k_punk_bast_ud` (the `evilbast2` twin) has the light branch: `SurrenderToEnemies`,
  `ChangeToStandardFaction(5)`, `CancelCombat` on party members 0/1/2, `SetPartyLeader(-1)`.
* `test3` (summit; wired as the tail of the evil dialogue): `SetPlotFlag(FALSE)` on
  `bastila`/`jolee`/`juhani`, then **`ChangeToStandardFaction(Jolee, HOSTILE_1 = 1)`** and
  **`ChangeToStandardFaction(Juhani, HOSTILE_1 = 1)`** (offsets 2557 / 2673), each followed
  by an `AssignCommand`/`DelayCommand` attack action. This is the "companions turn on you".
* `k_punk_bastjoin` (the join): `G_FinalChoice = 1`, fade out, `RemovePartyMember(4)`,
  `RemoveAvailableNPC(4)`, `RemovePartyMember(5)`, `RemoveAvailableNPC(5)` (Jolee, Juhani
  **removed from the roster, not just hostile**), reads `GetClassInformation` x3, then
  **`AddAvailableNPCByTemplate("p_bastilla001".."p_bastilla006", slots 15..19)`** -- six
  level-appropriate "Sith Bastila" templates keyed by the player's class level; and
  `k_punk_bastjoin2` then `AddPartyMember(0)` (Bastila joins the party). **VERIFIED.**
* Bastila's dialogue reads `k_punk_bastrom` (romance) etc.; the fight arms
  `ChangeToStandardFaction(Bastila, 1)` in `k_punk_bastatt` @2447 and sets `SetPlotFlag` on
  the party members during it.

Lehon beach (`unk_m41aa`, the Rakata fights before the temple) has the other flips:
`k_punk_fight14` (`unk41_blackrak4/5`, `redrak1..3` -> faction 5), `k_punk_misdie`
(`mission`/`zaalbar` faction flips + `UNK_PARTYSHOWDOWN = 2`), `k_punk_41aa_en` (reads
`G_FinalChoice`, sets `UNK_USEDDARKSIDE`), `k_punk_finale04` (**`DestroyObject` on every
companion body** and `StartNewModule("ebo_m41aa")`).

## 3. The ending chain (VERIFIED)

`sta_m45ad` `k_psta_mlkend` / `k_psta_ud_malak` (Malak's death): `STA_MALAK_DEAD = 1`, then
`G_FinalChoice`/`K_SWG_CARTH` gate `StartNewModule(... movie "51" or "54", "STUNT_54a")`. The
dark ending (`G_FinalChoice == 1`) goes `STUNT_54a` -> `STUNT_55a` (`k_scene_start`:
`AddAvailableNPCByTemplate("p_bastilla001", 0)`, `SetPartyLeader(-1)`, then movie `55`
with `bAllowSeparateSkips`) and `sta_m45aa` `k_psta_bastleav2` picks `STUNT_56a`
(movie `56`) or `STUNT_54a` (movie `51`); `STUNT_56a` -> `STUNT_57` (the credits, with
`CREDITPLAY`). `STUNT_44`/`STUNT_42` are the dark/light take-off vignettes
(`AddAvailableNPCByTemplate("p_bastilla"|"p_carth")`, `SetPartyLeader(-1)`). All are STUNT
modules: the follower gate refuses them (review blocker #6); the movie queue carries the
names. The choice itself never conflicts across players because it is a dialogue reply
(single token owner, `star-forge-finale.md` §4).

## 4. Literal calls

| script | offset | routine | args |
| --- | --- | --- | --- |
| `unk_m44ac/k_punk_bast_ud2` | 3377 / 3793 / 3940 / 3957 | `ChangeToStandardFaction` / `SetPartyLeader` / `RemovePartyMember` x2 | `(Bastila, 5)`, `(-1)`, `(4)`, `(5)` |
| `unk_m44ac/k_punk_bast_ud` | 3277 / 3343 | `ChangeToStandardFaction` / `SetPartyLeader` | `(.., 5)`, `(-1)` (after `SurrenderToEnemies`) |
| `unk_m44ac/test3` | 2557 / 2673 | `ChangeToStandardFaction` x2 | `(jolee, 1)`, `(juhani, 1)` |
| `unk_m44ac/k_punk_bastjoin` | 2465 / 2511 / 2528 / 2545 / 2562 / 2687..2997 | `SetGlobalNumber` / `RemovePartyMember` / `RemoveAvailableNPC` x2 / `AddAvailableNPCByTemplate` x6 | `("G_FinalChoice", 1)`, `(4)`, `(4)`, `(5)`, `(5)`, `("p_bastilla001".."006", 15..19, 0)` |
| `unk_m44ac/k_punk_bastjoin2` | 2468 | `AddPartyMember` | `(0, Bastila)` |
| `unk_m44ac/k_punk_bastadd` | 44 | `SetGlobalNumber` | `("K_SWG_BASTILA", 13)` |
| `unk_m44ac/k_punk_bastesc` | 3092 / 3126 / 3140 | `AddJournalQuestEntry` / `SetGlobalNumber` / `PlayMovie` | `("k_swg_juhani", 120)`, `("G_FinalChoice", 2)`, `("05_8E")` |
| `unk_m44ac/k_punk_evilfinal`, `goodfinal` | 118..446 | `AdjustAlignment` x5 | (per party member, values not unrolled) |
| `unk_m44ac/k_punk_bastatt` | 2447 / 2585 / 2607 | `ChangeToStandardFaction` / `SetPlotFlag` | `(.., 1)`, party plot flags |
| `unk_m44ac/k_punk_bastcs_en` | 3153 / 3892 / 4247 / 4481.. | `SetGlobalBoolean` / `SetPartyLeader` / `ActionJumpToObject` x4 | `("UNK_BASTILA_CS", 1)`, `(var)`, `(-1)` |
| `unk_m41aa/k_punk_finale04` | 366 | `StartNewModule` | `("ebo_m41aa")` after `DestroyObject` on 7 companion tags |
| `sta_m45ad/k_psta_mlkend` | 45 / 359 | `SetGlobalBoolean` / `StartNewModule` | `("STA_MALAK_DEAD", 1)`, `("STUNT_54a", movie "51")` |
| `sta_m45ad/k_psta_ud_malak` | 6041 / 6325 | same | `("STUNT_54a", movie "54")` |
| `STUNT_55a/k_scene_start` | 93 / 188 | `AddAvailableNPCByTemplate` / `SetPartyLeader` | `("p_bastilla001", 0)`, `(-1)` |

## 5. Co-op design

**The faction flip is script-driven on the engine running the dialogue node.** Only the owner
(host) executes `test3`/`k_punk_bast_ud2` (dialogue-consequences.md §2 assumption; NEEDS-LIVE).
`ChangeToStandardFaction` writes the creature's faction on the host's copy of Jolee/Juhani only.
The mirror then does the wrong thing on the joiner's screen: the joiner's Jolee/Juhani are
presence-mirrored bodies pinned to the player faction and held Neutral by
`PartyPresenceController.ApplyCompanionFaction` (`Odyssey.SetCreatureFaction`,
`FactionPlayer = 0`, `FactionNeutralId = 5`). The party wire (`PartyMemberState`: slot,
position, facing, animation, health, one-shot, equipped gear) carries no faction, so the joiner
can neither fight the hostile Jolee nor see them attack. And `RemovePartyMember(4/5)` +
`RemoveAvailableNPC` drop them from the host's roster while the joiner still holds them
(`PartyRoster.AvailableMask` does propagate a cleared bit if the roster controller acts on
removals; `PartyRosterController` "declines to re-add" but its handling of a peer-side
CLEAR is NEEDS-LIVE).

Ownership: **the host's engine owns the summit** (it is the dialogue owner and the fight is a
host-owned enemy fight for the four hostile bodies; `enemy-presence-sync.md` mirrors hostile
creatures to the joiner, so a hostile Jolee is a normal mirrored enemy IF his `FactionID`
reaches the enemy channel as hostile). The joiner keeps its own PC and fights normally.

Design, in increasing scope:

1. **Do nothing new for the fight, make the mirror release the flipped companions.** On the
   host, a companion whose faction has left `Player`/`Neutral` (read via `GetStandardFaction`,
   routine 713 handler `0x00545D80`, or the creature's faction id) is published as an
   ENEMY through the existing enemy census instead of a party member, and dropped from
   `PartyRoster`; the joiner's roster controller then removes the slot. Uses existing wires
   (enemy census, roster mask); new is only the host-side "flipped companion" classification.
   **M**.
2. **Or a tiny new field**: add `Faction` (1 byte) to `PartyMemberState` (protocol bump), so
   `ApplyCompanionFaction` on the joiner stops forcing the player faction and applies the
   host's value. **M** (wire + `PartyPresenceController` + tests). Simpler than (1) for a
   pure mirror, but the joiner's engine then runs the AI of a hostile Jolee itself unless
   held (double-fire risk).
3. **Bastila's join**: `AddAvailableNPCByTemplate("p_bastilla001..006", slot 15..19)` uses
   slots 15..19, outside `PartyRosterTemplateTable`'s 0..8 (the table is documented as nine
   slots): the roster mask is `ushort AvailableMask`, so bits 15..19 exceed 16 bits for 16..19.
   This is a real gap: **the Sith Bastila roster slot cannot be represented on the wire**.
   **S-M**: extend the mask (or translate 15..19 back to slot 0 with a blueprint id).
4. **The ending** is the STUNT chain: blocker #6's replay design. Dark-specific data to carry:
   `G_FinalChoice`, `K_SWG_BASTILA`, `STA_MALAK_DEAD` (ordinary globals, the merge covers
   them) and movies `50/51/54/55/56` (movie relay).

## 6. Missing surfaces (RE targets)

1. Host-side faction watch on companions: `CSWVirtualMachineCommands::
   ExecuteCommandChangeToStandardFaction 0x0052F2F0` (routine 412; shares its handler with
   173 `ChangeFaction`) as a hook or a per-companion faction poll. Hook splice shape as
   `RemovePartyMemberHook` (leviathan.md §5.3 #4). **M, RE-lite** (prologue verify).
2. Wire: `PartyMemberState.Faction` or an enemy-channel promotion (design 1/2).
3. Roster wire width for slots 15..19 (`PartyRoster.AvailableMask` is a `ushort`; the slots
   `AddAvailableNPCByTemplate` accepts are wider than 0..8). Not investigated whether
   `CSWPartyTable` has storage beyond `+0x30 int[9]` for them (the table layout in
   `party-reconciliation.md` documents nine): **an unresolved engine question** -- slot 15..19
   writes may land in adjacent fields; `PartyRosterTemplateTable`'s header claims nine slots.
4. The Bastila fight's `Min1HP` / plot flags on the joiner side: not investigated.

`SoloSegmentCatalogue` was not changed: the summit has no solo-gated segment (Jolee/Juhani
stay in the room; the party is not emptied), the two-call `RemovePartyMember(4)/(5)` in
`k_punk_bast_ud2`/`bastjoin` is below any sensible burst threshold's intent.

## 7. Rig recipe

1. `warp unk_m44ac` on the HOST (both land). `partystate`: roster with Jolee (4) / Juhani (5)
   via `recruit 4 jolee`, `recruit 5 juhani` (the dev verb, `party-reconciliation.md` round 75).
2. `setglobal G_FinalChoice 0`, `setglobal UNK_BASTILA_CS 0`. Walk into trigger `unk44_bastcs`
   `(86.9, 66.0)` with `walkto` (a `tp` skips OnEnter), or `startdialog <Bastilae id>
   unk44_evilbast` and `selectreply` down the dark path (reply text R65 "rule the galaxy").
3. Watch: host plugin log for `ChangeToStandardFaction` outcomes (no hook exists; read the
   creature faction with `blockpoke` resolve), `partystate` companion count dropping 2 -> 1..0,
   the joiner's roster (does it drop Jolee/Juhani), and whether the joiner's mirrored Jolee
   attacks or stays neutral. Do not use `firescript`.
