# Release verification — 3 October 2026

Verification continued from [the release-readiness review](RELEASE-READINESS.md), using the
working tree based on `1ad9b6d6` plus the uncommitted review fixes. No release was published
and VERSION remains `0.11.0`.

## Candidate and environment

- Windows package: `kotor-coop-0.11.0-readiness-20261003-r3-windows.zip`.
- Embedded agent version: `0.11.0-readiness-20261003-r3`; protocol 86, shared block 97.
- SHA-256: `52B0F227AEC08F8A3BA71EB139C98C07EE34A338021D5BC937CE8B7891F36D7A`.
- Built with `tools/package.ps1 -WarnAsError`, including the NativeAOT x86 plugin and C loader.
- Two disposable copies of the existing DRM-free 1.0.3 game, copied into ignored workspace
  artifacts. Both received the actual extracted package. This is a same-PC loopback test,
  not a clean second-PC, mixed-store or internet test. Existing source installs were not edited.
- Seed: `000900 - live_baseline_20260930`, an Endar Spire staged save. The rig drives engine
  commands and native UI handlers; it does not establish an uninterrupted human campaign.
- The game loads `verifier.dll` on this machine; memory readings include that environment.
  Do not compare them directly with older ordinary-game memory figures.

## Completed checks

| Check | Result | Evidence / scope |
| --- | --- | --- |
| Final full non-soak suite | **PASS: 5,786; zero failures/skips** | `dotnet test tests/KotorCoop.Tests --filter 'Category!=Soak' -warnaserror`; 11 min 24 s. Includes the real-socket slow tier and all five new mailbox/loot-recovery regression cases. The earlier pre-fix full run passed 5,781 tests; both TRX files are retained. |
| Native packages r1, r2 and r3 | **PASS** | Built with warnings treated as errors. Plugin PE architecture checked as x86. Windows zip, Linux tarball and separate agent symbols produced. Linux runtime/installer was not exercised here. |
| Actual packaged installer/support matrix | **PASS: 26 checks on r3** | Plain and chained installs, paths with spaces, health checks, upgrade, preservation of custom settings and chain entries, broken-chain failure exit, removal/restoration, chain-error bundling, secret redaction and stamped collecting/installed versions. Uses synthetic executable/DLL fixtures; it does not prove real ReShade compatibility. Run as a normal user: firewall instructions printed; elevated rule creation remains unverified. |
| First live battery | **48 passed; two checks lacked a combat target** | No active hostile in the seed. The old verifier called the two missing prerequisites failures. Original output retained, not discarded. |
| Staged combat rerun | **49 passed, zero failures, one intentional skip** | Spawned `n_sithsoldier001` from the loaded module, supplied its explicit object id and temporary HP. Swing animation and client damage to the host's real enemy passed. Transition intentionally omitted here because it had passed in the preceding run. |
| Bare disconnect/reconnect | **PASS on initial candidate** | Dedicated disconnect capture completed; peer id 2 returned, both phantoms restored, `same world, not rejoining`, no forced world reload. |
| Forced host-agent termination/restart | **PASS on initial candidate** | Joiner reported lock release/solo mode, remote count and phantom count became zero, then automatically reconnected after the packaged host agent restarted. Both real peer bodies resolved again. This was agent loss, not a reproduced native game crash. |
| Saved-world contamination | **PASS** | `saveinspect.py --all --expect-clean` found zero peer creatures/map pins in the fixture autosaves, named baselines and joiner's dedicated Co-op save. This is not an exhaustive inventory/save-loss test. |
| Final installed plugin / stock config warning | **PASS** | Both installed plugin hashes match r3; neither current startup segment logs an unknown-setting warning. Earlier log segments retain the original warning. |
| Ten-minute real-socket soak after loot recovery fix | **PASS: one test, 10 min 2 s** | Host plus two clients; 5–30% packet loss, jitter, duplicates, two client rejoins and an eight-second host blackout. After repairing the link, flags, journal, ground items and container-loot digests converged. This is a protocol test, not a ten-minute native-game playthrough. |
| r3 live battery | **49 passed, one failed, zero skipped** | Container unlock/re-lock/restoration and round-trip transition completed. The injected phantom damage counter rose by 22 rather than the expected three while the spawned hostile was also attacking. The exact assertion was retained. |
| Controlled r3 repeat | **INCOMPLETE** | Paralysed the staged hostile. Exact phantom damage passed (counter 5→8, HP 6→3); container restoration passed again. Client enemy-damage check skipped after the target ceased to be usable. After the round-trip, a further module load left no native player for cleanup, which aborted before the summary. This run is not a clean battery pass. |
| Packaged helper auto-start | **PASS** | Launched the games with `auto_start_agent = 1`; no explicit agent run command. Each plugin launched the installed r3 helper as its child in menu mode. Console Host/Connect then established peer 2 and real, engine-resolved peer bodies in both games. This does not verify retail button clicks. |
| Auto-helper termination / recovery | **REQUIRES HOST ACTION** | Killed only the host helper. The plugin launched a replacement, but it waited in menu mode and the joiner remained reconnecting throughout a 120-second readiness wait. Entering `host` restored the session and both native peer bodies. Host block counters retained one remote/phantom while menu-mode recovery waited; clearance in that state needs investigation. |
| Game/helper lifetime and rig cleanup | **PASS** | Terminating the host fixture game removed its owned helper. After eight seconds the joiner block read zero remotes, zero phantoms and zero desired/applied pause. Both fixture games/helpers were then stopped; original source installations were not modified. |

## Failures retained from verification

- The full r2 live battery aborted during container lock restoration. Rotating mailbox rows
  retained old acknowledgements; consuming them after newer rows reverted the bridge's shadow
  and could undo a later unlock. Two acknowledgement regressions reproduced the defect.
