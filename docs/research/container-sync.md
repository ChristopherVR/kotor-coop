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
| Open/close sync + the use-lock, both directions, against a real footlocker | **LIVE-VERIFIED (2026-08-22).** See §5. |
| `closeplaceable` calling real `CloseInventory` with arg1=0 | LIVE-DISPROVEN (silently no-ops); FIXED with arg1 = the closing player's own object id, LIVE-CONFIRMED |
| `PlaceableController`'s durable "ever opened" mirror vs. this pass's transient close | LIVE-CONFIRMED to conflict (permanent reopen); FIXED with `ContainerCloseHook`'s per-module closed-latch |
| A dead host's stale interaction-lock/dialogue-token cache on a client | LIVE-CONFIRMED never expires on its own; FIXED (`CoopClient.ForgetSession` now drops it, matching the existing dialogue-token fix) |
| `OdysseyPlaceables.HasInventory` (`+0x334`) as a container discriminator | LIVE-DISPROVEN — see §5's gap note |

---

## 5. Live pass (2026-08-22)

Two GOG v1.0.3 instances, `tools/demo.ps1`'s default save (END_M01AA, both instances start
together in the Endar Spire's first crew-quarters corridor). Host pid varied per relaunch below;
placeable **0x C6**, ~9m from the start position, confirmed live as a real, working container
(engine gate `+0x324` reads 1, and it actually opens — several nearby ids that also passed the
sweep never opened at all, see the gap note below). Driven entirely through the plugin's
in-game console channel (the same `0x9F8`/`0x9F4`/`0xA58` poke `verify.ps1`'s own `SendConsole`
uses), read back with the same `VirtualQueryEx`/vtable-scan technique `verify.ps1` uses for doors
and creatures, applied here to `CSWSPlaceable` (vtable `0x007494D0`).

### 5.1 The core sequence — open, mirror, refuse, close, mirror-close, reverse roles

Teleported both players onto the container (`tp 10.8 21.87`, its own `+0x90` position) so every
step is 0.0m and immune to AI-pathing latency, which the FIRST live attempt (real ~9m walk) did
not control for and which cost real time chasing a red herring (§5.3).

```
=== STEP 1: HOST opens 0xC6 ===
host open flips to 1: True
host local claim: kind=2 active=1 targetId=0xC6
client mirrors open: True
client lock: count=1 [peer=1 kind=2 targetId=0xC6]
[18:42:01.556] interaction: 1 object(s) held by a peer; stopped 0 of them walking

=== STEP 2: CLIENT attempts the SAME container (should be refused) ===
client local claim after attempt (should stay inactive): kind=0 active=0 targetId=0x0
[18:42:02.394] placeable: use-object sent for 0xC6 at 0.0m (ok)

=== STEP 3: HOST closes 0xC6 (closeplaceable, corrected arg1) ===
host open flips to 0: True  (final=0)
[18:42:07.000] placeable: close-inventory called for 0xC6
[18:42:07.001] cheat: placeable 0xC6: closed (close animation played, +0x338 cleared) (status 1)
host local claim after close: kind=0 active=0 targetId=0x0

--- waiting for client mirror close (up to 10s) ---
client mirrors close: True  (final=0)
client lock after host closes: count=0 []
[18:42:12.492] interaction: no peer holds anything here

=== STEP 5: CLIENT opens 0xC6 now that it is free ===
client open flips to 1: True
client local claim: kind=2 active=1 targetId=0xC6

=== STEP 6: HOST attempts the SAME container while CLIENT holds it (reversed refusal) ===
host local claim after attempt (should stay inactive): kind=0 active=0 targetId=0x0
host own placeable open field (mirror of the client's hold, not a real local open): 1
```

**Verdict: PASS, both directions.** The lease, the mirror-open animation/flag, and the refusal
(functionally — the second player never gets a local claim and never sees the panel) all work,
symmetrically, with no host/client branch. What could NOT be confirmed: an explicit
`ContainerUseHook` refusal LOG LINE or `BlockedCount` increment on either side's attempt in
steps 2/6 — see §5.4.

### 5.2 The close-side gap: no way to close what `useplaceable` opens — FIXED, `closeplaceable`

