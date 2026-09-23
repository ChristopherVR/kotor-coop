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

