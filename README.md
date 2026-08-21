# kotor-coop

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail
GOG build), in C#. Two full copies of the game run side by side — locally or across a
network — and an external agent plus a tiny in-process plugin bridge them into one shared
world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads
itself; everything else attaches from outside with `OpenProcess`.

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") wearing the other player's
  actual appearance, armor and weapons, with their name in the selection band, walking
  and running at true speed on the real walkmesh.
- **Travel together, either player leading**: whoever crosses into another area first,
  the other follows immediately through the engine's own module-transition path (the same
  machinery the built-in `warp` cheat uses) — an announce fires the instant the leader's
  engine commits to the crossing, so both loading screens overlap instead of running back
  to back. `follow_lead=host` in coop.ini restores host-only leading; `--no-follow` opts
  out of following entirely.
- **Fight the same fight — and see the same fight**: enemy health and death are
  host-authoritative and mirrored live; clients stream their local damage back and the
  host merges it in, so both players' hits count. And the fight *looks* the same on both
  screens: the host streams every enemy's position, facing, stance and weapon swings,
  and the client drives its copies to match frame by frame instead of letting two AIs
  play two private versions of the same battle. (Verified live: 27/27 creatures at
  identical positions and health across both games, rendered bodies included.)
- **Enemies fight back — at both of you.** Your partner's body is a real combat target in
  your game, not scenery: enemies pick it, swing at it, and every point of damage that
  lands is forwarded and applied to their actual character on their own machine, through
  the engine's own damage pipeline (their resistances, their difficulty, their floating
  numbers). Verified live: a hit landed on one machine took the other player from 12 hit
  points to 5. A mirrored enemy can also fight a client's own player directly, through that
  client's own engine rather than only through the forwarded channel — verified live in a
  real session, correctly distinguished from and never double-counted with the forwarded
  path (`docs/research/shared-state-audit.md` §2, round 52). And it is genuinely lethal: on
  a module with no min-1-hp pin, a real fight through that same client-side mechanism (26
  ground-truth-confirmed hits, zero forwarded-channel activity in the window at all) brought
  a player down for real — the engine's own death path fired, and the other player's screen
  correctly showed them down too (round 53). And a client can land the killing blow: its own
  damage against a shared, one-world enemy now reaches the host and is applied for real —
  verified live, the same `tar_m10aa` Black Vulkar, `combat: applied` climbing to exactly the
  enemy's max hit points, a real death with a corpse and real loot on the host, and the
  client's own mirrored copy following the death down the ordinary path (round 54; round 53's
  own narrowing traced to a transient false-positive save-mismatch warning that briefly
  switched the whole enemy channel off after certain module crossings — fixed, and the fix is
  live-verified on the identical crossing), and it now happens without either player asking:
  standing where a hostile can actually see you is enough. Round 55 settled the open question
  from round 54 (does the host's own engine notice a player naturally with the plugin loaded)
  decisively — yes: at true melee range, seven Vulkars independently aggroed the host through
  their own AI with zero plugin involvement in the decision, and the same host swarm aggroed a
  client's phantom the same way. The earlier "enemies never notice on their own" readings were a
  line-of-sight gap, not a plugin effect or a range problem — the same distance metric that
  produced zero aggro at one spot in a room produced seven simultaneous aggros a few metres
  closer, in view. With that settled, round 55 also rebuilt the client's OWN trigger: a hostile
  it is mirroring now joins the one local fight immediately on entering
  `local_combat_engage_range` (11 units by default) or on the host reporting it is currently
  hitting your phantom — not on whatever the census enumeration order or a 25s round-robin
  happened to hand it, which is what every earlier round actually observed. Verified live against
  the same seven-Vulkar swarm: immediate hand-off (armed in the same frame as the trigger, and
  the next candidate armed in the same frame a freed slot reopened), two real kills, and every
  other nearby hostile still landing real forwarded hits throughout — no invulnerability with
  several enemies engaged at once. See `docs/research/shared-state-audit.md` §2, round 55.
- **Share the world**: dropped items materialise on the other side as real, clickable
  objects and vanish when picked up; doors open and unlock for both; the story globals
  and the quest journal merge upward, so one quest line advances for everyone — verified
  live, a quest advanced on one machine lands in the other's real journal, once.
- **Pause together**: either player's pause pauses both worlds, and only the pauser can
  release it.
- **Go down together, and get each other back up**: a player at zero health drops their
  phantom into a real death pose on the other side. KOTOR never truly kills a party member —
  it downs them — so a partner who walks over picks you straight back up, and the game's own
  recovery still applies if nobody is close. Nobody gets dropped to the main menu alone
  either: a party wipe only ends the game once *every* player is down, and then it ends for
  both of you together. And while you wait for the rescue you are not staring at your own
  corpse: **a downed player's camera follows their partner** — through the engine's own
  camera handoff, the same one the party system uses — and **Tab cycles** through everyone
  worth watching: every living player, then the companions the host is fighting with (which
  is what the host is really *playing* after their own PC drops). A watched target that
  dies falls off the list and the view snaps to a survivor; the camera comes back the
  moment you are revived, or shows the honest corpse view when nobody is left standing.
  (On the host, KOTOR's own behaviour is kept instead: control swaps to a living companion
  and you keep playing.) `spectate_on_down=0` opts out.
- **See each other on the map**: every other player is a coloured arrow on your **minimap** —
  red, green, orange, purple, cyan, yellow, one per player — pointing the way they are facing
  and moving as they move, plus a live pin on the full M-screen map. Your own arrow stays blue
  and is always drawn on top.
- **Share one party**: the host drives the companions (Trask, Carth...) with the game's own
  AI and each client freezes and mirrors them — through the same frame-by-frame drive as
  enemies, swings included — so they stay one synchronised group instead of two that
  drift. `--no-party-sync` opts out.
- **Start the whole thing from the game's own menu**: a "Co-op" button on the main menu
  opens a real KOTOR panel — the same list widget the Load Game screen uses, with its native
  font, borders and scrollbar. One player clicks **Host**; the other sees them listed with
  their player count and clicks **Connect**. Both games load their own save and come up in
  one shared world. No console, no command line, no alt-tabbing: the agent is started by the
  mod itself when the game launches. A password keeps strangers out; a dropped player
  reconnects automatically and keeps their slot. `coop.ini` still works for a fixed host if
  you prefer.
- **Type commands in-game**: press `~` for the game's own console and type
  `connect <ip> [password]`, `disconnect` or `reconnect` at any time — the session is torn
  down and rebuilt live, no restart. If the host wants a password you didn't give, the
  console prompts you to retype it. Status and co-op replies print on-screen while it's open.
  The console helps you type: **Tab completes** and repeated Tab cycles the candidates,
  **Up/Down recall** what you typed before (and it survives a restart), **Escape** dismisses,
  and a hint line under the input shows the candidates or the verb's usage with the argument
  you are on marked. Completion covers **the game's own console verbs too** — `giveitem`,
  `revealmap`, the `set*` stat cheats — because it reads the engine's live command table, so
  it picks up a mod's verbs as well.
- **Stage a test without grinding to it.** Alongside KOTOR's own cheats (which all touch only
  your game, so they were already co-op-safe and are left alone) there are a few verbs whose
  answer depends on *who typed them*:

  | Verb | Does | Co-op rule |
  | --- | --- | --- |
  | `god [on\|off]` | your damage floors at 1 hp | yours alone — your health is what your peers mirror |
  | `topup [n]` | your hit points to n, or full | yours alone |
  | `standup [n]` | stand yourself up out of the downed state | yours alone; retail `heal` doesn't do this |
  | `slay [tag\|id]` | kill an enemy (blank = nearest) | the enemies' owner kills it for real; a client sends the kill through the damage ledger so the host's engine produces the death for both. Refuses a player, a phantom, a companion or a plot creature — out loud |
  | `tp <x> <y> [z]` | move yourself in this area | yours alone; your position already replicates |
  | `goto <player>` | move yourself to a peer | same module only |
  | `dropitem <resref\|0x131>` | drop a blueprint, or a copy of a live item, at your feet | goes through the item channel, so every instance materialises it and it vanishes for all of them when any one takes it. The `0x131` form carries the item's real stack size, charges and upgrades |
  | `items [n]` | list live items here with their object ids and state | read-only; how you find an item worth sending |
  | `item <0x131>` | what one item's per-instance state actually reads | read-only; the far side's copy is checked with this |
  | `setitem <0x131> …` | poke one item's stack / charges / upgrades / identified bit | staging only, local. KOTOR gives a script no way to spend a charge or install an upgrade, so this is how an identity test gets something worth testing |
  | `cheats` | lists these and the retail ones | — |

  `docs/research/console-cheats.md` has the full reference and what each one pokes.
