# Door state sync: story re-locks and re-closes — trace, content audit, live-test recipe (2026-09-24)

Roadmap ask: reconcile `docs/limitations.md`'s "a door or chest locked again by the story —
Not shared, by design" against `docs/review-seamless-2026-09-18.md`'s "Door state, including
story re-locks — Partial; needs live", and determine precisely whether a host-side script
`SetLocked(door, TRUE)` / `ActionCloseDoor(door)` reaches the joiner today.

Tag key, same as the rest of this research trail: **VERIFIED** (read from the binary, or
measured against the real game files) · **INFERRED** (reasoned, not observed) · **NEEDS-LIVE**
(must be confirmed on the running game before shipping). Nothing in this pass ran the game —
read-only static RE plus offline game-file audit; the rig was not touched.

## 1. Answer, up front

**Yes, both reach the joiner today, and have since `934c18e` (2026-09-21, "fix: synchronize
door relocks").** The two limitations-doc lines describe two different eras of the same
channel and are not actually in tension once dated: `docs/limitations.md`'s "Not shared, by
design" text was written when `DoorFlags`'s value channel was deliberately monotonic (see its
own git history below) and was never updated after the fix landed. `docs/review-*`'s "Partial;
needs live" is the accurate one — the mechanism is implemented and unit-tested, but no pass has
ever driven a *real shipped story script* through it; every prior door-sync live pass (`cinematics.md`
§8.3c, the 2026-09-17 eight-door soak) exercised ordinary player opens, never a re-lock.

This pass closes the gap between "implemented" and "known to work" three ways: (1) a full static
trace of the mechanism with citations, confirming nothing about it is script-specific — it
reaches a re-lock/re-close from *any* source, not just the two named NWScript calls; (2) an
offline content audit that names four real, early, reachable shipped scripts that do exactly
what the roadmap asked about; (3) a two-instance live-test recipe using this repo's own console
verbs, ready for the next live session. No code changed — the mechanism did not need fixing,
only proving and documenting.

## 2. The mechanism, traced end to end

### 2.1 Detection is a poll of the engine's own committed fields, not a script hook

`KotorGameBridge.CollectChangedDoorState()` (`src/KotorCoop.Memory/KotorGameBridge.Doors.cs`)
walks every live `CSWSDoor` (vtable `0x007498B0`) once a second and reads three fields per door:

| Field | Offset | VERIFIED by |
| --- | --- | --- |
| committed open state (0 closed, 1/2 open, 3 destroyed) | `+0x2CC` | `cinematics.md` §8 (`CSWSDoor::MoveToNextOpenState 0x00589680` commits pending→current here), `engine-api.md` |
| pending open state | `+0x2CD` | same — the byte `SetOpenState 0x00589600` writes before the swing commits |
| locked byte | `+0x2C4` | `placeables-containers.md` §1 ("the door lock write, `Odyssey.UnlockDoor` writes door `+0x2C4`") |

A door is only reported when `pending == open` (the swing has settled — mid-animation doors are
skipped and picked up on a later scan) and the `(open, locked)` tuple differs from the last
scan's shadow (`_doorShadow`). **This is cause-agnostic.** It does not matter whether the
committed state changed because a player clicked the door, because `ActionCloseDoor` ran from a
trigger's `OnEnter`, because `SetLocked` ran from a dialogue action node, or because an NPC's
`OnUserDefined` handler fired it — every one of those paths ends by the engine writing the same
two fields, and the scan cannot tell them apart (nor does it need to). This directly answers the
roadmap's question: **yes**, `SetLocked`/`ActionCloseDoor`, wherever they are called from, reach
the joiner, because the channel was never watching for those two calls specifically.

### 2.2 The value channel used to be monotonic; it no longer is

`git log` on `src/KotorCoop.Memory/DoorFlags.cs`: before `934c18e`, the value layout's own
docstring said "a door only ever advances: closed to open, locked to unlocked, anything to
destroyed. Closing and re-locking are deliberately not replicated." `KotorGameBridge.Doors.cs`
tracked *first-seen* locked state so it could report only "unlocked during play", and
`TryWriteDoorStates` refused any `flag.Value <= 0` (a "went backwards" sentinel under the old
max-merge policy). `934c18e` removed all three: the value layout comment now reads "bits 0-1
are the engine's open state..., bit 2 is the current unlocked state... Zero is a meaningful
closed-and-locked state", the baseline-locked bookkeeping is gone, and `TryWriteDoorStates`
accepts any value `0..7`. This is exactly the change the roadmap's "Not shared, by design" text
is describing — from before the fix.

### 2.3 Ordering: per-key Lamport clock with a host tie-break, not a bare max

