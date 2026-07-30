# Spawning a new creature in swkotor.exe

How to make the running game construct a **genuinely new** creature, so a co-op mod can
give each remote player its own body instead of hijacking an existing NPC.

Every claim below is labelled **VERIFIED** (checked against the binary, against the live
process read-only, or against the game's own data files) or **INFERRED** (reasoned, not
observed). Read `object-model.md` first: it establishes the address database, the absence
of ASLR, and the `CSWSCreature` layout this document builds on.

Nothing in this investigation wrote to the game, called a game function, or touched the
process beyond `ReadProcessMemory`.

---

## 1. Bottom line

**The NWScript engine-routine table is found, and it is live-verified.**

| | |
| --- | --- |
| Table location | `*(uint32*)(command_implementer + 0x0C)` - a **heap** array, not a static one |
| Live value on this install | `0x06AAE2E0` (session-scoped, re-resolve every run) |
| Length | 772 entries, 4 bytes each (`0x0C10` bytes) |
| Index | the NWScript routine number, directly: `table[n]` |
| **`CreateObject` = routine 243** | **`table[243] == 0x0052F820`** |
| That address | `CSWVirtualMachineCommands::ExecuteCommandCreateObject`, `__thiscall`, 8 bytes of args |

`0x0052F820` is a fixed code address in a non-ASLR image, so **the table is only needed as
proof, not at runtime**. What *is* needed at runtime is the `this` pointer, which comes
from the same chain.

Pointer chain (**VERIFIED live**, pid 28524, read-only):

```
VIRTUAL_MACHINE_PTR  0x007A3A00
  -> CVirtualMachine*         0x1BFACFC0     (a 4-byte wrapper; *this == internal)
  -> CVirtualMachineInternal* 0x1BF5BBF8
  -> +0x3D4 command_implementer  0x069CBAE8  <-- `this` for every ExecuteCommand*
       vtable = 0x007450E4                   == CSWVirtualMachineCommands vtable, exact
       +0x04 has_caller_object = 1
       +0x08 caller_object_id  = 0x0000000B
  -> +0x0C commands            0x06AAE2E0    <-- the 772-entry table
       [243] = 0x0052F820                    == ExecuteCommandCreateObject
```

---

## 2. How the table was found and verified

### Located

The allocation flagged in `object-model.md` (`push 0x0C10` at `0x0054C964`) is inside
`CSWVirtualMachineCommands::InitializeCommands` at **`0x0054C960`** (`__thiscall`, named in
the database). **VERIFIED** by disassembly:

```
0054C960  push ebx/ebp/esi/edi
0054C964  push 0xc10                 ; 772 * 4
0054C969  mov esi, ecx               ; this = CSWVirtualMachineCommands (size 0x10)
0054C96B  call 0x6fa7e6              ; Global::operator_new_2
0054C973  mov [esi+0xc], eax         ; this->commands = table
0054C976..0054C992                   ; zero-fill all 0xC10 bytes
0054C994  mov edx, [esi+0xc]         ; then 679 stores of the form
0054C997  mov ecx, 0x53f5c0          ;   mov [table + n*4], <routine address>
0054C99C  mov [edx+0x134], ecx       ;   0x134/4 = index 77
...
0054E9C8  jmp 0x5cd2c0               ; tail-call InitializeSWMGCommands
```

The dispatcher, at the bounds check already known from `object-model.md` (**VERIFIED**):

```
0052C0D0  mov edx, [esp+4]           ; nCommandId
0052C0D4  cmp edx, 0x304             ; 772
0052C0DA  jge fail
0052C0DC  mov eax, [ecx+0xc]         ; this->commands
0052C0DF  mov eax, [eax+edx*4]       ; table[nCommandId]
0052C0E2  test eax, eax
0052C0E4  je  fail                   ; unimplemented slot
0052C0E6  push esi
0052C0E7  mov  esi, [esp+0xc]        ; nParams
0052C0EB  push esi
0052C0EC  push edx                   ; nCommandId
0052C0ED  call eax                   ; ecx still == this
0052C0F0  ret 8
fail:
0052C0F3  mov eax, 0xfffff82e        ; -2002
```

So every routine is `__thiscall int Fn(CSWVirtualMachineCommands* this, int nCommandId, int nParams)`.
**VERIFIED**, and it matches the database's `param_size_bytes = 8` on all 567 labelled
`ExecuteCommand*` functions.

### Verified, five independent ways

1. **Static extraction vs. the live table.** I re-derived the whole table offline by
   interpreting all 679 memory stores in `InitializeCommands`, then read the live table out
   of the running process. **675 of 772 entries are byte-identical.** The other 97 are zero
   statically and non-zero live. **VERIFIED.**
2. **The 97 are fully accounted for.** They are exactly the `SWMG_*` minigame routines
   (indices 520, 521 and the contiguous block 582-639 plus stragglers), and they are all
   filled by a *second* initialiser, `CSWVirtualMachineCommands::InitializeSWMGCommands` at
   **`0x005CD2C0`**, which `InitializeCommands` tail-jumps to. Every live entry lands in
   `0x005CB000-0x005CD400`, the SWMG code region. 675 + 97 = 772, **zero unexplained slots,
   zero null slots at runtime**. **VERIFIED.**
3. **Routine number vs. Ghidra name.** Cross-referencing table index against
   KOTOR 1's `nwscript.nss` declaration order gives **450 exact matches** of the form
   `table[n]` is named `ExecuteCommand<nwscript name at n>`, with **no contradiction
   anywhere**. **VERIFIED.**
