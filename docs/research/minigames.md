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
3. Enter a forced turret sequence — see §8's correction below on where that actually is, the
   escape pod is NOT it — and confirm the plugin's own log shows the `minigame: entered` /
   `minigame: exited` lines from `MinigameController` on the correct frames.
4. Confirm `minigame_hide_peer = 0` in `coop.ini` actually restores the old (mirrored,
   possibly frozen-looking) behaviour, for players who prefer it.
5. Check whether `PumpEnemies`/`PumpParty`'s minigame guard ever fires falsely during an
   ordinary cutscene that also happens to touch `CSWCArea` — the guard is a single pointer
   read and should be cheap to confirm has zero false positives across a normal playthrough's
   cutscenes.

⚠ **Item 3's original wording ("the escape pod launch is the earliest [turret sequence]") is
almost certainly wrong** — see §8 for the correction and the citation. Left unedited above
(struck through in spirit, not in fact) so the mistake and its fix are both on the record;
§8 is the section to actually follow.

## 8. Ready to test (2026-08-23 round)

Static-audited this round: every file item 8 (§5) claims to have changed —
`MinigameController.cs`, `MinigameRule.cs`, `EngineGlobals.TryIsInMiniGame`,
`KotorGameBridge.IsInMinigame`/`ReadLocalFlags`, `PlayerState.FlagInMinigame`/
`SharedBlockView.FlagInMinigame` (both `32`, no collision with the other five flag bits),
`CoopConfig`/`coop.ini.example`'s `minigame_hide_peer`/`minigame_spectate`, and the three
`CoopAgent.cs` call sites (peer-visibility filter, `PumpPhantomDamage`, `PumpParty`,
`PumpEnemies`) — was read and traced against this doc and found to **match exactly**: same
offsets (`0x007A39FC` → `+0x04` → `+0x04` → `+0x18` → `+0x48` → `+0x264`) in both
`MinigameController` (plugin, in-process) and `EngineGlobals` (agent, cross-process), same
flag bit, same gating logic. `dotnet build` and the full `dotnet test` (1635 tests, including
the 10 in `MinigameRuleTests.cs`) are green on this commit. **No code drift, no bug found in
the gating/parking/restore logic itself** — traced end to end:

* A hidden peer is not merely "not driven", it is **destroyed** on the peer's own instance
  (`PhantomController.OnFrame`, `!remote.Active` branch → `DestroySlot`) the first frame their
  `Active` flag clears, and **respawned** fresh (`TrySpawnOne`, at their currently-published
  position) once it sets again — matching §7 item 1's "disappears, not freezes" expectation
  exactly, not an assumption.
* The slot a hidden peer occupies is **not** reclaimed while they are parked:
  `PeerSlotMap.Assign`/`KotorGameBridge.WriteToPlugin` treat "filtered out of `visible` this
  tick" exactly like "walked through a door" (the mechanism `PeerSlotMap`'s own doc comment
  was written for) — a slot is only evicted from a holder that is provably absent AND another
  peer needs the space. With two players there is no eviction pressure at all, so no
  desync-by-reassignment is possible here.
* `MinigameRule.cs` (the tested pure-policy class) is **not actually called from production
  code** — `CoopAgent.cs` restates its three booleans inline instead (§5 already says this is
  deliberate: "the rule class is the one place the policy is stated once and tested in
  isolation"). Confirmed by grep: `MinigameRule` appears only in its own file and
  `MinigameRuleTests.cs`. Not a bug — the inline booleans in `CoopAgent.cs` were checked
  line-by-line against `MinigameRule`'s three methods and agree with all of them — but worth
  knowing before assuming the tested class is what is running live.

No fix was made because none of the three questions the task asked about turned up a gap:
"what does parked mean" → destroyed-and-respawned, not frozen (verified above); "is there a
re-entry stuck state" → no, `ClearSlot` on destroy resets every per-slot latch
(`_factionKnown`, `_twinNamed`, `_lastAnim`, equipment cache, damage watch) so the respawn is
identical in every way to a peer's very first spawn of the session; "does anything triggered
by minigame completion fail to sync" → item/journal/global-flag sync (`ItemController.cs`,
`JournalController.cs`, `EventFlagStore`) is not gated on `IsInMinigame` anywhere (grepped —
zero hits), so a swoop-race prize or a turret-sequence story flag flows through the ordinary,
already-tested channels the moment the pump after minigame-exit runs; it is not a
minigame-specific path that could have been missed.

### 8.1 A correction found while planning the live pass, not a code bug

§7 item 3 says "the escape pod launch is the earliest [turret sequence]". Cross-checking that
against this project's OWN prior, fully-disassembled trace of the escape pod
(`docs/research/cinematics.md` §4.1–4.3) turns up **no minigame involvement at all**: the pod
flow is `end_pod.utp OnUsed → k_pend_pod02` (fade, gather party, jump to the pod) →
conversation `end_pod.dlg` → `k_pend_pod01`, which is 0x46 bytes and does exactly one thing —
`StartNewModule("STUNT_00", "", "01c", ...)`. A movie queue and a module transition, nothing
that touches `CSWCArea::mini_game`. `cinematics.md` traced this chain in enough detail to name
the exact script and its byte count; if there were a turret placeable in that flow it would
have surfaced there.

