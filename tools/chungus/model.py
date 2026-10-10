"""Puts the boss together: the skinned body, the rigid pieces, the skeleton and the two textures.

sculpt.py says what shape he is and which bones carry it, texture.py what colour every point
is, bake.py lays the surfaces out on the textures, and kmdl.py writes the file. This module
turns those into the node tree the game loads:

    k_coop_chungus
      skin_head, skin_torso, skin_armr, skin_armg, skin_legs   skinned meshes (16 bones each at most)
      cutscenedummy
        rootdummy ... the bones, each an empty node at its joint
          ... eyes, lids, teeth, the gauntlet's plates and the stones ride on single bones
"""
from __future__ import annotations

import numpy as np

import bake
import kmdl
import sculpt
import sdf
import texture

NAME = "k_coop_chungus"
TEXTURE = "k_coop_chungus"
GAUNTLET_TEXTURE = "k_coop_chungusg"

# The gauntlet's texture mirrors the room the way the game's droids and armour do: the engine
# reads this beside the texture and blends its stock metal reflection in where the alpha is low.
GAUNTLET_TXI = "envmaptexture CM_Baremetal\r\n"

SCALE = 1.0

# A little light of his own, so a dark room (the arena is one) does not turn grey fur into a
# silhouette.
BODY_GLOW = (0.20, 0.20, 0.21)

# Which skinned mesh a triangle goes into, by the bone that carries most of it.
GROUP_OF = {
    "head": "head", "jaw": "head", "neck": "head",
    "earl1": "head", "earl2": "head", "earl3": "head", "earr1": "head", "earr2": "head", "earr3": "head",
    "rootdummy": "torso", "belly": "torso", "chest": "torso", "tail": "torso",
    "uparmr": "armr", "forearmr": "armr", "handr": "armr",
    "uparmg": "armg", "forearmg": "armg", "handg": "armg",
    "thighl": "legs", "footl": "legs", "thighr": "legs", "footr": "legs",
}

# How much more texture a piece gets than its size alone would give it.
TEXEL_WEIGHT = {"skin_head": 1.7, "eyel": 3.0, "eyer": 3.0, "lidl": 2.0, "lidr": 2.0, "teeth": 2.5, "stones": 2.0}


def _mesh(piece, texture_name, weights=None, offset=(0.0, 0.0, 0.0), selfillum=(0.0, 0.0, 0.0), shadow=False):
    verts = (piece.verts[piece.origin].astype(float) - np.asarray(offset)) * SCALE
    return kmdl.Mesh(
        verts=[tuple(float(c) for c in v) for v in verts],
        normals=[tuple(float(c) for c in n) for n in piece.normals[piece.origin]],
        uvs=[tuple(float(c) for c in uv) for uv in piece.uvs],
        faces=[tuple(int(k) for k in f) for f in piece.out_faces],
        texture=texture_name,
        selfillum=selfillum,
        shadow=shadow,
        weights=[weights[k] for k in piece.origin] if weights is not None else None,
    )


def _flat_normals(verts, faces):
    """Normals for a faceted piece (its triangles do not share vertices)."""
    normals = np.zeros_like(verts)
    for a, b, c in faces:
        n = np.cross(verts[b] - verts[a], verts[c] - verts[a])
        n /= max(np.linalg.norm(n), 1e-12)
        normals[[a, b, c]] = n
    return normals


def _radial_normals(verts, centre):
    n = verts - np.asarray(centre)
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)


def _vertex_normals(verts, faces):
    normals = np.zeros_like(verts)
    face_normals = np.cross(verts[faces[:, 1]] - verts[faces[:, 0]], verts[faces[:, 2]] - verts[faces[:, 0]])
    for k in range(3):
        np.add.at(normals, faces[:, k], face_normals)
    return normals / np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-12)


class Built:
    """Everything build.py writes and preview.py draws."""

    def __init__(self):
        self.root = None
        self.textures = {}        # texture name -> (H, W, 3 or 4) uint8 array
        self.joints = {}          # node name -> rest position in the model's space
        self.txi = {}             # texture name -> text


