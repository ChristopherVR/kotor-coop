# Duel-ring solo parking (2026-09-21)

The shipped Taris duel has an exact player-pin edge for the zero/one-companion case. Korriban's
similarly named duel triggers are a separate NPC-versus-NPC scene; the player final-test path
now uses its own exact dialogue-start and area-return signals, with live validation still pending.

## Evidence from the shipped KOTOR resources

The resource reader was run against the installed GOG data at
`C:\GOG Games\Star Wars KOTOR (GOG)\modules`.

| Module | Compiled script | Evidence | Result |
| --- | --- | --- | --- |
| `tar_m02ae` | `k_ptar_duelring.ncs` | one `RemovePartyMember` action and `SetMinOneHP(GetFirstPC(), TRUE)`; `k_ptar_duelend.ncs` and `k_ptar_duelend2.ncs` contain the clear branch | Forced-solo candidate; the PC pin edge is the zero/one-companion trigger |
| `korr_m35aa` | `k_pkor_partyleav.ncs` | one `RemovePartyMember` action inside the party-leave loop; `k_pkor_duelend.ncs` has a `SetMinOneHP` call but no `GetFirstPC` operand | Forced-solo candidate; no verified PC-pin trigger |

The one removal opcode in each script is inside a loop over active party members. The runtime
hook therefore sees one call per removed companion; the existing two-call debounce starts the
segment only when at least two companions are actually removed within its 1.0 second window.
For Taris, `SoloSegmentMachine.NoteDuelPlayerPin` reads the local PC's `CSWSObject +0x200` and
starts only on the rising edge of the exact player-targeted pin in `tar_m02ae`. The matching
clear script ends the segment even when the roster remains empty or contains one companion.
The edge is consumed while the party-selection GUI is open, so a manual solo/bench action does
not replay as a story event. Korriban's named-creature pin is intentionally not treated as a
player boundary.

## Korriban trigger re-evaluation (2026-09-23)

The trigger named `k35_init_duel` is a real trigger boundary, but it is **not evidence that the
player has entered a forced-solo duel**. The installed `korr_m35aa` resources identify it as a
scripted fight between two NPCs:

- `m35aa.git` places `k35_init_duel` at (185.303, 74.077, 6.15); its `.utt` is one-shot and
  runs `k35_init_duel.ncs`. That script checks that the entering object equals
  `GetPartyMemberByIndex(0)` and that local boolean 0 on the trigger has not already been set.
  On the successful path it sets that bit, changes factions for `kor35_sithduel` and
  `kor35_captive9`, and signals user-defined event 135 to the Sith duelist.
- The two `.utc` blueprints mark both actors `Min1HP=1`; neither is a party member. The trigger
  script does not pin the PC. The duel actor's user-defined handler equips its lightsaber, while
  `kor35_duelconv.dlg` is a short, textless scripted interlude: after its first entry delay it
  runs `k_pkor_duelend.ncs`, which clears min-one-HP on `kor35_captive9` and commands
  `kor35_sithduel` to attack that captive. The captive's user-defined event 1007 cancels the
  Sith's combat, sends it back to its waypoint, changes its faction, and resumes that dialogue.
- The separate one-shot `k35_init_duelend` trigger runs `k_pkor_initduele.ncs`. Its success path
  has its own local-boolean-0 guard, requires the captive object to be valid and
  `KOR_END_HOSTILE` false, then starts `kor35_duelconv`. That dialogue and its scripts are part
  of the same NPC-on-NPC sequence, not a player-duel end signal.

This resolves the earlier ambiguity in the trigger names and in `k_pkor_duelend`: their combat
roles are the Sith duelist and the captive NPC. A `k35_init_duel` trigger observation must not
start solo spectating, including when the player has zero or one companion. The exact local-bit
transition can establish that the script passed its one-shot/global/object guards, but it still
only establishes this NPC scene; no player-duel classification follows from it.

### The separate player final-test party strip

The actual Korriban party strip is a different story beat. In `kor35_utharwynn.dlg`, entry 93 is
Uthar's “Then we go now.” line and runs `k_pkor_partyleav.ncs`; entry 94 then runs
`k_pkor_finalleav.ncs`. The first script stores `GetPartyMemberCount()` in module global
`KOR_REMOVE_PCS`, stores each active companion's NPC index in `KOR_REMOVE_PC<n>`, and calls
`RemovePartyMember` for each one. This is the final-test departure, separate from the NPC duel
triggers above. With one companion it emits only one removal call; with none it emits none, so
the existing two-call burst detector cannot be the only way to recognize this beat.

The installed story data also gives a bounded return/restore path. In `kor35_enter.ncs`
(`m35aa.are` OnEnter), when `KOR_FINAL_TEST > 3`, the script sets `KOR_ADD_PARTY`, re-adds
each saved NPC index with `AddPartyMember`, and moves the restored companions to the academy
waypoints. In the `korr_m39aa` final-test dialogue, the terminal exit scripts unlock the acid
door and starmap door and write distinct progression values: `k39_yth_leave` sets
`KOR_FINAL_TEST=5`, `k39_uth_leave` sets 6, and `k39_yth_redeem` sets 7. The m36aa area-entry
script `k36_pkor_enter` also gates door/escape behavior on the final-test and party-restoration
globals. These resources establish story progression and party restoration. The runtime
observers described below now identify their exact dispatches; a persistent quest value alone
is not treated as proof that a new solo segment began or ended.

