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

This page was written for the v0.1 beta release.

**Beta warning.** Dialogue appearing over the host's equipment
menu and Trask disappearing on the joining player's screen have fixes awaiting ordinary
in-game retesting. Save-name input, container responsiveness, subtitles/skipping and the
enemy-sighted banner after saving also await confirmation. Historical **Works** results
below do not close these newer reports. Public distribution is still gated by the
[release checklist](RELEASE-READINESS.md), including legal review.

## How to read the status words

| Word | What it means |
| --- | --- |
| **Works** | Watched working on two real copies of the game, more than once. |
| **Works, with a catch** | Works, but there is a real gap you will notice. The catch is spelled out. |
| **Not shared, by design** | The mod chooses not to do this, and says why. |
| **Untested** | Built, and the automatic tests pass, but nobody has watched it on a running game. Treat it as "probably". |
| **Not supported** | Don't do this. It can break the game, and that is a limit of the original game. |
| **Not a mod limitation** | The base game already behaves this way, with or without the mod. |

---

## Getting into a game together

| | Status | What to expect |
| --- | --- | --- |
| Starting from the game's own menu | **Works** | There is a **Co-op** button on the main menu. One player presses **Host**; the other picks the server and presses **Connect**. Nothing to launch by hand. |
| Your own character | **Works** | The first time you join, the normal character-creation screen opens and you make your character. That character comes with you every time you join, and your named single-player saves are not touched. While you are connected, the mod keeps a save called **Co-op** in your Load Game list; load that one, not Auto Save. Joining uses the Auto Save slot, so your own autosave is first kept as a save called **Autosave before co-op** (the two most recent are kept). If the game crashes during a co-op save, your own quick save is put back, or kept as **Recovered quicksave**. |
| Bringing your own backpack and credits | **Works** | Your items and credits stay yours. If you disconnect and come back, you get your own back, not your partner's. |
| Playing over the internet | **Works, with a catch** | The reliable way is a free tunnel program such as playit.gg, run by the host. The built-in automatic port opening was tried once on a real home connection and did not work, probably because of the internet provider. If your friend cannot connect, use a tunnel. The host must allow the agent through Windows Firewall: setup asks "Allow friends to connect to games you host?" and Windows asks for permission, so say yes. If you skipped it, run setup again. There is no relay server. |
| GOG and Steam copies together | **Works** | Any mix. Two copies on one PC need at least one to be the GOG version. |
| Saving and loading while connected | **Works** | Either player can save or load, and the other one rejoins by itself in about 10 to 20 seconds. If the host loads a save while the other player is still watching the Endar Spire's opening movie, the other player rejoins as soon as the movie ends. |
| Reconnecting after a drop | **Works** | You come back into the shared world. If the mod cannot tell your world still matches, it reloads you into the host's world after about 15 seconds, keeping your character, backpack and credits. |
| Your partner quits to the menu | **Works** | Their character disappears and a line on screen says they are away. They reappear when they come back. |
| The host's game crashes | **Works** | Within about six seconds you are released to play on alone, and the mod reconnects when the host returns. |
| The host's helper process crashes | **Works, with a catch** | Restarting the helper does not restore the hosted session by itself. The host must choose **Host** again before the joining player can reconnect. A replacement helper waiting in menu mode is not a recovered session. |
| Three or more players | **Works, with a catch** | Up to eight can connect. Three players made twelve area changes in half an hour, and eight copies travelled and fought a shared enemy together. A whole campaign, and every kind of fight at that size, has not been tried. |
| Different mods on each side | **Works, with a catch** | The mod compares installs when you connect and tells you on screen what differs and what it switched off because of it. Any difference in the game's core files switches things off cautiously, even a cosmetic one. Checking a large install can make startup slower. |
| KMRP graphics patch | **Untested** | A connection fix compares matching executable and patch files so different memory addresses do not reject the join. Both players need the updated co-op build and matching patch settings. Full KMRP gameplay compatibility still needs a two-player playtest. |
| Wrong mod version on one side | **Works** | Refused in about a second with a plain message such as "Your co-op mod version differs from the host's ... Both players need the same release." A wrong game version or password gets its own plain message too ("Wrong password. Ask the host for the password and try again."). |
| The host stops responding | **Works** | After about a minute you see "The host has stopped responding. Still trying - type `disconnect` to play on alone." After five minutes the mod gives up: "Gave up on the host after 5 minutes. Use Co-op > Connect, or type `reconnect`, to try again." |

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
| Loot on enemy bodies | **Works** | One copy for the party: whatever one of you takes from a body is gone for the other too. Set `corpse_loot = personal` if you would rather each find your own copy. |
| Doors and chests the story locks or closes again | **Works, with a catch** | Opening, closing, locking and unlocking reach the other player. The catch: a door or chest that the story itself locks again (for example in the Sith base) has not been watched yet. |
| Using the `warp` console command quickly in a row | **Not supported** | Several `warp`s in a row can crash the game. That is a bug in the original game, and real doors do not do it. |

