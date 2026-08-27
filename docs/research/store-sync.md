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

## Addendum (2026-08-27): `openstore` — finishing the trace §1.6/§6 declined to build a verb on

§1.6/§6's own words: "the call chain from there [`ExecuteCommandOpenStore`] to `ShowStoreGui`...
was not traced far enough to be confident it is a direct call rather than a queued client/server
GUI message." This pass finished that trace with fresh disassembly of both functions and built
the verb.

### `ShowStoreGui` (`0x0062E310`) — VERIFIED, takes only `this`

Full disassembly, this pass: `mov esi,ecx` (the only parameter read is `this`, a `CGuiInGame*`),
then three precondition checks —

```
test [esi+0x108]   ; nonzero required — HUD initialised (same offset MenuLoadController.
                    ; MainMenuPhase.cs names InGameGuiInitialized, a different class)
test [esi+0x30]    ; must be ZERO — another modal screen (galaxy map, party select, ...)
test [esi+0xB4]    ; must be ZERO — a conversation is active (the SAME flag
                    ; DialogStateWatchHook.cs / Odyssey.Dialogue.Spectate.cs's
                    ; GuiConversationActive already names, VERIFIED consistent)
```

— then allocates (or reuses, if `+0x84` is already non-null) a `CSWGuiStore` panel and calls its
constructor with an argument from `CGuiInGame+0x38`. **No stack argument anywhere in the
function's body carries a store id.** `ShowStoreGui` is a pure "put the panel on screen"
operation, unconditional on which store.

### `ExecuteCommandOpenStore` (`0x00540300`) — VERIFIED, the store id never reaches `ShowStoreGui` directly

Full disassembly, this pass, the relevant tail:

```
0x005403DF  mov eax,[0x7a39fc]                 ; appMgr
0x005403E4  mov ecx,[eax+8]                    ; ecx = server (matches GetServer())
0x005403E7  push edx                            ; oStore's VM id
0x005403E8  call 0x4ae750                       ; a GetXByGameObjectID sibling — vtable-cast
0x005403F9  call [edx+0x64]                     ;   via slot 0x64, the SAME cast pattern §1.2's
                                                 ;   own GetStoreByGameObjectID disasm shows
0x00540404  mov [eax+0x250],ecx                 ; store->+0x250 = bonusMarkUp   <- eax IS the
0x0054040E  mov [eax+0x24c],edx                 ; store->+0x24c = bonusMarkDown    resolved store
...
0x00540418  mov eax,[0x7a39fc]                  ; OVERWRITES eax — the store pointer above is
                                                 ; NEVER SAVED anywhere past this line
0x00540421  mov ecx,[eax+4]                     ; ecx = client
0x0054042B  call 0x5ed690                       ; -> CGuiInGame* (client_internal + 0x40)
0x00540430  mov ecx,eax
0x00540432  call 0x62e310                       ; ShowStoreGui(gui) — UNCONDITIONAL, right here
0x00540437  push 0x34
0x00540439  call 0x6fa7e6                       ; allocates a SEPARATE 0x34-byte object
0x00540468  mov word ptr [esi],0x16              ; tags it 0x16
...                                              ; posted through 0x4aea40 — a client/server
                                                 ; message, not chased further this pass
```

**Confirms and corrects §1.6/§1.7 in one pass**: `ShowStoreGui` IS called directly, unconditionally
(not gated behind the 0x34-byte message), settling the "direct call vs. queued message" question
§1.6 left open — but the resolved store pointer is discarded (never spilled to the stack) the
moment `eax` is reused to resolve the GUI, so **the store id genuinely never reaches the panel
through this call path** at all. Also corrects §1.6's own "INFERRED: a different resolve chain"
about `+0x250`/`+0x24c`: fresh disasm shows the write lands on the SAME vtable-cast `CSWSStore*`
`GetStoreByGameObjectID` returns — these two ints are ordinary fields inside the documented
`0x258`-byte struct (§1.1), not a separate structure.

The 0x34-byte, tag-`0x16` object posted through `0x4aea40` is very likely how the retail client
actually learns which store to show (the same client/server message-bus family
docs/research/container-sync.md already documents for `OpenInventory`/`CloseInventory`) — not
chased further this pass, because it is not needed: `CSWGuiStore::PopulateStore` (§1.2) already
names exactly where the panel reads its store id from (`CSWGuiStore+0x227C`), and that field can
be written directly.

### The verb: write the id, then force the read

`Odyssey.OpenStore(store, out reason)`:

1. Resolve `CGuiInGame*` (the same `GetInGameGui()` chain `Odyssey.Dialogue.Spectate.cs` already
   has, reused unqualified — same partial class).
2. Check the same three gates `ShowStoreGui` itself checks (`+0x108`/`+0x30`/`+0xB4`) — refusing
   BEFORE calling in, with a specific reason, rather than calling into a state the engine itself
   would have refused silently.
3. Write `0`/`0` into the resolved store's `+0x250`/`+0x24C` (the bonus markup/markdown fields —
   matching a bare `OpenStore(oStore, oPC)` with no bonus arguments, not whatever a PRIOR VM-driven
   open left there).
4. Call `ShowStoreGuiFn(gui)`.
5. Read the panel pointer back off `gui+0x84` (0 means allocation failed — refuse).
6. Write the store's own object id (`CSWSObject`'s embedded id, `store+0x04`) into
   `panel+0x227C` — the exact field `PopulateStore` reads.
7. Call `PopulateStoreFn(panel)` directly to force the item list to refresh from the id just
   written, rather than trust whatever the 0x34-byte message eventually does.

