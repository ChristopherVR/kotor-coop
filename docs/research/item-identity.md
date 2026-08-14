# Item identity: what a `CSWSItem` carries that its blueprint does not (2026-08-14)

Why a replicated item used to arrive as a pristine stock one, which five fields actually
hold the difference, and why writing them onto a freshly created body is the engine's own
idiom rather than a poke we invented.

Read `docs/research/placeables-containers.md` §2 first — it establishes the container/item
object model this builds on — and `docs/research/engine-api.md` for the rule every engine
call here would have had to satisfy if it made any (it makes none).

Tag key: **VERIFIED** = read from the instruction stream of the GOG retail `swkotor.exe`
1.0.3 (disk == memory, no ASLR, image base `0x400000`) with `tools/disasm.py`,
`tools/xrefs.py` and `tools/fieldwrites.py`, or from `tools/kotor1_0_3.db` cross-checked
against that stream · **INFERRED** = deduced from code I did read · **NEEDS-LIVE** = must be
confirmed on the running game. **Nothing in this document was measured in a running game.**

---

## Headline

**A resref names a blueprint, and the ground-item channel carried nothing else.** The
receiving instance called `CreateItemOnFloor(sTemplate, lLoc, bAnim)` — which takes a
template name and *nothing else* — so the item it built was whatever the `.uti` says: one of
it, full charges, unidentified, no upgrades. Every mutable thing about the sender's actual
item was dropped on the floor of the sending instance and never picked up again.

**Five fields hold the whole difference, and the engine names them itself.**
`CSWSItem::SaveItem` `0x0055CCD0` and `CSWSItem::LoadDataFromGff` `0x0055FCD0` are a matched
persist/restore pair, and between them they write and read exactly: `StackSize`, `Charges`,
`MaxCharges`, `Upgrades`, and three bits of one flags dword (`Identified`, `Stolen`,
`NonEquippable`). That list is not a judgement call — it is the engine's own answer to "what
would be lost if I rebuilt this item from its template".

**KOTOR 1 stores upgrades as a BITMASK on the item, not as a level and not as a list.**
`CSWSItem::HasUpgrade` `0x004EADA0` is nine instructions: `[this+0x294] & (1 << n)`. The
item's `PropertiesList` already contains the upgrade-granted properties, each tagged with an
`UpgradeType` byte, and the mask decides which of them count (§3). So carrying one dword
carries every upgrade.

**Writing those fields onto a freshly created body is what the workbench does.** The upgrade
GUI at `0x006C5620` rebuilds an upgraded weapon by calling `LoadFromTemplate`, then
`SetIdentified(1)`, then copying the *old* item's `+0x294` and its Stolen bit onto the *new*
one — with no rebuild call afterwards. Our apply path is that sequence, performed by us
instead of by the workbench.

---

## 1. `CSWSItem` — the per-instance state fields

`CSWSItem` vtable `0x00748DA0`, size `0x298`. Its embedded `CSWSObject` starts at `+0x10`
(so the object id is at `+0x14` from the item base, or `+0x04` from the object), which is the
two-shape resolve `Odyssey.ResolveItemBase` and `KotorGameBridge`'s item scan already handle.

All VERIFIED. Offsets from `tools/kotor1_0_3.db`; the *role* of each is from the disassembly
of `SaveItem`/`LoadDataFromGff`, which name every one with its GFF label.

