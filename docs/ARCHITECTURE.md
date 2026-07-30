# Architecture

## The shape of the problem

Seamless Co-op for Elden Ring is not a netcode implementation. Elden Ring already
replicates phantoms; the mod removes FromSoftware's restrictions on that replication and
adds a supplementary channel for world state. That is why one person could build it.

KOTOR has no replication layer at all. So this project has to supply one, which sets the
boundary of what a proof of concept can honestly claim:

| Layer                                    | Elden Ring          | KOTOR                        |
| ---------------------------------------- | ------------------- | ---------------------------- |
| Transport and session                    | exists, repurposed  | must be built (**built here**) |
| Avatar replication (position, animation) | exists, unlocked    | must be built (**built here**) |
| World progression sync                   | added by the mod    | must be built (**built here**) |
| A remote entity the engine can draw      | exists (phantoms)   | **does not exist**           |

The first three rows are done and tested. The fourth is why this stops where it does.

## Layering

```
KotorCoop.Agent / KotorCoop.MockGame        entry points
        |
KotorCoop.Session       CoopAgent, CoopHost, CoopClient, SnapshotBuffer,
        |               EventFlagStore, ReliableFlagSender, UdpChannel
        |
KotorCoop.Memory        ProcessMemory, PatternScanner, PointerChain,
        |               GameProfile, ProfileGameBridge, ValueScanner
        |
KotorCoop.Protocol      PacketCodec, PlayerState, Vector3f
```

`IGameBridge` is the only seam that knows about a specific game, and the only
implementation is `ProfileGameBridge`, which is driven entirely by JSON. That is why the
identical binary drives the mock harness and `swkotor.exe`.

## Memory access

External, not injected. `OpenProcess` with `PROCESS_VM_READ | PROCESS_VM_WRITE |
PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION`, then `ReadProcessMemory` and
`WriteProcessMemory`.

Details that matter:

- **Cross-bitness.** A 64-bit agent reading a 32-bit target is fine, but pointers in the
  target are 4 bytes. `ProcessMemory.PointerSize` follows `IsWow64Process`, and
  `TryReadPointer` zero-extends accordingly. Getting this wrong silently produces garbage
  addresses on every other read.
- **Module enumeration.** `Process.Modules` fails against a 32-bit target from a 64-bit
  caller. `EnumProcessModulesEx` with `LIST_MODULES_ALL` is used instead.
- **Scan scope.** Game object state lives in `MEM_PRIVATE` committed regions, so that is
  the default sweep. Guard pages are skipped: touching one would disturb the target.
- **Stale signatures.** Freed memory keeps its contents, so a scan can find a dead copy of
  a signature and anchor on it. The mock harness wipes its magic marker on dispose for
  exactly this reason, and it is a real hazard against a real game too.
- **Discovery scans by snapshot diff, not enumeration.** KOTOR has ~120 MB of private
  committed memory, or ~31 million floats. Enumerating every plausible candidate costs
  about a gigabyte, and capping the list biases the survivors towards low addresses and
  can exclude the player object entirely. `ValueScanner.TakeSnapshot` copies the bytes
  (0.1s on this install) and `DiffFloats` compares against live memory (0.3s), giving a
  complete first pass at fixed cost.

`ProfileGameBridge` re-resolves its anchor after ten consecutive read failures, since a
save load moves everything the pointer chain points at.

## Wire protocol

Binary, little-endian, hand-written. A 4-byte header (`magic`, `version`, `type`) so
foreign traffic on the port costs almost nothing to reject.

Reads never throw on malformed input: `PacketReader` sets a `Failed` flag and yields
defaults, and the codec returns `null`. A corrupt datagram is a dropped datagram. There is
a test that fires 40 random datagrams at a live host and asserts the session survives.

**Packet budget.** Everything must fit one 1200-byte datagram. The worst case is a full
8-peer `WorldSnapshot`, and the first version of this did not fit: with 63-character names
and modules it came to ~1265 bytes, which would have meant every tick being silently
dropped once a session filled up. Names are now capped at 24 and modules at 32 (Odyssey
ResRefs are 16), giving ~705 bytes worst case. `FullPeerSnapshotFitsInOneDatagram` guards
this.

## Session

**Star relay.** Clients send only to the host; the host broadcasts the combined world. One
extra hop of latency between two clients, in exchange for exactly one machine deciding
what is true. The host is a participant, not a spectator: it is peer 1 and its avatar is
in the same snapshot as everyone else's.

**Two reliability classes:**

- *Avatar state* is unreliable and rate-limited. A dropped position update is superseded
  50 ms later, so retransmitting it would add latency for nothing.
- *World flags* are reliable. `ReliableFlagSender` retransmits every 250 ms until
  acknowledged, then gives up after a bounded number of attempts rather than leaking into
  a dead link.

**Flag versioning.** Every flag change bumps a counter. The host stamps that counter into
every `WorldSnapshot`, 4 bytes per tick. A client whose version differs requests a full
flag snapshot, rate-limited to twice a second. So the common case costs 4 bytes and the
divergent case repairs completely, without streaming flag state continuously.

**Op deduplication.** UDP retransmission means the same flag op can arrive repeatedly, and
a toggle applied twice is a toggle undone. `EventFlagStore` remembers `(originPeer,
sequence)` pairs in a bounded ring.

## Interpolation

Remote peers are rendered at `now - 100 ms`, interpolated between the two snapshots that
bracket that time. Never extrapolated.

Extrapolation is what produces the rubber-banding Seamless Co-op players complain about:
guess where the peer went, be wrong, snap them back. A fixed 100 ms of latency instead
means a remote avatar is always shown at a position it genuinely occupied.

