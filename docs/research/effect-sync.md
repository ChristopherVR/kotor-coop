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

**SUPERSEDED for Knockdown by §7 (2026-08-22).** The "at least two parameters" hazard below is
real for `ExecuteCommandEffectComplex` in general (its `0x85`/133 and `0xA4`/164 cases DO read
extra script arguments), but §7 shows the type-8 family AND Knockdown's OWN case (routine 134)
read ZERO script arguments — the hazard just did not apply to the specific case Knockdown needed.
Left below as the historical record of why this pass's predecessor declined to guess.

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

## The test hook now exists (2026-08-22): `applyeffect` / `effects`

The gap named directly above — "no console verb exists to inject an effect synthetically" — is
closed. Two staging verbs (`ConsoleCommand`/`CoopCommandDispatcher`/`CheatController`, following
the exact pattern `giveloot`/`setitem`/`slay` already use; see docs/research/console-cheats.md §3
for the full table) mean this list no longer needs a Force-using hostile at all — an effect can
be applied on demand, without engaging anything:

- **`applyeffect <kind> [target] [durationMs] [magnitude]`** runs the kind's constructor
  through `OdysseyEffects.ApplyEffect` — the SAME apply-side call the receiving half of this
  channel makes on an arriving effect (`PeerEffectController.ApplyIncoming`). `kind` is any of
  the nine allowlist names (`stun, paralyze, knockdown, slow, frightened, sleep, choke,
  forcepush, whirlwind`); the six with a real constructor (§4) apply, the other three
  (knockdown, paralyze, sleep) answer `no apply-side constructor for this kind yet` — the
  correct, expected refusal, not a bug to chase.
- **`effects`** reads the shared block's OUT/IN peer-effect rings straight back and lists the
  last 8 with kind name, subject id, duration/magnitude, ring serial and how long ago this
  agent observed each one — the read-only half, so a run can confirm what happened without
  grepping either log.

**STAGING ONLY, LOCAL APPLY — what the hook forwards is the test.** `applyeffect` never crosses
the network by itself. Aimed at an explicit id (including your own player, via `me`/`self`) it
only proves the VM constructor call itself is well-formed — useful, but not what this document's
list has been missing. Aimed at **the nearest phantom** (the default — leave `target` blank) it
IS the live test: `PhantomEffectHook` watches phantom ids specifically
(`PhantomHitHook.IsWatchedPhantom`), so the effect lands there exactly as a real enemy attack
would, gets captured, published to the OUT ring, and forwarded by `PeerEffectController` to
whichever peer that phantom renders — precisely the path this document has never been able to
exercise.

### Exact staging steps for the next live pass

1. Both instances: `effect_sync=1` in `coop.ini` (unchanged from the prior attempt — the
   channel is still gated off by default).
2. Connect the pair in any module; each side renders a phantom for the other.
3. On instance A, aim at instance B's phantom (the default target) for each of the six
   constructed kinds in turn, spacing them out so `effects` can tell the rows apart:
   ```
   applyeffect stun
   applyeffect slow      (blank target, blank duration, blank magnitude -> 6000ms, 50%)
   applyeffect frightened
   applyeffect choke
   applyeffect forcepush
   applyeffect whirlwind
   ```
   `slow` can also be aimed explicitly with a magnitude, e.g. `applyeffect slow "" 8000 70` for
   an 8-second, 70% slow (percent is clamped to 1..90 and falls back to 50 outside that range —
   §4, `OdysseyEffects.MinSlowPercent`/`MaxSlowPercent`).
4. On instance A, `effects` — confirm each kind shows a `captured/forwarded` row with instance
   B's phantom id.