`EventFlagStore` (`src/KotorCoop.Session/EventFlagStore.cs`) replaced value-only "higher wins"
with a per-key `(Serial, OriginPeerId)` Lamport clock (protocol 31, well before this pass — see
`docs/research/quest-sync.md` §2-4). `Set` — called for every *genuine local transition*
`CollectChangedDoorState` reports — always mints the next serial and always wins locally,
whichever direction the value moved. A remote op is adopted only when its `(Serial,
OriginPeerId)` is strictly newer than what is held; an exact tie (both sides independently
minting the same serial number before hearing from each other) favours the lower peer id, i.e.
the host (`CoopHost.HostPeerId == 1`). This is the literal host-authoritative rule the roadmap
asked for, already built: whichever change actually happened *later* wins, and in the one case
where "later" is ambiguous (an exact serial tie), the host wins.

### 2.4 Apply: the engine's own calls, never a state poke

`WorldStatePump.PumpDoors` (`src/KotorCoop.Session/Pumps/WorldStatePump.cs`) announces every
local change (`session.SetFlag`) and unconditionally republishes the session's current door
truth for the local module into the plugin's shared block (`bridge.TryWriteDoorStates`) every
tick. `DoorController.Apply` (`src/KotorCoop.Plugin/DoorController.cs`), on the plugin's
game-thread frame pump:

1. Never touches a swing already in flight (`pending != current`) or a destroyed door
   (`current == 3`).
2. Skips a value the `DoorDesiredStateTracker` has already applied — **this is the no-oscillation
   guard**: once a given `(open, locked)` pair has been pushed to the engine, it is pushed only
   once, and is re-armed only when a genuinely *different* value arrives from the session (a
   fresh Lamport serial). Nothing in this loop asserts a value repeatedly, so nothing here can
   fight a later local change out from under itself, and nothing here re-issues a swing that
   already landed.
3. Applies lock state with a plain byte write to `+0x2C4` (`Odyssey.SetDoorLocked` — the same
   field the engine's own lock/unlock does — `placeables-containers.md` §1, VERIFIED) — this is
   what makes a RE-lock reach the peer, not just an unlock.
4. Applies a changed open state through `CSWSDoor::SetOpenState(state, deferred=1)`
   (`0x00589600`, VERIFIED by disassembly, `engine-api.md`), the same deferred path the real
   engine's own `OpenDoor` uses, so the swing animation plays and the walkmesh updates when the
   engine commits it — never a raw write to `+0x2CC` (which moves the collision without the
   model, per `Odyssey.Doors.cs`'s own warning). A fallback (`CommitDoorOpenState`,
   `MoveToNextOpenState 0x00589680`, VERIFIED) forces the commit if the swing never lands within
   three seconds.

Every address this loop calls is VERIFIED(static) and was already cited in `engine-api.md`
before this pass; nothing new needed disassembling.

### 2.5 No oscillation — proven at two levels, not just argued

* **Unit, `DoorFlagsTests.NewerStoryRelockWinsAndOldSnapshotCannotReopenIt`**: an owner (host)
  sets a door open+unlocked, a peer merges that snapshot, the owner then re-locks+closes it
  (serial advances), the peer merges the NEW snapshot (adopts the re-lock), and then — the
  actual oscillation shape — re-merges the STALE first snapshot. The stale merge cannot win: the
  peer still reads the re-locked value. This is `EventFlagStore`'s own guarantee (a stale replay
  can never regress a newer value), exercised with door-shaped data.
* **Unit, `DoorFlagsTests.UnchangedDesiredStateDoesNotFightLocalStoryChanges`**: pins
  `DoorDesiredStateTracker`'s exact contract — applying the same value twice is a no-op, a new
  value re-arms it, and a module change (`Clear`) resets it for reused object ids.

**The one residual race this design accepts, deliberately**: if a peer's own player clicks to
open the *same* door in the same real-time window the host's script re-locks it, whichever
change's `CollectChangedDoorState` scan runs first mints the lower serial and loses — ordinary
last-writer-wins, the same rule that already governs two players opening a chest or advancing a
quest global in the same tick (`quest-sync.md` §4). This is not the failure mode the roadmap
named ("a peer must not re-open a door the host's story just re-locked") — that failure mode
would be the peer's *own already-settled* state getting mis-reported as a fresh local change
after `DoorController` applies the host's re-lock, and §2.1's "genuinely differs from the shadow"
gate plus `EventFlagStore.Set`'s "no-op on an unchanged value" (both pre-existing) rule that out:
once the peer's engine reaches the re-locked state, re-scanning it reports no local change (the
shadow already agrees), and even if it did, minting a *value-identical* op is a no-op in the
store. No code change was needed for either guarantee; both already existed before this pass for
other reasons and happen to cover this case too.

### 2.6 Compatibility gating: already covered, no new switch

