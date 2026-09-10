# Real-time creature movement — why the phantom lags, and how to make it follow a position stream (research 2026-08-01)

How to place a creature at an arbitrary position **every frame** so it renders there smoothly,
and why every attempt so far has failed. Verified against `swkotor.exe` (GOG retail 1.0.3;
disk == memory, no ASLR, image base `0x400000`) by disassembly (`tools/disasm.py`), caller
scans (`tools/xrefs.py`) and the sha256-matched symbol DB `tools/kotor1_0_3.db`. Nothing was
modified and no game function was called for this document.

Sibling docs: `docs/re/engine-movement.md` (the server walk pipeline, action queue, animation
constants), `docs/research/animation-sync.md` (fire-and-forget replication to the twin),
`Handoff.md` §0i (the playtest report this answered, and how it was closed — measured
live at a mean gap of 0.20 units while the peer walked).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced from code I did
read · **NEEDS-LIVE** = must be confirmed on the running game.

---

## Headline

**The phantom's lag is two lags stacked, and the bigger one is not the pathfinder.**

The renderer does not read the server creature. It reads the **client twin**
(`CSWSObject + 0x224` → `CSWCCreature`), and the twin does not take the server position — it
**eases toward it over a fixed 512 ms budget**. `CSWCCreature::ResetDesiredPositionTimer`
(`0x0060BF10`) writes the literal `0x200` into `twin + 0x1E8`, and an exhaustive scan of every
instruction in `.text` that touches `+0x1E8` shows that is the **only** value that field is
ever set to outside the constructor (VERIFIED, §4.2). `CSWCCreature::MoveCreatureToPosition`
(`0x0060BDF0`) then closes only `dt / remaining` of the gap per client frame. Against a peer
moving at constant speed the steady-state visual error is

> `lag_distance ≈ peer_speed × 0.512 s`

which at KOTOR's run rate is roughly two world units of permanent, unclosable trail — *before*
the pathfinder has lost a single step. Every fix so far has been aimed at the wrong half.

**And `CSWSObject::SetPosition` never fails.** Full disassembly (§1) shows it writes the three
floats to `+0x90` unconditionally, with exactly one early-out: the new position being
**bit-exactly** equal to the old one (`Global::equal_2 0x004AA980` is three raw `fcomp`s, no
epsilon). Its return value is **not** a success flag — it is nonzero precisely on the
*no-op* path. So "the write did not commit" was never about the setter. It was about who
overwrote `+0x90` afterwards, and that writer is the AI master's quick-walk pre-pass, which the
mod itself keeps armed by queueing `ActionMoveToLocation` (§2).

**Verdict: the engine *can* be driven this way, cheaply, and the recipe is short.** Write the
server object with `SetPosition` while nothing else is stepping it, then pin the twin's ease
budget so the render side stops adding half a second of its own. Full recipe in §6.

---

## 1. `CSWSObject::SetPosition` — full decode (Q1)

`0x004CD1A0`, `__thiscall`, `ret 0x10` (four stack arguments). Disassembled in full; every
claim in this section is **VERIFIED** from the instruction stream.

```c
int __thiscall CSWSObject::SetPosition(
    CSWSObject* this,
    Vector*  pNewPosition,    // arg0
    int      bNotifySelf,     // arg1
    int      unusedAlways,    // arg2  <-- NEVER READ. Not "unused on this path": never read.
    int      bSuppressDirty); // arg3
```

Body, in order:

| Address | What it does |
| --- | --- |
| `0x004CD1B1` | `Global::equal_2(&this->position /*+0x90*/, pNewPosition)`. **Exact** float compare — `0x004AA980` is three `fld`/`fcomp` pairs and nothing else, no tolerance. |
| `0x004CD1BB` | If equal → `jne 0x4CD239` → **return immediately, writing nothing**, with `eax` = 1. |
| `0x004CD1BD` | `eax = arg3`; if `arg3 == 0` → `this->+0x1FC \|= 2` (dirty bit **1** = position). |
| `0x004CD1CF` | Resolve the area: `app = *(0x007A39FC)`; `srv = app->+0x08`; `objArray = 0x004AED70(srv)` (= `srv->+0x04->+0x1005C`); `0x004D8230(objArray, this->area_id /*+0x8C*/, &areaOut)`; if `areaOut` then `area = areaOut->vtbl[0x28]()`. |
| `0x004CD202` | **Writes the three floats to `this + 0x90`.** Unconditional once past the early-out. |
| `0x004CD212` | `eax = arg1`; if nonzero → `this->vtbl[0x30]()` **twice**, both results discarded. On the shipped vtables slot `0x30` is `Global::self_return` (`CSWSCreature`) or `Global::return_zero` (`CSWSObject`) — i.e. `AsSWSCreature()`. **Passing 1 is inert on retail.** |
| `0x004CD230` | If the area resolved → `CSWSArea::UpdatePositionInObjectsArray(area, this)` (`0x00506FE0`) — the area's spatial index. |

### 1.1 The return value is a trap

There is no success/failure flag. `eax` is:

- **nonzero** on the early-out (position unchanged — the one case where *nothing happened*);
- the result of `UpdatePositionInObjectsArray` when the area resolved and `arg1 != 0`;
- **zero** when `arg1 == 0`, whatever happened.

`Odyssey.StepToward`'s `setResult` diagnostic (`Odyssey.cs:3170`) is therefore reading noise,
and a "1" from it means *the write was skipped*. **Never branch on it.** (VERIFIED.)

### 1.2 What it writes, and what it does not

Writes: `+0x90..+0x9B` (position), `+0x1FC` bit 1 (unless `arg3 != 0`), the area's object
array.

Does **not** write, and this is the whole list of what a placed creature is missing:

