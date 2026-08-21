# Map reveal (fog-of-war sync) — static RE, 2026-08-21

Companion doc to [map-presence.md](map-presence.md) (the peer-pin waypoint), which already
named `CSWSAreaMap::SetWorldPointExplored` (0x005792D0) as the lever and sketched its call.
This pass pins the signature, the calling convention and two real callers via the symbol
database (`tools/kotor1_0_3.db`), `tools/disasm.py` and `tools/xrefs.py` — no live game was
run for this pass; see "What a live pass must check" at the bottom.

## VERIFIED

**`CSWSAreaMap::SetWorldPointExplored(float x, float y, float z, int explored)` — thiscall,
`ecx` = `CSWSAreaMap*`, address `0x005792D0`.**

- The symbol database (`functions` table) has this exact row:
  `class_name='CSWSAreaMap', function_name='SetWorldPointExplored', address=0x5792D0,
  calling_convention='__thiscall', param_size_bytes=16`.
- `tools/disasm.py 0x005792D0 160` shows the function ends `pop edi / pop esi / pop ebp /
  pop ebx / add esp,8 / ret 0x10` — pops 0x10 (16) bytes past the return address, i.e. FOUR
  stack dwords beyond the implicit `this`. 16 bytes matches `param_size_bytes` exactly and
  matches a `Vector` passed **by value** (three floats = 12 bytes) plus one `int` flag (4
  bytes) — not a pointer to a Vector, not a grid cell.
- The read-side counterpart, `CSWSAreaMap::IsWorldPointExplored` (`0x00579210`,
  `param_size_bytes=12`, confirmed `ret 0xc`), takes the same three-float Vector with no
  flag, and both functions open by calling `CSWSAreaMap::GetMapPixelFromWorldCoord`
  (`0x00578E00`, `param_size_bytes=20` — Vector by value + two `int*` out-params) to convert
  the raw world coordinate into a fog grid cell **internally**. So the argument this
  function wants is a **raw world-space position** (exactly what `RemoteView.Position`
  already carries) — the routine does the world→grid conversion itself; callers never touch
  a grid cell.

**Two real call sites, both matching the inferred semantics exactly** (`tools/xrefs.py
0x005792D0`):

1. **`CServerExoAppInternal::UpdateMapData`** (symbol db: `address=0x4B4E80`; the call itself
   is at `0x004B4EF3`). Disassembly shows a loop over the party (`CSWParty::GetCharacter`,
   `0x006346C0`) that, per member: resolves `CServerExoAppInternal::GetModule` → `+0x218` →
   `CSWSAreaMap*` (identical to `Odyssey.GetAreaMap()`'s own `module+0x218` chain), builds a
   3-float Vector from the member's cached position, calls `SetWorldPointExplored(areaMap,
   pos, 1)`, and on the **very next instruction** calls `CSWSAreaMap::SetPartyMemberWorldLocation`
   (`0x005790C0`) with the **same Vector** — the identical function `Odyssey.cs`'s
   `DrawMinimapPeers` already calls for the minimap pixel. This is the engine's own
   per-party-member fog reveal, run every server update tick. It is the routine
   `map-presence.md` inferred existed; this pass found it and confirmed the argument order.
2. **`CSWVirtualMachineCommands::ExecuteCommandRevealMap`** (symbol db: `address=0x547260`;
   the call is at `0x005472CD`) — the implementation of the `RevealMap()` nwscript command.
   It resolves the module's area map the same way, then calls `SetWorldPointExplored` with a
   position built from the script's own stack arguments (falling back to a different routine,
   `0x00578FA0`, when the script's x-coordinate is the `-1` sentinel — almost certainly
   "reveal the whole area" rather than one point, out of scope here).

Two independent, unrelated callers agreeing on the exact same signature, argument order and
constant flag value (`1`) is about as strong as static RE gets without running the binary.

**The `CSWSAreaMap*` resolution this reveal call needs is already live and shipping.**
`Odyssey.GetAreaMap()` (`module+0x218`, gated on `+0x04 != 0`) is the same pointer chain
`MinimapPeerHook` uses every frame it draws (`coop.ini minimap_peers`, default **on**) and
`MapMarkerController` uses to place peer pins. Only the `SetWorldPointExplored` call itself
is new; the pointer it is called through has already been exercised live by those two
features.

## INFERRED (not independently confirmed this pass)

- The `1` flag is almost certainly a boolean "explored" (vs. `0` to re-fog a point) — both
  real call sites always pass `1`, so the `0` path is untested by any caller found.
- `UpdateMapData`'s cadence (how often per second the engine itself calls this for the
  local party) was not measured; the design below does not need it, since the throttle is
  independent of the engine's own cadence.

## Design: reuse the position channel that already exists

`RemoteView.Position` (in the shared block `MapMarkerController`/`MinimapPeerHook` already
read) is a peer's raw world-space position, filtered to **the same module** before it ever
reaches this controller (`CoopAgent.PumpPhantomDamage`'s block comment: "the list written
into the block is filtered by module... before" a slot is ever populated) — so no new wire
and no extra same-module check is needed; `remote.Active` already implies same module.

Per tick, throttled: for each active remote slot, call `SetWorldPointExplored(areaMap,
remote.Position, 1)` when enough time has passed AND the peer has moved far enough since the
last reveal at that slot (`MapRevealRule`, unit-tested in isolation). No engine object is
created or destroyed, so there is no pre-store flush to add — unlike `MapMarkerController`,
this call leaves nothing behind for `StoreCurrentModule` to serialise.

## What a live pass must check

1. **Does it actually clear fog around the peer's marker on the M-screen**, immediately or on
   next open (per `map-presence.md`, note membership/position is live-read, but "explored"
   state might be sampled differently — confirm visually).
2. **No crash/hang calling it from the main frame hook** at the reveal cadence (this pass
   never ran the game; `GetAreaMap()`'s gate gives a logged no-op instead of a null-deref if
   the assumption is wrong, but the call itself has never executed).
3. **The `0` (un-reveal) path is unused by this design** — confirm nothing needs it before
   ever wiring it in.
4. **Area edges / instance transitions**: reveal calls right after a module change land on
   the correct new area map (not a stale one from `GetAreaMap()` returning 0 during the
   transition window, which the guard already declines cleanly).
5. Flip `coop.ini map_reveal_sync=1` (default OFF — pinned VERIFIED by static RE, but this is
   the first engine call of its kind and has not been exercised against the running game, so
   it ships off by the same "OFF until live-verified" convention `effect_sync` and
   `dialog_state_watch` already use) and confirm the M-screen shows the partner's explored
   trail without any visible cost to frame time.
