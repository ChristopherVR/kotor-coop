"""Writer for the KOTOR 1 (PC) binary model format: a .mdl with its .mdx vertex file.

Covers what a creature needs and nothing else: dummy nodes, textured triangle meshes, skinned
meshes (each vertex carried by up to four bones, at most sixteen bones a mesh, which is the
engine's limit and why the game's own creatures are cut into several skins), and animations
made of position / orientation / scale / self-illumination keyframes. No emitters, lights,
walkmeshes or lightsabers.

Layout facts were checked against the game's own creature models (c_gizka, c_rancor,
c_gammorean) with tools/chungus/mdldump.py; see docs/research/chungus-boss.md.
"""
from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field

# Engine vtable addresses the K1 PC loader expects in the file (it overwrites them on load,
# but it also uses the first to tell K1 from K2).
MODEL_FN = (4273776, 4216096)
ANIM_FN = (4273392, 4451552)
MESH_FN = (4216656, 4216672)
SKIN_FN = (4216592, 4216608)
MAX_SKIN_BONES = 16

NODE_BASE = 0x0001
NODE_MESH = 0x0020
NODE_SKIN = 0x0040

CTRL_POSITION = 8
CTRL_ORIENTATION = 20
CTRL_SCALE = 36
CTRL_SELFILLUM = 100
CTRL_ALPHA = 132

CLASS_CHARACTER = 4

MDX_VERTEX = 0x01
MDX_UV1 = 0x02
MDX_NORMAL = 0x20

NO_OFFSET = 0xFFFFFFFF
NO_FACE = 0xFFFF


@dataclass
class Mesh:
    verts: list            # [(x, y, z)]
    normals: list          # [(x, y, z)]
    uvs: list              # [(u, v)]
    faces: list            # [(a, b, c)] counter-clockwise seen from outside
    texture: str
    diffuse: tuple = (1.0, 1.0, 1.0)
    ambient: tuple = (1.0, 1.0, 1.0)
    selfillum: tuple = (0.0, 0.0, 0.0)
    alpha: float = 1.0
    scale: float = 1.0
    render: bool = True
    shadow: bool = False
    # For a skin: one entry per vertex, [(bone node name, share), ...] with up to four bones
    # whose shares sum to 1. A skin's node must sit unrotated at the model's origin, and so
    # must every bone it names rest unrotated (this writer assumes it; the model keeps to it).
    weights: list | None = None


@dataclass
class Node:
    name: str
    position: tuple = (0.0, 0.0, 0.0)
    orientation: tuple = (1.0, 0.0, 0.0, 0.0)   # w, x, y, z
    mesh: Mesh | None = None
    children: list = field(default_factory=list)

    def add(self, child: "Node") -> "Node":
        self.children.append(child)
        return child


@dataclass
class Animation:
    name: str
    length: float
    transition: float = 0.25
    events: list = field(default_factory=list)          # [(time, name)]
    # node name -> {"position": [(t, dx, dy, dz)], "orientation": [(t, w, x, y, z)],
    #               "scale": [(t, s)], "selfillum": [(t, r, g, b)]}
    # A position key is an OFFSET from the node's rest position (the game adds them: in the
    # stock c_gammorean the root dummy rests at z 1.203 and its keys read z -0.19). An
    # orientation key is the node's whole local orientation.
    tracks: dict = field(default_factory=dict)


@dataclass
class Model:
    name: str
    root: Node
    animations: list = field(default_factory=list)
    classification: int = CLASS_CHARACTER
    supermodel: str = "NULL"
    anim_scale: float = 1.0


def flatten(root: Node):
    """Nodes in the order the file stores them (depth first), with parent indices."""
    nodes, parents = [], []

    def walk(node, parent):
        index = len(nodes)
        nodes.append(node)
        parents.append(parent)
        for child in node.children:
            walk(child, index)

    walk(root, None)
    return nodes, parents


def _world_positions(nodes, parents):
    """Where each node rests in the model's space (rest orientations are all 'no rotation')."""
    world = []
    for node, parent in zip(nodes, parents):
        base = world[parent] if parent is not None else (0.0, 0.0, 0.0)
        if node.orientation != (1.0, 0.0, 0.0, 0.0) and node.mesh is None and node.children:
            raise ValueError(f"node '{node.name}' rests rotated; skins assume unrotated bones")
        world.append(tuple(b + p for b, p in zip(base, node.position)))
    return world