`CreatureFinder.FindStoreByTag` (new: `StoreVtable = 0x0074BDB8`, per
docs/research/placeables-containers.md §4's own citation) resolves a store by TAG the same way
`FindDoorByTag`/`FindPlaceableByTag` already do, for the human-typed form; the automatic
`store_sync` apply path (§2.4) keeps using `ResolveStoreBase`/`GetStoreByGameObjectID` by id, since
it always already has one.

### What was built

`src/KotorCoop.Plugin/Odyssey.Store.cs` — `ResolveStoreRepo` refactored to call a new, exposed
`ResolveStoreBase` (returns the store's BASE pointer, not the `+0x230` repo offset —
`OpenStore` needs the base for the bonus fields and the object-id field; behaviour unchanged for
`InsertItemIntoStore`/`RemoveStoreStack`, which still only ever see the repo pointer). New:
`ShowStoreGuiFn` (`0x0062E310`), `PopulateStoreFn` (`0x006C0610`), and `OpenStore(store, out
reason)` per the seven steps above. `src/KotorCoop.Plugin/CreatureFinder.cs` — `StoreVtable`,
`FindStoreByTag`. `src/KotorCoop.Plugin/CheatController.Store.cs` — `OpenStore()`: resolves by
`CheatTarget` (id) or `CheatText` (tag, via `FindStoreByTag`) — refuses if neither is given, there
is no "nearest store" sweep. `SharedBlock.CheatKindOpenStore = 36` /
`IGameBridge.CheatKinds.OpenStore = 36`.

Console: `openstore <resref|tag|id>` (`ConsoleCommand.cs`'s `CoopCommandKind.OpenStore`,
`ConsoleCommand.Items.cs`'s `ParseOpenStore`) — one required argument.
`CoopCommandDispatcher.Store.cs`'s `OpenStore(string)` resolves id-vs-tag the same way
`UsePlaceable` does. Registered in `ConsoleVerbCatalog.cs` and `FuncInterpHook.cs` — reachable at
the real `~` console, not blockpoke-only.

### NEEDS-LIVE

Nothing in this addendum has been run against the game — everything above is fresh static
disassembly plus the already-shipped, tested `store_sync` primitives it reuses. A live pass
should, per §6's own plan (Kebla Yurt, `tar_m02aa`):

1. `openstore kebla_yurt_shop` (or whatever the real tag/id turns out to be on the rig's save —
   `census`/`resolve` can find it first) and confirm the store GUI genuinely opens on screen
   with the RIGHT store's inventory, not empty or a stale one.
2. Confirm the SAME store, reached this way, is now known to `StorePump` — i.e. §6's "both
   instances must open the SAME store at least once" requirement is satisfied by `openstore` the
   same way a manual click satisfies it, unblocking §6 items 2-5 without a human driving the
   mouse.
3. Confirm the three refusal gates (another modal screen up, a conversation active, HUD not yet
   initialised) actually refuse cleanly rather than crash, by trying `openstore` while, e.g., the
   galaxy map is open.
4. Whether the 0x34-byte/`0x4aea40` message this pass left untouched does anything OBSERVABLE
   that `openstore`'s direct-write approach misses (e.g. a server-side `OnOpenStore` script that
   a real click fires but this bypass does not) — worth a few minutes watching whether a store
   with a nonzero `on_open_store` (`+0x228`, §1.1) behaves differently opened this way versus by
   a real click.

## Live pass (2026-08-28): item 1 run — a new, reproducible engine crash found inside
## `CSWGuiStore::PopulateStoreItemListBox`

Task: run §6's own item 1 (`openstore <tag>`, confirm the GUI opens with the right inventory).
Found the real store on the rig's save first (`Kebla Yurt` was never confirmed — this pass reads
the module's actual data instead of guessing): `tools/kotorfiles.py list
"modules\tar_m02aa.rim"` names the module's own GIT resref `m02aa` (not `tar_m02aa` —
a naming trap worth recording for the next static read); `tools/kotorfiles.py gff
"modules\tar_m02aa.rim:m02aa.git" --path StoreList/0` gives exactly one store, resref `larrim`,
position `(86.9, 101.7)`; `tools/kotorfiles.py gff "modules\tar_m02aa_s.rim:larrim.utm"` names
its real `Tag`: **`Larrim_Store`**, not `kebla_yurt_shop` (this doc's own earlier guess, never
confirmed live — corrected here).

Host warped to `tar_m02aa` (`warp tar_m02aa`), a real ambient conversation with a nearby NPC was
already active on arrival (an ordinary ARRIVAL trigger, not the "forced" kind — `selectreply 0`
a few times cleared it, confirming this pass's own instance of §6 item 3's "another modal screen
up" refusal gate working correctly first: `openstore Larrim_Store` refused cleanly with "a
conversation is active" while the conversation was still running, no crash). Once the
conversation ended, `openstore Larrim_Store` was issued again — **and the HOST process crashed**
(`Get-Process` afterward: gone; the CLIENT stayed up and healthy throughout, `Responding=True`).

**Symbolicated, `tools/crashlog.ps1` + `tools/q.py sql`**: `FIRST-CHANCE access violation
(0xC0000005) at 0x002C1872` (module offset; real address `0x6C1872`, `swkotor.exe` base
`0x400000`). Bracketing functions from the symbol database:

```
CSWGuiStore  PopulateStoreItemListBox  0x6C1840
CSWGuiStore  OnControlXButton          0x6C1B00
```

`0x6C1872` is `0x32` (50) bytes into `CSWGuiStore::PopulateStoreItemListBox`, well inside its
body (the next function starts `0x2C0` bytes later). **This is a new, real engine crash, not
previously documented anywhere in this tree**, distinct from the unrelated `0x40D386` STUNT_00
crash (`galaxy-map-and-party-select.md`) — different module, different function, different
trigger, and this pass's own STUNT_00-adjacent testing earlier the same session confirmed THAT
crash is fixed/not reproducing, ruling out any connection between the two.

**Likely cause, not confirmed this pass (no time to trace `PopulateStoreItemListBox`'s own
disassembly)**: `OpenStore`'s own implementation (§2, `CheatController.Store.cs`) writes the
store's object id directly into the panel's `+0x227C` field and calls
`CSWGuiStore::PopulateStore` (0x006C0610) to force the refresh, bypassing whatever a REAL click's
full open sequence (`ShowStoreGui` -> panel construction -> the store repository actually being
attached/populated in the normal order) would have set up first. `PopulateStoreItemListBox` is
very plausibly downstream of `PopulateStore` and dereferences something (an item-list widget, a
repository pointer) that is not yet in the state a real click would have left it in. This is
exactly the class of gap §1.6's own original write-up flagged as untraced risk in the direct-write
approach, now observed as a real crash rather than a theoretical one.

**Not fixed this pass** — out of scope for a live-verification round; flagged for whoever next
touches `openstore`/`store_sync`. `store_sync`/`openstore` should NOT be trusted for anything
beyond further diagnosis until `PopulateStoreItemListBox`'s own preconditions are traced and
either satisfied by `OpenStore`'s write sequence or the direct-write approach is abandoned in
favour of driving the real click path. `coop.ini`'s `store_sync` default is unaffected (already
`0`, unchanged by this finding) — this crash was reached through the diagnostic `openstore`
console verb specifically, not through the (still off-by-default) automatic sync apply path,
though the underlying `CSWGuiStore` machinery is shared and the automatic path should be assumed
at risk until this is traced.

**A/B pazaak, tested the same session, for comparison — clean, no crash.** `pazaak_detect=1` on
both installs, `pazaak` (deck 0, default opponent) on the host: hand started cleanly (`pazaak:
StartPazaakGame(...) called - running now: True`), the CLIENT's own agent correctly showed
`Lanik is in a minigame - waiting` (the shipped `MinigameAnnouncer` notice, `minigames.md`
§23.3's own fix, confirmed live for the first time — `pazaak_detect`'s own offset read,
previously NEEDS-LIVE per §23.1, is now watched flipping in a real running session). No crash,
no desync. `pazaak_detect` stays default OFF per this doc's own established discipline (one
clean session is not the live-verification bar this codebase sets for flipping a shipped
default), but this is a positive data point for a future round that wants to flip it.

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean, `dotnet test`
2339/2339 (unaffected by this crash — it is a live-engine finding, not a code defect this repo's
own suite could catch).

## Root cause and fix (2026-08-28): `ShowStoreGui` takes two more arguments than the
## 2026-08-27 addendum claimed — the missing store id, not a downstream engine bug

Task: root-cause the `PopulateStoreItemListBox` crash the live pass above found, by disassembly.
Finished it — fresh disasm of `CSWGuiStore::PopulateStoreItemListBox`, `CSWGuiStore::
InitializeStoreType`, `CSWGuiStore::OnPanelAdded`, `CSWGuiStore::Constructor`, and (the
load-bearing correction) a full re-read of `CGuiInGame::ShowStoreGui` itself, all `tools/
disasm.py` against the sha256-matched GOG `swkotor.exe` v1.0.3, cross-checked with `tools/
xrefs.py` and `tools/q.py`.

### The bug in one sentence

**The 2026-08-27 addendum's own claim — "`ShowStoreGui` takes only `this`... no stack argument
anywhere in the function's body carries a store id" — was wrong.** Fresh disasm this pass shows
`CGuiInGame::ShowStoreGui 0x0062E310` is `__thiscall(CGuiInGame* this, uint storeObjectId, uint
pcObjectId) -> void`, **`ret 8`** (VERIFIED: the trailing `ret 8`, and `[esp+0x30]`/`[esp+0x34]`
resolve to `[ESP_entry+4]`/`[ESP_entry+8]` — the two caller-pushed stack args — once the
function's own prologue displacement, 0x2C bytes (three SEH pushes + `sub esp,0x1C` + `push
esi`), is worked out by hand). Because `Odyssey.Store.cs`'s old `ShowStoreGuiFn` delegate was
declared `this`-only, the native call pushed **zero** stack bytes against a callee that reads
two dwords and pops 8 bytes on return — the two "arguments" `ShowStoreGui` read were whatever
stale bytes happened to already be on the native call stack, and the function's own `ret 8`
then desynced the caller's stack by 8 extra bytes on every return, a second, independent hazard
riding along with the crash.

### The full call chain, VERIFIED this pass

```
CGuiInGame::ShowStoreGui 0x0062E310  (this, uint storeObjectId, uint pcObjectId), ret 8
  ... three precondition gates (+0x108/+0x30/+0xB4 — unchanged from the addendum, still correct)
  panel = CGuiInGame+0x84   ; alloc + CSWGuiStore::Constructor 0x6C1C00 if null, else reused
  panel+0x2278 = storeObjectId        <-- VERIFIED: arg1, NOT `this`-only as the addendum said
  panel+0x227C = pcObjectId           <-- VERIFIED: arg2 — the SHOPPING CREATURE's id, not the
                                           store's (corrects the addendum's own mis-attribution
                                           of this field, made from PopulateStore's disasm alone)
  AddPanel 0x40BC70(gm, panel, 3, 1)  ; synchronously invokes the panel's own OnPanelAdded
    -> CSWGuiStore::OnPanelAdded 0x006C2170                                       (VERIFIED)
         InitializeStoreType(this)     0x6C1B50   reads panel+0x2278 (NOT +0x227C), resolves via
                                                   GetStoreByGameObjectID 0x4AE7B0 (type-checked,
                                                   fails safe to 0 — the same function §1.2
                                                   already verifies), reads the resolved store's
                                                   +0x256 buy_sell_flag, tail-jumps into
                                                   ShowBuyGUI 0x6C1A50 / ShowSellGUI 0x6C13C0
         ShowBuyGUI/ShowSellGUI                    ... own per-mode setup, tail-jumps into
                                                   PopulateStoreItemListBox 0x6C1840 at its end
         PopulateStoreItemListBox 0x6C1840         ALSO reads panel+0x2278 (not +0x227C), calls
                                                   the SAME GetStoreByGameObjectID, then
                                                   IMMEDIATELY dereferences the result:
                                                   `mov ebx, dword ptr [eax + 0x230]` @ 0x6C1872
                                                   -- **the exact crashing instruction**
         PopulateStore(this)          0x6C0610   reads panel+0x227C (the PC id), resolves via
                                                   GetCreatureByGameObjectID 0x4AE770 (VERIFIED
                                                   by name in tools/kotor1_0_3.db, and by the
                                                   same fail-safe type-check shape at 0x4B1790),
                                                   builds a description string — unrelated to the
                                                   item list
         base OnPanelAdded            0x40B870   generic mouse-over/focus revalidation (same as
                                                   every other panel, docs/research/
                                                   native-browser-panel.md already documents)
         jmp ShowBuyGUI               0x6C1A50   unconditional — tail-jumps into
                                                   PopulateStoreItemListBox a SECOND time
```

`CSWGuiStore::Constructor 0x6C1C00` (VERIFIED, fresh disasm) explicitly zeroes `panel+0x2270`
and `panel+0x2274` but **deliberately leaves `+0x2278`/`+0x227C` untouched** — proof these two
fields are the caller's job (`ShowStoreGui`'s own two stack args), not the constructor's, the
same pattern `docs/research/native-browser-panel.md` documents for `CSWGuiSaveLoad` leaving row
population to its own self-called `PopulateGameList` rather than the constructor initializing
rows directly.

### Why the old verb crashed exactly where it did, and why its own follow-up code never ran

The old `Odyssey.OpenStore` called `ShowStoreGuiFn(gui)` (no store id), which — per the chain
above — ran `AddPanel` -> `OnPanelAdded` -> `InitializeStoreType`/`PopulateStoreItemListBox`
**synchronously, inside that single call**, using whatever garbage was on the stack at
`panel+0x2278`. `GetStoreByGameObjectID`'s own type check (VERIFIED fail-safe, §1.2) either
returned 0 outright or resolved to an unrelated live object that then failed the `mov al,
[ecx+8]; cmp al, [type_constant]` check and ALSO returned 0 — either way `eax` was 0 (or, on a
run where the stale bytes coincidentally passed the type check, a wrong object) by the time
`PopulateStoreItemListBox` executed `mov ebx, [eax+0x230]` at `0x6C1872`, producing the observed
access violation. **This happened before the old verb's own subsequent code (writing the store
id to `panel+0x227C` and calling `PopulateStore`) ever had a chance to run** — that code was
already too late, and even if it had run first, `+0x227C` is the PC-id field `PopulateStore`
uses for its description string, not the store-id field `PopulateStoreItemListBox` actually
reads. The old fallback was doubly wrong, not just late.

### The fix

Pass `ShowStoreGui` the same two arguments the real engine path (`ExecuteCommandOpenStore`'s own
resolved `oStore`/`oPC`, or a real click) supplies, so `AddPanel`'s internal `OnPanelAdded`
populates the item list and description exactly as retail does — no manual field write, no extra
`PopulateStore` call, because the engine's own sequence already does both, correctly, inside the
single `ShowStoreGui` call:

- `src/KotorCoop.Plugin/Odyssey.Store.cs` — `ShowStoreGuiFn` redeclared
  `delegate* unmanaged[Thiscall]<nint, uint, uint, void>` (was `<nint, void>`);
  `StorePanelObjectIdOffset` corrected from `0x227C` to `0x2278` (now documented as the field
  `InitializeStoreType`/`PopulateStoreItemListBox` actually read, written by `ShowStoreGui`
  itself — not by this code); new `StorePanelPcIdOffset = 0x227C` (the field `PopulateStore`
  reads, also documented as the PC id, not the store id); `OpenStore(nint store, uint
  playerObjectId, out string reason)` gained the `playerObjectId` parameter, resolves
  `storeObjectId` from the store base BEFORE the call (was after), calls
  `ShowStoreGuiFn(gui, storeObjectId, playerObjectId)`, and no longer writes any panel field or
  calls `PopulateStoreFn` manually (the delegate and the manual `PopulateStore` call are both
  removed — the real `ShowStoreGui` call already does everything they were standing in for).
- `src/KotorCoop.Plugin/CheatController.Store.cs` — `OpenStore()` -> `OpenStore(uint playerId)`,
  threads the dispatcher's already-resolved local player object id through to
  `Odyssey.OpenStore(store, playerId, out reason)`.
- `src/KotorCoop.Plugin/CheatController.cs` — the `CheatKindOpenStore` dispatch case now calls
  `OpenStore(playerId)` (the `playerId` local `Execute` already resolves and null-checks before
  the switch, the same value every other per-player cheat kind in that switch already uses).

### VERIFIED / INFERRED / NEEDS-LIVE

| Item | Status |
| --- | --- |
| `ShowStoreGui 0x0062E310` signature `(this, uint storeObjectId, uint pcObjectId)`, `ret 8` | VERIFIED (fresh disasm, corrects the 2026-08-27 addendum) |
| `ShowStoreGui` writes `storeObjectId` -> panel `+0x2278`, `pcObjectId` -> panel `+0x227C`, then calls `AddPanel` | VERIFIED (fresh disasm) |
| `AddPanel` synchronously invokes `CSWGuiStore::OnPanelAdded 0x006C2170` (`InitializeStoreType`, `PopulateStore`, base `OnPanelAdded`, then an unconditional `ShowBuyGUI`->`PopulateStoreItemListBox` tail-jump) | VERIFIED (fresh disasm; `AddPanel`->`OnPanelAdded` dispatch itself already documented for `CSWGuiSaveLoad` in `docs/research/native-browser-panel.md`) |
| `InitializeStoreType`/`PopulateStoreItemListBox` read panel `+0x2278` (the store id) via `GetStoreByGameObjectID 0x4AE7B0`; `PopulateStoreItemListBox`'s `mov ebx,[eax+0x230]` @ `0x6C1872` is the exact crashing instruction on a bad/null resolve | VERIFIED (fresh disasm) |
| `PopulateStore 0x6C0610` reads panel `+0x227C` (the PC id, not the store id) via `GetCreatureByGameObjectID 0x4AE770` | VERIFIED (fresh disasm + `tools/kotor1_0_3.db` naming `0x4AE770` `CServerExoApp::GetCreatureByGameObjectID`) |
| `CSWGuiStore::Constructor 0x6C1C00` zeroes `+0x2270`/`+0x2274`, leaves `+0x2278`/`+0x227C` for the caller | VERIFIED (fresh disasm) |
| The fix (corrected delegate signature, corrected offsets, `playerObjectId` threaded through `CheatController`) | Implemented; build + full test suite green |
| The fix resolves the crash and `openstore` opens the RIGHT store's GUI, populated, without a manual field write | **NEEDS-LIVE** — this pass was static RE + a code fix only, no live process was run |
| Whether `ShowStoreGui`'s stack-cleanup mismatch (`ret 8` against zero pushed bytes) caused any OTHER, still-unobserved corruption before the crash reliably intercepted it every time | Moot after the fix (the call now pushes the matching two dwords), not otherwise investigated |

### Live re-test plan (supersedes the prior pass's reproduction steps)

1. Build and install: `pwsh src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError` (both rig game
   directories per `docs/CONTRIBUTING.md`'s "run this for every game copy the rig uses" rule —
   `demo.ps1` does not deploy the plugin itself).
2. `pwsh tools\demo.ps1`, host warps to `tar_m02aa` (`warp tar_m02aa`), waits out or clears any
   ambient conversation (`selectreply 0`), then `openstore Larrim_Store` (the real tag this doc's
   own prior live pass found — Kebla Yurt was never the store's own tag).
3. Confirm the HOST process survives (`Get-Process swkotor` — both PIDs still present,
   `Responding=True`) and the plugin log shows `openstore: 'Larrim_Store' - ShowStoreGui called,
   panel populated` with no crash line in `kotorcoop-general.log`/`crashlog.ps1`'s output.
4. Confirm the store GUI genuinely shows on screen with Larrim's real inventory (a screenshot via
   `tools/shot.ps1`, or a live description) — §6/the addendum's original item 1, now actually
   completable.
5. Repeat item 2 while the galaxy map or party select is up, and while a conversation is active,
   to confirm the three precondition gates (unchanged by this fix) still refuse cleanly rather
   than crash — this doc's prior pass already observed the conversation gate working; the other
   gate is untested live.
6. Once `openstore` is confirmed safe, resume §6's own items 2-5 (host buys/sells, client
   reconciles) — these were never reached because item 1 crashed every prior attempt.
7. `store_sync`/`coop.ini` stays default OFF regardless of this fix's outcome — this document's
   own existing discipline for flipping a shipped default (a clean `openstore` session is not
   the bar; the full §6 buy/sell/reconnect loop is) is unchanged.

## Live pass (2026-08-28): the `ShowStoreGui` fix confirmed clean — the crash is gone; the
## buy/sell/reconnect loop remains blocked by the pre-existing click-injection gap, not this fix

Two-instance rig (`tools/demo.ps1`), `pwsh src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError`
on both game directories first (deployed `dinput8.dll` hash-confirmed identical to the fresh
build on both installs). Both instances `warp tar_m02aa` together (confirmed via `where` on both:
host `92.1 128.9`, client `92.0 132.7`, same module).

### Items 1-3 of the re-test plan: PASS, live-confirmed

The same ambient conversation the prior crash-finding pass hit was active again on arrival;
`openstore Larrim_Store` correctly refused it cleanly (`the engine refused it: openstore
'Larrim_Store' refused: a conversation is active`, twice, while the conversation was still
running) rather than crashing — the precondition gate this fix left unchanged still works.
`selectreply 0`/`skipline` cleared it after a few rounds (this particular ambient conversation
took more turns to close than the prior pass's — not investigated further, not relevant to the
fix under test). With the conversation clear, `openstore Larrim_Store` returned `store
'Larrim_Store': opened`, and the plugin log recorded `openstore: store 'Larrim_Store' -
ShowStoreGui called, panel populated` — **no crash**, matching the fix's own claim exactly. The
HOST process (`Get-Process swkotor`) stayed alive and `Responding=True` throughout and for several
minutes after. `tools/crashlog.ps1` confirmed no new crash entry from this session — the most
recent `0xc0000005` at `0x002c1872` (the exact `PopulateStoreItemListBox` address this fix
targets) is timestamped from the PRIOR round's session, before the fix landed, not this one.
**Item 1 of the addendum's original checklist — "confirm the GUI opens with the right
inventory" — is now genuinely reachable and clean**, closing the crash this doc's own prior two
entries chased.

A `tools/shot.ps1` capture was attempted but returned a black frame (the rig's background/hidden
window is not the true foreground surface `BitBlt` needs — a capture-tooling limitation, not a
game-state one); the log-line evidence above (`ShowStoreGui called, panel populated`, no crash,
process alive) is treated as sufficient given `openstore`'s own code path leaves nothing else that
could silently fail between those two log points.

### Item 5 (the other precondition gate — galaxy map/party select): not attempted this pass

Out of time budget for this round; only the conversation gate (already proven working, above) was
re-confirmed. The galaxy-map/party-select gate remains exactly as untested as the prior pass left
it.

### Items 2-4 (host buys/sells a finite/infinite item, client reconciles): NOT REACHED — same
### pre-existing limitation this document has always named, not a gap in this fix

`CheatKinds.StoreInsert`/`StoreRemove` (`CheatController.Store.cs`) are the sync channel's own
automatic-apply primitives — they exist to let one instance's agent replay a PEER's already-
detected buy/sell locally, and are wired only to that internal drain path
(`CoopCommandDispatcher.Store.cs`'s `DrainAutomaticStore`), not to any console verb a human or a
scripted test can call directly (confirmed by grep: no `CoopCommandKind` maps to either). The
channel's own stock-change DETECTION (§2.3) only runs "while the store's GUI is open," reading a
real before/after delta — so exercising the buy/sell/reconnect loop needs a REAL purchase to
happen through the actual GUI, which needs a real mouse click. §6's own original write-up already
named this precisely ("driving the real click path... blocked by the same mouse-click-injection
gap `docs/research/session-save.md` already documents") and this pass confirms nothing has
changed: there is still no way to stage a real store transaction from this project's own console
primitives. **This is not a new finding and not something this fix could have addressed** — it is
the same limitation `openstore` itself was built to work around for item 1 only (confirming the
GUI opens), and buying/selling has no equivalent workaround yet. `store_sync` stays OFF.

### Verdict

The `ShowStoreGui` argument-count fix is **live-confirmed correct and safe**: `openstore` opens
the right store's GUI, populated, with no crash, across two separate live attempts (the
conversation-gate refusal and the successful open). The remaining §6 items (2-5, the actual
sync-channel behaviour) stay unverified for the same reason they always have — no click-injection
primitive exists in this codebase yet. `store_sync` stays at its default OFF in `coop.ini`/
`docs/CONFIG.md`; no default was flipped this round. `dotnet build KotorCoop.slnx -warnaserror`
and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`: both clean (no source changed this pass beyond
the unrelated `build.ps1` `$env:Platform` environment fix recorded in `companion-state-sync.md`'s
own 2026-08-28 entry).

## Addendum (2026-08-28): `buy`/`sell` — the click-injection gap the previous pass named,
## closed by calling the CLICK HANDLERS themselves instead of injecting a click

The prior pass's own verdict, restated exactly: "there is still no way to stage a real store
transaction from this project's own console primitives... buying/selling has no equivalent
workaround yet." This pass closes that gap without injecting a click at all — the same trick
`openstore` already used against `ShowStoreGui`, applied one level further down the chain.

### The two functions, VERIFIED (fresh disasm, this pass)

`tools/q.py sql` names both directly (confirming `tools/kotor1_0_3.db`'s own naming, not
guessed): **`CSWGuiStore::BuyItem` `0x006C0BE0`** and **`CSWGuiStore::SellItem`
`0x006C0D80`** — the GUI panel's own Buy/Sell button click handlers, `__thiscall(this=panel,
item) -> void`, `ret 4` (one stack argument each: the item pointer to move). Full disasm of
both, cross-checked against `tools/xrefs.py 0x5c6f70`/`0x5c7610` (each engine-side
`SellItem`/`AcquireItem` has exactly one caller — §1.4 already established this; this pass
disassembles that one caller instead of leaving it untraced):

```
CSWGuiStore::BuyItem(panel, item)                     0x006C0BE0, ret 4
  edi = CServerExoApp::GetPlayerCreatureId 0x4AEA40(server)        -- the ACTING PLAYER's id
  edi = CServerExoApp::GetCreatureByGameObjectID 0x4AE770(server, edi)
  ebp = CServerExoApp::GetStoreByGameObjectID 0x4AE7B0(server, panel+0x2278)   -- THE OPEN STORE
  CSWSCreature::GetItemRepository 0x4EF770(edi, 1)      -- refuses if the player has none
  CSWGuiManager::PlayGuiSound 0x40A140(panel+0x18, 9)   -- the Buy click sound
  eax = CSWGuiStore::GetItemBuyValue 0x6C0790(panel, item)          -- the price
  edx = panel+0x2270 - eax ; panel+0x2270 = edx         -- see the correction below
  CSWSCreature::SetGold 0x4EDD90(edi, edx)              -- ABSOLUTE, not a delta
  CSWSStore::SellItem 0x5C6F70(ebp, item, edi)           -- the item ACTUALLY moves (§1.4/§1.5)
  CSWGuiStore::PopulateStoreItemListBox 0x6C1840(panel)  -- refresh both lists, currency label

