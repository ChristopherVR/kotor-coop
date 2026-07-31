# Phantom equip crash — access violation in CExoString::CompareNoCase_2 (0x005E6491)

Retail GOG `swkotor.exe`, base 0x400000, no ASLR (disk == memory). Static RE + plugin
source. Live crash on the **host** instance during a two-instance run.

## Symptom

Host plugin log immediately before the fault:

```
spawned phantom 1: id 0x12D
equip: phantom 0x12D wears g_a_clothes01 (slot 1)     <- body/clothing equip, OK
move#1 phantom 0x12D ...
map marker: slot 0 pinned as 0x12F
equip: phantom 0x12D wears g_w_shortswrd01 (slot 4)   <- RIGHT-WEAPON equip, OK (logged)
CRASH: access violation at 0x005E6491
```

Both `equip:` lines print the "wears" form, i.e. `Odyssey.EquipItemFromTemplate` **returned
true** for each. The crash is therefore *not* inside the equip call — it happens on a later
tick, after the weapon equip was accepted and queued.

---

## 1. The faulting operand at 0x5E6491 — VERIFIED

`CExoString::CompareNoCase_2` = 0x005E6450, `__thiscall` (this=ecx, arg = `[esp+4]` = pointer
to the *other* CExoString). Relevant disassembly:

```
0x005E6450  mov  eax, [esp+4]        ; eax = &other
0x005E6458  mov  edi, [eax]          ; edi = other->CString   (arg buffer ptr)
0x005E645A  test edi, edi
            ... (walks 'edi' for strlen -> ebp; edi was valid, no fault) ...
0x005E6472  test edi, edi
0x005E6474  mov  esi, [ecx]          ; esi = this->CString     (<-- our bad ptr)
0x005E6476  jne  0x5E6488            ; taken: other->CString != 0
0x005E6488  test esi, esi
0x005E648A  je   0x5E64EF            ; NOT taken: this->CString != 0 (non-null)
0x005E648C  mov  eax, esi           ; eax = this->CString
0x005E648E  lea  edx, [eax+1]
0x005E6491  mov  cl, byte ptr [eax]  ; *** FAULT: read *(this->CString) ***
```

**Faulting operand:** the `this` CExoString's character-buffer pointer — member at offset
+0x00 (`m_psz`/`CString`), loaded into `esi` from `[ecx]` at 0x5E6474 and dereferenced at
0x5E6491.

**State that produces the fault:** `this->CString` is **non-null but dangling/invalid**. The
null case is handled (the `je 0x5E64EF` at 0x5E648A returns cleanly), so this is not a
null-string bug — it is a *stale pointer* in the `this` object. The **argument** string was
valid (it was walked byte-by-byte at 0x5E6463 without faulting).

So: `bad_this_CExoString.CompareNoCase(&valid_arg)`, where `bad_this` is a CExoString whose
buffer pointer points at freed/reinterpreted memory.

---

## 2. Call chain to the crash

### 2a. The equip dispatch is deferred — VERIFIED

`Odyssey.EquipItemFromTemplate` (src/KotorCoop.Plugin/Odyssey.cs:632) does:
`CreateItemOnObject` (0x0052F570, synchronous — item is really made) then
`ActionEquipItem` (0x005355F0, `bInstant=1`).

`ExecuteCommandActionEquipItem` 0x005355F0 does **not** equip synchronously. It ends at:

```
0x005356FF  call 0x004F0420          ; CSWSCreature::AddEquipItemActions
```

i.e. it *queues* an equip action on the creature's action list. The real equip runs later
when the creature's action queue is serviced (`RunEquip` 0x00501DE0 -> `CSWSCreature::EquipItem`
0x004FEB90). This is why the `equip: … wears g_w_shortswrd01` line prints and *then* the
process dies a tick or two later.

### 2b. The equip triggers a server-side appearance rebuild — VERIFIED

Equipping a visible item flags the creature for an appearance update. The server's replication
builder reruns the equipped-item model build:

- `CSWSCreature::UpdateAppearanceForEquippedItems` 0x004ED620 is called from exactly two sites,
  both in the server update-message path:
  - `CSWSMessage::ComputeAppearanceUpdateRequired` 0x00567124
  - `CSWSMessage::CreateNewLastUpdateObject` 0x0057706A
- Immediately after that call, the replication code reads the creature's appearance word:
  `0x0057706F  mov dx, word ptr [esi+0xA60]` — the **same field the plugin pokes** (see §3).
- `UpdateAppearanceForEquippedItems` reads the creature's model/hide items via
  `CSWInventory::GetItemInSlot` 0x005A4C20 for the creature-model slots (0x10, 0x20) and rebuilds
  the body model from appearance data.

