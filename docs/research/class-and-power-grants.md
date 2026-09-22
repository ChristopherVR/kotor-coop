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
