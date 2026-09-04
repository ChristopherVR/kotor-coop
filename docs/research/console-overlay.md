# In-game `~` console overlay (2026-07-31)

Verified against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB. Nothing in the install was modified during
research.

## Headline

KOTOR ships with the **entire Aurora developer console still linked in and functional** —
grave/tilde toggle, a text-input buffer with full editing, on-screen rendering, and a
keyboard grab. The plugin does **not** build a renderer or an input system: it *enables* the
native console and *reads* what the player typed. There is also a genuine self-contained
`DrawString` for painting our own reply/log lines on top.

## What we use

| Purpose | Address | Notes |
| --- | --- | --- |
| Console-open flag | `0x007A3A40` | `int`, nonzero while open. Read only. |
| Command-line buffer | `0x007A3A4C` | null-terminated ASCII, cap `0x1F4` (500). The engine edits it as you type. |
| Caret index | `0x007A3A44` | cleared with the buffer. |
| Enable gate | `EnableCheats` in `swkotor.ini [Game Options]` | opens the console the normal way, which also engages the keyboard grab. |
| Text draw | `Global::PrintStringScreenPos_UseTextureFont 0x0044CCA0` | `void __cdecl(const char*, float, float)`. Self-contained: saves/restores GL state, sets its own ortho, draws through the global texture font. No GUI tree. x/y are a **centered, 0.01-scaled** space (≈ screen-percent from the middle). |

Movement suppression is free: when the native console is open the engine has already called
`CExoInput::KeyboardModeOn 0x005DF520`, so typed keys don't drive the character. We only need
the console to be *open*, which `EnableCheats=1` provides.

## Why we do NOT register a console command

The engine-sanctioned extension point is `Global::ConsoleFuncs_Add 0x0044BA80` (table at
`0x007A8AB8`), and the lowest-latency capture is an inline hook on the Enter/execute path
`0x004021C5`. Both were rejected for this build:

- The `CmdEntry` struct layout and the **handler calling convention are unverified**. A wrong
  guess means the engine calls into our stub — or jumps through a malformed table entry —
  with a mismatched stack, which **crashes the game**. Unacceptable for a path we cannot
  runtime-verify here.
- An inline detour is new, riskier infrastructure under NativeAOT.

Instead the plugin calls only *into* the engine with the one known, self-contained, cdecl
(caller-cleaned) function `PrintStringScreenPos`, and otherwise just reads two globals and the
Win32 key state. The engine never calls back into us with an unknown ABI.

## Capture design (race-free, crash-free)

The engine clears the command buffer on Enter, and our frame hook runs at `SwapBuffers`
(after the message pump), so reading the buffer *on* the Enter frame usually reads empty. We
sidestep the timing entirely:

1. Each frame the console is open, **remember** the current non-empty buffer contents.
2. On the **rising edge** of Enter (`GetAsyncKeyState(VK_RETURN)`), route the *remembered*
   line — the command exactly as it stood the frame before submit — into the shared-block
   console channel, and clear the engine buffer so it isn't also dispatched as an unknown
   cheat.

Whichever runs first — the engine's Enter handling or our hook — the full command is
recovered. Only co-op verbs are intercepted (`connect`, `join`, `host`, `disconnect`, `dc`,
`leave`, `reconnect`, `rc`, `status`, `help`, `coop`); none collide with a stock KOTOR cheat,
and any other line is left for the engine.

The agent (`CoopCommandDispatcher`) consumes the channel, drives the `SessionManager`, and
writes status/replies back into the block; the plugin paints the last few reply lines with
`PrintStringScreenPos` while the console is open — including the "password required" prompt.

## Needs one live calibration pass

- **Text coordinates** for `0x0044CCA0` are a centered, 0.01-scaled space; the overlay's x/y
  are reasonable guesses. A wrong value only *mis-places* text — it cannot crash — so this is
  safe to tune live.
- The **remember-previous-frame** capture is sound in theory but unconfirmed against the real
  frame/message ordering. Failure mode if wrong: a co-op command typed in the console is not
  routed (the engine reports an unknown cheat) — **harmless**, never a crash.
- Confirm the native console renders at the **main menu** as well as in-world (the present
  path `UpdateScreen 0x00401C10` is universal; its gate is expected-true at the menu).

## 2026-09-04 addendum: chat-overlay clipping, and a screenshot-free way to check placement

A player-facing complaint ("the console overlay clips sometimes, not clear how it works")
prompted a live session against this rig's own GOG install. Findings:

