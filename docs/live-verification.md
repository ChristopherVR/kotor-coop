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

**Party reconciliation (2026-08-01).** Both halves measured, closing the design's open
"inferred, not measured" risk:
- **Solo mode is real and one-sided** — `partystate.ps1` reads `soloMode 0` on the host and
  `soloMode 1` on the client, with the same companion (slot 0, id `0x127`) on both.
- **The mirror actually drives the client's companion**, proven by a control rather than by
  coincidence. Displacing the CLIENT's companion by writing its position: with the agents
  running it converges back to the host's exact value within ~4 s; with the agents stopped it
  stays where it was put for 6 s+. Two identical saves would place a companion identically,
  so only the control distinguishes "mirrored" from "same script" — run it that way.
- Not covered: companions actually FOLLOWING (the Endar Spire opening script-locks Trask, so
  his AI never issues follow orders there). Needs a later save.

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
