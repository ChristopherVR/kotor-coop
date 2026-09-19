# Save ghosts: a manual save bakes co-op spawns into the module (2026-09-18)

Review blocker #7 (`docs/review-seamless-2026-09-18.md`), confirmed live the same day.

## 1. The mechanism

Everything this plugin spawns into the loaded area is a real engine object: a peer's stand-in
creature (`k_coop_phantom`, tag `coop_phantom`), a materialised enemy body (same blueprint,
repainted), a replicated ground item, and a peer's map-note waypoint pin. `StoreCurrentModule`
serialises the area with all of them in it. For a DOOR transition or a session-commanded warp
the plugin knows the store is coming — the "pre-store flush" window in `PluginHost.Frame`
destroys one body per frame until the area is clean, then lets the transition proceed. A
MANUAL save opens no such window: the retail quicksave (`F4`, `CClientExoAppInternal::DoQuickSave`
0x005F4B50, also what the plugin's own `save` verb calls) and the save panel store the module
with everything standing.

## 2. Measured (2026-09-18, Endar Spire deck, host + one joiner beside it)

| Save | Creature List | WaypointList | Extra |
|---|---|---|---|
| `000002 - Game1` (baseline, single player) | 25 | 58 | — |
| `000000 - QUICKSAVE` (host `save` verb with the joiner present) | 26 | 59 | 1 × tag `coop_phantom`, appearance 114; 1 × untagged enabled map note |

Read with `tools/kotorfiles.py` (`SAVEGAME.sav` → module `.sav` → `m01aa.git`). Every later
load of that save — the host reloading it, a joiner adopting it, a single-player continue —
therefore came up with a ghost NPC standing where the partner stood, a stray pin on the map,
and `Mod_NextObjId` moved by two. The "6/6 clean host reload" rounds of 2026-09-02 never saw
this because `loadgame 0` reloads the engine's own transition autosave, which IS flushed.

## 3. What ships (load-time half)

`GhostSweepController`, armed on every world-epoch flip in `PluginHost.Frame` — the epoch is
only readable once the module is fully loaded, so every GIT object exists — and ticked before
the phantom controller so nothing has been spawned for the new world yet. It collects every
creature (`CreatureFinder.CollectByVtable`, one address-space sweep), queues the ones tagged
`coop_phantom` (`GhostSweepRule.IsGhostCreatureTag`) and destroys them through the same
`Odyssey.DestroyObjectById` the flush uses, one per frame with up to six retries ten frames
apart, because the VM refuses a destroy while a script is mid-call and module OnEnter scripts
run exactly then. Map pins are swept in the same pass (`Odyssey.SweepGhostMapMarkers`):
`SpawnMapMarker` now stamps our pins with the tag `coop_mapnote`, and the sweep also removes
the UNTAGGED enabled map notes that earlier builds spawned — every shipped map note carries a
tag (`sw_waypoint001` on the deck), so an untagged enabled one can only be ours. The waypoint
vtable (`0x0074BF80`) comes from the constructor's own store (`tools/disasm.py 0x5C7E70`) and
is checked against a live object the first time a pin is spawned.

This half also heals saves already written by v0.9.0/v0.10.0 installs that saved with a
partner present. Log lines: `ghost sweep: this save carried N co-op stand-in creature(s) and M
stray map pin(s) ...`, `ghost sweep: destroyed baked-in stand-in 0x..`, `... map pin 0x..`.

## 4. What ships (save-time half, built 2026-09-18 without a rig — NEEDS-LIVE)

Opening the flush window BEFORE a manual save, so nothing is baked in the first place.
`SaveFlushHook` + `SaveFlushRule` + `PluginHost.SaveFlush.cs`; coop.ini `save_flush`, default **OFF**
until the recipe in 4.3 passes. Done: hook, rule, tests, inspector, static byte verification. Unfinished:
live verification. A held save whose frame pass returns early (or is starved of frames under a save panel)
would be a lost save, so it must not ship ON unverified; the load-time sweep covers a ghost either way.

### 4.1 The one entry both save paths reach — VERIFIED (static)

Traced with `tools/xrefs.py` and `tools/disasm.py` against the GOG `swkotor.exe`:

```
F4 / the `save` verb      CClientExoAppInternal::DoQuickSave 0x5F4B50 ── call 0x5ED670 @ 0x5F4BA5
the save panel's Save     CSWGuiSaveLoad::WriteGame 0x6C8790         ── call 0x5ED670 @ 0x6C88BA
                          CClientExoApp::SendSaveGameRequest 0x5ED670 → jmp 0x5EEA90 → jmp 0x6774E0
                          CSWCMessage::SendPlayerToServerModule_SaveGame   (client → server message)
server, next MainLoop     CSWSMessage::HandlePlayerToServerModuleMessage 0x524800, subtype 4 @ 0x524900:
                            number = ReadDWORD; name = ReadCExoString; description = ReadCExoString
                            0x524977 CServerExoApp::SetSaveGameName(&description)  → internal+0x1B93C
                            0x524992 CSWSPlayer::GetIsAllowedToSave(player)
                            0x5249B1 CServerExoApp::SaveGame(number, &name, player, 0)
                          CServerExoApp::SaveGame 0x4AE6E0 = mov ecx,[ecx+4]; jmp 0x4B58A0
                          CServerExoAppInternal::SaveGame 0x4B58A0          ← THE SPLICE
```

`xrefs.py 0x4B58A0`: exactly two sites — the wrapper's `jmp` at 0x4AE6E3 (message handler and
the script autosave at 0x528817 both go through the wrapper) and `MainLoop`'s pending-autosave
`call` at 0x4BAE4C. Nothing else reaches the store: `StoreCurrentModule 0x4B2E70` has two
callers, `StallEventSaveGame 0x4B3110` (0x4B32D1) and the module-transition path (0x4BAAE1).

