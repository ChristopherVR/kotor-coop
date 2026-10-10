"""The boss's animations, written as poses over time and sampled into keyframes.

Each animation is a function of time that says how far each part is moved and turned from
its rest pose. The names are the ones the game looks for on a single-model creature
(checked against c_gizka, c_rancor and c_gammorean), so the engine plays them by itself
for standing, walking, attacking, being hit and dying; the gauntlet powers ride on the
creature slots the engine has for taunts, casting and powering up.
"""
from __future__ import annotations

import math

import numpy as np

import geom
import kmdl
import model

FPS = 15

ALONG, ACROSS, BACK = model._fist_frame()
LEG_REST = {name: np.array(pivot) - np.array(model.BODY_CENTRE)
            for name, pivot in (("legl", model.FOOT_L), ("legr", model.FOOT_R))}

GLOW_IDLE = (0.3, 0.3, 0.3)
GLOW_OFF = (0.0, 0.0, 0.0)
PURPLE = (0.75, 0.25, 1.0)
BLUE = (0.25, 0.5, 1.0)
RED = (1.0, 0.2, 0.2)
ORANGE = (1.0, 0.6, 0.15)
GREEN = (0.25, 1.0, 0.4)
YELLOW = (1.0, 0.95, 0.3)
WHITE = (1.0, 1.0, 1.0)


# ---- small helpers for writing motion

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


def scaled(colour, amount):
    return tuple(c * amount for c in colour)


def fingers(opened):
    """0 is the closed fist, 1 is the hand held open."""
    return geom.q_axis(tuple(ACROSS), 115 * opened)


def thumb(opened):
    return geom.q_mul(geom.q_axis(tuple(ALONG), -60 * opened), geom.q_axis(tuple(BACK), -35 * opened))


def carry_legs(pose):
    """Moves the feet as if they were fixed to the body, for falls, jumps and spins."""
    root = pose.get("rootdummy", {})
    turn = geom.q_matrix(geom.q_euler(*root.get("rot", (0, 0, 0))))
    shift = np.array(root.get("pos", (0, 0, 0)), dtype=float)
    for name, rest in LEG_REST.items():
        extra = pose.get(name, {})
        moved = turn @ rest - rest + shift + np.array(extra.get("pos", (0, 0, 0)), dtype=float)
        rot = tuple(a + b for a, b in zip(root.get("rot", (0, 0, 0)), extra.get("rot", (0, 0, 0))))
        pose[name] = {"pos": tuple(moved), "rot": rot}
    return pose


def breathe(t, period=3.0, depth=1.0):
    """The slow in-and-out every standing pose shares."""
    b = wave(t, period)
    return {
        "rootdummy": {"pos": (0, 0, 0.012 * b * depth)},
        "body": {"scale": 1.0 + 0.018 * b * depth},
        "neck": {"rot": (1.5 * b * depth, 0, 0)},
        "earl": {"rot": (2 * wave(t, period, 0.1) * depth, -2 * b * depth, 0)},
        "earr": {"rot": (2 * wave(t, period, 0.2) * depth, 2 * b * depth, 0)},
        "armf": {"rot": (2 * b * depth, 0, 0)},
    }


def blend(*poses):
    """Adds poses together (positions and angles add; scale, glow and finger shapes take the last given)."""
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


# ---- the animations

def cpause1(t):
    pulse = 0.3 + 0.1 * wave(t, 3.0)
    return blend(breathe(t), {"stones": {"glow": scaled(WHITE, pulse)}})


def cpause2(t):
    # Looks one way, then the other, flicks an ear and pats the belly with the free hand.
    look = kf(t, (0, 0), (0.6, 28), (1.3, 28), (2.0, -24), (2.6, -24), (3.2, 0))
    flick = 22 * math.exp(-((t - 1.0) / 0.12) ** 2) + 22 * math.exp(-((t - 1.25) / 0.12) ** 2)
    pat = kf(t, (0, 0), (1.9, 0), (2.2, 1), (3.0, 1), (3.2, 0))
    return blend(breathe(t, 3.2), {
        "neck": {"rot": (0, 0, look)},
        "earl": {"rot": (0, -flick, 0)},
        "armf": {"rot": (38 * pat + 10 * pat * wave(t, 0.4), 28 * pat, 0)},
        "stones": {"glow": GLOW_IDLE},
    })


