# How a creature actually renders, and how server and client objects stay in sync

Scope: what a newly spawned creature needs in order to appear on screen, and the exact
mechanism that keeps the `CSWS*` server object and its `CSWC*` client twin in agreement.

Sources, and what the labels mean:

- **VERIFIED** - read out of `swkotor.exe` v1.0.3 itself: either disassembled from the
  binary (image base `0x400000`, no ASLR, sha256 matches the installed exe) or read
  out of the **live process** with `ReadProcessMemory` while a save was loaded
  (Taris Upper City South, 16 creatures). Nothing in this document was produced by
  writing game memory or calling a game function.
- **INFERRED** - a reasonable reading of verified facts, not directly observed.

Function/offset names come from LaneDibello's public Ghidra database (see
[`object-model.md`](object-model.md)); every address quoted below was re-checked
against the binary.

---

## 1. The headline answer

**The client twin is created for you. You never construct a `CSWCCreature` yourself.**

KOTOR runs its inherited NWN client/server messaging layer in-process, against itself.
Once a server creature is (a) in the area the player is in and (b) passes a visibility
test, the *next frame* the engine writes itself an "Add" message and its own client-side
handler builds the `CSWCCreature`, links it, registers it, loads its model, and attaches
it to the render scene. Appearance arrives one message later, through the same channel.

So the job of a co-op phantom is entirely **server-side**: construct a `CSWSCreature`,
give it an appearance, and put it in the area. Rendering follows.

Live proof (VERIFIED, read-only sweep of the running game):

```
16 CSWSCreature objects found by vtable scan (0x007470C8)

  every one has client_object (+0x224) != 0
  every twin has vtable 0x0074F5F8 (CSWCCreature)
  every twin has server_object (+0x0F8) pointing back at its server object
  every twin has anim_base (+0x068) != 0
  every anim_base has a Gob/model at +0x0B8 != 0
  all 16 twins share the same CSWCArea 0x1DA4DD20 (scene at +0x184 non-null)
```

There is not one server creature in the world without a fully built, model-loaded,
scene-attached client twin. That is what "automatic" looks like.

---

## 2. Object identity: the two id spaces

VERIFIED (`CClientExoAppInternal::ServerToClientObjectId` at `0x005EDA50` tail-jumping to
`0x005F2AC0`, and `CServerExoAppInternal::ClientToServerObjectId` at `0x004B1F70`):

```c
uint32 ServerToClientObjectId(uint32 id) { return id == 0x7F000000 ? id : (id |  0x80000000); }
uint32 ClientToServerObjectId(uint32 id) { return id == 0x7F000000 ? id : (id & ~0x80000000); }
```

That is the whole mapping. `0x7F000000` is `OBJECT_INVALID` and maps to itself.

Live confirmation (VERIFIED): the client area's own object id is `0x80000001`; Carth's
server id is `0x000000C3` and the client area's member list contains `0x800000C3`.

There are therefore **two `CGameObjectArray` instances**:

| Array | Reached via | Populated by |
| --- | --- | --- |
| server | `CServerExoApp::GetObjectArray` `0x004AED70` | `CGameObjectArray::AddInternalObject` `0x004D80E0` (allocates a fresh id) or `AddObjectAtPos` `0x004D7E90` (uses a given id) |
| client | `CClientExoApp::GetGameObjectArray` `0x005ED570` | `CGameObjectArray::AddExternalObject` `0x004D7FF0` (id supplied by the server) |

`APP_MANAGER_PTR` is `0x007A39FC`; `[APP_MANAGER + 4]` is the `CClientExoApp`,
`[APP_MANAGER + 8]` is the `CServerExoApp` (VERIFIED, used constantly in the
disassembly; live values `0x010DC508` / `0x1BFAD050`).

### GAME_OBJECT_TYPES resolved

`object-model.md` lists the numeric enum as unknown. It is recoverable from the
`Get*ByGameObjectID` type checks against pooled byte constants (VERIFIED):

| Value | Type | Value | Type |
| --- | --- | --- | --- |
| 4 | Area | 10 | Door |
| **5** | **Creature** | 11 | AreaOfEffect |
| 6 | Item | 12 | Waypoint |
| 7 | Trigger | 13 | Encounter |
| 8 | Projectile | 14 | Store |
| 9 | Placeable | 16 | SoundObject |

`CGameObject.object_type` is the byte at `+0x08`. `CGameObjectArray::GetGameObject`
(`0x004D8230`) returns **0 on success** (it compares against the pooled `0` at
`0x0074666C`). Both matter: `CClientExoApp::GetCreatureByGameObjectID` refuses to hand
back a twin whose `+0x08` is not 5.

---

## 3. The link, and how it repairs itself

| Direction | Field | Getter | Setter | Clearer |
| --- | --- | --- | --- | --- |
| server -> client | `CSWSObject + 0x224` | `CSWSObject::GetClientObject` `0x004CC2B0` | `AttachmentFromClientObject` `0x004CC310` | `DetachFromClientObject` `0x004CC320` |
| client -> server | `CSWCObject + 0x0F8` | `CSWCObject::GetServerObject` `0x0063D4B0` | `AttachmentFromServerObject` `0x0063D520` | `DetachFromServerObject` `0x0063D530` |

The setters are one-instruction stores. The **getters are lazily self-healing**
(VERIFIED, full disassembly of `0x004CC2B0`):

```c
CSWCObject* CSWSObject::GetClientObject() {
    if (this->client_object) return this->client_object;                    // +0x224
    uint32 cid = ServerToClientObjectId(this->id);                          // 0x005EDA50
    CGameObject* g = CClientExoApp::GetGameObject(clientApp, cid);          // 0x005ED580
    if (g) {
        CSWCObject* c = g->vtbl[0x0C]();                                    // AsSWCObject
        this->client_object = c;
        if (c) c->AttachmentFromServerObject(this);                         // 0x0063D520
    }
    return this->client_object;
}
```

`CSWCObject::GetServerObject` is the exact mirror (`ClientToServerObjectId` ->
`CServerExoApp::GetGameObject` `0x004AE750` -> `vtbl[0x10]` -> store -> back-link).

Consequence for the plugin: **you never have to set `+0x224` by hand.** Register the
client object in the client array under `serverId | 0x80000000` (the engine's Add handler
does this) and the first `GetClientObject()` call wires both directions.

Areas have the same pair: `CSWCArea + 0x27C` = `attached_server_area`
(`AttachmentFromServerArea` `0x00605690`), `CSWSArea::GetClientArea` `0x00506A10` /
`AttachmentFromClientArea` `0x00506A60`.

---

## 4. What "rendering" actually is (and what the render list is not)

### The chain that puts pixels on screen

VERIFIED:

```
CSWCCreature (+0x068 anim_base)
    -> CSWCAnimBase (or ...TW / ...Head / ...Wield / ...HeadWield)
         +0x0B8  Gob*                        <- the Aurora model instance
    CSWCObject::AttachToScene(Scene*)   0x0063D820
         -> animBase->vtbl[+0x04]  = CSWCAnimBase::AttachToScene  0x0069CD80
              -> gob->vtbl[+0x4C]  = Gob::AttachToScene           0x0044F7C0
                   -> Gob::SetScene                               0x0043F200
```

The `Scene*` comes from `CSWCArea + 0x184` (`main_scene`). A creature renders because its
`Gob` is in that `Scene`. Nothing else.

