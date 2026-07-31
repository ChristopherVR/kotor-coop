# Placeables, containers, traps and stores in swkotor.exe v1.0.3 (research 2026-07-31)

Goal: extend world-object interaction sync beyond doors. Doors already work
(`DoorController` + the reliable max-merge flag channel, keyed `0x44<<24 | moduleHash8<<16 |
objectId`, monotonic open/lock bits). This maps the same treatment onto **placeables**
(chests, corpses, switches), **container loot**, **traps** and, feasibility-only, **stores**.

House assumptions carried over from `object-model.md`: no ASLR (base `0x400000`, disk==memory),
symbol DB `tools/kotor1_0_3.db` sha256-matched to this exe, every address below directly
usable. Object-id keying works because both installs load the same save/GIT: the `.git` gives
every instance an explicit `ObjectId`, so a placeable/container/trigger has the **same id in
both processes** — exactly the property doors and enemies rely on. Failure mode is the same:
if one side loads a different save or a mod that changes an area's object set, ids diverge and
every channel here must degrade off (reuse the existing save/GIT capability bit that already
drops enemies+doors+items).

Every engine call listed is **main-thread only** (frame hook), like all of `Odyssey.cs`.

---

## 1. Placeables — `CSWSPlaceable`

**Vtable `0x007494D0`** (size `0x454`). Enumerate exactly like the creature scan
(`0x007470C8`) and door scan (`0x007498B0`): scan private committed memory for the 4-byte
value `0x007494D0`; every hit is a placeable. Then object id at `+0x04`, area id at `+0x8C`
(embedded `CSWSObject` at offset 0, same as creatures/doors). `ResolvePlaceable(id)` should be
the door pattern verbatim: `ResolveServerObject(id)` then check `*(nint*)p == 0x007494D0`.

### State fields (all VERIFIED against the symbol DB; persistence VERIFIED by disasm of `CSWSPlaceable::SavePlaceable` `0x00586A70`)

| Offset | Field | Type | Notes |
| --- | --- | --- | --- |
| `+0x260` | **locked** | int | Saved as byte @ `0x00586CBB`. The door-analog lock. |
| `+0x338` | **open** | int | Saved as byte @ `0x00586D13`. 0 = closed/unsearched, nonzero = opened. |
| `+0x328` | usable | int | Static "can be used" flag (saved), not a runtime "used" flag. |
| `+0x330` | lockable | int | Static. |
| `+0x334` | has_inventory | int | 1 ⇒ it is a container; `+0x36C` repository is meaningful. |
| `+0x36C` | **item_repository** | `CItemRepository*` | The container's contents. See §2. |
| `+0x264` | key_name | CExoString | |
| `+0x26C` | key_required | int | |
| `+0x274` | open_lock_dc | short | saved |
| `+0x278` | **trap_flag** | int | "is trapped"; saved as byte @ `0x00586B83`. Clearing ⇒ disarmed. See §3. |
| `+0x27C` | disarm_dc | int | saved |
| `+0x280` | trap_disarmable | int | saved |
| `+0x290` | trap_type | int | |
| `+0x394` | body_bag | int | saved (@ `0x00586BDB` reads `+0x398`) |
| `+0x440` | is_body_bag | int | corpse loot bags are placeables |
| `+0x44C` | is_corpse | int | a lootable corpse is a placeable with a repository at `+0x36C` |

There is **no distinct "activated/used" runtime byte** for switches. A pulled lever's
persistence is script/animation state (its `On*` scripts, local vars, or a global the script
sets), not a placeable field — out of scope here, same class of problem as "which global is
truth".

### Routines (VERIFIED addresses; behaviour VERIFIED by disasm where noted)

| Address | Routine | Use |
| --- | --- | --- |
| `0x00587420` | `OpenInventory` | Reads `+0x338`; if closed and `+0x324` set, plays the open animation via `push 0x275C; call [vtable+0x7C]`; then opens the loot GUI for a player. Entangled with a player-message context (`0x004AE910`) — **not** a clean headless "open the model". |
| `0x00587560` | `CloseInventory` | |
| `0x00587770` | `ClosePlaceableForAllPlayers` | |
| `0x00584AC0` | `RemoveItem(CSWSItem*)` | **The container take primitive.** See §2. |
| `0x00584B10` | `AcquireItem` | |
| `0x00587710` | `GetItemCount` | |
| `0x005877E0` / `0x00587A70` | Constructor / LoadFromTemplate | |

### Applying placeable state on the mirror (host-authoritative)

- **locked** — plain byte write to `+0x260` (0 to unlock). VERIFIED-safe pattern: identical to
  the door lock write (`Odyssey.UnlockDoor` writes door `+0x2C4`). No engine call, no crash risk.
