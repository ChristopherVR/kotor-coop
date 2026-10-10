"""The boss's shape and skeleton.

Axes as the game's creatures: +X is his right, +Y is forward, +Z is up; metres. He wears the
gauntlet on his LEFT hand (-X), held up in front of his chest.

The body is one sculpt (sdf.Sculpt) of parts that melt into each other; each part says which
bones carry it, which is where the skin weights come from. The gauntlet, eyes, lids and teeth
are separate rigid pieces that ride on single bones.
"""
from __future__ import annotations

import numpy as np

import sdf
from sdf import Part, ellipsoid, limb

# ---- skeleton: joint name -> (parent, rest position). Every bone rests unrotated.

JOINTS = {
    "rootdummy": (None, (0.00, 0.00, 1.05)),
    "belly": ("rootdummy", (0.00, 0.30, 0.80)),
    "tail": ("rootdummy", (0.00, -0.90, 0.60)),
    "chest": ("rootdummy", (0.00, 0.00, 1.60)),
    "neck": ("chest", (0.00, 0.04, 1.95)),
    "head": ("neck", (0.00, 0.08, 2.22)),
    "jaw": ("head", (0.00, 0.26, 2.10)),
    "earl1": ("head", (-0.20, 0.00, 2.60)),
    "earl2": ("earl1", (-0.25, -0.02, 2.93)),
    "earl3": ("earl2", (-0.29, -0.05, 3.24)),
    "earr1": ("head", (0.20, 0.00, 2.60)),
    "earr2": ("earr1", (0.25, -0.02, 2.93)),
    "earr3": ("earr2", (0.29, -0.05, 3.24)),
    # the free arm (his right)
    "uparmr": ("chest", (0.90, 0.08, 1.62)),
    "forearmr": ("uparmr", (1.20, 0.16, 1.16)),
    "handr": ("forearmr", (1.20, 0.40, 0.84)),
    # the gauntlet arm (his left)
    "uparmg": ("chest", (-0.90, 0.08, 1.62)),
    "forearmg": ("uparmg", (-1.18, 0.30, 1.22)),
    "handg": ("forearmg", (-0.90, 0.80, 1.55)),
    # legs
    "thighl": ("rootdummy", (-0.52, 0.10, 0.44)),
    "footl": ("thighl", (-0.56, 0.24, 0.16)),
    "thighr": ("rootdummy", (0.52, 0.10, 0.44)),
    "footr": ("thighr", (0.56, 0.24, 0.16)),
}

EYE_L = np.array((-0.185, 0.395, 2.335))
EYE_R = np.array((0.185, 0.395, 2.335))
EYE_RADIUS = 0.118


def J(name):
    return np.array(JOINTS[name][1], dtype=float)


def _step(lo, hi, x):
    t = np.clip((x - lo) / (hi - lo), 0.0, 1.0)
    return t * t * (3 - 2 * t)


def _torso_bones(p):
    z, y = p[:, 2], p[:, 1]
    chest = _step(1.25, 1.75, z)
    belly = (1.0 - _step(0.85, 1.35, z)) * _step(-0.35, 0.45, y) * 0.75
    return {"chest": chest, "belly": belly, "rootdummy": np.maximum(1.0 - chest - belly, 0.0)}


def _mirror(point, side):
    return (point[0] * side, point[1], point[2])


