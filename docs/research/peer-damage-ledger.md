# Carrying a peer's contribution as DAMAGE DEALT, not as sampled health (2026-08-07)

Why enemy health could not express what one player did to a fight, what the engine actually
does with a damage effect (verified from the instruction stream), and the ledger that
replaced the health min-merge in both directions.

Read `docs/research/combat-when-apart.md` §6a/§6b first — it is the measurement this
redesign answers — and `docs/research/combat-participation.md` §4 for the hook this reuses.

Tag key: **VERIFIED** = read from the instruction stream of the GOG retail `swkotor.exe`
1.0.3 (disk == memory, no ASLR, image base `0x400000`) with `tools/disasm.py` and
`tools/xrefs.py` · **INFERRED** = deduced from code I did read · **NEEDS-LIVE** = must be
confirmed on the running game. **Nothing in this document was measured in a running game.**

---

## Headline

**Health is the wrong shape for a peer's contribution, and no amount of latching fixes it.**
Health is a LEVEL. A level has to be sampled, and the owner's mirror writes its own value
over the peer's copy several times a second, so a peer's hit had to win a race to be
observed at all. §6a's fixes (mirror lower-only, latch the lowest reading, lift the pin at
the floor, report a vanished-at-the-floor creature as dead) each closed one leak in that
race, and together they worked — but they are four mechanisms serving a channel that can
never say WHO dealt what, and one of them cost owner-side healing entirely.

**Damage dealt is a SUM, and a sum cannot be raced, because it is never read back out of the
game.** The plugin already hooks the single choke point for every point of damage in the
game (`CSWSEffectListHandler::OnApplyDamage 0x004DFA40`) and accumulates a cumulative total
per phantom. The same hook now also accumulates a cumulative total per TARGET, for damage
whose creator is the local player or party. The owner keeps the last total it applied per
(peer, enemy) and spends each increase as real engine damage on its own copy.

**Three verified facts make it safe to re-run the receiving side's damage pipeline** — which
is the trap the "replicate health absolutely" rule exists to avoid, so it needed proving
rather than assuming. See §2.

**And the kill falls out of the arithmetic.** An enemy whose accumulated damage exceeds its
health dies in the owner's own engine, with the corpse, loot, XP and OnDeath script the
engine runs for any other death. There is no "it is dead" report to trust, no request
channel, and no body left standing at zero hit points.

---

## 1. What the engine does with a damage effect — decoded

All VERIFIED unless tagged. `CSWSEffectListHandler::OnApplyDamage` is `__stdcall`,
`ret 0xC`, `(CSWSObject* target, CGameEffect* effect, int bGameLoading)`.

### 1.1 The order of operations, and where our hook sits

Our detour runs at the function's first instruction, i.e. **before everything below**.

| Address | What happens |
| --- | --- |
| `0x004DFA94` | `GetDead` / `GetDeadTemp` on the target → exit. **A corpse still reaches our hook.** |
| `0x004DFAC0`–`0x004DFAF9` | Loop `i = 0..14`: `pEffect->GetInteger(i)` into a stack local. Plot target (`target+0xF8`) or the global `[0x00832878]` zeroes each component (the local, not the effect). Slot 14 is copied aside at `0x004DFAEA`. |
| `0x004DFAFB` | **If slot 14 is 0 and the target is not plot → exit with no damage at all.** |
| `0x004DFB10`–`0x004DFB69` | Reads integers 15..21 (feedback/metadata). |
| `0x004DFC35` / `0x004DFC4F` / `0x004DFC73` | The target's **immunity / resistance / reduction virtuals** (`vtbl+0xAC`, `+0xA8`, `+0xA4`). Each result **overwrites** the running total. |
| `0x004DFD10`–`0x004DFD23` | The fifteen typed locals are copied into the target's per-type damage tally at `target+0x168`. |
| `0x004DFD44` | If the (post-resistance) total ≤ 0 → skip the damage. |
| `0x004DFD9B` | `CSWSCreature::DoDamage(this=target, amount=total, bFeedback)` — the hit points come off here. |

**Two consequences, and both are load-bearing:**

1. **Slot 14 is not "one more damage type" — it IS the number the engine applies.** The
   other fourteen are the breakdown and go nowhere but the tally at `+0x168`. The previous
   code summed the typed slots and used slot 14 only as a fallback; the two agree in every
   case measured live (a 7-point universal hit reads `[3]=7` and `[14]=7`), but the engine's
   own rule is slot 14. The hook now prefers slot 14 and keeps the typed sum as the fallback
   for an effect that leaves it unpopulated (unset slots read `-1`, not `0`).
2. **The amounts our hook reads are PRE-RESISTANCE.** The resistance virtuals run after us
   and replace the total. This is the fact §2 turns on.

### 1.2 Difficulty scaling does NOT touch an enemy

`CSWSCreature::DoDamage 0x004F3830`, disassembled in full for this:

```
0x004F3856  call 0x4AEE70            ; CServerExoApp::GetPartyTable
0x004F3867  call 0x563740            ; is this creature in the party table?
0x004F386E  jne  0x4F3878            ;   yes -> difficulty path
0x004F3870  cmp  [ebp+0x9D4], 0      ; is_pc ?
0x004F3876  je   0x4F38E5            ;   neither -> SKIP THE SCALING ENTIRELY
0x004F3878..0x4F38E3                 ; difficultyopt.2da row [clientoptions+4] -> fmul
0x004F38E5  ebx = raw damage         ; the skip path
0x004F38E9  cmp  [ebp+0xA88], 0      ; NOT in party?
0x004F38F1  mov  eax, [0x007A1B30]
0x004F38F6  cmp  eax, 1
0x004F38F9  jle  0x4F3900            ;   <= 1 -> no multiply
0x004F38FB  imul eax, ebx            ; non-party global damage multiplier
```

So the difficulty slider applies to **party members and the PC only**. An enemy skips it.
The remaining non-party multiplier reads `[0x007A1B30]`, which is **`1` in the shipped image
and has exactly one reference in the whole binary — the read above** (`tools/xrefs.py`
finds no writer), so the `jle` always takes. **An enemy takes raw damage, unscaled, on both
instances regardless of either player's difficulty setting.**

---

## 2. ⚠ THE TRAP: is re-running the receiver's resistance pipeline double-counting?

This is the rule the whole enemy channel was built around — "health is replicated
absolutely, never as damage, because each instance applies its own difficulty multiplier,
temporary hit points and damage resistance" — so forwarding a damage number needed the
argument written down rather than waved at.

**It is not double-counting, for three independently verified reasons.**

1. **The number has had NO resistance applied.** We read the effect's integer list at the
   entry to `OnApplyDamage`; the resistance/immunity/reduction virtuals run afterwards and
   overwrite the total (§1.1). Applying it through the receiver's `EffectDamage` therefore
   runs the pipeline exactly once, on both instances, not twice on one.
2. **The two bodies are THE SAME CREATURE.** Same module, same blueprint, same equipment,
   same object id, and the health mirror keeps them in step. "The receiver's pipeline" is
   the pipeline the dealer's own copy already ran over that same raw number, so the two
   arrive at the same hit-point loss by construction.
3. **Difficulty cannot desynchronise it**, because it does not apply to an enemy at all
   (§1.2). This was the concrete asymmetry the original rule was protecting against.

**And note what this is emphatically NOT.** The phantom channel forwards damage between two
*different* creatures — a bare spawned phantom on one side and a real player character with
its own armour, resistances, temp hit points and party difficulty scaling on the other. For
that pair, replicating absolutely is right and re-running is wrong, and that stays exactly
as it is. The distinction is not "damage versus health"; it is **whether the two bodies are
the same creature**.

**Residual risk (INFERRED, NEEDS-LIVE):** a temporary effect present on one instance's copy
and not the other's — a resistance buff from a script that fired on one side only — makes
the two pipelines disagree for as long as it lasts. The health mirror pulls the copies back
together afterwards, so the error is transient, but the size of it has not been measured.

---

## 3. The channel

```
peer                                            enemy owner
────                                            ───────────
OnApplyDamage detour
  creator ∈ {my PC, my party}?
  target is a creature, alive, not a phantom,
  not one of my own bodies?
      DamageDealtTable.Add(targetId, slot14)
                │
   PeerDamageController (frame thread)
      block 0x1E60: 32 x {id, cumulative}
      + count / dropped / generation
                │
   agent: bridge.ReadDamageDealt()
                │
      EnemyDamagePacket (type 32, per tick,
      unreliable) { peer, module, generation,
                    [(id, cumulative)] }
                │
                └──────────────────────────►  CoopHost: freshness (2 s) + module gate
                                                        │
                                              PeerDamageLedger.Merge
                                                bank each peer's RISE per enemy
                                                        │
                                              bridge.TryPublishDamageOwed(epoch, table)
                                                block 0x2018: 32 x {id, cumulative}
                                                        │
                                              PeerDamageController.ApplyOwed
                                                delta = owed − applied (cap 200/pass)
                                                Odyssey.ApplyDamageEffect(id, delta)
                                                        │
                                              the engine's own damage → and, at ≤ 0,
                                              the engine's own DEATH
                                                        │
                                              enemy snapshot says Dead ──► peer's
                                                                          CombatController
                                                                          runs the real death
```

Every hop carries a CUMULATIVE TOTAL and applies the difference. That is the property that
makes the channel order-independent, immune to a duplicate, and costs one tick of staleness
rather than a hit when a datagram is lost.

### 3.1 Why each rule in the merge exists

| Rule | The failure it prevents |
| --- | --- |
| Bank each peer's **rise**, do not sum their current totals | a peer going quiet (disconnect, module change, a report gone stale) instantly un-hurts the enemy |
| Ignore a **decrease** within a generation | datagrams arrive out of order; a smaller total is an older total, not a heal |
| A new **generation** resets that peer's history | a peer that leaves the module and comes back restarts at zero, and would otherwise look permanently stale — its whole remaining session silently ignored |
| The plugin adopts a new **epoch** by clearing what it applied | the agent zeroes on a module change; a plugin still holding a high applied total swallows the new module's damage until it exceeds the old one's running sum |
| The plugin **adopts** an owed total below what it applied | a restarted agent must not replay a fight, and must not go deaf either |
| `Commit` is separate from `Pending` | the engine call can be refused (no VM, a conversation, a creature not here); advancing on a refusal loses the damage for good |
| A full ledger **refuses** rather than applying untracked | applying with nowhere to record it re-applies the same damage every pass until the creature dies of it |
| The owed table is ordered **live fight first** | it only grows, the plugin's side is 32 slots, and a long session in one module would push the current fight past the truncation |
| Totals **saturate**, never wrap | a wrap reads as a reset on the far side and hands the whole fight over to be applied a second time |

All of the above are unit-tested in `tests/KotorCoop.Tests/PeerDamageLedgerTests.cs`,
including two end-to-end runs (loss + duplication + reordering, and a module round trip).

### 3.2 ⚠ The feedback loop, and where it is cut

The plugin applies synthetic damage effects of its own — `KillCreature` (a 9999-point
`EffectDamage`), the incoming-phantom-damage apply, and now the owed-damage apply. Every one
of them is dispatched from a script context that names the local player, so the hook would
credit them as OUR damage: the mirroring side finishing a mirrored enemy would report 9999
fresh damage to the owner for a creature the owner had already killed.

`PhantomHitHook.SuppressDealt` is set inside `Odyssey.ApplyDamageEffect` itself, with a
`finally`, so no call site can forget it. The **phantom** half is deliberately left visible:
the test hook stages a real hit on a phantom through exactly that call, and blinding it
would blind the tool that verifies the whole path.

### 3.3 What the hook filters, and why each filter is there

Inside a callback that runs for **every point of damage in the game**, in order, cheapest
first — all plain reads, no engine calls, no allocation:

* the attacker set is empty → stop (this is how the fight's OWNER opts out entirely);
* target is a watched phantom → the existing phantom path instead;
* `*(void**)target != 0x007470C8` → not a server creature. Doors, placeables and mines take
  damage through here too, and forwarding a door's id would have the owner bash its own
  copy of that door;
* `*(short*)(target + 0xDC) <= 0` → already a corpse. The engine's own dead check is a few
  instructions past our detour (§1.1), so splash on a body it has already killed still
  arrives here, and crediting it reports damage nobody took. **⚠ Signed**: hit points are an
  `int16` and an overkilled body parks negative, which read unsigned is ~65525 and looks
  perfectly healthy;
* creator is `0`/`OBJECT_INVALID` → nothing to attribute;
* target is one of OUR bodies → damage between our own bodies is not a contribution to
  anyone's fight, and the incoming-phantom-damage channel lands on the player through this
  very path;
* creator ∈ our attacker set → credit it.

The attacker set is the PC plus the party roster (`serverInternal + 0x1B770 + 0x0C`, `int[9]`
— the offsets `PartyPresenceController` already uses), each validated as still resolving, and
is refreshed on the frame thread every 10 frames rather than resolved inside the hook. A
client holds solo mode so only the PC is swinging today; the alternative test — "anything
that is not one of the mirrored enemies" — silently credits us with enemy-on-enemy damage
the moment the 32-slot census sheds a creature.

---

## 4. What was retired, and why nothing is left fighting

| Retired | Why it is not needed, and why leaving it would be worse |
| --- | --- |
| `EnemyReportPacket` / `PacketType.EnemyReport = 22` | the health channel itself. Value 22 is left as a documented hole so an old build's packets are never re-interpreted. |
| `KotorGameBridge.TryApplyEnemyFloors` (health min-merge, and honouring a `Dead` report) | replaced wholesale. Running both would apply a peer's damage twice — once as a floor and once as real damage. |
| `CoopAgent.LatchLowestSeen` (lowest-health latch + synthesised death for a vanished-at-the-floor creature) | a workaround for sampling a level. There is no level to sample any more. |
| The kill-request channel (block `0x1DB0..0x1DD8`, `TryRequestKills`, `CombatController.ExecutePeerKills`) | the owner's own engine kills the creature, so there is no verdict to ship. It also removes the trust problem: the old rule had to gate a peer's "it is dead" on our copy being alive at ≤ 1 so a stray report could not delete a healthy creature. |
| Lifting the Min1HP pin at the floor | existed so a lone player could land the last blow themselves and report the death back. The death now comes down the ordinary channel, so mirrored enemies are pinned again — always. |
| `_enemyAddresses` / `_plotIds` in the bridge | only ever read by `TryApplyEnemyFloors`. |

**Plot creatures need no special case any more.** The engine zeroes every damage component
for a plot target at `0x004DFAC8` (§1.1), so a peer's damage against one applies as nothing
while the ledger still marks it paid — which is exactly the old "a plot creature's health is
the module's rule" behaviour, for free.

**The mirror's third design.** With damage carried explicitly the mirror could go back to
writing the owner's health absolutely in both directions, restoring healing. It does not
quite, and the reason is cosmetic rather than structural: our copy dips when we hit
something and the owner's value is a round trip behind, so an absolute write bounces the
health bar back up for a second before it comes down for real. So the rule is **lower
absolutely, raise by the owner's DELTA** (`MirrorHealthRule`): the owner's damage still
drags the copy down, and an owner-side heal of 5 heals our copy by 5 instead of overwriting
what we have done. Both instances are applying the same deltas to the same creature, so they
converge; any drift is pulled out by the lower-absolutely leg. Healing propagates again,
which lower-only had cost outright.

---

## 5. Block and wire layout

```
block, plugin -> agent
  0x1E60..0x1F60   32 x { u32 objectId, u32 cumulative }   damage DEALT (id written last)
  0x2000           u32 count
  0x2004           u32 dropped          (a hit with no free slot; should stay 0)
  0x2008           u32 generation       (bumped whenever the ledger restarts)

block, agent -> plugin
  0x200C           u32 epoch            (FNV-1a of the module; 0 = none yet)
  0x2010           u32 count            (written last, after the rows)
  0x2018..0x2118   32 x { u32 objectId, u32 cumulative }   damage OWED (id written last)
  0x2118           u32 dealt-enabled    (opt-in; keeps the hook idle on the fight's owner)

block, plugin -> agent
  0x2014           u32 applications run (diagnostic)

SharedBlock.TotalSize 0x2000 -> 0x2200
```

`0x1E60..0x1F5F` is the range reserved for this work and holds the peer half exactly. The
owner half needed the block to grow by one page: cramming both into 256 bytes would have
halved how many enemies a fight can carry damage for, which is the one failure this channel
exists to remove. Growing is transparent — the agent finds the block by signature and reads
fixed offsets.

Wire: `PacketType.EnemyDamage`. **⚠ Value 32, not the reserved 31** — 31 was taken by
`PhantomDamage` on `main` before this branch started. 302 bytes at a full 32 entries with a
maximum-length module name, a quarter of a datagram.

---

## 6. NEEDS-LIVE checklist

⚠ Item 1 below is now measured — see `shared-state-audit.md` §2 round 54 (2026-08-21): the
channel this whole document describes reached a real, engine-native kill live, on `tar_m10aa`
against `0x33`. The break that made round 53 look like a plugin/protocol bug was a transient
`[W_SAVE]` false alarm silencing the `Enemies` capability, not anything in this document's own
design — fixed at the compat layer (`WorldDigest.Compare` + `SaveIdentitySettle`), not here.
Everything else below is unchanged.

1. ✅ **The lone fight still ends, and now without the crutches.** The §6a test verbatim:
   client alone against a Sith Soldier, host's PC ~120 units away, attacking every 8
   seconds. Expect the enemy's health to fall in step on BOTH screens and
   `combat: killed 0xE on the owner's authority`-equivalent to be replaced by an ordinary
   death — the owner's log should show `combat: applied N of a peer's damage to 0xE` and
   then no kill line at all, because the engine did it. **Measured (round 54, a Black Vulkar
   on `tar_m10aa`, not a Sith Soldier on `END_M01AA` — the shape is identical):** host log
   `combat: applied 6 of a peer's damage to 0x33 (owed 6)` / `... 7 ... (owed 13)` / `... 9
   ... (owed 22)`, then `materialised loot: 1 enemy death(s) noticed this pass: 0x33` — an
   ordinary engine death, no kill-request line, exactly as predicted. The client's own
   mirrored copy followed it: `combat: killed 0x33 on the owner's authority`.
2. **The numbers agree.** Diff the client's `dealt` total for one enemy against the total
   hit points that enemy actually lost on the host. They should match exactly (§2). A
   consistent ratio would mean a resistance is being applied twice; a consistent offset
   would mean slot 14 is not the whole story.
3. **⚠ The slot-14 change to the PHANTOM path.** The hook now prefers slot 14 over the typed
   sum for BOTH halves, which touches an already-verified feature. The
   `taken N hits M / incoming: owed N applied N` log lines are a direct check that the
   forwarded phantom damage is unchanged.
4. **⚠ Aggro attribution.** The owner applies a peer's damage through the VM, so the effect's
   creator is whatever `OBJECT_SELF` resolves to on the owner — plausibly the owner's PC. Does
   an enemy being killed by a distant peer turn on the owner's player, or `SetLastHostileActor`
   to it? It is already fighting the phantom, so this may be harmless or may be wrong.
   Not investigated.
5. **Floating damage numbers on the owner's screen** for hits the peer landed. Probably
   desirable (you see your partner's fight); confirm it is not absurd at 200/pass.
6. **Owner-side healing propagates.** Force-heal or stim an enemy on the owner and confirm
   the peer's copy gains the same amount rather than jumping or staying hurt. This is the
   thing lower-only lost.
7. **The party leg of the attacker set.** With solo mode on, a client's companions are not
   in the world, so this path is untested by ordinary play. Force it (companion spawned on
   the mirroring side) or accept it as unexercised.
8. **`dropped` stays zero** on the client through a large fight (block `0x2004`), and
   `applications run` (`0x2014`) climbs on the host.
9. **A module round trip mid-fight.** Peer damages an enemy, both transition, come back:
   the second fight must count in full. This is the generation/epoch pair, tested as
   arithmetic but never against real transitions.
10. **A bump is needed at integration:** `ProtocolConstants.Version` 14 → 15,
    `PluginHost._block->Version`, and `KotorGameBridge.PluginSignature`. Deliberately not
    done here.

## 7. ⚠ 2026-08-15: the debt was sound; the KEY was not

The first live exercise of a forwarded kill (a client's `slay`) delivered nothing, and the
ledger was not at fault. Two id-space faults were, and both are written up in
[enemy-presence-sync.md](enemy-presence-sync.md) ("the census published GHOSTS"):

1. **The owner published ids it could not itself resolve.** The census is built from a vtable
   scan of committed memory, which cannot distinguish a live creature from the intact bytes of
   a destroyed one, so an id keyed against a ghost is owed for ever. The agent now confirms
   every scanned creature through the engine's own `CGameObjectArray` — the same lookup the
   debt is spent through — before publishing.
2. **The cheat resolved an owner id against the local engine.** Fixed with
   `CheatAuthority.MirrorIds`.

The change that made both findable belongs here, though, and is the lesson worth keeping: the
`Odyssey.ResolveServerObject(objectId) == 0` guard in `PeerDamageController.ApplyOwed` used to
`continue` in silence. Holding an unresolvable debt is the RIGHT behaviour — it lands when the
creature does, and never on a stranger — but doing it silently makes an entire failure class
unfalsifiable: both ends look healthy, for ever. It now logs
`combat: a peer owes N damage to 0xID, which does not resolve here` on its own bounded budget,
separate from the successful-apply budget so a flood of applies cannot crowd it out. That one
line turned "nothing happened" into a located defect inside a single run.

## 7. 2026-09-02: per-attacker attribution on the phantom-damage wire

§6 item 4 named the correctness gap directly: `IncomingDamageController` billed a whole pump
window's forwarded damage to whichever attacker id landed LAST, because the wire carried one
cumulative total plus one attacker id. Under `local_combat`, a window that mixed a locally-
fought attacker with a mirrored one had to be billed wholesale to one or the other — either
double-counted (billed to the local one, which is also swinging for real through the engine)
or dropped (billed to the mirrored one, absorbed, while the local one's own contribution is
never applied at all). This section replaces that single id with a per-attacker breakdown, end
to end: capture, wire, and apply.

### Table layout

Three tables, one shape, all `(attackerId u32, cumulativeDamage u32)` — id written LAST per
entry, same "payload then key" discipline every fixed table in `SharedBlock` already uses:

| Table | Where | Capacity | Semantics |
|---|---|---|---|
| Capture (`PhantomAttackerTable`) | Plugin, per phantom slot (native, `PhantomHitHook`) | 4 | Accumulates raw hit deltas per attacker; least-total eviction when a 5th attacker lands a hit |
| `SharedBlock.OffsetPhantomAttackerSlots` (0x4900..0x4A00) | Plugin -> agent, per phantom slot | 4 x `SharedBlock.SlotCapacity` (8) | Mirrors the capture table, published each drive pass alongside the legacy scalar `OffsetPhantomHitSlots` fields |
| `PhantomDamagePacket.Attackers` | Wire (protocol version 48) | `ProtocolConstants.MaxPhantomAttackerEntries` = 4 | Appended after the legacy 4 scalar fields; capped, never truncated silently — `ReadPhantomDamage` refuses a packet over the cap |
| `PhantomAttackerMerge` | Session (`CoopHost`/`CoopClient`), per target | unbounded input, capped to 4 output | Sums each attacker id across every sender's latest report, takes the top 4 by total |
| `SharedBlock.OffsetIncomingDamageAttackers` (0x4AA0..0x4AC0 — moved past the companion-damage region 0x4A00..0x4A90 at merge time) | Agent -> plugin, ONE target (the local player) | 4 | The merged breakdown, for `IncomingDamageController` |
| `PhantomAttackerAppliedLedger` | Plugin, victim-side (`IncomingDamageController`) | 4 | Per-attacker owed/applied reconciliation — the class that actually fixes the bug; least-total eviction, monotonic per attacker id, a regressed total is treated as a RESET (adopt the lower baseline) not a refusal |

`SharedBlock.Version` 62 (regions 0x4900..0x4A00 and 0x4AA0..0x4AC0, `TotalSize` grown 0x4800 -> 0x4B00, 61
deliberately skipped — reserved for parallel work in flight); `ProtocolConstants.Version` 48
(`PhantomDamagePacket` extended, no new `PacketType`); `KotorGameBridge.PluginSignature` byte 8
`3C` -> `3E`.

### The correctness fix

`IncomingDamageController.OnFrame` now prefers a per-attacker pass
(`RunPerAttackerPass`) whenever the block carries a non-empty attacker breakdown, classifying
EACH attacker independently — absorbed (locally engaged) or applied — instead of one whole-
window verdict. Absorbed and applied entries are tracked in the same
`PhantomAttackerAppliedLedger`, so a mixed window (one locally-fought attacker, one mirrored)
now splits correctly: the local one's contribution is absorbed, the mirrored one's is applied,
in the SAME pass, with neither double-counted nor dropped. Every apply-candidate attacker's
delta is summed into ONE capped `Odyssey.ApplyDamageEffect` call (never per attacker — several
small, individually-under-cap deltas must not add up to a one-shot the cap exists to prevent),
then the spent budget is distributed back across the candidates in order so a capped remainder
stays owed for the next pass, per attacker.

The legacy single-total path (`RunLegacyScalarPass`) is unchanged and is the fallback whenever
the block's attacker table is empty — an older peer's report (rejected at Hello by `E_PROTO`
anyway, so this is not a wire-compatibility path), or before the first attacker id has
resolved this session.

### VERIFIED (by test)

- `PhantomDamageWithAttackerBreakdownRoundTrips`/`PhantomDamageAttackerBreakdownIsCappedAtMaxEntries`
  (`PacketCodecTests.cs`): the wire shape round-trips and refuses/caps correctly.
- `PhantomAttackerAppliedLedgerTests.cs`: mixed-attacker window splits correctly
  (`MixedAttackerWindowSplitsCorrectly`), absorbed vs applied stays independent per attacker
  (`AbsorbedAndAppliedAreTrackedIndependentlyPerAttacker`), reordering/duplication is harmless
  and a genuine sender restart is adopted rather than refused forever
  (`ReorderingAndDuplicationAreHarmless`, `ARestartedSenderIsAdoptedRatherThanRefusedForever`),
  and the eviction rule holds past 4 distinct attackers
  (`EvictionDropsTheSmallestTotalWhenAFifthAttackerArrives`).
- `PhantomAttackerTableTests.cs`: the capture-side table's own accumulation and eviction rule.
- `PhantomAttackerMergeTests.cs`: the session-layer merge sums correctly across senders, keeps
  distinct attackers separate, and caps at capacity by keeping the biggest totals.
- `CoopSessionInterfaceShapeTests.cs`: the new `IDamageSession` members default correctly for a
  session that has not been upgraded.
- `TransitionMovieTests.TheBlockVersionMovedInBothHalves` /
  `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`: the version bump is the same
  number in `PluginHost.cs` and `KotorGameBridge.cs`.
- `MidModuleMovieRelayTests.TheRegionDoesNotOverlapAnyExistingField`: `TotalSize` matches the
  new region's own end.

### NEEDS-LIVE

Nothing in the plugin-side capture/apply path or the wire codec has been run against a live
game this pass — everything above is unit-tested arithmetic and codec round-trips only.

1. **The split itself, live.** Two mirrored enemies attacking one client at once, one of the
   two locally-fought (`local_combat=1`) and the other purely mirrored. Expect the log to show
   BOTH a `combat: absorbed N ... (last attacker 0x<local one's id>)` line AND a
   `combat: took N damage from a hit on our phantom (attacker 0x<mirrored one's id>)` line in
   the SAME pass window, and the victim's hit points to fall by exactly the mirrored attacker's
   contribution — not by both combined (double-count), not by zero (drop).
2. **No double-count, no drop, over a longer fight.** Sum every `took N damage` log line for
   the mirrored attacker across the whole fight and compare it against that attacker's own
   `combat: applied N of a peer's damage to 0x<id>` total on the OWNER's side (the enemy-damage
   ledger, §1/§2) — they should match exactly, the same cross-check §6 item 2 already asks for
   on the other direction of this channel.
3. **The 4-entry cap under real pressure.** Stage (or find) a fight with 5+ simultaneous
   attackers on one phantom and confirm the evicted 5th's damage still counts inside the
   legacy scalar total (so nothing is lost, only its PER-ATTACKER breakdown is coarser) rather
   than vanishing.
4. **A sender restart mid-fight** (a module transition on the attacking peer) does not stall
   that attacker's contribution — `ARestartedSenderIsAdoptedRatherThanRefusedForever` is unit-
   tested, but the live path (CombatPump -> CoopHost/Client -> SharedBlock -> ledger) has not
   been run through a real transition.
5. **Aggro/creator attribution (§6 item 4 itself) remains NOT implemented and NOT investigated
   further this pass beyond confirming it needs fresh RE.** `Odyssey.ApplyDamageEffectCore`
   constructs the `CGameEffect` through the VM's own `EffectDamage`/`ApplyEffectToObject`
   script-command routines; the effect lives on the VM's argument stack between the two calls,
   and this codebase has no verified layout for a VM stack ENTRY (its type tag vs. its engine-
   structure pointer) to safely read that pointer back out and poke `creator_id`
   (`CGameEffect+0x18`) to the local resolution of the attacker id before `ApplyEffectToObject`
   consumes it. `PhantomHitHook` only ever READS that field on an effect the engine already
   built; nothing in this codebase has ever WRITTEN it before applying. Doing this safely needs
   fresh disassembly of `EffectDamage`'s return-to-stack convention and
   `ApplyEffectToObject`'s argument-popping order — neither exists in `docs/re/` today. Left
   undone, behind no config key (there is nothing partially built to gate).

### Live recipe (verbatim, once a rig is available)

1. Load a 3-instance rig (host + two clients) on a module with at least two independently
   targetable hostiles reachable at once (`demo3.ps1`/a hand-built save).
2. Client A: `local_combat=1` (default). Engage ONE hostile so `StartLocalFight` pulls it out
   of the census (log: `combat: local fight started against 0x<id>`).
3. Client A: while still fighting the first, get a SECOND, un-engaged hostile from the same
   owner's census to also land hits on Client A's phantom on the OWNER's screen (walk into its
   aggro range, or `giveloot`-style stage a hit via the existing `testhit` cheat aimed at the
   right phantom slot if a live second hostile cannot be arranged).
4. Watch Client A's `kotorcoop-plugin.log` for, in the same pump window: an `absorbed` line
   naming the locally-fought hostile's id, and a `took N damage ... (attacker 0x<id>)` line
   naming the SECOND hostile's id — proving the split. Confirm Client A's hit points fell by
   exactly the second hostile's contribution (cross-check against the owner's own `applied N
   of a peer's damage to 0x<id>` line for that hostile, per NEEDS-LIVE item 2 above).
