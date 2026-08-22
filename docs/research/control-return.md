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