def body_parts():
    parts = [
        # ---- the pear: a huge belly under a narrower chest
        Part("torso", ellipsoid((0, 0.00, 1.08), (1.10, 0.95, 1.02)), _torso_bones, 0.0),
        Part("gut", ellipsoid((0, 0.12, 0.84), (1.17, 1.03, 0.80)), _torso_bones, 0.25),
        Part("rump", ellipsoid((0, -0.35, 0.80), (1.00, 0.72, 0.72)), [("rootdummy", 1.0)], 0.25),
        Part("chestmass", ellipsoid((0, 0.02, 1.66), (0.84, 0.70, 0.52)), [("chest", 1.0)], 0.22),
        Part("neckroll", ellipsoid((0, 0.05, 1.98), (0.60, 0.54, 0.30)), [("neck", 0.7), ("chest", 0.3)], 0.20),
        Part("tail", ellipsoid((0, -0.98, 0.58), (0.25, 0.22, 0.24)), [("tail", 1.0)], 0.08),
        Part("tailtuft", ellipsoid((0, -1.10, 0.70), (0.15, 0.13, 0.16)), [("tail", 1.0)], 0.08),

        # ---- head: small for the body, wide at the cheeks
        Part("skull", ellipsoid((0, 0.08, 2.30), (0.47, 0.46, 0.43)), [("head", 1.0)], 0.16),
        Part("crown", ellipsoid((0, 0.02, 2.50), (0.37, 0.36, 0.26)), [("head", 1.0)], 0.14),
        Part("jowl", ellipsoid((0, 0.16, 2.05), (0.54, 0.42, 0.25)), [("head", 0.6), ("jaw", 0.4)], 0.12),
        Part("chin", ellipsoid((0, 0.43, 1.975), (0.20, 0.14, 0.095)), [("jaw", 1.0)], 0.05),
        Part("bridge", ellipsoid((0, 0.50, 2.235), (0.10, 0.12, 0.10)), [("head", 1.0)], 0.06),
        Part("nose", ellipsoid((0, 0.665, 2.225), (0.080, 0.052, 0.052)), [("head", 1.0)], 0.03),
    ]
    for side, tag in ((-1, "l"), (1, "r")):
        ear = ("earl", "earr")[side > 0]
        m = lambda point: _mirror(point, side)
        flat = ((0.0, 1.0, 0.12), 0.30)
        parts += [
            # cheeks, and three tufts of fur combed outwards
            Part(f"cheek{tag}", ellipsoid(m((0.37, 0.26, 2.12)), (0.33, 0.29, 0.25)), [("head", 1.0)], 0.10),
            Part(f"tuft{tag}a", limb(m((0.52, 0.22, 2.10)), m((0.76, 0.16, 2.04)), 0.20, 0.085), [("head", 1.0)], 0.09),
            Part(f"tuft{tag}b", limb(m((0.48, 0.22, 2.02)), m((0.66, 0.18, 1.90)), 0.17, 0.08), [("head", 1.0)], 0.09),
            Part(f"tuft{tag}c", limb(m((0.52, 0.20, 2.18)), m((0.72, 0.13, 2.22)), 0.15, 0.07), [("head", 1.0)], 0.09),
            # the muzzle's two pads, and a heavy brow over a shallow socket
            Part(f"pad{tag}", ellipsoid(m((0.125, 0.515, 2.125)), (0.185, 0.165, 0.135)), [("head", 1.0)], 0.045),
            Part(f"smile{tag}", limb(m((0.0, 0.60, 2.012)), m((0.27, 0.47, 2.075)), 0.030, 0.022), None, 0.02, cut=True),
            Part(f"brow{tag}", limb(m((0.05, 0.44, 2.455)), m((0.33, 0.36, 2.465)), 0.060, 0.045), [("head", 1.0)], 0.07),
            Part(f"socket{tag}", ellipsoid(m((0.185, 0.47, 2.335)), (0.118, 0.10, 0.118)), None, 0.03, cut=True),
            # ears: a long flattened blade in three bones, scooped out at the front
            Part(f"ear{tag}1", limb(m((0.20, 0.00, 2.55)), m((0.25, -0.02, 2.93)), 0.105, 0.175, flat), [(ear + "1", 1.0)], 0.07),
            Part(f"ear{tag}2", limb(m((0.25, -0.02, 2.93)), m((0.29, -0.05, 3.24)), 0.175, 0.150, flat), [(ear + "2", 1.0)], 0.03),
            Part(f"ear{tag}3", limb(m((0.29, -0.05, 3.24)), m((0.31, -0.08, 3.52)), 0.150, 0.045, flat), [(ear + "3", 1.0)], 0.03),
            Part(f"earscoop{tag}", limb(m((0.235, 0.085, 2.78)), m((0.295, 0.045, 3.38)), 0.085, 0.045,
                                        ((0.0, 1.0, 0.12), 0.55)), None, 0.04, cut=True),
            # legs: a stub of thigh and a big flat foot with three toes
            Part(f"thigh{tag}", limb(m((0.50, 0.08, 0.50)), m((0.56, 0.24, 0.20)), 0.34, 0.25),
                 [("thigh" + tag, 0.7), ("rootdummy", 0.3)], 0.14),
            Part(f"foot{tag}", ellipsoid(m((0.57, 0.60, 0.135)), (0.27, 0.50, 0.135)), [("foot" + tag, 1.0)], 0.09),
            Part(f"heel{tag}", ellipsoid(m((0.56, 0.24, 0.15)), (0.24, 0.24, 0.15)),
                 [("foot" + tag, 0.6), ("thigh" + tag, 0.4)], 0.08),
        ]
        for k, dx in enumerate((-0.17, 0.0, 0.17)):
            parts.append(Part(f"toe{tag}{k}", ellipsoid(m((0.57 + dx * side, 1.06 - abs(dx) * 0.25, 0.115)),
                                                        (0.095, 0.13, 0.105)), [("foot" + tag, 1.0)], 0.05))

    # ---- the open grin between the pads and the chin
    parts.append(Part("mouth", ellipsoid((0, 0.56, 2.022), (0.115, 0.10, 0.040)), None, 0.02, cut=True))

    # ---- the free arm: short, soft, ending in a puffy three-fingered glove
    shoulder, elbow, wrist = J("uparmr"), J("forearmr"), J("handr")
    hand = wrist + np.array((0.00, 0.10, -0.13))
    parts += [
        Part("shoulderr", ellipsoid(shoulder + (-0.06, 0, 0.0), (0.34, 0.30, 0.30)), [("uparmr", 0.5), ("chest", 0.5)], 0.14),
        Part("uparmr", limb(shoulder, elbow, 0.25, 0.21), [("uparmr", 1.0)], 0.10),
        Part("forearmr", limb(elbow, wrist, 0.20, 0.165), [("forearmr", 1.0)], 0.07),
        Part("cuffr", ellipsoid(wrist + (0, 0.02, -0.02), (0.215, 0.20, 0.085),
                                _tilt(wrist - elbow)), [("handr", 1.0)], 0.03),
        Part("palmr", ellipsoid(hand, (0.185, 0.16, 0.19)), [("handr", 1.0)], 0.05),
        Part("thumbr", limb(hand + (-0.13, 0.06, 0.04), hand + (-0.25, 0.13, -0.03), 0.075, 0.062), [("handr", 1.0)], 0.04),
    ]
    for k, dx in enumerate((-0.115, 0.0, 0.115)):
        base = hand + (dx, 0.04, -0.13)
        parts.append(Part(f"fingerr{k}", limb(base, base + (dx * 0.25, 0.07, -0.17), 0.075, 0.064), [("handr", 1.0)], 0.035))

    # ---- the gauntlet arm: only the upper arm and the first of the forearm show; the rest is metal
    shoulder, elbow, wrist = J("uparmg"), J("forearmg"), J("handg")
    parts += [
        Part("shoulderg", ellipsoid(shoulder + (0.06, 0, 0.0), (0.34, 0.30, 0.30)), [("uparmg", 0.5), ("chest", 0.5)], 0.14),
        Part("uparmg", limb(shoulder, elbow, 0.25, 0.22), [("uparmg", 1.0)], 0.10),
        Part("forearmg", limb(elbow, elbow + (wrist - elbow) * 0.55, 0.21, 0.19), [("forearmg", 1.0)], 0.07),
    ]
    return parts


