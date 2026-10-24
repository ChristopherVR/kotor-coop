# KOTOR Co-op 0.1.0-beta.3 — experimental public playtest candidate

These are prepared release notes, not an announcement that a download is available.
The first public build is intended for volunteer testing by two players on Windows.
It is not a stable release or a verified complete co-op campaign.

Each player needs a lawful KOTOR 1 v1.0.3 installation and the same mod package.
Back up saves first. Use a separate test save rather than your only campaign copy.

## Included

- Direct hosting and joining, including automatic character/save entry where supported.
- Shared travel, combat and conversations; the player who begins a conversation chooses replies.
- The original Cairn Sentinel boss design and synthesized nonverbal effects.
- Corrected Steam startup-join handling and validation of invite destinations.
- Complete packaged third-party notices and a checked package inventory.
- GPLv3 source publication, build instructions and contribution workflow.
- Container arbitration fixes for stale close messages and grant-response ordering;
  final in-game retesting is in progress before the binary release.

## Evidence for the current candidate

The Windows candidate built from source commit
`59e6e2b687a35819f95f172de027bb057ee0feb0` passed a warnings-as-errors build,
234 focused Steam/join/boss tests and 50 packaged-installer checks. Defender scanned
both the ZIP and extracted files with no detections on 24 October 2026. These results
apply to that exact candidate, not subsequent menu/loader changes. Publication must
rebuild and rescan the final selected source before supplying the release files.

Controlled two-instance testing on one PC demonstrated direct joining, automatic
world adoption, entering an existing conversation, shared enemy damage/death, and
boss entry/return. Trailer footage used separately identified development binaries
and staged scenes. It is not proof that the scanned ZIP has passed a complete campaign.

## Known limitations

- Two independent PCs, external-network joining and ordinary non-admin installation
  have not been verified for this candidate. Port forwarding or a tunnel may be needed.
- Steam game support and Steam friend invitations are separate. The optional Steam
  invite library is not included, and native two-account friend-invite delivery is
  unverified. Use direct joining for this playtest.
- Direct command-line token launches of the tested retail Steam executable failed
  in a controlled local test, including with the mod removed. Joining through the
  helper worked. This does not certify the Steam client's own launch/invite path.
- A full natural campaign, multi-hour play, wider player counts and ordinary boss
  balance/victory remain unverified. Start with two players and matching game mods.
- Save and inventory recovery have limits. Crashes can lose unsaved progress; do not
  rely on the mod to preserve your only copy of a save.
- One liveness warning occurred following a replicated enemy death in the staged
  combat test. No resurrection was observed; the warning's cause remains unconfirmed.
- The concurrent widescreen/menu-loader changes are excluded from this candidate.
  Their integration and regression checks must finish before a build containing
  those changes is described as tested. The ongoing compatibility work includes a
  4K startup crash and controls disappearing after resolution changes with the
  tested enhanced-menu configuration; those scenarios are not certified supported.

## Installing and reporting problems

Extract the Windows ZIP to its own folder, close KOTOR and its helper, then run
`KotorCoop.Agent.exe` and follow setup. Keep antivirus enabled. Compare the archive
against `SHA256SUMS.txt` and read the dated `scan-report.json` for those exact bytes.
A clean scan is not a guarantee of safety, and the executables are not code-signed.

Report the version, game store/build, player count and reproduction steps at
https://github.com/ChristopherVR/kotor-coop/issues. Review logs before sharing and
remove passwords, addresses and personal paths. Do not upload game files or saves.

## Publication status

These notes do not grant game or media rights or establish legal clearance. The
owner confirmed authority over the original project and selected GPLv3. Game/store
terms and footage rights remain separate; no affirmative publisher permission is
claimed. The release will provide exact corresponding source alongside its binary.
The owner approved the narrow KOTOR linking permission; it accompanies GPLv3 and
does not authorize distribution of the game or change third-party terms.
These preparation notes and a clean antivirus scan do not establish legal clearance.
