# Class and power grants: getting a script's `AddMultiClass` onto the OTHER instance (2026-09-18)

Closes docs/review-seamless-2026-09-18.md blocker #1 (the joiner never becomes a Jedi) **statically**:
research done, class-grant relay built behind `class_grant_sync` (default OFF). **Nothing here has run
on a live process — every claim tagged NEEDS-LIVE below is unverified.**

Tags: **VERIFIED** (read from the GOG v1.0.3 `swkotor.exe` or the shipped game files) · **INFERRED** ·
**NEEDS-LIVE**.

## 1. Status: done, unfinished, not done

| | State |
| --- | --- |
| `AddMultiClass` (routine 389) | **Done, behind `class_grant_sync`.** Hook, capture, wire, apply, dev verb `addclass`. NEEDS-LIVE. |
| `GrantFeat` / `GrantSpell` / Force powers | **NOT BUILT.** See §5: no such NWScript routines exist in K1 (the routine table has no `ExecuteCommandGrantFeat`/`GrantSpell`); shipped scripts were not scanned for the other feat/power paths. The level-up screen grants those, per player, from the class (§6). |
| Scan of every other feat/power script | Not done. Only `AddMultiClass` was scanned (§4). |
| Full-suite test run | Not run by this agent (coordinator runs it once). |

## 2. Addresses found (GOG swkotor.exe, sha-matched `tools/kotor1_0_3.db`)

| Item | Address | Evidence |
| --- | --- | --- |
| `ExecuteCommandAddMultiClass` | `0x0052DDC0` | `tools/q.py func AddMultiClass`; `tools/xrefs.py 0x52DDC0` finds one address literal at `0x0054CBF8`, in the routine-table population code |
| Routine number | **389** | that site is `mov dword [eax+0x614], 0x52ddc0`; `0x614 / 4 = 389`. Method self-validates against `CreateObject`=243, `CreateItemOnObject`=31 (dialogue-consequences.md §1) |
| `CSWSCreatureStats::AddClass` | `0x005A5D10` | `tools/xrefs.py`: exactly one call site, `0x0052DE31`, inside the handler above |
| `CSWPartyTable::DistributeExperience` | `0x005653A0` | called by the handler at `0x0052DE5A` with the XP gap `CalculateExpNeededForNextLevel (0x005A5490) - current xp` |
| Hook splice site | `0x0052DDF6` (resume `0x0052DDFC`) | first instruction after both stack pops succeed; bytes `8B 15 FC 39 7A 00` (`mov edx,[0x7a39fc]`); anchor = the 11-byte "pop failed" exit before it |
| Related, not hooked | `ShowLevelUpGUI` routine 265 (`0x00543870`), `GetHitDice` 166, `GiveXPToCreature` 393, `GetPC` 548 | `tools/vmtable` dump of the same table (501 entries recovered by scanning `mov [reg+off], imm32` sites `0x0054C9AE..0x0054E9BD`) |
| `CSWSCreatureStats::LevelUp` | `0x005AABF0` | callers `0x005AF627` (`SetExperience`), `0x005AFA0A` (`ValidateLevelUp`); feats added via `CSWSCreatureStats::AddFeat 0x005AA810` (callers: `LevelUp`, `ReadStatsFromGff`, `AutoLevelup`, debug menu) and spells via `AddKnownSpell 0x005AA9B0` (callers: `ReadSpellsFromGff`, debug menu) |

## 3. What the handler writes (disassembly of `0x0052DDC0`)

1. `StackPopInteger` -> class (`[esp+4]`), `StackPopObject` -> target (`[esp]`); either failing returns `0xFFFFF82F`.
2. `GetCreatureByGameObjectID(target)`; null returns 0 quietly.
3. `stats = creature+0xA74`; remembers `stats+0x68` (experience) -> `CalculateExpNeededForNextLevel()`.
4. `AddClass(class, 0)`: `if (class_count < 2) class_count++` (`stats+0x89`); writes `classes[count-1].class_id` (`stats+0x8C + idx*0x28 + 0x1B`) and `.level = 0` (`+0x1C`); then calls `CSWSCreature::UpdatePureGoodEvilPowers`.
5. If the party table exists, `DistributeExperience(xpGap)` - gives the whole party exactly enough XP to reach the next level and lights the level-up flag.

So `AddMultiClass` does **not** grant feats, powers or a level; it opens the second class slot at level 0 and hands out one level's XP. **Feats and powers come from the level-up screen** the script then opens (`ShowLevelUpGUI`, `k_pdan_makejedi` ends with it). This is why a raw poke of `class_count` would be wrong (it skips `UpdatePureGoodEvilPowers` and the XP gap): the relay calls the VM handler itself.

## 4. Shipped scripts that call it (scan of every `.ncs` in `modules/*.rim` and the BIFs, ACTION `05 00 01 85 02`)

Only **one** script calls it: `danm13_s.rim:k_pdan_makejedi` (offsets 3810/3859/3908), plus a dead `test` script in the same rim. Called by `dan13_dorak.dlg` ReplyList[105]. The three calls are `AddMultiClass(3|5|4, GetPC())` (Guardian, Sentinel, Consular), selected by `DAN_PATH_STATE` (`GetGlobalNumber`, routine 580). The script then runs `GiveXPToCreature`, `NoClicksFor`, and `ShowLevelUpGUI`. No other module or BIF script calls `AddMultiClass`.

## 5. Grants that do NOT go through `AddMultiClass`

Recovering the routine table found no `GrantFeat`/`GrantSpell` VM commands (only `GetHasFeat` 0x00539170, `GetHasSpell` 0x00539440 and effect variants). Feats/powers enter a K1 character through `LevelUp` (§3 table) and the item/effect system. Unscanned: item property feats, `ReadStatsFromGff` (blueprint load). If a scripted power grant exists it is not in the routines this pass found; **the joiner's feats/powers are therefore not synced by this work** - they come from the joiner's own level-up.

## 6. Design as built

- **Capture (driver):** `AddMultiClassHook` (plugin) splices `0x0052DDF6`, copies `(target id, class)` into an 8-entry ring, no engine calls. Installed only when `class_grant_sync=1`; refuses if either byte check fails. Push discipline: both arguments are staged in `eax`/`edx` from the entry `esp` before `pushad`, then pushed once each.
- **Decide:** `ClassGrantController` (frame hook) drains the ring; `ClassGrantCaptureRule` publishes only a call whose target is the local player and that is not a replay.
- **Wire:** shared block region `0x4C20..0x4C38` (plugin -> agent serial/class; agent -> plugin request serial/class; applied + status ack). Agent `ClassGrantPump` announces via `ClassGrantPacket` (78) / `ClassGrantAckPacket` (79), retransmit-until-ack (`ClassGrantSender`, modelled on `DialogueGrantSender`), host relays under the origin's true (origin, sequence); receiver filters its own echo.
- **Apply (peer):** `ClassGrantApplyRule` reads the local class list (`Odyssey.TryReadLocalClassList`) and refuses `AlreadyHas` / `SlotFull` / bad class; otherwise `Odyssey.AddMultiClass` pushes the args and calls the SAME handler (with `NextGrantIsReplay` armed so the capture is not republished). Never a raw stats poke.
- **Receiver timing (`ClassGrantReplayRule`):** waits out its own load and conversation; no same-module test or expiry (a class is durable).
- **Config:** `class_grant_sync` read by `PluginConfig.ReadBool(…, false)`, `CoopConfig.ClassGrantSync` (parse and default false) and `AgentComposition` (`?? false`). Not a digest-negotiated key (both peers should set it).
- **Versions:** protocol `52 -> 53`; shared block `67 -> 68` (`PluginHost` `_block->Version` and `KotorGameBridge.PluginSignature` byte `0x43 -> 0x44`); `TotalSize` unchanged (`0x4D00`).

### Hazards (known, unresolved)

- **Level ordering:** the joiner needs XP (already max-merged, progression-sync.md) to level; `AddMultiClass` on the peer also calls `DistributeExperience`, granting one level's gap on the peer *in addition* to the max-merge - harmless upward but can overshoot by one level. INFERRED.
- **Class-2 slot:** `AddClass` silently drops a third class; the apply rule refuses instead.
- **`GetLevelByClass` gates:** satisfied only once the peer actually levels in the new class at the level-up screen; the relay only opens the slot at level 0.
- **Alignment / saber feats:** the light saber build and Jedi feats are granted at level-up (not synced); the peer must spend the level-up screen themselves. `UpdatePureGoodEvilPowers` is rebuilt by the handler.
- **Joiner not on Dantooine:** the grant applies whenever the receiver is not loading or in a conversation; dialogue gates for Jedi content may depend on globals already merged.
- **Two-player choice:** if both players make the choice (each has a Dorak conversation), each character gets its own class and each grant is relayed to the other, refused there as `AlreadyHas`/`SlotFull`.

