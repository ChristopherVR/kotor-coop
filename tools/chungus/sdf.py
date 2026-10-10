"""Sculpting by distance fields: shapes that melt into each other instead of sitting side by side.

A part is a function giving, for any point, how far it is from the part's surface (negative
inside). Parts are joined with a soft minimum so the join is a fillet, not a crease, and the
surface where the joined field is zero is turned into triangles (marching cubes) and thinned
out to a triangle budget.
"""
from __future__ import annotations

import numpy as np


def _v(p):
    return np.asarray(p, dtype=float)


class Part:
    """One named piece of the sculpt: its field, the bones that carry it, and how softly it joins."""

    def __init__(self, name, field, bones, blend=0.1, cut=False):
        self.name = name
        self.field = field
        # [(bone name, share)] or a function of the points returning {bone: share array}
        self.bones = bones
        self.blend = blend
        self.cut = cut

    def __call__(self, points):
        return self.field(points)


def ellipsoid(centre, radii, turn=None):
    """A stretched ball. turn is an optional 3x3 rotation of the ball about its centre."""
    centre, radii = _v(centre), _v(radii)

    def field(p):
        q = p - centre
        if turn is not None:
            q = q @ turn          # into the ball's own axes
        k0 = np.linalg.norm(q / radii, axis=1)
        k1 = np.linalg.norm(q / (radii * radii), axis=1)
        return np.where(k1 > 1e-9, k0 * (k0 - 1.0) / np.maximum(k1, 1e-9), -radii.min())

    return field


def limb(a, b, ra, rb, flatten=None):
    """A round limb from a to b whose radius goes from ra to rb.

    flatten = (axis vector, factor) squashes it along that axis (an ear is a flattened limb)."""
    a, b = _v(a), _v(b)
    squash = None
    if flatten is not None:
        axis = _v(flatten[0]) / np.linalg.norm(flatten[0])
        squash = (axis, float(flatten[1]))

    def field(p):
        q = p
        qa, qb = a, b
        if squash is not None:
            axis, factor = squash
            # Stretch space along the axis so a round limb there is a flat one here.
            stretch = lambda x: x + np.multiply.outer(x @ axis, axis) * (1.0 / factor - 1.0)
            q, qa, qb = stretch(p), stretch(a[None, :])[0], stretch(b[None, :])[0]
        ab = qb - qa
        t = np.clip(((q - qa) @ ab) / (ab @ ab), 0.0, 1.0)
        d = np.linalg.norm(q - (qa + np.outer(t, ab)), axis=1) - (ra + (rb - ra) * t)
        return d * (squash[1] if squash is not None else 1.0)

    return field


def along(a, b):
    """How far along a limb each point is, 0 at a and 1 at b (for sharing a limb between two bones)."""
    a, b = _v(a), _v(b)
    ab = b - a
    return lambda p: np.clip(((p - a) @ ab) / (ab @ ab), 0.0, 1.0)


def box(centre, half, round_=0.03, turn=None):
    centre, half = _v(centre), _v(half)

    def field(p):
        q = p - centre
        if turn is not None:
            q = q @ turn
        d = np.abs(q) - (half - round_)
        return np.linalg.norm(np.maximum(d, 0.0), axis=1) + np.minimum(d.max(axis=1), 0.0) - round_

    return field


def soft_min(a, b, k):
    """The smaller of two fields, with the corner between them rounded over a distance k."""
    if k <= 0:
        return np.minimum(a, b)
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0.0, 1.0)
    return b + (a - b) * h - k * h * (1.0 - h)


def soft_cut(a, b, k):
    """Field a with field b carved out of it, the rim rounded over k."""
    return -soft_min(-a, b, k)