`CSWCAnimBase::LoadModel` (`0x0069E1F0`, vtable slot `+0x0C`) is where the `Gob` is born:
it converts the `CResRef` to a string and calls `Global::NewCAurObject` (`0x00449CC0`),
storing the result at `animBase + 0x0B8`. If that pointer is null the creature is
logically present and completely invisible - which is precisely the failure mode this
investigation existed to rule out.

Cheap runtime health check: `CSWCCreature::HasGobModel` (`0x0063F5E0`) is literally
`animBase && animBase->vtbl[+0x08](0xFF) != 0`.

### `CSWCArea + 0x11C` (`render_list`) is a red herring for creatures

It is a `CExoArrayList<uint32>` of **client** object ids laid out as
`{ptr @+0x11C, count @+0x120, capacity @+0x124}`.

The only code that adds or removes creature-sized objects to it is
`CSWCTrigger::AddToRenderList` (`0x00691CC0`) / `RemoveFromRenderList` (`0x00691D80`),
plus `CSWCArea::AddPlaceableObjectStatic`, `Constructor` and `UnloadArea`. An exhaustive
scan of every instruction in `.text` with displacement `0x11C`/`0x120`/`0x124` finds no
creature path touching it (VERIFIED).

Live confirmation (VERIFIED):

```
CSWCArea 0x1DA4DD20
  render_list (+0x11C): ptr 0x1FEEAF20  n=6  cap=16
      ids: 0x8000002B 0x8000002E 0x8000002C 0x8000002A 0x8000002D 0x8000002F
  objects    (+0x20C): ptr 0x1DC00330  n=56
      ids: 0x8000005C 0x800000C3 0x80000009 0x80000012 ... (all 16 creatures present)
  main_scene (+0x184): 0x1E14ACC0
```

Six entries in the render list; none of them are the sixteen creatures. **A spawned
creature does not need a render-list entry.** (The six are trigger/static geometry;
INFERRED from who writes the list.)

The list a creature *does* join is `CSWCArea + 0x20C`, another `CExoArrayList<uint32>`,
maintained by `CSWCObject::SetArea` (`0x0063F570`, VERIFIED):

```c
void CSWCObject::SetArea(CSWCArea* newArea) {
    if (this->area /*+0x20*/ != newArea) {
        if (this->area)  CExoArrayList<int>::remove(&this->area->list_0x20C, this->id);   // 0x0066DCC0
        if (newArea) {
            CExoArrayList<uint>::AddUnique(&newArea->list_0x20C, this->id);               // 0x006449D0
            this->SetObjectVisibilityOnSetArea();                                          // 0x0063E350
        }
    }
    this->area = newArea;                        // CSWCObject + 0x20
}
```

`CSWCCreature::SetArea` (`0x00614B20`) wraps it and additionally caches the area's object
id into `creature + 0x39C`.

---

## 5. Appearance: which field picks the model

### Server side is the single source of truth

`CSWSCreature + 0xA50` is a `CSWSCreatureAppearanceInfo` (0x24 bytes). Verified live
layout, including three fields the database does not name:

| Offset (abs) | Field | Live values observed |
| --- | --- | --- |
| `+0xA50 .. +0xA5F` | four object ids (INFERRED: equipped-appearance item ids) | `0x7F000000` when empty; Carth has `0xC6 0xC5 0xC4` |
| **`+0xA60`** | **`appearance_type`, u16 - the appearance.2da ROW** | PC **174**, carth 6, dia022 14, twilek 270, droid1 28, bith 374 |
| `+0xA62` | `phenotype`, u8 | 0 on all 16 |
| `+0xA63` | `gender`, u8 | 0, 2, or 4 |
| `+0xA64..67` | skin / hair / tattoo1 / tattoo2 colour, u8 | 0 on all 16 |
| `+0xA68` | `appearance_head`, u8 | **1 on all 16** |
| `+0xA69` / `+0xA6A` | tail / wings, u8 | 0 |
| `+0xA6C` | `use_backup_head`, int | 0 |
| `+0xA70` | `duplicating_head`, u8 | `0xFF` on all 16 |

**Correction to `object-model.md`:** it lists `CSWSCreature.appearance` at `+0xA4C` as the
appearance field. `+0xA4C` reads **0 for all sixteen live creatures including the PC**
(VERIFIED). The field that drives rendering is `+0xA60`
(`appearance_info.appearance_type`). `+0xA4C` is vestigial.

Because `appearance_head` is 1 for every creature including the PC, it is **not** what
selects a head in KOTOR 1 (INFERRED): the head is baked into the appearance.2da row
itself. That is convenient - see 5.4.

### The message that carries it

`CSWSMessage::WriteGameObjUpdate_UpdateAppearance` (`0x00572070`) reads the server fields
directly and bit-gates each one (VERIFIED): `+0xA60` u16, `+0xA62` u8, `+0xA63` u8,
`+0xA68` u8, `+0xA69` u8, `+0xA6A` u8, `+0xA6C` bool, `+0xA70` u8, plus names out of
`creature_stats` at `+0xA74`. Whether to send at all is decided by
`CSWSMessage::ComputeAppearanceUpdateRequired` (`0x00567100`) against a per-player
snapshot, so **changing `+0xA60` at runtime is enough to make the engine push a new
appearance next frame** (INFERRED, but this is the only mechanism the appearance path
has).

### Client side: `CCAppearanceInfo` -> appearance.2da -> model

`CSWCMessage::HandleServerToPlayerCreatureUpdate_Appearance` (`0x00655E40`) does
(VERIFIED):

1. `memcpy(local, creature->appearance /*+0x21C*/, 0x3C)` - grab the current
   `CCAppearanceInfo` out of the `CSWCCreatureAppearance` object.
2. patch the changed fields; notably `*(int16*)(local + 0x18) = newAppearanceType`.
3. `CSWCCreature::SetAppearance(local, 0xEF, flag)`.

`CCAppearanceInfo` is 0x3C bytes; `+0x18` is `appearance_id` (int16), `+0x1B` gender,
`+0x24` personal space, `+0x28` height, `+0x2C` alignment.

```c
// 0x006134C0, ret 0xC
int CSWCCreature::SetAppearance(CCAppearanceInfo* info, int partMask, int reattachVfx);
//   partMask bit 0 = body, 1 = head, 2 = tail, 3 = wing
//   -> CSWCCreatureAppearance::CreateBodyAppearance  0x00698B10
//   -> CSWCCreatureAppearance::CreateHeadAppearance  0x00697A20
//   -> CreateTailAppearance / CreateWingAppearance
//   then SetAppearanceAlignment, ForceAnimate, UpdateShadowBlob, LoadRollingFootstepSound

// 0x0060C910, ret 8  - deferred variant, stages into creature + 0x31C
int CSWCCreature::RequestAppearance(CCAppearanceInfo* info, uint8 partMask);
```

`CreateBodyAppearance(CCAppearanceInfo* info)` (VERIFIED) uses
`*(uint16*)(info + 0x18)` as the **row index** into
`appearance.2da` = `(*(void**)0x007A3A28 /*RULES_PTR*/)->[0xB8] /*CTwoDimArrays*/ ->[0x0C]`,
and reads:

- speed/anim-rate floats (cached column indices `0x2A`, `0x2B`, `5`, `6` -> stored at
  `CSWCCreatureAppearance + 0x58/0x5C/0x60/0x64`), scaled by
  `CSWSCreature::GetMovementRateFactor`;
