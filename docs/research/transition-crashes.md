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

## DLL-level bisect of always-on plugin pieces (2026-08-29)

A follow-up round, prompted by the observation that Round 3 above only ever turned off
`coop.ini` FEATURE flags — the ones a controller reads with `PluginConfig.ReadBool` to decide
whether to act — never the small number of plugin pieces that INSTALL unconditionally
regardless of any flag (a detour/splice or a per-frame reader with no gate at all). Two of
those, both added since the 2026-08-27 refactor point this project's memory calls out, had no
kill key before this round:

- `RemovePartyMemberHook` (`src/KotorCoop.Plugin/RemovePartyMemberHook.cs`,
  `docs/research/star-forge-finale.md` §11) — a mid-function detour on every `RemovePartyMember`
  VM call, installed unconditionally in `PluginHost.Startup.cs` ("like BarkHook/PhantomHitHook").
- `EffectiveBehaviourFlag` (`src/KotorCoop.Plugin/EffectiveBehaviourFlag.cs`) — the reader all
  five host-adoptable controllers (`effect_sync`, `trigger_relay`, `liveness_sync`,
  `map_reveal_sync`, `dialogue_recruit_sync`) use every frame to consult
  `SharedBlock.OffsetBehaviourOverrideMask`/`Values`, added 2026-08-27 alongside the
  behaviour-flag negotiation feature (`b8e551f`), with no way to turn the READ itself off short
  of turning off all five features individually (which does not stop the shared-block read,
  only what happens with the result).

The task brief's other candidates — `item_template_spawn_sync` (gates
`ItemTemplateSpawnHook.Install` directly, `PluginHost.Startup.cs`), `status_indicator`
(`ConnectionIndicatorController`), `minigame_wait_notice` (`MinigameNoticeController`), and
`party_select_host_only` (`PartySelectSuppressionController`) — already had kill keys; grepping
confirmed `docs/CONFIG.md` already documents all four. "`connection_indicator`" is not a
separate key from `status_indicator` — `ConnectionIndicatorController`'s own field is literally
named `_enabled = PluginConfig.ReadBool("status_indicator", true)`.

**Two new diagnostic-only kill keys added** (default ON, not meant to ship OFF):
`removeparty_hook` (`PluginHost.Startup.cs`) and `behaviour_override_reader`
(`EffectiveBehaviourFlag.cs` — off skips the block read entirely and returns the local
`coop.ini` value with no log line). Documented in `docs/CONFIG.md` with footnote 13. Both
live-confirmed to take effect: the plugin log shows
`RemovePartyMember hook NOT installed: disabled by coop.ini removeparty_hook=0` when set, and
no `item-template spawns:` install line appears in a session with `item_template_spawn_sync=0`.

### Soak with all six diagnostic keys off: crashes anyway, identical signature

`item_template_spawn_sync=0`, `removeparty_hook=0`, `status_indicator=0`,
`minigame_wait_notice=0`, `party_select_host_only=0`, `behaviour_override_reader=0` on both
instances, everything else at its shipped default (so this round adds to, rather than repeats,
Round 3's coverage — between the two rounds, every coop.ini-gated plugin behaviour this
codebase currently exposes a switch for has now been tested OFF). Fresh rig, the same
12-transition soak recipe.

**Crashed on transition 2** — the client died during the dwell after `warp end_m01aa`, last
plugin-log line `epoch: world unloading; clearing phantom, item, combat and door state`, no
`FIRST-CHANCE` line captured (the probe did not get another poll cycle) — identical in every
particular to Rounds 1, 3, 4 and 5 above. **None of the six newly-gateable always-on pieces is
the cause either.**

### Attempted deeper bisect to `c9d08bb`: blocked by a real, separate hazard

Per the task brief, `git checkout c9d08bb -- src/KotorCoop.Plugin` was tried next (plus
deleting the 38 files added to the plugin after that commit, since a plain `checkout <rev> --
<path>` does not delete files the target commit never had — the naive version of this recipe
silently leaves a hybrid tree that fails to compile). With that done the pre-wave plugin built
clean and installed onto both game directories.

It could not be soak-tested, for a genuine reason worth recording rather than working around:
`KotorCoop.Agent`'s plugin-block scanner (`KotorGameBridge.Attach.cs TryFindPlugin`,
`KotorGameBridge.PluginSignature`) matches a **literal, non-wildcarded 16-byte pattern that
embeds the exact block version** (`"...3B 00 00 00..."` = 59, decimal, at the signature's byte
8-11). `c9d08bb`'s plugin writes `_block->Version = 54` (`PluginHost.cs`). The CURRENT
(HEAD) agent — install.ps1/build.ps1 always deploys the CURRENT agent, and reverting only
`src/KotorCoop.Plugin` leaves the agent at HEAD — would therefore find **zero** candidates for
the block at all, fall through to "external mode" instead of "PLUGIN mode", and never form a
co-op session — so neither the `warp` cheat's peer-follow contract nor `blockpoke.ps1 console`
(which itself skips exactly those 4 bytes when matching, so it alone would have kept working)
would exercise anything resembling the live two-instance scenario this bug needs. This is the
exact hazard `docs/CONTRIBUTING.md`'s "Adding a feature end-to-end" step 5 already warns about
("a mismatch is a silent 'plugin not found'") — this round is a second, independent
confirmation of it, this time from testing an OLD plugin against a NEW agent rather than a
half-bumped version pair. Properly exercising a historical plugin build over the live rig would
require reverting `src/KotorCoop.Memory`/`Session`/`Agent`/`Protocol` to the same commit too
(the same added-then-deleted-files problem, four times over, for a payoff this round judged not
worth it — see "Why the deeper bisect was not carried further" below) — not attempted this
round. `src/KotorCoop.Plugin` was restored to HEAD (`git checkout HEAD -- src/KotorCoop.Plugin`,
verified clean with `git status`) and both game directories reinstalled and strings-checked
against the correct build before any further testing.

