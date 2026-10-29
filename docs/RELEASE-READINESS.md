# Release readiness

kotor-coop is an **experimental beta**, developed and released from this one repository.
This page says what a release must pass before the tag is pushed, and what stays
unverified and is disclosed instead.

## Before pushing a release tag

1. `VERSION` is bumped and `docs/release-notes/public-playtest-<VERSION>.md` is written.
   The notes stand alone for a reader who has never seen the repo.
2. `dotnet build KotorCoop.slnx -warnaserror` and `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError`
   are clean (the solution build does not compile the plugin), and
   `dotnet test tests/KotorCoop.Tests --filter "Category!=Soak"` passes.
3. The Release workflow's packaged-installer check passes (`tools/verify-package.ps1`).
   It runs automatically on the tag; rehearse it first with **Actions > Release > Run workflow**
   on a branch, which builds everything and keeps the packages for three days instead of publishing.
4. Run the affected scenarios from [live-verification.md](live-verification.md) on a two-instance
   rig with the exact packaged bytes, and record what you ran. Mark anything not run **unverified**.
5. `git tag v<VERSION> && git push origin v<VERSION>`. The workflow publishes the setup program,
   Windows zip, Linux tarball and `SHA256SUMS.txt` to a GitHub release (a prerelease while the
   version has a `-beta` suffix). Then upload to Nexus Mods by following [nexus/README.md](../nexus/README.md).

Releases are built from the tag by GitHub Actions, never from a developer machine. The files are
not code-signed. The tagged source archive that GitHub attaches to the release is the GPL
corresponding source ([CORRESPONDING-SOURCE.md](CORRESPONDING-SOURCE.md)).

## Known open items for the current beta

- **Container exclusivity.** A same-moment open of one container by two players was reproduced
  in the 24 October smoke despite arbitration; a fix is in the tree and needs ordinary in-game retesting.
- Recent fixes for dialogue starting over the host's equipment menu, Trask disappearing on the
  joiner's screen, save-name input, dialogue presentation and the post-save enemy-sighted banner
  still need ordinary in-game confirmation.
- Legal: no publisher permission is claimed for a fan mod of this kind. See [LEGAL.md](LEGAL.md).

## What the bounded current smoke establishes

The [24 October smoke record](research/public-beta-smoke-2026-10-24.md) identifies exact 59e6e2b package/install hashes and disposable GOG copies. It used two instances on one PC, loopback networking and scoped injected developer/native controls on a non-input desktop, preserving original fixture saves. It is not independent-PC installation or an ordinary human playthrough.

Passed on those bytes: join/adoption into the host world while preserving the joiner's backpack, explicit reconnect, named save creation/loading, automatic readoption after host reload and a real container-item removal reaching both repositories. Both games stayed alive through cleanup. These remain useful bounded results; repeat affected scenarios after integration.

The same smoke reproduced both players opening one nonempty container simultaneously despite enabled arbitration. This demonstrates exclusivity failure, not a demonstrated duplicate-item take. The stale-release cause is an inference pending fix/retest evidence. Do not publish the old candidate as though this check passed.

## Scope to disclose rather than claim verified

The beta is offered free and experimental, with the following limits. Unavailable independent PCs/accounts and an unfinished full campaign are not tests to claim completed or automatic prohibitions on a clearly scoped beta. They limit support and any general-reliability recommendation.

- Start with two players on Windows, lawful KOTOR 1 v1.0.3 installations, matching mod versions and backed-up saves. Controlled evidence is strongest for same-PC GOG pairs. Describe Steam/mixed-store results by their dated tested configuration, without generalizing to all installations.
- Independent-PC/non-admin installation, two-account Steam friend delivery, genuinely external networking, firewall/forwarding/tunnel combinations and clean-machine upgrade/removal remain unverified where no exact dated result exists. Direct joining is the baseline; Valve's optional DLL is not shipped.
- A natural two-player campaign through an ending, ordinary Trask/recruitment, Jedi training, Leviathan equipment return, Manaan/sea-floor, temple and final-duel paths are not certified by warps or staged captures. Menu/subtitle/skipping/pause behavior still needs wider human use.
- Multi-hour ordinary sessions and three-to-eight-player campaign reliability remain experimental. The original boss has bounded rendering, arena-entry, loss/return and yield/return evidence; full animation, normal combat balance and ordinary victory are not established by it.
- Adoption/reconnect successes do not promise lossless crash recovery. Unsaved progress can be lost. Equipment/stacks/charges, Pazaak, interrupted transfers, helper/game faults and party wipes need broader ordinary use.
- Linux installer checks establish packaging behavior, not Linux gameplay support. The release pipeline packages the Windows setup program and zip, and a Linux tarball for the Proton/Wine installer.

Use [PLAYTEST.md](PLAYTEST.md) for ordinary controls, [live-verification.md](live-verification.md) for controlled rig checks and [limitations.md](limitations.md) for player-facing limits. Do not imply automated/injected checks were performed by an independent human.

## Dated evidence retained; superseded promotion rules

The [3 October verification record](release-verification-2026-10-03.md) and [beta.1 notes](release-notes/v0.1.0-beta.1.md) describe earlier private candidates. Their authorization to replace old private releases, private-visibility wording and tag/CI-artifact promotion process are historical. They do not authorize a history mirror or certify the new public package. [PUBLICATION.md](PUBLICATION.md) now governs a clean source commit, fresh build/scan and reviewed public draft. Old releases/tags/Actions were removed, not promoted.

Earlier preparation provides reusable controls: setup preserves configuration/other loaders, refuses running-game changes and supports backed-up migration; the support helper creates a local archive with configuration secrets redacted; packages carry full notices/provenance guards. Logs can still contain personal data and need review before sharing. Installer fixtures do not prove all clean-machine paths.

The 8 October rig result in [status-engineering.md](status-engineering.md) covered named save, return to play and enemy-sighted behavior with injected input. The 9 October crash/recovery matrix there exercised game/helper kills, interruption mid-adoption and party wipe; recovery succeeded in that setup, with possible joiner progress loss of approximately three minutes and host loss since its last save. These are dated observations, not current guarantees or independent-PC human repetitions.

The 24 October original-asset, Steam parser/startup and package checks, notice audit, 50 old-package installer checks and old Defender result remain evidence for their recorded inputs. Later menu/PDB/container changes supersede them for final-package certification. Music attribution and staged trailer provenance are separate from game-footage permission or campaign reliability.

## Evidence for each released candidate

Record the exact source commit, version, filenames, archive/member SHA-256 hashes, build/test results, installer/live checks, GPL source-access record and fresh scan report. Keep matching private symbols beyond temporary CI retention. All peers must use the same released package; recheck affected scenarios after any candidate change.

For each live scenario record date, controls, tester/harness, store/build, mods, player count, network topology, result and both logs. Mark absent evidence **unverified**. Beta publication invites testing of that limited candidate; it does not close broader reliability work or establish legal clearance.
