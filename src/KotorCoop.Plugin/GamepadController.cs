using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Gamepad support: turns an XInput pad into the keyboard-and-mouse the engine already
/// understands, once per frame on the SwapBuffers hook (main thread).
///
/// Keys go in through <see cref="DirectInputKeyboardHook"/> — the same buffered DirectInput
/// read the real keyboard feeds — as press/release edges diffed from the layout's per-frame
/// intent (<see cref="GamepadLayout"/>), so the engine sees a held W exactly as it would from
/// a finger on the key. The cursor and the two mouse buttons go in as the window messages
/// the mouse would post (docs/research/mouse-cursor-input.md §"Recipe A"), so the engine's
/// own hover, click and drag handling runs unchanged.
///
/// The mode (<see cref="GamepadModeRule"/>) follows the screen: sticks walk in the world and
/// drive the cursor in menus, conversations and the main menu; RS click forces cursor mode
/// for the times the world needs a pointer (the action bar, a far-away door). Everything is
/// released the moment the pad disconnects, the window loses focus, or the ~ console opens,
/// so a stuck W can never survive a state the pad no longer sees.
///
/// coop.ini: <c>gamepad = 1</c> (off by default; 0 or absent disables the layer entirely — no XInput load, no
/// DirectInput wrap use), <c>gamepad_cursor_speed = 1.0</c> (multiplier on
/// <see cref="GamepadCursorRule.BaseSpeed"/>). The <c>gamepad</c> console verb reports
/// status and, for the unattended rig, drives a virtual pad (<c>gamepad sim ly=1</c>).
///
/// Text entry (the address/password box, chat, the console — docs/research/gamepad.md §7) is
/// the other half, in GamepadController.TextEntry.cs: a co-op text box gaining focus preempts
/// World/Cursor mode the same way the ~ console already did (<see cref="GamepadModeRule"/>
/// only ever forced Suspended for it before), except now the pad drives an on-screen character
/// grid instead of doing nothing.
/// </summary>
internal sealed partial class GamepadController
{
    private const int NoticeColumn = 3;
    private const int NoticeRow = 7;
    private const float NoticeLife = 0.5f;
    private const float NoticeAlpha = 0.9f;
    private const long NoticeMs = 4000;

    private readonly bool _configured;
    private readonly float _cursorSpeed;
    private readonly GamepadKeymap _keymap;
    private readonly GamepadLayout _layout = new();
    private readonly GamepadIntent _intent = new();
    private readonly bool[] _held = new bool[256];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // ── on-screen keyboard (GamepadController.TextEntry.cs) ──────────────────────────────
    private readonly GamepadTextEntryStateMachine _textEntry = new();
    private readonly StickAxisLatch _gridVertical = new();
    private readonly StickAxisLatch _gridHorizontal = new();
    private int _gridVerticalWas;
    private int _gridHorizontalWas;

    private bool _enabled;
    private bool _cursorToggle;
    private bool _leftHeld;
    private bool _rightHeld;
    private PadState _previous = PadState.Idle;
    private bool _padSeen;
    private bool _padConnected;
    private int _loggedSlot = -1;
    private long _lastFrameMs;
    private float _cursorRemainderX;
    private float _cursorRemainderY;
    private GamepadMode _lastMode = GamepadMode.Suspended;

    // The virtual pad: set by `gamepad sim`, cleared by `gamepad sim off`. While present it
    // replaces the physical pad entirely, so a rig test is deterministic whatever is plugged in.
    private PadState? _virtual;

    private string? _notice;
    private byte[] _noticeBytes = [];
    private long _noticeUntilMs;

    public GamepadController(string gameDirectory)
    {
        _configured = PluginConfig.ReadBool("gamepad", false);
        _enabled = _configured;
        _cursorSpeed = PluginConfig.ReadFloat("gamepad_cursor_speed", 1.0f);
        _keymap = LoadKeymap(gameDirectory);

        if (_keymap.Overrides.Count > 0)
        {
            PluginHost.Log("gamepad: following swkotor.ini [Keymapping] re-bindings: "
                + string.Join(", ", _keymap.Overrides));
        }

        PluginHost.Log(_configured
            ? $"gamepad: enabled (cursor speed x{_cursorSpeed:0.##}); "
                + (XInputSource.IsAvailable ? $"XInput via {XInputSource.Library}" : "XInput not available on this machine")
            : "gamepad: disabled (coop.ini gamepad = 1 turns it on)");
    }

