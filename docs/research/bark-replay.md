# Why replaying an NPC bark killed the process — solved

Barks are the lines an NPC throws into a bubble without opening a conversation ("beep beep
boop" from a utility droid). Capture works and has always worked. **Replay killed the
instance, twice**, with no crash-probe line and no event-log entry, and the log statement
after the replay call never ran — so the fault was inside the engine call.

The standing hypothesis was right: one of the forwarded arguments is a pointer. It is worse
than that — **two of the four are pointers**, and they are dereferenced on *every* path.

## The verified signature

`CClientExoAppInternal::BarkString` @ `0x005F2C90`, `__thiscall`, **`ret 0x10`** (verified at
`0x005F2CCD`) → four stack arguments:

```c
void __thiscall BarkString(
        CClientExoAppInternal *this,   // ecx
        uint32_t   nClientObjectId,    // arg1 — VALUE   (server id | 0x80000000)
        CExoString *psText,            // arg2 — POINTER (char* @+0, uint32 length @+4)
        CResRef    *psSoundResRef,     // arg3 — POINTER (16 raw bytes, char[16])
        int         bIgnoreRange);     // arg4 — VALUE   (plain int flag)
```

The entry thunk `0x005EDBB0` ("CServerExoApp::BarkString") is `mov ecx,[ecx+4]; jmp 0x5F2C90`
— its `this` is a `CClientExoApp`, whose `+0x04` is the internal. Same `ret 0x10`.

**arg1 is a CLIENT id.** `CClientExoAppInternal::ServerToClientObjectId` (`0x005F2AC0`) is
literally `or eax, 0x80000000` (OBJECT_INVALID `0x7F000000` passes through unchanged), and
`CGameObjectArray::GetGameObject` indexes `((id>>31)&1)<<12 | (id & 0xFFF)` — the top bit
selects the client half of a 0x2000-slot array. Everything else in this mod speaks server
ids, so capture must normalise.

**arg2 is a `CExoString*`.** It reaches `CSWGuiBarkBubble::SetBark` (`0x006A9920`) which
copy-constructs it via `CExoString::Constructor_3` (`0x005E5B00`) — it reads `[src]` as a
`char*` and strlens it. `0x005E5B30 inc eax; mov [esi+4],eax` confirms the repo's standing
trap: **Length includes the NUL.**

**arg3 is a `CResRef*`.** `CSWCCreature::BarkString` (`0x0060F510`) passes it to vtable
`+0x128` = `CSWCCreature::LipSync` (`0x00616310`), whose first act is
`CResRef::GetResRefStr` with the pointer as `this`. `CResRef` is POD `char[16]` (size `0x10`;
its constructor `0x00405ED0` only zeroes 16 bytes) — **not** a NUL-terminated string.

## Why it always died

Both pointers are raw addresses in the *sender's* address space, shipped verbatim. On the
far side the first deref is `CResRef::GetResRefStr(this = arg3)` — a 16-byte read off a wild
pointer — then `SetBark` reads `[arg2]` as a `char*` and strlens it.

Critically, **the `je 0x5F2CD0` at `0x005F2CA8` is not an early-out.** `0x005F2CD0..0x005F2CE7`
re-pushes arg4/arg3/arg2 and calls `CGuiInGame::ShowBarkBubble` with arg1 replaced by
`0x7F000000`. So both pointers are dereferenced whether or not the speaker resolves — which
is why no guard on the speaker ever helped.

It also explains why swapping `this` to the client internal changed nothing: `this` is only
touched at `0x005F2C9B mov edi,ecx` and used solely on the not-found branch (`[edi+0x40]`).
arg1 and arg4 were never the problem.

## The fix

Dereference **at capture time**, where the pointers are still valid, and rebuild the call
locally:

1. `BarkHook.OnBark` copies the text bytes out of the `CExoString` (cut at the first NUL, and
   remember the counted length includes it) and all 16 raw bytes of the `CResRef`. It stores
   the **server** id (`clientObjectId & 0x7FFFFFFF`).
2. The block slot and `BarkPacket` carry `(speaker, text bytes, 16-byte resref, flags,
   serial)` instead of four opaque dwords. The current slot is `BarkStride = 0x14`, which
   cannot hold text — the ring has to widen and `TotalSize` grow, with the serial kept as the
   LAST dword of the slot so the write-serial-last discipline still holds.
3. `Odyssey.PlayBark` builds a real `CExoString` with the engine's own ctor
   (`0x005E5A90`, `ret 4`) and frees it after the call (`0x005E5C20`) — exactly what
   `ExecuteCommandBarkString` does at `0x00548A4A`; `SetBark` copy-constructs, so the callee
   never takes ownership. `CResRef` needs no constructor: stack-allocate 16 bytes, zero-fill.
   arg1 is `serverId | 0x80000000`.

⚠ **A strref alone is not enough.** `CSWSDialog::BarkOneLine` (`0x005A1050`) prefers the DLG
entry's own sound resref (`[dlg+8 + idx*0xB0 + 0x34]`) and falls back to the TLK row's only
when `CResRef::IsValid` fails. So the resolved text+resref pair must travel, not the strref.

⚠ **Do not reuse `PacketWriter.WriteString`** for the text: it is UTF-8 with a single length
byte, so a 200-byte Windows-1252 TLK line with accented characters can expand past the prefix
and be silently truncated mid-line. Add a raw bytes pair instead.

## Symbol-database rows that disagree with this binary

Looked up by address through sqlite directly, which bypasses `q.py`'s verification warning —
each was re-checked against the disassembly:

| Address | DB says | Binary says |
|---|---|---|
| `0x0062AB30` CGuiInGame::ShowBarkBubble | 8 | `ret 0x10` |
| `0x0060F560` CSWCObject::BarkBubble | 8 | 16 |
| `0x006A9920` CSWGuiBarkBubble::SetBark | 12 | 16 |
| `0x0041E550` CTlkTable::Fetch | 9 | `ret 0xC` |
| `0x005EDA50` ServerToClientObjectId | `__stdcall` | `__thiscall` thunk; body ignores ecx, `ret 4` |

`0x005F2C90` and `0x005EDBB0` both match.

## Open

- That the rebuilt call renders a bubble and survives is UNVERIFIED — it needs a live run.
- `[clientInternal + 0x40] = CGuiInGame` is read from disassembly only; exercised solely on
  the speaker-not-found branch.
- The real maximum bark length is unbounded in the binary; 200 is a guess anchored to
  `MaxDialogueLineLength = 220`.
- Whether any shipped bark line has bytes above 0x7F (decides raw bytes vs string) — checkable
  against `dialog.tlk` offline.
