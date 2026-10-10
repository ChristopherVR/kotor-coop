"""What colour every point of him is.

The texture is painted from the model, not onto a flat sheet: each function here is given
points on a surface and which way they face, and answers with colours. bake.py asks them for
every texel. The look aimed for is the game's own creature textures: muted, hand-painted, with
the shading of folds and undersides painted in, because the engine's lighting is too plain to
give a round body any depth by itself.
"""
from __future__ import annotations

import numpy as np

GREY = np.array((0.560, 0.570, 0.600))
GREY_DARK = np.array((0.360, 0.365, 0.395))
WHITE = np.array((0.965, 0.955, 0.920))
CREAM = np.array((0.930, 0.915, 0.870))
PINK = np.array((0.820, 0.500, 0.520))
NOSE = np.array((0.660, 0.360, 0.390))
CHARCOAL = np.array((0.105, 0.100, 0.115))
MOUTH = np.array((0.300, 0.055, 0.070))
IVORY = np.array((0.930, 0.900, 0.790))
GOLD = np.array((0.780, 0.585, 0.205))
GOLD_DEEP = np.array((0.300, 0.190, 0.060))
GOLD_BRIGHT = np.array((1.000, 0.880, 0.520))

STONE_COLOURS = {
    "power": (0.62, 0.20, 0.95),
    "space": (0.18, 0.42, 1.00),
    "reality": (0.95, 0.12, 0.14),
    "soul": (1.00, 0.52, 0.08),
    "time": (0.16, 0.85, 0.30),
    "mind": (1.00, 0.78, 0.05),
}