- The first ten-minute real-socket soak failed after its 90-second recovery window. Flags,
  journal and ground items converged, but both clients' loot histories stopped at version 1757
  while the host reached 1835. Recovery always restarted at the oldest history entry; histories
  larger than the 1024-operation send queue could repeatedly refill it before missing later
  entries were offered.

Both failures prompted code changes and an r3 rebuild. The original failing transcripts/TRX
remain available; they are not counted as passes.

The r3 soak closes the reproduced protocol backlog failure. Live container restoration verifies
the mailbox fix in the native game. The full live battery still needs a stable, repeatable pass:
the staged combat fixture and rapid scripted transition produced the later failures above.
The repeat also logged an owner/client enemy-liveness disagreement and a module object-mapping
warning. Their causes were not established by this pass; do not dismiss them as harmless or
certify combat/transition recovery solely from individual passing assertions.

## Fixes found while verifying

1. The plugin classified the shipped agent option `world_transfer_udp_fallback` as unknown.
   Added it to the plugin's player-setting classification, rebuilt as r2, and confirmed the
   warning disappeared in both actual game startups. Every active key in the shipped settings
   is now represented in the plugin's classification lists.
2. The live verifier incorrectly returned FAIL when no suitable hostile existed. Those two
   paths now return SKIP with staging instructions. The actual combat assertions remain intact;
   the staged-target rerun passed them.
3. Added `tools/verify-package.ps1` and wired it into CI after native packaging and before
   artifact upload. It runs the self-contained packaged installer, rather than a development
   binary, and leaves transcripts/support bundle evidence in a new fixture directory.
4. Placeable acknowledgement and local-change consumers now reject older generations,
   including across the unsigned generation wrap, rather than only rejecting identical rows.
5. Container-loot recovery retains a per-module cursor when the bounded sender fills, so later
   requests continue through the history instead of starving its tail. Regression cases cover
   1102 and 2500 removals, both larger than the sender capacity.
6. Corrected the player-facing firewall explanation and documented re-hosting after an automatic
   helper restart. Process relaunch and session recovery are separate results in this pass.

## Local evidence

All raw evidence is under `artifacts/release-readiness-2026-10-03/` (ignored by Git):

- `tests/release-readiness-full.trx`, the original failing `release-readiness-soak.trx`,
  final `release-readiness-full-fixed.trx`, the passing `release-readiness-soak-fixed.trx`
  and focused `release-fix-regressions.trx`.
- `live-verification.txt`, `live-verification-combat.txt`, `live-verification-r2.txt`,
  `live-verification-r3.txt` and `live-verification-r3-repeat.txt`.
- `rig/`, `rig-r2/`, `rig-r3/` and `rig-r3-repeat/` agent logs; each disposable game's plugin log.
- `installer-r3.txt`, `installer-matrix-r3/` transcripts and `support-bundle.zip`.
- `saved-world-ghost-check-r3.txt`, `package-r3-build.txt`, `package-manifest-r3.json`.
- `packages-r3/` final archives and symbols; earlier candidates retained separately.
- `autostart-verification.txt`, `autostart-recovery.txt`, `autostart-cleanup.txt` and
  `auto-agent-*.log` captured before/after helper restart and game termination.

The artifact directories also contain proprietary game copies for the local rig; do not upload
the entire evidence directory. Share individual reports/log bundles and the mod archives.

PowerShell script parsing, workflow/issue-form YAML parsing and `git diff --check` pass locally.
The new CI installer step has not yet run on GitHub. All changes remain uncommitted for review.

## Still required

The P0 general-release gates remain: a natural complete campaign, two ordinary independent PCs
and an outside-network join, plus comprehensive inventory/save/crash recovery. First-time
character creation with retail controls, firewall mutation, real third-party loader compatibility,
protected directories, mixed-store sessions on this candidate and multi-hour/many-player live
play are not certified by this pass. See [RELEASE-READINESS.md](RELEASE-READINESS.md) for the
acceptance steps. No missing check is promoted to a pass by the loopback results.

Also repeat the full native battery with a durable combat fixture and settled transitions,
investigate the recorded liveness/module-mapping warnings, and improve the host recovery prompt
after a helper restart. The Windows r3 package has passing installer and protocol-soak evidence,
but this record does not sign it off as a frictionless general release.

## Further verification: r4

The subsequent package is `kotor-coop-0.11.0-readiness-20261003-r4-windows.zip`,
SHA-256 `BA2209C9A3FF21B23308D66A7C670278834C9851704153A755000A41D16B1584`.
It adds replacement-helper attachment cleanup; protocol 86 and block 97 are unchanged.
Native packaging again passed with warnings treated as errors. The installer matrix passed
all 26 checks on this package.

The fast tier passed 5,670 tests, the slow tier excluding HostLoss passed 112, and the focused
attachment/teardown/HostLoss selection passed 24. Deduplicating test IDs across the three TRX
files gives **5,787 distinct passing tests**, with no failures or skips. This is split-run
non-soak coverage, not a new single full-suite invocation. The previous ten-minute passing
protocol soak remains r3 evidence; it was not rerun for the attachment-only change.

Forced termination of the automatically started r4 host helper reproduced the recovery path.
Its replacement now withdraws the previous session's levels before resuming its heartbeat.
The native game reported zero remotes, zero phantoms and zero session command; resolving the
old phantom object ID through the engine returned no body. Re-hosting restored both real
peer bodies. The replacement still waits for Host: automatic process relaunch does not resume
the previous hosting session. A regression also verifies that attachment does not replay the
previous console command.

