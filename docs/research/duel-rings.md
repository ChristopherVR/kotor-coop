# Duel-ring solo parking (2026-09-21)

The shipped duel scripts are catalogued for the existing solo-segment detector. This remains a
bounded detection change: it does not write solo mode, faction, party state, or any new packet
field. Taris now also has a direct player-pin boundary, which closes the zero/one-companion gap
for that ring. Korriban remains on the catalogue and RemovePartyMember-burst path until a
player-targeted boundary is proven for its scripts.

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

## Korriban start-cue research

The Korriban resources do expose a narrower story cue, but it is not a safe substitute for a
player pin read. `korr_m35aa.rim:m35aa.git` has a `TriggerList` entry whose
`TemplateResRef` is `k35_init_duel` at the duel-room coordinates (185.303, 74.0769, 6.15).
The compiled `k35_init_duel.ncs` is therefore the actual entry boundary: it is a trigger script,
not an area OnEnter or a module transition. Its action stream resolves `kor35_sithduel` and
`kor35_captive9`, then stages the duel conversation. The same GIT has a separate
`k35_init_duelend` trigger for the exit boundary.

The party-removal script is a later, script-level cue. `k_pkor_partyleav.ncs` writes the
transient module globals `KOR_REMOVE_PCS` and `KOR_REMOVE_PC`, loops over active party slots,
and calls the engine removal action for each valid slot. `KOR_DUEL_NUM` is read by
`k_pkor_duelnum.ncs`/`k_pkor_hasduels.ncs` and written by `k35_duc_fight.ncs`; it is a duel
progress counter, not a documented “duel active” latch. `kor35_duelconv.dlg` ends through
`k_pkor_duelend`, whose `SetMinOneHP` operand is the named duel actor rather than
`GetFirstPC`.

This leaves two evidence-backed facts: the trigger volume is the real Korriban start boundary,
and the removal loop is the real companion-strip boundary. The plugin has no verified, safe read
of trigger overlap or a stable PC/local duel flag, so no Korriban position heuristic, global
write, or speculative native trigger hook is added. With zero or one companion, Korriban remains
NEEDS-LIVE for a future trigger observation that can be tied to the exact `k35_init_duel` event.

`SoloSegmentCatalogue.BaseModules` now includes both modules. The regular roster-drop edge
remains catalogue-gated, and `solo_segment_any_module` remains OFF by default. Existing
Star Forge and Leviathan handling is unchanged.

## Dark-side companion authority

The summit resources independently establish `ChangeToStandardFaction(Jolee, HOSTILE_1 = 1)`
and the matching Juhani call in `unk_m44ac/test3.ncs`. The pure faction rule treats known
standard faction 1 as authoritative hostility even if the party table still contains the body
during the script's short remove window. A complete runtime promotion remains staged: the
receiver must map the host body to its retained local body across movement, health, death, and
damage paths before the host can omit it from the companion channel. Unknown faction reads remain
conservatively party-owned.

This is intentionally bounded. No VM hook, raw stats write, packet/shared-block layout change,
or new party slot is introduced. The pure decision is covered by `PartyCensusRuleTests`; the
retained-body receiver handoff is **NEEDS-LIVE** until the summit path confirms roster removal,
follow-state clearing, and every owner-id combat translation together.