What 0x4B58A0 does (disassembled 0x4B58A0..0x4B5C4D): `thiscall`, FOUR stack args — `ret 0x10`
at both exits, 0x4B59EE (not enough disk space, returns 0) and 0x4B5C4D (returns 1); the symbol
DB's `param_size 12` is one short. `[ebp+8]` number, `[ebp+0xC]` `CExoString* name`,
`[ebp+0x10]` player (read only at 0x4B59AA, the disk-space failure's status message),
`[ebp+0x14]` never read. It checks disk space, creates and cleans `SAVES:%06d - %s`, then ARMS
the stall record at `[[0x7A39FC]+0x14]` (0x4B5AB1..0x4B5B04: +0=1, +4=2, name/description into
+0x18/+0x20) and returns. The store itself runs on the NEXT `CServerExoAppInternal::MainLoop
0x4BABB0` pass, whose first act (0x4BABD9) is to dispatch an armed record → 0x4BAFB1
`StallEventSaveGame` → `StoreCurrentModule`. So an early return from 0x4B58A0 leaves the engine
exactly as it was before the request — no directory touched, no record armed — and calling it
again later with the same arguments is the same save. The description the handler stored at
internal+0x1B93C is untouched by the hold and still there for the re-issue.

### 4.2 The mechanism

- `SaveFlushHook.Install` splices 0x4B58A0's six-byte prologue (`55 8B EC 83 E4 F8`, relocated
  verbatim, resume 0x4B58A6) and verifies BOTH `ret 0x10` epilogues before patching, because the
  stub's own hold exit (`mov eax,1; ret 0x10`) mirrors them. The stub forwards the four stack
  arguments with the same `[esp+0x30]` push each time plus `ecx`, calls back, and either runs
  the relocated prologue or answers the caller as the function's own success exit would.
- The callback (`PluginHost.OnSaveGameRequested`) passes through when: the splice is off; the
  plugin has not ticked in the world the request is for (`TryGetWorldEpoch` unreadable or ≠
  the frame's `_epoch` — the module-enter autosave's situation, and why it is never held);
  the name cannot be captured; or nothing of ours is standing (`CountSpawnedBodiesStanding`:
  stand-ins + replicated items + materialised bodies + peer pins). Otherwise it captures
  `(this, number, name text, player, arg4)` and HOLDS.
- The frame branch (`RunSaveFlushFrame`, after the door and move-to-module blocks, before the
  session warp) runs the transition window's exact `FlushOneBody` sequence one destroy per
  frame, returning early so nothing re-spawns behind it. `SaveFlushRule` re-issues once the
  area has been clean two frames running (the engine frees a body on its next main-loop pass,
  not inside the destroy call), or after the 120-frame budget WITH bodies still standing —
  a lost save is worse than a ghost, and the load-time sweep heals the ghost. The re-issue is
  `Odyssey.ReissueServerSaveGame`: the same call with a fresh `CExoString` from the captured
  name, refused if the server object changed. It re-enters the splice, which passes it
  through because the rule is already in its quiet phase. Four quiet frames later the
  controllers resume; `ItemController.Invalidate()` is the one nudge needed (a destroyed item
  slot keeps its wanted id and would not re-spawn otherwise — the same nudge the aborted-
  transition path gives it); stand-ins, materialised bodies and pins re-spawn on their own.
