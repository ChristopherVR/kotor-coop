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
| Damage the player deals | lands directly | reported back (`EnemyReport`, module-scoped, 2 s freshness); the host **min-merges** it into its own enemies |
| Damage the player takes | normal | dealt to their PHANTOM in the host's game, forwarded, and applied through the client's own damage pipeline |

So a client never runs a private fight. Its screen shows the host's fight, frame by frame.

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
* **Encounters only fire for the host.** A client cannot trigger a spawn; it sees the host's.
  With the players apart this means an encounter trigger the client walks over does nothing
  until the host walks over it too. NOT FIXED, and not cheaply fixable: client-side spawning
  would shift the object-id counter, which is the identity the entire channel rests on.
