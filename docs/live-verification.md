# Live two-instance verification checklist

Run the pair, then verify each channel. "Logs" items I can confirm from the agent/plugin
logs; "Screen" items only you can see. NEEDS-LIVE items are the offsets/values that were
reasoned but not observed — a wrong one fails safe (no effect or mis-placed text, never a
crash), so confirming them is the point of this pass. For how to build and run the pieces
this checklist exercises, see `docs/CONTRIBUTING.md`.

## Already confirmed live (2026-08-01) — no need to re-verify unless something regresses

Session start from inside the game (Co-op button → panel → **Host** on one instance,
**Connect** on the other), both instances loading their REAL saved character through the
plugin's in-process load with no injected clicks; the agent auto-started by the plugin;
phantoms both ways with body armour and weapon; map pins; the content digest agreeing on two
identical installs; and death → a peer standing beside you → revive (`death: the local
player is down` → `revive applied` → `topped up to 15 hp`, ~1 s, both games still running).

**Sustained session, combat channel and reconnect (2026-08-01).**
- **25-minute soak, two instances connected the whole time**: no new crash lines on either
  side, both games and both agents up throughout, memory flat (host 190→191 MB; the client
  and the agents actually ended *lower*), peer held continuously, and **0 malformed packets
  out of 184,669 received / 76,769 sent**. No leak, no corruption, no drift.
- **Enemy health mirrors**: damaged an enemy on the host (15→7) and the client's copy of the
  same object id followed to 7. Note it took longer than one heavy pass — allow ~10 s before
  calling a mirror broken.
- **Enemy death replicates through the real death path**: killed it on the host, both
  dashboards moved to `1 dead` and the client's plugin logged
  `combat: killed 0xE on the owner's authority` — i.e. the client ran the engine's own kill,
  it did not just copy a number.
- **Automatic reconnect works unattended**: killing the host agent outright and restarting it
  healed the session with no action on the client, and afterwards the full chain was
  re-verified live — moving the host put the client's `coop_phantom` body at exactly
  (24.00, 60.00). Enemy state stayed consistent across the break (`26 known, 25 alive, 1 dead`
  on both).
- **Real AI combat did occur**: left beside two hostile Sith soldiers, the host player was
  worn down 12 → 1 hit points by the game's own AI, and the session stayed up throughout. The
  player never fought back, because that needs input (see the limit below), so this is
  one-sided combat — evidence the world is live under a session, not evidence of two players
  fighting together.

⚠ **The hard limit on autonomous testing: injected input does not reach the game at all.**
Both were tested directly this session — mouse (the click loader still times out) and
keyboard (holding W for 3 s moved the player 0 units). Nothing that requires a human at the
controls can be self-verified: two players actually fighting, any dialogue or quest
progression, and companion following. Those need a person playing.

**Travelling together (2026-08-01).** The host warps, the client follows, and both arrive
intact: `end_m01ab pos (50.02, 119.95) hp 12/0` on both, with each side respawning and
equipping the other's phantom in the new module. Getting there took three fixes worth
knowing about, because each failed in a way that pointed somewhere else:
- the transition **crashed** the process (the engine's map init dereferences the server's
  module every frame with no null check, in a window a plugin-driven transition reaches
  before the client has torn its own world down) — `docs/research/travel-map-init-crash.md`;
- the module name carried a **trailing junk byte** (a CExoString length that includes its
  terminating null), which the module filter tolerated but travel could not: the client
  asked for `end_m01ab\0` and retried forever;
- the agent kept a **stale player pointer** across the transition, reporting `pos (0,0,0)`
  and hit points like `21544/21544` — plausible-looking garbage that reads as a network
  fault. It now revalidates the cached creature every tick.

**Party reconciliation (2026-08-01).** Both halves measured, closing the design's open
"inferred, not measured" risk:
- **Solo mode is real and one-sided** — `partystate.ps1` reads `soloMode 0` on the host and
  `soloMode 1` on the client, with the same companion (slot 0, id `0x127`) on both.
- **The mirror actually drives the client's companion**, proven by a control rather than by
  coincidence. Displacing the CLIENT's companion by writing its position: with the agents
  running it converges back to the host's exact value within ~4 s; with the agents stopped it
  stays where it was put for 6 s+. Two identical saves would place a companion identically,
  so only the control distinguishes "mirrored" from "same script" — run it that way.
- Not covered: companions actually FOLLOWING. The Endar Spire opening script-locks Trask so
  his AI never issues follow orders, and warping to `end_m01ab` does not help — the party
  arrives with **zero** companions there (`partystate.ps1` reports `companions 0` on both
  sides, and the `carth` creature in that module is a placed NPC, not a party member). This
  genuinely needs a save from a real playthrough with an active following companion; it is
  the one item here that cannot be manufactured. Solo mode is still correctly held on the
  client even with an empty party, which is the failure mode that would matter.

**Content mismatch (2026-08-01).** Verified with a genuine difference — `install-datamods`
run against ONE install only, giving it a real 510-row `appearance.2da` against the other's
509:
- host and client independently reached the SAME verdict (that agreement is the design
  requirement): `compatible, 3 warning(s)` with `OFF: Appearance`;
- the warnings name the cause in plain language ("appearance.2da differs; phantoms keep
  their default appearance", "override folders differ (4 vs 2 files)…");
- behaviour matched the verdict: phantoms still spawned and were equipped on both sides with
  no appearance write, and enemies/movement/items carried on. The session was degraded, not
  refused — which is the whole point of the policy.
- Not covered: the REFUSE classes (different executable, different save). Those need two
  genuinely different installs/saves and remain unit-test-only.

Still unconfirmed live: fog-of-war reveal, and the phantom name plate visually (the phantom
renders; the band was only observed on a nearby NPC).

## Bring it up

The way a player does it — launch the game, click **Co-op**, then **Host** on one side and
**Connect** on the other. For unattended development runs:

```powershell
# 1. stop the orphaned agents from the previous run (they target dead games)
Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. rebuild the Agent with the current code (its bin was locked until step 1)
dotnet build .\src\KotorCoop.Agent\KotorCoop.Agent.csproj -c Debug

# 3. launch both games, load saves, connect the agents (unattended)
pwsh tools\demo.ps1               # -Save N for a different slot; -Stop to tear down
```

⚠ Two traps this checklist has hit repeatedly. **Injected clicks are discarded unless the
game window is the ACTIVE window** — a minimise/restore cycle is what revives them, and the
browser panel is CENTERED not scaled (screen = layout + (80,60) at 800×600). And
`tools\shot.ps1` returns a **stale frame** after a minimise/restore: it showed the main menu
for an instance that was provably in-world. Trust `blockpoke.ps1`, `creatures.ps1` and the
logs over a screenshot.

Logs land in `%TEMP%\kotorcoop-demo\agent1.log` / `agent2.log` and
`<gamedir>\kotorcoop-plugin.log`.

## Bring up three instances

`demo3.ps1`/`verify3.ps1` are the three-player variants of `demo.ps1`/`verify.ps1` — one host,
two joiners — for exercising peer-list/slot-table paths two instances can't (`tools/README.md`
indexes both). Same shape as the two-instance rig:

```powershell
pwsh tools\demo3.ps1              # launches all three, loads saves, connects both joiners
pwsh tools\verify3.ps1            # PASS/FAIL battery against the live trio
pwsh tools\demo3.ps1 -Stop        # tear down
```