def _fixed(text: str, size: int) -> bytes:
    raw = text.encode("ascii")
    if len(raw) >= size:
        raise ValueError(f"'{text}' does not fit in {size} bytes")
    return raw.ljust(size, b"\0")


def _array(offset: int, count: int) -> bytes:
    return struct.pack("<III", offset, count, count)


def _inverted_counter(count: int) -> int:
    quo, mod = divmod(count, 100)
    return int(2 ** quo * 100 - count + (100 * quo if mod else 0) + (0 if quo else -1))


def _face_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length < 1e-12:
        return (0.0, 0.0, 1.0), 0.0
    return (nx / length, ny / length, nz / length), length / 2.0


def _adjacency(mesh: Mesh):
    """For each face, the face across each of its three edges (by position, so a UV seam still joins)."""
    weld = {}
    ids = []
    for v in mesh.verts:
        key = (round(v[0], 5), round(v[1], 5), round(v[2], 5))
        ids.append(weld.setdefault(key, len(weld)))
    edges = {}
    for index, face in enumerate(mesh.faces):
        for k in range(3):
            a, b = ids[face[k]], ids[face[(k + 1) % 3]]
            edges.setdefault((min(a, b), max(a, b)), []).append(index)
    result = []
    for index, face in enumerate(mesh.faces):
        row = []
        for k in range(3):
            a, b = ids[face[k]], ids[face[(k + 1) % 3]]
            others = [f for f in edges[(min(a, b), max(a, b))] if f != index]
            row.append(others[0] if others else NO_FACE)
        result.append(row)
    return result


class _Controllers:
    """One node's controller keys and the float block they index into."""

    def __init__(self):
        self.keys = []
        self.data = []

    def add(self, ctrl_type, rows, columns, animated):
        # rows: [(time, v0, v1, ...)]
        start = len(self.data)
        unknown = ctrl_type + 8 if animated and ctrl_type in (CTRL_POSITION, CTRL_ORIENTATION) else 0xFFFF
        self.keys.append(struct.pack("<IHHHHB3x", ctrl_type, unknown, len(rows), start, start + len(rows), columns))
        self.data.extend(row[0] for row in rows)
        for row in rows:
            if len(row) != columns + 1:
                raise ValueError(f"controller {ctrl_type}: a row has {len(row) - 1} values, expected {columns}")
            self.data.extend(row[1:])

    def key_bytes(self):
        return b"".join(self.keys)

    def data_bytes(self):
        return struct.pack(f"<{len(self.data)}f", *self.data)


def _geometry_controllers(node: Node, is_root: bool) -> _Controllers:
    c = _Controllers()
    if is_root:
        return c
    w, x, y, z = node.orientation
    c.add(CTRL_POSITION, [(0.0, *node.position)], 3, False)
    c.add(CTRL_ORIENTATION, [(0.0, x, y, z, w)], 4, False)
    if node.mesh and node.mesh.weights is None:
        c.add(CTRL_ALPHA, [(0.0, node.mesh.alpha)], 1, False)
        c.add(CTRL_SCALE, [(0.0, node.mesh.scale)], 1, False)
        c.add(CTRL_SELFILLUM, [(0.0, *node.mesh.selfillum)], 3, False)
    elif node.mesh and any(node.mesh.selfillum):
        # A skin takes this one and no other (see the note on skins in chungus-boss.md).
        c.add(CTRL_SELFILLUM, [(0.0, *node.mesh.selfillum)], 3, False)
    return c


def _animation_controllers(node: Node, tracks: dict) -> _Controllers:
    c = _Controllers()
    if not tracks:
        return c
    if tracks.get("position"):
        c.add(CTRL_POSITION, tracks["position"], 3, True)
    if tracks.get("orientation"):
        rows = []
        previous = None
        for t, w, x, y, z in tracks["orientation"]:
            length = math.sqrt(w * w + x * x + y * y + z * z)
            q = (x / length, y / length, z / length, w / length)
            # Keep neighbouring keys on the same side so the engine turns the short way.
            if previous and sum(a * b for a, b in zip(previous, q)) < 0.0:
                q = tuple(-v for v in q)
            previous = q
            rows.append((t, *q))
        c.add(CTRL_ORIENTATION, rows, 4, True)
    if node.mesh:
        if tracks.get("scale"):
            c.add(CTRL_SCALE, tracks["scale"], 1, True)
        if tracks.get("selfillum"):
            c.add(CTRL_SELFILLUM, tracks["selfillum"], 3, True)
    elif tracks.get("scale") or tracks.get("selfillum"):
        raise ValueError(f"node '{node.name}' has mesh keyframes but no mesh")
    return c


