"""Walkmesh pathfinding for the playtest harness: where a player character can actually walk.

An area is the room list in its layout (`<area>.lyt`) plus one walkmesh per room
(`<room>.wok`, format `BWM V1.0`). Room walkmesh vertices are already in world coordinates,
faces carry a surfacemat.2da material (the `walk` column says which are walkable), and the
walkable faces come first with an adjacency table. Rooms meet at shared edges, so faces are
joined across rooms by matching edge vertices.

    nav = Area.load("end_m01aa")              # module resref
    path = nav.path((18.6, 116.0), (39.5, 125.0), blocked=[(39.56, 125.38)])

`path` is a list of (x, y) waypoints, smoothed so each segment stays on walkable ground, or
None when there is no route. `blocked` points (closed doors) make the faces within
`BLOCK_RADIUS` impassable.

Offline: reads game files through kotorfiles.py only; no running game.
"""
from __future__ import annotations

import heapq
import math
import struct
from pathlib import Path

BLOCK_RADIUS = 1.6
# surfacemat.2da `walk` column; read from the game when available, this is the stock table.
STOCK_WALKABLE = {1, 3, 4, 5, 6, 9, 10, 11, 12, 13, 14, 18}


def parse_bwm(data: bytes):
    """(vertices, faces, materials) of a BWM V1.0 walkmesh; faces are vertex-index triples."""
    if data[:8] != b"BWM V1.0":
        raise ValueError("not a BWM V1.0 walkmesh")
    u = lambda o: struct.unpack_from("<I", data, o)[0]
    nv, vo, nf, fo, mo = u(0x48), u(0x4C), u(0x50), u(0x54), u(0x58)
    verts = [struct.unpack_from("<3f", data, vo + 12 * i) for i in range(nv)]
    faces = [struct.unpack_from("<3I", data, fo + 12 * i) for i in range(nf)]
    mats = [u(mo + 4 * i) for i in range(nf)]
    return verts, faces, mats


def parse_lyt(text: str):
    """{room: (x, y)} from a layout file's `roomcount` block, in file order."""
    rooms, count = {}, None
    for line in text.splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == "roomcount":
            count = int(parts[1])
            continue
        if count is not None and len(rooms) < count and len(parts) == 4:
            rooms[parts[0].lower()] = (float(parts[1]), float(parts[2]))
    return rooms