def creadyr(t):
    sway = wave(t, 1.6)
    return blend(breathe(t, 1.6, 0.6), {
        "rootdummy": {"rot": (-6, 3 * sway, 2 * sway), "pos": (0.04 * sway, 0, 0)},
        "neck": {"rot": (-4, 0, -2 * sway)},
        "armg": {"rot": (34 + 4 * sway, 0, 0)},
        "gauntlet": {"rot": (26 + 5 * wave(t, 1.6, 0.25), 0, -10)},
        "armf": {"rot": (18, -14, 0)},
        "earl": {"rot": (-16, 0, 0)},
        "earr": {"rot": (-16, 0, 0)},
        "stones": {"glow": scaled(WHITE, 0.5 + 0.2 * wave(t, 0.8))},
    })


def _gait(t, period, stride, lift, roll, lean, ear, arm):
    """A waddle: the body rolls over each planted foot while the other swings forward."""
    phase = t / period
    pose = {
        "rootdummy": {"rot": (lean, roll * wave(t, period), -roll * 0.55 * wave(t, period)),
                      "pos": (0.05 * wave(t, period), 0, 0.05 * abs(wave(t, period, 0.25)) * (lift / 0.14))},
        "neck": {"rot": (-lean * 0.5, -roll * 0.5 * wave(t, period), roll * 0.4 * wave(t, period))},
        "earl": {"rot": (ear + 9 * wave(t, period / 2, 0.15), 0, 0)},
        "earr": {"rot": (ear + 9 * wave(t, period / 2, 0.3), 0, 0)},
        "armf": {"rot": (arm * wave(t, period), 0, 0)},
        "armg": {"rot": (8 - arm * 0.35 * wave(t, period), 0, 0)},
        "body": {"scale": 1.0 + 0.012 * wave(t, period / 2)},
        "stones": {"glow": GLOW_IDLE},
    }
    for name, offset in (("legl", 0.0), ("legr", 0.5)):
        f = (phase + offset) % 1.0
        if f < 0.5:      # planted, sliding back under the body
            forward = stride * (0.5 - 2 * f)
            height, pitch = 0.0, 0.0
        else:            # swinging forward through the air
            s = (f - 0.5) * 2
            forward = stride * (-0.5 + s)
            height = lift * math.sin(math.pi * s)
            pitch = 22 * math.sin(math.pi * s) - 10 * s
        pose[name] = {"pos": (0, forward, height), "rot": (pitch, 0, 0)}
    return pose


def cwalk(t):
    return _gait(t, 1.2, 0.42, 0.14, 7, -2, -4, 16)


def crun(t):
    return _gait(t, 0.7, 0.6, 0.22, 10, -11, -34, 30)


def cwalkinj(t):
    pose = _gait(t, 1.4, 0.3, 0.09, 5, -9, 26, 8)
    return blend(pose, {"rootdummy": {"rot": (0, -6, 0)}, "neck": {"rot": (-10, 0, 0)},
                        "armf": {"rot": (40, 30, 0)}, "stones": {"glow": scaled(WHITE, 0.15)}})


def g0a1(t):
    # The punch: twist back, drive the gauntlet straight out, the hit lands at 0.5 s.
    twist = kf(t, (0, 0), (0.3, 20), (0.5, -22), (0.75, -16), (1.5, 0))
    lean = kf(t, (0, 0), (0.3, 6), (0.5, -14), (0.8, -10), (1.5, 0))
    lunge = kf(t, (0, 0), (0.3, -0.12), (0.5, 0.3), (0.8, 0.22), (1.5, 0))
    raise_ = kf(t, (0, 0), (0.3, -25), (0.5, 66), (0.8, 60), (1.5, 0))
    reach = kf(t, (0, 0), (0.3, 25), (0.5, -45), (0.8, -40), (1.5, 0))
    flash = math.exp(-((t - 0.5) / 0.1) ** 2)
    return carry_legs_soft({
        "rootdummy": {"rot": (lean, 0, twist), "pos": (0, lunge, 0)},
        "neck": {"rot": (-lean * 0.6, 0, -twist * 0.7)},
        "armg": {"rot": (raise_, 0, 0)},
        "gauntlet": {"rot": (kf(t, (0, 0), (0.3, 10), (0.5, -66), (0.8, -60), (1.5, 0)), 0, reach)},
        "armf": {"rot": (kf(t, (0, 0), (0.3, 30), (0.5, -35), (1.5, 0)), -18, 0)},
        "earl": {"rot": (kf(t, (0, 0), (0.3, -12), (0.5, 30), (0.9, -8), (1.5, 0)), 0, 0)},
        "earr": {"rot": (kf(t, (0, 0), (0.3, -12), (0.55, 30), (0.95, -8), (1.5, 0)), 0, 0)},
        "stones": {"glow": scaled(PURPLE, 0.35 + 0.65 * flash)},
        "gauntlet+": {"glow": scaled(PURPLE, 0.35 * flash)},
    }, t, 1.5)