- **No ground snap.** No `CSWSArea::ComputeHeight` (`0x004BC380`). Z is whatever you passed.
- **No walkability test.** No `PositionWalkable` (`0x00506400`), no `ComputeSafeLocation`
  (`0x004BE860`).
- **No subarea reassignment.** No `UpdateSubareasOnMoveTo` (`0x0051B7B0`), no
  `UpdateSubareasOnJumpPosition` (`0x0051B940`). Rooms and triggers do not re-evaluate.
- **No orientation.** `SetOrientation` (`0x004CD170`) is separate — it writes `+0x9C` and
  always sets dirty bit 2, with no early-out (VERIFIED, 12 instructions).
- **No movement state.** `+0xA8C`, `+0x9F0`, `+0xA98`, `+0xD4` are untouched.
- **No client twin write.** The twin is updated later, by the AI master's sync pass (§3).

### 1.3 The argument values the plugin already uses

`SetPositionFn(creature, &pos, 1, 0, 0)` in `Odyssey.cs` (`SnapTo`, `SetPhantomPosition`,
`StepToward`) is **correct**: `arg1 = 1` is inert, `arg2 = 0` is never read, `arg3 = 0` raises
the position dirty bit, which is exactly what the twin sync needs. Keep it. Nothing about the
argument list explains the observed failures.

---

## 2. What actually drives a creature's position every frame (Q2)

### 2.1 The one contended writer: the AI master's quick-walk pre-pass — VERIFIED

`CServerAIMaster::UpdateState` (`0x004B0B70`) runs once per server main loop
(`CServerExoAppInternal::MainLoop 0x004BABB0`), which is the ~317 Hz loop, not the render rate.
Its first pass (disassembly at `0x004B0C20`..`0x004B0D0B`, already documented in
`docs/re/engine-movement.md` §6.1 and re-confirmed here) is:

```c
obj = CServerExoApp::GetGameObject(id);
if (!obj) continue;
if (obj->+0x08 /*type*/ <= 4) continue;             // creature is 5
if (obj->+0x1F8 == 0) continue;                     // visible-to-player
cre = obj->vtbl[0x30]();  if (!cre) continue;
head = *(void**)(*(void**)(cre + 0xFC));            // queue head POSITION
if (head && head->+0x08 && (*(int*)(head->+0x08) == 1 || *(int*)(head->+0x08) == 0x3D))
        goto quickwalk;                             // MoveToPoint | PartyFollowLeader
if (cre->+0xA8C == 4 || == 5 || == 6) goto quickwalk;
continue;
quickwalk:
   r = CSWSCreature::WalkUpdateLocation_QuickWalk(cre, 0);   // 0x0051D9C0
```

`WalkUpdateLocation_QuickWalk` ends in `WalkUpdateLocationDistance` →
`UpdateSubareasOnMoveTo` → **`SetPosition`** → `SetOrientation` (call sites `0x0051DF08`,
`0x0051DFB5`, `0x0051DFC6`, `0x0051E04F`).

**Therefore: for exactly as long as an `ActionMoveToLocation` (action id 1) sits at the head of
the phantom's queue, the engine re-derives and re-commits that creature's `+0x90` on its own,
at the server main loop's rate — several times per rendered frame.** The plugin's frame hook is
at `SwapBuffers` (`FrameHook.cs`), i.e. once per *render* frame at the very end. Two writers,
one field, and the engine's runs many times between each of ours.

That is the mechanism behind the play report: *"the write frequently did not commit (position
read back unchanged), then occasionally jumped when timing happened to align."* The write did
commit; the pre-pass then walked the body back onto its cached path within microseconds, and
the plugin's next read saw the engine's value. When `ClearAllActions` happened to land in a
window where the pre-pass had already given up, the write "took". (**INFERRED** — the chain is
VERIFIED but the causal attribution to the specific measurement is not; §8 gives the one-line
live check that settles it.)

`SnapTo` "reliably commits" for the same reason, not because clearing *enables* the setter:
`ClearAllActions(1)` (`0x004CCD80`) empties the queue **and** sets `+0xA8C = 1`, which removes
the quick-walk gate on both of its arms. The clear removes the competing writer. The comment in
`Odyssey.cs:3043` ("the engine re-asserts position from the action/movement state") is right in
spirit and wrong in mechanism — it is not a re-assert, it is a second walk step.

### 2.2 The fields that must be consistent for a written position to persist

Exhaustive, from the gate above:

| Field | Required value | Why |
| --- | --- | --- |
| `CSWSObject + 0xFC` queue head action id | **not 1, not 0x3D** | either one arms the quick-walk pre-pass |
| `CSWSCreature + 0xA8C` (move state) | **not 4, 5 or 6** | the other arm of the same gate. `1` (idle) or `-1` (walking) are both inert here |
| `CSWSObject + 0xE8` (commandable) | `1` **when you call ClearAllActions** | `ClearAllActions` early-outs unless it is 1 (`0x004CCD80`) |
| `CSWSCreature + 0x4C0` (party follow info) | `0` | a party member gets `AIActionPartyFollowLeader` (`0x3D`) queued by the party system; never claim one |

There is **no** velocity field and **no** desired-position field on the server object. The
server creature's position is a plain `Vector` at `+0x90` with no derived state other than the
area's spatial index (which `SetPosition` maintains for you) and the subareas (which it does
not — §7).

### 2.3 The other per-frame pass: the dirty-flag push to the twin — VERIFIED

The **second** loop of `CServerAIMaster::UpdateState`, at `0x004B1040`..`0x004B116B`, is the
one that has been invisible to this project and matters most:

```c
for (id in ai bucket 5) {
  obj = GetGameObject(id);            if (!obj) continue;
  if (obj->+0x08 <= 4) continue;                                    // 0x004B108C
  if (obj->+0x1F8 == 0) continue;                                   // visible-to-player
  twin = CSWSObject::GetClientObject(obj);   // 0x004CC2B0 -> obj->+0x224
  if (!twin) continue;
  if (!twin->vtbl[0x0C]()) continue;
  if (IsDirtyFlagSet(obj, 0))  { twin->vtbl[0x134](obj->+0xD4, obj->+0xD8);  ClearDirtyFlag(obj,0); }
  if (IsDirtyFlagSet(obj, 1))  { twin->vtbl[0x12C](&obj->+0x90, obj->+0xD4, obj->+0xB8);
                                                                     ClearDirtyFlag(obj,1); }
  if (IsDirtyFlagSet(obj, 2))  { twin->vtbl[0x130](&obj->+0x9C);     ClearDirtyFlag(obj,2); }
}
```

`SetDirtyFlag 0x004CC1F0` / `IsDirtyFlagSet 0x004CC220` / `ClearDirtyFlag 0x004CC250` are a
bit array at `+0x1FC` (`*(uint*)(this + 0x1FC + (n>>5)*4) |= 1u << (n&31)`) — VERIFIED
instruction by instruction. Bit 0 = animation, bit 1 = position, bit 2 = orientation.

Vtable slots (`CSWCCreature` vtable `0x0074F5F8`, dumped in full):

| Slot | `CSWCCreature` | `CSWCObject` |
| --- | --- | --- |
| `+0x08C` | `CSWCCreature::SetPosition 0x0060B9B0` | `CSWCObject::SetPosition 0x0063F340` |
| `+0x12C` | **`ClientServerSyncHandler_Position 0x00611F50`** | `0x0063E7D0` (straight to `vt[0x8C]`) |
| `+0x130` | `ClientServerSyncHandler_Orientation 0x00611EC0` | `0x0063E7E0` |
| `+0x134` | `ClientServerSyncHandler_Animation 0x0060C100` | `0x0063D3F0` |
| `+0x14C/0x150/0x154` | `GetCurrentWayPoint / GetEndWayPoint / GetStartWayPoint` | — |
| `+0x15C` | `MoveToNextWayPoint 0x0060C4A0` | — |

**Note the `+0x1F8` gate on both passes: an off-screen creature is neither walked nor synced to
its twin.** (`+0x1F8` is written only by `CSWSMessage::ComputeGameObjectUpdateForObject + 0x46`
= `0x005780A6`, from `TestObjectVisible`.)

---

## 3. Which object does the renderer read? (Q4)

**The client twin, decisively.** VERIFIED.

`CSWCObject + 0x24` is `position` (in `kotor1_0_3.db`). `CSWCCreature::SetPosition`
(`0x0060B9B0`, vtable `+0x8C`) is the only thing that moves the visible body:

```c
this->+0x24 = *pPos;                       // 0x0060B9BF
if (this->+0x20) 0x006054E0(pPos);         // the Gob / scene node — this is what renders
if (*(int*)0x0083395C) this->vtbl[0x2C]();  // shadow blob refresh
if (this->+0x68 /*anim base*/ && !this->+0x50) animBase->vtbl[0x68](pPos);
this->vtbl[0x90]();
```

So **writing `twin + 0x24` raw does nothing visible** — the scene node keeps its old transform.
Any twin-side placement must go through vtable `+0x8C`, or through the desired-position path
below which calls it for you.

The server's `+0x90` reaches the twin only through §2.3's dirty-bit push, i.e. through
`ClientServerSyncHandler_Position`. Which is where the half second lives.

### 3.1 `CSWCCreature::ClientServerSyncHandler_Position` — `0x00611F50`, thiscall, `ret 0xC`

```c
void __thiscall CSWCCreature::ClientServerSyncHandler_Position(
    CSWCCreature* this,
    Vector* pServerPosition,   // &CSWSObject + 0x90
    uint32_t nServerAnimation, // CSWSObject + 0xD4
    float    fBudget);         // CSWSObject + 0xB8
// 0x00611F60:  this->+0x1DC = *pServerPosition        <-- the DESIRED position
// 0x00611F77:  CSWCCreature::MoveCreatureToPosition(this, pServerPosition)
// 0x00611F9C:  this->vtbl[0x10C]  (SetGroundNormal)
// 0x00611FA9:  if (!AnimationStationary(nServerAnimation)) {
//                  ... footstep / sound work ...
// 0x00612086:      this->+0x1F8 = CSWCCreature::ComputeSpeedFactor(this, fBudget);
//              } else RotateCreatureToOrientation(&this->+0x1C4);
```

It sets the desired position and takes **one** interpolation step. It does **not** reset the
budget. (VERIFIED.)

---

## 4. The 512 ms ease — the actual cause of the visible lag

### 4.1 `CSWCCreature::MoveCreatureToPosition` — `0x0060BDF0`, thiscall, `ret 4` — VERIFIED

```c
void __thiscall CSWCCreature::MoveCreatureToPosition(CSWCCreature* this, Vector* pTarget)
{
    int remaining = this->+0x1E8;      // ms left in the ease
    int dt        = this->+0x44;       // ms since this twin's last AIUpdate
    if (dt >= remaining) goto SNAP;                              // 0x0060BDFF
    this->+0x1E8 = (remaining -= dt);                            // 0x0060BE03

    Vector A = this->+0x1DC - this->+0x24;                       // desired - current
    if (A.x*A.x + A.y*A.y + A.z*A.z < 1e-5f) goto SNAP;          // vs [0x00747504]
    if (A.x*A.x + A.y*A.y >= 36.0f)          goto SNAP;          // vs [0x0074F348] == 6.0^2

    float k = (float)remaining / (float)(remaining + dt);
    Vector p;
    p.x = (this->+0x24 - pTarget->x) * k + pTarget->x;           // 0x0060BEB7..
    p.y = (this->+0x28 - pTarget->y) * k + pTarget->y;
    p.z = (this->+0x2C - pTarget->z) * k + pTarget->z;
    this->vtbl[0x8C](&p);              // CSWCCreature::SetPosition
    return;
SNAP:
    this->+0x1E8 = 0;
    this->vtbl[0x8C](pTarget);         // exact placement
}
```

