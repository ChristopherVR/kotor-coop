# Final95 saved-world serialization: scoped live evidence

Evidence directory: `artifacts/dorak-training-v95`. Original installations were not changed. Only private game6/7/8 were used, with full native/config/userdata/Saves backups.

## Healthy three-player flow

The final `0518d08` serialization guard DLL (`artifacts/live-native-v95-saveguard-final/dinput8.dll`) and matching `artifacts/live-agent-v95` passed this bounded flow on 2026-10-01:

1. Load exact copied, genuine non-Jedi Endar saves. Host had class2; both partners had class0. All had one class and initially zero Dantooine/Jedi training globals. The launcher reported valid native remote bodies, two peers and two phantoms on every instance.
2. Use real engine travel to `danm13`, then follow its automatic stock Bastila arrival dialogue. This is **staged entry**, not natural campaign progression.
3. Allow actual adoption/rejoin to settle. Every engine reported `danm13`, two peers, two phantoms and the original single base class. Inspected GL images showed ordinary rendering.
4. Dismiss actual journal popups, then request dedicated Co-op saves. Save requests made while those popups were open were refused; command receipt was not counted as completion. All three subsequently produced readable `SAVEGAME.sav`, `GLOBALVARS.res`, `PARTYTABLE.res` and `savenfo.res`. Completion times were UTC 2026-09-30 19:17:33 (host), 19:17:36–37 (partner2), and 19:17:47 (partner3). Parsed saved characters retained classes2/0/0; partners retained their clothing and short sword.

`finalguard-native.json`, `finalguard-completed/`, `launch-finalguard.txt` and inspected `finalguard-*.png` record this result. No new access violation occurred. **The stale-party repair did not naturally emit a repair log during this successful run.** This proves a healthy three-player travel/adoption/save flow, not a live reproduction and repair of the exact earlier race. The separate focused guard tests cover the repair rule.

Copies of each original QuickSave slot were installed before Dantooine travel. All twelve core files retained their hashes and exact write times (`quicksave-before-dan.json`, `quicksave-after-dan.json`). Scoped cleanup restored original native/config/userdata/Saves and verified all64 original save-file hashes:17+23+24 (`restore-evidence.txt`).

## Failures and limits retained

The initial game8 access violation at engine `0x63D4B3` is preserved in `crash-game8`, including its binary crash record, logs and actual save/PIFO bytes. The host's later exit was launcher timeout cleanup, not another crash. Subsequent three-player attempts showed severe game8 scene corruption and two-second frames before Dantooine travel. Static assets, executable and graphics configuration matched stable game7; game8 rendered normally alone. A fresh run after reducing concurrent rigs rendered normally. This supports a resource-pressure contribution, but does not establish an exclusive cause.

The stable two-player staged Council conversation reached real training acceptance, then the original scripts moved both players into Taris's early Carth wake-up dialogue (`tar_m02af`). Neither gained a Jedi class. This is incomplete prerequisite coverage; it does not certify natural Council/Dorak training, private class choices, sabers, or full campaign support. No class or eligibility globals were forced.