- **open (cosmetic "lid open / searched")** — write byte `+0x338 = 1`. There is no clean
  placeable equivalent of `CSWSDoor::SetOpenState`; `OpenInventory` insists on a player context
  and pops the loot GUI, which is wrong for a headless mirror. To also play the open pose,
  invoke the placeable's own animation the way `OpenInventory` does: `[vtable+0x7C](this, 0x275C)`.
  **INFERRED / NEEDS-LIVE** — this is a guessed-ABI vtable call (thiscall, one int arg); if the
  slot signature is wrong it can crash. Unlike doors there is **no walkmesh coupling** on a
  placeable, so a bare `+0x338` field write desyncs nothing structural — it is the low-risk
  minimum, and the animation is pure cosmetics that can stay NEEDS-LIVE.

**Recommendation:** highest value is `locked` + container contents (§2); the "opened lid" pose
is low value and should ship last. Mirror `locked`/`open` on the existing max-merge flag
channel with a new tag byte (e.g. `0x50`), monotonic (locked→unlocked, closed→open), keyed
`0x50<<24 | moduleHash8<<16 | objectId`. A `PlaceableController` mirrors `DoorController`
one-for-one.

---

## 2. Containers / loot — `CItemRepository`

A container's contents live in one `CItemRepository` (size `0x18`), pointed to by
`CSWSPlaceable+0x36C` (also corpses/body-bags, same offset). Layout VERIFIED by disasm:

| Offset | Field | Notes |
| --- | --- | --- |
| `+0x0C` | items_list | flat array of **object ids** (DWORDs), NOT pointers |
| `+0x10` | item_count | int |

Proof the entries are object ids: `ItemListGetItemObjectID(index)` `0x005560E0` bounds-checks
`index` against `+0x10` and returns `[[this+0xC] + index*4]` verbatim (OBJECT_INVALID if OOB);
`ItemListGetItem(index)` `0x005560B0` takes the same DWORD and resolves it id→`CSWSItem*` via
`0x004AE760`; and `CItemRepository::RemoveItem` `0x00555FD0` searches the list for a DWORD equal
to the item's **object id at `CSWSItem+0x14`**. So the id in the list == the item's world object
id == the same id in both installs.

### Enumerating a container's items (read path, same shape as the area object array)

```
placeable = ResolvePlaceable(placeableId)          // vtable 0x007494D0
repo       = *(placeable + 0x36C)                  // may be 0 if has_inventory==0
count      = *(int*)(repo + 0x10)
ids        = *(int**)(repo + 0x0C)
for i in 0..count:  itemObjectId = ids[i]
```

No engine call needed to read; a direct field read is enough for the host to diff its
container against the last snapshot and detect a removed item — mirroring how `GroundItemStore`
diffs the floor.

### Take primitive (VERIFIED)

`CSWSPlaceable::RemoveItem(CSWSItem*)` `0x00584AC0`:
1. `GetItemInRepository` `0x00555F40` confirms the item is in `+0x36C`;
2. `CItemRepository::RemoveItem` `0x00555FD0` unlinks the id from the list;
3. `CSWSItem` unlink/dispose at `0x00553210` (args `0x7F000000, 1, 1, 0x7F000000`) detaches the
   item from the repository.

Callers include the store take, the loot GUI, and mine pickup — it is the engine's own
"remove item from this placeable" path.

### Design for loot sync ("host loots container X item Y → client removes it too")

Reuse the **ground-item `ItemOp` removal pattern** exactly, host-authoritative:
- Host detects item id `Y` disappeared from container `X`'s `+0xC` list (diff vs snapshot),
  broadcasts a removal op keyed by `(X, Y)` — same envelope as a ground-item vanish.
- Client resolves placeable `X` by id, walks `X`'s repository for the `CSWSItem*` whose
  `+0x14 == Y` (or `ItemListGetItem`), and calls `CSWSPlaceable::RemoveItem(X, item)` to delete
  it from its copy. Object ids match across installs, so `(X, Y)` is unambiguous.
- Do **not** try to deposit the looted item into the client's party — that dupes. The mirror is
  "remove from the container", identical to the ground-item "vanished" leg (which reports a
  pickup without transferring). Corpses/body-bags: same code path via the same `+0x36C`.

This is strictly safer than the placeable "open" pose: `RemoveItem` is a VERIFIED engine
routine with a known thiscall signature `(this, CSWSItem*)`, not a guessed vtable slot.

