# Merchant/store inventory sync (research + build, 2026-08-27)

Roadmap Tier 3 item 20 (promoted to Tier 1): `CSWSStore`'s item repository is independent per
instance. A limited-stock item bought on one instance is still for sale on the other; an item
sold to a merchant on one instance never appears in the other's copy of that same shop. Gold
already converges via the ledger (`docs/research/progression-sync.md`) — this document is about
the STOCK, not the credits.

Tag legend, matching every other doc in this directory: **VERIFIED** = read out of the
sha256-matched GOG retail `swkotor.exe` v1.0.3 instruction stream (`tools/disasm.py`) or the
symbol database (`tools/q.py`) cross-checked against it. **NEEDS-LIVE** = reasoned from that
evidence but never watched happen on a running game. **INFERRED** = a reasonable deduction from
code that was read, not a directly observed fact. Nothing in this document was exercised against
a live process this pass — static RE, design and implementation only.

Read `docs/research/party-pool-sync.md` first — the `CItemRepository` primitives this build
reuses verbatim (`InsertItem 0x0055D2C0`, `RemoveItem 0x00555FD0`) and the content-keyed op-log
shape were established there — and `docs/research/item-identity.md` for the five per-instance
item fields (`Charges`/`MaxCharges`/`Upgrades`/three flag bits/`StackSize`) this channel's stack
key and count are built from.

---

## 1. RE — the object model

### 1.1 `CSWSStore` layout (VERIFIED, `tools/q.py offsets CSWSStore`, size `0x258`)

```
class CSWSStore (size 0x258):
  +0x000  object            CSWSObject (embedded — object id lives inside it, same as every
                             other game object; a store is a normal CGameObject)
  +0x228  on_open_store     CExoString    ; the store's OnOpen script, if any
  +0x230  item_list         CItemRepository *   ; SAME class party-pool/container use
  +0x234  loc_name          CExoLocString
  +0x244  mark_down         int           ; sell-to-store price multiplier, from the .utm
  +0x248  mark_up           int           ; buy-from-store price multiplier, from the .utm
  +0x256  buy_sell_flag     short         ; can-buy / can-sell bits from the .utm
```

`item_list` at `+0x230` is a `CItemRepository *` — **the exact same class** party-pool sync and
every placeable's container already use (`docs/research/party-pool-sync.md` §1.2): parent id at
`+0x00`, `items_list` (a flat array of item OBJECT IDS) at `+0x0C`, `item_count` at `+0x10`. No
new repository primitive needed anywhere in this feature — `InsertItem`/`RemoveItem`, already
verified and already wired for the party pool, work on a store's `item_list` unchanged.

### 1.2 Store identity: a real game object, resolved by object id (VERIFIED)

This is the load-bearing question the task brief called out, and the answer is unambiguous:
**a store is a placed `CGameObject` with a stable, GIT-derived object id — the same identity
model `ContainerLootStore` and every placeable already use — not a resref respawned per
`OpenStore` call.**

Two independent pieces of evidence:

**`CServerExoApp::GetStoreByGameObjectID` `0x004AE7B0`** is a thin thunk (`ecx = [ecx+4]`,
tail-jump) to **`CServerExoAppInternal::GetStoreByGameObjectID` `0x004B1AB0`**, VERIFIED full
disassembly:

```
0x004B1AB0  push ecx
0x004B1AB1  mov edx, [esp+8]                ; arg1 = object id
0x004B1AB5  mov ecx, [ecx+0x1005c]          ; the app's general object-id lookup table
0x004B1ABF  lea eax, [esp]
0x004B1AC0  push eax                        ; &outPtr
0x004B1AC1  push edx                        ; object id
0x004B1AC1  mov dword ptr [esp+8], 0
0x004B1AC9  call 0x4d8230                   ; generic "resolve this id in the table" helper
0x004B1ACE  cmp al, [0x74666c]              ; lookup succeeded?
0x004B1AD4  jne 0x4b1af2                    ; -> return 0
0x004B1AD6  mov ecx, [esp]                  ; resolved object pointer
0x004B1ADA  test ecx, ecx
0x004B1ADC  je 0x4b1af2                     ; -> return 0
0x004B1ADE  mov al, [ecx+8]                 ; the object's own TYPE byte
0x004B1AE1  cmp al, [0x74663d]              ; == "store" type constant?
0x004B1AE7  jne 0x4b1af2                    ; wrong type -> return 0 (fails safe)
0x004B1AE9  mov edx, [ecx]
0x004B1AEB  call [edx+0x64]                 ; vtable cast to the concrete CSWSStore*
0x004B1AEF  ret 4
0x004B1AF2  xor eax, eax
0x004B1AF5  ret 4
```

