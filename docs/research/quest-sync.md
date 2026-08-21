# Quest sync: non-monotonic globals, journal removal, and the Lamport-clock fix (2026-08-21)

Roadmap ask: "ensure quests are properly synced, journals, etc." Story globals
(`EventFlagStore`), the journal (`JournalStore`/`JournalController`), plot XP and
dialogue-granted items were all already replicated in some form before this pass. What was
missing is the subject of this document: every one of those channels assumed KOTOR's own
world state only ever goes UP, and it does not.

Tag key, same as the rest of this research trail: **VERIFIED** (read from the binary, or
measured against the real game files) · **INFERRED** (reasoned, not observed) · **NEEDS-LIVE**
(must be confirmed on the running game before shipping — the rig was in use for this whole
pass, so nothing here has been run).

## 1. The bug this pass fixes, in one sentence

`EventFlagStore.Merge` and `JournalStore.Merge` refused any value that was not strictly
higher than what the store already held. That is correct for a `*_DONE` latch or a stage
counter that only advances, and wrong for a kill counter that ticks down, a boolean a script
clears back to `FALSE`, a quest stage a script deliberately pushes back, or
`RemoveJournalQuestEntry` — all of which are real NWScript operations KOTOR's own module
scripts use (§2). Two players whose engines legitimately diverge in that direction never
converged; they diverged **forever**, silently.

## 2. What actually occurs in the real game data

### 2.1 Method

`tools/kotorfiles.py` reads the GOG install's RIM/MOD archives directly (`Game.module(name)`
iterates every archive that makes up a module); `tools/q.py` queries the same symbol database
(`tools/kotor1_0_3.db`) `docs/research/dialogue-consequences.md` used; `tools/xrefs.py` +
`tools/disasm.py` locate where a routine's address is stored in the VM command table, exactly
`dialogue-consequences.md`'s method (§1.1 there). This pass extended that method from `.dlg`
node scripts only to **every `.ncs` in every module archive** (117 modules, 10,863 distinct
compiled scripts, ~30 MB), because `RemoveJournalQuestEntry` and a counter decrement are
generic script operations — triggers, heartbeats, on-death handlers — not just dialogue reward
nodes.

**VERIFIED** routine numbers (table-offset / 4, self-validating against
`dialogue-consequences.md`'s independently-computed `AddPartyMember = 574` at table offset
`0x8F8`: `SetGlobalNumber`'s own table offset `0x90C` is `0x8F8 + 0x14`, i.e. five slots after
`AddPartyMember`, landing at 579 — consistent, not coincidental):

| Function | Address | Table offset | Routine # |
| --- | --- | --- | --- |
| `SetGlobalNumber` | `0x00542B60` | `+0x90C` | 579 |
| `AddJournalQuestEntry` | `0x005483F0` | `+0x5BC` | 367 |
| `RemoveJournalQuestEntry` | `0x0054AF60` | `+0x5C0` | 368 |

`AddJournalQuestEntry` (367) and `RemoveJournalQuestEntry` (368) landing on consecutive table
slots is exactly what a compiler emitting `nwscript.nss` routines in declaration order
produces — the same self-check `dialogue-consequences.md` used for `CreateObject = 243`.

**Not found**: `SetGlobalBoolean`'s own VM-wrapper address is not present in `kotor1_0_3.db` —
the DB has `CSWGlobalVariableTable::SetValueBoolean` (the underlying setter, `0x00529570`) but
not `CSWVirtualMachineCommands::ExecuteCommandSetGlobalBoolean` (the routine table entry a
`.ncs` ACTION opcode actually names). Nothing nearby in the address range that brackets it
(`0x00542A50`..`0x00542C60`, `SetGlobalLocation`/`SetGlobalNumber`/`SetGlobalString`) is
labelled either — a DB gap, not a wrong answer. **Could not be counted this pass.**