4. **Every non-exact case is explained.** Of 675 filled indices there are only 523 distinct
   implementations: 67 functions are deliberately shared across 219 routine numbers, which
   is precisely why Ghidra named them generically. `fabs/cos/sin/tan/acos/asin/atan/log/pow/sqrt/abs`
   (routines 67-77) are all one `ExecuteCommandMath` at `0x0053F5C0`; `d2..d100`
   (95-103) are all `ExecuteCommandRollDice`; `FortitudeSave/ReflexSave/WillSave` (108-110)
   are all `ExecuteCommandSavingThrow`; `ActionMoveToLocation` (21) and
   `ActionForceMoveToLocation` (382) are both `ExecuteCommandMoveToPoint`. After removing
   shared implementations only **20** indices differ by name, and 16 of those are plain
   synonyms (`ActionAttack`/`Attack`, `ResistForce`/`ResistSpell`,
   `GetNearestTrapToObject`/`GetNearestMine`, `SetPlanetSelectable`/`SetGalaxyMapPlanetSelectable`, ...).
   **VERIFIED.**
5. **The `CreateObject` neighbourhood is aligned exactly.** **VERIFIED:**

   | idx | table entry | database name | nwscript.nss name |
   | --- | --- | --- | --- |
   | 240 | `0x00544270` | `ExecuteCommandActionSpeakStringByStrRef` | `ActionSpeakStringByStrRef` |
   | 241 | `0x0052FF20` | `ExecuteCommandDestroyObject` | `DestroyObject` |
   | 242 | `0x0053BDF0` | `ExecuteCommandGetModule` | `GetModule` |
   | **243** | **`0x0052F820`** | **`ExecuteCommandCreateObject`** | **`CreateObject`** |
   | 244 | `0x00535960` | `ExecuteCommandEventSpellCastAt` | `EventSpellCastAt` |
   | 245 | `0x0053D110` | `ExecuteCommandGetSpellCast` | `GetLastSpellCaster` |
   | 247 | `0x0053E220` | `ExecuteCommandGetUserDefinedEventNumber` | `GetUserDefinedEventNumber` |

**Corroborating structural proof, independent of all names.** `ExecuteCommandCreateObject`
dispatches its `nObjectType` argument through a jump table: a 128-byte case index at
`0x0052FDA8` and 6 targets at `0x0052FD90`. Exactly five of the 128 slots are non-default,
at type values **1, 2, 32, 64, 128** - which are precisely
`OBJECT_TYPE_CREATURE=1, OBJECT_TYPE_ITEM=2, OBJECT_TYPE_WAYPOINT=32, OBJECT_TYPE_PLACEABLE=64,
OBJECT_TYPE_STORE=128` from `nwscript.nss`, and precisely the set the routine's own doc
comment lists. The type-1 branch allocates **`0xAC8`** bytes, the exact size of
`CSWSCreature`. **VERIFIED.**

Finally: `ExecuteCommandCreateObject` has **zero** direct `E8 rel32` callers anywhere in
`.text`. It is reachable only through `call eax` in the dispatcher, confirming the table is
the sole entry point. **VERIFIED.**

### Other routine addresses this unlocks

**VERIFIED** (index -> live table entry -> database name):

| Routine | # | Address | Database name |
| --- | --- | --- | --- |
| `AssignCommand` | 6 | `0x0052E720` | `ExecuteCommandAssignCommand` |
| `DelayCommand` | 7 | `0x0052FE30` | `ExecuteCommandDelayCommand` |
| `ClearAllActions` | 9 | `0x0052F4A0` | `ExecuteCommandClearAllActions` |
| `SetFacing` | 10 | `0x00542830` | `ExecuteCommandSetFacing` |
| `ActionMoveToLocation` | 21 | `0x0053FE00` | `ExecuteCommandMoveToPoint` |
| `ActionMoveToObject` | 22 | `0x0053FB00` | `ExecuteCommandMoveToObject` |
| `GetPosition` | 27 | `0x0053CAE0` | `ExecuteCommandGetPosition` |
| `ActionPlayAnimation` | 40 | `0x00540550` | `ExecuteCommandPlayAnimation` |
| `SetCommandable` | 162 | `0x005426C0` | `ExecuteCommandSetCommandable` |
| `ActionJumpToLocation` | 214 | `0x0052CDC0` | `ExecuteCommandActionJumpToPoint` |
| `Location` | 215 | `0x0053F3B0` | `ExecuteCommandLocation` |
| `GetIsPC` | 217 | `0x0053F1C0` | `ExecuteCommandGetIsPC` |
| `DestroyObject` | 241 | `0x0052FF20` | `ExecuteCommandDestroyObject` |
| **`CreateObject`** | **243** | **`0x0052F820`** | **`ExecuteCommandCreateObject`** |
| `JumpToLocation` | 313 | `0x0052CDC0` | (shared with 214) |
| `ChangeToStandardFaction` | 412 | `0x0052F2F0` | `ExecuteCommandChangeFaction` |
| `GetFirstPC` | 548 | `0x0053C920` | `ExecuteCommandGetPC` |

The full 772-row map is reproducible offline in seconds; see section 9.

---

## 3. The call contract

### 3.1 What `CreateObject` is, at the machine level

