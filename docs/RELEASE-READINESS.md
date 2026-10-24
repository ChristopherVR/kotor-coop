# Beta readiness and remaining checks

## Current status — 24 October 2026

**No player binary is published in the new public repository. The tested 59e6e2b package is not the final release candidate.** Its container exclusivity failure and the developer PDB-path fix require a fresh build, package and scan. The separate, still-uncommitted menu/loader resizing work is excluded from this source freeze; it can be integrated in a later reviewed snapshot. Older successful scenarios and clean scans do not certify new bytes.

An explicit schema-2 allowlist prepares a clean current source/build/test snapshot for ChristopherVR/kotor-coop, preserving independent public history and keeping private development history at ChristopherVR/kotor-coop-dev. Do not mirror private refs. Existing public contents are player information, Pages and reviewed gameplay media; source publication still needs final committed inputs and integrated validation.

## Checks before the limited experimental beta

| Status | Check | Required result |
| --- | --- | --- |
| Open; fix/retest in progress | Container exclusivity | Repeat the simultaneous-open setup against the integrated fix and show only one player can hold the same nonempty container. Verify normal close/reopen, removal propagation and reconnect behavior. Ordinary removal propagation alone is not a race fix. |
| Open | Final integrated candidate | Review/commit the PDB-path, container, source-export and license changes; select version and public notes. Exclude other in-progress menu/loader edits. Build with warnings as errors, run appropriate tests/publication guards and retain matching private symbols. Freeze source before final byte-specific checks. |
| Open | Fresh exact package verification | Rebuild the Windows ZIP from that clean commit, validate exact inventory/current notices and installer behavior, verify the plugin CodeView PDB basename, then scan ZIP and extracted files with fresh Defender definitions. Record hashes, engine, definitions and time. The old package is refused by the new PDB-path guard. |
| Open | Affected live behavior | Repeat bounded adoption, reconnect, named save/load and container checks with the integrated package. New menu/loader behavior is outside this freeze and must not be claimed tested in this release. Identify controls/topology actually used. Developer-mailbox actions are not human menu tests. |
| Open | GPL source and license delivery | Apply GPL-3.0-only and the approved narrow KOTOR 1 permission consistently in contribution/legal/public text. Provide immutable exact Corresponding Source access beside the binary, including required scripts and actual embedded dependency sources. A moving branch or notices alone is insufficient. |
| Open | Clean current-source export | Validate the final immutable explicit export, exclusions and manifest; build/test its checkout without private research/media. No wildcard tree/history copying. Include all newly approved source files and the chosen notices. |
| Residual legal risk, documented | Rights/terms decision | Original-material authorization and the narrow mod-license linking permission are recorded. Retain actual Steam/GOG/game-footage uncertainty. The owner's linking approval does not grant publisher consent or establish worldwide statutory compliance. |
| Open | Public presentation | Standalone notes/limitations must match the candidate and scope. Check signed-out guides, credits, source/download links, checksums and scan report. Create the reviewed draft through PUBLICATION.md's current workflow. |

The [distribution review](research/distribution-review-2026-10-24.md) records the source-export implementation, old package/notice results, private-material exclusions and the developer PDB-path defect. Passing exporter unit tests alone does not prove the final immutable source inventory is complete. The GPL transition adds Corresponding Source work beyond the earlier notice-only audit.

## What the bounded current smoke establishes

The [24 October smoke record](research/public-beta-smoke-2026-10-24.md) identifies exact 59e6e2b package/install hashes and disposable GOG copies. It used two instances on one PC, loopback networking and scoped injected developer/native controls on a non-input desktop, preserving original fixture saves. It is not independent-PC installation or an ordinary human playthrough.

Passed on those bytes: join/adoption into the host world while preserving the joiner's backpack, explicit reconnect, named save creation/loading, automatic readoption after host reload and a real container-item removal reaching both repositories. Both games stayed alive through cleanup. These remain useful bounded results; repeat affected scenarios after integration.