- A second request during the hold is coalesced (the latest slot/name wins, one re-issue); a
  request in the quiet window passes through (the area is clean). A module change during the
  hold DROPS the held save (logged) rather than re-issuing into whatever is loading.
- Log lines: `save flush: holding the save (slot N, 'NAME') while M body(ies) are destroyed`,
  `save flush: re-issued the save (slot N, 'NAME') after F frame(s) - D body(ies) destroyed, L
  left standing` (plus `; the flush budget ran out ...` on the unclean path), `save flush:
  released; the controllers rebuild what the flush destroyed`, and the startup line
  `save flush: manual saves flushed first: CServerExoAppInternal::SaveGame owned at 0x4B58A0`.

Unit-tested (`SaveFlushRuleTests`, 9 tests): never held with nothing standing; held-then-
re-issued once clean; the re-issue passes through; the quiet window releases; a body that
keeps refusing cannot lose the save; coalescing; the settle count resets on a straggler; Reset.

### 4.3 NEEDS-LIVE recipe

1. Set `save_flush=1` in both coop.ini files, then `demo.ps1` (host + joiner beside it on the Endar Spire deck). Expect the host's startup log
   line `save flush: manual saves flushed first: ... owned at 0x4B58A0`. If it says `save flush
   NOT installed: prologue/epilogue mismatch`, the bytes differ — stop, disassemble, do not
   force it.
2. Host `save` (or F4). Expect, in order: `save flush: holding the save (slot 0, 'QUICKSAVE')
   while 2 body(ies) are destroyed` (one stand-in, one pin), then `flush, before the module is
   stored: destroyed phantom 0x..`, then `save flush: re-issued the save ... after ~4 frame(s) -
   2 body(ies) destroyed, 0 left standing`, then `save flush: released`. The joiner's stand-in
   should reappear beside the host within a second (the phantom drive's 30-frame cooldown).
3. `python tools/saveinspect.py "<game>/saves/000000 - QUICKSAVE"` → **25 creatures (ghosts 0),
   58 waypoints (ghost pins 0)** on `END_M01AA`, the §2 baseline; the polluted save read 26/59.
4. Then load that save on the host: the ghost sweep's lines must NOT appear (nothing to
   sweep), and the joiner adopting it must come up clean too.
5. The save panel (Esc → Save → a new slot) with the joiner present: the same three log lines,
   then `saveinspect.py` on the new directory. Watch for the game being PAUSED under the panel:
   if the destroys sit unprocessed the log shows 120 held frames and `; the flush budget ran
   out`, which is the bounded failure mode, not a wedge — note it here and the sweep covers it.
6. Negative: joiner `disconnect`, host `save` → no `save flush:` lines at all (nothing standing
   → pass-through), 25/58.

Replicated ground items are flushed here like everything else (they are engine objects the
store would serialise) but are NOT swept at load: on the host they are the real world's items,
and on a client the saved world is never played except through adoption, which replaces it.

## 5. Live verification (2026-09-18, Endar Spire deck)

- First build of the sweep crashed the host at world-up (`FIRST-CHANCE access violation`, rva
  0xB69AA = `Odyssey.CopyAsciiBounded` via `ReadObjectTag`): `CollectByVtable` had returned four
  candidates that were freed blocks still holding the creature vtable, and reading their tag
  string followed a garbage pointer. Fixed by verifying every candidate through the engine's own
  object table (`ResolveServerObject(id at +0x04) == pointer`) before touching it — the rule now
  applies to both sweeps.
- Host loading `000000 - QUICKSAVE` (the polluted save, 26 / 59):
  `ghost sweep: destroyed baked-in map pin 0x12F (tag '')` → `this save carried 1 co-op stand-in
  creature(s) and 1 stray map pin(s) ... (32 creature candidate(s) scanned, 4 stale)` →
  `destroyed baked-in stand-in 0x12E` → `done - 1 destroyed, 0 written off`. The joiner adopting
  the same save logged the identical sequence on its side (30 candidates, 4 stale).
- Joiner `disconnect` (host: `peer left: destroyed phantom 0x138`), host `save`: the rewritten
  quicksave read **25 creatures, 58 waypoints, 0 ghosts, 0 ghost pins** — the baseline numbers.
