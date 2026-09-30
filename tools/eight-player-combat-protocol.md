# Eight-player real combat protocol

Use only the eight private stock installs, game1 through game8. Restore identical stock
content before launch; never reuse the JFTS class-session save as a stock fixture. Record
the scoped `demo-many` work directory, each process ID/start time, and stable host roster
peer identities. Stop only that work directory. The checker below is offline and makes
no game writes; `peer-combat-probe.ps1` opens processes with read/query rights only.

## Preparation

Use host `local_combat=1`, `threat_retarget=1`; joiners `local_combat=0`. Set host damage
forwarding off and confirm `DamageDealtEnabled=0` in every host snapshot. Keep host and
companions idle. Choose one living, non-plot enemy without regeneration, immunity or
damage reduction, with enough base plus temporary HP to survive seven bounded attack
windows. A staged durable fixture is allowed; staging damage, HP changes or template
changes do not count as real attacks. Isolate all unrelated fights. For the damage-only
test use a non-commandable/paralyzed fixture so NPC follow-up cannot contaminate HP.

Resolve the selected host enemy to its actual local mirror ID in each joiner through
the owner-to-local enemy mapping. Verify native object identity/type, module/load
namespace and mapping generation before attacking. Host owner ID is not a joiner
local ID. Exclude every player/phantom ID shown by the probe. If a mirror is absent,
stop and repair the fixture; do not attack a stale old ID or pretend the host ID matches.

## Seven real contributors and exact health

1. Capture the host baseline with explicit enemy ID:

   ```powershell
   & tools/peer-combat-probe.ps1 -ProcId $hostPid -EnemyIds @($hostEnemy) -OutputPath "$run/combat-before.json"
   ```

2. One joiner at a time, issue `attack <verified-local-mirror-id>` through its CLI
   console. This invokes real engine attack actions/rolls; it does not apply synthetic
   damage. Limit each window to 15 seconds and stop immediately when that joiner's
   host `AcceptedCumulative` rises. Always issue `attack stop` afterwards to clear the
   player's combat actions, including timeout/error paths. A miss is not a contribution;
   permit at most two bounded windows per joiner, then report incomplete coverage.
   Stop if host fixture base HP becomes nonpositive or health approaches exhaustion.
3. Capture after each contributor. Verify its distinct peer row grows and native
   base plus temporary HP decreases by the summed accepted delta once queued effects
   settle. Existing rows must remain. Record raw health and global application count;
   the latter counts successful engine calls, not hit points or individual hits.
4. Stop all seven attack queues; wait for two consecutive settled snapshots, then save
   the final host snapshot. Supply the seven actual stable joiner peer IDs:

   ```powershell
   & tools/eight-player-combat-check.ps1 -Before "$run/combat-before.json" -After "$run/combat-after.json" -EnemyId $hostEnemy -PeerIds $sevenPeerIds -Mode Hits -OutputPath "$run/combat-proof.json"
   ```

## Quiet replay and ordering

Capture a settled baseline, then observe every second for at least seven seconds.
Check each snapshot against that baseline with `-Mode Quiet`: every cumulative, native
base/temp HP and application counter must stay unchanged. Next disconnect/reconnect
one joiner with the same stable identity and no attack. Wait for its new resolved host
phantom, then repeat the seven-second quiet checks; body/slot changes are expected,
peer identity and damage history must remain stable. After reconnect, remap its local
enemy freshly and permit one real bounded attack; only its new cumulative increment
may account for the additional health loss. Do not compare across a host world load,
enemy replacement, native damage incarnation change or unrelated combat.

## Optional native threat switching

Use a separate commandable fixture and two armed characters; unarmed level-zero
damage may never reach the 15-threat threshold. Preserve the other five idle players.
Within the ten-second threat lifetime, attacker A must establish at least 15 threat;
B must exceed 1.5 times A's decayed threat. Observe across the four-second cooldown and
native round boundaries. The enemy's `NativeTarget` must become B's current mapped host
phantom; a diagnostic `Top` change is insufficient. Separately stage B downed and keep
A's qualifying threat fresh to test fallback. This staging is not combat-damage proof.
Retain snapshots of native target, mapped phantom HP, threat, accepted totals and health.
Report threat coverage separately if safe commandable isolation cannot be established.