def _tilt(direction):
    """A rotation whose third axis lies along a direction (for a disc set across a limb)."""
    z = np.asarray(direction, dtype=float)
    z = z / np.linalg.norm(z)
    helper = np.array((0.0, 1.0, 0.0)) if abs(z[1]) < 0.9 else np.array((1.0, 0.0, 0.0))
    x = np.cross(helper, z)
    x /= np.linalg.norm(x)
    y = np.cross(z, x)
    return np.stack([x, y, z], axis=1)


BODY_BOX = ((-1.45, -1.40, -0.05), (1.65, 1.35, 3.65))


def body(cell=0.02, triangles=11000):
    """The skin: (sculpt, verts, faces)."""
    sculpt = sdf.Sculpt(body_parts())
    verts, faces = sculpt.mesh(*BODY_BOX, cell, triangles)
    return sculpt, verts, faces


# ---- the gauntlet: rigid plates on the forearm, the hand and each finger joint

def hand_frame():
    """Unit vectors of the gauntlet hand: along the forearm into the fingers, across the knuckles
    (toward his thumb, which is on the inside), and out of the back of the hand."""
    along = J("handg") - J("forearmg")
    along /= np.linalg.norm(along)
    back = np.array((-0.35, 0.90, -0.10))
    back -= along * (back @ along)
    back /= np.linalg.norm(back)
    across = np.cross(along, back)          # points to his right: the thumb side of a left hand
    return along, across, back