- `"MODELTYPE"` (cached column index `8`) -> `CSWCCreatureAppearance::GetAnimType`
  (`0x00696D40`), which maps the letter to a code:

  | modeltype contains | animType | dispatch |
  | --- | --- | --- |
  | `S` | 11 | `CreateSimple` `0x00698550` |
  | `B` | 3 | `CreateBTypeBody` `0x00697CE0` |
  | `F` or `L` | 2 | `CreateSimple` `0x00698550` |
  | none of those | 11 | `CreateSimple` |

`CreateSimple(animType, CCAppearanceInfo* info)` then reads appearance.2da columns
`"RACE"` (the body model ResRef) and `"ENVMAP"` (VERIFIED - the column-name strings are at
`0x0074B2BC` and `0x0074B68C`), probes them with `CExoResMan::Exists`, and finally calls
the creature's own model loader through the vtable:

```c
creature->vtbl[0x78](&modelResRef, 0xFF, animType);   // = CSWCCreature::LoadModel 0x0061B380
```

`CSWCCreature::LoadModel(CResRef*, int part, int animType)` (VERIFIED) frees any existing
`anim_base`, then `operator new`s the right subclass off a jump table on `animType`:

| size | class | ctor |
| --- | --- | --- |
| `0x0F0` | `CSWCAnimBase` | `0x0069DFB0` |
| `0x174` | `CSWCAnimBaseTW` | `0x0069CBD0` |
| `0x1C4` | `CSWCAnimBaseHead` | `0x0069BB80` |
| `0x1D0` | `CSWCAnimBaseWield` | `0x00699DD0` |
| `0x220` | `CSWCAnimBaseHeadWield` | `0x00698EC0` |

stores it at `CSWCObject + 0x68`, seeds `CSWAnimBase::Set` speed constants, `sprintf`s the
model name, and calls `CSWCCreature::RegisterCallbacks` (`0x0061AB40`) so footstep/hit/swing
events fire. On success `CreateSimple` copies the 0x3C-byte `CCAppearanceInfo` into
`CSWCCreatureAppearance + 0x00` (`rep movsd` of 0xF dwords at `0x00698819`) - that is why
the live appearance object's `+0x18` matches the server's `+0xA60` exactly on all 16
creatures.

Live cross-check (VERIFIED): server `+0xA60` == `CSWCCreatureAppearance + 0x18` for every
creature. `CSWCCreature + 0x31C` (the `RequestAppearance` staging buffer) is **zero** on
essentially everything, so do not read it expecting the live appearance.

### 5.4 Making a phantom look like a player character

The PC's appearance is nothing special: it is an ordinary `appearance_type` row.
Live (VERIFIED): PC (`is_pc = 1`, id `0x7FFFFFFF`) has `appearance_type = 174`, gender 0,
phenotype 0, `appearance_head = 1`, everything else default.

So "make this creature look like the PC" is:

```c
CSWSCreature* pc = CServerExoApp::GetPlayerCreature(serverApp);     // 0x004B2820
memcpy((char*)phantom + 0xA50, (char*)pc + 0xA50, 0x24);            // whole appearance block
```

and the appearance-update message pipeline pushes it to the twin on the next frame,
which rebuilds the model. `CSWCCreature::ForceAppearanceUpdate` (`0x00614A30`) exists as a
client-side "rebuild now" if the push proves lazy.

Copy the whole 0x24 block rather than just `+0xA60`: for player rows the *body texture*
is armour-derived and the four object ids at `+0xA50..+0xA5F` participate. For a
cosmetic phantom, `appearance_type` alone is very likely sufficient (INFERRED), and using
a *different* player row than the local PC's is desirable anyway so the two avatars are
distinguishable.

---

## 6. The reference implementation

There are two, and they are the same shape.

### 6.1 Module load: `CSWSArea::LoadCreatures` (`0x00504A70`)

VERIFIED, per GIT `Creature List` entry:

```c
p = operator new(0xAC8);                                     // sizeof CSWSCreature
CSWSCreature::Constructor(p, id, 0);                         // 0x004F7A10
if (fromTemplate) {
    CResRef tr = ReadFieldCResRef(gff, "TemplateResRef");
    CSWSCreature::LoadFromTemplate(p, &tr, 0);               // 0x005026D0  (reads the .utc)
} else {
    CSWSCreature::LoadCreature(p, gff, ...);                 // 0x00500350  (savegame path)
}
x = ReadFieldFLOAT("XPosition"); y = ...; z = ...;
CSWSArea::ComputeSafeLocation(area, ...);                    // 0x004BE860
CSWSCreature::AddToArea(p, area, x, y, z, 1, 1);             // 0x004FA100
CSWSObject::SetOrientation(p, &orientation);                 // 0x004CD170
```

### 6.2 Runtime spawn: NWScript `CreateObject`
### (`CSWVirtualMachineCommands::ExecuteCommandCreateObject` `0x0052F820`, creature branch)

VERIFIED, and this is the one to copy because it spawns into a *live* module:

```c
CSWSArea* area = CSWSModule::GetArea(CServerExoApp::GetModule(serverApp));   // 0x004AE6B0 / 0x004C30F0

CSWSCreature* c = (CSWSCreature*)operator_new(0xAC8);
CSWSCreature::Constructor(c, 0x7F000000, 0);          // OBJECT_INVALID id -> array allocates a real one
if (CSWSCreature::LoadFromTemplate(c, &utcResRef, 0) != 0) { /* failed: destruct, push OBJECT_INVALID */ }

CSWSObject::SetOrientation(c, &loc.orientation);       // 0x004CD170,  ret 4
CSWSArea::ComputeSafeLocation(area, &loc.pos, 20.0f, c->[0x340], 1, &safePos);  // 0x004BE860, ret 0x14
z = /* walkmesh height via area->[0x11C] vtable + CSWSArea::ComputeHeight 0x004BC380 */;
/* optional spawn VFX 81 via CGameEffect + CSWSObject::ApplyEffect */
CSWSCreature::AddToArea(c, area, x, y, z, 1, 1);       // 0x004FA100,  ret 0x18
objectId = *(uint32*)((char*)c + 4);
```

The id is assigned inside `CSWSObject::Constructor` (`0x004CFCB0`), VERIFIED at
`0x004CFEF3`:

```c
if (passedId == 0x7F000000) CGameObjectArray::AddInternalObject(&this->id, this, arg);  // 0x004D80E0
else                        CGameObjectArray::AddObjectAtPos(passedId, this);           // 0x004D7E90
```

`CSWSCreature::AddToArea(area, x, y, z, a5, a6)` (VERIFIED, fresh-object branch at
`0x004FA2B3`):

```c
CSWSObject::SetPosition(this, &pos, 1, 1, 0);          // 0x004CD1A0
CSWSArea::AddObjectToArea(area, this->id, (a6 == 0));  // 0x0050DFD0
CSWSObject::SetArea(this, area);                       // 0x004CB160
CServerAIMaster::SetAILevel(aiMaster, this, ...);      // 0x004B08A0
CSWSCreature::UpdateSubareasOnJumpPosition(...);       // 0x0051B940  (rooms/triggers)
```

Note what `AddToArea` does **not** do: it never creates the client twin and never sends an
Add message. It only makes the object a member of the area. Everything visual is the
consequence of that membership, one frame later.

---

## 7. The automatic path, step by step

VERIFIED end to end.

### 7.1 Server side, once per frame per player

```
CServerExoAppInternal::UpdateClientGameObjects          0x004B6950
  -> UpdateClientGameObjectsForPlayer                   0x004B3EC0
     -> CSWSMessage::SendServerToPlayerGameObjUpdate    0x00578200
        (walks the current area's objects through CServerExoApp::GetObjectArray)
        -> CSWSMessage::ComputeGameObjectUpdateForObject 0x00578060
```

