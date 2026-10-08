"""Two-player co-op playtest harness: both players act, a referee compares the two games.

Each step, the host and then the joiner get a list of legal actions built from their own
game's state (dialogue replies with their text, hostiles, containers, NPCs, doors, following
the partner). A policy picks one: Laya through its MCP server (--policy laya), or fixed rules
(--policy rules). Actions go through the plugin's console mailbox and real input verbs only;
no warps, teleports, spawns or save edits (laya_playtest.Game enforces the allowlist).

After every step the referee compares the two games: process alive, same area, still
connected, a conversation join that took, the same living creatures. A difference that
outlives its settle window becomes a finding with front-buffer screenshots from both games.
New crash/exception lines in either plugin log are findings too. The referee owns PASS/FAIL;
the policy only decides what to do next.

  python tools/coop_playtest.py --host <pid> --client <pid> --policy laya --minutes 20
      --output <new evidence directory>

Needs the isolated rig (tools/demo.ps1 -Isolated) so injected input reaches unfocused windows.
"""
from __future__ import annotations

import argparse
import asyncio
import collections
import json
import math
import os
import random
import re
import shutil
import time
import tomllib
from pathlib import Path

from laya_playtest import REPO, Game, MemoryError

GOAL = ("Play Star Wars: Knights of the Old Republic co-op and make story progress: finish "
        "conversations choosing replies that move the story forward, defeat hostile enemies, "
        "search containers, talk to new characters, then explore doors into new areas. "
        "Prefer actions not tried before. Choose only one of the listed actions.")

DIK_W, DIK_A, DIK_D, DIK_SPACE = 17, 30, 32, 57  # W/A/D only released on exit
COOP_TAG = re.compile(r"^coop", re.IGNORECASE)

# Settle windows: how long a cross-game difference may last before it is a finding.
SETTLE = {"module": 45.0, "disconnected": 15.0, "census": 25.0, "join": 15.0}
LOG_ALARM = re.compile(r"unhandled|exception|crash record|access violation|fail-fast", re.IGNORECASE)
LOG_IGNORE = re.compile(r"FIRST-CHANCE exception \(0x406D1388\)|unhandled-exception hook installed"
                        r"|crash probe|managed faults now reach")


# Retail 800x600 GUI positions, measured on the rig (2026-10-08). The panel names come from
# laya_playtest's active-panel list, so a click is only sent while its panel is up.
OK_STATUS_SUMMARY = "input click 399 316"     # "Experience Points Received" and similar
OK_MESSAGE_BOX = "input click 399 329"        # "This character must be a member of your party..."
CONTAINER_GET, CONTAINER_CLOSE = "input click 399 391", "input click 399 441"
PARTY_FIRST, PARTY_ADD, PARTY_OK = "input click 176 210", "input click 270 449", "input click 398 485"
HUD_EQUIP = "input click 565 20"              # the Equipment icon, top right of the HUD
EQUIP_RIGHT_WEAPON, EQUIP_BODY = "input click 370 325", "input click 320 275"
EQUIP_SECOND_ROW, EQUIP_OK = "input click 550 263", "input click 400 485"
HUD_SECOND_PORTRAIT = "input click 94 577"    # the first companion's portrait; click again to return
HUD_CHARACTER = "input click 625 20"          # the Character icon, top right of the HUD
CHARACTER_AUTO_LEVEL = "input click 268 297"  # Character screen "Auto Level Up"; its confirm is a message box


def menu_actions(panels, tried):
    """A menu owns the input; nothing else can happen until it closes."""
    if "status_summary" in panels:
        return {"ok_summary": {"description": "Dismiss the experience/status notice (OK)", "commands": [OK_STATUS_SUMMARY]}}
    if "message_box" in panels:
        return {"ok_message": {"description": "Dismiss the message box (OK)", "commands": [OK_MESSAGE_BOX]}}
    if "party_selection" in panels:
        if tried.get("party_ok", 0) < 1:
            return {"party_ok": {"description": "Accept the party selection (OK)", "commands": [PARTY_OK]}}
        return {"party_add": {"description": "Add the first party member, then accept (OK)",
                              "commands": [PARTY_FIRST, PARTY_ADD, PARTY_OK], "gap": 1.2}}
    if "container" in panels:
        # Take first, and only then close: a policy that closes a full container skips the
        # gear the story expects (Endar Spire: Trask waits for the footlocker's weapon).
        if not tried.get("took"):
            return {"take_items": {"description": "Take the items from the open container",
                                   "commands": [CONTAINER_GET], "took": True}}
        return {"close_menu": {"description": "Close the container", "commands": [CONTAINER_CLOSE]}}
    # Anything else (equipment, options, an unnamed popup) closes through HideSWInGameGui,
    # which leaves both games running.
    return {"close_menu": {"description": f"Close the open menu ({', '.join(panels)})", "commands": ["closegui"]}}


