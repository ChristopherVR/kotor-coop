"""The boss's animations, written as poses over time and sampled into keyframes.

Each animation is a function of time that says how the skeleton is posed: bodies and heads
by small turns, arms by where the wrist should be (the elbow is worked out), fingers by how
far they are curled. Everything is measured from the rest pose in sculpt.py.

The names are the ones the game looks for on a single-model creature (checked against c_gizka,
c_rancor and c_gammorean), so the engine plays them by itself for standing, walking, attacking,
being hit and dying. The gauntlet powers ride on the one-shots the engine can be told to play
on a creature model, a short fixed list (docs/research/chungus-boss.md): ctaunt, cvictory, the
two head turns, usecomp and the looping "disabled".
"""
from __future__ import annotations

import math

import numpy as np

import geom
import kmdl
import model
import sculpt

FPS = 15

REST = {name: np.array(position, dtype=float) for name, (_, position) in
        {**sculpt.JOINTS, **sculpt.gauntlet_joints()}.items()}
ALONG, ACROSS, BACK = sculpt.hand_frame()
HAND_REST = geom.frame(ALONG, BACK)

UP = (0.0, 0.0, 1.0)
FORWARD = (0.0, 1.0, 0.0)

GLOW_IDLE = (0.35, 0.35, 0.35)
WHITE = (1.0, 1.0, 1.0)
PURPLE = (0.85, 0.45, 1.0)
BLUE = (0.45, 0.65, 1.0)
RED = (1.0, 0.4, 0.4)
ORANGE = (1.0, 0.7, 0.3)
GREEN = (0.45, 1.0, 0.55)
YELLOW = (1.0, 0.95, 0.45)


# ---- helpers for writing motion

def wave(t, period, phase=0.0):
    return math.sin(2 * math.pi * (t / period + phase))


def kf(t, *keys):
    """Value at time t on a curve through (time, value) points, easing in and out of each one."""
    if t <= keys[0][0]:
        return keys[0][1]
    for (t0, v0), (t1, v1) in zip(keys, keys[1:]):
        if t <= t1:
            f = (t - t0) / (t1 - t0)
            f = f * f * (3 - 2 * f)
            if isinstance(v0, tuple):
                return tuple(a + (b - a) * f for a, b in zip(v0, v1))
            return v0 + (v1 - v0) * f
    return keys[-1][1]


def bump(t, at, width):
    return math.exp(-((t - at) / width) ** 2)


def scaled(colour, amount):
    return tuple(min(c * amount, 1.0) for c in colour)


def _arm(upper, fore, hand, wrist, elbow_hint, hand_frame=None, hand_rest=None):
    """Poses a two-bone arm so its wrist is at a point (in the body's own space).

    Returns the three bones' turns. elbow_hint is the side the elbow should stick out to."""
    shoulder, elbow0, wrist0 = REST[upper], REST[fore], REST[hand]
    l1, l2 = np.linalg.norm(elbow0 - shoulder), np.linalg.norm(wrist0 - elbow0)
    reach = np.asarray(wrist, dtype=float) - shoulder
    d = np.linalg.norm(reach)
    d = min(max(d, abs(l1 - l2) + 1e-3), l1 + l2 - 1e-3)
    direction = reach / np.linalg.norm(reach)
    target = shoulder + direction * d
    a = (l1 * l1 - l2 * l2 + d * d) / (2 * d)
    h = math.sqrt(max(l1 * l1 - a * a, 0.0))
    hint = np.asarray(elbow_hint, dtype=float)
    hint = hint - direction * (hint @ direction)
    hint /= max(np.linalg.norm(hint), 1e-9)
    elbow = shoulder + direction * a + hint * h

    q_upper = geom.q_between(elbow0 - shoulder, elbow - shoulder)
    r_upper = geom.q_matrix(q_upper)
    q_fore = geom.q_between(wrist0 - elbow0, r_upper.T @ (target - elbow))
    out = {upper: {"quat": q_upper}, fore: {"quat": q_fore}}
    if hand_frame is not None:
        r_parent = r_upper @ geom.q_matrix(q_fore)
        wanted = geom.frame(*hand_frame) @ hand_rest.T
        out[hand] = {"quat": geom.q_from_matrix(r_parent.T @ wanted)}
    return out


def gauntlet(wrist, along=None, back=None, elbow=(-1.0, -0.4, -0.5)):
    """The gauntlet arm: wrist position, and optionally which way the fingers and the back of
    the hand point."""
    frame = (along, back) if along is not None else None
    return _arm("uparmg", "forearmg", "handg", wrist, elbow, frame, HAND_REST)


def free_arm(wrist, elbow=(1.0, -0.5, -0.3)):
    return _arm("uparmr", "forearmr", "handr", wrist, elbow)


def fingers(curl=0.6, thumb=None, index=None, middle=None):
    """How far the gauntlet's fingers are curled: 0 open, 1 a fist. index/middle override
    those two (for pointing and for the snap)."""
    pose = {}
    for name in sculpt.FINGERS:
        amount = {"index": index, "middle": middle}.get(name)
        amount = curl if amount is None else amount
        pose[f"g{name}1"] = {"quat": geom.q_axis(tuple(ACROSS), -80.0 * amount)}
        pose[f"g{name}2"] = {"quat": geom.q_axis(tuple(ACROSS), -85.0 * amount)}
    amount = curl if thumb is None else thumb
    pose["gthumb1"] = {"quat": geom.q_mul(geom.q_axis(tuple(ALONG), -55.0 * amount), geom.q_axis(tuple(BACK), 25.0 * amount))}
    pose["gthumb2"] = {"quat": geom.q_axis(tuple(ALONG), -45.0 * amount)}
    return pose