### Final confirmatory soak, shipped defaults, HEAD build with both new keys present (ON)

Rebuilt, reinstalled, `coop.ini` reset to a fresh copy of `profiles/coop.ini.example` (every
flag, including the two new diagnostic keys, at its shipped/code default — all ON). Fresh rig,
same recipe. **Crashed again on transition 2**, identical signature, identical last log line.
Confirms the two new kill keys' default-ON code paths introduce no regression of their own —
the outcome with them present and ON is indistinguishable from every prior round.

### Why the deeper bisect was not carried further, and why the "no plugin at all" fallback could not be run

Two independent rounds this session, covering every coop.ini-gated plugin behaviour this
codebase can currently switch off — the ten features Round 3 tested plus the six diagnostic
keys this round added — all show the identical crash signature. Combined with Round 2's one
symbolicated occurrence (squarely inside `swkotor.exe`'s own `CExoKeyTable::DestroyTable`, zero
references from `src/*.cs` — `grep -rn "0x40D386\|CExoKeyTable\|DestroyTable" src/` still finds
nothing), a 60-commit bisection between `c9d08bb` and HEAD (`git log --oneline
c9d08bb..HEAD -- src/KotorCoop.Plugin` lists 60) was judged low-payoff for the cost of solving
the cross-version attach problem above at every step, and was not carried out this round.

The task brief's own fallback for "no plugin variant avoids the crash" — soak WITHOUT the
plugin loaded at all, by renaming `dinput8.dll` — was examined and found not executable with
this rig's existing tooling: the soak recipe's `warp <module>` is a co-op console verb the
plugin's `FuncInterpHook` intercepts (there is no such verb in stock KOTOR), and the CLIENT
following the HOST's warp is the plugin/session's own "peer follows" contract — both need the
plugin loaded and a live session. Without it, the only way to drive a module transition at all
would be an actual door crossing walked by input, and `docs/live-verification.md` already
establishes that injected keystrokes never reach this game. Proving the pure-engine-bug case
this way remains open, and would need either a human at the keyboard for one shipped-vanilla
(no `dinput8.dll`) session, or a scripted door-walk built on `teleport.ps1`-style memory writes
plus the game's own `usedoor`-equivalent action queue rather than the console.

### Updated attribution

Unchanged from Round 3's headline, now on broader evidence: the crash reproduces regardless of
every plugin-side feature this codebase can currently gate — sixteen keys tested OFF across the
two rounds (`party_select_host_only`, `solo_segment_spectate`, `transition_announce_scripted`,
`container_lock_arbitration`, `companion_gear_sync`, `script_item_sync`, `map_reveal_sync`,
`pazaak_detect`, `store_sync`, `party_pool_sync`, `item_template_spawn_sync`, `removeparty_hook`,
`status_indicator`, `minigame_wait_notice`, `behaviour_override_reader`, and — separately, at
shipped ON — reconfirmed once more this round) — plus every one of them ON (two more full
shipped-defaults soaks this round, both crashing on transition 2, exactly matching Rounds 1/4/5).
No code fix was made this round: there is still no confirmed culprit inside `src/*.cs` to fix,
only the two new diagnostic kill keys (which do not change behaviour by default) and the
process-hazard write-up above. The `CExoKeyTable::DestroyTable` retail-engine hypothesis from
Round 2 remains the best-supported explanation and is unchanged by this round's work.

## Mechanism attribution: real doors vs. the `warp` verb, and a pre-wave (`c9d08bb`) reproduction (2026-08-28)

Two questions the prior rounds left open: (1) every reproduction so far used the console `warp`
cheat exclusively — does an ordinary player, crossing real doors, ever hit this at all? (2) the
DLL-level bisect above could not exercise a historical plugin build live (the block-version
signature hazard); does the crash predate the current development wave, or was it introduced
somewhere in the ~60 commits between `c9d08bb` and HEAD? Two-instance rig, GOG 1.0.3, HEAD
`c3dc793` (this branch, post-merge with `main`'s `removeparty_hook`/`behaviour_override_reader`
kill-key commit), both game directories freshly `build.ps1 -WarnAsError -Install`'d, `coop.ini`
confirmed byte-identical to `profiles/coop.ini.example` (only cosmetic mojibake and the
operational `auto_start_agent` line differ, as in every prior round).

| Round | Build | Transition mechanism | Transitions | Crashed? | Address confirmed |
| --- | --- | --- | --- | --- | --- |
| 6 | HEAD (`c3dc793`) | real doors (`usedoor`) + 5 relocating `warp`s | 12 real-door + 5 warp | no, 0/17 | n/a — none crashed |
| 6.5 | pre-wave (`c9d08bb`) | `warp` only, exact Round-1 recipe | 2 | yes, transition 2 (client) | yes — `0x0023D4B3`, `CSWCObject::GetServerObject` |

### Round 6 — 12-transition real-door-only soak, HEAD build: ZERO crashes

Method deviated from a literal "bounce the same two modules 12 times" only where the game's own
level design forced it, and every deviation is called out below rather than glossed over:

