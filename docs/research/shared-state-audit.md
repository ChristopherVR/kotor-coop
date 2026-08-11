# Shared-state audit: what actually replicates, and what only looks like it does

Measured on the live pair (pids 17208 GOG / 46088 p2, Endar Spire, 2026-08-11) after a
playtest produced seven complaints at once. They are not seven bugs: one was a wrong
offset, and the rest are the same architectural gap wearing different clothes.

## 1. Max hit points: a field the engine never writes

`CSWSObject::GetMaxHitPoints` (0x004D01A0) is literally `mov ax,[ecx+0xE0]`, so +0xE0 is
right for doors, placeables and ordinary creatures. It reads **0 on both players**.

`CSWSCreature::GetMaxHitPoints` (0x004ED310) overrides it and branches on `stats+0x6C`
(`is_pc`). For a PC the engine never stores a total at all — it re-adds one from the
per-level records every time it is asked, in the loop at 0x004ED360..0x004ED3CA:

    max = Σ over levels ( levelStats[i]->hitPoints(+0x2B) + stats->conModifier(+0xEE) )

Field layout used, all verified live:

| what | where |
|---|---|
| `is_pc` | `CSWSCreatureStats + 0x6C` (int; 1 selects the accumulate path) |
| level list data ptr | `CSWSCreatureStats + 0x28` (`CExoArrayList<CSWLevelStats*>`) |
| level list count | `CSWSCreatureStats + 0x2C` (capacity follows at 0x30) |
| CON modifier | `CSWSCreatureStats + 0xEE` (byte, added once PER LEVEL) |
| hit points rolled | `CSWLevelStats + 0x2B` (byte) |

Measured: one level, rolled 10, CON modifier +2 → **12**, exactly the host's reading at
full health. Implemented as `KotorGameBridge.TryReadMaxHitPoints`, which reproduces the
branch (PC → accumulate, anything else → the stored field) and feeds both the player state
and the enemy snapshot.

⚠ The general lesson, again: a getter that is one `mov` is not proof the field is live.
Check whether a DERIVED class overrides it before trusting the base offset.

## 2. Combat state is reported, never shared — and on a client it cannot be

Measured: host creature `+0x4E0 = 1`, client `+0x4E0 = 0`, same area, same moment.

`PlayerState.FlagInCombat` is set by the bridge and read by the dashboard. **Nothing
anywhere applied a peer's combat flag.** Two players in the same room during the same
fight produced "IN COMBAT" on one readout and "exploring" on the other.

The deeper reason matters more than the missing plumbing: on a client the enemies are
MIRRORS — driven puppets, not creatures its own AI is fighting — so the client's engine
genuinely never enters combat. Forcing `+0x4E0` on the client would be a lie the engine
recomputes away, and combat state gates round timing and AI.

### The state cannot be replicated by writing it — measured

A full channel was built (`SharedBlock.OffsetSessionInCombat` at 0x1E50, block version 29,
`CoopAgent.PumpSharedCombat`, `CombatStateController`, and
`Odyssey.SetPlayerCombatState` calling `CSWSCreature::SetCombatState` at 0x004F2610). The
channel is right and stays. The last step does not work:

    trial 1 : reverted after 2 ms
    trial 2 : reverted after 0 ms
    trial 3 : reverted after 1 ms

A written in-combat flag on a real player with no attackers is gone in **0–2 ms**. The
field is not a latch the engine consults; it is continuously re-derived from whether the
creature is actually being fought. There is a second writer past the one in
`SetCombatState` — inside `CSWSCreature::ProcessPendingCombatActions` at 0x004F7C0E. No
re-assertion cadence a frame hook can manage competes with that.

`CombatStateController` is therefore **off by default** (`coop.ini combat_state_sync=1`
reproduces the measurement), rather than shipped as a feature that visibly does nothing.

