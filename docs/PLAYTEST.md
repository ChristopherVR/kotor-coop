# Playtest checklist: the things only a person at the keyboard can prove

This checklist mixes verification of implemented features with exploratory checks of known gaps
(including duel rings and the dark-side branch). A passing unit suite does not prove these game
paths, and some earlier rig limitations have since been overcome; see the dated review log.
Each item says what to do, what should happen, and what to look for. After each session run
`KotorCoop.Agent.exe logs` (see [TROUBLESHOOTING.md](TROUBLESHOOTING.md)) and attach the zip, whether it
worked or not: a "worked" report is as useful as a bug.

Two players, both on the current build, each with their own copy of the game. The host loads a save that
has reached the point in the list. Log files are `kotorcoop-plugin.log` (in the game folder) and the agent
log; "the joiner's log" means the joining player's copy.

## From a new game

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 1 | On the **Endar Spire**, walk to the escape pods so the party-select screen opens. Host presses OK. | The host leaves for the escape sequence; the joiner follows and arrives in the same place. | Joiner log: no crash. Note whether the joiner had to click anything. |
| 2 | Same screen, but the **joiner** is the one who reaches the pods first (host stands elsewhere). | Either the joiner also gets the screen and the leg starts, or it is refused with a message. It must not freeze. | Whether the joiner can leave the Hawk/ship on their own. This is the "client-led departure through a party-select screen" open question. |
| 3 | Press **F4** (quick save) with your partner standing next to you, then load that save. | The save is clean: no look-alike NPC and no extra map pin where your partner stood. | Host log: `save flush: holding the save ... while N body(ies) are destroyed`, then `re-issued`. |
| 4 | Save from the **Save Game menu** (not F4) with your partner beside you. | Same as above. The button uses the same path but has not been clicked. | Same `save flush:` lines. |
| 5 | Open the server browser. Pick **Connect to an address...** at the bottom and type the host's address. | A text box opens pre-filled with the last address; OK connects (asks for the password if there is one). A bad address shows a short reason and stays open. | Joiner log: `native browser:` lines. Note whether the box renders and the cursor works. |
| 6 | Join, then open **Equipment** on a companion (as the joiner). | The screen closes with a note that the host equips companions. Nothing is lost. | Joiner log: `companion equip:` lines. Check the item is still in the inventory. |
| 7 | Press the **stealth** button as one player and walk past a hostile on the other player's screen. | The hostile does not go for the sneaking player's stand-in. | Other player's log: `phantom 0x..: back to neutral; attackers released (peer is in stealth)`, and `joined the player faction` when stealth ends. Note whether the sneaker's copy looks odd. |

## Taris

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 8 | Put on the **Sith uniform** (or any disguise) as one player. | The other player sees that stand-in change appearance and change back when it comes off. | Other player's log: `identity: phantom slot N ... now renders appearance R`. |
| 9 | Enter the **Taris duel ring** and fight as one player while the other watches from the same area. | Note whether the partner's stand-in wanders into the ring and gets targeted. | Any enemy attacking the stand-in. Not handled yet, so this tells us how bad it is. |
| 10 | Recruit **Mission**, **Zaalbar** and **Bastila** in turn with the other player present. | Each new companion appears for both players without leaving and rejoining. | Whether the joiner's roster shows each. Two of nine are confirmed. |

## Ebon Hawk and Dantooine

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 11 | Leave the Ebon Hawk with the party-select screen: once as host, once as joiner. | The other player is taken along either way. | Whether the second player is left behind, and any "refused" message. |
| 12 | Fly to a planet: **host** clicks the map, then the **joiner** clicks the map. | Both work, both arrive together. | Already verified; recheck only after other changes. |
| 13 | Take the **Sith-fighter ambush** turret: host plays, joiner watches. | The joiner waits, then follows back to the Hawk when the host finishes. | Joiner log: `not following ... single-seat`, then a normal follow back. The host must not drop to the main menu. |
| 14 | On **Dantooine**, reach the Jedi Enclave and play Dorak's path choice as host, then as joiner in another run. | The chosen class is added to both players; each then picks their own feats and powers on their level-up screen. | Host: `class-grant: a script added class N ...`; joiner: `class-grant: applied a peer's class N`. |
| 15 | Pick up the **lightsaber crystal** and build the saber as one player. | Note what the other player sees. | Not handled; tells us if a channel is needed. |

## Later planets

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 16 | **Manaan**: get the envirosuit, use an airlock door in the Hrakert Station as host. (The scripted airlock and the return by `warp` are already proven on the rig; what is left is the walk on the sea floor and the real return trigger.) | The joiner stays in the station; the host walks the sea floor; on return the joiner is still fine. | Joiner: `not following the announced transition to 'manm28ab': that is a single-seat minigame track` and the on-screen line "is in a solo section - waiting". |
| 17 | **Leviathan**: play the capture and the brig break as host (choose a rescuer). | The joiner is parked and spectating while the rescuer is played, and released when the host frees the party. | Host: `solo segment: began in lev_m40aa`. Joiner: `fighting alone - spectating`. Note what the joiner actually sees and whether their gear comes back. |
| 18 | Play through an **ending** cutscene chain (Star Forge end, credits) as host. (The first hop already works on the rig via `warp STUNT_50a`; the multi-hop chain and other STUNT scenes need a real run.) | The joiner also sees the scenes. | Both logs: `stunt leg:` lines. Note anything skipped. |
| 19 | **Star Forge**: Bastila and Malak duels as host. | The joiner parks and watches. | Already verified with a forced trigger; the real fight is not. |
| 20 | **Temple summit** (Unknown World): make the dark-side choice as host. | Note what the joiner sees Jolee and Juhani do. | Not handled; tells us whether faction needs a channel. |

## Internet and install

| # | Do this | It should | Look for |
| --- | --- | --- | --- |
| 21 | Host on a **home connection with UPnP** and read the status line. | It reports local self-test evidence or possible additional NAT. Internet access stays unconfirmed until an outside friend connects. | The status wording and a real connection from another network; a local probe cannot prove WAN ingress. |
| 22 | Install on a fresh Windows PC as a normal user, host once. | A Windows Firewall prompt or the rule is added; a LAN friend can join. | Whether the game freezes behind a hidden prompt. |
| 23 | Run the game on **Linux / Steam Deck** (see the package README). | It loads and connects. | The `running under Wine` line and how far it got. |
| 24 | Plug in a **physical Xbox pad**. | Walk, fight, menus. | Anything that double-drives on Steam Input. |

If something here crashes the game, `KotorCoop.Agent.exe logs` includes the crash record that survives a hard crash.

## Card persistence regression

Buy a Pazaak card and select a distinctive side deck as the joiner. Save, disconnect,
and rejoin the same host; check the collection counts and selected deck. Repeat after
a module transition and agent restart. Host gold and party composition must remain
host-owned. An old character sidecar needs one fresh save capture to acquire card data.