`ComputeGameObjectUpdateForObject` (full disassembly, VERIFIED):

```c
CGameObject* g; if (GetGameObject(array, objId, &g) != 0 || !g) return;
CSWSObject* srv = g->vtbl[0x10]();                            // AsSWSObject
int vis = CSWSMessage::TestObjectVisible(this, srv, player);   // 0x0056DC60
srv->[0x1F8] = vis;                                            // <-- "visible to player" flag
if (vis != 1) { DeleteLastUpdateObjectsForObject(...); return; }   // twin gets torn down
TestObjectUpdateDifferences(...);                              // 0x00577FA0, vs per-player snapshot
if (changed) WriteGameObjUpdate_UpdateObject(...);             // 0x00574A10
StoreValuesInLastUpdateObject(...);                            // 0x00576FC0
```

The first time an object is visible to a player there is no `LastUpdateObject` for the
pair, so `CSWSMessage::CreateNewLastUpdateObject` (`0x00577000`) runs, and *it* writes the
Add record (VERIFIED at `0x005771E9`):

```
WriteBYTE('A')                              // 0x41  = Add
WriteBYTE(objectType)                       // 5     = creature
WriteOBJECTIDServer(srv->id)
WriteBYTE(srv->[0xA88] != 0)                // in-party flag (INFERRED name)
WriteFLOAT x6  <- srv->position  +0x90/+0x94/+0x98
               <- srv->orientation +0x9C/+0xA0/+0xA4
WriteWORD(srv->animation +0xD4)
```

Steady-state updates write `'U'` (`0x55`) instead, from
`WriteGameObjUpdate_UpdateObject`; appearance writes `'P'` (`0x50`).

### 7.2 Client side dispatch

`CSWCMessage::HandleServerToPlayerGameObjectUpdate` (`0x0066A4A0`) reads a category byte,
then a letter opcode, with the letter table decoded as (VERIFIED):

| letter | handler |
| --- | --- |
| `A` `0x41` | `HandleServerToPlayerUpdate_Add` `0x00656BD0` |
| `U` `0x55` | `HandleServerToPlayerUpdate_Update` `0x0066A2A0` |
| `D` `0x44` | `HandleServerToPlayerUpdate_Delete` `0x00656C90` |
| `P` `0x50` | `HandleServerToPlayerUpdate_Appearance` `0x00656C60` |
| `I` `0x49` | `HandleServerToPlayerUpdate_PlayerInfo` `0x00668BB0` |
| `G` `0x47` | `..._GuiElements` | 
| `W` `0x57` | `..._WorkRemaining` |

`Update_Add` reads the object-type byte and tail-jumps on it (VERIFIED): `5` ->
`CSWCMessage::HandleServerToPlayerCreatureUpdate_Add` (`0x0064C0A0`), `6` -> item,
`7` -> trigger, `9` -> placeable, `10` -> door, `11` -> AoE. `Update_Appearance` similarly
routes type `5` to `HandleServerToPlayerCreatureUpdate_Appearance` (`0x00655E40`).

### 7.3 `HandleServerToPlayerCreatureUpdate_Add` - the canonical construction sequence

Full disassembly, VERIFIED. This is exactly what would have to be replicated if the
automatic path ever failed:

```c
CSWCCreature* pc = CClientExoApp::GetPlayerCreature(clientApp);        // 0x005ED540
uint32 cid   = ReadOBJECTIDClient();                                   // 0x00692630
uint8  party = ReadBYTE(8);
float  px,py,pz, ox,oy,oz = ReadFLOAT x6;
uint16 anim  = ReadWORD(16);
if (MessageReadOverflow(1)) return 0;

CSWCCreature* c = CClientExoApp::GetCreatureByGameObjectID(clientApp, cid);  // 0x005ED5B0
if (c) { /* already exists -> fade fix-ups only, skip creation */ }
else {
    CSWCArea* area = CClientExoApp::GetModule(clientApp)->area;        // 0x005ED530, module + 0x48
    c = (CSWCCreature*)operator_new(0x44C);
    CSWCCreature::Constructor(c);                                      // 0x00616A20  (sets vtable 0x0074F5F8)
    c->vtbl[0x04](cid);                    // CSWCObject::SetId        0x00616FA0
    c->vtbl[0x84](area);                   // CSWCCreature::SetArea    0x00614B20  -> joins area + 0x20C
    CSWCCreature::SetInParty(c, party==1);                             // 0x0060EF50
    c->vtbl[0x8C](&pos);                   // SetPosition              0x0060B9B0  -> CSWCObject + 0x24
    c->vtbl[0x88](&ori);                   // CSWCObject::SetOrientation 0x0063C110
    CSWCCreature::SetMovingOrientation(c, &ori);                       // 0x0060B380
    CSWCCreature::setStationaryOrientation(c, &ori);                   // 0x0060B3C0
    *(Vector*)((char*)c + 0x1DC) = pos;                                // desired-position cache

    CGameObjectArray::AddExternalObject(                               // 0x004D7FF0
        CClientExoApp::GetGameObjectArray(clientApp), &outId, c, 1);   // 0x005ED570
    CClientAIList::AddObject(                                          // 0x00604A20
        CClientExoApp::GetClientAIMaster(clientApp), c);               // 0x005ED6B0

    CSWCObject::SetFadeState(c, 1);                                    // 0x0063E220
    CSWCObject::SetDesiredFadeState(c, 1, 0, 0, 0);                    // 0x0063CE60
}
c->vtbl[0x94](area->main_scene);           // AttachToScene            0x0060FB40, area + 0x184
*(uint16*)((char*)c + 0x3D4) = anim;
*(uint32*)((char*)c + 0x0E4) = 0;
```

Two things to notice.

- **No appearance and no model here.** The Add message contains only id, party flag,
  position, orientation, animation. The model arrives with the *next* message (`'P'`
  appearance) which runs `SetAppearance` -> `CreateBodyAppearance` -> `LoadModel`. A
  creature is therefore briefly present-but-modelless, which is normal and self-correcting.
- **`AttachToScene` is called on both branches**, existing twin or new, every Add.
  `CSWCCreature::AttachToScene` (`0x0060FB40`) diverts to the minigame scene when
  `creature + 0x3A4` is set and a minigame is active, otherwise passes the argument
  straight through to `CSWCObject::AttachToScene`.

### 7.4 The per-frame sync, and why the client mirror is unwritable

This is the mechanism behind the project's measured "63 times a second" overwrite.
`CServerAIMaster::UpdateState` (`0x004B0B70`), inner loop at `0x004B1050`-`0x004B117B`
(VERIFIED):

```c
CSWSObject* srv = CServerExoApp::GetGameObject(serverApp, id);
if (srv->object_type /*+0x08*/ <= 4)  continue;      // creatures and up only
if (srv->[0x1F8] == 0)                continue;      // not visible to the player -> no sync
CSWCObject* cli = CSWSObject::GetClientObject(srv);  // 0x004CC2B0, lazily links
if (!cli || !cli->vtbl[0x0C]())        continue;

if (IsDirtyFlagSet(srv, 0)) { cli->vtbl[0x134](srv->[0xD4], srv->[0xD8]); ClearDirtyFlag(srv, 0); }
if (IsDirtyFlagSet(srv, 1)) { cli->vtbl[0x12C](&srv->position /*+0x90*/, srv->[0xD4], srv->[0xB8]);
                              ClearDirtyFlag(srv, 1); }
if (IsDirtyFlagSet(srv, 2)) { cli->vtbl[0x130](&srv->orientation /*+0x9C*/); ClearDirtyFlag(srv, 2); }
```