def step(lo, hi, x):
    t = np.clip((x - lo) / (hi - lo), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _hash(ix, iy, iz, seed):
    n = (ix * 374761393 + iy * 668265263 + iz * 2147483647 + seed * 974711) & 0xFFFFFFFF
    n = ((n ^ (n >> 13)) * 1274126177) & 0xFFFFFFFF
    return ((n ^ (n >> 16)) & 0xFFFF) / 65535.0


def noise(points, scale, seed=0):
    """Smooth random values in -1..1 that vary over a distance of about 1/scale."""
    p = points * np.asarray(scale, dtype=float)
    base = np.floor(p).astype(np.int64)
    f = p - base
    f = f * f * (3.0 - 2.0 * f)
    total = np.zeros(len(points))
    for dx in (0, 1):
        for dy in (0, 1):
            for dz in (0, 1):
                w = (f[:, 0] if dx else 1 - f[:, 0]) * (f[:, 1] if dy else 1 - f[:, 1]) * (f[:, 2] if dz else 1 - f[:, 2])
                total += w * _hash(base[:, 0] + dx, base[:, 1] + dy, base[:, 2] + dz, seed)
    return total * 2.0 - 1.0


def fur(points, strength):
    """Short strokes combed downwards, over a slow mottle."""
    strokes = noise(points, (46, 46, 13), 1) * 0.55 + noise(points, (95, 95, 30), 2) * 0.35
    mottle = noise(points, (5, 5, 5), 3)
    return 1.0 + strength * (strokes + 0.6 * mottle)


def occlusion(sculpt, points, normals):
    """How open the surface is to the sky: low in folds and where parts press together."""
    total = np.zeros(len(points))
    for reach in (0.03, 0.08, 0.18):
        total += np.clip(sculpt.field(points + normals * reach) / reach, 0.0, 1.0)
    return total / 3.0


def _mix(a, b, t):
    return a + (b - a) * t[:, None]


def skin(sculpt):
    """The fur: grey coat, white belly, chest and lower face, pink inside the ears."""
    parts = {part.name: part for part in sculpt.parts}

    def near(names, points):
        return np.min([parts[name](points) for name in names if name in parts], axis=0)

    tufts = [n for n in parts if n.startswith("tuft") or n.startswith("cheek") or n.startswith("pad") or n in ("chin", "jowl")]
    glove = [n for n in parts if n in ("cuffr", "palmr", "thumbr") or n.startswith("fingerr")]
    brows = [n for n in parts if n.startswith("brow")]
    scoops = [n for n in parts if n.startswith("earscoop")]
    sockets = [n for n in parts if n.startswith("socket")]
    smiles = [n for n in parts if n.startswith("smile")]
    tail = ["tail", "tailtuft"]

    def paint(points, normals):
        x, y, z = points[:, 0], points[:, 1], points[:, 2]
        ax = np.abs(x)

        # the coat: darker down the back and on top of the head
        back = step(0.25, -0.85, normals[:, 1])
        colour = _mix(np.tile(GREY, (len(points), 1)), np.tile(GREY_DARK, (len(points), 1)), back * 0.55)
        colour = _mix(colour, np.tile(GREY_DARK, (len(points), 1)), step(3.30, 3.52, z) * 0.35)  # ear tips

        # white: the belly, up the chest to the chin, the lower face and its tufts, the tail
        belly = (1 - step(0.84, 1.02, np.hypot(x / 0.82, (z - 1.00) / 0.93))) * step(-0.08, 0.22, y) * (1 - step(1.78, 1.98, z))
        throat = step(0.22, 0.40, y) * (1 - step(0.34, 0.56, ax)) * step(1.62, 1.76, z) * (1 - step(1.98, 2.06, z))
        face = step(2.34, 2.22, z) * step(0.02, 0.20, y) * step(1.86, 1.92, z)
        blaze = step(0.085, 0.045, ax) * step(2.20, 2.30, z) * (1 - step(2.40, 2.52, z)) * step(0.30, 0.40, y)
        fluff = step(0.05, 0.0, near(tufts, points)) * step(-0.05, 0.10, y) * step(2.40, 2.28, z)
        tailed = step(0.05, 0.01, near(tail, points))
        white = np.clip(np.maximum.reduce([belly, throat, face, blaze, fluff, tailed]), 0.0, 1.0)
        colour = _mix(colour, np.tile(WHITE, (len(points), 1)), white)

        # the glove on his free hand
        gloved = step(0.03, 0.005, near(glove, points))
        colour = _mix(colour, np.tile(CREAM, (len(points), 1)), gloved)

        strength = 0.085 - 0.05 * np.maximum(white, gloved)
        colour = colour * (1.0 + (fur(points, 1.0) - 1.0) * strength)[:, None]

        # pink inside the ears, deeper toward the root
        inside = step(0.035, 0.0, near(scoops, points)) * step(0.0, 0.25, normals[:, 1])
        ear_pink = _mix(np.tile(PINK, (len(points), 1)), np.tile(PINK * 0.72, (len(points), 1)), step(3.1, 2.7, z))
        colour = np.where(inside[:, None] > 0, _mix(colour, ear_pink, inside), colour)

        # the face's dark accents: brows, the rims of the eyes, the nose, the line of the mouth
        browed = step(0.012, -0.01, near(brows, points)) * step(-0.2, 0.3, normals[:, 2] + normals[:, 1])
        colour = _mix(colour, np.tile(CHARCOAL, (len(points), 1)), browed * 0.92)
        rim = step(0.016, 0.0, np.abs(near(sockets, points) - 0.002))
        colour = _mix(colour, np.tile(CHARCOAL, (len(points), 1)), rim * 0.55)
        nosed = step(0.014, 0.0, parts["nose"](points))
        colour = _mix(colour, np.tile(NOSE, (len(points), 1)), nosed)
        lipped = np.maximum(step(0.016, 0.002, parts["mouth"](points)), step(0.014, 0.002, near(smiles, points)))
        colour = _mix(colour, np.tile(MOUTH * 0.6, (len(points), 1)), lipped)

        # painted light: open sky above, shadow in the folds and underneath
        ao = occlusion(sculpt, points, normals)
        shade = (0.66 + 0.34 * ao) * (0.88 + 0.12 * step(-1.0, 0.9, normals[:, 2]))
        # the cool cast of shadow that the game's own creature textures carry
        cool = np.array((0.92, 0.95, 1.04))
        colour = colour * shade[:, None] * _mix(np.tile(cool, (len(points), 1)), np.ones((len(points), 3)), ao)
        return np.clip(colour, 0.0, 1.0)

    return paint


def eye(centre, side):
    """An eyeball: a big dark pupil looking a little inward and down, a glint, a shaded white."""
    centre = np.asarray(centre, dtype=float)
    look = np.array((-side * 0.14, 1.0, -0.10))
    look /= np.linalg.norm(look)
    glint = np.array((-side * 0.16 - 0.22, 1.0, 0.02))
    glint /= np.linalg.norm(glint)

    def paint(points, normals):
        d = points - centre
        d /= np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-9)
        angle = np.arccos(np.clip(d @ look, -1.0, 1.0))
        white = np.tile(np.array((0.93, 0.92, 0.89)), (len(points), 1))
        # shadow from the lid above, and a warm edge
        white *= (0.70 + 0.30 * step(0.75, -0.2, d[:, 2]))[:, None]
        white = _mix(white, np.tile(np.array((0.80, 0.62, 0.60)), (len(points), 1)), step(0.95, 1.35, angle) * 0.5)
        iris = np.tile(np.array((0.22, 0.13, 0.07)), (len(points), 1))
        colour = _mix(white, iris, step(0.50, 0.45, angle))
        colour = _mix(colour, np.tile(np.array((0.02, 0.02, 0.03)), (len(points), 1)), step(0.42, 0.38, angle))
        spark = np.arccos(np.clip(d @ glint, -1.0, 1.0))
        colour = _mix(colour, np.ones((len(points), 3)), step(0.13, 0.09, spark))
        return colour

    return paint