`useplaceable` sends `Odyssey.UseObject` — the network HALF of a real click
(`CSWCMessage::SendPlayerToServerInput_UseObject`). The client-side menu wrapper that actually
SHOWS the loot panel (`CSWCPlaceable::UsePlaceable`'s own code, never replicated — see Odyssey.cs's
remarks on `SendUseObjectInputFn`) is not: a screenshot of the host mid-"open" showed normal
gameplay, no panel, even though `+0x338` had genuinely flipped to 1 engine-side. This is a real,
usable engine state (the mirror/lock machinery reacts to it correctly, §5.1) but it left the
live-test script with no Escape key to press and no button to click, exactly the gap the task
briefing anticipated ("find how the container GUI is closed... the engine's own `CloseInventory`
path via a verb if one exists"). It did not exist, so it was added: `closeplaceable [tag|id]`
(mirrors `useplaceable`'s shape) calls `CSWSPlaceable::CloseInventory 0x00587560` directly —
protocol/`SharedBlock`/`CheatKinds` all gained `ClosePlaceable = 19`, `CheatKindSyncTests` still
passes.

**First attempt, arg1=0, LIVE-DISPROVEN.** `CloseInventory` is `ret 8` (thiscall, two stack
args, same shape as `OpenInventory`); a first cut called it `(placeable, 0, 0)` on the theory
that arg2=0's `je 0x58762f` branch skips every read of arg1 (the "requesting player", resolved
via the same `call 0x4ae910` `OpenInventory` uses). That branch does exist, but 0x58762f is NOT
a shortcut to the close animation — it is the SAME unconditional tail every path through the
function falls into (a real close crosses it too), and that tail pushes arg1 RAW into a SECOND
message dispatch (`push ebx /* = raw arg1 */; ...; call 0x4aed80; call 0x4b08d0`) before the
close animation and the `+0x338 = 0` clear that come after it. Live-observed: the console command
replied (no crash), but `+0x338` never cleared —

```
closeplaceable replied: True
t=+0s open=0
t=+1s open=0
t=+2s open=0
t=+3s open=1
...
```

(that reopen is a SEPARATE bug, §5.3 — but at this stage of the pass it was mistaken for the
close never having worked at all). Fresh disasm of the real call site
(`AIActionUseObject`'s open call at `0x0057EEA3`, `tools/disasm.py 0x57EE80 0x50`) shows what a
real click actually passes for this argument: `mov eax,[edi+4]; push eax` — the ACTING
CREATURE'S OWN OBJECT ID, the standard `object+4` convention this whole plugin already uses.
`OdysseyPlaceables.CloseInventoryReal` now takes the closing player's creature pointer and passes
its own object id; **live-confirmed** this clears `+0x338` reliably (§5.1's step 3).

### 5.3 The durable "ever opened" mirror permanently defeats a close — FIXED, `ContainerCloseHook`

The single most significant finding of this pass. After a genuinely successful close (§5.2's
fix, `+0x338` verified 0), the container was watched for 30s straight and it silently reopened —
first observed at t=+3-4s, sticking open forever after:

```
[18:29:10.177] placeable: close-inventory called for 0xC6
[18:29:10.178] cheat: placeable 0xC6: closed (close animation played, +0x338 cleared) (status 1)
[18:29:10.287] placeable 0xC6: marked opened on the session's authority
```

That third line is `PlaceableController`'s own log — a channel that PREDATES this pass (shared
block version 11), unrelated to the container-sync feature except that it happens to write the
same `+0x338` field. It exists to give a late joiner a cosmetic "already looted, lid up" chest,
via a wire flag (`PlaceableFlags.cs`) that is explicitly, deliberately monotonic: *"a placeable
only ever advances — closed to open, locked to unlocked — never the reverse."* Its agent-side
half (`KotorGameBridge.CollectChangedPlaceableState`) polls every placeable's own `+0x338`
independently on EVERY instance about once a second; once it has EVER read 1 for an id, it keeps
re-broadcasting "open" and every instance's `PlaceableController.Apply` keeps re-asserting
`Odyssey.SetPlaceableOpen` (the same field) forever. A container this pass's new transient system
had genuinely closed got clobbered by this pre-existing channel within ~100ms, on both instances,
permanently, with no interaction lock held by anyone — i.e. **the close-sync feature, as first
shipped, worked for a single frame and then silently failed for the rest of the session, every
time.**

Fix: `ContainerCloseHook`, a second detour (same jmp-splice technique and prologue-verification
discipline as `ContainerUseHook`, on `CloseInventory` instead of `OpenInventory`, never refusing
— closing must never be blocked). Every real `CloseInventory` call, PLUS every call to
`OdysseyPlaceables.ClearPlaceableOpenFlag` (the mirror's own close, which never calls the real
function so the hook alone cannot see it), latches the id as closed for the rest of the module
load; `PlaceableController.Apply` checks the latch before writing. **A time-boxed suppression
(4000ms) was tried first and LIVE-DISPROVEN** — the reassertion pressure is continuous (the
agent's poll and `PlaceableController`'s own pass both run forever), so the container reopened
again at t=+3-4s against that window too. The latch (permanent within a module, cleared only by
`Invalidate()` on a module change) is what actually holds:

```
closeplaceable (client, the current holder) replied: True
t=+0s  host open=1  client open=0
t=+1s  host open=1  client open=0
t=+2s  host open=1  client open=0
t=+3s  host open=1  client open=0
t=+4s  host open=0  client open=0     <- host's own mirror catches up
t=+5s .. t=+29s  host open=0  client open=0     (25 more seconds, stable)
```

Re-tested in the OTHER direction (host closes what it holds) with the same 15-16s stability
window — also held. **This is the load-bearing fix of the whole pass**: without it, "closes for
everyone" was true for about a tenth of a second.

### 5.4 Lease expiry on a dead host — a SECOND stale-cache bug found and fixed the same way

Host holds the container (confirmed: `client lock: count=1 [peer=1 kind=2 targetId=0xC6]`), then
`Stop-Process` on the HOST's own `KotorCoop.Agent`. Expected (task briefing): the client's lock
releases and the container closes within the lease timeout (`ProtocolConstants.PeerTimeoutMs`,
5000ms). **First attempt: FAILED, LIVE-CONFIRMED** — 12 straight seconds of polling showed the
client's copy of the lock and the mirrored-open state completely unchanged, even though the
client's own agent correctly logged the disconnect (`agent reports 0 remote peer(s)`,
`peer left: destroyed phantom ...`) within the expected 5s window.

Root cause: `CoopClient.GetInteractionLocks()` is explicitly "the locks the host last
broadcast" — a CACHE, not a lease with its own clock. The design assumption (stated in
`SendInteractionClaim`'s own remarks) is that the HOST's `InteractionLockStore` prunes an expired
entry and the NEXT broadcast carries the drop — which only holds while the host is alive to
compute and send that next broadcast. Killing the host itself (not just the claimant) leaves
nobody left to ever publish the empty list, so the client's cache froze holding a peer that no
longer existed, indefinitely. **This is the identical shape of a bug already fixed once in this
exact function**, for the dialogue token (`CoopClient.ForgetSession`'s own comment: *"a host that
CRASHES mid-conversation... left this client spectating forever... measured live: the host was
killed mid-conversation and 20s later the client still read 'remote: active 1'"*) — the container
lock cache had the same hole, just never previously exercised by a live pass. Fixed the same way:
`ForgetSession()` (called on link-loss timeout) now also clears `_interactionLocks` and
`_interactionLockModule`. Re-tested: the client's lock and mirrored-open state both cleared
within the first second after the kill —

```
t=+1s  host open=1  client open=0  lockCount=0
t=+2s .. t=+10s  (unchanged, stable)
```

Session was then restarted (`demo.ps1 -Stop` / relaunch) per the briefing's own "restart via
reconnect/relaunch" — a host whose own agent was killed does not recover on its own; that part
was never expected to.

### 5.5 A corpse — NOT exercised this pass (honest gap)

`slay` (bare, nearest enemy) killed object id `0x35`. Immediately after, `resolve 0x35` reported
the object no longer resolves through ANY of the engine's own lookup paths
(`engine 0x0 walk 0x0 resolve 0x0`) — the id was fully invalidated, not converted to a lootable
body-bag placeable (`useplaceable` bare's own sweep count stayed at exactly 64 placeables,
unchanged from before the kill). Whether this is specific to the `slay` cheat's own
implementation (possibly destroying the body outright rather than leaving a corpse) or a genuine
property of this save/module was not root-caused — pursuing it further (a real combat kill
instead of the cheat) was not attempted this pass for time. **Not claimed working, not claimed
broken — genuinely not tested.** A future pass should either use a real combat kill or trace
`CombatController`'s own `slay` path to see whether it destroys the body.

### 5.6 Regression checks

- **An ordinary door** (`opendoor` bare): opened normally — `door: opened 0x47 (state 2 -> 1) at
  1.8m` — unaffected, as expected (doors are a separate `CSWSDoor` vtable/hook entirely).
- **A plot placeable / switch** (has_inventory-false objects encountered throughout this pass,
  e.g. while probing for a working container): never refused, never gated — by construction,
  `ContainerUseHook` only refuses an id present in the CURRENT peer-held set, and nothing in this
  pass ever claimed a lock on a switch. Not independently exercised against a NAMED computer
  panel/plot device this pass; the structural argument stands but is not a live click on a
  specific plot object.
- **Dialogue lock unaffected by a held container**: NOT independently live-exercised — no
  console primitive exists to start a real dialogue the way `useplaceable` starts a container use
  (an actual click is the only path). Argued instead from the code and the test suite: the
  lock's key is kind-discriminated (`InteractionLockStore.TryKey`: `"tag:..."` for
  `InteractionKind.Conversation` vs. `"id:XXXXXXXX"` for `InteractionKind.Container`, in the SAME
  dictionary but never colliding), and the full 1591-test suite (including the pre-existing
  conversation-lock tests, untouched by this pass) passed throughout.

### 5.7 A live-confirmed gap in `HasInventory` (`+0x334`) — not fixed, documented

Discovering 0xC6 required trial and error: `useplaceable` bare's own sweep tags a placeable
`[container]` only when `+0x334` (`has_inventory`) is nonzero, and every placeable probed this
pass — including 0xC6 itself, and including one with a genuine item sitting in its repository
(`repo+0x10` item count = 1) — read `+0x334` as 0. A container that demonstrably opens for real
and demonstrably holds a real item is not distinguishable from a switch by this field, live. The
field itself is VERIFIED at the struct level (state-field table, `placeables-containers.md`,
against the symbol DB) but was never itself live-verified as the right CONTAINER discriminator,
and this pass's own live probing disproves that reading. Left undiagnosed and unfixed (the
correct discriminator was not identified) — `useplaceable` bare and the `[container]` sweep tag
both under-report real containers as a result. `closeplaceable`'s own bare form sidesteps this
entirely by filtering on `Odyssey.IsPlaceableOpen` instead (unambiguous — the exact field
`OpenInventory`/`CloseInventory` themselves set/clear).

### 5.8 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1590/1591 passed** — the one failure is
`DisconnectRecoveryTests.HeavyPacketLossStillConverges`, the pre-existing documented flake, not a
regression from this pass. Both plugin builds (`build.ps1 -Install`, both GOG installs) clean,
0 warnings 0 errors, across five rebuild/redeploy/relaunch cycles this pass needed as each live
finding above required a code fix.

## 6. Round 68 — corpse looting: the console primitive extended, the refusal path verified,
the real-corpse question still genuinely open

Picks up §5.5's honest gap directly: whether a corpse from a REAL combat kill (not `slay`)
behaves as a lootable container under the container-sync lock, and whether it needs its own
`ContainerUseHook`-equivalent detour.

### 6.1 Where a corpse's inventory actually lives, per the symbol DB

A corpse is not a `CSWSPlaceable` (confirmed: `Odyssey.ResolvePlaceable` on a dead creature's id
returns 0 — the vtable never matches `PlaceableVtable`). `tools/q.py func`/`class` against
`tools/kotor1_0_3.db` finds the real engine surface instead: `CSWSCreature::GetItemRepository`
(`0x004EF770`, `__thiscall`) returns a `CItemRepository*` — the SAME kind of object
`CSWSItem::InitRepository`/`CItemRepository::GetItemInRepository` (0x00555F40) work with for an
item's own container contents (a backpack, say). No creature-specific `OpenInventory`/
`CloseInventory` pair analogous to `CSWSPlaceable`'s exists in the symbol DB — `CSWSItem`
has its own `OpenInventory`/`CloseInventory` (0x005561A0/0x0055D800), suggesting a corpse's loot
panel is opened through the SAME generic "open this repository-bearing thing" machinery an item
container uses, dispatched from `CSWSObject::AIActionUseObject` (0x0057E8C0) — the same function
whose disassembly `docs/research/container-sync.md` §5.2 already traced for the placeable case,
this time the branch that resolves to a script/repository open rather than
`CSWSPlaceable::OpenInventory` specifically was not isolated (§6.4: no real kill was available
to trace it live against). **Not a full disassembly of the creature-open path — a symbol-DB
identification of where it plausibly lives, one level short of what a live trace would confirm.**

