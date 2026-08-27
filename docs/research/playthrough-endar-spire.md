# End-to-end co-op playthrough smoke: the Endar Spire opening (2026-08-27)

A scripted, dev-verb-driven walk through the game's opening as two players, run against the
**shipped defaults** — `profiles/coop.ini.example` copied verbatim into both game directories,
no flags flipped from what a fresh install ships with (both installed `coop.ini` files were
found heavily customised from an earlier rig session and were reset to the shipped example
before this run; see "Setup" below). The goal is not to re-prove any single feature (each has
its own dated research doc already) but to record, PASS/FAIL, what a real pair would actually
experience end to end, and to fix or document anything that breaks along the way.

Every claim here is tagged **VERIFIED** (read off a running instance's logs/memory this
session), **NEEDS-LIVE** (not reachable this session, reasoned only), or **INFERRED**.

## Setup

Mid-round, the coordinator flagged that `main` had moved 25 commits ahead
(container_lock_arbitration, WorldSnapshot paging, solo-segment spectate, party-select
suppression, store/internet plumbing, companion_gear_sync, the Wave B rig fixes) since this
worktree branched. `git merge main` was clean (no conflicts), rebuilt (`dotnet build
KotorCoop.slnx -warnaserror`: 0/0; `dotnet test`: 2289 tests, one UDP-timing flake
(`ContainerLockArbitrationTests`, confirmed non-deterministic by an isolated rerun that
passed cleanly) plus the already-documented `HeavyPacketLossStillConverges` exclusion), then
`build.ps1 -Install` to both game directories (DLL grew 1916 KB -> 1954 KB, confirming the
new build actually deployed) and both `coop.ini` files were refreshed from the post-merge
`profiles/coop.ini.example` (which now ships `container_lock_arbitration=1` and
`companion_xp_sync=1` as defaults — both already covered by "no flags flipped" since they
are exactly what the file ships). **Everything from this point on is tested against commit
`7c5d7df`** (`Merge branch 'main' into worktree-agent-a66c6d5ab3117f8c5`, on top of `main`
`e1b25c1`). Steps 1-3 above were run against the pre-merge build (`c9d08bb` plus the local
`TestDriveController` fix); none of the merged content touches chat/dialogue/doors, so they
were not re-run.

- Rig: `tools/demo.ps1`, GOG installs at `C:\GOG Games\Star Wars KOTOR (GOG)` (host, "Lanik")
  and `D:\Games\swkotor-gog-p2` (client, "Malak"), save slot 1 (`END_M01AA`).
- Both `coop.ini` files replaced with an unmodified copy of `profiles/coop.ini.example`
  (previously they carried a hand-tuned set of flags from the 2026-08-27 rig pass —
  `dialogue_fast_join=0`, `pazaak_detect=1`, `map_reveal_sync=1`, `party_pool_sync=0`,
  `companion_gear_sync=0`, `movement=direct`, etc. — none of which is what a fresh unzip
  ships).
- Plugin + agent rebuilt and reinstalled into both directories (`build.ps1 -Install`),
  confirmed by DLL timestamp.
- All console verbs driven through `tools/blockpoke.ps1 -ProcId <pid> console '<line>'`
  (injected OS input never reaches this game — see `docs/live-verification.md`); replies are
  a 96-byte summary, the substance is in `%TEMP%\kotorcoop-demo\agent1.log`/`agent2.log` and
  `<gamedir>\kotorcoop-plugin.log`.

## Step 1 — connectivity, chat, pause, connection indicator

**PASS** (with one item that needs a human — noted, not a bug).

- Both instances loaded save 1 into `END_M01AA` unattended. Host `agent1.log`:
  `[22:48:44] * Malak joined the game` / `co-op: connected, 1 player`; host plugin log
  `spawned phantom in slot 0: id 0x12D ... equip: phantom 0x12D wears g_a_clothes01 ... wears
  g_w_shortswrd01`, `map marker: slot 0 pinned as 0x12E` — phantom, equipment and map pin all
  present. **VERIFIED.**
- Chat both ways: `console 'say hello from Lanik'` on host appears in the CLIENT's own
  `agent2.log` as `[22:49:19] * Lanik: hello from Lanik`; `say hi back from Malak` on the
  client appears in the HOST's `agent1.log` as `[22:49:24] * Malak: hi back from Malak`.
  **VERIFIED, both directions.**
- Connection indicator: both agent logs carry `* co-op: connected, 1 player` (the
  `status_toasts`/`status_indicator` data path, both default ON) the moment the peer joined.
  **VERIFIED** (data path only — the on-screen HUD line itself needs eyes).