def carry_legs_soft(pose, t, length):
    """For moves where the feet stay planted: nothing to do but tidy the helper key."""
    extra = pose.pop("gauntlet+", None)
    if extra:
        pose.setdefault("gauntlet", {}).update(extra)
    return pose


def g0a2(t):
    # The belly bump: rock back, throw the whole belly forward (hit at 0.5 s), wobble to a stop.
    rock = kf(t, (0, 0), (0.3, 12), (0.5, -14), (0.75, 5), (1.0, -3), (1.5, 0))
    shove = kf(t, (0, 0), (0.3, -0.22), (0.5, 0.55), (0.8, 0.1), (1.5, 0))
    hop = kf(t, (0, 0), (0.3, 0.0), (0.42, 0.16), (0.55, 0.02), (1.5, 0))
    squash = kf(t, (0, 1.0), (0.3, 0.96), (0.5, 1.1), (0.7, 0.97), (0.95, 1.02), (1.5, 1.0))
    arms = kf(t, (0, 0), (0.3, 35), (0.5, -40), (1.5, 0))
    return carry_legs({
        "rootdummy": {"rot": (rock, 0, 0), "pos": (0, shove, hop)},
        "body": {"scale": squash},
        "neck": {"rot": (-rock * 0.8, 0, 0)},
        "armf": {"rot": (arms, -20, 0)},
        "armg": {"rot": (arms * 0.6, 0, 0)},
        "earl": {"rot": (-rock * 2.2, 0, 0)},
        "earr": {"rot": (-rock * 2.2, 0, 0)},
        "stones": {"glow": GLOW_IDLE},
    })


def cdodgeg(t):
    side = kf(t, (0, 0), (0.25, 0.55), (0.6, 0.5), (1.0, 0))
    tip = kf(t, (0, 0), (0.25, 20), (0.6, 14), (1.0, 0))
    return carry_legs({
        "rootdummy": {"pos": (side, -0.1 * (side / 0.55), 0.1 * math.sin(math.pi * min(t / 0.5, 1.0))), "rot": (4, tip, 0)},
        "neck": {"rot": (0, -tip * 0.8, 0)},
        "earl": {"rot": (0, -tip * 1.5, 0)},
        "earr": {"rot": (0, -tip * 1.5, 0)},
        "armf": {"rot": (20, -tip * 1.5, 0)},
        "stones": {"glow": GLOW_IDLE},
    })


def cdamages(t):
    jolt = kf(t, (0, 0), (0.1, 1), (0.35, 0))
    return {
        "rootdummy": {"rot": (11 * jolt, 0, 4 * jolt), "pos": (0, -0.08 * jolt, 0)},
        "body": {"scale": 1.0 - 0.04 * jolt},
        "neck": {"rot": (14 * jolt, 0, -6 * jolt)},
        "earl": {"rot": (-20 * jolt, -14 * jolt, 0)},
        "earr": {"rot": (-20 * jolt, 14 * jolt, 0)},
        "armf": {"rot": (30 * jolt, -25 * jolt, 0)},
        "armg": {"rot": (20 * jolt, 0, 0)},
        "stones": {"glow": scaled(RED, 0.3 + 0.5 * jolt)},
    }