| Offset | Field | Type | GFF label | Provenance |
| --- | --- | --- | --- | --- |
| `+0x0C` | base item id | int | `BaseItem` | `SaveItem` @ `0x0055CCDD` |
| `+0x248` | active property count | int | — | list head for §3 |
| `+0x24C` | passive property count | int | — | |
| `+0x250` | active properties | `CSWItemProperty*` | — | stride `0x1C` |
| `+0x254` | passive properties | `CSWItemProperty*` | — | |
| `+0x258` | **charges** | int (byte-ranged) | `Charges` | `SaveItem` @ `0x0055CE27` writes the low byte; `LoadDataFromGff` @ `0x00560024` zero-extends a GFF byte into the dword. **Default 50** when the field is absent (`push 0x32` @ `0x0055FFFF`). |
| `+0x25C` | **max charges** | int (byte-ranged) | `MaxCharges` | `SaveItem` @ `0x0055CE3D`, load @ `0x0056004E` |
| `+0x260` | add cost | int | `AddCost` | feeds `GetCost` |
| `+0x264/5/6` | model / body / texture variation | byte | `ModelVariation`, `BodyVariation`, `TextureVar` | blueprint-derived; see §5 |
| `+0x268` | possessor object id | uint | — | `OBJECT_INVALID` = on the floor (already used by the floor probe) |
| `+0x270` | identified description | CExoLocString | `DescIdentified` | §5 |
| `+0x278` | description | CExoLocString | `Description` | §5 |
| `+0x280` | localized name | CExoLocString | `LocalizedName` | §5 |
| `+0x288` | **flag bitfield** | dword | see §2 | |
| `+0x28C` | **stack size** | dword, high half kept 0 | `StackSize` | `SaveItem` reads it as a WORD (`mov dx, word [esi+0x28C]` @ `0x0055CD4C`); the loader zero-extends a GFF word into the full dword (`movzx eax, ax` @ `0x0055FE76`) |
| `+0x294` | **installed-upgrade mask** | dword | `Upgrades` | `SaveItem` @ `0x0055CD7D` (AddDWORD), load @ `0x0055FECF` |

`+0x108` is the object's plot flag (`Plot`, saved from the same routine) and belongs to the
embedded `CSWSObject`, not to item identity.

### 1.1 Sanity check on the two "int" fields that hold byte values

`Charges` and `MaxCharges` are declared `ulong` in the symbol DB and used as full dwords by
the engine, but they are *persisted* as GFF bytes, so their live range is 0..255. Our decode
clamps to that rather than trusting the read, because the same read can land on reused
memory (§6).

---

## 2. The flags dword at `+0x288`, bit by bit

VERIFIED — every bit below is a shift-and-mask in `SaveItem`, paired with the matching
XOR-merge in `LoadDataFromGff`, and each is written out under a named GFF label.

| Bit | Meaning | Save site | Load site |
| --- | --- | --- | --- |
| 0 | **Identified** | written as the literal 1 (§2.1) | `0x0055FD6B` |
| 3 | Dropable | `0x0055CD97` | `0x0055FEDA` |
| 4 | Pickpocketable | `0x0055CDAE` | `0x0055FF0D` |
| 5 | **Stolen** | `0x0055CD61` | `0x0055FE8C` |
| 6 | **NonEquippable** | `0x0055CF32` | `0x0055FF44` |
| 7 | NewItem | `0x0055CF4F` | `0x0056002F` |
| 8 | DELETING | `0x0055CF6C` | `0x00560076` |

Bit 0 is pinned independently by `CSWSItem::SetIdentified` `0x00554170`, which is the whole
function:

```
mov  eax, [ecx+0x288]      ; ret 4, __thiscall, one int arg
xor  eax, [esp+4]
mov  edx, [ecx+0x288]
and  eax, 1
xor  edx, eax
mov  [ecx+0x288], edx
ret  4
```

That is a bit poke and nothing else — no notification, no derived rebuild — which is why the
plugin performs the poke directly instead of adding another declaration to
`tools/callaudit.py`'s audit surface (122 today, all agreeing). The provenance is stronger,
not weaker: the function *is* the documentation for the bit.

### 2.1 An oddity worth recording

`SaveItem` writes `Identified` as the literal `1`, unconditionally (`push 0x74900C; push 1`
@ `0x0055CCFF`). The *loader* reads the field into bit 0 properly, and the workbench calls
`SetIdentified(1)` explicitly, so the bit is real and runtime-meaningful; it is only the
save that flattens it. **INFERRED:** an item that survives a save/load round trip therefore
comes back identified regardless. That is the engine's behaviour, not ours, and it means the
identified bit is worth carrying live (a peer sees the right name immediately) while nobody
should be surprised if a reload changes it.

