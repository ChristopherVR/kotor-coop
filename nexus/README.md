# Nexus Mods listing

Everything needed to publish this mod on Nexus Mods, kept in the repo so the listing and
the code do not drift.

| File | Use |
| --- | --- |
| `description.bbcode` | Paste into the mod page's **Description** box (Nexus uses BBCode). |
| `changelog.txt` | The per-version change text for each uploaded file. |

## What gets uploaded

Mod page: <https://www.nexusmods.com/mod/1910> (mod id 1910).

The Release workflow uploads one **Main** file per version: `kotor-coop-<version>-nexus.zip`,
which holds the one-click `kotor-coop-<version>-setup.exe` and a `READ-ME-FIRST.txt`. (Nexus
discourages a bare `.exe` as the main file, so it is zipped.) The previous version is archived
automatically. The GitHub release carries the same setup program plus the plain Windows zip and
the Linux tarball.

The program is a *game-directory installer*, not a Vortex-style "drop into Override" mod, so the
page says **run the installer** and does not rely on Vortex deploying it.

Suggested page fields:

- **Requirements:** Star Wars: Knights of the Old Republic (GOG or Steam), v1.0.3, 64-bit Windows.
- **Permissions:** open source under GPL-3.0-only (see `LICENSE` and `KOTOR-LINKING-EXCEPTION.txt`).
- **Tags:** multiplayer, co-op, experimental.
- **Images:** `site/media/trailer-poster.jpg`; the trailer video is `site/media/trailer.mp4`.

## One-time setup for the automated upload

The `nexus` job in `.github/workflows/release.yml` runs after every tagged release and uses the
[Nexus Mods upload action](https://github.com/Nexus-Mods/upload-action):

1. On the mod page, upload a first file by hand (any zip) so a **file group** exists.
2. Put the numeric **file id** of that group in the repository **variable** `NEXUS_FILE_ID`
   (Settings > Secrets and variables > Actions > Variables).
3. Put your Nexus API key in the repository **secret** `NEXUSMODS_API_KEY`.

Without both, the job logs a warning and is skipped; the GitHub release is unaffected. The
description on Nexus is the release notes as plain text. The page description itself
(`description.bbcode`) is pasted by hand and is not touched by the workflow.
