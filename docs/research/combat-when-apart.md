# Combat when the players are NOT together (2026-08-07)

Everything in the combat channel was built and measured with both characters in the same
corridor. This is the review of what happens when they are not — one player mid-fight, the
other across the module or in a different one — plus the two defects that review found and
what was done about them.

Read `docs/research/combat-participation.md` first for the decoy/damage-forwarding design and
`docs/research/enemy-presence-sync.md` for the drive.

---

## 0. The short version

**It works, and the reason is the phantom.** The distant player is not simulated remotely in
any special way: their *body is really there* in the host's game, in the player faction, and
the host's enemies fight it like any other target. That single fact is what makes a fight the
client is having exist inside the host's authoritative simulation at all.

Three things were wrong, all of them invisible while the players stand together, and all now
fixed. The third — a crash — was found by testing the second:

0. **The follow fired off a placeholder module name and killed the client.** `PumpTravel`
   rejected the bridge's `areaXX` fallback for the *target* module but not for the *local*
   one, and that fallback appears for exactly one reason: our own transition is in progress.
   So mid-load we compared a placeholder against the host's real module, concluded we were
   separated, and issued a second transition into the middle of the first. Measured twice —
   travels 1.1 s apart, then the client dead 0.19 s later in
   `CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D`, which is
   `mov ebp,[eax+0x48]` on the result of `CClientExoApp::GetModule()` with **no null check**,
   while the previous world's creature-add messages were still being serviced. With the guard
   the same test produces travels **10.3 s apart and no crash**. A post-arrival settle window
   (`AgentSettings.ModuleSettleMs`, 5 s) backs it up.

   This is ordinary play, not a contrived case: a client that walks through its own door
   arrives somewhere the host is not and is brought straight back.


1. **The census was measured from the publisher only.** It is capped at 32 and sheds by
   distance, so the creatures around a distant partner sorted last and fell off the list —
   at exactly the moment that partner started to need them. Now the distance key is to the
   NEAREST player.
2. **The enemy snapshot carried no module.** Object ids are per module load, so a snapshot
   taken in `tar_m02aa` applied in `end_m01aa` writes the host's healths and positions onto
   whatever local creatures happen to share those numbers. The report direction had always
   carried its module; the snapshot direction now does too (protocol 14).

---

## 1. Who simulates what

| | Host | Client |
| --- | --- | --- |
| Enemy AI, health, death | **authoritative** — its engine runs the fight | mirrored; copies driven to the host's positions, stances and swings |
| Local encounters | fire normally | **disabled** (`DisableLocalEncounters`) — a spawn would shift the object-id counter the whole channel keys on |
| Damage the player deals | lands directly | reported back as a cumulative SUM of damage dealt (`EnemyDamage`, module-scoped, 2 s freshness); the host spends each increase as real engine damage on its own copy — §6b |
| Damage the player takes | normal | dealt to their PHANTOM in the host's game, forwarded, and applied through the client's own damage pipeline |

So a client never runs a private fight. Its screen shows the host's fight, frame by frame.

## 1b. MEASURED: the engine really does start the fight (2026-08-07)

This was the assumption everything else rested on, and until now it had never been tested —
`tools\verify.ps1` stages fights with `ActionAttack`, which bypasses perception entirely.

The test: put the client ALONE beside two Sith Soldiers at `(30.5, 139)` with the host's PC
at `(20, 22)` — **~120 units away, most of the length of the Endar Spire** — and watch the
one signal that says the host's engine ran the fight, namely damage landing on the client's
phantom, which is the client's only body inside the authoritative simulation.

```
baseline: phantom has taken 0 across 0 hit(s)
  taken 4    hits 2   lastAttacker 0xE
  taken 10   hits 5   lastAttacker 0x10
  taken 18   hits 9   lastAttacker 0x10
  taken 22   hits 11  lastAttacker 0x10
```

Both soldiers **walked to the phantom** — from `(29.74, 144.46)` and `(31.86, 141.99)` to
`(30.25, 140.78)` and `(31.24, 140.64)`, either side of it — and fought it, with both games
agreeing on their positions to two decimals. The forwarded damage reached the client's real
character (`incoming: owed 46 applied 46`), which floored at 1 hit point exactly as designed.

So there is no distance culling to work around: the area's AI priority is raised by
`CSWSArea::IncrementPlayersInArea` — **presence in the AREA, not proximity to a player** — and
a phantom is an ordinary creature to everything downstream of that.

