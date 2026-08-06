# Animations don't replicate, and the minimap won't show a peer (research 2026-08-01)

> **Status 2026-08-06: both bugs are closed and this document is now history plus one live
> correction.** The animation half shipped in full — the fire-and-forget hook, the overlay
> arm, and the classify-then-dispatch apply — and §"Apply" below is WRONG on one point that
> only the running game could settle: see *Live correction* at the end of this header. The
> minimap half stands as written: the mechanism cannot do it, and the M-screen pin is the
> answer. `docs/playtest-bugs.md` is retired; `Handoff.md` §0i has the outcomes.
>
> ### Live correction — the overlay setter is reached through an adjustor thunk
>
> §"Apply" says `ab->vt[0x44](row, 1)` is `CSWCAnimBase::SetOverlayAnimation_2 0x006A03C0`.
> On a real creature it is not. Measured on a live phantom, the animbase at `twin+0x68`
> carries vtable **0x00754AE8**, whose `+0x44` holds
> `sub ecx,[ecx-4]; jmp 0x00699420` — the `CSWCAnimBaseHeadWield` override behind an
> **adjustor thunk**, because that pointer is a secondary base. Calling 0x006A03C0 directly
> would hand it an unadjusted `this`. **Always call the slot.** `+0xE0` on the same vtable
> *is* `ServerToClientAnimation 0x0069F650` as documented, and since doors, placeables,
> triggers, projectiles and visual effects all override it, matching that slot is a sound
> "this is a creature's animbase" test before calling the other one.
>
> Also confirmed live, on a phantom: `0x27AD` reads **overlay 1, looping 0** through the
> engine's own predicates, `0x2806` is fire-and-forget, and `0x2712` (walk) is in neither
> class and must be dropped rather than queued.

Two unrelated playtest bugs — **#4 animations do not replicate**
and **#8 the minimap does not show both players** — answered statically against
`swkotor.exe` (GOG retail 1.0.3, base `0x00400000`, no ASLR, disk == memory) with
`tools/disasm.py`, `tools/xrefs.py`, the sha256-matched symbol DB and `tools/kotorfiles.py`
against the shipped 2DAs. Nothing was written and no game function was called.

Supersedes two claims in `docs/research/animation-sync.md` §1.2 and sharpens the one-line
minimap verdict in `docs/research/map-presence.md`. Corrections are called out inline.

Tag key: **VERIFIED** = read out of this binary (instruction stream or shipped 2DA) ·
**INFERRED** = my reading of what that code means · **NEEDS-LIVE** = cannot be closed
without the running game.

---

## Headline verdicts

### Bug A — animations

**The apply side is fine; the read side cannot see the two things the playtest tried.**

`X` is not a weapon draw. It is keymap `action242` **"Flourish"** (VERIFIED in
`keymap.2da` *and* in the input dispatch table), and it runs
`CClientExoAppInternal::PlayerFlourishWeapons 0x005F4460`, a **purely client-side**
function that branches on whether the player is moving:

* **standing still** → `InsertFireForgetAnimation(twin, 0x27AE, 1500 ms, 1.0×)`
  (`0x005F46E2`). This *does* land in `twin+0x48` while it plays — so a poll can catch it,
  for ~1.5 s.
* **moving or turning** → `animBase->SetOverlayAnimation_2(ServerToClientAnimation(0x27AD), 1)`
  (`0x005F4694`). This writes **no object field at all** — not `+0xD4`, not `twin+0x48`,
  not the fire-and-forget queue. It goes straight into the model's overlay slot. **No field
  read of any kind can ever observe it.**

So roughly half of all X presses are unobservable by construction and the other half are
observable only inside a short window. Nothing on the server ever changes (the one server
call the flourish makes is `CSWSCreature::SetCombatState(1,2) 0x004F2610`, and only for
weapon-model classes 2–4).

Item use (a stim) is a different miss: the animation is picked *server-side* in
`CSWSCreature::AIActionItemCastSpell 0x0050F170` from the item's spell row —
**`0x2756` = `animations.2da` row 37 `inject`** (VERIFIED both ends) — but it is gated on
that spell's `MODELTYPE` column being `"B"` or `"F"`, and it is carried by the **combat
round**, not written directly to `+0xD4` by the action. The *visible* part of a stim is
mostly a **visual effect** attached to the creature (`CSWCObject::AddVisualEffect
0x0063D970`), which is a completely separate replication channel the project does not touch.

Two smaller defects found while verifying:

1. `KotorGameBridge.ReadPlayedAnimation` reads **`TryReadUInt32(twin + 0x48)`**. The field is
   a **`ushort`** (`GetCurrentAnimation 0x0063C060` = `mov ax,[ecx+0x48]`); `+0x4A` is a
   separate `ushort` (`CSWCObject::QueuedCallback 0x0063D330` swaps it into `+0x48`). It is
   normally 0, so this usually works — but it is a live field and the wire value must be
   masked to 16 bits.
2. `PhantomController.MirrorAnimation` pushes **any** non-gait id into
   `AddFireForgetAnimation`, and `CSWCObject::InsertFireForgetAnimation 0x0063C760`
   **discards** the `AnimationFireAndForget` result it computes (VERIFIED at `0x0063C783`:
   `call 0x63c380` then `mov eax,[0x7a39fc]` — return value dropped). So a *looping* or
   *overlay* id can be queued, and `AnimateFireAndForget` will then park it in `twin+0x48`
   with no expiry path — a phantom stuck in one pose forever. See the crash-risk register.

The recipe (§A5) is: stop polling a field and **hook the one client choke point every
fire-and-forget passes through** — `CSWCObject::InsertFireForgetAnimation 0x0063C760`,
15 call sites, *all* of them (draw, flourish, swings, item use, doors, placeables) — plus
`PlayerFlourishWeapons` for the overlay variant, and mirror the id as a one-shot event.

### Bug B — the minimap

**The minimap cannot show a peer with the current approach, and never could.**
`CSWGuiMainInterface::DrawMap 0x0068AB10` is 0x1C0 bytes long and draws exactly two things:
the area-map image, offset by `GetPartyMemberMapLocation(areaMap, **0**, &px, &py)`, and one
arrow control. **There is no loop over map notes and no loop over party members** — party
slot 0 is the only one it reads, and only to *centre the image*, so even hijacking a slot
adds nothing. This confirms the one-line claim in `map-presence.md` and makes it decisive:
the waypoint map-note surface only ever addressed the **M-screen** map
(`CSWGuiMapHider::Draw 0x006943D0`).

