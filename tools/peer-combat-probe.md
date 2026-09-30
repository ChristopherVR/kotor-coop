# Native per-peer combat acceptance probe

`peer-combat-probe.ps1` opens the selected process with read/query rights only. Run:

```powershell
& tools/peer-combat-probe.ps1 -ProcId $hostPid -EnemyIds @($hostEnemy) -OutputPath artifacts/combat-before.json
```

Capture after each stage and retain the JSON. It reads the attributed bank using its sequence lock, rejects an odd/changing sequence or another world load, and resolves every native body through the current server object array. Compare `Pid`, `ProcessStartTicks`, `Epoch`, and `WorldLoadSerial`, plus `NativeNamespace.Module/ObjectArray/ObjectTree`, before comparing counters. A world change requires a new baseline. Load serial zero is valid in an initial world; it counts unannounced teardowns and does not increment for ordinary warps. The probe separately checks the bank load serial at `0x870C` against `0x396C` and its epoch against FNV-1a of the loaded native module. `ThreatGeneration` at `0x4B84` is independent. Peer slot numbers are transient; compare `Peers.Peer`, not slot position.

`AcceptedCumulative` is a received cumulative damage amount. `AppliedEffects` counts successful engine effect calls across all enemies and peers, not hit points. Per-peer consumed debt is private managed state and is explicitly unavailable. A bank increase alone is not acceptance proof. `Enemies.BaseHp + TemporaryHp` measures native health; temp HP can absorb a hit while base HP stays unchanged. `Enemies.NativeTarget` reads the real combat-round target, independently of diagnostic threat rows.

## Controlled three-player scenario

Use a host and two real joiners with distinct peer identities and matching stock content. Before launch, keep `threat_retarget=1` on the host and disable `local_combat` on both joiners for this forwarding-only test. The console attack command brings a player to melee distance and can otherwise transfer fight ownership. Host `DamageDealtEnabled` must remain zero throughout. Use one non-plot hostile with sufficient base/temp HP to survive the whole test, ordinary equipment without regeneration or damage-over-time effects, and no host/companion attacks. Keep all other combats idle. Parent controls every write and attack; this probe only observes.

1. Capture baseline. Have joiner A land a real hit on its mirror of the selected host enemy, then stop attacking. Capture until A's cumulative grows and the host application counter advances. Native base-plus-temp HP must decrease. Inspect damage reduction/immunity when comparing exact raw accepted damage to health loss; choose an enemy without either for an exact delta assertion.
2. Repeat with joiner B. Its distinct peer row must grow and a further native HP decrease must occur. Neither row may disappear or overwrite the other's accepted history. This proves two real senders reached one host-owned native body.
3. Within the 10-second threat window, have A establish at least 15 threat, then B establish more than 1.5 times the current target's decayed threat. Allow the four-second retarget cooldown while keeping both threats fresh. Capture several times within five seconds: the host enemy's **native** target must become B's mapped phantom. A changing diagnostic `Top` alone is insufficient.
4. Stop B and down B through the existing authorized staging command. Confirm B's host phantom has base HP <=0. Keep A's threat above 15 and fresh. Once the cooldown clears, the native target must become A's live mapped phantom. An absent/downed highest attacker must not mask the next eligible contributor.
5. Stop all attacks and damage effects. Capture a quiet baseline, disconnect B, and reconnect B with the same stable identity without attacking. Track B by peer identity even if its slot/body changes. Observe for at least six seconds after reconnection: accepted cumulative history must not duplicate, host `AppliedEffects` must stay unchanged, and native base-plus-temp HP must stay unchanged. Then allow one fresh real B hit; only that new contribution may increase the row/counter and reduce health.

For stages 3–4, observe multiple snapshots across native round boundaries. The diagnostic target is sampled before issuing an order and can lag the actual round. Record the enemy's native target, both mapped phantom IDs/HP, threat values, health, accepted totals and global application count together. Do not claim replay-free behavior across a world load or unrelated combat because those change the comparison oracle.
