# Map reveal (fog-of-war sync) — static RE, 2026-08-21

Companion doc to [map-presence.md](map-presence.md) (the peer-pin waypoint), which already
named `CSWSAreaMap::SetWorldPointExplored` (0x005792D0) as the lever and sketched its call.
This pass pins the signature, the calling convention and two real callers via the symbol
database (`tools/kotor1_0_3.db`), `tools/disasm.py` and `tools/xrefs.py` — no live game was
run for this pass; see "What a live pass must check" at the bottom.

## VERIFIED

**`CSWSAreaMap::SetWorldPointExplored(float x, float y, float z, int radius)` — thiscall,
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

- The earlier boolean interpretation was incorrect. Reinspection on 2026-09-30 established
  that the fourth argument is a radius: `0` reveals one grid point and `1` reveals a diamond
  neighbourhood. Neither value re-fogs an explored point. The co-op call matches the
  stock party update's radius of `1`.
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
3. **Radius `0` is unused by this design** — co-op uses the stock party radius of `1`.
4. **Area edges / instance transitions**: reveal calls right after a module change land on
   the correct new area map (not a stale one from `GetAreaMap()` returning 0 during the
   transition window, which the guard already declines cleanly).
5. Flip `coop.ini map_reveal_sync=1` (default OFF — pinned VERIFIED by static RE, but this is
   the first engine call of its kind and has not been exercised against the running game, so
   it ships off by the same "OFF until live-verified" convention `effect_sync` and
   `dialog_state_watch` already use) and confirm the M-screen shows the partner's explored
   trail without any visible cost to frame time.

## Live pass (2026-08-27): default flipped ON

Ran with `map_reveal_sync=1` on both instances of the two-instance rig (block version 53),
connected in `END_M01AA`.

**Fires correctly on connect and follows real movement.** Both plugin logs show a reveal the
instant each side's remote-peer slot went active:

```
[18:49:13.894] map reveal: slot 0 revealed at (20.0, 22.2, -1.3)     <- host, at connect
[18:49:13.963] map reveal: slot 0 revealed at (20.0, 22.2, -1.3)     <- client, at connect
```

`tp 47.4 116 -0.04` on the client (a real position change, ~95 m away) produced a second reveal
on the HOST tracking the new position within the next heavy-pass tick:

```
[18:53:13.031] map reveal: slot 0 revealed at (47.4, 116.0, 0.0)
```

**Survives a module transition cleanly.** `warp end_m01ab` on the host moved both instances
(`travel: moving to module 'end_m01ab' on the session's authority` → `epoch: world unloading` →
both blocks report `module 'end_m01ab'`, both processes still `Responding=True`). The reveal
resumed in the new module within ~4.5 s of the transition landing, with no `map reveal: slot 0
skipped — no area map yet` line at any point — `TryGetAreaMap` re-resolved the new module's
area map cleanly, closing check 4 of the live-pass list above:

```
[18:53:42.134] map reveal: slot 0 revealed at (50.0, 120.0, -0.0)
[18:53:49.006] map reveal: slot 0 revealed at (50.0, 120.0, -0.0)   <- same point, re-asserted; harmless
```

**No measurable frame cost.** `frame stats` around the transition show the expected transition
stall (`182.0 ms worst`, `189.9 ms worst` — the module load itself, matching the 5.1-5.3 s
bare-load baseline other rounds have measured) and otherwise 1.7-2.0 ms average with 0-7 ms
worst — no different from every other round's baseline with this controller OFF.

**Not confirmed this pass: the M-screen itself.** `tools/shot.ps1` is still the documented
solid-black capture (re-confirmed the same session, `disconnect-reconnect.md`/`connect-flow.md`
live passes) — visually seeing the fog actually clear around the peer's pin was not possible.
Everything upstream of the render (the engine call firing, with real coordinates, repeatably,
surviving a transition, at negligible cost) is now proven; only the pixels themselves are not.

**Verdict: default flipped ON.** `PluginConfig.ReadBool("map_reveal_sync", true)` in
`MapRevealController.cs` and `profiles/coop.ini.example`'s `map_reveal_sync=1`, matching
`docs/CONFIG.md`. Radius `0` remains unused by this design.

## Visible shared fog check (2026-09-30)

Native88/Agent81 host PID48440 and joiner PID53456 used the stock Endar Spire fixture.
The joiner stayed at `(15.4, 20.1, -1.3)` while the host moved to `(35, 22.2, -1.3)`.
The joiner's M-screen changed from the small starting room to a revealed vertical corridor
ending at the host's cyan pin. Both before and after captures were visually inspected:
`artifacts/stock-live-v88/map-client-before.png` and `map-client-after.png`.

The earlier movement from x15.4 to x25 stayed inside the same exploration cell on this
15-by-8 native map. That fixture could not demonstrate new fog and was not evidence of a
rendering defect. Moving to x35 crossed a cell boundary and completed the visible check.