⚠ **The trap that ate a whole session (2026-08-27/28): `build.ps1 -Install` (or `demo.ps1`)
does not touch a game directory it was never pointed at.** A third game directory created once
by `-CreateGame3` and never rebuilt into again keeps running whatever plugin build happened to
be installed the day it was created — in the round that found this, `D:\Games\swkotor-gog-p3`'s
`dinput8.dll` was three weeks stale (2026-08-07 vs. the other two installs' same-day build) and
instance 3's own menu auto-load failed across four full relaunch cycles, misread at first as a
code regression in the input-injection path. It wasn't — reinstalling fixed it outright:

```powershell
pwsh src\KotorCoop.Plugin\build.ps1 -WarnAsError -Install -GameDir 'D:\Games\swkotor-gog-p3'
```

**Reinstall the plugin into EVERY game directory the rig uses, every time, not just the first
two** — `docs/CONTRIBUTING.md`'s "run this for every game copy the rig uses" rule applies with
the same force to a third (or Nth) directory as it does to the second; nothing about `demo3.ps1`
deploys the plugin either, exactly like `demo.ps1`. Confirmed clean afterward: all three
instances came up first try (no retries logged), `verify3.ps1` scored **34/34** (three agents,
two remote peers on every instance with distinct ids, two phantoms alive on every instance, hit
tables agreeing, no faults, no crash in the event log), and a chat line from the host reached
both joiners' overlays. See this doc's own "Rig pass (2026-08-27, round 2)" and "2026-08-28 —
`demo3.ps1` three-peer" sections below for the full recipe and readings.

## Connectivity & phantoms (Logs + Screen)
- agent1.log/agent2.log: `session ... connected`, `peers 1`, non-zero `rx`/`tx`.
- plugin log: `agent reports 1 remote peer(s)`, `1 phantom(s) alive`.
- Screen: each game shows a phantom of the other player that moves as they move.

## In-game ~ console (NEEDS-LIVE: capture timing, native console at menu)
- Press `~`. A console should open (EnableCheats was set in swkotor.ini on first launch).
- Type `say hello` + Enter.
- plugin log: `console: routed a co-op command`.
- Screen: the other player sees `Lanik: hello` (or `Malak:`). Confirms buffer-capture timing.
- Also try `status`, `disconnect`, `reconnect` — agent log echoes each.

## Console assist & staging cheats (NEEDS-LIVE — full table in docs/research/console-cheats.md § 4)
- Press `~`, then plugin log: `console: completion has N engine console verbs` (expect ~35).
- Type `giv` + Tab repeatedly: it should cycle `givecredits → giveitem → givemed → ...`,
  i.e. the ENGINE's own verbs, not just ours.
- Type `connect 1.2.3.4 ` (trailing space): the hint row reads
  `connect <ip|#n> [password]  <- [password]`. Escape hides it; typing brings it back.
- Type `status`, close the console, quit, relaunch, `~`, Up → `status` comes back
  (`kotorcoop-console-history.txt` beside the plugin log; it must contain no `password` line).
- `god on` → "god ON"; take a hit and confirm health floors at 1 on BOTH screens.
- `topup`, then get downed and `standup` → the body stands within a second.
- **The important one:** stand near an enemy and type `slay` on the HOST (expect
  `killed 0x..`, corpse + loot, client logs `combat: killed 0x.. on the owner's authority`),
  then on the CLIENT (expect `0x.. is the host's; sent the kill to them` and the enemy dying
  on both screens through the ordinary mirror).
- Refusals must SAY SO, never no-op: `slay <peer's name>`, and `slay Trask` (a companion).
- `goto <the other player>` in the same module; then `dropitem g_w_blstrpstl001` and confirm
  the item appears for the other player and vanishes for both when either picks it up.
- No keyboard? `pwsh tools\blockpoke.ps1 -ProcId <pid> cheat god 1` (also `topup`, `standup`,
  `slay <decimal id>`) fires the same channel, and the bare `cheat` prints the status AND the
  plugin's detail sentence. Only the console-UI checks above need a human at the keys.

## Chat overlay (NEEDS-LIVE: anchor coordinates, font alpha)
- After a `say`, the message appears bottom-left, semi-transparent, and fades after ~9 s.
- If it's off-screen → adjust `chat_overlay_anchor` / calibrate the coords in ChatOverlayController.
- If it's fully opaque → the font texture-env is GL_REPLACE (alpha no-op); note it.

## Doors & placeables (Logs + Screen)
- Open a door / a chest / disarm a trapped container on ONE side.
- plugin log (other side): `door 0x..: swinging`, `placeable 0x..: unlocked/opened`,
  `placeable 0x..: trap disarmed`.
- Screen: the same chest reads opened/unlocked on the other instance.
- NEEDS-LIVE: confirms placeable offsets locked +0x260 / open +0x338 / trap +0x278.

## Shared progression (NEEDS-LIVE: XP stats pointer)
- On the host, pick up credits or gain XP (a kill).
- Screen (client): gold matches the host; XP/level tracks the host.
- If gold syncs but XP doesn't, the stats pointer (creature+0x0A74) needs adjusting — gold
  is on a verified offset, XP goes through that pointer.

## Party, journal, pause, world flags (Screen)
- Companions stay one synchronised set; a quest advanced on one side appears on the other;
  either player's pause pauses both; an opened door/killed boss replicates.
- `pwsh tools\partystate.ps1 -ProcId <pid>` prints the party table: companion count, the
  spawned object id per roster slot, and the solo-mode flag. Expect `soloMode 0` on the host
  and `1` on every client — that flag is what stops a client's own follow-AI fighting the
  mirrored positions.

## Content mismatch (Logs)
- To exercise it deliberately, give the two installs a real difference:
  `KotorCoop.Agent.exe install-datamods --game-path <ONE install>\swkotor.exe` rewrites that
  side's `appearance.2da`. Remove the two `.2da` files from its `Override` afterwards to
  restore parity.
- Both dashboards should show the SAME `content` block — that host and client judge
  identically is the design requirement, so compare them rather than trusting one.
- Expect degradation, never a dead session: `OFF: Appearance` with phantoms still spawning
  and being equipped.

## Dialogue token (Logs + Screen)
- Start a conversation on one side. dashboard/agent shows `dialogue: <who> in a conversation`.
- The other side sees the token owner. (Spectate text + control are task #25, not in this build.)

## Map, name plates (Screen)
- Press M: the peer shows as a pin. The phantom's selection band shows the peer's name.
- The pin itself is confirmed in the logs (`map marker: slot 0 pinned as 0x12F`); what needs
  eyes is that it lands in the right place on the M-screen.

## Death and rescue (Logs + Screen)
- Take one player to zero health. Their game must NOT swap control to a companion and must
  NOT show the game-over screen; the plugin logs `death: the local player is down`.
- Walk the other player up to them: `death: revive applied; the player stands up` within a
  second, then `topped up to N hp`.
- Take BOTH players down at once: only then should the game-over screen appear, and on both
  instances together.
- `pwsh tools\blockpoke.ps1 -ProcId <pid> death` prints the whole state, and
  `death revive` fires a rescue by hand.

---

When done: `pwsh tools\demo.ps1 -Stop`. Report anything that didn't fire and I'll fix the
offset/coord; everything here degrades safe, so a miss is a tuning note, not a crash.

## Integration live pass (2026-08-15) — six merged workstreams on the rig

Run against `integration/honest-limitations` with the plugin freshly installed into BOTH
game dirs and the agent rebuilt (block 40). Both instances loaded save 1 into `END_M01AA`
unattended; nothing below needed a human at the controls.

**Enabling change:** `tools/blockpoke.ps1 console '<line>'` was added, writing the same
console channel (`SharedBlock` 0x9F4/0x9F8, reply 0xA58/0xA5C) the in-game `~` console
writes. Injected keystrokes never reach this game, so before this every console-routed
feature — `netsim`, the staging cheats, `status` — was verifiable by a human and nobody
else. It waits on a reply-serial CHANGE, not on non-empty text, because the reply field
holds the previous answer until the next lands.

### Confirmed live

- **Block version 40 is what the running plugin publishes**, and the agent finds it. The
  merge-time reconciliation (item identity took 39, cheats moved to 40, signature `28`) is
  correct on a real process.
- **Cinematic instrumentation is live and reads the engine correctly.** The control line
  `cinematic: a movie is playing with no world up (the main menu's own loop)` appeared
  exactly once on each instance. It also caught two **5.3 s and 5.1 s main-thread stalls
  while in a world** during save load, with no transition movie queued — five seconds in
  which no frame callback ran at all.
- **The pre-store flush is NOT swallowed on a movie-less transition.** Host, before the
  module was stored: `destroyed phantom 0x136 (slot 0)` and `destroyed item body 0x131`,
  then `epoch: world unloading`. Client flushed its item body and three materialised
  enemies the same way. ⚠ **This does not close the hypothesis** — this transition had
  `0 transition movie(s)`. The risk case is a transition WITH a movie (the escape-pod /
  stunt-module chain), which still needs the real trigger.
- **Reconnect: full blackout.** `netsim blackout 10` on the client → host dropped the peer,
  client rejoined **as peer 2, the same id**, one phantom (`0x132`) re-equipped with clothes
  and weapon, never two bodies. Peer-id preservation across an outage is the load-bearing
  fix; a regression here double-banks the returning player's damage.
- **Reconnect: the half-open link.** `netsim blackout 15 out` reproduced the exact ghost
  signature — host expired the peer at 07:39:17 while the client still believed it was
  connected, then the client noticed at 07:39:22 and rejoined as peer 2. Before the fix that
  state was permanent, because the host kept ponging the ghost's pings. **This is the case
  no process-kill test can produce.**
- **0 malformed packets** across two blackouts and ~650 injected drops; `netsim off` returned
  `netsim off (clean link)` both times.
- **Cheat channel end to end**, and the guards say what they SAW rather than claiming
  success: `god 1` on the Endar Spire command deck answered `god was already ON` (the
  `k_pend_area01` min-1-hp pin), and `topup 5` answered `the engine refused it: the player
  reports 0 maximum hit points` — a transient transition-window read, refused rather than
  written as garbage.
- **Travel together survived** `END_M01AA` → `tar_m03aa` with both agents and both games up,
  no access violation. The only first-chance exception is `0x406D1388` (the thread-naming
  marker) in a system DLL, not ours.

### Failed live — `slay` from a client is silently dropped

The client answers `slaying the nearest enemy 0x4 (14.4m away)` and the plugin logs
`cheat: 0x4 is the host's; sent the kill to them (status 2)` — the authority decision is
RIGHT, the client correctly refuses to kill an enemy it does not own. But the kill never
arrives: the host plugin log has no entry at all after the arrival lines, there is no agent
`*` event for it on either side, and the target is still at exactly 14.4 m after two
attempts. The routing reports a success it has not earned, which is worse than refusing.

Reproduce: `blockpoke -ProcId <client> console 'slay'` twice, then read the host log.
Suspects, in order: the client plugin writes the request but the agent never forwards it;
or it is forwarded and the host drops it because the ledger was reset for the new epoch
(`combat: peer-damage ledger reset for epoch A3A42907` fired on arrival).

### Not verified this pass — item identity apply

`drop <resref>` replicates the item to both sides and correctly logs NO identity (a blueprint
drop has no live item to read identity from, and unknown stays unknown rather than being
faked as stock — the intended fail-safe). Exercising the apply path needs the by-object-id
form, and `#<hex>` through the drop FILE produced nothing. The blocker is a harness gap:
`AgentDashboard._lastDropNote` renders only in the dashboard UI, never to the log file, so an
unattended run cannot tell "refused, and here is why" from "nothing happened". Fix that first,
then re-run with an item carrying real charges/upgrades.

## Follow-up pass (2026-08-15, later) — fixes verified, and one defect localised deeper

### Fixed and confirmed live

- **`slay` on the HOST works end to end.** `slaying the nearest enemy 0x1A (27.1m away)` →
  `cheat: killed 0x1A (status 1)`. The authority path itself is sound.
- **The host no longer swallows unresolvable peer damage.** `PeerDamageController` had a bare
  `continue` for an id it could not resolve — no log, no counter — which is what made this
  entire failure class invisible. It now says
  `combat: a peer owes N damage to 0xID, which does not resolve here`, on its own bounded
  budget so successful applies cannot crowd it out. This single line turned an unfalsifiable
  "nothing happened" into a located defect within one run.
- **`slay` no longer claims delivery it has not earned.** A forwarded kill now answers
  `queued a killing blow for the owner, keyed by <id space>` instead of "sent the kill to
  them" — all the caller can actually know is that the debt was banked locally.
- **Drop outcomes reach the log.** `AgentDashboard._lastDropNote` is now a property that also
  calls `Note`, so `* drop: object 0x131 is not a readable item here` appears in the agent log
  instead of only on a dashboard nobody is watching. This was the blocker on unattended
  item-identity verification.
- **The nearest-hostile sweep reads the right census.** On a mirroring instance it now sweeps
  the OWNER's published set rather than the local engine's creature list, and the reply names
  the source: `[from owner census, 32 in 'END_M01AA']`.

### OPEN — the host publishes an enemy id it cannot itself resolve

The client's `slay` still does not kill, and the cause is now located precisely and is NOT in
the cheat:

1. The client sweeps the owner's census — proven by the reply tag `[from owner census, 32 in
   'END_M01AA']`.
2. That census contains `0x35`, so the HOST published it.
3. The host cannot resolve `0x35`: `combat: a peer owes 200 damage to 0x35, which does not
   resolve here`.
4. `tools/creatures.ps1` on the host finds **no `0x35` at all** among its 38 creatures
   (0x36/0x37/0x38 exist). The client has 45 creatures and `0x35` is an `end_weld` at
   (33.58, 22.72) that exists only there.

So an id in the host's own enemy census names nothing the host can resolve. Any damage a peer
forwards against it is held for ever. Mirroring still works because the client MATERIALISES
what it cannot find (`materialise: owner 0x1A -> local 0x135`), which masks the divergence for
every channel except the debt.

**Answered 2026-08-15 (code, not live) — the census was publishing GHOSTS.** Not a
client/server half mix-up: the client half is `CSWCCreature` (vtable `0x74F5F8`) and the scan
only matches `CSWSCreature`. The census comes from a sweep of committed memory for the
`CSWSCreature` vtable word, and that **cannot tell a live creature from the intact bytes of a
destroyed one** — `operator delete` leaves the vtable and the object id exactly where they
were. The duplicate in the dump is the proof that needs no theory: the engine's array maps one
id to one object, so `0x38` appearing at both `0x1505B674` and `0x1923A620` means at least one
of them was never in it. Fix, root cause and fail-safes:
`docs/research/enemy-presence-sync.md` §"Root cause 2026-08-15". The agent now confirms every
scanned creature through the engine's own `CGameObjectArray` (transcribed from
`CGameObjectArray::GetGameObject` 0x004D8230) before publishing, and the cheat path's
owner/local id confusion (`CheatAuthority.MirrorIds`) is fixed alongside it.

**Round one was INCONCLUSIVE** (silent diagnostic). **Round two** localised it to one
contradiction. **Round three** put `resolve` on the game thread and killed the lookup
hypothesis outright:

- agent, host: `census 0x35` -> `object array: held at 0x19179E38 | raw scan: 0x19179E38 'end_weld'`
- plugin, host: `slay 0x35` -> `no object 0x35 in this module`

Same process, same id, two lookups that are supposed to BE the same lookup. The binary rules
out the easy explanations (same array field `+0x1005C` both ways, the `[0x74666C]` sentinel is
`0x00` in read-only `.rdata`, identical bucket arithmetic) — see
`docs/research/enemy-presence-sync.md` §"Round three".

**The live check, round three.** The point of run three is ONE line: the plugin answering both
questions in the same frame.

```powershell
# 1. THE DECIDING MEASUREMENT — in-process, one frame, on the game thread.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'resolve 0x35'
#    console answers a summary; THE FULL LINE IS IN THE PLUGIN LOG:
#      <gamedir>\kotorcoop-plugin.log   ->   "resolve: 0x35: server 0x.. internal 0x..
#      array(+0x1005C) 0x.. array(engine) 0x.. | engine 0x.. walk 0x.. resolve 0x..
#      | at 0x..: vtable 0x.. id+4 0x.. id+0x14 0x.. type 0x.."
#    Please paste that whole line. It is the entire round.
#      engine 0 + walk non-zero  -> the engine call is not the lookup we think; agent is right
#      engine non-zero + resolve 0 -> the post-check is rejecting it, and id+4/id+0x14 say why
#      both 0                    -> compare array(+0x1005C) with array(engine) on the same line

# 2. The same question of an id that WORKS, as the control. Pick any id the host can kill
#    (e.g. one `census` lists and `slay` accepts) so the two lines can be diffed.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'resolve 0x1A'

# 3. Unchanged from round two, for context beside the above.
pwsh tools\blockpoke.ps1 -ProcId <host>   console 'census'
pwsh tools\blockpoke.ps1 -ProcId <host>   console 'census 0x35'

# 4. Only once 1-2 agree: the end-to-end result.
pwsh tools\blockpoke.ps1 -ProcId <client> console 'slay'
# 1. ASK THE HOST what it is publishing and what its own engine says about each id.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'census'
#    line 1  "census: gate: ..." - one of:
#      "array 0x... confirmed K of N ... (no ghosts)"   -> the check runs and the scan is clean
#      "array 0x... confirmed K of N, dropped D (0x..)" -> the check runs and is removing ghosts
#      "NOT CHECKING - no server object array"          -> the pointer chain is wrong; that is
#                                                          the bug, and nothing was ever gated
#    line 2  "census: my own census holds N id(s) ...; N resolve in MY engine, 0 do not"
#            As the OWNER, any non-zero "do not" is the defect, and each is then named.

# 2. ASK BOTH SIDES ABOUT THE DISPUTED ID. This is the line that ends the argument.
pwsh tools\blockpoke.ps1 -ProcId <host>   console 'census 0x35'
pwsh tools\blockpoke.ps1 -ProcId <client> console 'census 0x35'
#    "census: 0x35  object array: ...  |  raw scan: ...  |  census: ..."
#      array NOT HELD + raw scan present -> a ghost the gate should have caught (gate bug)
#      array NOT HELD + raw scan absent  -> the id never came from this instance at all;
#                                           the search moves to the wire, not to memory
#      array held                        -> the agent and the plugin disagree; go to check 3

# 3. THE PLUGIN'S OWN VERDICT on the same id, through the engine, on the game thread.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'slay #35'
#    "no object 0x35 in this module" = the plugin cannot resolve it either (agree)
#    a kill, or any other refusal   = the plugin CAN resolve it (disagree with check 2)

# 4. The end-to-end result, once 1-3 agree.
pwsh tools\blockpoke.ps1 -ProcId <client> console 'slay'
#    client: "queued a killing blow for the owner, keyed by owner id 0xNN"
#    host plugin log: "combat: applied 100000 of a peer's damage to 0xNN"
#                     and NOT "which does not resolve here"
```
resolve: 0x35: ... array(+0x1005C) 0x33EE540 array(engine) 0x33EE540
   | engine 0x19169BB8 walk 0x19169BB8 resolve 0x19169BB8 | at 0x19169BB8: vtable 0x7470C8 id+4 0x35 ...
```

One array, and the engine's call, the transcription and `ResolveServerObject` all agree on a
live pointer — while `slay 0x35` and the peer-damage pass, in the same process, refuse the
same id. Also settled: **`+0x14` is the third word of the object's `last_name`** (the
constructor at 0x004CFCB0 builds `CExoLocString` at `+0x0C` and `tag` at `+0x18`), so
`0x130014` vs `0x0` means "this one has a localised name" and nothing more. And **every call
site provably reaches the generic `CServerExoApp::GetGameObject`** — the type-filtered getters
appear nowhere in the codebase. See `docs/research/enemy-presence-sync.md` §"Round four".

What is left is either a TRANSIENT object (the three commands looked at different moments) or
something in the integration tree between the lookup and the caller. Round four makes every
refusal print its own working, so one run separates them.

**The live check, round four.** Run 1 and 2 back to back, in that order, within a few seconds.

```powershell
# 1. THE FAILING CALL, WHICH NOW EXPLAINS ITSELF. Read the plugin log afterwards:
pwsh tools\blockpoke.ps1 -ProcId <host> console 'slay 0x35'
#    <gamedir>\kotorcoop-plugin.log now carries, from INSIDE the refusal:
#      "cheat: refusing target - 0x35: ... | engine 0x.. walk 0x.. resolve 0x.. | at 0x..: ..."
#    engine/resolve non-zero here => the lookup worked and the refusal came from ABOVE it
#                                    (paste the merged CheatController.TryResolveTarget)
#    engine/resolve zero here     => the object really was absent at that instant: it is
#                                    transient, and rounds 1-3 compared different moments

# 2. THE SAME QUESTION, SECONDS LATER, from the verb that succeeded before.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'resolve 0x35'
#    Both lines are timestamped in the log; if they disagree, the object is transient and
#    the timestamps bound how fast it comes and goes.

# 3. THE CONTROL, and it matters as much as the failing case. 0x1A resolves and used to be
#    killable, so if THIS refuses too, the slay path regressed generally in the merge and
#    0x35 was never special.
pwsh tools\blockpoke.ps1 -ProcId <host> console 'slay 0x1A'

# 4. The debt's own verdict, which now names which kind of unresolvable it hit.
#    In the host plugin log, after the client forwards a kill:
pwsh tools\blockpoke.ps1 -ProcId <client> console 'slay'
#    "combat: a peer owes N damage to 0xNN, which does not resolve here ANY MORE - it did
#     earlier this epoch, so the body was destroyed"      -> a race; the debt is held
#    "... and never has this epoch - the peer keyed it in an id space this instance does not
#     share; the debt can never land"                     -> the census defect

# 5. Everything above lands in the COMBINED plugin log (the split channel files do not carry
#    the 'resolve:'/'cheat:' prefixes). One grep collects the whole round, timestamps and all:
Select-String -Path '<gamedir>\kotorcoop-plugin.log' `
    -Pattern 'resolve:|cheat: refusing|combat: a peer owes|combat: applied'