Dirty flags live in a dword bitfield at **`CSWSObject + 0x1FC`**
(`SetDirtyFlag` `0x004CC1F0`, `ClearDirtyFlag` `0x004CC250`, `IsDirtyFlagSet` `0x004CC220`;
bit index = flag number, VERIFIED). Bit 0 = animation, bit 1 = position, bit 2 = orientation.

`CSWSObject::SetPosition` (`0x004CD1A0`) is what raises bit 1 (`or [this+0x1FC], 2`,
VERIFIED at `0x004CD1C8`). So the sanctioned way to move a creature is
`SetPosition`, not a raw store to `+0x90`.

The receiving end, `CSWCCreature::ClientServerSyncHandler_Position`
(vtable `+0x12C`, `0x00611F50`, VERIFIED):

```c
*(Vector*)((char*)this + 0x1DC) = *serverPos;       // desired position
CSWCCreature::MoveCreatureToPosition(this, serverPos);   // 0x0060BDF0 -> writes CSWCObject + 0x24
this->vtbl[0x10C](...);                              // SetGroundNormal
/* then: if not stationary, look up creaturespeed.2da by
   appearance->info.appearance_id (+0x18) and pick walk vs run */
```

Three consequences, all consistent with what the project measured externally:

1. Writing `CSWCObject + 0x24` is pointless. It is an output of this handler.
2. Writing `CSWSObject + 0x90` without raising dirty bit 1 skips this handler, so the
   *animation* is never reselected - which is exactly the observed "teleports in an idle
   pose" symptom recorded in `object-model.md`. (INFERRED: other consumers read `+0x90`
   directly each frame, which is why the raw write still relocated the render.)
3. Live confirmation that this loop runs and consumes its work: every creature's
   `+0x1FC` read back as `0x00`, and every creature's `+0x1F8` read back as `1`.

---

## 8. Minimal requirements for a visible creature, in dependency order

Each item is a precondition for the ones below it.