## 1c. ⚠ What DID stall the lone fight: a tutorial box

The same test exposed a blocker that has nothing to do with authority or distance. The client
sighted an enemy for the first time and got:

> The game pauses when you sight an enemy, and the selection reticle will show up in red...

That box **sat on screen for six minutes** while two soldiers beat on the character. It
sheathed its weapon, every queued action was ignored, and the fight stopped — the auto-pause
option bits are already cleared by the plugin, so the world clock kept running, but the modal
panel owns the action queue and it needs a CLICK that a player being hit is not free to give.

Fixed with the bits beside it: `CClientOptions + 0x14` bit `0x02` gates every tutorial window.
Two independent confirmations — `SetTutorialWindowsEnabled 0x005472F0` toggles exactly that
bit, and `CGuiInGame::TutorialReasonWillShowWindow 0x0062F420` opens with
`test byte ptr [eax+0x14], 2` and returns "no window" when it is clear, so clearing it
suppresses every tutorial reason rather than this one message. Cleared alongside the six
auto-pause options in `Odyssey.SuppressAutoPause`.

Verified by screenshot: same room, same two soldiers, no box, weapon drawn, mid-swing.

## 2. Why a distant client's fight is not empty

The client walks ahead into a room the host has not reached. In the HOST's game:

* the client's phantom is standing in that room (it tracks its player — measured at a mean
  gap of 0.20 units while walking, `Handoff.md` §0i);
* the phantom is a real `CSWSCreature` in the **player faction**, not a plot-flagged prop
  (`phantom 0x130: joined the player faction; local enemies can fight it`);
* so the host's copies of those enemies pick it by the engine's ordinary nearest-hostile
  rule, walk to it and swing at it.

That fight is the authoritative one. Its results — positions, stances, swings, health, death —
are published and the client's copies are driven to match, so the client sees the fight it is
actually in. Damage the enemies land on the phantom is forwarded to the client's real
character, and damage the client deals is min-merged back on the host.

**The consequence worth internalising: proximity to the HOST is irrelevant. What matters is
that the phantom is where the player is.** The host can be asleep in the previous room.

## 3. The census, and why the cap is the interesting part

