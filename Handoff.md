# kotor-coop: handoff

Cold-start document. Read this and you should not need to re-derive anything.

Repo: `D:\Development\kotor-coop`
Goal: real multiplayer co-op for Star Wars: Knights of the Old Republic 1, retail binary,
no other dependencies. C# throughout.

---

## 0l. A CLIENT CAN SET OFF AN ENCOUNTER — AND THE GAP THAT FOUND, 2026-08-07

Built from a fresh RE pass, written up in full in **`docs/research/client-encounters.md`**.
**Nothing here has been run in the game**; §7 of that doc is the test list.

### The engine's answer to "a player walked in" is two instructions

`CSWSEncounter::EventHandler 0x00594220`, event 0xC, does exactly this:
`[encounter + 0x2E0] = whoEntered; SpawnIfAppropriate(whoEntered, -1.0f)`. Nothing else.
`SpawnIfAppropriate 0x00593BE0` decides and builds a spawn list; the creatures are made
later by `AIUpdate 0x00593FB0 -> SpawnCreatureFromSpawnList 0x00591CA0`, on the server's own
tick. So "fire this encounter for that player" is one reproducible call, not a forced spawn.

Event 0xC only ever comes from the engine's own movement functions
(`UpdateSubareasOnMoveTo/OnJumpPosition`, twelve call sites, all of them Walk/Drive/Combat/
Jump/AddToArea). A phantom is placed with direct `SetPosition` writes, which is in none of
them — **that** is the mechanical reason the host cannot just notice a remote player walking
in, and why this had to be an explicit request.

### What ships

