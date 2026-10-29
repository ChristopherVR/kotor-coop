# Nexus Mods listing

Everything needed to publish this mod on Nexus Mods, kept in the repo so the listing and
the code do not drift.

| File | Use |
| --- | --- |
| `description.bbcode` | Paste into the mod page's **Description** box (Nexus uses BBCode). |
| `changelog.txt` | The per-version change text for each uploaded file. |

## What to upload

Every GitHub release carries the same files; upload these to Nexus from it:

| Nexus file slot | File | Notes |
| --- | --- | --- |
| Main file | `kotor-coop-<version>-setup.exe` | One-click installer. If Nexus rejects an `.exe`, make the zip the Main file instead. |
| Optional file | `kotor-coop-<version>-windows.zip` | Same content; extract to its own folder and run `KotorCoop.Agent.exe`. |

Both are a *game-directory image*, not a Vortex-style "drop into Override" mod, so the page
says **use the installer / manual install** and does not rely on Vortex deploying it.

Suggested page fields:

- **Requirements:** Star Wars: Knights of the Old Republic (GOG or Steam), v1.0.3, 64-bit Windows.
- **Permissions:** open source under GPL-3.0-only (see `LICENSE` and `KOTOR-LINKING-EXCEPTION.txt`).
- **Tags:** multiplayer, co-op, experimental.
- **Images:** `site/media/trailer-poster.jpg`; the trailer video is `site/media/trailer.mp4`.

## Automated upload (optional)

`.github/workflows/nexus.yml` can push a release's zip to Nexus with the
[Nexus Mods upload action](https://github.com/Nexus-Mods/upload-action). One-time setup:

1. Create the mod page by hand and upload the first file manually.
2. Add repository secrets `NEXUSMODS_API_KEY` and `NEXUSMODS_FILE_ID` (the file group id
   shown in the mod page's file manager).
3. Run the workflow from the Actions tab with the release tag.

The action is maintained by Nexus and its inputs may change: read its README and compare it
with the workflow before the first run. The manual route above always works.