`__thiscall(CServerExoAppInternal* this, uint objectId) -> CSWSStore*` (or 0). This resolves a
store from a bare object id through the SAME general per-app object registry every other
object-id-keyed lookup in this codebase already goes through (the same family as
`GetCreatureByGameObjectID`/`GetGameObject`), with a runtime type-tag check that fails safe
(returns 0) rather than mis-casting a non-store object. **This function requires no GUI, no
`OpenStore` call, nothing but a valid object id** — it is available at any time once the module
holding the store is loaded, exactly like `GetGameObject` is for any other object.

**`CSWGuiStore::PopulateStore` `0x006C0610`** (the GUI panel's own refresh routine), VERIFIED
disassembly, reads the object id straight out of the PANEL's own state and resolves it the same
way:

```
0x006C0630  mov edx, [esi + 0x227c]         ; CSWGuiStore + 0x227C = the open store's object id
0x006C0636  mov ecx, [eax + 8]              ; ecx = server internal
0x006C0639  push edx
0x006C063A  call 0x4ae770                   ; a GetXByGameObjectID sibling (resolve family)
0x006C063F  test eax, eax
0x006C0641  je 0x6c067f
0x006C0643  mov ecx, eax
0x006C0645  call 0x4edd60
0x006C064B  ...
0x006C064F  mov [esi + 0x2270], eax         ; CSWGuiStore + 0x2270 = cached CSWSStore* (or the
                                             ; owner it resolves through — see NEEDS-LIVE below)
```

`CSWGuiStore + 0x227C` (VERIFIED, cross-checked against `tools/kotor1_0_3.db`'s
`CSWGuiStore` — the class has no member table for the tail of the 8832-byte panel, but this
offset is the last dword before the struct's end and is written by the store-open path, read
here) holds the currently-open store's OBJECT ID; `+0x2270` caches a resolved pointer once
`PopulateStore` has run. Both are GUI state, not `CSWSStore` fields.

### 1.3 `CGuiInGame + 0x84` (`store`, `CSWGuiStore *`) — detecting "a store is open" (VERIFIED)

`tools/kotor1_0_3.db`'s `CGuiInGame` offset table names member `store` at decimal offset 132
(`0x84`), type `CSWGuiStore *`. `CGuiInGame::HideStoreGui 0x0062E4A0`'s disassembly confirms it
is the live "is a store open" flag and clears it on close:

```
0x0062E4FC  mov eax, [esi + 0x84]           ; esi = CGuiInGame this; eax = CSWGuiStore*
0x0062E502  mov ecx, [eax + 0x44]
0x0062E505  and ecx, 0xfffffdff
0x0062E50B  or ecx, 0x500
0x0062E511  mov [eax + 0x44], ecx           ; clears a panel visibility/flags bit
0x0062E514  mov dword ptr [esi + 0x84], 0   ; <-- clears the store-open pointer itself
```

So `CGuiInGame + 0x84` is non-null exactly while the store panel is open, and is the
authoritative "which `CSWGuiStore` is currently showing" pointer. `CGuiInGame` itself is reached
externally by the SAME chain this codebase already uses for the in-game GUI elsewhere
(`src/KotorCoop.Plugin/EngineHandles.cs`'s `GetInGameGui`, `docs/research/ingame-gui-transition.md`):

```
appMgr         = *(u32*)0x007A39FC
client         = *(u32*)(appMgr + 0x04)
clientInternal = *(u32*)(client + 0x04)
gui            = *(u32*)(clientInternal + 0x40)     ; CGuiInGame*
storePanel     = *(u32*)(gui + 0x84)                ; CSWGuiStore*, or 0 if no store is open
storeObjectId  = *(u32*)(storePanel + 0x227C)        ; the open store's object id
```

**VERIFIED** for every link except the ultimate consumer of the two `CSWGuiStore` tail offsets,
which is this pass's own fresh disassembly (§1.2) rather than a symbol-table entry (the class's
GUI-widget members are catalogued in the DB up to `item_id` at `0x226C`; the two dwords past
that, at `0x2270`/`0x227C`, are not individually named there and are attributed here from
`PopulateStore`'s own code).

### 1.4 The store's own routines (VERIFIED, `tools/q.py func "" CSWSStore` + fresh disasm)

| Method | Address | Role |
| --- | --- | --- |
| `Constructor` | `0x005C6AB0` | |
| `Destructor` / `Destructor_2` | `0x005C6BF0` / `0x005C7160` | |
| `LoadFromTemplate` | `0x005C7760` | builds a store from a `.utm` blueprint (module placement) |
| `LoadStore` / `SaveStore` | `0x005C7180` / `0x005C6CD0` | save-game persist/restore |
| `AddToArea` | `0x005C6E80` | |
| `GetItemInInventory` | `0x005C6BB0` | membership test |
| `RemoveItem` | `0x005C6BA0` | thin wrapper, delegates to the shared `CItemRepository` primitive |
| `AddItemToInventory` | `0x005C70C0` | thin wrapper, delegates to the shared `CItemRepository` primitive |
| **`AcquireItem`** | `0x005C7610` | **the store ACQUIRES an item — i.e. the player SELLS to the store** |
| **`SellItem`** | `0x005C6F70` | **the store SELLS an item — i.e. the player BUYS from the store** |

⚠ Naming trap, called out explicitly because it inverts the obvious reading: `SellItem` is the
store's own "hand an item to the buyer" routine (the GUI's Buy button calls it), and
`AcquireItem` is the store's own "take an item from the seller" routine (the GUI's Sell button
calls it). This document always says "buy" and "sell" from the PLAYER's perspective and names
the engine function only when precision matters.