def ears(t, swing=0.0, flop=0.0, period=1.0, splay=0.0):
    """Both ears: swing is a back-and-forth in degrees that travels up the ear with a delay,
    flop a steady lean backwards, splay a lean outwards."""
    pose = {}
    for side, name in ((-1, "earl"), (1, "earr")):
        for k in (1, 2, 3):
            s = swing * wave(t, period, -0.12 * k + (0.07 if side > 0 else 0.0)) * (0.6 + 0.25 * k)
            pose[f"{name}{k}"] = {"rot": (flop * (0.5 + 0.25 * k) + s, side * splay * (0.4 + 0.3 * k), 0.0)}
    return pose


def lids(shut=0.0):
    """0 is the half-closed rest, 1 is shut, negative opens the eyes wide."""
    angle = -(sculpt.LID_SHUT_DEGREES - sculpt.LID_REST_DEGREES) * shut if shut >= 0 else sculpt.LID_REST_DEGREES * -shut
    return {"lidl": {"rot": (angle, 0, 0)}, "lidr": {"rot": (angle, 0, 0)}}


def blink(t, *times):
    return lids(max((bump(t, at, 0.07) for at in times), default=0.0))


def jaw(open_=0.0):
    return {"jaw": {"rot": (-22.0 * open_, 0, 0)}}


def blend(*poses):
    """Lays poses over each other: positions and angles add, everything else takes the last."""
    out = {}
    for pose in poses:
        for node, parts in pose.items():
            target = out.setdefault(node, {})
            for key, value in parts.items():
                if key in ("pos", "rot") and key in target:
                    target[key] = tuple(a + b for a, b in zip(target[key], value))
                else:
                    target[key] = value
    return out


def breathe(t, period=3.0, depth=1.0):
    """The slow in-and-out every standing pose shares: the belly swells, the chest lifts."""
    b = wave(t, period) * depth
    return {
        "belly": {"pos": (0, 0.030 * b, 0.010 * b)},
        "chest": {"pos": (0, 0.006 * b, 0.012 * b), "rot": (0.8 * b, 0, 0)},
        "neck": {"rot": (-0.8 * b, 0, 0)},
    }


GUARD = (-0.86, 0.84, 1.62)          # where the gauntlet is held at ease
HANG = (1.20, 0.40, 0.84)            # the free hand at rest


def stand(t, period=3.0, depth=1.0):
    """Standing at ease: breathing, the gauntlet held in front of the chest, a loose fist."""
    return blend(breathe(t, period, depth),
                 gauntlet(GUARD, (0.25, 0.45, 0.85), (-0.3, 0.9, -0.3)),
                 free_arm(HANG), fingers(0.75), lids(0.0), ears(t, 1.5, 0.0, period),
                 {"stones": {"glow": GLOW_IDLE}})


# ---- the animations

def cpause1(t):
    return blend(stand(t), blink(t, 1.3), {"stones": {"glow": scaled(WHITE, 0.33 + 0.07 * wave(t, 3.0))}})


def cpause2(t):
    # Looks about, flicks an ear, turns the gauntlet over to admire it.
    look = kf(t, (0, 0), (0.6, 24), (1.3, 24), (2.0, -20), (2.6, -20), (3.2, 0))
    admire = kf(t, (0, 0), (1.9, 0), (2.3, 1), (2.9, 1), (3.2, 0))
    flick = 24 * bump(t, 1.0, 0.1) + 24 * bump(t, 1.22, 0.1)
    return blend(stand(t, 3.2), blink(t, 0.5, 2.1),
                 {"neck": {"rot": (0, 0, look * 0.4)}, "head": {"rot": (4 * admire, 0, look * 0.6)},
                  "earl2": {"rot": (0, -flick, 0)}, "earl3": {"rot": (0, -flick, 0)}},
                 gauntlet(tuple(np.array(GUARD) + np.array((0.05, 0.05, 0.22)) * admire),
                          (0.25, 0.45 - 0.2 * admire, 0.85), (-0.3 + 0.6 * admire, 0.9, -0.3)),
                 fingers(0.75 - 0.5 * admire),
                 {"stones": {"glow": scaled(WHITE, 0.35 + 0.3 * admire)}})


def creadyr(t):
    sway = wave(t, 1.6)
    return blend(breathe(t, 1.6, 0.7),
                 {"rootdummy": {"rot": (-5, 2.5 * sway, 2 * sway), "pos": (0.03 * sway, 0, -0.03)},
                  "chest": {"rot": (-4, 0, 0)}, "head": {"rot": (-6, 0, -2 * sway)}},
                 gauntlet((-0.74 + 0.03 * sway, 0.92, 1.86 + 0.03 * wave(t, 1.6, 0.25)), (0.15, 0.25, 0.95), (-0.2, 0.95, -0.2)),
                 free_arm((1.05, 0.70, 1.10)), fingers(1.0), lids(0.25), ears(t, 3.0, -14.0, 1.6),
                 {"stones": {"glow": scaled(WHITE, 0.5 + 0.15 * wave(t, 0.8))}})