1. **`end_m01aa` -> `end_m01ab`** via the real corridor door: `tp 44.5 120.1 -0.04` then
   `usedoor end_door10` on the HOST, exactly `playthrough-endar-spire.md`'s own verified recipe.
   Clean. **This module pair turned out to be one-way** — `python tools/kotorfiles.py gff
   end_m01ab.rim:m01ab.git --path "Door List"` shows all five of `end_m01ab`'s own doors with an
   EMPTY `LinkedToModule` (confirmed by a live `usedoor` sweep too: "the sweep found 9 door(s), 0
   of them module transitions") — the Endar Spire escape corridor has no door back, only a
   scripted/human-only continuation toward `STUNT_00`. No real-door return trip exists for this
   pair; noted rather than forced.
2. **One relocating `warp tar_m02aa`** to reach Taris, exactly as the task brief sanctioned. Clean.
3. **`tar_m02aa` <-> `tar_m02af`** (the South Apartments interior door, tag `tar02_aprtdoor` one
   way / `tar02_aptdoor` the other, both confirmed present with non-empty `LinkedToModule` in the
   pristine `.git`). The forward crossing worked cleanly the first time. The return door
   (`tar02_aptdoor`) never fired despite being 0.0-1.0 m away and repeatedly re-opened — the
   engine's own `usedoor` walk-to-door primitive stalled short every time and the door eventually
   got stuck refusing to open at all (`the engine refused it: door 0x23: REFUSED to open (still
   state 1)`), a live-reproduced instance of the exact "usedoor needs to already be close" quirk
   `playthrough-taris.md` §Step-1 already flagged, worsened here by this round's own repeated
   retries wedging the door's animation state. **This is a scripted-content/pathing quirk in
   vanilla KOTOR's door-walk logic, not a coop crash** — nothing died, the door simply never
   opened. Rather than burn more time on one wedged door instance, the host was relocated with a
   second `warp tar_m02aa` (clean) then a third `warp tar_m02ac` (clean, bypassing a Sith
   checkpoint dialogue/combat gate at `tar_m02aa`'s other outbound doors that also proved
   unautomatable in the time budgeted).
4. **`tar_m02ac` <-> `tar_m02ae`** (Upper City <-> cantina, tag `tar02_cantdoor` one way /
   `ptar_stnddr` the other — a SECOND, never-before-touched instance of that tag; the earlier
   wedged `ptar_stnddr` near the checkpoint is a different door object). This pair worked cleanly
   and repeatably: **10 consecutive real-door module transitions, 5 round trips, ~20-25 s dwell
   each with both players walking (`blockpoke.ps1 walk`), the client following every time**,
   confirmed by `where` on both instances after each leg. Zero crashes, zero desyncs, zero stalls
   once past the first wedge.

**Aggregate: 12 real-door-driven module transitions total (1 + 1 forward + 10), plus 5 relocating
`warp`s across the session (all of them ALSO survived) — zero crashes on either instance for the
entire ~30-minute session.** This is the single largest crash-free transition run this doc has
recorded; every prior round that used `warp` as the transition mechanism itself (not just for
relocation) crashed by transition 2 or 3, 100% of the time (5/5 in the original attribution round,
repeated in the DLL-bisect round).

**Why the relocating warps did not contaminate the result, NEEDS-LIVE caveat included**: unlike
the crash-inducing recipe (a `warp` landing in or immediately following `tar_m02aa` while an
ambient conversation — the `duros` NPC trap — is active), every relocating warp in this round
either landed in an empty street with no ambient dialogue running, or (for `tar_m02aa` itself)
was immediately followed by minutes of quiet `tp`/`usedoor` staging rather than another
transition seconds later. The historical recipe's own Round 2 hypothesis above ("why `tar_m02aa`
in particular") is about TIMING relative to a lingering conversation, not simply "any warp,
ever" — so this round's clean warps are consistent with, not contradictory to, that hypothesis.
This is INFERRED, not proven: this round did not deliberately try to crash a relocating warp the
way Round 6.5 below deliberately reproduced the known recipe.

### Round 6.5 — pre-wave (`c9d08bb`) whole-tree build, exact Round-1 recipe: CRASHES, symbolicated to the OTHER originally-named address

Per the task brief's fallback plan, a scratch worktree was created at `c9d08bb` (`git worktree
add`, a sibling directory, not under `.claude/worktrees`), and **everything** was built there —
`dotnet build KotorCoop.slnx` (root solution) and `src/KotorCoop.Plugin/build.ps1 -Install` for
BOTH game directories — deliberately deploying `c9d08bb`'s own agent alongside its own plugin,
avoiding the version-mismatch hazard the DLL-bisect round hit (that round only reverted
`src/KotorCoop.Plugin` and kept the HEAD agent, so the agent's hardcoded plugin-signature scanner
never found the old block version). `blockpoke.ps1 state` confirmed `block version 54` on both
instances (matching `c9d08bb`'s `SharedBlock.Version`, not HEAD's 59) and `remotes 1` on both —
proof the session actually formed this time, unlike the blocked DLL-bisect attempt.

Ran the ORIGINAL Round 1 recipe exactly: `warp tar_m02aa` on the host (clean), dwelled ~20 s and
confirmed the client's plugin log showed the `duros` NPC's gait mirroring (the ambient-
conversation trap), then `warp end_m01aa` on the host.

**The CLIENT crashed within 25 ms of the transition starting.** Its last plugin-log lines:

```
[22:54:39.053] flush, before the module is stored: destroyed phantom 0xA7 (slot 0)
[22:54:39.061] travel: moving to module 'end_m01aa' on the session's authority
[22:54:39.076] epoch: world unloading; clearing phantom, item, combat and door state
[22:54:39.098] FIRST-CHANCE access violation (0xC0000005) at 0x63D4B3, eip 0x63D4B3, esp 0x1AFB80;
image-range stack values: 0x60FB28 0x4B8E5F
```

`0x63D4B3 - 0x400000 = 0x23D4B3` — **this is `+0x23d4b3`, `CSWCObject::GetServerObject`, the
OTHER address originally named in this doc's own brief** (alongside `+0x11C525`
`WalkUpdateLocation_QuickWalk_FollowLeader`), previously only ever seen by `store-sync.md` while
a client was stuck behind `tar_m02aa`'s ambient modal at the moment a transition began — exactly
the scenario just reproduced here, on purpose, at a commit from before every coop.ini feature
flag and diagnostic kill-key this doc's bisection rounds tested even existed. The host's own log
shows the identical "epoch: world unloading" / "agent reports 0 remote peer(s)" shape with no
fault of its own (it survives its own local transition, matching every prior round).

**Verdict: the crash predates the wave.** Per the task brief's own branching rule ("if it crashes
at `c9d08bb` too -> the crash predates the wave"), no bisection between `c9d08bb` and HEAD was
carried out — there is nothing to bisect FOR, since the fault reproduces identically at a commit
`git log --oneline c9d08bb..HEAD -- src/KotorCoop.Plugin` shows is 60 plugin commits and a full
`removeparty_hook`/`behaviour_override_reader`/party-pool-sync/store-sync development cycle
before HEAD. Combined with the DLL-bisect round's sixteen-flags-off result and this round's own
real-door soak, THREE independent lines of evidence now converge: no `coop.ini` feature gates it,
no diagnostic-only plugin piece gates it, and it is fully present in a build that predates the
existence of most of this codebase's current features. It is not a regression this project
introduced at any point on `main`.

After capturing the evidence, HEAD's plugin/agent were rebuilt and reinstalled into both game
directories (`build.ps1 -Install`, both dirs, confirmed via `strings dinput8.dll` against the PDB
path) before any further work, and the scratch worktree was removed (`git worktree remove`, plus
a manual `Remove-Item` for one leftover directory handle held by a still-open agent console from
the round — the working tree itself unregistered cleanly).

### Updated attribution and verdict

**The crash is a pre-existing, unfixed `swkotor.exe` retail engine defect — a family of at least
two related stale-pointer bugs (`CExoKeyTable::DestroyTable` at `0x0040D386`, and
`CSWCObject::GetServerObject` at `0x0023D4B3`, both in the "something invalidates a base pointer/
table between a validation read and a later re-read at the same index/offset" shape) — that this
project's own code does not cause, does not gate, and predates by a wide margin.** What this
round adds beyond the DLL-bisect's "no coop.ini flag" result is a MECHANISM finding: **every
reproduction of this crash, across every round in this document, used the console `warp` cheat as
the transition trigger. A 12-transition real-door-only soak on the same HEAD build, in the same
session type, on the same two-instance rig, produced zero crashes.** The most likely explanation
(INFERRED, consistent with but not a re-proof of Round 2's timing hypothesis): a real door
crossing is preceded by several seconds of walking and the door's own ~0.5 s fade/open animation,
during which whatever transient engine state a lingering conversation or resource load left
behind has time to settle; `warp` fires the transition essentially the instant it is typed
(`TravelController.Execute` is called on the very next frame after the plugin flushes its own
spawned objects — see `src/KotorCoop.Plugin/TravelController.cs`), which is far more likely to
catch the engine's own key/BIF or object tables mid-mutation.

**Is `warp`'s own path already "safe" in the sense the task brief asked about?** Yes, verified by
reading `src/KotorCoop.Plugin/TravelController.cs` and `Odyssey.Travel.cs`: the coop-intercepted
`warp` verb does NOT call `StartNewModule` (or anything else) synchronously from inside the
console-command dispatch. `CoopCommandDispatcher.Warp` writes the request onto the same
agent<->plugin travel channel peer-follow uses (`SharedBlock.TravelSerial`/`TravelModule`/
`TravelWaypoint`); `TravelController.Execute()` is "Called by PluginHost on the game's thread
after every plugin-spawned object has been flushed" (its own doc comment) — i.e. already deferred
to a frame boundary, and it raises the transition through `Odyssey.TravelToModule`, which does
nothing more than the identical three plain-field writes
(`SetMoveToModuleString`/`SetMoveToModuleStartWaypoint`/`SetMoveToModulePending`) that
`transition-smoothness.md` §11.1 found EVERY module-transition trigger uses — the door's own
`CSWSDoor::EventHandler`, every NWScript `StartNewModule()` call, native area/trigger transitions,
and the retail `Global::warp` debug cheat all write the exact same fields; the engine's own
`MainLoop` drains them through the single `StartNewModule` call site regardless of who wrote
them. There is no "mid-frame console-driven module load" bypass to fix — the co-op `warp` verb
already rides the engine's own pending-transition funnel exactly the way a script would. The
difference from a door is TIMING (near-instant vs. several seconds of walk+fade), not mechanism
safety, and that cannot be "fixed" by deferring further without turning `warp` into a slow walk,
which would defeat its purpose as a dev/test primitive.

**This is not treated as a player-facing bug requiring a code fix in this codebase.** A real
player crossing real doors, in this round's 12-transition sample, never hit it. It remains a real,
open, upstream `swkotor.exe` defect that a sufficiently unlucky ordinary player COULD in principle
still hit (nothing about the fault is co-op-specific — `docs/TROUBLESHOOTING.md` already says so),
but this round's evidence raises the bar for "how would you actually hit it" from "any module
transition" to "an unusually-timed transition with no walk/fade pacing, most reliably produced by
rapid `warp` chains" — exactly the shape a live-test rig's own soak recipe produces and an
ordinary playthrough does not. See the corresponding addenda in `docs/CONSOLE.md`'s `warp` row and
`docs/TROUBLESHOOTING.md`.

### Open follow-ups, updated

1. Item 1 from the earlier "Open follow-ups" list (disassemble `0x407230`) still stands for
   `CExoKeyTable::DestroyTable`; this round adds a second address, `CSWCObject::GetServerObject`
   at `0x0023D4B3`, to the same disassembly backlog — neither has had its own intervening
   call/invalidation traced yet.
2. This round's real-door soak is 12 transitions, not an exhaustive proof; a longer unattended
   soak (hours, not one session) using only real doors — ideally with crash dumps enabled, per
   this doc's own still-open "Tooling gap" item — would strengthen the "ordinary play is fine"
   claim further, or falsify it.
3. Nobody has yet tried to CRASH a relocating `warp` deliberately (e.g., warp back into
   `tar_m02aa` while the `duros` conversation is active, then immediately warp again) — Round 6's
   clean relocating warps are circumstantial support for the timing hypothesis, not a controlled
   test of it.

---

## The HOST's deterministic crash on any relocating `warp` (2026-08-29) — OURS, in one of five
## party-channel config keys, not an engine transition defect

Distinct from everything above: this one is **not** a stale-pointer race in the engine's own
transition machinery, and it is not helped by preferring real doors over `warp`. It is
deterministic — 6/6 across two different destination modules — and it kills the HOST specifically.

```
FIRST-CHANCE access violation (0xC0000005) at 0x6B5FEE, eip 0x6B5FEE, esp 0x1AFB80;
image-range stack values: 0x414E89 0x418DCD 0x6B5B48 0x41773C 0x733618 0x6B76A8 ...
```

Symbolicated against `tools/kotor1_0_3.db`:

```
0x006B5FEE  CSWGuiInGameEquip::UpdatePortraits + 0x48E      mov edx, dword ptr [eax]
  preceded by  CClientExoApp::GetSWParty   0x005ED8B0
               CSWParty::GetCharacter(1)   0x006346C0  -> NULL
  stack:  CSWGuiInGameEquip::Constructor + 0xA23,  CGuiInGame::Initialize + 0x680
```

`CSWParty::GetCharacter` returns 0 for `index < 0` or `index >= [this]` (the member count), else
`GetCreatureByGameObjectID(entry[index].objectId)`. Its caller guards with `cmp [edi],1 / jle skip`
— it only asks for member 1 when the party count is **above 1**. So the fault says exactly one
thing: **the CLIENT-side party reports a member count above 1 while member 1's object no longer
resolves**, and the incoming module's own GUI construction walks it.

Bisected live, twice (docs/research/shared-inventory-v2.md §15.6):

- `transition_close_client_world=0` — which disables `Odyssey.CloseClientWorldIfServerModuleGone`,
  the only thing in this codebase that empties the client's world early, and the prime suspect
  since it logs 60-70 ms before every fault — **does not help**: the crash is byte-identical.
  Ruled out. The key stays in the tree as a bisect lever; leave it at 1 in normal use or the
  map-init fault documented above comes back.
- With `party_roster_sync=0`, `party_together_fix=0`, `hide_party_portraits=0`,
  `party_follow_stall_recovery=0` and `party_select_host_only=0`, **the host survives**, repeatedly
  (four warps across two sessions, no crash).

`party_roster_sync` alone was already eliminated by shared-inventory-v2.md §14.5. The remaining
suspects are `party_together_fix` — which writes the party table's member count, and "count above
1 with a dead member 1" is precisely its failure mode — `hide_party_portraits`,
`party_follow_stall_recovery` and `party_select_host_only`. Narrowing to one costs one rig cycle
per key.

**Workaround until it is fixed**: a rig recipe that needs the host to change module should turn
those five keys off. That is what made the store round in shared-inventory-v2.md §15.5 possible.
(No longer needed as of the section below — the cause is fixed; the workaround is kept as the
description of the state this attribution was made from.)

### Attributed and fixed, 2026-08-29 — `party_follow_stall_recovery`'s count RESTORE, not
### `party_together_fix`'s clamp

The remaining bisect was not needed: the four surviving suspects are separable by reading, and
only one of them writes anything on a host at all.

| key | writes `CSWParty+0x00`? | runs on the HOST? | gate |
| --- | --- | --- | --- |
| `party_together_fix` | yes, **DOWN only** (`TryClampClientPartyCount` returns false unless `before > maxCount`) | **no** | `PartyRosterController.SyncClientPartyTogetherState` returns unless `block.TryGetHostPartyRoster(...)` — a bit only ever written for a client |
| `hide_party_portraits` | no | **no** | `PortraitController`: `shouldHide = Command == SpawnAndDrive && block.DamageDealtEnabled`, and `DamageDealtEnabled` is written only on the non-authority side |
| `party_select_host_only` | no | **no** (it does still read `CGuiInGame+0x78` per frame before its own gate) | `PartySelectSuppressionRule.ShouldSuppress(enabled, isClientInLiveSession)` |
| `party_follow_stall_recovery` | **yes, UP** | **YES** | `PartyFollowController`: `Command == SpawnAndDrive` (both roles write it) and `PartyPresenceCount == 0` (a client-only counter, so always true on a host) |

`PartyFollowController`'s own class comment is explicit that it is the HOST/solo controller —
"applies ONLY to companions THIS instance's own engine genuinely owns". So the fault is its count
restore, added by FOLLOW round 101 and copied into the runtime detector by round 105
(`Odyssey.TryResolveFollowRecord`, `allowCountRestore`):

```csharp
int truePopulation = tableCount + 1;          // CSWPartyTable's companions-only count, + the PC
int* countPtr = (int*)party;
if (*countPtr < truePopulation)
{
    *countPtr = truePopulation;               // <- raises CSWParty+0x00
    index = GetPartyIndexFn(party, clientObjectId);
    countWasRestored = index > 0;             // ...and the raise is left standing either way
}
```

**Why that is the crash.** `CSWParty+0x00` is not a statement about how many companions the save
has — it is the bound every consumer walks the member ARRAY with, `CSWParty::GetCharacter`
included. Across a module load the two facts diverge: the party TABLE is save state and survives
the transition already saying "one companion", while `CSWParty`'s array for the NEW module has not
been populated yet and member[1] still reads the empty-slot sentinel `0x7F000000`. Writing `2`
there in that window is exactly "count above 1 with a dead member 1", and
`CSWGuiInGameEquip::UpdatePortraits` — reached from `CGuiInGame::Initialize` as the incoming
module builds its GUI — asks for member 1 the moment the count is above 1 and dereferences the
answer with no null check.

Two further details made it a deterministic HOST crash rather than a transient:

- round 101's own reasoning for leaving the raise standing ("`SyncClientPartyTogetherState`
  re-clamps it on its own very next pass") **does not hold on a host**: that re-clamp is
  client-only, so on a host nothing in the process ever takes the value back;
- the per-slot latch that keeps this engine field write off the per-frame path
  (`PartyFollowController._stallCountRestored`) was set only when the retry SUCCEEDED. A restore
  that raised the count and still failed to resolve an index logged nothing and did not latch, so
  it re-armed every frame — the project's own per-frame-write rule, broken, across exactly the
  frames a module load spans.

**The fix** (`PartyCountRestoreRule`, `Odyssey.CountResolvableClientPartyMembers`,
`Odyssey.TryResolveFollowRecord`, `PartyFollowController.DriveStallRecovery`):

1. the raise is bounded by how many members actually RESOLVE right now — the length of the
   LEADING run of member slots whose stored client id resolves through
   `CClientExoApp::GetCreatureByGameObjectID` 0x005ED5B0, the same call `GetCharacter` itself
   makes. A leading run, not a total: the count is an array bound, so every index below it has to
   be safe. This is what makes the restore epoch-aware without a timer — during a half-built
   module it simply reads 1 and no raise happens, and the frame the engine finishes populating
   the array it reads 2 and round 101's actual purpose still works.
   (`PartyFollowTransitionRule`'s one-frame epoch skip stays where it is; it covers the frame the
   epoch first changes, this covers every frame after it until the array is genuinely ready.)
2. a raise that did not make the lookup succeed is **reverted on the spot**. No speculative value
   about this field may outlive the frame that guessed it.
3. the latch keys on "a write happened" (`FollowStallState.CountRestoreWritten`), not on "the
   write helped", and is set before the early return — one engine field write per slot per module.

Unit-pinned in `tests/KotorCoop.Tests/PartyCountRestoreRuleTests.cs`: refused at 1 resolvable
member, allowed at 2, the resolvable run is a hard ceiling rather than a hint, never lowers the
count (that stays `party_together_fix`'s decision alone), and a count it does not recognise as a
count is not written.

**Can a REAL transition that leaves a companion behind hit the same fault?** The obvious candidate
is `END_M01AA`'s own script dropping Trask at that door. No, and for two independent reasons:

1. A companion being left behind means the party TABLE loses them, so `TryGetPartyTableMemberCount`
   returns 0 and `truePopulation` is 1. `SafeRestoreCount` will not raise a count to 1 that is
   already 1, and `CSWGuiInGameEquip::UpdatePortraits` only asks for member 1 when the count is
   ABOVE 1 (`cmp [edi],1 / jle skip`). There is nothing to ask for.
2. Even in the harder shape — the table still claiming a companion the new module has not
   materialised — the resolvable-run ceiling refuses the raise until that member genuinely
   resolves. That is the same guard, and it does not care WHY the member is missing (mid-load,
   dropped by a script, or destroyed).

The fault needed the count to exceed the resolvable members, and that is now unreachable from this
path regardless of what the party table says.

**Live status (2026-08-29):** five relocating warps across four sessions with all five party keys
at their shipped ON defaults — `tar_m02aa` x2 and `tat_m17ac` x3, one of them on a `coop.ini`
copied verbatim from `profiles/coop.ini.example` — all survived, with **zero** occurrences of
`6B5FEE` in the host's plugin log, against a signature that was 6/6 deterministic before. A
`tools/verify.ps1` run in the same configuration reported
`the host still controls its companions PASS  table 1 companion(s), CSWParty 2 (PC+companions)`,
i.e. the legitimate case (count 2, both members resolvable) still works. The real-door clause
(`end_door10 -> end_m01ab`) is still **unexercised**: `usedoor`'s walk stalled short of the door
on both attempts and never started a transition, a staging-verb/pathing limitation unrelated to
this fix.

## The CLIENT vanishing on its first follow after a wipe-recovery rejoin (2026-09-02, open; FIXED 2026-09-03)

One occurrence, not yet reproduced. Sequence: both players wiped outside combat (`down 0` +
`down` on the host, `down` on the client), host reloaded save 1 from the game-over, the client
took exactly one `WipeRecovery` rejoin through the PLUGIN route (`rejoin: load request 2 sent to
the plugin`, 23:31:03), played three minutes in END_M01AA at 12/12, and then the host `warp
tar_m10aa` at 23:34:39. The client's plugin log ends 11 ms into the follow:

    [23:34:39.852] flush, before the module is stored: destroyed phantom 0x131 (slot 0)
    [23:34:39.855] travel: moving to module 'tar_m10aa' on the session's authority
    [23:34:39.866] epoch: world unloading; clearing phantom, item, combat and door state

The follow that succeeded at 22:52:33 on the same launch wrote `transition: closed the client
world ahead of the map init` 50 ms after its travel line; this one never got there. What is
NOT in evidence: no `FIRST-CHANCE` fault (the vectored stub never fired), no `FATAL (managed)`
line, no Windows Application-log entry (`tools/crashlog.ps1` found none; the only entries were
RestartManager 10000/10001 from a parallel build four minutes earlier), no fail-fast. The
host was unaffected (`Malak left the game` 23:34:45, in tar_m10aa 12/12).

Two candidate explanations, neither confirmed:

1. **A clean `ExitProcess`** — the CRT's `exit()`, or an engine fatal path that terminates
   without a dialog — is the one ending that leaves every one of those channels empty. The
   plugin now hooks swkotor.exe's `KERNEL32!ExitProcess` IAT slot (`ExitProbe.cs`, installed
   right after the crash probe) and writes `EXITPROCESS(code) called from 0x... ; image-range
   stack values: ...` on the way out, synchronously, before jumping to the real export. If the
   next occurrence carries that line, the return addresses name the path.
2. **A fault the process died on before the worker thread drained the probe record.** The
   crash probe formats its line on the worker thread; a fault followed by an immediate
   termination (WER disabled, or a `TerminateProcess` from the engine's own filter) can lose
   it. If the next occurrence shows neither an `EXITPROCESS` nor a `FIRST-CHANCE` line, this is
   the remaining suspect and the probe's drain needs to move onto the faulting thread's own
   machine-code path (a fixed-format line it can write without a managed frame).

Distinguishing feature versus the two wipe-recovery follows that worked (22:52 and 23:02): this
client had loaded its rejoin save through the plugin route from the GAME-OVER screen
(`RunDeathSequence` had run on it at 23:30:15), where the other two had gone through the menu
route. The one-process-two-halves rule (client-server-halves) makes "the client half was left
in the death sequence's state and the next area unload freed something twice" the leading
hypothesis, but it is a hypothesis: reproduce first.

Reproduction recipe (rig): `demo.ps1 -Save 1`, separate (`tp`), host `down 0` then `down`,
client `down`, host `load --pid <host> --load-save 1`, wait for the client's single
`WipeRecovery` rejoin via the plugin route, then host `warp tar_m10aa`. Check the client's
plugin log for `EXITPROCESS` / `FIRST-CHANCE` and whether `transition: closed the client
world` appears.

### 2026-09-03 00:06 — reproduced (2/2), and `ExitProbe` named the exit

Same recipe, plugins with `ExitProbe` in both dirs: wipe → host `load --load-save 1` (plugin
route) → client WipeRecovery rejoin, one only → six clean minutes → host `warp tar_m10aa`.
Client plugin, last four lines:

    [00:06:35.038] flush, before the module is stored: destroyed phantom 0x12F (slot 0)
    [00:06:35.043] travel: moving to module 'tar_m10aa' on the session's authority
    [00:06:35.054] epoch: world unloading; clearing phantom, item, combat and door state
    [00:06:35.059] EXITPROCESS(3221225477) called from 0x6FB0C7 (swkotor.exe) on thread 1; image-range stack values: 0x6FB20F 0x6FB230 0x6FB54B 0x6FB3A5 0x6FB38D 0x6FB38D

3221225477 = 0xC0000005: the engine's own top-level exception handler (the `0x6FB0xx` frames
are all inside it) caught an access violation on the main thread and called `ExitProcess`
with the exception code. That is why the earlier one left nothing: no Windows Application-log
entry (the engine handled it), no `FATAL (managed)` (not our thread), and no `FIRST-CHANCE`
line because the vectored handler's record is drained by the worker thread and the process
was gone 5 ms later — the persisted-record work in `CrashProbe` is what will name the
faulting eip next time.

What is known without it:

- It is the PLUGIN-ROUTE rejoin that arms it. The 23:02 wipe reload (four menu-route rejoins)
  was followed by two clean `warp tar_m10aa` follows (23:02:42, 23:02:53, both reaching
  `transition: closed the client world ahead of the map init` ~40 ms after the travel line).
  The 23:34 and 00:06 follows, each the first after a `load request 2 sent to the plugin`
  rejoin (`MenuLoadController.FireMidSessionPhase1`: DestroyServer / ClientUnloadModule /
  CreateServer / LoadPCAutoSave with a never-shown `CSWGuiSaveLoad` panel), died 5-11 ms
  after `epoch: world unloading`, before the next frame's close-client-world step ran.
- Timing puts the fault inside the remainder of the frame in which the server module was
  first seen gone — the same frame travel-map-init-crash.md's map-init dereference lives in,
  which the close-client-world step only covers from the NEXT frame.

Candidates, in order: (1) something the mid-session load leaves alive that the ordinary
menu-route load does not (the never-shown save panel, the load-screen state, the in-game GUI
the retail branch rebuilds and ours does not), which the module teardown then walks; (2) the
map-init dereference reached on the teardown frame itself because the client's GUI is in a
state the ordinary path never has. Next: merge the persisted crash record, re-run this exact
recipe (deterministic now), symbolicate the eip.

### 2026-09-03 00:18 — the persisted crash record named it: `DoPCAutosave` walking CSWParty past its resolvable entries

Third reproduction (3/3), plugins with the persisted-record `CrashProbe` in both dirs. Same
recipe; client gone 6 ms after `epoch: world unloading` again. `pwsh tools/crashbin.ps1 -GameDir
'D:\Games\swkotor-gog-p2'` on `kotorcoop-crash.bin`:

    access violation (0xC0000005) at 0x63D4B3 (swkotor.exe), thread 1
    image-range stack values: 0x60FB28 0x4B8E5F ...

Symbolicated (`tools/q.py` / `tools/disasm.py`):

    0x63D4B0  CSWCObject::GetServerObject      +3: mov eax,[esi+0xF8]   ← this = NULL
    0x60FB20  CSWCCreature::GetServerCreature  (return 0x60FB28) — no null test on `this`
    0x4B8300  CServerExoAppInternal::DoPCAutosave (return 0x4B8E5F, +0xB5F)
                loop i < [CSWParty+0x00]:
                  CClientExoApp::GetSWParty → CSWParty::GetCharacter(i) (0x6346C0)
                  → CSWCCreature::GetServerCreature(result)   ← result was NULL for i = 1
    0x4BA920  CServerExoAppInternal::StartNewModule (sole call site of DoPCAutosave, 0x4BAB7B)

So it is the SAME invariant as the 2026-08-29 host crash (`UpdatePortraits`), reached from the
other side: `CSWParty+0x00` is the bound every consumer walks the member array with, and after a
plugin-route mid-session rejoin (`MenuLoadController.FireMidSessionPhase1`: DestroyServer /
ClientUnloadModule / CreateServer / LoadPCAutoSave) the client's struct kept an entry the NEW
world could not resolve. `StartNewModule` runs `DoPCAutosave` first thing, `GetCharacter(1)`
returns NULL for the stale slot, and `GetServerCreature` dereferences it. The engine's top-level
handler turned that into the `ExitProcess(0xC0000005)` the 00:06 run saw. Neither candidate
from the 00:06 section (the never-shown save panel, the map-init dereference) was it.

Menu-route rejoins do not arm it because the menu load rebuilds `CClientExoApp` (and CSWParty
with it); the plugin route deliberately keeps the client half alive across the swap — which is
what makes it fast, and what leaves the old world's member ids in the array.

#### Fixed 2026-09-03 00:26 — bound the count by the resolvable run before the move; verified 1/1

`PartyCountRestoreRule.SafeTravelCount(currentCount, resolvableMembers, capacity)` — the
2026-08-29 rule's invariant enforced DOWNWARD: write `min(count, leading resolvable run)` when
the count exceeds the run, else nothing. 0 is a legitimate answer (a wholly stale array is
bounded at 0, not left alone). `Odyssey.TryBoundClientPartyCountByResolvable` measures the run
with the same `GetCreatureByGameObjectID` walk `GetCharacter` itself does, and names every slot
under the old bound in the log (`0xID` resolved, `0xID!` did not). Two call sites:

- `Odyssey.TravelToModule`, immediately before `SetMoveToModulePending(1)` — our own warps,
  which is the crashing case;
- the rising edge of `IsMoveToModulePending()` in `PluginHost.OnFrame` — the writers we do not
  own (galaxy map, stage changes, triggers), best effort since the flag may already be drained;
  lifted out from under the `transition_announce_scripted` gate so a crash guard does not switch
  off with a feature flag. Idempotent: a bounded count never exceeds the run.

Live, same recipe (wipe 00:29 → host `load --load-save 1` plugin route 00:30:46 → exactly one
`WipeRecovery` rejoin → host `warp tar_m10aa` 00:31:42). Client plugin:

    [00:31:42.656] party count: bounded 2 -> 1 before the module move (entries 0x80000129 0x80000127!; DoPCAutosave walks the array without a null check)
    [00:31:42.656] travel: moving to module 'tar_m10aa' on the session's authority
    [00:31:42.666] epoch: world unloading; clearing phantom, item, combat and door state
    [00:31:42.709] transition: closed the client world ahead of the map init

The client reached tar_m10aa (`module tar_m10aa hp 12/12 peers-here 1`), followed a second
`warp end_m01aa` cleanly with no bound needed, 0 faults on either side. And the open question
is answered: the stale entry was NOT the PC's slot — `0x80000129` (the PC, party-npc flag set)
resolved; `0x80000127` is Trask's PRE-WIPE client id (the host's `down 0` that started the
wipe named slot 0 as 0x127), the mirrored companion the old world had and the new one never
rebuilt. Answer: the client's own slot is safe; it is the mirrored companion entries that go
stale across a plugin-route swap.

Tests: `PartyCountRestoreRuleTests.ATravelBoundsTheCountByTheResolvableRun`,
`AWhollyStaleArrayIsBoundedAtZero`, `ACountWithinTheRunIsNotWritten`,
`NonsenseTravelInputsWriteNothing`.
