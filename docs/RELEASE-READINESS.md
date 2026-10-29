# Release readiness

kotor-coop is an **experimental beta**, developed and released from this one repository.
This page says what a release must pass before publication, and what stays
unverified and is disclosed instead.

## Before pushing to main

1. Every successful push CI on `main` triggers a release. The workflow increments the highest
   release tag automatically; raise `VERSION` above it only to select a new version series.
   Optional `docs/release-notes/public-playtest-<version>.md` notes should stand alone for a
   reader who has never seen the repo. Without them the workflow supplies installation notes.
2. `dotnet build KotorCoop.slnx -warnaserror` and `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError`
   are clean (the solution build does not compile the plugin), and
   the **full** suite passes on your machine with `pwsh tools/test-full.ps1` (CI skips the four heaviest
   real-socket classes, tagged `LocalOnly`, to keep CI around five minutes of tests).
3. The Release workflow's packaged-installer check passes (`tools/verify-package.ps1`).
   It runs automatically before publication; rehearse it with **Actions > Release > Run workflow**
   and **dry_run** checked, which keeps the packages for three days instead of publishing.
4. Run the affected scenarios from [live-verification.md](live-verification.md) on a two-instance
   rig with the exact packaged bytes, and record what you ran. Mark anything not run **unverified**.
5. Push to `main`. After CI succeeds, the workflow publishes the setup program,
   Windows zip, Linux tarball and `SHA256SUMS.txt` to a GitHub release marked **Latest**.
   Beta status stays in the version and release notes; GitHub's prerelease flag is off so the
   release appears in the repository sidebar. It then uploads to Nexus Mods and updates the mod page's version;
   see [nexus/README.md](../nexus/README.md) for one-time configuration.

Releases are built from the tag by GitHub Actions, never from a developer machine. The files are
not code-signed. The tagged source archive that GitHub attaches to the release is the GPL
corresponding source ([CORRESPONDING-SOURCE.md](CORRESPONDING-SOURCE.md)).

## Known open items for the current beta

- **Container exclusivity.** A same-moment open of one container by two players has been reproduced
  despite arbitration; a fix is in the tree and needs ordinary in-game retesting.
- Recent fixes for dialogue starting over the host's equipment menu, Trask disappearing on the
  joiner's screen, save-name input, dialogue presentation and the post-save enemy-sighted banner
  still need ordinary in-game confirmation.
- Legal: no publisher permission is claimed for a fan mod of this kind. See [LEGAL.md](LEGAL.md).

## Scope to disclose rather than claim verified

The beta is offered free and experimental, with the following limits. Checks that have not been run are not claimed as done, and they limit support and any general-reliability recommendation.

- Start with two players on Windows, lawful KOTOR 1 v1.0.3 installations, matching mod versions and backed-up saves. Controlled evidence is strongest for same-PC GOG pairs. Describe Steam and mixed-store results by the configuration that was actually tested, without generalizing to all installations.
- Independent-PC/non-admin installation, two-account Steam friend delivery, genuinely external networking, firewall/forwarding/tunnel combinations and clean-machine upgrade/removal remain unverified unless a recorded result exists. Direct joining is the baseline; Valve's optional DLL is not shipped.
- A natural two-player campaign through an ending, ordinary Trask/recruitment, Jedi training, Leviathan equipment return, Manaan/sea-floor, temple and final-duel paths are not certified by warps or staged captures. Menu/subtitle/skipping/pause behavior still needs wider human use.
- Multi-hour ordinary sessions and three-to-eight-player campaign reliability remain experimental. The original boss has bounded rendering, arena-entry, loss/return and yield/return evidence; full animation, normal combat balance and ordinary victory are not established by it.
- Adoption/reconnect successes do not promise lossless crash recovery. Unsaved progress can be lost. Equipment/stacks/charges, Pazaak, interrupted transfers, helper/game faults and party wipes need broader ordinary use.
- Linux installer checks establish packaging behavior, not Linux gameplay support. The release pipeline packages the Windows setup program and zip, and a Linux tarball for the Proton/Wine installer.

Use [PLAYTEST.md](PLAYTEST.md) for ordinary controls, [live-verification.md](live-verification.md) for controlled rig checks and [limitations.md](limitations.md) for player-facing limits. Do not imply automated/injected checks were performed by an independent human.

## Evidence for each released candidate

Record the exact source commit, version, filenames, archive/member SHA-256 hashes, build/test results, installer/live checks, GPL source-access record and fresh scan report. Keep the matching debug symbols beyond temporary CI retention. All peers must use the same released package; recheck affected scenarios after any candidate change.

For each live scenario record date, controls, tester/harness, store/build, mods, player count, network topology, result and both logs. Mark absent evidence **unverified**. Beta publication invites testing of that limited candidate; it does not close broader reliability work or establish legal clearance.