`KotorGameBridge.ReadEnemies` collects every creature that is not the player, a party member
or a phantom, then keeps at most **32** (`BlockEnemyCapacity`, the shared block's slot count).
What it sheds is decided by a sort: creatures the engine has mid-combat-round first, then by
distance.

Before this pass that distance was to the **local player**. With two players apart that is a
census with one eye: the creatures around the partner are the farthest things in the module
from the publisher, so they sort last and are the first dropped. Dropped means not published,
which means the partner's copies stop being driven and stop being health-mirrored — silently,
and only in a module with more than 32 creatures, and only when the players separate. A
nastier failure mode is hard to design.

The `Fighting` leg of the sort rescues an enemy **already** in a combat round, which covers
the middle of a fight. It does not cover the seconds before one — a partner walking into a
room — which is when the copies need to be in the right place.

Now: distance to the nearest of {local player, every peer in this module}. Peers arrive
already module-filtered (`visible` in `CoopAgent.Pump`), which is what makes the positions
comparable at all; an empty list behaves exactly like solo.

## 4. The module gate

`EnemySnapshotPacket` now carries the module its ids were read in, and a client returns an
empty list from `GetEnemies(module)` unless it matches. An unknown module on **either** side
(a profile that cannot read the field reports `""`) is treated as "no opinion" and lets the
channel through — treating unknown as "somewhere else" would disable the whole channel and
look exactly like a broken network layer, which is the same trap the peer filter documents.

The window this closes is real rather than theoretical: travel-follow is not instantaneous
(`FollowRetryMs` is 15 s and a module load takes seconds), and a client that joins
mid-session starts in whatever module its own save was in.

⚠ **An empty list is WRITTEN, not skipped.** Skipping on empty was right when empty only meant
"the host has not sent one yet"; with the gate it also means "these ids do not belong here",
and leaving the previous snapshot in the shared block would let the plugin keep driving the
old module's ids onto the new module's creatures — the exact thing the gate exists to stop.
Writing a count of zero clears them.

**Verified live**, one run, both directions:

```
together                     client enemies 25   presence 25
client travels to end_m01ab  client enemies  0   presence  0     <- gate held
follow brings it back        client enemies 25   presence 25     <- and reopened
```

## 5. What is still true, and is not a bug

* **A remote player can be worn down but not finished off.** KOTOR floors a party member at
  1 hit point and downs them through a separate path, so forwarded damage stops at 1. Being
  downed still works normally from damage in your own game.
* **Aggro is proximity only.** No threat shaping: which player an enemy picks is the engine's
  nearest-hostile rule and nothing else.
* **A conversation pauses enemy publication.** The dialogue system wipes every hostile's
  action queue each tick in the talking instance, so publishing during one would send a
  pacified fight. The last snapshot is held instead. Under the one-conversation rule the other
  player is spectating (and so is not fighting either), which is what makes the hold safe —
  but it does mean a conversation and a fight cannot happen at the same time in one session,
  by design.
## 6a. FIXED, END TO END: a lone player can now win a fight (2026-08-07)

Client alone against a Sith Soldier, host's PC 120 units away, attacking every 8 seconds:

```
start     host hp 15   client hp 15
round 1   host hp  6   client hp  2
round 2   host hp  1   client hp  1
round 4   host hp gone client hp gone
```

and on the owner:

```
combat: killed 0xE on the owner's authority
```

Before this the same test stalled at **11 hp for a whole ninety-second fight**. Three things
were in the way, and each one hid the next:

1. **The mirror raised the peer's copy back up.** `CombatController` wrote the owner's health
   whichever way it differed, eight times a second, so a hit landed here and was undone
   ~125 ms later — long before the once-a-second census could report it. It is now
   **lower-only**: the owner may always drag a mirrored enemy down (its damage is
   authoritative) and never back up, because the only reason our copy is lower is that we hurt
   it, which is exactly what the owner's min-merge wants to hear. Healing on the owner's side
   is the one thing that no longer propagates; that is a fair trade for damage that does.
2. **The report was a sample, not a high-water mark.** `LatchLowestSeen` in the agent now
   remembers the lowest health (and any death) seen this module, so a dip cannot be missed
   even if it is overwritten between censuses. The owner already merged with `min()`; this
   just makes "as hurt as ANY instance says" mean "…as any instance has EVER said".
3. **Min1HP meant the peer's copy could never die, so it could never report a death.** The pin
   exists so the owner delivers the killing blow — but when the players are apart the owner is
   not fighting and never will. It is now released once the owner's own value reaches 1: an
   enemy the owner agrees is at the floor has no health left to protect, so whoever is
   actually fighting it lands the last blow, and the owner runs the real death.
4. **A creature that dies leaves the census entirely**, so there is no row left to carry the
   flag and the owner would simply stop hearing about it. A vanished creature last seen AT THE
   FLOOR is now reported dead — safe precisely because only the owner's own merge can put
   something at 1.

22/22 on `toolserify.ps1` afterwards, 292 unit tests.

## 6b. ⚠ THE ORIGINAL DIAGNOSIS: a peer's damage was SAMPLED, so most of it was erased

Measured 2026-08-07, client alone against soldier `0xE` with the host 120 units away, the
client ordered to attack once every 8 seconds for 90 seconds:

```
start     host hp 15   client hp 15
round 1   host hp 11   client hp 11     <- one merge landed
round 2   host hp 11   client hp  5     <- the client hit it again...
round 3   host hp 11   client hp 11     <- ...and the mirror put it straight back
rounds 4-9  host hp 11   client hp 11   <- stuck, for the rest of the fight
```

**The enemy went from 15 to 11 and then stopped, permanently.** Not at the floor of 1 — at 11.
So §6's kill floor is the last inch of a road the damage mostly never travels.

The cause is that both halves of the merge work on ABSOLUTE HEALTH, sampled about once a
second each:

* the owner writes its value onto the peer's copy (`TryWriteEnemies` → `CombatController`);
* the peer's census reads whatever its copy currently says (`ReadEnemies`);
* the owner takes the minimum (`TryApplyEnemyFloors`).

A hit lands on the peer's copy and then races the next mirror write. Win the race and the
damage is reported and merged; lose it and the copy is restored to the owner's value and that
hit never existed. Round 2 above is exactly that: `5` was real, and it was gone before the
census saw it. The absolute-health design is right for REPLICATING the owner's truth — a
damage number replayed on the far side gets the wrong resistances — but it is wrong for
CARRYING a peer's contribution back, because it makes the contribution a sample instead of a
sum.

**The fix is to make the peer report damage it DEALT, not health it OBSERVED.** The plugin
already has the machinery: `PhantomHitHook` accumulates a running total of damage landing on a
phantom, and the same shape works here — watch damage the local player applies to an enemy,
keep a cumulative total per object id, and send that. The owner then subtracts each peer's
total from its own value, which is order-independent, survives a lost packet, and cannot be
erased by a mirror write because it is never read back out of the game. That also makes §6
mostly redundant: an enemy whose accumulated damage exceeds its health dies through the
owner's own death path with no "is it dead" report to trust.

✅ **Fixed twice.** §6a made the health channel work — mirror lower-only, latch the lowest
reading, lift the pin at the floor, report a vanished-at-the-floor creature as dead — and
that is what was live-verified. It was then **superseded the same day** by the
accumulate-damage-dealt design sketched above, which is now what ships. Two reasons, and
neither is that §6a was broken:

* four mechanisms were holding one leaky channel shut, and the channel still could not say
  WHO dealt what;
* the lower-only rule cost owner-side HEALING outright.

The peer now sends a cumulative sum of the damage it has dealt per enemy; the owner spends
each increase as real engine damage on its own copy, so the kill falls out of the arithmetic
with the engine's own corpse, loot and OnDeath, and the mirror goes back to propagating
healing (as a delta, not as an overwrite). **Retired with it: the health report packet, the
min-merge, the lowest-seen latch, the kill-request channel, and lifting the pin at the
floor.** Full write-up, including the disassembly proving that re-running the receiver's
resistance pipeline is not double-counting: `docs/research/peer-damage-ledger.md`.
⚠ Unlike §6a, the replacement has NOT been live-tested.

## 6. ⚠ OPEN: a player fighting alone cannot land the killing blow

Found by the same test, not yet fixed, and it needs a decision rather than a patch.

`KotorGameBridge.TryApplyEnemyFloors` is how a client's damage becomes real:

```csharp
if (report.Dead || !_enemyAddresses.TryGetValue(report.ObjectId, out nint address)) continue;
...
int floor = Math.Max(1, (int)report.CurrentHealth);
```

Two rules, and together they mean **a client can wear an enemy down to 1 hit point and never
finish it**: the floor never goes below 1, and a report that says the enemy is DEAD is
discarded outright. If the host is elsewhere, nothing else is going to hit that enemy — so the
room never clears and the fight has no end. Measured incidentally in the same session: soldier
`0xE` sat at exactly 1 hp on both instances.

Both rules were deliberate and both are defensible on their own. Death has to run through the
engine's own path or there is no loot, no XP, no scripts and no corpse — and a raw poke to 0
produces a body at zero hit points that the engine does not consider dead, which is the same
zombie class of bug as the pause and dialogue ones. Discarding `Dead` stops one peer's bad
read from deleting things.

**The shape of the fix** — the engine call already exists and is already used for the other
direction. `Odyssey.KillCreature` is `ApplyDamageEffect(id, 9999)`, a real `EffectDamage`
through the VM, which is exactly what `CombatController` runs when a client executes a death
the host decided ("killed 0x14 on the owner's authority"). So the host needs to run that same
call when a client reports a kill. What it needs that does not exist yet is an agent→plugin
channel on the HOST side, since `TryWriteEnemies` is client-only today.

Worth gating it tightly: honour a client's `Dead` only for a creature the host has ALREADY
floored to 1 — which the floor rule guarantees it will have — so a bad read can shave health
but can never delete anything the reporter had not genuinely fought down.

⚠ This changes who may kill, which is the centre of the combat authority model, so it is
written down rather than rushed.

* **Encounters only fire for the host.** A client cannot trigger a spawn; it sees the host's.
  With the players apart this means an encounter trigger the client walks over does nothing
  until the host walks over it too. NOT FIXED, and not cheaply fixable: client-side spawning
  would shift the object-id counter, which is the identity the entire channel rests on.

  **2026-09-02 note (append-only — the line above is left as written, not corrected in
  place):** this gap was closed by [client-encounters.md](client-encounters.md), which does
  not shift the object-id counter at all — instead of spawning on the client, the HOST calls
  `CSWSEncounter::SpawnIfAppropriate` itself, on its own engine, using the client's PHANTOM
  (already mirrored on the host) as the object that "walked in" — so every creature the
  encounter spawns is still created on the host's own counter, and the client just sees the
  ordinary enemy-mirroring channel bring them into view. Shipped as `client_encounters`, **ON
  by default**. See that document for the full design, the wire shape, and its own
  verification status.
