# Client control lock — pin a co-op client to its own PC (research 2026-07-31)

Static RE of GOG retail `swkotor.exe` (base 0x400000, disk==memory, no ASLR).
Goal: on a co-op **client**, guarantee the local player can only ever drive their **own
PC** — never a shared companion — even if they click a companion portrait or press Tab.

## TL;DR

The single value that decides "who does this client's player input drive" is an **OBJECT
ID**, not a pointer: `party_member_datas[0].client_object_id`, i.e. the dword at

```
[ [ [0x007A39FC] + 4 ] + 0x270 ] + 0x24
   APP_MANAGER   app    internal  CSWParty*   member[0].client_object_id
```

Because every consumer re-resolves that id through `GetCreatureByGameObjectID`
(null-safe), **writing the client's PC object id there each frame is crash-proof** and
pins control to the PC. That is the external (WriteProcessMemory) lever. The cleaner
in-process lever is to early-return the switch handler so the reorder never happens.

Server-side `pt_leader_id`/`pt_controlled_npc` (party table +0xEC/+0xF0) are **slot
indices, not object ids, and are NOT the client input gate** — do not use them for this.

---

## The control gate (VERIFIED)

`APP_MANAGER_PTR = 0x007A39FC` → app object; `CClientExoAppInternal = [app+4]`.

`CClientExoAppInternal::GetControlledCharacter` (0x005ED8B0) is just:

```
mov eax,[ecx+4]        ; internal
mov eax,[eax+0x270]    ; return internal+0x270
ret
```

`internal+0x270` is named in the DB and confirmed by the code below:

| field | offset | type | meaning |
|---|---|---|---|
| `CClientExoAppInternal+0x20` | player_creature_id | **object id** | the client's PC (stable) |
| `CClientExoAppInternal+0x270` | party | **CSWParty\*** | the client party/control mirror |
| `CClientExoAppInternal+0x3BC/0x3C0` | cached id / cached_creature\* | id + **pointer** | PC resolve cache — do NOT write |

`internal+0x270` is created **once** in the constructor (0x005FBFE0, store at 0x005FC4AF)
and never swapped — it is a stable sub-object, so a client can read it every frame without
a lifetime race. (VERIFIED: it is the only non-constructor path; all other +0x270 writes in
the binary belong to unrelated classes that reuse the offset.)

### CSWParty layout (VERIFIED — DB + disasm)

```
CSWParty+0x00   party_count            int
CSWParty+0x24   party_member_datas[3]  CSWPartyMemberData, stride 0x88
  CSWPartyMemberData+0x00  client_object_id  (ulong)   <-- the creature's object id
```

So `member[i].client_object_id = [party + 0x24 + 0x88*i]`, and **member[0] is the
controlled character / leader** — the creature the player drives.

### All player input resolves member[0], by id (VERIFIED)

`GetControlledCharacter` has **244 direct call sites** (input, movement, camera, targeting,
GUI). Two resolvers turn the returned CSWParty into the driven creature, both keyed on the
`client_object_id` at member[0]:

- `CSWParty::GetControlledCreature` (0x00635460), used by `ChangeCharacter`:
  `cmp [party],0` (party_count>0) → `mov eax,[party+0x24]` (member[0].client_object_id) →
  `GetCreatureByGameObjectID` (0x005ED5B0 → 0x005F1180).
- `CSWParty::GetMember(idx)` (0x006346C0): `[party + 0x88*idx + 0x24]`; the input dispatch
  at 0x004EF840 loops `idx = 0..party_count` and resolves each.

`GetCreatureByGameObjectID` (0x005F1180) is a null-safe map lookup (default 0), and every
caller null-checks. **This is why an id write is benign: a wrong-but-invalid id resolves to
null and is ignored, never dereferenced blindly.**

`player_creature_id` (`internal+0x20`) is likewise an object id — `GetPlayerCreature`
(0x005F1420) compares it against a cache key at +0x3BC and, on miss, re-resolves via
`GetCreatureByGameObjectID`, caching the pointer at +0x3C0. So +0x20 is an id; +0x3C0 is a
resolved pointer (never write it).

---

## What a client "switch control" action does (VERIFIED)

Switching control **reorders the member array** so the target becomes member[0]. Entry
points a client player can hit:

| trigger | chain |
|---|---|
| Tab / cycle party | `CClientExoApp::ChangeCharacterToNextLivingPartyMember` 0x005EDF80 → internal 0x005F7960 → ChangeCharacter |
| portrait click | `CGuiInGame::ChangeCharacter` 0x0062B0F0 → `CClientExoApp::ChangeCharacter` 0x005EDC30 → **internal `ChangeCharacter` 0x005F6B60** |
| script / VM | `ExecuteCommandSwitchPlayerCharacter` 0x00544910, `CSWPartyTable::SwitchPlayerCharacter` 0x005667C0 |