The scan itself is a **heuristic byte scan**, exactly like `dialogue-consequences.md`'s (5-byte
`ACTION` opcode `0x05 0x00 <u16 be routine> <u8 argc>`): it counts a routine NUMBER appearing
in the bytecode, not a decompiled call with resolved arguments. **It cannot tell a decrement
from an increment, or a plain `AddJournalQuestEntry` from one passing `bOverrideHigher=TRUE`**
— NWScript compiles both to the identical ACTION opcode; the argument values are pushed
separately and this scan does not track data flow. Treat every count below as "this operation
occurs, this often, in this many scripts" — not as a decrement/override count.

### 2.2 Results (VERIFIED against the real game files, heuristic scan)

| Routine | ACTION hits | Distinct scripts | Comment |
| --- | --- | --- | --- |
| `SetGlobalNumber` (579) | 1,457 | 1,115 | Enormous surface. Direction not recoverable from bytecode alone (§2.1) — this is "how often a script touches a number global at all," and the game's own kill-counter and stage-counter conventions mean a real fraction of these are decrements, not that all or none are. |
| `AddJournalQuestEntry` (367) | 234 | 100 | For context; already merged (`JournalStore`), and `bOverrideHigher=TRUE` usage (a deliberate push-back) is included in this count but not separable from a plain advance. |
| `RemoveJournalQuestEntry` (368) | **102** | **12** | **Not a hypothetical.** Confirmed present, real, and used by real quest content across 8 different modules. |

`SetGlobalBoolean(..., FALSE)`: not counted (§2.1). Qualitatively, `SetGlobalBoolean` is one of
the most common NWScript calls in any KOTOR module (every `*_DONE`/`*_DEAD` latch reads it),
and scripts that clear a flag back to `FALSE` — a re-triggerable trap, a state machine that
resets, a companion approval swing that can go either way — are a known, ordinary pattern in
BioWare's own K1 content; this pass simply could not isolate the routine number to count sites
mechanically.

The 12 `RemoveJournalQuestEntry`-bearing scripts, by module (**VERIFIED**, script names only —
the tag(s) each removes were not decompiled, see §6):

| Module | Script |
| --- | --- |
| `danm13` (Dantooine) | `k_pdan_temp98` |
| `danm13` (Dantooine) | `k_pdan_trig1303` |
| `ebo_m12aa` (Ebon Hawk) | `k_pebo_lurarka17` |
| `ebo_m12aa` (Ebon Hawk) | `k_pebo_sasha01` |
| `ebo_m12aa` (Ebon Hawk) | `k_pebo_zalmove` |
| `ebo_m41aa` (Ebon Hawk) | `k_pebn_remove` |
| `korr_m39aa` (Korriban) | `k_pkor_cleanup` |
| `lev_m40aa` (Leviathan) | `k_plev_journals` |
| `manm26aa` (Manaan) | `k_pman_trialend` |
| `manm26ae` (Manaan) | `k_pman_hulas99` |
| `tar_m02ad` (Taris) | `k_ptar_sithjourn` |
| `tar_m08aa` (Taris) | `k_ptar_tarisover` |

`k_pdan_trig1303` (a Dantooine **trigger** script — fires by walking into an area, not by
finishing a conversation) is the concrete example the live-test script in §7 uses: it is
early/mid-game and reproducible by walking into the right spot, unlike the Leviathan/endgame
entries in this list.

### 2.3 Net read

Non-monotonic quest state is not an edge case this fix hardens against speculatively — it is
confirmed, real content: 12 scripts across 8 modules explicitly remove journal entries, and the
global-number surface (1,457 call sites) is wide enough that decrements are certainly present
even though this pass could not isolate them mechanically. The old max-merge policy was wrong
for a real, non-trivial slice of the game.

## 3. A second bug found while reading the old code: the apply-back loop fights the engine

`CoopAgent.PumpWorldState` (and its door/placeable siblings) used to do this every tick:

```
foreach change in bridge.CollectChangedWorldState():
    if session.Flags.Merge(change.Key, change.Value):   // refuses a decrease
        session.SetFlag(change.Key, change.Value)

foreach flag in session.Flags.Snapshot():                // the OLD (higher) value
    bridge.TryApplyWorldState(flag.Key, flag.Value)       // ...gets written right back
```

