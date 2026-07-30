# Moving a creature through Odyssey's own movement system

Target: make a remote player's avatar **walk** instead of teleporting, by driving the
engine's movement path rather than fighting it.

## Provenance and what the labels mean

Everything below comes from two read-only sources:

1. `k1.db` - the public Ghidra symbol database for `swkotor.exe` v1.0.3, whose
   `game_version` sha256 matches the installed executable byte for byte.
2. Static disassembly of a dump of the unpacked in-memory image
   (`swkotor_image.bin`, base `0x400000`, no ASLR), cross-checked against `k1.db`
   symbol addresses.

**No game memory was written, no game function was called, and nothing was tested at
runtime for this document.** Every behavioural claim is therefore static.

- **VERIFIED** = read directly out of `k1.db`, or read directly out of the
  instruction stream of this exact binary. Addresses and offsets so labelled are
  facts about the shipped code.
- **INFERRED** = my interpretation of what code that I did read *means*
  (field semantics, argument names, control-flow intent).

Every address is an absolute VA for image base `0x400000`, directly usable at
runtime because the binary has no ASLR.

---

## 0. Executive summary

The movement pipeline, top to bottom (VERIFIED call chain, see section 6):

```
CServerExoAppInternal::MainLoop            0x004BABB0
  -> CServerAIMaster::UpdateState          0x004B0B70   (per-frame, once)
       -> [quick-walk pre-pass]  CSWSCreature::WalkUpdateLocation_QuickWalk  0x0051D9C0
       -> [per object]  vtable+0x70 = CSWSCreature::AIUpdate                 0x004FE210
            -> CSWSObject::RunActions                                        0x0057F4A0
                 -> pops the head action node, switches on node->action_id
                 -> CSWSCreature::AIActionMoveToPoint (action id 1)           0x0051F4F0
                      -> CSWSCreature::WalkUpdateLocation                    0x0051BB10
                      -> CSWSCreature::WalkUpdateLocationDistance            0x00516630  <-- the real step
                      -> CSWSObject::SetPosition / SetOrientation / SetAnimation
  -> CServerExoAppInternal::UpdateClientGameObjects  0x004B6950  (pushes dirty state to the client twin)
```

The single most useful discovery: **`CSWSCreature::WalkUpdateLocationDistance`
(`0x00516630`) is the atomic "advance this creature by this distance, this frame"
primitive**, and it is the thing that performs the walkmesh raycast, the ground
snap and the creature avoidance. It is what a raw `+0x90` write skips.

The second most useful: **`CSWSCreature::ForceMoveToPoint` (`0x004EDBA0`)** is a
complete, self-contained, *synchronous* relocation: it builds an action node,
calls the handler inline, and destroys the node. No queue, no AI scheduling. It
snaps to a walkable location, updates room/trigger subareas and sets orientation.
That is the correct primitive for a teleport (module change, large desync).

The third: **animation while moving is not a separate request.** It is
`SetAnimation(0x2712)` for walk / `SetAnimation(0x2714)` for run through
**vtable slot `+0x7C`**, plus the run flag at `CSWSCreature+0xA98`, plus the
movement state machine at `CSWSCreature+0xA8C`. All three are written by the
engine's own walk code and all three are invisible to a `+0x90` poke.

---

## 1. The action-queue API

### 1.1 Queue container (VERIFIED)

`CSWSObject + 0xFC` is a `CExoLinkedList<CSWSObjectActionNode>` whose single
member is the `CExoLinkedListInternal*`. Access pattern read out of
`CSWSObject::RunActions` and `CSWSObject::ClearAllActions`:

```
pInternal = *(void**)(obj + 0xFC)
POSITION  = *(void**)(pInternal + 0x00)          // head position, NULL == empty
element   = CExoLinkedListInternal::GetAtPos(pInternal, POSITION)
          == *(void**)(POSITION + 0x08)          // node data pointer (VERIFIED at 0x004B0C99)
CExoLinkedListInternal::GetNext(pInternal, &POSITION)
```

| Address | Function | Conv | Stack args |
| --- | --- | --- | --- |
| `0x005E9B60` | `CExoLinkedListInternal::GetAtPos` | thiscall | 4 |
| `0x005E9B90` | `CExoLinkedListInternal::GetNext` | thiscall | 4 |
| `0x005E9C80` | `CExoLinkedListInternal::RemoveHead` | thiscall | 0 |
| `0x005E9D70` | `CExoLinkedListInternal::AddHead` | thiscall | ? |

### 1.2 Add / clear / query (VERIFIED addresses, conventions and stack sizes from `k1.db`)

| Address | Function | Conv | Stack bytes |
| --- | --- | --- | --- |
| `0x004CEA20` | `CSWSObject::AddAction` | thiscall | **112** |
| `0x004CCB00` | `CSWSObject::AddActionToFront` | thiscall | **112** |
| `0x004CAC30` | `CSWSObject::AddActionNodeParameter` | thiscall | 16 |
| `0x004CAC00` | `CSWSObjectActionNode::Constructor` | thiscall | 0 |
| `0x004CC4D0` | `CSWSObjectActionNode::Destructor` | thiscall | 0 |
| `0x004CCD80` | `CSWSObject::ClearAllActions` | thiscall | 4 |
| `0x004CC390` | `CSWSObject::ClearAction` | thiscall | 8 |
| `0x004FAB00` | `CSWSCreature::ClearAction` (override, vtable `+0x74`) | thiscall | 8 |
| `0x004CCD30` | `CSWSObject::DeleteCurrentAIAction` | thiscall | 0 |
| `0x004CC530` | `CSWSObject::GetActionByGroupId` | thiscall | 8 |
| `0x004CC690` | `CSWSObject::GetNumActionGroups` | thiscall | 0 |
| `0x00518940` | `CSWSCreature::GetAction` | thiscall | 4 |
| `0x005188E0` | `CSWSCreature::HasAction` | thiscall | 4 |
| `0x00518B30` | `CSWSCreature::GetUserActionsPending` | thiscall | 0 |
| `0x004F76C0` | `CSWSCreature::RemoveAction` | thiscall | 4 |
| `0x004F6F30` | `CSWSCreature::UpdateActionQueue` | thiscall | 0 |
| `0x0057F4A0` | `CSWSObject::RunActions` (the dispatcher) | thiscall | 16 |
| `0x004AEF40` | `CServerExoApp::ClearAllActions` | thiscall | ? |
| `0x004AEAE0` | `CServerExoApp::IsActionsPending` | thiscall | ? |

**`AddAction` / `AddActionToFront` signature (VERIFIED by disassembling
`AddActionToFront` at `0x004CCB00`; 112 stack bytes == 28 dwords == exactly the
shape below):**

```c
void __thiscall CSWSObject::AddActionToFront(
    CSWSObject* this,
    uint32_t nActionId,        // written straight into node->action_id
    int16_t  nGroupActionId,   // 0xFFFF => auto-assign from this->+0x14 counter
    int32_t  nType0,  void* pValue0,     // 13 (type, value) pairs
    int32_t  nType1,  void* pValue1,
    ...
    int32_t  nType12, void* pValue12);
```

`AddAction` has the identical prototype (same 112 bytes, same construction
prologue) - INFERRED by symmetry, not disassembled line by line.

Argument handling detail (VERIFIED): the loop stops at the first `nTypeN == 0`,
so you only have to fill the pairs you use. `pValueN` is always a **pointer to**
the value, never the value itself.

`nGroupActionId == 0xFFFF` makes the engine allocate a group id from a per-object
counter at `CSWSObject + 0x14` (word), stashing the previous value at
`CSWSObject + 0x16` (word). VERIFIED code, INFERRED field names.

### 1.3 The dispatcher and the complete action-id enum (VERIFIED)

`CSWSObject::RunActions` at `0x0057F4A0` does, at `0x0057F6C0`:

```
node = CExoLinkedListInternal::RemoveHead(obj->+0xFC)   // NOTE: node LEAVES the queue
obj->+0x1E4 = node                                     // current action node
id = node->action_id                                   // dword at node+0
obj->+0x80 = (uint16)id                                // current action id mirror
if ((id - 1) > 0x40) goto default
jmp *(dword*)(0x005801E4 + (id - 1) * 4)
```

Because the node is *removed*, a continuing action re-queues itself with
`AddActionToFront` (this is exactly what `AIActionMoveToPoint` does through
`AddMoveToPointActionToFront`). VERIFIED.

Jump table base `0x005801E4`, 65 entries, index `action_id - 1`.
Object-type gate: most creature cases require `obj->+0x08 == 5` (VERIFIED
comparison `cmp ebx, 5` where `ebx = movzx byte [obj+8]`), so **object type 5 ==
creature** (INFERRED from that gate plus the `AsSWSCreature` calls).