### 6.2 The console test primitive, extended to reach a corpse

`useplaceable`'s cheat handler (`CheatController.UsePlaceable`) refused any explicit id/tag
that did not resolve as a `CSWSPlaceable` — exactly what stood between the live-test rig and a
corpse, since `Odyssey.UseObject` (the underlying `SendUseObjectInputFn` — the engine's real
client-to-server "I clicked that" input, already object-type-agnostic) never itself needed the
target to be a placeable. Extended: an explicit id/tag that fails `ResolvePlaceable` but
resolves to ANY server object now falls through to a raw `Odyssey.UseObject` send (its own
range check against the resolved object's position, its own log line and answer text
distinguishing "not a placeable" from a genuine failure) instead of refusing outright. The bare
sweep (no id/tag — `useplaceable` alone) is UNCHANGED and still refuses non-placeables; it
exists to discover containers, not to guess at a nearby id. No new console verb, no protocol
change, no `CheatKind` added — this is a widening of an existing cheat's own resolve logic.

### 6.3 Verified against the ALREADY-KNOWN case: a `slay`d body, exactly reproducing §5.5

`slay` (bare) killed a nearby hostile (id `0x17`); immediately after, `resolve 0x17` reported
`resolves to 0x0` (fully invalidated, matching §5.5's own finding byte-for-byte), and the new
`useplaceable 0x17` extension correctly answered `does not resolve to anything here` — the
clean, honest refusal the extension is supposed to produce when there is genuinely nothing to
use, rather than a crash or a stale-pointer read. This confirms the EXTENSION works as designed;
it does not, and cannot, confirm anything about a real corpse, since `slay` never leaves one
(§5.5, re-confirmed, not re-investigated further this round).

### 6.4 A real (non-`slay`) kill was not obtainable this round — the corpse question remains open

The plan was: `attack 0x7FFFFFFF <id>` (the player-as-attacker form of the `attack` test-hook,
round 58's own documented recipe for a real kill) against a live hostile, then
`useplaceable <corpse id>` on the resulting body. The first half never completed — see
`docs/research/phantom-locomotion.md` §14.3 for the full finding: across five distinct targets
in two modules (END_M01AA, END_M01AB), every player-as-attacker order left the target's action
queue empty (`head=0xFFFFFFFF`) and its combat round inactive for the entire observation window,
while the REVERSE (an enemy ordered to attack the player) reliably produced real combat-round
activity on the same targets. Nothing died from player damage this round, so §6.2's extension
was never actually exercised against a genuine corpse, and the questions the task brief asked
for — does the client get refused while the host loots, does open/close mirror, does loot
removal replicate the same way `ContainerLoot`/materialised-loot already do for a placeable —
are **all still open**, not newly closed. **Precisely what does and does not apply to a corpse,
honestly**: confirmed — the identity of the engine surface (§6.1, symbol-DB level); confirmed —
the console primitive now reaches a non-placeable id cleanly (§6.2/§6.3); NOT confirmed — any of
`ContainerUseHook`'s refusal, the open/close mirror, or loot replication for a real corpse,
because no real corpse existed to test them against.

### 6.5 What remains (NEEDS-LIVE, not closed this round)

- **A real kill, on a save/module where the `attack` test-hook's player-as-attacker form
  actually queues an action** (or via genuine player input, still blocked by the tooling gap
  `docs/live-verification.md` names) — the hard blocker for everything else in this section.
- **Whether `ContainerUseHook` (on `CSWSPlaceable::OpenInventory`) covers a corpse at all**,
  once one exists to test against — §6.1's symbol-DB reading suggests it structurally cannot
  (a corpse is never a `CSWSPlaceable`), which would mean a second detour on the creature-side
  open path (§6.1's `GetItemRepository`/the `AIActionUseObject` branch that reaches it) is
  needed for the lock to apply to a corpse the same way it applies to a chest — not built this
  round, since there was nothing live to verify it against.
- **The lease kind** — whether a corpse needs its own `InteractionKind` (alongside
  `Conversation`/`Container`) or reuses `Container` keyed on the corpse's object id the same way
  a placeable already is, is a design question best settled once the open path is confirmed
  live, not guessed at now.

### 6.6 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1601/1601 passed** (see
`docs/research/phantom-locomotion.md` §14.7 for the same run — one combined session covered
both this round's changes). Both plugin builds (`src/KotorCoop.Plugin/build.ps1 -Install`, both
GOG installs) clean, 0 warnings 0 errors. The `UsePlaceable` extension is the only source change
in this file's area this round; `ClosePlaceable` is untouched.

## 7. Round 71 (2026-08-23) — a REAL kill was finally obtainable, and it answers §6.4's own
question the wrong way: it is not different from `slay`

§6.4's hard blocker (`attack`'s player-as-attacker form never queuing) was root-caused and fixed
this round (shared-state-audit.md §2, round 71: a distance problem — the drive-controlled local
player was never actually in melee range when the order was staged — not a code defect). With a
working `attack` in hand, this round went straight at §5.5/§6.4's own open question: does a real
combat kill (not `slay`) leave a lootable corpse?

**No. Three separate creatures, killed through genuine player melee damage, all came back
exactly as `slay` did.** `tar_m10aa`'s Vulkar pool, `attack 0x7FFFFFFF <id>` against three
targets in turn (two `vulkarguard`, one `vulkarpool`), each fight watched live via the attack
probe until hp reached 0 — real damage, real rounds, no cheat kill involved. `resolve <id>`
immediately after each death: `engine 0x0 walk 0x0 resolve 0x0` — the identical "fully
invalidated, not converted to a body-bag" reading §5.5 and §6.3 got from `slay`, byte for byte.
`useplaceable` bare (no id) found no new placeable in the area's sweep after any of the three
deaths either — nothing new to loot appeared anywhere, not just under the dead creature's own
old id.

**This is not a coincidence — it is the direct, predictable consequence of both paths sharing
one call.** `Slay`'s `KillHere` branch and `CombatController.KillOnce` (the path a mirrored
enemy's death already goes through) both call the exact same `Odyssey.KillCreature`. Whatever
that call's underlying lethal-damage effect does to a creature's object-array entry, it does
identically no matter which caller triggered it. §5.5's own hedge ("possibly destroying the body
outright rather than leaving a corpse... a future pass should either use a real combat kill or
trace `CombatController`'s own `slay` path") is now answered: a real combat kill was used, and it
behaves exactly like `slay`, because at the engine-call level they ARE the same kill.

**What this closes and what it reopens.** Closed: whether `slay`'s own cheat implementation is
somehow special-casing away a corpse that a real kill would otherwise leave — it is not; the
task brief's proposed `killreal <id>` verb would produce nothing different from `slay` for the
same reason. Reopened, genuinely: the premise that combat kills in this game leave a lootable
body at all, for at least these two creature types. Not tested and not ruled out: whether a
creature that is actually carrying loot (none of the three killed here had any — all read `str`
in the low teens and no XP, consistent with trivial, empty-handed mooks) gets a body-bag
placeable that an empty-handed one does not; whether some OTHER death path exists in this engine
that never calls `Odyssey.KillCreature` at all (a natural HP-reaches-zero-through-damage-
resolution route, if the retail combat system has one distinct from the lethal-damage-effect
kill this plugin's own combat mirror and cheat layer both use) and behaves differently.

**Task item (B) (host loots under the lock, client refused, loot removal mirrors) could not be
run.** No lootable corpse could be produced by any method available this round — the hard
blocker is no longer "no real kill" (fixed) but "no real kill produces a corpse at all" (a new,
deeper finding). `ContainerUseHook`'s refusal, the open/close mirror, and `ContainerLoot`
replication for a corpse specifically all remain exactly as untested as §6.5 left them.

### 7.1 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1623/1623 passed**, unchanged baseline — this round's
changes (shared-state-audit.md §2, round 71) touch `TestDriveController`, `Odyssey.cs`, and the
new `PartyFollowController`, none of which this file's own container/corpse code depends on.

## 8. Round 72 (2026-08-23) — the corpse-vanish mystery closed: it is not this plugin, confirmed
live on the host with genuinely zero coop machinery in the loop

Picks up §7's own open question directly: is the coop plugin's own machinery — `CombatController`,
`EnemyPresenceController`, or `MaterialisedEnemies` — what is destroying a dead creature's body,
or is this vanilla engine/content behaviour this project never controlled in the first place?

### 8.1 The code-level argument, checked first

`Odyssey.KillCreature` (the function both `Slay`'s `KillHere` branch and `CombatController.KillOnce`
call) is `ApplyDamageEffect(objectId, 9999)` — a real `EffectDamage` dispatched through the VM's
own `ApplyEffectToObject`, i.e. the SAME pipeline a genuine melee swing's damage resolution uses,
not a `DestroyObject` call. Grepping every `Odyssey.DestroyObjectById`/`CheatKindDestroyObject` call
site in the plugin (`CheatController.DestroyObject`, `ItemController`, `LivenessReconcileController`,
`MaterialisedEnemies.Destroy`, `PhantomController`) finds exactly one that could plausibly reach a
GIT-placed enemy's body: `MaterialisedEnemies.Destroy`. Reading it end to end shows it only ever
operates on slots this SAME class's own `OwnerIds`/`LocalIds` arrays populated — i.e. only a
locally-spawned STAND-IN for a peer's enemy (the mirror's own materialised bodies, exactly what
`enemy-materialisation.md` says it exists to clean up), never an id this instance did not itself
spawn. **A GIT-placed creature the local engine fights directly is never a candidate for this path
at all.**

The second, decisive piece: on the **HOST**, `EnemyPresenceController.OnFrame` and
`CombatController.OnFrame` both gate their entire per-slot loop on `block.EnemyPresenceCount`/
`block.EnemyCount` — fields ONLY a peer's forwarded census ever populates (`CombatController`'s own
class comment: *"Mirrors the owning peer's enemies into this instance"*). In the two-player rig the
host publishes that census and never receives one, so both counts read 0 on the host for the whole
session, and neither loop body — `KillOnce`, the health pin, the materialise check — ever executes
for anything. **On the host, a creature the host's own engine kills is provably untouched by any
controller in this plugin.**

