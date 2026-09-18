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

## 4. Still open (save-time half)

Opening the flush window BEFORE a manual save so nothing is baked in the first place. Needs a
detour on `DoQuickSave` (already located; the plugin's `save` verb calls it) that arms the
flush and re-issues the save once the area is clean, plus the save-panel path (its store call is
not yet located). Until then a manual save still bakes spawns and the sweep makes that
harmless. Replicated ground items are NOT swept: on the host they are the real world's items,
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
