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


def actions_for(role, me, peer, tried):
    """Legal actions for one player, most useful first. role is 'host' or 'joiner'."""
    if me.get("loading") or me.get("hp", 1) <= 0:
        return {}
    acts = {}
    key = lambda o: f"{me['module']}:{o['id']}"
    fresh = lambda o, limit: tried.get(key(o), 0) < limit

    if me.get("menu_open") and not me["conversation"]:
        # A menu owns the input; nothing else can happen until it closes. The container popup
        # has Get Items / Close buttons at fixed 800x600 positions; anything else closes
        # through HideSWInGameGui (`closegui`), which leaves both games running.
        panels = me.get("panels") or []
        if "container" in panels:
            if not tried.get("took"):
                acts["take_items"] = {"description": "Take the items from the open container",
                                      "commands": ["input click 399 391"], "took": True}
            acts["close_menu"] = {"description": "Close the container", "commands": ["input click 399 441"]}
        else:
            acts["close_menu"] = {"description": f"Close the open menu ({', '.join(panels)})", "commands": ["closegui"]}
        return acts

    if me["conversation"]:
        if me["reply_count"]:
            texts = me.get("reply_texts") or []
            for i in range(me["reply_count"]):
                said = texts[i] if i < len(texts) else "(reply text unavailable)"
                acts[f"reply_{i + 1}"] = {"description": f'Answer: "{said}"', "commands": [f"input key {i + 2} tap"]}
        else:
            acts["continue"] = {"description": "Continue the conversation (Space)", "commands": [f"input key {DIK_SPACE} tap"]}
        return acts

    if peer.get("conversation") and me.get("peer_partner_tag") and tried.get("join:" + me["peer_partner_tag"], 0) < 2:
        tag = me["peer_partner_tag"]
        acts[f"join_{tag}"] = {"description": f"Join your partner's conversation with {tag}",
                               "commands": [f"talktag {tag}"], "join": tag}

    objs = me["objects"]
    living = [o for o in objs if o["kind"] == "creature" and o["hp"] > 0 and not o["party"] and not COOP_TAG.match(o["tag"])]
    for o in [o for o in living if o["faction"] == 1 and o["distance"] < 25 and fresh(o, 4)][:2]:
        acts[f"fight_{o['id']}"] = {"description": f"Attack the hostile {o['tag']} {o['distance']:.0f} m away",
                                    "commands": [f"attack 0x{o['id']:X}"], "target": key(o)}
    for o in [o for o in objs if o["kind"] == "placeable" and o["distance"] < 12 and fresh(o, 1)][:2]:
        acts[f"search_{o['id']}"] = {"description": f"Search the {o['tag'] or 'object'} {o['distance']:.0f} m away",
                                     "commands": [f"useplaceable 0x{o['id']:X}"], "target": key(o)}
    for o in [o for o in living if o["faction"] != 1 and o["distance"] < 12 and fresh(o, 1)
              and re.fullmatch(r"[A-Za-z0-9_]{1,31}", o["tag"] or "")][:2]:
        acts[f"talk_{o['id']}"] = {"description": f"Talk to {o['tag']}", "commands": [f"talktag {o['tag']}"], "target": key(o)}

    if role == "joiner":
        if peer.get("module") and peer["module"] != me["module"]:
            return acts  # the transition follow brings us; nothing to walk to
        if tried.get("follow", 0) < 1:
            acts["follow"] = {"description": "Follow your partner", "commands": ["autofollow"]}
        return acts

    # `usedoor <id>` queues the engine's own pathfinding move (around walls, unlike drive keys)
    # and, for an ordinary door, the native click path, so story locks still apply.
    doors = [o for o in objs if o["kind"] == "door" and fresh(o, 2) and not (o["open"] and not o["destination"])]
    for o in sorted(doors, key=lambda o: (not o["destination"], o["distance"]))[:3]:
        if o["distance"] > 38:
            # usedoor refuses past 40 m; walk most of the way first (engine pathfinding, with
            # the plugin's walkmesh guard on the destination). A story-locked route never
            # arrives, so each far door gets three tries.
            if tried.get(key(o) + ":approach", 0) >= 3:
                continue
            x, y = o["position"][0], o["position"][1]
            acts[f"approach_{o['id']}"] = {"description": f"Walk toward the far door {o['tag']}"
                                           + (f" into {o['destination']}" if o["destination"] else "")
                                           + f" ({o['distance']:.0f} m away)",
                                           "commands": [f"walkto {x:.1f} {y:.1f}"], "target": key(o) + ":approach"}
        elif o["destination"]:
            acts[f"exit_{o['id']}"] = {"description": f"Go through the door {o['tag']} into {o['destination']} "
                                       f"({o['distance']:.0f} m away)", "commands": [f"usedoor 0x{o['id']:X}"], "target": key(o)}
        else:
            acts[f"open_{o['id']}"] = {"description": f"Go to and open the door {o['tag']} ({o['distance']:.0f} m away)",
                                       "commands": [f"usedoor 0x{o['id']:X}"], "target": key(o)}
    return acts


def summary(role, me, peer):
    """What the policy is told about one player's situation."""
    if me.get("loading"):
        return f"{role}: loading"
    hostile = sum(1 for o in me["objects"] if o["kind"] == "creature" and o["faction"] == 1 and o["hp"] > 0 and not o["party"])
    text = (f"You are the {role} in area {me['module']}, health {me['hp']}, {hostile} hostile(s) nearby. "
            f"Partner is in {peer.get('module', 'unknown')}" + (", in a conversation" if peer.get("conversation") else "") + ".")
    if me["conversation"]:
        text += f' In a conversation. Last line: "{me.get("dialogue_line", "")[:160]}".'
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
            census = []
            for s in states:
                census.append(collections.Counter(o["tag"] for o in s["objects"] if o["kind"] == "creature"
                                                  and o["hp"] > 0 and not o["party"] and not COOP_TAG.match(o["tag"])))
            diff = (census[0] - census[1]) + (census[1] - census[0])
            self.persist("census", bool(diff), f"living creatures differ in {host['module']}: "
                         f"host-only {dict(census[0] - census[1])}, joiner-only {dict(census[1] - census[0])}", states, now)


async def run(args, policy):
    out = Path(args.output).resolve()
    out.mkdir(parents=True, exist_ok=False)
    games = [Game(args.host), Game(args.client)]
    referee = Referee(out, games)
    tried = {"host": {}, "joiner": {}}
    moving = {}
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
                if (tried[role].get("last", "").startswith(("exit_", "open_", "approach_")) and last_pos and me.get("position")
                        and not me.get("loading") and math.dist(last_pos[:2], me["position"][:2]) > .3):
                    continue  # still walking to the door it was sent to; a new order would cancel it
                acts = actions_for(role, me, peer, tried[role])
                if not acts:
                    continue
                text = summary(role, me, peer)
                choice, meta = await policy.choose(role, text, acts)
                act = acts[choice]
                now = games[index].snapshot()
                if now.get("loading") or now.get("module") != me.get("module"):
                    continue
                results = []
                for i, command in enumerate(act["commands"]):
                    results.append(games[index].console(command))
                    if i == 0 and act.get("hold"):
                        await asyncio.sleep(act["hold"])
                tried[role]["last"] = choice
                if act.get("took") or choice.startswith("search_"):
                    tried[role]["took"] = bool(act.get("took"))
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