```
int __thiscall ExecuteCommandCreateObject(   // 0x0052F820
        CSWVirtualMachineCommands* this,     // ECX
        int nCommandId,                      // [esp+4]  must be 243
        int nParams);                        // [esp+8]  must be 4
```

**VERIFIED.** `ret 8`, so the callee pops the two stack arguments; standard MSVC `__thiscall`.

`this` must be the live `command_implementer` from section 1. In fact
`ExecuteCommandCreateObject` never reads `this` (the compiler spills ECX and then reuses the
slot for the popped `nObjectType`), but pass the real pointer regardless: it costs nothing
and the general contract requires it. `this->has_caller_object` / `caller_object_id`
(`OBJECT_SELF`) are **not** consulted by `CreateObject`, so no caller object needs setting up.
**VERIFIED.**

`nParams` is load-bearing: `cmp dword [esp+0x68], 4; jl` skips popping the 4th argument when
`nParams < 4`. Pass 4 and push 4. **VERIFIED** (`[esp+0x68]` is the original `[esp+8]` after
the 0x60-byte prologue).

### 3.2 Arguments travel on the VM stack, not the C stack

`CreateObject`'s script-visible signature is

```c
object CreateObject(int nObjectType, string sTemplate, location lLocation, int bUseAppearAnimation=FALSE);
```

and the implementation pops them in **declaration order**, meaning the VM pushes them in
**reverse** declaration order. **VERIFIED** from the pop sequence at the top of the function:

```
0052F864  call 0x5d1000   CVirtualMachine::StackPopInteger(&nObjectType)
0052F878  call 0x5d1080   CVirtualMachine::StackPopString(&CExoString sTemplate)
0052F88E  call 0x5d10a0   CVirtualMachine::StackPopEngineStructure(2 /*LOCATION*/, &pLocation)
0052F8A9  call 0x5d1000   CVirtualMachine::StackPopInteger(&bUseAppearAnimation)   ; only if nParams >= 4
   any pop failing  -> return 0xFFFFF82F (-2001)
```

So the required push order is: `bUseAppearAnimation`, then the location, then the template
string, then `nObjectType`.

### 3.3 The VM stack API

All `__thiscall` on **`this = *(void**)0x007A3A00`** (the `CVirtualMachine` wrapper, *not*
the internal). `CVirtualMachine` is a 4-byte shim - each method is literally
`mov ecx,[ecx]; jmp <internal>`. **VERIFIED.**

| Address | Signature | args |
| --- | --- | --- |
| `0x005D1010` | `int StackPushInteger(void* vm, int v)` | 4 |
| `0x005D1030` | `int StackPushFloat(void* vm, float v)` | 4 |
| `0x005D1050` | `int StackPushVector(void* vm, Vector v)` | 12, **by value** |
| `0x005D1090` | `int StackPushString(void* vm, CExoString* s)` | 4 |
| `0x005D10B0` | `int StackPushEngineStructure(void* vm, int nType, void* p)` | 8 |
| `0x005D10D0` | `int StackPushObject(void* vm, uint oid)` | 4 |
| `0x005D10C0` | `int StackPopObject(void* vm, uint* out)` | 4 |
| `0x005D1000` | `int StackPopInteger(void* vm, int* out)` | 4 |

All **VERIFIED** against the database's `calling_convention` / `param_size_bytes` columns and
against the disassembly. Argument order for the two-argument forms is `(nType, pointer)`,
**VERIFIED** from `StackPushEngineStructure`'s body (`arg1 + 0x10` becomes the VM stack type
tag, `arg2` is handed to the copy helper).

**Both pushes copy. Neither takes ownership.** **VERIFIED:**

- `StackPushString` (`0x005D1930`) allocates a fresh 8-byte `CExoString` with the game's
  own `operator new` and copies the text out of ours via `CExoString::CStr()`.
- `StackPushEngineStructure` (`0x005D19C0`) calls the implementer's virtual at
  **vtable+0x20**, which the database names
  `CSWVirtualMachineCommands::CopyGameDefinedStructure` (`0x0052C140`), and pushes the
  *copy*.

So the `CExoString` and the `CScriptLocation` we hand in can live on our own stack. We must
free neither.

**The stack grows on demand and is safe to push into when empty.** **VERIFIED** from
`CVirtualMachineStack::AddToTopOfStack` (`0x005D15A0`): when `stack_pointer == total_size`
it bumps `total_size` by `0x100`, allocates `total_size*4` value bytes plus `total_size`
type bytes, copies, and frees the old buffers - and the copy is skipped when
`stack_pointer == 0`. The live idle state is `stack_pointer = 0, total_size = 0,
values = NULL, types = NULL`, and that is fine.

### 3.4 What a script `location` looks like in memory

`CScriptLocation`, size **`0x18`** (24 bytes), no vtable. **VERIFIED** (database + the
`push 0x18; new` in `ExecuteCommandLocation`):

```
+0x00  Vector position      3 floats  x, y, z   world coordinates, same units as CSWSObject+0x90
+0x0C  Vector orientation   3 floats            a NORMALISED direction vector, not an angle
```

`CreateObject` uses it as: `SetPosition(loc + 0x00, ...)`, `SetOrientation(loc + 0x0C)`, and
reads `loc[0..2]` as x/y/z for `ComputeSafeLocation` and `AddToArea`. It **ignores any area
field**, because Odyssey resolves the area itself (section 4). **VERIFIED.**