5. A single `combat: took N ... (attacker 0x<id>)` line naming ONLY the locally-fought
   hostile's id (double-count) or the absorbed line's total silently swallowing the second
   hostile's contribution (drop) are both regressions of the bug this section fixes.

## 8. 2026-09-02: attacker attribution for forwarded damage — the RE, the design, and why no protocol change was needed

§6 item 4 and §7's own NEEDS-LIVE item 5 named the same open question: every forwarded-damage
apply (`Odyssey.ApplyDamageEffectCore`, the one native path `PeerDamageController`,
`IncomingDamageController` and `CompanionDamageController` all call through) re-runs the
RECEIVING engine's own `EffectDamage`/`ApplyEffectToObject` VM routines, but with no meaningful
creator — so that engine's own "who hit me" bookkeeping sees nobody, or (measured live,
`shared-state-audit.md` line ~4531) an arbitrary stale id left over from whatever script last
touched the VM (`0x2C`, `0x20`, `0x7`, `0x109` — never the real attacker). §7 flagged this as
needing fresh disassembly of `EffectDamage`'s return-to-stack convention and
`ApplyEffectToObject`'s argument-popping order before it could be attempted at all. This section
is that disassembly, done statically against `tools/kotor1_0_3.db`'s GOG retail 1.0.3 image with
`tools/disasm.py`/`tools/xrefs.py`, plus a live SQL query against the symbol DB
(`sqlite3 tools/kotor1_0_3.db`) for the `GetLast*` NWScript routine family. **Nothing in this
section was measured in a running game.** Tag key unchanged from the rest of this document.

### 8.1 The VM argument stack's real layout — VERIFIED

`Odyssey.ApplyDamageEffectCore` already pushes `EffectDamage`'s three int args with
`StackPushInteger`/`StackPushFloat`/`StackPushObject` (`0x005D1010`/`0x005D1030`/`0x005D10D0`).
Every one of those is a 16-byte-aligned adjustor thunk (`mov ecx,[ecx]; jmp <body>` — the same
"vtable slot, not a raw address" shape `docs/re/` already documents for animation dispatch) that
resolves to a body sharing ONE helper, disassembled in full this pass:

```
0x005D15A0  CVirtualMachineStack::PushDefault(this, tag)   [determined by structure, not named in the DB]
```

Called as `push <tag>; mov ecx,<stack struct>; call 0x5D15A0`, where `<stack struct>` is
`internalVm + 0x1AC` — the SAME address `StackPointerOffset` already reads as "the stack
pointer" (it IS the struct's own leading `count` field). Full struct, VERIFIED by tracing every
read/write in `0x005D15A0` and its callers:

```
internalVm + 0x1AC (struct base, "the VM argument stack")
  +0x00  count        int32   (== StackPointerOffset itself)
  +0x08  capacity      int32   (grows by +0x100 entries when count == capacity)
  +0x0C  tag array     byte*   (one byte per entry — the type tag: 3 int, 4 float, 5 string,
                                 6 object, 0x10..0x19 engine-structure family)
  +0x10  value array   dword*  (one dword per entry — the int/float bit pattern, object id, OR
                                 an engine-structure pointer, all stored in the SAME array)
  +0x14  struct-array-allocated flag (gates tags 0x10..0x19 only; unrelated to where the
                                 pointer itself lives, which is still the +0x10 array)
```

`0x005D15A0` grows `+0x0C`/`+0x10` together (two `operator new` calls, sizes `newCapacity` and
`newCapacity*4` respectively — `0x005D15B8`/`0x005D15C3`), writes the tag byte unconditionally at
`tagArray[count]` (`0x005D1634`), then for tag 3/4/6 writes a DEFAULT value (0, 0.0,
`OBJECT_INVALID`) into `valueArray[count]` and increments `count` — the wrapper
(`StackPushInteger` etc.) then overwrites that default with the real value one instruction later,
exactly matching what `ApplyDamageEffectCore` already does. For tag 0x10..0x19 the default write
is `valueArray[count] = 0` (a null engine-structure pointer), gated on `+0x14` being allocated —
this is `StackPushEngineStructure`'s own entry point into the shared helper, not a separate one.

### 8.2 How the effect itself gets onto the stack — VERIFIED

