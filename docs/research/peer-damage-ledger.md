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

Nothing below has been measured. The build compiles and 321 unit tests pass; that is all.

1. **The lone fight still ends, and now without the crutches.** The §6a test verbatim:
   client alone against a Sith Soldier, host's PC ~120 units away, attacking every 8
   seconds. Expect the enemy's health to fall in step on BOTH screens and
   `combat: killed 0xE on the owner's authority`-equivalent to be replaced by an ordinary
   death — the owner's log should show `combat: applied N of a peer's damage to 0xE` and
   then no kill line at all, because the engine did it.
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