Call sites, VERIFIED (`tools/xrefs.py`): `SellItem` has exactly one caller, `0x006C0C89`, inside
`CSWGuiStore` — the GUI's Buy-button handler. `AcquireItem` has two callers, `0x0052F78B`
(unidentified, not traced this pass) and `0x006C0E4B`, also inside `CSWGuiStore` — the
Sell-button handler.

### 1.5 The Infinite bit: item flags `+0x288`, bit 2 (mask `0x4`) (VERIFIED, fresh disasm)

`docs/research/item-identity.md` §2 catalogues bits 0/3/4/5/6/7/8 of the item flags dword at
`+0x288` (Identified/Dropable/Pickpocketable/Stolen/NonEquippable/NewItem/DELETING) from
`CSWSItem::SaveItem`/`LoadDataFromGff`. **Bit 2 (mask `0x4`) is not in that list** — it is not a
persisted item field at all, but a STORE-STOCK property tested directly by both `SellItem` and
`AcquireItem`, and this is what the .utm store template's per-entry `Infinite` GFF field turns
into once `LoadStore`/`LoadFromTemplate` builds the item.

`SellItem 0x005C6F70` (buy path), full disasm:

```
0x005C6F8B  mov ecx, [esp+0x1c]             ; the store-stock item being bought
0x005C6F8F  test byte ptr [ecx+0x288], 4    ; Infinite bit
0x005C6F96  je 0x5c702d                     ; CLEAR (finite) -> the decrement/unlink path
        ; SET (infinite): allocate a fresh CSWSItem (0x29c bytes), Constructor, CopyItem
        ; (0x0055D950, copies the source item's stats onto the new one), clear the new item's
        ; Infinite bit and set its StackSize=1, then hand THE COPY to the buyer. The store's
        ; own stock item (ecx) is NEVER touched — not unlinked, not decremented.
0x005C702D  cmp dword ptr [ecx+0x28c], 1    ; finite path: StackSize
0x005C7034  jle 0x5c706c                    ; <=1 -> unlink the whole entry from item_list
        ; >1: split off one unit (call 0x55f280) and give it, decrementing the store's own
        ; stack in place.
```

`AcquireItem 0x005C7610` (sell path), the merge-into-an-existing-entry tail:

```
0x005C76E6  test byte ptr [esi+0x288], 4    ; esi = the MATCHING existing store-stock entry
0x005C76ED  je 0x5c7707                     ; CLEAR (finite) -> increment its StackSize
        ; SET (infinite): returns success WITHOUT incrementing — the incoming item is
        ; consumed (deleted) and the infinite entry's displayed stock never grows.
0x005C7707  inc dword ptr [esi + 0x28c]     ; finite path: StackSize++
```

**Confirms, both directions, VERIFIED**: an Infinite-flagged store entry's stock NEVER changes —
not from a buy (a fresh copy is spawned instead of touching it) and not from a sell of a
same-tag item (the merge silently no-ops instead of growing it). This is the load-bearing fact
for §2.1's design: **infinite entries are excluded from the diffable snapshot entirely** — they
provably never have a delta to replicate, in either direction, by the engine's own construction.

### 1.6 `ExecuteCommandOpenStore` — the VM `OpenStore` routine (VERIFIED, partial disasm)