### 2c. The CompareNoCase site — INFERRED (no live stack captured)

We have EIP but not a stack trace, and `CompareNoCase_2` has 41 call sites. On the
appearance-rebuild path the reachable ones are the 2DA lookups:

- `C2DA::GetColumnIndex` 0x00413100 (CompareNoCase call at 0x00413167)
- `C2DA::GetRowIndex`    0x004138B0 (CompareNoCase call at 0x00413917)

In **both**, `this` is a string **stored inside the 2DA** (column header / row label), and the
argument is the requested key:

```
GetColumnIndex: lea ecx, [edx + edi*8]   ; edx = [this+0x34] = column-name array -> this = &header[i]
GetRowIndex:    lea ecx, [edx + edi*8]   ; edx = [this+0x30] = row-label array  -> this = &label[i]
```

That matches the fault exactly: `this` (a stored 2DA/appearance string) has a dangling buffer,
the argument (the requested key) is valid.

Full inferred chain:

```
(later tick) action queue
  -> CSWSCreature::RunEquip           0x00501DE0
  -> CSWSCreature::EquipItem          0x004FEB90
  ... creature flagged for appearance update ...
server update pass:
  -> CSWSMessage::ComputeAppearanceUpdateRequired / CreateNewLastUpdateObject  0x00567100 / 0x00577000
  -> CSWSCreature::UpdateAppearanceForEquippedItems   0x004ED620
  -> appearance/2DA model resolution
  -> C2DA::GetColumnIndex / GetRowIndex               0x00413100 / 0x004138B0
  -> CExoString::CompareNoCase_2                       0x005E6450  (fault at +0x41 = 0x5E6491)
```

`NEEDS-LIVE`: capture the actual stack (or break on 0x005E6450 with a data breakpoint on the
faulting `ecx`) to promote 2c from INFERRED to VERIFIED and to pin whether `this` is a 2DA
string vs. a body-part CExoString inside `CSWSCreatureAppearanceInfo`.

---

## 3. Root cause — appearance changed by raw poke, derived state never rebuilt

`PhantomController.ApplyAppearance` (src/KotorCoop.Plugin/PhantomController.cs:505) and the spawn
path write the appearance **row number directly into the field** and nothing else:

```csharp
private const int CreAppearanceType = 0xA60;         // PhantomController.cs:497
...
ushort* field = (ushort*)(phantom + CreAppearanceType);
if (*field != (ushort)appearance) *field = (ushort)appearance;   // raw poke, no rebuild
```

Verified layout from the RE DB:

- `CSWSCreature.appearance_info` (`CSWSCreatureAppearanceInfo`) at creature+0xA50.
- `CSWSCreatureAppearanceInfo.appearance_type` (ushort) at struct+0x10 = **creature+0xA60**.
  So the poke lands on `appearance_info.appearance_type` — correct field, but it is only the
  *input* to the appearance state, not the state itself.
- There is **also** a sibling `CSWSCreature.appearance` (ushort) at creature+0xA4C that the poke
  never touches — a second copy that now disagrees with 0xA60.

The engine's own way to change a live creature's appearance is:
`CSWSCreature::UpdateAppearanceDependantInfo` **0x004ED850** — it reads the row from
creature+0xA60 (`movzx ecx, word ptr [esi+0xA60]` at 0x004ED893 and 0x004ED8EB) and rebuilds all
appearance-derived state via 2DA lookups (`call 0x00413660`, appearance.2da / model tables).
It is invoked by every legitimate appearance change: `CSWSCreature::Disguise` 0x004EE010,
`UnDisguise` 0x004EE150, and `PostProcess` 0x004F1C40. **The plugin never calls it.**

Consequence: the phantom is spawned from blueprint `k_coop_phantom.utc` (one appearance /
modeltype), then its `appearance_type` word is overwritten with the remote player's row — which
may be a different modeltype (e.g. a parts-based `P` PC model vs. a full-body commoner) — while
every derived field (modeltype, per-body-part model resref CExoStrings in
`CSWSCreatureAppearanceInfo`, and the 0xA4C mirror) still describes the **old** appearance. When
the weapon equip forces `UpdateAppearanceForEquippedItems` to rebuild the model from the new row,
it reads a body-part / model string that was allocated for the old modeltype (or indexes a 2DA
whose cell it resolves against a stale key) and `CompareNoCase`s a CExoString whose buffer was
freed/reinterpreted — the dangling `this` at 0x5E6491.

This also explains the ordering: the clothing equip (slot 1) went through, but the weapon equip
(slot 4) is what drove the appearance rebuild down the body-model branch that reads the
inconsistent string.

