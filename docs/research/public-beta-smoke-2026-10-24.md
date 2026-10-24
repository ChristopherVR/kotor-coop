# Public beta candidate: background GOG smoke (2026-10-24)

**Candidate `59e6e2b` is blocked by container exclusivity.** Join/adoption, reconnect, named save/load, host reload readoption and ordinary container removal propagation passed the bounded checks below. This does not certify the final candidate that will include later menu fixes.

## Provenance and isolation

Installed `artifacts/publication-installer-verified-20261024` into the stopped disposable `artifacts/live-installs/sentinel-20261024-host` and `-join` copies using its installer. Both installed files matched:

- `dinput8.dll`: `090FC46086F8E08E8770F6A4C3C8BA26E6141E434FF701B50121A5D4D34EFEDD`
- `KotorCoop.Agent.exe`: `9CBD0AF5A5E7CEDDE35F837F8C935353F6AF880DC93CCACDE08F05C526A9E7F2`
- Both GOG `swkotor.exe`: `D3432B321DBDE12A35C46A7A7B4795B5F947C6FE90E3A7A30C382D6C173E381A`

Old rig saves/config/logs were archived under `artifacts/public-beta-smoke-20261024/before-*`. Each role received a copy of its original `artifacts/trailer-v2/game-{host,joiner}/saves/000002 - Trailer` as `000002 - CandidateBaseline`. All ten original/copied save-file hash comparisons passed after testing (`baseline-preservation.json`). Original fixtures remain intact.

The pair ran through `tools/Start-CoopBackgroundRig.ps1` on the non-input desktop `KotorCoopBackgroundCandidate59e6`, windowed at 800x600. No desktop switch, foreground activation or global mouse/keyboard was used. Only the owned games' in-memory single-instance mutex strings were changed by the harness. Candidate helpers auto-started normally. Configuration was candidate defaults plus role/name, `join_mode=save`, loopback port 27150, empty STUN servers and UPnP off. The appended last values override the earlier defaults through `CoopConfig`'s dictionary assignment.

Host game PID 49296 and join PID 38680 are recorded with executable path/start ticks in `host-rig.json` and `join-rig.json`; helper identities are in `owned-agents.json`. Both games were stopped through those exact states at the end, and their helpers exited. The unrelated `menu-widescreen-20261024` game was not touched.

## Passed bounded checks

- Initial loading used the clean candidate-tree Release `KotorCoop.Memory.dll`, `TryStageSaveAsAutosaveFromDirectory` and the eight-argument `TryRequestLoad`; both staged `danm13`, request 1, status 0. Raw load requests with a missing module were not used.
- Console mailbox `host` and `connect 127.0.0.1:27150` connected with one peer and one phantom each. These are injected developer mailboxes, not human menu clicks. The baselines have different worlds, so the expected W_SAVE degradation initially appeared.
- After the normal mismatch grace, the joiner made a fresh dedicated character save, fetched the host's 1,195,505-byte world, preserved its own five backpack items, loaded with the carried-character path and automatically rejoined `danm13` at local time 15:02:06 (UTC 05:02:06). Both reached memory-ok, one peer, zero malformed packets and shared-world pumping. The transient load-time read failures cleared. The initial host screenshot was flat pink; a later capture rendered the world normally.
- An explicit `reconnect` after adoption returned to one peer with memory-ok and no malformed packets. One-sided save identity changes were correctly recognized as the same world rather than causing another adoption.
- `save CandidateNamedHost` produced `000004 - CandidateNamedHost` at 15:02:21. `loadgame CandidateNamedHost` resolved that exact name, staged it, and reported the world up at 15:02:38. The joiner then automatically adopted the host reload and reached memory-ok/one peer at 15:02:46. The resulting archive and character were readable: 118 player fields and module `danm13` (`named-save-validation.json`). The accidental unsupported `savegame` command was refused and is not counted as a save.
- One deliberately staged `warp end_m01aa` made both games arrive. Developer `tp` placed both by containers; this is synthetic setup, not natural campaign progression.
- Container `0xC6` was genuinely empty: screenshot and both `loottake` refusals agreed. It was not counted as removal coverage.
- Read-only engine object/repository inspection found a nonempty container `0xD1`, initially four items in each game. A client `loottake 0xD1` removed real frag grenade item `0xD5` and gave the client a fresh item through the engine path. Afterwards both repositories contained exactly the same three IDs `0xD4,0xD3,0xD2`. This proves ordinary removal propagation for that take; it does not prove simultaneous takes cannot duplicate an item.