def actions_for(role, me, peer, tried):
    """Legal actions for one player, most useful first. role is 'host' or 'joiner'."""
    if me.get("loading") or me.get("hp", 1) <= 0:
        return {}
    acts = {}
    key = lambda o: f"{me['module']}:{o['id']}"
    fresh = lambda o, limit: tried.get(key(o), 0) < limit

    panels = me.get("panels") or []
    if (me.get("menu_open") and not me["conversation"]) or {"status_summary", "message_box"} & set(panels):
        # Notices sit on top of a conversation too (XP mid-dialogue) and block it until dismissed.
        return menu_actions(panels, tried)

    if me["conversation"]:
        if me["reply_count"]:
            texts = me.get("reply_texts") or []
            for i in range(me["reply_count"]):
                said = texts[i] if i < len(texts) else "(reply text unavailable)"
                # Loop guard: a reply picked twice in this conversation is not offered again. A
                # tutorial question loops back to itself, and a spectator's picks are swallowed
                # by the mod (only the owner answers), so neither ever moves the story.
                if tried.get(f"said:{me.get('partner_tag')}:{said}", 0) >= 2:
                    continue
                acts[f"reply_{i + 1}"] = {"description": f'Answer: "{said}"', "commands": [f"input key {i + 2} tap"],
                                          "said": f"said:{me.get('partner_tag')}:{said}"}
            if not acts:
                acts["wait"] = {"description": "Wait: the conversation is not yours to answer, or every answer was tried",
                                "commands": []}
        else:
            acts["continue"] = {"description": "Continue the conversation (Space)", "commands": [f"input key {DIK_SPACE} tap"]}
        return acts

    if peer.get("conversation") and me.get("peer_partner_tag") and tried.get("join:" + me["peer_partner_tag"], 0) < 2:
        tag = me["peer_partner_tag"]
        acts[f"join_{tag}"] = {"description": f"Join your partner's conversation with {tag}",
                               "commands": [f"talktag {tag}"], "join": tag}

    objs = me["objects"]
    living = [o for o in objs if o["kind"] == "creature" and o["hp"] > 0 and not o["party"] and not COOP_TAG.match(o["tag"])]
    # Any distance in the area: an attack order makes the server run the creature over (seen
    # live from 74 m), and story gates wait for a room to be cleared (END_ROOM7_DEAD).
    for o in sorted([o for o in living if o["faction"] == 1 and o["distance"] < 90 and fresh(o, 4)],
                    key=lambda o: o["distance"])[:2]:
        acts[f"fight_{o['id']}"] = {"description": f"Attack the hostile {o['tag']} {o['distance']:.0f} m away",
                                    "commands": [f"attack 0x{o['id']:X}"], "target": key(o)}
    who = me.get("controlled_tag") or "pc"
    if tried.get("took_any") and not any(o["faction"] == 1 and o["distance"] < 25 for o in living)             and tried.get(f"equip:{who}", 0) < 1:
        # Retail clicks only: the HUD icon, the right-weapon slot, the first item under None,
        # OK; the same for the body slot; then close. Offered once per character, and again
        # after new items are taken from a container.
        acts["equip"] = {"description": "Equip your gear: a weapon and armor (Equipment screen)",
                         "commands": [HUD_EQUIP, EQUIP_RIGHT_WEAPON, EQUIP_SECOND_ROW, EQUIP_OK,
                                      EQUIP_BODY, EQUIP_SECOND_ROW, EQUIP_OK, "closegui"],
                         "gap": 1.2, "equip": who}

    if tried.get(f"level:{who}", 0) < 1 and not any(o["faction"] == 1 and o["distance"] < 25 for o in living):
        # Retail Auto Level Up. Offered again after new experience (an XP notice, a fight); when
        # the character cannot level the button does nothing and the screen is closed again.
        # The Endar Spire's bridge door refuses a character with a pending level.
        acts["level_up"] = {"description": "Level up if you can (Character screen, Auto Level Up); "
                            "some doors refuse a character with a level waiting",
                            "commands": [HUD_CHARACTER, CHARACTER_AUTO_LEVEL], "gap": 1.5, "level": who}
    for o in [o for o in objs if o["kind"] == "placeable" and o["distance"] < 12 and fresh(o, 1)][:2]:
        acts[f"search_{o['id']}"] = {"description": f"Search the {o['tag'] or 'object'} {o['distance']:.0f} m away",
                                     "commands": [f"useplaceable 0x{o['id']:X}"], "target": key(o)}
    for o in [o for o in living if role == "host" and o["faction"] != 1 and o["distance"] < 12 and tried.get("talk:" + key(o), 0) < 3
              and re.fullmatch(r"[A-Za-z0-9_]{1,31}", o["tag"] or "")][:2]:
        acts[f"talk_{o['id']}"] = {"description": f"Talk to {o['tag']}", "commands": [f"talktag {o['tag']}"], "target": "talk:" + key(o)}

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
    doors = [o for o in objs if o["kind"] == "door" and fresh(o, 2) and not (o["open"] and not o["destination"])]
    for o in sorted(doors, key=lambda o: o["distance"])[:3]:
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
    prefix = f"{me.get('module')}:"
    for k in [k for k in tried if (k.startswith(prefix) and not k.startswith("talk:")) or k.startswith("level:")]:
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

    async def choose(self, role, text, acts):
        replies = [k for k, a in acts.items() if k.startswith("reply_") and "repeat" not in a["description"].lower()]
        if replies:
            return self.rng.choice(replies), {"source": "rules"}
        return next(iter(acts)), {"source": "rules"}


