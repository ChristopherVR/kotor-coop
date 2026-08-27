# Synchronizing dialogue/conversations across two co-op instances (2026-07-31)

Verified against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, image base
`0x400000`) via the sha256-matched symbol DB (`tools/kotor1_0_3.db`) and static disassembly
(`tools/disasm.py`, `tools/xrefs.py`, `tools/q.py`). Read `docs/re/object-model.md` and
`docs/re/spawn.md` first — this builds on the address database, the vtable-scan creature
enumeration, the `[0x007A39FC]` app-manager chain, and the `SwapBuffers` frame hook.

Every address is tagged **VERIFIED** (checked in the binary — DB symbol, xref, or disasm),
**INFERRED** (reasoned, not observed), or **NEEDS-LIVE** (cannot be pinned statically; a live
`ReadProcessMemory` probe must confirm). Nothing here was run; this is pure static analysis.

## Headline

**Feasible, and the engine is unusually cooperative.** KOTOR keeps the *entire* conversation
as a first-class server object — `CSWSDialog` (size `0x12C`, vtable `0x0074A618`) — hung off
the initiating creature at a fixed offset (`CSWSObject+0x34`), fully populated with every
entry node, every reply node, their `CExoLocString` text and TLK `StrRef`s, the owner id and
the speaker table. **Detection and spectating are read-only and low risk.** There is a single
server entry point that constructs and loads the dialog (`CSWSObject::StartDialog 0x004CF490`,
reached only from `ExecuteCommandBeginConversation`), a single per-tick updater
(`CSWSObject::UpdateDialog 0x004CD580`, which is the caller of the already-known
`ClearAllPlayerHostileActions 0x005A0940`), and a single reply-handler
(`CSWSObject::ReplyDialog 0x004CB480 -> CSWSDialog::HandleReply 0x005A03C0`). **Shared control
(driving a remote player's reply) is where the real risk lives** and needs a live pass.

The five most load-bearing facts:

| Fact | Address / field | How verified |
| --- | --- | --- |
| Active `CSWSDialog*` for a creature | `*(CSWSObject + 0x34)`, non-null while it runs a dialog | disasm of StartDialog (`mov [esi+0x34],eax`) + UpdateDialog + ReplyDialog |
| "A conversation is active" (client) | `*(CGuiInGame + 0xB4)` | disasm of `GetIsConversationActive 0x0053EF70` + `SendPlayerToServerInput_Dialog 0x00677EA0` |
| The two participants (client) | `*(CGuiInGame + 0x170)`, `*(CGuiInGame + 0x174)` = two object ids | disasm of `CGuiInGame::IsInDialog 0x0062DBD0` |
| Conversation owner / speakers (server) | `CSWSDialog+0x80` owner_object_id, `+0x84` speakers list | DB `offsets` on `CSWSDialog` |
| Drive a reply (client) | `CSWGuiDialog::SelectReply 0x006A7110` (thiscall, 1 int arg) | disasm: bounds-checks `+0x6C`, calls `SetDialogSelection` then `UpdateDialog` |

---

## 1. Conversation object & lifecycle

### The live dialog lives on the initiating object

`CSWSObject::StartDialog 0x004CF490` (`__thiscall`, `this` = the object that initiates) is the
**one** place a live `CSWSDialog` is built. **VERIFIED** by disasm:

```
0x004CF5C7  push 0x12c                 ; sizeof(CSWSDialog) = 300
0x004CF5CC  call 0x6fa7e6              ; operator new
0x004CF5E6  call 0x5a0e80              ; CSWSDialog::Constructor
0x004CF5F8  mov  [esi+0x34], eax       ; store CSWSDialog* on the object   <<<
0x004CF5FB  call 0x5a2ae0              ; CSWSDialog::LoadDialog(resref, ...)
   on load failure: delete, [esi+0x34] = 0
```

So **`*(uint32*)(object + 0x34)` is the active-conversation pointer**, non-null exactly while
that object owns a running dialog, NULL otherwise. **VERIFIED.** `StartDialog` also zeroes a
block of per-object dialog flags (`+0x38,+0x3C,+0x40,+0x44,+0x48,+0x50,+0x60,+0x64,+0x68,
+0x70,+0x74`) and sets `+0x74 = 1`. **VERIFIED** (disasm), meaning **INFERRED** for the
individual flag semantics.

`StartDialog` is reached **only** from `ExecuteCommandBeginConversation 0x0052E9F0` (call
sites `0x0052ED1C`, `0x0052ED66`). **VERIFIED** by `xrefs.py 0x004CF490` (exactly 2 callers,
both inside that routine). The NWScript path is therefore:

```
ActionStartConversation (routine, 0x0052D5B0)  -- queues an action on the object
   ... action executes ...
BeginConversation       (routine, 0x0052E9F0)  -- validates participants, sets
                                                  action_conversation, calls StartDialog
   -> CSWSObject::StartDialog 0x004CF490        -- constructs CSWSDialog at obj+0x34
```
**VERIFIED** (disasm of `BeginConversation` shows it calls `Get/SetActionConversation` then
falls through to the participant-resolution and `StartDialog`).

### Per-tick and the known ClearAllPlayerHostileActions

`CSWSObject::UpdateDialog 0x004CD580` (`__thiscall`) is the per-tick driver. **VERIFIED**
it is the *sole* caller of `ClearAllPlayerHostileActions 0x005A0940` (`xrefs.py` → one call
site `0x004CD5AD`), and it reaches it as `mov ecx,[esi+0x34]; call 0x5A0940` — i.e. **it
passes the `CSWSDialog*` from `obj+0x34`**, re-confirming that field. **VERIFIED.**
`UpdateDialog` gates on `[esi+0x54]` (`dialog_owner`, DB-named) being the player id
(`0x7F000000`) or the object's own id (`[esi+4]`). **VERIFIED.**

### "Active" flag and participants, client-side

Cleanest active test is the client GUI flag. `GetIsConversationActive 0x0053EF70` reads
`*(CClientExoApp::GetInGameGui() + 0xB4)`; `GetInGameGui 0x005ED690` is literally
`return *(*(this+4) + 0x40)`. **VERIFIED.** Chain (all **VERIFIED** from the disasm idiom
`mov eax,[0x7A39FC]; mov ecx,[eax+4]; call 0x5ED690`):

```
mgr            = *(void**) 0x007A39FC          ; app manager / wrapper
clientApp      = *(void**)(mgr + 0x04)         ; CClientExoApp
clientInternal = *(void**)(clientApp + 0x04)   ; CClientExoAppInternal
gui            = *(void**)(clientInternal + 0x40)   ; CGuiInGame
conv_active    = *(int*)  (gui + 0xB4)         ; nonzero while a conversation runs
```

`CGuiInGame::IsInDialog 0x0062DBD0` compares an object id against `*(gui+0x170)` and
`*(gui+0x174)` — **the two dialog participants (speaker + listener) as object ids**.
**VERIFIED.** These are the client-side owner/other-party identifiers.

### Owner / other party, server-side

From the DB `offsets` table on `CSWSDialog` (**VERIFIED** — DB symbols):

| Off | Field |
| --- | --- |
| `+0x80` | `owner_object_id` (initiator) |
| `+0x84` | `speakers` (`CSWSDialogSpeaker*`, each `{object_id @0}`) |

And on `CSWSObject` (**VERIFIED** — DB symbols): `+0x54 dialog_owner`, `+0x30 portrait_id`,
`+0x38 dlg_delay_day`, `+0x3C dlg_delay_ms`, `+0x40 dlg_last_time_day`, `+0x44
dlg_last_time_ms`. So a creature is "the owner instance's dialog partner" when its
`dialog_owner (+0x54)` equals the PC id.

### End / abort

- Normal end runs through `CSWSObject::ReplyDialog 0x004CB480`: after `HandleReply` returns
  non-continuing, it calls `CSWSDialog::RunEndConversationScript 0x005A0A40` and clears
  `obj+0x38/+0x3C/+0x48`. **VERIFIED** (disasm).
- `CSWSDialog::Cleanup 0x005A27B0`, `CSWSObject::OnEndDialog 0x004EF910`,
  `CSWSDialog::ClearDialogOwnerInObject 0x0059E9B0` finish teardown. **VERIFIED** (DB names).
- Client end: `CGuiInGame::CloseDialog 0x006332B0`, `CSWGuiDialog::EndDialog 0x006A6D70`.
  **VERIFIED** (DB names). The observable end-of-conversation edge is `*(gui+0xB4)` returning
  to 0 and `obj+0x34` returning to NULL.

---

## 2. Reading current node state

### Server-side (robust, structural)

`CSWSDialog` (size `0x12C`, vtable `0x0074A618`) holds the whole parsed `.dlg` (all
**VERIFIED**, DB `offsets`):

| Off | Field | Notes |
| --- | --- | --- |
| `+0x04` | `entries_count` | NPC lines |
| `+0x08` | `entries` | `CSWSDialogEntry*`, stride `0xB0` |
| `+0x0C` | `reply_count` | player replies (whole file) |
| `+0x10` | `replies` | `CSWSDialogReply*`, stride `0xB4` |
| `+0x14/+0x18` | `starting_count` / `starting_list` | `CSWSDialogStarting {CResRef active@0; int index@0x10}` |
| `+0x80` | `owner_object_id` | |
| `+0x84` | `speakers` | |

Each `CSWSDialogEntry` / `CSWSDialogReply` begins with a `CSWSDialogNode`, whose first member
is `CSWSDialogBase`. **VERIFIED** (DB `offsets`):

| Node off | Field | Type |
| --- | --- | --- |
| `+0x00` | `speaker` | `CExoString {char* @0; u32 len @4}` |
| `+0x08` | `text` | `CExoLocString {CExoLocStringInternal* @0}` — carries the TLK `StrRef` |
| `+0x10` | `script` | `CResRef` (16 bytes) |
| `+0x34` | `sound` | `CResRef` |
| `+0x54/+0x58` | `quest_entry` / `plot_index` | |
| (`CSWSDialogEntry` only) `+0xA8/+0xAC` | `link_entry_count` / `link_entries` | `CSWSDialogLinkEntry {CResRef active@0; u32 display_inactive@0x14}` — the reply links available *from this entry* |

`CSWSDialog::GetExoStringFromLocString 0x0059F320` (`__stdcall`) resolves a node's
`CExoLocString` to displayable text through the TLK. **VERIFIED** (DB name); it is the
engine's own loc-string→text call and is a candidate read helper.

**The one gap: which entry is *currently displayed*.** The whole file is in `entries[]`, but
the index of the node on screen right now is not DB-named. From disasm of
`CSWSDialog::SendDialogEntryNode 0x005A13D0`, `[dialog+0x60]` is read as the count of active
reply-links for the current entry (it sizes `ebp*4` scratch arrays from it). **INFERRED**
that current-entry / current-reply-set state lives in `CSWSDialog +0x60..+0x74` (the same
band `HandleReply 0x005A03C0` reads as `this+0x64` (a list head), `this+0x70`, `this+0x74`).
The exact "current entry index" field is **NEEDS-LIVE**: a probe must open a conversation,
snapshot `dialog+0x60..0x78`, click through, and watch which dword tracks the visible line.

### Client-side (what the player actually sees)

The rendered text/speaker pair is a `CGuiInGameDialogMessage {CExoString message @0; CExoString
speaker @8}` (**VERIFIED**, DB). `CGuiInGame` handles an incoming entry in
`HandleDialogEntry 0x00631D80` (gated on `*(gui+0xB98)`, a pending-entry flag — **VERIFIED**
disasm) and shows it via `ShowDialogEntry 0x0062B730` / `ShowDialogMessage 0x0062B130`. The
exact `CGuiInGame` member that stores the current message string is **NEEDS-LIVE** (not
DB-named); `ShowDialogMessage`'s body would reveal it, but reading the already-resolved string
off the panel is the least robust surface — prefer the server `CSWSDialog` structure above.

### The reply list the GUI is showing

The conversation panel class is **`CSWGuiDialog`** (size `0x1DFC`, vtable `0x007559E0`;
cinematic subclass `CSWGuiDialogCinematic 0x00755800`, computer `CSWGuiDialogComputer`). Its
reply state (all **VERIFIED** by disasm):

| Field | Location | Source |
| --- | --- | --- |
| reply count currently shown | `*(int*)(panel + 0x6C)` | bounds check in `SelectReply`/`GetReplyIndex` |
| reply → `.dlg` node index map | `int[](panel + 0x1970)`, indexed by slot | `GetReplyIndex 0x006A6D80`: `return *(panel + slot*4 + 0x1970)` |
| highlighted/selected reply slot | `*(int16*)(panel + 0x1C8A)` | `HandleSelectReply 0x006A7390` (`movsx ecx,[esi+0x1C8A]`) |
| replies listbox widget | `panel + 0x19C4` | DB `offsets` |
| message label widget | `panel + 0x1CA4` | DB `offsets` |

So the reply node indices the GUI is offering are `*(panel+0x1970 + i*4)` for
`i in [0, *(panel+0x6C))`, and the highlighted one is `*(panel+0x1C8A)`. The `panel` pointer
itself (a `CSWGuiDialog*` reachable from the GUI manager `0x007A39F4` or `CGuiInGame`) is
**NEEDS-LIVE** — it was not pinned to a named offset here; a live probe should scan for
vtable `0x007559E0` or walk the GUI panel tree.

---

## 3. Driving a conversation

| Purpose | Function | ABI | Notes |
| --- | --- | --- | --- |
| Select reply N (client) | `CSWGuiDialog::SelectReply 0x006A7110` | `__thiscall(panel, int index)` | **VERIFIED** bounds vs `+0x6C`, then `CGuiInGame::SetDialogSelection 0x0062ADA0` + `UpdateDialog 0x006339C0`. This is the click-reply-N equivalent. |
| Commit highlighted reply | `CSWGuiDialog::HandleSelectReply 0x006A7390` | `__thiscall(panel)` | **VERIFIED**: reads `+0x1C8A`, dispatches vtable `+0x6C`. |
| Send input to server | `CSWCMessage::SendPlayerToServerInput_Dialog 0x00677EA0` | `__thiscall` | **VERIFIED** (DB); gated on `*(gui+0xB4)`. |
| Server: process a reply | `CSWSObject::ReplyDialog 0x004CB480` | `__thiscall`, `ret 0x14` (5 dword args) | **VERIFIED**: loads `[obj+0x34]` as `this` for `HandleReply`. |
| Server: advance to reply node | `CSWSDialog::HandleReply 0x005A03C0` | `__thiscall(dialog, ...)` | **VERIFIED** call convention from `ReplyDialog`'s push sequence (`this` in ECX = `obj+0x34`). Args (entry/reply ids + flag) are **INFERRED** from the pushes; not decoded to named parameters. |
| End the conversation (server) | `CSWSDialog::RunEndConversationScript 0x005A0A40` then object-field clear | `__thiscall` | **VERIFIED** (called on the non-continue branch of `ReplyDialog`). |
| End the conversation (client) | `CGuiInGame::CloseDialog 0x006332B0` / `CSWGuiDialog::EndDialog 0x006A6D70` | `__thiscall` | **VERIFIED** (DB names). |

Note the **client** drive path (`SelectReply`) has a fully verified, self-contained ABI
(one int arg, no struct marshalling) and routes through the engine's own message pipeline to
the server — strictly preferable to calling the **server** `HandleReply` directly, whose exact
argument shape is only inferred.

---

## 4. Which .dlg is running & node addressing

A node is stably identifiable over the wire as **(dlg resref, entry-or-reply, node index)**,
or more cheaply as the entry/reply's **TLK `StrRef`** (from the node's `CExoLocString @ +0x08`,
which is globally unique to the line and identical on both installs — the two instances share
`dialog.tlk`).

