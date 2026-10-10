"""Unwraps meshes onto one texture and paints it from the model itself.

Each mesh brings a function that gives a colour for any point on its surface (texture.py holds
them). The meshes are unwrapped together (xatlas), every texel is traced back to the point and
facing of the surface it covers, the functions are asked for those points, and the gaps between
the islands are filled from their edges so the seams do not show when the texture is filtered.
"""
from __future__ import annotations

import numpy as np
import xatlas
from scipy import ndimage


class Piece:
    """One mesh to unwrap: positions, triangles, per-vertex normals, and its paint function.

    paint(points, normals) -> (n, 3) or (n, 4) colours in 0..1 (the fourth is the alpha stored
    in the texture, which the gauntlet uses for how much it mirrors its surroundings)."""

    def __init__(self, name, verts, faces, normals, paint):
        self.name = name
        self.verts = np.asarray(verts, dtype=np.float32)
        self.faces = np.asarray(faces, dtype=np.uint32)
        self.normals = np.asarray(normals, dtype=np.float32)
        self.paint = paint
        # filled by unwrap():
        self.origin = None      # for each new vertex, the index of the old one it came from
        self.uvs = None         # (n, 2), v up
        self.out_faces = None


def unwrap(pieces, size, padding=6, weights=None):
    """Lays every piece out in one square. weights[name] scales how much room a piece gets
    (the face deserves more texels than the soles of the feet)."""
    atlas = xatlas.Atlas()
    for piece in pieces:
        scale = (weights or {}).get(piece.name, 1.0)
        # xatlas gives room by surface area; scaling a copy of the piece changes its share.
        atlas.add_mesh(piece.verts * scale, piece.faces, piece.normals)
    chart = xatlas.ChartOptions()
    chart.max_iterations = 2
    chart.normal_deviation_weight = 1.2
    chart.max_cost = 3.5
    pack = xatlas.PackOptions()
    pack.resolution = size
    pack.padding = padding
    pack.bilinear = True
    pack.rotate_charts = True
    atlas.generate(chart, pack)
    for index, piece in enumerate(pieces):
        origin, faces, uvs = atlas[index]
        piece.origin = np.asarray(origin, dtype=int)
        piece.out_faces = np.asarray(faces, dtype=int)
        piece.uvs = np.asarray(uvs, dtype=float)
        piece.uvs[:, 1] = 1.0 - piece.uvs[:, 1]          # the model file wants v up
    return atlas.width, atlas.height


def paint(pieces, size, channels=3, background=(0.5, 0.5, 0.5)):
    """Returns the texture as a (size, size, channels) array of 0..255, top row first."""
    image = np.zeros((size, size, channels), dtype=np.float32)
    filled = np.zeros((size, size), dtype=bool)

    for piece in pieces:
        verts = piece.verts[piece.origin].astype(float)
        normals = piece.normals[piece.origin].astype(float)
        # Pixel coordinates, y down.
        px = piece.uvs[:, 0] * size
        py = (1.0 - piece.uvs[:, 1]) * size
        ys, xs, pos, nrm = [], [], [], []
        for a, b, c in piece.out_faces:
            x0, x1, x2 = px[a], px[b], px[c]
            y0, y1, y2 = py[a], py[b], py[c]
            area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)
            if abs(area) < 1e-9:
                continue
            left, right = int(np.floor(min(x0, x1, x2))) - 1, int(np.ceil(max(x0, x1, x2))) + 1
            top, bottom = int(np.floor(min(y0, y1, y2))) - 1, int(np.ceil(max(y0, y1, y2))) + 1
            left, top = max(left, 0), max(top, 0)
            right, bottom = min(right, size - 1), min(bottom, size - 1)
            if left > right or top > bottom:
                continue
            gx, gy = np.meshgrid(np.arange(left, right + 1) + 0.5, np.arange(top, bottom + 1) + 0.5)
            w0 = ((x1 - gx) * (y2 - gy) - (x2 - gx) * (y1 - gy)) / area
            w1 = ((x2 - gx) * (y0 - gy) - (x0 - gx) * (y2 - gy)) / area
            w2 = 1.0 - w0 - w1
            # A texel whose centre is just outside still belongs to this triangle's island.
            inside = (w0 >= -0.6 / max(abs(area) ** 0.5, 1)) & (w1 >= -0.6 / max(abs(area) ** 0.5, 1)) \
                & (w2 >= -0.6 / max(abs(area) ** 0.5, 1))
            if not inside.any():
                continue
            w0, w1, w2 = w0[inside], w1[inside], w2[inside]
            ys.append(gy[inside].astype(int))
            xs.append(gx[inside].astype(int))
            pos.append(np.outer(w0, verts[a]) + np.outer(w1, verts[b]) + np.outer(w2, verts[c]))
            nrm.append(np.outer(w0, normals[a]) + np.outer(w1, normals[b]) + np.outer(w2, normals[c]))
        if not ys:
            continue
        ys, xs = np.concatenate(ys), np.concatenate(xs)
        pos, nrm = np.concatenate(pos), np.concatenate(nrm)
        nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        colours = np.asarray(piece.paint(pos, nrm), dtype=np.float32)
        if colours.shape[1] < channels:
            colours = np.concatenate([colours, np.ones((len(colours), channels - colours.shape[1]), np.float32)], axis=1)
        image[ys, xs] = colours[:, :channels]
        filled[ys, xs] = True

    # Fill the gaps from the nearest painted texel.
    if filled.any():
        nearest = ndimage.distance_transform_edt(~filled, return_distances=False, return_indices=True)
        image = image[nearest[0], nearest[1]]
    else:
        image[:] = background + (1.0,) * (channels - 3)
    return np.clip(image * 255.0 + 0.5, 0, 255).astype(np.uint8)


def tga_bytes(pixels):
    """Uncompressed TGA (24 or 32 bit), bottom row first, as the game reads it."""
    import struct
    height, width, channels = pixels.shape
    order = [2, 1, 0] + ([3] if channels == 4 else [])
    header = struct.pack("<BBBHHBHHHHBB", 0, 0, 2, 0, 0, 0, 0, 0, width, height, channels * 8, 8 if channels == 4 else 0)
    return header + pixels[::-1, :, order].tobytes()