| id | dec | Handler (VERIFIED from the table) |
| --- | --- | --- |
| `0x01` | 1 | **`CSWSCreature::AIActionMoveToPoint`** `0x0051F4F0` |
| `0x02` | 2 | `CSWSCreature::AIActionCheckMoveToObject` `0x005101A0` |
| `0x03` | 3 | `CSWSCreature::AIActionCheckMoveAwayFromObject` `0x005155B0` |
| `0x04` | 4 | `CSWSCreature::AIActionCheckInterAreaPathfinding` `0x0050FF20` |
| `0x05` | 5 | **`CSWSCreature::AIActionJumpToPoint`** `0x0051D600` |
| `0x06` | 6 | `CSWSObject::AIActionPlayAnimation` `0x0057D080` |
| `0x07` | 7 | `CSWSCreature::AIActionPickUpItem` `0x00517410` |
| `0x08` | 8 | `CSWSCreature::AIActionEquipItem` `0x00510FD0` |
| `0x09` | 9 | `CSWSCreature::AIActionDropItem` `0x00513830` |
| `0x0A` | 10 | `CSWSCreature::AIActionCheckMoveToPoint` `0x00510670` |
| `0x0B` | 11 | `CSWSCreature::AIActionUnequipItem` `0x00513EC0` |
| `0x0C` | 12 | `CSWSCreature::AIActionPhysicalAttacks` `0x005BBBF0` |
| `0x0D` | 13 | (default / debug only) |
| `0x0E` | 14 | `CSWSObject::AIActionSpeak` `0x0057B430` |
| `0x0F` | 15 | `CSWSCreature::AIActionCastSpell` `0x00514AF0` |
| `0x10` | 16 | `CSWSCreature::AIActionWaitForEndOfRound` `0x00513F60` |
| `0x11` | 17 | `CSWSCreature::AIActionCheckMoveToObjectRadius` `0x005103B0` |
| `0x12` | 18 | `CSWSCreature::AIActionCheckMoveToPointRadius` `0x005108A0` |
| `0x13` | 19 | `CSWSCreature::AIActionChangeFacingObject` `0x0050FB60` |
| `0x14` | 20 | `CSWSObject::AIActionOpenDoor` `0x0057D490` |
| `0x15` | 21 | `CSWSObject::AIActionCloseDoor` `0x0057BBF0` |
| `0x16` | 22 | `CSWSCreature::AIActionOrientCamera` `0x005137C0` |
| `0x17` | 23 | `CSWSObject::AIActionPlaySound` `0x0057CF00` |
| `0x18` | 24 | `CSWSObject::AIActionDialogObject` `0x0057A470` |
| `0x19` | 25 | `CSWSCreature::AIActionDisarmMine` `0x00519570` |
| `0x1A` | 26 | `CSWSCreature::AIActionRecoverMine` `0x00518C40` |
| `0x1B` | 27 | `CSWSCreature::AIActionFlagMine` `0x0050E400` |
| `0x1C` | 28 | `CSWSCreature::AIActionExamineMine` `0x0050E900` |
| `0x1D` | 29 | `CSWSCreature::AIActionSetMine` `0x00519E30` |
| `0x1E` | 30 | `CSWSObject::AIActionWait` `0x0057B5B0` |
| `0x1F` | 31 | `CSWSObject::AIActionPauseDialog` `0x0057B290` |
| `0x20` | 32 | `CSWSObject::AIActionResumeDialog` `0x0057B320` |
| `0x21` | 33 | `CSWSObject::AIActionSpeakStrRef` `0x0057B3D0` |
| `0x22` | 34 | `CSWSObject::AIActionGiveItem` `0x0057B6A0` |
| `0x23` | 35 | `CSWSObject::AIActionTakeItem` `0x0057BAE0` |
| `0x24` | 36 | `CSWSCreature::AIActionEncounterCreatureDestroySelf` `0x00510C20` |
| `0x25` | 37 | `CSWSObject::AIActionDoCommand` `0x0057B530` |
| `0x26` | 38 | `CSWSObject::AIActionUnlockObject` `0x0057D9D0` |
| `0x27` | 39 | `CSWSObject::AIActionLockObject` `0x0057BEC0` |
| `0x28` | 40 | `CSWSObject::AIActionUseObject` `0x0057E8C0` |
| `0x29` | 41 | (no-op, `Global::return_3`) |
| `0x2A` | 42 | `CSWSCreature::AIActionRest` `0x005140E0` |
| `0x2B` | 43 | (no-op) |
| `0x2C` | 44 | `CSWSCreature::AIActionCheckMoveAwayFromLocation` `0x0050FD50` |
| `0x2D` | 45 | `CSWSCreature::AIActionRandomWalk` `0x00515AC0` |
| `0x2E` | 46 | `CSWSCreature::AIActionItemCastSpell` `0x0050F170` |
| `0x2F` | 47 | `CSWSObject::AIActionSetCommandable` `0x0057C7B0` |
| `0x30` | 48 | **`CSWSCreature::AIActionJumpToObject`** `0x0051D110` |
| `0x31` | 49 | `CSWSCreature::AIActionChangeFacingPoint` `0x0050FC90` |
| `0x32` | 50 | `CSWSCreature::AIActionCounterSpell` `0x00514270` |
| `0x33` | 51 | `CSWSCreature::AIActionDrive` `0x0051E6A0` |
| `0x34` | 52 | `CSWSCreature::AIActionAppear` `0x005140F0` |
| `0x35` | 53 | `CSWSCreature::AIActionDisappear` `0x005141B0` |
| `0x36` | 54 | (no-op) |
| `0x37` | 55 | `CSWSCreature::AIActionForceFollowObject` `0x005132E0` |
| `0x38` | 56 | `CSWSCreature::AIActionHeal` `0x00517A60` |
| `0x39` | 57 | (default) |
| `0x3A` | 58 | `CSWSCreature::AIActionCheckForceFollowObject` `0x005136B0` |
| `0x3B` | 59 | (default) |
| `0x3C` | 60 | `CSWSCreature::AIActionAreaWait` `0x00511080` |
| `0x3D` | 61 | `CSWSCreature::AIActionPartyFollowLeader` `0x00511130` |
| `0x3E` | 62 | `CSWSObject::AIActionBarkString` `0x0057CE00` |
| `0x3F` | 63 | `CSWSCreature::AIActionCombat` `0x005B6210` |
| `0x40` | 64 | `CSWSCreature::AIActionCheckMoveToFollowRadius` `0x00510AB0` |
| `0x41` | 65 | `CSWSCreature::AIActionSurrenderToEnemies` `0x0051B420` |

All `AIAction*` handlers take one stack argument: the `CSWSObjectActionNode*`
(VERIFIED - `param_size_bytes == 4` in `k1.db` for every parameterised one, and
the dispatcher pushes `edi` which holds the node).

### 1.4 Move / jump builders (VERIFIED addresses + conventions)

| Address | Function | Conv | Stack bytes |
| --- | --- | --- | --- |
| `0x004F8B60` | `CSWSCreature::AddMoveToPointAction` | thiscall | 68 |
| `0x004F8A50` | `CSWSCreature::AddMoveToPointActionToFront` | thiscall | 68 |
| `0x004EDBA0` | **`CSWSCreature::ForceMoveToPoint`** | thiscall | 4 |
| `0x004FA050` | `CSWSCreature::AddCutsceneMoveActions` | thiscall | 12 |
| `0x004EB5A0` | `CSWSCreature::AddPathfindingWaitActionToFront` | thiscall | 4 |
| `0x004EB510` | `CSWSCreature::AddDriveAction` | thiscall | 24 |
| `0x005B7B30` | `CSWSCreature::ResolveMoveToForceJump` | thiscall | 8 |

`AddMoveToPointAction` takes **17 dwords**. Partially decoded (VERIFIED
fragments, INFERRED naming): arg index 15 (`entry_esp + 0x40`) is the
`Vector* pDestination` (the code does `push eax; lea ecx,[eax+4]; lea edx,[eax+8]`
and feeds those three as node params 0/1/2 with type 2); args 4, 6, 7, 9, 10, 11,
12 and 16 are folded into a packed bitmask that is stored back into the arg-7
slot before being passed to `AddAction`; the action id it passes is literal `1`
(`push 1` at `0x004F8C4D`, VERIFIED). I did **not** fully resolve all 17
arguments, and I recommend against calling it blind (see section 3).

---

## 2. `CSWSObjectActionNode` layout (0x74 bytes)

`k1.db` has the size (`0x74`) but **zero** offsets for this class. The layout
below is VERIFIED by disassembling the constructor (`0x004CAC00`),
`AddActionNodeParameter` (`0x004CAC30`), `AddActionToFront` (`0x004CCB00`) and
`ForceMoveToPoint` (`0x004EDBA0`).

```c
struct CSWSObjectActionNode {          // sizeof == 0x74
/* 0x00 */ uint32_t action_id;         // the dispatch id from the table in 1.3.
                                       // NOT zeroed by the constructor.
/* 0x04 */ int32_t  param_type[13];    // 0x04..0x37 - zeroed by ctor
/* 0x38 */ uint32_t param_value[13];   // 0x38..0x6B - zeroed by ctor.
                                       // raw 4 bytes: float bits, int, object id,
                                       // or CExoString* for type 4
/* 0x6C */ uint16_t group_action_id;
/* 0x6E */ uint16_t num_params;        // set to (index+1) on each Add..Parameter
/* 0x70 */ int32_t  flag_70;           // set to 1 by AddActionToFront and by
                                       // ForceMoveToPoint. INFERRED: "node valid /
                                       // interruptable"
};
```

Constructor (VERIFIED, `0x004CAC00`): loops 13 times writing zero to
`[this+0x04 + 4i]` and `[this+0x38 + 4i]`, then `*(uint16*)(this+0x6E) = 0`.
It does **not** touch `+0x00`, `+0x6C` or `+0x70`.

`AddActionNodeParameter` (VERIFIED, `0x004CAC30`):

```c
void __thiscall CSWSObject::AddActionNodeParameter(
    CSWSObject* this, CSWSObjectActionNode* pNode,
    int32_t nIndex, int32_t nType, void* pValue);
```

- `nIndex >= 13` jumps to an error path at `0x004CAD00`. **13 params max.**
- `pNode->num_params = (uint16)(nIndex + 1)` (unconditionally, even on error).
- `pNode->param_type[nIndex] = nType`.
- `nType` 1, 2 or 3: `pNode->param_value[nIndex] = *(uint32_t*)pValue`
  (a 4-byte copy - the *type tag* is what tells the handler how to read it back).
