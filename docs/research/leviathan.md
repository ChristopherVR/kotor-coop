# The Leviathan — capture, brig, rescuer, Saul, the reveal, the escape — in a co-op session (2026-09-18)

Review blocker #4 ([review-seamless-2026-09-18.md](../review-seamless-2026-09-18.md) §1) and
roadmap item 38. The review's own summary of the risk was: "PC removed from party, one companion
under script control, Bastila removed, `m12ab` turret, Saul's `GiveItem`. Both engines run the
module scripts with their own PC, so two independent brig sequences run; `PartyFreezeRule` only
pauses mirroring." This pass reads every compiled script of the sequence out of the real GOG
install and says exactly which engine surface each beat touches, what the shipped mod already
covers, and what is missing.

RESEARCH ONLY. No code changed, no game launched, no rig script run.

Tag key: **VERIFIED** = read directly out of the game's compiled bytecode / GFF data or the
GOG `swkotor.exe` (v1.0.3, no ASLR) · **INFERRED** = a deduction from that evidence ·
**NEEDS-LIVE** = must be confirmed on the two-instance rig.

Read alongside [star-forge-finale.md](star-forge-finale.md) (the model this doc follows: a
script-level map of a forced-solo beat, then `solo_segment_spectate` §8/§11/§12),
[party-reconciliation.md](party-reconciliation.md) (`PartyFreezeRule`, party table `+0xF0`),
[client-control-lock.md](client-control-lock.md) / [control-return.md](control-return.md)
(who the input drives), [cinematics.md](cinematics.md) §4.3 (the STUNT gate + movie carry),
[minigames.md](minigames.md) §9.2 (`m12ab`), [dialogue-sync.md](dialogue-sync.md) (adopt /
node sync) and [dialogue-consequences.md](dialogue-consequences.md) §1.1 (the routine-table
method reused here).

---

## 1. Method

* **Routine table.** `CSWVirtualMachineCommands::InitializeCommands 0x0054C960` fills the VM
  dispatch table with `mov [reg+id*4], handler` stores — the method `dialogue-consequences.md`
  §1.1 and `star-forge-finale.md` §1 already used for a handful of ids. This pass walked the
  whole store run once (the function tail-jumps at `0x0054E9C8` into a second store run at
  `0x005CD2C0` that fills 582..637 and friends — the earlier passes never needed it) and
  resolved **770 of 772 slots** against `tools/kotor1_0_3.db`'s handler names. Every routine
  name in this doc comes from that table; the four global-variable ids keep the project's own
  verified names (`NwscriptActions.cs`) where the DB names only the shared handler.
  New ids this doc leans on, all **VERIFIED** by the same `offset/4` arithmetic:

  | id | routine | handler |
  | --- | --- | --- |
  | 11 | `SwitchPlayerCharacter` | `0x00544910` |
  | 13 | `SetPartyLeader` | `0x00545A70` |
  | 58 | `SetPlayerRestrictMode` (DB: `SetRestrictMode`) | `0x00543450` |
  | 162 | `SetCommandable` | `0x005426C0` |
  | 271 | `GiveItem` | `0x0053E830` |
  | 412 | `ChangeToStandardFaction` (shares handler `0x0052F2F0` with 173 `ChangeFaction`) | `0x0052F2F0` |
  | 573 / 574 / 575 | `RemoveFromParty` / `AddPartyMember` / `RemovePartyMember` | `0x00541BA0` / `0x0052DE70` / `0x00541C00` |
  | 695 / 697 / 698 / 699 | `RemoveAvailableNPC` / `AddAvailableNPCByTemplate` / `SpawnAvailableNPC` / `IsNPCPartyMember` | — |
  | 712 | `ShowPartySelectionGUI` | `0x005438B0` |
  | 716 | `SetMinOneHP` | `0x00545F90` |
  | 734 | `SaveNPCState` | `0x005421A0` |
  | 509 / 733 | `StartNewModule` / `PlayMovie` | `0x00544390` / `0x00540ED0` |