def _gait(t, period, stride, lift, roll, lean, ear_flop, arm_swing, hurry=0.0):
    """A waddle: the weight rolls over the planted foot while the other swings through."""
    phase = t / period
    s = wave(t, period)
    pose = blend(
        {"rootdummy": {"rot": (lean, roll * s, -roll * 0.5 * s),
                       "pos": (0.05 * s, 0, 0.045 * abs(wave(t, period, 0.25)) * (1 + hurry))},
         "belly": {"pos": (0.02 * s, 0.02 * wave(t, period / 2, 0.1), 0.035 * wave(t, period / 2, 0.2) * (1 + hurry))},
         "chest": {"rot": (-lean * 0.4, -roll * 0.5 * s, roll * 0.6 * s)},
         "head": {"rot": (-lean * 0.5, -roll * 0.3 * s, roll * 0.3 * s)},
         "tail": {"rot": (0, 0, 10 * s)}},
        gauntlet((GUARD[0], GUARD[1] + 0.05 * s, GUARD[2] + 0.04 * abs(s)), (0.25, 0.45, 0.85), (-0.3, 0.9, -0.3)),
        free_arm((HANG[0] + 0.03, HANG[1] - arm_swing * s, HANG[2] + 0.05 * abs(s))),
        fingers(0.8), lids(0.0), ears(t, 7.0 * (1 + hurry), ear_flop, period / 2),
        {"stones": {"glow": GLOW_IDLE}})
    for name, offset, side in (("l", 0.0, -1), ("r", 0.5, 1)):
        f = (phase + offset) % 1.0
        if f < 0.5:                       # planted, sliding back under the body
            forward, height, pitch = stride * (0.5 - 2 * f), 0.0, 0.0
        else:                             # swinging forward through the air
            u = (f - 0.5) * 2
            forward = stride * (-0.5 + u)
            height = lift * math.sin(math.pi * u)
            pitch = 20 * math.sin(math.pi * u) - 12 * u
        pose["foot" + name] = {"pos": (0, forward, height), "rot": (pitch, 0, 0)}
        pose["thigh" + name] = {"pos": (0, forward * 0.35, height * 0.3), "rot": (-forward * 25, 0, 0)}
    return pose


def cwalk(t):
    # He does not walk. He bounces: the whole of him leaves the floor, comes down on his belly
    # and springs off it again, twice a cycle.
    period = 1.2
    u = (t / (period / 2)) % 1.0
    air = math.sin(math.pi * u)                       # 0 on the floor, 1 at the top
    squash = max(0.0, 1.0 - u * 8) + max(0.0, 1.0 - (1.0 - u) * 8)   # just after and before landing
    side = 1 if int(t / (period / 2)) % 2 == 0 else -1
    return blend({"rootdummy": {"pos": (0, 0, 0.55 * air - 0.10 * squash), "rot": (-6 + 10 * air, 5 * side * air, 0)},
                  "belly": {"pos": (0, 0.03 * squash, -0.10 * air + 0.12 * squash)},
                  "chest": {"pos": (0, 0, -0.06 * squash), "rot": (-4 * squash, 0, 0)},
                  "head": {"rot": (8 * air - 6 * squash, 0, -4 * side * air)},
                  "thighl": {"rot": (-30 * air, 0, 0)}, "thighr": {"rot": (-30 * air, 0, 0)},
                  "footl": {"rot": (-25 * air + 15 * squash, 0, 0), "pos": (0, 0, 0.08 * air)},
                  "footr": {"rot": (-25 * air + 15 * squash, 0, 0), "pos": (0, 0, 0.08 * air)},
                  "tail": {"rot": (20 * air, 0, 0)}},
                 gauntlet((GUARD[0] - 0.12 * air, GUARD[1], GUARD[2] + 0.35 * air), (0.25, 0.45, 0.85), (-0.3, 0.9, -0.3)),
                 free_arm((HANG[0] + 0.22 * air, HANG[1], HANG[2] + 0.45 * air)),
                 fingers(0.8), lids(-0.5 * air), ears(t, 0.0, -30 * air + 26 * squash, 1.0, splay=12 * air),
                 {"stones": {"glow": GLOW_IDLE}})


ROLL_PERIOD = 1.0


def crun(t):
    # He does not run either. He tucks in and rolls at you like a boulder: one whole turn a
    # cycle, bumping as his feet and ears come round.
    turn = -360.0 * (t / ROLL_PERIOD)                 # head over heels, forwards
    bump_ = abs(math.sin(2 * math.pi * t / ROLL_PERIOD))
    return blend({"rootdummy": {"rot": (turn, 0, 0), "pos": (0, 0, 0.05 + 0.10 * bump_)},
                  "belly": {"pos": (0, 0, 0.03 * wave(t, ROLL_PERIOD / 2))},
                  "chest": {"rot": (-14, 0, 0)}, "neck": {"rot": (-16, 0, 0)}, "head": {"rot": (-22, 0, 0)},
                  "thighl": {"rot": (-40, 0, 0)}, "thighr": {"rot": (-40, 0, 0)},
                  "footl": {"rot": (-35, 0, 0)}, "footr": {"rot": (-35, 0, 0)}},
                 gauntlet((-0.62, 0.86, 1.42), (0.7, 0.4, 0.3), (-0.2, 0.9, 0.3)), free_arm((0.62, 0.88, 1.30)),
                 fingers(1.0), lids(1.0), ears(t, 4.0, 62.0, ROLL_PERIOD / 2, splay=8),
                 {"stones": {"glow": scaled(WHITE, 0.6)}})


def cwalkinj(t):
    return blend(_gait(t, 1.4, 0.30, 0.08, 4.0, -8.0, 16.0, 0.08),
                 {"head": {"rot": (-10, 0, 6)}}, free_arm((0.55, 0.95, 1.05)), lids(0.5),
                 {"stones": {"glow": scaled(WHITE, 0.15)}})