def _on_back(amount, t=0.0):
    """0 standing, 1 flat on his back with the feet in the air."""
    return carry_legs({
        "rootdummy": {"rot": (88 * amount, 0, 0), "pos": (0, -0.35 * amount, -0.1 * amount)},
        "neck": {"rot": (-18 * amount, 0, 0)},
        "earl": {"rot": (-55 * amount, -18 * amount, 0)},
        "earr": {"rot": (-55 * amount, 18 * amount, 0)},
        "armf": {"rot": (-50 * amount, -55 * amount, 0)},
        "armg": {"rot": (-30 * amount, 30 * amount, 0)},
        "gauntlet": {"rot": (0, 0, -40 * amount)},
        "legl": {"rot": (-25 * amount, 0, 0)},
        "legr": {"rot": (-25 * amount, 0, 0)},
    })


def cdie(t):
    # Sways, tips over backwards, lands with a bounce, and the stones go dark.
    sway = kf(t, (0, 0), (0.25, -7), (0.55, 9), (0.8, 0))
    fall = kf(t, (0, 0), (0.7, 0.02), (1.25, 1.0), (2.4, 1.0))
    bounce = 0.0
    if t > 1.25:
        bounce = 0.18 * math.exp(-(t - 1.25) * 5) * abs(math.sin((t - 1.25) * 9))
    pose = _on_back(fall)
    pose = blend(pose, {
        "rootdummy": {"rot": (sway, 0, sway * 0.5), "pos": (0, 0, bounce)},
        "body": {"scale": 1.0 + (0.07 * math.exp(-((t - 1.3) / 0.1) ** 2) if t > 1.1 else 0.0)},
        "stones": {"glow": scaled(WHITE, kf(t, (0, 0.3), (0.5, 0.9), (0.9, 0.1), (1.2, 0.6), (1.6, 0.0), (2.4, 0.0)))},
    })
    return carry_legs_bounce(pose, bounce)


def carry_legs_bounce(pose, bounce):
    for name in ("legl", "legr"):
        x, y, z = pose[name]["pos"]
        pose[name]["pos"] = (x, y, z + bounce)
    return pose


def cdead(t):
    return blend(_on_back(1.0), {"body": {"scale": 1.0}, "stones": {"glow": GLOW_OFF}})


def ckdbck(t):
    fall = kf(t, (0, 0), (0.45, 1.0), (1.3, 1.0))
    bounce = 0.15 * math.exp(-(t - 0.45) * 5) * abs(math.sin((t - 0.45) * 9)) if t > 0.45 else 0.0
    pose = blend(_on_back(fall), {"rootdummy": {"pos": (0, 0, bounce)}, "stones": {"glow": scaled(WHITE, 0.15)}})
    return carry_legs_bounce(pose, bounce)


def ckdbcklp(t):
    kick = 12 * wave(t, 0.7)
    return blend(_on_back(1.0), {"legl": {"rot": (kick, 0, 0)}, "legr": {"rot": (-kick, 0, 0)},
                                  "armf": {"rot": (kick, 0, 0)}, "stones": {"glow": scaled(WHITE, 0.15)}})


def cgustandb(t):
    up = kf(t, (0, 1.0), (0.25, 1.0), (0.85, 0.0), (1.0, 0.0))
    return blend(_on_back(up), {"body": {"scale": 1.0 + 0.04 * math.sin(math.pi * min(t / 1.0, 1.0))},
                                "stones": {"glow": scaled(WHITE, 0.3 - 0.15 * up)}})


