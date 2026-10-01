# What works and what doesn't

This page is for someone who wants to play KOTOR 1 from the Endar Spire to the Star Forge with a
friend, and wants to know what to expect before starting. It is written for players. The
developer version, with test dates, logs and settings, is the
[evidence log](research/limitations-evidence-log-2026-10-02.md) and
[status-engineering.md](status-engineering.md).

**Where things stand.** Joining, exploring, fighting and talking have been played through on two
real copies of the game across the early game (the Endar Spire, Taris, Dantooine) and selected later
scenes (the Leviathan, Manaan, the dark-side temple, the Star Forge). Eight copies on one PC have
travelled and fought together. A complete start-to-finish campaign with two people has **not** been
played yet, so expect the odd rough edge in places nobody has tried.

This page describes the current development version, which may be newer than a downloadable release.

## How to read the status words

| Word | What it means |
| --- | --- |
| **Works** | Watched working on two real copies of the game, more than once. |
| **Works, with a catch** | Works, but there is a real gap you will notice. The catch is spelled out. |
| **Not shared, by design** | The mod chooses not to do this, and says why. |
| **Untested** | Built, and the automatic tests pass, but nobody has watched it on a running game. Treat it as "probably". |
| **Should work, untested** | Same, for a whole platform (Linux and the Steam Deck). |
| **Not supported** | Don't do this. It can break the game, and that is a limit of the original game. |
| **Not a mod limitation** | The base game already behaves this way, with or without the mod. |

---

## Getting into a game together

| | Status | What to expect |
| --- | --- | --- |
| Starting from the game's own menu | **Works** | There is a **Co-op** button on the main menu. One player presses **Host**; the other picks the server and presses **Connect**. Nothing to launch by hand. |
| Your own character | **Works** | The first time you join, the normal character-creation screen opens and you make your character. That character comes with you every time you join, and your single-player games are never touched. While you are connected, the mod keeps a save called **Co-op** in your Load Game list; load that one, not Auto Save. |
| Bringing your own backpack and credits | **Works** | Your items and credits stay yours. If you disconnect and come back, you get your own back, not your partner's. |
| Playing over the internet | **Works, with a catch** | The reliable way is a free tunnel program such as playit.gg, run by the host. The built-in automatic port opening was tried once on a real home connection and did not work, probably because of the internet provider. If your friend cannot connect, use a tunnel. The installer deals with the Windows firewall. There is no relay server. |
| GOG and Steam copies together | **Works** | Any mix. Two copies on one PC need at least one to be the GOG version. |
| Saving and loading while connected | **Works, with a catch** | Either player can save or load, and the other one rejoins by itself in about 10 to 20 seconds. The catch: if the host loads a save in the Endar Spire's opening area, the other player can keep retrying until the opening movie ends, and very occasionally sync stays off until the host loads again. |
| Reconnecting after a drop | **Works** | You come back into the shared world. If the mod cannot tell your world still matches, it reloads you into the host's world after about 15 seconds, keeping your character, backpack and credits. |
| Your partner quits to the menu | **Works** | Their character disappears and a line on screen says they are away. They reappear when they come back. |
| The host's game crashes | **Works** | Within about six seconds you are released to play on alone, and the mod reconnects when the host returns. |
| Three or more players | **Works, with a catch** | Up to eight can connect. Three players made twelve area changes in half an hour, and eight copies travelled and fought a shared enemy together. A whole campaign, and every kind of fight at that size, has not been tried. |
| Different mods on each side | **Works, with a catch** | The mod compares installs when you connect and tells you on screen what differs and what it switched off because of it. Any difference in the game's core files switches things off cautiously, even a cosmetic one. Checking a large install can make startup slower. |
| Wrong mod version on one side | **Works** | Refused in about a second with a clear message. |

## Moving around the world

| | Status | What to expect |
| --- | --- | --- |
| Walking through doors and area changes | **Works, with a catch** | Either player can lead and the other follows through the same loading screen. The catch: for about a second after arriving, your partner can appear in the wrong spot before snapping into place. |
| Flying to another planet | **Works** | Either player can choose the destination on the galaxy map and the other is taken along, movie and landing included. |
| Choosing your companions before leaving the ship | **Works** | The host picks. If the other player starts the departure, the host sees a "wants to leave, choose your party" message and picks. |
| Seeing each other | **Works** | Your partner is a real in-game character with their own face, armour and weapons, on the real floor, and on the minimap and map. |
| Companions following the party | **Works, with a catch** | Companions follow the host's game, and a companion left behind is gathered back automatically. A companion controlled by the other player moves exactly as that player's game says, not on its own. |
| The map | **Works** | The map reveals around your partner as well as you. |
| Chests and doors, and who opened them first | **Works** | If you both click the same chest, exactly one of you opens it. Loot goes to one of you, never both. |
| Doors and chests the story locks or closes again | **Works, with a catch** | Opening, closing, locking and unlocking reach the other player. The catch: a door or chest that the story itself locks again (for example in the Sith base) has not been watched yet. |
| Using the `warp` console command quickly in a row | **Not supported** | Several `warp`s in a row can crash the game. That is a bug in the original game, and real doors do not do it. |