def g0a1(t):
    # The punch: the gauntlet drawn back past the shoulder, then driven straight through. Hit at 0.5 s.
    twist = kf(t, (0, 0), (0.30, -22), (0.50, 24), (0.80, 18), (1.5, 0))
    lean = kf(t, (0, 0), (0.30, 5), (0.50, -13), (0.85, -9), (1.5, 0))
    lunge = kf(t, (0, 0), (0.30, -0.10), (0.50, 0.30), (0.85, 0.20), (1.5, 0))
    wrist = kf(t, (0, GUARD), (0.30, (-1.20, 0.10, 1.80)), (0.50, (-0.30, 1.28, 1.62)), (0.85, (-0.36, 1.18, 1.60)), (1.5, GUARD))
    flash = bump(t, 0.5, 0.1)
    return blend(breathe(t, 1.5, 0.3),
                 {"rootdummy": {"rot": (lean, 0, twist), "pos": (0, lunge, 0)},
                  "chest": {"rot": (lean * 0.4, 0, twist * 0.5)}, "head": {"rot": (-lean * 0.6, 0, -twist * 0.9)},
                  "belly": {"pos": (0, 0.06 * bump(t, 0.58, 0.12), -0.03 * bump(t, 0.58, 0.12))}},
                 gauntlet(wrist, (0.2, 1.0, 0.1), (-0.2, 0.1, 1.0), elbow=(-1.0, -0.6, 0.2)),
                 free_arm(kf(t, (0, HANG), (0.30, (0.95, 0.85, 1.25)), (0.50, (1.25, -0.10, 1.05)), (1.5, HANG))),
                 fingers(1.0), lids(0.3), jaw(0.5 * bump(t, 0.5, 0.15)),
                 ears(t, 0.0, kf(t, (0, 0), (0.30, -10), (0.52, 28), (0.9, -6), (1.5, 0)), 1.0),
                 {"stones": {"glow": scaled(PURPLE, 0.4 + 0.6 * flash)}, "g_handg": {"glow": scaled(PURPLE, 0.3 * flash)}})


def g0a2(t):
    # The backhand: the gauntlet swept across his body and back through whoever is in front of
    # him, knuckles and stones first. Hit at 0.5 s.
    twist = kf(t, (0, 0), (0.30, 26), (0.50, -30), (0.80, -22), (1.5, 0))
    wrist = kf(t, (0, GUARD), (0.30, (0.35, 0.95, 1.85)), (0.50, (-1.05, 0.95, 1.70)), (0.85, (-1.20, 0.60, 1.62)), (1.5, GUARD))
    flash = bump(t, 0.5, 0.1)
    hop = 0.10 * bump(t, 0.42, 0.12)
    return blend(breathe(t, 1.5, 0.3),
                 {"rootdummy": {"rot": (-4 * bump(t, 0.5, 0.2), 0, twist), "pos": (0, 0.18 * bump(t, 0.5, 0.2), hop)},
                  "belly": {"pos": (0.06 * bump(t, 0.56, 0.12), 0, 0)},
                  "chest": {"rot": (0, 0, twist * 0.5)}, "head": {"rot": (0, 0, -twist * 0.9)}},
                 gauntlet(wrist, (-0.5, 0.6, 0.6), (-0.8, 0.5, 0.2), elbow=(-0.3, -0.8, 0.5)),
                 free_arm(kf(t, (0, HANG), (0.30, (1.30, 0.20, 1.20)), (0.50, (1.05, 0.80, 1.10)), (1.5, HANG))),
                 fingers(1.0), lids(0.3), jaw(0.5 * bump(t, 0.5, 0.15)),
                 ears(t, 0.0, 0.0, 1.0, splay=-twist * 0.8),
                 {"stones": {"glow": scaled(ORANGE, 0.4 + 0.6 * flash)}, "g_handg": {"glow": scaled(ORANGE, 0.3 * flash)}})


def cdodgeg(t):
    side = kf(t, (0, 0), (0.25, 0.50), (0.6, 0.45), (1.0, 0))
    tip = kf(t, (0, 0), (0.25, 17), (0.6, 12), (1.0, 0))
    return blend(stand(t, 1.0, 0.3),
                 {"rootdummy": {"pos": (side, -0.08 * side / 0.5, 0.08 * math.sin(math.pi * min(t / 0.5, 1.0))), "rot": (3, tip, 0)},
                  "head": {"rot": (0, -tip * 0.8, 0)}, "belly": {"pos": (-0.05 * side, 0, 0)}},
                 ears(t, 0.0, 0.0, 1.0, splay=-tip * 0.8), lids(-0.6 * bump(t, 0.25, 0.2)))


def cdamages(t):
    jolt = kf(t, (0, 0), (0.1, 1), (0.35, 0))
    return blend(stand(t, 3.0, 0.0),
                 {"rootdummy": {"rot": (9 * jolt, 0, 4 * jolt), "pos": (0, -0.07 * jolt, 0)},
                  "belly": {"pos": (0, -0.07 * jolt, 0.02 * jolt)}, "chest": {"rot": (5 * jolt, 0, 0)},
                  "head": {"rot": (12 * jolt, 0, -6 * jolt)}},
                 ears(t, 0.0, -16 * jolt, 1.0, splay=10 * jolt), lids(0.9 * jolt), jaw(0.6 * jolt),
                 {"stones": {"glow": scaled(RED, 0.35 + 0.5 * jolt)}})


def _on_back(amount):
    """0 standing, 1 flat on his back with the feet in the air."""
    return blend(
        {"rootdummy": {"rot": (86 * amount, 0, 0), "pos": (0, -0.35 * amount, -0.12 * amount)},
         "chest": {"rot": (-6 * amount, 0, 0)}, "head": {"rot": (-14 * amount, 0, 8 * amount)},
         "thighl": {"rot": (-22 * amount, 0, 0)}, "thighr": {"rot": (-22 * amount, 0, 0)},
         "footl": {"rot": (-18 * amount, 0, 0)}, "footr": {"rot": (-18 * amount, 0, 0)}},
        gauntlet(tuple(np.array(GUARD) + np.array((-0.35, -0.25, 0.25)) * amount), (0.25 - 0.5 * amount, 0.45, 0.85), (-0.3, 0.9, -0.3)),
        free_arm(tuple(np.array(HANG) + np.array((0.25, 0.1, 0.55)) * amount)),
        ears(0.0, 0.0, -50 * amount, 1.0, splay=16 * amount), fingers(0.75 - 0.5 * amount))


