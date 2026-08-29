// Odyssey.CreatureAnim.cs - creature display-name sync and applying a peer's
// one-shot animation (fire-and-forget/overlay/looping/walking/running).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- creature naming ---------------------------------------------------------

    /// <summary>CSWSCreatureStats + this = first_name, a CExoLocString (symbol database).
    /// CSWSCreature::GetFirstName (0x004F8310) returns exactly this address.</summary>
    private const int StatsFirstName = 0x34;

    // CExoLocString::ClearLocString: empties the localized string in place.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClearLocStringFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E9EA0;

    // CExoLocString::AddString(int nID, CExoString* text, int gender). Verified against
    // the engine's own rename flow at 0x00521B4C, which calls AddString(0, &str, 0) on a
    // fresh CExoLocString and assigns it onto stats+0x34; operating on the field in place
    // skips the temp. The text is copied by the engine, so a stack-local source is fine.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, ExoString*, int, void> LocStringAddStringFn =
        (delegate* unmanaged[Thiscall]<nint, int, ExoString*, int, void>)0x005EA340;

    /// <summary>CSWSCreature + this = client twin CSWCObject*, 0 until the twin is made.</summary>
    private const int CreClientTwin = 0x224;

    /// <summary>True once the engine has created this creature's render-side twin, which is
    /// what carries the name to the selection band.</summary>
    public static bool HasClientTwin(nint creature) =>
        creature != 0 && *(nint*)(creature + CreClientTwin) != 0;

    /// <summary>A creature's render-side twin, or 0.</summary>
    public static nint GetClientTwin(nint creature) =>
        creature == 0 ? 0 : *(nint*)(creature + CreClientTwin);

    /// <summary>
    /// The LOCAL player's client twin, or 0. Used by the animation hook to tell the player's
    /// own one-shots apart from every other creature's — the hook fires for every swing in
    /// the area, so this must be cheap and must never call into the engine.
    /// </summary>
    public static nint GetLocalPlayerTwin()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint player = GetPlayerCreatureFn(server);
        return player == 0 ? 0 : *(nint*)(player + CreClientTwin);
    }

    // ---- applying a peer's one-shot animation -------------------------------------

    /// <summary>CSWCCreature vtable +0xA0 = AddFireForgetAnimation(uint anim, void* options).
    /// The options pointer may be null; the engine then derives the duration from the
    /// animation's own length divided by the twin's animation speed.</summary>
    private const int TwinAddFireForgetSlot = 0xA0;

    /// <summary>
    /// <c>CSWCAnimBase::GetAnimationName(u16 id)</c>, animbase vtable +0x40
    /// (0x0069E620, db-confirmed) — the function <c>SetAnimationInternal</c> (animbase
    /// vtable +0xF0, 0x0069E8D0) calls to turn a requested id into the model-row NAME it
    /// actually plays (docs/research/phantom-locomotion.md §10.2). Returns the resolved
    /// CExoString by value through a hidden first stack argument: prologue takes
    /// <c>this</c> in ecx, <c>[ebp+8]</c> = output CExoString*, <c>[ebp+0xc]</c> = the id as
    /// a zero-extended dword; epilogue is <c>ret 8</c>, i.e. exactly those two stack args.
    /// <see cref="ReadAnimationName"/> wraps the ABI.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, ushort, nint> GetAnimationNameFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, ushort, nint>)0x0069E620;

    /// <summary>
    /// Calls the real engine name-resolver and reads back what it constructed,
    /// then frees it (<see cref="CExoStringDtorFn"/>, the same destructor
    /// <see cref="SetControlText"/> already uses, and the exact address (0x005E5C20)
    /// <c>GetAnimationName</c>'s own tail calls to destruct its internal temporaries) so this
    /// can be called repeatedly from a live diagnostic without leaking.
    ///
    /// Cut at the first null rather than trusted-length, per the CExoString length trap (the
    /// stored length includes the terminating null — see BarkHook.cs/CreatureFinder.cs) — and
    /// belt-and-suspenders here anyway, since an unresolved id is expected to come back empty.
    ///
    /// MAIN THREAD ONLY (it can allocate/free through the engine's own string routines).
    /// </summary>
    private static string ReadAnimationName(nint animBase, ushort id, out int rawLength)
    {
        byte* buf = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            buf[i] = 0;
        }

        GetAnimationNameFn(animBase, buf, id);

        byte* chars = *(byte**)buf;
        int length = *(int*)(buf + 4);
        rawLength = length;

        string name = "";
        if (chars != null && length > 0)
        {
            int cap = Math.Min(length, 63);
            int n = 0;
            while (n < cap && chars[n] != 0)
            {
                n++;
            }

            name = n > 0 ? System.Text.Encoding.ASCII.GetString(chars, n) : "";
        }

        CExoStringDtorFn(buf);
        return name;
    }

    /// <summary>CSWCAnimBase + this = the model's own resref (CResRef, fixed char[16],
    /// zero-filled — NOT a CExoString). tools/kotor1_0_3.db: CSWCAnimBase.name, offset 44.
    /// Read for context alongside <see cref="ReadAnimationName"/> — which model
    /// this animbase's classifiers/name table are actually keyed to.</summary>
    private const int AnimBaseModelName = 0x2C;

    /// <summary>Reads a fixed CResRef (char[16], zero-filled, not a CExoString) as ASCII,
    /// cutting at the first null or 16 bytes, whichever comes first.</summary>
    private static string ReadResRef(nint address)
    {
        byte* chars = (byte*)address;
        int n = 0;
        while (n < 16 && chars[n] != 0)
        {
            n++;
        }

        return n > 0 ? System.Text.Encoding.ASCII.GetString(chars, n) : "";
    }

    /// <summary>CSWSCreature + this = appearance.2da row (ushort), inside the embedded
    /// CSWSCreatureAppearanceInfo at +0xA50 (tools/kotor1_0_3.db:
    /// CSWSCreatureAppearanceInfo.appearance_type, offset 16 -> 0xA50+0x10 = 0xA60). The
    /// same field <see cref="PhantomController"/>'s appearance poke writes.</summary>
    private const int CreServerAppearanceRow = 0xA60;

    /// <summary>
    /// CSWCCreature + this = the CLIENT-side cached "desired appearance" row
    /// (ushort). <c>CSWCCreature::ForceAppearanceUpdate</c> (0x00614A30, thiscall, no stack
    /// args — db-confirmed) reads exactly this field, writes it into
    /// <c>this-&gt;appearance-&gt;appearance_info.appearance_id</c> (CSWCCreatureAppearance
    /// +0x18, matching tools/kotor1_0_3.db's CCAppearanceInfo.appearance_id at offset 24
    /// embedded at CSWCCreatureAppearance offset 0), and then calls
    /// <c>CSWCCreature::SetAppearance</c> (0x006134C0, one of ForceAppearanceUpdate's own 7
    /// live call sites) — which itself calls
    /// <c>CSWCObject::GetAnimBase</c> (0x0063C070, the same <c>[ecx+0x68]</c> accessor
    /// <see cref="TwinAnimBase"/> uses) and the AnimationLooping/AnimationStationary
    /// classifiers, i.e. it touches the animbase, not just server-side bookkeeping.
    ///
    /// NEEDS-LIVE: whether writing this field then calling ForceAppearanceUpdate actually
    /// reloads the twin's model/animbase name table (the X2 hypothesis) was not confirmed by
    /// static disassembly alone — SetAppearance's own body was not fully traced past its
    /// animbase-touching calls. See phantom-locomotion.md §11 for the live result.
    /// </summary>
    private const int TwinDesiredAppearanceRow = 0x38C;

    /// <summary>CSWCCreature::ForceAppearanceUpdate(this) — thiscall, no stack
    /// args (db-confirmed param_size_bytes=0). See <see cref="TwinDesiredAppearanceRow"/>'s
    /// doc for the call chain this reaches.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ForceAppearanceUpdateFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00614A30;

    /// <summary>
    /// Rebuilds the client twin's render-side appearance/model — and, by
    /// construction (<see cref="TwinDesiredAppearanceRow"/>'s doc), its animbase's model name
    /// table — after a phantom's appearance row is poked directly. <see cref="RebuildAppearance"/>
    /// (server-side, <c>UpdateAppearanceDependantInfo</c>) already ran; this is the CLIENT-side
    /// counterpart the engine's own network appearance-change handler would have triggered, which
    /// nothing in this codebase called before this. MAIN THREAD ONLY.
    ///
    /// ⚠ Returns FALSE when there is no client twin yet — which is the ordinary case at spawn
    /// time, since the engine builds the twin a frame or more after the server object exists.
    /// The caller has to keep asking until it returns true, or the twin renders whatever the
    /// blueprint gave it forever: nothing else in the engine reloads it, and a server-side
    /// field that already reads correct gives a "has it been applied" check the wrong answer.
    /// <see cref="PeerIdentityRule.ShouldApply"/> is that latch for the phantom.
    /// </summary>
    public static bool RebuildClientAppearance(nint creature)
    {
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != ClientCreatureVtable)
        {
            return false;
        }

        ushort row = *(ushort*)(creature + CreServerAppearanceRow);
        *(ushort*)(twin + TwinDesiredAppearanceRow) = row;
        ForceAppearanceUpdateFn(twin);
        return true;
    }

    /// <summary>The appearance row the CLIENT twin is currently built for — what actually
    /// renders — or false when the body has no twin yet. Diagnostic only: the server row
    /// (<c>creature + 0xA60</c>) and this one disagreeing IS the wrong-look bug, so a log line
    /// that carries only one of them cannot tell them apart. MAIN THREAD ONLY.</summary>
    public static bool TryGetTwinAppearanceRow(nint creature, out ushort row)
    {
        row = 0;
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != ClientCreatureVtable)
        {
            return false;
        }

        row = *(ushort*)(twin + TwinDesiredAppearanceRow);
        return true;
    }

    /// <summary>CSWCAnimBase + this = Gob* (the render/scene node this animbase drives).
    /// Null until the model has finished loading. tools/kotor1_0_3.db: CSWCAnimBase.gob,
    /// offset 184 (0xB8). <see cref="AnimationExistsFn"/> refuses everything while this is
    /// null.</summary>
    private const int AnimBaseGob = 0xB8;

    private static int _animationClassLogged;

    /// <summary>Reports the first few unclassified ids. All three predicates are data-driven
    /// (they resolve the id through the model's own animbase and then look the row up in
    /// animations.2da), so which class an id falls into is a property of the CREATURE, not a
    /// constant — worth being able to see rather than assume.</summary>
    private static void LogAnimationClassOnce(string message)
    {
        if (_animationClassLogged < 8)
        {
            _animationClassLogged++;
            PluginHost.Log(message);
        }
    }

    /// <summary>CSWCObject + this = its CSWCAnimBase (the engine's own accessor at 0x0063C070
    /// is exactly <c>mov eax,[ecx+0x68]; ret</c>).</summary>
    private const int TwinAnimBase = 0x68;

    /// <summary>CSWCAnimBase vtable +0xE0 = ServerToClientAnimation(u16 anim, int) — maps an
    /// animation id onto the model's own row. <c>ret 4</c>: it consumes only the id, leaving
    /// the second pushed argument for the setter below, which is how the engine's own
    /// flourish arm at 0x005F4681 shares one <c>push 1</c> between the two calls.</summary>
    private const int AnimBaseServerToClientSlot = 0xE0;

    /// <summary>CSWCAnimBase vtable +0x44 = SetOverlayAnimation_2(row, int). <c>ret 8</c>.
    /// Overridden per animbase class; on a creature it is an adjustor thunk, so it is always
    /// called through the slot. See <see cref="PlayOverlayAnimation"/>.</summary>
    private const int AnimBaseSetOverlaySlot = 0x44;

    /// <summary>The +0xE0 implementation every creature-family animbase shares, and the one
    /// thing that tells a creature's animbase apart from a door's or a placeable's.</summary>
    private const nint AnimBaseServerToClientFn = 0x0069F650;

    /// <summary>
    /// Plays a peer's one-shot animation (a weapon draw, an attack swing, a flourish, an
    /// injected stim) on a phantom, through the engine's own client-side call.
    ///
    /// A raw field write cannot do this job: the engine's own object-update handler drops
    /// repeats and explicitly skips the entire combat animation set, and overlays have no
    /// field at all. See docs/research/animation-and-minimap.md.
    ///
    /// Three animation classes, three different calls, and using the wrong one is not a
    /// no-op: a looping or overlay id pushed down the fire-and-forget path parks in the
    /// twin's current-animation field with no expiry and wedges the body. So the engine's
    /// own predicates decide, and anything they both refuse is dropped.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PlayOneShotAnimation(nint creature, ushort animation)
    {
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != ClientCreatureVtable)
        {
            return false;
        }

        if (AnimationFireAndForgetFn(twin, animation) == 0)
        {
            // The moving weapon flourish (0x27AD) lands here: an OVERLAY, which plays over
            // whatever the body is already doing and exists only inside the animbase.
            // Measured live on a phantom: 0x27AD reads overlay 1, looping 0.
            if (AnimationOverlayFn(twin, animation) != 0 && AnimationLoopingFn(twin, animation) == 0)
            {
                return PlayOverlayAnimation(twin, animation);
            }

            LogAnimationClassOnce($"animation: 0x{animation:X} is in no playable class; dropped");
            return false;
        }

        nint vtable = *(nint*)twin;
        var addFireForget =
            (delegate* unmanaged[Thiscall]<nint, uint, nint, void>)(*(nint*)(vtable + TwinAddFireForgetSlot));

        // The engine reaches InsertFireForgetAnimation from inside this call; without the
        // suppression the mirrored animation would be captured as if this creature had
        // played it itself and echo back to its owner.
        AnimationHook.Suppress = true;
        try
        {
            addFireForget(twin, animation, 0);
        }
        finally
        {
            AnimationHook.Suppress = false;
        }

        return true;
    }

    /// <summary>
    /// Applies an OVERLAY animation to a twin, exactly as the engine's own moving flourish
    /// does at 0x005F4681: resolve the animbase, map the id to a model row, set it as the
    /// overlay.
    ///
    /// ⚠ BOTH CALLS GO THROUGH THE VTABLE, and that is not a style choice. Measured live on
    /// a phantom, a creature's animbase carries vtable <c>0x00754AE8</c>, whose +0x44 is an
    /// ADJUSTOR THUNK (<c>sub ecx,[ecx-4]; jmp 0x00699420</c>, the CSWCAnimBaseHeadWield
    /// override) rather than <c>CSWCAnimBase::SetOverlayAnimation_2 0x006A03C0</c>. That
    /// pointer is a secondary base, so calling the base implementation directly would hand
    /// it an unadjusted <c>this</c>. The engine calls the slot; so do we. (The first version
    /// of this code required the slot to equal 0x006A03C0 and refused every real creature —
    /// which is how the thunk was found, and an argument for guards that log.)
    ///
    /// The guard that remains is the +0xE0 slot's identity. Every creature-family animbase
    /// shares <c>CSWCAnimBase::ServerToClientAnimation 0x0069F650</c>; doors, placeables,
    /// triggers, projectiles and visual effects each override it. So the slot matching is a
    /// positive statement that this really is a creature's animbase before we call the other
    /// slot, whatever that turns out to be.
    /// </summary>
    private static bool PlayOverlayAnimation(nint twin, ushort animation)
    {
        nint animBase = *(nint*)(twin + TwinAnimBase);
        if (!LooksLikeHeapPointer(animBase))
        {
            LogAnimationClassOnce($"animation: no animbase on twin 0x{twin:X} (0x{animBase:X})");
            return false;
        }

        nint vtable = *(nint*)animBase;
        if (!LooksLikeCodePointer(vtable))
        {
            LogAnimationClassOnce($"animation: animbase 0x{animBase:X} has no vtable (0x{vtable:X})");
            return false;
        }

        nint serverToClient = *(nint*)(vtable + AnimBaseServerToClientSlot);
        nint setOverlay = *(nint*)(vtable + AnimBaseSetOverlaySlot);

        if (serverToClient != AnimBaseServerToClientFn || !LooksLikeCodePointer(setOverlay))
        {
            LogAnimationClassOnce($"animation: animbase vtable 0x{vtable:X} is not a creature's"
                + $" (+0xE0 0x{serverToClient:X}, +0x44 0x{setOverlay:X})");
            return false;
        }

        int row = ((delegate* unmanaged[Thiscall]<nint, ushort, int>)serverToClient)(animBase, animation);
        ((delegate* unmanaged[Thiscall]<nint, int, int, void>)setOverlay)(animBase, row, 1);
        return true;
    }

    /// <summary>CSWGuiMainInterface + this = the minimap viewport's width and height. DrawMap
    /// passes the first as BOTH dimensions to AurGUISetupViewport (0x0068AC77), which is what
    /// actually clips, so the width is the honest bound on each axis.</summary>
    private const int MainInterfaceViewportW = 0x6088;

    private const int MainInterfaceViewportH = 0x608C;

    /// <summary>How faded an off-map peer's marker is, so "somewhere that way" cannot be
    /// mistaken for "right here".</summary>
    private const float OffMapAlpha = 0.55f;

    /// <summary>
    /// Holds a marker inside the minimap's viewport, returning true when it had to move —
    /// which is the same thing as "this peer is off the map".
    ///
    /// The icon's extent is in viewport space (measured live: a 32×32 icon at (44,44) inside a
    /// 120×120 viewport, i.e. dead centre), so the bound is just the viewport inset by the
    /// icon, and clamping each axis independently keeps the marker on the border nearest the
    /// peer's direction.
    /// </summary>
    private static bool ClampToViewport(nint mainInterface, int iconW, int iconH, ref int x, ref int y)
    {
        int width = *(int*)(mainInterface + MainInterfaceViewportW);
        int height = *(int*)(mainInterface + MainInterfaceViewportH);

        // A viewport smaller than the icon (or an unread field) leaves the marker alone
        // rather than pinning everything to 0,0.
        if (width <= iconW || height <= iconH || iconW <= 0 || iconH <= 0)
        {
            return false;
        }

        int clampedX = Math.Clamp(x, 0, width - iconW);
        int clampedY = Math.Clamp(y, 0, height - iconH);

        bool moved = clampedX != x || clampedY != y;
        x = clampedX;
        y = clampedY;
        return moved;
    }

    /// <summary>Range check for a vtable pointer: inside the image, where .rdata lives.</summary>
    internal static bool LooksLikeCodePointer(nint p) => p is > 0x400000 and < 0x8C3000;

    /// <summary>The CSWCCreature vtable — the guard that a twin is really a creature twin
    /// before its GetName-side name fields are written.</summary>
    private const nint ClientCreatureVtable = 0x0074F5F8;

    // CSWCCreature::SetName(CExoString* first, CExoString* last). This is what the
    // selection band actually renders (via GetName, vtable +0x70). Both strings are copied
    // with the engine allocator, so stack locals are fine; the last name must be a non-null
    // empty string, not a null pointer.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, ExoString*, void> ClientSetNameFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, ExoString*, void>)0x0060D600;

    /// <summary>
    /// Sets a creature's first name — the selection band above its head — on BOTH the
    /// server locstring and the client twin.
    ///
    /// The twin write is the load-bearing half, and it is why the earlier "AddString ran
    /// but nothing showed" was a mystery: the band renders from a plain CExoString cached
    /// on the client twin, and the engine's per-frame appearance diff has no "name changed"
    /// bit — a name reaches the twin exactly once, in the object's first update. Writing
    /// only the server locstring after that update never reaches the band. The server half
    /// still matters (saves, feedback text, GetFullName, the combat log read it), so both
    /// are written.
    ///
    /// Idempotent — re-assert it after a module transition (the twin is recreated) and
    /// whenever the twin first appears. MAIN THREAD ONLY.
    /// </summary>
    public static bool SetCreatureFirstName(nint creature, byte* nameAscii, int length)
    {
        if (creature == 0 || length <= 0)
        {
            return false;
        }

        nint stats = Deref(creature + CreStats);
        if (stats == 0)
        {
            return false;
        }

        // Length is capacity (strlen + 1), matching what the engine's own CExoString
        // constructor from char* stores. nameAscii must be null-terminated.
        ExoString text = new() { CString = (nint)nameAscii, Length = (uint)(length + 1) };

        // Server half: the localized string GetFullName and the save writer read.
        nint locString = stats + StatsFirstName;
        ClearLocStringFn(locString);
        LocStringAddStringFn(locString, 0, &text, 0);

        // Client half: the plain string the band renders. Skipped safely until the twin
        // exists — the first GameObjUpdate then carries the name from the server locstring
        // just written.
        nint twin = *(nint*)(creature + CreClientTwin);
        if (twin != 0 && *(nint*)twin == ClientCreatureVtable)
        {
            byte emptyByte = 0;
            ExoString empty = new() { CString = (nint)(&emptyByte), Length = 1 };
            ClientSetNameFn(twin, &text, &empty);
        }

        return true;
    }

}