The same smoke reproduced both players opening one nonempty container simultaneously despite enabled arbitration. This demonstrates exclusivity failure, not a demonstrated duplicate-item take. The stale-release cause is an inference pending fix/retest evidence. Do not publish the old candidate as though this check passed.

## Scope to disclose rather than claim verified

The first public build can be offered as a free experimental beta once the concrete checks above pass, with the following limits. Unavailable independent PCs/accounts and an unfinished full campaign are not tests to claim completed or automatic prohibitions on a clearly scoped beta. They limit support and any general-reliability recommendation.

- Start with two players on Windows, lawful KOTOR 1 v1.0.3 installations, matching mod versions and backed-up saves. Controlled evidence is strongest for same-PC GOG pairs. Describe Steam/mixed-store results by their dated tested configuration, without generalizing to all installations.
- Independent-PC/non-admin installation, two-account Steam friend delivery, genuinely external networking, firewall/forwarding/tunnel combinations and clean-machine upgrade/removal remain unverified where no exact dated result exists. Direct joining is the baseline; Valve's optional DLL is not shipped.
- A natural two-player campaign through an ending, ordinary Trask/recruitment, Jedi training, Leviathan equipment return, Manaan/sea-floor, temple and final-duel paths are not certified by warps or staged captures. Menu/subtitle/skipping/pause behavior still needs wider human use.
- Multi-hour ordinary sessions and three-to-eight-player campaign reliability remain experimental. The original boss has bounded rendering, arena-entry, loss/return and yield/return evidence; full animation, normal combat balance and ordinary victory are not established by it.
- Adoption/reconnect successes do not promise lossless crash recovery. Unsaved progress can be lost. Equipment/stacks/charges, Pazaak, interrupted transfers, helper/game faults and party wipes need broader ordinary use.
- Linux installer checks establish packaging behavior, not Linux gameplay support. The initial public binary pipeline is for the Windows ZIP.

Use [PLAYTEST.md](PLAYTEST.md) for ordinary controls, [live-verification.md](live-verification.md) for controlled rig checks and [limitations.md](limitations.md) for player-facing limits. Do not imply automated/injected checks were performed by an independent human.

## Dated evidence retained; superseded promotion rules

The [3 October verification record](release-verification-2026-10-03.md) and [beta.1 notes](release-notes/v0.1.0-beta.1.md) describe earlier private candidates. Their authorization to replace old private releases, private-visibility wording and tag/CI-artifact promotion process are historical. They do not authorize a history mirror or certify the new public package. [PUBLICATION.md](PUBLICATION.md) now governs a clean source commit, fresh build/scan and reviewed public draft. Old releases/tags/Actions were removed, not promoted.

Earlier preparation provides reusable controls: setup preserves configuration/other loaders, refuses running-game changes and supports backed-up migration; the support helper creates a local archive with configuration secrets redacted; packages carry full notices/provenance guards. Logs can still contain personal data and need review before sharing. Installer fixtures do not prove all clean-machine paths.

The 8 October rig result in [status-engineering.md](status-engineering.md) covered named save, return to play and enemy-sighted behavior with injected input. The 9 October crash/recovery matrix there exercised game/helper kills, interruption mid-adoption and party wipe; recovery succeeded in that setup, with possible joiner progress loss of approximately three minutes and host loss since its last save. These are dated observations, not current guarantees or independent-PC human repetitions.

The 24 October original-asset, Steam parser/startup and package checks, notice audit, 50 old-package installer checks and old Defender result remain evidence for their recorded inputs. Later menu/PDB/container changes supersede them for final-package certification. Music attribution and staged trailer provenance are separate from game-footage permission or campaign reliability.

## Evidence for each released candidate

Record the exact source commit, version, filenames, archive/member SHA-256 hashes, build/test results, installer/live checks, GPL source-access record and fresh scan report. Keep matching private symbols beyond temporary CI retention. All peers must use the same released package; recheck affected scenarios after any candidate change.

For each live scenario record date, controls, tester/harness, store/build, mods, player count, network topology, result and both logs. Mark absent evidence **unverified**. Beta publication invites testing of that limited candidate; it does not close broader reliability work or establish legal clearance.