def cdie(t):
    # Sways, tips over backwards, lands with a bounce, and the stones go dark.
    sway = kf(t, (0, 0), (0.25, -6), (0.55, 8), (0.8, 0))
    fall = kf(t, (0, 0), (0.7, 0.03), (1.25, 1.0), (2.4, 1.0))
    after = max(t - 1.25, 0.0)
    bounce = 0.16 * math.exp(-after * 5) * abs(math.sin(after * 9)) if t > 1.25 else 0.0
    jiggle = math.exp(-after * 3.5) * math.sin(after * 20) if t > 1.25 else 0.0
    return blend(_on_back(fall),
                 {"rootdummy": {"rot": (sway, 0, sway * 0.5), "pos": (0, 0, bounce)},
                  "belly": {"pos": (0, 0.05 * jiggle, 0.07 * jiggle)}},
                 lids(kf(t, (0, -0.8), (0.6, -0.8), (1.3, 1.0), (2.4, 1.0))), jaw(kf(t, (0, 0.2), (0.7, 0.9), (1.5, 0.5), (2.4, 0.5))),
                 {"stones": {"glow": scaled(WHITE, kf(t, (0, 0.35), (0.5, 0.9), (0.9, 0.1), (1.2, 0.6), (1.6, 0.0), (2.4, 0.0)))}})


def cdead(t):
    return blend(_on_back(1.0), lids(1.0), jaw(0.5), {"stones": {"glow": (0.0, 0.0, 0.0)}})


def ckdbck(t):
    fall = kf(t, (0, 0), (0.45, 1.0), (1.3, 1.0))
    after = max(t - 0.45, 0.0)
    bounce = 0.14 * math.exp(-after * 5) * abs(math.sin(after * 9)) if t > 0.45 else 0.0
    return blend(_on_back(fall), {"rootdummy": {"pos": (0, 0, bounce)},
                                  "belly": {"pos": (0, 0, 0.06 * math.exp(-after * 3.5) * math.sin(after * 20) if t > 0.45 else 0.0)}},
                 lids(0.6), {"stones": {"glow": scaled(WHITE, 0.15)}})


def ckdbcklp(t):
    kick = 12 * wave(t, 0.7)
    return blend(_on_back(1.0), {"thighl": {"rot": (kick, 0, 0)}, "thighr": {"rot": (-kick, 0, 0)},
                                 "belly": {"pos": (0, 0, 0.015 * wave(t, 0.7))}},
                 lids(0.3), {"stones": {"glow": scaled(WHITE, 0.15)}})


def cgustandb(t):
    up = kf(t, (0, 1.0), (0.25, 1.0), (0.85, 0.0), (1.0, 0.0))
    return blend(_on_back(up), lids(0.2 * up), {"stones": {"glow": scaled(WHITE, 0.35 - 0.2 * up)}})


def ctaunt(t):
    # The snap. The gauntlet rises beside his head, the stones light one after another, finger and
    # thumb press together, and at 1.3 s they snap: a white flash, and the jolt runs through him.
    up = kf(t, (0, 0), (0.7, 1.0), (1.75, 1.0), (2.4, 0.0))
    press = kf(t, (0, 0), (0.55, 0.0), (0.95, 1.0), (1.27, 1.0), (1.33, 0.0), (2.4, 0.0))
    snapped = kf(t, (0, 0), (1.27, 0.0), (1.34, 1.0), (1.9, 1.0), (2.4, 0.0))
    flash = bump(t, 1.33, 0.09)
    jolt = bump(t, 1.37, 0.12)
    charge = kf(t, (0, 0.35), (0.7, 0.6), (1.28, 1.0), (1.6, 0.8), (2.4, 0.35))
    colours = [PURPLE, BLUE, RED, ORANGE, GREEN, YELLOW]
    colour = colours[min(int(max(t - 0.2, 0.0) / 0.18), 5)] if t < 1.28 else WHITE
    wrist = tuple(np.array(GUARD) + (np.array((-1.02, 0.52, 2.42)) - np.array(GUARD)) * up)
    return blend(breathe(t, 2.4, 0.3),
                 {"rootdummy": {"rot": (3 * up - 6 * jolt, -2 * up, 8 * up), "pos": (0, 0, 0.02 * up - 0.05 * jolt)},
                  "belly": {"pos": (0, 0.05 * jolt, -0.03 * jolt)},
                  "chest": {"rot": (2 * up, 0, 4 * up)}, "head": {"rot": (6 * up, 0, -14 * up)}},
                 gauntlet(wrist, (0.25 * (1 - up), 0.45 * (1 - up) + 0.05, 0.85 + 0.15 * up), (-0.3 + 0.2 * up, 0.9, -0.3 + 0.2 * up),
                          elbow=(-1.0, -0.2, -0.6)),
                 free_arm(tuple(np.array(HANG) + np.array((-0.05, 0.25, 0.1)) * up)),
                 # ring and little finger folded away; the middle finger meets the thumb, then
                 # slaps down into the palm as the index flicks out
                 fingers(0.75 + 0.25 * up, thumb=0.75 + 0.2 * press - 0.5 * snapped,
                         index=0.75 - 0.55 * up + 0.1 * press - 0.2 * snapped, middle=0.75 - 0.2 * press + 0.25 * snapped),
                 lids(0.25 * up - 0.5 * jolt), ears(t, 0.0, -6 * up + 22 * jolt, 1.0, splay=6 * jolt),
                 {"stones": {"glow": tuple(min(1.0, c * charge + flash) for c in colour)},
                  "g_handg": {"glow": scaled(WHITE, 0.5 * flash)}})


