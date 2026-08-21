# Effect sync — the generic effect dispatcher, and which non-damage effects can cross the wire (research 2026-08-21)

Item 3 of `docs/roadmap-seamless.md`: `PhantomHitHook.cs` forwards plain damage
(`CSWSEffectListHandler::OnApplyDamage`, `docs/research/combat-participation.md` §4) but nothing
else an enemy's attack can do — stun, paralyze, knockdown, slow, frighten, sleep, choke, Force
push/whirlwind — crosses to a peer's real character. This finds the generic dispatcher every
non-damage effect funnels through, the effect-type table behind it, and which allowlist members
have a VERIFIED, safe replay path today.

Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, image base
`0x400000`) with `tools/disasm.py`, `tools/xrefs.py`, the symbol DB `tools/kotor1_0_3.db`
(`sqlite3`/`python`), and direct reads of the binary's data section for the jump-table globals
below. Nothing was modified, no game was run, and no game function was called for this document.

Tag key: **VERIFIED** = read from the instruction stream (or the sha256-matched symbol DB) ·
**INFERRED** = deduced from code read but not itself disassembled · **NEEDS-LIVE** = must be
confirmed on the running game before the flag defaults to on.

---

## Headline

**The generic dispatcher is `CSWSEffectListHandler::OnEffectApplied` (`0x004D8320`).** Thiscall,
`ret 0xC` (three stack args: target, effect, gameLoading — the same three `OnApplyDamage`
takes, because `OnApplyDamage` is one of its 110 registered handlers, not a separate choke
point). It reads the effect's TYPE at `effect+0x008` (word — `CGameEffect.type`, per the symbol
DB), range-checks `0 < type < 0x6E` (110), indexes a 110-entry function-pointer table at
`this+0x004`, and tail-calls the result with `(target, effect, gameLoading)` unchanged — VERIFIED,
full decode below. `OnApplyDamage` is registered at table index 38; `OnApplyKnockdown` at 20;
`OnApplyMovementSpeedDecrease` at 29; `OnApplyForcePush` at 60; a single `OnApplySetState`
(index 8) handles an entire FAMILY of state effects — stun-shaped, paralyze-shaped, sleep-shaped,
frightened, choke, Force whirlwind — distinguished only by an integer sub-code carried in the
effect's own integer list, not by a distinct table slot each.

**A hook on the dispatcher sees every non-damage effect in the game with one detour**, exactly
the way `OnApplyDamage` sees every damage event — filter on `target == phantom`, then on
`effect->type` (and, for type 8, the sub-code) being in the allowlist, exactly as
`PhantomHitHook` filters on `target == phantom` first.

**The state-effect sub-codes are VERIFIED numerically, but the ENGLISH NAMES are not**, and that
matters for what ships. K1 nwscript.nss was not available to cross-reference (this repo has no
copy, and none was fetched), so the mapping "sub-code 5 is called `EffectParalyze` in a script"
could not be confirmed. What IS verified: the exact numeric round-trip — capture `(type,
subcode)`, and replay through the exact VM routine that would have produced that pair, and the
same engine code that ran on the sender runs the receiver's copy too. The three sub-codes this
pass could not identify by name (§3.3) are captured and forwarded (mechanism is general) but not
yet given an apply-side constructor — logged no-op, not a guess dressed as certainty.

---

## 1. The generic dispatcher — `OnEffectApplied` (`0x004D8320`) — VERIFIED

```c
int __thiscall CSWSEffectListHandler::OnEffectApplied(
    CSWSEffectListHandler* this,
    CGameObject*  target,       // [esp+4]
    CGameEffect*  effect,       // [esp+8]
    int           gameLoading); // [esp+0xC]
```

```
0x004D8320  mov edx, [esp+8]          ; edx = effect
0x004D8324  movzx eax, word [edx+8]   ; eax = effect->type   (CGameEffect+0x008, VERIFIED in DB)
0x004D8328  test eax, eax
0x004D832B  jle  0x4d834d             ; type <= 0  -> return 1 (no handler)
0x004D832D  cmp  eax, 0x6e            ; 110
0x004D8330  jge  0x4d834d             ; type >= 110 -> return 1
0x004D8332  mov  esi, [ecx+4]         ; ecx = this; esi = APPLY table (this+0x004)
0x004D8335  mov  eax, [esi+eax*4]     ; eax = table[type]
0x004D833A  je   0x4d834d             ; null entry -> return 1 (unregistered type)
0x004D833C  mov  esi, [esp+0x10]      ; forward gameLoading
0x004D8340  push esi
0x004D8341  push edx                  ; forward effect
0x004D8342  mov  edx, [esp+0x10]      ; forward target
0x004D8346  push edx
0x004D8347  call eax                  ; handler(target, effect, gameLoading), stdcall, self-cleaning
0x004D834A  ret  0xc
```

