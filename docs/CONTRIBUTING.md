# Contributing

## Prerequisites

- Windows. Building from source needs the .NET 10 SDK and the Visual Studio 2022 MSVC x86
  C++ toolset (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64`) for the plugin's
  NativeAOT `win-x86` build.
- **The DRM-free GOG build of KOTOR 1** (v1.0.3), not Steam, for anything that touches a
  live game — addresses are verified against this binary and Steam's DRM/packer defeats
  both static analysis and a second instance. See `docs/FINDING-OFFSETS.md`. KOTOR 2 is
  untouched by this codebase.

## Clone, build, test

```powershell
dotnet build KotorCoop.slnx
dotnet test KotorCoop.slnx
```

`KotorCoop.slnx` deliberately omits `KotorCoop.Plugin` — it is NativeAOT win-x86 and cannot
be referenced from the x64 solution, so a green root build says nothing about plugin code.
Build the plugin separately (below) before trusting a plugin-touching change.

Tests are xUnit, parallelisation disabled assembly-wide (some suites self-attach to the
test process; see `docs/ARCHITECTURE.md`'s Testing section for why). Some suites bind real
UDP ports, so two concurrent `dotnet test` runs on one machine can flake
`CoopCommandDispatcherTests` — rerun that class alone before treating it as a regression.

## Plugin build and install

```powershell
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

`build.ps1`'s own `-GameDir` default is a Steam path — always pass your GOG directory
explicitly. Without `-Install` it only builds `dinput8.dll`; with it, the DLL and the
override assets are copied into the game directory too, which is what `demo.ps1` needs to
find a current plugin (`demo.ps1` itself does **not** deploy the plugin — see the memory
note "demo.ps1 does not deploy the plugin"). `-Uninstall` removes what `-Install` placed.
The script imports the MSVC x86 developer environment itself (`tools/devenv.ps1` does the
same thing standalone, if you need `link.exe`/`vswhere.exe` on `PATH` for something else).

After building the agent, copy it beside the game so the plugin can auto-start it:

```powershell
Copy-Item src\KotorCoop.Agent\bin\Debug\net10.0-windows\* 'C:\GOG Games\Star Wars KOTOR (GOG)' -Recurse -Force
```

## Running the live rig

```powershell
pwsh tools\demo.ps1               # launch both instances, load saves, connect the agents
pwsh tools\verify.ps1 -Host1 <pid> -Client2 <pid>   # or omit pids: it finds the two swkotor.exe by start order
pwsh tools\demo.ps1 -Stop         # tear down
```

`verify.ps1` prints one `PASS`/`FAIL` line per feature, reading real engine state (logs,
`blockpoke.ps1`, live memory reads) rather than trusting the agent's own bookkeeping — "the
agent believes X" is not the same claim as "X is true", and every check here is built to
tell the two apart. A `FAIL` names what it actually observed; treat a `FAIL` with no
detail as a check bug before treating it as a feature regression. `docs/live-verification.md`
is the narrative log of what live-verification passes have found, session by session — skim
its most recent entries before a live round so you know what is already confirmed and what
is still open.

Two traps worth knowing before your first live run: `-Save 2` targets the game's second
save slot, but the engine's own load-menu list skips the autosave, so `-Save 2` silently
lands on the *third* file on disk (see the memory note "save slot off-by-one"); and to check
whether the demo is actually up, use `Get-Process swkotor,KotorCoop.Agent`, not `pgrep` —
two of `verify.ps1`'s own checks are environment-flaky and are called out in its output when
they are.

Three-player variants exist as `demo3.ps1`/`verify3.ps1`. `tools/README.md` indexes every
other helper script (probes, RE tools, the `kotor1_0_3.db` symbol database).

## Repo layout

```
src/KotorCoop.Protocol   wire format: packets, codec, player state
src/KotorCoop.Memory     OpenProcess, AOB scanner, engine bridge, GameFiles/ (KEY/BIF/GFF/2DA/TLK)
src/KotorCoop.Session    UDP transport, host/client, interpolation, stores, agent pumps
src/KotorCoop.Agent      CLI: launch, load, run, scan, track, info; the command dispatcher
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86, installs as dinput8.dll)
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
tests/KotorCoop.Tests    unit + loopback integration tests
profiles/                JSON offset profiles; profiles/override ships k_coop_ game files
docs/                    ARCHITECTURE.md, CONTRIBUTING.md, CONFIG.md, CONSOLE.md,
                          TROUBLESHOOTING.md, live-verification.md, FINDING-OFFSETS.md,
                          re/ (core reverse-engineering), research/ (per-feature write-ups)
tools/                   RE helpers, live probes, demo/verify scripts, the symbol database
```

## Code conventions

- **Small partials by concern.** A class over a few hundred lines is split into
  `<Class>.<Concern>.cs` files, one concern per file, each with a short header comment
  saying what it owns. Verbatim moves only when splitting an existing file — no drive-by
  logic changes riding along with a file split.
- **Events are additive and synchronous.** `PluginEvents`/`AgentEvents`-style hubs raise on
  the same thread and from the exact point a condition is already detected; a new event adds
  a notification, it does not add a new detection or change existing control flow. Every
  raise is per-subscriber exception-isolated (see `PluginEvents.Raise` for the pattern) so
  one throwing subscriber cannot take the game down.
- **Pumps, not god methods.** Per-feature work in `KotorCoop.Session` lives in one
  `Pumps/<Feature>Pump.cs` per feature, called from `CoopAgent.PumpOnce`'s three cadences
  (every tick / ~150 ms / ~1 Hz — see `docs/ARCHITECTURE.md`). A new channel gets its own
  pump method, not a growing branch in an existing one.
- **Registries over switches where the shape repeats.** `ConsoleCommand`'s `VerbSpec` table,
  `CoopCommandDispatcher`'s `Dictionary<CoopCommandKind, Action<CoopCommand>>`, and
  `CoopHost`/`CoopClient`'s `Dictionary<PacketType, PacketHandler>` are the pattern: a
  declarative table built once, indexed by an enum, with each entry's actual logic living in
  a small method in the file for its area.
- **Keep every hazard comment.** A `⚠` warning, a "do not" note, or an explanation of *why*
  a value/order/offset is what it is exists because getting it wrong cost real time once.
  Move it verbatim with the code it documents; never delete one while refactoring nearby
  code.
- **Changelog narrative goes to `docs/research/`, not code comments.** Round numbers, dates,
  A/B test stories ("STRATEGY 2 (2026-08-21)", "ROUND 47") belong in the dated research
  write-up for that feature. A code comment should explain what is true now and why, with at
  most a one-line pointer to the research doc for the history.
- **Engine-write rules.** Every write against `swkotor.exe` follows the rules in
  `docs/ARCHITECTURE.md`'s "Engine-write rules" section: rebuild derived state after a field
  poke rather than just poking the field, prefer object ids over raw pointers and vtable
  slots over hardcoded call addresses, verify a researched offset live before shipping the
  write, remember `CExoString` lengths include the terminating null, and never dispatch an
  engine call while the script VM is mid-call.

## Adding a feature end-to-end

Most features cross all five projects. Using a new wire-synced flag or object state as the
running example, in order:

1. **Packet.** Add the record to `Packets/<Feature>Packets.cs` in `KotorCoop.Protocol`, and
   its encode/decode to `PacketCodec.<Feature>.cs`. Add the type to `PacketType.cs` — reuse
   no existing value, and never fill a deliberately-skipped number (some are reserved on
   paper for parallel work in flight). Guard the datagram budget if the new packet can appear
   in a full-lobby snapshot (`FullPeerSnapshotFitsInOneDatagram`-style test).
2. **Host/client handler.** Add a case to `CoopHost.<Feature>.cs`/`CoopClient.<Feature>.cs`
   and register it in the `Dictionary<PacketType, PacketHandler>` built in the constructor.
3. **Pump.** Add (or extend) a `Pumps/<Feature>Pump.cs` in `KotorCoop.Session`, called from
   `CoopAgent.PumpOnce` at whichever of the three cadences the feature needs — position-like
   data goes on the fast path, a full scan goes on the heavy pass.
4. **Bridge role interface.** Add the member to the relevant `I<Concern>Bridge` interface in
   `KotorCoop.Memory` (or a new one, added to `IGameBridge`'s composite list) with a default
   body gated by a `Supports*` flag if not every bridge can implement it — never remove or
   rename an existing member, since `IGameBridge` is hand-implemented by test fakes.
   Implement it in `KotorGameBridge.<Concern>.cs` (real game) and, if relevant,
   `ProfileGameBridge`/`KotorCoop.MockGame` (mock harness).
5. **`SharedBlock` field + version bump + `PluginBlock` offset.** Add the field to the
   relevant `SharedBlock.<Concern>.cs`/`SharedBlockView.<Concern>.cs` pair in
   `KotorCoop.Plugin`, bump `SharedBlock.Version` with a one-line comment saying what the new
   number carries, and mirror the exact same offset in
   `KotorGameBridge.PluginBlock.cs` on the agent side — the two are transcribed by hand and a
   mismatch is a silent "plugin not found." `ItemHarnessTests`/`CheatKindSyncTests`-style
   tests exist to catch a half-bumped version; add one if the feature introduces a new
   parallel-numbering scheme (cheat kinds, packet types) rather than a plain field.
6. **Controller.** Add or extend a controller in `KotorCoop.Plugin` and wire it into
   `PluginHost.Controllers.cs` (construction) and `PluginHost.Frame.cs` (the per-frame call,
   placed and commented for exactly where in the schedule it needs to run).
7. **Tests.** Codec round-trip/budget tests in `KotorCoop.Tests`; a session-level test if the
   feature has host/client interaction; a bridge/memory test if it reads or writes real
   memory. If the plugin-side logic is pure arithmetic (a ledger, a rule, a table with no
   `Odyssey`/`SharedBlockView` dependency), add it to `KotorCoop.Tests.csproj`'s
   `<Compile Include>` list instead of re-implementing the rule in the test — see
   `docs/ARCHITECTURE.md`'s "plugin compile-include trick".

Not every feature needs all seven steps (a local-only console verb skips the packet/handler
steps entirely; a read-only diagnostic skips the version bump). Use the shape that matches
what the feature actually needs, but keep the same file-per-concern discipline at each layer.

## Writing a research doc

`docs/research/*.md` are **append-only logs**: a doc gets a new dated section every time a
round of work touches its subject, rather than being rewritten in place, so the history of
what was tried and found stays readable. Tag every claim: **VERIFIED** (read out of the
binary, or asserted by a test), **NEEDS-LIVE** (reasoned but not yet observed running — every
one of these fails safe), or **INFERRED** (a reasonable deduction, not directly proven). A
doc that is superseded by a later one stays — mark it superseded and point at the doc that
replaced it, but do not delete it or treat its earlier claims as current.

After writing or extending one, add or update its row in `docs/research/README.md`'s index
(one line: doc name, subject, current status read off the doc's own most recent section) and,
if the feature's user-facing status changed, the corresponding row in the top-level
`README.md`'s "Status at a glance" table and (if it is a known gap) "Honest limitations".

## Parallel-session hazards

More than one agent or session can work this repo at once — worktrees included. Before
trusting a fresh worktree or a "clean" `dev` commit:

- **A worktree may not branch from where you expect.** `isolation: "worktree"` can cut from
  `main` rather than the current integration branch, sometimes 100+ commits behind. Check
  `git merge-base HEAD <branch>` right after a worktree is created; if it is far behind,
  rebase before doing any work, or later merges will silently resolve against a stale base
  (a version-number bump made against the wrong starting number is the classic failure).
- **Commit with explicit paths, not `git add -A`, while any other agent might share your
  checkout** (a live-rig agent in particular, since it must deploy into real game
  directories and cannot itself be worktree-isolated). A broad add sweeps another agent's
  uncommitted edits into your commit; a `git stash`/`git reset`/`git checkout` on a file you
  don't own can revert someone else's in-flight work.
- **Check `git log` and running processes at every resume**, not just at the start of a
  session. Another session may have committed since you last looked, and `Get-Process
  swkotor,KotorCoop.Agent` tells you whether a live rig is up before you `dotnet build` (a
  build races locked DLLs the demo is running out of) or kill anything.
- **Version-numbering counters are a recurring collision point** across parallel work:
  `SharedBlock.Version`, the agent-side `PluginSignature`/`CheatKinds` mirrors, and
  `PacketType` values are all separately-incrementing, hand-transcribed numbers. When
  integrating more than one branch that touches any of them, grep every constant and
  reconcile by hand rather than trusting a clean auto-merge — two branches can each claim the
  same next number, and git merges the resulting text conflict-free while leaving both halves
  broken.
- **A clean merge is not proof of a working merge.** Two branches can each be individually
  green while encoding opposite invariants that git merges without complaint (different files,
  no textual conflict). Build and run the full test suite after every integration, not just
  after each individual branch.