`CClientExoAppInternal::ChangeCharacter` (0x005F6B60) is the common GUI/keyboard funnel. It
calls `CSWParty::SetControlledCreature/SetLeader` (0x00635480), which manipulates the member
blocks (reads member[0]@+0x24 and member[1]@+0xAC = +0x24+0x88). Crucially, **ChangeCharacter
does NOT call `SetPlayerCreature` (0x005F2BB0)** — verified by scanning its 400-byte body —
so `internal+0x20 player_creature_id` stays the PC across any switch. (`SetPlayerCreature`,
called only at character load, is the sole writer of +0x20 and, when a party exists,
propagates the PC into the controller via 0x006364C0.)

---

## Recipe A — external (WriteProcessMemory), per-frame  [PREFERRED for the agent]

Every frame (or every ~50 ms):

```
app      = read_u32(0x007A39FC)
if app == 0: skip
internal = read_u32(app + 4)
party    = read_u32(internal + 0x270)
if party == 0: skip
write_u32(party + 0x24, clientPcId)      # member[0].client_object_id = my PC
```

`clientPcId` source, in order of preference:
1. **The mod's own PC object id recorded at the co-op connect handshake** (source of truth).
2. `read_u32(internal + 0x20)` (`player_creature_id`) — equals the client's PC and is stable
   across switches (see above). Good fallback when the handshake id is unavailable.

**Why it cannot crash (VERIFIED):** the written value is an object id. Every input consumer
resolves it through `GetCreatureByGameObjectID`, which returns null for an unknown id and is
always null-checked. No raw creature pointer is written or dereferenced from this field, so
there is no stale-pointer hazard. Worst case from a transiently-wrong id is one frame of
"resolves to null / no-op," self-corrected on the next write.

**Why it cannot break normal PC control (VERIFIED):** member[0]==PC is exactly the engine's
own "you control your PC" state. Pinning it to the PC id is a no-op while the PC is already
the leader, and reverts a companion switch on the next tick — the client keeps full control
of its own PC throughout.

**Caveat (INFERRED / NEEDS-LIVE):** this write only fixes member[0]'s *id*; it does not swap
the rest of member[0]'s 0x88 `CSWPartyMemberData` block. After a client-attempted switch the
portrait / ability bar may briefly show the companion (~1 frame) until the id revert lands —
cosmetic only; input authority follows the id immediately. Confirm live that no per-frame
creature cache keeps driving the companion after the id flips (expected not, since 0x635460
re-resolves every call).

Do **NOT** write `internal+0x3C0` (cached_creature) or any resolved `CSWCCreature*` — those
are pointers and a stale write there would be dereferenced → crash.

## Recipe B — in-process plugin  [CLEANEST]

Prevent the reorder instead of reverting it. Hook `CClientExoAppInternal::ChangeCharacter`
(0x005F6B60): when running as a client and the requested target != own PC, early-return
before `CSWParty::SetControlledCreature` (0x00635480) runs. Also guard the Tab funnel
`ChangeCharacterToNextLivingPartyMember` (0x005F7960). This blocks the switch entirely —
zero block corruption, no portrait flicker, no per-frame poke — and member[0] never leaves
the PC, so the client's control of its own PC is untouched. (INFERRED strong; NEEDS-LIVE to
confirm the early-return leaves GUI state consistent.)

---

## Solo-mode and party-table interactions

- **Solo mode** (server `CSWPartyTable+0x190`, see `party-reconciliation.md`) freezes
  companions' follow-AI. It is **orthogonal** to this control gate: setting solo mode does
  NOT stop a client from *selecting* a companion. You still need Recipe A or B to lock
  input. Conversely, locking control does not require touching solo mode.
- **`pt_leader_id` (+0xEC) and `pt_controlled_npc` (+0xF0)** in the server `CSWPartyTable`
  are **slot indices (0..8) / -1**, not object ids (corrected in `party-reconciliation.md`),
  and they live on the server table, not the client `CSWParty`. They are **not** what client
  input reads. Do not use them for the client control lock.
- **id vs pointer, resolved:** the gate is an id (`CSWParty` member `client_object_id`), so
  id-writes alone DO work — no pointer write is ever needed for this lock. This is the safe
  case the task hoped for.

---

## Verification tags