class Sculpt:
    def __init__(self, parts):
        self.parts = list(parts)

    def field(self, points):
        points = np.asarray(points, dtype=float)
        total = None
        for part in self.parts:
            if part.cut:
                continue
            d = part(points)
            total = d if total is None else soft_min(total, d, part.blend)
        for part in self.parts:
            if part.cut:
                total = soft_cut(total, part(points), part.blend)
        return total

    def normals(self, points, step=0.004):
        """Which way the surface faces at each point (the field's slope)."""
        points = np.asarray(points, dtype=float)
        grad = np.zeros_like(points)
        for k in range(3):
            offset = np.zeros(3)
            offset[k] = step
            grad[:, k] = self.field(points + offset) - self.field(points - offset)
        length = np.linalg.norm(grad, axis=1, keepdims=True)
        return grad / np.maximum(length, 1e-12)

    def bone_weights(self, points, bone_names, softness=0.07, keep=4):
        """How much each bone carries each point: a point belongs to the parts it is in or near,
        and each part hands it to its bones. Returns (n, len(bone_names)) with at most `keep`
        non-zero shares per point, summing to 1."""
        points = np.asarray(points, dtype=float)
        index = {name: i for i, name in enumerate(bone_names)}
        weights = np.zeros((len(points), len(bone_names)))
        for part in self.parts:
            if part.cut or not part.bones:
                continue
            near = np.exp(-np.maximum(part(points), 0.0) / softness) ** 2
            shares = part.bones(points) if callable(part.bones) else {b: s for b, s in part.bones}
            for bone, share in shares.items():
                weights[:, index[bone]] += near * share
        if keep < weights.shape[1]:
            cutoff = np.partition(weights, -keep, axis=1)[:, -keep][:, None]
            weights = np.where(weights >= cutoff, weights, 0.0)
        weights[weights < 0.02 * weights.max(axis=1, keepdims=True)] = 0.0
        return weights / np.maximum(weights.sum(axis=1, keepdims=True), 1e-12)

    def mesh(self, lo, hi, cell, triangles):
        """Triangles of the surface inside the box lo..hi, thinned to about `triangles`."""
        from skimage import measure
        import fast_simplification

        lo, hi = _v(lo), _v(hi)
        counts = np.ceil((hi - lo) / cell).astype(int) + 1
        axes = [lo[k] + cell * np.arange(counts[k]) for k in range(3)]
        volume = np.empty(counts, dtype=np.float32)
        yy, zz = np.meshgrid(axes[1], axes[2], indexing="ij")
        for i, x in enumerate(axes[0]):          # a slab at a time keeps the memory flat
            slab = np.stack([np.full(yy.size, x), yy.ravel(), zz.ravel()], axis=1)
            volume[i] = self.field(slab).reshape(yy.shape)
        verts, faces, _, _ = measure.marching_cubes(volume, level=0.0, spacing=(cell, cell, cell))
        verts = verts + lo
        # Make every face point out of the solid (the field grows outwards).
        centres = verts[faces].mean(axis=1)[::max(len(faces) // 400, 1)]
        facing = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]], verts[faces[:, 2]] - verts[faces[:, 0]])
        if float((facing[::max(len(faces) // 400, 1)] * self.normals(centres)).sum()) < 0.0:
            faces = faces[:, ::-1]
        if triangles and len(faces) > triangles:
            verts, faces = fast_simplification.simplify(
                verts.astype(np.float32), faces.astype(np.int32), target_count=int(triangles), agg=5)
        verts = np.asarray(verts, dtype=float)
        faces = np.asarray(faces, dtype=int)
        # Pull the thinned points back onto the true surface (thinning drifts them a little).
        for _ in range(2):
            verts = verts - self.normals(verts) * self.field(verts)[:, None]
        return verts, faces


def gem(radius, centre=(0, 0, 0), squash=0.7):
    """A faceted stone: a low icosphere with flat faces, so it glints instead of shading smoothly."""
    t = (1.0 + 5 ** 0.5) / 2.0
    v = np.array([(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t),
                  (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)], dtype=float)
    v /= np.linalg.norm(v, axis=1, keepdims=True)
    f = np.array([(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2),
                  (10, 7, 6), (7, 1, 8), (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11),
                  (6, 2, 10), (8, 6, 7), (9, 8, 1)])
    verts = (v[f].reshape(-1, 3) * np.array([radius, radius, radius * squash])) + _v(centre)
    faces = np.arange(len(verts)).reshape(-1, 3)
    return verts, faces


def uv_sphere(radii, centre=(0, 0, 0), around=18, up=12):
    """A plain ball, for eyes."""
    radii, centre = _v(radii), _v(centre)
    verts, faces = [], []
    for j in range(up + 1):
        lat = -np.pi / 2 + np.pi * j / up
        for i in range(around + 1):
            lon = -np.pi + 2 * np.pi * i / around
            verts.append((np.cos(lat) * np.sin(lon), np.cos(lat) * np.cos(lon), np.sin(lat)))
    row = around + 1
    for j in range(up):
        for i in range(around):
            a, b, c, d = j * row + i, j * row + i + 1, (j + 1) * row + i, (j + 1) * row + i + 1
            if j > 0:
                faces.append((a, c, b))
            if j < up - 1:
                faces.append((b, c, d))
    return np.array(verts) * radii + centre, np.array(faces)
