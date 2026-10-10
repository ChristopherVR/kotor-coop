"""The boss's body: a fat grey-and-white rabbit holding a jewelled golden gauntlet.

Every part is rigid and hangs off a pivot, so the model needs no skinning: the belly, head,
ears, arms, gauntlet, fingers and feet are separate meshes that the animations swing about.
"""
from __future__ import annotations

import numpy as np

import geom
import kmdl
import texture
from geom import ball, merge, tube

NAME = "k_coop_chungus"
TEXTURE = "k_coop_chungus"

# Overall size. 1.0 puts the top of the head about 2.6 m up and the ear tips at about 3.4 m.
SCALE = 1.0

# The parts are laid out with the gauntlet on +X and flipped left-to-right when the nodes are
# built, so he wears it on his left hand.
MIRROR = True

BODY_CENTRE = (0.0, 0.0, 1.05)
NECK = (0.0, 0.05, 0.75)                 # from the body centre
HEAD = (0.0, 0.05, 0.28)                 # head ball's centre, from the neck
EAR_L = (-0.20, -0.02, 0.40)             # ear roots, from the head ball's centre
EAR_R = (0.20, -0.02, 0.40)
SHOULDER_L = (-0.95, 0.12, 0.45)         # from the body centre
SHOULDER_R = (0.95, 0.12, 0.45)
ELBOW_R = (0.20, 0.36, -0.40)            # from the right shoulder
FIST = (-0.40, 0.46, 0.16)               # from the right elbow
HAND_L = (-0.26, 0.12, -0.62)            # from the left shoulder
FOOT_L = (-0.52, 0.22, 0.30)             # leg pivots, from the ground under the body
FOOT_R = (0.52, 0.22, 0.30)

STONE_ORDER = ["stone_power", "stone_space", "stone_reality", "stone_soul"]   # along the knuckles


def _mesh(shape, shadow=False):
    shape = geom.drop_degenerate(shape)
    scaled = shape.verts * SCALE
    return kmdl.Mesh(
        verts=[tuple(v) for v in scaled],
        normals=[tuple(n) for n in geom.smooth_normals(shape)],
        uvs=[tuple(uv) for uv in shape.uvs],
        faces=[tuple(int(k) for k in f) for f in shape.faces],
        texture=TEXTURE,
        shadow=shadow,
    )


def _at(position):
    return tuple(float(v) * SCALE for v in position)


def _pear(h):
    # Heavier below the middle, narrowing toward the shoulders.
    return 1.0 - 0.13 * h - 0.06 * h * h


def _body():
    belly = ball((1.12, 0.95, 1.0), texture.rect("body"), around=24, up=16, profile=_pear)
    tail = ball((0.22, 0.2, 0.22), texture.rect("white"), around=10, up=6).painted(texture.rect("white"), "flat")
    return merge(belly, tail.moved((0.0, -0.93, -0.5)))


def _head():
    r = texture.rect
    skull = ball((0.52, 0.5, 0.5), r("head"), around=20, up=14)
    parts = [skull]
    for side in (-1, 1):
        cheek = ball((0.3, 0.26, 0.23), r("white"), around=12, up=8).painted(r("white"), "flat")
        parts.append(cheek.turned(ry=side * 20).moved((side * 0.38, 0.2, -0.2)))
        # A second, smaller tuft so the cheek reads as fluff and not one ball.
        tuft = ball((0.17, 0.15, 0.13), r("white"), around=10, up=6).painted(r("white"), "flat")
        parts.append(tuft.turned(ry=side * 35).moved((side * 0.58, 0.14, -0.3)))
        eye = ball((0.115, 0.05, 0.15), r("eye"), around=12, up=8).painted(r("eye"), "planar", "y")
        parts.append(eye.turned(rz=-side * 8).moved((side * 0.17, 0.445, 0.11)))
        # Heavy lids and low, level brows: the unbothered look.
        lid = ball((0.125, 0.06, 0.085), r("fur"), around=12, up=6).painted(r("fur"), "flat")
        parts.append(lid.turned(rz=-side * 8).moved((side * 0.17, 0.452, 0.2)))
        brow = tube((side * 0.04, 0.5, 0.31), (side * 0.3, 0.45, 0.3), 0.03, 0.024, r("black"), around=6, rings=1)
        parts.append(brow.painted(r("black"), "flat"))
    muzzle = ball((0.25, 0.19, 0.16), r("white"), around=12, up=8).painted(r("white"), "flat")
    parts.append(muzzle.moved((0.0, 0.42, -0.13)))
    nose = ball((0.075, 0.05, 0.05), r("pink"), around=8, up=6).painted(r("pink"), "flat")
    parts.append(nose.moved((0.0, 0.6, -0.05)))
    mouth = ball((0.16, 0.06, 0.07), r("mouth"), around=10, up=6).painted(r("mouth"), "flat")
    parts.append(mouth.moved((0.0, 0.47, -0.27)))
    for side in (-1, 1):
        tooth = ball((0.04, 0.025, 0.065), r("tooth"), around=6, up=4).painted(r("tooth"), "flat")
        parts.append(tooth.moved((side * 0.042, 0.535, -0.245)))
    return merge(*parts).moved(HEAD)


