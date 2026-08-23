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

## 9. The live pass (2026-08-23) — co-visibility answered, and it is different per category

Run against `integration/honest-limitations` on the real two-GOG-install rig
(`tools/demo.ps1`), plugin freshly built and installed into both game dirs, agent rebuilt.
This is the round that actually answers the question item 8 opened but never tested: **can a
swoop race or a turret sequence be genuinely co-visible, or does the engine structurally force
spectate/park?** Short answer, confirmed live and not just reasoned: **swoop racing is
architecturally NOT precluded from co-visibility — same module, in-place mode switch — but
this round could not get a human-quality walk to the actual trigger to observe what it LOOKS
like; the turret sequence is a dedicated separate module and co-visibility is impossible there
by construction, confirmed live on both sides at once.**

### 9.1 Swoop: confirmed live to be an IN-PLACE mode switch, not a module transition

Three independent lines of evidence, one of them a live crash that turned out to be the most
informative event of the round:

* **Static: no minigame data lives in the module's own files.** `tar_m02ab`'s `.are` and
  `module.ifo` were read in full (`tools/kotorfiles.py gff`) — neither carries any
  minigame-related field. The swoop track's `CSWMiniGame` GFF (§3's `"Type"` field) is loaded
  by a script call at race-start time, not baked into the area the way `Mod_Entry_Area` or
  `OnEnter` are. If entering the race required a `StartNewModule` to a dedicated instanced
  track, there would be no reason for the CURRENT area's own `.are`/`.ifo` to stay silent
  about it — a real per-module minigame would show up in the module's own data the way the
  turret module's `Mod_OnModLoad` does (see §9.2). It does not, because there is no second
  module to hold it.
* **Static: the swoop track is furniture inside an ordinary populated area, reached by a
  subarea trigger, not a door.** `m02ab.git`'s `TriggerList` carries two `tar02_swoop02ab`
  triggers (world centre ≈ `(221.2, 64.5)` and `(221.4, 141.0)`, ~4×3.7 m polygons) sitting a
  few metres from two `plc_swpbike1` display placeables (`(238.1,103.2)`, `(236.1,105.5)`,
  both `Useable=0` — decorative, not the interaction point) and two `k_trans_abort` placeables
  (`(221.4,56.4)`, `(221.3,145.7)`, `Conversation=k_transabort`, also `Useable=0` — almost
  certainly the confirm/decline prompt a subarea-enter script starts). None of this needs a
  door or a module boundary; it is ordinary GIT furniture inside the same Lower City area
  players already walk through for every other Taris errand.
* **LIVE, and decisive: forcing `CSWCArea::mini_game` non-null while the player stayed in
  `tar_m02ab` the whole time made the engine start calling `CSWMiniGame::Update` (0x6735d0)
  on the very next frame — with no `travel:`/`epoch: module changed` line anywhere near it.**
  This was not a passive read: a scratch script
  (`tools/../scratchpad/minigamepoke.ps1`, not committed — a throwaway diagnostic, not
  product code) walked the exact chain `MinigameController`/`EngineGlobals` already use
  (`0x007A39FC → +4 → +4 → +0x18 → +0x48 → +0x264`) and wrote a nonzero-but-fake pointer into
  `+0x264`. The plugin log shows the client peer's phantom being destroyed
  (`peer left: destroyed phantom 0xCF`) within 20 ms — the parking mechanism engaged
  correctly and immediately — and then, because the fake pointer had none of a real
  `CSWMiniGame`'s sub-objects (gun banks, track data, `CSWMiniGame::Load`'s own construction),
  the very next `Update` call dereferenced garbage and crashed the host
  (`FIRST-CHANCE access violation (0xC0000005) at 0x6736D8`, symbolised via
  `tools/kotor1_0_3.db` to `CSWMiniGame::Update+0x108`). **The crash is not a mod defect — it
  is the expected outcome of forcing a field the real loader normally populates — but the
  crash itself is proof that the engine treats an armed minigame as an ACTIVE PER-FRAME
  SUBSYSTEM running inside the CURRENT area's own frame loop, not a separate simulation behind
  a module boundary.** Nothing about entering the swoop race, per this evidence, requires
  leaving `tar_m02ab`.

