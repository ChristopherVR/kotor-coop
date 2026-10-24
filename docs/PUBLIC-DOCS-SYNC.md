# Repeatable public documentation and site sync

The private development repository is `ChristopherVR/kotor-coop-dev`; the public
information/download repository is `ChristopherVR/kotor-coop`. Local folders are
currently `D:\Development\kotor-coop` and `D:\Development\kotor-coop-public`.
"Development branch" means the reviewed branch in the private checkout. It is currently
`main`; this script does not invent a `dev` branch. Without `-SourceRef`, it resolves the
current branch to an exact commit. Pass `-SourceRef dev` if that branch is created, or
pass a reviewed tag/commit explicitly. Detached checkouts require `-SourceRef`.

## What is exported

`tools/publication/public-export.json` is the explicit allowlist, read from the chosen
source commit. Current approved sources are:

| Private source | Public destination |
| --- | --- |
| `site/public/project-README.md` | `README.md` |
| `tools/publication/public/SECURITY.md` | `SECURITY.md` |
| `tools/publication/public/gitignore` | `.gitignore` |
| `tools/publication/public/gitattributes` | `.gitattributes` (`* -text`, exact bytes) |
| `LICENSE` | `LICENSE` |
| `site/public/index.html`, `style.css`, `favicon.svg` | `docs/` with the same filenames |
| `site/public/install.html`, `play.html`, `limitations.html` | `docs/` with the same filenames |
| The seven explicitly listed files in `site/public/media/` | `docs/media/`: trailer, poster, transcript, provenance, music credits/license and font license |

GitHub Pages serves `main:/docs`. Source `site/public/README.md` is maintainer guidance
and is not exported. Private research/evidence is not public documentation. To publish
another player guide, write its curated standalone version in `site/public/`, remove
private links and personal information, then add an explicit mapping. Links between
exported files are checked; a guide cannot depend on an unexported private file.
The public `.gitattributes` is required to contain exactly `* -text` plus an LF newline.
This disables newline conversion for every exported file, including the provenance
inventory, so hashes survive Windows clones with `core.autocrlf=true`. Do not replace
it with `text=auto` or add per-file conversion rules.

The gameplay trailer is exported only through its seven explicit file mappings. The
raw gameplay takes and standalone music recording stay private. Preserve music
attribution, the applicable full license, font notices and exact final-media hashes
when replacing a trailer. The exporter rejects Git LFS pointer text rather than
publishing a pointer as a movie. It does not download remote content or infer any
new approval from filenames.

## Review, preview and apply

Commit reviewed changes to the private canonical inputs and manifest first. The exporter
refuses dirty selected files, a dirty policy, or uncommitted changes to its Python
implementation or PowerShell wrapper. Unrelated in-progress development changes
are not exported; all output bytes come from the resolved commit, never the working tree.

```powershell
# Preview the exact writes/deletions; no target files are changed.
pwsh tools/Prepare-PublicRepository.ps1 -Destination D:/Development/kotor-coop-public -SourceRef main -DryRun

# First managed sync only: adopt existing reviewed, tracked files in a clean public checkout.
pwsh tools/Prepare-PublicRepository.ps1 -Destination D:/Development/kotor-coop-public -SourceRef main -AdoptExisting -DryRun
pwsh tools/Prepare-PublicRepository.ps1 -Destination D:/Development/kotor-coop-public -SourceRef main -AdoptExisting

# Later exports use the recorded managed-file inventory.
pwsh tools/Prepare-PublicRepository.ps1 -Destination D:/Development/kotor-coop-public -SourceRef main
```

Use `-AdoptExisting` only for the first reviewed import. It permits replacement of exact
allowlisted existing files and removal of five explicitly named rejected overview files,
only when those files are tracked and the public checkout is clean. It is not a force
flag for changed managed files. Existing Git checkouts must have the expected public
`origin` and its own ordinary `.git` directory. Linked Git directories and gitfiles
(including worktrees) are refused; use a standalone public clone. No network request is made and the script does not change that remote.

After export, inspect and commit the public diff separately:

```powershell
git -C D:/Development/kotor-coop-public diff --stat
git -C D:/Development/kotor-coop-public diff
git -C D:/Development/kotor-coop-public status --short
# Stage only the reviewed exported files shown by status, plus .publication-manifest.json.
# Commit those public files, then push the public main branch when approved.
git -C D:/Development/kotor-coop-public push origin main
```

Do not push or mirror any private branch/ref to the public remote. A public commit's
history is independent; `.publication-manifest.json` records the corresponding private
source SHA (not a private source URL or local path), policy hash and output hashes.
The inventory records only the resolved commit, not the spelling of `-SourceRef`, so
`main` and its equivalent SHA produce identical provenance. The CLI plan still shows
the requested ref for convenience.
Source commit provenance identifies the bytes; it is not legal or malware clearance.

## Check for drift

```powershell
pwsh tools/Prepare-PublicRepository.ps1 -Destination D:/Development/kotor-coop-public -SourceRef main -Check
python -m unittest discover -s tools/publication -p test_export_public.py -v
```

`-Check` never writes. Exit 0 means the target matches, 1 means planned writes/deletions,
and 2 means a policy/precondition failure. `-DryRun` reports the same plan with exit 0
when valid, even if differences exist. A pending public change is a refusal, not silent
overwrite: review and commit it or reconcile the canonical private source first.

The exporter deletes only unchanged files recorded in its previous inventory, plus the
five legacy overview paths during explicit first adoption. Edited stale files, unknown
files, symlinks/junctions, path traversal, unexpected remotes, private links and recognizable
credential patterns cause refusal. Unmanaged files must be reviewed separately; the
script never sweeps an arbitrary directory or recursively removes `.git`/history.
The credential checks are deliberately limited and do not replace content review.

This pipeline synchronizes approved documentation and site assets. Player binaries still
use `Publish-PublicRelease.ps1` and its independent package/Defender/release-readiness gates.