The good news is the M-screen pin should already work, and the missing note *text* is not
why: the pin is a `CSWGuiButton` with a fixed icon texture built lazily in the draw loop
(`0x006947AB`); the text is only used as the label of the **selected** note, and
`CSWGuiInGameMap::SetMapNote 0x006929B0` has an explicit *empty string* branch
(`0x006929EF`) that cannot crash. Membership is snapshotted, but by `InitializeMapNotes
0x00692C70` on **every** map-panel open, so a waypoint created later joins on the next open.

The engine *does* have an other-party-members-on-a-map mechanism — `CSWSAreaMap`'s **3-slot**
party arrays at `+0x28` (map px X), `+0x34` (map px Y), `+0x40` (rotation), filled every
frame by `CServerExoAppInternal::UpdateMapData 0x004B4E80` from `CSWParty` — and the
**M-screen map draws slots `1 … partySize-1` as dots** (`0x00694A20` loop). But
`UpdateMapData` overwrites every slot below `partySize` each frame and the M-map's loop is
bounded by the same `partySize`, so a peer cannot borrow a slot without faking party
membership. Rejected.

Realistic options, in order: **(1)** verify the existing M-screen waypoint pin actually
appears (it should — nothing in the draw path is missing); **(2)** if the minimap is
genuinely wanted, detour `DrawMap` and blit one extra marker (§B4) — ~80 lines on the render
path, moderate risk; **(3)** do nothing and treat the M-screen as the map surface.

---

# BUG A — animations do not replicate

## A1. The animation fields, and which one the player actually sees

### Server: `CSWSObject + 0xD4` (VERIFIED)

DB `CSWSObject.animation = +0xD4 (int)`. Written only by `CSWSObject::SetAnimation
0x004CCF60` / `CSWSCreature::SetAnimation 0x004F0D70` (vtable slot `+0x7C`), which also sets
dirty bit 0 in the array at `+0x1FC`. `CSWSObject + 0xD8` is a companion **float** — the
animation *speed* — pushed alongside `+0xD4` by the sync pass (§A2). The plugin's use of
`+0xD4` is correct as far as it goes.

### Client twin: `CSWCObject + 0x48` is the animation **being rendered** (VERIFIED)

This corrects `animation-sync.md` §1.2, which said fire-and-forget animations "never change
`+0x48`". They do:

| Twin offset | Type | Meaning | Evidence |
| --- | --- | --- | --- |
| `+0x48` | **u16** | **current animation actually playing** — looping *or* fire-and-forget | `GetCurrentAnimation 0x0063C060` = `mov ax,[ecx+0x48]`; written by `CSWCObject::SetAnimation` (`0x0063EC30`, `0x0063F2DE`) **and by `CSWCCreature::AnimateFireAndForget` at `0x0061A7D1`** |
| `+0x4A` | u16 | queued-callback animation (swapped into `+0x48` by `CSWCObject::QueuedCallback 0x0063D330` at `0x0063D3C8`); 0 in the ctor | VERIFIED |
| `+0x4C` | float | **animation speed**, set from server `+0xD8` by the sync handler (`0x0060C112`) and used by `AddFireForgetAnimation` as the length divisor (`0x00618975`) | VERIFIED |
| `+0x58` | u16 | `looping_animation` — what the creature reverts to when a one-shot expires | `SetLoopingAnimation 0x0063D830` (`0x0063D843`), read back at `0x0061A6C7` |
| `+0x5A`/`+0x5C`/`+0x60` | u16/ptr/u8 | fire-and-forget queue count / array (`0x1C`-byte entries) / capacity | VERIFIED |
| `+0x64` | int | remaining time on the playing one-shot | `0x0061A688` |
| `+0x68` | ptr | `CSWCAnimBase*` — the model animation actually rendering | DB + `0x0063C070` |
| `+0x444` | u16 | deferred draw id, `0xFFFF` = none | VERIFIED |

`CSWCCreature::AnimateFireAndForget 0x0061A670`, per frame:

```
if AnimationFireAndForget([+0x48]) == 1:          # a one-shot is playing
    [+0x64] -= [+0x44]
    if [+0x64] < 0:                               # expired
        ... ; [twin]->vt[0x9C]( GetLoopingAnimation() /* +0x58 */, 0 )   # revert
if AnimationLooping([+0x48]) == 1:                # only start from a looping pose
    if GetTopFireForgetAnimation(...) == 1:
        TestAndReEquipweapons(old=[+0x48], new)
        [+0x48] = new                             # 0x0061A7D1
```

**So `twin+0x48` is authoritative for what the player sees** — it carries the draw, the
flourish, the swing, for as long as each plays — and the plugin's field choice was not
wrong in principle. The two failure modes are (a) the 32-bit read (§0), and (b) the
animations that never reach any field at all (§A4).

Note the guard: a new one-shot only starts when `+0x48` currently holds a **looping** id. An
id that is neither looping nor fire-and-forget parked in `+0x48` wedges the creature (§risk).

### Value space (VERIFIED)

One space, 10000-based (`0x2710`), shared by `+0xD4`, `twin+0x48`, `twin+0x58`, the F&F
queue and every `SetAnimation`. It is **not** an `animations.2da` row; the model layer
converts with `CSWCAnimBase::ServerToClientAnimation 0x0069F650`
(`thiscall(this, u16) -> int row`), keyed on the model's weapon class byte `animBase+0xC5`.
Emulating that dispatch against the shipped `animations.2da` gives (VERIFIED, weapon class 2
unless noted):

