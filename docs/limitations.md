# What works, what doesn't — in plain language (reviewed 2026-10-01)

This page is for someone who wants to play KOTOR 1 from the Endar Spire to the Star Forge with a
friend, and wants to know what to expect before starting. No engine internals here; the
developer-facing version with the evidence behind every line is
[status-engineering.md](status-engineering.md).

**Current scope:** co-op joining, exploration, combat and dialogue have been exercised in
selected stock-game scenes. Three-player travel has a short stress test, and eight real
instances completed four area changes. A complete campaign and all eight-player scenarios
are not verified. Recent fixes cover scripted
Dantooine dialogue and remote lightsaber power state, but they do not certify every story
trigger or animation. See the concise [player status guide](../site/works.html) and
[September 27 dialogue checks](research/session-regressions-2026-09-27.md).

These notes describe current development code, which may be newer than a packaged release.
Historical dates below describe the scope of each check, not a guarantee for every scene.

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
| Joining for the first time | **Works** | Connect, and the normal New Game character-creation screen opens; make your character. Shortly after you are in the host's world with it (mostly bounded by the Endar Spire's own opening cinematic, not by the mod), and that character is your co-op character on every join after (a single-player game on the same install is never mistaken for it). `join_mode = newcharacter` makes a new one; `join_mode = save` is the old bring-your-own-save way. Your starting weapon now reliably equips (live-verified fix, 2026-09-10, across several character classes). The current first-join path was checked again on 2026-10-01: a newly created Scout entered the host's world with its own class and health intact. A character-screen stall caused by repeated memory scanning has been fixed. See [current live checks](research/limitations-live-2026-09-30.md). If you still see your character in their default/bare-looking appearance, that is very likely how KOTOR renders that class's premade look, not a missing item — check your inventory and equip anything unequipped by hand if something still looks wrong. If you joined on v0.9.0 and after upgrading you come up as the *host's* character, delete `saves\kotorcoop-character.pifo` and `.json` in your game folder once and join again (v0.9.0 could copy the host's character into that file when you quit; fixed). |
| Playing on two computers over the internet | **Works, with a catch** | The reliable way is a free tunnel program such as playit.gg on the host: watched working end-to-end between two separate home networks on 2026-09-10, and since 2026-09-18 the mod handles a UDP-only tunnel on its own (`world_transfer_udp_fallback` is on by default). The built-in way, where the host's public address is found automatically and the router port opened for you (UPnP), has been tried once on a real home connection and did not work: the status line said the port was open, but nobody could reach it, most likely because the ISP puts the home behind a second layer of address sharing. The host checks itself after the router answers. Since 2026-09-21 its status reports a local self-test result or possible additional NAT, with internet access still unconfirmed: a local hairpin test cannot establish outside reachability. Test with your friend; if they cannot connect, check upstream routers or use a tunnel. The installer also opens the Windows Firewall for the host (or prints the command to run as Administrator). There is no relay server. To join, pick **Connect to an address...** at the bottom of the in-game server list and type the address the host shared (the box remembers the last one); `connect address:port` at the `~` console does the same. The address box connected a real joiner through typed localhost on 2026-09-30, including a return from a loaded world to the menu. |
| GOG and Steam copies playing together | **Works** | Any mix. Two copies on one PC needs at least one of them to be the GOG version. |
| Saving and loading mid-session | **Works** | Either player can save and load. The other side notices and rejoins on its own; you don't need to reconnect. A save made while your partner was standing next to you used to keep a copy of them in the save (a silent look-alike NPC and a stray map pin); since 2026-09-18 the mod removes those the moment such a save loads, including saves made with older versions. |
| Commands queued during a long movie or freeze | **Works, with a catch (checked 2026-09-30)** | Automatic load and travel requests pending across a block longer than 45 seconds are refused so they cannot replay an older world; the co-op program can request them again. Explicit `warp` and `loadgame` commands retain your intent. Live checks covered automatic refusal, both typed commands after 50-second freezes, and a host starting a new game while the frozen joiner had a crossing queued. The host stayed in its new game. See [live checks](research/limitations-live-2026-09-30.md). |
| Refreshing scripted world changes | **Works, with a catch (checked 2026-09-30)** | Current development builds detect Jedi From The Start's first host class choice in the Endar Spire, wait for dialogue and full-screen menus to close, and request a fresh dedicated **Co-op** save. Joiners then save their own characters and reload the host's updated world, carrying their own class and personal inventory. This is a scoped saved-world refresh; other script changes still need a save on both computers followed by the joiner's `reconnect world` command. That command requires a recent readable joiner save and keeps the connection if it cannot confirm one. Three real instances completed the automatic post-choice reload with Guardian, Consular and Sentinel choices preserved, separate personal inventory stacks, matching Trask replacements and refilled lockers. Quick Save was unchanged. Explicit recovery also completed on both joiners after their fresh saves had already been captured in the background, preserving their separate classes and item stacks. See [live checks](research/limitations-live-2026-09-30.md). |
| Disconnecting and reconnecting without loading a save | **Works** | You reconnect and you're back in the shared world. If the mod can't prove your world still matches the host's (for example after the co-op program was restarted), it no longer turns shared-world syncing off: after about 15 seconds it automatically reloads you into the host's current world — one short load screen — keeping your own character, backpack and credits. |
| Your partner quits to the main menu | **Works** | Their character disappears and a line on screen says they're away. When they load back in, they reappear. |
| The host's game crashes or drops off | **Works** | Within about six seconds you are released to keep playing solo, and the mod reconnects automatically when the host comes back. |
| Three or more players | **Works, with a catch** | The connection limit is eight. Three players completed twelve area changes in about half an hour on 2026-09-17. On 2026-09-30, eight real game instances each had seven native peer characters and completed four area changes led by the host and two different joiners. All eight reached each destination and rebuilt their peer characters. All seven joiners also damaged one shared enemy in a controlled eight-player fight: their accepted damage total exactly matched its native health loss. A fresh eight-player run also checked real hits from every joiner, a quiet period after combat stopped, quiet reconnect behavior, a fresh post-reconnect hit, and automatic return from a staged solo trip. Broader combat, story-skip mods and a full campaign at eight players still need checks. See [live evidence](research/limitations-live-2026-09-30.md). |
| Different mods on each side | **Works, with a catch** | The mod compares installs when you connect and tells you on screen what differs and what it turned off because of it. A very different save asks you to rejoin. Current development builds hash Override files, module archives, compatibility profiles, the base archive index and every referenced BIF, plus nested movie/audio/lip files. Same-size edits are detected. Known gameplay differences disable shared gameplay channels; base archive or media differences do this conservatively, even for cosmetic changes. Startup reads several GB, so it can take longer. Matching files do not certify a campaign. See [mod support](research/mod-support-framework.md) and [live audit](research/limitations-live-2026-09-30.md). |
| Wrong version of the mod on one side | **Works** | Refused in about a second with a clear message, instead of a silent timeout. |

