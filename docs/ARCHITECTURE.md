# Architecture

## The shape of the problem

Seamless Co-op for Elden Ring is not a netcode implementation. Elden Ring already replicates
phantoms; the mod removes FromSoftware's restrictions on that replication and adds a
supplementary channel for world state — which is why one person could build it. KOTOR has no
replication layer at all, so this project has to supply one:

| Layer                                    | Elden Ring          | KOTOR                        |
| ---------------------------------------- | -------------------- | ---------------------------- |
| Transport and session                    | exists, repurposed   | built here                   |
| Avatar replication (position, animation) | exists, unlocked     | built here                   |
| World progression sync                   | added by the mod     | built here                   |
| A remote entity the engine can draw      | exists (phantoms)    | built here, by the plugin    |

The fourth row used to be the honest boundary of this project: memory-poking a position
field moves what the renderer shows but not what the simulation knows about, so a body
poked into existence that way has no collision and cannot be hit. The plugin closes that gap
by injecting into the process and driving the engine's own object system — spawning a real
`CSWSCreature` through `Odyssey.SpawnCreature` (the engine's own NWScript `CreateObject`,
called on the game thread) and stepping it every frame through the same movement/animation
calls a script would use. See `docs/re/spawn.md`/`docs/re/engine-movement.md` and "The
plugin" below.

## Process topology

```
   swkotor.exe (x86, player A)                        swkotor.exe (x86, player B)
   dinput8.dll proxy (KotorCoop.Plugin)                dinput8.dll proxy
        |  SharedBlock (shared memory, signature-      |  SharedBlock
        |  scanned, not IPC)                           |
   KotorCoop.Agent.exe (x64, external)             KotorCoop.Agent.exe (x64, external)
   KotorGameBridge <-> CoopAgent <-> CoopClient/Host    KotorGameBridge <-> CoopAgent <-> CoopClient/Host
        \                                               /
         \_______________ UDP star relay ______________/
                    (host is also a peer)
```

Each `swkotor.exe` loads the plugin as a `dinput8.dll` proxy the game itself resolves — no
game file is modified. The plugin allocates a block of unmanaged memory
(`SharedBlock`/`SharedBlockView`) and never talks to the network; the external agent attaches
with `OpenProcess`, finds that block by scanning for a signature, and pumps it every tick.
Only the agent speaks UDP; one instance hosts (a star relay, not a mesh).

## Project layering

```
KotorCoop.Agent / KotorCoop.MockGame        entry points (x64)
        |
KotorCoop.Session       CoopAgent, CoopHost, CoopClient, SnapshotBuffer, pumps
        |
KotorCoop.Memory        ProcessMemory, PatternScanner, GameProfile, KotorGameBridge
        |
KotorCoop.Protocol      PacketCodec, Packets, PlayerState, Vector3f
```

Dependencies point one way, Protocol outward to Agent/MockGame. `IGameBridge` is the only
seam that knows about a specific game; `ProfileGameBridge` is the JSON-profile
implementation the mock harness and (through `KotorGameBridge`) the real game both drive
through. `PacketCodec.cs` and `Packets.cs` are themselves split into per-feature partials —
`PacketCodec.<Feature>.cs` and `Packets/<Feature>Packets.cs`, one per wire channel (avatar
state, flags, items, journal, party, dialogue, combat, movies, chat...), mirroring
`PacketType.cs`. Adding a channel means adding a partial, not growing the existing ones.