The same document's §4.4 full static sweep of every `StartNewModule` call in the game
(34 queued transitions found) names exactly one that leads into a minigame module:
**`"11a"` (→ the turret minigame module `m12ab`)**, immediately next to `"17a"+"11a"` labelled
"Leviathan escape" and `"17"` labelled "Leviathan capture". Read together, this is almost
certainly the Leviathan turret-gunner sequence (defending the Ebon Hawk during the escape),
not anything at the start of the game. **This is INFERRED, not itself live-verified in this
round** — `m12ab` has not been visited or confirmed to actually raise `mini_game` — but it is
the only lead this codebase's own research has for "which module hosts a turret", and it
directly contradicts the escape-pod claim with a citable trace. Use it as the target below;
if `warp m12ab` does not put the plugin log's `LocalInMinigame` anywhere near true, that is
itself useful information the live pass should record back into this doc.

### 8.2 Swoop race — exact steps

Grounded in this repo's own trigger sweep (`docs/research/trigger-relay.md`): module
`tar_m02ab` (Taris, Lower City) carries a trigger literally named `tar02_swoop02ab` running
`k_ptar_swoop02ab` — the in-fiction swoop-track entrance. This is reachable by console warp
alone, no story progress required.

1. Both instances connected and in the same module (any starting module — `warp` moves both
   independently, they do not need to already be together).
2. **Player A** (will race): `~` console → `warp tar_m02ab`. Once loaded, walk to the swoop
   track entrance (the game's own map/HUD marks it — this is ordinary Taris Lower City
   geography, not a co-op-specific location) and start a race through the normal in-game
   prompt (talk to the swoop-track NPC, accept the race).
3. **Player B** (stays put, watching): remain in `tar_m02ab`, near where A was standing, camera
   on A's phantom.
4. The moment A's race actually starts (screen changes to the swoop HUD/track view), check:
   - **A's plugin log** (`<gameA-dir>\kotorcoop-plugin.log`): `minigame: entered (swoop/turret)
     — peers will park this player's phantom`.
   - **B's screen**: A's phantom should visibly disappear within roughly one spawn-cooldown
     window (30 frames) of A's flag going up — not freeze in place.
   - **B's plugin log**: a `DestroySlot`-driven log line for A's slot (`"peer left: destroyed
     phantom 0x.. (slot N)"` — the reason string is literally `"peer left"`, inherited from
     the general "peer absent from this publication" path; that is correct and expected here,
     not a mislabelled minigame message).
5. While A is racing, have B deal damage to something (an enemy, or use `god`/`slay` cheats to
   generate a combat event) so there is a "damage owed" total in flight, per §6.
6. When A's race ends (win or lose, back to the normal HUD):
   - **A's plugin log**: `minigame: exited — back in the shared world`.
   - **B's screen**: A's phantom respawns at A's current (post-race) position within one
     spawn-cooldown window.
   - **A's own screen**, within the next pump or two: confirm whether B's mid-race damage now
     lands on A (per §6, it should — "delayed, not forgiven"). This is the live call §6 asks
     for: does that read as fair or as a surprise. Record the verdict either way.
7. Repeat with `minigame_hide_peer = 0` in both `coop.ini`s: confirm A's phantom now stays
   mirrored (frozen/odd-looking is expected and correct under this setting) through the whole
   race instead of disappearing — proves the toggle itself works, for players who prefer the
   old behaviour.
8. Optional, no keyboard needed: `pwsh tools\blockpoke.ps1 -ProcId <A's pid> cheat status`
   or the existing `creatures.ps1`/log tailing already used elsewhere in
   `docs/live-verification.md` can substitute for eyes-on-screen checks where useful.

### 8.3 Turret sequence — exact steps (target corrected per §8.1)

This one is genuinely less certain than the swoop test — `m12ab` has not been visited by any
prior round of this project's research — so treat step 2 as the actual experiment, not a
known-good recipe.

1. **Player A**: `~` console → `warp m12ab`. If the warp is refused (bad resref — the module
   may not exist under this exact name, or may need a waypoint tag `warp m12ab <tag>`), that
   itself is the finding: record the refusal and fall back to reaching the sequence by normal
   story progress from an existing save at/after the Leviathan capture, if the test rig has
   one — do not spend the session guessing resrefs blind.
2. If the warp lands: look for whatever placeable/console the module presents as a turret seat
   and interact with it the normal way (this project has not mapped that placeable's tag —
   it will be obvious in-game, the same way a swoop kiosk is).
3. The moment gunner view starts, check the same four things as the swoop test (§8.2 steps
   4–7): A's log `minigame: entered`, B's phantom-of-A disappearance, B's log destroy line,
   and on exit, A's log `minigame: exited` + B's respawn.
4. Whatever module actually turns out to host the working turret sequence — `m12ab` or
   something else found along the way — write the confirmed resref and any waypoint/placeable
   tag back into this section so the NEEDS-LIVE tag on §7 item 3 can be dropped and the next
   round does not have to re-derive it.

### 8.4 What "pass" looks like

Every NEEDS-LIVE item in §7 resolved one way or the other (including item 5's false-positive
check — worth a few minutes of ordinary cutscene-heavy play, e.g. a couple of story
conversations, watching for a stray `minigame: entered` that should not have fired), plus the
§6 damage-fairness call made explicit in this doc rather than left as an open question.
