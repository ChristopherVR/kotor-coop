# Queued screen text — rendering from the SwapBuffers hook (2026-07-31)

Reverse-engineered against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB and static disassembly + PE data reads. Nothing
in the install was modified.

Read `docs/research/console-overlay.md` and `docs/research/text-color-alpha.md` first. This
doc **supersedes** the "call `PrintStringScreenPos 0x0044CCA0` directly from the hook"
recipe in those docs and corrects two claims in `text-color-alpha.md §3–4` (see §4/§6).

## Headline (VERIFIED)

The immediate-draw call `PrintStringScreenPos_UseTextureFont 0x0044CCA0` crashes from the
`SwapBuffers` hook because the global texture font `*(0x0083052C)` is only bound during the
engine's own render pass, not at present time. The fix is to stop drawing and instead **enqueue**
strings the same way the engine's own console prompt, subtitles and feedback text do, and let
the engine's **own per-frame flush** draw them while the font is bound.

- **Enqueue with `AurPostString 0x0044D490`** — `__cdecl (char* str, int x, int line, float life)`.
  It allocates a `0x434`-byte `PostedString` node, copies the string in, and appends it to the
  global list at `[0x7B90FC]` (count `[0x7B9100]`). **It never touches the font or GL** →
  safe to call at `SwapBuffers` when the font is unbound.
- **The flush runs every present frame** — `UpdateScreen 0x00401C10` calls
  `AurRenderStrings 0x0044D6A0` at `0x00401CAD` **unconditionally**; the console-open flag
  `[0x007A3A40]` gates only the two prompt/buffer enqueues above it, **not** the flush. This
  is the exact path subtitles/feedback use, so an always-on overlay (console closed) renders.
- **Re-post every frame.** The node self-frees after its life counter runs out, and the
  constructor de-dups nodes at the same screen slot, so re-posting the same lines each frame
  keeps the list bounded (no leak).

---

## 1. The enqueue function — `AurPostString 0x0044D490` (VERIFIED)

DB name `Global::AurPostString`; the task's "console line printer". `__cdecl`,
`param_size_bytes = 16` (caller-cleaned — the function ends in a plain `ret`, and its two call
sites in `UpdateScreen` do `add esp, 0x20` for the two 16-byte calls).

```
0x0044D490  push 0x434 ; call 0x6FA7E6 (operator new)      ; node = new PostedString[0x434]
0x0044D4D9  call 0x44D320                                  ; PostedString::ctor(str, x, line, life)
0x0044D4EC  ret                                            ; returns node* in EAX
```

Signature (arg order recovered from the pushes into the `__thiscall` ctor and confirmed
against the two `UpdateScreen` call sites):

```c
PostedString* __cdecl AurPostString(const char* str, int x, int line, float life);
```

`UpdateScreen` calls it as `("> ", 0, 0, 5.0)` and `(cmdBuffer, 0, 2, 5.0)` — prompt on row 0,
the typed line on row 2, both at column 0. So:

| Arg | Meaning | Notes |
| --- | --- | --- |
| `str` | ASCII text, copied inline into the node (`+0x000`) | length cached at `+0x400` |
| `x` | **column index** into a viewport-derived grid | `0` = left edge |
| `line` | **row index** into that grid | `0` top; `2` is two rows lower (VERIFIED: prompt row 0 vs buffer row 2) |
| `life` | **lifetime / fade seed** (NOT a font scale) | stored to `+0x404` *and* `+0x408`; `5.0` ⇒ ~50 flushes before auto-free (see §3). **Correction to the docs' "scale" guess.** |

**Returns the node pointer** (EAX = the `new` result, preserved through the ctor). Useful if
you want to poke the node's color/lifetime directly (§4).

**Font-safety (VERIFIED).** The ctor `0x0044D320` only: copies the string, writes the int/float
fields, runs the de-dup pass (`CExoArrayList::Remove 0x00445080` + `free 0x006FA390`), grows the
list (`CExoArrayList::Allocate 0x005E03C0`), and reads the 8 color floats at `0x0078E368` (plain
`.data`). **No reference to the texture font `0x0083052C`, no GL call, no `GLRender::*`.** This is
why it is safe from the present hook while the immediate path is not.

### Why *not* `AurPrintString 0x0044CE50`