- `nType == 4`: allocates an 8-byte `CExoString`, `operator=` from `pValue`,
  stores the pointer.
- Any other `nType`: falls through without storing a value.

Type-tag usage, taken from real call sites (VERIFIED values, INFERRED meaning):

| tag | Used for | Evidence |
| --- | --- | --- |
| 1 | float (e.g. `1.0f`) | `ForceMoveToPoint` param 4 |
| 2 | float (position components) | `ForceMoveToPoint` params 0/1/2, `AddMoveToPointAction` params 0/1/2 |
| 3 | int / object id | `ForceMoveToPoint` param 3 (`1`), `JumpToObject` param 0 (`target_object`) |
| 4 | `CExoString` | the string branch in `AddActionNodeParameter` |

Tags 1, 2 and 3 are byte-identical in storage; only the handler distinguishes
them. Do not read meaning into "2 is float and 3 is int" beyond the observed call
sites.

### 2.1 How a move destination is stored (VERIFIED)

Both `ForceMoveToPoint` and `AddMoveToPointAction` store the destination as
**three separate float parameters at indices 0, 1, 2, with type tag 2** - not as
a `Vector*`. That is the canonical encoding.

`ForceMoveToPoint`, target-point branch (VERIFIED, `0x004EDC1B`..`0x004EDCEE`):

```
node->action_id     = 5                       // JumpToPoint
node->group_action_id = forcedAction->group_id
node->flag_70       = 1
param[0] type 2 = forcedAction->target_loc.x
param[1] type 2 = forcedAction->target_loc.y
param[2] type 2 = forcedAction->target_loc.z
param[3] type 3 = (int)1
param[4] type 1 = 1.0f
param[5] type 2 = <stack local, initialised from forcedAction->target_area>
param[6] type 2 = <stack local, 0>
CSWSCreature::AIActionJumpToPoint(node)        // called INLINE, not queued
CSWSObjectActionNode::Destructor(node); free(node)
```

`ForceMoveToPoint`, target-object branch (VERIFIED, `0x004EDCF6`+):

```
node->action_id = 0x30                        // JumpToObject
param[0] type 3 = forcedAction->target_object
param[1] type 1 = ...
```

### 2.2 The other two action-node shapes (VERIFIED sizes and offsets from `k1.db`)

`CSWSForcedAction` (size `0x1C`) - the compact "do this now" descriptor, and the
input to `ForceMoveToPoint`:

```c
struct CSWSForcedAction {   // 0x1C
/* 0x00 */ uint32_t action_id;
/* 0x04 */ uint16_t group_id;
/* 0x08 */ uint32_t target_area;
/* 0x0C */ Vector   target_loc;    // 3 floats
/* 0x18 */ uint32_t target_object;
};
```

`CSWSForcedAction::Constructor` `0x005034D0` (VERIFIED): zeroes `action_id`,
`group_id` and `target_loc`, and sets **both `target_area` and `target_object` to
`0x7F000000` (OBJECT_INVALID)**.

`CSWActionNode` is also size `0x1C` and `CSWSPlayerLastUpdateObject.action_queue`
at `+0x40` is `CSWActionNode[10]` - INFERRED: `CSWActionNode` is the same layout,
used as the save / GUI / replication form of a queued action.

`CSWSCombatRoundAction` (size `0x88`) is a different, combat-only structure with
`move_to_position` at `+0x38`, `should_run` at `+0x54` and `animation` at `+0x78`.
Not part of the object action queue. Do not confuse the two.

---

## 3. What to use for an avatar updated ~20 times per second

### Recommendation: drive the walk stepper yourself, once per game frame

**Primary: `CSWSCreature::WalkUpdateLocationDistance` (`0x00516630`).**

Per game frame, for each remote avatar: take the interpolated remote position
(the existing 100 ms-delayed `SnapshotBuffer` output), turn it into a *facing plus
a distance for this frame*, set the walk/run animation, call the stepper, and
commit the result with `SetPosition`. Never tell the engine about the remote goal
at all: the network already produces a smooth path, so the engine does not need
to path-find to it. It only needs to (a) validate each small step against the
walkmesh, (b) snap Z, (c) play the right animation, (d) update rooms and triggers.

Why this one:

- It is the function the engine itself calls to move a walking creature
  (VERIFIED: called from `WalkUpdateLocation` `0x0051BB10` and from
  `WalkUpdateLocation_QuickWalk` `0x0051D9C0`, which are the only two walk
  drivers).
- It performs exactly the work a raw position write skips (VERIFIED from its call
  graph): `CSWSArea::ClearLineOfSight` `0x0050C330`,
  `CSWSArea::NoNonWalkPolysDetailed` `0x00506650` (x2),
  `CSWSArea::ComputeHeight` `0x004BC380` (**x6** - the ground snap),
  `CSWSArea::IntersectingLineSegment` `0x004BF2C0` (x2),
  `WalkUpdateLocationTestDistance` `0x00512FD0`,
  `CAvoidCreature::PlotPathAroundCreature` `0x005D0840`,
  `CPathfindInformation::SetBlockingCreature` `0x004CC440`, plus animation
  through vtable `+0x7C`, `CSWSObject::AnimationWalking` `0x004CC1C0`, and
  `CSWSCreature::ActivityManager` `0x004F87D0`.
- It reads the creature's **current** `position` (`+0x90`) and `orientation`
  (`+0x9C`) and its `path_find_info` (`+0x340`) - VERIFIED, 10 / 5 / 27 field
  references respectively inside `0x00516630`..`0x00517A20`. So "steer by
  orientation, advance by distance" is its natural interface: exactly what a
  20 Hz stream of absolute positions converts into cleanly.
- There is **no goal** in its state, so a goal that changes 20 times a second
  costs nothing.

Signature, INFERRED from the two call sites (`0x0051DEEF`..`0x0051DF08` and
`0x0051DF77`..`0x0051DF89`) and VERIFIED to be 16 stack bytes / thiscall:

```c
int __thiscall CSWSCreature::WalkUpdateLocationDistance(
    CSWSCreature* this,
    float   fDistance,      // arg0 - distance to advance this frame
    Vector* pNewPositionOut,// arg1 - VERIFIED out: caller immediately does
                            //        fld [out.z]; fsub [this+0x98] (step-height test)
    Vector* pOutB,          // arg2 - out, not identified
    int32_t* pOutC);        // arg3 - out, zero-initialised by the caller
```

`pOutB` / `pOutC` are the weakest part of this document. Both are stack locals
zeroed by the caller before the call, so passing pointers to zeroed 12-byte and
4-byte buffers matches what the engine does, but their meaning is unresolved.

