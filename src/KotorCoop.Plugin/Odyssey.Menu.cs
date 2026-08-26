// Odyssey.Menu.cs - module identity/transition helpers, the main-menu Co-op button,
// and forcing BTN_WARP visible at runtime.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- module identity and transitions ----------------------------------------

    /// <summary>CServerExoAppInternal + this = CServerInfo* (symbol database: server_info).</summary>
    private const int ServerInfoOffset = 0x10004;

    /// <summary>CServerExoAppInternal + this = server mode; 2 while a module is running.</summary>
    private const int ServerModeOffset = 0x10008;

    /// <summary>CServerExoAppInternal + this = nonzero while a module load is in progress.</summary>
    private const int LoadingModuleOffset = 0x100B4;

    /// <summary>CServerInfo + this = module name, CExoString (symbol database: module_name).</summary>
    private const int ServerInfoModuleName = 0xFC;

    /// <summary>
    /// CSWSArea + this = trans_pending (symbol database), a byte that goes 1 roughly half a
    /// second before a door/trigger transition stores the area. That store serialises every
    /// live object — including anything this plugin spawned — into the saved module, so it
    /// is the window in which phantoms and replicated items must be destroyed.
    /// </summary>
    private const int AreaTransPending = 0x2C4;

    private const int ModuleAreaOffset = 0x220;

    /// <summary>The player's CSWSCreature, resolved through the engine's own lookup, or 0.</summary>
    public static nint GetPlayerCreaturePointer()
    {
        nint server = GetServer();
        return server == 0 ? 0 : GetPlayerCreatureFn(server);
    }

    /// <summary>
    /// The creature CLIENT INPUT currently drives — CSWParty member[0]
    /// (client-control-lock.md's control gate: <c>[[[0x7A39FC]+4]+0x270]+0x24</c>), which is
    /// NOT what <see cref="GetPlayerCreaturePointer"/> answers: that resolves a FIXED
    /// connected-players slot (the real PC, or nothing) and never moves with Tab/`leader`/
    /// <c>ChangeCharacter</c>. This function instead reads the field that DOES move — the
    /// same one <see cref="SwitchControlToPartySlot"/>/<see cref="SwitchControlToPc"/> locate a
    /// member index against — resolved through the same generic resolver every other id read
    /// in this file uses (<see cref="ResolveServerObject"/>). See docs/research/control-return.md.
    /// </summary>
    public static nint GetControlledCreaturePointer()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return 0;
        }

        uint id = *(uint*)(party + SwPartyMember0ClientId) & ~ControlPartyNpcFlag;
        return id == 0 ? 0 : ResolveServerObject(id);
    }

    /// <summary>
    /// Identifies the currently loaded world. Returns false while there is no world or a
    /// module transition is mid-flight — every cached object pointer and object id is (or
    /// is about to be) invalid, and nothing should run.
    ///
    /// The epoch combines the server object array pointer, which UnloadModule replaces,
    /// with a hash of the module name string. Area object ids are deliberately not used:
    /// they come from a counter that resets on transition, so different modules routinely
    /// share one.
    ///
    /// ⚠ Roadmap item 14 phase 2 (docs/research/session-save.md) leans on one more fact about
    /// this value: a plain SAVE does not change it. Nothing about writing the current world to
    /// disk calls UnloadModule or replaces the object array — the epoch is a LIVE-WORLD
    /// identity, not a save-file one — so a save-only edge and a genuine module reload (either
    /// an ordinary transition or a Load Game action) are exactly the two cases this function
    /// already distinguishes by construction: unchanged epoch versus a transition through
    /// "false" and back. INFERRED from the disassembly this doc comment already cites
    /// (UnloadModule/GetObjectArray), not measured against a real in-game save — see
    /// WorldLoadRule.cs, which is where PluginHost turns this into the further distinction
    /// between an ordinary transition and an unannounced load.
    /// </summary>
    public static bool TryGetWorldEpoch(out ulong epoch)
    {
        epoch = 0;

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            return false;
        }

        // ⚠ The mode is a WORD, not a dword: the engine itself uses
        // `cmp word ptr [esi+0x10008], 1` (0x4B6294) and `mov word ptr [...], 2` (0x4B62B0).
        // Reading 32 bits picks up the adjacent field, and whether that happens to be zero
        // depends on how the module was loaded — measured live, a click-loaded world read
        // 0x00000002 while a plugin-loaded one read 0x555FFC02, so the world gate silently
        // failed (and the agent's identical mistake made every module read fall back to
        // "area{id}", which in turn hid peers behind the module filter).
        if (*(int*)(internalServer + LoadingModuleOffset) != 0
            || *(ushort*)(internalServer + ServerModeOffset) != 2)
        {
            return false;
        }

        nint objectArray = GetObjectArrayFn(server);
        if (objectArray == 0)
        {
            return false;
        }

        uint nameHash = 2166136261;
        nint serverInfo = Deref(internalServer + ServerInfoOffset);
        if (serverInfo != 0)
        {
            nint text = Deref(serverInfo + ServerInfoModuleName);
            uint length = *(uint*)(serverInfo + ServerInfoModuleName + 4);
            if (text != 0 && length is > 0 and <= 32)
            {
                for (uint i = 0; i < length; i++)
                {
                    nameHash = (nameHash ^ *(byte*)(text + (nint)i)) * 16777619;
                }
            }
        }

        epoch = ((ulong)(uint)objectArray << 32) | nameHash;
        return true;
    }

    /// <summary>
    /// True when the player's area has flagged an imminent transition (trans_pending).
    /// This is the pre-store flush window: StoreCurrentModule will serialise the area on
    /// the way out, and SaveCreatures only skips party members, so anything the plugin
    /// spawned would come back as a permanent NPC on a revisit — and shift the id counter
    /// for the rest of the playthrough.
    /// </summary>
    public static bool IsAreaTransitionPending()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint module = GetModuleFn(server);
        if (module == 0)
        {
            return false;
        }

        nint area = Deref(module + ModuleAreaOffset);
        return area != 0 && *(byte*)(area + AreaTransPending) != 0;
    }

    // ---- main-menu Co-op button --------------------------------------------------
    //
    // The dormant BTN_WARP on the main menu is rebound to start co-op. See
    // docs/research/connect-flow.md. Guarded engine-call code, on by default now that it is
    // live-verified; coop.ini menu_button=0 opts out (read plugin-side, no agent needed).

    /// <summary>CClientExoAppInternal + this = CSWGuiMainMenu*, null outside the menu.</summary>
    private const int ClientInternalMainMenuOffset = 0x280;

    /// <summary>CSWGuiMainMenu + this = the BTN_WARP control pointer (dev-only, blank text).</summary>
    private const int MainMenuWarpButton = 0x1248;

    /// <summary>GUI input events: 0x27 = mouse click, 0x2D = A / Enter.</summary>
    public const int GuiEventClick = 0x27;
    public const int GuiEventAccept = 0x2D;

    // CSWGuiControl::AddEvent(this, int eventId, void* owner, void* fn). Same eventId
    // replaces; a null fn removes. thiscall, ret 0xC.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, nint, void> AddEventFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, nint, void>)0x0041AB20;

    /// <summary>The main-menu object, or 0 when not on the menu.</summary>
    public static nint GetMainMenu()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalClient = Deref(client + AppClientOffset);
        return internalClient == 0 ? 0 : *(nint*)(internalClient + ClientInternalMainMenuOffset);
    }

    /// <summary>
    /// Binds a callback to the main menu's dormant Co-op (BTN_WARP) control for both the
    /// click and accept events. The callback is a thiscall stub the engine invokes as
    /// <c>fn(owner /*ecx*/, firingControl /*stack*/)</c>. Returns false (harmlessly) if the
    /// menu or the control is not resolvable.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool BindCoopMenuButton(nint owner, nint callback)
    {
        nint menu = GetMainMenu();
        if (menu == 0)
        {
            return false;
        }

        nint button = *(nint*)(menu + MainMenuWarpButton);

        // Sanity-guard the control pointer before calling into it: a plausible heap
        // pointer, 4-aligned, whose vtable slot is also a plausible pointer. A wrong offset
        // would otherwise call AddEvent on garbage.
        if (!LooksLikeHeapPointer(button) || !LooksLikeHeapPointer(*(nint*)button))
        {
            return false;
        }

        AddEventFn(button, GuiEventClick, owner, callback);
        AddEventFn(button, GuiEventAccept, owner, callback);
        return true;
    }

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    // ---- forcing BTN_WARP visible at runtime -------------------------------------
    //
    // The retail menu constructor builds BTN_WARP, lays it out from the .gui (position + "Warp"
    // text + adds it to the draw list), then DELIBERATELY re-hides it: at 0x0067C669 it clears
    // bit1 (0x2) of control+0x44. That one bit gates BOTH drawing (CSWGuiPanel::Draw 0x0040B841
    // `test [ctl+0x44],2`) and click hit-testing (CSWGuiControl::HitCheckMouse 0x004187C5). So a
    // .gui Override can never surface it — but writing bit1 back each frame makes it draw AND
    // clickable in one store, with no code patch. See docs/research/menu-button-runtime.md.

    /// <summary>CSWGuiControl flags dword: bit1 0x2 = visible+hittable, bit5 0x20 = locked.</summary>
    private const int ControlFlagsOffset = 0x44;
    private const uint ControlFlagVisible = 0x2;
    private const uint ControlFlagLocked = 0x20;

    // The label lives in a CSWGuiText at button+0x154; its CSWGuiTextParams is at +0x18 -> +0x16C.
    private const int ButtonTextParamsOffset = 0x16C;

    // CSWGuiTextParams::SetText(this, CExoString* src) — sets the literal and str_ref = -1.
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, void> TextParamsSetTextFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, void>)0x00415E00;

    /// <summary>CSWGuiButton vtable — identifies the embedded control and guards every write.</summary>
    private const nint ButtonVtable = 0x0073E658;

    /// <summary>
    /// The main-menu Co-op control (the dormant BTN_WARP), or 0 when not on the menu.
    ///
    /// ⚠ BTN_WARP is EMBEDDED in CSWGuiMainMenu, not pointed to: the control object *begins* at
    /// <c>mainMenu + 0x1248</c>, whose first dword is its vtable. Dereferencing that offset (as
    /// this code originally did) yields the vtable CONSTANT 0x0073E658, which passes a naive
    /// heap-pointer guard and sends every subsequent write into the executable's static data —
    /// that, not any "uninitialised control", is what crashed the menu. Verified live: at the
    /// correct address the object has the CSWGuiButton vtable and its real .gui extent, and the
    /// visible-bit write is stable.
    /// </summary>
    public static nint GetCoopMenuButton()
    {
        nint menu = GetMainMenu();
        if (menu == 0)
        {
            return 0;
        }

        // The control is AT this address. Confirm by its vtable rather than a range check, so a
        // layout shift can never let us scribble somewhere arbitrary.
        nint button = menu + MainMenuWarpButton;
        return LooksLikeHeapPointer(button) && *(nint*)button == ButtonVtable ? button : 0;
    }

    /// <summary>Un-hides a control: sets the visible/hittable bit and clears the lock bit. Must
    /// be re-applied each frame because a menu rebuild re-runs the ctor's hide. MAIN THREAD ONLY.</summary>
    public static void SetControlVisible(nint control)
    {
        uint* flags = (uint*)(control + ControlFlagsOffset);
        *flags = (*flags | ControlFlagVisible) & ~ControlFlagLocked;
    }

    /// <summary>Sets a button control's on-screen label via the engine's own SetText (which
    /// refreshes the linked text object and pins str_ref to -1 so the literal wins). Builds a
    /// temporary CExoString on the stack and frees it. Idempotent. MAIN THREAD ONLY.</summary>
    public static void SetControlText(nint control, string ascii)
    {
        byte* s = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            s[i] = 0;
        }

        int n = ascii.Length;
        byte* buf = stackalloc byte[64];
        int count = n < 63 ? n : 63;
        for (int i = 0; i < count; i++)
        {
            buf[i] = (byte)ascii[i];
        }

        buf[count] = 0;

        CExoStringCtorFn(s, buf);
        TextParamsSetTextFn(control + ButtonTextParamsOffset, s);
        CExoStringDtorFn(s);
    }

    /// <summary>Binds the plugin's click/accept sink onto a resolved control (same-id AddEvent
    /// replaces). Separated from <see cref="BindCoopMenuButton"/> so the caller can reuse a
    /// control it already resolved. MAIN THREAD ONLY.</summary>
    public static void BindControlClick(nint control, nint owner, nint callback)
    {
        AddEventFn(control, GuiEventClick, owner, callback);
        AddEventFn(control, GuiEventAccept, owner, callback);
    }

    /// <summary>Binds ONLY the accept (Enter/A) event — used on browser listbox rows, where a
    /// click must stay selection-only (the listbox forwards the click event to the selected
    /// row, so a click binding would connect on single click). MAIN THREAD ONLY.</summary>
    public static void BindControlAccept(nint control, nint owner, nint callback) =>
        AddEventFn(control, GuiEventAccept, owner, callback);

}