Confidence: fault operand and the deferred-equip → appearance-update wiring are **VERIFIED**; the
"stale un-rebuilt appearance string" as the specific dangling object is **INFERRED** (strongly,
but pending a live stack).

---

## 4. Recommended fix

### Primary (root cause) — rebuild appearance-dependent state after changing the row

Whenever the plugin changes `appearance_type` (creature+0xA60), immediately rebuild the derived
state through the engine instead of leaving it stale. In `Odyssey`, add a thiscall thunk to
`CSWSCreature::UpdateAppearanceDependantInfo` **0x004ED850** and call it right after the poke in
`PhantomController.ApplyAppearance` (and in the spawn-time `ApplyAppearance`):

```csharp
// after: *field = (ushort)appearance;
Odyssey.RebuildAppearance(phantom);   // -> thiscall 0x004ED850, ecx = creature
```

Why it removes the fault: 0x004ED850 recomputes modeltype and the body-part model CExoStrings
from the *new* row (reading creature+0xA60), so no later path — including
`UpdateAppearanceForEquippedItems` on the next equip — is left holding a CExoString allocated for
the previous appearance. It is the exact step `Disguise`/`UnDisguise`/`PostProcess` run for the
same reason.

`NEEDS-LIVE`: the RE DB records `UpdateAppearanceDependantInfo` with param_size 4 (one stack arg
in addition to `this`); the function body clearly recomputes from 0xA60 and did not obviously
consume `[ebp+8]` in the prologue. Confirm the argument (likely a bStore/bUpdate flag — pass 1)
on a live run before shipping. Also consider writing the 0xA4C mirror to match, or preferring the
full `CSWSCreature::Disguise` 0x004EE010 path if standalone rebuild proves insufficient.

### Immediate mitigation (low-risk, ship first) — don't equip weapon slots on phantoms

The crash is driven by the **weapon** equip's appearance rebuild. Weapons on a phantom are
purely cosmetic (the phantom never fights). In `PhantomController.ApplyEquipment`
(src/KotorCoop.Plugin/PhantomController.cs:416) / `EquipInventorySlots`
(PhantomController.cs:400), drop `InventorySlotRightWeapon` (4) and `InventorySlotLeftWeapon` (5)
and equip only `InventorySlotBody` (1):

```csharp
private static ReadOnlySpan<int> EquipInventorySlots => [ Odyssey.InventorySlotBody ];
```

This removes the observed trigger with a one-line change and no new engine call. Caveat: it is a
*mitigation*, not a cure — because the underlying appearance state is still un-rebuilt (§3), a
body/clothing appearance rebuild could in principle reach the same 2DA/part path. Pair it with
the primary fix. (If a live test shows body equips are also unsafe, gate all equips behind the
rebuild landing first.)

### Not the cause — the map-note waypoint

Considered and set aside. `SpawnMapMarker` (src/KotorCoop.Plugin/Odyssey.cs:1352) builds a
`CSWSWaypoint` via raw `operator_new(0x240)` then the real ctor `0x005C7E70`, and never sets a
Tag. A properly-constructed-but-empty CExoString has `CString == null`, which `CompareNoCase_2`
handles safely (the `je 0x5E64EF` path). It would only fault if the ctor left a member CExoString
uninitialised (heap garbage). The crash's tight correlation with the weapon equip — not with map
open or a `GetObjectByTag` — makes the equip/appearance path the far stronger explanation.
`GetNearestObjectByName` 0x004CB5C0 (a CompareNoCase-over-tags caller) remains a theoretical
waypoint risk worth a defensive Tag init if a live stack ever points there.

---

## Verification summary

| Claim | Status |
|---|---|
| Fault operand = `this->CString` (CExoString +0x00), non-null but dangling, read at 0x5E6491 | VERIFIED |
| Arg string valid; null-`this` case is handled (not a null bug) | VERIFIED |
| `ActionEquipItem` (0x5355F0) queues `AddEquipItemActions` (0x4F0420); equip runs on a later tick | VERIFIED |
| `UpdateAppearanceForEquippedItems` (0x4ED620) runs from server appearance-update replication and reads creature+0xA60 | VERIFIED |
| Plugin pokes appearance_type at creature+0xA60 without calling the rebuild (0x4ED850) | VERIFIED |
| Specific CompareNoCase caller = C2DA GetColumnIndex/GetRowIndex on the appearance rebuild; dangling `this` = stale appearance/2DA string | INFERRED |
| `UpdateAppearanceDependantInfo` (0x4ED850) stack-arg value; whether weapon-skip alone suffices; the 0xA4C mirror | NEEDS-LIVE |