- **Talk to each other**: `say <message>` in the ~ console sends a line to the other players
  (the host relays it), so you can coordinate without seeing each other's screens.
- **One conversation at a time, like Baldur's Gate 3**: while any player is talking, nobody
  else can start a dialogue — enforced inside the engine, so clicking an NPC simply does
  nothing and tells you why. The other player spectates the owner's conversation live, with
  the real lines and reply picks, in the game's own dialogue UI, and a late joiner catches
  up to the right line — a single call jumps straight to the owner's current node, live-
  measured at 0 ms of self-reported content latency after opening across every run of a
  2026-08-21 A/B (`docs/research/dialogue-join-latency.md`'s "Live A/B" section), because a
  joiner already standing near the NPC resolves through this mod's fast **adopt** path rather
  than a slower cross-instance replay. `dialogue_fast_join` (off by default; targets a
  *distance* join, where the joiner has to be walked in before their own dialogue opens) is
  still unverified live for that specific path — the same doc's honest account of what this
  rig's fixed player-adjacent-to-NPC setup could and couldn't exercise.
- **A joiner who is genuinely far from the NPC** does not just lag — measured live, they never
  open the conversation at all, because the engine's own 10-unit conversation-start range
  (disassembled and confirmed by reading it live out of the process) never resolves for a
  player character this build drive-controls rather than walks via the action queue. `coop.ini
  dialogue_join_teleport=1` (off by default) closes that gap by placing the joiner next to the
  NPC for an automatic join and putting them back once the conversation ends — measured live in
  both ownership directions at 4-5 ms to open and 0-15 ms of content latency, matching an
  adjacent join exactly. A second, independent defect found alongside it — the existing
  last-resort recovery for a conversation shell that will not close could loop, unattended, for
  minutes with no effect — is fixed unconditionally (not gated on the flag above): see
  `docs/research/dialogue-join-latency.md`'s "Far-join live pass".
- **Scripted scenes play out the same on both screens**: cutscene actors and story NPCs
  replicate their positions, stances and movements, while staying exempt from the combat
  channel — the module decides who is un-killable, not the session.

An unfocused or minimised instance keeps simulating (the plugin suppresses the focus
pause), which is what makes one-machine testing practical at all.

### Mods work, even mismatched ones

Two players do not have to run the same mods. At join time each side fingerprints its
install — the executable, the override folder, `appearance.2da`, the journal, the string
table — and the two compare notes. The result is a **per-channel** decision, not
all-or-nothing:

- a different **executable** is refused (every engine address is per-binary);
- a different **`appearance.2da`** just drops the appearance sync (the phantom keeps a
  default look) rather than risk painting the wrong body;
- a mod-**edited module** drops the object-id-keyed channels (enemies, doors, items, party)
  *for that module only* — they come back by themselves in the next module the two installs
  agree on — while phantoms, journal and the rest continue throughout;
- a different **save** drops the same channels and tells you to rejoin, because joining
  *takes the host's world* (your character, their world) and that makes the two saves
  identical by construction; `--require-identical-save` refuses instead;
- a different **global-variable table** drops shared quest state (its keys would name
  different variables);
- differing **override folders** — the normal case for two modded installs — just
  produce a warning that explains any gear or item that does not appear.

Every one of those decisions carries a code (`E_EXE`, `W_GIT`, `W_SAVE`, ...) naming the
channels it switched off, and is **said on screen** as it happens — a channel that goes quiet
for safety is otherwise indistinguishable from one that broke.

Everything co-op ships as a game file uses a `k_coop_` name, so it can never collide with
a mod, and only ever adds to the override folder.

Verified live on two GOG instances: the whole session started from inside the game (Host on
one, Connect on the other, both loading their real saved characters with no injected
clicks), phantoms both ways with appearance and equipment, run-gait, item drop/pickup,
enemy health/death mirroring, enemy and companion presence (positions, health and rendered
bodies identical across both games), door unlock propagation, map pins, and going down and
being picked up by the other player. And no longer only on the Endar Spire: the same pair
has been warped live through Taris' Lower City (where the client set off the Black Vulkar
ambushes — see above) and Tatooine's Sand People territory, where the census ran at its
full 32-creature cap with the client mirroring 32 of 32 through a real multi-enemy battle
that wore both players down through their own damage pipelines
(`docs/research/transition-window-races.md` is the trail of what those transitions found). The mod-compatibility handshake is unit-tested (628
tests in the suite); it has been observed agreeing on two identical installs but its mismatch
paths have not had a live pass. See `docs/research/` for the precise per-feature status and the
reverse-engineering behind each.