The verifier now requires both native player objects and module names to remain settled for
five seconds before issuing the return warp. An expanded r3 battery completed with 48 passes,
one roster timing failure and one missing combat-target skip. The staged Sith UTC has Endar
Spire story scripts and ceased to be usable despite raw HP-field staging; this does not prove
a durable combat fixture or isolate the enemy-liveness discrepancy.

In a separate r4 session without staged combat or injected dialogue, the first outward warp
settled both worlds but the client roster was still absent after a 15-second wait. A later
read confirmed it recovered on its own. The normal bridge introduction dialogue ran during
that transition; the exact recovery time was not captured. A subsequent return/outward trip
passed both settled-world and roster checks using a bounded 90-second recovery wait. Neither
r4 agent log contained a `W_GIT` warning. Keep the initial timing failure as evidence of delayed
recovery; neither that failure nor the later pass establishes permanent roster loss or closes
the earlier r3 mapping warning. The repository battery retains its ten-second roster assertion.

Both fixture save trees again passed `saveinspect.py --all --expect-clean`, including autosaves
with both modules and the dedicated Co-op save. This checks peer creatures and map pins, not
inventory conservation. Terminating the host game removed its owned helper; after eight seconds
the joiner showed zero remotes, phantoms, session command and desired/applied pause. Both fixture
games were then stopped and no executable under the fixture root remained running. PowerShell
parsing and `git diff --check` passed. The remaining general-release gates above still apply.

Additional local evidence: `package-r4-build.txt`, `package-manifest-r4.json`, `installer-r4.txt`,
`installer-matrix-r4/`, `tests/helper-restart-regressions.trx`, `tests/more-verification-fast.trx`,
`tests/more-verification-slow.trx`, `live-verification-r3-checks2.txt`, `r4-autostart-verification.txt`,
`r4-helper-reset.txt`, `r4-clean-transition.txt`, `r4-transition-recovery.txt`,
`saved-world-host-r4.txt`, `saved-world-joiner-r4.txt`, `r4-cleanup.txt` and the captured
`r4-final-*-*.log` files.

## Remaining-blocker work: r5

Windows archive: `kotor-coop-0.11.0-readiness-20261003-r5-windows.zip`, SHA-256
`4071B5C92D7EB083B1DFE5A2BBB17AD01983669375A58FAA921E51367431A36A`.
NativeAOT packaging passed with warnings treated as errors; both installed plugin hashes match
the extracted r5 package. All 26 actual-package installer checks passed again.

### Combat and mapping investigation

The test creature is a local UTC derived from the module's Sith soldier, renamed
`coop_test_npc`, with all event scripts cleared and all three template HP fields set to 1,000.
Identical copies were installed only in both disposable game fixtures, not the distributed
package or source game. The engine reported 1,000 base HP and zero temporary HP before testing.
Template SHA-256: `85e148470c819b26b7b0c927e30329529024671df8bf5dca6a4d1ec63c1c14fb`.

- r4 staged battery: 49 passed, one transition failure, zero skips. Client damage reduced the
  host creature from 994 to 991 HP; animation and exact phantom damage also passed.
- Comparing both saved GIT mappings after that round trip found exactly one differing entry:
  the host-only spawned creature, ID `0x12C`. The host saved the genuine creature while the
  client's temporary materialisation was flushed. This explains this run's `W_GIT` warning:
  ignoring the mismatch would permit updates against differing IDs. It does not establish
  that every earlier r3 warning or naturally scripted spawn has the same cause.
- Forced world re-adoption restored the client's host-roster valid bit to `0x80000000`.
- r5 battery: **49 passed, zero failed, one intentional transition skip**. Client damage
  reduced the host target from 998 to 996 HP. Transition verification remains separate from
  this intentionally altered world; the clean r4 transition evidence above is retained.

### Disconnect capture failure and repair

After world adoption, a requested dedicated Co-op save wrote its files but timed out awaiting
completion proof. Restarting the game made the same capture succeed; another adoption reproduced
the failure. Native diagnostics found the save-name buffer length was 25, with its first null
terminator at byte 20. The engine had reused a longer QUICKSAVE buffer for the shorter Co-op name.
The completion hook trimmed only a final terminator and rejected the embedded terminator and
trailing buffer contents, yielding an empty directory name and no accepted output-file proof.

The decoder now reads to the first terminator within the already bounded/readable buffer.
Non-printable bytes before that terminator still refuse the string. Two new regressions failed
before the repair; all three decoder tests and the focused 76 save/persistence tests passed
afterward. Failure diagnostics now distinguish output-I/O rejection from party/world binding
changes. Startup registers the logger callback so the hook remains independently testable.

The actual installed r5 package completed dedicated disconnect capture **1,291 ms** after a
forced world adoption, publishing a verified character checkpoint rather than the old fallback.
A second disconnect after host reload also completed. This closes the reproduced buffer bug;
it is not proof that interrupted writes or native crashes always retain unsaved progress.

### Personal saves and inventory

Set the host to 5,000 credits and the joiner to 777. Compare a named joiner save before adoption
with its completed dedicated Co-op capture afterward: gold, every saved inventory item field
excluding runtime ObjectId, and Pazaak card/deck fields matched exactly. The fixture contained
one medpac item and baseline Pazaak state; this does not cover varied equipment, charge counts
or a custom deck. Bare reconnect followed by host loading its named checkpoint caused automatic
re-adoption; the subsequent named joiner save matched those same fields exactly. One superseded
world-transfer connection closed during that reload; the following transfer/rejoin succeeded.

Both original named baseline copies' SAVEGAME, PARTYTABLE, SAVENFO and GLOBALVARS files still
match the original source save byte-for-byte. r5 quicksaves, autosaves, dedicated Co-op capture
and named r5 test saves contain no peer ghosts or map pins. The all-save scan deliberately
retains a failure: older r4 `000006 - recovery_disconnected` contains one `coop_phantom` creature
after its failed capture/adoption sequence. That save was not erased or repaired; its origin
needs further forensic work. A new r5 disconnected named save contains zero ghosts/map pins.