The final-test outcomes also do not expose a Taris-style PC min-one-HP edge. The m39aa combat
handlers that call `SetMinOneHP` clear it on `OBJECT_SELF` and/or `kor39_yuthura`; none of the
installed m39aa scripts in this pass target `GetFirstPC()`. The hostile branch is separate:
`kor36_endsith1.dlg` has replies that run `k_pkor_end_host.ncs`, which closes the academy door,
sets `KOR_END_HOSTILE`, changes factions, and starts combat against the PC. On later entry to
m35aa, `kor35_enter` uses that flag to run scene cleanup, including removing the captive NPCs.

The local evidence was read from the installed GOG KOTOR resources: `m35aa.git`, the trigger
`.utt` files, `kor35_sithduel.utc`, `kor35_captive9.utc`, the named `.ncs` scripts and `.dlg`
files, plus `m35aa.are`, `m36aa.are`, and the `korr_m39aa` final-test dialogue. The conditional
control flow was checked against the compiled scripts; names of intermediate values emitted by
the decompiler are not treated as source evidence.

`KOR_DUEL_NUM` is a separate student/prisoner-combat counter: it is read by
`k_pkor_duelnum.ncs`/`k_pkor_hasduels.ncs` and written by `k35_duc_fight.ncs`; it is not a
reliable active flag for the final test or the player duel.

The true player final test is distinct from the NPC duel trigger. Its runtime detector is now
implemented in `KorribanFinalTestMachine` and wired to the shared dialogue-action observer plus
the verified `CSWSArea::EventHandler` OnEnter call site:

1. The exact `k_pkor_partyleav` dialogue action in `korr_m35aa` begins the latch. The observer
   runs before the VM call, so the host snapshots the local active-party NPC-slot mask before
   the script removes anyone and publishes the solo-owner level before a transition can begin.
   This also covers zero companions, where no `RemovePartyMember` call or roster-drop edge
   exists. Korriban is excluded from the generic removal-burst and roster-drop catalogue so an
   unrelated party edit in m35 cannot start this segment.
2. The latch remains active only through the verified `korr_m36aa`, `korr_m37aa`, `korr_m38aa`,
   `korr_m38ab`, and `korr_m39aa` route. An empty module read holds the latch during a transition;
   an unrelated module, session loss, or changed `WorldLoadSerial` clears it. Ordinary module
   transitions do not change that serial, so they do not look like a save/load.
3. The exact `kor35_enter` OnEnter dispatch in returned `korr_m35aa` is accepted only after one
   of those route modules was observed. Completion then waits for both a readable party roster
   and readable `KOR_FINAL_TEST > 3`, and requires every slot present before departure to have
   returned. Missing roster/global reads keep the exact return cue pending rather than consuming
   it.
4. The agent publishes an explicit valid/host role word in shared-block version 73. Korriban
   starts only with positive host proof and a fresh agent heartbeat; the absent host-party-roster
   bit is never treated as host evidence. If either exact observer fails its native anchor check,
   the Korriban feature stays disabled rather than starting a segment it cannot close.

The machine and role publication tests cover exact cue matching, zero/one-companion paths,
original-slot identity, route ordering, unreadable return evidence, stale dispatches, session/load
resets, generic-trigger isolation, and host/client publication/clear behavior. The NativeAOT x86
warnings-as-errors build passed on 2026-09-23. Root validation passed 97 focused tests plus
10 liveness/role-publication tests. Commit `980cabc` was deployed to both games with native
SHA-256 `C03D00D3A5C2045613B41B5ED4F2B3EE0E777312401414C862C89463AC9A4F63`.

### Staged live boundary checks (2026-09-23)

Both observer installs passed their byte checks in both engines. With zero companions,
the actual Uthar dialogue entry 93 began the host latch at 09:09:52 with mask `0x000`.
The joiner announced spectating, and the script-driven transition reached `korr_m39aa`.
Runtime fields in the tomb showed host `LocalInSoloSegment=1`, role word 3 (host), and
joiner solo level 0, role word 1 (client). Returning early to the academy with
`KOR_FINAL_TEST=1` kept the host latch active. After another tomb visit and staging the
completion value to 4, the native `kor35_enter` return ended it at 09:12:44; the joiner
announced the segment over.

A clean checkpoint reload then staged prestige/eligibility and recruited Carth in slot 2.
The actual departure at 09:14:46 captured mask `0x004`, removed Carth, and again started
only the host latch. After staging the completion value to 4, the native academy return
at 09:15:47 restored slot 2 and ended the latch. Both engines had one active companion
(their own local Carth object IDs) and both solo levels were 0. The load serial stayed
unchanged through ordinary module travel in each run.

These checks verify zero/one-companion entry, continued ownership across the scripted
trip, refusal to end on an early return, and restoration/release at the exact return cue.
They used staged prestige/eligibility, completion globals and return warps. The natural
tomb puzzles, combat, end-state branches and visual spectator presentation remain unverified.

## Dark-side companion authority

The summit resources independently establish `ChangeToStandardFaction(Jolee, HOSTILE_1 = 1)`
and the matching Juhani call in `unk_m44ac/test3.ncs`. This is a proposed authority signal only;
no runtime faction classifier ships. A complete runtime promotion remains staged: the receiver
must map the host body to its retained local body across movement, health, death, and damage
paths before the host can omit it from the companion channel. Unknown faction reads remain
conservatively party-owned.

This is intentionally bounded. No VM hook, raw stats write, packet/shared-block layout change,
or new party slot is introduced. No disconnected classifier ships; the retained-body receiver
handoff is **NEEDS-LIVE** until the summit path confirms roster removal, follow-state clearing,
and every owner-id combat translation together.