`ProtocolConstants.Version` requires an *exact* match to connect at all
(`CoopHost.Connection.cs`, `CoopClient.Connection.cs` — any mismatch is rejected at `Hello`, never
silently degraded). Door sync has no `coop.ini` key and no `SessionCapabilities` bit of its own
beyond the pre-existing `SessionCapabilities.Doors`, which both sides negotiate the same way
every other capability does. Because the wire protocol requires identical versions to connect,
there is no scenario where one peer runs the pre-`934c18e` monotonic semantics against a peer
running the current bidirectional ones — they simply cannot form a session. No `BehaviourDigest`
flag, `coop.ini` key, or protocol bump was added or is needed for this pass.

## 3. Content audit: real shipped scripts that re-lock or re-close a door

**Tool.** `tools/localvaraudit.py`'s existing abstract interpreter (`Interp`, built for the
per-object-local-variable audit in `quest-sync.md` §8.2) walks every compiled script's NCS stack
symbolically — constants, `OBJECT_SELF`, `GetObjectByTag("tag")` results tracked through copies,
subroutine calls analysed with the caller's real stack — and already resolves which routine
number an `ACTION` instruction calls from the game's own shipped `nwscript.nss` (read out of
`chitin.key`, so the routine table is this exact GOG 1.0.3 build's own, not a remembered
ordinal). It was written to watch `GetLocalBoolean`/`SetLocalBoolean`/`GetLocalNumber`/
`SetLocalNumber` (679-682); redirecting its `LOCAL_ROUTINES` watch-set and `Interp.record` to a
different set of routine numbers reuses the entire interpreter with no changes to
`tools/localvaraudit.py` itself. Confirmed against this build's own `nwscript.nss`:

| Routine # | Name | Confirmed this build (`load_signatures`) |
| --- | --- | --- |
| 43 | `ActionOpenDoor(object)` | yes |
| 44 | `ActionCloseDoor(object)` | yes |
| 324 | `SetLocked(object, int)` | yes |
| 325 | `GetLocked(object)` | yes |
| 483 | `ActionUnlockObject(object)` | yes |
| 484 | `ActionLockObject(object)` | yes |

**Method.** For every compiled script (`.ncs`) in the Endar Spire (`end_m01aa`, `end_m01ab`),
Taris (`tar_m02*`-`tar_m11*`) and Dantooine (`danm13`-`danm16`) modules, run the interpreter and
report every `ActionCloseDoor`/`SetLocked(…, TRUE)`/`ActionLockObject` call, with the resolved
target (a literal tag from `GetObjectByTag`, or `SELF`) and every GIT-object/dialogue/trigger/area
event that owns the script (the same `module_owners()` used by the local-var audit, which reads
every placed object's blueprint event fields, the conversation each names, and the area/module
event fields). One important negative result first: **a door's own `OnLock`/`OnClosed` template
fields are not a reliable audit signal** — every door template in these 23 modules has them
empty, even doors that genuinely do get re-locked (below), because nothing in the shipped
content bothers to react to its own lock/close event; the re-lock has to be found by tracing
*callers*, which is exactly what this audit does and a GFF field scan cannot.

**Result** (23 modules scanned; 18 had at least one hit): 68 `ActionCloseDoor`/`SetLocked(…,
TRUE)`/`ActionLockObject` call sites total, of which 35 name a concrete door tag via
`GetObjectByTag("…")` (the rest target `SELF` from inside the door's own event handler, or an
unresolved call such as `GetNearestObject()` — excluded here as not concretely retargetable for a
recipe). Four picked below for reachability, a single-command trigger where one exists, and
coverage of every "owner" shape the audit found (a door's own hook, a trigger's `OnEnter`, and a
dialogue action node):

| # | Module | Door tag | Script | Owner (what runs it) | Effect |
| --- | --- | --- | --- | --- | --- |
| A | `tar_m02ac` (Taris Sith Base, upper level) | `tar02_labdoor` | `k_ptar_alertzelk` | the door's own `OnOpen` | `SetLocked(SELF, TRUE)` — **opening it re-locks it in the same engine call**, no close |
| B | `tar_m02ac` (same module) | `tar02_cantdoor` | `k_ptar_autoclose` | the door's own `OnHeartbeat` | `ActionCloseDoor(SELF)` every heartbeat while it is open — a self-closing door |
| C | `end_m01aa` (Endar Spire) | `end_door01` | `k_pend_traskdl41` | a node action in Trask's own conversation (`end_trask01.dlg`) | `ActionCloseDoor` + `SetLocked(…, TRUE)` on `end_door01` — the earliest possible story re-lock in the game |
| D | `tar_m04aa` (Black Vulkar base entrance) | `tar04_maingate` | `k_ptar_closegate` | trigger `ta04_closegate`'s `OnEnter` | `ActionCloseDoor(tar04_maingate)` — walking past the trigger seals the gate behind you |