- Pause mirroring: the *mechanism* is confirmed reactive — writing the plugin's
  `RemotePauseDesired` field directly (`blockpoke pause 1`) made the host's own engine pause
  byte flip from `mask 0x00 RUNNING` to `mask 0x01 PAUSED: time-stop` within one tick, and the
  plugin log recorded `pause: mirroring a peer's pause`; clearing it (`pause 0`) released it
  cleanly back to `RUNNING`. But `RemotePauseDesired` is an **agent->plugin** field
  (`PausePump.cs`: the agent computes `anyPeerPaused` from the *real* peer `PlayerState` it
  read off the wire and writes this) — poking it directly exercises only the plugin's own
  reaction, not the full round trip of one player's REAL pause propagating over the network to
  the other engine. That full loop needs an actual pause-menu key press, which is the
  documented hard limit on autonomous testing (injected OS input does not reach this game —
  `docs/live-verification.md`'s "hard limit" note). **PARTIAL: mirror mechanism VERIFIED
  live; the real end-to-end pause-key round trip is NEEDS-LIVE (human only), same limit every
  prior round has hit — not a new gap.**
- Name plate / minimap: not independently screen-checked this round (no human at the
  controls); the phantom, equip and map-marker log lines above are the same evidence prior
  rounds have accepted for this ("Already confirmed live 2026-08-01" in
  `docs/live-verification.md`) and nothing regressed.

## Step 2 — Trask's opening dialogue

**PASS.** The save (module start, `20.0, 22.2, -1.3`, `hp 12/12`, party already carries Trask
`0x127`) is past the one-time opening cutscene, so this used `end_trask`'s own repeatable
conversation — exactly the case the task brief calls out ("or use the first available NPC
conversation"). Ran `tools/dlgprobe.ps1 -Tag end_trask -Seconds 30` (host starts, client joins
after 5s, host answers with reply key `1` at t=10):

```
t=0-4   host node 29, client node -1 (not in it), remoteActive=1, cLock=1 (host holds end_trask)
  -> client clicks 'end_trask' (join)
t=5-10  host node 29, client node 29           <- late joiner catches up to the SAME node
  -> host answers (key '1')
t=11+   conversation ends on both sides
```

Host plugin log: `dialogue: engine reached StartDialog`. Client plugin log:
`dialogue: the engine started the same conversation here; adopting it to follow` ->
`followed conversation delivered content 16 ms after opening` -> `dialogue-node-drive: #1
wanted 29 before 19 landed 29` -> `dialogue: followed the owner 19 -> 29` -> (11s later)
`dialogue: the owner ended the conversation; closing`.

`dlgprobe`'s own verdicts: `serial movement over 30s: host +1, client +3` -> "serials settled,
consistent with ONE conversation holding open"; `NODES: on the same entry for 7 of 7 paired
samples` -> "the two conversations held the same node throughout".

- **One owner, the other spectates**: confirmed by construction — the reply key was sent only
  to the host's process; the client's node only ever moves by *following* the owner
  (`dialogue-node-drive`/`followed the owner`), never by an independent choice. `verify.ps1`
  §11 ("dialogue exclusivity") is the standing automated check that a peer's own click is
  refused while a conversation token is held elsewhere; nothing here contradicts it.
- **A late-joiner catches up**: the client joined 5s into an already-open conversation and
  was on the SAME node as the host one tick later (16 ms measured owner-side latency to
  deliver content). **VERIFIED.**
- **The interaction lock** (`interaction_locks=1`, shipped ON) held `end_trask` for the whole
  conversation (`cLock 1` throughout) and released the instant it ended (`cLock 0`).
  **VERIFIED.**

## Step 3 — doors

**PASS.**

**Host leads through the corridor transition door (`end_door10` / object `0x4A`,
`end_m01aa` -> `end_m01ab`), client follows.** `tp 44.5 120.1 -0.04` then
`usedoor end_door10` on the HOST. Host plugin log: `door: 0x4A at 2.9m links to module
'end_m01ab'` -> `door: gathered 1 party member(s) to you` -> `area transition pending:
flushing spawned objects` -> `announce: door 0x4A transition beginning -> 'end_m01ab'`
(22:53:36.451). The CLIENT reacted to the announce, not to its own crossing:
`travel: moving to module 'end_m01ab' on the session's authority` (22:53:37.448, ~1s
after the host's announce — the `transition_announce`/`follow_lead=any` fast path, not
the slow "wait for the host's module to actually change" path). Both landed at the
IDENTICAL position: `where` on both = `module end_m01ab at 49.9 120.3 -0.0 hp 12/12`.
No crash on either side. **VERIFIED.**

