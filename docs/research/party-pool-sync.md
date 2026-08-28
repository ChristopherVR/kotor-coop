# Shared party item pool sync (research + build, 2026-08-27)

Roadmap Tier 1 item 1: `CSWPartyTable+0x118` (`pt_item_repo`) is a `CItemRepository*` that
exists independently on every instance. Two players can each drag gear "into the party" and
never see the other's deposit — one pool per game process, not one pool per party. This
document establishes the engine object model (static RE against the symbol database and the
disassembly), the sync design, and what was built.

Tag legend, matching every other doc in this directory: **VERIFIED** = read out of the
sha256-matched GOG retail `swkotor.exe` v1.0.3 instruction stream (`tools/disasm.py`) or the
symbol database cross-checked against it (`tools/q.py`), or asserted by a unit test.
**NEEDS-LIVE** = reasoned from that evidence but never watched happen on a running game.
**INFERRED** = a reasonable deduction from code that was read, not a directly observed fact.
No offset or engine call in this document was exercised against a live process this pass —
static RE, design and implementation only. See "Live test plan" at the end for what a rig
session still owes it.

Read `docs/research/item-identity.md` first (the five per-instance item fields this channel
carries and the `CSWSItem` layout everything here builds on) and
`docs/research/container-sync.md`/`docs/research/progression-sync.md` §3 (the container/party
repository object model this reuses almost unchanged).

---

## 1. RE — the object model

### 1.1 The pointer chain to the pool (VERIFIED)

Reused verbatim from `docs/research/progression-sync.md` §5's load-bearing constants table,
itself cross-checked this pass against `tools/kotor1_0_3.db`:

```
appMgr     = *(u32*)0x007A39FC                        ; APP_MANAGER_PTR
serverApp  = *(u32*)(appMgr + 0x08)                   ; CServerExoApp*
internal   = *(u32*)(serverApp + 0x04)                ; CServerExoAppInternal*
partyTable = internal + 0x0001B770                    ; CSWPartyTable (embedded)
pool       = *(u32*)(partyTable + 0x118)              ; CItemRepository* (pt_item_repo)
```

**VERIFIED** — `tools/kotor1_0_3.db`'s `offsets` table names `CSWPartyTable.pt_item_repo` at
byte offset 280 (`0x118`), type `CItemRepository *`, matching the pre-existing
`progression-sync.md` reading exactly. The agent-side read (`KotorGameBridge.PartyPool.cs`)
reuses the identical `internal + 0x1B770` chain already established and load-bearing for the
journal and progression channels (`KotorGameBridge.Journal.cs`'s `PartyTableOffset`,
`EngineGlobals.TryGetServerInternal`) — nothing new was invented for the walk to the table
itself.

### 1.2 `CItemRepository` — the same class a placeable's container uses (VERIFIED)

```
class CItemRepository (size 0x18):
  +0x00  parent object id (set by SetParentId; the "owner" of this repository)
  +0x04  "new item" counter (incremented/decremented alongside the flags-bit-7 NewItem bit)
  +0x0C  items_list   int*   — flat array of item OBJECT IDS (DWORDs, not pointers)
  +0x10  item_count   int
```

`items_list`/`item_count` at `+0x0C`/`+0x10` are exactly the fields
`docs/research/placeables-containers.md` §2 and `KotorGameBridge.ContainerLoot.cs` already
read for a placeable's container — **VERIFIED**, `CItemRepository` is one class serving both a
placeable's inventory and the party's shared stash. The read side of this feature
(`KotorGameBridge.PartyPool.cs`) reuses `ContainerLoot`'s own `RepositoryItemArray`/
`RepositoryItemCount` constants rather than redeclaring them.

### 1.3 The repository's own methods (VERIFIED, `tools/q.py`/`tools/disasm.py` this pass)

| Method | Address | ABI | Role |
| --- | --- | --- | --- |
| `Constructor` | `0x0055D290` | `__thiscall(this, int parentId)` | sets `+0x00` |
| `Destructor` | `0x00555DE0` | | |
| `FindItemWithBaseItemType` | `0x00555E00` | | unused here |
| `FindItemWithTag` | `0x00555ED0` | `__thiscall(this, char* tag)`, ret 4 | linear scan, recurses into a nested repo at `item+0x26C`; returns the item's object id or `OBJECT_INVALID` |
| `GetItemInRepository` | `0x00555F40` | `__thiscall(this, item*, bool recurse)`, ret 8 | membership test by resolved item POINTER, not id |
| `RemoveItem` | `0x00555FD0` | `__thiscall(this, item*)`, ret 4 | **the primitive this feature calls for removal** |
| `ClearNewItems` | `0x00556050` | | unused here |
| `CalculateContentsWeight` | `0x00556100` | | unused here |
| `SetParentId` | `0x00556150` | | unused here (the constructor already does it) |
| `ItemListGetItem`/`ItemListGetItemObjectID` | `0x005560B0`/`0x005560E0` | | unused here |
| `InsertItem` | `0x0055D2C0` | `__thiscall(this, item*, int index)`, ret 8 | **the primitive this feature calls for addition** |
| `AddItem` | `0x0055D330` | `__thiscall(this, item*)`, ret 16 | the FULL "give item to this repository" primitive — deliberately NOT used, see §2.3 |

Full disassembly of `RemoveItem` (`tools/disasm.py 0x555fd0 200`, VERIFIED):

```
0x00555FD0  push ebx / esi / edi
0x00555FD3  mov edi, [esp+0x10]         ; arg1 = item pointer
0x00555FD7  test edi, edi
0x00555FD9  je 0x555fff                 ; null item -> return 0 (no-op)
0x00555FDB  mov edx, [ecx+0x10]         ; count
0x00555FE0  test edx, edx
0x00555FE2  jle 0x555fff                ; empty repo -> return 0
0x00555FE4  mov esi, [edi+0x14]         ; esi = item's OWN object id
0x00555FE7  mov edx, [ecx+0xc]          ; items_list
        ; loop: cmp esi, [edx]; found -> break; else edx+=4, i++, loop while i<count
0x00555FF9..0x00555FFD                  ; loop exhausted -> return 0 (safe no-op)
0x00556007  ; found at index eax: shift the tail down by one slot, count--
0x00556028  mov al, [edi+0x288]         ; item's flags byte
0x0055602E  test al, al
0x00556030  jns 0x55603f                ; bit 7 (NewItem) clear -> skip
0x00556032  dec dword ptr [ecx+4]       ; else: repo's "new item" counter--
0x00556035  and dword ptr [edi+0x288], 0xffffff7f  ; clear NewItem bit
0x0055603F  mov eax, 1                  ; return 1 (removed)
0x00556047  ret 4
```

**Confirms**: `RemoveItem` takes the item's own POINTER (not an id), resolves the id itself
(`item+0x14`), is a **safe no-op** (returns 0, touches nothing) when the item is not present in
this repository, and its only side effect beyond unlinking is clearing the NewItem flag bit —
no GUI call, no message dispatch, no notification. This is exactly the minimal "unlink"
primitive the container-sync design philosophy prefers (§2.3 below).

`CSWSPlaceable::RemoveItem` `0x00584AC0` (Odyssey.Items.cs's existing `PlaceableRemoveItemFn`,
already used by container-loot) is now confirmed, by fresh disassembly this pass, to be a thin
wrapper around the SAME primitive:

```
0x00584AC0  mov edi, [esp+0xc]              ; item pointer
0x00584AC8  mov ecx, [esi+0x36c]            ; this placeable's repository
0x00584AD0  push edi
0x00584AD1  call 0x555f40                   ; GetItemInRepository(repo, item, recurse=0)
0x00584AD6  cmp eax, 1
0x00584AD9  jne 0x584b06                    ; not a member -> return 0
0x00584ADB  mov ecx, [esi+0x36c]
0x00584AE1  push edi
0x00584AE2  call 0x555fd0                   ; CItemRepository::RemoveItem(repo, item) <-- SAME FN
0x00584AE7  push 0x7f000000                 ; OBJECT_INVALID
0x00584AEC  push 1
0x00584AEE  push 1
0x00584AF0  push 0x7f000000                 ; OBJECT_INVALID
0x00584AF5  mov ecx, edi
0x00584AF7  call 0x553210                   ; CSWSItem::SetPossessor(item, OBJECT_INVALID, 1, 1, OBJECT_INVALID)
0x00584AFD  mov eax, 1
0x00584B03  ret 4
```

**VERIFIED**, this pass. This is the exact evidence that a placeable's own "take an item out"
path and this feature's "take an item out of the party pool" path are the SAME underlying
primitive, just reached through a different owning object (a `CSWSPlaceable` vs. a bare
`CItemRepository*` this feature resolves directly, since the party pool has no owning
placeable).

`InsertItem` (`0x0055D2C0`, VERIFIED disasm): a valid index (`0 <= index < count`) overwrites
that slot with the item's own id (`item+0x14`); any OUT-OF-RANGE index (this feature always
passes `-1`) takes the append path — read the item's own id, grow the backing array through
`CExoArrayList::Allocate 0x005E03C0` if `count == capacity` (double it, or start at 16), write
`items_list[count] = id`, `count++`. No message dispatch, no GUI call, no gold/Pazaak
special-casing — the minimal "link this item into the array" step.

### 1.4 `CItemRepository::AddItem` — the heavier primitive, deliberately NOT used (VERIFIED, partial)

`AddItem 0x0055D330` is the FULL "give item to this repository" engine primitive, and its
early control flow was disassembled this pass:

1. Resolves the repository's OWNER (`repo+0x00`, via `GetGameObject`).
2. If the item's base-item type is `0x17` ("credits"/a gold chit): calls
   `CSWSCreature::AddGold(owner, item->StackSize, 1)` (`0x004F3DB0`, the same `AddGold`
   `docs/research/progression-sync.md` §1 already documents) and DELETES the item — a credit
   chit deposited becomes gold, never a discrete stash entry.
3. If the item's base-item type is `0x2A` (a Pazaak card): calls
   `CSWSCreature::AddPazaakCard(owner, cardId)` (`0x004EFB40`) and likewise consumes the item.
4. Otherwise, falls through to a tail this pass did **not** fully trace: it begins building
   `CExoLocString` state and dispatching a client/server GUI message (the same local,
   no-network message bus `docs/research/container-sync.md` §1.1/§1.4 already documents for
   `OpenInventory`) before (as far as the traced prefix shows) linking the item in — the same
   job `InsertItem` does directly.

**Why this feature calls `InsertItem` instead of `AddItem`.** `AddItem`'s gold/Pazaak
special-casing is arguably *correct and desirable* behaviour (a deposited credit chit really
should become gold, matching what dragging one into the party stash would do locally) —
but its GUI-message tail was not traced far enough to trust unattended, and
`docs/research/container-sync.md`'s own precedent is explicit on this exact tradeoff: *"only
the field write and the animation call are replicated... would be wrong to invoke [the heavy
function] on a body nobody local is actually using."* The same reasoning applies here: this
build reuses `InsertItem`, the narrow, fully-traced, no-GUI, no-special-case primitive, and
resolves the party's repo through the engine's own lazy-construct accessor instead
(`CSWPartyTable::GetPartyItems`, next).

