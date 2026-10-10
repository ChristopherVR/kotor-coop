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
    /// (client-control-lock.md's control gate: <c>[[[0x7A39FC]+4]+0x270]+0x24</c>). This is
    /// the field a control switch reorders; <see cref="GetPlayerCreaturePointer"/> instead
    /// resolves the server's connected-player entry (<c>CSWSPlayer+0x28</c>), which a REAL
    /// engine switch (<c>CSWParty::SetLeader</c> → <c>CSWSPlayer::SetGameObject</c>) updates
    /// alongside the array — live-confirmed 2026-09-02 when the death auto-swap moved it to a
    /// companion — and which this plugin's own switches now keep in step too
    /// (<see cref="SyncServerPlayerToLeader"/>). Between a rotation and its sync, or after any
    /// raw member[0] write, the two can disagree; this one is the input truth. See
    /// docs/research/control-return.md and death-handling.md §6.
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

    /// <summary>The bare numeric id behind <see cref="GetControlledCreaturePointer"/> — CSWParty
    /// (client-side) member[0]'s own id, with the party-npc flag bit masked, WITHOUT resolving it
    /// to a pointer. 0 if there is no client-side party yet.
    ///
    /// Added 2026-09-10 (round 3) as a HYPOTHESISED fallback for <see cref="GetPlayerObjectId"/>
    /// (<c>CServerExoApp::GetPlayerCreature</c>) during a first-time `join_mode = newcharacter`
    /// joiner's equip-repair window, where that accessor is LIVE-CONFIRMED to read the engine's
    /// own <c>0x7FFFFFFF</c> "no real player yet" placeholder for the WHOLE window, every poll
    /// (there is no prior leader to switch FROM, so nothing ever calls
    /// <c>CSWSPlayer::SetGameObject</c> — see <see cref="SyncServerPlayerToLeader"/>'s own doc
    /// comment for the real callers). The reasoning was: this field is driven by CLIENT-side
    /// input authority, not the server-side wiring the placeholder above depends on, so it should
    /// be independent.
    ///
    /// ⚠ LIVE-FALSIFIED, twice, same round: this reads the SAME class of placeholder
    /// (<c>0x7FFFFFFF</c>, or <c>0x7F000000</c> — <c>OBJECT_INVALID</c> — before any client-side
    /// party exists yet) for the entire repair window too. A THIRD independent read — the
    /// chargen-time creature pointer's own id field, taken directly in
    /// <c>CharGenDriveController.Accept()</c> before <c>BeginPlayGame</c> even runs — was also
    /// tried and also came back <c>0x7F000000</c>. So this accessor is NOT currently used as the
    /// repair id (<c>NewCharacterCaptureController</c> still calls
    /// <see cref="GetPlayerObjectId"/> only); it is kept as a logged cross-check for a future
    /// pass. See docs/research/world-adoption.md's "Round 2026-09-10d" for the full timing
    /// evidence and the still-open question of whether this creature ever gets a real numeric id
    /// inside the original chargen module at all.
    /// </summary>
    public static uint GetControlledCreatureObjectId()
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

        return *(uint*)(party + SwPartyMember0ClientId) & ~ControlPartyNpcFlag;
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

        if (!NativeMethods.IsReadable(menu + MainMenuWarpButton, (nuint)sizeof(nint)))
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
        // The menu object is freed when character creation replaces it, and the client keeps
        // returning its old address for a frame or more: a plausible-looking pointer is not a
        // readable one (a joiner's first-join `newgame` crashed here every time).
        nint button = menu + MainMenuWarpButton;
        return LooksLikeHeapPointer(button)
            && NativeMethods.IsReadable(button, (nuint)sizeof(nint))
            && *(nint*)button == ButtonVtable ? button : 0;
    }

    /// <summary>Un-hides a control: sets the visible/hittable bit and clears the lock bit. Must
    /// be re-applied each frame because a menu rebuild re-runs the ctor's hide. MAIN THREAD ONLY.</summary>
    public static void SetControlVisible(nint control)
    {
        uint* flags = (uint*)(control + ControlFlagsOffset);
        *flags = (*flags | ControlFlagVisible) & ~ControlFlagLocked;
    }

    // ---- lining the Co-op button up with the menu's own buttons -------------------
    //
    // CSWGuiExtent sits at control+0x04 (left, top, width, height, screen pixels). The menu's
    // buttons are embedded in CSWGuiMainMenu like BTN_WARP, each starting with the CSWGuiButton
    // vtable, so scanning the menu object for that vtable finds every one of them without
    // knowing their offsets (docs/research/menu-button-runtime.md, "Extent").

    private const int ControlExtentOffset = 0x04;

    /// <summary>How far into CSWGuiMainMenu to look for embedded buttons: past BTN_WARP, the
    /// last control the .gui lays out.</summary>
    private const int MainMenuScanBytes = MainMenuWarpButton + 0x200;

    // CSWGuiButton::SetExtent(this, CSWGuiExtent* src), ret 4: copies the rect and re-lays the
    // button's border and label off it. VERIFIED (menu-button-runtime.md).
    private static readonly delegate* unmanaged[Thiscall]<nint, int*, void> ButtonSetExtentFn =
        (delegate* unmanaged[Thiscall]<nint, int*, void>)0x00417A20;

    /// <summary>A control's on-screen rectangle.</summary>
    public static MenuColumnRule.Extent GetControlExtent(nint control)
    {
        int* e = (int*)(control + ControlExtentOffset);
        return new MenuColumnRule.Extent(e[0], e[1], e[2], e[3]);
    }

    /// <summary>
    /// The rectangles of the main menu's visible buttons other than <paramref name="except"/>
    /// (the Co-op button itself), found by their vtable inside the menu object. Empty when the
    /// menu cannot be read. MAIN THREAD ONLY.
    /// </summary>
    public static List<MenuColumnRule.Extent> GetMainMenuButtonExtents(nint except)
    {
        List<MenuColumnRule.Extent> found = [];
        nint menu = GetMainMenu();
        if (menu == 0 || !NativeMethods.IsReadable(menu, (nuint)MainMenuScanBytes))
        {
            return found;
        }

        for (int offset = 0; offset + ControlFlagsOffset + 4 <= MainMenuScanBytes; offset += 4)
        {
            nint control = menu + offset;
            if (control == except || *(nint*)control != ButtonVtable)
            {
                continue;
            }

            if ((*(uint*)(control + ControlFlagsOffset) & ControlFlagVisible) != 0)
            {
                found.Add(GetControlExtent(control));
            }
        }

        return found;
    }

    /// <summary>Moves a button through the engine's own SetExtent, which re-lays its border and
    /// label. MAIN THREAD ONLY.</summary>
    public static void SetButtonExtent(nint button, MenuColumnRule.Extent extent)
    {
        int* rect = stackalloc int[4];
        rect[0] = extent.Left;
        rect[1] = extent.Top;
        rect[2] = extent.Width;
        rect[3] = extent.Height;
        ButtonSetExtentFn(button, rect);
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

    // ---- quit to main menu (LIVE-TEST PRIMITIVE, docs/research/disconnect-reconnect.md §9) --
    //
    // CClientExoAppInternal::ShutDownToMainMenu(this) - thiscall, NO stack args (bare `ret`,
    // verified at 0x005FCBD0: push ecx/ebx/esi, ..., pop esi/ebx/ecx, ret). This is exactly what
    // CClientExoAppInternal::StallServerCode (0x00602CDE) calls once
    // CSWGuiInGameOptions::ReallyQuit's SetEndGamePending(1,0,1) is noticed on a later frame -
    // i.e. the SAME function a real "Quit Game -> Yes" click ultimately reaches (confirmed via
    // xrefs.py: the only two callers are CClientExoApp::ShutDownToMainMenu's thin jmp-forward
    // wrapper at 0x005ED890 and StallServerCode). It tears the current server/module down,
    // clears the client's dialogue/GUI state and calls DisplayMainMenu (0x005FCA30) /
    // StartMenuMusic (0x005F9AF0) along the way.
    //
    // Built because reaching a genuine "no player object at all" state by clicking through the
    // vanilla pause menu is a documented dead end on this rig: docs/research/session-save.md's
    // "Attempted, not achieved" section measured ZERO of 36 candidate clicks landing on a
    // demo.ps1-repositioned window, and a blind keyboard-only navigation attempt (Escape/Enter
    // guesses) wedged an instance badly enough to need a full rig restart. Calling the engine
    // function directly needs no coordinates and no guessing, the same way `usedoor`/`skipline`
    // already route around missing OS input elsewhere in this codebase.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ShutDownToMainMenuFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005FCBD0;

    /// <summary>
    /// Quits the local instance straight to the main menu, exactly as a real "Quit Game -&gt;
    /// Yes" would. Returns false (harmlessly) if the client internal is not resolvable. MAIN
    /// THREAD ONLY - do not call while the script VM is mid-call.
    /// </summary>
    public static bool ShutDownToMainMenu()
    {
        nint internalClient = GetClientInternal();
        if (internalClient == 0)
        {
            return false;
        }

        ShutDownToMainMenuFn(internalClient);
        return true;
    }
}
