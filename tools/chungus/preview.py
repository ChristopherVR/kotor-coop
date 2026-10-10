"""A small software renderer, so the model and its animations can be looked at without the game.

It reads the same node tree and keyframes the model file is written from and poses them the way
the engine does: a position key is added to a node's rest position, an orientation key replaces
its rest orientation, a rigid mesh rides on its node, and a skinned vertex is carried by its
bones in proportion to its weights. Then it draws textured, lit triangles into an image.
"""
from __future__ import annotations

import math

import numpy as np
from PIL import Image

import geom
import kmdl


def _track_value(rows, t, slerp):
    if t <= rows[0][0]:
        return rows[0][1:]
    if t >= rows[-1][0]:
        return rows[-1][1:]
    for a, b in zip(rows, rows[1:]):
        if a[0] <= t <= b[0]:
            f = 0.0 if b[0] == a[0] else (t - a[0]) / (b[0] - a[0])
            if slerp:
                return geom.q_slerp(a[1:], b[1:], f)
            return tuple(x + (y - x) * f for x, y in zip(a[1:], b[1:]))
    return rows[-1][1:]


def _transforms(root, animation, t):
    """{node name: (rotation matrix, position, rest position, scale, glow)} in the model's space."""
    out = {}

    def walk(node, matrix, offset, rest_offset):
        tracks = animation.tracks.get(node.name, {}) if animation else {}
        position = node.position
        if tracks.get("position"):
            position = tuple(a + b for a, b in zip(node.position, _track_value(tracks["position"], t, False)))
        orientation = _track_value(tracks["orientation"], t, True) if tracks.get("orientation") else node.orientation
        scale = _track_value(tracks["scale"], t, False)[0] if tracks.get("scale") else 1.0
        glow = _track_value(tracks["selfillum"], t, False) if tracks.get("selfillum") else (
            node.mesh.selfillum if node.mesh else (0.0, 0.0, 0.0))
        offset = offset + matrix @ np.asarray(position, dtype=float)
        matrix = matrix @ geom.q_matrix(orientation)
        rest_offset = rest_offset + np.asarray(node.position, dtype=float)
        out[node.name] = (matrix, offset, rest_offset, scale, glow)
        for child in node.children:
            walk(child, matrix, offset, rest_offset)

    walk(root, np.eye(3), np.zeros(3), np.zeros(3))
    return out


def posed_triangles(root: kmdl.Node, animation: kmdl.Animation | None = None, t: float = 0.0):
    """World-space triangles of the whole model, grouped by texture:
    {texture name: (positions (n,3,3), normals (n,3,3), uvs (n,3,2), glow (n,3))}."""
    frames = _transforms(root, animation, t)
    groups = {}
    nodes, _ = kmdl.flatten(root)
    for node in nodes:
        mesh = node.mesh
        if not mesh or not mesh.render:
            continue
        matrix, offset, _, scale, glow = frames[node.name]
        verts = np.asarray(mesh.verts, dtype=float)
        normals = np.asarray(mesh.normals, dtype=float)
        if mesh.weights is not None:
            v = np.zeros_like(verts)
            n = np.zeros_like(normals)
            # Gather the vertices each bone carries, and move them with it.
            by_bone = {}
            for index, entry in enumerate(mesh.weights):
                for bone, share in entry:
                    by_bone.setdefault(bone, ([], []))
                    by_bone[bone][0].append(index)
                    by_bone[bone][1].append(share)
            for bone, (indices, shares) in by_bone.items():
                bone_matrix, bone_offset, bone_rest, _, _ = frames[bone]
                indices = np.asarray(indices)
                shares = np.asarray(shares)[:, None]
                v[indices] += shares * ((verts[indices] - bone_rest) @ bone_matrix.T + bone_offset)
                n[indices] += shares * (normals[indices] @ bone_matrix.T)
        else:
            v = (verts * scale) @ matrix.T + offset
            n = normals @ matrix.T
        f = np.asarray(mesh.faces)
        entry = groups.setdefault(mesh.texture, ([], [], [], []))
        entry[0].append(v[f])
        entry[1].append(n[f])
        entry[2].append(np.asarray(mesh.uvs)[f])
        entry[3].append(np.tile(np.asarray(glow, dtype=float), (len(f), 1)))
    return {name: tuple(np.concatenate(part) for part in entry) for name, entry in groups.items()}