## Requirements

- Windows. The packaged zip needs nothing else; building from source needs the .NET 10 SDK
  and the MSVC x86 toolset for the plugin (NativeAOT `win-x86`).
- **The DRM-free GOG build of KOTOR 1** (v1.0.3). The Steam copy's DRM wrapper refuses
  a second instance and its packed binary defeats static analysis; the GOG build has
  neither problem. Every engine address in the plugin is verified against this exact
  binary (no ASLR, so addresses are absolute and stable).
- For two instances on one machine: a second copy of the game directory. The retail
  single-instance guard is just a named mutex; `agent launch --instance 2` starts the
  second copy with the mutex renamed in memory.

## Quick start

Grab `kotor-coop-<version>.zip` (built with `pwsh tools\package.ps1`, or from a release),
extract it anywhere, and install it into your game directory:

```powershell
pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

That's the plugin, the agent and the co-op override files all in place in one step —
`install.ps1 -Uninstall` takes them back out again. No .NET SDK or MSVC toolset needed;
those are only for building from source (below).

Then just play: launch KOTOR, click **Co-op** on the main menu, and one of you presses
**Host** while the other picks that server and presses **Connect**. Optionally edit the
`coop.ini` the package dropped beside the game to set your player name, a password, or a
fixed host address.

### Building from source

```powershell
dotnet build KotorCoop.slnx
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'

