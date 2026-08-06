# Open bug — closing a conversation can crash the dialogue camera (2026-08-06)

**Status: OPEN, reproducible-ish, found by `tools\verify.ps1`.** Not a test-only defect:
the same call runs in production whenever a spectator's owner ends a conversation.

```
[13:19:23.729] test: closed any running conversation
[13:19:25.675] CRASH: access violation at 0x6307FB
stack: 0x63AD55 0x6413A2 0x634331 0x6345B8
```

- `0x6307FB` = `CGuiInGame::UpdateShotCamera`
- stack: `CSWCameraOnAStick::Destructor_2` -> `CSWCModule::SetDialogCamera` ->
  `CGuiInGame::ShowDialogReplies` -> `CGuiInGame::HandleDialogReplies`

So: a conversation was closed from the SwapBuffers frame hook while the GUI was still
handling dialogue replies; the reply handler's camera was destroyed, and the next
`UpdateShotCamera` dereferenced it. ~2 s elapsed between the close and the fault, which fits
"the camera is torn down, then the next frame updates it".

`Odyssey.CloseDialogue` is already guarded on `GuiConversationActive != 0`, so this is not an
unguarded close — the problem is WHEN it runs, not WHETHER a conversation exists.

**Why it matters beyond the harness:** `DialogueSpectateController` calls the same
`CloseDialogue` from the same frame hook on three paths — the owner ending their
conversation, the follow timing out, and the race back-off ("a peer won the token"). Those
have run many times without crashing, so the window is narrow, but it is the same window.

**Not fixed, and deliberately not guessed at.** The likely correct shape is to close the way
the player's own Escape does — through the GUI's own cancel path at a safe point in its
loop — rather than calling `CloseDialogFn` directly from the render hook. That needs an RE
pass on the Escape handler to find the safe entry point. A cheap interim mitigation would be
to defer the close by a frame and skip it while `ShowDialogReplies` is active, but neither
has been measured, and this project's history is clear about what shipping an unmeasured
guess costs.

---

# Playtest bug list — 2026-08-01

Reported by the user from the first real two-player session (the click-loaded setup). This
is the highest-value input the project has had: every item here is something no amount of
memory-state checking would have found, because the simulation and the network were fine in
all of them.

Status key: **OPEN** · **FIXED (unverified)** — fix written, not yet confirmed in play ·
**FIXED (verified)**.

---

## 1. The world is never shown after a plugin-driven load — FIXED (verified by screenshot)

Root cause (`docs/research/ingame-gui-transition.md`): the engine performs the whole
transition itself, every frame, inside the client's MainLoop — but only once its own gates
hold, and the replay reached none of them. The single biggest one: retail's `OnLoadSaveGame`
marks the MAIN MENU for the GUI manager's delete sweep, and the replay never did, so the
menu kept drawing over a correct running world. The un-re-enabled input was part of the same
missing block, not a separate bug.

Fixed: phase 1 marks the menu for teardown; phase 3 waits for the CLIENT's own module and
creature, then re-arms the display-pending flag (which alone makes the engine do the rest),
builds and shows the HUD, re-enables input, drops the load screen once, and releases the
unpauseable hold. Verified by screenshot — full in-game GUI, minimap, portraits — and the
plugin logs the GUI status so a regression shows without a capture.

## 2. Peer position lags by seconds — IMPROVED, needs your eyes

The transport was never the problem (the wire carries position every tick). The cause is
that the phantom is WALKED to a goal by the engine's pathfinder, which cannot close a gap
against a peer moving at the same speed — so it trails for as long as the peer keeps
running.

Tried and REVERTED: driving the position directly each step. It does not work — the position
setter only commits with the action queue cleared, and clearing it per step never lets the
engine finish applying the move, so the body sat still through bursts of writes and jumped
only when the timing happened to align. Restricting the write to once per received position
was no better. Recorded here so nobody spends the afternoon on it again; making it work
would need RE of what actually commits a creature move.

What shipped instead is tighter tuning plus a hard bound on the error:
goal resend 0.6 → 0.25 units, re-path cooldown 5 → 2 steps, **snap distance 15 → 3.5 units**
(the real lever: beyond that the body is placed rather than left to jog), tick 20 → 30 Hz,
interpolation 100 → 80 ms. ⚠ Not verifiable by me — a synthetic teleport is a discontinuity
and a poor proxy for continuous walking, which is what you actually see. Needs a play check.

## 3. Initial position is not synced — FIXED (verified)

The phantom was created at a fixed offset from the LOCAL player and then walked to the
peer's real position. It now spawns at the peer's own position — by definition a valid
walkmesh point, since they are standing on it — with the old spot kept as a fallback if the
engine refuses a body there. Confirmed live: `spawned phantom 1: id 0x12D at (20.01, 22.15)`,
the peer's exact position, where it used to read `(22.51, 23.65)`.

## 4. Animations do not replicate — OPEN, and the current approach cannot work

`docs/research/animation-and-minimap.md`. The apply side is fine; the READ side polls a field
the engine never writes for these cases:
- **X is "Flourish"** (keymap `action242`), and it is purely client-side. Standing still it
  queues a fire-and-forget that does reach the twin, but **moving or turning it writes no
  object field at all** — it goes straight to the model's animbase, so no poll can ever see
  it.
- **A stim** is animation `0x2756` (`inject`), carried by the combat round and gated on the
  item's spell row; the visible part is largely a VISUAL EFFECT, a channel this project does
  not touch at all.

Two further defects found in passing: `ReadPlayedAnimation` reads a `uint` from a 16-bit
field, and `MirrorAnimation` queues any non-gait id as fire-and-forget, which parks a
looping/overlay id with no expiry and can wedge the phantom.

The fix is not a tuning change: stop polling and hook `CSWCObject::InsertFireForgetAnimation
0x0063C760` — the single choke point for draws, flourishes, swings and item use — plus
`PlayerFlourishWeapons 0x005F4460` for the overlay case, then send one-shot and overlay
events separately from gait and apply each through its matching engine call. Addresses and
ABIs are in the doc. This is a substantial piece of work, not a patch.

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

## 8. The minimap does not show both players — WON'T FIX as asked; the mechanism cannot do it

`docs/research/animation-and-minimap.md`. **The minimap cannot show a peer and never could.**
`CSWGuiMainInterface::DrawMap` is 0x1C0 bytes and draws exactly the area-map image plus ONE
arrow — there is no map-note loop and no party loop in it. The waypoint the plugin creates
only ever addressed the full **M-screen** map, so the feature was mis-scoped from the start,
not broken. (The missing note text is a red herring: the M-screen pin builds a fixed icon and
never dereferences the string, and note membership is re-snapshotted every time the map panel
opens, so a later-created waypoint does join.)

The engine's own party dots are 3 fixed slots that `UpdateMapData` overwrites every frame and
both draw loops bound by the real party size, so a peer cannot borrow one without genuine
party membership.

Options, none of them free: detour `DrawMap` to blit an extra marker (~80-120 lines inside
the viewport frame, moderate crash risk, one value needs a live read), or accept M-screen
only. The cheap alternative that gives the same "where is my partner" answer is a name plate
over the phantom. **Recommend: verify the M-screen pin works, and treat the minimap as out of
scope unless you want the detour.**

## 9. Nobody can switch party character — FIXED (unverified)

A regression I introduced with death handling. The control-swap suppression is a CLIENT-side
lock (so a client cannot take over a companion the host's AI owns), but I applied it to every
peer including the host, and it blocks Tab and the portraits outright. Now gated on
`!session.IsEnemyAuthority`, so the host — which owns the companions — stays free to switch.