Constants read from the image: `0x00747504 = 1.0e-5f`, `0x0074F348 = 36.0f`, i.e. a **6.0 unit
horizontal snap threshold**. `this->+0x44` is written by `CSWCCreature::AIUpdate` at
`0x0061ADC5` from `CWorldTimer::SubtractWorldTimes` (`0x004AE460`) against the twin's
`last_update` pair at `+0x3C`/`+0x40` — i.e. **milliseconds since the previous client
AIUpdate** (VERIFIED chain, INFERRED unit — but `+0x1E8`'s only literal, `0x200`, only makes
sense as ms).

### 4.2 The budget is *always* 512 ms — VERIFIED and exhaustive

An exhaustive scan of `.text` for every instruction encoding displacement `+0x1E8` finds
exactly three writers inside `CSWCCreature`:

| Site | Write |
| --- | --- |
| `CSWCCreature::Constructor + …` (`0x00616B69`) | initial |
| `CSWCCreature::MoveCreatureToPosition` (`0x0060BE03`, `0x0060BE5A`) | `-= dt`, and `= 0` on snap |
| **`CSWCCreature::ResetDesiredPositionTimer` `0x0060BF10`** | `mov dword [ecx+0x1E8], 0x200` — the whole function |

and `ResetDesiredPositionTimer` has exactly **one** caller in the entire image (`tools/xrefs.py`):
`0x006664F3`, inside `CSWCMessage::HandleServerToPlayerCreatureUpdate_Update` (`0x006655E0`),
guarded by "the incoming position differs from the stored desired position by more than
`1e-5`" (`0x006664C0`, VERIFIED). That guard is satisfied by any real movement.

So: **whenever the peer is moving, the object-update message re-pins the twin's ease budget to
512 ms, every update.** The message path is live in single player — `docs/research/world-up-faction-crash.md`
established that `CSWSMessage::WriteGameObjUpdate_UpdateObject` runs and crashes in a
loopback single-player session, and that writer is the producer for this handler.

### 4.3 What 512 ms costs, quantitatively — INFERRED (arithmetic on VERIFIED code)

Per client frame the gap is multiplied by `remaining / (remaining + dt)` while the target
advances by `v·dt`. With the budget re-pinned to `R = 512 ms` on every update, the fixed point is

```
gap*  =  v · dt / (dt / R)  =  v · R  =  v × 0.512 s
```

independent of frame rate. The phantom shares the player's `appearance.2da` row (row 114:
`walkdist 1.6`, `rundist 3.96` — `PhantomController.cs:33`), so a running peer's body renders
**~2.0 world units behind wherever the server object is**, permanently, and the current
`SnapDistanceSquared` of 3.5 units never fires because the *server* object is not behind — only
the picture is. That is exactly the reported symptom, and it is why tightening the goal-resend
threshold and the snap distance produced so little.

Two further consequences worth naming:

- The 6.0-unit snap threshold in §4.1 means a `SnapTo` beyond 6 units renders instantly, while
  anything under 6 units eases in over half a second. **That is why a synthetic teleport test
  looked fine and continuous walking did not** — the playtest report's own caveat, and the
  reason the phantom is now PLACED rather than eased (Handoff §0i).
- `+0x1E8` counts down and, if no update arrives, reaches `dt` and snaps. So a *stopped* peer's
  phantom lands exactly on the right spot after ~half a second — matching "a stopped peer's
  phantom stands almost exactly on the true spot".

### 4.4 Facing is eased too — VERIFIED, confirmed to matter, and fixed 2026-09-10

`ClientServerSyncHandler_Orientation` (`0x00611EC0`) writes `twin + 0x1D0` (desired orientation,
normalized via `0x004AB180`) and `twin + 0x1C4` (`moving_orientation`, in the DB), and
`CSWCCreature::AnimationUpdate` calls `RotateCreatureToOrientation` (`0x00611850`) with
`+0x1D0` when the animation is stationary and `+0x1C4` when it is moving (`0x0061AA04` /
`0x0061AA0D`, VERIFIED). That is a rate-limited turn, not a snap, and it turned out to matter:
this is the root cause of the live-reported "NPC position indicator" bug — a mirrored
NPC/companion that snapped position (a `positionparty` follow-recovery reformation, or any sharp
turn) rendered correctly PLACED on a peer's screen but kept visibly turning toward its real
facing for a stretch afterward, i.e. placed right, pointing wrong.

**Fixed** in `src/KotorCoop.Plugin/Odyssey.Phantom.cs`'s `DriveBody`: the twin's own low-level
`CSWCObject::SetOrientation` (vtable +0x88) is now called directly every frame — the same call
the engine itself uses to set a fresh twin's orientation at construction. This overwrites
whatever partial turn the eased handler already took that frame, so the next frame's eased step
starts (and lands) on the true target with nothing left to visibly ease. See
`docs/research/network-topology.md`'s 2026-09-10 follow-up for the full write-up.
`tools/facingtrace.ps1` measures server-vs-twin orientation drift live for anyone re-verifying
this.

---

## 5. Options considered, and the one to use (Q3)

### (a) `SetPosition` with the right arguments and accompanying writes

**Necessary, not sufficient.** The arguments are already right (§1.3). It commits every time
once the quick-walk pre-pass is disarmed (§2.2). But on its own the twin still eases, so the
picture still lags half a second. **Keep it — as half the recipe.**

### (b) Drive the engine's own movement state so it interpolates for us