# copy the agent next to the game so the mod can start it itself
Copy-Item src\KotorCoop.Agent\bin\Debug\net10.0-windows\* 'C:\GOG Games\Star Wars KOTOR (GOG)' -Recurse -Force
```

Or build the same zip `install.ps1` above unpacks: `pwsh tools\package.ps1` writes it to
`dist\kotor-coop-<version>.zip`.

For development — two instances driven unattended on one machine:

```powershell
pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\demo.ps1 -Stop    # shut everything down
```

By hand, the pieces are:

```powershell
$agent = "src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe"

& $agent launch --windowed --load-save 1 --game-path 'C:\GOG Games\...\swkotor.exe'
& $agent launch --instance 2 --game-path 'D:\Games\swkotor-gog-p2\swkotor.exe'

# one agent per instance; --pid because two swkotor.exe are running
& $agent run --pid <pid1> --kotor --host --port 27140 --name Revan
& $agent run --pid <pid2> --kotor --join 127.0.0.1:27140 --name Malak
```

No game handy? `KotorCoop.MockGame` is a stand-in process the agent drives through the
identical code path:

```powershell
dotnet run --project src/KotorCoop.MockGame -- --host --name Revan
dotnet run --project src/KotorCoop.MockGame -- --join 127.0.0.1 --name Malak
```

## How it works

```
 swkotor.exe  (x86)                agent (x64, external)              network