## 7. NEEDS-LIVE checklist

Set `class_grant_sync = 1` in both installs' `coop.ini`; host + joiner in a Soldier/Scout/Scoundrel save.

1. Start both; plugin log has `class-grant: AddMultiClass captures at 0x52DDF6` (a `NOT installed` line means a byte check failed: stop).
2. On the host console: `addclass 3` (stands in for Dorak). Console answer: `classes 1:[0@1,-1@0] -> 2:[0@1,3@0] (hook armed, captures 1, published 1)`.
3. Joiner plugin log: `class-grant: applied a peer's class 3 to the local player`; agent log `this character is now a Jedi Guardian`. Read back with `resolve party` and the joiner's own `addclass 3` (it should answer `AlreadyHas`, status refused) to read the class list.
4. Retransmit: repeat `addclass 3` on the host; the host's second attempt is refused locally (`AlreadyHas`); no packet.
5. Real path: warp both to `danm13`, host finishes Dorak's training (`dan13_dorak`); joiner must gain the class; open the joiner's level-up screen and verify the new class appears.
6. Regression: the joiner's own log must show `replayed a peer's class 3 ... (not republished)` and the host must NOT receive a second grant.
7. Check no crash on a rapid `addclass` burst and that the peer applies exactly once.

## 7a. Real Dantooine recipe (static resource evidence; NEEDS-LIVE)

The following is a recipe for the shipped path, not a live result. It was extracted from the
GOG resources `modules\\danm13_s.rim` and `dialog.tlk` on 2026-09-22. The module resref is
`danm13`; the placed NPC tags and conversations are both `dan13_dorak` and `dan13_zhar`.

First reach `dan13_zhar.dlg` EntryList **61**, whose journal marker is `dan_trials` entry 20
(the line beginning “Blue is the color of the Jedi Guardian”). Its entry script is
`k_pdan_saber06`, which sets `DAN_DORAK_DONE`. End that conversation, then talk to Dorak.
`dan13_dorak.dlg` StartingList entry **0** points to EntryList **57**, gated by
`k_pdan_saber01` reading `DAN_DORAK_DONE`. Entry 57 runs `k_pdan_saber02`, resets the three
score globals, and sets `DAN_SABER_DONE`.

For the currently observed council node, `dan13_zhar.dlg` EntryList **74** is the
conversation node that introduces the council members. Its `RepliesList` is already in
display order; the visible ordinal and the shipped GFF reply member are:

| Visible ordinal | Visible text | GFF reply member | Next entry |
| ---: | --- | ---: | ---: |
| 0 | What do you want from me? | 108 | 83 |
| 1 | So you've drawn me into your little trap, Bastila. What now? | 107 | 82 |
| 2 | Isn't the Jedi Council on Coruscant? | 106 | 81 |
| 3 | I've got some questions for all of you. | 96 | 75 |

These IDs are resource member IDs, not UI ordinals. The direct training branch is ordinal 0
to Entry 83, then Entry 83's first visible reply (ordinal 0, GFF reply 129) to Entry 84.
Entry 84 is spoken by the placed `dan13_vrook` creature and its next unlabelled reply has
two gender-selected targets: Entry 86 (`Speaker=Bastila`, “this man”) and Entry 85
(`Speaker=Bastila`, “this woman”). The `Bastila` speaker is the party companion tag, while
the council speakers use their placed tags (`dan13_vrook`, `dan13_vandar`, and
`dan13_dorak`). If the conversation closes after Entry 84, record that as a live party
speaker/target-resolution issue; do not jump directly to Entry 61 or force the class grant.

Continue the class questionnaire to EntryList **76**, then click the visible choice matching
the desired path. The numbers below are GFF `ReplyList` member IDs, not the ordinal reply slot
shown by the dialogue UI; blank auto-replies and filtered conditions mean the visible slot can
have a different number.

| Visible text | GFF reply member | Next entry | Shipped entry script | Journal/item |
| --- | ---: | ---: | --- | --- |
| Green. The path of the Jedi Consular. | 102 | 77 | `k_pdan_saber13` | `dan_trials` 27; `dan13_grncryst` |
| Yellow. The path of the Jedi Sentinel. | 103 | 78 | `k_pdan_saber14` | `dan_trials` 26; `dan13_goldcryst` |
| Blue. The path of the Jedi Guardian. | 104 | 79 | `k_pdan_saber12` | `dan_trials` 25; `dan13_bluecryst` |

Entries 77-79 each link to GFF ReplyList member **105**, an unlabelled terminal reply whose
script is `k_pdan_makejedi`. That script is the real `AddMultiClass` path described above; do
not substitute `addclass`, `setglobal`, `scriptgive`, or `giveloot` when testing it. The four
question answers before Entry 76 only score the suggested path; the final three visible choices
are the deterministic class selection.

After the Dorak conversation, talk to `dan13_zhar` again. `dan13_zhar.dlg` StartingList entry
**2** points to EntryList **100**, gated by `k_pdan_saber07` reading `DAN_SABER_DONE`. Continue
the shipped chain Entry 100 -> ReplyList **130** -> Entry 101 -> ReplyList **131** -> Entry
102 -> ReplyList **132** -> Entry 103 -> ReplyList **133**. The final reply runs `k_pdan_saber16`,
which uses the engine's `CreateItemOnObject` routine to build the path-specific saber:
`g_w_lghtsbr01` (blue), `g_w_lghtsbr04` (yellow), or `g_w_lghtsbr03` (green). Again, these are
GFF member IDs, not visible UI slot numbers. Verify the joiner's class, journal/item result, and
level-up screen on the peer; this recipe has not yet been live-verified.

## 8. Not done / could not resolve statically

- Feat and Force-power grants (§1, §5).
- Whether `UpdatePureGoodEvilPowers` and `DistributeExperience` behave on a peer whose party table differs from the host's.
- A second live check that `handler(implementer, routine, 2)` accepts the argument count with a pushed `(object, int)` pair - modelled on `GiveItemToInventory`; NEEDS-LIVE.

## Live verification (2026-09-19)

Both instances, `class_grant_sync=1`: the AddMultiClass splice installs on both (`class-grant: AddMultiClass captures at 0x52DDF6`, routine 389). Host `addclass 3` (dev verb, runs the engine's own handler): `classes 1:[0@1,-1@0] -> 2:[0@1,3@0]`, then `class-grant: a script added class 3 ... published as grant #1`. Joiner: `applied a peer's class 3 to the local player (request #1); classes 1:[1,-1] -> 2:[1,3]` and `replayed ... (not republished)`; the joiner's agent logged `applying Lanik's class grant #1 (Jedi Guardian) ... (attempt 1/10)`. Capture to apply took about 100 ms and no echo came back. Not exercised: the real Dantooine conversation (Dorak's reply node), the joiner driving it, a peer already holding two classes (status 5), feats and powers. Default flipped ON.

## Feats and Force powers: closed as by-design, not as a missing channel (2026-09-19)

