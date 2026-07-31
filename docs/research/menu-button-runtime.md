# Forcing the dormant BTN_WARP visible at runtime (research 2026-07-31)

How the in-process plugin makes the main menu's dead **BTN_WARP** control appear, relabels it
"Coop", moves it into the button column, and receives its clicks — **entirely by touching the
live control each frame**, no `.gui` Override, no code patch required. Verified against
`swkotor.exe` (GOG retail 1.0.3; disk == memory, base `0x400000`, no ASLR) via the
sha256-matched symbol DB. Nothing was modified.

Read alongside `connect-flow.md` (BTN_WARP AddEvent rebind, `CSWGuiSaveNamePanel`) and
`server-browser.md` (the list widget that hangs off this button).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not yet run ·
**NEEDS-LIVE** = confirm on the running game before shipping.

## Headline — the whole mystery in three instructions

The main-menu constructor `CSWGuiMainMenu::Constructor 0x0067C4C0` constructs BTN_WARP, runs
the `.gui` layout (which positions it, texts it "Warp", and adds it to the draw list), and then
**deliberately hides it again**:

```
0x0067C664  call 0x0067ACE0          ; CSWGuiMainMenu::LoadFromLayout  (loads mainmenu*.gui)
0x0067C669  mov  ecx, [edi + 0x44]    ; edi = BTN_WARP  (mainMenu + 0x1248)
0x0067C66C  mov  eax, 0xFFFFFFFD
0x0067C671  and  ecx, eax             ; clear bit1 (0x2)  <-- HIDES the control
0x0067C673  mov  [edi + 0x44], ecx
0x0067C676  and  dword [esi + 0xd08], eax   ; same hide applied to a 2nd dormant control
```

**Bit 1 (0x2) of `control+0x44` is the visible/interactive flag.** Both the draw loop and the
mouse hit-test skip any control whose bit1 is clear (proved below). LoadFromLayout had *set*
that bit while adding BTN_WARP to the panel's control array; the ctor clears it one instruction
later. That is exactly why a `.gui` Override that repositions BTN_WARP still never draws — the
ctor re-hides it after the layout, unconditionally, regardless of the asset. **VERIFIED.**

So the fix is trivial and needs no patch: **set bit1 back**. The control is already in the draw
list, already positioned, already texted, already click-bound. We just un-hide it (and,
optionally, move + relabel it).

## The +0x44 flags byte — full decode (VERIFIED)

Every `CSWGuiControl` (BTN_WARP is a `CSWGuiButton`, vtable `0x0073E658`, size `0x1C4`, ctor
`0x0041C0F0`) has an undocumented flags dword at **`control+0x44`**. Bits, each read from the
setter/consumer that uses it:

| Bit | Mask | Meaning | Proof |
| --- | --- | --- | --- |
| 0 | `0x01` | **active / highlighted** (selects which border draws) | `CSWGuiControl::SetActive 0x004181D0` toggles it; `CSWGuiButton::Draw 0x00417AB0` @`0x417AB3` `test [esi+0x44],1` picks border |
| **1** | **`0x02`** | **VISIBLE + hit-testable** — the one that matters | draw gate `0x0040B841`; hit gate `0x004187C5`; cleared by ctor `0x0067C671` |
| 3 | `0x08` | enabled | `CSWGuiControl::SetEnabled 0x004176A0` @`0x4176B0` `and edx,8` |
| 5 | `0x20` | locked-out (blocks hit-test even when visible) | hit gate `0x004187CA` `test cl,0x20; jne no-hit` |

⚠ There is **no** `CSWGuiControl::SetVisible` in the binary — only `CSWGuiPanel::SetVisible
0x0040B9D0`, which sets a *different* bit (bit2 `0x4`) on a *panel*, not on a control. Do not
call it on BTN_WARP; it is the wrong object and the wrong bit. Un-hide a control by writing bit1
of `+0x44` directly.

### Draw gate — VERIFIED

