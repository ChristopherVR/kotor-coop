# Floor mines and trap triggers — what the engine does, and the one-way mirror

Source: `swkotor.exe` (GOG 1.0.3) read with `tools/disasm.py` / `tools/xrefs.py`, cross-checked against
`tools/kotor1_0_3.db` (`tools/q.py`). Tag key, same as the sibling write-ups: **VERIFIED** = read
directly out of the binary · **INFERRED** = a reasonable reading of control flow, not confirmed a
second way · **DESIGN** = a choice made here · **NEEDS-LIVE** = built, never watched on the game.

Closes the "Mines/floor traps not synced" gap of `docs/review-seamless-2026-09-18.md` §2 for the
GIT-placed mine. A player-PLACED mine is designed (§7), not built.

## 1. Summary

* A floor mine is an ordinary `CSWSTrigger` (vtable `0x00749A38`, size `0x318`) whose UTT `Type` is 2.
  It has **no armed / disarmed / detonated byte**. Every way it stops mattering — skill disarm,
  recover, key disarm, a one-shot detonation — ends the same way: the engine posts event 11
  (`DestroyObject`) at the trigger, and `CSWSTrigger::EventHandler` removes it from the area and
  deletes it. The only state that exists is *present or gone*, plus a "flagged" visibility bit.
* So the mirror carries one monotonic fact, "this mine is gone" (`MineFlags`, type tag `0x54`), and
  applies it on the peer through the engine's own DestroyObject. No field is ever poked.
* Correction to `placeables-containers.md` §3: `OnApplyDisarm 0x004D9CE0` is **not** the trap disarm.
  It is `CSWSEffectListHandler::OnApplyDisarm`, the WEAPON-disarm effect (§5). And `+0x2CC/+0x2D0`
  are not "is-active / fired": they are `AutoRemoveKey` and `TrapOneShot` (§2).

## 2. The CSWSTrigger trap state (all VERIFIED unless marked)

Field names come from the GFF labels `CSWSTrigger::LoadTrigger 0x0058DA80` reads (string literals
resolved with `tools/xrefs.py`-style byte search) and `SaveTrigger 0x0058E660` writes back.

| Offset | Field | Evidence |
| --- | --- | --- |
| `+0x244` … `+0x274` | `CExoString scripts[7]`: `+244` Heartbeat, `+24C` OnEnter, `+254` OnExit, `+25C` UserDefined, `+264` **OnTrapTriggered**, `+26C` **OnDisarm**, `+274` OnClick | `LoadTrigger` 0x0058DB65-0x0058DD15 loads the labels in that order at 8-byte strides; DB `scripts CExoString[7]` at `0x244` |
| `+0x27C` | `KeyName` (CExoString) | `LoadTrigger` 0x0058E0A1-0x0058E0CA |
| `+0x29C` | last entering object id (cached first by event `0xC`) | `EventHandler` 0x0058F1FA-0x0058F203 |
| `+0x2A4` | last disarmer / triggerer id | event `0x18` handler 0x0058F588; `OnEnterMine` 0x0058D669 |
| `+0x2A8` | list of creature ids that have DETECTED the trap (INFERRED) | `AIUpdate` 0x0058D863 asks it (`0x004AF240`) before choosing the hidden vs visible animation |
| `+0x2B4` | "is an area transition" (`Type == 1`) | `LoadTrigger` 0x0058E2BD |
| `+0x2B8` | faction id | `LoadTrigger` 0x0058E083 (`Faction`); `AIActionSetMine` 0x0051A6B6 copies the setter's faction |
| **`+0x2BC`** | **trap / mine gate** (`Type == 2`) | `LoadTrigger` 0x0058E2CB-0x0058E2D6 (`cmp eax,2; mov [esi+0x2BC],1; mov byte [esi+0x2FC],0`); constructor 0x0058EC8A zeroes it |
| `+0x2C0` / `+0x2C4` | `TrapDetectable` / `TrapDisarmable` | `LoadTrigger` 0x0058E100-0x0058E12E; ctor default 1 |
| `+0x2C8` | "flagged / detected" visibility bit | ctor zeroes it; `AIActionFlagMine 0x0050E400` sets it to 1 at 0x0050E7D5 after a skill check; `AIUpdate` 0x0058D82E picks animation `0x27A0` (visible) when set, `0x279F` (hidden) otherwise |
| `+0x2CC` | **`AutoRemoveKey`** (byte) | `LoadTrigger` 0x0058DF6F; read only by `OnEnterMine` 0x0058D628 |
| `+0x2D0` | **`TrapOneShot`** (byte, ctor default **1**) | `LoadTrigger` 0x0058DE37, ctor 0x0058ECA7; read by `OnEnterMine` 0x0058D6C0 |
| `+0x2D4` | `TrapType` (byte, ctor `0xFF`) — a row of `traps.2da` | `LoadTrigger` 0x0058DD67 |
| `+0x2D8` / `+0x2DC` | detect DC / disarm DC, from `traps.2da` columns `DetectDCMod` / `DisarmDCMod` | `LoadTrigger` 0x0058E18D-0x0058E1F5; `AIActionSetMine` 0x0051A670/0x0051A680 |
| `+0x2E4` | creator object id (`0x7F000000` = none) | `SetCreator 0x0058D700` (0x0058D70E) |
| **`+0x314`** | **"placed by the player's party"** (1 when the creator is the player or a party member) | `SetCreator` 0x0058D741/0x0058D750; counted by `CSWSArea::GetNumMinesSetByPlayerParty 0x00508960` (0x005089A5), whose caller `GetPlayerCanSetMines 0x005089D0` allows a new mine only while the count is `< 15` |
| `+0x0EC` | `is_destroyable` (CSWSObject) | `EventHandler` event-11 branch reads it at 0x0058F19F (DB: `CSWSObject +0xEC is_destroyable`) |

How a floor mine differs from an ordinary trigger, **VERIFIED**:

* `LoadTrigger` reads the UTT `Type` field (`"Type"`, 0x0058E29F): `1` sets `+0x2B4` (transition), `2`
  sets `+0x2BC` (trap). `TrapType`, `TrapOneShot`, `TrapDisarmable`, `TrapDetectable`, `AutoRemoveKey`,
  `KeyName`, `Faction`, `OnTrapTriggered`, `OnDisarm` are read for every trigger but only mean
  something when `+0x2BC != 0`.
* `CSWSTrigger::InTrigger 0x0058CE40` tests `+0x2BC` first: when non-zero it is a **sphere test**,
  squared distance from `+0x90..+0x98` against `[0x007A206C]²` (that float is `1.0`), not the
  polygon test. A floor mine fires within 1.0 unit of its centre; a placed mine's geometry is a
  2.0 disc (`CreateNewGeometry 0x0058D210`, `push 0x40000000`, at 0x0051A72B).
* `CSWSTrigger::EventHandler 0x0058F140` event `0xC` (entered) routes to `OnEnterMine 0x0058D570`
  when `+0x2BC != 0` instead of running `OnEnter` (0x0058F4E4).

