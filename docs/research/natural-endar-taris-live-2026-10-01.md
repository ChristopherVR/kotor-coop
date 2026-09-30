# Natural Endar Spire to Taris, 2026-10-01

This two-player run started from copied genuine non-Jedi Endar saves in isolated game6/game7, rather than a staged Dantooine entry or edited eligibility globals. The host is Scoundrel 2, level 2; the joiner is Soldier 0, level 1. Original installations and their saves remain untouched. Full Taris progression and natural Council training are still outstanding.

## Original route and inputs

The stock route reached `end_m01ab` through original conversations, doors, combat and module transitions. The host acquired the gray footlocker's five computer spikes and seven parts through its ordinary loot GUI. An original security-computer overload attempt subsequently ended in genuine combat deaths; its intended enemy-kill outcome remains unproven. That failure and the resulting worldless-menu recovery gap are preserved in `artifacts/natural-campaign-v95/overload-wipe-evidence`.

After automatic recovery into the untouched pre-overload checkpoint, the host again acquired the original locker supplies. The damaged assault droid's original dialogue consumed three parts for shields and three for patrol. Its final computer entry required HWND-local Return messages, as the original camera feed had: buffered DirectInput Return did not complete the last entry. Normal Return ended the conversation, awarded 130 XP, and started the original patrol. The droid moved from approximately (76,67) to (83,53); all five real Sith IDs 4,7,10,13,16 disappeared from the current object array. Screenshots show the cleared room and surviving droid. No forced HP, quest-global, class, unlock or enemy-destruction writes were used.

Navigation used developer-directed `walkto`, which queues the engine's original pathfinding action. Off-walkmesh points were refused; valid intermediate points were used around the room's central obstruction. The next door was opened through observed ordinary GUI clicks. Carth's original conversation completed and awarded 250 XP.

The pod used developer-directed `useplaceable end_pod`, invoking the stock object's original interaction. The visible original reply `[Use the escape pod.]` was selected through the GUI. The original `STUNT_00` dream and subsequent `tar_m02af` apartment transition ran on both peers. This proves the original story scripts and choices, but does not certify every action as ordinary player mouse input. Dialogue speech was advanced using existing `skipline` and HWND-local Return; original reply conditions and scripts remained in control.

## Checkpoints, upgrade and character continuity

The safe Carth checkpoint completed at 2026-09-30 23:05:47 UTC. Its readable archive SHA256 was `11897A48D46F4C140FBCF715E814C4B7EBDC86CED7CF15B9BBC0E75AD2EDB989`: Scoundrel 2/level 2, effective saved HP 10, one `g_a_clothes01` and one `g_w_shortswrd01` equipped. The joiner's own checkpoint was Soldier 0/level 1, effective saved HP 12, with the same item types. Saved health uses `CurrentHitPoints - HitPoints + MaxHitPoints`; raw 0/0/12 is living HP12.

The ABI95 native processes had run about 3 hours 35 minutes continuously; Agents were restarted during recovery and are not counted as uninterrupted. After the safe checkpoint, only owned games2684/63692 and Agents39136/48396 were stopped by validated path/start-time identity. Entire private saves and selected configs/logs were archived in `checkpoint-carth-before-v96`. Native96 and matching Agent96 were deployed from immutable artifacts; new games22760/28968 loaded the genuine checkpoint. Both native classes and HP remained correct. A real joiner W400ms input moved (76,73) to (76.91613,74.18575).

At Taris, both native modules were `tar_m02af`. After each player dismissed the original journal popup, joiner W450ms moved (91.21744,143.53221) to (89.46253,146.03198), with native Soldier0/level1 and HP12. A real host `save Co-op` completed with archive SHA256 `45515CCF81161F6A2772958C042479679E50FCC03B80CE34145F229B217FD00E`: host Scoundrel2/level2, effectiveHP12, clothes and short sword. The joiner's subsequent own sidecar retained Soldier0/level1, effectiveHP12 and those original equipped item types.

## Evidence and limits

Evidence is under `artifacts/natural-campaign-v95`: `starboard-droid-natural-return-end.png`, `starboard-next-door.png`, `carth-introduction.png`, `carth-safe-checkpoint-native.json`, `escape-pod-original-action.png`, `escape-pod-choices.png`, `escape-pod-movie.png`, `taris-world-initial.png`, `taris-client-initial.png`, `taris-joiner-control-v96.json`, and scoped `rig-v96` logs/manifest.

The earlier recovery patch's immutable f369cf5 source archive matched all 1,838 tracked extracted entries; focused tests passed30/30 and its full fast suite passed5,569/5,569. Live fresh-Agent worldless recovery then automatically rejoined in four seconds with the original Soldier checkpoint; the client was not manually loaded to mask that gap. This report does not claim full natural campaign, Council eligibility, combat-menu first-frame retarget, or final restoration of the currently active private route.