How the engine itself builds one, from `ExecuteCommandLocation` (`0x0053F3B0`) - **VERIFIED**:

```
orientation = normalize( cos(fFacing * pi/180), sin(fFacing * pi/180), 0 )
```

(`0x00747E00` holds the double `0.017453292519943295`; `0x004AB180` is `Global::normalize_2`.)
Note this is the script `Location()` helper's convention and it is **not obviously the same
as the GIT-file convention** (`-atan2(X, Y)`, 0 = +Y) recorded as unverified in
`object-model.md`. Reconciling them belongs to the movement work; for *spawning*, any unit
vector will do. **Do not pass `(0,0,0)`** - it is normalised somewhere downstream.

### 3.5 Return value

Two channels. **VERIFIED:**

- **The C return** is a status `int`: `0` on success, `-2001` (`0xFFFFF82F`) if an argument
  pop failed, `-2000` (`0xFFFFF830`) if the object push failed, `-2002` (`0xFFFFF82E`) from
  the dispatcher for an out-of-range or unimplemented routine.
- **The created object id comes back on the VM stack.** The function ends with
  `StackPushObject(oid)` at `0x0052FD0B`. Retrieve it with
  `StackPopObject(vm, &oid)` immediately after the call.

On any failure path the pushed id is the initial value of `ESI`, which is loaded at function
entry from the global `0x00746644` = **`0x7F000000` = `OBJECT_INVALID`**. **VERIFIED** by
reading the image. So a failed spawn yields `oid == 0x7F000000` and status `0`, not a crash.

### 3.6 What the creature branch actually does

Full **VERIFIED** trace of `nObjectType == 1`, starting at `0x0052F9A3`:

| Step | Call | Notes |
| --- | --- | --- |
| 1 | `Global::operator_new_2(0xAC8)` `0x006FA7E6` | `sizeof(CSWSCreature)` |
| 2 | `CSWSCreature::Constructor(0x7F000000, 0)` `0x004F7A10` | id `OBJECT_INVALID` means "allocate me one" |
| 2a | -> `CSWSObject::Constructor(type, oid, 0)` `0x004CFCB0` | `type` = `byte[0x00746634]` = **5**, the internal creature `GAME_OBJECT_TYPE` (this resolves the "not in the database" caveat in `object-model.md`) |
| 2b | -> `CServerExoApp::GetObjectArray()` `0x004AED70` then `CGameObjectArray::AddInternalObject(&obj->object_id, obj, type)` `0x004D80E0` | **only when the passed id is `0x7F000000`**; allocates a fresh id, writes it to `obj+0x04`, and inserts a `{index, object, next}` node into bucket `id & 0xFFF` |
| 3 | `CResRef::Constructor_4(char*)` `0x00406D80` | builds a 16-byte `CResRef` from the script string |
| 4 | `CSWSCreature::LoadFromTemplate(CResRef*, 0)` `0x005026D0` | opens the blueprint via `CResGFF::Constructor_2(0x7EB, "UTC ", resref)` - `0x7EB` = 2027 = `.utc`. Non-zero return = success; zero = the object is deleted and `OBJECT_INVALID` is returned |
| 5 | `CSWSObject::SetOrientation(loc+0x0C)` `0x004CD170` | |
| 6 | `CSWSArea::ComputeSafeLocation(pos, 20.0f, creature->path_find_info /*+0x340*/, 1, &outPos)` `0x004BE860` | walkmesh search within 20 units; on success the position is replaced |
| 7 | `CSWSArea::ComputeHeight(...)` `0x004BC380` via the area's `+0x11C` sub-object | ground snap for z |
| 8 | if `bUseAppearAnimation`: `CGameEffect::Constructor(1)` + `SetCreator` + `CSWSObject::ApplyEffect` `0x004D14F0` | effect type `word[0x007467C0]` = 81 |
| 9 | `CSWSCreature::AddToArea(area, x, y, z, 1, 1)` `0x004FA100` | -> `CSWSObject::SetArea`, `CSWSArea::AddObjectToArea` (`0x0050DFD0`) at `0x004FA303`, `CServerAIMaster::SetAILevel` (`0x004B08A0`), area-of-effect re-evaluation |
| 10 | `esi = creature->object_id` (`+0x04`), then `StackPushObject(esi)` | |

So the answer to "does it need registering afterwards" is **no, both registrations are
inside the path**: the **object array** is populated by the *constructor* (step 2b) and the
**area** by `AddToArea` (step 9). Calling `CreateObject` is sufficient. **VERIFIED.**

---

## 4. Which blueprint to spawn

`CreateObject`'s `sTemplate` is a `.utc` ResRef resolved through the normal resource
manager, so anything in `chitin.key` is available in every module. `data/templates.bif`
holds **205 `.utc` blueprints** that are always resolvable. **VERIFIED** by parsing
`chitin.key` + `templates.bif` from the install directory.

**Recommended: `n_commm`** (generic male commoner; `n_commf` is the female twin). Parsed
straight out of `templates.bif` - **VERIFIED**:

| field | `n_commm` | `n_commf` | `p_carth` |
| --- | --- | --- | --- |
| `Tag` | `CommM` | `CommF` | `Carth` |
| `Appearance_Type` | 17 | 14 | 6 |
| `FactionID` | **5 = Neutral** | 5 | 2 = Friendly_1 |
| `Conversation` | *(empty)* | *(empty)* | `k_hcar_dialog` |
| `ScriptSpawn` / `Heartbeat` / `OnNotice` | `k_def_spawn01` / `k_def_heartbt01` / `k_def_percept01` | same | `k_hen_*` (henchman AI) |
| `MaxHitPoints` | 8 | 8 | 44 |
| `Plot` / `NoPermDeath` | 0 / 0 | 0 / 0 | 0 / **1** |