### Release automation and validation

Numbered releases now require an explicit manual `promote_release` on a tested v* tag. VERSION
bumps and automatic tag CI completion no longer publish numbered releases. Promotion selects
successful push CI artifacts, verifies source/package/tag versions, and reuses those artifacts.
Serialized publication skips superseded main commits. Six local routing cases pass, including
rejection of promotion on main and skipping automatic/unapproved tag publication. YAML and all
embedded shell scripts parse. Hosted workflow execution remains unverified; no release was sent.

The fast tier passed **5,673 tests**, zero failures/skips, in 1 min 46 s. The initial focused
save/character/world-transfer/mapping selection passed 215; after the decoder repair the focused
76 selection passed with warnings treated as errors. The previous r4 slow tier and r3 soak are
unchanged earlier evidence, not new r5 full-suite/soak runs. No VERSION change or commit was made.

Evidence under the same ignored root: `live-verification-r4-neutral.txt`,
`mapping-before-rejoin.txt`, `inventory-*.json`, `save-proof-diagnostic*-*-plugin.log`,
`tests/save-string-regression-before.trx`, `tests/save-string-regression-fixed.trx`,
`tests/remaining-blockers-fast.trx`, `tests/remaining-blockers-recovery.trx`,
`package-r5-build.txt`, `package-manifest-r5.json`, `installer-r5.txt`, `installer-matrix-r5/`,
`live-verification-r5-neutral.txt`, `rig-r5-save-recovery/`, `r5-inventory-conservation.txt`,
`r5-host-reload-conservation.txt`, `baseline-preservation.txt`, `saved-world-*-r5.txt`,
`r5-disconnected-ghost-check.txt` and `release-routing-checks.txt`.

General-release sign-off still requires the independent PCs/network and full campaign gates,
broader crash/inventory recovery, ordinary door progression, retail UI checks and the owner's
license decision. The passing local checks reduce those gaps; they do not waive them.

## Follow-up: teardown/save race repaired in r6

Windows archive: `kotor-coop-0.11.0-readiness-20261003-r6-windows.zip`, SHA-256
`6923E0528015214068B06651493C67E60F51E2BC20559D74682F2F289AC5B089`.
Warning-clean native packaging and all 26 installer checks pass. Both installed native plugin
hashes match the extracted r6 package.

Forensic inspection of the older contaminated r4 save identified the peer creature as object
`0x138`. Its log shows that exact phantom being destroyed at 12:01:44.746, followed by a
quicksave request in the same native frame. The controller cleared its slot when the destroy
request was accepted; the engine still serialised that creature before processing deletion.
The saved appearance had also been reset during teardown. This is a separate race from the
save-name decoder bug: a completed save can still contain an object pending deletion.

Phantom teardown now records accepted deletion requests with both object ID and body address.
Save flush and session drain keep counting those bodies until the native object resolver no
longer returns the same body. World invalidation clears old requests; a reused object ID with
a different body does not hold the new world. Three new regressions cover deferred deletion,
object-ID reuse and world invalidation; the focused save/teardown selection passed 45 tests.

Native r6 reproduction suspended only the owned joiner fixture briefly, stopped its owned
helper, staged session release and a native quicksave together, then resumed it in `finally`.
The plugin requested peer body destruction, held that same-frame quicksave for two frames,
and reissued it after the engine resolved no old body. Fresh completion proof arrived and
the resulting save contained **zero peer creatures and zero peer map pins**. The log confirms
the new pending-body guard was exercised, rather than a save sent after cleanup had finished.

A replacement r6 helper then connected successfully. An ordinary in-session named save and
dedicated disconnect capture also completed; capture took 1,413 ms. Both resulting saves had
zero peer ghosts/map pins. The original contaminated r4 save remains unchanged as regression
evidence. The r5 combat/inventory checks above remain r5 evidence; r6 verification focuses on
the affected save and teardown paths, not a new full combat battery or campaign.

Evidence: `package-r6-build.txt`, `package-manifest-r6.json`, `installer-r6.txt`,
`installer-matrix-r6/`, `tests/pending-deletion-regressions.trx`, `r6-teardown-save.txt`,
`r6-active-save-ghost-check.txt`, `r6-normal-disconnect.txt`, `r6-restored-client-agent.log`
and `rig-r6-teardown-save/`. No package, tag or release was published.

The final r6 fast tier passed **5,676 tests**, zero failures/skips, in 2 min 8 s.
Current save checks and `git diff --check` pass. Native logs were captured before cleanup;
no fixture game/helper executable remains running. Evidence also includes
`tests/remaining-blockers-r6-fast.trx`, `r6-final-*-*.log` and `r6-cleanup.txt`.
## Further gates: running installer, door transition and forced exit (r7/r8)

The r7 Windows archive is `kotor-coop-0.11.0-readiness-20261003-r7-windows.zip`,
SHA-256 `7D99C43010B1534311FFF27D5195220205E20653FD24D96ECBC15B92C8042425`.
Native packaging passed with warnings treated as errors; 26 installer unit checks and
26 packaged fixture checks passed. Two new unit cases hold a later helper file open
and prove install/removal refuse before altering the plugin or creating a chain.

The installer now probes existing game/mod files for write access before mutation.
Against a running native fixture, r7 install exited 2 and uninstall exited 1 with a
clear close-game/helper instruction. Executable, plugin, helper, settings and override
hashes were unchanged; read-only `install --check` exited 0. The immediate retry after
forced game termination still refused while Windows released its files; a later retry
completed an actual r6-to-r7 upgrade. This is early lock detection, not a transactional
installer: a new lock or I/O failure after preflight can still interrupt copying.
Protected folders, independent machines and real third-party chain compatibility remain open.

