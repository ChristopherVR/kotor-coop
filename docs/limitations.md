# What works, what doesn't — in plain language (updated 2026-09-25)

This page is for someone who wants to play KOTOR 1 from the Endar Spire to the Star Forge with a
friend, and wants to know what to expect before starting. No engine internals here; the
developer-facing version with the evidence behind every line is
[status-engineering.md](status-engineering.md).

**The short version:** two people can play together from the Endar Spire through Taris, and that
part has been tested many times. You each control your own character, the host's game is the
"real" world, and the mod keeps the two copies in step — where you are, who you're fighting, what's
in the chests, which doors are open, how the story has progressed. Past Taris the same machinery
should carry you through most of the game, but nobody has played it that far with two people yet,
and a few story moments are known not to work for the second player: becoming a Jedi on
Dantooine, the Leviathan prison break, Manaan's underwater section, and seeing the ending. Those
are listed at the bottom of this page and tracked in
[review-seamless-2026-09-18.md](review-seamless-2026-09-18.md). The other things that *don't* work
are mostly the game's own single-player assumptions that the mod has chosen not to fight (one
conversation at a time, one person drives the galaxy map).

**How to read the status words**

| Word | Meaning |
| --- | --- |
| **Works** | Watched working on two real copies of the game, more than once. |
| **Works, with a catch** | Works, but there is a real gap you will notice; the catch is spelled out. |
| **Not shared, by design** | The mod deliberately does not do this, and says why. |
| **Untested** | Built, and the automated tests pass, but nobody has yet watched it on the running game. Treat it as "probably". |
| **Should work — untested** | Like "Untested", but for a whole platform (Linux / Steam Deck) rather than one feature: everything is built and reasoned to work, but nobody has tried it on real hardware yet. |
| **Not supported** | Don't do this — it can break the game. That's a limit of the original game, not something the mod adds. |
| **Not a mod limitation** | Not something co-op changes at all — it's how the base game already behaves, with or without the mod. |

---

## Getting into a game together

