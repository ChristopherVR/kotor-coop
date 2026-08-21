# Minigames: swoop races, forced turret sequences, and Pazaak (2026-08-21)

Roadmap item 8. What `CSWCModule::GetMiniGame` actually reads, why Pazaak is a different
mechanism entirely, and what the co-op layer now does about both.

Tag legend: **VERIFIED** = read out of this binary (GOG 1.0.3, disk == memory, base
`0x400000`) via `tools/disasm.py` / `tools/xrefs.py` / `tools/fieldwrites.py`, or the
plugin's own symbol database (`tools/kotor1_0_3.db`). **INFERRED** = the structure and code
path are read directly, but the specific conclusion (which numeric constant means what) is
reasoned rather than observed. **NEEDS-LIVE** = correct as reasoned, not yet watched happen
in a running two-instance session.

---

## 1. `CSWCModule::GetMiniGame` (0x00641980)

Full disassembly of the function:

```
0x00641980  mov eax, [ecx+0x48]      ; module->area  (CSWCArea*)
0x00641983  test eax, eax
0x00641985  je   0x64198e
0x00641987  mov eax, [eax+0x264]     ; area->mini_game  (CSWMiniGame*)
0x0064198D  ret
0x0064198E  xor eax, eax
0x00641990  ret
```

**VERIFIED.** Three instructions once the module pointer is in hand: read `area` off the
module, and if it exists, read `mini_game` off the area. The return value is the raw
`CSWMiniGame*` — nonzero means a swoop race or a turret sequence is running, zero means
neither. Both field names (`area`, `mini_game`) and their offsets (`0x48`, `0x264`) come
straight from `tools/kotor1_0_3.db`'s `offsets` table (`CSWCModule.area @ 0x48`,
`CSWCArea.mini_game @ 0x264`), not guessed from the disassembly alone.

This corrects [ingame-gui-transition.md](ingame-gui-transition.md) and is already flagged in
[cinematics.md](cinematics.md) §1: the function was previously read as a "cutscene/dialogue"
flag because `CClientExoAppInternal::MainLoop` branches on it right where the HUD normally
comes up (`SetInputClass(1,1)` instead). It is not cutscene state. It is minigame mode, full
stop — dialogue mode is a *different* input class (`3`, read via `CClientExoApp::IsInDialogue`
territory, `ClientInputClass` in `KotorGameBridge.cs`) and does not touch this field.

## 2. Resolving the module pointer, and how the plugin already does it

`CSWCModule::GetMiniGame` is called from `CClientExoApp::GetMiniGame` (0x005EDB00):

```
0x005EDB00  mov ecx, [ecx+4]         ; CClientExoApp -> CClientExoAppInternal
0x005EDB03  call 0x644760            ; CClientExoAppInternal::GetModule
0x005EDB08  test eax, eax
0x005EDB0A  je   0x5edb13
0x005EDB0C  mov ecx, eax
0x005EDB0E  jmp  0x641980            ; tail-call into CSWCModule::GetMiniGame
0x005EDB13  xor eax, eax
0x005EDB15  ret
```

and `CClientExoAppInternal::GetModule` (0x00644760) is one instruction:

```
0x00644760  mov eax, [ecx+0x18]      ; internal->module  (CSWCModule*)
0x00644763  ret
```

**VERIFIED.** The whole chain is `AppManager -> CClientExoApp (+0x04) -> CClientExoAppInternal
(+0x04) -> CSWCModule (+0x18) -> CSWCArea (+0x48) -> CSWMiniGame (+0x264)`.

This is not a new discovery — it is the **exact same chain** the plugin already resolves for
the spectate camera. `Odyssey.cs`'s `GetClientModuleFn` is declared as a raw function pointer
at `0x005ED530`:

```
0x005ED530  mov ecx, [ecx+4]
0x005ED533  jmp  0x644760            ; the same CClientExoAppInternal::GetModule above
```

— byte-identical to the first half of `CClientExoApp::GetMiniGame`. `Odyssey.GetClient()`
resolves `CClientExoApp` from the app manager singleton at `0x007A39FC` (`AppManagerPtr` in
both `Odyssey.cs` and `KotorCoop.Memory/EngineGlobals.cs`), exactly as this walk needs.