## Moving around the world

| | Status | What to expect |
| --- | --- | --- |
| Walking through doors and area transitions together | **Works** | Either player can lead; the other follows through the same loading screen. `follow_lead = host` if you want only the host to lead. |
| Flying to another planet on the galaxy map | **Works** | Either player can choose the destination at the map. The other player is taken along automatically — take-off movie, cutscene and landing on both screens — and you both arrive in the same Ebon Hawk. If you set `follow_lead = host`, the joining player's own choice at the map is politely refused instead ("only the host can choose a destination") and nothing moves; verified 2026-09-17. |
| Choosing your two companions before leaving the ship | **Works** | The host picks. When the joining player reaches the departure screen, the request goes to the host, who sees "<name> wants to leave — choose your party" and picks normally. Watched on 2026-09-26: the joiner triggered the Ebon Hawk exit, the host picked on the real party screen, and both arrived on Dantooine. This checks that departure path; other story departures still need a playthrough. See [campaign evidence](review-seamless-2026-09-18.md). |
| Seeing each other | **Works** | Your partner is a real in-game character with their real face, armour and weapons, walking on the real floor, visible on the minimap and the map screen. |
| Companions following the party | **Works, with a catch** | Companions follow the host's game. A companion that got stuck behind (an old game bug) is now gathered back to the party automatically rather than left standing. A companion controlled by the *other* player doesn't move around on its own on your screen — it just moves exactly the way the other player's game says it does. |
| Fog-of-war map reveal | **Works (checked 2026-09-30)** | The map reveals around your partner. A stationary joiner's M-screen visibly revealed a new corridor as the host moved into a different exploration cell. See [map evidence](research/map-reveal.md). |
| Chests, doors, and "who opened it first" | **Works** | Two players clicking the same chest at once: exactly one opens it. Loot goes to one of you, never both. |
| A door locked or closed *again* by the story | **Works, with a catch** | Opening, closing, unlocking and relocking reached the other player through the native door engine in two consecutive live verification runs on the same game instances (2026-10-01). These were controlled interactions; a natural story script re-lock, such as the Sith base, still needs a playthrough check. See [live checks](research/limitations-live-2026-09-30.md) and [story routes](research/door-relock.md). |
| A chest or container locked or closed *again* by the story | **Works, with a catch** | Native opening, closing, unlocking and relocking reached the other player in two consecutive live verification runs on the same game instances (2026-10-01). The controlled checks used the real loot Close button and caught and fixed delayed snapshots overwriting an immediate local relock; a natural story script re-lock remains a separate route check. See [live checks](research/limitations-live-2026-09-30.md) and [story routes](research/door-relock.md). |
| The `warp` console command, used rapidly | **Not supported** | Chaining several `warp`s can crash the game. That is a bug in the original game, not the mod, and real doors don't trigger it. |