def _ear(side):
    ear = ball((0.17, 0.07, 0.58), texture.rect("ear"), around=12, up=12).painted(texture.rect("ear"), "planar", "y")
    # Only the front shows the pink inside; the back is plain fur.
    back = geom.smooth_normals(ear)[:, 1] < -0.15
    fur = texture.rect("fur")
    ear.uvs[back] = ((fur[0] + fur[2]) / 2, (fur[1] + fur[3]) / 2)
    return ear.moved((0.0, 0.0, 0.52)).turned(ry=side * 7)


def _arm_left():
    r = texture.rect
    upper = tube((0, 0, 0), HAND_L, 0.2, 0.16, r("fur"), around=10, rings=3, bulge=0.1).painted(r("fur"), "flat")
    glove = ball((0.2, 0.19, 0.22), r("white"), around=10, up=8).painted(r("white"), "flat").moved(HAND_L)
    cuff = ball((0.2, 0.2, 0.08), r("white"), around=10, up=4).painted(r("white"), "flat")
    cuff = cuff.moved((HAND_L[0] + 0.05, HAND_L[1] - 0.02, HAND_L[2] + 0.17))
    fingers = []
    for k, dx in enumerate((-0.11, 0.0, 0.11)):
        tip = ball((0.065, 0.065, 0.11), r("white"), around=6, up=4).painted(r("white"), "flat")
        fingers.append(tip.moved((HAND_L[0] + dx, HAND_L[1] + 0.03, HAND_L[2] - 0.2)))
    return merge(upper, glove, cuff, *fingers)


def _arm_right():
    r = texture.rect
    return tube((0, 0, 0), ELBOW_R, 0.2, 0.18, r("fur"), around=10, rings=3, bulge=0.1).painted(r("fur"), "flat")


def _fist_frame():
    """Unit vectors of the hand: along the forearm, across the knuckles, and out of the back of the hand."""
    along = np.array(FIST, dtype=float)
    along /= np.linalg.norm(along)
    back = np.array([0.0, 0.8, 0.6])
    back -= along * (back @ along)
    back /= np.linalg.norm(back)
    across = np.cross(back, along)
    return along, across, back


def _in_hand(shape, frame):
    """Lays a shape built flat (x across the knuckles, y along the forearm, z out of the back) onto the hand."""
    along, across, back = frame
    return geom.Shape(shape.verts @ np.stack([across, along, back]), shape.uvs, shape.faces)


def _gauntlet():
    r = texture.rect
    frame = _fist_frame()
    along, across, back = frame
    fist = np.array(FIST)
    sleeve = tube((0, 0, 0), fist * 0.7, 0.25, 0.24, r("gauntlet"), around=14, rings=3, caps=False)
    cuff = tube(fist * -0.12, fist * 0.1, 0.3, 0.26, r("gold"), around=14, rings=1).painted(r("gold"), "flat")
    hand = _in_hand(ball((0.36, 0.3, 0.3), r("gold"), around=14, up=10).painted(r("gold"), "flat"), frame)
    plate = _in_hand(ball((0.24, 0.2, 0.07), r("gold"), around=10, up=4).painted(r("gold"), "flat"), frame)
    return merge(sleeve, cuff, hand.moved(fist), plate.moved(fist + back * 0.26)), fist, frame


def _stones(fist, frame):
    r = texture.rect
    along, across, back = frame
    mind = _in_hand(ball((0.13, 0.13, 0.08), r("stone_mind"), around=10, up=6).painted(r("stone_mind"), "planar", "z"), frame)
    parts = [mind.moved(fist + back * 0.31 - along * 0.02)]
    for k, name in enumerate(STONE_ORDER):
        gem = _in_hand(ball((0.075, 0.075, 0.06), r(name), around=8, up=6).painted(r(name), "planar", "z"), frame)
        parts.append(gem.moved(fist + along * 0.23 + across * (-0.225 + 0.15 * k) + back * 0.17))
    time = _in_hand(ball((0.075, 0.075, 0.06), r("stone_time"), around=8, up=6).painted(r("stone_time"), "planar", "z"), frame)
    parts.append(time.moved(fist + across * 0.34 + back * 0.1 - along * 0.02))
    return merge(*parts)


