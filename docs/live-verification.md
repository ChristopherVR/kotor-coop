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
  instances together. "Down" means the whole party — the engine auto-revives a PC whose
  companion still stands after ~5 s — so the unattended recipe is `down 0` (the companion
  slot) then `down` on EACH instance: `( host 'down 0'; host 'down' ) & ( client 'down 0';
  client 'down' ) & wait`. Sequence the two verbs per pid; two pokes to the same pid at once
  collide and one is lost.
- After the wipe (VERIFIED 2026-09-02, three cycles): both agents log `every player is down;
  releasing the game-over screen for everyone`, then ~15 s later `away: local reads have
  failed`. Reload the host (`KotorCoop.Agent.exe load --pid <host> --load-save 1`, or the
  menu). The client's agent must log `the host is back in the world after the wipe -
  rejoining their world` and `rejoined '<module>'` within a couple of seconds of the host
  standing, both end at full hp with `who` seeing each other, and the following two minutes
  of both logs must carry NO repeating `party pool` / `script item grant` lines. The
  client's carried stacks land in the host's pool exactly once (`added '<resref>' x1`).
  `grep -a FIRST-CHANCE` on both `kotorcoop-plugin.log`s must be empty.
- `pwsh tools\blockpoke.ps1 -ProcId <pid> death` prints the whole state, and
  `death revive` fires a rescue by hand; `console 'resolve party'` dumps the CSWParty
  member array (a stale id beyond the count after a reload is expected and harmless).

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

## Rig pass (2026-08-28): store_sync's ghost-object-scan + module-scoping fixes re-tested together
## live for the first time (item 1); party_pool_sync's root-cause-5/6 fix re-tested and found the
## recipe's own first clause still fails live (item 2); baseline clean (item 3)

Task: this session's three-item queue, starting from a worktree whose HEAD already equalled `main`
at `cbcc0c4` ("Taris end-to-end playthrough smoke") — no merge needed. Full detail in each
feature's own research doc; this entry is the session-level summary.

### Item 1 — `store_sync`: see `docs/research/store-sync.md`'s new "Live pass (2026-08-28, rig
### round): both fixes re-tested together" section. Summary: re-ran the full buy-back recipe
against the ghost-object-scan fix (`EngineGlobals.TryResolveServerObject` replacing the raw
`PatternScanner` sweep) and `StorePump`'s module-scoping together, live, for the first time. Four
clauses CONFIRMED CLEAN: wipe-prevention on open, the host-sell-to-client mirror (confirmed twice,
in two independently fresh sessions, ~1s latency, no ping-pong), the infinite-stock buy no-op, and
blackout+reconnect resync (0 liveness anomalies, no doubling). The client buy-back detection clause
is INCONCLUSIVE — `tar_m02aa`'s own well-documented ambient `duros`/party-select modal trap would
not clear with the doc's established fast-retry recipe this round, and the only recovery that
worked (`quittomenu` + `loadgame 0` on the client alone) desynced the client's own `StorePump`
belief from its freshly-reloaded engine state, contaminating that one check. The recipe's own "a
warp mid-session → no crash" bar was not met: three crashes occurred during module transitions —
two more live reproductions of the already-triaged, unrelated `0x0023d4b3` (`CSWCObject::
GetServerObject`) engine bug, plus one NEW, previously undocumented crash at `0x0051C525`
(`CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader`, the party-follow subsystem — flagged
for `party-reconciliation.md`, not chased further this session). Neither crash address is
reachable from any `store_sync` code path (`grep` confirms). `store_sync` stays OFF, not flipped
on.

### Item 2 — `party_pool_sync`: see `docs/research/party-pool-sync.md`'s new §25. Summary: re-ran
§24's own root-cause-5/6 fix (the `_pendingApplies` lock, the unconditional `_applyInFlight`
clear) against its own concrete next live step — three rapid host `pooladd`s expected to converge
to EXACTLY 3 on both sides. Run in `END_M01AA` (the default starting module) specifically to avoid
`tar_m02aa`'s own hazards entirely — no warp needed. The severe unbounded ping-pong from §21 did
NOT reproduce (a real improvement), but the recipe's own very first clause still failed: a live
`items` sweep, several minutes after the wire channel went silent, found the SAME object id
showing `stack 1` on the host and `stack 2` on the client — neither matching the target of 3, and
disagreeing with each other. The host's own three log-confirmed local inserts do not match its own
live-read total, and only 2 of the 3 published units were ever confirmed landing on the client. No
crash, no loop-breaker trip — a quieter, narrower defect than §21's runaway, but still
disqualifying against this item's own "EXACTLY +3" bar. Root cause not pinned this round (whether
the loss is at the engine-insert layer, the pump's own local scan, or the `items` diagnostic's own
read) — flagged as the concrete next target. `party_pool_sync` stays OFF, not flipped on; the
remaining recipe clauses (×3 client, deposit-then-remove, blackout-during-deposits) were not
reached.

### Item 3 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors (run at the top of this
  session).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed and strings-confirmed (`TryResolveServerObject` present in both
  deployed `KotorCoop.Memory.dll`s, `store: module changed (` present in both deployed
  `KotorCoop.Session.dll`s — UTF-16LE, confirming both `store_sync` fixes under test were genuinely
  deployed before item 1's rig round). Rebuilt and reinstalled a second time at the top of item 3
  to restore a clean baseline install after item 1/2's own rig work.
- `dotnet test tests/KotorCoop.Tests --no-build`: **2475/2475 passed** on a clean, isolated run.
  An earlier run taken concurrently with a native plugin rebuild showed 2 failures
  (`CoopCommandDispatcherTests.HostingWithARejoinGateOnServesItsWorldOverWorldTransfer` — the
  documented UDP-port-binding flake, confirmed by re-running that class alone: 8/8 clean; and
  `StoreLoopbackTests.LoopBreakerFreezesARunawayKeyThenReconvergesOnceItSettles` — a
  timing-sensitive test perturbed by the concurrent CPU-heavy native build, confirmed flaky by two
  isolated re-runs: one failure, one pass) — both re-run individually and in the final full-suite
  pass, both clean; no regression.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides (every sync flag at its shipped default): **41 passed,
  8 failed** — squarely the documented 40-42/7-9 baseline, all eight failures matching the
  long-documented known set exactly (`phantom damageable, not a sink`; `the peer really loses those
  hit points`; `a client hit costs the enemy real hit points`; `a downed player is reported to the
  agent`; `the co-op revive stands the player back up`; `every engine call signature matches the
  binary` (197/199 this run); `a container opened on the client is held and shown open on the
  host`; and the `no per-frame spam` flaky substitution, this run showing a `sync12c` burst rather
  than the death-spam variant seen in earlier sessions). No new failures, no regression.

No crash from item 3's own testing (all three crashes this session trace to item 1's own rig
rounds, in their own isolated sessions). Both game directories' `coop.ini` were reset back to the
plain example template before this session ended; `store_sync` and `party_pool_sync` both stay at
their shipped OFF default.

**No source changed this session** — every item was a live-verification pass against the code
already on `main` as of `cbcc0c4`. The two write-ups above (`docs/research/store-sync.md`,
`docs/research/party-pool-sync.md`) plus this entry are the session's only output — see git
history for the exact commits.

## Rig pass (2026-08-28): post-transition-crash-verdict queue — `party_pool_sync`'s §26 fix
## disqualified by a SEVERE runaway (item 1); `store_sync`'s buy-back check inconclusive again plus
## a NEW blackout+reconnect double-apply/misroute defect (item 2); baseline clean (item 3)

Task: this session's three-item queue, starting from HEAD already equal to `main` at `66430d3`
("Merge: transition crash verdict") — no merge needed. Full detail in each feature's own research
doc; this entry is the session-level summary.

### Item 1 — `party_pool_sync`: see `docs/research/party-pool-sync.md`'s new §27. Summary: ran
§26's own concrete next live step — three rapid host `pooladd g_i_medeqpmnt01`s in `END_M01AA` (no
warp). Did **not** converge to +3: an unbroken cascade of escalating local-add/local-remove
detections on both sides amplified the three requests to **535 units on the host and 550 on the
client** — a ~178-183x overshoot from target, AND a 15-unit cross-instance divergence, before
settling on its own after ~33 seconds with no crash and no loop-breaker trip. Consistent with (but
not mechanically re-proven as) a resurgence of §21's original severe-runaway shape, likely
contaminated by `END_M01AA`'s own ambient item-spawn scripts creating the identical resref
(`g_i_medeqpmnt01`) the test used, colliding with the §20.2 ambient-noise suppression window.
§26's serialized-issuer fix closes a different bug (a dropped request) and does not, on this
evidence, prevent this one. `party_pool_sync` stays OFF, disqualified for a second consecutive live
round — this round's failure is worse in magnitude than §25's undercounting stall.

### Item 2 — `store_sync`: see `docs/research/store-sync.md`'s new "Live pass (2026-08-28, rig
### round)" section. Summary: re-ran the established `tar_m02aa`/`Larrim_Store` recipe (ONE
relocating warp, per the transition-crash verdict). Three clauses re-confirmed clean: no wipe on
open, the host-sell-to-client mirror (~1s latency, confirmed working even with the client's own
store GUI never open), and the infinite-stock buy no-op. The client buy-back clause is INCONCLUSIVE
again — the `tar_m02aa` ambient `duros`/party-select trap did not clear on the client despite 10+
fast retries and a `tp`-away attempt, this time compounded by a genuine combat encounter at the
warp-in point (14 enemies, both PCs took damage but survived — `god` mode already active from the
save). A NEW, previously-undocumented defect was found in the blackout+reconnect clause: a
reconnect after `netsim blackout 8` double-applied an already-mirrored sell on the client, and the
duplicate landed in the CLIENT PLAYER's own inventory rather than the store's stock (`items 99`
confirmed: host's copy shows the sold item at `stack 2`, client's copy shows `stack 1` in the store
plus a separate `stack 1` tagged `yours`) — total unit count matches across instances, but the
allocation is wrong, a misroute rather than a simple duplicate. `store_sync` stays OFF, not flipped
on. No crash this round.

### Item 3 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors (run at the top of this
  session, before any rig work).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed (identical `dinput8.dll` size/timestamp confirmed both sides).
- `dotnet test tests/KotorCoop.Tests --no-build`: **2489/2489 passed**, run with the rig down to
  avoid the documented UDP-port-binding flake — a clean, isolated run.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides (every sync flag at its shipped default): **40 passed,
  9 failed** — squarely the documented 40-42/7-9 baseline, all nine failures matching the
  long-documented known set exactly: the 7 core known failures (`phantom damageable, not a sink`;
  `the peer really loses those hit points`; `a client hit costs the enemy real hit points`; `a
  downed player is reported to the agent`; `the co-op revive stands the player back up`; `every
  engine call signature matches the binary` at 197/199; `a container opened on the client is held
  and shown open on the host`) plus both documented flaky substitutions landing together this run
  (`an item picked up here vanishes on the peer`, `no per-frame spam`) — this exact combination is
  itself a previously-documented precedent, not a new failure shape. No regression, no crash.

Both game directories' `coop.ini` were reset back to the plain example template (shipped defaults,
`auto_start_agent=1` restored) before this session ended; `store_sync` and `party_pool_sync` both
stay at their shipped OFF default — no `coop.ini.example`/`CONFIG.md`/README changes needed since
neither flag flipped.

**No source changed this session** — every item was a live-verification pass against the code
already on `main` as of `66430d3`. The two write-ups above (`docs/research/store-sync.md`,
`docs/research/party-pool-sync.md`) plus this entry are the session's only output.

## Rig pass (2026-08-29): shared-inventory-v2 (`party_pool_sync=2`/`store_sync=2`) queue — BOTH
## disqualified by reproducible live defects on the first attempt; defaults stay at 0; baseline clean

Task: re-run `docs/research/party-pool-sync.md` §27's own recipe against the new mode-2
host-authoritative redesign (`docs/research/shared-inventory-v2.md`), and the established
`tar_m02aa`/`Larrim_Store` store recipe against mode 2. Started from `main` already containing
"shared inventory v2" (`eb98743`); fast-forwarded the worktree, no merge conflicts. Full detail in
`docs/research/shared-inventory-v2.md`'s new §6; this entry is the session-level summary.

### Item 1 — `party_pool_sync=2`: DISQUALIFIED, twice, by an exponential-doubling runaway

Two-instance rig, `END_M01AA` (no warp), three rapid host `pooladd g_i_medeqpmnt01`s per the
queue's own instructions (`coop.ini.example` defaults + `party_pool_sync=2`/`store_sync=2`).
**First attempt** was contaminated by `dialogue_item_sync`/`script_item_sync` (both ON by
default) — a completely different channel (`materialise: gave loot ... (dialogue grant)`)
produced a ~180x overshoot on its own, though both sides converged to the same final total
(82/82). **Second attempt**, `dialogue_item_sync=0`/`script_item_sync=0` isolated to rule that
out: mode 2's OWN client-side pump (log-tagged `party pool v2:`, unambiguous) reproduced a clean
exponential-doubling ramp — 1, 2, 4, 8, 18, 34, 66, 130... — hitting `g_w_blstrpstl001` and
`g_a_clothes01`, items the recipe never touched, alongside the targeted `g_i_medeqpmnt01`. Paused
~2 minutes, then resumed a second doubling ramp unprompted. Final state: host 66 items, client 72
— a real cross-instance divergence, the same qualitative failure shape as mode 1's own §21/§27
runaways. This directly contradicts `shared-inventory-v2.md` §1.2/§2.5's "amplification is
structurally impossible" argument. No crash; both instances stayed `Responding=True`.
`party_pool_sync` stays OFF.

### Item 2 — `store_sync=2`: DISQUALIFIED by two independent defects (a genuine double-sell, and
### a full-stock wipe/restore cascade)

Rig restarted clean with `party_pool_sync=0` (unsafe to run alongside item 1's still-unexplained
runaway — the single-slot cheat channel mode 1's own root cause 7 already documented as
clobber-prone) and `store_sync=2`, `dialogue_item_sync=0`/`script_item_sync=0` kept isolated. ONE
relocating `warp tar_m02aa` (this session's only warp). The documented ambient modal trap cleared
in 4 tries on the host, 1 try on the client — both `openstore Larrim_Store` succeeded, no wipe
between open and the first action. Host sell → client mirror: clean, ~2s latency, no
amplification. **Client buy-back never reached the host**: `buy` on the client engine-confirmed
success, but the host's own agent log showed zero `store v2:` lines for that resref over 2+
minutes — proven by consequence: the HOST was then able to `buy` the SAME unit a second time
(`buy 'g_w_ionblstr01' x1 from store Larrim_Store` succeeded twice), the queue's own explicit
"host re-buy refused" check failing outright — a genuine double-sell, two players each holding a
copy of what should have been one unit. **New, more severe finding**: minutes later, the host's
own store pump wiped then restored nearly the ENTIRE pristine stock unprompted — an 18-entry
`remove` cascade (tagged `store v2:`) followed by a matching `add` cascade ~5-20s later, net
correct except for the legitimately-sold item — the same "virgin baseline reads as zero" shape
`store-sync.md`'s very first live pass marked CRITICAL for mode 1, reproducing independently in
mode 2's own pump. Infinite-stock buy re-confirmed as a clean no-op. Incidental, unrelated
finding: a `progression_sync` gold-ledger integer-overflow — `setgold`/`buy` in sequence produced
`purse: peer 2 contributed +4294962076` / `+0 -3294967077` lines and drove the host's own gold
negative — flagged for its own follow-up, not a `store_sync` defect. Blackout+reconnect not
attempted (not meaningful once the first two clauses already fail this badly). No crash; both
instances stayed `Responding=True`. `store_sync` stays OFF.

### Item 3 — flip defaults: NOT REACHED. Both flags disqualified in item 1/2, so
`profiles/coop.ini.example`, `CoopConfig`/`AgentSettings` defaults, `CONFIG.md`, and the
README/roadmap/research-README rows are all unchanged.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors, run at the top of this
  session against the freshly-merged `main`.
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean NativeAOT win-x86 build, both game
  directories freshly installed (`dinput8.dll` 2,060,288 bytes / identical timestamp both sides;
  both deployed `KotorCoop.Session.dll` confirmed by byte-scan to contain
  `PartyPoolPumpV2`/`StorePumpV2`/`PartyPoolSnapshotPacketV2`). Re-run once more at the end of the
  session (no source changes since) — same clean result.
- `tools/verify.ps1`, fresh two-instance session with `coop.ini` reset to the plain
  `profiles/coop.ini.example` on both sides: **41 passed, 8 failed** — within the documented
  40-42/7-9 baseline range: all 7 core known failures (`phantom damageable, not a sink`; `the peer
  really loses those hit points`; `a client hit costs the enemy real hit points`; `a downed player
  is reported to the agent`; `the co-op revive stands the player back up`; `every engine call
  signature matches the binary` at 197/199; `a container opened on the client is held and shown
  open on the host`) plus one of the two documented flaky substitutions (`no per-frame spam`) —
  `an item picked up here vanishes on the peer` passed cleanly this run. No regression, no crash.
- `dotnet test tests/KotorCoop.Tests --no-build`: **2506/2506 passed**, run with the rig down to
  avoid the documented UDP-port-binding flake.

Both game directories' `coop.ini` were reset to the plain example template (shipped defaults)
before this session ended; `party_pool_sync`/`store_sync` both stay at their shipped OFF default —
no config/README changes needed since neither flag flipped.

**Source changed this session**: `docs/research/shared-inventory-v2.md` only (new §6, the two live
findings above). No production code changed — every item was a live-verification pass against the
mode-2 code already on `main`. This entry plus that write-up are the session's only output.

## Rig pass (2026-08-29): shared-inventory-v2, second round against §7's fixes — item 1 still
## disqualified (a NEW, distinct defect: a client's own reconcile-apply landing gets re-detected
## and re-published as a fresh intent, no exponential doubling this time); item 2 blocked on the
## client half by a `tar_m02aa` environment hazard, but the host-sell-never-mirrors result stands;
## defaults stay at 0; baseline clean

Task: re-run items 1/2 against `main` merged forward through §7 (retire-by-confirmed-snapshot-
movement, cloned-under-lock belief, store-removal debounce, absent-store=unknown). Full detail in
`docs/research/shared-inventory-v2.md`'s new §8; this entry is the session-level summary.

### Item 1 — `party_pool_sync=2`: DISQUALIFIED, by a different mechanism than the prior round

`END_M01AA`, no warp, `dialogue_item_sync=0`/`script_item_sync=0` isolated from the start. Three
rapid host `pooladd g_i_medeqpmnt01`s. No exponential doubling this time — §7's fix for that
specific pathology holds. But after everything settled (confirmed stable across 4+ minutes idle),
the two engines had permanently diverged: net +3 on the host, 0 on the client, for the same key.
Root-caused directly from the client's own log: its detect step fired twice for this key, each time
within 0-2 seconds of that SAME key's own reconcile-apply landing — the client re-detected its OWN
convergence-toward-the-snapshot as a fresh local player action and re-published it as a genuine
intent, which the host (correctly, per its own rules) accepted and applied, corrupting the host's
canonical state. This is the same general class of feedback the design's own §2.5 claims is
structurally impossible, manifesting through a different door than the prior round's doubling. No
crash. `party_pool_sync` stays OFF.

### Item 2 — `store_sync=2`: client half blocked by an environment hazard, host-sell-never-mirrors
### confirmed independently

`party_pool_sync=0` (isolated per the prior round's own precedent), `store_sync=2`,
`dialogue_item_sync=0`/`script_item_sync=0` kept isolated. ONE relocating `warp tar_m02aa` (host
only). Both landed cleanly, no crash, zero liveness anomalies. Host's store GUI opened in 1 retry
and stayed open. The CLIENT's store GUI never opened for the entire ~13-minute session — stuck on
`"another modal screen is up"` through 8 retries of the documented `skipline`/`selectreply
0`/`partyselect ok` loop, despite the client's own PC reading `hp 12/12`, not in combat, and
`standup` confirming it was never itself down. The client's log repeatedly showed `"your party is
down — a peer can still revive you"` (a companion, not the PC) — a different trigger than
`store-sync.md`'s known `tp`-fall hazard (`tp` was never used this round); moving the host onto the
client's own position and waiting 2.5 more minutes did not clear it. Host sell
(`scriptgive`/`sell g_w_ionblstr01`) succeeded at the engine level (store stock 1 -> 2 on the
host's own `items 99`); the client's `items 99` for the SAME object id in the SAME store still read
stack 1 five minutes later — never mirrored. Code inspection shows `StorePumpV2.PumpClient`
returns before reaching its reconcile logic whenever no store at all is open that tick, narrower
than the file's own "reconcile needs no GUI" doc comment for a store never opened even once — a
partial, not fully proven, explanation. Client buy-back and the "host re-buy refused" checks were
not reached. No crash. `store_sync` stays OFF.

### Item 3 — flip defaults: NOT REACHED, same as the prior round.

### Item 4 — baseline: **PASS.**

- `dotnet build KotorCoop.slnx -warnaserror`: clean, against the fast-forward-merged `main`
  (`1e7abca`).
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean both directories; `dinput8.dll`
  byte-identical (2,060,288 bytes, same timestamp) both sides; `KotorCoop.Session.dll` confirmed by
  byte-scan to contain `PartyPoolPumpV2`. Re-run clean at the end with no source changes.
- `dotnet test tests/KotorCoop.Tests --no-build` (rig down): **2511/2511 passed**, zero failures.
- `tools/verify.ps1`, fresh session, plain `coop.ini`: **41 passed, 8 failed** — the exact
  documented baseline set, no regression, no crash.

`party_pool_sync`/`store_sync` stay OFF (mode 0) on both game directories' `coop.ini`, reset to the
plain example template at the end of the session.

**Source changed this session**: `docs/research/shared-inventory-v2.md` (new §8) and this entry
only. No production code changed.

## Rig pass (2026-08-29): ground-item pickup sync — NOT a regression; the "9th failure" from
## `shared-inventory-v2.md` §10.4 is the SAME long-documented `an item picked up here vanishes
## on the peer` / `a client hit costs the enemy real hit points` flaky pair this doc already
## recorded on 2026-08-28 and several times before; likely mechanism pinned this round

Task: `shared-inventory-v2.md` §10.4 flagged `an item picked up here vanishes on the peer` as "a
9th, newly-reproducible failure not in the prior documented set", reproduced identically on two
independent fresh sessions, and asked for a dedicated look. That framing turns out to be
incomplete — this exact check is already named, twice, earlier in THIS file (the 2026-08-28
"item 5" entry above, and the `store_sync`-round entry before it) as one half of a long-running
**documented flaky pair**: `an item picked up here vanishes on the peer` and `a client hit costs
the enemy real hit points` trade PASS/FAIL against each other from run to run, going back well
before this pass. §10.4's round did not cross-reference that history, which is the only thing
"new" about it. This round confirms the pattern is not a regression, and pins a likely mechanism
neither prior round had.

**Ground-item removal/vanish propagation itself is correct**, confirmed by direct code reading
(`src/KotorCoop.Session/Pumps/WorldStatePump.cs`'s `PumpItems`, `src/KotorCoop.Memory/
KotorGameBridge.GroundItems.cs`'s `CollectVanishedItems`, `src/KotorCoop.Session/GroundItemStore.cs`,
`CoopHost.Items.cs`/`CoopClient.Items.cs`) and by six independent manual live reproductions this
round, using `tools/blockpoke.ps1 console dropitem` plus a direct poke of the plugin block's
`PickUpRequest` field (offset `0x280`, the identical test hook `verify.ps1` itself drives) against
a freshly-restarted rig:

- Host drops, client picks up its own copy up: **3 for 3**, full round trip (client detects vanish
  → wire op to host → host destroys its own body) landing in 2.4-6.4 s every time
  (`kotorcoop-plugin.log`: `pickup: player ... told to take object ...` → `item slot N: item ...
  gone` on the client → `item slot N: destroyed ...` on the host).
- `RecentlySyncedItems` (the script-item suppression ledger) and the `_spawnedItemResrefs` resref
  cache (`item-identity.md` §12.2/§13, both named as suspects in this task's own brief) are BOTH
  confirmed, by direct code reading, to never gate `PumpItems`'s removal loop — `RecentlySyncedItems
  .Record` is called strictly AFTER the vanish is already collected, as a pure side-channel write
  consumed only by `ScriptItemGrantPump` elsewhere; the resref cache is written the moment a slot
  is first observed alive and is never read by `CollectVanishedItems`'s own vanish-detection branch
  (`state == ItemStateVanished`). Neither can suppress or duplicate a removal. Pinned with a new
  bench test, `tests/KotorCoop.Tests/WorldStatePumpItemsTests.cs` (`AVanishedSlotForwardsExactlyOneRemoval`,
  `TwoSimultaneousVanishesBothForward`): a fake bridge that goes alive → vanished, with both caches
  warm, yields exactly one `RemoveItem` call, and stops re-reporting once the store (and, in the
  real bridge, the slot) has withdrawn the id — matching `KotorGameBridge.GroundItems.cs`'s own doc
  comment ("announced every pump until the store removes them, harmless because idempotent").

**What `verify.ps1`'s own check actually hits, live.** Replaying its section 15 sequence by hand
against a fresh session reproduced the failure 1 of 4 times (3 successes replaying the mechanism
below, 3 successes with no preceding combat at all — 6 for 6 when nothing precedes the pickup).
The one reproduction is decisive: `kotorcoop-plugin.log`'s own `attack probe` line shows the local
player's action-queue head sitting at `0xFFFFFFFF` (idle — no stuck action) for the entire window
between the earlier attack test and the pickup order, yet `ActionPickUpItem` is dispatched
successfully (`PickUpIssued` increments, `pickup: player ... told to take object ...` logs) and
then never executes — the player never walks to an item measured only ~2 units away, for 50+
seconds, permanently (confirmed by re-checking the slot state and the player's own position via
`console where` well after `verify.ps1`'s 45 s timeout had already elapsed).

The common factor in every verify.ps1 run that hits this: **section 13's own `a client hit costs
the enemy real hit points` check runs shortly before section 15's item test, and stages its fight
by calling `TestDriveController.CloseToMeleeRangeIfNeeded` → `Odyssey.SnapTo`** — the same raw
`ClearAllActionsFn` + `SetPositionFn` teleport `CheatController`'s own `tp` cheat uses (`docs/
CONSOLE.md`: "teleports your own character (no walkmesh check)") — to snap the CLIENT's own PC
into melee range of a distant enemy. This round reproduced the mechanism directly: poking the same
`0x1DA0`/`0x1DA4` test-attack fields (attacker `0x7FFFFFFF`, a live enemy) to force the identical
SnapTo, then breaking off combat and immediately dropping+picking-up an item exactly as section 15
does, failed to complete on 1 of 3 attempts — every attempt WITHOUT a preceding SnapTo succeeded.
This is consistent with, and gives a first mechanism for, every occurrence of this flaky pair this
file has recorded: `a client hit costs the enemy real hit points` is the SAME check that drives the
SnapTo, so a run where it fails to register damage (its own long-documented baseline failure) or
succeeds is plausibly correlated with whether the following item-pickup check's own SnapTo-adjacent
player state lets a later `ActionPickUpItem` actually execute. **Not fully root-caused** — which
specific field `SnapTo`'s teleport leaves the local player's pathfinding unable to read is still
open, and out of this round's budget (would need instrumenting `AIActionPickUpItem`'s own
precondition checks, or bisecting `SnapTo`'s two engine calls against a pickup order run
immediately after each alone) — but the reproduction rate (1/3 replicated, 0/6 unreplicated) and
the exact log signature match (`pickups issued 0 -> 1`, action-queue head idle, item never leaves
the floor) make a genuine engine-state interaction the better explanation than "no explanation",
which is where every prior round that hit this pair left it.

**Verdict**: no code fix applied. `WorldStatePump.PumpItems`/`CollectVanishedItems`/
`GroundItemStore`/`CoopHost.Items.cs`/`CoopClient.Items.cs`/`ItemController` are unregressed and
now have an explicit bench pin. `verify.ps1`'s `an item picked up here vanishes on the peer` /
`a client hit costs the enemy real hit points` pair should be treated as this file's long-documented
flaky pair going forward, NOT as a fresh, dedicated-look-worthy finding each time one lands — this
entry is the cross-reference future rounds should have found. Reordering `verify.ps1` (running
combat-staging checks after the item test, or giving the item test its own `tp`-based position
reset first) would likely reduce the flake rate, but was not attempted this round: the mechanism is
not yet pinned precisely enough to be confident a reorder fixes it rather than moving the same
interaction onto a different pair of checks.

- `dotnet build KotorCoop.slnx -warnaserror`: clean.
- `src/KotorCoop.Plugin/build.ps1 -Install -WarnAsError`: clean both directories, `dinput8.dll`
  byte-identical (2,060,288 bytes) both sides.
- `tools/verify.ps1`, TWO independent fresh-restart sessions this round: first **41 passed, 8
  failed** (item pickup FAILED, matching §10.4's own report exactly — `pickups issued 0 -> 1,
  client slot 0 state 1, host slot still state 1`); second (this entry's closing run) **43 passed,
  6 failed** (item pickup PASSED — `pickups issued 0 -> 1, client slot 0 state 2, host slot
  cleared` — the SAME `a client hit costs the enemy real hit points` FAILED both times, the
  expected flaky-pair shape). Every other failure both runs matches the long-documented baseline
  set.
- `dotnet test tests/KotorCoop.Tests --no-build`: **2,514/2,515 passed** on the full-suite run (one
  failure, `StoreLoopbackTests.ReconnectResyncLeavesUntransactedStoresAlone`, re-run alone
  immediately after — passed cleanly, matching this repository's own documented
  environment-flaky-under-full-suite-parallel-load pattern, unrelated to this round's own new
  test), plus the new `WorldStatePumpItemsTests.cs` (2/2) passing both standalone and inside the
  full run.

`coop.ini` on both game directories was already at the shipped `party_pool_sync=0`/`store_sync=0`
defaults (`profiles/coop.ini.example`) throughout; nothing needed resetting.

**Source changed this round**: `tests/KotorCoop.Tests/WorldStatePumpItemsTests.cs` (new — the
regression pin described above) and this entry. No production code changed.

---

## The new-character join (`join_mode = newcharacter`) — live pass 2026-08-29

Full write-ups: [research/world-adoption.md](research/world-adoption.md)'s 2026-08-29 section
(the mechanism, the two rig corrections, and the blocking finding) and
[research/dialogue-skip-desync.md](research/dialogue-skip-desync.md)'s addendum (spectator
skip). This is the short index.

### Bring it up

```powershell
pwsh tools\demo.ps1 -Stop
pwsh src\KotorCoop.Plugin\build.ps1 -Install
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'D:\Games\swkotor-gog-p2'
pwsh tools\demo.ps1 -ClientNewCharacter     # client stays at the MAIN MENU with no save

# drive the game's own character creation - NO mouse input
$c = (@(Get-Process swkotor | Sort-Object StartTime))[1].Id
pwsh tools\blockpoke.ps1 -ProcId $c chargen newgame
pwsh tools\blockpoke.ps1 -ProcId $c chargen quick 2
pwsh tools\blockpoke.ps1 -ProcId $c chargen accept
pwsh tools\blockpoke.ps1 -ProcId $c chargen          # read the state back
```

⚠ `blockpoke chargen` pokes the plugin's request region DIRECTLY rather than going through
`blockpoke console`. Measured this round: **the agent's console reply channel does not answer
at the main menu** — `blockpoke console 'status'`, a long-standing verb, is equally silent
there and answers normally in-world. Every chargen verb is menu-only, so the console route is
unusable for exactly this feature. That is a pre-existing gap, not a regression.

### Verified

| | |
|---|---|
| `agent run --join`, `join_mode = newcharacter`, at the menu | runs (it used to exit 2) |
| arm handshake agent → block → plugin | `newchar: armed (request 1)` |
| `CharGenSaveHook` / `DialogueSkipHook` byte checks + installs | both, no crash |
| `newgame` | class-selection screen up and rendering (screenshot) |
| `chargen quick <0-5>` | class picked, optimised template applied |
| `chargen accept` | `BeginPlayGame` runs, the new game starts |
| the save hook's timing | fires immediately after `BeginPlayGame` |

### NOT verified, and why (ALL THREE CLOSED by the round below)

* **The character capture.** `SaveCharGenCreature` has not flushed `Temp\pifo.ifo` when it
  returns — 0 bytes at the hook, still 0 after a 400 ms spin, and `Temp\` empty by world-up.
  Blocking; the recommended fix is in world-adoption.md. ⚠ *Root cause, found next round: there
  was no flush to wait for — that function writes `Temp	emp.bic`, never a pifo.*
* **A spectator's skip being swallowed.** `skipline` pokes `CSWSObject+0x1F4` directly and
  never enters `HandleInputEvent`, so it does not exercise the hook. Add a `skipkey` primitive
  that calls `CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)`; `DialogueSkipHook.SwallowedCount`
  is then the oracle.
* **Post-adopt door convergence in the game.** Covered by loopback tests (one of which was
  confirmed to FAIL against the pre-fix single-datagram snapshot); not re-run on the rig.

---

## The new-character join, END TO END, and the spectator skip gag (live pass 2026-08-29, round 2)

Full write-ups: [research/world-adoption.md](research/world-adoption.md)'s "LIVE PASS
(2026-08-29, round 2)" section and
[research/dialogue-skip-desync.md](research/dialogue-skip-desync.md)'s "LIVE-VERIFIED" section.
This is the short index.

### Bring it up

```powershell
pwsh tools\demo.ps1 -Stop
pwsh src\KotorCoop.Pluginuild.ps1 -Install
pwsh src\KotorCoop.Pluginuild.ps1 -Install -GameDir 'D:\Games\swkotor-gog-p2'
dotnet build src\KotorCoop.Agent\KotorCoop.Agent.csproj -c Debug   # demo.ps1 runs the DEBUG agent
pwsh tools\demo.ps1 -ClientNewCharacter

$c = (@(Get-Process swkotor | Sort-Object StartTime))[1].Id
pwsh toolslockpoke.ps1 -ProcId $c chargen newgame
pwsh toolslockpoke.ps1 -ProcId $c chargen quick 2
pwsh toolslockpoke.ps1 -ProcId $c chargen accept
# then wait: the flow saves the new game and pulls the client into the host's world by itself
```

### Verified

| | |
|---|---|
| `join_mode = newcharacter`, chargen to standing in the host's world | **works, unattended**, 2 min 10 s from `chargen accept` |
| the character that arrives | the player's own — live appearance **171** on the client vs **114** on the host, and the host's phantom of it reads 171 |
| the save the flow produces | `saves 000 - QUICKSAVE`, 566,770 bytes, **492 ms** after the request, first attempt |
| object-id convergence | `census: MIRROR 25 id(s): 25 resolve here, 0 do not` |
| doors / companions / gold / xp / journal | `verify.ps1` **41 pass / 8 fail** — the documented baseline, all failures in the known flaky set |
| session admitted at full capability | no `W_SAVE` degraded warning after the reconnect |
| quit, then rejoin with `join_mode = save` off the new save | works in ~1 s, unattended (`SaveMismatch` → adopt → `rejoined 'END_M01AA'`) |
| `skipkey` on the SPECTATOR | `swallowed 0 -> 1 (SWALLOWED - spectator)`, and **nothing advanced on either instance** |
| `skipkey` on the OWNER | `node 29/74 … isDialogDelay 1`, `swallowed 0 -> 0 (passed through - owner)` |
| no crash | event log clean across the whole session |

### Two rig traps fixed, both on the mid-session load path

* **`[msgbox+0x64]` bit 0 is clear in ordinary play** — measured on both instances at once
  (`0x06` on a healthy host twenty minutes in). `FireMidSessionPhase1` now logs it instead of
  refusing; that also removes the `loadgame` failure-9 trap four earlier rounds had to poke
  around by hand.
* **A mid-session adoption empties `Temp\`** — the carried-character stash written there two
  seconds earlier was gone by the time the plugin looked, and the login produced a blank
  character. The stash now lives in the game directory root.

### Still open

* The 4 s settle before the save is ~122 s in practice: `MenuFrame` is the game's own MainLoop
  and the Endar Spire's opening cinematic holds it. Correct, just slow.
* The owner-side `skipkey` was never observed stepping the node index — both attempts landed on
  reply-list nodes where retail's own skip is inert. `skipline`'s earlier pass has that half.

## Swoop racing: root cause, and "one races, the rest wait" (2026-08-29)

Full write-up in [minigames.md](research/minigames.md) §24. What was actually observed on the
rig, in the order it happened.

### Round 1 — the root cause

`tools/demo.ps1`, both instances, a crafted `Tar_SwoopEng` save, `coop.ini delay_probe = 1`.
Ran §22's own recipe verbatim (`travel tar_m11aa`, `tp 65.0 76.0 0.12`, `talk GadonThek112`) and
got its own six-node chain, then read the new probe:

- **ZERO `delayprobe: DelayCommand hasCaller=` lines** anywhere in the conversation window.
  `ExecuteCommandDelayCommand` was never called — so the "the `DelayCommand` never dispatches"
  claim §14-§22 spent eight rounds on had nothing to dispatch.
- **One** dialogue node action script ran for the whole conversation:
  `delayprobe: dialogue node script 'k_act_talktrue' runs as self=0x4` — and `k_act_talktrue` is
  `EntryList[22].Script`, the "already talked to" branch, not entry 17's race branch.
- The delayed-event machinery was demonstrably healthy the whole time: **1219 enqueues, 1076
  dispatches, 0 discards** in the same session.

Cause: `tar11_gadon112.dlg`'s starting entry 81 is gated by `k_ptar_zaerracex`, which reads the
boolean global **`Tar_SummonGadon`** — not `Tar_SwoopEng`. Then `setglobal Tar_SummonGadon true`,
`talk GadonThek112` and three reply picks (`selectreply 0` at entry 6, `1` at 11, `1` at 14) ran the
race chain first try, with the plugin loaded:

```
10:01:41.220  delayprobe: dialogue node script 'k_ptar_racecs0' runs as self=0x2
10:01:49.231  delayprobe: dialogue node script 'k_ptar_racecs1' runs as self=0x2
10:01:49.232  delayprobe: DelayCommand hasCaller=1 self=0x2
10:02:08.240  delayprobe: dialogue node script 'k_ptar_bekrace' runs as self=0x2
10:02:08.241  delayprobe: DelayCommand hasCaller=1 self=0x2
```

and `where` then read `module tar_m03af`. **Not a mod bug, and no debugger was needed.**

The same round found the real co-op problem: the follow-the-leader warp took the client onto the
race track (`tar_m03mg`) and the client process died there.

### Round 2 — the lock, the follow refusal and the rewards

Shipped defaults (`minigame_lock_peers` not present in either game directory's `coop.ini`, so the
ON-by-default path is what ran). Host warped to `tar_m03mg`.

| check | result |
|---|---|
| host enters a real swoop minigame | PASS — `minigame: entered`, then `minigame: kind settled to Swoop` (the kind fills ~100 ms after the pointer, which is why §3's offset looked wrong) |
| client is locked, and told why | PASS — agent: `minigame lock: Lanik started a minigame`; plugin: `minigame lock: a peer is racing — this player is held until it ends` |
| client is NOT warped onto the track | PASS — both follow gates refused (`not following the announced transition to 'tar_m03mg'` and `not following to 'tar_m03mg': a peer is in a single-seat minigame there`); `where` read `END_M01AA` on the client while the host read `tar_m03mg` |
| reward globals cross while the racer is in the minigame | PASS — `Tar_SwoopStatus` 3, `Tar_SwoopRaceCounter` 2, `Tar_SwoopRace` true and `TAR_SWOOP_MSEC` (byte 200 / signed -56) all read back already-set on the client |
| the lock releases when the race ends | PASS — host `minigame: exited` 11:56:11.028, client `the minigame ended — control is yours again` 11:56:12.875 (~1.8 s, the module-load settle plus one heavy pump) |
| no crash | PASS — both instances survived two relocating warps |

**Still NEEDS-LIVE**: the keyboard half of the input lock. `MinigameLockController` uses the same
two levers `SoloSegmentSpectateController` does (`creature+0xE8` commandable = 0 plus
`HoldCreatureStill`), and neither has ever been watched stop a real physical WASD press, because
injected keystrokes do not reach this game. What IS verified is the consequence that mattered: the
follow-warp onto the single-seat track is refused, and the waiting player is told what is
happening instead of standing in an unexplained frozen world.

**Rig hazard worth reusing**: this round's first attempt showed every agent-side line firing while
the client's PLUGIN log stayed silent — a concurrently-running agent had installed its own
`dinput8.dll` over the second game directory between this session's install and its launch. The
tell: the client's log had no `delayed-event probe armed` line while the host's did, and the two
installed DLLs differed in size (2,063,360 vs 2,072,064). Compare both installed DLLs before
trusting a negative plugin-side result.

- `dotnet test tests/KotorCoop.Tests`: **2560/2560**.
- `tools/verify.ps1` after the pass: **40 passed, 9 failed** — inside the documented ~40-42/7-9
  baseline, every failure in the long-known set.
- `coop.ini delay_probe` was removed from both game directories at the end of the round; it ships
  OFF and nothing else in either file was touched.

## Host loss: a client releases every lock and rejoins on its own (2026-08-29)

Full write-up in [disconnect-reconnect.md](research/disconnect-reconnect.md) §11. Two-instance
rig, `demo.ps1`, END_M01AA, Trask recruited. Shipped defaults (`host_loss_release` ON,
`host_loss_timeout_ms` unset — keeps the built-in 5000ms).

The client starts its OWN conversation with Trask (`startdialog end_trask` — dialogue is
initiator-led, so this does not depend on the host), then the host's `swkotor.exe` is killed
outright. Because the agent's own "dies with its game" watchdog tears the host's agent down the
instant the game process disappears, this exercises the fast `HostBye` (`PacketType.HostBye`,
70) path, not the 5 s silence fallback.

| check | result |
|---|---|
| every plugin-side lock releases | PASS — block state sampled every second: `t+4.9s command 1 remotes 1 phantoms 1` → `t+6.1s command 0 remotes 0 phantoms 0` |
| the release is announced | PASS — agent: `* Lanik left the game`, `* co-op: reconnecting...`; plugin: `[11:02:51.184] session: released - undoing what it did to this game`, followed by the companion-back-to-player-faction, death-suppression-lifted, interaction-locks-dropped and roster-is-yours-again lines, all firing end to end for the first time on a real (not manual) disconnect |
| the client keeps playing solo | PASS — companion control, dialogue and doors all answered normally with the host gone |
| the client's own reply list survives | PASS — the spectator-reply-list-rendering baseline (host-led, client joins at t=4) held node-sync for all 15 paired samples, confirming this path is unaffected by the change |

The host's own side of the question — a CLIENT vanishing only ever releases what THAT client was
holding, never the host's or another player's locks — is already correct by construction and was
not re-tested this round. `host_loss_release=0` restores the pre-2026-08-29 behaviour (every lock
held until a manual `disconnect`).

## Companion follow-AI stall: the runtime detector, live (2026-08-29, round 105)

Full write-up in [party-reconciliation.md](research/party-reconciliation.md) Round 105. Root
cause named precisely: the engine's own `PathWaitDest` waypoint-reached test is a STRICT compare,
and at the end of a leg the recorded trail node collapses onto the follower's cached prior
position — so the compare reads a hard 0 and is arithmetically incapable of ever passing.
Reproduced 4/4, including a leader teleported 16.5 units away that the companion never recovered
from on its own.

`FollowStallRule` (a runtime wedge detector, pure + unit-tested) plus a `followstall <slot>
[method]` dev verb A/B'd all six candidate recoveries against the same reproduced wedge in one
session:

| method | live result |
|---|---|
| `1` phasereset | no effect — re-wedged identically |
| `2` followleader | no effect |
| `3` positionparty (`CSWPartyTable::PositionParty`, the same call a module transition makes) | **recovers** — 16.5 units behind to 0.88 units, in one call |
| `4` priortie | real walking, then converges back onto the tie the strict compare rejects |
| `5` destnode | 0.76u the wrong way |
| `6` priorpush | 1.6u of progress, then a new frozen shape |

Only `3` (`positionparty`) actually recovers the companion, so it ships as the default
(`party_follow_stall_recovery=1`, `party_follow_stall_recovery_method=3`). Acceptance run: five
reps, every wedge recovered on the FIRST attempt (including two at 8+ units behind), never more
than 1/5 of the per-module anti-spin budget spent; a real door transition (`end_door10` →
`end_m01ab`), two reconnects, and `verify.ps1` all clean at baseline (41/8), zero false
positives across the whole battery. It RELOCATES the companion rather than walking it back — the
engine's own long-distance pathfinding limit underneath is a separate, still-unfixed issue. A
mirrored companion's follow-AI still does not run at all while mirroring holds, by design — this
fix targets only the moment the wedge is detected, not that structural fact.

## A peer looking like Trask after a warp: both root causes closed (2026-08-29)

Two independent defects behind the "second Trask standing next to the party" and "my partner IS
Trask" reports, both fixed and measured live. Full write-ups in
[name-plates.md](research/name-plates.md) ("The peer's LOOK has the same two halves as its name"
and "The census leak") and [enemy-materialisation.md](research/enemy-materialisation.md) §7.

**The phantom's client twin** — the engine-rendered body, not the server-side object the mod
writes — was left dressed as the bare `k_coop_phantom` blueprint after a re-spawn (a module
transition, a `warp`, any transition), because the mod decided "already correct" from the server
field alone while the twin did not exist yet at the moment it was written. Fixed by
`PeerIdentityRule` re-asserting the look on the twin, per body, until it lands.

**The census leak** — a companion whose party-FOLLOW pointer (`+0x4C0`) had not yet been
(re)attached after a transition read as an ordinary hostile stranger and went out on the ENEMY
channel; the receiving side, which cannot resolve that runtime id (party bodies are spawned by
the party table at load, not placed in the GIT), built a stand-in wearing the companion's own
appearance. Measured live on the rig, `end_m01ab`: Trask (appearance 378, party-table roster slot
0) read `+0x4C0 == 0` for **5.5 seconds** (fifteen consecutive samples) on one run, 4.9s on a
second. Fixed on both sides by `PartyCensusRule`: census membership now comes from the party
TABLE's roster, not the follow pointer, and a mirror refuses to materialise a body for one of its
own roster ids or for anything standing on one of its own companions wearing that companion's
look — confirmed with the receiving side logging the refusal by name
(`materialise: REFUSING owner 0x127 (appearance 378) - it is one of OUR party roster bodies`).
Stand-ins get the same per-body dressing latch phantoms use, so a stand-in that IS legitimately
built (a genuine enemy, never one of the local roster) is no longer left undressed either.
Every icon surface that could theoretically carry a portrait (minimap arrow, M-screen pin, party
bar, name plate) was checked explicitly and cleared as structurally incapable of causing this —
the defect was always the enemy-materialisation channel, never an icon-rendering one.

## Steam: a Steam client in a session with a DRM-free host (2026-08-29)

The first live run of this mod on the Steam build. Full write-up and every log line:
[research/steam-support.md §18](research/steam-support.md). What matters for this checklist:

**The rig can now be run cross-store, and the ordering is not the usual one.** A Steam install
cannot be a second instance — `agent launch --instance 2` renames the mutex successfully and the
Steam client still refuses the second copy. So **launch the Steam copy first** (through Steam,
`steam://rungameid/32370`; it takes the plain `swkotor` mutex), then `agent launch --instance 2`
on a DRM-free copy. `demo.ps1` cannot do this as-is — its instance 1 is the one that loads a save
and its instance 2 is the one that gets `--instance` — so a cross-store pairing is driven by hand:
`agent load --pid <each> --load-save 1`, then one `agent run --host` and one `agent run --join`.

The Steam instance also **re-spawns its own agent** if you kill one, because `auto_start_agent` is
read from coop.ini at plugin startup: editing coop.ini while the game is already up does not
disarm it, and that stray agent holds the attach so your own `run --pid` refuses. Set the key
before launching, or restart the game after editing.

**The oracle for the executable check is a pair of lines and one absence.** Each agent prints, at
startup:

```
GOG   content digest: exe image C6962619, 509 appearance rows, 2 override file(s), 49265 tlk entries [exe file 8B8EC44D (DrmFree)]
Steam content digest: exe image C6962619, 556 appearance rows, 2665 override file(s), 49349 tlk entries [exe file 220AC31D (SteamStub)]
```

The `exe image` values must match and the `exe file` values are expected NOT to. The absence that
matters is `executable identity unchecked` — if that note appears, the check did not run and a
"it connected" result proves nothing about the executables. In this run it did not appear on
either side, so the check was active and passed.

**Verified in one session:** connect (`* Malak joined the game`), mutual presence
(`phantoms 1  remotes 1`, `peers-here 1` on both), a module transition with the host's world
transferred to the Steam client (`* world sent (663,173 bytes)`), and both instances ending in
`end_m01ab` at the same coordinates. The console answers on the Steam process through the
`FuncInterp` detour (`where` → `module END_M01AA  at 20.0 22.2 -1.3  hp 12/12`), and the block is
found at version 60 by the usual signature scan.

**Still not verified:** a real DOOR crossing with a Steam client in the session. Three
`usedoor end_door10` attempts opened the door and stalled the walk 2.7 m short of it
(`No transition happened - the door is still an obstacle, or the path stopped short`), and
`walkto` did not move the PC — both are the known injected-input/pathing limits described above,
on the DRM-free host, and neither is store-specific. The transition that WAS verified went through
`warp`, which travels through the session the same way.

**Steam invites, single-machine half:** with a borrowed `steam_api64.dll` beside the agent (for
the smoke only — never committed, never packaged), a GOG host published its connect string:
`steam: ready as app 32370 (ISteamFriends via SteamAPI_SteamFriends_v018)` and
`steam: friends can now join this session (kc1/<public-ip>:27140)`. The receive half
(`GameRichPresenceJoinRequested_t`, callback 337) and `InviteUserToGame` need a second machine and
a second Steam account, and remain untested.

## Rig pass (2026-08-29, POOL4): `party_pool_sync` and `store_sync` both FLIPPED ON at mode 2, and the host's warp crash fixed at the root

Five rig cycles, `tools/demo.ps1`, both game dirs `build.ps1 -Install -WarnAsError`'d and
SHA-256-verified after every install (publish output, host and client `dinput8.dll`
byte-identical each time; `KotorCoop.Session.dll` and `KotorCoop.Agent.exe` identical across both
installs). Both installs' `saves\000002 - Game1` verified file-by-file identical before every
launch.

### Item 1 — `party_pool_sync`: one more real defect found on the SHIPPED combination, fixed, then clean

The point of running the recipe on shipped defaults rather than the isolated ones every prior
round used (`dialogue_item_sync=0`/`script_item_sync=0`) was to check the combination that would
actually ship. It was NOT clean, and the mechanism is worth writing down because it involves a
channel nobody had connected to this one:

```
client 19:00:16  party pool: local add of 1x 'g_w_blstrpstl001' detected, requesting (serial 1)
client 19:00:16  party pool: local add of 1x 'g_a_clothes01'   detected, requesting (serial 2)
host   19:00:17  party pool v2: submitting add of 1x 'g_w_blstrpstl001' -> added to the party pool
host   19:00:17  party pool v2: submitting add of 1x 'g_a_clothes01'   -> added to the party pool
host   19:00:18  script item grant: 2 new item(s) in the local PC's inventory outside dialogue:
                 g_w_blstrpstl001, g_a_clothes01
client 19:00:18  dialogue grant: received 2 item(s) from peer 1, queueing
client 19:00:18  materialised loot: ... gave 'g_w_blstrpstl001' to 0x7FFFFFFF
client 19:00:19  materialised loot: ... gave 'g_a_clothes01'   to 0x7FFFFFFF
```

A party-pool add lands in the PC's OWN item repository (shared-inventory-v2.md §15.1's
`CreateItemOnObject` -> `AcquireItem` -> `GetItemRepository(creature, 1)` chain), so
`ScriptItemGrantPump` — which exists precisely to catch an item a SCRIPT put in the PC's
inventory — read our own pool applies as script grants and re-broadcast them, and the peer
materialised a second copy through `giveloot`. `ScriptItemGrantPump`'s own class doc already names
this exact class of item ("a peer's own grant applied via giveloot ... must never be re-broadcast
as if a script had just created it"); the party-pool channel was simply never wired into the
`RecentlySyncedItems` ledger that says so. Closed by `CoopAgent.NoteLocalPartyPoolAdd`, fed from
both the automatic v2 drain and the human `pooladd` verb.

Rebuilt, reinstalled, hash-verified, relaunched, same recipe:

| clause | measured |
| --- | --- |
| the echo | **gone** — zero `script item grant` lines and zero `materialised loot` lines on either side across the whole connect window |
| three rapid host `pooladd g_i_medeqpmnt01` | client mirrored 2 -> 4 -> 5, one apply per deposit, `published=0` on every client trace line |
| spurious client publishes after connect | **zero** |
| cross-instance totals (`items 99`) | host **61 of 61**, client **61 of 61** — identical |

### Item 2 — `store_sync`: three clean sessions in a row

Module `tat_m17ac` (Yuka Laka), `store_sync=2`, `party_pool_sync=0` (§6.3's precedent), a fresh
connect each time. Every session ran the same recipe: client `popmodal`/`closegui`/`openstore
k_ptat_yukastore`, client buy, client sell-back, host re-buy, second host re-buy, close both GUIs,
155 s idle.

| clause | session 1 | session 2 | session 3 |
| --- | --- | --- | --- |
| client `openstore` after `popmodal` | ok | ok | ok |
| client's first live scan vs the host's snapshot | agreed on every key (`live reseed`, `published=0` everywhere) | agreed | agreed |
| client buy | `published=-1` once; host applied `remove of 1x` once | same | same |
| **§15.5's re-insert** | **none** | **none** | **none** |
| client sell | `published=1` once; host applied `insert of 1x` once | same | same |
| host re-buy | succeeded exactly once | same | same |
| host re-buy, second time | refused: `no stock matching that resref` | same | same |
| 155 s idle, engine store writes | host +0, client +0 | +0 / +0 | +0 / +0 |

Session 1's full ledger, as an example — every write accounted for, one per real transaction:

```
client 19:17:12  store: local remove of 1x 'g_i_drdshld005' ... requesting (serial 1)
host   19:17:13  store v2: submitting remove of 1x -> removed from store 0x1A
client 19:18:13  store: local add of 1x ... requesting (serial 2)
host   19:18:14  store v2: submitting add of 1x -> inserted into store 0x1A
client 19:19:16  store v2: submitting remove of 1x -> removed from store 0x1A   (mirroring the host's re-buy)
```

For contrast, the same moment on the pre-fix build (§15.5) inserted `g_i_drdshld005` back into the
store 2 s after the buy and removed it again 3 s later — two spurious engine writes for one
transaction.

### Item 3 — the host's warp crash: fixed, and the fix is live

`CSWGuiInGameEquip::UpdatePortraits+0x48E` was 6/6 fatal to the HOST on any relocating warp. The
cause is `party_follow_stall_recovery`'s own `CSWParty+0x00` member-count restore, not
`party_together_fix`'s clamp — see docs/research/transition-crashes.md for the full attribution
and the fix.

**Five relocating warps across four sessions with all five party keys at their shipped ON
defaults, zero faults:**

| # | destination | config | result |
| --- | --- | --- | --- |
| 1 | `tar_m02aa` | shipped defaults + `party_pool_sync=2` | both instances alive, both in `tar_m02aa` |
| 2 | `tat_m17ac` | shipped defaults + `store_sync=2` | alive |
| 3 | `tat_m17ac` | shipped defaults + `store_sync=2` | alive |
| 4 | `tat_m17ac` | shipped defaults + `store_sync=2` | alive |
| 5 | `tar_m02aa` | `coop.ini` copied verbatim from `profiles/coop.ini.example` | alive; host party table repopulated correctly in the new module (`companions 1`, member object id `0x98`) |

`kotorcoop-plugin.log` on the host: **0 occurrences of `6B5FEE`** across every one of those
sessions, against a signature that was previously deterministic.

**The real-door clause was NOT exercised.** `usedoor end_door10` reported
`walking through door 0x4A (3.3m) -> 'end_m01ab'` and then the documented
`600 frames after the walk the player is 3.1m from 0x4A ... No transition happened - the door is
still an obstacle, or the path stopped short` on both attempts (from `tp 47.4 116 -0.04` and from
`tp 50.0 118.0 -0.04`). That is a staging-verb/pathing limitation, not a transition failure — no
transition was ever started, so nothing about the fix was tested by it. Both instances stayed
alive and fault-free throughout. The five warps above are the evidence; a real-door confirmation
is still open.

## Host in-world reload with both inventory channels on: 6/6 clean on block version 64 (2026-09-02)

The first live run of `RejoinTrigger.HostReload` (the host reloads a save while standing in the
world; the client follows) with `party_pool_sync=2` and `store_sync=2` on. The rounds leading up
to it found seven distinct ways a reload fabricated pool content and one client hang; all are
in docs/research/shared-inventory-v2.md §17 (the pool side) and docs/research/session-save.md's
2026-09-02 entry (the rejoin side). This is the result on the fixed build.

Rig: `pwsh tools/demo.ps1 -Save 1`, END_M01AA, host GOG install + client second copy, both
agents with `party_pool_trace=1`; six `loadgame 0` on the host ~55 s apart, `spawnring.ps1` and
`items 80` between cycles.

| cycle | host reload | client | host spawn ring | host snapshot | client first sight | pool/grant traffic |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 18:33:39 | one `HostReload` rejoin (18:33:44), `Responding: True` | none | withdrawn, republished `adrnaline003=1, medeqpmnt01=2, blstrrfl001=1` | `snap=1/2/1 reconcile=0` | none |
| 2 | 18:34:35 | one (18:34:38) | `0x12B g_i_medeqpmnt01 cheat 0 count 1` — ambient merged into the loaded stack | `medeqpmnt01=2` | `snap=1/2/1 reconcile=0` | none |
| 3 | 18:35:29 | one (18:35:32) | `0x12B g_i_medeqpmnt01 cheat 0 count 1` | `medeqpmnt01=2` | `snap=1/2/1 reconcile=0` | none |
| 4 | 18:36:28 | one (18:36:32) | none | same three keys | `snap=1/2/1 reconcile=0` | none |
| 5 | 18:37:25 | one (18:37:30) | none | same three keys | `snap=1/2/1 reconcile=0` | none |
| 6 | 18:38:21 | one (18:38:25) | none | same three keys | `snap=1/2/1 reconcile=0` | none |

Cycles 2 and 3 are the version-63 dirty case (shared-inventory-v2.md §17.7): the module's
world-up script merged one ambient medpac into the loaded stack of two, and the hook reported
the SURVIVING id. Version 63 baselined the whole stack, hid the key, and the client re-deposited
its two; version 64 baselines the reported `count 1`, the snapshot stays at 2, nothing moves.

Across the whole run, on both agents: 0 `added` / `removed` / `local add` / `script item grant`
/ `dialogue grant: received` lines, 0 exceptions, 0 `pump tick failed`; the host's physical pool
at the end is exactly the save's (`0x12A g_i_adrnaline003 ×1, 0x12B g_i_medeqpmnt01 ×2, 0x12C
g_w_blstrrfl001 ×1`); the §16.6 connect-time first-sight publish did not occur on this launch.
Plugin logs on both sides: only the benign `0x406D1388` thread-naming first-chance entries.

Test suite: 3000/3000.

Still open from the same recipe: the full wipe cycle (client `down` first, host `down 0` +
`down`, game-over release, `WipeRecovery` rejoin) on this build, and recipe step 5
(`client_temp_death_suppress=1` on the client) — death-handling.md §6.5.

## Full wipe cycle with `client_temp_death_suppress=1`: 1/1 clean after the guard fix (2026-09-02)

Same rig and build as the entry above (block version 64, both inventory channels on), client
`coop.ini` `client_temp_death_suppress=1`. Recipe: client `down 0` + `down`, host `who`, host
`down 0` + `down` back to back, wait for the release, wait for both `away`, `Agent.exe load
--pid <host> --load-save 1`, wait for `rejoined`, settle 45 s, `items 80` on both.

- Cycles W1–W3 never released: with the guard on, the client's `TempDeathUpdate` — where the
  engine's all-dead scan asks for the game-over — was dark, so the client never raised
  `GameOverSerial`, the agent never published `FlagPartyWiped`, and the host's own
  `TempDeathUpdate` auto-revived the host's party 5 s after its companion went down. Fixed in
  the plugin (`DeathController.MaintainClientTempDeathGuard` + `Odyssey.IsWholeLocalPartyDown`
  on the CSWParty live view); mechanism and the failed party-table attempt in
  death-handling.md §6.6.
- W4 on the rebuilt plugin: client down 19:15:10 (`death: game-over suppressed; the session
  decides` — the synthesised ask; host `who` → `[party down]`), host down 19:15:16, both agents
  `every player is down; releasing the game-over screen for everyone` in the same second,
  client plugin `letting TempDeathUpdate end the game on this client`, both `away` by 19:15:32,
  host reload at 19:15:38, client `session-save: starting rejoin (HostReload)` → `rejoined
  'END_M01AA'` 19:15:42 (one rejoin), host `withdrawing the published snapshot` → `host
  snapshot 3 key(s)` 19:15:44, client firstSight `snap=1/2/1 reconcile=0`, both pools = the
  save's three stacks, both `Responding: True`, 0 pool/grant lines, 0 faults.
- Also on this build, with the guard on: the client stayed down 7+ min with no auto-revive and
  was revived by the host walking in (`Lanik revived you (3.2 units …)`, `topped up to 12 hp`).

The rejoin trigger was `HostReload` (`HostWorldReplacedRule`), not `WipeRecovery`; both feed the
same rejoin and whichever fires first wins.

## Full wipe cycle, continued: the CSWParty vote failed after a rejoin (W5), fixed, then 2/2 clean and one pool fabrication closed (2026-09-02, evening)

Same recipe as the entry above. Full detail in death-handling.md §6.7.

- W5 (second cycle on the same rig, i.e. after W4's rejoin): no release. The client's
  `IsWholeLocalPartyDown` walk of the CSWParty view found `count 2` = the host's mirrored
  companion at hp 1 (min-1-hp pinned, host-health mirrored) and a stale id from the previous
  world (now an item), with the PC's sentinel at member[2] beyond the count — the walk never
  reached the PC, the client never voted, the host auto-revived at +4 s, `Agent.exe load`
  refused ("already in the world; nothing to load"). The view was `count 1` = the PC on the
  fresh connect, which is why W4 passed once.
- Fix: the client's vote is the PC alone (`Odyssey.IsDowned(player)`), and on release the
  plugin calls `CClientExoApp::RunDeathSequence` (0x005EDC40) itself, latched once per wipe —
  new line `death: every player is down and the session released the game-over; running the
  engine's death sequence on this client`. `IsWholeLocalPartyDown`/`IsServerCreature` removed.
- W6 (fresh launch, host 45824 / client 23940): client down 19:40:24, host down 19:40:40, both
  agents released in the same second, client plugin ran the death sequence at 19:40:40.356,
  both `away`, host reload 19:41:26, client `session-save: starting rejoin (WipeRecovery)` →
  `rejoined` 19:41:34 (one rejoin), world up on the client 19:41:40.0, firstSight
  `snap=1/2/1 reconcile=0`. BUT at 19:41:53 the client published `script item grant: …
  g_w_shortswrd01` — its own carried sword — and the host pooled it (host pool 4 keys, client
  reconciled a second sword 0x137 next to its own 0x128).
- Root cause: the client's player resolved 3.5 s before the plugin acked the world on screen,
  a lookup in that window ran the post-epoch inline item sweep against a heap still being
  filled (clothes 0x127 found, sword 0x128 not), and that sweep was trusted for 15 s — long
  enough for the settled grant baseline to be read through it; the next background sweep
  found the sword and the diff called it new. Fix: `ItemSweepProvenanceRule` (Memory) — a
  sweep taken while `IsWorldBetweenLives` is served but provisional, and the first lookup in
  the settled world rebuilds inline first. `ItemSweepProvenanceRuleTests` pins it.
- W7 (fresh launch on the fixed agent, host 50820 / client 23780): client down 19:56:21, host
  down 19:56:29, released in the same second, client death sequence 19:56:29.557, host reload
  19:56:54, `starting rejoin (WipeRecovery)` → `rejoined` 19:56:58, world up 19:57:02.2,
  firstSight `snap=1/2/1 reconcile=0`, `items 80` at 19:57:46/48: both pools = the save's
  three stacks (host 0x12A–0x12C, client 0x12C–0x12E), both `Responding: True`, 0 grant lines,
  0 faults in either plugin log.

Death pipeline with the guard on: 3/3 clean cycles since the vote fix (W4 on the previous
build, W6, W7), across both rejoin triggers. Still not run with a hostile visible at the wipe.

## N-slot local combat live: two Sith on one client at once, two cross-side kills, and the immortal Sith the exclusion left behind (2026-09-02, evening)

Rig: host `C:\GOG Games\Star Wars KOTOR (GOG)`, client `D:\Games\swkotor-gog-p2`, both on
END_M01AA save 1, block version 64, `local_combat=1`, `local_combat_slots` at its default 3 (the
rig inis do not set it). Client PC `god on` for both passes (the same testing artifact the
2026-08-22 round 55 pass documents — it trips the absorb watchdog, and it is not what is under
test here). The two hostiles are the pair of `end_gensith` (12 hp, blaster rifles) at
(43.96,62.92) and (47.56,60.54); the client was `tp 45.5 57 -1.28` between them.

**A wrong first stage, recorded so nobody repeats it.** The first attempt used `end_sith2` 0x14 /
`end_sith3` 0x17 at (46,33) — both read `rep(creature,player)=2`-hostile in the census and the
`attack` verb took the order — but the PC's attack ran ONE round and dropped
(`round(active=0)`, `head=0xFFFFFFFF`), neither Sith moved or fired, and no `local_combat: slot`
line ever printed. Those two are the cutscene pair the corridor script animates (0x17 sits at
hp 1 in the save); they are not a fight. The two `end_gensith` further down the corridor are the
real first encounter and behaved like one.

### Pass 1 (20:29, the shipped build): both slots armed, both Sith firing, one kill — and a zombie

    [20:29:50.352] local_combat: slot 1/3 selected 0x3B (priority=Proximity, dist=4.09, engageRange=11.0)
    [20:29:50.352] local_combat: slot 2/3 selected 0x38 (priority=Proximity, dist=6.11, engageRange=11.0)
    [20:29:50.353] local_combat: slot 1/3 armed id=0x3B reason=proximity (timeout 25s)
    [20:29:50.353] local_combat: slot 2/3 armed id=0x38 reason=proximity (timeout 25s)
    [20:29:52.079] combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently locally-fighting creature ...)
    [20:30:05.723] combat ground-truth: local PC took 2 damage, attacker=0x38 (the currently locally-fighting creature ...)

Two script-fired `+0x260` creatures fighting the same client engine at once, both landing real
local hits, the agent's filter removing both from the mirrored census
(`excludedIds=[0x3B,0x38] enemies.Count=25 filtered.Count=23 (removed 2)`). §11's first
NEEDS-LIVE item — N concurrent fights as a load pattern — holds. `blockpoke attack 0x7FFFFFFF
0x3B` then killed 0x3B on the host from forwarded damage alone (`applied 6 ... owed 6`, `9 ...
owed 15`, `8 ... owed 23` against 12 hp; `materialised loot: captured 2 item(s) off 0x3B's
corpse`), and five seconds later the corpse faded (`liveness: 1 creature(s) newly gone this
pass: 0x3B`; the host's census stopped listing it).

The client never heard any of it. 0x3B was excluded from its feed for the whole window, its
local copy is min-1-hp pinned and cannot die on its own, so the slot ran out its 25 s
(`released id=0x3B reason=timed out` at 20:30:40) and was handed back to normal mirroring — which
had nothing to say about an id the owner no longer publishes. What was left was an immortal Sith
at 12/12 hp shooting the client's PC forty seconds after the host looted its corpse:

    [20:30:40.675] combat ground-truth: local PC took 2 damage, attacker=0x3B (unrecognized attacker id (not driven, not a phantom, not our own apply))
    [20:31:10.183] combat ground-truth: local PC took 2 damage, attacker=0x3B (unrecognized attacker id ...)

Round 55 (shared-state-audit.md) had seen the milder shape of this — `0x24` timing out after the
host had already killed it — and called it benign because "the next normal census pass reads the
owner's Dead flag". That is true only while the corpse still exists on the host; a Sith whose
inventory is all undroppable fades in ~5 s, well inside the 25 s window, and then no Dead flag
ever comes. With `liveness_sync=0` (its default) nothing else retires the body either.

### The fix: the owner's verdict passes through the exclusion

`LocalCombatExclusionRule` (Session, pure, `LocalCombatExclusionRuleTests` x9) replaces the
plain drop in `CombatPump.PumpEnemies`. An excluded id the owner lists ALIVE is still dropped —
that is the exclusion. An excluded id the owner lists DEAD passes through
(`OwnerReportsDead`). An excluded id the owner has STOPPED listing — after the rule saw it in
the feed at least once while excluded, unlisted for 2 s since that sighting, and only while the
feed is below its 32-entry cap so a capacity shed is never read as a destroyed body — is
republished from its last seen entry with `Dead` set (`OwnerNoLongerLists`). The plugin's
`DriveOne` sees an ordinary Dead entry for an id holding a slot, releases the slot
(`reason=owner reports dead or gone`) and falls through to the ordinary death path: `Forget`,
then `CombatController.KillOnce` unpins and kills the local body through the same
corpse/loot/XP pipeline as any mirrored death. Nothing changes on the wire or in the block.

### Pass 2 (20:41, the fixed build): two kills, both retired within 300 ms of the host's death

    host   [20:41:33.284] combat: applied 7 of a peer's damage to 0x3B (owed 7)
    host   [20:41:42.717] combat: applied 8 of a peer's damage to 0x3B (owed 15)
    host   [20:41:42]     materialised loot: captured 2 item(s) off 0x3B's corpse, publishing
    agent2 [20:41:43]     local combat: excluded 0x3B let through as dead (OwnerReportsDead)
    client [20:41:43.013] local_combat: slot 1/3 released id=0x3B reason=owner reports dead or gone
    client [20:41:43.069] combat: killed 0x3B on the owner's authority

    host   [20:41:53.905] combat: applied 6 of a peer's damage to 0x38 (owed 16)
    client [20:41:54.140] local_combat: slot 1/3 released id=0x38 reason=owner reports dead or gone
    client [20:41:54.203] combat: killed 0x38 on the owner's authority

In between, 0x38's original slot 2 timed out at 20:41:48.574 and the very next scan re-armed the
same still-alive creature into slot 1 (`selected 0x38 ... dist=1.80`) — the re-inclusion path
under N slots, live. No `unrecognized attacker` line after either kill; both `end_gensith` bodies
gone from both instances' creature lists a few seconds later (only the stale server-side scan
entries `creatures.ps1` is known to keep remain). **Kill XP on a cross-side kill (roadmap item 3)
agreed without any merge being needed: the PC read xp 150 before and 300 after on BOTH the host
and the client** — the host's engine paid the kill to every active party member and the
client's own engine paid the mirrored death the same way.

`OwnerNoLongerLists` did not fire this pass (the Dead flag arrived first both times, as it
should); it is the fallback for a death the client was not receiving for — pinned by
`LocalCombatExclusionRuleTests` rather than observed.

### Still open after this round

- No-god pass: the client PC (12 hp) took 2 per hit from two rifles and read `hp 1/12` afterwards
  even with the console's god on; a real level-1 client would die to this pair before killing
  either, which is the game, not the mod — but the death→wipe→revive path under a genuine
  two-slot fight has not been run.
- §11's other NEEDS-LIVE items (companion release against multiple slots, frame cost) stay open.

Verify.ps1 after the two passes (same rig, both kills already done): **41 passed, 8 failed** — the
documented baseline, the same eight checks.

## Companion damage TAKEN live: a client's hits down the host's Trask, the engine revives him, and the client's copy now stands back up (2026-09-02, night)

Rig: the same two installs, block version 64, save 1 (END_M01AA), then a host-only `warp
tar_m10aa` the client followed. Both inis: `local_combat=1`, `companion_local_combat=1`,
`companion_local_combat_range=80`, `liveness_sync=0`, `damage_creator_attribution=1` (ON on both
sides for this whole round — the C-attribution pass has not run yet, but nothing here depended on
it); the client also `client_temp_death_suppress=1`. Client PC `god on` throughout. The
`PhantomHitHook` build under test keys the companion watch by ROSTER SLOT (the earlier
object-id keying under-credited a reclaim/re-release cycle within one module — the cumulative
total restarted at 0 on every re-release, so the host's `DamageOwedLedger` saw the SAME total
twice and applied nothing the second time).

### What the round found first: a rejoin loop, not a combat bug

Host `warp tar_m10aa`, client followed cleanly — then 9 s later a W_SAVE mismatch, a
`SaveMismatch` rejoin that adopted **`END_M01AA`** (675,837 bytes, identity 0xC42CAB44), a
follow-warp back to tar_m10aa, the client's OWN transition autosave read as an `OwnLoad`, another
rejoin into END_M01AA, forever (~7 s per lap). Root cause, verified on the save files with
`grep -a -o`: the engine's transition autosave nests the module being **left** as its inner
`.sav` while `savenfo.res LASTMODULE` names the one being **entered**. `PluginMenuLoader
.TryReadSaveModuleName` read the ERF key, so the world the host served was tar_m10aa but the name
the client loaded under was END_M01AA. It now reads `LASTMODULE` first and falls back to the ERF
key only when savenfo.res is missing/empty/placeholder (unit test
`ATransitionAutosaveLoadsTheModuleBeingEnteredNotTheOneLeft`). Relaunched with the fix: the same
host-only warp produced no mismatch and no rejoin, and a forced `loadgame 0` on the client produced
exactly one:

    world adoption: adopted 'tar_m10aa' (668,109 bytes, identity 0x251335DA)
    rejoin: load request 3 sent to the plugin (module tar_m10aa ...)
    rejoined 'tar_m10aa'

session-save.md carries the write-up.

### Staging caveats in tar_m10aa (recorded so nobody repeats them)

- The eight pool-room Vulkars (0x24/0x26/0x2F/0x31/0x33/0x36/0x38/0x3B) never engaged the
  client's PC on their own — hostile in the census, `k_def_combend01` on `+0x260`, no movement for
  minutes. `blockpoke.ps1 attack 0x7FFFFFFF 0x33` got the first one moving and from there the
  slots armed and turned over normally; all eight died on the host's authority
  (`combat: killed 0x.. on the owner's authority`), Trask's XP 3081→5281 on both sides.
- `movenear 0x11C` on the client snapped Trask "near 0xE" → (0,0,0): the verb resolves a stale
  id here. The next 25 s reclaim put him back at the host's position. Move the HOST's PC (Trask
  follows) and `tp` the client next to it instead.
- The host's Trask carries **`SetMinOneHP` from the Endar Spire script** (min1hp `+0x200` = 1 on
  the host's server object, 0 on the client's released copy): on this rig he cannot be downed on
  the host until the pin is cleared (a 4-byte write of 0 at `+0x200` — a script flag with no
  derived state). That is the game's own tutorial rule (min-one-hitpoint.md), not the channel.

### The channel itself: 36 → 1 (pinned) → -11 (downed) on the host from client-side hits

Host PC `tp 148 90 2.25` (Trask follows), client `tp 151 92 2.25`, next to the guard droids
0x4A/0x4D and `tar10_vulkarpt02` 0x46. The client released Trask (`companion 0x11C released to
fight locally — within range of both you and the enemy you are fighting`), drove his attack on
0x46, and the HOST applied what the client's copy took:

    [21:38:36.053] companion damage: applied 5 of a peer's damage to companion 0x11C (roster slot 0, owed 5)
    [21:38:38.610] companion damage: applied 3 ... (owed 8)
    ...
    [21:39:02.117] companion damage: applied 12 ... (owed 42)
    [21:39:02.331] companion damage: applied 4 ... (owed 46)

The host's server Trask (`0x18DFCB58`) read `cur 1 max 30` — 46 owed against 36 hp, floored at 1
by the tutorial pin above. The host's CLIENT-side copy (`0x14187B94`, the other list
creatures.ps1 prints) still read 36: read the server object when asking what the host's engine
thinks. With the pin cleared and Trask released again, one more `hurt 5 0` on the client:

    client  [21:43:28.851] cheat: party slot 0 (0x11C) hit for 5: 1 -> -11 hp (downed) (status 1)
    host    [21:43:29.065] companion damage: applied 5 of a peer's damage to companion 0x11C (roster slot 0, owed 51)

