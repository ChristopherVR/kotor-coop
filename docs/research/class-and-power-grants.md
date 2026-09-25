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

### 11.2 Live round (2026-09-25): the receiver-side substitution never ran, and a correction to §11.2's original claim about the party pool

Commit `59cb101` shipped the design in the original §11.2-§11.6 below (kept in git history, not
here — this section replaces it after a live round on the main session found it dead). Staged
exactly per the recipe: joiner Sentinel (class 5), host Guardian (class 3), `first_saber_color_sync=1`
on both. On the joiner: `setglobal DAN_SABER_DONE true`, `setglobal DAN_PATH_STATE 2`,
`scriptgive dan13_goldcryst`, `startdialog 0x19 dan13_zhar` — the chain auto-advanced 100→101→102→
103→R133, `k_pdan_saber16` ran on the JOINER's own engine exactly as §11.1 decoded: the crystal was
consumed (`party pool: local remove of dan13_goldcryst` logged on BOTH instances) and the joiner's
own `g_w_lghtsbr04` (object `0x172`) was created and immediately equipped.

**Result: no `DialogueGrantPacket` was ever published, no `first lightsaber:` line appeared
anywhere, and the host received nothing at all.** The original §11.2-§11.3 design (a `LootPump`
hook reacting to a `DialogueGrantPacket`) never had anything to react to.

**Root cause.** `k_pdan_saber16` equips the new saber (`AssignCommand`+`ActionEquipItem`) within
the SAME script execution that creates it, while the Zhar conversation is still open. Both
`DialoguePump`'s reward diff and `ScriptItemGrantPump`'s own sample compare the local player's
BACKPACK/pool view (`IGameBridge.ReadPersonalPossessedItems`) between two sampled instants; an
item that is created and equipped inside one uninterrupted script call never sits in that view at
any instant either pump actually samples, so the net diff is zero — not a bug in either pump, a
structural blind spot for THIS reward specifically (a corpse loot, a plain dialogue reward that
stays in the backpack, and every other case those pumps were built for all still work). §11.1's
crystal-consumption evidence confirms the same mechanism from the other side: the crystal WAS
visible to both instances' pumps because it sat in the backpack/pool the whole time it existed.

**Correction to the original §11.2's claim about the party pool.** The original text said
`party_pool_sync` was "currently-OFF" and not what mirrors this reward. Both halves were wrong, per
`docs/research/key-items-and-solo.md` §1 and this round's own live log line
(`party pool: local remove of dan13_goldcryst` on both instances, unprompted): **`party_pool_sync`
is ON by default (mode 2, docs/CONFIG.md), and a live PC's own backpack IS the shared party pool**
(`CSWSCreature::GetItemRepository(1)` on a party member resolves to the SAME `CItemRepository`
`CSWPartyTable+0x118` names — there is no separate "personal inventory" repository object for a
live PC to begin with). So an UNEQUIPPED item sitting in any connected player's own backpack is
pool content and gets relayed by `PartyPoolPumpV2` to every OTHER connected player as pool content
— which is exactly the mechanism that would turn a naive "just give the receiver their own saber
into their backpack" fix into a NEW duplicate-visibility bug (the receiver's own colour showing up
in the BUILDER's pool view too). This is the load-bearing fact behind §11.4's redesign: the fix
must equip the created saber, never leave it in the backpack, on every engine it touches.

### 11.3 Redesign — reliable detection, existing-channel delivery, local build on every OTHER engine