def cvictory(t):
    # A belly laugh: head back, mouth open, the belly bouncing, the gauntlet resting on it.
    on = kf(t, (0, 0), (0.35, 1), (2.0, 1), (2.5, 0))
    ha = wave(t, 0.26) * on
    return blend({"rootdummy": {"rot": (7 * on, 0, 0), "pos": (0, 0, 0.03 * ha)},
                  "belly": {"pos": (0, 0.05 * ha, 0.06 * ha)},
                  "chest": {"rot": (5 * on + 2 * ha, 0, 0), "pos": (0, 0, 0.02 * ha)},
                  "head": {"rot": (16 * on + 4 * ha, 0, 5 * on)}},
                 gauntlet(tuple(np.array(GUARD) + np.array((0.22, 0.10, -0.28)) * on), (0.6, 0.5, 0.4), (-0.4, 0.8, 0.3)),
                 free_arm(tuple(np.array(HANG) + (np.array((0.72, 0.98, 1.18)) - np.array(HANG)) * on)),
                 fingers(0.5), lids(0.6 * on), jaw(0.55 * on + 0.35 * max(ha, 0.0)),
                 ears(t, 5.0 * on, -18 * on, 0.26),
                 {"stones": {"glow": scaled(YELLOW, 0.4 + 0.35 * on)}})


def disabled(t):
    # The power stone: the arm is levelled at the target, fingers splayed, and it shakes as the
    # lightning pours out (it lands at 0.9 s). Held by the engine for as long as it is asked.
    aim = kf(t, (0, 0), (0.40, 1), (1.30, 1), (1.6, 0))
    charge = kf(t, (0, 0.35), (0.85, 1.0), (1.3, 1.0), (1.6, 0.35))
    shot = bump(t, 0.92, 0.08)
    shake = 0.012 * wave(t, 0.08) * aim
    wrist = tuple(np.array(GUARD) + (np.array((-0.52 + shake, 1.22 - 0.10 * shot, 1.76 + shake)) - np.array(GUARD)) * aim)
    return blend(breathe(t, 1.6, 0.3),
                 {"rootdummy": {"rot": (-8 * aim + 7 * shot, 0, 12 * aim), "pos": (0, 0.10 * aim - 0.14 * shot, 0)},
                  "belly": {"pos": (0, -0.05 * shot, 0)}, "chest": {"rot": (-4 * aim, 0, 6 * aim)},
                  "head": {"rot": (4 * aim, 0, -16 * aim)}},
                 gauntlet(wrist, (0.15 * (1 - aim), 1.0, 0.85 * (1 - aim) + 0.12), (-0.3 * (1 - aim), 0.2, 1.0 * aim - 0.3 * (1 - aim) + 0.1)),
                 free_arm(tuple(np.array(HANG) + np.array((0.12, -0.30, 0.20)) * aim)),
                 fingers(0.75 - 0.75 * aim, thumb=0.75 - 0.75 * aim), lids(0.35 * aim), jaw(0.3 * aim),
                 ears(t, 0.0, -18 * aim + 24 * shot, 1.0),
                 {"stones": {"glow": tuple(min(1.0, c * charge + shot) for c in PURPLE)},
                  "g_handg": {"glow": scaled(PURPLE, 0.2 * charge + 0.4 * shot)}})


def usecomp(t):
    # The reality stone: the gauntlet is lifted overhead and brought down like a hammer (0.9 s);
    # his whole weight follows it and the belly rings.
    lift = kf(t, (0, 0), (0.55, 1), (0.75, 1), (0.9, 0), (1.8, 0))
    slam = kf(t, (0, 0), (0.75, 0), (0.9, 1), (1.3, 1), (1.8, 0))
    hop = kf(t, (0, 0), (0.25, -0.06), (0.6, 0.40), (0.9, -0.08), (1.1, 0.02), (1.8, 0))
    after = max(t - 0.9, 0.0)
    ring = math.exp(-after * 4) * math.sin(after * 22) if t > 0.9 else 0.0
    flash = bump(t, 0.92, 0.1)
    high, low = np.array((-0.62, 0.55, 2.62)), np.array((-0.40, 1.18, 1.02))
    wrist = tuple(np.array(GUARD) + (high - np.array(GUARD)) * lift + (low - np.array(GUARD)) * slam)
    return blend({"rootdummy": {"rot": (7 * lift - 15 * slam, 0, 6 * slam), "pos": (0, 0.10 * slam, hop)},
                  "belly": {"pos": (0, 0.05 * ring, -0.05 * lift + 0.09 * ring)},
                  "chest": {"rot": (4 * lift - 8 * slam, 0, 0)}, "head": {"rot": (-10 * lift + 12 * slam, 0, 0)},
                  "footl": {"rot": (-14 * lift, 0, 0)}, "footr": {"rot": (-14 * lift, 0, 0)}},
                 gauntlet(wrist, (0.2, 0.3 + 0.6 * slam, 0.9 - 1.4 * slam), (-0.2, 0.9, 0.3 + 0.6 * slam), elbow=(-1.0, -0.3, 0.1)),
                 free_arm(tuple(np.array(HANG) + np.array((0.10, 0.30, 0.32)) * lift + np.array((0.2, -0.2, 0.2)) * slam)),
                 fingers(1.0), lids(0.3 * slam - 0.4 * lift), jaw(0.5 * slam),
                 ears(t, 0.0, -26 * lift + 30 * slam + 16 * ring, 1.0),
                 {"stones": {"glow": tuple(min(1.0, c * (0.4 + 0.6 * lift) + flash) for c in RED)},
                  "g_handg": {"glow": scaled(RED, 0.45 * flash)}})