| Const | Dec | 2DA row | Name | Class |
| --- | --- | --- | --- | --- |
| `0x2710` | 10000 | 6 | `pause1` | looping |
| `0x2711` | 10001 | (model-dependent) | idle 2 | looping |
| `0x2719` | 10009 | 300 | `attack` | fire-forget |
| `0x271B` / `0x271C` / `0x271E` | 10011/12/14 | 302 / 301 / 303 | `dodge` / `parry` / `damage` | fire-forget |
| `0x2745`–`0x2748` | 10053–56 | 11/10/12/13 | `hturnl`,`hturnr`,`pausesh`,`pausebrd` | mixed |
| **`0x2756`** | **10070** | **37** | **`inject`** (stim/medpac) | fire-forget, `hideequippeditems=1` |
| `0x2791` / `0x2792` | 10129/30 | 57 / 58 | `throwgren`, `throwgren1` | fire-forget |
| `0x2798` | 10136 | 38 | `activate` | fire-forget |
| **`0x27AD`** | **10157** | 377 / 378 / 376 (class 2/3/4) | **`G2F1`/`G3F1`/`G4F1`** — flourish **OVERLAY** | `overlay=1` |
| **`0x27AE`** | **10158** | 132 / 173 / 214 | **`g2w1`/`g3w1`/`g4w1`** — flourish, full body | fire-forget |
| `0x27AF` / `0x27B0` | 10159/60 | 34 / 35 | `treatinj`, `treatinjlp` | ff / looping |
| **`0x2806`** | **10246** | 132 / 173 / 214 | same rows as `0x27AE` — **draw == flourish animation** | fire-forget |

(That last row is a nice sanity check: "draw weapon" and "flourish" are literally the same
model animation in KOTOR.)

`CSWCObject::AnimationFireAndForget 0x0063C380` is the classifier: for object types 5/9 it
range-checks the raw id, otherwise it converts through `ServerToClientAnimation` and reads
the `fireforget` column of `animations.2da` (`0x00413510` against the table at `[0x7A214C]`).
`AnimationLooping 0x0063C300` and `AnimationOverlay 0x0063C410` are its siblings.

## A2. How the server makes a remote client render an animation

**In this single-process build there is no message.** `CServerAIMaster::UpdateState
0x004B0B70` — the per-frame server pass — pushes dirty object state straight into the twin
(VERIFIED, `0x004B10A8`–`0x004B115C`):

```
client = CSWSObject::GetClientObject(server)          # 0x004CC2B0
if CSWSObject::IsDirtyFlagSet(server, 0):             # 0x004CC220 -> [obj + (bit>>5)*4 + 0x1FC]
    client->vt[0x134]( [server+0xD4], [server+0xD8] ) # ClientServerSyncHandler_Animation
    CSWSObject::ClearDirtyFlag(server, 0)             # 0x004CC250
... vt[0x12C] = position (bit 1), vt[0x130] = orientation (bit 2)
```

`CSWCCreature::ClientServerSyncHandler_Animation 0x0060C100` (vtable slot `+0x134`):

```
twin->+0x4C = speed
if anim in {0x2716,0x2718,0x2719,0x271B,0x271C,0x271E,0x277D,0x27AC}: return   # death + combat set
if anim == GetCurrentAnimation(twin /* +0x48 */):     return                   # already playing
if AnimationFireAndForget(twin, anim):  twin->vt[0xA0](anim, 0)   # AddFireForgetAnimation
else:                                   twin->vt[0x9C](anim, 0)   # CSWCObject::SetAnimation
```

Two consequences that matter:

* **Writing `+0xD4` (through `SetAnimation`) is genuinely sufficient** — same frame, in
  process, no networking. VERIFIED end to end.
* **Combat animations are deliberately excluded from that path** — `0x2719` (attack),
  `0x271B` (dodge), `0x271C` (parry), `0x271E` (damage), `0x277D` — because the client
  resolves swings itself from `combatanimations.2da`
  (`CClientExoAppInternal::GetClientMeleeAnimation 0x005F32E0`). Mirroring a swing therefore
  **requires the twin call**; `SetAnimation` will silently drop it.

Also VERIFIED: `SetAnimation` only re-dirties on a *value change* (`0x004F0F33`
`cmp [edi+0xD4], esi; je`), so two identical consecutive one-shots do not replay through
`+0xD4`.

## A3. Item use (a stim): what actually runs

`CSWSCreature::AIActionItemCastSpell 0x0050F170` is the server action behind
`SendPlayerToServerInput_UseItem 0x00677BD0`. VERIFIED shape:

```
prop  = CSWSItem::GetActiveProperty(item)                      # 0x00553960
spell = CSWSpellArray::GetSpell(prop)                          # 0x0059B6D0
C2DA::GetCExoStringEntry_2(spells2da, spellRow=[cre+0xA60], "MODELTYPE", &s)   # 0x00413270,
                                                                # string "MODELTYPE" @0x747C2C
useReal = (s == "B") || (s == "F")                             # 0x0050F531 / 0x0050F548
switch (baseItem->+0xAC - 6)          # base item type, 0x2A cases, table @0x50FB18/0x50FB30
  ... anim = useReal ? 0x2711+0x45 (= 0x2756 "inject")   : 0x2711        # 0x0050F59A
      anim = useReal ? 0x2711+0x87 (= 0x2798 "activate") : 0x2711        # 0x0050F5BA / 0x0050F676
      anim = grenade in/out of range ? 0x2791 : 0x2792                   # 0x0050F627 / 0x0050F631
CSWSCreature::SetCombatState(1, 2)                              # 0x004F2610
CSWSCombatRound::StartCombatRound / SetSpellCastRound / SetPauseTimer  # 0x004D5F70 / 0x4D28B0 / 0x4D2920
CSWSObject::SpellCastAndImpact                                  # 0x004CDF50 — applies the effects
```

Answers to the question as asked:

* **It is a creature animation** — `0x2756` (`inject`), a fire-and-forget with
  `hideequippeditems=1` (the engine stows the weapon for it). VERIFIED constant, VERIFIED
  2DA row.
* **But it is gated**, on `spells.2da.MODELTYPE ∈ {B, F}` for that item's active property.
  An item whose row does not match plays plain idle `0x2711` — i.e. *some* stims may
  legitimately have no animation at all. Which rows the stims in play use is **NEEDS-LIVE**
  (or offline via `tools/kotorfiles.py 2da spells.2da`).
* **The glow is not an animation.** `SpellCastAndImpact` applies effects; effects carry
  visual effects, which reach the client as `CSWCObject::AddVisualEffect 0x0063D970` /
  `CSWCVisualEffect`, replicated separately (`SendServerToPlayerGameObjUpdateVisEffect
  0x00569C40`). **Replicating "stim used" visibly means replicating the VFX, not just the
  animation** — a second channel, out of scope for this fix but the reason a mirrored
  `inject` will still look thinner than the real thing.
