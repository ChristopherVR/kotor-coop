"""Two-player co-op playtest harness: both players act, a referee compares the two games.

Each step, the host and then the joiner get a list of legal actions built from their own
game's state (dialogue replies with their text, hostiles, containers, NPCs, doors, following
the partner). A policy picks one: Laya through its MCP server (--policy laya), or fixed rules
(--policy rules). Actions go through the plugin's console mailbox and real input verbs only;
no warps, teleports, spawns or save edits (laya_playtest.Game enforces the allowlist).

After every step the referee compares the two games: process alive, same area, still
connected, a conversation join that took, the same living creatures, and the state the mod
promises to keep in step (each with its own settle window, see SETTLE): door and container
open/locked state, enemy health (one shared bar) and dead/alive, the host's companions present
on the joiner, each game's stand-in for the other player showing the peer's health, the party
journal, the story globals (booleans and numbers) and the player's experience. Inventory and
credits are personal by default and are never compared. A difference that outlives its
settle window becomes a finding with front-buffer screenshots from both games.
New crash/exception lines in either plugin log are findings too. The referee owns PASS/FAIL;
the policy only decides what to do next.

  python tools/coop_playtest.py --host <pid> --client <pid> --policy laya --minutes 20
      --output <new evidence directory>

--policy claude pursues quests: the brain is Claude Code run headless (`claude -p --model sonnet
--output-format json`, prompt on stdin, no tools; only Read when --claude-screenshots attaches a
picture). It is asked only for real decisions: two or more options that are not popups, menus,
forced steps, continue/wait or a plain fight with hostiles near; everything routine stays with the
rules. Each call gets the goal, the active journal quests with their current entry text (global.jrl
+ dialog.tlk via kotor_lore), the area name, party, nearby named characters, the last 12 decisions
with outcomes, the PLAN it returned last time, and the options. Identical situations reuse the last
answer (or the next untried option); --claude-budget N (default 150) caps calls, after which rules
take over and report.json says so. Every call (prompt size, latency, choice, reason, cost) is in
report.json under "claude". A reply that is not JSON, or names a key not offered, falls back to rules.
Quest-useful actions: talk options carry the character's name, and gotalk_<id> walks the walkmesh to
a named character out of talking range, then talks.

Needs the isolated rig (tools/demo.ps1 -Isolated) so injected input reaches unfocused windows.
"""
from __future__ import annotations

import argparse
import asyncio
import collections
import itertools
import json
import math
import os
import random
import re
import shutil
import subprocess
import tempfile
import time
import tomllib
from pathlib import Path

from laya_playtest import REPO, Game, MemoryError

try:
    import kotor_lore
except Exception:  # lore is context for the prompt, never a requirement
    kotor_lore = None

GOAL = ("Play Star Wars: Knights of the Old Republic co-op and make story progress: finish "
        "conversations choosing replies that move the story forward, defeat hostile enemies, "
        "search containers, talk to new characters, then explore doors into new areas. "
        "Prefer actions not tried before. Choose only one of the listed actions.")

DIK_W, DIK_A, DIK_D, DIK_SPACE = 17, 30, 32, 57  # W/A/D only released on exit
COOP_TAG = re.compile(r"^coop", re.IGNORECASE)

# Settle windows: how long a cross-game difference may last before it is a finding.
SETTLE = {"module": 45.0, "disconnected": 15.0, "census": 25.0, "join": 15.0, "stuckdialogue": 120.0,
          "door": 20.0, "container": 20.0, "enemyhp": 20.0, "party": 30.0, "peerhp": 15.0,
          "journal": 30.0, "globals": 30.0, "xp": 30.0}
MAX_PER_KIND = 5  # findings of one comparison kind per run; the rest repeat the same bug
LOG_ALARM = re.compile(r"unhandled|exception|crash record|access violation|fail-fast", re.IGNORECASE)
LOG_IGNORE = re.compile(r"FIRST-CHANCE exception \(0x406D1388\)|unhandled-exception hook installed"
                        r"|crash probe|managed faults now reach")


# Retail 800x600 GUI positions, measured on the rig (2026-10-08). The panel names come from
# laya_playtest's active-panel list, so a click is only sent while its panel is up.
OK_STATUS_SUMMARY = "input click 399 316"     # "Experience Points Received" and similar
OK_MESSAGE_BOX = "input click 399 329"        # "This character must be a member of your party..."
# Both popups grow with their text, so the OK button moves down: a one-line notice has it at
# y=316, a two-line one ("Journal Entry Added" + "Dark Side Points Gained") at about 333.
# Repeated attempts on a popup that is still up walk through these rows.
OK_ROWS = {"status_summary": (316, 333, 350, 300, 366), "message_box": (329, 345, 312, 362, 296)}


def ok_click(panel, attempt):
    rows = OK_ROWS[panel]
    return f"input click 399 {rows[attempt % len(rows)]}"


