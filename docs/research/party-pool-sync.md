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