* The animation is carried by the **combat round**, not written straight to `+0xD4` by the
  action — so whether it ever appears in `+0xD4` is INFERRED. It *will* appear in
  `twin+0x48` when it plays, and it *will* be seen by the hook in §A5, which is why the
  recipe does not depend on resolving this.

## A4. The X key

**VERIFIED chain, end to end:**

1. `keymap.2da` row `action242`: `name = Flourish`, `character = X`, `language0 = 74`,
   page 1, `icpc = 1`.
2. `CClientExoAppInternal::HandleInputEvent 0x00621210` dispatches event ids `0x19…0x108`
   through `idx = byte[0x006221FC + (id-0x19)]; jmp dword[0x00622154 + idx*4]`. For id
   **242** that resolves to **`0x00621C21`** (computed from the shipped tables), whose body
   is `call 0x005EDE90` → `CClientExoApp::PlayerFlourishWeapons` → jmp
   `CClientExoAppInternal::PlayerFlourishWeapons 0x005F4460`.
3. `PlayerFlourishWeapons` (VERIFIED disassembly):

```
cre    = GetCreatureByGameObjectID(player)                     # cached at +0x3BC/+0x3C0
CSWCCreature::ComputeWeaponOverlays(cre, 0, 1)                 # 0x00611700
srv    = CSWCCreature::GetServerCreature(cre)                  # 0x0060FB20
if srv && !dead && !deadTemp:
    cls = [ GetClientCreature(srv)->+0x68 /*animBase*/ + 0xC5 ]      # weapon model class
    if 2 <= cls <= 4:
        CSWCItem::PowerItem(rightHand, 1,0,1,0)                # 0x00647130  (ignite sabre)
        CSWCItem::PowerItem(leftHand,  1,0,1,0)
        CSWSCreature::SetCombatState(srv, 1, 2)                # 0x004F2610  <- ONLY server write
ctl = internal->+0x2A0                                          # CSWPlayerControl*
if ctl && (ctl->vt[0xC]() != 0.0f || ctl->vt[4]() != 0.0f):     # moving / turning  (INFERRED names)
    ab = cre->+0x68
    ab->vt[0x44]( ab->vt[0xE0](0x27AD), 1 )                     # SetOverlayAnimation_2(ServerToClientAnimation(0x27AD), 1)
else:
    CSWCObject::InsertFireForgetAnimation(cre, 0x27AE, 0x5DC, 1.0f, 0, 0)   # 0x0063C760
```

`[0x0073D700]` is `0.0f` (VERIFIED), so the branch really is "is the player moving".

**Which field or call reflects it:** in the standing case, `twin+0x48` for ~1.5 s (once the
queue drains next frame). In the moving case, **nothing** — `SetOverlayAnimation_2` writes
the model's overlay slot inside `CSWCAnimBase`, not any replicated field. `SetCombatState`
is the only server-visible side effect and it says nothing about the flourish.

## A5. THE RECIPE

### Read — hook, do not poll

`CSWCObject::InsertFireForgetAnimation 0x0063C760`, `thiscall(this, u16 anim, int durMs,
float speed, int, int)`, **20 stack bytes**, is the **single choke point for every
fire-and-forget on the client**: `xrefs` gives 15 direct call sites and all of them funnel
here —

| Site | Producer |
| --- | --- |
| `0x005F46E2` | flourish (X), full-body `0x27AE` |
| `0x00610AA9` | `PlayDrawAnimation` (weapon draw `0x2806`) |
| `0x00618521`, `0x006185C1`, `0x00618672`, `0x006186DD`, `0x006187FB`, `0x0061888A`, `0x0061895F`, `0x006189BC`, `0x00618A09` | `CSWCCreature::AddFireForgetAnimation` — swings, parries, item use, everything routed from the server |
| `0x0063D962` | `CSWCObject::AddFireForgetAnimation` (generic) |
| `0x0068264F`, `0x00683E45`, `0x006911D3` | placeables / doors / triggers (filter these out by `this`) |

Hook it, filter to the local player's twin (`*(void**)(playerServer + 0x224)`), and emit
`(u16)anim` as an **edge event**. That is race-free, needs no id table, and picks up the
2DA-driven swing ids for free.

For the overlay flourish, hook either `CClientExoAppInternal::PlayerFlourishWeapons
0x005F4460` (`thiscall`, 0 args — simplest, and you already know it means `0x27AD`) or
`CSWCAnimBase::SetOverlayAnimation_2 0x006A03C0` (`thiscall`, 8 bytes) if you want every
overlay. Send it as a distinct "overlay" event so the receiver applies it the same way.

Keep the existing per-tick `+0xD4` read for **gait/state** (idle, walk, run, scripted
`ActionPlayAnimation`) — but fix the read to
`(u16)*(ushort*)(twin + 0x48)`, or drop it and read the server `+0xD4` directly. Do not
send a `uint` sampled at `twin+0x48`.

### Apply — classify first, then use the matching call

```c
twin = *(void**)(phantom + 0x224);
if (twin == 0 || *(uint*)twin != 0x0074F5F8) return;      // CSWCCreature vtable guard

switch (kind) {
case ONESHOT:                                             // draw, flourish, swing, inject
    // vtable +0xA0 == CSWCCreature::AddFireForgetAnimation 0x006183D0
    //   thiscall(this, uint anim, void* pOpts)   8 stack bytes; pOpts=NULL is handled
    //   (0x00618433 -> 0x00618975: duration = GetAnimationLength(anim) / twin->+0x4C)
    AddFireForgetAnimation(twin, anim, NULL);
    break;

case OVERLAY:                                             // the moving flourish, id 0x27AD
    ab = *(void**)(twin + 0x68);                          // CSWCAnimBase*
    if (!ab) break;
    row = ab->vt[0xE0](anim);                             // ServerToClientAnimation 0x0069F650, thiscall, 4
    ab->vt[0x44](row, 1);                                 // SetOverlayAnimation_2 0x006A03C0, thiscall, 8
    break;

case LOOPING:                                             // stance / gait
    SetAnimation(phantom, anim);                          // server vtable +0x7C, 0x004F0D70
    break;
}
```