**Teleport primitive: `CSWSCreature::ForceMoveToPoint` (`0x004EDBA0`)**, for a
module change, a first spawn, or a desync larger than a plausible step. It is
synchronous, needs no queue and no AI, and does everything a jump needs
(VERIFIED from `AIActionJumpToPoint` `0x0051D600`'s call graph):
`CSWSModule::IsObjectInLimbo` / `RemoveObjectFromLimbo`,
`CPathfindInformation::ResetInterTileSearchData` + `ResetGridSearchData`,
**`CSWSArea::ComputeSafeLocation` `0x004BE860`**, `CutWaypointPath`,
`CSWParty::ResetAllFollowData`, `CSWSObject::SetPosition`,
**`CSWSCreature::UpdateSubareasOnJumpPosition` `0x0051B940` (twice - leave old
subareas, enter new ones, i.e. rooms and triggers)**, `CSWSCreature::AddToArea`
`0x004FA100`, `CSWSObject::SetOrientation`.

Recipe (VERIFIED from the caller at `0x0051F826`..`0x0051F888`):

```c
CSWSForcedAction fa;                     // 0x1C bytes of stack
CSWSForcedAction_Constructor(&fa);       // 0x005034D0
fa.target_area   = creature->area_id;    // CSWSObject + 0x8C
fa.target_loc    = destination;
fa.target_object = 0x7F000000;           // OBJECT_INVALID -> point branch
creature->+0xA8C = 1;                    // callers do this immediately before
ForceMoveToPoint(creature, &fa);         // 0x004EDBA0, returns 1
```

### Rejected alternatives, and why

**Rejected: `AddMoveToPointAction` / a queued action id 1 per network update.**
This is the "queued pathfinding action" the brief suspected, and it is wrong for
three independent reasons, all VERIFIED:

1. It resets the pathfinder every time. `AddMoveToPointAction` writes
   `creature->+0xA8C = 2` and zeroes `path_find_info->+0x26C/+0x270/+0x274`
   (`0x004F8BCF`..`0x004F8BF3`). At 20 Hz that is a pathfinder restart every 50 ms.
2. `AIActionMoveToPoint` re-queues itself with `AddMoveToPointActionToFront`
   every frame (VERIFIED calls at `0x0051F994`, `0x0051FCF9`, `0x00520002`), so
   the queue is already self-managing; injecting a competing node into it means
   two producers fighting over one queue.
3. It has a **17-argument** signature I could not fully decode, seven of whose
   arguments are folded into an undocumented packed bitmask. Calling it with
   guessed arguments in a NativeAOT plugin is how you get a silent stack
   imbalance in a 32-bit thiscall.

**Rejected: a queued `JumpToPoint` (action id 5) per network update.** It works
positionally but it is a *jump*: `AIActionJumpToPoint` never sets a movement
animation, resets follow data and party formation state, and calls
`AutoCloseGUIPanels` (`0x004EDED0`) - all VERIFIED in its call graph. Twenty of
those a second is the teleporting statue we are trying to get away from, plus
side effects.

**Rejected: keep one MoveToPoint node resident and patch `param_value[0..2]` in
place each tick.** Tempting (no re-queue, no pathfinder reset call), and the
current node is reachable at `CSWSObject + 0x1E4`. But `AIActionMoveToPoint`
caches its goal in `path_find_info` and has a stuck-detector that converts to
`ForceMoveToPoint` on a timer (VERIFIED: `CWorldTimer::CompareWorldTimes` then
`CSWSForcedAction::Constructor` then `ForceMoveToPoint` at
`0x0051F800`..`0x0051F888`). Silently moving the goal underneath a stateful
path-follower is exactly the class of bug that shows up as an avatar that walks
in circles. Keep it as fallback only.

**Rejected: keep writing `CSWSObject + 0x90` directly.** Already measured to
teleport in an idle pose, and now explained: `CSWSObject::SetPosition`
(`0x004CD1A0`) is itself only a position write plus a dirty flag plus
`CSWSArea::UpdatePositionInObjectsArray` (VERIFIED, full disassembly below) - it
does no walkmesh work either. The walkmesh work lives one level up, in
`WalkUpdateLocationDistance`. A raw poke additionally skips the dirty flag, so it
does not even mark the object for the client update pass.

`CSWSObject::SetPosition` in full (VERIFIED, `0x004CD1A0`, thiscall, 16 stack bytes):

```c
BOOL __thiscall CSWSObject::SetPosition(
    CSWSObject* this,
    Vector* pNewPosition,   // arg0
    int32_t bNotifyArea,    // arg1 - if nonzero, calls vtable+0x30 (AsSWSCreature) twice
    int32_t unused,         // arg2 - never read on this path
    int32_t bSuppressDirty);// arg3 - if ZERO, sets dirty bit 1 (0x1FC |= 2)
// body: early-out if the new position equals the old one (Global::equal_2 0x004AA980);
//       resolve the area via APP_MANAGER_PTR -> GetObjectArray -> GetGameObject(area_id);
//       write the three floats to this+0x90;
//       CSWSArea::UpdatePositionInObjectsArray(area, this)   // 0x00506FE0
```

Note the early-out: **`SetPosition` is a no-op if the position is unchanged**, so
a "hold it there" loop through `SetPosition` costs nothing but also refreshes
nothing.

---

## 4. Making it animate

This is the difference between a sliding statue and a walking character, and it
is three fields plus one virtual call.

### 4.1 The animation setter (VERIFIED)

**`SetAnimation` is virtual slot `+0x7C`.** Confirmed by dumping both vtables:

| vtable slot | `CSWSObject` vtable `0x00745F18` | `CSWSCreature` vtable `0x007470C8` |
| --- | --- | --- |
| `+0x30` | `Global::return_zero` | `Global::self_return` -> **`AsSWSCreature()`** |
| `+0x70` | `Global::exit_error_25` | `CSWSCreature::AIUpdate` `0x004FE210` |
| `+0x74` | `CSWSObject::ClearAction` | `CSWSCreature::ClearAction` `0x004FAB00` |
| `+0x7C` | `CSWSObject::SetAnimation` `0x004CCF60` | **`CSWSCreature::SetAnimation` `0x004F0D70`** |
| `+0x94` | `CSWSObject::GetDead` | `CSWSCreature::GetDead` `0x004EF820` |

Every walk site calls it indirectly (`call dword ptr [reg+0x7C]`), never
directly - VERIFIED: the only direct `call 0x004F0D70` in the whole image is from
`CSWSCreature::Constructor`. **Always call through the vtable**, because
`CSWSCreature::SetAnimation` does real work that `CSWSObject::SetAnimation` does not.

`CSWSObject::SetAnimation` (`0x004CCF60`) in full - 4 instructions:

```
this->+0x1FC |= 1          // dirty bit 0 == animation
this->+0xD4  = nAnimation
```

`CSWSCreature::SetAnimation` (`0x004F0D70`) additionally (all VERIFIED):
maps `10000` / `10001` / `-1` through `ambient_animation_state` (`+0x4C4`) using
the jump table at `0x004F0F50`; substitutes injured variants when
`CServerExoApp::IsInjured` (`0x004AEE40`) and the client "gore" option
(`ClientOptions + 0x6D == 5`) both hold (`0x2712 -> 0x276D`, `0x2714 -> 0x276E`);
substitutes `0x2795` when `creature->+0x9FC & 1`; refuses non-death animations
when `GetDead()` (vtable `+0x94`) or `GetDeadTemp()` (`0x004EF890`); pushes
fire-and-forget animations straight to the client twin via
`CSWCObject::AnimationFireAndForget` (`0x0063C380`); and finally writes `+0xD4`
and `SetDirtyFlag(0)`.

### 4.2 The animation constants (VERIFIED)

Internal animation ids are **10000-based** (`0x2710`). Read out of
`CSWSObject::AnimationWalking` (`0x004CC1C0`) and
`CSWSObject::AnimationStationary` (`0x004CAE60`), which are pure predicates over
a `uint16`:

| Value | Dec | Meaning |
| --- | --- | --- |
| `0x2710` | 10000 | idle / pause (base). `SetAnimation` remaps this via ambient state |
| `0x2711` | 10001 | idle variant (also remapped) |
| **`0x2712`** | **10002** | **WALK** - `AnimationWalking` returns true |
| `0x2713` | 10003 | moving (non-stationary per `AnimationStationary`); compared explicitly inside `WalkUpdateLocationDistance` at `0x005173BE` |
| **`0x2714`** | **10004** | **RUN** |
| `0x275E`,`0x275F`,`0x2764`..`0x2767` | | further moving animations (in `AnimationStationary`'s non-stationary set) |
| `0x276D` | 10093 | injured walk (`AnimationWalking` true) |
| `0x276E` | 10094 | injured run |
| `0x2795` | 10133 | third walk variant, used when `creature->+0x9FC & 1` (`AnimationWalking` true) |
| `0x2716`, `0x2718`, `0x27AC` | | death animations (allowed while dead) |

`CSWSObject::AnimationWalking(uint16)` -> true only for `0x2712`, `0x276D`,
`0x2795`. `CSWSObject::AnimationStationary(uint16)` -> **true unless** the value
is one of `{0x2712, 0x2713, 0x2714, 0x275E, 0x275F, 0x2764, 0x2765, 0x2766,
0x2767, 0x276D, 0x276E, 0x2795}`. Both VERIFIED instruction by instruction.

### 4.3 The three fields that must agree (VERIFIED writes, INFERRED names)

Only **three** places in the whole image set a walk or run animation
(exhaustive byte-pattern search for `push 0x2712` / `push 0x2714`):

| Site | What it does |
| --- | --- |
| `WalkUpdateLocation_QuickWalk + 0x309 / + 0x322` | `0x2712` / `0x2714` |
| `AIActionMoveToPoint + 0x128B / + 0x12A1` | `0x2714` / `0x2712` |
| `AIActionMoveToPoint + 0x1407 / + 0x1413` | `0x2714` / `0x2712` |

`WalkUpdateLocation_QuickWalk` at `0x0051DC90` (VERIFIED disassembly):

```c
if (GetActivity(this, 1))                      // 0x004EB190
    rate = GetWalkRate(this);                  // 0x004F1B20 - no animation change
else if (this->+0xA98 == 0) {                  // walk
    if (this->+0x9F0 & 2) vt[0x7C](this, 0x2712);
    rate = GetWalkRate(this);
} else {                                       // run
    if (this->+0x9F0 & 2) vt[0x7C](this, 0x2714);
    rate = GetRunRate(this);                   // 0x004F1BE0
}
if (rate == 0.0f) return 1;                    // bail
```

`AIActionMoveToPoint` at `0x0052075A` (VERIFIED):

```c
if (bRun) { vt[0x7C](this, 0x2714); this->+0xA98 = 1; }
else      { vt[0x7C](this, 0x2712); this->+0xA98 = 0; }
ActivityManager(this, 8);                      // 0x004F87D0
```

So the three fields:

| Field | Type | Meaning (INFERRED) | Evidence |
| --- | --- | --- | --- |
| `CSWSCreature + 0xA98` | int | **run flag**: 0 = walk, nonzero = run | written only in `CSWSCreature::Constructor`, `AIActionMoveToPoint+0x1293` (`=1`, next to `push 0x2714`) and `+0x12A9` (`=0`, next to `push 0x2712`); read only in `WalkUpdateLocation_QuickWalk+0x2F5`. VERIFIED writes; exhaustive. |
| `CSWSCreature + 0x9F0` | uint16 bitfield | **movement permission**; bit 1 (mask `2`) must be set or the walk/run animation is skipped entirely | tested with mask 2 in `AIActionMoveToPoint`, `AIActionCheckMoveToPoint`, `AIActionPartyFollowLeader`, `DoCombatStep`, `AddCounterSpellActions`; `CSWSCreatureStats::SetMovementRate+0x212` writes the literal `0xFFFD` (= `~2`), i.e. an immobile creature has the bit cleared. VERIFIED. |
| `CSWSCreature + 0xA8C` | int | **movement state machine** (see 4.4) | 30+ VERIFIED write sites |
| `CSWSCreature + 0x9FC` | int bitfield | **activity mask**; `GetActivity(mask)` is literally `(this->+0x9FC & mask) != 0` | `0x004EB190`, VERIFIED (4 instructions) |
| `CSWSCreature + 0xA00` | int bitfield | pending / requested activity, used by `SetActivity` and `ActivityManager` | VERIFIED reads/writes, INFERRED meaning |
| `CSWSObject + 0xD4` | int | current animation id | `k1.db` + `SetAnimation` |
| `CSWSObject + 0x80` | uint16 | current action id mirror, written by `RunActions` | VERIFIED |
| `CSWSObject + 0x1E4` | ptr | current `CSWSObjectActionNode*`, written by `RunActions` | VERIFIED |

### 4.4 `CSWSCreature + 0xA8C` - the movement state machine (VERIFIED write sites)

| Value | Written by | Meaning (INFERRED) |
| --- | --- | --- |
| `0` | `CSWSCreature::Constructor` | never moved |
| `1` | `ClearAllActions+0xE9`, `ClearAction+0x108`, `CServerAIMaster::UpdateState+0x18B`, `WalkUpdateLocation_QuickWalk+0x21B`, `AIActionMoveToPoint` (x6) | **idle / stopped** |
| `2` | `AddMoveToPointAction+0x6F`, `AddMoveToPointActionToFront+0x6F`, `AIActionMoveToPoint+0x11F8`, `+0x1342` | move requested |
| `3` | `AIActionPartyFollowLeader+0x71` | following party leader |
| `4`,`5`,`6` | `OnApplyForceJumpDelayed`, `OnApplyForcePush` | force-push / force-jump in flight |
| `-1` | `AIActionMoveToPoint+0x81`, `WalkUpdateLocation_QuickWalk+0x978`, `..._FollowLeader_FindPath+0x34A` | actively walking |

**This field is the direct link to the rendered animation.** The client's
`CSWCCreature::AnimationUpdate` (`0x0061A980`) reads it (VERIFIED at
`0x0061A9CD`):

```c
if (!AnimationStationary(this->+0x48)              // current client animation id
    && GetServerObject() && serverObj->AsCreature()
    && serverCreature->+0xA8C == 0)
        goto stationary_path;
// moving path:
RotateCreatureToOrientation(...);                  // 0x00611850
MoveCreatureToPosition(&this->+0x1DC);             // 0x0060BDF0 - client interpolates
if (!AnimationStationary(this->+0x48)) GetLoopingAnimation();  // 0x0063C100
```

So the **client** does the smooth visual slide and the looping walk cycle, gated
on (a) the client-side animation id at `CSWCObject + 0x48` being a *moving*
animation and (b) the server creature's `+0xA8C` being nonzero. `CSWCObject + 0x48`
is not in `k1.db`; INFERRED to be the current client animation id (uint16) from
its exclusive use as the argument to `AnimationStationary` / `AnimationWalking`.
`CSWCCreature + 0x1DC` is the client's target position (INFERRED from the
`MoveCreatureToPosition` argument).

### 4.5 Rate helpers (VERIFIED)

`CSWSCreature::GetWalkRate` (`0x004F1B20`, thiscall, 0 args, returns float in ST0):

```
rate = stats[+0x19C] * clamp(creature->movement_rate_factor /*+0xA08*/, 0.125f, 1.5f) * 1000.0f
```

Constants read from the image: `0x0073F400 = 0.125f`, `0x0073F414 = 1.5f`,
`0x0073D6FC = 1000.0f`. `CSWSCreature::GetRunRate` is `0x004F1BE0`.
`CSWSCreatureStats + 0x19C` (float) is the base rate; `k1.db` documents
`movement_rate` as an **int** at `+0x194`, so `+0x19C` is a separate,
undocumented float (INFERRED: the resolved rate from `creaturespeed.2da`, whose
columns `RUNRATE` / `WALKRATE` are in `k1.db` as `CreatureSpeed2DA`).
`CSWSCreature::ComputeModifiedMovementRate` is `0x004F6D70`;
`CSWSCreature::ComputeSpeedFactor` is `0x00512F10`.

---

## 5. Walkmesh and ground snap

### 5.1 Height (ground snap) - VERIFIED signatures

```c
// 0x004BC380, thiscall, 12 stack bytes
BOOL __thiscall CSWSArea::ComputeHeight(
    CSWSArea* this,
    Vector*    pPosition,     // arg0
    float*     pfHeightOut,   // arg1 - optional (NULL-checked)
    CSWSRoom** ppRoomOut);    // arg2 - optional (NULL-checked)
// body: room = CSWSArea::GetRoom(pPosition, NULL)  [0x004BB600]
//       if (ppRoomOut) *ppRoomOut = room
//       if (!room) return 0
//       if (pfHeightOut) CSWSRoom::ComputeHeight_2(room, *pPosition /*by value*/, pfHeightOut)
```

```c
// 0x004AEA50, thiscall, 12 stack bytes
BOOL __thiscall CServerExoApp::ComputeHeight(
    CServerExoApp* this,
    uint32_t   nAreaIdIgnored,  // arg0 - VERIFIED: never read
    Vector*    pPosition,       // arg1
    float*     pfHeightOut);    // arg2
// body: CSWSArea::ComputeHeight(GetModule()->GetArea(), pPosition, pfHeightOut, NULL)
```

The ignored first argument is consistent with Odyssey being single-area-resident
(one `CSWSArea*` at `CSWSModule + 0x220`). INFERRED.

Supporting chain (all VERIFIED addresses from `k1.db`):
`CSWSRoom::ComputeHeight` `0x00579430`, `CSWSRoom::ComputeHeight_2` `0x00579470`,
`CSWRoomSurfaceMesh::ComputeHeight` `0x005805E0`, `..::ComputeHeight_2` `0x00581B30`,
`CSWSArea::GetRoom` `0x004BB600` (thiscall, 8 bytes: `Vector*`, `CSWSRoom**`),
`CSWSRoom::LoadWalkMesh` `0x00579520`, `Global::PlaneHeightAtPoint` `0x004A9810`.

### 5.2 Walkability - VERIFIED

```c
// 0x00506400, thiscall, 12 stack bytes
BOOL __thiscall CSWSArea::PositionWalkable(CSWSArea* this, Vector vPosition /*BY VALUE*/);
// body: room = GetRoom(&vPosition, NULL); if (!room) return 0;
//       mesh = room->+0x3C;  ... surface-material test on the walkmesh
```

Note the **by-value `Vector`** - three floats pushed, not a pointer. VERIFIED
from the `lea eax,[esp+0x24]` that reconstructs `&arg0` for the `GetRoom` call.

Related (VERIFIED addresses):
`CSWSArea::NoNonWalkPolys` `0x005086D0` (32), `NoNonWalkPolysDetailed` `0x00506650` (48),
`NoNonWalkPolysInDoors` `0x0050CA10` (40), `CSWSArea::GetSurfaceMaterial` `0x005063C0` (12),
`CSWSArea::ClearLineOfSight` `0x0050C330` (32), `CSWSArea::IntersectingLineSegment` `0x004BF2C0` (12),
`CSWSRoom::CheckAABBWalkable` `0x00579560` (28), `CSWSRoom::ClippedLineSegmentWalkable` `0x00579670` (28),
`CSWRoomSurfaceMesh::GetSurfaceMaterialWalkCheckOnly` `0x00581A10` (12).
`CSWRoomSurfaceMesh` fields `walkable_material_mask +0xDC` / `walk_check_material_mask +0xE0`
are in `k1.db`. `CSWSRoom + 0x3C` as the surface mesh pointer is INFERRED from
`PositionWalkable`. `CWalkHitInfo` (size `0x48`) and `WalkmeshFace` (size `0xC`)
exist in `k1.db` with no offsets.

### 5.3 Safe location - VERIFIED signature, INFERRED arg 1

```c
// 0x004BE860, thiscall, 20 stack bytes
BOOL __thiscall CSWSArea::ComputeSafeLocation(
    CSWSArea* this,
    Vector*  pDesired,                  // arg0
    uint32_t nObjectIdToIgnore,         // arg1 - INFERRED (self)
    CPathfindInformation* pPathInfo,    // arg2 - VERIFIED: this->+0x1AC = arg2, and
                                        //        arg2->+0x04 is read as personal_space
    int32_t  bFlag,                     // arg3
    Vector*  pSafeOut);                 // arg4 - VERIFIED: *pSafeOut = *pDesired first
// returns 1 on success (AIActionJumpToPoint bails on != 1)
```

Call it with `creature->path_find_info` (`CSWSCreature + 0x340`) as `pPathInfo`;
that is exactly what `AIActionJumpToPoint` does (`0x0051D7A6`: `mov edx,[esi+0x340]`).

### 5.4 `CPathfindInformation` (size `0x278`)

From `k1.db` (VERIFIED): `personal_space +0x04` (float),
`cre_personal_space +0x08`, `camera_space +0x10`, `height +0x14`,
`hit_distance +0x18`, `creature_object_id +0x2C`, `start_point +0x5C` (Vector),
`step_tolerance +0x6C`, `end_point +0x74` (Vector).

Additional offsets seen touched by movement code (VERIFIED writes, INFERRED
meaning): `+0x9C` (saved / restored around each `WalkUpdateLocationDistance`
call - the current step direction or waypoint index), `+0x26C`/`+0x270`/`+0x274`
(zeroed by `AddMoveToPointAction`), `+0x254` (set to `OBJECT_INVALID` by
`ClearAllActions`), `+0x258..+0x260` (a Vector zeroed by `ClearAllActions`).

Reset helpers (VERIFIED, all thiscall / 0 args):
`Initialize 0x005CFDA0`, `ResetAtEndOfPath 0x005D0EC0`,
`ResetGridSearchData 0x005CEFA0`, `ResetInterTileSearchData 0x005D00A0`,
`ResetInterAreaSearchData 0x005D0E70`, `ResetInterAreaPathSearchData 0x005D0E00`,
`ResetWayPointData 0x005CF060`, `ComputeStepTolerance 0x005CEC90`,
`SetBlockingCreature 0x004CC440` (4 bytes), `Constructor 0x005D0CE0`.

`CSWSArea` also owns one at `pathfind_info +0x1B0`, plus `path_points +0x23C`
and `path_connections +0x244` (all `k1.db`, VERIFIED).

### 5.5 Subarea (room / trigger) reassignment - VERIFIED addresses

| Address | Function | Conv | Stack |
| --- | --- | --- | --- |
| `0x0051B7B0` | `CSWSCreature::UpdateSubareasOnMoveTo` | thiscall | 20 |
| `0x0051B940` | `CSWSCreature::UpdateSubareasOnJumpPosition` | thiscall | 12 |
| `0x00506FE0` | `CSWSArea::UpdatePositionInObjectsArray` | thiscall | 4 |
| `0x004FA100` | `CSWSCreature::AddToArea` | thiscall | 24 |
| `0x0050DFD0` | `CSWSArea::AddObjectToArea` | thiscall | ? |
| `0x0050DC40` | `CSWSArea::RemoveObjectFromArea` | thiscall | ? |

`WalkUpdateLocation_QuickWalk` calls `UpdateSubareasOnMoveTo` **immediately
before** `SetPosition` (VERIFIED, `0x0051DFB5` then `0x0051DFC6`). Preserve that
order: subareas first, then commit the position.

---

## 6. Stopping the AI from fighting us

### 6.1 What actually drives a creature each frame (VERIFIED)

`CServerExoAppInternal::MainLoop` (`0x004BABB0`) calls, in order:
`ProcessReceivedFrames`, `UpdateStatusLoop`, `TempDeathUpdate`, `UpdateMapData`,
**`CServerAIMaster::UpdateState` (`0x004B0B70`)**, then
**`CServerExoAppInternal::UpdateClientGameObjects` (`0x004B6950`)**.

`CServerAIMaster::UpdateState` has a **quick-walk pre-pass** before the normal
per-object AI. For each object id in an AI bucket (VERIFIED disassembly at
`0x004B0C20`..`0x004B0D0B`):

```c
obj = CServerExoApp::GetGameObject(id);
if (!obj) continue;
if (obj->+0x08 /*object type*/ <= 4) continue;      // byte const at 0x00746633 == 4
if (obj->+0x1F8 == 0) continue;                     // <-- see 6.3
cre = obj->vt[0x30]();  if (!cre) continue;         // AsSWSCreature
node = *(void**)(*(void**)(cre + 0xFC));            // head POSITION
if (node && node->+0x08 && (((*(int*)(node->+0x08)) == 1) ||
                            ((*(int*)(node->+0x08)) == 0x3D)))
        goto quickwalk;                             // MoveToPoint or PartyFollowLeader
if (cre->+0xA8C == 4 || == 5 || == 6) goto quickwalk;
continue;
quickwalk:
    r = CSWSCreature::WalkUpdateLocation_QuickWalk(cre, 0);   // 0x0051D9C0
    if (r == 2) cre->+0xA8C = 1;
```

**This is where a party member's 64 writes per second come from.** Note it is
driven by the queue head plus `+0xA8C`, not by `ai_level` alone.

### 6.2 `ai_level` (`CSWSObject + 0x78`) - VERIFIED

| Address | Function | Conv | Stack |
| --- | --- | --- | --- |
| `0x004B0850` | `CServerAIMaster::AddObject` | thiscall | 8 |
| `0x004AF3D0` | `CServerAIMaster::RemoveObject` | thiscall | 4 |
| `0x004B08A0` | `CServerAIMaster::SetAILevel` | thiscall | 8 |

`AddObject(CSWSObject* pObj, int nAILevel)`: rejects `nAILevel` outside `[0, 4]`;
if `pObj->+0x78 != -1` it calls `RemoveObject` first; adds `pObj->id` to a
`CExoArrayList<uint32>` bucket at `CServerAIMaster + 4 + nAILevel*16`; then sets
`pObj->+0x78 = nAILevel`. `RemoveObject` sets `pObj->+0x78 = -1` and drops the id
from the bucket. All VERIFIED.

So **`ai_level == -1` means "not registered with the AI master"**, and
`CServerAIMaster::RemoveObject` is the clean, reversible off switch. Writing
`-1` into `+0x78` **by hand is not equivalent**: the bucket still contains the
object id and `UpdateState` iterates the buckets, not the field (VERIFIED - the
loop never reads `+0x78`). This is a real trap.

`CServerAIMaster` is reachable from `CServerExoApp::GetServerAIMaster`
`0x004AED80` (VERIFIED), itself reachable from `APP_MANAGER_PTR 0x007A39FC`
(`app->+0x08` is the `CServerExoApp*` - VERIFIED usage pattern in
`SetPosition` and `ActionManager`).

### 6.3 `CSWSObject + 0x1F8` - "visible to the player" (VERIFIED)

Exhaustive search for writes: **one** site outside constructors,
`CSWSMessage::ComputeGameObjectUpdateForObject + 0x46` (`0x005780A6`), which
writes `obj->+0x1F8 = (CSWSMessage::TestObjectVisible(...) == 1)`.

Consumers: `CServerAIMaster::UpdateState` (twice), `AIActionMoveToPoint` (four
comparisons), `WalkUpdateLocation_QuickWalk + 0x20`. All VERIFIED.

**Consequence (INFERRED but well-supported): an off-screen / not-visible creature
does not get walk-updated at all.** Anything that relies on the engine's own
per-frame walk driver will freeze when the avatar leaves the player's view. A
plugin that calls `WalkUpdateLocationDistance` itself from its own frame hook is
immune to this, which is another argument for the manual-step design.

### 6.4 `CSWSObject + 0xE8` - commandable (VERIFIED)

`CSWVirtualMachineCommands::ExecuteCommandSetCommandable` writes
`obj->+0xE8 = value` at `0x00542744`. `CSWSObject::ClearAllActions` early-outs
unless `+0xE8 == 1`, and every `Add*Action` helper reads it. So:

- **`+0xE8 = 0` blocks scripts from queueing new actions** onto the avatar. Good.
- **but `+0xE8 = 0` also makes `ClearAllActions` a no-op.** Order matters: clear
  first, then set `+0xE8 = 0`.

`CSWSObject::AIActionSetCommandable` is `0x0057C7B0`.

### 6.5 `ClearAllActions` (VERIFIED)

`CSWSObject::ClearAllActions(int32_t bAlsoResetState)` at `0x004CCD80`:
requires `+0xE8 == 1`; walks and destroys every queued node; then
`CPathfindInformation::ResetAtEndOfPath(cre->+0x340)`; `cre->+0xA8C = 1`;
`cre->+0x508 = OBJECT_INVALID`; `pathInfo->+0x254 = OBJECT_INVALID`;
`pathInfo->+0x258..+0x260 = 0`.

That is a complete movement reset, which is exactly what you want when claiming a
creature as a phantom.

### 6.6 Limbo (VERIFIED addresses)

| Address | Function | Conv | Stack |
| --- | --- | --- | --- |
| `0x004C83B0` | `CSWSModule::AddObjectToLimbo` | thiscall | 4 |
| `0x004C63F0` | `CSWSModule::RemoveObjectFromLimbo` | thiscall | 4 |
| `0x004C6450` | `CSWSModule::IsObjectInLimbo` | thiscall | 4 |
| `0x004C8C70` | `CSWSModule::LoadLimboCreatures` | thiscall | 4 |
| `0x004C5BB0` | `CSWSModule::SaveLimboCreatures` | thiscall | 4 |

`AIActionJumpToPoint` calls `IsObjectInLimbo` then `RemoveObjectFromLimbo` before
relocating (VERIFIED, `0x0051D667` / `0x0051D684`). INFERRED: limbo is the
"exists but is not in the area" holding state used for party members who are not
the active three. It is the natural place to park an avatar whose peer is in a
different module - and it is the mechanism a spawned phantom body would use
across module transitions.

### 6.7 Recommended levers, in order

1. **`ClearAllActions(1)`** while `+0xE8 == 1`. Empties the queue, resets
   `path_find_info`, sets `+0xA8C = 1`.
2. **`CServerAIMaster::RemoveObject(pObj)`** - takes the creature out of the AI
   buckets entirely, so `UpdateState`'s quick-walk pre-pass never sees it and
   `AIUpdate` is never called on it. Reversible with `AddObject(pObj, level)`.
   Cache the original `ai_level` from `+0x78` first so you can restore it.
3. **`+0xE8 = 0`** (after step 1) so scripts cannot queue actions onto it.
4. **`party_follow_info` (`CSWSCreature + 0x4C0`) must be NULL.** Keep the
   existing rule from `object-model.md`: never claim a party member. A non-NULL
   `+0x4C0` plus `AIActionPartyFollowLeader` (action id `0x3D`) in the queue is
   the 64 Hz contender already measured.
5. Do **not** hand-write `+0x78 = -1` (see 6.2).

---

## 7. Concrete C# plan (NativeAOT, win-x86)

### 7.1 Static addresses and offsets

```csharp
// ---- functions (image base 0x400000, no ASLR) ----------------------------
const nint FN_SetPosition                 = 0x004CD1A0; // thiscall, 16 bytes
const nint FN_SetOrientation              = 0x004CD170; // thiscall,  4 bytes
const nint FN_SetDirtyFlag                = 0x004CC1F0; // thiscall,  4 bytes
const nint FN_IsDirtyFlagSet              = 0x004CC220; // thiscall,  4 bytes
const nint FN_ClearDirtyFlag              = 0x004CC250; // thiscall,  4 bytes
const nint FN_ClearAllActions             = 0x004CCD80; // thiscall,  4 bytes
const nint FN_WalkUpdateLocationDistance  = 0x00516630; // thiscall, 16 bytes
const nint FN_WalkUpdateLocation          = 0x0051BB10; // thiscall,  0 bytes
const nint FN_WalkUpdateLocationQuickWalk = 0x0051D9C0; // thiscall,  4 bytes
const nint FN_ForceMoveToPoint            = 0x004EDBA0; // thiscall,  4 bytes
const nint FN_ForcedActionCtor            = 0x005034D0; // thiscall,  0 bytes
const nint FN_UpdateSubareasOnMoveTo      = 0x0051B7B0; // thiscall, 20 bytes
const nint FN_UpdateSubareasOnJumpPos     = 0x0051B940; // thiscall, 12 bytes
const nint FN_GetWalkRate                 = 0x004F1B20; // thiscall,  0 bytes -> float
const nint FN_GetRunRate                  = 0x004F1BE0; // thiscall,  0 bytes -> float
const nint FN_AreaComputeHeight           = 0x004BC380; // thiscall, 12 bytes
const nint FN_AreaPositionWalkable        = 0x00506400; // thiscall, 12 bytes (Vector by value)
const nint FN_AreaComputeSafeLocation     = 0x004BE860; // thiscall, 20 bytes
const nint FN_AreaGetRoom                 = 0x004BB600; // thiscall,  8 bytes
const nint FN_ObjectGetArea               = 0x004CB120; // thiscall,  0 bytes
const nint FN_AIMasterAddObject           = 0x004B0850; // thiscall,  8 bytes
const nint FN_AIMasterRemoveObject        = 0x004AF3D0; // thiscall,  4 bytes
const nint FN_GetServerAIMaster           = 0x004AED80; // thiscall,  0 bytes
const nint FN_GetCreatureByGameObjectID   = 0x004AE770; // thiscall,  ? (from object-model.md)
const nint FN_GetModule                   = 0x004AE6B0;
const nint FN_ModuleIsObjectInLimbo       = 0x004C6450; // thiscall,  4 bytes
const nint FN_ModuleRemoveObjectFromLimbo = 0x004C63F0; // thiscall,  4 bytes
const nint FN_ModuleAddObjectToLimbo      = 0x004C83B0; // thiscall,  4 bytes

// ---- globals ------------------------------------------------------------
const nint APP_MANAGER_PTR = 0x007A39FC;   // *(void**) -> app; app->+0x08 == CServerExoApp*

// ---- vtable slots (CSWSCreature vtable value 0x007470C8) ----------------
const int VT_AsSWSCreature = 0x30;
const int VT_AIUpdate      = 0x70;
const int VT_ClearAction   = 0x74;
const int VT_SetAnimation  = 0x7C;   // <-- always animate through this
const int VT_GetDead       = 0x94;

// ---- CSWSObject / CSWSCreature offsets ----------------------------------
const int O_ObjectId       = 0x004;
const int O_ObjectType     = 0x008;   // creature == 5
const int O_AiLevel        = 0x078;   // -1 == unregistered
const int O_CurActionId    = 0x080;   // uint16
const int O_AreaId         = 0x08C;
const int O_Position       = 0x090;   // Vector
const int O_Orientation    = 0x09C;   // Vector (Euler)
const int O_Animation      = 0x0D4;
const int O_Commandable    = 0x0E8;   // must be 1 for ClearAllActions
const int O_ActionQueue    = 0x0FC;   // CExoLinkedListInternal*
const int O_DirtyFlags     = 0x1FC;   // bit0 anim, bit1 pos, bit2 orient
const int O_VisibleToPlayer= 0x1F8;   // gates the AI walk driver
const int O_CurActionNode  = 0x1E4;
const int O_ClientObject   = 0x224;
const int C_PathFindInfo   = 0x340;
const int C_PartyFollow    = 0x4C0;   // must be 0 to claim
const int C_MoveFlags      = 0x9F0;   // uint16, bit1 (mask 2) = movement allowed
const int C_ActivityMask   = 0x9FC;
const int C_MoveRateFactor = 0xA08;   // float, clamped [0.125, 1.5]
const int C_MoveState      = 0xA8C;   // 1 idle, 2 requested, 3 follow, -1 walking
const int C_RunFlag        = 0xA98;   // 0 walk, 1 run
const int C_CreatureStats  = 0xA74;

// ---- animation ids ------------------------------------------------------
const int ANIM_IDLE = 0x2710, ANIM_WALK = 0x2712, ANIM_RUN = 0x2714;
const uint OBJECT_INVALID = 0x7F000000;
```

### 7.2 Delegate types

`__thiscall` on win-x86 puts `this` in ECX and the rest on the stack
right-to-left, callee-cleaned. `unmanaged[Thiscall]` maps the **first** managed
parameter to ECX, so declare it as the instance pointer.

```csharp
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct Vec3 { public float X, Y, Z; }

[StructLayout(LayoutKind.Sequential)]
struct ForcedAction {          // 0x1C
    public uint  ActionId;     // 0x00
    public ushort GroupId;     // 0x04
    public ushort _pad;        // 0x06
    public uint  TargetArea;   // 0x08
    public Vec3  TargetLoc;    // 0x0C
    public uint  TargetObject; // 0x18
}

// ---- the step primitive -------------------------------------------------
// int WalkUpdateLocationDistance(this, float dist, Vector* newPos, Vector* outB, int* outC)
delegate* unmanaged[Thiscall]<nint, float, Vec3*, Vec3*, int*, int> WalkStep;

// ---- commit -------------------------------------------------------------
// BOOL SetPosition(this, Vector* pos, int bNotifyArea, int unused, int bSuppressDirty)
delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, int> SetPosition;
// void SetOrientation(this, Vector* dir)
delegate* unmanaged[Thiscall]<nint, Vec3*, void> SetOrientation;
// void UpdateSubareasOnMoveTo(this, a,b,c,d,e)  -- 20 bytes; arg shape UNRESOLVED
delegate* unmanaged[Thiscall]<nint, nint, nint, nint, nint, nint, void> UpdateSubareasOnMoveTo;

// ---- animation (through the vtable, NOT the direct address) -------------
// void SetAnimation(this, int nAnimation)
delegate* unmanaged[Thiscall]<nint, int, void> SetAnimation;   // = *(fn**)(*(nint*)cre + 0x7C)
// CSWSCreature* AsSWSCreature(this)
delegate* unmanaged[Thiscall]<nint, nint> AsSWSCreature;        // = vtable + 0x30

// ---- teleport ----------------------------------------------------------
// void CSWSForcedAction::Constructor(this)
delegate* unmanaged[Thiscall]<ForcedAction*, void> ForcedActionCtor;
// BOOL ForceMoveToPoint(this, CSWSForcedAction*)
delegate* unmanaged[Thiscall]<nint, ForcedAction*, int> ForceMoveToPoint;

// ---- walkmesh ----------------------------------------------------------
// BOOL CSWSArea::ComputeHeight(this, Vector* pos, float* zOut, void** roomOut)
delegate* unmanaged[Thiscall]<nint, Vec3*, float*, nint*, int> AreaComputeHeight;
// BOOL CSWSArea::PositionWalkable(this, Vector BY VALUE)  -> 3 floats on the stack
delegate* unmanaged[Thiscall]<nint, float, float, float, int> AreaPositionWalkable;
// BOOL CSWSArea::ComputeSafeLocation(this, Vector* desired, uint ignoreId,
//                                    CPathfindInformation*, int bFlag, Vector* safeOut)
delegate* unmanaged[Thiscall]<nint, Vec3*, uint, nint, int, Vec3*, int> AreaComputeSafeLocation;
// CSWSArea* CSWSObject::GetArea(this)
delegate* unmanaged[Thiscall]<nint, nint> ObjectGetArea;

// ---- AI levers ---------------------------------------------------------
// void CSWSObject::ClearAllActions(this, int bResetState)
delegate* unmanaged[Thiscall]<nint, int, void> ClearAllActions;
// BOOL CServerAIMaster::AddObject(this, CSWSObject*, int nAILevel)
delegate* unmanaged[Thiscall]<nint, nint, int, int> AIMasterAddObject;
// void CServerAIMaster::RemoveObject(this, CSWSObject*)
delegate* unmanaged[Thiscall]<nint, nint, void> AIMasterRemoveObject;
// CServerAIMaster* CServerExoApp::GetServerAIMaster(this)
delegate* unmanaged[Thiscall]<nint, nint> GetServerAIMaster;

// ---- rates (return float in ST0 -> declare as float return) -----------
delegate* unmanaged[Thiscall]<nint, float> GetWalkRate;
delegate* unmanaged[Thiscall]<nint, float> GetRunRate;
```

`GetWalkRate` / `GetRunRate` return in `ST0`. NativeAOT's x86 `float` return
marshalling expects `ST0` for cdecl-family conventions, so this should be
correct, but treat it as **INFERRED** and validate against a known creature
before trusting the value; if it misbehaves, read
`stats(+0xA74)->+0x19C * clamp(cre->+0xA08, 0.125f, 1.5f) * 1000f` directly instead
(VERIFIED formula).

### 7.3 Claim sequence (once, when a creature becomes a phantom body)

```
0. validate:  *(uint*)(obj+0x00) == 0x007470C8         // creature vtable
              *(nint*)(obj+0x4C0) == 0                 // NOT a party member
              *(nint*)(obj+0xA74) != 0                 // has stats
1. savedAiLevel   = *(int*)(obj + 0x78)
   savedCommandable = *(int*)(obj + 0xE8)
2. *(int*)(obj + 0xE8) = 1                             // ClearAllActions needs this
   ClearAllActions(obj, 1)                             // empties queue, resets path info,
                                                       // sets +0xA8C = 1
3. *(int*)(obj + 0xE8) = 0                             // block scripts from re-queueing
4. aiMaster = GetServerAIMaster(serverExoApp)
   AIMasterRemoveObject(aiMaster, obj)                 // out of the AI buckets;
                                                       // sets +0x78 = -1 for us
5. *(ushort*)(obj + 0x9F0) |= 2                        // ensure movement is permitted
6. teleport to the peer's first known position via ForceMoveToPoint (7.5)
```

Release is the mirror: restore `+0xE8`, then `AIMasterAddObject(aiMaster, obj,
savedAiLevel)` if `savedAiLevel` was in `[0,4]`.

### 7.4 Per-frame drive (the walking path)

Hook a point that runs **inside** `CServerExoAppInternal::MainLoop`
(`0x004BABB0`) **after** `CServerAIMaster::UpdateState` and **before**
`CServerExoAppInternal::UpdateClientGameObjects` (`0x004B6950`). The
`call CServerExoAppInternal::UpdateClientGameObjects` at `0x004BAC81` is the
natural hook site (VERIFIED call site). Running there means the position and
animation you set are picked up by the same frame's client update, and you are
not racing the AI master.

```
for each remote avatar (creature obj, interpolated target position T):

  cur   = *(Vec3*)(obj + 0x90)
  delta = T - cur
  dist  = length(delta.xy)                      // 2-D: the stepper handles Z

  // -- teleport case -------------------------------------------------
  if (peer changed module) { park in limbo (6.6); continue; }
  if (dist > TELEPORT_THRESHOLD)                // e.g. > 3 * maxStep
      { ForceMoveToPointTo(obj, T); continue; }  // 7.5

  // -- idle case -----------------------------------------------------
  if (dist < IDLE_EPSILON) {
      if (*(int*)(obj + 0xA8C) != 1) {
          vt_SetAnimation(obj, ANIM_IDLE);       // 0x2710 -> remapped to ambient
          *(int*)(obj + 0xA8C) = 1;
      }
      continue;
  }

  // -- 1. face the direction of travel -------------------------------
  Vec3 dir = normalize(delta);                   // caution: see 7.6 on the
  SetOrientation(obj, &dir);                     //          orientation encoding

  // -- 2. choose walk vs run and set the animation -------------------
  float dtSeconds = frameDelta;
  float speed     = dist / dtSeconds;
  bool  run       = speed > GetWalkRate(obj) * WALK_RUN_MARGIN;
  *(int*)(obj + 0xA98) = run ? 1 : 0;            // run flag the QuickWalk path reads
  int wantAnim = run ? ANIM_RUN : ANIM_WALK;
  if (*(int*)(obj + 0xD4) != wantAnim)
      vt_SetAnimation(obj, wantAnim);            // vtable +0x7C
  *(int*)(obj + 0xA8C) = -1;                     // "actively walking": the client's
                                                 // AnimationUpdate gate

  // -- 3. let the engine take the step (walkmesh + ground snap) ------
  Vec3 newPos = default; Vec3 outB = default; int outC = 0;
  float step  = min(dist, (run ? GetRunRate(obj) : GetWalkRate(obj)) * dtSeconds);
  int   r     = WalkStep(obj, step, &newPos, &outB, &outC);

  // -- 4. commit -----------------------------------------------------
  UpdateSubareasOnMoveTo(obj, ...);              // BEFORE SetPosition (engine order)
  SetPosition(obj, &newPos, 1, 0, 0);            // last arg 0 => sets dirty bit 1
```

The engine's own order in `WalkUpdateLocation_QuickWalk` is
`WalkUpdateLocationDistance` -> (`AutoCloseGUIPanels`) ->
`UpdateSubareasOnMoveTo` -> `SetPosition` -> `SetOrientation` (VERIFIED at
`0x0051DF08`, `0x0051DF9A`, `0x0051DFB5`, `0x0051DFC6`, `0x0051E04F`). Match it.

### 7.5 Teleport helper

```csharp
static void ForceMoveToPointTo(nint cre, Vec3 dest) {
    ForcedAction fa;
    ForcedActionCtor(&fa);                        // area/object = OBJECT_INVALID
    fa.TargetArea   = *(uint*)(cre + 0x8C);       // current area
    fa.TargetLoc    = dest;
    fa.TargetObject = OBJECT_INVALID;             // -> the JumpToPoint branch
    *(int*)(cre + 0xA8C) = 1;                     // callers do this first
    ForceMoveToPoint(cre, &fa);                   // walkmesh-safe + subareas + orientation
}
```

Optionally pre-validate the destination:
`AreaComputeSafeLocation(area, &dest, objectId, *(nint*)(cre + 0x340), 1, &safe)`
and use `safe` instead - which is what `AIActionJumpToPoint` does internally
anyway, so it is belt and braces.

### 7.6 Loose ends the implementation must resolve at runtime

These are the honest gaps; none of them can be closed statically without calling
into the game.

1. **`WalkUpdateLocationDistance` args 2 and 3.** `pNewPositionOut` is solid
   (the caller immediately does `fld [out.z]; fsub [this+0x98]`). `pOutB` and
   `pOutC` are unresolved out-params. Mitigation: pass pointers to zeroed 12-byte
   and 4-byte buffers, exactly as both engine call sites do, and log what comes
   back.
2. **The return value of `WalkUpdateLocationDistance`.** It is stored and tested
   by callers; values not enumerated. Log it before branching on it.
3. **`UpdateSubareasOnMoveTo`'s 5 arguments (20 bytes)** are not decoded.
   Fallback: skip it initially and accept that room reassignment and trigger
   entry lag by one teleport (or fall back to `ForceMoveToPoint`, which uses
   `UpdateSubareasOnJumpPosition` internally and needs no argument decoding).
4. **`SetOrientation`'s expected encoding.** `k1.db` types `+0x9C` as a `Vector`,
   and the engine builds it with `Global::normalize_2` (`0x004AB180`) /
   `Vector::Normalize` (`0x004AB130`) from a direction vector, then feeds it to
   `SetOrientation` (`0x0051E043` -> `0x0051E04F`, VERIFIED). So it is a
   normalized direction vector, not an angle. The existing UNVERIFIED caveat in
   `object-model.md` about facing conversion stays open.
5. **`+0x1F8` (visible to player)** freezes the engine's own walk driver for
   off-screen creatures. The manual-step design in 7.4 sidesteps it, but
   `WalkUpdateLocationDistance` itself may still contain visibility-dependent
   branches (it does call `CGuiInGame::SetCombatMessage` at `0x005171CB`).
   Test with the avatar behind the camera.
6. **`WalkUpdateLocationDistance` can queue actions.** It calls
   `CSWSObject::AddActionToFront` at `0x0051759E` and `0x005175E5` (VERIFIED) -
   presumably door-opening or bump-avoidance. If you have set `+0xE8 = 0` to lock
   the queue, verify those paths do not misbehave, and consider draining the
   queue every frame after the step.
7. **`GetWalkRate` / `GetRunRate` float returns** across the NativeAOT x86 ABI -
   validate, and fall back to the arithmetic in 4.5 if wrong.
8. **Thread affinity.** `MainLoop` is the game's own thread. All of this must run
   on it (which is what hooking `MainLoop` gives you). The existing plugin worker
   thread must not touch these functions.

### 7.7 Fallback design, if the manual step proves unworkable

Let the engine walk and only *retarget*, at a low rate:

- Keep exactly one `MoveToPoint` node (action id `1`) alive.
- Re-issue the destination only when the remote goal has moved more than a
  threshold (say 0.5 m) **or** every ~250 ms, not every 50 ms - this keeps the
  pathfinder resets down by 5x.
- Reissue by patching the resident node's `param_value[0..2]`
  (`node + 0x38 + 0..8`) rather than calling `AddMoveToPointAction`, and find the
  node at `*(nint*)(obj + 0x1E4)` (current) or through the queue head.
- Leave `ai_level` intact so `CServerAIMaster::UpdateState`'s quick-walk pre-pass
  keeps stepping the creature.

This gives correct animation and walkmesh behaviour for free, at the cost of the
avatar path-finding to a stale goal and of the stuck-detector occasionally firing
`ForceMoveToPoint`. It is the lower-risk, lower-fidelity option.

---

## 8. Corrections and additions to `docs/re/object-model.md`

- The claim "a position write sticks unless some system is actively driving that
  creature" is right, and section 6.1 gives the exact predicate: the AI master's
  quick-walk pre-pass steps a creature whose **queue head is action id 1
  (MoveToPoint) or `0x3D` (PartyFollowLeader)**, or whose `+0xA8C` is 4, 5 or 6 -
  and only if `+0x1F8` (visible to player) is nonzero.
- `CSWSObject + 0x1FC` is a **dirty-flag bit array**, not a scalar:
  `SetDirtyFlag(n)` does `*(uint*)(this + 0x1FC + (n >> 5) * 4) |= 1u << (n & 31)`
  (VERIFIED, `0x004CC1F0`). Bit 0 = animation, bit 1 = position, bit 2 =
  orientation. A raw poke at `+0x90` sets none of them.
- `CSWSObject + 0xE8` is **commandable** (VERIFIED via
  `ExecuteCommandSetCommandable`), not documented in `k1.db`.
- `CSWSObject + 0x1F8` is **visible-to-player** (VERIFIED via
  `ComputeGameObjectUpdateForObject`), not documented in `k1.db`.
- `CSWSObjectActionNode`'s layout is now known (section 2); `k1.db` has only the
  size.
- Object type `5` == creature (INFERRED from the dispatcher gate plus the byte
  constant `4` at `0x00746633` used as the AI master's "is at least a creature"
  threshold). This partially answers the `GAME_OBJECT_TYPES` caveat.
- `docs/re/object-model.md` links to `docs/re/movement.md`, which does not exist.
  This file is `docs/re/engine-movement.md`; the link should be updated (left
  alone here to stay inside my own file).
