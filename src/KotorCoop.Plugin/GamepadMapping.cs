namespace KotorCoop.Plugin;

// Gamepad support, the pure half: everything here is arithmetic and tables, no engine
// pointers, so the test project compiles this file in directly (see the Plugin\ links in
// tests/KotorCoop.Tests/KotorCoop.Tests.csproj). The engine-facing half — the DirectInput
// keyboard-device wrap, the XInput poll and the window-message cursor — is GamepadController,
// DirectInputKeyboardHook and XInputSource.
//
// Why the keyboard route at all: KOTOR reads its keyboard as buffered DirectInput events
// (docs/re/frame-hook.md §"Per frame": IDirectInputDevice8::GetDeviceData on the keyboard
// device, once per frame) and its cursor and clicks from ordinary window messages
// (docs/research/mouse-cursor-input.md §1). It never asks XInput, and externally injected
// SendInput keystrokes never reach it (docs/live-verification.md). So a pad becomes a
// keyboard-and-mouse: sticks and buttons are translated into the SAME DIK scancodes the
// player's real keyboard would produce — honouring swkotor.ini's [Keymapping] — and appended
// to the game's own buffered read; the cursor moves through the same WM_MOUSEMOVE the mouse
// would send. The engine sees nothing it does not already understand.

/// <summary>XInput's button word, bit for bit (XINPUT_GAMEPAD.wButtons).</summary>
[Flags]
internal enum PadButtons : ushort
{
    None = 0,
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>One sampled pad: axes normalised (-1..1 sticks, 0..1 triggers), buttons raw.</summary>
internal readonly record struct PadState(
    PadButtons Buttons,
    float LeftX,
    float LeftY,
    float RightX,
    float RightY,
    float LeftTrigger,
    float RightTrigger)
{
    public static readonly PadState Idle = new(PadButtons.None, 0, 0, 0, 0, 0, 0);

    public bool Has(PadButtons button) => (Buttons & button) != 0;

    /// <summary>Anything at all pressed or deflected — used to wake the pad from "idle".</summary>
    public bool IsActive(float deadZone) =>
        Buttons != PadButtons.None
        || MathF.Abs(LeftX) > deadZone || MathF.Abs(LeftY) > deadZone
        || MathF.Abs(RightX) > deadZone || MathF.Abs(RightY) > deadZone
        || LeftTrigger > 0.5f || RightTrigger > 0.5f;
}

/// <summary>
/// The engine actions the pad drives, each with the swkotor.ini [Keymapping] line that
/// re-binds it (<c>ActionNNN[A|B]=code</c>) and the shipped default key.
/// </summary>
internal enum EngineAction
{
    MoveForward,   // Action280A  W
    MoveBack,      // Action280B  S
    CameraLeft,    // Action284A  A
    CameraRight,   // Action284B  D
    DefaultAction, // Action239   R
    CancelCombat,  // Action240   F
    Pause,         // Action241   Space
    Gui,           // Action223   Escape
    ChangeChar,    // Action206   Tab
    SelectPrev,    // Action204   Q
    SelectNext,    // Action205   E
    Map,           // Action215   M
    Stealth,       // Action264   G
    Medical,       // Action234   5
    Target1,       // Action226   1
    Target2,       // Action228   2
    Target3,       // Action230   3
    Target4,       // Action232   4  (personal Force power / item slot)
    PrevMenu,      // Action243   Q  (GUI page left)
    NextMenu,      // Action244   E  (GUI page right)
}

/// <summary>DirectInput keyboard scancodes (DIK_*) the defaults and the code table use.</summary>
internal static class Dik
{
    public const byte Escape = 0x01;
    public const byte D1 = 0x02;
    public const byte D2 = 0x03;
    public const byte D3 = 0x04;
    public const byte D4 = 0x05;
    public const byte D5 = 0x06;
    public const byte D6 = 0x07;
    public const byte D7 = 0x08;
    public const byte D8 = 0x09;
    public const byte D9 = 0x0A;
    public const byte D0 = 0x0B;
    public const byte Tab = 0x0F;
    public const byte Q = 0x10;
    public const byte W = 0x11;
    public const byte E = 0x12;
    public const byte R = 0x13;
    public const byte T = 0x14;
    public const byte Y = 0x15;
    public const byte U = 0x16;
    public const byte I = 0x17;
    public const byte O = 0x18;
    public const byte P = 0x19;
    public const byte Return = 0x1C;
    public const byte LControl = 0x1D;
    public const byte A = 0x1E;
    public const byte S = 0x1F;
    public const byte D = 0x20;
    public const byte F = 0x21;
    public const byte G = 0x22;
    public const byte H = 0x23;
    public const byte J = 0x24;
    public const byte K = 0x25;
    public const byte L = 0x26;
    public const byte LShift = 0x2A;
    public const byte Z = 0x2C;
    public const byte X = 0x2D;
    public const byte C = 0x2E;
    public const byte V = 0x2F;
    public const byte B = 0x30;
    public const byte N = 0x31;
    public const byte M = 0x32;
    public const byte RShift = 0x36;
    public const byte Space = 0x39;
    public const byte Capital = 0x3A;
    public const byte F1 = 0x3B;
    public const byte Numpad8 = 0x48;
    public const byte Numpad4 = 0x4B;
    public const byte Numpad6 = 0x4D;
    public const byte Add = 0x4E;
    public const byte Numpad2 = 0x50;
    public const byte RControl = 0x9D;
    public const byte Pause = 0xC5;
    public const byte Up = 0xC8;
    public const byte Left = 0xCB;
    public const byte Right = 0xCD;
    public const byte Down = 0xD0;
    public const byte Delete = 0xD3;
}

/// <summary>
/// The engine's own key-code scheme (the numbers in keymap.2da's <c>character</c> column and
/// swkotor.ini's [Keymapping] values), as far as it has been read off the shipped table:
/// letters A..Z are 51..76, digits 1..9 are 77..85, then a handful of named keys. Anything
/// not in the table maps to 0 = "unknown", and the caller keeps the action's default.
/// </summary>
internal static class EngineKeyCodes
{
    private static readonly byte[] Letters =
    [
        Dik.A, Dik.B, Dik.C, Dik.D, Dik.E, Dik.F, Dik.G, Dik.H, Dik.I, Dik.J, Dik.K, Dik.L, Dik.M,
        Dik.N, Dik.O, Dik.P, Dik.Q, Dik.R, Dik.S, Dik.T, Dik.U, Dik.V, Dik.W, Dik.X, Dik.Y, Dik.Z,
    ];