def write(model: Model) -> tuple[bytes, bytes]:
    """Returns (mdl bytes, mdx bytes)."""
    nodes, parents = flatten(model.root)
    names = [n.name for n in nodes]
    if len(set(name.lower() for name in names)) != len(names):
        raise ValueError("node names must be unique")
    index_of = {n.name: i for i, n in enumerate(nodes)}
    children = [[index_of[c.name] for c in n.children] for n in nodes]

    for anim in model.animations:
        for name in anim.tracks:
            if name not in index_of:
                raise ValueError(f"animation '{anim.name}' moves unknown node '{name}'")

    # ---- pass 1: sizes and offsets (everything is relative to the end of the 12-byte file header)
    pos = 80 + 116
    off_name_offsets = pos
    pos += 4 * len(nodes)
    name_offsets = []
    for name in names:
        name_offsets.append(pos)
        pos += len(name) + 1

    off_anim_offsets = pos
    pos += 4 * len(model.animations)
    anim_layout = []
    for anim in model.animations:
        entry = {"offset": pos}
        pos += 136
        entry["events"] = pos
        pos += 36 * len(anim.events)
        entry["nodes"] = []
        for i, node in enumerate(nodes):
            ctrl = _animation_controllers(node, anim.tracks.get(node.name, {}))
            item = {"offset": pos, "ctrl": ctrl}
            pos += 80
            item["children"] = pos
            pos += 4 * len(children[i])
            item["keys"] = pos
            pos += 16 * len(ctrl.keys)
            item["data"] = pos
            pos += 4 * len(ctrl.data)
            entry["nodes"].append(item)
        anim_layout.append(entry)

    node_layout = []
    mdx_pos = 0
    for i, node in enumerate(nodes):
        item = {"offset": pos}
        pos += 80
        if node.mesh:
            m = node.mesh
            if not (len(m.verts) == len(m.normals) == len(m.uvs)):
                raise ValueError(f"mesh '{node.name}': vertex, normal and uv counts differ")
            if len(m.verts) > 65000:
                raise ValueError(f"mesh '{node.name}' has too many vertices")
            pos += 332
            if m.weights is not None:
                pos += 100
            item["faces"] = pos
            pos += 32 * len(m.faces)
            item["index_offset"] = pos
            pos += 4
            item["verts"] = pos
            pos += 12 * len(m.verts)
            item["index_count"] = pos
            pos += 4
            item["inverted"] = pos
            pos += 4
            item["indices"] = pos
            pos += 6 * len(m.faces)
            item["mdx"] = mdx_pos
            mdx_pos += (64 if m.weights is not None else 32) * (len(m.verts) + 1)
            if m.weights is not None:
                item["bonemap"] = pos
                pos += 4 * len(nodes)
                item["qbones"] = pos
                pos += 16 * len(nodes)
                item["tbones"] = pos
                pos += 12 * len(nodes)
                item["garbage"] = pos
                pos += 4 * len(nodes)
        ctrl = _geometry_controllers(node, parents[i] is None)
        item["ctrl"] = ctrl
        item["children"] = pos
        pos += 4 * len(children[i])
        item["keys"] = pos
        pos += 16 * len(ctrl.keys)
        item["data"] = pos
        pos += 4 * len(ctrl.data)
        node_layout.append(item)

    mdl_size = pos
    mdx_size = mdx_pos

    # ---- pass 2: bytes
    out = bytearray()
    mdx = bytearray()

    out += struct.pack("<II", *MODEL_FN)
    out += _fixed(model.name, 32)
    out += struct.pack("<II", node_layout[0]["offset"], len(nodes))
    out += _array(0, 0) + _array(0, 0)
    out += struct.pack("<IB3x", 0, 2)

    out += struct.pack("<BBBBI", model.classification, 0, 0, 1, 0)
    out += _array(off_anim_offsets, len(model.animations))
    out += struct.pack("<I", 0)
    out += struct.pack("<6f", -5.0, -5.0, -1.0, 5.0, 5.0, 10.0)
    out += struct.pack("<ff", 7.0, model.anim_scale)
    out += _fixed(model.supermodel, 32)
    out += struct.pack("<IIII", node_layout[0]["offset"], 0, mdx_size, 0)
    out += _array(off_name_offsets, len(nodes))
    assert len(out) == off_name_offsets

    out += struct.pack(f"<{len(nodes)}I", *name_offsets)
    for name in names:
        out += name.encode("ascii") + b"\0"

    assert len(out) == off_anim_offsets
    out += struct.pack(f"<{len(model.animations)}I", *(a["offset"] for a in anim_layout))
    for anim, layout in zip(model.animations, anim_layout):
        assert len(out) == layout["offset"]
        out += struct.pack("<II", *ANIM_FN)
        out += _fixed(anim.name, 32)
        out += struct.pack("<II", layout["nodes"][0]["offset"], len(nodes))
        out += _array(0, 0) + _array(0, 0)
        out += struct.pack("<IB3x", 0, 5)
        out += struct.pack("<ff", anim.length, anim.transition)
        out += _fixed(model.name, 32)
        out += _array(layout["events"] if anim.events else 0, len(anim.events))
        out += struct.pack("<I", 0)
        for time, event in sorted(anim.events):
            out += struct.pack("<f", time) + _fixed(event, 32)
        for i, (node, item) in enumerate(zip(nodes, layout["nodes"])):
            assert len(out) == item["offset"]
            ctrl = item["ctrl"]
            parent = layout["nodes"][parents[i]]["offset"] if parents[i] is not None else 0
            out += struct.pack("<HHHHII", NODE_BASE, i, i, 0, layout["offset"], parent)
            out += struct.pack("<3f4f", 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0)
            out += _array(item["children"], len(children[i]))
            out += _array(item["keys"] if ctrl.keys else 0, len(ctrl.keys))
            out += _array(item["data"] if ctrl.data else 0, len(ctrl.data))
            out += struct.pack(f"<{len(children[i])}I", *(layout["nodes"][c]["offset"] for c in children[i]))
            out += ctrl.key_bytes()
            out += ctrl.data_bytes()

    mesh_number = 0
    for i, (node, item) in enumerate(zip(nodes, node_layout)):
        assert len(out) == item["offset"], (node.name, len(out), item["offset"])
        ctrl = item["ctrl"]
        parent = node_layout[parents[i]]["offset"] if parents[i] is not None else 0
        flags = NODE_BASE | (NODE_MESH if node.mesh else 0)
        if node.mesh and node.mesh.weights is not None:
            flags |= NODE_SKIN
        out += struct.pack("<HHHHII", flags, i, i, 0, 0, parent)
        out += struct.pack("<3f4f", *node.position, *node.orientation)
        out += _array(item["children"], len(children[i]))
        out += _array(item["keys"] if ctrl.keys else 0, len(ctrl.keys))
        out += _array(item["data"] if ctrl.data else 0, len(ctrl.data))

        if node.mesh:
            m = node.mesh
            mesh_number += 1
            face_data = [_face_normal(m.verts[a], m.verts[b], m.verts[c]) for a, b, c in m.faces]
            used = [m.verts[k] for face in m.faces for k in face] or [(0.0, 0.0, 0.0)]
            bb_min = [min(v[k] for v in used) for k in range(3)]
            bb_max = [max(v[k] for v in used) for k in range(3)]
            average = [sum(v[k] for v in used) / len(used) for k in range(3)]
            radius = max(math.dist(v, average) for v in used)
            area = sum(a for _, a in face_data)

            skinned = m.weights is not None
            stride = 64 if skinned else 32
            out += struct.pack("<II", *(SKIN_FN if skinned else MESH_FN))
            out += _array(item["faces"], len(m.faces))
            out += struct.pack("<6f", *bb_min, *bb_max)
            out += struct.pack("<f3f", radius, *average)
            out += struct.pack("<3f3f", *m.diffuse, *m.ambient)
            out += struct.pack("<I", 0)
            out += _fixed(m.texture, 32) + _fixed("", 32) + bytes(24)
            out += _array(item["index_count"], 1)
            out += _array(item["index_offset"], 1)
            out += _array(item["inverted"], 1)
            out += struct.pack("<III", 0xFFFFFFFF, 0xFFFFFFFF, 0)
            out += bytes([3, 0, 0, 0, 0, 0, 0, 0])
            out += struct.pack("<I4f", 0, 0.0, 0.0, 0.0, 0.0)
            out += struct.pack("<II", stride, MDX_VERTEX | MDX_NORMAL | MDX_UV1)
            out += struct.pack("<11I", 0, 12, NO_OFFSET, 24, *([NO_OFFSET] * 7))
            out += struct.pack("<HH", len(m.verts), 1)
            out += struct.pack("<6B2x", 0, 0, 0, 1 if m.shadow else 0, 0, 1 if m.render else 0)
            out += struct.pack("<fIII", area, 0, item["mdx"], item["verts"])

            bones = []
            if skinned:
                # The bones this skin uses, in the order its vertices index them.
                for entry in m.weights:
                    for bone, _ in entry:
                        if bone not in bones:
                            bones.append(bone)
                if len(bones) > MAX_SKIN_BONES:
                    raise ValueError(f"skin '{node.name}' uses {len(bones)} bones; the engine allows {MAX_SKIN_BONES}")
                if node.position != (0.0, 0.0, 0.0) or parents[i] != 0:
                    raise ValueError(f"skin '{node.name}' must sit at the origin, directly under the root")
                out += _array(0, 0)
                out += struct.pack("<IIII", 32, 48, item["bonemap"], len(nodes))
                out += _array(item["qbones"], len(nodes))
                out += _array(item["tbones"], len(nodes))
                out += _array(item["garbage"], len(nodes))
                slots = [index_of[b] for b in bones] + [0xFFFF] * (MAX_SKIN_BONES - len(bones))
                out += struct.pack("<16HI", *slots, 0)

            assert len(out) == item["faces"]
            for (a, b, c), (normal, _), adjacent in zip(m.faces, face_data, _adjacency(m)):
                distance = -sum(n * v for n, v in zip(normal, m.verts[a]))
                out += struct.pack("<4fI3H3H", *normal, distance, 1, *adjacent, a, b, c)
            out += struct.pack("<I", item["indices"])
            for v in m.verts:
                out += struct.pack("<3f", *v)
            out += struct.pack("<I", 3 * len(m.faces))
            out += struct.pack("<I", _inverted_counter(mesh_number))
            for face in m.faces:
                out += struct.pack("<3H", *face)

            assert len(mdx) == item["mdx"]
            if skinned:
                for v, n, uv, entry in zip(m.verts, m.normals, m.uvs, m.weights):
                    if not 1 <= len(entry) <= 4:
                        raise ValueError(f"skin '{node.name}': a vertex needs one to four bones")
                    shares = [share for _, share in entry] + [0.0] * (4 - len(entry))
                    slots = [float(bones.index(bone)) for bone, _ in entry] + [-1.0] * (4 - len(entry))
                    mdx += struct.pack("<3f3f2f4f4f", *v, *n, *uv, *shares, *slots)
                mdx += struct.pack("<3f3f2f4f4f", 1e7, 1e7, 1e7, 0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 0, 0, 0)

                # Which bone number each node of the model is in this skin (-1: not one of its bones).
                bonemap = [-1.0] * len(nodes)
                for number, bone in enumerate(bones):
                    bonemap[index_of[bone]] = float(number)
                out += struct.pack(f"<{len(nodes)}f", *bonemap)
                # For every node, the transform from the skin's space into that node's own space at
                # rest. With everything unrotated that is no rotation and minus the node's position.
                world = _world_positions(nodes, parents)
                for _ in nodes:
                    out += struct.pack("<4f", 1.0, 0.0, 0.0, 0.0)
                for position in world:
                    out += struct.pack("<3f", -position[0], -position[1], -position[2])
                out += bytes(4 * len(nodes))
            else:
                for v, n, uv in zip(m.verts, m.normals, m.uvs):
                    mdx += struct.pack("<3f3f2f", *v, *n, *uv)
                mdx += struct.pack("<3f3f2f", 1e7, 1e7, 1e7, 0.0, 0.0, 0.0, 0.0, 0.0)

        assert len(out) == item["children"]
        out += struct.pack(f"<{len(children[i])}I", *(node_layout[c]["offset"] for c in children[i]))
        out += ctrl.key_bytes()
        out += ctrl.data_bytes()

    assert len(out) == mdl_size and len(mdx) == mdx_size
    return struct.pack("<III", 0, mdl_size, mdx_size) + bytes(out), bytes(mdx)