## Release blocker: both players can reopen the same container

The first staged attempt retained an earlier container panel, so it is not the decisive race. Scoped native DirectInput Escape and addressed click messages closed the real panels. A read-only probe at 15:05:00 confirmed **both copies of `0xD1` closed**, with the same three remaining item IDs.

Parallel console mailbox calls then used `0xD1` on both games. Native use requests were logged at 15:05:07.510 (joiner) and 15:05:07.794 (host). Both explicit arbitration requests were granted:

- Joiner: requested 15:05:08.515, granted 15:05:08.804 after 282 ms.
- Host: requested 15:05:08.792, granted 15:05:08.863 after 62 ms.

**Both inspected `race-host.bmp` and `race-join.bmp` show the same nonempty container inventory panel open simultaneously.** Probes at 15:05:15 show `Open=1` and all three item IDs in both repositories. Arbitration was enabled in both plugin startup logs. This violates the one-player container claim. No simultaneous item-take/duplicate-item claim is made: only exclusivity was reproduced.

Likely cause, requiring a fix and repeat validation: `InteractionLockController` keeps publishing its previous inactive `_lastClosedContainerId` while a new deferred request for that same container is pending. A grant can therefore be followed by an older ordinary release before its receiver observes the grant. `WorldStatePump` publishes the ordinary claim/release before forwarding explicit arbitration. This is source-based inference, not an instrumented packet trace.

The setup caused real nearby Sith combat; no god mode was enabled. Both survived and were moved back to the safe start before scoped cleanup. This is not evidence of ordinary two-human combat or a campaign playthrough.

## Focused fix, awaiting a fresh candidate live run

The patch retires a previous same-container close before the plugin publishes a new deferred request. The session captures each close's reservation epoch in the existing inactive `InteractionClaimPacket.RequestSerial`; the host ignores old or duplicate releases after a new reservation, while an actual matching close still releases immediately. The client also withdraws its cached old closed level before sending a new request. Ordinary active refreshes preserve the epoch, and abandoned requests still expire through the normal lease. No packet field or wire length changed.

Review also found `WriteContainerLockResponse` publishing its serial before the granted bit, contrary to the shared-block contract. It now writes the verdict first and only publishes the serial if that write succeeds. This closes the adjacent window in which a new denied request could observe the previous grant bit.

Targeted Release tests passed **169/169**: epoch/reordered/duplicate-release regressions, real loopback close/reopen arbitration, matching release, denial, lease expiry, lost-request fallback, serial wrap, existing container/interaction rules, guarded-page response-publication failure, and packet codec tests. TRX: `artifacts/public-beta-smoke-20261024/epoch-tests/container-epoch-targeted.trx`. This is automated verification of the patch; it does not replace the required live rerun against rebuilt candidate binaries.

Mixed builds retain the same packet layout but differ in release semantics. A fixed host ignores a legacy zero-epoch release of an explicitly reserved container, leaving expiry as its fallback; an old host does not protect newer reservations from delayed closes. **All peers must use the identical rebuilt candidate.** Zero-epoch unarbitrated claims still close normally. Full mixed-version behavior is not certified.

## Evidence

Ignored evidence is under `artifacts/public-beta-smoke-20261024/`: installer output; hash manifests; rig/helper ownership; command replies; before/after container probes; complete `after-host`/`after-join` saves, config and logs; and native GL BMP captures in each `after-*/kotorcoop-shots/`. `read-container.ps1` records the read-only repository/open-flag probe. Both games remained alive until scoped cleanup. First-chance records are retained: host latest serial 1449, C++ exception `0xE06D7363` at external address `0x7553BAE4`, and joiner serial 1, thread-naming exception `0x406D1388`; neither was an access violation or an observed terminating crash. The decoder outputs are `crash-record-host.txt` and `crash-record-join.txt`. Their originating external component was not identified. The final process inventory contains only the unrelated menu test game.