`Global::AurPrintString 0x0044CE50` is **not** an enqueue — it is the immediate single-string
renderer that the flush calls per node. It queries `glGetIntegerv(GL_VIEWPORT=0xBA2)`
(`[0x73D244]` at `0x0044CE6B`), computes grid geometry, and calls the font path
`GLRender::SetUpTextFormat 0x0044CB10` → `PrintStringScreenPos 0x0044CCA0` →
`GLRender::PrintStringAtScreenPos 0x0044CB90`. **It touches the font and needs the render pass
context — calling it from the hook has the same crash exposure as `0x0044CCA0`.** Do not call
it from the hook. `AurPrintString` is also gated by `[0x0078E360]` (default `1`); if that global
were `0` it early-returns and draws nothing — normally on, noted for completeness.

**Answer to Q1:** use `0x0044D490` (enqueue). `0x0044CE50` is the draw and is unsafe here.

## 2. When does the queue flush? — `UpdateScreen 0x00401C10` (VERIFIED)

The relevant block (present path, `esi = arg3 = 0`):

```
0x00401C64  call 0x40CC50            ; main scene render pass (font gets bound in here)
0x00401C69  test esi,esi ; jne skip  ; esi!=0 => offscreen path, skip text+swap
0x00401C75  call 0x5EDB40 ; jne skip ; render-eligibility gate (returns 0 on a normal present)
0x00401C7E  mov eax,[0x7A3A40]       ; <-- CONSOLE-OPEN FLAG
0x00401C83  test eax,eax
0x00401C85  je  0x401CAD             ; console CLOSED => jump straight to the flush
0x00401C93  call 0x44D490            ; AurPostString("> ", 0, 0, 5.0)     } only when
0x00401CA5  call 0x44D490            ; AurPostString(cmdbuf, 0, 2, 5.0)   } console open
0x00401CAD  call 0x44D6A0            ; AurRenderStrings  <-- FLUSH, reached either way
0x00401CC2  call [0x73D00C]          ; SwapBuffers  (our IAT hook fires HERE)
```

**VERIFIED:** the console flag `[0x007A3A40]` gates **only** the two prompt/buffer enqueues.
The `je 0x401CAD` jumps *to* the flush, so `AurRenderStrings` runs **every present frame**
regardless of the console state. This is the same unconditional flush that draws subtitles and
combat-feedback text during normal play (they are `PostedString`s posted elsewhere and drawn
here). **An always-on chat overlay will therefore render with the console closed.**

The block is skipped only when `arg3 (esi) != 0` (an offscreen/backbuffer render that also
skips `SwapBuffers`) or when the `0x5EDB40` eligibility check returns non-zero — INFERRED to be
false on any frame that actually presents, since subtitles render on those frames. Any frame
that reaches `SwapBuffers` (our hook) has already run the flush.

**Timing consequence (VERIFIED ordering, 1-frame latency):** the flush at `0x401CAD` runs
*before* the `SwapBuffers` IAT call at `0x401CC2`. Our hook fires *at* `SwapBuffers`, i.e.
*after* this frame's flush. A node we post in the hook is therefore drawn by **next** frame's
flush. With per-frame re-posting the overlay is continuously present (one frame of latency,
invisible in practice).

**Answer to Q2:** the flush is per-frame and ungated by the console flag; it is the subtitle
path. Always-on rendering works.

## 3. Node lifetime & re-queue cadence — `PostedString::Post 0x0044D010` (VERIFIED)

`AurRenderStrings 0x0044D6A0` walks the list at `[0x7B90FC]` (count `[0x7B9100]`) back-to-front
and calls `PostedString::Post 0x0044D010` on each node. `Post`:

1. Draws the node (sets the font RGBA globals from the node's color matrix, calls
   `AurPrintString`, restores them — see §4).
2. **Decrements the life counter** `+0x404 -= 0.1` (`fsub [0x0078E574]` at `0x0044D18A`).
3. When the node's life is spent it is **removed from the list and freed**
   (`CExoArrayList::Remove` + `free 0x006FA390` at `0x0044D1BC–D1CD`).

Seeded from `life` (§1): `+0x404` and `+0x408` both start at `life`. `life = 5.0` ⇒ ~50 flushes
(~50 frames) of visibility before auto-free; the draw fades over that span
(`t = (life0 − remaining)/life0`).

Two independent mechanisms keep a per-frame re-post **bounded (no leak):**

