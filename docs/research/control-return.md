# Control-return regression: locked onto Trask at connect (2026-08-22)

Reported from play: a client who was already controlling Trask (Tab'd/portrait-clicked onto him
as the active party leader, in single-player, before connecting) got LOCKED onto Trask after
connecting — the exact opposite of what `control_return`/`ControlReturnController.cs` promises
("if you were playing as a companion when you connected, control and the camera come back to
your own character"). This is the scenario `client-control-lock.md`'s own NEEDS-LIVE list named
and never got to: *"connecting while controlling Trask ends with the camera on the PC."*

Root-caused statically (no live run performed by this pass — see `tools/control-return-live.ps1`
below for the script that closes the NEEDS-LIVE tag next round).

## The write sequence on connect (client, mirroring)

Two processes, one plugin frame:

| # | Where | What | File:line |
|---|---|---|---|
| 1 | Agent, ~30 Hz (`PollRate`) | `LockControlToPlayer()` reverts an explicit party-NPC control-id back to the PC | `src/KotorCoop.Memory/KotorGameBridge.cs:5406` |
| 2 | Plugin, every frame | `PartyPresenceController.OnFrame` mirrors companions, skipping whichever creature `== player` (the one still under direct control) | `src/KotorCoop.Plugin/PartyPresenceController.cs:171,310` |
| 3 | Plugin, every frame | `PartyRosterController.SyncClientPartyTogetherState` clamps `CSWParty`'s own member count down to `CSWPartyTable`'s | `src/KotorCoop.Plugin/PartyRosterController.cs:152` |
| 4 | Plugin, every 30 frames | `PartyRosterController.Reconcile` adds/removes companions to match the host's roster | `src/KotorCoop.Plugin/PartyRosterController.cs:231` |
| 5 | Plugin, every 10 frames | `ControlReturnController.OnFrame` notices a session-owned companion is controlled, then hands the camera back once control is | `src/KotorCoop.Plugin/ControlReturnController.cs:70` |

Frame order inside `PluginHost.OnFrame` (`src/KotorCoop.Plugin/PluginHost.cs:828-869`):
`_partyPresence` → `_partyRoster` → … → `_controlReturn` (last). Step 5 running last is correct
by design — it wants the freshest state. The bug is not a same-frame ordering mistake at all;
it is step 3 corrupting the very struct step 5 (and the engine's own control resolution) needs
intact, for as long as the player is still driving the companion.

## The bug: the "last member" the clamp trims is not always a companion

`SyncClientPartyTogetherState`'s clamp exists to fix a real, different, LIVE-VERIFIED bug: on a
client, `CSWParty` (the client-side struct `CSWParty::IsPartyTogether` reads) counts the PC
*and* its companions, while `CSWPartyTable` counts companions only — so `CSWParty` always reads
one higher, `IsPartyTogether` always refuses, and every door transition failed on a client. The
fix, `Odyssey.TryClampClientPartyCount`, trims `CSWParty`'s own count field down to
`CSWPartyTable`'s (`PartyRosterController.cs:174-192`).

That trim removes whichever member sits at the END of the array — `member[count-1]` — without
regard for WHO occupies it. In the ordinary case the array is ordered `[PC, companion, …]`
(a real engine control switch reorders the array so the driven creature becomes `member[0]`;
see `docs/research/client-control-lock.md`'s "What a client 'switch control' action does"), so
"the last one" is always a companion and trimming it is exactly the harmless fix it was written
for.

But a client who was already controlling Trask **before** connecting has the array the other
way round: `[Trask, PC]`. "The last one" is now the PC's own slot. The same clamp that makes a
door crossable, applied at exactly this moment, trims the PC's own slot out of the struct that
both the engine's own control resolution and `ControlReturnController`'s camera hand-back need
intact — and there is nothing left to switch back into. The clamp runs unconditionally, every
single frame the roster channel is on (`SyncClientPartyTogetherState` is called at the TOP of
`PartyRosterController.OnFrame`, before the countdown-gated reconcile), so it re-corrupts the
struct every frame for as long as the player is still driving the companion — a level, not a
one-shot, exactly the shape that outlasts a single lucky frame.

This is not a hypothetical reading of the code. `SyncClientPartyTogetherState`'s own doc comment
already describes the SAME defect from the HOST's side, found and fixed the SAME day
(`b491b74`, 2026-08-22): *"Clamping CSWParty to the table's count drops the LAST companion out
of the struct the selection UI, portraits and follow logic read — which on the HOST meant the
player lost control of their own companions."* That fix restricted the clamp to clients only
(`block.TryGetHostPartyRoster` is only ever valid there) — which is precisely what let the SAME
mechanism reach a client scenario it had never run against before: a client controlling a
companion. Restricting the blast radius to clients didn't remove the defect, it relocated it.

## The fix: defer, not disable

`PartyTogetherClampRule.ShouldClamp` (`src/KotorCoop.Plugin/PartyTogetherClampRule.cs`) adds one
precondition, pure and unit-tested:

```csharp
public static bool ShouldClamp(bool controllingSessionOwnedCompanion, int clientCount, int tableCount) =>
    !controllingSessionOwnedCompanion && clientCount > tableCount;
```

`PartyRosterController.SyncClientPartyTogetherState` now asks the same question
`ControlReturnController` already asks — "is the creature the player is driving one of this
instance's own party-roster companions" (`IsControllingRosterCompanion`, mirroring
`ControlReturnController.IsPartyRosterBody`'s whole-roster scan, 0..8, for the same reason: at
the instant of connect the companion presence has not arrived yet) — and skips the clamp
entirely while that is true.

This costs nothing in the ordinary case (the clamp is exactly as safe and exactly as necessary
as it always was the moment the answer is "no") and closes the regression in the one case that
mattered: while the player is still on the companion, `CSWParty`'s array is left alone, so
`ControlReturnController`'s own mechanism — the agent's per-tick control-id revert
(`LockControlToPlayer`, already live-verified: *"the write stuck and +0x3C0 returned to the
PC"*, `KotorGameBridge.cs:5382-5388`) plus the engine's own `SetCameraFollow` camera hand-back
(`Odyssey.RestoreCameraToControlledCreature`) — has an intact struct to land control back into.
The moment it does, `IsControllingRosterCompanion` reads false and the clamp resumes on the very
next pass: no cooldown, no extra state, because the ordinary case was always this safe.

## Host stays untouched (as required)

The whole method this lives in is already client-only: `SyncClientPartyTogetherState` returns
immediately unless `block.TryGetHostPartyRoster` reads valid, which per its own doc comment "is
only ever written for a client." A HOST controlling Trask at connect never enters this code path
at all — it keeps control by construction, the same way it always did. `ControlReturnController`
itself is independently client-only (`mirroring = block.Command == CommandSpawnAndDrive &&
block.DamageDealtEnabled`, the same predicate `PortraitController` uses to know "this instance's
companions belong to somebody else"). No change in this pass touches either gate.

## Tests

- `tests/KotorCoop.Tests/PartyTogetherClampRuleTests.cs` — the pure rule: the ordinary case still
  clamps, agreement never clamps, the clamp never lowers below the table count, and — the
  regression case — controlling a companion refuses the clamp even when the counts would
  otherwise trigger it, regardless of how stale the counts look.
- `tests/KotorCoop.Tests/ControlReturnRuleTests.cs` (pre-existing, unchanged, still green) — the
  camera hand-back's own ordering: driving a session-owned companion arms the hand-back without
  moving the camera yet; once control is back, the camera follows and spends one assert of a
  bounded budget; a host is left alone entirely.

## Live script

`tools/control-return-live.ps1` closes the NEEDS-LIVE tag `client-control-lock.md` left open.
Against an already-running `tools/demo.ps1` pair, it disconnects the client, prints the ONE
step that cannot be automated (injected keystrokes never reach this game —
`docs/live-verification.md` — so a human Tabs/portrait-clicks onto Trask), reconnects the client,
and reads the client's plugin log for the three-part verdict:

1. `control: you are driving companion 0x...` — `ControlReturnController` saw the claim.
2. No `party together: ... clamping ...` line landing between the claim and the hand-back — the
   regression's own signature, now refused by `PartyTogetherClampRule`.
3. `control: putting the camera back on your own character (0x...)` — control-return landed.

```
pwsh tools\control-return-live.ps1
pwsh tools\control-return-live.ps1 -Game1 'C:\GOG Games\Star Wars KOTOR (GOG)' -Game2 'D:\Games\swkotor-gog-p2'
```

## Verification tags

- VERIFIED (source + git history): the clamp's own doc comment names the identical defect on
  the host, fixed the same day the client-only restriction was introduced (`b491b74`); the
  frame order in `PluginHost.OnFrame`; `SyncClientPartyTogetherState` runs every frame,
  unconditionally, before the countdown-gated reconcile.
- INFERRED (strong, from the address-cited disassembly in `client-control-lock.md`): a real
  engine control switch reorders `CSWParty`'s member array so the driven creature becomes
  `member[0]`, which is what puts the PC at the END of the array in this exact scenario.
- NEEDS-LIVE, still open after the 2026-08-22 live pass below — see that section for exactly
  what remains and why.

## Live pass (2026-08-22)

Ran `tools/control-return-live.ps1` against a live `tools/demo.ps1` pair (END_M01AA, Trask
recruited). Three of the four scenarios closed; the fourth — the regression itself — did not,
and tracing why it did not is the actual finding of this pass.

### The `leader <slot|pc>` verb

Built to remove the one manual step the script could never automate (`docs/live-verification.md`:
injected keystrokes do not reach this game). Follows `applyeffect`'s wiring exactly:
`ConsoleCommand.Parse` → `CoopCommandDispatcher.Leader` → `CheatRequest(CheatKinds.Leader, …)` →
`CheatController.Leader` → `Odyssey.SwitchControlToPartySlot`/`SwitchControlToPc`
(`src/KotorCoop.Plugin/Odyssey.cs`).

**Trask is party-roster slot 0 in `end_m01aa`, LIVE-CONFIRMED via `partystate.ps1`** — not slot 1
as this round's own brief assumed. `phantom-locomotion.md` §14.3 already recorded this once;
this pass re-confirmed it directly (`partystate.ps1 -ProcId <client>` → `member 0: slot 0
objectId 0x127`).

The verb's write mechanism went through two live-verified revisions:

1. **First version — id write only, `CClientExoAppInternal + 0x20`.** The reverse of
   `KotorGameBridge.LockControlToPlayer`'s own per-tick revert. A raw-memory read tool
   (independent of the plugin) confirmed the write lands and **holds stable while
   disconnected** — 2.5+ seconds, no drift, exactly as `LockControlToPlayer`'s own "toggling
   control (Tab) and watching it flip" claim predicts for this field. But `leader 0` followed
   by `reconnect` still produced no `ControlReturnController` claim across a 30 s window.
2. **Second version — also writes `CSWParty`'s member[0] client-space id (`party + 0x24`),
   client-control-lock.md's own "Recipe A" for the reverse direction.** Tracing
   `Odyssey.GetPlayerCreaturePointer` (`ControlReturnController` and
   `PartyRosterController.IsControllingRosterCompanion` both key off it) via
   `tools/disasm.py` found it does **not** read `internal + 0x20` at all — it is
   `CServerExoApp::GetPlayerCreature` (0x004B2820), a chain that resolves through
   `CServerExoAppInternal` (`0x4B2820 → 0x4AEA40 → 0x4B4CB0 → 0x4AE920 → 0x4B2AE0`), not through
   the client-side `CSWParty` `GetSWPartyFn` resolves either. So this write was added too —
   member[0]'s id only, not the full 0x88-byte block or member[1]'s (the doc's own caveat:
   cosmetic only, never a crash risk since nothing written is a pointer). Live-confirmed via
   the same raw-read tool: after `leader 0`, `party.member0.client_object_id` reads
   `0x80000127` (Trask, correctly flagged) and holds. **Still no claim.**

Both writes are shipped (Odyssey.cs's `SwitchControlToPartySlot`/`SwitchControlToPc`) because
both are real, safe, useful primitives in their own right — `internal+0x20` is the exact field
a real Tab press moves for input authority, and `party+0x24` is the exact field `IsPartyTogether`
and the clamp read. Scenarios 2 and 3 below exercise them successfully. The finding is narrower
than "the verb doesn't work" — it is that **neither of the two documented, safe, id-only control
fields is what `GetPlayerCreaturePointer` actually reads.**

### What `GetPlayerCreaturePointer` reads instead (traced, not fully resolved)

`0x004B2AE0`'s body (dumped via `tools/disasm.py 0x4B2AE0 0x80`) switches on a small integer
selector and, on the branch this call site takes, walks what reads like a bucket/list at
`this + 0x10064`, comparing each node against a wanted value at `[node + 4]` — the shape of a
"find the object flagged X" search, not an index into `CSWPartyTable`'s roster array or a read
of `pt_leader_id`/`pt_controlled_npc` (`+0xEC`/`+0xF0`, the fields `client-control-lock.md`
already ruled out for the client input gate). Neither `internal+0x20` nor `party+0x24` is
anywhere in that chain. This pass did **not** chase the write site further — the obvious next
step, calling `CClientExoAppInternal::ChangeCharacter` (0x005F6B60) directly, was disassembled
(`tools/disasm.py 0x5F6B60 0x100`) but its own stack-argument accounting did not resolve
cleanly against `CSWParty::SetControlledCreature`'s (0x00635480) apparent read at a THIRD stack
offset — enough of a mismatch in the arithmetic that guessing the calling convention live,
against a real running game process, was judged not worth the crash risk for a dev-only test
primitive. This is the honest state of the RE, not a finished result.

**Side finding, unexplained, flagged for its own round:** `topup`/`Odyssey.GetPlayerCreaturePointer`
report "0 max hit points" on BOTH instances in this exact session, regardless of Tab/`leader`
state — while the agent's own independent `where` reading shows `hp 12/12` for the same
character at the same moment. Whatever `GetPlayerCreaturePointer` resolves to in a live co-op
session, it is possibly not the real PC even in the ordinary, nobody-switched-anything case.
This is orthogonal to the `leader` verb (reproduced with it never having been called) and may
be the real explanation for why `ControlReturnController` never claims — a NEW open question,
not the one this round set out to answer.

### The four scenarios

1. **Client controls Trask, then reconnects — THE regression.** FAIL: no claim, no hand-back,
   across every attempt this pass made (`leader` v1, `leader` v2, in a fresh rig after
   redeploying the fix). Root cause traced above; not closed this pass.
2. **Host controls Trask, client reconnects — host untouched.** PASS, live. `leader 0` on the
   host answered "control switched to party slot 0 (0x127)"; across the client's
   disconnect/reconnect the host's plugin log logged **zero** `control:` lines in either
   direction — matching source (`ControlReturnController`'s mirroring gate is client-only;
   `CoopAgent.PumpOnce` only calls `LockControlToPlayer` when `!session.IsEnemyAuthority`,
   which a host never is).
3. **Client on its own PC, disconnects — no-op.** PASS, live. No `control: you are driving
   companion` claim logged at all across the disconnect.
4. **`control_return=0` opt-out.** NOT live-restarted this pass (the flag is `readonly`, read
   once at construction — a live toggle needs a fresh process, which this pass's time budget
   did not extend to). Verified by source only: `ControlReturnController.OnFrame`'s first line
   is `if (!_enabled) { return; }`.

### Net effect on the fix's status

`PartyTogetherClampRule`'s own logic is unchanged and still fully unit-tested
(`PartyTogetherClampRuleTests`) — nothing in this pass casts doubt on "defer the clamp while
`IsControllingRosterCompanion` is true" being the right rule IF that predicate is ever true on a
client. What this pass could not establish live is whether it ever becomes true through any
means short of the engine's own `ChangeCharacter` call — which includes a REAL Tab press, the
scenario the bug report was actually about. So the regression's fix remains statically sound and
now MORE PRECISELY unverified: not "did the fix run," but "does the trigger it fixes ever
actually fire on a client." Scenarios 2-4 close cleanly and are no longer NEEDS-LIVE.

### Verification tags (this pass)

- LIVE-VERIFIED 2026-08-22: scenarios 2 and 3 above; `internal+0x20` and `party+0x24` both hold
  stable after a `leader` write (raw-memory read tool, independent of the plugin); Trask is
  party-roster slot 0 in `end_m01aa`.
- VERIFIED (disasm, this pass): `GetPlayerCreaturePointer`'s full call chain from
  `CServerExoApp::GetPlayerCreature` (0x004B2820) down to the bucket walk at `0x4B2AE0`; neither
  `internal+0x20` nor `CSWParty`'s member array appears in it.
- NEEDS-LIVE, narrowed: scenario 1 (the regression itself) and scenario 4
  (`control_return=0`) — scenario 1 needs either a real human Tab press or a correctly
  reverse-engineered `ChangeCharacter`/`SetControlledCreature` call (not attempted live this
  pass, crash risk); scenario 4 needs a fresh process with the flag set. The "0 max hit points"
  finding above is a NEW open question a future round should chase first — it may be upstream
  of scenario 1's own failure to converge.

---

## Round 70 (2026-08-22)

Closes the regression live. Four parts: why detection never fired, what the 0-max-HP finding
actually was, the full disassembly of the engine's own switch function (and why calling it did
not work), and the working fix that replaced it.

### Part 1 — disassembling `CServerExoApp::GetPlayerCreature` (0x004B2820) fully

`tools/disasm.py`/`tools/xrefs.py` against the GOG binary, address by address:

```
0x004B2820  CServerExoApp::GetPlayerCreature(this)
    return CServerExoAppInternal_0x4B1790(internal, CServerExoAppInternal_0x4B4CB0(internal))
    ; internal = [this+4]; both callees reached via thunks (0x4AEA40 / 0x4AE770: mov ecx,[ecx+4]; jmp)

0x004B4CB0  "get player-slot-0's registered PC id, resolved and validated"
    eax = [0x007A39FC]                      ; APP_MANAGER_PTR (SAME global client-control-lock.md
                                             ; already names for the client-side control gate)
    ecx = [eax+8]                           ; ecx = CServerExoApp (the SAME "this" GetPlayerCreature
                                             ; was called with — this function ignores its own `this`
                                             ; parameter and re-derives from the global instead)
    esi = CServerExoAppInternal_0x4B2AE0(ecx, wanted=0, selector=1)
        ; walks a bucket/linked-list at internal+0x10064 (selector 1) or +0x10068 (selector 2),
        ; via two helper calls (0x5E9B60 "get value from node", 0x5E9B90 "advance"), for the node
        ; whose [+4] field equals `wanted` (0) — i.e. "find the connected-player entry for
        ; player index 0". This is the SERVER's own list of connected human players, unrelated
        ; to CSWParty (the client-side control/leader struct) entirely.
    if esi == 0: return 0x7F000000 (ObjectInvalid sentinel)
    playerObj = CServerExoApp::GetGameObject(server, [esi+0x28])   ; via 0x561080, which ALSO
        ; type-checks the resolved object's tag byte against a threshold (`ja`, not `==` — a
        ; range check, not the object-type-trap's exact-match hazard)
    return playerObj != 0 ? [playerObj+4] : 0x7F000000             ; the object's OWN id field,
        ; re-read from the resolved object rather than trusted from the table

0x004B1790  "resolve an object id to a pointer, then type-check and vtable-confirm it as a
             creature" — internal+0x1005c hashtable lookup (0x4D8230, the SAME
             bit31-flag/low-12-bits hash scheme client-control-lock.md's Recipe A already
             documents for the CLIENT side) → type-tag byte compare → virtual dispatch at
             vtable+0x30 (an "AsCreature" confirm, matching the object-type-field-trap's own
             warning: do not read the type byte directly, use the vtable)
```

**VERIFIED, this round's own disassembly.** `GetPlayerCreature` resolves a FIXED entry
("player index 0") in the SERVER's own connected-players list — a completely different
structure from `CSWParty` (the client-side control/leader struct `ChangeCharacter` mutates) —
and reads that player's own registered PC id. Nothing in this chain is sensitive to which
creature the player is currently DRIVING; it is built to answer "who is the human behind this
connection", and it answers that correctly and consistently regardless of Tab, `leader`, or any
other control switch. This is not a bug in `GetPlayerCreature` — it is the wrong function for
"what am I driving right now", which is exactly what `ControlReturnController` and
`PartyRosterController.IsControllingRosterCompanion` were asking it. See client-control-lock.md
for the field that DOES answer that question (CSWParty member[0], `party+0x24`).

### Part 2 — the 0-max-HP finding, explained

Live-reproduced first, cleanly, on a fresh rig, both instances, before any `leader` call: `cheat
topup` on the local player answers `0 max hp (obj 0x7FFFFFFF vtable 0x7470C8 cur 12)`, while
`console 'where'` (the agent's own, completely independent resolution) answers `hp 12/12` for
the same moment. The resolved object has the correct creature vtable (`0x7470C8`,
`creatures.ps1`'s own known-good value) and the correct current hp (12, matching the agent
exactly) — so `GetPlayerCreaturePointer` resolved the RIGHT object, not a wrong one. Its id,
`0x7FFFFFFF`, is not garbage either: it is this codebase's own extensively-documented "current
player" sentinel (`docs/re/object-model.md`: "Player object id is exactly 0x7FFFFFFF"; dozens
of confirmed uses across `shared-state-audit.md`).

The ONLY genuinely surprising field is max hp reading 0 — and `docs/research/min-one-hitpoint.md`
already recorded the IDENTICAL reading, three weeks earlier, for the SAME module this rig always
loads (`end_m01aa`, Endar Spire): `sethp.ps1 -ObjectId 0x7FFFFFFF` → `hp 12 (stored max 0)
[min1hp]`. That doc traces the cause fully: `end_m01aa/k_pend_area01`'s own `OnEnter` script
calls `SetMinOneHP(GetFirstPC(), TRUE)` (NCS-disassembly-confirmed at offset 0x11AC), and while
that pin holds the engine stores the PC's max hp as 0. **`GetPlayerCreaturePointer` needed no
fix.** The round-69 finding was real and reproducible, but it was KOTOR's own min-one-HP
behavior on this specific level, not a resolution defect — a case of not cross-referencing this
same repo's own prior work rather than a new bug. (`TopUp`'s diagnostic detail was still worth
keeping: it now names the resolved object's id/vtable/current-hp on a 0-max-hp refusal, logged
via `PluginHost.Log` rather than the block's 64-byte detail field, which silently truncates
anything this long.)

### Part 3 — the engine's own switch, fully disassembled, calling convention confirmed, but not

Followed the GUI/keyboard path from the top, per the brief: Tab →
`ChangeCharacterToNextLivingPartyMember` (0x005EDF80/0x005F7960); portrait click →
`CGuiInGame::ChangeCharacter` (0x0062B0F0) → `CClientExoApp::ChangeCharacter` (0x005EDC30) →
both funnel into `CClientExoAppInternal::ChangeCharacter` (0x005F6B60,
client-control-lock.md's own prior finding, re-confirmed). Disassembled in full this round:

```
CClientExoAppInternal::ChangeCharacter(this, int arg1, int arg2)   ; thiscall, ret 8 (two stack
                                                                    ; dwords), only arg1 read
    edi = CSWParty::GetControlledCreature(party)      ; 0x00635460 — the OLD controlled creature
    CSWParty::SetControlledCreature(party, arg1)      ; 0x00635480 — THE REORDER
    eax = CSWParty::GetControlledCreature(party)      ; re-read
    if edi == eax: return                             ; no-op short-circuit — skips EVERYTHING
                                                        ; below, including the camera call
    ; ... (spectate/portrait bookkeeping) ...
    CSWCModule::SetCameraFollow(module, [new+4], 1)    ; 0x0063F9D0, ALWAYS reached unless the
                                                        ; no-op check fired
    internal+0x4B4 = 1                                 ; set ONLY past the no-op check — the
                                                        ; single cleanest "did this actually do
                                                        ; something" signal in the function
```

`CSWParty::SetControlledCreature(party, arg1)` (0x00635480) treats `arg1` as an INDEX into the
CURRENT member array (0..party_count-1), with two sentinels: `-2` reads member[1]'s id directly
(no bound check at all — confirmed via `xrefs.py 0x5F6B60`: `ChangeCharacterToNextLivingPartyMember`
itself pushes exactly `push 0; push -2` before this call, i.e. Tab's own real argument); `-3`
searches for the next living member other than the one currently controlled. The general
(index) branch bound-checks `arg1` against the party's live count field before using it — the
SAME bound `PartyTogetherClampRule`'s clamp writes (see Part 4). A second real call site
(the portrait path) confirmed the general branch is genuinely used in production: it computes
its argument via `CSWParty::FindMemberIndexById` (0x006349C0, disassembled and found to be
EXACTLY the bounded id→index search this file already needed — client-control-lock.md's id
gate, walked by array position instead of resolved by pointer).

**Calling it from this process did not work, in any live test this round ran, including with
the `-2` sentinel** (which bypasses the count bound check entirely, ruling out an
index/bounds mistake as the cause): `internal+0x4B4` stayed 0 and the array's raw bytes were
byte-for-byte unchanged before and after every call, live-verified by direct memory read
alongside the call. The calling convention itself was checked twice (both argument orderings
tried) with no change in outcome. Traced as far as ruling out (a) a wrong function address —
confirmed via the diagnostic's own logged pointer value; (b) wrong `this` — confirmed
`internal+0x270` (the party pointer `ChangeCharacter` itself dereferences) matches the SAME
value `GetSWPartyFn` independently resolves; (c) an index/bounds problem — the `-2` sentinel
bypasses bounds entirely and still had zero effect. The likely remaining explanation is an
exception somewhere inside `SetControlledCreature`'s own body (which DOES install its own SEH
frame at entry — `push -1; push handler; mov eax,fs:[0]; push eax; mov fs:[0],esp` — unlike
`ChangeCharacter` itself, which has none), silently unwound before the array write, for a
reason this round's tooling (no debugger attach onto the live process) could not isolate
further.

**NEEDS-LIVE, honestly unresolved**: whether `SetControlledCreature` can be called successfully
from this process at all, and if not, why. A future round with debugger access (breakpoint at
0x635480, single-step past the SEH prologue) should pick this up before trying anything else
here — the full disassembly above is the starting point, so nothing here needs re-deriving.

### Part 4 — the working fix: reproduce the same end state, id-only

Rather than leave the regression open on an unresolved native-calling mystery,
`Odyssey.RotateControlToFront` performs the SAME reorder `SetControlledCreature` was supposed
to — move the target to member[0], shift the displaced members down by one, preserving their
relative order — by direct, id-only memory writes. This is exactly Recipe A's own established
risk tier (client-control-lock.md: "VERIFIED crash-proof... every consumer resolves it through
the null-safe GetCreatureByGameObjectID"), just applied across the whole array instead of only
member[0]. The camera move is delegated to the engine's own, ALREADY-independently-working
`SetCameraFollowServerObject`/`RestoreCameraToControlledCreature` (both wrap the real
`CSWCModule::SetCameraFollow`, 0x0063F9D0 — the SAME call `ChangeCharacter` itself would have
made). `Odyssey.SwitchControlToPartySlot`/`SwitchControlToPc` (the `leader` verb's own
primitives) and `ControlReturnController`'s hand-back both go through this same pair of
functions now.

**One real interaction found and fixed along the way.** Live-probed directly (raw memory read,
independent of the plugin) after a plain `leader 0` on an already-connected client:
`party_together_fix`'s clamp (`PartyRosterController.SyncClientPartyTogetherState`) had already
reduced CSWParty's own count field to 1 (CSWPartyTable's companions-only count) — even though
Trask's full flagged id (`0x80000127`) was still physically sitting at member[1]. Since the
SAME count bound gates the engine's own index-based switch, this makes a companion
UNREACHABLE for a deliberate switch attempt while the clamp is active, independent of whatever
mechanism performs the reorder. The fix: `SwitchControlToPartySlot` first searches within the
live count (fast path, no write); if that comes up empty, it searches the FULL member-array
capacity (3 — the PC plus the server table's own two-companion cap) ignoring the count, and if
found there, raises the count to include exactly that one entry — never inventing data, only
acknowledging what the search already confirmed is really there. `PartyTogetherClampRule`'s own
deferral (now driven by the corrected `IsControllingRosterCompanion`, itself now built on
`Odyssey.GetControlledCreaturePointer`) takes over the very next pass once the switch has
landed, so nothing re-clamps it back down mid-operation.

`ControlReturnController`'s design changed accordingly: detection now reads
`Odyssey.GetControlledCreaturePointer` (CSWParty member[0]'s id — the field a real switch
actually moves) instead of `GetPlayerCreaturePointer`, and its `NoteClaim` branch now calls
`Odyssey.SwitchControlToPc()` directly — performing the array reorder and arming the camera
re-assert in the SAME pass the claim is logged, rather than logging a claim and waiting on the
external agent's per-tick field revert (`LockControlToPlayer`, a DIFFERENT field —
`internal+0x20` — that round 69's own tracing showed never touches the array at all).

### Live matrix (2026-08-22, this round, three redeploy cycles across the investigation)

`tools/control-return-live.ps1` against a fresh `tools/demo.ps1` pair (END_M01AA, Trask
recruited, host pid and client pid captured fresh per redeploy):

1. **Client controls Trask, then reconnects (the regression) — PASS.** Client plugin log, in
   order: `control: you are driving companion 0x127, which this session owns — handing you back
   your own character - array reordered and camera pointed at your PC`, then
   `control: putting the camera back on your own character (0x7FFFFFFF)`. Direct memory read
   before/after confirmed the array reorder lands SYNCHRONOUSLY with the claim log line (same
   call, `Odyssey.SwitchControlToPc` runs before the log statement) — the "hand-back" line that
   follows a fraction of a second later is the camera re-assert, not the array fix.
2. **Host controls Trask, client reconnects — host untouched — PASS.** Zero `control:` lines on
   the host's plugin log across the whole disconnect/reconnect window, matching source
   (`ControlReturnController`'s mirroring gate is client-only).
3. **Client on its own PC, disconnects — no-op — PASS.** No claim logged at all.
4. **`control_return=0` — LIVE-RESTARTED this round** (rounds 68/69 only verified this by
   source). Appended `control_return = 0` to the client's `coop.ini`, full rig teardown and
   relaunch (a fresh process, since the flag is `readonly` and read once at construction),
   disconnected, `leader 0`, reconnected. Direct memory read: `member[0] = 0x80000127` (Trask),
   held stable; the plugin log shows zero `control:` lines across the entire window. Reverted
   the config change and relaunched clean afterward.

`verify.ps1` check 19 ("the host still controls its companions") — **PASS**, run against this
round's final build: `table 1 companion(s), CSWParty 2 (PC+companions), companion 0x127 (npc
slot 0) commandable=1, host clamped this run: False`. The host-side restriction
(`party_together_fix` gated to clients only, `TryGetHostPartyRoster`) this round's changes never
touch stayed intact. Tab itself could not be exercised on the host (injected keystrokes never
reach this game, `docs/live-verification.md`) — the host's own `leader 0`/`leader pc` (which
exercise the SAME `RotateControlToFront` + camera path this round shipped) both answered
correctly in scenario 2 above, and `DeathController`'s `ChangeCharacter` swap-suppression patch
— the mechanism that would make a REAL Tab press a no-op mid-session — is untouched by this
round's changes (this round's fix never calls `ChangeCharacter` at all, so the patch and this
fix are structurally orthogonal).

`control-return-live.ps1` itself needed updating, not just re-running: its scenario-1 check
was written for the OLD architecture (claim and hand-back as separate events, with a real gap a
clamp could corrupt) and flagged the NEW architecture's own correct behavior (the routine clamp
resuming, harmlessly, AFTER control is already back) as a failure. Moved the check to the window
that still matters post-round-70: no clamp before the claim.

### Live confirmation — missing-PC recovery with a non-standard server id (2026-09-21)

The current reconnect failure was a different array shape from the older `0xFFFFFFFF` PC
sentinel case. The client `resolve party` dump at 21:59:56 showed:

```
CSWParty count 1 (CSWPartyTable 1)
server player id 0x7FFFFFFE is_pc=True hp 9
member[0] 0x80000134 -> Trask hp 36
member[1] 0x80000119 -> unresolved
member[2] 0x80000109 -> NOT a creature (vtable 0x748CC0)
```

The server-side player pointer was a verified PC, but no client-side CSWParty member represented
it. The safe client id was derived from that observed server id (`0x7FFFFFFE | 0x80000000 =
0xFFFFFFFE`), resolved back to the same PC pointer, and inserted into the first stale slot
allowed by `CSWPartyTableCount + 1`. The client control field was updated to the same derived
id; no `0xFFFFFFFF` assumption or perception-range swap was used.

After deploying the rebuilt client agent (PID 41036), the live repair at 22:10:47 read back:

```
member[0] 0xFFFFFFFE -> PC hp 9
member[1] 0x80000134 -> Trask hp 36
client internal +0x20 = 0xFFFFFFFE
```

The plugin then logged camera restoration to PC `0x7FFFFFFE`. The user confirmed that the
portrait and movement worked, closing the observed Trask-portrait/input-lock regression.
This was a manual in-process repair using the validated engine ids and the new bridge identity
handling; the automatic plugin fallback has not yet been validated across a full game restart.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged from the round-69 baseline (this
round changed control-switch mechanics, not any pure rule under unit test). Both plugin builds
clean across five redeploy/relaunch cycles. `callaudit.py --bad`: 161/162 declarations agree
with the binary (the one pre-existing mismatch, `CSWSArea::SetTransitionPending` in
`EngineHandles.cs`, predates this round and this round never touched that file). Rig torn down
clean, `auto_start_agent` restored to `1` in both installs.

### Verification tags (Round 70)

- VERIFIED (disasm, this round): `GetPlayerCreature`'s full chain (0x4B2820 → 0x4AEA40/0x4B4CB0
  → 0x4B2AE0 bucket walk → 0x561080 → 0x4AE770/0x4B1790 hashtable+type-confirm); `ChangeCharacter`
  (0x5F6B60) and `SetControlledCreature` (0x635480) in full, including the rotate-loop shift
  algorithm; `FindMemberIndexById` (0x6349C0); the calling convention of all of the above,
  confirmed against two real call sites via `xrefs.py`.
- LIVE-VERIFIED 2026-08-22 (this round): the 0-max-hp reading on a fresh rig, both instances,
  unconditionally; all four control-return-live.ps1 scenarios including a live restart of
  scenario 4; `verify.ps1` check 19; the array reorder landing synchronously with the claim log
  line (direct memory read either side of the call).
- NEEDS-LIVE, still open, precisely scoped: whether `CSWParty::SetControlledCreature` can be
  called successfully from this process at all (every live attempt this round left the array
  unchanged, including the bounds-check-free `-2` sentinel) — needs debugger access this
  round's tooling did not have. Does not block the regression fix, which no longer depends on
  it.

## Temporary spectator lock persisted in an own save (2026-09-23)

On the v74 build preceding the save-lock correction, the host entered `tar_m03mg`
through a staged warp at 10:19:40. The joiner correctly refused that single-seat
transition and its native minigame lock engaged at 10:19:41. The joiner remained in
`korr_m35aa`, with zero remote phantoms. Its save `000035 - parked_lock_beforefix`
landed at 10:19:48; nested module.ifo contains the own Yasuko Kaar player with
`Commandable=0` and an empty ActionList. Returning the host to the academy released
the live lock at 10:20:08. Thus an ordinary save can persist a temporary mod-owned
control restriction even though the running session later releases it.

An earlier affected save, `000033 - zhar_joiner_before`, also contains Commandable 0
before the Zhar dialogue begins. That run reached the silent scripted entry 101,
which pauses dialogue for the PC's workbench movement/animation; it never queued
that movement. A clean earlier checkpoint has Commandable 1 and did start the
approach, but still stopped before the scene resumed. This is evidence of a saved
lock defect, not proof that correcting it alone completes the Zhar reward check.

The intended correction must serialize the original commandable value only when
this mod owns the temporary lock, then reassert the lock in the running game. It
must cover saves with no phantoms and must not normalize every loaded zero flag:
retail scripts can legitimately make actors non-commandable.