## Fighting

| | Status | What to expect |
| --- | --- | --- |
| Fighting the same enemies | **Works** | Enemies have one health bar that you both damage. Kills, deaths and loot agree on both screens, for one boss or a room full. |
| Which player an enemy goes after | **Works** | An enemy turns on whoever is hurting it most and lets go of a player who is down. |
| Really fighting, not just watching | **Works** | Your own game runs up to three fights at once for real. Enemies beyond that are mirrored, and your hits still land. |
| Enemies splitting attention | **Works, with a catch** | An enemy the host hit first will turn on you once you have clearly out-damaged the host. The game's own AI can still pull it back to the host. Not every kind of fight has been checked. |
| Stuns, fear, poison and other effects | **Works, with a catch** | Twelve kinds of effect reach your partner's real character or the shared enemy, in both directions, and healing and curing reach your partner too. Catches: a medpac only heals the person using it, and Speed affects only the caster. |
| Sneaking | **Works** | An enemy cannot target a partner who is sneaking, and their stand-in turns see-through on your screen. |
| Companions in combat | **Works, with a catch** | A companion's gear and experience stay in step on both sides. A companion fighting for the *other* player in a fight away from the host is off unless you switch it on. |
| Mods that replace a companion | **Works, with a catch** | Such replacements reach the other player, and a replacement companion carries across a reload. A full run of such a mod's story has not been played. |
| Going down, being rescued, a full wipe | **Works, with a catch** | A downed player watches from a spectator camera and can be rescued by their partner or a companion. After a full wipe the host reloads and the other player recovers on its own, keeping their own character and gear. The catch: repeated rescues used to crash the host in some fights on Manaan (now fixed, and tested through about 150 rescues), but a clean game-over sequence has not been watched start to finish. |
| Enemies scale with the number of players | **Works** | With more than one player, enemies have more health and hit harder (two players: about half as much health again and slightly harder hits) and shops stock more. Alone, nothing changes. |
| Placing a mine yourself | **Works, with a catch** | Off by default; you turn it on in the settings. When on, your partner sees the mine where you put it and enemies set it off for both of you. |
| Experience and levelling | **Works, with a catch** | Kill and story experience are earned by both players' games at the same time. If you both start with the same amount, you stay level with each other. If one character is already ahead (for example you brought a higher-level character), they keep their lead: experience is matched up to the higher total, never added together, so a gain by the one in front does not lift the other. |
| The Endar Spire and early Taris duels | **Not a mod limitation** | The game's own tutorial stops anyone dropping below 1 hit point there, so nobody can be downed in those fights. |

## Talking to people

| | Status | What to expect |
| --- | --- | --- |
| Conversations | **Works, with a catch** | One conversation at a time. Whoever starts it chooses the replies, and the other watches it play out live and sees the results. This is deliberate: two people steering one conversation would break the story. |
| Recruiting companions through conversation | **Works, with a catch** | When the host recruits someone, they join both players' parties. All nine companions have arrived correctly this way. The catch: recruiting led by the *joining* player has not been shown to reach the host, so let the host lead recruitment. |
| Cutscenes | **Works** | They play on both screens, and if one moves a player to a new area the other follows. |
| The Star Forge solo duels (Bastila, Malak) | **Works, with a catch** | The player not in the duel is held in place and watches. The duels themselves have not been played through with two people. |

## The party, items and shops

| | Status | What to expect |
| --- | --- | --- |
| Your inventory | **Works** | Your backpack is your own. What you pick up, buy, sell or craft stays yours. Quest items are the exception: they appear for both of you so the story keeps working. There is a setting to share one stash instead. |
| Your credits | **Works** | Your credits are your own. A credits reward from a conversation pays each of you the full amount. |
| Switching which party member you control | **Not shared, by design** | Only the host switches the active party member; everyone sees it. |
| Choosing the party before a departure | **Not shared, by design** | The "pick your party" screen belongs to the host. |
| Equipping a companion as the joining player | **Works, with a catch** | The host owns companion gear, so the joining player's companion Equipment screen closes with a note. Ask the host to equip them. |
| Merchants | **Works, with a catch** | Shop stock is shared: what one of you buys is gone for the other. What you buy and what you pay stay yours. The shared stock only updates while someone has the shop open. |
| Journal and quests | **Works** | Quest progress merges upward: whatever the furthest-along player has done, both have done. |
| Things an NPC remembers ("we've already talked") | **Works, with a catch** | The important ones reach the other player, including after a late join or a change of area. Not every minor flag in the game is shared. |

## Minigames