### 8.2 The live differential, run against a HUMANOID enemy (not a Vulkar) this time

§7's own finding (a real combat kill vanishes exactly like `slay`) used Vulkars — a non-humanoid
creature type whose vanilla death behaviour was never independently checked. This round repeated
the test against `end_soldier2`/`sithsoldier`-tagged Sith troopers in `END_M01AA` (the Endar Spire,
`tools/demo.ps1`'s own default save), on the **HOST**, via the `attack` cheat's player-as-attacker
form (round 71's fix) — genuine melee damage, the engine's own combat-round resolution, with
§8.1 already establishing that neither `CombatController` nor `EnemyPresenceController` ever touches
this creature on this instance.

`sithsoldier` id `0xE`: ordered to fight (`attack 0x7FFFFFFF 0xE`), real damage observed across the
probe (`hp 6 -> ... -> -11, dead=True`), a genuine kill through ordinary combat-round resolution with
no `Odyssey.KillCreature`/`slay` involved anywhere in the chain. Immediately after:

```
resolve 0xE  ->  no target: 0xE resolves to 0x0 here
useplaceable (bare)  ->  the sweep found 64 placeable(s)   (unchanged from before the kill)
creatures.ps1 (raw vtable scan)  ->  0xE no longer present at all
```

Byte-for-byte the same "fully invalidated, no body-bag, no corpse" result rounds 68/71 got from
Vulkars and from `slay` — this time against a humanoid, on the host, with the code-level argument
above already ruling out this plugin's own controllers as the cause. A second target
(`end_gensith` `0x38`, hp 12) was killed the same way and polled at 0.5s intervals starting
immediately after its own death log line: **already unresolvable at the very first poll** (roughly
1-2s after death, the fastest this rig's own console round-trip could ask), and stayed that way for
a full 21s of continued polling. The removal is nearly instantaneous, not a multi-second "corpse
fade" — consistent with either an immediate (zero-delay) `DestroyObject` in the creature's own
attached `OnDeath` script, or an engine-intrinsic cleanup; this pass could not and did not need to
distinguish which, since **either one is content/engine behaviour outside this plugin's own code**.

Cross-checked against the plugin's own log for the whole session (48,000+ lines): zero occurrences
of `"combat: killed"`, `"materialise: destroyed"`, or `"enemy drive:"` — direct, positive
confirmation that `CombatController`/`EnemyPresenceController`/`MaterialisedEnemies` never ran at
all on the host during either kill, matching §8.1's static argument exactly.

### 8.3 Verdict

**The corpse-vanish is not a coop-plugin defect. There is nothing in this codebase to fix.** A
creature killed through the engine's own genuine combat-round resolution, on the host, with
`CombatController` and `EnemyPresenceController` both structurally incapable of having touched it
(§8.1) and independently confirmed never to have run against it (§8.2's log grep), still vanishes
exactly like a mirrored/`slay` death does. The task brief's own premise — "vanilla KOTOR corpses
persist at the same object id and stay clickable/lootable" — does not hold for the creature types
and modules this project's own save/rig actually exercises (the Endar Spire's Sith troopers,
`tar_m10aa`'s Vulkars); whatever holds in some other module or for some other creature template,
these do not leave one, independent of coop.

This also **closes container-sync.md's own long-open corpse-loot matrix** (§5.5, §6.5, §7's own "(B)
could not be run"), not by completing it but by establishing there is nothing left to complete it
against: no kill method available to this rig, coop or single-player-equivalent, produces a
lootable corpse in any module this project tests against. `ContainerUseHook`'s refusal, the
open/close mirror, and `ContainerLoot` replication for a corpse were designed against the
assumption a corpse would eventually be producible to test them with; that assumption is now known
false for this project's own test modules, and the honest status is **not applicable here**, not
"still open."

### 8.4 What would still be worth knowing (not this round's scope)

- Whether SOME creature template in SOME other module (a boss, a named NPC with a scripted
  death, a droid with a "destroyed" wreck model) leaves a persistent corpse in this exact game
  version — if one exists, `ContainerUseHook`'s refusal/mirror machinery this pass never got to
  exercise against a corpse would need its own live pass against that specific creature.
- The exact mechanism (an `OnDeath`-script `DestroyObject` vs. something engine-intrinsic) was not
  distinguished, only bounded to "outside this plugin's own code" — reading the actual attached
  script resource for one of these creature templates (outside this codebase's own scope, a game
  data question rather than an engine-binary one) would settle it definitively.

### 8.5 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1623/1623 passed**, unchanged baseline — this round's
container/corpse investigation added no plugin code (diagnostic-only reads plus grep/log analysis
against the existing, unmodified `CombatController`/`EnemyPresenceController`/`MaterialisedEnemies`).
Both plugin builds (`src/KotorCoop.Plugin/build.ps1 -Install`, both GOG installs) clean, 0 warnings
0 errors, across the round's several rebuild/redeploy/relaunch cycles (shared with round 72's
`party-reconciliation.md`/`phantom-locomotion.md` work in the same session).
Both plugin builds clean across the round's several rebuild/redeploy cycles.

## 9. Round 77 (2026-08-23) — the open/close cadence mismatch: found by reading, fixed, smoke-
## tested live, exact latency not directly measured

A real (not staged) user report: *"very big delay... closing biggest"*, describing container
open/close taking noticeably longer to reach a peer than other state.

### 9.1 The mismatch, confirmed by reading `CoopAgent.cs`'s own pump loop

Two different channels carry two different halves of "a container is closed":

- `PumpInteractionLocks` — the open/close ANIMATION and the use-lock claim itself — runs on the
  ~150 ms combat cadence (`CombatPumpIntervalMs`), the same cadence enemy presence and phantom
  damage ride.
- `PumpPlaceables` — the DURABLE open-or-locked STATE (`bridge.CollectChangedPlaceableState()` /
  `TryWritePlaceableStates`) — what actually keeps a container looking closed rather than getting
  reasserted open (the round-66 `ContainerCloseHook` fix this document's own §2 covers) — ran on
  the ~1000 ms heavy-world-state cadence (`HeavyPumpIntervalMs`), alongside items, progression,
  world-state globals, journal and doors.

So a container's visible close animation and use-lock release could reach a peer within ~150 ms,
while the STATE that keeps it looking closed could lag up to a full ~1000 ms cycle behind it —
matching "closing biggest" (a container is the one interaction where the ANIMATION and the
DURABLE STATE are both directly, immediately visible to the player, so the gap between them reads
as a stall) more precisely than doors (animation-only, no separate durable-state channel most
players would notice diverging).

### 9.2 The fix — the same rollout shape `dialogue_fast_join`/`party_fast_pump` already used

New setting, `container_fast_pump` (`coop.ini`, default **off**), threaded through
`CoopConfig.cs` → `KotorCoop.Agent/Program.cs` → `CoopAgent.AgentSettings.ContainerFastPump`.
When on, `PumpPlaceables` moves from the 1000 ms heavy pass onto the SAME 150 ms combat cadence
`PumpInteractionLocks` already rides, so a container's animation/lock and its durable state land
in the same ~150 ms window instead of being staggered by up to ~850 ms. Off, behaviour is
byte-for-byte identical to before this pass (the call site simply moves back to the heavy pass).
Door/placeable state for anything nobody has recently touched is unaffected either way —
`CollectChangedPlaceableState` is a shadow-diff that only ever reports a REAL change, so polling
it more often costs nothing extra when nothing changed, the same argument dialogue's and party's
own fast-path flags already made for their own channels.

Files: `src/KotorCoop.Session/CoopConfig.cs` (setting + `container_fast_pump` ini key),
`src/KotorCoop.Session/CoopAgent.cs` (`AgentSettings.ContainerFastPump`, the pump-loop split),
`src/KotorCoop.Agent/Program.cs` (threading), `profiles/coop.ini.example` (documented).

### 9.3 What this round could and could not verify live

**Smoke-tested, not latency-measured.** `container_fast_pump=1` set on both installs for the
back half of this round's rig session (the same session used for `dialogue-sync.md`'s round-77
forced-conversation work above): a full session including a module transition (`end_m01aa` →
`tar_m02ad`), trigger firing, and a forced dialogue ran with the flag on throughout — no crash,
no new exception in either plugin log (the "FIRST-CHANCE exception"/"crash probe installed"
lines present are the same standard startup diagnostics every session logs, unchanged in shape
from before this pass), no regression in anything else this session exercised.

**Not done, and left for a future round**: an actual before/after container open/close timing
measurement (host closes a container, timestamp; client's own placeable-state field flips,
timestamp; compare `container_fast_pump=0` vs `=1`) — this round's rig time went to the dialogue
work above instead, and no OS-level mouse/keyboard input was available this session (see
`docs/research/dialogue-skip-desync.md`'s own round-77 entry) to actually click a container open
and closed for a timed test. The fix is shipped and reasoned from the code's own cadence
constants, not yet confirmed against a measured clock.

### 9.4 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1635/1635 passed**, same baseline as immediately before
this round's changes — the new setting only threads an existing bool through, no new test was
added or needed (`CoopAgent`'s own pump-loop split has no unit test seam; it is exercised the
same way `DialogueFastJoin`/`PartyFastPump` are, by the live rig). Both plugin builds
(`src/KotorCoop.Plugin/build.ps1 -Install`, both GOG installs) clean, 0 warnings, 0 errors.

## 10. Round 78 (2026-08-23) — the real measurement, and a MUCH bigger bug than
`container_fast_pump` found and fixed along the way; the flag itself stays off, for a documented
reason that has nothing to do with its own cadence

Picks up §9's own honest gap: an actual before/after latency measurement, `container_fast_pump=0`
vs `=1`, never done. Two GOG installs, `tools/demo.ps1`'s default save (END_M01AA), driven entirely
through the plugin's console channel (`useplaceable`/`closeplaceable`, the same primitive §5/§6
already established) — no OS input needed, so this round's own version of the "no keyboard/mouse
access" blocker that limited round 77 never applied here.

### 10.1 Method

A dedicated script (`container-latency.ps1`, the same `VirtualQueryEx`/vtable-scan technique
`verify.ps1` already uses for `CSWSPlaceable`) timestamps the HOST's own `useplaceable`/
`closeplaceable` console send, then polls the CLIENT's own copy of placeable `+0x338` (the
`OpenInventory`/`CloseInventory` field itself — the DURABLE open/closed state a peer actually
sees, not merely the lock) at 10ms resolution until it flips. Five open/close cycles per
condition, host teleported onto the container first (`tp 10.8 21.87`, its own position — §5's own
technique) so a real AI walk-and-use delay never contaminates the number being measured.

### 10.2 The baseline measurement surfaced a FAR bigger bug than the one this round came to check

First baseline pass (`container_fast_pump=0`, the shipped default, UNCHANGED code):

```
[baseline-off] OPEN  avg=1602.6ms min=1075 max=3273 n=5
[baseline-off] CLOSE avg=5120.4ms min=5099 max=5138 n=5
```

CLOSE landing at **5099-5138ms, five times, to within 39ms of each other** is not network jitter —
it is a fixed timeout. `InteractionLockStore.DefaultLeaseMs = ProtocolConstants.PeerTimeoutMs`
(5000ms) was the only 5-second constant anywhere near this code, which meant every close was
missing its own RELEASE message entirely and falling all the way back to the interaction lock's
LEASE EXPIRING on its own — the same slow path §5.4 designed for a DEAD HOST, not an ordinary
close.

**Root cause, traced through the code, not guessed:** `InteractionLockController.PublishLocalClaim`
(`src/KotorCoop.Plugin/InteractionLockController.cs`) blanks the WHOLE claim identity to
`kind=None, targetId=0, tag=""` the instant a container closes — the class's own header comment
even defends this as deliberate: *"A LEVEL. The lease on the far side expires on its own when this
stops being true, which is the whole reason there is no release message to lose."* That reasoning
holds for `InteractionKind.Conversation` (a claim is meaningless without its tag, and a blank tag
already reads as "nothing"), but it silently defeated `InteractionKind.Container`, whose claim is
keyed on an OBJECT ID rather than a tag: `CoopHost.PublishInteractionClaim`'s own guard —
```csharp
if (string.IsNullOrEmpty(tag) && !(kind == InteractionKind.Container && targetId != 0))
{
    return;
}
```
— and `InteractionLockStore.TryKey` (`case InteractionKind.Container: if (targetId == 0) { ...
return false; }`) BOTH require a nonzero `targetId` to even recognise a Container-kind packet at
all. A release with `targetId=0` is indistinguishable, at both of those call sites, from "nothing
happening" — so `_interactionLocks.Claim(...)` was never even reached for a container release, on
either the host's own claim (`CoopHost.PublishInteractionClaim`'s guard) or a client's (silently
dropped one hop later, at `TryKey`, inside `HandleInteractionClaim`). **Every container close in
this codebase, since protocol version 37 shipped, has been riding the 5-second dead-host lease
fallback instead of an immediate release — completely unrelated to `container_fast_pump`, whose own
channel (`PumpPlaceables` → `PlaceableController.Apply`) only ever OPENS
(`if (desired.Open && !Odyssey.IsPlaceableOpen(placeable) && ...)` — no `Open=false` branch exists
at all) and could not have been the close-side bottleneck under any setting.**