`FactionID 5` is `Neutral` in `repute.2da` (row labels: 1 `Hostile_1`, 2 `Friendly_1`,
3 `Hostile_2`, 4 `Friendly_2`, **5 `Neutral`**) - **VERIFIED** by parsing
`2da.bif/repute.2da`. A neutral commoner will not attack the player. It is an ordinary
Taris civilian model, has no conversation, no plot flag, and uses only the stock default AI
scripts. That is the smallest possible perturbation of the world.

Other candidates, and why not:

- **`p_carth`, `p_bastilla`, `p_mission`, `p_zaalbar`, `p_jolee`, `p_juhani`, `p_hk47`,
  `p_t3m4`, `p_cand`** all exist in `templates.bif` (**VERIFIED**), so they *can* be spawned.
  But they carry henchman AI scripts, a `Conversation`, `NoPermDeath`, and a tag that other
  scripts and the party system search for. Spawning a second `Carth` is asking for
  duplicate-tag confusion in stock dialogue and quest scripts. **Avoid.**
- **`partymember.utc`** is a leftover NWN badger: `Tag "SWDWEEB"`, `TemplateResRef "nw_badger"`,
  `Appearance_Type 1`, `Race 8`, `nw_c2_omnivore` AI. **VERIFIED.** Not a humanoid; do not
  use it despite the name.
- **`n_repsold`** (Republic soldier, appearance 38, faction 5 Neutral) is an equally safe
  humanoid alternative if you want a more player-like silhouette. **VERIFIED.**
- Module-local `.utc` files (e.g. the `tar02_*` blueprints backing the NPCs already
  enumerated on Taris) work too but only while that module is loaded. Prefer the global
  ones.

The spawned creature's `Tag` (`CommM`) is how you will recognise it in the existing
vtable scan, which already prints tags.

---

## 5. Prerequisites and ordering

1. **A module and an area must be loaded.** `CreateObject` does
   `CServerExoApp::GetModule()` (`0x004AE6B0`, via `APP_MANAGER_PTR 0x007A39FC` + 8) then
   `CSWSModule::GetArea()` (`0x004C30F0`), and jumps straight to the
   `OBJECT_INVALID` exit if either is null. **VERIFIED.** So calling on the main menu, during
   a loading screen, or inside a minigame is *safe but useless*: you get `0x7F000000`.
   The location's coordinates are interpreted in whatever area is currently resident;
   `CSWSModule` holds a **single** area pointer at `+0x220`, so there is no area to choose.
2. **The command implementer must exist.** It is created during engine startup and lives for
   the process; `StackPushEngineStructure` itself null-checks `internal+0x3D4`. Re-resolve
   the chain each time rather than caching it across a save load. **INFERRED** (the chain
   resolved cleanly mid-game; its lifetime was not observed across a load).
3. **Main thread, mandatory.** Every structure the path mutates is unsynchronised: I read the
   full bodies of `CGameObjectArray::AddInternalObject` and
   `CVirtualMachineStack::AddToTopOfStack` and **neither contains a `lock` prefix, an
   interlocked op, or a critical-section call** (**VERIFIED** for those two). The path also
   touches the resource manager, the area's `game_objects` heap array
   (`CSWSArea+0x190`, count at `+0x194`) and the server AI master, all of which the main loop
   walks every frame. Calling from the plugin's worker thread races all of it. **INFERRED**
   (strongly) that off-thread calls will corrupt the heap or the object array.
4. **Not while a script is running.** The VM stack is *the* stack; pushing into it mid-script
   corrupts the running script's frame. Cheap read-only guard, both **VERIFIED** to be zero
   when idle:

   ```
   *(int*)(internal + 0x1AC)  == 0     // CVirtualMachineStack.stack_pointer
   *(int*)(internal + 0x1C4)  == 0     // call_stack_size
   ```

   Refuse to spawn unless both are 0, and re-read `stack_pointer` after the sequence to
   assert you left it balanced (four pushes, one routine that pops four and pushes one, one
   pop = net zero).
5. **No client-side twin to build.** The renderable `CSWCCreature` (size `0x44C`, vtable
   `0x0074F5F8`) is constructed by the *client* message handler
   `CSWCMessage::HandleServerToPlayerCreatureUpdate_...` at `0x0064C3C0`, which the engine
   drives from its own per-frame object-update pipeline (`CSWSMessage::ComputeGameObjectUpdateForObject`,
   `SelectCategoryForGameObject`, ...). **VERIFIED** that `CSWCCreature::Constructor`
   (`0x00616A20`) is called from there and not from `CreateObject`. So the creature becomes
   visible on its own within a frame or two of being added to the area; there is nothing to
   do client-side.
6. **The AI will drive it.** `n_commm` runs `k_def_spawn01` on spawn plus a heartbeat. It
   will stand, look around, and react. To make it a *phantom* you will want
   `ClearAllActions` (routine 9, `0x0052F4A0`) and possibly `SetCommandable(FALSE)`
   (routine 162, `0x005426C0`) - but note that `SetCommandable(FALSE)` also blocks the
   `ActionMoveToLocation` you will need for movement, so this is a movement-design decision,
   not a spawn one. Cross-reference `docs/re/movement.md`. **INFERRED** from the blueprint's
   script fields plus the object-model finding that an AI-driven creature outwrites position
   pokes 64 times a second.