| | Status | What to expect |
| --- | --- | --- |
| Pazaak | **Works** | One player plays; the other is told they are waiting. |
| Swoop racing | **Works, with a catch** | One player races and the other is held in place with a "waiting" line, then released a moment after the race ends. Checked through the real Taris race, in both directions, including quitting or crashing mid-race. The catch: the Manaan and Tatooine races have not been run, and it is not yet confirmed that story experience from a race reaches the other player. |
| The Ebon Hawk turret ambush | **Works** | The player at the turrets fights for real; the other waits and is released when it is over. |

## Controls and interface

| | Status | What to expect |
| --- | --- | --- |
| Keyboard and mouse | **Works** | Everything the game already does, plus `~` for the console and Enter to chat. |
| Gamepad | **Works, with a catch** | Any Xbox-style pad plays the game and the menus. Typing (address, password, chat, console) uses an on-screen letter grid. It has only been tried with a simulated pad, not a real one. |
| Connecting by typing an address | **Works** | Choose **Connect to an address...** at the bottom of the server list and type the address the host gave you. |
| Seeing that you are connected | **Works** | A line of text at the top left shows the connection and fades out when it is not needed. |
| Pausing | **Works, with a catch** | The pause key pauses both games. Opening a menu only pauses *your* game, but for a few seconds your character cannot be hit on your partner's screen. Stay in a menu too long and that protection lapses, so it is a cushion, not a way to sit out a fight. |

## Linux and Steam Deck

| | Status | What to expect |
| --- | --- | --- |
| Playing through Proton or Wine | **Should work, untested** | Built to run with one launch option added. Nobody has tried it on real hardware. |
| The Deck's built-in controls | **Should work, untested** | They reach the game as an Xbox pad. |

## Story paths

Each of these was played on two real copies of the game, usually jumping ahead with the developer
commands so the test reached the scene without replaying the whole story. The scene itself ran
through the game's own scripts and conversations.

| | Status | What to expect |
| --- | --- | --- |
| Becoming a Jedi on Dantooine | **Works, with a catch** | Whoever talks to Master Dorak first chooses a path, as in single-player. The other player is then asked on their own screen to choose Guardian, Consular or Sentinel, so you can end up on different paths. Each player then gets a lightsaber in their own colour, with no duplicates. The catch: the long run from Taris to Dantooine that earns the training has not been played with two people. |
| Story-skip mods (Super Skip Taris, Jedi From The Start) | **Works, with a catch** | With the same mod files on both computers, the skipped story, journal and class choice carry across. Other mods' scripts are not certified. |
| The Leviathan | **Works, with a catch** | The capture, the cell conversation and the interrogation matched on both screens, the other player is held still while the host swaps to a rescuer, and a host reload brings them back. Not reached yet: the command deck, the escape and the Malak reveal. One crash was seen in three elevator rides to the next deck. |
| The dark-side temple | **Works, with a catch** | When the host takes the dark path, Bastila joins the party, Jolee and Juhani leave and turn hostile, and both players see the same thing. If the *other* player leads that conversation, the scene never starts: let the host lead it. |
| Manaan (the sea-floor trip) | **Works, with a catch** | When the host goes underwater, the other player stays at the station, held still with a "waiting" line, and is released when the host returns. A host disconnect mid-trip does not strand them. The joining player cannot start the airlock themselves, because that crashed their game. The star map and the control-station puzzle were skipped when testing. |
| Cutscenes and visions between areas | **Works, with a catch** | The Endar Spire escape, the dream, the Taris landing and the end credits played on both screens. The full ending chain and the final fights with two people have not been played. |
| Saving with your partner next to you | **Works** | Your partner's stand-in is removed while the game saves, so it is never written into your save. |

## Things that look like bugs but are not

- **Nobody can be knocked out in the Endar Spire.** That is the game's own tutorial rule.
- **The joining player cannot start a Manaan airlock, and only the host starts the underwater trip.**
  That is deliberate: the trip is for one player at a time.
- **A settings file from an old install keeps its old values.** The installer never overwrites your
  settings. If something this page says is on seems off, run the installer's "update defaults"
  option, or compare with the example settings file that ships with the mod.

## Not yet tried on real hardware

Built and tested automatically, but nobody has watched them on a running game:

- A friend on a different internet connection joining through the automatic port opening.
- The Windows firewall rule making the difference on a second real PC.
- Very long sessions: the longest was about three and a half hours with two copies.
- Wide-screen resolutions (1280x720 and 1920x1080 fell back to the game's own 800x600 when tried).
- Linux and the Steam Deck, from the mod loading under Wine to a session with a Windows PC.
- A real gamepad, including typing with one.
- A complete two-player campaign past Taris and through the ending.

If you try one of these and it works, or doesn't, that is the most useful bug report there is.
[PLAYTEST.md](PLAYTEST.md) lists each one with the exact steps, and `KotorCoop.Agent.exe logs`
makes a zip of logs to attach.
