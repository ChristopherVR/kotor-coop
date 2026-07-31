# Registering a native `~`-console command (2026-07-31)

Verified against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB and static disassembly. Nothing in the install
was modified during research. Read `console-overlay.md` first for the input/render side; this
doc covers the *command table* so we can register `connect`/`disconnect`/… and make the
engine stop printing "Function <x> not found".

Tag legend: **VERIFIED** = read directly from the disassembly on this build. **INFERRED** =
follows necessarily from verified code but not itself an instruction I executed. **NEEDS-LIVE**
= must be confirmed at runtime before shipping.

## Headline

The console command model is simpler than feared and, crucially, **we can register a verb
without inventing any ABI or vtable of our own** — we reuse an engine-supplied *adapter
vtable* and hand it a plain C function pointer. Every consumer of the table reads exactly two
fields of an entry: the **name at offset 0** and a **functor pointer at offset 0x50**. Nothing
else is touched, so a zeroed block with those two fields set is a valid entry.

## The three data structures (all VERIFIED)

### 1. The command table — `0x007A8AB8`, count `0x007B8FB8`
A flat `void*[]` array of pointers to *command-entry* structs, kept **sorted by name**.
`Global::ConsoleFuncs_Add` does a straight insertion sort and `table[count++] = entry` with
**no capacity check** (`0x0044BA80`). The array's backing store runs up to at least the next
known global `0x007B8AB8` — ~`0x10000` bytes ≈ 16 K slots — so appending a handful of verbs to
the ~100 built-ins is safe headroom (**INFERRED** from the gap; the array is static, valid
from load).

### 2. A command entry (the struct a table slot points at)
```
offset 0x00  char  name[...]   ; inline, NUL-terminated, name copied in at register time
   ...       (engine's own entries carry other fields here; the console code never reads them)
offset 0x50  void* functor      ; pointer to the 8-byte functor object below
```
**VERIFIED** the name is *inline at offset 0* (register helper `0x0044C560` copies the string
to `this+0`; every lookup compares bytes at `[entry]`). **VERIFIED** the functor lives at
`+0x50` (`ConsoleFuncs_Add` sort-compare reads `[entry]`; `LookupCommand 0x0044BB90` returns
`*(entry+0x50)`; tab-complete `0x0044C0C0` reads only `[entry]`). No console code reads any
other offset, so **we allocate ≥ `0x54` bytes, zero it, write the name at 0 and the functor
ptr at 0x50** (**INFERRED** minimum size).

### 3. The functor object — 8 bytes, `malloc(8)` at register time (VERIFIED)
```
offset 0x00  void* vtable   ; reuse an engine adapter vtable (see below)
offset 0x04  void* fn       ; OUR raw C function pointer
```
The engine builds exactly this at `0x0044C58C`: `*obj = <vtable const>; *(obj+4) = handler`.

## Adapter vtables — the reusable ABI bridge (VERIFIED)

Three engine vtables already exist; each has 3 slots `[dtor, invoke, noop]`. The dispatcher
calls **slot 1** (`vtable+4`). Pick the one matching the arg shape you want and put its address
in `functor.vtable`:

| vtable const | slot1 (`invoke`) | What it calls your `fn` as |
| --- | --- | --- |
| `0x00741434` | `0x0044BA20` | `fn()` — no argument |
| `0x00741440` | `0x0044BA30` | `fn(char* args)` — arg text after the verb |
| `0x0074144C` | `0x0044BA50` | parses the arg (via `0x006FAE08`, fmt `0x0073D720`) then `fn(parsed)` |

Common slots (shared by all three): slot0 `0x0044C010` (destructor), slot2 `0x0044C000`
(no-op). Base object vtable `0x00741428`. **Use `0x00741440`** — co-op verbs need the argument
string (`connect <ip>`, `say <msg>`).

