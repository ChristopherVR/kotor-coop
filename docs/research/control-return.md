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
- NEEDS-LIVE (this pass did not run the game): that the fix, applied, actually converges live —
  `tools/control-return-live.ps1` is the script to run next round. The three-part log signature
  above is the pass criterion.