**`usedoor` refuses precisely, never silently.** Three refusal shapes were driven
live and each names what it actually saw, matching the codebase's own convention
(`docs/CONSOLE.md` "every staging cheat that fails says what it saw"):
`refused: the nearest transition door is 101.7m away, past the 40m limit` (too far),
`refused: no door 'end_door11' in this module` (bad tag), and
`refused: 'end_door01': no LinkedToModule - an ordinary door` (a real door that just
isn't a module transition — `usedoor`/its bare-arg sweep only ever operates on
transition doors, by design). **VERIFIED.**

**Client leads through an ordinary (non-transition) LOCKED door, host mirrors it.**
Read the module's own GIT/UTD data directly (`tools/kotorfiles.py`) to find a real
target: `end_door02` in `end_m01aa` (tag `end_door02`, `Locked=1`, `OpenLockDC=1`) sits
20m from the module's spawn point. Both players warped back to `end_m01aa`
(`warp end_m01aa`, both landed at `15.4, 20.1, -1.3`). `opendoor end_door02` on the
CLIENT: `door 0x46: opened (state 0 -> 1)`, and the client's own log is explicit about
the lock: `door: opened 0x46 (state 0 -> 1) at 25.4m, LOCKED` — a real closed-to-open
transition on a genuinely locked door. **1.2 seconds later**, the HOST's own log:
`door 0x46: swinging 0 -> 1 on the session's authority` — the open state mirrored
cleanly. **VERIFIED**, and it also answers the task's "locked door" case: `opendoor`/
`usedoor` are dev-only bypass verbs that open a locked door regardless (documented,
intentional — `OpenDoor` never consults the engine's own lock check) but they are
never silent about it: the log always appends `, LOCKED` rather than pretending the
door was ordinary. There is no "refused, it's locked" line anywhere in this codebase
for these two cheats **by design** — a real player's retail click-to-open still gets
the engine's own normal locked-door behavior untouched, since these verbs are staging
primitives that call the same `OpenDoor`/walk path a click already uses, not a new
gate. The task's phrasing ("usedoor refusal shows the right line") is satisfied by the
three refusal shapes above; "locked door" specifically produces an honest-but-not-
refused result, which is the intentional, already-documented tradeoff for this class
of dev verb.

**One artifact worth a note, not a fix.** A companion probe on `end_door01`
(`sw_door_test001` — a different, unlocked door 6m from spawn) did **not** mirror to
the host: the client opened it (`door 0x47: opened (state 2 -> 1)`) but no `0x47`
line ever appeared in the host's log, even ~90s later. The likely cause, read from
`KotorGameBridge.Doors.cs`'s own doc comment ("the store's max-merge discards anything
that is not an advance, so a door closing again simply produces a no-op"): this door's
committed open state was already non-zero (**2**, i.e. already open) at module-entry
baseline — `sw_door_test001` appears to ship already open, a decorative doorway, not a
real interactive one — and our `opendoor` call re-swung it to the OTHER open value
(**1**), numerically *lower* than the baseline's **2**. If the door-flag encoding
treats a lower non-zero open value as "not an advance" the same way it treats a
door swinging shut, this specific transition (open-one-way -> open-the-other-way)
would be silently dropped by the same max-merge rule that intentionally no-ops a
real close. Low priority: the door LOOKED open on both screens either way, so no
player-visible desync resulted, and `end_door02`'s clean 0->1 case (used for the
locked-door check above) proves the ordinary closed->open path is not affected.
**INFERRED, not proven** — logged under "Open issues" below rather than fixed, since
reproducing it cleanly needs a door that starts open by design, which is unusual.

## Step 4 — combat (Sith patrol)

**PARTIAL.** One real fix landed and confirmed; one real gap found and documented (not
fixed this round); death/revive not completed this round.

