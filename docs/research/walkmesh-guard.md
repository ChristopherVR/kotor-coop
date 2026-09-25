# Walkmesh guard — the `walkto 187 100` crash, its root cause, and the one choke point every mover now goes through (research 2026-09-25)

Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, image base
`0x400000`) by disassembly (`tools/disasm.py`, `tools/q.py` against `tools/kotor1_0_3.db`).
Nothing was modified and no game function was called to produce the RE in §1-§3; the plugin/
agent code changes in §4-§6 are new, unit-tested, not yet run live (see §7 for the recipe the
next live session should run).

Sibling docs: [realtime-creature-movement.md](realtime-creature-movement.md) (the position-
drive recipe every mirror channel uses — `SetPosition` never fails, the AI master's quick-walk
pre-pass is what actually commits a move), [transition-window-races.md](transition-window-races.md)
(cross-world values are poison; the four safe patterns this write-up's §5/§6 extend to
positions specifically), [phantom-locomotion.md](phantom-locomotion.md) (why a phantom
lags/slides — a different bug in the same movement family).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced from code read ·
**NEEDS-LIVE** = must be confirmed on the running game.

---

## 1. The crash

Live, deterministic, 2026-09-25. Host player standing in `danm13` (Dantooine Jedi Enclave) at
(187.3, 74.3, 12.6). The dev console verb `walkto 187 100` (plugin issues
`Odyssey.MoveToLocation` for the local player) crashed `swkotor.exe` within ~15 ms:

```
FIRST-CHANCE access violation at 0x58063B  CSWRoomSurfaceMesh::GetTriangleAdjacency
stack values:
  0x584488  CSWRoomSurfaceMesh::ClippedLineSegmentWalkable
  0x4BB6B0  CSWSArea::GetRoom            (a stale/leftover frame value — see §3)
  0x4BCC2E  CSWSArea::TestDirectLine
```

`walkto 187 96` from the same area worked. The same fault (`0x58063B`, same top-of-stack
values) had also killed a JOINER earlier on `danm14aa` immediately after it arrived there,
while the HOST's own published position momentarily read `(0,0,0)` during the host's own
module load — a plugin-driven move toward a peer's transient/invalid position, not a
hand-typed console command. Both are the same engine bug; §6 covers the second one as the
SOURCE-side half of the fix.

## 2. RE: the bad pointer

**`CSWSArea::TestDirectLine` (0x004BCB70, `__thiscall`) already null-checks the START point's
room correctly** — full disassembly:

```
0x004BCB7C  mov ecx,[eax]        ; load the query position (x,y,z) at [esp+0x5C]
...
0x004BCBBF  call 0x4BB600        ; CSWSArea::GetRoom(this, &position) -> room ptr or 0
0x004BCBC4  mov ebx,eax
...
0x004BCBFC  test ebx,ebx
0x004BCBFE  je 0x4BCED0          ; ebx==0 -> pop regs, xor eax,eax, ret 0x18 (clean fail, NOT a crash)
```

`CSWSArea::GetRoom` (0x004BB600) itself never faults either: it loops every room in the area
(`this+0x22C` count, `this+0x230` array, 0x4C bytes/room) calling
`CSWSRoom::CheckAABBWalkable` (0x00579560) on each and returns 0 (no match) if the loop
finishes without a hit (`xor eax,eax; ret 8` at 0x4BB6C2). **This is a coarse per-room
BOUNDING-BOX test only** — it says "your point is inside this room's rectangle", not "your
point is on a real triangle".

If a room IS found, `TestDirectLine` tail-calls into the walkability chain:

```
0x004BCC29  call 0x579670   ; CSWSRoom::ClippedLineSegmentWalkable
```

`CSWSRoom::ClippedLineSegmentWalkable` (0x00579670) is a **bare two-instruction thunk**, no
null check of its own:

```
0x00579670  mov ecx,[ecx+0x3C]   ; room+0x3C = CSWRoomSurfaceMesh* (never null for a real room)
0x00579673  jmp 0x584220         ; tail call, CSWRoomSurfaceMesh::ClippedLineSegmentWalkable
```