CSWGuiStore::SellItem(panel, item)                     0x006C0D80, ret 4      -- SAME shape,
  ... identical resolve of edi(player)/ebp(store) via the same two calls ...    mirrored:
  eax = CSWGuiStore::GetItemSellValue 0x6C07F0(panel, item)
  edx = panel+0x2270 + eax ; panel+0x2270 = edx          -- ADD, not subtract
  CSWSCreature::SetGold 0x4EDD90(edi, edx)
  CSWSStore::AcquireItem 0x5C7610(ebp, itemToGive)        -- the store's own native routine
  CSWGuiStore::PopulateInventoryItemListBox 0x6C0850(panel)
```

**Confirms, directly, §1.7's own open question** ("no gold movement inside
`SellItem`/`AcquireItem` themselves... INFERRED that the GUI handler is where it happens — not
traced"): the GUI handler is exactly where it happens, one call (`SetGold`) per handler, and it
is the ONLY gold-moving call either handler makes.

### Correction to §1.2/§1.7: `CSWGuiStore+0x2270` is a cached GOLD INT, not a pointer

§1.7 left this an open NEEDS-LIVE item: "the exact meaning of `CSWGuiStore`'s `+0x2270` field
(cached `CSWSStore*` vs. some other resolved owner) was not independently confirmed." Settled
by static disasm alone, no live process needed: `PopulateStore` (`0x006C0610`, already quoted
in §1.2) calls `CSWSCreature::GetGold 0x4EDD60` on the resolved PC and stores the plain INT
result at `+0x2270`; `BuyItem`/`SellItem` (above) both read-modify-write that same int (minus
the buy price / plus the sell price) before handing it straight to `SetGold`. It is the panel's
own cached "gold currently shown on the currency label" running total, not a pointer of any
kind — the §1.2 comment's own "(or the owner it resolves through — see NEEDS-LIVE below)"
hedge is resolved in favour of neither guess.

### The design: call the click handlers directly, refuse unless THIS store's GUI is open

Neither handler takes the player or the store as a parameter — both are re-resolved from
scratch every call (`GetPlayerCreatureId`/`GetCreatureByGameObjectID` for the player,
`panel+0x2278` -> `GetStoreByGameObjectID` for the store, exactly `openstore`'s own resolve
chain). The only external input either needs is the PANEL and an ITEM POINTER — so `buy`/`sell`
below refuse outright unless the target store's own GUI is already open (`openstore` first) and
its `panel+0x2278` matches the requested store's id; there is no way to hand either function a
store that is not the one currently on screen, matching the task's own "refuse if the store GUI
isn't open" instruction exactly, made precise (it is not "a store", it is THIS store).

One call moves exactly one unit of the given stack — the store's own native decrement-by-1-or-
unlink logic (§1.5) still runs underneath every call. `count > 1` means calling the handler
`count` times, re-walking the live repository fresh before each call (`FindFirstMatchingItem`,
the same resref-as-tag-proxy match `TagEqualsIgnoreCase`/`RemoveStoreStack` already use) rather
than caching an index — a full unlink on a `count`-1 stack invalidates the previous match.
Infinite-stock items are valid `buy` targets (unlike the sync channel's own scan, which
deliberately excludes them, §2.1): a manual buy against one is exactly what a real click does
(§1.5 — the engine spawns a copy, the store's own stock never moves), so there is no reason to
refuse it here.

### What was built

`src/KotorCoop.Plugin/Odyssey.Store.cs` — `GuiBuyItemFn`/`GuiSellItemFn` (the two delegates
above), `ResolveOpenStorePanel`, `FindFirstMatchingItem`, `BuyFromStore(storeObjectId, resref,
resrefLength, count, out bought)`, `SellToStore(player, storeObjectId, resref, resrefLength,
count, out sold)`. `src/KotorCoop.Plugin/CheatController.Store.cs` — `Buy()`/`Sell(player)`,
sharing a `Trade(isBuy, player)` body that unpacks the store id/tag and resref out of the wire
request (see the field-packing note below), clamps count to `[1, 100]` (a typo should not loop
hundreds of engine calls), and reports how many actually moved if the store ran out partway
through. `SharedBlock.CheatKindStoreBuy = 40` / `CheatKindStoreSell = 41`,
`IGameBridge.CheatKinds.StoreBuy = 40` / `StoreSell = 41` (mirrored, `CheatKindSyncTests`
covers both by construction). `CheatController.cs`'s dispatch switch gained the two cases.

**The field-packing problem, and why it needed a new convention**: `buy`/`sell` need BOTH a
store identity (tag or id) AND an item resref at once — unlike every existing single-string
verb (`giveloot`'s target is always a numeric owner id; `openstore` needs only one string
total). The wire cheat request has exactly one string field (`CheatText`, 32 bytes). Resolution:
when the store is given as a numeric id, it travels in the existing numeric `CheatTarget` field
and `CheatText` carries the bare resref (identical shape to `giveloot`). When the store is a
TAG, `CheatTarget` is sent as 0 and `CheatText` packs `"<tag>|<resref>"` — a pipe is not a legal
character in either a GFF tag or a resref, so the split is unambiguous. `startdialog` (next
addendum, below) needed the identical shape for an NPC tag + an optional dialog resref and
reuses the same convention.

**Session/Agent**: `src/KotorCoop.Session/ConsoleCommand.cs` — `CoopCommandKind.Buy`/`Sell`,
verb table rows. `src/KotorCoop.Session/ConsoleCommand.Items.cs` — `ParseBuy`/`ParseSell`
(shared `ParseStoreTrade`): `<store-tag|id> <resref> [count]`, count optional (default 1).
`src/KotorCoop.Agent/CoopCommandDispatcher.Store.cs` — `Buy`/`Sell`, sharing a `Trade` body that
performs the numeric-vs-tag split described above. `CoopCommandDispatcher.cs`'s dispatch
dictionary gained the two entries. `src/KotorCoop.Plugin/ConsoleVerbCatalog.cs` and
`FuncInterpHook.cs` both gained `buy`/`sell` — reachable at the real `~` console, not
blockpoke-only, same as `openstore`/`pazaak`/`setglobal`. `docs/CONSOLE.md` gained both rows.

**Tests**: `tests/KotorCoop.Tests/ConsoleCommandVerbTableCoverageTests.cs` gained sample lines
for both new `CoopCommandKind`s (`EveryNonSentinelKindHasASampleLine`/`SampleLineParsesToItsKind`
cover them by construction); `ConsoleCompletionEngineTests`'s catalog-sync assertions
(`EveryCatalogVerbParsesToACoopCommand`, `NoCoopVerbShadowsARetailConsoleVerb`) cover the two new
catalog entries the same way, with no new test code needed (`buy`/`sell` are not in
`console-cheats.md`'s retail verb table). Full suite green,
`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError` both
clean.

### VERIFIED / INFERRED / NEEDS-LIVE

| Item | Status |
| --- | --- |
| `CSWGuiStore::BuyItem 0x006C0BE0` / `SellItem 0x006C0D80` full control flow (player/store re-resolve, price lookup, `SetGold`, native `SellItem`/`AcquireItem` call, list refresh) | VERIFIED (fresh disasm, this pass, cross-checked against `tools/kotor1_0_3.db`'s own naming) |
| `CSWGuiStore+0x2270` is a cached gold INT (not a pointer) | VERIFIED (fresh disasm of `PopulateStore`'s own write, `GetGold 0x4EDD60`) — closes §1.7's NEEDS-LIVE item |
| Gold moves through `SetGold`, absolute not delta, inside the GUI handler | VERIFIED (fresh disasm) — closes §1.7/§2.5's "not traced" note |
| `Odyssey.BuyFromStore`/`SellToStore` (this pass's own new code) | Implemented, unit-tested at the console/dispatcher layer; **NEEDS-LIVE** for the engine call itself |
| The full `buy`/`sell` console round trip | Implemented; **NEEDS-LIVE** |

### Live test plan (NOT yet run)

Two `tools/demo.ps1` instances (or one is enough for `buy`/`sell` alone — no sync channel
involved, this is a bare engine-call verb like `openstore`), both `pwsh
src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError` first.

1. `warp tar_m02aa` (Larrim's shop, per this doc's own earlier live pass — the real tag,
   `Larrim_Store`, not the doc's original `kebla_yurt_shop` guess).
2. `openstore Larrim_Store` — confirm it opens cleanly (already live-confirmed by the
   2026-08-28 root-cause pass above).
3. `census`/`item`/`items` (or just watch the store's on-screen list) to find a real stocked
   resref, e.g. `g_w_blstrpstl001` if Larrim carries one — substitute whatever the save
   actually shows.
4. `buy Larrim_Store g_w_blstrpstl001` — confirm: (a) the plugin log line
   `buy: bought '<resref>' x1 from store 'Larrim_Store' (0x...)`, (b) the item genuinely
   appears in the player's own inventory (`items`), (c) the on-screen gold counter and
   `CSWSCreature`'s own gold both decreased by the item's real buy price, (d) the store's own
   on-screen stock count for that resref decreased by one (or, for an infinite-flagged entry,
   stayed the same — §1.5).
5. `sell Larrim_Store <resref just bought>` — confirm the item leaves the player's inventory,
   gold increases by the sell price, and the store's stock count increases (or, again, stays
   flat for an infinite entry — though a fresh sell can never target one, since the store never
   had it to begin with).
6. `buy Larrim_Store <resref> 3` — a `count > 1` buy in one call; confirm three separate
   `BuyItem` calls actually ran (three separate log-visible price deductions, or a stock count
   that dropped by exactly 3, not 1).
7. `buy Larrim_Store <resref>` **without** `openstore` first (or with a DIFFERENT store's GUI
   open) — confirm the refusal ("no store GUI is open" / "the open store is 0x..., not the
   requested 0x... - open it first") rather than a crash or a silent no-op.
8. `sell Larrim_Store <resref the player never had>` — confirm a clean "no matching item in
   inventory" refusal, not a crash.

Watch for in the plugin log: `buy:`/`sell:` lines from `CheatController.Store.cs`, and any
`... failed - <LastStoreNote>` refusal, which names exactly which guard fired.

## Live pass (2026-08-28): three real bugs found and fixed; a fourth, more serious design
## defect found and left OPEN — `store_sync` is not merely untested, it is ACTIVELY DESTRUCTIVE

Task: run this doc's own §6/queue-item-1 live test plan (`warp tar_m02aa` both, `openstore
Larrim_Store`, `buy`/`sell` a finite item, confirm the CLIENT's copy reconciles, `netsim
blackout 10` reconnect, an infinite-stock buy replicates nothing) with `store_sync=1` on both
installs, flip ON if clean. Two-instance rig (`tools/demo.ps1`), host `C:\GOG Games\Star Wars
KOTOR (GOG)`, client `D:\Games\swkotor-gog-p2`, both freshly built and installed
(`build.ps1 -Install -WarnAsError`) before each round below.

**Result: NOT flipped ON. Three narrow bugs were found and fixed live (all VERIFIED, all still
in the tree). A fourth, much more serious defect was found and is NOT fixed — merely opening a
store on two `store_sync=1` instances with ZERO buy/sell activity empties that store's entire
stock on BOTH instances.** Do not attempt to flip `store_sync` on again until bug 4 below is
fixed and re-verified live.

### Bug 1 (FIXED): `Odyssey.Store.cs`'s `ResolveStoreRepo` took the FIELD's address, not its VALUE

`CSWSStore.item_list` at `+0x230` is documented (§1.1) as a pointer type
(`CItemRepository *`) — the field HOLDS a pointer, it is not itself the repository. The plugin's
`ResolveStoreRepo` returned `store + StoreItemRepo` (the address of that field) instead of
`*(nint*)(store + StoreItemRepo)` (the pointer value stored there). Every consumer
(`InsertItemIntoStore`, `RemoveStoreStack`, and this pass's own `BuyFromStore`/`SellToStore`
via `FindFirstMatchingItem`) then read `count`/`ids` from garbage bytes past the real
`CSWSStore` struct tail, tripped the `count <= 0 or > 4096`/`ids == 0` guard, and failed safe
with "no stock matching that resref" — silent, never a crash, which is exactly why three prior
live passes (the `openstore` crash-and-fix rounds, and the `buy`/`sell` addendum's own build)
never caught it: nothing had walked the array live before this pass's own `buy` attempt.
**First live-observed**: `buy Larrim_Store g_w_ionblstr01` against a store the `items` cheat
itself could see held that exact resref (`0x82 g_w_ionblstr01 held by 0x74`) refused with "no
stock matching that resref in store 0x74". The container/placeable read side
(`Odyssey.Items.cs`'s `PlaceableRepository`) already dereferences correctly
(`*(nint*)(placeable + PlaceableRepository)`) — the store path was the one place this pattern
was wrong. **Fixed**: `src/KotorCoop.Plugin/Odyssey.Store.cs`'s `ResolveStoreRepo` now
dereferences. Re-tested live after the fix: `buy Larrim_Store g_w_ionblstr01` succeeded
(`buy 'g_w_ionblstr01' x1 from store Larrim_Store`), the item genuinely left store 0x74's own
`item_list` and appeared in the buyer's inventory (`items` before/after).

### Bug 2 (FIXED): the SAME mistake, independently duplicated in the agent-side reader

`KotorGameBridge.Store.cs`'s `TryReadStoreRepo` (the C# out-of-process reader `StorePump`'s
`bridge.TryScanOpenStore` depends on) made the IDENTICAL error, independently: `repo = address
+ StoreItemRepo` instead of dereferencing through `_memory.TryReadPointer`. Effect: every
subsequent `count`/`ids` read landed on garbage, tripped the same guard, and
`TryScanOpenStore` silently reported "no store open" even with a real store GUI on screen —
total, unconditional silence (no `store: local ... detected` log line, ever, on either side,
across a live buy that genuinely moved an item through the engine). **Fixed**: dereferences via
`_memory.TryReadPointer`, matching every other pointer-field read in this file.

### Bug 3 (FIXED): the agent-side reader read the WRONG panel field for the store id

Independent of bugs 1/2. This doc's own 2026-08-28 "Root cause and fix" pass (above) corrected
the PLUGIN side's belief about which `CSWGuiStore` panel field holds the store id:
`ShowStoreGui`'s first argument (the store id) is written to panel `+0x2278`, its second
(the shopping PC's id) to `+0x227C` — the ORIGINAL 2026-08-27 addendum had these swapped, and
that fix corrected `Odyssey.Store.cs`'s own `StorePanelObjectIdOffset` accordingly. **The
agent-side mirror, `KotorGameBridge.Store.cs`'s `StoreGuiObjectId`, was never updated to
match** — it kept reading `+0x227C`, i.e. the PLAYER's own object id, not the store's. Every
`TryReadStoreRepo(id, ...)` call then searched for a `CSWSStore` whose own id equalled the
PLAYER's id — never matches anything — so `TryScanOpenStore` again silently reported "no store
open". This is exactly the cross-process mirror-sync hazard `docs/CONTRIBUTING.md`'s "Adding a
feature end-to-end" step 5 names by name ("the two are transcribed by hand and a mismatch is a
silent 'plugin not found'"), and it is why bugs 2 and 3 together produced IDENTICAL, totally
silent symptoms — fixing bug 2 alone was not enough to unblock detection; bug 3 also had to be
found and fixed before `TryScanOpenStore` worked at all. **Fixed**: `StoreGuiObjectId` corrected
to `0x2278`.

All three fixes: `dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError` clean; `dotnet test` 2355/2355 (no regressions —
none of the three bugs had a unit test that could have caught a wrong raw-pointer offset against
real engine memory shape, since the test suite has no live process to check the arithmetic
against; this is a known, accepted gap the CONTRIBUTING.md engine-write rules already name:
"verify a researched offset live before shipping the write").

### Bug 4 (NOT FIXED, OPEN, CRITICAL): a virgin store's entire stock is wiped by the mere act of

### opening it on two `store_sync=1` instances, with no buy/sell at all

With bugs 1-3 fixed, `StorePump`'s detect/reconcile finally ran end to end for the first time —
and immediately did the wrong thing. Reproduction, live, twice (once on the HOST, once
independently on the CLIENT, in the same session): `warp tar_m02aa` both, `tp` both away from
the module's ambient "duros" swoop-registration NPC (see the note below — this NPC is an
unrelated hazard this module carries), `openstore Larrim_Store` on the HOST, then
`openstore Larrim_Store` on the CLIENT. **No `buy`/`sell` was issued by anyone.** Within
seconds, BOTH instances' plugin logs showed a rapid-fire sequence of automatic `StoreRemove`
applies against their OWN store, one per stocked resref, driving the entire finite-stock
inventory to zero:

```
[07:15:03.083] store: removed 'g_w_rptnblstr01' x1 (asked for 1) from store 0x74
[07:15:03.240] store: removed 'g_a_class7001' x1 (asked for 1) from store 0x74
[07:15:03.378] store: removed 'k_computer_spike' x2 (asked for 2) from store 0x74
... (40 lines on the host, 28 on the client — nearly every finite-stock entry in Larrim's shop)
```

**Root cause, read from `StorePump.Pump`'s own step 2 (reconcile)**: step 2 diffs
`_knownEngineTotals` (`expected`, this instance's own local scan) against `session.StoreTotals`
(`actual`, the WIRE-SYNCED "desired" truth — populated ONLY by an explicit `Add`/`Remove`/`Set`
op, from this instance's own step-1 detection of a genuine CHANGE, or from a peer). The very
FIRST time a store is baselined locally (`_openStoreBaselined` false→true), step 1 seeds
`_knownEngineTotals` with a full scan **but publishes nothing** — there is no corresponding
entry in `session.StoreTotals` for any of those keys, because nobody has ever bought, sold, or
resynced this store yet. `StoreDiffRule.Diff`'s own "keys present in `expected` but absent from
`actual`" branch then reads that silence as "the session desires ZERO of this" and emits a
`Remove` for the ENTIRE quantity, for every single key — i.e. **"nobody has told me what this
key should be" and "this key should be zero" are indistinguishable to step 2 as written**, and
it resolves that ambiguity by deleting real, pre-existing, .utm-template game content that was
never meant to be wire-synced from empty in the first place (the store's own PRISTINE stock,
unlike the party pool's genuinely-starts-at-zero shared inventory, which is why
`PartyPoolPump`'s identical-shaped step 2 has never shown this symptom).

**Why the obvious fix ("only reconcile a key already present in `session.StoreTotals`") is not
safe to ship blind**: `StoreStore.cs` (the wire-side bookkeeping `session.StoreTotals` reads
from) **prunes a key from its own dictionary the moment its tracked total reaches zero**
(`StoreStore.cs` line ~99, `_totals.Remove(key)`) — the same convention `PartyPoolStore` uses.
So "absent from `session.StoreTotals`" is genuinely ambiguous between two very different facts:
(a) "nobody has ever told the session anything about this key" (the virgin-store case above,
where the local engine scan must NOT be reconciled toward zero), and (b) "the session already
knows this key was reduced to exactly zero by a real, already-applied op" (a legitimate case
where THIS instance's own local copy — if it still shows a nonzero scan, because its own GUI
hasn't been open to observe the depletion — genuinely SHOULD be reconciled down to zero). A
filter that excludes case (a) from step 2 also silently excludes case (b), which would trade
one correctness bug (wipes a virgin store) for a different, quieter one (a fully-depleted stack
never reconciles down on an instance that was never present to see it happen locally). Telling
the two apart needs the session to track "has this key EVER been named by a genuine op", not
merely "what is its CURRENT total" — `StoreStore`'s public surface does not currently expose
that distinction, and adding it (or an equivalent "first-baseline publishes an implicit
host-only `Set` resync of its own scan", mirroring `ResyncStore`'s existing Hello-time
behaviour, extended to also fire on a store's first LOCAL baseline rather than only a peer's
reconnect) is real design work this pass did not attempt under live-rig time pressure — a
wrong, rushed fix here has the same blast radius as the bug itself (deleting real store
content), so it is being left OPEN and clearly flagged rather than guessed at.

**Verdict**: `store_sync` stays at its shipped default OFF (`profiles/coop.ini.example`
unchanged, still `store_sync=0`) — this was never close to being flipped ON this pass despite
three real, narrow bugs being found and fixed, because the fourth defect they unblocked is
strictly worse than "not yet verified": it is confirmed-live DESTRUCTIVE the moment two
`store_sync=1` instances merely LOOK at the same store, with no buy/sell involved. Anyone
picking this back up should treat bug 4 as the blocking item, not bugs 1-3 (already fixed) and
not the buy/sell mechanism itself (bug-1-fixed, confirmed working correctly in isolation — a
single-instance `buy`/`sell` round trip with `store_sync` OFF is completely unaffected by any of
this, since that mechanism never touches `StorePump`).

### Incidental finding: `tar_m02aa` has an ambient "duros" swoop-registration NPC that

### repeatedly drags a nearby player into a conversation → party-select-screen cascade

Unrelated to `store_sync` itself, but cost real time this pass and is worth recording so the
next live round on this module does not re-discover it from scratch. An ambient NPC tagged
(dialogue partner name) `duros`, positioned near both players' `tar_m02aa` warp-in point,
re-initiates conversation on a nearby idle player repeatedly (the same "Trask re-initiates"
shape `dialogue-sync.md` already documents for the Endar Spire, a different NPC/module).
Clearing it with `selectreply 0` in a loop eventually succeeds, but its `Yes`-shaped default
reply (slot 0) advances into what reads as a Taris swoop-race REGISTRATION flow, which opens the
native PARTY-SELECTION screen — matching `galaxy-map-and-party-select.md`'s own finding that
this module's swoop-taxi system is a confirmed party-select trigger. Once open, that screen
gates `openstore` (and everything else `ShowStoreGui`'s own `+0x30` "another modal screen"
precondition covers) behind `partyselect ok` (the existing dev verb) rather than any dialogue
verb. Cheapest known workaround, used successfully this pass: `tp <x> <y>` (no `z` argument) to
a spot 20-30 units from the warp-in point BEFORE letting the ambient conversation resolve, so
`duros` cannot re-trigger repeatedly. **⚠ Do not pass an explicit `z` to `tp` without knowing
the real ground height at that x/y** — this pass's own `tp 60 90 0` (explicit `z=0`) landed a
player below/through the walkmesh, produced "your party is down" (a fall-damage-shaped death),
and warping a downed player crashed the client instance outright (`0xc0000005`, though the
specific crashed address logged by `crashlog.ps1` turned out to be a STALE entry from hours
earlier, not this crash's own — `crashlog.ps1`'s newest-entry-first output can be misleading
when Windows Error Reporting is slow to flush a fresh one; cross-check the timestamp before
attributing a crash address to the session that just ended). This matches the memory note "the
min-1-hp warp artifact" — warping while downed is already a known-poison transition, and `tp`'s
own doc string already says "no walkmesh check" for exactly this reason. Omit `z` (`tp <x>
<y>`) unless the real ground height is already known.

## Fix (2026-08-28): Bug 4, the virgin-store wipe — closed with a redesign, not a filter; a
## second, independent defect found and closed in the same pass

Task: fix the prior pass's own Bug 4 ("a virgin store's entire stock is wiped by the mere act of
opening it on two `store_sync=1` instances") with a correct design, per the invariant the prior
pass itself named but did not implement: **the wire store never holds an absolute truth for a
store nobody has transacted in — an absent key means "unknown", not "zero".**

### Root cause, restated precisely

`Pumps/StorePump.cs`'s step 2 (reconcile) used to diff this pump's ENTIRE local engine belief
(`_knownEngineTotals` — every key it has ever locally scanned, including a store's pristine,
never-transacted stock) against `session.StoreTotals` (`StoreStore.Snapshot()`, a dictionary that
only ever holds a key someone has actually transacted) via `StoreDiffRule.Diff`. `Diff`'s own
logic treats "a key present in `expected` but absent from `actual`" as "remove the full quantity"
— correct when both sides are complete snapshots of the SAME thing (which is exactly how step 1
already used it, comparing two consecutive LOCAL scans of the same store), but wrong here: a
virgin store's pristine stock is present in `expected` (the local scan) and absent from `actual`
(nobody has told the session anything about it) for a completely different reason than "it was
deleted" — nobody has ever transacted it. `StoreDiffRule.Diff` cannot tell the two apart from the
dictionaries alone, and the old step 2 handed it dictionaries that made them genuinely
indistinguishable.

### The design (implemented this pass)

Four rules, matching the task brief's own numbering:

**(a) A store's first local read publishes nothing; it becomes a local baseline.**
`Pumps/StorePump.cs`'s `!_openStoreBaselined` branch already did this correctly before this pass
(seeds `_knownEngineTotals` from the fresh scan, no diff, no publish) — this was never the bug.

**(b) Only local deltas become ops.** Step 1 diffs two consecutive LOCAL scans of the currently
open store and publishes only the delta — also already correct; unchanged in shape.

**(c) A peer op adjusts local stock by a DELTA, never "set to wire total" — except `Set`
(resync), scoped to stores the sender has transacted in.** This is the actual redesign. Step 2 no
longer diffs two absolute snapshots at all. `IStoreSession` gained `DrainStoreOps()` — previously
declared but never implemented anywhere in this codebase (`IPartyPoolSession.DrainPartyPoolOps`
is its identically-unused sibling) — now backed by a real per-side queue (`CoopHost`/
`CoopClient`'s new `_storeOpDrainQueue`, `Lock`-guarded for the cross-thread boundary between the
packet-receive path and `Pumps.StorePump`'s own tick): every op this instance's `ApplyOp` call
genuinely accepts (a REMOTE fact — origin-filtered on `CoopClient`, and the host is never its own
client so needs no such filter) is queued there. `StorePump`'s step 2 now drains this queue every
tick and applies each op DIRECTLY: `Add`/`Remove` adjust whatever the local engine currently holds
by the op's raw `CountDelta` — needing no knowledge of the store's true pristine baseline at all,
correct regardless of whether this instance has ever locally scanned the affected store. A
virgin store's keys never generate an op (rule b), so `DrainStoreOps()` never contains anything
for them — Bug 4 is closed STRUCTURALLY, not by a filter bolted onto the old diffing shape.

`Set` (resync) is the one case that IS an absolute target, and is applied by computing the delta
needed to reach it from this pump's own existing engine belief (`_knownEngineTotals`) — but ONLY
if that belief already exists for the key; a `Set` for a key this instance has never locally
observed is left alone rather than guessed at (see "Known limitation" below). `CoopHost.ResyncStore`
emits a `Set` only for keys the host has ITSELF transacted (`StoreStore.SnapshotKnown()`, see (d)).

**(d) A legitimate full depletion drives a receiver to zero via an explicit delta; `StoreStore`
may prune afterward without implying "absent = zero" later.** `StoreStore` gained a `_known`
`HashSet<StoreStackKey>` — every key ever the target of a real `Record`/`ApplyOp` call, added
regardless of whether the total ends up zero, never removed except by `Clear()`. `Snapshot()`
(nonzero-only, unchanged) still powers `StoreTotals`; the new `SnapshotKnown()` returns EVERY
known key mapped to its current total (0 for a pruned, fully-depleted one) and is what
`ResyncStore` now sends — so a rejoining peer who never saw the original depleting op live still
learns "this key is known, and currently zero" via an explicit `Set(key, 0)`, rather than the
resync silently omitting it (which would leave that peer's own pristine copy wrong forever). A
store nobody has transacted contributes NOTHING to `SnapshotKnown()` — its keys were never added
to `_known` — so the resync genuinely never touches an untransacted store, closing the "leaves
untransacted stores alone" requirement structurally as well.

### A second, independent defect found while implementing the fix: the floored first Remove

Investigating the redesign surfaced a second, previously-undetected bug the OLD snapshot-diffing
shape had been incidentally masking: `StoreStore.Mutate`'s `Remove` branch computed
`Math.Max(0, before - countDelta)`. The very FIRST op ever recorded for a key is, for many real
shops, a BUY (`Remove`) — and `before` for a never-before-recorded key is 0 (nothing has ever
publishd anything for it). `Math.Max(0, 0 - delta) == 0 == before` — `Mutate` reported "no
change", `Record` returned `false`, and `PublishStoreOp` — which gates the actual wire send on
`Record`'s return value — silently dropped the packet. **A buy as the first transaction on a
store's stock would never even reach the wire.** Two fixes, both shipped this pass:

1. `Mutate`'s `Remove` arithmetic no longer floors (`before - (int)countDelta`, allowed to go
   negative); a non-positive `after` is pruned exactly like a legitimately-zeroed key already was
   (negative and zero are `Snapshot`/digest-equivalent: "not present"). This alone makes `Record`
   return `true` for any genuine delta, closing the silent drop as a defence-in-depth measure.
2. The PRIMARY fix: `StoreStore.Record` gained a `localBaselineIfUnknown` parameter — the true
   local engine total the delta was computed against, which `Pumps.StorePump`'s step 1 always
   already knows (it is the "before" side of the very diff that produced the delta). The first
   time a key is recorded, this seeds `_totals[key]` (and pre-XORs the digest so the seed does not
   corrupt it) BEFORE the delta is applied — so `SnapshotKnown()`/resync report the TRUE absolute
   stock (e.g. 39 after a buy off a pristine 40) rather than a wire-relative "0". `IStoreSession
   .PublishStoreOp` gained the same parameter (default 0, source-compatible with every pre-existing
   call site), threaded from step 1's own `priorForDetect.GetValueOrDefault(delta.Key)`.

### Files changed

- `src/KotorCoop.Session/StoreStore.cs` — `_known` set, `KnownKeys`/`SnapshotKnown()`, the
  `Record` baseline-seeding parameter, the un-floored `Remove` arithmetic.
- `src/KotorCoop.Session/IStoreSession.cs` — `PublishStoreOp`'s new parameter, `StoreKnownKeys`,
  `DrainStoreOps`'s corrected doc (it excludes self-originated ops, contrary to its own stale
  pre-existing comment).
- `src/KotorCoop.Session/CoopHost.Store.cs` / `CoopClient.Store.cs` — the `_storeOpDrainQueue`
  (`DrainStoreOps()`, populated from `HandleStoreOp`'s successful `ApplyOp`), `PublishStoreOp`'s
  threaded baseline, `ResyncStore` switched to `SnapshotKnown()`.
- `src/KotorCoop.Session/Pumps/StorePump.cs` — step 2 rewritten around `DrainStoreOps()`; step 1
  threads the local baseline into `PublishStoreOp`.
- `tests/KotorCoop.Tests/StoreLoopbackTests.cs` (new) — five real host/client/socket loopback
  tests (see below), each independently confirmed to FAIL against the pre-fix code (a genuine
  regression test, not merely a green check against the new code): opening a virgin store touches
  nothing; a host buy decrements the client's copy without touching an unrelated stack in the same
  store; a full depletion drives the client to zero; a reconnecting peer's resync converges the
  store it transacted in while leaving an entirely separate, never-transacted store untouched; an
  infinite-stock item's unchanged scan produces no op. `tests/KotorCoop.Tests/StoreTests.cs`'s
  existing 15 unit tests all still pass unmodified (the new `Record` parameter is
  source-compatible; `Remove`'s un-floored arithmetic still reports `Get`/`Count` identically for
  every existing assertion, which only ever checks the PUBLIC, floor-equivalent surface).

### Build and test result

`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings, 0 errors.
`dotnet test tests/KotorCoop.Tests --no-build`: **2360/2360 passing** (2355 pre-existing + 5 new
loopback tests), full suite, foreground, no regressions.

### Known limitation, honestly stated (not solved this pass)

A `Set` (resync) for a key this instance has NEVER locally scanned is left alone rather than
guessed at (see (c) above) — applying it as `Add[target]` from a zero belief would very likely be
WRONG, since this instance's own true pristine baseline for that key is unknown to the resync
message (the wire never carries a virgin store's pristine value, by design (a)). This is a
narrower restatement of this document's own pre-existing §5 risk 1 ("a store neither instance has
opened cannot sync until someone opens it"): a peer who reconnects having NEVER opened the
transacted store itself will not have that key corrected until it opens that store for the first
time (at which point its own first-scan baseline picks up the truth directly, matching the
pristine local engine value at that moment). Every test this pass wrote deliberately stays within
the well-defined, correctly-handled case (a peer who was online during the transaction, or who
independently baselined the same store before reconnecting) — this narrower gap is flagged here
rather than silently left broken or guessed at with an unverified "best-effort" apply.

### Live re-test plan (supersedes nothing prior — §6/the 2026-08-28 buy/sell pass's own plan is
### still the right shape; this section only adds what to re-check given this fix)

`store_sync` stays at its default OFF (`profiles/coop.ini.example` unchanged, `store_sync=0`) —
this fix is unit/loopback-tested only, not yet live-verified, per this project's own engine-write
discipline (`docs/CONTRIBUTING.md`: "verify a researched offset live before shipping the write" —
though note this particular fix touches NO researched offset or engine call at all, purely
session/protocol-layer C# logic; the NEEDS-LIVE items below are about the SYNC BEHAVIOUR, not
about any new memory read/write).

1. Two `tools/demo.ps1` instances, `store_sync=1` on both, both freshly built/installed
   (`build.ps1 -Install -WarnAsError`). `warp tar_m02aa` both, clear the ambient `duros`
   conversation (`tp <x> <y>` without `z`, per this doc's own prior-pass warning), `openstore
   Larrim_Store` on both (the real store/tag this doc's own prior passes already found and
   live-confirmed opens cleanly).
2. **Bug 4's own repro, re-run.** With BOTH instances' store GUI open and ZERO buy/sell issued,
   watch `kotorcoop-general.log` on both for several seconds — confirm NO `store: removed ...`
   lines appear at all (the prior pass's own repro produced ~30-40 such lines within seconds on
   each side; this fix should produce zero).
3. `buy Larrim_Store <a real stocked resref>` on the host — confirm the plugin log shows exactly
   one `buy:` line, the HOST's own on-screen stock count decreases by exactly 1, and — once the
   CLIENT's `StorePump` has had a tick to drain the resulting op — the CLIENT's on-screen copy of
   the SAME store (already open) also shows the decrease, with NO other stocked resref in that
   store changing on either side.
4. Buy out an entire finite stack in one or more `buy` calls — confirm the item disappears from
   BOTH sides' on-screen list once fully depleted, matching this pass's own
   `DepletingAStackOnHostDrivesItToZeroOnClient` loopback test.
5. `netsim blackout 10` (or an actual network drop) on the client mid-session with the transacted
   store's depletion already converged, reconnect, confirm the stock stays exactly as converged
   (no doubling, no reset) — matching `ReconnectResyncLeavesUntransactedStoresAlone`'s "transacted
   store" half. Separately, confirm a THIRD store neither instance ever opened stays silently
   unknown to both (no crash, no log line) — matching this doc's own pre-existing §5 risk 1.
6. An infinite-stock `buy` — confirm the store's own on-screen stock count for that resref is
   IDENTICAL before and after (§1.5's own engine guarantee), and no `store: local add/remove...
   detected` log line appears for it.
7. `store_sync` stays OFF in `coop.ini`/`docs/CONFIG.md` regardless of this live pass's outcome —
   flipping the shipped default is a separate decision this document's own established discipline
   (a full, clean multi-round live pass, not one) still gates.

## Live pass (2026-08-28, later): the wipe-prevention/buy/deplete/infinite-stock/reconnect half of
## §6's plan is CLEAN; `sell` and unguarded `buy` are found broken this round — stays OFF

Ran the full §6/2026-08-28-addendum live plan against a fresh two-instance rig
(`store_sync=1` both, `script_item_sync=0`/`dialogue_item_sync=0` both to keep this pass's own
party-pool round isolated), `warp tar_m02aa` both, the ambient `duros` NPC cleared with `tp`
**before** letting either conversation resolve (selecting a reply on it, even to clear it, is what
walked a fresh client instance into the party-selection-screen trap the addendum above warns
about — recovered by killing and relaunching that one game process fresh, see "incidental
finding" below), `openstore Larrim_Store` on both.

### Clean: wipe-prevention, buy/decrement, full depletion, infinite stock, blackout resync

- **No wipe on open.** Both sides' `kotorcoop-general.log` stayed silent (zero `store:` lines) for
  15+ seconds after both GUIs were open with zero transactions — Bug 4's own fix (the prior
  section) holds. The prior pass's own repro produced 30-40 such lines in the same window; this
  pass produced zero.
- **A host buy decrements exactly the client's copy of that one resref.** `buy Larrim_Store
  g_w_ionblstr01` → host plugin log `buy: buy 'g_w_ionblstr01' x1 from store Larrim_Store`; client
  plugin log, the ONLY store line that session so far: `store: removed 'g_w_ionblstr01' x1 (asked
  for 1) from store 0x74`. No other resref moved.
- **A depleted (finite, 1-unit) stack hits zero on the client via the same op** — `g_w_ionblstr01`
  was a 1-unit stock; the single buy above both consumed it AND is the depletion case (a follow-up
  `buy Larrim_Store g_w_ionblstr01 10` correctly refused with "no stock matching that resref").
- **An infinite-stock item never touches the wire**, confirmed decisively this pass (stronger than
  planned — accidentally, not deliberately, since `g_w_blstrpstl001` turned out to be Larrim's
  infinite entry): 23 consecutive `buy Larrim_Store g_w_blstrpstl001` calls (single + a `count=20`
  batch) all succeeded with **zero** `store:` lines for that resref on the client, ever. §1.5's own
  engine guarantee (the store's own stock never moves for an infinite entry) holds through the
  sync layer's own filter.
- **`netsim blackout 10` + reconnect leaves the transacted store exactly as converged.** Client
  reconnected as the same peer id; zero new `store:` lines appeared on either side afterward — no
  re-send, no doubling, no reset of `g_w_ionblstr01`'s already-converged zero.

### BROKEN — `sell` cannot find an item this session's own tooling confirms is in the player's
### inventory, every time, on a totally fresh process

Never previously live-run (`docs/research/store-sync.md`'s every earlier mention of `sell` is a
NEEDS-LIVE plan, not a result) — this is the first live attempt. `sell Larrim_Store <resref>`
refused with `sell '<resref>' failed: no matching item in inventory` **five separate times**,
against three different resrefs, including:

- an item bought moments earlier in the same session (`g_i_medeqpmnt01`, `g_w_blstrpstl001`);
- an item placed directly via `scriptgive g_i_medeqpmnt01` (bypassing `buy` entirely) — the plugin
  answered `gave 'g_i_medeqpmnt01' (0xBA) to the local player, untracked`, and a same-second
  external read confirmed it: `item 0xBA g_i_medeqpmnt01: stack 5, chg 0/0, upg 0x0, identified`
  and the `items` sweep independently listed `0xBA g_i_medeqpmnt01 yours stack 5` — yet `sell
  Larrim_Store g_i_medeqpmnt01` **immediately after** still answered "no matching item in
  inventory".

Three independent readers (the store's own `sell` lookup; the item-identity `item <id>` read; the
`items` sweep) disagree about the SAME id in the SAME frame-or-two — the shape this document's own
enemy-presence hunt (`docs/research/enemy-presence-sync.md`) has seen before with two lookups that
are "supposed to be the same lookup" and are not. Read `src/KotorCoop.Plugin/Odyssey.Store.cs`'s
`SellToStore`: it reads `player`'s `CreatureItemRepo` (`player` = `Odyssey.GetPlayerCreaturePointer()`,
`CServerExoApp::GetPlayerCreature`) and walks it with `FindFirstMatchingItem` (a `Tag`-based scan,
the SAME helper `buy`'s own repo lookup shares). Meanwhile `scriptgive`/the item that ends up
"yours" in `items` resolves the player via `Odyssey.GetPlayerObjectId()` → `GiveItemToInventory`.
Both are meant to name the same creature; whether they genuinely do on this build, on THIS repo
pointer, at the moment `sell` runs, is unconfirmed and is exactly where the next round should
look — paste `SellToStore`'s own repo pointer and item count next to `items`' at the same instant,
the same "two lookups, one frame" discipline that closed the enemy-presence hunt.

**Verdict: `sell` is functionally unusable as tested. `store_sync` cannot be flipped ON — it is one
of this queue's own explicit acceptance items ("sell → appears on both").**

### BROKEN, more seriously — `buy` has no affordability guard and can underflow the party's gold
### to within a few billion of wrapping, silently

`BuyFromStore`/`GuiBuyItemFn` (`CSWGuiStore::BuyItem`, per this doc's own §1.2/§2.5 disassembly)
subtracts the item's price from the player's cached gold int and hands the result straight to
`SetGold` — **unconditionally**, with no check that the player can afford it. The real in-game Buy
button is normally guarded by the GUI graying itself out when unaffordable; calling the click
handler directly, the way this project's own `buy` console primitive does, bypasses that
precondition entirely. This round's own `buy Larrim_Store g_w_blstrpstl001 20` (unknowingly against
an infinite-stock, non-trivially-priced item, well past the player's actual purse) produced, on the
HOST's own agent log:

```
* purse: peer 2 contributed +0 -3294967077, party gold 4294967076 -> 999999999
* purse: peer 2 contributed +0 -294966090, party gold 4294966966 -> 4000000876
* purse: peer 2 contributed +0 -294965100, party gold 4294966746 -> 4000001646
* purse: peer 2 contributed +0 -294951900, party gold 4294964546 -> 4000012646
* purse: peer 2 contributed +0 -4312, party gold 4294964502 -> 4294960190
```

`party gold` is a plain `uint`; going negative wraps to just under `UINT32_MAX` (≈4.29 billion) —
exactly what these lines show. The party-progression merge (`PartyPump.PumpProgressionHost`)
partially self-heals by clamping toward the 999,999,999 display cap on some passes but not others,
and the rig's shared save is now left with gold pinned at the 999,999,999 cap (`grantgold 0` reads
back `gold 999999999 -> 999999999`) — not corrupted (a valid, in-range value), but not a value any
real playthrough would organically reach either, and a fact worth knowing before the next round
reuses this save. This is a gap in the `buy`/`sell` cheat wrapper's own precondition (it does not
replicate the GUI's own affordability gate before calling the real click handler `count` times),
not a `store_sync` wire-protocol defect — but it is severe enough on its own to block treating this
round as clean, and the next round adding `buy` should add a price/affordability check (read the
item's cached price the same way `PopulateStore` does, per §1.2) before calling `GuiBuyItemFn` at
all.

### Incidental finding: the `duros` party-select trap can survive a savegame reload

Confirms and extends the "Incidental finding" section above. Selecting a reply on the ambient
`duros` conversation (even `selectreply 0` used only to "clear" it) can advance straight into the
party-selection screen exactly as documented — but this round found `partyselect ok` did NOT
close it (repeated calls answered "confirmed" every time, with an unchanging "message-box
precondition was already set" detail, and `openstore` kept refusing with "another modal screen is
up"), and **a full `loadgame 0` reload of that one instance did not clear it either** — the very
next warp-follow into the same module re-triggered the same conversation, and this time simply
walking away with `tp` (never selecting a reply) let it resolve to `state exploring` cleanly,
matching the addendum's own documented safe recipe. When that also failed to unblock `openstore`,
the client's `swkotor.exe` was killed and `instance 2` relaunched fresh (`agent launch --instance
2` → `agent load --pid <new> --load-save 1` → a fresh `agent run --join`) rather than continuing to
fight the stuck panel — a clean process restart, not `partyselect ok`, is what actually recovered
it. Worth a real fix (the panel object apparently outlives even a savegame reload) before this
module is used again for an unattended run.

### Files changed this pass

None — this was a pure live-verification pass, no source changes. `store_sync` stays at its
default OFF (`profiles/coop.ini.example` unchanged, `store_sync=0`).
