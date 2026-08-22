# Container/corpse open-close sync and the use-lock (research + build, 2026-08-22)

Goal: when a player opens a shared container (chest/corpse/footlocker), (1) every other
instance shows it open too, (2) closing the panel closes it everywhere, and (3) a second
player is refused from opening a container someone else already has open — the same
one-at-a-time discipline `InteractionKind.Conversation` already gives conversations, extended
to `InteractionKind.Container` (protocol version 37).

House assumptions carried over from `object-model.md` and `placeables-containers.md`: no ASLR
(base `0x400000`, disk==memory), symbol DB `tools/kotor1_0_3.db` sha256-matched to this exe,
GOG v1.0.3, every address below directly usable. Nothing in this pass was run against the
game — static RE, implementation and tests only; see "Live-test script" below for what a rig
session still needs to run.

---

## 1. Engine facts

### 1.1 The choke point: `CSWSPlaceable::OpenInventory` 0x00587420 (VERIFIED, full disasm)

`tools/xrefs.py 0x00587420` finds exactly **three** call sites in the whole binary:

| Caller | Offset | Address |
| --- | --- | --- |
| `CSWSObject::AIActionUseObject` **0x0057E8C0** | +0x5e3 | 0x0057EEA3 |
| `CSWSObject::AIActionUseObject` **0x0057E8C0** | +0x8cc | 0x0057F18C |
| `CSWSPlaceable::EventHandler` 0x00587BA0 (the OnUsed arm) | +0xa6e | 0x0058860E |

⚠ **Correction to an address in the task briefing and in `CheatController.cs`'s own existing
door-notes comment**, which cite `AIActionUseObject` at `0x0057ECC0`. The symbol database and
this pass's own xref arithmetic (`0x0057E8C0 + 0x5e3 = 0x0057EEA3`, `0x0057E8C0 + 0x8cc =
0x0057F18C`, both matching the raw scan exactly) agree on **`0x0057E8C0`**. The pre-existing
comment is left alone (out of scope for this pass, and it does not affect anything built
here), but a future reader chasing that address should use `0x0057E8C0`.

So **every path that opens a container's loot panel — a click via `AIActionUseObject`, or a
script's own `OnUsed`-arm dispatch — funnels through `OpenInventory`**, on whichever
instance's own local player is doing the using (each instance runs its own client+server
halves internally, even in solo play — see `docs/research/client-server-halves.md`-class
notes). This is the one place to hook for both detection and refusal.

Full disassembly (`tools/disasm.py 0x587420 350`):

```
0x00587420  mov eax, fs:[0]            ; SEH prologue (13 bytes total incl. the two below)
0x00587426  push -1
0x00587428  push 0x7204bb              ; the SEH handler, an absolute constant
0x0058742D  push eax
0x0058742E  mov fs:[0], esp
0x00587435  push ebx
0x00587436  push esi
0x00587437  mov esi, ecx               ; esi = this (the placeable)
0x00587439  mov eax, [esi+0x338]       ; "open" field
0x0058743F  test eax, eax
0x00587442  jne 0x587544               ; already open -> do nothing, epilogue
0x00587448  mov eax, [esi+0x324]
0x0058744E  test eax, eax
0x00587450  je 0x587544                ; "allowed to open" gate unset -> do nothing
        ; ... resolves a subsystem pointer; if null, plays the CLOSE pose (0x275c) and bails
        ;     without setting +0x338 (a degenerate "opened but nobody is watching" path) ...