| | Status | What to expect |
| --- | --- | --- |
| Starting from the game's own menu | **Works** | A **Co-op** button on the main menu. One player presses **Host**, the other picks the server and presses **Connect**. Nothing to launch by hand. |
| Bringing your own character | **Works** | Your co-op character is separate from any single-player game on the same install: the first join makes it (next row), every join after carries it, and it is refreshed whenever you save while connected. While you are connected the mod keeps its own dedicated save — **"Co-op"** in the Load Game list — refreshed every 10 minutes and as you leave, holding the host's world, shared credits and anything bought or found together. Your Quick Save and any save you make yourself under your own name are never touched by the mod. The game's own Auto Save slot is still used mechanically to bring the host's world in the instant you join or rejoin (a KOTOR engine requirement), and — same as single-player always did — the engine keeps autosaving through it as you walk through doors while connected; load "Co-op" for the save meant to be kept, not Auto Save. |
| Joining for the first time | **Works** | Connect, and the normal New Game character-creation screen opens; make your character. Shortly after you are in the host's world with it (mostly bounded by the Endar Spire's own opening cinematic, not by the mod), and that character is your co-op character on every join after (a single-player game on the same install is never mistaken for it). `join_mode = newcharacter` makes a new one; `join_mode = save` is the old bring-your-own-save way. Your starting weapon now reliably equips (live-verified fix, 2026-09-10, across several character classes). If you still see your character in their default/bare-looking appearance, that is very likely how KOTOR renders that class's premade look, not a missing item — check your inventory and equip anything unequipped by hand if something still looks wrong. If you joined on v0.9.0 and after upgrading you come up as the *host's* character, delete `saves\kotorcoop-character.pifo` and `.json` in your game folder once and join again (v0.9.0 could copy the host's character into that file when you quit; fixed). |
| Playing on two computers over the internet | **Works, with a catch** | The reliable way is a free tunnel program such as playit.gg on the host: watched working end-to-end between two separate home networks on 2026-09-10, and since 2026-09-18 the mod handles a UDP-only tunnel on its own (`world_transfer_udp_fallback` is on by default). The built-in way, where the host's public address is found automatically and the router port opened for you (UPnP), has been tried once on a real home connection and did not work: the status line said the port was open, but nobody could reach it, most likely because the ISP puts the home behind a second layer of address sharing. The host checks itself after the router answers. Since 2026-09-21 its status reports a local self-test result or possible additional NAT, with internet access still unconfirmed: a local hairpin test cannot establish outside reachability. Test with your friend; if they cannot connect, check upstream routers or use a tunnel. The installer also opens the Windows Firewall for the host (or prints the command to run as Administrator). There is no relay server. To join, pick **Connect to an address...** at the bottom of the in-game server list and type the address the host shared (the box remembers the last one); `connect address:port` at the `~` console does the same. The box was added on 2026-09-18 and has not yet been watched on a live game — if it misbehaves, the console is the way in. |
| GOG and Steam copies playing together | **Works** | Any mix. Two copies on one PC needs at least one of them to be the GOG version. |
| Saving and loading mid-session | **Works** | Either player can save and load. The other side notices and rejoins on its own; you don't need to reconnect. A save made while your partner was standing next to you used to keep a copy of them in the save (a silent look-alike NPC and a stray map pin); since 2026-09-18 the mod removes those the moment such a save loads, including saves made with older versions. |
| Disconnecting and reconnecting without loading a save | **Works, with a catch** | Usually you just reconnect and you're back in the shared world. There is a known bug where the mod occasionally mistakes the reconnect for a different save and turns off shared-world syncing; a fix is in progress. If that happens, load your game once (yours or the host's) and reconnect. |
| Your partner quits to the main menu | **Works** | Their character disappears and a line on screen says they're away. When they load back in, they reappear. |
| The host's game crashes or drops off | **Works** | Within about six seconds you are released to keep playing solo, and the mod reconnects automatically when the host comes back. |
| Three or more players | **Works** | Up to eight, same rules. Three players walking through many doors was stress-tested on 2026-09-17: twelve area changes in about half an hour, and every single time all three arrived in the same place and could see each other properly. Nothing crashed. |
| Different mods on each side | **Works, with a catch** | The mod compares installs when you connect and tells you on screen what differs and what it turned off because of it. A very different save asks you to rejoin. An item that has the same name but was changed by a mod on one side only is not detected. |
| Wrong version of the mod on one side | **Works** | Refused in about a second with a clear message, instead of a silent timeout. |

## Moving around the world

| | Status | What to expect |
| --- | --- | --- |
| Walking through doors and area transitions together | **Works** | Either player can lead; the other follows through the same loading screen. `follow_lead = host` if you want only the host to lead. |
| Flying to another planet on the galaxy map | **Works** | Either player can choose the destination at the map. The other player is taken along automatically — take-off movie, cutscene and landing on both screens — and you both arrive in the same Ebon Hawk. If you set `follow_lead = host`, the joining player's own choice at the map is politely refused instead ("only the host can choose a destination") and nothing moves; verified 2026-09-17. |
| Choosing your two companions before leaving the ship | **Untested** | Only the host ever picks — that screen has no meaning for two human characters otherwise. If the JOINING player is the one who reaches that screen, the mod now asks the host to choose instead of just closing it silently: the joiner sees "asked the host to choose the party", and the host sees "<name> wants to leave — choose your party" and picks normally. Built and unit-tested since 2026-09-25; nobody has yet watched the actual request/pick round trip on the running game — see [status-engineering.md](status-engineering.md). |
| Seeing each other | **Works** | Your partner is a real in-game character with their real face, armour and weapons, walking on the real floor, visible on the minimap and the map screen. |
| Companions following the party | **Works, with a catch** | Companions follow the host's game. A companion that got stuck behind (an old game bug) is now gathered back to the party automatically rather than left standing. A companion controlled by the *other* player doesn't move around on its own on your screen — it just moves exactly the way the other player's game says it does. |
| Fog-of-war map reveal | **Works, with a catch** | The map reveals for both. Whether the M-key map *draws* the shared reveal has never been checked on screen — only confirmed behind the scenes, not by watching it happen. |
| Chests, doors, and "who opened it first" | **Works** | Two players clicking the same chest at once: exactly one opens it. Loot goes to one of you, never both. |
| A door locked or closed *again* by the story | **Untested** | The mod now carries a door both ways — the other player's copy follows a story re-lock or re-close, not just the first opening — through the game's own door engine, same as an ordinary open. Built and unit-tested since 2026-09-21; nobody has yet watched a real story re-lock (e.g. the Sith base) cross the network on the running game. See [door-relock.md](research/door-relock.md). |
| A chest or container locked or closed *again* by the story | **Untested** | The mod now carries a chest/container both ways too, the same way it now does for doors: the other player's copy follows a story re-lock or a real close, not just the first opening, through the game's own container engine. Built and unit-tested since 2026-09-24; nobody has yet watched a real story re-lock cross the network on the running game. See [door-relock.md](research/door-relock.md). |
| The `warp` console command, used rapidly | **Not supported** | Chaining several `warp`s can crash the game. That is a bug in the original game, not the mod, and real doors don't trigger it. |