## Fighting

| | Status | What to expect |
| --- | --- | --- |
| Cairn Sentinel arena boss | **Not yet verified in-game** | The optional boss now has a new stone-automaton appearance, animations and sounds. These have passed automated checks but have not been watched in the game with two players. Earlier tests of the previous boss design do not verify this replacement. |
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
| Conversations | **Works, with a catch** | One conversation at a time. Whoever starts it chooses the replies. To watch it live, the other player clicks the same person; scenes the game starts by itself (cutscenes, story conversations) pull both players in automatically. This is deliberate: two people steering one conversation would break the story. |
| Recruiting companions through conversation | **Works** | When the host recruits someone, they join both players' parties. All nine companions have arrived correctly this way. If the joining player leads a recruitment, the host's game replays the conversation's scripts the same way it does for any conversation, and we could not make it fail in testing (no stock recruiter other than Bastila could be set up for a joiner-led test), so we treat it as working. Report it if it ever doesn't. |
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
| Swoop racing | **Works** | One player races and the other is held in place with a "waiting" line, then released a moment after the race ends. Checked on the Taris, Manaan and Tatooine tracks, with either player as the racer, including quitting or crashing mid-race. After a race started from a normally loaded game, shared state stayed on and nothing needed re-syncing. Shared state staying off after a race was seen only in test setups that jumped straight to the track, and could not be reproduced from a normal load. Experience works as described under Experience and levelling. |
| The Ebon Hawk turret ambush | **Works** | The player at the turrets fights for real; the other waits and is released when it is over. |

## Controls and interface

| | Status | What to expect |
| --- | --- | --- |
| Keyboard and mouse | **Works** | Everything the game already does, plus `~` for the console and Enter to chat. |
| Connecting by typing an address | **Works** | Choose **Connect to an address...** at the bottom of the server list and type the address the host gave you. |
| Seeing that you are connected | **Works** | A line of text at the top left shows the connection and fades out when it is not needed. |
| Pausing | **Works, with a catch** | The pause key pauses both games. Opening a menu only pauses *your* game, but for a few seconds your character cannot be hit on your partner's screen. Stay in a menu too long and that protection lapses, so it is a cushion, not a way to sit out a fight. |

## Story paths

Each of these was played on two real copies of the game, usually jumping ahead with the developer
commands so the test reached the scene without replaying the whole story. The scene itself ran
through the game's own scripts and conversations.