def render(root, textures, animation=None, t=0.0, size=(480, 480), yaw=25.0, pitch=8.0,
           centre=(0.0, 0.0, 1.75), span=4.4, background=(46, 50, 62), ambient=0.38):
    """Orthographic view. yaw 0 looks at the model's front; positive yaw walks the camera to its left side.

    textures: {name: PIL image or array}; a mesh whose texture is missing is drawn mid grey."""
    width, height = size
    y, p = math.radians(yaw), math.radians(pitch)
    toward = np.array([math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), math.sin(p)])
    right = np.cross(toward, [0.0, 0.0, 1.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, toward)
    right = -right              # seen from the front, his right hand is on the picture's left

    light = np.array([0.35, 0.55, 0.75])
    light /= np.linalg.norm(light)
    light = light[0] * -right + light[1] * toward + light[2] * up
    fill = -right * 0.6 + toward * 0.4 + up * 0.2
    fill /= np.linalg.norm(fill)

    colour = np.empty((height, width, 3))
    colour[:] = background
    zbuf = np.full((height, width), -1e9)

    for name, (tris, norms, uvs, glows) in posed_triangles(root, animation, t).items():
        source = textures.get(name) if isinstance(textures, dict) else textures
        if source is None:
            tex = np.full((2, 2, 3), 128.0)
        else:
            tex = np.asarray(source, dtype=float)[:, :, :3]
        th, tw = tex.shape[:2]
        rel = tris - np.asarray(centre, dtype=float)
        sx = (rel @ right) / span * width + width / 2
        sy = height / 2 - (rel @ up) / span * width
        depth = rel @ toward
        for k in range(len(tris)):
            x0, x1, x2 = sx[k]
            y0, y1, y2 = sy[k]
            area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if area >= -1e-9:
                continue
            left, rightpx = max(int(math.floor(min(x0, x1, x2))), 0), min(int(math.ceil(max(x0, x1, x2))), width - 1)
            top, bottom = max(int(math.floor(min(y0, y1, y2))), 0), min(int(math.ceil(max(y0, y1, y2))), height - 1)
            if left > rightpx or top > bottom:
                continue
            px, py = np.meshgrid(np.arange(left, rightpx + 1) + 0.5, np.arange(top, bottom + 1) + 0.5)
            w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) / area
            w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) / area
            w2 = 1.0 - w0 - w1
            inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
            if not inside.any():
                continue
            z = w0 * depth[k, 0] + w1 * depth[k, 1] + w2 * depth[k, 2]
            region = zbuf[top:bottom + 1, left:rightpx + 1]
            visible = inside & (z > region)
            if not visible.any():
                continue
            u = w0 * uvs[k, 0, 0] + w1 * uvs[k, 1, 0] + w2 * uvs[k, 2, 0]
            v = w0 * uvs[k, 0, 1] + w1 * uvs[k, 1, 1] + w2 * uvs[k, 2, 1]
            tx = np.clip((u * tw).astype(int), 0, tw - 1)
            ty = np.clip(((1.0 - v) * th).astype(int), 0, th - 1)
            normal = (w0[..., None] * norms[k, 0] + w1[..., None] * norms[k, 1] + w2[..., None] * norms[k, 2])
            normal /= np.maximum(np.linalg.norm(normal, axis=-1, keepdims=True), 1e-9)
            shade = ambient + (1.0 - ambient) * (0.8 * np.clip(normal @ light, 0.0, 1.0) + 0.2 * np.clip(normal @ fill, 0.0, 1.0))
            pixel = tex[ty, tx] * shade[..., None] + tex[ty, tx] * glows[k] * 1.2
            region[visible] = z[visible]
            colour[top:bottom + 1, left:rightpx + 1][visible] = np.clip(pixel, 0, 255)[visible]

    return Image.fromarray(colour.astype(np.uint8), "RGB")


def sheet(images, columns, labels=None):
    """Tiles images into one picture (labels are drawn in the top-left corner of each)."""
    from PIL import ImageDraw
    w, h = images[0].size
    rows = (len(images) + columns - 1) // columns
    out = Image.new("RGB", (columns * w, rows * h), (20, 20, 24))
    for index, image in enumerate(images):
        cell = image.copy()
        if labels:
            ImageDraw.Draw(cell).text((6, 4), labels[index], fill=(255, 255, 255))
        out.paste(cell, ((index % columns) * w, (index // columns) * h))
    return out