`tar02_labdoor` (candidate A) ships `Locked=1, Lockable=1`; `tar02_cantdoor` (candidate B) ships
`Locked=1, Lockable=0` and is also a real module transition (`LinkedToModule=tar_m02ae`) with its
own `OnFailToOpen = k_ptar_sithalert` (an alarm script) — both confirmed by dumping their `.utd`
templates out of `tar_m02ac_s.rim`. `end_door01` (candidate C) is an ordinary, unlinked door
(`sw_door_test001` template). `tar04_maingate` (candidate D) ships `Locked=1, Lockable=0`.

Full reproduction (read-only, offline, ~15s per module group):

```python
import sys; sys.path.insert(0, "tools")
import kotorfiles as kf, localvaraudit as lva

TARGET = {44: "ActionCloseDoor", 324: "SetLocked", 484: "ActionLockObject"}
lva.LOCAL_ROUTINES = set(TARGET)
def record(self, pc, routine, args):
    obj = args[0] if args else None
    a1 = args[1] if len(args) > 1 else None
    self.sink.append({"script": self.script, "routine": TARGET.get(routine, routine),
                       "obj": lva.describe(obj),
                       "arg1": a1[1] if a1 and a1[0] == "int" else lva.describe(a1)})
lva.Interp.record = record

game = kf.Game(r"C:\GOG Games\Star Wars KOTOR (GOG)")
sigs = lva.load_signatures(game)
mod = lva.Module(game, "tar_m02ac")
objects, owners = lva.module_owners(mod)
for script in mod.resources("ncs"):
    sink, err = lva.analyse_script(mod.read(script + ".ncs"), sigs, script)
    for h in sink:
        if h["routine"] in ("ActionCloseDoor", "SetLocked", "ActionLockObject"):
            print(script, h["routine"], h["obj"], h["arg1"], owners.get(script, [])[:2])
```

## 4. Live-test recipe (two instances, GOG rig) — NEEDS-LIVE

Neither instance needs to be at any particular save; every candidate below is a placed GIT
object that exists the moment its module loads, so `warp` reaches it directly.

**Primary test (candidates A + B — single console verb, no dialogue navigation, exercises both
halves of the mechanism independently):**

1. Connect both instances (host + joiner), either order.
2. On **both** instances: `warp tar_m02ac` (host first, joiner follows or warps independently —
   either is fine for this test since it only needs both engines to have the module loaded, not
   a synchronised crossing).
3. On the **HOST**: `opendoor tar02_labdoor`. This calls the real engine `OpenDoor`
   (`Odyssey.OpenDoorFully` — confirmed it ignores the lock, `engine-api.md`), which opens the
   door **and runs its `OnOpen` script in the same call**, re-locking it
   (`k_ptar_alertzelk` → `SetLocked(SELF, TRUE)`).
4. Within the next door scan (≤1s) plus the next `DoorController` pass (≤40 frames), check the
   **JOINER's** copy of `tar02_labdoor`: it should render open (the swing already played on the
   host's `OpenDoor` call, which the peer also plays via `SetDoorOpenState`) but **locked** —
   confirm by attempting to interact with it a second time on the joiner (should refuse as
   locked) or by reading its state through `resolve <id>`/the plugin log line
   (`"door 0x...: swinging ... -> ... on the session's authority"` for the open half; the lock
   write itself is silent, matching `DoorController`'s comment that lock application has no log
   line of its own — add one locally if the live pass wants to see it fire).
5. On the **HOST**: `opendoor tar02_cantdoor`. Wait roughly 5-10 seconds (one `OnHeartbeat`
   interval) without touching the door again.
6. Confirm the **HOST's** own copy auto-closes (`k_ptar_autoclose`), then confirm the **JOINER's**
   copy closes to match within the same poll windows as step 4. This exercises `ActionCloseDoor`
   specifically (open state regressing, not just the lock bit).

**Secondary test (candidate C — the actual earliest story re-lock, natural dialogue path):**

7. On the **HOST** (fresh Endar Spire start, or `warp end_m01aa`): let the pre-existing
   `end_trask01` conversation with Trask run its course (`startdialog end_trask` if not already
   in it) up to the point the game seals the corridor behind you. `end_door01` should have been
   open (the path you just walked through) and end closed+locked on the host.
8. Confirm the **JOINER's** copy of `end_door01` follows to closed+locked within the same poll
   windows, and does not need to be near the door or in the conversation to receive it (the
   channel is module-scoped, not proximity-scoped).

**Tertiary test (candidate D — a trigger-driven close, for coverage of that owner shape):**

