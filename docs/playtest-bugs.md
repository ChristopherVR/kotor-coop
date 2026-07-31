# Playtest bug list — 2026-08-01

Reported by the user from the first real two-player session (the click-loaded setup). This
is the highest-value input the project has had: every item here is something no amount of
memory-state checking would have found, because the simulation and the network were fine in
all of them.

Status key: **OPEN** · **FIXED (unverified)** — fix written, not yet confirmed in play ·
**FIXED (verified)**.

---

## 1. The world is never shown after a plugin-driven load — OPEN

Both instances keep displaying the MAIN MENU (host lit, client faded near-black
mid-transition, which reads as "loading forever") while the simulation runs correctly
underneath. Full detail and the addresses to look at are in Handoff §0h. The click loader
renders correctly, so it is the route to use meanwhile.

**Also**: `FirePhase2` calls `SetInputClass(client, 0, 1)` + `DisableInput(client)` and
nothing ever re-enables them.

## 2. Peer position lags by seconds — OPEN

The other player takes a few seconds to catch up rather than moving in real time. Position
is supposed to be on the every-tick path, so the suspects are the phantom DRIVE rather than
the transport: `PhantomController` re-issues a walk goal only past a 0.6-unit threshold and
then sits out a 5-step re-path cooldown, which quantises movement into laggy hops. Measure
the wire rate first (the dashboard's tx/rx climb steadily), then the goal-resend logic.

## 3. Initial position is not synced — OPEN

A phantom spawns at a fixed offset (observed: `(22.51, 23.65)` regardless of where the peer
actually is) and then WALKS to the real position, instead of being created at it. See
`Odyssey.SpawnPhantom`'s call site in `PhantomController` — the spawn position should be the
peer's current position, with the walk only for subsequent movement.

## 4. Animations do not replicate — OPEN

Pressing X (and equivalents) plays nothing on the peer. Item use — a stim, for example —
also shows nothing. `PlayerState.Animation` is transmitted and `PhantomController` writes
the raw animation slot, so this is either the wrong field, the wrong value space (the peer's
*played* animation vs the *requested* one), or a value the twin needs re-asserting to see.
`docs/research/animation-sync.md` is the starting point.

## 5. Enter opens the chat overlay on BOTH instances — FIXED (unverified)

Cause: every key read went through `GetAsyncKeyState`, which reports the PHYSICAL keyboard
and is completely independent of focus, so both instances on one machine saw every keypress.
Fixed by gating all of them on `NativeMethods.IsKeyDownHere`, which first checks that the
foreground window belongs to this process. Affects Enter (chat), PageUp/PageDown (overlay
scroll), F9 (browser) and the browser's arrow/Enter/Escape handling.

## 6. Pause does not pause both instances — OPEN
## 7. Pause gets stuck: "paused" shows top-right and can never be released — OPEN

Almost certainly one bug. The design is that each peer publishes its OWN pause and the
plugin holds the engine's pause while any peer's flag is up, with the pauser being the only
one who can release it — so a latch that never clears leaves the game permanently paused and
locally unpausable, which is exactly the reported symptom. Look at `_ownPauseLatched` in
`KotorGameBridge.ReadLocalFlags` together with `PauseController`'s re-assertion: if the
mirrored pause is re-asserted while the origin flag is stuck set, nothing can ever clear it.
⚠ Game-breaking — highest priority after the presentation bug.

## 8. The minimap does not show both players — OPEN

`MapMarkerController` reports `map marker: slot 0 pinned as 0x12F` in the log, so a waypoint
IS being created and moved — but nothing appears on the minimap. Note the marker never sets
the waypoint's map-note TEXT (`CExoLocString` at waypoint `+0x230`); only `has_map_note
+0x228` and `map_note_enabled +0x22C` are written. Also worth checking whether the minimap
(as opposed to the full M-screen map) draws map notes at all, and whether note membership is
snapshotted at area load — in which case a marker created later never joins the set.

## 9. Nobody can switch party character — FIXED (unverified)

A regression I introduced with death handling. The control-swap suppression is a CLIENT-side
lock (so a client cannot take over a companion the host's AI owns), but I applied it to every
peer including the host, and it blocks Tab and the portraits outright. Now gated on
`!session.IsEnemyAuthority`, so the host — which owns the companions — stays free to switch.
