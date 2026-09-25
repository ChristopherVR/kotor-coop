namespace KotorCoop.Plugin;

/// <summary>
/// Synthetic mouse and keyboard for the isolated rig (docs/research/isolated-rig.md): drives
/// the game exactly the way <see cref="GamepadController"/> already does — window messages for
/// the mouse (docs/research/mouse-cursor-input.md §6 "Recipe A"), the plugin's own DirectInput
/// keyboard proxy (<see cref="DirectInputKeyboardHook"/>) for keys — but as a direct, one-shot
/// console verb instead of a per-frame stick translation, and WITHOUT the one thing the gamepad
/// path does that the isolated rig cannot afford: <c>SetCursorPos</c>. A click here never moves
/// the user's real OS pointer, because the engine reads its cursor from the <c>lParam</c> of the
/// window message alone (verified in mouse-cursor-input.md; the OS pointer position is never
/// read on this path).
///
/// Coordinates are CLIENT pixels, top-down — the same space <c>tools/shot.ps1</c> captures and
/// <c>tools/click.ps1</c> already accepts, so a caller can point at a screenshot pixel directly
/// with no translation. (The old SendInput-based click.ps1 needed ClientToScreen because
/// SendInput moves the OS pointer in screen space; PostMessage's lParam is already client-
/// relative, so this controller skips that step entirely.)
///
/// No instance state: every call is a complete, independent action, so this is a static class
/// like <see cref="DirectInputKeyboardHook"/> rather than a controller PluginHost constructs and
/// drives per frame.
///
/// Stays clear of the engine's own memory: every mouse action is a Win32 message the retail
/// window procedure already knows how to handle, and every key action is an entry in the same
/// ring buffer the DirectInput proxy already drains — nothing here pokes an engine field
/// directly (see the "engine write rules" note: prefer engine/mod surfaces over memory edits).
/// </summary>
internal static class InputInjectionController
{
    /// <summary>Handles an `input ...` console line. MAIN THREAD (the cheat drain, same as
    /// GamepadController.HandleConsole). Returns the reply line.</summary>
    public static string HandleConsole(string arguments)
    {
        string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();
        try
        {
            return verb switch
            {
                "status" => Status(),
                "move" => Move(parts),
                "click" => Click(parts),
                "key" => Key(parts),
                _ => "input: usage input click <x> <y> [left|right] | input move <x> <y> | "
                    + "input key <dik hex/dec> [down|up|tap] | input status",
            };
        }
        catch (Exception ex)
        {
            return $"input: {verb} failed: {ex.Message}";
        }
    }

    private static string Status()
    {
        nint window = Odyssey.GameWindow;
        string cursor = Odyssey.TryGetCursor(out int cx, out int cy)
            ? $"{cx},{cy} of {Odyssey.ScreenWidth}x{Odyssey.ScreenHeight}"
            : "unavailable";
        string line = $"input: window 0x{window:X}, foreground {NativeMethods.IsForeground()}, "
            + $"engine input {(Odyssey.IsInputActive ? "active" : "inactive")}, cursor {cursor}, "
            + $"DI keyboard {(DirectInputKeyboardHook.IsKeyboardWrapped ? "wrapped" : "not wrapped")}, "
            + $"DI mouse {(DirectInputMouseHook.IsWrapped ? "wrapped" : "not wrapped")}, "
            + $"isolate real input {(DirectInputKeyboardHook.IsolateRealInput ? "ON" : "off")}";
        PluginHost.Log(line);
        return line;
    }

    private static string Move(string[] parts)
    {
        if (!TryParseXy(parts, 1, out int x, out int y, out string error))
        {
            return $"input move: {error}";
        }

        nint window = Odyssey.GameWindow;
        if (window == 0)
        {
            return "input move: no game window yet";
        }

        EnsureUnfocusedDelivery();
        NativeMethods.PostMessage(window, NativeMethods.WmMouseMove, 0, NativeMethods.MouseLParam(x, y));
        return $"input: moved to {x},{y} (client)";
    }

