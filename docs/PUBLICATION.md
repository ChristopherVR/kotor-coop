# Private development and public distribution

As of 2026-10-24, `ChristopherVR/kotor-coop-dev` is the private development
repository and `ChristopherVR/kotor-coop` is a separate public information and
download repository. The local development folder may still be named `kotor-coop`.
Check `git remote -v` before pushing. Never mirror private refs to the public remote.

## Boundary

The public repository has separate Git history. Approved player information and the
website are exported by `tools/Prepare-PublicRepository.ps1` using the exact mappings in
`tools/publication/public-export.json`. Canonical public writing lives in `site/public/`
and `tools/publication/public/`; the original project license is also exported.
No wildcard copying is permitted. Private research, development source, historical
recordings, databases, build symbols and support bundles remain private.

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

1. Finish the legal and gameplay gates in `LEGAL.md` and `RELEASE-READINESS.md`.
   The original boss redesign still needs live verification; optional Steam invite
   success needs a licensed library and two-account test. Repository separation
   does not resolve underlying game/store rights or the custom license review.
2. Update `VERSION` and write reviewed notes in `docs/release-notes/v<VERSION>.md`.
   Notes must stand alone for public readers and must not link private evidence.
3. Commit the reviewed source, build with warnings as errors, run non-soak tests
   and the publication policy tests. No source changes may be pending at publication.
4. Run `pwsh tools/Publish-PublicRelease.ps1 -Directory artifacts/public-candidate-<unique>
   -Version <VERSION> -NotesFile docs/release-notes/v<VERSION>.md -ReleaseReviewed`.
   Use a single PowerShell command line. This rebuilds the package and scans it
   again; it does not trust pre-existing editable scan reports.
5. Inspect the resulting draft in the public repository. Publish through GitHub
   when approved, or use the script's explicit `-Publish` option for an already
   reviewed release. Existing releases are never overwritten.

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

## Local verification — 24 October 2026

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
public repository currently has one root commit and no player releases.