7. **It will end up in your save.** A creature added to the area is part of area state.
   Test on a throwaway save. **INFERRED.**

---

## 6. Concrete C# call plan

`src/KotorCoop.Plugin` is NativeAOT `win-x86` and already uses
`delegate* unmanaged[Stdcall]<...>` function pointers in `Exports.cs`, so this fits the
existing idiom. On x86, `unmanaged[Thiscall]` puts the first argument in `ECX`, pushes the
rest right-to-left, and lets the callee pop - exactly the retail convention.

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
internal struct Vector3 { public float X, Y, Z; }

/// CScriptLocation, 0x18 bytes: position then a NORMALISED orientation vector.
[StructLayout(LayoutKind.Sequential, Size = 0x18)]
internal struct ScriptLocation { public Vector3 Position; public Vector3 Orientation; }

/// CExoString, 0x08 bytes: { char* c_string; uint length }.
[StructLayout(LayoutKind.Sequential, Size = 0x08)]
internal struct ExoString { public nint CString; public uint Length; }

internal static unsafe class Odyssey
{
    // Image base 0x400000, no ASLR: these are absolute and stable for this build.
    const nint VirtualMachinePtr = 0x007A3A00;   // -> CVirtualMachine*
    const nint AppManagerPtr     = 0x007A39FC;

    const int CommandImplementerOffset = 0x3D4;  // CVirtualMachineInternal
    const int CommandsTableOffset      = 0x0C;   // CSWVirtualMachineCommands
    const int StackPointerOffset       = 0x1AC;  // CVirtualMachineStack.stack_pointer
    const int CallStackSizeOffset      = 0x1C4;

    const int RoutineCreateObject = 243;
    const nint ExpectedCreateObject = 0x0052F820;

    const int ObjectTypeCreature = 1;            // nwscript OBJECT_TYPE_CREATURE
    const int EngineStructureLocation = 2;       // nwscript `location`
    const uint ObjectInvalid = 0x7F000000;

    // __thiscall(CVirtualMachine* vm, ...)
    static readonly delegate* unmanaged[Thiscall]<nint, int, int>
        StackPushInteger = (delegate* unmanaged[Thiscall]<nint, int, int>)0x005D1010;
    static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, int>
        StackPushString = (delegate* unmanaged[Thiscall]<nint, ExoString*, int>)0x005D1090;
    static readonly delegate* unmanaged[Thiscall]<nint, int, void*, int>
        StackPushEngineStructure = (delegate* unmanaged[Thiscall]<nint, int, void*, int>)0x005D10B0;
    static readonly delegate* unmanaged[Thiscall]<nint, uint*, int>
        StackPopObject = (delegate* unmanaged[Thiscall]<nint, uint*, int>)0x005D10C0;

