# Screen text with color / alpha (2026-07-31)

Reverse-engineered against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB. Nothing in the install was modified. Verified
by static disassembly + IAT resolution; the one open question (texture env mode) is tagged
NEEDS-LIVE and cannot crash if wrong.

Read `docs/research/console-overlay.md` first — this extends the known text path
`Global::PrintStringScreenPos_UseTextureFont 0x0044CCA0`.

## Headline (VERIFIED)

**Configurable alpha is feasible, and it is the lowest-risk kind of change we can make.**
The font color used by the existing `PrintStringScreenPos_UseTextureFont` is not a parameter
and not glColor state — it is **four `float` globals in `.data`** that the glyph renderer
copies into every vertex:

| Global | Field | Default (in file) |
| --- | --- | --- |
| `0x00798BB4` | red   | `1.0` (`0x3F800000`) |
| `0x00798BB8` | green | `1.0` |
| `0x00798BBC` | blue  | `1.0` |
| `0x00798BC0` | **alpha** | `1.0` |

Default `(1,1,1,1)` is exactly the "opaque white" we see today. `PrintStringScreenPos`
already `glEnable`s `GL_BLEND` with standard alpha blending around the draw (proven below), so
**writing `0x00798BC0` = 0.5 before the call yields ~50%-opacity text with no other change.**
No new function is called, no ABI is guessed — we set plain data the engine already reads.

Recommended usage from the SwapBuffers hook:

```
oldR=*0x798BB4; oldG=*0x798BB8; oldB=*0x798BBC; oldA=*0x798BC0   // save (should be 1,1,1,1)
*0x798BB4 = r; *0x798BB8 = g; *0x798BBC = b; *0x798BC0 = alpha   // set RGBA (0..1 floats)
PrintStringScreenPos_UseTextureFont(line, x, y)                 // one call per line
...
*0x798BB4=oldR; *0x798BB8=oldG; *0x798BBC=oldB; *0x798BC0=oldA   // restore to white
```

Restore afterward: these globals are **shared** with any other engine text that flows through
this font (subtitle/feedback path — see §4), so leaving them tinted would tint that text too.
Failure to restore is a cosmetic bug, never a crash.

---

## 1. `PrintStringScreenPos_UseTextureFont 0x0044CCA0` — GL state it sets (VERIFIED)

`void __cdecl(const char* text, float x, float y)` — DB `param_size_bytes = 12`, cdecl
(caller-cleaned). Unchanged from `console-overlay.md`.

IAT-resolved GL calls at entry (all through `OPENGL32.dll` thunks, names verified from the
import table):

| Site | Thunk | Call |
| --- | --- | --- |
| `0x0044CCB8` | `[0x73D390]` glEnable | `glEnable( *0x78D448 )` — `*0x78D448 = 0x0BE2 = GL_BLEND` |
| `0x0044CCC8` | `[0x73D38C]` glBlendFunc | `glBlendFunc( *0x73F270, *0x73F274 )` = `glBlendFunc(0x0302, 0x0303)` = `GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA` |
| `0x0044CE3B` | `[0x73D22C]` glDisable | `glDisable(GL_BLEND)` at exit (restores state) |

So the function **turns on standard alpha blending for the draw and off again**. It never
calls `glColor*`. The glyph geometry (built in `CAurFont::TextOutA`) carries its own per-vertex
RGBA sourced from the four `0x798BB4..0x798BC0` globals. No sibling overload takes a color/argb
int — the color channel is exclusively those globals.

`x`/`y` are the centered, 0.01-scaled space already documented (scale `0x73DFEC = 0.01`).

**Crash risk of calling it with our args: none new** — we already call it. Setting the four
globals adds zero call-ABI risk.

## 2. `CAurFont::TextOutA 0x004A1770` — where the vertex color comes from (VERIFIED)

`__thiscall`, DB `param_size_bytes = 8`: `this=ecx (CAurFont*)`, `(const char* text, int len)`
pushed. Builds glyph quads into a global vertex vector (`0x00830524`, grown via
`0x00798BB0` count) and hands them to the interleaved-array draw helper `0x00423F10`.

At **both** glyph-emit sites (`0x004A182A–0x004A187E` and `0x004A19F6–0x004A1A50`) it reads
the four color globals and writes them into the staged vertex record:

```
mov eax,[0x798BB4]   ; R  -> vertex
mov ecx,[0x798BB8]   ; G  -> vertex
mov edx,[0x798BBC]   ; B  -> vertex
mov eax,[0x798BC0]   ; A  -> vertex     <-- alpha IS carried into the vertex color
```

TextOutA does **not** take a color argument and does **not** call `glColor` — it reads the
globals. It also contains an inline **color-tag parser** (`cmp al, 0x3c` = `'<'` at
`0x004A18C0`, tag decode at helper `0x004A1600`) — this is KOTOR's `<cXXXX>`-style markup, an
alternate way to recolor mid-string. See §5.

## 3. `AurPrintString 0x0044CE50` / `AurPostString 0x0044D490` / `AurRenderStrings 0x0044D6A0` (VERIFIED)

None of the public queue entry points take a color argument:

| Fn | Conv | param_size | Args (inferred from callers) | Color arg? |
| --- | --- | --- | --- | --- |
| `Global::AurPrintString` | `__cdecl` | 12 | `(PostedString*/text, float x, float y)` | no |
| `Global::AurPostString` | `__cdecl` | 16 | `(const char* str, float x, float y/line, float scale)` | no |
| `Global::AurRenderStrings` | `__stdcall` | 0 | `()` — flushes the queue | no |
| `PostedString::Constructor` | `__thiscall` | 16 | `(str, x, line, scale)` | no |

