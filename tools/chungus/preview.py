"""A small software renderer, so the model and its animations can be looked at without the game.

It reads the same node tree and keyframes the model file is written from, poses them the way
the engine does (each keyframe is the node's whole local position / orientation) and draws
textured, lit triangles into an image.
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


def posed_triangles(root: kmdl.Node, animation: kmdl.Animation | None = None, t: float = 0.0):
    """World-space triangles of the whole model: (positions (n,3,3), normals (n,3,3), uvs (n,3,2), glow (n,3))."""
    tris, norms, uvs, glows = [], [], [], []

    def walk(node, matrix, offset):
        tracks = animation.tracks.get(node.name, {}) if animation else {}
        position = _track_value(tracks["position"], t, False) if tracks.get("position") else node.position
        orientation = _track_value(tracks["orientation"], t, True) if tracks.get("orientation") else node.orientation
        scale = _track_value(tracks["scale"], t, False)[0] if tracks.get("scale") else 1.0
        glow = _track_value(tracks["selfillum"], t, False) if tracks.get("selfillum") else (0.0, 0.0, 0.0)
        offset = offset + matrix @ np.asarray(position, dtype=float)
        matrix = matrix @ geom.q_matrix(orientation)
        if node.mesh and node.mesh.render:
            v = (np.asarray(node.mesh.verts) * scale) @ matrix.T + offset
            n = np.asarray(node.mesh.normals) @ matrix.T
            f = np.asarray(node.mesh.faces)
            tris.append(v[f])
            norms.append(n[f])
            uvs.append(np.asarray(node.mesh.uvs)[f])
            glows.append(np.tile(np.asarray(glow, dtype=float), (len(f), 1)))
        for child in node.children:
            walk(child, matrix, offset)

    walk(root, np.eye(3), np.zeros(3))
    return np.concatenate(tris), np.concatenate(norms), np.concatenate(uvs), np.concatenate(glows)


def render(root, texture_image, animation=None, t=0.0, size=(480, 480), yaw=25.0, pitch=8.0,
           centre=(0.0, 0.0, 1.5), span=4.2, background=(58, 62, 78)):
    """Orthographic view. yaw 0 looks at the model's front; positive yaw walks the camera to its left side."""
    tris, norms, uvs, glows = posed_triangles(root, animation, t)
    width, height = size
    tex = np.asarray(texture_image.convert("RGB"), dtype=float)
    th, tw = tex.shape[:2]

    # Camera basis: 'right' and 'up' span the picture, 'toward' points from the model to the eye.
    y, p = math.radians(yaw), math.radians(pitch)
    toward = np.array([math.sin(y) * math.cos(p), math.cos(y) * math.cos(p), math.sin(p)])
    right = np.cross(toward, [0.0, 0.0, 1.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, toward)
    # Seen from the front the model's right hand must be on the picture's left.
    right = -right

    rel = tris - np.asarray(centre, dtype=float)
    sx = (rel @ right) / span * width + width / 2
    sy = height / 2 - (rel @ up) / span * width
    depth = rel @ toward

    light = np.array([0.35, 0.55, 0.75])
    light /= np.linalg.norm(light)
    light = light[0] * -right + light[1] * toward + light[2] * up

    colour = np.empty((height, width, 3))
    colour[:] = background
    zbuf = np.full((height, width), -1e9)

    for k in range(len(tris)):
        x0, x1, x2 = sx[k]
        y0, y1, y2 = sy[k]
        area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
        if area >= -1e-9:           # back-facing or edge-on in this screen winding
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
        shade = 0.42 + 0.58 * np.clip(normal @ light, 0.0, 1.0)
        pixel = tex[ty, tx] * shade[..., None] + glows[k] * 255.0
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
