# Client-cast non-damage effects on enemies (2026-09-19)

Closes the gap named in `docs/review-seamless-2026-09-18.md` §2, "Client-cast non-damage effects
on enemies are cosmetic". **Status: built, unit-tested, behind `enemy_effect_sync` (default OFF),
NEEDS-LIVE.** Nothing here has run in a live game. Protocol 58 (packets 84/85), shared block 70
(`0x4C60..0x4C80`).

Tags used below: **VERIFIED** = read from this repository's code or a listed research doc, or
proven by a unit test in `tests/KotorCoop.Tests/EnemyEffectRelayTests.cs`. **INFERRED** = follows
from those but no run has confirmed it. **NEEDS-LIVE** = a rig check, recipe in §7.

## 1. The gap

Enemies are host-authoritative: the host's engine owns the real body, the AI and the health, and
every client renders a mirrored local copy that the enemy census moves and hurts. A client's
damage already reaches the host, because the hook on `OnApplyDamage` credits it to a per-enemy
cumulative ledger (`peer-damage-ledger.md` §3). A client's Stasis, Stun, Fear (Horror),
Whirlwind, Force Push, Slow, Paralysis, Sleep, or a grenade rider (ion, cryoban, adhesive) has no
such path. `PhantomEffectHook` only captures an effect whose target is a **peer's phantom**
(`PhantomHitHook.IsWatchedPhantom`), so an effect on an enemy body lands on the caster's local
mirror, animates there, and the host's real enemy keeps fighting.

## 2. Where the effect is observable — VERIFIED (existing hook), INFERRED (creator)

The same choke point the phantom capture already uses: `CSWSEffectListHandler::OnEffectApplied`
(`0x004D8320`, `effect-sync.md` §1), which every effect the game applies to anything passes through
before its per-type handler runs. Layout at entry (`__thiscall`, stdcall-style stack):

| arg | meaning |
| --- | --- |
| `[esp+4]` | `CGameObject* target` (a `CSWSObject`; object id at `+0x04`) |
| `[esp+8]` | `CGameEffect* effect` (type ushort at `+0x08`, duration float seconds at `+0x0C`, creator id at `+0x18`, int count at `+0x30`, int list ptr at `+0x34`) |
| `[esp+0xC]` | `int gameLoading` |

No new detour is needed. `PhantomEffectHook.OnEffect` already reads `(target, effect)`; when the
target is **not** a watched phantom it now hands the pair to `EnemyEffectCapture.Consider`, and
only when `enemy_effect_sync` is on and `PhantomHitHook.HasAttackers` (see below). The
classification half (engine type + integer-list sub-code -> `PeerEffectKind`, duration seconds ->
milliseconds, magnitude) was extracted unchanged into `PhantomEffectHook.TryClassify` so the
phantom path and the enemy path cannot disagree about what an effect is.

### 2.1 "Target is an enemy body WE mirror" versus "a locally-owned creature"

There is no per-creature "owner" flag in the engine; ownership is a property of which instance
runs the fight. The existing damage ledger already encodes the answer and this relay reuses it
verbatim (`PhantomHitHook.IsOursOnForeignCreature`, a thin public wrapper over the private
`IsOursToCredit`):

1. **The attacker set is non-empty.** `PeerDamageController.RefreshAttackers` fills it (PC plus the
   party roster) only where `block.DamageDealtEnabled` is on, i.e. on a mirroring client, and
   empties it on the enemies' owner and while spectating a peer's solo segment. Empty set means
   the capture is a single comparison and stops. This is how the host opts out: its own casts
   already ran against the real body.
2. **Creator is ours.** `effect+0x18` (the same `EffectCreator` the damage path reads) is in the
   attacker set. Effects an enemy applies to itself or that other creatures apply are not ours.
   INFERRED: the creator is stamped on a non-damage effect by the same `ApplyEffectToObject` step
   that stamps it on damage; both reach `OnEffectApplied` through that step. NEEDS-LIVE, §7 step 3.
