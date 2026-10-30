# Playtest checklist: the things only a person at the keyboard can prove

This checklist mixes verification of implemented features with exploratory checks of known gaps
(including duel rings and the dark-side branch). A passing unit suite does not prove these game
paths, and some earlier rig limitations have since been overcome; see the dated review log.
Each item says what to do, what should happen, and what to look for. After each session choose
**Create support report** (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)) and review the zip before attaching it, whether it
worked or not: a "worked" report is as useful as a bug.

Two players, both on the current build, each with their own copy of the game. The host loads a save that
has reached the point in the list. Log files are `kotorcoop-plugin.log` (in the game folder) and the agent
log; "the joiner's log" means the joining player's copy.

## Beginner acceptance check — not yet verified

Use the exact packaged release on two independently installed PCs, with two people who have
not developed the mod. Follow the [install guide](../site/install.html) and
[play guide](../site/play.html). A maintainer typing commands or changing files for a player
counts as assistance, not a pass. Record each result separately; a same-PC rig or automated
test does not establish these results.

| Check | Player task | Pass evidence |
| --- | --- | --- |
| Clean installation | Install as an ordinary Windows user on Steam and GOG copies; follow any Windows permission prompt. | Both find the game folder, complete setup and see Co-op without a terminal or manual file edits. Record the package version/hash and store/build. |
| Honest options and update | Open setup without the optional Steam component. Upgrade an older configuration and inspect recommended settings changes; try both keeping and applying them. | Unavailable invites are explained. Choices persist, the original settings have a backup when changed, and the intended gameplay defaults match. |
| Same-network joining | Host, discover the session, create the joining character and play together. | Both players reach the same world using only the guide and visible controls. Record time and any assistance. |
| Different-network joining | Use two separate internet connections. Copy the connection address, then join; if it fails, follow Connection help. | Record whether direct joining succeeds and any required router/tunnel work. A copied public address or local self-test is not evidence of outside reachability. |
| Save and return another day | Save, leave through F9, close both games, then host and rejoin on the next day. | Both recover the intended checkpoint, characters, inventories and credits without commands or save-folder manipulation. |
| Disconnect and recover | Temporarily interrupt a connection, restore it, and use Retry last connection if needed. On disposable test saves, separately interrupt the host helper and host game. | Clear status, released controls, successful Start/resume hosting/rejoin, and explicit accounting of any unsaved progress lost. Test each fault separately. |
| Useful support report | Create a report in game and with the double-click collector after the game closes. | Player locates the ZIP, understands READ-ME-FIRST.txt and attaches it without hunting for logs. Known private test addresses, paths and credentials are absent; diagnostic errors remain. |
| Complete campaign | Play naturally from Endar Spire through an ending, including ordinary recruitment, training, Leviathan equipment return, Manaan and final duels. | Dated checkpoints and reports from both PCs; no developer warps, skipped blockers or unexplained item loss. Any workaround is recorded as a remaining limitation. |

The new connection/recovery menu actions and installer migration flow also require ordinary
in-game/desktop confirmation. Do not mark this checklist complete from unit or package tests.

## From a new game

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 1 | On the **Endar Spire**, walk to the escape pods so the party-select screen opens. Host presses OK. | The host leaves for the escape sequence; the joiner follows and arrives in the same place. | Joiner log: no crash. Note whether the joiner had to click anything. |
| 2 | Same screen, but the **joiner** is the one who reaches the pods first (host stands elsewhere). | Either the joiner also gets the screen and the leg starts, or it is refused with a message. It must not freeze. | Whether the joiner can leave the Hawk/ship on their own. This is the "client-led departure through a party-select screen" open question. |
| 3 | Press **F4** (quick save) with your partner standing next to you, then load that save. | The save is clean: no look-alike NPC and no extra map pin where your partner stood. | Host log: `save flush: holding the save ... while N body(ies) are destroyed`, then `re-issued`. |
| 4 | Save from the **Save Game menu** (not F4) with your partner beside you. | Same as above. The button uses the same path but has not been clicked. | Same `save flush:` lines. |
| 5 | Open the server browser. Pick **Connect to an address...** at the bottom and type the host's address. | A text box opens pre-filled with the last address; OK connects (asks for the password if there is one). A bad address shows a short reason and stays open. | Joiner log: `native browser:` lines. Note whether the box renders and the cursor works. |
| 6 | Join, then open **Equipment** on a companion (as the joiner). | The screen closes with a note that the host equips companions. Nothing is lost. | Joiner log: `companion equip:` lines. Check the item is still in the inventory. The close itself is proven on the rig with the `showgui equip` verb; a real key press and the item check are what is left. |
| 7 | Press the **stealth** button as one player and walk past a hostile on the other player's screen. | The hostile does not go for the sneaking player's stand-in. | Other player's log: `phantom 0x..: back to neutral; attackers released (peer is in stealth)`, and `joined the player faction` when stealth ends. Note whether the sneaker's copy looks odd. |