| | Status | What to expect |
| --- | --- | --- |
| Becoming a Jedi on Dantooine | **Works, with a catch** | Whoever talks to Master Dorak first chooses a path, as in single-player. The other player is then asked on their own screen to choose Guardian, Consular or Sentinel, so you can end up on different paths. Each player then gets a lightsaber in their own colour, with no duplicates. The catch: the long run from Taris to Dantooine that earns the training has not been played with two people. |
| Story-skip mods (Super Skip Taris, Jedi From The Start) | **Works, with a catch** | With the same mod files on both computers, the skipped story, journal and class choice carry across. Other mods' scripts are not certified. |
| The Leviathan | **Works, with a catch** | The capture, the cell conversation and the interrogation matched on both screens, the other player is held still while the host swaps to a rescuer, and a host reload brings them back. Not reached yet: the command deck, the escape and the Malak reveal. One crash of the joining player's game was seen in the original engine on one of three elevator rides to the next deck; it did not happen again and could not be reproduced, so it is treated as a one-off. |
| The dark-side temple | **Works** | Bastila's scene is run by the host's game whichever player starts it. If the joining player tries to start it, the request goes to the host, who starts it, and the joining player watches it play. A player who connects part-way through joins the conversation at the point it has reached. When the host takes the dark path, Bastila joins the host's party, Jolee and Juhani leave and turn hostile, and both players see the same thing. This was tested from a saved state at the temple rather than by playing up to it. In a few early test runs the joining player did not see the conversation; that did not happen on any later run and could not be reproduced, so it is treated as a one-off. |
| Manaan (the sea-floor trip) | **Works** | When the host goes underwater, the other player stays at the station, held still with a "waiting" line, and is released when the host returns. A host disconnect mid-trip does not strand them. If the joining player tries to start the airlock, the request is passed to the host instead, because starting it themselves crashed their game; the host then gets the airlock screen (if the host is in a fight it waits a few seconds and gives up). The star map and the control-station puzzle were skipped when testing. |
| Cutscenes and visions between areas | **Works, with a catch** | The Endar Spire escape, the dream, the Taris landing and the end credits played on both screens. The full ending chain and the final fights with two people have not been played. |
| Saving with your partner next to you | **Works** | Your partner's stand-in is removed while the game saves, so it is never written into your save. |

## Things that look like bugs but are not

- **Nobody can be knocked out in the Endar Spire.** That is the game's own tutorial rule.
- **The joining player's click on a Manaan airlock or on Bastila at the dark-side temple hands the
  scene to the host.** That is deliberate: the host runs those scenes so the story stays in one place.
- **A settings file from an old install keeps its old values.** The installer never overwrites your
  settings. Double-click setup previews any settings that still match older defaults and offers
  to update them, after making a backup; decline to keep yours. If something this page says is on
  seems off, compare with the packaged `KotorCoop.DEFAULTS.ini`.

## Still incomplete or not fully verified

These still need live testing or have unresolved failures from the checks so far:

- A friend on a different internet connection joining through the automatic port opening.
- The Windows firewall rule making the difference on a second real PC.
- Very long sessions: the longest was about three and a half hours with two copies.
- Full widescreen and replacement-menu compatibility. Co-op now follows the live button
  column and chooses a visible, non-overlapping slot. The supplied KOTOR Enhanced Menu and
  Controller Support v3.0 archive was checked on this PC with GOG 1.03. Cold 1280x720,
  1920x1080, 2560x1440 and 3840x2160 now retain their requested size, render Co-op, and accept clicks
  on the drawn Co-op, browser Cancel and Options/Graphics controls. The early resolution
  fallback and isolated-background mouse offsets are fixed. Co-op also rebinds when a
  resolution reload restores its old label. The 4K startup crash is fixed by preserving
  requested windowed mode. The 1024x768 → 1280x720 → 1024x768 round trip now keeps controls
  visible and clickable. This is **not full compatibility**: Enhanced still needs a restart
  to regenerate layout/fonts, so large reductions can leave old controls outside the viewport.
  The full 4K client check used a borderless window on a private desktop; ordinary decorated
  windows can be constrained by Windows. This PC does not advertise 3440x1440, which still
  falls back to 800x600. Steam, normal foreground input, exclusive fullscreen and physical controllers
  remain unverified. Geometry tests through 5120x1440 do not establish rendering there.
  If the button cannot fit, try **F9**. See the [evidence and remaining checks](research/menu-widescreen-compatibility.md).
- Parts of the story that were skipped when testing: the Manaan star map and control-station puzzle, the Leviathan command deck and escape, and a story script locking a door or chest again (the Sith base).
- A complete two-player campaign past Taris and through the ending.

If you try one of these and it works, or doesn't, that is the most useful bug report there is.
[PLAYTEST.md](PLAYTEST.md) lists each one with the exact steps, and `KotorCoop.Agent.exe logs`
makes a zip of logs to attach.