`CSWRoomSurfaceMesh::ClippedLineSegmentWalkable` (0x00584220) walks the line segment across
the mesh triangle by triangle. It finds the START point's triangle first and DOES check for
"no triangle" there (`call 0x581720; cmp eax,-1; je 0x584742` at 0x00584369-0x584370 — a clean
bail on `-1`). It then loops (0x584440-0x5844C9), advancing to the next triangle along the
segment via `CSWRoomSurfaceMesh::GetTriangleAdjacency` (0x00580620):

```
0x00584479  push edx                 ; edx = the CURRENT triangle index (from the prior step)
0x0058447E  push 0
0x00584481  mov ecx,ebp
0x00584483  call 0x580620            ; GetTriangleAdjacency(this, side, curTri, &n0,&n1,&n2)
0x00584488  ...                      ; <- return address seen on the crash stack
```

**`GetTriangleAdjacency` (0x00580620) is where the bug is.** Its own bounds check only
rejects a triangle index that is too LARGE, never one that is negative:

```
0x00580620  mov eax,[esp+8]          ; eax = triIndex (arg)
0x00580624  cmp eax,[ecx+0x58]       ; ecx+0x58 = this mesh's triangle COUNT
0x00580627  jge 0x5806B2             ; triIndex >= count -> safe return (does nothing, ret 0x14)
0x0058062D  mov edx,[ecx+0x88]       ; edx = the adjacency array pointer
0x00580633  test edx,edx
0x00580635  je 0x5806B2              ; array null -> safe return
0x00580637  push esi
0x00580638  lea esi,[eax+eax*2]      ; esi = triIndex * 3
0x0058063B  mov edx,[edx+esi*4]      ; <<< THE FAULT: edx[triIndex*3] read
```

`cmp eax,[ecx+0x58]; jge` is a **signed** comparison. When the line-segment walk steps off the
LAST valid triangle with no neighbour on that side — exactly what happens when the segment's
far end is off the mesh entirely, as `(187, 100)` is — the triangle-index sentinel that reaches
this second call is `-1` (`0xFFFFFFFF`). `-1 >= count` is false for any positive count, so the
bounds check is satisfied and execution falls through into
`edx[(-1)*3*4] = edx - 12`: **a 12-byte out-of-bounds read immediately BEFORE the adjacency
array (`this+0x88`), landing on whatever heap bytes happen to precede it.** That dereference —
not a null pointer, an OOB/garbage pointer one triangle-record short of the real array — is
the access violation. (The unchecked-negative-sentinel shape recurs identically at
`0x005806A1`/`0x005806C9` for the mesh's two sibling "look up this triangle's own data" call
sites in the same function; not exercised by this particular repro, but the same class of bug
if anything else ever feeds them a raw index without checking for `-1` first.)

The `0x4BB6B0` value on the crash's reported stack is **not** part of this call chain — it is
the return address inside `GetRoom`'s own loop (after its call to `CheckAABBWalkable`,
0x004BB6AB), left over in stack memory from `TestDirectLine`'s EARLIER call to `GetRoom` a few
dozen bytes up the same frame. Raw stack scans routinely pick up such stale return addresses
from a prior call whose stack space has since been reused; the genuine chain is
`TestDirectLine (0x4BCC29 call) -> CSWSRoom::ClippedLineSegmentWalkable (thunk) ->
CSWRoomSurfaceMesh::ClippedLineSegmentWalkable (0x584483 call, return 0x584488) ->
GetTriangleAdjacency (fault at 0x58063B)`, matching every other value on the reported stack
exactly.

## 3. RE: the engine's own safe validator

`CSWSArea::PositionWalkable` (**0x00506400**, `__thiscall`, `ret 0xC`) is the engine's own
dedicated "can I even ask about this point" query. Full disassembly:

```
0x00506400  ... SEH prologue ...
0x00506420  call 0x4BB600            ; CSWSArea::GetRoom(this, x,y,z) -> room or 0
0x00506427  test eax,eax
0x00506427  je 0x5064B5              ; room==0 -> xor eax,eax; ret 0xC   (clean "not walkable")
0x0050642D  mov ecx,[eax+0x3C]       ; room+0x3C = CSWRoomSurfaceMesh*
0x00506449  call 0x581A10            ; CSWRoomSurfaceMesh::GetSurfaceMaterialWalkCheckOnly
0x0050645E  ... 2DA "Walk" flag lookup on the returned surface material ...
0x005064A0  setne al                 ; returns nonzero iff a real material row was found
0x005064B2  ret 0xC
```