**INFERRED / open**: whether a peer's credit-chit deposit reaching a receiver as a discrete
stash ITEM (via `InsertItem`) rather than GOLD (via `AddItem`'s special case) is a real,
player-visible gap. Flagged as a known limitation rather than fixed this pass — see §5.

### 1.5 `CSWPartyTable::GetPartyItems` — the lazy accessor (VERIFIED, full disasm)

```
0x00563340  mov eax, [edi+0x118]        ; edi = this (partyTable)
0x00563359  test eax, eax
0x00563361  jne 0x5633a2                ; already constructed -> return it
        ; else: allocate 0x18 bytes, call CServerExoApp::GetPlayerCreatureId(1) [0x004AEA40]
        ; for the parent id, construct the repository (0x0055D290), store at +0x118
0x005633A2  mov eax, [edi+0x118]
0x005633B7  ret                          ; __thiscall(this), NO stack args
```

**VERIFIED**: on a save where nobody has ever used the party stash, `partyTable+0x118` is
NULL — the repository is constructed on first access, and its "owner" (`repo+0x00`) is
whatever `CServerExoApp::GetPlayerCreatureId(1)` (`0x004AEA40`) resolves, i.e. the current
player character. This is why the plugin's write path calls the real accessor
(`GetPartyItemsFn`, guaranteeing construction) rather than reading `+0x118` directly the way
the read-only agent side has to (it cannot make engine calls at all).

### 1.6 `CSWSItem::SetPossessor` (VERIFIED shape, partial trace)

`0x00553210`, `__thiscall`, four stack args (symbol DB: 16 bytes). The one live call site this
pass found (`CSWSPlaceable::RemoveItem`, §1.3) passes
`(item, OBJECT_INVALID, 1, 1, OBJECT_INVALID)`. Partial disassembly shows it compares the
item's current possessor (`item+0x268`) against the new value and, on a genuine change, builds
and dispatches ANOTHER client/server GUI message — the same local message-bus family
`OpenInventory`/`CloseInventory` already use (see `docs/research/container-sync.md`). This
feature reuses the exact literal argument shape the engine's own removal path uses rather than
guessing new ones (see §2.3's removal design) and does **not** call it on the ADD path (§2.3).

### 1.7 Why no VM (NWScript) routine can reach the pool (INFERRED, argued from evidence)

`CheatController.GiveLoot`/`Odyssey.GiveItemToInventory` already give the base game's own
`CreateItemOnObject(resref, target, stack)` VM routine — the same call chain
`docs/research/enemy-materialisation.md` §6 documents. `CreateItemOnObject`'s target is a
scriptable game OBJECT id; the party pool is not a `CGameObject` at all — it is embedded data
on `CSWPartyTable`, which has no object id of its own (confirmed by the absence of any object-
id field on the class in the symbol DB, and by `GetPartyItems`'s own signature taking no
target argument). **So the task brief's "check whether `giveloot`'s give path can target the
party pool" is answered NO** — there is no VM path to it, which is why §2.3's design spawns
into the local player's own inventory first and then transfers with the raw repository
primitives, rather than trying to hand `CreateItemOnObject` a pool "object" that does not
exist.

---

## 2. Design

### 2.1 Why monotonic merge is wrong here, and what replaces it

Container loot (`ContainerLootStore`) is a SET of removed `(container, item)` pairs — safe to
re-declare, because both instances loaded the same save and share the item's engine object id
(GIT-placed). A party-pool item has NO such shared id: each instance spawns its own separate
`CSWSItem` for the SAME logical stack, on its own engine, at its own address — the ids
genuinely differ side to side. Monotonic-only-grows-or-only-shrinks reasoning (the door/
placeable "open" flag model) is also wrong: a stash entry can be added AND removed, repeatedly,
by either player, in any order — this needs true add/remove semantics, not a one-way ratchet.

**The design is a content-keyed multiset**, not an object-id-keyed set:

- **Key** (`PartyPoolStackKey`): resref + the four non-quantity `ItemIdentity` fields
  (charges, max charges, upgrade mask, the identified/stolen/non-equippable flag bits) — i.e.
  everything that makes two items of the same blueprint genuinely different stacks (a plain
  vibroblade and a stolen, upgraded one are different keys). Two engine items with the SAME key
  are, by construction, indistinguishable to a peer, matching how the engine's own
  `CSWSItem::MergeItem` (`0x00553F90`, `docs/research/item-identity.md`) already merges
  identical stacks locally.
- **Quantity**: an aggregate int per key, mutated by ADD (additive), REMOVE (additive,
  subtracted, floored at zero) and — resync only — SET (absolute).
- **Op log**: `PartyPoolStore`, the same op-sequence-dedup / `Version`+`Digest` tripwire shape
  every world store in this codebase uses (`ContainerLootStore`, `GroundItemStore`), reliably
  delivered by `ReliablePartyPoolSender` (the `ReliableLootSender` contract — refuse past a
  cap, retransmit with backoff, never abandon an unacked op — minus its in-flight content
  dedup, which would be WRONG here: two adds of the same key are two distinct, cumulative
  facts, never collapsible the way a container removal is).

### 2.2 The echo problem, and why it needed a NEW mechanism (not `ContainerLootStore`'s)

`ContainerLootStore` suppresses an echo for free: its underlying operation is `_set.Add(pair)`,
which is naturally idempotent (adding the same fact twice is a no-op) REGARDLESS of which op
(sequence number, or even which host-relay re-attribution) asked for it. An aggregate COUNT has
no such free idempotency — applying "add 5" twice really is 10. This pass needed two distinct,
new anti-echo mechanisms, one at the wire layer and one at the engine layer:

**Wire layer** (`CoopHost.HandlePartyPoolOp`/`CoopClient.HandlePartyPoolOp`). A client's own
detected change is sent to the host, which applies it to its own store and RELAYS it to every
connected peer (including, harmlessly, back to the client's own address — the host does not
special-case its own broadcast recipient list). Unlike `HandleContainerLoot`'s relay, which
re-attributes the op to the host's own peer id (safe there, since dedup is by content), this
relay keeps the ORIGINAL contributor's peer id and only re-mints the sequence number (needed
because `ReliablePartyPoolSender`'s own pending table is keyed by a bare `uint` sequence, and
forwarding the client's own sequence risks colliding with the host's unrelated in-flight ops in
that same table — the identical reason `HandleContainerLoot` re-sequences). The originating
client's own `HandlePartyPoolOp` then recognises `op.OriginPeerId == _localPeerId` and skips
re-applying it to its OWN store (still acking, since the host's sender is waiting for that
specific peer's ack regardless of origin) — see `CoopClient.PartyPool.cs`.

**Engine layer** (`Pumps/PartyPoolPump.cs`). Detecting a local player's own deposit/withdrawal
means diffing a fresh engine scan against the LAST scan. But the moment this instance's own
queued apply (for a PEER's change) lands on the engine, the very next scan would see the
engine's own total "jump" — indistinguishable, by a bare before/after diff, from a real local
action — and would re-publish the pump's own application right back onto the wire, forever.
The pump therefore tracks, per key, the engine total an in-flight apply is expected to
PRODUCE (`_pendingApplyTarget`) and excludes that key from local-change detection until a
fresh scan confirms the value landed. See the class's own doc comment for the full two-step
(detect, then reconcile) tick shape and why the two never race each other.

### 2.3 Removal: reuse `CItemRepository::RemoveItem` directly (VERIFIED primitive, §1.3)

`Odyssey.RemovePartyPoolStack` resolves the pool's repository, snapshots its current item id
array (removal mutates the array in place, so a live walk would skip/revisit entries),
and for each snapshot id: resolves the item, matches its Tag against the requested resref
(case-insensitive, cut at the first null — the project's own CExoString length trap) and its
non-stack identity fields against the requested key, and — greedily, until the requested count
is satisfied — either shrinks a partially-consumed stack's `StackSize` in place or, for a
fully-consumed stack, calls `CItemRepository::RemoveItem` (unlink) followed by
`CSWSItem::SetPossessor(item, OBJECT_INVALID, 1, 1, OBJECT_INVALID)` — the EXACT literal call
`CSWSPlaceable::RemoveItem` makes (§1.3), reused verbatim rather than guessed. The item becomes
an inert orphan on this instance afterward, the same fate `docs/research/container-sync.md`'s
own removal design already gives a picked-up container item.

`RemoveItem` is a safe no-op when the target is not present (§1.3's disasm) and this codebase's
resolve helpers (`Odyssey.ResolveItemBase`, reused unchanged) fail safe on a stale/freed
pointer — so a mismatched or already-gone target costs a "removed 0 of N, refused" answer,
never a crash or a wrong write.

### 2.4 Addition: spawn into the player's own inventory, then transfer (NEEDS-LIVE)

No VM routine can target the pool (§1.7), so `Odyssey.AddItemToPartyPool`:

1. Spawns the FULL requested stack directly into the local player's own backpack via
   `GiveItemToInventory`/`CreateItemOnObject` (the SAME verified VM routine
   `docs/research/enemy-materialisation.md` §6's give-loot channel already uses) — one call
   creates the whole stack size at once, so there is never a floor object and never a
   duplicate visible body.
2. Applies the requested identity via the EXISTING `Odyssey.ApplyItemIdentity` (unchanged —
   the same five-field write the ground-item channel already ships).
3. Resolves the player's own backpack repository (`creature+0xA30`,
   `docs/research/item-identity.md`'s "creature backpack" offset) and the pool's repository
   (via `CSWPartyTable::GetPartyItems`, guaranteeing it exists — §1.5), then unlinks the item
   from the backpack (`CItemRepository::RemoveItem`) and links it into the pool
   (`CItemRepository::InsertItem`, index `-1` = append).

The item's possessor field (`+0x268`) is deliberately left exactly as `CreateItemOnObject` set
it (the local player's own id) rather than cleared to `OBJECT_INVALID` — `GetPartyItems`'s own
constructor call establishes that the pool's "owner" IS the current player creature (§1.5), so
leaving the field alone matches that invariant instead of fighting it. **NEEDS-LIVE**: the
possessor semantics of a genuine hand-deposited stash item (what does the retail GUI actually
leave it as?) were never independently observed this pass — this is a reasoned choice, not a
measured one.

### 2.5 Resync on join: `Set`, not replayed history

`ResyncPartyPool` (called from `HandleHello` on (re-)admission, and from
`HandlePartyPoolSyncRequest` for a still-connected peer whose digest disagrees — mirroring
`ResyncContainerLoot`'s two call sites exactly) re-offers every stack the host's store holds as
an ABSOLUTE `PartyPoolOpKind.Set` fact under the host's OWN id, never as replayed `Add` history:
there is no per-contributor ledger to replay (the store only ever tracks aggregate totals), and
a peer being resynced may already hold a DIFFERENT nonzero total for a key — replaying "Add N"
onto an unknown existing total would double-count it. `Set` is idempotent by construction
(re-setting to the same value is a no-op, no version bump), so a redundant resync costs nothing.

---

## 3. What was built

**Protocol** (`KotorCoop.Protocol`): `Packets/PartyPoolPackets.cs` (`PartyPoolStackKey`,
`PartyPoolOpKind{Add,Remove,Set}`, `PartyPoolOpPacket`, `PartyPoolAckPacket`,
`PartyPoolSyncRequestPacket`), `PacketCodec.PartyPool.cs`, `PartyPoolDiffRule.cs` (the pure
detect/reconcile diff — see docs/ARCHITECTURE.md's "plugin compile-include trick": it is
dependency-free like every other pure rule here, though nothing currently pulls it into the
plugin build since the plugin never runs the diff itself). `PacketType.cs` gained
`PartyPoolOp = 51`/`PartyPoolAck = 52`/`PartyPoolSyncRequest = 53`; `ProtocolConstants.cs`
gained a `TODO(version): bump` changelog entry (the actual `Version` constant is intentionally
untouched — owned elsewhere per this pass's own instructions) and `MaxPartyPoolStacks = 256`.
`SessionCapabilities.PartyPool = 1 << 11` (content-keyed, so NOT part of
`ContentCompatibility.ObjectIdKeyed` — a save/module mismatch never touches it).

**Session** (`KotorCoop.Session`): `PartyPoolStore.cs` (op dedup, `Version`/`Digest`, the
`MaxPartyPoolStacks` cap — refuses only a brand-new key, an existing one can always still
change), `ReliablePartyPoolSender.cs` (the `ReliableLootSender` contract, minus in-flight
content dedup), `IPartyPoolSession.cs` (added to `ICoopSession`'s composite list),
`CoopHost.PartyPool.cs`/`CoopClient.PartyPool.cs` (publish/apply/relay/resync/ack, wired into
each class's handler dictionary, `Tick` broadcast/send loop, and `HandleHello`'s
freshly-admitted resync block), `Pumps/PartyPoolPump.cs` (the two-step detect/reconcile tick —
see its own doc comment for the full mechanism), `EntryDigest.PartyPoolEntry` (the digest
contribution function). `AgentSettings.PartyPoolSyncEnabled`, `CoopConfig.PartyPoolSyncEnabled`
+ `party_pool_sync` key parsing, `CoopAgent.cs` wiring (`_partyPoolPump`, gated call in the
heavy pump pass, `PendingPartyPoolApply` public nested type + `TryDequeuePendingPartyPoolApply`
— mirrors `PendingMaterialisedLootGive`/`TryDequeuePendingLoot` exactly, since
`KotorCoop.Agent` is a separate assembly and needs a PUBLIC type to see across it).

**Memory** (`KotorCoop.Memory`): `IPartyPoolBridge.cs` (added to `IGameBridge`'s composite),
`KotorGameBridge.PartyPool.cs` (the read side — pure `ReadProcessMemory`, throttled 1 Hz like
`ContainerLoot`'s own sweep, reusing its `RepositoryItemArray`/`RepositoryItemCount` constants
and the existing item-tag/identity cache built for ground items). `IGameBridge.CheatKinds`
gained `PartyPoolAdd = 25` / `PartyPoolRemove = 26` — no `SharedBlock` layout change, no
version bump: both reuse the EXACT existing staging-cheat fields `SetItem`/`GiveLoot` already
carry (`Text` = resref, `Amount` = count, `Charges`/`MaxCharges`/`Upgrades`/`ItemFlags` = the
rest of the stack identity).

**Agent** (`KotorCoop.Agent`): `CoopCommandDispatcher.PartyPool.cs`'s `DrainAutomaticPartyPool`
— the same single-slot arbitrated cheat-channel drain `DrainAutomaticLoot` already established,
wired into the command tick loop right after it.

**Plugin** (`KotorCoop.Plugin`, x86 NativeAOT): `SharedBlock.CheatKindPartyPoolAdd = 25` /
`CheatKindPartyPoolRemove = 26` (SharedBlock.cs, no version bump — same reasoning as
`CheatKindGiveLoot`'s own comment: every field these two use already exists).
`CheatController.PartyPool.cs` (`PartyPoolAdd`/`PartyPoolRemove` handlers, wired into
`CheatController.cs`'s dispatch switch), `Odyssey.PartyPool.cs` (the engine-call layer: the
pointer chain, the three new delegates — `GetPartyItemsFn`, `RepositoryRemoveItemFn`,
`RepositoryInsertItemFn`, `SetPossessorFn` — the Tag-match/stack-identity-match helpers, and
`AddItemToPartyPool`/`RemovePartyPoolStack`).

**Tests**: `PartyPoolTests.cs` (store add/remove/Set-is-absolute/distinct-identities-never-
merge/cap/digest-order-invariance; sender enqueue/no-in-flight-dedup/ack-retirement/no-
recipients-waits/cap/retransmit-backoff; `PartyPoolDiffRule` no-change/new-key/vanished-key/
stack-size-increase/decrease/echo-suppression/multiple-independent-keys — the "add, remove,
stack change, echo suppression, cap" cases the task asked the pure rule to cover). Codec
round-trip (Add/Remove/Set/Ack/SyncRequest, plus a malformed-zero-delta-non-Set rejection) in
`PacketCodecTests.cs`. `PacketHandlerRegistryTests.cs`'s handler-count pins updated for the
three new registrations on each side. `CheatKindSyncTests` (pre-existing, unmodified) already
covers the two new plugin/agent cheat-kind pairs by construction.

---

## 4. VERIFIED / INFERRED / NEEDS-LIVE summary

| Item | Status |
| --- | --- |
| `partyTable+0x118` -> `CItemRepository*`, `items_list`/`item_count` at `+0xC`/`+0x10` | VERIFIED (symbol DB + disasm, this pass) |
| `CItemRepository::RemoveItem` 0x00555FD0, full control flow, safe-no-op-if-absent | VERIFIED (fresh disasm, this pass) |
| `CItemRepository::InsertItem` 0x0055D2C0, full control flow, append-on-invalid-index | VERIFIED (fresh disasm, this pass) |
| `CSWSPlaceable::RemoveItem` 0x00584AC0 calls the SAME `RemoveItem` primitive, then `SetPossessor` | VERIFIED (fresh disasm, this pass) — new evidence for a call this codebase already used |
| `CItemRepository::AddItem` 0x0055D330's gold/Pazaak special-case and array-link tail | VERIFIED for the special-case branches; the array-link tail itself was NOT fully traced (deliberately not used — see §1.4/§2.3) |
| `CSWPartyTable::GetPartyItems` 0x00563340, lazy construction, owner = `GetPlayerCreatureId(1)` | VERIFIED (full disasm, this pass) |
| `CSWSItem::SetPossessor` 0x00553210's exact ABI and the one live call-site's argument shape | VERIFIED (symbol DB shape + one disassembled call site); the two middle arguments' individual meaning is INFERRED, not independently traced |
| No VM routine can target the pool directly | INFERRED (argued from the object model: the pool has no object id) |
| The design (content-keyed multiset, op log, two-layer echo suppression) | Implemented, unit-tested |
| Reading the pool externally (`KotorGameBridge.PartyPool.cs`) | Implemented; NEEDS-LIVE (never run against a real save) |
| Applying an add (`Odyssey.AddItemToPartyPool`) | Implemented; **NEEDS-LIVE** — the riskiest half, see §2.4 |
| Applying a remove (`Odyssey.RemovePartyPoolStack`) | Implemented; NEEDS-LIVE, but built entirely from primitives independently verified in §1.3 |
| End-to-end wire sync (host<->client, both directions, reconnect resync) | Implemented, unit-tested at the store/sender/codec layer; NEEDS-LIVE for the full loop against a real game |

---

## 5. Open risks

1. **The GUI-message tails of `AddItem`/`SetPossessor` were not fully traced** (§1.4/§1.6).
   Using the narrower `InsertItem`/`RemoveItem` primitives sidesteps re-entering that code, but
   it also means this feature does NOT know whether retail's own "item added to party
   inventory" notification fires for a peer's deposit, or whether the stash GUI panel (if a
   player has it open) repaints live. Likely cosmetic if wrong (docs/research/progression-sync.md
   §1's "NEEDS-LIVE: does the credits readout refresh after a bare field write" is the closest
   precedent, and that one turned out to be fine) — genuinely unmeasured.
2. **A peer's credit-chit / Pazaak-card deposit becomes a discrete stash item, not gold/a card**
   (§1.4) — `AddItem`'s special-casing is bypassed on purpose. If this proves player-visible,
   the fix is narrow: special-case those two base-item types in `CheatController.PartyPoolAdd`
   before calling `Odyssey.AddItemToPartyPool`, mirroring what `AddItem` already does.
3. **Possessor semantics on addition** (§2.4) — left unset (inherits the spawning player's id)
   on a reasoned-not-measured basis.
4. **A mid-session reconnect's cold-start baseline.** `PartyPoolPump`'s local-detection
   baseline is silently seeded from the FIRST scan after (re)connect — correct for a fresh
   session, but if a client disconnects and reconnects mid-session, its own bridge scan will
   silently re-baseline against whatever the local engine already holds rather than
   re-detecting it as new. The cross-instance STORE state is unaffected (it survives a
   reconnect; only `ForgetSession`-cleared lease-like state is dropped, and the party pool is
   deliberately not lease-like — see `CoopClient.Connection.cs`'s `ForgetSession`, left
   untouched by this pass). Bounded, not exercised live.
5. **`MaxPartyPoolStacks` (256) refusing a genuinely new key** once the cap is hit mid-session
   only logs, it does not surface to the player — matches every other reliable sender's
   cap-refusal behaviour in this codebase, not a regression, but worth naming.

---

## 6. Live test plan (NOT yet run)

Two `tools/demo.ps1` instances, both loaded into the same save/module.

1. **Baseline read.** Enable `party_pool_sync = 1` in both `coop.ini`s, connect, and watch the
   agent log for `party pool:` lines. On a fresh save (nobody has ever used the stash) both
   sides should read an empty pool with no spurious detection — confirms
   `KotorGameBridge.PartyPool.cs`'s null-repository path (§1.5) is read correctly.
2. **Host deposits.** Drag an item into the party stash panel on the HOST. Expect the agent log
   to show a local detection (`party pool: local add of Nx '<resref>' detected, publishing`)
   and, on the CLIENT, a reconcile line as `PartyPoolPump` applies it — then confirm live, via
   the client's OWN stash panel (or the `items`/`inspectitem` console verbs against the pool's
   repository), that the SAME item genuinely appears there.
3. **Client deposits**, reverse roles — confirms symmetry (no host/client branch anywhere in
   this channel).
4. **Client withdraws** an item the host deposited — confirms `RemovePartyPoolStack`'s
   Tag+identity matching against a REAL engine item, and that the host's own copy converges.
5. **Stack change**: deposit 5 of an item, then deposit 3 more of the identical stack — confirm
   the peer's total becomes 8 as ONE converged stack, not two separate entries (the content-key
   aggregation, §2.1).
6. **A credit chit / Pazaak card**, deposited — confirms or refutes open risk #2.
7. **Reconnect**: disconnect one client mid-session (with the pool non-empty), reconnect it —
   confirm `ResyncPartyPool`'s `Set`-based full resync converges it without doubling anything
   already correct locally (open risk #4's live check).
8. **Cap**: (bench-only, not worth a live pass) already covered by
   `PartyPoolTests.ANewKeyIsRefusedPastTheCapButAnExistingKeyStillUpdates`.

Watch for in the plugin log: `party pool: added/removed '<resref>'...` lines from
`CheatController.PartyPool.cs`, and any `party pool: ... failed — <LastPartyPoolNote>` refusal,
which names exactly which of §1's guards fired.

## 7. Live-verified 2026-08-27 — one bug fixed, one open; stays OFF

Two-instance rig (`tools/demo.ps1`), `party_pool_sync=1` on both `coop.ini`s. Two new console
verbs, `pooladd <resref> [stack]`/`poolremove <resref> [stack]`, were built to reach this test
at all — they submit through the exact same `CheatKinds.PartyPoolAdd`/`PartyPoolRemove`
channel `DrainAutomaticPartyPool` already used for the automatic apply half, just from a human
console line instead — see `docs/CONSOLE.md`.

**Baseline read (test 1): PASS.** On connect, both instances' pools independently reconciled
to the save's own pre-existing contents identically (`removed 'g_i_adrnaline003' x1`,
`removed 'g_i_medeqpmnt01' x2`, `removed 'g_w_blstrrfl001' x1` — a leftover from earlier
manual testing on this save, not a fresh-pool case, but both sides converging to the SAME
result either way is the property this test checks).

**Host deposits, client reconciles (test 2): a real bug, found and fixed.** `pooladd
g_w_thermldet01 3` on the host: the client's plugin log showed the deposit applied **twice**
(`party pool: added 'g_w_thermldet01' x3` at two distinct timestamps ~18 s apart) — a real
total left double what was actually deposited, not a cosmetic log duplicate. Root cause and
fix: see the `PartyPoolPump.cs` commit message (an order-of-operations bug in the pump's own
landed-apply/local-detection interaction — a just-landed apply was misdetected as a fresh
local action on the very tick it landed, and republished). **Re-tested after the fix**: a
fresh `pooladd g_w_thermldet01 3` on the host produced exactly ONE `added x3` line on the
client, confirmed by grepping the full plugin log across a 30 s window (previously two).

**Client deposits, host reconciles (test 3): a SECOND, separate bug, NOT fixed.** Symmetric
test, `pooladd g_i_medeqpmnt01 4` on the client (after the fix above): the HOST's own log
showed the deposit applied twice, ~3.3 s apart, immediately after the client's own single,
correct apply completed. This is a DIFFERENT mechanism from test 2's bug — my fix specifically
protects a receiving instance's OWN just-landed apply from self-detection, but the depositing
side (the instance that originates a change via a direct console cheat, bypassing
`PartyPoolPump`'s own queue entirely) is not covered by that fix, and re-tracing the logic did
not find an obvious same-tick ordering error there. The leading (**unconfirmed**) hypothesis
is `KotorGameBridge.PartyPool.cs`'s own `PartyPoolScanIntervalMs = 1000` throttled scan cache
interacting with `PartyPoolPump`'s per-tick landed/pending tracking — a `TryScanPartyPoolTotals`
call can return a stale, up-to-1s-old snapshot rather than the true current engine state, which
the pump's tick-by-tick bookkeeping does not account for. Not fixed this round: the exact
interaction was not pinned down with confidence, and shipping a second blind fix under the
same time pressure that produced the first one felt like the wrong trade. Both applies WERE
bounded (exactly two, not runaway) in every observed case — this is a double-count, not an
infinite-growth bug.

**Verdict: `party_pool_sync` stays OFF.** One genuine bug was found and fixed live (test 2's
scenario is now clean); a second, distinct bug remains open (test 3's scenario still
double-counts). Tests 4-7 of the plan above (client withdraws, stack aggregation, credit
chit/Pazaak card, reconnect) were not reached this round — the double-count bug takes priority
over exercising further scenarios on top of it.

**Follow-up work, for whoever picks this back up**: (1) pin down and fix the depositing-side
double-apply (likely the scan-cache interaction above); (2) `PartyPoolPump` has no direct unit
test coverage at all (confirmed while investigating this round — every existing party-pool
test either exercises `PartyPoolDiffRule` in isolation or a real loopback session, neither of
which drives `PartyPoolPump.Pump()` itself tick-by-tick), so neither bug found this round would
have been caught by `dotnet test`; a regression test using a fake `IGameBridge`/`ICoopSession`
through `CoopAgent.PumpOnce()` (the same shape `CompanionStateSyncTests.cs` already uses) is
worth adding once both bugs are closed, so a third round doesn't rediscover them by hand again.

---

## 8. 2026-08-27 addendum, round 2 — root cause 2 investigated, still open (no rig)

This pass picked up exactly where §7 left off: root cause 1 (the ordering bug above) is
confirmed fixed in `PartyPoolPump.cs` and stayed untouched here — no re-fix was needed or
made. This addendum instead targets root cause 2, the depositing-side duplicate §7 left open,
and closes §7's other follow-up item (`PartyPoolPump` had no direct unit test coverage) along
the way.

⚠ **Worktree-base note, for whoever reads this next.** This pass's isolation worktree had
branched from `main` BEFORE §7's fixes (root cause 1, the item-tag cache fix, the
`pooladd`/`poolremove` verbs) had landed — see
`kotor-coop-worktree-base-and-shared-checkout.md`: an isolation worktree's base can be well
behind `main` even mid-round. The investigation below re-derived and re-applied root cause 1's
fix by hand before realising the branch was stale, then merged `main` in directly (bringing the
real fix, the real dev verbs, and §7 itself) once that became clear. The merge is clean;
nothing here duplicates or reverts §7.

**`tests/KotorCoop.Tests/PartyPoolLoopbackTests.cs`** is the unit coverage §7's follow-up work
item 2 asked for: a real host, real client, real sockets, real `CoopAgent` pump loop on each
side (a fake `IGameBridge` stands in for the actual game process — see the file's own doc
comments for exactly what it reproduces and why), driving `PartyPoolPump.Pump()` tick-by-tick
through `CoopAgent.PumpOnce()` rather than exercising the store/sender in isolation the way
every pre-existing party-pool test does. Run against a DELIBERATELY REVERTED copy of root cause
1's fix (the pre-`PartyPoolPump.cs`-fix ordering), all four tests fail with a duplicated total
on the RECEIVING side (`host=5, client=10`, `a deposit doubled after first converging`, etc.) —
matching §7 test 2's live symptom exactly. Against the current (fixed) ordering, all four pass.
This is a real failing-then-passing regression pin for root cause 1, on top of `main`'s own
fix — the first this channel has had at the `PartyPoolPump` level.

**Root cause 2 — the depositing-side duplicate (§7 test 3) — was investigated this round and
NOT reproduced**, despite deliberately targeting the leading (self-described "unconfirmed")
hypothesis from the live round that found it: `KotorGameBridge.PartyPool.cs`'s
`PartyPoolScanIntervalMs = 1000` scan-result cache interacting with `PartyPoolPump`'s per-tick
`_lastScan`/`_pendingApplyTarget` bookkeeping. `PartyPoolLoopbackTests.cs`'s `FakeBridge`
reproduces that cache mechanism line-for-line (same "throttled call returns the same stale
snapshot" shape as the real `TryScanPartyPoolTotals`, just on a test-controlled interval), and
each deposit test's `AgentLoop` additionally simulates the ONE mechanism a normal GUI-dragged
deposit never goes through but `pooladd`'s console-cheat deposit does — a single-slot,
non-instantaneous cheat round trip (`_pendingCheatSerial`'s real gate: at most one apply in
flight, landing after a randomised tens-to-low-hundreds-of-ms delay, deliberately uncorrelated
with both the bridge cache and the heavy-pump cadence). Configurations tried, all clean (no
duplicate observed):

- The compressed/stress config used in the permanent suite: 150 ms bridge cache,
  `HeavyPumpIntervalMs = 0` (the pump runs on every tick — far more pump ticks per cache
  window than production ever produces), held for 8 s after first convergence.
- The exact production ratio — both the bridge cache and `HeavyPumpIntervalMs` at the real
  1000 ms, independent uncorrelated epochs exactly like the real bridge and the real heavy-pump
  gate — sampled continuously for 25 s after depositing. (This was a throwaway experiment,
  `PartyPoolExperimentTests.cs`, run and then deleted rather than kept in the suite, since it
  duplicates the permanent tests' shape once the config is a parameter — the interesting
  finding is the negative result, recorded here.)
- A late/lost ack forcing several retransmit-backoff rounds before the depositing peer's op is
  finally acknowledged (`ADepositSurvivesALateHostAckWithoutDoublingEitherSide`).
- A full blackout-and-reconnect after a deposit, exercising `ResyncPartyPool`'s `Set`-based
  resync on top of an already-converged stack (`AReconnectAfterADepositDoesNotDoubleIt`).

None doubled, on either side, in any of these. That is reasonably strong evidence AGAINST the
bridge-scan-cache hypothesis specifically — the mechanism was reproduced faithfully and stress
tested well past production's own timing ratios without producing the symptom. It does not
prove root cause 2 does not exist; it narrows where it must live. Everything on the
C#-testable side of this channel — `PartyPoolPump`, `PartyPoolStore`, `ReliablePartyPoolSender`,
`CoopHost.PartyPool.cs`/`CoopClient.PartyPool.cs`'s wire/echo logic, and
`KotorGameBridge.PartyPool.cs`'s own caching — was exercised under real concurrency (real
background threads, real sockets, real timers) and held up. What was **not**, and could not be
without a live rig, exercised here: the native plugin round trip itself
(`CheatController.PartyPoolAdd`/`Odyssey.PartyPool.cs`'s `AddItemToPartyPool` spawn-then-
transfer) and the `SendCheat`/shared-block staging-cheat ACK protocol
(`CoopCommandDispatcher.cs`'s `SendCheat`/`ReflectCheatAnswer`/`_pendingCheatSerial`) under
genuine game-frame timing — the one difference between a `pooladd` console deposit and an
ordinary GUI drag this round could reason about but not run. That is the next place to look,
and it needs a rig: watch whether the HOST's plugin log shows `CheatKinds.PartyPoolAdd` (or the
reconcile-driven automatic apply) processed twice for what was, on the wire, a single relayed
op — if so, the duplicate is native-side or in the cheat-ack protocol, not in anything this
addendum's tests cover.

**`party_pool_sync` stays OFF** (`AgentSettings.PartyPoolSyncEnabled` defaults false; nothing
in this pass changed that default) — root cause 2 is still open.

**Updated live test plan, on top of §6/§7 above:**

1. Re-run §7 test 3 (client deposits via `pooladd`, watch the HOST's plugin log) specifically
   watching timestamps on every `party pool: added/removed '<resref>'` line the HOST logs, and
   cross-reference against the HOST's own network log for how many DISTINCT `PartyPoolOp`
   packets it received/relayed for that key in the same window — if the wire only ever carried
   one op but the plugin log shows two applies, the bug is in the native round trip; if the
   wire itself carried the op (or the automatic-apply queue drained) twice, that is now a
   further C#-testable lead this pass did not find a way to trigger, and is worth a regression
   test once identified. Everything else in §6 (tests 4-8) is still bench-only/not yet run.

## 9. Live pass (2026-08-28) — the depositing-side duplicate reproduced directly, on the
## ORIGINATING side's own log, not the receiving side; asymmetric, not deterministic

Two-instance rig, `party_pool_sync=1` on both `coop.ini`s, fresh connect. §7's baseline-read
test (test 1) reconfirmed clean: both sides independently reconciled the save's pre-existing
pool contents identically (`removed 'g_i_adrnaline003' x1`, `removed 'g_i_medeqpmnt01' x2`,
`removed 'g_w_blstrrfl001' x1`, matching §7's own recorded baseline exactly — this save's own
pool state has not changed across sessions).

**Test 2 (host deposits `pooladd g_w_thermldet01 3`, watch the CLIENT/receiver) re-confirmed
root cause 1's fix still holds**: the client's own log shows exactly ONE `party pool: added
'g_w_thermldet01' (0x131) x3...` line. Clean, as §7's own fix predicted.

**But the HOST's OWN log — the ORIGINATING side, for its OWN direct console deposit — shows the
identical add applied TWICE**, 1.34 seconds apart, same resref, same quantity, same format
(`CheatController.PartyPool.cs`'s own `PartyPoolAdd` log line, not a cosmetic duplicate — two
separate `cheat: added ... (status 1)` replies, meaning `Odyssey.AddItemToPartyPool` genuinely
ran twice):

```
[03:47:45.466] party pool: added 'g_w_thermldet01' (0x131) x3, charges 0/0, upgrades 0x0
[03:47:45.467] cheat: added 'g_w_thermldet01' x3 to the party pool (status 1)
[03:47:46.807] party pool: added 'g_w_thermldet01' (0x131) x3, charges 0/0, upgrades 0x0
[03:47:46.808] cheat: added 'g_w_thermldet01' x3 to the party pool (status 1)
```

**This is the first direct, live reproduction of a depositing-side duplicate** — every prior
round (§7 test 3, §8's whole investigation) looked for it on the RECEIVING side after a peer's
deposit and either found it there (§7) or could not reproduce it under any synthetic
configuration (§8). This round found it on the OPPOSITE side: the instance that itself issued
the `pooladd` console command, applied through the exact same single-slot arbitrated cheat
channel (`CheatKinds.PartyPoolAdd`) both `pooladd` and the automatic per-peer apply drain share
(`docs/CONSOLE.md`'s own note: *"submit through the exact same channel `DrainAutomaticPartyPool`
already used for the automatic apply half"*). The leading, still-unconfirmed hypothesis, now
narrower than §8's own "bridge scan cache" guess (§8 stress-tested that specific mechanism and
found it innocent): `PartyPoolPump`'s own step 1 (detect a local change vs. `_lastScan`) and
step 2 (reconcile vs. `session.PartyPoolTotals`) are documented to "never fight... because step
2 always reads the store AFTER step 1's publishes have already updated it" (the class's own doc
comment) — but that invariant is stated for a GUI-dragged deposit, which the pump discovers
entirely on its own. A `pooladd` deposit is different in exactly the way §8's own closing
paragraph named: it lands through the single-slot CHEAT channel, asynchronously, on a frame the
pump's own tick does not control — if the cheat's own engine-level apply and the pump's step-1
scan land close enough together, the HOST being locally authoritative (its own `PartyPoolStore`
updates in-process, no network round trip) could plausibly let step 2 see a "session total" that
already reflects step 1's own just-published change while `_lastScan` itself has not yet
advanced past the pre-deposit baseline for that specific tick — reading as "the engine is behind
what the session knows" and queuing a SECOND apply of the same delta. **Reasoned from the
evidence and the class's own documented design, not confirmed by adding instrumentation to
`PartyPoolPump` itself** — this round prioritised getting a second real reproduction over
chasing the mechanism further, given the time already spent finding this one.

**Test 3 (client deposits `pooladd g_i_medeqpmnt01 4`, watched from both sides in the SAME
session) did NOT double, on either side** — clean single applies on both the client's own log
and the host's (receiver's) log. This is the asymmetry worth naming plainly: the exact same
mechanism, exercised twice in the same session (once for the host's own deposit, once for the
client's), doubled once and not the other time. **This reads as a genuine timing race, not a
deterministic bug** — consistent with the hypothesis above (a race between two independently-
scheduled tick cadences), and consistent with §8's own repeated stress-testing failing to
force it on demand under a controlled, compressed timing ratio.

**Not independently re-verified this round**: whether the pool's actual resulting STACK SIZE
ended up doubled (6 instead of 3) or whether the second apply was itself a no-op once the
repository already held the stack (`InsertItem`'s own append-only behaviour, §1.3, would make a
second call genuinely add a SECOND stack entry, not merge — so the honest expectation is a real
functional double-count, not just a duplicate log line, but this round did not read the pool
back through `items`/a repository dump to confirm the number directly).

**`party_pool_sync` stays OFF** (unchanged shipped default — this round only enabled it in the
live rig's own `coop.ini` for the test, per usual practice, and left the setting where it found
it). This is now the THIRD independent round narrowing the same bug (§7 found it once on the
receiving side and fixed root cause 1; §8 investigated root cause 2 and ruled out the bridge
cache; this round reproduced root cause 2 directly, on the originating side, and proposes a
concrete mechanism — the step1/step2 tick-timing race for a cheat-applied, not GUI-dragged,
local change) without yet landing a fix. **Concrete next step**: add direct instrumentation to
`PartyPoolPump.Pump()` (log `_lastScan`/`session.PartyPoolTotals`/`_pendingApplyTarget` for the
specific key on every tick around a `pooladd` call) to catch the race in the act the way
`party-reconciliation.md`'s own `party follow extend`/`phase6advance` write-logging did for a
similarly elusive live-only race — reasoning about the mechanism from the code got this round to
a strong hypothesis but not a confirmed one.

`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`:
both clean (no code changed this round). `dotnet test tests/KotorCoop.Tests --no-build`:
2339/2339 passing (unchanged).

## 10. 2026-08-28, round 4 — root cause 2 fixed: step 1's own detections had no protection
## against a transient scan disagreement; a fourth attempt still could not force the exact live
## symptom through pure timing

This pass merged §9's own fresh live reproduction (the depositing-side duplicate, on the HOST's
own log, 1.34s apart, asymmetric against test 3's clean client-originated deposit in the SAME
session) and picked up exactly where it left off: "add direct instrumentation... to catch the
race in the act" and "watch whether the HOST's plugin log shows `CheatKinds.PartyPoolAdd`...
processed twice for what was, on the wire, a single relayed op." No rig this round either — the
brief was to reproduce it in `PartyPoolLoopbackTests.cs` and fix it there.

### 10.1 What was ruled out, again, more rigorously than §8's own attempt

§8's own closing paragraph named the one thing its harness could not exercise: "the native
plugin round trip itself... under genuine game-frame timing — the one difference between a
`pooladd` console deposit and an ordinary GUI drag this round could reason about but not run."
Re-reading `PartyPoolLoopbackTests.cs`'s existing `AgentLoop` this round found a SECOND gap
alongside that one, never named before: its cheat-round-trip simulation ran INSIDE THE SAME
loop iteration as `Agent.PumpOnce()` — one thread, strict ordering, every tick. Production is
not that: `CoopAgent.RunAsync` (drives `PartyPoolPump.Pump()`) and
`CoopCommandDispatcher.RunAsync` (drives `pooladd`/`DrainAutomaticPartyPool` through the
single-slot cheat channel) are two independently-scheduled `async` loops with no shared
synchronization context (`AgentComposition.cs`'s `dispatcher.RunAsync`,
`SessionManager.cs`'s `agent.RunAsync` — separate `Task`s, not one loop calling the other). A
race between step 1/step 2 and the cheat drain was structurally impossible to observe in the
old single-loop harness, regardless of timing.

This round closed that gap: `PartyPoolLoopbackTests.cs` gained a `DispatcherLoop` — the SAME
single-slot arbitrated cheat simulation `AgentLoop` already had, but running as a genuinely
separate `Task`, polled every 150ms (matching `CoopCommandDispatcher.RunAsync`'s real cadence),
racing freely against `AgentLoop`'s own tick loop. `AgentLoop` itself gained an `autoDrain: false`
mode so it does nothing but `PumpOnce()` when a `DispatcherLoop` owns the cheat channel instead
— exactly `CoopAgent.RunAsync`'s real shape, which never drains `_pendingApplies` itself.

`HostOriginatedPoolAddThroughTheRealCheatChannelDoesNotDoubleApply` (new) drives a HOST-issued
`pooladd` through this genuinely-separated-threads rig, at PRODUCTION timing values (both the
bridge scan-cache and `HeavyPumpIntervalMs` at the real 1000ms — the exact ratio §8's own
"production ratio" experiment used) — the missing ingredient there was the single combined loop,
not the timing, so this round fixed that specifically. Run repeatedly (multiple manual passes,
both at production timing and at a compressed/stress timing matching §8's own worst-case
config): **still clean every time — no double observed.** This is now a FOURTH independent
negative result against forcing the exact live symptom (a duplicate `CheatKinds.PartyPoolAdd`
submission on the ORIGINATING side) through anything reachable from pure C# timing alone, even
with the one remaining structural gap between the test harness and production (separate threads)
finally closed. Static tracing over the same ground reached the same wall analytically: on a
`CoopHost`, `PublishPartyPoolOp` calls `PartyPoolStore.Record` SYNCHRONOUSLY (a `Lock`-protected,
same-call-stack mutation — see `PartyPoolStore.cs`), so within any SINGLE `PartyPoolPump.Pump()`
tick, step 2's read of `session.PartyPoolTotals` always already reflects step 1's own publish
from earlier in that SAME call — the two literally cannot disagree with each other on a single
thread, for a single-peer, single-key, host-only-deposits scenario, no matter how the cheat round
trip's OWN timing is shifted. The kept test is real regression coverage for this now-rigorously-
tested rig shape either way, and it stays in the suite.

**Conclusion carried forward, now on stronger evidence: whatever the live round observed is not**
**reachable by racing this codebase's own async loops against each other at ANY timing** — it
must be either genuinely native (the plugin's own frame-timing, `Odyssey.AddItemToPartyPool`'s
spawn-then-transfer sequence, or the shared-block cheat-serial handshake under real game-frame
scheduling — none of which a loopback test can drive) or something narrower this round still did
not find.

### 10.2 A real, adjacent structural gap found and fixed anyway

Re-reading `PartyPoolPump.cs`'s own two-layer echo-suppression design (the class's own doc
comment) against the task's own candidate (c) — "the suppression that prevents 'my own apply
looks like a local action' is keyed on the CLIENT path only" — surfaced a genuine asymmetry,
independent of whichever native mechanism eventually explains the live symptom:

`_pendingApplyTarget` (root cause 1's own fix) protects a key ONLY while step 2's own QUEUED
apply is in flight — a peer's op this instance still owes its engine. A key step 1 ITSELF just
detected and published (a genuine local action — a GUI drag, or a `pooladd` cheat's own landed
effect) got NO equivalent protection at all: the very next tick, it was immediately eligible for
re-detection again. Ordinarily harmless, since a real engine value does not spontaneously change
— but if a fresh scan ever transiently DISAGREED with what step 1 just published (a stale
scan-cache read still in its 1-second window, an item whose resref failed to resolve for one
scan and recovered the next — see `KotorGameBridge.PartyPool.cs`'s own remarks on both), step 1
would misread its OWN recent local action as a FURTHER genuine change and republish it — the
exact "republish my own recent local action" shape root cause 1 already named for a different
trigger (a landed AUTOMATIC apply, not a step-1 DETECTED one).

**Fix** (`PartyPoolPump.cs`): a key step 1 detects and publishes now ALSO lands in
`_pendingApplyTarget`, guarded by a new `_localDetectExpiry` deadline
(`LocalDetectGraceMs = 2000`, twice the bridge's own 1-second scan-cache window). Unlike step 2's
own entries — which wait INDEFINITELY for a matching scan, because a queued apply really is a
fact this instance is still in the process of making true — a step-1 entry is released either
once a fresh scan reconfirms the value (the common, fast path) OR once the grace window elapses,
REGARDLESS of what the scan shows by then. The indefinite-wait shape root cause 1 used would have
been WRONG here specifically: it would permanently swallow a genuine second local change on the
same key (a player who deposits, then immediately withdraws the same stack, would never have the
withdrawal detected — the "safe no-op forever" failure this codebase's own engine-write rules
warn about). The new entries are staged during step 1's own loop but only actually registered
into `_pendingApplyTarget` AFTER the existing "resolve applies that have landed" block runs (see
the new `justDetected` staging list in `Pump()`) — registering them earlier would let that SAME
tick's landed-check see a trivially-matching target and release the key right back out before
step 2 ever saw it excluded, defeating the whole point.

### 10.3 Regression coverage — one timing-based, one fully deterministic

`HostOriginatedPoolAddThroughTheRealCheatChannelDoesNotDoubleApply` (§10.1) exercises the real,
timing-dependent rig end to end; it did not fail before or after this fix (per §10.1, the
mechanism it targets was never reachable this way), but it is kept as the strongest available
loopback coverage of the live scenario's shape.

`AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning` (new) isolates §10.2's
specific mechanism with a fully scripted, deterministic bridge (`ScriptedBridge` — no wall clock,
no randomness, no thread races: each `PartyPoolPump.Pump()` tick is driven by the NEXT entry in
an explicit scan sequence). The script: a host deposit lands and is detected once (0→3), the
next two scans transiently disagree with what was just published (3→0→0 — standing in for ANY
momentary engine-read inconsistency after a local detection), then recovers (0→3→3).

**Verified failing against a reverted copy of this fix** (`git stash` the `PartyPoolPump.cs`
change, keep the test): step 1 misreads the glitch as two FURTHER genuine local changes (a real
Remove then a real Add) — `PartyPoolStore.Version` ends at 3 instead of 1, and the other five
pre-existing `PartyPoolLoopbackTests` all still pass unchanged (confirming root cause 1's own fix
is untouched and this is a distinct regression pin, not a duplicate of it).

**Passing with the fix restored**: `Version` stays 1, `PartyPoolTotals[Blaster]` stays 3
throughout the glitch — the transient disagreement is absorbed as noise instead of being
republished, exactly root cause 2's fix's job.

### 10.4 Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled` still defaults false; this
pass did not touch the default). The specific live symptom §9 recorded (a duplicate
`CheatKinds.PartyPoolAdd` submission on the host's own log) was, for a FOURTH time, not
reproducible through pure C# timing manipulation of this codebase's own async loops — the
evidence now leans more strongly toward a native-layer trigger than before. This pass instead
found and closed a real, adjacent structural gap in `PartyPoolPump`'s own echo-suppression
design (step 1's detections had no protection against a transient scan disagreement that step
2's queued applies already had) — a defensible hardening either way, and, if the native trigger
does turn out to be exactly the kind of transient engine-scan inconsistency this fix anticipates
(a stale scan-cache read, a resref-resolution miss on a freshly-spawned item — both genuinely
possible per `KotorGameBridge.PartyPool.cs`'s own documented behaviour), this fix closes it too.

**Next live-test step, unchanged in spirit from §8's own closing paragraph**: re-run §9's exact
`pooladd` reproduction on a rig with `party_pool_sync=1`, watching the HOST's own plugin log for
a duplicate `CheatKinds.PartyPoolAdd`. If it recurs even with this fix in place, the trigger is
confirmed native (the plugin's own frame timing or the shared-block cheat-serial handshake under
real game-frame scheduling) and this fix, while still a legitimate hardening, is not the whole
answer; if it does not recur, this fix — or the combination of it and root cause 1's earlier one
— was sufficient.

`dotnet build KotorCoop.slnx -warnaserror`: clean. `dotnet test tests/KotorCoop.Tests --no-build
--filter "FullyQualifiedName~PartyPool"`: 30/30 passing (up from the pre-existing suite plus the
two new tests this round added).

## 11. 2026-08-28, live pass — re-tested with `pooladd`: no duplicate, but a NEW, opposite

## defect found — rapid same-resref deposits UNDER-count on the wire, and a landed relay can

## permanently lock a key out of further detection

Task: §10.4's own "next live-test step" — re-run §9's `pooladd` reproduction on a rig with
`party_pool_sync=1`, watching for a duplicate `CheatKinds.PartyPoolAdd`. Two-instance rig,
`store-sync.md`-style isolation (`script_item_sync=0`/`dialogue_item_sync=0` on both, so
`pooladd`'s own `GiveItemToInventory` call is not ALSO picked up by the unrelated
script-item-grant channel as an "untracked" item and double-replicated through a completely
different mechanism — confirmed as real cross-channel noise on the FIRST, non-isolated attempt
this round, matching the identical contamination `store-sync.md`'s own 2026-08-28 pass found for
`buy`). `pooladd g_w_thermldet01` x3 on the HOST, then x3 on the CLIENT, each round issued back
to back over `blockpoke.ps1 -ProcId <pid> console 'pooladd g_w_thermldet01'`.

**The specific symptom §9/§10 chased (a duplicate `CheatKinds.PartyPoolAdd` on the originating
side) did not recur** — consistent with §10's own four independent negative results. **But the
totals never converged to +6 on either side, which the task's own bar ("both pools show exactly
+6, no doubles in either log") fails just as hard as a duplicate would have.** Final tally, both
sides stable for 25+ seconds with no further change:

- **HOST**: exactly 3 `added 'g_w_thermldet01' x1 to the party pool` lines — matching its own 3
  local `pooladd` calls. **Zero** of the client's 3 deposits ever arrived.
- **CLIENT**: exactly 4 `added` lines — 1 relayed from the host, plus its own 3 local calls'
  worth of LOCAL engine effect (all 3 genuinely landed in the client's own inventory — `pooladd`
  is a local engine primitive, not itself a wire call). But only **one** `party pool: local add
  ... detected, publishing` line ever appeared, on the HOST, for its OWN first deposit — the
  client's log shows **zero** `local add ... detected` lines for any of ITS 3 deposits, despite
  all 3 genuinely happening.

### Root cause, read from `PartyPoolPump.cs`'s own step 1/step 2 exclusion logic (VERIFIED,

### code read, not merely inferred from the log pattern)

Two related but distinct mechanisms, both already present in the shipped code:

1. **Step 1's own key exclusion has no headroom for a SECOND genuine change to the same key
   inside the grace window.** `engineForDetect = Without(engine, _pendingApplyTarget.Keys)`
   (line ~127) excludes ANY key currently in `_pendingApplyTarget` from step 1's diff entirely —
   by design, per §10.2's own fix, so a transient scan glitch right after a detection is not
   misread as a further genuine change. But `_pendingApplyTarget` cannot distinguish "a glitch"
   from "a second REAL deposit of the same resref 800ms later" — both look identical from
   inside the pump (a key already pending, seeing yet another local change before its own grace
   window or scan-confirmation clears it). Three `pooladd` calls issued back-to-back over
   `blockpoke.ps1` land well inside `LocalDetectGraceMs`'s 2000ms window, so calls 2 and 3 are
   invisible to step 1 — their local effect is real (the item is genuinely in the engine's own
   party inventory, confirmed by the console's own `added 'X' x1` reply after every single
   call), but nothing about them ever reaches `session.PublishPartyPoolOp`, so the OTHER
   instance never hears about them at all.

2. **A step-2-originated pending entry (an APPLIED relay, not a local detection) has NO
   expiry, and — critically — nothing re-arms it if the local engine total keeps moving
   further while it waits.** The client's ONE received-and-applied relay (the host's first
   deposit) registers `_pendingApplyTarget[key] = target` in the RECONCILE block (`step 2`,
   below the excerpt quoted in §10.2), which — unlike a step-1 detection — is never given a
   `_localDetectExpiry` entry (that dictionary is populated ONLY from `justDetected`, i.e. only
   for step-1's own local-change branch). Per the class's own doc comment, a step-2 entry "wait
   INDEFINITELY for a matching scan... a queued apply really is a fact this instance is still in
   the process of making true" — correct when nothing else is touching that key, but if the
   LOCAL PLAYER ALSO deposits more of the same resref while that relay is still pending (exactly
   this round's own scenario: the client's 3 `pooladd` calls landed seconds after receiving the
   host's relayed deposit), the engine's own total keeps moving further away from the ORIGINAL
   `target` the pending entry is still waiting to see — so it can never "land," and the key
   stays excluded from BOTH step 1 (further local changes invisible) and step 2 (further
   reconcile toward the merged truth also skipped) for as long as that mismatch persists, which
   in the general case is indefinitely.

**These are two independent-but-compounding gaps in the SAME exclusion mechanism** — the first
purely a timing/grace-window question (real but bounded, 2 seconds), the second a genuinely
unbounded lockout with no timeout at all. Both trade the ORIGINAL bug (a duplicate) for the
opposite one (silent data loss / a permanently stuck key), because both mechanisms treat "this
key is currently in flight" as a single boolean with no notion of "how many DISTINCT local
events does this cover" — the party pool's own multiset semantics (§2.1: "a store's stock
genuinely goes up... repeatedly, by either player, so a one-way ratchet is wrong here too," the
identical reasoning this doc's own §2 already gives for the party pool) is exactly the shape
that breaks a single-pending-value model the moment two real events land close together.

### Not fixed this pass — same discipline as `store-sync.md`'s bug 4

This is a live-time-pressure judgement call, made the same way `store-sync.md`'s bug 4 was left
open this same round: the safe fix needs the exclusion mechanism to track "how much of this
key's movement is ALREADY accounted for by an in-flight op" as a quantity, not "is this key
currently touched at all" as a boolean — a real design change to `_pendingApplyTarget`'s own
shape, not a one-line patch, and this codebase's own engine-write discipline (verify a
researched offset/design live before shipping) argues against guessing at it without a
follow-up live re-verification loop this pass did not have budget for.

### Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled`/`profiles/coop.ini.example`
unchanged, still default `false`/`0`). §9/§10's own duplicate-apply symptom is, for a fifth time
(four prior negative results in §10.1 plus this round), NOT what a live rig reproduces — but
this round found a real, live-confirmed, DIFFERENT defect in the exact same exclusion mechanism
those four rounds were all defending: rapid same-key local changes silently fail to replicate,
and a landed relay racing a further local deposit of the same resref can lock that key out of
sync indefinitely. **Concrete next step**: redesign `_pendingApplyTarget` to hold a covered
QUANTITY per key (how much of the key's current total this instance believes is already
accounted for by in-flight/just-applied ops) rather than a single target value, so a NEW local
change past that quantity is still detected even while an earlier one for the same key is still
settling — then re-run this exact `pooladd x3`/`pooladd x3` reproduction to confirm both sides
converge to +6 with no drops and no doubles.

Both `swkotor.exe` processes stayed healthy (`Get-Process`, `Responding=True`) throughout and
after. No crash. `dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError`: both clean — no source changed this pass, a live
rig session only.

## 12. 2026-08-28, round 5 — §11's under-count/lockout fixed: the echo guard is now count-aware,
## not key-blanket

Picked up exactly where §11 left off: "redesign `_pendingApplyTarget` to hold a covered QUANTITY
per key... so a NEW local change past that quantity is still detected even while an earlier one
for the same key is still settling." No rig this round (isolation worktree, no live game
process) — the brief was to reproduce §11's two mechanisms in `PartyPoolLoopbackTests.cs` first,
then fix `PartyPoolPump.cs`, matching the discipline every other round in this document has
followed for a non-live pass.

### 12.1 Root cause, restated precisely

§11 named two "independent-but-compounding gaps in the SAME exclusion mechanism," both stemming
from `_pendingApplyTarget`/`_localDetectExpiry` treating "this key is currently in flight" as a
**boolean** with no notion of *how many distinct local events* that covers:

1. A step-1 detection registered a 2-second blanket exclusion (`LocalDetectGraceMs`) for the
   key. A SECOND genuine local change to the same key inside that window was invisible to step 1
   in its entirety — not delayed, **lost**: `_lastScan` is updated unconditionally every tick
   regardless of exclusion, so by the time the window expired there was no residual diff left to
   find against.
2. A step-2 queued-apply entry had no expiry at all and, critically, nothing re-armed it if the
   local engine total kept moving further while it waited — so a landed relay racing a further
   local change on the same key could lock that key out of BOTH steps indefinitely.

### 12.2 The fix

`_pendingApplyTarget` no longer means "exclude this key from detection." It now means "this key
owes the engine a specific TARGET value, not yet confirmed" — and, critically, **only step 2's
queued applies register an entry in it at all**. A step-1 self-detection no longer registers
anything: the change is already true in the engine the instant it is observed, so there is no
"landing" to wait for at all, and (per §12.4 below) no time-boxed grace window is needed either
— the class doc comment in `PartyPoolPump.cs` calls this "root cause 3" and gives the full
reasoning.

`PartyPoolPump.Pump()`'s step 1 is now a single per-key loop over the union of `_lastScan`'s and
the fresh scan's keys:

- **No movement since the last scan** (`actual == prior`): skip. If a queued apply is
  outstanding for this key, it simply has not landed yet — keep waiting, unchanged from before.
- **Movement, no pending entry**: an ordinary genuine local change. The FULL movement is
  published immediately — no exclusion window at all, so a second, third, or Nth rapid change to
  the same key is never invisible while an earlier one is "settling," because there is no
  settling period for a self-detected change.
- **Movement, a pending entry exists** (a step-2 apply in flight for this key): compare the
  fresh value against the entry's `target` (the value that apply ALONE should produce). An exact
  match is the apply landing cleanly — nothing new to report, the store already accounts for its
  own contribution. Anything else publishes exactly the **excess** beyond `target` (not the raw
  movement, which would double-count the apply's own already-accounted-for contribution) — this
  is the literal "expect this key to move by +N; a later read that differs by exactly N is an
  echo; any FURTHER difference beyond N is a genuine new local change and must be published"
  rule the task asked for. Either way the pending entry is cleared: if the apply turns out not to
  have actually landed after all, the very next reconcile pass (step 2, using the fresh engine
  value against the now-updated store) re-queues whatever gap remains, so nothing is lost by
  resolving the expectation here.

`Without()`-based exclusion survives only in step 2's reconcile (no second apply queued for a
key that already has one in flight) — step 1 no longer excludes anything.

### 12.3 Why this closes both of §11's mechanisms

Mechanism 1 (rapid same-key deposits under-counting): with no step-1 exclusion at all, each
tick's movement is diffed and published on its own — three deposits landing on three consecutive
ticks are three consecutive publishes, not one grace-protected value with the rest silently lost.

Mechanism 2 (a landed relay racing a further local deposit permanently locking a key): the
step-2 entry no longer waits for an EXACT match indefinitely. The moment a fresh scan shows
movement, it is resolved one way or the other in the SAME tick — either confirmed (delta 0,
target reached) or excess-published (delta ≠ 0, whatever the true combined cause). There is no
state a key can get stuck in: every tick with movement either does nothing (waiting, unchanged
total) or fully resolves the pending entry.

### 12.4 A deliberate, documented trade: root cause 2's own glitch-immunity

Root cause 2's original fix (§10.2) gave a step-1 detection its own 2-second grace window
specifically so a TRANSIENT one-or-two-tick scan disagreement (a stale scan-cache read, a
resref-resolution miss) would self-heal without republishing. Because a step-1 detection no
longer registers a pending entry at all, that specific glitch-immunity is gone: a transient dip
that fully reverts to the pre-deposit value is no longer distinguishable, by this mechanism, from
a genuine withdrawal that happens to net back to the same number — and per this round's own task
brief, that ambiguity must resolve in favour of publishing (never silently dropping data).
`PartyPoolLoopbackTests.AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning`'s
own scripted 3→0→0→3→3 wobble is a case in point: the FINAL total is still exactly correct (3,
nothing lost, nothing doubled) but `PartyPoolStore.Version` is now 3 instead of 1 (Add 3, the
glitch's own Remove 3, the recovery's own Add 3) — three real wire ops instead of one, for a
scenario that never happens on a healthy scan path in the first place. This codebase's own
multiset design philosophy (§2.1: "a store's stock genuinely goes up and down repeatedly... a
one-way ratchet is wrong here too") makes this the right trade: correctness over avoiding a rare
extra pair of ops during scan noise. The test's docstring and assertions were updated in place to
record this rather than silently changing behaviour a reader would otherwise assume was still
guaranteed.

### 12.5 New regression coverage

Four new tests in `PartyPoolLoopbackTests.cs`, all verified to FAIL against the pre-fix code and
PASS against the fix:

- `ThreeRapidSameKeyLocalDetectionsAreAllPublishedNotSwallowed` — deterministic `ScriptedBridge`,
  three deposits on three consecutive ticks, reaches 3 (pre-fix: 1 — the second and third
  deposits' movement is silently lost while the first key is excluded).
- `RapidSameKeyDepositsOnEitherSideAreAllCountedNotSwallowed` — the real two-instance async rig
  (host x3, then client x3, +6 both sides): kept as end-to-end store/wire/relay confidence, but
  documented as NOT a reliable pre-fix regression pin (the old guard's exclusion window is only a
  few milliseconds wide in practice for a self-detected key with nothing else touching it, since
  the very next tick's own scan already reconfirms and clears it — real wall-clock spacing can't
  reliably land inside that window, hence the scripted test above as the actual pin).
- `ADepositImmediatelyFollowedByAWithdrawalOfTheSameKeyNetsToZeroNotSwallowed` — deterministic
  `ScriptedBridge`, a withdrawal landing the tick immediately after a deposit (before any
  confirming scan), nets to 0 (pre-fix: stuck at 5 — the phantom-deposit shape of mechanism 1).
- `APeerApplyLandingInTheSameTickAsAGenuineLocalDepositOfTheSameKeyCountsBothAsTwo` —
  deterministic `ScriptedBridge`, a step-2 pending apply (target 1, seeded via
  `PartyPoolStore.Record` the way a peer op would) and a genuine local deposit landing in the
  SAME scan as one combined jump (0→2); the fix publishes exactly the excess (+1), total reaches
  2 (pre-fix: stuck at 1 — mechanism 2's lockout, the apply's pending entry never matches its
  original target again once a second change lands on the same key).

`AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning`'s `Version` assertion
was updated from 1 to 3 (see §12.4); its final-total assertion (3) is unchanged. All other
pre-existing `PartyPoolLoopbackTests` (root cause 1 and 2's own regression pins) pass unchanged —
neither fix's own protection was touched, only step 1's detection mechanism around them.

### 12.6 Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled`/`profiles/coop.ini.example`
unchanged, still default `false`/`0`) — this pass changed only `PartyPoolPump.cs` and its own
test suite, not the shipped default.

`dotnet build KotorCoop.slnx -warnaserror`: clean. `dotnet test tests/KotorCoop.Tests --no-build
--filter "FullyQualifiedName~PartyPool"`: 34/34 passing (up from 30 — four new tests, one
pre-existing test's expectations updated per §12.4). Full suite: see this round's own commit for
the exact pass/fail count (run with `--no-build` after the filtered run above).

**NEEDS-LIVE, unchanged in spirit from §11's own closing paragraph and §10.4's before it**: this
pass had no rig (isolation worktree, no live game process) — the fix and its regression coverage
are bench-only. The concrete next live-test step is exactly what this round's own task brief
described: re-run §11's `pooladd g_w_thermldet01` x3 on the HOST then x3 on the CLIENT (with
`script_item_sync=0`/`dialogue_item_sync=0` on both to avoid the cross-channel contamination §11
found on its first, non-isolated attempt), and confirm both pools converge to exactly +6 with no
drops and no doubles in either log — the exact bar §11 recorded as failed (host stuck at 3,
client's own local deposits never reaching the wire at all). If that reproduces cleanly, this
fix is confirmed sufficient; if a residual gap remains, it is most likely in the native plugin
round trip or the shared-block cheat-serial handshake under real game-frame timing — the same
category of gap §8/§10 already named and were unable to drive from C# alone, since this round's
fix operates entirely on the `PartyPoolPump.cs` side of that boundary and cannot itself rule out
a native-layer contribution to any residual live discrepancy.

## 13. 2026-08-28, live pass — §12's live-test plan run; a NEW, more basic native-layer defect
## found: a freshly-inserted pool item is not reliably found by the very next `poolremove`/scan,
## independent of anything `PartyPoolPump.cs` does — stays OFF

Ran §12's own closing "concrete next live-test step" on the two-instance rig
(`party_pool_sync=1` both, `script_item_sync=0`/`dialogue_item_sync=0` both, as directed). Found
the bug is more basic than anything §9-§12 diagnosed or fixed, and sits entirely on the
NATIVE/engine side of the boundary §12's own closing paragraph flagged as unable to rule out from
C# alone.

### 13.1 `pooladd g_w_thermldet01` x3 host, x3 client: only 1 of the host's 3 local additions ever
### reached the session layer at all

Host issued three `pooladd g_w_thermldet01` console calls, ~2s apart. The plugin's own `cheat:
added...` line fired for all three (the engine call itself succeeded three times) — but the
HOST's own `agent1.log` shows exactly ONE `party pool: local add of 1x 'g_w_thermldet01' detected,
publishing` line, for the first call only. The second and third produced no detection at all — not
delayed, never appeared, even after 20+ seconds of settle time. Every one of the plugin's own
three `party pool: added 'g_w_thermldet01' (0xE4) x1, ...` lines reports the SAME item id
**and the same post-add `StackSize` of 1**, all three times — not growing to 2, then 3, the way a
merge into one running stack would read. The client, meanwhile, logged four local `cheat: added`
events against only three console calls typed on it — one extra, timed between the host's second
and third call, consistent with (but not proven to be) one of the host's own ops actually arriving
over the wire. The host never logged receiving any of the client's three.

### 13.2 The same defect, isolated from `PartyPoolPump.cs` entirely: `poolremove` immediately after
### `pooladd` of the identical resref fails outright

```
pooladd g_i_progspike01            -> added 'g_i_progspike01' x1 to the party pool
                                       (plugin log: added 'g_i_progspike01' (0xE7) x1, ...)
poolremove g_i_progspike01  (~2s later)
                                    -> the engine refused it: 'g_i_progspike01' remove failed:
                                       no matching stack found in the pool
```

Neither of these verbs touches `PartyPoolPump.cs` or the wire at all — `pooladd`/`poolremove` are
both purely local, single-instance staging cheats (`CheatController.PartyPool.cs`,
`Odyssey.AddItemToPartyPool`/`RemovePartyPoolStack`). A THIRD, independent reader settles that the
item really is there: `item 0xE7` (the item-identity read primitive, unrelated code path) answered
`item 0xE7 g_i_progspike01: stack 1, chg 0/0, upg 0x0, UNidentified` in the same breath —
confirming the object is live, correctly tagged, and resolvable by id, while
`RemovePartyPoolStack`'s own tag-based repo walk cannot find it two seconds later.

### 13.3 Reading, not diagnosing (this round had no time budget to go further)

Two candidate explanations, not distinguished this round:

1. `Odyssey.PartyPool.cs`'s `AddItemToPartyPool` spawns a genuinely NEW item via
   `GiveItemToInventory` every call (never searching for an existing matching stack to merge
   into), then does `RepositoryRemoveItemFn(playerRepo, item)` +
   `RepositoryInsertItemFn(repo, item, -1)` to move it from the player's own backpack into the
   pool repo. If `RepositoryInsertItemFn(..., -1)` does not update whatever count/array field the
   SEARCH side (`FindFirstMatchingItem`, and by extension the session's own
   `TryScanPartyPoolTotals`) iterates, every symptom above is explained by one root cause: the
   insert succeeds (the item object is live, tagged, and independently readable by id) but is
   invisible to any subsequent walk of the repository.
2. `ResolvePartyPoolRepo()` (`CSWPartyTable+0x118`'s `CItemRepository`) resolves a different
   pointer on a call shortly after another, so `poolremove`/the scan walk a repo the just-inserted
   item was never linked into.

Either way this is a NATIVE-side defect in the party-pool repository plumbing itself, not in
`PartyPoolPump.cs`'s C# detection/reconcile logic that §9-§12 spent four rounds fixing — those
fixes are still believed correct for what they cover (nothing in this round contradicts them), but
they sit downstream of a more basic problem: the plugin's own insert is not reliably visible to
the plugin's own very next read of the same repository. The next round should paste
`ResolvePartyPoolRepo()`'s returned pointer and the repo's own item-count field right before and
right after one `pooladd` call, the same "two lookups, one frame" discipline that closed the
enemy-presence-sync hunt (`docs/research/enemy-presence-sync.md`), rather than adding further
C#-side detection logic on top of an already-lossy native read.

### 13.4 Verdict

`party_pool_sync` stays OFF (`profiles/coop.ini.example`/`AgentSettings.PartyPoolSyncEnabled`
unchanged). §12's own C# fix may well be correct, but it cannot be confirmed live while the
underlying native insert/search disagreement in §13.1-§13.2 stands — a wire-level "no under-count"
test is meaningless when the local engine already loses track of its own inserts within a couple
of seconds, independent of any sync code. No source changes this round; live-verification only.

## 14. 2026-08-28, round 6 — two independently-diagnosable freshness bugs from §13 fixed
## (AGENT scan-cache staleness, PLUGIN resref-vs-Tag match precedence); NO RIG this round, so the
## live re-test §13 called for is NOT run — stays OFF, both fixes NEEDS-LIVE

§13 left two candidate causes on the table for "a freshly-inserted pool item is not reliably
found by the very next `poolremove`/scan": a NATIVE-side repository-plumbing defect (§13.3's own
leading hypothesis, still open — see below) and, independently, two narrower freshness bugs each
worth fixing on their own merits regardless of which native explanation eventually proves out.
This round fixes those two; no live rig was available to run §13's own closing "concrete
next-step" (pasting `ResolvePartyPoolRepo()`'s pointer and the repo's item-count field around one
`pooladd` call) or the live re-test this task asked for (`pooladd` ×3 rapid → 3 detected;
`pooladd` then `poolremove` → found) — both remain **NEEDS-LIVE**.

### 14.1 AGENT-side: the bridge's own scan cache could mask a second rapid change behind an
### already-stale window

`KotorGameBridge.PartyPool.cs`'s `TryScanPartyPoolTotals` throttles a genuine repository walk to
once per `PartyPoolScanIntervalMs` (1000ms), returning the last snapshot on every call inside that
window — cheap and correct for a caller that only needs "recent enough." The bug: once any real
(non-cached) scan ran, the cache re-armed a FULL fresh window from that scan's own timestamp
regardless of whether anything had actually changed since the LAST scan before it. A second
genuine local change landing shortly after a first was detected could land inside a window a
merely-confirming scan had just re-armed, reading as stale until the window ran out again — the
freshness bugs sit ON TOP of §9-§12's own detection-logic fixes, not instead of them.

**Fix.** `IPartyPoolBridge` gets a new default-no-op member,
`InvalidatePartyPoolScanCache()` (`src/KotorCoop.Memory/IPartyPoolBridge.cs`), implemented by
`KotorGameBridge.PartyPool.cs` as `_nextPartyPoolScanTicks = 0` — forces the very next
`TryScanPartyPoolTotals` call to bypass the throttle. `Pumps/PartyPoolPump.cs` calls it in two
places: inside `PublishDetected` (every step-1 detection, ordinary or pending-excess) and right
after step 2 enqueues a reconcile apply — both are moments the pump has concrete reason to expect
the engine is about to look different again. A scan this cheap (a linked-list walk over a few
dozen items' worth of `ReadProcessMemory` calls — the same order of magnitude as
`ContainerLoot`'s own 1Hz sweep) affords running more often than its nominal throttle whenever the
pump has a specific reason to ask again.

**Test** (`tests/KotorCoop.Tests/PartyPoolLoopbackTests.cs`,
`ASecondRapidLocalChangeIsNotMaskedBehindTheRestOfAnAlreadyStaleScanWindow`): a new
`ThrottledFakeBridge` reproduces the real cache/throttle mechanism exactly, but driven by a
test-controlled `Clock` field instead of `Environment.TickCount64`/a real timer — fully
deterministic, no `Task.Delay`, no risk of a background pump tick consuming the fix's one-shot
invalidation before the test can observe it (a real-timing version of this scenario was tried
first and rejected for exactly that race). Sequence: baseline scan at clock 0; a first deposit
observed at clock 1000 (past the cache's own natural window, so this part is unaffected by the
fix); a SECOND deposit at clock 1001 — one tick later, nowhere near another full window. **Verified
this is a genuine regression pin**: with the two `InvalidatePartyPoolScanCache()` calls in
`PartyPoolPump.cs` temporarily commented out, the test fails (`Expected: 2, Actual: 1` — the exact
"three inserts read as one" shape); restored, it passes.

### 14.2 PLUGIN-side: `RemovePartyPoolStack` trusted Tag alone, which §13.2 already showed can
### disagree with the object even while a THIRD reader confirms it

`Odyssey.PartyPool.cs`'s `RemovePartyPoolStack` matched candidates purely by
`TagEqualsIgnoreCase` before this round. §13.2's own live evidence is worth restating precisely:
`item 0xE7` (an unrelated, independent read primitive) confirmed the freshly-inserted item was
live and "correctly tagged" in the SAME breath `RemovePartyPoolStack`'s own Tag-based walk
reported "no matching stack found" — i.e. by the time a THIRD reader looked, Tag read back fine,
yet the search still missed it. That specific observation does not, by itself, prove Tag was ever
literally empty at removal time (§13.3 leaves the two live hypotheses as native repo-plumbing
issues, not a Tag-content issue) — but it does establish that Tag alone is not a reliable enough
signal to gate a removal on, immediately after an insert, whatever the deeper mechanism turns out
to be.

**Fix.** New pure, portable file `src/KotorCoop.Plugin/PartyPoolStackMatcher.cs` (added to
`KotorCoop.Tests.csproj`'s compile-include list, per this repo's "plugin pure arithmetic" testing
convention) holds the precedence rule: `Matches(spawnRingResref, targetResref, tagMatches)` trusts
`spawnRingResref` — what `ItemTemplateSpawnHook` itself captured for that item's object id at the
exact moment `CreateItemOnObject` built it (the same VM command `AddItemToPartyPool`'s own
`GiveItemToInventory` call routes through, so a party-pool deposit is always captured) —
whenever the ring has an answer at all, and falls back to `tagMatches` only when it does not
(evicted by `ItemTemplateSpawnHook.Capacity` (16) more recent spawns, a module change cleared it,
or the item predates this plugin session). `ItemTemplateSpawnHook.cs` gained `TryGetResRef(uint
objectId)`, searching the ring newest-first and trimming at the first NUL, the same discipline
`TagEqualsIgnoreCase` already uses. `RemovePartyPoolStack`'s per-candidate loop now computes both
signals and calls `PartyPoolStackMatcher.Matches` instead of checking Tag alone.

**Test** (`tests/KotorCoop.Tests/PartyPoolTests.cs`, four `[Fact]`s): `SpawnRingMatchIsTrusted`,
`SpawnRingDisagreementOverridesATagMatch` (the load-bearing one — a Tag match is deliberately
overridden by a ring mismatch, the precise shape needed if Tag content is ever the actual
divergent field), `SpawnRingComparisonIsCaseInsensitive`, `NoRingEntryFallsBackToTag`.

**What this fix does NOT address.** If §13.3's hypothesis 1 is the real mechanism —
`RepositoryInsertItemFn(repo, item, -1)` not updating whatever `item_count`/`items_list` field
`RemovePartyPoolStack`'s own `count`/`ids` read walks — then the freshly-inserted item's id is
never even IN the `snapshot` this loop iterates, and no amount of match-precedence improvement
inside the loop can find an id that was never offered to it. This round's fix improves matching
once a candidate id is present to examine; it does not touch `ResolvePartyPoolRepo()` or the
repository's own count/array fields, which §13.3's closing paragraph already flagged as the next
round's real target. Framed the way this task's own hypothesis did (Tag being empty/unreliable at
insert time, or a resref-cache mismatch) — this fix covers that shape. Framed the way §13.3's own
live evidence leans (a native repository-visibility gap) — this fix is necessary-but-maybe-not-
sufficient, and only a live pass with the "two lookups, one frame" `ResolvePartyPoolRepo`/count
dump §13.3 asked for will tell which.

### 14.3 Build/test evidence this round

`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors. `pwsh
src/KotorCoop.Plugin/build.ps1 -WarnAsError`: clean NativeAOT win-x86 build. `dotnet test
tests/KotorCoop.Tests --no-build --filter "FullyQualifiedName~PartyPool"`: 39/39 passed (30
pre-existing + 1 new cache test + 4 new matcher tests + 4 new tests overall counted against the
pre-round baseline). Full `dotnet test KotorCoop.slnx` run for this round's regression sweep —
see the repo's own commit history for the exact pass/fail count logged alongside this entry.

### 14.4 Verdict

`party_pool_sync` stays OFF — unchanged this round. Both fixes are believed correct for what they
individually cover (each has its own deterministic regression test, and each was verified to
actually fail without its fix — see §14.1/§14.2's own "verified this is a genuine regression pin"
notes) but **NEEDS-LIVE**: no rig was available this round to run either §13's own native-layer
diagnostic step or the live re-test this task asked for. The next round with a rig should run
BOTH: §13's "two lookups, one frame" `ResolvePartyPoolRepo`/count-field dump around one `pooladd`
(still the open question this round could not touch), and then, only once that native question is
answered, the `pooladd`×3-rapid / `pooladd`-then-`poolremove` re-test — since a native
repository-visibility gap, if that is what §13.3 turns out to be, would still fail the live
re-test regardless of how correct this round's two C#/plugin-side fixes are.

## 15. 2026-08-28/29, live pass — §13.3's own "two lookups, one frame" diagnostic run; hypothesis 1
## CONFIRMED with direct evidence (`item_count` never updates on insert), hypothesis 2 RULED OUT
## (the repo pointer is stable); stays OFF, root cause now precisely located

Task: this round's own queue item 2, plus §13.3's/§14.4's own explicit closing ask — dump
`ResolvePartyPoolRepo()`'s pointer and the repo's own `item_count` field immediately before and
after one `pooladd` call, in the same frame, before re-running the `pooladd`×3-rapid /
`pooladd`-then-`poolremove` live tests.

### 15.1 The diagnostic added

`Odyssey.AddItemToPartyPool` (`src/KotorCoop.Plugin/Odyssey.PartyPool.cs`) now logs, on every
call: the resolved repo pointer, `item_count` immediately before and after
`RepositoryInsertItemFn`, and whether the just-inserted item id is found by walking the
repo's own array up to the post-insert `item_count` — the exact "two lookups, one frame"
discipline that closed the enemy-presence-sync hunt, reused here for the identical shape of
problem (`docs/research/enemy-presence-sync.md`).

### 15.2 Result: `item_count` does not change across a genuine insert, reproduced 6/6 times, on
### BOTH host and client, across two separate live sessions this round

Host, three back-to-back `pooladd g_i_progspike01` calls, ~2-18s apart:

```
party pool: insert diag - repo 0x186A7788, item_count 1->1, 0xBE is NOT in the post-insert array walk
party pool: insert diag - repo 0x186A7788, item_count 1->1, 0xC0 is NOT in the post-insert array walk
party pool: insert diag - repo 0x186A7788, item_count 1->1, 0xC1 is NOT in the post-insert array walk
```

A fourth call (`g_i_drdshld001`) immediately followed by `poolremove` of the SAME resref
reproduced §13.2's exact symptom, now with the mechanism attached in the same breath:

```
party pool: insert diag - repo 0x186A7788, item_count 1->1, 0xC2 is NOT in the post-insert array walk
party pool: added 'g_i_drdshld001' (0xC2) x1, ...
party pool: remove 'g_i_drdshld001' x1 failed - no matching stack found in the pool
```

**The repo pointer itself never changed across ANY call this round** (`0x186A7788` on the host,
every single time, across dozens of calls over ~20 minutes of wall time) — this is direct,
positive evidence AGAINST §13.3's hypothesis 2 (`ResolvePartyPoolRepo()` resolving a different
pointer call to call). `item_count`, read fresh both immediately before and immediately after the
SAME `RepositoryInsertItemFn` call that is the only write between the two reads, is IDENTICAL
every time — not "sometimes stale," genuinely unchanged, on a call the engine's own item body
(tag, id, spawn-ring capture) proves actually ran (§13.2's own independent-reader evidence still
holds: a live `item <id>` read agrees the object is real). This is now the strongest evidence yet
for hypothesis 1: `CItemRepository::InsertItem`'s (0x0055D2C0) append path (index `-1`, this
codebase's only calling convention) does not update `item_count` (`repo+0x10`) — either the
disassembly's own understanding of the append path is incomplete (an increment that requires some
precondition this call site does not meet), or `item_count` at `+0x10` is not the field the
append path actually increments (a capacity vs. count desync, i.e. the true backing array DID
grow but the field this codebase — and by extension `RemovePartyPoolStack`'s own search loop,
which reads the identical field — reads to bound its walk does not reflect it).

**One asymmetry worth flagging, not yet explained.** The identical test run on the CLIENT this
round showed a different secondary symptom: `item_count` was likewise stable (`6->6` all three
calls) but the post-insert array walk reported the SAME item id (`0xBF`) as present all three
times, for three textually-identical `pooladd g_i_progspike01` calls that each separately logged
"added... (0xBF)". Either the object id genuinely was not incrementing across calls (a possible
second, independent defect in id allocation/recycling this round did not chase), or the id WAS
advancing normally and the walk's "IS in the array" verdict was a coincidental match against a
pre-existing, unrelated array slot rather than proof the fresh insert linked correctly — the
diagnostic as written cannot distinguish the two. Flagged for whoever picks this up: log the
returned `itemId` value on every call (not just inside the "added" line, which already does) and
cross-reference it against the walk hit to settle which explanation is right.

### 15.3 Sell/store-sync connection

`docs/research/store-sync.md`'s own live pass this round hit what is very plausibly the SAME
defect from the other side: `sell Larrim_Store <resref>` on a `pooladd`-freshly-inserted item
failed with "no matching item in inventory" even immediately after a fresh `openstore` (ruling
out a stale GUI sell-list). `SellToStore`'s own fix resolves the identical repository
(`GetItemRepositoryFn(player, 1)` -> the same `CSWPartyTable::GetPartyItems` this file's
`ResolvePartyPoolRepo()` also calls) — if its own search is ALSO bounded by `item_count`, a
freshly-inserted item would be invisible to it for the exact same reason `poolremove` cannot find
one. This is not proven (this round did not disassemble `CSWGuiStore::SellItem`'s own inventory
scan to confirm it reads the same count field), but it means `sell`'s own live-test blocker this
round may not be a `sell`-specific defect at all — see that document's own new section for the
full detail.

### 15.4 Verdict

`party_pool_sync` stays OFF — unchanged this round. §13's own open question is now answered as
far as live evidence can take it without a disassembly pass on `CItemRepository::InsertItem`
itself: the repo pointer is stable (hypothesis 2 ruled out), and `item_count` provably does not
reflect a genuine, engine-confirmed insert (hypothesis 1 confirmed with direct before/after
evidence, not inference). The next round's concrete target is now narrow and specific:
disassemble `CItemRepository::InsertItem` 0x0055D2C0 itself (not just its call site) to find
either the real increment condition the `-1` append path is missing, or the TRUE count/capacity
field this codebase should be reading instead of `+0x10` — the same "read the function, not just
its documented call convention" fix that closed the `sell`/`GetItemRepository` defect earlier
this session. No source changes this round beyond the diagnostic logging itself (already
committed alongside `followkick`, see `docs/live-verification.md`).

## 16. 2026-08-29 — §15's own closing ask answered by disassembly: `InsertItem` bails out on a
## flag bit the party pool's repository is ALWAYS constructed with; switched to the engine's own
## `AddItem`, which is not gated the same way — fixed, NEEDS-LIVE, stays OFF

Task: §15's own concrete next target — disassemble `CItemRepository::InsertItem` 0x0055D2C0
itself, find either the missing increment condition or the true count/capacity field — plus this
round's own wider brief (trace `AddItem`'s full tail, decide whether it is safe to call, settle
the possessor question, confirm the read side). No rig this round (isolation worktree); this pass
is static RE, source fix, and build/test verification only, matching every prior no-rig round's
own discipline in this document.

### 16.1 Root cause, found on `InsertItem`'s very first two instructions

Fresh full disassembly of `CItemRepository::InsertItem` 0x0055D2C0
(`tools/disasm.py 0x0055D2C0 400`), VERIFIED:

```
0x0055D2C0  test byte ptr [ecx+8], 1     ; ecx = this (repo); test bit 0 of the byte at repo+0x08
0x0055D2C4  jne 0x55d326                 ; SET -> jump straight to `ret 8` — nothing else runs
0x0055D2C6  mov eax, [esp+8]             ; (bit 0 CLEAR only, from here on) eax = index arg
0x0055D2CA  test eax, eax
0x0055D2CC  jl 0x55d2ea                  ; index < 0 -> append path
0x0055D2CE  cmp eax, [ecx+0x10]          ; index vs item_count
0x0055D2D1  jge 0x55d2ea                 ; index >= count -> also append path
        ; [0 <= index < count]: overwrite that slot's id, tail-jump into 0x00616940
0x0055D2EA  ...                          ; APPEND path: grow the array via
                                          ; CExoArrayList::Allocate 0x005E03C0 if count==capacity
                                          ; (repo+0x14, a THIRD field this document had not
                                          ; previously named), write items_list[count]=id,
                                          ; item_count++ (repo+0x10) — confirms §1.3's own
                                          ; original reading of the append path was correct...
                                          ; for a repository this bit is CLEAR on.
```

**The first two instructions are an unconditional bail-out**: whenever bit 0 of the repository's
own `+0x08` byte is set, `InsertItem` does `ret 8` immediately — no array touch, no grow, no
`item_count` write, nothing. Every other instruction in the function, including the append path
that §1.3 originally (correctly, for a bit-0-clear repo) documented, is unreachable when that bit
is set.

**`+0x08` is a real, load-bearing field this document had not previously named at all** —
`CItemRepository`'s size (24/`0x18`, VERIFIED against `tools/q.py sql "select size from classes
where class_name='CItemRepository'"`) leaves exactly one dword of room between the documented
`+0x04` ("new item" counter) and `+0x0C` (`items_list`), and this pass's own fresh disasm of
`CItemRepository::Constructor` 0x0055D290 (below) confirms it is written there, not merely read.

### 16.2 `CItemRepository::Constructor` (0x0055D290) — a SECOND stack argument sets that bit,
### preserving whatever else is there

VERIFIED, full disasm, and cross-checked against `tools/q.py`'s own `param_size_bytes=8` for this
function (two stack dwords, i.e. genuinely `__thiscall(this, int parentId, int flag)`, not the
one-argument reading this document previously assumed):

```
mov edx, [esp+4]        ; edx = arg1 = parentId
...                       ; zero +0x0C/+0x10/+0x14 (items_list/item_count/capacity), write
                          ; +0x00 = parentId
mov edx, [esp+8]         ; edx = arg2 = flag
mov ecx, [eax+8]         ; ecx = existing +0x08 (whatever the allocator left there)
xor ecx, edx
mov edx, [eax+8]
and ecx, 1               ; ecx = bit0(existing) XOR bit0(flag)
xor edx, ecx             ; edx = existing with ONLY bit0 replaced by flag's bit0
mov [eax+8], edx
```

This is a compiler-generated "set one bitfield bit from a bool argument, leave the rest of the
byte alone" sequence — `Constructor(this, parentId, bool someFlag)` sets `+0x08`'s bit 0 to
`someFlag`, nothing else.

### 16.3 `CSWPartyTable::GetPartyItems` (0x00563340) constructs the party pool's repository with
### that flag TRUE — the party pool's own repository is the one this bug always fires against

`tools/xrefs.py 0x55d290` lists exactly 6 call sites to the Constructor. Fresh disasm of each
call site's own argument setup, VERIFIED:

| Caller | Owner type | Constructor's arg2 (the flag) |
| --- | --- | --- |
| `CSWSCreature::Constructor` 0x004F7A10 (a creature's own `+0xA30` backpack) | creature | a register already proven `0` by a `cmp eax, ebx` null-check idiom right above the call — **0** |
| `CSWSPlaceable::Constructor` 0x005877E0 (a container's `+0x36C` repo) | placeable | same idiom, same register, proven `0` — **0** |
| `CSWSStore::Constructor` 0x005C6AB0 (a store's `+0x230` repo) | store | same idiom, same register, proven `0` — **0** |
| `CSWPartyTable::GetPartyItems` 0x00563340 (the party pool, `+0x118`) | — | **1** (see below) |
| `CSWPartyTable::TransferInventory` 0x005641E0 | — | not chased this pass, out of scope (transient repo, not `+0x118`) |
| `CSWPartyTable::CreateParty` 0x00565760 | — | not chased this pass, out of scope (transient repo, not `+0x118`) |

**Only `GetPartyItems` passes a nonzero flag, and the party pool's own repository is the ONLY one
of the four persistent repositories this codebase touches (party pool, creature backpack,
placeable container, store) constructed with the bug-triggering bit set.** This is confirmed,
not inferred — placeable and store repositories share the SAME null-check-proven-zero register
idiom `CSWSCreature::Constructor` uses, so `InsertItem` is not broken for containers or stores by
this defect. (`InsertItemIntoStore` in `Odyssey.Store.cs` is therefore left unchanged this
round — see §16.6.)

**How `GetPartyItems` produces a `1`, worth recording precisely because it very nearly reads as a
compiler bug**: at `0x00563388`, `push 1` executes immediately before `call 0x004AEA40`
(`CServerExoApp::GetPlayerCreatureId`, whose docs-cited call shape was `GetPlayerCreatureId(1)`).
Fresh disasm of that call's OWN target, `CServerExoAppInternal::GetPlayerCreatureId` 0x004B4CB0
(reached via the thunk at 0x004AEA40, `mov ecx,[ecx+4]; jmp 0x4b4cb0`), shows it is a bare
`__thiscall(this)` that reads NO stack argument at all and does a plain `ret` (no immediate —
pops nothing beyond the return address). **The docs' own prior reading of "`GetPlayerCreatureId(1)`"
was wrong about what the `1` is for** — it is never consumed by that call at all. It is staged
early (a compiler scheduling choice) and survives untouched across the call, landing as
`Constructor`'s own second stack argument once `push eax` (the resolved player id) is pushed
immediately after and `call 0x0055D290` executes. **The literal constant `1` that
`GetPartyItems` happens to push for a completely different, unrelated reason is what sets the
party pool's own repository `+0x08` bit 0** — an accidental-looking but entirely real and
consistent piece of retail engine code, reproduced identically across every observation this
round made of it (the flag never varies; it is a compile-time constant, not runtime data).

### 16.4 `CItemRepository::AddItem` (0x0055D330) — full disasm this pass; NOT gated the same way,
### and is what the party pool needs

Full disassembly (`tools/disasm.py 0x0055D330 1300`), VERIFIED. Signature correction from this
document's own prior (partial) reading: `__thiscall(this=repo, CSWSItem** ppItem, int wantMerge,
int arg3, int markNewItem)`, `ret 0x10` (4 stack dwords, matching `tools/q.py`'s own
`param_size_bytes=16`) — the FIRST argument is a pointer TO an item pointer (in/out), not a bare
item pointer: on a successful merge the callee writes the EXISTING stack's pointer back into
`*ppItem`; on a credit-chit/Pazaak-card conversion it clears `*ppItem` to 0 after deleting the
incoming item (both already partially documented in §1.4; the by-reference shape is new this
pass and explains both writes cleanly).

**A real, literal call site, fully traced, gave the exact argument shape to copy**:
`CSWSStore::AddItemToInventory` 0x005C70C0 — a small, single-purpose "give item to this
repository" wrapper, `tools/xrefs.py 0x55d330`'s 38 callers narrowed to this one specifically
because it has nothing else going on (no GUI, no gold, just four literal pushes):

```
push 1        ; arg4 = markNewItem = 1
push 0        ; arg3 = 0
push 1        ; arg2 = wantMerge = 1
push ebx      ; arg1 = ppItem (the caller's own item-pointer-pointer)
call 0x55d330
```

**Confirms, for a bit-0-SET repository (the party pool), `item_count` DOES get updated — the
opposite of `InsertItem`**: with `wantMerge=1`, `AddItem` first walks every existing entry via
`CSWSItem::MergeItem` 0x00553F90 looking for a mergeable stack (same tag id + same `+0x264` byte);
when nothing merges, it falls through to a SEPARATE append block (`0x0055D73C-0x0055D774`) that
runs the SAME grow-the-array-then-append-then-`item_count++` sequence `InsertItem`'s own
(unreachable, for this repo) append path uses — this append block is **not** gated on the `+0x08`
bit at all. Only `InsertItem`'s own top-of-function check is. This directly answers this round's
own §16 question: **`AddItem` is the correct, and now the only working, primitive for linking an
item into the party pool's repository.**

**Possessor — settles §1.6/§2.4's own open question.** A SEPARATE branch, gated on bit-0-SET
specifically (`0x0055D6FE-0x0055D728`, reached only for the party pool), resolves a creature via
`0x004AE920` (the same lower-level lookup `GetPlayerCreatureId`'s own body calls internally) and
calls `CSWSItem::SetPossessor` 0x00553210 on the item before the array-append runs. **Yes — the
party pool needs a possessor write on a fresh add, and the engine's own `AddItem` already does
it**; `AddItemToPartyPool` no longer has to reason about this by hand (its prior doc comment's own
"NEEDS-LIVE: the possessor semantics... were not independently observed" is resolved by using the
primitive that already gets it right, rather than by independently guessing).

**The "GUI message" tail — corrects this document's own prior framing, and narrows the risk.**
§1.4 previously reasoned the notification was gated to a store/placeable owner and therefore
inert for a creature-owned repository. Closer tracing this pass shows that reading was wrong in
its specifics: the block at `0x0055D5B6-0x0055D610` compares the repository owner's `+0x08` type
byte against two literal constants — read directly from the retail binary's own `.rdata`
(`tools/disasm.py`'s helper script extended to read raw bytes at a VA): `[0x746634] = 5` and
`[0x746635] = 6`. Per this project's own `kotor-coop-object-type-field-trap` finding (a live,
driveable creature's low byte reads `5`), **type `5` is CREATURE** — so the notification IS
reachable for a creature-owned repository, i.e. the party pool, not excluded from it as
previously assumed. What DOES gate it, and matters more: **it only runs on a MERGE SUCCESS**
(`wantMerge=1` AND an existing compatible stack found) — the merge-search loop's own "no match"
exit (`jmp 0x0055D6C2`) skips this block entirely, so a brand-new key's first deposit into the
pool never reaches it. When it does run (a second-or-later deposit of an already-present stack),
it resolves to a getter+notify pair (`0x004AEDB0`, a plain field-offset getter with no stack args
of its own — its own two pushed args are, by the same "staged early, never popped" mechanism as
§16.3's `push 1`, actually consumed by the FOLLOWING call — then `0x00569FC0`, called on the
getter's result) that was not traced further than confirming its shape matches the local,
non-networked client/server notification bus `docs/research/container-sync.md` already documents
and this codebase already treats as safe-when-unobserved elsewhere (progression-sync.md's
credits-readout precedent). **NEEDS-LIVE**: watching one merge-deposit (the same resref added
twice) for any visible glitch with no stash panel open is the concrete open item this leaves —
this pass's own confidence is "matches an established safe pattern," not "independently proven
inert" the way §1.4 originally overclaimed.

Gold/Pazaak-card special-casing (§1.4) is unchanged by this pass's own re-read: still deletes the
incoming item and writes nothing to `item_count`, confirmed again in the fresh full trace.

### 16.5 Read side: no change needed

`KotorGameBridge.PartyPool.cs`'s `TryScanPartyPoolTotals` reads `repo+0x0C`/`repo+0x10`
(`items_list`/`item_count`) — the EXACT two fields both `InsertItem`'s own (bit-0-clear-only)
append path and `AddItem`'s own (bit-0-agnostic) append path write together in the same two
instructions (`items_list[count]=id; count++`). Once `item_count` is actually being updated (by
calling `AddItem` instead of the always-no-op `InsertItem`), the agent's existing read path is
already correct and needs no change — this directly answers this round's own item 4.

### 16.6 The fix

`src/KotorCoop.Plugin/Odyssey.PartyPool.cs`: added `RepositoryAddItemFn` (0x0055D330, the 5-arg
`__thiscall` shape from §16.4). `AddItemToPartyPool` now calls
`RepositoryAddItemFn(repo, (nint)(&itemLocal), 1, 0, 1)` — the exact literal argument shape
`CSWSStore::AddItemToInventory` uses (§16.4) — instead of `RepositoryInsertItemFn(repo, item,
-1)`, after still unlinking from the player's own backpack via `RepositoryRemoveItemFn` (unchanged
— `AddItem` only links INTO its own repo, it does not know to unlink from elsewhere first). The
existing "two lookups, one frame" diagnostic (§13.3/§15) is kept and extended: it now also reports
whether `itemLocal` came back unchanged (a fresh array entry), cleared (a gold/Pazaak conversion),
or repointed (a merge into an existing stack), so a live re-test can read the mechanism straight
off the log line instead of inferring it from `item_count` alone.

`Odyssey.Store.cs`'s `InsertItemIntoStore` is **unchanged** — §16.3's table shows a store's own
repository is always constructed with the flag clear (`CSWSStore::Constructor`'s own single call
site, the SAME null-check-proven-zero-register idiom every non-party-pool repository uses), so
`InsertItem` already works there; switching it to `AddItem` as well was out of this round's scope
per the task's own "if the same" qualifier, since it is not.

`RepositoryInsertItemFn`'s own delegate and doc comment are kept (still used by
`InsertItemIntoStore`), rewritten to state the bail-out precisely instead of the prior
(incomplete) reading of its append path alone.

### 16.7 Tests

No new pure-logic surface was introduced — the fix is a native delegate retarget with a verified
different call shape and literal arguments, not new branching C# logic, so there is nothing novel
to unit-test in isolation the way `PartyPoolStackMatcher`/`PartyPoolDiffRule` were for earlier
rounds. `dotnet build KotorCoop.slnx -warnaserror` and `pwsh src/KotorCoop.Plugin/build.ps1
-WarnAsError` both clean (0 warnings, 0 errors) with the new delegate and call site.
`dotnet test tests/KotorCoop.Tests --no-build`: full suite unaffected (native plugin code is not
exercised by the managed test suite; no existing test references
`AddItemToPartyPool`/`RepositoryInsertItemFn`/`InsertItemIntoStore` directly — confirmed by
`grep -rl` across `tests/` and `src/` before making this change).

### 16.8 Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled`/`profiles/coop.ini.example`
unchanged) — this is a native engine-call fix with no rig available this round to confirm it live.
**This is believed to be the actual, complete fix for the defect §7-§15 spent nine rounds
chasing and narrowing** (every one of those rounds' own C#-side fixes to `PartyPoolPump.cs` — root
causes 1-3 — remain independently correct and untouched; they were always downstream of this
native defect, never wrong on their own terms, just unable to fully converge while the native
insert silently no-op'd underneath them). **Concrete next live-test step** — the same one §14.4
already called for, now finally unblocked by a root cause instead of two hypotheses: `pooladd
g_i_progspike01` immediately followed by `poolremove` of the same resref (§13.2's exact
reproduction) should now succeed instead of failing "no matching stack found"; `pooladd`
`g_w_thermldet01` x3 rapid on the HOST then x3 on the CLIENT (§11's own reproduction) should now
converge both sides to exactly +6; and the new diagnostic's own `itemLocal` line should show
`unchanged (fresh entry)` for a first deposit of a new key and `changed to 0x... (merged...)` for
a second deposit of an already-present one — confirming the mechanism directly rather than
inferring it from `item_count` alone. If any of these still fail, the failure mode has moved past
everything §7-§16 have collectively covered and is worth a fresh "two lookups, one frame" pass
on whatever the new failure is, rather than re-litigating `InsertItem`/`AddItem` again.

## 17. 2026-08-29, live pass — §16's own concrete next-test-step run; a NEW, more severe defect
## found: `AddItem` reliably CRASHES the host, 3/3 reproductions across two different resrefs and
## two separate rig sessions, at the exact same instruction — worse than the `InsertItem` no-op it
## replaced; stays OFF; a second, independent, real regression fixed along the way
## (`pooladd`/`poolremove` were never reachable from the real in-game console at all)

Task: this round's own queue item 1 — run §16's own closing "concrete next live-test step" first
(`pooladd` then `poolremove` of the same resref; `pooladd` x3 rapid host, x3 client) on the
two-instance rig, `party_pool_sync=1` both sides.

### 17.1 A prerequisite bug found before the live test could even start: `pooladd`/`poolremove`
### were never reachable from a real in-game keystroke — only from `blockpoke.ps1`'s own
### direct shared-block write

Typing `pooladd g_i_medeqpmnt01` at the live console (`tools/sendkey.ps1`, extended this round
with the digit/underscore scan codes resrefs need — see §17.5) produced a correctly-rendered
input line, a real submit (the console's own `[0x007A3A40]` open flag cleared on Enter,
confirmed via a raw `ReadProcessMemory` probe), and **zero** plugin-log output — not even the
"add failed" line `CheatController.PartyPoolAdd` unconditionally logs on refusal. Root cause,
found by reading `FuncInterpHook.cs`: its own `Verbs` allowlist (the ONLY gate deciding whether a
submitted console line reaches `CoopCommandDispatcher` at all, or falls through to the retail
engine's own `FuncInterp`, which has never heard of `pooladd`) never had `pooladd`/`poolremove`
added to it — the commit that added both verbs to `ConsoleCommand.cs`
(`fcdab50`, 2026-08-27) touched twelve files and never touched `FuncInterpHook.cs`. Every prior
round's own successful live use of `pooladd` (§9-§16) went through `blockpoke.ps1 -ProcId <pid>
console '...'`, which writes directly to the SAME shared-block field `FuncInterpHook`'s detour
would have written — bypassing the allowlist check entirely — so nobody had actually typed
`pooladd` at the real console until this round tried it. **Fixed**: `"pooladd", "poolremove"`
added to `FuncInterpHook.Verbs` (`src/KotorCoop.Plugin/FuncInterpHook.cs`), confirmed not to
shadow a retail verb the same way every prior addition to that list was checked. Live-confirmed
working immediately after rebuild/redeploy (§17.2's own repro used a real keystroke submit, not
blockpoke, and reached `CheatController.PartyPoolAdd` correctly).

**Not fixed this round** (found, not chased further, for lack of time budget once §17.2's crash
took priority): the same allowlist is also missing `grantxp`, `equip`, `grantgold`, `followkick`,
`partyselect`, `faction`, `destroyobject`/`destroy`, and `movenear` — none of these are marked
blockpoke-only in `docs/CONSOLE.md` the way `scriptgive`/`loottake`/`quittomenu`/etc. are, so each
is very likely unreachable from a real player's own keystroke today, the same way `pooladd` was.
Worth a dedicated pass to add the whole set at once and re-verify each live.

### 17.2 The real finding: `CItemRepository::AddItem`, called by `AddItemToPartyPool` exactly as
### §16 shipped it, crashes the host — 3/3 reproductions, two different resrefs, two separate rig
### sessions, including once via ordinary gameplay (not the manual `pooladd` cheat at all)

With the console fix in place, `pooladd g_i_medeqpmnt01` on the host (`party_pool_sync=1` both,
fresh two-instance session, `docs/research/enemy-presence-sync.md`-style clean `demo.ps1`
restart) produced the EXPECTED diagnostic line first —

```
party pool: add diag - repo 0x18649458, item_count 1->1, 0x133 IS in the post-add array walk,
itemLocal unchanged (fresh entry)
party pool: added 'g_i_medeqpmnt01' (0x133) x1, charges 0/0, upgrades 0x0
cheat: added 'g_i_medeqpmnt01' x1 to the party pool (status 1)
```

— immediately followed, within ~150ms, by:

```
FIRST-CHANCE access violation (0xC0000005) at 0x56E549, eip 0x56E549, esp 0x1AFC40
```

and the host process was confirmed gone (`Get-Process` returns nothing) a moment later. **This
reproduced 3/3 times**: a fresh rig restart with `pooladd g_w_thermldet01` (a different resref,
ruling out anything specific to the first key) crashed identically at the SAME address; a third
crash happened WITHOUT any manual `pooladd` at all — mid-`warp tar_m02aa`, something in the
transition/dialogue sequence auto-deposited two items into the pool (`g_w_blstrpstl001` then
`g_a_clothes01`, both logging a normal "added" line) and the host crashed immediately after the
second one, confirming this defect is reachable through party_pool_sync's own automatic apply
channel (`CheatController.PartyPoolAdd` is shared by both the manual `pooladd` verb and the
peer-apply path — see that file's own doc comment), not merely the manual staging cheat.
`tools/crashlog.ps1`, cross-checked against wall-clock timestamps, confirms all three as genuine
Windows Application-log crash events (`code=0xc0000005 offset=0x0016e549`, the module-relative
form of the same absolute address), not a plugin-log artifact.

**Root cause, static RE this round** (`tools/disasm.py`, `tools/q.py`): 0x56E549 is inside
`CSWSMessage::WriteRepositoryUpdate` (0x0056E360), called via `CSWSMessage::WriteOBJECTIDServer`
(0x00578570) — **the retail engine's own dead multiplayer network-message serialization code**,
never exercised in a real single-player session, reached from `AddItem`'s own merge-search loop
at `0x0055D598-0x0055D5B6` (`CSWSItem::MergeItem` returning 1 branches into the "notify" block
§16.4 already found and disassembled, `0x0055D5B6-0x0055D610`). The crashing instruction itself,
`mov dl, byte ptr [edi + 0x18]`, reads a bad/uninitialized pointer in `edi` — consistent with a
function that expects to be called only when a real networking session object exists, which
retail single-player KOTOR never constructs. **This directly contradicts §16.4's own reading**
that this notify block "only runs on a MERGE SUCCESS" being a narrow, rare case: this round's own
diagnostic reported `itemLocal unchanged (fresh entry)` — i.e. the code's OWN classification says
NO merge happened — on every one of the three crashes, meaning either `MergeItem` returns 1 for a
broader set of cases than "genuinely stacked into an existing item" (worth precisely nailing down
next round), or the notify block is reachable by some path this round's tracing missed. Either
way, the practical result is unambiguous and was not narrowed further this round given the
severity: **calling `AddItem` against the party pool's repository crashes the host essentially
every time an item lands in it**, not merely on some narrow edge case.

### 17.3 Severity: this is WORSE than the defect it replaced

§7-§16 spent nine rounds chasing a defect whose worst symptom was a silent no-op — annoying, but
safe to keep testing against. §16's own fix trades that for a **reliable host crash**, which is
categorically worse: it makes `pooladd`/`poolremove` themselves unsafe to use as this project's
own live-test primitives (the exact tools this document's own prior rounds relied on), and — per
§17.2's third repro — makes `party_pool_sync=1` unsafe to leave on during ordinary play if
anything at all gets deposited into the pool through the normal sync channel. **`AddItem` must
not ship as-is.**

### 17.4 Verdict and recommendation

`party_pool_sync` stays OFF (`profiles/coop.ini.example`/`AgentSettings.PartyPoolSyncEnabled`
unchanged) — now for a stronger reason than any prior round: not "unconfirmed," but a confirmed,
reproducible crash. **Do not re-attempt §16's `AddItem` switch without first either (a) finding
and calling whatever precondition genuinely initializes the network-message state
`WriteRepositoryUpdate` expects (unlikely to be a real single-player-safe option), or (b)
suppressing the notify block entirely** — e.g. a small inline patch/detour on
`CItemRepository::AddItem` itself that skips the `0x0055D5B6-0x0055D610` block unconditionally
(the party pool has no in-game UI panel for this notify to matter to; §16.4's own tracing already
established the ONLY thing that block does when it doesn't crash is a getter+notify pair this
project already treats as safe-when-unobserved elsewhere), or (c) reverting to `InsertItem` and
instead manually incrementing `item_count`/appending to `items_list` by hand from C#, bypassing
`AddItem`'s notify path altogether by not calling it at all. Whichever path the next round takes,
it MUST re-run this round's own 3/3 crash repro as its own regression pin before calling anything
fixed.

### 17.5 Tooling fixed along the way

`tools/sendkey.ps1` gained scan codes for `0`/`6`/`7`/`8`/`9` and a `TapShifted` primitive for
`underscore` (Shift+minus) — resref console arguments need both and neither existed before this
round. Confirmed working live (typed, not blockpoke'd, `pooladd g_i_medeqpmnt01` rendered
correctly character-for-character on screen). **Not a substitute for `blockpoke.ps1 console`**:
raw keystroke injection against this game remains flaky under synthetic `SendInput` in ways
`blockpoke.ps1`'s own header comment already documents ("injected keystrokes never reach this
game") — the native `~` toggle in particular did not reliably open on repeat presses this round,
while `blockpoke.ps1 console` was 100% reliable throughout. Use the keystroke path only when the
real end-to-end input path itself is what is under test (as it was for §17.1's own finding, which
blockpoke's bypass would have masked); prefer `blockpoke.ps1 console` for everything else.

### 17.6 Build/test evidence

`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors. `src/KotorCoop.Plugin/
build.ps1 -WarnAsError`: clean NativeAOT win-x86 build, both game directories freshly installed
and strings-confirmed (`pooladd`/`poolremove` present in both deployed `dinput8.dll`s via a
UTF-16LE `strings` scan). `dotnet test tests/KotorCoop.Tests --no-build`: 2410/2410 passed — the
`FuncInterpHook.cs` fix touches no C# logic under test (it is a native-only allowlist), so no new
tests were added for it; nothing regressed.

## 18. 2026-08-29, round 6 — a SAFE insert primitive: back to `InsertItem`, bracketed by a
## transient bit-0 clear, avoiding `AddItem` entirely; found, by fresh disasm, that the crash's
## real trigger is broader than §17 left it and that the task's own "known-safe" `AcquireItem`
## alternative is exposed to the identical defect; no rig this round, stays OFF

Task: §17's own closing recommendation (option (c) — stop calling `AddItem`'s notify path
altogether) plus a wider brief: disassemble every `CItemRepository::+0x08` bit-0 reader and
`CSWSMessage::WriteRepositoryUpdate`'s own gate, disassemble how the retail engine's own "give
item to a live party member" paths (`CSWSCreature::AcquireItem` 0x005158E0, the one `buy`
already uses per `docs/research/store-sync.md`) call the repository primitives, and ship whichever
primitive is actually safe. No rig this round (isolation worktree, matching every prior round's
own no-rig discipline) — static RE, source fix, and build/test verification only.

### 18.1 Every `CItemRepository::+0x08` bit-0 reader, VERIFIED by fresh disasm of the whole class

`tools/q.py sql "select * from functions where class_name='CItemRepository'"` lists 14 methods.
Ten were not previously disassembled for this specific question:
`CalculateContentsWeight`/`ClearItems`/`ClearNewItems`/`Destructor`/`FindItemWithBaseItemType`/
`FindItemWithTag`/`GetItemInRepository`/`ItemListGetItem`/`ItemListGetItemObjectID`/`SetParentId`.
Fresh disasm of each this round: **none of them reads or writes `+0x08` at all** — every `+0x08`
touch anywhere near these functions is unrelated (`appMgr+0x08` = `serverApp`, a completely
different structure at a completely different base address). Combined with `InsertItem`'s own
top-of-function bail-out (§16.1), `AddItem`'s own several reads (§16.4/§18.2 below), and
`Constructor`'s own write (§16.2), **the complete, exhaustive list of `CItemRepository::+0x08`
bit-0 touches in this binary is: `Constructor` (writes it), `InsertItem` (reads it once, as an
unconditional bail-out), `AddItem` (reads it three separate times, none of which is the actual
crash gate — see §18.2).** `RemoveItem` (§1.3, previously disassembled) also does not touch it.

### 18.2 `AddItem`'s own three `+0x08` reads, and why NONE of them gates the crash

Full fresh disasm of `AddItem` 0x0055D330 (`tools/disasm.py 0x55d330 1350`, VERIFIED, 428 lines —
extending §16.4's own partial trace to the complete function body):

1. `0x0055D4C7 test byte ptr[ebx+8],1 / je 0x55d4e7` — bit-0-SET only: formats the item's own
   name into a scratch buffer (`0x5ea130` on `[edi]+0x280`, `[edi]` = the incoming `*ppItem`).
   Display-only; no crash-adjacent call.
2. `0x0055D523 test byte ptr[ebx+8],1 / je 0x55d578` — inside the `wantMerge==1` merge-search
   loop: bit-0-SET only, does a SECOND, name-based "already have one" pre-check before the
   loop's own unconditional match test (see item 3). Bit-0-CLEAR repositories skip straight to
   that unconditional check instead.
3. `0x0055D57A-0x0055D59B` (**unconditional, NOT gated by `+0x08` at all**): compares the
   iterated candidate's `+0xc`/`+0x264` fields against the incoming item's own; on a match, calls
   `CSWSItem::MergeItem` 0x00553F90, and `cmp eax,1 / je 0x55d5b6` jumps into the notify block
   (0x0055D5B6-0x0055D610: resolves a message object via `CServerExoApp::GetSWSMessage`
   0x004AEDB0, calls `CSWSMessage::SendServerPlayerItemUpdate_DestroyItem` 0x00569FC0 — VERIFIED
   by symbol name this round, correcting §16.4's own "not traced past that" reading) — **this is
   the ONLY place `AddItem` itself reaches network-message code, and it fires for ANY repository
   (store, creature backpack, party pool) on a genuine template match + successful merge,
   completely independent of the party pool's own `+0x08` bit.** §16.4's framing of this as a
   party-pool-specific concern was itself imprecise; corrected here.
4. `0x0055D6EE test al,1 / je 0x55d776` (`al` = incoming item's own `+0x08` byte, a DIFFERENT
   object's flags than the repository's — a coincidental same-offset field on `CSWSItem`, not a
   second repository check): bit-0-SET only, resolves the local player
   (`0x004AE920`) and calls `CSWSItem::SetPossessor` 0x00553210 — the possessor-write branch §16.4
   already found and this round's fix reproduces by hand (§18.4).

None of these four reads is what actually crashed 3/3 in §17's own live rig, because §17's own
diagnostic showed **no merge occurred** on the logged call (`itemLocal unchanged (fresh entry)`)
— see §18.3.

### 18.3 The REAL trigger: `markNewItem`, not the merge notify — `WriteRepositoryUpdate` runs from
### a completely separate, later, per-tick network sweep, with an unguarded null-deref

`AddItem`'s tail (0x0055D786-0x0055D7A7, unconditional on `+0x08`, gated only by the caller's own
`markNewItem` argument — `[ebp+0x14]`): on ANY successful insert, if `markNewItem != 0`, ORs bit 7
into the item's own `+0x288` byte (the NewItem flag `RemoveItem`, §1.3, already documents clearing
on removal) and increments the repository's own `+0x04` "new item" counter. Both this codebase's
prior call and `CSWSCreature::AcquireItem`'s own call (§18.5) pass `markNewItem=1`.

`tools/xrefs.py 0x56e360` (`CSWSMessage::WriteRepositoryUpdate`, the function that actually
crashes at 0x0056E549) lists exactly two callers, both inside
`CSWSMessage::WriteGameObjUpdate_MajorGUIPanels` 0x00577670, itself called from
`CSWSMessage::SendServerToPlayerGameObjUpdate` 0x00578200 — VERIFIED, `tools/q.py`'s own function
table names both. **`AddItem` never calls `WriteRepositoryUpdate` itself, directly or indirectly
— it only arms the "new item" bookkeeping that this SEPARATE, per-player game-object network sync
sweep later consumes.** This matches §17.2's own observed symptom exactly: every one of the 3/3
crashes was preceded by a LOGGED, successful, non-merging add (`itemLocal unchanged (fresh
entry)`), with the crash itself arriving separately — immediately in one case, ~150ms later in
the other two — consistent with a later sweep pass picking up the just-armed NewItem bookkeeping,
not with the in-function merge-notify block (which never ran, per the diagnostic's own report).

Fresh disasm of `WriteRepositoryUpdate` 0x0056E360 (`tools/disasm.py`, 251 instructions across
two dumps) finds the actual defect: it walks the repository's own "new item" list via a helper at
0x005CA520, resolves each entry's item id via `ItemListGetItemObjectID` 0x005560E0 +
`GetGameObjectByID` 0x004AE760 into `edi`, and at 0x0056E549 —
`mov dl, byte ptr [edi + 0x18]` — dereferences `edi` **with no null/validity check anywhere on
the immediately preceding branches**: `edi` is zero-initialized at function entry and only ever
reassigned inside a conditional block gated on the helper's own returned index being non-negative;
neither that reassignment nor the `ecx==0` branch a few lines earlier (`0x0056E3FE`, a DIFFERENT
local) actually tests `edi` itself before the dereference. **This is a genuine, unconditional
retail defect in dead multiplayer network-sync bookkeeping** — armed the instant `markNewItem=1`
is passed to `AddItem` (or, per §18.6, previously also live-fired via other engine paths that
mark items new), independent of whether a merge ever happens.

### 18.4 Why the task brief's own "known-safe" `AcquireItem` alternative does NOT avoid this

Fresh full disasm of `CSWSCreature::AcquireItem` 0x005158E0 this round (previously never traced):
its own "give to the buyer's/PC's own inventory" branch (target `== OBJECT_INVALID`, the ordinary
case) resolves the repository via `GetItemRepository(creature, 1)` 0x004EF770 exactly as
`docs/research/store-sync.md` already documented, then calls `AddItem` with the LITERAL argument
shape `(ppItem, wantMerge=1, arg3=edi=0, markNewItem=1)` —
**byte-for-byte identical to this codebase's own prior `AddItemToPartyPool` call.** `edi` is
proven 0 in this branch by an `xor edi,edi` at function entry never touched again before the call
(the alternate branch, a target OTHER than the caller's own creature, DOES set `edi` to a resolved
target creature pointer — out of scope here, since `AddItemToPartyPool` never uses that branch).

**Conclusion: `AcquireItem` is exposed to the identical `markNewItem=1` trigger (§18.3) and the
identical merge-notify trigger (§18.2 item 3) as the codebase's own prior direct `AddItem` call.**
It is not a safer primitive — the task brief's own framing ("known to work: `scriptgive` creates
items... without crashing") most plausibly reflects that `scriptgive`/`buy` testing never
happened to deposit a template-duplicate stack, and no round's live test window was ever held open
long enough afterward to catch a delayed `WriteRepositoryUpdate` sweep the way §17's own 150ms gap
did. Routing `AddItemToPartyPool` through `AcquireItem` instead of calling `AddItem` directly would
not have fixed anything.

### 18.5 The fix: `InsertItem`, bracketed by a transient `+0x08` bit-0 clear — avoids both triggers
### by construction, not by argument-tuning

Since neither of `AddItem`'s two crash triggers (§18.2 item 3, §18.3) is reachable through
`InsertItem`'s own append path (§16.1's own fully-verified disasm: grow-if-needed,
`items_list[count]=id`, `item_count++`, nothing else — no merge search, no NewItem bit, no
counter increment), and `InsertItem`'s ONLY defect against the party pool is its own top-of-
function bail-out on `+0x08` bit 0 (§16.1), the fix restores the pre-§16 call
(`CItemRepository::InsertItem`) but brackets it with a transient clear of that one bit for the
duration of the single call — `Odyssey.PartyPool.cs`'s new `WithRepoBit0Cleared(repo, action)`:
read the byte, clear bit 0, run the call, restore the ORIGINAL byte (not merely re-set bit 0,
preserving whatever else might be there) in a `finally`. Synchronous, MAIN THREAD ONLY, no engine
re-entrancy between the clear and the restore — two memory writes bracketing one native call, the
same discipline this codebase's own engine-write rules already require.

The possessor write `AddItem`'s own bit-0-set branch used to provide for free (§16.4,
0x0055D6FE-0x0055D728: `SetPossessor(item, resolvedPlayerId, arg3, arg3, OBJECT_INVALID)` with
`arg3=0` in every observed call) is now reproduced by hand:
`SetPossessorFn(item, playerObjectId, 0, 0, ObjectInvalid)`, reusing the caller's own already-known
player id rather than re-resolving it via `0x004AE920`. Net effect: the exact same array link and
the exact same possessor write a fresh (non-merging) `AddItem` deposit used to perform, with
neither crash trigger ever armed.

**Deliberate behavioural regression, by design, not oversight**: a deposit of a resref+identity
key that ALREADY has a matching entry in the pool no longer merges natively — it becomes a second,
discrete array entry (`InsertItem`'s append path has no merge concept at all). This is judged
acceptable, not merely tolerated, because this channel's own content-keyed aggregation
(docs/research/party-pool-sync.md §2.1) was ALREADY the source of truth for stack totals at the
store/wire layer — no existing code (`RemovePartyPoolStack`'s own Tag+identity matching, the
read-side `KotorGameBridge.PartyPool.cs` scan) depended on the engine's own native merge state for
correctness; both already treat the repository as a flat array of discrete entries to sum over.

`RepositoryAddItemFn`'s own delegate is kept declared, undisassembled-history preserved in its doc
comment, but is no longer called anywhere — left in place as a documented reference for whoever
next attempts to actually fix (rather than route around) `WriteRepositoryUpdate`'s own missing
null check, e.g. by patching/detouring 0x0056E549 itself. That is out of scope for this round: it
would require hooking retail code well outside anything this project's write-rules currently
sanction casually, for a benefit (native stack merging + the NewItem "!" UI indicator on a party-
pool deposit) this round judges not worth the risk against the working, if slightly less featureful,
`InsertItem`-based fix.

### 18.6 `InsertItemIntoStore` re-checked — still not on a replicated repo

Re-verified this round, fresh disasm of `CSWSStore::Constructor` 0x005C6AB0's own single call to
`CItemRepository::Constructor` (`0x005C6B44`): the flag (second) argument is `edi`, proven zero by
an `xor edi,edi` at function entry (`0x005C6AD6`) never touched again before the call. A store's
own repository is therefore still constructed with `+0x08` bit 0 CLEAR — confirming §16.3's own
finding independently, this round, from scratch — so `Odyssey.Store.cs`'s `InsertItemIntoStore`
needs no bit-clear bracket and is unchanged.

### 18.7 Files changed

`src/KotorCoop.Plugin/Odyssey.PartyPool.cs`: new `RepositoryFlagsOffset` constant (`0x08`, named
in place of the previous bare literal), new `WithRepoBit0Cleared(nint repo, Action action)`
helper, `AddItemToPartyPool` now calls `RepositoryInsertItemFn` inside that bracket instead of
`RepositoryAddItemFn` directly, followed by an explicit `SetPossessorFn` call; the "two lookups,
one frame" diagnostic is kept (item-count before/after, post-insert array-membership walk) with
the now-inapplicable `itemLocal`/merge-tracking removed (there is nothing for `InsertItem` to
repoint, since it takes a bare item pointer, not a `CSWSItem**`). Doc comments on
`RepositoryInsertItemFn` and `RepositoryAddItemFn` rewritten to carry this round's own findings
(§18.1-18.4) in place of §16's now-superseded reasoning. No protocol, session, memory, or agent
files touched — this is a native-plugin-only, single-file fix, matching §16's own scope.

### 18.8 VERIFIED / INFERRED / NEEDS-LIVE summary

| Item | Status |
| --- | --- |
| Every `CItemRepository::+0x08` bit-0 reader/writer across all 14 class methods | VERIFIED, fresh disasm this pass (§18.1) |
| `AddItem`'s three own `+0x08` reads, none of which gates the crash | VERIFIED, full fresh disasm (§18.2) |
| The merge-notify block calls `GetSWSMessage`/`SendServerPlayerItemUpdate_DestroyItem`, fires for ANY repository on a real merge, independent of `+0x08` | VERIFIED, fresh disasm + symbol names (§18.2 item 3) |
| `markNewItem=1` unconditionally arms the NewItem bit + repo counter on every successful insert | VERIFIED, fresh disasm (§18.3) |
| `WriteRepositoryUpdate` is called ONLY from the per-player network sync sweep (`WriteGameObjUpdate_MajorGUIPanels`/`SendServerToPlayerGameObjUpdate`), never from `AddItem` itself | VERIFIED, `tools/xrefs.py` + fresh disasm (§18.3) |
| The crash instruction (0x0056E549) dereferences an unchecked, possibly-null `edi` | VERIFIED, fresh disasm (§18.3) |
| `CSWSCreature::AcquireItem`'s self-deposit branch passes the IDENTICAL argument shape as this codebase's prior direct `AddItem` call, and is exposed to the same two triggers | VERIFIED, fresh disasm (§18.4) |
| `InsertItem`'s append path touches neither the NewItem bit, the repo counter, nor any merge/notify code | VERIFIED, re-confirmed against the full fresh disasm (§18.5, building on §16.1) |
| `CSWSStore::Constructor`'s own repository flag argument is proven zero | VERIFIED, re-confirmed fresh disasm this round (§18.6, independent of §16.3) |
| The fix (`InsertItem` + transient bit-0 clear + explicit `SetPossessor`) avoids both known crash triggers | Implemented, build clean; **NEEDS-LIVE** — no rig this round, matching §16's own original verdict before §17's live pass found the `AddItem` regression |
| Whether a duplicate-resref deposit reads correctly as two discrete entries end-to-end (spawn, unlink, insert, read-side scan, wire sync) | NEEDS-LIVE — the array-level mechanics are verified in isolation; a full live deposit was not re-run this round |

### 18.9 Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled`/`profiles/coop.ini.example`
unchanged) — this round produces a build-clean, fully-reasoned-from-disassembly fix for §17's own
confirmed 3/3 crash, but, like §16 before the crash was found, has NOT been live-tested against a
real rig. **The concrete next live-test step, exactly matching §17.4's own closing ask, now
finally on the primitive believed actually safe**: re-run §17's own 3/3 reproduction
(`pooladd g_i_medeqpmnt01`, then `pooladd g_w_thermldet01` fresh restart, then the `warp
tar_m02aa` auto-deposit path) and confirm the host survives all three; additionally, specifically
re-run a deposit of a resref ALREADY present in the pool (the case that used to reach `AddItem`'s
own merge-notify block) to confirm §18.5's "becomes a second discrete entry" prediction reads
correctly end-to-end, not merely in the diagnostic log line. If any of these still crash, the
failure has moved past everything §16-§18 collectively cover and is worth a fresh instrumented
pass on `WriteRepositoryUpdate`'s own callers rather than re-litigating `InsertItem`/`AddItem`
again.

`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors. `src/KotorCoop.Plugin/
build.ps1 -WarnAsError`: clean NativeAOT win-x86 build (dinput8.dll, 2006 KB). `dotnet test
tests/KotorCoop.Tests --no-build`: 2410/2410 passed, unchanged from the pre-fix baseline — the fix
touches only native delegate targets and their bracketing logic, no C#-testable pure logic was
added or changed (matching §16.7's own note that this class of fix has nothing new to unit-test in
isolation).

## 19. 2026-08-28, rig round — §18's `InsertItem` + transient bit-0 clear fix re-run live: the
## 3/3 crash reproduction is CLOSED. No crash across a deposit/remove cycle, a warp transition,
## rapid multi-add on both host and client, pathological ambient-script stack sizes (x65535,
## x6783, x38937), and a 10 s full blackout + reconnect. `party_pool_sync` recommended ON, held
## back only because a same-key remove edge case needs one more pass

Task: this round's own queue item 1 — the exact re-test §18.9 asked for (`pooladd
g_i_medeqpmnt01`, `pooladd g_w_thermldet01`, the `warp tar_m02aa` auto-deposit path, plus
`poolremove`, rapid ×3 both sides, deposit-then-immediate-remove, and a blackout/reconnect),
two-instance rig, `party_pool_sync=1` both sides, `store_sync=0` for this item (isolated per
item 2's own established discipline).

### 19.1 Crash repro: CLOSED, 3/3 (and then some)

`pooladd g_i_medeqpmnt01` on the host (`item_count 2->3`, real player-facing deposit already in
flight from an unrelated ambient dialogue grant at `item_count 1->2` moments earlier) produced no
access violation; a 60 s watch afterward showed both `swkotor.exe` processes alive, no
`FIRST-CHANCE`/`fatal` line in the plugin log, only ordinary gait-sync/pool traffic. A `warp
tar_m02aa` transition immediately after — while the pool held content and while the ambient
module's own script machinery auto-deposited `g_i_medeqpmnt01` twice more (`x9`, then `x12`, both
via `CheatController.PartyPoolAdd`'s automatic-apply path, not the manual verb) — produced no
crash either; both processes confirmed alive post-transition. This is the exact scenario §17.2's
third repro crashed on (an auto-deposit during a `warp`), now clean with §18's fix in place.

**Stress case beyond the original ask**: `netsim blackout 10` on the client, reconnect, both
sides confirmed `connected ... liveness anomalies: 0` afterward. During and around the outage
window the ambient module's own duros-registration script fired a script bug independent of this
codebase (`stack 65535`, `stack 6783`, `remove ... x38937`, `add ... failed - did not spawn` for
`k_computer_spike` — classic KOTOR retail script sentinel/overflow values, not a coop-mod
artifact) — every one of these pathological adds/removes landed through the exact same
`InsertItem`-bracketed path with zero crashes, which is a harder stress test than this round's own
plan called for and the fix held under it.

### 19.2 `poolremove` — found for a fresh key, NOT found for one of two auto-deposited entries;
### narrowed to an identity-key mismatch, not a repository/crash defect

`poolremove g_i_medeqpmnt01` (the resref auto-deposited moments earlier by the ambient script)
refused `no matching stack found in the pool`, while an isolated manual pair —
`pooladd g_w_thermldet01` immediately followed by `poolremove g_w_thermldet01` — succeeded
cleanly (`removed 'g_w_thermldet01' x1 (asked for 1)`, status 1). Since `poolremove`'s own key has
no charge/upgrade/flag arguments (it always searches the same implicit all-default identity), the
most likely explanation is that the auto-deposited entry's real identity fields (set by whatever
script/engine path actually granted it) do not match `poolremove`'s assumed default key — the
match is by Tag AND identity together (`docs/research/party-pool-sync.md` §2.3), so a resref match
alone is not sufficient. Not root-caused further this round (the manual add/remove pair, which
controls for this exactly, worked); flagged for whoever next touches `poolremove`'s own key
defaults.

### 19.3 Rapid multi-add, both sides: no doubles, no under-count, on the issuing side's own log

Three back-to-back `pooladd g_i_progspike01` on the HOST: `item_count 6->7->8->9`, three separate
log lines, each its own discrete entry (matches §18.5's documented "no native merge, becomes a
second discrete entry" design) — no skipped increments, no double-application. Three back-to-back
`pooladd g_i_progspike01` on the CLIENT: all three console replies independently confirmed
`added ... x1` (status 1); the client's own plugin log around the same window is heavily
interleaved with the wire mirror of the host's own deposits AND a second wave of the ambient
script's pathological adds/removes (`x20`, `x1`, `removed x7`, `x1`), which made an exact
cross-side aggregate reconciliation impractical to read cleanly this round — but every individual
add this round issued was independently acknowledged and no crash or duplicate-id insert was
observed at any point.

### 19.4 Deposit then immediate remove: net 0, confirmed

Covered by 19.2's own clean pair (`pooladd g_w_thermldet01` -> `poolremove g_w_thermldet01`,
same resref, back to back): the remove found and removed exactly the deposited unit
(`removed x1 (asked for 1)`), net effect zero, no crash.

### 19.5 Verdict

`party_pool_sync`'s crash defect (§17/§18) is **CLOSED, live-confirmed** — the fix ships with
significantly more confidence than §18's own "NEEDS-LIVE, no rig this round" left it. **Not yet
flipped ON by default**: §19.2's `poolremove` identity-mismatch gap is real and unexplained, and
this round's rapid-multi-add cross-side reconciliation could not be read cleanly enough (ambient
module noise) to positively confirm "no under/over-count" at the AGGREGATE wire level, only at the
per-call level — both are narrower, lower-severity gaps than a crash, but this project's own bar
for flipping a default is a decisive, clean A/B, which the ambient module's own concurrent script
noise prevented this round from delivering as cleanly as the recipe intended. Recommend testing
next in a module without an active ambient item-granting script (or waiting out/clearing that
script's own triggers before staging the pool test) to get a clean aggregate reconciliation
reading, then flip ON.

Build: `dotnet build KotorCoop.slnx -warnaserror` clean, `src/KotorCoop.Plugin/build.ps1
-WarnAsError` clean (both game directories reinstalled and strings-confirmed), `dotnet test
tests/KotorCoop.Tests --no-build`: 2413/2413 passed. No source change this round — this is a live
re-verification pass only.

## 20. 2026-08-28 — robustness pass on §19's two open gaps: the `poolremove` identity-key
## mismatch fixed with a wildcard rule; ambient-script party-pool deposits now suppressed at
## the source instead of merely surviving them; no rig this round, stays OFF

Task: make the content-keyed multiset key robust against exactly the two shapes §19 left open —
(1) §19.2's own live "identity-key mismatch" (a bare `poolremove` refusing an ambient-deposited
stack), and (2) the ambient module's own item-granting script noise that made §19.3's cross-side
aggregate reconciliation unreadable in the first place. No rig this round (isolation worktree) —
root-cause audit, source fix, and build/test verification only, matching every prior no-rig
round's own discipline.

### 20.1 Root cause 1 (§19.2's "identity-key mismatch"): `poolremove`'s console verb has no way
### to name charges/upgrades/flags, and coercing "unspecified" to a literal 0 demanded an exact
### match nothing but a freshly-constructed item has

Audit of every field `PartyPoolStackKey` carries against what `RemovePartyPoolStack`/
`StackIdentityMatches` actually compared (this task's own item 1 ask): the AUDIT itself came back
clean on the shape that matters most — stack size was ALREADY excluded from identity comparison
(`StackIdentityMatches` never read `fields.StackSize`), matching `PartyPoolStackKey`'s own design
(§2.1: resref + the four non-quantity `ItemIdentity` fields, count handled entirely outside the
key). The real defect was narrower and exactly where §19.2 left it: `poolremove <resref> [stack]`
(`ConsoleCommand.Items.cs`'s `ParsePoolRemove`) has no syntax for charges/upgrades/flags at all,
so every one of those fields reaches `CheatController.PartyPoolRemove` as
`SharedBlock.CheatFieldUnchanged` — which the OLD code coerced to a literal `0` before calling
`RemovePartyPoolStack`. An ambient script's own deposit essentially never has all-zero identity
(`Charges` defaults to 50 on a freshly loaded item, per docs/research/item-identity.md §1's own
`CSWSItem::Constructor` citation), so a bare `poolremove` demanded an exact match against a value
the depositing side never had reason to produce.

**Fix.** `CheatFieldUnchanged` now reaches `RemovePartyPoolStack` UNCOERCED, and
`StackIdentityMatches` (via the new pure `PartyPoolIdentityMatcher.Matches`,
`src/KotorCoop.Plugin/PartyPoolIdentityMatcher.cs`) treats it as a per-field WILDCARD — "match any
value for this field" — rather than a literal request for `0xFFFFFFFF`. This only changes a
human's own bare `poolremove <resref>`: the wire-driven apply
(`CoopCommandDispatcher.DrainAutomaticPartyPool`) always supplies concrete fields straight from a
scanned `PartyPoolStackKey`, which are byte-ranged (Charges/MaxCharges/Flags) or a real upgrade
mask and can never coincidentally equal the sentinel in practice — the same accepted risk profile
`CheatFieldUnchanged` already carries for every other item-identity cheat channel in this codebase
(SetItem, Store).

**A second, closely-related fix in the same audit.** `Odyssey.PartyPool.cs`'s
`ReadItemFields`/`StackIdentityMatches` read Charges/MaxCharges as bare `uint`s straight off the
live item — UNLIKE `KotorCoop.Memory.ItemIdentityLayout.Decode` (the scan side that builds the
wire `PartyPoolStackKey` in the first place), which already clamps a garbage MaxCharges (above the
0..255 GFF-byte range — docs/research/item-identity.md §1.1's own "charges 50/1768092" measured
example) down to the Charges value. This asymmetry meant a wire-driven remove, built from a scan
that safely clamped a garbage field, could never match the SAME live item again on a later read,
because the live re-read would still show the raw garbage the scan had already repaired. Fixed by
duplicating the identical clamp (`PartyPoolIdentityMatcher.SafeChargeFields` — the plugin cannot
reference `KotorCoop.Memory`, NativeAOT win-x86 vs the agent's x64, so this is the same
project-wide "verified offset, independently re-derived on each side of the boundary" pattern
`TagEqualsIgnoreCase`/`EngineBitsToWireFlags` already use) on the plugin side, so both ends of a
scan-then-remove round trip derive the identical key for the identical item.

**Tests.** `PartyPoolIdentityMatcher` is a new pure, unsafe-pointer-free class (compiled into
`KotorCoop.Tests.csproj` per this repo's "plugin pure arithmetic" convention, matching
`PartyPoolStackMatcher`'s own precedent) — nine new `[Fact]`/`[Theory]` cases in
`PartyPoolTests.cs`: an exact match, a single-mismatched-field refusal, every-field-wildcarded
matching any live value (the direct §19.2 regression pin), a mix of wildcard and concrete fields,
the charge clamp on both an out-of-range MaxCharges and an out-of-range Charges, in-range values
passing through untouched, and the load-bearing "a key derived from a clamped scan still matches
the same live item's raw garbage MaxCharges on a later read" case.

### 20.2 Root cause 2 (§19.3's unreadable aggregate — ambient scripts deposit into this exact
### repository, and BOTH instances run the same scripts, so a naive publish doubles them)

KOTOR's own `k_*` heartbeat/plot scripts can call `CreateItemOnObject(resref, GetFirstPC())`,
which — for the local player specifically — deposits into the SAME `CItemRepository` this whole
channel scans (§19.1/§19.3's own live evidence: the ambient duros-registration script's adds/
removes landed through the identical `InsertItem`-bracketed path party-pool test traffic used, and
were visible IN the party-pool test window). Both instances run the identical script content, so
each side creates its OWN separate copy of the same deposit — if this pump published either side's
detection as an ordinary local player action, the merged total would double it, exactly the
`ContainerLootStore`-vs-multiset distinction §2.1 already draws (these are NOT removed-pair facts
safe to re-declare; they are cumulative counts).

**Why suppression can't be resref-keyed like `RecentlySyncedItems`.** That mechanism (used by
`ScriptItemGrantRule` to stop a script's OWN inventory grant from being double-reported alongside
an already-synced pickup/loot/giveloot) works because those channels explicitly WANT the script
grant published — the question there is only "was this specific row already accounted for by
something else." Here the opposite is true: an ambient party-pool deposit must NEVER be published
at all (both sides make their own), so a resref-keyed consume-on-match budget is the wrong shape —
what is needed is a durable "was this item's OWN creation recent and NOT something our own code
did" fact, checked once per item id, forever.

**Why the distinction can only come from the CALLER, not the hook site.** The task's own leading
question — can `ItemTemplateSpawnHook` derive "VM script vs our cheat" itself — has a definite
negative answer, re-confirmed this round: `Odyssey.GiveItemToInventory` (which
`AddItemToPartyPool`, `SetItem`, `GiveLoot`, `EquipItemFromTemplate` and `storeinsert` all funnel
through) reaches `CSWVirtualMachineCommands::ExecuteCommandCreateItemOnObject` — the EXACT same VM
command a vanilla script's own `CreateItemOnObject` call reaches
(docs/research/item-identity.md §13.1's own citation) — so nothing observable AT the hook site
(0x0052F748, mid-function, well past any caller-identifying context) can tell the two apart. The
only place that genuinely knows is the CALLER, at the instant it makes the call.

**Fix: a per-entry cheat flag on the item-template-spawn ring (SharedBlock version 59).**
`ItemTemplateSpawnHook.NextSpawnIsCheat` (`src/KotorCoop.Plugin/ItemTemplateSpawnHook.cs`) is a
static bool, bracketed tightly (`try`/`finally`) around the ONE VM call in each of
`Odyssey.GiveItemToInventory` and `Odyssey.EquipItemFromTemplate` that reaches
`ExecuteCommandCreateItemOnObject` — the two, and only two, call sites in this plugin that reach
it (`grep`-verified). The hook's own capture (`OnItemTemplateSpawn`) reads this flag at the exact
instant it fires and stores it alongside the (object id, resref) pair it already captured. The
ring's own entry stride grew from 0x14 to 0x18 (`SharedBlock.ItemTemplateSpawnIsCheatOffset`,
bit 0 = cheat) to carry it; `SharedBlock.TotalSize` grew from 0x4700 to 0x4800 to hold the wider
ring. `PluginHost.cs`'s `_block->Version` and `KotorGameBridge.PluginSignature`'s own version byte
both moved from 58 (0x3A) to 59 (0x3B) together — `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`
and `TransitionMovieTests.TheBlockVersionMovedInBothHalves` both updated and re-passing.

**Agent side.** `KotorGameBridge.ItemTemplateSpawns.cs`'s `DrainItemTemplateSpawns` now records,
for EVERY drained ring entry (not only cheat ones), an `ItemCreationObservation` (isCheat, the
AGENT's own wall-clock time it learned the fact — a faithful proxy for true creation time, since
the drain runs unconditionally every tick per docs/research/item-identity.md §13.2, so the
observation lags true creation by at most one tick). `KotorGameBridge.PartyPool.cs`'s scan then
calls `IsAmbientPartyPoolNoise(itemId, now)` per live pool item: if a creation observation exists,
is NOT cheat-flagged, and was observed within 1000ms of the FIRST time this scan ever looked at
this id, the item's stack is excluded from the aggregate entirely — permanently, not just for that
tick.

**Why the decision is made ONCE, not re-checked every scan.** This is the load-bearing design
choice, not an afterthought: if suppression were re-evaluated on every pass, an item correctly
excluded on tick 1 (fresh, non-cheat) would start being COUNTED the moment the 1-second window
elapsed on some LATER tick — and that "sudden appearance" would read to `Pumps.PartyPoolPump`'s
own step-1 diff as a brand-new genuine local change, reproducing the exact double-count bug this
section exists to prevent, merely delayed by however long the item sat unsuppressed before the
window ran out. Framed the way §2.1 already frames every other design choice in this channel: an
ambient item that is never counted is, from the sync layer's perspective, simply not part of the
multiset at all, on either side, ever — correct, because both instances independently hold their
own equivalent physical copy without any wire op ever being needed to converge them. The decision
(`_partyPoolSuppressionDecided`/`_partyPoolAmbientSuppressed`, `KotorGameBridge.PartyPool.cs`) is
therefore cached per item id and never revisited, cleared only on a module change (object ids are
module-scoped) or a generous 4096-entry defensive cap past the repository's own hard count limit.

**What this does NOT suppress.** A human's own `pooladd` (manual console verb) and the automatic
wire-apply channel (`CoopCommandDispatcher.DrainAutomaticPartyPool`, replaying a PEER's own
change) both route through `AddItemToPartyPool` -&gt; `GiveItemToInventory`, which is now
bracketed — so both are marked cheat and never suppressed, exactly the "player-driven pool moves…
publish" half of this task's own ask. A GUI drag-and-drop of an item that has been sitting in a
backpack for any length of time was never freshly created at the moment it entered the pool, so it
falls outside the 1-second window and is counted normally regardless of how it was originally
created. The suppression is also purely additive on top of the existing read: with no plugin
attached, `TryGetItemCreationObservation` never has an answer, `IsAmbientPartyPoolNoise` always
returns false, and every item counts exactly as it did before this round.

**Tests.** `PartyPoolAmbientSuppressionTests.cs` (new file, eight `[Fact]`s): no creation fact
never suppresses; a fresh non-cheat creation IS suppressed; a fresh CHEAT creation is never
suppressed (the load-bearing "our own pooladd must not be mistaken for ambient noise" case); a
non-cheat creation outside the window is not suppressed; a suppression decision is PERMANENT even
once the window would have expired (the regression pin for the "re-checking would reproduce the
bug on a delay" reasoning above); a not-suppressed decision is equally permanent; different item
ids are decided independently; and the window boundary itself is exclusive (`<`, not `<=`),
matching `RecentlySyncedItems`' own comparison convention. Seeded through
`KotorGameBridge.RememberItemCreation`/`IsAmbientPartyPoolNoise(id, nowMs)` — both `internal`,
granted to the test assembly the identical way `RememberSpawnedItemResref` already is — with
`nowMs` threaded through explicitly rather than read from `Environment.TickCount64`, so the window
boundary is pinned deterministically rather than raced against a real timer.

### 20.3 Stack-size clamp/validation (this task's own tail ask)

Re-confirmed rather than re-implemented: `ItemIdentityLayout.Decode` (the scan side) already
`Math.Clamp`s a raw stack-size read to `[1, ushort.MaxValue]` before it is ever aggregated — the
type itself (`ItemIdentity.StackSize` is a `ushort`) makes "clamp to 65535" the natural ceiling
already in force, not a change this round needed to make. What WAS missing is a way to notice a
pathological value without a live-attach probe: `Pumps.PartyPoolPump` now logs a one-time-per-key
diagnostic (`_warnedSuspiciousStackSize`, threshold 99 — nothing in base-game KOTOR content or
scripting stacks past a few dozen of anything) whenever a key's total exceeds it, naming the exact
values §19.1 observed live (65535/6783/38937) as the expected shape rather than a mystery. This
never changes what gets published or applied, only whether the pump SAYS something happened.
Deliberately NOT implemented: reading a `.uti` template's own max-stack field via `GameFiles` — no
verified GFF field name for a per-item stack cap exists in this codebase's own research
(docs/research/item-identity.md's own §"What is deliberately NOT carried" catalogues every `.uti`
field this project has actually disassembled a save/load routine for, and a per-item numeric stack
ceiling is not among them); encoding an unverified field name would violate this project's own
"VERIFIED, not guessed" bar for a research doc claim, for a diagnostic the simpler one-time log
already serves. **Tests**: three new `[Fact]`s in `PartyPoolLoopbackTests.cs` — a suspiciously
large stack (65535, the exact live-observed value) logs exactly once across three ticks holding it
steady; an ordinary stack (5) never logs it; and two distinct oversized keys each get their own
independent one-time warning.

### 20.4 Files changed

`src/KotorCoop.Plugin/PartyPoolIdentityMatcher.cs` (new, pure): the wildcard-aware identity match
rule and the charge clamp. `src/KotorCoop.Plugin/Odyssey.PartyPool.cs`: `StackIdentityMatches`
delegates to it. `src/KotorCoop.Plugin/CheatController.PartyPool.cs`: `PartyPoolRemove` no longer
coerces `CheatFieldUnchanged` to 0. `src/KotorCoop.Plugin/ItemTemplateSpawnHook.cs`:
`NextSpawnIsCheat`, the `IsCheat` capture array, `Read`'s widened signature.
`src/KotorCoop.Plugin/Odyssey.Equipment.cs`: brackets both `CreateItemOnObject` call sites.
`src/KotorCoop.Plugin/ItemTemplateSpawnController.cs`,
`src/KotorCoop.Plugin/SharedBlockView.ItemTemplateSpawns.cs`,
`src/KotorCoop.Plugin/SharedBlock.ItemTemplateSpawns.cs`, `src/KotorCoop.Plugin/SharedBlock.cs`,
`src/KotorCoop.Plugin/PluginHost.cs`: the ring's widened stride and the version 59 bump.
`src/KotorCoop.Memory/KotorGameBridge.cs`: `PluginSignature`'s version byte.
`src/KotorCoop.Memory/KotorGameBridge.PluginBlock.cs`: the mirrored offset/stride constants.
`src/KotorCoop.Memory/KotorGameBridge.ItemTemplateSpawns.cs`: the new `ItemCreationObservation`
cache. `src/KotorCoop.Memory/KotorGameBridge.PartyPool.cs`: `IsAmbientPartyPoolNoise` and its
call from the scan loop. `src/KotorCoop.Memory/KotorGameBridge.Player.cs`: clears both new caches
on module change. `src/KotorCoop.Session/Pumps/PartyPoolPump.cs`: the suspicious-stack-size
diagnostic. Tests: `tests/KotorCoop.Tests/PartyPoolTests.cs` (nine new identity-matcher cases),
`tests/KotorCoop.Tests/PartyPoolAmbientSuppressionTests.cs` (new file, eight cases),
`tests/KotorCoop.Tests/PartyPoolLoopbackTests.cs` (three new stack-size-diagnostic cases),
`tests/KotorCoop.Tests/TransitionMovieTests.cs` (the version-59 expectation),
`tests/KotorCoop.Tests/MidModuleMovieRelayTests.cs` (the TotalSize 0x4800 expectation),
`tests/KotorCoop.Tests/KotorCoop.Tests.csproj` (the new compile-include).

### 20.5 Verdict

`party_pool_sync` stays OFF (`AgentSettings.PartyPoolSyncEnabled`/`profiles/coop.ini.example`
unchanged) — this round is a build-clean, fully-reasoned source fix for both of §19's own closing
gaps, but, like every round since §16 before a rig confirmed it, has NOT been live-tested. The
concrete next live-test step: re-run §19's own recipe (a module with an active ambient
item-granting script, `pooladd`/`poolremove` on both sides, a rapid multi-add burst) and confirm
(a) `poolremove <resref>` now succeeds against an ambient-deposited stack with no charges/upgrades
named, (b) the party-pool test's own aggregate reconciliation is finally readable — no ambient
duros-registration-style noise interleaved with genuine test traffic in either side's log — and
(c) the suspicious-stack-size diagnostic fires for any pathological value the ambient script still
produces, confirming it is cosmetic (a log line) and not a behavioural change. If (b) still cannot
be read cleanly, the suppression's own 1-second window or its "decide once" permanence is the next
thing to instrument, not the identity-matcher fix, which (a) can confirm independently.

Build: `dotnet build KotorCoop.slnx -warnaserror` clean, `src/KotorCoop.Plugin/build.ps1
-WarnAsError` clean (NativeAOT win-x86, 2008 KB). `dotnet test tests/KotorCoop.Tests --no-build
--filter "FullyQualifiedName~PartyPool"`: 61/61 passed. Full `dotnet test tests/KotorCoop.Tests
--no-build` regression sweep: 2435/2435 passed (one pre-existing, unrelated test —
`MidModuleMovieRelayTests.TheRegionDoesNotOverlapAnyExistingField` — needed its own hardcoded
`SharedBlock.TotalSize` expectation updated from 0x4700 to 0x4800 for this round's version-59
growth; fixed alongside `TransitionMovieTests.TheBlockVersionMovedInBothHalves`'s own hardcoded
58).