The clean r6 rig's second menu load timed out after 90 seconds while its frame loop
was parked; the demo's retry recovered it. A join attempted before the host was ready
also timed out and needed another Connect. Both failures are retained. `usedoor`
refused its queued walk with engine failure 8. `crossdoor end_door10`, after staging
near the door, did fire the engine's crossing event: both native worlds settled in
`end_m01ab` with the client roster restored. Host fade to follower warp-start measured
694 ms. This is engine-event coverage, not retail input or uninterrupted progression.

A named checkpoint contained 500 credits and baseline empty inventory/Pazaak fields.
The joiner was changed to 1,234 credits without saving, then its owned native process
was forcibly terminated without a disconnect command. Its helper exited and the host
removed the peer. Loading the checkpoint's quicksave and reconnecting (r6 host/r7
joiner, same protocol) restored those personal fields exactly: the unsaved credits
were lost. Initial reconnection raised W_GIT/W_SAVE and disabled shared channels.
Host save plus `reconnect world` recovered the connection and `localvar: pumping`;
personal fields still matched. A host-save automatic rejoin superseded the first
explicit adoption, so this does not isolate either recovery path independently.

**This sequence uncovered another blocker:** the saved outgoing `END_M01AA` on the host
contained one `coop_mapnote` waypoint. Re-adoption inherited it on the joiner. Checkpoint
and pre-adoption restart saves had zero ghosts/pins; the later re-adopted save had zero
peer creatures but one historical-module pin. The contaminated saves and logs are
retained. Pending phantom deletion could monopolise the transition flush and starve
map-pin cleanup. The r8 change makes pending deletion contribute to standing-body
counts without preventing other registries from flushing, clears synchronous map pins
first during the engine-owned fade, and separately waits for outstanding bodies before
a plugin-owned warp. Focused save/deletion/installer tests passed 40 cases.

Evidence under the dated artifact root includes `r6-next-gates-start.txt`,
`r6-door-attempt.txt`, `r6-door-settled.txt`, `r6-door-timing.txt`, `r7-running-*.txt`,
`r7-running-preservation-hashes.json`, `r7-real-offline-upgrade*.txt`,
`r7-crash-personal-comparison.txt`, `r7-crash-save-ghost-check.txt`,
`r7-resumed-client-agent.log`, `r7-game-*-*.log*`, `installer-r7.txt`,
`installer-matrix-r7/`, and `tests/r8-flush-installer.trx`.

The r9 native ordering-only attempt **failed**: the outgoing module had one peer
creature and zero pins. This exposed a second part of the timing problem: the native
fade can leave only one rendered cleanup frame, so returning after the first registry
still strands the other registry. The final r10 door path gives every registry a turn
in that frame, while the save path waits for pending deletion after all registries have
had a chance to flush. Plugin-controlled travel also waits for the standing-body count.
The failed r9 save/log and the contaminated r7 saves remain untouched.

Final Windows candidate: `kotor-coop-0.11.0-readiness-20261003-r10-windows.zip`, SHA-256
`AB239354084B9CA5E67015C05BB36E6A0EC10535B7B77C534F8553BCDBC226A8`.
Warning-clean packaging passed, installed plugin hashes match the extracted package,
and all 26 packaged installer checks pass. The earlier r9 fast tier passed 5,678 tests
with zero failures/skips in 2 min 51 s; r10 native verification covers the subsequent
door scheduling change. It is not a fresh r10 full automated or combat battery.

The r10 clean-baseline two-player engine-door repeat passed: both native worlds settled
in `end_m01ab`, the joiner roster recovered, and both players' named saves contained
**zero peer creatures and zero co-op map pins in both outgoing and incoming modules**.
Repeating the same-frame session-release/quicksave race also passed: the old native
peer was gone and both saved modules had zero ghosts/pins. These checks cover one peer
and baseline ground state; transitions with several peer bodies, many replicated items,
materialised enemies or placed mine replicas still need stress coverage. The refused
`usedoor` walk and retail-input/full-campaign gates remain open.

Final evidence: `package-r10-build.txt`, `r10-provenance.json`, `installer-r10.txt`,
`installer-matrix-r10/`, `install-r10-game-*.txt`, `r10-door-start.txt`,
`r10-door-check.txt`, `r10-teardown-save.txt`, `rig-r10-door/`,
`r10-final-game-*-*.log*` and `r10-cleanup.txt`. Nothing was published.

## Three additional native rounds on r10

The same r10 package was used; no runtime code changed in these rounds.

1. A clean two-player outward/return plugin-controlled warp pair passed: both native
   modules settled and the joiner roster recovered on both legs.
2. A fresh-process two-player session staged six shared medpac floor items through the
   dashboard drop-file route. All six were verified materialised in each native game
   before the host engine-door crossing. Both players arrived in `end_m01ab` and both
   named saves contained zero peer creatures/map pins. Inspection of each saved module's
   GIT `List` also found zero floor-item replicas in both outgoing and incoming modules.
3. Returning to `end_m01aa` rebuilt all six shared items on both instances. Repeating
   same-frame helper termination/session release plus native quicksave passed: the old
   peer no longer resolved, both saved modules had zero peer creatures/map pins, and
   both floor-item lists were empty. The installed host package's read-only health check
   exited 0. This verifies live session item reconstruction, not durable ground-item
   recovery after restarting the host or real inventory acquisition through retail UI.

