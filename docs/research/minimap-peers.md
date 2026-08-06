# Peers on the minimap (2026-08-07) — and why the old verdict was half right

`docs/research/animation-and-minimap.md` §B concluded **WON'T FIX**: `CSWGuiMainInterface::DrawMap
0x0068AB10` draws the area image, the fog of war and exactly ONE arrow, with no map-note loop
and no party loop, and the engine's three `CSWSAreaMap` party slots cannot be borrowed because
`UpdateMapData` rewrites them every frame and the M-screen reads them back under the real
party size.

**All of that is still true. The conclusion drawn from it was not.** It assumed that showing a
peer meant building a GUI control, setting up a viewport and ~120 lines on the render path.
It does not: the arrow is already there, already the right size, and already inside an open
viewport. Drawing it a second time at an offset is the whole feature.

Shipped as `MinimapPeerHook` + `Odyssey.DrawMinimapPeers`. Verified live by screenshot.

---

## 1. The hook: one call-site redirect

```
0x0068ACB1  call [edx+0x38]          ; the arrow's own Draw
0x0068ACB4  call 0x00459580          ; AurGUICloseViewport   <- redirected
0x0068ACB9  call 0x004592B0          ; AurGUIStopLayer
```

The five bytes at `0x0068ACB4` become a call to a stub that draws the peers and then
**tail-jumps** to the real `AurGUICloseViewport` — the redirected call already pushed the
return address, so the engine returns straight into `DrawMap`. No prologue to relocate, no
trampoline, and the site is only reached on the path where the viewport was genuinely opened
(`0x0068AC8A` jumps past it otherwise), so a marker can never be drawn outside one.

At that instruction `esi` is the `CSWGuiMainInterface` and `edi` is the draw argument
(loaded at `0x0068AC8C`), which is everything the callback needs.

Install is verify-bytes-or-refuse, and it checks the call's **target** resolves to
`AurGUICloseViewport` rather than just that the opcode is `E8`.

## 2. The offset falls out of the engine's own scroll maths

`DrawMap` places the map image at `(side/2) - pixel*scale`, so a map pixel lands on screen at
`side/2 + (pixel - localPixel)*scale`. That is **relative to the arrow**, which sits at the
viewport centre — measured live, extent `(44, 44, 32, 32)` in a `120×120` viewport. So a peer
marker needs only `savedExtent + (peerPixel - localPixel) * scale`, and this code never needs
to know where the minimap is on screen.

| Value | Where | Notes |
| --- | --- | --- |
| scale | `CSWSAreaMap + 0x14`, float | `DrawMap` stashes it at `[esp+0xC]` and `fmul`s by it twice |
| local pixel | `GetPartyMemberMapLocation(areaMap, 0, &x, &y)` `0x005791B0` | `ret 0xC`, returns 0 when the slot is `-1` |
| peer pixel | see §3 | |
| arrow control | panel `+0x5F40` | embedded, not a pointer; vtable is its first dword |
| arrow extent | control `+0x04` | `CSWGuiObject::SetExtent` vtable `+0x04` is a four-dword copy |
| arrow draw | vtable `+0x38` | the same slot `DrawMap` calls |

## 3. World → map pixel, without reimplementing it

`CSWSAreaMap::SetPartyMemberWorldLocation(slot, Vector)` (`0x005790C0`, `ret 0x10` — the
vector is three separate stack dwords) runs `GetMapPixelFromWorldCoord` and stores the result
in slot `slot`. So the conversion is borrowed rather than reimplemented: write the peer's
position into **slot 2**, read the pixel back, and put the slot's previous contents back.
Nothing observes the borrow — even in a party of three, whose slot 2 `UpdateMapData` rewrites
every frame anyway.

⚠ `SetPartyMemberWorldLocation` only **stores on success**, so the slot is cleared to `-1`
first; without that an off-map peer silently reuses the previous peer's pixel.

## 4. Colour — the part that had to be looked at rather than reasoned about

The arrow embeds a `CSWGuiImage` whose `CSWGuiImageParams` start `0x68` into the control.
Confirmed by a live read of the live panel (angle held the rotation `DrawMap` had just
written; alpha and colour read exactly `1.0` and `(1,1,1)`):

| Control offset | Field |
| --- | --- |
| `+0x78` | `angle` — the map rotation |
| `+0x7C` | `alpha` |
| `+0x80..+0x88` | `color`, a Vector |
| `+0x8C` | `bit_flags` |

The colour is a **multiplier against the shipped art, which is blue**, so the values look
wrong written down and right on screen. Measured, each one captured and looked at:

| Multiplier | Result |
| --- | --- |
| `(1, 0.15, 0.15)` | dark maroon — too dark to read |
| `(4, 0.4, 0.4)` | purple (the blue channel still wins) |
| `(6, 0, 0)` | **red** |
| `(0.2, 4, 0.2)` | **green** |
| `(4, 0.3, 0)` | **orange** |
| `(5, 0.4, 3)` | **purple** |
| `(0.2, 3, 3)` | **cyan** |
| `(4, 2, 0)` | **yellow** |

Above 1.0 saturates a channel rather than clamping the result, which is the only way to get a
red arrow out of blue art. The six in the table are the shipped palette, one per peer slot;
more peers than that cycle it. The local player's arrow is left alone.

## 5. Restore everything, and draw yourself last

Extent, rotation, colour and the borrowed party slot are all saved and put back. The peers are
drawn **after** the engine's own arrow (the hook site is past it), so the local arrow is drawn
once more at the end — otherwise a partner standing where you are hides you, which is the one
place you always want to be able to find yourself. One extra textured quad, only when a peer
was actually drawn.

A peer outside the map is clipped by the viewport the engine already set up. That is the
behaviour you want: a partner who is off your map is not on your map.

`coop.ini minimap_peers=0` opts out.