`KotorCoop.Plugin` (x86, NativeAOT) cannot reference any of the above — different bitness —
but shares 5 dependency-free Protocol files by SOURCE via its `.csproj`'s `<Compile
Include>` block: `DialogueFollowClock.cs`, `DialogueFollowMachine.cs`,
`DialogueNodeFollower.cs`, `DialogueAutoJoinRule.cs`, `ContainerAnimationEdges.cs`. Any
wire-shaped rule the plugin needs to evaluate identically to the agent has to be pure and
dependency-free enough to live in Protocol and be pulled in this way.

## Memory access

External, not injected, from the agent's side (`OpenProcess` + `ReadProcessMemory`/
`WriteProcessMemory`). `KotorGameBridge` is `KotorGameBridge.cs` plus 18
`KotorGameBridge.<Concern>.cs` partials (Attach, Player, Party, Items, Doors, Enemies,
Journal, Travel, Cheats, Encounters, WorldState, ContainerLoot, GroundItems, Placeables,
Menu, Dialogue, Helpers, PluginBlock); `PluginBlock` mirrors `SharedBlock`'s `[FieldOffset]`s
field-for-field on the agent side.

Details that still matter: pointers in a 32-bit target are 4 bytes even from a 64-bit agent
(`ProcessMemory.PointerSize` follows `IsWow64Process`); `Process.Modules` fails
cross-bitness, so `EnumProcessModulesEx(LIST_MODULES_ALL)` is used instead; freed memory
keeps its contents, so a scan can anchor on a dead copy — the plugin's block carries a
`Heartbeat` dword the agent watches for exactly this reason; and discovery scans by
snapshot diff, not enumeration (`ValueScanner`), since diffing two ~120 MB snapshots gives a
complete first pass at fixed cost instead of enumerating tens of millions of candidates.
`KotorGameBridge` re-resolves the plugin block after repeated read failures, since a save
load or transition can free what it anchored on.

## The plugin (`KotorCoop.Plugin`, x86, NativeAOT)

Runs inside `swkotor.exe`. Two threads, strictly divided: a worker thread only touches the
shared block; the frame hook runs on the game's own thread and is the only place engine
calls happen — the engine is not reentrant.

**Three hook points**, installed in `PluginHost.Startup.cs` in a fixed, commented order
(`InstallCrashProbe` first — a fault during startup must be diagnosable like one during
play — then the shared block, `ConstructControllers`, then `InstallHooks`; every install
carries its own comment saying why its position matters, e.g. the block must exist before
`FuncInterpHook`, which needs `_view` to publish into):

- **`FrameHook`** — an IAT patch on the import slot holding `GDI32!SwapBuffers`
  (`0x0073D00C`), the last thing in a frame after both engine `MainLoop`s. Drives the
  per-frame callback (`OnFrame`) that runs every world controller; dormant at the main menu,
  where the looping menu movie routes the swap through Bink instead (`docs/re/frame-hook.md`).
- **`MainLoopHook`** — a call-site rewrite on `CClientExoApp::MainLoop` inside `WinMain`,
  which fires at the menu too. Drives `MenuFrame`: the Co-op button, server browser,
  password prompt, and (only while `FrameHook` is visibly stalled behind a load screen) a
  fallback ~10 Hz pump of the world pipeline.
- **`FuncInterpHook`** — an inline detour on the developer console's command interpreter
  (`FuncInterp 0x0044C1F0`). A co-op verb is published to the block and swallowed before the
  engine sees it (no "function not found"); anything else tail-calls the original
  interpreter, so the `~` console works at the menu too, independent of the frame hook.

**The per-frame schedule** (`PluginHost.Frame.cs`'s `OnFrame`) is one long, explicitly
ordered method, not a generic "list of controllers to iterate" — each call is preceded by a
comment saying why it precedes or follows its neighbour (e.g.
`Odyssey.CloseClientWorldIfServerModuleGone` runs before anything else touches the world,
because the engine's own map-init code dereferences a freed server module with no null
check; staging cheats run dead last, on state every earlier pass has already settled).
`PluginHost.Controllers.cs` constructs the ~35 per-frame controllers in one place, in the
same order as before the split — that order is incidental; the call order in `Frame.cs` is
what is load-bearing.

**`PluginEvents`** is a static, dependency-free typed event hub (`WorldLoadObserved`,
`ModuleTransitionStarted`, `ServerModuleGone`, `DialogueStarted`/`Ended`,
`LocalCombatEngagementChanged`, `PeerSlotSpawned`/`Destroyed`, `SessionConnected`/
`Released`) raised synchronously on the game thread, from the point each condition is
already detected — it adds a notification, not a new detection — with every raise
per-subscriber exception-isolated so one throwing subscriber cannot fail-fast the game from
inside the `[UnmanagedCallersOnly]` frame hook. Being dependency-free, it also compiles
directly into the test project.

**`PluginHost.SessionLifecycle.cs`** turns a session's presence in the block
(`Command == CommandSpawnAndDrive`) into engine state changes both ways:
`PumpSessionLifecycle` raises `SessionConnected`/`SessionReleased` on the edge, and every
controller holding engine or GUI state does its own teardown from `ReleaseSession` rather
than a second hand-written list — a structural fix for a bug class where per-field teardown
kept missing one field. `DrainSessionSpawns` destroys everything the session spawned across
several frames, since the engine refuses a destroy call while its script VM is mid-call.

### The `SharedBlock` contract

An explicit-layout (`[FieldOffset]`) struct allocated with `VirtualAlloc` (a fixed address
the agent can scan for) inside `swkotor.exe`. Split by concern (`SharedBlock.Session.cs`,
`.Combat.cs`, `.Dialogue.cs`, `.Items.cs`, `.Party.cs`, `.World.cs`, `.Browser.cs`,
`.Cheats.cs`, plus the base file), with a matching `SharedBlockView.<Concern>.cs` split for
the typed accessors both the plugin and the tests use. `KotorGameBridge.PluginBlock.cs`
mirrors the same offsets on the agent's x64 side — the two halves cannot share a type
(different bitness), so every offset is written twice on purpose, and a mismatch reads as
"plugin not found" rather than a crash.

`Magic` + `Version` + a guard dword form a 16-byte scan signature; bumping `Version`
intentionally severs old agents from a new plugin rather than letting them silently
misinterpret a moved field. `ItemHarnessTests`/`CheatKindSyncTests` assert the version and
cheat-kind numbering agree between the plugin's `SharedBlock.cs` and the agent's
`CheatKinds` mirror at build time — the two are transcribed by hand and drift is otherwise
silent.

## The agent (`KotorCoop.Agent`, x64)

`Program.cs` (45 lines) is pure routing: parse `AgentOptions`, dispatch to one
`Commands/<Verb>Command.cs` file per CLI verb. `AgentComposition.Compose` is the composition
root a live `agent run` uses: resolve coop.ini, compute the install's content digest, build
`AgentSettings`/`SessionManager`, wire the `CoopCommandDispatcher`, and wire logging so every
session event and console reply reach a file an unattended run can read.

**`CoopCommandDispatcher`** is the single command surface for the whole session — CLI
`--host`/`--join`, the in-game `~` console, the main-menu Co-op button and the server
browser panel all resolve to the same `Dispatch(CoopCommand)` call. `Partial` by area
(`.Connect.cs`, `.Session.cs`, `.SaveLoad.cs`, `.Items.cs`, `.Movies.cs`, `.Doors.cs`,
`.Effects.cs`, `.Cheats.cs`, `.Browser.cs`, `.Announcers.cs`, `.Diagnostics.cs`) with a
`Dictionary<CoopCommandKind, Action<CoopCommand>>` built once; the base file owns the 150 ms
tick loop, that table, and the shared staging-cheat request/answer channel (only one cheat
in flight at a time — the plugin has one 96-byte reply slot).

`CoopAgent` (612 lines) is the scheduler that joins the two halves: read the local player
out of the game, hand it to the session, take the session's view of everyone else, write it
back. The per-feature work lives in `KotorCoop.Session.Pumps` (`BarkPump`, `TransitionPump`
+ `.Movies`/`.Travel`, `LootPump`, `DialoguePump`, `PartyPump`, `CombatPump` +
`.PhantomAndEffects`/`.Enemies`, `LivenessPump`, `TriggerPump`, `PausePump`, `DeathPump`,
`WorldStatePump`); `PumpOnce` owns only the schedule and the module/peer bookkeeping its
typed events (`ModuleChanged`, `WorldAdopted`, `PeerJoined`, `PeerLeft`, `LocalStateRead`)
are raised from.

**Three cadences**, all wall-clock gated, not tick-counted — tying scan rate to send rate
was a real, measured bug: raising the tick rate multiplied how often the expensive scans
ran, and positions took 3.5-7.4 seconds to cross. **Every tick** (`DefaultTickRate = 30`
Hz): local player read/publish, position, pause, shared combat state, death arbitration
(checked against every peer, not the module-filtered set, or "everyone is down" can go true
while a peer is alive elsewhere), and, when fast-pathed, dialogue/party. **Every
`CombatPumpIntervalMs`** (150 ms default): enemy census, phantom damage, peer effects,
materialised loot, and (unless fast-pathed) dialogue/party/interaction locks/container
state — fast enough that a mirrored enemy's movement reads as movement, not a
once-a-second lurch. **Every `HeavyPumpIntervalMs`** (~1 Hz): items, progression, world
state, journal, doors, placeables, container loot — full memory scans, deliberately rare.

`SessionManager` owns a session's lifecycle (`Connect`/`Disconnect`/`Reconnect`/`Poll`) so the
console's `connect`/`disconnect`/`reconnect` can tear down and restand a session without
restarting the process, exposing `SessionPhase` through a `PhaseChanged` event raised from the
single `SetPhase` helper every transition already goes through.

`ConsoleCommand` (shared by the console and the dashboard) is a declarative `VerbSpec` table
built into a `Dictionary<string, ...>` once; each verb's argument grammar lives in a small
parse method inside the `ConsoleCommand.<Area>.cs` file for its area, and `Parse` itself
only strips an optional `coop`/`/coop` prefix and does the table lookup.

`ICoopSession` is a composite of 16 role interfaces (peer, lifecycle, flags, items, enemies,
damage, triggers, barks, movies, capabilities, party, dialogue, interaction locks, chat,
loot, journal), mirroring `IGameBridge` in `KotorCoop.Memory` (`IPlayerBridge` carries the 5
members with no sane default — attach status, read local state, write remote peers, clear
co-op state; every other interface default-implements, gated by a `Supports*` flag, so a
bridge that cannot do something simply doesn't override it). `CoopHost`/`CoopClient` are
`partial` per feature (`CoopHost.<Feature>.cs`/`CoopClient.<Feature>.cs`, one pair per wire
channel) with a `Dictionary<PacketType, PacketHandler>` built once in the constructor, and
`Channels/RelayQueue<T>` is the shared "receive locally, relay to everyone but the sender"
inbox/outbox idiom several unreliable channels used to hand-roll separately.

## Wire protocol, session and interpolation

Carried over from the pre-refactor design, still accurate. Binary, little-endian,
hand-written, with a 4-byte header (2-byte magic, 1-byte version, 1-byte type) so foreign
traffic on the port costs almost nothing to reject. Reads never throw on malformed input:
`PacketReader` sets a `Failed` flag and yields defaults, the codec returns `null`, and a
corrupt datagram is simply dropped — a test fires 40 random datagrams at a live host and
asserts the session survives.

**Packet budget.** `ProtocolConstants.MaxPacketSize = 1400` bytes (inside the smallest
common path MTU so nothing fragments). Raised once already, from 1200: a full 8-peer lobby
with every string field at its cap came to ~1265 bytes and was silently dropped once a
session filled up, because the guard test measured a peer with no equipment. Per-field caps
(`MaxNameLength = 24`, `MaxModuleLength = 32`, `MaxTagLength = 32`) keep the worst case under
the limit; `FullPeerSnapshotFitsInOneDatagram` guards it.

**Star relay.** Clients send only to the host, which broadcasts the combined world and is a
participant, not a spectator — peer 1, its own avatar in the same snapshot as everyone else's.

**Two reliability classes.** Avatar state is unreliable and rate-limited
(`DefaultTickRate = 30` Hz) — a dropped position update is superseded next tick, so
retransmitting it would only add latency. World flags, items, journal, loot and dialogue
grants are reliable: `ReliableFlagSender` retransmits until acknowledged, then gives up
after a bounded number of attempts rather than leaking into a dead link.

**Flag versioning.** Every flag change bumps a counter the host stamps into every
`WorldSnapshot` (4 bytes/tick); a client whose version differs requests a full flag
snapshot, rate-limited. `EventFlagStore` also deduplicates by `(originPeer, sequence)` in a
bounded ring, since UDP retransmission means the same op can arrive twice and a toggle
applied twice is a toggle undone.

**Interpolation.** Remote peers render at `now - DefaultInterpolationDelayMs` (80 ms, just
over two tick intervals on a LAN), interpolated between the two bracketing snapshots — never
extrapolated, avoiding extrapolation-based rubber-banding. Facing interpolates along the
shortest arc; a module change is a teleport, not motion, snapping rather than dragging an
avatar through the space between two areas; and a peer in a different module (or the
engine's own minigame mode) is not written into the game at all — KOTOR loads one module at
a time, so a peer elsewhere has no meaningful position in your copy of it.

## The mock harness and testing

`KotorCoop.MockGame` exists because "does the co-op stack work" and "have we found KOTOR's
offsets" are separate questions, and conflating them makes both unanswerable. It allocates a
player structure in unmanaged memory (`GameStateBlock`), marks it with a magic signature, and
renders (`MockRenderer`) whatever remote avatars appear in it (`MockWorld`). The agent
attaches by PID and finds the block by scanning for the signature — exactly as it does
against the real game — so the scanner, pointer arithmetic, and reads/writes under test are
the production ones; only the game is faked, and rendering deliberately reads from the
memory block rather than the session, so a remote avatar only appears if genuinely written
in from outside.

`tests/KotorCoop.Tests`, xUnit, parallelisation disabled assembly-wide
(`AssemblyInfo.cs`) because `MemoryBridgeTests`/`ValueScannerTests` self-attach to the test
process and full address-space sweeps against your own runtime are unsafe: measured live,
sweeping a *separate* process (`PatternScanner.ScanAll`, `ValueScanner.TakeSnapshot`,
`FindPointersTo`) costs nothing (120 MB captured in 0.1 s, diffed in 0.3 s, repeatedly); run
against the calling process itself, the same code intermittently kills the .NET runtime with
`0x80131506` (`COR_E_EXECUTIONENGINE`) inside `coreclr.dll` — reading one's own GC heap
through `ReadProcessMemory` in a tight loop is unsupported, and a snapshot's own byte arrays
live inside the address space it is capturing, so each capture compounds the last. Neither
applies to the real tool (the agent is a separate process from the game); the tests that
attach to the test host now anchor on a known address or scope the sweep to a bounded
window, with the unbounded paths exercised only against a spawned child process in
`ExternalTargetTests`. Some suites also bind real UDP ports, including
`ProtocolConstants.DefaultPort`, so two concurrent test runs on one machine can flake
`CoopCommandDispatcherTests` — a port-collision failure is a machine-contention artifact,
worth one rerun before treating it as a regression.

What each group proves: **codec** tests round-trip, truncate, reject a version and foreign
traffic, and guard the datagram budget; **interpolation** covers midpoint accuracy,
clamping, module snapping, shortest-arc facing, out-of-order/NaN rejection, pruning;
**flags/items/journal/loot** cover version bumps, duplicate-op suppression, cross-peer
sequence independence, retransmit backoff; **memory** tests allocate a real block, write
remote peers into it and read them back through genuine pointer-chain indirections;
**external-target** tests spawn `KotorCoop.MockGame` as a separate process and sweep its
address space for the signature — the production configuration; **session** tests run real
`CoopHost`/`CoopClient` over loopback UDP (connection, bidirectional visibility, movement,
flag sync both ways, three-way relay, survival under random garbage); **dispatcher/console**
tests exercise `CoopCommandDispatcher`/`ConsoleCommand` directly rather than only through a
live game.

**The plugin compile-include trick.** The plugin is NativeAOT win-x86 and cannot be
referenced from the (x64) test project, but its pure-arithmetic files are exactly the ones
most worth testing directly rather than re-implementing their rules in a test (a mirrored
copy of a rule is a rule that can drift). `KotorCoop.Tests.csproj` pulls in ~50
dependency-free plugin files with `<Compile Include="..\..\src\KotorCoop.Plugin\X.cs"
Link="Plugin\X.cs" />` — `DamageDealtTable`, `DamageOwedLedger`, every `SharedBlock*`/
`SharedBlockView*` file, `PluginEvents`, and every standalone `*Rule.cs`/`*Ledger.cs`/
`*Table.cs`. Only genuinely dependency-free files qualify — anything touching
`SharedBlockView`'s unsafe pointer plumbing, `Odyssey`, or an engine call will not compile in
a 64-bit host. A new pure-logic plugin file worth testing directly means one more
`<Compile Include>` line, not a reinvented copy of its rule in the test project.

## Engine-write rules

Distilled from `docs/re/` (`object-model.md`, `engine-movement.md`, `rendering.md`,
`spawn.md`, `frame-hook.md`), `docs/research/` (indexed at `docs/research/README.md`), and
hard-won live-testing rounds; every write against `swkotor.exe` in this codebase follows
these:

- **Rebuild derived state after a field poke, don't just poke the field.** A raw write to a
  door's open-state byte moves the collision without telling the area
  (`CSWSArea::BudgeCreatures` does that); the engine's own `MoveToNextOpenState`/`OpenDoor`
  calls commit a change correctly.
- **Prefer object ids over raw pointers across time**, and dispatch through a vtable slot
  rather than a hardcoded call address. A pointer into a `CSWSCreature` can be freed and
  reused; an id resolved fresh through `CServerExoApp::GetGameObject` is what every
  controller here keys on, and `KotorGameBridge` revalidates its cached player pointer every
  tick. A creature's `animbase` override is reached through an adjustor thunk, not a flat
  function pointer, so guards along that path log what they actually saw.
- **Verify a researched offset live before shipping the write.** `docs/FINDING-OFFSETS.md`
  exists because a wrong offset reads plausible garbage and fails silently; every offset in
  `profiles/kotor1.json` and every `SharedBlock` field was confirmed against the running
  process, not inferred from disk bytes alone (the executable is packed). And `CExoString`
  lengths include the terminating null — cut at the first null when reading one out of engine
  memory; three separate bugs in this project's history came from treating the reported
  length as the character count.
- **Never dispatch an engine call while the script VM is mid-call.** `Odyssey.SpawnCreature`
  checks `CallStackSize == 0` before touching the routine table, and every destroy call in
  the plugin is retried across frames, because a declined call (VM busy) silently orphans
  whatever it was trying to remove otherwise.

## Where this stops

**KOTOR 2 is untouched.** Every offset here is verified against the retail GOG KOTOR 1 build
v1.0.3 specifically (no ASLR, disk bytes == memory bytes outside the packer stub); a KOTOR 2
port is a fresh reverse-engineering pass, not a config change.

Within KOTOR 1, what remains open — party-character switching, follow-AI stalls, local
companion combat, dialogue recruitment sync, swoop racing, creature-liveness
reconciliation — is tracked live in `README.md`'s "Honest limitations" section rather than
duplicated here, since that list changes with nearly every round of live testing; each item
links a dated write-up in `docs/research/`.
