"""Shapes and rotations for building the model: every part is a few stretched spheres and tubes."""
from __future__ import annotations

import math

import numpy as np

# Axes follow the game's creatures: +X is the character's right, +Y is forward, +Z is up.


# ---- quaternions as (w, x, y, z)

def q_axis(axis, degrees):
    half = math.radians(degrees) / 2.0
    s = math.sin(half)
    return (math.cos(half), axis[0] * s, axis[1] * s, axis[2] * s)


def q_mul(a, b):
    aw, ax, ay, az = a
    bw, bx, by, bz = b
    return (aw * bw - ax * bx - ay * by - az * bz,
            aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw)


def q_euler(rx=0.0, ry=0.0, rz=0.0):
    """Turn about X, then Y, then Z (degrees, fixed axes).

    +rx tips the top backwards, +ry tips it to the right, +rz turns to the left."""
    q = q_axis((1, 0, 0), rx)
    q = q_mul(q_axis((0, 1, 0), ry), q)
    return q_mul(q_axis((0, 0, 1), rz), q)


def q_matrix(q):
    w, x, y, z = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def q_conj(q):
    return (q[0], -q[1], -q[2], -q[3])


def q_from_matrix(m):
    """The quaternion of a rotation matrix."""
    m = np.asarray(m, dtype=float)
    trace = m[0, 0] + m[1, 1] + m[2, 2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2
        q = (0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s)
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = ((m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s)
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = ((m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s)
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = ((m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s)
    n = math.sqrt(sum(c * c for c in q))
    return tuple(c / n for c in q)


def q_between(a, b):
    """The shortest turn that takes direction a to direction b."""
    a = np.asarray(a, dtype=float) / np.linalg.norm(a)
    b = np.asarray(b, dtype=float) / np.linalg.norm(b)
    cross = np.cross(a, b)
    dot = float(a @ b)
    if dot < -0.999999:
        axis = np.cross(a, (1.0, 0.0, 0.0))
        if np.linalg.norm(axis) < 1e-6:
            axis = np.cross(a, (0.0, 1.0, 0.0))
        axis /= np.linalg.norm(axis)
        return (0.0, *axis)
    q = np.array((1.0 + dot, *cross))
    return tuple(q / np.linalg.norm(q))


def frame(along, back):
    """A rotation matrix whose columns are a hand's axes: along the fingers, across the
    knuckles, out of the back of the hand (back is squared up against along)."""
    along = np.asarray(along, dtype=float) / np.linalg.norm(along)
    back = np.asarray(back, dtype=float)
    back = back - along * (back @ along)
    back /= np.linalg.norm(back)
    return np.stack([along, np.cross(along, back), back], axis=1)


def q_slerp(a, b, t):
    a = np.array(a, dtype=float)
    b = np.array(b, dtype=float)
    dot = float(a @ b)
    if dot < 0.0:
        b, dot = -b, -dot
    if dot > 0.9995:
        q = a + t * (b - a)
    else:
        theta = math.acos(dot)
        q = (math.sin((1 - t) * theta) * a + math.sin(t * theta) * b) / math.sin(theta)
    return tuple(q / np.linalg.norm(q))


# ---- meshes: verts (n,3), uvs (n,2), faces (m,3)

class Shape:
    def __init__(self, verts, uvs, faces):
        self.verts = np.asarray(verts, dtype=float).reshape(-1, 3)
        self.uvs = np.asarray(uvs, dtype=float).reshape(-1, 2)
        self.faces = np.asarray(faces, dtype=int).reshape(-1, 3)

    def moved(self, offset):
        return Shape(self.verts + np.asarray(offset, dtype=float), self.uvs, self.faces)

    def turned(self, rx=0.0, ry=0.0, rz=0.0):
        return Shape(self.verts @ q_matrix(q_euler(rx, ry, rz)).T, self.uvs, self.faces)

    def scaled(self, sx, sy=None, sz=None):
        sy = sx if sy is None else sy
        sz = sx if sz is None else sz
        return Shape(self.verts * np.array([sx, sy, sz]), self.uvs, self.faces)

    def mirrored(self):
        """Mirror left to right (flips the winding back so the faces still point outwards)."""
        return Shape(self.verts * np.array([-1.0, 1.0, 1.0]), self.uvs, self.faces[:, ::-1])

    def painted(self, rect, mode="planar", axis="y"):
        """Place the UVs inside a texture rectangle (u0, v0, u1, v1), all in 0..1 with v up.

        planar: looked at along an axis; the whole shape fills the rectangle.
        flat:   one point of the rectangle (a plain colour swatch)."""
        u0, v0, u1, v1 = rect
        if mode == "flat":
            uvs = np.tile([(u0 + u1) / 2, (v0 + v1) / 2], (len(self.verts), 1))
            return Shape(self.verts, uvs, self.faces)
        a, b = {"y": (0, 2), "x": (1, 2), "z": (0, 1)}[axis]
        lo = self.verts.min(axis=0)
        span = np.maximum(self.verts.max(axis=0) - lo, 1e-9)
        s = (self.verts[:, a] - lo[a]) / span[a]
        t = (self.verts[:, b] - lo[b]) / span[b]
        inset = 0.04
        s = inset + s * (1 - 2 * inset)
        t = inset + t * (1 - 2 * inset)
        return Shape(self.verts, np.stack([u0 + s * (u1 - u0), v0 + t * (v1 - v0)], axis=1), self.faces)


def merge(*shapes):
    verts, uvs, faces = [], [], []
    base = 0
    for shape in shapes:
        verts.append(shape.verts)
        uvs.append(shape.uvs)
        faces.append(shape.faces + base)
        base += len(shape.verts)
    return Shape(np.concatenate(verts), np.concatenate(uvs), np.concatenate(faces))


def ball(radii, rect, around=16, up=10, profile=None):
    """A stretched sphere centred on the origin, unwrapped like a globe.

    The texture rectangle's centre column lands on the front (+Y) and the seam runs down the back.
    profile(h) with h from -1 (bottom) to 1 (top) scales the girth at that height."""
    rx, ry, rz = radii
    u0, v0, u1, v1 = rect
    verts, uvs = [], []
    for j in range(up + 1):
        lat = -math.pi / 2 + math.pi * j / up
        h = math.sin(lat)
        girth = math.cos(lat) * (profile(h) if profile else 1.0)
        for i in range(around + 1):
            lon = -math.pi + 2 * math.pi * i / around      # -pi at the back, 0 at the front
            verts.append((rx * girth * math.sin(lon), ry * girth * math.cos(lon), rz * h))
            uvs.append((u0 + (u1 - u0) * i / around, v0 + (v1 - v0) * j / up))
    faces = []
    row = around + 1
    for j in range(up):
        for i in range(around):
            a, b = j * row + i, j * row + i + 1
            c, d = a + row, b + row
            if j > 0:
                faces.append((a, c, b))
            if j < up - 1:
                faces.append((b, c, d))
    return Shape(verts, uvs, faces)


def tube(start, end, r_start, r_end, rect, around=12, rings=4, caps=True, bulge=0.0):
    """A round limb from one point to another; it can taper and swell in the middle."""
    start = np.asarray(start, dtype=float)
    end = np.asarray(end, dtype=float)
    axis = end - start
    length = np.linalg.norm(axis)
    axis = axis / length
    helper = np.array([0.0, 0.0, 1.0]) if abs(axis[2]) < 0.9 else np.array([1.0, 0.0, 0.0])
    side = np.cross(axis, helper)
    side /= np.linalg.norm(side)
    other = np.cross(axis, side)
    u0, v0, u1, v1 = rect

    steps = []
    cap = 3 if caps else 0
    for k in range(cap, 0, -1):                       # rounded start
        a = math.pi / 2 * k / cap
        steps.append((-math.sin(a) * r_start / length, math.cos(a) * r_start))
    for k in range(rings + 1):
        f = k / rings
        steps.append((f, (r_start + (r_end - r_start) * f) * (1 + bulge * math.sin(math.pi * f))))
    for k in range(1, cap + 1):                       # rounded end
        a = math.pi / 2 * k / cap
        steps.append((1 + math.sin(a) * r_end / length, math.cos(a) * r_end))

    verts, uvs = [], []
    for j, (f, radius) in enumerate(steps):
        centre = start + axis * length * f
        for i in range(around + 1):
            ang = 2 * math.pi * i / around
            verts.append(centre + radius * (math.cos(ang) * side + math.sin(ang) * other))
            uvs.append((u0 + (u1 - u0) * i / around, v0 + (v1 - v0) * j / (len(steps) - 1)))
    faces = []
    row = around + 1
    for j in range(len(steps) - 1):
        for i in range(around):
            a, b = j * row + i, j * row + i + 1
            c, d = a + row, b + row
            faces.append((a, c, b))
            faces.append((b, c, d))
    shape = Shape(verts, uvs, faces)
    # Make the faces point away from the limb's axis whatever direction it was drawn in.
    mid = shape.verts[shape.faces].mean(axis=1)
    along = (mid - start) @ axis
    outward = mid - (start + np.outer(along, axis))
    v = shape.verts
    normals = np.cross(v[shape.faces[:, 1]] - v[shape.faces[:, 0]], v[shape.faces[:, 2]] - v[shape.faces[:, 0]])
    if float((normals * outward).sum()) < 0.0:
        shape.faces = shape.faces[:, ::-1]
    return shape


def drop_degenerate(shape):
    v = shape.verts
    f = shape.faces
    area = np.linalg.norm(np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]]), axis=1)
    return Shape(shape.verts, shape.uvs, f[area > 1e-9])


def smooth_normals(shape):
    """One normal per vertex, averaged over every face that touches that point in space."""
    v = shape.verts
    f = shape.faces
    face_normals = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    keys = np.round(v, 5)
    _, welded = np.unique(keys, axis=0, return_inverse=True)
    welded = welded.reshape(-1)
    total = np.zeros((welded.max() + 1, 3))
    for k in range(3):
        np.add.at(total, welded[f[:, k]], face_normals)
    normals = total[welded]
    lengths = np.linalg.norm(normals, axis=1, keepdims=True)
    return np.where(lengths > 1e-12, normals / np.maximum(lengths, 1e-12), np.array([0.0, 0.0, 1.0]))