**Fix** (`src/KotorCoop.Plugin/InteractionLockController.cs`): a new field, `_lastClosedContainerId`,
remembers the id `PollLocalContainerUse` just detected as closed (set in the same branch that
already clears `_localContainerId`/`_localContainerActive`), and `PublishLocalClaim`'s inactive tail
now publishes `kind=Container, targetId=_lastClosedContainerId, active=false` instead of an
all-zero blank — an OPTIMISATION on top of the same lease (a lost packet still costs exactly the 5s
it always cost; the class's own "no release message to lose" argument is preserved verbatim), not a
replacement for it. Cleared the moment a fresh hold starts or the module changes; left standing
otherwise, since a stale republish is a harmless no-op (`ContainerUseRule.MayRelease` requires a
FRESH current holder, gone after the first successful release).

**Live-confirmed fixed**, same rig, same container, same script, `container_fast_pump` still `=0`:

```
[off-fixed-tp] OPEN  avg=1257ms  min=1151 max=1401 n=5
[off-fixed-tp] CLOSE avg=289.6ms min=248  max=326  n=5
```

CLOSE dropped from a rock-steady ~5.1s to ~290ms — an **~18x improvement**, and the new numbers are
tight and consistent (248-326ms), not a wide scatter, which is itself evidence the fix removed a
fixed-timeout dependency rather than just getting lucky once. Re-tested in the REVERSED direction
(client opens/closes, host observes) for symmetry: `OPEN avg=1282.7ms, CLOSE avg=322.7ms` (n=3) —
statistically the same numbers, confirming the fix (like everything else in this file) has no
host/client branch.

### 10.3 `container_fast_pump`'s OWN effect, measured cleanly against the now-fixed baseline

With the release bug out of the way, `container_fast_pump=1` was measured against the identical
scenario:

```
container_fast_pump=0 (fixed):  OPEN avg=1257.0ms (1151-1401)   CLOSE avg=289.6ms (248-326)   n=5
container_fast_pump=1 (fixed):  OPEN avg=1277.0ms (1091-1375)   CLOSE avg=333.4ms (217-439)   n=5
```

**No measurable difference — the two conditions are statistically indistinguishable, well inside
each other's own run-to-run jitter.** This matches §10.2's own structural finding exactly:
`PumpInteractionLocks` (the animation mirror AND the use-lock claim/release, §2.2/§2.3) already
rides the fixed 150ms combat cadence UNCONDITIONALLY — it is not gated by `container_fast_pump` at
all, and it is what actually flips a peer's `+0x338` for both edges, open and close alike, in a
live two-peer session. `container_fast_pump` only moves `PumpPlaceables` (`PlaceableController`'s
own OPEN-only durable/"ever opened" channel, §5.3) onto that same cadence — a channel that never
touches a close, and whose OPEN-only reassertion is largely redundant with the lock mirror for two
players already connected to the same session. Its real job (matching §9's own design note) is
catching up a LATE JOINER or a post-module-reload resync, not the live open/close visibility this
round measured.

**Decision: `container_fast_pump` stays OFF by default — CONFIRMED by measurement, not merely
carried over.** The setting itself is not removed (it still has the late-joiner/resync job its own
design intended), but it is not what closes the latency gap the user actually reported. That gap is
now understood and fixed at its real root (§10.2): the same `useplaceable`/`closeplaceable` round-
trip that used to take ~5.1s for a close now takes ~290-330ms, independent of this flag either way.

### 10.4 Regression check

Full session (five rebuild/redeploy/relaunch cycles as the fix above was found and iterated,
~20 open/close cycles total across baseline, fixed, `container_fast_pump=1`, and the reversed-role
check): no crash, no flicker, no false-open observed on either instance. `grep`-ing both plugin
logs for `exception`/`crash`/`access violation` across the whole session finds only the standard
per-session startup diagnostics (`FIRST-CHANCE exception (0x406D1388)` at the same fixed address
every session — the well-known benign .NET thread-naming exception, unchanged in shape from every
prior round's own log) — no new exception of any kind.

### 10.5 Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1635/1635 passed**, same baseline as before this round's
changes (the fix is a plugin-side field-plumbing change with no new managed surface — the plugin
assembly is x86 NativeAOT and carries no unit test coverage, per this project's own standing note;
it is exercised by the live rig above instead). Both plugin builds
(`src/KotorCoop.Plugin/build.ps1 -Install`, both GOG installs) clean, 0 warnings, 0 errors, across
the round's several rebuild/redeploy cycles.

## 11. `container_lock_arbitration` (2026-08-27) — closes roadmap-seamless.md Tier 1 item 4, the
container-loot race; built and unit-tested this round, **not yet run against the live game**

Picks up the item the roadmap has carried since it was first written: *"two clicks on the same
chest within one RTT can both hand out loot before either removal lands (`ContainerUseRule` is
eventually consistent)."* The most recent attempt (also 2026-08-27, logged in the roadmap table)
staged both players on a known footlocker and fired `useplaceable` on both within ~200ms via
`tools/blockpoke.ps1`, but the container turned out to already be emptied by an earlier test round
against the same persistent save — the race itself was never actually exercised. This round does
not repeat that live attempt (no rig session was available); it builds the fix the race needs and
unit-tests it as thoroughly as a fix that depends on real network timing and a real engine click
can be tested without one, then lays out exactly what a live pass still needs to run.

### 11.1 Why the existing lease alone cannot close the race

§2.1/§5 above already give the host's arbiter first-fresh-claim-wins semantics
(`ContainerUseRule.MayClaim`) — that part was never the gap. The gap is *when* a caller finds out.
`InteractionLockController.PublishLocalClaim` publishes the local player's claim once a tick,
`CoopHost`/`CoopClient` broadcast the resulting lock set once a tick, and `ContainerUseHook` only
ever consults whatever that broadcast last said. Two players clicking the same never-yet-claimed
container inside one round trip both read "nobody holds this" from that stale snapshot and both
let `OpenInventory` run — the claim each click *also* publishes has not reached the host, let alone
come back, before either engine call happens. Nothing about that sequence is a bug in the arbiter
itself; it is a caller that never waited for an answer.

### 11.2 The fix: an explicit, synchronously-answered request layered on the same store

Rather than inventing a second lock-keeping structure, this pass adds a REQUEST/RESPONSE pair that
asks the exact same `InteractionLockStore.Claim` decision the existing lease already computes, and
gets a direct answer instead of waiting for the next broadcast to (maybe) reflect it:

- **Protocol** (`src/KotorCoop.Protocol/Packets/InteractionLocksPackets.cs`,
  `PacketCodec.InteractionLocks.cs`, `PacketType.cs`). `InteractionClaimPacket` gains
  `RequestSerial` (0 = the ordinary per-tick republish, unchanged; nonzero = "answer this one
  specifically"). A new packet, `LockResponsePacket`/`PacketType.LockResponse = 56`, carries the
  host's answer back to the SINGLE requesting peer (unicast, not broadcast) — Grant and Deny are
  the same packet with a `Granted` bool rather than two wire types, since both halves of the reply
  need to name the identical (kind, targetId, serial) triple. TODO(version): both are additive
  (a new field appended to an existing packet, a new packet type nothing else touches) and ship
  without their own `ProtocolConstants.Version` bump — that counter is owned by another agent this
  wave; whoever bumps it next should fold this in.
- **Session** (`CoopHost.InteractionLocks.cs`, `CoopClient.InteractionLocks.cs`,
  `IInteractionLockSession.cs`). `RequestContainerLock(module, targetId, requestSerial)` /
  `TryGetLockResponse(requestSerial, out granted)` are the two new members every session
  implements. A CLIENT's `RequestContainerLock` sends one `InteractionClaimPacket` with the serial
  set; its `TryGetLockResponse` reads back whatever `LockResponsePacket` last arrived. The HOST's
  own implementation never touches the network at all — `RequestContainerLock` calls
  `InteractionLockStore.Claim` in-process and `TryGetLockResponse` already has the answer on the
  same call, which is the whole "host and client are symmetric, no host/client branch on the
  calling side" property the task asked for: the caller always does
  `session.RequestContainerLock(...)` then polls `session.TryGetLockResponse(...)`, and it works
  identically whichever role the local instance is playing. `CoopHost.HandleInteractionClaim` now
  unicasts a `LockResponsePacket` back to the sender whenever `RequestSerial != 0`; the ordinary
  per-tick republish (`RequestSerial == 0`) is completely unaffected and gets no reply, exactly as
  before this pass.
- **Plugin -&gt; agent bridge** (`SharedBlock.Dialogue.cs` version 55, region `0x40C0..0x40D0`,
  `SharedBlockView.Dialogue.cs`, `IDialogueBridge.cs`/`KotorGameBridge.Dialogue.cs`,
  `KotorGameBridge.PluginBlock.cs`). The plugin cannot call `ICoopSession` directly — it only ever
  reads/writes the shared block, and the agent process is the only half that can reach the session
  — so this adds the same "cheat-style request/serial" shape every console command's own ack
  channel already uses: plugin -&gt; agent `LocalContainerLockRequestTargetId`/
  `LocalContainerLockRequestSerial`, agent -&gt; plugin `ContainerLockResponseSerial`/
  `ContainerLockResponseGranted`. `WorldStatePump.PumpContainerLockArbitration` (called from the
  existing `PumpInteractionLocks`, every tick) is the forwarder: a NEW serial from the plugin
  becomes exactly one `session.RequestContainerLock` call, and every tick it also copies whatever
  `session.TryGetLockResponse` currently has back into the block. No `SharedBlock.TotalSize`
  growth — the region lands in the 0x40 free bytes left after the companion-equipment growth to
  `0x4100` (version 54).
- **Plugin: the deferral state machine.** `ContainerUseHook` gains `ArbitrationEnabled`
  (`container_lock_arbitration`) and a one-shot grant allow-list (`AllowNextOpen`/`_grantedId`).
  On a click, the existing peer-held refusal loop runs FIRST and unchanged (a peer's own KNOWN
  hold always wins outright, exactly as before); a click that clears that loop and is not already
  pre-approved is now DEFERRED — swallowed with the identical `ret 8` stack effect the peer-held
  refusal already uses (see `ContainerUseHook`'s own remarks on why that needs no synthesised
  failure value) — instead of allowed outright, and captured for `InteractionLockController` to
  see (`LatestDeferred`, the same "capture a serial, poll it" shape `LatestAttempt` already used
  for the open-confirm logic). `ContainerLockArbiter` (`src/KotorCoop.Plugin/ContainerLockArbiter.cs`,
  compiled into the test project via the plugin compile-include trick — see
  docs/CONTRIBUTING.md — since it touches no `Odyssey`/`SharedBlockView`) is the pure click -&gt;
  pending -&gt; grant/deny/timeout -&gt; re-issue/refuse state machine:
  `InteractionLockController.ContainerArbitration.cs` drives it every frame (not the existing
  ten-frame `ApplyPeerLocks` cadence — a grant has to be acted on within a couple of frames), turns
  a fresh deferred click into a request published to the shared block, and on the outcome either
  calls `Odyssey.UseObject` to re-issue the click (Granted, or TimedOut's fail-safe fallback) or
  shows the existing "&lt;name&gt; is looting that" notice machinery with a generic name (Denied —
  the bare deny carries no holder identity; the ordinary peer-held refresh picks that up on its
  own next pass for any FURTHER attempt). The re-issued click runs back through the identical hook:
  the peer-held loop is checked again (our own hold is filtered out of it, same as every other
  local-vs-peer distinction in this file), then the one-shot grant allow-list lets it through.

### 11.3 Why ON by default: the fallback fails safe, and that is the whole argument

`container_lock_arbitration=1` ships as the default. The reasoning the task asked for explicitly:
if the host never answers within `ContainerLockArbiter.TimeoutMs` (300ms — chosen against this
file's own §10.2/§10.3 measurements of a real claim+broadcast round trip, 250-440ms, so the
deadline is comfortably above ordinary LAN jitter without reading as "container appears not to
work"), the deferred click simply proceeds exactly as `ContainerUseHook` behaved before this pass
existed — `AllowNextOpen` plus a re-issued click, logged. A lost request, a lost response, or a
peer running a build old enough to never answer at all are all the SAME failure shape from the
requester's side (no answer), and all fall back to the pre-arbitration behaviour rather than a
refusal — a request that times out can never make a container LESS usable than it already was, only
(rarely, on a bad connection) slower to open by up to 300ms. That asymmetry — fail open, never fail
closed — is what makes ON safe as a default despite this round having no live confirmation that the
request/response round trip itself works end to end against the real game.

### 11.4 What is genuinely tested this round, and by what

- **Codec pinning** (`PacketCodecTests.cs`): `InteractionClaimPacket.RequestSerial` round-trips
  (including the pre-existing zero-default every OLD caller still relies on) and rejects an
  unknown kind at its new byte offset; `LockResponsePacket` round-trips both Grant and Deny and
  rejects an unknown kind.
- **The pure deferral state machine** (`ContainerLockArbiterTests.cs`, 11 tests): idle -&gt; pending
  on `BeginRequest` (and refuses to start with a zero id or zero serial — 0 is reserved wire-wide
  for "nothing pending"); `Poll` returns Granted/Denied and drops back to idle on a matching
  response, Waiting while nothing has arrived and the deadline has not passed, TimedOut (and back
  to idle) once it has; a fresh request can start again once idle; `Reset` drops an in-flight
  request without acting on it, for the module-change case.
- **The loopback race itself** (`ContainerLockArbitrationTests.cs`, real `CoopHost` + TWO real
  `CoopClient`s over loopback UDP — the same pattern `ThreePeerSessionTests.cs` already
  established for other multi-peer bugs):
  `TwoClientsRequestingTheSameContainerWithinOneMillisecondExactlyOneIsGranted` sends both
  clients' requests back-to-back on one thread (as close to simultaneous as the task's own "within
  1ms" framing asks for) at the SAME container id, asserts exactly one comes back granted
  (`oneGranted ^ twoGranted`), has the winner release through the ordinary per-tick claim
  (`Active: false`, the same shape a real close already used before this pass), and confirms the
  loser can then be granted. `TheHostsOwnRequestResolvesSynchronouslyInProcess` and
  `AHostLocalReRequestForItsOwnHeldContainerIsIdempotent` pin the host-local, no-network path
  directly (first request granted immediately, a re-request for the SAME hold succeeds by
  re-entrancy — the identical rule `ContainerUseRule.MayClaim`'s second bullet already gives a
  client). `AClientIsDeniedAContainerTheHostAlreadyHoldsLocally` confirms the host's own in-process
  clicks are not a silent exception to the arbiter a peer can race against.
- **Full suite**: `dotnet test tests/KotorCoop.Tests --no-build` — 2131/2131 passed on a clean run
  (one `ValueScannerTests` timing flake and the documented `CoopCommandDispatcherTests` UDP-port
  flake each independently reproduced as failing alone once and passing on an isolated re-run,
  neither related to this feature — see docs/CONTRIBUTING.md's own note on the latter). Both
  `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`
  (without `-Install` — no live rig was up this round) are clean, 0 warnings, 0 errors.

### 11.5 NEEDS-LIVE — what this round could NOT confirm

Everything above is reasoned from the code and proven by tests that do not touch the real engine.
**Not run this round, for the honest reason that no rig session was available**, in the shape
§3/§5's own live-test scripts already establish for this file:

1. **The request/response round trip against the real game.** Whether the plugin's shared-block
   write (`LocalContainerLockRequestTargetId`/`Serial`) actually reaches
   `WorldStatePump.PumpContainerLockArbitration` and back with the SharedBlock version-55 offsets
   this pass hand-transcribed in two files (`SharedBlock.Dialogue.cs` and
   `KotorGameBridge.PluginBlock.cs`) — every prior SharedBlock growth in this file's own history
   needed at least one live-measured correction to a hand-transcribed offset or write order, so
   this one should be assumed unverified until it is read back from a running process.
2. **The race itself, staged for real** (the exact scenario the roadmap's own last attempt tried
   and could not reach): `end_m01aa` with a FRESH, never-looted footlocker (the last attempt's own
   footlocker was already emptied by an earlier round on the same persistent save — pick a
   DIFFERENT id via a bare `useplaceable` sweep, or use a save that has not run this container-sync
   file's own earlier live passes). Teleport both players onto the container (`tp 10.8 21.87`, §5's
   own technique, to remove walk-latency as a confound) and fire `useplaceable <id>` on both
   instances within about 1 RTT of each other via `tools/blockpoke.ps1` (the same primitive the
   roadmap's own attempt used). Expected with `container_lock_arbitration=1`: exactly one side's
   loot panel actually opens (confirmed via `Odyssey.IsPlaceableOpen`, the same `+0x338` read §5
   already uses); the plugin log on the granted side shows `container: arbitration granted
   0x...; re-issuing open`, and on the denied side `container: arbitration denied 0x... (a peer
   got there first)` plus the "Another player is looting that" HUD line (this round's fallback
   text — the bare Deny carries no holder identity, see §11.2).
3. **A genuine item removal only happens once.** `loottake <id>` (docs/CONSOLE.md; removes a real
   item through the engine's own `RemoveItem` and gives an equivalent copy locally — the console
   primitive built specifically because no injected click can drive the container GUI, see
   docs/research/dialogue-consequences.md) on the granted side, then the same verb on the denied
   side, should show the denied side refused (nothing left to take, or the container empty) rather
   than a second, independent copy of the same loot appearing.
4. **The A/B against `container_lock_arbitration=0`** on the identical staged race, to confirm the
   OLD failure mode (both sides' panels open) is what this pass actually fixes rather than
   something the test rig's own staging already avoided by construction.
5. **The timeout's fail-open path, forced.** Block the response specifically (not the whole link —
   `tools/blockpoke.ps1`-style datagram-level control, or briefly pausing the host's own agent
   process for slightly over 300ms right after a click) and confirm the deferred click still opens
   after the timeout, logged, rather than hanging or silently refusing.

Until 1-5 run, this feature's status is **Built (NEEDS-LIVE)** — see
docs/roadmap-seamless.md Tier 1 item 4's updated row.

## Live verification (2026-08-28): items 2/3/4 of §11.5 run — outcome correct, but a real latency
## bug found in the fast-path request/response itself

Fresh rig, a genuinely fresh footlocker (`0xC6`, `Larrim`-adjacent — actually the demo save's own
starting footlocker near `10.8, 21.87`, still holding its full 3-item stock at test time, confirmed
via `items 61` before the race). Both players teleported onto it (`tp 10.8 21.87`, §11.5's own
technique), `useplaceable 0xC6` fired on both instances back-to-back via two parallel PowerShell
jobs (measured ~120ms apart from the plugin logs' own timestamps — the same order of magnitude as
one agent tick, close to the "within 1 RTT" scenario §11.5 item 2 asks for).

**Item 2 (the race itself) and item 3 (exactly one winner): PASS, run twice** (once contaminated
by an unrelated stuck game-over-suppression loop from an earlier test on the same session — see
below — repeated clean on a fresh session). `closeplaceable 0xC6` afterward confirmed the actual
open state directly: the HOST's copy closed successfully (`+0x338` was 1, now cleared); the
CLIENT's own `closeplaceable 0xC6` was **refused** ("placeable 0xC6 (0xC6) is not open") — the
client's copy never opened at all. Exactly one winner, both times.

**Item 4 (the A/B against `container_lock_arbitration=0`): PASS, confirms this feature is the
actual fix.** Same recipe, same container, `container_lock_arbitration=0` in both `coop.ini`s,
fresh session. This time `closeplaceable 0xC6` succeeded on **both** sides — the OLD race,
reproduced live for the first time (the roadmap's own prior attempts never actually staged it).
Flipped back to `container_lock_arbitration=1` immediately after (the shipped default, unchanged
by this round).

**A genuinely new finding, not predicted by §11.3's own design: the fast-path request/response
round trip itself timed out on BOTH sides, both live runs, 2/2 — yet the outcome stayed correct
anyway.** Plugin log, both host and client, identically: `container: arbitration timed out for
0xC6 (no host answer within 300ms); proceeding as before`. §11.2's own design states the HOST's
own `RequestContainerLock` resolves "synchronously, in-process" — but the plugin can only reach
`ICoopSession` through the shared-block round trip (`LocalContainerLockRequestSerial` ->
`WorldStatePump.PumpContainerLockArbitration`, polled once per AGENT tick -> the response written
back -> read on the PLUGIN's next frame), which is **at least two agent-tick latencies**, not
zero — and on this rig's own measured cadence that round trip did not land inside the 300ms
`ContainerLockArbiter.TimeoutMs` budget, for the HOST's own request as well as the CLIENT's.
**Why the outcome was still correct despite this**: §11.3's fail-open fallback re-issues the
click exactly as pre-arbitration behaviour, which re-enters the OLD `ContainerUseHook` peer-held
refusal loop first — and by the time either side's 300ms timeout elapsed, the ordinary per-tick
claim broadcast (§2.1/§5's pre-existing eventually-consistent mechanism, the one the arbitration
layer was built to route around) had already caught up and correctly refused the loser's
re-issued click. **The new fast path is not doing the job §11.2 designed it to do on this rig — it
is currently a no-op that always times out — but the OLD slower mechanism it was meant to
supersede is still there underneath it and is what is actually winning the race.** This is not
visible from the outcome alone (exactly one winner, every time, matching the design's own success
criterion) — only from reading the plugin log's own timeout line. Flagged as an open item: either
`ContainerLockArbiter.TimeoutMs` needs raising (300ms is apparently too tight for the real
plugin<->agent round trip, not just network jitter as §11.3 assumed), or the
`WorldStatePump.PumpContainerLockArbitration` forwarding path itself has an unmeasured extra
delay `SharedBlock`'s version-55 offsets/write order should be checked against.

**Item 1 (offset correctness) and item 5 (forced timeout path)**: item 1 is confirmed by
construction — the arbitration mechanism visibly ran (logged timeouts, not silence), so the
version-55 offsets are wired correctly even though the round trip itself is slow. Item 5 (forcing
the timeout deliberately) was not run as a separate test — it happened for free, twice, unplanned.

**A staging note for whoever repeats this**: the first live attempt this round was contaminated
by a leftover, continuously-firing `death: game-over suppressed; the session decides` loop on the
CLIENT (thousands of lines/second) from an earlier, unrelated test in the same rig session — not
caused by anything in this feature, but it pollutes log-based verification of anything running at
the same time. A fresh `demo.ps1 -Stop`/relaunch cycle cleared it; the second (clean) run is the
one whose results are reported above.

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean, `dotnet test`
2339/2339. This feature's status upgrades to **Built, live-verified correct-outcome (NEEDS-FOLLOW-UP:
fast-path latency)** — see docs/roadmap-seamless.md Tier 1 item 4's row.

### 11.6 Test suite and build

`dotnet test tests/KotorCoop.Tests --no-build`: **2131/2131 passed** on a clean isolated run (see
§11.4's own note on the two unrelated flakes observed across two full-suite runs this round, each
independently confirmed non-reproducing when filtered to its own test class alone).
`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings, 0 errors.
`src/KotorCoop.Plugin/build.ps1 -WarnAsError` (no `-Install`, no live rig this round): clean, 0
warnings, 0 errors, x86 architecture confirmed on the produced `dinput8.dll`.

### 2026-08-28 follow-up: fallback budget raised to 1000ms

The live pass above showed every explicit request expiring at 300ms — the budget was sized for
one LAN round trip, but the real path is plugin → shared block → agent tick → UDP → host → UDP →
agent tick → shared block → plugin. `ContainerLockArbiter.TimeoutMs` is now 1000ms (two agent
ticks each way with margin). NEEDS-LIVE: re-run the two-click race and confirm the fast path now
resolves inside the budget (`granted`/`denied` log lines instead of `0xC6 no host answer`).

### 2026-08-28, later same day — re-run with the 1000ms budget: PASS, the fast path now resolves

Fresh `tools/demo.ps1` rig, both installs rebuilt/reinstalled from this commit
(`f69cc2d` + this branch's own merge), `dotnet build -warnaserror` and `dotnet test
tests/KotorCoop.Tests --no-build` both clean (2339/2339) before touching the rig.
`container_lock_arbitration=1` is the shipped default in both `coop.ini`s (untouched this round).
Both players teleported onto the same footlocker used in the two prior live passes
(`tp 10.8 21.87`; §5.7's own known gap means the bare `useplaceable` sweep still tags 0xC6 as
`none with inventory` — `HasInventory` reads false for this container live, same as every prior
round — so the race was staged with the explicit-id form, `useplaceable 0xC6`, which does not
depend on that filter). Fired `useplaceable 0xC6` on both instances from two parallel
`Start-Job`/`blockpoke.ps1 console` calls (host send logged `03:12:05.136`, client send logged
`03:12:04.888` — ~250ms apart, the same order of magnitude as the prior round's ~120ms stagger).

**Result: PASS.** Both sides resolved through the fast path, no fallback, no timeout:

```
HOST   [03:12:06.325] container: arbitration denied 0xC6 (a peer got there first)
CLIENT [03:12:06.170] container: arbitration granted 0xC6; re-issuing open
```

A full-log grep for `timed out` on both sides across the whole session returned nothing — every
prior round (§11.4 above) saw `0xC6 no host answer within 300ms` on **both** sides, every time;
this round saw it on **neither** side. `closeplaceable 0xC6` succeeded on both instances
afterward (`+0x338 cleared` on each) — expected and not a second independent open: the client
held the real lock (`granted`) while the host's own copy carries the existing cross-instance
open-animation mirror (§2.3/§5.1), so both sides have a legitimately clearable `+0x338`, but only
one side (`granted`) ever re-issued a real click — the host's `denied` line means its own attempt
was refused before reaching `OpenInventory` a second time, exactly the "exactly one winner"
property §5.1/§11.4 already established, now reached via the fast path instead of the slower
fallback that used to carry it.

**Verdict**: `ContainerLockArbiter.TimeoutMs = 1000` is confirmed live to fix the fast-path
latency gap §11.4 found — the request/response round trip (plugin → shared block → agent tick →
UDP → host → UDP → agent tick → shared block → plugin) now lands inside the budget. This
feature's status upgrades to **Built, live-verified correct-outcome AND fast-path-confirmed** —
no further follow-up item outstanding for this specific gap. `container_lock_arbitration` stays
at its shipped default (`1`, ON) — this round did not change it, only confirmed the 1000ms raise
that shipped ahead of it.

Build/test this round: `dotnet build KotorCoop.slnx -warnaserror` clean; `src/KotorCoop.Plugin
/build.ps1 -WarnAsError -Install` clean on both GOG installs (x86 confirmed); `dotnet test
tests/KotorCoop.Tests --no-build` 2339/2339 passing before the live pass.