3. **Target is a server creature** (vtable `0x007470C8`), not a door or placeable.
4. **Target is alive** (hit points at `+0xDC` as a signed int16).
5. **Target is not one of our own bodies.** A creator in the set and a target in the set is damage
   between our own bodies; the incoming-phantom-effect path lands on the player through the same
   dispatcher, and counting it would loop it back out.
6. **Not the `local_combat` bypass enemy** (`EnemyPresenceController.LocalCombatBypassId`): for
   that one enemy the client's copy is deliberately the authoritative fight, exactly as the damage
   path excludes it.

Materialised stand-ins versus GIT-identical twins: a stand-in's local id differs from the host's,
so the id is translated through `MaterialisedEnemies.ToOwner(targetId)` (non-zero only for a
stand-in); otherwise the local id already **is** the owner's id, because both instances load the
same save and assign GIT-order ids (`enemy-materialisation.md`). The wire carries the **host's**
id space, like `EnemyDamagePacket`.

## 3. Which kinds are safe to forward — VERIFIED

Only kinds whose **host apply path already exists and is live-verified**: the peer-effect channel's
`OdysseyEffects.ApplyEffect` constructors (`effect-sync.md` §7, §9, §11: 12 of 12 kinds live in both
directions, plus Fear and the general Stun §12). The forwardable set
(`EnemyEffectRules.IsForwardable`, mirrored by `EnemyEffectMailboxRule.IsForwardable` in the plugin;
`EnemyEffectRelayTests.TheForwardableSetIsTheTwelveCombatKindsAndOnlyThose` pins all three copies):

| kind | value | notes |
| --- | --- | --- |
| Stun (droid stun) | 1 | |
| Paralyze | 2 | Paralysis / Stasis-family |
| Knockdown | 3 | |
| Slow | 4 | magnitude = percent |
| Frightened (`EffectHorrified`) | 5 | Horror |
| Sleep | 6 | |
| Choke | 7 | |
| ForcePush | 8 | instant, duration ignored |
| Whirlwind | 9 | |
| Confused | 12 | |
| Fear (`EffectFrightened`) | 13 | |
| Stunned (general stun) | 14 | |

**Excluded on purpose**, refused at capture, at enqueue, on wire read and at apply (defence in
depth; a modified client cannot send them):

* **Heal (10), TemporaryHitpoints (11)**: buffs. A client's Cure on a mirrored enemy is not an
  effect to replay on the real one.
* **Disguise (16)**: a wearer-appearance effect with its own peer channel semantics.
* **Poison (15)**: the application is captured once, but its **ticks are ordinary type-38 damage**
  that the damage ledger already credits (`effect-sync.md` §10.3). Replaying the application as
  well would poison the real enemy twice. Its apply path is also still behind `poison_sync`, never
  run live.

Grenade riders (ion, cryoban, adhesive) are ordinary effects with the same engine types (a
droid-stun state, a slow, a stun/immobilise state), so they ride the kinds above. INFERRED: no
rider was dumped live; if one has an engine type off the allowlist it is simply not forwarded.

### 3.1 Duration and magnitude

Captured exactly as the phantom path does (`effect-sync.md` §2.3): duration is `effect+0x0C`
seconds times 1000; a zero or absurd value (over 3600 s) becomes a 6 s fallback; the wire clamps it
to `EnemyEffectRules.MaxDurationMs` = 60 s and substitutes the 6 s default for a zero. It is stored
in the shared block in 100 ms units, rounded **up**, so the host never applies less than was cast
(`TheMailboxPackingRoundTripsAndThePluginCopyAgreesWithTheAgentCopy`). Magnitude is
`integer_list[0]` for Slow (percent) and is otherwise 0; it is clamped to 16 bits. The host applies
with the same `durationMs`/`magnitude` the constructors already take. INFERRED: `effect+0x0C` is the
caller's requested duration for a non-damage effect on an enemy (NEEDS-LIVE §7 step 3 dumps it).

