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

## 6. 2026-09-02 — `hurt`/`down` dev verbs, and the client `TempDeathUpdate` guard §5 item 4 shipped

`playthrough-taris.md` Step 3/4 confirmed, under two different real fights (Vulkars, then
rakghouls), the same finding the Endar Spire round had already made: **no dev verb could drop
a party member below 1 hp**, because vanilla ordinary damage cannot — the −10 dying window
`GetDead` checks is reached only through a Death effect (§1), and nothing in the console
reached one. This round closes that gap and ships the client-side suppression §5 item 4
recommended and left unbuilt.

### 6.1 `hurt`/`down` — VERIFIED (code + disasm), NEEDS-LIVE (the exercise itself)

Two new dev-only console verbs, `src/KotorCoop.Plugin/CheatController.Downed.cs`, cheat kinds
`CheatKindHurt = 63` / `CheatKindDown = 64` (`SharedBlock.cs`, mirrored in
`IGameBridge.CheatKinds`), wired through the same registry every other staging cheat uses
(`ConsoleCommand.Cheats.cs` → `CoopCommandDispatcher`'s dispatch table → the plugin's cheat
channel → `CheatController.Execute`) — no new `SharedBlock` fields; both ride the existing
`CheatAmount`/`CheatTarget` pair, exactly the shape `leader`/`followstall` already use for a
party-roster slot.

- **`hurt <amount> [slot]`** calls `Odyssey.ApplyDamageEffect(creatureId, amount, out
  failure)` — the SAME VM-dispatched `EffectDamage`(type 8)+`ApplyEffectToObject` call
  `Slay`'s `KillHere` branch and `Odyssey.KillCreature` both already use (Odyssey.Health.cs),
  which routes through `CSWSCreature::DoDamage`/`CSWSObject::DoDamage` (§1) exactly like a
  live swing: difficulty scaling, temp-HP (shield) absorption, and **both** of the engine's
  1-hp floors apply — a `min1hp` pin (a module's own, or `god`'s) and the difficulty `MinHP1`
  rule (statically dead in retail — `IsDifficultyMinOneHitPointOn`, `min-one-hitpoint.md`).
  This is deliberate: `hurt` exists to prove the pipeline reacts to a *genuine* hit, not to
  bypass anything.
- **`down [slot]`** clears any `min1hp` pin the target currently holds — the same order
  `Slay`'s `KillHere` branch already uses before a real kill — applies `9999` damage through
  the identical `ApplyDamageEffect` call (clamped to 10000 by the VM handler, same value
  `KillCreature` uses), then restores the pin. Because `CSWSCreature::GetDead` hard-returns 0
  for a client-party member (§1, VERIFIED), this lethal hit can never truly kill a party
  member — it can only cross zero and produce **exactly** the temp-dead state a real lethal
  hit produces, the same state `DeathController`/`DeathPump`/the engine's own
  `TempDeathUpdate` auto-revive already know how to handle. The pin is restored afterward
  (not left off) because, unlike a slain enemy, the party member keeps playing and a module's
  own safety net (Endar Spire command deck, the Taris duel ring) should still be there once
  they are revived.
- **THE FLOOR EACH RESPECTS/BYPASSES, PRECISELY:** `hurt` respects both 1-hp floors (it is
  ordinary damage). `down` bypasses only the `min1hp` pin, and only for the one hit it applies
  — nothing needs to bypass the difficulty floor, since it is statically unreachable in
  retail. Neither verb touches the −10 dying window itself; that is exactly what makes `down`
  produce the SAME state a real kill does rather than a synthetic one.