9. On the **HOST**: `warp tar_m04aa`, then walk a few metres past the entrance until the
   `ta04_closegate` trigger fires (`k_ptar_closegate` → `ActionCloseDoor(tar04_maingate)`).
   Confirm the **JOINER's** copy of `tar04_maingate` closes to match.

**No-oscillation check, all of the above**: after each relock/close lands on the joiner, have the
joiner's player attempt the ordinary interaction (click the door / `usedoor`) and confirm it is
refused as locked/closed rather than silently reopening, and that it does NOT flip back open on
its own on a later poll (watch two or three door-scan intervals, ~2-3s, with nobody touching it).
A genuine player-initiated open from either side afterward (a real click, or `opendoor` again)
should still work and should still propagate — the mechanism does not lock the door against
*legitimate* re-opening, only against the joiner's own stale poll re-asserting a value nobody
actually changed.

## 5. What remains open

* Everything in §4 is NEEDS-LIVE — this pass proves the mechanism statically and finds real
  reachable content, but nobody has watched a real story re-lock cross the network on the running
  game.
* **Containers/placeables were a separate gap when this pass was first written — closed the
  same day.** §6 below extends the exact model this section describes to `PlaceableFlags.cs` /
  `KotorGameBridge.Placeables.cs` / `PlaceableController.cs`, including re-deriving
  `WasEverClosed`'s role the way this paragraph anticipated. `docs/limitations.md`'s "chest" row
  is updated accordingly (no longer "Not shared, by design").

## 6. Extending the model to placeables/containers (2026-09-24, same-day follow-up)

Closing the gap this section flagged: `PlaceableFlags.cs` / `KotorGameBridge.Placeables.cs` /
`PlaceableController.cs` now carry a placeable's open and locked state both ways, the same model
§§2.1-2.5 above describe for doors, so a story re-lock or a real container close reaches every
peer honestly. Nothing here changes the wire protocol or the shared-block layout — every field
this pass touches already existed (the placeable slot's byte `+0x04`/`+0x05` were already
booleans, not derived from a "has ever advanced" bit).

### 6.1 Detection: the same cause-agnostic poll, now honest about the lock bit too