**Retained anomaly:** after the first warm warp round trip, `crossdoor end_door10`
reported `no LinkedToModule - an ordinary door`; an in-process host baseline reload
and automatic joiner re-adoption still produced that refusal. The subsequent settled
module check failed as expected because no crossing occurred. Fresh native processes
loaded from the same baseline and crossed that door successfully with the six-item
load. This is an unresolved warm-world/door-helper observation; it does not establish
whether a retail door click fails or whether the helper resolved stale metadata. Do
not count the warm round trip as ordinary player-driven door progression. Reproduce
with retail input and record door identity/destination before and after reload.

Evidence under the same dated artifact root: `extra-round1-warp.txt`,
`extra-round2-items-stage.txt`, `extra-round2-door.txt`,
`extra-round2-baseline-reload.txt`, `extra-warm-game-*-*.log*`,
`extra-fresh-six-items.txt`, `extra-fresh-six-items-door.txt`,
`extra-fresh-saved-floor-items.txt`, `extra-round3-return-items.txt`,
`extra-round3-teardown-save.txt`, `extra-round3-floor-items.txt`,
`extra-round3-install-health.txt`, `rig-extra-rounds/`, `rig-six-items-fresh/`,
`extra-final-game-*-*.log*` and `extra-rounds-cleanup.txt`.
Eight native players, materialised enemy populations, mine replicas, real campaign
progression and separate-PC internet play remain unverified. Nothing was published.

## Human playtest: host input and presentation blockers

The visible two-game r10 session is recorded in
`human-playtest-20261003-132811/`. The tester confirmed that save-name input,
slow locker opening and slow dialogue presentation occurred in the left-hand host
game. Logs at the initial report were preserved in `reported-issues-133259/`.

- **Startup diagnostics:** the successful bridge's memory-address summary printed
  before dashboard filtering, and config loading printed the developer-switch notice
  directly. Both now go to the agent support log; developer switches remain visible
  in F1 and invalid/unknown config warnings retain their existing handling.
- **Save naming / Enter:** reproduced with retail UI on the running host. Clicked
  New Slot, Save, and the edit field; a physical `a` appeared. Pressing Enter opened
  the `say` console above the still-open native save-name popup and emitted the
  empty-message notice. This confirms chat could steal input from the save UI.
  `ChatInputLatch` now excludes full-screen GUI and co-op text modals, and consumes
  an Enter hold until release even if its original UI closes. Five regression cases
  pass. This source fix has not yet been retested in a running native game.
  Closed chat, used the native OK button and confirmed a new `000011 - Game10`
  save/sidecar at 13:42:31. Returned the host to the world; no previous save was
  overwritten and neither human-playtest game was terminated.
- **Locker delay:** the initial log proves an arbitration grant but does not contain
  its request time. The new plugin records request serial/time and elapsed grant,
  denial or timeout, plus whether the subsequent use request was queued. A failed
  queue now withdraws the unused one-shot grant. This is diagnostic coverage, not
  a proven latency fix. The current path reissues UseObject after deferring
  OpenInventory; distinguish host-answer delay from repeated engine interaction
  pacing before changing native calls or weakening container exclusivity.
- **Dialogue bars, subtitles and skip delay:** unresolved on the host. The peer's
  log received dialogue nodes about 47ms after the host and joined approximately
  half a second later; those observations do not explain slow host presentation.
  These disposable fixtures have `Sound Init=0` and subtitles enabled. Repeat with
  sound initialized and frame/pump timing enabled before assigning causality.
- **Enemy-sighted pause banner while movement continues:** unresolved. The host's
  auto-pause settings screen currently shows the options off; this does not prove
  an earlier event or stale presentation was suppressed. Existing manual HUD-button
  pause/unpause reached both games. Capture GUI pause belief, both masks/timers and
  the actual notification identity at recurrence; do not label the banner fixed.

Validation: 73 focused chat-latch, container-arbiter and configuration tests passed,
zero failures/skips. Native x86 warning-as-error builds passed. The startup/input
changes and container diagnostics require a new package and native UI retest;
the live human session still runs r10.

The r11 warning-as-error Windows/Linux packages were built successfully; all 26
packaged installer checks passed (`installer-matrix-r11/`). Windows archive:
`packages-r11/kotor-coop-0.11.0-readiness-20261003-r11-windows.zip`, SHA-256
`A17B195DC8E613717D53A35F60DB2FED43BAAE4D12746243275C8B76DB70E9E0`.
Extracted candidate is `package-r11/`. It has not been installed into the running
human-playtest games. No release was published.

### Interaction fixes and diagnostic verification (r12)

- Container arbitration now captures OpenInventory's actor and feedback arguments
  and resumes that same native call after a grant. Replaying UseObject discarded
  the arrived interaction and repeated engine pacing. The capture is consumed once,
  matched to its target/serial, and invalidated on world changes; the peer-held gate
  still runs. Two diagnostic opens resumed 15 ms and 141 ms after their requests.
  The native container inventory panel was subsequently observed with its items.
- A byte-checked hook on the actual pause-panel producer suppresses only paused=1,
  reason=1 (enemy sighted), while co-op keep-alive is enabled. Controlled calls on
  the diagnostic game's main thread left GUI intent/banner at 0/0 for that reason;
  manual reason 4 produced 1/1, and its unpause restored 0/0. This is producer
  verification, not a natural two-player enemy encounter. The candidate logs the
  first five suppressed notifications, alongside container request/grant timings.
- World-timer reconciliation now skips full-screen GUI, conversations and text
  modals, whose intentional timer state it must not repair. A diagnostic Trask
  conversation displayed subtitles and reached its reply choices. Slow initial
  bars and delayed physical skipping were not reproduced or conclusively verified.
  The fixture was launched with Sound Init=1, but its INI subsequently read 0;
  therefore successful audio initialization is not established by that setting.