```

⚠ If check 1 shows the engine resolving the id while the cheat refused it, please paste the
integration tree's `CheatController.TryResolveTarget` — this branch's copy calls
`Odyssey.ResolveServerObject` and nothing else, and two other agents' cheats now live in that
same file.

⚠ Cheat kind for `resolve` is **9** in this branch now (7 and 8 belong to StageMovie and
SetItem), so the merge should no longer need renumbering; `CheatKindSyncTests` fails the build
if the two halves ever disagree or if two verbs share a number.

### Still unverified — item identity apply

`drop <resref>` replicates and correctly records no identity. The by-object-id harness path
still cannot exercise the apply: the ground body genuinely IS object `0x131`
(`item slot 0: spawned item 0x10001 as object 0x131`) and `drop #131` answers
`object 0x131 is not a readable item here` — so `TryReadItemIdentity` cannot read a
ground-spawned item body. The refusal is now VISIBLE, which is the part that was broken; the
read path needs its own look, ideally against an inventory item with real charges/upgrades
rather than a freshly spawned stock one.

### Ready to run, never run — a transition WITH a movie

The pre-store-flush hypothesis is still open, and it now has an instrument instead of needing
a human to play the escape-pod conversation. `moviewarp` stages a cinematic onto a module
transition through the engine's own `move_to_module_movies` queue; the full procedure, the
PASS/FAIL lines and the baked-phantom check are `docs/research/cinematics.md` §7.

Two runs, and **only the second one tests the hypothesis** (§7.4 — the first is
plugin-initiated, so the flush has already finished before the movie starts):

```powershell
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp tar_m03aa 01c'   # run A: the lever
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp queue 01c'       # run B: then a DOOR
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp clear'           # always, afterwards
```

`01c` is 6.4 MB and present in both installs (61 identical `.bik` each, checked), so a stall
under ~6 s means no movie played — a bare module load already measured 5.1-5.3 s here.
**Diagnosed and fixed on 2026-08-15 (block 41); see the next section for the run to make.**
The read path was not failing to read a ground item — it was never asked to. `ItemResref` is
a cache lookup that cannot populate itself, `TryReadItemIdentity` was the only thing that
could rebuild the cache, and the two were joined by `||` with the cache lookup first. Full
write-up in `docs/research/item-identity.md` §8.

## Item identity, end to end — the unattended run (block 41)

**The WRITER is PROVEN LIVE (2026-08-15).** `setitem 0x131 stack=20 charges=3 max=50
upgrades=0x5` answered `0x131: stk 20 chg 3/50 upg 5 bits 18`, the plugin log read back
`stack 1->20, charges 50/1768092->3/50, upgrades 0x0->0x5`, and an external
`item 0x131` agreed: `stack 20, charges 3/50, upgrades 0x5`. So `Odyssey.ApplyItemIdentity`
genuinely writes all five fields into a live `CSWSItem` and they read back — the thing that
had never once been observed in this feature's life. **The wire half is still NOT RUN.**

Everything below goes through `pwsh tools/blockpoke.ps1 -ProcId <pid> console '<line>'`, which
is the only channel that reaches a co-op verb without a keyboard. **The reply field is 96
bytes and holds ONE line**, so every reply is a summary and the substance is in
`%TEMP%\kotorcoop-demo\agent1.log` / `agent2.log` — the dispatcher writes there now, which it
did not on the first attempt (that is why `items` looked empty).

**Ids are hex: `0x131` or `#131`. A bare `131` is refused on purpose** — it is also a legal
resref, and reading it as decimal would name object `0x83` silently.

⚠ **Type the resref carefully, and check `items` afterwards.** `CreateItemOnFloor` 0x00547F60
calls `LoadFromTemplate` and **never tests the result** (0x0054806B), so a blueprint that is
not installed still yields a live object id — an empty body with no tag, 50 charges and an
uninitialised MaxCharges. That is what `g_w_blstrpstl01` produced on the first attempt; the
real resrefs end `001`..`010`. Both spawn paths now say so out loud, and `items` prints
`<NO NAME - probably a bad blueprint>` for such a body.

Verified to exist in the GOG install: `g_w_blstrpstl001`, `g_w_lghtsbr01`, `g_i_medeqpmnt01`,
`g_w_fraggren01`, `g_i_progspike01`, `g_i_drdshld001` (the last is one of only 26 blueprints
that ship with real charges — `Charges 10`).

```powershell
# The two game pids and the two game directories, the same way every other section gets them.
$pid1, $pid2 = (Get-Process swkotor | Sort-Object StartTime).Id
$Game1 = 'C:\GOG Games\Star Wars KOTOR (GOG)'   # demo.ps1's defaults
$Game2 = 'D:\Games\swkotor-gog-p2'
$log1  = "$env:TEMP\kotorcoop-demo\agent1.log"
$log2  = "$env:TEMP\kotorcoop-demo\agent2.log"

# 0. Both halves must be the NEW build. build.ps1 -Install, then confirm the version:
pwsh tools\blockpoke.ps1 -ProcId $pid1 state      # expect "version 41"

# 1. On the HOST: find an item worth sending. Most interesting first; the sweep reports
#    itself on the first line, so a short list cannot be mistaken for a truncated one.
#    THE ROWS ARE IN THE AGENT LOG, not in the reply — the reply is the closing summary.
pwsh tools\blockpoke.ps1 -ProcId $pid1 console 'items 40'
Select-String -Path $log1 -Pattern 'items:|^\s+0x' | Select-Object -Last 45
#    -> items: 59 item bodies, 58 with a readable tag
#    ->   0xB4  g_w_fraggren01  yours  stack 1  charges 0/0  upgrades 0x0
#    Pick one that is "yours" and has a real name. If nothing is, `giveitem` at the in-game
#    console (a retail verb, typed by hand) will put one in the pack.

# 2. Make it genuinely non-stock. This writes through Odyssey.ApplyItemIdentity — the SAME
#    function an arriving item goes through — and answers with the fields read BACK.
pwsh tools\blockpoke.ps1 -ProcId $pid1 console 'setitem 0xb4 stack=20 charges=3 max=50 upgrades=0x5'
#    -> reply: 0xB4: stk 20 chg 3/50 upg 5 bits 18        <- PROVEN on 2026-08-15
#    -> host plugin log: setitem 0xB4: stack 1->20, charges 0/0->3/50, upgrades 0x0->0x5, ...

# 3. Replicate it. Both instances spawn a body from the item slot, so BOTH plugin logs
#    should show the apply — the host is not a special case here.
pwsh tools\blockpoke.ps1 -ProcId $pid1 console 'dropitem 0xb4'
#    -> dropped g_w_fraggren01 as item 0x10001 (stack 20, charges 3/50, upgrades 0x5, flags 0x3)
#    ⚠ If this refuses with "its tag is unreadable", the item is an empty body from a bad
#      blueprint (see the warning above) — pick a different one from step 1.

# 4. Read both plugin logs for the marker. The success line reports what the item NOW HOLDS,
#    not what was asked for; the two halves of it must agree.
Select-String -Path "$Game1\kotorcoop-plugin.log","$Game2\kotorcoop-plugin.log" -Pattern 'identity (applied|SKIPPED)|NO TAG' | Select-Object -Last 6
#    -> item slot 0: identity applied to 0x132 — asked for (stack 20, charges 3/50,
#       upgrades 0x5, flags 0x3), item now reads (stack 20, charges 3/50, upgrades 0x5, bits 0x18)
#    -> "identity SKIPPED for 0xID — <reason>" now names what the resolve saw.

# 5. Read the receiver back from OUTSIDE its plugin, using the object id step 4 named.
pwsh tools\blockpoke.ps1 -ProcId $pid2 console 'item 0x132'
Select-String -Path $log2 -Pattern 'item 0x' | Select-Object -Last 2
#    -> item 0x132 g_w_fraggren01: stack 20, chg 3/50, upg 0x5, identified

# 6. Re-send the receiver's own copy back. This is the loop closing on itself, and it is the
#    case that could not work before: a body the plugin spawned has no Tag, so the resref now
#    comes from the item slot we wrote it into rather than from the item.
pwsh tools\blockpoke.ps1 -ProcId $pid2 console 'dropitem 0x132'
```

Three independent witnesses have to agree at step 5: the sender's `setitem` read-back, the
receiver's plugin log read-back, and the receiver's external `item`. Two of them share no
code and the third is in a different process.

**The one thing this run cannot prove.** `setitem upgrades=0x5` writes the mask; whether the
engine then *renders and applies* two upgrades from that mask alone is
`docs/research/item-identity.md` §3's inference from `HasUpgrade`'s consumer. Confirming it
needs a human to look at the receiving instance's item description and see the upgrade slots
filled — one screenshot, and the only step in this whole feature that still wants eyes.

## Three open items closed live (2026-08-15, third pass)

### Client `slay` — WORKS, and the hunt was chasing an aftermath

The forwarded kill had been landing; what looked like failure was the corpse. The decisive
log, host side:

```
combat: applied 200 of a peer's damage to 0x35 (owed 100000)   x3
combat: a peer owes 200 damage to 0x35, which does not resolve here ANY MORE
        - it did earlier this epoch, so the body was destroyed; holding the debt
```

and on the client, through the engine's own death path:

```
combat: killed 0x35 on the owner's authority     <- the client's forwarded slay
combat: killed 0x1A on the owner's authority     <- the host's own slay, mirrored down
combat: killed 0x17 on the owner's authority     <- a second forwarded slay, fresh target
```

The owner census fell 25 -> 23 as they died. Chain confirmed: client forwards a debt keyed
in owner space -> host spends it as REAL engine damage -> the enemy dies with corpse, loot
and OnDeath -> the death mirrors back.

**What actually cost three rounds was a diagnostic that could not tell two states apart.**
"Does not resolve here" was emitted both for *never resolved* (the real defect: an id keyed
in a space this instance does not share) and for *resolved earlier, then the body was
destroyed* (success, one tick later). Splitting that one sentence in two ended the hunt
immediately. Three separate measurements — `census`, `resolve`, `slay` — were also taken
seconds and processes apart, so each was reading a different world; `resolve` answering both
lookups in ONE frame is what made them comparable.

Everything else on the way was real and stays fixed: the census liveness gate (a freed
creature block keeps its vtable and id, so a ghost reads as a creature until the memory is
reused), ids parsed as hex everywhere (`slay #35` used to kill `0x23`), and diagnostics built
to fit the 96-byte reply channel with the long form in the log.

### Item identity — WORKS end to end

```
setitem 0x34 stack=20 charges=3 max=50 upgrades=0x5   ->  0x34: stk 20 chg 3/50 upg 5
dropitem 0x34                                         ->  dropped g_w_blstrrfl001 as item 0x10002
receiver plugin: item slot 1: identity applied to 0x132 - asked for (stack 20, charges 3/50,
                 upgrades 0x5, flags 0x3), item now reads (stack 20, charges 3/50, upgrades 0x5)
receiver, read externally: item 0x132 g_w_blstrrfl001: stack 20, chg 3/50, upg 0x5, identified
sender,   read externally: item 0x132 g_w_blstrrfl001: stack 20, chg 3/50, upg 0x5, identified
```

Three independent witnesses agree, so the README bullet retires on evidence.

⚠ **The previous round's failure was a typo of mine, and the reason it cost a round is now a
guard.** `CreateItemOnFloor 0x00547F60` calls `LoadFromTemplate` and **never tests the
result**, so `g_w_blstrpstl01` (the real blueprint is `...001`) produced a live, addressable,
perfectly valid `CSWSItem` carrying only constructor defaults — and every "did it spawn?"
check reported success. That body is the whole explanation for `charges 50/1768092`: the
constructor writes `Charges = 50` and never writes MaxCharges at all. The offset was right;
the memory had never been written. The spawn now says so:
`spawned 0x131 but it has NO TAG and 50 charges - the blueprint almost certainly does not
exist in this install`.