Two details that are easy to get wrong:

- **Facing interpolates along the shortest arc.** A naive lerp from 6.08 rad to 0.2 rad
  spins the avatar the long way round.
- **A module change is a teleport, not motion.** Interpolating across it would drag the
  avatar through the space between two areas, so the buffer snaps instead.

Peers in a different module are not written into the game at all: KOTOR loads one module
at a time, so a peer on Taris has no meaningful position in your copy of the Endar Spire.

## The mock harness

`KotorCoop.MockGame` exists because "does the co-op stack work" and "have we found KOTOR's
offsets" are separate questions, and conflating them makes both unanswerable.

It allocates a player structure in unmanaged memory, marks it with a magic signature, and
renders whatever remote avatars appear in it. The agent then attaches to that process by
PID and finds the block by **scanning for the signature**, exactly as it would against a
real game. So the scanner, the pointer arithmetic, the reads and the writes under test are
the production ones. The only thing being faked is the game.

Its rendering deliberately reads from the memory block rather than from the session: a
remote avatar only appears on screen if it was genuinely written into that process's
memory from outside.

## Testing

73 tests, no mocking of the memory layer:

- **Codec** round-trips, truncation, version rejection, foreign traffic, and the datagram
  budget.
- **Interpolation**: midpoint accuracy, clamping, module snapping, shortest-arc facing,
  out-of-order packet rejection, NaN rejection, pruning.
- **Flags**: version bumps, duplicate-op suppression, cross-peer sequence independence,
  retransmit backoff.
- **Memory**: allocates a real block, reads it, writes remote peers into it, and reads
  them back. Pointer chains are resolved through genuine indirections.
- **External target**: spawns `KotorCoop.MockGame` as a *separate process*, sweeps its
  whole address space for the signature, reads its live player state, watches it move, and
  writes remote peers into it from outside. This is the production configuration.
- **Session**: real `CoopHost` and `CoopClient` over loopback UDP. Connection, bidirectional
  avatar visibility, movement propagation, flag sync in both directions, three-way relay
  between two clients, and survival under random garbage traffic.

### Do not sweep your own address space

A finding worth recording, because it cost real time and the failure mode is opaque.

Full address-space sweeps (`PatternScanner.ScanAll`, `ValueScanner.TakeSnapshot`,
`FindPointersTo`) run cleanly against a separate process: measured against live KOTOR,
120 MB captured in 0.1s and diffed in 0.3s, repeatedly, with no ill effects.

Run against **the calling process itself**, the same code intermittently kills the .NET
runtime with `0x80131506` (`COR_E_EXECUTIONENGINE`), faulting inside `coreclr.dll`. Two
things go wrong at once: reading one's own GC heap through `ReadProcessMemory` in a tight
loop is not a supported thing to do to a live runtime, and a snapshot's own byte arrays
live inside the address space it is capturing, so each capture swallows the previous one
and memory compounds without bound.

Neither applies to the real tool, where the agent is a separate process from the game. It
only bites tests that attach to the test host. Those tests now either anchor on a known
address or scope the sweep to a bounded window, and the unbounded paths are exercised
against a spawned child process in `ExternalTargetTests`. `TakeSnapshot` and
`FindPointersTo` grew `minAddress`/`maxAddress` parameters for this.

## The position field is a per-frame mirror, not the authoritative state

This is the most important measured fact for anyone continuing, and it was not obvious:
**`0x23066A04` is an output copy that the engine rewrites every frame.** Reading it is
completely reliable. Writing it does not relocate the character.

Measured against the live game:

| Test | Result |
| --- | --- |
| Single write, then hold at ~125 Hz for 4s | engine overwrote our value **252 times in 4s** = 63 Hz, exactly frame rate. Final value reverted to the original. |
| Tight-loop hammer, 149,940 writes in 6s (~25 kHz) | render **stabilised** at the written position across frames a second apart, no flicker |

So the engine recomputes this field from its own simulation once per frame. Outwriting it
by three orders of magnitude does produce a stable *rendered* result, because the renderer
reads the mirror. But the simulation never learns about it: collision, AI and pathing still
use the upstream state, and the moment the writes stop the character snaps back.

What that means for a co-op mod:

- **Reading remote-player state out of KOTOR: solved.** The mirror is an accurate,
  cheap, stable source.
- **Drawing a remote player by poking memory: a dead end as an architecture.** It could
  produce a visual-only phantom at enormous cost (a tight write loop per remote player,
  permanently fighting the engine), with no collision and no interaction.
- **The real answer is what Seamless Co-op does: inject and hook.** Load code into the
  process and hook the engine's own object and movement functions, so a remote avatar is
  created and moved *through* the simulation instead of against it. That is a code-injection
  project, not a memory-editing one, and it is the honest boundary of this proof of concept.

A related finding: the "portrait resref at position + 0x174" layout that identifies the
player's object does **not** generalise. Sweeping the process for `"po_"` and validating a
world position 0x174 earlier returns exactly one real object, the PC. Every other creature
uses a different layout, so locating a spare body to drive is itself an open problem.

## Where this stops

The bridge can read a player out of any process given the right offsets. It can write
remote players into any process that has somewhere to put them. KOTOR does not.

Closing that gap means making the Odyssey engine spawn and drive a creature it was never
asked to spawn, which is reverse engineering, not configuration. Beyond it sit dialogue
arbitration, combat state, module transition gating, inventory, and saves.

The profile schema already has room for the answer: give `kotor1.json` a `remotes` block
pointing at whatever slot array can be driven, and the agent starts writing with no code
change. Everything above the bridge is finished and tested and does not care what game is
underneath.