Reaching the running resref at runtime:

- `CSWSCreature::GetDialogResref 0x004F8290` (`__thiscall`) returns the creature's blueprint
  conversation resref, read from `CSWSCreatureStats+0x44` (a `CResRef`). **VERIFIED** (disasm:
  `mov eax,[ecx+0xA74]` (stats) `... +0x44`; empty-`CResRef` fallback via `0x00406D80`). This
  is the *default* conversation of the owner creature; when a dialog is started with the
  creature's own conversation (the common case) it equals the running `.dlg`.
- `CServerExoAppInternal+0x1B908 last_conversation` (a `CExoString`) is written on
  conversation start. Accessors `GetLastConversation 0x004AEFE0` / `SetLastConversation
  0x004AEFD0`. **VERIFIED** (DB offset name + disasm: `[internal+4]+0x1B908`, copied via the
  `CExoString` op at `0x005E5C50`). **NEEDS-LIVE** whether this holds the resref string vs a
  tag; a probe should read it during a live conversation.
- `CServerExoAppInternal+0x1B910 action_conversation` — the queued/active
  `ActionStartConversation` handle. Accessors `GetActionConversation 0x004AF020`,
  `SetActionConversation 0x004AF010`. **VERIFIED** (disasm). Cleared/set inside
  `BeginConversation`; it is the *pending action*, not the live `CSWSDialog` (that is
  `obj+0x34`). **INFERRED** it is an action pointer/id.

For the network protocol, prefer the **StrRef of the current entry** as the sync key: it is
already an integer, install-independent, and needs no resref plumbing. The resref +
entry/reply index is the fallback when a StrRef is 0 (custom strings stored inline in the
`CExoLocString` rather than the TLK).

---

## 5. Suppression / lock

Goal: on the non-owner instance, do not run the real conversation (spectate or wait instead).
Options, cleanest first:

1. **`CanSendDialog` gate (read/write a flag, no hook) — LOW risk.**
   `CClientExoAppInternal+0x228` is a boolean "may submit dialog input"; setter
   `CClientExoApp::SetCanSendDialog 0x005EDC80 -> CClientExoAppInternal::SetCanSendDialog
   0x005F3030` (`mov [esi+0x228], arg`), getter `0x005EDC90 / 0x005F3070`. **VERIFIED**
   (disasm). Forcing it 0 on the non-owner blocks that client from *committing* replies. It
   does **not** stop a conversation from *starting*, and whether the engine re-sets it each
   frame is **NEEDS-LIVE**. Good for "spectator can't choose", not a full lock.

2. **Guard `CSWSObject::StartDialog 0x004CF490` — MEDIUM risk (requires a hook).**
   This is the single construct-and-load entry point (§1), reached only from
   `BeginConversation`. An inline detour that early-returns 0 on the non-owner would prevent
   the dialog object from ever being built there. **VERIFIED** it is the only construction
   site. **UPDATE 2026-08-06: the return-value contract is now VERIFIED statically** —
   disassembly of both call sites (`0x0052ED1C`, `0x0052ED66`) shows `test eax,eax; je
   <cleanup>`, where the cleanup path calls `0x004F34A0(initiator, 0x7F000000, 0)` and
   completes normally, i.e. returning 0 takes a failure path retail already ships. The
   function's own epilogue is `ret 0xC` (`0x004CF8DF`), and the 13-byte SEH prologue
   (`6A FF 68 9E 9D 71 00 64 A1 00 00 00 00`) relocates verbatim, so a guard that refuses
   BEFORE the prologue runs is a plain `xor eax,eax; ret 0xC`. **Shipped** as
   `DialogueGateHook` (verify-bytes-or-refuse; whether a blocked SCRIPTED conversation's
   caller retries gracefully in play remains NEEDS-LIVE).

3. **Freeze via input mode — LOW risk, coarse.** The known client input class field
   `CClientExoAppInternal+0x9C == 3` means "dialog mode" (given). Reading it is a free,
   read-only way to know the non-owner has been *pulled into* dialog mode so the session can
   freeze that player's movement/AI. It is a detector, not a preventer.

Recommended lock design: the **session holds a single "dialogue token."** Only the token
holder's instance is allowed to let `StartDialog` run (option 2 if a hook is acceptable, else
rely on the natural single-player flow and use option 1 to keep the other client from
committing). The non-token instance uses §1's read-only signals to freeze and §2/Layer-2 to
spectate.

---

## Layered design (lowest → highest risk)

### Layer 1 — Detect active conversation + participants (SAFE, read-only)

Purely `ReadProcessMemory`. Per frame at the `SwapBuffers` hook:

- `conv_active = *(int*)(gui + 0xB4)` via the client chain (`0x7A39FC → +4 → +4 → +0x40`).
- participants `*(gui+0x170)`, `*(gui+0x174)` (object ids).
- cross-check server-side: the PC creature (id `0x7FFFFFFF`, `is_pc=1` per object-model.md) or
  its partner has `obj+0x34 != 0`; partner is the creature whose `dialog_owner (+0x54)` is the
  PC id.

Addresses: `0x007A39FC`, `+0x04/+0x04/+0x40` chain, `CGuiInGame+0xB4/+0x170/+0x174`,
`CSWSObject+0x34/+0x54`. **Crash risk: none** (reads only). **Must verify live:** the exact
value of `+0xB4` (int vs bitfield) and that `+0x170/+0x174` are object ids not pointers — both
strongly implied by disasm but not observed. This layer is essentially already available from
existing findings; it lets the session hold the dialogue token and freeze the other player.

### Layer 2 — Spectate (read the owner's current line, mirror it) (LOW–MEDIUM risk)

On the token owner, each frame read the current entry's **speaker id + text/StrRef** and send
it; on the other instance, display it (own overlay via `PrintStringScreenPos 0x0044CCA0` from
the console doc, or drive the native panel — see risk).

