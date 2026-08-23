# Live-test plan: does a released companion's swing land as CREDITED damage?

Status: **RUN, 2026-08-23 (round 82)** — decisive result on hypothesis 2 (solo mode), the
first row of the decision tree below. See `party-reconciliation.md`'s "Round 82" entry for the
full log evidence: a real, sustained multi-Vulkar fight staged via `blockpoke.ps1 attack`, two
independent release cycles (11 samples total), `head` never left `0xFFFFFFFF` (empty) on a single
sample while `soloMode=1` held and the client's own PC was demonstrably taking real damage from
multiple attackers. **Solo mode is confirmed, not merely suspected, to block a released
companion's AI from ever queuing an attack.** No live fix was attempted (the task's own gate for
a scoped per-companion release was not met — see that entry). `companion_local_combat` stays OFF.
The original plan below is left intact as the recipe that was actually followed, for whoever
picks up the "scoped solo-mode release" design next.

## What this settles, precisely

`companion_local_combat`'s release/reclaim gate is already live-proven clean
(`party-reconciliation.md`, 2026-08-22: three release/reclaim cycles, correct faction/pin
handling, no stranding). What has never been observed is the thing the flag exists for: a
companion released to its own engine AI actually landing a hit on the locally-fought enemy, that
hit reaching the fight's OWNER (the host) as real, authoritative damage, and both sides agreeing
on the enemy's HP afterward — the same "credited, not cosmetic" bar `peer-damage-ledger.md`
already proved for the PLAYER's own local-combat damage.

Two prior blockers, both on record, need to be worked around or ruled out in the same pass:

1. **Party-presence staleness** (`party-reconciliation.md`, 2026-08-22): a mirroring client's
   own copy of a companion's position (`SharedBlock.PartyPresenceSlots`) can freeze for minutes
   even in a healthy, connected session, self-clearing only on `disconnect`/`reconnect`. If the
   companion's mirrored body never reaches the fight, it has nothing to swing at, and the run
   proves nothing either way.