Survey (GOG install, `tools/kotorfiles.py` + `predict_object_ids`, whose id rule is verified 217/217
against a real save): **122 GIT-placed trap triggers** across the shipped modules, all `TrapOneShot=1`;
the `g_t_trap001..013` templates are disarmable and detectable, while the Korriban
`k_kor_rockfall*` / `k_kor_deadrocktr` triggers are `Type 2` scripted traps with disarm and detect off.
Every object id is small (largest seen `0x12E`), deterministic across instances exactly like a door's.

## 3. Detonation — `CSWSTrigger::OnEnterMine 0x0058D570`

Called by `EventHandler` on event `0xC` (VERIFIED, 0x0058F4E4 gate):

```
0x0058D574  eax = [esi+0x29C]                      ; the entering object
0x0058D584  ebx = GetCreatureByGameObjectID(eax)   ; 0x004AE770; null -> return
0x0058D59E  if (!GetCanFireMineOnObject(this, ebx->id, arg)) return   ; 0x0058D4A0
0x0058D5B7  item lookup by KeyName ([esi+0x27C]) in ebx's repository, then in its 18 equip slots
            (CItemRepository::FindItemWithTag 0x00555ED0, CSWInventory::GetItemInSlot 0x005A4C20)
  ... KEY FOUND (edi != 0):
0x0058D628      if ([esi+0x2CC]) { CItemRepository::RemoveItem 0x00555FD0; delete item }   ; AutoRemoveKey
0x0058D669      [esi+0x2A4] = [esi+0x29C];  RunScript([esi+0x26C]=OnDisarm, trigger id)
0x0058D683      jmp 0x0058D6D3                                          ; destroy self
  ... NO KEY (edi == 0):
0x0058D68B      CSWSCreature::SendFeedbackMessage(ebx, 0x52, 0)         ; 0x004EDE10
0x0058D69D      RunScript([esi+0x264]=OnTrapTriggered, trigger id)
0x0058D6BB      RunScript([esi+0x24C]=OnEnter, trigger id)
0x0058D6C0      if ([esi+0x2D0] /*TrapOneShot*/ == 0) return            ; a re-arming trap stays
0x0058D6D3  AddEventDeltaTime(days 0, time 0, obj=trigger, obj=trigger, event=[0x007441E0]=11, 0)
```

So a key is a *silent disarm*, an unkeyed one-shot mine detonates (its `OnTrapTriggered` script,
resolved from `traps.2da` `TrapScript` when the field is empty or `"default"`, 0x0058DD78-0x0058DE0F,
applies the damage) and **then deletes itself**. A `TrapOneShot=0` mine survives and can fire again.

`GetCanFireMineOnObject 0x0058D4A0` (INFERRED from control flow, 0x0058D4E7-0x0058D556): the entering
creature must be hostile to the mine's faction through `CSWSObject::GetReputation 0x0057CB80` (`<= 10`
against `+0x2B8`, or the creator's side for a placed mine), and the creature must not be exempt through the
`0x005A6960` flag test. That is why a friendly phantom standing on a mine sets nothing off.

`AIUpdate 0x0058D760` (the per-frame service) runs the heartbeat script every 6000 ms
(`cmp [esp+0x14], 0x1770`, 0x0058D7CF) and, while `+0x2BC != 0`, flips the trigger's animation state
between hidden `0x279F` and visible `0x27A0` from `+0x2C8` / the detect list.

## 4. Disarm, flag, recover — the creature actions (all end by destroying the trigger)