- Separate diagnostic game/agent and logs live in game-interaction-check/ and
  interaction-check-agent.log. Human host/follower processes and their saves were
  left intact on r10. No original game installation was changed.

Validation: 29 focused regression tests passed; the full fast suite passed 5,695
tests, zero failures/skips. Native x86 and r12 packaging use warnings as errors.
The new native probes exercised the built plugin before the final bounded logging
addition. Save naming, natural encounter synchronization and two-player dialogue
skip remain required human confirmation; this candidate is not signed off for
public release.

The r12 Windows/Linux packages built successfully with warnings as errors; all 26
packaged installer checks passed (installer-matrix-r12/). Windows archive:
packages-r12/kotor-coop-0.11.0-readiness-20261003-r12-windows.zip, SHA-256
F2AE4EE15095A73FBEF1B7D9560CFE3F293698688820E004FEACAD4AB456FE45.
The extracted candidate is package-r12/. It is ready for the next launch; the
currently running human games still use r10. No release was published.

### Save-return enemy-sighted panel (r12 human report; r13 fix)

The r12 human session started at 14:14 Brisbane time (host 62404, follower 40788).
The host loaded Game10; the follower adopted its world automatically with its own
character and then resumed shared channels. Both rendered one native peer body;
only Malak had auto-follow enabled. Save Game11 completed at 14:19:53. After returning
to play the host showed ENEMY SIGHTED / PRESS THE PAUSE BUTTON TO CONTINUE.
The screenshot and read-only native state confirmed a different path from the
controlled r12 producer check: GUI+0xB38=1, GUI+0xB3C=0, client/server masks=0,
and both world-timer paused flags=0. The follower intent/belief were both 0.
Evidence was copied before mutation into the human-playtest-r12 session's
enemy-banner-after-save-042132 directory. This refutes complete r12 banner coverage.

r13 reconciles this orphaned panel through SetPauseVisuals(gui, 0, manualReason).
It requires co-op keep-alive, no pause mask, no GUI pause belief and two running
timers; existing full-screen GUI, conversation and load guards still apply.
The timer, mask and real-GUI-pause checks preserve player, peer and script pauses.
Eight added regression cases (20 interaction tests total) passed. A separate native
diagnostic instance recreated intent=1/belief=0 with the engine's ordinary reason-0
producer; within the 250 ms observation both fields were 0 and the reconciler logged
the cleanup. A reason-4 manual-pause control remained 1/1, and unpause returned 0/0.
This verifies the captured-state cleanup, not a retail save-button cycle on r13.

Warning-as-error native x86 and Windows/Linux packages passed; all 26 packaged
installer checks passed (installer-matrix-r13). Windows SHA-256:
EBCDAA3462912957744CAF080CF8929C71F7AF0B5B9856DD9C1530EA87EB663F.
Candidate: packages-r13/kotor-coop-0.11.0-readiness-20261003-r13-windows.zip,
extracted into package-r13. The running human games remain on r12 with progress
preserved. No public release was published.

### r13 human reports and r14 follow-up

Both human games were subsequently restarted on r13 at 14:29 Brisbane, host
41360 and follower 25692. Host loaded Game11; follower adopted the world with
its own character; only Malak followed Lanik. The host's orphan-panel cleanup
was logged at 14:38:04. At 14:38:27 the host recruited Trask into active slot 0.
The follower had recruited exact `end_trask` into the inactive cache, but the
nonstock identity gate held its active party; the host requested a replacement
save refresh even though this was normal tutorial recruitment.

The user also reported dialogue starting over the host's already-open equipment
screen. Logs were copied into the r13 session's `dialogue-menu-trask-report/`
before further diagnostic work. Both human game processes had exited by the
subsequent read-only party probes; no restart of their progress was attempted.

r14 permits the exact first `end_trask` admission in slot 0 as a stock companion
in both the managed saved-binding rule and native mirror gate. A repeated/replaced
admission, another slot or another template still needs the previous saved proof.
The native local identity check accepts first-admission Trask, allowing his
already-recruited cache to reconcile without a world re-adoption.

r14 also defers new queued conversations while a co-op full-screen menu is open.
This happens at AIActionDialogObject, before StartDialog can construct/refuse
a conversation. Static checks confirmed pending status 3 at 0x745DC4, the ret-4
cleanup at 0x57B285, and RunActions' result-1-only free path at 0x5800F2; pending
actions are requeued. The existing constructor gate's forced-story behavior is
preserved. An isolated r14 engine-loaded diagnostic returned 3 through the native
entry with the menu flag set; callback checks returned defer=1 while busy and
defer=0 after clearing it. IAT and GUI state were restored in each case. This
is a controlled flag test, not proof of retail equipment UI or story execution.

The r13 full suite passed 5,821 tests, zero failures/skips, in 11m35s. Its
separate opt-in ten-minute lossy/reordering/reconnect network soak passed.
A later build attempt encountered the running soak's locked Protocol.dll;
tests were then rebuilt after the soak completed. The r14 full suite passed
5,830 tests, zero failures/skips, in 11m26s. A subsequent solution build passed
with zero warnings/errors under warnings-as-errors. Native x86 and Windows/Linux
r14 packages passed with warnings
as errors. The packaged installer matrix passed 32 checks, including unchanged
project license, legal gates and third-party inventory in both install layouts.
The final 38 party saved-binding, occupant and dialogue-menu tests passed after
adding the native mirror-gate assertions for first-admission versus replaced Trask.

The license permits personal noncommercial play and official-project contributions,
restricts independent releases/commercial use and preserves platform/statutory/
third-party rights. [LEGAL.md](LEGAL.md) records outstanding legal review, extracted
game-media permissions/history, contributor authorization and full dependency
notice audit. No legal clearance or public campaign sign-off is asserted.

