// Odyssey.Walkmesh.cs - the one choke point every plugin code path that makes the engine
// PATH TOWARD A COORDINATE must go through before issuing the move: TryValidateMoveDestination.
//
// docs/research/walkmesh-guard.md has the full write-up (RE evidence, call-site audit, live
// recipe). Short version:
//
// CRASH (2026-09-25, danm13): `walkto 187 100` -> Odyssey.MoveToLocation -> engine
// ActionMoveToLocation -> AIActionMoveToPoint -> CSWSArea::TestDirectLine (0x004BCB70) ->
// CSWSRoom::ClippedLineSegmentWalkable (0x00579670, a bare tail-call thunk) ->
// CSWRoomSurfaceMesh::ClippedLineSegmentWalkable (0x00584220) -> ...ComputeClippedLineSegment
// (0x00581720) walks the line across the mesh, and when it steps off the LAST valid triangle
// with no neighbour (an off-mesh endpoint - exactly what (187,100) is) it hands the "no
// triangle" sentinel -1 to CSWRoomSurfaceMesh::GetTriangleAdjacency (0x00580620) UNGUARDED.
// GetTriangleAdjacency only checks the UPPER bound (`cmp eax,[ecx+0x58]; jge safe-return`, at
// 0x00580624/0x00580627) - a signed -1 index is not >= a positive triangle count, so the
// check passes and `mov edx,[edx+esi*4]` at 0x0058063B (esi = triIndex*3) reads 12 bytes
// BEFORE the adjacency array (this+0x88) - an out-of-bounds/invalid pointer. THAT read is the
// access violation. The bad pointer is the adjacency-array element computed from a -1
// (0xFFFFFFFF) triangle index the bounds check never rejects.
//
// The engine's OWN safe query for "can I even ask about this point" is
// CSWSArea::PositionWalkable (0x00506400, __thiscall, ret 0xC - x,y,z pushed by value, no
// pointer). Disassembly (0x00506400-0x005064C9) confirms it: it calls CSWSArea::GetRoom
// (0x004BB600, whose own AABB-scan loop returns 0 cleanly when nothing contains the point -
// no crash) and, only if a room is found, calls
// CSWRoomSurfaceMesh::GetSurfaceMaterialWalkCheckOnly (0x00581A10) on room+0x3C. THAT function
// calls CSWRoomSurfaceMesh::CheckAABBWalkable (0x00581530) to find the actual TRIANGLE at
// (x,y,z); when none exists - inside the room's bounding box but off the real mesh, i.e. the
// walkto crash's exact shape - it returns 0 cleanly (`cmp eax,-1; je -> xor eax,eax; ret`,
// 0x00581ACF-0x00581ADA) WITHOUT ever reaching GetTriangleAdjacency. PositionWalkable never
// goes near TestDirectLine/ClippedLineSegmentWalkable/GetTriangleAdjacency, so it cannot hit
// the bug above; it is what every caller below now validates a destination with FIRST.
namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CSWSArea::PositionWalkable(CSWSArea* this, float x, float y, float z) -&gt; BOOL</c>,
    /// __thiscall, <c>ret 0xC</c> (three stack floats pushed by value, right-to-left — VERIFIED
    /// by disassembly: <c>lea eax,[esp+0x24]</c> at 0x00506420 computes entry_esp+4, i.e. the
    /// FIRST stack dword, and hands it to GetRoom as <c>&amp;position</c>, so x/y/z are
    /// contiguous stack dwords, not a pointer argument). See this file's header comment for
    /// the full call chain this was found through (0x00506400-0x005064C9).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, float, float, int> PositionWalkableFn =
        (delegate* unmanaged[Thiscall]<nint, float, float, float, int>)0x00506400;

    /// <summary><c>CSWSObject::GetArea()</c>, __thiscall, plain <c>ret</c> — same address as
    /// <c>Odyssey.Combat.cs</c>'s <c>GetAreaFn</c> / <c>Odyssey.PlacedMines.cs</c>'s
    /// <c>PlacedMineGetAreaFn</c>; re-declared here rather than shared, matching this
    /// codebase's existing convention of one private binding per concern file. Called fresh on
    /// the CREATURE being moved (never a cached/agent-published area) so a stale cross-module
    /// pointer is never what gets validated — see docs/kotor-coop-transition-window-rules
    /// ("anything the plugin dereferences must be resolved fresh via Odyssey each frame").</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> WalkGuardGetAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004CB120;

    /// <summary>
    /// The one guard every plugin path that makes the engine path TOWARD A COORDINATE must
    /// call before issuing the move: <c>Odyssey.MoveToLocation</c> (queued
    /// ActionMoveToLocation — walkto, door-walk retries, phantom/materialised-enemy drive) and
    /// <c>Odyssey.SnapTo</c> (the raw teleport/snap primitive — tp, party/attack snaps, follow
    /// join snaps). Rejects NaN/inf/out-of-world magnitude, the exact-origin poison sentinel,
    /// and — via the engine's own <see cref="PositionWalkableFn"/> — a point that is not
    /// actually on the mover's current walkmesh, all WITHOUT ever touching
    /// TestDirectLine/ClippedLineSegmentWalkable/GetTriangleAdjacency. MAIN THREAD ONLY.
    /// </summary>
    public static bool TryValidateMoveDestination(nint creature, Vec3 destination, out MoveRejectReason reason)
    {
        // The pure part first (no engine call) — see WalkmeshGuardRule for why it is split
        // out and its own unit tests.
        reason = WalkmeshGuardRule.ClassifyDestination(destination);
        if (reason != MoveRejectReason.None)
        {
            return false;
        }

        if (creature == 0)
        {
            reason = MoveRejectReason.NoArea;
            return false;
        }

        nint area = WalkGuardGetAreaFn(creature);
        if (area == 0)
        {
            reason = MoveRejectReason.NoArea;
            return false;
        }

        if (PositionWalkableFn(area, destination.X, destination.Y, destination.Z) == 0)
        {
            reason = MoveRejectReason.NotWalkable;
            return false;
        }

        reason = MoveRejectReason.None;
        return true;
    }

    /// <summary>CSWSObject + this = object id — the same offset every other concern file
    /// re-declares its own copy of (ContainerCloseHook, CreatureFinder, Odyssey.Faction.cs,
    /// OdysseyTriggers.cs, OdysseyPlaceables.cs, ...).</summary>
    private const int WalkGuardObjectIdOffset = 0x04;

    /// <summary>One refusal logged per (object id, reason) per module — never per frame/per
    /// call, per docs/kotor-coop-per-frame-engine-writes: "a per-frame detect -&gt; act loop
    /// with no latch is the pattern to look for first" (that write-up was a crash from an
    /// UNLATCHED per-frame WRITE; this is only a log line, but a refused walkto retried every
    /// frame by some future caller would still flood the log without this). Cleared at every
    /// module boundary by <see cref="InvalidateMoveGuardLog"/>.</summary>
    private static readonly HashSet<ulong> _moveGuardLoggedThisModule = [];

    /// <summary>
    /// Logs a refusal the FIRST time this object hits this reason in the current module, then
    /// goes quiet for it. Callers still refuse the move every time; only the log line is
    /// latched. Returns the human-readable reason string so a cheat verb can answer with it.
    /// </summary>
    public static string LogMoveRefusalOnce(nint creature, uint objectId, Vec3 destination, MoveRejectReason reason)
    {
        string why = reason switch
        {
            MoveRejectReason.NotPlausible => "destination is not a finite in-world point",
            MoveRejectReason.Origin => "destination is the area-origin poison sentinel (0,0,0)",
            MoveRejectReason.NoArea => "the mover has no resolvable area (no loaded world, or mid-transition)",
            MoveRejectReason.NotWalkable => "destination is not on the walkmesh (CSWSArea::PositionWalkable said no)",
            _ => "accepted",
        };

        uint id = objectId != 0 ? objectId
            : creature != 0 ? *(uint*)(creature + WalkGuardObjectIdOffset)
            : 0;

        ulong key = ((ulong)id << 8) | (byte)reason;
        if (_moveGuardLoggedThisModule.Add(key))
        {
            PluginHost.Log(
                $"move refused: 0x{id:X} -> ({destination.X:F1},{destination.Y:F1},{destination.Z:F1}) - {why}");
        }

        return why;
    }

    /// <summary>Module-boundary reset for <see cref="_moveGuardLoggedThisModule"/> — wired into
    /// <c>PluginHost.InvalidateAll</c> alongside every other controller's <c>Invalidate()</c>.</summary>
    public static void InvalidateMoveGuardLog() => _moveGuardLoggedThisModule.Clear();
}