- **De-dup on enqueue (VERIFIED).** The ctor (`0x0044D390–D3EE`) scans existing nodes and, for
  any node on the **same row** (`+0x430`) whose column span overlaps the new one, **erases and
  frees it** before appending. Re-posting line *k* at the same `(x, line)` each frame evicts the
  previous frame's line *k*.
- **Auto-free on expiry (VERIFIED).** Even without de-dup, a node disappears after `~life/0.1`
  flushes.

**Answer to Q3:** the correct usage is **re-post every frame**. The list does not accumulate:
de-dup replaces same-slot nodes immediately, and lifetime frees any stragglers. "Post once and
it stays" also works but only for `~life/0.1` frames and then fades out — not what we want for a
persistent overlay. Re-post each frame with a fresh `life` and the text stays crisp at full
opacity (fresh node ⇒ `t≈0` ⇒ no fade yet).

## 4. Color / alpha for the queued path (VERIFIED mechanism; correction to prior doc)

**Important correction.** For the **queued** path the four RGBA globals
`0x00798BB4/B8/BC/C0` are **not** the color input. `PostedString::Post` *saves* them, then
**overwrites** them from the node's own **8-float color-transform matrix** and *restores* them
after the draw (`0x0044D0D4–D17F`). So writing `0x00798BC0` (alpha) before an `AurPostString`
call has **no effect** on the queued text — that trick only works for the immediate
`PrintStringScreenPos` path in `text-color-alpha.md`.