| # | Requirement | Where it lives | Who satisfies it |
| --- | --- | --- | --- |
| 1 | A module and an area exist, with a loaded client area whose `main_scene` (`CSWCArea + 0x184`) is non-null | `CSWSModule + 0x220`, `CSWCModule + 0x48` | already true in a loaded save |
| 2 | Heap object of `0xAC8` bytes with `CSWSCreature` vtable `0x007470C8` and `object_type` (`+0x08`) `== 5` | `CSWSCreature::Constructor` `0x004F7A10` | you |
| 3 | Registered in the **server** `CGameObjectArray` with a valid id (`< 0x7F000000` counting up) | `CSWSObject::Constructor` -> `AddInternalObject` | automatic, inside #2 |
| 4 | Template data loaded: stats, `creature_stats` (`+0xA74`), appearance block (`+0xA50`) | `CSWSCreature::LoadFromTemplate` `0x005026D0` | you (a `.utc` ResRef) |
| 5 | `appearance_info.appearance_type` (`+0xA60`) is a valid appearance.2da row with a resolvable `RACE` model | appearance.2da via `RULES_PTR->[0xB8]->[0x0C]` | #4, or copy from the PC |
| 6 | A position on the walkmesh, ideally ground-snapped | `+0x90`; `ComputeSafeLocation` `0x004BE860`, `ComputeHeight` `0x004BC380` | you (or copy the PC's z) |
| 7 | **Area membership**: id in `CSWSArea + 0x190` list, and `CSWSObject::SetArea` done | `CSWSArea::AddObjectToArea` `0x0050DFD0`, `CSWSObject::SetArea` `0x004CB160` | `CSWSCreature::AddToArea` `0x004FA100` |
| 8 | Passes `CSWSMessage::TestObjectVisible` for the local player (same area, within perception) -> sets `+0x1F8 = 1` | `0x0056DC60` | automatic, per frame |
| 9 | Add message emitted (first `LastUpdateObject` created for the pair) | `CreateNewLastUpdateObject` `0x00577000` | automatic |
| 10 | **Client twin**: `CSWCCreature` (`0x44C`, vtable `0x0074F5F8`), `SetId(serverId \| 0x80000000)`, `SetArea`, registered via `AddExternalObject`, added to `CClientAIList` | `HandleServerToPlayerCreatureUpdate_Add` `0x0064C0A0` | automatic |
| 11 | Twin linked to server object (`+0x224` / `+0x0F8`) | `GetClientObject` / `GetServerObject`, self-healing | automatic |
| 12 | Appearance message applied -> `SetAppearance` -> `CreateBodyAppearance` | `0x00655E40` -> `0x006134C0` -> `0x00698B10` | automatic |
| 13 | **Model loaded**: `anim_base` at `CSWCObject + 0x68`, `Gob` at `animBase + 0xB8` | `CSWCCreature::LoadModel` `0x0061B380` -> `CSWCAnimBase::LoadModel` `0x0069E1F0` -> `NewCAurObject` `0x00449CC0` | automatic, inside #12 |
| 14 | **Attached to the scene**: `Gob` inserted into `CSWCArea::main_scene` | `AttachToScene` chain, vtable `+0x94` | automatic, at the end of #10 and #12 |
| 15 | On the `CClientAIList` so the twin ticks (animation, position interpolation) | `CClientAIList::AddObject` `0x00604A20`; driven by `CClientAIMaster::UpdateState` `0x006048C0` -> `vtbl[0x74]` = `CSWCCreature::AIUpdate` `0x0061AD60` | automatic, inside #10 |
| 16 | Movement thereafter: server `SetPosition` raises dirty bit 1; `CServerAIMaster::UpdateState` pushes it into the twin and picks walk/run | `0x004CD1A0`, `0x004B0B70` | you call `SetPosition` |

**A render-list entry (`CSWCArea + 0x11C`) is NOT on this list.** See section 4.

Steps 8-15 are the engine's. The plugin owns 2, 4, 5, 6, 7 and 16.

---

## 9. Plugin plan (C# NativeAOT, win-x86)

### 9.1 Threading, first

The plugin currently runs a worker thread (`README.md`). **Engine calls must not happen
there.** All of the above touches the server object array, the client object array, the
scene graph and the resource manager, none of which are thread-safe, and the sync loop in
section 7.4 runs on the game's main thread every frame.

Marshal work onto the main thread by hooking a per-frame function and draining a queue.
`CClientExoAppInternal::MainLoop` (`0x00602EB0`) is the obvious candidate; it is on the
main thread and runs once per frame. The worker thread should only ever push intents into
a lock-free queue.

### 9.2 Delegate declarations

No ASLR, image base `0x400000`, so addresses are literals. Every one of these was
confirmed against the binary; `ret N` was checked to derive the argument count.

```csharp
using System.Numerics;   // or a local [StructLayout(LayoutKind.Sequential)] struct Vec3 { float X, Y, Z; }

internal static unsafe class K1
{
    // ---- globals -------------------------------------------------------------
    // *(void**)AppManager -> +0x04 CClientExoApp, +0x08 CServerExoApp
    const nint APP_MANAGER_PTR = 0x007A39FC;
    const nint RULES_PTR       = 0x007A3A28;   // ->[0xB8] CTwoDimArrays ->[0x0C] appearance.2da

    // ---- app accessors (thiscall, no args) -----------------------------------
    // CServerExoApp
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetModuleSrv    = (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetObjectArray  = (delegate* unmanaged[Thiscall]<nint, nint>)0x004AED70;
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetPlayerCreatureSrv = (delegate* unmanaged[Thiscall]<nint, nint>)0x004B2820;
    static readonly delegate* unmanaged[Thiscall]<nint, uint, nint>      GetCreatureByIdSrv   = (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x004AE770;
    static readonly delegate* unmanaged[Thiscall]<nint, uint, nint>      GetGameObjectSrv     = (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x004AE750;
    // CSWSModule
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetAreaOfModule = (delegate* unmanaged[Thiscall]<nint, nint>)0x004C30F0;
    // CClientExoApp (only needed for verification / diagnostics)
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetModuleCli    = (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;
    static readonly delegate* unmanaged[Thiscall]<nint, nint>            GetPlayerCreatureCli = (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED540;
    static readonly delegate* unmanaged[Thiscall]<nint, uint, nint>      GetCreatureByIdCli   = (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005ED5B0;

    // ---- allocation ----------------------------------------------------------
    // Global::operator_new_2, __cdecl(size)
    static readonly delegate* unmanaged[Cdecl]<uint, nint>               GameNew = (delegate* unmanaged[Cdecl]<uint, nint>)0x006FA7E6;

    // ---- server creature lifecycle ------------------------------------------
    // CSWSCreature::Constructor(uint objId, int flag)                      ret 8
    static readonly delegate* unmanaged[Thiscall]<nint, uint, int, nint>  CreatureCtor = (delegate* unmanaged[Thiscall]<nint, uint, int, nint>)0x004F7A10;
    // CSWSCreature::LoadFromTemplate(CResRef* utc, int flag)               nonzero return == failure
    static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int>   LoadFromTemplate = (delegate* unmanaged[Thiscall]<nint, nint, int, int>)0x005026D0;
    // CSWSCreature::AddToArea(CSWSArea*, float x, float y, float z, int, int)  ret 0x18
    static readonly delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int, void> AddToArea = (delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, int, void>)0x004FA100;
    // CSWSCreature::RemoveFromArea(int)
    static readonly delegate* unmanaged[Thiscall]<nint, int, void>        RemoveFromArea = (delegate* unmanaged[Thiscall]<nint, int, void>)0x004F0FD0;

    // ---- server transform ----------------------------------------------------
    // CSWSObject::SetPosition(Vector* p, int a2, int a3, int a4)           ret 0x10; raises dirty bit 1
    static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, void> SetPositionSrv = (delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, void>)0x004CD1A0;
    // CSWSObject::SetOrientation(Vector* o)                                ret 4
    static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, void>      SetOrientationSrv = (delegate* unmanaged[Thiscall]<nint, Vec3*, void>)0x004CD170;
    // CSWSObject::SetDirtyFlag(int n) / ClearDirtyFlag / IsDirtyFlagSet    bitfield at +0x1FC
    static readonly delegate* unmanaged[Thiscall]<nint, int, void>        SetDirtyFlag = (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CC1F0;
    static readonly delegate* unmanaged[Thiscall]<nint, int, int>         IsDirtyFlagSet = (delegate* unmanaged[Thiscall]<nint, int, int>)0x004CC220;

    // ---- walkmesh helpers (optional; see 9.4) --------------------------------
    // CSWSArea::ComputeSafeLocation(Vector* in, float maxDist, int, int, Vector* out)  ret 0x14
    static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, float, int, int, Vec3*, int> ComputeSafeLocation = (delegate* unmanaged[Thiscall]<nint, Vec3*, float, int, int, Vec3*, int>)0x004BE860;

    // ---- linkage + verification ---------------------------------------------
    // CSWSObject::GetClientObject() - lazily creates the link, never the object
    static readonly delegate* unmanaged[Thiscall]<nint, nint>             GetClientObject = (delegate* unmanaged[Thiscall]<nint, nint>)0x004CC2B0;
    // CSWCCreature::HasGobModel() -> did the model actually load?
    static readonly delegate* unmanaged[Thiscall]<nint, int>              HasGobModel = (delegate* unmanaged[Thiscall]<nint, int>)0x0063F5E0;
    // CSWCCreature::ForceAppearanceUpdate() - client-side rebuild, if ever needed
    static readonly delegate* unmanaged[Thiscall]<nint, void>             ForceAppearanceUpdate = (delegate* unmanaged[Thiscall]<nint, void>)0x00614A30;
}
```

Struct offsets used below:

```
CSWSCreature   size 0xAC8   vtable 0x007470C8
  +0x004 id            +0x008 object_type (byte, 5)      +0x08C area_id
  +0x090 position      +0x09C orientation                +0x0D4 animation
  +0x1F8 visibleToPlayer (engine-written)                +0x1FC dirty flag bitfield
  +0x224 client_object +0x4C0 party_follow_info
  +0xA50 appearance_info (0x24)   +0xA60 appearance_type (u16)   +0xA74 creature_stats

CSWCCreature   size 0x44C   vtable 0x0074F5F8
  +0x020 client area   +0x024 position (mirror)          +0x030 orientation
  +0x068 anim_base -> +0x0B8 Gob*                        +0x0F8 server_object
  +0x21C CSWCCreatureAppearance* -> +0x00 CCAppearanceInfo (+0x18 appearance_id)
  +0x31C staged CCAppearanceInfo (RequestAppearance only; usually zero)

CSWCArea       size 0x284   vtable 0x0074EEF0
  +0x104 object id     +0x11C render_list (NOT creatures) +0x184 main_scene
  +0x20C area object-id list                              +0x27C attached_server_area

CSWCModule +0x48 = CSWCArea*        CSWSModule +0x220 = CSWSArea*
```

### 9.3 Call order: spawn one phantom

Runs on the main thread, inside the frame hook.

```
 1. app       = *(nint*)APP_MANAGER_PTR
    serverApp = *(nint*)(app + 8)
 2. area      = GetAreaOfModule(GetModuleSrv(serverApp))
    if (area == 0) -> no module loaded, abort
 3. pc        = GetPlayerCreatureSrv(serverApp)        // reference for appearance + z
 4. mem       = GameNew(0xAC8)                          // must be the game's allocator
 5. c         = CreatureCtor(mem, 0x7F000000, 0)        // id allocated by the object array
 6. rc        = LoadFromTemplate(c, &utcResRef, 0)      // rc != 0 -> destruct via vtbl[0] and abort
 7. copy appearance:  memcpy(c + 0xA50, pc + 0xA50, 0x24)
    (then optionally overwrite *(ushort*)(c + 0xA60) with a different player row so the
     phantom is distinguishable)
 8. SetOrientationSrv(c, &facing)
 9. pos = desired; pos.Z = *(float*)(pc + 0x98)         // reuse the PC's ground height
    (or ComputeSafeLocation(area, &pos, 20f, 0, 1, &pos))
10. AddToArea(c, area, pos.X, pos.Y, pos.Z, 1, 1)
11. serverId = *(uint*)(c + 4);  clientId = serverId | 0x80000000
12. return; do nothing else this frame
```

### 9.4 Verification, next frame (all reads)

```
*(int*)(c + 0x1F8)  == 1        // engine considers it visible to the player
*(nint*)(c + 0x224) != 0        // client twin exists  (or call GetClientObject(c))
twin = *(nint*)(c + 0x224)
*(nint*)twin        == 0x0074F5F8   // it is a CSWCCreature
*(nint*)(twin + 0x0F8) == c     // back-link
*(nint*)(twin + 0x020) != 0     // client area
anim = *(nint*)(twin + 0x068);  anim != 0 && *(nint*)(anim + 0xB8) != 0   // model loaded
HasGobModel(twin) == 1
appr = *(nint*)(twin + 0x21C);  *(short*)(appr + 0x18) == *(ushort*)(c + 0xA60)
```

If steps 1-12 succeeded and the twin is still missing after a couple of frames, the
suspect is `TestObjectVisible` (`0x0056DC60`) - i.e. the position is outside the player's
perception radius or in the wrong area - not the object construction.

### 9.5 Moving it every tick

```
pos = interpolatedRemotePosition;
SetPositionSrv(phantom, &pos, 1, 1, 0);   // raises dirty bit 1
SetOrientationSrv(phantom, &facing);      // then SetDirtyFlag(phantom, 2) if it does not raise it
```

`CServerAIMaster::UpdateState` then pushes both into the twin and
`ClientServerSyncHandler_Position` selects walk vs run from `creaturespeed.2da`. That is
the piece a raw memory write can never get, and it is the whole reason the phantom
currently teleports in an idle pose.

Do **not** write `CSWCObject + 0x24`; do **not** write `CSWSObject + 0x90` directly if
`SetPosition` is available.

### 9.6 Teardown

`CSWSCreature::RemoveFromArea` (`0x004F0FD0`) then the destructor through
`vtbl[0]` with the deleting flag (`CSWSCreature::Destructor_2` pattern: `push 1; call [vtbl]`).
The client twin is torn down automatically: once `TestObjectVisible` fails,
`ComputeGameObjectUpdateForObject` calls `DeleteLastUpdateObjectsForObject` and the next
pass emits `'D'` -> `HandleServerToPlayerCreatureUpdate_Delete` (`0x0064CC90`).

### 9.7 Open risks

- **Allocator identity (INFERRED).** `Global::operator_new_2` at `0x006FA7E6` is what the
  engine's own spawn paths call. If it is a thunk into a CRT `new` that the plugin's own
  CRT does not share, allocating with it and letting the game free it is still correct
  (same heap on both sides), but allocating with .NET and letting the game free it is
  not. Always use the game's allocator for game objects.
- **`.utc` availability.** `LoadFromTemplate` resolves a ResRef through
  `EXO_RESOURCE_MANAGER_PTR` (`0x007A39E8`); a stock template that exists in every module
  is needed. Copying appearance from the PC afterwards means the template's own appearance
  does not matter, only that it loads.
- **AI will fight you.** `AddToArea` calls `CServerAIMaster::SetAILevel`. A creature with
  live AI recomputes its own position, which is the documented reason Carth cannot be
  driven. Suppressing AI on the phantom (an `ai_level` of `CSWSObject + 0x78`, or keeping
  its action queue at `+0x0FC` empty) is a separate problem, addressed in
  `docs/re/movement.md` territory rather than here.
- **`TestObjectVisible` gating (INFERRED consequence).** Because `+0x1F8` gates the
  per-frame sync, a phantom that walks out of the local player's perception stops being
  synced and eventually loses its twin, then regains both on return. That is correct
  engine behaviour, but any plugin state cached on the twin pointer must tolerate it.
  Never cache a `CSWCObject*`; re-resolve through `GetClientObject` each frame.

---

## 10. Address index

Everything quoted above, in one place. All VERIFIED against the binary.

| Address | Symbol |
| --- | --- |
| `0x00449CC0` | `Global::NewCAurObject` (model -> `Gob`) |
| `0x0043F200` | `Gob::SetScene` |
| `0x0044F7C0` | `Gob::AttachToScene` (Gob vtable `0x741268` slot `+0x4C`) |
| `0x004AE6B0` | `CServerExoApp::GetModule` |
| `0x004AE750` | `CServerExoApp::GetGameObject` |
| `0x004AE770` | `CServerExoApp::GetCreatureByGameObjectID` |
| `0x004AE910` | `CServerExoApp::GetClientObjectByObjectId` |
| `0x004AED70` | `CServerExoApp::GetObjectArray` |
| `0x004B08A0` | `CServerAIMaster::SetAILevel` |
| `0x004B0850` | `CServerAIMaster::AddObject` |
| `0x004B0B70` | `CServerAIMaster::UpdateState` (**the per-frame server -> client push**) |
| `0x004B1F70` | `CServerExoAppInternal::ClientToServerObjectId` |
| `0x004B2820` | `CServerExoApp::GetPlayerCreature` |
| `0x004B3EC0` | `CServerExoAppInternal::UpdateClientGameObjectsForPlayer` |
| `0x004B6950` | `CServerExoAppInternal::UpdateClientGameObjects` |
| `0x004BC380` | `CSWSArea::ComputeHeight` |
| `0x004BE860` | `CSWSArea::ComputeSafeLocation` |
| `0x004C30F0` | `CSWSModule::GetArea` |
| `0x004CB120` | `CSWSObject::GetArea` |
| `0x004CB160` | `CSWSObject::SetArea` |
| `0x004CC1F0` | `CSWSObject::SetDirtyFlag` |
| `0x004CC220` | `CSWSObject::IsDirtyFlagSet` |
| `0x004CC250` | `CSWSObject::ClearDirtyFlag` |
| `0x004CC2B0` | `CSWSObject::GetClientObject` (lazily links) |
| `0x004CC310` | `CSWSObject::AttachmentFromClientObject` |
| `0x004CC320` | `CSWSObject::DetachFromClientObject` |
| `0x004CD170` | `CSWSObject::SetOrientation` |
| `0x004CD1A0` | `CSWSObject::SetPosition` (raises dirty bit 1) |
| `0x004CFCB0` | `CSWSObject::Constructor` (registers in the server object array) |
| `0x004D7E90` | `CGameObjectArray::AddObjectAtPos` |
| `0x004D7FF0` | `CGameObjectArray::AddExternalObject` (client side) |
| `0x004D80E0` | `CGameObjectArray::AddInternalObject` (allocates an id) |
| `0x004D8230` | `CGameObjectArray::GetGameObject` (0 == success) |
| `0x004F0FD0` | `CSWSCreature::RemoveFromArea` |
| `0x004F7A10` | `CSWSCreature::Constructor` |
| `0x004FA100` | `CSWSCreature::AddToArea` |
| `0x00500350` | `CSWSCreature::LoadCreature` (savegame path) |
| `0x005026D0` | `CSWSCreature::LoadFromTemplate` |
| `0x00504A70` | `CSWSArea::LoadCreatures` (GIT reference implementation) |
| `0x00506A10` | `CSWSArea::GetClientArea` |
| `0x0050DFD0` | `CSWSArea::AddObjectToArea` |
| `0x0051B940` | `CSWSCreature::UpdateSubareasOnJumpPosition` |
| `0x0052F820` | `CSWVirtualMachineCommands::ExecuteCommandCreateObject` (runtime reference impl) |
| `0x00567100` | `CSWSMessage::ComputeAppearanceUpdateRequired` |
| `0x0056CD20` | `CSWSMessage::SendServerToPlayerArea_ClientArea` |
| `0x0056DC60` | `CSWSMessage::TestObjectVisible` (writes `+0x1F8`) |
| `0x0056E190` | `CSWSMessage::DeleteLastUpdateObjectsForObject` |
| `0x00572070` | `CSWSMessage::WriteGameObjUpdate_UpdateAppearance` |
| `0x00574A10` | `CSWSMessage::WriteGameObjUpdate_UpdateObject` (writes `'U'`) |
| `0x00576FC0` | `CSWSMessage::StoreValuesInLastUpdateObject` |
| `0x00577000` | `CSWSMessage::CreateNewLastUpdateObject` (writes `'A'`) |
| `0x00577FA0` | `CSWSMessage::TestObjectUpdateDifferences` |
| `0x00578060` | `CSWSMessage::ComputeGameObjectUpdateForObject` |
| `0x00578200` | `CSWSMessage::SendServerToPlayerGameObjUpdate` |
| `0x005ED530` | `CClientExoApp::GetModule` |
| `0x005ED540` | `CClientExoApp::GetPlayerCreature` |
| `0x005ED570` | `CClientExoApp::GetGameObjectArray` |
| `0x005ED580` | `CClientExoApp::GetGameObject` |
| `0x005ED5B0` | `CClientExoApp::GetCreatureByGameObjectID` |
| `0x005ED6B0` | `CClientExoApp::GetClientAIMaster` |
| `0x005EDA50` | `CClientExoAppInternal::ServerToClientObjectId` (body at `0x005F2AC0`) |
| `0x005F1180` | `CClientExoAppInternal::GetCreatureByGameObjectID` |
| `0x00602EB0` | `CClientExoAppInternal::MainLoop` (per-frame hook candidate) |
| `0x006048C0` | `CClientAIMaster::UpdateState` (ticks twins via `vtbl[0x74]`) |
| `0x00604A20` | `CClientAIList::AddObject` |
| `0x00605690` | `CSWCArea::AttachmentFromServerArea` |
| `0x00607610` | `CSWCArea::LoadArea` |
| `0x0060B380` | `CSWCCreature::SetMovingOrientation` |
| `0x0060B9B0` | `CSWCCreature::SetPosition` |
| `0x0060BDF0` | `CSWCCreature::MoveCreatureToPosition` |
| `0x0060C8E0` | `CSWCCreature::UnloadModel` |
| `0x0060C910` | `CSWCCreature::RequestAppearance` |
| `0x0060EF50` | `CSWCCreature::SetInParty` |
| `0x0060FB40` | `CSWCCreature::AttachToScene` |
| `0x006134C0` | `CSWCCreature::SetAppearance` |
| `0x00614A30` | `CSWCCreature::ForceAppearanceUpdate` |
| `0x00614B20` | `CSWCCreature::SetArea` |
| `0x00616A20` | `CSWCCreature::Constructor` |
| `0x00616FA0` | `CSWCObject::SetId` |
| `0x006179C0` | `CSWCCreature::GetModel` |
| `0x0061AD60` | `CSWCCreature::AIUpdate` |
| `0x0061B380` | `CSWCCreature::LoadModel` |
| `0x0063C110` | `CSWCObject::SetOrientation` |
| `0x0063CE60` | `CSWCObject::SetDesiredFadeState` |
| `0x0063D4B0` | `CSWCObject::GetServerObject` (lazily links) |
| `0x0063D520` | `CSWCObject::AttachmentFromServerObject` |
| `0x0063D820` | `CSWCObject::AttachToScene` |
| `0x0063E220` | `CSWCObject::SetFadeState` |
| `0x0063E350` | `CSWCObject::SetObjectVisibilityOnSetArea` |
| `0x0063F340` | `CSWCObject::SetPosition` |
| `0x0063F570` | `CSWCObject::SetArea` (maintains `CSWCArea + 0x20C`) |
| `0x0063F5E0` | `CSWCCreature::HasGobModel` |
| `0x006449D0` | `CExoArrayList<uint>::AddUnique` |
| `0x0064C0A0` | `CSWCMessage::HandleServerToPlayerCreatureUpdate_Add` |
| `0x0064CC90` | `CSWCMessage::HandleServerToPlayerCreatureUpdate_Delete` |
| `0x00655E40` | `CSWCMessage::HandleServerToPlayerCreatureUpdate_Appearance` |
| `0x00656BD0` | `CSWCMessage::HandleServerToPlayerUpdate_Add` (type dispatch) |
| `0x00656C60` | `CSWCMessage::HandleServerToPlayerUpdate_Appearance` |
| `0x0066A4A0` | `CSWCMessage::HandleServerToPlayerGameObjectUpdate` (letter dispatch) |
| `0x0066DCC0` | `CExoArrayList<int>::remove` |
| `0x00691CC0` | `CSWCTrigger::AddToRenderList` (the only creature-shaped user of `+0x11C`) |
| `0x00696D40` | `CSWCCreatureAppearance::GetAnimType` |
| `0x00698550` | `CSWCCreatureAppearance::CreateSimple` (reads `RACE`, calls `LoadModel`) |
| `0x00697CE0` | `CSWCCreatureAppearance::CreateBTypeBody` |
| `0x00698A40` | `CSWCCreatureAppearance::RequestBodyAppearance` |
| `0x00698B10` | `CSWCCreatureAppearance::CreateBodyAppearance` |
| `0x0069CD80` | `CSWCAnimBase::AttachToScene` |
| `0x0069E1F0` | `CSWCAnimBase::LoadModel` (creates the `Gob`) |
| `0x006FA7E6` | `Global::operator_new_2` |

`CSWCCreature` vtable is `0x0074F5F8`; useful slots (VERIFIED by dumping it):

| slot | offset | function |
| --- | --- | --- |
| 1 | `+0x04` | `CSWCObject::SetId` |
| 30 | `+0x78` | `CSWCCreature::LoadModel` |
| 32 | `+0x80` | `CSWCCreature::UnloadModel` |
| 33 | `+0x84` | `CSWCCreature::SetArea` |
| 34 | `+0x88` | `CSWCObject::SetOrientation` |
| 35 | `+0x8C` | `CSWCCreature::SetPosition` |
| 37 | `+0x94` | `CSWCCreature::AttachToScene` |
| 38 | `+0x98` | `CSWCCreature::GetModel` |
| 39 | `+0x9C` | `CSWCObject::SetAnimation` |
| 57 | `+0xE4` | `CSWCObject::ReAttachVisualEffects` |
| 75 | `+0x12C` | `ClientServerSyncHandler_Position` |
| 76 | `+0x130` | `ClientServerSyncHandler_Orientation` |
| 77 | `+0x134` | `ClientServerSyncHandler_Animation` |

`CGameObject` vtable slot `+0x0C` is the `AsSWCObject`-style cast used by
`GetClientObject`; slot `+0x10` is the `AsSWSObject` cast used by `GetServerObject` and
`ComputeGameObjectUpdateForObject`.

---

## 11. Corrections and additions to `object-model.md`

Flagged here rather than edited into that file, since it belongs to another workstream.

1. `CSWSCreature.appearance` at `+0xA4C` reads **0 on all sixteen live creatures**,
   including the PC. The live appearance is `appearance_info.appearance_type` at
   **`+0xA60`**. (VERIFIED live.)
2. The `GAME_OBJECT_TYPES` values it lists as unrecoverable are recoverable: creature is
   **5**, and the full table is in section 2 above. (VERIFIED.)
3. Its "position write sticks unless something is driving the creature" rule is right but
   incomplete: the propagation from server `+0x90` to the *client twin and its animation*
   only happens when **dirty bit 1 at `CSWSObject + 0x1FC`** is set, which only
   `CSWSObject::SetPosition` (and friends) do. That is the missing half of why a raw write
   relocates the render but produces an idle pose. (VERIFIED code; INFERRED causal link to
   the observed symptom.)
4. `CSWSCreatureAppearanceInfo + 0x00 .. +0x0F` (unnamed in the database) holds four
   object ids, `0x7F000000` when unused; Carth has three real ones. INFERRED: equipped
   appearance-affecting item ids.