+-------------------+   found by   +------------------+
| dinput8.dll proxy |  signature   |  KotorGameBridge |   PlayerState,
|  (C# NativeAOT)   | <----------> |  reads player,   |   items, enemies,   UDP
|  SharedBlock      |    scan      |  writes peers    | <----------------> peers
|  frame hook       |              |  runs the        |   flags, journal
|  engine calls     |              |  CoopSession     |
+-------------------+              +------------------+
```

- **Division of labour**: all discovery is external (the agent scans for the creature
  vtable, reads state, runs the UDP session); all engine calls are in-process (the
  plugin hooks the `SwapBuffers` IAT slot for a main-thread per-frame callback and
  calls the engine's own NWScript machinery: `CreateObject`, `ActionMoveToLocation`,
  `CreateItemOnFloor`, `SetState`, ...). The two halves meet at a versioned shared
  block the plugin allocates and the agent finds by signature.
- **Star relay, not mesh.** Clients talk only to the host; one machine decides what is
  true. Same trade FromSoftware makes.
- **Avatars stream, world truth is reliable.** Positions are unreliable and superseded
  every 50 ms; flags, items, doors and journal entries are retransmitted until acked,
  with version tripwires in every snapshot for divergence repair.
- **Everything monotonic merges upward** (quest states, door states, globals), so
  replayed or reordered packets can never walk the world backwards.
- **Authority is explicit**: your character is yours alone; enemies are the host's
  (clients' copies are pinned at 1 hp and mirror the host's outcomes, with client
  damage merged in host-side); a phantom belongs to its player and cannot be killed
  locally, only knocked about.

`docs/research/` is the deep documentation: verified engine addresses and structures,
traps (each of which cost real time), per-feature status, and the research trail.
Start with `docs/research/shared-state-audit.md` for what actually replicates today and
what only looks like it does. `tools/` holds the helper scripts and the symbol-database
queries everything was derived from.

## Layout

```
src/KotorCoop.Protocol   wire format: packets, codec, player state
src/KotorCoop.Memory     OpenProcess, AOB scanner, engine bridge, offset profiles
src/KotorCoop.Session    UDP transport, host/client, interpolation, stores, agent pump
src/KotorCoop.Agent      CLI: launch, load, run, scan, track, info
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86, installs as dinput8.dll)
src/KotorCoop.Memory     OpenProcess, scanner, engine bridge, GameFiles/ (KEY/BIF/GFF/2DA/TLK)
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
tests/KotorCoop.Tests    unit + loopback integration tests
profiles/                JSON offset profiles; profiles/override ships k_coop_ game files
docs/research/           verified reverse-engineering write-ups behind each feature
tools/                   RE helpers, demo scripts, the kotor1_0_3 symbol database
```

## Honest limitations

- **Only the host can switch party character.** The companions are driven by the host's own
  AI and mirrored to everyone else, so a client taking one over would fight its own mirror;
  Tab and the portraits are locked there on purpose. On the host they work normally. The lock
  lasts exactly as long as the session: disconnecting hands the whole party straight back —
  Tab, the portraits, and the companions themselves, out of the mirror's faction hold and off
  its health pin — and if you were playing *as* a companion when you connected, control and
  the camera come back to your own character rather than leaving you stuck watching a body
  the host now drives. (`control_return=0` opts out of the camera half.)
- **A mirrored companion cannot swing on a client — except, now, for one, in one narrow
  case, and it is OFF by default.** The presence mirror re-claims and wipes every companion's
  action queue every pass, so only the host's own companions ever really fight; a client sees
  their swings replayed as animation, not damage. `companion_local_combat=1` releases the ONE
  companion standing close enough to both this player and the enemy `local_combat` (item 1,
  proven live) is already fighting locally to its own engine AI instead — the same "let the
  engine's own AI run it" trick `local_combat` uses for an enemy, gated by
  `CompanionLocalFightRule` on a triple-proximity check so a released companion can never end
  up fighting something anchored to wherever the host actually is. A released companion's hit
  already has a credit path with no code changes needed — `PeerDamageController` already treats
  every party roster id as "ours to credit" — but its own health is not forwarded uphill (no
  such channel exists), so it is simply left alone while released and snapped back to the
  host's figure the moment it is reclaimed. Built and unit-tested, **not yet run against the
  live game**: see [docs/research/party-reconciliation.md](docs/research/party-reconciliation.md)'s
  dated 2026-08-21 entry for the design question and why the gate is this tight.
- **One conversation at a time.** While a player is talking, nobody else can start a
  dialogue; the other player spectates rather than getting their own copy. That is the
  design (it keeps one plot line advancing), not a limitation to be lifted. While a player
  is mid-conversation the session freezes enemy publication for them, because the engine
  disarms hostiles during dialogue.
- **On the Endar Spire's command deck nobody can be downed — and that is KOTOR, not us.**
  The tutorial deck's area script pins the player character with min-one-hit-point
  (`k_pend_area01`: `SetMinOneHP(GetFirstPC(), TRUE)`) and only releases it when you reach
  the starboard section. While it holds, damage floors at exactly 1 hit point and no death
  path runs — for local hits and forwarded hits alike, in co-op and in a normal
  playthrough. Forwarded damage still lands and the health bar still tracks the beating;
  the session logs which floor is in the way rather than pretending the damage went
  missing. See [docs/research/min-one-hitpoint.md](docs/research/min-one-hitpoint.md).
- **Enemies choose targets purely by proximity.** There is no threat shaping, so which
  player an enemy picks is the engine's nearest-hostile rule and nothing else.
- ~~**Encounters only fire for the host.**~~ No longer: a client walking into an ambush
  reports the polygon it is standing in, the host's engine fires the encounter for that
  player's phantom (the `PlayerOnly` gate is suspended for the one call — the remote
  player *is* a player), and the spawned enemies materialise on the client as driven
  stand-ins. Verified live on the Taris Lower City Vulkar ambushes: the client walked in,
  three encounters fired host-side, all nine spawned Vulkars appeared on the client, and
  damage flowed both ways — the spawns hurt the client through its phantom, and the
  client's own hits were spent by the host on the real bodies.
- **A generic trigger's OnEnter (the classic "walk into the room, the scene starts") now
  relays from a client to the host — live-verified 2026-08-21, ON by default.** A client's own
  story triggers already fired correctly on the client's own engine; the gap was the HOST never
  running the same script, so a plot `CreateObject`/`DestroyObject` in it drifted the two
  instances' ids apart. `TriggerRelay` reports which generic triggers a client is standing in
  (excluding traps and area transitions, and filtered by a `coop.ini` allowlist pattern) and the
  host runs the trigger's own `ScriptOnEnter` against that peer's phantom, the same `RunScript`
  call the engine's own `EventHandler` makes. Live pass on `end_m01ab`: the story trigger
  `end_trig_carth02` fired once against the client's phantom, stayed at "fired 1" across four
  separate re-visits (the per-module fired-set dedup held), and the negative control
  (`k_trg_stealth`) — confirmed geometrically inside the client's own position — never entered
  the watch's report at all. `coop.ini` `trigger_relay=0` opts out. See
  [docs/research/trigger-relay.md](docs/research/trigger-relay.md) §11.
- **Mid-module movies (a script's own `PlayMovie`, not a transition) are captured and relayed
  now, gated off pending a live pass.** `MoviePlayHook` detours
  `CClientExoAppInternal::PlayMovie 0x00602CF0` (VERIFIED disassembled; never the transition
  path, by the call graph) to capture WHICH resref a mid-module call played, and
  `MovieRelayQueue.Decide` (pure, tested) picks play-now / defer-to-next-transition / ignore for
  the receiving peer. `coop.ini` `movie_relay=0` by default until live-verified. See
  [docs/research/cinematics.md](docs/research/cinematics.md)'s 2026-08-22 section and
  [docs/research/trigger-relay.md](docs/research/trigger-relay.md) §6.
- ~~**Item identity is a blueprint resref**~~ — no longer, on the wire: an item that crosses
  between players now carries **what makes it that item** — its stack size, its charges, its
  installed upgrades, and whether it is identified (plus the stolen and non-equippable bits) —
  and the receiving instance writes them onto the body it just created, the same five fields
  the engine's own upgrade workbench copies when it rebuilds an upgraded weapon from its
  template. An identity nobody could read stays explicitly *unknown* rather than being faked
  as stock, and merges upward: a stock re-announcement can never strip an upgraded weapon.
  See [docs/research/item-identity.md](docs/research/item-identity.md).
  **Still open:** nothing yet *detects* a player putting an item on the ground — KOTOR 1 has
  no drop UI, so the ground channel is still driven by the harness (the dashboard's `drop`
  key, which can now name a live item by object id to send its real state). And the apply
  path has not had a live pass: every offset is read out of the retail binary's own save/load
  routines, but a guarded no-op is what a wrong one produces, not a crash.
- **The compatibility check is complete but has never seen a real mismatch.** It now covers
  the executable, the override folder, `appearance.2da`, the other 2DAs, `dialog.tlk`,
  `global.jrl`, the module census, the globals catalogue, **save identity** (module +
  `Mod_NextObjId` counters + the stored GIT's id list) and **each module's GIT**, computed
  lazily off the game thread on the way into that module and re-judged on every module change.
  Each difference produces a coded decision (`E_EXE`, `W_GIT`, `W_SAVE`...) that names the
  channels it cost and says so on screen. What is still open is evidence: every mismatch path
  is unit-tested and none has been run live — two identical installs only ever exercise the
  agreeing case. And note what the bullet above does *not* buy you here: an item now carries
  its own mutable state, but it is still named by resref, so two installs whose *same* resref
  names a differently-modded item are not detected at all.
- **Minigames (swoop races, forced turret sequences) are detected and the peer is parked —
  not yet live-verified.** A player inside `CSWCModule::GetMiniGame` is flagged on the wire
  (`PlayerState.FlagInMinigame`); a peer's instance parks that player's phantom instead of
  mirroring it, and the minigame player's own instance stops publishing enemy/party presence
  and applying forwarded damage for the duration. Nobody has yet watched this run in a
  two-instance session — see [docs/research/minigames.md](docs/research/minigames.md) §7 for
  what a live pass needs to check, including whether damage taken during the sequence lands
  as a lump the instant it ends. Pazaak is unrelated engine machinery and is not covered.
- **Non-damage combat effects (stun, paralyze, knockdown, slow, frightened, sleep, choke,
  Force push/whirlwind) do not cross the wire yet, and the mechanism that will is OFF by
  default.** Plain damage already forwards through `PhantomHitHook`, but today an enemy that
  Force-Stuns a peer's phantom in your game leaves them free to act on their own screen, and a
  client's Stasis on an enemy only affects their private mirror, which the host drives anyway.
  A hook on the engine's generic effect dispatcher (`PhantomEffectHook`) now captures and
  forwards the whole allowlist, and Slow/Choke/Whirlwind/Frightened/Stun (the droid-scoped
  variant) have a real apply-side engine call; Knockdown, Paralyze and Sleep are captured but
  never replayed — one because the VM routine that builds it has an unverified argument count,
  the other two because static analysis alone could not match their engine sub-code to an
  English name (no `nwscript.nss` in this repository to check against). `coop.ini`
  `effect_sync=0` keeps the whole channel — capture, forward and apply — off until a live pass
  confirms it. See [docs/research/effect-sync.md](docs/research/effect-sync.md).
- **A joiner's progress now survives to the next session — mid-session save/load does not,
  yet.** "Resume tomorrow" works: when a client saves in-game (or disconnects) while playing
  in the adopted world, their progressed character — inventory, equipment, alignment, feats —
  is captured to a sidecar (`saves\kotorcoop-character.pifo` + `.json`) the next join prefers
  automatically over their stale own save, with an on-screen line saying which character was
  used. **Still open:** a HOST loading a different save mid-session is detected and refused
  on screen (clients are dropped to the reconnect path they already have) rather than served a
  stale world — but whether an in-game "Load Game" menu action reliably trips the detection at
  all (as opposed to only a subsequent save) is unverified; a client loading mid-session is
  detected but NOT auto-rejoined (a save and a load look identical on this signal, so
  distinguishing them is deferred). None of this has run on the live rig yet — see
  [docs/research/session-save.md](docs/research/session-save.md)'s "Phase 1 shipped" section.
- **Dialogue reward-node consequences (item grants, recruitment) are built and not yet
  live-verified at all.** A reward node's script only ever ran on the conversation OWNER's
  instance — the spectator never calls `HandleReply` — so an item placed into the speaker's
  inventory used to land on one screen only. The owner's inventory is now diffed across the
  conversation and the net-new items are given to every other player through the same
  giveloot cheat channel materialised loot already uses (`dialogue_item_sync`, on by
  default). Alignment shifts are left to diverge on purpose — that is two people making
  different choices in one story, not a bug. **Recruitment availability is the riskier
  half and defaults OFF** (`dialogue_recruit_sync=0`): it pokes a peer's recruited-companion
  flag directly onto this instance's own party table rather than only logging that
  recruitment does not replicate, but whether the bare flag is enough for the engine to spawn
  a real body afterward — or whether the game's own recruit routine also records a second
  association elsewhere in the table that a raw flag poke does not touch — has never been
  checked against the running game. See
  [docs/research/dialogue-consequences.md](docs/research/dialogue-consequences.md)'s "Shipped"
  section for the full list of what a live pass still needs to confirm.
- **A body left alive on one screen and dead on the other can now be corrected — partially
  live-verified.** Ordinary enemy death only ever reaches the 32-slot combat census; a
  creature beyond that cap, killed by a script (`DestroyObject`, a cutscene, `SetPlotFlag`
  then a kill), killed while the two players were apart, or killed by a client's own engine
  under `local_combat` before the host confirmed, could stay alive on one screen forever. The
  enemy owner now publishes every creature its engine knows about in the loaded area — not
  just the fought-over 32 — every couple of seconds, and a mirroring instance kills or
  destroys its own copy to match; if the owner is the one that disagrees (alive there, dead
  here), it only logs an anomaly and counts it (`status` shows "liveness anomalies: N") —
  never a forced resurrection. Live pass 2026-08-22: the owner's scan/derive and the
  never-resurrect anomaly path are both confirmed clean and repeatable, but every test module
  small enough to reach in the time available never exceeds the ordinary 32-slot census, so
  the channel's own distinguishing case (a creature the ordinary census does NOT already
  cover) was never independently exercised. `coop.ini liveness_sync=0` stays the default
  until that specific gap closes — see
  [docs/research/creature-liveness.md](docs/research/creature-liveness.md)'s dated live-pass
  section, which also carries the live-test script (`tools/live-liveness.ps1`).
- **A quest journal entry removed on one screen (`RemoveJournalQuestEntry`, a real operation
  12 scripts across 8 modules use) now removes on the other too — live-verified 2026-08-22.**
  A raw read of the engine's own journal array confirmed the entry genuinely vanishes (the
  array shrinks by one), not merely gets hidden — settling the one open question against the
  disassembly-only read that shipped it. `coop.ini journal_remove_sync=1` is now the default.
  A separate, newly-found gap from the same pass: a genuine DECREASE to an existing journal
  entry's state (a kill counter ticking down, a deliberate stage push-back) logs success but
  does not visibly change the engine's own journal array — recorded as an open defect in
  [docs/research/quest-sync.md](docs/research/quest-sync.md)'s dated live-pass section, since
  there is no flag to gate it behind.
- **Alignment (light/dark) is per-player on purpose.** Each character stores its own
  alignment, read by that character's own dialogue gates, dark-side powers and ending —
  two players making different choices in one story is a feature of playing together, not a
  bug, and needs no sync channel. The one consequence worth knowing: an alignment-gated
  dialogue option reflects the conversation OWNER's alignment, since only the owner runs the
  conversation — a spectating partner sees the options the owner qualifies for, not their
  own. See [docs/research/dialogue-consequences.md](docs/research/dialogue-consequences.md).
- **Announced transitions (smoother follow) are live-verified and ON by default.** A host's
  plugin announces a transition's destination the instant its engine commits to it — a door's
  0.5 s fade window, not the several-to-tens-of-seconds wait for its published module to
  actually change — so a follower can start its own warp immediately and both loading screens
  overlap instead of running back to back. Live pass on `end_m01aa` → `end_m01ab`: the
  host-door-use-to-follower-warp interval dropped from 1648 ms to 181 ms (~9x), and a
  movie-carrying transition (the Endar-Spire-escape shape) showed both instances' stalls
  overlapping within 32 ms of each other instead of running back to back, with no crash either
  time. `coop.ini transition_announce=0` opts out. The optional fade-to-black
  (`transition_announce_fade=1`, still OFF by default) never hung or stuck black across three
  transitions, but its direction argument is still an unconfirmed best guess — no screenshot
  capture was available to check it visually. A client's own premature door crossing can also be
  refused instead of completing and being yanked back — live-verified 2026-08-22 and now ON by
  default (`client_door_refusal=0` opts out). The blocker that stopped this feature's own
  decisive check from running at all (a client's real door crossings were refused by the
  vanilla single-player party-together gate before this feature's code ever ran — the
  client-side `CSWParty` struct keeps a stale, higher member count than the real party table
  after a companion is removed locally) is root-caused and fixed
  (`coop.ini party_together_fix=1`, new, default ON — see
  [docs/research/party-reconciliation.md](docs/research/party-reconciliation.md)). With that
  fixed, the decisive check ran: `client_door_refusal` genuinely stops the transition rather
  than merely logging a refusal, confirmed twice live. See
  [docs/research/transition-smoothness.md](docs/research/transition-smoothness.md) §9.
- KOTOR 2 is untouched, and only GOG v1.0.3 addresses are known.
