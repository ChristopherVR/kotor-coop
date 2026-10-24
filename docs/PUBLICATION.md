# Private development and public distribution

As of 2026-10-24, `ChristopherVR/kotor-coop-dev` is the private development
repository and `ChristopherVR/kotor-coop` is the separate public source,
contribution and download repository. The local development folder may still be named `kotor-coop`.
Check `git remote -v` before pushing. Never mirror private refs to the public remote.

## Boundary

The public repository has separate Git history. Reviewed current source, tests,
build inputs, contributor/player information and the website are exported by
`tools/Prepare-PublicRepository.ps1` using the exact mappings in
`tools/publication/public-export.json`. Canonical public writing lives in `site/public/`
and `tools/publication/public/`; the original project license is also exported.
Schema 2 explicitly enables the open-source export mode. No wildcard copying is
permitted. Private research, development history, historical recordings, optional
address databases, build symbols and support bundles remain private. The owner
selected GNU GPL version 3 only for original project code and documentation.
The former custom source-available license is replaced for this snapshot; this
does not revoke earlier grants or relicense third-party material.

The rejected illustrative overview is not part of the approved export. Its five former
public media paths are the only legacy paths the first managed export may explicitly
remove after adoption of a clean public checkout. New gameplay media must be reviewed,
committed and explicitly added to the mapping before it can be exported.

The exporter supports an existing public checkout, reads immutable Git blobs from one
commit, and records the source commit plus each exported file's SHA-256 in
`.publication-manifest.json`. It never copies `.git`, commits, changes remotes or pushes.
See [repeatable public documentation sync](PUBLIC-DOCS-SYNC.md) for preview, adoption,
apply, drift checking and the separate review/commit/push steps.

At the owner's request, two old private releases, six remote/local tags, 632
historical Actions runs and 43 artifacts were inventoried and removed. All 683
GitHub deletion requests succeeded. Local inventory and
deletion results are in ignored `artifacts/publication-backup/`. Metadata inventory
does not back up downloadable binaries or Actions log content. Future private CI
will create new runs and artifacts; these are not public distribution.

## Player build publication

1. Finish the concrete release blockers in `LEGAL.md` and `RELEASE-READINESS.md`.
   The original boss redesign has bounded two-instance live evidence; ordinary
   balance/victory and complete animations remain unverified. Optional Steam friend
   delivery still needs a licensed library and two-account test and is not a
   verified first-beta feature. Disclose unavailable independent-PC/campaign tests.
   Repository separation and an open-source license do not resolve game/store
   rights. Record the owner's decision on the identified residual terms/IP risk.
2. Update `VERSION` and write reviewed notes in `docs/release-notes/public-playtest-<VERSION>.md`.
   Notes must stand alone for public readers and must not link private evidence.
3. Commit the reviewed source, build with warnings as errors, run non-soak tests
   and the publication policy tests. Export and build the public source snapshot
   to verify it contains the needed build inputs. No selected source changes may
   be pending at publication. Rebuild after the container-arbitration and PDB-path
   fixes; earlier candidate scans do not cover them. The still-uncommitted menu
   resizing work is excluded until separately reviewed and committed.
4. Run `pwsh tools/Publish-PublicRelease.ps1 -Directory artifacts/public-candidate-<unique>
   -Version <VERSION> -NotesFile docs/release-notes/public-playtest-<VERSION>.md -ReleaseReviewed`.
   Use a single PowerShell command line. This rebuilds the package and scans it
   again; it does not trust pre-existing editable scan reports.
5. Inspect the resulting draft in the public repository. Publish through GitHub
   when approved, or use the script's explicit `-Publish` option for an already
   reviewed release. Existing releases are never overwritten.

The GPL release guard downloads the public source archive at an immutable commit,
requires its export provenance to identify the same private source commit used
for the build, and checks every mapped file. It validates the actual embedded
dependency inventory against the corresponding-source manifest and verifies
upstream source availability. The release includes the exact public source
archive and source instructions beside the binary. `SHA256SUMS.txt` covers these
assets too; the antivirus report's scope remains the player ZIP and extracted
files. A source archive is not described as antivirus-scanned by that report.

The publication boundary requires an exact Windows ZIP inventory, current asset
and notice bytes, and exact local build outputs. Symbols and Linux packages are
not public assets in this first pipeline. Defender scans both ZIP and extracted
files using fresh definitions and `-DisableRemediation`, which ignores file
exclusions and avoids treating a remediated detection as a clean package. Errors
or detections stop publication. The report records engine/signature versions,
time, archive SHA-256 and every packaged file hash. No scanner guarantees safety.
This process does not sign executables or eliminate SmartScreen reputation prompts.