    private static GamepadKeymap LoadKeymap(string gameDirectory)
    {
        try
        {
            string path = Path.Combine(gameDirectory, "swkotor.ini");
            if (File.Exists(path))
            {
                return GamepadKeymap.FromIni(File.ReadAllLines(path));
            }
        }
        catch (Exception ex)
        {
            PluginHost.Log($"gamepad: could not read swkotor.ini [Keymapping], using the default keys: {ex.Message}");
        }

        return GamepadKeymap.Default;
    }

    public void OnFrame()
    {
        long nowMs = _clock.ElapsedMilliseconds;
        float dt = _lastFrameMs == 0 ? 0f : (nowMs - _lastFrameMs) / 1000f;
        _lastFrameMs = nowMs;

        DrawNotice(nowMs);

        // Stays on after `sim off` until the releases it queued have gone out.
        DirectInputKeyboardHook.DeliverWhileUnacquired =
            _virtual is not null
            || (DirectInputKeyboardHook.DeliverWhileUnacquired && DirectInputKeyboardHook.PendingEvents > 0);

        if (!_enabled)
        {
            ReleaseEverything();
            _textEntry.Close();
            return;
        }

        PadState pad;
        bool connected;
        if (_virtual is { } virtualPad)
        {
            pad = virtualPad;
            connected = true;

            // Rig only: the window is never the active one, so the engine would drop the
            // clicks this is about to post (mouse-cursor-input.md §7's activity gate).
            if (Odyssey.EnsureInputActive())
            {
                PluginHost.Log("gamepad: virtual pad re-activated the engine's input");
            }
        }
        else
        {
            connected = XInputSource.TryRead(nowMs, out pad);
        }

        if (connected != _padConnected)
        {
            _padConnected = connected;
            if (connected)
            {
                _padSeen = true;
                if (_virtual is null && XInputSource.ActiveSlot != _loggedSlot)
                {
                    _loggedSlot = XInputSource.ActiveSlot;
                    PluginHost.Log($"gamepad: controller connected on XInput slot {XInputSource.ActiveSlot}");
                    ShowNotice($"gamepad connected (RS click toggles cursor mode)", nowMs);
                }
            }
            else
            {
                PluginHost.Log("gamepad: controller disconnected");
                _loggedSlot = -1;
            }
        }

        if (!connected)
        {
            ReleaseEverything();
            _textEntry.Close();
            _previous = PadState.Idle;
            return;
        }

        PadButtons pressedThisFrame = pad.Buttons & ~_previous.Buttons;
        _previous = pad;

        // The virtual pad is the rig's, and the rig's windows are never the foreground one;
        // a real pad stays foreground-only so a backgrounded game does not walk off.
        bool foreground = _virtual is not null || NativeMethods.IsForeground();
        bool consoleOpen = Odyssey.IsConsoleOpen;
        bool modalOpen = PasswordEntryModal.IsAnyOpen;

        // A co-op text box has focus: hand the pad to the on-screen keyboard instead of the
        // usual World/Cursor translation. Same foreground gate as everything else here — a
        // backgrounded window drives nothing, same as before this mode existed. Checked BEFORE
        // GamepadModeRule because it needs state (the console/modal open flags) the rule does
        // not take; the rule keeps deciding Suspended/World/Cursor for every other frame.
        if (foreground && (consoleOpen || modalOpen))
        {
            ReleaseEverything(); // release anything still held from World/Cursor mode
            _lastMode = GamepadMode.TextEntry;
            RunTextEntry(pad, pressedThisFrame, consoleOpen, modalOpen, nowMs);
            return;
        }

        if (_textEntry.Active)
        {
            // The box closed by some other means (Alt-Tab dismissed it, a keyboard player
            // typed Enter/Escape themselves, ...): drop the grid rather than leave it drawn
            // over a screen it no longer applies to.
            _textEntry.Close();
        }

        bool inGame = Odyssey.IsInGame;
        bool fullScreenGui = Odyssey.IsFullScreenGuiUp;
        bool conversation = Odyssey.IsConversationActive;
        GamepadMode mode = GamepadModeRule.Decide(true, foreground, consoleOpen, inGame, fullScreenGui, conversation, _cursorToggle);

        if (mode != GamepadMode.Suspended && (pressedThisFrame & PadButtons.RightThumb) != 0)
        {
            _cursorToggle = !_cursorToggle;
            ShowNotice(_cursorToggle ? "gamepad: cursor mode" : "gamepad: world mode", nowMs);
            mode = GamepadModeRule.Decide(true, foreground, consoleOpen, inGame, fullScreenGui, conversation, _cursorToggle);
        }

        _lastMode = mode;

        _layout.Evaluate(pad, mode, _keymap, pressedThisFrame, _intent);
        ApplyKeys();

        if (mode == GamepadMode.Suspended)
        {
            ApplyMouseButtons(false, false);
            return;
        }

        ApplyCursor(dt);
        ApplyMouseButtons(_intent.LeftButton, _intent.RightButton);
        ApplyWheel(_intent.Wheel);
    }