`CSWSCreature::AIActionDisarmMine 0x00519570`, trigger branch (object-type byte `[0x00746636]` = 7, reached
through the object's virtual-slot `+0x40` accessor):

* Range check (`0x004F6000`, radius `0.25` = `0x3E800000`), a timing bar (`StartGuiTimingBar 0x004EF480`,
  `0x1194`), then a d20 roll (`rand()%20+1`, or a fixed 20 when creature `+0x4E0` is 0) plus the skill
  rank (`CSWSCreatureStats::GetSkillRank 0x005AA570`) against the trigger's `+0x2DC` (disarm DC).
* **Success:** builds a `CScriptEvent` (`0x004D7540`, subtype `0x18`) and posts it to the trigger through
  `CServerAIMaster::AddEventDeltaTime` with event id `[0x007441DC]` = 10 (0x005199BD; the handler runs
  the `OnDisarm` script and stores the disarmer at `+0x2A4`, `EventHandler` 0x0058F579-0x0058F596), then
  posts event `[0x007441E0]` = **11** at the same object (0x005199C9-0x005199EF), then
  `SetFadeOutTime 0x004CE8A0`. (For a door or placeable the same routine clears their trap fields instead;
  that is the placeable `trap_flag +0x278` mirror already shipped.)
* **Failure:** a feedback message and no destroy event (control flow to 0x00519C24; INFERRED).

`AIActionFlagMine 0x0050E400` — skill check against `+0x2DC - 5`, then `+0x2C8 = 1` (0x0050E7D5). Local
visibility only; not mirrored.

`AIActionRecoverMine 0x00518C40` — the same shape with DC `+0x2DC + 10`. On success it posts event 11 at the
trigger (0x005190A2), then builds the ITEM the player gets back: allocates a `CSWSItem` (`push 0x29C`,
ctor `0x005530A0`), reads the `ResRef` column of `traps.2da` at row `+0x2D4`
(`GetCExoStringEntry 0x00413270`, string at `0x00747508`), `CSWSItem::LoadFromTemplate 0x005608B0`, and
`CItemRepository::AddItem 0x0055D330` into the recoverer's repository (0x005192D1-0x005192D8). `traps.2da`
(14 rows) names the item per trap type: rows 0-11 give `g_i_trapkit001`..`012` (the three Korriban rockfall
rows 12-13 have none). The item appears on the recovering instance only; the shared-inventory channel is
what carries it to a peer.

## 5. Why `OnApplyDisarm 0x004D9CE0` is not this

`CSWSEffectListHandler::OnApplyDisarm` (DB name; its only reference is an effect-handler table entry at
`0x004E4BE9`) reads slot `0x10` (right-hand weapon) of the target through `CSWInventory::GetItemInSlot`,
tests the weapon's flag, removes it from the creature and drops it near the feet. It is `EFFECT_DISARM`, the
combat manoeuvre. Trap disarm lives in `AIActionDisarmMine` above.

## 6. What the mirror does

**Publish (`KotorGameBridge.Mines.cs`, agent process, read-only).** Once a second: every cached
`CSWSTrigger` address is re-verified (vtable, object id `<= 0xFFFF`, `+0x2BC != 0`, `+0x314 == 0`), giving
the set of live GIT mines. `MineVanishTracker` turns "a baselined mine stopped being alive" into a report,
with these guards (all unit-tested), because a false "gone" would make a peer delete a live mine:

* the first 3 world-alive passes after a module entry are the baseline; anything first seen later is never
  eligible (a party-placed mine is also excluded by `+0x314`);
* absent for 3 consecutive passes;
* the pass only counts if at least half of the cached door + placeable addresses still carry their vtable
  (an area unload frees everything at once and must not read as every mine vanishing);
* a pass that would report 3 or more mines AND more than half the baseline is treated as an unload and dropped;
* each id is reported once per module; an id the store already says is gone is never echoed.

The report is `session.SetFlag(MineFlags.MakeKey(moduleHash, objectId), 1)` — key = tag `0x54` in bits
63..56, the module hash, the 24-bit object id (`ObjectKeyedFlags.TriggerTag`). It rides the existing
reliable flag store; no packet or protocol change (`ProtocolConstants.Version` stays 56).

**Apply (plugin, `MineController`, game thread).** The agent appends each store flag for the local module to the
placeable slot list as a kind-1 entry (`SharedBlock.Mines.cs`: byte `+7` of an 8-byte slot, padding until
now; layout, capacity and version unchanged). For each one `MineController` requires: `mine_sync` on, the id
resolves to a trigger by vtable, `+0x2BC != 0` (never delete an ordinary trigger whose id drifted),
`+0xEC != 0` (the engine skips a non-destroyable object), no destroy already posted. Then it calls
`Odyssey.DestroyObjectById`, i.e. the NWScript `DestroyObject` command. Its tail
(`ExecuteCommandDestroyObject 0x0052FF20`, 0x0053012F-0x0053015C) posts event `[0x007441E0]` = 11 through
`AddEventDeltaTime` — the very event `OnEnterMine` and `AIActionDisarmMine` post — and
`CSWSTrigger::EventHandler` (0x0058F197-0x0058F1BA) answers with `RemoveFromArea 0x0058EE30` (unlinks the id
from the area's object lists at `+0x12C` and `+0x1A0`) and the virtual deleting destructor. The engine
rebuilds its own derived state; the plugin writes nothing.

**Not mirrored (by design).** The damage/effect of a detonation (only the instance that stepped on it takes
it), the recovered mine ITEM (inventory channel), the flagged/detected bit `+0x2C8`, a `TrapOneShot=0`
mine that survives its detonation, and every player-placed mine (§7).

## 7. Player-placed mines — DESIGN only, not built

`CSWSCreature::AIActionSetMine 0x00519E30` (VERIFIED, 0x0051A607-0x0051A76B) builds the object: gate on
`CSWSArea::GetPlayerCanSetMines 0x005089D0`; a d20 + Demolitions-type skill check against `SetDC` from
`traps.2da`; then, when there is no target object (a floor mine, not a door/placeable trap):

```
alloc 0x318 ; CSWSTrigger::Constructor 0x0058EAE0 (0x7F000000 => a DYNAMIC id) ; SetCreator 0x0058D700(creator = setter)
+0x264 = TrapScript(2da) ; +0x2D4 = TrapType ; +0x2D8/+0x2DC = detect / disarm DC
+0x2BC = +0x2C0 = +0x2C4 = 1 ; +0x2B8 = setter's faction (creature stats +0x78)
+0x22C = TrapName strref ; CreateNewGeometry 0x0058D210 (radius 2.0) ; AddToArea 0x0058F030 at the setter's position
area->list(+0x12C).Add(id) (CExoArrayList::Add 0x00671C00) ; then consume the mine item (stack-1 or event 11 on the item)
```

Why it is NOT mirrored: its object id is a per-instance dynamic id — the same number on the peer names a
different object (or nothing). A key built from it would delete the wrong thing, which is why the publish
side excludes `+0x314 != 0` mines outright. A safe mirror needs a host-authoritative spawn plus an owner id:

1. The placing instance publishes `{owner peer, module hash, trap type, position, detect/disarm DC}`, NOT an
   object id; the host allocates the one canonical mine (running the block above through a plugin call) and
   answers with a session-wide mine serial.
2. Every instance keeps a `serial -> local dynamic id` map; a detonate or disarm reports the serial, and the
   apply resolves the local id through the map (the same shape the enemy census uses for materialised
   creatures). The placer's item consumption already travels through the inventory channel.
3. Faction is the placer's party faction, hostile to enemies only, so the hazard is mostly enemy-facing; on a
   client the enemies are mirrored copies, so damage would have to route through the enemy-damage ledger
   rather than be applied twice. That routing is the open question, and the reason to stop here.

### 7.1 Offline route audit (2026-09-21)

The installed GOG executable was re-read from disk with `tools/disasm.py`, `tools/xrefs.py`, and
`tools/q.py`; no process was opened and no game memory or code was written. The static trace confirms
that a safe *engine-thread construction sequence exists*, but it does not make the replication feature
safe to enable yet.

The only floor-mine constructor in the retail image is the no-target branch of
`CSWSCreature::AIActionSetMine 0x00519E30` (`0x0051A607..0x0051A76B`). The exact sequence is:

```
operator new(0x318)                         0x006FA7E6 (__cdecl)
CSWSTrigger::Constructor(p, 0x7F000000)     0x0058EAE0 (__thiscall, ret 8)
CSWSTrigger::SetCreator(p, setter_id)        0x0058D700 (__thiscall, ret 4)
CSWSTrigger::CreateNewGeometry(p, 2.0, ...) 0x0058D210 (__thiscall, ret 12)
CSWSTrigger::AddToArea(p, area, x, y, z, 1, 1)
                                             0x0058F030 (__thiscall, ret 0x14)
```

Before `AddToArea`, the engine writes the trigger's trap fields (`+0x2BC`, `+0x2C0`, `+0x2C4`,
`+0x2D4`, `+0x2D8`, `+0x2DC`, `+0x2B8`, `+0x264`, and `+0x22C`) and then appends the returned
dynamic id to the area's mine list at `+0x12C`. The constructor starts the id at `0x7F000000`, so
the id allocated by this path is dynamic and local to the process. `tools/xrefs.py` reports the
constructor call sites at `0x0050A42E`, `0x0051A62B`, and `0x005220C6`; `CreateNewGeometry` is
called at `0x0051A72D` and `0x0052214E`; and `AddToArea` is called at `0x0050A744` and
`0x0051A750`. These are independent static references to the same route.

The damage side is engine-owned: `CSWSTrigger::OnEnterMine 0x0058D570` calls the trap script at
`+0x264`, then queues event 11 (`0x007441E0`) at the trigger. The shipped trap script applies its
damage through the normal effect handler (`CSWSEffectListHandler::OnApplyDamage 0x004DFA40`), whose
effect creator is the object that entered. This proves that a directly constructed mine can use the
normal damage pipeline; it does **not** prove which process should own damage when the entering object
is a mirrored enemy.

The frame hook gives a suitable *thread boundary*: the IAT `GDI32!SwapBuffers` hook at `0x0073D00C`
runs on the game thread after `CServerExoApp::MainLoop`. It is therefore the only candidate at which
the sequence above could be attempted. It is not a license to call it: the construction still needs
the trigger's `CExoString` lifetime, `TrapScript`/`TrapName` resolution, area pointer, and creator
semantics to be proved with a live spawn. No such spawn has been attempted in this pass.

The static route leaves three blockers that are material to correctness:

* No NWScript command in the 772-entry command table creates a floor mine. `CreateObject` is a
  blueprint path and does not provide the `CSWSTrigger` trap fields; using it for a mine would require
  the raw field writes above.
* The dynamic id cannot cross the wire. A host serial and a per-instance `serial -> dynamic id` map
  are required, plus a module epoch so a stale serial cannot target a newly allocated object.
* A mine's `OnTrapTriggered` script damages the local entering object. Mirroring the destroy event is
  straightforward once the serial map exists, but forwarding damage requires an explicit authority
  rule for mirrored enemies. Reusing the existing enemy-damage ledger without proving that the trap
  effect is captured exactly once would either double-apply damage or lose it.

Accordingly this pass adds no engine writes, hooks, or packet types. The one-way GIT-mine mirror remains
the only enabled path. The concrete next implementation step is a disposable, host-only main-thread
spawn probe that verifies the returned id, `+0x314` creator marker, `+0x2BC` trap gate, geometry, and
one walk-on damage event before any serial transport or peer apply code is enabled.

## 8. Live-proof recipe (two instances, GOG rig, both with `mine_sync = 1`)

Every WRITE here is behind `mine_sync` (default ON). The live results are recorded in §9; these
steps remain the reproducible setup for a future regression pass.

Setup: `mine_sync = 1` in both `coop.ini`; host `host`, client `connect`; host `warp tar_m10aa` (the client
follows). `tar_m10aa` has GIT mines `0x71` (173.26, 127.53) and `0x74`, `0x75`, `0x76` (~102.8, 128.9-134.1);
`python tools/kotorfiles.py objectids tar_m10aa` lists every trigger id so the ids can be re-checked.

1. **Both sides see the same mine.** On each instance `resolve 0x71` and `resolve 0x74`: expect a non-zero
   pointer (a trigger). If `resolve` says `0x0` the id assumption is wrong; stop and re-read §2.
2. **Engine removal path, no damage (host to client).** Host: `destroyobject 0x75`. Expect the host log
   `destroyobject: destroyed 0x75`. Within ~5 s (3 s absence streak + a 1 s pump + the plugin's 40-frame pass)
   the client plugin log should read `mine 0x75: removed on the session's authority (engine DestroyObject)`, and
   client `resolve 0x75` reports `0x0`. That proves publish, transport, slot kind byte and the DestroyObject-on-a-
   trigger path in one go.
3. **Real detonation (host to client).** Host: `tp 173.26 127.53`. Expect the host takes the trap's damage, `resolve 0x71` becomes
   `0x0` on the host, then on the client within ~5 s.
4. **Reverse direction.** Client: `tp 102.83 134.06` (mine `0x74`). Expect the same on the host. (`destroyobject`
   is refused on a mirroring client by design; the walk-on detonation is the client's trigger.)
5. **No false positives.** With `mine_sync = 1` on both, warp out and back (`warp tar_m10ab`, then `warp tar_m10aa`)
   and `resolve 0x76` on both: the untouched mine must still exist on both (a leaving instance must not read
   its unload as "every mine vanished"). Repeat the transition a few times, in both directions.
6. **Refusals.** `resolve` a story trigger id from `objectids` (a non-mine): it must never disappear.
7. **A party-placed mine stays local.** `scriptgive g_i_trapkit004` (a minor fragmentation mine, `traps.2da`
   row 3), lay it from the inventory or radial menu, then detonate or recover it: the peer must NOT lose any
   mine, and the placing instance must publish nothing (its `+0x314` is 1).
8. **Off switch.** Set `mine_sync = 0` explicitly on the receiving side and repeat step 2:
   that side must keep its mine. Removing the key enables the shipped default and does not
   switch the feature off.

Things to read off the live game that this pass could not: whether a freed trigger's memory really stops
carrying the `0x00749A38` vtable within the 1 s scan (the absence detector relies on it; if it keeps it, mines
are never reported, the safe failure), whether `+0x314` reads 0 for every GIT mine, and whether the
DestroyObject event on a trigger visibly removes the mine model for the peer.

## 9. Live result (2026-09-19) - steps 1, 2 and 5 VERIFIED

Both instances with `mine_sync = 1`, host `warp tar_m10aa`: step 1 (`resolve 0x71/0x74/0x75/0x76` non-zero on both). Step 2: host `destroyobject 0x75` -> host `destroyed 0x75`; client plugin `mine 0x75: removed on the session's authority (engine DestroyObject)` within 12 s and client `resolve 0x75` = 0 while `0x76` stayed. Step 5: four warps (`tar_m10ab`, `tar_m10aa`, twice) and then `0x74` and `0x76` still resolved on BOTH instances, i.e. no false "gone" from a module unload; `0x75` stayed gone on both (the area keeps the removal for the session). A `tp` onto a mine does not fire it (a teleport skips the trigger's enter test), so steps 3 and 4 were run by walking.

**Steps 3 and 4 VERIFIED (second run, same day).** `tools/click.ps1 -HoverOnly` to give the window focus, then `walkto`: the host walked onto `0x71` (173.26, 127.53): `0x71` was gone on the host and the client's plugin logged `mine 0x71: removed on the session's authority (engine DestroyObject)`. The client then walked toward `0x74` (102.83, 134.06) and detonated `0x75` on the way (the trio sits together): the host's plugin logged `mine 0x75: removed on the session's authority`, `0x75` was gone on both, `0x74` still existed on both when checked. Both games stayed up.

**`mine_sync` is now ON by default** (three readers agree: `PluginConfig.ReadBool("mine_sync", true)`, `CoopConfig.MineSync` and its absent-key parse, `AgentComposition`'s `?? true`). Still not built: mines a party member PLACES.

## 10. Static hardening (2026-09-21)

The apply side now rejects IDs outside the publisher's deterministic `1..0xFFFF` range and
refuses a trigger whose party-placed field (`+0x314`) is set. This protects local dynamic mines
even if an incoming removal resolves to one. The shared 32-slot list reserves at most four
entries per pass for mines and rotates both mine and placeable backlogs, so neither list's tail
is permanently omitted by the publisher. The off-switch test now actually writes `= 0`.

All 27 mine tests pass, including both selection cursors and party-placed refusal. These checks
do not replace a live busy-module test of slot consumption, and do not implement mine placement.

## 11. Dynamic identity foundation (2026-09-23)

`PartyPlacedMineIdentityMap` now provides a tested, currently unconsumed mapping from
authoritative spawn serials to each engine's local object IDs. Every operation carries
session-generation and world-epoch tokens. Scope changes clear both directions; retired
serials cannot be resurrected. Registrations must arrive in increasing authority serial
order, with idempotent repeats permitted only while the same mapping remains active.
The 15-active-mine limit follows the retail party-placement cap; a scalar serial high-water
mark avoids accumulating retirement tombstones. Ten focused tests pass, including stale
events, local ID reuse, capacity, and different IDs for the same mine in two engines.

This does not enable party-placed replication. The future transport must preserve spawn
order and scope, and the native consumer must validate a live party-placed trigger before
using a resolved ID. Main-thread creation, capture of the actual spawned identity, exactly
one item consumption, recovery rewards, and single-authority damage still require proof.

## 12. Dev-only real recover/disarm action probe (2026-09-23)

The console now has a bounded diagnostic for exercising the retail mine action on an existing
GIT mine:

```text
mineaction recover 0x71
mineaction disarm #115
```

The target must be explicitly written as `0xN` or `#N`, and must be a deterministic GIT object
ID in `1..0xFFFF`. The agent records both the current module and `WorldLoadSerial` when it submits
the mailbox request. On the game thread, the plugin checks both values before resolving any
object. A request left queued across a reload or same-module world replacement is refused. The
target must still resolve to the requested server object and be a readable, disarmable GIT mine
(`CSWSTrigger` vtable, mine gate set, `+0x2C4 != 0`, `+0x314 == 0`); the plugin also checks its
client-side object identity, current local player identity, and that both are in the same area.
Player-placed mines and non-mine triggers are refused.

This calls the retail client action-menu entry points, not a plugin implementation of the skill:
recover uses `0x00691950` and disarm uses `0x00691900`. The installed executable's entry bytes
are checked before invocation. Both methods use `thiscall` and end in `ret 8`; the recover entry
passes the local actor to `PerformSkill` (`0x0060EE00`, skill `0x65`), while disarm submits the
normal use-skill action. The engine owns range, timing, Demolitions roll, trigger destruction,
and any reward. For a trap row with an item resref, the engine adds that item to the recovering
actor's repository. The destination matters: the shared party pool is handled by `party_pool_sync`
(`PartyPoolPumpV2`), while a personal inventory addition is observed by `script_item_sync`'s
personal-inventory grant diff. Do not assume which channel owns the recovered item until the
live result identifies its actual destination. The diagnostic's “queued” response only means
the native request was submitted; it does not claim the skill succeeded, the trigger was removed
on both peers, or that exactly one item reached the appropriate peer inventory/pool. It never
grants an item or destroys a trigger directly.

The pure request/rule suite passed 20 tests, and the native build completed with
warnings-as-errors. The diagnostic was deployed. Both engines safely refused a non-mine target,
and a real recovery and disarm request were submitted; the “queued” response records submission,
not skill success. See §13 for the bounded outcomes and the unresolved positive-result test.

## 13. Recovery/disarm live attempts and candidate audit (2026-09-23)

The deployed diagnostic was exercised against the installed GOG campaign. A recruited Canderous
with Demolitions rank 2 attempted `mineaction recover 0x71` on `tar_m10aa` at 11:42:06. The mine
was absent in the later save and his HP remained 50, but no trapkit delta was found in the shared
pool or available-NPC inventory. This does **not** prove recovery succeeded: the mine is
`g_t_trap010.utt` (`TrapType=9`, `DisarmDC=20`, recoverable), `traps.2da` row 9 is
`TRAP_POISON_GAS_MINOR` with recovery item `g_i_trapkit010`, and recovery checks
`DisarmDC + 10` (30). Rank 2 cannot meet that DC even with the maximum d20 result (22). The
poison hazard may remove the mine without direct HP loss, so mine disappearance plus unchanged
HP is not a success signal. The exact disposition of any reward remains unobserved.

A separate `mineaction disarm 0x74` attempt removed that mine, but Canderous's saved HP fell
from 50 to 32. This is consistent with a failed roll/detonation; it is not evidence of a
successful disarm. Ordinary disarm has no recovery-item reward. Neither attempt had a peer
connected, so neither establishes co-op parity or exactly-once item delivery. No skill ranks,
mine flags, or reward items were forced.

An offline scan of installed campaign GIT/UTT resources found 106 disarmable placed mines among
117 module areas. The `DisarmDC` value used by the native check is loaded from the mine's
`traps.2da` row into trigger `+0x2DC` (`CSWSTrigger::LoadTrigger`, 0x0058E181–0x0058E18D).
The scanned DCs are 20 (35 mines), 25 (41), and 30 (30); no mine has `DisarmDC <= 12`, the
maximum row DC that Demolitions 2 could recover (row DC + 10). Therefore the currently staged
rank-2 Canderous cannot provide a legitimate positive recovery test on any scanned GIT mine.
Use an actor with naturally sufficient Demolitions to test successful recovery, then capture
the actor's personal repository and shared pool before/after on both peers. A separate successful
disarm check also needs a naturally qualified actor and a saved HP/trigger result that shows the
roll succeeded without relying on disappearance alone.

The native audit confirms `AIActionRecoverMine` creates the row's item and calls
`CItemRepository::AddItem` on the recoverer's repository on success (0x005192D1–0x005192D8).
The exact live repository delta and its transport remain unproved. Check `script_item_sync` for
personal inventory and `party_pool_sync` for shared-pool changes; do not assume the reward belongs
to either destination until a qualified live success identifies it. The diagnostic never grants
the item or destroys a trigger directly.

## 14. Player-placed mine replication — BUILT, NEEDS-LIVE (2026-09-24)

The design in §7/§7.1 is now fully implemented end to end, behind `placed_mine_sync`
(**default OFF**). Nothing in this section has been exercised against a running game; every
native call and field write is verify-bytes-or-refuse and marked NEEDS-LIVE below. The pure
logic (identity map, host authority, fairness, the damage rule) is unit-tested; a live two-
instance run is the only thing this pass could not do (§14.6 gives the recipe for the next one).

Three readers agree on the default (OFF): `PlacedMineController.Enabled`
(`PluginConfig.ReadBool("placed_mine_sync", false)`), `CoopConfig.PlacedMineSync`
(`IsTruthy` over an absent key), and `AgentComposition`'s `config?.PlacedMineSync ?? false`.

### 14.1 Shape: publish, serial, map, apply

Exactly the three steps §7 called for:

1. **Publish, never a local id.** `PlacedMineController.ScanCaptures` (plugin, game thread,
   `PlacedMineController.cs`) walks the local area's trigger list once every 10 frames and finds
   a trigger with the trap gate set (`+0x2BC`) AND the party-placed marker set (`+0x314`) that
   this controller has not already accounted for (baselined at scope entry, already captured, or
   built here as a replica). It reads the record with `Odyssey.ReadPartyPlacedMine` — trap type,
   position, orientation, both DCs, faction — and hands it to the agent as a **capture ring**
   entry (`SharedBlock.PlacedMines.cs`, plugin -> agent), keyed by a per-process capture counter.
   The local dynamic object id never leaves the process (it rides only as a diagnostic field the
   agent never reads for identity).
2. **The host assigns one ordered serial.** `PlacedMineAuthority` (`KotorCoop.Session`, host-only,
   pure and thread-safe) is the canonical set for the host's current module. `Place` mints
   `_nextSerial++` — session-wide, monotonic, **never reused even across a world change** — keyed
   by `(ownerPeerId, captureSerial)` so a retransmitted placement intent is a no-op `Duplicate`
   rather than a second mine, refuses a placement outside the host's own current module (the
   host's copy is the only one that can ever damage a host-owned enemy — §14.4), and enforces the
   retail 15-mines-per-area cap (`CSWSArea::GetPlayerCanSetMines 0x005089D0`,
   `PlacedMineRosterPacket.MaxEntries`). A client's own placement goes out as a
   `PlacedMineIntentPacket` (`PacketType.PlacedMineIntent = 100`, retransmitted by
   `PlacedMineIntentSender` until `PlacedMineIntentAckPacket` (101) arrives); the host's own
   placement calls `Place` directly. The whole set goes back out as a `PlacedMineRosterPacket`
   (`PacketType.PlacedMineRoster = 102`) — generation-numbered so a client keeps only the newest,
   ascending by serial, carrying `HighestSerial` as the fence below which an absent serial means
   "removed" rather than "not announced yet" (`PlacedMineRosterRule.ShouldRemove`, unit-tested).
   Protocol version 66.
3. **Every engine keeps its own serial -> local id map.** `PartyPlacedMineIdentityMap` (§11,
   ten tests) is scoped to `(session generation, world epoch)`; a scope change (reconnect, module
   change, same-module reload) invalidates every mapping so a stale serial can never resolve
   after a world boundary. Registrations must arrive in non-decreasing serial order (matching how
   `PlacedMineReplicaPlanner` drives the mailbox, next). The placer's own mine is **registered**
   under the host's serial, never destroyed and rebuilt — its item consumption and any engine-side
   bookkeeping already happened once, on the placer, through the retail branch itself. Every other
   engine **builds a replica** through §14.2's route.

`KotorGameBridge.PlacedMines.cs` (agent) is the read/write half of the shared-block region
(`SharedBlock.PlacedMines.cs`, version 78, offsets `0x5A00..0x5BBC`, `TotalSize` grown to
`0x5C00` — both files are cross-checked by `SharedBlockNewRegionOffsetMirrorTests`): capture ring
head/entries, gone ring head/entries, the roster level (agent -> plugin) and the spawn mailbox
(agent -> plugin) with its ack (plugin -> agent). `IPlacedMineBridge.cs` mirrors the plugin's
`PlacedMineSpawnStatus` enum as `PlacedMineSpawnStatusCodes`; `PlacedMineRelayTests` cross-checks
the two numbers stay in sync, the same discipline `CheatKindSyncTests` and
`BehaviourOverrideBitsSyncTests` use for their own plugin/agent pairs.

### 14.2 Native construction route — VERIFIED(static), spawn itself NEEDS-LIVE

`Odyssey.TrySpawnPartyPlacedMine` (`Odyssey.PlacedMines.cs`) replays
`CSWSCreature::AIActionSetMine`'s no-target branch (§7.1, `0x0051A607..0x0051A76B`) step for
step, on the game thread only:

```
operator new(0x318)                              0x006FA7E6
CSWSTrigger::Constructor(id = 0x7F000000)         0x0058EAE0   (fresh dynamic id)
CSWSTrigger::SetCreator(creatorId)                0x0058D700   (writes +0x2E4, sets +0x314 = 1)
CExoString::operator=(+0x264, TrapScript)         0x005E5C50 / 0x005E5A90 / 0x005E5C20
+0x2D4 = TrapType; +0x2D8/+0x2DC = DCs; +0x2BC/+0x2C0/+0x2C4 = 1 (trap/detect/disarm gates)
CSWSObject::SetPosition(pos, 1, 1, 0)             0x004CD1A0
+0x2B8 = faction; +0x22C = TrapName (traps.2da INT column, when present)
CSWSTrigger::CreateNewGeometry(2.0, pos, area)    0x0058D210
CSWSTrigger::AddToArea(area, x, y, z, 1)          0x0058F030
CExoArrayList<ulong>::Add(area+0x12C, id)         0x00671C00
```

`VerifyPlacedMineRoute` decodes 13 call sites and 18 byte runs (every `E8` target and every
constant/opcode the sequence depends on: the `push 0x318`/`0x7F000000` immediates, every raw
field-offset `mov`, the `traps.2da` pointer chain `g_pRules->2da-set->traps`, the two literal
column-name strings) straight out of the loaded image and refuses on the first mismatch, exactly
like the GIT-mine and fire-gate routes. This has been checked against the installed GOG binary
(`VerifyPlacedMineRoute` logs `"AIActionSetMine route verified (13 calls, 18 byte runs)"` at
plugin startup when `placed_mine_sync` is on) but **the construction call sequence itself has
never been executed** — no live spawn has been attempted. That is the one piece of this feature
that is NEEDS-LIVE in the strong sense: everything else here is either pure logic (unit-tested)
or a byte-verified read.

One deliberate departure from the retail branch: a replica is given a recognisable tag
(`CSWSObject +0x018`, a `CExoString`, e.g. `COOP_PMINE_<serial>`) written through the engine's own
`CExoString` constructor/assignment before `AddToArea` registers the object anywhere — so it can
be told apart in a save, a script's `GetObjectByTag`, or a debugger. The placer's own mine (and
the `placemine` test verb) passes no tag and stays byte-for-byte like a retail placement. Faction
and both DCs come from the **placer's** record (they carry that engine's d20 roll and skill rank,
which cannot be recomputed on a peer); `TrapScript`/`TrapName` are resolved from **this**
instance's own `traps.2da` row, the engine's own way, so a modded `traps.2da` that differs between
peers still produces a locally-consistent object (the fields that matter for parity — position,
DCs, faction, trap type — are exactly what travelled over the wire).

`Odyssey.IsLivePartyPlacedMine` is the read-side gate every apply path uses before touching a
serial's local id: trigger vtable (`0x00749A38`), object id round-trips, `+0x2BC != 0` (trap
gate), `+0x314 != 0` (party-placed). `Odyssey.ListPartyPlacedMines` walks the area's own trigger
list (`+0x12C`/`+0x130`, the same list `GetNumMinesSetByPlayerParty 0x00508960` counts) rather
than a separately maintained cache, so it can never drift from what the engine itself considers a
party mine.

### 14.3 Fairness: one spawn request at a time, strictly ascending

`PartyPlacedMineIdentityMap.Register` accepts a scope's mappings only in non-decreasing serial
order, so the agent cannot hand the plugin a spawn request out of turn. `PlacedMineReplicaPlanner`
(pure, `PlacedMineReplicaPlannerTests`) is the fairness rule that follows from that constraint: it
always serves the **lowest still-unsettled** roster entry, skipping entries already settled
(spawned, registered, refused, or reported gone) regardless of how many are done — so in a busy
module where several party mines were placed close together, one entry stuck retrying (an area
not yet loaded, a transient refusal) never starves every later serial; the planner simply keeps
re-offering it each tick until it settles or the attempt cap gives up on it, while nothing ahead
of it in the fairness order is skipped. This is the placed-mine half of the "mine backlog
fairness" line in the review doc — the GIT-mine mirror's rotating slot list is the other half.

`PlacedMinePump` (agent, one in flight at a time) drives exactly one `RequestPlacedMineSpawn`
mailbox write per tick, waits up to 3 s for the plugin's ack (`AckTimeoutMs`), retries up to 20
times with a fixed 500 ms delay between attempts, and gives up (marks the serial settled locally)
rather than retrying forever — a single stubborn mine cannot wedge the whole roster. An own
capture that vanishes on the placer before the host's serial ever arrives is reported gone instead
of spawned (`PlacedMineStepKind.ReportGone`), so a mine detonated in the few hundred milliseconds
between placement and the host's ack never gets a phantom replica anywhere.

### 14.4 Single-authority damage rule

Enemies are host-owned everywhere else in this mod (creature census, phantom damage ledger); a
party-placed mine must follow the same rule or a walk-on detonation would either be lost (a
client's mirror overwrites its health from the census) or counted twice (the client's own trap
script AND the peer-damage ledger both apply it). The rule (`PlacedMineFireRule`,
`PlacedMineRulesTests`): **only the host's engine may ever run a party mine's trap script.**

`PlacedMineFireGateHook` is a five-byte call-site redirect — the only call to
`CSWSTrigger::GetCanFireMineOnObject 0x0058D4A0` in the image (confirmed with
`tools/xrefs.py 0x58d4a0`: one direct call, no address literal), at `0x0058D59E` inside
`CSWSTrigger::OnEnterMine 0x0058D570`. The stub is a handful of plain instructions with no managed
transition: it answers 0 ("do not fire") only when this instance is a live-session client (its
own dword, written every frame by `PlacedMineController.UpdateFireGate`) AND the trigger's
`+0x314` is set (party-placed) — a GIT mine is never touched. Verify-bytes-or-refuse on install;
the call is left alone entirely when `placed_mine_sync` is off. `OnEnterMine`'s own `je` on a
zero return skips straight to its epilogue, before the key lookup, the trap script, and the
event-11 self-destruction — a refused mine simply stays armed and untouched, exactly as if the
walker had missed it.

`PlacedMineFireRule.SuppressPartyMines` fails **open** (never suppress) when the host/client role
is not yet known, because refusing fire on an actual host would make every one of its mines
harmless everywhere — the unknown-role case must favour "the mine works" over "the mine is
inert," since the former is merely un-mirrored (today's behaviour) and the latter breaks the
single-player game the host is also playing. A client learns of a detonation only from the host's
roster (the serial disappears, or the entry falls below the announced `HighestSerial`) and removes
its own copy through the engine's own `DestroyObject`, exactly like the GIT-mine mirror (§6) —
never by reading its own (suppressed) `OnEnterMine`.

### 14.5 Transition flush

A client's replicas are session state, not world state: the host's copies are the canonical mines
of that module, and a client's replica exists only so the peer sees the hazard. `PluginHost.Frame`
calls `PlacedMineController.FlushOneReplica` from both the ordinary and mid-transition body-flush
sites, alongside the existing map-marker and phantom flushes, so a replica is never baked into a
client's own saved area as a permanent (and now un-suppressed, if the feature is later reconfigured
differently) party mine. The serial is retired from the identity map **before** the destroy call,
so the vanish scan can never misread the flush as a local detonation and echo a false "gone" back
to the host. The host keeps every one of its own copies across a transition — they are not
replicas, and destroying them would remove a live mine everywhere for no reason.

### 14.6 Dev console verbs and the live-test recipe

Three verbs exercise the whole path without the radial menu (dispatcher wiring:
`CoopCommandDispatcher.Mines.cs`, `ConsoleCommand.Mines.cs`, `ConsoleVerbCatalog.cs`,
`FuncInterpHook.cs`'s in-game verb allowlist; cheat kinds 90-92, `CheatKindSyncTests`-covered):

* `placemine <traps.2da row> [detectDC] [disarmDC]` — builds a party-placed mine at the local
  player's feet through the exact §14.2 sequence, AS IF a party member here had placed it. It is
  deliberately **not** recorded as a replica, so the very next capture scan publishes it exactly
  like a real placement; it consumes no item (a real placement's item consumption stays the
  engine's own business, on the placer, and is out of scope for this diagnostic). DCs default to
  10 (`ConsoleCommand.PlaceMineDefaultDc`) so the live recipe does not depend on any Demolitions
  rank.
* `minestate` — dumps every mapped serial (`#S -> local 0xID (own|replica, x,y)`), pending
  captures awaiting a host serial, the fire gate's install/suppress/refusal state, and
  `VerifyPlacedMineRoute`'s verdict, both to the log and as a one-line summary.
* `placedmineaction <recover|disarm> <serial>` — the §12 retail-skill diagnostic
  (`AIActionRecoverMine`/`AIActionDisarmMine` via the client action-menu entry points), retargeted
  at a session serial instead of a GIT object id: resolves the serial through this instance's own
  `PartyPlacedMineIdentityMap` first, then runs the same module/load-serial-bound request the
  GIT-mine `mineaction` verb uses.

**Recipe (two instances, GOG rig, `placed_mine_sync = 1` on BOTH — mismatched settings are
refused loudly by the fire gate: a suppressed client with no host to answer it would simply sit
on an inert mine forever).** Any module works; `tar_m10aa` is convenient because §8 already
establishes its GIT-mine layout as a landmark and it is known not to have any party-placed mines
of its own to confuse the baseline.

1. `host` / `connect`, both instances on `tar_m10aa`.
2. On the CLIENT: `placemine 3` (row 3, minor fragmentation mine, low blast radius). Expect the
   client's log: `placed mines: captured local placement #1 (local 0x..., creator 0x..., row 3,
   DC 10/10, faction ..., at X,Y,Z)`. This proves the construction route ran and the capture scan
   found it (§14.1 step 1, §14.2).
3. Within ~1-3 s, expect on the CLIENT: `placed mines: local placement #1 ... - reporting to the
   host`, then `placed mines: own placement #1 (local 0x...) is session mine #1 - kept, not
   rebuilt` (the host answered and this instance registered its own mine under the new serial —
   §14.1 step 3, "own capture" branch). On the HOST: `placed mines: peer <id>'s placement #1 is
   session mine #1 (row 3, DC 10/10, at X,Y,Z)`.
4. Within ~1-3 s more, expect on the HOST: `placed mines: asking the plugin to build session mine
   #1 (owner peer <id>, attempt 1)` then `placed mines: replica of session mine #1 spawned - built
   party-placed mine 0x... (row 3, DC 10/10, faction ...) at (X.XX, Y.YY, Z.ZZ), tag
   COOP_PMINE_1`. This is the one native write in the whole feature (§14.2) — the first thing to
   actually watch on screen: does a mine model appear at the reported coordinates on the HOST,
   at the position the CLIENT placed it at?
5. `minestate` on both: both must show `#1=0x...o` (own) on the placer and `#1=0x...r` (replica)
   on the peer, `route=ok` on both, `gate=installed` on both, `suppress=true` on the CLIENT only
   (the placer here) and `suppress=false` on the HOST.
6. **Walk-on detonation, single damage.** Only the HOST's copy can ever fire (§14.4), so walk the
   HOST's own party onto the HOST's replica's coordinates (from step 4's log line) until it goes
   off. Expect, in order:
   - the HOST takes the trap's damage exactly once (retail combat log / HP delta) — this is the
     ONLY place damage should appear anywhere for this mine;
   - the HOST's log reads `placed mines: mine #1 (local 0x...) is gone here (the engine removed
     it) - reported to the session`, then `placed mines: session mine #1 gone on the host -
     removed from the canonical set`;
   - within ~1-3 s the CLIENT's log reads `placed mines: mine #1 (local 0x...) removed on the
     session's authority (engine DestroyObject)` — this is the CLIENT's OWN placed mine (the
     object it originally built at its own location) being torn down by `ReconcileRoster`
     because the roster no longer lists serial #1, NOT because the client ever saw a detonation.
   - `minestate` on the CLIENT afterward shows the entry for #1 gone and `refusals` unchanged
     from before the walk (the client's fire gate never got a chance to refuse anything — the
     object was destroyed outright, not walked into); `minestate` on the HOST shows the same.
   Confirm the CLIENT's own party took no damage (it was never near either copy) — this, plus the
   single HP delta on the host, is the "damaged exactly once, on the host" property under test. If
   a host-side COMPANION triggers it instead of the player, confirm the damage lands on that
   companion's HP on the host and nowhere else.
7. **Disarm.** `placemine 3` again on either side for a fresh mine (repeat steps 2-5 for serial
   #2), then on either instance `minestate` to read its serial, then
   `placedmineaction disarm 2` on that SAME instance. Expect a retail skill-check outcome exactly
   as in §13 (a low-DC test mine at DC 10 with an unskilled actor should usually succeed): on
   success, the mine is destroyed on the acting instance, which reports it gone through the same
   path as step 6, and the peer removes its copy within ~1-3 s. Confirm the mine disappears on
   BOTH instances and stays gone after a warp-out/warp-back (mirrors §8 step 5's false-positive
   guard, now for party-placed mines).
8. **Off switch / mismatch refusal.** Set `placed_mine_sync = 0` on one side only and repeat step
   2 on that side: `placemine` still builds the native mine object (`PlaceTestMine` does not read
   the switch), but `PlacedMineController.OnFrame` returns immediately when `Enabled` is false —
   before the baseline, the capture scan, or anything else — so the mine is never captured and
   never reported; its `minestate` shows the fire gate itself absent (`gate=absent`, since
   `PlacedMineFireGateHook.Install` is only ever called when `Enabled`). `minestate` on the OTHER
   (still-enabled) side must show no pending replica and no log line about it at all — the off
   side never speaks, so the on side never even hears about the placement. This is the expected
   degrade, not a bug: the feature is symmetric-only, and an asymmetric config is a
   misconfiguration to flag, not a scenario to silently paper over.

Confirm across the whole recipe: the host's own placements never wait for an ack (no
`PlacedMineIntentPacket` needed for the host's own mines — `Place` is called directly, in-process
— §14.1 step 2), a party-placed mine that was already active in the area when a session begins
(placed in a prior single-player play session, or by a script before the session started) is
baselined and never published (`PlacedMineController.Baseline`, mirrors the GIT-mine baseline
rule in §6), and `route=ok` never flips to `route=REFUSED` on the installed GOG build — a refusal
there means a different executable and every native write in §14.2 stops before touching memory.

**What is still unverified after this pass:** every native write in §14.2 (the spawn itself has
never run), whether `SetCreator`'s party-membership check accepts a plugin-supplied creator id the
same way it accepts the retail placer, whether the CreateNewGeometry/AddToArea pair produces a
mine that looks and fires identically to a retail one, and the live timing of the fire-gate
refusal counter under an actual walk-on. Recovery's item reward for a party-placed mine is
untested even in single life (§12-13's live attempts were both against GIT mines); a
party-placed mine's own `traps.2da` item reward, if any, follows the same
`script_item_sync`/`party_pool_sync` ambiguity §13 already flags for GIT-mine recovery.

### 14.7 Live run (2026-09-24, `80439e1`, `tar_m10aa`, both `placed_mine_sync = 1`)

Both engines logged the fire gate at `0x58D59E` and `AIActionSetMine route verified (13 calls,
18 byte runs)`; the joiner logged itself inert as a client.

- **Capture -> serial -> replica (steps 2-5).** Joiner `placemine 3`: `captured local placement
  #1 (local 0x148, ... at 113.00,116.00,2.25)`, then `own placement #1 (local 0x148) is session
  mine #1 - kept, not rebuilt`. Host: `peer 2's placement #1 is session mine #1`, then `replica of
  session mine #1 spawned - built party-placed mine 0x145 ... at (113.00, 116.00, 2.25), tag
  COOP_PMINE_1`, the first live run of the native construction route, with no fault. `minestate`:
  host `#1=0x145r` gate armed, joiner `#1=0x148o` gate inert.
- **Client fire gate.** The joiner stood on its own mine: `fire gate refused a party-placed mine on
  this client (total 1); the host's copy decides`. No damage on the joiner.
- **Removal propagation.** `destroyobject 0x145` on the host: host `mine #1 (local 0x145) is gone
  here - reported to the session` at 17:14:39.099; joiner `mine #1 (local 0x148) removed on the
  session's authority (engine DestroyObject)` at 17:14:39.216. Both `minestate`s then 0 mapped.

**Recipe correction (step 6).** Walking the HOST onto the replica does nothing, and correctly: the
mine carries the party's faction (0) and a party's mines fire only on hostiles. The walk-on test
needs a hostile crossing the host's replica; the Vulkars in this save were not hostile and
`spawncreature n_sithsoldier001` refused here (failure 9), so detonation-by-enemy and
damage-exactly-once on the host remain unobserved.

**Open finding.** `placedmineaction recover|disarm` queues the retail skill request but the engine
never performs it, on the host's replica AND on the joiner's own `placemine`-built mine. Both were
built by this route's replay rather than by a real placement, so the retail action evidently needs
state the replay does not set (the likeliest candidate is the trap's per-creature detection
record). A retail-placed mine is unaffected on its placer; recovering a PARTNER's mine is the case
that stays open.