r14 Windows archive SHA-256:
`C777371F0511A14E13EB2C979B24226164EEC7264A6B2F4E3454D88D9B0E9A2C`.
Six checks confirmed that Windows and Linux archives contain the three legal
files unchanged. The Windows player archive contains no game executable or
trailer audio/video; this does not clear the separate repository media gate.

Required retail retest on r14: load a checkpoint before Trask's forced tutorial
conversation, open host equipment and equip an item, then keep the menu open
when his conversation is queued. It must remain usable with no dialogue overlay;
closing it must allow the conversation and subtitles to start. Finish recruiting
Trask, switch host control to him, open the tutorial door, complete its forced
conversation and return control to the PC. The follower must still show the same
Trask body and resume following. Also save a named slot and return to play to
check text entry and the orphaned enemy-sighted panel. Preserve both logs and
checkpoint saves. Controlled diagnostics and automated tests do not replace these
steps.

### r15 public-readiness friction follow-up

The main installer now previews player settings that match historical defaults and
offers opt-in migration to the exact incoming KotorCoop.DEFAULTS.ini template.
Declining preserves settings. Accepting first backs up coop.ini; custom values,
comments and line endings are preserved, and locked-game refusal happens before
settings or backups are written. The PowerShell wrapper delegates -UpdateDefaults
to this same implementation. A syntax error in the legacy PowerShell drift table
was also corrected; that helper is no longer shipped or used by the wrapper.

KotorCoop.CollectLogs.cmd runs the interactive support collector. Inside a game
folder it selects that install; from an extracted package it offers game-folder
selection as needed. Bundles are local, redact password/host settings, and upload
nothing. The existing warning about IP addresses and character names in logs remains.

The native liveness overlay now retains a re-host instruction when a previously
hosted helper recovers into menu mode without an active session. It clears when a
session is established. This is guidance, not automatic session restoration; visible
retail verification and the earlier dialogue/Trask/save-menu retests remain open.

Validation: the non-Slow/non-Soak tier passed 5,715 tests, zero failures/skips, in
2m57s. Final focused migration/logging/installer/options checks passed 58 tests,
including refusal with a game image locked against writes. A rebuild attempted while
the broader test process was still running hit a file lock; the focused checks were
rebuilt and rerun successfully after that process finished. Native x86 and Windows/
Linux packaging passed with warnings as errors. The package matrix passed 48 checks,
covering plain/chained layouts, migration opt-in, backups, custom settings,
interactive support routing, notice copies and removal of the log shortcut.

The solution-wide NuGet audit including transitive dependencies reported no known
vulnerable packages. Exact agent dependency versions are recorded in the third-party
inventory; full copyright/license texts, embedded-material review and legal clearance
remain open. Contributor guidance explicitly welcomes forks intended to submit PRs
back to this project. No public visibility change or numbered release was made.

Final local r15 Windows archive: kotor-coop-0.11.0-readiness-20261003-r15-windows.zip.
SHA-256: `5ED584A4C01EE883F99C003422460A7C4306362DAB78F9380ECA3CC0DC2ADEB3`.
The final rebuilt archive passed the 48-check package matrix. This is a local,
pre-commit candidate; do not treat it as a hosted release artifact.

### r16 dependency-notice collection

Collected full upstream library licenses, .NET 10.0.12 runtime/NativeAOT notices,
Onigwrap notices and available embedded-grammar licenses into third-party/NOTICES.txt.
Recorded exact published dependencies, NuGet author/copyright/source metadata and
57 upstream grammar repository revisions. A malformed Go grammar source URL was
corrected for retrieval while preserving the original recorded provenance.

Packaging now ships the full text plus the inventory, verifies notice/provenance
hashes, and refuses changes to the published dependency set or NativeAOT runtime
version until reviewed. Notice/provenance bytes are preserved across Git checkouts.
The valid staging path and rejection of an injected unreviewed dependency passed.
Warnings-as-errors Windows/Linux packaging passed; the actual Windows package
passed 50 installer checks, including unchanged installation of all four notice/
legal files. No gameplay code changed and the gameplay suite was not rerun.

The audit is not complete: cshtml's pinned upstream source is unavailable, the
assembly grammar lacks a pinned source revision, and embedded themes need complete
provenance/notice mapping. These specific cases remain public-distribution gates.

### r17 beta preparation and recovered notices

Recovered cshtml's MIT copyright notice from Microsoft's pinned VS Code 1.50.0
ThirdPartyNotices, whose razor cgmanifest names the identical grammar revision.
Matched the assembly grammar against its archived upstream after newline/final
whitespace normalization and collected its MIT notice. Recorded hashes and notice
sources for all 21 bundled themes, including full Microsoft, Atom, One Dark/Light
conversion and Dracula notices. Source URLs/revisions are retained in
third-party/embedded-resource-provenance.json; packaging verifies its hash too.
The valid guard passed and altered provenance was rejected.

Prepared VERSION 0.1.0-beta.1, explicit beta download links and player release notes.
Replaced the active readiness page with current remaining gates, preserving earlier
evidence here. Publication now requires manual tagged promotion of tested CI
artifacts; no automatic rolling release is recreated and beta tags stay prereleases.
Workflow YAML and Release Bash syntax checks passed. Warnings-as-errors native x86,
Windows and Linux packaging passed; the local Windows beta passed 50 installer
checks. This is fixture/package evidence, not a retail gameplay retest. Hosted CI
and publication results belong to the subsequent tagged promotion record.

Existing release metadata and assets were backed up under ignored artifacts before
replacement. Source tags/history and repository visibility are preserved. The owner
authorized this experimental beta; human gameplay, independent-PC/network, complete
campaign and game/media/custom-license legal gates remain open.