`CSWRoomSurfaceMesh::GetSurfaceMaterialWalkCheckOnly` (0x00581A10) calls
`CSWRoomSurfaceMesh::CheckAABBWalkable` (0x00581530) to locate the actual TRIANGLE at
(x,y,z) — a point-query, not a line-segment walk. When none exists (inside the room's bounding
box but off the real mesh — **exactly the `walkto 187 100` shape**), it returns cleanly:

```
0x00581AC6  call 0x581530            ; CheckAABBWalkable -> triangle index, or -1 by convention
0x00581ACB  mov eax,[esp+0x34]
0x00581ACF  cmp eax,-1
0x00581AD2  jne 0x581ADD             ; found a real triangle -> look up its material
0x00581AD4  xor eax,eax              ; not found -> 0, cleanly
0x00581ADA  ret 0xC
```

**`PositionWalkable` never calls `TestDirectLine`, `ClippedLineSegmentWalkable`, or
`GetTriangleAdjacency`** — it is a completely separate query path that only ever reads a
triangle by direct point lookup, so the negative-index bounds bug in §2 is structurally
unreachable through it. It is what the guard in §4 validates a destination with before
handing anything to the engine's pathing.

**ABI, VERIFIED by disassembly**: `BOOL __thiscall CSWSArea::PositionWalkable(CSWSArea* this,
float x, float y, float z)`. `ret 0xC` pops three stack dwords; `lea eax,[esp+0x24]` at
0x00506420 (right after the SEH prologue's fixed 0x20-byte stack growth) computes
`entry_esp + 4` — the FIRST caller-pushed stack slot — and hands that address straight to
`GetRoom` as `&position`, so x/y/z are three CONTIGUOUS stack floats pushed by value
(right-to-left, `push z; push y; push x`), not a pointer argument.

**Why retail never hits this bug**: the click-to-move path
(`CClientExoAppInternal::HandleMouseClickInWorld`, 0x00620350) resolves a screen click through
a 3D ray-pick against the CLIENT-side render geometry — a click is geometrically constrained to
land ON a rendered triangle by construction, so retail never feeds `ActionMoveToLocation` an
off-mesh endpoint. Only a synthetic destination — a console `walkto`, or a plugin driving a
body toward a coordinate nothing ray-picked — can produce one.

## 4. The guard

One choke point, `Odyssey.TryValidateMoveDestination(nint creature, Vec3 destination, out
MoveRejectReason reason)` (`src/KotorCoop.Plugin/Odyssey.Walkmesh.cs`):

1. **Pure sanity** (`WalkmeshGuardRule.ClassifyDestination`, `src/KotorCoop.Plugin/WalkmeshGuardRule.cs`,
   no engine call, unit-tested): NaN/infinite or beyond `Vec3.MaxWorldCoordinate` →
   `NotPlausible`; the exact `(0,0,0)` poison sentinel → `Origin` (the same sentinel `tp`/
   `walkto`/`movenear` already refused ad hoc, and the one a resolved-but-never-placed object
   reads back as — see `Vec3.IsOrigin`'s own doc comment).
2. **Resolve the mover's own area fresh**, `CSWSObject::GetArea()` (0x004CB120, same address
   already bound as `Odyssey.Combat.cs`'s `GetAreaFn` / `Odyssey.PlacedMines.cs`'s
   `PlacedMineGetAreaFn`) on the CREATURE being moved — never a cached/agent-published area
   pointer, per [transition-window-races.md](transition-window-races.md)'s rule that anything
   the plugin dereferences must be resolved fresh via Odyssey each frame. `0` (no loaded world,
   mid-transition, or the mover does not even resolve) → `NoArea`, fail closed.
3. **The engine's own answer**, `CSWSArea::PositionWalkable` (§3) on that area. `0` →
   `NotWalkable`.

On any rejection: the move is NOT issued, a refusal is logged **once per (object id, reason)
per module** (`Odyssey.LogMoveRefusalOnce`, latch cleared at every module boundary from
`PluginHost.InvalidateAll` via `Odyssey.InvalidateMoveGuardLog` — the same "latch per object,
reset at the epoch edge" shape [per-frame-engine-writes.md] already established, here for a log
line rather than a write storm), and the caller reports back through its own existing failure-
code channel.

Resolving the area fresh from the MOVER (rather than taking an area pointer from the caller)
also gives the "cross-world" rejection the task asked for for free: a position left over from a
PREVIOUS module, even if numerically sane, is validated against the CURRENT area's rooms —
astronomically unlikely to coincidentally land inside a real room/triangle of a totally
different module, so `PositionWalkable` itself rejects it as `NotWalkable` without needing a
separate module-tag check.

## 5. Call sites audited

Every plugin path that queues a move or writes a position now funnels through one of two
primitives, and both are guarded:

| Primitive | File | Guard |
| --- | --- | --- |
| `Odyssey.MoveToLocation` (queues NWScript `ActionMoveToLocation`) | `Odyssey.ActionQueue.cs` | Full guard (sanity + `PositionWalkable`) at entry, before ever resolving the VM implementer. New failure codes 7 (`NotPlausible`/`Origin`/`NoArea`) and 8 (`NotWalkable`) added beside the existing 1/2/4/5/6/9; `failure == 4` (VM mid-call, transient) retry logic in `CheatController.Doors.cs` is unaffected (7/8 are terminal, same as any other non-4 code already was) |
| `Odyssey.SnapTo` (raw `SetPosition` + action-queue clear) | `Odyssey.Downed.cs` | Full guard at entry; now returns `bool` (was `void`) |

Every caller of each, traced:

- **`walkto` console verb** (`CheatController.World.cs:WalkTo`) → `MoveToLocation`. Guarded;
  answers `"not walkable here (off the walkmesh)"` / `"not walkable here (destination is not a
  finite in-world point)"` on refusal (failure 7/8) instead of crashing. **This is the exact
  live repro.**
- **`tp` console verb** (`CheatController.World.cs:Teleport`) → `SnapTo`. Guarded; answers `"the
  teleport was refused (not on the walkmesh)"` on refusal.
- **`movenear` console verb** (`CheatController.Party.cs`) → `SnapTo`, with an EXPLICIT
  hand-typed destination exactly as likely to be off-mesh as `tp`'s (and this verb's own prior
  history — docs/research/shared-state-audit.md §2 round 36 — is a live crash from an
  unvalidated one). Guarded; answers a refusal naming the destination.