Client plugin watches which activate areas contain the local player (the engine's own
`GetInActivateArea 0x00590090`, over the area's own subarea list at `CSWSArea + 0x1A0`);
the agent sends the SET every tick (`EncounterTrigger`, PacketType **32** — 30 and 31 were
already taken on main); the host resolves each id, finds that peer's phantom, and calls
`SpawnIfAppropriate` with it. One engine ever spawns, so ids stay identical and
`DisableLocalEncounters` is untouched. Block region `0x1DE0..0x1E60`; kill switch
`client_encounters=0`. `blockpoke encounters` reads both halves and can write a request by
hand. Verify-bytes on both engine entry points, refusing (and logging what it saw) rather
than calling into a binary that is not the one this was read from.

### `player_only` is the whole difficulty, and the phantom nearly passes on its own

Dumped every encounter `.ute` in the retail install: **47 placements, 34 with
`PlayerOnly = 1`**, including every Taris/Korriban/Star Forge fight. A phantom has
`is_pc = 0` and is not in the party table, so without handling this the feature would look
alive and do nothing. It IS in faction 0 (PLAYER) since 0l, so the *reputation* gate
(`GetAIStateReputation == 2`, hostile) already gives it the same answer as a real PC. The
flag is therefore suspended for the duration of the one call and restored in a `finally` —
much narrower than telling the engine a phantom is a PC.

Also measured offline: **`end_m01aa` has no encounters at all.** The standard demo save
cannot exercise any of this. `tar_m03aa` (4 × Black Vulkar ambush) is the cheapest module
that can.

### ⚠ THE GAP THIS FOUND: the mirror never materialises an enemy it does not have

`enemy-presence-sync.md` and addendum 0i both say "the host keeps its encounters; whatever
they spawn reaches the client through the enemy channel." **That is not true of the code,
and it was already not true before this change.** `CombatController.Apply` and
`EnemyPresenceController.DriveOne` both start with `ResolveServerObject(id)` and give up on
0 — they drive existing local copies and never create one. A creature that exists only in
the owner's instance is silently skipped.

So a host-triggered encounter is invisible to the client today, and with this change a
client-triggered one is invisible to the client too — while still being able to hurt them
through the phantom (0l). That is worse than nothing, so treat `client_encounters=0` as the
correct setting until the materialisation path exists. The design for it, with the cheap
key (`encounterId` + `creatureListIndex` against the mirror's identical copy of the same
encounter's `CreatureList`, one byte instead of a 16-byte resref), is in §8 of the research
doc. This is the next real piece of work in the enemy channel.

316 tests (22 new); solution, Agent and plugin NativeAOT all clean. **No protocol version
bump applied** — an added PacketType degrades safely (an older peer ignores it), but the
convention is to bump, so bump `ProtocolConstants.Version` when integrating.

## 0k. ⚠ THE CRASH PROBE WAS THE CRASH — and three players now have a harness, 2026-08-07

### The 0xC0000602 that killed the host, proven rather than guessed

Symptom: with a third peer in the session the host vanished a couple of seconds after
`agent reports 2 remote peer(s)`, with nothing in `kotorcoop-plugin.log` and only a Windows
Application log entry — `swkotor.exe`, faulting module `DINPUT8.dll`, exception `0xc0000602`,
fault offset `0x0007d2c7`.

**How it was pinned down.** The DLL's PE TimeDateStamp in the WER report (`0x6a7553cc`) is a
real build time — 13:41 local, which is commit `39fd515`'s working tree. Rebuilding the
plugin from that revision reproduces a byte-identical-size image, and dbghelp against its own
PDB resolves RVA `0x7d2c7` inside `RhpReversePInvokeAttachOrTrapThread2`. Disassembled, it is
the instruction immediately after this:

```
1007D2AB  cmp  dword ptr [esi+0x30], 0     ; Thread::m_pTransitionFrame
1007D2AF  jne  0x1007D2C7                  ; non-null -> thread is in NATIVE mode, fine
1007D2B1  push 0x100E6728                  ; -> "\nFatal error. Invalid Program: attempted to
1007D2B6  call 0x100818B0                  ;     call a UnmanagedCallersOnly method from
1007D2BB  push 1                           ;     managed code.\n"
1007D2BD  push 0
1007D2BF  push 0
1007D2C1  call dword ptr [0x100B5110]      ; KERNEL32!RaiseFailFastException
1007D2C7  <- the reported fault offset, i.e. that call's return address
```

`FAIL_FAST_GENERATE_EXCEPTION_ADDRESS` (the `push 1`) makes Windows report the return address,
which is why the offset is bit-exact.

**So the fault is: a reverse P/Invoke entered on a thread that was already in cooperative
mode.** The plugin has sixteen `[UnmanagedCallersOnly]` entry points and fifteen of them are
reached only from engine code, which is always in native mode because the plugin only enters
the engine through `delegate* unmanaged` calls and there is no `SuppressGCTransition`
anywhere. The sixteenth was `CrashProbe.OnException` — a **vectored exception handler**, which
Windows calls on the faulting thread. When the fault happens inside the plugin's own managed
code that thread IS in cooperative mode, so the handler could not be entered at all.

Three consequences, all of which match the record:

- Any hardware fault in plugin managed code was an instant, silent process kill.
- ⚠ **That includes every ordinary `NullReferenceException`.** NativeAOT implements null
  checks as faults on the null page and converts them back in its OWN vectored handler — but
  ours was registered later with `first=TRUE`, which puts it at the FRONT of the chain, so it
  ran first and fail-fasted before the conversion. **Every `try`/`catch` in the plugin was
  dead code against that fault class**, including the ones in the hooks.
- The probe that existed to describe crashes was the reason they were undescribable. The
  crashes it DID log were all at `swkotor.exe` addresses — engine code, native mode — which
  is exactly the case it was written for and the reason nobody noticed.

This also retires the unexplained note in 0n ("a client instance vanished right after
connecting, with no crash-probe report").

**The fix.** `CrashProbe`'s handler is now hand-emitted x86 machine code: it copies the
exception code, address, EIP, ESP and a TEB-bounded 256-byte slice of the faulting stack into
a fixed buffer, bumps a serial, and returns `EXCEPTION_CONTINUE_SEARCH`. No managed frame, no
transition, nothing that can fail fast. The worker thread turns the buffer into a log line.
The emitted bytes are re-derived and disassembled offline by
`python tools\checkstub.py`; every branch is a forward `rel8` and the whole
stub decodes cleanly.

Alongside it: `AppDomain.CurrentDomain.UnhandledException` now logs any escaping managed
exception with its type, message and stack (`StackTraceSupport` turned ON for this reason —
+164 KB in a DLL that loads once), and startup writes one line saying that a fail-fast leaves
no trace here and to run `pwsh tools\crashlog.ps1`, which reads the Application log, explains
the exception code, and can enable WER local dumps.

⚠ NEEDS-LIVE: the stub, the unhandled-exception hook and the log line have never run inside
the game. What triggered the *underlying* fault is still unproven — `Odyssey.DrawMinimapPeers`
is the prime suspect (new, per-peer, on the render path, and the crash arrived with the second
peer), and it has been hardened, but the honest position is that the next occurrence will now
name itself.

### The phantom-id table overlapped three other fields

`OffsetPhantomIds = 0x264` was documented as "8 x uint32". It never was: `0x268` is
`OffsetKeepAliveUnfocused`, `0x26C` is `OffsetPausesCleared`, `0x280` is
`OffsetPickUpRequest`. With one remote player only index 0 is written, so it could not show.
The SECOND phantom — the third player — writes its object id over the agent's keep-alive flag,
the focus keeper then writes its pause counter into what the agent reads back as "phantom 2",
and `ReadPhantomObjectIds` strikes creatures with those small ids out of the enemy census, the
set drifting as the counter climbs. The real table now lives at `OffsetPhantomIdTable`
(`0x1DE0`); `0x264` stays as the slot-0 mirror so nothing that has not been rebuilt breaks.

### Slots belong to peers now, not to list positions

Everything the plugin derives from a shared-block slot is keyed by slot index and nothing
else — body, name, gear, faction, cumulative damage. The agent packed peers into slots in
list order, so one player leaving renumbered the rest and the survivors inherited the leaver's
body. `PeerSlotMap` makes a slot sticky per peer id (reclaimed only under pressure, and only
from a peer that is genuinely absent); `RemoteCount` is now the occupied RANGE rather than a
population; `PhantomController` spawns, drives and despawns per slot instead of assuming a
contiguous prefix.

And `CoopAgent` no longer attributes phantom damage by list position — it asks
`bridge.PeerInSlot(slot)`. The list it wrote was filtered by module first, so with three
players the wrong player was billed, sometimes one standing in another area.

### Damage from two attackers is added, not maxed

`_phantomDamageIn` was a single running total kept with `if (incoming > stored)`. Per-sender
totals do not compare like that: once the host had dealt 40, a second player's reports of 5,
10, 15 were all smaller and were discarded forever. Now keyed by source peer and summed; the
host keys its outbound table by `(target, source)` and relays with the ORIGINATOR's id so the
receiver can tell two attackers apart.

### One agent per game, enforced

⚠ Two things start an agent — the plugin's `AgentLauncher` (`coop.ini auto_start_agent`, ON by
default) and whatever the human ran — and nothing stopped both attaching to the same
`swkotor.exe`, both writing the same block, and both joining as separate players. That is how
a stray third peer appeared in a two-instance session. The agent now takes
`Local\kotorcoop-agent-<pid>` and a second one exits 3 with a message; `tools\demo3.ps1` also
writes `auto_start_agent = 0` into each install so the harness owns its agents.

### The harness

`tools\demo3.ps1` brings up three instances (`--instance 3` needs nothing new — the mutex
rename is `kcoopN` with N clamped 1..9; what it needs is a third *directory*, which
`-CreateGame3` robocopies from instance 2's), and `tools\verify3.ps1` is a 24-check battery of
things two players could not fail.

---

## 0j. PEERS ON THE MINIMAP, AND COMBAT WHEN THE PLAYERS ARE APART, 2026-08-07

### The minimap shows the other players after all — in their own colours

⚠ **This overturns the WON'T FIX in `docs/research/animation-and-minimap.md` §B.** The
*findings* there stand: `DrawMap 0x0068AB10` was read end to end, it draws the area image, the
fog and exactly ONE arrow, and the engine's three `CSWSAreaMap` party slots cannot be borrowed.
What was wrong was the conclusion — that a peer needed its own GUI control, its own viewport
and ~120 lines on the render path.

**The arrow is already there, already the right size, and already inside an open viewport.
Drawing it again at an offset is the whole feature.** `MinimapPeerHook` redirects the five-byte
`call AurGUICloseViewport` at `0x0068ACB4` to a stub that draws the peers and tail-jumps to the
real function — no prologue to relocate, and the site is only reached when the viewport really
was opened. Full write-up: `docs/research/minimap-peers.md`.

Three things worth carrying forward:

- **The offset is RELATIVE to the arrow**, because `DrawMap` scrolls the image to
  `(side/2) - pixel*scale`. So this code never needs to know where the minimap is on screen.
- **World→pixel is borrowed, not reimplemented**: write the peer's position into party slot 2
  via `SetPartyMemberWorldLocation 0x005790C0`, read the pixel back, restore the slot. ⚠ That
  setter only STORES on success, so the slot must be cleared to `-1` first or an off-map peer
  silently reuses the previous peer's pixel.
- **Colour is a MULTIPLIER against blue art**, at control `+0x80..+0x88` (the embedded
  `CSWGuiImageParams.color`; angle `+0x78`, alpha `+0x7C`). Values above 1.0 saturate a channel
  instead of clamping the result, which is the only way to get red out of blue. Every palette
  entry was captured and looked at — `(1, 0.15, 0.15)` is unreadable maroon, `(6, 0, 0)` is
  red. Six shipped colours, one per peer slot.

The local arrow is drawn once more at the end so a partner standing on you cannot hide you.
`coop.ini minimap_peers=0` opts out.

### ⚠ MEASURED 2026-08-07: the engine DOES start a lone player's fight

The whole "apart" review rested on an untested assumption — that the host's engine runs its
enemies against a distant partner's phantom. `verify.ps1` never tested it: it stages fights
with `ActionAttack`, which bypasses perception. It does now, and the answer is yes.

Client alone beside two Sith Soldiers at `(30.5, 139)`, host's PC at `(20, 22)` — **120 units
away**. Both soldiers walked to the phantom and fought it: 25 hits, 50 damage, positions
identical on both instances to two decimals, forwarded damage reaching the client's real
character (`owed 46 applied 46`). There is no distance culling to defeat —
`CSWSArea::IncrementPlayersInArea` raises AI priority for the AREA, not by proximity to a
player, and a phantom is an ordinary creature to everything below that.

**But the lone fight stalled anyway, on a tutorial box.** "The game pauses when you sight an
enemy" sat on screen for SIX MINUTES while two soldiers beat on the character; it sheathed its
weapon and every queued action was ignored. The auto-pause option bits were already cleared so
the world clock ran, but a modal panel owns the action queue and needs a click a player being
hit cannot give. `CClientOptions + 0x14` bit `0x02` gates every tutorial window — confirmed
twice, by `SetTutorialWindowsEnabled 0x005472F0` toggling exactly that bit and by
`CGuiInGame::TutorialReasonWillShowWindow 0x0062F420` opening with `test byte [eax+0x14], 2`.
Cleared alongside the auto-pause options. Verified by screenshot: same room, no box, weapon
drawn, mid-swing.

⚠ **And one thing found but NOT fixed: a player fighting alone cannot land the killing blow.**
`TryApplyEnemyFloors` floors at `max(1, reported)` and discards reports marked Dead, so a
client wears an enemy to 1 hp and nothing dies — with the host elsewhere, the room never
clears. Both rules were deliberate (death must run the engine's own path; one peer's bad read
must not delete things). The fix needs the host to run `Odyssey.KillCreature` — which already
exists and is what a client runs for the host's kills — behind a new host-side agent→plugin
channel, gated on the host having already floored that creature to 1. It changes who may kill,
which is the centre of the authority model, so it is written up in
`docs/research/combat-when-apart.md` §6 rather than rushed.

### 2026-08-07 (latest): A LONE PLAYER CAN NOW WIN A FIGHT — verified live

```
client alone vs a Sith Soldier, host's PC 120 units away
start     host hp 15   client hp 15
round 1   host hp  6   client hp  2
round 2   host hp  1   client hp  1
round 4   host hp gone client hp gone     <- and: "combat: killed 0xE on the owner's authority"
```

The same test previously stalled at **11 hp for a whole ninety-second fight** with the screen
showing a genuine, swinging, combat-mode battle. Four things were in the way, each hiding the
next — full write-up in `docs/research/combat-when-apart.md` §6a:

1. **The mirror RAISED the peer's copy back up.** `CombatController` wrote the owner's health
   whichever way it differed, eight times a second, undoing every local hit ~125 ms before the
   once-a-second census could see it. Now **lower-only**: the owner may drag a mirrored enemy
   down (its damage is authoritative) and never back up, because the only reason our copy is
   lower is that we hurt it — which is exactly what the min-merge wants to hear. Owner-side
   healing no longer propagates; a fair trade for damage that does.
2. **The report was a sample, not a high-water mark.** `CoopAgent.LatchLowestSeen` remembers
   the lowest health and any death seen this module, so a dip cannot be missed.
3. **Min1HP meant a peer's copy could never die, so it could never REPORT a death** — and the
   pin exists so the owner delivers the killing blow, which it never will when the players are
   apart. Released once the owner's own value reaches 1.
4. **A dead creature leaves the census entirely**, so no row is left to carry the flag. A
   creature that vanishes having last been seen AT THE FLOOR is reported dead — safe because
   only the owner's own merge can put something at 1.

⚠ The §6b "report damage dealt, not health observed" redesign is NOT needed and was not built:
making the mirror lower-only removes the race at its source. Keep it in mind only if
per-hit attribution (who dealt what) is ever required, which health alone cannot express.

22/22 on `toolserify.ps1` afterwards, 292 unit tests.

### 2026-08-07 (later): pause scoped to the vicinity, and the kill channel

Two changes toward "the default engine rules still apply, per player":

- **A pause no longer freezes a partner across the module.** `PumpPause` now only mirrors a
  peer's pause when they are within `AgentSettings.PauseShareRangeUnits` (default 30, and 0
  restores the old share-everything behaviour). A pause is for the situation you are BOTH in;
  beyond that range you are not interacting and there is nothing to keep in step. Either of
  you can still release it — `PauseController` already honours a local unpause and does not
  re-assert.
- **The owner can now execute a kill a PEER earned** (block version 22, kill-request channel
  at `0x1DB0`). `CombatController.ExecutePeerKills` runs `Odyssey.KillCreature` — the engine's
  own lethal damage, so corpse, loot and OnDeath are real — gated on our own copy being ALIVE
  and already at the floor of 1, which only happens because that peer fought it there.

⚠ **BUT the kill channel cannot be live-proven yet, and the reason is a bigger bug.** Measured:
a client alone against soldier `0xE`, host 120 units away, attacking for 90 s — the enemy went
**15 → 11 and then stopped for the rest of the fight**, never reaching the floor at all. The
client's copy briefly read 5 and the mirror put it straight back to 11.

**A peer's damage is SAMPLED, not accumulated.** Both halves of the merge work on absolute
health about once a second: the owner writes its value onto the peer's copy, the peer's census
reads whatever its copy says, the owner takes the minimum. Every hit races the next mirror
write, and the ones that lose never existed. Absolute health is right for replicating the
owner's truth; it is wrong for carrying a peer's contribution back.

The fix is to report damage DEALT rather than health OBSERVED — `PhantomHitHook` already does
exactly this shape for damage landing on a phantom. Written up in
`docs/research/combat-when-apart.md` §6b. Until it lands, a player fighting away from the
enemy owner contributes only the damage that survives the race.

⚠ Also seen, unrelated to the above and worth knowing: **a third peer crashed the host** with
a .NET fail-fast (`0xc0000602` in DINPUT8, which bypasses the crash probe — check the Windows
Application log, not just the plugin log). It happened seconds after `agent reports 1 remote
peer(s)` became `2`. `demo.ps1` starts an agent AND the plugin auto-starts one, so a stale
agent can join as a third player; the whole project is only ever tested with two. Kill every
`KotorCoop.Agent` before a run.

### Combat with the players apart — a review, two fixes, and a crash

Full write-up: `docs/research/combat-when-apart.md`. **The short version: it works, and the
reason is the phantom** — the distant player's body is really there in the host's game, in the
player faction, so the host's enemies fight it and that fight is the authoritative one.
Proximity to the HOST is irrelevant; what matters is that the phantom is where the player is.

Three defects, all invisible while the players stand together:

1. **⚠ THE FOLLOW FIRED OFF A PLACEHOLDER AND KILLED THE CLIENT.** `PumpTravel` rejected the
   bridge's `areaXX` fallback for the TARGET module but not for the LOCAL one — and that
   fallback means exactly one thing: our own transition is in progress. So mid-load it compared
   a placeholder against the host's real module, concluded we were separated, and fired a
   second transition into the middle of the first:

   ```
   07:58:01.186  travel: moving to module 'end_m01ab'
   07:58:02.285  travel: moving to module 'END_M01AA'      (1.1 s later)
   07:58:02.473  CRASH at CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D
   ```

   That instruction is `mov ebp,[eax+0x48]` on the result of `CClientExoApp::GetModule()`,
   dereferenced with **no null check**, while the previous world's creature-add messages are
   still being serviced. Reproduced twice, fixed, and the same test now produces travels
   **10.3 s apart with no crash**. A post-arrival settle window (`AgentSettings.ModuleSettleMs`,
   5 s) backs it up. This is ordinary play: a client who walks through their own door arrives
   where the host is not and is brought straight back.

2. **The census was measured from the publisher only.** `ReadEnemies` caps at 32 and sheds by
   distance, so the creatures around a distant partner sorted last and fell off — at exactly
   the moment that partner needed them. Now the key is distance to the NEAREST player; peers
   arrive already module-filtered.

3. **The enemy snapshot carried no module.** Object ids are per module load, so a snapshot
   taken elsewhere writes the host's healths and positions onto whatever local creatures share
   those numbers. `EnemySnapshotPacket` now carries its module (**protocol 14**) and a client
   drops it when it does not match. ⚠ And an empty list is now WRITTEN rather than skipped —
   otherwise the last snapshot stays in the shared block and the plugin keeps driving the old
   module's ids. Verified live: `client enemies 25 → 0 → 25` across a travel out and back.

**Method note, and it is the same one as §0i:** the first fix I wrote for the crash (a settle
window alone) was aimed at "the load had completed" — which I inferred from the module name
being readable. It was not; the name was the `areaXX` fallback. Re-running the test rather than
trusting the reasoning is what found the real cause.

⚠ **Two verify.ps1 failures that are NOT regressions**, both cost time and will recur: the
plugin logs are append-only ACROSS sessions, so `no crash on either side` reports a crash from
a previous run; and the phantom-damage check takes 3 hp off the client each time, so after a
few batteries the peer sits at 0 hp and `the peer really loses those hit points` fails
`0 -> 0`. Truncate the logs and restart before believing either.

---

## 0i. THE PLAYTEST BUG LIST IS CLOSED, 2026-08-06 (late)

`docs/playtest-bugs.md` is gone: every item on it is resolved, and what was worth keeping is
here. ⚠ **This also supersedes 0h below** — the in-process load has shown the world since
2026-08-01 (`docs/research/ingame-gui-transition.md`); read 0h as history, not as an open bug.

### The dialogue-close crash — FOUND AND FIXED

The last open item, and the interesting one. Closing a shared conversation could kill the
game about two seconds later:

```
[13:19:23.729] test: closed any running conversation
[13:19:25.675] CRASH: access violation, eip 0x6307FB   (CGuiInGame::UpdateShotCamera+0x11B)
```

**Root cause.** `Odyssey.CloseDialogue` called `CGuiInGame::CloseDialog 0x006332B0` — the
CLIENT half only. That sets both dialogue participants at `gui+0x170/+0x174` to
`OBJECT_INVALID` (0x006333FC) while the SERVER's `CSWSDialog` keeps running: it walks the
tree on its own line timers and, one line later, sends the next reply list. The client's
`HandleDialogReplies` → `ShowDialogReplies` → `UpdateShotCamera` then resolves both
participants and dereferences the results with **no null check**:

```
0x006307D8  call CClientExoApp::GetGameObject   ; gui+0x170
0x006307DD  mov  edx, [eax]                     ; <- no test
0x006307F6  call CClientExoApp::GetGameObject   ; gui+0x174
0x006307FB  mov  edx, [eax]                     ; <- the fault
```

The ~2 s delay is the dialogue line's own duration. Retail never reaches this because a
conversation only ever ends through `CSWSDialog::Cleanup 0x005A27B0`, which is *what calls*
`CloseDialog` (0x005A2841) — client and server go down together, so nothing is left to send.

**Fix, and the general lesson.** End the conversation the way the engine does: on the SERVER.
`CSWSObject::StopDialog 0x004CB4F0` on the dialog owner runs the end-conversation script,
clears the participants and destructs the `CSWSDialog`, whose destructor calls `Cleanup`,
which closes the client UI. The owner is found from the GUI's own participant ids: the one
whose `+0x54` (dialog owner) equals its own `+0x04` (object id). A non-owner participant
works too — it routes through `RemoveObjectFromDialog` to the owner. This is the same call
the engine makes at a natural end (`CSWSDialog::HandleReply 0x005A04AD`) and when combat
interrupts a conversation (`CSWSCreature::ActivityManager`).

⚠ **The lesson generalises past this bug: KOTOR's client and server halves are one process
but not one state machine. Driving only the half you can see leaves the other half running.**
That is the same shape as the travel crash in 0g (a client holding a world the server had
dropped) and the pause bug in 0.4 (a client mask disagreeing with the server's).

`ShotCameraGuardHook` is the net under it: a verify-bytes detour on `UpdateShotCamera` that
returns without running the body when either participant fails to resolve. All three callers
discard the return value, so `xor eax,eax; ret 0x18` is exactly the engine's own early-out at
0x00630F62. Its refusal count is reported from the frame hook (the detour itself must not log).

**Verified live**: 22 open/close rounds across two sessions plus two full `tools\verify.ps1`
batteries (22/22 each), the client following and closing every one, zero access violations,
both processes alive throughout.

### Animations: the last two defects, and the moving flourish

- **`twin + 0x48` is a USHORT** and `+0x4A` is a separate ushort (the queued animation).
  `KotorGameBridge.ReadPlayedAnimation` read 32 bits, so whenever anything was queued the
  published gait was `queued<<16 | played` — not an animation id at all. Now a 16-bit read.
  `Odyssey.GetTwinCurrentAnimation` had the same bug and is deleted.
- **`PhantomController.MirrorAnimation` queued any non-gait id as fire-and-forget.** A
  looping or overlay id down that path parks with no expiry and wedges the body. It now goes
  through `Odyssey.PlayOneShotAnimation`, which asks the engine's own predicates first.
  `Odyssey.PlayFireForget` (unguarded) is deleted.
- **The moving flourish is captured now.** X standing still is a fire-and-forget (0x27AE) and
  passes `InsertFireForgetAnimation` like everything else; X while MOVING takes the other arm
  of `PlayerFlourishWeapons` and pushes an OVERLAY (0x27AD) straight into the animbase.
  `AnimationHook` detours that arm at **0x005F4681** (`mov ebx,[esi]; push 1; push 0x27AD`,
  nine relocatable bytes, resume 0x005F468A) and feeds the same one-shot channel.

⚠ **A creature's animbase reaches its overlay setter through an ADJUSTOR THUNK.** Measured
live: the animbase at `twin+0x68` carries vtable **0x00754AE8**, whose `+0x44` is
`sub ecx,[ecx-4]; jmp 0x00699420` (the `CSWCAnimBaseHeadWield` override) — NOT
`CSWCAnimBase::SetOverlayAnimation_2 0x006A03C0`. That pointer is a secondary base, so
calling the base implementation directly hands it an unadjusted `this`. **Call the slot, not
the address.** The first version of `PlayOverlayAnimation` required the slot to equal
0x006A03C0 and therefore refused every real creature; it was a guard that logged why, which
is the only reason this took one run instead of a debugging session. `+0xE0`
(`ServerToClientAnimation 0x0069F650`) is shared by every creature-family animbase and
overridden by doors, placeables, triggers, projectiles and visual effects — so slot identity
there is the check that this is a creature's animbase at all.

**Verified live on a phantom**, all three classes through one channel:

```
overlay  0x27AD  ->  applied 6/6   ("played one-shot 0x27AD on phantom 0x12D")
oneshot  0x2806  ->  applied 3/3   (control)
looping  0x2712  ->  dropped 4/4   ("in no playable class") — this is the one that wedges
```

### Where the rest of the list landed

| Playtest item | Outcome |
| --- | --- |
| 1. World never shown after a plugin load | Fixed 2026-08-01; supersedes 0h |
| 2. Peer position lags by seconds | Fixed by direct (seamless) drive. **Measured**: peer walked 54.4 units, mean gap **0.20**, p90 0.55, max 2.93 units |
| 3. Initial position not synced | Fixed; phantom spawns at the peer's own position |
| 4. Animations do not replicate | Fixed — hook not poll, plus the three fixes above |
| 5. Enter opens chat on BOTH instances | Fixed: every key read goes through `NativeMethods.IsKeyDownHere`, which requires the foreground window to belong to this process. Code-verified, not live-verified (it needs focus juggling, the least reliable thing in this repo) |
| 6/7. Pause not shared / stuck paused | Fixed: `SetPlayerPause` writes both halves unconditionally, `PauseController` honours a local release, `ReconcilePause` repairs a stuck banner, `PauseSanityPatch` stops the engine reverting an unbacked pause. Verified by `verify.ps1` (holds both masks, releases with the peer) |
| 8. Minimap does not show both players | WON'T FIX — `DrawMap` has no note or party loop (0x0068AB10, whole function read). The M-screen pin works and is the supported answer |
| 9. Nobody can switch party character | Fixed. **Measured**: host `swap 0` (Tab and portraits free), client `swap 1` — the client's lock is by design, because the host's AI owns the companions |

---

## 0h. ⚠ THE IN-PROCESS LOAD NEVER SHOWS THE WORLD — SUPERSEDED BY 0i (fixed 2026-08-01)

**Observed by the user, confirmed by screenshot: after a plugin-driven load both instances
are still displaying the MAIN MENU** — the host lit, the client faded to near-black
mid-transition (which reads as "loading forever"). The simulation underneath is genuinely
correct: creatures exist, the saved character is right, phantoms spawn and equip on both
sides, the enemy channel replicates, game audio plays. **You simply never see any of it.**
The presentation never leaves the menu, and `FirePhase2` also calls `SetInputClass(client,
0, 1)` + `DisableInput(client)` and nothing ever re-enables them.

⚠ **Correct these claims in 0f**: "both players are in one world" and the click-to-join flow
being usable are WRONG as written. What was verified was the simulation and the network, not
the presentation. The click loader (`KOTORCOOP_PLUGIN_LOAD=0`) renders correctly — verified
by screenshot the same day, full in-game GUI, minimap, portraits — so **use the click route
for anything a human will look at** until this is fixed.

⚠ **Also correct the "injected input does not reach the game" conclusion** recorded in
`docs/live-verification.md` and 0f. That test was run against a plugin-loaded instance, which
was sitting at a menu with input explicitly disabled by our own load path. The conclusion is
unsafe; re-run it against a click-loaded world before believing it.

**Where to look.** The missing work is the client-side GUI transition the retail load performs
once the module is up: tear down / hide the main menu, dismiss the load screen, and bring up
the in-game GUI. `CGuiInGame::SetSWGuiStatus 0x62AA00` is almost certainly central — it is
what calls `CSWGuiMainInterface::InitializeMap`, the function whose null-module dereference
caused the travel crash in 0g, and that is the same in-game-GUI bring-up path. Related and
already bound in `MenuLoadController`: `HideSWInGameGui 0x0062CBA0`, `ShowLoadScreen
0x005EDA80`, `SetLoadStep 0x005EDFD0`, `SetInputClass 0x005EDA60`, `DisableInput 0x005EDBE0`.

**Method note, worth internalising:** this was visible in screenshots twice and I explained it
away as a stale-capture artefact because the memory state said the world was up. Memory state
proves the simulation; only a screenshot proves the presentation. When they disagree, believe
the screenshot.

---

## 0g. MODULE TRAVEL CRASHED — FOUND AND FIXED, 2026-08-01

**Fixed and verified**: the same travel that killed the process twice now completes, and the
world comes up in the destination module with the character intact
(`local module end_m01ab pos (50.02, 119.95) hp 12/0`).

**Cause** (`docs/research/travel-map-init-crash.md`). `CSWGuiMainInterface::InitializeMap`
runs every in-world frame, asks the SERVER for its module (`GetModule 0x4AE6B0`) and
dereferences `module+0x30` to build the minimap name — **with no null check**. A transition
destroys the server module in `UnloadModule 0x4B9240` and only builds the replacement on the
next SERVER MainLoop frame, but the outer loop runs the CLIENT MainLoop first — that gap is
the measured ~15 ms. Retail survives it only because the client tears its own world down
first, which it does when it processes the server's queued dump-player message; a
plugin-driven transition reaches the window before that message is serviced.

**Fix**: `Odyssey.CloseClientWorldIfServerModuleGone`, called at the top of
`PluginHost.OnFrame` before the epoch gate — when the server's module is gone but the client
still holds one, call `CClientExoApp::UnloadModule 0x5ED430`, byte-for-byte what the engine's
own dump-player handler does, just a frame earlier. Self-limiting: the engine's later call
early-outs and the client rebuilds from the next Module_Info.

Also settled by the same pass: **`TravelController` omits nothing** — the engine's own `warp`
(`Global::warp 0x60AF50`) is only `SetMoveToModuleString` + `SetMoveToModulePending(1)`,
which is exactly what it does. The `PanelExists`/`SetSWGuiStatus` frames in the original
stack were stale locals; the real caller is a tail-call thunk (`CGuiInGame::InitializeMap
0x62B0A0`) — worth remembering when reading this crash probe's stack dumps.

⚠ Still NEEDS-LIVE: whether retail shares this latent window (no netlayer pump was found
between the server unload and the next client MainLoop). The fix is correct either way; the
doc has a two-run probe that settles it.

**Two more bugs sat behind it, both only visible by running the thing:**
- **The module name carried a trailing junk byte.** A CExoString's length can include its
  terminating null (the same trap as the `g_a_clothes01\0` equipment resrefs). Invisible in a
  log, and harmless to the module FILTER — both peers carry the same junk and still compare
  equal — but fatal to TRAVEL: the client asked the engine for `end_m01ab\0`, no such module,
  and retried forever (`travel: moving to module 'end_m01ab?'`). `TryReadModuleName` now cuts
  at the first character a module name cannot contain.
- **The agent kept a stale player pointer across the transition.** A module change frees every
  creature and the new module allocates its own, so the cached pointer lands in reused
  memory: both agents reported `pos (0,0,0)` with hit points of `57/134` and `21544/21544` —
  plausible-looking garbage, which is the worst kind, because the phantom positions derived
  from it read as a network fault. The cached creature is revalidated each tick (creature
  vtable + stats still saying PC) and re-resolved when it stops matching.

**Result, verified live:** host warps, client follows, both arrive at
`end_m01ab pos (50.02, 119.95) hp 12/0` and respawn each other's phantom there.

### The original report, for context

A module transition requested through the travel channel killed the process ~15 ms later:

```
travel: moving to module 'end_m01ab' on the session's authority
epoch: world unloading; clearing phantom, item, combat and door state
CRASH: access violation at 0x405F73, eip 0x405F73
stack: 0x687E99 0x40BD7A 0x62AA6D 0x72F420 0x602FB8 0x7273A8 0x4046C2
```

`CResRef::CopyToString` (`0x405F70`) faulting on a null/garbage `this`, called from
`CSWGuiMainInterface::InitializeMap` (`0x687E40+0x59`) ← `CGuiInGame::SetSWGuiStatus`
(`0x62AA00+0x6D`) ← `CClientExoAppInternal::MainLoop`. So the CLIENT-side GUI is initialising
the new module's map and finds a resref it cannot read.

**Isolated already — do not re-do this:** it reproduces with **no co-op session, no peers, no
phantoms and no map markers** (single instance, agent killed before the travel request). It
is the transition itself on a world brought up by the in-process load replay, not the
replication code. Reproduce with
`pwsh tools\blockpoke.ps1 -ProcId <pid> travel end_m01ab` after a plugin load.

The suspicion at the time — that the load replay left client-side state uninitialised, the
same family as the two bugs 0f fixed — was right in shape: the client was holding a world it
had not been told to drop.

---

## 0f. THE WHOLE SESSION STARTS INSIDE THE GAME, 2026-08-01 (later)

**Verified live, end to end, with no external commands and no injected clicks in the load
path:** launch the game → the plugin starts the agent itself → **Co-op** on the main menu →
a native panel → one player clicks **Host** (its save loads, it advertises on the LAN), the
other player's panel lists it and they click **Connect** (their save loads, they join) →
both spawn each other's phantom with equipment and a map pin. Both characters are the real
saved ones.

Three things landed to get there, on top of section 0e:

- **The in-process load finally loads the SAVED character** (`docs/research/character-login-chain.md`).
  `LoadPCAutoSave` leaves `DoingStartNewModule` set, and `RunModule` forks the character
  login on that flag: set, it takes the new-game flavour, which reads the PC from
  `TEMP:pifo` — **a file a manual save does not contain**. `LoadCharacterFromIFO` fails
  silently (its caller never checks the return), login is confirmed anyway, and
  `InitiateModuleForPlayer` manufactures a blank creature. That same path never calls
  `PostProcess`, which is where the missing faction in 0e came from. Clearing the flag during
  the load window (`MenuLoadController`, the `_loginRerouted` block) reroutes onto
  `LoadPrimaryPlayer`, which reads the PC from the save's own `module.ifo`. Measured:
  `pos (20.01, 22.15) hp 12/0` — identical to a click load; the default character was
  `(15.42, 20.12) hp 1/1`. **The plugin route is now the DEFAULT loader**
  (`KOTORCOOP_PLUGIN_LOAD=0` forces clicking; clicking remains the automatic fallback).
- **⚠ A latent bug that was silently hiding peers: the server MODE at
  `serverInternal+0x10008` is a WORD, not a dword** — the engine uses
  `cmp word ptr [esi+0x10008], 1` (`0x4B6294`). Both the plugin and the agent read 32 bits,
  picking up the adjacent field. A click-loaded world happened to read `0x00000002` so
  nobody noticed; a plugin-loaded one reads `0x555FFC02`, which failed the world gate, made
  every module name fall back to `area{id}`, and hid peers behind the module filter —
  looking exactly like a broken network layer. Both sides now read 16 bits. **If a channel
  ever "works sometimes", suspect a width like this before suspecting the network.**
- **Host lives in the panel, and the agent starts itself.** The save screen's Delete button
  (previously just hidden) is relabelled **Host** and rebound, so hosting and joining are one
  screen. `AgentLauncher` starts `KotorCoop.Agent.exe` from the plugin with the game's own
  pid (`coop.ini auto_start_agent=0` opts out; a missing executable is skipped). Installing
  the plugin + agent + `coop.ini` into the game folder is now the whole installation.

⚠ **Shared block is version 15.**

**Two things 0e/0f flagged as unproven are now measured (2026-08-01), details in
`docs/live-verification.md`:**
- **Party reconciliation.** Solo mode is real and one-sided — `tools\partystate.ps1` (new)
  reads `soloMode 0` on the host, `1` on the client, same companion in the same roster slot.
  The mirror was proven by a CONTROL, not a coincidence: displace the client's companion and
  it converges back to the host's exact position in ~4 s with the agents running, and stays
  put for 6 s+ with them stopped. Two identical saves place a companion identically, so the
  control is the only thing that distinguishes mirroring from "same script" — always run it
  that way. Companions actually FOLLOWING is still untested (Endar Spire script-locks Trask).
- **The mod-mismatch path**, with a real difference rather than a fabricated digest:
  `install-datamods` against one install only (510-row `appearance.2da` vs 509). Host and
  client independently produced the SAME verdict — `compatible, 3 warning(s)`,
  `OFF: Appearance` — and behaviour matched: phantoms still spawned and were equipped with no
  appearance write. Degraded, not refused. The REFUSE classes (different exe/save) remain
  unit-test-only. The verdict is now shown in the agent dashboard's `content` block; it used
  to exist only on the session object, so a silently disabled channel looked like a bug.

**Panel coordinates, for anyone scripting clicks:** `saveload.gui` is 640×480 and the panel
is **CENTERED, not scaled**, at 800×600 — screen = layout + (80, 60). Connect (`BTN_SAVELOAD`,
layout centre 320,424) is at **(400, 484)**; Host (`BTN_DELETE`, 182,424) at **(262, 484)**.
Getting this wrong looks exactly like a dead button.

**Screenshot trap:** `tools\shot.ps1` returns a STALE frame after a minimise/restore — it
captured the main menu for an instance that was demonstrably in-world (28 creatures, phantom
alive, frames advancing). Trust the logs and memory over a capture; at the menu the capture
is often black entirely.

---

## 0e. NATIVE MENU + DEATH + THE LOAD CRASH FIXED, 2026-08-01

⚠ **Superseded on the block version by 0f (now 15); protocol is version 9.** The block
signature carries the version, so a mismatched pair fails to find each other instead of
reading wrong offsets.

**The headline: the whole join is now one click, in the game's own UI, and it works live.**
Main menu → **Co-op** → a native panel listing the LAN servers → click **Connect** → the
agent joins the host → the save loads → both players are in one world. Verified end to end
this session (log excerpt, joiner instance):

```
native browser: row 0 confirmed; connect handed to the agent
menu: constructing the save panel ... creating the server and loopback session
menu: player faction repair returned 1
menu: the world is up
agent reports 1 remote peer(s)
```

- **The browser is a real KOTOR panel now, not overlay text** (`NativeBrowserController`,
  `docs/research/native-browser-panel.md` — every ABI read out of the binary first). It
  constructs the engine's own `CSWGuiSaveLoad`, clears the save rows, and fills the embedded
  `CSWGuiListBox` with one engine-cloned row per discovered server; native font, borders,
  scrollbar, mouse and keyboard. Rows are cloned through the engine's `Initialize_2` from the
  listbox's own prototype, captions go through `SetText`, and `AddControls` transfers row
  OWNERSHIP to the listbox — so a refresh is just another `AddControls` and teardown frees
  everything. Two non-obvious facts drove the design: showing a child panel marks the MAIN
  MENU for the manager's delete sweep (so `[clientInternal+0x280]` DANGLES while the browser
  is up — `NativeBrowserController.IsOpen` gates the other menu controllers off), and the
  stock **Back** button already performs a perfect close, so it is left wired on purpose.
  The old queued-text browser survives as the in-world F9 panel and as the fallback if a
  native guard ever refuses.
- **⚠ The menu-load replay had to move to the MainLoop hook.** The SwapBuffers frame hook is
  movie-gated at the menu and NEVER fires there — measured, the frame counter sits at 0 at a
  settled menu — so a load request serviced from `OnFrame` sat forever. This is why the
  in-process load looked dead in earlier sessions.
- **THE WORLD-UP CRASH IS FIXED** (`docs/research/world-up-faction-crash.md`). Root cause: a
  load driven from the menu brings the player up **in no faction** — `PostProcess` adds a PC
  to the player faction but silently falls through when the faction-manager slot is not yet
  populated, and nothing retries. `GetFaction` then returns null forever and the server's
  object-update pass dereferences it ~0.4 s later (`0x5BF694` in `GetSingletonParty`, plus
  six more windows like it). Fix: `Odyssey.EnsurePlayerFaction` makes the engine's own
  `CSWSFaction::AddMember 0x5BFA70` call at world-up — byte for byte what `PostProcess`
  would have done. One call closes every window; the game now runs indefinitely where it
  used to fault in half a second. `PartyFactionGuard` (a byte patch routing a null faction in
  `HandlePlayerToServerParty` onto the handler's own default-faction path) fixed the earlier
  `0x5272D8` crash and stays installed as a canary.
- **⚠ BUT: the in-process load produces a DEFAULT CHARACTER, not the save's.** Measured on
  both instances: after the replay the PC stands at the module's start point with starting
  hit points (`pos (15.42, 20.12) hp 1/1`) instead of the saved character (`(20.01, 22.15)
  hp 12/0`). The replay reproduces the module transition but NOT the engine's character-login
  chain (`EndStartNewModule → LoginIFOCharacter → LoadCharacterFromIFO(TEMP:pifo) →
  PostProcess`), which is also exactly why the faction was missing. So the browser's
  auto-load deliberately uses the **menu-click route**, which loads the real save. Anyone
  reviving the in-process route must reproduce that login chain — the addresses are in the
  crash doc.
- **Co-op death handling** (`DeathController`, `docs/research/death-handling.md`), **verified
  live**: HP forced to 0 on one instance produced `death: the local player is down` →
  the peer standing beside them triggered `death: revive applied; the player stands up` →
  `topped up to 15 hp`, all within a second, both instances still running. KOTOR downs party
  members rather than killing them and stands them up itself once the area is clear; that is
  kept. Co-op adds the rescue (a standing peer within ~3.5 units revives you immediately,
  through the engine's own Resurrection effect — a raw HP write leaves a prone body with
  zeroed AI flags, never ship that) and makes a wipe unanimous (one instance's game-over is
  swallowed and only released when EVERY player is down, so nobody is dropped to the menu
  mid-fight). Two byte patches, both verify-bytes-or-refuse and both INERT until the agent
  turns them on. ⚠ `ChangeCharacter 0x5F6B60` is `ret 8` with a `sub esp,0xC; push esi;
  mov esi,ecx` prologue — the first attempt assumed `push ebp` and was correctly refused by
  the guard rather than corrupting anything.
- **Save-identity + per-module GIT digests** complete the mod-compatibility handshake
  (protocol v9): a different save refuses the id-keyed channels, a mod-edited module drops
  enemies/doors/items for that module only and restores them in the next module where the
  GITs agree. 265 tests pass.
- Diagnostics: `tools\blockpoke.ps1` gained `browser` (list/open/close the panel) and
  `death` (state, and `death revive` to fire a rescue by hand).

**Trap, still live and worth knowing:** injected clicks are discarded unless the game window
is the ACTIVE window (every mouse branch in the window proc is gated on `CExoInput::GetActive`).
`PostMessageA(WM_LBUTTONDOWN/UP)` with the coordinate in `lParam` works and ignores the
physical pointer (`docs/research/mouse-cursor-input.md`), but the activation gate still
applies — a minimise/restore cycle is what reliably revives it.

---

## 0c. CONNECTION + PARTY BUILD, 2026-07-31 (later) — party sync, password, reconnect, Connect menu

⚠ **Protocol is version 5, shared block is version 8.** Rebuild both agents and reinstall
the plugin in both game folders before the next live run. Four features, all unit-tested
(156 tests), none yet live-verified:

- **Party reconciliation** (companions no longer drift). Host-authoritative: the host's own
  engine AI drives Trask/Carth and publishes their state (`PartySnapshot`, keyed by NPC
  ROSTER SLOT — slot is save-stable, object id is not). Each client freezes its own party
  with the engine solo-mode flag (`partyTable+0x190`, verified via SetSoloMode disasm at
  0x00565500) and mirrors the host's positions (writes stick because solo mode stops the
  follow AI — trap 3). Agent-side (KotorGameBridge), no plugin change. Party is a
  SessionCapability dropped on a save mismatch. Default on, `--no-party-sync` escape hatch.
  Freeze released on shutdown. Risk: solo-mode behaviour is inferred not measured (first
  live test: set `+0x190=1` in one instance and play 5 min). Death-swap (player controlling
  a companion after a PC death) is a known unhandled edge. Full design:
  `docs/research/party-reconciliation.md`.
- **Session password**: Hello carries `SessionPassword.Hash`; a host with a password sends
  a typed `E_PASSWORD` reject to a wrong one. `--password`.
- **Automatic reconnect**: the client detects link loss (no inbound for the peer timeout)
  and re-handshakes; the host matches a returning Hello by the client's stable nonce and
  restores its slot (same id/name, no doubled phantom, no ghost). Survives a host crash or
  a network blip in-game.
- **In-game Connect menu** (foundation): the plugin rebinds the dormant main-menu BTN_WARP
  to "Co-op" (`AddEvent 0x0041AB20`, events 0x27/0x2D → a thiscall stub bumping a connect
  serial); the agent `--menu` mode reads `coop.ini` (mode/host/port/password/name) and
  connects on the press. OPT-IN via `coop.ini menu_button=1`, guarded, because the GUI
  rebind is unverified engine code. The interactive in-game IP-entry panel (reuse
  `CSWGuiSaveNamePanel`, typed string at panel+0x548, intercept OK) is designed in
  `docs/research/connect-flow.md` and is the remaining piece.

## 0b. "PROPER CO-OP" BUILD, 2026-07-31 — research + mod-compat + map + name plates

Five parallel research deep-dives (all in `docs/research/`, verified against the GOG
binary) drove a build pass. ⚠ **Protocol bumped to version 4** — Hello/Welcome now carry a
content digest, so an agent from before this bump is cleanly rejected. Rebuild both agents.

**Mod compatibility — the user's headline requirement, now real.** The audit
(`docs/research/mod-compatibility.md`) found 20 vanilla-data assumptions, several
corruption-class, and the globals digest that the docs claimed protected quest state was
*computed but never compared*. Built:
- **C# game-file readers** (`src/KotorCoop.Memory/GameFiles/`: KEY/BIF, GFF, 2DA, TLK,
  ERF/RIM), ported from `tools/kotorfiles.py`, verified byte-identical to it (appearance.2da
  509 rows, global.jrl 101 categories under restype **utw not jrl** in this build, dialog.tlk
  49265 entries).
- **A content-digest handshake** (`ContentDigest`, `ContentCompatibility`,
  `SessionCapabilities` in the protocol assembly; `InstallDigest` in memory). The agent
  fingerprints its install (exe ends+size, override/modules census, appearance.2da rows+labels,
  global.jrl tags, dialog.tlk header) and carries it in Hello/Welcome. `Compare` judges it
  identically on host and client into a capability set: **refuse** a different exe; **degrade**
  the specific channel a mismatch invalidates (a different appearance.2da drops the appearance
  write, a different save/GIT drops enemies+doors+items, a different globals catalogue drops
  world state+doors); **warn** for the expected case of two differently-modded override folders.
  The agent's pump gates every channel on its bit, and masks appearance/equipment on the peer
  write when their capability is off. `--allow-different-save` relaxes a save mismatch.
- Audit fixes folded in: version-mismatch now sends a typed Reject (was silence);
  equipment-slot object-id bug (slots hold ids not pointers — that fix is in `0a` too); the
  phantom blueprint now tries a chain (`k_coop_phantom` then `n_commm`) and **latches
  "phantoms unavailable"** after 5 misses instead of retrying forever with an unbounded
  game-thread log; the phantom faction is **forced Neutral** rather than trusted from the
  blueprint (a modded `n_commm` faction would resurrect the aggro sink).

**Map presence** (`MapMarkerController`, `docs/research/map-presence.md`). Each peer is a
map-note waypoint on the M-screen map, constructed directly (the NWScript CreateObject
routine has an engine bug for waypoints — makes the object, returns OBJECT_INVALID) and
moved per tick; the map's own draw re-reads the position every frame, so no GUI patching.
No new protocol. Fog-of-war reveal deferred (its areaMap pointer is not DB-verified).

**Name plates FIXED** (`docs/research/name-plates.md`) — and this corrects `0.4`/`4.11`,
which had recorded them as suspect on a TLK theory. The band renders from a plain CExoString
on the **client twin**, and the engine's per-frame diff has no name-changed bit, so a name
reaches the twin exactly once (first update). `SetCreatureFirstName` now writes the twin
(`CSWCCreature::SetName 0x0060D600`) as well as the server locstring; `PhantomController`
re-asserts until the twin appears. No TLK needed — substrings beat the strref.

**Shipped after the note above:**
- **`k_coop_phantom.utc`** — now a real file at `profiles/override/`, generated by a
  round-trip-verified C# GFF writer (`GffWriter`), deployed by `build.ps1 -Install`. Blanks
  the 14 stock AI scripts, silences barks, adds the equip proficiencies, sets
  appearance/Min1HP/Plot/FactionID from frame zero. The runtime `MarkAsRemotePlayer` writes
  stay as the `n_commm`-fallback safety net.
- **World-digest (globals-catalogue slice)** — `WorldDigestPacket` exchanged both ways
  in-world; a globals-catalogue mismatch now drops world state + doors (closes the audit's
  critical "computed but never compared" finding). Save-identity and per-module GIT digests
  remain the follow-up.

**Still designed-not-built:** party reconciliation
(`docs/research/party-reconciliation.md` — gated on live solo-mode measurement), and the
save/GIT half of the world digest. 147 tests pass.

## 0a. LIVE VERIFICATION, 2026-07-31 (pids 68952 host / 54536 client, both GOG, Endar Spire)

Everything below was observed in a real two-instance session, not inferred.

| Capability | Result |
| --- | --- |
| Phantom spawn, both directions | ✅ `spawned phantom 1: id 0x12D` in both |
| **Movement gait matches (the run fix)** | ✅ `move#1 ... -> goal (20.01,22.15) (run)` in both — previously always walked |
| **Phantom tracking accuracy** | ✅ 2-4 units while the peer moves, **settles to 1.19 units** when they stop (was up to 2.5 + lag) |
| **Equipment: body armour** | ✅ `equip: phantom 0x12D wears g_a_clothes01 (slot 1)` |
| **Equipment: weapon** | ✅ `equip: phantom 0x12D wears g_w_shortswrd01 (slot 4)` |
| Ground item drop | ✅ same object id `0x131`, same coords, 149 ms apart |
| Door unlock replication | ✅ `door 0x43: unlocked on the session's authority`, confirmed by reading `+0x2C4` in the peer |
| Focus keeper / auto-pause | ✅ both cleared on cue |

Screenshot evidence: instance 1 renders the peer's phantom in the corridor wearing
clothes with the short sword on its back, alongside the local PC and Trask.

⚠ **The bug this run caught, and it is a big one for anyone reading inventory:
`CSWInventory` slots hold OBJECT IDS, not pointers.** Measured live on the PC: body slot
(`+0x08`) = `0xC9`, right weapon (`+0x14`) = `0xCA`. Dereferencing them as pointers is not
a crash — it silently yields an empty tag for every slot, which reads exactly like "the
player has nothing equipped", and the first live equipment run duly published three empty
resrefs and equipped nothing. The external agent has no `GetGameObject`, so ids are
resolved through a tag cache built by scanning the `CSWSItem` vtable `0x748DA0`
(`KotorGameBridge.RebuildItemTagCache`).

Second, smaller trap from the same run: **a `CExoString`'s length can include the
terminating null**, so a tag arrives as `g_a_clothes01\0`. That is not a valid resref, the
engine's `CreateItemOnObject` finds no blueprint, and the equip is refused with failure 7
while the log line still LOOKS correct (`equip: g_a_clothes01? refused`). Resrefs are now
truncated at the first character a resref cannot contain.

Also fixed here: `tests/KotorCoop.Tests/PacketCodecTests.cs` had two `HelloPacket`
constructions left over from before the content-digest field landed; they now pass
`default`. 128/128 tests green.

Startup note: instance 2 needed the trap-22 recovery again, and what finally worked was
**minimise → restore → two throwaway clicks in the client area → `agent load`, all in one
uninterrupted sequence**. Input goes stale again if anything else runs in between.

---

## 0. Addendum, session of 2026-07-30 (evening)

Everything below the line was true at handoff; this section is what changed since.

**Built and verified live (two GOG instances):**

- **Epoch invalidation** (was 7.9's two latent bugs). `PluginHost.OnFrame` now opens with
  `Odyssey.TryGetWorldEpoch` — gate on `loading_module == 0 && server_mode == 2`, epoch =
  object-array pointer (replaced by `UnloadModule`) + FNV of the module name from
  `CServerInfo+0xFC`. On change every controller's state is dropped before anything runs.
  Pre-store flush: `CSWSModule+0x220 -> CSWSArea+0x2C4 trans_pending` opens a ~150-frame
  window in which phantoms and replicated items are destroyed (one per frame) so
  `StoreCurrentModule` cannot bake them into the save.
- **The module filter now uses the real module name** (`EngineGlobals.TryReadModuleName`,
  `serverInternal+0x10004 -> +0xFC`), not `area{id:X}` — area ids recycle across modules.
  Side effect: KOTOR and the mock now agree on module names (trap 8 partly obsolete).
- **Door sync, verified live** (unlock leg). Doors ride the existing reliable max-merge
  flag channel: key `0x44<<24 | moduleHash8<<16 | objectId`, value
  `openState(0-2, 3=destroyed) | 4 unlocked` — monotonic by construction. Agent scans the
  `CSWSDoor` vtable `0x7498B0` (open `+0x2CC`, locked `+0x2C4`), baselines silently per
  module, diffs. Plugin `DoorController` applies: unlock is a byte write; open goes
  through `SetOpenState(state, deferred=1)` (verified by disasm: writes pending `+0x2CD`,
  plays the swing via vtable `+0x7C`) with a forced `MoveToNextOpenState` (`0x589680`,
  commits state + walkmesh) if the engine has not committed after ~3 s. Verified: unlock
  written in instance 1 propagated and logged `door 0x46: unlocked on the session's
  authority` in instance 2 within ~5 s. The open leg is coded but was not observed live.
- **Client-side encounters disabled** (`active`=0 `+0x238`, `started`=1 `+0x278`,
  vtable `0x749D98`), once per module, on every non-enemy-authority peer.
- **Dialogue guard** (7.9 step 0) was already built and wired; confirmed present.
- **Item drop regression-tested** on the new build: replicated in <1 s both ways.

**Built after live user feedback, needs live verification:**

- **Movement fidelity.** The phantom now runs when its player runs (the peer's raw
  animation travels in the slot at `+0x1C`, previously never written; walk `0x2712` is
  the only walking gait), goal-resend threshold dropped 2.5 -> 0.6 units with a 5-step
  re-path cooldown, facing is mirrored on arrival, and a phantom >15 units adrift snaps
  via `SetPosition` instead of walking a catch-up.
- **Equipment sync v1.** `PlayerState` gained three resref strings (body armor, right
  weapon, left weapon), read from `CSWSCreature+0xA2C -> CSWInventory` (slot pointers at
  `+0x4 + 4*slot`: body `+0x08`, right `+0x14`, left `+0x18`; item tag lowercased as the
  resref). The plugin equips the phantom for real: `CreateItemOnObject` (`0x52F570`) then
  `ActionEquipItem` (`0x5355F0`, instant, OBJECT_SELF redirected), both resolved by
  handler-address scan. Missing resref (peer has a mod we lack) fails soft at create.
  ⚠ Untested risk: `n_commm` may lack proficiency/armor feats and refuse some equips —
  watch for `equip: ... refused` in the plugin log; fallback is calling
  `CSWSCreature::EquipItem` (`0x4feb90`) directly, which needs its 5 args disassembled.
- **Shared block is now version 5** (signature `... 05 00 00 00 ...`), `TotalSize 0x920`:
  door slots at `0x690/0x698`, equip resrefs at `0x7A0` (8 peers x 3 x 16 ascii).
  `tools/pluginstate.ps1` now matches any version.

**User direction (recorded in auto-memory too):** sync *everything* visible (equipment,
map presence), movement must feel real-time, expect users to have mods, prefer
game-integration over memory-editing — an in-game "Connect" main-menu flow (via 4.6's
direct GUI-event firing plus an override `.gui` mod) should replace external menu
clicking, and the map should show the other player (candidate: engine waypoints with map
notes moved per-tick). NWScript has no networking, so the dinput8 plugin remains the
backbone regardless.

**The engine-side load path, finally mapped (supersedes the dead end in 4.2).**
The route 4.2 could not find is `CServerExoApp::ExecuteLoadModule(saveInfo*)` at
**`0x004AEBF0`** — SendLoadGameRequest was a red herring; the GUI never relies on the
message being serviced. The working sequence, read out of
`CSWGuiSaveLoad::LoadPCAutoSave` (`0x006CA250`, thiscall, one arg = save entry) and its
call site in `LoadGame` (`0x006CB0E0`):

1. `CClientExoApp::SetInputClass` (`0x005EDA60`) / `DisableInput` (`0x005EDBE0`), and
   `SetLoadStep(4, 0x17)` (`0x005EDFD0`) — the loading-screen GUI state.
2. `CServerExoApp::UnloadModule` (`0x004AE8A0`), then `0x004AED00(1)`.
3. `CServerExoApp::SetDoingStartNewModule(1)` (`0x004AF090`).
4. `ExecuteLoadModule(saveInfo)` — the actual transition.

`LoadPCAutoSave` also shows how to build the `saveInfo` without the GUI list: it
allocates **0xD0** bytes via `operator new` (`0x006FA7E6`), constructs it with
`0x005DD9C0`, and fills it from the literals `"AUTOSAVE"`/`"%06d - %s"` (`0x007455A0`) and
the `"SAVES:"` alias (`0x00745174`) — i.e. directory `000000 - AUTOSAVE`. So the
no-input-injection load is: copy the co-op save over the autosave slot on disk (a save is
just a directory of 5 files), construct `CSWGuiSaveLoad` (`0x006CC680`, args
`(owner=[mainmenu+0x18], 0, 1)`; main menu object at `clientInternal+0x280`, only at the
main menu) and call `LoadPCAutoSave` — or skip the panel entirely and replicate its body.
Spare shared-block space at `0x970-0x97C` is reserved for the request/ack fields; the
handler must run BEFORE PluginHost's epoch gate, which returns early whenever there is no
server (i.e. exactly at the main menu). Not yet implemented — needs crash-testing.

Also mapped for the same flow: `CSWGuiMainMenu::OnLoadSaveGame` (`0x0067B1A0`, thiscall,
arg = a control whose `+0x4C` must be non-zero) validates `mainmenu+0x140C`, allocates
the 0x1160-byte `CSWGuiSaveLoad`, and shows it via `0x0040BC70(owner, panel, 2, 1)`.
`CSWGuiSaveLoad::LoadGame` takes a populated LIST ENTRY (flags `+0x1C4`, name `+0x210`),
so driving it requires `PopulateGameList` (`0x006CC160`) — the autosave route avoids all
of that.

**STATUS 2026-07-31: the load WORKS. The world comes up with no input injection.**
What remains is a crash ~0.5 s later, now fully characterised (see below). Two bugs were
found and fixed to get here, both worth knowing:

1. **The autosave slot is `000001 - AUTOSAVE`, not `000000`.** LoadPCAutoSave formats
   `Format("%06d - %s", 1, "AUTOSAVE")`. Staging to `000000` produced a load that reached
   `SetLoadingModule(1)` and then silently did nothing — no module, no error.
2. **The save-list entry's MODULE NAME at `entry+0x00` is the load's real argument.**
   `ExecuteLoadModule` forwards the entry verbatim and `CServerExoAppInternal::LoadModule`
   (`0x004B95B0`) reads `[ebp+8]` as a `CExoString` and loads THAT module. A zeroed entry
   = an empty name = the silent stall. The agent reads the name from the staged save's
   `SAVEGAME.sav` ERF key list (the entry whose restype is **2057** — the module `.sav`;
   note `tools/kotorfiles.py` claims 2055, which is wrong for this build) and publishes it
   in the shared block at `0x9B0`; the plugin builds it with `CExoString::CStrConstructor`
   (`0x005E5A90`) into `entry+0x00`. Also required: the entry must live on the ENGINE heap
   (`operator_new`), not a pinned managed array — LoadModule keeps the pointer.

**The remaining crash, measured not guessed.** A vectored-exception probe
(`CrashProbe.cs`, kept in the plugin — it logs faulting address, EIP and image-range
stack values on the first access violation) caught it exactly:

```
CRASH: access violation at 0x5272D8, eip 0x5272D8
stack: 0x527D7F 0x4B1634 0x5D5852 0x4BAC36
       HandlePlayerToServerMessage <- HandleMessage <- ProcessReceivedFrames <- MainLoop
```

`0x5272D8` is inside `CSWSMessage::HandlePlayerToServerParty` (`0x00526910`): it calls
`CSWSPlayer::GetGameObject` (`0x00561080`), then vtable `+0x30`, then
`CSWSCreature::GetFaction` (`0x00513FC0`), and dereferences the result — which is null
because **the server has no logged-in player character**. `players=0` in the diagnostics
throughout confirms it. The missing step is the client's login: the retail flow has the
server send `StartStartNewModule`, the client's
`HandleServerToPlayerModule_EndStartNewModule` (`0x00652860`) reply with
`SendPlayerToServerLogin_LoginIFOCharacter` (`0x00678030`), and only then do party
messages have a player to resolve. Dropping our own `RequestStatus` did NOT help (tested),
so the next step is to drive the login explicitly — either call
`SendPlayerToServerLogin_LoginIFOCharacter` once the module is up, or find why the
server's `EndStartNewModule` notification is not reaching the client over the loopback.

Two things that made it worse and are now avoided: **do not** manually pump
`ServerMainLoop`/`ProcessReceivedFrames` once the load is armed (the game's own outer loop
services it; double-stepping crashed it at world-up), and **do not** `AddPanel`/
`PopModalPanel` the stand-in save panel (it only needs to exist as LoadPCAutoSave's
`this`).

**Implementation state (2026-07-30, late):** built as `MenuLoadController` (plugin) +
`PluginMenuLoader` (agent) + `SaveLoader.TryLoadViaPlugin`, request fields at shared
block `0x9A4/0x9A8/0x9AC`. The full replayed branch (0x6CB256..0x6CB3E8, every callee
identified by DB — panel ctor + AddPanel, ResetCurrentSessionStartTim,
SetGlobalUnpauseableState, PopModalPanel, HideSWInGameGui, SetLoadScreenByModuleName,
SetLoadMusic, ShowLoadScreen, SetLoadBarProgress, UpdateScreen, **CreateServer**, both
world-timer pauses, **CNetLayer::StartConnectToSession(0, clientName, "", 10,
byte[0x74C540]) — the loopback connect 4.2 was missing**,
SendPlayerToServerStatus_RequestStatus, SetInputClass(0,1), DisableInput,
LoadPCAutoSave(panel, dummyEntry)) now **fires without crashing**: server created, world
timer runs, load-state accepted. What it does not yet do is finish: **the module
transition is never serviced** — black screen, no module, no creatures, indefinitely.
Two leads, in order: (1) the loopback connect completes asynchronously and needs real
pumping between connect and load. A two-phase split WAS tried (fire the connect, return
to the game, call LoadPCAutoSave later) but the grace was measured in frames — 600
frames passed in 0.2 s because the menu renders unthrottled — so the variant actually
tested gave the connect only 200 ms; retry with a WALL-CLOCK delay (3-5 s) and, better,
find the CNetLayer "connected" flag and gate on it. (2) compare against
`CSWGuiMainMenu::OnModulePicked` / the `warp` path, which reaches a world from the menu
without a save at all. A zeroed 0x400 dummy entry with flags `+0x1C4 = 0x30` was tried;
no change. Earlier one-line attempts (bare LoadPCAutoSave with no preamble) CRASH the
game — the server-null deref 4.2 predicted — so the preamble is definitely
load-bearing. The route is OPT-IN via `KOTORCOOP_PLUGIN_LOAD=1` until it completes,
because a stalled load leaves a black screen the click fallback cannot rescue.

**New traps:**

22. **A game window that has never been foreground-activated ignores all injected input**
    — `DeactivateRenderWindow` ran `SetInputActive(0)` and nothing re-enabled it. The
    menu renders, `GetForegroundWindow` can even report the game, clicks and hovers do
    nothing, and no in-game cursor is rendered (that absence is the tell). Fix: a real
    activation cycle — minimise then restore the window — then input lands. This is what
    "cannot drive the menu: foreground pid is 0" degenerates into after a transient
    focus failure at launch.
23. **The game's menu cursor can be positioned by writing `CClientExoAppInternal+0x3C8/+0x3CC`
    from outside**, then clicking with button-only `SendInput`. But hover/hit state only
    refreshes on a real mouse-move event, so send a net-zero relative wiggle between the
    write and the click (`scratchpad/menuclick.ps1` had this working except the final
    hit test — untested further once minimise/restore fixed the root cause).
24. **`Read-Host` in a non-interactive `demo.ps1` run dies instead of waiting.** The
    script's instance-2 fallback prompt assumes a human terminal.
25. **Menu input injection is intermittently dead even with the game foreground and the
    window activation-cycled.** Observed repeatedly on the evening of 2026-07-30: the
    same fresh-launch + `agent load` sequence succeeded five times and then failed five
    times across both instances, with `GetForegroundWindow` returning the game and the
    plugin forwarding DirectInput correctly (an A/B with the plugin removed also
    succeeded once and proved nothing — the failures did not correlate with the plugin
    in the end). Root cause not identified; a human clicking the menu always works. This
    unreliability is the operational argument for the in-process load above.

### 0.4 Fifth addendum, remote session (~22:20): first live results for the v7 tranche

Two instances were brought up and both agents connected, so parts of the tranche now
have live evidence. **Read 0.5 first for the input oracle** — it is what made the
session start at all.

**Verified live (two instances, pids 60396 host / 65268 client, Endar Spire):**

- **Phantoms spawn both ways with the v7 plugin**, same object id `0x12D` in both, and
  the agents' dashboards saw each other (`remote peers (1)`, `25 known (25 alive)`).
- **The 7.4 phantom redesign is exactly as intended, read out of live memory in BOTH
  instances**: `plot=0`, `min1hp=1`, `+0x9F0 = 0xFFFB` (attack bit `0x04` cleared),
  `appearance=114` (the PC's row). This is the first live confirmation that a phantom is
  damageable-but-unkillable rather than an immortal aggro sink.
- **Encounter disabling, focus-pause suppression and auto-pause suppression** all logged
  on both sides as before.

**Found and FIXED live — shared pause was pausing the wrong half.** The plugin was
calling `SetPauseState` correctly and logging re-assertions, yet the pause bit read back
clear within a frame; a raw memory write behaved identically, which proved it was the
engine rather than the mod. Cause: **the client keeps its own pause mask at
`CClientExoAppInternal + 0x1CC` with its own world timers**, and reconciles the server
back to it every frame. A real pause holds both halves.
`Odyssey.SetPlayerPause` now drives `CClientExoApp::SetPauseState` (`0x005ED650`) and the
server together, and `EngineGlobals.TryReadPauseMask` reports the union of the two masks.
Built and unit-tested; the live re-test did not fit in this session.

**Not verified, and suspect: phantom name plates.** The locstring at `stats+0x34` does
change (count fields go 1 where an untouched creature's are 0), so `AddString` runs — but
the stored text could not be found by walking the internal, and the control creature
(Trask, whose band reads "TRASK" in game) has an entirely EMPTY locstring, i.e. real
names come from a **TLK strref**, not inline text. So the inline-string approach may be
the wrong mechanism even when it succeeds. Next step: find how `CExoLocStringInternal`
stores an inline string (or set a strref instead), and confirm visually by selecting the
phantom.

**Still unverified live** (needs a stable session): travel-follow, death poses, journal
merge, client damage floors, and the pause fix above.

### 0.6 Where the menu-input problem actually stands (end of the 22:30 session)

The oracle in 0.5 is sound and the tooling around it is committed, but **be honest about
the hit rate**: early in the evening two consecutive launches had live input and loaded
first try; from ~21:55 onward *every* launch (six or more, both game directories) probed
dead, with the main menu confirmed up and the window confirmed foreground. Three
candidate causes were eliminated by measurement, and each elimination is worth keeping:

1. **Not "probed too early."** The probe now waits for `CSWGuiMainMenu`
   (`clientInternal + 0x280`) before judging, and a screenshot confirms the menu is
   drawn. (This *was* a real self-inflicted bug first — it relaunched healthy instances
   four times in a row before being fixed.)
2. **Not "the window was in the background."** `SetForegroundWindow` is refused
   *silently* for a detached/hidden caller, which is indistinguishable from the game
   ignoring input; `inputalive.ps1` now forces foreground via `AttachThreadInput` + the
   stray-ALT trick and **verifies** it, returning exit 2 ("result is not meaningful")
   rather than a false "dead".
3. **Not the plugin.** Dead instances behave identically with and without it (matches the
   earlier A/B in trap 25).

So the remaining suspect is genuinely inside the game's DirectInput acquisition, and the
practical position is: **menu automation cannot be relied on at all right now**, while a
human clicking Load Game → slot → Load still works instantly. That is the operational
argument for finishing the in-process load path (addendum 0's `ExecuteLoadModule` route,
which is crash-free and stalls only at the module transition) — it is not a nicety, it is
the only unattended way in.

`tools\verify.ps1` is written and ready: give it two in-world pids and it prints PASS/FAIL
for phantom presence, the 7.4 ownership fields, appearance, the shared-pause hold/release
across both masks, and a journal apply. It needs nothing but a session that started.

### 0.5 The menu-input oracle (this is the one to remember)

Trap 25's "menu input intermittently dead" is diagnosable after all, and the diagnosis is
cheap. **The game's own GUI hit-test predicts it perfectly.** Write the game cursor
(`CClientExoAppInternal + 0x3C8/+0x3CC`), send a net-zero relative mouse wiggle so the
game re-hit-tests, then read the control under the cursor at `CSWGuiManager + 0x08`
(`[0x7A39F4]`):

```
hover != 0  ->  every load attempt that followed succeeded ON THE FIRST TRY (2/2 instances)
hover == 0  ->  every load attempt failed (8+ attempts), activation cycles included
```

Two operational consequences, both now encoded in `tools\`:

1. **A minimise/restore activation cycle sometimes revives input and sometimes does not.**
   When it does, `hover` goes non-zero and loading works immediately; when it does not,
   no amount of clicking recovers that process. **Relaunch instead of retrying.**
2. `tools\inputalive.ps1` is the probe (exit 0 = alive), and `tools\session.ps1` is an
   unattended two-instance bring-up that probes, relaunches dead instances, loads both
   saves and starts both agents. That replaces the hand-holding `demo.ps1` flow.

### 0.3 Fourth addendum, remote session (same evening, ~21:10): verification attempt, yielded

The remote session attempted the live two-instance pass of everything built and stopped
after colliding with this session's own live work. State worth having:

- **The v7 plugin (with menu-load) is installed in BOTH game directories** as of 21:01.
- **The LoadPCAutoSave replay crashes the game**: three swkotor.exe Application-log
  crashes 20:58–21:01 (both sessions' tests), plugin log ending at
  `menu: panel added, calling LoadPCAutoSave` every time. `agent load --load-via-menu`
  now forces the old click route as an escape hatch until the replay is fixed.
- **The two sessions were killing each other's processes.** The remote session's
  instance (pid 23864, 21:03) died about when this session launched pid 26408 (21:05);
  earlier the remote session killed this session's demo agents as "strays" (trap 12).
  Until single-session ownership is possible, check process ancestry and recent
  StartTimes before killing anything named swkotor or KotorCoop.Agent.
- **Nothing from the v7 tranche has had its live pass yet**: travel-follow, name
  plates, death poses, journal merge, client damage floors, shared pause. The plan is
  scripted and cheap once two instances are up: `tools/blockpoke.ps1` fires travel and
  journal requests straight into a block, `sethp.ps1` stages damage/death,
  `sendkey.ps1` a real space-bar pause; the expected log lines are in each
  controller's LogFew strings.

### 0.2 Third addendum, remote session, second tranche (same evening, ~20:45)

⚠ **Shared-block collision warning for the load-path plan above:** the "spare space at
`0x970-0x97C`" reserved for the load request/ack fields is spare no longer — **shared
block version 7** (committed 31ffa96) occupies `0x96C-0x9A4`. Current v7 map, all new
fields beyond v6:

```
0x96C  u32  agent -> plugin   remote-pause desired (any peer holds a pause)
0x970  u32  plugin -> agent   remote-pause applied (echo guard for flag publication)
0x974  u32  agent -> plugin   journal request serial (written LAST)
0x978  32b  agent -> plugin   journal quest tag, ascii
0x998  i32  agent -> plugin   journal state
0x99C  u32  plugin -> agent   journal applied serial
0x9A0  i32  plugin -> agent   journal status
TotalSize 0x9C0 — free space for the load request/ack: 0x9A4-0x9BC.
```

Built this tranche (all committed, 118 tests pass, plugin builds; **untested in game**):

- **Journal replication** (quest merge): reliable JournalOp/Ack + version tripwire in
  the world snapshot + multi-part snapshot repair — the flag channel's shape with a
  string payload. Agent reads entries externally (`internal+0x1B770` party table,
  journal list at `+0x11C`: `{SJournalEntry* data, int size}`, tag CExoString `@+0x18`,
  state `@+0x20`, stride 0x3C — derived from GetState/SetState disassembly); apply goes
  through the plugin: `CSWSJournal::SetState` (`0x005C5A40`, thiscall
  `(CExoString* tag, int state, int bOverrideHigher=0)`), which also refreshes the GUI.
- **Client damage counts**: clients stream their enemy census to the host
  (EnemyReport, module-scoped, 2 s freshness); the host floors its own enemies at the
  reported minimum (never below 1; death stays host-side). Fixed two latent bugs:
  enemy health read UNSIGNED (an overkilled enemy at -5 hp read as 65531, alive), and
  phantom exclusion relied on the plot flag the redesign below removes — now by object
  id from the block's phantom list.
- **Phantom vulnerability** (7.4's design, replacing plot): no plot, min-1-hp, attack
  bit (`+0x9F0 & ~4`) cleared, `PacifyCreature` (`0x004FD960`, thiscall, no args) on
  the owner leaving combat or going down. The owner's in-combat state travels in the
  new **PlayerState flags dword** (bit0 dialogue, bit1 paused, bit2 combat; slot
  `+0x3C`).
- **Shared pause**: either player's pause pauses both. Player pause is mask bit 0x01
  (`TogglePauseState 0x004BA610` → internal `SetPauseState 0x004B8110`, which drives
  `CWorldTimer::PauseWorldTimer` at `0x004ADFF0`). Plugin `PauseController` holds/
  re-asserts the bit via `SetPauseState(1, x)` while any peer's own-pause flag is up;
  the mirrored pause is excluded from republication by an edge-latch in the bridge plus
  the `0x970` echo-guard field. Only the pauser can release.
- **Wire protocol bumped to version 3** (PlayerState flags + journal + reports), so
  old agents are cleanly rejected at Hello rather than desyncing.

### 0.1 Second addendum, parallel remote session (same evening)

A remote session ran concurrently with the one above and added, on top of its work:

- **The repo is now under git** (task 7.8): baseline commit of the whole working state,
  then one commit per feature. `tools/__pycache__` is ignored; `tools/kotor1_0_3.db` is
  committed.
- **Tasks 40-42** (travel-together, phantom name plates, death-pose replication) — all
  engine detail in **4.11**. These bumped the shared block to **version 6, TotalSize
  0x980** (superseding the "version 5 / 0x920" note above): travel request at `0x920`
  (serial u32, module char[32] `0x924`, waypoint char[32] `0x944`, applied u32 `0x964`,
  status i32 `0x968`). Plugin and agents must be rebuilt together and `dinput8.dll`
  reinstalled in BOTH game directories before the next live run.
- 105 tests pass (9 new for the follow-the-host policy in `TravelPumpTests`).
- ⚠ The remote session killed stray `KotorCoop.Agent` processes at ~19:50 and ~19:54 to
  unblock builds (trap 12). If those belonged to a live run of this session, that is why
  it died; the two `swkotor.exe` instances were left untouched.

---

## 1. Current state

**Two live KOTOR instances now play co-op.** The old environmental blocker is gone: the
single-instance guard is a named mutex, not DRM, and the GOG build has no DRM at all.

| Capability | Status |
| --- | --- |
| Transport, session, interpolation, world-flag sync | done, 82/82 tests pass |
| Reading the real player out of `swkotor.exe` | working |
| In-process plugin loaded by the game itself | working |
| Once-per-frame callback on the game's main thread | working |
| Spawning a creature via the engine's own `CreateObject` | working |
| Driving that creature so it walks (pathfinding, animation) | working |
| **Two KOTOR instances running at once** | **working** (see 4.1) |
| **Each instance seeing the other player as a phantom, both ways** | **working** |
| **Phantom wearing the remote player's appearance** | **working** |
| **Dropping an item and it appearing in the other game** | **working** |
| **Player health replicated (the `hp 0` bug is fixed)** | **working** |
| **Host-authoritative enemy health, mirrored to the client** | **working, verified** |
| **A host-side enemy death killing the client's copy for real** | **working, verified** |
| **Item pickup detected and removed from the session** | **working, verified** (7.1) |
| **Phantom is neutral + plot, so it is not an aggro sink** | **working, verified** |
| **Auto-pause suppressed** | **working, verified** (`0x17000` -> `0x00000`) |
| World/quest state replication | code done; the table reads correctly live, cross-instance replication untested |
| Epoch invalidation, door sync, encounters off, equipment sync | **built** — see addendum 0 for what was verified live |
| **Module-transition following (travel together)** | **built** (SharedBlock v6, TravelController; see 4.11), untested in game |
| **Phantom name plates (peer's name in the selection band)** | **built** (see 4.11), untested in game |
| **Player death mirrored as a phantom death pose** | **built** (ambient state 4; see 4.11), untested in game |

Evidence from the two-instance run of 2026-07-30 (pids 35464 and 61108, both on the Endar
Spire, `area1`):

```
instance 1 plugin            instance 2 plugin
spawned phantom 1: id 0x130  spawned phantom 1: id 0x12F
move#2 -> goal (17.38,22.06) move#2 -> goal (17.55,21.58)
move#3 -> goal (14.81,21.46) move#3 -> goal (14.94,20.97)
move#4 -> goal (12.24,20.86) move#4 -> goal (12.39,20.08)
```

Live memory read of both processes at rest, showing each phantom tracking the other
process's real player, and both wearing appearance row 114 (the PC's row, not the
`n_commm` blueprint's 17):

```
pid 35464  player 0x7FFFFFFF (11.68,19.73) appr 114     phantom 0x130 (12.24,20.86) appr 114
pid 61108  player 0x7FFFFFFF (10.78,20.64) appr 114     phantom 0x12F (12.39,20.08) appr 114
```

Item drop, one keystroke on instance 1, both plugins reacting within 15 ms:

```
[12:29:38.495] i1: item slot 0: spawned item 0x10001 as object 0x131 at (12.68,20.73,-1.27), area 0x1
[12:29:38.510] i2: item slot 0: spawned item 0x10001 as object 0x130 at (12.68,20.73,-1.27), area 0x1
```

Confirmed by scanning both processes for `CSWSItem` (vtable `0x748DA0`): a real
`g_w_lghtsbr01` at identical world coordinates in both games, each with a non-null client
twin and the visibility flag set, so both are genuinely rendered and clickable.

Despawn is proven too — restarting an agent withdraws its slots and the plugin destroys
the body it owned: `item slot 0: destroyed 0x131 (item 0x10001)`.

Combat replication, verified on the two-instance run of 2026-07-30 (pids 59540 host and
34660 client). Both agents independently agree on the enemy census, and an enemy damaged
on the host is mirrored on the client within a tick:

```
baseline    host 0x14 hp 15        client 0x14 hp 15
host -> 6   host 0x14 hp 6         client 0x14 hp 6        (mirrored, not commanded)
host -> 0   host reports dead      client: "combat: killed 0x14 on the owner's authority"
                                   client 0x14 no longer exists - the real death path ran
untouched   host 0x17 hp 1         client 0x17 hp 1        (nothing else disturbed)
dashboards  "25 known (24 alive, 1 dead)" on BOTH sides
```

The damage in that run was applied by writing the host's memory rather than by swinging a
sword, because staging a real fight on demand proved unreliable — see 7.5. What is being
demonstrated is the *replication*: the host's authority over enemy health and death
reaching the client and being executed there through the engine's own death path.

---

## 2. Architecture

```
KotorCoop.Agent (external, x64)          KotorCoop.Plugin (in-process, x86 NativeAOT)
  attaches with OpenProcess                loaded by the game as dinput8.dll proxy
  finds player by creature vtable          hooks GDI32!SwapBuffers IAT for main-thread tick
  runs the UDP session                     calls CreateObject to spawn phantoms
  writes peers into the shared block  -->  queues ActionMoveToLocation to walk them
```

The two halves meet at a `SharedBlock` the plugin allocates inside the game. The agent
finds it by scanning for a 16-byte signature. Division of labour is deliberate: **all
discovery is external, all engine calls are in-process.**

```
src/KotorCoop.Protocol   wire format: packets, codec, player state, ground items
src/KotorCoop.Memory     OpenProcess, AOB scanner, offset profiles, KotorGameBridge
src/KotorCoop.Session    UDP transport, host/client, interpolation, flags, ground items
src/KotorCoop.Agent      CLI: launch, autotrack, track, run, scan, info
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86 -> dinput8.dll)
tests/KotorCoop.Tests    82 tests
profiles/                JSON offset profiles
docs/re/                 144 KB of verified reverse-engineering research (4 files)
```

Two replication channels share one shape, and it is worth seeing the symmetry:

| | avatars | world flags | ground items |
| --- | --- | --- | --- |
| carrier | `WorldSnapshot`, every tick | `FlagOp`, retransmit-until-ack | `ItemOp`, retransmit-until-ack |
| divergence detector | n/a, it is a stream | `FlagVersion` in the snapshot | `ItemVersion` in the snapshot |
| repair | next tick | `FlagSyncRequest` -> `FlagSnapshot` | `ItemSyncRequest` -> `ItemSnapshot` |
| in-game | plugin spawns/walks a creature | not wired to the game yet | plugin spawns/destroys an item |

Positions are disposable, so they stream. "A lightsaber is lying there" is world truth, so
it is reliable and versioned.

Enemies are a fourth channel and a different shape again: `EnemySnapshot`, host → clients
every tick, unreliable. Death is a *level* in that state rather than an event, so a lost
packet costs one tick of staleness instead of a permanently missed kill.

### Authority

| Thing | Owner | Enforcement |
| --- | --- | --- |
| A player's own character | that player's instance | separate processes; nothing else can drive it |
| A peer's phantom | the peer | friendly faction + `plot`, so it cannot be fought or killed locally |
| Enemies | the **host** | clients pinned with `min1hp`, so only the host can land a killing blow |
| Ground items | whoever dropped it, until someone picks it up | possessor field decides |
| Party members | **nobody yet** — both instances run their own. See 7.4. |

---

## 3. Environment

**The GOG build is now the primary target.** Use it, not the Steam copy.

| | GOG (use this) | Steam (legacy) |
| --- | --- | --- |
| path | `C:\GOG Games\Star Wars KOTOR (GOG)` | `C:\Program Files (x86)\Steam\steamapps\common\swkotor` |
| sha256 | `D3432B32...C173E381A` | `34E6D971...39F34C88` |
| DB `game_version` row | `gog 103` | `steam 103` |
| entry point | `0x006FB38D`, inside `.text` | `0x0086D2ED`, inside `.bind` |
| disk bytes == memory bytes | **yes** | no, packed |
| second instance | works, see 4.1 | refused by Steam DRM |

Second GOG copy for player 2: **`D:\Games\swkotor-gog-p2`** (6.02 GB, plain robocopy of
the first). Both have the plugin installed and both inis are windowed.

Both builds share the identical layout, which was verified before trusting any of it:
same `SizeOfImage 0x4C3000`, same section table, and the hardcoded addresses spot-checked
byte for byte in the GOG image — `CSWSCreature` vtable at `0x007470C8`, the `SwapBuffers`
IAT slot at `0x0073D00C`, and the first instructions of `GetPlayerCreature`,
`ExecuteCommandCreateObject`, `ExecuteCommandMoveToPoint` and `StackPushInteger`. The
plugin then hooked the same IAT slot in the live GOG process, so the match holds at
runtime, not just on disk.

- x86 32-bit, ImageBase `0x400000`, **no ASLR**, so every address here is absolute and
  stable. The image spans `0x400000`-`0x8C3000`.
- The GOG entry point is in `.text`, so unlike the Steam build **the bytes on disk are the
  bytes in memory** and static disassembly is trustworthy. That is how the single-instance
  guard in 4.1 was found without attaching a debugger.
- .NET 10 SDK; Visual Studio 2022 Community with MSVC 14.44 x86 toolset; Windows SDK
  10.0.26100. Python 3.12 at `C:\Python312\python.exe`.
- `swkotor.ini` backup in each copy: `swkotor.ini.kotorcoop-backup`. Both inis have
  `FullScreen=0` and `AllowWindowedMode=1`.

### The address database (most valuable single asset)

A complete symbol-labelled Ghidra RE of this exact binary, as a queryable SQLite file:
977 classes, 9,710 functions, 4,720 member offsets, 16 global pointers.

- Source: https://github.com/LaneDibello/Kotor-Patch-Manager -> `AddressDatabases/kotor1_0_3.db`
- Thread: https://deadlystream.com/topic/11948-kotor-1-gog-reverse-engineering/
- Its `game_version` table lists `steam 103` with the same sha256 as the installed exe.

A copy is committed at **`tools/kotor1_0_3.db`** (2.3 MB).

Schema: `classes(class_name,size,vtable)`, `offsets(class_name,member_name,offset)`,
`functions(class_name,function_name,address,calling_convention,param_size_bytes)`,
`global_pointers(pointer_name,address)`, `game_version(...)`.

### Helper scripts

All of these are committed in **`tools/`**. Paths in the examples below assume you run them
from the repo root.

| Script | Purpose |
| --- | --- |
| `q.py` | query the address DB (`schema`, `class`, `offsets`, `func`, `globals`, `sql`) |
| `disasm.py` | disassemble a VA range (GOG only: disk bytes == memory bytes) |
| `xrefs.py` | find call/jmp sites and address literals targeting a VA |
| `demo.ps1` | bring up the whole two-instance co-op session |
| `movewin.ps1` | move / minimise / restore an instance's window |
| `pausestate.ps1` | decode the server pause-reason bitmask |
| `pluginstate.ps1` | read the plugin's shared block: frame rate, phantoms, heartbeat |
| `tooltip.ps1` | detect KOTOR's selected-object name band in a screenshot |
| `wheel.ps1` | scroll the mouse wheel over the game window |
| `creatures.ps1` | enumerate live creatures by vtable, with tag/stats/position |
| `vmtable.ps1` | resolve the live NWScript routine table and dump slots |
| `shot.ps1` | screenshot the game's client area (handles the window offset) |
| `click.ps1` | click at client-relative coordinates |
| `sendkey.ps1` | send scan-code keystrokes via SendInput |
| `walktest.ps1` | drive the character and sample a memory address |
| `writetest.ps1` | write a position and count engine overwrites |
| `exports.ps1` | dump a PE export table |
| `devenv.ps1` | import the MSVC x86 developer environment |
| `checkcands.ps1` | test a list of candidate addresses for a plausible position |
| `crop.ps1` | crop and upscale part of a screenshot (for reading small UI text) |

Note `q.py` and several `.ps1` files have the old scratchpad path hardcoded as a default;
point them at `tools/kotor1_0_3.db` when you first use them.

---

## 4. Verified engine facts

Subsections were added as the research landed, so they are not in numeric order in the
file. Read them in this order; use the numbers to search.

| | |
| --- | --- |
| 4.0 | an unfocused instance pauses, and the plugin has to stop it |
| 4.1 | the single-instance guard, and how it is defeated |
| 4.2 | starting a save without the menu — what was tried, and why it does not work |
| 4.3 | health, damage and death (**current HP is `+0xDC`, not `+0xE0`**) |
| 4.4 | ground items: the exact "is it on the floor" test |
| 4.5 | object ids are the same in both instances |
| 4.6 | the GUI cursor is writable (the proper fix for menu automation) |
| 4.7 | world and quest state — where KOTOR keeps it |
| 4.8 | module transitions destroy everything, and the counters reset |
| 4.9 | reading the game's own data files |
| 4.10 | dialogue silently disarms every enemy, and it collides with combat authority |
| 4.11 | module travel, creature naming, and the death-pose route (built 2026-07-30 evening) |

### 4.0 An unfocused instance DOES pause, and the plugin has to stop it

An unfocused KOTOR pauses itself, which is fatal for co-op because only one of two windows
can hold focus. The plugin's `FocusKeeper` suppresses it, and that suppression is load
bearing — not a safety net.

Be careful measuring this, because it is easy to get a false negative. An early set of
measurements found the pause bitmask reading `0x00` while unfocused and even while
minimised, with the frame counter running at full speed:

```
FOCUSED     frames 162,663 -> 164,008    ~337 fps    mask 0x00
MINIMISED   frames 165,254 -> 166,891    ~346-473 fps mask 0x00
```

and that led to the wrong conclusion. A later reading of an instance genuinely left in the
background for minutes, with no agent attached, showed:

```
mask 0x02  PAUSED: window-not-focused
```

and the plugin logs `cleared focus-loss pause (#1)` during ordinary two-instance runs. So
the pause is real but does not fire on every deactivation — the short, synthetic
alt-tab used in the first test did not reproduce it. **Do not conclude "it never pauses"
from a brief test.** The frame counter is also not a proxy for it: rendering continues
while `CWorldTimer` is paused, so frames keep climbing on a simulation that has stopped.

The mechanism: `Global::DeactivateRenderWindow` (`0x00401D90`), reached from the
`WM_ACTIVATEAPP` handler at `0x00402951`, calls `CServerExoApp::SetPauseState(2, 1)`,
which calls `CWorldTimer::PauseWorldTimer`.

The pause state is a **bitmask of reasons**, one byte at `CServerExoAppInternal + 0x10078`,
reachable as `[[[0x7A39FC]+8]+4]+0x10078`. Bit `0x02` is the focus-loss reason.
`tools\pausestate.ps1` decodes it.

`FocusKeeper` clears bit `0x02` once per frame when the agent asks it to (`--kotor` mode
turns it on automatically), leaving every other pause reason alone so a genuine
player-requested pause still works.

### 4.1 The single-instance guard, and how it is defeated

This was recorded as an environmental blocker requiring a second machine. It is not. The
whole guard is nine instructions at **`0x00404222`**:

```
push 0x0073D75C            ; "swkotor", the mutex name
call [0x0073D050]          ; CreateMutexA
mov  edi, eax
test edi, ebx / je ...     ; NULL handle -> skip
call [0x0073D05C]          ; WaitForSingleObject(handle, 0)
test eax, eax / je ...     ; WAIT_OBJECT_0 -> we own it, carry on
call [0x0073D200]          ; CloseHandle
or   eax, -1               ; return -1  <-- the entire single-instance guard
```

`or eax,-1` is exactly the exit code a second instance produces, which is how it was
identified. Give the second process a different mutex name and it starts normally.

`KotorCoop.Agent`'s `launch --instance 2` does that: `CreateProcess` suspended, rewrite
the ASCII string at `0x0073D75C` from `swkotor` to `kcoopN`, `ResumeThread`. Safe because
that address is referenced from **exactly one place** in `.text` (verified by scanning the
whole section for the literal) — the guard's own `push`. Nothing on disk is modified and
no game code is patched; the replacement must be at most 7 characters so it fits in place.
It refuses to resume unless it reads back the expected `swkotor` first, so a wrong build
is a clean failure rather than a corrupted `.rdata`.

Steam's copy additionally has its own DRM wrapper (`Application load error 5:0000065434`),
which is why the GOG build is the target.

### 4.10 Dialogue silently disarms every enemy — and it collides with combat authority

**This is a design constraint on combat, not a dialogue detail, and it is the reason to
settle dialogue before building any more of combat.**

`CSWSDialog::ClearAllPlayerHostileActions` (`0x005A0940`) walks the area object list
(`area+0x190` / `+0x194`), picks out every creature with `+0xA88 == 0` (not party) and
`GetReputation(player) <= 10` (hostile), and calls `CSWSObject::ClearAllActions(1)` on each
plus the same on its client twin. It is called from `CSWSObject::UpdateDialog`
(`0x004CD5AD`), so it runs on **every dialog update tick**, not once when the conversation
opens.

So while one player is in a conversation, every hostile in *that* instance has its action
queue wiped continuously — a partial combat-off switch. In the peer's instance the same
enemies keep fighting normally.

Now combine that with the host-authoritative enemy model already built (1, 4.3):

- **The host starting a conversation pacifies enemies for both players**, because the
  client mirrors the host's enemy state.
- **A client starting a conversation** desynchronises enemy behaviour for its duration —
  the client sees a frozen fight, the host sees a live one.

Neither is acceptable, and neither is visible in a test where nobody talks during a fight.
Whatever dialogue design is chosen has to answer "what do enemies do while a peer is in
conversation" *before* combat replication is extended.

Related, from the same work: the input lock is `CClientExoAppInternal + 0x9C` (0 world,
1 minigame, 2 full-screen GUI, 3 dialog), entered from exactly one site
(`CGuiInGame::ShowDialogEntry+0x46`, `0x0062B776`) and left in `CGuiInGame::CloseDialog`
(`0x006335A4`). `ProcessInput` at `0x00623895` jumps over the whole movement block when the
class is non-zero. The **world keeps simulating during dialogue** — confirmed by sweeping
every read of the gate — which is precisely why the disarm has to re-fire every tick.

Do not be misled by `IgnoreConversationStartRange` (`CServerExoAppInternal + 0x1B914`): it
has exactly one reader and only suppresses proximity culling of *bark bubbles*. The real
range gate is `GetIsInUseRange` (`0x004F6000`), and failing it queues a move rather than
refusing the conversation.

### 4.11 Module travel, creature naming, and the death-pose route

Three engine surfaces verified and wired up on the evening of 2026-07-30. All three ship
in **SharedBlock version 6** (the block grew to `0x980`; the agent's scan signature now
carries `06`), so both the plugin and the agents must be rebuilt together, and the copies
of `dinput8.dll` in BOTH game directories must be reinstalled before the next live run.

**Module travel** — `CServerExoApp::SetMoveToModulePending` (`0x004AECC0`) is a plain
dword store to `internal+0x10080`; `SetMoveToModuleString` (`0x004AECD0`) and
`SetMoveToModuleStartWaypoint` (`0x004AED30`) assign `CExoString`s at `internal+0x10084`
and `+0x1008C` through `CExoString::operator=` (`0x005E5C50`), which copies with the
engine's own allocator, so stack-local sources are safe. The engine's MainLoop notices
the pending flag and runs the entire transition. Wired as: client agents follow the
host's module (`CoopAgent.PumpTravel` — host-led only so no ping-pong, debounced 15 s,
never into `stunt*` modules, never into the `areaXX` fallback name, not while the local
player is in dialogue, `--no-follow` disables). The plugin's `TravelController` flushes
every spawned phantom and item BEFORE raising the pending flag, because
`StoreCurrentModule` would bake them into the outgoing module (4.8).

**Creature naming** — a creature's first name (the selection band) is a `CExoLocString`
at `stats+0x34` (`CSWSCreature::GetFirstName`, `0x004F8310`, returns exactly that
address). The engine's own rename flow (call site `0x00521B4C`) builds it with
`CExoLocString::AddString(0, &exoString, 0)` (`0x005EA340`, `__thiscall`, args pushed as
`nID`, `CExoString*`, `gender`); the plugin does `ClearLocString` (`0x005E9EA0`) +
`AddString` in place on the field, right after spawn so the client twin is created with
the name already set. The agent writes each peer's name into the remote slot's 24-byte
name field, which had been reserved since version 1 and never used.

**Death pose** — the client's animation sync rejects all three death constants written to
`+0xD4` (trap 21b). The working route: `CSWSCreature::SetAmbientAnimationState`
(`0x004EFEF0`, a byte store to `+0x4C4`) then re-issue idle `0x2710` through the
`SetAnimation` vtable slot, which remaps the idle through the ambient state using the
jump table at `0x004F0F50`. That table, decoded from the exe: states 3/4/14 produce the
death poses `0x2718`/`0x2716`/`0x27AC`; values outside 2..14 fall through to the normal
idle, so **state 1 stands the creature back up**. `PhantomController` drops a phantom
when its player's health is `<= 0` (clearing the action queue first, or a queued move
stands the body back up) and raises it on recovery. The health read in the bridge is now
**signed** — a downed PC's hit points go negative (threshold -10), and the old unsigned
read reported -3 as 65533.

### 4.7 World and quest state — where KOTOR keeps it

`CSWGlobalVariableTable` is **embedded** in `CServerExoAppInternal`:

```
gvt = [[0x7A39FC]+0x08]+0x04 + 0x000100FC
  +0x00000  identifiers[1775], stride 0x18: char name[22], u16 (type<<14 | index)
  +0x0A668  booleans   — bit array, MSB-first within each byte, 900 usable
  +0x0A6D9  numbers    — 500 SIGNED bytes
  +0x0B230  strings[5] / +0x0A8D0 locations[100]
```

Everything needed is **plain memory at fixed offsets**, so the external agent replicates
this with no engine calls — reading and writing the arrays is exactly what the engine's own
setters do (`SetValueBoolean` `0x00529570`, `SetValueNumber` `0x00529680`).

The catalogue is fixed for a session and comes out of the save, so **two instances loading
the same save get identical names AND identical slot indices**. That makes `(type, index)`
a complete network key: no name hashing, and no chance of two variables colliding on one
id. A catalogue digest is computed so a mismatch (different mods) can be detected rather
than silently corrupting quest state.

⚠ **The in-memory index is NOT the on-disk catalogue position.** Measured live against the
Endar Spire save: the four non-zero numbers hold the same values in both places, but at
different slots —

```
GLOBALVARS.res : [128]=1  [193]=1  [269]=5  [297]=11
live memory    : [145]=1  [184]=1  [93]=5   [181]=11
```

and the difference is not a constant offset, so it is a genuinely different ordering rather
than a base adjustment. This does not affect the network key, because both instances derive
the index from their own memory by the same route. It does mean **`tools\globalvars.py`'s
indices cannot be used to identify a live slot** — use it to learn names and to diff two
saves, and read the live catalogue (the `identifiers` array) when you need to map a running
game's slot to a name.

Scale, from the real Endar Spire save: **1185 declared globals** — 809 boolean, 369 number,
5 location, 2 string — of which 4 are non-default. The whole live state is
`113 + 500 = 613` bytes, so diffing it every tick is a `memcmp`.

Two rules that are not optional:

- **Update the shadow copy when applying a remote value.** Otherwise the next scan sees it
  as a fresh local change and broadcasts it back, and the two instances echo forever.
- **Merge upwards, never overwrite.** KOTOR's booleans are latches (`*_DONE`, `*_DEAD`) and
  its numbers are stage counters; the engine itself refuses to lower a journal quest state.
  Taking the maximum makes the merge a join — order-independent and idempotent, so a
  reordered or replayed packet cannot walk a quest backwards.

Not replicated: locations and strings. Every one in a real save is a per-player scratchpad
(`K_LAST_MODULE`, `K_LAST_LOCATION`, `K_STUNT_MODULE`) or cutscene staging. `K_CURRENT_PLANET`
and `k_vis_*` are excluded for the same reason.

The **journal is a separate store**, not derived from globals: `CSWSJournal` at
`partyTable + 0x11C`, entries of stride `0x3C`, and `SetState` (`0x005C5A40`) is the call
that also refreshes the GUI. It needs its own small channel.

### 4.8 Module transitions destroy everything, and the counters reset

`CServerExoAppInternal::UnloadModule` (`0x004B9240`) **destroys the entire
`CGameObjectArray` and allocates a fresh one**, whose constructor resets the id counters to
0 (ascending) and `0x7FFFFFFF` (descending). Two consequences:

- Trap 13 is confirmed with a mechanism: every server object pointer is dangling.
- **Id drift does not carry across a transition** — it is wiped. But bare object ids must
  never be compared across a module boundary; the key is `(moduleName, objectId)`.

**A KOTOR 1 module contains exactly one area**, which removes a whole axis from the design.

Detecting a transition: **do not use area id at `+0x8C`** (7.3 said to) — it is an object id
from a counter that resets, so different modules routinely share one. Use the module *name
string* at `CServerInfo + 0xFC`, reached via `CServerExoAppInternal + 0x10004`. Gate all
work on `loading_module` (`+0x100B4`) being 0 and `server_mode` (`+0x10008`) being 2.

**The serious one:** `StoreCurrentModule` writes the live area back out on module exit, and
`SaveCreatures` only skips creatures with `+0xA88` set (the PC and party). **A mod-spawned
phantom or ground item is therefore serialised into the saved module** and comes back as a
permanent NPC on a revisit, offsetting that instance's ids for the rest of the playthrough.
Phantoms and replicated items must be destroyed before the module is stored. `CSWSArea +
0x2C4` goes 1 about 0.5 s ahead of a door/trigger transition and is the flush window.

Following a peer is cheap and low risk — it is exactly what the shipped `warp` cheat does:
`SetMoveToModuleString` (`0x004AECD0`), optionally `SetMoveToModuleStartWaypoint`
(`0x004AED30`), then `SetMoveToModulePending(1)` (`0x004AECC0`). The engine's own MainLoop
does the rest with every invariant intact.

### 4.5 Object ids are the same in both instances (verified)

Two instances loaded from the same save assign **identical object ids** to everything the
module places. Measured live, heap addresses differ but ids match exactly:

```
              instance 1        instance 2
end_sith2     id 0x14           id 0x14
end_sith3     id 0x17           id 0x17
end_trask     id 0x127          id 0x127
```

The mechanism: `CGameObjectArray::AddInternalObject` allocates from a monotonic counter
(characters count *down* from `0x7FFFFFFF`, which is why the PC is that value), and
`CSWSArea::LoadCreatures` walks the GIT "Creature List" in file order. Same save, same
order, same ids.

**This is what makes combat replication cheap** — an enemy needs no cross-instance key
invented for it, its object id already is one.

The caveat is drift: anything either instance creates that the other does not shifts the
counter for everything created afterwards (phantoms, replicated items, encounter spawns).
So build any id mapping at area load, before mod-side spawns, and rebuild it after a
transition.

### 4.9 Reading the game's own data files

`tools\kotorfiles.py` (stdlib only, read-only) decodes KEY/BIF, RIM, ERF/MOD/SAV, GFF,
2DA and TLK, with a CLI: `key`, `list`, `gff`, `2da`, `extract`, `tlk`, `creatures`,
`objectids`. Exercised over 246 RIMs / 7,725 GFF resources with no failures.

Two findings from it that change decisions:

**The save records object ids explicitly.** `SAVEGAME.sav` is an ERF containing
`<MODULE>.sav`, whose `.git` gives every instance an `ObjectId` field — creatures, doors,
placeables, triggers, waypoints and sounds. And `Module.ifo` carries `Mod_NextObjId0`, the
id the next runtime-created object will get. So the id map can be built **offline and
exactly**, rather than hoping ids coincide. (`Mod_NextObjId0` = 295 = `0x127` in this save,
which is precisely the id Trask was observed to take, because he is re-instantiated from
`AVAILNPC0.utc` on load.) Read the field; do **not** infer order from the save GIT, which
is written sorted by X.

**The phantom walk speed of 5.4 was wrong — and also dead code.** The constant fed a
`maxDistance` local that was computed and never read, so it bounded nothing; a phantom's
speed comes entirely from the engine, because movement is an `ActionMoveToLocation` the AI
executes. Both are now deleted. Recorded in case speed ever does need setting: it is **not**
`creaturespeed.2da`. `CSWSCreature::GetWalkRate` (`0x004F1B20`) and `GetRunRate`
(`0x004F1BE0`) read the creature's appearance record, i.e. `appearance.2da`
`walkdist` / `rundist` — for the PC's row 114 that is 1.6 walk / 3.96 run.

Also worth knowing: `baseitems.2da`'s `container` column is dead (0 for all 92 rows) — a
container is marked by `HasInventory` on the *placeable* blueprint. And `stacking` is 99 for
nearly every item type, so the destroy-on-pickup stack-merge case from 4.4 is the **normal**
case, not an edge case.

### 4.6 The GUI cursor is writable (fixes menu automation properly)

Trap 19 records that KOTOR's menu cursor is accumulated from DirectInput deltas and cannot
be positioned by moving the OS pointer. It can, however, be **written directly**, which is
the clean fix and is not yet implemented:

```
appInternal = *(*(void**)(*(void**)0x007A39FC + 0x04) + 0x04)   // CClientExoAppInternal
  +0x3C8  int mouse_x     authoritative, clamped to SCREEN_WIDTH
  +0x3CC  int mouse_y     authoritative, clamped to SCREEN_HEIGHT
GUI_MANAGER_PTR 0x007A39F4 -> CSWGuiManager
  +0x00/+0x04  mouse x/y  (mirror; overwritten from +0x3C8 on every input event)
  +0x08        control currently under the cursor  <-- read back to VERIFY before clicking
```

`CClientExoAppInternal::RestoreMousePositionTo` (`0x005F4EA0`) writes the authoritative
pair; `CSWGuiManager::HandleMouseMove` (`0x0040C1E0`) updates hover state. Call both, then
inject only the button down/up — never mouse motion, which would clobber it.

Why relative stepping could never work: `CExoInputInternal::ScaledValue` divides the raw
axis delta by 100 and **clamps to ±1.0**, so a delta of −5000 and one of −100 are
identical.

Better still, the menu can be driven without a cursor at all by firing a control's event
directly — `CSWGuiControl::HandleInputEvent` is vtable slot `+0x3C`, and the main menu's
Load Game button is at `CSWGuiMainMenu + 0x5B4` bound to event `0x27`
(`0x0067B1A0 OnLoadSaveGame`). `CSWGuiMainMenu*` lives at `appInternal + 0x280` and is null
outside the main menu.

### 4.2 Starting a save without the menu: what was tried and why it does not work yet

Goal: launch straight into a save, no menu at all. **Not achieved.** The findings below are
the expensive part; do not redo them.

The GUI's load path ends at `CClientExoApp::SendLoadGameRequest` (`0x005ED680`), which
writes `WriteDWORD(saveId) + WriteCExoString + WriteCExoString` and is handled server-side
by `CSWSMessage::HandlePlayerToServerModuleMessage` (`0x00524800`) calling
`CServerExoApp::LoadGame(saveId, CExoString*, CExoString*, player)` (`0x004AE6F0`).
`CGuiInGame::DoQuickLoad` (`0x00633C50`) calls the same request with NULL for the second
string, so the strings are not all mandatory.

Reproducing it from the plugin got as far as "accepted and ignored":

| Step | Result |
| --- | --- |
| `SendLoadGameRequest(id, NULL, NULL)` from the menu | **crashes the process** |
| ... because **`APP_MANAGER+0x08` (the server) is NULL at the main menu** | measured directly |
| Add `CAppManager::CreateServer` (`0x00401380`) first | no crash; request returns success |
| Add `CServerExoApp::StartServices` (`0x004AE8F0`) | still nothing loads |
| Add `SetLoadFromSaveGame` (`0x004AF060`) + `SetLoadingSaveGame` (`0x005EDFC0`) | still nothing loads |
| Real empty `CExoString`s instead of NULL, and the save directory name | still nothing loads |

So the message is accepted and never serviced. `CSWGuiSaveLoad::LoadGame` (`0x006CB0E0`)
does considerably more around the request - GUI state transitions, input class changes,
`CGuiInGame` teardown - and one of those is what actually drives the transition. Finishing
this means replaying that sequence, not just sending the message.

The command line does not help either: the only switches `Global::checkCommandLineArgs`
(`0x00401700`) understands are `-ecf`, `-epf` and `-epr`, all capture/profiling.

What ships instead is menu automation (`agent launch --load-save <n>`), which needs no
human input but does briefly show the menu. Its own limitation is in 7.2.

### Object model

`CSWSCreature` size `0xAC8`, **vtable `0x007470C8`**. Scanning private committed memory for
that dword finds every creature with no false positives. `CSWSObject` (size `0x228`) is
embedded at offset 0.

| Offset | Field |
| --- | --- |
| `+0x004` | object id (player is `0x7FFFFFFF`) |
| `+0x008` | object type (creature = 5) |
| `+0x018` | tag, `CExoString` = `{char* @0, uint len @4}` |
| `+0x08C` | area id |
| `+0x090` | **position**, 3 floats |
| `+0x09C` | orientation, 3 floats (direction vector, not an angle) |
| `+0x0D4` | animation |
| `+0x0E0` | hit points (NOT the Vitality the UI shows) |
| `+0x0E8` | **commandable** (ClearAllActions no-ops unless 1; AI needs 1 to run the queue) |
| `+0x0FC` | action queue, `CExoLinkedList<CSWSObjectActionNode>` |
| `+0x1F8` | visible-to-player (engine's walk driver skips off-screen creatures) |
| `+0x1FC` | dirty-flag bit array (0 anim, 1 pos, 2 orient) |
| `+0x224` | client twin `CSWCObject*` |
| `+0x4C0` | party follow info (non-null on party members) |
| `+0x9F0` | bit 1 (`& 2`) = movement permitted; clear means no walk animation |
| `+0xA50` | appearance_info, `0x24` bytes (copy the PC's look with a memcpy of this) |
| `+0xA60` | appearance type, u16, `appearance.2da` row (PC is 174 in the current save) |
| `+0xA74` | `CSWSCreatureStats*` |
| `+0xA8C` | movement state (1 idle, 2 requested, 3 follow, -1 walking) |
| `+0xA98` | run flag (0 walk, nonzero run) |

`CSWSCreatureStats`: experience `+0x068`, **is_pc `+0x06C`**, faction `+0x078`,
strength `+0x0E9` (then dex/con/int/wis/cha, each followed by its modifier byte).

`CSWCObject` (client twin, size `0xFC`): position `+0x024`, orientation `+0x030`.

Animations: idle `0x2710`, walk `0x2712`, run `0x2714`, set through **vtable slot `+0x7C`**.

### The NWScript VM (how to call engine functions)

```
[0x007A3A00] -> CVirtualMachine*
             -> *that -> CVirtualMachineInternal*
             -> +0x3D4 -> implementer (vtable reads back 0x007450E4)
             -> +0x0C  -> routine table, 772 dwords indexed by routine number
```

| What | Value |
| --- | --- |
| `table[243]` | `0x0052F820` `ExecuteCommandCreateObject` |
| `table[21]` and `table[382]` | `0x0053FE00` `ExecuteCommandMoveToPoint` (ActionMoveToLocation) |
| implementer `+0x04` | has_caller_object |
| implementer `+0x08` | caller_object_id (the VM's OBJECT_SELF) |
| internal `+0x1AC` | stack pointer. **Reads 158 on an idle game, NOT 0** |
| internal `+0x1C4` | call_stack_size. **0 when idle; this is the real idle test** |

Dispatch is `__thiscall int(implementer, routineNumber, paramCount)`. Arguments go on the
VM stack, pushed in **reverse declaration order**.

| Helper | Address |
| --- | --- |
| `StackPushInteger` | `0x005D1010` |
| `StackPushString` | `0x005D1090` (takes `CExoString*`) |
| `StackPushEngineStructure` | `0x005D10B0` (`nType`, `void*`); location type = **2** |
| `StackPopObject` | `0x005D10C0` |

`CScriptLocation` is `0x18` bytes: `{Vector position; Vector orientation}` where orientation
is a normalised direction vector. Both the string and the engine structure are **copied** by
the engine, so stack locals are fine and nothing needs freeing.

Blueprint used for phantoms: **`n_commm`** (generic male commoner, appearance 17, faction 5
neutral, no conversation, no plot flag, stock AI). Avoid `p_*` party templates: henchman AI,
a conversation and `NoPermDeath`.

No post-registration is needed. `AddInternalObject` runs inside `CSWSObject::Constructor`
(triggered by passing id `0x7F000000`), `AddObjectToArea` runs inside
`CSWSCreature::AddToArea`, and the renderable client twin is created automatically by the
engine's own object-update pipeline.

### Frame hook

Patch the single IAT dword at **`0x0073D00C`** (`GDI32!SwapBuffers`, `__stdcall BOOL(HDC)`).
Only two code references exist, both once per frame on the initial thread, and the swap is
the last thing in the frame after both `CClientExoApp::MainLoop` (`0x005ED7D0`) and
`CServerExoApp::MainLoop` (`0x004AE860`). 4-byte aligned so the store is atomic. Install
from `DirectInput8Create`, which is verified main-thread.

The loop runs at roughly **317 Hz**, not the frame rate, so per-frame work must be
allocation-free and rate-limited off a wall clock.

### Other useful addresses

| Address | Function |
| --- | --- |
| `0x004B2820` | `CServerExoApp::GetPlayerCreature` |
| `0x004AE750` | `CServerExoApp::GetGameObject` |
| `0x004AE6B0` | `CServerExoApp::GetModule` |
| `0x004FA100` | `CSWSCreature::AddToArea` |
| `0x0050DFD0` | `CSWSArea::AddObjectToArea` |
| `0x004CD1A0` | `CSWSObject::SetPosition` (raises the position dirty bit) |
| `0x004CD170` | `CSWSObject::SetOrientation` |
| `0x004CCD80` | `CSWSObject::ClearAllActions` |
| `0x004EDBA0` | `CSWSCreature::ForceMoveToPoint` (proper synchronous teleport) |
| `0x00516630` | `CSWSCreature::WalkUpdateLocationDistance` (see trap 4) |
| `0x004AF3D0` | `CServerAIMaster::RemoveObject` (writing -1 to ai_level does NOT work) |
| `0x004F8B60` | `CSWSCreature::AddMoveToPointAction` (17 args, undecoded bitmask) |
| `0x00547F60` | `ExecuteCommandCreateItemOnFloor` (used for shared ground items) |
| `0x0052FF20` | `ExecuteCommandDestroyObject` (used to despawn them) |
| `0x005D1030` | `CVirtualMachine::StackPushFloat` |
| `0x005D10D0` | `CVirtualMachine::StackPushObject` |

Globals: `VIRTUAL_MACHINE_PTR 0x007A3A00`, `APP_MANAGER_PTR 0x007A39FC`,
`AURORA_PTR 0x007A39F8`, `RULES_PTR 0x007A3A28`.

### 4.3 Health, damage and death

**`+0xE0` is MAX hit points. Current is `+0xDC`.** That off-by-four is the whole reason the
dashboard used to read `hp 0` while the character sheet said 25/28. Verified two ways:
`CSWSObject::SetCurrentHitPoints` (`0x004CC280`) writes `+0xDC`, and
`ExecuteCommandSetMaxHitPoints` writes `+0xE0` and *then* calls it.

| Offset | Field |
| --- | --- |
| `+0x0DC` | **current hit points** (u16 meaningful) |
| `+0x0E0` | max hit points, as stored (u16) |
| `+0x0E4` | temporary hit points |
| `+0x0F8` | `plot` — blocks **all damage**, not just death |
| `+0x200` | `min1hp` — damage lands, health floors at 1, death effect early-outs |
| `+0x4E0` | in combat (0/1) |
| `+0x504` | attack target as NWScript reports it |
| `+0x9C8` | `CSWSCombatRound*` |
| `+0x9C8` → `+0x9CC` | **the persistent target the retarget logic actually reads** |
| `+0x9D4` | mirror of `stats.is_pc`; switches the death threshold from 0 to **-10** |
| `+0x920` / `+0x924` | perception list and count; an enemy must perceive you to target you |

**Writing `+0xDC` is enough to move the health bar.** There is no dirty flag for health:
`CSWSMessage::ComputeUpdateRequired` diffs live health against a per-player snapshot every
update. That also resolves part of the old "unexplained" note in 7.6.

All damage funnels through one path, each link with exactly one caller:
`CSWSEffectListHandler::OnApplyDamage` (`0x004DFA40`) → `CSWSCreature::DoDamage`
(`0x004F3830`) → `CSWSObject::DoDamage` (`0x004CCF80`).

**Never replicate damage numbers.** `DoDamage` applies a per-difficulty multiplier,
temporary hit points, and damage reduction/resistance/immunity before subtracting, all
per-instance. Replaying "took 7 damage" on the far side does not produce 7. Replicate
absolute health.

There is **no `EffectDeath` routine in KOTOR** (NWN has one; this does not). To kill
deterministically, apply an instant lethal damage effect — that runs the real death path
including animation, corpse, XP and the OnDeath script:

```
EffectDamage        routine 79   handler 0x00531AE0   (amount clamped to 0..10000)
ApplyEffectToObject routine 220  handler 0x0052E570
```

Death itself is `GetDead`: health `<= 0`, or `<= -10` for a party member.

⚠ **Routine-table trap:** some handlers occupy *two* slots — `0x00539610` is both
`GetCurrentHitPoints` (49) and `GetMaxHitPoints` (50), exactly as `0x0053FE00` is both 21
and 382. Scanning the table for a handler address and taking the first hit can therefore
give you the wrong routine.

### 4.4 Ground items: the exact "is it on the floor" test

**An item is on the floor if and only if `CSWSItem + 0x268` (possessor) is `0x7F000000`.**
This is the engine's own predicate: `CSWSCreature::AIActionPickUpItem` gates the entire
pickup on it at `0x005174E9`, and `AcquireItem` re-validates it.

Pickup **moves** the item — same allocation, same object id — and
`CSWSItem::RemoveFromArea` sets area id to `0x7F000000` and position to `(0,0,0)`.
So the old area-id probe did work, but only as a proxy; it also fires on a module unload
and inside the brief window in `AddToArea` where an item is legitimately area-less.

**The exception that matters:** if the item merges into a stack the picker already carries
(credits, medpacs, grenades, ammo), `CItemRepository::AddItem` **destroys** the object and
its id becomes permanently unresolvable. That is still a pickup, so both outcomes must be
handled. Object ids are never recycled — the counter only increments.

`+0x26C` (`item_repo`) is **not** related: it is the repository an item owns as a
*container*. Using it as a pickup test would be completely wrong.

To hand an item to a specific creature rather than destroying it everywhere:
`CServerExoApp::CreatureAcquireItem` (`0x004AEB40`) takes an object id and a resref and is
the direct counterpart of the `CreateItemOnFloor` already in use.

### Ground items

⚠ **`GetGameObject` hands back the `CSWSObject` inside an item, not the `CSWSItem` base**
(verified live — see 7.1). Every offset below that is relative to the `CSWSItem` base is
therefore `0x10` further along than what the lookup returns. Check where the object id
lands — `+0x14` means you have the base, `+0x04` means you have the inner `CSWSObject` —
before indexing anything.

`CSWSItem` size `0x298`, **vtable `0x00748DA0`**. Unlike `CSWSCreature` it does NOT embed
`CSWSObject` at offset 0: a `CSWItem` base occupies `0x00`-`0x0F` and **`CSWSObject`
starts at `+0x10`**. So relative to the `CSWSItem` pointer, object id is `+0x14`, tag
`+0x28`, area id `+0x9C`, position `+0xA0`, client twin `+0x234`. Getting this wrong reads
plausible-looking garbage, so `Odyssey.ResolveServerObject` tests where the object id
actually lands rather than assuming either layout.

Two NWScript routines do the work. Their **routine numbers are not hardcoded** — unlike
`CreateObject` (243) and `ActionMoveToLocation` (21), which were verified live, these were
never confirmed, so the plugin scans the 772-entry routine table for the handler address
the symbol database names and uses the index it finds:

```
CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation)   -> 0x00547F60
DestroyObject(object oDestroy, float fDelay, int bNoFade, float fDelayUntilFade) -> 0x0052FF20
```

An item created this way gets a client twin automatically, exactly as creatures do, so it
renders and is clickable with no extra work. Verified live: `client_twin 0x1AEE9DF0`,
`visible 1`.

---

## 5. How to run it

### The two-player demo, one command

```powershell
dotnet build D:\Development\kotor-coop\KotorCoop.slnx
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
# and copy the same dinput8.dll into D:\Games\swkotor-gog-p2

pwsh tools\demo.ps1          # launch both, load saves, connect the agents
pwsh tools\demo.ps1 -Save 2  # a different save slot (1 = top of the list)
pwsh tools\demo.ps1 -Stop    # shut it all down
```

Instance 1 loads its save automatically. Instance 2 currently does not (7.2), so the
script prompts once: click Load Game / Game 1 / Load in that window and press Enter.
Everything else, including both agents, is automatic.

Drop an item from either side (the agents watch these paths):

```powershell
Set-Content "$env:TEMP\kotorcoop-demo\drop1.txt" g_w_lghtsbr01
Set-Content "$env:TEMP\kotorcoop-demo\drop2.txt" g_i_medeqpmnt01
```

Interactively, the agent dashboard drops on **D**. The drop file exists because a
redirected or scripted agent has no console to read a keystroke from.

### The pieces by hand

```powershell
dotnet test D:\Development\kotor-coop\KotorCoop.slnx        # 82 tests

$agent = "src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe"

# instance 1 normally; instance 2 needs the renamed mutex
& $agent launch --windowed --load-save 1 --game-path 'C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe'
& $agent launch --instance 2 --game-path 'D:\Games\swkotor-gog-p2\swkotor.exe'

# drive an instance that is already sitting at the menu into a save
& $agent load --pid <pid> --load-save 1

# one agent per instance. --pid, NOT --process: two swkotor.exe are running
& $agent run --pid <pid1> --kotor --host --port 27140 --name Lanik
& $agent run --pid <pid2> --kotor --join 127.0.0.1:27140 --name Malak
```

Mock-only co-op, no game needed:

```powershell
$mock = "src\KotorCoop.MockGame\bin\Debug\net10.0-windows\KotorCoop.MockGame.exe"
& $mock --host --name Revan
& $mock --join 127.0.0.1 --name Malak
```

### Diagnostics

`agent scan --pid <n>` gives an interactive REPL with `creatures`, `int <value>`,
`find <aob>`, `near <addr>`, `dump <addr> [n]`, `ptr <addr>`, `snap`, `diff changed|same`.
Each plugin logs to `kotorcoop-plugin.log` beside its own `swkotor.exe`, which is the
reason for two separate game folders rather than launching one twice.

Every tool in `tools/` takes `-ProcId <n>` to pick between the two instances.
`tools\creatures.ps1 -ProcId <n>` is the quickest way to see whether a phantom exists and
where it is.

---

## 6. Traps, all of which cost real time

1. **`FullScreen=0` alone is silently ignored.** KOTOR 1 needs `AllowWindowedMode=1` too.
   Without it the game takes an exclusive fullscreen display mode, and then GDI screen
   capture returns a **stale frame that never updates** while DirectInput **ignores
   injected input**. The tooling still appears to run: three full discovery runs were
   burned "successfully" walking a character that never moved. Verify by checking the
   desktop resolution and that the client area has a non-zero screen origin like `(8,31)`.
2. **The Windows firewall prompt steals focus.** The first UDP bind raises a dialog hosted
   by `PickerHost.exe`, which takes foreground so injected input stops reaching the game.
   Its window has an **empty title**, so closing dialogs by title misses it. Kill
   `PickerHost.exe` specifically.
3. **A position write to `+0x90` sticks only if nothing is driving that creature.**
   Measured overwrites over ~5 s: idle player 0, idle NPC 0, party member running follow-AI
   **319** (~64/s), client twin `+0x24` **252**. It is not client-versus-server; it is who
   owns the movement.
4. **`WalkUpdateLocationDistance` does not work standalone.** Measured: returns 1, never
   writes its `newPos` argument, creature never advances. Its third argument *is* written,
   with the unit direction, so the documented argument roles are wrong. It is an internal
   helper of `AIActionMoveToPoint` and depends on state the action sets up. Use the action
   queue instead.
5. **Setting commandable (`+0xE8`) to 0 stops the AI running the action queue**, so a
   creature made non-commandable ignores every move action. Counter-intuitive if you set it
   to "stop the AI interfering".
6. **The VM stack pointer is 158 on an idle game, not 0.** Guarding on `sp != 0` refused
   three spawn attempts out of four. Only `call_stack_size == 0` is a valid idle test;
   check stack neutrality afterwards instead.
7. **A 12-byte magic signature was not unique enough.** The scanner takes the first hit, hit
   a false match at a lower address, and the agent silently published garbage
   `(0, 0, 2.4e29)` to every peer. The signature is now 16 bytes (magic + version + guard
   `0xC0FFEE01`) and a mismatch is fatal rather than a warning.
8. **The module filter hides every peer when the two sides name areas differently.** KOTOR
   reports `area1`, the mock reports `end_m01aa`. Use `--no-module-filter` when bridging
   different games.
9. **Never sweep your own address space.** Full sweeps against a separate process are fine
   (120 MB in 0.1 s on live KOTOR), but the same code against the calling process
   intermittently kills the CLR with `0x80131506` inside `coreclr.dll`. Snapshot buffers
   also live inside the space being captured, so each capture swallows the previous one.
   `TakeSnapshot`/`FindPointersTo` grew `minAddress`/`maxAddress` for this.
10. **Differential float scanning loses badly on KOTOR.** ~200 MB of the process is graphics
    memory, and the translation rows of per-frame transform matrices are indistinguishable
    from a position by any static test. Exact-integer scanning wins: `int 3155` gave 8 hits
    in 0.4 s versus millions of float candidates that never converged.
11. **NativeAOT does support `win-x86`**, which is why the plugin is C#. It shells out to
    `link.exe` and `vswhere.exe`, so it only builds inside an MSVC developer environment.
12. **A leftover `KotorCoop.Agent.exe` locks its own binary** and the next build fails with
    MSB3027. Kill stray agent/mock processes before rebuilding.
13. **An area transition destroys and recreates every object**, so all addresses change and
    the plugin's phantom list goes stale. Untested.
14. **`SetCursorPos` is invisible to KOTOR.** The game reads the mouse through DirectInput,
    which watches the injected-input stream; `SetCursorPos` moves the pointer without
    generating that input, so the game never registers a hover and no object name ever
    appears. A grid sweep looking for tooltips found nothing at all until the move was
    reissued through `SendInput` with `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE`. Clicks
    worked the whole time, which makes this easy to misread as "the game ignores hovers".
    `tools\click.ps1` now moves via `SendInput` and takes `-HoverOnly`.
15. **The name band under an object is a *selection* label, not a hover tooltip.** It stays
    on the last selected object regardless of where the cursor goes, so using it as a
    "what is under the cursor" oracle silently reports the same object for every probe
    position. `tools\tooltip.ps1` detects the band; just do not read it as hover state.
16. **A phantom shows no name band at all**, because the spawned commoner has no first or
    last name set. Do not use "no tooltip" as evidence that an object is absent.
17. **The plugin outlives the agent.** It is loaded by the game, so a restarted agent
    inherits whatever the previous session spawned. Without an explicit reset those items
    stay on the floor forever, owned by a slot nobody tracks; `KotorGameBridge` now clears
    all item slots the moment it finds the plugin block.
18. **Both game copies must be separate folders**, even though the mutex rename means one
    folder would technically run twice. Each plugin writes `kotorcoop-plugin.log` beside
    its own executable, and two instances sharing a folder interleave into one file.
19. **KOTOR's menu cursor is not the OS cursor.** It is accumulated from DirectInput
    relative deltas and clamped to the client area, so putting the OS pointer on a button
    does not put the game's cursor there. Verified directly: a click landed on exactly the
    right screen pixel for "Load Game" while "New Game" stayed highlighted. This is why
    menu automation works for a window at its launch position and fails for one that has
    been moved. See 7.2.
20. **`SendInput` silently does nothing if `cbSize` is wrong.** The x64 `INPUT` struct is
    40 bytes; a P/Invoke declaration that marshals to any other size makes every call a
    no-op that returns 0, and the cursor simply never moves. An hour went into "the game
    ignores injected input" that was really a struct with two extra padding fields.
21b. **The client silently drops eight animation constants.**
    `CSWCCreature::ClientServerSyncHandler_Animation` (`0x0060C100`) opens with an explicit
    reject list covering attack (`0x2719`, `0x277D`), parry (`0x271C`), dodge (`0x271B`),
    damage (`0x271E`) and the three death poses (`0x2716`, `0x2718`, `0x27AC`). Writing any
    of them to `+0xD4` and raising the dirty bit does **nothing at all** — the write
    succeeds and no animation plays. Death poses must go through
    `SetAmbientAnimationState` (`0x004EFEF0`) followed by `SetAnimation(0x2710)`; a real
    weapon swing needs `CutsceneAttack` (routine 503).
21c. **Verifying a field is not verifying a behaviour.** The phantom ownership guard set
    exactly the values it was supposed to, was confirmed live by reading them back, and was
    still badly wrong (see 7.4). Nothing was in combat at the time, so the failure mode
    could not appear. When a change is about how the engine will *behave*, the test has to
    provoke the behaviour.
21. **A one-element byte array holding 0 is falsy in PowerShell.** `if (-not $b)` on the
    result of a successful 1-byte read of an unpaused game reports "read failed". Compare
    against `$null` explicitly.

---

## 7. Next steps, in priority order

### 7.0 Staging a real fight on demand is harder than it sounds

Worth knowing before anyone repeats it. The Endar Spire's `end_sith2` / `end_sith3` sit at
(46.78,33.27) and (45.18,32.45) and are **completely inert**: teleporting the player next
to them produced no aggro over 15 s, and clicking one selected it (the "SITH SOLDIER" name
band appeared) without starting a fight. They appear to be scripted placements waiting on a
trigger rather than live encounters.

That is why the verified combat run in section 1 applied damage by writing memory. It
demonstrates the replication, not the fighting. To get a genuine fight for the next round,
the options are: walk to an encounter that is actually armed, fire the trigger that arms
these, or command an attack through the engine — `ActionAttack` is NWScript routine 37,
handler `0x0052E7F0`, and the plugin already has the machinery to dispatch a routine with
OBJECT_SELF redirected (that is how `ActionMoveToLocation` and `ActionPickUpItem` work).
The last of those is the cheapest and is the recommended next step.

`tools\sethp.ps1 -ProcId <n> -ObjectId 0x14 [-Set <hp>]` reads or writes a creature's
current health by object id, which is how the run above was staged.

### 7.1 Item pickup — VERIFIED end to end

`ItemController.ProbeOneSlot` now uses the engine's own predicate — an item is on the
floor exactly when `CSWSItem+0x268` (possessor) is `0x7F000000` — instead of comparing area
ids. See 4.4.

The first live run of that change **failed usefully**. The client spawned its copy of a
dropped lightsaber and 36 ms later reported it gone:

```
15:38:55.272  item slot 0: spawned item 0x10001 as object 0x131 at (18.45, 21.79, -1.27)
15:38:55.308  item slot 0: item 0x10001 (object 0x131) gone (destroyed, or merged into a stack)
```

and because a local vanish is announced to the session, that false positive propagated a
removal and deleted the item in *every* instance — the host's dashboard dropped to
`ground items (0)` immediately after reporting the drop.

It took **three attempts** to fix, and the reason is worth recording because it is a trap
anyone touching item code will hit.

`CServerExoApp::GetGameObject` returns, for an item, a pointer to the **`CSWSObject`
embedded inside** the `CSWSItem` — not the `CSWSItem` base. Instrumenting the probe proved
it outright:

```
item slot 0: spawned item 0x10001 as object 0x12E at (18.45, 21.79, -1.27), area 0x1
item slot 0: item 0x10001 (object 0x12E) gone (ptr 0x19153B88, id@+0x14 0x0)
```

The pointer is perfectly good; `+0x14` reads 0 because the object id is at `+0x04` of what
was returned. Every field indexed from the `CSWSItem` base — including the possessor at
`+0x268` — is therefore off by `0x10`.

This is also why the *original* area-id probe worked: `Odyssey.ResolveServerObject` tests
`+0x04` first, that test matches for items too, and it returns the pointer unchanged — so
area id at `+0x8C` landed correctly by accident of the same shift.

The probe now accepts **either** shape (object id at `+0x14` = `CSWSItem` base, at `+0x04`
= the `CSWSObject` inside it, in which case subtract `0x10`) and sanity-checks the vtable
before trusting a field `0x268` bytes in. There is also a three-probe grace period after a
spawn.

The two earlier attempts failed because each fixed a real-looking problem that was not the
cause: first an over-strict vtable check applied to the wrong pointer, then a timing guard.
Guessing cost two full rebuild-and-relaunch cycles; **one diagnostic log line found it
immediately**.

**Verified working**, run of 2026-07-30, pid 57956 with a mock peer:

```
item slot 0: spawned item 0x10001 as object 0x130 at (18.45, 21.79, -1.27), area 0x1
   ... item stays on the floor, dashboard reads "ground items (1)" ...
pickup: player 0x7FFFFFFF told to take object 0x130
item slot 0: item 0x10001 (object 0x130) taken by creature 0x7FFFFFFF
   ... dashboard reads "ground items (0)" ...
```

So the whole chain holds: spawn, persist without a false positive, a real engine
`ActionPickUpItem`, detection through the engine's own possessor predicate — which also
names the creature that took it — and removal propagated to the session. Trigger a pickup
with `P` on the dashboard, or by writing `pickup` into the drop file.

### 7.1b Older note — what the pickup chain has to prove

Drop and despawn are both proven. What has **not** been observed is the third leg: a
player picking an item up locally, and it disappearing in the other game.

The mechanism is written. `ItemController.ProbeOneSlot` checks one live slot every 20
frames and treats the item as gone when its object id no longer resolves, or resolves to
an object no longer in the area it was spawned into — which is what a pickup does, since
pickup moves the item into an inventory rather than destroying it. It then reports
`ItemStateVanished`, the bridge picks that up in `CollectVanishedItems`, and the session
broadcasts a `Remove` op that makes every other instance destroy its copy. The destroy
half of that chain is proven independently (see 1).

What is missing is one confirming run. It was not obtained because **the automated harness
could not land a mouse click on the item**: KOTOR needs a pixel-accurate click on a small
floor model, the camera sits close behind the character so the item is usually occluded,
and the two obvious oracles both misled (traps 14-16). This is about thirty seconds of
work for a human with a mouse:

1. `pwsh tools\demo.ps1`, load a save in both.
2. `Set-Content "$env:TEMP\kotorcoop-demo\drop1.txt" g_w_lghtsbr01`.
3. In instance 2, walk onto the item and click it.
4. Expect `item slot 0: ... left the floor - picked up locally` in instance 2's plugin log,
   then `destroyed` in instance 1's, and `ground items (0)` in both agent readouts.

If the probe turns out not to fire, the likely cause is the area-id test: confirm what
`+0x9C` (`CSWSItem` -> `CSWSObject.area_id`) reads once the item is in an inventory. If it
keeps its old area id, switch the test to the item's parent/repository pointer
(`CSWSItem.item_repo` at `+0x26C`) becoming non-null instead.

### 7.2 Auto-load is reliable for one instance and not for two

`agent launch --load-save <n>` drives the main menu into a save with no human input, and
`agent load --pid <n> --load-save <n>` does the same to an instance already running. It
waits for `GamePhase.MainMenu` before clicking (see `GameLifecycle`) and for
`GamePhase.InWorld` after, so it never guesses at sleeps.

**It works every time for the first instance and never for the second.** The cause is
identified and the fix is calibration work:

KOTOR's menus do not read the OS pointer. The game accumulates DirectInput **relative**
deltas into a cursor of its own, clamped to the client area. Moving the OS pointer onto
"Load Game" therefore highlights whatever the game's own cursor happens to be over -
directly observed: the click landed at the right screen pixel and "New Game" stayed
highlighted. It happens to work for a window at the launch position and fails for one
placed beside it.

Pinning the game cursor by sending a large negative relative move (clamping it to the
top-left) and then stepping out by the wanted offset **does** move it, so the approach is
sound, but it is not one unit per pixel: the game applies a mouse sensitivity multiplier.
Calibrating that - move a known delta, find the cursor, solve for the scale, ideally
reading sensitivity out of `swkotor.ini` - is the remaining work. Keyboard navigation of
the menu is the other candidate and was not tried properly.

Until then `tools\demo.ps1` loads instance 1 automatically and prompts once for instance 2.

### 7.3 Finish the phantom

- **Facing on arrival.** The engine picks it from the path direction; arbitrary once stopped.
  Set orientation explicitly after the move completes.
- ~~**Appearance.**~~ Done. The remote player's `appearance.2da` row now travels in
  `PlayerState` and is written to the phantom's `+0xA60`; the engine's own appearance
  update pushes it to the render side next frame. Verified: phantoms read row 114,
  matching the PC, not the `n_commm` blueprint's 17. Note this is a **single u16 write**,
  not the `0x24`-byte memcpy of `+0xA50` the old note suggested — copying the whole
  struct would also drag across the other player's equipped-item object ids, which are
  meaningless in this process.
- **Multi-peer.** Still only ever tested with one phantom. Spawn is rate-limited to one
  per 30 frames, capped at 8.
- **Module transitions.** Detect an area change (area id at `+0x8C`, or the module
  pointer) and re-spawn phantoms rather than driving stale pointers. The same applies to
  ground items, which currently keep their spawn-time area id.
- **Name plates.** Phantoms have no name at all, so KOTOR shows no label (trap 16). Set
  the creature's first name to the peer's name.
- **Goal-resend threshold.** The phantom only re-paths when the peer has moved more than
  2.5 units, so it settles up to 2.5 units from the true position — visible as a slight
  lag when a peer stops. Worth lowering once it is clear how much churn the pathfinder
  tolerates.

### 7.4 Ownership, and what "seamless co-op" needs next

The target is NWN / Souls-seamless: each player drives exactly one character and cannot
command anyone else's.

**What the architecture already guarantees.** Two full games are bridged, not one game
with two inputs, so a peer physically cannot control another peer's PC - different
processes, different saves. A phantom is not a party member, and KOTOR only lets you
command party members, so there is no route to ordering a peer's avatar around either.
That half is free.

**What was added, and the bug it originally shipped with.** A phantom is marked as
belonging to a remote player when it spawns (`Odyssey.MarkAsRemotePlayer`): `CSWSObject.plot`
(`+0xF8`) is set so this instance cannot kill it, since the remote player is the only
authority on whether their character is alive.

The first version **also copied the local player's faction onto the phantom**, on the
reasoning that this would stop anything treating it as an enemy. That was wrong in a way
worth recording, because the fields were verified correct and the behaviour still was not:

- Faction **0 is PLAYER**. Giving a phantom faction 0 makes enemies evaluate it exactly as
  they evaluate a PC — a valid, hostile target. It stops *allies* attacking it, which is
  the half that was checked, and actively invites *enemies*, which was not.
- `plot` does more than prevent death: `OnApplyDamage` (`0x004DFAC8`) zeroes every damage
  component, so health never moves.
- `CreateNewAttackActions` (`0x005B6980`) releases a target only when it reads dead, and
  contains no plot, `min1hp` or health check at all.

Together those make an **immortal aggro sink**: the first enemy to pick a phantom attacks
it forever and never attacks the real local player again. Now fixed by simply not touching
the faction — `n_commm` ships as faction 5 (Neutral), which reads as ~50 reputation both
ways, so enemies and allies both ignore it.

The lesson generalises: verifying that a field holds the intended value is not verifying
that the engine behaves as intended. Two live instances sat side by side with the fields
set correctly and the defect invisible, because nothing was fighting.

A better phantom (designed, not built) keeps damage visible without the sink: clear `plot`,
set `min1hp` (`+0x200`) so damage floors health at 1, clear bit `0x04` of `+0x9F0` so it
can never swing back, and call `CSWSCreature::PacifyCreature` (`0x004FD960`) to release
aggro when the peer leaves combat. `min1hp` alone re-creates the lock, so the pacify call
is not optional.

Relevant offsets: `CSWSObject.plot +0xF8`, `dead_selectable +0xF4`, `min1hp +0x200`,
`CSWSCreatureStats.faction_id +0x78`, `CSWSCombatRound + 0x9CC` (the target the retarget
logic actually reads — not `+0x504`, which is what NWScript reports).

**What is still open, and it is the real work:**

- **Party duplication.** Both instances run their own copy of Trask, Carth and the rest,
  independently. Nothing reconciles them, so they drift apart immediately. Seamless co-op
  needs a decision here: either the host is authoritative for party members and clients
  suppress or hide their duplicates, or party members are assigned an owning instance
  each. Neither is implemented.
- **Combat is entirely uninvestigated.** Nothing is known yet about whether an enemy
  engages a phantom, whether the two instances agree on who is in combat, whether combat
  rounds line up, or what happens when the same enemy dies in one game and not the other.
  Enemies are per-instance, so the likely starting position is that each player fights
  their own private copy of every encounter and the results diverge. Replicating combat
  properly means picking an authority for enemy state and damage, which is a bigger design
  question than anything solved so far.
- **Death.** Phantoms cannot die, but nothing yet replicates a real player's death.

### 7.5 Publish local state from inside the plugin (task #9, still open)

The plugin knows nothing about the local player; the external agent does the reading. Moving
that in-process would give facing, module name and correct health for free, and remove the
`PlayerCreature` pointer handshake. `CServerExoApp::GetPlayerCreature` is at `0x004B2820`.

### 7.6 Correctness gaps worth closing

- **`hit_points +0xE0` is not the Vitality the UI shows** (read 12 while the sheet said
  25/28). Current vitality is probably in `CSWSCreatureStats`. The profile schema also only
  reads `int32`, while Vitality is two `uint16`.
- **Facing conversion in `KotorGameBridge` is unverified.** reone and xoreos agree that for
  GIT data facing is `-atan2(XOrientation, YOrientation)` with 0 = +Y and forward
  `(-sin f, cos f)`, but the runtime `Vector` at `+0x9C` has not been checked against a
  turning character.
- **Open inconsistency:** `docs/re/object-model.md` records that a server->client push
  requires both the visibility flag `+0x1F8` and the position dirty bit `+0x1FC`, and only
  `SetPosition` raises that bit. That implies a raw `+0x90` write should not update the
  render at all, yet it demonstrably does. Unexplained. Do not build on either account.
- **Only `--kotor` mode uses `KotorGameBridge`.** `profiles/kotor1.json` is still an
  unusable template and `profiles/kotor1-session.json` is an `Absolute` heap anchor kept
  only as evidence. Consider deleting or clearly marking both.

### 7.7 What real co-op still needs beyond the demo

The demo proves the transport and the two hardest engine primitives. The gap to a mod
someone would actually play is mostly *scope*, not mechanism:

- **Item identity is a resref, not an item.** A dropped lightsaber replicates as the
  string `g_w_lghtsbr01`, so upgrades, charges, stack size and item properties are lost,
  and the item is *copied* rather than moved — nothing removes it from the dropper's
  inventory. Real sharing means serialising `CSWSItem` state and hooking the inventory.
- **World flags are not wired to the game.** `EventFlagStore` is complete and tested but
  nothing reads or writes KOTOR's own globals, so doors, quests and dead bosses do not
  agree between instances. This is the "which of the thousands of booleans are global
  truth" problem and is the largest remaining design question.
- **No combat, dialogue or cutscene coordination.** Two players in the same conversation
  or the same fight is entirely unexplored.
- **Health is wrong.** See 7.4; the dashboard shows `hp 0` because `+0xE0` is not Vitality.

### 7.9 Two live bugs the transition research found, and the next build

**Both of these are latent right now and will fire on the first module transition.**

1. `CombatController._killed[]` is never cleared. After a transition the id counters reset,
   so those remembered ids name completely different objects and the controller will
   suppress kills on innocent ones.
2. `PhantomController._phantoms[]` holds raw `CSWSCreature*`. `UnloadModule` frees every
   object, so the first frame after a transition dereferences freed memory —
   `SetAnimation` reads a vtable off it and calls through it. The same applies to
   `SharedBlock+0x258 PlayerCreature`, which the agent writes and the plugin trusts.

The fix is an epoch check as the **first** statement in `PluginHost.OnFrame`: gate on
`loading_module == 0 && server_mode == 2`, compute an epoch key from
`(objectArrayPtr, moduleObjectId, moduleNameHash)`, and on a change clear every controller's
state *before* any of them runs. Plus a pre-store flush of phantoms and replicated items,
so they are not baked into the save (4.8).

**Recommended next build, in order:**

0. **Freeze enemy publication while the local player is in dialogue** (4.10, 7.10). Small —
   read the input class at `CClientExoAppInternal + 0x9C` and skip `PublishEnemies` when it
   is 3 — but it closes a hole in combat replication that is already built.
1. **Epoch invalidation + pre-store flush.** Fixes the two bugs above and protects saves.
2. **Door sync.** Assessed as the highest impact for the lowest risk of anything left: a
   door open for one player and shut for the other is the most visible possible desync.
   Read `door+0x2CC` (open state) and `+0x2C4` (locked); apply through
   `CSWSDoor::OpenDoor` (`0x00589C70`) / `SetOpenState` (`0x00589600`), never by poking the
   field — the walkmesh follows the field but **the model does not**, so a raw write gives
   a door you can walk through that still looks shut. Locked is a plain write. Reuses the
   existing reliable `FlagOp` transport; no id translation needed.
3. **Disable encounters on the client.** Two dword writes per encounter at area load
   (`+0x238 Active = 0`, `+0x278 Started = 1`). Encounter spawns are the one engine-side
   source of id drift and would otherwise break the key everything else rests on.
4. **Container contents**, after 7.1 is confirmed — it reuses the same item machinery.

### 7.10 Dialogue: the design that follows from 4.10

This section is **design reasoning, not reverse engineering** — it is derived from the
verified facts in 4.10 and 4.7, and none of it has been tried. A fuller write-up was
commissioned and did not survive repeated API overloads; if it arrives later, merge it.

The starting point is that **each instance owns a private copy of every NPC**, so nothing
in the engine prevents both players talking to "the same" character at once. There is no
conflict to resolve at the engine level — only at the design level.

**Option 1 — both talk independently.** Costs nothing to implement, because it is what
already happens. Each player gets the full conversation in their own game, and the
*outcomes* converge through the global-variable channel (4.7), whose merge-upwards rule is
idempotent and order-independent — exactly the right shape for quest state. What does
**not** converge is side effects: a conversation that grants an item, awards XP or recruits
a party member runs twice, once per instance. For KOTOR that is duplicated loot and double
XP, which is a balance problem rather than a correctness one.

**Option 2 — lock the NPC.** First player to open a conversation claims it over the
existing reliable op channel; the other instance refuses to start one with that object id
until it is released. Object ids match across instances (4.5), so the claim needs no
translation. Fixes duplicate rewards. The cost is that the second player is locked out of
story content they cannot even watch — the conversation is happening in another process, so
there is nothing to spectate. That is a worse experience than the duplication it prevents.

**Option 3 — host-only dialogue.** Cleanest authority story and the worst game: in KOTOR
most of the content *is* the conversations, so the client becomes a passenger.

**Recommendation for the proof of concept: option 1, plus one guard.** It needs no new
mechanism, keeps both players playing, and converges on quest state for free now that the
global channel exists. Accept the duplicated rewards and write them down as a known
limitation.

The guard is required, and it is the part that interacts with work already built. Per 4.10,
a player in conversation has every hostile in *their* instance disarmed on every dialog
tick. Because enemies are host-authoritative, a host conversation would propagate that
pacification to the client. So: **while the local player is in dialogue, stop publishing
enemy state and let the peer hold its last snapshot.** Detect it by reading the input class
at `CClientExoAppInternal + 0x9C` (3 = dialog); freezing publication rather than sending
"all enemies idle" keeps the client's fight coherent for the few seconds a conversation
lasts. The same read is the natural hook for "peer is busy talking" in the UI later.

**The fuller design, later.** Option 2's claim mechanism becomes worthwhile once rewards
matter, and it pairs naturally with replicating the *journal* (4.7) rather than inferring
quest state from globals alone. Suppressing a cutscene for the non-participating player was
not investigated; it is the obvious next question, because a conversation that stages a
cutscene will move that instance's camera and party while the peer keeps playing.

### 7.8 Housekeeping

- Helper script defaults are fixed and every tool now takes `-ProcId`. New tools this
  round: `demo.ps1`, `movewin.ps1`, `wheel.ps1`, `tooltip.ps1`.
- **The repo is still not under version control.** `git init` and commit; there is a
  `.gitignore` already. Consider whether the 2.3 MB `tools/kotor1_0_3.db` belongs in git
  or should be fetched by a script.
- `docs/re/` is 144 KB of research across `object-model.md`, `frame-hook.md`, `spawn.md`,
  `engine-movement.md`, `rendering.md`. Section 11 of `rendering.md` holds corrections to
  `object-model.md` that were never merged in. None of it yet covers items or the
  single-instance guard; section 4 above is the only record of those.

---

## 8. Task list state

| # | Task | Status |
| --- | --- | --- |
| 6 | Locate object list and creature vtable | done |
| 7 | Find and call the creature spawn path | **done, working** |
| 8 | Drive the phantom through engine movement | **done, working** (via action queue, not the researched primitive) |
| 9 | Publish authoritative local state from inside the game | open |
| 10 | Main-thread dispatch | **done, working** |
| 11 | Test writing `CSWSObject.position` | done |
| 12 | Durable player anchor | done (superseded by `KotorGameBridge`) |
| 13 | Phantom walk animation and facing | done (animation); facing on arrival open |
| 14 | Decide the single frame-hook site | resolved: `SwapBuffers` IAT |
| 15 | Run two KOTOR instances at once | **done, working** (mutex rename, 4.1) |
| 16 | Two real instances seeing each other | **done, working**, both directions |
| 17 | Phantom appearance matches the remote player | **done, working** |
| 18 | Shared ground items | drop and despawn **done**; pickup detection unverified, 7.1 |
| 19 | Auto-load a save with no human input | **works for instance 1**, unreliable for instance 2, 7.2 |
| 20 | Keep an unfocused instance running | **done**: the pause is real; `FocusKeeper` clears it, 4.0 |
| 21 | Ownership: peers cannot fight or kill each other | **fixed** after the first version made phantoms an immortal aggro sink, 7.4 |
| 34 | Auto-pause suppressed (six option bits) | **done**: two instances auto-pausing independently is a desync source |
| 35 | Dialogue in co-op | designed (7.10), not built; the enemy-publication guard is the one piece that matters now |
| 22 | Combat across instances | **done, verified**: host-authoritative enemy health and death |
| 23 | Fix current HP (`+0xDC`, not `+0xE0`) | **done, verified**: health reads 12 instead of 0 |
| 24 | Item pickup detection via the possessor field | **done, verified**: drop, persist, engine pickup, detect, remove |
| 25 | Combat participation: aggro, targeting, player death | researching |
| 26 | Dialogue and cutscenes in co-op | researching |
| 27 | Module / area transitions | researching |
| 28 | World and quest state wired to engine globals | researching |
| 29 | Doors, placeables, containers, triggers | researching |
| 30 | Game data file readers (KEY/BIF, RIM, GFF, 2DA) | **done**: `tools\kotorfiles.py`, 4.9 |
| 31 | World/quest globals replicated | **code done, untested in game**, 4.7 |
| 32 | Doors / placeables / containers | doors **built**; unlock leg **verified live** (addendum 0); placeables/containers open |
| 33 | Module transitions: epoch invalidation + pre-store flush | **built** (addendum 0); a real door/trigger transition not yet exercised live |
| 36 | Freeze enemy publication during dialogue | **done** (was 7.9 step 0) |
| 37 | Client encounters disabled | **built** (addendum 0) |
| 38 | Movement fidelity: run gait, 0.6u resend, facing, snap | **built**, awaiting live feel-check (addendum 0) |
| 39 | Equipment sync v1: armor + weapons on the phantom | **built**, untested live; watch for proficiency refusals (addendum 0) |
| 40 | Travel together: clients follow the host across modules | **built, untested in game**, 4.11 (SharedBlock v6) |
| 41 | Phantom name plates: peer's name in the selection band | **built, untested in game**, 4.11 |
| 42 | Player death mirrored as a phantom death pose | **built, untested in game**, 4.11 |
| 43 | In-game Connect flow (plugin-driven save load, then a .gui mod) | designed; CSWGuiSaveLoad::LoadGame 0x6CB0E0, see 4.6 and addendum 0's load path — ⚠ block offsets moved, see 0.2 |
| 44 | Map presence: peers as map-note waypoints | idea recorded, unverified |
| 45 | Journal replication (quest merge) | **built, untested in game**, addendum 0.2 |
| 46 | Client damage counts on the host (enemy report min-merge) | **built, untested in game**, addendum 0.2 |
| 47 | Shared pause across instances | **built, untested in game**, addendum 0.2 |
| 48 | Phantom vulnerability without the aggro sink (no plot, min1hp, pacify) | **built, untested in game**, addendum 0.2 |

---

## 9. One-paragraph summary for a fresh session

kotor-coop is a C# co-op mod for retail KOTOR 1, and it now runs as actual two-player
co-op on one machine. Two GOG instances run side by side — the retail single-instance
guard is just a `CreateMutexA("swkotor")` at `0x00404222`, so `agent launch --instance 2`
starts the second copy suspended, renames that one string, and resumes it. Each instance
gets an external agent (which finds the player by scanning for the `CSWSCreature` vtable
`0x007470C8` and testing `is_pc`) plus an in-process NativeAOT `win-x86` plugin loaded as
a `dinput8.dll` proxy, which hooks the `GDI32!SwapBuffers` IAT slot at `0x0073D00C` for a
main-thread per-frame callback. On that callback the plugin calls the engine's own
NWScript routines: `CreateObject` to spawn a real creature per remote player,
`ActionMoveToLocation` to walk it with pathfinding and animation, and
`CreateItemOnFloor`/`DestroyObject` to materialise and remove shared ground items. Both
players see each other move, both wear each other's real appearance, and an item dropped
by one appears as a genuine engine object at identical coordinates in the other. 82 tests
pass. Startup is close to unattended: `agent launch --load-save <n>` drives the menu into
a save on its own, reliably for the first instance and not yet for the second (7.2), and
an unfocused or minimised instance keeps simulating at full speed, which was measured
rather than assumed (4.0). Three things are written but unproven: item *pickup*
propagation (7.1), and the ownership guard that makes a peer's avatar unkillable and
friendly (7.4). Combat across instances is completely unexplored and is the largest open
question — enemies are per-instance, so each player currently fights their own private
copy of every encounter, and fixing that means choosing an authority for enemy state.

---

## Addendum 0d — runtime command layer + in-game ~ console (block v9)

Built on top of addendum 0c (party sync, password, reconnect, Connect menu):

- **Session lifecycle is now runtime-controllable.** `SessionManager` (KotorCoop.Session)
  owns connect/disconnect/reconnect and creates one `CoopAgent` per session;
  `Program.RunAsync` now drives it through `CoopCommandDispatcher` instead of a single
  process-lived session. Disconnect cancels the agent, disposes the session and calls the
  new `IGameBridge.ClearCoopState()` (zeroes remote/enemy counts, releases the party
  freeze). `AgentDashboard` reads the live agent from the manager each tick.
- **In-game ~ console** (task 23). Reuses KOTOR's native Aurora dev console rather than
  building input/rendering or guessing a console-callback ABI (that would crash the game).
  The plugin sets `EnableCheats=1` in swkotor.ini so `~` opens the console with its keyboard
  grab, then `ConsoleController` READS the engine command buffer `0x007A3A4C` — remembering
  it from the frame before Enter so the engine's clear can't race it — and routes co-op
  verbs into the block console channel. Replies paint via `PrintStringScreenPos 0x0044CCA0`.
  Commands: `connect <ip> [password] | host | disconnect | reconnect | status | help`; a
  password rejection becomes an on-screen "connect <ip> <password>" prompt.
- **SharedBlock is v9** — console command/reply channel appended (serial + 96-byte line each
  way) at 0x9F4..0xA7C; plugin signature bumped 08 → 09. Total size 0xA80.
- Full RE write-up: docs/research/console-overlay.md. 181 tests pass; plugin
  NativeAOT-publishes to a correct x86 dinput8.dll.

**Live-verification gaps (all fail safe — cosmetic or no-route, never a crash):** console
text coordinates (centered 0.01-scaled space, guessed), the remember-previous-frame capture
timing vs the real message/frame order, and that the native console renders at the main menu.
These join the existing "built, untested in game" items and task 11 (live two-instance pass).

---

## Addendum 0e — dialogue token + text chat (protocol v6/v7)

- **Dialogue sync — host-arbitrated conversation token** (task 24, protocol v6). KOTOR
  conversations are modal single-player; co-op needs both instances to agree on one owner or
  two players race the plot flags a story conversation sets. `DialogueState` +
  `DialogueArbiter` (pure, exhaustively tested): grant when free, update the spectate line
  from the owner, ignore a contender while held (first speaker wins), release only from the
  owner — and on owner disconnect/timeout, so a crash mid-conversation can't wedge everyone.
  `DialogueSnapshot` (host->all, every tick) + `DialogueRequest` (peer->host each tick,
  state-not-events). Capability bit `Dialogue` (content-independent). The agent publishes the
  in-dialogue bit already present in `PlayerState.Flags`; dashboard shows who holds the token.
- **Dialogue plugin work is deferred** (task 25) — see the new
  docs/research/dialogue-sync.md (VERIFIED/INFERRED/NEEDS-LIVE tagged). Layer-1 detect is
  static-safe; the current-entry-index field (`CSWSDialog+0x60..0x74`), the `CSWGuiDialog`
  panel pointer, `CGuiInGame+0xB4` semantics, and a live `SelectReply 0x006A7110` round-trip
  are all NEEDS-LIVE. Safe drive call when it's time: `CSWGuiDialog::SelectReply` (thiscall,
  one int, verified ABI) from the SwapBuffers hook. The active `CSWSDialog*` is
  `*(CSWSObject+0x34)`.
- **Text chat** (protocol v7). `say <msg>` in the ~ console; the host relays in the star
  topology; incoming lines render through the existing console reply path — no new plugin or
  block code. Sender echoes locally, never receives its own back.
- 196 tests pass. All builds (tests, Agent, plugin NativeAOT) clean.

---

## Addendum 0f — chat overlay, shared progression, three new research docs

- **In-game chat overlay** (block v10). Always-on, semi-transparent chat readout independent
  of the ~ console. Agent pushes each chat line + coop.ini config (chat_overlay / _opacity /
  _anchor / _lines / _fade_ms) into the block; `ChatOverlayController` accumulates timestamped
  lines and draws recent ones at a screen corner, fading them out. **Opacity is real**: the
  texture-font color is four .data floats (RGBA 0x00798BB4..0x00798BC0, default white) and the
  draw already alpha-blends, so `Odyssey.SetFontColor` writes the alpha before the lines and
  restores white after — no new engine call (docs/research/text-color-alpha.md). Also fixed
  SharedBlock TotalSize (the v9 console-reply field overran 0xA80; worked only via page
  rounding). Signature 09 -> 0A.
- **Shared progression** (protocol v8). Host-authoritative absolute gold + PC XP mirror so both
  parties level/spend together. Gold = partyTable+0x0FC (VERIFIED); XP via the PC stats block
  (pointer NEEDS-LIVE, but a bad deref only fails the cross-process write). Capability
  `Progression` (content-independent); `--no-progression-sync` escape hatch.
  docs/research/progression-sync.md.
- **Research ready to build next:** docs/research/placeables-containers.md — placeable vtable
  `0x007494D0`, state bytes locked `+0x260` / open `+0x338`, container `CItemRepository*` at
  `+0x36C` (ids `+0x0C`, count `+0x10`), client apply via VERIFIED `CSWSPlaceable::RemoveItem
  0x00584AC0`. Design: a `PlaceableController` mirroring `DoorController` one-for-one + loot
  removals reusing the ground-item ItemOp envelope keyed (container, item). The placeable
  open-animation vtable slot `[vt+0x7C]` and the item-dispose tail are NEEDS-LIVE guessed-ABI
  paths — keep behind live verification; the field writes + RemoveItem are safe.
- 198 tests pass. All builds (tests, Agent, plugin NativeAOT) clean.

---

## Addendum 0g — placeable state + trap disarm (block v11)

The door channel's twin for placeables (chests, corpses, switches, containers), from
docs/research/placeables-containers.md. Object-id keyed, same-save assumption, shares the
Doors capability gate.

- **PlaceableFlags** (tag 0x50, distinct from doors' 0x44, out of the globals namespace):
  monotonic bits open(1) / unlocked(2) / disarmed(4), module-hashed + object-id keyed, rides
  the existing reliable max-merge flag store.
- **Bridge**: CollectChangedPlaceableState scans vtable `0x007494D0`, reads locked `+0x260`,
  open `+0x338`, trap `+0x278`; silent per-module baseline; throttled 1/s like doors.
  TryWritePlaceableStates fills block v11 placeable slots (0xB58 count, 0xB60 slots, id@0,
  open@4, unlocked@5, disarmed@6). TotalSize 0xB80 -> 0xC80; signature 0A -> 0B.
- **Plugin PlaceableController** mirrors DoorController but applies as **plain field writes**
  (placeables have no walkmesh coupling): ResolvePlaceable(id) then write locked/trap/open.
  No engine call, no crash risk. The cosmetic open-pose vtable call `[vt+0x7C]` is left out
  (NEEDS-LIVE, desyncs nothing without it).
- **Agent PumpPlaceables** shares the Doors gate.
- **Deferred (task #29):** container-loot removal — needs a (container,item) transport and a
  live pass on the verified `RemoveItem 0x00584AC0` round-trip. Trigger-mine disarm
  (`CSWSTrigger +0x2CC/+0x2D0`) is NEEDS-LIVE too.
- 206 tests pass. All builds (tests, Agent, plugin NativeAOT) clean.

### Session channel tally (protocol v8, block v11)
Phantoms · world/quest flags · doors · **placeables (open/locked/disarmed)** · enemies +
client damage · ground items · journal · appearance · equipment · party · **dialogue token** ·
**shared gold+XP** · pause · travel · map pins · name plates · **text chat + on-screen
overlay** · session connect/disconnect/reconnect · in-game ~ console. Remaining big items are
all live-gated: dialogue spectate/control (task #25), container loot (task #29), and the live
two-instance verification pass (task #11).

---

## Addendum 0h — overlay UX, dialogue spectate, container loot (block v12)

- **Live pass fixes** (all verified live): the ~ console crash (null font-table deref in the
  immediate `PrintStringScreenPos` from the SwapBuffers hook) -> switched overlay text to the
  engine's QUEUED path (`AurPostString 0x0044D490`, flushed every frame by UpdateScreen,
  never touches the font); the console-capture race (buffer cleared+console closed on Enter
  before our hook) -> route the remembered line on the Enter edge regardless of open state;
  "function <x> not found" (console-table registration didn't match our entries) -> blank the
  message format string at `0x0074145C`. Overlay UX: `clear`/`cls` command (block clear-serial
  at 0xC60), moved off-screen row 24 -> visible top rows, single always-on ChatOverlayController
  as the sole display capped to last N, all feedback (replies+chat) routed to it.
- **Animation sync**: the "x" draw / flourishes / attack swings are client-twin fire-and-forget
  animations (not the server +0xD4 the bridge read). Bridge now reads the twin's current
  animation (creature+0x224 -> twin+0x48); phantom replays via `AddFireForgetAnimation`
  (twin vtable+0xA0, 0x006183D0). NEEDS-LIVE: twin+0x48 vs the F&F queue for repeats.
- **Dialogue spectate** (task 25, DONE): owner plugin reads the live CSWSDialog speaker+line
  (region 0xC80..0xD00), the token carries it, the peer sees `Speaker: line` on the overlay.
  NEEDS-LIVE: current-entry-index (+0x60 guess, bounds-checked) and base-game TLK-StrRef lines
  (resolve empty for now).
- **Container-loot removal** (task 29, DONE): host diffs each placeable's CItemRepository
  (+0x36C), broadcasts (container,item) removals reliably (ContainerLoot=29 / Ack=30), client
  applies via VERIFIED `CSWSPlaceable::RemoveItem 0x00584AC0` (region 0xD00..0xE00). NEEDS-LIVE:
  cross-instance item-id shape (+0x14) in a container + GIT-seeded id match.
- Both features built by parallel worktree subagents from the research docs, merged clean.
  Block still v12 / signature 0C (used pre-reserved regions). 217 tests pass; Agent + plugin
  NativeAOT build. New research docs: text-render-queued, console-register, animation-sync.

---

## Addendum 0i — enemy presence sync (protocol v12, block v20), 2026-08-06

The biggest remaining seam in "seamless": the enemy channel replicated outcomes only
(health + death), so each instance's AI ran its own private version of every fight — same
soldier, different place, different behaviour on the two screens. Now the owner streams
each enemy's PRESENCE and the mirroring instance drives its copies to match, with the same
recipe that made phantoms seamless. Design + risk register: docs/research/enemy-presence-sync.md.

- **Wire (protocol v12)**: `EnemyState` gains Position/Facing/Animation, serialized in the
  SNAPSHOT only — a client's `EnemyReport` still carries health alone (its positions are
  never authoritative). 29 bytes/enemy; a full 32-enemy snapshot still fits one datagram.
- **Owner side**: `ReadEnemies` also reads `+0x90` position, `+0x9C` orientation (→ facing
  angle) and `+0xD4` looping animation — three extra external reads per enemy, all
  long-verified offsets.
- **Block v20** (signature `…14 00 00 00…`, TotalSize 0x1400 → 0x1A00): presence region
  count @0x1400, 32 slots @0x1410 stride 0x20 (id last per slot), paired 1:1 with the
  existing enemy slots and written by the same `TryWriteEnemies` pass.
- **Plugin `EnemyPresenceController`** (mirror side, every frame): resolve + revalidate the
  creature (id @+0x04, type @+0x08 == 5 — the 0g stale-pointer lesson), re-claim EVERY pass
  (`ClaimCreature`: commandable, ClearAllActions, movement permitted — an armed MoveToPoint
  is the contended writer from realtime-creature-movement §2.1), then the phantom drive:
  measured speed, smoothed shown position, gait hysteresis, `Odyssey.DriveEnemy` →
  `DriveBody` (shared with `DrivePhantom`; gait table widened 8 → 48). Standing enemies
  hold the owner's looping id when it is in the 0x2710..0x27FF band, else idle.
- **Deliberate model change**: on the mirroring instance enemies no longer act locally —
  they are a presentation of the owner's fight. The mirroring player's damage still
  min-merges as before. Enemy threat TO the mirroring player is combat-participation work,
  with the option space recorded in the research doc (aggro-sink hazard and all).
- **Guards**: dead → stop driving + forget; skip local phantom-id collisions (host
  encounter spawns share the id range with local phantom spawns); skip party-follow
  creatures and the player; whole pass skipped during a local conversation; positions
  sanity-checked; `enemy_drive=0` kill switch in coop.ini.
- **NEEDS-LIVE** (checklist in the doc): render-position check, per-frame re-claim side
  effects, forced commandable vs cutscenes, stance mirror, client attack targeting, perf
  at census cap.
- 266 tests pass; solution, Agent publish and plugin NativeAOT x86 publish all clean.

### 0i.1 — enemy one-shots (swings) ride the same channel, 2026-08-06 (later)

Presence alone shows bodies in the right places; the strikes were still invisible. Now the
animation hook (`InsertFireForgetAnimation 0x0063C760`) captures EVERY creature's
fire-and-forget — not just the local player's — into a fixed 48-slot table keyed by object
id (creature twins only, vtable-checked; doors/placeables come through the same site).
Plugin publishes the table (block 0x1920..0x1C20, TotalSize -> 0x1E00); the owner's agent
matches ids against its census and each `EnemyState` carries (one-shot serial, animation)
— snapshot 35 B/enemy, full 32 still one datagram (asserted by test). The mirror's
presence slots grew to stride 0x28 and `EnemyPresenceController` replays new serials via
`PlayOneShotAnimation`, primed-on-first-sight so a join never replays a stale swing.
**Echo guard**: `AnimationHook.Suppress` is held while the plugin itself applies any
mirrored animation (`PlayOneShotAnimation` / `PlayFireForget`) because
`AddFireForgetAnimation` reaches the hook site from inside the engine — without it every
mirrored swing would be re-captured and bounce back to its owner. NEEDS-LIVE: that
reentry path (suppress flag is cheap either way), and whether enemy swings visibly play
on driven bodies. 266 tests; all three builds clean.

### 0i.2 — companions ride the seamless path too, 2026-08-06 (later)

The client's Trask/Carth were mirrored by the AGENT's raw external position writes
(`TryWritePartyMembers`), which predate the seamless recipe: no dirty bit, never touched
the client twin, so the rendered companion trailed the 512 ms ease and stepped once per
network tick. Now the same block-and-drive shape as enemies:

- `PartyMemberState` carries one-shots (swing serial + id) like the enemy channel; the
  hook's capture table is shared (renamed bridge-side to `ReadCreatureOneShots`).
- Block: party presence region 0x1C20/0x1C30, stride 0x28, 4 slots, keyed by ROSTER SLOT
  + 1 (0 = empty; runtime ids are not cross-instance keys for companions). Bridge writes
  it when the plugin block exists; the old external-write path survives only as the
  plugin-less (mock) fallback. Companion HEALTH moved into the slot so the plugin is the
  single writer for the body.
- Plugin `PartyPresenceController`: resolves the roster slot against THIS instance's own
  party table (APP_MANAGER -> server -> internal + 0x1B770 walk, in-process), then the
  standard claim-each-pass + one-shot replay + `MotionSmoother` + `DriveEnemy`. Solo mode
  stays agent-held. Kill switch `party_drive=0`.
- `MotionSmoother` extracted: the measure-smooth-decide half shared by the enemy and
  party controllers (PhantomController keeps its tuned copy; fold in only with a live
  check).
- NEEDS-LIVE: claiming a party-followed creature every pass (the phantom-era warning
  about +0x4C0 was about CLAIMING one as a phantom; here solo mode should keep the
  follow actions away — verify), and companion swings visibly playing. 266 tests; all
  three builds clean.

### 0i.3 — LIVE VERIFIED, same day: presence sync works end to end, 2026-08-06

Full unattended two-instance run (`tools\demo.ps1`, Endar Spire save 1, pids 7820 host /
41636 client). **Both instances auto-loaded with no human click** — instance 2 came up via
`agent load` on the first try, so 7.2's "never works for the second instance" appears to
be superseded by the plugin-driven load path; downgrade that item after one more clean run.

What the run proved, by memory read (worst case across ALL creatures):

- Client log: `enemy drive: mirroring 24 of the owner's 25 enemies` and `party drive:
  mirroring 1 of the host's companions`, within ~11 s of the world coming up.
- **Cross-instance census agreement: 27 of 27 tag-matched creatures, 26 at distance
  0.00 units, health identical on every one** (including Carth at 500 both sides). The
  single 0.60 u outlier is a patrolling welder sampled seconds apart by two sequential
  scans; the 25th unmirrored enemy is `end_trask 0x127`, party-followed on the client and
  therefore skipped by the +0x4C0 guard by design — the party drive owns him instead, and
  his position still matched exactly.
- **The presentation followed, not just the simulation** (the 0h lesson applied): a probe
  of every client creature's twin (`+0x224 -> +0x24`, the rendered position) against its
  server object showed **28/28 twins present with worst gap 0.000**.
- Interplay intact: `sethp 0x14 -> 6` on the host mirrored to 6 on the client's DRIVEN
  copy within seconds — drive, min-1-hp pin and health mirror do not fight.
- No crash, no errors in either plugin log across the whole run; both games shut down
  clean via `demo.ps1 -Stop`.

Still genuinely NEEDS-PLAY (a human watching the screen): whether driven enemies LOOK
right in motion during a real fight (gait, swing timing), stance mirroring, and the
per-frame re-claim's effect on scripted sequences. The static/inert-enemy save cannot
show those.

---

## Addendum 0j — the BG3 dialogue rule + server-browser review, 2026-08-06

The user's ask: one conversation for the whole session (a la Baldur's Gate 3 — while any
player talks, nobody else can start a dialogue), correct behaviour on mid-fight/mid-dialogue
joins and on disconnects, and a review-and-finish pass on the server browser.

### One conversation, enforced at the engine's front door

The token arbiter alone only ignored a loser's CLAIM — the loser's conversation still ran
locally and raced the plot flags (docs/research/dialogue-sync.md option 2 was the designed
fix and sat unbuilt). Now shipped as **`DialogueGateHook`**: a verify-bytes detour on
`CSWSObject::StartDialog 0x004CF490` that refuses every conversation start while a REMOTE
peer holds the token. The one NEEDS-LIVE that blocked it — the return contract — is now
settled STATICALLY: both call sites do `test eax,eax; je <cleanup>` (cleanup calls
0x004F34A0 and completes), the epilogue is `ret 0xC` at 0x004CF8DF, and the guard runs
before the SEH prologue is established, so the refusal is `xor eax,eax; ret 0xC` onto the
engine's own failure path. Doc updated in place.

Pieces around it, all in DialogueSpectateController:
- **The bypass is LEVEL-based, not scoped.** The spectator's own follow-open is requested
  via a server action and StartDialog fires FRAMES later — a try/finally around
  StartDialogueWith would block our own follow. `Bypass` goes up at the follow request and
  down when the follow ends/gives up/invalidates.
- **The race window yields.** Two players clicking inside one token round-trip both open
  before the gate can know; the loser now CLOSES its own conversation
  (`Odyssey.CloseDialogue`) and follows the winner, instead of the old "whoever got there
  first keeps theirs" coexistence.
- **A refused click gets feedback**: on-screen "Another player is in a conversation"
  (queued-text path) for 2.5 s plus a capped log line.
- Kill switch: `dialogue_exclusive=0` in coop.ini.

### Joins and disconnects, reviewed end to end

- **Join mid-dialogue**: the host broadcasts the token every tick to every connected peer,
  and the spectate controller replays the pick log FROM ZERO on follow — a late joiner
  lands on the right node (this was protocol v11's whole point). The gate blocks them from
  initiating from their first tick. ✓ already correct.
- **Join mid-fight**: enemy census + presence are per-tick level state, encounters are
  disabled before mirroring, deaths are a level not an event, one-shot serials are
  primed-on-first-sight. ✓ already correct (and live-verified this morning). Edge noted:
  an enemy whose corpse the host's module has fully cleaned up stops being published, so a
  VERY late joiner could hold a live copy of a long-dead enemy; acceptable, recorded here.
- **Owner client disconnects/times out**: host releases the token (Bye handler + the
  5 s peer timeout both call ReleaseDialogueIfOwnedBy — covered by the existing
  TheDialogueTokenIsReleasedWhenTheOwnerLeaves integration test), broadcast goes idle,
  spectators close their mirrored copy. ✓ already correct.
- **The session itself dies mid-follow (host gone, agent torn down)**: FOUND AND FIXED —
  the remote-dialogue flag is level-written by the agent every tick, so with the agent gone
  nothing ever lowered it and a mid-follow spectator stayed locked out of its own replies
  forever. `KotorGameBridge.ClearCoopState` (run on every SessionManager.Disconnect) now
  lowers it, plus the enemy/party presence counts for the same staleness reason.
- **Reconnect of a mid-conversation owner**: token was released on timeout; their local
  conversation still runs; their next publish re-claims and the spectators re-follow.
  Self-healing, no code needed.

### Server browser review (task: review and implement)

Reviewed all three layers (beacons -> agent -> native panel/overlay). The flow itself was
complete and live-verified previously; what it lacked was join-in-progress context, so a
browsing player had no idea a listed game was mid-run, locked, or where. Shipped:

- **Beacon v2** (back-compatible both directions, unit-tested): appends flags (bit 0 =
  password required) + the host's CURRENT MODULE, read live per beacon alongside the live
  player count. v1 datagrams parse as unlocked/unknown-module.
- **Rows now read** `Name [end_m01aa]  ip:port  (2/8) *` — module folded into the name
  column (the 0x40-byte entry has no room for a new string field; entry byte 0x3A carries
  the new locked flag), asterisk = password prompt coming. Both the native saveload-panel
  rows and the F9 overlay fallback show it.
- SessionManager advertises locked (password set) + module via a live read of the bridge's
  local state.

268 tests (2 new beacon tests); solution + Agent publish + plugin NativeAOT all clean.
NEEDS-PLAY: a real two-human pass on the gate (does a blocked click FEEL right; does a
blocked scripted conversation retry rather than wedge a quest — the gate only fires while
a peer is actively mid-conversation, which bounds the exposure).

### 0j.1 — smoke-tested live, same day

Unattended two-instance run with the gate armed: `dialogue gate at 0x4CF490` on BOTH
instances (the verify-bytes prologue check passed against the real binary), session came
up normally, enemy drive (23/25) and party drive (1) live on the client, zero crash lines
across several minutes of runtime with the detour sitting on the conversation path. What
this run could NOT exercise (inert save, no second human): an actual blocked click, the
race back-off, and the spectate-follow bypass — those are the NEEDS-PLAY list from 0j.

---

## Addendum 0k — quests actually sync now (the journal pointer was never dereferenced), 2026-08-06

Task #45 ("journal replication, built, untested in game") was not merely untested — it was
**broken in both directions and it killed the host the first time anyone pulled the
trigger**. Found by finally running it.

**The bug.** `CSWPartyTable + 0x11C` **HOLDS A POINTER** to the CSWSJournal; it is not the
journal. Both the plugin's write path and the agent's read path used `partyTable + 0x11C`
as the array list itself, so `CSWSJournal::SetState` walked whatever followed it in the
party table and faulted dereferencing a "tag" of `0xAE37C3BE`
(`CRASH at 0x5E6491` inside `CExoString::CompareNoCase_2`, from the compare at
`0x005C5AFF`). The engine settles it in three instructions: `CSWPartyTable::GetPartyJournal
0x005633C0` is `mov eax,[esi+0x11C]`, reached from `CSWSCreature::GetJournal 0x004EDB00`
-> `CServerExoApp::GetPartyTable 0x004AEE70` (`[server+4] + 0x1B770`, which independently
confirms the party-table base). **Method note:** the crash address alone named the victim;
the *fix* came from asking the binary how IT reaches the journal — the xref on SetState's
callers took two minutes and removed all guessing.

**The second bug, behind the first.** With the pointer fixed the update applied but the tag
grew a byte per round trip (`'end_attack'`, `'end_attack\0'`, `'end_attack\0\0'`…) and the
quest re-applied every ~3 s forever. A CExoString's length INCLUDES its terminating null —
the engine's own entry stores 11 for "end_attack" — so `ReadExoString` was baking a `\0`
into the string, which then went back out as the next request's tag and never matched the
shadow. Same trap as the module name in 0g; `ReadExoString` now cuts at the first null.
(This is the THIRD time this convention has cost a bug: module names, equipment resrefs,
now journal tags. It is a property of the engine's strings, not of any one field.)

**Also shipped**: `SetJournalState` validates before calling — array-list shape plus a real
committed-memory probe (`NativeMethods.IsReadable`) on every entry tag, because `SetState`
validates nothing. Verified in isolation: with the guard but before the pointer fix, the
crash became `journal: 'k_cooptest' refused (failure 5)` and both instances survived.

**VERIFIED LIVE, end to end.** Two instances, `blockpoke journal end_attack 25` on the host:
both plugin logs report the apply exactly ONCE (`count: 1` each, clean tag, no loop), zero
crashes, and both games' real journals read `entry 0: 'end_attack' state 25` — up from the
baseline 20 both sides started at. **Quest state now genuinely replicates between two
KOTOR instances.**

### Cinematic actors (protocol v13)

- Plot creatures now ride the presence census with a `Plot` flag: presence replicates (so a
  scripted scene plays out identically on both screens) while the combat half skips them
  entirely — no pin, no health mirror, no death, and a peer's damage report for one is
  ignored on the owner (whose own census decides plot-ness; a peer cannot un-plot a body).
- **The census no longer filters by faction.** It excluded "friendly" creatures, which is
  exactly what a story NPC is — measured live: the client mirrored 24 of 25 creatures and
  the missing one was `carth 0x2`. Now 25/25. The player, party and phantoms are still
  excluded by their own explicit checks.
- The 32-slot cap now sheds the least visible bodies: engine-flagged in-combat creatures
  first (the honest fight signal — faction semantics are a matrix lookup an external agent
  cannot make), then nearest-first from the local player.
- NEEDS-LIVE: the plot path itself is wire-tested only — the Endar Spire start contains no
  plot-flagged creature, so a module that does is required to see a scene driven end to end.

268 tests; solution, Agent publish and plugin NativeAOT all clean.

---

## Addendum 0l — combat participation: enemies can hurt BOTH players now, 2026-08-06

Built from docs/research/combat-participation.md and **verified live between two windows**.
This closes the last "co-op in name only" gap: a client could previously be surrounded and
swung at on the host's screen and never lose a hit point.

### What ships

- **The phantom is a real target.** On spawn (and whenever the peer is alive) it joins
  faction **0 (PLAYER)** via the engine's own `CSWSFaction::AddMember`, so every hostile
  evaluates it exactly as it evaluates a PC. On death / leaving combat it returns to
  Neutral **and** is pacified. That flip back is the release lever 7.4 never had:
  `CreateNewAttackActions` re-checks `GetReputation > 10` at every combat-round boundary.
  Edge-triggered only (AddMember walks member lists). Kill switch `phantom_targetable=0`.
  Also fixed while in there: `MarkAsRemotePlayer`'s faction write was a **ushort** into a
  field the engine reads as a **dword**.
- **Hits are observed** by a verify-bytes detour on `CSWSEffectListHandler::OnApplyDamage
  0x004DFA40` (13 bytes verified, 7 relocated) — the damage analog of the animation hook.
  Filters on phantom id FIRST (it runs for every damage event in the game), sums bounded
  per-type amounts, records `(cumulative, attacker)`.
- **Carried as CUMULATIVE TOTALS** through the block (v21), the wire (`PhantomDamage`,
  peer -> host -> addressee, monotonic) and back — never events. A lost datagram costs a
  tick; a duplicate costs nothing. The receiver applies the DIFFERENCE.
- **Applied** through the VM's own instant `EffectDamage` (`ApplyDamageEffect`, generalised
  out of `KillCreature`), so resistances, difficulty scaling and floaty text all run
  natively. Capped per application, skipped during conversations and while downed.
- **Test hook** `blockpoke hitphantom <slot> <amount>` lands a REAL engine damage effect on
  a phantom, because staging an armed fight on demand does not work (7.0 — the Endar
  Spire's Sith are inert). `blockpoke combat` prints both halves of the ledger.

### The bug the live run caught, and how

First hit: **4 damage in, 8 out.** The block's hit table said `hits 1`, so it was not firing
twice — it was double-counting inside one event. Rather than guess, the hook was made to
record the raw 15 slots and the controller logged them:

```
hit breakdown: [0]=-1 [1]=-1 [2]=-1 [3]=7 ... [13]=-1 [14]=7
```

**Unpopulated slots read -1, and the amount appears TWICE** — in its type slot and again in
the plain/universal slot 14, which the engine keeps separately (`0x004DFAEA`) as a view of
the same damage, not an extra component. Summing all fifteen doubled everything. Now: sum
the typed slots, use slot 14 only as the fallback when nothing else is populated. This is
invisible to a unit test — the effect object comes from the engine.

### Verified live (two instances, host 31720 / client 28500)

- `combat: phantom-hit watch at 0x4DFA40` on both; 13-byte verify passed on the real binary.
- Faction flip **read back from memory**: phantom `stats+0x78` = 0, identical to the real
  player, min1hp still 1, on BOTH sides.
- 7 damage on the host's copy of the client's phantom -> `combat: took 7` on the client,
  real HP **12 -> 5**. Then 3 -> **5 -> 2**. Host ledger: `taken 10, hits 2`. Exact, no
  double-count, no replay.
- Zero crashes across the whole run with a detour sitting on every damage event.

### Known limit, found by the same run

**Forwarded damage cannot DOWN a remote player** — 20 damage floored them at 1 HP. That is
KOTOR's own party rule (the plugin never sets min1hp on a PC; party members floor at 1 and
are downed by a separate temp-death path), so the last hit does not convert into a downed
state. Enemies can now wear a remote player down but not finish them. Needs its own pass:
what the engine requires to trigger party temp-death from a damage event.

### Still NEEDS-LIVE (unchanged by this run)

The aggro half: no enemy has actually ATTACKED a phantom yet, because nothing in the test
save is armed. Items 1-3 and 8 of the research checklist (cold-aggro latency, the release
soak, two-sided fight) need a module with a live encounter — a human walking into a real
fight settles all of them at once. 269 tests; all three builds clean.

---

## Addendum 0m — driving the players, and a stale-token bug the drive exposed, 2026-08-06

Added test hooks to drive the local player from the block, because "click the NPC" is
exactly the step automation keeps failing at (KOTOR tracks its own cursor from relative
DirectInput deltas; traps 22/25). `TestDriveController` + `blockpoke talk <tag>` /
`walk <x> <y>` / `dialogue`. **`talk` runs `StartDialogueWith`, which is the same
player-to-server request a mouse click produces**, so the whole conversation path —
including the exclusivity gate — is exercised for real rather than simulated.

### Verified live, two windows

- **Conversation opens on demand**: `blockpoke talk end_trask` on the host ->
  `test: opened a conversation with 'end_trask'`.
- **The peer follows it**: the client logged `dialogue: following the peer's conversation`
  ~2 s later, and its block read `remote: active 1 partner 'end_trask'` while the host read
  `remote: active 0` (owner, not follower). The token propagates and the spectator opens
  the SAME conversation against its own copy of the NPC.
- **The BG3 rule holds**: with the host mid-conversation, the client's own
  `talk carth` was REFUSED (`could not open a conversation`). One conversation per session,
  confirmed end to end rather than by construction.
- ⚠ **The spectate LINE is still empty** for base-game dialogue (`line: ''` on both sides).
  That is the known TLK-StrRef gap from addendum 0h, now confirmed to still be open: the
  conversation itself is shared correctly, the transcript text is not.

### THE BUG: a host that CRASHES leaves the client spectating forever

Killed the host process mid-conversation. Twenty seconds later the client still read
`remote: active 1` and never logged the close — meaning its own reply control
(`SetCanSendDialog(false)`) stayed off **permanently**. The explicit-disconnect hole was
fixed this morning (0j); this is the same hole on the TIMEOUT path, which is the one that
actually happens when a game crashes or a router blips.

Cause: `CoopClient` detects link loss and clears `_localPeerId`, but `_dialogue` kept its
last value, and the agent re-publishes that level state every tick — so
`IsHeldByAnotherThan(0)` stayed true forever. Fixed: the link-loss branch now drops the
token to `Idle`, since a token from a session you are no longer in is not authoritative
(the host re-publishes a fresh one on reconnect). Regression test
`AClientStopsFollowingWhenTheHostVanishesMidConversation` covers the crash case
specifically — its sibling covers the polite Bye path.

### Also found: the PC ignores script-queued move actions

`walk` dispatches `ActionMoveToLocation` on the PC successfully (no failure code) and the
player does not move — twice, to two different destinations. Phantoms obey the same call
because `ClaimCreature` takes them out of AI control first (commandable, cleared queue,
movement bit). Not chased further: movement replication is already proven from the
phantom side, and `StartDialogueWith` makes the SERVER walk the player into range, so the
dialogue test never needed it. Recorded so nobody assumes the hook is broken.

270 tests; all three builds clean.

---

## Addendum 0n — a repeatable verification battery, and staging real fights, 2026-08-06

The recurring complaint in every addendum above is the same: the interesting bugs only
appear with two real games running, and there was no repeatable way to get there. Two
things fix that.

### Staging a fight without a different save

`ActionAttack` is now bound (`Odyssey.AttackTarget`, routine 37 checked against
`CSWVirtualMachineCommands::ExecuteCommandAttack 0x0052E7F0` before dispatch, OBJECT_SELF
redirected to the attacker). `blockpoke attack <attackerId> <targetId>` orders a real
combat round — swings, rolls, damage, and the AI carrying on afterwards.

**This is why no new save was needed.** The blocker was always that the Endar Spire's Sith
are inert scripted placements (7.0), so nothing could exercise swings or aggro. Downloading
a save was considered and rejected: it is an untrusted binary loaded into the user's game
and it would not be reproducible for anyone else. Commanding the attack works on ANY save,
including the two that already exist, and it is the route 7.0 itself recommended.

Also added, same shape: `blockpoke talk <tag>` (a real conversation, exactly what a click
sends), `talk close` (ends one — required for a repeatable pass), `walk <x> <y>`,
`hitphantom <slot> <amount>`, and the read-outs `combat` / `dialogue`.

### tools\verify.ps1 — 21 checks, PASS/FAIL, idempotent

Extended the existing battery from 7 checks to 21, covering hook installation, the LAN
beacon, presence, combat participation, animation replication, dialogue exclusivity and
quests. Pids are auto-detected; exit code is the failure count.

**Verified: 21/21 passing, twice in a row against the same session.** Two of those had
never been machine-checked before — `swing animations reach the peer` (the attack hook made
this possible at all) and the LAN beacon end to end.

Three lessons the harness itself taught, all worth keeping:

- **A check that greps the whole log can pass on the PREVIOUS run's line.** One did, and
  reported success for a conversation that never opened. Log checks now only read lines
  written after the action (`LogHasSince` / `WaitForLog`).
- **A verification pass must not damage what it verifies.** Three points of test damage a
  run walked the player down to 1 hit point and then into the engine's temp-death — at
  which point forwarded damage is *deliberately* discarded, so the combat check failed and
  blamed the forward. The battery now revives and sets a known health first, and restores
  it after.
- **Judge on state, not on log lines, when the log cannot tell you the truth.**
  `StartDialogueWith` returns true once the request is SENT; the engine's refusal happens
  later and server-side, so "opened a conversation" in the log proves nothing. The
  exclusivity check now asserts what the client is actually talking to.

### Known gap found while doing this

A CONNECTED agent stops republishing discovered servers into the block (only the menu-mode
loop does it), so the in-world F9 browser panel reads empty mid-session. The beacon itself
is fine — verified end to end here. Minor: the browser's job is finding a server before you
join. Recorded, not fixed.

⚠ Also observed once: a client instance vanished right after connecting, with no crash-probe
report. Not reproducible across four subsequent launches. Recorded rather than explained.

### 0n.1 — the browser stays fed mid-session; battery at 22/22

**Fixed the gap 0n recorded.** `CoopCommandDispatcher` already ran a LAN listener for the
whole session (it is what `servers` and `connect #n` read) but never published the result
into the block — only the menu-mode loop did. So the F9 panel went empty the moment you
joined and stayed that way, which reads as "the browser is broken" rather than "the list
stopped refreshing". One call per 150 ms tick, and the publish signature-checks itself, so
it writes nothing when the list has not changed. Verified live: the in-world list now reads
`Lanik [END_M01AA] 10.5.0.2:27140` mid-session.

Two harness defects fixed in the same pass, both of which had failed checks for reasons
unrelated to the feature under test:

- **The swing check attacked whatever enemy was published first**, which on a fresh session
  was ~98 units away: the character spent the whole timeout walking and never swung. It now
  picks the NEAREST enemy from the presence slots (which carry positions), and the check
  reports the distance so a slow pass is legible rather than mysterious.
- **The beacon check took the first datagram it heard.** A plugin auto-starts an agent per
  instance and those advertise too, so it kept measuring a menu-mode host with no module.
  It now selects the beacon whose game port is the session under test.

**22/22, three consecutive runs on one session.** 270 unit tests.

### ⚠ And one genuine crash the battery found — FIXED, see §0i

Closing a conversation from the frame hook crashed the host in
`CGuiInGame::UpdateShotCamera`, with `CSWCameraOnAStick::Destructor` / `SetDialogCamera` /
`ShowDialogReplies` on the stack. `CloseDialogue` is already guarded on an active
conversation, so the problem was WHEN it ran, not whether, and it implicated a production
path (`DialogueSpectateController` closes the same way on three edges). Deliberately not
guessed at here; the RE pass is in **§0i**, and the answer was not the Escape path but the
SERVER side — the client close left the server's dialog running, and its next line landed on
a conversation whose participants had just been invalidated.
