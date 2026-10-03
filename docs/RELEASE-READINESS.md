# Public release readiness

Reviewed 3 October 2026 against committed `bb066bd0cf17d9af27dd6e231072e79fb19f2d30`.
This is a repository review, not a new live-game verification session.

**Publication decision: hold.** The build and package pipeline is healthy, but legal
distribution gates and confirmation of reported gameplay defects remain open. The
repository is still private. Neither passing CI nor adding a license clears those gates.

The r15 follow-up implements opt-in player-default migration, double-click support
collection and a re-host notice. Its non-Slow/non-Soak tier passed 5,715 tests; final
focused checks passed 58 and packaged installer checks passed 48. See the
[r15 evidence](release-verification-2026-10-03.md#r15-public-readiness-friction-follow-up).
Hosted results below are for the preceding bb066bd commit, not this newer candidate.

### Current candidate and hosted evidence

- Local r14: 5,830 tests passed, zero failures/skips; warnings-as-errors builds and
  32 packaged installer checks passed. The preceding r13 candidate also passed a real
  ten-minute lossy/reordering/reconnect network soak. That soak is not an r14 live-game run.
- [Hosted CI](https://github.com/ChristopherVR/kotor-coop/actions/runs/37098628209)
  passed on `bb066bd`, including Windows package verification, package/symbol uploads
  and all Linux installer steps. Linux installer checks do not establish Linux gameplay support.
- [Rolling release publication](https://github.com/ChristopherVR/kotor-coop/actions/runs/37099622880)
  succeeded. The private `latest` prerelease contains Windows/Linux packages versioned
  `0.11.0-11-gbb066bd`. These hosted archives are distinct from the local r14 archive;
  record and test their own hashes before promotion.
- Website download buttons use `/releases/latest`, which currently resolves to stable
  `v0.11.0` at `0e3a313`, published 1 October, rather than the rolling tag named `latest`.
  That stable build predates the current fixes and license. Align every download link
  with the approved candidate before launch; do not silently route players to an unapproved build.
- [Pages deployment](https://github.com/ChristopherVR/kotor-coop/actions/runs/37098628177)
  failed at `configure-pages`: the Pages site API returned `Not Found`. Configure the
  repository's Pages source as GitHub Actions when publication is authorized, rerun
  deployment, and check the public install/download journey while signed out.
- CI symbols currently expire on 2 November 2026. Archive symbols for each supported
  release independently of the 30-day CI retention.

Follow-up verification is recorded in [release-verification-2026-10-03.md](release-verification-2026-10-03.md):
full automated, actual-package installer and local live-game checks. The independent-PC,
outside-network and complete-campaign gates below still apply.

Final local follow-up r10 verifies one two-player engine door crossing and clean outgoing/
incoming saved modules, including a newly found map-pin/phantom flush starvation fix. A
forced native exit restored checkpoint personal fields but lost unsaved credits; initial
reconnection needed world re-adoption to restore shared channels. Running-game install and
removal now refuse before changing files. These bounded checks do not close retail-input,
larger transition cleanup, full campaign, protected-folder or separate-PC gates.
Three further r10 rounds verify a settled warp round trip, a fresh engine-door crossing
with six shared floor items, item reconstruction on return, and clean teardown-frame
serialization under that load. A warm round trip/reload made the door helper report a
missing destination; retail-input reproduction and door metadata inspection remain open.

The current evidence is useful preparation for a limited public playtest; the publication
gates above and below must close first. It does not yet support promising a frictionless,
complete campaign. Prioritise two players on separate Windows PCs,
matching game mods and the same co-op package. Eight-player staged tests do not establish
eight-player campaign reliability.

### Order of work before opening access

1. Close [LEGAL.md](LEGAL.md): review game/store terms and original-code ownership,
   resolve extracted game media in both current files and accessible history, review
   promotional footage, and ship full notices for the exact packaged dependencies.
   Full collected notices now ship from third-party/NOTICES.txt. Two embedded grammar
   origins and theme provenance remain unresolved in THIRD-PARTY-NOTICES.md.
   Forks intended for pull requests back to the official project are explicitly allowed.
   Public GitHub platform permissions also apply; restrictions concern independent
   releases and commercial reuse, within the project's rights.
2. Freeze one packaged candidate and record its hash. Retest the r14 dialogue/menu and
   Trask fixes using ordinary gameplay; also confirm save-name input, containers,
   subtitles/skipping and the post-save enemy-sighted banner. Preserve both logs and saves.
3. Have two players install that download on separate ordinary Windows PCs without
   developer tooling. Cover upgrade/removal, join UI, LAN and a genuinely external
   connection, save/reload, disconnect/rejoin and host recovery. Start with matching
   game mods; advertise only store/network combinations actually checked.
4. For a limited playtest, publish explicit limits and known issues, a tested download,
   and a usable support-bundle path. Verify numbered promotion, website links and Pages;
   retain its symbols. Review accessible history for secrets/private support data before
   changing repository visibility, and provide a private security-reporting route.
5. Before recommending a general campaign release, finish a natural two-player run
   through an ending, the save/inventory matrix, and multi-hour recovery checks below.
   Keep three-to-eight-player and unplayed story-branch claims experimental until verified.

## Required before recommending a general release

| Priority | Gap | What closes it |
| --- | --- | --- |
| P0 | Legal and distribution review remains open | Complete [LEGAL.md](LEGAL.md): game/store terms, extracted trailer media and accessible history, contributor authorization, full dependency notices and custom-license review. A disclaimer is not legal clearance. |
| P0 | r13 human reports: dialogue over host equipment UI and missing follower Trask | r14 defers queued dialogue while a full-screen menu is open and accepts the exact first stock Trask admission. Automated and controlled native checks pass. Still verify menu usability, conversation resumption, subtitles, companion switching and Trask's door conversation with retail controls on the frozen candidate. Passing automated tests does not close these reports. |
| P0 | Natural gameplay transitions still need final-candidate coverage | r5 passed 49 native checks with no failures and one intentional transition skip using a scriptless, engine-loaded 1,000-HP target. The r4 staged round-trip mapping warning was traced to that host-only test creature; re-adoption restored the roster. Separate clean r4 round trips passed, with one earlier delayed roster recovery retained. r6 fixes and verifies the historical r4 ghost's same-frame teardown/save race. Repeat ordinary door progression on the final candidate; the earlier r3 liveness/cleanup evidence still needs natural-gameplay coverage. See the dated follow-up. |
| P0 | No uninterrupted two-player campaign through the ending | Play from a new game through natural progression without developer warps. Keep checkpoint saves and both players' logs. Cover recruiting, Jedi training, Leviathan gear return, Manaan puzzles and sea-floor return, temple choices, final duels and ending. Record which branch was completed; do not certify the other branch implicitly. |
| P0 | Fresh installation and network reachability still lack independent-PC evidence | On two ordinary Windows PCs, use the actual packaged download as a non-admin user. Test GOG/GOG, Steam/Steam and mixed stores, LAN discovery, password entry and a join from a genuinely different internet connection. Confirm firewall guidance works when automatic rule creation is unavailable. Verify a tunnel separately and record its protocol and endpoint setup. A local self-test does not prove inbound internet reachability. |
| P0 | Save and inventory recovery coverage is incomplete | Fixed a reproduced completion-proof timeout after world adoption and, in r6, a same-frame teardown/save race that baked a peer body into a save. r5 verifies forced world adoption, dedicated disconnect capture, reconnect and host reload while preserving 777 personal credits, one inventory item and baseline Pazaak fields; original named baseline files match byte-for-byte. r6 verifies the teardown-frame quicksave and ordinary save/disconnect paths. Still test interrupted capture/adoption, native game/agent crashes with unsaved progress, restart/resume, varied equipment/stacks/charges, a custom Pazaak deck, full wipe/rescue and retail save-menu controls. |
| P1 | UI-driven gameplay evidence is thinner than console-driven evidence | Run [PLAYTEST.md](PLAYTEST.md) with retail clicks and keys: address/password boxes, invalid addresses, party selection, companion equipment, merchant buy/sell and persuasion, dialogue choices, waiting notices and recovery. A successful command invoking an engine handler does not prove its visible UI. |
| P1 | Upgrade and removal need a clean-machine package matrix | Install, update, check and uninstall plain and chained installs; preserve custom coop.ini and the original third-party DLL, including an existing chain configuration. Test a path containing spaces, a Steam library on another drive, a protected install folder and attempted update while the game is running. Failed checks must not offer a launch. |
| P1 | Long sessions and wider multiplayer coverage remain bounded | Repeat a multi-hour session with varied modules and ordinary doors, saves, combat and reconnects. Record CPU/memory and crashes. Expand to three and eight players before broadening campaign claims. Investigate recurring crashes with dumps rather than assuming they belong to the base engine. |
| P1 | Numbered release promotion and website publication need verification | Hosted CI and rolling prerelease publication pass on bb066bd. Explicit manual promotion on a tested v* tag remains unverified. Align source/package/tag versions and website download links, resolve the Pages Not Found failure, and retain release symbols. Passing CI alone is not campaign approval. |

Use [limitations.md](limitations.md) for player expectations, the
[dated evidence log](research/limitations-evidence-log-2026-10-02.md) for supporting evidence,
and [live-verification.md](live-verification.md) for the rig checks. Older status tables contain
historical open items and later resolutions; reconcile each claimed status against dated evidence
before copying it into release notes.

## Earlier review changes and validation

- Corrected the packaged README's complete-campaign, GOG-only and shared-inventory claims.
- Made separate-folder installer use the default in the repository, download README and generated
  release notes. Direct extraction cannot preserve an existing DLL or settings file.
- Corrected troubleshooting's stale claim that command-line installs require an explicit chain flag.
- Corrected the website's connection port: shipped settings use 27140; the code fallback is 27015.
- Stopped setup offering a game launch when its post-install health check fails.
- Included chain-loader failures and the stamped collecting/installed versions in support bundles.
- Added a bug-report form requesting build, host/joiner role, network, mods, reproduction and logs.
- Restricted automatic release handling to successful push runs from this repository, excluding PR runs.

Validation during that earlier review: `dotnet build KotorCoop.slnx --no-restore -warnaserror`
passed with zero warnings/errors; the fast tier (`Category!=Slow&Category!=Soak`) passed
5,664 tests with no failures or skips. The release workflow and issue form parsed as YAML;
`git diff --check` passed. Native packaging, GitHub workflow execution, the slow/soak tiers
and live-game checks were not run in that initial review. Later r14 and hosted results
are recorded at the top of this page; this publication review changed documentation only.

## Remaining friction to address after the first validation pass

1. **Settings drift:** the next candidate's main installer previews settings matching historical
   defaults, preserves them unless explicitly accepted, and backs up coop.ini before migration.
   The PowerShell wrapper delegates `-UpdateDefaults` to the same agent implementation.
   KotorCoop.DEFAULTS.ini ships the exact incoming template separately. Custom values differing
   from old defaults are preserved; matching values may still be deliberate, so consent is required.
2. **Support without a terminal:** the next candidate ships KotorCoop.CollectLogs.cmd, which collects
   the current game install or offers a game-folder selector from an extracted package. It creates
   a local zip, redacts settings secrets, and uploads nothing. Verify double-click UI on a clean PC.
3. **Mod compatibility:** explain the actual disabled channels and recovery in the join UI. Recommend
   matching content for a first session. A successful connection with degraded shared state is not
   a compatible campaign.
4. **Evidence consistency:** PLAYTEST and historical engineering reviews mix current checks with old
   descriptions (including shared inventory and already-resolved items). Keep the release checklist
   focused on the exact candidate; retain historical evidence without presenting it as current status.
5. **Release provenance:** verify tag, VERSION, archive filename and embedded version agree. Retain
   symbols for supported releases beyond the current 30-day CI retention so older reports remain
   diagnosable. Prevent an older completed CI run replacing a newer rolling release.
6. **Distribution metadata:** the custom top-level LICENSE, legal gates and third-party inventory
   now ship in both packages, with unchanged-copy installer checks. Full collected upstream
   copyright/license texts now ship with version guards. Complete the remaining embedded
   grammar/theme provenance audit before public distribution.
   Custom-license and game/media legal review also remain open.
7. **Helper recovery:** automatic helper launch works on the r3 package. After a forced helper exit,
   its replacement waited in menu mode and the joiner remained in reconnecting state for 120 seconds.
   The next candidate adds a persistent in-game re-host instruction after a previously hosted
   helper recovers into menu mode; its visible behavior still needs retail verification.
   Verify host-side stale peer bodies
   and locks are cleared while no session is active. Relaunching a process must not be labelled
   automatic session recovery.

   Follow-up r4 verification fixed stale session levels on replacement attachment and proved
   the old native peer body was destroyed before re-hosting. The replacement still waits for
   Host; a clear visible recovery action remains necessary. A clean transition also briefly
   withdrew the client roster beyond a 15-second check before recovering; subsequent settled
   round trips passed. Retain this timing evidence alongside the unresolved staged-combat and
   mapping warnings in the [verification record](release-verification-2026-10-03.md#further-verification-r4).

## Candidate sign-off record

### Human playtest blockers (2026-10-03)

The left-hand host playtest reported save-name input loss, slow container opening,
slow dialogue bars/missing subtitles/delayed skipping, and an enemy-sighted pause
banner while movement continued. Enter opening chat over the native save popup was
reproduced and fixed in source; native retest remains required. Startup diagnostic
noise is also fixed in source. The r12 candidate resumes the captured container open
instead of replaying UseObject, suppresses the enemy-sighted banner when co-op keeps
simulation running, and excludes menus/dialogue from world-timer reconciliation.
Native diagnostic checks passed for the container and pause producer; subtitles were
visible in a diagnostic conversation. Two-player retail confirmation, including
dialogue skipping and save naming, remains a public-release gate. See the
[human-playtest record](release-verification-2026-10-03.md#human-playtest-host-input-and-presentation-blockers).

The r12 human retest reproduced the enemy-sighted panel after saving and returning
to play. Captured state showed GUI intent=1, belief=0, both pause masks=0 and both
timers running. r13 clears this orphaned panel through the native unpause visual
setter only in that state; diagnostic native verification and manual-pause controls
passed. Actual save-screen return on r13 still requires human confirmation.

For each release candidate, record:

- Commit, package filenames, SHA-256 values and embedded version; use identical packages on all peers.
- Windows version, game store/build, game mods, player count and LAN/direct/tunnel topology.
- Each checklist result, date, tester, logs and save checkpoints; mark missing evidence **unverified**.
- Every remaining reproducible defect, player-visible workaround and link to the known limitation.
- Decision: public playtest or general release, with the exact tested scope and notes.

Do not change VERSION or publish as part of checklist maintenance. Promote only the candidate
whose package was tested. Recheck affected scenarios after changes to that candidate.
