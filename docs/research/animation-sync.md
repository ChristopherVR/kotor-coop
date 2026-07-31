# Syncing played animations (weapon-draw, flourish, attack) to a phantom

Research 2026-07-31. Target: retail GOG `swkotor.exe` v1.0.3, image base `0x00400000`,
no ASLR, disk == memory. Symbol DB `tools/kotor1_0_3.db`. Sibling docs:
`docs/re/engine-movement.md` (the walk/run animation pipeline and the three movement
fields), `docs/re/object-model.md` (server `CSWSCreature` vs client twin `CSWCObject`),
`docs/research/name-plates.md` (the twin-write pattern the plugin already uses).

## Provenance / label meaning

- **VERIFIED** = read directly out of `kotor1_0_3.db`, or out of the instruction stream of
  this exact binary (via `tools/disasm.py` / `tools/xrefs.py`). Facts about shipped code.
- **INFERRED** = my reading of what code I did disassemble *means* (field/arg semantics).
- **NEEDS-LIVE** = cannot be closed without calling into / observing the running game.

No game memory was written and no game function was called for this document; every claim
below is static.

---

## 0. Executive summary

The suspicion in the brief is correct, but the fix is smaller than feared.

- **The server field `CSWSCreature + 0xD4` is the "current animation" for the server, and
  `CSWSCreature::SetAnimation` writes *any* animation into it — movement and
  fire-and-forget alike.** It is not movement-only. (VERIFIED, `0x004F0D70`.)
- **Why draws/flourishes don't sync: the local player's "x" draw never passes through the
  server at all.** It is a purely client-side call, `CSWCCreature::PlayDrawAnimation`
  (`0x00610A70`), which pushes the draw straight onto the client twin's fire-and-forget
  queue. The server `+0xD4` on the local player therefore never sees the value, so a plugin
  that reads `+0xD4` each tick cannot observe it. (VERIFIED.) The same is true of the
  combat swing *visuals*, which the client resolves from `combatanimations.2da` and plays
  via the twin. This is a **read-side blindspot, not an apply-side one.**
- **The apply side already works.** When the server sets `+0xD4` and marks animation dirty,
  the per-frame client update replays it through
  `CSWCCreature::ClientServerSyncHandler_Animation` (`0x0060C100`), which asks
  `CSWCObject::AnimationFireAndForget` (`0x0063C380`) "is this a fire-and-forget id?" and,
  if yes, calls `CSWCCreature::AddFireForgetAnimation` (vtable `+0xA0`, `0x006183D0`) — the
  weapon-draw/attack path — otherwise a looping set. So
  **`SetAnimation(phantom, 0x2806)` through vtable `+0x7C` is enough to make the phantom
  visibly draw a weapon** (same mechanism that already makes walk/run work). VERIFIED chain.
- **Exact field to READ on the local player to catch a played animation:** the client
  twin's fire-and-forget activity, not `+0xD4`. Best done by hooking the two client entry
  points (`PlayDrawAnimation 0x00610A70` and `CSWCCreature::AddFireForgetAnimation
  0x006183D0`) and capturing the `ushort` animation id they are handed. Poll-only fallback:
  twin `+0x444` (pending draw, `0xFFFF` = none) and twin fire-forget count at `+0x5A`.
- **Exact call to APPLY on the phantom:** either `SetAnimation` (vtable `+0x7C`,
  `0x004F0D70`, `thiscall(this, int anim)`) — reuses the existing code path — or, for
  guaranteed replay, `AddFireForgetAnimation` on the twin (vtable `+0xA0`, `0x006183D0`,
  `thiscall(twin, uint anim, void* pOpts=NULL)`, 8 stack bytes). Both VERIFIED-callable.
- **Biggest risk:** `SetAnimation` only re-marks the dirty bit when `+0xD4` *changes*
  (VERIFIED at `0x004F0F33`), so two identical consecutive fire-and-forgets (two attacks of
  the same swing, draw-sheathe-draw) will not replay unless you bounce `+0xD4` through
  another value first — or apply via `AddFireForgetAnimation`, which queues every call.