Read surface (prefer server-side structure):
- `dialog = *(CSWSObject_owner + 0x34)`.
- walk `entries` (`dialog+0x08`, stride `0xB0`); the current entry index is
  `CSWSDialog+0x60..0x74` (**NEEDS-LIVE** to pin — Layer 2's single required live probe).
- node text `CExoLocString @ node+0x08` → `StrRef`; speaker `CExoString @ node+0x00`.
- reply set the GUI shows: `CSWGuiDialog` `+0x6C` count, `+0x1970[i]` node indices,
  `+0x1C8A` highlighted (panel pointer is **NEEDS-LIVE**, scan vtable `0x007559E0`).

**Crash risk:** none if we only read and render on our own overlay. It rises only if we *call*
`GetExoStringFromLocString 0x0059F320` (stdcall, plausible ABI but engine-internal) or drive
the remote panel's `SetDialogMessage 0x006A7010` (thiscall) — flag both **HIGH risk** until
live-verified, and prefer reading the already-resolved `CExoString` off the node, or the TLK
directly, into our own text draw. **Must verify live:** the current-entry-index field, and the
`CSWGuiDialog` panel pointer.

### Layer 3 — Shared control (drive reply selection from the network) (HIGH risk)

The other player picks a reply; we replay that pick on the owner instance.

Lowest-ABI-risk path (**preferred**): call **`CSWGuiDialog::SelectReply 0x006A7110`**
(`__thiscall(panel, int index)` — one integer arg, fully verified body) on the main thread,
then let the engine's own `SetDialogSelection`/`UpdateDialog`/`SendPlayerToServerInput_Dialog`
pipeline carry it to the server. This mirrors exactly what a local click does.

Higher-risk path (avoid unless the above fails live): call the server
`CSWSObject::ReplyDialog 0x004CB480` / `CSWSDialog::HandleReply 0x005A03C0` directly — their
argument shapes are only **INFERRED** (`ReplyDialog` is `ret 0x14` = 5 dword args, contents
undecoded). A wrong stack here **crashes** exactly like the spawn doc's ABI warning.

**Crash risks:**
- Any engine `call` with a guessed ABI — **HIGH**. Only `SelectReply` (1 int) and the
  read-only `GetReplyIndex 0x006A6D80` have a fully verified ABI; treat everything else as
  needing a `param_size_bytes` cross-check in the DB before calling.
- Threading — **HIGH**: like spawn, the dialog/VM/GUI state is unsynchronised; drive only from
  the main-thread `SwapBuffers` hook.
- Desync — **MEDIUM**: the two instances must agree on the node addressing (StrRef or
  resref+index) or a mispicked reply diverges the trees.

**Must verify live (all of Layer 3):** the `CSWGuiDialog` panel pointer; that `SelectReply`
alone reproduces a click end-to-end (through to the server tree advancing); the reply-slot →
node-index mapping is stable across both installs; the `HandleReply` argument shape (only if
the server path is used).

---

## Infeasible / needs live verification (honest list)

- **Current-entry index on `CSWSDialog`.** Band `+0x60..+0x74` is implicated (disasm of
  `SendDialogEntryNode`, `HandleReply`) but no single field is confirmed as "index of the
  line on screen." **NEEDS-LIVE.**
- **`CSWGuiDialog` panel pointer at runtime.** Not tied to a named offset here. Scan for
  vtable `0x007559E0` or walk the GUI manager (`0x007A39F4`). **NEEDS-LIVE.**
- **`CGuiInGame` current-message member.** The resolved on-screen string's storage offset
  is not DB-named; prefer the server `CSWSDialog` read. **NEEDS-LIVE.**
- **`last_conversation (+0x1B908)` contents** — resref vs tag vs display name. **NEEDS-LIVE.**
- **`HandleReply` / `ReplyDialog` argument semantics** — arg count known (`ret 0x14`), meaning
  not decoded. **NEEDS-LIVE** before any direct server call.
- **`CanSendDialog (+0x228)` re-set cadence** — whether the engine rewrites it each frame,
  which decides if it is a durable suppression lever. **NEEDS-LIVE.**
- **`StartDialog` return contract** for a suppression hook. **NEEDS-LIVE.**
- **`GetIsConversationActive`'s `+0xB4`** exact type/semantics (int flag vs count). Strongly
  implied; not observed. **NEEDS-LIVE.**

None of these block Layer 1 (fully read-only and safe). Layer 2 needs exactly one probe
(current-entry index). Layer 3 is gated on the panel pointer + a live `SelectReply` round-trip.

---

## Quick-reference address list (this build, no ASLR)

Client chain: `mgr=*(0x007A39FC)` · `clientApp=*(mgr+0x04)` · `clientInternal=*(clientApp+0x04)`
· `CGuiInGame gui=*(clientInternal+0x40)` (via `CClientExoApp::GetInGameGui 0x005ED690`).

Server chain: `serverApp=*(mgr+0x08)` · `serverInternal=*(serverApp+0x04)` (per object-model.md).

| Address | Symbol / field | Conv | Verify |
| --- | --- | --- | --- |
| `0x004CF490` | `CSWSObject::StartDialog` (constructs `CSWSDialog` at `obj+0x34`) | thiscall | disasm+xref |
| `0x004CD580` | `CSWSObject::UpdateDialog` (per-tick; calls ClearAllPlayerHostileActions) | thiscall | disasm+xref |
| `0x004CB480` | `CSWSObject::ReplyDialog` (ret 0x14) → HandleReply | thiscall | disasm |
| `0x004CB220` | `CSWSObject::RunDialogOneLiner` | thiscall | DB |
| `0x004EF910` | `CSWSObject::OnEndDialog` | thiscall | DB |
| `0x005A0940` | `CSWSDialog::ClearAllPlayerHostileActions` (known) | (stdcall/ecx) | disasm |
| `0x005A0E80` | `CSWSDialog::Constructor` | thiscall | xref |
| `0x005A2AE0` | `CSWSDialog::LoadDialog` | thiscall | disasm |
| `0x005A03C0` | `CSWSDialog::HandleReply` (this = `obj+0x34`) | thiscall | disasm |
| `0x005A0A40` | `CSWSDialog::RunEndConversationScript` | thiscall | disasm |
| `0x005A27B0` | `CSWSDialog::Cleanup` | thiscall | DB |
| `0x005A3640` | `CSWSDialog::GetStartEntry` | thiscall | DB |
| `0x005A4010` / `0x005A13D0` | `CSWSDialog::SendDialogEntry` / `SendDialogEntryNode` | thiscall | DB/disasm |
| `0x005A3820` | `CSWSDialog::SendDialogReplies` | thiscall | DB |
| `0x0059F320` | `CSWSDialog::GetExoStringFromLocString` | stdcall | DB |
| `0x0059E9B0` | `CSWSDialog::ClearDialogOwnerInObject` | thiscall | DB |
| `0x004F8290` | `CSWSCreature::GetDialogResref` (← `stats+0x44`) | thiscall | disasm |
| `0x0052E9F0` | `ExecuteCommandBeginConversation` (only caller of StartDialog) | thiscall | disasm |
| `0x0052D5B0` | `ExecuteCommandActionStartConversation` | thiscall | DB |
| `0x0053EF70` / `0x0053EEC0` | `GetIsConversationActive` / `GetIsInConversation` | thiscall | disasm |
| `0x0053F010` | `ExecuteCommandGetLastConversation` | thiscall | DB |
| `0x004AEFE0` / `0x004AEFD0` | `CServerExoApp::Get/SetLastConversation` (`+0x1B908`) | thiscall | disasm |
| `0x004AF020` / `0x004AF010` | `CServerExoApp::Get/SetActionConversation` (`+0x1B910`) | thiscall | disasm |
| `0x005ED690` | `CClientExoApp::GetInGameGui` (`*(*(this+4)+0x40)`) | thiscall | disasm |
| `0x0062DBD0` | `CGuiInGame::IsInDialog` (`gui+0x170/+0x174`) | thiscall | disasm |
| `0x006339C0` | `CGuiInGame::UpdateDialog` | thiscall | DB |
| `0x0062ADA0` | `CGuiInGame::SetDialogSelection` | thiscall | disasm |
| `0x006332B0` | `CGuiInGame::CloseDialog` | thiscall | DB |
| `0x00631D80` / `0x00634340` | `CGuiInGame::HandleDialogEntry` / `HandleDialogReplies` | thiscall | disasm/DB |
| `0x0062B730` / `0x0062B130` | `CGuiInGame::ShowDialogEntry` / `ShowDialogMessage` | thiscall | DB |
| `0x006A7110` | `CSWGuiDialog::SelectReply` (index) — **the drive call** | thiscall(1 int) | disasm |
| `0x006A7390` | `CSWGuiDialog::HandleSelectReply` (`panel+0x1C8A`) | thiscall | disasm |
| `0x006A6D80` | `CSWGuiDialog::GetReplyIndex` (`panel+i*4+0x1970`) | thiscall | disasm |
| `0x006A86A0` / `0x006A7010` | `CSWGuiDialog::SetReplies` / `SetDialogMessage` | thiscall | DB |
| `0x006A6D70` | `CSWGuiDialog::EndDialog` | thiscall | DB |
| `0x00677EA0` | `CSWCMessage::SendPlayerToServerInput_Dialog` | thiscall | disasm |
| `0x0060F620` | `CSWCCreature::ActionInitiateDialog` | thiscall | DB |
| `0x005EDC80`/`0x005F3030` | `Set CanSendDialog` (`clientInternal+0x228`) | thiscall | disasm |
| `0x005EDC90`/`0x005F3070` | `Get CanSendDialog` | thiscall | disasm |

Key fields: `CSWSObject+0x34` active `CSWSDialog*` · `+0x54` `dialog_owner` id ·
`CSWSDialog` `+0x08` entries / `+0x10` replies / `+0x80` owner / `+0x84` speakers ·
node `+0x00` speaker(CExoString) / `+0x08` text(CExoLocString→StrRef) ·
`CGuiInGame+0xB4` conv-active / `+0x170`,`+0x174` participants ·
`CSWGuiDialog` (vtable `0x007559E0`) `+0x6C` reply count / `+0x1970[]` reply node ids /
`+0x1C8A` selected slot · `CClientExoAppInternal+0x9C == 3` dialog input mode ·
`CExoInput` global `0x007A39E4` · GUI manager `0x007A39F4`.


---

## ⚠ A JOINER CANNOT START A CONVERSATION AT ALL (found 2026-08-07, OPEN)

Measured on a live two-instance session, and it invalidates two earlier claims in this
repo's own notes — read this before trusting anything about spectating.

**The symptom.** Every spectate follow ends in
`dialogue: the peer's conversation never opened locally; backing out` after the 15 s budget.
`Odyssey.StartDialogueWith` returns true (the request is sent), and no conversation ever
becomes active on the client.

**It is not the spectate logic.** Asking the client DIRECTLY to talk, via the
`blockpoke talk <tag>` test hook with no follow involved, fails identically.

**Eliminated by measurement, in this order:**

| Suspect | Result |
| --- | --- |
| `DialogueGateHook` refusing the start | No `blocked a local conversation start` line ever logged |
| A peer holding the co-op token | Closed the host's conversation, `remote: active 0`, still fails |
| Talking to a PARTY MEMBER while solo mode is held | A non-party NPC (`end_bridgerep1`) fails too |
| `CanSendDialog` | `CClientExoAppInternal + 0x228` reads **1** on BOTH instances |
| Range / walking into position | Client PC 0.9 units from its own Trask |
| A stale `gui+0xB4` conversation-active flag | Cleared it; still fails |

The HOST does the identical thing, to the same NPC, successfully. The only structural
differences left on the joiner are solo mode (`partyTable+0x190 = 1`), the control lock, and
disabled encounters — none of which obviously reach conversation start.

⚠ **Two claims elsewhere that this corrects:**

1. `toolserify.ps1`'s check *"the peer follows the same conversation"* does **not** test
   following. It asserts `RemoteDialogueActive` — that the client RECEIVED the token — and
   nothing about whether the client opened anything. It has been passing throughout.
2. The 22-round open/close hammer run recorded in Handoff §0i proves the CLOSE path does not
   crash, and nothing more. Its 6-second window between open and close is shorter than the
   15-second back-out timeout, so it could never have printed the failure. Do not cite it as
   evidence that spectating worked.

**Next step is instrumentation, not inspection**: detour `CSWSObject::StartDialog 0x004CF490`
on the client and log whether it is reached at all, then trace the dialogue branch of
`CSWSMessage::HandlePlayerToServerInputMessage`. If StartDialog is never reached, the message
is being dropped before it; if it is reached and returns 0, one of its own gates is refusing
and the disassembly will name which.


---

## ⚠ CORRECTION (same day): the joiner CAN start conversations — they THRASH

The section above concluded "a joiner cannot start a conversation at all". That was wrong,
and the instrumentation that proves it is worth keeping.

`DialogueGateHook` already detours `CSWSObject::StartDialog 0x004CF490`, so a counter there
(`StartAttempts`, surfaced from the frame hook because the detour must not log) answers the
question directly. On the joiner:

```
18:12:45.833  dialogue: following the peer's conversation
18:12:45.837  dialogue: engine reached StartDialog (11 so far)     <- 4 ms later
18:12:47.429  dialogue: the owner ended the conversation; closing  <- 1.6 s later
```

So the whole chain works on the joiner — input message, action queue, `RunActions`,
`AIActionDialogObject`, `BeginConversation`, `StartDialog`. Nothing is dropped upstream.

**The real defect is that conversations do not STAY open.** Watched over 35 s with the host
holding one conversation, the published dialogue serial climbs continuously on BOTH instances
with an empty speaker — host 6, 8, 10, 13, 15, 18, 20; client 7, 9, 12, 14, 17, 19, 22 — which
is conversations opening and closing over and over, not one conversation running.

Two suspects, neither yet proven:

1. **`Odyssey.CloseDialogue` runs `CSWSObject::StopDialog`, which executes the
   END-CONVERSATION SCRIPT.** That was the right call for correctness (it is what the engine
   runs at a natural end) but the Endar Spire's Trask is scripted to re-initiate conversation
   during the tutorial, so ending it can immediately start it again — a loop the spectate
   controller then follows and closes, forever.
2. **`FollowContentTimeoutMs = 3000`** closes a followed conversation that has OPENED but not
   yet delivered a line. That matches the play report exactly: *"the dialogue shows for both
   but the subtitles and dialog options don't show for a while"*. If the content legitimately
   takes longer than three seconds, the controller is killing conversations that were working.

⚠ Both of these were introduced by fixes that were themselves correct in isolation. The next
pass should measure how long content actually takes to arrive before touching either number,
and should test somewhere OTHER than the tutorial deck, whose Trask has scripted conversation
behaviour that no other module shares.

## RESOLVED: the thrash, and what it actually was

Both suspects above were wrong about the mechanism, and the measurement the note asked for
settled it in one run. `FollowContentTimeoutMs` was never the trigger — instrumented, a
followed conversation delivers content **0 ms after opening, 15 ms after the request**, when
the two players are near each other. The number was not too small; it was being measured on
the wrong clock.

Five defects, four of them mine, all fixed and live-verified:

| Defect | Effect |
|---|---|
| `PublishLocal` had no flicker debounce | The engine's conversation-active flag drops for a few frames BETWEEN ENTRIES. The agent has always debounced it (`CoopAgent.DialogueLingerMs`); the plugin did not, so every ordinary line change cleared the partner tag, wiped the whole pick log and bumped the serial twice. **This is the serial climb** the previous note read as open/close churn. |
| The content timeout ran from the REQUEST | `waited = now - _followingSinceMs`. The open takes seconds (the server walks the player into range first), so it spent the content budget: a conversation that opened at 2.8 s got 200 ms to deliver a line before its own watchdog tore it down. Now measured from `_sawOpenAtMs`. |
| A single timeout poisoned the whole conversation | Give-up set `_openAttempts` to the cap, and the cap is only cleared when the peer's conversation ENDS. One failure meant the spectator ignored that conversation for as long as it ran while the gate kept refusing the player's own clicks — *"we just see 'another person started the conversation'"*. Now a backoff, not a poison. |
| The yield close ran EVERY FRAME | `CloseDialogue` routes to `StopDialog`, which executes the end-conversation script. Per-frame, against an NPC scripted to re-initiate, that is a genuine open/close loop. Now rate-limited. |
| The gate bypass was a TIME WINDOW | Held up from the follow request until the conversation appeared — a second or more — during which ANY conversation start passed. A click on another NPC during setup sailed through, and the engine, which will not run two conversations, dropped the one being followed. Now a counted grant: one request's worth (`DialogueGateHook.BypassGrants`). |

Plus one improvement rather than a fix: when both players stand in the same trigger the
engine starts the SAME conversation on both independently, and the loser's copy is exactly the
copy a follow would have opened. It is now **adopted** instead of being closed and reopened —
measured at ~880 ms with content immediately, against a teardown-and-reopen that ran the end
script and left the player watching an empty box.

Verified live (both on the Endar Spire, which is the WORST case for this — Trask's end script
re-initiates where no other module's does):

* one conversation held 40 s, serials flat (host +0, client +1) against host +14 / client +16
  before;
* simultaneous start on both instances: adopted in ~880 ms, serials flat at 5 for 25 s;
* the full battery, 22/22.

### `CGuiInGame + 0xB4` is a flag, not an object id

`GetIsConversationActive 0x0053EF70` reads `+0xB4` and passes it to an object lookup
(0x0053EF83), which reads like an id. **It is not.** Sampled across a whole conversation it
only ever holds 0 or 1, and the engine's own teardown clears it — a host that ended a
conversation properly reads `0xB0=0, 0xB4=0` on the next sample.

Tried and reverted, so nobody repeats it: resolving `+0xB4` as an id. It only "works" because
object id 1 happens to exist, and asking the wrong table (the `0x007A39FC` client chain rather
than `0x007A3A00`) made **every conversation in the game read as inactive**.

### RESOLVED: the wedge is gone, and the reason is the counted bypass grant

The wedge — `gui+0xB4` non-zero, `gui+0xB0` zero, both participants `OBJECT_INVALID`, and the
instance believing forever that it is in a conversation — no longer reproduces. Two runs of
its own recipe (host holds a conversation; the client is asked through the test hook, which
bypasses the gate deliberately, to talk to an unreachable NPC — `carth`, then
`sith_commander`) leave the client at `0xB4 = 0` throughout, and the full battery leaves both
instances clean.

Absence is not a fix, so here is the mechanism. The wedge needed a **half-started conversation
to persist on the client**: its own `StartDialog` reaching the engine while a peer held the
token, producing a client-side shell with no participants that nothing could then close. Two
of the changes above remove both halves of that:

* `DialogueGateHook.BypassGrants` is **counted**, so the only start that passes while a peer
  holds the token is the follow-open we asked for. The stray request that used to create the
  shell is now refused, and the engine's own failure path runs — which is what clears the
  pending conversation action (0x004F34A0).
* The follow **backs out and retries** instead of poisoning its budget, so a request that does
  not become a conversation is dropped cleanly rather than left half-alive. The log shows
  exactly this, repeatedly, with `0xB4` never leaving 0:

```
05:04:30  dialogue: following the peer's conversation
05:04:37  test: opened a conversation with 'carth'
05:04:45  dialogue: the peer's conversation never opened locally; backing out
05:04:50  dialogue: following the peer's conversation        <- retries, does not wedge
```

### The writer of `gui+0xB4`, and the watch that found it

`DialogStateWatchHook` (opt-in, `coop.ini dialog_state_watch`) detours
`CGuiInGame::SetGlobalDialogState 0x0062EC60` — the **one** writer of `+0xB4`, found with
`tools/fieldwrites.py` — and records the RETURN ADDRESS and value of each distinct call. It
observes only; the engine's own store always runs.

It is kept rather than deleted because the question it answers is not fully closed: the
hypothesis that a stale flag is self-sustaining (`SendPlayerToServerInput_Dialog 0x00677EA0`
is gated on `*(gui+0xB4)` at 0x00677EB3, and 0x0060F764 sets the flag immediately after
calling it) was never tested against a live wedge, because there is no longer one to test it
against. If the state ever returns, turn the watch on and the call site names itself.

⚠ One reading it has already corrected: `gui+0x108` is `initialized`, not a dialog object.
`CloseDialog`'s early return at 0x006332B7 is "the GUI is not up", not "there is no dialog".

### Still imperfect: a follow can time out when the peer is far

Visible in the same log — `the peer's conversation never opened locally; backing out`, three
times in a row, when the client's PC had been left across the room by the combat checks.
`FollowOpenTimeoutMs` is 15 s and the server walks the player the whole way first. The retry
covers it (that is what the backoff is for) but a spectator far from the speaker can spend
several rounds of it before catching up.

---

## Node sync: replaying the owner's POSITION, not their pick (2026-08-11, live-verified)

Slot replay is gone. Everything below was measured against two live instances, not inferred.

### Why reply-slot replay could never converge

A reply slot is not a property of the conversation. It is a property of **one instance's
evaluation** of the conversation, and three separate decisions inside a dialogue are taken
independently on each machine by scripts reading that machine's own world state:

* **Where it starts.** `CSWSDialog::GetStartEntry 0x005A3640` takes the first starting entry
  whose `Active` script passes. `end_trask01.dlg` has 19 starting entries, 18 conditional.
* **Which replies are offered.** The panel's slots are *compacted* over the links that passed
  locally (the real link index lives at `panel+0x1970[slot]`), so one differently-filtered
  link shifts every later slot.
* **Where a reply goes.** Its outgoing link is first-that-passes as well.

**Measured**, and this is the finding that settles it: with both instances on the same save in
the same module, the host's conversation started on entry **29** and the client's on entry
**19** — *before any pick was made at all*. No amount of correcting the pick log could have
fixed that, because no pick was involved.

### The current entry index is `CSWSDialog+0x70`, not `+0x60`

`+0x60` was a candidate picked out of a "+0x60..+0x74 band" without a proof, and it is the
wrong dword. `+0x70` is settled twice over, both times by the engine using it as an index:

* `CSWSDialog::SendDialogEntry` loads it at 0x005A4045 and indexes the entry array —
  `imul eax, eax, 0xB0` against `[esi+0x08]`, and 0xB0 is exactly `sizeof(CSWSDialogEntry)`
  with `+0x08` exactly the `entries` pointer;
* `CSWSDialog::HandleReply` compares the caller's claimed entry index against it at
  0x005A0403 and refuses the reply when they differ — the engine's **own desync check**, on
  this field.

Live: a host mid-conversation read `node 29/74`, stable across a 30 s sample.

### The owner object is the one that names itself

`CSWSObject::UpdateDialog 0x004CD583` refuses to run unless `[obj+0x54] == [obj+0x04]` — the
dialog-owner id equals the object's own id. That equality **is** the definition of the object
a conversation hangs off; everything else is a participant.

### ⚠ Drive through `CSWSDialog::SendDialogEntry 0x005A4010`, NOT the CSWSObject wrapper

This is the difference between a follower that moves and one that does not, and it cost two
live runs to find.

`CSWSObject::SendDialogEntry 0x004CD460` consults `CSWSObject::IsDialogDelay 0x004CD2D0`
first and, when that says wait, **queues** the entry (`+0x4C`, flag `+0x48`) and returns
**success** having sent nothing. Measured on a live follower:

```
dialogue: the engine has deferred 1200 attempts to move from 19 to 29; still trying —
  owner 0x127 node 19/74 pending 1:29 sendGate 0 isDialogDelay 1 delay 0:37591 last 0:17591
```

`pending 1:29` — the request was sitting in the queue. The queue is only ever flushed by
`UpdateDialog`, i.e. by the local pacing a follower is not supposed to be running.

That gate is not wrong, it is simply **not ours**: it exists to pace an *owner's* conversation
against its own audio. A follower's pacing authority is the other player. So the inner
producer is called directly — `__thiscall(dialog)(CSWSObject* speaker, int, int entryIndex,
int* status)`, `ret 0x10` (epilogues 0x005A414A / 0x005A41B3 / 0x005A41F7 / 0x005A4259). It
bounds-checks the entry index itself at 0x005A41FD, and it is the same function the deferral
would eventually have called.

It is also the single call site of `CGuiInGame::HandleDialogSound` (the voice-over) and of the
lipsync arming that follows — so driving the node brings the line, its audio and its mouth
together. "Subtitles stop, lips not synced, audio not synced" is **one** defect, not three.

⚠ **A successful call is not an arrival.** Banking the serial on a reported success stranded
the follower for the rest of the conversation. The node itself is the acknowledgement: nothing
is recorded until a re-read shows the engine actually moved.

### What was measured, end to end

`tools/dlgprobe.ps1` now samples both instances' node columns, makes the client click to join,
and makes the host answer:

```
  t   host#  client#  remoteActive  hostNode  clientNode  wantNode
  4       1        0             1        29          -1        29
  -> client clicks 'end_trask' (join)
  5       1        2             1        29          29        29
  -> host answers (key '2')
 11       2        3             1        19          19        19

dialogue: followed the owner 19 -> 29     (join: the client's own start node was wrong)
dialogue: followed the owner 29 -> 19     (the host picked a reply; the client tracked it)
NODES: on the same entry for 15 of 15 paired samples.
```

### Two state-machine defects fixed alongside

* **`Suppressed` was asserted below the `!open` early return**, so the entire join window —
  up to `FollowOpenTimeoutMs`, 15 s — ran unsuppressed and *both players could answer*. It is
  asserted as soon as the follow is live now. This was never a hole in the reply hook.
* **The gate was held shut on an EMPTY peer partner tag.** `SameTag` refuses an empty tag by
  design, so while the peer's tag was unresolved every local start was refused — including the
  join the player was trying to make (measured in play as a click at :23 doing nothing and the
  identical click at :26 working). The tag is empty for a real window at the start of every
  conversation, because `gui+0x170/0x174` stay `OBJECT_INVALID` until the server resolves
  them. The gate now only closes once the peer's identity is known; a competitor that slips
  through is still yielded by the adopt path.

---

## The spectate controller is an explicit state machine now (2026-08-11)

A follow-up audit of `DialogueSpectateController` found thirteen state defects. Two were
fixed in place (the section above). **Seven of the remaining eleven were the same shape** — a
flag whose CLEAR sat below an early return that a later pass had added above it — so the
structure changed rather than the instances. Patching them one at a time only waits for the
next early return.

### The defect list, as re-derived

| # | Where | Defect |
|---|---|---|
| 1 | `PluginHost.InvalidateAll` | ⚠ **`_dialogueSpectate.Invalidate()` IS NEVER CALLED.** Some thirty controllers are reset on a module change and this is not one of them, so the whole "a node index names an entry in ONE .dlg" teardown was dead code. **Still open — PluginHost is owned elsewhere.** Mitigated: the controller now watches `Odyssey.TryGetWorldEpoch` itself and self-invalidates. |
| 2 | `PluginHost` frame fn | The frame function `return`s above `_dialogueSpectate?.OnFrame()` three ways — the area-transition flush, a travel warp, and the whole of a no-world-epoch window (a loading screen). `DialogueReplyHook.Suppressed` and `DialogueGateHook.PeerHoldsConversation` were per-frame LEVELS with no writer during those, so they held their last value. A latched `Suppressed` is the "cannot answer any conversation, ever" report. **Fixed** by making both LEASES. |
| 3 | `Invalidate` | Set `_hadDialogue = false`, which is the guard on the branch that clears the block's speaker, line and partner tag — so a module change left a stale partner tag published for the rest of the session, and that tag is what the far side's join gate compares against. **Fixed**: the block is cleared directly. |
| 4 | give-up path | Reset `_following/_sawOpen/_reportedContentMs` but not `_appliedNodeSerial` or `_driveAttempts`. A follow that began while the owner's serial was still the banked one believed it had already been driven there and stood still. **Fixed**: node state is reset on every frame the machine is not following. |
| 5 | give-up path | No cooldown. Give-up closes the conversation, and the adopt path finds the very same conversation still on screen with the very same matching tag on the NEXT frame and takes it straight back — an open/close cycle whose period is the content timeout and whose every lap runs the END SCRIPT. `ReopenCooldownMs = 4000` existed but nothing used it. **Fixed**: `BackingOff`. |
| 6 | `_localUnidentifiedSinceMs` | `0` used as "not started", which is a value `Environment.TickCount64` genuinely takes — and a sentinel the clock can produce re-stamps itself every frame, so the grace never expires. **Fixed**: nullable. |
| 7 | yield path | The tag comparison refuses an EMPTY tag by design, so a peer whose participants had not resolved read as "not our partner" and every local conversation looked like a competitor — including the copy of the peer's own conversation the engine hands both players in the same trigger, which is exactly the copy the adopt path exists to keep. This is the mirror image of defect 2 in the section above. **Fixed**: nothing is yielded until the peer has named its partner. |
| 8 | adopt path | Banked `_sawOpen = true` unconditionally, but `_hadDialogue` is debounced across the between-entries flicker — so an adopt could start the 8 s content clock on a conversation that was not on screen. **Fixed**: the adopt banks an open only if the engine flag is up; otherwise the long open budget applies until it is. |
| 9 | `BypassGrants` / `_followOpenPendingUntilMs` | Dead. Nothing has raised a grant since the self-open was removed, yet three code paths cleared the counter and a whole expiry branch ran against a deadline nothing ever set. **Fixed**: the controller no longer touches either; the hook keeps the mechanism, documented as ungranted. |
| 10 | `ReopenCooldownMs` | Declared, never read. Now the stand-off in defect 5. |
| 11 | `LogFew` | `_logged < 60` was a LIFETIME cap: a session that had seen sixty dialogue events went permanently silent, including for the next genuinely new state. Same failure the file's own comment argues against for repeat suppression, one level up. **Fixed**: the budget refills on a 120 s window. |
| 12 | suppression scope | `Suppressed` covered only `_following`. `Yielding` (a conversation we have decided to close, whose plot flags race the owner's) and `BackingOff` (a matching conversation still on screen) were both answerable. **Fixed**: suppression is now `state != Idle`, i.e. "a peer holds the token". |
| 13 | (unchanged, noted) | A follow whose own conversation closes locally while the peer keeps the token stays `Following` until the peer releases it. This is deliberate — `DialogueFollowClock` cannot tell that from the between-entries flicker without a number nobody has measured — and it is bounded by the owner's conversation. Left alone. |

### The states

```
Idle        no peer holds the token. Nothing suppressed, nothing gated.
Standby     a peer holds it; we are not committed. Suppressed. Unsticks a stale shell.
            Also where an unidentified local conversation waits out its grace period,
            and where a competing one waits for the peer to name itself.
Yielding    a peer holds it and we hold a DIFFERENT one. Rate-limited close, then force.
Adopting    one frame: our conversation IS the peer's. Starts both clocks.
Opening     following, not yet on screen. The 15 s budget.
Following   following, on screen. Drives the owner's node. The 8 s content budget.
BackingOff  a follow gave up. Stand off ReopenCooldownMs before adopting again.
```

Transitions: `!PeerActive` → `Idle` from anywhere (closing once, if we were following).
`Standby` → `Adopting` when the local partner is the peer's, → `Yielding` when it is a
different, named NPC and the peer has named theirs. `Adopting` → `Opening`/`Following`.
`Following` → `BackingOff` on either deadline. `BackingOff` → `Standby` when the stand-off
expires. `Yielding` → `Standby` when our conversation has gone.

### What moved into testable form

`src/KotorCoop.Protocol/DialogueFollowMachine.cs` (states, inputs, actions, the transition
table) and `src/KotorCoop.Protocol/DialogueNodeFollower.cs` (which of the owner's positions
we have been driven to, and the deferral count). Both are compiled into the plugin BY SOURCE
via `KotorCoop.Plugin.csproj`, the same route `DialogueFollowClock` already took — the plugin
is a NativeAOT win-x86 library and nothing may reference it, so anything that must be covered
has to live in `KotorCoop.Protocol`. 41 new tests in
`tests/KotorCoop.Tests/DialogueFollowMachineTests.cs` and `DialogueNodeFollowerTests.cs`.

What is left in `DialogueSpectateController` is engine reads, engine writes and shared-block
plumbing. If a line in it decides something, it is in the wrong file.

### The two levels are leases, not flags

`DialogueReplyHook.SuppressReplies()` / `AllowReplies()` and `DialogueGateHook.HoldForPeer()` /
`ReleaseForPeer()` stamp a deadline (2000 ms) that the controller renews every frame. Both are
`volatile int` ticks compared with a wrap-safe unchecked subtraction, so the detours — which
run on the engine's own thread mid-dispatch — can never read a torn deadline. Both fail OPEN,
which is the direction the rest of both hooks already fails in.

This is what defect 2 costs if it is not done: the flag is only correct while the frame pass
runs, and the frame pass does not always run.

## The second-conversation client crash (2026-08-21 investigation, root cause NOT confirmed live)

Two real access-violation crashes were observed on the client instance during a 2026-08-15
session (Windows Application event log + `kotorcoop-plugin.log`), both with an identical
signature: a session's SECOND local `StartDialog` is fatal, its FIRST always ran fine.

    [13:44:41.890] dialogue: engine reached StartDialog (2 so far)
    [13:44:44.040] FIRST-CHANCE access violation (0xC0000005)      <- 2.1s later, plugin hook region
    [20:56:04.985] dialogue: engine reached StartDialog (2 so far)
    [20:56:05.006] FIRST-CHANCE access violation (0xC0000005)      <- 21ms later, INSIDE swkotor.exe

The original hypothesis (from the log alone) was stale per-session dialogue state carried by
`DialogueGateHook`/`DialogueSpectateController` across two conversations in the same module.
**That hypothesis does not survive reading the current code.** `DialogueSpectateController`
already resets `LocalDialogueNode`, the pick log and `LocalDialoguePartnerTag` at the end of
EVERY conversation (`PublishLocal`'s `!active` branch, not only `Invalidate()`'s module-change
path) — this reset already existed before this investigation and is correct. Whatever crashes
these two sessions, it is not this class of staleness.

### The crash #2 address, decoded

`0x589112` (event-log fault offset `0x189112` + image base `0x400000`) disassembles to:

    0x005890F0  movzx eax, byte ptr [ecx + 0x2cc]
    0x005890F7  mov   eax, dword ptr [ecx + eax*4 + 0x32c]   ; per-object-type array lookup
    0x005890FE  test  eax, eax
    0x00589100  jne   0x589105                               ; null IS guarded
    0x00589102  ret   0x18
    0x00589105  push  ebx
    0x00589106  add   eax, 0x20
    ...
    0x00589110  mov   esi, dword ptr [edx]        ; edx = ecx+0x90 (source)
    0x00589112  mov   dword ptr [eax], esi         ; <- THE FAULT: write through eax

The null check at `0x5890FE` already guards against an empty slot. The crash is therefore a
write through a **non-null but stale/dangling** pointer sitting in the `ecx+0x32c` array — the
same general shape `ShotCameraGuardHook.cs` (this file's own §"Stops CGuiInGame::UpdateShotCamera")
already documents and fixes for a *different* address (`0x006306E0`, `gui+0x170`/`gui+0x174`):
"an id the client no longer holds is an access violation... a peer's conversation ending, a
follow timing out, or losing the token race all end a conversation from OUR frame hook, and one
stray reply list arriving afterwards is enough." This is very plausibly the same bug CLASS, one
call site over, never previously found because nobody had the crashing address to start from.

`0x5890F0` is entered from `0x004BBE6E`, itself reached (per the surrounding code at `0x4BBDE0`
region) via `mov eax, [0x7a39fc]` — the SAME app-manager global chain this document's own
headline table already verifies for the dialogue subsystem — then `CClientExoApp::GetGameObject`
(`0x4ae750`, null-checked correctly), then a branch that only proceeds for the resolved object's
type byte (`ecx+8`) equal to 9 or 10; type 10 additionally casts through `vtbl+0x18` first (the
same door-cast slot `AddAttackAction`'s disassembly elsewhere in this session identified). This
is consistent with per-conversation camera/shot positioning data tied to a door or similar
type-10 object referenced by the conversation, going stale exactly the way `ShotCameraGuardHook`
describes.

### Why this is NOT fixed here, and what the real fix needs

The pointer at fault is non-null, so a `ShotCameraGuardHook`-style bare null-check guard at the
call site would not catch it — the value looks valid and only turns out to reference freed
memory. Applying this codebase's own ghost-object liveness gate (`CGameObjectArray`-based,
already proven for exactly this "freed block still reads as alive" shape in the enemy-census
fix) would need confirming the `ecx+0x32c[index]` slot actually holds a `CGameObject`-family
pointer and not something else (a camera-cue cache entry, a GUI element, unconfirmed either way)
— getting this wrong and hooking blind risks a NEW crash on a native patch nobody has verified
live, which this whole codebase's own established discipline (see every `Install()` in this
directory: verify-bytes-or-refuse, nothing shipped without a live pass) argues strongly against.

Reproducing this live needs two real, sequential in-game conversations against the SAME client
instance. A prior pass here believed no console verb could trigger `StartDialog` synthetically —
that was wrong: `TestDriveController.HandleTalk` (`src/KotorCoop.Plugin/TestDriveController.cs`)
already exists for exactly this, driven through the shared block via
`pwsh tools\blockpoke.ps1 -ProcId <pid> talk <tag>` (and `talk close` to end one), sending "the
same player-to-server dialogue request a click produces" with no mouse injection needed at all.

### RESOLVED (2026-08-21, live-confirmed): does not reproduce on current HEAD

Reproduced live on `D:\Games\swkotor-gog-p2` (a single instance, no co-op session needed — both
original crashes were purely client-local with the host idle): loaded save 1 into `END_M01AA`,
then `talk end_trask` → `StartDialog (1 so far)` → `talk close` → `talk end_trask` →
`StartDialog (2 so far)` — the exact fatal step in both 2026-08-15 crashes. The process survived,
stayed `Responding: True`, and kept surviving through TWO more rapid-fire close/reopen cycles
(one with only ~1s between close and the next open, tighter than either original crash's
2.1s/21ms window), reaching `StartDialog (4 so far)` with no fault. `kotorcoop-plugin.log` shows
zero `0xC0000005` access violations anywhere after the test began — the only matches in the whole
file are the two original historical crash lines from 2026-08-15 and the pre-existing, harmless
`0x406D1388` thread-naming marker (documented elsewhere as expected and inert).

**This closes the open question: the crash does not reproduce on current HEAD.** The decoded fault
mechanism above (a stale-but-non-null pointer at `ecx+0x32c[index]`, same class as
`ShotCameraGuardHook`'s fix) is left in this document as-is — it's still the best available
explanation for what the two 2026-08-15 crashes were, and worth keeping as a reference if a
similar symptom ever resurfaces — but nothing needed shipping today, since extensive intervening
dialogue-subsystem hardening this session (`DialogueSpectateController`'s per-conversation-end
reset, among other changes) already closed whatever window let it happen. No source changed for
this; a guess-patch against a non-reproducing crash would only have added unverified risk.

## Live A/B and rough-edge pass (2026-08-21) — see `dialogue-join-latency.md` for the full writeup

Ran the `dialogue_fast_join` A/B (this doc's own hop #3/#6 analysis is what that flag closes)
and the rough edges below live, on the two-GOG-install rig, `END_M01AA`, both players adjacent
to `end_trask`. Full numbers, screenshots and method in `dialogue-join-latency.md`'s "Live A/B
(2026-08-21)" section; summary here for anyone scanning this file's own NEEDS-LIVE trail:

* **Content delivery, self-reported (`ReportContentLatency`)**: 0 ms after opening, both
  `dialogue_fast_join=0` and `=1`, 10/10 runs. The user-reported symptom (blank subtitles under
  a slowly-closing letterbox) did not reproduce in this configuration under either setting —
  because every join in this rig resolves through **adopt**, not a distance **follow**, and
  adopt was already fast before this session (see this file's own "adopted in ~880 ms" note
  above). The flag's target path (a joiner not yet standing at the NPC) was not exercised.
  `dialogue_fast_join` default was **left at 0** — no regression found, but no measurable win
  found either, for the reason above; not a design defect, a live-testing gap.
* **Both players clicking the same NPC simultaneously** (this file's existing "simultaneous
  start" note, re-run): still clean. No crash, no stuck state, the losing side's `StartDialog`
  adopted the winner's exact current line (content latency 0 ms), confirmed both directions
  (tie went either way across two attempts).
* **The joiner leaving mid-conversation via Esc**: no observable effect on either instance —
  reads as vanilla KOTOR1 behaviour (no Esc-to-leave affordance in dialogue on this engine
  build), not a gap this mod needs to close. Leaving via the real give-up path (`talk close`,
  i.e. `Odyssey.CloseDialogue`) does work as expected: the owner's conversation is unaffected,
  and the joiner can immediately re-open and be readmitted as a follower.
* **Owner ends the conversation → joiner UI closes**: qualitatively prompt (the joiner's own
  "the owner ended the conversation; closing" log line appeared within a few seconds of every
  close request), but this session's PowerShell-subprocess-driven harness could not isolate true
  propagation time from its own ~1–3 s of per-call start-up overhead — still NEEDS-LIVE for a
  millisecond figure, with a different triggering method than blockpoke.
* **A trigger/cutscene-started conversation (END_M01AA's opening Trask scene)**: **not
  independently re-verified live** — the loaded save is already past that scripted opening on
  both installs, and re-triggering it needs an earlier save this session did not use. Still
  NEEDS-LIVE. `BeginConversation → StartDialog` is the same entry point regardless of trigger
  vs. click (this file's own §1, verified by disasm), so no separate risk is expected, but that
  is an inference from the code, not an observation.

## The FAR join, and the fix — see `dialogue-join-latency.md`'s "Far-join live pass" (2026-08-21)

The gap the two passes above left open — a joiner who is not already standing near the NPC —
reproduces, live-verified, and it is a hard failure rather than a slow one: `CSWSObject::StartDialog`
is never reached at all (the engine's own conversation-active flag lights up immediately from the
request, which is the letterbox-eligible state the user's report calls "the bars creep in", but
no content ever follows), because `CSWSObject::AIActionDialogObject 0x0057A470`'s own range gate
(disassembled and VERIFIED LIVE at a flat **10.0-unit** threshold, `[0x747450]`) never resolves
for a player character that is drive-controlled rather than action-queue-walked in this build.
Worse: the existing last-resort recovery (`ForceCloseUi`) does not stop the resulting stuck
shell either — measured running for over two minutes unattended with zero effect, because it
only closes the client GUI panel and never touches the still-queued action re-arming the flag
every tick.

Both are fixed from already-verified primitives, no new native detour: `dialogue_join_teleport`
(default off) snaps a joiner who is farther than the verified 10-unit range next to the NPC
before the request goes out (`Odyssey.SnapTo`, the same call `CheatController`'s own `tp` uses),
and restores their position once the follow concludes; and the stuck-shell loop is fixed
unconditionally by escalating `DialogueFollowMachine`'s `Yielding` path, past a bounded number of
failed `ForceCloseUi` cycles, to also clear the local player's action queue
(`ForceCloseUiAndClearActions` / `Odyssey.ClearLocalPlayerStuckDialogueAction`) — live-verified
to resolve the loop within one cycle every time it was tried, where the UI-only close never did.
See `dialogue-join-latency.md` for the full disassembly, the before/after numbers in both
ownership directions, and what remains unverified (a genuine mouse-driven click's own walk, and
the no-LOS case).

## Live pass (2026-08-22): the trigger/arrival-started conversation, closed

The 2026-08-21 pass above explicitly left this open ("the loaded save is already past that
scripted opening... still NEEDS-LIVE"). This pass reached it a different way: `warp end_m01ab`
on both instances (`dialogue_join_teleport=1`, `trigger_relay=1`, `liveness_sync=1`, `effect_sync=1`
— the full flag set this session's four-check task asked for), arriving both players together at
the module's own `from_01a` waypoint, which is what `trigger-relay.md`'s 2026-08-21 live pass
already identified as sitting almost exactly on `end_trig_carth02`'s polygon.

**What actually started the conversation was NOT `end_trig_carth02` — it was the module's own
built-in arrival cutscene, and the trigger fired separately, afterward.** Both plugin logs:

```
HOST    [02:27:37.845] dialogue: engine reached StartDialog (1 so far)
CLIENT  [02:27:39.294] dialogue: engine reached StartDialog (1 so far)
CLIENT  [02:27:40.754] dialogue: the engine started the same conversation here; adopting it to follow
CLIENT  [02:27:40.757] dialogue: followed conversation delivered content 0 ms after opening (0 ms after the request)
CLIENT  [02:27:40.757] dialogue: followed the owner 3 -> 6
CLIENT  [02:27:44.546] dialogue: followed the owner 6 -> 7
CLIENT  [02:27:52.865] triggers: standing in 1 generic trigger(s), first 0x26 - asking the enemy owner to run its OnEnter too
HOST    [02:27:53.892] triggers: fired OnEnter of 0x26 for peer 2 (phantom 0x6F)
CLIENT  [02:27:54.358] dialogue: the owner ended the conversation; closing
```

**This is a genuine double-fire** — both engines independently reached `StartDialog` on the same
arrival cutscene, 1.449 s apart, exactly the risk `trigger-relay.md` §3 and §7 item 5 name (a
trigger/cutscene that both players' own real presence can reach natively). **The one-conversation
lock and adopt path caught it cleanly**: rather than running its own independent copy, the client's
engine recognised its own `StartDialog` as the SAME conversation the owner already had open and
switched to following it (`"the engine started the same conversation here; adopting it to
follow"`) — the explicit log line this file's §"adopt" section describes, now confirmed live for
an ARRIVAL-triggered double-start, not just the simultaneous-click case §"Live A/B" above already
covered. **Node agreement: exact.** The follower's own node column tracked the owner's in real
time — 0 ms content latency on adoption, then `3 -> 6` and `6 -> 7` each landing the same pump the
owner's own advance did (no drift, no lag beyond the ordinary per-tick propagation this file's
other passes already measured). **The owner ending the conversation closed the spectator's UI**:
confirmed working end to end (`"the owner ended the conversation; closing"`), but — same gap the
2026-08-21 pass already flagged — a precise millisecond figure remains unmeasured; the log gives
no independent HOST-side "ended" timestamp to diff against (the client infers the close from the
owner's published state going idle, not from a discrete host log line), and this pass did not
build a tighter polling harness to isolate it. **Still NEEDS-LIVE for the ms figure specifically**,
carried forward from the prior pass, now for a second scenario shape.

**`dialogue_join_teleport` correctly did not fire.** No `teleport` line in either plugin log —
both players were already standing together at the shared arrival waypoint, so the far-joiner
snap this flag exists for was never needed. This is a useful negative control: the flag is
conditional on actual distance, not a blanket snap on every join/adopt.

**`end_trig_carth02` itself, once the relay fired it ~16 s later, produced no new observable
dialogue** — the SAME finding `trigger-relay.md`'s own 2026-08-21 pass already recorded for this
exact trigger ("no visible consequence was found... consistent with `k_pend_carth_t02` being one
of the scripts whose own idempotence absorbs a duplicate call for free"). Reproduced, not new.

**Net effect on the 2026-08-21 pass's own open item**: "a trigger/cutscene-started conversation"
is no longer purely inferred from `BeginConversation`/`StartDialog` sharing one entry point — it
has now been observed live, including the double-fire case the static reasoning could only
gesture at. What remains open is narrower: the owner-ends-to-spectator-closes latency in
milliseconds, and a case where the trigger's OWN script (rather than a coincident arrival
cutscene) is unambiguously what starts a NEW conversation nobody's engine would have started
natively.

## Live pass (2026-08-22, round 59): `dialogue_auto_join` — all four documented modes confirmed

`end_trask` (already recruited; a recruitment-class NPC, `dialogue_recruit_sync=0` default
throughout — no recruit/dismiss script exercised, so this is a safety observation about the
auto-join CHANNEL, not a recruitment-sync test). Full context and the `verify.ps1` hardening
shipped alongside this pass are in docs/research/shared-state-audit.md's own "Round 59" section.

**`near` (default), host starts, client auto-joins with zero clicks — PASS.** `dlgprobe.ps1
-NoJoin` (the joiner's own click test-hook never fires): `interaction: auto-joining a peer's
conversation with 'end_trask' (dialogue_auto_join=Near)` on the client, unprompted, `NODES: on
the same entry for 28 of 28 paired samples`.

**`near`, the OTHER direction — PASS, observed organically.** Trask's own known scripted
re-initiate (this file's own "worst case for this" quirk) raced the conversation token during a
close/reopen cycle, and this time the CLIENT held it: `interaction: auto-joining a peer's
conversation with 'end_trask' (dialogue_auto_join=Near)` fired on the HOST — confirming
`TryForceOpen` really does run identically on every peer with no host/client branch, live, not
only via `DialogueAutoJoinRuleTests`.

**`near`, separated >20 m — no pull, confirmed for the distance reason, not a hang.** Worth
naming plainly since it is easy to misread: near-mode's distance check is the LOCAL (deciding)
player's own distance to the NPC, not peer-to-peer distance. `tp`'d the client to 58 units from
Trask; the host reinitiated the same conversation five separate times across 40+ s; zero
`interaction: auto-joining` lines on the client the entire window. The on-screen "walk over to
join" notice itself was not independently screenshotted this pass — confirmed only via log
absence plus the code path (`_farNoticeUntilMs` is set whenever `shouldJoin=false` and the
local player is not already talking/in combat/in a minigame and the NPC's distance resolved,
all of which held here). Moving the client back to 0 units did not rejoin on the very next
close/reopen (Trask's own reinitiate cadence made that one data point noisy — took roughly 14
extra seconds longer than the clean-arrival case above); the FAR case's own 40+ s negative
result stands as the clean evidence, this near-again edge is flagged as unresolved-not-alarming
below.

**`always` — pulls regardless of distance, confirmed with a real coop.ini + restart cycle.**
Client `tp`'d to 58 units from Trask; host `talk end_trask`; client:
`interaction: auto-joining a peer's conversation with 'end_trask' (dialogue_auto_join=Always)`,
content delivered 0 ms after opening, node followed 19→29 — the SAME clean numbers `near` gets
when already in range, just without the distance gate.

**`off` — restores exact click-to-join parity, confirmed with a real coop.ini + restart
cycle.** Host `talk end_trask` with the client standing right next to Trask: client shows
`remote: active 1` (aware a peer holds something) but `local: partner ''` (never pulled in),
zero `auto-joining` lines. The client's own explicit talk request — the identical channel a
real click drives — then opened, adopted and followed exactly as the pre-`dialogue_auto_join`
era's click-to-join always did (`test: opened a conversation` → `the engine started the same
conversation here; adopting it to follow` → `followed the owner 19 -> 29`).

**Recruitment-class safety**: no crash, no roster corruption, across every cycle above.
`verify.ps1`'s own new "the host still controls its companions" check (shared-state-audit.md
round 59, §2) independently confirmed the party roster was intact at session end.

**What remains**: the "moved back into range, took longer than expected" observation above —
likely Trask's own reinitiate noise rather than a real auto-join timing gap, but not isolated
to a single clean repro this pass; and the on-screen far-notice text itself, still confirmed
only by log absence + code path, never screenshotted.

## 2026-08-23, Item 1: stop auto-forcing ORDINARY dialogue joins; auto-join UNCONDITIONALLY only when the ENGINE itself would force it

User's own words: "we don't want to force dialogue joins unless it's a critical/fixed point
where dialogue was auto forced. The dialogue sync should still happen properly." Read together
with the round-59 pass above (which confirmed all three `dialogue_auto_join` modes work exactly
as coded), the gap was never in the mechanism — it was that `near` pulled the other player into
EVERY conversation within range, with no distinction between an ordinary player-clicked NPC
chat and a script-forced/cutscene conversation. This section closes that gap.

### The signal: re-deriving `bIgnoreStartRange` from the existing disassembly, one step further

`docs/research/dialogue-join-latency.md`'s "Far-join live pass" (already in this repo) found
`CSWSObject::AIActionDialogObject 0x0057A470`'s range gate (`[0x747450]`, a flat 10.0-unit
threshold, VERIFIED LIVE) and a bypass flag on the action struct at `[ebx+0x48]`, INFERRED to be
NWScript's `bIgnoreStartRange` by position and effect. This pass re-derived that finding
statically (`tools/disasm.py`/`tools/xrefs.py` against the on-disk GOG exe — read-only, no
process, no rig needed) and traced the data flow one step further, since the earlier pass never
needed to know WHERE the flag lived at function entry:

```
0x0057A4B2  mov ebx, [esp+0xb4]        ; ebx = the action struct — VERIFIED: this is the
                                        ;   function's OWN FIRST STACK ARGUMENT. At entry (esp0
                                        ;   = value right after the call instruction), [esp+0xb4]
                                        ;   works out to [esp0+4] once the two vtable calls and
                                        ;   3 pushes before it are accounted for — traced
                                        ;   instruction-by-instruction, not assumed.
0x0057A4E5  mov edx, [ebx+0x48]        ; VERIFIED: the SAME field dialogue-join-latency.md
0x0057A4F9  mov [esp+0x24], edx        ;   already found
...
0x0057A74A  mov eax, [esp+0x24]        ; VERIFIED (re-derived here): the store above and this
0x0057A750  test eax, eax              ;   read are the SAME stack slot — esp is unchanged
0x0057A756  jne 0x57a9d7               ;   between the two (every intervening call is
                                        ;   thiscall/stdcall with its own cleanup), so this is
                                        ;   the identical flag, not a coincidentally-matching one
```

Two things new in this pass, both **VERIFIED**:

1. **The flag is the function's first stack argument**, present and readable from the moment
   `AIActionDialogObject` is entered — `this` (ECX) is the actor whose action queue is running
   the conversation, and `[esp+4]` at entry is the `ActionStruct*`. This matters because it
   means the flag can be tapped at the WHOLE-FUNCTION PROLOGUE, the same low-risk hook shape
   `DialogueGateHook` already uses for `StartDialog`, rather than needing a mid-function
   trampoline (which dialogue-join-latency.md's own house rules flag as materially riskier).
2. **`CServerExoAppInternal+0x1B910` (`action_conversation`) is a re-entrancy BOOLEAN, not the
   action struct pointer.** dialogue-sync.md's own §4 described it as "the queued/active
   `ActionStartConversation` handle" without pinning its type. This pass disassembled all three
   setter call sites (`xrefs.py 0x004AF010` → `0x004FF65C`, `0x00588258`, `0x0058BF3A`) and every
   one pushes a literal `1` before the call — never a struct pointer. So the ONE place
   `dialogue-sync.md` already documented as durable and reachable from `StartDialog`'s own
   call chain (`BeginConversation`, which calls `GetActionConversation`/`SetActionConversation`
   right before dispatching) carries no flag to read. **This closes off the "read it at
   StartDialog's own site" approach dialogue-sync.md left open as a possibility** — the flag
   must be tapped where it is still alive, at `AIActionDialogObject` itself.

### Implementation: a second, read-only whole-function tap

`KotorCoop.Plugin.DialogueForceFlagHook` (new file) hooks `AIActionDialogObject`'s prologue —
verify-bytes-or-refuse, same discipline as every native patch in this plugin — and records
(object id, forced) into a small fixed table. `DialogueGateHook.OnStartDialog` (already firing
on every `StartDialog` call) looks the SAME object id up and latches
`DialogueGateHook.LastStartForced`; `DialogueSpectateController.PublishLocal` reads that latch
exactly once, at the moment it detects a NEW local conversation (the `_hadDialogue` false->true
edge), and holds it in `block.LocalDialogueForced` for the conversation's whole duration —
mirroring how the partner tag is already held across the engine's own between-entries flicker.

**Why `this` should be the same object at both hook sites (INFERRED, not proven here):**
NWN-heritage engine convention queues `ActionStartConversation` on the SPEAKING object (the
actor whose action executes the "start conversation" step), which is also the object that
becomes `StartDialog`'s own `this` (dialogue-sync.md's headline table: `StartDialog`'s `this` =
"the object that initiates"). A real click queues it on the PC; a scripted ambush typically
queues it on the ambushing NPC via `AssignCommand`. Both cases: whichever object's action queue
runs it is the one `StartDialog` later fires on. **This correlation is not independently proven
by this pass** — it is inference from engine convention, not from tracing a live call. If it is
wrong, the failure mode is a missed match: `WasForced` returns `false` (its default), and the
conversation is treated as ordinary — today's cautious behaviour, never a wrong TRUE.

**One wire field, following the existing pattern exactly**: `DialogueState`/
`DialogueRequestPacket` gain `Forced` (protocol version 38), threaded through
`DialogueArbiter.Apply`, `CoopHost`/`CoopClient`, `IGameBridge.TryReadLocalDialogueFollow`/
`WriteRemoteDialogue`, and two new `SharedBlock` fields (`LocalDialogueForced`/
`RemoteDialogueForced`, version 51, riding the free tail slack after the container-lock-target-
ids region — no `TotalSize` growth). `InteractionLockController.TryForceOpen` reads
`block.RemoteDialogueActive && block.RemoteDialogueForced`, confirms the interaction-lock
entry's tag matches `block.RemoteDialoguePartnerTag` (so a transient mismatch during the token's
own resolve window degrades to "not forced yet" rather than a wrong answer), and if so calls
`DialogueAutoJoinRule.ShouldAutoJoin` with `isForced: true` — which the pure rule answers
UNCONDITIONALLY true, ignoring the configured mode (including `off`) and range, subject only to
three runtime-safety gates that were never a policy choice (already-in-a-conversation, combat,
minigame — the engine cannot correctly enter a second conversation regardless of how
vanilla-forced the first one is).

**The default flipped**: `DialogueAutoJoinRule.ParseMode`'s fallback (unset/unrecognised text)
changed from `near` to `off` — the user's stated preference is no forced joins for ordinary
chat, full stop. `dialogue_join_teleport` (already shipped, default on) is untouched and still
covers a forced conversation's own joiner-too-far case exactly the way it already covers a far
`always`/`near` join, since it triggers off `Odyssey.StartDialogueWith`'s own distance check
regardless of WHY the join is happening.

### VERIFIED / INFERRED / NEEDS-LIVE

| Claim | Status |
| --- | --- |
| `[0x747450]` = 10.0-unit range threshold | VERIFIED LIVE (prior pass) |
| `[ebx+0x48]` bypasses the ENTIRE range/walk block when nonzero | VERIFIED (disasm, both passes) |
| `[ebx+0x48]` is the function's first stack arg, readable at prologue | VERIFIED (this pass, instruction-traced) |
| `action_conversation` (`+0x1B910`) is a boolean, not the action struct pointer | VERIFIED (this pass, all 3 setter sites disassembled) |
| `[ebx+0x48]` = NWScript's `bIgnoreStartRange` specifically (not merely "the range bypass") | INFERRED (position + effect match; not DB-named) |
| `AIActionDialogObject`'s `this` = the same object `StartDialog` later fires `this` on | INFERRED (NWN-heritage convention; fails safe if wrong) |
| A real click's action is built with this flag hardcoded to 0 (`HandlePlayerToServerInputMessage`'s "mostly-zeroed pushes") | INFERRED (dialogue-join-latency.md's own reading of that call site; not independently re-verified this pass) |
| **The flag reads TRUE for a real ambush/forced conversation and FALSE for a real player click, on the actual game** | **NEEDS-LIVE — the one item this round names explicitly** |

Everything else in this section either follows deterministically from a VERIFIED disasm fact or
is pure managed code covered by unit tests (`DialogueAutoJoinRuleTests`'s forced x mode x range
matrix). The NEEDS-LIVE item is narrow and has a ready-made diagnostic:
`DialogueSpectateController`'s `dialogue: engine reached StartDialog (N so far, forced=...)` log
line (extended this pass specifically for this) fires on every `StartDialog` call, refused or
not, on either instance. A live round should: (a) `talk <tag>` (the existing test hook, which
sends the same request a click does) against an ordinary NPC and confirm `forced=False`; (b)
find or trigger a real ambush/forced conversation (docs/research/dialogue-consequences.md
may already name one; Taris and the Endar Spire both script forced conversations) and confirm
`forced=True` there, and confirm `interaction: auto-joining a peer's conversation with '...'
(forced — matching the engine's own force)` fires on the OTHER instance even with
`dialogue_auto_join=off`.

## Live pass (2026-08-23): the FALSE half confirmed both directions; the TRUE half still open

Two-GOG-install rig (`tools\demo.ps1`), `END_M01AA`, block v51 / protocol v38, both builds
rebuilt and installed with `build.ps1 -Install` before this pass. `dialogue_auto_join` unset in
either install's `coop.ini` (so both ride the new code default, `off`).

### (a) Ordinary conversation: no auto-pull, either direction — PASS

Host opens `end_trask` via the `talk` test hook (the same request a click sends):

```
HOST    [12:15:06.640] dialogue-node-pub: #1 node -1 -> 29 serial 1 at 2419281
HOST    [12:15:06.641] dialogue: engine reached StartDialog (1 so far, forced=False)
```

Client's block, sampled seconds later: `remote: active 1 partner 'end_trask'` (aware a peer
holds one) but `local: partner ''` (never pulled in) — no `interaction: auto-joining` line
anywhere in the client's log for this open. The client's own explicit `talk end_trask` (the
click-to-join equivalent) then opens and adopts normally: `local: partner 'end_trask'` — click-
to-join parity confirmed intact under the new default.

Reversed — client opens, host must not get pulled:

```
CLIENT  [12:17:20.797] dialogue-node-pub: #2 node -1 -> 19 serial 3 at 2553437
CLIENT  [12:17:20.798] dialogue: engine reached StartDialog (2 so far, forced=False)
```

Host's block: `remote: active 1 partner 'end_trask'`, `local: partner ''` — same result, other
direction. **Both halves of (a) pass, both directions, forced=False logged both times.**

### (b) A genuinely forced conversation: not reached live this round — the positive case stays NEEDS-LIVE

Extensive search, all negative (each entry below is a distinct thing tried, not a retry of the
same one): repeated `end_trask` open/close cycles waiting up to ~60 s for the documented
scripted re-initiate; teleporting (`console 'tp x y z'`, the same lever `CheatController.tp`
uses) into the `nulltrigger` (`k_pend_pctut`) polygon right at the spawn point; into the
`end_sealbridge` (`k_pend_bridge06`/`07`) polygon at the far end of the corridor; and into the
Carth/`end_bridgerep1`/`end_bridgerep2` cluster near the bridge, including killing the two
hostile `sithsoldier`s blocking the approach (`cheat slay`) so the area could be reached
without combat noise drowning the log. None produced a spontaneous `StartDialog` outside a
`talk`-hook request.

**One genuinely useful negative did turn up, though, and it is worth keeping**: Trask's own
documented scripted re-initiate (dialogue-sync.md's own "worst case for this" quirk, first
seen 2026-08-22) reproduced live, unprompted, ~60 s after the owner's conversation had gone
idle at a reply node with no player input:

```
HOST    [12:26:40.279] dialogue-node-pub: #8 node -1 -> 1 serial 13 at 3112921
HOST    [12:26:40.279] dialogue: engine reached StartDialog (6 so far, forced=False)
```

This `StartDialog` was NOT reached via the `talk` test hook (no `test: opened a conversation`
line anywhere near it) — it is the engine's own NPC-driven re-initiate, exactly the kind of
"conversation nobody clicked into" this section's whole design exists to classify correctly.
**It reads `forced=False`, and that is the CORRECT answer, not a miss.** `[ebx+0x48]` is
specifically the range/walk BYPASS bit (`bIgnoreStartRange`) — Trask standing directly next to
an already-in-range player passes the ordinary range gate on its own merits and never needs the
bypass, so an NPC deciding to talk is not automatically "forced" in this flag's sense. This is
useful live confirmation that the flag tracks what the disassembly says it tracks (the bypass),
not a looser "who initiated" proxy that would have made it fire here — and the client, correctly,
did not auto-join this re-initiate either (`dialogue_auto_join=off`, no `auto-joining` line on
either instance's log for this event).

Combined FALSE-side tally this pass: **6 independent `StartDialog` events, host and client
combined, every one `forced=False`** — 2 ordinary `talk`-hook opens (a) above, 1 client-side
join-click, 2 short "already-friendly, no reply needed" branches hit while probing for a
different starting entry, and the 1 spontaneous Trask re-initiate — comfortably past the task's
own "five separate ordinary conversations, no false positives" floor for a round that could not
reach the positive case.

**Why the positive case stayed out of reach**: the two saves available to this rig
(`docs/live-verification.md`'s own long-standing limitation) are both already past whatever
truly-forced content the Endar Spire's early script has to offer, and none of the 14 STORY
triggers this session could reach without walking (the explosion/reinforcement cluster, the
seal-bridge trigger, the tutorial null-trigger) turned out to independently fire a `StartDialog`
of their own when entered by teleport rather than by the scripted sequence that normally leads
into them — consistent with `trigger-relay.md`'s own finding that several of these triggers are
idempotent or content-free when fired out of their native order, not evidence that no forced
conversation exists in this game at all. **The positive case (`forced=True` observed live)
remains NEEDS-LIVE**, same status as before this pass — what changed is the FALSE side is now
verified far more thoroughly, including the one case (an NPC-initiated, non-click conversation)
that most plausibly could have been a false positive and was not.

No source change was needed for item 1 — the mechanism performed exactly as designed on every
observation.

## Live pass, retry (2026-08-23, round 77): the TRUE half finally observed, live, twice — and a
## real gate bug it exposed, found and fixed

Two-GOG-install rig, warped (not walked in from a fresh save — the user's own direction this
round) to `tar_m02ad`, the Taris Sith-base interrogation cluster (`tar02_sithtortur`/
`tar02_sithresume`/`tar02_sithatk`, trigger ids `0x33`/`0x35`/`0x36` this session — see
`docs/research/trigger-relay.md` for the inventory these resrefs come from). `blockpoke.ps1
travel tar_m02ad` on the host; the client auto-followed via the existing module-follow channel
(`docs/research/cinematics.md`'s module-transition machinery, unrelated to dialogue). Both
players' real characters arrived together at the module's own arrival waypoint, `(90.00,
125.21)`, which sits inside the interrogation trigger cluster's footprint on its own — no OS
keyboard/mouse input was used anywhere in this pass; every position change was
`blockpoke.ps1 walk`, the engine's own move action (`TestDriveController`), never a raw
position write.

### The positive case, finally: `forced=True`, observed independently on both instances

```
CLIENT  [13:51:25.815] triggers: observed my own engine natively fire OnEnter of 0x33 — publishing for other peers
CLIENT  [13:51:25.826] dialogue-node-pub: #1 node -1 -> 9 serial 3 at 8198468
CLIENT  [13:51:25.826] dialogue: engine reached StartDialog (3 so far, forced=True)
```

```
HOST    [13:51:37.456] triggers: observed my own engine natively fire OnEnter of 0x36 — publishing for other peers
HOST    [13:51:37.457] dialogue: engine reached StartDialog (6 so far, forced=True)
```

Both are the engine's own `EventHandler`-driven native fire of a STORY trigger's `OnEnter`
(`TriggerFireHook`, `docs/research/trigger-relay.md` §12) — genuinely uninitiated by a player
click on either side — followed immediately by `DialogueGateHook.LastStartForced` reading
**TRUE**. This closes dialogue-sync.md's own long-standing NEEDS-LIVE item verbatim: *"The flag
reads TRUE for a real ambush/forced conversation and FALSE for a real player click, on the
actual game"* — now observed twice, independently, on two different trigger ids in the same
cluster, on two different instances. No false positive: every ordinary (`talk`-hook or
click-adjacent) `StartDialog` in this pass and every one from the prior 2026-08-23 pass still
read `forced=False`.

The auto-join-from-genuine-distance half of item 2's brief (the OTHER player, positioned
elsewhere and not clicking, auto-joining) was **not cleanly isolated this pass** — both real
characters kept landing at the identical module arrival waypoint, so both independently and
natively entered the same trigger cluster before a `blockpoke.ps1 walk` command sending one of
them away could get clear of it (a `~90` unit walk takes real seconds; the cluster's own
triggers fired well inside that window both times this was tried, including a repeat attempt
aimed at a fresh, untried trigger — `tar02_sartlk` — which never fired a conversation at all,
either because its script does not call `ActionStartConversation` or because the walk did not
land inside its polygon; `tar02_crtpty` fired natively but is a spawn/setup script with no
dialogue, confirmed harmless). What was observed instead, both times, was the pre-existing
**adopt** path (`dialogue: the engine started the same conversation here; adopting it to
follow`) — both engines independently and natively entering the identical conversation, which is
a different, already-verified code path from the cross-instance forced-auto-join
(`InteractionLockController.TryForceOpen`) this item also asked about. The `interaction:
auto-joining a peer's conversation ... (forced — matching the engine's own force)` log line
never appeared in either pass's full log. This is left NEEDS-LIVE for a future round with a
reliable way to hold two real characters at genuine, sustained distance — `blockpoke.ps1 walk`'s
own documented unreliability (`trigger-relay.md` §11) is the limiting factor here, not anything
this pass found wrong with the auto-join code itself (see the bug found below, which is a
different code path entirely — the START side, not the JOIN side).

### A real bug this pass found and fixed: the exclusivity gate had no forced bypass

The first attempt's raw log showed something worth reading closely — not "the client's forced
conversation reached the client," which worked, but what happened on the **HOST** while it did:

```
HOST [13:51:35.579] dialogue: blocked a local conversation start; a peer holds the token
HOST [13:51:35.580] dialogue: engine reached StartDialog (4 so far, forced=False)
HOST [13:51:36.487] dialogue: blocked a local conversation start; a peer holds the token
HOST [13:51:36.487] dialogue: engine reached StartDialog (5 so far, forced=False)
HOST [13:51:37.456] triggers: observed my own engine natively fire OnEnter of 0x36 — publishing for other peers
HOST [13:51:37.456] dialogue: blocked a local conversation start; a peer holds the token
HOST [13:51:37.457] dialogue: engine reached StartDialog (6 so far, forced=True)
```

**A genuinely forced `StartDialog` — attempt #6, `forced=True` — was refused**, "a peer holds
the token", the exact same refusal an ordinary click gets when another peer is mid-conversation.
Reading `KotorCoop.Plugin.DialogueGateHook.OnStartDialog` explained why: it computes
`LastStartForced` (via `DialogueForceFlagHook.WasForced`) purely for diagnostics, and the
refusal decision a few lines later (`!Enabled || !PeerHoldsConversation` → `BypassGrants` →
`TargetIsPeersPartner`) **never once consulted it**. The hook's own doc comment on
`BypassGrants` already said as much, in so many words: *"NOTHING GRANTS ONE TODAY... the next
thing that needs one — a scripted cutscene join, say — should use it instead of inventing a
second door."* Forced conversations were exactly that unimplemented case, and this pass is what
finally exercised it live.

This is not a cosmetic delay. The hook's own header documents that a refusal takes the engine's
real StartDialog failure path, which **clears the queued `BeginConversation` action** (retail's
own cleanup, `0x004F34A0`). If the NPC's script does not re-queue the `AssignCommand` itself (not
knowable without a decompile), a forced conversation refused by this mod's own gate could be
**lost outright**, not merely delayed — worse than doing nothing, since vanilla KOTOR never
refuses its own forced conversation regardless of what else the engine is doing. In this
session's own case the sith-interrogation script evidently DID retry (attempts #4, #5, #6 across
~2 seconds, #6 finally the forced one), and it eventually got through when the client's own
earlier conversation ended — but relying on every future forced-conversation script to retry
indefinitely is not a foundation to build on.

**Fix** (`src/KotorCoop.Plugin/DialogueGateHook.cs`, `OnStartDialog`): compute the forced flag
into a local before any refusal branch, and bypass the whole gate — `Enabled`/
`PeerHoldsConversation`/`BypassGrants`/`TargetIsPeersPartner`, all of it — unconditionally when
the attempt is forced, mirroring `DialogueAutoJoinRule.ShouldAutoJoin`'s own `isForced` handling
on the JOIN side (*"this rule joins UNCONDITIONALLY... because this is matching behaviour
vanilla already forces, not inventing a new one"*). `LastStartForced` itself is still recorded
exactly as before (same stale-value-preserving guard when `target` fails the plausible-pointer
check), so nothing about the diagnostic changes — only the refusal decision does.

**Live-confirmed fixed**, same rig, same module, same trigger cluster, after rebuilding and
reinstalling on both instances:

```
HOST [14:12:31.543] triggers: observed my own engine natively fire OnEnter of 0x33 — publishing for other peers
HOST [14:12:31.544] dialogue: engine reached StartDialog (1 so far, forced=True)
```

Zero `blocked` lines anywhere before it — the FIRST `StartDialog` attempt this session, forced,
succeeded immediately, with the client adopting and following normally afterward
(`dialogue-node-drive` landed `arrived=True` on both subsequent nodes). Full `dotnet test
tests/KotorCoop.Tests` still 1635/1635 after the change (the plugin assembly itself carries no
managed test coverage — native x86 NativeAOT, nothing references it, per this project's own
standing note).

### Bug report 2 (combat presence goes quiet after a forced-dialogue-into-combat transition) —
### root-caused by reading, NOT fixed this round

User report, from real (not staged) play: *"the other clients were able to kill one NPC which did
sync correctly, however when the first client that initiated dialogue started entering combat
with the sith soldiers — the other clients did not [see them enter combat], damage was synced
though."*

Reading `CoopAgent.PumpEnemies` (~line 2705) confirms the shape exactly: the enemy CENSUS
(presence, not damage) is published only by the enemy authority, which is **always the host**
(`CoopHost.IsEnemyAuthority => true`, `CoopClient.IsEnemyAuthority => false` — static per role,
never dynamic), and only while `!bridge.IsInDialogue` — held, not merely delayed, for the WHOLE
duration any conversation (the host's own, or one it is only FOLLOWING) is open on the host's
own engine:

```
// Hold the last snapshot rather than publishing while in a conversation. The
// dialogue system wipes every hostile's action queue in this instance on every
// tick, so what we would send is a pacified fight — and the peer, who is not
// talking, would have their own fight go quiet for the duration.
if (!bridge.IsInDialogue && !bridge.IsInMinigame) { ... }
```

Damage (`PumpPeerEffects`/the phantom-damage channel) carries no such gate — "published every
pass, even when it is empty" — which is exactly why the report's own damage-synced/
presence-didn't split matches the code precisely, not approximately.

**The most likely mechanism, given this round's OWN forced-auto-join fix above**: before today,
the host rarely entered ANY conversation except its own, so this gate rarely mattered for a
fight the host was not personally having. Once a peer's forced conversation can pull the HOST
itself into FOLLOWING it (a real, native `StartDialog` on the host's own engine — dialogue-sync's
own node-sync design opens a genuine local `CSWSDialog` copy, not a fake one), the host's own
engine now ALSO has hostile AI queues paused by the SAME vanilla mechanism that pauses them
during the host's own conversations — for the SAME duration the followed conversation is open,
not longer. If the sith soldiers attack the instant the conversation's last node closes (a
common ambush-then-fight script shape), the client's own real combat can start while the host's
followed copy is still catching up to the close, so the census stays frozen for that window.

**This round did NOT ship a fix for this.** Two reasons, both deliberate rather than a shortage
of time alone:

1. **The underlying engine behaviour the gate protects against is real and correct.** KOTOR's own
   dialogue system genuinely pauses hostile AI action queues on WHICHEVER instance has any
   conversation open, including a purely-followed one — this is not a bug in the gate, it is the
   gate correctly refusing to publish data the host's own paused simulation cannot currently
   produce accurately. Simply removing the gate would publish a pacified snapshot, which is the
   exact failure this code was written to prevent in the first place (see the comment above,
   predating this pass).
2. **No live reproduction of the specific timing this round's fix would need to be judged
   against.** Setting up "forced dialogue that rolls immediately into combat, with the HOST
   specifically pulled into following it while a peer fights" needs either a scripted
   ambush→combat sequence this rig could reliably trigger (not found this pass — the sith
   cluster's own three triggers produced a conversation but no immediate follow-on combat in
   either live pass) or a human at the controls (this round's own standing focus blocker, see
   below).

**What a future round should try**: measure the actual gap between the followed conversation
visibly closing on the host (`the owner ended the conversation; closing`) and the host's own
`bridge.IsInDialogue` clearing, under a real ambush→combat script; if that gap is small (under a
second, matching the existing `DialogueLingerMs` debounce elsewhere in this file), the report is
better explained by something else (a stale census cap, `otherPlayers` distance shedding, or the
client simply not being within the census's own range-sheds — `PumpEnemies`'s own `otherPlayers`
parameter). If the gap is materially longer than the conversation itself, that is the bug to
chase, and the fix is likely in how promptly a FOLLOWED (as opposed to owned) conversation closes
on the follower's own engine, not in the census gate itself.

## Round 78 (2026-08-23): the gap measured, confirmed real, and fixed - a debounce sized for "a
## few frames" was actually 1.5 seconds

Picks up round 77's own bug 2 exactly where it left off: measure the actual gap between the
followed conversation visibly closing on the host and the host's own `bridge.IsInDialogue`
clearing. No real ambush-into-combat script was needed to answer this - the mechanism under test
is a pure function of `CoopAgent.PumpDialogue`'s own debounce, independent of how the conversation
started, so a CONTROLLED reproduction (below) measures the identical code path.

### Instrumentation added first, per the task brief

`CoopAgent.cs` had no log of `bridge.IsInDialogue`'s own transitions anywhere. Added
`LogHostDialogueEdge` (called once per poll tick, ahead of every cadence gate), edge-triggered,
logging on EVERY instance (not only the host) so a live pass can see both the OWNER's true-end edge
and the FOLLOWER's clear-edge on the same timeline:

```
dialogue-gate: host bridge.IsInDialogue False -> True at <tick> (followingPeer=..., tokenActive=..., tokenSerial=...)
```

### Reproduction: a controlled follow/close cycle, no OS input, no ambush script needed

`dialogue_auto_join=always` set for this round's rig only (not shipped - reverted before teardown;
the shipped default stays `Off` per the 2026-08-23 Item 1 decision, unrelated to this bug), which
makes the SAME `DialogueAutoJoinRule.ShouldAutoJoin` unconditional-join path an ordinary
conversation takes that a FORCED one already takes regardless of this setting - the mechanism
`InteractionLockController`/`DialogueSpectateController` runs is identical either way, so this is a
faithful reproduction of the follower-close path, not a different one. Client-side `talk end_trask`
(the existing `TestDriveController.HandleTalk` primitive, exactly what a click does) started a real
conversation; the HOST auto-joined and adopted it (`dialogue: the engine started the same
conversation here; adopting it to follow`, content delivered within tens of ms). Client-side
`talk close` (the SAME primitive's `Odyssey.CloseDialogue` branch - a real `CSWSObject::StopDialog`
call, not a shortcut) ended it for real.

Trask's own end script re-initiates the conversation on its own (documented already,
`YieldCloseRetryMs`'s own remarks: an NPC scripted to re-initiate turns a per-frame close into an
open/close loop) - several cycles needed a `talk close` on both sides to settle before each timed
attempt, and which side ends up OWNER vs FOLLOWER on a re-initiate is a genuine race (the host's own
claim is always zero-latency in-process, a client's crosses the network, so whichever side's local
`StartDialog` lands first wins the token) - not a bug this round chased further, just a fact about
Trask specifically that shaped how each measurement had to be staged.

### The bug, confirmed live: the OLD 1500ms `DialogueLingerMs` was the dominant term

Before any fix, one full cycle, correlated across the CLIENT's plugin log (the real close) and the
HOST's agent log (the edge):

```
CLIENT [15:01:38.403] test: closed any running conversation      <- the real StopDialog call
HOST   [15:01:41.101] dialogue: the owner ended the conversation; closing
HOST   dialogue-gate: host bridge.IsInDialogue True -> False at 12413750   (~15:01:41.111 by tick correlation)
```

**Gap: ~2.7 seconds** between the real close and the host's own census-gating flag clearing -
consistent with `DialogueLingerMs`'s 1500ms ceiling plus the existing ~150ms x2 wire/pump cadence
(client detects+publishes, host reads+republishes) the debounce sits on top of. `CoopAgent.cs`'s own
`DialogueLingerMs` comment - "Longer than any between-entries gap... shorter than feels laggy on a
real close" - was sized by feel, not by a measured flicker duration, and its true cost was invisible
to every existing test: nothing reads a conversation OWNER's own debounce state (its
`bridge.IsInDialogue` is a raw, undebounced engine read - see `KotorGameBridge.IsInDialogue`), so
this only ever mattered for a FOLLOWER, and specifically for `PumpEnemies`'s host-only census gate,
which nothing in this codebase's test suite exercises (the plugin assembly carries no managed
tests, and the census gate itself has no unit-test seam either).

### Fix: `DialogueLingerMs` 1500 -> 300 (`src/KotorCoop.Session/CoopAgent.cs`)

Sized against the debounce's OWN stated purpose - surviving "a few frames" of the engine's
between-entries flicker (the frame hook runs at ~300 Hz, so a few frames is on the order of
10-20ms) - 300ms is still a 15-30x safety margin over that, while being 5x smaller than the old
value. A 50ms value was also tried live, as an isolation experiment (see below) - not shipped.

### Isolating what the debounce itself actually controls, live

Three more measured cycles at `DialogueLingerMs=300` (the shipped value):

```
cycle 1: client true-close 15:09:08.906 -> host clear ~15:09:09.921   (1015 ms)
cycle 2: client true-close 15:09:18.594 -> host clear ~15:09:19.389   ( 795 ms)
cycle 3: client true-close 15:09:28.293 -> host clear ~15:09:29.729   (1436 ms)
```

Average ~1082ms - a real, repeatable ~60% cut from the ~2708ms measured before the fix (roughly
2.5x faster). A further isolation experiment at `DialogueLingerMs=50` (one clean cycle,
964-1089ms) landed in the SAME 800-1500ms band as the 300ms samples - i.e. shrinking the debounce
below ~300ms bought no further measurable improvement, because the remaining time is dominated by
the pre-existing, unrelated ~150ms x2 wire/pump cadence stacking (client detect+publish, host
read+republish - the same cadence `dialogue-join-latency.md` already documents for the join side of
this exact machinery), not by the debounce ceiling itself once it is no longer the dominant term.
300ms was kept over 50ms specifically because of this finding: it buys the same real-world latency
as a much smaller value while keeping a comfortable margin against a between-entries flicker this
round did not have time to directly measure (no OS input access to click through a long multi-line
conversation and watch the raw engine flag flicker directly) - the safe, evidence-respecting choice
given a genuinely flat region of the latency curve, not a guess.

### What this does NOT close

The remaining ~800-1400ms is the SAME wire/pump cadence every other dialogue-follow channel in this
codebase already rides (`dialogue-join-latency.md`'s own documented numbers for the join side) - not
a new bug this round introduces, and reducing it further is exactly what `dialogue_fast_join`
already exists for (a separately, already-decided, off-by-default setting - out of this round's
scope to flip). The task brief's own target - "visible... within roughly one census cadence"
(~150ms) - is met in the sense that matters practically (the census unfreezes in about a second
instead of several), but not literally in the "same frame or two" sense; getting there would need
`dialogue_fast_join=1` as well, which this round did not turn on by default (unrelated decision,
already made in the 2026-08-23 Item 1 entry above).

### Regression check: no flicker observed

Across the ~6 full talk/adopt/close cycles run this round (some spanning up to 30 real seconds with
multiple node transitions before the deliberate close), zero spurious `dialogue: the owner ended the
conversation; closing` / `adopting` pairs appeared BETWEEN a cycle's own adopt and its own deliberate
close - i.e. no premature-flicker regression at either 300ms or (in the one cycle it was tried)
50ms. Not exhaustive (a single rig session, one NPC's own script shape), which is exactly why 300ms
was kept rather than the smaller value that showed no benefit anyway.

### Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1635/1635 passed**, unchanged baseline - the fix is a
constant change plus a broadened log line in `CoopAgent.cs`, no new managed surface. Both plugin
builds (`src/KotorCoop.Plugin/build.ps1 -Install`, both GOG installs) clean, 0 warnings, 0 errors
(the plugin itself is untouched by this job; rebuilt anyway as part of the same session's combined
verification pass with job 1's `InteractionLockController.cs` fix). No crash, no new exception in
either plugin log across the whole session (same `FIRST-CHANCE exception (0x406D1388)` benign
startup diagnostic as every other round, nothing new).

## Addendum (2026-08-28): `startdialog` — a dev verb that starts a conversation without
## walking into a trigger, built by reusing the VM routine-table dispatch, NOT a raw StartDialog call

Task: a `startdialog <npc-tag|id> [dlg-resref]` verb so scripted conversations
(`k_sta_bastila.dlg` named specifically) can be triggered without navigating to their real
trigger — `star-forge-finale.md`'s own live pass never reached Bastila's duel because walking
into `k45_init_bastila` never fired it. Built. **NEEDS-LIVE** — see the plan below.

### Why NOT call `CSWSObject::StartDialog` directly (the `openstore`-style shortcut declined)

`store-sync.md`'s own `openstore` addendum set the precedent this task pointed at: skip the VM
command handler's own argument-popping machinery and call the clean C++ function underneath
directly. This pass tried exactly that first, and stopped once fresh disasm showed why it is
NOT safe here the way it was for `ShowStoreGui`.

`CSWSObject::StartDialog 0x004CF490` (VERIFIED, `tools/kotor1_0_3.db` and `dialogue-sync.md`'s
own headline table) is `__thiscall(this, uint listenerId, CExoString* privateConversation,
CExoString* defaultConversation)` — `tools/q.py sql` confirms `param_size_bytes=12` (3 stack
args). Fresh disasm of its ONE caller, `CSWVirtualMachineCommands::
ExecuteCommandBeginConversation 0x0052E9F0` (VERIFIED, `tools/xrefs.py 0x4cf490` — exactly one
caller, matching `dialogue-sync.md`'s own §1 finding), traced both of `StartDialog`'s two call
sites there in full:

```
ExecuteCommandBeginConversation 0x0052E9F0 (this = the VM command dispatch context, NOT a
                                             creature — its own ecx+8/ecx+4 fields feed the
                                             SAME id-resolve chain other Execute* handlers use)
  esi = CServerExoApp::GetActionConversation 0x004AF020(server)   -- the QUEUED action's own
                                                                      staged data, a SERVER-
                                                                      GLOBAL "current action"
                                                                      blob, not a per-object field
  ... resolves TWO participants (ecx=ebx+8's id, and either OBJECT_INVALID's own party-leader
      substitution or [firstResolved+0x17C]) via CGameObjectArray::GetGameObject 0x004D8230,
      landing in esi/edi ...
  ebp = [esi + 0x180]          -- read ONCE, off one of the TWO RESOLVED PARTICIPANTS, not off
                                   a bare id or a caller-owned local — passed as StartDialog's
                                   3RD argument (a CExoString*, per the DB's own param size)
  push ebp ; push &localCExoString ; push <listenerId> ; ecx=<speaker> ; call 0x4cf490
```

Inside `StartDialog` itself: the 2nd argument (`&localCExoString`, the "private conversation"
override) is tested via what reads as `CExoString::IsEmpty` (`0x5e5290`); if EMPTY, `StartDialog`
does NOT fall back to its own 3rd argument — it instead makes a VIRTUAL CALL through the SPEAKER
object's OWN vtable slot `+0x80` to fetch a default resref, and copy-constructs THAT into a
local. Both the 2nd and 3rd arguments are therefore values the ENGINE builds by resolving two
separate participant objects and reading fields off THEM — not bare ids, not values this plugin
can safely invent. Calling `StartDialog` directly would mean either (a) fabricating a
`CExoString*` pointing at real, correctly-shaped memory for "no override" (recoverable — an
empty, zero-initialised `CExoString` local is a reasonable guess), or (b) somehow supplying
`esi+0x180`'s real value for a participant this code never independently resolves — genuinely
unclear without a great deal more tracing of what that field is (a per-object "cached
conversation partner" resref? something else?), and a wrong guess here is exactly the class of
bug `store-sync.md`'s own 2026-08-28 "Live pass" section hit for `openstore`'s first attempt (a
crash inside `PopulateStoreItemListBox`, from a similarly-plausible-looking but wrong argument).
**Declined, not attempted against a live process.**

### The design actually built: dispatch through the VM's own routine table

`src/KotorCoop.Plugin/Odyssey.ActionQueue.cs`'s existing `MoveToLocation` already solved the
identical shape of problem for `ActionMoveToLocation` (NWScript routine 21): rather than
hand-crafting the C++ function underneath, it pushes REAL values onto the VM's own parameter
stack (`CVirtualMachine::StackPushInteger/String/Object/EngineStructure`, all VERIFIED,
pre-existing bindings) and calls the routine's OWN handler with `(implementer, routineId,
argCount)` — exactly the calling convention a compiled script's own CALL instruction would use,
letting the ENGINE build every downstream struct itself. `walkto`/`usedoor` already ship on this
exact mechanism, live-verified.

`ActionStartConversation` is NWScript routine **204**, handler
`CSWVirtualMachineCommands::ExecuteCommandActionStartConversation 0x0052D5B0` — VERIFIED,
`docs/research/star-forge-finale.md` §1 (the `InitializeCommands` routine-table scan method,
cross-checked against two independently-known ids from the same scan). This pass reused that
citation rather than re-deriving it, and — matching `MoveToLocation`'s own defence-in-depth —
`Odyssey.StartConversation` re-checks the table entry against the expected handler address
LIVE, every call, refusing rather than dispatching to whatever else might now live at
`table[204]` if the citation turns out wrong or the binary differs.

Fresh disasm of `0x0052D5B0` itself (this pass) confirms it pops its NWScript parameters off the
REAL VM stack via `CVirtualMachine::StackPopObject 0x5D10C0` (unconditionally, the mandatory
`oObject`) and, gated on the caller-supplied `nParams` count, `StackPopString 0x5D1080`
(`sDialogResRef`) then a further run of `StackPopInteger 0x5D1000` calls for the trailing
optional int parameters (`bIgnoreStartRange`, `iAnimation`, ...) — the standard NWScript
optional-argument popping idiom this codebase's own `SpawnCreature`/`MoveToLocation` already
push against. Pushing exactly 1 or 2 arguments (`oObject`, optionally `sDialogResRef`) and
dispatching with the matching `argCount` reproduces a bare
`ActionStartConversation(oObject)`/`ActionStartConversation(oObject, sDialogResRef)` script call
precisely — no fabricated structs, no guessed field, the engine resolves everything else itself
exactly as it would for a real script.

### Who queues on whom: the LOCAL PLAYER is the actor, the NPC is the argument

The one design choice this pass had to settle from convention rather than a fresh trace:
`ActionStartConversation` queues its action on OBJECT_SELF (the OWNER of the action queue that
will run it), and `dialogue-sync.md`'s own existing "Implementation" section (the
`AIActionDialogObject` force-flag round) already documents the answer: **"a real click queues
it on the PC; a scripted ambush typically queues it on the ambushing NPC via `AssignCommand`."**
For a player-driven console verb — the direct analogue of a real click, not a scripted ambush —
the LOCAL PLAYER must be the queuing object: `DialogueSpectateController.PublishLocal` detects a
NEW local conversation off the LOCAL PLAYER's own `obj+0x34` going non-null (the SAME field
`StartDialog` writes, `dialogue-sync.md`'s own headline fact); queuing on the NPC instead would
leave the player's own `+0x34` untouched and the existing spectate/lock machinery
(`DialogueGateHook`, `DialogStateWatchHook`) would never notice the conversation this verb just
started. So `startdialog <npc-tag|id>` redirects the VM's caller-object id to the LOCAL PLAYER
(the same `ImplHasCallerObject`/`ImplCallerObjectId` redirect-and-restore dance
`MoveToLocation` already does) and pushes the NPC as the `oObject` argument — i.e. the exact
shape of a real click, `ActionStartConversation(oNpc)` queued on the player.

### The action is QUEUED, not immediate

Same caveat `MoveToLocation` already documents for itself: this does not run synchronously. It
queues onto the player's action queue and executes once the queue reaches it — in practice the
very next tick if nothing else is queued ahead of it, since a `startdialog` call is not expected
to race a real walk/attack action. Runs through the REAL, completely unmodified
action-queue -> `AIActionDialogObject` -> `BeginConversation` -> `CSWSObject::StartDialog` chain
this whole document already maps, so every existing hook this codebase built to OBSERVE that
chain (`DialogueGateHook`'s `StartDialog` prologue tap, `DialogStateWatchHook`) sees it exactly
as it would a real click or a scripted trigger — nothing about this verb's mechanism bypasses
them, unlike a hypothetical raw `StartDialog` call would have (see above).

### What was built

`src/KotorCoop.Plugin/Odyssey.Dialogue.StartConversation.cs` (new file) —
`RoutineActionStartConversation = 204`, `ExpectedActionStartConversation = 0x0052D5B0`,
`StartConversation(playerObjectId, npcObjectId, dialogResref, out failure)`: resolves the VM
implementer, verifies the routine-table entry, redirects OBJECT_SELF to the player, pushes
`sDialogResRef` (if non-empty) then `oObject` (reverse declaration order, matching
`MoveToLocation`'s own documented push order), dispatches, restores OBJECT_SELF, verifies the
VM stack came back stack-neutral. `SharedBlock.CheatKindStartDialog = 42`,
`IGameBridge.CheatKinds.StartDialog = 42` (mirrored, `CheatKindSyncTests` covers both).
`src/KotorCoop.Plugin/CheatController.Dialogue.cs` — `StartDialog(playerId)`: resolves the NPC
by id or tag (`CreatureFinder.FindByTag`, the same primitive `pazaak`'s own opponent resolution
already uses), unpacks the optional dialog resref out of the wire request's packed text field
(see below), calls `Odyssey.StartConversation`. `CheatController.cs`'s dispatch switch gained
the one case.

**Field packing**: identical convention to `buy`/`sell` (`store-sync.md`'s own 2026-08-28
addendum) and for the same reason — `startdialog` needs BOTH an NPC identity (tag or id) AND an
optional dialog resref override at once, and the wire cheat request has exactly one string
field. Numeric NPC id -> `CheatTarget` carries it, `CheatText` carries the bare (optionally
empty) resref override. NPC tag -> `CheatTarget=0`, `CheatText` packs `"<tag>"` (no override) or
`"<tag>|<dlg-resref>"` (override present) — the same pipe-delimited shape, reused rather than
inventing a second convention.

**Session/Agent**: `src/KotorCoop.Session/ConsoleCommand.cs` — `CoopCommandKind.StartDialog`,
verb table row. `src/KotorCoop.Session/ConsoleCommand.Cheats.cs` — `ParseStartDialog`:
`<npc-tag|id> [dlg-resref]`, resref validated as a resref (max 16 chars, matching
`SharedBlock.ItemResrefLength`'s own dialog-adjacent convention — a DLG resref, same 16-char
engine cap as an item resref) when given. `src/KotorCoop.Agent/CoopCommandDispatcher.Dialogue.cs`
(new file) — `StartDialog(npcToken, dialogResref)`, performing the numeric-vs-tag split.
`CoopCommandDispatcher.cs`'s dispatch dictionary gained the entry.
`src/KotorCoop.Plugin/ConsoleVerbCatalog.cs` and `FuncInterpHook.cs` both gained `startdialog` —
reachable at the real `~` console, not blockpoke-only, same as `buy`/`sell`/`openstore`.
`docs/CONSOLE.md` gained the row.

**Tests**: `tests/KotorCoop.Tests/ConsoleCommandVerbTableCoverageTests.cs` gained a sample line
for the new `CoopCommandKind` (covered by construction the same way every other kind is);
`ConsoleCompletionEngineTests`'s catalog-sync assertions cover the new catalog entry with no new
test code (`startdialog` is not in `console-cheats.md`'s retail verb table). Full suite green,
`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError` both
clean.

### VERIFIED / INFERRED / NEEDS-LIVE

| Item | Status |
| --- | --- |
| `CSWSObject::StartDialog 0x4CF490` signature `(this, uint listenerId, CExoString* privateConversation, CExoString* defaultConversation)`, its ONE caller `ExecuteCommandBeginConversation`, and both argument sources (a server-global "current action" blob via `GetActionConversation`, and a field read off a SEPARATELY RESOLVED participant object) | VERIFIED (fresh disasm, this pass) — the reason a raw `StartDialog` call was declined |
| `ActionStartConversation` = NWScript routine 204, handler `0x0052D5B0` | VERIFIED (`star-forge-finale.md` §1's own citation, reused, not re-derived) |
| `0x0052D5B0`'s own VM-stack-popping shape (`StackPopObject` unconditional, `StackPopString`/`StackPopInteger` gated on `nParams`) | VERIFIED (fresh disasm, this pass) — confirms pushing 1-2 args and dispatching with the matching `argCount` reproduces a real script call |
| "A real click queues `ActionStartConversation` on the PC" (the design's own choice of actor) | Documented convention (`dialogue-sync.md`'s own prior "Implementation" section), not independently re-traced this pass |
| `Odyssey.StartConversation`/`CheatController.StartDialog` (this pass's own new code) | Implemented, unit-tested at the console/dispatcher layer; **NEEDS-LIVE** for the engine call itself |
| The full `startdialog` console round trip, and whether it actually reaches `k_sta_bastila.dlg` | Implemented; **NEEDS-LIVE** |

### Live test plan (NOT yet run)

One `tools/demo.ps1` instance is enough (no sync channel involved — this is a bare engine-call
verb, like `openstore`/`pazaak`). `pwsh src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError`
first.

1. `warp sta_m45ac` (the Star Forge shield-generator floor — `star-forge-finale.md` §3.3 names
   Bastila's chamber here, door tags `k45_door_bast1`/`2`).
2. `census`/`resolve` to confirm `sta_invis_bast_conv` (the task's own named tag — an invisible
   conversation-owner placeholder, matching how `k_psta_door.ncs`'s own `ActionStartConversation`
   call is scripted per §3.3) is actually present and resolvable on this save; substitute
   whatever the real GIT tag turns out to be if it differs (the same "confirm against the real
   save, don't trust a doc's own earlier guess" discipline `store-sync.md`'s own `Larrim_Store`
   correction already established).
3. `startdialog sta_invis_bast_conv k_sta_bastila` — confirm: (a) the plugin log line
   `startdialog: ActionStartConversation queued on the player, targeting '...' (dlg
   'k_sta_bastila')`, (b) within a frame or two, `DialogueGateHook`'s own `StartDialog` tap
   fires (its existing log line), (c) the conversation genuinely appears on screen (a real node,
   real VO/text — not a silent no-op), (d) `census`/a live read confirms the LOCAL PLAYER's own
   `obj+0x34` is now non-null, matching the design's "player is the actor" choice.
4. `skipline`/`selectreply <n>` (both pre-existing verbs) to navigate the conversation once
   started, confirming it behaves like any other conversation this codebase already drives.
5. `startdialog <a tag with no NPC here>` — confirm a clean "no npc tagged '...' found here"
   refusal, not a crash.
6. `startdialog sta_invis_bast_conv` with NO resref — confirm it either starts the NPC's own
   default `Conversation` field (if that placeholder has one baked in) or refuses cleanly if it
   has none; either is an acceptable outcome, a crash is not.
7. Repeat step 3 while another conversation is already active, or while a modal screen (galaxy
   map, party select) is up, to see how the queued action behaves against those conditions —
   not traced this pass (the routine-table dispatch itself has no such precondition gate the
   way `ShowStoreGui`'s three checks do; whatever `AIActionDialogObject`/`BeginConversation`
   themselves refuse on is untested).

Watch for in the plugin log: `startdialog:` lines from `CheatController.Dialogue.cs`, any
`... refused (failure N)` (the failure code names which of `Odyssey.StartConversation`'s own
guards fired — 1 no VM, 2 routine table mismatch, 4 VM mid-script, 5 stack push failed, 6
dispatch returned nonzero, 9 stack not neutral after the call), and `DialogueGateHook`'s own
existing `StartDialog` log line for confirmation the real engine chain actually ran.
