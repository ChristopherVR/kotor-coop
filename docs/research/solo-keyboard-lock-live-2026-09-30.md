# Native keyboard parking checks, 2026-09-30

Two private stock installations, game2 and game3, ran preserved native87-stop
(`FBFA71E79418E173D49712E9EA8F43566D2F7B9967EA3A2518CFBC50129AA8A1`)
with published Agent87b/wire79. Host PID33836 and joiner PID56680 used port32140;
the owned manifest is `artifacts/minigame-lock-v87/pair2/rig.json`.
The artifact-local launcher snapshot pins native87 because evolving source startup
helpers already expected native88. The first version-mismatched launch rolled back
before loading a game; the subsequent matched pair reached native readiness.

Only synthetic DirectInput W edges through input-kind103 were used, with exact native
ACKs and a two-second hold. No operating-system input, native position writes or
health writes formed these movement checks. Before/after telemetry resolved the same
current native player through the live object array and checked its module and native
liveness epoch. The baseline unlocked W control moved the joiner 2.744286 units.

## Swoop track

The host was explicitly staged from END_M01AA into `tar_m03mg`. Read-only native
telemetry found a real active minigame with Type1 and player HP999. The joiner agent
logged refusal to follow the single-seat track and remained in the preceding world.
Native `MinigameLockActive` was1 before and after W: displacement was exactly0.0.
After an explicit staged host return to `end_m01aa`, the level was0 and the same W
hold moved the joiner0.560953 units.

Evidence: `swoop-native-entry.json`, `swoop-parked-lock.json`,
`swoop-unlocked-positive.json` and `pair2/agent2.log`, under
`artifacts/minigame-lock-v87`. This verifies real track detection and parked keyboard
blocking/release. It does not prove natural race entry, completion or scripted return.

## Star Forge solo spectating

The original valid `000005 - starforge` save was copied, without changing the original,
to private game2's `000902 - lock_starforge_fixture`. Every file's original and copied
SHA256 is recorded in `starforge-fixture-hashes.json`; SAVEGAME.sav SHA256 is
`432BC2E17D6F52FC163A0158EE825C39AA463319120DCCCDE2A82DD18B0ED6A4`.
Both players reached `sta_m45ac`. The fixture lacks Bastila. Opening
`k45_door_bast1` and actually walking across the known init-trigger coordinates
produced no dialogue or natural solo-start event, consistent with that missing actor.

The bounded fallback issued real native `RemovePartyMember(0)` calls through the
developer console. The first removed the active companion; the next reached the VM
hook but returned failure9 because that slot was already absent. Those two observed
calls,156ms apart in the hook log, triggered the existing burst detector. The host plugin logged
`solo segment: began in sta_m45ac`; the client logged
`solo segment: spectating 0x119 ... input parked, camera follows`.

The client native remote slot carried `FlagSoloSegment=128`, and the current local
player's native `Commandable` was0 before and after W. Displacement was exactly0.0.
This uses the solo spectate controller, whose lock is distinct from
`MinigameLockActive` (that minigame field remained0). After an explicit staged host
return to END_M01AA, the remote solo flag cleared and W moved the joiner8.797779 units.
Evidence: `starforge-staged-parked-lock.json`, `starforge-unlocked-positive.json`,
the two saved plugin logs and `pair2/agent2.log`, in the same artifact directory.

This proves keyboard blocking and release for the controlled Star Forge solo-spectate
path. It does not prove the natural Bastila or Malak duel, their outcomes, or a complete
finale. Mouse behavior was not checked in this pass.

## Cleanup

The scoped launcher stopped only this pair and restored its original configuration.
Private games2/3 save folders were restored from pre-run copies with every file hash
checked; modified test saves were preserved under `saves-after-game2/3` instead of
deleted. Original native DLLs were restored. The original Star Forge save was never
written. The restoration script is `artifacts/minigame-lock-v87/restore-owned.ps1`.