Animation ids in this build are 10000-based (`0x2710`). The **same numeric space is used by
`+0xD4`, by the twin fire-forget queue, and by `SetAnimation`** — so the wire can stay a
single `uint`.

---

## 1. What holds the "current played animation"

### 1.1 Server side: `CSWSCreature + 0xD4` and `SetAnimation` (VERIFIED)

`SetAnimation` is virtual slot `+0x7C`. `CSWSObject::SetAnimation` (`0x004CCF60`) is 4
instructions: `+0x1FC |= 1` (dirty bit 0 = animation) and `+0xD4 = anim`. The creature
override `CSWSCreature::SetAnimation` (`0x004F0D70`) does the real work. Disassembled in
full; the shape is:

```
esi = anim
if anim in {0x2710, 0x2711}          -> remap through the ambient table (1.4)
elif anim == -1                       -> esi = 0x2710
                                      // any OTHER value (draw/attack/flourish/...) passes
                                      // straight through untouched
IsInjured & gore-option ? substitute  0x2712->0x276D, 0x2714->0x276E   (walk/run only)
if GetDead()/GetDeadTemp() and anim not in {0x2716,0x2718,0x27AC}
                                      -> skip client push, just write +0xD4   (0x4F0F4A)
twin = client twin (via +0x224 chain)
isFF = CSWCObject::AnimationFireAndForget(twin, anim)      // 0x0063C380, PREDICATE only
if !isFF  OR  +0xD4 != anim:  SetDirtyFlag(0)              // 0x004F0F3B  <-- dirty-on-change
+0xD4 = anim                                               // 0x004F0F44 (always)
```

Key reading: `SetAnimation` accepts **every** animation id, not just movement. It uses
`AnimationFireAndForget` here **only as a predicate** to decide whether to raise the
animation dirty bit (fire-forgets that repeat the same value do not re-dirty). It always
writes `+0xD4`. So `+0xD4` is a faithful "last animation the *server* set." The catch is
that some animations are never set on the server at all (section 2).

`ambient_animation_state` (`+0x4C4`, byte) only ever affects the idle ids `0x2710`/`0x2711`:
`SetAnimation` does `jmp [ (byte[+0x4C4]-2)*4 + 0x004F0F50 ]` for those, remapping idle to a
context idle variant (`0x274A, 0x27A6, 0x28A0..0x28A2, 0x2716, 0x2718, 0x27AC, 0x2785,
0x278C, 0x2786, 0x2799, 0x279A`). Irrelevant to played animations; do not mirror `+0x4C4`.

### 1.2 Client twin: where a *played* animation actually lives (VERIFIED)

The twin is `CSWCCreature*` at `[server + 0x224]` (guard `[twin] == 0x0074F5F8`, the
`CSWCCreature` vtable). Relevant twin fields:

| Twin offset | Type | Meaning | Evidence |
| --- | --- | --- | --- |
| `+0x48` | u16 | **current looping/base animation** (`GetCurrentAnimation 0x0063C060` = `mov ax,[ecx+0x48]`) | VERIFIED |
| `+0x4C` | u32 | last server-sync arg (timestamp-ish), written by the sync handler | VERIFIED |
| `+0x58` | u16 | `looping_animation` (DB) | DB |
| `+0x5A` | u16 | **fire-forget queue count** | VERIFIED (`InsertFireForgetAnimation` reads `[esi+0x5a]`) |
| `+0x5C` | ptr | **fire-forget queue** (list of `0x1C`-byte entries; top via `GetTopFireForgetAnimation 0x0063C610`) | VERIFIED |
| `+0x60` | u8 | fire-forget queue capacity | VERIFIED |
| `+0x68` | ptr | `anim_base` (`CSWCAnimBase*`) — the resolved model animation actually rendering; name `CResRef` at `+0x2C` | DB |
| `+0x444` | u16 | **pending draw-weapon animation**, `0xFFFF` = none (set by `PlayDrawAnimation`, consumed by `AnimationUpdate`) | VERIFIED |