The 2026-09-18 report listed feat and power grants as "incomplete". Re-reading §3 with the live result in hand: `AddMultiClass` opens the second class slot at level 0 and hands out one level's XP; it never grants a feat or power (the joiner's live before/after shows only the class row change, `[1,-1] -> [1,3]`). Feats and powers enter through the level-up screen each player runs for themselves (`CSWSCreatureStats::LevelUp` 0x005AABF0 -> `AddFeat` 0x005AA810), and K1 has no `GrantFeat`/`GrantSpell` VM routine for a script to call. So the relay is complete for the Jedi path: the joiner gets the class, the shared XP (max-merge) lets them level, and they choose their own feats and powers, which is also what a single-player Jedi does. Consequences: the limitations page says so; `class_grant_sync` needs no feat channel. The one case that WOULD need one is a third-party mod that writes feats directly (K1SE's `KSE_GrantFeatArrayA`, feat array at creature-stats +0xA74 in its Steam build); that is out of scope until such a mod is in use, and the offsets would need verifying on the GOG executable first (see the K1SE note in the project memory).

## Real dialogue checks (2026-09-22)

Host-led Guardian acquisition at 05:42:23 changed the peer from Soldier only to
Soldier plus class 3 at level 0. Joiner-led Sentinel acquisition at 05:47:27 changed
the host from Soldier only to class 5 at level 0. Both directions ran the shipped
Dorak terminal reply and AddMultiClass; named saves on both instances confirm the
class rows. Eligibility was staged with DAN_DORAK_DONE, so this does not verify all
preceding council/training steps. Feat/power selection in the level-up UI remains a
separate player action.

Zhar entries 100-103 completed near the workbench, but reward correctness is still
under investigation: two live saber rows were published. The Sentinel run correctly
gave g_w_lghtsbr04 (GOLD in upcrystals.2da); the earlier recipe swapped green/yellow IDs. Do not count the earlier recipe as a proven
single-item, correct-color construction result.


## Corrected host-led saber reward (2026-09-23)

The possessed-item liveness guard compared a scanned CSWItem base to the native
CSWSObject pointer, which is embedded at base +0x10. Live native resolver and
read-only memory checks confirmed the mismatch; `6495505` corrects it. The old
06:54 saves contained no saber in either shared pool or player equipment, so the
two raw scan IDs from that run are not proof of duplicate inventory rewards.

After fresh pre-reward loads, the shipped Zhar chain ran entries 100–103 at
06:59:33–46 and granted one yellow `g_w_lghtsbr04` at 06:59:53. The joiner applied
one grant that same second and its settled pool reported actual=1, belief=1,
snapshot=1. Named saves `000031 - campaign_zhar_fixed_host` and
`000027 - campaign_zhar_fixed_joiner` each contain exactly one such row in outer
`INVENTORY.res`, StackSize 1, DELETING 0. Their player equipment does not contain
a second saber. This verifies the host-led Sentinel reward after the guard fix;
joiner-led reward and natural council prerequisites remain separate checks.

## 9. `levelup`/`stats`: making the live-test character a REAL Jedi with real Force points (2026-09-25)

Closes the dead end effect-sync.md §14.7 hit: `grantpower cure` then `cast cure 0x17F`
queued the spell but nothing landed, because "every character in the rig's saves is
the level-1 test character with no Force points... the engine's own cast action
cannot pay for the power" and "`addlevel` is a game console command, not reachable
through the mod's console channel." This section finds and wires the headless
route `addlevel` itself is not (it opens the level-up GUI screen and needs mouse
input — console-cheats.md §2's own table already says so: "a level: `addlevel` —
local; the level-up itself is the character screen, not scriptable").

Tags follow this document's own legend (§0 header): **VERIFIED(static)** = read directly off
the sha256-matched GOG v1.0.3 `swkotor.exe` with `tools/disasm.py`/`tools/xrefs.py`/`tools/q.py`,
byte-for-byte, in this pass. **NEEDS-LIVE** = the route is verified statically but has not run
in a live game. Everything in this section is static-only, per the task's constraint — nothing
here has been live-tested.

### 9.1 The route — VERIFIED(static)

`python tools/q.py func LevelUp` lists every LevelUp-adjacent symbol in the DB, including one
the task did not name but that turns out to be the actual answer:
`CSWSCreatureStats::AutoLevelup` (`0x005B27E0`). `tools/xrefs.py 0x005B27E0` finds exactly four
call/jmp sites:

| Caller | Address | What it is |
| --- | --- | --- |
| `CSWGuiInGameCharacter::DoAutoLevelup` | `0x006AEC80`, a raw `jmp 0x5b27e0` at `0x006AECC7` (a genuine tail call — no return, no stack teardown) | the character screen's own "Auto Level-Up" button handler |
| `CSWPartyTable::AutoLevelUpNPCs` | `0x00563B90`, calls at `0x00563BD2` | the "Auto Level Up Party Members" option, applied to every roster member each pass |
| `CSWPartyTable::DistributeExperience` | `0x005653A0`, calls at `0x0056543A` | the SAME auto-level-up option, applied right after an XP grant |

Both `CSWPartyTable` call sites gate the call with `CSWSCreatureStats::CanLevelUp`
(`0x005A6810`, thiscall, `this` = the stats block, boolean in `eax`) immediately before it —
`call 0x5a6810; test eax,eax; je <skip>` — then `push 1; call 0x5b27e0`. `DoAutoLevelup`
resolves the SELECTED character's stats pointer (`creature+0xA74`, the SAME `CreStats` offset
`Odyssey.Core.cs` already declares) and calls it unconditionally with `push 0`. `AutoLevelup`
itself (`__thiscall`, DB and the binary's own `ret 4` agree: one stack argument) is a ~1800-byte
function; disassembling its first ~150 instructions (`tools/disasm.py 0x5B27E0 1808`) shows the
ONE stack argument is never read to select a class — the class it operates on is computed purely
from the stats block itself:

```
0x005B281E  mov al, byte ptr [ecx + 0x89]      ; al = class_count  (StatsClassCount, 0x089)
0x005B2824  mov bl, al
0x005B2826  dec bl                              ; bl = class_count - 1  (the LAST slot)
...
0x005B283C  movzx eax, bl
0x005B283F  lea eax, [eax + eax*4]              ; eax = bl*5  (ClassInfoStride/8 = 5)
0x005B2842  mov dl, byte ptr [ecx + eax*8 + 0xa7]  ; classes[bl].class_id  (0x08C+0x1B=0xA7)
```

So **AutoLevelup always levels `classes[class_count-1]` — the LAST class slot** — never an
explicit argument. That is exactly what `addclass <id>` leaves behind (§3: it always appends),
so `addclass 3` (opening Jedi Guardian at slot 1, class_count 1→2) followed by `levelup 1`
levels the NEW Jedi class, never the base class. `levelup`'s own `classSlot|class id` argument
is therefore not passed to the engine at all; it is a **confirmation** the plugin checks against
the real `class_count-1` before calling anything, refusing (failure 4) rather than silently
leveling a class the caller did not name.

Internally `AutoLevelup` builds a `CSWLevelStats` level-up record (`CSWLevelStats::Constructor`
`0x005CB1B0`), picks recommended skills (`CSWClass::GetRecommendedSkill`/`IsSkillUseable`/
`IsSkillClassSkill`, `CSWLevelStats::SetSkillRankChange` `0x005CB120`) and a feat
(`CSWSCreatureStats::CalcLevelUpNumberFeats` `0x005A6F40`, `CSWClass::GetRecommendedFeat`
`0x005BE3C0`, `CSWSCreatureStats::CanChooseFeat` `0x005B2530`), commits
the chosen feat through the REAL `CSWSCreatureStats::AddFeat` (`0x005AA810` — the same address
§3 already names) and the record via `CSWLevelStats::AddFeat` (`0x005CB150`), then calls
`CSWSCreatureStats::ValidateLevelUp` (`0x005AF950`), which calls
`CSWSCreatureStats::LevelUp` (`0x005AABF0` — §2's table already names this as "the level-up
screen's apply path") to actually apply the record. Full disassembly of `LevelUp`
(`tools/disasm.py 0x5AABF0 2352`) finds the two writes that matter:

```
0x005AAD1C  mov ecx, dword ptr [esp + 0x1c]     ; ecx = the level-up record (LevelUp's 2nd arg)
0x005AAD20  mov eax, dword ptr [esi + 0x24]     ; eax = the owning CSWSCreature (stats+0x24)
0x005AAD23  movsx edi, word ptr [eax + 0xe0]    ; edi = current hit points
0x005AAD2A  movzx edx, byte ptr [ecx + 0x2b]    ; edx = record.hpGained
0x005AAD2E  add edx, edi
0x005AAD30  mov dword ptr [eax + 0xe0], edx     ; hit_points += hpGained
0x005AAD36  movzx ax, byte ptr [ecx + 0x2d]     ; ax = record.fpGained
0x005AAD3B  add word ptr [esi + 0x122], ax      ; force_points += fpGained
```

**⚠ Offset-naming trap avoided:** `eax` here is the CSWSCreature's own base (via
`stats+0x24`), so `[eax+0xE0]` is CSWSCreature's field at that offset — the SAME field
`Odyssey.Health.cs` already documents as "NOT +0xE0, which is the stored MAX" for the generic
`CSWSObject` read path, and as ALWAYS ZERO for a PC outside this one write. This IS that write:
the engine's OWN level-up path is what puts a nonzero value there for a player character in the
first place — matching `Odyssey.Health.cs`'s `GetEffectiveMaxHitPoints`/`MaxHitPointsRule`
comment that a PC's true max is computed from its rolled per-level hit points, not read back
from a stored field. `esi` is `LevelUp`'s own `this` (the stats block), so `[esi+0x122]` is
`CSWSCreatureStats::force_points` — the SAME field name `tools/q.py offsets
CSWSCreatureStats` already lists, with no separate stored "max" field anywhere server-side
(only `CSWCCreatureStats`/`CLastUpdateObject`, the CLIENT-side UI mirrors, cache a
`max_force_points`).

**Current Force points are therefore "filled" by the SAME write the task asked to find** — a
real level in a Force-using class raises current Force points by the level's own rolled amount,
exactly the way current hit points rise, through the engine's OWN `LevelUp` apply path. This is
not a raw poke (the write happens inside the verified call target, never touched directly by
this codebase) and it is not a "set to max" (it is additive, matching real KOTOR level-up
behaviour for hit points too) — so no separate "rest"/"heal Force" step was added; none exists
as a plain callable setter and none was needed.

**`AutoLevelup` grants no new POWER knowledge.** `tools/xrefs.py 0x005AA9B0`
(`CSWSCreatureStats::AddKnownSpell`, §14.3's grant route) finds exactly two callers:
`ReadSpellsFromGff` (loading saved data) and one inside the level-up GUI's own code
(`0x006CEC2C`) — neither reachable from `AutoLevelup`'s call graph (confirmed by grep over the
full list of functions `AutoLevelup` itself calls). This matches the live evidence §"Feats and
Force powers" (2026-09-19) already recorded for `AddMultiClass`: a level in a Force class does
not teach you a new power by itself in KOTOR (you pick powers on the level-up SCREEN, which is
exactly the GUI path this section avoids). So `grantpower` (§14) stays the separate, required
step — this section's own live recipe below keeps it that way, matching the task's own example
recipe verbatim.

### 9.2 The max hit points and max Force points readers — VERIFIED(static), reused not reinvented

For the `stats` read verb, max hit points reuses the ALREADY-VERIFIED, ALREADY-LIVE-TESTED
`Odyssey.GetEffectiveMaxHitPoints` (`Odyssey.Health.cs`) — its own comment records a live
result: "a level-1 PC with a rolled 10 and a +2 CON modifier read back 12, matching a full-health
reading exactly." No new code was written for max hit points; `stats` only calls it.

Max Force points has no such existing reader, so this pass added one: `CSWSCreature::
GetMaxForcePoints` (`0x004FD490`) — the SAME address `AutoLevelup` itself calls first
(`0x005B286C: mov ecx,[esi+0x24]; call 0x4fd490`, `esi` = the stats block, `+0x24` = its
creature backpointer), i.e. a call target the engine already exercises on every level-up, not a
fresh guess. `tools/q.py func GetMaxForcePoints` confirms `__thiscall`, 0 stack arguments (DB
and the binary's bare `ret` agree); `CSWSCreatureStats::GetMaxHitPoints` (`0x004ED310`) was
found too but its ONE stack argument's meaning was not resolved in the time this pass allowed,
so `stats` deliberately does NOT call it (calling an engine function with a guessed argument is
exactly what "verify-bytes-or-refuse" is meant to prevent) — max hit points comes from the
existing `GetEffectiveMaxHitPoints` reimplementation instead, which needs no argument.

Current hit points/Force points are plain, already-established field reads:
`Odyssey.GetCurrentHitPoints` (`CSWSObject+0xDC`, existing) and this section's new
`Odyssey.GetCurrentForcePoints` (`CSWSCreatureStats+0x122`, the same field §9.1 verified
`LevelUp` writes).

### 9.3 Known powers — VERIFIED(static), traced from `AddKnownSpell`'s own append

`grantpower`'s target (§14.3) is `CSWSCreatureStats::AddKnownSpell` (`0x005AA9B0`), which
(after its `classIndex < class_count` guard) does `lea ecx, [esi + eax*8 + 0x8c]` (the
`classes[classIndex]` ClassInfo entry — the SAME `StatsClasses`/`ClassInfoStride` arithmetic
`Odyssey.ClassGrant.cs` already uses) and calls `CSWSCreatureStats_ClassInfo::AddKnownSpell`
(`0x00649660`) with that pointer as `this`. Disassembling `0x00649660` shows a dedupe-then-append
onto a `CExoArrayList<uint32>` living at the ClassInfo entry's OWN base (offset 0) — confirmed
independently by `tools/q.py offsets CSWSCreatureStats_ClassInfo`, which lists `known_spells`
(a `CExoArrayList<unsigned_long>`) at `0x000`, `class_id` at `0x01B` and `level` at `0x01C` —
the exact three fields this document's earlier sections already use. `Odyssey.ReadKnownSpells`
reads this array directly (`data` pointer at `+0x00`, `count` at `+0x04`), the read-side
counterpart of the write `grantpower` already performs, never a new engine call.

### 9.4 XP: reusing `grantxp`'s own field write, gap computed by the engine's own calculator

Both real `AutoLevelup` callers gate on `CanLevelUp` first, so this pass does the same: if
`CanLevelUp(stats)` is false, `CSWSCreatureStats::CalculateExpNeededForNextLevel`
(`0x005A5490` — §3 already names this as `AddMultiClass`'s own XP-gap calculator) gives the
ABSOLUTE XP threshold for the next level (0 once the 20-level cap is reached), and the plugin
writes that value directly through `Odyssey.Progression.SetExperience` — the SAME raw
`CSWSCreatureStats+0x068` field write `grantxp` already performs (companion-state-sync.md's own
live test plan), reused rather than reversing an unverified two-argument engine
`SetExperience`/`AddExperience`. This satisfies the task's "the XP is granted consistently
(grantxp exists)" instruction literally: it is the same write, called from a second place.

### 9.5 The verbs

- **`levelup <classSlot 0|1 | classes.2da id> [count]`** (`CheatKindLevelUp` = 100,
  `CheatKinds.LevelUp` = 100) — `Odyssey.TryAutoLevelUp`: resolves the local player, confirms
  the named class IS `classes[class_count-1]` (refusing otherwise — failure 4), verify-bytes-or-
  refuses `AutoLevelup`/`CanLevelUp`/`CalculateExpNeededForNextLevel` (failure 5), then loops up
  to `count` (clamped 1-40) times: top up XP if `CanLevelUp` is false, call `AutoLevelup(stats,
  1)`, and confirm the class level actually advanced before continuing (stopping early — not
  failing — at the level cap or on an engine-side refusal). Console/log answer shows the
  before/after class list, exactly like `addclass`'s own precedent.
- **`stats`** (`CheatKindStats` = 101) — read-only: class levels, current/max hit points,
  current/max Force points, and known powers per class slot (full lists in the plugin log,
  the LAST-leveled slot's list in the 64-byte console reply — the same "full listing in the
  log, short summary in the reply" precedent `items`/`item` already established,
  console-cheats.md §3).

Neither verb changes the shared-block version or adds a protocol field — both are local-only
staging verbs, the same authority class `addclass`/`cast`/`grantpower` already have.
`CheatKindSyncTests` covers the two new numbers the same way it covers every other pair.

### 9.6 Co-op: what the peer sees

`levelup`/`stats` call ONLY the local player's own stats block and touch no shared/replicated
state — they are pure local staging verbs, like `cast`/`grantpower`/`addclass` before them.
The class SLOT itself is not new information to a peer: `addclass` already relays the class
grant (protocol 78/79, §6-§7). What `levelup` changes on top of that — the LEVEL inside that
class, hit points, current Force points, feats and skill points — is exactly the same
per-player state `progression-sync.md`'s XP max-merge and `companion-state-sync.md`'s own
fields already reason about, but this pass does not add or touch a sync channel for any of
them: level-up in KOTOR (like feat/power selection, §"Feats and Force powers" above) has always
been something each player does for their OWN character, on their OWN screen, and `levelup` is
a headless stand-in for exactly that action — it doesn't fight `progression-sync.md`'s XP
channel because it GRANTS XP through the SAME local field `grantxp` already uses (§9.4), which
that channel's own max-merge already treats as "this player's own report." A peer watching over
network sees only what they would see from a real level-up: the caster's HP/FP numbers change
on next sync, and (once `class_grant_sync` and a companion/party-state channel carry it) their
class and level fields update accordingly — nothing here is hidden from or contradicts those
channels, because nothing here bypasses the fields they already read.

### 9.7 NEEDS-LIVE — the exact recipe

None of §9.1-9.6 has run in the actual game. On ANY save (the level-1 rig test character is
fine — this section exists because that character has zero Force points):

1. `addclass 3` — opens Jedi Guardian at class slot 1, class_count 1→2 (§3's own existing
   live-verified behaviour, 2026-09-19).
2. `levelup 1 4` — levels slot 1 (Guardian) four times. Expect the console answer
   `classes 2:[0@1,-1@0] -> 2:[0@1,3@4], granted 4/4` (exact "before" numbers depend on the
   save's base class/level).
3. `stats` — expect `fp` in the reply to read greater than 0 (Guardian's Force-point-per-level
   gain, four levels' worth) and `cls` to show `1@4` for the Guardian slot. If `fp` is still 0,
   STOP: something in §9.1's chain did not fire the way this pass expects — do not proceed to
   `grantpower`, since a genuine 0 Force points is the exact dead end effect-sync.md §14.7 hit.
4. `grantpower cure` — grants Force Cure to the Guardian class slot (effect-sync.md §14.3,
   unchanged by this section).
5. `cast cure me` — expect `cast: queued spell 10 at 0x7FFFFFFF (metamagic 0)` AND (unlike
   §14.7's first attempt) `stats` run again afterward should show `fp` LOWER than step 3's
   reading — the engine actually PAID for the cast this time, which is the whole point of this
   section. If the character was already at full health, damage yourself first (`hurt <n>`) so
   Cure's animation and hit-point rise are also visible, not just the Force-point spend.

Regression check: run `stats` once BEFORE `addclass`/`levelup` on the same save and confirm it
reports `fp 0/0` (or close to it) and the base class only — the negative control that this
section's numbers are real, not stale/cached.

### 9.8 Live result (2026-09-25)

* **Lehon save:** `levelup 1 4` took class 5 from level 0 to level 6 and reported
  `granted 4/4`. `stats` then read `hp 72/72 fp 36/82` with six powers known.
* **`000010 - campaign_dantooine`:** the rig's level-1 Soldier + Guardian@0 carries a large XP
  bank. `levelup 1 3` jumped Guardian 0 -> 19 on the first call (`granted 1/3 (reached the
  max...)`). `stats` read `hp 240/240 fp 76/135`.

So one `AutoLevelup` call can consume the whole XP bank, and `count` counts calls, not levels.
On a character with no spare XP each call is one level, because the top-up only fills the gap to
the next level. This is harmless for staging and is recorded so nobody reads `count` as exact.

**Force points are real:** `GetMaxForcePoints` and the `+0x122` current value both moved with
the level. Spending them with `cast` does NOT work yet. The queued node is type 0x0F, which
RunActions sends to `CSWSCreature::AIActionWaitForEndOfRound` (0x513F60). It carries spell 10
and the target, leaves the queue within ~3 s, and spends no Force points. This held for the
caster itself and for the peer's stand-in, and nothing reached the partner. See effect-sync.md
for the follow-up.

## 10. Redesign: everybody picks their OWN class (2026-09-25)

Every section above shipped and was live-verified against a design that turns out to be wrong
in one important way: §6's relay literally forwards the CLASS ID (§7's `addclass 3` →
`classes 1:[0@1,-1@0] -> 2:[0@1,3@0]` on the peer) — so the first player through Dorak's
conversation picks a class for EVERY connected player, whether they wanted it or not. The user's
own report: "Each player must choose their OWN Jedi class." This section replaces the apply
half of the relay; the capture/announce half (§6, `AddMultiClassHook`, `ClassGrantController`'s
`DrainCaptures`, the wire packet's bytes) is unchanged.

### 10.1 The new contract

1. Whoever runs Dorak's conversation (host OR joiner — see docs/research/dialogue-sync.md's
   "the joiner CAN start conversations" correction; conversations are single-owner) picks their
   class there, exactly as retail.
2. Every OTHER connected player whose character does NOT already carry a Jedi class (Guardian 3
   / Consular 4 / Sentinel 5) is shown a private popup on their OWN screen and picks their OWN
   class, applied through the SAME `Odyssey.AddMultiClass` VM-handler call §3 already verified —
   never a raw poke, and never the peer's exact class.
3. A player who already has a Jedi class (rejoining, or their own character already chose) is
   never prompted again — `JediChoiceRule.Decide` reads BOTH class slots, not just slot 0.
4. A late joiner, or a peer disconnected at the exact moment of the choice, is checked again the
   next time they are admitted (`CoopHost.ResyncJediChoice`, §10.4).
5. The relay now carries "a Jedi choice happened", never "apply this class to the receiver".

### 10.2 No NCS compiler exists in this repo — the popup needed none

The task asked to check for a native K1 class-selection GUI first. None was found: `k_pdan_makejedi`
(§3-§4) is the ONLY code path in the shipped game that ever calls `AddMultiClass`, and it runs
entirely from Dorak's dialogue tree (three visible replies at `dan13_dorak.dlg` Entry
76→77/78/79, §7a) — there is no separate class-selection panel anywhere in the client's GUI
manager or resource list this pass could find. So the popup is a small co-op conversation, per
the task's own fallback plan.

Building one hit a wall the task anticipated ("compile NCS with the repo's existing approach…
otherwise document exactly how the existing `k_coop_` assets are produced"): **this repo has
never compiled a line of NWScript.** `git grep`-ing for `nwnnsscomp`/`.nss`/`.ncs` across
`tools/`, `docs/`, `src/` finds nothing, and the two prior `k_coop_` override assets
(`k_coop_phantom.utc`, `k_coop_spawn.utw` —
`KotorCoop.Memory.GameFiles.PhantomBlueprint`/`SpawnWaypointBlueprint`) are PURE DATA: every
`Script*` field in `PhantomBlueprint` is shipped blank, on purpose ("no NCS authoring", its own
doc comment). Standing up a K1-compatible NWScript compiler (verifying a third-party tool
against this project's byte-exact, sha256-matched-engine ethos) is a much larger undertaking
than this task's scope, and the existing `k_coop_` precedent already answers the question: this
repo's established approach IS "pure data mod, no new compiled scripts, ever".

**So `k_coop_jediclass.dlg` (`JediClassDialogueBlueprint.cs`, same
`GffWriter`/`GffWriterStruct`/`GffWriterList` API the other two blueprints use) carries NO
scripts at all** — every `Script` field on every entry and reply is blank. Its field set is
copied struct-for-struct from the smallest dlg found in the game's own resources
(`danm13_s.rim:dan13_droid.dlg`, decoded with `tools/kotorfiles.py gff …` against the local GOG
install, one entry/one reply, 17 and 15 fields respectively — the exact counts the blueprint's
`AddEntry`/`AddReply` helpers reproduce), the same "clone a decoded stock struct" method
`PhantomBlueprint`'s own doc comment describes.

Structure: `StartingList[0]` → `EntryList[0]` ("Choose the training you will pursue"), whose
`RepliesList` offers `ReplyList[0..2]` (Guardian/Consular/Sentinel, in that order — the GFF list
POSITION is stable because this code builds it, unlike a shipped .dlg's slot compaction
docs/research/dialogue-sync.md warns about). Each reply's `EntriesList` links to its OWN
terminal `EntryList` index (1/2/3), whose `RepliesList` is EMPTY — the same "an empty link list
ends the conversation" shape the shipped sample's own terminal reply already uses, with no
script needed. `JediChoiceRule.ClassForTerminalEntry` maps entry index → `classes.2da` row; both
sides (`JediChoiceRule.cs` constants and `JediClassDialogueBlueprint.cs` constants) are
duplicated by hand — the plugin cannot reference `KotorCoop.Memory` (NativeAOT win-x86 vs the
agent's x64, the same split `CheatKindSyncTests.cs` documents for `CheatKinds`) — and
cross-checked by `JediChoiceRelayTests.TheDialogueConstantsMatchTheBlueprint`.

Since there is no script to run `AddMultiClass`, **the PLUGIN drives it**:
`JediChoicePromptController` starts the conversation, then watches
`Odyssey.GetDialogueNodeIndex()` (§"Node sync" above, `CSWSDialog+0x70`, already live-verified
2026-08-11) every frame; landing on one of the three terminal indices calls
`Odyssey.AddMultiClass(player, classId, isReplay: false)` directly — the identical, already-live
-verified VM-handler call §3/§7 use, just invoked by the controller instead of by a script. Not
a replay, so `AddMultiClassHook` captures it as a genuine local grant and the EXISTING announce
path (§6) republishes it — for free, extending the chain to a third, fourth, … player with no
new wire logic.

⚠ NEEDS-LIVE, more than anything else in this document: whether the ENGINE accepts a
conversation whose speaker and listener are the SAME object id. `Odyssey.StartConversation` is
called `(playerObjectId, playerObjectId, "k_coop_jediclass")` — a self-talk conversation, chosen
because it needs no NPC, no proximity and no spawn. Nothing in this pass's static reading rules
it out (`BeginConversation`'s participant resolution was never fully decoded — see
docs/research/dialogue-sync.md's own "NEEDS-LIVE" list), but nothing proves it either. If it
turns out the engine refuses (or silently no-ops) a self-talk conversation, the fallback is to
target the existing `k_coop_phantom` creature (already spawned near a remote peer for other
purposes) or a new inert placeable/waypoint instead of the player itself — a one-line change to
`JediChoicePromptController.StartPrompt`'s `npcObjectId` argument, nothing else in this design
depends on speaker==listener.

### 10.3 Why the relay's wire bytes did not need to change, and why the protocol version still bumped

`ClassGrantPacket(OriginPeerId, OpSequence, ClassId)` is UNCHANGED — `ClassId` still travels,
purely for the receiver's chat/log line ("Malak's Jedi choice happened") and the announcer's own
"you are now a Guardian" message. What changed is what the RECEIVER does with it:

* **Before:** `ClassGrantController.ApplyRequest` read `ClassGrantRequestClass` and called
  `Odyssey.AddMultiClass(player, thatExactClass, isReplay: true)` unconditionally (refused only
  by `ClassGrantApplyRule`'s slot-full/already-has/bad-class checks).
* **Now:** `JediChoicePromptController` (new file, the OLD `ApplyRequest` method deleted)
  reads the SAME `ClassGrantRequestSerial` field, but only asks "does this character have ANY
  Jedi class yet" (`JediChoiceRule.Decide`, checking BOTH slots — `ClassGrantApplyRule.Decide`
  is left alone, still used by the local `addclass` dev verb, which still forces an exact class
  for rig testing). If not, it shows the popup; the popup's own pick is what eventually calls
  `AddMultiClass`, always with the LOCAL player's OWN choice.

No `SharedBlock` layout change was needed either: the same six offsets
(`OffsetClassGrantSerial/Class/RequestSerial/RequestClass/Applied/Status`) are reused verbatim,
with `RequestClass` now vestigial (kept only so the wire's field shape needs no bump) and one
new `ClassGrantStatusCode` value (`Prompted = 6`, "the popup is up; the class arrives later
through the announce channel, not this ack").

Even though not one byte on the wire moved, `ProtocolConstants.Version` bumped 70 → 71 anyway.
The project's own rule ("bumped on any wire-FORMAT change") does not strictly require this — but
an old peer and a new peer exchanging the IDENTICAL bytes would silently disagree about what
those bytes mean (old: force my class onto you; new: check whether you need your own popup) —
exactly the class of bug version-gating exists to catch at Hello, even though this instance of
it has no byte-level symptom. See `ProtocolConstants.cs`'s own version-71 entry for the reasoning
recorded in the changelog.

### 10.4 The late-join / reconnect gap, and how it is closed

`ClassGrantSender`'s retransmit-until-ack table (`CollectDue`) retires an op once EVERY peer
CURRENTLY known has acked it — exactly like every other reliable-broadcast channel in this
codebase (container loot, materialised loot, party pool, store). That is correct for "deliver to
who's here now" but wrong for "a player who joins after the op already retired must still learn
the choice happened" (task requirement 3's second clause) — the identical gap
`ResyncContainerLoot`/`ResyncMaterialisedLoot`/`ResyncPartyPool`/`ResyncStore` already exist to
close for their own channels, all called from `CoopHost.HandleHello` on a `freshlyAdmitted` peer
(`CoopHost.Connection.cs`).

`CoopHost` now tracks `_jediChoiceEverHappened` (set the moment it either publishes its own
grant or relays a client's — never cleared for the life of the process; a module change does not
undo a class) and `ResyncJediChoice` re-enqueues a BRAND NEW, never-acked `ClassGrantPacket`
under the HOST's own identity for any (re)admitted peer, once that flag is set. Attribution in
the resync case is a minor cosmetic cost — the log might credit the host for a choice a client
actually made — but the receiver's own `JediChoiceRule.Decide` check ("do I already have a
class") makes an unneeded resync a safe no-op, exactly the same reasoning
`ResyncMaterialisedLoot`'s own dedup gives for a peer that already applied an op before it
dropped.

Live-verified test shape (`JediChoiceResyncTests.cs`, loopback host/client pairs, no live game):
a grant fully retires against one already-connected peer, THEN a second peer joins — and still
receives a fresh copy. A separate test confirms a joiner receives NOTHING when no Jedi choice
has happened yet (the resync must not manufacture one).

### 10.5 The `jedichoice` dev verb

`addclass <n>` (§7) still exists, unchanged, for forcing an EXACT class onto the local player —
still useful for exercising `AddMultiClassHook`/the announce path/`ClassGrantCaptureRule`
without touching the new popup at all. `jedichoice` (no arguments, `CheatKinds.JediChoice` /
`SharedBlock.CheatKindJediChoice = 102`) is new: it manufactures a LOCAL "a peer's Jedi choice
happened" request by bumping `ClassGrantRequestSerial` directly — the exact field a real relay
bumps — so `JediChoicePromptController`'s popup can be exercised on ONE instance, with no second
player and no Dantooine conversation. If the local character already has a Jedi class, the verb
answers with an immediate `AlreadyHas` ack and no popup — itself a test of the never-re-prompt
rule.

### 10.6 NEEDS-LIVE checklist for this section

Nothing in §10 has run in the actual game (the unit tests in `ClassGrantRelayTests.cs`,
`GffWriterTests.cs` and `JediChoiceResyncTests.cs` cover every PURE rule and the resync's wire
delivery; nothing exercises the popup itself, which needs `Odyssey`/engine calls no test host
can make). A precise live recipe:

1. **Host chooses via Dorak, joiner is prompted.** Both in a Soldier/Scout/Scoundrel save,
   `class_grant_sync=1` on both installs (default). Host reaches Dorak's terminal reply (§7a's
   recipe, or the `addclass` dev verb as a stand-in for the conversation itself). Expect the
   joiner's plugin log: `jedi-choice: request #1 - showing the class-choice prompt
   (k_coop_jediclass)`, then the popup itself on the joiner's screen (NEEDS-LIVE: does self-talk
   even open one — §10.2's caveat). Joiner picks a DIFFERENT class than the host. Expect:
   `resolve party` on both sides shows two DIFFERENT Jedi classes, and the host's own log never
   shows a second `AddMultiClass` for its own character.
2. **Joiner chooses via Dorak, host is prompted.** Mirror of 1, joiner-led (per
   docs/research/dialogue-sync.md, the joiner CAN start and hold a real conversation).
3. **Rejoin with a class already: no prompt.** A player who already has a Jedi class
   disconnects and reconnects (or the same save is loaded fresh): expect
   `jedi-choice: request #N - this character already has a Jedi class (…); nothing to prompt` in
   the log, and no popup.
4. **Late join: prompt.** A THIRD player connects for the first time after 1 or 2 already
   happened (their character has no Jedi class yet). Expect `CoopHost.ResyncJediChoice` to fire
   on admission (`freshlyAdmitted` in the host's log/behaviour) and the new player to see the
   popup within the same tick budget as a live grant.
5. **Never re-prompt within a session.** Trigger `jedichoice` twice in a row on the same
   character before answering the first popup: expect the SECOND request to be logged as
   "coalesced into the prompt already on screen" and only ONE popup ever shown.
6. **The popup is never mirrored.** While a popup is up on one instance, confirm the OTHER
   instance's `DialogueSpectateController`/follow machinery shows nothing (no serial bump, no
   partner tag) — the exemption `JediChoicePromptController.IsPromptActive` provides.

### 10.7 Open risks not resolved by this pass

* **Self-talk conversation, unverified (§10.2).** The single biggest live-verification risk in
  this whole section.
* **Story flags that assume THE PC made Dorak's choice.** Some later content may read a global
  (`DAN_PATH_STATE` or similar) that only the ORIGINAL chooser's script sets — a player who got
  their class from the popup rather than the real conversation never runs `k_pdan_saber12/13/14`
  (§7a), so any quest/journal/reward state those scripts ALSO set (the saber crystal item, the
  `dan_trials` journal entries) is NOT replicated for the popup player. This document's §"Feats
  and Force powers" already established that feats/powers/levels were always per-player; this
  redesign does not change that, but it widens the same gap to cover the Dantooine
  saber-crystal reward and journal entries too — the popup grants ONLY the class, deliberately
  (never a raw copy of quest state, which is out of scope here and would need its own relay,
  the same as the existing `dialogue_item_sync`/`script_item_sync` channels handle for OTHER
  scripted rewards).
* **Cross-instance globals are not synced by this pass.** `Odyssey.TryReadGlobalNumber`/
  `TryWriteGlobal` (Odyssey.Globals.cs) exist and could read `DAN_PATH_STATE` locally, but each
  instance simulates its OWN world state — there is no general mechanism in this codebase that
  replicates an arbitrary script global from one player's engine to another's (only specific,
  purpose-built channels like this one exist). The late-join fix (§10.4) is therefore a
  SESSION-layer fact (`CoopHost._jediChoiceEverHappened`), not a game-engine one; it answers
  "has ANY player's Jedi choice happened", not "what does Dantooine's own story state say" —
  which is exactly what the task's own risk list anticipated.
* **Both peers must run the same build.** Since the wire bytes are unchanged (§10.3), a
  mismatched pre-71/post-71 pair would be REJECTED at Hello rather than silently misbehave — but
  only because the version was bumped specifically for this reason; there is no independent
  runtime check that both sides agree on what a `ClassGrantPacket` means beyond that gate.
* **The popup versus following a PEER's real conversation, at the same moment.** The exemption
  (§10.2, `IsPromptActive`) stops `DialogueSpectateController.PublishLocal` from announcing the
  popup outward, but `PublishLocal`'s early return also skips `FollowRemote` for that frame —
  if a peer's OWN conversation starts in the exact window this player's popup is up, the follow
  is simply delayed a frame rather than colliding with it (the engine's own one-conversation
  rule and `DialogueGateHook`'s token would refuse a second real conversation opening on this
  player regardless). Not exercised live; flagged here rather than assumed safe.

## 11. First lightsaber colour, per player (2026-09-25)

Closes the review doc's "Zhar saber construction reward" row for the case two players have (or
will have) DIFFERENT Jedi classes: today the construction reward mirrors whichever ONE colour
the builder's engine created to every connected player, regardless of that player's own class.
Static research + design + implementation this pass; **nothing here has been run on a live
process** — tagged NEEDS-LIVE throughout, unit-tested at the pure-rule and pump level instead.

### 11.1 Retail mechanics — VERIFIED by static NCS decode (this pass)

Decoded with a small reuse of `tools/localvaraudit.py`'s own NCS decoder/abstract interpreter
(`decode()`/`Interp`, the same machinery the file already uses for `SetLocalBoolean` audits) —
subclassed to log every `ACTION` call instead of only the four local-var routines, then run
against `danm13_s.rim`'s own scripts via `kotorfiles.py`'s `Game.module_read`. Cross-checked
against §7a's own hand-derived recipe and the `2026-09-23` live result — both agree.

**Path choice** (`dan13_zhar.dlg` entries 76-79, the three visible class choices, already
mapped in §7a's table) each run one of three near-identical scripts:

| Script | `SetGlobalNumber("DAN_PATH_STATE", …)` | Crystal created (`CreateItemOnObject(…, GetFirstPC(), 1)`) | Class (§4, via `k_pdan_makejedi`) |
| --- | ---: | --- | --- |
| `k_pdan_saber12` | 1 | `dan13_bluecryst` | Guardian (3) |
| `k_pdan_saber14` | 2 | `dan13_goldcryst` | Sentinel (5) |
| `k_pdan_saber13` | 3 | `dan13_grncryst` | Consular (4) |

Each also runs `SetGlobalBoolean("DAN_DORAK_DONE", FALSE)` first (re-arming Dorak's own gate,
§7a). The crystal is created directly onto `GetFirstPC()` — the LOCAL engine's own player, not
a wire-visible id.

**Construction** (`dan13_zhar.dlg` entries 100-103, §7a's table), terminal script
`k_pdan_saber16`, full decode:

```
oPC = GetFirstPC()
oCrystal = GetItemPossessedBy(oPC, "dan13_plotcrys")   ; a TAG lookup, not a resref
SetPlotFlag(oCrystal, FALSE)
DestroyObject(oCrystal, 0.0, FALSE, 0.0)
switch (GetGlobalNumber("DAN_PATH_STATE")) {
    case 1: oSaber = CreateItemOnObject("g_w_lghtsbr01", oPC, 1); break;  // Guardian, blue
    case 2: oSaber = CreateItemOnObject("g_w_lghtsbr04", oPC, 1); break;  // Sentinel, yellow
    case 3: oSaber = CreateItemOnObject("g_w_lghtsbr03", oPC, 1); break;  // Consular, green
}
AssignCommand(oPC, ActionEquipItem(oSaber, 4 /* INVENTORY_SLOT_RIGHTWEAPON */, FALSE))
DelayCommand(…, ShowUpgradeScreen(oSaber))   // the workbench GUI, local/cosmetic only
```

**`GetItemPossessedBy(oPC, "dan13_plotcrys")` is a TAG lookup, not a resref match** — all three
crystal blueprints (`dan13_bluecryst`/`goldcryst`/`grncryst`) share the SAME `Tag` field
(`dan13_plotcrys`), which is why one script can find "the" crystal regardless of which colour
path granted it (the same Tag-as-a-different-thing-than-resref fact item-identity.md §8.2/§12.1
already established for other items).

**Colour/template selection is keyed 100% off the single global `DAN_PATH_STATE`, never off the
PC's class fields directly.** In single-player this is invisible, because `DAN_PATH_STATE` and
the class `k_pdan_makejedi` grants both come from the SAME dialogue choice on the SAME (only)
character. In a co-op world where two players can hold different classes, one shared global
cannot represent two different players' two different choices at once — which is exactly why
today's generic item-mirror channel (§11.2) copies whatever `DAN_PATH_STATE` happened to say
onto every connected player, right or wrong for their own class.

**Scan for other callers, whole game (this pass):** every module archive referencing any of the
three saber resrefs as raw bytes (27 archives — `STUNT_14/57`, `danm13/14/15/16`, `ebo_m12aa`,
`end_m01aa`, `korr_m34/36aa`, `kas_m22ab`, `manm26-28`, `sta_m45aa/ac`, `unk_m44aa`) was decoded
the same way. **`k_pdan_saber16` is the only script anywhere in the shipped game that reaches a
`CreateItemOnObject`/`GiveItem`/`CopyItem` call with one of these three literals as an
argument.** Every other reference is either a stock creature-equipment blueprint field (an enemy
Jedi's `.utc` simply equips one) or a plain string comparison/array literal — e.g.
`danm14aa:k_pdan_14a_area.ncs` builds an unrelated area tag table containing
`g_w_lghtsbr01`/`03`/`04` back-to-back with `dan_wanderhound` (a companion tag), never spawning
any of them; confirmed by reading the raw bytecode context around each string constant, not just
the interpreter's reachability answer (one other script, `k_pdan_14a_area.ncs`'s OWN area-enter
routine, hit the interpreter's recursion-depth budget before reaching every branch — inconclusive
by the interpreter alone, but the same string-table evidence applies to it directly since all
three strings sit in the identical straight-line load sequence). Treated as a closed question for
design purposes; see §11.6 for the residual risk this leaves.

### 11.2 What "the shared party pool" the task named actually is here

The task brief says the reward "lands in the shared party pool" and that "the party item pool is
synced as one shared stash across players" (`docs/research/party-pool-sync.md`). That document's
own channel — the `CItemRepository` behind `CSWPartyTable+0x118`, `party_pool_sync` — is a
SEPARATE, currently-OFF, still-buggy feature (party-pool-sync.md §7-§10: a live double-apply bug
still open) for the drag-into-stash shared stash panel. **It is not what mirrors Zhar's saber.**
The channel actually responsible, confirmed by re-reading `docs/research/dialogue-consequences.md`
§3's own live evidence (and matching the review doc's check31/host-led-Sentinel results exactly):
`dialogue_item_sync`/`script_item_sync` (`DialoguePump`/`ScriptItemGrantPump`) — a generic "a new
item just appeared in the granting player's OWN inventory outside a pickup" diff, publishing
through `PublishDialogueGrant`/the give-loot cheat channel, applied on the peer via
`Odyssey.GiveItemToInventory` into THAT player's own creature backpack. Both channels are ON by
default. This is worth stating precisely because the fix below depends on it: each player's
inventory is genuinely a SEPARATE `CSWInventory` on a SEPARATE engine process — "shared" here
only ever meant "the generic mirror channel copies items 1:1 between them", never a literal
single stash object. That is exactly the property the design in §11.3 exploits.

### 11.3 Design — a receiver-side substitution, not a sender-side change

**Choice: intercept on the RECEIVING side's apply step, not the sending side's detection.**
`DialoguePump`/`ScriptItemGrantPump` keep announcing exactly what their own engine created,
unchanged, for every item they carry — that generic detector is correct and heavily tested for
everything else it forwards, and touching it would risk every other reward/script grant in the
game. Instead, `LootPump` (the shared receiving half both `CoopHost`'s and `CoopClient`'s agent
already run — see its own doc comment, no host/client branch anywhere in it) is given one new,
narrow decision to apply to ONE incoming `DialogueGrantPacket` item, immediately before it would
otherwise be queued for a local give unchanged:

- Is the incoming resref one of the three canonical starter sabers
  (`FirstLightsaberColorRule.IsCanonicalFirstSaber`)? If not, nothing changes — every other item
  this channel ever carries is untouched, by construction (the check is the first line of the
  rule).
- Does the RECEIVING player already possess one of those three resrefs (read fresh, every call,
  via the SAME `IGameBridge.ReadPersonalPossessedItems` sweep `ScriptItemGrantPump` already
  uses)? If so: **Skip** — drop the give, log why, never touch the crystal (the receiver never
  had one to begin with — see §11.4) and never re-arm anything, because the check is re-derived
  from the live inventory every time, not a one-shot flag. This is what makes a resync, a
  retransmit, a rejoin or a late join's backlog naturally idempotent.
- Otherwise, read the RECEIVING player's own current class ids (`IPlayerBridge.ReadLocalClassIds`,
  §11.5 — a NEW, purely local, `ReadProcessMemory`-only reader, no engine call, mirroring the
  plugin's own already-verified `Odyssey.ClassGrant.cs::TryReadLocalClassList` offsets on the x64
  agent side, duplicated because the x86 plugin and this x64 agent cannot share an assembly — the
  same reasoning item-identity.md §14 already gives for its own stack-identity duplication) and
  map through the SAME retail table §11.1 found (`FirstLightsaberColorRule.ResrefForClass`):
  **Substitute** the resref if it disagrees with what the sender announced, **PassThrough**
  unchanged if it already agrees, or (no resolvable Jedi class yet on this player — a timing gap
  against whatever grants classes) **PassThrough** the builder's own colour rather than silently
  dropping their first saber.

This is deliberately the opposite direction from "the sender decides what to send each peer": the
sender has no reason to know anything about anyone else's class, and never needs to — each
receiver independently decides, from its OWN locally-read class, what an announced grant should
become for itself. Two players with different classes each get their own correct colour
regardless of which one led the construction, with zero host/client special-casing, because the
SAME code runs the SAME decision on whichever side is receiving.

**Crystal handling needs no new code at all.** The peer never had a plot-crystal copy to begin
with — `k_pdan_saber12`/`13`/`14` only ever grants one to `GetFirstPC()` on whichever engine ran
THAT specific Dorak-path dialogue choice, and `k_pdan_saber16`'s `DestroyObject` call only ever
runs, locally, against the builder's own crystal item on the builder's own engine. This feature
never creates, reads or destroys a crystal on the receiving side — "no duplicate plot crystal
left behind" is true by construction, not by an added check.

**Why this is isolated from the inventory model, and what it would take to change.** The
substitution lives entirely inside "what resref does this ONE grant item become before it is
applied" — it does not know or care whether the destination is a personal `CSWInventory` (today)
or some future shared/marked-ownership stash. If the inventory model changes later so that an
item can be tagged "belongs to player X" rather than mirrored wholesale, this rule's inputs
(incoming resref, receiver's own class, receiver's own possession check) and its three-way
decision stay exactly the same — only the code that ACTS on `Decision.Substitute`/`Skip` (today,
`LootPump` deciding what to enqueue for a give) would need to change to write an ownership tag
instead of a different item entirely. Nothing in `FirstLightsaberColorRule` itself references
`LootPump`, `IGameBridge` or any wire type — it is a pure `(string, IReadOnlyList<int>, bool) ->
Result` function in `KotorCoop.Protocol`, by design, so it survives that kind of change unchanged.

### 11.4 What was built

**Protocol** (`KotorCoop.Protocol`): `FirstLightsaberColorRule.cs` — the pure decision above
(`PassThrough`/`Substitute`/`Skip`), plus the retail `ResrefForClass` table and
`IsCanonicalFirstSaber`. No wire types, no protocol version bump — this never crosses the network
in a new shape, it only reinterprets an existing `DialogueGrantPacket`/`MaterialisedLootItem` on
the receiving side.

**Memory** (`KotorCoop.Memory`): `IPlayerBridge.ReadLocalClassIds()` (new default-interface
member, empty by default) and its `KotorGameBridge.Player.cs` implementation — four new private
consts (`StatsClassCount 0x089`, `StatsClasses 0x08C`, `ClassInfoClassId 0x1B`,
`ClassInfoStride 0x28`, matching `Odyssey.ClassGrant.cs`'s own already-verified offsets
byte-for-byte) and a plain pointer-chain read off the cached `_player` the same way
`TryReadFaction` already does. No plugin/native change of any kind — this reads the SAME field
the plugin already reads, independently, from outside the process.

**Session** (`KotorCoop.Session`): `LootPump`'s `DialogueGrantPacket` receive loop (the same loop
§3/the script-item-sync section already documents) now evaluates the rule per item, behind
`AgentSettings.FirstSaberColorSync`; `CoopConfig.FirstSaberColorSync` (coop.ini
`first_saber_color_sync`, OFF by default) and `AgentComposition`'s `?? false` fallback — all
three agree at OFF (docs/CONFIG.md footnote ³'s own rule, checked by hand this pass since this
key has no plugin half to cross-check against). `LootPump`'s constructor gained an `AgentSettings`
parameter it did not have before (every other pump already takes one); its one call site
(`CoopAgent.cs`) updated to match.

**Tests**: `FirstLightsaberColorRuleTests.cs` (9 cases — unrelated resref always passes through,
each class produces its own retail colour even against a different builder colour, an
already-matching class is a no-op PassThrough, an already-has-one receiver is always Skip
regardless of class, the idempotency shape across two calls, a base class with no Jedi slot yet
falls back to PassThrough, no class ids at all falls back to PassThrough, a multiclassed receiver
is matched by whichever slot holds the Jedi class, `IsCanonicalFirstSaber` is case-insensitive and
exact); `FirstLightsaberColorSyncPumpTests.cs` (6 cases, through a real `CoopAgent.PumpOnce()` the
same shape `DialogueGrantQueueTests` already uses — off by default leaves the grant untouched,
a differing class substitutes, a matching class passes through, an already-owned first saber
drops the give entirely, an unrelated item in the SAME grant is never touched, a resend after the
first grant landed is skipped not doubled).

### 11.5 NEEDS-LIVE

Nothing in this section has run against the real game. In rough order of what would invalidate
the most:

1. **`ReadLocalClassIds` reads the right bytes.** Compare its answer against the plugin's own
   already-live-verified `addclass`/`stats` console replies (§7/§9.8) on the SAME character —
   they read the identical offsets, so they must agree.
2. **The end-to-end host-led case.** Two connected instances, DIFFERENT classes (e.g. host
   Guardian, joiner Sentinel — `class_grant_sync` on, or each player runs their own Dorak path).
   `first_saber_color_sync=1` on the RECEIVING instance (this setting is purely local — only the
   side that would otherwise get the wrong colour needs it on). Host completes Zhar's
   construction chain (§7a's exact recipe). Expect: host's own saber matches the host's own
   colour (untouched, vanilla path); joiner's plugin log shows `first lightsaber: substituting
   'g_w_lghtsbrNN' for 'g_w_lghtsbrMM'…`; joiner's inventory holds exactly the substituted
   resref, never the host's.
3. **The joiner-led case**, reversed roles — confirms `LootPump`'s own claim of being
   host/client-symmetric actually holds against the real cheat-ack timing, not just in a fake
   bridge.
4. **The double-grant guard, live.** Repeat the SAME construction reward delivery (a resync,
   or a second `scriptgive`-style redelivery) after the first one landed — confirm the SECOND
   attempt is refused (`already has a first saber`) rather than producing a second saber in the
   receiver's own inventory.
5. **A rejoin/reload after the fact.** Disconnect the receiver after it holds its own correct
   saber, reconnect, and confirm no second grant is queued (the "already has one" check reads
   the LIVE inventory on the reconnecting instance, not a value carried over the wire).
6. **§11.1's one open item**: `k_pdan_14a_area.ncs`'s interpreter-depth-budget timeout. If a rig
   session ever reaches Dantooine part 2 with `first_saber_color_sync=1`, watch for an
   unexpected `first lightsaber:` log line outside the Zhar construction chain — that would mean
   this script's string table is not what §11.1 concluded it was.

### 11.6 Open risks

1. **Dependency on the parallel per-player-class work.** This feature only produces the RIGHT
   colour once the receiving player already has their own resolved Jedi class at the moment the
   grant arrives — if the per-player class assignment (a separate, concurrent effort per this
   task's own brief) has not reached that player yet, `FirstLightsaberColorRule` falls back to
   PassThrough (today's exact behaviour: the builder's own colour), never a crash and never a
   dropped saber, but not yet the fully-correct outcome either. No live timing data exists yet
   for how large this gap typically is.
2. **`k_pdan_14a_area.ncs`'s unresolved script (§11.1).** Static evidence (the string-table shape)
   argues it is not a real second grant source, but the interpreter itself could not independently
   confirm it by reachability. Bounded risk: even in the worst case, the item it would trigger
   this rule on is only ever one of the three canonical resrefs, its effect at worst substitutes
   or skips a saber the game intended the player to have anyway — never fabricates a WRONG item
   for an unrelated grant.
3. **Only `dialogue_item_sync`/`script_item_sync` were checked as delivery paths.** If a future
   channel ever also relays a `g_w_lghtsbr0{1,3,4}` grant by some other route (a container/corpse
   loot pickup, for instance — deliberately NOT covered, since a picked-up saber someone else
   actually left on the ground is not "your own first saber" and should never be recoloured),
   this rule is not consulted there, on purpose. If that assumption turns out wrong for a real
   quest item elsewhere in the game, it needs its own review, not an automatic extension of this
   one.
4. **`first_saber_color_sync` is asymmetric on purpose** — only the RECEIVING side needs it on
   for that side's own colour to come out right; the setting name and doc say so, but a rig
   session should confirm the console/log output makes this obvious enough that a live tester
   does not spend time toggling it on the wrong machine.