def chturnl(t):
    # The space and time stones: an open hand raised slowly, palm up, as if lifting the room.
    rise = kf(t, (0, 0), (0.7, 1), (1.5, 1), (2.0, 0))
    low, high = np.array((-0.78, 1.05, 1.30)), np.array((-0.82, 0.92, 2.28))
    wrist = tuple(np.array(GUARD) + (low - np.array(GUARD)) * min(rise * 2.5, 1.0) + (high - low) * rise)
    colour = tuple(g + (b - g) * (0.5 + 0.5 * wave(t, 0.5)) for g, b in zip(GREEN, BLUE))
    return blend(breathe(t, 2.0, 0.4),
                 {"rootdummy": {"rot": (4 * rise, 0, 6 * rise), "pos": (0, 0, 0.03 * rise)},
                  "chest": {"rot": (3 * rise, 0, 0)}, "head": {"rot": (8 * rise, 0, -10 * rise)}},
                 gauntlet(wrist, (0.1, 0.9, 0.35), (0.1, 0.3, -1.0)),
                 free_arm(tuple(np.array(HANG) + np.array((0.05, 0.15, 0.1)) * rise)),
                 fingers(0.75 - 0.6 * rise, thumb=0.75 - 0.7 * rise), lids(0.4 * rise),
                 ears(t, 2.0 * rise, 8 * rise, 0.5, splay=10 * rise),
                 {"stones": {"glow": scaled(colour, 0.35 + 0.65 * rise)}, "g_handg": {"glow": scaled(colour, 0.2 * rise)}})


def chturnr(t):
    # The soul stone: the hand reaches out open, closes on something unseen and drags it back.
    reach = kf(t, (0, 0), (0.45, 1), (0.75, 1), (1.05, 0.25), (1.2, 0))
    clench = kf(t, (0, 0.75), (0.45, 0.05), (0.7, 0.05), (0.85, 1.0), (1.2, 0.75))
    wrist = tuple(np.array(GUARD) + (np.array((-0.50, 1.22, 1.70)) - np.array(GUARD)) * reach)
    return blend(breathe(t, 1.2, 0.3),
                 {"rootdummy": {"rot": (-6 * reach, 0, 10 * reach), "pos": (0, 0.08 * reach, 0)},
                  "head": {"rot": (4 * reach, 0, -12 * reach)}},
                 gauntlet(wrist, (0.15, 1.0 * reach + 0.45 * (1 - reach), 0.85 * (1 - reach)), (-0.3, 0.3 + 0.6 * (1 - reach), 0.9 * reach - 0.3)),
                 free_arm(HANG), fingers(clench), lids(0.5 * reach), jaw(0.2 * reach),
                 ears(t, 0.0, 14 * reach, 1.0),
                 {"stones": {"glow": scaled(ORANGE, 0.4 + 0.6 * max(reach, 1 - clench))}, "g_handg": {"glow": scaled(ORANGE, 0.25 * reach)}})


def tlknorm(t):
    talk = 0.5 + 0.5 * wave(t, 0.33) * (0.6 + 0.4 * wave(t, 1.3))
    return blend(stand(t, 2.0, 0.6), jaw(0.45 * talk),
                 {"head": {"rot": (3 * wave(t, 0.5), 0, 5 * wave(t, 2.0))}}, blink(t, 0.9))


def choke(t):
    kick = wave(t, 0.5)
    return blend({"rootdummy": {"pos": (0, 0, 0.5 + 0.04 * wave(t, 1.5)), "rot": (5, 3 * kick, 0)},
                  "head": {"rot": (20, 0, 8 * kick)},
                  "thighl": {"rot": (-20 + 18 * kick, 0, 0)}, "thighr": {"rot": (-20 - 18 * kick, 0, 0)},
                  "footl": {"rot": (-20, 0, 0)}, "footr": {"rot": (-20, 0, 0)}},
                 gauntlet((-0.45, 0.72, 2.02), (0.6, 0.2, 0.75), (-0.3, 0.9, 0.2)), free_arm((0.45, 0.72, 2.00)),
                 fingers(0.3), lids(-0.9), jaw(0.9), ears(t, 8.0, 24.0, 0.5, splay=12),
                 {"stones": {"glow": scaled(WHITE, 0.12)}})


def horror(t):
    shiver = wave(t, 0.08)
    return blend({"rootdummy": {"rot": (8, 0, 0), "pos": (0.012 * shiver, -0.08, -0.05)},
                  "belly": {"pos": (0.01 * shiver, 0, 0)}, "head": {"rot": (8, 0, 3 * shiver)}},
                 gauntlet((-0.55, 0.80, 1.95), (0.5, 0.2, 0.85), (-0.2, 0.95, 0.1)), free_arm((0.55, 0.80, 1.95)),
                 fingers(0.2), lids(-1.0), jaw(0.7), ears(t, 3.0, 44.0, 0.08, splay=20),
                 {"stones": {"glow": scaled(WHITE, 0.12)}})


def sleep(t):
    slow = wave(t, 3.0)
    return blend({"rootdummy": {"rot": (-9 - 1.5 * slow, 0, 0), "pos": (0, 0, -0.05)},
                  "belly": {"pos": (0, 0.04 * slow, 0.015 * slow)}, "chest": {"rot": (-6, 0, 0)},
                  "neck": {"rot": (-12, 0, 0)}, "head": {"rot": (-20 - 2 * slow, 0, 8)}},
                 gauntlet((-0.95, 0.62, 1.12), (0.3, 0.5, -0.5), (-0.5, 0.8, 0.2)), free_arm(HANG),
                 fingers(0.4), lids(1.0), jaw(0.25 + 0.1 * slow), ears(t, 1.0, 40.0, 3.0, splay=20),
                 {"stones": {"glow": scaled(WHITE, 0.08)}})


def paralyzed(t):
    return blend({"rootdummy": {"rot": (4, 0, 0)}, "head": {"rot": (6, 0, 0)}},
                 gauntlet((-1.05, 0.55, 1.75), (-0.2, 0.3, 0.9), (-0.3, 0.9, -0.2)), free_arm((1.25, 0.45, 1.30)),
                 fingers(0.1), lids(-1.0), jaw(0.3), ears(0.0, 0.0, 0.0, 1.0, splay=26),
                 {"stones": {"glow": scaled(BLUE, 0.6)}})