`KotorCoop.Memory/EngineGlobals.cs` (the agent-side equivalent of `Odyssey.cs`, used for every
out-of-process pointer chase the same way) already had `TryGetClientInternal`; this item adds
`TryGetClientModule` and `TryIsInMiniGame` right next to it, following the file's own stated
purpose ("nearly every capability... begins by walking `APP_MANAGER -> client|server ->
internal`... collected in one place deliberately").

## 3. The value domain: is it just a bool, or does it distinguish swoop from turret?

`CSWMiniGame` (`tools/kotor1_0_3.db`, size `0xF0`, vtable `0x7678228`) has a `type` field at
offset `0x80` (`CSWMiniGame.type`). Disassembling `CSWMiniGame::Load` (`0x006723d0`, symbol
database) around the store to that offset:

```
0x00672461  mov ecx, [esi+8]
...
0x00672474  push 0x745e38            ; ASCII "Type\0" — a GFF field name
...
0x0067247E  mov ecx, [esi+8]
0x00672481  call 0x411c20            ; GFF field reader, returns eax
0x00672486  cmp eax, 1
0x00672489  je   0x672490
0x0067248B  cmp eax, 2
0x0067248E  jne  0x672496
0x00672490  mov [esi+0x80], eax      ; type = 1 or 2; any other GFF value is left unset
0x00672496  mov eax, [esi+0x80]
0x0067249C  cmp eax, 1
0x0067249F  jne  0x6724ab
0x006724A1  mov [esp+0x20], 0x40c00000   ; 6.0f — a camera/physics constant used only when type==1
```

**VERIFIED**: the minigame's loader reads a GFF field literally named `"Type"` off the
minigame resref data and stores it into `CSWMiniGame+0x80` only when the value is `1` or `2`;
any other value leaves the field at its constructor default. `type == 1` selects a distinct
float constant a few instructions later (most plausibly a swoop track's camera/physics
tuning; turret gunnery uses a different camera model entirely).

**INFERRED**: `1` and `2` are "swoop" and "turret" (in some order) — plausible given this is
exactly the two-minigame-family split KOTOR 1 ships (swoop tracks on Taris/Tatooine/Manaan;
turrets on the escape pod launch, the Leviathan, the Ebon Hawk hangar exit), and consistent
with the `SWMG_*` NWScript minigame command family `docs/re/spawn.md` already names. **Not
read further** — this item's whole gating decision only needs "is a minigame running", not
"which kind", so `type` is documented here and left unread by the plugin/agent code. A future
consumer (a HUD label, a spectate camera tuned differently per kind) can read
`CSWMiniGame+0x80` off the same `mini_game` pointer this item already resolves.

## 4. Pazaak is not a minigame in this sense

Pazaak has its own, entirely separate mechanism — no `CSWCModule::GetMiniGame` involvement at
all:

* `CClientExoAppInternal+0x70` is a `CPazaakData*` (symbol database:
  `CClientExoAppInternal.pazaak @ 112`), holding `game_running`, `end_script`, `wager`,
  `won_last_game`, `opponent_pazaak_deck`.
* `CClientExoApp::GetPazaakIsRunning` (`0x005EE140`) is `[[ecx+4]+0x70]` — the raw pointer
  itself is the running flag (nonzero once a game is allocated).
* `CClientExoApp::StartPazaakGame` / `EndPazaakGame` (`0x005EDDC0`, `0x005EDDD0`) drive it;
  `CSWVirtualMachineCommands::ExecuteCommandPlayPazaak` (`0x0053B1E0`) is the NWScript entry
  point (`PlayPazaak()`).
* The GUI is `CSWGuiPazaakStart` (the wager/start screen, asset likely `pazaakstart.gui`
  going by the class's control names — `wager_popup`, `sidedeck_gui`, `card_labels`) handing
  off to `CSWGuiPazaakGame` (the actual table, vtable `0x7680856`, `panel`/`player_sidedeck`/
  `npc_sidedeck`/`flip_icon` controls) once a hand starts. Both are ordinary `CSWGuiPanel`
  subclasses, loaded and pushed the same way every other in-game panel is — the same
  mechanism `native-browser-panel.md` and `override-surfaces.md` already document for the
  save/load and main-menu panels, not a special "minigame mode".

**VERIFIED** (class layout, offsets, function list) via `tools/kotor1_0_3.db`; **not
disassembled further** — item 8's brief only asked whether the existing pause/menu detection
already sees Pazaak, and it does not need to: Pazaak is GUI-panel state, not
`CSWCModule::GetMiniGame` state, so this item does not attempt to detect it. **Consequence**:
a player playing Pazaak against an NPC is invisible to everything built here. Their
`PlayerState.FlagInMinigame` stays clear, their phantom keeps being mirrored standing at the
table, and any damage forwarded to them still applies — which is arguably correct (Pazaak is
not a location change and does not remove the player's body from the world the way a swoop
track or a turret view does) but is unverified either way. **Left for a future item** if
Pazaak turns out to need the same "not really present" treatment (e.g. a peer's phantom
frozen mid-animation for the length of a hand looking wrong) — the detection would be
`CClientExoApp::GetPazaakIsRunning`'s pointer, added to `PlayerState.Flags` the same way
`FlagInMinigame` was.

## 5. What this item changed

* `PlayerState.FlagInMinigame` (Protocol, bit `32`) — set from `EngineGlobals.TryIsInMiniGame`
  on the agent side (`KotorGameBridge.ReadLocalFlags`), the same file and the same pattern as
  `FlagInCombat`/`FlagPaused`. No plugin-to-agent block field was added — the wire flag is
  computed by the agent reading the target process directly, the same way `IsInCombat` already
  is, so nothing about `SharedBlock.cs`'s layout changed.
* `RemoteSlot.Flags` (`SharedBlock.cs`, untouched) already carries the full `PlayerState.Flags`
  dword verbatim (`KotorGameBridge.WriteToPlugin` writes `peer.State.Flags` into it as-is); the
  new bit rides that existing field. `SharedBlockView.cs`'s `RemoteView` gained a matching
  `FlagInMinigame` / `InMinigame` convenience pair, purely additive.
* `MinigameController.cs` (Plugin, new) — reads the same pointer chain in-process, every
  frame, and logs the enter/exit edges. It does not gate anything itself and does not write
  the shared block; the actual policy (below) runs agent-side, reading `bridge.IsInMinigame`
  (also backed by `EngineGlobals.TryIsInMiniGame`) directly. It exists for near-zero-latency
  local logging and as a ready per-frame hook for whatever consumes it next (a HUD indicator,
  the spectate hookup in §6).
* `MinigameRule.cs` (Plugin, new) — the pure, unit-tested statement of the three gating
  questions: hide a peer, suppress incoming damage, suppress this instance's own presence
  publish. `CoopAgent.cs` applies the equivalent plain booleans directly (see below); the rule
  class is the one place the policy is stated once and tested in isolation, matching
  `PartyFreezeRule` / `ControlReturnRule` / `SpawnDrainRule`'s existing role in this codebase.
* `CoopAgent.cs`:
  - The peer-visibility filter (the one `FilterByModule` already uses, right before
    `bridge.TryWriteRemotePeers`) now also drops a peer whose `State.IsInMinigame` is set,
    gated by `settings.HidePeersInMinigame` (coop.ini `minigame_hide_peer`, default on). This
    is the exact "peer in a different module" idiom: an excluded peer is simply absent from
    what gets written, and `KotorGameBridge.WriteToPlugin` already zeroes `Active` for any
    slot not in that list — so hiding costs nothing beyond the filter itself.
  - `PumpEnemies` (the enemy-authority branch) and `PumpParty` (the host branch) both now skip
    publishing while `bridge.IsInMinigame` is true, on top of the existing dialogue guard —
    the engine's own creature/companion state during a swoop/turret sequence is not the
    shared-world fight.
  - `PumpPhantomDamage`'s inbound half skips writing the running "damage owed" total into the
    shared block while `bridge.IsInMinigame` is true — the plugin never sees new damage to
    apply, so a player racing or manning a turret cannot be hit.
* `coop.ini` keys `minigame_hide_peer` (default on) and `minigame_spectate` (default off, read
  and stored, **not yet wired** — see §6) join `CoopConfig.cs` and `profiles/coop.ini.example`.

## 6. What was deliberately left out

* **Damage taken DURING a minigame is not forgiven, only delayed.** `PumpPhantomDamage` skips
  the *write* while in a minigame, but the session's own damage ledger (`GetIncomingPhantomDamage`)
  is untouched and keeps accumulating from whatever peers report. The moment the local player
  is back in the world, the next pump resumes publishing the (now larger) running total, and
  it applies as a lump. Whether that reads as fair ("you owed it, you're paying it") or as a
  surprise ("I just got hit the instant I got off the swoop bike") is a live-verification
  question, not a static one — actually forgiving it would mean the agent telling the SESSION
  to drop the accrued total, which is a session-ledger change well past "gate an existing
  path", and was left out on that basis.
* **Spectate is not wired.** `SpectateController.cs`'s camera handoff
  (`Odyssey.SetCameraFollowServerObject`) is triggered on `Odyssey.IsDowned`, and both its
  entry condition and its `StopSpectating` exit condition are written specifically in terms of
  "downed". Reusing it for "a peer is in a minigame" is not a one-line change — it needs a
  second trigger and a second, independent exit condition (the peer's minigame flag clearing,
  not "back up") layered onto a class whose control flow currently assumes exactly one reason
  to be spectating. `coop.ini minigame_spectate` is read and stored (`CoopConfig.SpectateMinigame`)
  so the key exists ahead of that work, but nothing acts on it yet.
* **Pazaak is undetected**, per §4 — not in scope for `PlayerState.FlagInMinigame`, which is
  specifically `CSWCModule::GetMiniGame`.

## 7. What a live pass must check (NEEDS-LIVE)

1. Enter a swoop race (Taris or Tatooine) as one player while the other watches: confirm the
   racer's phantom disappears (not freezes) on the peer's screen, and reappears in the right
   place once the race ends.
2. Take damage from a peer's fight while racing, then finish the race: confirm whether the
   accrued damage lands as described in §6, and decide from the live result whether that
   needs fixing before this is more than "documented as a known limitation".
3. Enter a forced turret sequence (the escape pod launch is the earliest one) and confirm the
   plugin's own log shows the `minigame: entered` / `minigame: exited` lines from
   `MinigameController` on the correct frames.
4. Confirm `minigame_hide_peer = 0` in `coop.ini` actually restores the old (mirrored,
   possibly frozen-looking) behaviour, for players who prefer it.
5. Check whether `PumpEnemies`/`PumpParty`'s minigame guard ever fires falsely during an
   ordinary cutscene that also happens to touch `CSWCArea` — the guard is a single pointer
   read and should be cheap to confirm has zero false positives across a normal playthrough's
   cutscenes.