## Fighting

| | Status | What to expect |
| --- | --- | --- |
| Fighting the same enemies | **Works** | Enemies have one health bar that both of you damage. Kills, deaths and loot agree on both screens. This works for any number of enemies at once, from one boss to a room full of ambushers. |
| Which player an enemy goes after | **Works** | An enemy turns on whoever is hurting it and stays on them, rather than locking onto the host. If you both hit it about equally, it switches between you the way it would between two party members. It lets go of a player who is down. (`threat_retarget`, on by default.) |
| Really fighting, not just watching | **Works** | Each player's own game runs up to three fights at once for real (your own dice rolls, your own animations). Enemies beyond that are mirrored — your hits still land. |
| Enemies splitting attention between you | **Works, with a catch** | An enemy the host attacked first will turn on you once you've clearly out-damaged the host. The mod tracks damage separately for every connected partner and keeps re-checking the target. In a three-player live fight, the enemy first selected one joiner, then selected the other while the first remained down for 21.2 seconds. The living partner sustained qualifying threat for 10.6 seconds, with both living players more than six metres from the downed one. The game's own AI can still pull a target back to the host on the host's next swing; this check does not establish every natural fight. See [live evidence](research/limitations-live-2026-09-30.md). |
| Stun, slow, choke, heal and other effects | **Works, with a catch** | Twelve kinds of non-damage effect that land on your partner's stand-in reach their real body, in both directions. Heal and Cure now reach your partner too: a Cure you cast heals them for real (on by default, `partner_buffs`). A stun you cast on an enemy lands on the host's copy of that enemy as well, so it really stops acting (on by default, `enemy_effect_sync`). Catches: a medpac only ever heals the person using it, Speed is for the caster alone, and fear, push and stasis on enemies ride the same path as stun but have not each been watched. Only the host can equip companions (a joiner's companion equipment screen closes with a note); a joiner's medpac on a companion fighting for them counts on the host. |
| Companions in combat | **Works, with a catch** | A companion's equipment and experience stay in step on both sides. A companion fighting *for the other player specifically* (through their own copy of the game, in a fight away from the host) is off by default — set `companion_local_combat = 1` in `coop.ini` to turn it on; once on, damage it takes is real. |
| A mod reuses a companion slot | **Works, with a catch (checked 2026-10-01)** | Native recruitment identifies the exact replacement. An inactive BoS Shadow-to-Sera replacement reached the joiner. A separate controlled removal and re-add of Sera also triggered a fresh host save and automatic joiner reload with active Sera present, while preserving the joiner's own classes, items and Quick Save files. A further local replacement blocked stale host equipment updates. The full natural BoS route remains unverified. Saves with unproven nonstock companion identities keep those slots on hold. Current development code records identities after verified engine save completion and checks the exact selected bytes before restoring them on load. A real stock Load Game selection restored Sera after the co-op program was stopped during loading and reattached; missing and changed manifests kept her identity on hold. A save made without verified identity evidence still leaves that slot on hold. A large BoS return save also adopted over UDP fallback with classes and native equipment preserved. See [companion replacement evidence](research/bos-occupant-replacement-live-2026-10-01.md), [active adoption checks](research/bos-active-adoption-live-2026-10-01.md), and [controlled return and reload checks](research/bos-controlled-return-live-2026-10-01.md). |
| Going down, being revived, the party wipe | **Works (checked 2026-10-01)** | A downed player watches from a spectator camera and can be rescued by their partner (or a companion). After a full wipe, the host reloads and joiners recover automatically from their verified living checkpoints. Three real instances repeated the game-over and recovery sequence with different Jedi classes and ordinary GUI-equipped weapons preserved. Selecting New Game as a joiner leaves co-op and preserves character creation. See [live checks](research/limitations-live-2026-09-30.md). |
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
| The Star Forge's solo duels (Bastila, Malak) | **Works, with a catch** | The player not in the duel is parked and watches. A controlled live solo segment blocked two seconds of keyboard movement and released it after return. The natural Bastila and Malak duels still need playthrough checks. See [keyboard lock evidence](research/solo-keyboard-lock-live-2026-09-30.md). |

## The party, items and shops

| | Status | What to expect |
| --- | --- | --- |
| Your inventory | **Works — personal by default** | Your backpack is your own: what you pick up, buy, sell, drop or craft stays yours. Quest/plot items are the one exception — they still appear for both of you, so the story keeps working the same for both players. Set `inventory_mode = shared` in `coop.ini` to go back to one shared stash for everything (the older behaviour: pick something up on either side and it's in both inventories). If you disconnect and later rejoin (the same host or a different one), your personal backpack comes back with you — the mod remembers what you last had on YOUR OWN install and hands it back, rather than replacing it with whatever the host has. Nothing travels over the network for this; it's your own saved progress, the same way a single-player save would be. On a true first join (nothing saved yet), you simply start with an empty personal backpack plus whatever quest items the host's story already has. See [personal-inventory.md](research/personal-inventory.md) §12. |
| Switching which party member you control | **Not shared, by design** | Only the host can switch the active party member; the switch is mirrored to everyone. You get your own character back automatically when you connect. |
| Choosing the party before a departure | **Not shared, by design** | The "pick your party" screen before leaving an area is the host's. |
| Merchants | **Works, with a catch** | Shop stock is still shared: what one of you buys is gone for the other. Credits and the items you buy stay yours (see "Your credits" below) — `store_prices` in `coop.ini` (default 100 = retail prices) still applies to both of you, host-set, watched working on 2026-09-24 for both buying and selling. Two catches remain: the shared stock can only be *checked* while someone actually has the shop open (a quirk of the game), and an item a merchant has an infinite supply of never shows a change — which is harmless, since it's infinite. |
| Your credits | **Works** | Your credits are your own to earn and spend, separate from your partner's. A credits reward from a conversation pays EACH of you the full amount, not a split or a one-sided payout. Like your inventory, your personal credits come back with you across a disconnect/rejoin — your own last balance, not the host's purse and not reset to zero. Set `credits_mode = shared` in `coop.ini` (or `inventory_mode = shared`, which it follows) for the older one-purse behaviour, where spending, earning and quest rewards converge on the same number for both of you. See [personal-inventory.md](research/personal-inventory.md) §§4, 12. |
| Journal and quests | **Works** | Quest progress merges upward: whatever the furthest-along player has done, both have done. |
| Story flags an NPC remembers (e.g. "we've already talked") reaching the other player | **Works, with a catch** | Off by default — turn it on with `local_var_sync = 1` in `coop.ini`. Watched working on 2026-09-24: a conversation flag reached the other player, a late join caught up, and flags survived a world change. The check that combat-only flags stay private remains outstanding before enabling this by default. See [campaign evidence](review-seamless-2026-09-18.md). |

## Minigames

| | Status | What to expect |
| --- | --- | --- |
| Pazaak | **Works** | One player plays; the other is told they're waiting and is held out of the table until the hand ends. |
| Swoop racing | **Works, with a catch** | One player races; the other is held safely off the track with a "racing — waiting" message. Rewards reach everyone. A live check on the real track blocked two seconds of parked joiner keyboard movement and released it after return. Natural race entry, completion and scripted return still need playthrough checks. See [keyboard lock evidence](research/solo-keyboard-lock-live-2026-09-30.md). |

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

## Recent additions and story paths

The status in each row separates live checks from features that only have automated tests.
Some checks exercise one story scene or a staged route; they do not certify a whole campaign.

| | Status | What to expect |
| --- | --- | --- |
| Cutscenes and visions that the game plays between areas | **Works, with a catch** | Selected scripted cutscene routes run on both games. A staged ending entry on 2026-09-23 let both engines progress from STUNT_56a to STUNT_57 and enter credits without forcing the later hop. Credits were seen in a preceding run; visual confirmation with the default suppression setting remains open. The full earlier ending chain and the natural finale fights still need a playthrough. See [campaign evidence](review-seamless-2026-09-18.md). |
| Story-skip mods (Super Skip Taris, Jedi From The Start) | **Works, with a catch** | With identical mod files on every computer, Super Skip Taris's story settings, journal, credits and cutscene trip carried to a second player (2026-09-29). Jedi From The Start's class choice reaches the other player as their own Jedi prompt; a movie can delay it. Its changes to Trask and the lockers appeared after loading the host's post-choice save (2026-09-30). The automatic first-class saved-world refresh was checked with three real players choosing different classes and keeping their personal items; all three received the replacement Trask and matching refilled lockers. Other scripted object creation or removal is not mirrored automatically. The Endar Spire opening conversation cannot be saved, so the tested joining path starts afterwards. The stock turret fight was won through real shooting on native v86 (2026-09-30), with all six fighters sampled dead, an automatic scripted Hawk return, and both players responsive; this does not prove the complete mod skip sequence. Super Skip Taris has two-player evidence; the JFTS refresh has three-player evidence. Neither establishes an eight-player mod campaign. The JFTS detection markers were checked against archive `Jedi from the Start-1-4-0-0`; they do not certify other versions. See [story-skip evidence](research/story-skip-mods.md). |
| Becoming a Jedi on Dantooine | **Untested (redesigned 2026-09-25)** | Whoever talks to Master Dorak first picks their own path there, exactly like the single-player game. Every OTHER connected player who is not already a Jedi is then asked, on their OWN screen, to pick Guardian, Consular or Sentinel — nobody's choice is ever copied onto anybody else, so two players can end up on completely different paths. A player who joins later, or who was disconnected at the exact moment, is asked the next time they are in the world. The class opens at level 0, exactly as in single-player: each player then chooses their own Jedi feats and Force powers on their own level-up screen. On by default. Built and unit-tested; the private popup applied different Guardian, Consular and Sentinel choices on three real instances through Jedi From The Start on 2026-09-30, and Dorak's shipped terminal choices were watched host-led and joiner-led on 2026-09-22 with eligibility staged. That earlier pass used the previous class relay; the natural training prerequisites and current private-choice flow through Dorak still need a complete check. When one player finishes building their lightsaber with Master Zhar, every OTHER player can now get their OWN saber in their OWN class's colour too, instead of getting nothing — placed straight into their backpack, exactly where the game leaves the builder's own saber (nobody's saber equips itself automatically; each player equips their own by hand, once their character is experienced enough to use it, same as in single-player). On by default (`first_saber_color_sync` in `coop.ini`). A joiner-led construction gave the host its own class-coloured saber without duplication on 2026-09-25; the natural Dorak-to-Zhar route and the opposite direction still need checks. See [class and saber evidence](research/class-and-power-grants.md). Nobody ends up with two starter sabers, even after a reconnect or a late join. |
| Saving with your partner next to you | **Works** | The mod removes your partner's stand-in from the world for a moment while it saves, so it is never written into the save (watched working on a quicksave; the save-menu button uses the same path). It also cleans any such copy out of older saves when they load. |
| Fear, stun and sneaking | **Works, with a catch** | Fear and stun land on your partner's real character (watched working both ways), and a stun you put on an enemy holds on both screens. Poison is passed on and ticks on the other player once per interval. When your partner sneaks, their stand-in stops being a target for enemies on your screen (watched working with the game's own sneak toggle, 2026-09-25). By default their stand-in also turns translucent on your screen while they sneak, the same shimmer the game gives a real party member in stealth, instead of vanishing outright (`stealth_partner_visual` in `coop.ini`; set it to `hidden` for the old fully-invisible look, or `off` for no visual change at all). The translucent appearance was captured on screen on 2026-09-25. See [effect evidence](research/effect-sync.md). |
| Protection while your menu is open | **Works, with a catch** | Opening a full-screen menu (inventory, map, character sheet, journal, equipment, options) makes your stand-in on your partner's screen stop being a valid target and drops any hit that still lands on it, for up to 12 seconds. Closing the menu ends the protection immediately either way. Staying in a menu past 12 seconds costs you the shield for the rest of that visit, and starts a 20-second cooldown before it comes back, so it can't become a permanent way to duck a fight. On by default (`menu_guard_sync` in `coop.ini`). A live galaxy-map menu check on 2026-09-24 observed the stand-in becoming neutral, the 12-second cap expiring, the cooldown preventing an immediate repeat, and closing the menu restoring it. On 2026-10-01, an actual native hit about 1.2 seconds after opening the map was refused without changing damage counters or health. After the 12-second cap expired, a fresh hit applied exactly once. An actual hostile changing targets and the first opening frame still need direct checks. See [menu guard evidence](research/effect-sync.md#136-live-run-2026-09-24). |
| Equipping a companion as the joining player | **Works, with a catch (checked 2026-09-25)** | The host owns companion gear, so a joiner's companion Equipment screen closes with a note. A real T3 screen closed after 14 milliseconds while the joiner's own Equipment screen stayed open. Other companions and story configurations still need checks. See [companion evidence](research/companion-state-sync.md). |
| Typing an address in the game | **Works (checked 2026-09-30)** | The **Connect to an address...** row opens a working address box. Typing `localhost:27140` and confirming connected a real joiner after quitting its loaded world to the menu. A second quit and reconnect reused the remembered address successfully. Hostnames prefer IPv4 to match the session socket. See [live checks](research/limitations-live-2026-09-30.md). |
| Typing with a gamepad | **Untested** | The address box, the password box, chat and the `~` console can now all be typed into with an XInput pad: an on-screen character grid appears while one of them has focus (D-pad/stick to pick a letter, A to type it, B to back up, X for a space, Y or Start to send, Back to cancel, LB for upper case). Built and unit-tested (the grid's navigation and editing); nobody has typed with a real pad yet. |
| Leviathan escape, dark-side temple choice and Manaan return | **Works, with a catch** | These paths have code and narrower live checks, but their full natural story routes remain unverified. On the Leviathan, staged rescuer control and reload recovery worked, and each player retrieved gear from their own real locker; natural capture, item upgrades and the later save/rejoin mapping need further checks. At the temple, Jolee and Juhani left both parties and their hostile fights and deaths agreed; Bastila's recruitment still needs a suitable story save. Manaan keeps the joiner at the station during the host's underwater section; staged return and reunion worked, but the full underwater trip and combat recovery remain unverified. See [campaign evidence](review-seamless-2026-09-18.md). |

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
- Natural Swoop entry, completion and return, and the actual Star Forge duels. On 2026-09-30,
  staged live checks verified that two seconds of keyboard movement moved the parked joiner
  zero distance on the real Swoop track and during a controlled Star Forge solo segment.
  Movement resumed after explicit host returns. Those checks establish the locks, not the
  natural story sequences. See [keyboard lock evidence](research/solo-keyboard-lock-live-2026-09-30.md).
- Connection layouts beyond the measured 800x600, 1024x768 and 1280x960 sizes. On 2026-10-01,
  actual 1280x960 HUD and map buffers showed readable connection text without overlap.
  Requested 1280x720 and 1920x1080 settings fell back to retail 800x600 here, so those
  widescreen layouts remain unverified. The actual 1024x768 map also showed a newly
  revealed corridor while the joiner remained stationary. See [live checks](research/limitations-live-2026-09-30.md).
- Linux and Steam Deck (Proton): the whole thing, from the DLL loading under Wine to a session
  between a Deck and a Windows PC.
- A complete run past Taris. Dantooine's natural training prerequisites and current private class-choice flow, Leviathan rescue and gear
  return, the full Manaan sea-floor return, and the multi-hop
  ending still need playthrough proof. The stock Ebon Hawk turret ambush was won through real
  shooting on native v86 (2026-09-30): all six original fighters were sampled dead after 11.513 s,
  with 2700/3000 host HP; the game returned to the Hawk through its own script and both players
  moved afterwards. During the fight, the joiner's two-second W input moved zero distance with
  the native lock active; after return, the same input moved 1.04 units with the lock cleared.
  Class relay, brig detection, scripted airlock parking and the staged terminal credits
  leg have narrower live evidence; this does not establish a complete story path. Missing
  implementation and live checks are separated in the current table at the end of
  [review-seamless-2026-09-18.md](review-seamless-2026-09-18.md).
- Script changes outside the scoped JFTS refresh. Explicit `reconnect world` recovery passed live
  on both JFTS joiners, preserving their different classes and personal items; that does not prove
  every mod's scripts. The steps are in
  [PLAYTEST.md](PLAYTEST.md#story-skip-follow-up-checks-2026-09-30).
- A physical gamepad. The controller support was verified with a simulated one. On 2026-09-17 the
  mod was confirmed to find Windows' controller support on a real PC and to correctly report "no
  pad" for two Xbox pads that were paired but switched off — nobody was there to switch one on.

If you try one of these and it works — or doesn't — that's the most useful bug report there is.

[PLAYTEST.md](PLAYTEST.md) lists each one with the exact steps and what to look for; run `KotorCoop.Agent.exe logs` afterwards and attach the zip.