# KOTOR 1 experience for each level: level n needs n(n-1)/2 * 1000.
LEVEL_XP = [n * (n - 1) // 2 * 1000 for n in range(1, 21)]
NEVER = 10 ** 12
# Within this the attack verb's own melee snap is a step, not a teleport (AttackMeleeRange 1.5 m).
MELEE_REACH = 2.5


def next_level_xp(xp):
    """The experience at which the next level becomes available above `xp`. Past level 20
    there is none: a veteran joiner carrying millions of XP must not be offered it forever."""
    return next((t for t in LEVEL_XP if t > xp), NEVER)
CONTAINER_GET, CONTAINER_CLOSE = "input click 399 391", "input click 399 441"
PARTY_FIRST, PARTY_ADD, PARTY_OK = "input click 176 210", "input click 270 449", "input click 398 485"
HUD_EQUIP = "input click 565 20"              # the Equipment icon, top right of the HUD
EQUIP_RIGHT_WEAPON, EQUIP_BODY = "input click 370 325", "input click 320 275"
EQUIP_SECOND_ROW, EQUIP_OK = "input click 550 263", "input click 400 485"
HUD_SECOND_PORTRAIT = "input click 94 577"    # the first companion's portrait; click again to return
HUD_CHARACTER = "input click 625 20"          # the Character icon, top right of the HUD
CHARACTER_AUTO_LEVEL = "input click 268 297"  # Character screen "Auto Level Up"; its confirm is a message box


UPGRADE_STATIONS = re.compile(r"workbench|lab ?station|labstation", re.IGNORECASE)

# Full-screen in-game menus (and the pause banner) that take the keyboard away from a
# conversation underneath them.
OVER_DIALOGUE = {"menu", "equip", "inventory", "character", "abilities", "messages", "journal", "map",
                 "options", "pause"}


def menu_actions(panels, tried):
    """A menu owns the input; nothing else can happen until it closes."""
    if "status_summary" in panels:
        return {"ok_summary": {"description": "Dismiss the experience/status notice (OK)",
                               "commands": [ok_click("status_summary", tried.get("ok:status_summary", 0))],
                               "ok": "status_summary"}}
    if "message_box" in panels:
        return {"ok_message": {"description": "Dismiss the message box (OK)",
                               "commands": [ok_click("message_box", tried.get("ok:message_box", 0))],
                               "ok": "message_box"}}
    if "party_selection" in panels:
        # Add a companion first: OK on an empty party is accepted, and the Taris runs played the
        # whole Upper City solo with Carth left waiting on the selection screen (2026-10-09).
        if tried.get("party_ok", 0) < 1:
            return {"party_add": {"description": "Add the first party member, then accept (OK)",
                                  "commands": [PARTY_FIRST, PARTY_ADD, PARTY_OK], "gap": 1.2}}
        return {"party_ok": {"description": "Accept the party selection (OK)", "commands": [PARTY_OK]}}
    if "container" in panels:
        # Take first, and only then close: a policy that closes a full container skips the
        # gear the story expects (Endar Spire: Trask waits for the footlocker's weapon).
        if not tried.get("took"):
            return {"take_items": {"description": "Take the items from the open container",
                                   "commands": [CONTAINER_GET], "took": True}}
        return {"close_menu": {"description": "Close the container", "commands": [CONTAINER_CLOSE]}}
    if "pause" in panels and not (set(panels) & (OVER_DIALOGUE - {"pause"})):
        # A held press: while paused, a one-frame injected tap is not read (seen live 2026-10-09).
        return {"unpause": {"description": "The game is paused: press the pause key (Space)",
                            "commands": [f"input key {DIK_SPACE} down", f"input key {DIK_SPACE} up"], "gap": 0.25}}
    # Anything else (equipment, options, an unnamed popup) closes through HideSWInGameGui,
    # which leaves both games running.
    return {"close_menu": {"description": f"Close the open menu ({', '.join(panels)})", "commands": ["closegui"]}}


# Story gates a first-time player learns from the scene but the policy cannot see (the harness
# tests co-op, not puzzle solving). Read from the game data: the Lower City elevator guard's
# script (k_ptar_strgrd_en) passes a PC holding ptar_sithpapers or a party member wearing
# ptar_sitharmor, and tar02_sithcomm in Upper City North carries the armor.
_TARIS_UPPER = ("To reach the Lower City (the elevator guarded by a Sith in Upper City North, tar_m02ab) you "
                "need Sith papers or Sith armor. The Sith commander harassing the alien in tar_m02ad carries "
                "Sith armor: fight him and his troopers, search the body, equip the armor, then talk to the "
                "elevator guard. Take Carth into the party when the party screen offers him.")
CAMPAIGN_HINTS = {
    "tar_m02af": "Talk to Carth when he speaks to you; take him into the party when the party screen opens.",
    "tar_m02aa": _TARIS_UPPER, "tar_m02ab": _TARIS_UPPER, "tar_m02ac": _TARIS_UPPER, "tar_m02ad": _TARIS_UPPER,
}


def walk_actions(me, nav, tried, closed_doors):
    """Walking a player would do, on walkmesh routes that avoid closed doors: up to a closed
    door that is out of reach, and into rooms not visited yet."""
    acts = {}
    if nav is None:
        return acts
    here = tuple(me["position"][:2])
    # Doors into another area first: the story goes on through one, and a far exit lost to
    # four nearer side doors kept a campaign circling Taris's Upper City (2026-10-09: the Lower
    # City door tar02_lowdoor was never offered).
    for o in sorted(closed_doors, key=lambda o: (not o.get("destination"), o["distance"]))[:4]:
        k = f"{me['module']}:{o['id']}:goto"
        if o["distance"] <= 4 or tried.get(k, 0) >= 2:
            continue
        others = [tuple(d["position"][:2]) for d in closed_doors if d["id"] != o["id"]]
        route = nav.path(here, tuple(o["position"][:2]), blocked=others)
        if route and len(route) >= 2:
            last = route[-2]
            door = tuple(o["position"][:2])
            gap = math.dist(last, door)
            stop = door if gap < 2 else (door[0] + (last[0] - door[0]) * 2 / gap, door[1] + (last[1] - door[1]) * 2 / gap)
            acts[f"goto_{o['id']}"] = {"description": f"Walk to the door {o['tag']} ({o['distance']:.0f} m away)",
                                       "walk": route[:-1] + [stop], "commands": [], "target": k}
    blocked = [tuple(d["position"][:2]) for d in closed_doors]
    visited = tried.setdefault("visited", set())
    for name, centre in sorted(nav.rooms.items(), key=lambda kv: math.dist(kv[1], here)):
        if name in visited or tried.get(f"explore:{name}", 0) >= 2:
            continue
        route = nav.path(here, centre, blocked=blocked)
        if route:
            acts[f"explore_{name}"] = {"description": f"Explore the area {name} ({math.dist(here, centre):.0f} m away)",
                                       "walk": route, "commands": [], "target": f"explore:{name}"}
            if len([a for a in acts if a.startswith("explore_")]) >= 2:
                break
    return acts


def who_is(module, tag):
    """'Trask (end_trask)': the name a player sees, then the tag the commands use."""
    name = ""
    if kotor_lore is not None:
        try:
            name = kotor_lore.creature_name(module, tag)
        except Exception:
            pass
    return f"{name} ({tag})" if name and name.lower() != (tag or "").lower() else (tag or "someone")


def what_is(module, tag):
    """'Footlocker (end_locker01), holds items': the name a player sees, whether it is a
    container, then the tag. A raw tag lost to a dozen door options on the Endar Spire; Trask
    only says out loud that the gear is in the footlocker."""
    name, holds = "", False
    if kotor_lore is not None:
        try:
            name, holds = kotor_lore.placeable_name(module, tag)
        except Exception:
            pass
    label = f"{name} ({tag})" if name and name.lower() != (tag or "").lower() else (tag or "object")
    return label + (", holds items" if holds else "")


def npc_walk_actions(me, nav, tried, closed_doors, living):
    """Walk up to a named non-hostile character that is out of talking range, on a walkmesh route
    that avoids closed doors, then talk to them. Quests are mostly people, and the nearby-only
    talk option never reaches the one across the room."""
    acts = {}
    if nav is None or not me.get("position"):
        return acts
    here = tuple(me["position"][:2])
    blocked = [tuple(d["position"][:2]) for d in closed_doors]
    far = [o for o in living if o["faction"] != 1 and 12 <= o["distance"] <= 80
           and re.fullmatch(r"[A-Za-z0-9_]{1,31}", o["tag"] or "")
           and tried.get(f"gotalk:{me['module']}:{o['id']}", 0) < 2 and tried.get(f"talk:{me['module']}:{o['id']}", 0) < 3]
    for o in sorted(far, key=lambda o: o["distance"])[:6]:
        target = tuple(o["position"][:2])
        route = nav.path(here, target, blocked=blocked)
        if not route or len(route) < 2:
            continue
        last, gap = route[-2], math.dist(route[-2], target)
        stop = target if gap < 2 else (target[0] + (last[0] - target[0]) * 2 / gap, target[1] + (last[1] - target[1]) * 2 / gap)
        acts[f"gotalk_{o['id']}"] = {"description": f"Walk to {who_is(me['module'], o['tag'])} ({o['distance']:.0f} m away) and talk",
                                     "walk": route[:-1] + [stop], "commands": [f"talktag {o['tag']}"],
                                     "count": f"gotalk:{me['module']}:{o['id']}"}
        if len(acts) >= 2:
            break
    return acts


_NAV = {}


def nav_for(module):
    """The walkmesh of a module, loaded once; None when its files cannot be read."""
    if module not in _NAV:
        try:
            import kotor_nav
            _NAV[module] = kotor_nav.Area.load(module)
        except Exception:
            _NAV[module] = None
    return _NAV[module]


def actions_for(role, me, peer, tried, nav=None):
    """Legal actions for one player, most useful first. role is 'host' or 'joiner'."""
    if me.get("loading") or me.get("hp", 1) <= 0:
        return {}
    acts = {}
    key = lambda o: f"{me['module']}:{o['id']}"
    fresh = lambda o, limit: tried.get(key(o), 0) < limit

    panels = me.get("panels") or []
    if ((me.get("menu_open") and not me["conversation"]) or {"status_summary", "message_box"} & set(panels)
            or OVER_DIALOGUE & set(panels)):
        # Notices sit on top of a conversation too (XP mid-dialogue) and block it until dismissed.
        # So does a full-screen menu: on 2026-10-08 the joiner's character sheet (a level_up
        # that had nothing to level) sat over a bark for 13 minutes while "continue" pressed
        # Space under it, and Space under a menu toggles PAUSE, which froze the bark.
        return menu_actions(panels, tried)

    if me["conversation"] and UPGRADE_STATIONS.search(me.get("partner_tag") or ""):
        return {"close_menu": {"description": "Leave the upgrade screen", "commands": ["closegui"]}}

    if me["conversation"]:
        if me["reply_count"]:
            texts = me.get("reply_texts") or []
            for i in range(me["reply_count"]):
                said = texts[i] if i < len(texts) else "(reply text unavailable)"
                # Loop guard, per conversation NODE: a reply picked twice on the same node is not
                # offered there again. A tutorial question loops back to itself, and a
                # spectator's picks are swallowed by the mod (only the owner answers). Keyed by
                # node, because the same words ("Okay.") are the right answer on many nodes.
                said_key = f"said:{me.get('partner_tag')}:{me.get('dialogue_node')}:{said}"
                if tried.get(said_key, 0) >= 2:
                    continue
                acts[f"reply_{i + 1}"] = {"description": f'Answer: "{said}"', "commands": [f"input key {i + 2} tap"],
                                          "said": said_key}
            if not acts:
                acts["wait"] = {"description": "Wait: the conversation is not yours to answer, or every answer was tried",
                                "commands": []}
        else:
            acts["continue"] = {"description": "Continue the conversation (Space)", "commands": [f"input key {DIK_SPACE} tap"]}
        return acts

    # A player joins by clicking the same character. A cutscene owned by an invisible scene
    # object (end01_sceneobj02) has nobody to click, and auto-follow brings the player in anyway;
    # a late "join" there restarted the whole cutscene as a second, stalled run (2026-10-09).
    partner = (me.get("peer_partner_tag") or "").lower()
    clickable = any(o["kind"] == "creature" and (o.get("tag") or "").lower() == partner and o.get("hp", 0) > 0
                    for o in me["objects"])
    if peer.get("conversation") and partner and clickable and tried.get("join:" + me["peer_partner_tag"], 0) < 2:
        tag = me["peer_partner_tag"]
        acts[f"join_{tag}"] = {"description": f"Join your partner's conversation with {tag}",
                               "commands": [f"talktag {tag}"], "join": tag}

    objs = me["objects"]
    living = [o for o in objs if o["kind"] == "creature" and o["hp"] > 0 and not o["party"] and not COOP_TAG.match(o["tag"])]
    # Nearby only: an attack order runs the creature anywhere in the area (seen from 74 m),
    # which skips the route a player walks and can break the story's order (killing the
    # bridge's Sith from outside sealed the Endar Spire bridge with the player locked out).
    # Walking (explore_/goto_) brings the player to the next fight instead.
    # The `attack` verb snaps the attacker to melee range first (CheatController.Attack), a
    # staging teleport: on 2026-10-08 it put the host 25 m away inside the Endar Spire's sealed
    # cutscene room, behind a window, and the engine's pathing faulted (0x58063B). A player walks
    # to the enemy, so the harness does too: within reach it just attacks; farther off it walks
    # the walkmesh route (closed doors blocked) to just short of the enemy, then attacks. No
    # route, no fight.
    blocked_doors = [tuple(d["position"][:2]) for d in objs if d["kind"] == "door" and not d["open"]]
    for o in sorted([o for o in living if o["faction"] == 1 and o["distance"] < 30 and fresh(o, 4)],
                    key=lambda o: o["distance"])[:2]:
        attack = {"description": f"Attack the hostile {o['tag']} {o['distance']:.0f} m away",
                  "commands": [f"attack 0x{o['id']:X}"], "target": key(o), "reach": (o["id"], MELEE_REACH + 1.5)}
        if o["distance"] > MELEE_REACH:
            route = nav.path(tuple(me["position"][:2]), tuple(o["position"][:2]), blocked=blocked_doors) if nav else None
            if not route or len(route) < 2:
                continue
            last, foe = route[-2], tuple(o["position"][:2])
            gap = math.dist(last, foe)
            stop = last if gap <= 1.2 else (foe[0] + (last[0] - foe[0]) * 1.2 / gap, foe[1] + (last[1] - foe[1]) * 1.2 / gap)
            attack["walk"] = route[:-1] + [stop]
            attack["description"] = f"Walk to and attack the hostile {o['tag']} {o['distance']:.0f} m away"
        acts[f"fight_{o['id']}"] = attack
    who = me.get("controlled_tag") or "pc"
    if tried.get("took_any") and not any(o["faction"] == 1 and o["distance"] < 25 for o in living)             and tried.get(f"equip:{who}", 0) < 1:
        # Retail clicks only: the HUD icon, the right-weapon slot, the first item under None,
        # OK; the same for the body slot; then close. Offered once per character, and again
        # after new items are taken from a container.
        acts["equip"] = {"description": "Equip your gear: a weapon and armor (Equipment screen)",
                         "commands": [HUD_EQUIP, EQUIP_RIGHT_WEAPON, EQUIP_SECOND_ROW, EQUIP_OK,
                                      EQUIP_BODY, EQUIP_SECOND_ROW, EQUIP_OK, "closegui"],
                         "gap": 1.2, "equip": who}

    level_gate = tried.get(f"level:{who}")
    xp = me.get("xp")
    level_ready = level_gate is None or (xp is not None and xp >= level_gate)
    if level_ready and not any(o["faction"] == 1 and o["distance"] < 25 for o in living):
        # Retail Auto Level Up. Offered again after new experience (an XP notice, a fight); when
        # the character cannot level the button does nothing and the screen is closed again.
        # The Endar Spire's bridge door refuses a character with a pending level.
        acts["level_up"] = {"description": "Level up if you can (Character screen, Auto Level Up); "
                            "some doors refuse a character with a level waiting",
                            "commands": [HUD_CHARACTER, CHARACTER_AUTO_LEVEL], "gap": 1.5, "level": who}
    # A workbench or lab station opens the upgrade screen, which the harness cannot drive; it sat
    # two minutes in one on Taris (2026-10-09).
    for o in [o for o in objs if o["kind"] == "placeable" and o["distance"] < 12 and fresh(o, 1)
              and not UPGRADE_STATIONS.search(f"{o.get('tag') or ''} {what_is(me['module'], o.get('tag'))}")][:2]:
        acts[f"search_{o['id']}"] = {"description": f"Search the {what_is(me['module'], o['tag'])} {o['distance']:.0f} m away",
                                     "commands": [f"useplaceable 0x{o['id']:X}"], "target": key(o)}
    for o in [o for o in living if role == "host" and o["faction"] != 1 and o["distance"] < 12 and tried.get("talk:" + key(o), 0) < 3
              and re.fullmatch(r"[A-Za-z0-9_]{1,31}", o["tag"] or "")][:2]:
        acts[f"talk_{o['id']}"] = {"description": f"Talk to {who_is(me['module'], o['tag'])}", "commands": [f"talktag {o['tag']}"],
                                   "target": "talk:" + key(o)}

    if role == "joiner":
        if peer.get("module") and peer["module"] != me["module"]:
            return acts  # the transition follow brings us; nothing to walk to
        if tried.get("follow", 0) < 1:
            acts["follow"] = {"description": "Follow your partner", "commands": ["autofollow"]}
        return acts

    # `usedoor <id>` queues the engine's own pathfinding move (around walls, unlike drive keys)
    # and, for an ordinary door, the native click path, so story locks still apply. Tries are
    # counted per controlled character: a door the player could not open is fresh again for a
    # companion (the Endar Spire's lockdown door needs Trask).
    key = lambda o: f"{me['module']}:{o['id']}:{who}"
    fresh = lambda o, limit: tried.get(key(o), 0) < limit
    doors = [o for o in objs if o["kind"] == "door" and not o.get("static") and fresh(o, 2)
             and not (o["open"] and not o["destination"])]
    # Open transitions first (the way on), then nearest; five, so dead-end story doors next to
    # the player do not crowd out the exit (Endar Spire: end_door10 behind four sealed doors).
    for o in sorted(doors, key=lambda o: (not (o["destination"] and o["open"]), o["distance"]))[:5]:
        if o["distance"] > 95:
            # usedoor by id reaches 100 m (DoorUse.MaxUseDistanceById) and walkto never moves the
            # player character in this build (TestDriveController.HandleWalk). A far LOCKED door
            # can still take Security: the server walks the creature over itself (80 m).
            if o.get("locked") and o["distance"] <= 78 and tried.get(key(o) + ":unlock", 0) < 2:
                acts[f"unlock_{o['id']}"] = {"description": f"Use your Security skill on the locked door {o['tag']} "
                                             f"({o['distance']:.0f} m away)", "commands": [f"unlockdoor 0x{o['id']:X}"],
                                             "target": key(o) + ":unlock"}
            continue
        if o.get("locked"):
            # Plain opening first: on a locked door it runs the door's own script (the Endar
            # Spire lockdown door hands over to Trask). Then Security, as the target menu sends it.
            acts[f"open_{o['id']}"] = {"description": f"Try to open the locked door {o['tag']} ({o['distance']:.0f} m away)",
                                       "commands": [f"usedoor 0x{o['id']:X}"], "target": key(o)}
            if tried.get(key(o) + ":unlock", 0) < 2:
                acts[f"unlock_{o['id']}"] = {"description": f"Use your Security skill on the locked door {o['tag']} "
                                             f"({o['distance']:.0f} m away)", "commands": [f"unlockdoor 0x{o['id']:X}"],
                                             "target": key(o) + ":unlock"}
        elif o["destination"]:
            acts[f"exit_{o['id']}"] = {"description": f"Go through the door {o['tag']} into {o['destination']} "
                                       f"({o['distance']:.0f} m away)", "commands": [f"usedoor 0x{o['id']:X}"], "target": key(o)}
        else:
            acts[f"open_{o['id']}"] = {"description": f"Go to and open the door {o['tag']} ({o['distance']:.0f} m away)",
                                       "commands": [f"usedoor 0x{o['id']:X}"], "target": key(o)}

    if who == "pc":
        closed = [o for o in objs if o["kind"] == "door" and not o["open"]]
        # An open exit beyond usedoor's reach is walked to as well.
        far_exits = [o for o in objs if o["kind"] == "door" and o["open"] and o.get("destination")
                     and o["distance"] > 95 and not o.get("static")]
        acts |= walk_actions(me, nav, tried, closed + far_exits)
        acts |= npc_walk_actions(me, nav, tried, closed, living)

    # Only the host switches the controlled party member (party_switch_host_only). The HUD's
    # second portrait is the first companion while the PC leads, and the PC while a companion
    # leads, so one click swaps either way.
    #
    # Offered only when it can help: a nearby closed door the player character already failed
    # on. Once swapped, the way back is offered only after the companion has tried something,
    # or a policy flips straight back (seen with Laya on the Endar Spire lockdown door).
    companions = [o for o in objs if o["kind"] == "creature" and o["party"] and o["hp"] > 0
                  and o["id"] != me.get("player_id") and not COOP_TAG.match(o["tag"])]
    if who != "pc":
        if tried.get("acted_as_companion", 0) >= 2 or not acts:
            acts["control_pc"] = {"description": "Switch control back to your own character",
                                  "commands": [HUD_SECOND_PORTRAIT], "swap": True}
    elif companions and tried.get("swaps", 0) < 6:
        failed_here = [o for o in objs if o["kind"] == "door" and not o["open"] and o["distance"] < 15
                       and tried.get(f"{me['module']}:{o['id']}:pc", 0) >= 1]
        if failed_here:
            c = companions[0]
            acts = {f"control_{c['tag']}": {"description": f"Switch control to your companion {c['tag']}: the door "
                                            f"{failed_here[0]['tag']} would not open for you, and some locked doors "
                                            "need a companion", "commands": [HUD_SECOND_PORTRAIT], "swap": True}} | acts
    if who != "pc":
        # A companion is in control for one reason: the doors. Keep its options to those.
        acts = {k: a for k, a in acts.items() if k.startswith(("open_", "exit_", "approach_", "unlock_", "control_pc"))} or acts
    return acts


def refresh_doors(tried, me):
    """Forget door, swap and level attempts so they are offered again: story beats (a scene, a
    level, a recruit) unlock doors that refused earlier. Talks keep their own budget."""
    # Door keys are module:id:who[:unlock]; container keys (module:id) stay spent, or a
    # refresh sends a player round the same emptied boxes forever.
    prefix = f"{me.get('module')}:"
    for k in [k for k in tried if k.startswith(prefix) and k.count(":") >= 2]:
        del tried[k]
    tried.pop("swaps", None)


def summary(role, me, peer):
    """What the policy is told about one player's situation."""
    if me.get("loading"):
        return f"{role}: loading"
    hostile = sum(1 for o in me["objects"] if o["kind"] == "creature" and o["faction"] == 1 and o["hp"] > 0 and not o["party"])
    party = [o["tag"] for o in me["objects"] if o["kind"] == "creature" and o["party"] and o["tag"] and not COOP_TAG.match(o["tag"])]
    who = me.get("controlled_tag") or "your own character"
    text = (f"You are the {role} in area {me['module']}, controlling {who}, health {me['hp']}, "
            f"{hostile} hostile(s) nearby, companions: {', '.join(party) or 'none'}. "
            f"Partner is in {peer.get('module', 'unknown')}" + (", in a conversation" if peer.get("conversation") else "") + ".")
    if me["conversation"]:
        text += " You are in a conversation."
    if me.get("menu_open"):
        text += f" A menu is open: {', '.join(me.get('panels') or [])}."
    return text


class RulesPolicy:
    name = "rules"

    def __init__(self, seed):
        self.rng = random.Random(seed)

    async def choose(self, role, text, acts, ctx=None):
        replies = [k for k, a in acts.items() if k.startswith("reply_") and "repeat" not in a["description"].lower()]
        if replies:
            return self.rng.choice(replies), {"source": "rules"}
        return next(iter(acts)), {"source": "rules"}


class LayaPolicy:
    name = "laya"

    def __init__(self, session, confidence, fallback):
        self.session, self.confidence, self.fallback = session, confidence, fallback

    async def choose(self, role, text, acts, ctx=None):
        if len(acts) == 1:
            return next(iter(acts)), {"source": "forced"}
        result = await self.session.call_tool("laya_predict", {
            "state": {"situation": text}, "model": "english", "min_confidence": self.confidence,
            "questions": {"action": {"type": "choice", "instructions": GOAL,
                                     "criteria": {k: a["description"] for k, a in acts.items()}}}})
        data = [json.loads(c.text) for c in result.content if c.type == "text"]
        answer = (data[0] if data else {}).get("answers", {}).get("action", {})
        choice = answer.get("choice")
        if answer.get("abstention") == "passed" and choice in acts:
            return choice, {"source": "laya", "confidence": answer.get("confidence")}
        picked, _ = await self.fallback.choose(role, text, acts)
        return picked, {"source": "fallback", "laya_choice": choice, "abstention": answer.get("abstention")}


CLAUDE_GOAL = ("Goal: play KOTOR 1 as a co-op party and advance the main story and the active quests. Prefer the next "
               "concrete objective in the journal; talk to quest-relevant characters; pick dialogue replies that "
               "advance quests; use doors and transitions that lead toward objectives; avoid repeating actions that "
               "already failed or were already tried (see the recent history). A locked door that did not open is "
               "usually waiting on the story: search nearby containers that hold items (the game often expects you "
               "to pick up and equip gear first) and talk to the character who gave the last instruction before "
               "trying the door again.")
CLAUDE_SYSTEM = ("You choose the next action for a KOTOR 1 co-op playtest bot. You never call tools unless a screenshot "
                 "file is named, and then only to look at it. Answer with one JSON object and nothing else.")
CLAUDE_ANSWER = ('Answer with only this JSON: {"choice": "<one option key>", "plan": "<one or two sentences: what you are '
                 'trying to do next>", "why": "<short reason>"}')
PROMPT_LIMIT = 6000
# Options that are housekeeping, not decisions: popups, menus, forced steps, the partner, gear.
ROUTINE_KEYS = ("ok_", "party_", "take_items", "close_menu", "unpause", "continue", "wait", "equip", "level_up", "control_",
                "join_", "follow")


def real_options(acts):
    return [k for k in acts if not k.startswith(ROUTINE_KEYS)]


def needs_claude(acts):
    """A real decision: two or more options that are not routine, and no plain fight to take."""
    return len(real_options(acts)) >= 2 and not any(k.startswith("fight_") for k in acts)


def journal_lines(journal, limit=8, width=350):
    """Active quests with the text of their CURRENT entry, newest first. A quest whose entry
    ends it is done and left out."""
    if kotor_lore is None or not journal:
        return ["(journal empty or unreadable)"]
    lines = []
    for tag, state in list(journal.items())[-limit:][::-1]:
        entry = kotor_lore.journal_entry(tag, state)
        if entry and entry[1]:
            continue
        name = kotor_lore.quest_name(tag) or tag
        text = (entry[0] if entry else "").replace("\n", " ")
        text = text[:width] + "..." if len(text) > width else text or "(no text)"
        lines.append(f"- {name} [{tag} #{state}]: {text}")
    return lines or ["(no active quests)"]


def build_prompt(role, me, peer, acts, plan="", history=(), image=None):
    """The compact text one Claude call gets: goal, journal, place, history, options (< ~6 KB)."""
    module = me.get("module") or ""
    area = (kotor_lore.area_name(module) if kotor_lore else "") or module
    objs = me.get("objects") or []
    hostile = sum(1 for o in objs if o["kind"] == "creature" and o.get("faction") == 1 and o["hp"] > 0 and not o["party"])
    party = [f"{who_is(module, o['tag'])} hp {o['hp']}/{o.get('max_hp')}" for o in objs if o["kind"] == "creature"
             and o["party"] and o["tag"] and not COOP_TAG.match(o["tag"])]
    folk = [f"{who_is(module, o['tag'])} {o['distance']:.0f}m" for o in objs if o["kind"] == "creature" and o["hp"] > 0
            and not o["party"] and o.get("faction") != 1 and o["tag"] and not COOP_TAG.match(o["tag"]) and o["distance"] <= 60][:8]
    where = (f"Area: {area} (module {module}). You are the {role}, controlling {me.get('controlled_tag') or 'your own character'}, "
             f"health {me.get('hp')}/{me.get('max_hp')}. Partner is in {peer.get('module', 'unknown')}. "
             f"Companions: {'; '.join(party) or 'none'}. Hostiles nearby: {hostile}. "
             f"Named characters nearby: {'; '.join(folk) or 'none'}.")
    if me.get("conversation"):
        speaking = who_is(module, me.get("partner_tag") or "")
        where += f" In a conversation with {speaking}; their last line: {(me.get('dialogue_line') or '').strip()[:200]!r}."
    for width in (350, 200, 120):
        hint = CAMPAIGN_HINTS.get(module, "")
        parts = [CLAUDE_GOAL, "", "Journal (active quests, current entry):", *journal_lines(me.get("journal"), width=width), "",
                 where, "", *([f"Walkthrough hint for this area: {hint}", ""] if hint else []), f"Your plan from last time: {plan or '(none yet)'}", "",
                 "Recent decisions, oldest first:", *(history or ["(none yet)"]), "",
                 "Options (key: description):", *(f"{k}: {a['description']}" for k, a in acts.items()), ""]
        if image:
            parts += [f"A screenshot of your game is at {image}. Read it only if the options are not enough.", ""]
        parts.append(CLAUDE_ANSWER)
        prompt = "\n".join(parts)
        if len(prompt) <= PROMPT_LIMIT:
            break
    return prompt


def extract_choice(text):
    """The first JSON object in `text` that has a "choice", or None."""
    decoder, i = json.JSONDecoder(), 0
    while True:
        i = (text or "").find("{", i)
        if i < 0:
            return None
        try:
            obj, end = decoder.raw_decode(text, i)
            if isinstance(obj, dict) and "choice" in obj:
                return obj
        except ValueError:
            pass
        i += 1


def find_claude():
    return shutil.which("claude") or str(Path.home() / ".local" / "bin" / ("claude.exe" if os.name == "nt" else "claude"))


class ClaudePolicy:
    """Claude Code, headless, decides the real choices; the rules policy keeps everything routine.
    Calls are budgeted and cached; every call is recorded for report.json."""
    name = "claude"

    def __init__(self, fallback, budget=150, model="sonnet", timeout=180, screenshots=False, claude_cmd=None, workdir=None):
        self.fallback, self.budget, self.model, self.timeout = fallback, budget, model, timeout
        self.screenshots, self.claude_cmd = screenshots, claude_cmd or find_claude()
        self.workdir = workdir or tempfile.gettempdir()
        self.shot_provider = None  # set by run(): role -> png path or None
        self.calls, self.plan, self.history, self.cache = [], {}, {}, {}
        self.cache_hits = self.rules_decisions = 0
        self.exhausted = False

    # -- memory the prompt carries ---------------------------------------------------------
    def record(self, role, choice, description, outcome):
        self.history.setdefault(role, collections.deque(maxlen=12)).append(f"{choice} ({description[:80]}) -> {outcome[:100]}")

    def note(self, role, text):
        self.history.setdefault(role, collections.deque(maxlen=12)).append(text)

    # -- the call -----------------------------------------------------------------------------
    def _run_claude(self, prompt, image):
        cmd = [self.claude_cmd, "-p", "--model", self.model, "--output-format", "json", "--no-session-persistence",
               "--disable-slash-commands", "--system-prompt", CLAUDE_SYSTEM]
        if image:
            cmd += ["--tools", "Read", "--allowedTools", "Read", "--add-dir", str(Path(image).parent)]
        else:
            cmd += ["--tools", ""]
        return subprocess.run(cmd, input=prompt, capture_output=True, text=True, encoding="utf-8", errors="replace",
                              timeout=self.timeout, cwd=self.workdir)

    async def _ask(self, role, prompt, image):
        record = {"n": len(self.calls) + 1, "role": role, "prompt_chars": len(prompt), "image": bool(image)}
        self.calls.append(record)
        started = time.monotonic()
        parsed = None
        try:
            done = await asyncio.to_thread(self._run_claude, prompt, image)
            record["seconds"] = round(time.monotonic() - started, 1)
            text = done.stdout
            try:
                outer = json.loads(done.stdout)
                record["cost_usd"] = outer.get("total_cost_usd")
                if outer.get("is_error"):
                    record["error"] = str(outer.get("result"))[:200]
                text = outer.get("result") if isinstance(outer.get("result"), str) else done.stdout
            except ValueError:
                pass
            if done.returncode != 0 and "error" not in record:
                record["error"] = f"exit {done.returncode}: {done.stderr.strip()[:200]}"
            parsed = extract_choice(text)
            if parsed is None and "error" not in record:
                record["error"] = "no JSON choice in the reply: " + str(text)[:160]
        except (subprocess.TimeoutExpired, OSError) as exc:
            record["seconds"] = round(time.monotonic() - started, 1)
            record["error"] = f"{type(exc).__name__}: {exc}"[:200]
        return record, parsed

    async def choose(self, role, text, acts, ctx=None):
        me, peer = (ctx or {}).get("me") or {}, (ctx or {}).get("peer") or {}
        real = real_options(acts)
        if not needs_claude(acts):
            self.rules_decisions += 1
            key, meta = await self.fallback.choose(role, text, acts)
            return key, dict(meta, routine=True)
        sig = (role, me.get("module"), tuple(sorted(real)), tuple(sorted((me.get("journal") or {}).items())),
               me.get("dialogue_node") if me.get("conversation") else None, bool(me.get("conversation")))
        cached = self.cache.get(role)
        if cached and cached["sig"] == sig:
            self.cache_hits += 1
            if cached["choice"] in acts and cached["uses"] < 3:
                cached["uses"] += 1
                return cached["choice"], {"source": "claude-cache"}
            untried = [k for k in real if k not in cached["used"]]
            if untried:
                cached["used"].add(untried[0])
                return untried[0], {"source": "claude-cache", "next_untried": True}
            self.rules_decisions += 1
            key, meta = await self.fallback.choose(role, text, acts)
            return key, dict(meta, note="claude choices for this situation are used up")
        if len(self.calls) >= self.budget:
            self.exhausted = True
            self.rules_decisions += 1
            key, meta = await self.fallback.choose(role, text, acts)
            return key, dict(meta, note="claude budget spent")
        image = None
        if self.screenshots and self.shot_provider:
            try:
                image = self.shot_provider(role)
            except Exception:
                image = None
        prompt = build_prompt(role, me, peer, acts, self.plan.get(role, ""), list(self.history.get(role, ())), image)
        record, parsed = await self._ask(role, prompt, image)
        record["offered"] = sorted(real)  # so a report can tell "not offered" from "passed over"
        choice = (parsed or {}).get("choice")
        if choice in acts:
            record.update(choice=choice, why=str(parsed.get("why", ""))[:200], plan=str(parsed.get("plan", ""))[:300])
            if parsed.get("plan"):
                self.plan[role] = str(parsed["plan"])[:300]
            self.cache[role] = {"sig": sig, "choice": choice, "uses": 1, "used": {choice}}
            return choice, {"source": "claude", "why": record["why"], "call": record["n"]}
        if parsed is not None:
            record["error"] = f"choice {choice!r} is not an offered key"
        self.rules_decisions += 1
        key, meta = await self.fallback.choose(role, text, acts)
        return key, dict(meta, source="rules", note="claude failed: " + record.get("error", "?"), call=record["n"])

    def report(self):
        ok = [c for c in self.calls if c.get("choice")]
        return {"budget": self.budget, "calls": len(self.calls), "answered": len(ok), "cache_hits": self.cache_hits,
                "rules_decisions": self.rules_decisions, "budget_spent": self.exhausted, "model": self.model,
                "prompt_chars_max": max((c["prompt_chars"] for c in self.calls), default=0),
                "seconds_total": round(sum(c.get("seconds", 0) for c in self.calls), 1),
                "cost_usd_total": round(sum(c.get("cost_usd") or 0 for c in self.calls), 4), "log": self.calls}


class Referee:
    def __init__(self, out, games):
        self.out, self.games = out, games
        self.since, self.reported, self.findings = {}, set(), []
        self.log_offsets = {g.pid: self._log(g).stat().st_size if self._log(g).exists() else 0 for g in games}
        self.join_wanted = {}

    @staticmethod
    def _log(game):
        return game.game_dir / "kotorcoop-plugin.log"

    def shot(self, name):
        paths = []
        for role, game in zip(("host", "joiner"), self.games):
            shotname = f"{name}_{role}"
            try:
                if game.console(f"screenshot {shotname} front").get("applied"):
                    src = game.game_dir / "kotorcoop-shots" / f"{shotname}.bmp"
                    if src.exists():
                        shutil.copy2(src, self.out / src.name)
                        paths.append(src.name)
            except (MemoryError, OSError):
                pass
        return paths

    def shot_one(self, role, name):
        """A front-buffer shot of one game as a PNG (PIL) or the BMP; None when it cannot be taken."""
        game = self.games[0 if role == "host" else 1]
        shotname = f"{name}_{role}"
        try:
            if game.console(f"screenshot {shotname} front").get("applied"):
                src = game.game_dir / "kotorcoop-shots" / f"{shotname}.bmp"
                if src.exists():
                    dest = self.out / src.name
                    shutil.copy2(src, dest)
                    try:
                        from PIL import Image
                        Image.open(dest).save(dest.with_suffix(".png"))
                        return dest.with_suffix(".png")
                    except Exception:
                        return dest
        except (MemoryError, OSError):
            pass
        return None

    def flag(self, key, detail, states):
        if key in self.reported:
            return
        self.reported.add(key)
        name = f"finding{len(self.findings) + 1:02d}"
        finding = {"id": name, "kind": key.split(":")[0], "detail": detail, "at": time.strftime("%H:%M:%S"),
                   "shots": self.shot(name),
                   "state": [{k: s.get(k) for k in ("module", "loading", "hp", "conversation", "partner_tag",
                                                    "remote_count", "position")} for s in states]}
        self.findings.append(finding)
        print("FINDING", json.dumps(finding), flush=True)

    def persist(self, key, bad, detail, states, now):
        if not bad:
            self.since.pop(key, None)
            return
        start = self.since.setdefault(key, now)
        if now - start >= SETTLE[key.split(":")[0]]:
            self.flag(key, detail, states)

    def persist_each(self, kind, diffs, states, now):
        """persist() per differing item. diffs maps an item id to its detail string; an item
        that agrees again drops its timer, and a kind stops reporting after MAX_PER_KIND."""
        live = {f"{kind}:{k}" for k in diffs}
        for key in [k for k in self.since if k.startswith(kind + ":") and k not in live]:
            del self.since[key]
        for item, detail in diffs.items():
            key = f"{kind}:{item}"
            if key not in self.reported and sum(1 for r in self.reported if r.startswith(kind + ":")) >= MAX_PER_KIND:
                continue
            self.persist(key, True, detail, states, now)

    def stamp_xp_start(self, host, joiner):
        """Remember whether the run began with both players on the same experience."""
        if not hasattr(self, "_xp_equal") and host.get("xp") is not None and joiner.get("xp") is not None                 and not host.get("controlled_tag") and not joiner.get("controlled_tag"):
            self._xp_equal = host["xp"] == joiner["xp"]
        host["xp_started_equal"] = getattr(self, "_xp_equal", False)

    def observe(self, host, joiner):
        self.stamp_xp_start(host, joiner)
        now = time.monotonic()
        states = [host, joiner]
        for role, game in zip(("host", "joiner"), self.games):
            if game.exited():
                self.flag(f"crash:{role}", f"the {role}'s game process exited", states)
        for game in self.games:
            log = self._log(game)
            if log.exists() and log.stat().st_size > self.log_offsets[game.pid]:
                with log.open("rb") as f:
                    f.seek(self.log_offsets[game.pid])
                    fresh = f.read().decode("utf-8", "replace")
                self.log_offsets[game.pid] = log.stat().st_size
                for line in fresh.splitlines():
                    if LOG_ALARM.search(line) and not LOG_IGNORE.search(line):
                        self.flag(f"log:{line[15:90]}", f"pid {game.pid}: {line.strip()[:300]}", states)
        if host.get("loading") or joiner.get("loading"):
            return
        # A conversation that stays on one node in BOTH games is a frozen scene: the harness
        # keeps pressing continue/answers, a voiced line would have ended long ago, and a
        # player would be stuck too. The key carries the node, so any progress restarts it.
        if host.get("conversation") and joiner.get("conversation"):
            nodes = (host.get("dialogue_node"), joiner.get("dialogue_node"))
            self.persist(f"stuckdialogue:{nodes}", True,
                         f"both games have sat on dialogue node {nodes} (partner {host.get('partner_tag') or joiner.get('partner_tag')}) "
                         f"for {SETTLE['stuckdialogue']:.0f} s while answers/continue were sent", states, now)
            for key in [k for k in self.since if k.startswith("stuckdialogue:") and k != f"stuckdialogue:{nodes}"]:
                self.since.pop(key, None)
        else:
            for key in [k for k in self.since if k.startswith("stuckdialogue:")]:
                self.since.pop(key, None)
        self.persist("module", host["module"] != joiner["module"],
                     f"host in {host['module']}, joiner in {joiner['module']}", states, now)
        # A load is not a disconnect: each side reports nobody while either world loads, and two
        # quick area changes in a row (Dantooine 2026-10-09) kept that up past the window.
        loading = host.get("loading") or joiner.get("loading")
        self.persist("disconnected", not loading and (not host["remote_count"] or not joiner["remote_count"]),
                     f"remote counts host {host['remote_count']} joiner {joiner['remote_count']}", states, now)
        for role, me in (("host", host), ("joiner", joiner)):
            tag = self.join_wanted.get(role)
            if tag:
                self.persist(f"join:{role}", not me["conversation"],
                             f"the {role} asked to join the conversation with {tag} but is not in it", states, now)
                if me["conversation"] or not (host["conversation"] or joiner["conversation"]):
                    self.join_wanted.pop(role, None)
                    self.since.pop(f"join:{role}", None)
        if host["module"] == joiner["module"]:
            diff, detail = census_difference(host, joiner)
            self.persist("census", diff, detail, states, now)
            for kind, check in SHARED_STATE_CHECKS:
                self.persist_each(kind, check(host, joiner), states, now)
        else:
            for kind, _ in SHARED_STATE_CHECKS:
                self.persist_each(kind, {}, states, now)


def safe_to_checkpoint(me):
    """A checkpoint saved mid-fight is a trap: the host loads it alone (the joiner connects later),
    and the enemies standing there kill a lone host, which is a real game over (2026-10-09:
    campaign_tar_m02aa came back to the main menu 20 s after loading). Save only with no living
    hostile within 30 m and at least half health."""
    hp, max_hp = me.get("hp") or 0, me.get("max_hp") or 0
    if max_hp > 0 and hp * 2 < max_hp:
        return False
    return not any(o["kind"] == "creature" and o.get("faction") == 1 and o.get("hp", 0) > 0 and not o.get("party")
                   and o.get("distance", 0) < 30 for o in me.get("objects", []))


def checkpoint_new_area(games, checkpointed, report):
    """The host saves `campaign_<module>` the first time both players stand in a module, so a long
    campaign run can resume from its furthest area instead of the Endar Spire. Uses the co-op
    `save <name>` verb through blockpoke (the playtest console allowlist leaves saves out)."""
    host, joiner = snapshot_retrying(games[0]), snapshot_retrying(games[1])
    module = (host.get("module") or "").lower()
    if (not module or module in checkpointed or host.get("loading") or joiner.get("loading")
            or (joiner.get("module") or "").lower() != module or host.get("conversation") or joiner.get("conversation")
            or (host.get("panels") and "main_interface" not in host["panels"])
            or not safe_to_checkpoint(host) or not safe_to_checkpoint(joiner)):
        return
    checkpointed.add(module)
    blockpoke = os.path.join(os.path.dirname(os.path.abspath(__file__)), "blockpoke.ps1")
    subprocess.run(["pwsh", "-NoProfile", "-File", blockpoke, "-ProcId", str(games[0].pid), "console", f"save campaign_{module}"],
                   capture_output=True, check=False)
    report.setdefault("checkpoints", []).append({"module": module, "at": time.strftime("%X")})
    print("CHECKPOINT", module, flush=True)


def snapshot_retrying(game, attempts=60, pause=0.5):
    """A snapshot that survives the game freeing an object mid-read. Under the rig's full page
    heap a freed creature is unreadable at once, and one such read ended a 2026-10-09 run with
    both games healthy; a module transition (the Endar Spire's escape pod to Taris) keeps reads
    failing for several seconds, so a live game gets up to 30 s. Only a process that has exited,
    or a read failing every attempt, raises."""
    for attempt in range(attempts):
        try:
            return game.snapshot()
        except MemoryError:
            if game.exited() or attempt == attempts - 1:
                raise
            time.sleep(pause)
            time.sleep(pause)


def census_difference(host, joiner):
    """Whether the two games disagree about who is alive in the shared area. An enemy the
    host's scripts created after the joiner loaded (Endar Spire reinforcements) has no body
    of its own on the joiner: the mod materialises a stand-in from the co-op blueprint, tagged
    coop_phantom (MaterialisedEnemies). So host-only creatures are matched by the joiner's
    extra coop_phantom bodies, beyond one per remote player."""
    def alive(s):
        return collections.Counter(o["tag"] for o in s["objects"] if o["kind"] == "creature"
                                   and o["hp"] > 0 and not o["party"] and not COOP_TAG.match(o["tag"]))
    def stand_ins(s):
        bodies = sum(1 for o in s["objects"] if o["kind"] == "creature" and o["hp"] > 0 and COOP_TAG.match(o["tag"]))
        return max(0, bodies - s.get("remote_count", 0))
    h, j = alive(host), alive(joiner)
    host_only, joiner_only = h - j, j - h
    unexplained = sum(host_only.values()) - stand_ins(joiner)
    bad = unexplained > 0 or sum(joiner_only.values()) > stand_ins(host)
    return bad, (f"living creatures differ in {host['module']}: host-only {dict(host_only)}, joiner-only "
                 f"{dict(joiner_only)}, joiner stand-ins {stand_ins(joiner)}, host stand-ins {stand_ins(host)}")


def _located(objs, kind, usable):
    """{(tag, x, y): object} for the unambiguous objects of one kind. Doors and containers do
    not move, so tag plus position rounded to 0.1 m names the same object in both games; a key
    two objects share is dropped rather than guessed."""
    seen, dup = {}, set()
    for o in objs:
        if o["kind"] != kind or not o.get("tag") or not usable(o):
            continue
        key = (o["tag"].lower(), round(o["position"][0], 1), round(o["position"][1], 1))
        if key in seen:
            dup.add(key)
        seen[key] = o
    return {k: v for k, v in seen.items() if k not in dup}


def _state_pairs(host, joiner, kind, usable):
    h, j = _located(host["objects"], kind, usable), _located(joiner["objects"], kind, usable)
    return [(k, h[k], j[k]) for k in h if k in j]


def door_differences(host, joiner):
    """Doors in the shared area whose open or locked state differs (DoorController: ids and
    state agree across instances, lock and open both ride the flag channel). Open is the engine's
    open-state byte, 0 closed, non-zero some open pose; a swing in flight settles."""
    out = {}
    word = lambda o: f"{'open' if o['open'] else 'closed'}/{'locked' if o['locked'] else 'unlocked'}"
    for (tag, x, y), h, j in _state_pairs(host, joiner, "door",
                                         lambda o: o.get("open") is not None and o.get("locked") is not None
                                         and not o.get("static")):  # scenery the mod does not sync
        if bool(h["open"]) != bool(j["open"]) or bool(h["locked"]) != bool(j["locked"]):
            out[f"{tag}@{x:g},{y:g}"] = (f"door {tag} at ({x:g}, {y:g}) in {host['module']}: host {word(h)}, joiner {word(j)}")
    return out


def container_differences(host, joiner):
    """Containers (placeables with an inventory) whose open or locked state differs
    (docs/research/placeables-containers.md: both are plain fields the mod mirrors)."""
    out = {}
    usable = lambda o: o.get("container") and o.get("locked") is not None and o.get("open") is not None
    word = lambda o: f"{'opened' if o['open'] else 'unopened'}/{'locked' if o['locked'] else 'unlocked'}"
    for (tag, x, y), h, j in _state_pairs(host, joiner, "placeable", usable):
        if h["open"] != j["open"] or h["locked"] != j["locked"]:
            out[f"{tag}@{x:g},{y:g}"] = (f"container {tag} at ({x:g}, {y:g}) in {host['module']}: host {word(h)}, joiner {word(j)}")
    return out


def enemy_hp_differences(host, joiner):
    """Hostile creatures present in both games (same object id and tag: enemies are keyed by
    engine object id, SharedBlock.Combat.cs) that disagree about health. Enemies have one shared
    health bar, so after the settle window the two must agree within the larger of 3 points and
    15% of max health, and dead on one must be dead on the other. Stand-ins (coop tag), party
    members and non-hostiles are not compared."""
    def hostile(s):
        return {o["id"]: o for o in s["objects"] if o["kind"] == "creature" and o.get("hp") is not None
                and not o["party"] and not COOP_TAG.match(o["tag"] or "") and o.get("faction") == 1}
    h, j = hostile(host), hostile(joiner)
    out = {}
    for oid in h.keys() & j.keys():
        a, b = h[oid], j[oid]
        if (a["tag"] or "").lower() != (b["tag"] or "").lower():
            continue
        # Full on both sides is agreement. A blueprint can carry MaxHitPoints above its base
        # HitPoints (Korriban's war droids: 30 base, 48 max), and one game may sit at the bonus
        # maximum while the other sits at the base one; both health bars read full.
        if all(o["hp"] >= (o.get("max_hp") or o["hp"] + 1) for o in (a, b)):
            continue
        top = max(a.get("max_hp") or 0, b.get("max_hp") or 0, a["hp"], b["hp"], 1)
        tolerance = max(3, 0.15 * top)
        dead = (a["hp"] <= 0) != (b["hp"] <= 0)
        if dead or (a["hp"] > 0 and abs(a["hp"] - b["hp"]) > tolerance):
            out[f"{a['tag']}#{oid:X}"] = (f"enemy {a['tag']} (id 0x{oid:X}) in {host['module']}: host hp {a['hp']}/"
                                          f"{a.get('max_hp')}, joiner hp {b['hp']}/{b.get('max_hp')}"
                                          + (" (dead on one side)" if dead else ""))
    return out


def party_differences(host, joiner):
    """The host's companions are mirrored onto the joiner as real party members (PartyPresence
    keys them by roster slot, so tags agree). Compares the multiset of non-empty, non-coop tags
    of creatures with the party flag (+0xA88): the player character's tag is empty, so a swap to a
    companion keeps that companion in both lists."""
    def party(s):
        return collections.Counter((o["tag"] or "").lower() for o in s["objects"] if o["kind"] == "creature"
                                   and o["party"] and o["tag"] and not COOP_TAG.match(o["tag"]))
    h, j = party(host), party(joiner)
    out = {}
    lists = f"(host party {sorted(h.elements())}, joiner {sorted(j.elements())})"
    for tag in (h - j):
        out[f"missing_on_joiner:{tag}"] = f"host companion {tag} has no party member on the joiner in {host['module']} {lists}"
    for tag in (j - h):
        out[f"extra_on_joiner:{tag}"] = f"joiner has party member {tag} the host does not in {host['module']} {lists}"
    return out


def peer_health_differences(host, joiner):
    """Each game's picture of the OTHER player's health. Two reads per direction, both skipped
    when the peer is downed (the stand-in is held at 1 hp then), controls a companion, or the
    game has other than exactly one remote slot: (1) the remote slot's health against the peer's
    own hp, tolerance max(2, 10% of max); (2) the peer's stand-in body, the coop-tagged creature
    nearest the slot (within 3 m), against clamp(slot health, 1, slot max): ApplyVitals mirrors it."""
    out = {}
    for role, me, peer in (("host", host, joiner), ("joiner", joiner, host)):
        slots = me.get("remote_slots")
        if not slots or len(slots) != 1 or peer.get("hp") is None or peer["hp"] <= 0 or peer.get("controlled_tag"):
            continue
        slot = slots[0]
        top = max(peer.get("max_hp") or 0, slot.get("max_health") or 0, peer["hp"], 1)
        if abs(slot["health"] - peer["hp"]) > max(2, 0.1 * top):
            out[f"{role}:slot"] = (f"the {role}'s remote slot says the peer has {slot['health']} hp, the peer's own game "
                                   f"has {peer['hp']}/{peer.get('max_hp')}")
        bodies = [o for o in me["objects"] if o["kind"] == "creature" and COOP_TAG.match(o["tag"] or "") and o.get("hp") is not None
                  and math.dist(o["position"][:2], slot["position"][:2]) <= 3]
        if bodies and slot["health"] > 0 and slot.get("max_health"):
            body = min(bodies, key=lambda o: math.dist(o["position"][:2], slot["position"][:2]))
            want = min(max(slot["health"], 1), slot["max_health"])
            if abs(body["hp"] - want) > max(2, 0.1 * slot["max_health"]):
                out[f"{role}:phantom"] = (f"the {role}'s stand-in for the peer shows {body['hp']} hp but the peer slot says "
                                          f"{slot['health']}/{slot['max_health']} (peer's game: {peer['hp']}/{peer.get('max_hp')})")
    return out


def _dict_differences(a, b, label, module):
    out = {}
    for name in sorted(a.keys() | b.keys()):
        if a.get(name, 0) != b.get(name, 0):
            out[name] = f"{label} {name} in {module}: host {a.get(name, 'absent')}, joiner {b.get(name, 'absent')}"
    return out


def journal_differences(host, joiner):
    """Quest entries (tag -> state) that differ. Progress merges upward and the session is the
    authority, so after the settle window both journals hold the same entries at the same state."""
    if host.get("journal") is None or joiner.get("journal") is None:
        return {}
    return _dict_differences(host["journal"], joiner["journal"], "journal entry", host["module"])


def globals_differences(host, joiner):
    """Synced story globals (non-zero booleans and numbers, by name) that differ. The bridge
    replicates every boolean and number except K_CURRENT_PLANET, K_LAST_MODULE, K_LAST_LOCATION,
    K_STUNT_MODULE and k_vis_*; the snapshot already leaves those out. Absent means zero."""
    if host.get("globals") is None or joiner.get("globals") is None:
        return {}
    return _dict_differences(host["globals"], joiner["globals"], "global", host["module"])


def xp_differences(host, joiner):
    """The player's own experience. The promise (limitations.md, progression-sync.md's
    2026-09-18 baseline rule): players who start level stay level; a character that joins
    ahead keeps its own lead and does not lift the other. So totals are compared only when the
    run started with both equal (the referee stamps `xp_started_equal`). Skipped while either
    player controls a companion (the snapshot then reads the companion's experience)."""
    if host.get("xp") is None or joiner.get("xp") is None or host.get("controlled_tag") or joiner.get("controlled_tag"):
        return {}
    if not host.get("xp_started_equal") or host["xp"] == joiner["xp"]:
        return {}
    return {"pc": f"player experience drifted apart after starting level: host {host['xp']}, joiner {joiner['xp']}"}


SHARED_STATE_CHECKS = (("door", door_differences), ("container", container_differences),
                       ("enemyhp", enemy_hp_differences), ("party", party_differences),
                       ("peerhp", peer_health_differences), ("journal", journal_differences),
                       ("globals", globals_differences), ("xp", xp_differences))


async def run(args, policy):
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=False)
    games = [Game(args.host), Game(args.client)]
    referee = Referee(out, games)
    tried = {"host": {}, "joiner": {}}
    moving, idle, waits = {}, {}, {}
    report = {"policy": policy.name, "steps": [], "modules": [], "counts": collections.Counter()}
    if hasattr(policy, "shot_provider") and args.claude_screenshots:
        shots = itertools.count(1)
        policy.shot_provider = lambda role: referee.shot_one(role, f"claude{next(shots):03d}")
    own_tags = {}

    def look(index):
        # A player character can carry a tag (characters made in chargen on some saves do), so
        # "controlling a companion" means a tag other than the one the run started with.
        state = snapshot_retrying(games[index])
        if not state.get("loading") and "controlled_tag" in state:
            own = own_tags.setdefault(index, state["controlled_tag"])
            if state["controlled_tag"] == own:
                state["controlled_tag"] = ""
        return state

    checkpointed = set()
    started = time.monotonic()
    try:
        for step in range(args.steps):
            if time.monotonic() - started > args.minutes * 60:
                report["stop_reason"] = "time budget"
                break
            if args.checkpoints:
                checkpoint_new_area(games, checkpointed, report)
            for index, role in enumerate(("host", "joiner")):
                me, peer = look(index), look(1 - index)
                if me.get("module") and me["module"] not in report["modules"]:
                    report["modules"].append(me["module"])
                last_pos = moving.get(role)
                moving[role] = me.get("position")
                if (tried[role].get("last", "").startswith(("exit_", "open_", "approach_", "unlock_")) and last_pos and me.get("position")
                        and not me.get("loading") and math.dist(last_pos[:2], me["position"][:2]) > .3):
                    continue  # still walking to the door it was sent to; a new order would cancel it
                if "party_selection" not in (me.get("panels") or []):
                    tried[role].pop("party_ok", None)  # a new party screen starts with adding a companion
                if tried[role].get("was_talking") and not me.get("conversation"):
                    refresh_doors(tried[role], me)  # story beats unlock doors
                if tried[role].get("was_talking") and not me.get("conversation") and hasattr(policy, "note"):
                    policy.note(role, f"conversation with {who_is(me.get('module'), tried[role].get('partner', ''))} ended")
                tried[role]["was_talking"] = bool(me.get("conversation"))
                if me.get("conversation"):
                    tried[role]["partner"] = me.get("partner_tag") or ""
                nav = nav_for(me.get("module")) if role == "host" and me.get("module") else None
                if nav and me.get("position"):
                    for name, centre in nav.rooms.items():
                        if math.dist(centre, me["position"][:2]) < 7:
                            tried[role].setdefault("visited", set()).add(name)
                acts = actions_for(role, me, peer, tried[role], nav)
                if not acts:
                    idle[role] = idle.get(role, 0) + 1
                    if idle[role] >= 3:
                        refresh_doors(tried[role], me)
                        idle[role] = 0
                    continue
                idle[role] = 0
                if list(acts) == ["wait"]:
                    waits[role] = waits.get(role, 0) + 1
                    if waits[role] >= 5:
                        # Every answer was tried and the conversation is still open: forget them.
                        for k in [k for k in tried[role] if k.startswith("said:")]:
                            del tried[role][k]
                        waits[role] = 0
                else:
                    waits[role] = 0
                text = summary(role, me, peer)
                choice, meta = await policy.choose(role, text, acts, ctx={"me": me, "peer": peer})
                act = acts[choice]
                now = snapshot_retrying(games[index])
                if now.get("loading") or now.get("module") != me.get("module"):
                    continue
                results = []
                if act.get("walk"):
                    import coop_walk
                    reached = coop_walk.walk_path(games[index], act["walk"], log=lambda m: report.setdefault("harness_notes", []).append(f"{role}: {m}"))
                    results.append({"reply": f"walked {reached}/{len(act['walk']) - 1} legs"})
                commands = act["commands"]
                if choice == "continue" and not snapshot_retrying(games[index]).get("conversation"):
                    # Space outside a conversation is the PAUSE key. On run 1 a "continue" landed
                    # 4 ms after the mod swapped the joiner's own conversation for the host's;
                    # the joiner's world sat paused, with no banner, for 32 s, and the join looked
                    # dropped. Re-check right before sending.
                    commands = []
                    results.append({"reply": "the conversation had closed; no Space"})
                if act.get("join"):
                    # Only while the partner is still in that conversation: a join that lands
                    # after it ended starts a fresh one with the same character.
                    partner_now = snapshot_retrying(games[1 - index])
                    if not partner_now.get("conversation") or (partner_now.get("partner_tag") or "").lower() != act["join"].lower():
                        commands = []
                        results.append({"reply": "the partner's conversation had ended; no join"})
                if act.get("reach"):
                    # The attack verb teleports the rest of the way (CheatController.Attack's
                    # melee snap). A route can look walkable on the room graph and still end at
                    # a window: twice on the Endar Spire the host was snapped 26 m into the
                    # sealed cutscene room and the engine faulted. Attack only from within reach.
                    target_id, limit = act["reach"]
                    there = next((o for o in snapshot_retrying(games[index])["objects"] if o["id"] == target_id), None)
                    if there is None or there["distance"] > limit:
                        commands = []
                        results.append({"reply": "could not walk within reach of the enemy; no attack"})
                for i, command in enumerate(commands):
                    results.append(games[index].console(command))
                    if act.get("gap") and i + 1 < len(commands):
                        await asyncio.sleep(act["gap"])  # GUI clicks land one frame-pump apart
                tried[role]["last"] = choice
                if hasattr(policy, "record"):
                    policy.record(role, choice, act["description"], "; ".join(str(r.get("reply", ""))[:50] for r in results[:2]) or "sent")
                if act.get("took") or choice.startswith("search_"):
                    tried[role]["took"] = bool(act.get("took"))
                if act.get("took"):
                    tried[role]["took_any"] = True
                    # New items: offer the equipment screen again for whoever is in control.
                    for k in [k for k in tried[role] if k.startswith("equip:")]:
                        del tried[role][k]
                if act.get("level"):
                    # Try again only once experience reaches the next level's threshold: a
                    # character that cannot level just reopened and closed its sheet 60 times.
                    xp = me.get("xp")
                    tried[role]["level:" + act["level"]] = next_level_xp(xp) if xp is not None else NEVER
                if act.get("ok"):
                    tried[role]["ok:" + act["ok"]] = tried[role].get("ok:" + act["ok"], 0) + 1
                for panel in OK_ROWS:
                    if panel not in (me.get("panels") or []):
                        tried[role].pop("ok:" + panel, None)
                if act.get("said"):
                    tried[role][act["said"]] = tried[role].get(act["said"], 0) + 1
                if act.get("equip"):
                    tried[role]["equip:" + act["equip"]] = 1
                if act.get("equip") or act.get("took"):
                    # New gear changes what people say (Trask's tutorial): talk to them again.
                    for k in [k for k in tried[role] if k.startswith("talk:")]:
                        del tried[role][k]
                if act.get("swap"):
                    tried[role]["swaps"] = tried[role].get("swaps", 0) + 1
                    tried[role]["acted_as_companion"] = 0
                elif me.get("controlled_tag"):
                    tried[role]["acted_as_companion"] = tried[role].get("acted_as_companion", 0) + 1
                if choice.startswith("party_"):
                    tried[role]["party_ok"] = tried[role].get("party_ok", 0) + 1
                if act.get("count"):
                    tried[role][act["count"]] = tried[role].get(act["count"], 0) + 1
                if act.get("target") and not act.get("walk"):
                    tried[role][act["target"]] = tried[role].get(act["target"], 0) + 1
                if act.get("join"):
                    tried[role]["join:" + act["join"]] = tried[role].get("join:" + act["join"], 0) + 1
                    referee.join_wanted[role] = act["join"]
                if choice == "follow":
                    tried[role]["follow"] = 1
                report["counts"][choice.split("_")[0]] += 1
                row = {"step": step, "role": role, "situation": text, "options": {k: a["description"] for k, a in acts.items()},
                       "choice": choice, **meta, "results": [str(r.get("reply", ""))[:120] for r in results]}
                report["steps"].append(row)
                print(json.dumps({k: row[k] for k in ("step", "role", "choice", "source")}), flush=True)
            await asyncio.sleep(args.pace)
            referee.observe(look(0), look(1))
            if any(f["kind"] == "crash" for f in referee.findings):
                report["stop_reason"] = "crash"
                break
            if step % args.shot_every == 0:
                referee.shot(f"step{step:03d}")
        else:
            report["stop_reason"] = "step budget"
    except Exception as exc:
        report["stop_reason"] = f"runtime error: {type(exc).__name__}: {exc}"
        raise
    finally:
        for game in games:
            try:
                for key in (DIK_W, DIK_A, DIK_D):
                    game.console(f"input key {key} up")
                game.console("attack stop")
            except Exception:
                pass
            game.close()
        report["seconds"] = round(time.monotonic() - started, 1)
        report["findings"] = referee.findings
        if hasattr(policy, "report"):
            report["claude"] = policy.report()
        (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        lines = [f"# Co-op playtest ({policy.name}), {report['seconds']} s, stop: {report.get('stop_reason')}", "",
                 f"Areas: {', '.join(report['modules'])}", f"Actions: {dict(report['counts'])}", "",
                 f"## Findings ({len(referee.findings)})", ""]
        if report.get("claude"):
            c = report["claude"]
            lines[3:3] = [f"Claude: {c['calls']} calls ({c['answered']} answered), {c['cache_hits']} cache reuses, "
                          f"{c['rules_decisions']} decisions by rules, max prompt {c['prompt_chars_max']} chars, {c['seconds_total']} s"
                          + (f". BUDGET SPENT ({c['budget']} calls): the rest of the run fell back to rules." if c["budget_spent"] else ""), ""]
        lines += [f"- **{f['kind']}** {f['at']}: {f['detail']} (shots: {', '.join(f['shots']) or 'none'})" for f in referee.findings]
        (out / "findings.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
        print("FINAL", json.dumps({k: report[k] for k in ("stop_reason", "modules", "counts", "seconds")} | {"findings": len(referee.findings)}), flush=True)


async def main_async(args):
    rules = RulesPolicy(args.seed)
    if args.policy == "rules":
        await run(args, rules)
        return
    if args.policy == "claude":
        await run(args, ClaudePolicy(rules, budget=args.claude_budget, model=args.claude_model, timeout=args.claude_timeout,
                                     screenshots=args.claude_screenshots, claude_cmd=args.claude_cmd))
        return
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    config = tomllib.loads((Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))) / "config.toml")
                           .read_text())["mcp_servers"][args.server]
    params = StdioServerParameters(command=config["command"], args=config["args"], env=dict(os.environ, **config.get("env", {})))
    Path(args.output).resolve().parent.mkdir(parents=True, exist_ok=True)
    with open(Path(args.output).resolve().with_suffix(".mcp-stderr.log"), "w", encoding="utf-8") as errors:
        async with stdio_client(params, errlog=errors) as (reader, writer):
            async with ClientSession(reader, writer) as session:
                await session.initialize()
                await run(args, LayaPolicy(session, args.confidence, rules))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", type=int, required=True)
    parser.add_argument("--client", type=int, required=True)
    parser.add_argument("--policy", choices=("laya", "rules", "claude"), default="laya")
    parser.add_argument("--minutes", type=float, default=20)
    parser.add_argument("--steps", type=int, default=400)
    parser.add_argument("--checkpoints", action="store_true",
                        help="the host saves campaign_<module> the first time both players settle in each module")
    parser.add_argument("--pace", type=float, default=3.0, help="seconds between steps")
    parser.add_argument("--shot-every", type=int, default=10)
    parser.add_argument("--confidence", type=float, default=.4)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--server", default="laya-kotor-pilot")
    parser.add_argument("--claude-budget", type=int, default=150, help="most claude -p calls per run; rules take over after")
    parser.add_argument("--claude-model", default="sonnet")
    parser.add_argument("--claude-timeout", type=float, default=180, help="seconds per claude call")
    parser.add_argument("--claude-cmd", default=None, help="path to the claude CLI (default: PATH, then ~/.local/bin)")
    parser.add_argument("--claude-screenshots", action="store_true",
                        help="attach a front-buffer screenshot of the acting game to each call and allow the Read tool")
    parser.add_argument("--output", required=True, help="a NEW directory for the evidence")
    args = parser.parse_args()
    if not 0 < args.minutes <= 240 or not 1 <= args.steps <= 5000:
        parser.error("minutes 0..240, steps 1..5000")
    asyncio.run(main_async(args))


if __name__ == "__main__":
    main()