0x0058748D  mov eax, [0x7a39fc]
0x00587492  mov ebx, [esp+0x1c]        ; arg1 (the requesting player) — [esp+4] at entry
0x00587499  push ebx
0x0058749A  call 0x4ae910              ; resolve player id/handle -> creature ptr
0x0058749F  test eax, eax
0x005874A1  je 0x5874cf                ; unresolved -> skip the item-count/message build
        ; ... builds a "message context" from (player, this placeable's OWN id, hasItems) ...
0x005874C0  push edx                   ; hasItems bool
0x005874C1  push edi                   ; THIS PLACEABLE'S OWN OBJECT ID ([esi+4])
0x005874C2  push eax                   ; the resolved player
0x005874C3  call 0x4aedb0
0x005874C8  mov ecx, eax
0x005874CA  call 0x569f40              ; CSWSMessage::SendServerToPlayerGuiContainerObject_Open
        ; ... item-repository bookkeeping, an effect (0x34), and a second message dispatch ...
0x00587526  mov eax, [esp+0x20]        ; arg2 at entry — the "animate" flag
0x0058752A  test eax, eax
0x0058752C  je 0x58753a                ; animate == 0 -> skip the open pose
0x0058752E  mov edx, [esi]
0x00587530  push 0x275b                ; OPEN pose
0x00587535  mov ecx, esi
0x00587537  call [edx+0x7c]
0x0058753A  mov dword ptr [esi+0x338], 1   ; unconditional: marks open regardless of arg2
0x00587544  ; epilogue: mov ecx,[esp+0xc]; pop edi; pop esi; mov fs:[0],ecx; pop ebx;
            ; add esp,0xc; ret 8
```

`ret 8` — two stack args beyond `this` (thiscall): arg1 (the requesting player id/handle, at
`[esp+4]` on entry) and arg2 (an "animate the open pose" bool, at `[esp+8]` on entry). Neither
of the three call sites this pass inspected reads `OpenInventory`'s return value afterward, and
the function itself never deliberately sets one on any exit — `eax` is unspecified, by the
engine's own indifference.

### 1.2 `CSWSPlaceable::CloseInventory` 0x00587560 (VERIFIED, full disasm) — and a correction

```
0x00587579  mov eax, [esi+0x338]
0x0058757F  test eax, eax
0x00587582  je 0x5876ee                ; already closed -> nothing to do
        ; ... resolves the requesting player, unlinks the item(s) the player took (if any),
        ;     gives them to the player's inventory, sends a "container updated" message ...
0x00587686  mov edx, [esi]
0x00587688  push 0x275c                ; CLOSE pose
0x0058768D  mov ecx, esi
0x0058768F  call [edx+0x7c]
0x00587692  mov eax, [esi+0x324]
0x00587698  test eax, eax
0x0058769A  mov dword ptr [esi+0x338], 0    ; UNCONDITIONAL — runs regardless of the test above
0x005876A4  je 0x5876ee
        ; ... "everything taken and repository now empty" bookkeeping (+0x328) ...
```

**`+0x338` is cleared unconditionally, right after the close animation plays.**
`docs/research/placeables-containers.md` reads this field as *"0 = closed/unsearched, nonzero
= opened"*, phrased as a permanent post-loot marker. That reading is **corrected** by this
disassembly: the field is a **transient "is the GUI showing right now" flag**, toggled 1 on a
real open and back to 0 on a real close, not a durable "ever searched" record. It still
appears in `SavePlaceable`'s field list because every field in the struct is serialised
verbatim; it is simply always 0 at real save time (KOTOR does not allow saving with a
container panel open), so the two readings never actually conflicted in practice — they only
looked like they did.

This correction is what makes `+0x338` usable as this pass's **local open/close detector** at
all (§2.2) — a permanent flag could not have told a currently-open container from a
long-since-looted one.

`PlaceableController`'s existing one-way mirror (a host's "ever opened" cosmetic pose,
written via `Odyssey.SetPlaceableOpen`, never cleared) is **unaffected** by this correction and
keeps its own separate job: a late joiner sees an already-looted chest with its lid open. That
mirror never calls the real `CloseInventory`, so nothing ever claws its write back. This pass's
NEW transient tracking (§2) is a second, independent concern layered on top, matching the
build brief: *"keep `PlaceableController`'s open-state flag mirror as the durable state."*

### 1.3 The animation call — upgraded from INFERRED to VERIFIED

`placeables-containers.md` flagged the placeable's animation call as *"INFERRED /
NEEDS-LIVE — a guessed-ABI vtable call."* It no longer needs to be guessed: reading the
`CSWSPlaceable` vtable's own `+0x7c` slot directly out of the sha256-matched exe on disk (no
ASLR, so the file's static content IS the runtime content) resolves to a **fixed address**,
`0x004CCF60`, which the symbol database itself names `CSWSObject::SetAnimation`.
Disassembling it:

```
0x004CCF60  or dword ptr [ecx+0x1fc], 1    ; a "dirty/pending" bit
0x004CCF67  mov eax, dword ptr [esp+4]     ; the one stack arg: the requested anim id
0x004CCF6B  mov dword ptr [ecx+0xd4], eax  ; stored for the engine's own update to pick up
0x004CCF71  ret 4
```

`__thiscall void SetAnimation(int animId)` — two field writes, no branch, no allocation, no
re-entrancy risk. Both `OpenInventory` (open pose) and `CloseInventory` (close pose) call it
through the identical `mov edx,[esi]; push <id>; mov ecx,esi; call [edx+0x7c]` sequence, which
is also how this pass's own mirror calls it — directly, at the fixed address, the same choice
this codebase already makes for every other verified fixed-address call (e.g.
`Odyssey.ClearAllActionsFn`).

| Animation id | Meaning | Verified by |
| --- | --- | --- |
| `0x275B` | OPEN pose | `OpenInventory`'s success path, gated on its own "animate" arg, immediately before `+0x338 = 1` |
| `0x275C` | CLOSE pose | `CloseInventory`, unconditional, immediately before `+0x338 = 0`; also `OpenInventory`'s degenerate no-subsystem branch |

### 1.4 Explored, not used: the client-side GUI functions

`CGuiInGame::ShowContainerGui` (0x0062D4B0) / `HideContainerGui` (0x0062D510) and
`CSWGuiContainer::SetContainer` (0x006B8130) were disassembled this pass while looking for the
"currently open" signal, before the `+0x338` correction above made them unnecessary.
`ShowContainerGui` forwards its two stack args into `SetContainer`, which resolves one of them
through `CServerExoApp::GetPlaceableByGameObjectID` (0x004AE7A0) back to a `CSWSPlaceable*` —
i.e. the container object id is recoverable from this path too, but it requires unwinding an
extra layer of indirection that `+0x338` (§2.2) makes redundant. **INFERRED shape only, not
exercised further, kept here as a note for whoever next needs a client-side GUI hook for
something `OpenInventory`/`CloseInventory` cannot answer.**

---

## 2. Design

### 2.1 The lock: `InteractionKind.Container` on the existing channel

The per-object interaction lock (`InteractionClaim`/`InteractionLock`, packets 35/36,
`docs/research/shared-state-audit.md` §4) already does exactly the job needed here — a lease,
claimed the instant a player starts interacting, broadcast as a level, expiring on its own —
for `InteractionKind.Conversation`. This pass adds `InteractionKind.Container = 2` to the same
channel rather than inventing a parallel one.

**The one real difference: what the claim is keyed on.** A conversation claim carries a TAG,
because NPC tags are authored unique. A container claim cannot: the base game routinely places
several placeables with the **same** tag (every generic footlocker in a module, typically), so
two unrelated chests would fight over one lock — or worse, a lock on chest A would silently
"protect" chest B. So `InteractionClaimPacket`/`InteractionLock` both gain a `TargetId` field
(the placeable's object id), and `InteractionKind.Container` keys on THAT instead of the tag,
which stays empty for a container claim. `InteractionLockStore`'s key becomes
kind-discriminated (`"tag:..."` vs `"id:XXXXXXXX"`) so the two keyspaces can never collide even
at the same numeric value.

The lease/claim decision itself — first-fresh-claim-wins, re-entrancy (the opener may re-open
their own held container), release-only-by-holder, and "no fresh hold = free" (which is what
an expired lease looks like from the caller's side) — is `KotorCoop.Protocol.ContainerUseRule`,
a pure function `InteractionLockStore.Claim` now defers to. It is the SAME rule for both kinds
(the class is named for the container use case because that is what motivated pulling it out,
but conversations ride it too, unchanged).

### 2.2 Local detection: `ContainerUseHook` on `OpenInventory`'s entry

A jmp-splice detour on `CSWSPlaceable::OpenInventory`'s VERIFIED prologue (25 bytes, §1.1),
following the exact idiom `DialogueGateHook`/`MoviePlayHook` already use: pushad, call a
managed callback with `this` (the placeable), test the result, popad, and either fall through
into the relocated prologue or take a synthesised refusal path.

**Two jobs at one hook site:**

1. **Refusal (the lock).** The callback checks the placeable's own object id
   (`[this+4]`) against a small array of "containers a PEER currently holds," refreshed once a
   frame by `InteractionLockController` from the block's interaction-lock list (exactly like
   `DialogueGateHook.PeerPartnerTag`). A match bumps a blocked counter (for the on-screen
   notice) and returns 1; the stub then does a bare `ret 8` **instead of running the relocated
   prologue at all**.

   ⚠ **The refusal needs nothing invented, unlike `DialogueGateHook`'s synthesised
   `xor eax,eax; ret 0xC`.** This stub is entered by a `jmp` splice at the function's TRUE
   entry point — the stack is exactly as the caller left it (`[esp+0]` return address,
   `[esp+4]`/`[esp+8]` the two args `ret 8` pops). A `ret 8` there, after the stub's own
   `pushad`/`popad` balance back out, is bit-for-bit what the function's own "nothing to do"
   early exits (`+0x338` already set, `+0x324` unset) already do to the stack. Nothing is
   pushed, no SEH frame is touched, and `OpenInventory`'s return value is read by none of its
   three callers (§1.1) — leaving `eax` unspecified on the refusal path is exactly as safe as
   the engine's own indifference to it already implies.

2. **Detection (an allowed attempt).** Every call that is NOT refused records the placeable id
   and bumps a serial — an "allowed attempt," not yet a confirmed open: `OpenInventory` has its
   own internal no-op branches (already-open, `+0x324` unset) this hook does not
   re-implement. `InteractionLockController.PollLocalContainerUse` correlates the latest
   attempt against `Odyssey.IsPlaceableOpen` (the corrected §1.2 field) actually flipping true
   within a short frame window; only then does it publish the local Container claim. The same
   poll watches for the field flipping back to false (the player closed their own panel) and
   releases the claim.

A claim with no holder (`TargetId` present, `HolderPeerId` 0 — i.e. nothing broadcast for that
id) is never refused: the engine's own scripted opens (an NPC's own inventory access, a
script-driven `OpenInventory`) are never blocked, mirroring `DialogueGateHook`'s identical rule
for an unclaimed NPC.

### 2.3 The mirror: open/close animation, exactly once per edge

`InteractionLockController.ReconcileContainerAnimations` diffs "containers a PEER currently
holds" (this pass's Container-kind locks) against "containers this instance already believes
it opened on a peer's behalf." The diff itself is `KotorCoop.Protocol.ContainerAnimationEdges`,
a pure function tested directly (the plugin assembly is x86 NativeAOT and nothing in the test
project references it — the same reason `DialogueAutoJoinRule` lives outside it too). A newly
held id gets `OdysseyPlaceables.PlayContainerOpenAnimation` (§1.3's `0x275B` call) plus
`Odyssey.SetPlaceableOpen` (the existing durable-mirror setter, §1.2); an id that drops out of
the held set — lease expired, or the peer released it, no distinction needed — gets
`PlayContainerCloseAnimation` (`0x275C`) plus a new `OdysseyPlaceables.ClearPlaceableOpenFlag`
(`+0x338 = 0`), mirroring what the real `CloseInventory` does to the same field.

`OpenInventory`/`CloseInventory` themselves are never called for a mirror: both also run
GUI/message-context logic meant for a real local player (§1.1/§1.2's item-repository and
message-dispatch code), which would be wrong to invoke on a body nobody local is actually
using. Only the field write and the animation call are replicated — the same "field write is
the safe minimum, the animation is separable" split `placeables-containers.md` already
recommended for the durable mirror, now applied to the transient one.

### 2.4 On-screen refusal notice

`InteractionLockController` polls `ContainerUseHook.BlockedCount`; on a new refusal it
resolves the holder's peer id to a name (scanning the block's remote-peer slots, the same data
the minimap/portrait mirrors already read) and draws `"<name> is looting that - wait a
moment"` at screen row 7 for three seconds — the identical `Odyssey.PostTextLine` mechanism
`DialogueSpectateController`'s "click to join" notice (row 4) and this controller's own
"walk over to join" notice (row 6) already use, on its own row so none of the three collide.
Falls back to `"Another player is looting that - wait a moment"` if the holder's slot cannot
be resolved (e.g. they disconnected between the refusal and the notice being drawn).

### 2.5 Wire/block changes

- **Protocol version 37.** `InteractionKind.Container = 2`. `InteractionClaimPacket` and
  `InteractionLock` both gain `uint TargetId = 0`.
- **`SharedBlock` version 50**, region `0x3F08..0x3F1C` (16+4 bytes) in the free tail after the
  creature-liveness region (`0x3C00..0x3F08`, `TotalSize` stays `0x4000`): a plugin→agent
  `OffsetLocalInteractionTargetId` and an agent→plugin `OffsetInteractionLockTargetIds[4]`
  parallel array, matched 1:1 by index against the existing tag-keyed lock slots at
  `OffsetInteractionLocks` rather than widening `InteractionLockStride` (every byte of its
  existing 0x28 is already spoken for).
- **`CheatKindUsePlaceable = 18`** / **`CheatKinds.UsePlaceable = 18`** (kept in sync,
  `CheatKindSyncTests` enforces it) and a new **`useplaceable [tag|id]`** console verb, the
  container analogue of `opendoor`: it calls `Odyssey.UseObject` (the engine's own
  client-to-server "I clicked that" input), so the real `AIActionUseObject`/`EventHandler`/
  `OpenInventory` path runs exactly as a real click would. Unlike `opendoor`/`usedoor` there is
  no walk to queue — a container needs no walkmesh coupling — so this is a single
  fire-and-forget send. A bare `useplaceable` (no tag or id) also logs the FULL placeable sweep
  (id, container-or-not, distance, locked/open/trapped) before picking the nearest container,
  so the same verb both discovers ids and uses one.

---

## 3. Live-test script (NOT yet run — static RE + implementation + tests only)

`end_m01aa` (the Endar Spire's first module) has footlockers near the start. Two instances,
host and client, both loaded into the same module.

1. **Discover ids.** On either instance's console: `useplaceable` (bare). The plugin log
   (`kotorcoop-plugin.log`) prints every placeable in the module — `placeables: N in this
   module`, then one line per placeable: `0x<id> [container] at <dist>m locked=... open=...
   trapped=...`. Note a nearby footlocker's id, e.g. `0x50`.

2. **Host opens it.** Host console: `useplaceable 0x50` (or `useplaceable` bare if it is the
   nearest container). Expect: the host's own loot panel opens (real engine GUI, unaffected by
   this pass), and the plugin log shows `container: 0x50 use sent`.
   **Check 1 — the other side sees it open:** on the CLIENT, `0x50`'s lid animation should play
   and `interaction: ... object(s) held by a peer` should log; the client's OWN copy of the
   footlocker (not the host's) should visually be open.

3. **Client tries the same container while the host's panel is open.** Client console:
   `useplaceable 0x50`. Expect: **refused** — the client's own loot panel never opens, the
   plugin log records a container-use refusal, and the on-screen line `"<host's name> is
   looting that - wait a moment"` appears for ~3 seconds on the client's screen.

4. **Host closes.** Host: close the loot panel through the normal in-game UI (Escape/close
   button — no console verb needed, this is the ordinary player action `CloseInventory`
   handles). Expect: the CLIENT's mirrored copy plays the close animation and its `+0x338`
   clears (log line for the release), and the client can NOW successfully `useplaceable 0x50`
   — no refusal this time.

5. **Reverse roles.** Repeat steps 2-4 with the client opening first and the host attempting
   the blocked open, to confirm the gate and the mirror are symmetric (no host/client branch
   anywhere in `ContainerUseHook`, `InteractionLockController`, or `InteractionLockStore`).

6. **A corpse after a kill.** Kill an enemy near the start (`slay` or a real fight), then
   `useplaceable` (bare) to confirm the corpse's body-bag placeable appears in the sweep
   (`is_body_bag`/`is_corpse`, `docs/research/placeables-containers.md` §1), and repeat steps
   2-4 against it — corpses share the same `CSWSPlaceable`/`OpenInventory` path as a chest, so
   nothing here is chest-specific, but a live pass should confirm it rather than assume it.

Watch for in the plugin log: `container-use gate at 0x587420 (stub 0x...)` at startup
(confirms the detour installed — a byte mismatch here means `container_locks` silently did
nothing and every check above needs re-reading as "gate absent"), and
`interaction: N object(s) held by a peer` alongside the existing conversation-lock line.

---

## 4. VERIFIED / INFERRED summary

| Item | Status |
| --- | --- |
| `OpenInventory` 0x00587420, full control flow, args, callers | VERIFIED (fresh disasm + xrefs, this pass) |
| `CloseInventory` 0x00587560, full control flow, the `+0x338` clear | VERIFIED (fresh disasm, this pass) |
| `+0x338` is a TRANSIENT "GUI open now" flag, not permanent | VERIFIED — corrects `placeables-containers.md` |
| Animation ids `0x275B` (open) / `0x275C` (close) | VERIFIED (both call sites disassembled) |
| The animation call is `CSWSObject::SetAnimation` 0x004CCF60, `__thiscall void(int)` | VERIFIED (vtable slot read + disasm) — upgraded from `placeables-containers.md`'s INFERRED |
| `AIActionUseObject` is at `0x0057E8C0`, not `0x0057ECC0` | VERIFIED (symbol db + xref-offset arithmetic); corrects a stale address in the task briefing / `CheatController.cs`'s existing comment (not edited — out of scope) |
| The `OpenInventory` entry-point refusal needs no synthesised failure value | VERIFIED by stack-effect analysis (jmp-splice entry, balanced pushad/popad, unread return value) |
| `CGuiInGame::ShowContainerGui`/`HideContainerGui`/`CSWGuiContainer::SetContainer` shape | INFERRED (disassembled, not exercised — superseded by the `+0x338` approach) |
| The gate, the mirror, the lock store, the pure rules, the console verb | Implemented, unit-tested |
| Any of this actually working against the live game | **NOT VERIFIED — no live pass has run.** See §3.