The node's color matrix lives at `+0x40C..+0x428` and is captured **at enqueue time** by the
ctor from eight `.data` floats `0x0078E368..0x0078E384` (**all `1.0` by default** — verified by
reading the PE; this is the opaque-white we see). `Post` computes each output channel as a
fade-weighted blend of two matrix coefficients (`u,v` derived from the node's fade fraction):

| Font global written by `Post` | Formula | Matrix slots (source `.data`) |
| --- | --- | --- |
| `0x798BB4` R | `u·[+0x41C] + v·[+0x40C]` | `0x0078E378`, `0x0078E368` |
| `0x798BB8` G | `v·[+0x410] + u·[+0x420]` | `0x0078E36C`, `0x0078E37C` |
| `0x798BBC` B | `u·[+0x424] + v·[+0x414]` | `0x0078E380`, `0x0078E370` |
| `0x798BC0` A | `u·[+0x428] + v·[+0x418]` | `0x0078E384`, `0x0078E374` |

With every coefficient `1.0`, each channel = `u+v` and (for a fresh node, `t≈0`) that is `1.0`
→ opaque white. To tint/fade queued text, set these matrix-source globals **before** the
enqueue and restore them after (same save/set/restore discipline the color doc uses, just on
`0x0078E368..0x0078E384` instead of `0x00798BB4..0x00798BC0`):

- **Alpha-only fade (recommended):** set `0x0078E384 = 0x0078E374 = alpha` (the A-channel pair),
  leave the other six at `1.0`. RGB stay white, alpha = `alpha`. INFERRED from the table above;
  confirm the pair maps to A live (NEEDS-LIVE — cosmetic if wrong).
- **Uniform dim:** set all eight to `alpha` (dims color *and* alpha together — text darkens as
  it fades).
- **Alternative (no globals):** `AurPostString` returns the node pointer, so you can instead
  poke `node+0x418` and `node+0x428` = `alpha` right after the call (and set `node+0x404/+0x408`
  for a custom lifetime). Higher precision, no shared-global window to restore.

**Answer to Q4:** the node captures a color *matrix* (`0x0078E368..84`), **not** the RGBA
globals, at enqueue. Drive queued color/alpha through those eight floats (or the returned node),
not `0x00798BC0`.

## 5. Recipe — 6 lines at a screen corner, from the SwapBuffers hook

Per frame, inside the `SwapBuffers` IAT hook (do this **every** frame):

```c
// signatures
typedef void*(__cdecl *AurPostString_t)(const char* str, int x, int line, float life);
AurPostString_t AurPostString = (AurPostString_t)0x0044D490;

// optional alpha (0..1). Matrix-source globals default to 1.0 = opaque white.
float* mA1 = (float*)0x0078E384;  // A-channel pair
float* mA2 = (float*)0x0078E374;
float saveA1 = *mA1, saveA2 = *mA2;
*mA1 = alpha; *mA2 = alpha;       // alpha-only fade (NEEDS-LIVE: confirm pair -> A)

const int col = 0;                // column 0 = left; grid is viewport-derived
const int row0 = 0;               // top; increase to move the block down
const float life = 5.0f;          // ~50 flushes; fresh each frame => full opacity
for (int i = 0; i < 6; ++i)
    AurPostString(lines[i], col, row0 + i, life);   // row per line

*mA1 = saveA1; *mA2 = saveA2;     // restore so subtitles/feedback aren't tinted
```

Nodes posted this frame are drawn by next frame's flush (§2). De-dup + lifetime keep the list
bounded (§3). No call into the font, no GL, no guessed callback ABI.

**Positioning:** `line` (row) and `x` (column) index a grid whose cell size is derived from the
GL viewport inside `AurPrintString` (magic-number divides of width/height; INFERRED cols≈w/N,
rows≈h/M). Rows stack downward from the top (VERIFIED: prompt row 0, buffer row 2). To place the
block at a **lower** corner, raise `row0`; there is no negative/bottom-anchored index, so a
bottom corner means choosing a high `row0` (tune live). This is a coarser, simpler space than
the centered 0.01 space of `PrintStringScreenPos` — good for line lists, less good for pixel
placement.

## 6. Risks

- **NEEDS-LIVE — grid origin & cell size.** Exact columns/rows per viewport (the divisor magics
  in `AurPrintString`) are not pinned; `row0`/`col` need one calibration pass. Wrong values only
  mis-place text — **cannot crash.**
- **NEEDS-LIVE — alpha pair → A channel.** The `0x0078E384/0x0078E374` → alpha mapping is
  INFERRED from `Post`'s float math. If wrong, text is mis-tinted or not faded — **cosmetic,
  never a crash.** Fall back to poking the returned node's `+0x418/+0x428`.
- **VERIFIED-safe — enqueue never derefs the font.** `0x0044D490` + ctor only do heap/array/copy
  work and read `.data`. This is the whole reason to prefer it over `0x0044CCA0`/`0x0044CE50`.
- **Low — de-dup miss.** If a line's text length changes enough that its column span no longer
  overlaps the previous frame's node at that row, the old node is not evicted early — but it
  still auto-frees after `~life/0.1` flushes, so at worst a few extra transient nodes. Keep each
  line at a fixed row and the de-dup catches it. No unbounded growth.
- **Cosmetic — restore the matrix globals.** `0x0078E368..0x0078E384` are shared with all queued
  text (subtitles/feedback). Leaving them tinted tints that text too. Restore after posting.
- **Reminder — 1-frame latency & flush ordering are VERIFIED**, not a risk: the overlay simply
  trails by one frame, which is imperceptible with per-frame re-posting.

## Reference addresses (this build)

Enqueue `Global::AurPostString 0x0044D490` (`__cdecl (char*,int x,int line,float life)`,
returns node*) · ctor `PostedString::ctor 0x0044D320` (`__thiscall`) · draw-per-node
`PostedString::Post 0x0044D010` · immediate renderer `Global::AurPrintString 0x0044CE50`
(unsafe from hook; queries `GL_VIEWPORT`, uses font) · flush `Global::AurRenderStrings 0x0044D6A0`
· present `Global::UpdateScreen 0x00401C10` (flush call `0x00401CAD`, console-gate `je 0x00401CAD`
at `0x00401C85`, `SwapBuffers` IAT `[0x0073D00C]` at `0x00401CC2`).
Node list `[0x007B90FC]` (base) / `[0x007B9100]` (count); feedback list `[0x007B9108]`/`[0x007B910C]`.
Console-open flag `[0x007A3A40]`. Node layout: `+0x000` inline string · `+0x400` length ·
`+0x404` life remaining · `+0x408` life0 · `+0x40C..+0x428` color matrix · `+0x42C` x(col) ·
`+0x430` line(row). Color-matrix source `.data` `0x0078E368..0x0078E384` (8× `1.0` default) ·
fade step `0x0078E574` (`0.1`) · `AurPrintString` enable gate `0x0078E360` (`1`).
Immediate path (font, do NOT call from hook) `PrintStringScreenPos_UseTextureFont 0x0044CCA0`,
font `0x0083052C`, RGBA globals `0x00798BB4..0x00798BC0`.