def ctaunt(t):
    # The snap. The gauntlet goes up beside the head, the stones flare one after another, the
    # fingers press against the thumb and at 1.3 s they snap: a white flash and a jolt.
    up = kf(t, (0, 0), (0.7, 1.0), (1.75, 1.0), (2.4, 0.0))
    press = kf(t, (0, 0), (0.55, 0.0), (0.9, 0.75), (1.26, 0.8), (1.34, 0.05), (1.8, 0.05), (2.4, 0.0))
    flash = math.exp(-((t - 1.33) / 0.09) ** 2)
    charge = kf(t, (0, 0.3), (0.7, 0.6), (1.28, 1.0), (1.6, 0.8), (2.4, 0.3))
    colours = [PURPLE, BLUE, RED, ORANGE, GREEN, YELLOW]
    colour = colours[min(int(max(t - 0.2, 0.0) / 0.18), 5)] if t < 1.28 else WHITE
    jolt = math.exp(-((t - 1.36) / 0.12) ** 2)
    return {
        "rootdummy": {"rot": (4 * up - 7 * jolt, -3 * up, -9 * up), "pos": (0, 0, 0.03 * up - 0.05 * jolt)},
        "body": {"scale": 1.0 + 0.05 * jolt},
        "neck": {"rot": (7 * up, 0, 12 * up)},
        "armg": {"rot": (82 * up, 0, 0)},
        "gauntlet": {"rot": (-6 * up, 0, -35 * up), "glow": scaled(WHITE, 0.55 * flash)},
        "fingers": {"quat": fingers(press)},
        "thumb": {"quat": thumb(press)},
        "armf": {"rot": (16 * up, -32 * up, 0)},
        "earl": {"rot": (-8 * up + 26 * jolt, -8 * jolt, 0)},
        "earr": {"rot": (-8 * up + 26 * jolt, 8 * jolt, 0)},
        "stones": {"glow": tuple(min(1.0, c * charge + flash) for c in colour)},
    }


def cvictory(t):
    # A belly laugh with the gauntlet pumped in the air.
    laugh = wave(t, 0.28) * kf(t, (0, 0), (0.3, 1), (2.0, 1), (2.5, 0))
    up = kf(t, (0, 0), (0.4, 1), (2.0, 1), (2.5, 0))
    return {
        "rootdummy": {"rot": (9 * up, 0, 0), "pos": (0, 0, 0.035 * laugh)},
        "body": {"scale": 1.0 + 0.035 * laugh},
        "neck": {"rot": (15 * up + 4 * laugh, 0, 0)},
        "armg": {"rot": (96 * up + 8 * laugh, 0, 0)},
        "gauntlet": {"rot": (-10 * up, 0, -35 * up)},
        "armf": {"rot": (30 * up + 8 * laugh, 25 * up, 0)},
        "earl": {"rot": (-18 * up + 8 * laugh, 0, 0)},
        "earr": {"rot": (-18 * up - 8 * laugh, 0, 0)},
        "stones": {"glow": scaled(YELLOW, 0.4 + 0.4 * up)},
    }


def castout1(t):
    # Power blast: the fist is levelled at the target, shakes as it charges, and kicks back on the shot (0.9 s).
    aim = kf(t, (0, 0), (0.45, 1), (1.25, 1), (1.6, 0))
    charge = kf(t, (0, 0.3), (0.85, 1.0), (1.0, 1.0), (1.6, 0.3))
    shot = math.exp(-((t - 0.92) / 0.08) ** 2)
    shake = 2.5 * wave(t, 0.09) * aim * (1 - shot)
    return {
        "rootdummy": {"rot": (-10 * aim + 10 * shot, 0, -14 * aim), "pos": (0, 0.12 * aim - 0.2 * shot, 0)},
        "neck": {"rot": (4 * aim, 0, 10 * aim)},
        "armg": {"rot": (60 * aim + shake + 12 * shot, 0, 0)},
        "gauntlet": {"rot": (-60 * aim, 0, -41 * aim + shake), "glow": scaled(PURPLE, 0.2 * charge + 0.4 * shot)},
        "armf": {"rot": (-25 * aim, -30 * aim, 0)},
        "earl": {"rot": (-22 * aim + 30 * shot, 0, 0)},
        "earr": {"rot": (-22 * aim + 30 * shot, 0, 0)},
        "stones": {"glow": tuple(min(1.0, c * charge + shot) for c in PURPLE)},
    }