- VERIFIED (disasm + DB): gate = `[[[0x7A39FC]+4]+0x270]+0x24` = member[0].client_object_id
  (object id); GetControlledCharacter/244 sites resolve member[0] by id via null-safe
  GetCreatureByGameObjectID; CSWParty layout; switch funnels; ChangeCharacter reorders member
  blocks and does not touch `internal+0x20`; +0x20 is an id, +0x3C0 is a pointer.
- INFERRED (strong): per-frame id re-pin reverts input within one frame; in-process
  early-return blocks the switch cleanly; +0x20 remains the client's own PC in co-op.
- NEEDS-LIVE: confirm no per-frame creature cache survives the id flip; confirm the cosmetic
  portrait/ability desync is ≤1 frame and self-heals; confirm the in-process early-return
  leaves GUI consistent.

---

# The lock that outlived the session (implemented 2026-08-15)

Reported from play: *"when disconnecting, the Trask player is returned to the client, but
when attempting to click on it, it's not doing anything."* Trask was back in the party and
completely inert — and so was Tab, and so were the other two portraits.

## The cause: one level nobody lowered

Recipe B was implemented not as a hook but as a **byte patch** on the same funnel, in
`DeathController` (see `death-handling.md`): five bytes at `ChangeCharacter` (`0x005F6B60`)
jump to a stub that tests a flag word and, when set, executes `ret 8` before the prologue.
That covers Tab, all three portraits and the engine's death auto-swap in one place, which is
exactly what was wanted.

The flag is a **level**, mirrored out of the shared block into the stub's word every single
frame by `DeathController.OnFrame`:

```
_flags[FlagSuppressSwap] = block.SuppressControlSwap ? 1 : 0;   // block 0x1030
```

and the agent raises it, once, at the first death pump of a client session
(`CoopAgent.PumpDeath` → `SetDeathSuppression(suppressGameOver: !allDown,
suppressSwap: !session.IsEnemyAuthority)`). It is never lowered again while connected —
correctly, since a client must never take a host-owned companion.

**`KotorGameBridge.ClearCoopState()` did not clear it.** It cleared the peer count, the two
presence counts, the dialogue flag, the encounter fields and the command — and nothing else.
So on disconnect the word stayed at 1, `DeathController.OnFrame` (which has no command gate,
deliberately) kept writing 1 into the stub every frame, and `ChangeCharacter` returned
immediately **for the rest of the process**. Nothing about the companion was wrong; the
click had nowhere to go.

Three more agent-written levels were leaking through the same gap, each with its own visible
symptom on a disconnected client:

| block | field | what it kept doing |
|---|---|---|
| `0x102C` | game-over suppression | the death screen stayed swallowed — a solo party wipe just lay there |
| `0x1358` | peer interaction locks | `InteractionLockController` (no command gate either) kept calling `ClearAllActions` on every locked creature ~6×/s: an NPC that ignores every order |
| `0x3304` | peer party roster | `PartyRosterController` kept reconciling the local party to the host's last roster — dismiss a companion after disconnecting and it is put straight back |

And one leak on the plugin side with no block field at all: `PartyPresenceController` holds
mirrored companions in the **Neutral faction** and pinned at **min-1-hp** under
`local_combat`, and its own comment claimed "this instance ceasing to mirror puts them back
in the player faction on the next pass". It does not — that restore is behind the same
command gate that stops the pass running. Both are SAVED state.

## The fix: teardown is a list, and the command's falling edge is a release

Two halves, deliberately symmetric.

1. **`KotorGameBridge.SessionScopedBlockLevels`** — every agent-written LEVEL that belongs to
   a session rather than to the process, named, in the order `ClearCoopState` lowers them.
   Only levels: serials (travel, journal, revive, chat) are edge-compared against an applied
   counter, and zeroing one re-fires the last event rather than cancelling it. `BlockCommand`
   is **last**, because its falling edge is what runs the plugin's own release, and that
   release must not read a block still full of the previous session's levels.
   `SessionTeardownTests` pins the four fields above by offset, and pins the ordering.

2. **`PluginHost.PumpSessionLifecycle`** — the one place a session is undone in-process. It
   watches `block.Command` and, on the falling edge, calls `ReleaseSession()` on every
   controller that holds engine or GUI state, in a fixed order: spectate camera and control
   return first (before `PhantomController` starts destroying bodies), then the companion
   bodies, then the suppressions, locks, roster and portraits. `DeathController.ReleaseSession`
   zeroes the stub words directly, so a player whose agent DIED without tearing down still
   gets their portraits back.

The rule that follows from it: **anything a controller does to the world while a session is
live belongs in that controller's `ReleaseSession`** — never in a second copy of the list.