## 4. De-echo rules

* **The plugin's own apply must not be re-captured.** `EnemyEffectCapture.Suppress` is held across
  `OdysseyEffects.ApplyEffect` in `EnemyEffectController.ApplyIn`. Belt and braces: on the host the
  attacker set is empty, so the capture is already off there.
* **The host applying to the real enemy must not bounce back.** Nothing on the host captures a
  non-phantom target (rule 1 above), and the wire is client to host only; there is no relay to
  other clients. The host's own `PhantomEffectHook` path is untouched: an effect on the enemy is
  not an effect on a phantom.
* **Effects landing on the local player** (`applyeffect ... me`, a peer's forwarded effect via
  `PeerEffectController`) never satisfy rule 5, so the peer-effect channel and this one cannot feed
  each other.
* **Other clients see the effect through the ordinary census**, not a second packet: the host's
  enemy now stands still or is knocked back, which the census mirrors to every client. The caster
  already saw its local application.
* **Follow-on effects.** The engine may derive a second effect from the first (a Force Push that
  also knocks down). Both may be captured; the per-(enemy, kind) cooldown below keeps a duplicate of
  the same kind from being applied twice, and a distinct extra knockdown is harmless. INFERRED.

## 5. Flood protection

`EnemyEffectThrottleRule` (fixed arrays, allocation-free, time passed in), applied **twice**: at
capture on the client (a spam never leaves the machine) and at apply on the host (a modified client
cannot bypass it):

* the same (enemy, kind) is admitted at most once per 1 s;
* one enemy is admitted at most 8 times per 10 s window;
* a refused attempt records nothing, so a burst does not extend its own cooldown.

Around it: a capture queue of 16 (`EnemyEffectCapture`, oldest dropped), a reliable-sender table of
32 with a 15 s expiry (`EnemyEffectSender`; an effect is only worth replaying while it runs), a host
inbox of 64 with a 512-entry (origin, sequence) dedup, and an agent pending queue of 32 fed to the
plugin one entry at a time with a handshake.

## 6. Design as built

```
client                                                     host (enemy owner)
------                                                     ------------------
OnEffectApplied detour (PhantomEffectHook)
  target not a phantom, attackers set non-empty
  EnemyEffectCapture.Consider: allowlist, creator
  ours, live foreign creature, throttle,
  ToOwner id translation           -> queue (16)
EnemyEffectController.PublishOut
  block OUT mailbox 0x4C60 (one entry, paced by ack)
EnemyEffectPump (agent): read, ack (0x4C78),
  session.PublishEnemyEffect
EnemyEffectPacket (84, reliable, to host) ----------->   CoopHost.HandleEnemyEffect: ack (85), origin
      <---------------- EnemyEffectAck (85)                  known, dedup, bounded inbox
                                                          EnemyEffectPump: drop other-module, feed
                                                            IN mailbox 0x4C6C one at a time
                                                          EnemyEffectController.ApplyIn:
                                                            EnemyEffectApplyRule (kind, resolves,
                                                            creature, not our body, alive, throttle)
                                                            OdysseyEffects.ApplyEffect(enemyId, ...)
                                                            echo 0x4C7C
                                                          every client sees the result via the census
```

* **Packets**: `EnemyEffect = 84` (`OriginPeerId, OpSequence, Module, EnemyId, Kind, DurationMs,
  Magnitude`), `EnemyEffectAck = 85`. The reader rejects a kind outside `IsForwardable`, an empty
  enemy id and a truncated packet, and clamps duration and magnitude. The module string guards a
  retransmit that outlives a module change (object ids restart per world); the agent drops a report
  whose module is not the host's current one.