**`+0x48` (current looping anim) is NOT where a draw/attack lives.** Fire-and-forget
animations are pushed onto the queue at `+0x5C` and drained per frame by
`CSWCCreature::AnimationUpdate` (`0x0061A980`, which calls `AnimateFireAndForget 0x0061A670`
and processes `+0x444`); they never change `+0x48`. So polling `+0x48` gives you the same
information the server already has and misses exactly the animations we care about.

`GetTopFireForgetAnimation` (`0x0063C610`) returns `0x2710` (idle) via its out-param when
the queue is empty, and the top entry otherwise. That is the client's answer to "what
fire-and-forget is playing right now."

---

## 2. The "x" draw path, and which animations bypass the server

### 2.1 Draw-weapon (VERIFIED)

Pressing "x" runs `CSWCCreature::PlayDrawAnimation` (`0x00610A70`), a **client** method. Its
only two callers pass the literal draw id: `push 0x2806; call 0x610A70` at `0x006138EE`
(inside the equip/ready update) and `push [twin+0x444]; call 0x610A70` at `0x0061AE06`
(inside `AnimationUpdate`, replaying a deferred draw). Body:

```
if (client "paused/loading" test 0x5EDC10)  twin->+0x444 = anim;  return;   // defer
else  CSWCObject::InsertFireForgetAnimation(twin, anim, 0x5DC/*1500ms*/, 1.0f, 0, 0);
      twin->+0x444 = 0xFFFF;                                                 // play now
```

So **`0x2806` (10246) = draw/ready weapon** (VERIFIED). It goes onto the twin fire-forget
queue and never touches the server object. `InsertFireForgetAnimation` (`0x0063C760`) calls
`AnimationFireAndForget` to validate, then grows the `+0x5C` array and bumps `+0x5A`.

### 2.2 NWScript `PlayAnimation` and `AIActionPlayAnimation` DO reach the server (VERIFIED)

`ExecuteCommandPlayAnimation` (`0x00540550`) and the queued handler
`CSWSObject::AIActionPlayAnimation` (`0x0057D080`, action id 6 in the queue dispatcher)
run on the **server** creature, compute a duration (they special-case ids near
`0x2790..0x279D`, storing the animation end time to `server+0xC4/+0xC8`) and end up calling
`SetAnimation` through the vtable — i.e. they land on `+0xD4` and replicate normally. So
**scripted animations already sync** through the existing `+0xD4` read; only the
client-initiated ones (2.1, and combat swing visuals) do not.

### 2.3 Combat swings (INFERRED + NEEDS-LIVE)

The server picks *stance/ready* ids in `CSWSCreature::ResolveMeleeAnimations`
(`0x005B7470`) — e.g. it writes `0x271B`/`0x271C`/`0x271E` (combat-ready stances) into a
combat-round struct — but the visible **swing** is resolved on the client from
`combatanimations.2da` via `CClientExoAppInternal::GetClientMeleeAnimation` (`0x005F32E0`)
and played as fire-and-forget on the twin. The swing ids are therefore **data-driven, not
immediates in the binary**, so their exact numeric values are NEEDS-LIVE (capture them by
hooking `AddFireForgetAnimation`, section 4). `AddFireForgetAnimation` itself special-cases
ids `0x2745,0x2746,0x2747,0x2748,0x27A3` (VERIFIED early-branch) — likely the melee
swing/parry family.

### 2.4 Fire-and-forget id constants recovered statically (VERIFIED immediates)

Whitelist candidates (exclude idle/walk/run/death — handled elsewhere). Values are `ushort`,
10000-based:

| Id | Dec | Meaning | Confidence |
| --- | --- | --- | --- |
| `0x2806` | 10246 | **draw / ready weapon** ("x") | VERIFIED (PlayDrawAnimation) |
| `0x2745`–`0x2748`, `0x27A3` | 10053–10056, 10147 | melee swing / parry family (special-cased in `AddFireForgetAnimation`) | INFERRED |
| `0x2719` | 10009 | combat pause/ready (in the sync handler's "state" skip-set; 8 sites) | INFERRED |
| `0x271B`,`0x271C`,`0x271E` | 10011/12/14 | combat-ready stances (looping, from `ResolveMeleeAnimations`) | VERIFIED writes |
| `0x272E`,`0x2736`–`0x273A`,`0x27AA`,`0x27AB` | | gesture/dialog/taunt family (pushed near `0x0061xxxx` client anim code) | INFERRED |
| `0x2716`,`0x2718`,`0x27AC` | 10006/8/10156 | death (already excluded) | VERIFIED |

The taunt/flourish exact id is NEEDS-LIVE (also 2da-driven). Practical rule below (4.4):
**do not hardcode a swing/flourish list — mirror whatever fire-forget id the local twin is
actually handed.** The one id worth hardcoding is `0x2806` (draw), because it is engine
literal.

---

## 3. Applying a played animation to the phantom

The phantom is a server `CSWSCreature` we own on the other instance; single-player KOTOR
runs client and server in one process, so the phantom's own twin (`[phantom+0x224]`) renders
it. Two VERIFIED ways to make it play a fire-and-forget:

### 3.1 Reuse `SetAnimation` (recommended default; identical to movement path)

`vtable+0x7C` → `CSWSCreature::SetAnimation` (`0x004F0D70`), `thiscall(this, int anim)`,
4 stack bytes. Sets `+0xD4`, raises the animation dirty bit, and the same-frame client
update calls **`CSWCCreature::ClientServerSyncHandler_Animation` (`0x0060C100`)**, which is
the proof this is sufficient (VERIFIED disassembly):

```
recv anim (esi); twin->+0x4C = arg2
if anim in {death/state set 0x2716,0x2718,0x2719,0x271B,0x271C,0x271E,0x277D,0x27AC}: return
if anim == GetCurrentAnimation(twin /*+0x48*/): return          // already playing
if AnimationFireAndForget(twin, anim):   twin->vt[0xA0](anim, 0) // AddFireForgetAnimation
else:                                    twin->vt[0x9C](anim, 0) // CSWCObject::SetAnimation (looping)
```

So a fire-forget id set on the server is dispatched to `AddFireForgetAnimation` on the twin
— exactly the weapon-draw path. This is the **least-code option** (the plugin already calls
`SetAnimation` for gait) and it is VERIFIED end to end.

Caveat (the biggest risk, section 0): `SetAnimation` only re-dirties on a *value change*
(`0x004F0F33`: `cmp [edi+0xD4], esi; je skip-dirty`). To replay an identical consecutive
fire-forget, first set `+0xD4` to a different id (e.g. idle `0x2710`) that frame, then the
target — or use 3.2, which does not have this problem.

### 3.2 Call the twin directly (recommended for reliable replay of F&F)

```
twin = *(void**)(phantom + 0x224);   require *(u32*)twin == 0x0074F5F8   // CSWCCreature vtable
AddFireForgetAnimation(twin, anim, NULL);     // vtable +0xA0 == 0x006183D0
```

`CSWCCreature::AddFireForgetAnimation` — `thiscall(this, uint anim, void* pOpts)`, **8 stack
bytes** (DB `param_size_bytes = 8`). The engine's own replication calls it as
`push 0 /*pOpts=NULL*/; push anim; call [twin_vtable+0xA0]` (VERIFIED at `0x0060C167`), and
the function has an explicit `pOpts == NULL` default branch (`0x00618433`). This queues the
animation **every call**, so identical consecutive plays work. It is the exact function the
engine uses, so the ABI is VERIFIED-safe (unlike a guessed 5-arg `InsertFireForgetAnimation`,
which is also usable but has 20 stack bytes / more args — prefer `AddFireForgetAnimation`).

Do **not** invent argument shapes for engine calls: only `SetAnimation` (1 arg) and
`AddFireForgetAnimation(anim, NULL)` (2 args) are used here, and both match observed call
sites exactly. Run them on the game thread (the existing `MainLoop` hook; see
`docs/re/engine-movement.md` §7.4 and `docs/re/frame-hook.md`).

### 3.3 Do you need both looping and fire-forget?

Yes, distinguish them. `SetAnimation`/the sync handler already route looping vs fire-forget
via `AnimationFireAndForget`. If you apply via 3.2 (twin `AddFireForgetAnimation`), that path
is fire-forget only — a *looping* animation (a held stance) must still go through
`SetAnimation` so it lands as `+0x48`/looping. Classify with
`AnimationFireAndForget(twin, anim)` (`0x0063C380`, `thiscall(this,u16)->bool`) or the
whitelist (4.4).

---

## 4. Recommended design

### 4.1 READ on the local player, each tick

Keep sending the existing movement `uint Animation` (server `+0xD4`) — it already covers
idle/walk/run and scripted `ActionPlayAnimation`. **Add a second, one-shot "played
animation" event** sourced from the client twin:

- **Primary (recommended): hook the two client play entry points on the local player and
  emit the `ushort` id they receive, edge-triggered.**
  - `CSWCCreature::PlayDrawAnimation` `0x00610A70` — arg0 is the id (`0x2806` for draw).
  - `CSWCCreature::AddFireForgetAnimation` `0x006183D0` — arg0 (`di`) is the id; catches
    swings/flourishes/gestures without a hardcoded list.
  This is race-free (fires exactly when the game plays it) and needs no id table. Filter to
  the local player's twin (`[playerServer+0x224]`), and skip ids already covered by `+0xD4`.
- **Fallback (no hooks), NEEDS-LIVE:** poll twin `+0x444` (draw pending, `!= 0xFFFF`) and
  twin `+0x5A` (fire-forget count); on a 0→N transition read the newest `+0x5C` entry's id.
  Fire-forgets are short-lived, so a 20 Hz poll will miss some — hooking is strongly
  preferred.

### 4.2 WIRE

Reuse the existing `uint` shape. Send the played id as a **discrete event** ("play anim X
once"), not a level/state, so the receiver fires it exactly once. Keep it separate from the
continuous movement `Animation` field so the two never fight.

### 4.3 APPLY on the phantom

- Looping/gait (idle/walk/run/stance): keep the current `SetAnimation` (vtable `+0x7C`)
  write — unchanged.
- One-shot played id (draw/swing/flourish/gesture): call
  **`AddFireForgetAnimation(twin, id, NULL)`** (vtable `+0xA0`, 3.2) on the phantom's twin.
  This replays reliably and matches the engine's own path. (Or `SetAnimation(phantom, id)`
  with the idle-bounce workaround if you prefer a single code path.)

### 4.4 WHITELIST (what to mirror as a played animation)

Mirror a fire-forget id iff `AnimationFireAndForget(twin, id)` returns true **and** the id is
not one already handled as movement/state/death. Explicit excludes: `0x2710`/`0x2711` (idle),
`0x2712`/`0x2713`/`0x2714` (walk/moving/run), `0x276D`/`0x276E`/`0x2795` (injured/variant
walk-run), `0x2716`/`0x2718`/`0x27AC` (death), the stance set `0x2719`/`0x271B`/`0x271C`/
`0x271E`. Explicit include (engine literal): `0x2806` (draw). Everything else the twin is
handed is data-driven and safe to mirror as-is — which is why the hook-based read (4.1) that
just forwards the id beats maintaining a numeric table.

### 4.5 Desync / looping risks

- **Replay of identical F&F:** `SetAnimation` won't re-dirty an unchanged `+0xD4` (§3.1).
  Use `AddFireForgetAnimation` (§3.2) or bounce through idle.
- **Fire-forget vs looping confusion:** applying a looping id via `AddFireForgetAnimation`,
  or a one-shot id via a *held* looping write, leaves the phantom stuck or spamming.
  Classify with `AnimationFireAndForget` (§3.3/§4.4).
- **Duration / speed:** `PlayDrawAnimation` uses 1500 ms @ 1.0×; `AddFireForgetAnimation`
  with `pOpts=NULL` uses the engine default (`ebp=0x1F4`=500 ms seen in the prologue). If a
  mirrored animation looks too short/long, that default is the knob (NEEDS-LIVE tuning).
- **Off-screen phantom:** animation dirty replication rides the same client-update pass as
  position; the movement doc (§6.3) notes visibility gating (`+0x1F8`) affects the AI walk
  driver. If a played animation fails to show while the phantom is behind the camera, apply
  via the direct twin call (§3.2), which does not depend on the server dirty/replication
  path. NEEDS-LIVE.

---

## 5. Address / offset quick reference (this build, VA @ base 0x00400000)

Functions:

| Address | Function | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004F0D70` | `CSWSCreature::SetAnimation` (vtable `+0x7C`) | thiscall, 4 | writes `+0xD4`, dirty-on-change |
| `0x004CCF60` | `CSWSObject::SetAnimation` (base) | thiscall, 4 | `+0xD4 = anim; +0x1FC |= 1` |
| `0x0060C100` | `CSWCCreature::ClientServerSyncHandler_Animation` | thiscall, 8 | replays `+0xD4` on twin |
| `0x0063C380` | `CSWCObject::AnimationFireAndForget` | thiscall, 4 | **predicate** (is id F&F?) |
| `0x006183D0` | `CSWCCreature::AddFireForgetAnimation` (twin vtable `+0xA0`) | thiscall, 8 | apply F&F: `(twin, anim, NULL)` |
| `0x0063E930` | `CSWCObject::SetAnimation` (twin vtable `+0x9C`) | thiscall, 12 | apply looping on twin |
| `0x0063C060` | `CSWCObject::GetCurrentAnimation` | thiscall, 0 | returns `[twin+0x48]` |
| `0x0063C610` | `CSWCObject::GetTopFireForgetAnimation` | thiscall, 16 | top of `+0x5C` queue |
| `0x0063C760` | `CSWCObject::InsertFireForgetAnimation` | thiscall, 20 | low-level queue push |
| `0x00610A70` | `CSWCCreature::PlayDrawAnimation` | thiscall, 4 | client draw ("x"), arg0=id |
| `0x0061A980` | `CSWCCreature::AnimationUpdate` | thiscall, 0 | per-frame drain of `+0x5C`/`+0x444` |
| `0x0057D080` | `CSWSObject::AIActionPlayAnimation` | thiscall, 4 | server NWScript anim (→`+0xD4`) |
| `0x00540550` | `CSWVirtualMachineCommands::ExecuteCommandPlayAnimation` | thiscall, 8 | NWScript `PlayAnimation` |
| `0x005B7470` | `CSWSCreature::ResolveMeleeAnimations` | thiscall | server stance ids |
| `0x005F32E0` | `CClientExoAppInternal::GetClientMeleeAnimation` | thiscall | 2da-driven swing id |

Offsets:

| Where | Offset | Meaning |
| --- | --- | --- |
| `CSWSCreature`/`CSWSObject` | `+0xD4` | server current animation (int) |
| " | `+0x1FC` | dirty-bit array; bit0=animation |
| " | `+0x224` | client twin `CSWCObject*` |
| " | `+0x4C4` | ambient_animation_state (idle remap only) |
| twin `CSWCCreature` | `+0x48` | current looping animation (u16) |
| " | `+0x5A` / `+0x5C` / `+0x60` | F&F queue count / ptr / capacity |
| " | `+0x444` | pending draw id (`0xFFFF`=none) |
| " | `+0x68` | `anim_base` (rendered model anim) |

Vtables: server `CSWSCreature` `0x007470C8` (slot `+0x7C` = `SetAnimation`); client
`CSWCCreature` `0x0074F5F8` (slot `+0x9C` = `CSWCObject::SetAnimation`, slot `+0xA0` =
`AddFireForgetAnimation`).

Animation id space: 10000-based (`0x2710`). Shared by `+0xD4`, the twin F&F queue and every
`SetAnimation`. Draw = `0x2806` (VERIFIED); swing/flourish/taunt ids are 2da-driven
(NEEDS-LIVE — capture by hooking, do not hardcode).