`ExecuteCommandEffectDamage` (`0x00531AE0`, the same handler `ApplyDamageEffectCore` already
calls) builds its `CGameEffect` on the HEAP (`operator_new(0x8C)` — `0x00531BEE` — then
`CGameEffect::Constructor(1)` — `0x00503E40`, called at `0x00531C0A`), zeroes all 15 damage
integer slots to `-1` via `CGameEffect::SetInteger(effect, i, -1)` (`0x005036A0`, called in a
loop `0x00531C46`-`C54` — this is the source of "unset slots read -1" §1.1 already documents),
then writes the actual amount **into slot 14** (`SetInteger(effect, 14, nDamageAmount)` —
`0x00531C88`-`91`, independently reconfirming §1.1's "slot 14 IS the number the engine applies").

Only then does it call `StackPushEngineStructure(internalVm, structType=0, effect)` — the site is
`0x00531D01`-`0A`, thunk `0x005D10B0` → body `0x005D19C0`. That body:

1. Reads the active command implementer (`internalVm+0x3D4` — the SAME
   `CommandImplementerOffset` `Odyssey.GetImplementer` already uses); refuses (returns 0, no
   throw) if none.
2. Calls `implementer_vtable[0x20](implementer, structType, effect)`. Its return value (`ebp`) is
   a DIFFERENT pointer from `effect` — this is a clone/registration call, not a passthrough.
3. Pushes tag `0x10 + structType` (`0x005D19EB`: `add ebx, 0x10` — structType 0 for Effect, so
   tag `0x10`, i.e. `EffectStackTag`) via the shared helper from §8.1.
4. Overwrites `valueArray[count-1] = ebp` (`0x005D1A01`) — **the cloned pointer, not the original
   heap `effect`.**

Back in `ExecuteCommandEffectDamage`, immediately after this push succeeds
(`0x00531D2A`-`37`): `call 0x503590(effect)` then `call 0x6FA390(effect)` — a plain MSVC
`delete effect;` (destructor, then `operator delete`) on the ORIGINAL heap object. **The
temporary is destroyed the instant it is pushed — the VM stack's own clone (`ebp` above) is
the only surviving copy from this point on.**

`ExecuteCommandApplyEffectToObject` (`0x0052E570`) is symmetric: it pops the effect back with
`StackPopEngineStructure` (thunk `0x005D10A0` → body `0x005D2630`, called at `0x0052E5A6`
alongside the `nDurationType`/target/duration pops using the ordinary `StackPop*` family) into a
local (`esi`), then — after resolving the target object id and running the (feedback-only,
irrelevant here) creator/area check that reads `esi+0x18` — calls
**`CSWSObject::ApplyEffect(targetPtr, esi)` at `0x0052E6D6` → `0x004D14F0`**, the SAME address
`death-handling.md` and `spawn.md` already document for the non-VM effect-apply path. No further
clone happens between the pop and this call. **The pointer `StackPopEngineStructure` hands back
is the exact same VM-owned clone `StackPushEngineStructure` created — never the original,
already-destroyed heap object, and never re-cloned before use.**

### 8.3 The write, and why it must be OURS, not the engine's own attempt — VERIFIED

`CGameEffect::SetCreator(uint creatorId)` — `0x00503A00` — is already independently documented
(`combat-participation.md` §8, `effect-sync.md` §2.3) and re-disassembled here to confirm the
write order: `mov [edi+0x18], ebx` at `0x00503A1B` happens BEFORE the function's own (best-effort,
irrelevant to the write itself) id-to-pointer resolve-and-cache logic that follows it. So calling
this on the VM's own cloned effect pointer between §8.2's push and pop, with our own resolved
creator id, is a single, unconditional, already-independently-verified field write — no new
engine surface, no guesswork about `CGameEffect`'s layout.

`ExecuteCommandEffectDamage` ALREADY calls this same function internally, conditionally
(`0x00531CFC`, gated by `0x00531CC0`-`E1`: resolve some object via `GetObjectArray`
(`0x004AED70`, per `spawn.md`) + `0x4D8230`, compare a returned byte against global `0x74666C`,
and require a non-null "current script" pointer). This is the VM's own OBJECT_SELF-of-the-
currently-running-script convention — and it is exactly why letting the engine's own logic set
creator is unsafe for a call our C# code dispatches directly, outside any real running NWScript:
there is no "currently running script" in the sense that gate means, so whatever it resolves to
is leftover VM state, not our caller's intended attacker. This is the SAME empirical finding
`shared-state-audit.md` already recorded (creator ids `0x2C`/`0x20`/`0x7`/`0x109`, never the real
attacker) — that measurement is now explained by an actual mechanism rather than left as an
observed oddity. **`TrySetEffectCreator` (`Odyssey.Health.cs`) therefore always OVERWRITES
whatever the internal call already wrote**, immediately afterward, with the caller's own resolved
id — it does not try to make the internal call behave differently.

### 8.4 The approach chosen, and why not (a) or (c)

**Chosen: a refined (b).** Reuse `ExecuteCommandEffectDamage` exactly as `ApplyDamageEffectCore`
already does (clamping, slot population, the duration-power log2 computation, all of it, for
free), then patch `creator_id` on the resulting VM-stack clone via the now-fully-mapped layout
from §8.1/§8.2, using the already-verified `SetCreator` from §8.3 — a single well-understood
native call at a well-defined point, additive to the existing, shipped, live-verified damage
path (§6 item 1's "materialised loot" / "applied N of a peer's damage" measurements).

**Not (a).** Constructing a `CGameEffect` directly (bypassing the VM stack) would mean
re-implementing `ExecuteCommandEffectDamage`'s own field population by hand — the 15-slot `-1`
initialization loop, the duration-power `log2`/`frndint` computation (`0x00531C56`-`73`), the
`0x8327E0` global it writes through, whatever the `0x15`-argument call at `0x00531C1E` does — all
to save exactly one function call this refined version of (b) already reuses. Nothing about (a)
is safer; it is strictly more surface for the same result.

**Not (c).** There is no `SetEffectCreator`-and-apply combined helper — `SetCreator` (§8.3) IS
the primitive (b) already uses; a hunt for a separate "helper that also applies" would have
turned up the same function.

### 8.5 What the engine DOES with creator on damage — VERIFIED (the write), INFERRED (the AI consequence)

**VERIFIED, fresh disassembly this pass:** `CSWSEffectListHandler::OnApplyDamage` itself — the
SAME choke point §1/§4 of `combat-participation.md` already fully decoded — reads
`effect->creator_id` (`edi+0x18`, `0x004DFBA5`) and calls
**`CSWSObject::SetLastHostileActor(target, creator_id, 0)`** (`0x004CC0C0`, called at
`0x004DFCFE`-`01`) on every damage application that reaches that far, writing `target+0x158`.
This is the SAME field `combat-participation.md` §2.5 already names as one of `PacifyCreature`'s
release tests (`+0x158` — matched against the releasing creature's id, cleared to
`OBJECT_INVALID` on release).

**VERIFIED, fresh SQL query against `tools/kotor1_0_3.db`'s `functions` table** (the
`ExecuteCommandGetLast*` family, `CSWVirtualMachineCommands`) plus disassembly of three of them:
the NWScript function **`GetLastHostileActor()`** (`ExecuteCommandGetLastHostileActor`,
`0x0053AF40`) reads the identical field — `mov esi, [edi+0x158]` at `0x0053AFC3` (guarded by a
plot check at `+0xF8` first, same pattern `OnApplyDamage` itself uses). **This closes the loop:
our `SetCreator` write reaches a real, script-visible "who last attacked me" surface, through the
exact same field the engine's own damage handler populates for a real swing.**

`GetLastAttacker()` and `GetLastDamager()` read ADJACENT but DIFFERENT fields — `target+0x15C`
and `target+0x160` respectively (`0x0053A8D5`, `0x0053AD7E`) — and this pass found no writer for
either one anywhere in the disassembly touched so far (not `OnApplyDamage`, not
`ResolvePostMeleeDamage`); they may be written from a code path this investigation did not walk,
or (per standard Aurora-engine convention) exist for combat sub-events this build's K1 content
does not exercise. **`GetLastHostileActor()` is the one this feature's write demonstrably feeds
— the other two are out of scope for this pass, named here only so a future investigation does
not re-derive the offsets.**

**INFERRED, not verified this pass:** whether K1's own stock AI (heartbeat / `OnDamaged` event
scripts — `.ncs` bytecode, not disassembled here) actually CALLS `GetLastHostileActor()` to pick
or change a combat target, and under what conditions (idle → engaged only, or an override of an
already-engaged target too). The already-VERIFIED native retargeting logic
(`CreateNewAttackActions`/`EndCombatRound`, fully decoded in `combat-participation.md` §1) does
**not** itself read `+0x158` — the field is native-write, script-read only, as far as this
investigation walked. So the mechanism this section ships is proven to reach the standard
BioWare "who hit me" script surface; whether K1's shipped AI scripts act on it is exactly what
§8.6's live recipe measures, and is why the feature ships default OFF, tagged NEEDS-LIVE, rather
than assumed to work end to end.

### 8.6 The design actually shipped