## Fighting

| | Status | What to expect |
| --- | --- | --- |
| Fighting the same enemies | **Works** | Enemies have one health bar that both of you damage. Kills, deaths and loot agree on both screens. This works for any number of enemies at once, from one boss to a room full of ambushers. |
| Which player an enemy goes after | **Works** | An enemy turns on whoever is hurting it and stays on them, rather than locking onto the host. If you both hit it about equally, it switches between you the way it would between two party members. It lets go of a player who is down. (`threat_retarget`, on by default.) |
| Really fighting, not just watching | **Works** | Each player's own game runs up to three fights at once for real (your own dice rolls, your own animations). Enemies beyond that are mirrored — your hits still land. |
| Enemies splitting attention between you | **Works, with a catch** | An enemy the host attacked first will turn on you once you've clearly out-damaged the host — and, as of 2026-09-24, the game now remembers the host's own hits too, so it takes a genuine, sustained lead rather than just any hit to pull the enemy over, and it keeps re-checking so a stray flip back gets corrected quickly. The game's own AI can still pull it back to the host on the host's next swing; whether the enemy now holds on you more reliably than before has not yet been checked in a live game. With three or more players only one partner is tracked. |
| Stun, slow, choke, heal and other effects | **Works, with a catch** | Twelve kinds of non-damage effect that land on your partner's stand-in reach their real body, in both directions. Heal and Cure now reach your partner too: a Cure you cast heals them for real (on by default, `partner_buffs`). A stun you cast on an enemy lands on the host's copy of that enemy as well, so it really stops acting (on by default, `enemy_effect_sync`). Catches: a medpac only ever heals the person using it, Speed is for the caster alone, and fear, push and stasis on enemies ride the same path as stun but have not each been watched. Only the host can equip companions (a joiner's companion equipment screen closes with a note); a joiner's medpac on a companion fighting for them counts on the host. |
| Companions in combat | **Works, with a catch** | A companion's equipment and experience stay in step on both sides. A companion fighting *for the other player specifically* (through their own copy of the game, in a fight away from the host) is off by default — set `companion_local_combat = 1` in `coop.ini` to turn it on; once on, damage it takes is real. |
| Going down, being revived, the party wipe | **Works** | A downed player watches from a spectator camera and can be rescued by their partner (or a companion). If everyone falls, the host reloads and the other player rejoins on its own. |
| Enemies scale with the number of players | **Works** | With more than one player connected, enemies get more health and hit harder (two players: 50% more health, about 15% harder hits) and shops hold more stock; alone, nothing changes. Saving and reloading keeps the health you left with, for both players. Set `enemy_health` or `enemy_damage` to `100` in `coop.ini` for the normal game. |
| Placing a mine yourself (not one already sitting in the world) | **Works, with a catch** | Off by default. Turn it on with `placed_mine_sync = 1` in `coop.ini`. Your partner sees the mine at the exact spot you laid it, an enemy walking onto it sets it off for both of you, and a party mine never hurts your partner's stand-in. Recovering or disarming a mine removes it for both of you and gives the mine back once. As in the normal game, you need at least one rank of Demolitions to recover or disarm; without it the game quietly does nothing. |
| Experience and levelling | **Works** | Kill experience and story experience end up the same on both sides. |
| The Endar Spire / early Taris duels | **Not a mod limitation** | The game's own tutorial script stops anyone dropping below 1 hit point there, so nobody can be downed in those fights — co-op or not. |

## Talking to people

| | Status | What to expect |
| --- | --- | --- |
| Conversations | **Works, with a catch** | Only one conversation at a time. Whoever starts it chooses the replies; the other player watches it play out live (their clicks and skips are ignored) and sees the consequences — quests and rewards (a recruit is a separate catch, below). Once you click the NPC to join, the subtitles catch up in well under a second — measured repeatedly, close range or far. If it still feels like a few seconds, that's normal walking time to reach the NPC (same as it would be solo) plus the game's own brief pause on the very first line of any conversation, not a co-op sync delay. This is by design: two people driving one dialogue at once would break the story. |
| Recruiting companions through dialogue | **Works** | A companion recruited in a conversation reaches both players (`dialogue_recruit_sync`, on by default). All nine companions have been checked arriving on the other player's side, and a party of recruited companions carries through a module change on both. Two were watched through their real recruitment conversation; the rest through the same game calls those conversations make. An old `coop.ini` that sets the key to 0 keeps the old behaviour, where only whoever ran the conversation gets the companion. |
| Cutscenes | **Works** | They play on both screens. If a cutscene moves one player to a new area, the other follows. |
| The Star Forge's solo duels (Bastila, Malak) | **Works, with a catch** | The player not in the duel is parked and watches. We haven't been able to prove in testing whether that lock actually stops the keyboard from working — but nothing suggests it doesn't. |

## The party, items and shops

| | Status | What to expect |
| --- | --- | --- |
| Your inventory | **Works — personal by default** | Your backpack is your own: what you pick up, buy, sell, drop or craft stays yours. Quest/plot items are the one exception — they still appear for both of you, so the story keeps working the same for both players. Set `inventory_mode = shared` in `coop.ini` to go back to one shared stash for everything (the older behaviour: pick something up on either side and it's in both inventories). If you disconnect and later rejoin (the same host or a different one), your personal backpack comes back with you — the mod remembers what you last had on YOUR OWN install and hands it back, rather than replacing it with whatever the host has. Nothing travels over the network for this; it's your own saved progress, the same way a single-player save would be. On a true first join (nothing saved yet), you simply start with an empty personal backpack plus whatever quest items the host's story already has. See [personal-inventory.md](research/personal-inventory.md) §12. |
| Switching which party member you control | **Not shared, by design** | Only the host can switch the active party member; the switch is mirrored to everyone. You get your own character back automatically when you connect. |
| Choosing the party before a departure | **Not shared, by design** | The "pick your party" screen before leaving an area is the host's. |
| Merchants | **Works, with a catch** | Shop stock is still shared: what one of you buys is gone for the other. Credits and the items you buy stay yours (see "Your credits" below) — `store_prices` in `coop.ini` (default 100 = retail prices) still applies to both of you, host-set, watched working on 2026-09-24 for both buying and selling. Two catches remain: the shared stock can only be *checked* while someone actually has the shop open (a quirk of the game), and an item a merchant has an infinite supply of never shows a change — which is harmless, since it's infinite. |
| Your credits | **Works** | Your credits are your own to earn and spend, separate from your partner's. A credits reward from a conversation pays EACH of you the full amount, not a split or a one-sided payout. Like your inventory, your personal credits come back with you across a disconnect/rejoin — your own last balance, not the host's purse and not reset to zero. Set `credits_mode = shared` in `coop.ini` (or `inventory_mode = shared`, which it follows) for the older one-purse behaviour, where spending, earning and quest rewards converge on the same number for both of you. See [personal-inventory.md](research/personal-inventory.md) §§4, 12. |
| Journal and quests | **Works** | Quest progress merges upward: whatever the furthest-along player has done, both have done. |
| Story flags an NPC remembers (e.g. "we've already talked") reaching the other player | **Works, with a catch** | Off by default — turn it on with `local_var_sync = 1` in `coop.ini`. Watched working in the game on 2026-09-24: a flag set by one player's conversation reached the other. What a flag set mid-combat does, and whether a player who joins late catches up on flags already set, still need checking before it ships on by default. |

## Minigames

| | Status | What to expect |
| --- | --- | --- |
| Pazaak | **Works** | One player plays; the other is told they're waiting and is held out of the table until the hand ends. |
| Swoop racing | **Works, with a catch** | One player races; the other is held safely off the track with a "racing — waiting" message. Rewards reach everyone. Whether the hold physically stops keyboard input is the same untestable question as the duel lock above. |

## Controls and interface

| | Status | What to expect |
| --- | --- | --- |
| Keyboard and mouse | **Works** | Everything the game already does, plus `~` for the console and Enter to chat. |
| Gamepad / controller | **Works, with a catch** | Any Xbox-style (XInput) pad plays the whole game — walking, combat, every menu and panel — through your own key bindings. Typing now works too: open the address box, the password box, chat (Enter) or the `~` console with the pad and an on-screen character grid appears — D-pad or the left stick picks a letter, A types it, B backspaces, X is space, Y/Start sends, Back cancels, LB shifts case. Not yet watched with a real pad; see "Added recently" below. Layout in the README; `gamepad = 0` turns it off. |
| Seeing that you're connected | **Works** | A one-line status in the top-left, moved off the minimap on 2026-09-10 so it no longer overlaps it; confirmed on screen, and it fades out correctly when it's no longer needed. |
| Pausing | **Works, with a catch** | The pause key pauses both games. Opening a menu (inventory, map, journal, character sheet) only pauses your own game: enemies on the other screen still can't be pulled off you for the rest of the fight the way pausing would, but for the first several seconds your stand-in stops being a target and shrugs off anything that still lands on it, so a quick check doesn't cost you the hits you couldn't see coming. Leave a menu open too long and the protection lapses; use it constantly and it cools down before it comes back, so it's a shield against surprise, not a way to sit out a fight. The game's own auto-pause options (enemy sighted, end of round) and the tutorial pop-ups are switched off while the mod runs. |

## Linux and Steam Deck

| | Status | What to expect |
| --- | --- | --- |
| Playing through Proton / Wine | **Should work — untested** | Everything is built to run inside the game's Proton prefix with nothing extra installed (the agent carries its own .NET). You add one launch option, `WINEDLLOVERRIDES="dinput8=n,b" %command%`, so Wine loads the mod's `dinput8.dll` instead of its own; the packaged README has the Steam Deck, Heroic and Lutris versions. Nobody has run it on a real Linux machine yet — the logs say `running under Wine` so a report tells us exactly how far it got. |
| The Deck's built-in controls | **Should work — untested** | They reach the game as an Xbox controller, which the controller support above picks up. Typing (address, chat, console) now has the mod's own on-screen grid, or the Deck's own on-screen keyboard through Steam Input either way. |

---

## Added recently, not yet watched on a running game

These are built and pass the automatic tests, but nobody has watched them work in the game yet. Treat them as "probably", and tell us if one misbehaves.

| | Status | What to expect |
| --- | --- | --- |
| Cutscenes and visions that the game plays between areas | **Untested** | If one player triggers a scripted cutscene area (a vision, an ending scene), the other player's game now plays it too instead of skipping it. The ending credits chain is the longest and least tried. |
| Becoming a Jedi on Dantooine | **Untested (redesigned 2026-09-25)** | Whoever talks to Master Dorak first picks their own path there, exactly like the single-player game. Every OTHER connected player who is not already a Jedi is then asked, on their OWN screen, to pick Guardian, Consular or Sentinel — nobody's choice is ever copied onto anybody else, so two players can end up on completely different paths. A player who joins later, or who was disconnected at the exact moment, is asked the next time they are in the world. The class opens at level 0, exactly as in single-player: each player then chooses their own Jedi feats and Force powers on their own level-up screen. On by default. Built and unit-tested; nobody has watched the popup or the Dantooine conversation itself work in the game yet. When one player finishes building their lightsaber with Master Zhar, every OTHER player can now get their OWN saber in their OWN class's colour too, instead of getting nothing — placed straight into their backpack, exactly where the game leaves the builder's own saber (nobody's saber equips itself automatically; each player equips their own by hand, once their character is experienced enough to use it, same as in single-player). Off by default until watched working on a running game (`first_saber_color_sync` in `coop.ini`, needed on every connected computer). Nobody ends up with two starter sabers, even after a reconnect or a late join. |
| Saving with your partner next to you | **Works** | The mod removes your partner's stand-in from the world for a moment while it saves, so it is never written into the save (watched working on a quicksave; the save-menu button uses the same path). It also cleans any such copy out of older saves when they load. |
| Fear, stun and sneaking | **Works, with a catch** | Fear and stun land on your partner's real character (watched working both ways), and a stun you put on an enemy holds on both screens. Poison is passed on and ticks on the other player once per interval. When your partner sneaks, their stand-in stops being a target for enemies on your screen (watched working with the game's own sneak toggle, 2026-09-25). By default their stand-in also turns translucent on your screen while they sneak, the same shimmer the game gives a real party member in stealth, instead of vanishing outright (`stealth_partner_visual` in `coop.ini`; set it to `hidden` for the old fully-invisible look, or `off` for no visual change at all). Built and unit-tested; the translucent look itself has not yet been watched on a running game. |
| Protection while your menu is open | **Untested** | Opening a full-screen menu (inventory, map, character sheet, journal, equipment, options) makes your stand-in on your partner's screen stop being a valid target and drops any hit that still lands on it, for up to 12 seconds. Closing the menu ends the protection immediately either way. Staying in a menu past 12 seconds costs you the shield for the rest of that visit, and starts a 20-second cooldown before it comes back, so it can't become a permanent way to duck a fight. On by default (`menu_guard_sync` in `coop.ini`). Built and unit-tested; nobody has watched it work in the game yet. |
| Equipping a companion as the joining player | **Untested** | The host's game owns companion gear, so the joining player's Equipment screen for a companion is closed with a note instead of losing the item. |
| Typing an address in the game | **Untested** | A **Connect to an address...** row at the bottom of the server list. The `~` console `connect` command still works. |
| Typing with a gamepad | **Untested** | The address box, the password box, chat and the `~` console can now all be typed into with an XInput pad: an on-screen character grid appears while one of them has focus (D-pad/stick to pick a letter, A to type it, B to back up, X for a space, Y or Start to send, Back to cancel, LB for upper case). Built and unit-tested (the grid's navigation and editing); nobody has typed with a real pad yet. |
| Story moments still known not to work | **Not supported yet** | The Leviathan prison break and the dark-side temple choice have designs but no code; the second player is likely to see them go wrong. Manaan's underwater section is handled by keeping the second player in the station while the host is under water (the underwater walk itself has not been played through). |

## Things that are *not* limitations

A few things look like bugs and aren't:

- **Your partner appears for a moment as a stranger, or as Trask, right after a warp.** This was a
  real bug. Most of it was fixed in August 2026 and it was made much faster to recover from on
  2026-09-10 — but the August fix did not finish the job, and saying so here was wrong. Two more
  faults in the same area were found and fixed on 2026-09-17, both of which looked to a player
  like "the other person is a stranger, or isn't there at all":
  - **Your partner had no clothes, or no body at all.** Putting their outfit onto their stand-in
    character was failing outright, and once it had failed it was never tried again for the rest
    of that area. On a character whose look is built out of what they are wearing, that left
    nothing to draw.
  - **Your partner was simply invisible, all session, while everything else about them worked.**
    When you join, your game reloads to pick up the host's world, and their stand-in was being
    created in the split second before your game had finished building the part of the world that
    draws characters. It ended up in limbo: it followed them around perfectly, it had the right
    face and the right clothes, and it was never drawn on your screen. Disconnecting and
    reconnecting used to be the only cure. Their stand-in now waits for your world to be ready.

  If you still see any of this after 2026-09-17, it's worth reporting.
- **"Another modal screen is up" when a shop won't open for the other player.** Fixed the same way the
  galaxy-map follow was fixed (September 2026). Also worth reporting if you still see it.
- **You can't click on a companion to talk to them right after joining.** Shares its root cause
  with the stranger/Trask portrait bug above and should be much rarer after the 2026-09-10 fix.
  Worth reporting if it still happens.
- **Your own portrait and health bar showed Trask's face after you joined.** On a first join through
  the character-creation path, the joiner's party list came up with Trask first and the joiner's own
  character past the end of the list, so the HUD's left portrait drew Trask, with Trask's health, for
  the whole session (hovering it read "Trask, Vitality 1/36"). Fixed on 2026-09-17: the hand-back that
  puts your own character first now finds it wherever the list left it.
- **Lines like `gave 'g_w_blstrpstl001' to 0x13B` on the joiner's screen during a fight.** That was
  the mod's own bookkeeping (handing an enemy stand-in its loot) answering onto the chat overlay.
  Fixed on 2026-09-17: those answers go to the log only. A command you type yourself still answers on
  screen.
- **Your badly hurt partner looked fine on your screen.** Their stand-in never carried their hit
  points, so the game's own limping walk (which it plays below a fifth of health) never applied to
  it, even though the mod knew they were at 1 hit point. Fixed on 2026-09-17: the stand-in now
  carries your partner's current and maximum hit points, and limps when they do.
- **The host's companion stood through a fight and did nothing.** The mod's "companion stopped
  following" recovery could fire on a companion that had simply arrived beside a standing host,
  teleport them into place five times, and leave the host's party in solo mode, which is the
  game's own "companions do not act" switch. Fixed on 2026-09-17: a companion within three units
  of a standing host is left alone, and the recovery puts solo mode back the way it found it.
- **Blaster fire from enemies and companions did not play on the other screen.** Melee swings
  did. The ranged attack animation arrives as a model-level animation number rather than one of
  the game's named animations, and the mod was throwing those away. Fixed on 2026-09-17: they are
  replayed the same way the firing player's own game played them.
- **Nobody can be knocked out on the Endar Spire.** The game's own tutorial rule.
- **Old `coop.ini` files.** The installer never overwrites your settings file, so an install from
  before a default changed keeps its old value. If a feature this page says is on seems off, run
  `install.ps1 -GameDir '...' -UpdateDefaults`, or compare against `profiles/coop.ini.example`.

## Still untested on real hardware

Built and automatically tested, but nobody has watched them on the running game yet:

- A player on a *different* internet connection joining through the automatic port-opening. The
  router half was checked on 2026-09-17 (a real home router was asked to open the port, agreed, and
  confirmed afterwards that it had); nobody has yet come in from outside through it. Tunnel-based
  internet play (e.g. playit.gg) has been watched working end-to-end, which is a different path.
  The reachability self-check and the status wording it drives (2026-09-18) are unit-tested but
  have not yet been watched against a real router.
- The Windows Firewall rule the installer adds (2026-09-18) has not yet been watched making the
  difference on a real second PC.
- Longer multi-hour sessions: the recovered 2026-09-17 three-player run covered 12 transitions (eight door crossings and four warps) over about 30 minutes, with all three games alive.
- The keyboard side of the "you're parked" locks (swoop, Star Forge duels) — injected keystrokes
  never reach this game, so it can't be tested without a person at the keyboard.
- The connection line outside the measured 800x600 layout, and the shared map reveal.
- Linux and Steam Deck (Proton): the whole thing, from the DLL loading under Wine to a session
  between a Deck and a Windows PC.
- A complete run past Taris. Dantooine's real class-grant dialogue, Leviathan rescue and gear
  return, the full Manaan sea-floor return, winning the Ebon Hawk turret ambush by actually
  shooting (finishing it and both players carrying on afterwards is proven), and the multi-hop
  ending still need playthrough proof. Class relay, brig detection, scripted airlock parking and one STUNT
  hop have narrower live evidence; this does not establish a complete story path. Missing
  implementation and live checks are separated in the current table at the end of
  [review-seamless-2026-09-18.md](review-seamless-2026-09-18.md).
- A physical gamepad. The controller support was verified with a simulated one. On 2026-09-17 the
  mod was confirmed to find Windows' controller support on a real PC and to correctly report "no
  pad" for two Xbox pads that were paired but switched off — nobody was there to switch one on.

If you try one of these and it works — or doesn't — that's the most useful bug report there is.

[PLAYTEST.md](PLAYTEST.md) lists each one with the exact steps and what to look for; run `KotorCoop.Agent.exe logs` afterwards and attach the zip.