`CSWGuiPanel::Draw 0x0040B760` (which `CSWGuiMainMenu::Draw 0x0067AC10` tail-calls) draws the
panel background, then walks the control array `controls@panel+0x20` / `count@panel+0x24`:

```
0x0040B837  mov  eax, [esi+0x20]          ; controls[] base
0x0040B83A  mov  ecx, [eax + edi*4]       ; controls[i]
0x0040B83D  test ecx, ecx    / je skip
0x0040B841  test byte [ecx+0x44], 2       ; <-- VISIBLE bit
0x0040B845  je   skip                     ;     bit1 clear -> control NOT drawn
0x0040B847  mov  edx, [ecx]               ; vtable
0x0040B84A  call [edx+0x38]               ; control->Draw  (GuiControlMethods slot 0x38)
```

### Hit-test gate — VERIFIED

Panel hit-testing `CSWGuiPanel::HitCheckMouse 0x0040B690` walks the same array top-down and
calls each control's `vtable+0x44` (`HitCheckMouse`). `CSWGuiControl::HitCheckMouse 0x004187C0`:

```
0x004187C2  mov  ecx, [eax+0x44]
0x004187C5  test cl, 2      / je no-hit   ; must be VISIBLE (bit1) to be clickable
0x004187CA  test cl, 0x20   / jne no-hit  ; must NOT be locked (bit5)
0x004187CF  ...compare mouse vs extent[eax+4..]
```

**Consequence:** setting bit1 makes BTN_WARP *both* draw *and* receive clicks in one write
(bit5 is already clear — the ctor never sets it). No separate "register for input" step exists;
visibility and clickability are the same bit. **VERIFIED.**

## Extent (reposition) — VERIFIED

`CSWGuiExtent` at `control+0x004`, four `int`s: **`+0x04` left, `+0x08` top, `+0x0C` width,
`+0x10` height** (screen pixels; screen size at globals `SCREEN_WIDTH 0x0078D1D4`,
`SCREEN_HEIGHT 0x0078D1D8`).

Two ways to move it:
- **Recommended: `CSWGuiButton::SetExtent 0x00417A20`** — `__thiscall void(this, CSWGuiExtent*
  src)`, ret 4. Copies the 4 ints into `this+0x04` **and re-lays the button's borders + label**
  off the new rect (`0x417A44` reads `[esi+0x6c]` border, etc.). Pass a pointer to a 16-byte
  `{left,top,width,height}` struct in `ecx=BTN_WARP`. VERIFIED (copy loop `0x417A30..0x417A41`).
- Fallback: write the four ints at `+0x04..+0x10` directly. Simpler, but the label/border keep
  their old offsets — only safe if you leave width/height as-is and merely nudge left/top.

