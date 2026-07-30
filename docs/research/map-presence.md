# Map presence — solved (research 2026-07-31). Ship it; nearly free.

## Key fact: the map screen reads waypoint positions LIVE every frame

`CSWGuiMapHider::Draw 0x006943D0` (embedded in `CSWGuiInGameMap` at +0xE38) draws map-note
pins by iterating an object-id list (`hider+0x238`), and for each waypoint calls
`CSWSAreaMap::GetMapPixelFromWorldCoord(*(Vector*)(wp+0x90), &px, &py)` (0x00578E00) —
i.e. the pin position is derived from the waypoint's `+0x90` position field EVERY frame.
Per-frame gates re-read: `MapNoteEnabled` at `wp+0x22C`, and fog via
`CSWSAreaMap::IsWorldPointExplored` (0x00579210). No caching, no dirty flag — move the
waypoint, the pin moves.

Only MEMBERSHIP is snapshotted: `InitializeMapNotes 0x00692C70` scans the area object
array (`CSWSArea+0x190`/count`+0x194`), keeping objects whose vtable`+0x58` (AsWaypoint)
is non-null AND `+0x228` (HasMapNote) != 0, pushing OBJECT IDS to `hider+0x238`. Called
once per map-screen open (OnPanelAdded 0x00693650 → 0x00693781). So: spawn the marker at
area load and it survives close/reopen.

**Minimap (corner) draws NO map notes** — `CSWGuiMainInterface::DrawMap 0x0068AB10` only
centres on the party leader and draws one arrow label. A peer marker there needs a
render-thread GUI detour (~200 lines, crash-risk). DEFER; the M-screen pin is likely
enough.

## Design: peer-pin waypoint, constructed directly (bypass the VM)

⚠ `CreateObject(OBJECT_TYPE_WAYPOINT=32)` creates the object but RETURNS OBJECT_INVALID —
the waypoint case in ExecuteCommandCreateObject (0x0052FC97) forgets to write the return
register. So construct directly:

```
p = operator_new(0x240)                          // 0x006FA7E6
CSWSWaypoint::Constructor(p, 0x7F000000)         // 0x005C7E70  ret 4
CSWSObject::SetPosition(p, &pos, 1, 1, 0)        // 0x004CD1A0  (already in Odyssey)
CSWSObject::SetOrientation(p, &ori)              // 0x004CD170  (already in Odyssey)
CSWSWaypoint::AddToArea(p, area, x, y, z, 0)     // 0x005C8580  ret 0x14
*(int*)(p+0x228) = 1;   // HasMapNote     -> included by InitializeMapNotes
*(int*)(p+0x22C) = 1;   // MapNoteEnabled -> drawn
id = *(uint*)(p+0x04);  // real object id (teardown)
// optional: peer name into CExoLocString at p+0x230 (shows in note label on select)
```

Per tick (main thread, phantom cadence): `*(Vector*)(p+0x90) = peerPos;` — that's all.
Optional: `CSWSAreaMap::SetWorldPointExplored(areaMap, peerPos, 1)` (0x005792D0) each tick
so fog doesn't hide the pin — this ALSO gives shared fog-of-war reveal for free.

Teardown in the existing pre-store flush window (SaveWaypoint 0x005C8230 exists, so an
un-destroyed marker bakes into the save): RemoveFromArea 0x005C8350 then vtable[0] with
deleting flag 1.

areaMap: read live `CSWSAreaMap*` at `CSWSModule+0x218` (module from GetModule 0x004AE6B0)
and call 0x00578E00 — do not reimplement the world→map transform.

**Override option:** a `k_coop_mapnote.utw` (HasMapNote/MapNoteEnabled/inline MapNote,
`k_coop_` prefix = zero collision) could seed the fields, but direct construction needs
no asset. Ship the plugin path.

Size: ~150 lines, new MapMarkerController.cs + ~6 Odyssey delegates. Peer position is
already replicated (phantom uses it), so NO new protocol.