class LayaPolicy:
    name = "laya"

    def __init__(self, session, confidence, fallback):
        self.session, self.confidence, self.fallback = session, confidence, fallback

    async def choose(self, role, text, acts):
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

    def observe(self, host, joiner):
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
        self.persist("module", host["module"] != joiner["module"],
                     f"host in {host['module']}, joiner in {joiner['module']}", states, now)
        self.persist("disconnected", not host["remote_count"] or not joiner["remote_count"],
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


async def run(args, policy):
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=False)
    games = [Game(args.host), Game(args.client)]
    referee = Referee(out, games)
    tried = {"host": {}, "joiner": {}}
    moving, idle = {}, {}
    report = {"policy": policy.name, "steps": [], "modules": [], "counts": collections.Counter()}
    started = time.monotonic()
    try:
        for step in range(args.steps):
            if time.monotonic() - started > args.minutes * 60:
                report["stop_reason"] = "time budget"
                break
            for index, role in enumerate(("host", "joiner")):
                me, peer = games[index].snapshot(), games[1 - index].snapshot()
                if me.get("module") and me["module"] not in report["modules"]:
                    report["modules"].append(me["module"])
                last_pos = moving.get(role)
                moving[role] = me.get("position")
                if (tried[role].get("last", "").startswith(("exit_", "open_", "approach_", "unlock_")) and last_pos and me.get("position")
                        and not me.get("loading") and math.dist(last_pos[:2], me["position"][:2]) > .3):
                    continue  # still walking to the door it was sent to; a new order would cancel it
                if "party_selection" not in (me.get("panels") or []):
                    tried[role].pop("party_ok", None)  # a new party screen starts with plain OK
                if tried[role].get("was_talking") and not me.get("conversation"):
                    refresh_doors(tried[role], me)  # story beats unlock doors
                tried[role]["was_talking"] = bool(me.get("conversation"))
                acts = actions_for(role, me, peer, tried[role])
                if not acts:
                    idle[role] = idle.get(role, 0) + 1
                    if idle[role] >= 3:
                        refresh_doors(tried[role], me)
                        idle[role] = 0
                    continue
                idle[role] = 0
                text = summary(role, me, peer)
                choice, meta = await policy.choose(role, text, acts)
                act = acts[choice]
                now = games[index].snapshot()
                if now.get("loading") or now.get("module") != me.get("module"):
                    continue
                results = []
                for i, command in enumerate(act["commands"]):
                    results.append(games[index].console(command))
                    if act.get("gap") and i + 1 < len(act["commands"]):
                        await asyncio.sleep(act["gap"])  # GUI clicks land one frame-pump apart
                tried[role]["last"] = choice
                if act.get("took") or choice.startswith("search_"):
                    tried[role]["took"] = bool(act.get("took"))
                if act.get("took"):
                    tried[role]["took_any"] = True
                    # New items: offer the equipment screen again for whoever is in control.
                    for k in [k for k in tried[role] if k.startswith("equip:")]:
                        del tried[role][k]
                if act.get("level"):
                    tried[role]["level:" + act["level"]] = 1
                if choice == "ok_summary" or choice.startswith("fight_"):
                    # New experience: a level may be waiting for anyone in the party.
                    for k in [k for k in tried[role] if k.startswith("level:")]:
                        del tried[role][k]
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
            referee.observe(games[0].snapshot(), games[1].snapshot())
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
        (out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        lines = [f"# Co-op playtest ({policy.name}), {report['seconds']} s, stop: {report.get('stop_reason')}", "",
                 f"Areas: {', '.join(report['modules'])}", f"Actions: {dict(report['counts'])}", "",
                 f"## Findings ({len(referee.findings)})", ""]
        lines += [f"- **{f['kind']}** {f['at']}: {f['detail']} (shots: {', '.join(f['shots']) or 'none'})" for f in referee.findings]
        (out / "findings.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
        print("FINAL", json.dumps({k: report[k] for k in ("stop_reason", "modules", "counts", "seconds")} | {"findings": len(referee.findings)}), flush=True)


async def main_async(args):
    rules = RulesPolicy(args.seed)
    if args.policy == "rules":
        await run(args, rules)
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
    parser.add_argument("--policy", choices=("laya", "rules"), default="laya")
    parser.add_argument("--minutes", type=float, default=20)
    parser.add_argument("--steps", type=int, default=400)
    parser.add_argument("--pace", type=float, default=3.0, help="seconds between steps")
    parser.add_argument("--shot-every", type=int, default=10)
    parser.add_argument("--confidence", type=float, default=.4)
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--server", default="laya-kotor-pilot")
    parser.add_argument("--output", required=True, help="a NEW directory for the evidence")
    args = parser.parse_args()
    if not 0 < args.minutes <= 240 or not 1 <= args.steps <= 5000:
        parser.error("minutes 0..240, steps 1..5000")
    asyncio.run(main_async(args))


if __name__ == "__main__":
    main()