    public static byte ToDik(int engineCode)
    {
        if (engineCode is >= 51 and <= 76)
        {
            return Letters[engineCode - 51];
        }

        if (engineCode is >= 77 and <= 85)
        {
            return (byte)(Dik.D1 + (engineCode - 77));
        }

        if (engineCode is >= 39 and <= 44)
        {
            return (byte)(Dik.F1 + (engineCode - 39));
        }

        return engineCode switch
        {
            86 => Dik.D0,
            87 => Dik.Space,
            88 => Dik.Return,
            31 => Dik.Escape,
            30 => Dik.Tab,
            24 => Dik.LShift,
            25 => Dik.RShift,
            28 => Dik.LControl,
            29 => Dik.RControl,
            89 => Dik.Capital,
            90 => Dik.Pause,
            9 => Dik.Up,
            10 => Dik.Down,
            7 => Dik.Left,
            8 => Dik.Right,
            12 => Dik.Numpad2,
            14 => Dik.Numpad4,
            16 => Dik.Numpad6,
            18 => Dik.Numpad8,
            21 => Dik.Delete,
            23 => Dik.Add,
            _ => 0,
        };
    }
}

/// <summary>
/// Which DIK scancode each <see cref="EngineAction"/> currently sends: the shipped keymap by
/// default, overridden per action by the player's swkotor.ini [Keymapping] section so a
/// re-bound "move forward" still moves forward from the pad.
/// </summary>
internal sealed class GamepadKeymap
{
    private static readonly (EngineAction Action, string IniKey, byte Default)[] Table =
    [
        (EngineAction.MoveForward, "Action280A", Dik.W),
        (EngineAction.MoveBack, "Action280B", Dik.S),
        (EngineAction.CameraLeft, "Action284A", Dik.A),
        (EngineAction.CameraRight, "Action284B", Dik.D),
        (EngineAction.DefaultAction, "Action239", Dik.R),
        (EngineAction.CancelCombat, "Action240", Dik.F),
        (EngineAction.Pause, "Action241", Dik.Space),
        (EngineAction.Gui, "Action223", Dik.Escape),
        (EngineAction.ChangeChar, "Action206", Dik.Tab),
        (EngineAction.SelectPrev, "Action204", Dik.Q),
        (EngineAction.SelectNext, "Action205", Dik.E),
        (EngineAction.Map, "Action215", Dik.M),
        (EngineAction.Stealth, "Action264", Dik.G),
        (EngineAction.Medical, "Action234", Dik.D5),
        (EngineAction.Target1, "Action226", Dik.D1),
        (EngineAction.Target2, "Action228", Dik.D2),
        (EngineAction.Target3, "Action230", Dik.D3),
        (EngineAction.Target4, "Action232", Dik.D4),
        (EngineAction.PrevMenu, "Action243", Dik.Q),
        (EngineAction.NextMenu, "Action244", Dik.E),
    ];