**NEEDS-LIVE:** the take has not been exercised cross-instance; confirm `+0x14` is the id an
item in a container reports (the ground-item code already relies on `+0x14`/`+0x04` duality —
reuse `Odyssey.ResolveServerObject`'s two-shape logic). Also confirm the item id is stable
between the two saves for pre-placed container contents (GIT-seeded, should be).

---

## 3. Traps / triggers (lower priority)

**`CSWSTrigger`** vtable `0x00749A38` (size `0x318`). Mines/traps that are triggers.
DB-labelled: trap_detectable `+0x2C0`, trap_disarmable `+0x2C4`, trap_type `+0x2D4`.
`CSWSTrigger::SaveTrigger` `0x0058E660` additionally persists **bytes at `+0x2CC` and `+0x2D0`**
(not in the DB) alongside `+0x2D4` — these are the runtime trap-state bytes (INFERRED roles:
is-trap-active / one-shot-fired). NEEDS-LIVE to pin which is which.

For a **placeable** trap, the simpler surface is `trap_flag +0x278` (int, saved as byte): a
disarmed trap has it cleared. Mirror = byte write `+0x278 = 0` on the disarming side, monotonic
(armed→disarmed only). Low risk, no engine call.

Mines placed as triggers are item-backed: `CSWSTrigger::OnEnterMine` `0x0058D570` removes the
mine via `CItemRepository::RemoveItem` `0x00555FD0`, and disarm runs through
`OnApplyDisarm` `0x004D9CE0`. A cleaner mirror for a **disarmed mine** is therefore removal
(delete the mine object on the peer) rather than a flag, reusing the §2 take/destroy path.

**Recommendation:** ship only the placeable `trap_flag +0x278 = 0` monotonic mirror first
(keeps a disarmed trapped-chest disarmed for both, byte write, safe). Trigger-mine sync is a
follow-up gated on identifying `+0x2CC`/`+0x2D0` live.

---

## 4. Stores / merchants (feasibility only)

**`CSWSStore`** vtable `0x0074BDB8` (size `0x258`). Its stock **is a normal `CItemRepository`**
at `+0x230` (same struct as §2), with `AddItemToInventory` `0x005C70C0`,
`AcquireItem` `0x005C7610`, `RemoveItem` `0x005C6BA0`, `SellItem` `0x005C6F70`. So a merchant's
inventory could be kept consistent with the **same id-list diff + take/add primitives** as
containers — mechanically feasible, no new machinery.

Caveats that make it a later, larger job (don't build now): stock mutates on every buy/sell,
markup/markdown (`+0x244`/`+0x248`) and gold interplay make "who is authoritative" a real
economy-design question, and infinite-stock store entries don't behave like unique world items.
Verdict: **feasible, defer** — note only.

---

## Executive summary

- **Feasible, and it reuses machinery that already exists.** Placeables, container loot, trap
  disarm and (later) stores all key on the same object-id-matches-across-installs property that
  doors/enemies/items already exploit, and all four ride existing transports (the door-style
  max-merge flag channel for state bytes; the ground-item `ItemOp` pattern for loot removal).
- **Placeables:** `CSWSPlaceable` vtable **`0x007494D0`**, enumerate by vtable scan just like
  creatures. State bytes `locked +0x260`, `open +0x338` (both VERIFIED, both saved). Mirror
  `locked` with a plain byte write (safe, door-proven); mirror `open` as a byte write plus an
  INFERRED/NEEDS-LIVE animation vtable call (`[vt+0x7C](0x275C)`) that is cosmetic-only.
- **Containers/loot (the prize):** contents = `CItemRepository*` at `+0x36C`; object-id array at
  repo `+0x0C`, count `+0x10` (VERIFIED). Host diffs the list, broadcasts `(container, item)`
  removals; client applies via `CSWSPlaceable::RemoveItem` **`0x00584AC0`** (VERIFIED, clean
  thiscall). Corpses/body-bags use the same `+0x36C`.
- **Traps:** placeable `trap_flag +0x278 → 0` monotonic mirror is the safe first step; trigger
  `CSWSTrigger` (`0x00749A38`) state bytes `+0x2CC/+0x2D0` are NEEDS-LIVE.
- **Stores:** `CSWSStore` `+0x230` is a normal repository — feasible, deferred.
- **Biggest risk:** the two guessed-ABI paths — the placeable open-animation vtable slot
  (`[vt+0x7C]`) and the `CSWSItem` dispose tail inside `RemoveItem` — are crash risks if a
  signature is wrong; keep both behind live verification and prefer the field-write minimum
  (`locked`, `trap_flag`) and the VERIFIED `RemoveItem` call. Same-save assumption is the
  standing failure mode: divergent saves/mods break id keying, so gate every channel on the
  existing save/GIT capability bit.