To slot it into the button column, copy the extent of an adjacent live button (e.g. read
another menu button's `+0x04..+0x10`) and offset `top` by one row. NEEDS-LIVE for exact pixels.

## Relabel to "Coop" — VERIFIED

Button label lives in a `CSWGuiText` at **`button+0x154`**; its `CSWGuiTextParams` is at
`+0x18` inside that → **`button+0x16C`** (cross-checked: `CSWGuiButton::SetActive` pokes
`[button+0x16C+0x38]` = `text_params.bit_flags`).

`CSWGuiTextParams::SetText 0x00415E00` — `__thiscall void(this=CSWGuiTextParams*, CExoString*
src)`, ret 4. VERIFIED body:
```
0x00415E08  call 0x005E5C50            ; CExoString::operator=(this.text@+0x00, src)  -> arg is CExoString*
0x00415E12  mov  dword [esi+0x08], -1  ; str_ref = 0xFFFFFFFF  (use the literal, ignore TLK)
0x00415E33  call [edi+0x18]            ; refresh the linked text_object [esi+0x50]
```
`0x005E5C50` is `CExoString::operator=(const CExoString&)` — so **the argument is a
`CExoString*`, not a `char*`**. Build one on the stack:

```
CExoString s;                                   // 8 bytes {char* data@0, int len@4}
((void(__thiscall*)(void*,const char*))0x005E5A90)(&s, "Coop");  // CExoString::CExoString(const char*)
((void(__thiscall*)(void*,void*))0x00415E00)((char*)warpBtn + 0x16C, &s);  // SetText
((void(__thiscall*)(void*))0x005E5C20)(&s);     // ~CExoString  (free the copy)
```
`this` for SetText is `warpBtn + 0x16C`. Set once (idempotent); re-applying every frame is
harmless. VERIFIED signatures for `0x005E5A90` (ctor, strlen loop @`0x5E5AA1`) and `0x005E5C20`
(dtor).

## Clicks — already bound (VERIFIED)

The ctor already `AddEvent`s BTN_WARP for click and accept:
```
0x0067C6CE  push 0x0067C4B0 ; push esi ; push 0x27 ; ecx=edi(BTN_WARP) ; call 0x0041AB20  ; click  -> OnWarpButtonPressed
0x0067C734  push 0x0067C4B0 ; push esi ; push 0x2D ; ecx=edi(BTN_WARP) ; call 0x0041AB20  ; accept
```
`OnWarpButtonPressed 0x0067C4B0` is a stub (`ret`). Per `connect-flow.md`, re-`AddEvent` the
same event ids (`0x27`, `0x2D`) with the plugin sink — same-id AddEvent **replaces**. Because
hit-testing keys off the *same* bit1 we set for drawing, the click path lights up automatically
the moment the control is visible; no focus/selection registration is needed to receive a
mouse click. (D-pad/keyboard *navigation* to it is a separate concern — `CSWGuiNavigable`
up/left/down/right links at control `+0x5C/+0x60/+0x64/+0x68` are unset for BTN_WARP; mouse
clicks don't need them. NEEDS-LIVE if you want gamepad focus.)

## Runtime recipe (per frame, while on the main menu)

Resolve, then poke. All `__thiscall` (`this` in `ecx`).

```
; 1. resolve the menu   (chain per connect-flow.md / dialogue-sync.md)
appMgr        = *(void**)0x007A39FC
clientApp     = *(void**)(appMgr     + 0x04)     ; CClientExoApp
clientInternal= *(void**)(clientApp  + 0x04)     ; CClientExoAppInternal
mainMenu      = *(void**)(clientInternal + 0x280); CSWGuiMainMenu*   (null unless on menu -> bail)
warpBtn       = *(void**)(mainMenu   + 0x1248)   ; CSWGuiButton*     (null-check -> bail)

; 2. UN-HIDE  (the essential write; makes it draw AND clickable)
*(uint32*)(warpBtn + 0x44) |= 0x02;              ; set visible bit1
*(uint32*)(warpBtn + 0x44) &= ~0x20;             ; defensively clear lock bit5

; 3. REPOSITION into the button column   (once is enough; safe to repeat)
CSWGuiExtent ext = { left, top, width, height }; ; copy from a sibling button, offset top
((void(__thiscall*)(void*,void*))0x00417A20)(warpBtn, &ext);   ; CSWGuiButton::SetExtent

; 4. RELABEL "Coop"   (once; idempotent)
CExoString s; ctor 0x005E5A90(&s,"Coop");
((void(__thiscall*)(void*,void*))0x00415E00)(warpBtn + 0x16C, &s);  ; SetText
dtor 0x005E5C20(&s);

; 5. CLICK sink  (once per menu instance; AddEvent replaces same-id)
((void(__thiscall*)(void*,int,void*,void*))0x0041AB20)(warpBtn, 0x27, ctx, OnCoopClicked);
((void(__thiscall*)(void*,int,void*,void*))0x0041AB20)(warpBtn, 0x2D, ctx, OnCoopClicked);
```

Do step 2 **every frame** (cheap, and it survives any menu re-entry that reruns the ctor's
hide). Steps 3–5 are idempotent; re-run them when `mainMenu` or `warpBtn` changes identity
(menu was rebuilt) — track the last pointer and re-apply on change.

## Recommended vs fallbacks

- **RECOMMENDED (no patch):** the per-frame recipe above. Zero binary modification, survives
  menu rebuilds, reversible (stop writing → the control is hidden again next frame). This is the
  ship path.
- **Fallback A (one-time patch):** NOP the 3-instruction hide at `0x0067C669..0x0067C673`
  (7 bytes: `8B 4F 44` / `B8 FD FF FF FF` / `23 C8` / `89 4F 44` — NOP the final `and`/`store`,
  or just replace `mov eax,0xFFFFFFFD` with `mov eax,0xFFFFFFFF` so the `and` is a no-op). Then
  BTN_WARP stays visible from birth at its `.gui` position. Less flexible (still need SetExtent/
  SetText), and it edits code — prefer A only if the per-frame write proves racy.
- **Fallback B (control not in draw list):** if NEEDS-LIVE reveals BTN_WARP is *not* in the
  panel array on the shipping `.gui`, add it once: `CSWGuiPanel::AddControl 0x0040B9A0(mainMenu,
  warpBtn)`, then continue from step 2. (Evidence says this is unnecessary — see failure mode.)

## Single most likely failure mode — and why it is benign

**BTN_WARP is not actually in the panel's `controls@+0x20` array on the stock `mainmenu*.gui`.**
Then step 2 sets bit1 on a real object but the draw loop never iterates it, so nothing appears —
and SetExtent/SetText still operate only on the control's own owned memory. **No crash**: every
write in the recipe targets fields inside the BTN_WARP object itself (its `+0x44`, its extent,
its own `CExoString`), never the menu's array or the render state. The worst outcome is "the
button doesn't show," identical to today. Recovery is Fallback B (one `AddControl`).

Why this failure is unlikely: the ctor clears bit1 *after* LoadFromLayout **and** does so
in-place on `[edi+0x44]` — pointless unless the layout had just added the control and set that
bit. The stock asset also carries BTN_WARP's inline "Warp" text (per prior research), i.e. the
layout defines it. So on the retail menu it is almost certainly already in the array, positioned
and texted — only bit1 is off. **NEEDS-LIVE:** one debugger pass to confirm `warpBtn` appears in
`[mainMenu+0x20][0..count]` and that step 2 alone makes "Warp" render.

## Address / offset quick table

| Thing | Value | Tag |
| --- | --- | --- |
| menu resolve | `*(*(*0x007A39FC+4)+4) + 0x280` = `CSWGuiMainMenu*` | VERIFIED (chain) |
| BTN_WARP | `mainMenu + 0x1248` → `CSWGuiButton*` (vtable `0x0073E658`) | VERIFIED |
| flags dword | `warpBtn + 0x44` — bit1 `0x2`=visible/hittable, bit0 `0x1`=active, bit3 `0x8`=enabled, bit5 `0x20`=locked | VERIFIED |
| the hide instr | `0x0067C669..0x0067C673` `and [warpBtn+0x44], 0xFFFFFFFD` | VERIFIED |
| draw gate | `0x0040B841 test [ctl+0x44],2` (skip if clear) | VERIFIED |
| hit gate | `0x004187C5 test cl,2` / `0x004187CA test cl,0x20` | VERIFIED |
| extent | `warpBtn + 0x04` L / `+0x08` T / `+0x0C` W / `+0x10` H (int) | VERIFIED |
| SetExtent | `0x00417A20` `__thiscall(this, CSWGuiExtent*)` ret4 — moves borders+label | VERIFIED |
| label text_params | `warpBtn + 0x16C` (text `+0x154`, params `+0x18`) | VERIFIED |
| SetText | `0x00415E00` `__thiscall(this=params, CExoString*)` ret4; sets str_ref=-1 | VERIFIED |
| CExoString ctor / dtor | `0x005E5A90(this, const char*)` / `0x005E5C20(this)` | VERIFIED |
| AddEvent | `0x0041AB20` `__thiscall(ctl, int evtId, void* owner, void* fn)` ret0xC; 0x27=click 0x2D=accept | VERIFIED (see connect-flow.md) |
| existing warp click sink | `OnWarpButtonPressed 0x0067C4B0` (stub) | VERIFIED |