    private readonly byte[] _dik;
    private readonly List<string> _overrides;

    private GamepadKeymap(byte[] dik, List<string> overrides)
    {
        _dik = dik;
        _overrides = overrides;
    }

    public static GamepadKeymap Default => new(DefaultCodes(), []);

    /// <summary>Actions whose key differs from the shipped default, as "Action280A=Z" text —
    /// for the status line, so a player can see the pad followed their re-binding.</summary>
    public IReadOnlyList<string> Overrides => _overrides;

    public byte this[EngineAction action] => _dik[(int)action];

    /// <summary>
    /// Parses a swkotor.ini's lines. Only the [Keymapping] section is read; a code the table
    /// does not know (or a malformed line) leaves that action at its default rather than
    /// binding it to a key the engine would not recognise either.
    /// </summary>
    public static GamepadKeymap FromIni(IEnumerable<string> lines)
    {
        byte[] codes = DefaultCodes();
        List<string> overrides = [];
        bool inSection = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inSection = line.Equals("[Keymapping]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            if (!int.TryParse(line[(equals + 1)..].Trim(), out int code))
            {
                continue;
            }

            foreach ((EngineAction action, string iniKey, byte fallback) in Table)
            {
                if (!key.Equals(iniKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                byte dik = EngineKeyCodes.ToDik(code);
                if (dik != 0 && dik != fallback)
                {
                    codes[(int)action] = dik;
                    overrides.Add($"{iniKey}={code}");
                }

                break;
            }
        }

        return new GamepadKeymap(codes, overrides);
    }

    private static byte[] DefaultCodes()
    {
        byte[] codes = new byte[Table.Length];
        foreach ((EngineAction action, _, byte fallback) in Table)
        {
            codes[(int)action] = fallback;
        }

        return codes;
    }
}

/// <summary>What the pad is driving this frame.</summary>
internal enum GamepadMode
{
    /// <summary>Hands off entirely: the ~ console is open (a key press would type into it),
    /// the window is not ours, or the pad is disabled.</summary>
    Suspended,

    /// <summary>In the world: left stick walks and turns, right stick is the cursor, RT clicks.</summary>
    World,

    /// <summary>A full-screen GUI, conversation, main menu or the player's own toggle: both
    /// sticks are the cursor, A clicks.</summary>
    Cursor,

    /// <summary>A co-op text box has focus (the ~ console, or the address/password panel) and
    /// the pad is driving the on-screen character grid instead (docs/research/gamepad.md §7).
    /// <see cref="GamepadModeRule"/> never returns this — it is a frame-loop decision made
    /// BEFORE the rule runs, because it needs state (the console/modal open flags) the rule
    /// does not take. Movement/menu keys and the cursor are fully released while in this mode,
    /// same as <see cref="Suspended"/>.</summary>
    TextEntry,
}

/// <summary>Chooses the <see cref="GamepadMode"/> from what the engine says is on screen.</summary>
internal static class GamepadModeRule
{
    public static GamepadMode Decide(
        bool enabled,
        bool foreground,
        bool consoleOpen,
        bool inGame,
        bool fullScreenGuiUp,
        bool conversationActive,
        bool cursorToggle)
    {
        if (!enabled || !foreground || consoleOpen)
        {
            return GamepadMode.Suspended;
        }

        if (!inGame || fullScreenGuiUp || conversationActive || cursorToggle)
        {
            return GamepadMode.Cursor;
        }

        return GamepadMode.World;
    }
}

/// <summary>
/// One stick axis turned into a held key with hysteresis: the key goes down past
/// <see cref="PressAt"/> and only comes up again below <see cref="ReleaseAt"/>, so a stick
/// resting near the threshold does not chatter W down/up every frame (each transition is a
/// buffered keyboard event the engine has to process, and a run/stop flicker looks awful).
/// </summary>
internal sealed class StickAxisLatch
{
    public const float PressAt = 0.5f;
    public const float ReleaseAt = 0.3f;

    /// <summary>-1, 0 or +1: which direction, if any, is currently latched down.</summary>
    public int Direction { get; private set; }

    public int Update(float value)
    {
        if (Direction == 0)
        {
            if (value >= PressAt)
            {
                Direction = 1;
            }
            else if (value <= -PressAt)
            {
                Direction = -1;
            }
        }
        else if (Direction > 0 ? value < ReleaseAt : value > -ReleaseAt)
        {
            Direction = 0;
        }

        return Direction;
    }

    public void Reset() => Direction = 0;
}

/// <summary>Stick-to-cursor response: radial dead zone, then a squared curve so a light touch
/// nudges by pixels and full deflection crosses the screen.</summary>
internal static class GamepadCursorRule
{
    /// <summary>XInput's own recommended left-stick dead zone (7849/32767).</summary>
    public const float DeadZone = 0.24f;

    /// <summary>Pixels per second at full deflection, before the coop.ini multiplier.</summary>
    public const float BaseSpeed = 900f;

    /// <summary>Maps one axis to a signed 0..1 response.</summary>
    public static float Response(float value)
    {
        float magnitude = MathF.Abs(value);
        if (magnitude <= DeadZone)
        {
            return 0f;
        }

        float scaled = MathF.Min(1f, (magnitude - DeadZone) / (1f - DeadZone));
        return MathF.CopySign(scaled * scaled, value);
    }

    /// <summary>Pixel delta for one frame. Y is screen-down positive: a stick pushed UP
    /// (positive XInput Y) moves the cursor up the screen.</summary>
    public static (float Dx, float Dy) Step(float stickX, float stickY, float speedMultiplier, float dtSeconds)
    {
        float dt = Math.Clamp(dtSeconds, 0f, 0.1f); // a hitch must not fling the cursor
        float speed = BaseSpeed * Math.Clamp(speedMultiplier, 0.1f, 5f);
        return (Response(stickX) * speed * dt, -Response(stickY) * speed * dt);
    }
}

/// <summary>
/// What the pad wants held this frame: keys as DIK scancodes, the two mouse buttons, and the
/// cursor/wheel motion. GamepadController diffs consecutive frames into press/release events.
/// </summary>
internal sealed class GamepadIntent
{
    private readonly bool[] _keys = new bool[256];

    public bool LeftButton { get; set; }
    public bool RightButton { get; set; }
    public float CursorX { get; set; }
    public float CursorY { get; set; }

    /// <summary>Wheel notches this frame: positive = up.</summary>
    public int Wheel { get; set; }

    public bool this[byte dik]
    {
        get => _keys[dik];
        set => _keys[dik] = value;
    }

    public void Clear()
    {
        Array.Clear(_keys);
        LeftButton = false;
        RightButton = false;
        CursorX = 0;
        CursorY = 0;
        Wheel = 0;
    }

    public int HeldKeyCount()
    {
        int n = 0;
        foreach (bool held in _keys)
        {
            if (held)
            {
                n++;
            }
        }

        return n;
    }

    public IEnumerable<byte> HeldKeys()
    {
        for (int i = 0; i < _keys.Length; i++)
        {
            if (_keys[i])
            {
                yield return (byte)i;
            }
        }
    }
}

/// <summary>
/// The layout. World mode: left stick = W/S + A/D (walk and turn, the way the Xbox release
/// played), right stick = cursor, RT = left click at the cursor (attack / use what it points
/// at), A = default action, B = Escape, X = cancel combat, Y = switch character, LB/RB =
/// previous/next target, LT = pause, D-pad = the four action slots 1-4, Start = Escape,
/// Back = map, LS click = stealth, RS click = force cursor mode. Cursor mode: both sticks =
/// cursor, A = left click, X = right click, B = Escape, D-pad up/down = wheel, D-pad
/// left/right and LB/RB = previous/next page, Y = switch character, LT = pause.
/// </summary>
internal sealed class GamepadLayout
{
    private readonly StickAxisLatch _forward = new();
    private readonly StickAxisLatch _turn = new();
    private readonly StickAxisLatch _wheel = new();

    /// <summary>Fills <paramref name="intent"/> for this frame. <paramref name="wheelEdge"/>
    /// is true on the frame a D-pad wheel direction was first pressed, so a held D-pad scrolls
    /// once per press rather than once per frame.</summary>
    public void Evaluate(PadState pad, GamepadMode mode, GamepadKeymap map, PadButtons pressedThisFrame, GamepadIntent intent)
    {
        intent.Clear();
        if (mode == GamepadMode.Suspended)
        {
            _forward.Reset();
            _turn.Reset();
            _wheel.Reset();
            return;
        }

        bool lt = pad.LeftTrigger > 0.5f;
        bool rt = pad.RightTrigger > 0.5f;

        // Shared between the two modes.
        intent[map[EngineAction.Gui]] = pad.Has(PadButtons.B) || pad.Has(PadButtons.Start);
        intent[map[EngineAction.ChangeChar]] = pad.Has(PadButtons.Y);
        intent[map[EngineAction.Pause]] = lt;

        if (mode == GamepadMode.World)
        {
            _wheel.Reset();
            int forward = _forward.Update(pad.LeftY);
            int turn = _turn.Update(pad.LeftX);
            intent[map[EngineAction.MoveForward]] = forward > 0;
            intent[map[EngineAction.MoveBack]] = forward < 0;
            intent[map[EngineAction.CameraLeft]] = turn < 0;
            intent[map[EngineAction.CameraRight]] = turn > 0;

            intent[map[EngineAction.DefaultAction]] = pad.Has(PadButtons.A);
            intent[map[EngineAction.CancelCombat]] = pad.Has(PadButtons.X);
            intent[map[EngineAction.SelectPrev]] = pad.Has(PadButtons.LeftShoulder);
            intent[map[EngineAction.SelectNext]] = pad.Has(PadButtons.RightShoulder);
            intent[map[EngineAction.Map]] = pad.Has(PadButtons.Back);
            intent[map[EngineAction.Stealth]] = pad.Has(PadButtons.LeftThumb);
            intent[map[EngineAction.Target1]] = pad.Has(PadButtons.DpadUp);
            intent[map[EngineAction.Target2]] = pad.Has(PadButtons.DpadRight);
            intent[map[EngineAction.Target3]] = pad.Has(PadButtons.DpadDown);
            intent[map[EngineAction.Target4]] = pad.Has(PadButtons.DpadLeft);

            intent.LeftButton = rt;
            intent.CursorX = pad.RightX;
            intent.CursorY = pad.RightY;
            return;
        }

        // Cursor mode.
        _forward.Reset();
        _turn.Reset();
        intent.LeftButton = pad.Has(PadButtons.A) || rt;
        intent.RightButton = pad.Has(PadButtons.X);
        intent[map[EngineAction.PrevMenu]] = pad.Has(PadButtons.LeftShoulder) || pad.Has(PadButtons.DpadLeft);
        intent[map[EngineAction.NextMenu]] = pad.Has(PadButtons.RightShoulder) || pad.Has(PadButtons.DpadRight);

        // Whichever stick is further from centre drives the cursor; the other is ignored
        // rather than summed, so a thumb resting on one does not slow the other.
        float leftMagnitude = pad.LeftX * pad.LeftX + pad.LeftY * pad.LeftY;
        float rightMagnitude = pad.RightX * pad.RightX + pad.RightY * pad.RightY;
        if (leftMagnitude > rightMagnitude)
        {
            intent.CursorX = pad.LeftX;
            intent.CursorY = pad.LeftY;
        }
        else
        {
            intent.CursorX = pad.RightX;
            intent.CursorY = pad.RightY;
        }

        if ((pressedThisFrame & PadButtons.DpadUp) != 0)
        {
            intent.Wheel = 1;
        }
        else if ((pressedThisFrame & PadButtons.DpadDown) != 0)
        {
            intent.Wheel = -1;
        }
    }
}

/// <summary>
/// The <c>gamepad sim ...</c> console verb's argument grammar: <c>lx=1 ly=-0.5 rx= ry= lt= rt=</c>
/// for axes and <c>a b x y lb rb start back ls rs up down left right</c> (bare, or <c>=1</c> /
/// <c>=0</c>) for buttons. Every field not named keeps its previous value, so a rig test can
/// hold W with <c>sim ly=1</c>, then release with <c>sim ly=0</c>.
/// </summary>
internal static class VirtualPadGrammar
{
    public static bool TryApply(string arguments, PadState previous, out PadState result, out string error)
    {
        PadButtons buttons = previous.Buttons;
        float lx = previous.LeftX, ly = previous.LeftY, rx = previous.RightX, ry = previous.RightY;
        float lt = previous.LeftTrigger, rt = previous.RightTrigger;
        result = previous;
        error = "";

        foreach (string token in arguments.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = token;
            string value = "1";
            int equals = token.IndexOf('=');
            if (equals >= 0)
            {
                name = token[..equals];
                value = token[(equals + 1)..];
            }

            name = name.ToLowerInvariant();
            if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float number))
            {
                error = $"'{token}': not a number";
                return false;
            }

            PadButtons? button = name switch
            {
                "a" => PadButtons.A,
                "b" => PadButtons.B,
                "x" => PadButtons.X,
                "y" => PadButtons.Y,
                "lb" => PadButtons.LeftShoulder,
                "rb" => PadButtons.RightShoulder,
                "start" => PadButtons.Start,
                "back" => PadButtons.Back,
                "ls" => PadButtons.LeftThumb,
                "rs" => PadButtons.RightThumb,
                "up" => PadButtons.DpadUp,
                "down" => PadButtons.DpadDown,
                "left" => PadButtons.DpadLeft,
                "right" => PadButtons.DpadRight,
                _ => null,
            };

            if (button is { } flag)
            {
                buttons = number != 0 ? buttons | flag : buttons & ~flag;
                continue;
            }

            float axis = Math.Clamp(number, -1f, 1f);
            switch (name)
            {
                case "lx": lx = axis; break;
                case "ly": ly = axis; break;
                case "rx": rx = axis; break;
                case "ry": ry = axis; break;
                case "lt": lt = Math.Clamp(number, 0f, 1f); break;
                case "rt": rt = Math.Clamp(number, 0f, 1f); break;
                default:
                    error = $"'{token}': unknown field (axes lx ly rx ry lt rt; buttons a b x y lb rb start back ls rs up down left right)";
                    return false;
            }
        }

        result = new PadState(buttons, lx, ly, rx, ry, lt, rt);
        return true;
    }
}
