# Transition crashes under SHIPPED DEFAULTS — attribution round (2026-08-28/29)

Whether the two module-transition crashes flagged in `54e7994` (`+0x23d4b3`
`CSWCObject::GetServerObject`, `+0x11C525` `CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader`)
happen with a shipped `coop.ini` (every optional feature at its code default, `store_sync=0`,
`party_pool_sync=0`), and — since every reproduction this round landed on a THIRD, already
half-documented address instead — whether any of the eleven coop.ini feature flags on the
project's own bisection list are the cause. Two-instance rig (`tools/demo.ps1`), GOG 1.0.3,
both game directories freshly `build.ps1 -WarnAsError -Install`'d, agent rebuilt per
`docs/live-verification.md`. Tag key: **VERIFIED** = read out of the binary or a live log,
**NEEDS-LIVE** = reasoned, not yet confirmed, **INFERRED** = a plausible deduction.

## Headline

**The crash reproduces under shipped defaults, and is not caused by any tested coop.ini flag.**
Five separate two-instance sessions, each starting from a fresh `warp` (the ordinary `~`-console
cheat, not a door or script), crashed one side of the pair by the second or third module
transition — a **5-for-5** hit rate once a warp landed in or immediately followed `tar_m02aa`.
One of the five was symbolicated directly (the plugin's own crash probe caught it): **`0xC0000005`
at `0x0040D386`**, image offset `0x0000d386`, confirmed independently against the real Windows
Application log. That address is **not** either of the two named in this round's brief — it is
the previously-documented, previously-"fixed" **STUNT_00 escape-pod crash** from
`galaxy-map-and-party-select.md`, now reproduced by an entirely different, much simpler recipe
that never touches party-select, the galaxy map, or STUNT_00 at all. The other four occurrences
share an identical log signature but could not be individually symbolicated (see "Tooling gap,"
below) — they are recorded as *probably* the same fault, not proven to be.

None of `party_select_host_only`, `solo_segment_spectate`, `transition_announce_scripted`,
`container_lock_arbitration`, `companion_gear_sync`, `script_item_sync`, `map_reveal_sync`,
`pazaak_detect`, `store_sync`, or `party_pool_sync` gates it: one reproduction ran with every one
of those OFF simultaneously (see Round 3). `item_template_hook` does not exist as a `coop.ini`
key in this codebase — `grep -rn item_template_hook profiles/ src/` finds nothing, so it is
noted rather than tested.

## Method

12-transition soak: alternate `warp tar_m02aa` / `warp end_m01aa` / `warp tar_m10aa` /
`warp tar_m04aa` issued on the HOST only via `tools/blockpoke.ps1 console 'warp <module>'` (the
client follows automatically — the coop "peer follows" contract), ~20-25 s dwell per leg, both
players nudged a few metres with a small scratch helper (`rigwalk.ps1`, reusing
`tools/teleport.ps1`'s own creature-scan to find the live position, then
`tools/blockpoke.ps1 walk` to request an engine move a few metres away). A foreground loop
polled `Get-Process swkotor` every leg and stopped immediately on either instance vanishing.
Crash dumps could not be enabled and no debugger was available this round — see "Tooling gap."

## Round 1 — shipped defaults, full soak: CRASHES on transition 2

`coop.ini` on both instances confirmed byte-identical to `profiles/coop.ini.example` before
starting (only cosmetic `?`/`—` mojibake from a codepage mismatch and the operational
`auto_start_agent` key differed — no feature flag). `store_sync=0`, `party_pool_sync=0`,
everything else at its shipped default (mostly ON: `party_select_host_only`,
`transition_announce_scripted`, `container_lock_arbitration`, `companion_gear_sync`,
`script_item_sync`, `map_reveal_sync`, `dialogue_recruit_sync`, `party_together_fix`,
`effect_sync`, `trigger_relay`, `menu_button`, `pazaak_detect`, `solo_segment_spectate`).

Transition 1 (`warp tar_m02aa`): clean. Transition 2 (`warp end_m01aa`), issued while the
CLIENT was still mid an ambient conversation (`tar_m02aa`'s well-known `duros` NPC trap, already
called out in `docs/live-verification.md`'s store-sync rounds) — the **client crashed** during
the dwell. Its plugin log's last lines, verbatim:

```
[21:07:52.064] party drive: mirroring 1 of the host's companions
[21:07:52.065] triggers: standing in 2 generic trigger(s), first 0x2A - asking the enemy owner to run its OnEnter too
[21:07:52.078] travel: moving to module 'end_m01aa' on the session's authority
[21:07:52.092] epoch: world unloading; clearing phantom, item, combat and door state
```
— nothing after. The host, which warped cleanly and survived, DOES show
`transition: closed the client world ahead of the map init` (the existing `travel-map-init-crash.md`
Fix 1 guard, `Odyssey.CloseClientWorldIfServerModuleGone`, working correctly for its own local
transition). The dying client's log never shows that line before death, in this or any of the
four other reproductions below — see "What the existing InitializeMap guard does and does not
cover."

## Round 2 — `transition_announce_scripted=0` only: STILL CRASHES, and this one was symbolicated

Client's immediate-follow log line from Round 1 (`following Lanik to 'end_m01aa' IMMEDIATELY
(announced transition serial 2, not waiting for the module-change settle)`) implicated
`transition_announce_scripted` directly enough to test it alone first. Set to `0` on both
instances, fresh rig, same recipe (`warp tar_m02aa`, dwell until the client's own ambient
conversation is confirmed running via its plugin log, `warp end_m01aa` on the host).

**Crashed anyway** — this time the **HOST** (its own local `warp`, not a peer-follow at all).
The plugin's crash probe (`CrashProbe.cs`) got one more poll cycle before the process died and
wrote the fault to the log before vanishing:

```
[21:17:01.127] travel: moving to module 'end_m01aa' on the session's authority
[21:17:01.141] epoch: world unloading; clearing phantom, item, combat and door state
[21:17:01.147] agent reports 0 remote peer(s)
[21:17:01.148] 0 phantom(s) alive
[21:17:01.179] FIRST-CHANCE access violation (0xC0000005) at 0x40D386, eip 0x40D386, esp 0x1AF9B8; image-range stack values: 0x745898 0x745898 0x6FD8BD
```

Confirmed independently via `tools/crashlog.ps1` against the real Windows Application log
(~5 minutes later — see "Tooling gap"): `08/28/2026 21:17:01  module=swkotor.exe
code=0xc0000005  offset=0x0000d386`. `0x400000 + 0xd386 = 0x40D386` — matches exactly.

**This is the STUNT_00 escape-pod crash, byte-for-byte.** `galaxy-map-and-party-select.md`'s own
2026-08-28 entry recorded `FIRST-CHANCE access violation (0xC0000005) at 0x40D386` twice, with
`esp 0x1AF9B8` and image-range stack values `0x745898 0x745898 0x6FD8BD` both times — identical
in every field to this round's reading. That doc's own fix section ("`CGuiInGame+0x78` always-
non-null... fixed") root-caused a real, severe, independently-confirmed dangling-pointer write
storm in `PartySelectSuppressionController`, but its own honest caveat said the STUNT_00 crash
itself "could not re-reproduce... root cause plausible but not confirmed" **after** that fix —
and `store-sync.md`'s own later round claimed (without re-testing) that "this pass's own
STUNT_00-adjacent testing earlier the same session confirmed THAT crash is fixed/not
reproducing." **This round's reproduction — via a recipe that never opens the galaxy map, never
touches STUNT_00, and never calls `partyselect ok` — shows the write-storm fix did not close
this crash.** Either the two were only ever coincidentally correlated (the write storm was real
and is fixed; `0x40D386` had a separate, still-live cause the whole time), or the write storm can
still occur through some other path this round did not exercise. `docs/TROUBLESHOOTING.md` and
`galaxy-map-and-party-select.md` are corrected below.

### New RE: `0x0040D386` is inside `CExoKeyTable::DestroyTable`

Neither prior round had time to symbolicate. This one did, with `tools/q.py` against
`tools/kotor1_0_3.db` and `tools/disasm.py`:

```
CExoKeyTable  DestroyTable    0x0040D2E0
CExoKeyTable  GetTableIndex   0x0040D490
```

`0x0040D386` is `+0xA6` into `DestroyTable` (368 bytes, `0x40D2E0`-`0x40D490`). The faulting
instruction and its immediate context (VERIFIED, `tools/disasm.py 0x40D2E0 300`):

```
0x0040D36E  call 0x407230                    ; a resolve/lookup call into some global table
0x0040D373  mov  eax, [esp+0x14]             ; that call's own out-param
0x0040D377  cmp  eax, ebx                    ; ebx == 0 here
0x0040D379  jne  0x40d40d                    ; resolved -> take a different branch (not this fault)
0x0040D37F  mov  eax, [esi+0x10]             ; re-read the table's entry array base
0x0040D382  mov  ecx, [eax+edi+0x10]         ; re-index it at the SAME edi as before the call
0x0040D386  cmp  word ptr [ecx+4], bx        ; <-- FAULTS: ecx is null/garbage here
```

Fifteen instructions earlier (`0x40D330`-`0x40D33B`), the SAME expression —
`[[esi+0x10]+edi+0x10]` — was already computed and explicitly null-checked (`je 0x40d455`) before
`call 0x407230` ran. This is the classic shape of the bug class the project already has one fixed
example of (`PartySelectSuppressionController`'s dangling panel pointer): **a base pointer is
validated, an intervening call runs that can invalidate/reallocate the thing it points into, and
the code re-reads the SAME index off the SAME base afterward without re-validating.** Here the
base is `CExoKeyTable`'s own entry array (`[esi+0x10]`), not a GUI panel — this is the KEY/BIF
resource-file table, an entirely different engine subsystem from `InitializeMap` (map GUI) or
`GetServerObject`/`WalkUpdateLocation_QuickWalk_FollowLeader` (server objects, party-follow).
Whether the intervening `call 0x407230` is what actually resizes/frees the array, or something
else running between two frames does, is **NEEDS-LIVE** — this round's RE budget did not extend
to disassembling `0x407230` itself.

**Why `tar_m02aa` in particular is such a strong trigger, INFERRED**: `CExoKeyTable::DestroyTable`
plausibly runs as part of the ordinary per-module resource-directory swap
(`travel-map-init-crash.md` §3 step 7: "`LoadModule` then does resource-directory plumbing").
Every module transition goes through it. What appears to matter is *timing* — a conversation
(dialogue VO/lip resources are loaded through this exact key/BIF path) that is still winding down
or has JUST settled when the transition's own resource-table swap runs is a plausible way to
leave the table mid-mutation at the exact moment `DestroyTable` walks it. `tar_m02aa`'s
well-documented ambient `duros` conversation (`docs/live-verification.md`, several
`store-sync.md` rounds) is exactly that kind of lingering dialogue state — but Round 4 below
shows a transition with NO `tar_m02aa` in the immediate pair also crashed, so dialogue proximity
is a strong correlate, not a proven sole cause.

## Round 3 — ALL ELEVEN flags OFF simultaneously: STILL CRASHES

The decisive bisection. Both instances' `coop.ini`: `party_select_host_only=0`,
`solo_segment_spectate=0`, `transition_announce_scripted=0`, `container_lock_arbitration=0`,
`companion_gear_sync=0`, `script_item_sync=0`, `map_reveal_sync=0`, `pazaak_detect=0`,
`store_sync=0`, `party_pool_sync=0` — every flag on the task's bisection list plus the two
originally-suspected sync channels, all OFF at once. Fresh rig, identical recipe.

**Crashed anyway** — the CLIENT, on the exact same `warp tar_m02aa` -> (dwell, ambient
conversation confirmed running) -> `warp end_m01aa` recipe, with the identical
"epoch: world unloading" as the last log line and no event-log entry captured in time (see
"Tooling gap"). This is the single decisive data point: **with every listed coop feature
disabled, the crash still fires**, so none of them is the cause — the fault is in code that runs
regardless of which optional features are enabled (baseline session traffic: connect, phantom
mirror, party mirror, dialogue mirror, the always-on parts of travel).

## Round 4 — shipped defaults restored, full 12-transition soak: CRASHES on transition 3, no `tar_m02aa` in the pair

`coop.ini` on both instances reset to a byte-for-byte copy of `profiles/coop.ini.example`. This
run got further before crashing — transitions 1 (`tar_m02aa`) and 2 (`end_m01aa`) both completed
cleanly — but transition 3 (`warp tar_m10aa`, i.e. `end_m01aa -> tar_m10aa`, no `tar_m02aa`
anywhere in this specific leg) crashed the **client** during the dwell. Same signature: client's
last plugin-log line is `epoch: world unloading; clearing phantom, item, combat and door state`,
no `transition: closed the client world ahead of the map init` line beforehand, no
`FIRST-CHANCE` line this time (the process died before the probe's poll cycle ran again). This
shows the fault is not exclusively a `tar_m02aa`-ambient-conversation trigger — it can fire on an
ordinary module pair with no special content, though `tar_m02aa` transitions (which had just
happened two legs earlier in this same run) appear to raise the odds.

## Round 5 — repeat of Round 1's exact shipped-defaults recipe: CRASHES on transition 2 again

Third independent shipped-defaults session. Transition 1 clean, transition 2
(`tar_m02aa -> end_m01aa`) crashed the client during the dwell — identical signature to Round 1
and Round 4, again no `FIRST-CHANCE` line captured (probe did not get another poll cycle).

## Aggregate result

| Round | Config | Crashed? | Which side | Address confirmed |
| --- | --- | --- | --- | --- |
| 1 | shipped defaults | yes, transition 2 | client | no (signature match only) |
| 2 | `transition_announce_scripted=0` | yes, transition 2 | host | **yes — `0x0040D386`** |
| 3 | ALL 11 flags off | yes, transition 2 | client | no (signature match only) |
| 4 | shipped defaults | yes, transition 3 | client | no (signature match only) |
| 5 | shipped defaults | yes, transition 2 | client | no (signature match only) |

5 attempts, 5 crashes, none surviving past transition 3. Every one shares the identical log
signature (death immediately after "epoch: world unloading", no InitializeMap-guard line first).
The one that was symbolicated is `0x0040D386` — a pre-existing, already-partially-documented
engine crash, not either of the two addresses named in this round's brief, and not gated by any
tested coop.ini flag.

## What the existing `InitializeMap` guard does and does not cover

`Odyssey.CloseClientWorldIfServerModuleGone()` (`travel-map-init-crash.md`'s Fix 1, called
unconditionally every frame from `PluginHost.Frame.cs`'s `OnFrame`, before the epoch gate) closes
exactly one race: server module already null, client module still held, right before the
client's own `MainLoop` would dereference the null module inside `CSWGuiMainInterface::
InitializeMap`. It is doing its job — every host in this round that ran its own local transition
logged `transition: closed the client world ahead of the map init` and survived. But **that line
never appears on any of the five crashing instances, on any of the five occasions**, which means
none of these five crashes are the specific `InitializeMap` race that guard targets. `0x0040D386`
confirms this directly: it is inside `CExoKeyTable::DestroyTable`, an unrelated subsystem
(resource key/BIF table, not the map GUI), reading a different kind of stale pointer that this
guard's narrow scope (the client's own module/creature-id fields) does nothing to protect. The
other four unsymbolicated crashes may or may not be the same fault — see below.

## Attribution — the answer to "which feature causes them"

**None of the tested features.** `store_sync` and `party_pool_sync` were OFF in all five rounds
(never the suspected pair from `54e7994`'s own message). `transition_announce_scripted`,
`party_select_host_only`, `solo_segment_spectate`, `container_lock_arbitration`,
`companion_gear_sync`, `script_item_sync`, `map_reveal_sync`, and `pazaak_detect` were all
simultaneously OFF in Round 3 and it still crashed. `item_template_hook` is not a real
`coop.ini` key in this codebase. The crash is in pre-existing retail engine code
(`swkotor.exe`'s own image, `CExoKeyTable::DestroyTable` for the one confirmed instance) that
runs on the baseline co-op session path regardless of which optional sync channels are enabled —
consistent with, and now directly evidenced rather than merely grep-inferred, the standing
triage in `store-sync.md` for `+0x23d4b3` (`CSWCObject::GetServerObject`, "an unrelated
pre-existing engine bug not reachable from any store_sync code path") and the flag in
`party-reconciliation.md`'s direction for `+0x11C525`
(`CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader`, "not reachable from store_sync code
path").

**Neither of the two originally-named addresses was directly reproduced or symbolicated this
round.** Only `0x0040D386` was confirmed. The other four crashes share its outward signature but
their addresses are unconfirmed — they could be `0x0040D386` again, `0x0023d4b3`, `0x0011C525`,
or a fourth, still-undocumented fault; this round's tooling could not tell them apart (below).
The honest summary: **this class of transition crash — process dies immediately after
"epoch: world unloading," on either the host or the client, on an ordinary console `warp`, with
no InitializeMap-guard line first — reproduces reliably (5/5) under shipped defaults and is
provably independent of every coop.ini feature flag tested, but is not a single fully-identified
bug; it is at least one confirmed engine defect (`CExoKeyTable::DestroyTable`) and possibly a
small family of siblings in the same "module transition invalidates state a per-frame/per-call
read assumes is still live" shape that this project has already fixed one instance of
(`InitializeMap`) and partially fixed a second (`PartySelectSuppressionController`'s write
storm).**

## No code fix shipped this round, and why

The task brief's fix recipe (disable candidate flags, find the culprit, guard its code on module
epoch/`IsTransitionalModuleName`/`LiveObjectGate`) assumes the culprit is one of this project's
own features. Round 3 rules that out for every flag on the list. The one crash this round could
symbolicate is unambiguously inside `swkotor.exe`'s own image, in a resource-table subsystem
(`CExoKeyTable`) that nothing in `src/*.cs` calls, references, or has any prior doc coverage of
(`grep -rn "0x40D386\|CExoKeyTable\|DestroyTable" src/` — zero hits). Shipping a byte-patch
against undisassembled unmanaged engine internals (the intervening `call 0x407230` that
plausibly invalidates the array is not yet understood) on the strength of one symbolicated
occurrence would be exactly the kind of "researched offset, never verified live" mistake
`docs/ARCHITECTURE.md`'s engine-write rules exist to prevent. This is flagged as follow-up work,
not fixed.

## Tooling gap found this round

Two real gaps cost real time and should be fixed before the next crash-attribution round:

- **No crash dumps, no debugger available.** `tools/crashlog.ps1 -EnableDumps` needs
  Administrator (`Access to the registry key ... is denied`) and this session had no elevated
  shell. No `cdb.exe`/`windbg.exe`/`procdump.exe` was found anywhere on the machine (`Windows
  Kits\10` is installed but without the Debugging Tools component). Without a dump, individual
  crash attribution falls back entirely on whatever the plugin's own `CrashProbe` managed to
  write before the process died — which happened only once in five occurrences (Round 2). A
  future round with an elevated shell (or the debugging tools component installed) should enable
  dumps FIRST and re-run this exact recipe; a single `.dmp` from a repeat crash would settle
  whether the other four occurrences are `0x0040D386` again or something else.
- **Windows Error Reporting logs late, and seems to drop repeats.** The one event-log entry this
  round got (`08/28/2026 21:17:01`, matching Round 2's crash) did not appear in `crashlog.ps1`'s
  output until roughly five minutes after the process died, and despite four MORE crashes in the
  following twelve minutes, no further Application-log entries for `swkotor.exe` ever appeared —
  consistent with WER's own per-application dedup/throttle window for identically-bucketed
  crashes, though this was not confirmed against Microsoft's own documentation this round. Do not
  treat "only one `crashlog.ps1` line" as "only one crash": check `Get-Process` directly, not the
  event log, to know how many times the game actually died.

## Correction to prior documents

`docs/research/galaxy-map-and-party-select.md`'s "Fix (2026-08-28)" section and
`docs/TROUBLESHOOTING.md`'s "A partner's game crashes roughly 12 seconds after a scripted module
transition" section both currently read as more confident than the evidence now supports —
the former's own text already hedged this correctly ("could not re-reproduce... not fixed and
verified"), but the latter states outright that the crash "was... fixed 2026-08-28... confirmed
clean across three independent live re-verifications." This round's reproduction (Round 2, fully
symbolicated, same eip/esp/stack trace, via a recipe that touches none of the party-select code
the fix changed) shows that claim needs walking back. See the dated addenda added to both
documents alongside this one.

## Open follow-ups, ranked

1. **Disassemble `0x407230`** (the call between the two reads of `CExoKeyTable`'s entry array)
   to confirm what it does to the array and pin the exact invalidation — this is the single next
   step that would turn "plausible stale-array-index bug" into a confirmed root cause with a
   real fix shape (mirror `PartySelectSuppressionController`'s "touch at most once" pattern, or a
   re-validate-after-call guard, whichever the disassembly supports).
2. **Re-run this round's exact recipe with crash dumps enabled** (needs an elevated shell) to
   get a full `.dmp` for a repeat occurrence and settle whether the four unsymbolicated crashes
   are `0x0040D386` again or the two originally-named addresses.
3. **A dedicated attempt at the two originally-named addresses specifically**: `+0x23d4b3`
   (`CSWCObject::GetServerObject`) was previously seen while a client was stuck behind
   `tar_m02aa`'s ambient modal at the moment a transition began (`store-sync.md`); `+0x11C525`
   (`WalkUpdateLocation_QuickWalk_FollowLeader`) was previously seen when a HOST warped while a
   companion's follow-FSM was mid-walk (`store-sync.md`, flagged for `party-reconciliation.md`).
   Neither recipe was deliberately re-run this round — this round's soak found `0x0040D386`
   organically before either could be specifically targeted.
