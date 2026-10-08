"""Closed-loop player walking for the playtest harness: drive keys only, as a player walks.

Measured on the rig (2026-10-08): with `isolate_real_input`, `input key` W walks the player
character in the CAMERA's direction and the character ends up facing that way; A/D turn the
camera (A counter-clockwise, D clockwise) at roughly 120-170 degrees per second. So a step
is: read the heading after a short W, turn the camera by the error, walk a bounded stretch,
re-read. `walk_path` follows kotor_nav waypoints and gives up on a leg that stops moving.
"""
from __future__ import annotations

import math
import time

DIK_W, DIK_A, DIK_D = 17, 30, 32
TURN_DEG_PER_S = 140.0
ARRIVE = 1.2          # metres from a waypoint that count as arrived
ALIGNED = 18.0        # degrees of heading error that are walked on, not turned
STUCK_STEPS = 4       # consecutive walk stretches that gain under 0.3 m


def _heading(state):
    ox, oy = state["orientation"][:2]
    return math.degrees(math.atan2(oy, ox))


def _press(game, key, seconds):
    game.console(f"input key {key} down")
    time.sleep(max(0.05, seconds))
    game.console(f"input key {key} up")


def walk_to(game, target, timeout=40.0, log=None):
    """Walk the controlled character to (x, y). True on arrival, False when stuck or out of
    time (a closed door, a wall, a held player, a conversation)."""
    start = time.monotonic()
    stuck, last = 0, None
    _press(game, DIK_W, 0.15)  # the heading only follows the camera once the character moves
    time.sleep(0.25)
    while time.monotonic() - start < timeout:
        s = game.snapshot()
        if s.get("loading") or s.get("conversation") or s.get("menu_open"):
            return False
        pos = s["position"][:2]
        dist = math.dist(pos, target)
        if dist <= ARRIVE:
            return True
        want = math.degrees(math.atan2(target[1] - pos[1], target[0] - pos[0]))
        err = (want - _heading(s) + 540) % 360 - 180
        if abs(err) > ALIGNED:
            _press(game, DIK_A if err > 0 else DIK_D, abs(err) / TURN_DEG_PER_S)
            time.sleep(0.15)
            _press(game, DIK_W, 0.12)
            time.sleep(0.2)
            continue
        _press(game, DIK_W, min(0.9, dist / 4.5))
        time.sleep(0.2)
        moved = last is not None and math.dist(last, pos) >= 0.3
        stuck = 0 if (last is None or moved) else stuck + 1
        last = pos
        if stuck >= STUCK_STEPS:
            if log:
                log(f"walk: stuck at {tuple(round(v, 1) for v in pos)} heading for {tuple(round(v, 1) for v in target)}")
            return False
    return False


def walk_path(game, waypoints, log=None):
    """Follow waypoints (from kotor_nav.Area.path). Returns how many were reached."""
    reached = 0
    for point in waypoints[1:]:
        if not walk_to(game, point, log=log):
            break
        reached += 1
    return reached
