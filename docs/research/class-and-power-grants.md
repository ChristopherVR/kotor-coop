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

## 8. Not done / could not resolve statically

- Feat and Force-power grants (§1, §5).
- Whether `UpdatePureGoodEvilPowers` and `DistributeExperience` behave on a peer whose party table differs from the host's.
- A second live check that `handler(implementer, routine, 2)` accepts the argument count with a pushed `(object, int)` pair - modelled on `GiveItemToInventory`; NEEDS-LIVE.