* **Host apply path**: the same `OdysseyEffects.ApplyEffect` that `PeerEffectController.ApplyIncoming`
  and the `applyeffect` staging verb call, aimed at the resolved enemy id. It needs the VM idle; a
  refusal for "no implementer" or "script owns the stack" is retried for 30 frames, anything else is
  final and logged (`enemy-effect: ...`, first 40).
* **Host refusals** (`EnemyEffectApplyRule`, tested): bad kind, id resolves to nothing, not a server
  creature, one of the host's own bodies (the player, a party-roster body, a peer's phantom: a client
  cannot stun the host's character by naming it), dead, throttled.
* **Shared block**: only `0x4C60..0x4C80`. Two 12-byte mailboxes (`enemy id`, `head = kind |
  serial << 8`, `args = duration/100ms | magnitude << 16`, head written last) plus an ack dword each
  way. Single entries with a handshake rather than rings because 32 bytes is the whole allowance; the
  plugin-side queue absorbs an area cast that lands on four enemies in one frame. `TotalSize` is
  unchanged (`0x4D00`); `_block->Version` 70; plugin signature byte `0x46`.
* **Both machines must set `enemy_effect_sync`.** Capture (client plugin) and apply (host plugin and
  agent) are independent per-machine switches, like `class_grant_sync`.

## 7. Default, and the NEEDS-LIVE recipe

**Default OFF.** Every piece of the capture side is new, and the apply side, although its
constructors are live-verified, has only ever been aimed at the local player and at phantoms. It has
never been aimed at an **enemy body** (a hostile creature with its own AI, which may re-plan when it
is stunned, or crash the effect list handler on a creature type the phantom does not exercise).
Per the brief, ON requires every piece of the apply path to already be live-verified; it is not.
The three readers agree on OFF: `PluginConfig.ReadBool("enemy_effect_sync", false)` (via
`EnemyEffectCapture.Enabled`), `CoopConfig.EnemyEffectSync` and its parser, and
`AgentComposition`'s `?? false`.

Rig recipe (two instances, `enemy_effect_sync=1` in **both** `coop.ini`, same module, both attached;
console verbs from `docs/CONSOLE.md`):

1. **Stage a hostile the host owns.** On the host: `spawncreature tar10_vulkpoo002 1` (a
   confirmed-good template, `docs/CONSOLE.md`). Note its id from `census ids`. On the client, wait
   for the census to mirror it (`census <id>` shows it; a stand-in appears under
   `MaterialisedEnemies`).
2. **Apply-side proof without any capture** (isolates the never-run part). On the **host**:
   `applyeffect 14 <hostEnemyId> 5000` (Stunned) and watch the enemy stop (`census <id>`, screen).
   Repeat for 1, 2, 4 (`applyeffect 4 <id> 5000 50`), 5, 8, 9, 13. Expected: `applyeffect: kind N
   applied to 0x... ` in the plugin log and a visible effect; **no crash**, and the host's enemy AI
   resumes when the duration ends. This alone decides whether ON is safe.
3. **Capture proof.** On the **client**, aim the same verb at the mirrored enemy's client-side id:
   `applyeffect 14 <clientEnemyId> 5000`. The staging call runs in a script context that names the
   local player, so it should stand in for a Force power, with the player as creator (INFERRED, the same assumption as §2.2). Expected in the
   client agent log: `enemy-effect: reporting kind 14 on enemy 0x<hostId> to the host (5000 ms ...)`;
   in the host agent log: `enemy-effect: handing a client's kind 14 ...`; in the host plugin log:
   `enemy-effect: applied kind 14 to enemy 0x<hostId> (5000ms, magnitude 0)`. If nothing is reported,
   the creator field is not stamped on the effect at `OnEffectApplied` (the INFERRED half of §2.2):
   dump `effect+0x18` from `EnemyEffectCapture.Consider` and adjust.