---

## 3. How upgrades are actually represented

Two halves, both VERIFIED:

**The mask.** `CSWSItem::HasUpgrade(char n)` `0x004EADA0`:

```
mov  eax, ecx
mov  cl, [esp+4]
cmp  cl, 0xFF
je   yes                    ; 0xFF = "no upgrade required" -> always true
mov  edx, 1
shl  edx, cl
test [eax+0x294], edx
jne  yes
xor  eax, eax
ret  4
yes: mov eax, 1; ret 4
```

So `+0x294` is a bitmask over upgrade slot indices, and `0xFF` is the sentinel for
"unconditional".

**The properties.** The item's own `PropertiesList` already holds the upgrade-granted
properties. `CSWSItem::SaveItemProperties` `0x00555790` walks the active list
(`count +0x248`, array `+0x250`) then the passive list (`+0x24C` / `+0x254`), stride `0x1C`,
and writes each entry as:

| Offset | GFF label |
| --- | --- |
| `+0x00` u16 | `PropertyName` |
| `+0x02` u16 | `Subtype` |
| `+0x04` u8 | `CostTable` |
| `+0x06` u16 | `CostValue` |
| `+0x08` u8 | `Param1` |
| `+0x09` u8 | `Param1Value` |
| `+0x0A` u8 | `ChanceAppear` |
| `+0x0C` u8 | `Useable` |
| `+0x10` u8 | `UsesPerDay` |
| `+0x11` u8 | `UpgradeType` |

And the consumer at `0x0057E4A9` closes the loop: it fetches an active property, reads its
`UpgradeType` byte at `+0x11`, calls `HasUpgrade` with it, and only then adds the property's
`CostValue`. **An upgrade property is inert until its bit is set.**

Consequence, and it is the load-bearing one: **the properties come from the blueprint; only
the mask is per-instance.** `CreateItemOnFloor` builds the properties for us, so replicating
`+0x294` replicates the upgrades. This is exactly the workbench's own conclusion — see §4.

**INFERRED:** a *mod* that authors upgrade properties differently would break this, in the
same way and for the same reason every other blueprint-keyed channel does. The existing
override-folder mismatch warning already covers it.

---

## 4. The workbench does what we do

`CSWGuiUpgrade`'s install path, `0x006C5620` onward (VERIFIED):

```
call 0x5530A0            ; CSWSItem::Constructor
call 0x5608B0            ; LoadFromTemplate  -> a fresh, stock item
call 0x553210            ; SetPossessor
push 1; call 0x554170    ; SetIdentified(TRUE)
mov  eax, [<old>+0x294]
mov  [<new>+0x294], eax  ; copy the upgrade MASK across verbatim
...                      ; then the Stolen bit (0x288 bit 5), same XOR-merge shape
```

No `ApplyItemProperties`, no `ComputeArmorClass`, no notification. That is the strongest
statement available about whether a raw write to `+0x294` needs a rebuild afterwards: the
engine's own upgrade path does not do one.

### 4.1 Nothing caches upgrade-derived state

Checked directly, because the house rule is "rebuild derived state after a field poke" and
skipping that rebuild is what produced the phantom-equip crash:

- `CSWSItem::ComputeArmorClass` `0x00553CC0` — reads the **base item** row (`GetBaseItem`
  `0x005B4790`, then `+0x8A`). Nothing per-instance, nothing upgrade-derived.
- `CSWSItem::GetCost` `0x00554000` — recomputes from the base item's cost float times
  `+0x260` on every call.

So there is no stale sibling to refresh. Contrast `docs/research/phantom-equip-crash.md`,
where an appearance poke *did* leave a dangling derived string; that hazard is specific to
appearance, and it is worth saying so rather than assuming a symmetry that is not there.