Classify with the engine's own predicates on the phantom's twin, never with a hardcoded list:

* `CSWCObject::AnimationFireAndForget 0x0063C380` — `thiscall(this, u16) -> bool`, 4 bytes
* `CSWCObject::AnimationLooping 0x0063C300` — same shape
* `CSWCObject::AnimationOverlay 0x0063C410` — same shape

**Can writing a raw field ever work?** Partly, and only for one of the three classes:

* looping/gait — **yes**, `+0xD4` + dirty bit 0 is the engine's own path (§A2);
* one-shots — **yes in principle** (the sync handler dispatches them to
  `AddFireForgetAnimation`), **but** it drops identical consecutive values and it explicitly
  skips the whole combat set `{0x2719, 0x271B, 0x271C, 0x271E, 0x277D}`. Use the twin call.
* overlays — **no. An engine call is required.** There is no field.

So: an engine call is required, and specifically the *right* engine call per class.

### Wire

Three shapes, not one `uint`:

* `u16 GaitAnimation` — continuous, as today (from `+0xD4`), masked to 16 bits.
* `{u16 anim, u8 seq}` **one-shot event** — fires exactly once on receipt; the sequence byte
  makes a repeated identical flourish replay.
* `{u16 anim}` **overlay event** — same, applied through the animbase path.

### Sanity budget

Everything above is a hook + three small dispatch arms. No new engine object, no allocation,
all on the main thread (the existing `MainLoop` hook).

---

# BUG B — the minimap does not show the other player

## B1. What the minimap actually draws (VERIFIED — and it is not map notes)

`CSWGuiMainInterface::DrawMap 0x0068AB10`, `thiscall(this, void*)`, whole function read:

```
if (client && !(client->+0x14 & 8))               goto disable        # map-enabled flag
areaMap = GetModule()->+0x218
if (!areaMap || areaMap->+4 == 0)                 goto disable        # no map data
if (mapTextureName changed) InitializeMap()                            # 0x00687E40
CSWSAreaMap::GetPartyMemberMapLocation(areaMap, 0, &px, &py)           # 0x005791B0  <-- INDEX 0 ONLY
offX = (this->+0x6088 / 2) - px * scale
offY = (this->+0x608C / 2) - py * scale
mapImageControl(this + 0x5E00)->vt[4](&offset)                         # scroll the image
this->+0x5FB8 = CSWSAreaMap::GetMapRotateCCWFromWorldOrientation(...)  # 0x00578ED0 — arrow angle
AurGUIStartLayer(); AurGUISetupViewport(this->+0x6080, this->+0x6084,
                                        this->+0x6088, this->+0x6088, 0x00833910, 0, this->+0x4C)
    mapImageControl->vt[0x38](arg)                                     # draw the map image
    UpdateAndDrawFogOfWar(&offset)                                     # 0x00688100
    arrowControl(this + 0x5F40)->vt[0x38](arg)                         # draw ONE arrow
AurGUICloseViewport(); AurGUIStopLayer()
this->+0x60DC |= 2
```

**No map-note iteration. No party-member iteration. One arrow.** The answer to "does the
minimap render map notes at all" is a plain **no** — which means the waypoint approach could
never have shown anything there, and the log line `map marker: slot 0 pinned as 0x12F` is
telling the truth about a marker that the minimap was never going to consult.

`InitializeMap 0x00687E40` likewise creates a fixed set of controls, no marker array.

## B2. What the M-screen note draw requires (VERIFIED)

For completeness, because the same waypoint *is* the right object for the M-screen:

`CSWGuiMapHider::Draw 0x006943D0`, note loop from `0x006945EF`:

```
for id in list [hider+0x238]:
    obj = CServerExoApp::GetGameObject(id)                 # 0x004AE750
    wp  = obj->vt[0x58]()                    ; if (!wp)                     continue   # AsWaypoint
    if (wp->+0x22C == 0)                                                    continue   # MapNoteEnabled
    if (!CSWSAreaMap::IsWorldPointExplored(areaMap, *(Vector*)(wp+0x90)))   continue   # 0x00579210
    if (!CSWSAreaMap::GetMapPixelFromWorldCoord(areaMap, *(Vector*)(wp+0x90), &px,&py)) continue
    ... lazily build a CSWGuiButton (0x0041C0F0) with fixed icon resrefs, position it, draw
```

* **It never dereferences the note's `CExoLocString`.** Not setting `map_note` (`+0x230`) or
  `localized_name` (`+0x238`) cannot cause a null deref or a silent skip in the draw path.
  Confirmed by naming every call in the loop body: `operator_new`, `CSWGuiButton::Constructor`,
  `CExoArrayList::Add_2`, `CResRef` ctors, `CSWGuiTextParams`/`CSWGuiBorderParams` ctors,
  `CSWGuiControl::AddEvent`. No locstring accessor.
