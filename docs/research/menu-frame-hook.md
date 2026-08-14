# A per-frame hook that fires at the MAIN MENU (2026-07-31)

Why the plugin's `SwapBuffers`-via-IAT frame hook (`0x0073D00C`) is dead at the main menu, and
the per-frame hook point that replaces it — runs every frame at the menu **and** in-world.
Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, base `0x400000`, no ASLR) via
the sha256-matched symbol DB and a direct parse of the on-disk import table. Nothing was modified.

Read alongside `menu-button-runtime.md` (the dormant BTN_WARP we un-hide from this callback) and
`console-overlay.md` (which already named `UpdateScreen 0x00401C10` the present path).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not yet run ·
**NEEDS-LIVE** = confirm on the running game before shipping.

## Headline

`0x0073D00C` is **`GDI32!SwapBuffers`** — the *only* back-buffer present in the whole binary
(import-table parse; no `wglSwapBuffers`, no second thunk). It has **exactly two call sites**,
both reading that same IAT slot, so one IAT detour covers both:

| Site | In | Present used for |
| --- | --- | --- |
| `0x004048C1` | `WinMain 0x004041F0` | the top-level loop's own swap |
| `0x00401CC2` | `UpdateScreen 0x00401C10` | the per-frame present (`MainLoop` calls this) |

**Both swap sites are gated behind `!CClientExoApp::IsMoviePlaying()`.** At the main menu a
looping movie is playing, so `IsMoviePlaying()` is true, *both* swaps are skipped, and the movie
presents through the Bink path instead of GDI `SwapBuffers` — so the IAT hook never fires. In a
loaded module no movie plays, the gate opens, `UpdateScreen` swaps (~168 fps), and the hook fires.
That is the whole mystery. **VERIFIED** (gate + call sites); **INFERRED/NEEDS-LIVE** (that the menu
keeps a movie playing — see below).

**Recommended replacement hook: `CClientExoApp::MainLoop 0x005ED7D0`.** It is called once per
rendered frame from `WinMain`, *above* the movie gate, in every app state. One hook, both states.

## Why the SwapBuffers hook is menu-dormant (VERIFIED, except the movie claim)

`0x0073D00C` resolves to `GDI32.dll!SwapBuffers` (import descriptor parse). It is the sole present:
the exe imports the full `OPENGL32` set but **no `wglSwapBuffers`**, and `xrefs 0x0073D00C` returns
only the two literal sites above. So whatever presents the menu must be one of those two — or not
`SwapBuffers` at all.

Both sites share one predicate: **`CClientExoApp::IsMoviePlaying 0x005EDB40`** (`__thiscall`, called
with `ecx = [APP_MANAGER_PTR 0x7A39FC + 4]` = the `CClientExoApp`). It tail-chains
`0x005F2BD0 → 0x0070C1F0 → 0x00404DD0`, which is just:

```
movie = *(CClientExoAppInternal + 0x12C)   ; = movie_player  (CExoMoviePlayer*, offset decoded in DB)
if (movie == 0) return 0                    ; no movie -> gate open
return *(*movie + 0x30)                      ; else -> the movie's "playing" flag
```

- **`UpdateScreen 0x00401C10`** swap `0x00401CC2` is reached only if `IsMoviePlaying` returned 0
  (checks at `0x00401C26` / `0x00401C75`; `jne` skips the swap) **and** its 3rd arg is 0.
- **`WinMain`** swap `0x004048C1` is reached only if `IsMoviePlaying`==0 (`0x0040488E`) **and** the
  window-active flag `0x007A3A2C`==0 (`0x004048A5`; that global is set to `2` on deactivate/minimize
  near `0x00402868`, unrelated to menu-vs-world).

So `SwapBuffers` runs **iff no movie is playing** (and the window is active). At the menu the movie
gate is closed, both swaps are skipped, and frames are pushed by `binkw32!_BinkBufferBlit@12`
(`0x0073D460`) — never through `0x0073D00C`. Hence frame counter == 0 at the menu, ~168 fps in a
module. **VERIFIED**: the gate is literally `IsMoviePlaying`, the call sites, the skips.

**The movie claim — INFERRED / NEEDS-LIVE.** That the *settled start menu* keeps a movie playing is
inferred from: the gate being `IsMoviePlaying`; the observed dormancy; and engine functions
`CClientExoApp::SetStartMenuMusicOnMovieEnd 0x005ED9E0`, `PlayLoadModuleMovies 0x005EDB20`,
`RestartMoviesIfNecessary 0x005ED980`. Confirm live by reading the playing flag at the menu
(`[[[[[0x7A39FC]+4]+4]+0x12C]]+0x30`) or logging `IsMoviePlaying()`'s return. **This "why" does not
affect the recommended hook** — `MainLoop` fires whatever the reason for the skip.

**The probe now ships.** `CinematicWatch` (on this very hook) calls `IsMoviePlaying` every frame
and logs `cinematic: a movie is playing with no world up (the main menu's own loop)` the first
time it reads true with no server. One run of `tools/demo.ps1` settles this claim; it is step 2
of the test plan in `docs/research/cinematics.md`, where it doubles as the control that proves
the watch is reading the engine at all. The same document explains why the *in-world* case is
the opposite: there a movie blocks `MainLoop` itself, so neither hook sees it.

## The frame loop (VERIFIED)

`WinMain 0x004041F0` is a `PeekMessageA` pump:

```
loop 0x00404570:
  PeekMessageA           [0x73D3F4]   ; test eax
    je  0x004045D0        ; NO message  -> render a frame  (the common case at a settled menu)
    GetMessageA/Translate/Dispatch  [0x73D3B4/0x73D3A0/0x73D3B8] ; then jmp back
render 0x004045D0:
  ...
  0x004046B8  mov ecx,[APP_MANAGER_PTR+4]      ; ecx = CClientExoApp
  0x004046BD  call 0x005ED7D0                  ; <-- CClientExoApp::MainLoop  (per-frame tick)
  0x004046C2  or  [quit_flag 0x7A39B8], eax    ;     return folded into the quit flag
  ...                                          ; glClear + swap block, all behind IsMoviePlaying
  0x004048C1  call [0x73D00C]                  ; SwapBuffers (gated)
loop_cond 0x00404A0B:
  cmp [0x7A39B8],0 ; je 0x00404570             ; not quitting -> next frame
```

The `MainLoop` call at `0x004046BD` sits on the render branch **before** and **outside** the movie
gate, so it runs every frame regardless of `IsMoviePlaying`. (Under a message flood the render
branch is skipped for that iteration — identical cadence to the engine's own rendering, i.e. one
callback per rendered frame, the same guarantee `SwapBuffers` gave in-world.)

## Recommended hook — `CClientExoApp::MainLoop 0x005ED7D0` (VERIFIED reachability)

| Property | Value |
| --- | --- |
| Address | `0x005ED7D0` |
| Signature | `__thiscall void MainLoop(CClientExoApp* this)` — DB `param_size_bytes = 0`; returns the quit flag in `eax` |
| Body | thunk: `8B 49 04` `mov ecx,[ecx+4]` then `E9 ..` `jmp 0x00602EB0` (`CClientExoAppInternal::MainLoop`) |
| Callers | **exactly one**: `WinMain 0x004046BD` (`xrefs` = 1 call, 0 data refs — **not** a vtable slot) |
| Reaches the menu present | `0x00602EB0` calls `UpdateScreen 0x00401C10` at `0x006032E5` |
| Fires at menu? | **Yes** — render branch, above the movie gate |
| Fires in-world? | **Yes** — same loop, same call; drives `UpdateScreen`→`SwapBuffers` in a module |

So **one `MainLoop` hook replaces both the menu need and the in-world `SwapBuffers` hook.** The
plugin can drop the `0x0073D00C` IAT detour, or keep it as an in-world-only fallback.

### How to detour it

**Option A — call-site rewrite at `0x004046BD` (PREFERRED; lowest risk).** The site is unique. Its
5 bytes are `E8 0E 91 1E 00` (`call rel32` → `0x005ED7D0`). Repoint the `rel32` at a small **naked**
stub; `ecx` already holds the `CClientExoApp` (set at `0x004046B8`). The stub: preserve `ecx`, run
the managed per-frame callback, then `jmp 0x005ED7D0` (tail-call the original) so its `eax`
(quit flag) flows straight into `WinMain`'s `or [0x7A39B8],eax`. **One 4-byte displacement change,
no trampoline, no prologue relocation, no reentrancy** (single caller). This is strictly simpler
than the existing IAT detour and avoids the "new inline-detour infrastructure" concern raised in
`console-overlay.md`.

**Option B — inline trampoline on the internal body `0x00602EB0`.** Prologue
`55 8B EC 83 E4 F8 ...` (`push ebp; mov ebp,esp; and esp,-8; ...`) is position-independent and
cleanly relocatable for a 5-byte `E9` + trampoline (MinHook-style). Prefer this over hooking the
`0x005ED7D0` entry directly, whose `8B 49 04 / E9 rel32` thunk shape forces you to relocate a
`mov` plus recompute the tail `jmp`.

**Not viable:** vtable swap (`MainLoop` is called by absolute address and is in no vtable) or an IAT
detour (it is an internal function).

### BTN_WARP timing (VERIFIED against menu-button-runtime.md)

`menu-button-runtime.md` establishes the menu ctor clears BTN_WARP's visible bit `+0x44 & 0x2`
**once**, and nothing re-clears it. `MainLoop` runs every frame *after* menu construction, so setting
that bit from this callback un-hides BTN_WARP and it stays set; ordering versus the menu's own Draw
is immaterial because the bit persists. A per-frame set is belt-and-suspenders — one set after the
menu exists is enough.

## Reference addresses

`GDI32!SwapBuffers IAT 0x0073D00C` · swaps `0x004048C1` (WinMain) `0x00401CC2` (UpdateScreen) ·
`WinMain 0x004041F0` · render branch `0x004045D0` · **MainLoop call site `0x004046BD`** ·
`CClientExoApp::MainLoop 0x005ED7D0` → `CClientExoAppInternal::MainLoop 0x00602EB0` →
`UpdateScreen 0x00401C10` @ `0x006032E5` · gate `CClientExoApp::IsMoviePlaying 0x005EDB40`
(`→0x005F2BD0→0x0070C1F0→0x00404DD0`, reads `movie_player @ CClientExoAppInternal+0x12C`) ·
window-active flag `0x007A3A2C` · quit flag `0x007A39B8` · `APP_MANAGER_PTR 0x007A39FC` ·
`PeekMessageA IAT 0x0073D3F4` · Bink present `binkw32!_BinkBufferBlit@12 0x0073D460`.