`CSWVirtualMachineCommands::ExecuteCommandOpenStore 0x00540300` reads four VM parameters
(matching the well-known NWScript signature `OpenStore(object oStore, object oPC=OBJECT_SELF,
int bonusMarkUp=0, int bonusMarkDown=0)`, the two ints clamped to `[-100, 100]`), resolves
`oStore`'s object id and `oPC`'s creature, then — VERIFIED — writes the two bonus ints into a
struct reached via a DIFFERENT resolve chain (`+0x250`/`+0x24c` of whatever
`call 0x5ed690(clientApp, storeId, pcId)` returns), not into `CSWSStore.mark_up`/`mark_down`
(`+0x248`/`+0x244`) directly, and finally calls `CGuiInGame::ShowStoreGui 0x0062E310`. **INFERRED**:
the two bonus ints are a session-scoped GUI overlay on top of the store's own persistent
markup/markdown, not a mutation of the `CSWSStore` object itself — consistent with
`ShowStoreGui`'s own precondition checks (`CGuiInGame+0x108` "initialized", `+0x30` clear,
`+0xB4` clear — the dialogue-active flag `docs/research/*.md` already documents elsewhere) gating
when a store can open at all. **Confirms** (§1.2's point again, from the opposite direction):
`OpenStore` resolves an EXISTING placed object by id; it does not spawn or reconstruct a store.

### 1.7 What was deliberately not chased further

- The exact meaning of `CSWGuiStore`'s `+0x2270` field (cached `CSWSStore*` vs. some other
  resolved owner) was not independently confirmed beyond "PopulateStore writes a resolved
  pointer there right after resolving the store by id" — the READ path this feature builds uses
  `+0x227C` (the object id) and re-resolves through the SAME safe, type-checked accessor
  (`GetStoreByGameObjectID`) the write side already uses, rather than trusting `+0x2270`'s exact
  type. **NEEDS-LIVE.**
- Gold movement during a buy/sell was not found inside `SellItem`/`AcquireItem` themselves — the
  200-body disasm windows read this pass show no `AddGold`/`SubtractGold`-shaped call in either.
  **INFERRED**: gold changes hands in the GUI button-handler code that calls these (`0x6C0C89`/
  `0x6C0E4B`, not traced), separately from stock mutation. This is independently good news for
  the design (§2.4): stock and gold are separate engine concerns, so this channel touching only
  stock cannot double-apply a price the ledger already reconciled.
- No per-area "list every store" enumeration was found this pass (`CSWSArea::LoadStores
  0x005057A0`'s GFF-driven construction loop was read far enough to confirm it builds one
  `CSWSStore` per `.git` "Store List" entry and calls `LoadFromTemplate`/`LoadStore` on each, but
  not far enough to find where — or whether — the area keeps its own array of them afterward,
  the way it very likely does for creatures/placeables/doors). Its absence is why §2.5's design
  reads a store's stock only while that store's GUI is open, rather than continuously for every
  store in the loaded area. See §5's open risks.

---

## 2. Design

### 2.1 Key shape: object-id identified, content-keyed stock

A store IS a stable `CGameObject` (§1.2) — both instances, having loaded the same save/module,
place the SAME store at the SAME object id, exactly like a door or a container. So the op-log key
is `(StoreObjectId, resref + the four non-quantity ItemIdentity fields)` — the party pool's own
`PartyPoolStackKey` (`docs/research/party-pool-sync.md` §2.1), widened by one leading object id.
Quantity is an aggregate int per key, mutated by `Add`/`Remove` (additive) and, resync-only,
`Set` (absolute) — identical semantics to the party pool's multiset, for the identical reason: a
store's stock genuinely goes up (a sell) and down (a buy) repeatedly, by either player, so a
one-way ratchet is wrong here too.

**Infinite entries never enter the store — literal exclusion, not a runtime branch.**
§1.5 proved, from the engine's own two call sites, that an Infinite-flagged entry's stock cannot
change from either a buy or a sell. The bridge's scan (§2.3) therefore filters them out of the
snapshot it hands the diff rule entirely — there is no "infinite buy is a no-op" branch anywhere
in this feature's runtime code, because there is never a delta to reconcile for one. This directly
satisfies the task brief's "infinite-stock buys are no-ops on the peer".

### 2.2 Reuse, not reinvention: `PartyPoolStore`/`ReliablePartyPoolSender`/`PartyPoolDiffRule`,
generalised by one field

Every mechanism `docs/research/party-pool-sync.md` §2 built is reused, widened by the store id:

- **`StoreStore`** = `PartyPoolStore` with `StoreStackKey = (uint StoreObjectId, string Resref,
  byte Charges, byte MaxCharges, uint Upgrades, byte Flags)` instead of `PartyPoolStackKey`. Same
  `Version`/`Digest` tripwire, same op-sequence dedup (an aggregate count is not naturally
  idempotent — see the party-pool doc's §2.2 for why this matters), same cap
  (`MaxStoreStacks`, keyed the same "refuse only a brand-new key" way).
- **`ReliableStoreSender`** = `ReliablePartyPoolSender` verbatim in shape: no in-flight content
  dedup (two sells of the same stack are two distinct, cumulative facts), retransmit-until-ack,
  refuse past a cap with backoff.
- **`StoreDiffRule`** = `PartyPoolDiffRule` verbatim: pure `Diff(expected, actual)` over
  `Dictionary<StoreStackKey, int>`, no dependencies, testable standalone.
- **Echo suppression, two layers** — identical to the party pool's own (§2.2 of that doc): wire
  layer (`CoopClient.HandleStoreOp` skips re-applying an op whose origin is its own peer id, the
  same way `HandlePartyPoolOp` does, while still acking), and engine layer (`StorePump` tracks a
  `_pendingApplyTarget` per key exactly like `PartyPoolPump`, so a queued apply landing does not
  get mistaken for a fresh local change on the very next scan).
- **Resync** — `ResyncStore`, called from `HandleHello` and `HandleStoreSyncRequest`, re-offers
  every currently-held `(store, key)` as an absolute `Set` under the host's own id — identical
  reasoning to `ResyncPartyPool` (§2.5 of that doc): there is no per-contributor history, and a
  rejoining peer may already hold a different total, so replaying `Add` history could double it.

### 2.3 Reading a store's stock: only while its GUI is open (matches the task brief; NEEDS-LIVE)

Per §1.7's open item, no per-area store enumeration was established this pass, so —
**exactly as the task brief specifies** — the bridge reads a store's contents only while a LOCAL
player has that store's GUI open, via the `CGuiInGame+0x84 -> CSWGuiStore+0x227C` chain (§1.3):

```
IStoreBridge.TryScanOpenStore(out uint storeObjectId, out totals):
  gui = read chain to CGuiInGame              -> 0 means not in a loaded module
  storePanel = *(gui + 0x84)                  -> 0 means no store GUI open; return false
  storeObjectId = *(storePanel + 0x227C)
  store = resolve via the SAME safe path the write side uses (§2.4) — NOT +0x2270, since that
          field's exact type was not independently confirmed (§1.7)
  itemList = *(store + 0x230)                 ; CItemRepository*
  walk itemList exactly like ContainerLoot/PartyPool: items_list (+0xC) / item_count (+0x10),
  resolve each item's tag+identity, SKIP any item whose flags bit 2 (0x4) is set (Infinite, §2.1)
```

Throttled the same 1 Hz `ContainerLoot`/`PartyPool` already use — a full item-identity read per
entry is not free, and the GUI is not going anywhere between two ticks a human is looking at it.

### 2.4 Applying an op: resolve by object id, no GUI required (VERIFIED primitive, NEEDS-LIVE apply)

`Odyssey.Store.cs`'s apply side does NOT need the store's GUI open — `§1.2`'s
`CServerExoApp::GetStoreByGameObjectID 0x004AE7B0` (thunking to `CServerExoAppInternal`'s
`0x004B1AB0`) resolves a `CSWSStore*` from a bare object id at any time the module is loaded,
type-checked and failing safe to 0 on a mismatch. Given that pointer, `+0x230` is the SAME
`CItemRepository*` class the party pool already writes through:

- **Insert (a peer's sell arrived)**: spawn the full stack into the LOCAL PLAYER's own inventory
  via `GiveItemToInventory`/`CreateItemOnObject` (the identical verified VM routine
  `Odyssey.AddItemToPartyPool` already uses — there is still no VM path that targets an arbitrary
  repository directly), apply identity via the existing `Odyssey.ApplyItemIdentity`, then unlink
  from the player's own backpack (`CItemRepository::RemoveItem`) and link into the store's
  `item_list` (`CItemRepository::InsertItem`, index -1 = append) — `Odyssey.AddItemToPartyPool`'s
  own three-step shape, retargeted at a resolved store's repository instead of the party table's.
- **Remove (a peer's buy arrived)**: resolve the store, snapshot its `item_list` (mutation-safe,
  same reason `RemovePartyPoolStack` snapshots first), match by Tag + identity, and either
  decrement `StackSize` in place (a partial take) or unlink + `SetPossessor(OBJECT_INVALID, 1, 1,
  OBJECT_INVALID)` (a full take) — `Odyssey.RemovePartyPoolStack`'s own logic, retargeted. As a
  defence-in-depth guard (never strictly reached, since §2.3 never diffs an Infinite entry into an
  op in the first place), a candidate whose flags bit 2 is set is skipped during matching, the
  same way the engine's own `SellItem`/`AcquireItem` refuse to touch one.

### 2.5 Gold is explicitly NOT re-applied here

§1.7 found no gold movement inside `SellItem`/`AcquireItem` — it happens in the GUI button
handler that calls them, a separate code path this feature never touches. **This channel
replicates STOCK ONLY.** The existing gold ledger (`docs/research/progression-sync.md`) already
converges party credits after a buy/sell changes them locally; this feature's apply path
(`Odyssey.Store.cs`) makes no `AddGold`/`SubtractGold` call and no gold-field write of any kind.
Stated explicitly, as the task brief asked, to head off a double-charge: a peer who sees a
finite-stock item vanish from their copy of a store because SOMEONE ELSE bought it does not lose
gold for it — only the buyer's own instance's normal local purchase (already handled by the
existing engine UI and the existing gold channel) charges anyone.

### 2.6 Detect/reconcile scoping: only the currently-open store (open risk, by design)

Because §2.3's read is GUI-gated, `StorePump`'s "detect a local change" step (mirroring
`PartyPoolPump`'s step 1) only ever runs against whichever store is open on THIS tick — there is
no way to notice a local buy/sell in a shop nobody has opened. The "reconcile toward the merged
truth" step (mirroring step 2) is, by contrast, NOT GUI-gated on the apply side (§2.4) — but it is
still driven by `StorePump`, which only knows to reconcile a given store id once it has been seen
at least once (by this instance's own scan, or by a peer's op naming it on the wire). Net effect,
stated plainly as an open risk (§5): **a peer's buy/sell reaches this instance's copy of the same
store once this instance has learned that store's id** (from its own open, or from any op a peer
already sent about it) **and is reconciled the next time `StorePump` ticks** — it does not require
this instance to ALSO have the GUI open at the moment the apply lands, only to have discovered
the store id at some point. The GUI-gating is purely a limitation of DETECTING a fresh local
change, not of applying a remote one.

---

## 3. What was built

**Protocol** (`KotorCoop.Protocol`): `Packets/StorePackets.cs` (`StoreStackKey` — a
`PartyPoolStackKey` widened by a leading `uint StoreObjectId` — `StoreOpKind{Add,Remove,Set}`,
`StoreOpPacket`, `StoreAckPacket`, `StoreSyncRequestPacket`), `PacketCodec.Store.cs`,
`StoreDiffRule.cs` (pure diff, dependency-free, pulled into the plugin build by source the same
way `PartyPoolDiffRule` is — see docs/ARCHITECTURE.md's "plugin compile-include trick"; unused by
the plugin directly today). `PacketType.cs` gained `StoreOp = 57`/`StoreAck = 58`/
`StoreSyncRequest = 59` (the next free numbers after `PartyPoolSyncRequest = 55`);
`ProtocolConstants.cs` gained a `TODO(version)` changelog entry (the `Version` constant itself is
intentionally untouched) and `MaxStoreStacks = 256`. `SessionCapabilities.Store = 1 << 12`
(object-id keyed like a container, but tracked separately from `ContentCompatibility
.ObjectIdKeyed`'s existing members is NOT needed — the exact same save/GIT identity a store
resolves through is already the assumption every object-id-keyed channel makes).

**Session** (`KotorCoop.Session`): `StoreStore.cs`, `ReliableStoreSender.cs`, `IStoreSession.cs`
(added to `ICoopSession`'s composite list), `CoopHost.Store.cs`/`CoopClient.Store.cs`
(publish/apply/relay/resync/ack, wired into each class's handler dictionary, `Tick`
broadcast/send loop, and `HandleHello`'s freshly-admitted resync block — mirroring the party
pool's own wiring exactly), `Pumps/StorePump.cs` (the detect/reconcile tick, scoped per §2.6),
`EntryDigest.StoreEntry`. `AgentSettings.StoreSyncEnabled`, `CoopConfig.StoreSyncEnabled` +
`store_sync` key parsing, `CoopAgent.cs` wiring (`_storePump`, gated call in the heavy pump pass,
`PendingStoreApply` public nested type + `TryDequeuePendingStoreApply`).

**Memory** (`KotorCoop.Memory`): `IStoreBridge.cs` (added to `IGameBridge`'s composite),
`KotorGameBridge.Store.cs` (the read side — the `CGuiInGame+0x84` GUI-open chain, §2.3).
`IGameBridge.CheatKinds` gained `StoreInsert = 30` / `StoreRemove = 31` — no `SharedBlock` layout
change: both reuse `CheatRequest.Target` (the store's object id — already a first-class field,
unlike the party pool which had no per-target concept) plus the same
`Text`/`Amount`/`Charges`/`MaxCharges`/`Upgrades`/`ItemFlags` fields `SetItem`/`GiveLoot`/
`PartyPoolAdd` already carry.

**Agent** (`KotorCoop.Agent`): `CoopCommandDispatcher.Store.cs`'s `DrainAutomaticStore` — the
same single-slot arbitrated cheat-channel drain `DrainAutomaticPartyPool` already established.

**Plugin** (`KotorCoop.Plugin`, x86 NativeAOT): `SharedBlock.CheatKindStoreInsert = 30` /
`CheatKindStoreRemove = 31` (no version bump — every field reused already exists).
`CheatController.Store.cs` (`StoreInsert`/`StoreRemove` handlers, wired into
`CheatController.cs`'s dispatch switch), `Odyssey.Store.cs` (the engine-call layer: the
`GetStoreByGameObjectIDFn` delegate — §1.2/§2.4 — the store-repository insert/remove helpers built
on the SAME `RepositoryInsertItemFn`/`RepositoryRemoveItemFn`/`SetPossessorFn` addresses
`Odyssey.PartyPool.cs` already declares, redeclared here per this codebase's own established
"duplicate a small pointer/delegate declaration per file" convention — see `EngineHandles.cs`'s
own header comment for the precedent — and `InsertItemIntoStore`/`RemoveStoreStack`).

**Tests**: `StoreTests.cs` (store add/remove/Set-is-absolute/per-store-object-isolation/cap/
digest-order-invariance; sender enqueue/no-in-flight-dedup/ack-retirement/no-recipients-waits/
cap/retransmit-backoff; `StoreDiffRule` no-change/new-key/vanished-key/stock-increase/decrease/
echo-suppression/two-different-stores-never-collide). Codec round-trip
(Add/Remove/Set/Ack/SyncRequest, malformed-zero-delta-non-Set rejection) added to
`PacketCodecTests.cs`. `PacketHandlerRegistryTests.cs`'s handler-count pins updated for the three
new registrations on each side. `CheatKindSyncTests` (pre-existing, unmodified) covers the two
new plugin/agent cheat-kind pairs by construction.

---

## 4. VERIFIED / INFERRED / NEEDS-LIVE summary

| Item | Status |
| --- | --- |
| `CSWSStore` layout, `item_list` at `+0x230` is a `CItemRepository*` | VERIFIED (symbol DB) |
| A store is a real `CGameObject`, resolved by object id via a type-checked accessor | VERIFIED (fresh disasm of `CServerExoAppInternal::GetStoreByGameObjectID`) |
| `SellItem`/`AcquireItem`'s full control flow, Infinite bit (`+0x288` bit 2) gating both directions | VERIFIED (fresh disasm, this pass) |
| `CGuiInGame+0x84` = the open store's `CSWGuiStore*`, cleared by `HideStoreGui` | VERIFIED (fresh disasm) |
| `CSWGuiStore+0x227C` = the open store's object id | VERIFIED (fresh disasm of `PopulateStore`) |
| `CSWGuiStore+0x2270` = a cached resolved pointer (exact identity) | NEEDS-LIVE (§1.7) |
| `ExecuteCommandOpenStore`'s VM signature and that it resolves rather than spawns | VERIFIED for the resolve; INFERRED for the markup/markdown target struct |
| No gold movement inside `SellItem`/`AcquireItem` themselves | VERIFIED for these two functions (INFERRED that the GUI handler is where it happens — not traced) |
| No per-area store enumeration found | Not established either way — absence of evidence, drove §2.3/§2.6's design |
| The design (object-id + content keyed multiset, op log, GUI-gated detect / ungated reconcile) | Implemented, unit-tested |
| Reading an open store (`KotorGameBridge.Store.cs`) | Implemented; NEEDS-LIVE |
| Applying an insert/remove (`Odyssey.Store.cs`) | Implemented; **NEEDS-LIVE** — built entirely from primitives independently verified for the party pool, retargeted at a resolved store |
| End-to-end wire sync (host<->client, resync) | Implemented, unit-tested at the store/sender/codec layer; NEEDS-LIVE for the full loop |

---

## 5. Open risks

1. **Detection is GUI-gated; reconciliation is not, but both need the store's id known first**
   (§2.6). A store neither instance has ever opened, and that no op has ever named, cannot sync —
   there is nothing to key on. Once EITHER instance has opened it once, it stays known
   (`StorePump`'s per-store bookkeeping is never cleared just because the GUI closed) for the rest
   of the session, so this is a one-time "someone has to open the shop at least once" cost, not a
   per-transaction one.
2. **`CSWGuiStore+0x2270`'s exact identity is unconfirmed** (§1.7) — the read side deliberately
   avoids depending on it, re-resolving through the same type-checked accessor the write side
   uses instead, so this is a documented gap rather than a live risk.
3. **The one untraced `AcquireItem` caller (`0x0052F78B`)** might be a non-GUI code path (a script
   command?) that also feeds items into a store. Not chased this pass; if it turns out to matter,
   it changes nothing about the primitives this feature already calls, only whether "the store
   GUI is the only way stock changes locally" (assumed throughout §2) is completely true.
4. **A mid-session reconnect's cold-start baseline** — the identical caveat
   `docs/research/party-pool-sync.md` §5 already names for its own pump, applying unchanged here:
   `StorePump`'s per-store local-detection baseline reseeds silently on first scan after a
   (re)open, not on session reconnect specifically, since a store is only ever scanned while open
   anyway.
5. **`MaxStoreStacks` (256) refusing a genuinely new key** past the cap only logs — matches every
   other reliable sender's cap-refusal behaviour in this codebase.

---

## 6. Live test plan (NOT yet run)

Two `tools/demo.ps1` instances, both loaded into the same save/module, Taris `tar_m02aa` (Kebla
Yurt's shop, a base-game store with a mix of finite and infinite stock).

1. **Baseline.** Enable `store_sync = 1` on both, connect. Walk up to Kebla Yurt on the HOST and
   open the store (a manual click — see below for why this pass does not ship a scripted
   opener). Confirm the agent log shows a scan with no spurious deltas.

   **A scripted `openstore <resref>` dev verb was deliberately NOT built this pass.**
   `ExecuteCommandOpenStore` (§1.6) reads its four arguments off the VM's OWN parameter-stack
   machinery (`0x5d10c0`/`0x5d1000` calls), not from a plain calling convention a cheat can hand
   literal values to, and the call chain from there to `ShowStoreGui` (§1.6, the `0x5ed690`
   intermediate call and the `+0x34`-byte allocated struct tagged `0x16`) was not traced far
   enough to be confident it is a direct call rather than a queued client/server GUI message —
   the same local message-bus family `docs/research/container-sync.md` documents for
   `OpenInventory`/`CloseInventory`. Building a bare-engine-call opener on that unclear a
   foundation is exactly the kind of unverified write this project's own house rules
   (`docs/CONTRIBUTING.md`'s engine-write discipline) ship behind a live check first, not guess
   at — so the live test plan below relies on a manual click, same as every other GUI-only
   interaction this codebase's live rig already handles by hand. A future pass with more RE
   budget for `0x5ed690`/the `0x16`-tagged message shape could add the verb without touching any
   of the sync channel this document actually ships.
2. **Host buys a finite item.** Confirm the host's own stock decreases, an op is published, and
   the CLIENT — once it also opens (or has already opened) the SAME store — reconciles to the
   same decreased stock. Confirm NO gold is deducted a second time on the client (§2.5).
3. **Host buys an INFINITE item** (most Kebla Yurt ammo/consumables are). Confirm NO op is
   published at all (§2.1/§1.5) — the store scan should show the SAME total before and after on
   the host itself.
4. **Client sells an item to the store.** Confirm the item appears in the store's stock for BOTH
   instances once both have it open, and that a subsequent buy-back on EITHER side works (finite,
   so it decrements correctly either direction).
5. **Reconnect** with the store's stock non-empty; confirm `ResyncStore`'s `Set`-based resync
   converges the reconnecting client without doubling anything.
6. **A store neither instance has opened yet** stays untouched by this channel (no crash, no
   spurious log line) — confirms §5 risk 1 is a limitation, not a bug.

Watch for in the plugin log: `store: inserted/removed '<resref>' into/from store 0x<id>...` lines
from `CheatController.Store.cs`, and any `store: ... failed — <LastStoreNote>` refusal, which
names exactly which of §2.4's guards fired.