`invoke` for the string adapter (`0x0044BA30`, **VERIFIED**):
```
mov  eax,[esp+4]      ; the arg string the dispatcher pushed
push eax
call [ecx+4]          ; ecx = functor(this); calls fn(args)  -- fn is cdecl(char*)
add  esp,4            ; adapter cleans fn's arg
mov  byte [0x7B8AB8],0 ; result string set empty -> console prints nothing
ret  4               ; adapter cleans the arg the dispatcher pushed (callee-cleaned here)
```

## Calling conventions (both VERIFIED from real call sites)

- **`ConsoleFuncs_Add 0x0044BA80`** — `void __cdecl ConsoleFuncs_Add(void* entry)`. One arg,
  `ret` (no immediate) → **caller-cleaned** (DB `param_size_bytes = 4`, matches). It **stores
  our pointer; it does not copy** the entry or the name. Real call: `push entry; call
  0x44BA80; add esp,4` (`0x0044C599`).
- **`ConsoleFuncs_Remove 0x0044BB10`** — `void __cdecl ConsoleFuncs_Remove(char* name)`.
  Finds by name, `rep movsd` compacts the array, `count--`.
- **Our handler `fn`** — reached only through adapter `0x0044BA30`, which pushes one arg and
  does `add esp,4` afterward: **`fn` must be `void __cdecl fn(const char* args)`**
  (caller-cleaned). `args` = the text after the verb, leading `=`/spaces skipped and trailing
  whitespace trimmed; empty string if no args. Return value ignored.

## The dispatch & the "not found" print (VERIFIED)

The consumer of a submitted line is **`0x0044C1F0`** (`char* __cdecl RunConsoleLine(char*
line)`, returns result buffer `0x007B8AB8`). It is invoked from the frame/command pump at
**`0x00404810`**: when the "line ready" flag `0x007A3A48` is set it calls `0x0044C1F0` with the
edit buffer `0x007A3A4C`, prints the returned string via `0x0044D490`, then clears
buffer/caret/flag. (The Enter handler at `0x004021C5` in `DoConsole` only echoes the line and
sets that flag — it does **not** dispatch.)

Inside `0x0044C1F0`: skip leading ws → extract the verb (delimited by space/`=`/NUL) into a
local → `LookupCommand(verb)` (`0x0044BB90`):
- **not found** (`0x0044C4AE` `test ecx,ecx; je …`): `sprintf(0x7B8AB8, "Function %s not
  found", verb)` — format string at **`0x0074145C`** (`0x0044C4B5 push 0x74145C`) — and return
  that buffer. **This is the message we want gone.**
- **found**: copy the arg remainder to a scratch buffer, trim trailing ws, then
  `eax=[functor]; push args; call [eax+4]` (`0x0044C537`) — i.e. `functor->vtable[1](args)`
  with `ecx=functor`. Our `fn` runs; adapter blanks the result buffer → console prints nothing.

There is **no wildcard / "unknown-command" vector** — lookup is exact-name only, and the
not-found `sprintf` is inline with a single `je` guard. So registering the verbs is the only
*data-driven* way to silence it (see suppression alternative below).

## Safe registration recipe (for the plugin, main thread)

For each verb (do this **once**, after game init, on the thread that runs the console pump; the
table has no lock, so never register concurrently with engine startup):

1. **Pin one functor** (8 bytes, lives for the whole process): `functor[0] = 0x00741440;
   functor[1] = &OurHandler;` where `OurHandler` is `void __cdecl OurHandler(const char*
   args)`.
2. **Pin one entry** (≥ `0x54` bytes, zeroed): copy the verb string to offset `0`; write
   `&functor` to offset `0x50`.