The sibling entry point at `0x004D8360` is `OnEffectRemoved` — same shape, reads the REMOVE
table at `this+0x008` instead, `ret 8` (two args: no `gameLoading` on removal). Not needed for
this pass (an effect that lands is what a peer needs to feel; expiry is symmetric with duration
already carried, §2.4).

### 1.1 Where the tables come from — `InitializeEffects` (`0x004E4A10`) — VERIFIED

Allocates two 0x1B8-byte (110 × 4) blocks at `this+4` (apply) and `this+8` (remove), zeroes
them, then writes ~106 function pointers into the apply table by literal `mov dword [table +
offset], handler` at fixed offsets. `offset / 4` is the effect type. This is how every
`(type, handler)` pair below was read — not inferred from names, from the actual store
instructions. The full apply-table dump (106 of 110 slots populated; 0, 1, 2 and 0x99/85 are
never written and read as null — VERIFIED) is in `docs/research/effect-sync.md` §5 for
completeness but the allowlist only needs a handful:

| Type | Handler | Notes |
| --- | --- | --- |
| 8 | `OnApplySetState` (`0x004E1C20`) | the state-effect family — §2 |
| 9 | `OnApplySetStateInternal` (`0x004DA330`) | a second, narrower state entry point; not used by any allowlist member found this pass |
| 18 | `OnApplyEntangled` (`0x004D9720`) | not in the allowlist; listed because it shares the direct-type-no-subcode shape with Knockdown |
| 19 | `OnApplyDeath` (`0x004E0AC0`) | `death-handling.md` |
| 20 | `OnApplyKnockdown` (`0x004D99B0`) | §4.1 |
| 29 | `OnApplyMovementSpeedDecrease` (`0x004DA990`) | Slow — §4.2 |
| 38 | `OnApplyDamage` (`0x004DFA40`) | `combat-participation.md` §4 — PhantomHitHook's existing hook |
| 60 | `OnApplyForcePush` (`0x004E3800`) | §4.3 |

---

## 2. The state-effect family — `OnApplySetState` (`0x004E1C20`) — VERIFIED (mechanism), sub-code names NOT verified

### 2.1 How a sub-code reaches this handler

