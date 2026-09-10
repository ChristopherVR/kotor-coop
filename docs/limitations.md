# What works, what doesn't — in plain language (2026-09-10)

This page is for someone who wants to play KOTOR 1 from the Endar Spire to the Star Forge with a
friend, and wants to know what to expect before starting. No engine internals here; the
developer-facing version with the evidence behind every line is
[status-engineering.md](status-engineering.md).

**The short version:** two people can play the whole game together. You each control your own
character, the host's game is the "real" world, and the mod keeps the two copies in step — where
you are, who you're fighting, what's in the chests, which doors are open, how the story has
progressed. The things that *don't* work are mostly the game's own single-player assumptions that
the mod has chosen not to fight (one conversation at a time, one person drives the galaxy map) and
a couple of situations nobody has been able to test on real hardware yet.

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
| Bringing your own character | **Works** | Your character comes from your latest save and is placed next to the host. Your own save files are never changed. |
| Joining for the first time | **Works** | Connect, and the normal New Game character-creation screen opens; make your character. Shortly after you are in the host's world with it (mostly bounded by the Endar Spire's own opening cinematic, not by the mod), and that character is your co-op character on every join after (a single-player game on the same install is never mistaken for it). `join_mode = newcharacter` makes a new one; `join_mode = save` is the old bring-your-own-save way. Your starting weapon now reliably equips (live-verified fix, 2026-09-10, across several character classes). If you still see your character in their default/bare-looking appearance, that is very likely how KOTOR renders that class's premade look, not a missing item — check your inventory and equip anything unequipped by hand if something still looks wrong. If you joined on v0.9.0 and after upgrading you come up as the *host's* character, delete `saves\kotorcoop-character.pifo` and `.json` in your game folder once and join again (v0.9.0 could copy the host's character into that file when you quit; fixed). |
| Playing on two computers over the internet | **Works, with a catch** | The host's public address is found automatically and the router port is opened for you where the router allows it (UPnP). On a router that refuses, the host forwards one port by hand. Very restrictive networks (some mobile/corporate) can't be reached at all — there is no relay server. Watched working end-to-end over the public internet on 2026-09-10 using a tunnel tool (playit.gg) between two separate home networks; automatic port-opening on a real home router is still untested (see below). If your connection uses a UDP-only tunnel, turn on `world_transfer_udp_fallback = 1` in `coop.ini` — off by default — so the initial world hand-off doesn't silently stall on a TCP-only path. |
| GOG and Steam copies playing together | **Works** | Any mix. Two copies on one PC needs at least one of them to be the GOG version. |
| Saving and loading mid-session | **Works** | Either player can save and load. The other side notices and rejoins on its own; you don't need to reconnect. |
| Your partner quits to the main menu | **Works** | Their character disappears and a line on screen says they're away. When they load back in, they reappear. |
| The host's game crashes or drops off | **Works** | Within about six seconds you are released to keep playing solo, and the mod reconnects automatically when the host comes back. |
| Three or more players | **Works** | Up to eight, same rules. Long sessions with three players walking through many doors have not been stress-tested. |
| Different mods on each side | **Works, with a catch** | The mod compares installs when you connect and tells you on screen what differs and what it turned off because of it. A very different save asks you to rejoin. An item that has the same name but was changed by a mod on one side only is not detected. |
| Wrong version of the mod on one side | **Works** | Refused in about a second with a clear message, instead of a silent timeout. |

## Moving around the world

| | Status | What to expect |
| --- | --- | --- |
| Walking through doors and area transitions together | **Works** | Either player can lead; the other follows through the same loading screen. `follow_lead = host` if you want only the host to lead. |
| Flying to another planet on the galaxy map | **Works** | Either player can choose the destination at the map. The other player is taken along automatically — take-off movie, cutscene and landing on both screens — and you both arrive in the same Ebon Hawk. |
| Seeing each other | **Works** | Your partner is a real in-game character with their real face, armour and weapons, walking on the real floor, visible on the minimap and the map screen. |
| Companions following the party | **Works, with a catch** | Companions follow the host's game. A companion that got stuck behind (an old game bug) is now gathered back to the party automatically rather than left standing. A companion controlled by the *other* player doesn't move around on its own on your screen — it just moves exactly the way the other player's game says it does. |
| Fog-of-war map reveal | **Works, with a catch** | The map reveals for both. Whether the M-key map *draws* the shared reveal has never been checked on screen — only confirmed behind the scenes, not by watching it happen. |
| Chests, doors, and "who opened it first" | **Works** | Two players clicking the same chest at once: exactly one opens it. Loot goes to one of you, never both. |
| A door or chest locked *again* by the story | **Not shared, by design** | Doors and containers only ever move from locked to open in the shared world. If the game's own story re-locks one on the host's screen, the other player's copy stays open. Rare, and never blocks progress. |
| The `warp` console command, used rapidly | **Not supported** | Chaining several `warp`s can crash the game. That is a bug in the original game, not the mod, and real doors don't trigger it. |

## Fighting

| | Status | What to expect |
| --- | --- | --- |
| Fighting the same enemies | **Works** | Enemies have one health bar that both of you damage. Kills, deaths and loot agree on both screens. |
| Really fighting, not just watching | **Works** | Each player's own game runs up to three fights at once for real (your own dice rolls, your own animations). Enemies beyond that are mirrored — your hits still land. |
| Enemies splitting attention between you | **Works, with a catch** | An enemy the host attacked first will turn on you once you've clearly out-damaged the host. The game's own AI keeps pulling it back to the host, so in practice it alternates between you rather than sticking. With three or more players only one partner is tracked. |
| Stun, slow, choke, heal and other effects | **Works** | All twelve kinds of non-damage effect land on your partner's real body in both directions. |
| Companions in combat | **Works** | A companion can fight for the other player; damage it takes is real, and its equipment and experience stay in step on both sides. |
| Going down, being revived, the party wipe | **Works** | A downed player watches from a spectator camera and can be rescued by their partner (or a companion). If everyone falls, the host reloads and the other player rejoins on its own. |
| Experience and levelling | **Works** | Kill experience and story experience end up the same on both sides. |
| The Endar Spire / early Taris duels | **Not a mod limitation** | The game's own tutorial script stops anyone dropping below 1 hit point there, so nobody can be downed in those fights — co-op or not. |

## Talking to people

| | Status | What to expect |
| --- | --- | --- |
| Conversations | **Works, with a catch** | Only one conversation at a time. Whoever starts it chooses the replies; the other player watches it play out live (their clicks and skips are ignored) and sees the consequences — quests, rewards, recruits. Once you click the NPC to join, the subtitles catch up in well under a second — measured repeatedly, close range or far. If it still feels like a few seconds, that's normal walking time to reach the NPC (same as it would be solo) plus the game's own brief pause on the very first line of any conversation, not a co-op sync delay. This is by design: two people driving one dialogue at once would break the story. |
| Recruiting companions through dialogue | **Works** | When one of you recruits someone, the other gets them too. |
| Cutscenes | **Works** | They play on both screens. If a cutscene moves one player to a new area, the other follows. |
| The Star Forge's solo duels (Bastila, Malak) | **Works, with a catch** | The player not in the duel is parked and watches. We haven't been able to prove in testing whether that lock actually stops the keyboard from working — but nothing suggests it doesn't. |

## The party, items and shops

| | Status | What to expect |
| --- | --- | --- |
| The party's shared inventory | **Works** | One stash. Pick something up on either side and it's in both inventories; use or drop it and it's gone from both. |
| Switching which party member you control | **Not shared, by design** | Only the host can switch the active party member; the switch is mirrored to everyone. You get your own character back automatically when you connect. |
| Choosing the party before a departure | **Not shared, by design** | The "pick your party" screen before leaving an area is the host's. |
| Merchants | **Works, with a catch** | Stock and credits are shared: what one of you buys is gone for the other. Two catches: the shared stock can only be *checked* while someone actually has the shop open (a quirk of the game), and an item a merchant has an infinite supply of never shows a change — which is harmless, since it's infinite. |
| Credits | **Works** | One purse. Spending, earning and quest rewards converge on the same number. |
| Journal and quests | **Works** | Quest progress merges upward: whatever the furthest-along player has done, both have done. |

## Minigames

| | Status | What to expect |
| --- | --- | --- |
| Pazaak | **Works** | One player plays; the other is told they're waiting and is held out of the table until the hand ends. |
| Swoop racing | **Works, with a catch** | One player races; the other is held safely off the track with a "racing — waiting" message. Rewards reach everyone. Whether the hold physically stops keyboard input is the same untestable question as the duel lock above. |

## Controls and interface

| | Status | What to expect |
| --- | --- | --- |
| Keyboard and mouse | **Works** | Everything the game already does, plus `~` for the console and Enter to chat. |
| Gamepad / controller | **Works, with a catch** | Any Xbox-style (XInput) pad plays the whole game — walking, combat, every menu and panel — through your own key bindings. It can't type: the address/password box, chat and the `~` console need a keyboard. Layout in the README; `gamepad = 0` turns it off. |
| Seeing that you're connected | **Works** | A one-line status in the top-left, moved off the minimap on 2026-09-10 so it no longer overlaps it; confirmed on screen, and it fades out correctly when it's no longer needed. |
| Pausing | **Works** | Either player pauses both games. |

## Linux and Steam Deck

| | Status | What to expect |
| --- | --- | --- |
| Playing through Proton / Wine | **Should work — untested** | Everything is built to run inside the game's Proton prefix with nothing extra installed (the agent carries its own .NET). You add one launch option, `WINEDLLOVERRIDES="dinput8=n,b" %command%`, so Wine loads the mod's `dinput8.dll` instead of its own; the packaged README has the Steam Deck, Heroic and Lutris versions. Nobody has run it on a real Linux machine yet — the logs say `running under Wine` so a report tells us exactly how far it got. |
| The Deck's built-in controls | **Should work — untested** | They reach the game as an Xbox controller, which the controller support above picks up. Typing (address, chat, console) needs the on-screen keyboard. |

---

## Things that are *not* limitations

A few things look like bugs and aren't:

- **Your partner appears for a moment as a stranger, or as Trask, right after a warp.** This was a
  real bug, fixed in August 2026, and made much faster to recover from on 2026-09-10 (it could
  briefly persist far longer than expected before that). If you still see it, it's worth
  reporting.
- **"Another modal screen is up" when a shop won't open for the other player.** Fixed the same way the
  galaxy-map follow was fixed (September 2026). Also worth reporting if you still see it.
- **You can't click on a companion to talk to them right after joining.** Shares its root cause
  with the stranger/Trask portrait bug above and should be much rarer after the 2026-09-10 fix.
  Worth reporting if it still happens.
- **Nobody can be knocked out on the Endar Spire.** The game's own tutorial rule.
- **Old `coop.ini` files.** The installer never overwrites your settings file, so an install from
  before a default changed keeps its old value. If a feature this page says is on seems off, run
  `install.ps1 -GameDir '...' -UpdateDefaults`, or compare against `profiles/coop.ini.example`.

## Still untested on real hardware

Built and automatically tested, but nobody has watched them on the running game yet:

- Internet play through a real home router's own automatic port-opening (UPnP). Tunnel-based
  internet play (e.g. playit.gg) has been watched working end-to-end, which is a different path.
- Long three-player sessions crossing many doors.
- The keyboard side of the "you're parked" locks (swoop, Star Forge duels) — injected keystrokes
  never reach this game, so it can't be tested without a person at the keyboard.
- The exact on-screen look of the connection line and of the shared map reveal.
- Refusing the other player's galaxy-map choice when `follow_lead = host` is set (the default lets anyone
  choose, and that path is verified).
- Linux and Steam Deck (Proton): the whole thing, from the DLL loading under Wine to a session
  between a Deck and a Windows PC.
- A physical gamepad (the controller support was verified with a simulated one).

If you try one of these and it works — or doesn't — that's the most useful bug report there is.