3. `((void(__cdecl*)(void*))0x0044BA80)(&entry);`  — call `ConsoleFuncs_Add` directly (bypass
   the engine's thiscall wrapper `0x0044C560`; we pre-built the entry it would have built).
4. **Keep `entry`, its name bytes, `functor`, and `OurHandler` alive and non-moving forever.**
   The table stores the *entry pointer*; the name is *not copied*; lookup re-reads them every
   time. Under NativeAOT: use a fixed/pinned native allocation (or `GCHandle.Alloc(…,
   Pinned)` / `NativeMemory.Alloc`), never a movable managed object.
5. To unregister on shutdown: `((void(__cdecl*)(char*))0x0044BB10)(nameptr)`.

One shared `OurHandler` can serve every verb: register distinct entries but, since the adapter
only forwards `args` (not the verb), either use one entry per verb each pointing at its own
thin `fn`, or point them all at one `fn` and recover the verb from context. Simplest correct
form: **one entry + one functor + one `fn` per verb.**

Reuse the engine's own vtable (`0x00741440`) means the entire call path
`dispatcher → adapter → fn` is engine code except the final `call [functor+4]`; we only supply
a correctly-cdecl C function. That removes essentially all of the ABI guesswork that
`console-overlay.md` flagged as the reason registration was deferred.

## Biggest crash risk (NEEDS-LIVE)

**Calling convention of `OurHandler` + validity/lifetime of the two pinned blocks.** The
adapter invokes it caller-cleaned-style and itself does `add esp,4`; if `OurHandler` is
compiled `__stdcall` (its own `ret 4`) the stack is cleaned twice → corruption/crash. It
**must be `__cdecl`**. Equally fatal: if `entry`, `functor`, or the name bytes are freed or
GC-relocated, the next lookup dereferences garbage (`mov eax,[ecx]; call [eax+4]`) and jumps
through a bad pointer. Pin everything for the process lifetime. Secondary: register on the
console/main thread only (unlocked sorted-insert). These are the only items to confirm on a
live run; the struct layout, both ABIs, and the adapter reuse are all read straight from this
build.

## Lighter alternative — just suppress "not found" (INFERRED, optional)

If we prefer not to register at all, the message has a single inline source: the `sprintf` at
`0x0044C4B5`/`0x0044C4BF` in `0x0044C1F0`. A minimal code patch that makes *every* unknown verb
print nothing (not just ours): overwrite the not-found branch so the result buffer's first byte
is set to `0` instead of formatted — e.g. patch the `je 0x0044C4B0` target region to jump to a
`mov byte [0x7B8AB8],0` + return, or NOP the `sprintf` and store a NUL. Trade-offs vs.
registration:
- Pro: no per-verb state to pin; one patch covers all verbs.
- Con: it is a **code patch** (new hooking surface, and it also swallows genuine typos'
  feedback for the player), whereas registration is pure data and cleanly reversible via
  `ConsoleFuncs_Remove`. There is **no flag** that toggles the message — the only lever is this
  code site. Registration is the recommended path; keep this as a fallback if pinning under
  NativeAOT proves awkward.

## Reference addresses

`ConsoleFuncs_Add 0x0044BA80` · `ConsoleFuncs_Remove 0x0044BB10` · `LookupCommand 0x0044BB90`
(returns `entry+0x50`) · `RunConsoleLine 0x0044C1F0` (returns `0x007B8AB8`) · dispatch/pump
site `0x00404810` (flag `0x007A3A48`, edit buffer `0x007A3A4C`) · register-thiscall-wrapper
`0x0044C560`, call sites `0x0044C599 / 0x0044C5F9 / 0x0044C659`.
Table `0x007A8AB8` · count `0x007B8FB8` · lookup cache `0x0078E354` · result buffer
`0x007B8AB8`.
Adapter vtables: no-arg `0x00741434`→`0x0044BA20`; **string-arg `0x00741440`→`0x0044BA30`**;
parsed-arg `0x0074144C`→`0x0044BA50`; shared dtor `0x0044C010`, noop `0x0044C000`, base vtable
`0x00741428`.
Strings: `"Function %s not found"` `0x0074145C` · tab-complete `"%s "` `0x00741458`.