## The other half: control came back, the camera did not

Recipe A reverts control by writing an object id from another process. The engine's own
camera handoff is **inside** `ChangeCharacter` — the `SetCameraFollow` at `0x5F6C27`
(`spectate-camera.md`) — so an external id write does not go through it.

Consequence, and it is exactly the case the report asked about: a player who is playing AS
Trask at the moment they **connect** (or when the unattended auto-reconnect in `CoopClient`
fires) gets their PC's controls back with the camera still bolted to Trask, and cannot fix
it, because the same session has just armed the patch that makes `ChangeCharacter` a no-op.
Tab dead, portraits dead, view stuck on a body the host now drives.

`ControlReturnController` closes it, as a **level, not a one-shot**:

- Runs only where the lock runs — `Command == SpawnAndDrive && DamageDealtEnabled`, the
  "this instance mirrors the world" predicate `PortraitController` already uses. A host owns
  its companions and is untouched.
- Notices when the controlled creature's id is one of this instance's party roster bodies
  (`partyTable + 0x0C + 4*slot`, slots 0..8 — the whole roster, because at the instant of
  connect the companion presence has not arrived yet). It **logs the id it saw** and arms a
  hand-back; it does not move the camera then, because the controlled creature IS the
  companion at that point.
- When control lands back on the player's own character it calls
  `Odyssey.RestoreCameraToControlledCreature()` (the engine's own
  `CSWCModule::SetCameraFollow`) for a bounded budget of passes — bounded because that call
  silently no-ops while the client is in combat mode, so one call lands or is lost with no
  way to tell which, and because a permanent re-assert would make this the camera's owner.
- Defers completely to dialogue and to the spectate camera (`IsConversationActive`, or the
  controlled creature being down), and **keeps** the budget across the deferral so a
  hand-back owed when a conversation opened still happens after it closes.
- Writes no control id of its own. That lever is the agent's, at ~30 Hz; a second writer of
  the same field in a second process is how two halves come to disagree about who the player
  is. This half owns the camera only.

The decision is `ControlReturnRule`, pure and unit-tested (`ControlReturnRuleTests`), the
same split `SpectateCycle` uses. Kill switch `control_return=0`.

Fail-safe direction is fixed: **the player controls their own PC**. The worst case if the
hand-back cannot be made is the stock camera the engine restores at the next area attach —
never a client driving a shared body.

`SpectateController.ReleaseSession` is part of the same story and is not cosmetic: its
`OnFrame` is command-gated, so a disconnect while spectating simply stopped it running, and
the installed `CAurBehaviorCameraFollow` keeps the followed client creature as a raw pointer
at `+0x14` which it reads every frame — while `PhantomController` despawns exactly that body,
one per frame, starting immediately.

## Verification tags (this section)

- VERIFIED (source + the reported symptom): the swap suppression is a block level, mirrored
  into the stub every frame, raised once per client session and not cleared by teardown.
- VERIFIED (disasm, unchanged from above): `ChangeCharacter 0x005F6B60` is the single funnel;
  `SetCameraFollow 0x0063F9D0` is called from inside it at `0x5F6C27`.
- NEEDS-LIVE: that the portraits and Tab answer again after `disconnect`; that a companion
  mirrored under `local_combat` reads faction PLAYER after a disconnect; that connecting
  while controlling Trask ends with the camera on the PC.

## Key addresses

```
0x007A39FC  APP_MANAGER_PTR ([+4] = CClientExoAppInternal)
0x005ED8B0  CClientExoAppInternal::GetControlledCharacter   -> internal+0x270 (CSWParty*)
0x005F1420  CClientExoAppInternal::GetPlayerCreature        (reads/caches +0x20 id)
0x005F2BB0  CClientExoAppInternal::SetPlayerCreature        (writes +0x20; sole writer)
0x00635460  CSWParty::GetControlledCreature                 ([party+0x24] id -> resolve)
0x006346C0  CSWParty::GetMember(idx)                        ([party+0x88*idx+0x24])
0x00635480  CSWParty::SetControlledCreature/SetLeader       (reorders member blocks)
0x005F1180  CClientExoAppInternal::GetCreatureByGameObjectID (null-safe id -> creature)
0x005F6B60  CClientExoAppInternal::ChangeCharacter          (GUI/keyboard switch funnel)
0x005F7960  CClientExoAppInternal::ChangeCharacterToNextLivingPartyMember (Tab)
0x00544910  ExecuteCommandSwitchPlayerCharacter
0x005667C0  CSWPartyTable::SwitchPlayerCharacter
```