`KotorGameBridge.CollectChangedPlaceableState()` (`src/KotorCoop.Memory/KotorGameBridge.Placeables.cs`)
already polled every live `CSWSPlaceable` (vtable `0x007494D0`) once a second and diffed
`(open, locked, trapped)` against a per-module shadow — the shadow-diff itself, and its
cause-agnostic character (a real player's click and a script's `SetLocked` write the same fields
and cannot be told apart, exactly §2.1's point for doors), predate this pass. What changed is
what got REPORTED once a genuine change fired: the old code computed `unlockedDuringPlay` —
`locked == 0 && <the FIRST lock value ever seen this module>` — so a placeable that started
locked, was unlocked, and was later RE-locked by a script reported `unlockedDuringPlay = false`
at the moment of the re-lock (the honest transition), but `TryWritePlaceableStates` then dropped
that report on the floor (next paragraph) and, more fundamentally, nothing on the apply side ever
acted on "the peer says lock this back up". The fix drops the baseline-relative computation
entirely and reports the CURRENT `locked == 0` reading directly — `PlaceableFlags.MakeValue(open
!= 0, locked == 0, disarmedDuringPlay)` — the same "carry the actual state, including a
placeable that shipped already unlocked" choice `CollectChangedDoorState` already made for doors.
Disarm (bit 2) is untouched and stays relative to the module's own baseline: nothing shipped
ever re-arms a trap, so there is no reverse direction to carry.

### 6.2 Transport: no new plumbing — the value floor was the only real gate

`WorldStatePump.PumpPlaceables` (`src/KotorCoop.Session/Pumps/WorldStatePump.cs`) already called
`session.SetFlag` unconditionally for every entry `CollectChangedPlaceableState` reported, riding
the SAME per-key Lamport `EventFlagStore` doors use (`PumpDoors` and `PumpPlaceables` are two
call sites over one store) — this was already true before today's pass; placeables were never on
a *different* store, just a channel that never told the store about a re-lock. The only actual
value-floor gates were in `KotorGameBridge.Placeables.cs`'s own `TryWritePlaceableStates`
(`flag.Value <= 0` — under the old model, 0 meant "nothing to tell the peer") and
`MineSlotPlanner.SelectPlaceables` (the same `<= 0` filter, used to choose which placeables fit
in the shared block's 32-slot list when a module holds more than that). Both now accept the full
`0..7` range, `< 0 or > 7`, matching `TryWriteDoorStates`'s own bound — 0 ("closed, locked, not
disarmed") is a real state a story re-lock can produce, not "no update yet".

### 6.3 Apply: the engine's own calls, gated by a new no-oscillation tracker

`PlaceableController.Apply` (`src/KotorCoop.Plugin/PlaceableController.cs`) now:

1. Disarms a trap (unchanged, forward-only, unaffected by this pass).
2. Gates everything else on a NEW `PlaceableDesiredStateTracker.IsNew(id, open, unlocked)` —
   the placeable twin of `DoorDesiredStateTracker`, same shape, same job: apply a genuinely new
   `(open, unlocked)` tuple once, so a stale re-published snapshot from before a local scan
   catches up with its own recent story change (or the closing player's own panel) cannot fight
   that change out from under itself, the exact guarantee §2.4 describes for
   `DoorController.Apply`.
3. Writes the lock field either direction — `Odyssey.UnlockPlaceable` (existing) or the new
   `Odyssey.LockPlaceable` (`*(int*)(placeable + 0x260) = 1`, the same plain-field-write pattern
   `Odyssey.SetDoorLocked` already uses for doors; VERIFIED offset, `placeables-containers.md`
   §1, unchanged by this pass) — unconditionally on every genuinely new tuple, same as
   `DoorController.Apply` always calls `SetDoorLocked` regardless of whether the bit actually
   moved.
4. Opens or closes through the engine's own calls, only when the live `Odyssey.IsPlaceableOpen`
   reading actually disagrees with the desired value (mirroring `DoorController.Apply`'s
   `if (desired.OpenState == current) return;` before it swings a door):
   - **open**: `OdysseyPlaceables.PlayContainerOpenAnimation` (VERIFIED `0x004CCF60`,
     animation id `0x275B` — `container-sync.md` §1.2, confirmed live 2026-08-22, no longer the
     "NEEDS-LIVE guessed-ABI" call `placeables-containers.md` §1 originally described) then
     `Odyssey.SetPlaceableOpen`.
   - **close**: `OdysseyPlaceables.PlayContainerCloseAnimation` (same call, animation id
     `0x275C`) then `OdysseyPlaceables.ClearPlaceableOpenFlag` — the exact pair the
     container-sync interaction-lock mirror (`InteractionLockController
     .ReconcileContainerAnimations`) already uses for a peer's claim release, now reused for
     the durable channel's own close.

### 6.4 `WasEverClosed` retired from this path, not ripped out

The 2026-08-22 `ContainerCloseHook.WasEverClosed` latch existed because the OLD monotonic
channel could not itself express "closed" — once it had ever seen a container open, it kept
re-asserting that forever, and the latch was the only way a real close could stick (permanently
suppressing the re-assertion for that id, for the rest of the module load). That is no longer
true: the channel itself now carries an honest closed value, and `PlaceableDesiredStateTracker`
gives the exact no-oscillation guarantee needed WITHOUT the latch's own failure mode — a
PERMANENT block that would have refused a legitimate LATER re-open of the same container (a
second player looting it again, or a script re-opening it) forever. `PlaceableController.Apply`
no longer calls `WasEverClosed`. The hook itself (`ContainerCloseHook.Install`, its detour on
`CloseInventory`, and `Register`) is left installed rather than removed — it is a separately
verified, working splice with no correctness cost to leaving it running, and unhooking a live
detour is a bigger, separate change than this pass's own scope; `ClearPlaceableOpenFlag`'s own
call to `Register` (from the UNRELATED interaction-lock claim mirror, §6.6) keeps it fed
regardless. See the updated remarks in `ContainerCloseHook.cs` and `OdysseyPlaceables.cs`.

### 6.5 No oscillation — proven the same way doors are

`PlaceableFlagsTests.cs` gained the placeable twins of the two door tests §2.5 cites:
`NewerStoryRelockWinsAndOldSnapshotCannotReopenIt` (an owner opens+unlocks, a peer merges that
snapshot, the owner then closes+re-locks with a fresh serial, the peer adopts the re-lock, and a
re-merge of the STALE first snapshot cannot undo it — `EventFlagStore`'s own guarantee, exercised
with placeable-shaped data) and `UnchangedDesiredStateDoesNotFightLocalStoryChanges` (pins
`PlaceableDesiredStateTracker`'s exact contract: the same tuple twice is a no-op, a genuinely new
one — including a re-open AFTER a close — re-arms it, and `Clear()` resets it for a module change
that reuses object ids). The residual race is the same one §2.5 accepts for doors: if a peer's
own click opens the same container in the same real-time window a host script re-locks it,
whichever change's scan runs first mints the lower serial and loses — ordinary last-writer-wins,
unchanged from every other channel on this store.

### 6.6 What this pass does NOT touch — container-loot behaviour is a separate, faster channel

`docs/research/container-sync.md` describes a completely different mechanism for "who is
looting this container right now": the interaction-lock claim (`InteractionLockController`,
`ContainerUseHook`, `ContainerLockArbiter`), which rides the fixed 150ms combat pump
UNCONDITIONALLY, arbitrates exclusivity (exactly one opener wins a contested click), and mirrors
the open/close ANIMATION on every other peer's own copy the instant a claim is taken or released
— live-measured at ~250-330ms end to end (§10.2-10.3 of that doc). The channel this pass changes
(`PlaceableFlags`/`PlaceableController`, §5.3 of `container-sync.md`) is the slower, DURABLE
truth underneath it — "what a late joiner or a post-reload resync should see" — and was never
what made two clicks on one chest resolve safely, nor what the loot mailbox (24-slot acknowledged
replay, delayed materialised-drop owner mapping) depends on. Neither of those is touched here:
this pass only makes the durable open/locked STATE bidirectional, the same relationship
`container-sync.md` §10.3 already documents ("`container_fast_pump` only moves `PumpPlaceables`
... a channel that never touches a close" — now historical; it does touch a close, but the
interaction-lock claim mirror remains the one that actually keeps two live players' screens in
sync within a fraction of a second).

### 6.7 Content audit: real shipped scripts that re-lock or close a PLACEABLE (not a door)

Reused `tools/localvaraudit.py`'s interpreter exactly as §3 describes, redirecting its routine
watch-set to `SetLocked` (324), `ActionLockObject` (484) and `ActionUnlockObject` (483) —
`ActionCloseDoor` (44) was included too, to CONFIRM it never targets a placeable in this build
rather than assume it — and cross-referencing every resolved `GetObjectByTag("tag")` target (or a
`SELF` call from inside the object's own event handler) against that module's own GIT placeable
list (`module_owners()`'s `ext == "utp"`) rather than door-relock.md's door list. Scanned every
module shipped in this build (`modules/*.rim`, all 117), not just the Endar Spire/Taris/Dantooine
subset §3 used.

**Result:** 26 `SetLocked`/`ActionLockObject`/`ActionUnlockObject` call sites resolve to a
placeable target (0 `ActionCloseDoor` hits — confirming it is door-only here, as `nwscript.nss`'s
own description implies). Three picked below for reachability via this repo's own console verbs,
a real re-lock/re-close effect (not just an unlock), and coverage of the owner shapes the audit
found (a heartbeat toggle, a one-shot self-lock on use, and a real loot-panel close handler):

| # | Module | Placeable tag | Ships | Script | Owner (what runs it) | Effect |
| --- | --- | --- | --- | --- | --- | --- |
| A | `tat_m20aa` (Tatooine, Tusken camp) | `tressndpplbin` (7 copies, same tag) | `Locked=0, Lockable=0, HasInventory=1` | `k_ptat_tusklock` | the placeable's own `OnHeartbeat` | Alternates `ActionLockObject(SELF)` / `ActionUnlockObject(SELF)` every heartbeat while nobody holds it — a self-toggling container, the placeable twin of the door audit's `tar02_cantdoor` auto-close candidate |
| B | `tar_m03ad` (Taris, upper apartments) | `holo1` (also `holo2`-`holo6`, same script pattern) | `Locked=0, Lockable=1, HasInventory=0` | `tar03_holo1` | the placeable's own `OnUsed` | `SetLocked(SELF, TRUE)` — a single "use" (click) locks it permanently; no open/close animation involved (not a container), a clean single-bit re-lock test |
| C | `sta_m45ab` (Star Forge) | `sta_armor_container` | `Locked=1, Lockable=1, HasInventory=1` | `k_psta_binopen` | the placeable's own `OnClosed` | `SetLocked(SELF, TRUE)` the moment the loot panel finishes closing — the real "close→re-lock" shape the roadmap asked about, exercised by the engine's own `CloseInventory` (the same call the `closeplaceable` staging verb makes, `OdysseyPlaceables.CloseInventoryReal`) |

Candidate C ships locked, and unlike `opendoor` (VERIFIED to ignore a door's lock,
`engine-api.md`), `useplaceable` sends the SAME network `UseObject` input a real click does
(`CheatController.World.cs`'s own remarks) — `OpenInventory`'s own disassembly (`tools/disasm.py
0x587420 350`) never reads the locked field at all, so the lock check happens upstream, in the
action-queue path a real click also takes. Console-only reachability for C therefore needs the
placeable already unlocked (a real Security check or key in an actual playthrough); A and B ship
unlocked and are fully console-reachable end to end, which is why they are the primary test below.

Full reproduction (read-only, offline; the routine watch-set and `Interp.record` override are
identical in shape to §3's script, redirected to the routine ids above and cross-referenced
against `module_owners()`'s own `ext` per tag):

```python
import sys; sys.path.insert(0, "tools")
import kotorfiles as kf, localvaraudit as lva

TARGET = {44: "ActionCloseDoor", 324: "SetLocked", 483: "ActionUnlockObject", 484: "ActionLockObject"}
lva.LOCAL_ROUTINES = set(TARGET)
def record(self, pc, routine, args):
    obj = args[0] if args else None
    a1 = args[1] if len(args) > 1 else None
    self.sink.append({"script": self.script, "routine": TARGET.get(routine, routine),
                       "obj": lva.describe(obj),
                       "arg1": a1[1] if a1 and a1[0] == "int" else lva.describe(a1)})
lva.Interp.record = record

game = kf.Game(r"C:\GOG Games\Star Wars KOTOR (GOG)")
sigs = lva.load_signatures(game)
mod = lva.Module(game, "tat_m20aa")
objects, owners = lva.module_owners(mod)
tagtype = {o["tag"]: o["ext"] for o in objects if o["tag"]}
for script in mod.resources("ncs"):
    sink, err = lva.analyse_script(mod.read(script + ".ncs"), sigs, script)
    for h in sink:
        if h["routine"] in ("SetLocked", "ActionLockObject", "ActionUnlockObject"):
            target = h["obj"][4:] if h["obj"].startswith("tag:") else h["obj"]
            if tagtype.get(target) == "utp" or h["obj"] == "SELF":
                print(script, h["routine"], h["obj"], h["arg1"], owners.get(script, [])[:2])
```

### 6.8 Live-test recipe (two instances, GOG rig) — NEEDS-LIVE

Every candidate below is a placed GIT object present the moment its module loads, so `warp`
reaches it directly regardless of story progress.

**Primary test (candidate A — heartbeat auto-toggle, console verbs only):**

1. Connect both instances.
2. On **both** instances: `warp tat_m20aa`.
3. On the **HOST**: bare `useplaceable` (no tag/id) — this logs the full placeable sweep,
   including every `tressndpplbin` copy's live object id (there are 7 sharing that tag, so the
   bare sweep or an explicit id is the reliable way to pick one; the tag form "stops at the
   first match" found by an address-space sweep, which is not deterministic — see
   `CreatureFinder.FindPlaceableByTag`'s own remarks). Note one id from the log.
4. Wait roughly 5-10 seconds (a couple of `OnHeartbeat` intervals) without touching it.
5. On the **HOST**: confirm the plugin log shows the lock state toggling on that id (or check
   with a second `useplaceable 0x<id>` attempt — refused while locked, allowed while unlocked).
6. Confirm the **JOINER's** copy of the SAME id agrees with the host's current locked state
   within the next scan (≤1s) plus apply pass (≤~130ms at 40 frames/~317Hz) — attempt
   `useplaceable 0x<id>` on the joiner and confirm it is refused exactly when the host's own
   copy is locked, allowed exactly when unlocked. Watch two or three toggle cycles to confirm
   the joiner tracks every flip, not just the first one (the no-oscillation check).

**Secondary test (candidate B — a one-shot self-lock, console verbs only):**

7. On **both** instances: `warp tar_m03ad`.
8. On the **HOST**: `useplaceable holo1`. This sends the real `UseObject` input, which fires
   `OnUsed` → `tar03_holo1` → `SetLocked(SELF, TRUE)` on the host's own copy.
9. Confirm the **JOINER's** copy of `holo1` reads locked within the same poll windows as step 6
   above — a second `useplaceable holo1` on the joiner should be refused where an identical
   attempt before step 8 would have succeeded. `holo1` is not a container (`HasInventory=0`),
   so there is no loot panel or animation to check here — purely the lock bit.

**Tertiary test (candidate C — the real close→re-lock shape, needs an unlocked/looted copy):**

10. On the **HOST**, once `sta_armor_container` is genuinely unlocked and reachable in a real
    playthrough (Security check or key — console verbs do not bypass this placeable's lock, per
    §6.7): `useplaceable sta_armor_container`, take or leave the contents, then
    `closeplaceable sta_armor_container`. The real `CloseInventory` call fires `OnClosed` →
    `k_psta_binopen` → `SetLocked(SELF, TRUE)`.
11. Confirm the **JOINER's** copy of `sta_armor_container` reads closed (the lid pose reverts)
    AND locked within the same poll windows, without needing to be near it or to have opened it
    itself — the channel is module-scoped, not proximity-scoped, same as doors.

**No-oscillation check, all of the above**: after each re-lock/close lands on the joiner, attempt
the ordinary interaction again from the joiner's side and confirm it is refused rather than
silently succeeding, and that the state does NOT flip back on its own across two or three later
poll intervals (~2-3s) with nobody touching it. A genuine later unlock/re-open from either side
(a real Security check, or `closeplaceable`/`useplaceable` again once unlocked) should still work
and still propagate — the mechanism does not lock a placeable against *legitimate* state changes,
only against a stale poll re-asserting a value nobody actually changed, the same guarantee §4's
no-oscillation check describes for doors.