`OnApplySetState` reads `GetInteger(effect, 0)` (`CGameEffect::GetInteger 0x00503690`, same
accessor `combat-participation.md` §4.3 already documents) into `ebx`, then dispatches on `ebx`
with a plain `cmp`/`jne` cascade — VERIFIED, decoded through `ebx ∈ {1, 2, 4, 5, 6, 8}`. Each
branch does an immunity/feat check specific to that code (a droid-race test for one, a
`GetHasFeat`-shaped OR a resistance-table test for others — decoded but not named, because the
checks themselves don't carry English names either), then calls a shared "set the state" helper
(`0x004D7780`) with the sub-code, `-1` (duration marker — the caller's `fDuration` already
established the effect's real duration before this handler ever runs, §2.4) and a per-code visual
effect id, and finally posts a VFX icon (`0x004EDE10`) keyed by a per-code icon constant
(`0x87`, `0x89`, `0x80`, `0x8A`, ...). None of this reveals an nwscript name — it reveals
*mechanism*, not *label*.

### 2.2 Where the sub-codes are minted — `ExecuteCommandEffectComplex` (`0x005313F0`) — VERIFIED

K1 builds every one of these through one VM routine, `EffectComplex`, keyed on its first script
argument (`nComplexType`) via a two-level jump table (a byte-indexed compaction table at
`0x53184C` feeding a dword jump table at `0x531820`, decoded in full for the `0x33..0xA4`
range). Reading the raw bytes at the table's data-section globals (disk == memory) gives the
EXACT `(nComplexType, effect type, sub-code)` triples the compiled game uses:

| `nComplexType` | Effect type written | Sub-code (`SetInteger(0, N)`) | What this pass calls it |
| --- | --- | --- | --- |
| `0x82` (130) | 18 (`OnApplyEntangled`) | — (no sub-code call) | not in the allowlist |
| `0x86` (134) | 20 (`OnApplyKnockdown`) | — (no sub-code call) | **Knockdown** |
| `0x94` (148) | 8 (`OnApplySetState`) | 5 | unnamed — §3.3 |
| `0x9A` (154) | 8 | 6 | unnamed — §3.3 |
| `0x9D` (157) | 8 | 1 | unnamed — §3.3 |
| `0x9E` (158) | 8 | 2 | unnamed — §3.3 |
| `0xA1` (161) | 8 | 4 | unnamed — §3.3 |

Sub-codes 3, 7, 8 and 10 do **not** go through `EffectComplex` at all — they have their own
one-line VM routines that build the same type-8 effect directly (§3).

### 2.3 Duration and magnitude — `CGameEffect` layout — VERIFIED from the symbol DB, cross-checked against live reads/writes in this session's disassembly

```
CGameEffect (per tools/kotor1_0_3.db `offsets` table, itself sha256-matched to this binary):
  +0x00  id            ulonglong
  +0x08  type          ushort      (EFFECT_TYPES — the dispatcher's index)
  +0x0A  subtype       ushort      (a SEPARATE field from the integer-list sub-code above;
                                     used by e.g. MovementSpeedDecrease as a bit-flag word,
                                     not by the SetState family)
  +0x0C  duration      float       (seconds)
  +0x10  expire_day    ulong
  +0x14  expire_time   ulong
  +0x18  creator_id    ulong       (the attacker — combat-participation.md §4.3)
  +0x1C  spell_id      int
  +0x30  num_integers  int
  +0x34  integer_list  int*        (index 0 is the SetState sub-code; damage's 15 slots live
                                     here too, on effects of type 38 — same field, different type)
```

`duration`, `expire_day` and `expire_time` are populated by the generic "add this effect to the
object" step that runs BEFORE dispatch reaches `OnEffectApplied` — INFERRED from `ApplyEffectToObject`'s
signature (`int nDurationType, effect eEffect, object oTarget, float fDuration`, already
documented in `Odyssey.cs`'s `ApplyDamageEffectCore` comment) taking the duration as a caller
argument rather than any type-specific handler computing it. Reading `effect+0xC` inside the hook
(§5) gets the caller's requested duration in seconds for any effect, damage included — this is
what the hook multiplies by 1000 and forwards as `durationMs`. NEEDS-LIVE: confirm the field is
non-zero and sane (not `-1`/uninitialized) on an actual state-effect hit before trusting it over
a hard-coded fallback.

### 2.4 Duration-type constants — INFERRED, not independently verified this session

`nDurationType` (`ApplyEffectToObject`'s first argument) is caller-supplied, not looked up in
an internal table, and `Odyssey.ApplyDamageEffectCore` already passes `0` for damage's own
instant application. This document assumes the standard Aurora/Odyssey convention — 0 instant, 1
temporary, 2 permanent — is unchanged here, on the reasoning that it is a value the CALLER
chooses (baked into compiled `.ncs` by the K1 script compiler, not decided by this engine's own
code), so it is far less likely to have moved than the effect-type table above, which demonstrably
has. NEEDS-LIVE if this is ever load-bearing for something other than "temporary vs instant."

---

## 3. Dedicated (non-`EffectComplex`) constructors for type-8 effects — VERIFIED

Four more sub-codes never go through `EffectComplex` — they have their own trivial VM routines
that build the same effect type (8) directly, reading the type constant from a data-section
global exactly like `EffectComplex`'s cases do (same technique, read the byte from the binary,
disk == memory):

| VM routine | Address | Effect type (global read) | Sub-code (immediate `push`) | Script params |
| --- | --- | --- | --- | --- |
| `ExecuteCommandEffectDroidStun` | `0x00534810` | 8 (`[0x74669C]`) | `3` | **none** — VERIFIED, no arg reads |
| `ExecuteCommandEffectChoke` | `0x005351E0` | 8 | `7` | **none** |
| `ExecuteCommandEffectWhirlWind` | `0x00534C20` | 8 | `0xA` (10) | **none** |
| `ExecuteCommandEffectHorrified` | `0x00534E90` | 8 | `8` | **none** |

All four bodies are byte-identical in shape apart from the two immediates (the type-global
address and the pushed sub-code) — construct effect, `SetInteger(effect, 0, subcode)`, done, no
`[esp+...]` script-argument read anywhere in the function. That absence is the evidence these
are zero-parameter NWScript effect constructors (`effect EffectXxx()`), which is exactly why
they are safe to call from native code without having reverse-engineered a parameter list: push
nothing, call with `nParams = 0`, matching the discipline `Odyssey.ApplyDamageEffectCore`
already uses for `EffectDamage`'s three declared params (push three, call with `nParams = 3`).

`ExecuteCommandEffectForcePushed` (`0x005349F0`) is the odd one out: it writes type **60**
(`[0x74676C]` reads `60` — VERIFIED) directly (not through the type-8 family), calls
`SetCreator(effect, [implementer+8])` — the VM's own notion of `OBJECT_SELF`, not a script
argument — and otherwise reads no `[esp+...]` script argument either. Also zero declared
params.

`ExecuteCommandEffectMovementSpeedDecrease` (`0x005332E0`) writes type **29** directly
(`[0x7466F0]` reads `29`) and DOES read one script argument at `[esp+0x24]`, pushed straight into
`SetInteger(effect, 0, value)` — a single `int nPercentChange` parameter. This is Slow, and it is
the only allowlist member (besides the two above) whose full parameter list is known with
certainty.

### 3.1 What this pass names the allowlist, and why

The roadmap's allowlist names eight things: stun, paralyze, knockdown, slow, frightened, sleep,
choke, force push/whirlwind. Cross-referencing against what was actually found:

| Roadmap name | Numeric identity | Verified? |
| --- | --- | --- |
| Knockdown | type 20, no sub-code | **VERIFIED** — direct table entry AND `EffectComplex(0x86)` agree |
| Slow | type 29, `SetInteger(0, percent)` | **VERIFIED** |
| Force push | type 60, no sub-code | **VERIFIED** |
| Whirlwind | type 8, sub-code 10 | **VERIFIED** (dedicated VM routine, name literally says WhirlWind) |
| Frightened | type 8, sub-code 8 | **VERIFIED** (dedicated VM routine, name literally says Horrified) |
| Choke | type 8, sub-code 7 | **VERIFIED** (dedicated VM routine, name literally says Choke) |
| "Stun" | type 8, sub-code 3 | **VERIFIED BY NAME, DROID-SCOPED** — the one sub-code whose VM routine is literally named `EffectDroidStun`. Used as this pass's Stun implementation; whether a SEPARATE, non-droid stun exists among the unnamed `EffectComplex` sub-codes (§2.2) is open. |
| Paralyze | one of the unnamed `EffectComplex` sub-codes {1, 2, 4, 5, 6} minus 3 | **NOT IDENTIFIED** — §3.3 |
| Sleep | same set | **NOT IDENTIFIED** — §3.3 |

### 3.2 Why the remaining sub-codes were not guessed

Five `EffectComplex` sub-codes (1, 2, 4, 5, 6) reach `OnApplySetState` with no `nwscript.nss` in
this repository to name them, and the immunity/feat checks each branch runs (§2.1) are
themselves unnamed data (a feat index, a resistance-table index) rather than a string anywhere
in the binary. Naming one of them "Paralyze" without a source would be exactly the kind of
un-verified claim the RE conventions this codebase uses (`combat-participation.md`,
`shared-state-audit.md`) explicitly tag as **INFERRED at best** — and calling the wrong one
"Paralyze" in a doc or a log line is worse than not naming it, because the next person trusts the
label. §3.3 is the honest state: captured, forwarded (the mechanism doesn't care about the
English name), not yet given an apply-side constructor.

### 3.3 NEEDS-LIVE: naming the remaining sub-codes

Trigger each of the five `EffectComplex` sub-codes against a phantom (a console `EffectComplex`
call staged like the existing damage/attack test hooks, or by provoking the matching enemy
Force power) and log:
1. Which sub-code the hook captured (`GetInteger(effect, 0)`, already read by `OnApplySetState`
   itself — the hook only has to read the same field).
2. What animation/pose the target visibly plays.
3. Whether the target can still act (a paralyzed/stunned/sleeping creature cannot; a merely
   slowed one can).

That is enough to assign English names with actual evidence, at which point the apply-side gets
constructors for them the same way §4 gives them to Knockdown-adjacent effects — or does not, if
none of the five turn out to be things worth replaying.

---

## 4. Apply-side safety — why Knockdown has no constructor yet, and Slow/Choke/Whirlwind/Frightened/"Stun" do

Calling a VM effect constructor from native code (the pattern `Odyssey.ApplyDamageEffectCore`
already uses for `EffectDamage`) requires pushing the EXACT number of stack arguments the
compiled routine expects, then invoking it with a matching `nParams`. Get the count wrong and
the VM's own stack bookkeeping desyncs for whatever script runs next — a correctness bug with no
guard possible after the fact, unlike a wrong ENGINE OFFSET (which this codebase's convention
turns into a logged refusal by checking the byte pattern or table entry first).

- `EffectChoke()`, `EffectWhirlWind()`, `EffectHorrified()`, `EffectDroidStun()`,
  `EffectForcePushed()` — **zero** declared parameters, VERIFIED by the total absence of any
  `[esp+...]` script-argument read in each routine's body. Safe to call: push nothing, `nParams
  = 0`.
- `EffectMovementSpeedDecrease(int nPercentChange)` — **one** declared parameter, VERIFIED the
  same way (exactly one `[esp+...]` read, fed straight to `SetInteger`). Safe to call: push one
  int, `nParams = 1`.
- `EffectComplex(int nComplexType, ...)` — **at least two** parameters are read across its
  cases (a second int in the `0x85`/133 case, a float in the `0xA4`/164 case), and the full
  declared list was not recovered this session. Knockdown is only reachable through this
  routine (§2.2). Calling it with the wrong argument count is the exact hazard above, so
  **Knockdown ships as capture-and-forward only this pass** — the hook records and forwards it
  (the mechanism is general), and the receiving side logs `"knockdown: not yet replayed —
  EffectComplex's full signature is unverified"` rather than guess at a stack shape that could
  desync the VM. This is the "guarded so a wrong one is a logged no-op, not a crash" rule from
  the engine-write conventions, applied to a signature gap rather than an offset.

Paralyze and Sleep are in the same "captured, not replayed" state, for the more basic reason
that §3.3 has not yet named which sub-code is which.

---

## 5. Full apply-table dump (for completeness / future work)

`type = offset / 4` into the 110-entry apply table at `CSWSEffectListHandler+4`, VERIFIED by
parsing every `mov dword [table+offset], handler` instruction `InitializeEffects` executes:

```
3  OnApplyHasteOrSlow        20  OnApplyKnockdown            60  OnApplyForcePush
4  OnApplyResurrection       21  OnApplyDeaf                 62  OnApplyDisguise
5  OnApplyDisease            22  OnApplyEffectNull            63  OnApplySanctuary
6  OnApplySummonCreature     23  OnApplySetAIState            64  OnApplyTimestop
7  OnApplyRegenerate         24  OnApplyEnemyAttackBonus      66  OnApplyEffectNull
8  OnApplySetState           25  OnApplyArcaneSpellFailure    67  OnApplyEffectIcon
9  OnApplySetStateInternal   26  OnApplySavingThrowIncrease   68  OnApplyRacialType
10 OnApplyAttackIncrease     27  OnApplySavingThrowDecrease   69  OnApplyVision
11 OnApplyAttackDecrease     28  OnApplyMovementSpeedIncrease 70  OnApplySeeInvisible
12 (return_zero_0xc, stub)   29  OnApplyMovementSpeedDecrease 71  OnApplyUltraVision
13 OnApplyDamageIncrease     30  OnApplyVisualEffect          72  OnApplyTrueSeeing
14 OnApplyDamageDecrease     31  OnApplyAreaOfEffect          73  OnApplyBlindness
15 OnApplyTemporaryHitpoints 32  OnApplyBeam                  74  OnApplyDarkness
16 OnApplyDamageImmunityIncr 33  OnApplyForceResistanceIncr   75  OnApplyMissChance
17 OnApplyDamageImmunityDecr 34  OnApplyForceResistanceDecr   76  OnApplyMissChance (dup)
18 OnApplyEntangled          35  OnApplyPoison                79  OnApplyDisappearAppear
19 OnApplyDeath              36  OnApplyAbilityIncrease       80  OnApplyDisappear
36 OnApplyAbilityIncrease    37  OnApplyAbilityDecrease       81  OnApplyAppear
38 OnApplyDamage             39  OnApplyHeal                  82  OnApplyNegativeLevel
40 OnApplyLink               41  OnApplyHasteInternal         83  OnApplyBonusFeat
42 OnApplySlowInternal       43  OnApplyEffectNull            84  OnApplyWounding
44 OnApplyModifyNumAttacks   45  OnApplyCurse                 86  OnApplyVampiricRegeneration
46 OnApplySilence            47  OnApplyInvisibility          87  OnApplyDisarm
48 OnApplyACIncrease         49  OnApplyACDecrease            89  OnApplySummonParty
51 OnApplyDispelAllMagic     52  OnApplyDispelBestMagic       90  OnApplyForceDrain
53 OnApplyTaunt              54  OnApplyLight                 91  OnApplyTemporaryForce
55 OnApplySkillIncrease      56  OnApplySkillDecrease         95  OnApplyDamageForcePoints
57 OnApplyHitPointChangeWhenDying                             96  OnApplyHealForcePoints
58 OnApplySpecialWalkAnimation                                98  OnApplyBodyFuel
59 OnApplyLimitMovementSpeed                                 100  OnApplyLightsaberThrow
                                                              101  OnApplyAssuredHit
                                                              102  OnApplyForceJump
                                                              103  OnApplyForceJumpDelayed
                                                              104  OnApplyAssuredDeflection
                                                              105  OnApplyForceResisted
                                                              106  OnApplyForceFizzle
                                                              107  OnApplyForceShield
                                                              108  OnApplyPureGoodPowers
                                                              109  OnApplyPureEvilPowers
```
(0, 1, 2, 85 read null — never written by `InitializeEffects`; 12, 50, 61, 65, 78, 92, 93, 94,
97, 99 write a shared no-op stub `0x0066B360`, `return_zero_0xc` in the symbol DB.)

---

## 6. Key addresses

| Address | Symbol | Conv / stack | Note |
| --- | --- | --- | --- |
| `0x004D8320` | `CSWSEffectListHandler::OnEffectApplied` | thiscall, 0xC | **the hook point** — §1 |
| `0x004D8360` | `CSWSEffectListHandler::OnEffectRemoved` | thiscall, 8 | sibling; not hooked this pass |
| `0x004E4A10` | `CSWSEffectListHandler::InitializeEffects` | thiscall | builds both 110-entry tables — §1.1 |
| `0x004E1C20` | `OnApplySetState` | stdcall, 0xC | the state-effect family — §2 |
| `0x004DA330` | `OnApplySetStateInternal` | stdcall, 0xC | unused by the allowlist this pass |
| `0x004D99B0` | `OnApplyKnockdown` | stdcall, 0xC | type 20 |
| `0x004DA990` | `OnApplyMovementSpeedDecrease` | stdcall, 0xC | type 29 — Slow |
| `0x004E3800` | `OnApplyForcePush` | stdcall, 0xC | type 60 |
| `0x005313F0` | `ExecuteCommandEffectComplex` | thiscall | mints most of the type-8 family — §2.2 |
| `0x00534810` | `ExecuteCommandEffectDroidStun` | thiscall | sub-code 3, 0 params |
| `0x005351E0` | `ExecuteCommandEffectChoke` | thiscall | sub-code 7, 0 params |
| `0x00534C20` | `ExecuteCommandEffectWhirlWind` | thiscall | sub-code 10, 0 params |
| `0x00534E90` | `ExecuteCommandEffectHorrified` | thiscall | sub-code 8, 0 params |
| `0x005349F0` | `ExecuteCommandEffectForcePushed` | thiscall | type 60, 0 params |
| `0x005332E0` | `ExecuteCommandEffectMovementSpeedDecrease` | thiscall | type 29, 1 param (`int nPercentChange`) |
| `0x00503690` | `CGameEffect::GetInteger` | thiscall, 4 | sub-code / damage-slot reader, per `combat-participation.md` §4.3 |
| `0x0052E570` | `ApplyEffectToObject` | thiscall | existing VM apply call, `Odyssey.cs` |
| `0x53184C` / `0x531820` | `EffectComplex` case tables | data | byte-compaction table / dword jump table, `0x33..0xA4` range decoded |

### Constants (read from the binary's data section, disk == memory)

```
0x0074669C  8    type written by EffectComplex's SetState-family cases and every dedicated
                  zero-param routine (Choke, WhirlWind, Horrified, DroidStun)
0x0074676C  60   type written by ExecuteCommandEffectForcePushed
0x007466F0  29   type written by ExecuteCommandEffectMovementSpeedDecrease
0x007466C4  18   type written by EffectComplex case 0x82 (Entangled — not in the allowlist)
0x007466C8  19   type written by EffectComplex case 0x85's neighbour (Death-adjacent — not decoded further)
0x007466CC  20   type written by EffectComplex case 0x86 (Knockdown)
0x00746748  51   type written by EffectComplex's dispel-adjacent case (not decoded further)
```

### CGameEffect (from `tools/kotor1_0_3.db`'s `offsets` table)

```
+0x00  id             ulonglong
+0x08  type           ushort   (EFFECT_TYPES)
+0x0A  subtype        ushort
+0x0C  duration       float    (seconds)
+0x10  expire_day     ulong
+0x14  expire_time    ulong
+0x18  creator_id     ulong
+0x1C  spell_id       int
+0x30  num_integers   int
+0x34  integer_list   int*     (index 0 = SetState sub-code; damage's 15 slots on type-38 effects)
```

---

## Verification tags — summary

- **VERIFIED**: `OnEffectApplied`'s full decode, signature and table lookup (§1); the
  `InitializeEffects` table-build pattern and every `(type, handler)` pair in §1.1 and §5, read
  from the actual store instructions; `OnApplySetState`'s sub-code read and cascade shape (§2.1,
  mechanism only, not names); every `(nComplexType, type, subcode)` triple in §2.2, read from
  the compiled jump table AND the binary's data-section globals; the four dedicated zero-param
  routines and `ForcePushed`'s zero-param, self-derived-creator shape (§3); `MovementSpeedDecrease`'s
  single `int` parameter (§3); `CGameEffect`'s field layout (from the symbol DB — a lighter form
  of verification than an independent disassembly, but the DB itself is sha256-matched to this
  binary per every prior research doc that cites it).
- **INFERRED**: that `duration`/`expire_day`/`expire_time` are populated before
  `OnEffectApplied` dispatches (§2.3 — the field exists and `ApplyEffectToObject` takes duration
  as a caller argument, but the exact write site was not disassembled this session); the
  `nDurationType` constant convention (§2.4 — caller-supplied, presumed unchanged from the
  wider Aurora/Odyssey convention, not independently verified).
- **NOT IDENTIFIED / NEEDS-LIVE**: which of `EffectComplex` sub-codes {1, 2, 4, 5, 6} is
  "Paralyze," "Sleep," or anything else in English (§3.2, §3.3); whether `effect+0xC`'s duration
  reads sane values on a live state-effect hit (§2.3); `EffectComplex`'s full parameter list,
  needed before Knockdown can get a client-side apply constructor (§4); the whole live-fire
  checklist this implies is carried into the plugin/agent code as `NEEDS-LIVE` comments at the
  exact call sites they gate.

## Live pass (2026-08-22): not reached this session

`effect_sync=1` was set on both GOG installs as part of a four-check live-testing task (this
channel's smoke test was explicitly marked optional — "if time allows" — behind three other
checks with a harder bar). No hostile with a captured allowlist effect (Knockdown, Slow, Force
push, Whirlwind, Frightened, Choke, the droid-scoped "Stun") was engaged this session: the modules
actually visited (`END_M01AA`, `END_M01AB`, `tat_m20aa`'s Sand People, all melee/blaster
combatants) do not use Force powers, and no console verb exists to inject an effect synthetically
(`PeerEffectController` has no test hook analogous to `dlgprobe.ps1`'s `OffTestTag`/`OffTestSerial`
pair). Time ran out before a Force-using module could be reached. `effect_sync` stays OFF by
default, unchanged — this pass neither confirms nor regresses anything in this document; it is
simply an attempt that did not get far enough to produce evidence either way. A future pass
should either build a test hook (mirroring the dialogue one) or specifically route through a
module with an early Force-using hostile (a Dark Jedi encounter, not reached in this game's
opening hours) before spending rig time on this again.
