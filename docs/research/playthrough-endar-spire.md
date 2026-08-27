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

_pending_

## Step 5 — loot

_pending_

## Step 6 — journal/globals

_pending_

## Step 7 — module transition

_pending_

## Step 8 — disconnect/reconnect, quit-to-menu, host save/reload

_pending_

## Step 9 — verify.ps1

_pending_

## Fixes made this round

_pending_

## Open issues, ranked by how badly they hurt a real pair

_pending_