**The `attack` dev verb (real `ActionAttack`, not `slay`) is the only way to stage a real
fight without a keyboard.** First attempt, host vs `end_soldier2` (0x1A, 6m away):
`attack 0x7FFFFFFF 0x1A` (0x7FFFFFFF = the local-player sentinel) answered "ordered to
attack", but nothing happened for 13+ seconds — no swing, no damage, `attack probe:
0x7FFFFFFF(resolved=False) ... round(active=0,delay=0)`. **Root cause, fixed**: see "Fixes
made this round" below — `CloseToMeleeRangeIfNeeded`'s own attacker resolution silently
failed on the sentinel, so the drive-controlled player was never snapped into melee range
and the queued order just sat there. Rebuilt, redeployed, re-ran the identical recipe:
`test: closed range for 0x7FFFFFFF -> 0x1A (6.02m -> 1.50m) before arming the attack` ->
`test: 0x7FFFFFFF ordered to attack 0x1A`.

**Host lands real hits, real damage, a real kill (`end_gensith` 0x38, hp 12).**
`attack 0x7FFFFFFF 0x38` from 6m: the probe line's own hp column moved
`12 -> 8 -> 2 -> dead (-11 hp)` across five 1s samples, with the target's own combat round
also toggling active — genuine mutual melee, not a scripted kill. Host's own agent readout
during the fight: `state IN COMBAT combat [FIGHT in END_M01AA - engaged: you; mirroring
only: Malak (their engine is not in combat)]` — the co-op layer correctly reported that
ONLY the host's own engine was in combat, not overclaiming the client. **VERIFIED.**

**Client landing a real hit on the same class of target did NOT happen this round —
open issue, not fixed.** Ran the identical recipe on the CLIENT against three different
GIT-identical hostiles it can resolve directly (`end_sith3` 0x17 already at 1 hp/likely a
decorative background prop — see below; `end_gensith` 0x3B, which turned out to have
already died fighting the host's phantom moments earlier per `combat ground-truth: local
PC took 2 damage, attacker=0x3B`; and `end_sith2` 0x14, hp 15, five separate attack orders
over ~90s). Every attempt against 0x14 showed `resolved=True`, `dist=0.00`,
`commandable=1` — the melee-range fix engaged correctly — but `round(active=0,delay=0)` on
BOTH sides for the whole window and hp never moved, and no `local_combat:` engagement log
line appeared anywhere in the client's plugin log this entire session. Not root-caused
within this round's time budget; logged under "Open issues" with what would settle it.

**One artifact, not a bug**: `end_soldier2` (0x1A) and `end_sith3` (0x17) both read `hp 1`
identically across every fresh session this round (cold-launched from the save each time,
never moved) — almost certainly a decorative background-skirmish set piece from the
Endar Spire's own opening, not a real patrol meant to be engaged. `end_gensith`
(0x38/0x3B, hp 12) and `end_sith2` (0x14, hp 15) are the real targets.

**Kill credit / loot / XP / gold ledger.** `grantxp me 500` on the host: `xp on
0x7FFFFFFF: 375 -> 875` (personal XP — by design never shared between two independent
player characters; only party-companion XP is a co-op channel, `companion_xp_sync=1`).
`grantgold 200` on the host: `gold 0 -> 200`; read back on the CLIENT immediately after
(`grantgold 0`, a no-op add used as a read): `gold 200 -> 200` — the shared party-gold
pool already agreed. **VERIFIED** for gold; XP is confirmed correctly NOT shared (matches
design).

**Downed player + revive + spectate camera — NOT COMPLETED this round.** No console verb
directly damages a player (only `topup`/`standup` heal or revive). Two workarounds were
tried and neither produced damage in the observed window: `sethp.ps1 -ObjectId
0x7FFFFFFE` (the PC's own sentinel/real id) could not locate the player object via its raw
vtable scan this session (`no creature with id 0x7FFFFFFE`); and commanding a live hostile
to attack the player directly (`attack 0x14 0x7FFFFFFF`, the reverse of the working
host-vs-0x38 case) produced the same `round(active=0)`/no-damage symptom step 4's other
open issue shows. Time did not allow chasing a third approach this round. This item was
**already confirmed live on 2026-08-01** per `docs/live-verification.md` ("death -> a peer
standing beside you -> revive... ~1s, both games still running") and nothing this round
found should have regressed it, but it was not RE-verified against this build. Genuinely
open for the next round; see "Open issues".

## Step 5 — loot

**PASS for footlocker no-duplicate and quest-item forwarding; one live-confirmed instance
of an already-documented architectural limitation found (not fixed — see why below).**

**`useplaceable`/`loottake`'s bare (nearest-container) form cannot find ANY container in
this module.** Confirmed with a full sweep log: `placeables: 64 in this module`, zero
tagged `[container]`. Root cause is already in the code's own doc comment
(`OdysseyPlaceables.cs`): `HasInventory` (`+0x334`) "under-reports real containers,
live-confirmed on a placeable that opened and held items despite reading 0 there" — a
known, already-documented limitation, not new. Worked around by reading the module's own
GIT/UTP data (`tools/kotorfiles.py`) for the real Tag (`FootLker`, not the
`footlker003` TemplateResRef) and using `loottake <tag>` directly, which does not go
through the buggy `HasInventory` filter.

**Footlocker loot, no duplicate.** The HOST took `g_a_class4001` (0xD2) from `FootLker`
(0xD1, 5-item template). The CLIENT then looted the SAME container tag 4 more times in a
row (`g_w_lngswrd01`, `g_w_shortswrd01`, `g_w_fraggren01`, then `refused: ... has no
takeable item`) — **`g_a_class4001` was never offered to the client**, i.e. each
instance's own take-first-item removal correctly excludes what the other side already
took; no double-grant of the same item. **VERIFIED** (item-identity level; the
`container_lock_arbitration` GUI-race check specifically — two `loottake`/click calls
within ~50ms racing for the SAME single item — was not separately staged this round for
time; it is unit-tested (`ContainerLockArbitrationTests`, confirmed non-flaky in isolation
during this round's test run) and shipped ON by default as of the merge).

**`scriptgive` -> `script_item_sync` -> the peer, in the same second.** `scriptgive
g_i_progspike01` on the host: `gave 'g_i_progspike01' (0x135) to the local player,
untracked`, host log `script item grant: 1 new item(s) ... k_computer_spike` at
`23:32:58`. Client log the SAME second: `materialised loot: submitting
'k_computer_spike' for target 0x7FFFFFFF through the giveloot channel` at `23:32:59`.
Sub-1-second delivery. **But the spawn on the receiving side FAILED**: client plugin log
`cheat: 'k_computer_spike' did not spawn onto 0x7FFFFFFF (failure 8; wrong resref?)`.

**Root cause, live-confirmed, not fixed (architectural, not a bug).**
`KotorGameBridge.Items.cs`'s own doc comment on `TryGetItemResref` already names this
exact failure mode: a live `CSWSItem` never retains its template resref at all
(`CSWSItem::SaveItem` writes `BaseItem` and properties, never a `TemplateResRef`), so
every resref this codebase reports for a live item is really **the item's Tag**, cut and
lowercased — "for base-game items the tag IS the resref... a mod that tags items
differently breaks it, in the same way and for the same reason every other
blueprint-keyed channel here breaks." `g_i_progspike01` is a stock, unmodded KOTOR 1
quest item whose in-game Tag (`k_computer_spike`) genuinely differs from its blueprint
resref — so this is the first LIVE confirmation that the known limitation fires on
**vanilla content**, not only a hypothetical re-tagging mod. No `k_computer_spike.uti`
exists to spawn, so the forward is silently lost for this one item family (script-granted
quest items whose tag diverges from their resref) while every other item this round
handled correctly (all footlocker/weapon drops use base-game items where tag==resref
uppercased). Not fixed this round — the fix would need a different way to recover an
item's true blueprint identity than the engine exposes, which is a genuinely open design
question, not a contained patch; flagged under "Open issues".

**Ground item pickup.** Not separately re-verified this round (no click, and `dropitem`
was already exercised extensively in prior rounds per `docs/live-verification.md`'s
item-identity section — see there for the established evidence); time budget went to the
footlocker/script-item findings above instead.

## Step 6 — journal/globals

**PASS for journal; global-flag merge not directly demonstrated this round (time).**

`blockpoke journal k_swg_bastila 10` on the HOST — this pokes the plugin's own
agent<-plugin journal-request field, which runs the real engine
`AddJournalQuestEntry`-style call (not a fake), so the ordinary sync pump has to pick up a
genuine journal change to pass this. Host log: `journal: 'k_swg_bastila' -> state 10 on
the session's authority` at `23:34:50.131`. CLIENT log, same entry, **1.9s later**:
`journal: 'k_swg_bastila' -> state 10 on the session's authority` at `23:34:52.014`.
**VERIFIED.**