When the local engine genuinely lowered a value, `CollectChangedWorldState` reported it (its
own shadow tracks direction-neutral changes), but `session.Flags.Merge` silently refused to
adopt the lower value — so the SECOND loop, re-declaring the (still-stale, higher) session
value back to the engine on the very same tick, **immediately overwrote the player's own
engine write**. This was not "the two instances never agree" — it was "the coop layer fights
the local game every tick," which is strictly worse. Fixed as part of the same change (§4):
`SetFlag`/`SetJournalEntry` now always accept a genuine local change, so the apply-back loop
re-declares the correct value.

The equivalent bug existed one layer lower for the journal specifically: `JournalShadow`
(`KotorGameBridge`'s own local-vs-game shadow, independent of the session store) only ever
reported RISES from `Observe()` and only ever accepted RISES in `ShouldApply()` — so a genuine
local decrease never even reached the session, and a session-authorised decrease was refused
before it ever reached `Odyssey.SetJournalState`. Both are now direction-neutral (§5.2), and
`Observe()` now also detects a tag that DISAPPEARS from the live read as a removal — the old
code had no way to notice a `RemoveJournalQuestEntry` at all.

## 4. The new policy: per-key Lamport clock, not a bare max

Both `EventFlagStore` and `JournalStore` (`src/KotorCoop.Session/`) now store, per key,
`(Value, Serial, OriginPeerId)` instead of a bare value. `Version` is repurposed as the
store's own Lamport clock (still a plain `uint`, still the same tripwire field on the wire —
no new field needed for it):

