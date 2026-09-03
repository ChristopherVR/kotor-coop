namespace KotorCoop.Plugin;

// The engine's own view of the cursor, the window and "is a full-screen panel up" — what
// the gamepad layer needs to move a cursor the way the mouse does and to pick a mode. Every
// address is from docs/research/mouse-cursor-input.md's answer sheet (all VERIFIED there)
// and the GUI-busy flag from Odyssey.Store.cs / Odyssey.GalaxyMap.cs, where it gates the
// engine's own Show*Gui calls.
internal static unsafe partial class Odyssey
{
    /// <summary>The render window, <c>*(HWND*)0x007A39D8</c>; 0 before the window exists.</summary>
    private const nint GameWindowPtr = 0x007A39D8;

    /// <summary>Client size, <c>SCREEN_WIDTH 0x0078D1D4</c> / <c>SCREEN_HEIGHT 0x0078D1D8</c>.</summary>
    private const nint ScreenWidthPtr = 0x0078D1D4;
    private const nint ScreenHeightPtr = 0x0078D1D8;

    /// <summary>CClientExoAppInternal's authoritative cursor (int, client px, top-down),
    /// re-pushed to the GUI every frame — the one the mouse-message path writes.</summary>
    private const int ClientInternalMouseX = 0x3C8;
    private const int ClientInternalMouseY = 0x3CC;

    /// <summary>CExoInputInternal+0x140 → CExoRawInputInternal, whose +0x04 is "input active" —
    /// what <c>CExoInput::GetActive 0x005DF510</c> returns and every mouse branch of
    /// WinMessageHandler tests first (mouse-cursor-input.md §7).</summary>
    private const int InputInternalRawOffset = 0x140;
    private const int RawInputActiveOffset = 0x04;

    // CClientExoApp::SetInputActive 0x005EDA10 — __thiscall (this = the client object,
    // *(CAppManager+4)), (int active, int clearEvents); the same thunk WM_ACTIVATE's handler
    // calls (0x0040288D..0x004028DC). Body (0x005F6B10, read 2026-09-03): CExoInput::SetActive
    // (writes the flag, re-Acquires the keyboard and mouse devices), TurnOffKeyboardModal when
    // deactivating, ClearEvents when asked, and a no-op camera-drag mask clear when activating.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetInputActiveFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x005EDA10;

    public static nint GameWindow => *(nint*)GameWindowPtr;

    /// <summary>The engine's own "input active" flag: false while the window is inactive,
    /// when WinMessageHandler drops every mouse message before the input layer sees it.</summary>
    public static bool IsInputActive
    {
        get
        {
            nint input = Deref(InputSingletonPtr);
            if (input == 0)
            {
                return false;
            }

            nint internalInput = Deref(input + 4);
            if (internalInput == 0)
            {
                return false;
            }

            nint raw = Deref(internalInput + InputInternalRawOffset);
            return raw != 0 && *(int*)(raw + RawInputActiveOffset) != 0;
        }
    }

    /// <summary>
    /// Turns input on through the engine's own WM_ACTIVATE path while the window is NOT the
    /// active one — the rig's virtual pad needs it (its windows never are), because posted
    /// clicks are otherwise dropped at the activity gate. Not for a real pad: a backgrounded
    /// instance must keep ignoring input, as FocusKeeper's doc comment insists.
    /// MAIN THREAD ONLY. Returns true when it had to act.
    /// </summary>
    public static bool EnsureInputActive()
    {
        if (IsInputActive)
        {
            return false;
        }

        nint manager = Deref(AppManagerPtr);
        if (manager == 0)
        {
            return false;
        }

        nint client = Deref(manager + 4);
        if (client == 0)
        {
            return false;
        }

        SetInputActiveFn(client, 1, 0);
        return true;
    }

    public static int ScreenWidth => *(int*)ScreenWidthPtr;

    public static int ScreenHeight => *(int*)ScreenHeightPtr;

    /// <summary>The cursor the engine currently believes in, client pixels, top-down.</summary>
    public static bool TryGetCursor(out int x, out int y)
    {
        nint clientInternal = GetClientInternal();
        if (clientInternal == 0)
        {
            x = 0;
            y = 0;
            return false;
        }

        x = *(int*)(clientInternal + ClientInternalMouseX);
        y = *(int*)(clientInternal + ClientInternalMouseY);
        return true;
    }

    /// <summary>
    /// True while a full-screen in-game panel (equipment, inventory, character, map,
    /// journal, options, store, galaxy map, party selection) is up — CGuiInGame+0x30, the
    /// flag the engine's own Show*Gui entry points refuse on. False when no world is loaded.
    /// </summary>
    public static bool IsFullScreenGuiUp
    {
        get
        {
            nint gui = GetInGameGui();
            return gui != 0 && *(int*)(gui + InGameGuiBusyOffset) != 0;
        }
    }
}