- **Door-walk retries** (`CheatController.Doors.cs`, `TryWalkThroughDoor` / the stall-reissue
  path) → `MoveToLocation` with a point computed just past a door
  (`DoorUse.WalkThroughPoint`) — always walkable in practice, but now fails closed rather than
  crashing if it is ever not.
- **Phantom (peer stand-in) drive**, non-direct-movement mode (`PhantomController.Drive.cs`,
  the `!goalMoved` repath branch) → `MoveToLocation`. Guarded centrally.
- **Phantom "hopelessly behind" catch-up snap** (`PhantomController.Drive.cs`, `behindSquared >
  SnapDistanceSquared`) → `SnapTo`. Guarded centrally.
- **Materialised-enemy drive** (`TestDriveController.cs:HandleWalk`) → `MoveToLocation`;
  (`TestDriveController.cs:CloseToMeleeRangeIfNeeded`) → `SnapTo`. Both guarded centrally.
- **Attack-range close** (`CheatController.Attack.cs:CloseLocalPlayerToMeleeRange`) → `SnapTo`,
  guarded; the destination is always adjacent to a live, resolved target so this is
  virtually always accepted.
- **Follow join-teleport / return-teleport** (`Odyssey.Dialogue.Follow.cs:TryTeleportForJoin`,
  its restore counterpart) → `SnapTo`, guarded; destinations are always beside a live NPC or the
  player's own prior (already-valid) position.
- **`Odyssey.Dialogue.Follow.cs:ClearLocalPlayerStuckDialogueAction`** — the "nuclear option"
  that breaks a stuck `AIActionDialogObject` loop no UI-level close could touch — is
  DELIBERATELY NOT routed through the guard: it re-commits the creature's OWN CURRENT position
  purely for `ClearAllActions`'s side effect, never asking the engine to go anywhere new, and
  refusing it on a borderline `PositionWalkable` read would remove the only recovery this
  specific failure mode has. New primitive `Odyssey.ClearActionsAndHoldPosition` (bypasses the
  guard by design, documented inline) replaces the old `SnapTo(player, GetPosition(player))`
  idiom for this one call site.