## Taris

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 8 | Put on the **Sith uniform** (or any disguise) as one player. | The other player sees that stand-in change appearance and change back when it comes off. | Other player's log: `identity: phantom slot N ... now renders appearance R`. |
| 9 | Enter the **Taris duel ring** and fight as one player while the other watches from the same area. | Note whether the partner's stand-in wanders into the ring and gets targeted. | Any enemy attacking the stand-in. Not handled yet, so this tells us how bad it is. |
| 10 | Recruit **Mission**, **Zaalbar** and **Bastila** in turn with the other player present. | Each new companion appears for both players without leaving and rejoining. | Whether the joiner's roster shows each. All nine slots are proven on the rig through the engine's own recruit calls; this row checks the real conversations. |

## Ebon Hawk and Dantooine

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 11 | Leave the Ebon Hawk with the party-select screen: once as host, once as joiner. | The other player is taken along either way. | Whether the second player is left behind, and any "refused" message. |
| 12 | Fly to a planet: **host** clicks the map, then the **joiner** clicks the map. | Both work, both arrive together. | Already verified; recheck only after other changes. |
| 13 | Take the **Sith-fighter ambush** turret: host plays and **wins** (shoot the fighters down), joiner watches. | The joiner waits, then carries on with the host back on the Hawk. | Joiner log: `not following ... single-seat`, then `minigame lock: ... finished`. Proven on native v86 (2026-09-30): real shooting killed all six native fighters in 11.513 s, host HP remained 2700/3000, and the stock script returned to the Hawk. The parked joiner moved zero distance during two seconds of W with native lock=1, then 1.04 units with lock=0 after return; the host also moved afterwards. Evidence: `artifacts/turret-live-v86/shoot6-proof.json` and `host-positive6.json`. This checks the stock ambush, not the complete mod skip sequence. |
| 14 | On **Dantooine**, complete the natural training prerequisites and play Dorak's path choice as host, then as joiner in another run. Pick different classes on the two screens. | The speaker gets their chosen class; every eligible partner receives their own private Guardian, Consular or Sentinel choice. Each then chooses their own feats and powers on their level-up screen. Reconnect and join late to check that an eligible character still receives the choice exactly once. | Record each character's class before and after, both private prompts, and the reconnect/late-join results. The older shared-class relay logs do not prove this current flow. |
| 15 | Build the **lightsaber** with Zhar as the **joiner** (walk through the trigger in front of Zhar first). | One saber, and both players' shared stash agrees afterwards. | Save on both sides afterwards and compare the stash. A host-led build already matches on both sides. |

## Later planets

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 16 | **Manaan**: get the envirosuit, use an airlock door in the Hrakert Station as host. (The scripted airlock and the return by `warp` are already proven on the rig; what is left is the walk on the sea floor and the real return trigger.) | The joiner stays in the station; the host walks the sea floor; on return the joiner is still fine. | Joiner: `not following the announced transition to 'manm28ab': that is a single-seat minigame track` and the on-screen line "is in a solo section - waiting". |
| 17 | **Leviathan**: play the capture and the brig break as host (choose a rescuer), then open the **locker** by the cells and take your gear back. | The joiner is parked while the rescuer is played and released when the party is freed. Every item comes back exactly once, with its upgrades, for both players. | Host: `solo segment: began in lev_m40aa`. Compare both players' inventories before the capture and after the locker; note anything missing or doubled. |
| 18 | Play through an **ending** cutscene chain (Star Forge end, credits) as host. (The first hop already works on the rig via `warp STUNT_50a`; the multi-hop chain and other STUNT scenes need a real run.) | The joiner also sees the scenes. | Both logs: `stunt leg:` lines. Note anything skipped. |
| 19 | **Star Forge**: Bastila and Malak duels as host. | The joiner parks and watches. | Already verified with a forced trigger; the real fight is not. |
| 20 | **Temple summit** (Unknown World): make the **dark-side** choice as host ("Together we can rule the galaxy", then "Death to the Jedi"). | Jolee and Juhani leave both players' parties and fight you on both screens; hits and deaths agree; Bastila joins. | Whether Bastila joins both parties. The removal, the fight, hits and deaths are already proven on the rig with a staged save. That save's Bastila cannot run the join script, so only a real playthrough can show it. |

