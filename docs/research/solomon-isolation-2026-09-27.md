# Brotherhood of Shadow: Solomon's Revenge — isolated verification

Tested 2026-09-27 against the DRM-free GOG KOTOR executable. Result: **experimental**
compatibility profile and passing smoke checks; no complete campaign qualification.

## Package and isolation

Downloaded the [author's Deadly Stream 1.5 bundle](https://deadlystream.com/files/file/761-brotherhood-of-shadow-solomons-revenge/):
seven `bos_sr_1.0.partNN.rar` volumes and `bossr_patch_1.1.7z`. The bundle contains the
original base installation and its 1.1 patch, source scripts and `BOS_SAVE`.
The [author's readme](https://www.nexusmods.com/kotor/mods/95?tab=docs) describes the
post-Leviathan starting point and Shadow's use of Bastila's slot.

Full writable game copies are at `artifacts/solomon-isolation/host` and `client`.
No junctions or hard links were used. The ordinary GOG and player-two installations
were not patched with Solomon resources or saves. The unrelated trailer rig was
left running independently. Downloads, installed mod assets and saves are ignored
artifacts; only our JSON declaration, helper and reports belong in the repository.

Applied the supplied `changes.ini` files with headless
[HoloPatcher](https://pypi.org/project/holopatcher/) 1.5.3 in an isolated Python
environment. PyKotor 2.3.12 and then 2.3.0 hit the same keyword-only constructor
incompatibility before installation began. The successful environment used PyKotor
2.3.0 plus `loggerplus`, with this local dependency correction in
`pykotor/tslpatcher/reader.py`:

```python
ModificationsNSS(file, replace=replace)  # previously ModificationsNSS(file, replace)
```

This changed the ignored test environment only, not the mod or repository library
code. This is not a claim that the author's Windows TSLPatcher executable was used.
Base installation completed with **0 errors, 0 warnings, 10,039 patches**; the 1.1
patch completed with **0 errors, 0 warnings, 304 patches**. The installed host was
then copied completely to the client. The initial audit found **6,654 audited
files, 617 gameplay files and zero differences**, with the Solomon profile active.

`tools/solomon-isolation.ps1` reproduces the copy/install sequence with a prepared,
working patcher environment and extracted package. It requires a fresh rig directory
and never passes the source installation to the patcher. Install a published co-op
package into both copies afterwards and copy the bundled save into their `saves/`
directories. A profile does not itself install or download the campaign.

## Live checks

Both instances used shared-block version 81 and protocol 76, loaded the bundled
post-Leviathan save in `ebo_m12aa`, and connected locally on port 27143.
Developer console commands staged individual scenarios; they do not prove the
quest's normal progression or alternate branches.

| Check | Observed result |
| --- | --- |
| Bundled save and connection | Both loaded and connected; audited content matched. |
| Mod-added Drix in `korr_m33aa` | Appeared on both; native `drixdlg` opened for the auto-follow listener. |
| Drix replies and line progression | Listener followed native dialogue nodes, including 5, 6, 7, 9, 10 and 11. Logged engine calls took 0–16 ms in this run. |
| Custom module travel | Host debug warp to `bos_orion` reached both copies with travel status 0. |
| Forced module-entry conversation | Both entered `orn_onenter` with `orn_kobayashi`; the listener adopted its locally started conversation and followed the owner. |
| Final entry-dialogue content | Both shared blocks reported Kobayashi and the same captured line. A reply-selection probe while no reply was offered correctly refused. |
| Phase-conditioned Shadow recovery | In a stock area, set `Tar_ZelkaRm=30` and recruited slot 0 through the engine. Client first logged a hold while its global lagged, then recruited and spawned the companion on subsequent passes. |
| Recovered identity | Client `gameinprogress/AVAILNPC0.utc` contains name **Shadow** and appearance row **519**, matching `p_channa.utc`. Its tag remains `bastila`, so a tag alone would not distinguish it. |
| Playable mod companion | Engine control switched to Shadow's slot and back to the PC successfully. This does not qualify scripted temporary-PC flashbacks. |
| Final profile's campaign hold | Fresh run in `bos_orion` at phase 30 refused developer `recruit 0`; both party tables remained empty while the forced conversation ran on both copies. |
| Shutdown | After stopping only these verified test game paths, both agents exited themselves and printed `Co-op agent closed: the game exited.` |

Run-two recruitment evidence: at 05:53:53.025 the client held slot 0 because the
identity was unknown; at 05:53:53.489 it recruited the template; at 05:53:54.054
it spawned and added object `0x40CB`. Both party tables then contained slot 0.
The client used solo mode for mirrored companion positioning.

An early test staging package omitted Terminal.Gui dependencies. Publishing a
self-contained agent resolved startup; `build.ps1 -Install` now publishes the
agent rather than copying an incomplete managed Debug output. A later restart
collided with another rig's instance number; using a separate instance number
resolved that launch. Neither issue was a campaign-script failure.

## Profile evidence and limits

The checked-in profile maps slot 0 to `p_channa` at `Tar_ZelkaRm=30` outside campaign
areas. This comes from the actual `ship6/add.nss`, rather than a guessed character
name. Unmapped slots 0, 1 and 2 hold automatic recovery in every area. Explicit
slot-0 holds in `bos_*`, `ship*` and `vision*` override the phase mapping, because
scene scripts can replace an occupant without changing that phase:

- `bos_orion/orn_chara3.nss`: slot 0 becomes `orn_fakechanna`.
- `bos_msp1/msp_bladoor2.nss`: slots 0 and 2 become `msp_evilchanna` and `msp_malak`.
- Other scripts recruit objects directly or replace slots 1 and 2 during scenes.

Native scripts retain ownership of those replacements. The current roster channel
does not relay template identity or an occupant generation; an unchanged slot mask
cannot tell it that a character changed. Holding recovery avoids a guessed template
but does not solve every scene, and can delay stock companion recovery with BoS
installed. Catalogue regression tests cover stale globals and the final campaign
hold precedence.

Not qualified: full quest progression, hostile campaign encounters, dismissal and
re-recruitment through story dialogue, flashbacks, temporary PCs, cinematic cameras,
alternate branches, save adoption and reconnect. One debug warp out of an active
conversation left the shared subtitle capture at an earlier speaker temporarily;
the final module-entry check matched both copies, but this does not certify all
subtitle/UI capture paths. External campaign movies, voice and music are currently
outside the content manifest.

## Evidence files

Under `artifacts/solomon-isolation/`: `base-install.log`, `patch-install.log`,
`download-manifest.json` (archive SHA-256 hashes), `final-mod-audit.log`, and
`run-one/`, `run-two/` and `run-three/` (agent stdout/stderr and native plugin logs).
`run-three/profile-hold-evidence.txt` records the final held recovery and matching
dialogue content. `deployment.json` confirms both isolated and ordinary installations
match the published agent/plugin hashes and retain their original `coop.ini` hashes.
`original-installs-audit.log` confirms Solomon is inactive in both ordinary installs
and their 237 audited files match. All isolated test games and agents were stopped.
Timestamps
distinguish fresh test runs from historical logs copied with the source game.
Final code validation: **5,079 fast tests passed**, focused catalogue/installer
checks passed again after tightening the profile, and native x86/agent publishing
passed with warnings treated as errors.