def whirlwind(t):
    return blend({"rootdummy": {"rot": (6, 5, 360 * t / 1.0), "pos": (0, 0, 0.6 + 0.05 * wave(t, 0.5))},
                  "thighl": {"rot": (-20, 0, 0)}, "thighr": {"rot": (-20, 0, 0)}},
                 gauntlet((-1.25, 0.50, 1.80), (-0.6, 0.3, 0.6), (-0.3, 0.9, -0.2)), free_arm((1.35, 0.35, 1.55)),
                 fingers(0.2), lids(-0.8), jaw(0.6), ears(t, 4.0, 10.0, 0.25, splay=34),
                 {"stones": {"glow": scaled(WHITE, 0.15)}})


def cspasm(t):
    j, k = wave(t, 0.11), wave(t, 0.17)
    return blend({"rootdummy": {"rot": (4 * j, 3 * k, 0), "pos": (0.02 * k, 0, 0.02 * abs(j))},
                  "belly": {"pos": (0.02 * j, 0.02 * k, 0.02 * j)}, "head": {"rot": (8 * k, 0, 9 * j)}},
                 gauntlet((GUARD[0] + 0.06 * j, GUARD[1], GUARD[2] + 0.10 * k), (0.25, 0.45, 0.85), (-0.3, 0.9, -0.3)),
                 free_arm((HANG[0], HANG[1] + 0.10 * k, HANG[2] + 0.25 + 0.10 * j)),
                 fingers(0.3 + 0.3 * abs(j)), lids(-0.5), jaw(0.5), ears(t, 16.0, 0.0, 0.11, splay=10),
                 {"stones": {"glow": scaled(BLUE, 0.5 + 0.5 * abs(j))}})


# name: (pose function, length in seconds, events)
ANIMATIONS = {
    "cpause1": (cpause1, 3.0, []),
    "cpause2": (cpause2, 3.2, []),
    "creadyr": (creadyr, 1.6, []),
    "cwalk": (cwalk, 1.2, [(0.0, "snd_hitground"), (0.6, "snd_hitground")]),
    "crun": (crun, ROLL_PERIOD, [(0.0, "snd_hitground"), (0.5, "snd_footstep")]),
    "cwalkinj": (cwalkinj, 1.4, [(0.0, "snd_footstep"), (0.7, "snd_footstep")]),
    "g0a1": (g0a1, 1.5, [(0.5, "Hit")]),
    "g0a2": (g0a2, 1.5, [(0.5, "Hit")]),
    "cdodgeg": (cdodgeg, 1.0, [(0.3, "snd_footstep")]),
    "cdamages": (cdamages, 0.35, []),
    "cdie": (cdie, 2.4, [(1.27, "snd_hitground")]),
    "cdead": (cdead, 0.0333, []),
    "ckdbck": (ckdbck, 1.3, [(0.47, "snd_hitground")]),
    "ckdbcklp": (ckdbcklp, 0.7, []),
    "cgustandb": (cgustandb, 1.0, [(0.85, "snd_footstep")]),
    # The powers, each under an animation name the engine can play on a creature.
    "ctaunt": (ctaunt, 2.4, []),                                   # the snap
    "cvictory": (cvictory, 2.5, []),                               # the laugh
    "disabled": (disabled, 1.6, []),                               # the power stone (held 1.6 s)
    "usecomp": (usecomp, 1.8, [(0.9, "snd_hitground")]),           # the reality stone's slam
    "chturnl": (chturnl, 2.0, []),                                 # the space and time stones
    "chturnr": (chturnr, 1.2, []),                                 # the soul stone
    "tlknorm": (tlknorm, 2.0, []),
    "choke": (choke, 1.5, []),
    "horror": (horror, 1.6, []),
    "sleep": (sleep, 3.0, []),
    "paralyzed": (paralyzed, 0.0333, []),
    "whirlwind": (whirlwind, 1.0, []),
    "cspasm": (cspasm, 1.0, []),
}

# How far the walk and run cycles carry him, for appearance.2da's walkdist / rundist: two
# bounces of about a metre, and one whole turn of a ball about a metre in radius.
WALK_DISTANCE = 2.0 * model.SCALE
RUN_DISTANCE = 6.3 * model.SCALE


def build(root: kmdl.Node):
    """Samples every pose function into the keyframes the model file stores."""
    nodes, _ = kmdl.flatten(root)
    known = {n.name: n for n in nodes}
    animations = []
    for name, (pose_at, length, events) in ANIMATIONS.items():
        count = max(int(round(length * FPS)), 1)
        times = [length * k / count for k in range(count + 1)] if length > 0.05 else [0.0]
        samples = [pose_at(t) for t in times]
        tracks = {}
        for node_name in sorted({n for s in samples for n in s}):
            if node_name not in known:
                raise ValueError(f"animation '{name}' poses '{node_name}', which is not a node")
            parts = [s.get(node_name, {}) for s in samples]
            track = {}
            if any("pos" in p for p in parts):
                # A position key is how far the node is moved FROM its rest position (the game
                # adds the two); an orientation key replaces the rest orientation outright.
                track["position"] = [(t, *(c * model.SCALE for c in p.get("pos", (0, 0, 0)))) for t, p in zip(times, parts)]
            if any("rot" in p or "quat" in p for p in parts):
                track["orientation"] = [(t, *(p["quat"] if "quat" in p else geom.q_euler(*p.get("rot", (0, 0, 0)))))
                                        for t, p in zip(times, parts)]
            if any("scale" in p for p in parts):
                track["scale"] = [(t, p.get("scale", 1.0)) for t, p in zip(times, parts)]
            if any("glow" in p for p in parts):
                track["selfillum"] = [(t, *p.get("glow", (0, 0, 0))) for t, p in zip(times, parts)]
            for key, rows in list(track.items()):
                if all(row[1:] == rows[0][1:] for row in rows):
                    track[key] = [rows[0]]            # it never moves: one key is enough
            tracks[node_name] = track
        animations.append(kmdl.Animation(name, length, 0.25, list(events), tracks))
    return animations