def lid(centre):
    centre = np.asarray(centre, dtype=float)

    def paint(points, normals):
        d = points - centre
        d /= np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-9)
        colour = np.tile(GREY * 0.92, (len(points), 1)) * (1.0 + (fur(points, 1.0) - 1.0) * 0.06)[:, None]
        # the lid's edge is dark, like a lash line (the cap's rim is what is lowest when it hangs)
        rim = np.linalg.norm(points - centre, axis=1)
        low = points[:, 2] - centre[2]
        edge = step(low.min() + 0.035, low.min() + 0.012, low)
        return np.clip(_mix(colour, np.tile(CHARCOAL, (len(points), 1)), edge * 0.8), 0, 1)

    return paint


def teeth(points, normals):
    colour = np.tile(IVORY, (len(points), 1))
    colour *= (0.72 + 0.28 * step(2.03, 1.95, points[:, 2]))[:, None]          # shadow under the lip
    gap = step(0.010, 0.003, np.abs(points[:, 0]))
    return np.clip(_mix(colour, np.tile(IVORY * 0.45, (len(points), 1)), gap), 0, 1)


def mouth_inside(points, normals):
    depth = step(0.60, 0.42, points[:, 1])
    return _mix(np.tile(MOUTH, (len(points), 1)), np.tile(np.array((0.05, 0.01, 0.015)), (len(points), 1)), depth)


def metal(sculpt, axis_from=None, axis_to=None):
    """Beaten gold. The fourth channel is how much of the surface is paint and how much mirror:
    worn high spots mirror the room, the recesses stay dull."""
    def paint(points, normals):
        ao = np.zeros(len(points))
        for reach in (0.012, 0.03, 0.07):
            ao += np.clip(sculpt.field(points + normals * reach) / reach, 0.0, 1.0)
        ao /= 3.0
        # Slow tonal drift, like a casting, and a few long scratches; nothing fine-grained,
        # which would read as cloth.
        cast = noise(points, (9, 9, 9), 11) * 0.7 + noise(points, (20, 20, 20), 12) * 0.3
        scratches = np.abs(noise(points, (150, 14, 150), 13))
        colour = np.tile(GOLD, (len(points), 1)) * (1.0 + 0.07 * cast)[:, None]
        # worn bright where the surface is most exposed, tarnished dark in the recesses
        colour = _mix(colour, np.tile(GOLD_BRIGHT, (len(points), 1)), step(0.86, 1.0, ao) * 0.30)
        colour = _mix(colour, np.tile(GOLD_BRIGHT, (len(points), 1)), step(0.05, 0.0, scratches) * step(0.7, 1.0, ao) * 0.45)
        colour = _mix(np.tile(GOLD_DEEP, (len(points), 1)), colour, step(0.0, 0.60, ao))
        if axis_from is not None:
            # two engraved rings around the sleeve
            along = (points - axis_from) @ (axis_to - axis_from) / ((axis_to - axis_from) @ (axis_to - axis_from))
            for at in (0.50, 0.76):
                line = step(0.012, 0.004, np.abs(along - at))
                colour = _mix(colour, np.tile(GOLD_DEEP, (len(points), 1)), line * 0.8)
        colour *= (0.86 + 0.14 * step(-1.0, 1.0, normals[:, 2]))[:, None]
        mirror = 0.50 + 0.45 * (1.0 - step(0.25, 0.9, ao))      # alpha: lower mirrors more
        return np.concatenate([np.clip(colour, 0, 1), mirror[:, None]], axis=1)

    return paint


def stone(name, centre, outward):
    colour = np.array(STONE_COLOURS[name])
    centre = np.asarray(centre, dtype=float)
    outward = np.asarray(outward, dtype=float)

    def paint(points, normals):
        facing = np.clip(normals @ outward, 0.0, 1.0)
        lit = _mix(np.tile(colour * 0.50, (len(points), 1)), np.tile(np.minimum(colour * 1.12 + 0.10, 1.0), (len(points), 1)),
                   facing ** 1.5)
        return np.concatenate([np.clip(lit, 0, 1), np.ones((len(points), 1))], axis=1)

    return paint