This is what is happening today, and it *is* the problem — but the two knobs are exposed and
writable:

- `twin + 0x1DC` (`Vector`) — the desired position the twin walks to.
- `twin + 0x1E8` (`int`, ms) — how long it is allowed to take.

Setting `+0x1E8` to a small value (or `0`) turns the same machinery from a 512 ms ease into a
one-frame follow, with no new function calls and no new code paths. **This is the fix.**

There is no server-side velocity field to drive: §2.2. The engine's server-side "interpolator"
is the walk stepper, which is goal-driven and pathfinding-backed and is precisely what we are
trying to get away from.

### (c) The NWScript jump routines

`ActionJumpToLocation` / `ActionJumpToObject` queue action ids `5` / `0x30` →
`AIActionJumpToPoint 0x0051D600`, which resets party follow data, calls `AutoCloseGUIPanels`
(`0x004EDED0`), and **never sets a movement animation** (call graph VERIFIED in
`docs/re/engine-movement.md` §3). Twenty of those a second is a teleporting statue with side
effects. **Rejected for per-frame use.** `CSWSCreature::ForceMoveToPoint` (`0x004EDBA0`) remains
the right primitive for a *relocation* (spawn, module change, large desync) because it does the
subarea work — see §7.

### (d) What the engine itself uses for a creature that must follow exactly

It uses **desired position + a waypoint list + an animation speed factor**:

- `twin + 0x1BC` — `Vector*` waypoint array; `twin + 0x1C0` — current cursor;
  `twin + 0x1EC` — `uint16` count. (VERIFIED from `GetStartWayPoint 0x0060C1C0`,
  `GetEndWayPoint 0x0060C1A0`, `GetCurrentWayPoint 0x0060C190`, `MoveToNextWayPoint 0x0060C4A0`.)
- `CSWCCreature::SetWayPoints` (`0x0060C790`, thiscall, 8 bytes: `uint16 count, Vector* pts`)
  has four callers; three are message handlers and one is `EvaluateNewWayPoints 0x0060E7D0`,
  itself called only from the object-update handler (`0x0066640B`). **The waypoint list is
  written only by the network object update.**
- `CSWCCreature::ComputeSpeedFactor` (`0x0060BF60`) sums the remaining path length from
  `+0x1DC` through the waypoints and returns a playback-rate scale, stored at `twin + 0x1F8`.

**Rejected.** `MoveToNextWayPoint` (vtable `+0x15C`) has **zero call sites in the whole image**
(byte scan for `call [reg+0x15C]` — no hits; the only direct reference is the vtable slot). The
cursor is only ever advanced through the message-driven `EvaluateNewWayPoints` reconciliation.
Feeding waypoints by hand would mean reimplementing a consumer that is not there. The waypoint
machinery exists to let a *sparsely updated* creature look smooth over a laggy link — it is a
symptom of the same 512 ms design, not a cure for it.

### Recommendation

> **Write the server object with `SetPosition` every frame, with nothing queued to contest it,
> and pin the twin's ease budget to `0` (or a small value) every frame so the render side stops
> adding half a second.** All smoothing then belongs to the plugin's own snapshot
> interpolation, which is where it should be.

---

## 6. The recipe (Q6)

Everything below runs on the **game thread**, from the `SwapBuffers` frame hook. That hook site
is ideal for this: it fires after both `CClientExoAppInternal::MainLoop` and
`CServerExoApp::MainLoop`, so our writes are the **last word before the next frame's client
`AIUpdate` consumes them**, and nothing in the current frame can walk them back.

### 6.1 Once, when the body becomes a phantom (extend `Odyssey.ClaimCreature`)

```csharp
*(int*)(cre + 0x0E8) = 1;                 // commandable — ClearAllActions no-ops otherwise
ClearAllActionsFn(cre, 1);                // 0x004CCD80: empties queue, resets path info,
                                          //             sets +0xA8C = 1
*(int*)(cre + 0x9F0) |= 2;                // movement permitted (gates the walk/run animation)
```

and then **never queue a move action again**. Delete the `Odyssey.MoveToLocation` call from
`PhantomController.OnFrame` entirely — its presence is what arms the contended writer (§2.1).
`ForceMoveToPoint` (§7) stays for spawn and module change.

### 6.2 Every frame, per phantom

```csharp
Vec3 p = interpolatedRemotePosition;       // the plugin's own snapshot buffer output
bool moving = planarSpeed > IdleEpsilon;   // measured from the stream, not from remote.Animation
bool run    = planarSpeed > walkRunSplit;

// --- 1. facing --------------------------------------------------------------
Vec3 dir = new() { X = MathF.Cos(facing), Y = MathF.Sin(facing), Z = 0f };
SetOrientationFn(cre, &dir);                       // 0x004CD170, always dirties bit 2

// --- 2. gait ----------------------------------------------------------------
int want = moving ? (run ? 0x2714 : 0x2712) : 0x2710;
*(int*)(cre + 0xA98) = run ? 1 : 0;                // run flag
*(int*)(cre + 0xA8C) = moving ? -1 : 1;            // move state: -1 walking, 1 idle
if (*(int*)(cre + 0xD4) != want)
    ((delegate* unmanaged[Thiscall]<nint,int,void>)(*(nint*)(*(nint*)cre + 0x7C)))(cre, want);

// --- 3. the server position -------------------------------------------------
SetPositionFn(cre, &p, 1, 0, 0);                   // 0x004CD1A0; arg3 = 0 raises dirty bit 1

// --- 4. kill the twin's half-second ease ------------------------------------
nint twin = *(nint*)(cre + 0x224);                 // re-read every frame; never cache
if (twin != 0)
{
    *(Vec3*)(twin + 0x1DC) = p;                    // desired position
    *(int*)(twin + 0x1E8)  = 0;                    // budget 0 -> MoveCreatureToPosition SNAPS
}
```