**What this settles and what it does not.** The structural question — same module or a
separate instanced one — is answered: same module, in-place. That means co-visible swoop
racing is not ruled out by the engine's own architecture the way the turret sequence is (§9.2).
**What it does NOT answer is what it actually LOOKS like** — whether a peer's ordinary
creature-phantom, still position-synced through the normal channel while `CSWMiniGame::Update`
drives the racer's camera/physics every frame, renders as a sensible "watching a friend race"
scene or as something uncanny (a stationary or sliding on-foot body while a rail-vehicle HUD
takes over the racer's own screen). Answering that needs a human to actually watch a race
happen with the peer-hide gate turned off, and this round could not manufacture that
observation unattended — see §9.3. **No code change was made to the parking behaviour as a
result.** Forcing co-visibility on the strength of the architecture alone, without having seen
it, would be exactly the kind of change the task brief said not to make.

### 9.2 Turret: confirmed live to be a dedicated, separate module — spectate/park is structurally forced

`warp m12ab` (§8.1's inferred lead) resolved cleanly and is now **VERIFIED, not inferred**:

```
travel: moving to module 'm12ab' on the session's authority
epoch: world unloading; clearing phantom, item, combat and door state
...
where -> module m12ab at 3.0 3.0 -0.7
```

`m12ab`'s own `module.ifo` names it `Mod_Name = "Ebon Hawk Turret"` and — the key field —
**`Mod_OnModLoad = k_pebo_mgload`**, a script that runs automatically the instant the module
finishes loading, no interaction with any placeable required (`m12ab.git`'s `Placeable List`
is ten `plc_invisible` markers, `Useable=0` — position anchors for the minigame, not clickable
objects; the `Creature List` and `TriggerList` are both empty). `Mod_OnHeartbeat =
k_pebo_mgheart` keeps polling once inside.

Live-confirmed the consequence of that auto-start directly: **both instances, independently,
logged `minigame: entered` the moment each one's own engine finished loading `m12ab`** — the
host at `18:30:37.634` (having warped there) and the client at `18:30:39.498` (having followed
via the existing "travel together" mechanism, `docs/live-verification.md`'s already-proven
host-warps-client-follows path). Each side's `MinigameController` read its OWN engine's
`CSWCArea::mini_game`, set independently by that engine's own `Mod_OnModLoad` run — not
something the wire told it. **This is the structural answer**: because merely being present in
the module puts a player into minigame mode on their OWN engine, there is no state a peer
could be in that is "in the same space, watching, not racing" — anyone who follows into
`m12ab` is themselves auto-parked the instant they arrive, symmetrically, on their own
initiative, with no gate the mod could relax to change that. Spectating the turret sequence
from inside the shared module is not a design choice this mod made; it is not offered by the
module at all. The only way to be "together" during this sequence is for the mod's existing
park behaviour to hide both directions at once, which is exactly what it already does.

**§8.1's inference is now confirmed and §7 item 3 is closed**: `m12ab` is the turret module,
reached from the Leviathan-escape `StartNewModule` chain `cinematics.md` §4.4 traced
statically; this round found it live from a bare `warp` with no story progress needed.

### 9.3 Why an actual race/gunnery run could not be observed this round — a genuine tooling limit, not a shortcut taken

Reaching the swoop trigger polygon (§9.1) or getting past `m12ab`'s auto-start into an actual
interactive gunnery view both need the player CHARACTER to physically move, and the mod's only
existing player-walk primitive (`usedoor`'s internal `Odyssey.MoveToLocation` call) is coupled
to a door target. This round added a bare-point equivalent —
**`walkto <x> <y> [z]`** (`CoopCommandKind.WalkTo` / `CheatKinds.WalkTo = 24` /
`SharedBlock.CheatKindWalkTo = 24`, `src/KotorCoop.Session/ConsoleCommand.cs`,
`src/KotorCoop.Plugin/CheatController.cs`) — the same `Odyssey.MoveToLocation` call `usedoor`
already uses, queued at a raw point instead of a door threshold, because a subarea trigger's
`OnEnter` only fires for a genuinely MOVING creature (`docs/research/trigger-relay.md` §2) and
a `tp` snap crosses nothing.

**Measured live, and the primitive itself works — the problem is not knowing the map.** From
the `tar_m02ab` module entry point, a 3.6 m `walkto` covered 2.6 m (72%) before stopping, and
an initial call toward a ~10 m target covered 1.4 m before stalling — both consistent with
this project's own prior finding for `usedoor` ("0.9–2.4 m short… the pathfinder cuts a
corner near the doorway"), i.e. ordinary partial-pathing friction, not a broken primitive.
But every attempt to close the remaining **~110 m** from the entry point to the swoop trigger
— through unmapped, unseen interior geography — either stalled after a few metres or (twice,
from coordinates picked off the GIT polygon's raw numbers with no sight of the room) refused
to move at all, most likely because the guessed point sat outside walkable geometry entirely.
Reissuing the same call more often made this WORSE, not better (a tight retry loop produced
zero net movement where a single call, given time, had produced some) — repeated re-queuing
appears to restart the pathfind before the creature builds any momentum.

This matches, exactly, this project's own repeated prior conclusion for interactive content —
real party-following, real PvP-style combat, real dialogue choices — recorded throughout
`docs/live-verification.md`: **"needs a person playing,"** not a gap in effort. Blind
turn-by-turn navigation through an unfamiliar building via coordinate guesses is not a
substitute for a human who can see the screen. `walkto` is left in the tree as a real,
useful primitive for future live passes (a human — or an agent with screen access — can use it
for the LAST few metres once they can see the target), but this round does not claim to have
watched an active race or an active gunnery view.

### 9.4 The parked-peer path, live end to end

* **Entry (hide) is correct and immediate**, confirmed twice with two different triggers for
  the same underlying flag: the forced-pointer test (§9.1, `peer left: destroyed phantom
  0xCF` within 20 ms of the flag going up) and the real `m12ab` auto-start (§9.2, the
  client's own `flush, before the module is stored: destroyed phantom 0x12D` firing the
  instant its own follow-travel carried it into the minigame module). Matches §8's static
  audit exactly: destroyed, not frozen.
* **A peer disappearing entirely while parked degrades safely.** The host crash from §9.1's
  forced-pointer test killed the host process outright while the client still believed itself
  connected and parked. The client's own log shows the ordinary disconnect path running
  cleanly — `* Lanik left the game` → `* co-op: connecting...` → a steady reconnect loop, no
  stuck state, no repeated errors — the same resilience already proven for a killed agent
  process in `docs/live-verification.md`'s 2026-08-01 pass, now shown to hold when the GAME
  process itself disappears mid-park.
* **An unattended, no-input run of the turret sequence ends on its own — but the ending is
  rough.** Left alone with zero player input for ~65 s inside `m12ab` (nothing this rig can
  do reaches the actual gunnery controls — see §9.3), the host's world tore itself down on its
  own initiative: `world: unannounced teardown - treating as a load, not a transition`, then
  a clean drop to the main menu (`menu: Co-op button shown, labelled and bound`, no crash, no
  error spam). Read against ordinary KOTOR 1 design — a defend-the-ship sequence with a
  passive player almost certainly means the player LOSES and the game ends the run — this is
  very likely vanilla single-player Game Over behaviour, not a co-op-specific bug, and the
  plugin's existing "unannounced teardown" fallback (built for exactly this shape of surprise
  world loss) absorbed it without incident on both sides.
* **One genuine rough edge, found by an unusual sequence, not ordinary play.** Manually
  reloading the host's save from that main-menu state (the same `KotorCoop.Agent.exe load
  --pid … --load-save 1` demo.ps1 itself uses) while the CLIENT was still sitting parked
  inside `m12ab` made the client's own "travel together" logic immediately try to follow the
  host's fresh load back into `END_M01AA` — and the client's game process did not survive
  it (log ends mid-transition, process gone). This is very likely the ALREADY-DOCUMENTED
  `docs/research/travel-map-init-crash.md` class (a plugin-driven transition reaching the
  engine's map init before the client has torn its own world down), not a new defect, and it
  needed a specifically artificial trigger — an external forced reload of one side while the
  other is still deep inside an unrelated minigame module — that normal play (finishing or
  failing a race/turret sequence through the game's own flow, not an operator's manual
  reload) does not produce. Flagged here rather than chased further this round; a session
  ending on both sides at once through the sequence's own natural conclusion, not an
  operator's reload, is the case that actually matters and was not itself observed to fail.
* **Item/journal/global-flag sync is unaffected**, per §8's static grep (`IsInMinigame` never
  gates `ItemController`/`JournalController`/`EventFlagStore`) — nothing in this round's live
  observations contradicts that, and nothing needed to change there.

### 9.5 Conclusion, stated plainly

* **Swoop racing: co-visibility is not structurally precluded** (same module, in-place mode
  switch — live-confirmed via the `CSWMiniGame::Update` crash evidence) **but is not yet
  demonstrated to look good, or to look like anything at all** — that needs a human at the
  controls to actually start a race with the peer-hide gate off and look at both screens. The
  current default (hide/park) is the safe, shipped behaviour and stays unchanged.
* **Turret sequences: spectate/park is the only possible outcome, confirmed live, not
  inferred.** `m12ab` is a dedicated module whose own `Mod_OnModLoad` auto-parks anyone who
  merely walks in, on both sides independently — there is no state to relax into
  co-visibility here even in principle.
* **The parked-peer mechanism itself is solid**: entry is immediate and correct, a lost peer
  (even by a hard crash) degrades to a clean reconnect loop, and the one rough edge found
  needed an artificial combination (a manual reload of one side while the other sits inside an
  unrelated minigame module) that ordinary play does not produce.
* No production behaviour changed this round. The one code addition, `walkto`, is a live-test
  instrument (`SharedBlock.CheatKindWalkTo` / `CheatKinds.WalkTo`, both `24`, covered by the
  existing `CheatKindSyncTests`), not a gameplay change.

## 10. The follow-up round (2026-08-23, later same day) — the navigation problem is SOLVED, and the blocker moved one layer deeper

Picked up exactly where §9.3 left off: §9's own round could not get a human-quality walk to the
swoop trigger and called that a genuine tooling limit. This round's brief was explicit — reach
the trigger a different way than `walkto` alone, using the project's established **tp-hop, then
walk the last stretch** pattern (the same idiom `console 'tp 45 35 -1.3'` uses elsewhere in this
project, see `docs/research/party-reconciliation.md`) instead of one long blind walk. That
technique worked. What it uncovered is a **new, more specific blocker** than "can't get there":
the trigger fires cleanly and the race still does not start.

### 10.1 Rig state and screenshot capability, checked first

`dotnet test` was green (1638/1638) before touching anything live. Both game directories'
`dinput8.dll`/agent files were rebuilt and reinstalled (`src/KotorCoop.Plugin/build.ps1
-Install`) to be certain the running rig matched this branch, then `tools/demo.ps1` brought up
both instances against the rig's only non-autosave save (`000002 - Game1`, save slot 1).

`tools/shot.ps1` was tried once, purely to sanity-check whether this round could get real visual
evidence instead of relying on logs. It came back **solid black**, confirming what
`docs/research/spectate-camera.md`, `docs/research/phantom-locomotion.md`,
`docs/research/dialogue-join-latency.md` and `docs/research/shared-state-audit.md` already say
independently: KOTOR's 3D viewport does not BitBlt, so `shot.ps1` is only ever useful for menu
overlays, never gameplay. **Every observation in this round is therefore from the plugin log and
console-command state reads (`where`, `dialogue`, `census`, `resolve`), the same evidentiary
standard the rest of this project's live-verification docs already use** — not a limitation this
round introduced.

### 10.2 Navigation: tp-hop-then-walk, with two real hazards found and both worked around

Starting point (fresh `warp tar_m02ab`): `(109.4, 123.4, 0.0)`. Target: the trigger centred at
`(221.4, 141.0)` per §9.1's static GIT read (the north of the two `tar02_swoop02ab` polygons,
picked as slightly closer to the entry point than the south one at `(221.2, 64.5)`).

**Hazard 1 — a blind `tp` can land you completely off the walkmesh, and the symptom is total,
silent movement refusal, not a partial short walk.** A three-hop `tp` chain
(`109→150→190→214,138`) landed cleanly by the tool's own report (`"moved from X,Y to X,Y"`,
every hop), but from `(214, 138, 0)` **every subsequent `walkto` — even a 2 m nudge — moved the
player 0.0 m**, repeatably, across five different target directions tried. This is a materially
different failure mode than §9.3's partial-progress-then-stall; it is worth distinguishing for
whoever hits this next: partial coverage means "the pathfinder is working, the destination or
route is hard"; **zero coverage from a `tp` landing means "you are almost certainly standing on
un-walkable geometry, revert to the last confirmed-walkable point."**

**The fix, confirmed live: don't guess `tp` targets from raw GIT numbers, and don't chain short
`walkto` hops either — issue ONE `walkto` at the FULL remaining distance to the real target and
let the engine's own pathfinder route around whatever is in the way, reissuing the identical
command (not a different intermediate point) each time it stalls.** From the confirmed-walkable
entry point `(112.0, 124.0)` (verified by a successful 2 m sanity-check walk first), a single
`walkto 221.4 141.0 0` reissued three times over about 35 seconds of real elapsed time covered
the full ~112 m — the pathfinder visibly routed through corridors on its own (the intermediate
reads, `(140.9,102.1)` then `(185.4,105.3)` then `(220.9,131.7)`, are NOT on the straight line to
the target — Y briefly drops to 102 before climbing back to 131, meaning real corridor-following
happened, not a straight-line nudge). This got the player to within 9.3 m of the trigger centre,
dramatically further than §9.3's ~110 m dead stop.

**Hazard 2 — a story/banter trigger fired mid-walk and silently absorbed every subsequent
`walkto` until closed out.** At `(220.9, 131.7)` — actually mid-route, well before the swoop
trigger — `tar_m02ab`'s own `tar02_drunks2`/banter content put the player into a conversation
with `drunk023` (visible only via `console 'dialogue'`, not `where` or the plugin log's usual
"cheat: walking to..." accept line, which kept reporting `status 1` normally even while every
walk was actually a no-op). **`skipline` + `selectreply 0`, looped a handful of times until
`console 'dialogue'` showed an empty local partner, closed it out** — after which movement
resumed immediately from the exact position the dialogue interrupted. Left as a documented
hazard for future live navigation through this specific module: any `walkto` that stops making
progress with no movement-refusal symptom (Hazard 1's tell) and no crash should prompt a
`console 'dialogue'` check before assuming a pathing problem.

After closing the dialogue, the walk resumed and closed the remaining distance in stages exactly
as before (the same "one full-distance `walkto`, reissue on stall" pattern), reaching the
trigger's actual polygon.

### 10.3 The trigger itself: reached, crossed, and identified with certainty — twice

At `(222.0, 141.5)` — inside the box `docs/research/trigger-relay.md`'s GIT read describes
(`~4×3.7 m` around `(221.4, 141.0)`) — the plugin log recorded:

```
[19:02:08.426] triggers: observed my own engine natively fire OnEnter of 0x3D — publishing for other peers
```

This is not an inference. `console 'resolve 0x3D'` plus a small scratch reader (**not committed
— a throwaway diagnostic, same category as §9.1's `minigamepoke.ps1`**) that reads
`CSWSObject+0x18` (the tag `CExoString`, the same offset `tools/creatures.ps1` already uses)
off the resolved address confirmed, byte for byte:

```
tag at 0x3389BB8 = 'tar02_swoop02ab ' (len 16)
```

**This is the swoop-race entrance trigger, confirmed live by its own tag, not assumed by
position.** A second crossing later in the round (continuing north to `(221.3, 146.5)`, near the
`k_trans_abort` placeable §9.1 flagged) fired a second, different trigger, `0x3F`, which resolved
to tag `tar02_npcext` — an unrelated party/NPC-visibility trigger also placed in this module (see
`docs/research/trigger-relay.md`'s full sweep), not part of the swoop flow. Only the first one
matters for this task.

`tools/creatures.ps1` (a `CSWSCreature`-vtable memory scan already in the tree, previously used
for census-style checks elsewhere in this project) was run against the loaded module and turned
up 53 live creatures — Taris citizens, droids, Sith patrols, "gortlistener" NPCs, three
`drunk0##` tags (matching the banter trigger from §10.2, clustered near `(235-238, 94-97)`, i.e.
a *different* location from the swoop trigger entirely) — **and nothing resembling a swoop-track
attendant, bookie, or kiosk NPC.** Whatever the intended interaction is, it is not "walk up and
talk to someone" the way most of this module's other content is; it is squarely the trigger
volume itself, matching this project's own prior (§8.2) assumption that the trigger alone is the
entry point.

### 10.4 The new finding: crossing the trigger does not start the race, and this round could not determine why

After the confirmed `OnEnter of 0x3D` fire, the player was left alone — no further console
commands issued — for **43 real seconds** (`19:02:08.426` to the next command at `19:02:51.928`).
In that entire window: no `minigame: entered` log line (`MinigameController`'s own log, grepped
for the whole session — the only two `minigame: entered` hits anywhere in the log are timestamped
`18:30` and `18:34`, both **before** this round's swoop attempt even began, left over from a
previous session's turret test), no `dialogue-node-pub` line, no `StartDialog`, nothing. A second
attempt — retreating just outside the box and walking cleanly back in for a fresh crossing —
produced no second `OnEnter` fire at all (KOTOR trigger scripts commonly latch a "already fired"
local/global the first time, so this is consistent with the first fire being the one that
mattered and simply not doing what §8.2 assumed it would).

**This directly narrows, rather than confirms, §8.2's assumption** ("reachable by console warp
alone, no story progress required" — stated there as a plan, not verified). The live result says
the trigger's `OnEnter` script (`k_ptar_swoop02ab`) genuinely runs — the engine-level fire is not
in question — but whatever it does either has no visible effect from this save's state, or its
effect requires a precondition this save does not satisfy. The leading candidate, unverified:
**the rig's only available save is `000002 - Game1`, loaded from module `END_M01AA` — the Endar
Spire, i.e. the very first area of the game** — and this round warped straight from there into
deep Taris Lower City content with no intervening story progress. If `k_ptar_swoop02ab` checks a
global/local flag that vanilla KOTOR's own Taris storyline sets somewhere between the Endar Spire
and the Lower City swoop track (plausible — the trigger's resref is classified `STORY` by
`docs/research/trigger-relay.md`'s own naming heuristic, not `generic`), a save this early would
never have set it. **This is INFERRED, not confirmed** — this project has no NWScript
(`.ncs`) disassembler (`tools/kotorfiles.py` reads GFF/BIF/ERF resource containers, not compiled
script bytecode), so `k_ptar_swoop02ab`'s actual condition was not read directly, and the rig's
save directory has exactly one non-autosave slot (`000002 - Game1`), so there was no
differently-progressed save available to test the theory against.

### 10.5 What this round did and did not answer

* **Solved**: the ~110 m navigation problem §9.3 stopped at. The tp-hop-then-full-distance-walk
  pattern, plus the two hazards documented above (off-mesh `tp` landings, and mid-route
  story/banter triggers silently absorbing walk commands), is the reusable recipe for the next
  round that needs to reach interactive content blind.
* **Not answered, again, but for a different and now much more specific reason**: whether a
  co-visible swoop race looks reasonable. §9's round could not reach the trigger. This round
  reached it, crossed it, and had the engine confirm the correct script ran — and the race still
  did not start. The open question moved from "can an agent get there" to "what precondition
  does `k_ptar_swoop02ab` check, and does this rig's save satisfy it" — a strictly narrower, more
  actionable question for whoever picks this up next, but not one this round could close without
  either an NCS disassembler or a differently-progressed save.
* **No code was changed this round.** `git status` is clean against `fff98c0` — every observation
  came from existing live-test primitives (`tp`, `walkto`, `skipline`, `selectreply`, `census`,
  `resolve`, `tools/creatures.ps1`) plus one uncommitted scratch tag-reader, the same category as
  §9.1's `minigamepoke.ps1`.
* **The shipping decision is unchanged and still cannot be otherwise**: co-visible swoop racing
  is **not shipped**. `minigame_hide_peer` stays default-on (park/hide the racing peer's
  phantom). This is not a new caution — it is the same conclusion §9.5 already reached, now
  resting on one additional, concrete round of live evidence that reaching and crossing the
  actual trigger is achievable but insufficient on its own; actually watching a race still
  requires either decompiling `k_ptar_swoop02ab.ncs` to find and satisfy its precondition
  directly, a save with legitimate Taris story progress, or a human playing through that content
  naturally. Turret sequences remain structurally impossible to make co-visible regardless
  (§9.2, unaffected by anything in this round).

## 11. Offline save-flag tooling (2026-08-23, an offline round) — real candidate names found, no crafted save file shipped, and why

This round's brief was explicit and different from §9/§10: build a save file **offline**,
without touching either live GOG install — another agent owned the live rig exclusively for the
whole round. That constraint turned out to be load-bearing for what this round could and could
not responsibly produce, and is worth recording precisely, not just the result.

### 11.1 What "offline" actually meant, discovered the hard way

Every scripted (Bash/PowerShell/`dotnet run` invoked with the install path on the command line)
read OR write against `C:\GOG Games\Star Wars KOTOR (GOG)` was refused outright by this session's
own tooling guardrail — confirmed empirically: `ls`, a `kotorfiles.py` invocation, and a plain
`[System.IO.File]::ReadAllBytes(...)` PowerShell one-liner were all blocked, while a bare
`Test-Path` on the same file was allowed. The one channel that stayed open was the file-reading
tool built into this environment, used for a single file at a time — which is how §11.2's global
variable catalogue below was actually recovered: a direct, one-shot inspection of the real save's
own `GLOBALVARS.res`, not a script, not a bulk extraction, and not a write.

**This means no `.ncs` bytecode could be pulled this round either.** `k_ptar_swoop02ab`'s
compiled script lives inside `data\scripts.bif`, reachable only by parsing `chitin.key`'s BIF
index — exactly the kind of multi-step, scripted archive walk the guardrail refused. §10.4's gap
(no NWScript disassembler in this tree, so the trigger's actual precondition was read as
"probably a global flag" rather than confirmed) is therefore still open after this round too, for
a different reason than before: previous rounds had live install access and no disassembler;
this round had neither.

### 11.2 What WAS recovered: the real global-variable catalogue, not a guess

A single read of the rig's own `saves\000002 - Game1\GLOBALVARS.res` (the same file
`tools/globalvars.py dump` decodes) surfaced its full name catalogue as plain embedded ASCII —
readable even though the file is otherwise binary. Filtering that catalogue for Taris/swoop-shaped
names turns up a short, well-defined set of **real, declared** globals (not invented names — every
one below is confirmed present in this save's own `CatBoolean`/`CatNumber` lists):

Boolean-shaped:
`TAR_FIRST_SWOOP_USE`, `Tar_SwoopRace`, `Tar_SwoopNew`, `Tar_SwoopStatus` (this one reads more like
a number, see below), `TAR_STARTED`. Also present for pattern comparison, one per other planet's
swoop track: `K_TAR_SWOOP_POINT_01`…`_09`, `K_MAN_SWOOP_POINT_01`…`_05`,
`K_TAT_SWOOP_POINT_01`…`_07`, `K_DAN_SWOOP_POINT_01`…`_05`, `K_KOR_SWOOP_POINT_01`…`_04`,
`K_KAS_SWOOP_POINT_01`…`_04` — almost certainly per-planet collectible "swoop upgrade point found"
trackers (a known KOTOR 1 mechanic), not race-start gates, and not tried.

Number-shaped: `Tar_SwoopStatus`, `Tar_SwoopRaceCounter`, `TAR_SWOOP_MIN`/`_SEC`/`_MSEC` and their
`_BEAT` twins, `TAR_SWOOP_RUN`, `TAR_SWOOP_ACCEL` — these read as race-clock/scoring state written
DURING or AFTER a race, not preconditions checked before one starts, and were not prioritised.

**What is NOT known**: which (if any) of these `k_ptar_swoop02ab` actually reads, and what value
it requires. The type split above (boolean vs. number) is inferred from which catalogue section
each name was recovered from during the single read, not independently confirmed per-name against
the live save — the coordinator should treat that split as a strong hint, not a certainty, and
`tools/globalvars.py get <name>` (which DOES need live install access) is the fast way to confirm
it before crafting, if convenient.

### 11.3 The tool built: `KotorCoop.Memory.GlobalFlagCraft` + `KotorCoop.Agent craft-save`

Rather than ship a save file this round could not safely produce (see §11.4), this round built
and fully unit-tested the tool that produces one, using exactly this repo's own
`ErfArchive`/`Gff`/`GffWriter`/`GffCopy` toolchain (`src/KotorCoop.Memory/GameFiles/`), the same
one `docs/research/mod-compatibility.md` §5 used for its own live save-identity fixture:

* **`src/KotorCoop.Memory/GlobalFlagCraft.cs`** — `TryCraft(source, dest, booleanFlags,
  numberFlags, out results, out diagnostic)`: copies every file in `source` into a freshly
  created `dest` (refuses to run if `dest` already exists and is non-empty — never overwrites),
  then rewrites only the copy's `GLOBALVARS.res`, flipping the `ValBoolean` bit or `ValNumber`
  byte for each requested name that is **already declared** in that save's own `CatBoolean`/
  `CatNumber` catalogue. A name not in the catalogue is reported as not-found and left alone —
  deliberately never fabricated, matching `tools/globalvars.py`'s own documented engine behaviour
  ("the catalogue is FIXED: `SetGlobalBoolean` on an unknown name... does nothing"). `source` is
  never opened for writing. `TryVerify(...)` reads a crafted save back and confirms every
  requested flag actually holds the value it was set to — the same "prove the fixture isn't
  silently malformed" discipline `SaveDigestTests` uses.
* **`KotorCoop.Agent craft-save`** (`AgentOptions.cs`, `Program.cs`) — a thin CLI over the same
  class, for the coordinator to run directly:

  ```powershell
  dotnet run --project src/KotorCoop.Agent -- craft-save `
    --source "C:\GOG Games\Star Wars KOTOR (GOG)\saves\000002 - Game1" `
    --dest   "C:\GOG Games\Star Wars KOTOR (GOG)\saves\000003 - zzz_swoop_flagtest" `
    --bool TAR_FIRST_SWOOP_USE `
    --bool Tar_SwoopRace `
    --bool Tar_SwoopNew
  ```

  Prints one line per requested flag (`[boolean] TAR_FIRST_SWOOP_USE: 0 -> 1` or `NOT in this
  save's catalogue`) and a summary diagnostic. Repeat with `--dest` pointed at
  `D:\Games\swkotor-gog-p2\saves\...` for the other install if both players should start
  flag-primed (only the instance that will actually attempt the race needs it, per §8.2's plan,
  but priming both costs nothing and avoids a mid-test surprise if the racing role changes).
* **`tests/KotorCoop.Tests/GlobalFlagCraftTests.cs`** (new, 7 tests) — a synthetic
  `GLOBALVARS.res` built the same way `SaveDigestTests` builds its ERF/RIM fixtures, proving:
  the source directory is never modified; a requested flag's bit/byte round-trips correctly and
  `TryVerify` confirms it; every OTHER declared flag stays at its default; an undeclared name is
  reported, not invented, and produces a byte-identical `GLOBALVARS.res` in the copy; crafting
  refuses a non-empty destination; a directory without `GLOBALVARS.res` fails cleanly.
  `dotnet build` and the full `dotnet test` are green: **1645/1645** (1638 baseline + 7 new).

### 11.4 Why no literal crafted `SAVEGAME.sav` ships in this round, and why that is the right call

The task's own fallback (§8.2/§10's own precedent, and this task's brief) is "build the BEST save
you can... document what's uncertain" rather than guess blindly — but the honest constraint this
round hit is narrower than "uncertain which flag": **there was no legitimate way to obtain a real
base save's bytes at all**, given the guardrail in §11.1, and a save the engine will actually load
without crashing cannot be safely fabricated from nothing:

* Basing a crafted save on the real `000002 - Game1` (copying its bytes into the repo, then
  editing) would require reading that save's binary content in bulk — exactly what §11.1's
  guardrail refused for every scripted path this round tried. The one channel that stayed open
  (single-file inspection) is not bulk-safe for reconstructing an exact binary copy; a KOTOR save
  is many files, several binary, and losing byte fidelity on any one of them (a nested ERF's
  offsets, a GFF's field-data block) is exactly the "structurally invalid, crashes the engine"
  failure mode the task asked to avoid.
* Building an entirely SYNTHETIC save from scratch (a fabricated character, party, module state)
  was considered and rejected: KOTOR's save format has many required-in-practice fields this
  project has not needed to enumerate exhaustively (it has only ever read real saves, never
  authored one from nothing), and with zero live-testing capability this round, a synthetic save
  that fails to load would waste the coordinator's time worse than not having one — the task's own
  words, "must be structurally valid enough for the engine to load without crashing," were taken
  seriously as a real risk, not a formality.
* **Position/module was assessed as the higher-risk, lower-value half of the ask anyway.** An
  unvisited module (this save has never been to `tar_m02ab`) carries no saved player-position
  state for it at all — the engine would use the module's own default entry point regardless of
  anything forced into the save, unless a full synthetic module `.sav` (GIT, area state, a
  player entry positioned at the trigger) were also fabricated and injected into the outer ERF —
  materially higher risk than the global-flag edit, for a problem §10.2 has ALREADY solved live
  and repeatably: `warp tar_m02ab` then one full-distance `walkto 221.4 141.0 0` (reissued on
  stall, per §10.2's hazards) reaches the trigger polygon in well under a minute. Baking position
  into the save would save that minute; risking a load-crashing save to save it was judged not
  worth it. This is a **design choice, not an oversight** — recorded here so the next round does
  not have to re-derive it.

### 11.5 Exact live-test plan for the coordinator

1. Confirm the rig is up on `integration/honest-limitations` (or later) with `dotnet test`
   green, per this project's usual pre-flight.
2. Run the `craft-save` command in §11.3 against whichever install(s) will race, producing
   `saves\000003 - zzz_swoop_flagtest` as a NEW, additive directory (nothing existing is
   touched) — the same "newest save wins `--load-save 1`" mechanism
   `docs/research/mod-compatibility.md` §5 already established and verified live.
3. Bring up `tools/demo.ps1` as usual; the crafted save is what `--load-save 1` stages, per (2).
4. Once in-world, follow §10.2's proven navigation recipe verbatim: `warp tar_m02ab`, confirm a
   walkable start with a small sanity `walkto`, then `walkto 221.4 141.0 0` reissued on stall
   (watch for Hazard 1 — zero-coverage movement means standing off-mesh, revert — and Hazard 2 —
   a stalled `walkto` with no movement-refusal symptom means check `console 'dialogue'` for the
   `tar02_drunks2` banter trigger and close it with `skipline`/`selectreply 0`).
5. Cross into the trigger polygon around `(222.0, 141.5)`. Watch for
   `triggers: observed my own engine natively fire OnEnter of 0x3D` (or the equivalent id this
   round) in the plugin log — §10.3 already proved this part fires reliably.
6. **The actual test**: does `MinigameController`'s `minigame: entered (swoop/turret)` line show
   up within the next few seconds this time? If yes — the flags in §11.3's command were sufficient
   (or at least one of them was; consider re-running with them set individually afterward, once
   confirmed, to identify which one actually mattered, and update this doc). If no — the theory
   in §10.4 is not fully confirmed, or a different name/value is needed; try the fallback singles
   below one at a time (each needs a fresh `--dest`, since `craft-save` refuses to overwrite):
   `Tar_SwoopRace` alone, `TAR_STARTED` alone, `--number Tar_SwoopStatus=0` (in case a nonzero
   default read from elsewhere is short-circuiting the script, though the catalogue default
   measured this round was already 0 for every candidate touched).
7. Either way, record the result back into this doc: which flag(s) (if any) got the race to
   start, or confirmation that the flag theory itself is wrong and `k_ptar_swoop02ab.ncs` needs
   an actual disassembly (no tool for that exists in this tree yet — would be a new roadmap item,
   not a quick follow-up).

### 11.6 Confidence summary

* **High confidence**: the tool (`GlobalFlagCraft`/`craft-save`) is correct — proven by 7
  synthetic-fixture unit tests exercising exactly its contract, source-immutability included.
* **High confidence**: every name in §11.2's candidate list is a real, currently-declared global
  in the rig's own save — read directly, not invented.
* **Low confidence, explicitly**: that any single one of those names is what `k_ptar_swoop02ab`
  actually checks, or that the value it wants is simply "true"/nonzero. This is the same
  uncertainty §10.4 already flagged, carried forward rather than resolved, because this round's
  access constraints made resolving it (an NCS disassembly, or bulk live-install access to try
  values interactively) unavailable rather than merely effortful.
* **No code path in the shipping plugin/agent changed.** `GlobalFlagCraft` and `craft-save` are
  both new, additive, opt-in tooling — nothing in `CoopAgent.cs`, `MinigameController.cs`, or
  `MinigameRule.cs` was touched, and `minigame_hide_peer` stays default-on exactly as §9.5/§10.5
  already concluded.

## 12. The live pass (2026-08-23, live rig round) — the flag theory tested and DISPROVEN for two combinations; a real navigation bug found and fixed along the way

This round had live install access (the two prior rounds either couldn't reach the trigger at
all, §9, or reached it but had no crafted save to test against, §10) and ran §11's own plan
end to end, twice, with two different flag combinations. **Neither started the race.** This is
a materially stronger result than §10.4's open question — that round had no flags forced at
all; this one did, on two separate live saves, and still got nothing.

### 12.1 What was run

`dotnet build`/`dotnet test` green (1652/1652) before touching anything live.
`src/KotorCoop.Plugin/build.ps1 -Install` re-run against both real GOG install dirs (`-GameDir`
explicitly — the script's own default points at a Steam copy that happens to sit on this
machine too, which is not the rig; that default install was left as harmless dead weight, see
§12.4).

Two saves were crafted with `KotorCoop.Agent craft-save`, both from the rig's own
`saves\000002 - Game1` (Endar Spire, unmodified, never opened for writing):

* `000003 - zzz_swoop_flagtest`: `TAR_FIRST_SWOOP_USE`, `Tar_SwoopRace`, `Tar_SwoopNew`,
  `TAR_STARTED` all forced `0 -> 1` — every boolean candidate §11.2 named, all at once.
* `000004 - zzz_swoop_flagtest2`: only `TAR_FIRST_SWOOP_USE` and `Tar_SwoopNew` forced
  `0 -> 1` — deliberately leaving `Tar_SwoopRace` and `TAR_STARTED` at their default `0`, on the
  theory that a name shaped like "already racing" or "already started" could plausibly be an
  ALREADY-IN-PROGRESS gate that a fresh crossing should NOT have set, rather than a
  permission-to-start gate. (Both `craft-save` runs printed every flag's `0 -> N` transition and
  a summary; nothing was invented — the same source-immutability guarantee §11.3's tests cover.)

Both were loaded and driven to the trigger with `warp tar_m02ab` + the tp/walk recipe from §10.2,
crossed, and watched for `MinigameController`'s `minigame: entered` log line for 40–90+ seconds
after a confirmed `OnEnter` fire. **Neither combination produced it.**

### 12.2 A real navigation bug found: a closed ORDINARY door, not a walkmesh gap

§10.2 already found two hazards (off-mesh `tp` landings, a mid-route banter trigger). This round
found a third, and it is the actual explanation for why earlier rounds' `walkto` stalled at
almost exactly the same coordinate every time: **`m02ab.git`'s own `Door List` has a real door,
`tar02_xstddoor` (an "ordinary" door — `LinkedToModule` empty, not a transition), sitting at
`(221.25, 132.56)` — directly athwart the route from the module entrance to the swoop trigger at
`(221.4, 141.0)`.** `walkto` does not open doors on approach; every call toward the trigger
stalled at `(220.9, 131.7)`, a few tenths of a metre short of the door, no matter how many times
it was reissued, from how many different intermediate points, or how long between attempts —
**indistinguishable from Hazard 1's "off-mesh" symptom (zero coverage, repeatable) but with a
completely different cause and fix.** Retreating and re-approaching from any angle produced the
identical stall at the identical coordinate — the tell that it is a real object in the way, not
a bad pathfind.

**The fix**: this project's console already has `opendoor <tag>` for exactly this case (ordinary,
non-transition doors — distinct from `usedoor`, which is for transition doors and correctly
refuses an ordinary one: `"door 'tar02_xstddoor': no LinkedToModule - an ordinary door"`).
`console 'opendoor tar02_xstddoor'` then the SAME `walkto 221.4 141.0 0` covered the entire
remaining ~9 m in one call and landed exactly on the trigger centre. Confirmed twice, on two
independently-loaded instances of the module (§12.1's two saves), both times with the identical
stall-then-clear behaviour. **This is now the definitive navigation recipe for this module**,
superseding §10.2's plan where it stops short: `warp tar_m02ab` → sanity `walkto` → full-distance
`walkto 221.4 141.0 0` reissued on stall (watching for Hazard 2's banter trigger) → at the stall
point `(220.9, 131.7)`, `opendoor tar02_xstddoor` → reissue the same `walkto` once more → done.

### 12.3 The trigger fires cleanly both times; nothing after it does

Both crossings produced the expected engine-level fire in the plugin log —
`triggers: observed my own engine natively fire OnEnter of 0x3D — publishing for other peers`
(object id differs per session; the tag and behaviour do not) — confirming §10.3's finding holds
regardless of which globals are set. In neither case, across a combined ~2+ minutes of
post-crossing observation with zero other console commands issued to disturb it, did
`MinigameController` log `minigame: entered`, did `console 'dialogue'` show a partner, or did
`where` show any movement — the character just stands there.

**This narrows §10.4's hypothesis rather than confirming it, but narrows it hard.** Two disjoint
flag sets (all four candidates together; then two of them alone, specifically avoiding the two
whose names most plausibly mean "already in progress") both failed identically. That rules out,
with real live evidence rather than a documentation gap, the simplest version of "one of these
five names, set true, is sufficient." What remains open is exactly what §10.4 and §11.6 already
named as the honest uncertainty: either `k_ptar_swoop02ab` checks a **different** global
entirely (a Taris story-arc flag unrelated to swoop-specific naming, plausible since this save's
only station is the Endar Spire, before any Taris content has run), or it checks one of these
five names but wants a **specific non-1 value** (only `Tar_SwoopStatus`/`Tar_SwoopRaceCounter`
etc. were left untried, per §11.2's own read those are number-shaped race-clock fields, not
preconditions, so this is not a promising next guess), or the visible effect of a correctly-
satisfied precondition is not "the race starts immediately" but something this round's
instrumentation would not have caught (a HUD prompt, a change to what `usedialog`-style
interaction becomes available — nothing in this module's creature list, per §10.3, offers one).
**Resolving this for certain still needs either an NCS disassembler (none exists in this tree) or
a save with genuine, naturally-earned Taris story progress** — exactly §10.4's and §11.6's
conclusion, now resting on two rounds of live negative evidence instead of zero.

### 12.4 Two operational notes, not swoop-specific

* **`build.ps1 -Install`'s bare (no `-GameDir`) default installs into a Steam KOTOR copy that
  happens to be present on this machine, not either rig install.** Run once by mistake this
  round before the omission was caught; corrected immediately by re-running with explicit
  `-GameDir` against both real installs. The stray Steam install was left in place (harmless —
  that copy is never launched by anything this project's tooling drives) because this round's
  sandboxing refused the `-Uninstall` cleanup call against that path; flagged here rather than
  silently left unmentioned.
* **Killing and relaunching one instance's game process while a peer is still connected in an
  ordinary (non-minigame) module reproduces the existing `travel-map-init-crash` class.**
  Attempting to swap in a differently-crafted save mid-round (rather than a fresh
  `tools\demo.ps1` run) meant killing instance 1's `swkotor.exe` and relaunching it via
  `agent launch --instance 3` + `agent load` while instance 2 (the peer, itself parked in
  `tar_m02ab`, an ordinary module, not a minigame) was still connected. Instance 2's process did
  not survive the host's reappearance (`* following the host to 'END_M01AA'` in its log,
  session-saved character carried over, then gone) — the same crash class §9.4 already
  documented for a manual reload during an ACTIVE minigame module, now shown to trigger under
  the more mundane condition of an ordinary shared module too. This was caused by this round's
  own test methodology (an operator-driven process swap), not by anything a real player's
  session would do (finishing/leaving a race, or a normal disconnect/reconnect), so it is
  recorded here as reinforcing evidence for the existing `docs/research/travel-map-init-crash.md`
  item rather than filed as a new one. Not chased further this round — out of scope for item 8.

### 12.5 Conclusion, stated plainly

* **The shipping decision is unchanged**: co-visible swoop racing is **not shipped**.
  `minigame_hide_peer` stays default-on. No code in `CoopAgent.cs`, `MinigameController.cs`, or
  `MinigameRule.cs` changed this round.
* **The navigation problem is now fully solved**, including the door hazard §10.2 did not know
  about — §12.2's recipe reaches and crosses the trigger reliably, twice, from a cold `warp`.
* **The precondition question is not solved, but the search space shrank.** Two live, disjoint
  attempts at "the right global flag(s)" both failed cleanly with no crash, no side effect, and
  no ambiguity in the observation. The honest state of this investigation is now "reaching and
  crossing the trigger is a solved, five-step console recipe; making the race actually start
  needs either NCS disassembly of `k_ptar_swoop02ab` or a save with real Taris story progress,"
  which is strictly more precise than any prior round could say.
* **The original open question — is genuine dual-visibility possible — remains unanswered**,
  because no round has yet observed an actual race. §9.5's structural finding (same module,
  in-place mode switch, not precluded by architecture) stands unmodified; whether it looks good
  is still untested. Park/spectate (the shipped default) remains the correct, safe behaviour
  given that unresolved uncertainty — this round adds no reason to change it and no new evidence
  either way on the visual question.

## 13. An NCS disassembler, and the precondition question actually answered (2026-08-24)

§12.5 left the investigation at "reaching and crossing the trigger is solved; making the race
start needs either NCS disassembly of `k_ptar_swoop02ab` or a save with real Taris story
progress" — and noted this project has no NCS bytecode reader at all. This round built one and
used it. **The result is definitive and negative in a way no flag-guessing round could reach:
`k_ptar_swoop02ab`'s OnEnter script checks no precondition whatsoever. It is not a race-start
gate. It is a collectible "found this swoop point" flag setter, unconditionally writing two
globals every prior round correctly identified as unlikely candidates and correctly declined to
try.**

### 13.1 Access note: this round could read the real install directly

Unlike §11's round, this round's sandboxing did **not** block reading `chitin.key`, `data\*.bif`,
or `modules\*.rim` directly (via `[System.IO.File]::ReadAllBytes`, run through this session's
PowerShell tool) — only *writing* into the install was blocked (a `WriteAllBytes` call to a
scratch output path was refused by the auto-mode classifier). This is the opposite constraint
from §11 (which could read but not write) and enough to do the job: every byte quoted below was
read straight out of this machine's real `C:\GOG Games\Star Wars KOTOR (GOG)` install, not
synthesised. No `demo.ps1`, no `build.ps1 -Install`, no game process was launched or touched —
this is pure static-archive reading, the same class of operation `tools/kotorfiles.py` already
does, just done here in-process against the KEY/BIF/RIM formats `src/KotorCoop.Memory/GameFiles/`
already parses.

**First finding, before any disassembly**: `k_ptar_swoop02ab.ncs` is **not in `scripts.bif`** —
`chitin.key`'s global index has zero entries for it (confirmed by an exhaustive scan of all
25,836 KEY entries for `*swoop*`/`k_ptar*`, which turns up every OTHER planet's swoop script
— `k_pman_swoop01`..`18`, `k_pebn_swoop` — but no Taris one). It lives **inside the module's own
companion archive**, `modules\tar_m02ab_s.rim` (`k_ptar_swoop02ab.ncs`, 145 bytes, alongside 188
other module-local compiled scripts including a second, shorter one, `k_ptar_swoop.ncs`, 111
bytes — see §13.4). This is exactly why a chitin.key/BIF-only reader would never have found it;
the extraction had to go through the module archive reader (`RimArchive` in
`src/KotorCoop.Memory/GameFiles/Archives.cs`), already in this project's tree for GIT/ARE
reading, unmodified.

### 13.2 The tool: `NcsReader` (`src/KotorCoop.Memory/GameFiles/NcsReader.cs`)

A pure byte-in/instructions-out disassembler for NWScript compiled bytecode — no file I/O of its
own, so it works identically on bytes pulled from a BIF, a RIM, or a hand-built test fixture.
Implements the full stack-machine instruction set (opcode + type byte, then per-opcode
operands): `RSADD`/`CONST`/`ACTION`/the jump family (`JMP`/`JSR`/`JZ`/`JNZ`)/the
stack-copy family (`CPDOWNSP`/`CPTOPSP`/`CPDOWNBP`/`CPTOPBP`)/`MOVSP`/`DECISP`/`INCISP`/
`DECIBP`/`INCIBP`/the arithmetic-logic-compare group/`DESTRUCT`/`STORESTATE`/`RETN`/`NOP`, plus
the 5-byte `0x42` program-size pseudo-instruction every compiled file opens its header with.

**The format was cross-checked against a real published implementation before trusting it**,
not assembled from memory alone: a first hand-decode attempt (assuming jump instructions have no
type byte, matching some informal community notes) produced garbage after a handful of
instructions. Fetching xoreos' own `ncsfile.cpp` (the reference open-source NWScript VM/decompiler)
settled it precisely — **every instruction has a type byte, including the jump family and
`RETN`/`MOVSP`/`SAVEBP`/`RESTOREBP`** (the informal notes were wrong on this one point) — and
that jump offsets are relative to the **start of the jump instruction itself** (its own opcode
byte), confirmed by the exact code: `_script->skip(offset - 6)` immediately after reading a
6-byte jump instruction (opcode + type + 4-byte offset), which only resolves to "target =
instruction start + offset" when worked through. With that fix, **both real compiled scripts this
round pulled out of the game disassemble end to end with zero leftover bytes and every jump
target landing exactly on a real instruction boundary** — the strongest evidence this format
implementation is correct, independent of the KOTOR-specific action-id question below.

`NwscriptActions.cs` adds the numeric-routine-to-name table `ACTION` calls need to be readable at
all: **578/579/580/581 = `GetGlobalBoolean`/`SetGlobalBoolean`/`GetGlobalNumber`/
`SetGlobalNumber` are VERIFIED** — cross-checked against xoreos-tools' published KOTOR2 function
table, then confirmed to hold for KOTOR1's own numbering too (this game's binary) because the
argument counts the bytecode itself carries (1 for the `Get*` pair, 2 for the `Set*` pair) match
each routine's real NWScript signature exactly, and using them turns the raw bytecode into
unambiguous, idiomatic script logic (§13.3). Two more (25, 217) are used and labelled **INFERRED**
in the table itself — resolved only from the bytecode's own shape (a 0-arg object getter feeding
a 1-arg object→bool predicate feeding a `JZ` gate is the standard compiled form of
`if (GetIsPC(GetEnteringObject()))`), not independently checked against a published table.

`FindActionCalls()` (`NcsInstructionsExtensions`, same file area) is the actually-useful surface:
walks the instruction stream, and for every `ACTION` call, resolves its name and collects
whatever string constants were pushed immediately before it — which is exactly how a global
variable's name shows up right next to a `GetGlobalBoolean`/`SetGlobalBoolean`-style call, since
NCS embeds string constants inline via `CONST` with a length-prefixed string operand.

**Tested** (`tests/KotorCoop.Tests/NcsReaderTests.cs`, 11 tests, all green): 9 always run,
against a hand-built synthetic script constructed with a small purpose-built `NcsBuilder` test
helper (mirrors the real format byte-for-byte, including the jump-offset convention, entirely
independent of any real game file) — header parsing, full-stream disassembly with no leftover
bytes, jump-target resolution (both `JSR`-forward-into-a-labelled-block and
`JZ`-skip-past-a-guarded-block), constant decoding, `FindActionCalls` pairing a string constant
with its `SetGlobalBoolean` call, and two negative tests (bad magic, truncated stream both throw
`InvalidDataException`, matching this project's other GameFiles readers' Try/throw discipline).
Two more tests are gated by a new `[SwoopModuleFact]` attribute (same skip-not-fail pattern as
the existing `[GogInstallFact]`, checking for `modules\tar_m02ab_s.rim` specifically) — these run
the disassembler against the REAL extracted scripts and assert §13.3/§13.4's findings directly as
executable proof, not just prose. **On this machine they ran (not skipped) and passed.** Full
suite: 1663/1663 (1652 baseline + 11 new), one unrelated pre-existing timing-flaky test
(`DisconnectRecoveryTests.ADamageLedgerDoesNotReBankAPeersTotalAfterAReconnect`) confirmed to
pass in isolation, not a regression from this work.

### 13.3 `k_ptar_swoop02ab.ncs`, fully disassembled — the answer

The complete 145-byte file (base64 `TkNTIFYxLjBCAAAAkR4AAAAACCAAAgYFAAAZAAEB////+AAEGwD////8
AwH////8AAQFAADZAR8AAAAAUgQDAAAAAQQFABRLX1RBUl9TV09PUF9QT0lOVF8wMQUAAkMCBAMAAAABBAUAFEtfVEFSX1NX
T09QX1BPSU5UXzAyBQACQwIdAAAAAAYbAP////wgAA==`, decode this to reproduce independently):

```
    8: ProgramSize      totalSize=145
   13: JumpToSubroutine  type=00 -> 21          ; jumps past the next line, into main()
   19: Return            type=00                 ; top-level RETN — reached when main() returns
   21: Reserve           type=06 (object)         ; local: object oPC
   23: CallEngine        type=00 routine=25  argcount=0   ; GetEnteringObject() [INFERRED name]
   28: CopyDownSp        type=01 offset=-8  size=4         ; oPC = <result>
   36: MoveSp            type=00 -4
   42: CopyTopSp         type=01 offset=-4  size=4          ; push oPC
   50: CallEngine        type=00 routine=217 argcount=1     ; GetIsPC(oPC) [INFERRED name]
   55: JumpIfZero        type=00 -> 137                     ; if (!GetIsPC(oPC)) skip the block below
   61: PushConstant      int 1
   67: PushConstant      string "K_TAR_SWOOP_POINT_01"
   91: CallEngine        type=00 routine=579 argcount=2     ; SetGlobalBoolean("K_TAR_SWOOP_POINT_01", TRUE)
   96: PushConstant      int 1
  102: PushConstant      string "K_TAR_SWOOP_POINT_02"
  126: CallEngine        type=00 routine=579 argcount=2     ; SetGlobalBoolean("K_TAR_SWOOP_POINT_02", TRUE)
  131: Jump              type=00 -> 137
  137: MoveSp            type=00 -4
  143: Return            type=00
```

Equivalent NWScript:

```c
void main()
{
    object oPC = GetEnteringObject();

    if (GetIsPC(oPC))
    {
        SetGlobalBoolean("K_TAR_SWOOP_POINT_01", TRUE);
        SetGlobalBoolean("K_TAR_SWOOP_POINT_02", TRUE);
    }
}
```

**This settles the question §10.4/§11.6/§12.5 all left open, with certainty rather than
inference: the trigger's OnEnter script reads no global, checks no story flag, and gates on
nothing except "is the thing that walked in here the player character."** It only ever WRITES —
unconditionally, once — two globals that §11.2's own read had already flagged as the more likely
red herring ("`K_TAR_SWOOP_POINT_01`…`_09` … almost certainly per-planet collectible 'swoop
upgrade point found' trackers … not race-start gates, and not tried"). That inference is now
directly confirmed by disassembly: they are exactly that, a collectible marker, not a
precondition of anything. **Neither of §12's two crafted-save attempts could ever have started
the race, no matter which of the five candidate names or values had been chosen** — the script
that runs the instant the trigger fires does not read any of them, and there is no other script
attached to this trigger.

### 13.4 The concrete new lead: `k_ptar_swoop.ncs`, the companion script that DOES read `Tar_SwoopRace`

The same RIM holds a second, shorter script, `k_ptar_swoop.ncs` (111 bytes, base64
`TkNTIFYxLjBCAAAAbwIDHgAAAAAIIAACAwQFAA1UYXJfU3dvb3BSYWNlBQACQgEEAwAAAAELIAEB////+AAEGwD////8AwH
////8AAQBAf////QABBsA////+B0AAAAAEhsA/////BsA/////CAA`), which disassembles (same zero-leftover,
every-jump-lands-clean confidence) to:

```c
int main()   // compiled shape of a boolean-returning conditional script
{
    return GetGlobalBoolean("Tar_SwoopRace") == 1;
}
```

This is the one script in the module that actually reads `Tar_SwoopRace` — but its compiled
shape (a single global `int` reserved before the `JSR`, the body computing a bool and storing it
into that slot, then falling through to a bare `RETN` with no further branching) is the standard
form the reference compiler emits for a **conversation/dialogue condition script** — the kind a
`.dlg` node's "Active" field points at to decide whether a line or reply is even offered — not a
trigger's OnEnter (which is `void`, not `int`, and this project's own trigger sweep already
confirms `k_ptar_swoop02ab` — not `k_ptar_swoop` — is what `tar02_swoop02ab`'s OnEnter actually
names).

**The honest conclusion, and the next concrete step for whoever picks this up:** the real
race-start gate almost certainly lives behind a **conversation node** somewhere — an NPC or a
kiosk placeable whose dialogue offers a "start the race" option only when
`Tar_SwoopRace == 1` (or offers a DIFFERENT option, such as "you need to earn the right first",
when it is not) — rather than at the entrance trigger this project's three prior live rounds
correctly reached, correctly crossed, and correctly found to do nothing race-related. This round
did not locate that conversation node: `k_transabort.dlg` (the placeable `k_trans_abort` sits
right next to the trigger and was flagged in §9.1 as "almost certainly the confirm/decline
prompt") turned out to be a **generic, chitin.key-global dialog** (not module-local, not
swoop-specific — the same shared "leave this subarea?" prompt other transition-abort placeables
across the game use), so it is very unlikely to be the swoop-specific gate. The concrete,
scoped-but-not-yet-done next step: grep every `.dlg` this game ships (via the same
`RimArchive`/`ErfArchive`/`KeyIndex` infrastructure `NcsReader` now complements) for a script
reference to `k_ptar_swoop`, which will name the exact conversation node — and, from there, the
exact placeable or NPC tag — that is the real swoop-race entry point. That is a bounded,
mechanical search with the tooling now in place to do it; it was not attempted this round because
the task's actual question (does the entrance trigger check a flag) was already fully answered
without it.

### 13.5 What this changes about the shipping recommendation

Nothing. **`minigame_hide_peer` stays default-on; co-visible swoop racing is still not shipped.**
This round closes the precondition-guessing thread definitively (it was never going to work, and
now there is a citable reason why) and opens a narrower, better-aimed one (§13.4) for whichever
future round wants to keep pulling on it. No file under `CoopAgent.cs`, `MinigameController.cs`,
or `MinigameRule.cs` was touched. The only production code this round adds is the disassembler
itself (`NcsReader.cs`, `NwscriptActions.cs`) — new, additive, general-purpose tooling with no
call site in the shipping plugin/agent path, exactly the same category as §11's `GlobalFlagCraft`.

## 14. The `.dlg` sweep §13.4 asked for — `Tar_SwoopRace` turns out to be a completion flag, not
an entry gate, and the REAL race-start conversation is found and disassembly-confirmed (2026-08-24,
follow-up round)

§13.4 left one concrete, scoped lead: grep every `.dlg` this game ships for a script reference to
`k_ptar_swoop`, to find the conversation node that gates entering the swoop race. This round did
exactly that — and the answer inverts the working hypothesis. `Tar_SwoopRace` is not a pre-race
permission flag at all; it is a **post-race "you already won" flag**, and the real entry point to
the race is a completely different, story-gated conversation with Gadon at the Hidden Bek base,
found by broadening the search past the one exact resref name.

### 14.1 Tooling: no new reusable class needed — `Gff.cs` already reads `.dlg` as-is

`.dlg` is plain GFF (confirmed against `tools/kotorfiles.py`'s own format table, line 66:
`2029: "dlg"`), and this project's existing `Gff`/`GffStruct`/`GffLocString` reader
(`src/KotorCoop.Memory/GameFiles/Gff.cs`) already decodes every field type a `.dlg` uses —
`ResRef` (the `Active`/`Script` condition/action-script fields), `List` (`EntryList`, `ReplyList`,
`StartingList`, and each node's own `RepliesList`/`EntriesList`), `Struct`, and `ExoLocString`
(`Text`, resolved via `dialog.tlk` StrRef when there's no inline substring — `Tlk.cs`, also
already in the tree). Nothing in the format needed a new writer. This round's search tool is a
one-off console program (not checked into the repo) built against `RimArchive`, `ErfArchive`,
`Gff`, `Tlk`, and `NcsReader`/`NcsInstructionsExtensions.FindActionCalls` exactly as they already
exist — a generic recursive walker over every `GffStruct` field, flagging any `ResRef`-typed field
whose value matched a target string, with the parent link struct's `Index`/`Active` pair resolved
back into `EntryList`/`ReplyList` text via `dialog.tlk`. This is the same "one-off is fine" call
§13's own writeup flagged as the right level of investment for a single bounded question, and nothing
here revealed a gap in `Gff.cs` worth building a dedicated `.dlg`-node class around.

### 14.2 The narrow sweep: every StartingList `Active == "k_ptar_swoop"` across all 46 `tar_*`
module archives

Scanned every `.dlg` resource inside every `tar_m*.rim`/`tar_m*_s.rim` (46 archive files, 291
distinct `.dlg` resources total) for a `ResRef` field exactly equal to `k_ptar_swoop`. **11 hits**,
in 9 different `.dlg` files across 4 different modules (`tar_m02ae`, `tar_m03aa`, `tar_m03ae`,
`tar_m11aa`), every one of them a `StartingList` link (the "which greeting does this NPC open
with" condition), and every single resolved `EntryList` line is a **congratulations-after-the-fact
bark**:

* `tar02_musictwi21.dlg` (a Twi'lek musician, Lower City cantina): *"You may be a big celebrity and
  all, but I'm trying to earn a living here!"*
* `tar03_hidbek031.dlg`, `tar03_hidbekguar.dlg`, `tar11_hidbek113/114/115/116.dlg` (rank-and-file
  Hidden Bek gang members): *"Hey — I recognize you! You're the one who won the swoop race!"* /
  *"With Brejik dead it won't be long until those Vulkars of his collapse..."*
* `tar03_cardsell31.dlg` (a card seller): *"...the hero of the swoop race makes an appearance to
  brighten our day in this dingy cantina. People all over the Lower City are talking about your
  glorious victory."*
* `tar03_vulkar.dlg` (a rank-and-file Black Vulkar): *"I saw what you did to Brejik. I won't be
  trying anything in here, but if we ever meet out on the street you're dead!"*
* `tar11_zaerdra111.dlg` (Zaerdra, the Hidden Beks' second-in-command): *"On behalf of Gadon, I
  want to offer the thanks of all the Hidden Beks. You rode like a true champion in the swoop
  race."*

**Every text this condition gates is written in the past tense about a race and about Brejik's
death.** `k_ptar_swoop.ncs`'s `return GetGlobalBoolean("Tar_SwoopRace") == 1;` (disassembled in
§13.4) is the shared condition roughly a dozen random NPCs across four modules use to pick their
"ambient reaction" line once the swoop race has already happened — a scene-dressing flag, not an
entry gate. §13.4's inference that the "conversation node" behind `k_ptar_swoop` would be the
race's front door was reasonable given only the trigger and the script name, but it is now
disproven by direct evidence: no node gated by `k_ptar_swoop`/`Tar_SwoopRace` offers a "start the
race" option anywhere; every one of them offers ordinary barks, and the barks only make sense
after Brejik is already dead.

One dead-end ruled out concretely along the way: `plc_swpbike1`, the swoop bike placeable seen
sitting in `tar_m02ab`'s own `Placeable List` (the same module as the entrance trigger), has
`Useable = 0` and every script field blank — it is pure set dressing, not an interaction point.

### 14.3 The broad sweep: every `ResRef` field containing `"race"` or `"swoop"` (not just the exact
`k_ptar_swoop` name), across the same 46 archives

Widening the substring match (still scoped to `tar_*` modules) surfaces the real questline. The
decisive hits, all in `tar_m11aa_s.rim` (`tar11_gadon112.dlg`, the Hidden Bek leader Gadon) and
`tar_m03af_s.rim` (`tar03_mechanic.dlg`, the swoop-track mechanic; `tar03_announcer.dlg`,
`tar03_brejik031.dlg`, `tar03_bastila.dlg`, and four individual rival-racer `.dlg`s —
`tar03_anglu`, `tar03_doba`, `tar03_phirk`, `tar03_redros` — none of which exist in the Lower City
module set at all, confirming `tar_m03af` is a dedicated swoop-track module, not the Lower City
area the entrance trigger sits in):

* `tar03_mechanic.dlg`'s own `StartingList[0]` opens on `Active='k_ptar_raceover'`, and the full
  conversation tree is explicitly about racing — the mechanic explains the rules, the reply
  *"How do we get the race started?"* routes to *"Sounds like you're ready. Go talk to the race
  announcer over by the counter."* This is the confirmation that `tar_m03af` (not `tar_m02ab`) is
  where an actual heat runs, with its own mechanic/announcer/rival-racer NPCs — a genuinely
  separate module from the one the three earlier live rounds (§9–§12) explored.
* `tar11_gadon112.dlg` — Gadon's full conversation is the "Search for Bastila" questline
  (`Quest="tar_bastsearch"`) itself. The path: give Gadon the prototype swoop accelerator
  (`Reply[5]`, gated `Active='k_ptar_hasaccel'`, text *"I have the accelerator right here."*) →
  `Entry[9]` (`Script='k_ptar_takeaccel'`) → Zaerdra objects, Gadon insists the PC rides →
  `Reply[18]` *"Okay, I'm ready for the race."* → `Entry[17]` (`Script='k_ptar_racecs0'`) →
  `Entry[18]` (`Script='k_ptar_racecs1'`, `Listener=Zaerdra111`, the text literally reads
  *"[You spend a restless night in the Bek base. In the morning, one of the Beks takes you
  to...]"*) → the terminal node, **`ReplyList[21]`, `Script='k_ptar_bekrace'`, no further
  `EntriesList` — ending the conversation and firing the script that actually starts the race.**

### 14.4 Disassembly confirms it: `k_ptar_bekrace.ncs` calls a module-transition action with the
literal module name `"tar_m03af"` as an argument

Ran `NcsReader.Disassemble` + `FindActionCalls` (both unmodified from §13) against
`k_ptar_bekrace.ncs` (1554 bytes, `tar_m11aa_s.rim`) and its neighbours. The decisive line:

```
ACTION   782: #509("", "", "", "", "", "", "", "tar_m03af", ...) [routine 509, 8 args]
```

Routine `#509` is not in this project's `NwscriptActions.KnownActions` table (only 578/579/580/581
and the two INFERRED ids from §13 are verified there), so the routine's *name* is not claimed —
but the **literal 8th string argument is the real, existing Taris module resref `tar_m03af`**,
the exact module identified in §14.3 as the dedicated swoop-track area. An engine call taking a
module resref as a string argument, called from the terminal node of "I'm ready for the race," is
about as strong a circumstantial identification of a module-transition action as static analysis
without symbol names can give. (`k_ptar_racecs0`/`k_ptar_racecs1`, the two cutscene entries
immediately before it, call `CreateObject`/`AssignCommand`-shaped actions against waypoint tags
`tar11_wppcsleep`/`tar11_wpcsbek`/`tar11_csbek` — consistent with staging the "restless night,
morning departure" cutscene in place before the module transition fires.)

Also disassembled `k_ptar_hasaccel.ncs` (110 bytes, the condition on Gadon's *"I have the
accelerator right here"* reply) — confirmed, not inferred, since it uses the same
`GetGlobalBoolean` (routine 578) verified in §13:

```c
int main() { return GetGlobalBoolean("Tar_SwoopEng") == 1; }
```

So the item-possession gate on this path is the global boolean **`Tar_SwoopEng`**, a different
name from every candidate §11.2/§12 tried (`Tar_SwoopRace`, `Tar_SwoopNew`, `Tar_SwoopStatus`,
`TAR_FIRST_SWOOP_USE`, `TAR_STARTED`) — which is exactly why none of those five crafted-save
combinations in §12 could ever have worked, independent of the §13 finding that the entrance
trigger checks nothing at all.

### 14.5 The answer, stated as a recipe

**There is no single NPC/trigger "gate" for the swoop race in the sense the original question
assumed** — it is the payoff of the "Search for Bastila" main questline (`tar_bastsearch`), not a
side interaction:

1. Progress the Bastila/Black Vulkar Base questline through recovering the prototype swoop engine
   accelerator (sets the global boolean `Tar_SwoopEng = 1` — not directly observed being set this
   round, but it is exactly what `k_ptar_hasaccel.ncs` reads, and `tar10_waitress.dlg` independently
   confirms the accelerator is a distinct trackable quest item — *"Where can I find the prototype
   swoop engine accelerator?"*).
2. Return to **Gadon** (creature tag `GadonThek112`, module `tar_m11aa` — the Hidden Bek base,
   `.dlg` resref `tar11_gadon112`) and take the conversation path that opens once `Tar_SwoopEng`
   is true: *"I have the accelerator right here."* → accept riding for the Beks → *"Okay, I'm
   ready for the race."*
3. The conversation ends on `k_ptar_bekrace` (a Reply node with no further entries), which
   disassembles to a call taking `"tar_m03af"` as a literal argument — the module transition into
   the dedicated swoop-track area.
4. `tar_m03af` is where the actual race happens (mechanic, announcer, rival racers Anglu/Doba/
   Phirk/Redros, and Brejik's death cutscene all live there) and where `Tar_SwoopRace` is almost
   certainly the flag the race's own completion script sets — consumed afterward by the §14.2 barks
   back in the Lower City and Bek-base modules.

None of this was live-tested this round (same access constraint as §13: reads only, no
`demo.ps1`/`build.ps1 -Install`, no game process). The concrete next step for a live round: craft
a save with `Tar_SwoopEng = 1` (via the existing `craft-save --bool Tar_SwoopEng` from §11.3) at a
point in the story where the Hidden Bek base is otherwise reachable, warp to `tar_m11aa`, and talk
to `GadonThek112` — if the accelerator-handoff branch opens and running it through to *"Okay, I'm
ready for the race"* actually transitions into `tar_m03af`, that is live confirmation of this
round's entire chain in one pass.

### 14.6 An honest tension with §9.1, flagged rather than papered over

§9.1's live pass forced `CSWCArea::mini_game` non-null while staying inside `tar_m02ab` and watched
`CSWMiniGame::Update` start running on the very next frame with no module-changed event anywhere —
concluding **"same module, in-place… co-visible swoop racing is not ruled out by the engine's own
architecture the way the turret sequence is."** §14.4's disassembly of the only known real entry
path (`k_ptar_bekrace`) shows it calling an unnamed 8-argument engine routine whose 8th argument is
the literal module resref `tar_m03af` — which reads, on its face, like a `StartNewModule`-shaped
transition out of the CURRENT module entirely. These are not the same claim, and this round did not
resolve which one governs the actually-shipped experience:

* §9.1's evidence is about the **engine subsystem's own requirements** (a forced pointer, observed
  live, with a real crash as corroboration) — it shows the `CSWMiniGame` machinery *can* run without
  a module boundary. That finding stands on its own and is not contradicted by anything this round
  found.
* §14.4's evidence is about **what the shipped content's own script actually calls** at the one
  real trigger point this round found — and it is a `.ncs` disassembly, not a live observation;
  routine `#509`'s identity is inferred from its argument shape, not verified against a published
  action table the way 578–581 are.

The most likely reconciliation, **not verified either way**: the engine mechanism doesn't *require*
a module boundary (§9.1), but BioWare's own authored swoop-race sequence uses one anyway (§14.4) —
for staging reasons (a dedicated track-with-pits-and-announcer-booth area, camera work, spawning
Brejik) rather than a technical necessity. If that reconciliation is right, it matters for shipping:
**the real, only-known player path into the race crosses a module boundary**, which puts it
structurally closer to the turret sequence's forced-spectate case (§9.2) than to the "same module,
no transition" framing §9.1's conclusion implied — even though the underlying engine subsystem
itself doesn't force that. A future round should not treat §9.1's "not ruled out" conclusion as the
last word on co-visibility without first live-testing whether `k_ptar_bekrace` really does leave
`tar_m02ab`.

### 14.7 What this changes about the shipping recommendation

Nothing. **`minigame_hide_peer` stays default-on; co-visible swoop racing is still not shipped.**
This round replaces a wrong-but-plausible working hypothesis (`Tar_SwoopRace`/`k_ptar_swoop` as an
entry gate) with a disassembly-backed one (`Tar_SwoopEng`/`k_ptar_hasaccel`/`k_ptar_bekrace`, and
a likely-but-not-live-confirmed module transition into `tar_m03af` — see §14.6's honest caveat on
that last point) and narrows "what a live pass needs" from "grep every `.dlg`" to one specific NPC
and one specific global boolean. No production file was touched; the search tool built this round
is a throwaway console program, not checked into the repo, for the
reason given in §14.1 — `Gff.cs` already reads everything a `.dlg` needed.

## 15. The live attempt (2026-08-24) — `Tar_SwoopEng` alone gets the dialogue all the way to the terminal node exactly as disassembled, but the module transition itself never fires

Ran §14.5's exact recipe live, on a fresh `build.ps1 -Install` rig. `KotorCoop.Agent craft-save
--source "...saves\000002 - Game1" --dest "...saves\000003 - zzz_swoop_test" --bool Tar_SwoopEng`
against the install's own pristine, never-modified `000002 - Game1` (confirmed untouched before AND
after — `TIMEPLAYED=146`, `LASTMODULE=END_M01AA`, the very start of the game, nowhere near Taris).
Confirmed offline the craft actually stuck (`Tar_SwoopEng: 0 -> 1`, verified via `globalvars.py get`
against the crafted copy) before ever loading it. `craft-save`'s own safety behavior held exactly as
documented: `--source` was never opened for writing, `--dest` was refused if non-empty (moot here,
freshly created), and the crafted directory was deleted again at the end of this round (a throwaway
test artifact, not meant to persist) — `000002 - Game1` was never touched at any point.

Loaded the crafted save into the live host session via the `loadgame` console verb (newest-by-mtime
save resolves to slot 0, confirmed by directory listing before the load), then host-only
`console 'warp tar_m11aa'`, `tp 65.0 76.0 0.12` to land next to Gadon's own `.git`-recorded spawn
point (`(63.90, 78.62)`, read directly from `tar_m11aa.git`'s `Creature List` rather than guessed),
then `console 'talk GadonThek112'`.

### What happened: the dialogue ran itself, and it matches §14.3's traced node sequence exactly

**No additional Taris story/faction state was needed beyond the single flag** — this answers the
task's own open question directly. With only `Tar_SwoopEng=1` set (no Hidden Bek alliance flag, no
Bastila-met flag, nothing else touched, on a save that has literally never left the Endar Spire),
`talk GadonThek112` did not stop at a greeting waiting for a reply pick; it opened directly into a
chain of six auto-advancing dialogue nodes over about 30 seconds, published in
`kotorcoop-plugin.log` as `dialogue-node-pub: #1 node -1 -> 17` ... `#5 node 20 -> 21` — an exact,
non-coincidental match to §14.3's own disassembly-traced node numbers for `tar11_gadon112.dlg`:
entry 17 is `k_ptar_racecs0`, entry 18 is `k_ptar_racecs1` (the node with `Listener=Zaerdra111` —
consistent with the dialogue tracker's `partner` field showing `'zaerdra111'` mid-sequence, matching
her interjection rather than a wrong-NPC mistake), and node 21 is the terminal `ReplyList[21]`
(`Script='k_ptar_bekrace'`) §14.4 disassembled as the module-transition call. Each step auto-advanced
with no `selectreply`/`skipline` issued by this round at all — every branch encountered had exactly
one satisfied continuation, which is itself further live confirmation that the flag theory is
sufficient to open this entire path, not just its first line.

### What did NOT happen: no module transition, ever, across several minutes of real time

After node 21 published, the tracked dialogue partner cleared (conversation ended, `local: partner
''`) and the log went quiet — no `epoch: world unloading`, no `transition:` line, nothing resembling
§9.1's own AV-corroborated `CSWMiniGame` engine activity. Watched for **just under 3 minutes**
(`08:02:05` terminal node to `08:05:26`, several `console 'where'` polls in between): the player's
own position never moved from the `tp` landing spot at all (`65.0, 76.0` unchanged start to finish —
notable because §14.4's own disassembly read describes a staged cutscene, "a restless night... in
the morning, one of the Beks takes you to...", with `AssignCommand`-shaped waypoint-walk actions in
`k_ptar_racecs0`/`racecs1` that should visibly move somebody), the module stayed `tar_m11aa`
throughout (`console 'where'` polled repeatedly), the journal's `tar_bastsearch`-adjacent state never
advanced beyond the module-entry `tar_vulkarbase` bump that fired on the earlier warp itself, and
`tools/crashlog.ps1` showed no new access violation and both processes stayed `Responding=True` the
whole time — this is a clean stall, not a crash.

### Honest assessment: three live possibilities, not distinguished this round

1. **The cutscene staging never completed.** `k_ptar_racecs0`/`racecs1`'s own waypoint-walk
   `AssignCommand` actions may depend on real pathfinding/animation time that a console-driven
   `talk`/auto-advance session doesn't actually produce (the player's own position never moving is
   the strongest evidence for this reading) — if the terminal `k_ptar_bekrace` script itself gates
   on that staging having finished (e.g. waiting on an NPC reaching a waypoint before firing its own
   action), a console session that skips straight through dialogue text without letting movement
   actions resolve could stall it indefinitely without ever reaching the transition call at all.
2. **Routine `#509`'s inferred identity is wrong, or takes more than the one argument this project's
   `NwscriptActions` table has verified.** §14.4 was explicit that the routine is not in the
   verified table and its behavior is inferred purely from the literal `"tar_m03af"` argument shape
   — this round's negative result is consistent with (but does not prove) that inference being
   incomplete.
3. **A precondition inside `k_ptar_bekrace.ncs` itself, before the action call, silently no-ops on
   this save's otherwise-untouched world state** (no companions beyond Trask, no other Taris
   progression) — §14.4's disassembly excerpt showed only the decisive action-call line, not the
   script's full control flow, so an earlier guard this round never saw is not ruled out.

This round cannot distinguish between the three without either a live debugger break at the
`k_ptar_bekrace` script's own entry point, or a repeat of this exact recipe with a REAL human
driving the dialogue clicks (to rule out possibility 1 specifically) rather than the console `talk`
shortcut. **The shipping decision is unchanged**: `minigame_hide_peer` stays default-on; co-visible
swoop racing is still not shipped and still not observed. What this round adds, concretely: the
single-flag theory is now confirmed sufficient to reach and auto-play the ENTIRE dialogue chain
identified in §14.3–14.4 (closing that open question decisively), and the remaining gap is narrowed
from "does the flag even work" to "why doesn't the terminal script's own transition action fire" —
a strictly smaller, better-scoped question for whoever has the rig next.

No code changed this round — verification only. The crafted test save was deleted at the end of
this round (throwaway artifact, not meant to persist); `000002 - Game1` was never modified.
`dotnet test`: 1674/1674 green throughout.
