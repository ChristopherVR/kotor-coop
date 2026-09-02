// Odyssey.Map.cs - map marker waypoints, the minimap, and the module-transition
// safety window for the in-world GUI's minimap-name read.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- map markers -------------------------------------------------------------
    //
    // A peer's position shows on the M-screen map as a map-note pin. The map's draw code
    // (CSWGuiMapHider::Draw) re-reads each map-note waypoint's position field EVERY frame
    // and converts it to a pixel, so a waypoint whose +0x90 we move per tick moves its pin
    // live — no GUI patching. The waypoint is constructed DIRECTLY rather than through the
    // NWScript CreateObject routine, because that routine has an engine bug for waypoints:
    // it makes the object but forgets to return its id (returns OBJECT_INVALID).

    /// <summary>CSWSWaypoint total size, for the engine allocator.</summary>
    private const int WaypointSize = 0x240;

    /// <summary>CSWSWaypoint + this = HasMapNote (nonzero -&gt; the map includes it).</summary>
    private const int WaypointHasMapNote = 0x228;

    /// <summary>CSWSWaypoint + this = MapNoteEnabled (nonzero -&gt; the pin is drawn), re-read
    /// every frame by the map draw.</summary>
    private const int WaypointMapNoteEnabled = 0x22C;

    // Global::operator_new(size) — the engine allocator, so the object can be freed by the
    // engine's own teardown.
    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNewFn =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWSWaypoint::Constructor(this, objectId). Passing 0x7F000000 makes the constructor
    // allocate a real object id through AddInternalObject, exactly as creature spawning does.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> WaypointCtorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005C7E70;

    // CSWSWaypoint::AddToArea(this, area, x, y, z, bStore). Appends the object id to the
    // area's object array, which is what the map's InitializeMapNotes scans.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, void> WaypointAddToAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, void>)0x005C8580;

    // CSWSWaypoint::RemoveFromArea(this). Half of teardown; the destructor is the other half.
    //
    // ⚠ NO ARGUMENTS. This was declared as (this, area) and called with the area, and the
    // function does not take one: it ends in a plain `ret` (0x005C83A8) and derives the area
    // from `this` via 0x004CB120. A __thiscall callee pops its own arguments, so every call
    // pushed four bytes that nothing ever popped — the stack pointer walked away four bytes
    // at a time, and a skewed stack does not fault where it is caused, it corrupts a frame
    // and surfaces later as a stack-cookie failure (0xC0000409) in whatever returns next.
    // Found by tools/callaudit.py, which compares every one of these declarations against
    // the callee's own `ret` immediate.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> WaypointRemoveFromAreaFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005C8350;

    /// <summary>The server-side area of the loaded module, or 0.</summary>
    private static nint GetAreaPointer()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        return module == 0 ? 0 : Deref(module + ModuleAreaOffset);
    }

    // ---- the minimap ---------------------------------------------------------------
    //
    // CSWGuiMainInterface::DrawMap 0x0068AB10, read end to end, draws the area image, the
    // fog of war and EXACTLY ONE arrow — the local player's. There is no map-note loop and
    // no party loop in it, so the M-screen pin the marker controller maintains was never
    // going to appear there, and no data-only trick can put it there.
    //
    // What CAN: the arrow is a control embedded at panel+0x5F40, drawn through its own
    // vtable inside a viewport the engine has already set up. Drawing it a SECOND time, at
    // an offset, costs no new GUI object and no viewport work. The offset falls out of the
    // engine's own scroll formula: it places the map image at (side/2 - pixel*scale), so a
    // map pixel lands on screen at side/2 + (pixel - localPixel)*scale — i.e. RELATIVE to
    // the arrow, which is what makes this independent of where the viewport actually is.

    /// <summary>CSWGuiMainInterface + this = the embedded player-arrow control.</summary>
    private const int MainInterfaceArrowControl = 0x5F40;

    /// <summary>CSWGuiObject + this = its extent {x, y, w, h}, as ints.</summary>
    private const int GuiObjectExtent = 0x04;

    // The arrow control embeds a CSWGuiImage whose CSWGuiImageParams start 0x68 in, so the
    // params' own layout (resref 0x00, angle 0x10, alpha 0x14, colour 0x18, flags 0x24)
    // lands at the offsets below. Confirmed by a live read of the panel: angle held the
    // rotation DrawMap had just written, and alpha/colour read exactly 1.0 / (1,1,1).

    /// <summary>Arrow control + this = CSWGuiImageParams.angle — the rotation DrawMap writes
    /// each draw (panel+0x5FB8 in its own terms).</summary>
    private const int ArrowRotation = 0x78;

    /// <summary>Arrow control + this = CSWGuiImageParams.alpha.</summary>
    private const int ArrowAlpha = 0x7C;

    /// <summary>Arrow control + this = CSWGuiImageParams.color, three floats multiplied into
    /// the icon. Above 1.0 they saturate a channel rather than clamping the result, which is
    /// what makes a red arrow possible at all out of blue art.</summary>
    private const int ArrowColour = 0x80;

    /// <summary>
    /// One tint per peer slot, so two partners are never the same arrow.
    ///
    /// These are MULTIPLIERS against the shipped blue arrow, not colours — which is why they
    /// look wrong written down and right on screen, and why they are over-bright. Each was
    /// eyeballed on the real icon: (1, 0.15, 0.15) came out maroon, (4, 0.4, 0.4) purple and
    /// (4, 0.3, 0) orange, because the art's blue channel dominates until the opposing
    /// channel is driven to zero and the wanted one past 1.0. All six below were captured and
    /// looked at; more peers than that cycle the list.
    /// </summary>
    private static ReadOnlySpan<float> PeerArrowColours =>
    [
        6.0f, 0.0f, 0.0f,   // red
        0.2f, 4.0f, 0.2f,   // green
        4.0f, 0.3f, 0.0f,   // orange
        5.0f, 0.4f, 3.0f,   // purple
        0.2f, 3.0f, 3.0f,   // cyan
        4.0f, 2.0f, 0.0f,   // yellow
    ];

    /// <summary>CSWGuiObject vtable +0x04 = SetExtent(Extent*) — a plain four-dword copy
    /// (0x00409E90), but called through the slot because the arrow's class may override it.</summary>
    private const int GuiSetExtentSlot = 0x04;

    /// <summary>CSWGuiObject vtable +0x38 = Draw(arg) — the same slot DrawMap itself calls
    /// for the map image and the arrow.</summary>
    private const int GuiDrawSlot = 0x38;

    /// <summary>CSWSModule + this = its CSWSAreaMap.</summary>
    private const int ModuleAreaMapOffset = 0x218;

    /// <summary>CSWSAreaMap + this = map data present; every accessor fails when it is 0.</summary>
    private const int AreaMapHasData = 0x04;

    /// <summary>CSWSAreaMap + this = the pixel-to-screen scale DrawMap multiplies by.</summary>
    private const int AreaMapScale = 0x14;

    /// <summary>CSWSAreaMap + this + slot*4 = a party member's map pixel X (-1 = unset).</summary>
    private const int AreaMapPartyX = 0x28;

    private const int AreaMapPartyY = 0x34;

    /// <summary>
    /// The party slot borrowed to convert one peer's world position into a map pixel.
    ///
    /// The engine's own world→pixel maths lives behind
    /// <see cref="SetPartyMemberWorldLocationFn"/>, applies the area's orientation mode, and
    /// is the only way to be certain a marker lands in the same space as the arrow. Slot 2
    /// is the last of three; its previous contents are saved and put back before this
    /// returns, so nothing observes the borrow — not even a party of three, whose slot 2
    /// <c>UpdateMapData</c> rewrites every frame anyway.
    /// </summary>
    private const int AreaMapScratchSlot = 2;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int*, int*, int> GetPartyMemberMapLocationFn =
        (delegate* unmanaged[Thiscall]<nint, int, int*, int*, int>)0x005791B0;

    /// <summary>CSWSAreaMap::SetPartyMemberWorldLocation(slot, Vector) — <c>ret 0x10</c>, so
    /// the vector is three separate stack dwords, not a pointer.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, float, float, float, void> SetPartyMemberWorldLocationFn =
        (delegate* unmanaged[Thiscall]<nint, int, float, float, float, void>)0x005790C0;

    /// <summary>CSWSAreaMap::GetMapRotateCCWFromWorldOrientation(Vector) — <c>ret 0xC</c>,
    /// returns the arrow angle for a facing.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, float, float, float> GetMapRotateFn =
        (delegate* unmanaged[Thiscall]<nint, float, float, float, float>)0x00578ED0;

    /// <summary>The server module's area map, or 0 when there is no world.</summary>
    private static nint GetAreaMap()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        if (module == 0)
        {
            return 0;
        }

        nint areaMap = Deref(module + ModuleAreaMapOffset);
        return LooksLikeHeapPointer(areaMap) && *(int*)(areaMap + AreaMapHasData) != 0 ? areaMap : 0;
    }

    /// <summary>
    /// Draws one extra arrow on the minimap per peer, from inside the engine's own open
    /// viewport (see <see cref="MinimapPeerHook"/>). Everything it touches is saved and put
    /// back before it returns: the arrow's extent, the arrow's rotation and the borrowed
    /// party slot. A peer outside the map is clipped by the viewport the engine already set
    /// up, which is the behaviour you want — an off-map partner simply is not on your map.
    ///
    /// MAIN THREAD, INSIDE THE RENDER PATH. No allocation, no logging, no engine object
    /// construction: this runs every frame the minimap is visible.
    /// </summary>
    public static void DrawMinimapPeers(nint mainInterface, nint drawArg, SharedBlockView block)
    {
        nint areaMap = GetAreaMap();
        if (mainInterface == 0 || areaMap == 0)
        {
            return;
        }

        int localX;
        int localY;
        if (GetPartyMemberMapLocationFn(areaMap, 0, &localX, &localY) == 0)
        {
            return; // the player's own pixel is unset; there is nothing to be relative to
        }

        nint arrow = mainInterface + MainInterfaceArrowControl;
        nint vtable = *(nint*)arrow;
        if (!LooksLikeCodePointer(vtable))
        {
            return;
        }

        var setExtent = (delegate* unmanaged[Thiscall]<nint, int*, void>)(*(nint*)(vtable + GuiSetExtentSlot));
        var draw = (delegate* unmanaged[Thiscall]<nint, nint, void>)(*(nint*)(vtable + GuiDrawSlot));

        // The vtable itself was checked; the two slots taken out of it were not. A garbage
        // slot here is a call to an arbitrary address from the render path.
        if (!LooksLikeCodePointer((nint)setExtent) || !LooksLikeCodePointer((nint)draw))
        {
            return;
        }

        float scale = *(float*)(areaMap + AreaMapScale);
        if (!float.IsFinite(scale))
        {
            return;
        }

        // A scratch copy, never the control's own field: SetExtent copies FROM the pointer
        // INTO this+0x04, so handing it that same address would be a self-copy — harmless
        // today and a trap the day the arrow's class overrides the slot.
        int* extentField = (int*)(arrow + GuiObjectExtent);
        int* extent = stackalloc int[4];
        for (int e = 0; e < 4; e++)
        {
            extent[e] = extentField[e];
        }

        int savedX = extent[0];
        int savedY = extent[1];
        float savedRotation = *(float*)(arrow + ArrowRotation);
        float savedAlpha = *(float*)(arrow + ArrowAlpha);
        float* colour = (float*)(arrow + ArrowColour);
        float savedR = colour[0];
        float savedG = colour[1];
        float savedB = colour[2];

        int* scratchX = (int*)(areaMap + AreaMapPartyX + (AreaMapScratchSlot * 4));
        int* scratchY = (int*)(areaMap + AreaMapPartyY + (AreaMapScratchSlot * 4));
        int scratchSavedX = *scratchX;
        int scratchSavedY = *scratchY;

        bool drewAny = false;
        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        for (int i = 0; i < wanted; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active)
            {
                continue;
            }

            // ⚠ Never hand the engine a position we have not looked at. This runs on the
            // RENDER PATH against a slot an entirely separate process writes, and a slot can
            // legitimately be read mid-write: a peer that has connected but not yet published
            // is all zeroes, and a torn write is arbitrary bits reinterpreted as floats. The
            // phantom spawner has always screened positions this way (IsPlausiblePosition);
            // this path was published without the same screen, and it is the one path where
            // the consequence lands inside the renderer.
            if (!Vec3.IsWorldPlausible(remote.Position) || !float.IsFinite(remote.Facing))
            {
                continue;
            }

            // -1 first: SetPartyMemberWorldLocation only STORES on a successful conversion,
            // so without this an off-map peer would silently reuse the previous peer's pixel.
            *scratchX = -1;
            SetPartyMemberWorldLocationFn(
                areaMap, AreaMapScratchSlot, remote.Position.X, remote.Position.Y, remote.Position.Z);

            int peerX;
            int peerY;
            if (GetPartyMemberMapLocationFn(areaMap, AreaMapScratchSlot, &peerX, &peerY) == 0)
            {
                continue; // off the map image entirely
            }

            // Clamped before the cast. (int) on a float larger than int.MaxValue is not a
            // saturating conversion in IL — it is undefined, and on x86 it yields
            // 0x80000000, which is then handed to the engine as an extent.
            int wantX = savedX + (int)Math.Clamp(MathF.Round((peerX - localX) * scale), -8192f, 8192f);
            int wantY = savedY + (int)Math.Clamp(MathF.Round((peerY - localY) * scale), -8192f, 8192f);

            // ⚠ HELD AT THE EDGE, NOT CLIPPED AWAY.
            //
            // The minimap viewport is about 120 px and the map draws at roughly 2.8 px per
            // world unit, so it shows a radius of only ~21 units — the room you are standing
            // in. Letting the engine clip a peer outside that means your partner is missing
            // from the minimap for almost all of normal play, which is the one question the
            // feature exists to answer. Reported from play as "it doesn't show the other
            // person at all", and visible in the very first separated screenshot: at 18 units
            // the marker was already half off the edge.
            //
            // So an off-map peer is pinned to the border in their direction. You lose the
            // exact distance and keep the bearing, which is the useful half; the marker is
            // also faded, so "over there somewhere" never reads as "right there".
            bool offMap = ClampToViewport(mainInterface, extent[2], extent[3], ref wantX, ref wantY);

            extent[0] = wantX;
            extent[1] = wantY;
            *(float*)(arrow + ArrowRotation) =
                GetMapRotateFn(areaMap, MathF.Cos(remote.Facing), MathF.Sin(remote.Facing), 0f);

            int tint = (i % (PeerArrowColours.Length / 3)) * 3;
            colour[0] = PeerArrowColours[tint];
            colour[1] = PeerArrowColours[tint + 1];
            colour[2] = PeerArrowColours[tint + 2];
            *(float*)(arrow + ArrowAlpha) = offMap ? OffMapAlpha : savedAlpha;

            setExtent(arrow, extent);
            draw(arrow, drawArg);
            drewAny = true;
        }

        *scratchX = scratchSavedX;
        *scratchY = scratchSavedY;

        if (drewAny)
        {
            extent[0] = savedX;
            extent[1] = savedY;
            *(float*)(arrow + ArrowRotation) = savedRotation;
            *(float*)(arrow + ArrowAlpha) = savedAlpha;
            colour[0] = savedR;
            colour[1] = savedG;
            colour[2] = savedB;
            setExtent(arrow, extent);

            // And once more in its own colours, so the player is on TOP. These draws land
            // after the engine's, so a partner standing where you are stands on your arrow
            // and hides it — which is the one place you always want to be able to find
            // yourself. One extra textured quad, only when a peer was actually drawn.
            draw(arrow, drawArg);
        }
    }

    /// <summary>
    /// Creates a map-note waypoint at a position and returns its object id (0 on failure).
    /// The pin appears on the map screen next time it is opened (the map snapshots note
    /// MEMBERSHIP on open, but reads each note's POSITION live), so spawn it at area load.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint SpawnMapMarker(Vec3 position, out nint waypoint)
    {
        waypoint = 0;

        nint area = GetAreaPointer();
        if (area == 0)
        {
            return ObjectInvalid;
        }

        nint wp = OperatorNewFn(WaypointSize);
        if (wp == 0)
        {
            return ObjectInvalid;
        }

        WaypointCtorFn(wp, 0x7F000000);
        SetPositionFn(wp, &position, 1, 1, 0);
        WaypointAddToAreaFn(wp, area, position.X, position.Y, position.Z, 0);

        *(int*)(wp + WaypointHasMapNote) = 1;
        *(int*)(wp + WaypointMapNoteEnabled) = 1;

        waypoint = wp;
        return *(uint*)(wp + 0x04);
    }

    /// <summary>Moves a map marker; its pin follows on the next map draw. MAIN THREAD ONLY.</summary>
    public static void MoveMapMarker(nint waypoint, Vec3 position)
    {
        if (waypoint != 0)
        {
            *(Vec3*)(waypoint + ObjPosition) = position;
        }
    }

    /// <summary>
    /// Destroys a map marker through the engine: remove from the area, then the object's
    /// own deleting destructor (vtable slot 0, flag 1). Required in the pre-store flush
    /// window — a waypoint left in the area is serialised into the saved module.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void DestroyMapMarker(nint waypoint)
    {
        if (waypoint == 0)
        {
            return;
        }

        // The area is still checked — it is how we know a module is loaded at all — but it is
        // NOT passed: the function reads it off the waypoint itself.
        if (GetAreaPointer() != 0)
        {
            WaypointRemoveFromAreaFn(waypoint);
        }

        nint vtable = *(nint*)waypoint;
        var destructor = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)vtable);
        destructor(waypoint, 1);
    }

    // ---- module-transition safety -------------------------------------------------
    //
    // The engine's in-world GUI rebuilds the minimap name every frame from the SERVER's
    // module, and dereferences it with NO null check. A transition destroys the server
    // module and only builds the replacement on the next server MainLoop frame — but the
    // outer loop runs the CLIENT MainLoop first, so there is a window in which the map init
    // reads a destroyed module and faults (measured: ~15 ms after the unload, in
    // CResRef::CopyToString).
    //
    // Retail's protection is that the client tears its OWN world down first, which it does
    // when it processes the server's queued "dump player" message. A plugin-driven
    // transition can reach the window before that message is serviced, so the client is
    // still holding a world it thinks is live. Doing what the engine's own handler does —
    // calling CClientExoApp::UnloadModule — closes the window.
    // See docs/research/travel-map-init-crash.md.

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClientUnloadModuleFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005ED430;

    /// <summary>
    /// Tears the CLIENT's world down when the SERVER's module is already gone — the exact
    /// call the engine's own dump-player handler makes, just made a frame earlier than the
    /// queued message would. Self-limiting: the engine's later call early-outs, and the
    /// client rebuilds itself from the next Module_Info. Returns true if it acted.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool CloseClientWorldIfServerModuleGone()
    {
        nint server = GetServer();
        nint client = GetClient();
        if (server == 0 || client == 0)
        {
            return false;
        }

        // Server module already destroyed, client still holding one: the unsafe window.
        if (GetModuleFn(server) != 0 || ClientGetModuleFn(client) == 0)
        {
            return false;
        }

        ClientUnloadModuleFn(client);
        return true;
    }

}