## Internet and install

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 21 | Host on a **home connection with UPnP** and read the status line. | It reports local self-test evidence or possible additional NAT. Internet access stays unconfirmed until an outside friend connects. | The status wording and a real connection from another network; a local probe cannot prove WAN ingress. |
| 22 | Install on a fresh Windows PC as a normal user, host once. | A Windows Firewall prompt or the rule is added; a LAN friend can join. | Whether the game freezes behind a hidden prompt. |


## Added 2026-09-24

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 25 | With `placed_mine_sync = 1` on both sides, place a mine from the radial menu as the joiner, then **recover** it from the radial menu. | The mine appears on both screens; recovering it removes it on both and gives the item back once. | Whether both copies go away and where the recovered mine item lands. A console-driven recover does nothing yet. |
| 26 | Walk past a door the story **re-locks** (the Taris Sith Base lab door, or the door Trask seals on the Endar Spire) with your partner elsewhere. | Your partner's copy closes and locks too. | Try the door on the partner's side afterwards: it should say locked. Already proven with the lab door on the rig. |
| 27 | Disconnect and reconnect (console `disconnect`, then `reconnect`) without loading anything. | Enemies, doors and items keep syncing; no "different save" warning. | Joiner log: `same world, not rejoining`. Already proven on the rig. |

| 29 | As a Jedi with Force points, cast **Cure** or **Heal** on your hurt partner (`partner_buffs`, on by default). | Your partner's real health goes up once. | Partner's log: an `effect: applied` heal line. Proven on the rig: a host Cure healed the joiner 4 -> 12. |
| 30 | Both players fight the same enemy; the joiner out-damages the host clearly. | The enemy turns on the joiner and stays on them instead of flipping back and forth. | Host log: `threat` retarget lines; whether the enemy keeps switching. The joiner-only case and the alternating equal-damage case are proven on the rig with the `attack` verb. |

If something here crashes the game, the support report includes a summary of the crash record
that survives a hard crash. The raw stack stays in the original game folder for follow-up.

## Story-skip follow-up checks (2026-09-30)

Use identical mod files on separate test installs. Keep a file-level backup before
patching: HoloPatcher `--uninstall` failed to restore the previous test installs;
that run was restored by comparison with `D:\kotor-story-skip-backup2`.

| Check | Setup and action | Evidence required |
| --- | --- | --- |
| New-game hijack | Connect a joiner in an older world, block its main thread for more than 45 seconds with an automatic crossing queued, and start a new game on the host before releasing the joiner. | Passed live on 2026-09-30: the old automatic request was refused and the host stayed in its new game. Typed warp and load commands also retained their explicit intent after 50-second freezes. See [live evidence](research/limitations-live-2026-09-30.md). |
| Busy-host announcement expiry | Keep the host in real combat for more than 20 seconds while a joiner completes an actual warp and announces its destination. Keep the joiner from following back before the deadline. | Passed live on 2026-10-01: the host dropped the native announcement after 20,005 ms of combat and stayed in its original area. See [live evidence](research/limitations-live-2026-09-30.md). |
| JFTS world edits | Put the joiner in the host's same Endar Spire world before the class choice; choose different Jedi classes. After the scripts finish, inspect Trask and both lockers on each side. | Passed through automatic saved-world refresh on 2026-09-30: Trask replacements and refilled lockers matched. This is a scoped reload after the class script, not continuous replication of arbitrary script edits. |
| JFTS scale | Repeat class choice and world refresh with at least three players. | Passed live with Guardian, Consular and Sentinel choices, separate personal item stacks and unchanged Quick Save. Explicit recovery also passed on both joiners after background character capture. See [live evidence](research/limitations-live-2026-09-30.md). |
| Skip and turret fight | Join after the opening conversation, choose the skip, and win the turret fight through real shooting. | All players arrive at the following playable scene; bypassing or forcing the fight's completion does not prove shooting. |
| Verification repetition | Run `tools/verify.ps1` twice in the same two-instance session. | Passed on native89d: each run reported 49 PASS, 0 FAIL and one explicit opt-in transition skip, with no session restart between runs. Preserve the per-run counts; historical totals included the removed LAN/browser checks. See [live evidence](research/limitations-live-2026-09-30.md). |

## Card persistence regression

Buy a Pazaak card and select a distinctive side deck as the joiner. Save, disconnect,
and rejoin the same host; check the collection counts and selected deck. Repeat after
a module transition and agent restart. Host gold and party composition must remain
host-owned. An old character sidecar needs one fresh save capture to acquire card data.