def castout2(t):
    # Ground slam: he hops, and comes down fist first (impact at 0.9 s); the belly squashes and rings.
    hop = kf(t, (0, 0), (0.25, -0.08), (0.6, 0.75), (0.9, -0.06), (1.1, 0.03), (1.8, 0))
    lift = kf(t, (0, 0), (0.55, 1), (0.75, 1), (0.9, 0), (1.8, 0))
    slam = kf(t, (0, 0), (0.75, 0), (0.9, 1), (1.3, 1), (1.8, 0))
    ring = math.exp(-(t - 0.9) * 4) * math.sin((t - 0.9) * 22) if t > 0.9 else 0.0
    flash = math.exp(-((t - 0.92) / 0.1) ** 2)
    return carry_legs({
        "rootdummy": {"rot": (8 * lift - 16 * slam, 0, 0), "pos": (0, 0.1 * slam, hop)},
        "body": {"scale": 1.0 + 0.06 * ring - 0.03 * lift},
        "neck": {"rot": (-10 * lift + 12 * slam, 0, 0)},
        "armg": {"rot": (150 * lift + 20 * slam, 0, 0)},
        "gauntlet": {"rot": (20 * lift - 45 * slam, 0, -35 * slam), "glow": scaled(ORANGE, 0.5 * flash)},
        "armf": {"rot": (140 * lift + 10 * slam, -20 * lift, 0)},
        "earl": {"rot": (-30 * lift + 35 * slam + 20 * ring, 0, 0)},
        "earr": {"rot": (-30 * lift + 35 * slam - 20 * ring, 0, 0)},
        "stones": {"glow": tuple(min(1.0, c * (0.4 + 0.6 * lift) + flash) for c in ORANGE)},
    })


def castout3(t):
    # The pirouette: a full spin on the spot with the gauntlet held out, green and blue light trailing.
    spin = kf(t, (0, 0), (0.3, -20), (1.5, 380), (1.75, 355), (2.0, 360))
    out = kf(t, (0, 0), (0.35, 1), (1.5, 1), (2.0, 0))
    colour = tuple(g + (b - g) * (0.5 + 0.5 * wave(t, 0.5)) for g, b in zip(GREEN, BLUE))
    return carry_legs({
        "rootdummy": {"rot": (0, 5 * out, spin), "pos": (0, 0, 0.12 * out * abs(wave(t, 0.6)))},
        "neck": {"rot": (-6 * out, 0, 0)},
        "armg": {"rot": (60 * out, -40 * out, 0)},
        "gauntlet": {"rot": (25 * out, 0, -70 * out)},
        "armf": {"rot": (25 * out, 75 * out, 0)},
        "earl": {"rot": (-10 * out, 38 * out, 0)},
        "earr": {"rot": (-10 * out, -38 * out, 0)},
        "stones": {"glow": scaled(colour, 0.3 + 0.7 * out)},
    })


def _charged(t, amount):
    buzz = wave(t, 0.07) * amount
    return {
        "rootdummy": {"rot": (-5 * amount, 0, 0), "pos": (0.012 * buzz, 0, -0.06 * amount)},
        "body": {"scale": 1.0 + 0.03 * amount + 0.008 * buzz},
        "neck": {"rot": (-8 * amount, 0, 0)},
        "armg": {"rot": (48 * amount, 0, 0)},
        "gauntlet": {"rot": (60 * amount + 2 * buzz, 0, 12 * amount), "glow": scaled(YELLOW, 0.3 * amount)},
        "armf": {"rot": (48 * amount, -35 * amount, 0)},
        "earl": {"rot": (18 * amount + 4 * buzz, 0, 0)},
        "earr": {"rot": (18 * amount - 4 * buzz, 0, 0)},
        "stones": {"glow": scaled(WHITE, 0.3 + 0.7 * amount)},
    }


def powerup(t):
    return _charged(t, kf(t, (0, 0), (1.2, 1)))


def powered(t):
    return _charged(t, 1.0)


def powerdown(t):
    return _charged(t, kf(t, (0, 1), (1.0, 0)))


def tlknorm(t):
    nod = wave(t, 0.5) * (0.6 + 0.4 * wave(t, 2.0))
    return blend(breathe(t, 2.0, 0.5), {
        "neck": {"rot": (5 * nod, 0, 6 * wave(t, 2.0))},
        "armg": {"rot": (20 + 8 * wave(t, 1.0), 0, 0)},
        "gauntlet": {"rot": (20 + 10 * wave(t, 1.0, 0.2), 0, -12)},
        "stones": {"glow": GLOW_IDLE},
    })


def chturnl(t):
    turn = kf(t, (0, 0), (0.35, 40), (0.65, 40), (1.0, 0))
    return {"neck": {"rot": (0, 0, turn)}, "rootdummy": {"rot": (0, 0, turn * 0.2)}, "stones": {"glow": GLOW_IDLE}}