Why step 4 works: with `+0x1E8 == 0`, `MoveCreatureToPosition`'s first test (`dt >= remaining`)
is true for any `dt >= 0`, so it takes the SNAP branch and calls `vtbl[0x8C](pTarget)` — the real
`CSWCCreature::SetPosition`, which moves the scene node, the shadow blob and the anim base. The
twin therefore renders **exactly** on `p` from the next client `AIUpdate` onward.

Writing `+0x1DC` ourselves (rather than relying on the server push) also makes the phantom
**immune to the `+0x1F8` visible-to-player gate** on both server passes (§2.3): an off-screen
phantom keeps tracking instead of freezing until you look at it.

**Tuning knob.** `+0x1E8 = 0` is exact tracking with zero engine-side smoothing. If the result
looks steppy at low network rates, raise it to **33–80 ms** (about one to two frames) rather
than back toward 512; the same code path then gives a short ease that hides single-frame jitter
without accumulating error. Do not leave it at the engine default.

**Do not** write `twin + 0x24` directly — the scene node would not follow (§3).

### 6.3 What this removes from `PhantomController`

- `Odyssey.MoveToLocation` and everything that feeds it: `_lastGoal`, `_repathCooldown`,
  `GoalResendDistanceSquared`, `RepathCooldownSteps`.
- `SnapDistanceSquared` / `SnapTo` as a *lag* bound. Keep a much larger version (say 20 units)
  purely as a "the peer teleported / changed module" detector that routes to `ForceMoveToPoint`.
- `Odyssey.SetFacing`'s "only when almost arrived" condition — facing can now be written every
  frame.
- The `StepIntervalSeconds` 60 Hz gate can stay (it keeps the cost bounded), but the writes are
  cheap enough — four stores and two thiscalls — to run on every hook invocation if that reads
  better.

---

## 7. Crash-risk register