    private static string Click(string[] parts)
    {
        if (!TryParseXy(parts, 1, out int x, out int y, out string error))
        {
            return $"input click: {error}";
        }

        bool right = parts.Length > 3 && parts[3].Equals("right", StringComparison.OrdinalIgnoreCase);
        if (parts.Length > 3 && !right && !parts[3].Equals("left", StringComparison.OrdinalIgnoreCase))
        {
            return $"input click: unknown button '{parts[3]}' (expected left|right)";
        }

        nint window = Odyssey.GameWindow;
        if (window == 0)
        {
            return "input click: no game window yet";
        }

        EnsureUnfocusedDelivery();

        // Recipe A (mouse-cursor-input.md §6): the button message ALONE carries the position —
        // BufferEvent_2 buffers mouse-X, mouse-Y and the button edge from the SAME lParam, and
        // ProcessInput re-runs HandleMouseMove after every buffered event, so the hover control
        // is already correct for this coordinate by the time the button event dispatches. No
        // separate WM_MOUSEMOVE is needed, and none is sent here.
        nint lParam = NativeMethods.MouseLParam(x, y);
        uint down = right ? NativeMethods.WmRButtonDown : NativeMethods.WmLButtonDown;
        uint up = right ? NativeMethods.WmRButtonUp : NativeMethods.WmLButtonUp;
        nuint heldFlag = right ? NativeMethods.MkRButton : NativeMethods.MkLButton;

        NativeMethods.PostMessage(window, down, heldFlag, lParam);
        NativeMethods.PostMessage(window, up, 0, lParam);

        return $"input: clicked {(right ? "right" : "left")} at {x},{y} (client)";
    }

    private static string Key(string[] parts)
    {
        if (parts.Length < 2 || !TryParseDik(parts[1], out byte dik))
        {
            return "input key: usage input key <dik hex 0xNN or decimal> [down|up|tap]";
        }

        string mode = parts.Length > 2 ? parts[2].ToLowerInvariant() : "tap";
        if (mode is not ("tap" or "down" or "up"))
        {
            return $"input key: unknown mode '{mode}' (expected down|up|tap)";
        }

        if (!DirectInputKeyboardHook.IsKeyboardWrapped)
        {
            return "input key: the keyboard is not proxied yet (coop.ini gamepad = 0, or "
                + "no IDirectInputDevice8 keyboard created yet) - nothing queued";
        }

        EnsureUnfocusedDelivery();

        bool ok = mode switch
        {
            "down" => DirectInputKeyboardHook.Enqueue(dik, true),
            "up" => DirectInputKeyboardHook.Enqueue(dik, false),
            _ => DirectInputKeyboardHook.Enqueue(dik, true) & DirectInputKeyboardHook.Enqueue(dik, false),
        };

        return ok
            ? $"input: key 0x{dik:X2} {mode}"
            : $"input key: queue full ({DirectInputKeyboardHook.PendingEvents} pending) - dropped";
    }

    /// <summary>
    /// The rig's windows are deliberately never the OS foreground one (that is the whole point
    /// of an isolated run), so both input paths need the same nudge the virtual gamepad already
    /// uses: turn the engine's own "input active" flag on directly (bypassing the WM_ACTIVATE
    /// wait so mouse messages are not dropped at WinMessageHandler's activity gate) and mark the
    /// DirectInput keyboard queue to answer even while the real device sits unacquired (its
    /// buffered read only ever succeeds while the window IS foreground, which an isolated window
    /// never is). Idempotent and cheap; safe to call on every action.
    /// </summary>
    private static void EnsureUnfocusedDelivery()
    {
        if (Odyssey.EnsureInputActive())
        {
            PluginHost.Log("input: engine input re-activated for an unfocused window");
        }

        DirectInputKeyboardHook.DeliverWhileUnacquired = true;
    }

    private static bool TryParseXy(string[] parts, int startIndex, out int x, out int y, out string error)
    {
        x = 0;
        y = 0;
        if (parts.Length < startIndex + 2
            || !int.TryParse(parts[startIndex], out x)
            || !int.TryParse(parts[startIndex + 1], out y))
        {
            error = "usage <x> <y> (client pixels, same space as shot.ps1's screenshot)";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryParseDik(string token, out byte dik)
    {
        dik = 0;
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return byte.TryParse(token.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out dik);
        }

        return byte.TryParse(token, out dik);
    }
}