**Companion/party mirror drive (`PartyPresenceController.cs`) and materialised-enemy per-frame
drive (`EnemyPresenceController.cs`) were AUDITED and found ALREADY SAFE**, pre-existing: both
already gate their own remote position (`Vec3.IsWorldPlausible(...) && !(X==0 && Y==0)`) before
ever calling `Odyssey.DriveEnemy`/computing a smoothed target, in the same per-frame per-slot
function that does the drive — this repo's established pattern for a hot per-frame consumer
(cheap arithmetic only, no engine call in the hot path). No change needed there.

**Does `tp`/`SnapTo` run `TestDirectLine`? No** — VERIFIED by disassembly: `SnapTo` calls
`CSWSObject::SetPosition` (0x004CD1A0) directly, a bare thiscall with no pathing call inside it
(confirmed by `realtime-creature-movement.md`'s own full disassembly of the setter). A raw
`SnapTo`/teleport therefore cannot crash the way `walkto` did — but a body left standing
off-mesh by one is exactly the shape of the OTHER live fault (§6), so `SnapTo` is guarded
anyway as a source-side measure: refuse and hold the creature at its last position ("hold the
stand-in where it is") rather than write a destination some LATER engine tick's own
pathing/room check might fault on.

## 6. Source-side: does the publisher ever send `(0,0,0)`?

Yes. `PlayerState.IsPlausible` (`src/KotorCoop.Protocol/PlayerState.cs`) only screens
NaN/infinity and out-of-world magnitude (`Vector3f.IsWorldPlausible`) — it deliberately
ACCEPTS the exact origin, because a creature can genuinely stand there. But `Vec3.IsOrigin`'s
own doc comment (plugin side) already names the OTHER thing `(0,0,0)` means: "a resolved
object that has never actually been placed" — and `PhantomController.Lifecycle.cs`'s own spawn
comment independently confirms the exact window: *"during a mid-session load the player
creature exists before it is placed."* A host reading its OWN local state through exactly that
window republishes a truthful-looking `(0,0,0)` that `IsPlausible` waves through.

**Fix, at the true source**: `KotorGameBridge.Player.cs:TryReadLocalStateCore` (the GOG bridge
— the primary target per `kotor-coop-gog-is-primary-target`) and
`ProfileGameBridge.cs:TryReadLocalState` (the secondary profile-driven bridge, same fix for
consistency) now ALSO reject `state.Position.IsOrigin` (new `Vector3f.IsOrigin`, mirroring the
plugin's `Vec3.IsOrigin`) as a failed read — not a fresh valid position. This slots into
EXISTING, already-correct machinery with no further change needed: `CoopAgent.PumpOnce`
already holds `LastLocalState` and republishes it unchanged on any failed read, only raising
`FlagAway` after `AwayRule.ConsecutiveFailuresThreshold` SUSTAINED failures — so a one-tick
origin blip during a module load is now silently absorbed exactly the way a NaN/out-of-world
blip already was, instead of being published as truth.

**Deliberately NOT also gated in `SnapshotBuffer.Push`** (the receive-side buffer every network
peer sample lands in): that class is exercised directly by a very large share of the existing
test suite using `Vector3f.Zero` as a generic "don't-care" placeholder position for tests about
entirely unrelated concerns (death, dialogue, combat flags, ...). Adding the origin check at
that generic a choke point would have silently broken dozens of unrelated tests rather than
catching a real bug. The two layers that DO close this gap — the true source above, and the
plugin-side consumption guard below — are both closer to where the bug actually manifests and
do not have this collision.

**Plugin-side consumption gap, closed**: `PhantomController.Drive.cs`'s per-frame per-slot
drive loop had NOTHING screening `remote.Position` (the shared-block sample a peer's own agent
publishes) before feeding it to `SnapTo`/`MoveToLocation`/`DriveDirect` every driven frame —
unlike its own SPAWN gate (`PhantomController.Lifecycle.IsPlausiblePosition`), which already
screened it, and unlike `EnemyPresenceController`/`PartyPresenceController`'s own per-frame
drive loops, which already screen their own remote positions the same way (§5). This is the
exact mechanism of the SECOND live crash (§1): a joiner's phantom of the host, driven toward a
`(0,0,0)` sample during the host's own module load, was left standing off-mesh until some later
engine tick's own pathing/room check faulted on it. Now gated with the cheap pure check only
(`WalkmeshGuardRule.IsRemotePositionDriveable` — no engine call, since this runs at up to ~60 Hz
per phantom; the walkability engine call is reserved for the low-frequency choke points in §5
that actually risk `TestDirectLine`): a poisoned sample holds the phantom at its last good
position for that frame instead of driving it toward the poison value.

## 7. Tests

Pure-logic unit tests (no engine, no game process — same seam every other `*Rule` class in this
project uses):

- `tests/KotorCoop.Tests/WalkmeshGuardRuleTests.cs` — `WalkmeshGuardRule.ClassifyDestination`/
  `IsRemotePositionDriveable`: ordinary destinations accepted (including the exact crash
  position 187.3/74.3/12.6), NaN/infinite/out-of-world rejected as `NotPlausible`, the exact
  origin rejected as `Origin` (and distinguished from a near-origin point with nonzero Z, which
  is accepted), plausibility checked before origin.
- `tests/KotorCoop.Tests/WorldPlausibilityTests.cs` — added `Vector3f.IsOrigin` parity tests
  (agrees with `Vec3.IsOrigin` on the same inputs; `IsWorldPlausible` still separately accepts
  the exact origin on both sides, confirming the new check is additive, not a change to that
  screen).

The engine-call half of the guard (`GetArea`/`PositionWalkable`) and the `KotorGameBridge`/
`ProfileGameBridge` publish-side fix have no test double (a real `CSWSArea*`/live game process),
consistent with how `PlayerState.IsPlausible` itself has never had a direct unit test at its
`TryReadLocalStateCore` call site — exercised live instead (§8's recipe).

Full existing suite re-run green after both changes (`dotnet test KotorCoop.slnx`, `pwsh
src/KotorCoop.Plugin/build.ps1 -WarnAsError`); the widespread use of `Vector3f.Zero` as a
"don't-care" position placeholder across the test suite is exactly why the receive-side
`SnapshotBuffer.Push` gate was NOT added (§6).

## 8. Live recipe (NEEDS-LIVE)

1. `demo.ps1 -ClientAutoCharacter -Isolated`.
2. On the HOST, open the console and run `walkto 187 100` in `danm13` (or the module/point that
   reproduced the crash). Expected: a refusal line
   (`"not walkable here (off the walkmesh)"` or similar), `swkotor.exe` does NOT crash, and the
   plugin log shows one `move refused: 0x<id> -> (187.0,100.0,<z>) - destination is not on the
   walkmesh ...` line (not one per frame).
3. `walkto 187 96` (the known-good point from the original repro) still succeeds normally.
4. A 20-leg warp soak with the joiner following (`demo.ps1`'s standard two-instance rig,
   repeated `crossdoor`/`galaxymap depart` legs or `tools/demo3.ps1`'s equivalent), watching
   for any `move refused` line firing more than once per (object, reason) per module and for any
   `CSWSArea::TestDirectLine`/`GetTriangleAdjacency` crash signature in `crashlog.ps1` output.
5. Optional: reproduce the SECOND crash's shape directly — pause one instance mid-module-load
   (e.g. hold a save/load dialog open) so its own local read has a real chance to sample a
   not-yet-placed player creature, and confirm the peer's phantom holds its last position
   instead of being driven to `(0,0,0)` and later faulting.

## 9. Live run 2026-09-25

On `demo.ps1 -ClientAutoCharacter -Isolated`, danm13, host at (187.3, 74.3):
- `walkto 187 100` answered `refused: not walkable here (off the walkmesh)`. The plugin
  logged one `move refused: 0x7FFFFFFF -> (187.0,100.0,0.0) - destination is not on the
  walkmesh (CSWSArea::PositionWalkable said no)`. Both games stayed up. Before the guard this
  exact command crashed the engine at 0x58063B every time.
- `walkto 187 96` still walked; `where` read (187.0, 96.0).
The 20-leg follow soak result is recorded in status-engineering.md.