    // __thiscall(CSWVirtualMachineCommands* this, int nCommandId, int nParams)
    static readonly delegate* unmanaged[Thiscall]<nint, int, int, int>
        ExecuteCommandCreateObject = (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x0052F820;

    static nint Deref(nint p) => *(nint*)p;

    /// Returns the new object id, or 0x7F000000 on refusal/failure. MAIN THREAD ONLY.
    internal static uint SpawnCreature(string template, Vector3 position, Vector3 facingUnitVector)
    {
        nint vm = Deref(VirtualMachinePtr);                       // CVirtualMachine*
        if (vm == 0) return ObjectInvalid;
        nint internalVm = Deref(vm);                              // CVirtualMachineInternal*
        if (internalVm == 0) return ObjectInvalid;
        nint implementer = Deref(internalVm + CommandImplementerOffset);
        if (implementer == 0) return ObjectInvalid;

        // Guard 1: the routine table must still say what we think it says.
        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0) return ObjectInvalid;
        if (*(nint*)(table + RoutineCreateObject * 4) != ExpectedCreateObject) return ObjectInvalid;

        // Guard 2: never touch the VM stack while a script owns it.
        int sp0 = *(int*)(internalVm + StackPointerOffset);
        if (sp0 != 0) return ObjectInvalid;
        if (*(int*)(internalVm + CallStackSizeOffset) != 0) return ObjectInvalid;

        var loc = new ScriptLocation { Position = position, Orientation = facingUnitVector };

        // ASCII, NUL-terminated, pinned for the duration. StackPushString copies it.
        byte* utf8 = stackalloc byte[24];
        int n = 0;
        foreach (char c in template) { if (n == 23) break; utf8[n++] = (byte)c; }
        utf8[n] = 0;
        var s = new ExoString { CString = (nint)utf8, Length = (uint)n };

        // Push in REVERSE declaration order.
        if (StackPushInteger(vm, 0) == 0) return ObjectInvalid;                        // bUseAppearAnimation
        if (StackPushEngineStructure(vm, EngineStructureLocation, &loc) == 0) return ObjectInvalid;
        if (StackPushString(vm, &s) == 0) return ObjectInvalid;
        if (StackPushInteger(vm, ObjectTypeCreature) == 0) return ObjectInvalid;

        int status = ExecuteCommandCreateObject(implementer, RoutineCreateObject, 4);
        if (status != 0) return ObjectInvalid;                                          // -2000/-2001

        uint oid = ObjectInvalid;
        if (StackPopObject(vm, &oid) == 0) return ObjectInvalid;

        // Guard 3: the sequence must have been stack-neutral.
        if (*(int*)(internalVm + StackPointerOffset) != sp0) { /* log loudly */ }
        return oid;
    }
}
```

Marshalling notes:

- `ScriptLocation` and `ExoString` are passed **by pointer**, and both are copied by the
  engine, so `stackalloc`/locals are correct and there is nothing to free. **VERIFIED**
  (section 3.3).
- `template` must be plain ASCII, <= 16 significant characters (`CResRef` is 16 bytes).
  `n_commm` is 7.
- Do **not** use `StackPushVector` here; the 12-byte by-value struct argument is an extra
  ABI risk for no benefit, since the location engine structure already carries the vector.
- `facingUnitVector` must be normalised and non-zero. `(1, 0, 0)` is fine for a first test.
- Keep every one of the three guards. The table check in particular turns "the layout
  changed under us" from a crash into a refusal.

Getting a valid position: read the local player's `CSWSObject.position` at `+0x90` with the
existing creature-vtable scan (`0x007470C8`) and offset it a couple of metres. That
guarantees a spot inside the walkmesh, and `ComputeSafeLocation`'s 20-unit search then
handles the rest.

Where to call it from: the plugin's worker thread must only *request* a spawn (set a flag in
`SharedBlock`); the actual call has to happen on the game's main thread. **That question is
answered in `docs/re/frame-hook.md`**, which establishes the hook point as
`GDI32!SwapBuffers` via the single IAT dword at `0x0073D00C` - main thread by construction,
once per frame, and positioned at the end of the frame immediately after
`CServerExoApp::MainLoop`, which is the best moment in the frame to add an object to the
world. Do not use the DirectInput device as the pump: that document tested it against the
live process and refuted it (the game stops polling DirectInput entirely when its window is
not in the foreground, while the frame loop keeps running).

---

## 7. Risk assessment

Ordered by likelihood of actually biting.

| Risk | Severity | Why | Mitigation |
| --- | --- | --- | --- |
| **Calling from the wrong thread** | crash, possibly delayed and unattributable | unsynchronised object array, area array and VM stack (section 5.3) | use the `SwapBuffers` frame hook (`docs/re/frame-hook.md`), log the main-thread id, and hard-refuse any call from another thread |
| **Calling while a script runs** | corrupts the running script, arbitrary game misbehaviour | the VM stack is shared | the `stack_pointer == 0 && call_stack_size == 0` guard |
| **`unmanaged[Thiscall]` ABI mistake** | immediate crash | callee-pops; a wrong count desynchronises `ESP` | verify against `param_size_bytes` in the database for every single function pointer; all values used above are listed in section 3.3 |
| **Zero or non-normalised orientation** | possible divide-by-zero deep in `normalize_2` | `CreateObject` feeds it to `SetOrientation` unmodified | always pass a unit vector |
| **Bad or missing ResRef** | none | `LoadFromTemplate` fails, the engine deletes the object and pushes `OBJECT_INVALID` (**VERIFIED** cleanup path at `0x0052FA09`) | check the returned id |
| **No area loaded** | none | early exit to the `OBJECT_INVALID` path (**VERIFIED**) | check the returned id |
| **Position off the walkmesh** | creature stuck in geometry, no crash | `ComputeSafeLocation` searches only 20 units | derive the position from the live player's position |
| **AI takes over the body** | phantom drifts / reacts / talks | `k_def_spawn01` + heartbeat | movement work; `ClearAllActions` first |
| **Extra creature persisted into a save** | save-file pollution | it is genuine area state | use a throwaway save |
| **Module transition** | the creature and all cached pointers die | area transitions destroy every object (already recorded in `object-model.md`) | re-spawn after a transition; never cache the object pointer, only the id, and re-resolve through `CGameObjectArray::GetGameObject` (`0x004D8230`) |

### A safe first experiment

1. **Read only, no calls.** With the game sitting in a loaded area, resolve the chain and
   assert `table[243] == 0x0052F820`, `stack_pointer == 0`, `call_stack_size == 0`. This is
   exactly what `vmtab.py` already does; it is pure `ReadProcessMemory` and cannot hurt the
   game. **(Already done - see section 1.)**
2. **Get onto the main thread.** Install the `SwapBuffers` IAT hook from
   `docs/re/frame-hook.md` and log the calling thread id per frame, confirming it equals the
   window-owning thread. Still zero *engine* calls.
3. **One spawn, nothing else.** From that hook, once, on an explicit request flag:
   `SpawnCreature("n_commm", playerPos + (2, 0, 0), (1, 0, 0))`. Log the status and the
   returned id. **Do not move it, do not write to it, do not queue actions.**
4. **Verify externally.** Re-run the existing vtable scan (`0x007470C8`) from the agent and
   confirm: creature count went from 16 to 17; the new record's `object_id` matches what
   `StackPopObject` returned; its `tag` (`+0x18`) reads `CommM`; its position is where you
   asked. Then look at the screen: a civilian should be standing there.
5. **Clean up.** `DestroyObject` is routine **241** at `0x0052FF20`; its script signature is
   `void DestroyObject(object oDestroy, float fDelay=0.0, int bNoFade=FALSE,
   float fDelayUntilFade=0.0, int nHideFeedback=0)`, so push in reverse
   (`nHideFeedback, fDelayUntilFade, bNoFade, fDelay, oDestroy`) and call with
   `nCommandId = 241, nParams = 5`. **INFERRED** for the argument order (the reverse-order
   rule is verified for `CreateObject`; `ExecuteCommandDestroyObject`'s own pop sequence was
   not disassembled). Confirm before use.

Only after step 4 renders a body on screen is it worth touching movement.

---

## 8. Second angle: direct construction

If the VM route is ever unusable (for example you need to spawn while a script *is*
running), the engine's own creature-creation sequence can be replayed directly. It touches
no VM state. Every address and argument below is **VERIFIED** from the disassembly of
`0x0052F9A3..0x0052FB47`; what is **INFERRED** is that the sequence is *sufficient* in
isolation, since it has only ever been observed as part of `CreateObject`.

```
pArea    = CSWSModule::GetArea( CServerExoApp::GetModule( *(APP_MANAGER_PTR)+8 ) )
                                  0x004AE6B0                       0x004C30F0
