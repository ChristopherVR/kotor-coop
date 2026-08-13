# The transition window: four races found by warping a live pair to Taris (2026-08-13)

The first attempt to test combat in a module other than the Endar Spire — `blockpoke
travel tar_m03aa` on the host of a live pair — killed the client within a second, twice.
Fixing that exposed three more defects hiding in the same few seconds, none of which could
ever appear on the single-module demo save. All four share one shape: **state read in one
world, acted on in another.**

Everything below was measured live on the GOG pair (rig: `tools/demo.ps1`), fixed, and
re-verified live except where noted.

## 1. The client died following any cross-module travel — FIXED, verified

`FIRST-CHANCE access violation ... eip 0x5F304D91` with the eip OUTSIDE the exe image and
nothing after it in the log. The eip was inside our own NativeAOT `dinput8.dll` — resolved
by taking the module's load range from a live run and handing the RVA to `llvm-symbolizer
--obj=dinput8.dll --relative-address` (ships with VS; the PDB sits next to the installed
dll). It named `EncounterController.HandleWatch`, EncounterController.cs:157:

```
Vec3 position = Odyssey.GetPosition(player);   // player = (nint)block.PlayerCreature
```

`block.PlayerCreature` is **agent-written**, and through a module transition the agent's
process reads fail — so the block keeps the OLD world's creature pointer while a new epoch
is already up (the epoch gate reopens the moment the new server module constructs, seconds
before the agent recovers). The watch dereferenced the freed pointer; it survived by luck
for ~1 s until the heap page was decommitted, then the AV escalated to a NativeAOT
fail-fast, which bypasses every log.

Fixed at all four consumers (EncounterController, EnemyPresenceController,
PartyPresenceController, PartyRosterController), the same way PhantomController always did
it: **the block's pointer is a liveness signal only; anything the plugin dereferences is
resolved fresh via `Odyssey.GetPlayerCreaturePointer()`**, which returns null while there
is no world. Verified: the same warp that killed the client twice now completes, repeatedly.

## 2. Stale block inputs spawned bodies for the wrong world — FIXED, verified

Same window, host side: arriving in Taris, the plugin read the pre-transition remote slot
(the agent could not clear it — its reads were failing) and spawned a phantom for a peer
still standing on the Endar Spire. That burned an object id, and when the phantom was
destroyed three seconds later, one census pass caught the dying body after its slot was
cleared and published it as a dead enemy — which the peer dutifully executed against a
stand-in it had materialised for the ghost.

Two fixes:

* `SharedBlockView.ClearAgentWorldInputs()` — on EVERY epoch invalidation the plugin
  zeroes the agent-written world-keyed inputs (remote slots, enemy presence, party
  presence, the player pointer). The agent rewrites them within a tick of recovering;
  until then, empty is the truth. Verified: the same transition now logs
  `agent reports 0 remote peer(s)` 19 ms after the epoch change, and no phantom spawns
  until the agent re-reports.
* The census (`KotorGameBridge.ReadEnemies`) also excludes creatures by **tag**
  (`coop_phantom`) — the id table clears before the body finishes dying, but the tag
  survives to the last readable moment, and no body the co-op blueprint spawned is ever
  this instance's enemy to publish.

## 3. The census label could name the wrong world — FIXED, unit-tested

`CoopAgent.PumpOnce` retains the whole last-good `PlayerState` across a failed read —
right for positions, catastrophic for the module label: for a few seconds after arriving
the host published a **fresh Taris census labelled END_M01AA**, because the census
enumerates the live world while `local.Module` was the retained value. That label passed
the module gate of a client genuinely standing on the Endar Spire, which then drove Taris
ids onto its own creatures (four collided; the rest were materialised as stand-ins — enemies
from a world the client was not in).

Fix: `IGameBridge.TryReadCurrentModule` — a live, never-retained engine read — and
`PumpEnemies` now reads the label **in the same breath** as the census and skips the pass
when the label is unreadable, a fallback (`areaXX`), or disagrees with the retained state.
Everything gated is a level, so the next clean tick republishes it. The encounter-set
publish deliberately stays ungated (it must keep superseding with an empty set on a
fallback name). Covered by `EncounterPumpTests.ASkewedCensusLabelSuppressesTheOwnersEnemyPublish`
(+ control), and `SessionIntegrationTests.ModuleSurvivesTheWireWhenPlayersAreApart` pins
the wire itself.

## 4. The agent's player scan latched a FREED COPY of the PC — FIXED (block v38)

After a transition the agent re-finds the player by scanning for creature vtables. A freed
copy of the player object keeps its vtable, its stats pointer and its `is_pc` flag — so
`FirstOrDefault(c => c.IsPlayer)` can pick the corpse of the old allocation and every
cheap validation (`StillLooksLikeThePlayer`) keeps passing forever, while the copy's
position and health stay frozen at whatever it died with.

Measured: the client agent published the PC standing at its Taris spawn point for minutes
while the real character walked across the Lower City into a Vulkar ambush. The plugin's
own encounter watch (which resolves the player through the engine) reported every polygon
correctly — while the host's phantom, driven from the frozen published position, never
moved, so the engine declined the fire 33 times against a body standing somewhere else.
`where` disagreeing with the encounter watch is the diagnostic signature.

Fix: the plugin now republishes the engine's own `CServerExoApp::GetPlayerCreature()` and
its object id into the block every frame (`SharedBlock.OffsetEnginePlayerPointer`, 0x2260,
block **version 38**; zeroed while no world is up), and the agent adopts it whenever it
names a different creature than the scan chose. The scan remains the fallback when the
plugin is absent (external mode).

VERIFIED live on the retry: the phantom ran with its player (gait 6.2 u/s across the
Lower City) and the same walk that was declined 33 times fired three ambushes — see
`client-encounters.md` §7. ⚠ Half-bump warning honoured the hard way: the version is part
of the agent's block SIGNATURE (`KotorGameBridge.PluginSignature`); bumping
`_block->Version` without it makes every agent silently fail to find the plugin, which
presents as "no phantoms, no follow, nothing" with both sides claiming `memory ok`.

## 5. Object-type equality rejected the materialised bodies — FIXED, verified

With the fires finally happening, the client's mirror destroyed every stand-in it had just
built: `EnemyPresenceController.Resolve` validated bodies with
`*(ushort*)(creature + 0x08) == 5`, and the object-type word carries FLAG BITS on live
creatures (`0x40005` measured long ago — the trap was already documented and fixed in
CreatureFinder, but this copy predated that). Every drive pass rejected the stand-in the
resolver had correctly returned, destroyed it, and re-materialised — one burned object id
per half-second per enemy (0xB7..0xC4+ in ten seconds, measured). Identity is now the
creature VTABLE here too. Verified: the next run materialised nine Vulkars with zero
give-ups and zero destroys.

## Where it ended: Taris and Tatooine, live

With all five fixed, the same session then chained a second cross-planet warp
(tar_m03aa → tat_m20aa, the Sand People territory) and ran a real battle there:

* Object ids, positions and health identical across both instances for the module's
  placed creatures (chief `0x9` at 120 hp, the same coordinates to the centimetre).
* The census ran at exactly its 32-slot cap and the client mirrored **32 of 32** —
  the first live exercise of the cap in a crowded module.
* A staged Sand People fight engaged BOTH bodies: the host's engine fought for real
  (worn from 12 to 1 hp) while three different warriors landed hits on the client's
  phantom, each forwarded and applied through the client's own pipeline
  (`took 2/3/5 damage from a hit on our phantom`, named attackers).
* One companion mirrored throughout.

⚠ One warp artifact to know about: `SetMinOneHP` from the Endar Spire command deck
travels WITH the character — the area script only releases it in the starboard section,
so a character warped out early keeps the pin on every planet, and nobody can be downed
until it is released. That is the engine honouring its save data, not a co-op defect,
but a test save built by warping off the command deck will silently block every
death-path test.

## The general rule these four instantiate

A module transition destroys every object and reassigns every id, but nothing pauses the
machinery: the plugin keeps running frames, the agent keeps pumping, the wire keeps
delivering. Any value that crosses the transition — a pointer in the block, a retained
state, a cached census row, a scan result — is a claim about a world that no longer
exists. The safe patterns, in the order they were paid for:

1. Pointers the plugin dereferences are resolved fresh from the engine, every frame.
2. Agent-written block inputs are cleared BY THE PLUGIN at the epoch edge, because the
   agent cannot do it at exactly the moment it matters.
3. Labels travel in the same breath as the data they label, from the same read.
4. Identity comes from the engine, not from a scan, whenever an in-process call exists.