def chturnr(t):
    turn = kf(t, (0, 0), (0.35, -40), (0.65, -40), (1.0, 0))
    return {"neck": {"rot": (0, 0, turn)}, "rootdummy": {"rot": (0, 0, turn * 0.2)}, "stones": {"glow": GLOW_IDLE}}


def choke(t):
    kick = wave(t, 0.5)
    return carry_legs({
        "rootdummy": {"pos": (0, 0, 0.55 + 0.04 * wave(t, 1.5)), "rot": (6, 3 * kick, 0)},
        "neck": {"rot": (22, 0, 8 * kick)},
        "armf": {"rot": (120, 45, 0)},
        "armg": {"rot": (60 + 6 * kick, 0, 0)},
        "gauntlet": {"rot": (55, 0, 0)},
        "earl": {"rot": (30 + 10 * kick, -15, 0)},
        "earr": {"rot": (30 - 10 * kick, 15, 0)},
        "legl": {"rot": (-30 + 22 * kick, 0, 0)},
        "legr": {"rot": (-30 - 22 * kick, 0, 0)},
        "stones": {"glow": scaled(WHITE, 0.12)},
    })


def horror(t):
    shiver = wave(t, 0.08)
    return {
        "rootdummy": {"rot": (9, 0, 0), "pos": (0.015 * shiver, -0.08, -0.04)},
        "neck": {"rot": (10, 0, 3 * shiver)},
        "armf": {"rot": (95 + 4 * shiver, 40, 0)},
        "armg": {"rot": (70 + 4 * shiver, 0, 0)},
        "gauntlet": {"rot": (45, 0, 10)},
        "earl": {"rot": (55 + 5 * shiver, -22, 0)},
        "earr": {"rot": (55 - 5 * shiver, 22, 0)},
        "stones": {"glow": scaled(WHITE, 0.12)},
    }


def sleep(t):
    slow = wave(t, 3.0)
    return {
        "rootdummy": {"rot": (-12 - 2 * slow, 0, 0), "pos": (0, 0, -0.06)},
        "body": {"scale": 1.0 + 0.03 * slow},
        "neck": {"rot": (-28 - 3 * slow, 0, 6)},
        "armf": {"rot": (-6, 0, 0)},
        "armg": {"rot": (-14, 0, 0)},
        "gauntlet": {"rot": (-18, 0, 0)},
        "earl": {"rot": (48 + 4 * slow, -24, 0)},
        "earr": {"rot": (52 + 4 * slow, 24, 0)},
        "stones": {"glow": scaled(WHITE, 0.08)},
    }


def paralyzed(t):
    return {
        "rootdummy": {"rot": (5, 0, 0)},
        "neck": {"rot": (8, 0, 0)},
        "armf": {"rot": (20, -60, 0)},
        "armg": {"rot": (30, 0, 0)},
        "earl": {"rot": (0, -30, 0)},
        "earr": {"rot": (0, 30, 0)},
        "stones": {"glow": scaled(BLUE, 0.6)},
    }


def whirlwind(t):
    return carry_legs({
        "rootdummy": {"rot": (8, 6, 360 * t / 1.0), "pos": (0, 0, 0.6 + 0.05 * wave(t, 0.5))},
        "armf": {"rot": (30, -75, 0)},
        "armg": {"rot": (50, 40, 0)},
        "earl": {"rot": (0, 40, 0)},
        "earr": {"rot": (0, -40, 0)},
        "legl": {"rot": (-25, 0, 0)},
        "legr": {"rot": (-25, 0, 0)},
        "stones": {"glow": scaled(WHITE, 0.15)},
    })


def cspasm(t):
    j = wave(t, 0.11)
    k = wave(t, 0.17)
    return {
        "rootdummy": {"rot": (5 * j, 4 * k, 0), "pos": (0.02 * k, 0, 0.02 * abs(j))},
        "body": {"scale": 1.0 + 0.02 * k},
        "neck": {"rot": (9 * k, 0, 10 * j)},
        "armf": {"rot": (40 + 25 * j, -30, 0)},
        "armg": {"rot": (30 + 20 * k, 0, 0)},
        "gauntlet": {"rot": (20 * j, 0, 0)},
        "earl": {"rot": (20 * j, -10, 0)},
        "earr": {"rot": (20 * k, 10, 0)},
        "stones": {"glow": scaled(BLUE, 0.5 + 0.5 * abs(j))},
    }