class Area:
    def __init__(self, triangles, rooms=None):
        """triangles: list of ((x,y),(x,y),(x,y)) walkable faces, world coordinates; rooms:
        {name: (x, y)} layout origins, used as exploration targets."""
        self.rooms = dict(rooms or {})
        self.tris = triangles
        self.centroids = [((a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3) for a, b, c in triangles]
        self.boxes = [(min(a[0], b[0], c[0]), min(a[1], b[1], c[1]), max(a[0], b[0], c[0]), max(a[1], b[1], c[1]))
                      for a, b, c in triangles]
        edges = {}
        for i, (a, b, c) in enumerate(triangles):
            for p, q in ((a, b), (b, c), (c, a)):
                k = tuple(sorted((_key(p), _key(q))))
                edges.setdefault(k, []).append(i)
        self.neighbours = [[] for _ in triangles]
        for (p, q), owners in edges.items():
            mid = ((p[0] + q[0]) / 200, (p[1] + q[1]) / 200)
            for i in owners:
                for j in owners:
                    if i != j:
                        self.neighbours[i].append((j, mid))

    @classmethod
    def load(cls, module: str, game_dir: str | None = None):
        import kotorfiles
        game = kotorfiles.Game(game_dir) if game_dir else kotorfiles.Game()
        area = _area_name(game, module)
        rooms = parse_lyt(game.read(f"{area}.lyt").decode("latin-1"))
        walkable = _walkable_materials(game)
        tris = []
        for room in rooms:
            try:
                data = _read_any(game, module, f"{room}.wok")
                verts, faces, mats = parse_bwm(data)
            except (KeyError, ValueError, OSError):
                continue
            for (i, j, k), m in zip(faces, mats):
                if m in walkable:
                    tris.append(tuple((verts[n][0], verts[n][1]) for n in (i, j, k)))
        return cls(tris, rooms)

    def locate(self, point):
        x, y = point
        for i, (x0, y0, x1, y1) in enumerate(self.boxes):
            if x0 - 1e-3 <= x <= x1 + 1e-3 and y0 - 1e-3 <= y <= y1 + 1e-3 and _inside(self.tris[i], point):
                return i
        return None

    def nearest(self, point, limit=3.0):
        """The walkable face at the point, or the closest centroid within `limit` metres."""
        face = self.locate(point)
        if face is not None:
            return face
        best = min(range(len(self.tris)), key=lambda i: math.dist(self.centroids[i], point), default=None)
        return best if best is not None and math.dist(self.centroids[best], point) <= limit else None

    def path(self, start, goal, blocked=()):
        s, g = self.nearest(start), self.nearest(goal)
        if s is None or g is None:
            return None
        banned = {i for i, c in enumerate(self.centroids)
                  if any(math.dist(c, b) < BLOCK_RADIUS for b in blocked)} - {s, g}
        came, cost = {s: None}, {s: 0.0}
        frontier = [(0.0, s)]
        while frontier:
            _, cur = heapq.heappop(frontier)
            if cur == g:
                break
            for nxt, mid in self.neighbours[cur]:
                if nxt in banned:
                    continue
                c = cost[cur] + math.dist(self.centroids[cur], mid) + math.dist(mid, self.centroids[nxt])
                if c < cost.get(nxt, math.inf):
                    cost[nxt], came[nxt] = c, (cur, mid)
                    heapq.heappush(frontier, (c + math.dist(self.centroids[nxt], goal), nxt))
        if g not in came:
            return None
        points, cur = [tuple(goal)], g
        while came[cur] is not None:
            prev, mid = came[cur]
            points.append(mid)
            cur = prev
        points.append(tuple(start))
        points.reverse()
        return self.smooth(points, banned)

    def walkable_segment(self, a, b, banned=frozenset(), step=0.4):
        n = max(1, int(math.dist(a, b) / step))
        for i in range(n + 1):
            p = (a[0] + (b[0] - a[0]) * i / n, a[1] + (b[1] - a[1]) * i / n)
            face = self.locate(p)
            if face is None or face in banned:
                return False
        return True

    def smooth(self, points, banned=frozenset()):
        """Greedy string-pulling: from each kept point, jump to the furthest point still in
        straight walkable reach."""
        out, i = [points[0]], 0
        while i < len(points) - 1:
            j = len(points) - 1
            while j > i + 1 and not self.walkable_segment(points[i], points[j], banned):
                j -= 1
            out.append(points[j])
            i = j
        return out


def _key(p):
    return (round(p[0] * 100), round(p[1] * 100))


def _inside(tri, p):
    (ax, ay), (bx, by), (cx, cy) = tri
    d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
    if abs(d) < 1e-9:
        return False
    l1 = ((by - cy) * (p[0] - cx) + (cx - bx) * (p[1] - cy)) / d
    l2 = ((cy - ay) * (p[0] - cx) + (ax - cx) * (p[1] - cy)) / d
    return l1 >= -1e-6 and l2 >= -1e-6 and 1 - l1 - l2 >= -1e-6


def _read_any(game, module, resname):
    """Module archives first (a module may ship its own walkmesh), then override/key."""
    try:
        return game.module_read(module, resname)
    except KeyError:
        return game.read(resname)


def _area_name(game, module):
    """A module's area resref: the .are inside its archives (end_m01aa -> m01aa)."""
    for archive in game.module(module):
        for res in archive.resources:
            if res.restype == 2012:  # .are
                return res.resref.lower()
    return module.lower().split("_", 1)[-1]


def _walkable_materials(game):
    try:
        table = game.twoda("surfacemat")
        return {int(table.row_labels[i]) for i in range(table.row_count) if table.get(i, "walk").strip() == "1"}
    except Exception:
        return set(STOCK_WALKABLE)