5. On instance B, `effects` — confirm the same kinds show an `applied` row (what
   `PeerEffectController.ApplyIncoming` received and replayed on B's own player), and watch B's
   own character for the visible effect (staggered gait for Slow, the choke pose, etc.) — the
   `#3` item in §3.3's own checklist ("what animation/pose the target visibly plays") is
   answerable this way even without a hostile.
6. For the three capture-only kinds (`knockdown`, `paralyze`, `sleep`), `applyeffect <kind>`
   against the phantom should answer `no apply-side constructor for this kind yet` on the
   TYPING side — that refusal is itself confirmation the routing (kind name -> numeric ->
   `CheatController.ApplyEffect`) works even where the apply half deliberately does not exist
   yet. This does NOT exercise capture/forward for those three; the earlier plan (§3.3 —
   provoke the matching enemy Force power, or stage a synthetic `EffectComplex` call) is still
   the only way to name their sub-codes.
7. Record which of the six constructed kinds visibly landed and which the peer's screen agreed
   with — that is what finally moves this document's per-kind rows from **VERIFIED
   (mechanism)** to **VERIFIED (live)**.

## Live pass (2026-08-22): all six constructed kinds VERIFIED (live), both directions

Ran on a fresh `demo.ps1` pair (block version 48, protocol 34), both instances in `END_M01AA`,
`effect_sync=1` on both, using the `applyeffect`/`effects` staging verbs against each other's
phantom via `tools/blockpoke.ps1 <pid> console '...'` (the same agent<->plugin channel a human's
`~` console line writes — no keyboard injection needed, per `console-cheats.md` §4).

**Per-constructor result table** (host = pid with name `Lanik`, client = pid with name `Malak`;
"forward" = host applies to client's phantom, "reverse" = client applies to host's phantom):

| Kind | Forward (host→client) | Reverse (client→host) | Local (`me`) | Notes |
| --- | --- | --- | --- | --- |
| Stun | **APPLIED** — `effect: applied kind 1 from peer 0x1 (duration 6000ms, magnitude 0)` | **APPLIED** — `... from peer 0x2 ...` | **APPLIED** on both | Also round-tripped at an explicit non-default duration: `applyeffect stun 0x12D 3000` → client logged `duration 3000ms` exactly, not the hook's 6000ms fallback (see below) |
| Slow | **APPLIED**, magnitude 50 | **APPLIED**, magnitude 50 | **APPLIED** | Requested magnitude 0 (blank) correctly fell back to the documented 50% default (`OdysseyEffects.MinSlowPercent`/`MaxSlowPercent` clamp) — confirmed in the APPLIED log line, not just the request |
| Frightened | **APPLIED** | **APPLIED** | **APPLIED** | |
| Choke | **APPLIED** | **APPLIED** | **APPLIED** | |
| ForcePush | **APPLIED** | **APPLIED** | **APPLIED** | |
| Whirlwind | **APPLIED** | **APPLIED** | **APPLIED** | |
| Knockdown | refused: `no apply-side constructor for this kind yet` | not tried (symmetric) | refused (same) | Expected, correct refusal — confirms routing works even where the apply half deliberately does not exist |
| Paralyze | refused (same wording) | not tried | refused (same) | ditto |
| Sleep | refused (same wording) | not tried | refused (same) | ditto |

All 6 constructed kinds: **12/12 forward+reverse applications succeeded**, 6/6 local smoke
tests succeeded, all 3 capture-only kinds refused correctly on both the phantom and local
target. Every application was read back from the **plugin log on the receiving instance**
(`effect: applied kind N from peer 0x{id} (duration Nms, magnitude N)`), not just the sender's
own "applied" acknowledgement — i.e. this confirms the packet actually crossed the network and
`PeerEffectController.ApplyIncoming` ran `OdysseyEffects.ApplyEffect` against the receiver's own
`GetPlayerObjectId()`, not merely that the phantom-side capture worked.

**NEEDS-LIVE item 3 (the hook's prologue byte-check) — CONFIRMED.** Every plugin startup this
session logged `combat: phantom-effect watch at 0x4D8320 (stub 0x...)` with no `prologue
mismatch` line anywhere in either log — `PhantomEffectHook.Install`'s byte-check passed cleanly
against the live binary every time.

**§2.3's `effect+0xC` duration sanity — CONFIRMED, and distinguished from the fallback.** The
hook's `FallbackDurationMs` and the console verb's own `DefaultApplyEffectDurationMs` are both
6000ms, so the default-duration tests alone couldn't tell a real field read from a lucky
fallback. Sending an explicit non-default duration settled it: `applyeffect stun 0x12D 3000`
came back on the receiving side as `duration 3000ms` — not 6000. `effect+0xC` is genuinely being
read, non-zero and sane, not silently substituted.

**Damage regression — CONFIRMED clean.** With `PhantomEffectHook` installed and having just
processed a dozen non-damage applications, `blockpoke <hostpid> hitphantom 0 7` still forwarded
7 damage through `PhantomHitHook` exactly as before: `combat: taken 7` on the host's ledger, and
the client's real health dropped `12/12 -> 5/12`. No interaction between the two hooks.

**Stability — clean.** Both instances stayed up through the whole pass (frame counters at
157,576 and 186,040 by the end), `grep -c "FIRST-CHANCE access violation"` was 0 in both logs.

**Sub-code naming (§3.3) — not advanced this pass.** The three unidentified `EffectComplex`
sub-codes (candidates for Paralyze/Sleep) have no apply-side constructor in `OdysseyEffects`, so
`applyeffect` cannot trigger them — by design, the same safety argument §4 already makes. Naming
them still requires either a Force-using hostile or a synthetic `EffectComplex` call this pass
did not attempt. No new evidence either way.

**Movement-block probe — not independently verified.** The task asked whether the client's real
character visibly could not act during Stun's duration. The one movement primitive available
unattended, `blockpoke walk`, is documented dead code (`docs/research/cinematics.md`'s own key-
address table: `HandlePlayerToServerInputWalkToWaypoint 0x005235B0` has zero callers — "the
reason `blockpoke walk` cannot move the PC"), so a `walk` issued during Stun and never landing
proves nothing (it wouldn't land unstunned either — confirmed separately: the position never
moved over 5 polled seconds regardless of stun state). No other unattended movement/action-
block reader exists yet. The **apply-side evidence is still strong** — the engine's own
zero-parameter `EffectDroidStun` constructor ran without refusal, on the receiver's real player
object — but "the target could not act" specifically was not independently instrumented this
pass.

**Verdict: `effect_sync` flips to ON by default** (`PeerEffectController.cs`,
`PluginConfig.ReadBool("effect_sync", true)`). The bar the task set — "stun+slow in both
directions, no crash, no regression" — was cleared with margin: all six constructed kinds, both
directions, local smoke tests, explicit-duration fidelity, and a clean damage-forwarding
regression check.

---

## 7. Naming the remaining sub-codes, and shipping Knockdown/Paralyze/Sleep (2026-08-22)

This closes the gap §3.2/§3.3/§4 left open: the five unnamed `OnApplySetState` sub-codes
{1, 2, 4, 5, 6} are now named, and Knockdown/Paralyze/Sleep now have real apply-side
constructors, static analysis only (no game run this pass). All three independent
cross-checks below AGREE EXACTLY on every routine number, which is the strongest
confirmation this document's methodology can produce without a live test.

### 7.1 Why the prior pass's scan missed the routine numbers — VERIFIED

§2.2 already established the compaction/jump-table's internal `(nComplexType, type, subcode)`
triples correctly. What it could not do was tie those numbers to NAMES, because a first attempt
to find `ExecuteCommandEffectComplex`'s (`0x005313F0`) OWN routine number(s) via
`tools/xrefs.py 0x005313F0` finds only **one** literal 4-byte occurrence of that address anywhere
in `.text` — at `0x0054CCA8`, inside `CSWVirtualMachineCommands::InitializeCommands` (the same
kind of table-builder `InitializeEffects` is for the OTHER dispatch table, §1.1). That one
`mov eax, 0x5313F0` is immediately followed by exactly two stores reusing that same register:

```
0x0054CCA8  mov eax, 0x5313f0
0x0054CCAD  mov dword ptr [edi+0xCC], eax   ; table offset 0xCC / 4 = 51
0x0054CCB6  mov dword ptr [edi+0x3F0], eax  ; table offset 0x3F0 / 4 = 252
```

A raw address-literal scan (`xrefs.py`'s indirect-hit search) cannot see any MORE registrations
than this, because the compiler's own CSE reuses the SAME `eax` for every later store without
re-embedding the address — a routine pattern visible only by reading the surrounding
instructions, not by searching for the constant. Doing exactly that — disassembling a much
larger span of `InitializeCommands` (`0x54B000`..`0x554000`, `tools/disasm.py`) and grepping for
stores to the SPECIFIC table offsets `§2.2`'s compaction table already implicated
(`0x94*4=0x250`, `0x9A*4=0x268`, `0x9D*4=0x274`, `0x9E*4=0x268`... — see below) finds **fifteen
more** registrations of the same address, reusing the SAME `eax` loaded at `0x0054CCA8` across a
much longer run of individually-addressed `mov [edx+off], eax` stores:

```
0x0054CFD3  mov dword ptr [edx+0x274], eax   ; 157
0x0054D044  mov dword ptr [edx+0x214], eax   ; 133
0x0054D04D  mov dword ptr [edx+0x73C], eax   ; 463  (not decoded further — outside our targets)
0x0054D056  mov dword ptr [edx+0x730], eax   ; 460  (not decoded further)
0x0054D05F  mov dword ptr [edx+0x764], eax   ; 473  (not decoded further)
0x0054D068  mov dword ptr [edx+0x208], eax   ; 130
0x0054D07E  mov dword ptr [edx+0x690], eax   ; 420  (not decoded further)
0x0054D0B7  mov dword ptr [edx+0x278], eax   ; 158
0x0054D0C0  mov dword ptr [edx+0x438], eax   ; 270  (not decoded further)
0x0054D0A1  mov dword ptr [edx+0x648], eax   ; 402  (not decoded further)
0x0054D0FD  mov dword ptr [edx+0x218], eax   ; 134
0x0054D147  mov dword ptr [edx+0x250], eax   ; 148
0x0054D1B4  mov dword ptr [edx+0x268], eax   ; 154
0x0054D1D7  mov dword ptr [edx+0x284], eax   ; 161
0x0054D1FA  mov dword ptr [edx+0x74C], eax   ; 467  (not decoded further)
```

`0x5313F0` (`ExecuteCommandEffectComplex`) is therefore registered at **at least 17** routine
numbers, not 2. The 7 relevant to this document's allowlist — `0x208`(130), `0x218`(134),
`0x250`(148), `0x268`(154), `0x274`(157), `0x278`(158), `0x284`(161) — are EXACTLY the 7
`nComplexType` values §2.2 already decoded from the compaction/jump-table data (`0x82, 0x86,
0x94, 0x9A, 0x9D, 0x9E, 0xA1`). This is the first cross-check: two completely different
disassembly techniques (reading a runtime table-population site vs. reading a compiled
switch-table's own data bytes) landed on the identical 7 numbers.

### 7.2 Why arg1 is the calling routine number, not a popped script value — VERIFIED

Stack arithmetic on `ExecuteCommandEffectComplex`'s own prologue settles what `[esp+0x24]` (the
value the function switches on, `0x00531443`) actually is. Its prologue is the standard MSVC
`/EHsc` shape: `push -1; push <unwind-table>; push fs:[0]; mov fs:[0],esp; sub esp,8; push ebp;
push esi; push edi; push 0x8C; call 0x6FA7E6; add esp,4` — and `0x6FA7E6` (disassembled
separately) is a small `push 1; push [esp+8]; call 0x6FD8CF; pop ecx; pop ecx; ret` helper (a
stack-probe wrapper) that is net-zero on the caller's `esp` once its call/ret and the
caller's `add esp,4` are accounted for. Tallying every push/pop from function entry (`esp0`) to
the read at `0x00531443` gives `esp0 - 32` at that point, so `[esp+0x24]` = `esp0 - 32 + 0x24` =
`esp0 + 4` — **the first stack argument at function entry**, before any of this function's own
pushes. Per this codebase's own established calling convention (`OdysseyEffects.cs`'s existing
`effectCtor(implementer, cachedRoutine, nParams)` calls, already confirmed live in the §6 pass),
that slot is `cachedRoutine` — the routine number the VM used to invoke this handler.
Cross-checked against `ExecuteCommandEffectMovementSpeedDecrease` (0x005332E0), which reads its
OWN one declared script parameter from the SAME NUMERIC OFFSET `[esp+0x24]` but only via an
explicit `call 0x5D1000` pop gated on `nParams==1` (`cmp eax,1` on the raw entry arg2, checked
BEFORE this function's own `/EHsc` prologue even runs) — a totally different code shape from
`ExecuteCommandEffectComplex`'s unconditional, un-gated read. The two functions sharing a
numeric offset is coincidence (different local-frame sizes); what matters is `EffectComplex`
never pops a VM-stack value before branching, while `MovementSpeedDecrease` always does. This
means `nComplexType` is not a real NWScript-visible parameter at all — it is simply "which
routine number got you here", and every one of the 7 targets is thus safe to invoke with
**zero pushed script arguments** (nothing to push, since nothing is ever popped for these cases).

Reading the actual case bodies at each jump target confirms this directly — none of the 7 pop
anything before writing the effect's type/sub-code and jumping to the shared epilogue at
`0x531781`:

```
0x0053147A  (130, Entangle)   mov dx,[0x7466C4]; mov [esi+8],dx;            jmp 0x531781   ; type 18, no subcode
0x005314FB  (134, Knockdown)  mov dx,[0x7466CC]; mov [esi+8],dx;            jmp 0x531781   ; type 20, no subcode
0x0053150B  (148, Paralyze)   mov ax,[0x74669C]; push 5; mov [esi+8],ax; push 0; jmp 0x53177A ; type 8, SetInteger(0,5)
0x0053151E  (154, Sleep)      mov cx,[0x74669C]; push 6; mov [esi+8],cx; push 0; jmp 0x53177A ; type 8, SetInteger(0,6)
0x00531532  (157, Confused)   mov dx,[0x74669C]; push 1; mov [esi+8],dx; push 0; jmp 0x53177A ; type 8, SetInteger(0,1)
0x00531546  (158, Frightened) mov ax,[0x74669C]; push 2; mov [esi+8],ax; push 0; jmp 0x53177A ; type 8, SetInteger(0,2)
0x00531559  (161, Stunned)    mov cx,[0x74669C]; push 4; mov [esi+8],cx; push 0; jmp 0x53177A ; type 8, SetInteger(0,4)
```

`0x53177A` calls `CGameEffect::SetInteger(effect, 0, subcode)` (`0x005036A0`, the sibling of
`GetInteger` at `0x00503690` already used elsewhere in this doc) then falls into the same
`0x531781` epilogue Entangle/Knockdown reach directly. The epilogue does creator-copy and
icon-push work off `esi`/`ebp` only — no `nParams`-dependent branch anywhere in it — so a call
with `nParams = 0` is correct for every one of these 7 cases, exactly like the true zero-param
constructors §3/§4 already ship.

### 7.3 Naming them — VERIFIED, three independent sources agreeing exactly

A K1 `nwscript.nss`'s DECLARATION ORDER assigns each native function a routine number
sequentially at compile time (the same fact `quest-sync.md` and `dialogue-consequences.md`
already used for `CreateObject`=243). Parsing one such file's ~877 top-level function
prototypes and indexing them 0-based:

| Anchor | Declaration-order index | Independently known value | Source |
| --- | --- | --- | --- |
| `GetFirstPC` | 548 | 548 | `min-one-hitpoint.md` |
| `SetMinOneHP` | 716 | 716 | `min-one-hitpoint.md` |
| `CreateObject` | 243 | 243 | `spawn.md` |
| `EffectAssuredHit` | 51 | 51 | THIS session's own `InitializeCommands` disassembly (§7.1) |
| `EffectAssuredDeflection` | 252 | 252 | THIS session's own `InitializeCommands` disassembly (§7.1) |

5-for-5, and the last two are not circular — they were derived from the RAW BINARY BEFORE the
`nwscript.nss` copy was ever consulted (§7.1's table-store disassembly), so their agreement is a
genuine independent check, not the same fact read twice. A third, fully independent, public
source — `xoreos-tools`' own community-maintained K1 routine table
(`src/nwscript/game_kotor.h`, fetched fresh from `github.com/xoreos/xoreos-tools` this session)
— was cross-checked against the same 7 indices and matches EXACTLY:

| Routine # | `nwscript.nss` declaration order | `xoreos-tools/game_kotor.h` | Agree? |
| --- | --- | --- | --- |
| 130 | `EffectEntangle` | `EffectEntangle` | ✅ |
| 134 | `EffectKnockdown` | `EffectKnockdown` | ✅ |
| 148 | `EffectParalyze` | `EffectParalyze` | ✅ |
| 154 | `EffectSleep` | `EffectSleep` | ✅ |
| 157 | `EffectConfused` | `EffectConfused` | ✅ |
| 158 | `EffectFrightened` | `EffectFrightened` | ✅ |
| 161 | `EffectStunned` | `EffectStunned` | ✅ |

Three independent methods (a fresh disassembly of this exact binary's own table-population
code; a K1 `nwscript.nss` copy's declaration order; a community-maintained public routine table
built independently for the `xoreos` NCS decompiler) agreeing on all 7 numbers is as strong a
naming as this document's own VERIFIED/INFERRED discipline recognizes. Combined with §2.2's
already-VERIFIED `(nComplexType, type, subcode)` triples, the final sub-code table is:

| Sub-code (`OnApplySetState`, type 8) | Routine # | Name | Status |
| --- | --- | --- | --- |
| 1 | 157 | `EffectConfused` | **VERIFIED** — not shipped (no `PeerEffectKind`; out of scope this pass) |
| 2 | 158 | `EffectFrightened` | **VERIFIED** — a genuinely DISTINCT native from sub-code 8 (see below); not shipped |
| 3 | — (dedicated routine, §3) | `EffectDroidStun` | already shipped as `PeerEffectKind.Stun` |
| 4 | 161 | `EffectStunned` (general, non-droid) | **VERIFIED** — not shipped; §3.1's open question ("does a separate non-droid stun exist among the unnamed sub-codes") is answered YES |
| 5 | 148 | `EffectParalyze` | **VERIFIED** — now shipped as `PeerEffectKind.Paralyze`, §7.4 |
| 6 | 154 | `EffectSleep` | **VERIFIED** — now shipped as `PeerEffectKind.Sleep`, §7.4 |
| 7 | — (dedicated routine, §3) | `EffectChoke` | already shipped |
| 8 | — (dedicated routine, §3) | `EffectHorrified` | already shipped as `PeerEffectKind.Frightened` — see the naming note below |
| 10 | — (dedicated routine, §3) | `EffectWhirlWind` | already shipped |

Direct-type effects (no sub-code): type 18 (`nComplexType` 130) = `EffectEntangle`, not on the
allowlist; type 20 (`nComplexType` 134) = `EffectKnockdown`, now shipped, §7.4.

**Naming correction: `PeerEffectKind.Frightened` (wire value 5, sub-code 8) is really
`EffectHorrified`, not `EffectFrightened`.** K1's nwscript.nss declares BOTH as separate native
effects — `EffectFrightened()` at routine 158 (sub-code 2, named for the first time this pass)
and `EffectHorrified()` at its own dedicated routine (sub-code 8, §3). The prior pass used
"Frightened" as an ENGLISH LABEL for sub-code 8 by semantic guess (its own comment already said
so: "VERIFIED (dedicated VM routine, name literally says Horrified)"), before either the real
`EffectFrightened` or `EffectHorrified`'s distinctness was knowable. The NUMERIC identity shipped
under `PeerEffectKind.Frightened` (sub-code 8) is unchanged and correct; only the English label
was ever a guess, and it is being left as-is (not renamed) to avoid a wire-breaking change to an
already-shipped, live-tested kind — see the XML doc notes on `PeerEffectKind.Frightened` and
`PeerEffectKind.Sleep` (`KotorCoop.Protocol/PeerEffectKind.cs`). The real `EffectFrightened`
(sub-code 2) has no wire kind of its own; naming five more constructors and wiring three more
peer-effect kinds was out of this pass's scope (task asked for Paralyze/Sleep/Knockdown
specifically), but the mapping is now fully known for whoever picks it up next.

### 7.4 Implementation

`OdysseyEffects.cs` gained `ApplyComplexEffect(objectId, routineIndex, ref cachedRoutine,
durationMs, out failure)` — the same push-args/construct/apply choreography
`ApplyZeroParamEffect` already uses, with one deliberate difference: it does NOT use
`FindRoutine`'s first-match linear table scan. `FindRoutine` would resolve to routine 51
(`EffectAssuredHit`, the LOWEST-numbered of the 17+ aliases) for Knockdown/Paralyze/Sleep alike —
silently building the wrong effect for all three. Instead it verifies the SPECIFIC, VERIFIED
routine index directly: `table[routineIndex] == ExpectedEffectComplex` (`0x005313F0`), refusing
(logged no-op, failure code 3) if it does not — the same "wrong offset is a refusal, not a
guess" discipline `engine-write-rules` calls for, applied to a multiply-aliased handler instead
of a single-address one. `PeerEffectRules.HasApplyConstructor` now returns `true` for all nine
allowlist members; `PeerEffectRules.TryMapAllowlist` now accepts sub-codes 5 (Paralyze) and 6
(Sleep) in addition to the existing 3/7/8/10.

**Known asymmetry, left deliberately unresolved this pass — CLOSED the same session (§8).**
`PhantomEffectHook.cs` (the NativeAOT plugin file that actually runs during a live capture)
carries its OWN hand-copied allowlist, and this pass's task explicitly excluded editing it ("a
rig agent owns them"). Knockdown's `case 20:` entry there ALREADY existed before this pass (its
capture path is untouched and already proven live, §6: "refused: no apply-side constructor for
this kind yet" — routing worked, only the constructor was missing), so Knockdown was fully wired
end to end by this pass alone. Paralyze/Sleep's sub-codes 5/6 were NOT yet in
`PhantomEffectHook.cs`'s copy at the moment this paragraph was first written — only
`PeerEffectRules.cs` (protocol side, used by tests) had them. **That gap did not survive the
session**: commit `5f1975e` ("The capture allowlist learns the two sub-codes the constructors
just named"), one minute after the commit that added this section, added the matching `case 5:`
/ `case 6:` lines to `PhantomEffectHook.cs`. This document's prose was never updated to say so
until the 2026-08-24 pass (§8) read the actual file and found both copies already agreed. The
two-line change described below as "left for whoever next owns that file" was already made —
nothing left to wire.

### 7.5 Live-test additions (not run this pass — static analysis only) — superseded by §8

The next live pass, per the task's request to describe forward/local additions, ORIGINALLY read
as below. **This was written before §7.4's asymmetry was found (2026-08-24, §8) to have already
been closed by commit `5f1975e` within the same 2026-08-22 session** — `PhantomEffectHook.cs`
already maps sub-codes 5/6, so the phantom-aimed rows this section predicted would show "nothing
captured" are now expected to behave exactly like Knockdown's row: captured, forwarded, and
applied on the peer. Left below as the historical record of what the doc used to (incorrectly)
expect; §8 has the corrected staging list.

```
applyeffect knockdown              (forward AND reverse should now APPLY — capture already worked)
applyeffect paralyze  "" 0 0 me    (local-only: proves the constructor; use "me"/an explicit id)
applyeffect sleep     "" 0 0 me    (local-only, same reason)
applyeffect paralyze                (aimed at a phantom: ORIGINALLY expected to still show nothing
applyeffect sleep                   captured/forwarded on the OUT ring — that expectation was
                                     WRONG, §8: PhantomEffectHook.cs already had the case 5/6
                                     lines by the end of the 2026-08-22 session)
```

`effects` on both sides should show `knockdown`, `paralyze` and `sleep` rows all behaving exactly
like the six kinds §6 already proved (`captured/forwarded` on the sender, `applied` on the
receiver, for both forward and reverse) — see §8 for the corrected staging steps and what still
needs a live pass to confirm.

---

## 8. This document's own gap closed: a wiring bug that was already fixed, undocumented (2026-08-24)

This session's task was to fix the "Knockdown/Paralyze/Sleep are captured but never applied"
gap the roadmap still pointed at. Reading this document plus a fresh read-through of the actual
source (no game run, no memory touched — a code/doc audit only) found that gap had already been
closed, twice over, inside the 2026-08-22 session that produced §7:

- **Apply-side constructors** (`OdysseyEffects.ApplyEffect`, `src/KotorCoop.Plugin/OdysseyEffects.cs`):
  all nine allowlist kinds have a real constructor call, including `ApplyComplexEffect` for
  Knockdown/Paralyze/Sleep (routines 134/148/154 through the shared `ExecuteCommandEffectComplex`
  handler, §7.2–§7.4) — committed `19a5a63`, unchanged this pass.
- **Protocol-side allowlist and constructor registry** (`PeerEffectRules.cs`,
  `src/KotorCoop.Protocol/PeerEffectRules.cs`): `TryMapAllowlist` maps sub-codes 5/6, and
  `HasApplyConstructor` returns `true` for all nine — committed `19a5a63`, unchanged this pass.
- **The plugin's own capture-side allowlist** (`PhantomEffectHook.cs`,
  `src/KotorCoop.Plugin/PhantomEffectHook.cs`) — the piece §7.4 said was "intentionally left for
  whoever next owns that file" — turned out to have ALREADY been given `case 5:`/`case 6:` by
  commit `5f1975e` ("The capture allowlist learns the two sub-codes the constructors just
  named"), timestamped one minute after the commit that added §7's constructors. Nobody
  ever came back and updated this document's §7.4/§7.5 prose to say so, so the document kept
  describing a gap that no longer existed in the code — the actual bug this pass fixes is in
  `effect-sync.md` and in a stale doc-comment in `PeerEffectRules.cs`, not in the plugin.

**What this pass actually changed:**

1. `src/KotorCoop.Protocol/PeerEffectRules.cs` — replaced the "⚠ KNOWN DESYNC (2026-08-22)"
   doc-comment (which asserted `PhantomEffectHook.cs` was still missing the case 5/6 lines) with
   an accurate note that the desync closed within the same session, one commit later.
2. This document's §7.4 and §7.5 — corrected in place (marked superseded rather than deleted, so
   the historical record of what was believed at the time is preserved) rather than silently
   rewritten, per this codebase's convention of not editing away a wrong prior conclusion without
   a trace.
3. No source-code behavior changed. `OdysseyEffects.cs`, `PhantomEffectHook.cs`,
   `PeerEffectRules.cs`'s `TryMapAllowlist`/`HasApplyConstructor` bodies, `PeerEffectKind.cs`
   (both copies) and `CheatController.ApplyEffect` were read in full and are already correct as
   of `5f1975e`/`19a5a63` — nothing to wire.

**Test coverage — already present, re-verified this pass, no gaps found:**

- `tests/KotorCoop.Tests/PeerEffectRulesTests.cs`: `MapsEveryVerifiedTypeAndSubCode` and
  `RefusesEverythingNotOnTheAllowlist` cover sub-codes 5/6 by name (Paralyze/Sleep) alongside
  every other allowlist member; `ApplyConstructorCoverageMatchesEffectSyncMd` and the dedicated
  `AllNineAllowlistMembersHaveAnApplyConstructor` fact pin all nine kinds — including Knockdown,
  Paralyze, Sleep — as having a real constructor, so a future regression (someone reverting one
  of the three §7 cases, in either `PeerEffectRules.cs` or, by proxy, an un-caught drift in
  `PhantomEffectHook.cs`'s hand-copy) fails a unit test rather than only showing up live.
- `tests/KotorCoop.Tests/EffectVerbsTests.cs`: `EveryAllowlistKindParsesCaseInsensitively` covers
  the `applyeffect knockdown|paralyze|sleep` console forms; the dedicated
  `KnockdownParalyzeAndSleepReachThePluginWithTheRightKind` theory pins the dispatcher routing
  (parsed line → `CheatRequest` with the right `PeerEffectKind` in `Amount`) for exactly the
  three kinds this document is about.
- `OdysseyEffects.cs` and `PhantomEffectHook.cs` themselves remain untested by design — both are
  `internal static unsafe class`es that poke live engine memory/VM state and cannot run outside
  a live `swkotor.exe` process, the same reason `PhantomHitHook`/`Odyssey.cs`'s own VM-call code
  has never had a unit test either. The project's split (unsafe engine code calls into a pure,
  fully-tested decision layer — `PeerEffectRules.cs`) is exactly the pattern that makes the
  numeric/routing side of this fully testable while leaving only the engine-call plumbing for a
  live pass. No test was added for `ApplyComplexEffect`/`TryMapAllowlist`'s plugin-side copy
  beyond what already existed, because there was nothing new to cover — see NEEDS-LIVE below for
  what a live pass still owns.
- `dotnet test` (repo root, all projects): re-run this pass, all green, no regressions. See the
  commit message for the exact pass/fail counts at the time of this commit.

**NEEDS-LIVE — unchanged from §7, restated so it isn't lost under the doc fix above:**

1. **Knockdown/Paralyze/Sleep have never actually been fired end-to-end on a live game.** §6's
   live pass covered the six kinds that existed before §7; §7 and this pass are static analysis
   only ("no game was run, no game function was called," carried forward from this document's
   own header rule). The `ApplyComplexEffect` call path (§7.4 in `OdysseyEffects.cs`) — pushing
   the VM stack args, invoking `ExecuteCommandEffectComplex` at the verified routine index, then
   `ApplyEffectToObject` — has the exact same SHAPE as the six already-live-verified
   `ApplyZeroParamEffect`/`ApplySlowEffect` calls, but shape-matching a verified pattern is not
   the same as having run it.
2. **The routine-index verification itself** (`table[134/148/154] == ExpectedEffectComplex`, the
   refusal path in `ApplyComplexEffect`) has never been exercised against the live table — if
   this refuses in practice, `applyeffect knockdown/paralyze/sleep` will log `failure 3` on a
   real run and that needs to be seen at least once to trust the "safe to invoke with zero pushed
   arguments" conclusion (§7.2) under real VM state, not just static stack arithmetic.
3. **Corrected staging steps for the next live pass** (supersedes §7.5's original list, which
   wrongly expected the phantom-aimed Paralyze/Sleep rows to show nothing captured):
   ```
   applyeffect knockdown              (local: me/explicit id — constructor smoke test)
   applyeffect paralyze
   applyeffect sleep
   applyeffect knockdown              (blank target = nearest phantom — the full forward test)
   applyeffect paralyze
   applyeffect sleep
   ```
   Expected, if §7's static analysis holds: all six local calls APPLY (no more "no apply-side
   constructor" refusal); all three phantom-aimed calls show `captured/forwarded` in `effects`
   on the sender and `applied` on the receiver, exactly like the six kinds §6 already proved live
   — i.e. these three finally join that table with a `Forward`/`Reverse`/`Local` row of their
   own, closing the one gap §6's table left open.
4. **The visible animation/pose and "can the target act" questions** §3.3 originally asked for
   Paralyze/Sleep specifically remain open — naming the sub-codes (§7.3) answered "which routine
   is which" from static/public-source cross-checks, not "what does it look like when it lands."
   Worth a look during the same live pass, opportunistically, once `effects` confirms the packet
   crossed.

## 9. Live-verified (2026-08-24): Knockdown/Paralyze/Sleep fire end-to-end for the first time —
matches §7's static analysis exactly, item 1-2 of §8's NEEDS-LIVE closed

Ran the corrected §8 staging recipe on a fresh `demo.ps1` pair (both GOG installs rebuilt/
reinstalled this session, `effect_sync=1` already on in both `coop.ini`s by default), instance 1
(pid 41900, "Lanik") as sender, instance 2 (pid 8080, "Malak") as receiver, `END_M01AA`, via
`tools/blockpoke.ps1 console`.

**Local smoke test** (`applyeffect knockdown me`, `applyeffect paralyze me`, `applyeffect sleep
me`, each on instance 1 against itself): all three now answer `applied kind N to 0x7FFFFFFF
(0x7FFFFFFF), 6000ms, mag 0` — the previously-expected `no apply-side constructor for this kind
yet` refusal is gone, confirming §7's `ApplyComplexEffect` constructor call really is live and
well-formed under real VM state (closes §8 NEEDS-LIVE item 2 — the routine-index table lookup at
134/148/154 does not refuse).

**Phantom-aimed test** (same three, blank target = nearest phantom, id `0x131`): all three answer
`applied kind N to the nearest phantom (0x131), 6000ms, mag 0` on the TYPING side (instance 1).
`console 'effects'` and the agent's own readout log then confirmed the full round trip on both
ends:

```
instance 1 (sender, agent1.log):
  captured/forwarded knockdown subject 0x131 dur 6000ms mag 0 serial 1
  captured/forwarded paralyze  subject 0x131 dur 6000ms mag 0 serial 2
  captured/forwarded sleep     subject 0x131 dur 6000ms mag 0 serial 3

instance 2 (receiver, agent2.log):
  applied knockdown subject 0x1 dur 6000ms mag 0 serial 1
  applied paralyze  subject 0x1 dur 6000ms mag 0 serial 2
  applied sleep     subject 0x1 dur 6000ms mag 0 serial 3
```

Serials line up 1:1 sender to receiver for all three kinds, subject ids resolve correctly on each
side (the phantom id on the sender, the receiver's own local player id `0x1` on the receiver) —
exactly the same shape §6 already proved live for the six previously-verified kinds. This closes
§8 NEEDS-LIVE item 1: Knockdown/Paralyze/Sleep have now actually been fired end-to-end on a live
game, not just shape-matched against a verified pattern. All nine allowlist kinds are now
VERIFIED (live), both directions, joining the §6 table.

Not chased this pass (§8 NEEDS-LIVE item 4, visible animation/pose): the receiver's on-screen pose
was not screenshotted or visually confirmed frame-by-frame this round — `effects`/the agent log's
`applied` line is the evidence gathered, matching this document's own established bar for the six
kinds §6 already closed the same way. Left open for whoever next wants the visual confirmation
specifically.

`dotnet test`: no production code changed this pass (a pure live-verification run against
already-shipped code) — see this round's own commit for the full-suite pass/fail count.

---

## 10. Heal family + poison/AoE analysis (2026-09-02)

Static analysis only, same discipline as every prior round of this document (`tools/disasm.py`,
`tools/xrefs.py`, `tools/q.py` against `kotor1_0_3.db`, direct reads of the binary's data
section — nothing modified, no game run, no game function called). Two tasks: (1) ship the
heal-family kinds the roadmap named but this document never covered, and (2) settle whether
poison/AoE damage needs a new capture path or already rides the existing one.

### 10.1 Heal (type 39) and TemporaryHitpoints (type 15) — VERIFIED, shipped

`tools/q.py func Heal` / `func Temporary` name the two handlers directly from the symbol DB (a
stronger source than the sub-code inference §7 needed — these are the engine's own dispatch-table
entries, already listed in §5's table): `CSWSEffectListHandler::OnApplyHeal` (`0x004E0750`, type
39) and `CSWSEffectListHandler::OnApplyTemporaryHitpoints` (`0x004D92D0`, type 15).

Their VM constructors were disassembled fresh:

- **`CSWVirtualMachineCommands::ExecuteCommandEffectHeal`** (`0x00532A70`) — VERIFIED single
  `int nDamageToHeal` parameter. The function reads its OWN raw stack arg2 (`[esp+0x14]` before
  the SEH prologue, `esp0+8` by the same stack arithmetic §7.2 used) and `cmp`s it against `1`,
  refusing up front (`mov eax, 0xFFFFF82F; ret 8`) if the caller's declared param count is not
  exactly 1 — a stronger, earlier gate than Slow's own constructor has. If the gate passes, it
  pops one int via the shared VM stack-pop helper at `0x005D1000` (itself a thunk to
  `0x005D24C0` — VERIFIED by disassembly: this helper checks a stack-depth counter and the
  top-of-stack's TYPE TAG byte before popping, returning 0 rather than corrupting anything on
  underflow or a type mismatch — the real safety net, independent of any per-constructor gate).
  If the popped value is `> 0` it writes effect type 39 (global `0x746718`, read directly:
  **VERIFIED = 39**) and calls `SetInteger(effect, 0, value)` (`0x005036A0`); otherwise it writes
  the SHARED null/no-op type (global `0x74667C`, read directly: **VERIFIED = 0**) — a
  non-positive heal amount silently becomes an unregistered effect type, which
  `OnEffectApplied`'s own `type <= 0` / null-table-entry checks (§1) already refuse harmlessly.
  Routine number **78** — VERIFIED by disassembling `InitializeCommands`' table-store site
  (`0x0054D0C9: mov dword ptr [edx+0x138], 0x532a70`; `0x138/4 = 0x4E = 78`).
- **`CSWVirtualMachineCommands::ExecuteCommandEffectTemporaryHitpoints`** (`0x00534490`) —
  VERIFIED single `int nHitPoints` parameter, same one-int-pop shape via the same `0x005D1000`
  helper (no separate up-front `cmp nParams,1` gate visible for this one specifically, but the
  pop itself is internally guarded exactly the same way, so this is equally safe to call).
  Positive value → effect type 15 (global `0x7466B8`, **VERIFIED = 15**) + `SetInteger(0,
  value)`; non-positive → the same shared null type. Routine number **314** — VERIFIED
  (`0x0054D1ED: mov dword ptr [edx+0x4e8], 0x534490`; `0x4E8/4 = 0x13A = 314`).

`tools/xrefs.py` on both constructor addresses finds exactly **one** address-literal occurrence
each (unlike `ExecuteCommandEffectComplex`'s 20+ aliases) — confirming `FindRoutine`'s ordinary
first-match linear scan is safe for both, no `ApplyComplexEffect`-style specific-index lookup
needed.

Both match §4's already-established "one declared `int` parameter" shape exactly (the same
shape `MovementSpeedDecrease`/Slow already ships and has live-verified) — implemented as
`OdysseyEffects.ApplyOneIntParamEffect`, the same push/construct/apply choreography
`ApplySlowEffect` uses, factored out separately so that already-tested method is untouched.
**Heal applies INSTANT** (like ForcePush — healing lands once, it is not an ongoing state);
**TemporaryHitpoints applies DURABLE** (like Slow — a temporary buff that should actually last
the requested duration). Neither magnitude has a natural engine-side upper bound the way Slow's
percent does, so the clamp (`1..999`, default `20`) is a sanity bound against a corrupted or
malicious wire value, not a measured game figure — flagged NEEDS-LIVE below.

**Direction, per §1**: the hook captures effects the LOCAL engine applies TO a phantom and
forwards them to that phantom's owner. A medpac, `Cure`, or `Heal` cast BY the local player ON a
peer's phantom (host heals a client's phantom, or vice versa) lands on the phantom exactly like a
hostile effect would, gets captured by `PhantomEffectHook` (already extended to types 39/15/
sub-code 1 below), forwarded, and replayed on the peer's REAL character via
`OdysseyEffects.ApplyEffect` — real HP change, not a cosmetic. `OnApplyACIncrease` (type 48) was
read from the same §5 table but is deliberately **not** shipped: a stat buff applied to a
phantom (which nothing else reads) has no real-body counterpart worth forwarding, unlike HP.

> **Correction, 2026-09-19 (`partner-buffs.md`):** the medpac/Cure/Heal sentence above is only
> half right. A medpac heals its user only; Heal and Cure loop the party roster; only the Valor
> family tests friendship. A cast never "lands on" a stand-in unless it is a friend-sphere buff, so
> the forwarding described here is reachable by the staging verb, not by ordinary play.

`PeerEffectKind.Heal = 10`, `PeerEffectKind.TemporaryHitpoints = 11` (`KotorCoop.Protocol` and
its `PeerEffectKindValues` plugin-side mirror); `PeerEffectRules.TryMapAllowlist` maps engine
type 39/15 directly (no sub-code involved, same shape as Knockdown/Slow/ForcePush);
`PhantomEffectHook.cs`'s own copy updated in the same commit (both copies changed together this
pass — no repeat of §7.4's temporary desync). The magnitude capture in `PhantomEffectHook.OnEffect`
(previously only read for type 29/Slow) now also reads `integer_list[0]` for types 39 and 15 —
same field, same bounded read, three different meanings.

### 10.2 Confused (type 8, sub-code 1) — VERIFIED, shipped

§7.3 already named sub-code 1 as `EffectConfused` (routine 157) through the same three-way
cross-check (fresh `InitializeCommands` disassembly, `nwscript.nss` declaration order, the
`xoreos-tools` public routine table) that named Knockdown/Paralyze/Sleep/Stunned/Frightened, but
explicitly left it unshipped ("out of this pass's scope"). It is mechanically IDENTICAL to
Knockdown/Paralyze/Sleep — same shared `ExecuteCommandEffectComplex` handler (`0x005313F0`), same
zero-pushed-argument call shape (the handler dispatches on the CALLING routine number, not a
popped script value — §7.2), same `table[157] == ExpectedEffectComplex` specific-index safety
check `ApplyComplexEffect` already performs for the other three. Adding it was a four-line change
(a `RoutineEffectConfused = 157` constant, a cached routine field, a `case` in `ApplyEffect`'s
switch reusing the existing `ApplyComplexEffect` method verbatim, and a `case 1:` in both
allowlist copies) — no new mechanism, no new risk beyond what §7 already live-verified for the
other three complex-family members. `PeerEffectKind.Confused = 12`.

### 10.3 Poison (type 35) — VERIFIED shared entry point, INFERRED conclusion: no new kind needed

**The question**: does a poison tick re-enter `OnApplyDamage` (`0x004DFA40`, the choke point
`PhantomHitHook` already hooks — combat-participation.md §4.2, "every damage event in the
game"), or does it write HP some other, more direct way that would leave phantom-sourced poison
unforwarded?

**`Global::OnApplyPoison`** (`0x004DB320`, the type-35 dispatch-table entry) — VERIFIED: a large
(~0x4D0-byte) one-time setup routine. It posts poison-state icons/flags through the same
"set state" helper (`0x004D7780`) §2.1 already documents for the `OnApplySetState` family, then
— near its end — calls `CSWSCreature::ApplyPoisonDamage` directly:

```
0x004DB638  push 0x3f800000     ; 1.0f (severity/tick multiplier)
0x004DB63D  push 0
0x004DB63F  push esi            ; the poison CGameEffect itself
0x004DB640  push ebx            ; creator/attacker id
0x004DB641  mov ecx, edi        ; ecx = the poisoned creature
0x004DB643  call 0x4ee770       ; CSWSCreature::ApplyPoisonDamage
```

**`CSWSCreature::ApplyPoisonDamage`** (`0x004EE770`) — VERIFIED: builds a genuine `CGameEffect`
of **TYPE 38** (Damage) — confirmed by reading the type-constant global it writes directly:
`0x746714 = 38` — filling the SAME `integer_list` slot layout `combat-participation.md`
documents for `EffectDamage` (`SetInteger` at indices `0, 0xE, 0x10, 0x11, 0x12, 0x15`), then
hands it to:

```
0x004EECB2  call 0x4d14f0       ; CSWSObject::ApplyEffect
```

**`CSWSObject::ApplyEffect`** (`0x004D14F0`) — VERIFIED as the SAME function
`ApplyEffectToObject` (`0x0052E570` — the VM's own script-facing "apply this effect" wrapper,
already called by every `OdysseyEffects.ApplyEffect` constructor this document ships) itself
calls, at its own `0x0052E6D6: call 0x4d14f0`. Both call sites were read directly from the
instruction stream. This makes `CSWSObject::ApplyEffect` the single canonical "give this object
an effect" entry point in the engine — melee/ranged combat damage (already proven LIVE to reach
`OnApplyDamage`/`PhantomHitHook`, §6/§9) and poison's own tick both build a type-38 effect and
hand it to this exact function.

**What was NOT independently single-stepped**: the one remaining hop, `CSWSObject::ApplyEffect`'s
own dispatch through the `CSWSEffectListHandler` vtable (`OnEffectApplied` is slot 2 — VERIFIED:
the vtable at `0x744274` has `0x4D8320` at `+8`, `(0x74427C-0x744274)/4 = 2`). A grep of every
`call dword ptr [reg+off]` across a ~2200-byte disassembly of `CSWSObject::ApplyEffect` and its
immediate neighbors found offsets `0x10, 0x30, 0x94, 0x98, 0x9C, 0xD8, 0xDC` — never `+8` — so
the actual vtable hop happens somewhere this pass's static reach did not cover (a deeper nested
call, or a helper `ApplyEffect` calls that this pass did not expand). Every other fact matches
(same effect type, same integer layout, same single apply entry point already proven to carry
live-verified damage) but this specific hop is graded **INFERRED**, not VERIFIED, for that
reason — high confidence, not a completed trace.

**Practical answer**: no new `PeerEffectKind` is proposed for Poison. If the INFERRED conclusion
holds, a poison tick landing on a peer's phantom is **already forwarded today, for free**,
through the existing, already-shipped, already-live-verified `PhantomHitHook`/damage channel —
there is nothing to build. **NEEDS-LIVE**: poison a phantom (a poison-type hostile, or a
synthetic `EffectPoison` staged the way §3.3 describes for the unnamed sub-codes) and confirm the
owner's `combat: taken N` ledger fires per tick the same way a weapon hit does. If it does not,
this conclusion is wrong and poison needs its own capture path after all — but nothing here
should be trusted as shipped until that check runs.

### 10.4 AreaOfEffect (type 31) — VERIFIED: not a "landed effect", out of scope by design

**`CSWSEffectListHandler::OnApplyAreaOfEffect`** (`0x004DADE0`) — VERIFIED: does not apply
anything to a creature. It allocates and constructs a `CSWSAreaOfEffectObject` (`0x290` bytes,
`call 0x594480`) and sets its enter/heartbeat/exit NWScript names — read via
`CGameEffect::GetString` at integer_list-adjacent string indices 0/1/2 (`call 0x503700`) into the
new object's own `+0x270`/`+0x260`/... slots (`call 0x5e5c50`). In other words, AreaOfEffect
spawns a persistent trigger VOLUME whose enter/heartbeat/exit are ordinary NWScript callbacks the
VM runs like any other script event, per creature that enters/lingers/exits it. Whatever those
scripts do — apply damage, apply a state effect, whatever the specific AoE spell's author wrote —
happens through the SAME generic `ApplyEffectToObject`/`OnEffectApplied`/`OnApplyDamage` channels
already covered by `PhantomHitHook`/`PhantomEffectHook`.

**Not added as a `PeerEffectKind`, and no periodic-model design proposed** — this is not a gap
needing one. AreaOfEffect itself carries no discrete magnitude/duration a phantom "feels" the way
Stun or Heal does; its `target` argument names the zone/area, not a creature something landed on.
It is simply outside the "one effect lands on a phantom" model this whole channel is built
around, and whatever it eventually triggers already rides the existing generic dispatch.

### 10.5 What shipped this pass

- `PeerEffectKind` (`KotorCoop.Protocol`) / `PeerEffectKindValues` (`KotorCoop.Plugin`): `Heal =
  10`, `TemporaryHitpoints = 11`, `Confused = 12`.
- `PeerEffectRules.TryMapAllowlist` / `PhantomEffectHook.TryMapAllowlist` (both copies, same
  commit): type 39 → Heal, type 15 → TemporaryHitpoints, type 8 sub-code 1 → Confused.
- `PeerEffectRules.HasApplyConstructor`: all twelve kinds now `true`.
- `PhantomEffectHook.OnEffect`'s magnitude capture: extended from type 29 only to types
  `29, 39, 15`.
- `OdysseyEffects.ApplyEffect`: three new `case`s — Heal/TemporaryHitpoints through the new
  `ApplyOneIntParamEffect` (same choreography as `ApplySlowEffect`, factored out separately so
  that already-live-tested method is untouched), Confused through the existing
  `ApplyComplexEffect` (routine 157).
- `CheatController.World.ApplyEffect`'s range check and refusal message; `ConsoleCommand.Effects`'s
  `applyeffect` usage string — both now name all twelve kinds and accept `1..12`.
- No `SharedBlock` field, version bump, or `PacketType` change — the wire `Kind` byte
  (`PacketCodec.Effects.cs`) already had 246 unused values; the peer-effect ring's shape
  (`SharedBlock.Combat.cs`) is unchanged. This is purely "more kinds fit through the same pipe."
- Tests: `PeerEffectRulesTests` gained allowlist-mapping rows for (8,1)/(39,0)/(15,0), apply-
  constructor-coverage rows for the three new kinds, and explicit refusal rows for ACIncrease
  (48), Poison (35) and AreaOfEffect (31) documenting they are deliberately not on the allowlist;
  `AllNineAllowlistMembersHaveAnApplyConstructor` renamed
  `EveryAllowlistMemberHasAnApplyConstructor` (no longer nine). `EffectVerbsTests` gained parser
  and dispatcher-routing coverage for `heal`/`temporaryhitpoints`/`confused`.
  `PacketCodecTests.PeerEffectRoundTripsWithHealKind` pins the wire `Kind` byte at a value above
  9 for the first time (every prior kind was single-digit).

### 10.6 NEEDS-LIVE

1. **Heal/TemporaryHitpoints/Confused have never been fired end-to-end on a live game** — static
   analysis only this pass, same caveat every earlier round of new kinds carried before its own
   live pass (§6, §9). Shape-matches already-live-verified patterns (`ApplySlowEffect` for the
   first two, `ApplyComplexEffect` for the third) but that is not the same as having run it.
2. **The magnitude clamp (`1..999`, default `20`) for Heal/TemporaryHitpoints is not a measured
   figure** — worth checking against what a real medpac/Cure/stim actually heals in this game
   (`spells.2da`/item property values) so the default is a plausible number, not just a safe one.
3. **Poison's INFERRED conclusion (§10.3)** — the practical claim that poison ticks already
   forward through the existing damage channel needs the live check described there before
   anyone treats it as confirmed.
4. **Confused's visible effect** (what the target's screen/animation does, whether it can still
   act) was not investigated — out of scope for a wiring pass, same as §3.3/§8 left open for
   Paralyze/Sleep's own visible confirmation.

### 10.7 Live recipe for the next pass

Same staging verbs as §6/§8/§9 (`applyeffect`/`effects`, `tools/blockpoke.ps1 <pid> console
'...'`), extended to the three new kinds:

```
applyeffect heal me 0 30                 (local: heals the caster's own player by 30 — proves
                                           the constructor without touching the network)
applyeffect temporaryhitpoints me 8000 20 (local: 20 temporary HP for 8s)
applyeffect confused me                   (local smoke test)

applyeffect heal 0 30                     (blank target = nearest phantom, the full forward
                                           test — a "medpac applied to a peer's phantom" stand-in)
applyeffect temporaryhitpoints 8000 20
applyeffect confused
```

**What proves the real body's HP rose** (task's own question): `applyeffect heal` is the verb;
there is no bespoke "HP rose" log line — `PeerEffectController.ApplyIncoming`'s existing generic
line (`effect: applied kind 10 from peer 0x{id} (duration {durationMs}ms, magnitude {magnitude})`,
`src/KotorCoop.Plugin/PeerEffectController.cs`) only confirms the packet arrived and the
constructor call did not refuse — it does not itself read HP. The actual HP change has to be read
directly, the same way §6's damage-regression check did (`"the client's real health dropped
12/12 -> 5/12"`): `tools/sethp.ps1 <receiverPid> 0x7FFFFFFF` before and after the sender's
`applyeffect heal` (object id `0x7FFFFFFF` is the local player on every instance — the same
constant `ConsoleCommand.Effects.cs`'s own `me`/`self` resolution already uses). A successful
live pass reads a HIGHER number after than before, bounded by the character's max HP (healing
past full should clamp, not overflow — worth confirming as part of the same pass).

## 11. Live 2026-09-03 00:37–00:58: all 12 kinds both directions, then a replay-on-arrival defect found and closed

### 11.1 The three §10 kinds, live, both directions (§10.6 item 1 closed)

Rig: `tools/demo.ps1 -Save 1`, host and client in end_m01aa, `applyeffect <kind>` with a blank
target (= the nearest phantom, i.e. the peer's real body on the other instance).

- **Host → client** (00:37–00:38): host ran `applyeffect heal`, `applyeffect temporaryhitpoints`,
  `applyeffect confused`; client plugin logged `effect: applied kind 10 from peer 0x1 (duration
  6000ms, magnitude 20)`, then kind 11, then kind 12.
- **Client → host** (00:43–00:44, fresh session): the same three the other way; host plugin logged
  kind 10, 11, 12 `from peer 0x2`.
- Repeated 00:56 (the fix build, see 11.3): `[00:56:16.222] kind 10`, `[00:56:21.376] kind 11`,
  `[00:56:26.485] kind 12` on the client; `[00:56:31.737] kind 10` on the host.

So every kind the mod forwards (12/12) has now been observed landing on the peer's real body in
both directions. Heal's HP rise was not read with `sethp.ps1` this pass (the confused stagger and
the floaty text on the receiving screen were the visible evidence); §10.7's HP read stays a
worthwhile follow-up but the constructor path is no longer in question.

### 11.2 DEFECT: the IN ring was replayed onto the fresh character at every module arrival

Found while warping after the smoke test. The old `PeerEffectController.Invalidate()` (called on
every world-epoch change from `PluginHost.SessionLifecycle`) did `Array.Clear(_appliedSerial)`,
but nothing clears the shared block's IN ring — the agent's `TryWritePeerEffectIn` is round-robin
and an entry stays readable until its slot is reused. So on arrival in each new module
`ApplyIncoming` saw every slot as "serial != 0 and != applied" and re-ran the whole ring:

```
client  [00:48:02.359] effect: applied kind 10/11/12 from peer 0x1   ← arrival in tar_m10aa
client  [00:48:36.859] effect: applied kind 10/11/12 from peer 0x1   ← arrival back in end_m01aa
host    [00:48:35.653] effect: applied kind 11, 12, 10, 10 from peer 0x2 (+ kind 10 at 35.654)
```

Minutes-old heals, a temporary-hp buff and a 6 s *confused* re-applied to a character that had
just loaded, once per transition, forever. In a real game that is a stagger on every door after
someone was ever confused near you.

A second, latent defect in the same latch: it compared the SERIAL only. Serials are per-sender
counters (`++_peerEffectSequence` in `CoopClient.Effects.cs` / `CoopHost.Effects.cs`), so with
three players slot *i* can legitimately hold peer A's #5 and later peer B's #5, and the old
latch would have swallowed the second.

### 11.3 Fix — adopt at the edge, key by (origin, serial); verified 2/2 arrivals clean

`PeerEffectReplayRule` (new, pure, `tests/.../PeerEffectReplayRuleTests.cs`, 5 tests):

- `Key(origin, serial)` = `origin << 32 | serial`; the per-slot latch is now a `ulong[] _appliedKey`.
- `Adopt(origin, serial)`: what the latch becomes at a world edge — whatever the slot holds. It
  was captured for the world being torn down; it is marked applied without being run. Only an
  entry written AFTER the edge is new.
- `IsNew(latch, origin, serial)`: `serial != 0 && Key != latch`.

`Invalidate()` now walks `block.GetPeerEffectIn(i)` and adopts each slot instead of clearing.

Live 00:56–00:58 (fix build in both installs, byte-identical): three effects host→client and one
client→host at 00:56:16–00:56:31, then host `warp tar_m10aa` (client epoch edges 00:56:47 and
00:56:57, arrival with `party count` untouched) and host `warp end_m01aa` (edge 00:57:33). **No
`effect: applied` line on either side after 00:56:31** — where the previous build logged three
on each client arrival and five on the host's.

### 11.4 One-off client crash: a heal on a phantom drew a floaty for a node the phantom does not have (1/4, staging verb only)

00:38:44, the client (post-plugin-route-rejoin, after two warps, having just received the three
effects above) ran `applyeffect heal` on the nearest phantom and died. Persisted crash record
(`tools/crashbin.ps1 -GameDir 'D:\Games\swkotor-gog-p2'`): `access violation at 0x688F54`.

Symbolicated chain: `CSWSEffectListHandler::OnApplyHeal` (0x4E0750, return 0x4E08FB) →
`CClientExoApp::AddFloatyText` (0x5EDEA0) → `CSWGuiMainInterface::AddFloatyText` (0x68B7C0,
return 0x68B82B) → `CSWGuiFloatyText::CreateEntry` (0x688E10), which does
`edi = [vtbl+0x98](0xff, 1)` on the healed object's client object (a node lookup for where to
hang the floaty) and then `mov edx,[edi]` with edi = NULL. `OnApplyHeal` draws the floaty when the
healed object is `CClientExoApp::GetLastTarget` (0x5EDD80) or has a `CSWParty::GetIndex`
(0x6349C0) != -1 — so this is the engine trying to put a "+N" over a phantom whose client-side
model has no head node to anchor to.

