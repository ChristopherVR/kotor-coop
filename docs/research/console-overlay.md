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

## Reference addresses (from the deep-dive)

Toggle/handlers: `HandleWMCharMessage 0x00402240` · `DoConsole 0x00401F00` · execute path
`0x004021C5` · enable-gate compare `0x005E5630` (`swkotor.ini`/`Game Options`/`EnableCheats`
strings at `0x0073D648`/`0x0073D658`/`0x0073D668`).
Input grab: `KeyboardModeOn/Off 0x005DF520 / 0x005DF530`.
Text: `PrintStringScreenPos_UseTextureFont 0x0044CCA0` · console line printer `0x0044D490` ·
flush `AurRenderStrings 0x0044D6A0` · coord scale `0x0073DFEC` (=0.01).
Present: `UpdateScreen 0x00401C10` (SwapBuffers import `[0x0073D00C]`).
Registration (unused, documented): `ConsoleFuncs_Add 0x0044BA80` / table `0x007A8AB8`.