⚠ Note on measuring this: a bare vtable scan for `CSWSCreature` (0x007470C8) plus an
`is_pc` check lands on a COMPANION — party members carry `is_pc` too. The first attempt at
this test wrote the flag onto Trask and concluded the channel was broken. The bridge
publishes the creature it actually chose at `block+0x258`; use that.

### The suppressor is the action wipe, not the attack bit

`MarkAsMirroredEnemy` clears the attack-permitted bit, but it has exactly ONE caller
(`MaterialisedEnemies`), so ordinary module enemies — the common case — keep theirs. What
actually stops them is `EnemyPresenceController` calling `Odyssey.ClaimCreature` on every
mirrored enemy on every pass, and `ClaimCreature` calls
`CSWSObject::ClearAllActions(0x004CCD80)`. That call does not distinguish a queued
MoveToPoint (which fights our placement, and is why the wipe exists) from a queued attack
(which is the entire fight). So the mirror's enemies have their swings deleted several times
a second, never land one, and the mirror's combat state — derived from being fought — stays
false through the whole battle.

Implemented as `coop.ini local_combat` (default 0):

* `Odyssey.ClaimCreature(nint, bool clearActions)` — the wipe becomes optional.
* `EnemyPresenceController.LocalCombat` — leaves the queue alone when set, so the local
  copies can finish a combat round. Position is still driven from the owner every pass.
* `IncomingDamageController` reads the SAME flag and stands down when it is set, absorbing
  the owner's forwarded total instead of applying it. The two must move together: with local
  enemies swinging here and the owner forwarding what its copies did to this player's
  phantom over there, every hit would otherwise land twice.

⚠ NOT YET VERIFIED, AND THAT IS WHY IT DEFAULTS OFF. The staging attempt — co-locate both
players, teleport a live enemy onto them by writing its position — produced no fight on
EITHER instance (`host entered combat: no`), so it says nothing about the change. Writing a
creature's position does not raise a perception event, and the AI never noticed the player.
A real verification needs a fight the engine itself starts.

⚠ The failure mode if it is switched on and the local enemies still do not engage is that
the client becomes INVULNERABLE: forwarded damage is absorbed and nothing local replaces it.
Verify engagement before trusting it in play.

### Adversarial review found this is not one playtest away

An independent review of the change (2026-08-11) found defects that are structural, not
tuning. Three were fixed immediately; the rest are open and are why the flag stays off.

FIXED:

1. **Materialised enemies could never swing.** `MarkAsMirroredEnemy` clears attack-permitted
   bit 0x04, and `ClaimCreature` ORs only 0x02 back — so the clear was permanent for the life
   of the body. Those bodies are exactly the host encounter spawns and scripted waves that
   `local_combat` exists to make dangerous: they would hold a target, queue attacks, deal
   nothing, and have the owner's report absorbed. **A host-triggered ambush would have left
   the client completely invulnerable.** The clear is now skipped under `local_combat`, and
   `EnemyPresenceController` re-asserts the bit every drive pass (a spawn-time decision does
   not survive the body's lifetime).
2. **No role gate.** `local_combat` is read per-install from `coop.ini` and never crosses the
   wire, so the absorb also ran on an instance mirroring no enemies. Now gated on
   `block.DamageDealtEnabled`, the same "am I mirroring" test PortraitController uses.
3. **Silent failure.** The absorb logged five times and then went quiet forever — total
   invulnerability with nothing in the log. Now a running total on a 10-second heartbeat.

STILL OPEN — do not default this on:

4. **Target divergence breaks the authority split.** The split assumes the local enemies
   attack the local PC. Nothing enforces it: a peer's phantom is faction PLAYER while that
   peer lives, and `GetNearestEnemy` ranks by hostility + LOS + distance, so the client's
   copy of an enemy can lock onto the HOST's phantom while the host's copy hits the client's
   phantom. Both sides then absorb and nobody loses a hit point. The lock is sticky —
   `CreateNewAttackActions` releases only on dead / unresolvable / reputation flip, and a
   phantom is min-1-hp and stays faction 0. Likely fix: hold peer phantoms at Neutral on a
   mirroring instance so the only player-faction targets are the local PC and companions.
5. **Blanket absorb, partial local reproduction.** Census cap and distance shedding, the
   strict type test that skips creatures reading `0x40005`, phantom-id collisions, mines and
   traps and scripted `EffectDamage`, and the conversation hold all produce forwarded damage
   with no local analogue. Absorption should be attributable — the attacker id is already on
   the channel.
6. **Mirrored companions become an immortal aggro sink** and can be permanently downed: they
   are faction PLAYER, not min-1-hp pinned, cannot fight back, and their health is only
   force-written when the host's value is positive — so a burst between passes lays one down
   with a full health bar, forever.
7. **The position fight returns.** `DriveBody` passes the suppress-dirty-bit argument for a
   measured reason; the local AI's own `SetPosition` does not, so the "flying" artifact can
   come back. Worse, `MotionSmoother` never reads the engine's position, so a queued
   `MoveToPoint` can never reduce its distance, never completes, and wedges the queue head —
   the transient condition the wipe existed for, made permanent. A targeted wipe (move
   actions only) is the likely answer rather than no wipe.

The honest test is not "did it feel like a fight" — it is a scripted comparison of hit points
lost on the mirroring instance against the owner's forwarded total over the same battle.

### The real fix

Give the client's engine a genuine reason to be in combat instead of asserting the
conclusion: stop clearing the attack-permitted bit in `Odyssey.MarkAsMirroredEnemy` for
enemies near the local player, so its own copies swing at it and its combat state rises for
the same reason the host's does. That is an ownership change, not a poke, and it needs the
damage reconciliation thought through first — a client whose mirrored enemies really attack
must not also apply the owner's damage report for the same swing, or every hit lands twice.

`AgentDashboard.DescribeSharedCombat` meanwhile combines the flags into a session-level view
and says which engines are engaged and which are only mirroring, so the asymmetry reads as
what it is rather than as a dropped packet.

## 3. A client's XP is actively erased

`ProgressionSnapshotPacket` is host→client only and absolute (CoopAgent.cs, `PumpProgression`):
the host publishes gold and XP; the client **writes the host's totals over its own** every
tick. A client's own kill grants XP locally and then has it stamped back down within a
tick. Both instances read 300 in the audit only because neither had gained any since load.

Needs to become a merge, not an overwrite — most likely "publish deltas and take the max",
the shape the journal channel already uses.

## 4. The root of most of the list: ownership is one-directional

"Clients are not triggering aspects", "Trask still walks around on the host while a client
is talking to him", death and dialogue not replicating from a client — one cause. The host
owns enemies, companions, doors and containers; `PartySnapshotPacket` says so outright
("every client freezes its own copies and mirrors these"). The client→host channels that
exist are narrow and specific: `EnemyDamagePacket`, `EncounterTriggerPacket`,
`ItemOpPacket`, `FlagOpPacket`, `JournalOpPacket`, `DialogueRequestPacket`.

There is no channel by which a client says *"hold this companion still, I am talking to
him"*. So the client's Trask freezes (it is mirroring), the client opens a conversation
with what is effectively a puppet, and the host's Trask carries on walking because the
host's AI was never told otherwise. Everything a client does to a host-owned object has
this shape.

⚠ Do not fix these one at a time. The next real step is an **interaction lock in the
owner's direction**: a client-claimed object that the owner's engine agrees to hold.
Click-to-join is the same missing primitive seen from the other end, which is why it has
resisted three passes of gate-hunting.

## 5. The black window until you click it — FIXED

A launched instance draws nothing until the player clicks into it. With two windows that
means clicking both, every launch.

`0x7A3A38` is the render-window-active latch: `Global::ActivateRenderWindow` (0x00401E00,
__stdcall, NO arguments — 0x00401E83 is a plain `ret`) sets it and early-outs if it is
already set; `Global::DeactivateRenderWindow` (0x00401D90) early-outs if it is already
clear. Measured on the live pair:

| moment | instance 1 | instance 2 |
|---|---|---|
| straight after launch | 0 | 0 |
| after focusing instance 1 | 1 | 0 |
| after focusing instance 2 | **1** | 1 |

Both come up unactivated, and — the useful part — instance 1 stayed at 1 after losing
focus. It is a latch, not a focus mirror, so activating once per launch is enough and
nothing turns it off again in play.

`FocusKeeper` now calls `Odyssey.EnsureRenderWindowActive()`, which invokes the engine's own
activation rather than poking the latch (its body also clears the unfocused pause reason via
`SetPauseState(2, 0)` — the same one FocusKeeper clears). Level-triggered, so the several
deactivate/reactivate cycles the load chain performs are absorbed. Verified: both instances
render fully with nothing ever clicked. `coop.ini activate_render_on_start=0` restores stock
behaviour.

### The spurious startup pause it exposed

With both windows finally drawing, the first launch came up with `PAUSED / PRESS THE PAUSE
BUTTON TO CONTINUE` on both. Not the pause adoption — the log said `space fallback ->
paused`. `PauseKeyController` starts with `_spaceWasDown = false`, so the very first poll is
a rising edge whenever space happens to read down, which it does during startup; the
instance paused itself and the peer mirrored it. The first observation now only records the
key state (`_spaceSeen`). Verified: the pause log is clean through a launch.

## 6. The pause button

`Odyssey.ReconcilePause` has a `union == 0` branch that force-unpauses both world timers
and clears the GUI banner whenever neither pause mask holds a bit. It runs every 20 frames,
session or no session. During live play at 14:48 it logged five repairs in three seconds.

If the engine's own HUD-button path moves the world timer and the GUI belief without
setting a mask bit, that branch is a second force-unpause — reinventing exactly the bug
`PauseSanityPatch` exists to delete, this time on our side of the line. That would present
as "clicking pause does nothing at all", on both instances.

CONFIRMED and FIXED. One press produced:

    .600  reconciler UNPAUSED (banner) — no mask held a bit (server 0x00, client 0x00)
    .744  space fallback -> paused

The engine's own pause paths move the banner and the world timers but set NO mask bit, so
the reconciler read it as stranded and tore it down within ~60 ms. Space only ever appeared
to work because `PauseKeyController` re-paused it 200 ms later; the HUD button has no such
fallback and did nothing at all. The reconciler now ADOPTS the banner instead — writing the
mask the engine forgot — which also makes the pause publishable, so a HUD-button pause now
reaches the other player. Verified: adopted in 27 ms, mirrored in 39 ms, no fallback.

### Releasing a shared pause — FIXED for the key, OPEN for the button

A release could only ever be made by the player who STARTED the pause. Reproduced: paused on
instance 1, released on instance 2, instance 1 stayed frozen. The cause is that a release is
not observable as the absence of `FlagPaused` — a mirroring player never had that flag set,
because mirrored pauses are excluded at the source. It needs its own signal:
`PlayerState.FlagPauseReleased` plus a serial each way through the block (version 30).
Verified: instance 2 released, instance 1 followed 92 ms later.

⚠ STILL BROKEN: **the HUD hourglass can pause but cannot UNPAUSE.** Measured — click it once
and the pause is adopted and mirrored (81 ms); click it again and *nothing happens at all*,
on either instance, with no reconciler line. The masks were read live straight afterwards and
both halves still held `0x01` on the instance that was clicked, so this is **not** a
propagation failure — the second click never unpauses even locally.

The likely cause, unconfirmed: an adopted pause has no entry in the session's `PausedBy` list
(we write the mask bit directly rather than going through the engine's pause registration),
so the button's unpause path finds nothing to remove and no-ops. `PauseKeyController`'s
200 ms fallback is what rescues the space key, and there is no equivalent for the mouse.

NEXT: find the HUD pause button's handler and either hook it (the click is the intent, and
we already own the pause) or give the mouse the same fallback the key has. Do NOT "fix" this
by re-asserting in the reconciler — that is the loop that made the banner unclearable before.

---

## §4 answered: the per-object interaction lock (2026-08-11, live-verified)

§4 recorded that ownership is one-directional and that nothing tells an object's owner when
somebody else is using it. That channel now exists.

**The reported symptom:** a client walks up to Trask and starts talking. The client sees a body
it drives from the host's position stream; the host's own AI, never told anything happened,
carries on walking the real Trask around. Both screens show a conversation with a companion
who is strolling out of it.

### Shape: a lease, claimed early

* **A lease, not a handshake.** The claim is re-sent every tick while it is held and expires
  on its own. There is no release message, so losing one cannot strand an NPC for the rest of
  the session — which is the failure that actually matters, because the case to design for is
  a client dying mid-conversation. Same discipline as the encounter-trigger channel.
* **Claimed the moment a player starts interacting**, which is earlier than a conversation
  exists. That is what makes a contested NPC fair, and it fixes a bias nothing had noticed:
  "first speaker wins" is only fair if both claims race on equal terms, and they never did —
  the host's dialogue claim reaches the arbiter **in-process at zero latency** while a
  client's crosses the network. Two players clicking the same NPC at the same instant
  resolved to the host every time, however early the client clicked. `DialogueArbiter` now
  takes a `lockHolder` and refuses a claim on an NPC someone else holds **even when the token
  is free**.
* **A claim with no partner tag is never refused.** The tag is empty for a real window at the
  start of every conversation (`gui+0x170/0x174` stay `OBJECT_INVALID` until the server
  resolves the participants), and refusing on an empty tag would refuse every conversation in
  the game for its first few hundred milliseconds.

### The hold itself

`Odyssey.HoldCreatureStill` clears the action queue **only when the head action is a move**.
A blanket `ClearAllActions` every pass is what stopped a mirrored enemy ever landing a swing —
the queue does not distinguish a walk from the whole fight, so the test has to.

The agent filters the local peer's own claim out before handing the list down, so the plugin
holds everything it is given without needing to know its own peer id. Sending it would be
actively wrong: the object *this* player is talking to is already frozen by the engine's own
dialogue system.

### Measured

Client starts the conversation, host owns the NPC:

```
  t   host#  client#  hostNode  clientNode  hLock  cLock   hostPartner  clientPartner
  0       0        1        -1          29      1      0                end_trask
  -> host clicks 'end_trask' (join)
  5       1        1        29          29      1      0   end_trask    end_trask
NODES: on the same entry for 9 of 9 paired samples.
```

`hLock 1` from t=0 — the host is holding Trask still at the client's request, before the host
is in the conversation at all.

`verify.ps1` carries it permanently: *an interaction lock reaches the other instance*.

### `force_dialogue_party`, and why it is OFF

With the lock in place, being *pulled into* a peer's conversation rather than clicking to join
is a few lines — it sends the same request a click sends and takes the same adopt path.
Verified live: with nobody clicking, the host joined **531 ms** after the client started and
landed on the same node (14/14 paired samples).

It ships **off**, for two reasons:

1. It is the behaviour that was already removed once. Players were yanked out of whatever they
   were doing from anywhere in the module; joining became a click because walking away while
   your partner talks to a shopkeeper is a legitimate thing to want.
2. **Unresolved:** a conversation that recruits or dismisses a companion calls
   `AddPartyMember` / `RemovePartyMember`, and those have **no channel in this mod**. A player
   driven into a recruitment conversation ends it holding a party roster the other instance
   does not share — and the roster is what the companion channel keys on. Ordinary
   conversation is fine; recruitment is not.