4. **The real thing.** With both players in a fight, the client casts Stasis / Force Push / Slow /
   Horror on a host-owned enemy. Expected: the same three log lines, and the **host's** enemy
   visibly stops or is thrown; the second client (if any) sees it move accordingly.
5. **De-echo.** In step 3, confirm exactly one `reporting` line per cast (not two), and no
   `reporting` line on the host at all. Cast the same effect twice inside a second: the second is
   throttled (no second line).
6. **Refusals.** From the client, `applyeffect 14 me` (the player) must produce no report. On the
   host, hand-write an IN mailbox naming its own player id (blockpoke) and expect `RefuseOurBody`.
7. If 2-6 pass with no crash across two module transitions, flip the three defaults to ON together
   (plugin `ReadBool` fallback, `CoopConfig` record and parser, `AgentComposition` fallback), update
   the `CONFIG.md` row and `status-engineering.md`, and remove the `limitations.md` caveat.

## 8. Not covered

* **Effects the host's enemies cast on the client's mirrored body** are out of scope: that is the
  existing phantom-effect channel's direction.
* **Effect expiry.** The host's constructors carry the duration; nothing forwards an early removal
  (a dispel). Symmetric with the phantom channel's stance (`effect-sync.md` §1).
* **Companions.** A companion the client controls is in the attacker set, so its casts qualify by
  rule 2; a client holds solo mode today, so in practice this is the PC.
* **Three or more players.** The report goes to the host only (the enemies' owner) and is not
  relayed to other clients; they see the consequence through the census.

### 7.1 Live result, apply side (2026-09-19)

Recipe step 2 run on the rig (host, `enemy_effect_sync=1`, module `tar_m02aa`, a hostile `g_sithtroop002` from `spawncreature`): `applyeffect` kinds 14 (Stunned), 1, 4 (Slow, magnitude 50) and 8 applied to the enemy body, each answered `applied kind N to 0xBD`, and the game stayed up. **The never-run half is now run: aiming the apply constructors at an enemy body does not crash.** Not confirmed: that the enemy visibly stops or that its AI resumes (no screen check was done). The capture half (step 3) is still NEEDS-LIVE, so the key stays OFF.

Two rig facts for whoever runs step 3: (1) a `spawncreature` template must exist in the CURRENT module's archive (`tar10_vulkpoo002` failed with failure 9 outside `tar_m10aa`; list a module's templates with `python tools/kotorfiles.py list modules/<module>_s.rim --type utc`, `g_sithtroop002` works in `tar_m02aa`); (2) a runtime-spawned creature has no counterpart on the client, so it is never mirrored (host census 15, client 14) and cannot be used to test the client-cast capture: use a creature that is in the module's own GIT and has a client stand-in.

### 7.2 Live result, capture and round trip (2026-09-19) - VERIFIED for Stunned

Step 3 run with `enemy_effect_sync=1` in both `coop.ini` (module `sta_m45ac`, enemy id 0x19 resolving on both instances; the verb aimed at the client's copy): client plugin `enemy-effect: saw kind 14 on 0x19 creator 0x7FFFFFFF ours=True attackers=True`; client agent `reporting kind 14 on enemy 0x19 to the host (5000 ms, magnitude 0)`; host agent `handing a client's kind 14 on enemy 0x19 to the plugin`; host plugin `enemy-effect: applied kind 14 to enemy 0x19 (5000ms, magnitude 0)`. Both games stayed up. **The INFERRED half of 2.2 is settled**: an effect applied through the script pipeline carries the local player (`0x7FFFFFFF`) as creator at the dispatcher, so the ownership gate passes. `EnemyEffectCapture` now logs its first 12 sightings (`enemy-effect: saw kind ...`) to keep that visible.

Still not shown: a real Force-power cast (the console verb goes through the same VM pipeline, so this is expected to hold), the other eleven kinds through the wire, that the host's enemy visibly stops, and the throttle. The key stays OFF until someone has seen a real cast stop an enemy.