`tools/fieldwrites.py 0x294` lists 19 writers of that offset across the whole image; the two
inside `CSWSItem` are the constructor (`0x005531BA`) and `LoadDataFromGff` (`0x0055FECF`),
plus `CopyItem` (`0x0055DB6D`) which copies the mask when duplicating an item. Everything
else is the upgrade GUI or an unrelated class at the same offset.

---

## 5. What is deliberately NOT carried

- **`LocalizedName` `+0x280`, `Description` `+0x278`, `DescIdentified` `+0x270`.** These are
  `CExoLocString`s and they exist per instance, but **KOTOR 1 has no way for a player to
  change them**: there is no rename UI, and the NWScript command set has no `SetName` /
  `SetDescription` (KOTOR 2's `SetItemName`-alikes and NWN2's `SetName` are a different
  engine). They are therefore blueprint-derived in practice, and `CreateItemOnFloor`
  reproduces them from the same `.uti`. Carrying three localised strings per ground item to
  replicate a value neither side can change would cost about 200 bytes a datagram for
  nothing. **INFERRED** on the "no script can change them" half; the field-exists half is
  VERIFIED.
- **`ModelVariation` / `BodyVariation` / `TextureVar` (`+0x264`..`+0x266`).** Blueprint
  appearance, re-derived on load from the base item row (`0x0055FFD9` reads `+0xAF` of the
  base item when the loader has no value). Same argument.
- **`AddCost` `+0x260` and the derived `Cost`.** `Cost` is *computed* on save, never
  restored; `AddCost` is a blueprint field.
- **`Dropable` (bit 3), `Pickpocketable` (bit 4), `NewItem` (bit 7), `DELETING` (bit 8).**
  Bits 7 and 8 are engine bookkeeping for the current session and would be actively wrong to
  copy. Bits 3 and 4 are creature-inventory concepts (what a corpse drops, what a pickpocket
  can take) that do not apply to an item lying on the floor, and their loader default is 0 —
  so a copied value could only ever be a downgrade.
- **The `PropertiesList` itself.** Blueprint-derived (§3). Replicating it would mean
  reproducing a variable-length array of `0x1C`-byte structures through `CSWSItem::AddProperty`
  `0x0055E180` — a new engine call, a new failure mode, and no case in the base game where it
  differs from the blueprint's.

---

## 6. Container loot needs none of this, and that is not an oversight

The loot channel (`ContainerLootStore` / `LootController`) carries `(containerId, itemId)`
and removes. It never *creates* an item, because both instances loaded the same save and
therefore already hold the same container with the same contents at the same object ids —
which is the property `placeables-containers.md` §2 establishes. There is no blueprint
round trip, so there is nothing for identity to be lost through. The channel that re-creates
is the ground channel, and that is the one that carries identity.

---

## 7. What was built

**Wire (protocol 24).** `ItemIdentity { StackSize u16, Charges u8, MaxCharges u8,
Upgrades u32, Flags u8 }`, nine fixed bytes appended to every ground-item entry.
`Flags` bit 0 is **KNOWN** and gates the other four fields: a zeroed identity means "nobody
read this item", not "this item is stock". That distinction is the safety property of the
whole feature — a caller that only has a blueprint name (the drop harness, an old snapshot)
must be able to say so, because inventing a stock identity is indistinguishable from having
genuinely read a stock item, and would strip an upgraded weapon.

**Merge rule.** Identity merges upward and only upward, like every other shared fact here:
a re-announcement that knows the identity replaces one that does not; a stock re-announcement
never overwrites a known one; and an authoritative host snapshot decides *which* items are on
the ground but cannot *forget* an identity (`GroundItemStore.Drop` / `ReplaceAll`).

**Shared block (version 39, now 41).** The item slot's stride was already `0x30` and only `0x24`
bytes were used, so identity lives in the tail: `upgrades u32 @0x24`, `stackSize u32 @0x28`,
and `charges | maxCharges<<8 | flags<<16` packed into one dword `@0x2C`. One dword rather
than three bytes because the agent writes each field with a separate `WriteProcessMemory`
and the plugin reads the slot on its own frames.

