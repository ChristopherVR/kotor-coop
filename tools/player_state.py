"""Capture one player's personal state on a running rig, for crash/recovery comparisons.

  python tools/player_state.py <pid> <agent log> [--out state.json]
  python tools/player_state.py --diff before.json after.json

The state is: party gold (CSWPartyTable+0xFC through the server, read out of process), the
player's experience (laya_playtest's reader), and the player's own items with stack, charges and
upgrades (the `items` console verb's "yours" rows, read back from that agent's log). Item object
ids are left out on purpose: a rejoin reloads the world and renumbers them.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from collections import Counter

from laya_playtest import Game

ROW = re.compile(r"\*\s+0x[0-9A-F]+\s+(\S+)\s+yours(?: \[pool\])?\s+stack (\d+)\s+charges (\d+)/(\d+)\s+upgrades (0x[0-9A-F]+)")


def gold(game):
    manager = game.u32(0x7A39FC)
    server_internal = game.u32(game.u32(manager + 8) + 4)
    return game.u32(server_internal + 0x1B770 + 0xFC)


def items(game, agent_log, settle=3.0):
    with open(agent_log, encoding="utf-8", errors="replace") as f:
        before = len(f.readlines())
    # Not game.console: the playtest allowlist deliberately leaves diagnostics out.
    subprocess.run(["pwsh", "-NoProfile", "-File", os.path.join(os.path.dirname(os.path.abspath(__file__)), "blockpoke.ps1"),
                    "-ProcId", str(game.pid), "console", "items 500"], capture_output=True, check=False)
    time.sleep(settle)
    with open(agent_log, encoding="utf-8", errors="replace") as f:
        lines = f.readlines()[before:]
    rows = Counter()
    for line in lines:
        m = ROW.search(line)
        if m:
            resref, stack, charges, max_charges, upgrades = m.groups()
            rows[f"{resref} stack {stack} charges {charges}/{max_charges} upgrades {upgrades}"] += 1
    return dict(sorted(rows.items()))


def capture(pid, agent_log):
    game = Game(pid)
    snap = game.snapshot()
    return {"gold": gold(game), "xp": snap.get("xp"), "module": snap.get("module"), "items": items(game, agent_log)}


def diff(a, b):
    out = []
    for key in ("gold", "xp", "module"):
        if a.get(key) != b.get(key):
            out.append(f"{key}: {a.get(key)} -> {b.get(key)}")
    for name in sorted(set(a["items"]) | set(b["items"])):
        if a["items"].get(name, 0) != b["items"].get(name, 0):
            out.append(f"item {name}: x{a['items'].get(name, 0)} -> x{b['items'].get(name, 0)}")
    return out


def main(argv):
    if argv and argv[0] == "--diff":
        a, b = (json.load(open(p, encoding="utf-8")) for p in argv[1:3])
        lines = diff(a, b)
        print("\n".join(lines) if lines else "identical")
        return 0
    state = capture(int(argv[0]), argv[1])
    text = json.dumps(state, indent=1)
    if "--out" in argv:
        with open(argv[argv.index("--out") + 1], "w", encoding="utf-8") as f:
            f.write(text)
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