| # | Risk | Severity | Mitigation |
| --- | --- | --- | --- |
| 1 | **No walkmesh validation.** `SetPosition` will happily place a body inside geometry or off the mesh (§1.2). | Medium — not a crash by itself, but a body inside a wall is a support ticket, and a subsequent equip/appearance rebuild on an out-of-room creature is the class of thing that bit `phantom-equip-crash.md`. | The streamed position is by construction a walkable point *in the peer's module*. **Gate the whole drive loop on "peer module == my module"**, which the shared block already carries. For a first placement or a resync use `ForceMoveToPoint` (`0x004EDBA0`), which runs `ComputeSafeLocation` (`0x004BE860`) internally. |
| 2 | **No subarea update.** Rooms and triggers do not re-evaluate on a raw `SetPosition` (§1.2). | Low, and partly desirable — you do **not** want a phantom firing local triggers or area transitions. | Accept it for per-frame movement. Route real relocations through `ForceMoveToPoint`, which calls `UpdateSubareasOnJumpPosition` (`0x0051B940`) twice. If room-based culling misbehaves visually, `UpdateSubareasOnMoveTo` (`0x0051B7B0`) is the engine's own pre-`SetPosition` call — but its 5 arguments (20 bytes) are still undecoded; do not call it blind. |
| 3 | **Twin pointer lifetime.** `cre + 0x224` is null before the twin exists and dangling after `UnloadModule`. | High — a stale twin write is a wild pointer store. | Re-read `+0x224` **every frame**, null-check **every frame**, never cache across frames. `PhantomController.Invalidate()` already drops phantom pointers on module change; keep that invariant. |
| 4 | **Writing `+0x1E8` on the wrong creature.** | High if it hits a freed object; cosmetic if it hits a live party member (they simply stop easing). | Only ever write it on a body this instance spawned and tracks in `_phantoms`. Never on the PC, never on a party member (`+0x4C0 != 0`). |
| 5 | **`+0xA8C = -1` with an empty queue.** | Low. | The engine itself writes `-1` in exactly this situation (`AIActionMoveToPoint + 0x81`, `WalkUpdateLocation_QuickWalk + 0x978`). The AI-master gate tests only `4/5/6`, so `-1` is inert there; the only consumer that matters is the client's `AnimationUpdate` gate at `0x0061A9CD`. |
| 6 | **Do not set commandable to `0`.** | Medium. | `ClearAllActions` is a no-op unless `+0xE8 == 1` (`0x004CCD80`), so a non-commandable phantom can never be cleaned up. The existing comment in `Odyssey.ClaimCreature` is correct — leave it at 1. |
| 7 | **Z drift.** Nothing ground-snaps a `SetPosition`. | Low across a shared module (the peer's Z is a real walkmesh height), high across a version/mod mismatch. | Optional belt and braces: `CSWSArea::ComputeHeight(area, &p, &z, null)` (`0x004BC380`, thiscall, 12 bytes) and override `p.z`. Cheap; one room lookup. |
| 8 | **Thread affinity.** | Fatal. | All of §6 is main-thread-only, from the frame hook. The worker thread must continue to touch only the shared block. |
| 9 | **Vtable dispatch for `SetAnimation`.** | Medium. | Keep dispatching through slot `+0x7C`; `CSWSCreature::SetAnimation 0x004F0D70` is only ever called directly from the constructor, and it does the injured/dead/gore substitutions that the base class does not. |

---

## 8. How to verify live

Do these **before** writing the fix. Item 2 alone proves or kills the entire thesis.

1. **Prove the setter never fails.** Around the existing `SetPositionFn` call, log
   `*(Vec3*)(cre+0x90)` immediately before and immediately after, plus what you passed. The
   after-value must equal the passed value on **every** call. (If it does not, everything in
   §1 is wrong and stop here.)

2. **Measure the twin ease.** With a peer running in a straight line, log per frame:
   `server = *(Vec3*)(cre + 0x90)`, `twin = *(Vec3*)(twinPtr + 0x24)`, `budget = *(int*)(twinPtr + 0x1E8)`,
   `dt = *(int*)(twinPtr + 0x44)`.
   **Prediction:** `budget` hovers at 512 minus a frame's worth and is re-pinned to 512 on
   every update; `|server − twin|` settles at a roughly constant ~1.5–2.0 units and does not
   shrink while the peer keeps running. That constant offset **is** the reported lag.

3. **Confirm the contention.** Log the queue head each frame:
   `head = *(nint*)(*(nint*)(cre + 0xFC))`, and when non-null, `*(int*)(*(nint*)(head + 0x08))`.
   With today's code it should be `1` (MoveToPoint) most of the time. After §6.1 it must be `0`
   (empty) forever. Also log `*(int*)(cre + 0xA8C)`.

4. **Confirm the visibility gate.** Turn the camera fully away from a moving phantom and watch
   `*(int*)(cre + 0x1F8)`. Expect `0`, and with today's code expect the twin to stop updating.
   After the fix (which writes `+0x1DC` directly) it must keep tracking.

5. **After the fix:** `|server − twin|` must be `0` every frame, and `budget` must read `0`
   whenever you sampled after your own write.

6. **Sanity on the render.** `+0x1E8 = 0` snapping means the *rendered* motion is exactly the
   plugin's interpolated stream. If it looks jittery, the network interpolation is the thing to
   tune (the 80 ms buffer), not the engine. Raise `+0x1E8` to 33–80 ms only as a second-order
   smoother.

7. **Unresolved, worth one log line:** `CSWSObject + 0xB8` (float) is passed as the third
   argument of `ClientServerSyncHandler_Position` and becomes `ComputeSpeedFactor`'s input, i.e.
   the animation playback rate scale (`twin + 0x1F8`). `ComputeSpeedFactor` returns `0.0f`
   immediately when `twin + 0x1C0` (waypoint cursor) is null (`0x0060BF79`), which is our case.
   Log `*(float*)(twin + 0x1F8)` on a moving phantom and confirm the run cycle still plays at a
   sane rate; if it does not, that field is the knob. **NEEDS-LIVE.**

---

## 9. Animation while the body is placed rather than walked (Q5)

**There is no speed or velocity field the animation system reads to decide walk-versus-run.**
The gait is a plain animation id you choose and push. VERIFIED chain:

1. Server: `SetAnimation` through vtable `+0x7C` writes `CSWSObject + 0xD4` and raises dirty
   bit 0 (`CSWSObject::SetAnimation 0x004CCF60` is four instructions;
   `CSWSCreature::SetAnimation 0x004F0D70` adds the injured/gore/dead substitutions).
2. `CServerAIMaster::UpdateState` sees dirty bit 0 and calls the twin's
   `ClientServerSyncHandler_Animation` (vtable `+0x134`, `0x0060C100`) with
   `(obj->+0xD4, obj->+0xD8)` (§2.3).
3. The twin stores it at `CSWCObject + 0x48` and, for looping ids, plays it via
   `GetLoopingAnimation 0x0063C100` → vtable `+0x9C`. `CSWCCreature::AnimationUpdate`
   (`0x0061A980`) re-asserts the looping animation each frame at `0x0061AA3B`.

The three things that must agree for a run cycle rather than an idle slide:

| Field | Value | Evidence |
| --- | --- | --- |
| `CSWSObject + 0xD4` | `0x2714` run / `0x2712` walk / `0x2710` idle | `AnimationWalking 0x004CC1C0`, `AnimationStationary 0x004CAE60` |
| `CSWSCreature + 0x9F0` bit 1 (mask `2`) | set | every walk site skips the animation entirely when clear |
| `CSWSCreature + 0xA8C` | **non-zero** while moving | `CSWCCreature::AnimationUpdate + 0x4D` (`0x0061A9CD`): if the twin's current animation is a *moving* one and the server creature's `+0xA8C == 0`, the whole update — rotation, `MoveCreatureToPosition`, looping animation — is **skipped**. Use `-1` while moving, `1` when idle. VERIFIED. |

`CSWSCreature + 0xA98` (run flag) is read only by `WalkUpdateLocation_QuickWalk`, which we are
deliberately keeping disarmed, so it is now cosmetic — set it anyway for consistency, it costs
one store.

**Choose the gait from the stream, not from the peer's mirrored animation field.**
`PhantomController` currently derives `run` from `remote.Animation != AnimWalkValue`, which is
unreliable for the reason `docs/research/animation-sync.md` documents (the peer's `+0xD4` never
sees client-side one-shots). The measured planar speed of the incoming positions is a direct,
always-correct signal: below ~0.3 u/s idle, below ~2.5 u/s walk, above run. The existing
`MirrorAnimation` fire-and-forget overlay is orthogonal and should stay exactly as it is — it
goes to the twin's fire-and-forget queue and does not disturb `+0xD4`.

One honest gap: `CSWCCreature::ComputeSpeedFactor` (`0x0060BF60`) exists precisely to rescale
the walk cycle so the feet match the ground speed, and it returns `0.0f` for a creature with no
waypoint list — which is us. §8 item 7 is the check. If the run cycle plays at the wrong rate,
write `twin + 0x1F8` yourself with a ratio of `measuredSpeed / appearanceRunDist`.

---

## 10. Key addresses

### Server (`CSWSObject` / `CSWSCreature`)

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004CD1A0` | `CSWSObject::SetPosition` | thiscall, 16 | `(Vector*, bNotify, unused, bSuppressDirty)`; return value is noise |
| `0x004CD170` | `CSWSObject::SetOrientation` | thiscall, 4 | writes `+0x9C`, always dirties bit 2 |
| `0x004AA980` | `Global::equal_2` | cdecl, 8 | exact 3-float compare, no epsilon |
| `0x004CC1F0` / `0x004CC220` / `0x004CC250` | `SetDirtyFlag` / `IsDirtyFlagSet` / `ClearDirtyFlag` | thiscall, 4 | bit array at `+0x1FC` |
| `0x004CCD80` | `CSWSObject::ClearAllActions` | thiscall, 4 | needs `+0xE8 == 1`; sets `+0xA8C = 1` |
| `0x004CC2B0` | `CSWSObject::GetClientObject` | thiscall, 0 | returns `+0x224`, with a fallback lookup |
| `0x004B0B70` | `CServerAIMaster::UpdateState` | thiscall | quick-walk pre-pass + the twin sync pass |
| `0x0051D9C0` | `CSWSCreature::WalkUpdateLocation_QuickWalk` | thiscall, 4 | the contended per-frame position writer |
| `0x00516630` | `CSWSCreature::WalkUpdateLocationDistance` | thiscall, 16 | not needed by this design |
| `0x004EDBA0` | `CSWSCreature::ForceMoveToPoint` | thiscall, 4 | the *relocation* primitive (spawn, module change) |
| `0x005034D0` | `CSWSForcedAction::Constructor` | thiscall, 0 | |
| `0x004BC380` | `CSWSArea::ComputeHeight` | thiscall, 12 | optional ground snap |
| `0x004BE860` | `CSWSArea::ComputeSafeLocation` | thiscall, 20 | for untrusted destinations |
| `0x00506FE0` | `CSWSArea::UpdatePositionInObjectsArray` | thiscall, 4 | called for you by `SetPosition` |
| `0x004F0D70` | `CSWSCreature::SetAnimation` | thiscall, 4 | **always via vtable `+0x7C`** |

### Client twin (`CSWCObject` / `CSWCCreature`, vtable `0x0074F5F8`)

| Address | Symbol | Note |
| --- | --- | --- |
| `0x0060BDF0` | `CSWCCreature::MoveCreatureToPosition` | the ease; snaps if `dt >= +0x1E8`, if gap² < 1e-5, or if horizontal gap ≥ 6.0 |
| `0x0060BF10` | `CSWCCreature::ResetDesiredPositionTimer` | `+0x1E8 = 0x200` — the entire function; one caller |
| `0x00611F50` | `CSWCCreature::ClientServerSyncHandler_Position` | vtable `+0x12C`, `ret 0xC` |
| `0x00611EC0` | `CSWCCreature::ClientServerSyncHandler_Orientation` | vtable `+0x130` |
| `0x0060C100` | `CSWCCreature::ClientServerSyncHandler_Animation` | vtable `+0x134` |
| `0x0060B9B0` | `CSWCCreature::SetPosition` | vtable `+0x8C` — the only thing that moves the scene node |
| `0x0060BF60` | `CSWCCreature::ComputeSpeedFactor` | returns `0.0f` with no waypoints |
| `0x0061A980` | `CSWCCreature::AnimationUpdate` | the per-frame consumer |
| `0x0061AD60` | `CSWCCreature::AIUpdate` | writes `+0x44` (ms since last update) |
| `0x006655E0` | `CSWCMessage::HandleServerToPlayerCreatureUpdate_Update` | the only caller of `ResetDesiredPositionTimer` |
| `0x0060C790` | `CSWCCreature::SetWayPoints` | `(uint16 count, Vector* pts)` — message-fed only |

### Offsets

```
// CSWSObject / CSWSCreature (server)
0x008  object_type        (creature == 5)
0x078  ai_level
0x08C  area_id
0x090  position   Vector          <- SetPosition writes here
0x09C  orientation Vector         <- SetOrientation writes here
0x0B8  float, passed to ClientServerSyncHandler_Position as the speed-factor budget
0x0D4  animation
0x0D8  float, passed with the animation sync
0x0E8  commandable       (must be 1 for ClearAllActions)
0x0FC  action queue      (CExoLinkedListInternal*)
0x1E4  current action node
0x1F8  visible-to-player (gates BOTH per-frame passes)
0x1FC  dirty bits: 0 anim, 1 position, 2 orientation
0x224  client_object     -> CSWCCreature*
0x340  path_find_info
0x4C0  party_follow_info (must be 0 to claim)
0x9F0  move flags, bit 1 (mask 2) = movement permitted
0xA8C  move state: 0 never moved, 1 idle, 2 requested, 3 follow, 4/5/6 force, -1 walking
0xA98  run flag

// CSWCObject / CSWCCreature (twin)
0x024  position   Vector          <- what renders; write ONLY via vtable +0x8C
0x030  orientation Vector
0x044  ms since this twin's last AIUpdate
0x048  current animation id (uint16)
0x0F8  server_object -> CSWSObject*
0x1BC  waypoint array  (Vector*)
0x1C0  waypoint cursor (Vector*)
0x1C4  moving_orientation Vector
0x1D0  desired orientation Vector
0x1DC  DESIRED POSITION Vector     <- write this
0x1E8  ease budget, ms             <- write this (engine default 0x200 == 512)
0x1EC  waypoint count (uint16)
0x1F4  int, selects a 500 vs 1000 ms budget inside ComputeSpeedFactor
0x1F8  animation speed factor (float)
```

### Constants

```
0x00747504 = 1.0e-5f   // "desired position actually changed" epsilon, and the ease's arrival test
0x0074F348 = 36.0f     // 6.0^2 — horizontal distance beyond which the twin snaps instead of easing
0x00200    = 512       // the ease budget in ms; the only value ResetDesiredPositionTimer writes
0x2710/0x2712/0x2714   // idle / walk / run animation ids
0x7F000000             // OBJECT_INVALID
```