The slot's identity is refreshed on every pump, not only when the slot is first assigned, so
an identity learned *after* the item was published still reaches the block. The plugin reads
it only when it spawns a body, so this does not retro-fix an item already lying on the floor
— it means the respawn after a module transition gets the item right rather than stock. A
live retro-fix would mean re-applying to a body a player may already have interacted with,
which is a worse trade than one stale floor item.

⚠ **The block version was bumped 38 → 39 in both places** — `PluginHost._block->Version` and
`KotorGameBridge.PluginSignature`'s embedded `27 00 00 00`. The layout is technically
backward-compatible (an old agent leaves the tail zeroed, which reads as "unknown" and
changes nothing), but the *meaning* of those bytes changed, and a mixed pair silently losing
item identity is worse than a mixed pair refusing to find each other. It has since moved to
**41** (`29 00 00 00`) for the four `setitem` fields at `0x33B0..0x33C0` — see §9.
`ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves` compares the two literals,
because nothing at runtime can: the version is *inside* the pattern the agent scans for, so a
half-bump presents as "plugin shared block not found" and reads like a broken install.

**Apply.** `Odyssey.ApplyItemIdentity` runs on the frame hook straight after
`SpawnItemOnFloor` returns an object id: resolve → vtable check → five field writes. No
engine call, so no argument size to get wrong. An id that does not resolve to a live
`CSWSItem` fails the vtable check and the function returns having touched nothing, which
leaves exactly the old behaviour (a stock item) rather than a crash.

**Read.** `KotorGameBridge.TryReadItemIdentity` reads the `0x258..0x298` window externally
and `ItemIdentityLayout.Decode` turns it into an `ItemIdentity`. Pure `ReadProcessMemory`;
the vtable is re-checked *at read time* rather than trusted from the scan, because an item
that is picked up and merged into an existing stack really is freed and its memory reused
(the same hazard `Odyssey.GetItemFloorState` documents). A garbage stack size is clamped,
because that is the one decoded field that ends up written into a real item on another
machine.

---

## 8. Why the apply path could not be reached, and what fixed it (2026-08-15)

The first live pass could not exercise the apply at all. The symptom, recorded in
`docs/live-verification.md`: the plugin logs
`item slot 0: spawned item 0x10001 as object 0x131`, so the ground body genuinely IS object
`0x131` — and four seconds later `drop #131` answers
`drop: object 0x131 is not a readable item here`.

### 8.1 The root cause was an ORDER, not an offset

`AgentDashboard.Drop` asked two questions joined by `||`:

```csharp
string liveResref = live.ItemResref(objectId);
if (liveResref.Length == 0 || !live.TryReadItemIdentity(objectId, out identity))
```

`ItemResref` is a pure lookup in a cache it cannot populate. `TryReadItemIdentity` is the
only method that could rebuild that cache. `||` short-circuits. So the one call able to go
and look was never reached, and the caches were whatever a sweep had built when the agent
attached — long before object `0x131` existed.

The agent log times it exactly: the host attached at 08:11:54, the item was created at
08:12:44, and the refusal came at 08:12:48. Nothing between those two moments could have
re-swept, because `ReadEquippedResref` — the only other rebuild trigger — resolves the
player's three equipped ids out of the cache and therefore never misses.

**None of the reverse-engineering in this document was wrong.** The offsets, the vtable, the
two-shape id resolve and the write sequence were all fine; a cache-refresh path was missing
in front of them. That is worth saying plainly, because the obvious hypotheses — "a ground
item is not a bare `CSWSItem`", "the vtable guard is rejecting it" — were both wrong, and
both were checked:

- **A ground item IS a bare `CSWSItem`.** `CreateItemOnFloor` builds one, and the plugin's
  own floor probe resolves object `0x131` through `GetGameObject` and reads its possessor
  field successfully on every frame it is alive.