* The text *is* read in one place: `InitializeMapNotes 0x00692C70` auto-selects the first
  enabled+explored note and calls `CExoLocString::GetString_3 0x005EA130` on `wp+0x230`
  (`0x00692DC8`). That is a real member of the object we allocate (`0x240` bytes,
  default-constructed by `CSWSWaypoint::Constructor 0x005C7E70`), i.e. an *empty* locstring,
  not a null pointer — and the consumer `CSWGuiInGameMap::SetMapNote 0x006929B0` has an
  explicit empty-string branch at `0x006929EF`. Safe. Setting the text is a nice-to-have
  (the peer's name in the label), not a fix.
* **Membership is snapshotted per map-panel OPEN, not per area load.** `InitializeMapNotes`
  is called from `CSWGuiInGameMap::OnPanelAdded 0x00693650`; it rescans the area's object
  array (`CSWSArea+0x190`, count `+0x194`), keeping objects whose type byte matches
  `[0x0074666C]` and whose `+0x228` (`has_map_note`) is non-zero. So a waypoint created at
  any time joins the set the next time M is pressed. (VERIFIED — this is a *relaxation* of
  the "spawn it at area load" advice in `map-presence.md`.)

## B3. The engine's own other-party-members mechanism (VERIFIED)

`CSWSAreaMap` carries three party slots:

| Offset | Type | Meaning |
| --- | --- | --- |
| `+0x04` | int | map data present (0 ⇒ every accessor fails) |
| `+0x10` | int | map orientation mode (1/2/3 — selects one of three rotation biases) |
| `+0x28 + i*4` | int | party member *i* map-pixel **X**; `-1` = unset |
| `+0x34 + i*4` | int | party member *i* map-pixel **Y** |
| `+0x40 + i*4` | float | party member *i* map rotation (CCW) |

Accessors: `SetPartyMemberWorldLocation 0x005790C0` (`ret 0x10`, rejects `index >= 3`),
`SetPartyMemberWorldOrientation 0x00579130`, `GetPartyMemberMapLocation 0x005791B0`
(`ret 0xC`, returns 0 when the slot is `-1`), `GetPartyMemberMapOrientation 0x005791F0`.

Filled by exactly one caller — `CServerExoAppInternal::UpdateMapData 0x004B4E80`, per frame:

```
party = CClientExoApp::GetSWParty()                       # 0x005ED8B0 ; count at [party+0]
areaMap = GetModule()->+0x218
for i in 0 .. count-1:
    c = CSWParty::GetCharacter(party, i)                  # 0x006346C0
    CSWSAreaMap::SetWorldPointExplored(areaMap, c->+0x24, 1)     # 0x005792D0  (fog reveal)
    CSWSAreaMap::SetPartyMemberWorldLocation(areaMap, i, c->+0x24)
    CSWSAreaMap::SetPartyMemberWorldOrientation(areaMap, i, c->+0x30)
```

And the **M-screen** map draws them: `CSWGuiMapHider::Draw` at `0x00694A20` loops
`i = 1 … partySize-1`, calls `GetPartyMemberMapLocation(i, …)` and positions/draws the
control at `[hider+0x64]` for each; index **0** is then drawn separately as the rotated
player arrow at `[hider+0x60]` (`0x00694AAC` … `0x00694AFC`).

**This is the right-shaped surface but it cannot be borrowed.** `UpdateMapData` rewrites
every slot `< partySize` every frame, and the M-map's loop bound is that same `partySize`,
so writing slot 1 while soloing draws nothing and writing it in a full party is overwritten
within a frame. Making a peer appear this way means making it a real `CSWParty` member —
a much larger change than the pin, with its own AI/control consequences. **Rejected.**

## B4. THE RECIPE

### First: verify the pin you already have, on the M-screen

Nothing in `CSWGuiMapHider::Draw` is missing from `Odyssey.SpawnMapMarker`. Open the map
screen (M) with a peer connected and look for the pin. If it is there, bug #8 is really
"the *minimap* does not show it", and the cheap fix is to say so in the UI (or add the
peer's name via the locstring so the note label is meaningful). Two zero-risk additions
while you are there:

```c
// name the note so the selected-note label is not blank
CExoLocString::SetString(waypoint + 0x230, /* lang */ 0, peerName);   // NEEDS-LIVE: pick the
                                                                      // right CExoLocString setter
// keep the pin out of fog, and share fog reveal for free
CSWSAreaMap::SetWorldPointExplored(areaMap, peerPos, 1);              // 0x005792D0, thiscall, 16
```

`areaMap = *(void**)(CSWSModule + 0x218)`, module from
`CServerExoAppInternal::GetModule 0x004B14F0` (or the existing `GetModule 0x004AE6B0`).

### If the minimap is genuinely required: detour `DrawMap`

There is no data-only route. The only surface is the draw itself:

```
detour CSWGuiMainInterface::DrawMap 0x0068AB10   (thiscall, 4 stack bytes)
  call original(this, arg)                       // let it set up + draw the map, fog, arrow
  areaMap = GetModule()->+0x218 ; if (!areaMap || areaMap->+4 == 0) return
  if (!CSWSAreaMap::GetPartyMemberMapLocation(areaMap, 0, &lx, &ly)) return    // 0x005791B0
  for each peer:
      if (!CSWSAreaMap::GetMapPixelFromWorldCoord(areaMap, peerPos, &px, &py)) continue  // 0x00578E00
      // minimap is a square viewport at (this+0x6080, this+0x6084), side this+0x6088
      sx = this->+0x6080 + this->+0x6088/2 + (px - lx) * scale
      sy = this->+0x6084 + this->+0x608C/2 + (py - ly) * scale
      AurGUIStartLayer();                                                        // 0x004591B0
      AurGUISetupViewport(this->+0x6080, this->+0x6084,
                          this->+0x6088, this->+0x6088, 0x00833910, 0, this->+0x4C)  // 0x004592F0 cdecl, 7 args
        markerControl->vt[4](&rectAt(sx,sy)) ; markerControl->vt[0x38](arg)
      AurGUICloseViewport();  AurGUIStopLayer();                                 // 0x00459580 / 0x004592B0
```

`scale` is the float `DrawMap` keeps at `[esp+0xc]` while computing the image offset
(`0x0068ABC7`, `0x0068ABED`) — **NEEDS-LIVE**: read it in the detour, or calibrate once by
comparing two known world positions. `markerControl` is cheapest as a `CSWGuiButton`
(`0x0041C0F0`) built once with the map-note icon resref, exactly as `CSWGuiMapHider::Draw`
does at `0x006947AB`; do **not** construct GUI objects inside the draw call every frame.

Honest cost: ~80–120 lines, on the render path, in a detour of a function that runs every
frame with an open viewport. That is the "~200 lines, crash-risk" `map-presence.md`
deferred, and the estimate holds. It buys one dot.

### Recommendation

Ship (1) — confirm and polish the M-screen pin — and leave the minimap alone until the
higher-priority playtest bugs (#1 presentation, #7 pause deadlock) are closed. If a peer
must be visible in-world without opening the map, the **name plate over the phantom** is a
far cheaper answer than a minimap detour, and the plugin already writes twin names
(`docs/research/name-plates.md`).

---

## Crash-risk register

| Risk | Assessment |
| --- | --- |
| `AddFireForgetAnimation 0x006183D0` called on a non-`CSWCCreature` twin | **Real.** It touches creature-only fields (`+0x390`, `+0x398`, `+0x13C`, `UpdateAttackData 0x00617330`). `Odyssey.PlayFireForget` calls the address directly with no class check. Guard on `*(uint*)twin == 0x0074F5F8` before every call. |
| Queueing a **looping** or **overlay** id as fire-and-forget | **Real, and current behaviour.** `InsertFireForgetAnimation` computes `AnimationFireAndForget` and **discards the result** (`0x0063C783`), so anything gets queued; `AnimateFireAndForget` then parks it in `twin+0x48` where neither the expiry branch (needs F&F) nor the start branch (needs looping) applies — the phantom is stuck in that pose until something else writes `+0x48`. `MirrorAnimation`'s six-value exclusion list lets `0x2711`, `0x2713`, `0x276D`, `0x276E`, `0x2744`, the stance set and every overlay id through. **Classify with `AnimationFireAndForget 0x0063C380` before applying.** |
| Reading `uint` at `twin+0x48` | Field is `u16`; `+0x4A` is a live `u16` (queued-callback animation). Usually 0, so mostly benign — but a non-zero `+0x4A` puts a garbage id on the wire, which the receiver then queues (see previous row). Mask to 16 bits. |
| Hooking `InsertFireForgetAnimation 0x0063C760` | 15 call sites, `ret 0x14`, no SEH of its own beyond a standard frame; a trampoline must preserve `ecx` and all 5 stack args. Filter by `this` — doors/placeables/triggers call it too (`0x0068264F`, `0x00683E45`, `0x006911D3`). |
| Hooking `PlayerFlourishWeapons 0x005F4460` | `thiscall`, 0 args, single caller (`0x005EDE93` tail-jmp from `0x005EDE90`) — the safest hook in this document. It is client-only; it cannot desync the server. |
| `SetOverlayAnimation_2` on a phantom with no `animBase` | `twin+0x68` is null until the model loads. Null-check; `0x0063C380` itself null-checks `+0x68` (`0x0063C3B4`) but the overlay call does not. |
| Mirrored animation duration | With `pOpts=NULL`, `AddFireForgetAnimation` uses `GetAnimationLength(anim) / twin->+0x4C` (`0x00618975`). `twin+0x4C` is the *speed* fed from server `+0xD8`; when it is 0 the code takes the no-divide branch (`0x0061899B`) — safe, but a phantom whose `+0x4C` was never synced plays at nominal speed. Cosmetic. NEEDS-LIVE. |
| Detouring `CSWGuiMainInterface::DrawMap` | Render path, inside a GUI layer/viewport pair. Any exception or unbalanced `AurGUIStartLayer`/`AurGUIStopLayer` corrupts the frame. Construct nothing per frame; wrap the body in a try/catch that always closes the viewport. |
| Writing `CSWSAreaMap +0x28/+0x34/+0x40` party slots directly | Pointless and unsafe: `UpdateMapData 0x004B4E80` overwrites slots `< partySize` every frame, and both map draws bound their loops by `partySize`. Do not. |
| Waypoint marker left un-destroyed at save | Unchanged from `map-presence.md`: `SaveWaypoint 0x005C8230` exists, so an un-destroyed marker bakes into the save. Keep the pre-store teardown. |

## What still needs a live pass (NEEDS-LIVE)

1. Whether the **M-screen** pin already appears with a peer connected — the single
   measurement that decides whether bug #8 is "wrong surface" (my reading) or something else.
2. The stim in question: its active property's `spells.2da` row and that row's `MODELTYPE`.
   `"B"`/`"F"` ⇒ `0x2756 inject`; anything else ⇒ the engine itself plays only idle, and no
   amount of replication will show anything.
3. The `scale` float `DrawMap` uses at `[esp+0xc]`, if the minimap detour is built.
4. Whether the flourish's overlay/full-body split matches what the player saw — i.e. whether
   the playtest presses were made standing (should have been catchable) or walking (cannot
   have been). Log the id from the `0x0063C760` hook and note whether it fires at all.
5. `twin+0x4C` on a phantom after a few seconds of walking, to confirm one-shot playback
   speed is sane.

## Key addresses

### Animation

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004F0D70` | `CSWSCreature::SetAnimation` (server vtable `+0x7C`) | thiscall, 4 | writes `+0xD4`, dirty-on-change (`0x004F0F33`) |
| `0x004CCF60` | `CSWSObject::SetAnimation` | thiscall, 4 | `+0xD4 = anim; +0x1FC |= 1` |
| `0x004B0B70` | `CServerAIMaster::UpdateState` | thiscall, 0 | **the whole server→client sync**: `0x004B10A8`–`0x004B115C` |
| `0x004CC2B0` / `0x004CC220` / `0x004CC250` | `GetClientObject` / `IsDirtyFlagSet` / `ClearDirtyFlag` | thiscall | dirty array at `obj + (bit>>5)*4 + 0x1FC` |
| `0x0060C100` | `CSWCCreature::ClientServerSyncHandler_Animation` (twin vtable `+0x134`) | thiscall, 8 | `(anim, speed)`; skips `{0x2716,0x2718,0x2719,0x271B,0x271C,0x271E,0x277D,0x27AC}` |
| `0x0063C060` | `CSWCObject::GetCurrentAnimation` | thiscall, 0 | `mov ax,[ecx+0x48]` — **u16** |
| `0x0063C300` / `0x0063C380` / `0x0063C410` | `AnimationLooping` / `AnimationFireAndForget` / `AnimationOverlay` | thiscall, 4 | the classifiers; `0x0063C380` reads `animations.2da` via `0x00413510` |
| `0x0063C760` | `CSWCObject::InsertFireForgetAnimation` | thiscall, 20 | **hook here** — 15 call sites, every client one-shot |
| `0x006183D0` | `CSWCCreature::AddFireForgetAnimation` (twin vtable `+0xA0`) | thiscall, 8 | apply one-shot: `(twin, anim, NULL)`; NULL branch at `0x00618975` |
| `0x0061A670` | `CSWCCreature::AnimateFireAndForget` | thiscall, 0 | drains the queue; **writes `twin+0x48` at `0x0061A7D1`** |
| `0x0063D830` | `CSWCObject::SetLoopingAnimation` | thiscall, 2 | writes `twin+0x58` only if `AnimationLooping` |
| `0x0069F650` | `CSWCAnimBase::ServerToClientAnimation` (animbase vtable `+0xE0`) | thiscall, 4 | const → `animations.2da` row, keyed on `animBase+0xC5` |
| `0x006A03C0` | `CSWCAnimBase::SetOverlayAnimation_2` (animbase vtable `+0x44`) | thiscall, 8 | `(row, 1)` — the overlay flourish |
| `0x00610A70` | `CSWCCreature::PlayDrawAnimation` | thiscall, 4 | draw `0x2806`; called from `UpdateVisibleWeapons 0x00613850` at `0x006138F5`, not from a key |
| `0x005F4460` | `CClientExoAppInternal::PlayerFlourishWeapons` | thiscall, 0 | **the X key**; `0x27AE` F&F at `0x005F46E2`, `0x27AD` overlay at `0x005F4694` |
| `0x005EDE90` / `0x00621C21` / `0x00621210` | `CClientExoApp::PlayerFlourishWeapons` / dispatch arm for input event **242** / `HandleInputEvent` | | jump tables at `0x006221FC` (byte) and `0x00622154` (dword) |
| `0x004F2610` | `CSWSCreature::SetCombatState` | thiscall, 5 | `(1, 2)` — the only server write the flourish makes |
| `0x0050F170` | `CSWSCreature::AIActionItemCastSpell` | thiscall, 4 | item use; anim chosen at `0x0050F59A` / `0x0050F5BA` / `0x0050F627` |
| `0x0063D970` | `CSWCObject::AddVisualEffect` | thiscall, 28 | the stim's *glow* channel — not an animation |
| `0x005F32E0` | `CClientExoAppInternal::GetClientMeleeAnimation` | thiscall | swings resolved client-side from `combatanimations.2da` |

Offsets: server `CSWSObject +0xD4` animation (int), `+0xD8` animation speed (float),
`+0x1FC` dirty bits (0=anim, 1=pos, 2=orient), `+0x224` client twin. Twin `CSWCObject`
`+0x48` current animation (**u16**), `+0x4A` queued animation (u16), `+0x4C` speed (float),
`+0x58` looping animation (u16), `+0x5A/+0x5C/+0x60` F&F queue, `+0x64` remaining time,
`+0x68` `CSWCAnimBase*`, `+0x444` deferred draw. `CSWCAnimBase +0xC5` weapon model class,
`+0xC6` model kind. Vtables: server `CSWSCreature 0x007470C8`, client
`CSWCCreature 0x0074F5F8`, `CSWCAnimBase 0x00754F60`.

Constants worth hardcoding: `0x27AE` flourish (full body), `0x27AD` flourish (overlay),
`0x2806` draw, `0x2756` inject, `0x2798` activate, `0x2791`/`0x2792` grenade. Everything else
is 2DA-driven — mirror what the hook hands you.

### Map

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x0068AB10` | `CSWGuiMainInterface::DrawMap` | thiscall, 4 | **the minimap** — map image + fog + ONE arrow; no notes, no party loop |
| `0x00687E40` | `CSWGuiMainInterface::InitializeMap` | thiscall, 0 | builds the fixed control set |
| `0x00688100` | `CSWGuiMainInterface::UpdateAndDrawFogOfWar` | thiscall, 4 | |
| `0x006943D0` | `CSWGuiMapHider::Draw` | thiscall, 4 | **the M-screen** — fog grid, then the note loop from `0x006945EF`, then the party dots at `0x00694A20`, then the arrow at `0x00694AAC` |
| `0x00692C70` | `CSWGuiMapHider::InitializeMapNotes` | thiscall, 8 | membership snapshot, per **panel open** (`OnPanelAdded 0x00693650`); needs `+0x228 != 0` |
| `0x006929B0` | `CSWGuiInGameMap::SetMapNote` | thiscall, 4 | empty-string branch at `0x006929EF` — a blank note label is safe |
| `0x00578E00` | `CSWSAreaMap::GetMapPixelFromWorldCoord` | thiscall, 20 | world → map pixel |
| `0x00578ED0` | `CSWSAreaMap::GetMapRotateCCWFromWorldOrientation` | thiscall, 12 | arrow angle |
| `0x005790C0` / `0x00579130` | `SetPartyMemberWorldLocation` / `…Orientation` | thiscall, 16 | `index < 3`; stores `+0x28+i*4`, `+0x34+i*4`, `+0x40+i*4` |
| `0x005791B0` / `0x005791F0` | `GetPartyMemberMapLocation` / `…Orientation` | thiscall, 12 / 4 | returns 0 when the slot is `-1` |
| `0x005792D0` | `CSWSAreaMap::SetWorldPointExplored` | thiscall, 16 | fog reveal (shared reveal for free) |
| `0x00579210` | `CSWSAreaMap::IsWorldPointExplored` | thiscall, 12 | per-frame gate on every note |
| `0x004B4E80` | `CServerExoAppInternal::UpdateMapData` | thiscall, 0 | the only writer of the party slots; iterates `CSWParty` every frame |
| `0x005ED8B0` / `0x006346C0` | `CClientExoApp::GetSWParty` / `CSWParty::GetCharacter` | thiscall | count at `[party+0]` |
| `0x004591B0` / `0x004592F0` / `0x00459580` / `0x004592B0` | `AurGUIStartLayer` / `AurGUISetupViewport` (cdecl, 7 args) / `AurGUICloseViewport` / `AurGUIStopLayer` | | the blit frame a minimap detour must reproduce |
| `0x0041C0F0` | `CSWGuiButton::Constructor` | thiscall, 0 | how the M-screen builds each pin |

Offsets: `CSWSModule +0x218` → `CSWSAreaMap*`. `CSWSAreaMap +0x04` map-present,
`+0x10` orientation mode, `+0x14` map texture name, `+0x28/+0x34/+0x40` party slots ×3.
`CSWSWaypoint +0x228` `has_map_note`, `+0x22C` `map_note_enabled`, `+0x230` `map_note`
(`CExoLocString`), `+0x238` `localized_name`, `+0x90` position.
`CSWGuiMainInterface +0x5E00` map image control, `+0x5F40` arrow control, `+0x5FB8` arrow
rotation, `+0x6080/+0x6084` minimap viewport origin, `+0x6088` viewport side,
`+0x608C` height used for centring. `CSWGuiMapHider +0x238` note-id list, `+0x23C` selected
note, `+0x60` arrow control, `+0x64` party-dot control.
