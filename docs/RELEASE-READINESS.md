# Beta readiness and remaining gates

## Publication split — 24 October 2026

Private development moved to `ChristopherVR/kotor-coop-dev`; the new public
`ChristopherVR/kotor-coop` contains curated information and an original illustrative
overview only. Old releases/tags/Actions records were removed at the owner's
request, not promoted. [PUBLICATION.md](PUBLICATION.md) supersedes the historical
tag-promotion process below: a reviewed clean checkout is rebuilt and scanned
before a draft is created in the public repository. There is no current public
player build. No legal or general-playability sign-off is implied.

New targeted checks cover the original Cairn Sentinel assets, Steam invite parsing
and privacy fixes, and the public-package allowlist. The replacement boss still
requires live rendering/audio/multiplayer checks; real two-account Steam invites
remain unverified. Existing gameplay/legal gates below remain open.

## Historical beta readiness

Updated 3 October 2026 for `v0.1.0-beta.1`. The owner authorized replacement of the
existing GitHub releases with this experimental beta. Repository visibility stays
private. This is not general-release or legal sign-off.

Start with two players on Windows, lawful KOTOR 1 v1.0.3 GOG/Steam installations,
matching game mods and backed-up saves. See the [beta notes](release-notes/v0.1.0-beta.1.md)
and [player limitations](limitations.md). Earlier checks remain in the
[dated verification record](release-verification-2026-10-03.md); they do not certify
every scenario on the beta.

## Preparation completed

- Setup checks files before offering launch, preserves custom settings and existing
  DLL loaders, refuses changes while the game is running, and offers opt-in migration
  of historical defaults with a settings backup.
- `KotorCoop.CollectLogs.cmd` collects a local support zip without a terminal or upload.
  Settings secrets are redacted; users should review logs before sharing.
- Hosts receive a re-host instruction when a replacement helper waits without an
  active session. Relaunching a helper does not guarantee session recovery.
- Packages carry the project license, legal guidance, full dependency notices and
  inventory. The cshtml notice was recovered from Microsoft notices with the exact
  upstream revision; assembly material was matched against its archived upstream;
  all 21 bundled themes have recorded notice-source mappings. Packaging verifies
  hashes and rejects unreviewed dependency-version changes.
- Publication requires manual promotion of a tag pointing at a successful CI commit,
  reusing CI's tested archives. Beta tags are prereleases; main builds no longer
  recreate a rolling release. Download links target the approved beta.
- Contribution forks and branches intended for PRs back to this project are allowed.

## Still required before recommending public or general use

| Priority | Gate | Evidence needed |
| --- | --- | --- |
| P0 | Game, media and license review | Complete [LEGAL.md](LEGAL.md): game/store terms, extracted media in current files and accessible history, promotional footage, original-code ownership and custom-license review. Collected dependency notices do not establish legal clearance. |
| P0 | Human confirmation of reported fixes | On the beta, use ordinary controls to save with a name, return to play, open containers, start dialogue while the host equipment menu is open, skip dialogue and read subtitles. Recruit/switch Trask and follow his door conversation; confirm the follower sees him. Check enemy-sighted and manual pause behavior on both peers. Keep both logs and saves. Rig evidence now exists for the named-save, return-to-play and enemy-sighted-after-save item (2026-10-08, injected input rather than a human; see [status-engineering.md](status-engineering.md)). Everything else in this row, and a human repeat of that item, stays open. |
| P0 | Independent-PC install and joining | Install the downloaded archive without developer tooling as a non-admin user on two Windows PCs. Cover GOG/GOG, Steam/Steam and mixed stores, LAN, passwords and a genuinely external connection. Verify firewall/forwarding guidance and any supported tunnel separately. Advertise only combinations actually checked. |
| P0 | Save, inventory and crash recovery | Test interrupted capture/adoption, game/helper crashes with unsaved progress, restart/rejoin, equipment, stacks, charges, Pazaak decks and party wipes. Earlier controlled checks recovered checkpoints but lost unsaved credits. Do not promise lossless crash recovery. Rig evidence 2026-10-09 (injected faults, see status-engineering.md "Crash and recovery matrix"): joiner/host game kills, helper kills, a kill mid-adoption and a party wipe all recovered automatically; a joiner loses at most ~3 minutes, the host loses everything since its last save. Human repeats on two PCs remain open. |
| P0 | Natural campaign coverage | Complete a two-player campaign through one ending without developer warps. Preserve recruitment, Jedi training, Leviathan equipment-return, Manaan/sea-floor, temple and final-duel checkpoints. Record the branch actually completed. |
| P1 | Clean-machine upgrade/removal | Verify plain/chained installations, backups, paths with spaces, another-drive Steam libraries, protected folders and running-game refusal. Double-click setup/support collection need visible checks beyond fixture tests. |
| P1 | Longer sessions and wider player counts | Run multi-hour ordinary gameplay with saves, combat, doors and reconnects. Record memory, crashes and dumps. Three-to-eight-player campaign reliability remains experimental. |
| P1 | Website and access review | Pages previously failed with Not Found because the site was not configured. Before enabling public hosting or changing repository visibility, review accessible history for private data/game media, then check downloads and installation while signed out. |

Linux installer checks establish packaging behavior, not Linux gameplay support.
Use [PLAYTEST.md](PLAYTEST.md) for retail-input coverage and
[live-verification.md](live-verification.md) for controlled rig checks.

## Candidate evidence and promotion

Record the exact commit, tag, embedded version, filenames and SHA-256 hashes,
successful CI run, installer results and retained symbols for each supported release.
Keep symbols beyond CI's 30-day retention. All peers must use the same package.

For every human scenario, record date, tester, store/build, mods, player count,
network topology, result and both support logs. Mark missing evidence **unverified**.
Beta publication is permission to test this limited candidate; it does not close the
remaining gates above. Recheck affected scenarios after changing the candidate.