- **The vtable constant is right for THIS binary. VERIFIED.**
  `CSWSItem::Constructor 0x005530A0` writes `mov dword [esi], 0x748DA0` at `0x005530EE` and
  `mov dword [edi], 0x748CC0` at `0x005530F4`, where `edi = esi + 0x10`. So a `CSWSItem`
  carries **two** vtable pointers — its own at `+0x00` and the embedded `CGameObject`'s at
  `+0x10` (`CSWSItem_GameObject`, per `tools/kotor1_0_3.db`). That is why a vtable test
  applied to the pointer `GetGameObject` hands back — which is the subobject — rejects every
  item, the failure `Odyssey.GetItemFloorState`'s comment records, and why the same test
  applied to the *resolved base* is correct. The same constructor writes the possessor
  default at `0x0055315F` (`[esi+0x268]`), confirming `+0x268`.

### 8.2 Three more defects in the same read path, all found by reading it

- **The sweep silently truncated at 512 hits.** `PatternScanner.ScanAll` stops the moment it
  has `limit` hits — mid-region, in ascending address order — so every item allocated above
  the cut was invisible, and the item a test cares about is usually the one allocated last.
  Now 4096, and `KotorGameBridge.LastItemScanNote` says when the cap was reached anyway.
- **The refusal named neither half.** "not a readable item here" is the same sentence for a
  wrong id, an unswept id, a freed item and an unreadable tag. Each now says which.
- **`+0x28` is the Tag, and the tag is a PROXY for the resref.** VERIFIED from `SaveItem` at
  `0x0055CCEE`, which writes `[esi+0x28]` under the GFF label at `0x747A28` = `"Tag"`. A live
  `CSWSItem` does not retain its template name at all — `SaveItem` writes `BaseItem` and the
  property list and never a `TemplateResRef`, because an item is rebuilt from its own fields.
  Base-game blueprints set `Tag` to the resref in upper case (`g_w_blstrpstl001.uti` has
  `Tag = G_W_BLSTRPSTL001`), which is what makes the proxy work and what a mod could break.

---

## 9. The unattended harness

KOTOR has no drop UI and no workbench a script can reach, so an unattended run could produce
a sender but never an *interesting* sender: every drop it could make named a blueprint, which
by construction carries no identity. Fixing the read bug alone would therefore have bought a
drop that still carried a stock identity — the second half of why the apply had never run.
Four console verbs close the loop; all of them reach the game through
`pwsh tools/blockpoke.ps1 -ProcId <pid> console '<line>'`.

| Verb | Where it runs | What it is for |
| --- | --- | --- |
| `items [n]` | agent (ReadProcessMemory) | Every live `CSWSItem` the sweep can see, most interesting first, with id, tag, holder and identity. Prints the sweep's own account of itself first, because a short list has two very different causes. |
| `setitem <id> [stack=N] [charges=N] [max=N] [upgrades=0xN] [identified=0\|1]` | plugin (game thread) | Pokes the named fields through **`Odyssey.ApplyItemIdentity`** — the same function an arriving item goes through — and answers with the fields read back. Naming no field is a pure read. |
| `dropitem <resref\|0x131>` | agent → session | Announces a shared ground item. The `0x131` form sends a LIVE item's real identity. |
| `item <id>` | agent (ReadProcessMemory) | Reads one item's fields back, from outside the game, independently of the plugin that wrote them. |

Two independent readers is the point of the split: the plugin's read-back proves the write
reached the address, and the agent's `item` proves the address holds the item.

`dropitem` also changed meaning, and the old meaning was wrong. It used to file a `DropItem`
staging cheat, which calls `CreateItemOnFloor` in the typist's own game — and nothing scans
the floor for items to publish, so that drop reached no peer at all, while both the help text
and `docs/research/console-cheats.md` §4 said "the item channel shares it". It now goes
through the session's own drop, which is the thing that shares it; the local body then arrives
the same way a peer's does, spawned by the plugin from the item slot with identity applied.

