# Death handling — the death pipeline, control-swap, game-over, and revival (research 2026-07-31)

What happens when a player's PC dies mid-session, and what co-op should do about it.
Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, base `0x400000`)
via the sha256-matched symbol DB. Nothing was modified.

Read alongside `min-one-hitpoint.md` — the floor that stops this whole pipeline from ever
starting, which KOTOR's own Endar Spire and Taris-duel scripts switch on for the PC — plus
`client-control-lock.md` (the input gate this pipeline fights) and
`party-reconciliation.md` (solo mode, the +0xA88 in-party flag, host-authoritative
companions). Plugin surfaces referenced: `src/KotorCoop.Plugin/Odyssey.cs` (VM-call
pattern, `KillCreature`, `SetMinOneHitPoint`) and `CombatController.cs` (the min1hp
pin + death-only-on-owner's-report rule already used for enemies).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not yet run ·
**NEEDS-LIVE** = must be confirmed on the running game before shipping.

## Headline

KOTOR party members — the PC included — **never truly die**. `CSWSCreature::GetDead`
(0x004EF820) hard-returns 0 for anyone in the client party; at HP ≤ 0 they become
**"temp dead"** (`GetDeadTemp` 0x004EF890), lie down, and the engine's own
`CServerExoAppInternal::TempDeathUpdate` (0x004B6DA0, called every frame from the server
MainLoop) **auto-revives them with a Resurrection effect once no hostile has been visible
within 10 m for 5 continuous seconds**. The two things co-op must defuse are both
one-call-site events inside that machinery:

- the **control auto-swap**: `OnApplyDeath` calls
  `ChangeCharacterToNextLivingPartyMember` when the dying creature is the PC — and the
  swap funnels through `CClientExoAppInternal::ChangeCharacter` (0x005F6B60), the exact
  function the client control-lock's Recipe B already hooks. One hook kills both problems.
- the **game-over**: all party members temp-dead → `TempDeathUpdate` calls
  `CClientExoApp::RunDeathSequence` (0x005EDC40 → internal 0x005F7200) from **exactly one
  call site** (0x004B6E99). Suppress that and the session survives an all-down party —
  and the engine's own revive machinery keeps running underneath.

Revival is an engine-shipped, exercised-every-session path: a bare `CGameEffect` of
type 4 (Resurrection) applied to the creature. The plugin can reach it through the same
VM surface `KillCreature` already uses.

## 1. The death pipeline — VERIFIED

### HP fields on `CSWSObject` (creature embeds it at +0)

| Offset | Field | Evidence |
| --- | --- | --- |
| `+0x0DC` | **current HP** (int) | written by `SetCurrentHitPoints` 0x004CC280 and `DoDamage` 0x004CCF80 |
| `+0x0E0` | max/base HP (int) | `CSWSObject::GetMaxHitPoints` 0x004D01A0 reads word +0xE0 |
| `+0x0E4` | temporary HP (int) | `GetCurrentHitPoints(0)` 0x004CAEC0 returns `[+0xE4] + [+0xDC]`; `GetCurrentHitPoints(1)` returns base only |
| `+0x0F0` | `is_raiseable` | **the resurrection gate** — see §4 |
| `+0x0F8` | `plot` | blocks the Death effect entirely (0x004E0ADE) |
| `+0x200` | `min1hp` | clamps HP writes to ≥ 1 in both `SetCurrentHitPoints` (0x004CC299) and `DoDamage` (0x004CCFAB), and converts an applied Death effect into `SetCurrentHitPoints(1)` (0x004E0AF8) |
| creature `+0x9D4` | `is_pc` (copied from `CSWSCreatureStats+0x6C` in `PostProcess` 0x004F1D02) | selects the −10 dead threshold in `CSWSObject::GetDead` |

### Who is "dead"

- `CSWSObject::GetDead` (0x004CB810): HP ≤ 0 for ordinary objects; for a creature with
  `is_pc` (+0x9D4) set, HP ≤ **−10** (cmp ax, 0xFFF6) — the classic dying window.
- `CSWSCreature::GetDead` (0x004EF820, vtable slot **+0x94** of vtable 0x007470C8):
  loops the **client party** (`GetControlledCharacter` 0x005ED8B0 →
  `CSWParty::GetCharacter` 0x006346C0, id-compared via `ServerToClientObjectId`
  0x005EDA50) and returns **0 for any party member**; non-members fall through to
  `CSWSObject::GetDead`. Party members are never "dead".
- `CSWSCreature::GetDeadTemp` (0x004EF890): the same party loop; a party member is
  temp-dead iff `GetCurrentHitPoints(0) <= 0`; non-members return 0.
- `GetControlledCharacterDead` (0x005F2ED0) = `GetDeadTemp` on member[0]'s server
  creature; it gates the in-game input handlers (calls at 0x006213F8, 0x0062190C,
  0x0062386A inside `HandleInputEvent` 0x00621210).
- The script `GetIsDead` (0x0053ECB0) returns `GetDead || GetDeadTemp` — scripts see
  temp-dead as dead.

### How death fires

Damage lands through `CSWSEffectListHandler::OnApplyDamage` (0x004DFA40, effect type 38
in the handler table built at 0x004E4A50; the table is `OnApply[type]` at handler+4,
`OnRemove[type]` at handler+8, 110 entries). It early-outs if the target is already
`GetDead` or `GetDeadTemp` (0x004DFA7B–0x004DFAA8), zeroes damage for `plot` creatures,
routes through `CSWSCreature::DoDamage` 0x004F3830 (difficulty scaling for party/is_pc
creatures, temp-HP absorption, floaty text) into `CSWSObject::DoDamage` 0x004CCF80
(`hp = base − dmg` into +0xDC, min1hp clamp). Then the decision, at **0x004E0240**:

```
if (GetDead() || GetDeadTemp())        ; vtbl+0x94, then 0x4EF890
    effect = new CGameEffect(0x8C)     ; 0x004E025D
    effect.type = 0x13 (19 = Death)    ; word [eff+8], 0x004E0296
    effect.SetCreator(damager)         ; 0x00503A00
    ApplyEffect(effect)                ; 0x004D14F0 -> OnApplyDeath
```

`OnApplyDeath` (0x004E0AC0, effect type 19) then, for a creature:
skip if `plot`; convert to `SetCurrentHitPoints(1)` if `min1hp`;
`SetLastPartyMemberTempKilled` (0x005EDC70 → 0x005F2FE0: stores the client id at
`CClientExoAppInternal+0x2D4` iff creature `+0xA88` in-party); death choreography
(attack-data, `SetAmbientAnimationState(3|4)` 0x004EFEF0, `SetAnimation(0x2710)`);
**`listening +0x19C = 0`, `word +0x9F0 = 0`** (AI flags); **clamps HP to −11** if above
(0x004E0D8F–0x004E0D99, keeping it under the −10 window); `AutoCloseGUIPanels`;
strips effects except duration-subtypes 3/4 and `IsEffectToIgnoreOnDeath` 0x004DF420.
The inverse writes in `OnApplyResurrection` (§4) are the authoritative list of what
death takes away.

## 2. The control auto-swap — VERIFIED, and it funnels through the control-lock hook

The swap is fired **from the death effect handler itself**. `OnApplyDeath` tail
(0x004E1184–0x004E11E4):

1. Loop client party; `ebx=1` if **any member is still alive** (`!GetDeadTemp`).
2. If someone is alive → `CClientExoApp::SetAutoPaused(1, reason 9)` (0x005EDEE0) —
   the "party member down" auto-pause.
3. If the dying creature's id == `CServerExoApp::GetPlayerCreatureId` (0x004AEA40) —
   i.e. **the PC died** → `ChangeCharacterToNextLivingPartyMember(1,1,1)`
   (0x005EDF80 → internal **0x005F7960**) and `GetInGameGui` → `RemoveGUIPopUps`
   (0x0062B150).

`ChangeCharacterToNextLivingPartyMember` (0x005F7960) tries at most **3** switches
(`lea ebx,[eax+eax*2]` at 0x005F7A23 — bounded, no infinite loop): each iteration calls
**`CClientExoAppInternal::ChangeCharacter(-2, 0)` (0x005F6B60)** and stops when the new
member[0] is neither `GetDead` (vtbl+0x94) nor `GetDeadTemp`. Its only other caller is
the Tab-key input case (0x00621342).

**Interaction with the client control lock:**

- **Recipe B (in-process `ChangeCharacter` hook) suppresses the death-swap for free** —
  the auto-swap uses the identical funnel the hook guards. Early-returning for target ≠
  own PC leaves member[0] = the (temp-dead) PC; the 3-try loop exits harmlessly.
  (VERIFIED funnel; the GUI state after a suppressed swap is the same NEEDS-LIVE item
  already flagged in `client-control-lock.md`.)
- **Recipe A (external per-frame id pin) fights the swap**: `SetControlledCreature`
  (0x00635480) reorders the whole 0x88-byte member blocks, the pin only reverts the id —
  a companion-flavoured member[0] block with the PC's id persists until the next real
  reorder. Works, but messy. Prefer Recipe B on clients before shipping death handling.
- While the PC is temp-dead and un-swapped, `GetControlledCharacterDead` = 1 and the
  input handlers gate on it — a downed player's clicks are largely inert, which is the
  behaviour a bleed-out state wants anyway. Exact residual input (camera, Esc, map)
  NEEDS-LIVE.

## 3. Game-over — one call site, plus the built-in auto-revive it hides — VERIFIED

`CServerExoAppInternal::TempDeathUpdate` (0x004B6DA0), called every frame from the server
`MainLoop` (0x004BABB0, site 0x004BAC4A). Early-outs: `GetGameOver` (0x005EDC60, reads
`CClientExoAppInternal+0x2C0 game_over_state`) and `GetAreaNotReadyToDisplay`. Then:

1. **All-dead scan.** Loop client party members (`GetCharacter` → `GetServerCreature`
   0x0060FB20 → `GetDeadTemp`). If **every** member is temp-dead and the party is
   non-empty → **`CClientExoApp::RunDeathSequence` (0x005EDC40) — the game-over. Single
   call site: 0x004B6E99.**
2. **Combat-clear probe** (any member dead): every 1000 ms (accumulator
   `internal+0x100CC/+0x100D0`), scan the leader's area: a creature that is not in-party
   (`+0xA88 == 0`), has `GetReputation(leader) <= 10` (hostile) and `IsPartyVisible`
   (0x004F7650) within **10 m** (push 0x41200000 via the reputation/visibility block at
   0x004B6F45-0x004B6F6B) marks the party "not safe" and resets the safe-timer
   (`+0x100D4 = 0`).
3. **Auto-revive** once `+0x100D4` exceeds **0x1388 = 5000 ms** of continuous safety:
   for each temp-dead member — `ComputeSafeLocation` (0x004AEB60, radius 5.0) +
   `SetPosition` (0x004CD1A0), then **builds a bare `CGameEffect` (0x8C bytes, ctor
   0x00503E40) with `type = [0x74668C] = 4` (Resurrection)** and applies it via
   `CSWSObject::ApplyEffect` (0x004D14F0), then **forces `is_raiseable +0xF0 = 1`**
   (0x004B70FC). Finally, any member farther than 40 m from the leader
   (`dist² > [0x745548] = 1600.0`) is teleported to the leader — **skipped when solo
   mode `partyTable+0x190` is set** (0x004B7114), so party-sync's held solo mode
   suppresses only the gather-teleport, not the revive.

`RunDeathSequence` (internal 0x005F7200): sets `internal+0x2C0 = +0x2C4 = 1`
(game_over_state + fade pending — `SetGameOver` 0x005EDC50/0x005F2F00 writes the same
pair), `CSWGuiManager::HideAllPanels`, `CSWGuiMessageBox::DisplayDeathMessage`
(0x00627260 — strref **0xA56F**: *"Your entire party has been killed. Return to Main
Menu."*, confirm callback = `OnDeath` 0x00625A60), and points the camera at the last
temp-killed member (`internal+0x2D4`). Per frame afterwards `UpdateDeathSequence`
(0x005F7330, called from client MainLoop at 0x006033B3) drives a fade and re-raises the
panel; a click funnels through `HurryDeathSequence` (0x005F2F20). `OnDeath` →
`SetEndGamePending(1,0,1)` (0x005EDE10: `internal+0x1A8 = 1`, `+0x374` fade timer,
`+0x1AC = 1`) → back to the main menu. There is no "load last save" shortcut — the stock
flow is menu-out.

**Suppression options** (a co-op session must not let one instance menu-out):

- **Preferred: hook `RunDeathSequence` internal 0x005F7200** in the plugin (detour
  framework already exists) — report "party down" to the agent and early-return. Nothing
  is half-initialised by skipping it: `+0x2C0/+0x2C4` stay 0, so `UpdateDeathSequence`
  and the MainLoop death branch stay dormant, and `TempDeathUpdate` keeps running —
  **including the auto-revive machinery**. It will be re-called every frame while the
  party is down; the swallow must be idempotent. (INFERRED clean; NEEDS-LIVE.)
- Equivalent external lever: **NOP the 5-byte call at 0x004B6E99** (`E8 A2 6D 13 00`).
  Zero-argument call, stack-neutral, single site — the smallest possible patch.
- Do **not** pre-set `+0x2C0` to fake "already game over": that both starts the fade/GUI
  path in the client MainLoop and kills the auto-revive scan (the `GetGameOver` early-out
  guards all of `TempDeathUpdate`).

Legacy note: `CSWSMessage::HandlePlayerToServerPlayerDeath` (0x005252A0, single dispatch
site 0x00527EEF) is the NWN-era networked "player died" event (posts AI event 0x21) —
dormant in K1 single-instance play; not a useful surface here.

## 4. Revival surfaces — VERIFIED (one gate, one ordering caveat)

### The Resurrection effect (type 4) — the engine's only stand-up path

`OnApplyResurrection` (0x004E13C0, registered for effect type 4 at 0x004E4A77):

1. **Gate: `is_raiseable +0xF0` must be non-zero** or the whole handler no-ops
   (0x004E13C5). `OnApplyDeath` never clears it (only a read of `is_destroyable +0xEC`
   exists in its body), but `TempDeathUpdate` still forces `+0xF0 = 1` alongside its own
   revive — do the same.
2. If `GetCurrentHitPoints(0) < 1` → `SetCurrentHitPoints(1)` (0x004CC280).
3. `ClearAllActions(1)` (0x004CCD80); **`listening +0x19C = 1`; `word +0x9F0 = 0xFFFF`;
   `commandable +0xE8 = 1`** — the exact inverse of the death writes;
   `RecomputeAmbientAnimationState` (0x004F28D0) — the creature stands up.
4. If a client object exists → `SendServerToPlayerDestroyDeathGUI` (0x0056B320) — tears
   down a death GUI that already appeared (covers the race where game-over fired first).
5. Removes lingering effects of **type 57 = HitPointChangeWhenDying** (the bleed-down
   effect; handlers 0x004DC660/0x004DC7A0) with subtype 1.

Script-level `EffectResurrection` (`ExecuteCommandEffectResurrection` 0x00533660) builds
exactly this: `CGameEffect` ctor 0x00503E40, `type = [0x74668C] = 4`, valid-bit `|8` in
+0xA. Zero NWScript arguments. The plugin can mint one with the same VM pattern
`KillCreature` uses (`FindRoutine(table, 0x00533660)` + `StackPush*` +
`ApplyEffectToObject 0x0052E570`).

### What will NOT work

- **Heal cannot raise the dead: `OnApplyHeal` (0x004E0750, type 39) explicitly no-ops
  when the target is `GetDead` or `GetDeadTemp`** (0x004E084A–0x004E0865). Resurrection
  first, top-up after.
- Writing HP alone does not stand the creature up: the death state is the animation
  state + `+0x19C/+0x9F0/+0xE8` writes, and nothing re-runs them on an HP poke. A raw
  `SetCurrentHitPoints(30)` on a temp-dead creature leaves `GetDeadTemp` false but the
  body prone and the AI flags zeroed (INFERRED from the write lists; do not ship this).

### Preventive levers

- `min1hp +0x200` (script `SetMinOneHP` 0x00545F90; plugin `SetMinOneHitPoint`) floors
  every HP write at 1 **and** converts an applied Death effect into "set HP 1" — full
  immunity to the pipeline. Already proven live on phantoms/enemies. Caveat from
  `Odyssey.cs`: the target never reads dead, so hostiles never disengage.
  ⚠ **KOTOR sets this on the PC itself**: the Endar Spire command deck's OnEnter pins it
  for the whole deck and the Taris duel ring pins it per duel, so on those maps none of the
  machinery above can fire for anyone. That is a module rule, not a co-op fault — see
  `min-one-hitpoint.md`.
- `plot +0xF8` zeroes damage and blocks the Death effect — the phantom blueprint sets
  both from frame zero, which is why **a peer's phantom can never trigger any of this
  machinery on the remote instance** (VERIFIED gates; the phantom carries Min1HP+Plot).

### Minimal safe "revive this creature at X hp" recipe

All by object id, resolved via the tag/id caches — never a stored pointer
(engine-write rules). In-process, main thread:

```
1. creature = ResolveServerObject(id);  if !creature → abort
2. write int  creature+0xF0 = 1              // is_raiseable; engine-precedented raw write
                                             // (TempDeathUpdate does exactly this)
3. VM: EffectResurrection()                  // routine at 0x00533660, 0 args
   VM: ApplyEffectToObject(0, eff, id, 0f)   // 0x0052E570, same shape as KillCreature
4. next tick: SetCurrentHitPoints(creature, X)   // 0x004CC280; X ≈ 30% of [+0xE0]
```

Step 4 waits one tick because `ApplyEffect` routes through
`CServerAIMaster::OnEffectApplied` (0x004AF0F0) and `TempDeathUpdate`'s own
`+0xF0 = 1`-after-apply ordering suggests handler execution may be deferred; a same-frame
heal/HP write could land while the target still reads dead (NEEDS-LIVE: if the handler
turns out to be synchronous, steps 3–4 can merge). No derived state needs rebuilding
beyond what the handler itself restores.

## 5. Recommended co-op death design

Ground rule from the findings: the engine already implements "downed → wait for combat to
clear → stand up" for party members. Co-op should keep that machinery and amputate
exactly two things: the swap and the menu-out.

1. **Local PC keeps native temp-death.** No min1hp on the local PC (an unkillable 1-HP
   PC never disengages hostiles and never reads down to anyone). HP is already in the
   shared block, so the peer's agent sees "player down" without new protocol.
2. **Swap suppression = control-lock Recipe B.** Ship the `ChangeCharacter` (0x005F6B60)
   early-return hook on clients; it blocks Tab, portraits, *and* the death auto-swap in
   one place. Clear auto-pause reason 9 with the existing auto-pause machinery when it
   fires (partial-death case).
3. **Game-over suppression, host:** hook `RunDeathSequence` internal 0x005F7200 (or NOP
   0x004B6E99) → notify agent, swallow. The auto-revive keeps running: an all-down party
   lies on the floor and stands up at 1 HP when no hostile has been visible for 5 s —
   the engine's own bleed-out-and-recover loop, for free.
4. **Client instances: neutralise `TempDeathUpdate` entirely** (hook 0x004B6DA0,
   early-return). On a client, all three of its jobs are host-authoritative decisions:
   game-over (host decides the wipe), auto-revive (host publishes companion/PC state;
   a client-local revive would stand a companion up while the host still shows it down),
   and the gather-teleport (party-sync owns positions). The client's own PC revive
   arrives as a session command → recipe §4. Host keeps stock behaviour minus step 3.
5. **Peer revive.** Proximity/timing rules live in the agent (e.g. peer within N units,
   channel through the existing command layer): agent sends ReviveRequest → owning
   instance runs the §4 recipe at ~30% max HP. Because Resurrection tears down a
   death GUI and restores the AI flags itself, this is safe even if the downed state
   lingered for minutes.
6. **Both PCs down (each on its own instance) = synchronized wipe.** The swallow hook
   consults session state: if the peer's PC is also down (grace window ~5 s), *stop
   swallowing* — call the original `RunDeathSequence` on both instances so both players
   get the stock "Your entire party has been killed" flow, and the agents tear the
   session down (or coordinate a synchronized reload, which is just the existing
   load-path plus a handshake). Honest, stock-engine, no half-alive stranded session.
7. **Companions under party-sync** stay on the CombatController rule that already works
   for enemies: non-owners pin min1hp and mirror HP ≥ 1; death only on the owner's
   report (KillOnce → real Death effect so scripts/XP fire once, on the authority).
   Restore any min1hp pins in the pre-save flush (min1hp persists with the creature).

### Crash-risk register

| Lever | Risk | Why |
| --- | --- | --- |
| `ChangeCharacter` hook (swap + lock) | LOW | early-return before `SetControlledCreature`; bounded 3-try caller; GUI consistency NEEDS-LIVE (already tracked) |
| `RunDeathSequence` swallow / NOP 0x004B6E99 | LOW | zero-arg call, single site, skipped code only starts GUI/fade; called per-frame while down — swallow must be idempotent |
| `TempDeathUpdate` early-return (clients) | LOW | function only reads party state and applies effects; skipping it leaves no dangling state (timers at `+0x100D0/+0x100D4` simply stay put) |
| `+0xF0 = 1` raw write | LOW | int flag, engine writes it raw itself; no derived state |
| Resurrection via VM | LOW | engine-shipped path exercised every play session by `TempDeathUpdate`; gate checked first |
| Same-frame HP top-up after resurrection | MEDIUM | possible deferred handler → write lands on a still-dead target; defer one tick until measured |
| Raw HP write instead of resurrection | HIGH (state corruption, not crash) | prone body, zeroed AI flags, no stand-up — never ship |
| Re-down loop after revive in combat | design | revive at ~30% HP, agent-side proximity rule; consider a short damage-immunity effect if live play shows insta-drops |

## Verification tags

- VERIFIED (disasm + DB): HP fields and both min1hp clamp sites; party-never-GetDead /
  GetDeadTemp semantics; the death decision in OnApplyDamage (GetDead‖GetDeadTemp →
  Death effect type 19); OnApplyDeath's writes (anim, +0x19C/+0x9F0 zeroed, HP → −11,
  plot/min1hp gates); the auto-swap trigger (dying == GetPlayerCreatureId →
  0x005EDF80 → ChangeCharacter 0x005F6B60, ≤3 tries); auto-pause reason 9;
  TempDeathUpdate's all-dead → RunDeathSequence single call site, 10 m/1 s/5 s
  combat-clear auto-revive with Resurrection type 4 + `+0xF0 = 1`, and the solo-mode
  skip of the gather-teleport; RunDeathSequence/UpdateDeathSequence flag flow
  (+0x2C0/+0x2C4/+0x2D4/+0x1A8/+0x1AC), strref 0xA56F text, OnDeath →
  SetEndGamePending; OnApplyResurrection's gate and restore writes; OnApplyHeal's
  dead-target no-op; EffectResurrection = type-4 builder, 0 args.
- INFERRED (strong): swallowing RunDeathSequence leaves no half-set state; skipping
  TempDeathUpdate on clients is side-effect-free; raw-HP-write-only revive corrupts
  presentation state.
- NEEDS-LIVE: everything in the risk register; whether ApplyEffect defers the handler
  one tick; residual input while temp-dead un-swapped; GUI/portrait state after a
  suppressed swap; behaviour of the 5 s auto-revive with phantoms/mirrored enemies in
  the area (a min1hp-pinned mirrored hostile standing on the bodies keeps the safe-timer
  at 0 until the authority reports it dead).

## Key addresses

```
0x004DFA40  CSWSEffectListHandler::OnApplyDamage     (death decision at 0x004E0240)
0x004F3830  CSWSCreature::DoDamage                   (difficulty, temp HP; -> 0x004CCF80)
0x004CCF80  CSWSObject::DoDamage                     (+0xDC write, min1hp clamp)
0x004CC280  CSWSObject::SetCurrentHitPoints          (+0xDC write, min1hp clamp)
0x004CAEC0  CSWSObject::GetCurrentHitPoints          ((1)=base +0xDC, (0)=+0xE4++0xDC)
0x004CB810  CSWSObject::GetDead                      (<=0; is_pc +0x9D4 -> <=-10)
0x004EF820  CSWSCreature::GetDead                    (party member -> never; vtbl+0x94)
0x004EF890  CSWSCreature::GetDeadTemp                (party member, HP<=0)
0x004E0AC0  CSWSEffectListHandler::OnApplyDeath      (type 19; swap tail 0x004E11B5)
0x004E13C0  CSWSEffectListHandler::OnApplyResurrection (type 4; +0xF0 gate)
0x004E0750  CSWSEffectListHandler::OnApplyHeal       (type 39; no-ops on dead)
0x00533660  ExecuteCommandEffectResurrection         (0 args -> type-4 effect)
0x00532A70  ExecuteCommandEffectHeal
0x00545F90  ExecuteCommandSetMinOneHP                (+0x200)
0x004B6DA0  CServerExoAppInternal::TempDeathUpdate   (game-over + auto-revive)
0x004BAC4A    its single caller (server MainLoop 0x004BABB0)
0x004B6E99    the single RunDeathSequence call site  (game-over trigger)
0x005EDC40  CClientExoApp::RunDeathSequence -> internal 0x005F7200
0x005F7330  CClientExoAppInternal::UpdateDeathSequence (fade; client MainLoop 0x006033B3)
0x005F2F20  CClientExoAppInternal::HurryDeathSequence
0x005EDC50/60  CClientExoApp::SetGameOver/GetGameOver (internal+0x2C0 game_over_state)
0x005EDC70  CClientExoApp::SetLastPartyMemberTempKilled (internal+0x2D4)
0x005EDE10  CClientExoApp::SetEndGamePending         (+0x1A8/+0x374/+0x1AC)
0x00627260  CSWGuiMessageBox::DisplayDeathMessage    (strref 0xA56F)
0x00625A60  CSWGuiMessageBox::OnDeath                (-> SetEndGamePending)
0x005EDF80  CClientExoApp::ChangeCharacterToNextLivingPartyMember -> 0x005F7960
0x005F6B60  CClientExoAppInternal::ChangeCharacter   (the control-lock hook point)
0x005EDEE0  CClientExoApp::SetAutoPaused             (reason 9 on partial death)
0x005F2ED0  CClientExoAppInternal::GetControlledCharacterDead
0x004AEB60  CServerExoApp::ComputeSafeLocation
0x0056B320  CSWSMessage::SendServerToPlayerDestroyDeathGUI
0x005252A0  CSWSMessage::HandlePlayerToServerPlayerDeath (NWN legacy, dormant)
0x004E4A50  effect handler table init (types: 4=Resurrection, 19=Death, 38=Damage,
            39=Heal, 57=HitPointChangeWhenDying)
```