Still needing one human look: whether the engine RENDERS two upgrades from the mask alone
(the receiving item's description).

### Cinematics — the instrument works; the hypothesis is still open

`moviewarp tar_m03aa 01c` staged and played a real movie on the co-op warp path:

```
cinematic: armed 01c for the next co-op warp
flush, before the module is stored: destroyed phantom 0x12D (slot 0)
cinematic: staged 1 of 1 movie(s) onto this transition: 01c
travel: moving to module 'tar_m03aa' on the session's authority
cinematic: the main thread was gone for 15531 ms
```

**15.5 s against 5.1-5.3 s for a bare module load, so the movie genuinely played** — and the
mod is unreachable for every one of those seconds. `AddMoveToModuleMovie` is correctly wired
and the co-op warp path carries a queued movie, which is the design lever `cinematics.md`
proposed, now confirmed on the running game.

The flush ran BEFORE the stall and the baked-phantom check was clean (two `coop_phantom` rows
but one distinct id, for one peer). **This does not settle the hypothesis**: on this path the
plugin initiates the transition and has already destroyed spawned bodies before raising the
pending flag, so the flush branch under test never runs. That needs `moviewarp queue` plus an
ENGINE-initiated transition (a door), per `cinematics.md` §7.4 — still outstanding.

### Run B is now stageable — `usedoor` (2026-08-15, later)

A KOTOR transition door is **used, not walked into**, and `end_m01aa` has no area-transition
trigger at all, so there was no way to make the engine start a transition unattended. `usedoor`
sends the engine's own click — `CSWCMessage::SendPlayerToServerInput_UseObject 0x00677D10`,
whose server handler queues `AddUseObjectAction` on the PC, whose action posts the event
`CSWSDoor::EventHandler` turns into a transition.

Why only that opens the window: a door transition and a co-op warp end in the *same* three
`SetMoveToModule*` calls (0x0058C185), but the door does them in a SECOND phase. The first
phase — on use — raises `CSWSArea::SetTransitionPending(1, id)` and starts a 0.5 s fade
(0x0058C230), and `trans_pending` at CSWSArea+0x2C4 is the exact byte `IsAreaTransitionPending`
reads. That half-second IS the pre-store flush window; a co-op warp never opens it.

**First attempt, live: the verb worked and the party gate stopped it.** `usedoor end_door10`
from 101.7 m was refused with the distance (good). After `tp 47.4 116 -0.04` it reported
`door: using door 0x4A at 4.1m - it links to module 'end_m01ab' at waypoint 'from_01a'` and then
warned that the party was not together — and it was right: Trask was still at the module start,
`CSWSDoor::EventHandler` cancelled the transition into `k_trg_transfail`, and there was no
`area transition pending:` line and no flush at all.

**So `usedoor` now gathers the party first**, through `CSWPartyTable::PositionParty 0x00565B00`
— the engine's own party collection, whose only caller is `MovePlayerToArea`, i.e. the very
thing a module transition does. It is skipped entirely when the party is already together, and
`CSWPartyTable+0x10C` (a one-shot "skip positioning" flag tested at 0x00565B6E) is read first so
a set flag cannot make the first call silently move nobody.

```powershell
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp queue 01c'        # arm, do not warp
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'dropitem g_w_blstrrfl001'   # something to flush
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'tp 47.4 116 -0.04'          # PC beside the door
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'usedoor end_door10'         # gathers, then transitions
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp clear'            # always, afterwards
```

`area transition pending: flushing spawned objects` is the line that makes it run B — it
cannot appear on a co-op warp at all. Whether the `flush,` lines land before that run's stall,
after it, or not at all is the entire answer; the three readings are `cinematics.md` §8.5, and
the baked-phantom check for the departed module is §8.6.

### ⚠ Two verification gaps found the hard way (2026-08-15)

Both cost time this session and neither is about the game.

* **`dotnet build` at the repo root does not compile the plugin.** `KotorCoop.slnx` lists five
  projects and the test project; `KotorCoop.Plugin` is deliberately absent because it is
  NativeAOT win-x86 and cannot be referenced. So a "Build succeeded" at the root says nothing
  about plugin code, and the test project only compiles the handful of dependency-free plugin
  files it links in. **`pwsh src\KotorCoop.Plugin\build.ps1` is the only compiler that ever
  sees the rest**, and it caught a plain `CS0103` this round that the root build had passed
  over. Run it before claiming a plugin change builds.
* **Grepping the built `dinput8.dll` for a log string is not a reliable check.** NativeAOT does
  not store every literal where a naive UTF-16 scan of the file finds it: control strings that
  have shipped for weeks (`god was already ON`, `no target given`) read as absent by exactly
  the same method that "confirms" others. It is useful as positive evidence and worthless as
  negative evidence — a missing hit means nothing. The compiler is the gate.

### ⚠ …and a closed door is a wall (the second half of the same problem)

The first walk-through run got further than anything before it: the walk was queued, the PC
**moved** — (47.4, 116.0) → (46.6, 118.4) — and stopped **1.8 m short** of the door at
(47.4172, 120.126), nowhere near the 2.5 m-past target. Moving and stopping short is a
different failure from not moving, and the reading is physical: `end_door10` was **shut**, and
a shut door is a pathing obstacle, so the path ended at its face.

The engine agrees: `CSWSDoor::MoveToNextOpenState 0x00589680` commits pending → current and
then calls **`CSWSArea::BudgeCreatures 0x00508180`** with the door's position and its new
open/closed bit — the update that stops the area treating the door as solid. (Which is also
why writing +0x2CC directly is forbidden: the byte moves the collision without telling the
area.)

So the real sequence is **both events in order — open it, then cross it** — and `usedoor` now
does both. It opens through the engine's own **`CSWSDoor::OpenDoor 0x00589C70`** (`ret 4`,
argument = the opener's object id), which picks the swing side from
`dot(opener − door, orientation@+0x9C)`, records the opener, opens the LINKED twin so double
doors move together, and runs the OnOpen script — none of which a state poke reproduces — then
commits the swing immediately so the walkmesh is open on this frame, then walks.

Two more fixes in the same pass:

* **The walk direction now comes from the door's own facing**, not the approach vector, with
  the far side chosen by the same sign test `OpenDoor` uses. A doorway has one axis you can
  cross it on; an approach overshoot aims at the wall beside the opening whenever you arrive at
  an angle. They agreed only because the teleport to (47.4, 116) was due south of a door
  bearing −1.57079.
* **The outcome self-reports.** Ten seconds after the walk, and only if no transition happened,
  the verb logs the player's distance to the door, where it ended up and where it was aimed.
  That line exists because its absence cost this round: "walked into a shut door" and
  "mis-aimed" left identical evidence and had to be told apart by hand.

The entry line now also carries the door's **open state and lock state**, so the next run
cannot be ambiguous about what it walked into.

### ⚠ A transition door is WALKED INTO, not used — the premise was backwards

Run B's first real attempt: party gathered (the gather works — a second `usedoor` moments later
raised no gate warning at all), `door: using door 0x4A at 4.1m -> 'end_m01ab'`, console replied
`walking to door 0x4A (1.5m)`, the PC visibly moved 4.1 m → 1.5 m — **and then nothing.** No
`area transition pending:`, no flush, no stall, no conversation, no error.

The binary explains all of it. `CSWSDoor::EventHandler` switches on the event type through the
jump table at `0x0058C594`/`0x0058C5C8`, and **only event `0x1E`** reaches the transition arm
(`0x0058C0BE`). Using an object posts **`0x1A`** — `AIActionUseObject` at `0x0057ECC0` and
`AIActionOpenDoor` at `0x0057D661` both build that — which lands at `0x0058BD62`, the arm that
runs the door's OnUsed script and nothing else.

Event `0x1E` is built in exactly five functions and **every one is movement**:
`HandleSubAreaStateChanges 0x00516020` (×3, and only for party index 0 — `0x00516263`),
`WalkUpdateLocation 0x0051BB10` (×2), `DriveUpdateLocation 0x0051CD20`,
`WalkUpdateLocation_QuickWalk 0x0051D9C0`, `AIActionMoveToPoint 0x0051F4F0` (×2). A module
transition is **a creature crossing into the door's sub-area, noticed by its own movement
update**.

Three consequences, and they close every dead end of the last two passes:

* **`tp` onto the door can never transition** — a `SetPosition` snap runs no movement update, so
  nothing notices the crossing. Every "teleport next to the door and it does nothing" result was
  this.
* **A use/open input can never transition** — it posts `0x1A`. The 4.1 → 1.5 m walk was
  `AIActionUseObject` pathing to the door's *action point*, which stops short of the threshold
  by design.
* **The PC's action queue works.** That same walk proves it. The two no-ops were not one bug
  wearing two hats — they were two different mistakes that looked alike. The session is not
  implicated and the disconnected control run is not needed.

`usedoor` now **walks the player through the door**: `ActionMoveToLocation` to a point 2.5 m
past it along the approach, letting `AIActionMoveToPoint` post the event as the PC crosses.
`MoveToLocation` declines with failure 4 while the script VM is mid-call, so the walk is retried
for up to 120 frames and says so if it gives up. Procedure unchanged otherwise —
`cinematics.md` §8.3 has the full derivation.

### Why `blockpoke walk` no-ops on the player character — settled

It queues NWScript `ActionMoveToLocation`, the AI action queue, which is what it was built and
verified for — on PHANTOMS. **The PC does not move that way in this build.** All 17 call sites
of `CSWCMessage::SendPlayerToServerMessage 0x00677410` were enumerated and the message type read
off each; on the player-input channel (6) this game only ever sends 2 Attack, 3 ChangeDoorState,
7 UseSkill, 8 Dialog, 9 UseItem, 0xB UseObject, 0xC UnlockObject, 0x12 CastSpell. Type **1**,
`WalkToWaypoint`, whose server handler `0x005235B0` is a complete implementation, is **never
sent by anything** — dead code from the networked lineage this engine came from. There is no
"walk the player here" message to borrow, which is exactly why `usedoor` could go through a
message and a walk cannot; the PC is moved by drive control from local input.

The channel used to `return` silently on three of its four paths. It now logs every one, so
"the walk did nothing" can be told from "the walk was never tried". Use `tp` plus `usedoor`
(which gathers the party) instead. **INFERRED, not proven:** whether the queued action is
cancelled by the drive controller or simply never run for a player-controlled creature.

⚠ Also measured: a same-module `warp <module> <waypoint>` **ignores the waypoint** —
`warp end_m01aa end_wp_traskdie` put the party back at the module start (20.2, 23.0).

## Companion control, session teardown and the ghost that looks like a leak (2026-08-15)

**The reported bug — "after disconnecting, Trask is back but clicking him does nothing" — was
never about Trask.** It was the client control lock left armed after teardown. The lock is a
byte patch: a 5-byte `jmp` at `CClientExoAppInternal::ChangeCharacter` (`0x005F6B60`) into a
stub that returns immediately while a flag word is set, and that one funnel is Tab, all three
party portraits, and the death auto-swap. The flag is a LEVEL, rewritten into the stub every
frame, raised once per client session and never lowered — `ClearCoopState()` cleared the peer
count, the presence counts, the dialogue flag, the encounter fields and the command, and not
that word. Verified live:

```
connected:            suppress: gameOver 1  swap 1
after `disconnect`:   suppress: gameOver 0  swap 0      <- the fix
                      command 0,  partystate: soloMode 0, member 0x127 (Trask) present
session: released - undoing what it did to this game
death: suppressions lifted - Tab and the party portraits answer again
interaction: dropped every peer lock - nobody else is holding anything here
party roster: your party is yours again - nothing left to reconcile against
portraits: restored the party widgets (the session ended)
```

Three more session levels leaked identically (game-over suppression, peer interaction locks,
peer party roster), plus mirrored companions left in faction Neutral and pinned min-1-hp.

**Then the phantom.** The peer's body survived teardown — `phantoms 1`, `remotes 0`, stable
across 10,000 frames. Two causes, neither the one I guessed (I assumed the destroy sat behind
the command gate; it does not): a gate on a derived count the branch never recomputed, and —
the real one — **`Odyssey.DestroyObjectById` returns failure 4 while the script VM is mid-call,
and all four spawners cleared their bookkeeping regardless of the outcome.** Teardown made
exactly one attempt on an arbitrary frame, so a single declined call orphaned the body
permanently, with nothing left that remembered it. Destroys are now retried (bounded at 3) and
a write-off names the id and the failure code. Verified live:

```
session released: destroyed phantom 0x12D (slot 0)
session: nothing it spawned was left standing
state -> phantoms 0
```

### ⚠ `tools/creatures.ps1` cannot answer "is this body still alive"

After that clean teardown, `creatures.ps1` **still listed `0x12D coop_phantom`**. It is a raw
sweep for the `CSWSCreature` vtable with no liveness gate, and `operator delete` clears neither
the vtable nor the id — so a destroyed creature keeps reading as one until its memory is
reused. The engine's own object array settles it:

```
resolve: 0x12D: ... array(+0x1005C) 0x345E138  array(engine) 0x345E138
              | engine 0x0  walk 0x0  resolve 0x0
```

Nothing holds it. The body is gone and the row is a ghost — the same artifact behind the
enemy-census hunt, and behind run A's "two `coop_phantom` rows" which were one body seen twice.

**Consequence for the cinematics work: the baked-phantom CLEAN criterion in `cinematics.md`
must not count `creatures.ps1` rows.** Use `console 'resolve <id>'` and believe the object
array, or the check will report a freed block as a baked body. Any BAKED reading taken before
today was also measuring the teardown leak above rather than the transition window.

## Run B: the phantom-baking hypothesis is REFUTED (2026-08-15)

The question `cinematics.md` has carried since it was written — *does a movie playing across a
module transition swallow the pre-store flush window, baking `coop_phantom` bodies permanently
into the stored module?* — is answered. **It does not.**

Staged on the host with a phantom and a dropped rifle standing, `01c` queued and confirmed by
the engine, then an ENGINE-initiated transition through `end_door10`:

```
11:39:47.767  door: 0x4A at 2.9m links to module 'end_m01ab' at waypoint 'from_01a'; open state 2
11:39:48.669  area transition pending: flushing spawned objects before the module is stored
11:39:48.669  flush, before the module is stored: destroyed phantom 0x12D (slot 0)
11:39:48.672  flush, before the module is stored: destroyed item body 0x131 (item 0x10001)
11:39:49.216  cinematic: a movie is playing
11:39:57.355  cinematic: the main thread was gone for 7407 ms
11:39:57      epoch: module changed
where -> module end_m01ab at 49.9 120.3
```

`area transition pending:` is the line that makes this run B — it cannot appear on a co-op
warp, because a plugin-initiated warp never opens phase 1's fade. **The flush lines precede the
stall**, so the window survives the movie and the flush branch is correct as written. The movie
did play: the queue was consumed (`moviewarp show` → `queued: nothing`), `a movie is playing`
was logged, and the stall exceeds the 5.1–5.3 s bare-load baseline.

Baked-phantom check, using the CORRECTED criterion (the object array, not a row count): back in
`end_m01aa` the sweep offers two candidate rows, both id `0x138` — one distinct id for one
connected peer, i.e. the new phantom. The body that existed across the transition, `0x12D`,
`resolves to 0x0`. Nothing was baked.

### What it took to stage, recorded so nobody repeats it

Four wrong premises died on the way, each looking exactly like a bug in the mod:

1. **"A door is used, not walked into."** Backwards. `CSWSDoor::EventHandler` reaches the
   transition arm only on event `0x1E`, which is built exclusively by movement updates — a
   creature CROSSING into the door's sub-area. Using a door posts `0x1A`, which runs OnUsed and
   nothing else. No amount of using a door transitions it.
2. **A `tp` onto the door can never fire it** — a `SetPosition` snap runs no movement update, so
   nothing notices the crossing.
3. **A shut door is a wall.** `MoveToNextOpenState` calls `CSWSArea::BudgeCreatures`, which is
   what stops the area treating the door as solid; the walk ended at its face until the door was
   opened through the engine's own `OpenDoor` (which also picks the swing from which side the
   opener stands, opens the linked twin, and runs OnOpen — none of it reproducible by hand).
4. **Aim along the door's own facing, not the approach vector.** With the PC at (47.4, 116) —
   beside a doorway whose bearing is −1.57079 — the crossing axis is X, the side test is
   degenerate, and the path routed AROUND: the watch line reported the player ending 7.9 m away
   having started at 4.1 m. Repositioning onto the axis at (44.5, 120.1) crossed on the first
   try.

The watch line (`600 frames after the walk the player is N.Nm from 0x<id> (was M.Mm), at X,Y;
aimed at X,Y`) is what turned round four from a guess into a measurement, and it is the reason
this closed instead of continuing.

⚠ Also learned, and unrelated to the game: **`dotnet build` at the repo root does not compile
the plugin** — `KotorCoop.slnx` deliberately omits the NativeAOT win-x86 project, so only
`src/KotorCoop.Plugin/build.ps1` ever type-checks most plugin code. And **grepping a built DLL
for a log string is worthless as negative evidence**: literals that have shipped for weeks read
as absent to the same scan that "confirms" others. Positive evidence only.

### Measured in the same run: the follower does not see the movie

The client half of run B, from its own log:

```
11:39:50.646  flush, before the module is stored: destroyed phantom 0x12D (slot 0)
11:39:50.648  flush, before the module is stored: destroyed item body 0x131 (item 0x10001)
11:39:50.652  travel: moving to module 'end_m01ab' on the session's authority
11:39:50.663  epoch: world unloading; clearing phantom, item, combat and door state
```

It flushed correctly and followed correctly — and there is **no `cinematic:` line anywhere near
the transition**, where the host logged a 7,407 ms stall. So the movie played for one player
and not the other. That is not a regression and not a gate refusing it: the follower arrives
through `TravelController` → `Odyssey.TravelToModule` and **nothing ever writes a movie into
its queue**, so the engine plays an empty queue, which is a plain load.

