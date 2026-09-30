# Native96 enemy-effect relay live check, 2026-10-01

Matching Native96/wire85 and Agent builds passed warning-as-error publication. The
immutable validation tree passed **5,580 fast tests and 117 Slow tests** (9m31s for
Slow). Runtime source is commit `7cf5706`; validation commit `33cf5a7` adds only a
documentation encoding repair and the legacy expected-version assertion update.

| Published artifact | SHA256 |
| --- | --- |
| `artifacts/live-native-v96/dinput8.dll` | `843148E23B21340F612CB393186D589AAB7CEF361438797FBD100C83C2CA6CB8` |
| `artifacts/live-agent-v96/KotorCoop.Agent.exe` | `656ADDB4F8A72FBEBCD008C944A690347F60BF509486F26388DDF5D95A08E79A` |

## Controlled fixture

Private game1/game2 only, port 39147, actual game PIDs 30632/27268. Each loaded an
unchanged copy of `000900 - live_baseline_20260930`. Host `warp tar_m10aa` was an
explicit staged entry, not natural Taris arrival. The original GIT creature `0x33`
(`vulkarpool`) had matching census twins: both reported 26 present and zero stand-ins.
Explicit `local_combat=0` exercises the mirrored-enemy effect channel; that channel
deliberately excludes local-combat bypass enemies. `enemy_effect_sync=1` was explicit.

The developer caster was created with actual native `addclass 3`, `levelup 3 10`,
and `grantpower` calls. Native stats proved base class2 at level1 plus Guardian10,
HP106/106 and FP40/110.
These are controlled plumbing checks, **not natural class progression or power
acquisition**. Every successful cast used `CastSpellAtObjectViaClient`, the radial-menu
client handler. No `applyeffect`, raw HP/FP, quest/global/class writes, or pause writes
were used. FP spending was not established by this test.

Initial cast requests did not land while an ordinary closed door blocked the route.
They count as setup attempts, not successes. Actual native `opendoor` opened door
`0x61`; the subsequent effects landed. The caster took genuine NPC damage during
the fixture, eventually HP13/106, and withdrew using scoped position/stop commands.

## Actual casts and host applications

All capture rows below logged creator `0x7FFFFFFF`, `ours=True`, `attackers=True` on
the client. Host rows are successful native engine applications, not console ACKs.

| Actual client spell | Client capture | Host native application |
| --- | --- | --- |
| Stasis29 | 09:05:14.788, kind2 Paralyze, target33 | 09:05:14.841, kind2, 12,000ms |
| Fear16 | 09:06:37.923, kind5 Horrified, target33 | 09:06:38.000, kind5, 6,000ms |
| Force Push23 | 09:07:36.850, kind8, target33 | 09:07:36.900, kind8, 100ms |
| Force Push follow-on | 09:07:39.401, kind14 general Stunned, target33 | 09:07:39.466, kind14, 2,000ms |

## Same-area load and reconnect fences

An actual host named save and `loadgame Effect Epoch Fresh` retained `tar_m10aa`
while advancing native load serial0 to1 and native epoch2 to3. The original target33
still resolved as the current typed creature through the native server object array.

A negative control refiled the genuine old Stasis IN payload, including its old
session/load/epoch tuple, under synthetic serial50001 to bypass handled-serial dedup.
This is **synthetic stale-mailbox input**, not another cast. The native controller
explicitly logged `refused effect from a retired world or session` at09:10:26.424
and consumed serial50001. Fresh actual Stasis then captured at09:10:38.846 and
applied at09:10:38.941 under epoch3/load1, host serial5.

Peer2 disconnected with a completed dedicated character save and reconnected as
peer2 at09:11:50. Through09:12:10 there were no additional host effect applications;
coherent before/after snapshots retained IN serial5, sequence12, target33 baseHP11,
temporaryHP0, and the same current body. A fresh actual Stasis captured at09:12:29.686
and applied at09:12:29.797, advancing IN serial6/sequence14. This proves quiet
same-process reconnect idempotency and continued fresh-cast admission. Native process
restart/incarnation refusal remains covered by focused tests rather than this live run.

## Evidence and cleanup

`artifacts/enemy-effects-live-v96` contains launch logs, copied final native/Agent
logs and saves, coherent namespace/current-object JSON snapshots, the read-only
probe, the explicit stale-mailbox negative-control script, and screenshots. Host
native application lines are in `final-evidence/game1/kotorcoop-general.log`; actual
client capture lines are in `final-evidence/game2/kotorcoop-general.log`.

Only this rig's recorded PID/start-time pairs were stopped. Original private1/2
configuration, native DLL, saves and mutable runtime files passed an exact SHA256,
size and file-mtime restoration audit. GPU slots were released to the Manaan test.
The matching96 stock-GUI NewGame-after-wipe repeat was deferred after cleanup;
the earlier qualified94 stock-menu proof and95 native intent fences remain separate
evidence. No companion equipment claim is made by this fixture.