- Both refuse cleanly with no creature resolved: `TryResolvePartyOrSelf` only ever resolves
  the local player (`CheatTargetLocalPlayer`, the console's own "blank slot" default) or a
  live party-roster slot 0-8 (`Odyssey.GetPartyRosterObjectId` + `ResolveServerObject`) — an
  empty slot, a stale roster entry, or an out-of-range number all answer with the specific
  reason rather than silently no-opping, the same convention every other staging cheat in this
  channel follows.
- Pure-logic pieces are unit-tested: `CoopCheatDispatchTests.HurtAndDownCarryTheDamageAndTheLocalPlayerSentinel`/
  `HurtAndDownCarryATypedPartySlotVerbatim` exercise `ConsoleCommand`'s parser end to end
  through the full agent dispatch (amount/slot bounds, the blank-slot default, the local-player
  sentinel vs. a real slot 0, the exact refusal set in `ARefusedLineNeverReachesThePlugin`), and
  `CheatKindSyncTests`/`ConsoleCommandVerbTableCoverageTests` now also cover `Hurt`/`Down`.
- NEEDS-LIVE: everything about actually watching the pipeline react — see the recipe below.
  Code review and static disassembly cross-check (`Odyssey.ApplyDamageEffect` is exercised
  today by `slay`/`hurt`'s own dev-verb sibling and by `KillCreature`, so the call shape
  itself is already exercised on this engine) give this reasonable confidence, but "the
  handler chain converts a `down` hit into `GetDeadTemp` and the game does not crash" is not
  yet an observed fact. One specific ordering risk worth naming: `Down` clears `min1hp`,
  applies the lethal effect, and restores `min1hp` all in the SAME native call sequence
  (unlike the Resurrection recipe in §4, which defers its hit-point top-up a full tick because
  `ApplyEffect`'s own handler dispatch may not be synchronous). `Odyssey.ApplyDamageEffect` is
  a plain in-process native call with no queued/async step of its own — the same shape
  `KillCreature` already uses synchronously throughout this codebase — so the death DECISION
  inside `OnApplyDamage` is expected to run and complete before the call returns, making an
  immediate restore safe. But this has not been independently confirmed the way §4's own
  caveat was inferred, so if a live round ever finds `down` producing "set hp 1" instead of a
  genuine downed state on a creature that DID have `min1hp` set, this ordering is the first
  place to look — the fix would be to defer the restore one tick, the same shape
  `ApplyPendingHitPoints`/`_pendingHitPoints` already use.

### 6.2 Client `TempDeathUpdate` suppression — VERIFIED (bytes, calling convention), NEEDS-LIVE (behaviour)

§5 item 4 recommended neutralising `TempDeathUpdate` (0x004B6DA0) entirely on a client, since
all three of its jobs (game-over, auto-revive, gather-teleport) are host-authoritative
decisions in co-op — and the code never built it; only comments referenced the address. This
round adds the third guard to `DeathController.cs`, in the exact verified-bytes-or-refuse
style of the two shipped ones, gated OFF by default behind a new coop.ini key.

- **The bytes patched, and why they are safe.** `TempDeathUpdate`'s first instruction is `mov
  eax, dword ptr fs:[0]` (`64 A1 00 00 00 00`, 6 bytes) — the opening move of MSVC's SEH
  exception-frame setup, unrelated to anything the function actually computes before the
  early return skips the rest of it. VERIFIED against the binary with `tools/disasm.py 0x4B6DA0
  64`/`0x4BAC30 48`/`0x4B71C0 80`: the prologue bytes match exactly, the function's single
  caller (server `MainLoop`, `mov ecx, esi; call TempDeathUpdate` at 0x004BAC4A) pushes NO
  stack arguments, and the function's own epilogue is a bare `ret` (`c3`, no operand) at
  0x004B71DE — a plain `__thiscall` with zero explicit arguments. The early-return stub
  therefore needs no stack cleanup at all (unlike the `ChangeCharacter` swap guard's `ret 8`):
  when suppressed it is a single `ret`; when not, the relocated 6-byte prologue instruction
  runs and control jumps to `TempDeathUpdate+6`, identical in shape to the two shipped
  guards. death-handling.md's own risk register already rated this LOW ("function only reads
  party state and applies effects; skipping it leaves no dangling state") — the combat-clear
  timers at `+0x100D0/+0x100D4` simply stay wherever they were for as many frames as the
  client suppresses the function, and nothing else reads them.
- **What a client loses while this is armed**, precisely: the engine's own auto-revive of its
  own PC after 5 s with no hostile visible (`DeathPump`'s `RequestRevive` — a standing peer
  within 3.5 units — is the client's PC's only remaining stand-up path while this guard is
  on, alongside the existing peer-rescue window that already exists regardless), and the
  gather-teleport that snaps a member more than 40 m from the leader back to them (party-sync
  already owns positions on a client, so this was redundant at best). Game-over itself was
  already swallowed by the second, always-on guard (`RunDeathSequenceCall`) on every instance,
  client or host — this third guard's marginal effect on game-over is that a client no longer
  even evaluates the all-dead scan that would have tried to raise it.
- **Gating.** New coop.ini key `client_temp_death_suppress` (plugin-side, `PluginConfig`),
  default **OFF** — this is exactly the kind of NEEDS-LIVE lever the project's own convention
  ships inert until observed. Even with the key on, the guard only actually suppresses while
  `SharedBlockView.DamageDealtEnabled` is true — the same flag `CheatAuthority`/`IncomingDamageController`
  already treat as "this instance mirrors somebody else's enemies", i.e. is a connected
  client, set true only by `KotorGameBridge.ReadDamageDealt` on the client's own pump path
  (`CombatPump.Enemies.cs`). At the main menu, on a host, or before that flag has been set,
  the guard is armed-but-inert — `Install()` treats a refusal of this third patch as
  non-fatal (logged, not fatal) so a bad match here can never take the two already-shipped
  suppressions down with it.
- Added to `profiles/coop.ini.example` (shipped `=0`, with the reasoning above) and
  `docs/CONFIG.md`.
- NEEDS-LIVE: whether a client actually behaves correctly with `TempDeathUpdate` fully dark —
  in particular, whether anything else on the client side silently depended on the
  combat-clear timers advancing even though nothing reads them today.

### 6.3 The live-verification recipe

**Save/module:** any module outside the two known `min1hp`-pinned zones
(`min-one-hitpoint.md`) — the Endar Spire command deck and the Taris duel ring. `tar_m10aa`
(Black Vulkar Base), already the Step 3 combat module in `playthrough-taris.md`, is a known-
clean choice reachable from the same Taris save the last two playthrough rounds used.

**Setup:** `demo.ps1` with both instances loaded into that module, standing within a few
meters of each other. For (ii)/(iv) below, set `client_temp_death_suppress = 1` in the
CLIENT's `coop.ini` before launch (default OFF leaves the client on stock `TempDeathUpdate`,
which is also worth a control run).

1. **A peer sees the phantom down.** On instance A: `~`, `down`. Instance A's own plugin log:
   `death: the local player is down`. On instance B, `who`: the peer's row now reads `<name>
   tar_m10aa  [down]` (`CoopCommandDispatcher.Session.cs`'s `WhoCommand`, sourced from
   `PlayerState.IsDowned` on the wire) — this is the check that the DOWNED STATE ITSELF, not
   just the console's own answer, crossed to the other instance. `pwsh tools\blockpoke.ps1
   -ProcId <A's pid> death` on instance A also reads `downed 1 (serial N)`.
2. **Auto-revive from a standing peer within 3.5 units.** With A still down from step 1, walk
   B's character within ~3.5 units of A (`DeathPump.ReviveRangeUnits`) and stop. Instance A's
   plugin log: `death: revive applied; the player stands up`, then `death: revived player
   topped up to 15 hp` (`DeathPump.ReviveHitPoints`) within about a second. `who` on instance
   B: the `[down]` flag on A's row clears. Repeat from >3.5 units away first to confirm the
   rescue does NOT fire prematurely (`DeathPump`'s own distance log line names both positions
   if it does — see its "found live" comment about a 76-unit false rescue).
3. **Game-over suppression holds while only one is down.** `down` on instance A again;
   confirm A does NOT show the "Your entire party has been killed" screen and does NOT swap
   control to a companion (`SuppressGameOver`/`SuppressControlSwap` both hold while
   `DeathPump.PumpDeath` sees B still standing) — `blockpoke.ps1 death` on A should read
   `suppress: gameOver 1 swap <0 or 1, host-vs-client>`. Instance B keeps playing normally the
   whole time (walk it around, open a container — nothing on B should hitch).
4. **Both down together → stock game-over on both.** With A already down, `down` on instance
   B too (same module, or `down` naming B's own PC if B is standing elsewhere — the point is
   both players' local `IsDowned` reads true at once). Within a few seconds both plugin logs
   should show `death: game-over suppressed; the session decides` followed by
   `every player is down; releasing the game-over screen for everyone`
   (`DeathPump.PumpDeath`'s `allDown` branch), and BOTH screens should then show the real
   "Your entire party has been killed" flow together — never one instance alone at the menu
   while the other still plays.
5. **The client-suppression guard itself (§6.2).** With `client_temp_death_suppress = 1` on
   the client and the client actually connected (so `DamageDealtEnabled` is true), the plugin
   startup log line should read `death handling: ...; client temp-death guard at
   0x4B6DA0 (stub 0x.., configured=True)`. Repeat steps 1-2 once more and confirm the client's
   own PC still revives via `DeathPump.RequestRevive` (step 2's mechanism, unaffected by this
   guard) even though the client's own `TempDeathUpdate` is not running at all — the point of
   this check is that the peer-rescue path does not depend on the function this guard
   disables.

None of this recipe has been run against a live game as part of this pass (build + unit tests
only, per this round's own constraints) — every claim in §6.1/§6.2 above is tagged
accordingly. The recipe is written to be followed verbatim by whichever session next has the
rig.

### 6.4 Live result 2026-09-02 — the recipe run on the rig (END_M01AA, save 1), what it found, what shipped

Run by an unattended session on the rig (`tools/demo.ps1 -Save 1`, `blockpoke.ps1 … console
'down'`), three full wipe → recovery cycles back to back on the final build. Tags below are
VERIFIED = watched in the logs of that run; everything under "shipped" has a unit test.

**Steps 1–3 (one player down, peer rescue, game-over held) — VERIFIED.** Nothing surprising
beyond two engine facts the recipe did not know:

- The plugin's game-over/control-swap asks arrive **per frame** while a party is down; the
  agent throttles its answer rather than re-deciding 500×/s (`per-frame-engine-writes` rule).
- The engine's own auto-revive is **per party** (~5 s with no hostiles in range), so a downed
  PC whose companion still stands comes back on its own; "down" in the co-op sense means the
  whole party. That is why the recipe's wipe needs `down 0` (the companion slot) AND `down`
  on BOTH instances, and why `PlayerState.FlagPartyWiped` (= 256, protocol 50) exists: a
  peer's wire row now says "my whole party is down", distinct from `FlagDowned`.

**Step 4 (both down) — VERIFIED, with the whole post-wipe path that step 4 never described:**

1. `every player is down; releasing the game-over screen for everyone` on both agents in the
   same second. Both games show the stock "party killed" flow.
2. ~13–18 s later each game tears its world down; agent reads fail; each publishes its last
   state as **away** (`AwayRule`). A stale `LocalDowned` used to survive that teardown and
   report "back in a world, 12/12 hp, still downed" after the reload — `DeathController.
   Invalidate` now lowers it on teardown (pinned by `WipeRecoveryRuleTests.AStaleDownedHost…`).
3. The host reloads a save (`Agent.exe load --pid <host> --load-save 1`, or the menu). Neither
   `SaveOrLoadClassifier` sees it: the classifier watches digests of a running world, and a
   load from the game-over menu has no "before" world to diff against. So no `HostReload`
   trigger ever fired and the client sat at its own game-over menu forever.
4. **Shipped:** `WipeRecoveryRule` (Session) — pure: latch when both the local and the host
   rows carry `FlagPartyWiped`; hold through the local teardown (`local == null`) and the
   host's parked away/wiped row; trigger exactly once when the host's row is standing in a
   named module without the downed flag; disarm. `SessionManager` runs it every tick and feeds
   `RejoinTrigger.WipeRecovery` into the same `ClientLoadRejoinRule` gate as `HostReload`;
   `RejoinAfterLoadAsync` is called with `inSession: true` (the client's own world is gone).
   Client log: `the host is back in the world after the wipe - rejoining their world` →
   `session-save: starting rejoin (WipeRecovery)` → `rejoined 'END_M01AA'`. Eight rule tests.

**Three crashes/loops the first recoveries exposed, all fixed and re-verified:**

- **Client AV after the rejoin** (`FIRST-CHANCE … eip 0x69DEF3DD`, unsymbolizable at first).
  `CrashProbe` now logs the plugin's own image range at install and stamps `(OURS: rva 0x…)`
  on a first-chance line inside it; `llvm-symbolizer --obj=dinput8.dll --relative-address`
  put RVA `0xAF71D` at `Odyssey.IsPlayerCharacter` (Odyssey.Health.cs). Cause: after the
  reload the client's CSWParty still held member id `0x127` (the host's companion) BEYOND its
  count; that id now named a `CSWSItem` (vtable `0x748CC0`), and `+0xA74` (CreStats) on an
  item faulted. Fix: `IsPlayerCharacter` refuses anything whose vtable is not
  `CSWSCreature` (`0x007470C8`), logging the first ten refusals (`is-pc: 0x… (id 0x127) is
  not a creature (vtable 0x748CC0)`), and `DeathController`'s latched PC pointer is
  re-validated through it every use — an id outlives the object it named. The next two runs
  logged the refusals and no fault; the run after that logged neither.
  `resolve party` (new console verb, `CheatTargetParty = 0xFFFFFFFE` — not `0xFFFFFFFF`,
  which is what member[0] holds for the PC: `0x7FFFFFFF | ControlPartyNpcFlag`) dumps the
  member array; post-rejoin shape on the client was `count 1; [PC, 0x80000127 → NOT a
  creature, 0x80000129 → companion hp 36]`, on the host `count 2; [PC, companion, empty]`.
- **Party-pool inflation loop** after a same-module reload: every 10 s the client logged
  `expected-reconcile entry for 'g_w_blstrrfl001' never observed landing … presumed
  resolved` and `local add of 1x 'g_a_clothes01' detected`, the host `added 'g_a_clothes01'
  x1`, forever; plus `script item grant: … end_door05` (a door tag as an inventory item) on
  both sides during the reload. Cause: `KotorGameBridge` dropped its object-id caches
  (`_itemTags`, `_itemAddresses`, spawn/creation/suppression) only on a module NAME change.
  A reload of END_M01AA into END_M01AA re-mints every id and the caches answered with the old
  world's facts until the 15 s background rescan — and a rescan that started mid-teardown
  applied freed item bodies with half-overwritten ids/tags. Fix: the reset also fires on a
  `WorldLoadSerial` edge (the plugin counts teardowns), bumps a new
  `IWorldStateBridge.WorldEpoch`, and background scan hits requested under an older epoch are
  discarded; `ScriptItemGrantPump` re-baselines on a `WorldEpoch` change exactly as on a
  module change (test `AReloadOfTheSameModuleResetsTheBaselineSilently`).
- **The rejoined client's carried stacks were silently deleted.** After the reload the client
  re-requested its two stacks (`requesting (serial 1)`, `(serial 2)`); the host never applied
  them, the client then removed both from its own inventory. Cause: `CoopHost` dedups intents
  by (peer, serial) and remembers 4096 pairs; a rejoin builds a fresh `CoopClient` that
  reclaims the same peer id (the nonce is the stable `PeerIdentity`) and restarted its serials
  at 1 — so its first intents matched the pairs remembered from the first join, were acked as
  replays, and the acks were read as landings. Fix: `IntentSerialBase.Mint()` — every client
  instance's party-pool and store intent serials start from a random base in [1, 2^31). Live
  after the fix: `requesting (serial 1489680311)` → host `added 'g_i_medeqpmnt01' x1`. Test
  `ARejoinedClientsIntentsAreNotDedupedAgainstItsFirstLifetime` (loopback, two lifetimes of
  one identity) fails with the base at 1 and passes with the fix.
- Same run, smaller: the client's post-reload pool reconcile added two stacks at :55, its
  inventory sweep first saw them at :11 (the 15 s cache rebuild), and the 5 s
  `RecentlySyncedItems` budget had expired — both went to the host as script grants and the
  host gave itself duplicates. The window is now 20 s (bounded below by `ItemTagRebuildMs`).

**Final state, three cycles in a row on one build:** release → both away → host reload →
client `rejoined 'END_M01AA'` within ~1 s of the host standing → both `hp 12/12`, `who` sees
each other, the client's carried stacks land in the host's pool once, no pool/grant traffic
for the following two minutes, no first-chance faults in either plugin log.

**Still NEEDS-LIVE from this section:** step 5 (`client_temp_death_suppress=1`), and the
in-world `HostReload` variant (host `loadgame` while the client stands in a world) on the
`inSession: true` rejoin path.

**Rig notes that cost time:** two `blockpoke.ps1` pokes to the SAME pid at once collide (the
host downed only slot 0 and the client auto-revived at 1 hp) — sequence the verbs per pid and
parallelise only across pids; `Get-Process swkotor` tells you the rig is up, `pgrep` does not;
the demo's adoption may pick a **session-saved character from a previous rig run** ("newer
than the own save") — expected for the rig, worth knowing when the client's inventory does
not match its save.
