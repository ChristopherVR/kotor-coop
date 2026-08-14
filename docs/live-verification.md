# Live two-instance verification checklist

Run the pair, then verify each channel. "Logs" items I can confirm from the agent/plugin
logs; "Screen" items only you can see. NEEDS-LIVE items are the offsets/values that were
reasoned but not observed — a wrong one fails safe (no effect or mis-placed text, never a
crash), so confirming them is the point of this pass.

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

Next diagnostic (not run): dump the host's published census beside `creatures.ps1` output and
find which ids in it have no resolvable creature — the suspicion is the census is read in a
different id space (KOTOR's client/server halves — the host dump shows the same id at two
addresses) or holds entries for creatures already destroyed. Until that is settled, a client's
forwarded damage against a naturally-present enemy is only reliable where the ids happen to
agree, which the ID counts (38 vs 45) say is not guaranteed.

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