### 9.1 Ids are hex and must say so

`0x131` or `#131`. A bare `131` is refused, deliberately: it is both a legal resref and a
legal decimal id, and reading it as decimal would aim at object `0x83` — a different object
that will usually resolve, so the mistake would be silent.

---

## 10. `CreateItemOnFloor` does not check that the blueprint exists (2026-08-15)

The first rig run of §9's harness proved the writer and then hit three walls. All three turned
out to be one engine fact.

**VERIFIED.** `CreateItemOnFloor` `0x00547F60` calls `CSWSItem::LoadFromTemplate` `0x005608B0`
at `0x0054806B` and **never tests its result**. The next instruction reloads an unrelated
stack slot; the item is added to the area at `0x005480A8` and its object id returned at
`0x005480AF` regardless. A resref naming no blueprint therefore produces a live, addressable,
perfectly valid `CSWSItem` — carrying nothing but its constructor's defaults.

From `CSWSItem::Constructor` `0x005530A0`, which is the whole profile of such a body:

| Field | Value | Site |
| --- | --- | --- |
| Tag `+0x28` | **empty** — never written | (absent) |
| Charges `+0x258` | **50** | `0x00553189` `mov dword [esi+0x258], 0x32` |
| MaxCharges `+0x25C` | **uninitialised heap** | no writer — see below |
| StackSize `+0x28C` | 1 | `0x005531CD` |
| Upgrades `+0x294` | 0 | `0x005531BA` |
| BaseItem `+0x0C` | 30 | `CSWItem::Constructor` `0x005B466B` — a real row, so useless as a tell |

`tools/fieldwrites.py 0x25C` lists 23 writers image-wide and exactly two inside `CSWSItem`:
`LoadDataFromGff` `0x0056004E` and `CopyItem` `0x0055DB0F`. **The constructor is not among
them.** So MaxCharges on a template-less item is whatever the allocator handed over.

That is precisely what the rig saw. A drop of `g_w_blstrpstl01` — a typo; the real blueprints
are `g_w_blstrpstl001`..`010` and `020` — produced object `0x131` reading `<no tag>`,
`charges 50/1768092`, stack 1, upgrades 0. Every field matches the table above.

### 10.1 What each blocker actually was

- **"A spawned ground item has no tag, so it cannot be re-sent."** True, and only for this
  reason: the wire carries a blueprint name, the only name a live `CSWSItem` keeps is its Tag
  (§8.2), and a body built from no template has none. A body built from a REAL template does
  have one. Fixed properly either way: `KotorGameBridge.TryGetSpawnedItemResref` recovers the
  resref from the item slot the agent itself wrote it into, which is better evidence than the
  tag proxy ever was.
- **`charges 50/1768092`.** Not a wrong offset. `+0x258` and `+0x25C` are confirmed by their
  own GFF labels — `SaveItem` `0x0055CCEE`+ writes `byte [esi+0x258]` under `"Charges"`
  (`0x748F88`) and `byte [esi+0x25C]` under `"MaxCharges"` (`0x748F7C`). The value is
  uninitialised memory, exactly as the constructor predicts.
- **`items` printed nothing.** Unrelated to all of the above, and a plain plumbing bug: the
  dispatcher's replies went only to `Console.WriteLine`, which in a `demo.ps1` run is a
  visible window with no reader and no file behind it. `CoopCommandDispatcher.Log` is now
  wired to `AgentDashboard.Note`, the same hook `SessionManager.Log` uses.

### 10.2 What changed because of it

- **A garbage MaxCharges is no longer clamped to 255.** Clamping turned memory nobody wrote
  into a plausible maximum and shipped it to a peer. Since both fields persist as GFF *bytes*,
  anything above 255 is provably not a value the engine meant, so it is treated as unread —
  and repaired with **the engine's own default**: at `0x00560016` the loader passes the
  Charges it has just read as the default for MaxCharges. Every base-game item takes that
  path, because **none of the 557 `.uti` blueprints declares a MaxCharges field at all.**
  (Surveyed with `tools/kotorfiles.py`: 557 item blueprints, 0 with an empty Tag, 26 carrying
  real Charges — the droid shields and utility devices, all `Charges 10`.)