    /// <summary>Diffs the intent against what is held and queues one edge per change.</summary>
    private void ApplyKeys()
    {
        for (int dik = 1; dik < 256; dik++)
        {
            bool want = _intent[(byte)dik];
            if (want == _held[dik])
            {
                continue;
            }

            if (DirectInputKeyboardHook.Enqueue((byte)dik, want))
            {
                _held[dik] = want;
            }
        }
    }

    private void ReleaseEverything()
    {
        for (int dik = 1; dik < 256; dik++)
        {
            if (_held[dik] && DirectInputKeyboardHook.Enqueue((byte)dik, false))
            {
                _held[dik] = false;
            }
        }

        ApplyMouseButtons(false, false);
        _layout.Evaluate(PadState.Idle, GamepadMode.Suspended, _keymap, PadButtons.None, _intent);
    }

    private void ApplyCursor(float dt)
    {
        (float dx, float dy) = GamepadCursorRule.Step(_intent.CursorX, _intent.CursorY, _cursorSpeed, dt);
        _cursorRemainderX += dx;
        _cursorRemainderY += dy;
        int stepX = (int)_cursorRemainderX;
        int stepY = (int)_cursorRemainderY;
        if (stepX == 0 && stepY == 0)
        {
            return;
        }

        _cursorRemainderX -= stepX;
        _cursorRemainderY -= stepY;

        nint window = Odyssey.GameWindow;
        if (window == 0 || !Odyssey.TryGetCursor(out int x, out int y))
        {
            return;
        }

        int width = Odyssey.ScreenWidth;
        int height = Odyssey.ScreenHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        x = Math.Clamp(x + stepX, 0, width - 1);
        y = Math.Clamp(y + stepY, 0, height - 1);

        // Move the OS pointer too, so the next real mouse movement continues from where the
        // pad left the cursor instead of snapping back to wherever the mouse physically sat.
        NativeMethods.Point screen = new() { X = x, Y = y };
        if (NativeMethods.ClientToScreen(window, ref screen))
        {
            NativeMethods.SetCursorPos(screen.X, screen.Y);
        }

        NativeMethods.PostMessage(window, NativeMethods.WmMouseMove, HeldButtonFlags(), NativeMethods.MouseLParam(x, y));
    }

    private nuint HeldButtonFlags() =>
        (_leftHeld ? NativeMethods.MkLButton : 0u) | (_rightHeld ? NativeMethods.MkRButton : 0u);

    private void ApplyMouseButtons(bool left, bool right)
    {
        if (left == _leftHeld && right == _rightHeld)
        {
            return;
        }

        nint window = Odyssey.GameWindow;
        if (window == 0 || !Odyssey.TryGetCursor(out int x, out int y))
        {
            _leftHeld = left;
            _rightHeld = right;
            return;
        }

        nint lParam = NativeMethods.MouseLParam(x, y);
        if (left != _leftHeld)
        {
            _leftHeld = left;
            NativeMethods.PostMessage(window,
                left ? NativeMethods.WmLButtonDown : NativeMethods.WmLButtonUp, HeldButtonFlags(), lParam);
        }

        if (right != _rightHeld)
        {
            _rightHeld = right;
            NativeMethods.PostMessage(window,
                right ? NativeMethods.WmRButtonDown : NativeMethods.WmRButtonUp, HeldButtonFlags(), lParam);
        }
    }