def _in_hand(point):
    along, across, back = hand_frame()
    return J("handg") + along * point[0] + across * point[1] + back * point[2]


HAND_TURN = None  # filled in below: columns are the hand's axes


def _hand_turn():
    along, across, back = hand_frame()
    return np.stack([along, across, back], axis=1)


FINGERS = ("index", "middle", "ring", "little")
FINGER_ACROSS = (0.225, 0.075, -0.075, -0.22)   # the index finger is next to the thumb
FINGER_LENGTH = (0.21, 0.23, 0.21, 0.17)
KNUCKLE_ALONG = 0.40
THUMB = ((0.11, 0.29, -0.03), (0.26, 0.42, -0.03), (0.43, 0.48, -0.03))   # base, joint, tip


def gauntlet_joints():
    """Extra joints for the fingers: name -> (parent, rest position)."""
    joints = {}
    for name, across, length in zip(FINGERS, FINGER_ACROSS, FINGER_LENGTH):
        joints[f"g{name}1"] = ("handg", tuple(_in_hand((KNUCKLE_ALONG, across, 0.0))))
        joints[f"g{name}2"] = (f"g{name}1", tuple(_in_hand((KNUCKLE_ALONG + length, across, 0.0))))
    joints["gthumb1"] = ("handg", tuple(_in_hand(THUMB[0])))
    joints["gthumb2"] = ("gthumb1", tuple(_in_hand(THUMB[1])))
    return joints


def gauntlet_pieces(cell=0.011):
    """Rigid metal pieces: {bone name: (verts, faces, the sculpt they came from)} in rest-pose world positions."""
    turn = _hand_turn()
    elbow, wrist = J("forearmg"), J("handg")
    arm = wrist - elbow
    pieces = {}

    def build(parts, triangles):
        pts = np.concatenate([np.atleast_2d(np.asarray(p, dtype=float)) for p in _extent(parts)])
        lo, hi = pts.min(axis=0) - 0.06, pts.max(axis=0) + 0.06
        figure = sdf.Sculpt(parts)
        return (*figure.mesh(lo, hi, cell, triangles), figure)

    # The sleeve: a flared bracer with raised bands.
    sleeve = [
        Part("sleeve", limb(elbow + arm * 0.34, elbow + arm * 0.98, 0.255, 0.240), None, 0.0),
        Part("flare", limb(elbow + arm * 0.30, elbow + arm * 0.44, 0.315, 0.262), None, 0.025),
        Part("band1", limb(elbow + arm * 0.60, elbow + arm * 0.66, 0.272, 0.272), None, 0.012),
        Part("band2", limb(elbow + arm * 0.88, elbow + arm * 0.96, 0.275, 0.275), None, 0.012),
        Part("hollow", limb(elbow + arm * 0.10, elbow + arm * 0.42, 0.225, 0.215), None, 0.02, cut=True),
    ]
    sleeve[0].extent = [elbow + arm * 0.2 - 0.36, wrist + 0.30]
    pieces["forearmg"] = build(sleeve, 900)

    # The hand: a thick plate with a ridge of knuckles and a raised boss for the big stone.
    hand = [
        Part("palm", sdf.box(_in_hand((0.19, 0.0, -0.02)), (0.235, 0.32, 0.145), 0.09, turn), None, 0.0),
        Part("wristcap", limb(_in_hand((-0.08, 0, -0.02)), _in_hand((0.04, 0, -0.02)), 0.245, 0.235), None, 0.05),
        Part("ridge", limb(_in_hand((0.37, -0.27, 0.07)), _in_hand((0.37, 0.27, 0.07)), 0.095, 0.095), None, 0.04),
        Part("boss", ellipsoid(_in_hand((0.17, 0.0, 0.125)), (0.165, 0.165, 0.05), turn), None, 0.03),
        Part("thumbmount", ellipsoid(_in_hand((0.09, 0.27, -0.03)), (0.13, 0.11, 0.12), turn), None, 0.04),
    ]
    hand[0].extent = [_in_hand((-0.2, -0.4, -0.25)), _in_hand((0.5, 0.45, 0.25))]
    pieces["handg"] = build(hand, 1100)

    # Fingers: two armoured joints each.
    for name, across, length in zip(FINGERS, FINGER_ACROSS, FINGER_LENGTH):
        a = _in_hand((KNUCKLE_ALONG, across, 0.0))
        b = _in_hand((KNUCKLE_ALONG + length, across, 0.0))
        c = _in_hand((KNUCKLE_ALONG + length * 1.85, across, 0.0))
        r = 0.072 if name != "little" else 0.062
        first = [Part("a", limb(a, b - (b - a) * 0.12, r, r * 0.95), None, 0.0),
                 Part("k", ellipsoid(a + (b - a) * 0.15 + hand_frame()[2] * r * 0.55, (r * 0.8, r * 0.75, r * 0.5), turn), None, 0.02)]
        first[0].extent = [a - 0.12, b + 0.12]
        second = [Part("a", limb(b, c, r * 0.95, r * 0.72), None, 0.0),
                  Part("k", ellipsoid(b + (c - b) * 0.1 + hand_frame()[2] * r * 0.5, (r * 0.7, r * 0.7, r * 0.45), turn), None, 0.02)]
        second[0].extent = [b - 0.12, c + 0.12]
        pieces[f"g{name}1"] = build(first, 220)
        pieces[f"g{name}2"] = build(second, 220)
    a, b, c = (_in_hand(point) for point in THUMB)
    first = [Part("a", limb(a, b - (b - a) * 0.1, 0.088, 0.082), None, 0.0)]
    first[0].extent = [a - 0.14, b + 0.14]
    second = [Part("a", limb(b, c, 0.082, 0.062), None, 0.0)]
    second[0].extent = [b - 0.14, c + 0.14]
    pieces["gthumb1"] = build(first, 220)
    pieces["gthumb2"] = build(second, 220)
    return pieces