pCre     = operator_new(0xAC8)                                      0x006FA7E6  __cdecl(size)
           CSWSCreature::Constructor(pCre, 0x7F000000, 0)           0x004F7A10  __thiscall, 8
                 // allocates the object id and inserts into CGameObjectArray internally
CResRef  rr; CResRef::Constructor_4(&rr, "n_commm")                 0x00406D80  __thiscall, 4
           CSWSCreature::LoadFromTemplate(pCre, &rr, 0)             0x005026D0  __thiscall, 8
                 // zero return => failed; delete pCre and stop
           CSWSObject::SetOrientation(pCre, &orientVec)             0x004CD170  __thiscall, 4
           CSWSArea::ComputeSafeLocation(pArea, &pos, 20.0f,
                        *(pCre+0x340), 1, &safePos)                 0x004BE860  __thiscall, 20
           CSWSCreature::AddToArea(pCre, pArea, x, y, z, 1, 1)      0x004FA100  __thiscall, 24
newId    = *(uint*)(pCre + 0x04)
```

Skipping `ComputeSafeLocation`/`ComputeHeight` is legal - they only refine the coordinates.
Skipping `AddToArea` is **not**: without it the creature exists in the object array but has
no area, is not in `CSWSArea.game_objects`, gets no AI level, and will never appear in a
client object update, so nothing renders. This is the direct analogue of what reone's
`CreateObject` does (blueprint -> object -> register with area and object table).

Prefer the VM route anyway: it is 5 calls of ours instead of 7, all the argument shapes are
simple integers and pointers, and it is the code path BioWare's own scripts take a thousand
times per playthrough.

---

## 9. Reproducing this

All of it is offline and read-only. Scratchpad tooling used:

```
q.py      sqlite queries against k1.db (classes / offsets / functions / globals)
ana.py    capstone disassembly of a flat image dump at base 0x400000
show.py   annotated disassembly + caller/xref lookup
mytab.py  static extraction of the 772-entry table from InitializeCommands 0x0054C960
xval.py   cross-validation: table index vs. Ghidra name vs. nwscript.nss routine number
vmtab.py  READ-ONLY live resolution of the chain and diff against the static table
kres.py   enumerate .utc ResRefs in chitin.key / RIM / ERF
gff.py    pull a resource out of KEY/BIF and dump its GFF fields (blueprint inspection)
```

Load-bearing constants, all **VERIFIED** on this build:

| | |
| --- | --- |
| `VIRTUAL_MACHINE_PTR` | `0x007A3A00` |
| `APP_MANAGER_PTR` | `0x007A39FC` (`CServerExoApp` at `+0x08`) |
| `CVirtualMachineInternal.command_implementer` | `+0x3D4` |
| `CSWVirtualMachineCommands.commands` | `+0x0C` (772 dwords) |
| `CSWVirtualMachineCommands` vtable | `0x007450E4` |
| `CVirtualMachineStack.stack_pointer` | `internal + 0x1AC` |
| `CVirtualMachineInternal.call_stack_size` | `internal + 0x1C4` |
| `CSWVirtualMachineCommands::ExecuteCommand` | `0x0052C0D0` |
| `CSWVirtualMachineCommands::InitializeCommands` | `0x0054C960` |
| `CSWVirtualMachineCommands::InitializeSWMGCommands` | `0x005CD2C0` |
| `OBJECT_INVALID` (global) | `0x00746644` -> `0x7F000000` |
| internal `GAME_OBJECT_TYPE` for creature | `byte[0x00746634]` = `5` |
| `sizeof(CSWSCreature)` | `0xAC8` |
| `sizeof(CScriptLocation)` | `0x18` |
| degrees->radians constant | `double[0x00747E00]` = `0.017453292519943295` |

Known unresolved:

- The orientation convention gap between `ExecuteCommandLocation`
  (`normalize(cos d, sin d, 0)`, degrees) and the GIT-file convention recorded in
  `object-model.md` (`-atan2(X, Y)`, 0 = +Y). Irrelevant to spawning, relevant to facing.
- `ExecuteCommandDestroyObject`'s pop order (assumed reverse-declaration by analogy).
- Whether the command implementer pointer survives a save load (never observed across one).
- Whether spawning from the very end of the frame (the `SwapBuffers` hook point) is as safe
  as spawning from inside the server tick. It should be strictly safer, since the server
  tick has already finished, but that is **INFERRED**, not measured.