For local scan-only validation use `tools/Scan-PublicPackage.ps1 -Archive <zip>
-Version <VERSION> -OutputDirectory <new-directory>`. That does not upload files.
Raw Defender diagnostics stay in a uniquely named temporary directory locally.
No VirusTotal upload is performed: ordinary submissions can share binaries with
third parties. Public scan reports contain no machine paths or private CI URLs.

## GitHub automation

Private CI continues building/testing. The old Release workflow is replaced with
manual **Public release candidate**, and the old Pages deployment is disabled so
legacy game media cannot be published accidentally. The release workflow creates
drafts only, runs tests, then builds/scans the exact uploaded package.

To use that workflow, configure the private repository's `public-release`
environment with a narrowly scoped `PUBLIC_RELEASE_TOKEN` granting Contents write
only to the public `ChristopherVR/kotor-coop` repository. Configure required
reviewers where supported. No broad local OAuth credential is copied into Actions.
Until the secret is configured, the workflow fails closed; local publication can
use the owner's existing authenticated GitHub CLI without creating another token.

Sources: [Defender custom-scan behavior](https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus),
[GitHub visibility consequences](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility),
[VirusTotal sharing](https://docs.virustotal.com/docs/how-it-works).

## Historical local verification — 24 October 2026

The records below describe earlier snapshots, not approval of the latest tree.
The current trailer is the 57-second gameplay edit with licensed music. The
overview mentioned below was rejected and removed from the public current tree.
The `59e6e2b` live smoke later found a simultaneous-container-open race; its passing
A basename-only native PDB locator, current source export and concurrent menu
changes also require a fresh final build and scan.

The original overview renders and decodes successfully at 1280×720, 24 fps, 64 seconds.
The redesigned boss's seven generated assets reproduce byte-for-byte. Focused checks
passed (368 boss/PvP/console cases, 125 Steam/digest cases, 14 publication-policy cases).
The solution built with warnings as errors and no warnings. A fresh NativeAOT/package
build passed after the runtime notice audit was refreshed to the actual .NET 10.0.13
packages; their license/notice bodies match the previously collected 10.0.12 texts.

The local Windows candidate passed 50 packaged-installer checks. Defender scanned
both the ZIP and every extracted file with no detections, engine 1.1.26090.9,
definitions 1.461.175.0, at 2026-10-24T03:16:00.5675250Z. Candidate SHA-256:
`bc2ada00de4ca2f78e4ae3d7b60c9b36b91dccf323a43e2d2675e214ba9b595f`.
The report is in ignored `artifacts/publication-scan/scan-report.json`; this candidate
has not been uploaded as a public player release. It is not signed or legally cleared.

The full local non-soak suite completed with **6,307 passed, zero failed, zero
skipped**, in 15 minutes 1 second (`publication-integration.trx`). This run used
the integrated boss/Steam build from before unrelated concurrent menu-layout
work; that separate work is not covered by this result. The later installer
message-only change was compiled in the packaged build and exercised by the
50 installer checks. The NuGet audit with transitive dependencies reported no
known vulnerable packages from its configured sources.

Private GitHub CI run `40057914816` could not start: GitHub's annotation says an
Actions budget is preventing further use. No workflow steps ran. Billing was not
changed; local results above are the validation evidence. The manual public-release
workflow also needs its target-scoped environment token before remote use. The
public repository now serves the curated Pages site and player guides, with no
player releases. The rejected overview is absent from its current tree. The local
exporter has 29 passing policy tests and records exact source/file provenance.
The newer Steam startup fixes have 157 focused passing tests; the previous package
hash/scan above does not cover these subsequent source changes.


## Fresh candidate verification — 24 October 2026, 03:59 UTC

A separate clean checkout of `59e6e2b687a35819f95f172de027bb057ee0feb0`
produced a new `0.1.0-beta.3` candidate, including the live-discovered Steam startup
guard fix, invalid invite-destination rejection and the shorter Sentinel banner.
The clean snapshot excludes concurrently edited widescreen/menu integration files.
NativeAOT and packaging completed with warnings treated as errors. The focused
Steam/join/boss/PvP test selection passed **234 tests, zero failures/skips**; the
exact extracted Windows candidate then passed **50 installer checks**.

Defender scanned the ZIP and extracted files with no detections at
`2026-10-24T03:59:33.6821627Z`, engine `1.1.26090.9`, signatures `1.461.176.0`.
The ZIP SHA-256 is
`ceae53f87fda2e581204e1bc89c7380b3cbe347ceb48710212e9dd3f3e92ea25`.
Evidence stays in ignored `artifacts/publication-scan-verified-20261024/`,
`artifacts/publication-installer-check-verified-20261024/` and
`artifacts/publication-verified-tests/public-candidate-targeted.trx`. This is a
local candidate, not a published player release or a claim of legal clearance.
The initial GOG boss/trailer captures used the preceding live build; their exact
plugin/agent hashes are recorded separately and must not be confused with this ZIP.