- **Local change** (`Set`/`Merge`/`Remove` — this instance's own engine or player did this):
  mints `Serial = ++clock`. Always wins locally, in either direction — a fresh serial
  outranks anything this store has originated or observed.
- **Remote op** (`ApplyOp`/`ApplyRemote`/`MergeSnapshot`): adopted only when its
  `(Serial, OriginPeerId)` is strictly newer than what is already held — higher serial wins
  outright; an exact tie favours the LOWER peer id, i.e. the host (`CoopHost.HostPeerId == 1`
  always). Observing a serial always advances the local clock to at least that value, win or
  lose, so this instance's own next local change is guaranteed to outrank everything it has
  seen so far (the standard Lamport-clock causality argument).
- **Journal removal is a tombstone, not a deletion**: `JournalStore` keeps a removed tag's
  `(Serial, OriginPeerId)` around (excluded from `Snapshot()`/`Count`/the digest) so a stale
  re-add replayed later still has something to lose the comparison against — deleting the key
  outright would let a sufically-late stale replay resurrect a removed quest.

This keeps every safety property the old max-merge had:

- **Order-independent / idempotent**: the comparison is a total order on `(Serial, -OriginId)`,
  so applying a set of ops in any order (or twice) converges to the same result.
- **A stale replay can never regress a newer value**: proven directly by
  `EventFlagStoreTests.AStaleReplayWithAFreshSequenceNumberNeverRegressesANewerValue` and
  `JournalStoreTests.AStaleReplayNeverRegressesANewerValueEvenAfterARemoval` — a reconnecting
  peer replaying an old value under a BRAND NEW op sequence (the (origin, sequence) dedup set
  alone cannot catch this — it has never seen that sequence before) is still refused, because
  its serial is older.
- **Ties are deterministic**: `EventFlagStoreTests.ATieIsBrokenByTheLowerPeerIdIeTheHost` /
  `JournalStoreTests.ATieIsBrokenByTheLowerPeerIdIeTheHost` apply the same two ops in both
  orders on two independent stores and assert they converge on the same (host's) value.

...while finally allowing what the old policy could not: a genuine decrease, or a removal,
propagates and sticks (`ANonMonotonicRemoteDecreaseIsAcceptedWhenItIsGenuinelyNewer`,
`ARemoteRemovalIsAcceptedWhenItIsGenuinelyNewerAndPropagatesAsATombstone`).

`CoopAgent`'s door/globals/placeable pumps no longer gate the announce on "did this advance" —
`ICoopSession.SetFlag`/`SetJournalEntry` already no-op correctly on a value the store agrees
with (via the store's own `Set`/`Merge` returning false), so the gate was redundant for the
echo-suppression case and actively wrong for the decrease case (§3). It is simply removed.

## 5. Protocol and wire changes

**Version 30 is reserved** for a concurrent agent working the same integration branch (a
liveness/round-trip fix, not detailed here — see `ProtocolConstants.cs`'s own comment).

**Version 31** (this pass): `FlagOpPacket` and `JournalOpPacket` each gained a `Serial` field;
`JournalOpPacket` also gained a `Removed` bit. `FlagSnapshotPacket`/`JournalSnapshotPacket`'s
per-entry shape grew from a bare `(key, value)` pair to `FlagSnapshotEntry`/
`JournalSnapshotEntry` carrying `Serial`/`OriginPeerId` (and, for the journal, `Removed`) —
without this a resync could apply a stale entry over a genuinely newer local one, or silently
omit a removal instead of telling a peer "this tag is gone" (an omission is indistinguishable
from "this snapshot part did not need to mention it," which is exactly the trap
`disconnect-reconnect.md` §4.1 already documents for the version-alone tripwire). No new
`PacketType` values were needed — every change is to an EXISTING packet's field list, so no
packet type collides with the concurrent agent's reservation at 44, or with 45/46
(`DialogueGrant`/`DialogueGrantAck`, already shipped). `WorldSnapshotPacket` itself
(`FlagVersion`/`FlagDigest`/`JournalVersion`/`JournalDigest`) is unchanged — the tripwire
mechanism does not need to know about serials, only "did the content change" (§4.1 of
`disconnect-reconnect.md`, and the digest still depends only on VALUE per key — see §6).

### 5.1 What did NOT need to change: propagation latency

The task asked what the world-snapshot cadence is and whether an immediate event-driven send
is needed on top of it. **Measured** (`ProtocolConstants.DefaultTickRate = 30`,
`CoopHost.Tick()` runs on a `PeriodicTimer(1000/TickRate)` and unconditionally builds and sends
`WorldSnapshotPacket` every tick): **~33 ms**, not "a snapshot cadence" in the sense of a slow
periodic broadcast — it is the host's own game loop tick. And the snapshot is not the primary
path in the first place: `SetFlag`/`SetJournalEntry` already send a reliable `FlagOpPacket`/
`JournalOpPacket` IMMEDIATELY on the next pump when connected (`CoopClient.SetFlag`/
`SetJournalEntry`, unchanged by this pass), so a client's own change reaches the host in one
network round trip, not one tick interval. The snapshot's `FlagVersion`/`FlagDigest`/
`JournalVersion`/`JournalDigest` fields exist purely as the resync TRIPWIRE (catches drift from
a lost op or a third peer that never got a direct relay — `CoopHost.HandleFlagOp`/
`HandleJournalOp` apply a client's op to the host's own store and ACK the sender, but do not
relay it to OTHER clients; a third peer learns about it via the tripwire), and a client
recognising a version/digest mismatch requests a resync IMMEDIATELY in the same receive
handler, not on a timer. **Conclusion: no new immediate-send channel is needed.** Both the
primary path (op, on change) and the repair path (33 ms tripwire + one round trip) are already
well under the 1-second threshold the task named.

### 5.2 Bridge-layer fixes (no wire change, but load-bearing)

`JournalShadow` (`src/KotorCoop.Memory/JournalShadow.cs`) — the local-engine-vs-shadow layer
BELOW the session store — was upward-only in both directions (§3) and is now direction-neutral:
`Observe()` reports any value change AND any tag that disappears from the live read (a
`RemoveJournalQuestEntry` this instance's own engine just ran) as a `JournalChange` with
`Removed=true`; `ShouldApply`/`MarkApplied` accept a decrease; new `ShouldRemove`/`MarkRemoved`
mirror them for the removal path. `KotorGameBridge.CollectChangedJournal()` now returns
`List<JournalChange>` instead of a bare `(tag, state)` pair.

`KotorGameBridge` gained `TryRemoveJournalEntry(tag)`, using the SAME shared-block request slot
`TryApplyJournalEntry` already uses (one request in flight at a time), signalled through the
EXISTING `JournalState` int field via a sentinel (`int.MinValue` — a real quest state is never
negative). **No `SharedBlock` layout change** — that struct is owned by another agent's pass
per this task's own file-ownership list, so reusing the existing field rather than adding one
was the only option anyway, and turned out to be the simpler one.

## 6. Engine-write changes (Plugin project)

`Odyssey.SetJournalState` used to hardcode `bOverrideHigher=0`. It now WALKS the journal array
(which it already does, to find the matching tag) and decides the override bit itself by
comparing the found entry's current state against the target: current > target → genuine
decrease → override=1; current < target → genuine advance → override=0 (unchanged); current ==
target → no-op, exactly as before. The caller (`JournalController`) never has to say which
direction — the session's target value is simply asked for, and the engine call decides how to
get there. This reuses the ALREADY-VERIFIED `CSWSJournal::SetState` (0x005C5A40) and its
already-verified array-safety guards; only the third argument (a value already known to be 0
or 1 by the function's own contract) changed. **NEEDS-LIVE**: confirm `bOverrideHigher=1`
actually lowers the state and refreshes the GUI as expected (the disassembly's own notes on
`SetState` — see `Odyssey.cs`'s existing remarks — establish that the popup fires and the GUI
refreshes regardless of direction; a decrease has never been exercised).

`Odyssey.RemoveJournalEntry` is new and calls `CSWSJournal::Destroy` at `0x005C68B0` — **found
by static disassembly only, NOT verified live**. Method: `tools/xrefs.py`/`tools/disasm.py`
against `ExecuteCommandRemoveJournalQuestEntry` (0x0054AF60, the VM wrapper for routine 368,
§2.1) shows it walks the server's player list (`CServerExoApp::GetPlayerList`), fetches each
player's `CSWSCreature::GetGameObject` and `CSWSCreature::GetJournal` (0x004EDB00 — already
VERIFIED elsewhere in `Odyssey.cs`), and calls a function at 0x005C68B0 whose own `ret 8`
matches a one-argument thiscall (`param_size_bytes=4` in the symbol DB, function name
`CSWSJournal::Destroy`), immediately after pushing the address of a local `CExoString` tag
buffer — the same calling shape `SetState` uses for its own tag argument. This is
**self-consistent** (matches the naming, the argument count, and the call pattern) but the
project's own engine-write rule ("always verify a researched offset live before shipping the
write") and this pass's explicit "do not run the game" instruction mean it cannot ship enabled.
**It is wired in but OFF BY DEFAULT** behind `journal_remove_sync` (coop.ini), following the
exact precedent `docs/research/dialogue-consequences.md` §4 set for
`dialogue_recruit_sync` — a plausible, self-consistent offset that still needs one live pass
before it is trusted. With the flag off, `JournalController` still services the removal
request (acknowledges the shared-block slot so the agent's one-request queue never stalls) but
does not call the engine — the SESSION side (`JournalStore`) tracks the removal correctly
either way, so the only thing missing while this is off is the LOCAL game's own journal UI
still showing an entry a peer's no longer has.

**NEEDS-LIVE, unresolved, gating this feature**: same two open questions
`docs/research/dialogue-consequences.md` left for its own analogous case — (1) does
`CSWSJournal::Destroy` actually remove the array entry the way its name implies, or does K1's
journal instead mark an entry "ended" without deleting it (the task's own framing floated this
possibility); (2) does it crash on a tag that does not exist (this pass adds a defensive
pre-check — `found` must be true before the call — specifically to make that question moot,
but the check itself is unverified). Recruit a companion... no — for THIS feature, the live
test is: run a script that calls `RemoveJournalQuestEntry` on instance A with
`journal_remove_sync=1`, confirm the entry is genuinely gone from the in-game journal UI (not
just hidden), and confirm instance B (which never ran the script) also loses the entry once the
session propagates the removal.

## 7. Live-test script

Two instances (host + client), same save, `journal_remove_sync` left at its default (0) unless
specifically testing removal.

1. Launch both per `tools/demo.ps1`; confirm both are connected (`coop status` or the dashboard).
2. Talk to Trask on the Endar Spire on the HOST side; let the conversation run its
   plot-XP/journal beats. **Expected**: both instances' journals show the same entries at the
   same stage within ~1 tick + one round trip (§5.1) — watch for `journal: '<tag>' -> state N
   on the session's authority` in BOTH plugin logs, not just the host's.
3. Travel to Dantooine on either instance and reach the trigger behind `k_pdan_trig1303` (§2.2)
   — **NEEDS-LIVE**: this pass did not decompile which tag(s) that script removes or at what
   state; the exact trigger location and in-game consequence are unknown until someone reaches
   it live and reads the plugin log. What IS known: the script calls `RemoveJournalQuestEntry`
   (VERIFIED, §2.1), so SOME journal tag will disappear from that instance's journal when it
   fires.
4. On the instance that did NOT trigger it, confirm within a few seconds:
   - With `journal_remove_sync=0` (default): the session's OWN journal store no longer lists
     the tag (`session.Journal.Snapshot()` / the dashboard's journal view, if one exists) —
     this proves the SESSION-LEVEL propagation works — but the OTHER instance's in-game journal
     UI may still show the stale entry (expected, documented gap).
   - With `journal_remove_sync=1` on BOTH instances: the tag should disappear from the actual
     in-game journal UI on the instance that did not trigger the removal, and the plugin log on
     that instance should show `journal: '<tag>' removed on the session's authority
     (journal_remove_sync)`. **This is the NEEDS-LIVE item that decides whether §6's disassembly
     read is correct.**
5. For a concrete NON-MONOTONIC GLOBAL rather than a journal removal: no single confirmed
   decrement site was identified this pass (§2.1's limitation) — the closest available proxy is
   watching `SetGlobalNumber` traffic generally (`coop netsim` / the plugin's own logging) across
   a play session and confirming a value that goes DOWN on one instance reaches the other,
   rather than being silently dropped. `tools/globalvars.py stats`/`dump` against each
   instance's own save after a session can confirm post-hoc convergence even without catching
   the moment live.
6. Proof-of-fix line to grep both plugin logs for: any `journal:` line whose reported state is
   LOWER than a previously logged state for the same tag on the same instance — under the old
   code this could never appear (the apply was refused before it reached the log line); under
   the fix it is expected exactly when step 3's script (or an equivalent) fires.

`tools/verify.ps1` §5 (`journal apply`) is extended with two new checks in this pass (§5b/§5c
in the script): a same-slot follow-up request that asks for a state BELOW what was just
applied (proves the override path fires without needing real game content), and a removal
request with `journal_remove_sync` at its default off (proves the request is serviced —
acknowledged — without touching the engine, and logs the specific "gated off" line). Both are
poke-the-shared-block smoke tests on a single running instance, not a two-instance convergence
test — the cross-instance proof is §7's own script above.

## 8. Explicitly out of scope

- **Per-object local variables** (`SetLocalBoolean`/`SetLocalNumber`/`GetLocalBoolean`/
  `GetLocalNumber` — `CSWVarTable`, seen in the symbol DB at `0x0059B000`/`0x0059B040` during
  this pass's routine lookups but not otherwise investigated) are NOT covered by
  `EventFlagStore`/`JournalStore` and remain per-instance. This was already true before this
  pass and is unchanged by it — flagged here only because the task explicitly asked it be
  documented as out of scope.
- **String globals** — `KotorGameBridge`'s `TryApplyWorldState` already refuses to replicate
  location and string globals ("every one of them in a real save is a per-player scratchpad or
  cutscene staging value" — existing comment, unchanged by this pass).
- **`SetGlobalBoolean`'s exact call count** — the routine number was not in the symbol DB
  (§2.1); a future pass could find it by disassembling the VM command-table population routine
  directly (the same site `xrefs.py` located `SetGlobalNumber`/`Remove`/`AddJournalQuestEntry`
  at) rather than relying on the DB having a matching row.
- **Live verification of `Odyssey.RemoveJournalEntry`** — §6's whole open question. Ships
  disabled by default specifically because of this.
- **Doors/placeables' own doc comments** in `CoopAgent.cs` still describe their values as
  "usually monotonic" — this pass's fix benefits them incidentally (they ride the same
  `EventFlagStore`) but no equivalent module-content enumeration was done for door/placeable
  state specifically; §2's scan was scoped to the task's own named operations.