# name: (pose function, length in seconds, events)
ANIMATIONS = {
    "cpause1": (cpause1, 3.0, []),
    "cpause2": (cpause2, 3.2, []),
    "creadyr": (creadyr, 1.6, []),
    "cwalk": (cwalk, 1.2, [(0.0, "snd_footstep"), (0.6, "snd_footstep")]),
    "crun": (crun, 0.7, [(0.0, "snd_footstep"), (0.35, "snd_footstep")]),
    "cwalkinj": (cwalkinj, 1.4, [(0.0, "snd_footstep"), (0.7, "snd_footstep")]),
    "g0a1": (g0a1, 1.5, [(0.5, "Hit")]),
    "g0a2": (g0a2, 1.5, [(0.5, "Hit"), (0.55, "snd_footstep")]),
    "cdodgeg": (cdodgeg, 1.0, [(0.3, "snd_footstep")]),
    "cdamages": (cdamages, 0.35, []),
    "cdie": (cdie, 2.4, [(1.27, "snd_hitground")]),
    "cdead": (cdead, 0.0333, []),
    "ckdbck": (ckdbck, 1.3, [(0.47, "snd_hitground")]),
    "ckdbcklp": (ckdbcklp, 0.7, []),
    "cgustandb": (cgustandb, 1.0, [(0.85, "snd_footstep")]),
    "ctaunt": (ctaunt, 2.4, []),
    "cvictory": (cvictory, 2.5, []),
    "castout1": (castout1, 1.6, []),
    "castout2": (castout2, 1.8, [(0.9, "snd_hitground")]),
    "castout3": (castout3, 2.0, []),
    "powerup": (powerup, 1.2, []),
    "powered": (powered, 1.0, []),
    "powerdown": (powerdown, 1.0, []),
    "tlknorm": (tlknorm, 2.0, []),
    "chturnl": (chturnl, 1.0, []),
    "chturnr": (chturnr, 1.0, []),
    "choke": (choke, 1.5, []),
    "horror": (horror, 1.6, []),
    "sleep": (sleep, 3.0, []),
    "paralyzed": (paralyzed, 0.0333, []),
    "whirlwind": (whirlwind, 1.0, []),
    "cspasm": (cspasm, 1.0, []),
}

# How far the walk and run cycles carry him, for appearance.2da's walkdist / rundist: both feet
# take one stride per cycle.
WALK_DISTANCE = 2 * 0.42 * model.SCALE
RUN_DISTANCE = 2 * 0.6 * model.SCALE


def build(root: kmdl.Node):
    """Samples every pose function into the keyframes the model file stores."""
    nodes, _ = kmdl.flatten(root)
    rest = {n.name: n for n in nodes}
    sign = -1.0 if model.MIRROR else 1.0
    animations = []
    for name, (pose_at, length, events) in ANIMATIONS.items():
        count = max(int(round(length * FPS)), 1)
        times = [length * k / count for k in range(count + 1)] if length > 0.05 else [0.0]
        samples = [pose_at(t) for t in times]
        tracks = {}
        for node_name in sorted({n for s in samples for n in s}):
            node = rest[node_name]
            track = {}
            parts = [s.get(node_name, {}) for s in samples]
            if any("pos" in p for p in parts):
                rows = []
                for t, p in zip(times, parts):
                    dx, dy, dz = p.get("pos", (0, 0, 0))
                    # Poses are written with the gauntlet on +X; the built nodes are already mirrored.
                    rows.append((t, node.position[0] + sign * dx * model.SCALE,
                                 node.position[1] + dy * model.SCALE, node.position[2] + dz * model.SCALE))
                track["position"] = rows
            if any("rot" in p or "quat" in p for p in parts):
                rows = []
                for t, p in zip(times, parts):
                    w, x, y, z = p["quat"] if "quat" in p else geom.q_euler(*p.get("rot", (0, 0, 0)))
                    rows.append((t, w, x, sign * y, sign * z))
                track["orientation"] = rows
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