def build(body_texture=1024, gauntlet_texture=512, log=print):
    built = Built()
    joints = dict(sculpt.JOINTS)
    joints.update(sculpt.gauntlet_joints())
    bones = list(joints)

    # ---- the skin
    log("sculpting the body ...")
    figure, verts, faces = sculpt.body()
    normals = figure.normals(verts)
    share = figure.bone_weights(verts, bones)
    per_vertex = [[(bones[b], float(share[v, b])) for b in np.nonzero(share[v])[0]] for v in range(len(verts))]

    carried = share[faces].sum(axis=1)                       # (faces, bones)
    group_names = sorted(set(GROUP_OF.values()))
    score = np.stack([carried[:, [i for i, b in enumerate(bones) if GROUP_OF.get(b) == g]].sum(axis=1)
                      for g in group_names], axis=1)
    face_group = score.argmax(axis=1)

    body_pieces, skin_weights = [], {}
    for index, group in enumerate(group_names):
        chosen = faces[face_group == index]
        used = np.unique(chosen)
        remap = -np.ones(len(verts), dtype=int)
        remap[used] = np.arange(len(used))
        piece = bake.Piece("skin_" + group, verts[used], remap[chosen], normals[used], texture.skin(figure))
        body_pieces.append(piece)
        skin_weights[piece.name] = [per_vertex[v] for v in used]
        count = len({bone for v in used for bone, _ in per_vertex[v]})
        log(f"  {piece.name}: {len(chosen)} triangles, {count} bones")

    # ---- rigid face pieces
    rigid = {}                                               # node name -> (piece, parent bone, pivot)
    for tag, centre, side in (("l", sculpt.EYE_L, -1), ("r", sculpt.EYE_R, 1)):
        ev, ef = sdf.uv_sphere((sculpt.EYE_RADIUS,) * 3, centre)
        rigid["eye" + tag] = (bake.Piece("eye" + tag, ev, ef, _radial_normals(ev, centre), texture.eye(centre, side)),
                              "head", centre)
        lv, lf = sculpt.eyelid(centre)
        close = np.radians(-sculpt.LID_REST_DEGREES)
        turn = np.array([[1, 0, 0], [0, np.cos(close), -np.sin(close)], [0, np.sin(close), np.cos(close)]])
        lv = (lv - centre) @ turn.T + centre                 # at rest the lids hang half shut
        rigid["lid" + tag] = (bake.Piece("lid" + tag, lv, lf, _radial_normals(lv, centre), texture.lid(centre)),
                              "head", centre)
    tv, tf = sculpt.teeth()
    rigid["teeth"] = (bake.Piece("teeth", tv, tf, _vertex_normals(tv, tf), texture.teeth), "head", sculpt.J("head"))
    mv, mf = sculpt.mouth_inside()
    rigid["mouthin"] = (bake.Piece("mouthin", mv, mf, -_radial_normals(mv, (0.0, 0.50, 2.02)), texture.mouth_inside),
                        "head", sculpt.J("head"))

    log("unwrapping and painting the body texture ...")
    all_body = body_pieces + [entry[0] for entry in rigid.values()]
    bake.unwrap(all_body, body_texture, weights=TEXEL_WEIGHT)
    built.textures[TEXTURE] = bake.paint(all_body, body_texture, channels=3)

    # ---- the gauntlet
    log("forging the gauntlet ...")
    metal = {}
    elbow, wrist = sculpt.J("forearmg"), sculpt.J("handg")
    for bone, (gv, gf, gsculpt) in sculpt.gauntlet_pieces().items():
        ring = (elbow, wrist) if bone == "forearmg" else (None, None)
        metal[bone] = bake.Piece("g" + bone, gv, gf, gsculpt.normals(gv), texture.metal(gsculpt, *ring))
    along, across, back = sculpt.hand_frame()
    stone_verts, stone_faces, stone_normals, stone_paint = [], [], [], []
    base = 0
    for name, (gv, gf) in sculpt.stones().items():
        stone_verts.append(gv)
        stone_faces.append(gf + base)
        stone_normals.append(_flat_normals(gv, gf))
        stone_paint.append((gv.mean(axis=0), texture.stone(name, gv.mean(axis=0), back)))
        base += len(gv)
    stone_verts = np.concatenate(stone_verts)
    centres = np.array([centre for centre, _ in stone_paint])

    def paint_stones(points, normals_):
        # each point belongs to the stone it is nearest
        which = np.linalg.norm(points[:, None, :] - centres[None, :, :], axis=2).argmin(axis=1)
        out = np.zeros((len(points), 4))
        for k, (_, fn) in enumerate(stone_paint):
            mask = which == k
            if mask.any():
                out[mask] = fn(points[mask], normals_[mask])
        return out

    stones = bake.Piece("stones", stone_verts, np.concatenate(stone_faces), np.concatenate(stone_normals), paint_stones)
    all_metal = list(metal.values()) + [stones]
    bake.unwrap(all_metal, gauntlet_texture, padding=4, weights=TEXEL_WEIGHT)
    built.textures[GAUNTLET_TEXTURE] = bake.paint(all_metal, gauntlet_texture, channels=4)
    built.txi[GAUNTLET_TEXTURE] = GAUNTLET_TXI

    # ---- the node tree
    log("building the skeleton ...")
    Node = kmdl.Node
    root = Node(NAME)
    for piece in body_pieces:
        root.add(Node(piece.name, mesh=_mesh(piece, TEXTURE, skin_weights[piece.name], selfillum=BODY_GLOW)))
    cutscene = root.add(Node("cutscenedummy"))
    nodes = {}
    world = {name: np.array(position, dtype=float) for name, (_, position) in joints.items()}
    for name, (parent, _) in joints.items():
        above = nodes[parent] if parent else cutscene
        origin = world[parent] if parent else np.zeros(3)
        nodes[name] = above.add(Node(name, position=tuple(float(c) * SCALE for c in world[name] - origin)))

    def hang(name, parent, pivot, mesh=None):
        pivot = np.asarray(pivot, dtype=float)
        world[name] = pivot
        nodes[name] = nodes[parent].add(
            Node(name, position=tuple(float(c) * SCALE for c in pivot - world[parent]), mesh=mesh))
        return nodes[name]

    for name, (piece, parent, pivot) in rigid.items():
        hang(name, parent, pivot, _mesh(piece, TEXTURE, offset=pivot, selfillum=BODY_GLOW))
    for bone, piece in metal.items():
        hang("g_" + bone, bone, world[bone], _mesh(piece, GAUNTLET_TEXTURE, offset=world[bone], shadow=True))
    hang("stones", "handg", world["handg"],
         _mesh(stones, GAUNTLET_TEXTURE, offset=world["handg"], selfillum=(0.35, 0.35, 0.35)))

    # Points the engine looks for by name: where effects strike, where the hands and head are.
    hang("impact", "rootdummy", (0.0, 1.0, 1.25))
    hang("headhook", "head", world["head"] + (0.0, 0.0, 0.55))
    hang("talkdummy", "head", world["head"] + (0.0, 0.45, -0.15))
    hang("headconjure", "head", world["head"] + (0.0, 0.75, 0.05))
    hang("lhand", "handg", world["handg"] + along * 0.25)
    hang("handconjure", "handg", world["handg"] + along * 0.25 + back * 0.45)
    hang("rhand", "handr", world["handr"] + (0.0, 0.10, -0.13))
    world["camerahook"] = np.array((0.0, 0.0, 2.4))
    cutscene.add(Node("camerahook", position=tuple(float(c) * SCALE for c in world["camerahook"])))

    built.root = root
    built.joints = {name: tuple(float(c) * SCALE for c in position) for name, position in world.items()}
    return built
