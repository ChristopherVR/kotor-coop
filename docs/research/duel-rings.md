# Duel-ring solo parking (2026-09-21)

The shipped Taris duel has an exact player-pin edge for the zero/one-companion case. Korriban
also strips party members for its final-test departure, but the similarly named duel triggers
are a separate NPC-versus-NPC scene. Its zero/one-companion final-test path still needs a
verified start/end signal that remains valid across the Korriban area transitions.

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
globals. These resources establish story progression and party restoration, but they do not
provide a player-readable, verified runtime boundary that can keep a spectate segment continuous
across the academy, valley, and final-test areas. No Korriban solo-segment trigger is added on
this evidence alone.

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

The true player final test is therefore distinct from the zero/one-companion gap at the NPC
duel trigger. The shipped start and restore scripts give better candidates than that trigger,
but there is not yet a runtime detector or live coop proof.

### Candidate runtime cue and remaining proof

The narrowest existing-code design to investigate is a story-specific cue from
`k_pkor_partyleav`, with its removal data correlated against the real removal hook, rather than
broadening the current two-call burst rule:

1. On the engine thread, read `KOR_REMOVE_PCS` and the corresponding `KOR_REMOVE_PC<n>` values
   alongside each captured `RemovePartyMember` call in `korr_m35aa`. Require the count to match
   the party roster observed immediately before the script and require each captured NPC slot to
   match the saved NPC index. For one companion, the existing sampled `>=1 -> 0` roster-drop
   path may already start a segment if it observes the transition in `korr_m35aa`; the two-call
   burst path alone cannot. The correlated story cue would avoid relying on a transient sample
   and qualify the zero-party case too: even with no NPC to remove, the other player may still
   need the local solo owner classified for spectating.
2. Retain this specifically identified segment across only the verified final-test modules;
   do not use the general machine's current module-change end rule for it. On return to
   `korr_m35aa`, require both `KOR_FINAL_TEST > 3` and the `KOR_ADD_PARTY` restore path, then end
   after the saved companion actually appears in the local roster. The final-test threshold
   alone is persistent progression state, so it must not end a segment in an unrelated module
   or before a roster restoration is observed.

This is a proposal, not an implementation. It still needs a scan proving these globals and
their ordering are exclusive to the identified story path, confirmation that local global reads
on both peers observe the required values at the removal hook, and live zero/one-companion runs
through the return. In particular, the zero-companion start cue must come from the story action
itself or an independently verified dialogue/script-context signal; there is no removal or
roster edge to infer it from. `Odyssey.Globals` currently has engine getter signatures used by its write
primitive but no general read API; global deltas also exist in the agent-side state bridge, but
their timing relative to the native removal hook is not established. If the global/roster
correlation is stale or ambiguous, add a source-specific script-context signal only after its
engine path is independently identified. Do not implement a guessed VM hook or extend the NPC
duel trigger.

`SoloSegmentCatalogue.BaseModules` includes `korr_m35aa` because the final-test departure script
can remove the party there. A single removal can be caught by the existing sampled roster-drop
edge if that transition is observed; the burst detector itself still requires two calls. The
regular roster-drop edge remains catalogue-gated, and
`solo_segment_any_module` remains OFF by default. Existing Star Forge and Leviathan handling is
unchanged. The NPC-duel triggers documented above are not additional solo-segment boundaries.

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