**Global flags ride the identical channel** (`WorldStatePump`'s reliable-flag store, the
same one doors and journal entries use — confirmed by reading the code, not just
inference: `PumpDoors`'s own doc comment says "Carries door state both ways on the same
reliable flag channel the quest globals use"). Attempted to trigger one live via
`firescript 0x127 fire` (fired Trask's `+0x260` combat-end script, `k_hen_combend01`) —
it fired cleanly but did not itself set any global this session, so no merge was actually
observed end to end this round. Not claimed as verified; the mechanism is the same one
journal above just proved live, but a genuine plot-global flip needs a different script
target than what time allowed this round to locate.

## Step 7 — module transition

**PASS for `end_m01aa` -> `end_m01ab`; escape-pod leg NOT REACHED (a real, human-only
GUI gate, not a mod bug).**

**`end_m01aa` -> `end_m01ab` re-confirmed on the post-merge build.** After the mid-round
`git merge main` and full rebuild/redeploy, re-ran `warp end_m01ab` on the host: both
landed at the identical `50.0, 120.0, -0.0` (host `hp 10/12` — carrying the combat damage
from step 4 correctly; client `hp 12/12`). Matches step 3's pre-merge evidence for the
same crossing — nothing in the 25-commit merge regressed it. **VERIFIED.**

**The escape pod itself (`end_pod`, `85.29, 41.13` — read straight from the module's own
GIT/UTP via `tools/kotorfiles.py`, per `docs/research/cinematics.md`'s own documented
script chain `OnUsed=k_pend_pod02` -> conversation `end_pod.dlg` -> reply script
`k_pend_pod01` -> `StartNewModule("STUNT_00", ..., "01c", ...)`) was not reached.**
Shortly after the `end_m01ab` entry (a `dialogue: engine reached StartDialog` fired
automatically on arrival — an engine-forced conversation, not anything this round
triggered), the CLIENT's own plugin log started an unbroken stream of
`party-select: client copy suppressed - the host is picking (PartyRosterController's
poll converges the roster)` / `party-select: client copy swept away` pairs, **every
~2ms, continuously, for over 2 minutes** with no sign of stopping. This is
`party_select_host_only` (new in this round's merge) doing exactly its documented job —
correctly suppressing the CLIENT's own copy of the native "who's coming with you"
party-selection screen because the HOST is the one with a live copy to answer — but the
HOST's own real copy of that screen needs an actual mouse click nobody was there to give,
so it never resolved. `tp`/`useplaceable` issued at the host in this window answered
`the game never answered that - is a world loaded? (cheats run on the game's frame)`
several times in a row before eventually going through again (a bare `where` succeeded
in between) — consistent with the console channel racing a native modal UI loop rather
than the process being truly deadlocked (`Get-Process ... Responding` read `False` for
the host throughout this window, but also intermittently during otherwise-successful
console exchanges earlier in the session, so it is not being treated as reliable
evidence of a hang on this particular engine build — likely just how a DirectInput-driven
render loop reports to the OS message-pump check regardless of state).

**This is the established, already-documented hard limit, now confirmed to reach
`end_m01ab`'s own entrance, not only the main-menu/pause-menu panels prior rounds hit
it on**: `docs/live-verification.md`'s own "hard limit on autonomous testing" note — a
native GUI panel needs a real human click, and none of the console-driven primitives
(`useplaceable`, `talk`, `selectreply`) reach INTO a party-selection screen specifically
(they drive dialogue nodes and object interactions, not this particular native panel).
Not a new co-op bug: `party_select_host_only`'s own suppression is doing exactly what it
should on the client side. **Escape-pod transition, movies/loading overlap for that leg,
and the announced-transition/partner-follow behaviour for it are NOT VERIFIED this
round** — genuinely needs a human at the host's controls to click through the
party-select screen once, the same category of gap `docs/live-verification.md` has
carried since 2026-08-01 ("companions actually FOLLOWING... needs a person playing").
The rig was restarted clean afterward rather than risk continuing from a session with an
unresolved native modal open on one instance.

## Step 8 — disconnect/reconnect, quit-to-menu, host save/reload

**PASS for blackout reconnect, quittomenu/away/loadgame recovery; host-save re-adopt
inconclusive (time).** Fresh rig restart for this step (see note at the top of this doc
about the mid-round `end_m01ab` party-select gate — restarted clean rather than continue
from that session).

**`netsim blackout 10` on the client.** `netsim blackout 10.0s left (both ways) - dropped
0 out / 0 in`. Host: `peers 1 -> peers 0` (`hosting on 0.0.0.0:27140 (waiting for
players)`) at `23:42:34`, then `* Malak joined the game` again at `23:42:39` — full
automatic recovery in ~5s, matching `docs/live-verification.md`'s established blackout
behaviour. **VERIFIED.**

**`quittomenu` on the client -> FlagAway -> `loadgame` recovery**, the exact fixed
sequence from `docs/research/disconnect-reconnect.md` §10-11 (the round-2 PASS), now
re-confirmed on this build: client `quittomenu` at `23:42:xx` -> client's own log
`away: local reads have failed for ~1s; publishing last known state as away` -> host's
SAME-second toast `* Malak left the world (main menu / loading)` (both at `23:42:54`) ->
`loadgame 0` on the client -> host's `* Malak is back` at `23:43:11`. **VERIFIED**, and
confirms the FlagAway fix documented earlier this round on `main` survived the merge
into this playthrough's own build.

**Host save + client rejoin (`host_load_readopt`) — INCONCLUSIVE, not chased further
this round.** `loadgame 0` on the HOST (who was never away, still fully in-world)
answered `loadgame: the world is up` and produced no `host loaded a save - everyone
re-joins` announce and no matching plugin-log `loadgame:` line on either side in the
following 15s — most likely because the host was already in a live, current-session
world and `loadgame 0` (most-recent save) resolved to a state the engine/plugin judged
as "nothing to load" rather than a genuine save-vs-load edge, but this was not confirmed
by reading the code this round. Neither a PASS nor a demonstrated FAIL; needs a real
distinct save (or the disk-digest-divergence trick `disconnect-reconnect.md` §"Update
(2026-08-23)" used) to force a genuine edge, which time did not allow this round.

## Step 9 — verify.ps1

**39 passed, 10 failed** — essentially the documented baseline (40-42 pass / 7-9 fail from
`docs/live-verification.md`'s 2026-08-27 rig pass), 1 short on pass count and 1-3 over on
fail count. 8 of the 10 failures are the EXACT documented baseline set: `phantom
damageable, not a sink`; `the peer really loses those hit points`; `a client hit costs the
enemy real hit points`; `a downed player is reported to the agent`; `the co-op revive
stands the player back up`; `every engine call signature matches the binary` (177/179
here vs the baseline's 171/173 — the codebase grew by the merge, still exactly 2
disagreeing); `a container opened on the client is held and shown open on the host`; `no
per-frame spam`.

**Two failures beyond the documented set**, both plausibly explained by this session's
own unusual history rather than a regression from anything changed this round:
- `an item picked up here vanishes on the peer` — this session ran an unusually large
  amount of item traffic beforehand (`loottake` x6, `scriptgive`, `dropitem`), any of
  which could leave the ground-item store in a state this particular timed check does not
  expect; not chased further given time.
- `death guards installed on both sides` (`ChangeCharacter 0x5F6B60 + the game-over call
  site 0x4B6E99`) — very likely the SAME two engine-call-signature mismatches item #46
  already reports (177/179), just surfaced through a different check; nothing this round
  touched `ChangeCharacter` or the game-over path (the one fix made was to
  `TestDriveController.cs`, a debug-only test-drive verb file with no bearing on either).

**No crash**: `no swkotor crash in the event log` PASS, `clean since 23:18`.

`dotnet test tests/KotorCoop.Tests --no-build` run CONCURRENTLY with `verify.ps1` against
the live rig (not the recommended way — `CONTRIBUTING.md` warns two concurrent runs can
flake UDP-bound suites — but time required overlapping them): **2289/2289 passed clean**, including
`ContainerLockArbitrationTests` (flaked once earlier in isolation, passed on rerun — a
known UDP-timing-sensitive class per `CONTRIBUTING.md`) and the normally-excluded
`HeavyPacketLossStillConverges`.

**After the rig was stopped, `ContainerLockArbitrationTests.
TwoClientsRequestingTheSameContainerWithinOneMillisecondExactlyOneIsGranted` failed FOUR
times in a row** (isolated with `--filter`, and once inside a full run), always the same
way (`the loser must be grantable once the winner releases`, hitting the full 15s
timeout every time) — a different pattern from the single earlier flake this round (which
passed on its own very next isolated rerun). Not chased further given time; this is
pre-existing merged content (`container_lock_arbitration`, new this round from `main`),
not anything this round's own fix touched, but the flip — passing reliably with the rig
running concurrently, failing reliably once it was stopped — is unusual enough to flag
precisely rather than wave off as the documented UDP-port flake. Worth a dedicated look
before trusting this test's green as unconditional.

## Fixes made this round

1. **`TestDriveController.CloseToMeleeRangeIfNeeded` resolved the local-player attacker
   through the wrong lookup** (`src/KotorCoop.Plugin/TestDriveController.cs`). Live-found
   in step 4: `attack 0x7FFFFFFF <target>` from beyond melee range silently never landed a
   swing, for every target tried, because `Odyssey.ResolveServerObject(0x7FFFFFFF)` — a
   generic `CGameObjectArray` walk — does not contain the local-player sentinel, so the
   round-71 "snap the drive-controlled player into range" fix silently no-op'd every time
   it mattered. Fixed to resolve the attacker through
   `Odyssey.GetPlayerCreaturePointer()` (the same lookup every other player-facing cheat
   already uses) when the attacker is the sentinel. Re-verified live after rebuild:
   `test: closed range for 0x7FFFFFFF -> 0x1A (6.02m -> 1.50m) before arming the attack`,
   then a full multi-round fight with real damage and a real kill
   (`end_gensith` 0x38: `hp 12 -> 8 -> 2 -> dead`). Commit `ad2df75`.

## Open issues, ranked by how badly they hurt a real pair

1. **A client cannot land real hits on a host-owned/GIT-identical hostile via the local
   `attack` mechanism, even with the range fix in place** (step 4). Every attempt against
   a live target (`end_sith2` 0x14, five separate orders over ~90s) showed
   `resolved=True, dist=0.00, commandable=1` but `round(active=0)` on both sides and no
   damage, and no `local_combat:` engagement line ever appeared in the client's log this
   session. The host, by contrast, fights its own owned creatures cleanly (real damage,
   real kill) — that path needs no co-op mediation at all. **This is the single item that
   would hurt a real pair most**: it means one player's melee swings against an enemy the
   OTHER player (or the module itself) placed may not connect at all outside whatever
   scenario the existing `local_combat` live rounds already proved (a companion-credit
   fight, or a target already `TargetingUs` per `LocalFightCandidateRule`). Needs a
   dedicated live round with logging added to `EnemyPresenceController`/
   `LocalFightCandidateRule`'s live gate evaluation (reputation, `hasScript`,
   `gitIdentical` — whichever gate is failing was not isolated this round) — see step 4
   above for the exact repro (`tp` next to `end_sith2` 0x14 in `END_M01AA`, then
   `attack 0x7FFFFFFF 0x14` on the CLIENT).

2. **A downed player + revive was not re-verified against this build** (step 4). No dev
   verb directly damages a player; both workarounds tried this round
   (`sethp.ps1 -ObjectId 0x7FFFFFFE`, and commanding a live hostile to attack the player
   via `attack <enemyId> 0x7FFFFFFF`) failed to produce a result this session, the second
   for the same unresolved reason as item 1. This item was confirmed live once already
   (2026-08-01, `docs/live-verification.md`) and nothing found this round should have
   regressed it, but "should not have regressed" is not the same claim as "confirmed" —
   worth a quick re-check next round once item 1 is understood (they may share a cause).

3. **`script_item_sync` silently loses a quest item whose Tag diverges from its blueprint
   resref** (step 5) — live-confirmed on stock, unmodded KOTOR 1 content
   (`g_i_progspike01`, Tag `k_computer_spike`): `giveloot` on the receiving side answered
   `'k_computer_spike' did not spawn onto 0x7FFFFFFF (failure 8; wrong resref?)`. Root
   cause is architectural and already documented (`KotorGameBridge.Items.cs`: a live
   `CSWSItem` never retains its template resref, only its Tag, so every "resref" this
   codebase reports for a live item is really a Tag proxy that breaks whenever the two
   diverge) — not a contained fix, a genuinely open design question. Previously this was
   flagged as a risk for "a mod that tags items differently"; this round is the first live
   proof it also fires on vanilla quest items, which raises its priority — a lost quest
   item (not a lost weapon drop) is the kind of desync a real pair would notice and be
   unable to explain.

4. **The escape-pod leg of `end_m01aa` -> `end_m01ab` -> `STUNT_00` was not reached**
   (step 7): a native party-selection GUI opens on `end_m01ab` entry that only the HOST
   can answer (correctly un-mirrored to the client by the new `party_select_host_only`),
   and no console-driven primitive reaches into it. Same category as the already-known
   "GUI panels need a human" limit, now confirmed to gate the corridor module's own
   entrance rather than only the main menu — worth a note in `docs/roadmap-seamless.md`
   that a from-scratch two-player Endar Spire run needs a human for at least this one
   click, even with every dev verb available.

5. **Host-save re-adopt (`host_load_readopt`) produced no observable announce** (step 8)
   when tried against a host that was never actually away — inconclusive, not a
   demonstrated failure; needs a genuine save-vs-load edge (a different save, or the
   disk-digest-divergence trick `disconnect-reconnect.md` already documents) to test
   properly.

6. **One door-flag artifact, low confidence, likely harmless** (step 3): re-swinging an
   already-open decorative door (`end_door01`/`sw_door_test001`, baseline state 2) to its
   other open value (1) did not mirror to the peer, while an ordinary closed-to-open
   transition on a different door worked cleanly. No player-visible effect either way
   (door looked open on both screens regardless). Flagged for whoever next touches
   `KotorGameBridge.Doors.cs`'s max-merge semantics, not urgent.