def _extent(parts):
    return parts[0].extent


# name -> (position in the hand's own axes, radius). Four on the knuckles, one on the thumb's
# mount, the big one on the back of the hand.
STONES = {
    "power": ((0.385, 0.225, 0.195), 0.068),
    "space": ((0.390, 0.075, 0.200), 0.068),
    "reality": ((0.390, -0.075, 0.200), 0.068),
    "soul": ((0.385, -0.22, 0.190), 0.062),
    "time": ((0.09, 0.30, 0.115), 0.062),
    "mind": ((0.17, 0.0, 0.235), 0.120),
}


def stones():
    """{name: (verts, faces)} of the six gems, set into the hand piece."""
    turn = _hand_turn()
    out = {}
    for name, (where, radius) in STONES.items():
        verts, faces = sdf.gem(radius, squash=0.62)
        out[name] = (verts @ turn.T + _in_hand(where), faces)
    return out


# ---- rigid face pieces

LID_REST_DEGREES = 22.0   # how far the lids are tipped over the eyes at rest: the unbothered look
LID_SHUT_DEGREES = 82.0   # tipped this far the eye is covered


def eyelid(centre):
    """The upper lid: a cap of a sphere just outside the eyeball, modelled wide open, its pole
    straight up so that it covers only the top of the eye. Turning its node about X by a
    NEGATIVE angle tips it forward over the eye."""
    radius = EYE_RADIUS * 1.07
    verts, faces = [], []
    around, up = 14, 6
    for j in range(up + 1):
        lat = np.radians(18 + (90 - 18) * j / up)          # from the lid's edge up to the pole
        for i in range(around + 1):
            lon = np.radians(-100 + 200 * i / around)       # the front half and a little more
            verts.append((np.cos(lat) * np.sin(lon), np.cos(lat) * np.cos(lon), np.sin(lat)))
    row = around + 1
    for j in range(up):
        for i in range(around):
            a, b, c, d = j * row + i, j * row + i + 1, (j + 1) * row + i, (j + 1) * row + i + 1
            faces += [(a, b, c), (b, d, c)]
    verts = np.array(verts) * radius
    return verts + centre, np.array(faces)[:, ::-1]


def teeth():
    """Two buck teeth under the nose, as one small piece that rides on the head."""
    parts = []
    for side in (-1, 1):
        part = Part("t", sdf.box((side * 0.043, 0.635, 1.985), (0.038, 0.022, 0.060), 0.016), None, 0.0)
        parts.append(part)
    lo, hi = np.array((-0.12, 0.58, 1.89)), np.array((0.12, 0.69, 2.08))
    return sdf.Sculpt(parts).mesh(lo, hi, 0.006, 260)


def mouth_inside():
    """A dark bowl behind the lips so the open grin does not show the back of the head."""
    verts, faces = sdf.uv_sphere((0.15, 0.10, 0.07), (0.0, 0.50, 2.02), around=10, up=6)
    return verts, faces[:, ::-1]      # seen from inside