- **Both spawn paths now say when a template did not load.** `ItemController` logs
  `spawned 0xID but it has NO TAG and 50 charges — the blueprint almost certainly does not
  exist`, and the `dropitem` cheat answers `'<resref>' is not a blueprint here - 0xID spawned
  EMPTY`. The old guard (`did not spawn (failure N; wrong resref?)`) could never fire for a
  typo, because the engine does not fail. An empty Tag is a safe tell: zero of the 557
  base-game blueprints have one.

---

## 11. NEEDS-LIVE

**The writer is PROVEN LIVE (2026-08-15)**, with two independent witnesses:
`setitem 0x131 stack=20 charges=3 max=50 upgrades=0x5` answered
`0x131: stk 20 chg 3/50 upg 5 bits 18`, the plugin's own read-back logged
`stack 1->20, charges 50/1768092->3/50, upgrades 0x0->0x5`, and the agent's external
`item 0x131` — a different process, a different reader — agreed. So `ApplyItemIdentity`
writes all five fields into a live `CSWSItem` and they read back. That was item 1's first
half and the thing nothing had ever observed.

**The wire half is still unmeasured.** In order of what would invalidate the most:

1. **The apply lands ON A PEER.** `setitem` on the sender, `dropitem 0x<id>`, then read the
   receiver. Three witnesses have to agree: the sender's `setitem` answer, the receiver's
   plugin log line `item slot N: identity applied to 0xID — asked for (...), item now reads
   (...)`, and the receiver's own `item 0x<newId>`. `identity SKIPPED for 0xID — <reason>`
   means the resolve refused and now names why. ⚠ Pick a target with a REAL blueprint name —
   §10 is what happens otherwise.
2. **The read is reading an item.** `items` on a loaded save should list items whose stack
   sizes and charges match the inventory screen. The specific risk is the sweep handing back
   an address whose `+0x14` is an id but which is not the `CSWSItem` base; the vtable+id
   re-check at read time guards it, so the failure mode is a false "unknown", not a bad write.
3. **An upgraded weapon really does reconstitute from mask alone.** `setitem <saber>
   upgrades=0x5`, drop it, and confirm the receiving instance's copy shows the same upgrade
   slots filled and the same stats. This is the one claim in §3 that is an inference from the
   *consumer* rather than a direct observation of the result — and it is the reason `setitem`
   can write the mask at all, since no script can install an upgrade.
4. **A stack survives.** `setitem <grenades> stack=20`, drop, and confirm 20 arrive and that
   picking them up merges into the picker's inventory as 20 (`CSWSItem::MergeItem`
   `0x00553F90` is the merge, and it is untouched by any of this).
5. **The sweep is not truncating.** `items` reports `⚠ the sweep STOPPED at its 4096 cap` if
   it ever does. Seeing that line once would mean the cap is the wrong shape of answer.
6. **The block version skew is clean.** After `build.ps1 -Install`, an agent from before this
   change must report "plugin shared block not found" rather than attaching and writing the
   wrong offsets. That is the intended behaviour of the bump (40 → 41 for the four `setitem`
   fields at `0x33B0..0x33C0`), and it is worth seeing once.
7. **A body the plugin spawned can be re-sent.** `dropitem 0x<theReceiversCopy>` on the
   receiving instance. This is the loop closing on itself and it is the case §10.1 fixed:
   the resref comes from the item slot rather than from the item, so it works even for a
   body whose Tag the engine never filled in.
8. **A bad blueprint is caught.** `dropitem g_w_notathing` should answer
   `'g_w_notathing' is not a blueprint here - 0xID spawned EMPTY` rather than reporting a
   successful drop. Cheap to run, and it is the check that would have saved the first pass.
