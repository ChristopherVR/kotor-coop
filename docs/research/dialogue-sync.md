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