The receiving half is already proven — run A showed a movie queued by us on that same co-op
travel path plays for real (15,531 ms). What is missing is only the wire: the owner announcing
the names it is about to play, the host relaying them, and the follower's travel command
carrying them. Two things make it its own pass rather than a footnote: it needs a new shared
block region and therefore a version bump on both halves (the travel region `0x920`–`0x96C` is
full), and on the canonical Endar Spire → Taris chain the follower must ACCUMULATE the movie
lists of the transitions it correctly declines (the `stunt*` gate refuses `STUNT_00`) and hand
the whole set — `["01c", "01f"]` — to the warp it finally does issue.

### Built, not yet run — cinematic parity (2026-08-15)

The wire above now exists. Both halves it joins were already measured: the follower's empty
queue (7,407 ms of host stall against no `cinematic:` line at all, above) and the receiving
path (run A, a queued movie playing for 15,531 ms on the co-op warp).

`PacketType.TransitionMovies = 40`, protocol **25**, shared block **42** (`0x2A`), block grown
to `0x3A00`. Packet type 39 is deliberately skipped — `network-topology.md` reserves it on
paper — and two new tests fail the build on a duplicate packet type or a half-bumped block
version, which nothing had ever enforced.

⚠ **The one that would have failed silently**: the engine stores a movie as the resolved path
`.\movies\01c.bik`, not as the name the script gave it. Handing that back to
`AddMoveToModuleMovie` makes it build `MOVIES:.\movies\01c.bik`, fail to open it, and refuse
the movie — so an unstripped forward would have looked exactly like no channel at all. The
names are reduced to resrefs at publication.

Acceptance procedure and the full PASS/FAIL reading: `docs/research/cinematics.md` §9.7. The
short form, with the pair CONNECTED and both in END_M01AA:

```powershell
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp queue 01c'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'dropitem g_w_blstrrfl001'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'tp 44.5 120.1 -0.04'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'usedoor end_door10'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp clear'
```

PASS is symmetric: the FOLLOWER must log `cinematic: the module transition has queued 1
movie(s)` BEFORE its `travel:` line, and both sides must stall well past the 5.1-5.3 s
bare-load baseline. Run the negative control too — with nothing queued, neither side may stall
beyond baseline and `staged N of M` must appear nowhere.

## Rig pass (2026-08-27): seven NEEDS-LIVE items from the same day's builds

Ran the pair (`tools/demo.ps1`, `end_m01aa`, block version 53) through the roadmap's seven
Tier-1 NEEDS-LIVE items in order. Full evidence and per-item detail lives in each feature's own
research doc; this is the session index.

| # | Item | Result | Detail |
| --- | --- | --- | --- |
| 1 | `FlagAway` | **FAILED**, root cause located | `disconnect-reconnect.md` §10 |
| 2 | Persistent connection indicator | Partial — data path correct, `Reconnecting` state unreachable | `connect-flow.md`, 2026-08-27 section |
| 3 | `script_item_sync` | Partial — core grant + 1 of 3 controls confirmed, stays OFF | `dialogue-consequences.md`, 2026-08-27 round |
| 4 | `map_reveal_sync` | **Clean pass, default flipped ON** | `map-reveal.md`, "Live pass" |
| 5 | `pazaak_detect` | Negative check only (no false positives); positive path unrun | `minigames.md`, "Live pass" |
| 6 | Container-loot race | Attempted, blocked (no container with remaining loot in range) | `roadmap-seamless.md` Tier 1 item 4 |
| 7 | `demo3.ps1` (3 instances) | Not run this session (time) | — |

Two new dev-only test primitives were built to unblock items that had no way to reach the
scenario under test without unreliable click/OS input, following the exact pattern `usedoor`/
`skipline`/`moviewarp` already established: `quittomenu` (`CheatKinds.QuitToMenu` = 25) calls
`CClientExoAppInternal::ShutDownToMainMenu` directly, and `scriptgive` (`CheatKinds.ScriptGive` =
26) gives an item into the local player's inventory with no suppression-ledger entry — see
`docs/CONSOLE.md`.

**Verify.ps1, twice.** First against the session as left after items 1-6 (heavily warped/tp'd
through three modules and a main-menu teardown): 38 passed, 11 failed — several failures were a
single root cause (`stats host 0x0 client 0x0`) plausibly caused by that unusual session history.
Re-ran against a **freshly launched, untouched session** immediately after connecting: **41
passed, 8 failed** — squarely inside the documented 40-42/7-9 baseline, and every failing check
(`phantom damageable, not a sink`; `the peer really loses those hit points`; `a client hit costs
the enemy real hit points`; `a downed player is reported to the agent`; `the co-op revive stands
the player back up`; `every engine call signature matches the binary` (171/173); `a container
opened on the client is held and shown open on the host`; `no per-frame spam`) reproduced
identically in character on both runs. No regression from this session's changes.
`dotnet test tests/KotorCoop.Tests`: 1980/1980 green throughout (the one `CoopCommandDispatcherTests`
UDP-port flake noted in CONTRIBUTING.md reproduced once and cleared on a solo rerun).

## Rig pass (2026-08-27, round 2): behaviour-flag negotiation, companion state, party pool, transition de-dup, progression, three-peer smoke