* **Scripts.** A Python port of `NcsReader.cs` (scratchpad only, same precedent as
  `minigames.md`'s `ncsdump2`) disassembled every `.ncs` in `STUNT_16`, `lev_m40aa/ab/ac/ad`,
  `ebo_m40aa`, `ebo_m40ad`, `STUNT_18`, `STUNT_31b`, `M12ab` and the BIF scripts they name.
  Argument literals are the `CONST` run immediately preceding each `ACTION` — the same
  "preceding constants" idiom `FindActionCalls` uses, extended to ints and floats. NWScript
  pushes arguments last-first, so a listed const run reads right-to-left as first-to-last
  argument (checked against `StartNewModule('',…,'lev40_sw40ab13','lev_m40ab')`, whose FIRST
  argument is the module name).
* **Wiring.** `tools/kotorfiles.py`'s GFF reader over each module's `.ifo`/`.are`/`.git` and
  every `.utt`/`.utd`/`.utp`/`.utc`/`.dlg` template, so each script below is tied to the
  trigger, door, placeable or dialogue node that fires it, not associated by name (the trap
  `star-forge-finale.md` §9 documents).

---

## 2. The module chain

```
ebo_m12aa (galaxy map)
   │ k_sup_galaxymap (BIF) "ST_PlayLevCaptureStunt" branch
   ▼
STUNT_16  (area stunt_ebolev, "pulled out of hyperspace")            movie 08 (k_inc_stunt)
   │ k_ren_levcapture (BIF): StartNewModule("ebo_m40aa", movie "17")
   ▼
ebo_m40aa (Ebon Hawk under tow; lev40_carth.dlg = "who rescues us?")
   │ k_pebo_jumplev2: StartNewModule("lev_m40aa", "lev40_wptorturecs")
   ▼
lev_m40aa  Leviathan - Prison Block   (torture, rescuer alone, the freeing)
   │ elevator dialogue plev_elev_dlg: k_plev_goto40ab / goto40ac
   ├──▶ lev_m40ab  Leviathan - Command Deck  ──door lev40_sw40ab21/24──▶ lev_m40ad  Leviathan - Bridge (Saul)
   └──▶ lev_m40ac  Leviathan - Hangar   (Malak; Bastila stays behind)
              │ R58 k_plev_gostunt: StartNewModule("stunt_31b", movie "31a")
              ▼
         STUNT_31b (area m44ad: the Revan reveal)  ── k_stu_jumplev: StartNewModule("lev_m40ac","lev40_wpconfa", movie "05r")
              ▼ (back in lev_m40ac) hangar door / k_plev_hackebon / k_plev_goebon
         ebo_m40ad (Ebon Hawk, post-escape) ── k_ren_levescape (BIF) ──▶ M12ab (turret, movies 17a+11a)
              ▲                                                            │ k_pebo_mgheart: StartNewModule("ebo_m40ad", movie "11b")
              └────────────────────────────────────────────────────────────┘
   next galaxy-map departure: k_sup_galaxymap "ST_PlayBastilaTorture" ──▶ STUNT_18 (area stunt_unkhall) ──▶ the chosen planet
```

Every arrow is a `StartNewModule` (routine 509) read out of the named script — **VERIFIED**,
with two exceptions marked below. Real area names from each `.are`'s `Name` strref:

| Module | Area | `OnEnter` | `OnHeartbeat` | `Mod_OnModLoad` | Unescapable |
| --- | --- | --- | --- | --- | --- |
| `STUNT_16` | Untitled (`stunt_ebolev`) | `k_scene_start` | — | — | — |
| `ebo_m40aa` | Ebon Hawk (`m12aa`) | — | — | `k_pebn_pophawk` | — |
| `lev_m40aa` | Leviathan - Prison Block | `k_plev_40aa_en` | — | — | 1 |
| `lev_m40ab` | Leviathan - Command Deck | `k_plev_40ab_en` | — | — | 1 |
| `lev_m40ac` | Leviathan - Hangar | `lev40_area_enter` | `k_plev_40ac_hb` | — | 1 |
| `lev_m40ad` | Leviathan - Bridge | `k_plev_40ad_en` | `k_plev_space_hb` | — | 1 |
| `STUNT_31b` | Untitled (`m44ad`) | `k_scene_start` | — | — | — |
| `ebo_m40ad` | Ebon Hawk (`m12aa`) | — | — | `k_pebn_pophawk` | — |
| `M12ab` | Ebon Hawk - Turret | `k_pebo_skybox` | — | `k_pebo_mgload` | — |
| `STUNT_18` | Untitled (`stunt_unkhall`) | `k_scene_start` | — | — | — |

The two INFERRED links: (a) `k_sup_galaxymap`'s branch predicate. The script reads
`K_CURRENT_PLANET`, `K_STAR_MAP`, `K_CAPTURED_LEV`, `K_KOTOR_MASTER`, `K_KALO_BANDON` and
prints `"I am running ST_PlayLevCaptureStunt()"` on one branch and
`"I am running ST_PlayBastilaTorture()"` on another (both **VERIFIED** string constants at
offsets 2266 / 2463); which combination of those globals selects each branch was not unrolled.
(b) `k_ren_levescape` is the BIF script that names both `m12ab` and movie `11a` (the string
scan found six such scripts; this is the one named "levescape"), matching
`galaxy-map-and-party-select.md` §3C's `ST_PlayLeviathanEscape01`. Who calls it from
`ebo_m40ad` was not traced — `k_pebn_pophawk` is the only `Mod_OnModLoad` and is the likely
caller.

---

## 3. Beat by beat: what each script does to the party

### 3.1 The capture (`STUNT_16` → `ebo_m40aa`)

`STUNT_16/k_scene_start` (**VERIFIED**): `AddAvailableNPCByTemplate("p_carth", 2)`,
`AddAvailableNPCByTemplate("p_bastilla", 0)`, `SetPartyLeader(-1)`, then
`ActionStartConversation` against the `CutStart` object. A pure cutscene room, structurally
identical to `STUNT_00` — the follower's stunt gate (`cinematics.md` §4.3) refuses it, the
queued movie `17` is carried to the follower's eventual warp.

`ebo_m40aa` is the Ebon Hawk with the party standing around. `k_pebo_40aa_en` (present in the
module's `_s.rim`, not wired as an `On*` field — INFERRED to be run from `k_pebn_pophawk`)
sets `K_AT_EBON_HAWK`, walks every companion to a `pebn_<tag>` waypoint, and in a loop over
slots 0..8 (`IsNPCPartyMember(slot)` → `RemovePartyMember(slot)`, then `SpawnAvailableNPC`)
**empties the active party** so every companion is a free-standing body for the scene.
Trigger `ebo40_carthtlk` → `k_pebo_carthtlk` → `lev40_carth.dlg` (38 entries): Carth and
Bastila argue, then entry 9 ("we better pick someone quick") offers one reply per companion
who is available (`k_ebo_hkavail`, `k_con_juhanilive`, `k_ptar_joleechk`, `k_ebo_zalavi`
conditions). The reply's follow-up entry runs `k_plev_escplot1..6`, each a one-liner
`SetGlobalNumber("LEV_ESCAPE", n)`; `k_pebo_jumplev2` then `StartNewModule("lev_m40aa",
"lev40_wptorturecs")`. **VERIFIED.** The `LEV_ESCAPE` → rescuer map is read straight out of
`k_plev_transform`'s switch (§3.3): 1 Mission (slot 6), 2 T3-M4 (7), 3 Jolee (4), 4 Canderous
(1), 5 Juhani (5), 6 HK-47 (3); `k_plev_escsetup` defaults an unset `LEV_ESCAPE` to 6.

**Co-op reading.** The choice is a dialogue reply; `dialogue-sync.md`'s token-owner rule means
one player picks and the other watches, and `LEV_ESCAPE` is an ordinary global that
`quest-sync.md`'s merge carries. Nothing new here — except that the joiner's own engine, if it
followed into `ebo_m40aa`, ALSO ran `k_pebo_40aa_en`'s roster strip (its own mirrored roster;
`PartyRosterController` reconciles rosters by slot, and the host's is empty too, so the two
agree). **NEEDS-LIVE**, never watched.

### 3.2 The prison block on arrival (`lev_m40aa`): the torture

`k_plev_40aa_en` (area `OnEnter`): `SetGlobalNumber("LEV_LEVEL", 1)`, `LEV_SPIKES`, a
`DestroyObject`. Nothing party-shaped. The work is in trigger `lev40_escpcs` at
`(20.3, 45.0, 9.0)`, i.e. ON the module entry point `(21.5, 44.0, 9.1)`, whose
`ScriptOnEnter = k_plev_escsetup` (**VERIFIED** GFF wiring, also reachable from
`lev40_carth.dlg` reply 43). `k_plev_escsetup` (3642 bytes), gated on
`GetIsPC(GetEnteringObject())`:

1. `SetGlobalFadeOut`, `SetCommandable(TRUE, GetPC())`.
2. `CreateObject(PLACEABLE=64, "lev40_tortcage", GetLocation(lev40_wptorturecs))`; if
   `LEV_ESCAPE == 0` → `SetGlobalNumber("LEV_ESCAPE", 6)`; `ActionJumpToObject(lev40_wptorturecs)`
   assigned to the PC.
3. Per `LEV_ESCAPE`, the rescuer's own cell dressing: `CreateObject("lev_sithtroop004")` at
   `lev40_wpmisguard` (Mission), `lev40_t3tech`/`lev40_shopdroid` (T3), `lev40_idletech` +
   `ChangeToStandardFaction`, `lev_sithtroop003` at `lev40_wpjolguard` (Jolee),
   `CreateObject("p_cand")` at `lev40_spcand` jumped to `lev40_spcandcell` (Canderous),
   `lev40_jutech` (Juhani), `lev40_hktech0` (HK-47).
4. Ten `GetIsObjectValid` + `GiveItem` pairs against the objects tagged `bastila`, `carth` and
   `lev40_ptylocker`: **the PC's and both companions' equipment is moved into the party locker
   placeable** (this is the review's "Saul's `GiveItem`" — it is the locker, not Saul).
5. `ActionStartConversation` with the object tagged `sithcommander` (`lev40_sithcomman.dlg`
   → `k_plev_tortscene`), `ExecuteScript("k_plev_goescape")`.

`lev40_saul403.dlg` (the torture, Saul + Bastila + Carth, 122 entries) runs
`k_plev_torture`/`k_plev_tortpc` (a `lev40_tortsnd` cue and a temporary
`ApplyEffectToObject(DURATION_TEMPORARY=1, …)` on the victim — a damage/visual, no HP pin was
found), `k_plev_tortbettr`/`k_plev_tortbetfa` (`Lev_TortureBetray` 1/0), `k_plev_journals`,
`k_plev_saulover`, `k_plev_tortfade` and — on the exit node — **`k_plev_transfrm1`** (§3.3).
**VERIFIED** (GFF `Script` fields). Neither `SetMinOneHP` nor `RemovePartyMember` occurs
anywhere in `lev_m40aa`'s 181 scripts; the "PC removed from the party" of the review brief is
not a roster operation at all — see §3.3.

### 3.3 The rescuer: `SwitchPlayerCharacter`, not `SetPartyLeader`

Two scripts hand control to the rescuer, both dialogue-node actions, both **VERIFIED** by full
disassembly:

* `k_plev_transform` (`lev40_transform.dlg`, the `lev40_transformer` placeable's conversation,
  started by `k_plev_goescape`): `GetGlobalNumber("lev_escape")` → a six-way switch that pushes
  the NPC slot (`1→6, 2→7, 3→4, 4→1, 5→5, 6→3`) and calls **`SwitchPlayerCharacter(slot)`**
  (routine 11, offset 945), wrapped in `ActionPauseConversation` / `ActionWait(3.0)` /
  `ActionResumeConversation`; then hands the rescuer their gear out of `lev40_patgear` /
  `lev40_ptylocker` (ten `GiveItem`s).
* `k_plev_transfrm1` (`lev40_saul403.dlg` and `lev40_transform.dlg`): the same switch,
  preceded by `ActionJumpToObject(lev40_wpescape1..6)` on the PC (the rescuer's cell waypoint)
  and a `SetGlobalFadeIn`, then `SwitchPlayerCharacter(slot)` at offset 1639 and the same gear
  hand-over.
* `k_plev_switchply` (3720 bytes) does the same with six `CreateObject(p_*)` calls — **no
  template, trigger or dialogue in any module references it** (string sweep of every
  `.dlg`/`.utt`/`.utd`/`.utp`/`.utc` in `modules/*.rim`): dead content, listed so nobody
  chases it.

**What `SwitchPlayerCharacter` does in the engine** (`CSWVirtualMachineCommands::
ExecuteCommandSwitchPlayerCharacter 0x00544910` → `CSWPartyTable::SwitchPlayerCharacter
0x005667C0`, both **VERIFIED** by disassembly; the call sequence below is read out of the
function, its exact field semantics are INFERRED from the callee names):

* `nNPC` in 0..8 with `avail_npcs[+0x30+4n] != 0` and `pt_controlled_npc (+0xF0) == -1`:
  the current player creature is **serialised to a GFF** (`CResGFF::CreateGFFFile("UTC ",
  "V2.0")` + `CSWSCreature::SaveCreature` + `WriteGFFFile`, name from the string
  `"GAMEINPROGRESS:PC"` / `"PC"` at `0x749340`/`0x749354`), `CSWPartyTable::SaveMember`
  records it, the NPC's body is looked up (`GetNPCObject`), `CSWParty::ClearParty`,
  `SetInParty`, `CSWSCreature::AddToArea`, `SetOrientation`, and **`CSWSPlayer::SetGameObject`
  makes the NPC the player's game object**; the old PC body is faded out and removed
  (`SetFadeState`/`SetDesiredFadeState`, `KillNPCObject`, `RemoveMember`, slot ids reset to
  `0x7F000000`); finally `[table+0xF0] = nNPC`.
* `nNPC == -1` with `+0xF0 != -1` (the `k_plev_freeparty` restore, §3.4): the reverse —
  `SaveMember` the controlled NPC, `new CSWSCreature` + `LoadFromTemplate("PC")` reloads the
  serialised player, `AddGameInProgress`/`RemoveGameInProgress`, `KillNPCObject`, and
  `+0xF0 = -1`.
* `nNPC == -1` with `+0xF0 == -1`, or `nNPC == +0xF0`: returns 1, nothing happens.

Consequences the mod cares about, all **INFERRED** from the above and **NEEDS-LIVE**:

1. **The player creature's object id changes twice** (PC → rescuer at the switch, rescuer →
   a freshly loaded PC at the restore). Everything keyed on "the player creature" has to
   follow: `PlayerState` publication, `DeathController`'s cached player, the
   `coop_phantom` targeting, `IncomingDamageController.watched`, `EncounterController`,
   `CarriedPinReleaseController`. `party-reconciliation.md`'s design point 6 ("PC death swaps
   the player to a companion — everything keyed on the player creature must handle it") is the
   same hazard in a different costume; `DeathController` (`_pcControlledWhileUp`,
   `GetControlledCreaturePointer`) already tolerates a controlled-creature change, the rest
   re-read `GetPlayerCreaturePointer` each frame or cache it.
2. **`pt_controlled_npc (+0xF0) != -1` for the whole rescuer segment**, so `PartyFreezeRule`
   (`party-reconciliation.md` "roster-freeze guard") backs `PartyRosterController.Reconcile`
   and `PartyPresenceController` off for its duration on BOTH sides. Correct for the roster;
   for presence it means the joiner's copy of the rescuer's body is never driven (§5 #2).
3. The party table's own count is 0 (the rescuer is the player, not a companion) — the
   roster-drop trigger of `SoloSegmentMachine` never sees a companion count fall from ≥1
   because the count was already 0 after `ebo_m40aa`'s strip; and no `RemovePartyMember`
   burst fires in `lev_m40aa` at all (§3.2). **`SoloSegmentCatalogue` cannot detect this
   segment with either of its two triggers**, which is why no catalogue entry was added.

### 3.4 The freeing, and the party-select screen

`k_plev_freeparty` (`lev40_brigcomp.dlg` / `lev40_detcompdlg.dlg` — the two brig computers —
and trigger `lev40_freeparty.utt`), **VERIFIED**: opens `lev40_celldoor` and
`lev40_partycelldoor` (`SignalEvent` on `lev40_groundzero` and the `lev40_tortcage`),
`SetGlobalNumber("LEV_ELEVATOR", 1)`, `SetGlobalBoolean("LEV_RESCUE", 1)`, reads
`Lev_Escape`, looks up `lev40_headslot`/`bodyslot`/`rhandslot`/`lhandslot`, `SetGlobalFadeOut`,
**`SwitchPlayerCharacter(-1)`** (offsets 1605-1613: `CONST 1; NEG; ACTION 11` — the PC is
back), `ActionJumpToObject(lev40_wppcesc)` on the PC, `CreateObject("p_cand")`, and
`ActionStartConversation` with `lev40_freetalker` (`lev40_bast400.dlg`). That conversation's
`k_plev_freecs2` runs **`AddPartyMember(2)` (Carth) and `AddPartyMember(0)` (Bastila)** after
jumping the scene bodies tagged `bastila`/`carth` to `lev40_wpequiproom*` and destroying
the cell props; `k_plev_partyhawk` then `AddAvailableNPCByTemplate("p_bastilla", 0)`,
`AddAvailableNPCByTemplate("p_carth", 2)` and **`ShowPartySelectionGUI("k_plev_killbc",
nForceNPC1=2, nForceNPC2=0)`** — the party-select screen with Carth and Bastila forced in.
Its exit script `k_plev_killbc` destroys the two scene bodies, `SetCommandable`s the party,
jumps it to `lev40_wpequiproom`/`a`, and gives the locker's ten items back (`GiveItem` ×10).

The trigger `lev40_killparty` at `(48.0, 60.4)` → `k_plev_killparty` destroys any entering
creature tagged `cand`/`jolee`/`juhani`/`mission`/`zaalbar`/`hk47`/`t3m4` that is not a PC —
a scene-actor sweep for whoever the rescuer was not. Cells: `lev40_jolcel` → `k_plev_jolcel_en`,
`lev40_miscel` → `k_plev_miscel_en`, `lev40_hkcs`/`lev40_t3cs` → `k_plev_hkcs_en`/`t3cs_en`,
`lev40_juhanics` → `k_plev_juhanics`, `lev40_jeditrigger` → `k_plev_spawnjedi` — every one
reads `LEV_ESCAPE` and starts a companion-specific conversation; the cell doors `lev_cell09/
010/011` open with `lev09_starcellkey`, `lev_detdoor` with `lev40_detcard`.

**Co-op reading.** The party-select screen is the case `galaxy-map-and-party-select.md` §2.4
built `party_select_host_only` for: the host confirms, the joiner's panel is suppressed and its
roster is reconciled from the host's `PartyRosterPacket`. Whether the suppressed panel's exit
script (`k_plev_killbc`, which hands the GEAR back) runs on the joiner is exactly the open
question `PartySelectSuppressionController.cs:20-27` records — here it decides whether the
joiner's PC gets its equipment back (§5 #5).

### 3.5 Command deck and bridge (`lev_m40ab`, `lev_m40ad`): Saul

`lev_m40ab/k_plev_40ab_en`: `LEV_LEVEL = 2`, `SetLocked(lev40_bridgedoor)`, `LEV_REPOP40AB`,
`CreateObject("n_darkjedim"/"n_darkjedif")` at `lev40_wpdarkspawn`. Ordinary combat module.

`lev_m40ad` is the bridge. `k_plev_40ad_en` flips `lev40_mk1guard` and `lev40_turret` with
`ChangeToStandardFaction`; `k_plev_space_hb` (heartbeat) tracks `LEV_SPACEWALK`. The Saul
scene is `lev40_saul402.dlg` on the invisible placeable `lev40_talker` at `(265.7, 57.1, 9.1)`,
**VERIFIED** node scripts:

| node | script | party effect |
| --- | --- | --- |
| E19 | `k_plev_saulcs0` | `SetPartyLeader(...)`, jump PC/Carth/Bastila to `lev_jump_wp_*`, **`RemovePartyMember(2)`, `RemovePartyMember(0)`** (cutscene staging) |
| R32 | `k_plev_bridgeatk` (74 KB combat package) | `ChangeToStandardFaction(SaulKarath402, HOSTILE_1=1)`, same for `lev40_saulguard`/`lev40_sauljedi`, **`AddPartyMember(2)`, `AddPartyMember(0)`** — the fight is a full-party fight |
| E0 | `k_plev_playpron2` | after Saul falls: **`RemovePartyMember(2)`, `RemovePartyMember(0)`**, `SetGlobalBoolean("LEV_SAULDEAD", 1)`, `SetPartyLeader`, `SetLockOrientationInDialog` — the "Carth, finish him" scene |
| E11 | `k_plev_killsaul` | `SetPlotFlag`, **`SetMinOneHP`** on `SaulKarath402` (min-1-hp OFF, so Carth's shot kills him), `ApplyEffectToObject` |
| (post) | `k_ptar_carthjoin` | **`AddPartyMember(2)`, `AddPartyMember(0)`**, party to `lev40_party0/1/2`, `lev40_alarm`, `CreateObject("g_sithtroop04")` |

Two two-call `RemovePartyMember` bursts, each undone by an `AddPartyMember` pair within the
same conversation. With `solo_segment_any_module=1` the burst trigger WOULD begin a segment here
and end it on the refill — a false "fighting alone" toast during a cutscene, one more reason
`lev_m40ad` is not catalogued.

### 3.6 The hangar (`lev_m40ac`): Malak, the reveal, Bastila stays behind

`lev40_area_enter` sets `LEV_MALAK_SPAWN` and spawns; `k_plev_40ac_hb` counts
`LEV_AIRLOCKOPEN`. Trigger `lev40_malacs` → `k_plev_malacs_en`: `SetLocked(lev40_blastdoorb)`,
`SetPartyLeader`, `ActionStartConversation` against `lev_invis_malak_conv` (`lev40_darthmala2.dlg`
— `lev40_darthmalak.dlg` is the older twin). **VERIFIED** node scripts:

| node | script | party effect |
| --- | --- | --- |
| E21 | `k_plev_malstun` | **`RemovePartyMember(2)`, `RemovePartyMember(0)`**, `ChangeToStandardFaction(Bastila, NEUTRAL=5)`, `ChangeToStandardFaction(Carth, NEUTRAL=5)`, stun VFX — Malak freezes both companions |
| R58 | `k_plev_gostunt` | **`RemovePartyMember(0)`, `RemovePartyMember(2)`**, fade, `StartNewModule("stunt_31b", movie "31a")` |
| `STUNT_31b/k_scene_start` | | `SetPartyLeader(-1)`, `CreateObject("n_darthrevan001"/"003")` at `WP01`, conversation `19`-style; `k_stu_setdark`: `LEV_ALIGNMENT`, `AdjustAlignment`; `k_stu_jumplev`: `StartNewModule("lev_m40ac", "lev40_wpconfa", movie "05r")` |
| E0 | `k_plev_postvis` | **`AddPartyMember(0)`, `AddPartyMember(2)`** — back from the vision |
| `lev40_combatdlg` E0 | `k_plev_combtlk0` | `SetPartyLeader`, effects on party members, **`RemovePartyMember(<var>)`** + `SetCommandable(FALSE, …)` — INFERRED slot 0: Bastila breaks off to hold Malak |
| `lev40_carth.dlg` | `k_plev_freezepc` | `SetCommandable(TRUE)`, `SetLightsaberPowered(Bastila)`, the hold |
| `lev40_carth.dlg` | `k_plev_finale5` | `K_KOTOR_MASTER = 20`, `K_CAPTURED_LEV = 10`, `SetLocked(lev40_hangar)`, **`RemoveAvailableNPC(0)`, `AddPartyMember(2)`** — Bastila is gone from the roster, Carth is back |
| trigger `lev40_tpebonhawk` | `k_plev_hackebon` | **`RemovePartyMember(0)`, `RemoveAvailableNPC(0)`**, same globals, `StartNewModule("ebo_m40ad")` |
| — | `k_plev_goebon` | same globals, fade 3 s, `StartNewModule("ebo_m40ad")` |

`ebo_m40ad` is an ordinary Ebon Hawk; `k_ren_levescape` (BIF) runs the turret
(`StartNewModule("m12ab", movies "17a", "11a")`) and `M12ab/k_pebo_mgheart` returns with
`StartNewModule("ebo_m40ad", movie "11b")` (offset 1163; the same script's other exits go to
`ebo_m12aa`/`STUNT_35` for the other turret sequences). `minigames.md` §9.2 already settled
`m12ab` live: auto-park on both engines, no co-visible state exists. The next galaxy-map
departure runs `STUNT_18` (`k_scene_start`: `AddAvailableNPCByTemplate("p_bastilla", 0)`,
`SetPartyLeader(-1)`, the Bastila-torture vignette) before the chosen planet.

---

## 4. The literal calls, for checking

Every row is a `CallEngine` instruction and its preceding constants, read by the scratchpad
disassembler; `(var)` = the argument came off the stack, not a literal. Module shown once per
script.

| script | offset | routine | args |
| --- | --- | --- | --- |
| `STUNT_16/k_scene_start` | 87 / 221 / 316 / 529 | `AddAvailableNPCByTemplate` ×2 / `SetPartyLeader` / `ActionStartConversation` | `("p_carth", 2)`, `("p_bastilla", 0)`, `(-1)`, `(…"CutStart")` |
| `k_ren_levcapture` (BIF) | 72 | `StartNewModule` | `("ebo_m40aa", "", movie1 "17")` |
| `ebo_m40aa/k_pebo_40aa_en` | 701 / 3185 / 3204 / 3273 | `SetGlobalBoolean` / `IsNPCPartyMember` / `RemovePartyMember` / `SpawnAvailableNPC` | `("K_AT_EBON_HAWK", 1)`, slot loop 0..8 |
| `ebo_m40aa/k_pebo_jumplev2` | 95 | `StartNewModule` | `("lev_m40aa", "lev40_wptorturecs")` |
| `lev_m40aa/k_plev_escplot1..6` | 44 | `SetGlobalNumber` | `("LEV_ESCAPE", 1..6)` (one script each) |
| `lev_m40aa/k_plev_40aa_en` | 662 / 701 | `SetGlobalNumber` ×2 | `("LEV_LEVEL", 1)`, `("LEV_SPIKES", var)` |
| `lev_m40aa/k_plev_escsetup` | 872 / 944 / 1080 / 1145 / 1571 / 1845 / 2400 / 2476 / 2997..3590 | `SetCommandable` / `CreateObject` / `SetGlobalNumber` / `ActionJumpToObject` / `ChangeToStandardFaction` / `CreateObject` / `ActionStartConversation` / `ExecuteScript` / `GiveItem` ×10 | `(1, GetPC())`, `(64, "lev40_tortcage", loc)`, `("LEV_ESCAPE", 6)` when 0, `(lev40_wptorturecs)`, `(var, lev40_idletech)`, `("p_cand", 1, …)`, `(…"sithcommander")`, `("k_plev_goescape", …)`, locker moves |
| `lev_m40aa/k_plev_transform` | 657 / 945 | `GetGlobalNumber` / `SwitchPlayerCharacter` | `("lev_escape")` → `(6|7|4|1|5|3)` |
| `lev_m40aa/k_plev_transfrm1` | 741 / 1606 / 1639 | `GetGlobalNumber` / `ActionJumpToObject` / `SwitchPlayerCharacter` | `("lev_escape")`, `(lev40_wpescape1..6)`, `(6|7|4|1|5|3)` |
| `lev_m40aa/k_plev_freeparty` | 1139 / 1164 / 1613 / 1674 / 1757 / 1885 | `SetGlobalNumber` / `SetGlobalBoolean` / `SwitchPlayerCharacter` / `ActionJumpToObject` / `CreateObject` / `ActionStartConversation` | `("LEV_ELEVATOR", 1)`, `("LEV_RESCUE", 1)`, `(-1)`, `(lev40_wppcesc)`, `("p_cand", 1, …)`, `(…lev40_freetalker)` |
| `lev_m40aa/k_plev_freecs2` | 1238 / 1263 | `AddPartyMember` ×2 | `(2, var)`, `(0, var)` |
| `lev_m40aa/k_plev_partyhawk` | 41 / 69 / 125 | `AddAvailableNPCByTemplate` ×2 / `ShowPartySelectionGUI` | `("p_bastilla", 0)`, `("p_carth", 2)`, `("k_plev_killbc", 2, 0)` |
| `lev_m40aa/k_plev_killbc` | 733 / 776 / 863 / 1376..1969 | `DestroyObject` ×2 / `SetCommandable` / `GiveItem` ×10 | scene `bastila`/`carth`, `(1, var)`, locker returns |
| `lev_m40aa/k_plev_killparty` | 1071 | `DestroyObject` | entering `cand/jolee/juhani/mission/zaalbar/hk47/t3m4` |
| `lev_m40aa/plev_elev_dlg → k_plev_goto40ab/ac` | 92 | `StartNewModule` | `("lev_m40ab", "lev40_sw40ab13")`, `("lev_m40ac", "lev40_sw40ac29")` |
| `lev_m40ab/k_plev_40ab_en` | 666 / 737 / 838 / 987 / 1038 | `SetGlobalNumber` / `SetLocked` / `SetGlobalBoolean` / `CreateObject` ×2 | `("LEV_LEVEL", 2)`, `(lev40_bridgedoor, var)`, `("LEV_REPOP40AB", 1)`, `("n_darkjedim"/"n_darkjedif", 1, …)` |
| `lev_m40ad/k_plev_saulcs0` | 29 / 220 / 237 | `SetPartyLeader` / `RemovePartyMember` ×2 | `(var)`, `(2)`, `(0)` |
| `lev_m40ad/k_plev_bridgeatk` | 3099 / 3261 / 3448 / 3577 / 3616 | `ChangeToStandardFaction` ×3 / `AddPartyMember` ×2 | `(SaulKarath402, 1)`, `(lev40_saulguard, var)`, `(lev40_sauljedi, var)`, `(2, var)`, `(0, var)` |
| `lev_m40ad/k_plev_playpron2` | 572 / 589 / 622 / 1327 / 1352 | `RemovePartyMember` ×2 / `SetGlobalBoolean` / `SetPartyLeader` / `SetLockOrientationInDialog` | `(2)`, `(0)`, `("LEV_SAULDEAD", 1)`, `(var)`, `(var, var)` |
| `lev_m40ad/k_plev_killsaul` | 924 / 943 | `SetPlotFlag` / `SetMinOneHP` | `(SaulKarath402, var)` ×2 |
| `lev_m40ad/k_ptar_carthjoin` | 47 / 86 / 384 | `AddPartyMember` ×2 / `CreateObject` | `(2, var)`, `(0, var)`, `("g_sithtroop04", 1, …)` |
| `lev_m40ad/k_plev_40ad_en` | 771 / 930 | `ChangeToStandardFaction` ×2 | `(lev40_mk1guard, var)`, `(lev40_turret, var)` |
| `lev_m40ac/k_plev_malacs_en` | 1073 / 1086 / 1165 | `SetLocked` / `SetPartyLeader` / `ActionStartConversation` | `(lev40_blastdoorb, var)`, `(var)`, `(…lev_invis_malak_conv)` |
| `lev_m40ac/k_plev_malstun` | 800 / 817 / 842 / 861 | `RemovePartyMember` ×2 / `ChangeToStandardFaction` ×2 | `(2)`, `(0)`, `(Bastila, 5)`, `(Carth, 5)` |
| `lev_m40ac/k_plev_gostunt` | 27 / 44 / 279 | `RemovePartyMember` ×2 / `StartNewModule` | `(0)`, `(2)`, `("stunt_31b", "", movie1 "31a")` |
| `STUNT_31b/k_scene_start` | 51 / 141 / 237 / 435 | `SetPartyLeader` / `CreateObject` ×2 / `ActionStartConversation` | `(-1)`, `("n_darthrevan001"/"003", 1, WP01)`, `(…)` |
| `STUNT_31b/k_stu_setdark` | 123 / 189 | `SetGlobalNumber` / `AdjustAlignment` | `("LEV_ALIGNMENT", var)`, `(var…)` |
| `STUNT_31b/k_stu_jumplev` | 78 | `StartNewModule` | `("lev_m40ac", "lev40_wpconfa", movie1 "05r")` |
| `lev_m40ac/k_plev_postvis` | 109 / 134 | `AddPartyMember` ×2 | `(0, var)`, `(2, var)` |
| `lev_m40ac/k_plev_combtlk0` | 3301 / 3926 / 3951 | `SetPartyLeader` / `RemovePartyMember` / `SetCommandable` | `(var)`, `(var)`, `(0, var)` |
| `lev_m40ac/k_plev_finale5` | 45 / 74 / 112 / 123 / 160 | `SetGlobalNumber` ×2 / `SetLocked` / `RemoveAvailableNPC` / `AddPartyMember` | `("K_KOTOR_MASTER", 20)`, `("K_CAPTURED_LEV", 10)`, `(lev40_hangar, var)`, `(0)`, `(2, carth)` |
| `lev_m40ac/k_plev_hackebon` | 649 / 666 / 701 / 730 / 776 | `RemovePartyMember` / `RemoveAvailableNPC` / `SetGlobalNumber` ×2 / `StartNewModule` | `(0)`, `(0)`, as above, `("ebo_m40ad")` |
| `lev_m40ac/k_plev_goebon` | 45 / 74 / 109 / 171 | `SetGlobalNumber` ×2 / `SetGlobalFadeOut` / `StartNewModule` | as above, `(0,0,0,3.0,0)`, `("ebo_m40ad")` |
| `M12ab/k_pebo_mgheart` | 733 / 847 / 1054 / 1163 / 2289 | `StartNewModule` ×5 | `("ebo_m12aa", "K_MINI_GAME")`, `("ebo_m12aa", "", "11b")`, `("ebo_m12aa", "K_TARIS_DESTROYED", movie "11b")`, `("ebo_m40ad", "", movie "11b")`, `("STUNT_35", "", movie "07_2")` |
| `STUNT_18/k_scene_start` | 90 / 185 / 322 | `AddAvailableNPCByTemplate` / `SetPartyLeader` / `ActionStartConversation` | `("p_bastilla", 0)`, `(-1)`, `(…)` |

---

## 5. The co-op design

### 5.1 Ownership: the host's engine owns the brig; the joiner is the second prisoner

The engine surface that makes the rescuer segment single-player is not the roster — it is
**`CSWSPlayer::SetGameObject` inside `SwitchPlayerCharacter`**: the engine literally swaps
which creature is "the player" and serialises the real one away. Two engines each doing that
with their own PC produce two rescuers (the review's fear) only if the joiner's engine executes
`SwitchPlayerCharacter`. It is a dialogue-node action script (`k_plev_transform`/`transfrm1`),
and under `dialogue-sync.md`'s node sync the follower's conversation is driven by
`CSWSDialog::SendDialogEntry` from the mod, with the owner's engine holding the token —
`dialogue-consequences.md` §2 already relies on the follower NOT executing node grants (that is
why item grants need a wire). So the expected shape, **INFERRED, NEEDS-LIVE** as the first
thing to measure: the host (dialogue owner) switches to the rescuer; the joiner's PC stays the
joiner's player creature, sitting in the torture cage/cell where `k_plev_escsetup` jumped it,
watching.

That is also the narratively right answer — both characters were captured; one crew member
rescues the party. The joiner's role for the segment: **the second prisoner**, parked and
spectating the rescuer, released when the host's `k_plev_freeparty` opens the cell doors.

### 5.2 What existing mechanisms already cover

| Beat | Mechanism | Status |
| --- | --- | --- |
| Galaxy-map departure into `STUNT_16` | galaxy-map departure relay (`galaxymap depart`, follow_lead `any`) — the follower replays the leg; the STUNT gate refuses `STUNT_16`, the movie queue carries `17` to the follower's `ebo_m40aa` warp | shipped, live for one leg; **NEEDS-LIVE** for this leg (`cinematics.md` §4.3, `transition-smoothness.md` §11) |
| `ebo_m40aa` roster strip, `lev40_carth.dlg` choice | roster reconcile by slot (`PartyRosterController`), dialogue spectate + token owner, `LEV_ESCAPE` via the global merge | shipped |
| `k_pebo_jumplev2` / elevator `StartNewModule`s / `k_plev_gostunt` / `k_plev_goebon` | scripted-transition announce edge (`CServerExoAppInternal+0x10080`, `transition_announce_scripted`) + `follow_lead` | shipped, the announce edge NEEDS-LIVE (`transition-smoothness.md` §11.7) |
| `pt_controlled_npc != -1` during the rescuer segment | `PartyFreezeRule` stops roster reconcile and presence driving on both sides | shipped, purpose-built for "the Leviathan brig" per its own comment |
| Saul / Malak `RemovePartyMember`+`AddPartyMember` pairs | roster reconcile follows the host's `PartyRosterPacket`; the burst trigger stays inert (uncatalogued) | shipped |
| Bastila `RemoveAvailableNPC(0)` | `PartyRoster.AvailableMask` is on the wire; `PartyRosterController` already declines to re-add an unavailable slot | shipped, direction (host → joiner) NEEDS-LIVE |
| `STUNT_31b` reveal, `STUNT_18` torture | STUNT gate + movie carry (`31a`, `05r`, the torture's own) — the joiner does not watch them (review blocker #6) | design limit |
| `M12ab` turret | `MinigameModuleRule` + auto-park on both engines | live-verified `minigames.md` §9.2; the ambush never completed by two players (blocker #10) |
| Globals `LEV_*`, `K_CAPTURED_LEV`, `K_KOTOR_MASTER` | `quest-sync.md` merge | shipped |

### 5.3 What is missing — the engine surfaces, numbered

1. **A "the host's player creature is not the PC" signal on the wire, and a joiner reaction to
   it.** `PlayerState.FlagSoloSegment` is the right bit to reuse (its spectator side already
   parks input, follows a body with the camera, and stops damage credit — `star-forge-finale.md`
   §8.2, live-verified §12). A new plugin-side trigger for `SoloSegmentMachine`: the edge
   `partyTable+0xF0` (`Odyssey.IsPartyRosterFrozen`) going `-1 → n` on the local engine begins a
   segment; `n → -1` ends it. No new address: `+0xF0` is the field `PartyFreezeRule` already
   reads every frame. **S**, pure rule + one plugin edge; unit-testable exactly like
   `NoteRemovePartyMemberCall`.
2. **The rescuer's body must reach the joiner's screen.** `SoloSegmentSpectateController` points
   the camera at the owner's PHANTOM; during the brig the host's PC is in a cell and the body
   that moves is the rescuer — a roster companion whose presence mirror `PartyFreezeRule` has
   just frozen. Two options: (a) while `+0xF0 != -1`, the host publishes `PlayerState` from
   `GetControlledCreaturePointer()` (the engine's own answer to "who is the player now",
   `client-control-lock.md`) so the phantom stands where the rescuer is, and the phantom's
   appearance/gear channel carries the rescuer's appearance row; or (b) keep the phantom in the
   cell and exempt the controlled slot from the presence freeze (drive that one companion body
   from the host's `PartyMemberState` as today). (b) is smaller and keeps the "phantom = the
   other player's PC" invariant every controller assumes; the spectator camera then follows the
   mirrored companion (`SetCameraFollowServerObject` takes any object id). **M**, no RE.
3. **Player-creature identity across the two swaps.** The plugin caches the player pointer in
   several controllers (§3.3 #1). Re-resolve on the `+0xF0` edges (both directions) and treat a
   changed `GetPlayerCreaturePointer()` as a soft world-up (re-adopt the phantom target, reset
   `IncomingDamageController.watched`, `DeathController`'s cached player, the map pin). The
   `coop_phantom`'s own id is untouched. **S-M**; the exact list of caches is a grep, not RE.
4. **Suppress the joiner's own `SwitchPlayerCharacter` if the node-sync assumption fails.** If
   the live pass shows the follower's engine DOES execute `k_plev_transform`, the surface is a
   guard on `ExecuteCommandSwitchPlayerCharacter 0x00544910`: the same two-anchor mid-function
   splice shape as `RemovePartyMemberHook` (`star-forge-finale.md` §11.1), returning the
   "success" value without calling `CSWPartyTable::SwitchPlayerCharacter 0x005667C0` when a
   peer owns the segment. `nNPC` is popped by `CVirtualMachine::StackPopInteger 0x005D1000` at
   `0x0054491C` (first instruction after it: `0x0054492E mov ecx,[0x7a39fc]`, the same
   `AppManagerPtr` prologue as the RemovePartyMember hook site). **M, RE-lite**: verify the
   prologue bytes live, then the hook is a copy of an existing one.
5. **Gear return on the joiner.** `k_plev_escsetup` strips the PC's equipment into
   `lev40_ptylocker` on each engine that runs it (both do — it is a trigger on the entry
   point, `GetIsPC` passes for either PC); the return is `k_plev_killbc`, the party-select
   EXIT script on the host. Whether the joiner's suppressed party-select runs its exit script
   is `PartySelectSuppressionController`'s open question; if it does not, the joiner's gear is
   in a locker it can open by hand (`lev40_ptylocker` at `(9.6, 69.7, 9.0)`, `HasInventory`) —
   acceptable for a first pass, and the shared-inventory snapshot (`shared-inventory-v2.md`)
   would otherwise put the host's copy back. **S** to document, **M** to relay the exit.
6. **The torture on two engines.** Both engines run `lev40_escpcs`'s `k_plev_escsetup` and
   start `lev40_sithcomman.dlg` natively (a forced conversation on arrival, the
   `end_m01ab`-shaped case `playthrough-taris.md` and `dialogue-sync.md` "adopt" handle); the
   cage placeable and the per-rescuer dressing are created twice, once per engine — fine, each
   engine owns its props. **No surface needed**, but it is the first place a double-fire
   (`star-forge-finale.md` §6 #5) will show as two Saul conversations; NEEDS-LIVE.
7. **Door/lock state flowing backwards.** `lev40_celldoor`, `lev40_partycelldoor`,
   `lev40_blastdoorb`, `lev40_hangar`, `lev40_bridgedoor` are `SetLocked` by scripts
   (re-locks) and opened by the rescuer's `k_plev_freeparty`; `DoorFlags` is max-merge (a
   re-lock never reaches the joiner — review §2 "doors are monotonic"). For the brig this is
   benign: the joiner's cell opens when the host's does. **No new surface.**

### 5.4 Why `SoloSegmentCatalogue` was not extended

The catalogue's two triggers (a companion count dropping ≥1 → 0 in a listed module; a
`RemovePartyMember` burst) both miss the brig — the count is already 0 from `ebo_m40aa`'s strip
and no `RemovePartyMember` fires in `lev_m40aa` — and would misfire on the Saul/Malak cutscene
pairs in `lev_m40ad`/`lev_m40ac`. The right trigger is item 1 (`+0xF0`), a code change with
its own tests, out of this doc's research-only scope.

---

## 6. Rig recipe for the first live pass

Staging without a save that has genuinely reached the Leviathan:

1. `warp lev_m40aa` on the HOST; both land on the entry point, i.e. inside `lev40_escpcs` —
   expect `k_plev_escsetup` to fire on both engines (log: two forced conversations, the
   `sithcommander` dialogue; `partystate.ps1` reads `companions 0` if the roster was empty, or
   the locker strip if it was not). If the trigger does not fire on a warp landing (a warp is a
   teleport; `trigger-relay.md` §2 says OnEnter needs a genuine crossing), `tp` 5 m away and
   `walkto 20.3 45.0 9.0`.
2. `setglobal LEV_ESCAPE 4` (Canderous — the only rescuer `k_plev_escsetup` itself spawns as a
   `p_cand` body, so the switch has a body to swap to without earlier story state), then
   `startdialog <id of lev40_transformer> lev40_transform` on the HOST (`lev40_transformer`
   is a Static placeable at `(34.0, 56.6, 9.0)`; resolve its id with `useplaceable`'s refusal
   line as the Star Forge pass did for `sta_invis_bast_conv`). Reply through it with
   `selectreply 0` until `k_plev_transform` fires.
3. Watch the HOST plugin log for `party freeze` (the `PartyFreezeRule` edge line) and
   `partystate.ps1` for `controlled` ≠ -1; watch `where` on the host — it should now report the
   rescuer's position. Watch the JOINER: did its own `+0xF0` change (it must not); is the
   host's phantom where the rescuer walks (today: no — it stays in the cell; that is item 5.3 #2).
4. `startdialog <id of BrigComputer> lev40_detcompdlg` on the HOST and drive to
   `k_plev_freeparty`; confirm `+0xF0` returns to -1, the host's PC id CHANGED
   (`blockpoke state` player id before/after), the cell doors opened on both sides
   (`DoorFlags`), and the party-select screen appears on the host only (`party_select_host_only`).
5. Regression: `removeparty 0` bursts in `lev_m40ad` must NOT begin a solo segment with the
   shipped `solo_segment_any_module=0`.

Do not use `firescript` on the door or the trigger (`star-forge-finale.md` §8's live-confirmed
crash burst). Both instances need the rig lock (`kotor-coop-shared-rig-lock`).

---

## 7. Provenance

Read: `modules/STUNT_16*.rim`, `lev_m40aa/ab/ac/ad*.rim`, `ebo_m40aa*.rim`, `ebo_m40ad*.rim`,
`STUNT_18*.rim`, `STUNT_31b*.rim`, `M12ab*.rim` (every `.ncs`, `.ifo`, `.are`, `.git`,
`.utt`, `.utd`, `.utp`, `.utc`, `.dlg`), `chitin.key`/`scripts.bif` (`k_sup_galaxymap`,
`k_ren_levcapture`, `k_ren_levescape`), `swkotor.exe` (`0x0054C960`, `0x005CD2C0`,
`0x00544910`, `0x005667C0`, `0x00543450`, `0x00506B90`) with `tools/disasm.py` and
`tools/kotor1_0_3.db`. Tools were scratchpad-only (a Python port of `NcsReader`, a wiring
walker over `kotorfiles.py`, a call annotator over `disasm.py`), not committed, same precedent
as `minigames.md` §13 and `star-forge-finale.md` §9.6. No rig, no game launched.

## 8. Surface #1 built and its wiring proven (2026-09-19)

**Built:** `SoloSegmentMachine.NoteControlledNpc` (Protocol) begins a solo segment on the `CSWPartyTable+0xF0` edge `-1 -> slot` in a module listed by `SoloSegmentCatalogue.ControlledNpcBaseModules` (`lev_m40aa`, where both `k_plev_transform` and `k_plev_freeparty` run) and ends it on `slot -> -1` or a module change. `Step` deliberately stays out of a segment this trigger owns, because the roster count is 0 from the strip and the rescuer's own party would otherwise read as a "refill" and end it. The plugin edge is `Odyssey.IsPlayerUnderCompanionControl` in `SoloSegmentController`; the spectator side is the Star Forge one. Eight unit tests (`SoloSegmentControlledNpcTests`): edge in/out, case-insensitive module, other modules never begin (including `lev_m40ab`, `sta_m45ac`), refill does not end it, arriving mid-segment counts as an edge, module change ends it, Invalidate, and the roster triggers still work alongside.

**Live wiring check** (host + joiner, `warp lev_m40aa`, then `tools/partyctl.ps1 -Set 3` on the host to force the field the switch would set, without running the engine's switch): host `solo segment: began in lev_m40aa - a script put the player under a companion (SwitchPlayerCharacter, party table +0xF0)`; the joiner's agent `Lanik is fighting alone - spectating` within a second; `partyctl -Set -1`: host `solo segment: ended (the player is back in their own body, or the module changed)`. **What this proves:** the trigger, the wire flag and the joiner's spectate switch. **What it does not:** the real `SwitchPlayerCharacter` (the engine's player-object swap), the spectator camera and input park (a forced conversation was running on both engines at the time, and the spectator defers to a live conversation), the rescuer's body reaching the joiner (#2 below), and the gear return.

**A useful accident:** the joiner's plugin log showed `dialogue-node-pub` / `dialogue-node-drive` lines from `lev_m40aa`'s own arrival conversation, i.e. BOTH engines started the forced Saul/torture conversation natively and the joiner published node advances too. That is the double-fire the design (§5.3 #6) predicted; it works through the existing dialogue adopt path, but whether the two copies stay in step through the whole torture scene is still to be watched with a human clicking.

**Still missing (unchanged):** #2 the rescuer's body on the joiner's screen, #3 re-resolving cached player pointers across the two swaps, #4 the `SwitchPlayerCharacter` guard if the joiner's engine executes the node action, #5 gear return.