Not reproduced: the same step ran clean in a fresh session, post-rejoin, and post-rejoin plus
two warps (3/3 after). The receive side of the sync never targets a phantom (`ApplyIncoming`
applies to the local player only), so this is reachable only through the staging verb, or by an
engine heal landing on a phantom that is the local last-target. Left open as a staging hazard;
if it recurs the cheap guard is to refuse `applyeffect heal` on a phantom whose model lacks the
node (or to apply the staging heal with the floaty suppressed).

### 11.5 The 0x688F54 / 0x6890A4 crash (§11.4) — root-caused and FIXED (2026-09-03, 03:45–04:11)

§11.4's phantom-heal crash (00:38, `eip 0x688F54`, `applyeffect heal` on a phantom, 1/4) recurred
twice more the same night, both times over a HOST-SPAWNED / materialised stand-in rather than the
staging-verb path, and this time it was root-caused and fixed. Full RE write-up and patch:
`src/KotorCoop.Plugin/FloatyTextNodeGuardHook.cs`'s class summary (authoritative — reuse its
wording for anything not repeated here); the blow-by-blow of both new occurrences, a self-inflicted
host crash during the fix, and the final live verification are in
`docs/research/transition-crashes.md`'s continuation of the "Adjacent, NOT this crash" note.

Both `CSWGuiFloatyText::CreateEntry` (0x688E10) and its per-entry update (0x688FF0) resolve the
floaty's target through `CClientExoApp::GetClientObjectByObjectId`, then ask the scene object for
its anchor node (`[vtbl+0x98](0xFF, 1)`) and dereference the answer with NO null test — exactly the
case the engine's own three guards (no object / no scene / no model) do not cover. A materialised
stand-in's client model can hit that window: on the spawn frame before
`MaterialisedEnemies.Dress` closes the twin latch, or during the kill/destroy teardown that follows
`combat: killed ... on the owner's authority` (see `enemy-materialisation.md` §7.2 and
`limitations.md`'s combat table for what the twin latch itself looks like when it succeeds).

`FloatyTextNodeGuardHook` splices both sites with a relocated 15-byte run plus a `test reg,reg`
null check; the null path counts the skip and takes the function's own existing no-op exit, so a
node-less entry is refused (CreateEntry) or ages out unseen (the per-entry update) instead of
crashing. Final live verification (04:08–04:11, 20 host-spawned creatures slain against both
guards): `floaty: floaty-text null-node guard on 2/2 site(s): CreateEntry 0x688F45 (stub
0x17120000); Update 0x689079 (stub 0x17130000)`, climbing to `floaty: guard totals so far - 27
floaty(ies) not added, 16967 tick(s) skipped` by the next module epoch, host log carrying ZERO
guard lines across the whole round (real models always have the node), both processes alive
throughout.

**§11.4's "left open as a staging hazard" status is superseded — this hazard is closed.** See
`docs/limitations.md`'s combat table.

---

## 12. Fear, the general Stun, Poison, Disguise, and stealth (2026-09-18)

Static analysis only (`tools/disasm.py`, `tools/xrefs.py`, `tools/q.py` against
`kotor1_0_3.db`, direct reads of the binary's data section, and a K1 `nwscript.nss` copy
parsed the way §7.3 did — 772 prototypes, the same five anchors re-checked). Nothing modified,
no game run, no game function called. Four new wire kinds and one new `PlayerState` bit, from
`docs/review-seamless-2026-09-18.md` §2's "named but not on the allowlist", "disguises don't
mirror", "poison INFERRED" and "stealth not synced" bullets.

Tag key as before: **VERIFIED** / **INFERRED** / **NEEDS-LIVE**.

### 12.1 Fear (sub-code 2) and the general Stun (sub-code 4) — VERIFIED, shipped, default ON

Nothing new to research: §7.2 already decoded both case bodies (`0x531546` = routine 158
`EffectFrightened` → type 8, `SetInteger(0, 2)`; `0x531559` = routine 161 `EffectStunned` →
type 8, `SetInteger(0, 4)`; neither pops a script argument) and §7.1 already read both
registrations (`0x0054D0B7: mov [edx+0x278], eax` = 158; `0x0054D1D7: mov [edx+0x284], eax` =
161). Re-decoding the compaction/jump table this pass (`0x53184C`/`0x531820`, routines
`0x33..0xA4`) gives the same 10 targets, 158 → idx 7 → `0x531546`, 161 → idx 8 → `0x531559`.
The `nwscript.nss` copy indexes `EffectFrightened` = 158 and `EffectStunned` = 161 (anchors
`GetFirstPC` 548, `CreateObject` 243, `SetMinOneHP` 716, `EffectAssuredHit` 51,
`EffectAssuredDeflection` 252 all still agree).

Shipped as `PeerEffectKind.Fear = 13` and `PeerEffectKind.Stunned = 14`, both allowlist
copies (`PeerEffectRules` / `PhantomEffectHook`) mapping `(8, 2)` and `(8, 4)`, and applied
through the SAME `OdysseyEffects.ApplyComplexEffect` path Knockdown/Paralyze/Sleep/Confused use
(`table[158]`/`table[161] == 0x5313F0` specific-index check, zero pushed arguments,
`nParams = 0`). That path is live-verified for four sibling routine numbers (§9, §11.1), so
these two ride `effect_sync` alone — no new key. Naming: `Fear` because this codebase's
`Frightened` is really `EffectHorrified` (§7.3's correction, wire value 5, kept for stability);
`Stunned` because `Stun` is the droid-scoped `EffectDroidStun`. Console verbs: `applyeffect
fear`, `applyeffect stunned`.

### 12.2 Disguise — VERIFIED two ways: the stand-in already follows a disguise; type 62 is now also a kind

**What a disguise actually does to the wearer** — `CSWSCreature::Disguise(row, effect,
gameLoading)` (`0x004EE010`, thiscall, `ret 0xC`), called from `Global::OnApplyDisguise`
(`0x004E31D0`, the type-62 dispatch-table entry) with `GetInteger(effect, 0)` as the row:

```
0x004EE034  mov eax, [esi+0xA48]          ; already disguised? -> return 0 (refuses a second)
0x004EE06B  mov eax, [esi+0xA74]          ; stats
0x004EE071  mov cx, [eax+0x186]           ; current appearance row (stats copy)
0x004EE078  mov [esi+0xA4C], cx           ; SAVED for UnDisguise
0x004EE07F  mov ecx, [esp+0x28]           ; the disguise row
0x004EE083  mov [eax+0x186], cx
0x004EE08A  mov [esi+0xA60], cx           ; <-- CSWSCreature+0xA60, the appearance.2da row
0x004EE093  call 0x4ED6E0                 ; UpdatePersonalSpace
0x004EE09C  call 0x4ED850  (push 1)       ; UpdateAppearanceDependantInfo(bStore=1)
0x004EE0A7  mov dword [esi+0xA48], 1      ; disguised flag
```

`CSWSCreature::UnDisguise` (`0x004EE150`, from `OnRemoveDisguise 0x004DCAC0`) is the mirror
image: `+0xA60 = +0xA4C`, the same two rebuild calls, `+0xA48 = 0`.

**So the mirror already exists.** `+0xA60` is exactly the field `KotorGameBridge.ReadLocalState`
samples into `PlayerState.Appearance` every tick (`OffAppearanceType = 0xA60`), and the peer's
`PhantomController.ApplyAppearance` repaints the stand-in whenever the published row differs from
the body (`PeerIdentityRule.ShouldApply`: `onBody != chosen`, then the server rebuild plus
`Odyssey.RebuildClientAppearance` — the X2 client-twin rebuild, live-verified
`phantom-locomotion.md` §11.2 and re-verified for the re-spawn case `name-plates.md`
2026-08-29). A Sith uniform / Sand People robes / envirosuit on player A therefore publishes as a
changed row and A's stand-in on every peer is repainted with it, and back on removal — with
NO new engine offset, through a path that is already live. The review's "disguises don't mirror"
was a code-audit inference from the kinds table, not something anyone had watched; what was
never true is that the effect KIND had to cross the wire for the look to follow. NEEDS-LIVE
only in the sense that nobody has stood next to a partner in a Sith uniform yet (§12.6 #3).

**What does NOT follow, and is out of scope**: the uniform's gameplay effect. Guards decide
"is this a Sith?" in NWScript against THEIR instance's own PC, so a client in uniform walks the
host's Sith base as a civilian on the host's screen (and vice versa). That is the
script-locals / per-instance-scripting gap (`quest-sync.md` §8), not an appearance gap.

**The kind itself** — `EffectDisguise(int nDisguiseAppearance)` is routine **463** and is an
`ExecuteCommandEffectComplex` case, reached through the handler's SECOND dispatch stage (the
`0x531443` cascade: `== 0xFC` → `0x5315DC`; `> 0xFC` → `0x53160F`, which is `== 0x1D3`(467) →
`0x531703`, else `routine - 270` into a second compaction table at `0x5318D8` feeding a
6-entry jump table at `0x5318C0`). Decoded from the bytes: 270 → `0x53163E`, 402 → `0x5316BC`,
420 → `0x5316F6`, 460 → `0x53164E`, **463 → `0x531689`**. That case body:

```
0x00531689  mov ecx, [0x7A3A00]           ; the VM
0x0053168F  lea edx, [esp+0x24]
0x00531693  push edx
0x00531694  call 0x5D1000                 ; guarded stack pop (returns 0 -> refuse below)
0x0053169B  je  0x531670                  ; -> mov eax, 0xFFFFF82F; ret 8
0x0053169D  mov ecx, [esp+0x24]           ; the popped int
0x005316A1  mov ax, [0x746774]            ; = 62, read from the data section (0x74667C + 4*62)
0x005316AC  mov [esi+8], ax               ; effect type 62
0x005316B0  call 0x5036A0                 ; SetInteger(effect, 0, value)
```

— the ONE complex-family case this codebase calls that pops a script argument, hence the new
`OdysseyEffects.ApplyComplexEffectOneInt` (the `table[463] == 0x5313F0` check of
`ApplyComplexEffect`, plus the single push and `nParams = 1` of `ApplySlowEffect`). §7.1's
"0x0054D04D → 0x73C (463), not decoded further" registration is this. `nwscript.nss` index 463
= `EffectDisguise` — two independent sources, same number, same shape (the `EffectAssuredHit`
51 / `EffectAssuredDeflection` 252 anchors again derived from this binary first).

Shipped as `PeerEffectKind.Disguise = 16`: captured when type 62 lands on a stand-in, magnitude
= `integer_list[0]` = the appearance row (bounded read, refused as unusable when 0 or > 0xFFFF
BEFORE any VM push — `IsUsableDisguiseRow`, mirrored in `PeerEffectRules`), forwarded, and on
the owner replayed as `EffectDisguise(row)` on the real body — whose `+0xA60` then publishes
back and repaints the stand-in everywhere, closing the loop. `Disguise` at `0x4EE034` refuses a
second disguise on an already-disguised body, so a replay onto a wearer is a logged no-op, not
a double. **Apply gated by `disguise_sync` (default OFF, NEEDS-LIVE)**: the constructor call is
shape-matched to live-verified paths but has never run, and realistically nothing in the retail
game applies a disguise to a party-less stand-in (uniforms are item properties on the wearer),
so the kind is completeness, not the mirror. Capture is always on so `effects` can show it.

### 12.3 Poison — VERIFIED constructor, shipped as an explicit once-per-application kind, default OFF

§10.3 left the tick path INFERRED (the `CSWSObject::ApplyEffect` → vtable slot 2 hop was not
single-stepped) and concluded "no kind needed". This pass makes the APPLICATION explicit
instead of relying on that inference, and leaves the tick question to the live pass it needs:

- `Global::OnApplyPoison` (`0x004DB320`, type 35) is dispatched ONCE per application through
  `OnEffectApplied` like every other handler (§1). `PhantomEffectHook` therefore sees a poison
  landing on a stand-in exactly once, whatever its ticks do afterwards — that is what "once per
  application, not per tick" means here, and it is a property of the dispatcher, not of this
  code.
- `CSWVirtualMachineCommands::ExecuteCommandEffectPoison` (`0x00533560`) — VERIFIED
  `EffectHeal`'s exact shape: `call 0x5D1000` (guarded pop of one int) with an up-front refusal
  `mov eax, 0xFFFFF82F; ret 8` when nothing is there, then effect type 35 (`0x5335FB` reads
  global `0x746708`, = 35 by the `0x74667C + 4*type` layout every constant in §6 obeys) and
  `SetInteger(effect, 0, nPoisonType)`. Routine number **250**: `0x0054D150: mov dword
  [edx+0x3E8], 0x533560`, `0x3E8/4 = 250`; `tools/xrefs.py 0x533560` finds exactly that one
  literal (unaliased, so `FindRoutine`'s linear scan is safe, as for Heal); `nwscript.nss`
  index 250 = `EffectPoison(int nPoisonType)`.

Shipped as `PeerEffectKind.Poison = 15`, magnitude = `integer_list[0]` = the poison.2da row
(row 0 is a real row, so — unlike every other magnitude the hook reads — it is NOT gated on
`aux > 0`; the receiver refuses rows above 255 before pushing), applied durable for the
effect's own duration through `ApplyOneIntParamEffect` (`ExpectedEffectPoison`, min 0, max
255). Console verb: `applyeffect poison [target] [ms] <poison.2da row>`.

**Why `poison_sync` defaults OFF, and what must be answered before it flips**: if §10.3's
INFERRED conclusion is right, a poison landing on a stand-in ALREADY reaches its owner as
per-tick forwarded damage (`PhantomHitHook`'s cumulative total). Replaying the poison effect on
the owner's real body on top of that would poison them twice — their own engine ticks the
replayed effect AND the stand-in's ticks keep arriving as damage. Which of the two paths
actually fires (or both) is precisely what nobody has watched; until a rig pass reads the
owner's `combat: taken N` ledger with the key OFF and then ON, the safe default is the one that
cannot double. Capture/forward are always on so the OFF run can still prove the capture half
(`effects` on the sender shows a `poison` row; the receiver logs `effect: not replaying kind
15 ... poison_sync is off`).

### 12.4 Stealth — VERIFIED field and setter; `FlagStealth` (protocol 53); the stand-in is held Neutral

**The field.** `CSWSCreature::SetStealthMode` (`0x0050EE50`, thiscall, `ret 8`) is the only
writer of the byte the symbol DB names `stealth_mode` at `+0x4D1` (`SetDetectMode 0x0050EE30`
is its twin for `detect_mode` at `+0x4D0`):

```
0x0050EE50  mov al, [esp+4]            ; mode (byte)
0x0050EE54  cmp al, [ecx+0x4D1]
0x0050EE5A  je  0x50EE71               ; idempotent
0x0050EE5C  mov [ecx+0x4D1], al
0x0050EE62  mov eax, [esp+8]           ; arg2
0x0050EE66  mov [ecx+0xABC], eax
0x0050EE6C  call 0x4F6D70              ; CSWSCreature::ComputeModifiedMovementRate
0x0050EE71  ret 8
```

**The toggle path.** `tools/xrefs.py 0x50EE50` → 11 call sites, all in `CSWSCreature`:
`SetActivity` (`0x4F02BC`/`0x4F02CF`), `ActivityManager` (`0x4F8905`/`0x4F89F1`), `AIUpdate`
(`0x4FE596`), `LoadCreature` (`0x50042E`/`0x50043D` — the GFF `StealthMode` read
`character-login-chain.md` line 145 mentions), `LoadFromTemplate` (`0x502918`/`0x502927`) and
two in `0x4F2ADD`/`0x4F2B37`. `SetActivity(activity, on, arg)` keeps an activity mask at
`+0x9FC` (`or ecx, ebx` / `and edx, ~ebx`), and for `activity == 1` with bits 2/3 (`test
[esi+0x9FC], 0xC`) clear calls `SetStealthMode(on, [esp+0x18])`; the `LoadFromTemplate`/
`LoadCreature` sites pass `(1, 0)` / `(0, 0)`. The client side of the same button is
`CSWGuiMainInterface::OnStealthButtonPressed 0x00688640` → `CSWCCreature::IsStealthCapable
0x00610AC0` → `CSWCCreature::PartyMenuStealth 0x0060F4B0` (the DB's names), which is where the
player-to-server message that lands in `SetActivity` originates — not traced further, because
the READ side does not need it: a level at `+0x4D1` that only this setter writes is the whole
evidence the flag rests on.

**Shipped.** `PlayerState.FlagStealth = 512` (the highest bit so far; the plugin slot's flags
half is masked at `0xFFFF`, `KotorGameBridge.WriteToPlugin`, so it survives), read every tick
by `ReadLocalFlags` from `creature+0x4D1` — a read, never a write. On peers,
`PhantomController.ApplyTargetable` now takes the bit: while it is set and `stealth_sync` is on
(default ON), `PhantomTargetRule.WantsPlayerFaction` answers false and the stand-in is held
Neutral through the SAME faction flip + `PacifyCreature` that `phantom_targetable` already
uses for a downed peer or the local_combat authority split — no new engine write, and the
existing, live-verified release lever. `ProtocolConstants.Version` 52 → 53 for the new bit.

**The visual half — `stealth_visual_sync`, default OFF, NEEDS-LIVE.** `Odyssey.SetStealthMode
(creature, on)` calls the setter above with `(1|0, 0)`, the same arguments every toggle site
passes, and reads the byte back. Latched per slot on the edge (`ApplyStealthVisual`), never
per frame, and never called at all for a peer who never stealths. It stays off because (a) no
stand-in has ever been put into this mode, (b) the callers' `+0x9FC` activity bit is not
maintained by the setter, and (c) whether the CLIENT twin picks up the stealth shader from the
server byte on a body the engine did not put there itself is exactly the kind of "server half
written, client half unknown" gap X2 (`phantom-locomotion.md` §11.2) already found once for
appearance.

### 12.5 What shipped this pass

- `PeerEffectKind` / `PeerEffectKindValues`: `Fear = 13`, `Stunned = 14`, `Poison = 15`,
  `Disguise = 16`; `PeerEffectKindValues.Max` so the plugin's range check has one number.
- Both allowlist copies (same commit): `(8,2)`, `(8,4)`, `35`, `62`. `PhantomEffectHook`'s
  magnitude read extended to types 62 (row, `> 0`) and 35 (row, `>= 0`).
- `OdysseyEffects`: `RoutineEffectFear`/`RoutineEffectStunned` through the existing
  `ApplyComplexEffect`; `ExpectedEffectPoison` through the existing `ApplyOneIntParamEffect`;
  `RoutineEffectDisguise` through the new `ApplyComplexEffectOneInt`. Failure code **11** =
  "magnitude is not a usable poison type / appearance row", refused before any VM push.
- `PeerEffectController`: `poison_sync` / `disguise_sync` (plain plugin keys, default OFF)
  gate the APPLY of kinds 15/16 only; a gated entry is latched and logged, never replayed
  later. `PeerEffectRules.NeedsOwnApplyGate` / `IsUsableDisguiseRow` are the testable mirrors.
- `PlayerState.FlagStealth` (512, `IsStealthed`), `RemoteView.FlagStealth`/`Stealthed`,
  `KotorGameBridge` `OffStealthMode = 0x4D1`, `PhantomTargetRule`, `stealth_sync` (ON),
  `stealth_visual_sync` (OFF) + `Odyssey.SetStealthMode`. Protocol **53**.
- `CheatController.ApplyEffect` / `applyeffect` usage: kinds `1..16`, the four new verbs.
- Tests: `PeerEffectRulesTests` (four mapping rows, the distinct-from-look-alike fact, the
  gate table, the disguise-row table, the contiguous-kinds pin), `PacketCodecTests`
  (`FlagStealth` round trip above the low byte, a Disguise packet with a full u16 row),
  `EffectVerbsTests` (the four verbs, magnitude carried), `PhantomTargetRuleTests` (new).
- No `SharedBlock` layout change, no packet shape change.

### 12.6 NEEDS-LIVE (exact `applyeffect` verbs — docs/CONSOLE.md; blank target = nearest phantom)

1. **Fear / Stunned, both directions** (default ON — same bar §9 set for Knockdown/Paralyze/
   Sleep): on instance A, `applyeffect fear` then `applyeffect stunned`; B's plugin log shows
   `effect: applied kind 13 from peer 0x..` and `kind 14`; reverse from B. Local smoke:
   `applyeffect fear me`, `applyeffect stunned me` answer `applied kind N to 0x7FFFFFFF`. If
   either refuses with `failure 3`, `table[158]`/`table[161]` did not hold `0x5313F0` on the
   live table — report it, do not guess.
2. **Poison, the double-count question** (`poison_sync=0` first): `applyeffect poison "" 12000 0`
   at B's stand-in from A. Expect A's `effects` to show a `poison` row captured/forwarded, B's
   log `effect: not replaying kind 15 ... poison_sync is off`, and then WATCH B's `combat:
   taken N` ledger for ~12 s: ticks arriving there confirm §10.3's inference (the tick path
   forwards as damage). Then `poison_sync=1` on B, repeat: B logs `applied kind 15`; if the
   ledger ALSO ticks, the two paths double and the key must stay OFF until the stand-in's
   poison-tick damage is filtered out of `PhantomHitHook` (its type-38 effects carry the
   poison effect's creator; that filter is the follow-up). Local smoke: `applyeffect poison me
   6000 0`; a row above 255 must answer failure 11.
3. **Disguise mirror (no key needed)**: A equips the Sith uniform on Taris (or any disguise
   item); B watches A's stand-in repaint (`identity: phantom slot N ... now renders appearance
   R` with the uniform's row in B's plugin log), and repaint back on unequip. This is the
   review's actual ask and rides the existing appearance channel.
4. **Disguise kind** (`disguise_sync=1` on the receiver): `applyeffect disguise "" 6000 <row>`
   at B's stand-in from A (a real appearance.2da row, e.g. a Sith soldier's); B logs `applied
   kind 16`, B's own body wears it for 6 s, and A's screen repaints B's stand-in from B's
   republished row. Local smoke: `applyeffect disguise me 6000 <row>`; `... me 6000 0` must
   answer failure 11; a second disguise on an already-disguised body is refused by the engine
   (`Disguise` returns 0 at `0x4EE034`) — expect `applied` on the VM side with no visible change.
5. **Stealth targeting** (default ON): B presses stealth; A's plugin log shows `phantom 0x..:
   back to neutral; attackers released (peer is in stealth)`, and a hostile A provokes ignores
   B's stand-in; B leaves stealth → `joined the player faction`. The agent dashboard shows
   `STEALTH` on B's row while set. If the line never appears, read `creature+0x4D1` on B with
   a memory probe while stealthed — the flag is only as good as that byte.
6. **Stealth visual** (`stealth_visual_sync=1` on A): same steps; A's log adds `engine stealth
   mode ON (stealth_visual_sync, NEEDS-LIVE)`, and the question is whether B's stand-in on A's
   screen renders the stealth shader and is skipped by A's enemies' `DoStealthDetection`. Any
   crash or a body stuck translucent after B unstealths → leave the key OFF and note it here.

### 12.7 Live verification (2026-09-19, Endar Spire deck, host + joiner)

`applyeffect` through `tools/blockpoke.ps1`, `effect_sync` ON, `disguise_sync=1` on both:

1. **Fear (kind 13) and Stunned (kind 14), both directions: PASS.** Host `applyeffect fear` / `applyeffect stunned` at the client's stand-in (0x14E): host `applied kind 13 ... 6000ms`, client `effect: applied kind 13 from peer 0x1 (duration 6000ms, magnitude 0)`, then kind 14 the same about 5 s later; reversed from the client, the host logged `effect: applied kind 13/14 from peer 0x2`. No refusal (`table[158]`/`table[161]` held `0x5313F0`). The visible pose was not observed.
2. **Disguise kind (16): PASS.** Host `applyeffect disguise 0x14E 8000 28` (a Sith soldier's appearance row): client `effect: applied kind 16 from peer 0x1 (duration 8000ms, magnitude 28)`; within 470 ms the host's stand-in of that player repainted, `identity: phantom slot 0 now renders appearance 146 (server row was 28, peer published 146)` then `renders appearance 28 (peer published 28)`. The revert after 8 s was not watched. Refusal path: `applyeffect disguise me 6000 0` answers `refused - magnitude is not a usable ... appearance row`. A blank target written as `""` through blockpoke is taken literally; use the phantom id.
3. **Poison (15), local smoke only.** `applyeffect poison me 6000 0` applied; magnitude 300 refused as designed. The double-count question (does `poison_sync=1` on top of the damage-tick path double the damage?) was NOT run and `poison_sync` stays OFF.
4. **Stealth, the stealth visual, the real Sith-uniform equip: not exercised** (no console verb sets stealth; the uniform needs a Taris save).

Defaults after this pass: Fear/Stunned stay ON (now live-verified); `disguise_sync` stays OFF (the kind only fires when an effect lands on a stand-in, and a real uniform already rides the appearance channel, so ON buys nothing); `poison_sync` and `stealth_visual_sync` stay OFF.

### 12.8 Live verification, part 2 (2026-09-19): stealth targeting and poison

- **Stealth targeting: PASS (flag path).** With host + joiner on the Endar Spire deck, the joiner's stealth byte (`creature+0x4D1`, read with a raw poke because no console verb toggles stealth) was set to 1: the host logged `phantom 0x14E: back to neutral; attackers released (peer is in stealth)` within about a second, and clearing it logged `phantom 0x14E: joined the player faction; local enemies can fight it`. That proves the read, the `FlagStealth` publication and the stand-in's release from targeting. Not proven: the real engine toggle (`SetActivity` -> `SetStealthMode`, which sets other state the raw byte does not), enemies actually ignoring the stand-in, and `stealth_visual_sync`.
- **Poison, `poison_sync` off: capture PASS, tick path INCONCLUSIVE.** `applyeffect poison 0x14E 12000 1` on the host: `effects` showed `captured/forwarded poison ... serial 1` (once per application, as designed); the joiner logged `not replaying kind 15 ... its coop.ini key poison_sync is off`. Over 15 s (the effect lasts 12 s) no `combat: took N damage` line reached the joiner and its hit points stayed 9, so §10.3's inference (that the tick path forwards poison damage) was NOT confirmed; poison row 1 may simply not tick on a stand-in. The double-count question (`poison_sync=1` on top of the tick path) needs a poison that visibly ticks first, so `poison_sync` stays OFF.

## 13. Menu guard (2026-09-24) — the Pausing-row parity fix; `FlagMenuGuard`, protocol 69

Closes docs/limitations.md's "Pausing" row catch: single-player KOTOR freezes combat the
instant a menu opens; co-op only pauses the OPENER's own game, so their stand-in kept eating
hits on every other screen while they had no way to react (they cannot see, let alone dodge,
what is happening on a partner's separate engine instance). Rather than trying to replicate a
real cross-engine pause (the "Pausing" row's harder, still-open half — see roadmap), this reuses
the ONE release lever `stealth_sync` already proved out: hold the menu-opener's stand-in Neutral
and pacified for as long as their menu is open, bounded so it cannot become a standing shield.

### 13.1 The field — VERIFIED, already relied on elsewhere; no new offset

`CGuiInGame+0x30` is the field this whole feature reads — the SAME field
`Odyssey.Store.cs`'s `InGameGuiBusyOffset`/`Odyssey.Input.cs`'s `IsFullScreenGuiUp` and
`Odyssey.GalaxyMap.cs`/`Odyssey.EquipScreen.cs` already gate on, VERIFIED by fresh disasm of
`ShowStoreGui 0x0062E339`/`ShowGalaxyMapGui 0x0062D069`'s own precondition checks and, since
the 2026-09-03 hardware-watchpoint correction, its true writer —
`CClientExoAppInternal::SetInputClass 0x006200E0` (class 2 = GUI sets it, class 0 = game clears
it; `Show*Gui`'s own `SetInputClass(2, 1)` and every retail close path's `SetInputClass(0, 1)`).
It reads "the full-screen in-game GUI is up" — the equipment/inventory/character/abilities/
journal/map/options screen the menu key raises, store and galaxy-map panels alike — not "some
panel exists". No new RE for this feature: only a new FIELD read off an already-resolved
pointer, on a NEW side (the external agent) that had never read it before.

`KotorGameBridge.IsLocalFullScreenGuiOpen` (`KotorGameBridge.MenuGuard.cs`) walks the SAME
pointer chain the agent's store scan already uses (`InGameGuiOffset = 0x40`,
`KotorGameBridge.Store.cs`, itself VERIFIED against `EngineHandles`/`Odyssey`'s in-process
chain) and adds the one new offset read, `+0x30`. Pure, read-only, false when no client is
resolvable.

### 13.2 The bounded window — `MenuGuardRule`, pure, unit-tested

Publishing the raw bit unconditionally (the way `FlagStealth` samples `creature+0x4D1`) would
turn "protected while a menu is open" into "protected for as long as I hold the menu open" —
parking on the inventory screen for a standing shield. `KotorCoop.Session.MenuGuardRule` is a
small pure state machine (same shape as `AwayRule`/`HostAwayRule`: external state, free
functions, tested without a clock or a game) that instead grants a BOUNDED window:

1. The instant the menu opens, protection starts (`TrackOpenSince`) — no delay.
2. A single opening is capped at `menu_guard_cap_ms` (default 12 s, inside the 10-15 s the
   feature was scoped to) — `CapExhausted`/`IsProtected`.
3. Protection ends the INSTANT the menu closes, capped or not.
4. An opening that burns through the full cap starts a cooldown (`menu_guard_cooldown_ms`,
   default 20 s, `TrackCooldown`): closing and reopening the menu does not grant a fresh cap
   while the cooldown is counting down, which is what stops "tap the menu key every 12
   seconds" from being a permanent shield. A short check well under the cap never triggers a
   cooldown at all — the ordinary way a player actually uses an inventory screen mid-fight.

`CoopAgent.ApplyMenuGuard` drives it once a pump (`Environment.TickCount64`, matching every
other ms-timestamped rule in this codebase), gated on `menu_guard_sync`, and folds
`PlayerState.FlagMenuGuard` into the freshly-read local state before it is published — the
same place `FlagAway` is folded in. `IGameBridge` itself is NOT extended: `_bridge` is checked
against the concrete `KotorGameBridge` (the pattern `AgentComposition`'s own
`ResolveDiagnostic` wiring already uses), because the interface is implemented by a long tail
of test fakes across this project and a full-screen-GUI read is meaningless against any of
them — a non-`KotorGameBridge` bridge simply never publishes the bit.

### 13.3 Consumption — the same lever `stealth_sync` already proved

`PhantomTargetRule.WantsPlayerFaction` gained a second pair of parameters,
`menuGuardSync`/`menuGuarded`, alongside the existing `stealthSync`/`stealthed` — same
formula, `!(sync && flag)`, ORed into the existing refusal. `PhantomController.ApplyTargetable`
folds `menu_guard_sync` and `PlayerState.FlagMenuGuard` together ONCE per slot per frame (the
same value both feeds `PhantomTargetRule` and gates the damage drop below, rather than two
copies of the same gate that could disagree) and passes it through; when the body is held
Neutral for this reason the log names it: `phantom 0x..: back to neutral; attackers released
(peer has a menu open)` — the exact `stealth_sync` shape (`... (peer is in stealth)`), read by
`IsMenuGuardHold`.

That release is edge-only — re-applied only when the wanted state CHANGES, the same discipline
every faction write in this class follows (`AddMember` walks faction lists; a peer standing
still must not pay that every frame). A hit landing the SAME frame the peer's menu opens, before
the flip has had a chance to run, would otherwise still reach them for real. `PhantomHitHook`
closes that race directly: `PhantomController.ClearSlot`/the per-frame drive call
`PhantomHitHook.SetMenuGuarded(slot, guarded)` every pass, and `OnDamage` drops (does not
accumulate) a hit on a menu-guarded slot before anything else runs — the exact early-drop shape
`PlacedMineFireGateHook`'s party-mine window already uses for its own same-frame race. Logged
(bounded, 12 lines): `menu guard: dropped a hit on player stand-in 0x.. (peer has a full-screen
menu open)`.

### 13.4 What shipped this pass

- `PlayerState.FlagMenuGuard = 1024` (`IsMenuGuarded`), `RemoteView.FlagMenuGuard`/`MenuGuarded`
  — carried verbatim through the existing flags dwords, no `SharedBlock` layout change, same as
  `FlagStealth`. `ProtocolConstants.Version` 66 → 69 (67-68 reserved by other streams) so the
  changelog names the new bit, same convention as 50/54; no packet shape change.
- `KotorCoop.Session.MenuGuardRule` (pure) + `CoopAgent.ApplyMenuGuard` +
  `KotorGameBridge.IsLocalFullScreenGuiOpen`.
- `PhantomTargetRule.WantsPlayerFaction`/new `IsMenuGuardHold`; `PhantomController.Drive.cs`'s
  `_menuGuardSync` field and `ApplyTargetable` wiring; `PhantomHitHook.SetMenuGuarded`/the
  `OnDamage` drop gate; `PhantomController.ClearSlot` clears the watch.
- `menu_guard_sync` (plugin `PluginConfig.ReadBool`, default ON; agent `CoopConfig`/
  `SessionSettings`/`AgentComposition`, same default) — the "one coop.ini key, two files"
  pattern `solo_segment_spectate`/`minigame_hide_peer` already use. `menu_guard_cap_ms`
  (`12000`) / `menu_guard_cooldown_ms` (`20000`), agent-side only.
- Tests: `MenuGuardRuleTests` (the timer/cap/cooldown state machine, every transition in
  §13.2's list), `PhantomTargetRuleTests` (the stealth/menu-guard combination table, including
  both held at once).
- Not in `BehaviourDigest`: like `stealth_sync`, this is a purely local, per-peer consumption
  toggle (the opener's own agent decides whether to run the timer; a peer's plugin decides
  whether to honour a bit it receives) — a one-sided setting is harmless in exactly the same
  way `local_var_sync`'s digest entry describes for its own one-sided case, so it does not
  need a warn-on-mismatch entry.

### 13.5 NEEDS-LIVE — exact recipe (not run this pass)

Static analysis and unit tests only; nobody has watched this work in the game. Uses
`galaxymap open`/`galaxymap close`/`galaxymap stats` (docs/CONSOLE.md) rather than
`tools/sendkey.ps1`'s Escape key: `galaxymap open` calls `CGuiInGame::ShowGalaxyMapGui`
directly and is the SAME field this feature reads (`galaxymap stats` already reports
`CGuiInGame+0x30` for exactly this reason), whereas Escape's real pause menu is confirmed
live (docs/research/session-save.md "Items 2/3") to set a DIFFERENT field entirely
(`CGuiInGame+0x7C`, `in_game_pause`) and click/key injection on a real inventory/character
panel is the same rig-tooling gap that section documents — `galaxymap open` needs neither a
click nor a key, and is already the console's own answer to "how do I set +0x30 without a
human". `openstore <tag>` is the fallback if a merchant is in reach.

1. Host + joiner connected, both on the same module, at least one hostile creature near the
   HOST able to see the joiner's phantom (any Endar Spire/Taris encounter).
2. On the JOINER: `galaxymap stats` — confirm `CGuiInGame+0x30 = 0` (baseline, no menu open).
   `galaxymap open`. `galaxymap stats` again — expect `CGuiInGame+0x30 = 1`.
3. Within about a second, the HOST's plugin log should show `phantom 0x..: back to neutral;
   attackers released (peer has a menu open)`, and the agent dashboard's row for the joiner
   should show a MENU-GUARD indicator (mirroring `STEALTH`'s existing one, see
   `AgentDashboard.cs`'s stealth line — the equivalent needs adding if it is not there yet).
4. With a hostile already fighting near the host, verify it re-targets something else (the
   host's own PC or a companion) rather than the joiner's now-Neutral phantom — the same
   "attackers released" verification §12.8 ran for stealth (`the host logged ... attackers
   released`, enemies actually ignoring the stand-in was NOT independently confirmed there
   either; the same caveat applies here).
5. On the JOINER: `galaxymap close`. `galaxymap stats` — expect `CGuiInGame+0x30 = 0` again.
   Within about a second the HOST's log should show `phantom 0x..: joined the player faction;
   local enemies can fight it`, and the phantom should become attackable again.
6. Cap test: leave the joiner's menu open past 12 s (`galaxymap open`, wait). Expect the SAME
   "joined the player faction" log line to appear on the host WHILE the joiner's menu is still
   open (i.e. `galaxymap stats` on the joiner still reads `+0x30 = 1`) — proving the cap
   lapsed the protection rather than the menu closing. `galaxymap close` immediately after;
   `galaxymap open` again right away and confirm the host does NOT log a fresh "attackers
   released" line for at least `menu_guard_cooldown_ms` (20 s) — proving the cooldown, not a
   fresh cap, governs the immediate reopen.
7. Same-frame drop gate (harder to trigger deliberately — a hit has to land in the single
   frame between the peer's menu opening and the faction flip landing): watch the host's log
   for `menu guard: dropped a hit on player stand-in 0x.. (peer has a full-screen menu open)`
   during steps 3-4 if a hit happens to race the flip; its absence does not mean the gate is
   broken (the ordinary case is that the flip wins the race and nothing ever reaches
   `OnDamage` for that slot in the first place — the ProbeHook-verified target release is the
   primary evidence, this is defense in depth).

### 13.6 Live run (2026-09-24)

Joiner `galaxymap open` at 18:48:22: host `phantom 0x17F: back to neutral; attackers released
(peer has a menu open)`, joiner dashboard `state MENU-GUARD`. With the menu still open the cap
lapsed at 18:48:34 (host: the stand-in rejoined the party faction). An immediate close/reopen
produced no release (cooldown). After the cooldown, a reopen at 18:49:37 released it again and
the close at 18:49:41 restored it at 18:49:42. Not run: a hostile actually switching targets and
the same-frame hit drop.

### 12.7 Live run (2026-09-24): poison and disguise

**Poison.** `poison_sync = 1` on both. Host `applyeffect poison 0x17F 30000 5` (poison.2da row 5,
5 HP every 3 s) on the joiner's stand-in: joiner `effect: applied kind 15 from peer 0x1 (duration
30000ms, magnitude 5)`, then `combat: took 5 damage from a hit on our phantom` every 3.0 s
(21:33:00, :03, :06 ... :21), one per tick and no local poison tick on top. Row 1 (an ability-score
poison) applied too and correctly did no HP damage. `poison_sync` now defaults ON.

**Disguise appearance.** Host `applyeffect disguise me 30000 28` (Sith_Soldier_01): joiner
`identity: phantom slot 0 (0x17F) now renders appearance 28`, and back to 114 when the effect
expired 30 s later. That is the look; `disguise_sync` (replaying a disguise cast ON a stand-in)
stays opt-in and was not exercised.