    private void ApplyWheel(int notches)
    {
        if (notches == 0)
        {
            return;
        }

        nint window = Odyssey.GameWindow;
        if (window == 0 || !Odyssey.TryGetCursor(out int x, out int y))
        {
            return;
        }

        // The wheel message carries SCREEN coordinates (the engine ScreenToClient's them).
        NativeMethods.Point screen = new() { X = x, Y = y };
        NativeMethods.ClientToScreen(window, ref screen);
        nuint wParam = (nuint)(uint)((notches * NativeMethods.WheelDelta) << 16) | HeldButtonFlags();
        NativeMethods.PostMessage(window, NativeMethods.WmMouseWheel, wParam, NativeMethods.MouseLParam(screen.X, screen.Y));
    }

    // ── console verb ──────────────────────────────────────────────────────────────────

    /// <summary>Handles a <c>gamepad ...</c> console line. MAIN THREAD (the cheat drain). Returns the reply line.</summary>
    public string HandleConsole(string arguments)
    {
        long nowMs = _clock.ElapsedMilliseconds;
        string[] parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length == 0 ? "status" : parts[0].ToLowerInvariant();
        switch (verb)
        {
            case "status":
                return Report(nowMs);

            case "on":
                _enabled = true;
                return Say("gamepad: on", nowMs);

            case "off":
                _enabled = false;
                _virtual = null;
                ReleaseEverything();
                _textEntry.Close();
                return Say("gamepad: off (keys released)", nowMs);

            case "cursor":
                _cursorToggle = !_cursorToggle;
                return Say(_cursorToggle ? "gamepad: cursor mode" : "gamepad: world mode", nowMs);

            case "sim":
            {
                string rest = string.Join(' ', parts.Skip(1));
                if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    _virtual = null;
                    ReleaseEverything();
                    _previous = PadState.Idle;
                    return Say("gamepad: virtual pad off", nowMs);
                }

                if (!VirtualPadGrammar.TryApply(rest, _virtual ?? PadState.Idle, out PadState next, out string error))
                {
                    return Say($"gamepad sim: {error}", nowMs);
                }

                _virtual = next;
                _enabled = true;
                return Say($"gamepad: virtual pad {Describe(next)}", nowMs);
            }

            default:
                return Say("gamepad: usage gamepad [status|on|off|cursor|sim <lx= ly= rx= ry= lt= rt= a b x y lb rb start back ls rs up down left right>|sim off]", nowMs);
        }
    }

    private string Report(long nowMs)
    {
        string source = _virtual is { } v
            ? $"virtual pad {Describe(v)}"
            : _padConnected
                ? $"XInput slot {XInputSource.ActiveSlot} via {XInputSource.Library}"
                : XInputSource.IsAvailable
                    ? $"no controller connected (XInput via {XInputSource.Library}" + (_padSeen ? ", one was seen earlier)" : ")")
                    : "XInput not available on this machine";

        string held = _intent.HeldKeyCount() == 0
            ? "none"
            : string.Join(",", _intent.HeldKeys().Select(k => $"0x{k:X2}"));

        string line =
            $"gamepad: {(_enabled ? "on" : "off")}; {source}; mode {_lastMode}"
            + $"{(_cursorToggle ? " (cursor forced)" : "")}; keys held {held}"
            + $"{(_leftHeld ? " +LMB" : "")}{(_rightHeld ? " +RMB" : "")}; "
            + $"DI: {(DirectInputKeyboardHook.IsKeyboardWrapped ? "keyboard wrapped" : DirectInputKeyboardHook.IsDirectInputWrapped ? "IDirectInput8 wrapped, keyboard not yet" : "not wrapped")}, "
            + $"mouse {(DirectInputMouseHook.IsWrapped ? "wrapped" : "not wrapped")}, "
            + $"{DirectInputKeyboardHook.RealReads} reads, {DirectInputKeyboardHook.InjectedEvents} injected, "
            + $"{DirectInputKeyboardHook.PendingEvents} pending, {DirectInputKeyboardHook.DroppedEvents} dropped, "
            + $"last read hr 0x{DirectInputKeyboardHook.LastReadHr:X8} shape {DirectInputKeyboardHook.LastReadShape}"
            + (DirectInputKeyboardHook.LastDiagnostic is { } diag ? $" ({diag})" : "")
            + $"; isolate real input {(DirectInputKeyboardHook.IsolateRealInput ? "ON" : "off")}"
            + $"; cursor speed x{_cursorSpeed:0.##}"
            + (Odyssey.TryGetCursor(out int cx, out int cy) ? $" at {cx},{cy} of {Odyssey.ScreenWidth}x{Odyssey.ScreenHeight}" : "")
            + $"; engine input {(Odyssey.IsInputActive ? "active" : "inactive")}"
            + (_keymap.Overrides.Count > 0 ? $"; keymap overrides {string.Join(",", _keymap.Overrides)}" : "")
            + (_textEntry.Active
                ? $"; text entry: target {_textEntryTargetLabel}, row {_textEntry.Row} col {_textEntry.Col}"
                    + $" shift {_textEntry.Shift}, selected '{_textEntry.Selected}'"
                : "");
        PluginHost.Log(line);

        // The console reply (the shared cheat-detail slot) holds 63 characters, so what the
        // player sees is the short form; the full line above is in the plugin log.
        string shortSource = _virtual is not null ? "virtual pad"
            : _padConnected ? $"XInput slot {XInputSource.ActiveSlot}"
            : XInputSource.IsAvailable ? "no pad" : "no XInput";
        string di = DirectInputKeyboardHook.IsKeyboardWrapped ? "keyboard"
            : DirectInputKeyboardHook.IsDirectInputWrapped ? "no keyboard" : "none";
        string brief =
            $"gamepad: {(_enabled ? "on" : "off")}; {shortSource}; {_lastMode}"
            + $"{(_cursorToggle ? " (forced)" : "")}; DI {di}; held {_intent.HeldKeyCount()}"
            + $"{(_leftHeld ? " +LMB" : "")}{(_rightHeld ? " +RMB" : "")}"
            + (_textEntry.Active ? $" r{_textEntry.Row}c{_textEntry.Col}='{_textEntry.Selected}'" : "");
        ShowNotice(brief, nowMs);
        return brief;
    }

    /// <summary>Only the non-zero fields — a full dump would not fit the console reply.</summary>
    private static string Describe(PadState pad)
    {
        List<string> parts = [];
        if (pad.LeftX != 0) parts.Add($"lx={pad.LeftX:0.##}");
        if (pad.LeftY != 0) parts.Add($"ly={pad.LeftY:0.##}");
        if (pad.RightX != 0) parts.Add($"rx={pad.RightX:0.##}");
        if (pad.RightY != 0) parts.Add($"ry={pad.RightY:0.##}");
        if (pad.LeftTrigger != 0) parts.Add($"lt={pad.LeftTrigger:0.##}");
        if (pad.RightTrigger != 0) parts.Add($"rt={pad.RightTrigger:0.##}");
        if (pad.Buttons != PadButtons.None) parts.Add($"buttons={pad.Buttons}");
        return parts.Count == 0 ? "idle" : string.Join(' ', parts);
    }

    private string Say(string line, long nowMs)
    {
        PluginHost.Log(line);
        ShowNotice(line.Length > 110 ? line[..110] : line, nowMs);
        return line;
    }

    // ── on-screen notice (column 3, row 7: below ConnectionIndicatorController's row 6) ──

    private void ShowNotice(string text, long nowMs)
    {
        _notice = text;
        _noticeBytes = EncodeAscii(text);
        _noticeUntilMs = nowMs + NoticeMs;
    }

    private unsafe void DrawNotice(long nowMs)
    {
        if (_notice is null || nowMs >= _noticeUntilMs || _noticeBytes.Length == 0)
        {
            _notice = null;
            return;
        }

        Odyssey.SetQueuedTextAlpha(NoticeAlpha);
        fixed (byte* p = _noticeBytes)
        {
            Odyssey.PostTextLine(p, NoticeColumn, NoticeRow, NoticeLife);
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    private static byte[] EncodeAscii(string text)
    {
        byte[] bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)'?';
        }

        return bytes;
    }
}