2. **Solo mode possibly blocking companion combat AI entirely, not just follow-AI** (new,
   `party-reconciliation.md`'s 2026-08-23 entry): `KotorGameBridge.TryWritePartyMembers` holds
   `CSWPartyTable+0x190` at 1 for the whole client session regardless of release state. Two
   independent, pre-existing documents in this project describe solo mode as stopping
   companions from fighting, not merely from following. This pass ships a read-only diagnostic
   (`PartyPresenceController`'s release-diagnostic log line) built specifically to answer this
   live, cheaply, without touching the engine.

Structure the pass so it produces a clear answer to **both**, not just "did HP move."

## Exact coop.ini settings

On **both** installs (host and client), starting from `profiles/coop.ini.example` defaults:

```ini
local_combat=1                      ; default already; confirm explicitly
companion_local_combat=1            ; OFF by default — this is what is being tested
companion_local_combat_range=15     ; wider than the default 10: gives real walking room to
                                     ; get all three (player, companion, enemy) inside the gate
                                     ; without needing sub-metre positioning
mirror_smooth_ms=150                ; default; do not raise — a slower mirror widens the
                                     ; staleness window this pass is trying to work around
party_fast_pump=0                   ; leave off (still unproven per lag-audit.md's own 2026-08-23
                                     ; entry) unless the staleness bug is not otherwise workable
                                     ; in the time available, in which case try =1 as a SEPARATE
                                     ; leg and say so in the writeup — do not conflate the two
auto_start_agent=1                  ; tools/demo.ps1 restores this on teardown; do not leave off
```

Everything else at `profiles/coop.ini.example` defaults. Do not set `local_combat_bisect_mask`,
`local_combat_bypass_id`, or `DiagUndrivenObjectId` — this pass wants the shipped path, not a
bisect leg.

## Rig and staging

1. `pwsh tools\demo.ps1` — cold start, save 1 (Endar Spire opening, Trask recruited, matching
   every prior round's own recipe so results are comparable). Confirm both instances connect
   clean (`shared`, no `[W_SAVE]`) before doing anything else.
2. Identify both process ids: `Get-Process swkotor | Select Id,MainWindowTitle` (or reuse
   whatever `demo.ps1` itself echoes on startup).
3. On the **HOST**, `pwsh tools\blockpoke.ps1 -ProcId <hostPid> console 'warp tar_m10aa'` —
   HOST-ONLY, per the 2026-08-22 finding: both instances independently warping produced a
   persistent `[W_SAVE]` mismatch that never self-cleared, because each instance's own
   autosave-on-transition genuinely diverged. Let the client follow through the co-op
   travel-follow channel instead of issuing its own warp.
4. Confirm both instances are in `tar_m10aa` and still `shared` (no `[W_SAVE]`) before staging
   combat. If a `[W_SAVE]` mismatch appears and does not clear within ~10-15 s on its own
   (the fast-clearing transient `shared-state-audit.md` already documents), stop and re-roll the
   warp rather than staging combat on top of a desynced session.

## Working around the party-presence staleness bug: real walking, not a raw poke

The 2026-08-22 pass's own conclusion was that a raw position poke to stage the companion
defeats the very channel being tested (the companion is released from wherever its CLIENT-side
body was already standing, which was never smoothly mirrored to begin with). This pass should
NOT use `teleport.ps1`'s raw-poke technique on the companion. Instead:

1. On the HOST, walk (or `console 'tp <x> <y> <z>'` — a REAL engine teleport, which
   `PartyFollowController`'s own round-73 methodology note already established as the one thing
   that keeps every downstream consumer, including the party-presence publish, correctly in
   sync — never a raw `+0x90` poke) toward a Black Vulkar in `tar_m10aa`'s point-blank pool
   (`shared-state-audit.md` round 55's own recipe, object id `0x33` in that save/module).
   `console 'tp'` is a real engine position change; a raw poke to the HOST's own creature is not
   what is being avoided here — only the COMPANION's position must never be raw-poked, because
   that is the value the client-side mirror channel is supposed to carry, and poking it locally
   on the host bypasses the very publish/mirror hop this pass is testing.
2. Let the host's own native companion AI/follow bring Trask along BEHIND the host as the host
   walks the last ~15-20 units toward the Vulkar, rather than warping/poking her into position.
   This is slower but is the only way to know the client's own mirrored copy is tracking a
   REAL, continuously-updated position rather than a single stale snapshot — matching the
   2026-08-22 verdict's own suggested fix ("a live pass staged through genuine incremental host
   movement... patient enough to let real mirroring keep the client's copy caught up
   throughout").
3. On the CLIENT, before engaging, run `pwsh tools\partystate.ps1 -ProcId <clientPid>` a few
   times, a couple of seconds apart, while the host is still walking. Confirm Trask's reported
   position is CHANGING between samples (not frozen) — this is the direct staleness check. If it
   is frozen, `disconnect`/`reconnect` the client (per the 2026-08-22 workaround) before
   continuing rather than staging combat on a known-stale mirror.
4. Once the host's walk has closed to point-blank range on the Vulkar and Trask's client-side
   mirror is visibly tracking (not frozen), have the CLIENT'S player attack the same Vulkar
   (`local_combat`'s own exclusion should arm — confirm via the existing
   `party drive: companion 0x... released to fight locally` log line, which only fires once the
   triple-proximity gate — player, companion, and the excluded enemy all mutually close — is
   satisfied).

## What to capture, and what each line proves

Tail both instances' plugin logs (`PluginHost.Log` output — wherever `demo.ps1`/the agent
launcher redirects it, or `tools\blockpoke.ps1 -ProcId <pid> console` if reading it live through
the console channel is more convenient) from the moment the client engages the Vulkar.

### 1. The release edge (already proven, confirm it still fires)

```
party drive: companion 0x<id> released to fight locally — within range of both you and the
enemy you are fighting (head=0x.. moveState=.. soloMode=.. t=..s slot=..)
```

The parenthetical is new this pass (`PartyPresenceController.ReleaseDiagSnapshot`). Record the
first sample's `soloMode` value — expect `1` on the client (it is held for the whole session);
if it reads `0`, something upstream already changed and the whole solo-mode hypothesis needs
re-examining before going further.

### 2. The release-diagnostic trickle (new this pass) — THE decisive line for hypothesis 2

```
party drive: companion 0x<id> still released — head=0x.. moveState=.. soloMode=.. t=..s slot=..
```

Logged every ~3 s while the companion stays released, capped at 60 lines. Collect every line for
the duration of the fight (or as many as land before the 60-line cap) and answer:

- **Does `head` ever read anything other than `0x-1` (`0xFFFFFFFF`/empty)?** A nonzero, non
  "-1" value means the companion's own AI queued SOMETHING — attack, move, or otherwise. Cross
  reference the value against `tools/kotor1_0_3.db`'s own RunActions jump table (the same lookup
  `Odyssey.cs`'s own `IsMoveAction` comment already did for the follow-AI investigation) to
  classify it: an attack-shaped action id here is the single strongest piece of evidence this
  whole investigation has been missing.
- **Does `moveState` ever change from whatever it read at release** (expect 1 "idle" or similar
  at the moment of release, given the companion was mid-mirror a frame earlier)? Movement toward
  the enemy is a secondary, weaker signal than an attack action id, but corroborating.
- **Does `soloMode` ever read anything other than `1` for the client** during the whole window?
  It should not, per how `KotorGameBridge.TryWritePartyMembers` is currently written — if it
  ever reads `0` unexpectedly, that is itself worth flagging (a different bug, an unexpected
  release of the flag).

**If `head` stays `0x-1` (empty) for the WHOLE release window, every sample, while `nearEnemy`
was true throughout (corroborate against the position samples from partystate.ps1 taken in
parallel) — that is first-hand, live-measured proof the companion's AI never even tries to
attack while solo mode holds, independent of proximity or staleness.** This confirms hypothesis
2 (`party-reconciliation.md`'s 2026-08-23 entry) and points the next round at
`KotorGameBridge.TryWritePartyMembers`/`ReleasePartyFreeze`, gated on a new "is any companion
currently released" shared-block signal `PartyPresenceController` would need to publish.

**If `head` DOES show attack-shaped activity but no damage ever gets credited (see §3-4 below)**,
the AI is not the blocker — the credit pipeline itself has a real bug worth chasing directly
(re-check `PhantomHitHook.IsOursToCredit`'s corpse/vtable/attacker-id gates against the actual
runtime ids involved, and `PeerDamageController.RefreshAttackers`'s `ResolveServerObject`
re-validation, with the concrete ids from this run in hand).

### 3. The client's own damage-dealt ledger picking up the companion's hits

```
combat ground-truth: local PC took N damage, attacker=0x.. (...)
```
is the wrong line to watch here (that is damage LANDING on the local PC). What to watch instead
is silent unless something is wrong or unresolved — `PeerDamageController`'s "combat: a peer owes
N damage to 0x.., which does not resolve..." lines fire ONLY on the OWNER (host) side and only
for a failure. On the CLIENT, confirm indirectly: `tools\blockpoke.ps1 -ProcId <clientPid> state`
(or an equivalent block dump) should show `DamageDealtCount > 0` and a nonzero cumulative total
against the Vulkar's object id once the companion has landed at least one swing — if you can read
the block layout, this is the most direct confirmation that `PhantomHitHook.OnDamage`'s
`IsOursToCredit` branch actually fired for the companion's attacker id, not just the PC's.

### 4. The host applying it as real, authoritative damage

```
combat: applied N of a peer's damage to 0x<enemyId> (owed N)
```

On the **HOST**. This is `PeerDamageController.ApplyOwed` on the owning side, spending the
peer's reported total through `Odyssey.ApplyDamageEffect` — the actual authoritative HP change.
This is the line that proves "credited," not merely "the client's screen showed an animation."

### 5. HP consistency both sides

After a few of line-4's applies have landed (or the Vulkar dies), compare the Vulkar's HP (or
its death) on both instances:

- **HOST**: read directly (console `getlocalvar`-style inspection is unnecessary; the plugin's
  own enemy census / `tools\blockpoke.ps1` state dump, or simply observing the on-screen health
  bar/death, is sufficient — the host is authoritative by construction).
- **CLIENT**: the mirrored enemy's HP as displayed should track the host's (mirrored, per
  `EnemyPresenceController`'s ordinary drive) EXCEPT for the brief `local_combat`-excluded
  window, during which the client's own screen shows the enemy fighting for real. A kill should
  produce a corpse, loot and an `OnDeath` script run identical to the host's own experience
  (`PeerDamageController`'s own class doc: "an enemy whose accumulated damage exceeds its health
  simply dies here").

If the Vulkar dies from the COMBINED damage of the client's PC AND the released companion (i.e.,
fewer swings from the PC alone were needed than a solo `local_combat` fight takes — compare
against `shared-state-audit.md` round 55's own baseline timing for a PC-only kill of the same
enemy pool if available, or simply note the elapsed time and swing count), that is strong
corroborating evidence the companion's damage is landing and being credited, on top of the direct
line-4 log evidence.

### 6. `verify.ps1`

`tools\verify.ps1 -Host1 <hostPid> -Client2 <clientPid>` after the fight — specifically the
existing checks "a client hit costs the enemy real hit points" and "the peer really loses those
hit points" (both pre-existing, general `local_combat`/damage-ledger checks, not
companion-specific, but a clean pass on them alongside a clean companion-specific observation
above rules out a companion-swing test having desynced the channel for the PC's own damage too).

## Decision tree

| Observation | Conclusion | Next step |
|---|---|---|
| `head` never leaves `0x-1` for the whole release window, position samples confirm the companion was genuinely in range throughout | Solo mode (or something else party-wide) blocks a released companion's AI from ever acting | Design and live-test a scoped solo-mode release (`KotorGameBridge`, gated on a new "companion released" shared-block signal), per `party-reconciliation.md`'s 2026-08-23 entry. Do not ship it unverified. |
| `head` shows attack-shaped activity, but §3/§4 show no damage ever credited | The AI fires but the credit pipeline drops it | Re-trace `PhantomHitHook.IsOursToCredit`/`PeerDamageController.RefreshAttackers` against the SPECIFIC ids this run used; check whether `MaterialisedEnemies.ToOwner` is wrongly mapping (or failing to map) the Vulkar's id if it turned out to need materialisation this run |
| `head` shows attack-shaped activity, §4 shows real applies landing, HP/kill consistent both sides | Roadmap item 4 is fully proven — release/reclaim (already known clean) AND the swing landing as credited damage | Flip `companion_local_combat` default to `1` per this project's own "flip on if clean" bar, with this document's own log lines as the evidence trail |
| Position samples show the companion's mirror still frozen despite real host walking | The staleness bug is not (only) a raw-poke artifact — it can occur under real movement too | Chase `CoopAgent.PumpParty` → `bridge.ReadPartyMembers()` → `CoopHost.PublishParty`/`BroadcastParty` directly, per the 2026-08-22 entry's own pointer to where the answer likely lives |

## Teardown

`pwsh tools\demo.ps1 -Stop`. Confirm `Get-Process swkotor,KotorCoop.Agent` returns nothing
afterward. Revert any coop.ini overrides this pass made (`companion_local_combat`,
`companion_local_combat_range`, and `party_fast_pump` if it was tried as a separate leg) back to
`profiles/coop.ini.example` defaults on both installs unless the pass's own conclusion is to ship
a changed default.
