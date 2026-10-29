# Nexus Mods listing

Everything needed to publish this mod on Nexus Mods, kept in the repo so the listing and
the code do not drift.

| File | Use |
| --- | --- |
| `description.bbcode` | Paste into the mod page's **Description** box (Nexus uses BBCode). |
| `changelog.txt` | The per-version change text for each uploaded file. |
| `../site/media/showcase/*.gif` | Animated exploration, dialogue and combat clips shared with the GitHub README. |

The description embeds GIFs from the public repository. Push the media before pasting the
BBCode into Nexus. The clips combine silent trailer excerpts with five new v0.1.0-beta.4
background-harness captures: hosting, character creation, inventories, chat and reconnecting.
The new clips include pauseable MP4 links. Sources, cuts and output hashes are recorded in
`../site/media/showcase/manifest.json`, with session details in `capture-20261030.json` beside it.
They show staged gameplay on two local game copies; their captions retain those limits. The release workflow updates
Nexus file versions, but the mod page description still needs to be pasted into Nexus itself.

## Page checklist

| Field | Value |
| --- | --- |
| Name | KOTOR Co-op |
| Summary (short description) | Seamless drop-in, drop-out co-op for KOTOR 1: two to eight players, each with their own character, in one shared world. Experimental, open source. |
| Description | Paste `description.bbcode` |
| Version | the release version, e.g. `0.1.0-beta.3` |
| Category | Miscellaneous (or Gameplay / Multiplayer if offered) |
| Primary image | `images/1-cover-title.jpg` |
| Gallery | the other three images in `images/` |
| Source / donation links | `https://github.com/ChristopherVR/kotor-coop` |
| Adult content | No |

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
- **Images:** the gallery is in `images/`, taken from the public trailer (two real game copies side by
  side): `1-cover-title.jpg` is the **primary image**, then the three gameplay shots in order.
  Nexus wants videos as a YouTube or Vimeo link, so upload `site/media/trailer.mp4` there first
  (the credit text for the music is in `site/media/music-credits.txt`; paste it into the video description).

## One-time setup for the automated upload

The `nexus` job in `.github/workflows/release.yml` runs after every GitHub release and uses the
[Nexus Mods upload action](https://github.com/Nexus-Mods/upload-action):

1. On the mod page, upload a first file by hand (any zip) so a **file group** exists.
2. Put the numeric **file id** of that group in the repository **variable** `NEXUS_FILE_ID`
   (Settings > Secrets and variables > Actions > Variables).
3. Put your Nexus API key in the repository **secret** `NEXUSMODS_API_KEY`.

Every successful push CI on `main` triggers an incremented release. Manual Release runs on
`main` also publish unless **dry_run** is checked. The upload uses the version from the release
job, archives the previous file version and sets `update_mod_version: true` so the mod page
shows the new version too. The Nexus zip is downloaded from the GitHub release assets.

Without both settings, the Nexus job fails visibly; the GitHub release remains available.
Fix configuration and use **Re-run failed jobs** to retry the Nexus upload. The description
on Nexus is the release notes as plain text. The page description itself
(`description.bbcode`) is pasted by hand and is not touched by the workflow.