`AurPostString` heap-allocates the queued node — `push 0x434; call 0x006FA7E6` — so the
**node is `0x434` bytes** (`PostedString`). Its layout (from the constructor `0x0044D320` and
the flush transform in `PostedString::Post 0x0044D010`):

| Offset | Field |
| --- | --- |
| `+0x000` | string (inline char buffer) |
| `+0x400` | string length |
| `+0x404` | **fade / lifetime counter** — decremented by `0.1` (`*0x78E574`) each flush (`0x0044D18A`) |
| `+0x408` | (init from ctor arg) |
| `+0x40C..+0x428` | **color transform matrix** applied to the global RGBA before printing |
| `+0x42C` / `+0x430` | x / y |

`PostedString::Post` is the proof the globals are the live font color: it **saves**
`0x798BB4..0x798BC0`, overwrites them with `matrix(+0x40C) × node-color`, calls
`AurPrintString`, then **restores** them (`0x0044D0FB` set … `0x0044D16D` restore). So the
queued-string system drives color/fade purely by writing these same four globals around each
print — exactly the seam we use.

The node color/matrix is not exposed as an argument on `AurPostString`, so driving color
through the queue would mean writing into a `0x434` struct with a partially-mapped matrix —
higher effort and risk than just setting the four globals ourselves. Not recommended.

## 4. Subtitle / feedback fade path (VERIFIED mechanism, source of confidence for alpha)

The fade KOTOR shows on subtitles/feedback is the `PostedString` queue above: `+0x404` counts
down (`fsub 0.1` per flush at `0x0044D18A`), the `+0x40C` matrix scales the color/alpha, and
the scaled RGBA is pushed into `0x798BB4..0x798BC0` for the draw. That the engine implements
fade by scaling **the same alpha global we intend to set**, through the **same
`GL_BLEND`/`GL_SRC_ALPHA` path**, is strong evidence that vertex alpha genuinely produces
transparency here (i.e. the font texture env is modulating, not replacing). We inherit a
mechanism the engine itself relies on rather than inventing one.

## 5. Fallbacks

- **Inline color tags (INFERRED / NEEDS-LIVE).** TextOutA parses `'<'` tags
  (`0x004A18C0` → `0x004A1600`). Passing a `<c…>`-tagged string to the existing
  `PrintStringScreenPos` would recolor without touching globals. Very low crash risk (same
  call we use). But the tag grammar and whether it encodes **alpha** (likely RGB only) are
  unverified — needs a live string test. Weaker than the global-alpha path for opacity.
- **Dimmed backdrop quad (NOT recommended).** No verified self-contained "draw filled quad"
  primitive was located; building one means issuing raw GL through the engine's IAT thunks
  (state save/restore, ortho setup) — strictly more code and more risk than the global-alpha
  set. The global-alpha text path already gives per-line transparency without a backdrop.

---

## Recommendation

Use the **global-RGBA path**: before the existing `PrintStringScreenPos_UseTextureFont`
call(s) each frame, write floats to `0x00798BB4/B8/BC/C0` (RGB + alpha, `0..1`), draw the N
chat lines, then restore all four to `1.0`. This is the single lowest-risk option — it adds
no new call, no guessed ABI, and no new GL code; it only writes four `.data` floats the glyph
renderer already reads, on a draw that already enables `GL_SRC_ALPHA` blending.

**Biggest risk (NEEDS-LIVE, non-fatal):** the font's GL texture-environment mode. If it is
`GL_MODULATE` (the OpenGL default, and consistent with the engine's own subtitle fade in §4)
vertex alpha scales glyph coverage and text goes translucent as intended. If some setup forced
`GL_REPLACE`, vertex alpha would be ignored and text would stay opaque — a **no-op, never a
crash**. One live pass: set alpha `0.4`, confirm on-screen translucency; if opaque, fall back
to a manual `glColor4f`/`glTexEnv(GL_MODULATE)` bracket around the call. Also verify the shared
globals are restored to white so subtitle/feedback text is not tinted (cosmetic only).

## Reference addresses (this build)

Text draw `PrintStringScreenPos_UseTextureFont 0x0044CCA0` · glyph builder
`CAurFont::TextOutA 0x004A1770` · queue `AurPrintString 0x0044CE50` /
`AurPostString 0x0044D490` / flush `AurRenderStrings 0x0044D6A0` ·
`PostedString::Post 0x0044D010` / `::Constructor 0x0044D320`.
Font RGBA globals `0x00798BB4` (R) `0x00798BB8` (G) `0x00798BBC` (B) `0x00798BC0` (A),
default `1.0`. Vertex-count/buffer `0x00798BB0` / `0x00830524`.
Blend state: `GL_BLEND` enum at `0x0078D448` (=`0x0BE2`); blend factors
`0x0073F270`(=`0x0302` GL_SRC_ALPHA) / `0x0073F274`(=`0x0303` GL_ONE_MINUS_SRC_ALPHA).
Fade step `0x0078E574` (=`0.1`) · coord scale `0x0073DFEC` (=`0.01`).
OPENGL32 IAT thunks: glEnable `[0x73D390]` · glDisable `[0x73D22C]` · glBlendFunc `[0x73D38C]`.