**Detect: an equip-slot edge, not an inventory diff.** `PlayerState.EquippedRightWeapon`/
`EquippedLeftWeapon` (docs/research/animation-and-minimap.md's own "phantom body" fields) are
resolved every tick straight from the creature's equip-slot pointers
(`KotorGameBridge.ReadEquippedResref`, `CSWInventory+0x14`/`+0x18`) — NOT from a repository
snapshot, so they cannot miss an equip the way a backpack diff can; they simply report whatever
the engine's own slot holds, this tick. The first tick either slot reads one of the three canonical
starter sabers (`FirstLightsaberColorRule.IsCanonicalFirstSaber`), THIS engine's own player has
built one — no native hook, no shared-block change, nothing beyond a field this session was
already reading every tick for a different reason.

**Deliver: no new packet.** The announcement is a single, session-wide, sticky fact — "the party
has built a first saber" — with NO payload beyond that: every receiver (including the publisher's
own next tick) decides ENTIRELY from its own locally-read class which colour, if any, it
personally needs, so there is nothing else worth carrying. This fits the EXISTING world-progression
flag channel (`EventFlagStore`/`FlagOpPacket`, `ICoopSession.SetFlag`/`Flags`) exactly: a new
64-bit flag id (`FirstLightsaberColorRule.FirstSaberBuiltFlagId`, tag byte `'S'` in the same
object-keyed address space `ObjectKeyedFlags` already documents for doors/placeables/mine
triggers — see that class's own remark that "adding a tag needs no protocol bump: the key is
opaque to the flag store and the wire"). This channel is already reliable (per-key Lamport clock,
ack'd, retransmitted), already resynced WHOLE to a late joiner via the existing paged world
snapshot, and needed ZERO protocol changes — a straight `session.SetFlag(id, 1)` call.

**Apply: build+equip locally, on every OTHER engine, from that engine's own class.** While the
flag reads true and this player has not yet equipped a first saber (own weapon slots re-checked
every tick — never a one-shot latch, so a resync/rejoin/late join is naturally idempotent) and
this player's own class list (`IPlayerBridge.ReadLocalClassIds`, unchanged from the original
design — the plugin's own already-verified `Odyssey.ClassGrant.cs` offsets, read externally) names
a Jedi class, queue exactly one automatic `equip` cheat (`CheatKinds.Equip` →
`Odyssey.EquipItemFromTemplate`: create THEN `ActionEquipItem` in one engine call, matching
retail's own idiom byte-for-byte) for the colour that class implies
(`FirstLightsaberColorRule.ResrefForClasses`). Gated the same way `ClassGrantPump`'s own apply is
(`ClassGrantReplayRule.Decide` — not mid-load, not mid-dialogue, not mid-combat) and rate-limited
to `ClassGrantReplayRule.MaxAttempts` tries `RetryMs` apart, reusing those constants directly
rather than inventing new ones.

**Equipping, not just giving, is deliberate — see §11.2's own correction.** An unequipped item in
a backpack is pool content and would relay right back onto every OTHER player; equipping removes
it from that repository the same call, exactly the property that already (if inadvertently)
protected the BUILDER's own saber from ever appearing as a pool addition in the live round above.

**Symmetric, no host/client branch.** The SAME pump (`FirstLightsaberBuildPump`) runs on every
connected engine and does both halves (publish-if-I-built-one, build-if-I-haven't) — whichever
player leads the construction, every OTHER engine's own copy of this pump reacts to the SAME flag
the same way. Two players with different classes each end up with their own colour regardless of
who led, with zero special-casing for host vs. joiner.

**Crystal handling still needs no new code.** Unchanged from the original design: a receiver never
had a plot-crystal copy to begin with, so nothing here creates, reads or destroys one.

**Dead code removed, per this round's own instruction.** The original `LootPump.DrainDialogueGrants`
hook (§11.2-§11.3 as they existed before this round) is reverted — it is not merely redundant, it
is a live double-apply RISK: `k_pdan_saber16`'s equip timing is a script/engine-frame race, not a
guarantee, so a future run where the item happens to linger in the backpack one sample longer could
still trip the OLD path while the NEW one (below) also fires, granting a receiver two sabers. Only
the new equip-slot-based detection is wired in now. `FirstLightsaberColorRule.Decision`/`Result`/
`Evaluate` (the three-way substitute/skip/pass-through the old design needed for an INCOMING
resref) are removed with it; `ResrefForClass`/`IsCanonicalFirstSaber` stay (still load-bearing) and
a new `ResrefForClasses(classIds) -> string?` pure lookup replaces `Evaluate` for the new design's
simpler question ("what colour, if any, does this class list want" — there is no longer an
incoming resref to compare against).

### 11.4 What was built (supersedes the original §11.4)

**Protocol** (`KotorCoop.Protocol`): `FirstLightsaberColorRule.cs` reduced to `ResrefForClass`,
`IsCanonicalFirstSaber`, the new `ResrefForClasses`, and the new `FirstSaberBuiltFlagId` constant.
No packet types, no protocol version bump — the flag id lives inside the EXISTING `EventFlagStore`
key space.

**Memory** (`KotorCoop.Memory`): `IPlayerBridge.ReadLocalClassIds()` and its
`KotorGameBridge.Player.cs` implementation are UNCHANGED from the original round (still correct,
still needed) — see the original §11.4's description, kept here by reference rather than repeated.

**Session** (`KotorCoop.Session`): new `Pumps/FirstLightsaberBuildPump.cs` (detect via
`PlayerState` equip fields, publish via `session.SetFlag`, apply via a single-slot pending-build
queue mirroring `LootPump`'s own `TryDequeuePendingLoot` shape exactly) — wired into
`CoopAgent.PumpOnce` right after `_classGrantPump.Pump(...)`, and `CoopAgent.TryDequeuePendingFirstSaberBuild`
added alongside `TryDequeuePendingLoot`. `LootPump.cs` reverted to its pre-59cb101 shape (the
`AgentSettings` constructor parameter it gained for the old design is removed; its one call site in
`CoopAgent.cs` updated to match). `AgentSettings.FirstSaberColorSync`/`CoopConfig.FirstSaberColorSync`/
`AgentComposition`'s `?? false` fallback are UNCHANGED in shape (same coop.ini key, same OFF
default) — only their doc comments were updated to describe what the key now gates.

**Agent** (`KotorCoop.Agent`): `CoopCommandDispatcher.Items.cs`'s new `DrainAutomaticFirstSaberBuild()`
— the SAME single-slot arbitrated cheat-channel drain `DrainAutomaticLoot` already established,
submitting through `CheatKinds.Equip` (target 0 = local player, default slot = right weapon — the
SAME defaults the manual `equip` console verb already relies on, docs/research/companion-state-sync.md
§7/§5's own live-test primitive) rather than `CheatKinds.GiveLoot`. Wired into the command tick
loop right after `DrainAutomaticLoot`.

**Tests**: `FirstLightsaberColorRuleTests.cs` rewritten (9 cases — the three canonical resrefs
recognised case-insensitively, `ResrefForClasses` per class / no-Jedi-class / no-classes-at-all /
multiclass-slot-order, the flag id provably above `uint.MaxValue`).
`FirstLightsaberColorSyncPumpTests.cs` rewritten (7 cases, through a real `CoopAgent.PumpOnce()` —
off by default publishes and builds nothing; the builder's own equip edge publishes the flag and
queues nothing for themselves; another player with no saber yet builds their own class's colour
once the flag is set; a player with no Jedi class yet builds nothing but keeps watching, and
builds the moment a class arrives; a player who already has one equipped never gets a second;
no build is queued mid-dialogue, but the very next eligible tick catches it; a repeated/resynced
flag after the first build landed is not doubled).

### 11.5 NEEDS-LIVE

Nothing in this section has run against the real game — everything below is a fresh, unexercised
design following a live round that disproved the PREVIOUS design. In rough order of what would
invalidate the most:

1. **The equip-slot detection actually fires.** On the BUILDER's own engine, after `k_pdan_saber16`
   equips the new saber, the plugin/agent log should show
   `first lightsaber: this character is wielding a starter lightsaber - announcing it to the
   party` — confirms `PlayerState.EquippedRightWeapon` really does carry the new resref the SAME
   tick (or very shortly after) the equip happens, unlike the diff-based channels that missed it.
2. **The flag actually reaches the other engine.** The OTHER player's log should show the flag
   becoming true (or simply proceed straight to the build log below) within one round trip of the
   announcement above — confirms `EventFlagStore`/`FlagOpPacket` genuinely carries a
   non-KOTOR-global synthetic key end to end, which nothing in this codebase has exercised before
   (every existing flag is a real global variable or a real door/placeable/trigger).
3. **The build+equip actually lands.** The OTHER player's log should show
   `first lightsaber: building this character's own '<resref>' from the party's construction`,
   then the `equip` cheat's own answer (`equipped '<resref>' on 0x... slot 4`), and their in-game
   character should be visibly wielding it. Confirm the resref matches THEIR OWN class, not the
   builder's.
4. **The exact recipe, host-led.** Both instances in `danm13`, `first_saber_color_sync=1` on BOTH
   (the setting is symmetric now — every engine needs it to both announce its own build and build
   its own colour). On the HOST: give it a Jedi class different from the joiner's (`class_grant_sync`
   or the parallel per-player-class work), then stage and run the construction exactly as this
   round did on the joiner:
   ```
   setglobal DAN_SABER_DONE true
   setglobal DAN_PATH_STATE <1|2|3, matching the host's own intended colour per §11.1's table>
   scriptgive dan13_<blue|gold|grn>cryst
   startdialog 0x19 dan13_zhar
   ```
   The chain auto-advances 100→101→102→103→R133 exactly as this round observed; no manual reply
   selection needed once the crystal and globals are staged. Expect the host's OWN saber (vanilla,
   unaffected) plus the joiner ending up with THEIR OWN class's colour, never the host's.
5. **The exact recipe, joiner-led** — this round's own staging, reversed to run on the HOST from
   the joiner's perspective, or simply re-run verbatim (joiner Sentinel, host Guardian) now that
   the fix is in: `setglobal DAN_SABER_DONE true`, `setglobal DAN_PATH_STATE 2`,
   `scriptgive dan13_goldcryst`, `startdialog 0x19 dan13_zhar` on the JOINER. Expect this time: the
   HOST'S log shows the build/equip lines and the host ends up wielding `g_w_lghtsbr01` (Guardian),
   never `g_w_lghtsbr04`.
6. **The double-grant guard, live.** After a receiver's own build has landed, force the flag to be
   re-observed (a reconnect, or a host resync) and confirm NO second `equip` cheat is queued —
   the "already has one" check re-reads the LIVE equip slots every time, not a value carried over
   the wire.
7. **A late joiner.** Connect a THIRD player after the flag is already true and confirm their very
   first full flag-store sync already carries it (the paged snapshot, untested for a synthetic key
   before this feature) and that they build their own colour on schedule, same as anyone already
   connected.
8. **§11.1's one open item**: `k_pdan_14a_area.ncs`'s interpreter-depth-budget timeout — unchanged
   by this round. If a rig session ever reaches Dantooine part 2 with `first_saber_color_sync=1`,
   watch for an unexpected `first lightsaber:` announcement outside the Zhar construction chain.

### 11.6 Open risks

1. **The flag channel has never carried a synthetic (non-KOTOR-global, non-object) key before.**
   `EventFlagStore`'s own doc calls itself "key-agnostic" and `ObjectKeyedFlags` explicitly says a
   new tag byte needs no protocol bump, but every PRODUCTION consumer of the channel today carries
   a real global variable or a real door/placeable/trigger id — this is the first flag this
   codebase mints that names neither. Bounded: the store's own merge/dedup/resync logic does not
   inspect the key's meaning at all (VERIFIED by reading `EventFlagStore.cs`), so there is no
   reason to expect different behaviour, but it is untested in exactly this shape.
2. **Dependency on the parallel per-player-class work**, unchanged from the original design: this
   feature only builds the RIGHT colour once the receiving player already has their own resolved
   Jedi class — if that has not happened yet, the pump keeps checking every tick (never gives up
   on "no class yet", only on repeated ENGINE REFUSALS after a class IS found) and builds the
   moment one arrives, per `APlayerWithNoJediClassYetBuildsNothingButKeepsWatching`.
3. **`k_pdan_14a_area.ncs`'s unresolved script (§11.1)**, unchanged — see item 8 above.
4. **Only an equip-slot edge is watched, not the backpack.** A hypothetical future path that puts
   one of the three canonical resrefs into a player's backpack WITHOUT equipping it (no such path
   is known to exist in the shipped game per §11.1's whole-game scan) would not be detected. Judged
   an acceptable, documented gap rather than defence-in-depth worth the complexity, given §11.2's
   own finding that an unequipped copy is pool content anyway and would need different handling
   regardless.
5. **`first_saber_color_sync` is now SYMMETRIC, not asymmetric** (a correction from the original
   design's own open risk #4) — every connected engine needs it on, since the SAME pump both
   announces its own build and builds its own colour on every instance. A mixed on/off pair means
   whichever side has it off neither announces its own build nor builds anyone else's — a rig
   session should confirm the log output makes an accidental mixed pair obvious quickly.