Host server Trask `cur -11`, client copy `cur -11`: **downed on the host from a client-side hit,
both copies converged** (§13 NEEDS-LIVE items 1 and 2).

### The engine's own revive, and what "no hostile visible" means in practice

He stayed at -11 for four minutes. `TempDeathUpdate`'s 5 s safe-timer kept resetting: the pool
droid 0x44 (listed in the census, 6 m from Trask) — slain; the guard droids 0x4A/0x4D 3-5 m from
the host's PC — slain; still -11. `slay 0x46` — `tar10_vulkarpt02` at (163.5,87.3), **15.7 m**
from the host's PC — and within ~5 s the host's engine resurrected him (`cur 1`, `listening
+0x19C` back to 1) and the client's mirror followed to 1. So the gate is the hostile's
PERCEPTION of the party (death-handling.md §3's `IsPartyVisible`), not the 10 m figure: a hostile
that still sees the party from 15 m holds every downed companion down. Correct engine behaviour,
worth knowing when a fight "ends" and nobody gets up.

### The defect the revive exposed: the client's copy never stood up

No `party drive: stood companion` line in the whole session's client log, and the client's
Trask copies read `listening +0x19C = 0` (the death write) against the host's 1.
`PartyPresenceController` wrote the host's positive hp onto the body FIRST and only then asked
`Odyssey.IsDowned` — which is `GetDeadTemp || hp <= 0`, and `GetDeadTemp` computes `hp <= 0` on
the spot (there is no stored flag). After the write both halves are false, so the Resurrection
repair was dead code and a reclaimed copy kept the prone body and the zeroed AI flags under a
1-hp bar — the very symptom that comment was written to prevent. Fix: read `wasDown` before the
write. Rebuilt, relaunched on save 1, the `end_gensith` pair at (43.96,62.92)/(47.56,60.54), host
`tp 45.5 57 -1.28`, client `tp 45 59 -1.28`; Trask (0x127 here) released at 22:03:05; `hurt 40 0`
on the client (`36 -> -11 hp (downed)`); the 25 s turnover:

    host    [22:03:30.171] companion damage: applied 40 of a peer's damage to companion 0x127 (roster slot 0, owed 40)
    client  [22:03:30.326] party drive: companion 0x127 held neutral and pinned — the host owns its health
    client  [22:03:30.326] party drive: stood companion 0x127 back up — it was down here while the host has it on 1 hp
    client  [22:03:30.433] party drive: companion 0x127 released to fight locally — ...

Both client copies `listening +0x19C = 1` afterwards. The host's 40 was floored at 1 by the same
tutorial pin (END_M01AA sets it on Trask at OnEnter), which is what the mirror then wrote.

### Status after this round

- §13 NEEDS-LIVE 1 (host health genuinely falls) and 2 (downed on the host, revived by the host's
  own `TempDeathUpdate`, every copy converging) — VERIFIED.
- The reclaim/re-release under-credit (object-id keying) — fixed by the roster-slot keying;
  every re-release in this round applied on the host (owed 5→46 across three release cycles).
- The reclaimed-copy stand-up — fixed, verified above.
- Full suite 3016/3016 after the three fixes.
- Still open: two companions released at once (§13 item 4 — one companion on this rig), the
  no-god client death under a real fight, C-attribution's own pass (the flag was merely ON here).

## Attacker attribution live: the field is written, the engine does not retarget on it, and a 1.6-billion-point "hit" killed a Vulkar (2026-09-02, night)

Same rig and inis as the companion-damage round above (`damage_creator_attribution=1` both
sides throughout). peer-damage-ledger.md §8.10 has the analysis; this is the evidence.

### The write, at the field level

Reading the SERVER object of the creature the host applied a client's forwarded damage to
(`resolve <id>` on the host prints the pointer chain in its plugin log; `+0x158/+0x15C/+0x160`):

    end_gensith 0x3B (END_M01AA, after the client's hits):  lastHostile 0x12D  lastAttacker 0x7F000000  lastDamager 0x12D
    guard droid 0x4D (tar_m10aa, before any client hit):    round+0x9CC 0x7FFFFFFF  lastHostile 0x7F000000  lastDamager 0x7F000000
    guard droid 0x4D (after "combat: applied 4 ... to 0x4D"): round+0x9CC 0x7FFFFFFF  lastHostile 0x12F  lastDamager 0x12F

0x12D/0x12F is the client's phantom on the host (it changed at the one rejoin below). No
`damage_creator_attribution could not set a creator` line in any session.

### The behaviour: no retarget

0x4D was shooting the host's PC before the client arrived and kept shooting it after — 27 s of
2.5 s samples, `round+0x9CC` = `0x7FFFFFFF` on every one, `combat ground-truth: local PC took
{10,3,1,3,1,4,7,8} damage, attacker=0x4D` in the host log in between — until its round ended
(`0x7F000000`) when the client walked away. The key does what §8.5 proved and nothing more: it
fills `GetLastHostileActor()`/`GetLastDamager()`; K1's shipped AI does not consult them to
change an existing target. Idle → engaged was not measured (see the Vulkar caveat above).

### The defect: a poisoned ledger row

    host  [22:23:34.366] combat: applied 3 of a peer's damage to 0x46 (owed 3)
    host  [22:23:35.647] combat: applied 200 of a peer's damage to 0x46 (owed 1634994435)
          ... nine of those in 0.9 s ...
    host  [22:23:38.794] combat: a peer owes 200 damage to 0x46, which does not resolve here ANY MORE - it did earlier this epoch, so the body was destroyed; holding the debt in case it comes back
    client[22:23:35.834] local_combat: slot 2/3 released id=0x46 reason=owner reports dead or gone

`tar10_vulkarpt02` had taken 3 real points from the client's released Trask and then died of a
number no weapon deals. The client's plugin log rotated over that second (8.4 MB cap; the
`.1.log` ends at 22:25:59 and the fresh one starts there), so the effect is unnamed. Fix and
diagnostics in §8.10: a plausibility gate at the hook (with a dump of the effect for the next
time) and at both owner-side merges, `blockpoke.ps1 ledger` to read both directions live.

### Also seen: one benign SaveMismatch rejoin after a host-only warp

With the LASTMODULE fix in, a host `warp tar_m10aa` still produced ONE `SaveMismatch` rejoin on
the follower this launch (22:21:01: `adopted 'tar_m10aa' (644,785 bytes, identity 0xE46E8264)`,
rejoined in ~1 s) and then nothing — the loop is gone; the single rejoin is the client's own
transition autosave being judged against the host's newer one, and it converges. Noted, not
chased.

### Staging note

The `tp` verb on the client reported `moved from 102.3,114.9 to 148.0,92.0`; the attack order
`blockpoke.ps1 attack 0x7FFFFFFF 0x4D` engaged within a second (`attack probe: ... head=0x3F
round(active=1 ...)`). Read `pwsh <scratch>/target.ps1 -ProcId <host> -Ptr <server ptr>` on the
HOST's server object for `round+0x9CC`/`+0x158`/`+0x160`; the client-side twin never carries them.

## Engagement fallback live, and the first wipe under a real fight with hostiles visible (2026-09-02, night)

Rig: block 64, plugin rebuilt from 191c1b5 (engagement fallback, movenear refusal, implausible-hit
gates), `local_combat_engage_fallback` at its default ON. Host pid 8904, client 52328, `-Save 1`,
host `warp tar_m10aa`, one benign follower rejoin (`world: unannounced teardown` twice on the
client at 22:52:45/50, in tar_m10aa by 22:52:52).

### `local_combat_engage_fallback` — VERIFIED, and it lands real hits

Host `tp 160 88 2.25`, client `tp 161 90 2.25`, host `attack 0x7FFFFFFF 0x24`: the whole Vulkar
pool jumped the host (`combat ground-truth: local PC took 2/14/8/2/2 damage, attacker=0x38/0x36/
0x24/0x31/0x2F` within 1.1 s). On the client the two guard droids were the local fight:

    [22:55:42.839] local_combat: slot 1/3 armed id=0x4D reason=targeting-us (timeout 25s)
    [22:55:42.840] local_combat: slot 2/3 armed id=0x4A reason=proximity (timeout 25s)
    [22:55:43.591] local_combat: slot 1/3 engaged 0x4D directly (no attack target 752ms after the script fire)
    [22:55:43.591] local_combat: slot 2/3 engaged 0x4A directly (no attack target 752ms after the script fire)

with NO `attack` order given on the client, and the host applying that client's forwarded hits
from then on: `combat: applied 5 of a peer's damage to 0x4A (owed 5)` 22:55:52, `... (owed 10)`
22:55:55, `(owed 16)` 22:55:58, `(owed 22)` 22:56:21; 0x4D `owed 2 → 17` over the same window.
Zero `REFUSED an implausible` lines on either side across the whole session (the gate is
silent when nothing is wrong).

Also seen: the 25 s slot timeout releases a LIVE fight (`released id=0x4D reason=timed out`
22:56:07.839, re-selected 22:56:07.881, re-engaged by the fallback 22:56:08.631) — three times
in this fight. Handed off as an inactivity-timeout change (combat-participation.md, the dated
section after §12). The `local_combat WATCHDOG TRIPPED` line at 22:56:19 is the god-mode
artifact §11 already describes, not a defect.

### `movenear` — the refusal reply is right, the snap destination is still END_M01AA's

`movenear 0x999` → `no target: 0x999 does not resolve to a live creature here` (was a silent
"near 0xE" before). `movenear 0x46` on a live Vulkar in tar_m10aa → `0x46 snapped to
(29.0, 143.5, 0.0)` — the verb's hardcoded END_M01AA point, so it teleports a creature out of
the map in any other module. Diagnostic-only; noted, not changed.

### Wipe with hostiles visible, under a real fight — VERIFIED (first time)

`god off` both, `+0x200` min-1-hp cleared on both PCs and both Trasks (the Endar Spire pin the
save carries; `min1clear.ps1`), at 23:01:09-11, with the Vulkar pool and both droids alive and
shooting:

    host   [23:01:10.892] death: the local player is down                       (hp -11/12, real hits, no `down` verb)
    client [23:01:11.001] phantom 0x125: down (player at 0 hp)
    client [23:01:24.913] death: the local player is down
    client [23:01:24.916] death: game-over suppressed; the session decides
    host   [23:01:25.228] death: game-over suppressed; the session decides
    both   [23:01:25]     every player is down; releasing the game-over screen for everyone
    client [23:01:25.333] death: every player is down and the session released the game-over; running the engine's death sequence on this client
    both   [23:01:38]     world: unannounced teardown  →  away by 23:01:43

Fourteen seconds between the two deaths with the host lying downed under fire and the client
still fighting; the release came the second the client fell. 0 faults, 0 crashes on either
side. Host reload from the game-over (`load --pid 8904 --load-save 1`: "plugin load route
failed: plugin shared block not found", menu route "loaded on attempt 1"): host in END_M01AA
12/12 at 23:02:56, client rejoined and `Lanik is back` 23:03:01, pool snapshot 3 keys,
`firstSight ... reconcile=0`.

### The defect the recovery exposed: four rejoins in 21 s

SaveMismatch → OwnLoad → SaveMismatch → OwnLoad (23:02:38 / :40 / :51 / :56), the two OwnLoads
being the client reacting to its own rejoin loads. Root cause (a 0 read off a not-yet-re-found
plugin block edging the load serial) and fix in session-save.md, "The rejoin that rejoined
itself". It converged on its own; the fix removes the false trigger. Not yet re-run live after
the fix.

### Also: loot relay noise on rejoin

`materialised loot: submitting 'g_w_vbroshort01' for target 0x38 through the giveloot channel`
→ `refused: 0x38 is not a materialised stand-in here - nothing to give`, six pairs in one
second at 23:03:05 for the Vulkar corpses. Benign (the corpses are GIT-identical on the
client, nothing to give), just loud.

## Wipe reload re-run: one rejoin, then two new defects (2026-09-02, late night)

Rig: plugin rebuilt from ca49f63 (the `TryReadWorldLoadSerial` rejoin fix), block 64, `-Save 1`,
host pid 8904 / client 52328 kept from the previous section. Purpose: re-run the wipe reload
that produced four rejoins and confirm the fix.

### Wipe recipe outside combat (what it actually takes)

`down` alone does not wipe a party that is standing together, and does not keep a host down:

- Co-located: the client `down` was rescued by the 3.5 u auto-rescue within a second. Separate
  first (`tp`).
- Separated, host `down`: the engine revived the host PC at 1 hp after ~5 s — with the min-1-hp
  floor (`+0x200`) set AND with it cleared (`min1clear.ps1`). The revive is the engine's own
  "a companion is up and nobody is fighting" rule, not the floor.
- What works: host `down 0` (Trask) THEN host `down`; client `down` (the client's temp-death
  guard keeps its own PC down). Both PCs down with no companion up → the session releases the
  game-over on both.

### `TryReadWorldLoadSerial` fix — VERIFIED: exactly one rejoin

Host reload from the game-over (`load --pid 8904 --load-save 1`, menu route). Client:

    [23:31:01] * Lanik is back
    [23:31:01] * the host is back in the world after the wipe - rejoining their world
    [23:31:01] * session-save: starting rejoin (WipeRecovery)
    [23:31:03] * rejoin: load request 2 sent to the plugin (module END_M01AA, carried character, mid-session)
    [23:31:03] * rejoined 'END_M01AA'
    [23:31:07] connected ... module END_M01AA  hp 12/12  peers-here 1

and NOTHING after it — no `SaveMismatch`, no `OwnLoad` (the 23:02 run had four in 21 s). The
plugin route worked this time (`load request 2 sent to the plugin`; the 23:02 run had fallen
back to the menu route). Rejoin churn after a wipe reload: **Works**.

### Defect 1: the reload duplicated the host PC's saved equipment

Fifteen seconds after the host's world was up (the `ItemTagRebuildMs` rebuild), the host's
script-grant pump diffed the save's own equipped clothes and short sword in as a grant:

    host   [23:31:05] * party pool trace: host snapshot 3 key(s): g_i_adrnaline003 ..., g_i_medeqpmnt01 ..., g_w_blstrrfl001 ...
    host   [23:31:19] * script item grant: 2 new item(s) in the local PC's inventory outside dialogue: g_a_clothes01, g_w_shortswrd01
    client [23:31:19] * dialogue grant: received 2 item(s) from peer 1 (''), queueing
    client [23:31:19] * gave 'g_a_clothes01' to 0x7FFFFFFF
    client [23:31:19] * gave 'g_w_shortswrd01' to 0x7FFFFFFF
    client [23:31:24] * party pool: local add of 1x 'g_w_shortswrd01' detected, requesting (serial 184535419)
    host   [23:31:34] * party pool trace: host snapshot 5 key(s): g_a_clothes01 ..., g_i_adrnaline003 ..., g_i_medeqpmnt01 ..., g_w_blstrrfl001 ..., g_w_shortswrd01 ...

The client ended up with a second set of clothes and sword, and the pool grew 3 → 5 keys. Cause:
the "settled" resweep the provisional rule grants ran while the engine was still finishing the
loaded PC (status read `hp 8/8` before `12/12`), so the equipped items were not in the baseline
and the next rebuild saw them as new. Fix: `ItemSweepProvenanceRule.Settling` (two spaced sweeps
must agree after a world edge; `ScriptItemGrantPump` rebaselines silently until then) —
shared-inventory-v2.md §18. NEEDS-LIVE.

### Defect 2: the client vanished on the first follow after the wipe-recovery rejoin

Host `warp tar_m10aa` at 23:34:39, three minutes after the rejoin. Client agent:

    [23:34:39] * host announced transition to 'tar_m10aa'
    [23:34:39] * following Lanik to 'tar_m10aa' IMMEDIATELY (announced transition serial 1, ...)
    [23:34:39.988] [resolve] player 0x1C562C18: vtable unreadable          ← last line

Client plugin:

    [23:34:39.852] flush, before the module is stored: destroyed phantom 0x131 (slot 0)
    [23:34:39.855] travel: moving to module 'tar_m10aa' on the session's authority
    [23:34:39.866] epoch: world unloading; clearing phantom, item, combat and door state   ← last line

The successful follow at 22:52:33 logged `transition: closed the client world ahead of the map
init` 50 ms after the travel line; this one never reached it. No `FIRST-CHANCE` line, no
`FATAL (managed)` line, no Windows Application-log entry (`tools/crashlog.ps1`: none), and
agent2 went with it (its "the agent dies with its game" watchdog). The host was unaffected
(`Malak left the game` 23:34:45, in tar_m10aa 12/12).

A process leaving with nothing in any log is what `ExitProcess` looks like from outside, so the
plugin now hooks swkotor.exe's `KERNEL32!ExitProcess` IAT slot (`ExitProbe.cs`) and writes one
`EXITPROCESS(code) called from 0x... ; image-range stack values: ...` line on the way out. Next
reproduction attempt: wipe → host reload → client WipeRecovery rejoin (plugin route) → host
`warp`. Tracked in transition-crashes.md and limitations.md.

### Also confirmed this launch

- `log_keep` rotation: `kotorcoop-plugin.1.log` etc. exist beside the live files on both sides.
- The loot-relay noise on rejoin (six `not a materialised stand-in` refusals in one second) is
  what the client-side pre-filter (block 65, `MaterialisedLootFilterRule`) removes; NEEDS-LIVE.

## Wipe reload, third pass: equipment duplication verified fixed, client follow-crash root-caused and fixed (2026-09-03, 00:17–00:33)

Rig: `-Save 1`, plugins rebuilt twice this stretch (fingerprint settle at 00:14; the party-count
travel bound at 00:26). Same wipe recipe as the section above (client `tp 28 22.2 -1.3`, host
`down 0` then `down`, client `down`; note the host `down 0`/`down` pair sometimes needs a second
go — the engine revived the host at 1 hp once when Trask's down had not settled).

### Defect 1 (equipment duplication) — VERIFIED FIXED, twice

00:17 and 00:30 host reloads through the plugin route: no `script item grant`, host snapshot
3 keys before and after, one `WipeRecovery` rejoin each. shared-inventory-v2.md §18.5.

### Defect 2 (client vanish on first follow) — ROOT-CAUSED and FIXED, verified 1/1

00:18: third reproduction, with the persisted crash record. `tools/crashbin.ps1` decoded
`access violation at 0x63D4B3`, symbolicated to `CSWCObject::GetServerObject` ←
`CSWCCreature::GetServerCreature` ← `CServerExoAppInternal::DoPCAutosave` (+0xB5F), the loop
`StartNewModule` runs over `i < [CSWParty+0x00]` with no null check on `GetCharacter(i)`. The
plugin-route rejoin keeps the client half alive across the swap, so the mirrored companion's
pre-wipe id stays in the array and the first move dereferences NULL for it.

00:26 rebuild with `PartyCountRestoreRule.SafeTravelCount` (bound the count by the leading
resolvable run before the move). 00:31:42 host `warp tar_m10aa` after the 00:30:46 rejoin:

    client plugin  [00:31:42.656] party count: bounded 2 -> 1 before the module move (entries 0x80000129 0x80000127!; ...)
    client plugin  [00:31:42.709] transition: closed the client world ahead of the map init
    client agent   [00:32:26] connected ... module tar_m10aa  hp 12/12  peers-here 1

`0x80000127` = Trask's pre-wipe client id (the host's `down 0` named slot 0 as 0x127) — the
stale entry is the mirrored companion, not the PC's own slot. Second warp (`end_m01aa`)
followed cleanly with no bound needed. 0 faults either side across the whole stretch (only the
0x406D1388 thread-naming first-chance lines at launch). transition-crashes.md 2026-09-03.

### Also this launch

- Loot-relay noise on rejoin: zero `not a materialised stand-in` refusals on the 00:30 rejoin
  (the pre-filter round had six in one second) — `MaterialisedLootFilterRule` verified as far as
  this recipe exercises it.

## Effects both ways, the count bound fired a second time, and effects were replaying on every arrival (2026-09-03, 00:37–00:58)

Rig `demo.ps1 -Save 1`; three sessions (00:37 fresh, 00:43 fresh, 00:56 fix build). Plugin
build: 2b93f80 for the first two, the `PeerEffectReplayRule` build for the third.

**Effect sync, the last three kinds, both directions — verified.** Host `applyeffect heal` /
`temporaryhitpoints` / `confused` on the nearest phantom: client logged `effect: applied kind
10`, `11`, `12 from peer 0x1` (00:37–00:38). Client → host the same at 00:43–00:44 and again at
00:56:16–00:56:31. That is 12/12 kinds observed live in both directions (effect-sync.md §11.1).

**The CSWParty count bound fired a second time and the client followed cleanly (2/2).**
`[00:47:49] party count: bounded 2 -> 1 before the module move (entries 0x80000129 0x80000127!;
DoPCAutosave walks the array without a null check)` on the post-rejoin client, host `warp
tar_m10aa`, client arrived (transition-crashes.md updated to 2/2).

**Defect found: peer effects replayed at every module arrival.** After the warps the client
logged the whole IN ring again — `[00:48:02.359] kind 10/11/12 from peer 0x1` arriving in
tar_m10aa and `[00:48:36.859]` the same three arriving back in end_m01aa — and the host
re-applied five (`[00:48:35.653] kind 11, 12, 10, 10, 10 from peer 0x2`). Root cause: the
controller cleared its applied-serial latch at each epoch change while the ring itself is never
cleared. Fixed by adopting the ring at the edge and keying the latch by (origin, serial)
(`PeerEffectReplayRule`, effect-sync.md §11.2–11.3).

**Fix verified (00:56–00:58).** Four effects applied (three on the client, one on the host),
host `warp tar_m10aa` then `warp end_m01aa`, client followed both (epoch edges 00:56:47,
00:56:57, 00:57:33): **zero** `effect: applied` lines on either side after 00:56:31. Both
processes alive at the end.

**One-off client crash, 1/4, staging verb only.** 00:38:44, client `applyeffect heal` on the
nearest phantom, post-rejoin + two warps: `CSWGuiFloatyText::CreateEntry` dereferenced the NULL
node the phantom's client model returned for the "+N" floaty (chain from
`CSWSEffectListHandler::OnApplyHeal` via `AddFloatyText`; eip 0x688F54). Not reproduced in three
further tries; not reachable through the sync's receive side. Written up in effect-sync.md §11.4
and a limitations row.

## The first real vendor purchase: a stale store cache wrapped the purse to a fortune, fixed, converged (2026-09-03, 01:03–01:19)

Rig `demo.ps1 -Save 1`, both instances warped to tar_m02aa. Host cleared the warp-in
conversation (`selectreply 0` / `skipline`, three rounds), `openstore Larrim_Store`.

**Defect found: `CSWGuiStore::BuyItem` spends from the panel's gold snapshot, not the party
table.** Panel opened at 100, client contributed `grantgold 400` (table 500, cache 100), host
bought a 110-credit pistol: the engine wrote `100 − 110` unsigned —
`[01:06:38] * purse: peer 2 contributed +0 -3294967287, party gold 4294967286 -> 999999999` —
and both purses settled at 999,999,999. Written up in store-sync.md (2026-09-03 section).

**Fix build crashed the host on its first frame (1/1, fixed).** `CGuiInGame+0x84` is non-null
with store id 0 on a fresh module; the reconcile called `PopulateStoreItemListBox` on it —
`access violation at 0x6C1872`. Gated on store id + `GetStoreByGameObjectID` + player; clean
since.

**Fix verified (01:17–01:18).** Host panel open at 0, client `grantgold 400`: host `[01:17:52.459]
store: gold cache 0 -> 400 (the party table moved while the panel was open)`, purse `0 -> 400`;
host buy → `gold 290` on both sides. Vendor-purchase ledger convergence: Works.

**Not verified this pass: a real door crossing.** Host `usedoor` in tar_m02aa picked door 0x23
→ 'tar_m02af', the walk never reached it (`600 frames after the walk the player is 5.9m from
0x23`), `opendoor` replied `REFUSED to open (still state 2)` — a locked apartment door, not a
transition. Needs a module whose transition door is unlocked from the warp-in side.

## Two companions released at once, the two-companion warp crash found and fixed, and a warp mid-dialogue (2026-09-03, 01:31–02:25)

Rig: `demo.ps1 -Save 1`, host GOG + client p2, shipped defaults.

**Two companions released to local combat at the same time (row "Two companions released")** —
01:38, client fighting `0x3B` + `0x38` (Endar Spire soldiers) with Trask `0x127` and Carth `0x2`
in the party. Both companions were released in the same pass (`party drive: companion 0x127
released to fight locally`, `companion 0x2 released to fight locally`, `mirroring 0 of the
host's companions`), each was driven at a different enemy (`drove companion 0x127's attack on
0x38`, `drove companion 0x2's attack on 0x3B`), both enemies died (host `materialised loot:
captured 2 item(s) off 0x3B's corpse` 01:38:14, `0x38` 01:38:32) and both companions were
reclaimed together at 01:38:32 (`companion 0x127 reclaimed — resuming the host mirror`,
`companion 0x2 reclaimed`, `mirroring 2 of the host's companions`). Works.

**Two-companion host warp crash** — 01:40 and 01:45 the host died on its first `warp` after
`recruit 2 carth` (client attached), and again at 02:01 with no client at all. Root cause and
fix in transition-crashes.md (same date): a slot recruited by a raw availability-flag poke has no
`AVAILNPC<slot>.utc` for the engine to rebuild the companion from after the move, so
`PositionParty` calls `SetPosition(NULL)`. After the fix (every recruit goes through the
engine's `AddNPC`): host `recruit 2 carth` 02:19:32, warps `tar_m10aa`, `tar_m02aa`,
`tar_m03aa` at 02:19:45 / 02:20:07 / 02:20:22 and `tar_m10aa` again at 02:25:09 — 4/4 clean,
both companions rebuilt each time, client followed with both, 0 faults both sides.

**Mid-dialogue partner transition (row "Mid-dialogue partner transition")** — 02:21:45 host
`startdialog end_trask` (Trask, `0x139`, node 29). The client does NOT auto-join
(`dialogue_auto_join=off`, shipped default) — it joined by starting the same conversation
(02:24:57 `the engine started the same conversation here; adopting it to follow`, `followed the
owner 19 -> 29`, agent `dialogue-gate: client ... False -> True (followingPeer=True,
tokenActive=True)`). Host `warp tar_m10aa` with the conversation open: client 02:25:13 `the
owner ended the conversation; closing`, 02:25:14 gate `True -> False`, `following Lanik to
'tar_m10aa' IMMEDIATELY`, arrived 02:25:18 with hp 12/12 and both companions. No orphaned
dialogue GUI, no crash on either side. (The `W_SAVE` "different save" auto-rejoin that followed
at 02:25:24 is the ordinary world-adoption path every host warp triggers on this rig — 24 today —
not part of this row.) Works.

## 2026-09-03 (00:30–04:15): floaty-text crash root-caused and fixed, `spawncreature` and `liveness_sync`'s checklist verified (`liveness_sync` now ON by default), `threat_retarget` live, local-combat inactivity timeout confirmed

Rig: `pwsh tools/demo.ps1 -Save 1`, host `C:\GOG Games\Star Wars KOTOR (GOG)`, client
`D:\Games\swkotor-gog-p2`, block version 66.

**The 0x688F54 / 0x6890A4 floaty-text crash — FIXED.** §11.4's phantom-heal crash (00:38)
recurred twice more: 03:45:32 over a 49-creature population (`materialise: owner 0x182 -> local
0x139 (appearance 264, renders no twin yet, faction 1)`, host `slay 0x182`,
`access violation (0xC0000005) at 0x688F54`), and 03:56:47 on the per-entry update after only the
first site was guarded (`floaty: target model has no anchor node - not drawn (would have been the
0x688F54 crash; 1 so far)` then 3 ms later `access violation (0xC0000005) at 0x6890A4`). A
self-inflicted build mistake (resuming at the fault site instead of `SpliceAt + run length`)
crashed the HOST at 04:01:00 on an ordinary damage floaty. Final build, both sites guarded,
04:08–04:11: 20 host-spawned creatures slain, `floaty: floaty-text null-node guard on 2/2
site(s): CreateEntry 0x688F45 (stub 0x17120000); Update 0x689079 (stub 0x17130000)`, ending at
`floaty: guard totals so far - 27 floaty(ies) not added, 16967 tick(s) skipped`, zero crashes,
host log carrying no guard lines at all. Full RE and fix: `FloatyTextNodeGuardHook.cs`,
`effect-sync.md` §11.5, `transition-crashes.md` 2026-09-03.

**`spawncreature` — verified.** `spawncreature g_vulkar001 3` refused with failure 9 (template
did not resolve); `spawncreature tar10_vulkpoo002 3` → `spawned 3/3 tar10_vulkpoo002: 0x13F 0x142
0x145`, then batches of 20 more. Spawned creatures materialise as ordinary stand-ins on the
client, loot flows off them, and they land real damage on the host PC (`combat ground-truth:
local PC took 4 damage, attacker=0x140`). `creature-liveness.md`'s 2026-09-03 addendum,
`CONSOLE.md`'s `spawncreature` row.

**`liveness_sync`'s checklist — items 1, 2, 3, 4, 6 clean; flipped ON by default (`2bbf8de`).**
Item 1: host `destroyobject 0x1F` → client `liveness: 0x1F no longer exists on the owner and
still did here - destroying to match (local 0x1F)`, one line. Item 2 (host launched with
`--enemy-census-cap 5`): host `slay 0x3B` → client `liveness: 0x3B is a corpse on the owner and
was still alive here - killing to match`, one line. Item 3: `liveness-freshness-probe.ps1
-Stage destroyobject` on 0x1C, 26-creature `tar_m10aa` population, zero disagreements. Item 4:
warp `tar_m10aa` → `tar_m03aa`, `epoch: world unloading; clearing phantom, item, combat and door
state`; object ids are reused across module loads (0x2 was a 22/22 Vulkar, then a 20/20 plot
creature). Item 6: at a 49-creature population, `census: gate: array ... confirmed 52 of 70`,
`OWNER 32 id(s)` — the census caps at 32, sorted fighting-first then by squared distance to the
NEAREST player, so only the far/idle creatures shed — not a bare truncation. A log-budget bug
(the shared 20-line cap spent entirely on the anomaly counter) hid the destroy/kill lines above
until fixed (99c6ffe, one budget per outcome). `creature-liveness.md`'s 2026-09-03 sections.

**`threat_retarget` — live, three rounds.** Round 1 exposed `ThreatRetargetOptions.Default` built
with `new()` (a record struct's zero-initialiser skips the primary-constructor defaults), so every
threshold read 0 and a 6-point hit retargeted through what should have been a 15-point floor
(`threat: 0x24 retargeted 0x7FFFFFFF -> 0x13F (threat 6 vs 0)`); fixed (99c6ffe). Round 2: a
genuine retarget fired at the corrected 1.5x/15/4s thresholds when the AI had gone for the host
first (0x3B retargeted at 15/20 threat). Round 3: the engine's own AI already prefers the client's
phantom when the client is the attacker (`creature 0x26 currentTarget 0x13A topAttacker 0x13A
(threat 9.42)`), so no retarget fired — and none should have. Default stays OFF.
`peer-damage-ledger.md` §10.

**Local-combat inactivity timeout — confirmed live**, 04:00–04:01, `tar_m10aa`, before the
`spawncreature` rounds above: a slot with traffic extends past 25s
(`local_combat: slot 2/3 still active (dealt +3 to 0x4D), timeout extended`) while an idle one
releases exactly at 25s (`local_combat: slot 1/3 released id=0x4A reason=timed out (no activity
for 25s)`); the 04:08 round added `local_combat: slot N/3 still active (it targets us), timeout
extended` (12 lines) and `local_combat: slot N/3 armed id=... reason=proximity (timeout 25s)` (9
lines). `combat-participation.md` §13's own NEEDS-LIVE mark is closed — see its own §14.

**Galaxy map** — already documented in `galaxy-map-and-party-select.md`; no new facts this round.

**Still NEEDS-LIVE**: the galaxy-map confirm refusal firing against a real Accept click.
(`E_PROTO` was done at 04:21 the same night, the real door at 05:10–05:33 via `crossdoor` — see
the sections below.)

## `E_PROTO` version-mismatch reject, live (2026-09-03, 04:21)

Rig: block 66, `ProtocolConstants.Version = 50`; `debug_hello_version=1` added under `[coop]` in
the CLIENT's `coop.ini` only, `demo.ps1 -Save 1`, no password either side. Removed right after.

CLIENT (`agent2.log`): `* co-op: connecting...` at 04:21:18, then at 04:21:19 — one second
later, not the 15 s `DefaultConnectTimeoutMs` — `* failed - rejected by host: E_PROTO: host is
v50, you are v1` followed by `* co-op: connection failed`, and the status line thereafter reads
`rejected by host: E_PROTO: host is v50, you are v1  peers 0` (no retry loop). Both lines are
`CoopCommandDispatcher.Reply` output, the same path the in-game console reply and chat overlay
are fed from (overlay not screen-checked this round).

HOST (`agent1.log`): nothing at all about the rejected Hello — `peers 0` stays `peers 0`. That
silence is the one known gap (a host cannot tell an old-build peer was turned away).

Row moved to Works in docs/limitations.md and README.md. Full write-up:
docs/research/disconnect-reconnect.md §12 "Live result".

## `threat_retarget` round 4, live (2026-09-03, 04:35–04:45): aggro SHARES, default flipped ON

Same tar_m10aa staging as the 00:30–04:15 section, plus `applyeffect temporaryhitpoints 0x141
120000 400` so the Vulkar outlived a party round. 98 s of fight, 7 retarget orders, 12 hits
on the host's PC vs 10 on the client's phantom (§8.10's passive creator write alone: 0 on the
phantom). Every order landed (`attacktarget diag: 0x141 round+0x958 0->1`) and every one was
undone by the host PC's next landed hit within ~3–10 s, so the 4 s cooldown turns it into an
alternation — the enemy splits between the two players rather than following one. 0 faults.
`profiles/coop.ini.example` now ships `threat_retarget=1`; full timeline and the verdict in
docs/research/peer-damage-ledger.md §11. Rerun 04:48–04:51 with the host `tp`'d away
first (§11.6): the engine's own AI went to the client and stayed — 28 phantom hits in two
minutes, none on the host PC, no `threat:` order needed — so the channel acts only while both
players are in the same melee.

## Real door crossing via `crossdoor`, live (2026-09-03, 05:10–05:35): 3/3 transitioned, a false `W_SAVE` rejoin found and fixed

Rig `demo.ps1 -Save 1`, both in end_m01aa. `tp 44.5 120.1 0` (end_door10 is 101.7 m from the
spawn; `crossdoor` refuses past 40 m), then `crossdoor end_door10` on the host, three times over
three fresh rigs.

**Every run:** host plugin `door: crossing 0x4A (2.9m) -> 'end_m01ab' (waypoint 'from_01a')
posted as event 0x1E from the PC (party index 0), not walked` → `area transition pending:
flushing spawned objects before the module is stored` → `announce: door 0x4A transition beginning
-> 'end_m01ab' at waypoint 'from_01a'` within 0.6 s; client agent `following Lanik to 'end_m01ab'
IMMEDIATELY (announced transition serial 1 ...)`; both agents' status lines `module end_m01ab`;
end_m01ab's module-entry conversation started on both sides (`engine reached StartDialog`, the
other side `adopting it to follow`, `followed the owner 6 -> 7`). Trask left the party on entry
(`party roster: local party is now 0x000`) — vanilla OnEnter behaviour, and the client's
`party_together_fix` clamp line followed it. 0 faults on either plugin log. The warp-crash fix's
real-door clause (transition-crashes.md) is exercised: no `6B5FEE`, no crash.

**Run 1 (05:10:48) — the bug:** at 05:11:03 the client printed `[W_SAVE] different save`,
`your world still differs from the host's - rejoining automatically`, `starting rejoin
(SaveMismatch)`, `adopted 'end_m01ab' (661,457 bytes, identity 0x2EDFDAA3)`, `rejoined
'end_m01ab'` — and, because the client owned the cutscene, the host logged `dialogue: the owner
ended the conversation; closing` mid-way. Cause: the engine's transition autosave (both
`000001 - AUTOSAVE` dirs rewritten at 05:10:49, different bytes) puts each side's own
`Mod_NextObjId` counters into the digested save, so the identities disagree for good after any
real door. Fix: `SaveLineage` (session-save.md, 2026-09-03 section) — a mismatch after a recorded
agreement with no load edge on either side is a same-world save.

**Runs 2 and 3 (05:25:36, 05:33:01), agent rebuilt with the fix:** no `W_SAVE`, no rejoin, the
cutscene completed on both sides (`the owner ended the conversation` at 05:25:53 / 05:33:18 with
the owner's own close), both exploring end_m01ab. Run 3 printed the fix's one line — `save
identity differs after a save on one side, with no load since the two last agreed - same world,
not rejoining` — at 05:33:04 (and again at 05:33:08: per-call flag flickering on the mid-transition
module-name mismatch; made a per-episode level afterwards, unit-tested, not re-run live).

Reading the demo agent logs: `agent1.log`/`agent2.log` carry TIMES ONLY and accumulate across
days, so `^\[05:` also matches yesterday's 05:xx status lines (a `module END_M01AA  malformed 1`
run from 2026-09-02 sat interleaved with today's `module end_m01ab` lines). Cross-check against
the plugin logs, which are per launch.