- **This rig's own default resolution is 800×600.** Neither game dir's `swkotor.ini` sets
  `Width`/`Height` under `[Graphics Options]`, confirmed live by reading the engine's own
  `Global::ScreenWidth`/`ScreenHeight` globals (`0x0078D1D4`/`0x0078D1D8`) out of a running
  process: both read exactly `800`/`600`. That is almost certainly smaller than whatever
  resolution `ChatOverlayController`'s anchors were originally eyeballed against, and the
  text-render-queued grid is viewport-derived (`cols≈w/N`, §5 above) — so a column chosen at a
  larger resolution has much less headroom at 800×600.
- **The right-side anchors' column (`60`) was never actually live-verified**, unlike column `3`
  (confirmed against the screen edge) and the rows tied to the minimap/portraits — the source
  comment already said so. It is the most likely single cause of a reported "clipping" overlay,
  compounded by there being **no width-aware truncation anywhere**: `ChatLineLength` allows a
  128-byte line, and nothing capped what actually got posted. `AurPostString` does not clip or
  wrap a line that runs past the true edge of the viewport-derived grid — it just draws
  off-screen, invisible glyphs, which is exactly what a player would describe as the overlay
  "clipping".
- **Fix shipped:** `ChatOverlayController.PrepareLine` now truncates the on-screen copy of a
  line to a conservative, resolution-independent character budget before posting (60 chars for
  the verified-safe left anchors, 32 for the right ones), appending `...` when it had to cut.
  This is a safety net, not a pixel-accurate fix — a shorter line is never *more* likely to run
  off an edge than a longer one at any resolution, so it is safe to ship without a further
  calibration pass. The stored line is never shortened, only what gets drawn — scrollback keeps
  the full message. The right anchors' column was also tightened from the unverified `60` down
  to `50`, purely as extra margin.
- **`tools\shot.ps1` could not be used to confirm any of this on this machine**: it returned a
  solid black frame for the game window on every attempt, even though (a) the desktop session is
  a real, active local console session (not RDP, not locked — confirmed via `qwinsta`/
  `TerminalServerSession`) with a genuine 2560×1440 monitor, and (b) the plugin log and the
  engine's own frame counter (`blockpoke.ps1 state`'s `frames`) proved the game was genuinely
  in-world and rendering continuously (~700 fps uncapped) the whole time. This looks like a
  machine-specific GDI/compositor quirk unrelated to the plugin — the render path itself is
  proven live-correct below without needing a single pixel.
- **New technique: read the engine's own text queue directly instead of screenshotting it.**
  `Global::AurPostString`'s node list (`[0x007B90FC]`/`[0x007B9100]`, §1 above) is a plain
  in-process data structure — reading it from outside needs nothing more than the same
  `ReadProcessMemory` primitive `blockpoke.ps1`/`pluginstate.ps1` already use, no ASLR to defeat.
  `tools\textqueue.ps1` dumps every currently-queued node's column, row and text. Live 2026-09-04
  against this fix: posting a 91-character line at the default (bottom-left) anchor produced a
  queued node reading exactly `col=3 row=14` with the text cut to 60 characters ending `...`;
  switching `chat_overlay_anchor = bottom-right` and reposting produced `col=50` with the text
  cut to 32 characters ending `...` — both anchors, both budgets, confirmed structurally correct
  end to end (config → capture → truncate → post), independent of the screenshot problem above.
- **Still NEEDS-LIVE:** the actual pixel/column mapping (`cols≈w/N` in §5) remains uncalibrated —
  a future session with working screen capture on this rig (or any other machine) should confirm
  visually that column 50/60 and rows 11/14 land where intended at 800×600 and at a couple of
  larger resolutions, and tune the two `MaxChars` constants and the right anchor's column with
  real numbers instead of conservative guesses. `textqueue.ps1` is the fast way to correlate
  "what got posted" with a screenshot once one is available.

## Reference addresses (from the deep-dive)

Toggle/handlers: `HandleWMCharMessage 0x00402240` · `DoConsole 0x00401F00` · execute path
`0x004021C5` · enable-gate compare `0x005E5630` (`swkotor.ini`/`Game Options`/`EnableCheats`
strings at `0x0073D648`/`0x0073D658`/`0x0073D668`).
Input grab: `KeyboardModeOn/Off 0x005DF520 / 0x005DF530`.
Text: `PrintStringScreenPos_UseTextureFont 0x0044CCA0` · console line printer `0x0044D490` ·
flush `AurRenderStrings 0x0044D6A0` · coord scale `0x0073DFEC` (=0.01).
Present: `UpdateScreen 0x00401C10` (SwapBuffers import `[0x0073D00C]`).
Registration (unused, documented): `ConsoleFuncs_Add 0x0044BA80` / table `0x007A8AB8`.