Five new dev-only console verbs were built to reach scenarios no existing primitive could
stage, following the same pattern `quittomenu`/`scriptgive`/`loottake` already established:
`pooladd`/`poolremove <resref> [stack]` (party pool, reusing the existing engine-side
`CheatKinds.PartyPoolAdd/Remove`), `grantxp <id|me> <amount>` (pokes a creature's XP directly),
`equip <id|me> <resref> [slot]` (calls `Odyssey.EquipItemFromTemplate` on any creature), and
`grantgold <amount>` (a pure agent-side field read/write, no plugin round trip — reuses
`KotorGameBridge.TryReadProgression`/`TryWriteProgression` directly). See `docs/CONSOLE.md`.

| # | Item | Result | Detail |
| --- | --- | --- | --- |
| 1 | Behaviour-flag negotiation | **PASS** | `mod-compatibility.md` "Live-verified 2026-08-27" |
| 2 | `companion_xp_sync` | **PASS, default flipped ON** | `companion-state-sync.md` §6 |
| 3 | `companion_gear_sync` | **FAILED — real bug found** (equip lands on the player, not the companion), stays OFF | `companion-state-sync.md` §6 |
| 4 | `party_pool_sync` | **FAILED — two bugs found, one fixed, one open**, stays OFF | `party-pool-sync.md` §7 |
| 5 | Scripted-transition announce (de-dup only) | **PASS** for the de-dup gate on a real door crossing; galaxy-map/escape-pod legs not reached | `transition-smoothness.md` §12 |
| 6 | Pazaak positive | **Not attempted** — no console primitive exists to start a real hand, and building one needs new RE this round did not budget for | `docs/research/minigames.md` §23 unchanged |
| 7 | Progression vs vendor (gold sync) | **PASS**, both directions, first live pass for this channel | `progression-sync.md` "Live-verified 2026-08-27" |
| 8 | `demo3.ps1` three-peer smoke | **Attempted, blocked** — instance 3's menu-input auto-load failed across 4 full relaunch cycles (20 retries) in this session; stopped rather than exhaust the fifth. Not a code regression: the same class of input-injection fragility this repo already documents for two-instance sessions, apparently worse for a third. Not re-attempted. | — |

Two real, previously-unknown bugs were found and one was fixed along the way, neither
specific to the item that exposed it:

- **`KotorGameBridge.Items.cs`'s item-tag cache permanently blacklisted a missed lookup**
  instead of retrying it on the next scheduled rebuild as its own doc comment claimed — found
  while testing `companion_gear_sync` (a freshly-created item's id missed once, before the
  periodic scan caught it, and was then stranded for the rest of the session). **Fixed** —
  see the commit removing `_itemTagMisses`. This affects every equipped-gear read in the
  codebase (the PC's own equipment channel, the phantom equipment channel, and companion gear
  alike), not just the companion path that exposed it.
- **`PartyPoolPump`'s landed-apply resolution ran before its own local-action detection**,
  letting the pump misdetect its own just-landed automatic apply as a fresh local player
  action and republish it — every party-pool deposit/withdrawal was silently doubled.
  **Fixed** for the receiving-side case (confirmed live, re-tested clean); a second, distinct
  doubling on the depositing side remains open (see `party-pool-sync.md` §7).

`dotnet test tests/KotorCoop.Tests`: 2114/2114 green after every code change in this round
(the plugin build — `src/KotorCoop.Plugin/build.ps1 -WarnAsError` — also clean throughout;
`SharedBlock.Version` stayed at 54, no shared-block layout change was needed for any of the
five new verbs). The one `CoopCommandDispatcherTests` UDP-port flake (documented in
CONTRIBUTING.md) reproduced once, during a test run launched in parallel with a rig relaunch,
and cleared on a solo rerun of that class alone.

## 2026-08-28 — `demo3.ps1` three-peer: item 8's own auto-load failure was a stale plugin
## build in the third game directory, not the code

Item 8 above (2026-08-27) reported instance 3's menu auto-load failing across 4 full
relaunch cycles and being abandoned. This round picked that up directly. Before touching
`demo3.ps1` itself, a quick check of the third game directory's own `dinput8.dll` found it
dated **2026-08-07** (1,319,936 bytes) against the other two installs' freshly-built
**2026-08-28** copy (2,019,840 bytes) — game directory 3 (`D:\Games\swkotor-gog-p3`, created
once by an earlier `-CreateGame3` robocopy and never touched again) had simply never had the
plugin reinstalled since, so instance 3 was launching with three weeks of this project's own
fixes missing, including whatever earlier changes made the two-instance menu-load path
reliable in the first place.

**Fix: `src/KotorCoop.Plugin/build.ps1 -WarnAsError -Install -GameDir 'D:\Games\swkotor-gog-p3'`**
— nothing else. Reset all three `coop.ini`s to `profiles/coop.ini.example`, cleared all three
plugin logs, then `pwsh tools\demo3.ps1` end to end with no retries logged for any instance —
all three came up first try. `pwsh tools\verify3.ps1` immediately after: **34/34 passed**
(three agents, two remote peers on every instance with distinct ids, two phantoms alive on
every instance, hit tables agreeing, no faults logged, no crash in the event log, all three
plugins driving). `say hello from three-peer smoke test` on the host reached both peers'
chat overlays (confirmed via both `agent2.log`/`agent3.log`, `* Lanik: hello from
three-peer smoke test`).

**The door-transition leg of the smoke test hit a different, pre-existing, non-regression
limitation, not a bug**: `warp end_m01ab` moved all three together cleanly (`where` on every
instance agreed on the new module and position), but `end_m01ab`'s own native `OnEnter`
trigger starts a forced, non-forced-conversation-flag NWScript dialogue
(`dialogue: engine reached StartDialog (1 so far, forced=False)`) — the vanilla
Trask-introduction scene — which held the main thread for the rest of this round's
observation window (`WARNING: frame hook installed but has not fired for ~2s`, repeating;
`skipline`/`selectreply` both refused with *"the game never answered that... cheats run on
the game's frame"*, since the frame hook genuinely was not ticking). This is the same
"needs a person playing" class of gap `docs/research/minigames.md` §9.3/§10 and this
project's own repeated prior findings already document for interactive dialogue content —
not new, and not specific to a third peer. **No crash**: all three `swkotor.exe` and all
three `KotorCoop.Agent.exe` processes stayed alive and were confirmed via
`Get-Process`/the Windows event log throughout, including after the stuck window persisted
for several minutes; two of the three windows simply stopped pumping Windows messages
(`Responding=False`) while the native dialogue held the thread, exactly as the plugin's own
log line predicts. `demo3.ps1 -Stop` tore everything down cleanly afterward.

**Verdict**: item 8's "menu auto-load failing" finding is closed — it was a stale binary in
one of the three game directories, not a code defect, and not worth a code change. The
3-minute door-transition smoke test could not be completed to a fresh, cutscene-free module
this round (every warp target tried either needed the same stuck-dialogue workaround or was
not attempted for time) — the three-peer session itself (connect, chat, phantom mirroring,
one clean same-frame module transition) is confirmed healthy; a longer soak with a
cutscene-free warp target is the concrete next step for whoever needs the full 3-minute
figure. `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1
-WarnAsError`: both clean (no source changed this round — a rig/install-hygiene fix only).

## Rig pass (2026-08-28): companion gear sync re-test — body slot confirmed, weapon slot's
## own new bug found

Full item queue: the 16-character resref fix, mid-session gear propagation, a
`companion_local_combat` release check, and `verify.ps1`/full test suite/native build against
the baseline. Both installs freshly `build.ps1 -Install -WarnAsError`ed (hash-confirmed
byte-identical deploys), both `coop.ini`s reset from `profiles/coop.ini.example` with
`companion_gear_sync=1` for this round.

| # | Item | Result | Detail |
| --- | --- | --- | --- |
| 1 | 16-char resref (`equip`/`scriptgive`) | **PASS**, both directions | `companion-state-sync.md` §10.1 |
| 2 | Mid-session gear propagation | **Body slot PASS** (925 ms / 235 ms latency); **weapon slot BLOCKED** by a newly-found, unrelated engine-call bug (`EquipItemFn`'s return value is never checked; the raw `CSWInventory` weapon slot does not update past the first equip call) | `companion-state-sync.md` §10.2 |
| 3 | `companion_local_combat` release with re-equipped gear | **Not completed** — the companion follow-AI's pre-existing terminal stall kept Trask from ever reaching the staged fight; the player-side exclusion mechanism itself worked correctly | `companion-state-sync.md` §10.3 |
| 4 | `verify.ps1` baseline / full suite / native build | **PASS** — 41/8 verify.ps1 result matches the documented baseline exactly (same specific checks fail); `dotnet test` 2352/2352; both native builds clean | `companion-state-sync.md` §10.4 |

`companion_gear_sync` stays OFF — see `companion-state-sync.md` §10.5 for the verdict and the
concrete next step (capture `EquipItemFn`'s real return value on a weapon-slot call that
reports success but does not land).

## Rig pass (2026-08-28): `store_sync` — three narrow bugs found and fixed, a fourth critical
## defect found and left open

Task: run `store-sync.md`'s own queue item 1 (`warp tar_m02aa` both, `openstore Larrim_Store`,
`buy`/`sell` a finite item, confirm the client's copy reconciles, no double gold charge, an
infinite-stock buy replicates nothing), flip `store_sync` ON if clean.

**Not flipped ON.** Full detail in `docs/research/store-sync.md`'s own 2026-08-28 "Live pass"
section. Summary:

| # | Bug | Status |
| --- | --- | --- |
| 1 | `Odyssey.Store.cs`'s `ResolveStoreRepo` returned the `item_list` FIELD's address, not the pointer VALUE it holds — every store repo consumer (insert/remove/buy/sell) silently failed to find any matching stock | **FIXED** |
| 2 | `KotorGameBridge.Store.cs`'s `TryReadStoreRepo` made the identical mistake, independently, on the agent-side reader `StorePump` depends on | **FIXED** |
| 3 | `KotorGameBridge.Store.cs`'s `StoreGuiObjectId` read panel `+0x227C` (the PC's id) instead of `+0x2278` (the store's id) — a stale mirror of a field this doc's own earlier "Root cause and fix" pass had already corrected on the PLUGIN side but never carried over to the agent side | **FIXED** |
| 4 | With 1-3 fixed, `StorePump` finally ran end to end — and immediately wiped a store's ENTIRE stock on both instances the moment each opened it, with NO buy/sell ever issued (a virgin store's local baseline is diffed against an empty wire-side "desired" total, and "nobody has told me" reads as "should be zero") | **OPEN, CRITICAL** — see store-sync.md for why the obvious fix is not safe to rush |

Also found live: `tar_m02aa`'s ambient "duros" NPC drags a nearby player into a
conversation → Taris swoop-registration → native party-select-screen cascade (blocks
`openstore`'s modal-screen gate); and a `tp <x> <y> 0` with an explicit `z=0` landed a player
below the walkmesh, producing a "party is down" fall-death and a crash on the next warp while
downed — a known-poison transition (`tp`'s own doc string: "no walkmesh check"), not a
`store_sync` defect. Both recorded in `store-sync.md`'s own live pass so the next round does
not rediscover them.

`profiles/coop.ini.example`'s `store_sync=0` default is unchanged. `dotnet build
KotorCoop.slnx -warnaserror`, `src/KotorCoop.Plugin/build.ps1 -WarnAsError`, `dotnet test`
(2355/2355): all clean after each of the three fixes.

## Rig pass (2026-08-28): `solo_segment_spectate` — `startdialog` reaches the Bastila
## conversation for the first time, roster wipe still not observed

Task: `solo_segment_spectate=1` both, `warp sta_m45ac` both, trigger `k_sta_bastila.dlg` via
`startdialog sta_invis_bast_conv k_sta_bastila`, confirm the roster wipe/phantom hide/input
lock/camera follow/toast, flip ON if clean.

**Not flipped ON.** Full detail in `docs/research/star-forge-finale.md`'s own 2026-08-28 live
pass. The tag form refused every time (`sta_invis_bast_conv` turned out to be a PLACEABLE,
object id `0xAA` — confirmed live via `useplaceable` — and `startdialog`'s NPC resolution is
creature-only); `startdialog 0xAA k_sta_bastila` (by id) DID work, reaching a real
`StartDialog` twice and advancing through several `selectreply`-driven nodes (as far as node
61/83 one attempt, node 52/83 responsive on the other) — the first time any live pass on this
doc's history has gotten the conversation running at all. `partystate.ps1`, polled after every
advance across 15+ tries, never showed a roster drop (`companions 1` throughout on the host).
Which node calls `k_psta_door.ncs` (the script string-found inside the `.dlg` in a prior round)
remains unresolved — this pass's blind "always pick reply slot 0" has no visibility into what
each reply actually says. Concrete next step recorded in `star-forge-finale.md`: a `.dlg`-node
script-walker, or live-capturing each node's reply text via the `CSWGuiDialog` panel fields
`dialogue-sync.md` already documents. No crash; both processes stayed healthy throughout.
`solo_segment_spectate` stays at its default OFF.

## Rig pass (2026-08-28): behaviour-override plugin bridge — `map_reveal_sync`/`effect_sync`

## both PASS

Task: `docs/research/mod-compatibility.md`'s ⚠ NEEDS-LIVE item 3. Set `map_reveal_sync=0`
and `effect_sync=0` on the CLIENT only (host stays default `1` for both). Confirm the client's
plugin log shows the adoption line and the behaviour genuinely changes at runtime.

**Both PASS.** Client log, immediately on connect:
`behaviour: adopted host's effect_sync=1 (local coop.ini says 0)` and
`behaviour: adopted host's map_reveal_sync=1 (local coop.ini says 0)`. Behaviour, not just the
log line: moving the host produced a matching fresh `map reveal: slot 0 revealed at ...` on
the CLIENT tracking the host's new position; `applyeffect 1` on the host produced
`effect: applied kind 1 from peer 0x1` on the client. No crash. Full detail in
`docs/research/mod-compatibility.md`'s 2026-08-28 "Live-verified" section.
`trigger_relay`/`liveness_sync`/`dialogue_recruit_sync` (the other three of the five bridged
keys) remain untested live.

## Rig pass (2026-08-28): `party_pool_sync` host-side dup re-test — the duplicate never

## recurred, but a NEW, opposite defect found (rapid same-resref deposits under-count)

Task: `docs/research/party-pool-sync.md` §10's own next step — re-run the `pooladd` reproduction with
`party_pool_sync=1` live, watching for a duplicate `CheatKinds.PartyPoolAdd`. `pooladd
g_w_thermldet01` x3 on the host, x3 on the client (isolated from `script_item_sync`/
`dialogue_item_sync` the same way the `store_sync` round was, after the first attempt showed
identical cross-channel contamination).

**Not flipped ON.** The specific duplicate-apply symptom did not recur (a fifth negative
result). But neither side converged to +6: the HOST ended with exactly 3 (its own 3 local
calls; zero of the client's 3 ever arrived), the CLIENT ended with 4 (1 relayed from the host
plus its own 3 local calls' worth of real local effect, but only that one relay was ever
detected/published — none of the client's own 3 were). Root cause, read from
`PartyPoolPump.cs`: the SAME `_pendingApplyTarget` exclusion mechanism §10's own "root cause
2" fix added to stop a transient scan glitch from being misread as a real change also excludes
a SECOND, genuinely real, rapid local change to the same key — both a bounded 2-second grace
window (step 1) and an unbounded lockout when a landed relay's own pending entry never
re-matches because a further local deposit keeps moving the engine total (step 2, no expiry at
all). Full root-cause detail and the concrete fix direction (track a covered QUANTITY per key,
not a single target value) in `docs/research/party-pool-sync.md`'s new §11. No crash, both
processes healthy throughout. `party_pool_sync` stays at its default OFF.

## Rig pass (2026-08-28): item 5 — `verify.ps1` baseline, full test suite, native build

Task: `verify.ps1` against a fresh session, compared to the documented 40-42/7-9 baseline;
full `dotnet test`; both native builds.

**`verify.ps1`, against a freshly-connected, untouched session** (`coop.ini` reset to
`profiles/coop.ini.example` defaults on both installs): **41 passed, 8 failed.** Seven of the
eight failures match the long-standing documented baseline exactly (`phantom damageable, not a
sink`; `the peer really loses those hit points`; `a downed player is reported to the agent`;
`the co-op revive stands the player back up`; `every engine call signature matches the binary`
(187/189); `a container opened on the client is held and shown open on the host`; `no
per-frame spam`). One substitution from the baseline set: `a client hit costs the enemy real
hit points` (normally a baseline failure) PASSED this run, while `an item picked up here
vanishes on the peer` (normally passing) FAILED. A second `verify.ps1` run immediately after,
reusing the SAME session (not a fresh one), came back **40 passed, 9 failed** with yet another
different substitution set (the item-pickup check flipped back to PASS, several
combat/dialogue/interaction-lock checks flipped to FAIL instead) — confirming the single
substitution in the first run is ordinary run-to-run/session-history timing flakiness in this
environment, not a regression: `docs/CONTRIBUTING.md` already documents two of `verify.ps1`'s
own checks as environment-flaky, and this doc's own prior rounds record the identical pattern
(a re-run against a non-fresh session produces a materially different failure set than a fresh
one). Squarely inside the documented 40-42/7-9 range; no regression.

**Full test suite**: `dotnet test tests/KotorCoop.Tests --no-build` — **2355 passed, 0 failed**
(run after all code changes this session — the three `store_sync` bug fixes — had already
landed).

**Native builds**: `dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError` — both clean, 0 warnings, 0 errors.

No crash on either instance across the whole session's several rig rounds (`store_sync`,
`solo_segment_spectate`, the behaviour-override bridge, `party_pool_sync`, and this baseline
pass). This closes the task's item 5.

## Rig pass (2026-08-28): `companion_gear_sync` — the weapon-slot bit-flag fix (§11) confirmed
## live across all three equip slots, `companion_gear_sync` flipped ON

Task: `companion-state-sync.md` §11.6's live test plan, then the task's own item 3 (flip the
default if clean) and item 4 (baseline/tests/build).

| # | Item | Result | Detail |
| --- | --- | --- | --- |
| 1 | Direct primitive across right weapon (twice, incl. a 16-char resref), left weapon, body | **PASS**, all four calls — raw `CSWInventory` slot confirmed changed after every call, no crash | `companion-state-sync.md` §12 |
| 2 | Mid-session propagation of each to the client | **PASS**, all four — 1.0-2.6s latency, confirmed via the client's log and independent raw slot reads | `companion-state-sync.md` §12 |
| 3 | Flip `companion_gear_sync` ON | **Done** — `CoopConfig`/`AgentSettings`/`AgentComposition`/`PartyPresenceController` defaults, `coop.ini.example`, `CONFIG.md`, `README.md`, roadmap, research README all updated; tests re-asserted, 2355/2355 green | `companion-state-sync.md` §12 |
| 4 | `verify.ps1` baseline / full suite / native build (post-flip, fresh rig) | **PASS** — 40/9 verify.ps1 result matches the documented 40-42/7-9 baseline (the 2 non-baseline failures are the same documented flaky substitutions); `dotnet test` 2355/2355; both native builds clean | `companion-state-sync.md` §12 |

`companion_gear_sync` is now ON by default. No crash on either instance across the whole round.

## Rig pass (2026-08-28): `solo_segment_spectate` — the wipe chain's `Sta_BastTalk`/entry-61
## gate is live-confirmed for the first time; the chain still stalls before the wipe itself

Task: `star-forge-finale.md` §9.4's console recipe (`setglobal Sta_BastTalk 0` → `startdialog
0xAA k_sta_bastila` once → 7× `selectreply 0`), with `solo_segment_spectate=1` on both, to
confirm the roster wipe and then the phantom-hide/input-lock/camera-follow/toast/restore levers.

**Not flipped ON.** `warp sta_m45ac` brought both instances into the module together.
`startdialog` reliably reached entry 61 twice in the same session (confirmed via
`Sta_BastTalk` reading back `1` after the first attempt, reset to `0` before the second) —
live-confirming §9's static analysis of the `StartingList` gate for the first time. But neither
attempt advanced past entry 61 despite 7 clean `selectreply 0` calls each reporting success at
the API level; `partystate.ps1` held `companions 1` throughout on both instances, so the wipe
never fired and none of the spectate levers got a chance to engage (no `solo segment: ...` line
appeared in either plugin log). Full detail, including the leading suspect (entry 62's script
possibly depending on the real `sta_bastila` NPC's spawn state) and the concrete next step (a
tag-resolution console primitive, or pursuing the `RemovePartyMember`-hook alternative), in
`docs/research/star-forge-finale.md`'s new §10. No crash on either instance.
`solo_segment_spectate` stays at its default OFF.

## Rig pass (2026-08-28, later): `store_sync`/`party_pool_sync` live re-test queue — both stay OFF,
## new defects found in each

Task: the queue's three items — `store_sync=1` live re-test per `store-sync.md`'s own plan,
`party_pool_sync=1` live re-test per `party-pool-sync.md` §12's own closing plan, and a
`verify.ps1`/full-test/native-build baseline check.

| # | Item | Result | Detail |
| --- | --- | --- | --- |
| 1 | `store_sync=1` live re-test | **FAIL — stays OFF.** Wipe-prevention, buy/decrement, full depletion, infinite-stock no-op, and blackout-reconnect all confirmed CLEAN. `sell` refuses "no matching item in inventory" for an item three independent readers (item-identity read, `items` sweep, the sell lookup itself) agree exists — never live-run before this pass. Separately, `buy` has no affordability guard: an unaffordable buy underflowed the party's `uint` gold to within a few billion of wrapping (`party gold 4294967076 -> 999999999` and similar lines), leaving the rig's shared save pinned at the 999,999,999 display cap. | `store-sync.md`'s new "Live pass (2026-08-28, later)" section |
| 2 | `party_pool_sync=1` live re-test | **FAIL — stays OFF.** §12's C# echo-guard fix (bench-only) could not be confirmed: the live rig found a more basic native-side defect first — a freshly-inserted party-pool item is not reliably found by the very next local `poolremove`/scan (host: 3 rapid `pooladd` calls to the same resref, only 1 ever reached the session's own local-change detection; a bare `pooladd` immediately followed by `poolremove` of the identical resref failed with "no matching stack found in the pool" while an independent `item <id>` read confirmed the item was live and correctly tagged). Sits below `PartyPoolPump.cs` entirely — the C# fix's own correctness is unconfirmed either way. | `party-pool-sync.md` §13 |
| 3 | `verify.ps1` baseline / full suite / native build | **PASS.** Fresh two-instance session (`coop.ini` reset to `profiles/coop.ini.example` on both): **40 passed, 9 failed** — exactly the documented 40-42/7-9 baseline (the 7 core known failures plus both documented flaky substitutions, `a client hit costs the enemy real hit points` and `an item picked up here vanishes on the peer`, both landing as FAIL together this run). `dotnet test tests/KotorCoop.Tests --no-build`: 2363/2364 (the one failure is the pre-existing, CI-excluded `HeavyPacketLossStillConverges`). `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`: both clean, 0 warnings/errors. | this section |

No crash on either instance across the whole round. Both flags stay at their default OFF
(`profiles/coop.ini.example` unchanged); both game directories' `coop.ini` were reset back to the
plain example template before this pass ended. An incidental finding — recovering a `tar_m02aa`
client instance stuck behind the ambient `duros` party-selection trap needed a full process
restart (`partyselect ok` and even a savegame reload did not clear it) — is recorded in
`store-sync.md`'s own new section alongside the rest of this round's evidence.

## Fix pass (2026-08-28, no rig this round): `sell`'s wrong-repository bug and `buy`'s missing
## affordability guard, root-caused and fixed by static RE; `setgold` added to repair the rig save

Task: root-cause the two `sell`/`buy` defects the previous rig pass above found and fix them, add
a `setgold` dev verb, and note how to repair the shared rig save's own poisoned gold. No live rig
was available this round — everything below is static disassembly plus unit/build verification;
the fixes are **NEEDS-LIVE** until the next rig round re-runs `store-sync.md`'s own live test plan.

**`sell`'s root cause, VERIFIED by fresh disasm**: `Odyssey.Store.cs`'s `SellToStore` used to read
the player's inventory repository by dereferencing `CSWSCreature+0xA30` directly. Fresh disasm of
`CSWSCreature::GetItemRepository 0x004EF770` — the SAME accessor `CSWGuiStore::
PopulateInventoryItemListBox` (the sell list's own population routine) and `CSWSCreature::
AcquireItem 0x005158E0` (the routine a bought item is actually handed to the buyer through) both
call with argument `1` — shows that for a live, in-party PC (the overwhelmingly common case)
this accessor does NOT read `+0xA30` at all: it resolves `CSWPartyTable::GetPartyItems`, the
SAME shared party-wide item repository `Odyssey.PartyPool.cs` already reaches directly for
`pooladd`/`poolremove`. KOTOR 1's own inventory model is party-shared, not per-creature — a real
player's "own" backpack IS the party pool. `CSWSCreature::GetGold`/`SetGold` (0x4EDD60/0x4EDD90)
independently confirm the same thing: both gate on the identical creature `+0xA88` flag and read/
write `CSWPartyTable+0xFC` (the party wallet) rather than a per-creature field. Fixed: `SellToStore`
now calls `GetItemRepositoryFn(player, 1)` instead of reading `+0xA30` — full detail and the
complete disassembly trace in `docs/research/store-sync.md`'s new addendum.

**`buy`'s missing affordability guard, VERIFIED by fresh disasm**: full disassembly of
`CSWGuiStore::BuyItem 0x006C0BE0` this pass confirms the previous rig pass's live finding exactly
— price lookup, subtract from the cached gold int, `CSWSCreature::SetGold`, with NO comparison or
branch anywhere between the subtraction and the `SetGold` call. `SetGold` itself (0x004EDD90) only
clamps the incoming value to an UPPER bound (999,999,999) — there is no lower-bound/underflow
guard at all, confirming exactly how the rig's own gold got driven to just under `UINT32_MAX`.
The real in-game Buy button is guarded only by the GUI graying its own button out when
unaffordable; `Odyssey.BuyFromStore` calls the click handler directly, bypassing that
precondition, so it now applies the same check itself (`StoreAffordabilityRule.CanAfford`,
`price <= 0 || gold >= price`) before every single unit, refusing with the price and current gold
named rather than letting the purchase through.

**`setgold <amount>` added** (`CheatKindSetGold = 44`, mirrored in both `SharedBlock.cs` and
`IGameBridge.cs`): sets the party's gold directly through the real `CSWSCreature::SetGold` call —
an ABSOLUTE target, unlike the pre-existing `grantgold <delta>` (a signed delta written directly
by the agent). Reachable at the real `~` console (registered in `ConsoleVerbCatalog.cs`/
`FuncInterpHook.cs`, not blockpoke-only), same as `buy`/`sell`/`openstore`.

**⚠ The shared rig save's gold is poisoned.** The previous rig pass's own unaffordable `buy` left
the shared rig save's party gold pinned at the engine's own 999,999,999 `SetGold` clamp (reached
via the underflow-then-partial-self-heal sequence `store-sync.md`'s live pass logged) — not
corrupted (a valid, in-range value), but not one any real playthrough would organically reach, and
worth resetting before the next round trusts a "gold converged" result against this save. **Fix**:
once the fixed plugin is installed on both game directories (`build.ps1 -Install -WarnAsError` —
`demo.ps1` does not deploy the plugin itself), run `setgold <sane-amount>` (e.g. `setgold 500`) on
whichever instance holds the shared save loaded, or on both if `progression_sync`/the gold ledger
is not yet converging them. `grantgold 0` (a no-op delta) can be used first to read the current
value back without changing it, if confirming the poisoned reading is wanted before repairing it.

**Build and test result**: `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/
build.ps1 -WarnAsError` both clean (0 warnings, 0 errors). `dotnet test tests/KotorCoop.Tests
--no-build`: full suite green (see `docs/research/store-sync.md`'s own addendum for the exact
count) — new `StoreAffordabilityRuleTests` (the pure guard rule, pulled into the plugin build's
compile-include list per `docs/ARCHITECTURE.md`'s trick) plus the console verb-table coverage
tests for `setgold` and the `CheatKindSyncTests` mirror check, none of which needed a reinvented
copy of the rule. **NEEDS-LIVE**: the next rig round should re-run `store-sync.md`'s own §6/
2026-08-28-addendum live test plan (`sell` an item just bought, confirm it now finds it; a `buy`
past the party's real gold, confirm the refusal names the price/gold and gold is unchanged) —
`store_sync` itself stays at its default OFF regardless, unrelated to this fix (it touches only
the `buy`/`sell` dev verbs and `SellToStore`'s repo resolution, not the wire sync channel or its
own separately-tracked Bug 4).

## Rig pass (2026-08-28, later): `solo_segment_spectate` — the script-agnostic `RemovePartyMemberHook`
## live plan run end to end, every lever confirmed, flipped ON by default; `party_follow_phase2_kick`
## A/B attempted but the target precondition never reproduced

Task: two items from this session's own queue — run `star-forge-finale.md` §11's live plan for
the `RemovePartyMember`-hook solo-segment trigger and flip the default if clean; run
`party-reconciliation.md` round 95's own A/B recipe for `party_follow_phase2_kick`.

**Item 1 — `solo_segment_spectate`: PASS, flipped ON.** Fresh build/install on both game
directories (strings-confirmed `RemovePartyMemberBySlot` present in both deployed `dinput8.dll`s).
The splice install line (`solo segment: RemovePartyMember calls at 0x541C1E`) appeared on both
plugin logs at startup — live-confirming §11.1's `HookSite` derivation for the first time. A
single `removeparty 0` produced exactly one debug line and no crash. A tight 3-call burst
(~330ms total, sent from one PowerShell process to avoid the per-invocation spawn overhead that
made three separate `blockpoke.ps1` calls land ~1s apart and miss the debounce window) began a
segment on the second call, in both a catalogued module (`sta_m45ac`) and, after a plugin
restart with `solo_segment_any_module=1`, an uncatalogued one (`END_M01AA`) — and, with the key
back at its default `0`, the identical burst in an uncatalogued module correctly produced no
`began`. The owner's roster genuinely emptied (`partystate.ps1`: `companions 0` on both sides),
the client's own phantom stopped being tracked on the owner's instance at all
(`phantoms 0  remotes 0`), the spectator's plugin logged `solo segment: spectating 0x... — input
parked, camera follows`, and its agent surfaced a toast (`* Lanik is fighting alone -
spectating`). A real roster refill (`recruit 0`) ended the segment cleanly
(`solo segment: ended (roster refilled or the module changed)`), confirming §11.2's own
race-guard fix does not block a genuine end. No crash across the whole round. Full detail in
`star-forge-finale.md`'s new §12. **`solo_segment_spectate` is now ON by default** — the one
remaining gap (stopping a real physical WASD press) is unprovable with this project's own input
primitives (injected keystrokes never reach this game), unchanged from every prior round.

**Item 2 — `party_follow_phase2_kick`: INCONCLUSIVE, stays OFF.** Round 88's own exact recipe
(`tp 20 22 -1.3`, bare-point `walk 10 15`, `END_M01AA`) run six times with
`party_follow_finelog=1` on. Five completed attempts all reproduced the SAME shape — a
transient pass through `phase=2` followed by a freeze in `phase=7`, position bit-identical
across dozens of samples, `attempts=0`, but the companion only ~1-1.4 units from its leader
(never the >3-units-away condition the fix's own `Phase2StallRule.ShouldKick` requires
alongside `phase==2`). The candidate's own target precondition — frozen in phase 2 specifically,
genuinely far behind — never arose under the CURRENT shipped defaults with this recipe, so no
control-vs-treatment comparison was possible; `party_follow_phase2_kick` was never toggled to
`1` this round because there was nothing to test it against. A sixth attempt (an instant `tp`
separation instead of a gradual `walk`) produced a different anomaly — `party follow finelog`
output stopped entirely for 5+ minutes with no crash, both processes responsive, no combat per
the agent's own status line, but console round-trips slowing from sub-second to ~5.5s — flagged
as a fresh, unexplained lead rather than chased further this round. Full detail, including two
concrete next-step leads, in `party-reconciliation.md`'s new Round 96. No crash across the whole
round; no code or default changed for this item.

**Item 3 — `verify.ps1` baseline / full suite / native build, post-flip: PASS.** After the
`solo_segment_spectate` default flip and its supporting `CoopConfig.cs` fallback fix above, both
native builds were rebuilt (`dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError` on both game directories, strings-confirmed) and
`dotnet test tests/KotorCoop.Tests --no-build` run: **2382/2382 passed**, 0 failed, 0 skipped —
up from the pre-round 2364 baseline by the item-1 code changes' own test-suite headcount (no
regressions). A fresh two-instance session (`coop.ini` reset to the now-updated
`profiles/coop.ini.example` on both installs, which now ships `solo_segment_spectate=1`) then
ran `tools/verify.ps1`: **41 passed, 8 failed** — squarely the documented 40-42/7-9 baseline,
all eight failures matching the long-standing known set (`phantom damageable, not a sink`, `the
peer really loses those hit points`, `a client hit costs the enemy real hit points`, `a downed
player is reported to the agent`, `the co-op revive stands the player back up`, `every engine
call signature matches the binary` (187/189), `a container opened on the client is held and
shown open on the host`, and the `no per-frame spam` flaky substitution). No new failures from
this round's changes. No crash on either instance across the whole session.

## Rig pass (2026-08-28/29): `store_sync`/`party_pool_sync` re-test queue, follow-AI Round 96's
## phase-7 lead, and baseline — party-pool insert defect PINNED with hard live evidence, follow-AI
## phase-7 question ANSWERED (negatively) via a new `followkick` verb; both sync flags stay OFF

Task: this session's five-item queue — repair the shared rig save's gold (item 0), re-test
`store_sync` (item 1), re-test `party_pool_sync` (item 2), chase the follow-AI phase-7 lead with
a new `followkick` dev verb (item 3), and a baseline `verify.ps1`/full-test/native-build pass
(item 4).

### Item 0 — gold repair: the shared rig save was NOT found poisoned this round

`grantgold 0` (a no-op read) on both instances at session start answered `gold 0 -> 0` — not the
previously-documented ~999,999,999 clamp. The prior pass's own poisoned reading was apparently
never persisted to the save file itself (this project has no `savegame` console primitive; every
runtime gold write, including this pass's own `setgold`, only affects the live process and is
lost on the next `demo.ps1` load unless a real in-game save happens, which nothing here
automates). `setgold 5000` was applied and confirmed converging both sides
(`setgold: gold 0 -> 5000` on the host, `setgold: gold 5000 -> 5000` on the client ~1s later —
`progression_sync` already had it) as a functional confirmation of the verb, but this value is
NOT expected to persist past the next rig restart for the reason above. No further action taken;
noted here rather than fabricated as "fixed."

### Item 1 — `store_sync`: see `docs/research/store-sync.md`'s own new "Live pass (2026-08-28/29)"
### section for full detail. Summary: wipe-prevention CLEAN, `buy` (afford/refuse/deplete) all
### CONFIRMED including a new finding that the wire mirror needs neither side's store GUI open,
### `sell` still NOT positively confirmed — traced to test-methodology (picking store-owned
### resrefs by mistake) plus the SAME native party-pool defect item 2 pinned this round, not a
### `sell`-specific regression as far as this round can tell. Stays OFF.

### Item 2 — `party_pool_sync`: see `docs/research/party-pool-sync.md`'s own new §15 for full
### detail. Summary: the "two lookups, one frame" diagnostic this round added to
### `Odyssey.AddItemToPartyPool` (repo pointer + `item_count` before/after every insert) pinned
### the native defect precisely and reproducibly — the repo pointer is rock-stable across dozens
### of calls (§13.3's hypothesis 2 RULED OUT), while `item_count` never changes across a genuine,
### engine-confirmed insert (hypothesis 1 CONFIRMED with direct before/after evidence, 6/6 times
### this round, on both host and client). `CItemRepository::InsertItem`'s own append path
### (index -1) either has an increment condition this call site does not meet, or `+0x10` is not
### the field it actually increments. Stays OFF; the next round's target is now a direct
### disassembly of `InsertItem` itself (0x0055D2C0), not further C#-side detection logic.

### Item 3 — follow-AI phase-7 lead: see `docs/research/party-reconciliation.md`'s own new
### "Round 97" for full detail. Summary: Round 96's phase-7 near-target freeze reproduced on the
### FIRST attempt this round (`phase=7 attempts=0`, position frozen, `targetVsSnapshotDist=1.105`,
### `pathWaitDestAccept=False`, `CurrentPathNode==DestNode` yet never accepting). A new dev verb,
### `followkick <slot>` (`docs/CONSOLE.md`'s new row), dumps the follow record and then forces
### `record+4=10` (TeleportForward) UNCONDITIONALLY, regardless of phase — unlike the shipped
### `party_follow_phase2_kick` (phase 2 only, stayed at its default OFF for this whole round).
### Fired once against the reproduced stall: the companion did NOT resume — phase moved to 10 and
### stayed there, bit-identical, for 34+ seconds afterward. This directly answers Round 96's own
### open question: the phase-2 recovery lever does NOT generalise to the phase-7 freeze: it just
### trades one permanent stall for a different one. Documented only, per the task's own
### instruction — no default changed.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors.
- `src/KotorCoop.Plugin/build.ps1 -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed and strings-confirmed (`FollowKick` present in both deployed
  `dinput8.dll`s).
- `dotnet test tests/KotorCoop.Tests --no-build`: **2396/2396 passed**, 0 failed, 0 skipped.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` (both sync flags at their shipped OFF default): **41 passed, 8
  failed** — squarely the documented 40-42/7-9 baseline, all eight failures matching the
  long-standing known set exactly (`phantom damageable, not a sink`; `the peer really loses those
  hit points`; `a client hit costs the enemy real hit points`; `a downed player is reported to
  the agent`; `the co-op revive stands the player back up`; `every engine call signature matches
  the binary` (191/193 this run — same category, count varies run to run); `a container opened
  on the client is held and shown open on the host`; and the `no per-frame spam` flaky
  substitution). No new failures, no regression from this round's changes.

No crash from any of this round's own code on either instance across the whole session — the one
client crash mid-round was the pre-existing, documented `tar_m02aa` ambient `duros`/warp-while-
downed trap (see `store-sync.md`'s own incidental-finding note), recovered by a full process
restart, matching this project's own established pattern for that hazard. Both `store_sync` and
`party_pool_sync` stay OFF (`profiles/coop.ini.example` unchanged); both game directories'
`coop.ini` were reset back to the plain example template before this pass ended.

**Code committed this round** (before the rig pass, so the rig ran the code it tests):
`followkick` dev verb (`SharedBlock.CheatKindFollowKick`/`CheatKinds.FollowKick = 45`,
`Odyssey.TryForceTeleportForward`, full wiring through `ConsoleCommand`/`CoopCommandDispatcher`/
`CheatController`) and the party-pool insert diagnostic
(`Odyssey.AddItemToPartyPool`'s new repo-pointer/`item_count` before/after log line) — see git
history for the exact commit.

## Rig pass (2026-08-29): the merged `AddItem` party-pool fix is confirmed to CRASH the host live
## (3/3), `sell` is confirmed NOT to move the item despite reporting success, item 3
## (`party_follow_phase7_release` A/B) was not reached, baseline is clean

Task: this session's four-item queue, starting from a worktree merged forward onto the
`party-pool deposit uses CItemRepository::AddItem` commit — re-test `party_pool_sync` (item 1),
re-test `store_sync` (item 2), the `party_follow_phase7_release` A/B (item 3), and a
`verify.ps1`/full-test/native-build baseline (item 4).

### Item 1 — `party_pool_sync`: **FAIL, stays OFF.** See `docs/research/party-pool-sync.md`'s new
### §17 for full detail. Summary: before the live test could even run, a real, separate
### regression was found and fixed — `pooladd`/`poolremove` were never added to
### `FuncInterpHook`'s console-verb allowlist, so a real player's own keystroke submission of
### either fell through to the retail engine's own `FuncInterp` and silently did nothing; every
### prior round's "live" use of these verbs went through `blockpoke.ps1 console`, which bypasses
### that allowlist entirely, masking the gap. Fixed (`"pooladd", "poolremove"` added to
### `FuncInterpHook.Verbs`), confirmed working via a real typed keystroke. With that fixed, the
### actual live test found something more severe than anything `party-pool-sync.md`'s prior nine
### rounds turned up: `AddItemToPartyPool` (the `AddItem`-based fix this session's merge brought
### in) reliably CRASHES the host — 3/3 reproductions across two different resrefs and two
### separate rig restarts, including once via ordinary gameplay (a `warp` transition's own
### auto-deposit into the pool, not the manual `pooladd` cheat at all). Root cause: `AddItem`'s
### own merge-notify path reaches the retail engine's dead multiplayer network-message
### serialization code and dereferences a bad pointer. This is WORSE than the silent no-op defect
### it replaced — `party_pool_sync` stays OFF, and `AddItem` must not ship without either
### suppressing that notify path or finding a way to avoid it.

### Item 2 — `store_sync`: **`sell` FAIL (confirmed negative), rest CLEAN, stays OFF.** See
### `docs/research/store-sync.md`'s new "Live pass (2026-08-29)" section. Summary: with
### `party_pool_sync=0` (to avoid item 1's crash contaminating this test) and a `scriptgive`d
### resref (specifically to rule out the party-pool insert-visibility defect the prior round's
### own `sell` test was blocked by), `sell Larrim_Store g_w_blstrpstl001` reported success
### (`status 1`, no refusal, no crash) but a follow-up `items` sweep two minutes later shows the
### item STILL listed as `yours` — never actually transferred to the store. `store_sync`'s own
### pump correspondingly never detected a stock change, for the honest reason there was nothing to
### detect. This narrows the prior round's "still not positively confirmed" to a verified,
### `sell`-specific negative result — `buy` remains confirmed working and unaffected.
### `CSWGuiStore::SellItem` needs a fresh full disassembly next round. `store_sync` stays OFF.

### Item 3 — `party_follow_phase7_release` A/B: **NOT REACHED this round.** See
### `docs/research/party-reconciliation.md`'s new "Round 99" entry. Items 1 and 2 (ahead of this
### one in the task's own ordering) each surfaced a defect severe enough to need full
### root-causing and write-up, consuming the round's rig time budget before item 3's own
### multi-rep staging could start. The flag stays at its shipped default (OFF); Round 98's own
### live-test recipe is unchanged and still the one to run next.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors.
- `src/KotorCoop.Plugin/build.ps1 -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed and strings-confirmed (`pooladd`/`poolremove` present in both
  deployed `dinput8.dll`s).
- `dotnet test tests/KotorCoop.Tests --no-build`: **2410/2410 passed**, 0 failed, 0 skipped —
  including the sometimes-flaky `CoopCommandDispatcherTests` pair, clean this run.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` (both sync flags at their shipped OFF default): **41 passed, 8
  failed**. Seven of the eight match the long-documented baseline set exactly (`phantom
  damageable, not a sink`; `the peer really loses those hit points`; `a client hit costs the
  enemy real hit points`; `every engine call signature matches the binary` (192/194 this run);
  `a container opened on the client is held and shown open on the host`; `no per-frame spam`
  (the flaky substitution); `an item picked up here vanishes on the peer` (the OTHER documented
  flaky substitution — both landing as FAIL together this run, same as the 2026-08-28 baseline
  pass's own "40 passed, 9 failed" entry recorded). The eighth, `no swkotor crash in the event
  log`, is a false positive from this SAME session's own item-1 crash testing still sitting in
  the Windows Application event log (`tools/crashlog.ps1`'s three entries at 12:34/12:42/12:47
  all predate this baseline session's own 13:01 start, confirmed by wall-clock timestamp — no
  crash happened during the baseline run itself, and the host process stayed alive and
  responding throughout). No new failures, no regression from this round's own code changes.

No crash from `store_sync`, from the `FuncInterpHook` fix, or from the baseline session itself —
every crash this round traces to `party_pool_sync`'s `AddItem` defect specifically (item 1's own
finding), run in its own isolated rig sessions with `store_sync`/baseline testing done separately
to avoid contaminating them. Both `party_pool_sync` and `store_sync` stay OFF
(`profiles/coop.ini.example` unchanged); both game directories' `coop.ini` were reset back to the
plain example template before this pass ended.

**Code committed this round**: the `FuncInterpHook.Verbs` fix
(`src/KotorCoop.Plugin/FuncInterpHook.cs`), `tools/sendkey.ps1`'s digit/underscore scan-code
additions (needed to type a resref at the live console at all), and this write-up across
`docs/research/party-pool-sync.md`, `docs/research/store-sync.md`,
`docs/research/party-reconciliation.md` and `docs/research/README.md` — see git history for the
exact commit(s).

## Rig pass (2026-08-28): store_sync's brand-new-key mirror confirmed live but a new client-buy
## detection gap found (item 1); party_pool_sync's wildcard identity fix confirmed live but a
## SEVERE rapid-repeat runaway amplification found (item 2); party_follow_phase7_release's
## refusal logging confirmed executing on every rep but never caught the target stuck phase
## (item 3); baseline clean (item 4)

Task: this session's four-item queue, starting from a worktree merged forward onto block v59
(`party-pool identity matcher with wildcards` + ambient-suppression, `966fe0f`). Full detail in
each feature's own research doc; this entry is the session-level summary.

### Item 1 — `store_sync`: see `docs/research/store-sync.md`'s new "Live pass (2026-08-28, rig
### round)" section. Summary: the prior no-rig round's brand-new-key wire-mirror fix is now
### CONFIRMED live — a host sell genuinely mirrors to the client's copy within one pump tick. But
### the recipe's own other half failed: a client buy of a key it had only just received via
### mirror was never detected/published back to the host for 2+ minutes (confirmed by the host
### successfully re-buying the same "already sold" unit nearly three minutes later). A same-round
### isolation test with an untouched resref shows client-side detect/publish works in general,
### narrowing this to keys still armed in `StorePump`'s own `_pendingApplyTarget` guard — not
### root-caused this round. Infinite-stock no-op and blackout+reconnect resync both clean. Also
### flags an incidental `setgold`-vs-`grantgold` gold-ledger display defect (engine value stayed
### safely clamped; only a log line was wrong). `store_sync` stays OFF.

### Item 2 — `party_pool_sync`: see `docs/research/party-pool-sync.md`'s new §21. Summary: block
### v59's wildcard identity fix for `poolremove` confirmed working live (clean drains on both
### sides). But three rapid `pooladd` calls of the SAME resref — explicitly part of this item's
### own recipe — triggered an UNBOUNDED host<->client ping-pong that inflated 3 real deposits into
### nearly 2,000 phantom units before settling back near zero on its own. Reproduced independently
### in the other direction with a second resref from the client. No crash (both processes survived
### a 60s watch and a warp), but a severe correctness catastrophe for a channel meant to ship
### silently ON. `party_pool_sync` stays OFF, disqualified — worse than either gap §19/20 left
### open.

### Item 3 — `party_follow_phase7_release` A/B: see `docs/research/party-reconciliation.md`'s new
### "Round 102". Summary: three reps per leg this time (six rig sessions total). CONTROL clean 2/2
### (a third rep's own `reconnect` never re-mirrored the companion — inconclusive, not a failure,
### and itself a new finding: `reconnect` does not reliably re-establish companion mirroring the
### way a fresh `connect` does). TREATMENT produced a real `phase7release` log line on EVERY rep
### (3/3) — round 101's own retry/refusal-logging fix genuinely executes now, unlike round 100's
### total silence — but all three landed on "nothing to do - phase already at the reset sentinel
### (-1)", not the recipe's own target "reset phase X -> -1" line; this round's staging never
### caught the companion in a genuinely stuck non-reset phase at release time. Not a regression;
### the fix's actual corrective behavior remains NEEDS-LIVE. `party_follow_phase7_release` stays
### OFF. No crash across any of the six rig sessions this item used.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors.
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed and strings-confirmed (`followkick` present in both deployed
  `dinput8.dll`s).
- `dotnet test tests/KotorCoop.Tests --no-build`: **2444/2444 passed**, 0 failed, 0 skipped (run
  twice this session — once before the rig work, once for this baseline — both clean).
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides (every sync flag at its shipped default):
  **41 passed, 8 failed** — squarely the documented 40-42/7-9 baseline, all eight failures
  matching the long-documented known set exactly (`phantom damageable, not a sink`; `the peer
  really loses those hit points`; `a client hit costs the enemy real hit points`; `a downed player
  is reported to the agent`; `the co-op revive stands the player back up`; `every engine call
  signature matches the binary` (197/199 this run — same category, count varies run to run); `a
  container opened on the client is held and shown open on the host`; and the `no per-frame spam`
  flaky substitution). No new failures, no regression from this session's own work (all four items
  were live-verification-only passes — no source changed this session).

No crash from any of this session's own testing across all four items and the baseline — both
`swkotor.exe` processes confirmed alive and responding at the end of every rig session (ten
restarts total across the four items, item 2's runaway pool amplification and item 3's A/B
included). Both game directories' `coop.ini` were reset back to the plain example template before
this session ended; `store_sync`, `party_pool_sync`, and `party_follow_phase7_release` all stay at
their shipped OFF default.

**No source changed this session** — every item was a live-verification pass against the code
already on `main` as of `966fe0f`. The four write-ups above (`docs/research/store-sync.md`,
`docs/research/party-pool-sync.md`, `docs/research/party-reconciliation.md`, this entry) are the
session's only output — see git history for the exact commits.

## Rig pass (2026-08-28): reconnect re-mirror fix (round 103) confirmed live (item 1); party-pool
## loop breaker (root cause 4) confirmed fixed but a smaller over-count/divergence found (item 2);
## store_sync's client buy-back gap still reproduces live plus a genuine client crash (item 3);
## baseline clean (item 4)

Task: this session's four-item queue, starting from a worktree merged forward onto `main` at
`9914148` ("party-pool pump publishes only excess beyond the live wire total" — root cause 4's own
fix). Full detail in each feature's own research doc; this entry is the session-level summary.

### Item 1 — reconnect re-mirror: see `docs/research/party-reconciliation.md`'s new "Round 104".
### Summary: round 103's `PartyPresenceWriteCache.Reset()` fix, live-confirmed. Three
`disconnect`/`reconnect` reps on the client, defaults on both sides: every rep re-established
`party drive: mirroring 1 of the host's companions` within ~2s of reconnect (1.76s, 0.76s, 1.82s),
no full instance restart needed. **PASS, 3/3.**

### Item 2 — `party_pool_sync`: see `docs/research/party-pool-sync.md`'s new §23. Summary: the
severe, unbounded host<->client ping-pong (root cause 4, disqualifying in the prior round) is
CONFIRMED FIXED — six rig sessions of rapid-deposit and blackout recipes, zero loop-breaker trips,
every burst stayed at 2-4 published ops per key. But a smaller, real, live-reproduced defect
remains: rapid same-key deposits over-count by a small bounded amount (roughly +1 to +4) in either
direction, and a blackout+reconnect resync left the two instances genuinely DIVERGED from each
other (host held 9, client held 7, for 5 real deposits) rather than merely over-counted together.
`party_pool_sync` stays OFF, **NOT flipped ON** — this round's recipe was not clean.

### Item 3 — `store_sync`: see `docs/research/store-sync.md`'s new "Live pass (2026-08-28, rig
### round)" section. Summary: the sell-side mirror (host to client) still works cleanly. But the
client buy-back gap the most recent no-rig round believed it had closed did NOT hold live: 35+
seconds with zero detection/publish on the client side after a buy-back, and the host's own re-buy
of the "sold" item succeeded when the recipe expected a refusal ("absent"). A genuine client-side
crash (access violation, confirmed via the Windows Application event log,
`Fault offset: 0x0023d4b3`) was also hit mid-session during a module transition — not conclusively
tied to `store_sync` (no store operation was in flight at the moment), but observed under it and
worth the next round's attention. `store_sync` stays OFF, **NOT flipped ON**.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors (run fresh at the top of
  this session, after merging `main` forward to `9914148`).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed and strings-confirmed (`RequestPartyPoolResync` present in both
  deployed `KotorCoop.Session.dll`s, `FollowKick` present in both deployed `dinput8.dll`s).
- `dotnet test tests/KotorCoop.Tests --no-build`: **2469/2470 passed** — the one failure,
  `DisconnectRecoveryTests.HeavyPacketLossStillConverges`, is the documented known-flaky suite CI
  itself excludes; re-run alone immediately after, it passed cleanly, confirming flake rather than
  regression.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides (every sync flag at its shipped default): **41 passed,
  8 failed** — squarely the documented 40-42/7-9 baseline. Seven of the eight failures match the
  long-documented known set exactly (`phantom damageable, not a sink`; `the peer really loses those
  hit points`; `a client hit costs the enemy real hit points`; `every engine call signature matches
  the binary` (197/199); `a container opened on the client is held and shown open on the host`; `an
  item picked up here vanishes on the peer`; `no per-frame spam` — the two documented flaky
  substitutions both landing as FAIL together this run, as before). The eighth, `no swkotor crash
  in the event log`, is a false positive from THIS SAME session's own item-3 crash finding (see
  above) still sitting in the Windows Application event log at the time this baseline ran — that
  crash predates this baseline session's own instance start by roughly 12 minutes, confirmed by
  wall-clock timestamp; no crash happened during the baseline run itself, and both processes stayed
  alive and responding throughout. No new failures, no regression from this session's own work (all
  four items were live-verification-only passes — no source changed this session, only the four
  research write-ups and this entry).

No crash from item 1, item 2, or the baseline pass itself — every crash this session traces to item
3's own finding, in its own isolated rig session. Both game directories' `coop.ini` were reset back
to the plain example template before this session ended; `store_sync`, `party_pool_sync`, and
`party_follow_phase7_release` all stay at their shipped OFF default.

**No source changed this session** — every item was a live-verification pass against the code
already on `main` as of `9914148`. The three write-ups above (`docs/research/party-reconciliation.md`,
`docs/research/party-pool-sync.md`, `docs/research/store-sync.md`) plus this entry are the
session's only output — see git history for the exact commits.