def _fingers(frame):
    """Two gold fingers curled under the fist. The node's pivot is the knuckle line, so turning it opens the hand."""
    r = texture.rect
    along, across, back = frame
    parts = []
    for offset in (-0.1, 0.08):
        base = across * offset
        knuckle = base + along * 0.1 - back * 0.1
        parts.append(tube(base, knuckle, 0.085, 0.08, r("gold"), around=8, rings=1).painted(r("gold"), "flat"))
        parts.append(tube(knuckle, knuckle - along * 0.12 - back * 0.16, 0.08, 0.065, r("gold"), around=8, rings=1)
                     .painted(r("gold"), "flat"))
    return merge(*parts)


def _thumb(frame):
    r = texture.rect
    along, across, back = frame
    return tube((0, 0, 0), across * 0.14 + along * 0.14 - back * 0.14, 0.095, 0.075, r("gold"), around=8, rings=2).painted(r("gold"), "flat")


def _foot():
    r = texture.rect
    sole = ball((0.3, 0.56, 0.15), r("fur"), around=12, up=8).painted(r("fur"), "flat").moved((0.0, 0.36, -0.15))
    toes = []
    for dx in (-0.17, 0.0, 0.17):
        toe = ball((0.1, 0.13, 0.11), r("fur"), around=8, up=5).painted(r("fur"), "flat")
        toes.append(toe.moved((dx, 0.85, -0.17)))
    return merge(sole, *toes)


def build_nodes():
    """The node tree in its rest pose. Every node's rest orientation is 'no rotation'."""
    Node = kmdl.Node
    root = Node(NAME)
    cutscene = root.add(Node("cutscenedummy"))
    rootdummy = cutscene.add(Node("rootdummy", position=_at(BODY_CENTRE)))

    rootdummy.add(Node("body", mesh=_mesh(_body())))
    neck = rootdummy.add(Node("neck", position=_at(NECK)))
    head = neck.add(Node("head", mesh=_mesh(_head())))
    head_centre = np.array(HEAD)
    head.add(Node("earl", position=_at(head_centre + EAR_L), mesh=_mesh(_ear(-1))))
    head.add(Node("earr", position=_at(head_centre + EAR_R), mesh=_mesh(_ear(1))))
    head.add(Node("talkdummy", position=_at(head_centre + (0.0, 0.5, -0.2))))
    head.add(Node("headhook", position=_at(head_centre + (0.0, 0.0, 0.55))))
    head.add(Node("headconjure", position=_at(head_centre + (0.0, 0.7, 0.0))))

    arml = rootdummy.add(Node("armf", position=_at(SHOULDER_L), mesh=_mesh(_arm_left())))
    arml.add(Node("lhand", position=_at(HAND_L)))

    armr = rootdummy.add(Node("armg", position=_at(SHOULDER_R), mesh=_mesh(_arm_right())))
    gauntlet_shape, fist, frame = _gauntlet()
    gauntlet = armr.add(Node("gauntlet", position=_at(ELBOW_R), mesh=_mesh(gauntlet_shape)))
    stones = gauntlet.add(Node("stones", mesh=_mesh(_stones(fist, frame))))
    stones.mesh.selfillum = (0.3, 0.3, 0.3)
    along, across, back = frame
    gauntlet.add(Node("fingers", position=_at(fist + along * 0.24 - back * 0.02), mesh=_mesh(_fingers(frame))))
    gauntlet.add(Node("thumb", position=_at(fist + across * 0.3 - back * 0.08), mesh=_mesh(_thumb(frame))))
    gauntlet.add(Node("rhand", position=_at(fist)))
    gauntlet.add(Node("handconjure", position=_at(fist + back * 0.45)))

    rootdummy.add(Node("impact", position=_at((0.0, 0.95, 0.25))))

    cutscene.add(Node("legl", position=_at(FOOT_L), mesh=_mesh(_foot())))
    cutscene.add(Node("legr", position=_at(FOOT_R), mesh=_mesh(_foot())))
    cutscene.add(Node("camerahook", position=_at((0.0, 0.0, 2.3))))
    if MIRROR:
        _mirror(root)
    return root


def _mirror(node):
    x, y, z = node.position
    node.position = (-x, y, z)
    if node.mesh:
        m = node.mesh
        m.verts = [(-vx, vy, vz) for vx, vy, vz in m.verts]
        m.normals = [(-nx, ny, nz) for nx, ny, nz in m.normals]
        m.faces = [(a, c, b) for a, b, c in m.faces]
    for child in node.children:
        _mirror(child)