Behind a NEW plugin-only coop.ini key, **`damage_creator_attribution`** (default **OFF**,
NEEDS-LIVE): `Odyssey.DamageCreatorAttributionEnabled`, assigned once in
`PluginHost.Startup.cs` alongside a startup log line naming which path is active for the whole
session (not per controller — one flag, one line). `Odyssey.ApplyDamageEffect` gained a
`uint creatorId` overload; the existing zero-arg-creator overload is unchanged (still used by
`KillCreature`, `PhantomController.Drive.cs`'s test-hit staging, and the dev `hurt`/`down`
cheats in `CheatController.Downed.cs` — none of those three forward a peer's damage, so none of
them changed). `ApplyDamageEffectCore` calls `TrySetEffectCreator` (§8.1-8.3's mechanism) between
`EffectDamage` and `ApplyEffectToObject` only when `creatorId != 0`; every refusal path (empty
stack, wrong tag, null pointer — none observed in the disassembly, all defensive) is logged ONCE
per session and the damage still applies with no creator, exactly as if the key were off —
attribution never blocks the underlying hit.

**No SharedBlock field, no protocol change, no version bump.** The task that opened this
investigation provisioned block space (`0x4AC0..0x4B00`) and a `Version` 63→64 bump in case one
was needed; it was not. Every id each controller passes is already reachable from data already
in the block:

| Controller | Creator passed | Why this id, and its limit |
| --- | --- | --- |
| `IncomingDamageController` — `RunPerAttackerPass` | the SAME `lastApplyAttacker` id its own log line already quotes — a real, specific enemy/attacker creature id, already per-attacker since §7 shipped | Clean: this pass's per-attacker table (`PhantomAttackerAppliedLedger`) keeps DISTINCT attacker ids because there is only ONE victim (the local player); nothing is merged away. The one capped apply call is for the SUM of every apply-candidate this pass, so only one creator slot exists for potentially several attackers' combined delta — the last one is used, same as the existing log line already does. |
| `IncomingDamageController` — `RunLegacyScalarPass` | `block.IncomingDamageAttacker` (the pre-§7 single "last attacker" id) | The wire shape this fallback reads was never per-attacker; this is the best it can do, and matches the log line it already prints. |
| `PeerDamageController` — `ApplyOwed` | `SharedBlockView.SoleRemotePhantomId()` — the sole occupied phantom-id slot, 0 otherwise | The owed-per-enemy table is MERGED across every contributing peer's rise before this instance ever sees a row (§3.1 "bank each peer's rise per enemy") — peer identity is deliberately discarded at that step, by design, for reasons unrelated to this feature. With exactly one remote peer this is unambiguous (their phantom is the only body that could have dealt it); a 3+-peer session falls back to 0 rather than guess which peer's phantom actually swung. |
| `CompanionDamageController` — `ApplyOwed` | `SharedBlockView.SoleRemotePhantomId()`, same helper | `CompanionDamageLedger.Merge` drops the reporting peer id at the identical merge step, for the identical reason (a companion's owed total is per ROSTER SLOT, not per (peer, slot)) — same limitation, same fallback. |

`SharedBlockView.SoleRemotePhantomId()` is a new METHOD, not a new FIELD — it reads the existing
`OffsetPhantomIdTable` (`0x1F80..`, `SharedBlock.SlotCapacity` entries) that `PhantomController`
already publishes every pass for map markers and drive bookkeeping, and returns the one occupied
slot's id or 0. Nothing new crosses the wire; nothing new is written into the block.

### 8.7 Live-verification recipe (verbatim, once a rig is available)

1. `damage_creator_attribution=1` in `coop.ini` on BOTH instances (the host applies with a
   creator; the write only matters on whichever instance's engine actually runs
   `EffectDamage`/`ApplyEffectToObject` for the hit in question — set it on both to cover every
   direction while testing).
2. Load a 2-instance rig (host + one client — the topology `SoleRemotePhantomId()` is
   unambiguous for) on `tar_m10aa` (§6 item 1's own module, a Black Vulkar reachable at once).
   Both instances' `kotorcoop-plugin.log` should show `combat: damage_creator_attribution=ON -
   ...` once, near startup.
3. Client attacks the Vulkar from a distance while the HOST's PC is also nearby, already fighting
   it (or about to). Expect the HOST's log to show its ordinary `combat: applied N of a peer's
   damage to 0x<id> (owed N)` lines (§6 item 1, unchanged) — this is `PeerDamageController`
   passing the client's phantom id as creator now.
4. **The actual test:** watch the Vulkar ON THE HOST'S OWN SCREEN. Prediction: within roughly one
   combat round of the client's damage landing, the Vulkar turns to face and swing at the
   CLIENT'S PHANTOM instead of staying locked on the host's own PC — the visible consequence of
   `GetLastHostileActor()` (if K1's stock AI scripts consult it, per §8.5's INFERRED gap) now
   resolving to a real, targetable, resolvable creature (the phantom) instead of nobody or a
   stale id. Compare against `damage_creator_attribution=0`: the same fight, same positions,
   where the Vulkar's target choice should show no such pull toward the phantom.
5. Cross-check with the console: `GetLastHostileActor` has no console verb today, but the
   Vulkar's `round+0x9CC` current-attack-target (already read by existing dev tooling per
   `combat-participation.md` §8) turning to the phantom's id is the same observation from a
   different angle.
6. No regression check: with the key OFF (default), confirm the existing §6/§7 log lines and
   behaviour are byte-for-byte unchanged — `TrySetEffectCreator` is never called at all when
   `creatorId == 0`, so this should require no explanation beyond "the code path is not entered."

### 8.8 NEEDS-LIVE checklist

1. **§8.5's core question**: does a mirrored enemy visibly retarget onto the attacking peer's
   phantom when `damage_creator_attribution=1`, per §8.7's recipe? This is the one measurement
   that turns "the write reaches a real script surface" into "the feature does what it is for."
   *2026-09-02: measured, NO retarget for an already-engaged creature — §8.10.*
2. **The stack-poke itself, live.** Nothing in §8.1-8.3 has been run against the real game;
   confirm `TrySetEffectCreator` never logs a refusal in ordinary play (an unexpected tag or a
   null pointer would mean this pass's disassembly missed a code path a live run would expose
   immediately). *2026-09-02: VERIFIED live, no refusal in four launches — §8.10.*
3. **The companion and legacy-scalar paths specifically** — §8.7's recipe exercises
   `PeerDamageController` only; `CompanionDamageController` (companion released under
   `companion_local_combat`) and `IncomingDamageController`'s `RunPerAttackerPass`/
   `RunLegacyScalarPass` creator passes are unit-tested arithmetic only (the new
   `Odyssey.ApplyDamageEffect(uint,int,uint,out int)` overload has no plugin-side test of its own
   — the whole VM/native surface it drives is compile-included but engine-dependent, same as
   every other `Odyssey.*` VM call) and need their own live pass.
4. **A 3+-peer session** falling back to 0 (no attribution) rather than guessing — still
   unexercised against a real 3-peer rig (no such rig exists in this codebase's test harness),
   but now pinned at the loopback/unit level — see the 2026-09-02 addendum immediately below.
5. **Whether `GetLastAttacker()`/`GetLastDamager()` (target+0x15C/+0x160, §8.5) matter for
   anything K1's own content does** — out of scope for this feature (only `+0x158` is written),
   named so a later investigation into a script that reads one of those two does not start from
   zero. *2026-09-02: `+0x160` IS written by the same path (read back live); `+0x15C` is not — §8.10.*

### 8.9 2026-09-02: re-checked whether `ApplyOwed` can do better than `SoleRemotePhantomId()` with 3+ peers — it cannot, without a protocol change

Re-read `PeerDamageController.ApplyOwed` (src/KotorCoop.Plugin/PeerDamageController.cs:281-410)
against the question "does the per-attacker table `IncomingDamageController.RunPerAttackerPass`
uses (§7, `PhantomAttackerAppliedLedger`) give the ENEMY-damage path anything §8.6's table
didn't already account for." It does not, and the reason is structural, not an oversight:

- `RunPerAttackerPass`'s per-attacker table exists because that direction has exactly ONE
  victim (the local player) and MANY attackers, so keeping attacker ids distinct costs nothing
  — nobody is merged together on that side. `ApplyOwed`'s direction is the reverse shape: MANY
  attackers (every peer's phantom) hitting ONE victim (the shared enemy), and the wire already
  banks each peer's rise into a single per-enemy total before this instance ever reads a row
  (§3.1 "bank each peer's rise per enemy") — confirmed again at
  `PeerDamageController.cs:313-320`'s own comment, unchanged since §8.6 shipped. There is no
  buffer anywhere on the receiving side — plugin or session — that still carries "peer 2 dealt
  40, peer 3 dealt 15" for a given enemy by the time `ApplyOwed` runs; `DamageOwedLedger`
  (`src/KotorCoop.Plugin/PeerDamageController.cs:55`, backing `_owed`) is keyed by enemy object
  id alone, one running total per id, and the host-side merge that produces the number
  `block.GetDamageOwed` hands back is `PeerDamageLedger` in `src/KotorCoop.Session` (the
  session-side ledger §3.1 describes), which sums every peer's reported rise per enemy before
  it is ever written into the shared block. Peer identity is discarded upstream of the plugin
  entirely.
- Recovering it would mean carrying a per-(enemy, peer) breakdown across the wire and through
  the host merge instead of one per-enemy total — exactly the protocol change §8.6 already
  concluded was unnecessary for the two-peer case and that this task was explicit should not be
  invented speculatively for the 3+ case. So this remains **documentation-only**: no plugin
  change, no protocol change, no `SharedBlock` field, no `Version` bump.
- What changed this round: the current fallback (`SoleRemotePhantomId()` returns the id of the
  one occupied phantom slot, or 0 for zero or two-or-more occupied slots) is now pinned by a
  pure unit test directly against `SharedBlockView` — no plugin runtime, no session, a scratch
  pinned block the same shape `MidModuleMovieRelayTests.NewScratchBlock` already uses —
  `tests/KotorCoop.Tests/SoleRemotePhantomIdTests.cs`:
  `NoOccupiedSlotsMeansNoAttribution`, `ExactlyOneOccupiedSlotIsUnambiguous`,
  `TwoRemotePeersFallsBackToZeroRatherThanGuess` (the task's own "2 remote peers → 0" case),
  `ThreeRemotePeersAlsoFallsBackToZero`, and `ANonAdjacentSingleOccupiedSlotIsStillUnambiguous`
  (the scan counts occupied slots, not slot-0-specifically, so a peer reconnecting into a
  different slot index is still unambiguous alone). This closes item 4 above at the unit level;
  a real 3-peer rig run remains **NEEDS-LIVE**, unchanged.

### 8.10 2026-09-02 (night): the live pass — the write lands, the engine does NOT retarget on it, and the round found a poisoned ledger row

Rig: block 64, `damage_creator_attribution=1` on both inis for the whole F and C rounds. Full
excerpts in live-verification.md ("Attacker attribution live", 2026-09-02, night).

**§8.8 item 2 — the stack-poke, live: VERIFIED.** Not one `combat: damage_creator_attribution
could not set a creator this pass` line in any session with the key ON (four launches, both
sides, every `ApplyOwed`/`CompanionDamageController.ApplyOwed`/`IncomingDamageController` pass).
The disassembly in §8.1-8.3 matched the shipped binary.

**§8.5's field-level claim — VERIFIED, and wider than §8.5 said.** After the host applied a
client's forwarded hits, the target's SERVER object read `lastHostile (+0x158)` = the client's
phantom id **and** `lastDamager (+0x160)` = the same id — on `end_gensith` 0x3B (both fields
0x12D, the phantom of that launch) and again on guard droid 0x4D in tar_m10aa (both 0x12F, the
phantom after a rejoin had reassigned it). So `+0x160` (`GetLastDamager()`) IS written on this
path too; §8.5's "no writer found" was a gap in the walk, not in the engine. `+0x15C`
(`GetLastAttacker()`) stayed `OBJECT_INVALID` throughout — that one really is a different event.

**§8.8 item 1 — does the engine retarget onto the phantom? NO, measured.** Guard droid 0x4D
(server object `0x17C07418` on the host) was shooting the host's PC: `round+0x9CC` (the current
attack target, `combat-participation.md` §1) read `0x7FFFFFFF`. The client walked up
(`tp 148 92 2.25`, 1.5 m from it), `attack 0x7FFFFFFF 0x4D`; the host applied the client's
first hit at 22:23:32.5 (`combat: applied 4 of a peer's damage to 0x4D (owed 4)`), and from the
next sample on `lastHostile`/`lastDamager` read `0x12F`. Sampled every 2.5 s for the next 27 s:
`round+0x9CC` stayed `0x7FFFFFFF` on every read and the host's `combat ground-truth: local PC
took N damage, attacker=0x4D` lines kept coming (10, 3, 1, 3, 1, 4, 7, 8 hp) until the round
ended (`0x7F000000`) when the client walked off. **The field is written; nothing in K1's shipped
AI acts on it for an already-engaged creature.** That matches §8.5's own reading of the native
code (`CreateNewAttackActions`/`EndCombatRound` never read `+0x158`) and is the honest status
of this feature: a correct, script-visible "who last hit me", not an aggro pull. A hostile that
is NOT already fighting anyone was not measured (the tar_m10aa Vulkars need an ordered attack
to move at all — `combat-participation.md` §12), so the idle → engaged case stays open;
`default OFF` stands, and the roadmap row says what the key does and does not do.

**What the round found instead: a poisoned ledger row (fixed the same night).** At 22:23:34.4
the host had applied the client's real 3 damage to `tar10_vulkarpt02` 0x46 (`owed 3`). One tick
later the row read **`owed 1634994435`** (0x61747103): `DamageOwedLedger`'s per-pass clamp
turned that into `combat: applied 200 of a peer's damage to 0x46` nine times in 0.9 s, the
Vulkar died of a hit nobody had landed, and the debt sat at 1.6 billion for the rest of the
module (`blockpoke.ps1 ledger`, new this pass, showed `owed[1]: id 0x46 total 1634994435`
minutes later). The client's plugin log had rotated over that exact second (8.4 MB cap), so
the effect that carried the number is unnamed. Every layer between the hook and the apply is
straight arithmetic (`DamageDealtTable.Add` saturates, `PeerDamageLedger.Merge` banks the rise,
`Pending` clamps), so the number came out of `PhantomHitHook.ReadTotal` on the client — an
effect whose int list this hook does not understand, or an int list shorter than its count.
Shipped:

* `PhantomHitHook.OnDamage` refuses any total above `MaxPlausibleHit` (2000) for all three
  cumulative totals — enemy dealt, phantom taken, companion taken — and dumps the first five
  such effects (vtable, header dwords, creator, int count and values) so the next occurrence
  names itself. The engine's own apply is untouched.
* `PeerDamageLedger.Merge` and `CompanionDamageLedger.Merge` refuse a per-tick rise above
  `MaxPlausibleRise` (10,000). The baseline still moves to the reported total, so the next real
  hit is credited from there; `RefusedRises`/`LastRefused` feed one `combat: REFUSED an
  implausible damage rise ...` line per refusal in the combat pump.
* Tests: `ARiseNoFightCouldProduceIsRefusedAndTheNextRealHitStillCounts`,
  `TheLargestBelievableRiseIsStillBanked`, `ACorruptFirstReportOfANewGenerationIsRefusedToo`,
  `CompanionDamageLedgerTests.ARiseNoFightCouldProduceIsRefused`.

Open: which effect it was. The hook's dump will say; until then a mirrored fight can no longer
be ended by it.

## 9. 2026-09-03: threat retarget — an ACTIVE substitute for §8.10's passive dead end

§8.10 measured, live, that `damage_creator_attribution`'s write of the client's phantom id
into `+0x158`/`+0x160` reaches no native retargeting logic for a creature already fighting
someone: `round+0x9CC` stayed pinned on the host's own PC for 27s of the client's own hits
landing on the same guard droid, sampled every 2.5s. Correct "who last hit me", not an aggro
pull — and §1 of `combat-participation.md` already fully decoded WHY: `CreateNewAttackActions`
and `EndCombatRound`, the only two functions that ever write `round+0x9CC`, never read
`+0x158` anywhere in their own logic. There is no passive lever left to pull here; the only way
to make an enemy switch onto whoever is actually hitting it is to tell it to, directly, through
the same primitive the test battery already uses to stage a fight in the first place.

### 9.1 The mechanism

Entirely host-side, entirely additive — no protocol change, no new wire field, and (see §9.4)
no `SharedBlock` growth either. Behind a NEW plugin-only coop.ini key, **`threat_retarget`**
(default **OFF**, NEEDS-LIVE), wired the identical way `damage_creator_attribution` is:
`Odyssey.ThreatRetargetEnabled`, assigned once in `PluginHost.Startup.cs` with a startup log
line, read by exactly one call site.

**`ThreatTable`** (`src/KotorCoop.Plugin/ThreatTable.cs`) is pure C# — no engine calls, no
unsafe pointers, a plain `Dictionary<uint, Dictionary<uint, …>>` keyed first by creature id
then by attacker id. This is deliberately NOT the fixed-array-on-the-hot-hook-path shape
`PhantomAttackerTable`/`DamageOwedLedger` use: those live on `PhantomHitHook.OnDamage`, which
runs on every damage event in the whole game and cannot allocate. `ThreatTable` is only ever
touched from `PeerDamageController.ApplyOwed`'s existing per-row loop — itself throttled to
once every 10 frames, and only when a peer's forwarded damage has actually landed (the same
cadence the existing `combat: applied N of a peer's damage` log line already runs at) — so a
`Dictionary` costs nothing measurable here, the same tradeoff the SESSION-side
`PeerDamageLedger` merge already makes for the identical reason.

Each (creature, attacker) entry decays **linearly** to zero over a fixed window (default 10s,
`ThreatTable.DefaultWindowSeconds`) rather than an exponential half-life — simplest to reason
about and to pin with a fixed clock in a unit test, and the curve only changes how fast an
attacker who stops swinging loses priority, not whether the feature works at all.

**`ThreatRetargetRule.Decide`** (`src/KotorCoop.Plugin/ThreatRetargetRule.cs`) is the whole
decision, a static function with no state of its own:

1. `NotInCombat` — the creature's `round+0x9CC` (read through the EXISTING accessor
   `Odyssey.GetCurrentAttackTargetId`, added for `LocalFightActivityRule`'s §13 and reused
   verbatim here, per this task's own instruction not to invent a new offset) is 0 or
   `OBJECT_INVALID`. Never force an idle/non-hostile creature into a fight through this rule —
   that is `local_combat_engage_fallback`'s job, a different feature entirely.
2. `NoTopAttacker` — nothing is tracked against this creature at all.
3. `AlreadyTarget` — the top attacker already IS the current target; nothing to do.
4. `BelowMinimum` — the top attacker's threat has not cleared an absolute floor
   (`ThreatRetargetOptions.MinThreatToRetarget`, default 15). This is the test that actually
   matters in practice — see §9.2 below for why the margin test alone is not enough.
5. `BelowMargin` — the top attacker's threat has not cleared `MarginRatio` (default 1.5x) times
   the CURRENT target's own tracked threat.
6. `OnCooldown` — this creature was retargeted more recently than `CooldownSeconds` (default 4)
   ago. A fresh creature with no retarget history is never held back by this (`ThreatTable`
   reports `double.PositiveInfinity` seconds-since for one that has never been marked).
7. `Retarget` — every test passed. `PeerDamageController` then (a) resolves the top attacker's
   phantom body and refuses if it is unresolvable or downed (never onto a dead or unresolved
   target), (b) arms the cooldown UNCONDITIONALLY at this point — win or lose, so a structural
   refusal (e.g. a stale phantom id) cannot hammer `Odyssey.AttackTarget` at the owed-loop's own
   ~6/s cadence for the whole window — and (c) calls
   `Odyssey.AttackTarget(creatureId, topAttackerId, out int failure)`, the EXISTING primitive
   (`src/KotorCoop.Plugin/Odyssey.Faction.cs`) that dispatches through the real
   `ExecuteCommandAttack -> AddAttackActions -> AddAttackAction` chain and arms the round timer
   the same way `QueueAttackAction` does — confirmed live (round 11, that file's own doc
   comment) to take a genuinely fresh creature from inert to landing real damage.

`plot`/story actors are exempted BEFORE anything is even recorded (`Odyssey.IsPlot(creature)`,
checked first in `PeerDamageController.EvaluateThreat`) — the same exemption
`CombatController.Apply` already grants a cutscene actor via the census's own `EnemyView.Plot`
flag, applied here directly off the live engine object since this loop works from a resolved
id, not the census view.

### 9.2 ⚠ WHAT THIS DOES NOT TRACK, and why the margin test is mostly decorative today

Only damage this instance can attribute to a SPECIFIC attacker id feeds the table: a peer's
forwarded damage, via `SharedBlockView.SoleRemotePhantomId()` — unambiguous only with exactly
one remote peer connected, the identical limit `damage_creator_attribution` already documents
(§8.6/§8.9). A 3+-peer session's forwarded damage banks into the ledger the same way it always
has, but contributes NOTHING to the threat table (the attacker id is unresolvable), so
`threat_retarget` simply never fires for that session — falls back to today's behaviour rather
than guessing which peer to name, the same choice §8.9 already made for attribution.

More importantly: **the HOST's own local PC/party damage to the same creature is not recorded
here at all.** This was investigated, not assumed — `PhantomHitHook.Dealt` (the table that
would seem to answer "how much has the local player dealt to this creature") is only ever
populated while `block.DamageDealtEnabled` is true, and that flag is precisely the signal that
THIS instance is NOT the fight's owner (`PeerDamageController.RefreshAttackers`'s own remarks:
"how the fight's OWNER opts out … its own damage is already authoritative, nobody will ever
read its ledger" — `WatchAttackers([])` is called and the hook stops watching entirely). No
other cheap existing ground-truth read for "how much did the HOST's own PC just deal to THIS
specific enemy" was found in this pass. Building one would mean either watching the host's own
PC as an attacker unconditionally on `PhantomHitHook` (a change to a hot per-frame native hook,
out of scope for a host-side-only feature this task asked to build from EXISTING primitives) or
threading a new per-enemy read through `CombatController`'s own health-mirror pass, which reads
health, not damage dealt. Left undone and documented rather than guessed at.

The practical consequence: the current target's own threat almost always reads 0 (it is not a
tracked peer attacker id, and the host is never one), so `BelowMargin` (test 5) is trivially
satisfied by any tracked peer damage at all — the ratio math reduces to `topThreat >= 0`, always
true. **`BelowMinimum` (test 4, the absolute floor) is therefore the test doing essentially all
the real work today**, not the margin. The margin test is still real and still exercised
(`ThreatRetargetRuleTests.BelowTheMarginOverAKnownCurrentTargetRefuses`/
`ExactlyAtTheMarginIsEnough`) for the case where the current target happens to also be a
tracked attacker id (a second peer, in a topology where that peer's own hits are separately
attributable) — it simply is not the common case yet.

### 9.3 VERIFIED / INFERRED / NEEDS-LIVE

**VERIFIED (unchanged from earlier sections, re-cited for this feature's own reasoning):**
`round+0x9CC` is the combat round's own live target field (`combat-participation.md` §1.2,
disassembly); `CreateNewAttackActions`/`EndCombatRound` never read `+0x158` (§8.5, disassembly,
and confirmed by §8.10's live measurement finding no retarget); `Odyssey.AttackTarget` takes a
genuinely fresh creature from inert to landing real damage (`Odyssey.Faction.cs`'s own doc
comment, round 11, live); `SoleRemotePhantomId()`'s fallback behaviour (2-vs-3+ peers) is pinned
at the unit level (`SoleRemotePhantomIdTests`, §8.9).

**VERIFIED, this pass (unit level, 27 tests, `dotnet test … --filter "FullyQualifiedName~Threat"`):**
`ThreatTable`'s accumulation, linear decay (including the "a fresh hit resets the clock on top
of what remains" interaction), top-attacker selection under mixed decay ages, cooldown arming
and the "never held back before a first retarget" case, and per-creature `Forget` versus global
`Clear` (`ThreatTableTests`, 14 tests); every branch of `ThreatRetargetRule.Decide`, including
the not-in-combat sentinel test, the absolute-floor-alone case, the margin boundary exactly at
1.5x, and the cooldown boundary exactly at 4s (`ThreatRetargetRuleTests`, 13 tests).

**NEEDS-LIVE (nothing below has been run against a live game):**

1. **The core question**: does a mirrored enemy actually turn and swing at the peer's phantom
   after `threat_retarget=1` orders it there, the same way the anti-7.4 phantom-targetability
   flip already does (`combat-participation.md`'s own live pass)? `Odyssey.AttackTarget` is
   independently live-verified for a FRESH creature (round 11); this is the first live exercise
   of it against a creature ALREADY mid-round with a different target locked (`round+0x9CC`
   already non-`OBJECT_INVALID`) — whether `AddAttackAction`'s own re-validation (test 1-6,
   `combat-participation.md` §1.2) simply appends a new pending action on top of the existing
   round, or whether a stale round state actually blocks the new order, is unmeasured.
2. **Whether the cadence feels right in practice** — `ApplyOwed` runs once every 10 frames only
   when a peer's damage has actually landed, so the very first retarget after a peer engages a
   fresh enemy waits for that enemy's first forwarded-damage pass, not a whole extra observation
   step of its own. The 4s cooldown and 1.5x/15-point thresholds are rule-defined defaults
   (§9.1), not measured against how a real fight feels — expect to tune them after the first
   live pass.
3. **§9.2's gap, live**: whether the "current target's threat mostly reads 0" reduction actually
   causes visible thrash (rapid re-retargets as the top attacker changes) in a real 3-way tug of
   war (host PC + one peer both hitting the same enemy), which the cooldown (test 6) should
   dampen but has never been watched do so.
4. **The diagnostic region itself** — `tools/blockpoke.ps1 threat` reads
   `SharedBlock.OffsetThreatDebugCount`/`OffsetThreatDebugRows` (version 66) against a live
   process for the first time in the recipe below; only the WRITE side
   (`SharedBlockView.SetThreatDebugRow`) has been exercised, indirectly, by the plugin build
   succeeding.

### 9.4 Block layout — no growth, a genuine first for this file

Every earlier growth in `SharedBlock.cs`'s own history (versions 31 through 65) either
widened `TotalSize` to a fresh `0x100` boundary or claimed a small leftover gap smaller than
what it needed. This is the first to claim a leftover gap and have it fit EXACTLY:
`OffsetMaterialisedOwnerIds` (version 65) ends at `0x4B80`, `TotalSize` stays `0x4C00`, and the
threat-debug region's count/generation dwords plus its 6-row table
(`4 + 4 + 6*0x14 = 0x80` bytes) fill the remaining `0x80` bytes precisely. `SharedBlock.Version`
bumped 65 → 66. The region is diagnostics only — dev-verb output for `blockpoke.ps1 threat`, not
read by the agent, no wire/protocol implication.

### 9.5 Live-verification recipe (verbatim, once a rig is available)

1. `threat_retarget=1` in `coop.ini` on the HOST only — this is entirely a host-side channel
   (the client's own `threat_retarget` value is inert; nothing on a client instance ever reads
   `Odyssey.ThreatRetargetEnabled` from a code path a client's own `PeerDamageController.ApplyOwed`
   would reach, since a client never has rows in `block.DamageOwedCount` for enemies it does not
   own). The host's `kotorcoop-plugin.log` should show `combat: threat_retarget=ON - …` once,
   near startup.
2. Load a 2-instance rig (host + one client) on `tar_m10aa` (the same module §8.7's own recipe
   uses — a Black Vulkar reachable by both players at once).
3. **Stage the lopsided fight.** Host attacks the Vulkar first through the `attack` dev verb
   (`tools/blockpoke.ps1 -ProcId <host> attack <hostPcId> <vulkarId>`) so `round+0x9CC` locks
   onto the host's own PC. Confirm with `tools/blockpoke.ps1 -ProcId <host> ledger` that the
   Vulkar's id is not yet in the owed table.
4. **The client attacks the SAME creature** (`tools/blockpoke.ps1 -ProcId <client> attack
   <clientPcId> <vulkarId>` — the client's own real swing, not a scripted forward) and keeps
   hitting it for several rounds so the host's `ledger`/`combat: applied N of a peer's damage`
   lines accumulate past the 15-point floor.
5. **Sample `round+0x9CC` on the HOST** (`combat-participation.md` §8's existing dev tooling, or
   `tools/blockpoke.ps1 -ProcId <host> threat` — the `currentTarget` column of the Vulkar's own
   row) every couple of seconds. Prediction: within roughly one `ApplyOwed` pass after the
   client's tracked threat clears both the absolute floor and the 4s cooldown, the HOST's log
   shows `threat: 0x<vulkar> retargeted 0x<hostPcId> -> 0x<clientPhantomId> (threat <N> vs <M>)`,
   and the very next `threat` dump's `currentTarget` column reads the client's phantom id.
6. **Watch it happen on screen, on the host's own view**: the Vulkar should visibly turn and
   swing at the client's phantom rather than the host's PC — the same visual confirmation §8.7
   step 4 describes for the passive-write test, but this time driven by an active order rather
   than an unconfirmed AI read.
7. **Refusal path**: repeat with the client's phantom ALREADY downed (or disconnected mid-fight)
   at the moment its threat would otherwise win — expect NO retarget line, and (if `AttackTarget`
   itself was reached and refused for an unrelated reason) a `threat: 0x<id> retarget refused …
   (failure <code>)` line instead, never a silent no-op.
8. **Cooldown**: immediately after step 5's retarget, have the host's own PC land another hit
   restoring its own targeting priority is not possible today (its threat is untracked, §9.2) —
   instead confirm the NEGATIVE: within the 4s cooldown window, no second `threat: … retargeted`
   line appears for the same creature even if the client's tracked threat keeps climbing: `Get`
   the same `threat` row twice, ~1s apart, and see `currentTarget` hold steady.
9. **No regression check**: with the key OFF (default), confirm `ledger`/`combat: applied …`
   behaviour is byte-for-byte unchanged and no `threat:` line ever appears —
   `EvaluateThreat` is never called at all when `Odyssey.ThreatRetargetEnabled` is false, so this
   should require no explanation beyond "the code path is not entered," the same closing note
   §8.7 step 6 makes for `damage_creator_attribution`.

### 9.6 NEEDS-LIVE checklist

1. §9.5 step 5's core question — does the ordered retarget actually turn a mid-round creature,
   confirmed both via `round+0x9CC` and visually on the host's own screen.
2. Whether `AttackTarget`'s re-validation path behaves the same against an ALREADY-engaged
   creature as it does against the fresh one round 11 confirmed (§9.3 item 1).
3. Whether the rule-defined thresholds (1.5x margin, 15-point floor, 4s cooldown) feel right in
   a real fight, or need rig-driven tuning — same caveat every other rule-defined default in
   this codebase carries at ship time.
4. The diagnostic region itself, end to end, against a live process (§9.3 item 4).
5. A 3+-peer session's threat table staying genuinely empty (never firing) rather than merely
   assumed to — `SoleRemotePhantomId()`'s own fallback is unit-pinned (§8.9), but this feature's
   OWN consumption of it (`threatAttackerId` in `PeerDamageController.ApplyOwed`) has not been
   exercised against a real 3-peer rig, which does not exist in this codebase's test harness.

## 10. 2026-09-03: live pass — the `new()` zero-defaults bug found and fixed, two rounds of genuine retargeting, verdict stays OFF by default *(superseded by §11 the same day: ships ON)*

Rig: `pwsh tools/demo.ps1 -Save 1`, `coop.ini` has `threat_retarget=1` on the HOST only, per §9.5
step 1; shipped `profiles/coop.ini.example` keeps it `0`.

### 10.1 Round 1 — the `new()` zero-defaults bug

02:55: the first retarget fired, but the diagnostic read as a "6 vs 0"-style threshold instead of
the documented 1.5x/15/4s — `ThreatRetargetOptions.Default` had been constructed with `new()`,
which for a record struct is the zero-initialiser and does NOT run the primary constructor's
parameter defaults, so every tunable read 0:

```
threat: 0x24 retargeted 0x7FFFFFFF -> 0x13F (threat 6 vs 0)
```

a 6-point hit redirected a Vulkar straight through a "15-point" floor that was really 0. Fixed in
99c6ffe: `Default` now explicitly constructs `new(MarginRatio: 1.5, MinThreatToRetarget: 15.0,
CooldownSeconds: 4.0)` — see `ThreatRetargetRule.cs`'s own remarks on `ThreatRetargetOptions` for
the full account, and `ThreatRetargetRuleTests` for the regression pin.

### 10.2 Round 2 — a genuine retarget, correct thresholds

With the fix in place: `0x3B` retargeted at 15/20 threat, with the host going first — the engine's
AI had chosen the host as the initial target — exactly the case this whole design targets.

### 10.3 Round 3 — the engine's own AI already picks the client when the client attacks first

When the CLIENT was the attacker, the engine's own AI picked the client's phantom as the target
without any retarget order being needed:

```
creature 0x26 currentTarget 0x13A topAttacker 0x13A (threat 9.42)
```

`blockpoke.ps1 threat` showed non-zero sticky rows (count 1, generation climbing), and no "not
retargeted" refusal lines appeared, because top threat never crossed the 15-point floor in this
round.

### 10.4 Verdict

`threat_retarget` only matters when the AI went for the host first — the case the engine's own
targeting already handles correctly (round 3) needs no help from this channel. Live-verified
2026-09-03; ships OFF by default (`profiles/coop.ini.example` keeps `threat_retarget=0`) — see
`docs/limitations.md`'s combat table.

## 11. 2026-09-03 (04:35–04:45): round 4 — the "host first" case measured for 98 s, the retarget SHARES aggro rather than moving it, and the default flips ON

Rig: host pid 4964 (`C:\GOG Games\Star Wars KOTOR (GOG)`), client pid 51068 (`swkotor-gog-p2`),
both `warp tar_m10aa`, `god on` on both, host `tp 160 88 2.25`, client `tp 161 90 2.25`, host
`spawncreature tar10_vulkpoo002 1` → `0x141` (client stand-in `0x138`, phantom of the client on
the host `0x139`), host `applyeffect temporaryhitpoints 0x141 120000 400` so the fight lasts
longer than one party round, then client `attack 0x7FFFFFFF 0x138`. §10's rounds 1–3 had settled
the mechanism but never held a fight long enough to see what the engine does AFTER our order.

### 11.1 What the creature did, second by second

Host log (`combat ground-truth: local PC took N damage, attacker=0x141` is the Vulkar hitting
the host's PC; `threat:` lines are the host-side rule; `attacktarget diag` is the order landing)
against the client log (`combat: took N damage from a hit on our phantom (attacker 0x141)` is
the Vulkar hitting the CLIENT):

```
04:39:00.756  host PC hit (16)
04:39:02.317  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 19 vs 0)    attacktarget diag: round+0x958 0->1, +0x960 0->5DC
04:39:05.253  host PC hit (6)          <- back on the host 3 s after the order
04:39:05.275  threat: not retargeted (OnCooldown) - current 0x7FFFFFFF ... 3.0s since last
04:39:08.849                                                                   client phantom hit (2)
04:39:11.255  host PC hit (8)
04:39:11.384  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 16 vs 0)
04:39:17.285  threat: not retargeted (AlreadyTarget) - current 0x139            <- it STAYED this time
04:39:19.763                                                                   client phantom hit (11)
04:39:21.761  host PC hit (10)
04:39:25.253                                                                   client phantom hit (4)
04:39:27.768  host PC hit (11)
04:39:29.308  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 19 vs 0)
04:39:32.687                                                                   client phantom hit (7)
04:39:35.818                                                                   client phantom hit (8)
04:39:38.269  host PC hit (7)
04:39:38.376  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 25 vs 0)
04:39:43.695                                                                   client phantom hit (6)
04:39:44.481  threat: not retargeted (AlreadyTarget) - current 0x139
04:39:45.770  host PC hit (13)
04:39:49.462                                                                   client phantom hit (5)
04:39:50.452  threat: not retargeted (AlreadyTarget) - current 0x139 ... 12.1s since last
04:39:54.779  host PC hit (5)
04:40:00.780  host PC hit (5)
04:40:02.325  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 20 vs 0)
04:40:08.319  threat: not retargeted (AlreadyTarget) - current 0x139
04:40:11.288  host PC hit (4)
04:40:14.568  threat: not retargeted (AlreadyTarget) - current 0x139 ... 12.2s since last
04:40:15.353                                                                   client phantom hit (6)
04:40:17.295  host PC hit (6)
04:40:17.337  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 15 vs 0)
04:40:22.445                                                                   client phantom hit (10)
04:40:25.250                                                                   client phantom hit (3)
04:40:27.802  host PC hit (11)
04:40:29.220  threat: 0x141 retargeted 0x7FFFFFFF -> 0x139 (threat 19 vs 0)
04:40:38.833                                                                   combat: killed 0x141 (owner id, local stand-in 0x138) on the owner's authority
```

Totals over the 98 s the Vulkar lived: 12 hits on the host's PC, 10 hits on the client's
phantom, 7 retarget orders (every one against `current 0x7FFFFFFF threat 0` — the host PC,
whose own damage the table deliberately does not track, §9.2). Compare §8.10, the same
staging with only the passive creator write: the phantom took NOTHING while the host was
engaged.

### 11.2 Reading it

- The order works every time (`round+0x958 0->1, +0x960 0->5DC` on a fresh round, the phantom
  hit follows 3–7 s later) — but it is not sticky. The engine's own AI re-picks the host PC
  within one or two of its rounds whenever the host PC is also swinging at it (the host's
  `OnAttacked`/last-attacker path, the same one §8.10 showed ignores the creator write). Seven
  times in 98 s the creature went host → phantom → host, and the 4 s cooldown is what
  turned that into a steady alternation rather than a fight over the target every frame.
- Sometimes it does stick for a while — 04:39:11 → 04:39:21 and 04:39:44 → 04:39:54 are ten
  seconds each of `AlreadyTarget` with the host PC still in melee. What breaks it is the host
  PC's next landed hit, not time.
- Net effect in play: with the host's party and the client both fighting one enemy, the
  enemy splits its attacks roughly evenly instead of ignoring the second player. That is the
  "second player can pull aggro" behaviour co-op needs, achieved without the engine's own AI
  cooperating.

### 11.3 What was NOT measured (at the time): the host walking away — *answered in §11.6*

The follow-up (host `tp 130 60 2.25` at 04:40:3x with the client still attacking) was meant
to show whether the retarget sticks when the host stops hitting. It is inconclusive: the
temporary hit points expired at 04:40:29 (120 s after the apply) and the host's companions
killed the Vulkar at 04:40:38, before the host had cleared melee range. The frozen `threat
19.32 / currentTarget 0x7F000000` read afterwards was a dead creature, not a disengaged one.
Rerun recipe if it matters: apply `temporaryhitpoints` for 300000 ms, `tp` the host away
BEFORE the client's first `attack`, and expect zero `attacker=0x141` ground-truth lines on the
host with the phantom hits continuing.

### 11.4 Side observation: the `liveness ANOMALY` transient on the kill

Each kill on the owner's authority produced up to 20 client lines of `liveness ANOMALY: owner
says 0x141 is alive, this instance already has it dead - not resurrecting` for ~1.5 s, until
the host census dropped the id. The kill channel and the liveness snapshot are not ordered
against each other; the reconcile's own rule is to never resurrect, so the only cost is the
budgeted log noise. Recorded in creature-liveness.md's terms as expected, not a defect.

### 11.5 Verdict — ships ON

Four rounds, two Vulkars held for 98 s, 7 orders, 0 faults, and a measurable change from
"the second player is ignored" to "the enemy splits between them". Costs: one `ActionAttack`
per creature per ≥4 s (each resets that creature's round, visible as `+0x960 0->5DC`), only
with exactly one remote peer (3+ peers → inert, §9.2), never a plot creature, never onto a
downed or absent phantom. `profiles/coop.ini.example` now ships `threat_retarget=1`; the code
fallback when the key is absent stays OFF, same as `liveness_sync`. §10.4's "stays OFF"
is superseded by this section.

### 11.6 (04:48–04:51) The host-away rerun: with the host out of reach the engine's own AI picks the client and STAYS — no order needed

§11.3's recipe, run exactly: host `tp 160 88 2.25`, `spawncreature tar10_vulkpoo002 1` → `0x144`
(client stand-in `0x13C`), `applyeffect temporaryhitpoints 0x144 300000 400`, host `tp 130 60
2.25` at ~04:48:34 (the Vulkar had already landed three hits on the host's PC, 04:48:27–33, while
the host stood next to it), client `attack 0x7FFFFFFF 0x13C` at 04:48:41.

- Host log after the `tp`: ZERO further `attacker=0x144` ground-truth lines, and ZERO
  `threat:`/`attacktarget diag` lines for `0x144` at all — the rule never had to fire.
- Client log: `combat: took N damage from a hit on our phantom (attacker 0x144)` from 04:48:48
  on, then (once that budgeted line ran out) the `combat ground-truth: local PC took N damage,
  attacker=0x144 (forwarded-damage applier ...)` lines kept coming every ~3 s — 28 hits by
  04:50:54, uninterrupted.
- `blockpoke.ps1 threat`: `creature 0x144 currentTarget 0x139 (threat 22.29) topAttacker 0x139`.

So the three cases are now all measured: the client attacks first → the engine picks the client
(§10.3); the host is engaged and stays in melee → the engine keeps pulling back to the host PC
and this channel's re-issued order splits the fight (§11.1); the host disengages → the engine's
own AI moves to the client and stays there without help. `threat_retarget` only ever acts in the
middle case, which is exactly the case it was built for.

## 12. 2026-09-24: sticky-aggro stages 1+2 — the host's own damage is now tracked, and the
retarget decision runs every pass instead of only when a peer's hit happens to land

§9.2 flagged the gap this section closes, in the ORIGINAL doc comment on `ThreatTable`, before
any live round ever ran: *"The HOST's own local PC/party damage to the same creature is NOT
recorded here … So the current target's own threat reads 0 unless it happens to also be a
tracked peer attacker id, which in practice means the retarget decision reduces to 'does the top
peer attacker clear the absolute minimum', not a true margin over the incumbent's own
contribution."* §11.1's 98-second round then measured exactly that shape live: 7 retarget orders
against `current 0x7FFFFFFF threat 0` every single time — never once a real comparison against
what the host had actually been dealing — and the project decision (2026-09-03, cited in
docs/limitations.md and the roadmap) named the fix as a staged plan: (1) feed host PC/companion
hits into `ThreatTable`, (2) re-assert immediately when the engine flips away from a clear
leader, (3) only if still not sticky, detour `CSWSCreature::CreateNewAttackActions`. This section
covers stages 1 and 2; stage 3 was not attempted (see §12.5).

### 12.1 Stage 1 — the host's own hits, fed into the same table

`PhantomHitHook` already had exactly the mechanism this needed, just gated the wrong way for the
host's own purposes. `WatchAttackers`/`_ourAttacker`/`Dealt` exist so a MIRRORING instance (a
client, `block.DamageDealtEnabled`) can tell the hook "this is my own damage, forward it to
whoever owns the fight" — the host never populates that set, because it applies its own damage
through the engine natively and has nothing to forward.

A second, independent watch was added alongside it, gated the OPPOSITE way:
`WatchThreatAttackers`/`_threatAttacker` (populated only when `!block.DamageDealtEnabled` — an
instance that owns its own fights — and `Odyssey.ThreatRetargetEnabled`), feeding a small
per-pass drain table (`DrainHostThreat`, `HostThreatCapacity = 32` distinct (creature, attacker)
pairs between two drains) rather than the peer-forwarding ledger. The attacker-matching logic
common to both (creature vtable check, corpse check, "not one of our own bodies" check) was
factored out of `IsOursToCredit` into a shared `TryMatchAttacker`, so the two channels can never
drift apart on what counts as a valid hit.

The id banked for the host's PC is `0x7FFFFFFF` — not a placeholder, the engine's own real,
permanent self-id for whichever creature a client currently drives (see
`NewCharacterCaptureController`'s own remarks on `Odyssey.GetPlayerObjectId`, and the "`current
0x7FFFFFFF`" seen throughout §11's own log excerpts). This is the SAME value
`Odyssey.GetCurrentAttackTargetId` reads back out of a creature's live round when that creature
is fighting the host's PC, so banking damage under this id is what makes `currentTargetThreat`
stop reading 0 for the case §9.2 called out. Party NPCs bank under their own real object ids the
same way a peer's phantom already does.

`PeerDamageController.RefreshThreatAttackers` (parallel to the existing `RefreshAttackers`,
sharing its id-gathering loop via a new `BuildLocalPartyIds` helper) builds this set from the PC
plus every party-roster id that currently resolves to a live object, every 10-frame pass — same
cadence `RefreshAttackers` already uses.

### 12.2 Stage 2 — re-assert every pass, not only when a peer's damage lands this exact pass

Before this change, `PeerDamageController.ApplyOwed` returned immediately when
`block.DamageOwedCount == 0`, and the only place a retarget decision was ever evaluated was
inside the owed-damage loop, once per peer-forwarded hit that pass. That meant a stale engine
flip (§11.1: "the host PC's next landed hit pulls the AI back … within one or two of its
rounds") could only ever be corrected on the NEXT peer hit that happened to land — in practice
the ~4s cooldown already bounded this to roughly the observed cadence, but nothing made it
happen SOONER than that even once the cooldown had cleared.

`EvaluateThreat` was split into two: `BankThreat` (the plot-actor exemption plus the
`ThreatTable.AddThreat` call — unchanged logic, just extracted so both the owed loop and the new
host-drain path can call into it once) and `EvaluateThreat` itself, now PURE decision +
retarget-order logic with no banking of its own. A new `ReassertThreat` runs unconditionally
every pass `threat_retarget` is on (drains `DrainHostThreat` into `BankThreat` first, then calls
`ThreatTable.CopyCreatureIds` — a new, non-allocating method — to walk every creature the table
currently tracks and calls `EvaluateThreat` on each), regardless of whether `block.DamageOwedCount`
was zero that pass. The `IsConversationActive` gate still covers it (retargeting into a frozen
dialogue-instance action queue would be as wrong as applying damage into one).

Net effect: a creature the engine flips back to the host is re-evaluated on literally the next
`ApplyOwed` pass (every 10 frames, ~0.17s) once the 4s cooldown clears, whether or not the peer
happens to land a fresh hit in that exact window — not "eventually, once the peer's next swing
triggers a look."

### 12.3 What changes in the decision itself

With the host's own damage now genuinely banked, `ThreatRetargetRule.Decide`'s margin check
(`topAttackerThreat >= currentTargetThreat * 1.5`) stops being trivially satisfied by any peer
hit clearing the 15-point floor (§9.2's degenerate case: `current 0x7FFFFFFF threat 0`, so
`anything >= 0 * 1.5`) and becomes an actual comparison between two tracked contributions. A
peer that is merely matching the host's damage (a tie, or anything under 1.5x) no longer pulls
the creature at all — the alternation §11.1 measured (7 orders in 98s, every one against a
`threat 0` incumbent) should not reproduce the same way against a host that is genuinely still
fighting hard, though whether it still reproduces at some smaller rate (the host's own damage
naturally lags a little behind its own hits landing, same 10-frame batching as everything else
in this file) is exactly what §12.4's live recipe has to measure.

### 12.4 Unit coverage

`ThreatTable`/`ThreatRetargetRule` stay engine-free (no change there — `PeerDamageController`
itself still cannot be unit tested directly, same reason `ThreatRetargetRuleTests` scans its
source instead of calling into it, see that test's own remarks). A new
`tests/KotorCoop.Tests/StickyAggroIntegrationTests.cs` drives the two pure pieces together —
the exact shape `PeerDamageController`'s engine-touching code wires up — covering:

- The OLD degenerate shape (untracked host damage, any peer hit above the floor retargets) next
  to the FIXED shape (same peer hit, now refused because the host's own tracked threat does not
  clear the margin) side by side, so the fix is pinned by contrast, not just asserted.
- A clear peer lead still retargets.
- An exact tie (host == peer) never retargets either way — whichever id `TryGetTopAttacker`'s
  own tie-break lands on, the decision is never `Retarget`.
- A leader switch the ENGINE flips back mid-cooldown is correctly refused (`OnCooldown`) and
  then correctly re-fires on the very next pass the cooldown allows — pinning stage 2's own
  reason to exist, without needing a peer hit to land in that exact window.
- The former leader genuinely retaking the lead (its own fresh damage, not a bare engine flip)
  flips the decision the other way too.
- "Going away" (a side that stops dealing damage) is not a separate rule — it is what decay
  already models: the other side's tracked threat alone clears the margin once enough time has
  passed with no fresh hits from the first side.

NOT unit tested, and NOT changed by this pass: the "top attacker is downed or unresolvable"
guard in `EvaluateThreat` (`Odyssey.ResolveServerObject`/`Odyssey.IsDowned`, both real engine
calls) still runs exactly where it always did — AFTER `Decide` already says `Retarget`, right
before `AttackTarget` is issued — deliberately left there rather than folded into the pure rule,
because moving it earlier would mean resolving+downed-checking the top attacker on every
evaluated creature every pass (now larger, since `ReassertThreat` evaluates every tracked
creature, not only ones an owed row touched) rather than only the rare pass that is actually
about to issue an order. This guard cannot be exercised without a live creature pointer, so it
stays a live-recipe item (§12.6 step 5), same category of thing as `IsDowned` always was.

### 12.5 Stage 3 — not attempted

The task that produced this section explicitly gated stage 3 (a static-RE'd detour on
`CSWSCreature::CreateNewAttackActions 0x005B6980`) on "only if [stages 1+2 are] still not
sticky" — an empirical question that needs a live rig to answer, which was not available while
this pass ran (see docs/kotor-coop-parallel-sessions and the task's own rule against launching
the game). Stage 3 was therefore NOT designed, RE'd or implemented. If §12.6's live recipe shows
stages 1+2 still alternating (rather than genuinely sticking once the host is clearly behind),
`CreateNewAttackActions` is the next thing to open with `tools/disasm.py`/`xrefs.py`/`q.py` —
its single caller and never-captured prologue are exactly as the task description left them.

### 12.6 Live-verification recipe (NEEDS-LIVE — nothing in §12 has been run against a live rig)

Same staging §9.5/§11 already established (`tar_m10aa`, `spawncreature tar10_vulkpoo002 N`
reachable by both players, `attack <attacker> <target>` via `tools/blockpoke.ps1` for the HOST's
own attacks — **the joiner can only attack by real mouse click, `blockpoke.ps1`'s `attack` verb
is host-side only**, so the client side of every step below has to be driven by an actual click
on the target, not a scripted forward):

1. `threat_retarget=1` on the HOST (default, so this only matters if a rig's `coop.ini` was
   overridden). Confirm the startup log line as §9.5 step 1 describes.
2. Host attacks a fresh Vulkar first (`blockpoke.ps1 attack <hostPcId> <vulkarId>`), locking
   `round+0x9CC` onto the host's own PC (`current 0x7FFFFFFF` in a `threat` dump). Let the host
   land 2-3 real hits (watch `combat ground-truth: local PC took N damage` is NOT what to look
   for here — that is incoming damage; look for the host's OWN damage-dealt log lines, or simply
   the Vulkar's `blockpoke.ps1 ledger`/health falling under the host's own attack) before the
   client joins in.
3. **New check, not in §9.5**: `blockpoke.ps1 threat` on the host should now show a row for the
   Vulkar with `currentTarget 0x7FFFFFFF` and a NON-ZERO threat value under that id — the direct
   confirmation that stage 1 is banking the host's own damage (§9.5's original recipe never
   checked this, because the mechanism did not exist yet).
4. Client attacks the SAME Vulkar by a real mouse click and keeps swinging. Watch
   `blockpoke.ps1 threat` on the host every 1-2s: does the peer's tracked threat have to clear a
   real 1.5x margin over the host's own (nonzero) threat before `threat: … retargeted …` fires —
   i.e. is there now a visible delay/requirement compared to §11.1's immediate-any-hit firing?
5. Once retargeted, have the HOST land another real hit on the Vulkar (not scripted — through
   actual gameplay, so the engine's own AI has a genuine reason to reconsider) and watch whether
   the order re-fires sooner than the old ~4s-alternation cadence, or whether it now holds
   because the host's own margin no longer clears 1.5x over the peer's (now larger) threat. This
   is the core question §12.3 could not answer without a live rig.
6. Cooldown-clear timing: with the client still swinging and the host genuinely disengaged
   (moved away, `tp` on a solo test rig), confirm the order re-fires on the very first pass after
   a stale flip clears the 4s cooldown — sample `threat` twice, 4-5s apart, immediately after an
   engine flip back to the host with no NEW client hit landing in between (§12.2's stage-2 claim).
7. Down/away: down the top attacker (client) mid-fight (e.g. `applyeffect damage <clientPcId>
   9999` or a scripted knockdown) at the moment its threat would otherwise win, and confirm NO
   retarget line fires while it is down — the unmoved guard §12.4 describes.
8. No-regression: `threat_retarget=0` — confirm zero `threat:` lines and unchanged
   `ledger`/`combat: applied …` behaviour, same closing check §9.5/§10.4 already established.

If step 5 shows the order still flapping (the host's own margin never sustains a hold because
its damage keeps pace with the peer's), that is the signal for §12.5's stage 3.
