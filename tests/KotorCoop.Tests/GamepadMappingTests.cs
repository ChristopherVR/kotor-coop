using Xunit;
using KotorCoop.Plugin;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of controller support (src/KotorCoop.Plugin/GamepadMapping.cs): the engine
/// key-code table, the [Keymapping] reader, the mode rule, the stick latch, the cursor
/// curve, the layout and the `gamepad sim` grammar. The DirectInput wrap and the window
/// messages are live-verified on the rig (docs/research/gamepad.md).
/// </summary>
public class GamepadMappingTests
{
    // ---- engine key codes -------------------------------------------------------------

    [Theory]
    [InlineData(51, Dik.A)]
    [InlineData(54, Dik.D)]
    [InlineData(73, Dik.W)]
    [InlineData(69, Dik.S)]
    [InlineData(76, Dik.Z)]
    [InlineData(77, Dik.D1)]
    [InlineData(81, Dik.D5)]
    [InlineData(85, Dik.D9)]
    [InlineData(86, Dik.D0)]
    [InlineData(87, Dik.Space)]
    [InlineData(88, Dik.Return)]
    [InlineData(31, Dik.Escape)]
    [InlineData(30, Dik.Tab)]
    [InlineData(39, Dik.F1)]
    [InlineData(9, Dik.Up)]
    [InlineData(8, Dik.Right)]
    public void EngineKeyCodesMatchTheShippedKeymap(int engineCode, byte dik)
    {
        // The engine codes come straight off the rig's swkotor.ini [Keymapping]:
        // Action280A=73 (W) / 280B=69 (S) / 284A=51 (A) / 284B=54 (D) / 241=87 (Space) /
        // 223=31 (Esc) / 206=30 (Tab) / 226=77 (1) / 234=81 (5).
        Assert.Equal(dik, EngineKeyCodes.ToDik(engineCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50)]
    [InlineData(200)]
    public void UnknownEngineCodesMapToZero(int engineCode)
    {
        Assert.Equal(0, EngineKeyCodes.ToDik(engineCode));
    }

    // ---- keymap ----------------------------------------------------------------------

    [Fact]
    public void DefaultKeymapIsTheShippedLayout()
    {
        GamepadKeymap map = GamepadKeymap.Default;
        Assert.Equal(Dik.W, map[EngineAction.MoveForward]);
        Assert.Equal(Dik.S, map[EngineAction.MoveBack]);
        Assert.Equal(Dik.A, map[EngineAction.CameraLeft]);
        Assert.Equal(Dik.D, map[EngineAction.CameraRight]);
        Assert.Equal(Dik.Escape, map[EngineAction.Gui]);
        Assert.Equal(Dik.Space, map[EngineAction.Pause]);
        Assert.Equal(Dik.D1, map[EngineAction.Target1]);
        Assert.Empty(map.Overrides);
    }

    [Fact]
    public void KeymapFollowsARebindingInTheKeymappingSection()
    {
        GamepadKeymap map = GamepadKeymap.FromIni(
        [
            "[Game Options]",
            "Action280A=76",          // wrong section: ignored
            "[Keymapping]",
            "Action280A=59",          // I
            "action280b=61",          // K, case-insensitive key
            "Action239 = 87",         // Space, with spaces around '='
            "[Autopause Options]",
            "Action223=51",           // after the section ends: ignored
        ]);

        Assert.Equal(Dik.I, map[EngineAction.MoveForward]);
        Assert.Equal(Dik.K, map[EngineAction.MoveBack]);
        Assert.Equal(Dik.Space, map[EngineAction.DefaultAction]);
        Assert.Equal(Dik.Escape, map[EngineAction.Gui]);
        Assert.Equal(["Action280A=59", "Action280B=61", "Action239=87"], map.Overrides);
    }

    [Fact]
    public void KeymapKeepsTheDefaultOnAnUnknownCodeOrMalformedLine()
    {
        GamepadKeymap map = GamepadKeymap.FromIni(
        [
            "[Keymapping]",
            "Action280A=999",         // not in the table
            "Action280B=abc",         // not a number
            "Action284A",             // no '='
            "Action284B=54",          // the default itself: not an override
        ]);

        Assert.Equal(Dik.W, map[EngineAction.MoveForward]);
        Assert.Equal(Dik.S, map[EngineAction.MoveBack]);
        Assert.Equal(Dik.A, map[EngineAction.CameraLeft]);
        Assert.Equal(Dik.D, map[EngineAction.CameraRight]);
        Assert.Empty(map.Overrides);
    }

    // ---- mode ------------------------------------------------------------------------

    [Fact]
    public void ModeIsSuspendedWheneverThePadMustNotType()
    {
        Assert.Equal(GamepadMode.Suspended, GamepadModeRule.Decide(enabled: false, foreground: true, consoleOpen: false, inGame: true, fullScreenGuiUp: false, conversationActive: false, cursorToggle: false));
        Assert.Equal(GamepadMode.Suspended, GamepadModeRule.Decide(enabled: true, foreground: false, consoleOpen: false, inGame: true, fullScreenGuiUp: false, conversationActive: false, cursorToggle: false));
        // The ~ console: a W press would type a 'w' into the command line.
        Assert.Equal(GamepadMode.Suspended, GamepadModeRule.Decide(enabled: true, foreground: true, consoleOpen: true, inGame: true, fullScreenGuiUp: false, conversationActive: false, cursorToggle: false));
    }

    [Fact]
    public void ModeIsCursorOnAnyScreenThatIsNotTheWorld()
    {
        Assert.Equal(GamepadMode.Cursor, GamepadModeRule.Decide(true, true, false, inGame: false, fullScreenGuiUp: false, conversationActive: false, cursorToggle: false));
        Assert.Equal(GamepadMode.Cursor, GamepadModeRule.Decide(true, true, false, inGame: true, fullScreenGuiUp: true, conversationActive: false, cursorToggle: false));
        Assert.Equal(GamepadMode.Cursor, GamepadModeRule.Decide(true, true, false, inGame: true, fullScreenGuiUp: false, conversationActive: true, cursorToggle: false));
        Assert.Equal(GamepadMode.Cursor, GamepadModeRule.Decide(true, true, false, inGame: true, fullScreenGuiUp: false, conversationActive: false, cursorToggle: true));
        Assert.Equal(GamepadMode.World, GamepadModeRule.Decide(true, true, false, inGame: true, fullScreenGuiUp: false, conversationActive: false, cursorToggle: false));
    }

    // ---- stick latch -----------------------------------------------------------------

    [Fact]
    public void StickLatchHasHysteresis()
    {
        StickAxisLatch latch = new();
        Assert.Equal(0, latch.Update(0.45f));   // below the press threshold
        Assert.Equal(1, latch.Update(0.5f));    // pressed
        Assert.Equal(1, latch.Update(0.35f));   // still held: above the release threshold
        Assert.Equal(0, latch.Update(0.29f));   // released
        Assert.Equal(-1, latch.Update(-0.8f));
        Assert.Equal(-1, latch.Update(-0.31f));
        Assert.Equal(0, latch.Update(0f));
    }

    [Fact]
    public void StickLatchDoesNotFlipDirectionWithoutPassingThroughCentre()
    {
        StickAxisLatch latch = new();
        Assert.Equal(1, latch.Update(1f));
        // A straight swing to the other side releases first; the next frame presses the
        // other way. One transition per frame keeps the event stream sane.
        Assert.Equal(0, latch.Update(-1f));
        Assert.Equal(-1, latch.Update(-1f));
    }

    // ---- cursor curve ----------------------------------------------------------------

    [Fact]
    public void CursorResponseHasADeadZoneAndASquaredCurve()
    {
        Assert.Equal(0f, GamepadCursorRule.Response(0.2f));
        Assert.Equal(0f, GamepadCursorRule.Response(-0.24f));
        Assert.Equal(1f, GamepadCursorRule.Response(1f), 3);
        Assert.Equal(-1f, GamepadCursorRule.Response(-1f), 3);

        float half = GamepadCursorRule.Response(0.62f); // half-way through the live range
        Assert.InRange(half, 0.2f, 0.3f);                // squared: well under linear 0.5
    }

    [Fact]
    public void CursorStepScalesBySpeedAndClampsTheFrameTime()
    {
        (float dx, float dy) = GamepadCursorRule.Step(1f, 0f, 1f, 0.01f);
        Assert.Equal(9f, dx, 2);
        Assert.Equal(0f, dy, 2);

        (dx, dy) = GamepadCursorRule.Step(0f, 1f, 2f, 0.01f);
        Assert.Equal(0f, dx, 2);
        Assert.Equal(-18f, dy, 2); // stick up = screen up (negative y), doubled

        // A 2 s hitch is treated as 0.1 s: the cursor may not fly off the screen.
        (dx, _) = GamepadCursorRule.Step(1f, 0f, 1f, 2f);
        Assert.Equal(90f, dx, 2);

        // The coop.ini multiplier is clamped to 0.1..5.
        (dx, _) = GamepadCursorRule.Step(1f, 0f, 100f, 0.01f);
        Assert.Equal(45f, dx, 2);
    }

    // ---- layout ----------------------------------------------------------------------

    private static GamepadIntent Evaluate(GamepadLayout layout, PadState pad, GamepadMode mode, PadButtons pressed = PadButtons.None)
    {
        GamepadIntent intent = new();
        layout.Evaluate(pad, mode, GamepadKeymap.Default, pressed, intent);
        return intent;
    }

    [Fact]
    public void WorldModeLeftStickWalksAndTurns()
    {
        GamepadLayout layout = new();
        GamepadIntent intent = Evaluate(layout, PadState.Idle with { LeftY = 1f }, GamepadMode.World);
        Assert.True(intent[Dik.W]);
        Assert.False(intent[Dik.S]);
        Assert.Equal(1, intent.HeldKeyCount());

        // A straight swing forward-to-back releases W on the first frame and presses S on
        // the next (StickLatchDoesNotFlipDirectionWithoutPassingThroughCentre).
        PadState backLeft = PadState.Idle with { LeftY = -1f, LeftX = -1f };
        intent = Evaluate(layout, backLeft, GamepadMode.World);
        Assert.False(intent[Dik.W]);
        Assert.False(intent[Dik.S]);
        Assert.True(intent[Dik.A]);
        intent = Evaluate(layout, backLeft, GamepadMode.World);
        Assert.True(intent[Dik.S]);
        Assert.True(intent[Dik.A]);
        Assert.False(intent[Dik.W]);
        Assert.False(intent[Dik.D]);

        intent = Evaluate(layout, PadState.Idle, GamepadMode.World);
        Assert.Equal(0, intent.HeldKeyCount());
    }

    [Fact]
    public void WorldModeButtonsMapToTheShippedKeys()
    {
        GamepadLayout layout = new();
        PadState pad = new(
            PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y | PadButtons.LeftShoulder
            | PadButtons.RightShoulder | PadButtons.Back | PadButtons.LeftThumb | PadButtons.DpadUp
            | PadButtons.DpadRight | PadButtons.DpadDown | PadButtons.DpadLeft,
            0, 0, 0.7f, -0.2f, 1f, 1f);
        GamepadIntent intent = Evaluate(layout, pad, GamepadMode.World);

        Assert.True(intent[Dik.R]);       // A: default action
        Assert.True(intent[Dik.Escape]);  // B
        Assert.True(intent[Dik.F]);       // X: cancel combat
        Assert.True(intent[Dik.Tab]);     // Y: change character
        Assert.True(intent[Dik.Q]);       // LB
        Assert.True(intent[Dik.E]);       // RB
        Assert.True(intent[Dik.M]);       // Back: map
        Assert.True(intent[Dik.G]);       // LS: stealth
        Assert.True(intent[Dik.D1]);      // D-pad
        Assert.True(intent[Dik.D2]);
        Assert.True(intent[Dik.D3]);
        Assert.True(intent[Dik.D4]);
        Assert.True(intent[Dik.Space]);   // LT: pause
        Assert.True(intent.LeftButton);   // RT: click at the cursor
        Assert.False(intent.RightButton);
        Assert.Equal(0.7f, intent.CursorX);
        Assert.Equal(-0.2f, intent.CursorY);
        Assert.Equal(0, intent.Wheel);
    }

    [Fact]
    public void CursorModeClicksWithAAndUsesTheStrongerStick()
    {
        GamepadLayout layout = new();
        PadState pad = new(PadButtons.A | PadButtons.X | PadButtons.DpadLeft | PadButtons.RightShoulder,
            0.9f, 0f, 0.3f, 0.3f, 0f, 0f);
        GamepadIntent intent = Evaluate(layout, pad, GamepadMode.Cursor);

        Assert.True(intent.LeftButton);
        Assert.True(intent.RightButton);
        Assert.True(intent[Dik.Q]);       // D-pad left: previous page
        Assert.True(intent[Dik.E]);       // RB: next page
        Assert.False(intent[Dik.R]);      // A is a click here, not the default action
        Assert.False(intent[Dik.F]);      // X is a right click here
        Assert.False(intent[Dik.W]);      // the left stick never walks on a menu
        Assert.Equal(0.9f, intent.CursorX);
        Assert.Equal(0f, intent.CursorY);
    }

    [Fact]
    public void CursorModeWheelFiresOncePerDpadPress()
    {
        GamepadLayout layout = new();
        PadState held = PadState.Idle with { Buttons = PadButtons.DpadUp };
        Assert.Equal(1, Evaluate(layout, held, GamepadMode.Cursor, pressed: PadButtons.DpadUp).Wheel);
        Assert.Equal(0, Evaluate(layout, held, GamepadMode.Cursor).Wheel);
        Assert.Equal(-1, Evaluate(layout, PadState.Idle with { Buttons = PadButtons.DpadDown }, GamepadMode.Cursor, pressed: PadButtons.DpadDown).Wheel);
    }

    [Fact]
    public void SuspendedModeHoldsNothingAndForgetsTheLatches()
    {
        GamepadLayout layout = new();
        Assert.True(Evaluate(layout, PadState.Idle with { LeftY = 1f }, GamepadMode.World)[Dik.W]);

        GamepadIntent intent = Evaluate(layout, PadState.Idle with { LeftY = 1f, Buttons = PadButtons.A }, GamepadMode.Suspended);
        Assert.Equal(0, intent.HeldKeyCount());
        Assert.False(intent.LeftButton);

        // Back in the world with the stick at 0.4: the latch was reset, so this is a fresh
        // "not yet pressed", not a "still held from before".
        Assert.False(Evaluate(layout, PadState.Idle with { LeftY = 0.4f }, GamepadMode.World)[Dik.W]);
    }

    [Fact]
    public void LayoutHonoursARebinding()
    {
        GamepadKeymap map = GamepadKeymap.FromIni(["[Keymapping]", "Action280A=59"]); // W -> I
        GamepadLayout layout = new();
        GamepadIntent intent = new();
        layout.Evaluate(PadState.Idle with { LeftY = 1f }, GamepadMode.World, map, PadButtons.None, intent);
        Assert.True(intent[Dik.I]);
        Assert.False(intent[Dik.W]);
    }

    // ---- sim grammar -----------------------------------------------------------------

    [Fact]
    public void SimGrammarUpdatesOnlyTheFieldsNamed()
    {
        Assert.True(VirtualPadGrammar.TryApply("ly=1 a rt=0.75", PadState.Idle, out PadState pad, out string error));
        Assert.Equal("", error);
        Assert.Equal(1f, pad.LeftY);
        Assert.True(pad.Has(PadButtons.A));
        Assert.Equal(0.75f, pad.RightTrigger);
        Assert.Equal(0f, pad.LeftX);

        Assert.True(VirtualPadGrammar.TryApply("a=0 lx=-2", pad, out pad, out _));
        Assert.False(pad.Has(PadButtons.A));
        Assert.Equal(1f, pad.LeftY);        // untouched
        Assert.Equal(-1f, pad.LeftX);       // clamped
        Assert.Equal(0.75f, pad.RightTrigger);
    }

    [Theory]
    [InlineData("lz=1")]
    [InlineData("a=yes")]
    public void SimGrammarRejectsUnknownFieldsAndNonNumbers(string arguments)
    {
        Assert.False(VirtualPadGrammar.TryApply(arguments, PadState.Idle, out PadState pad, out string error));
        Assert.Equal(PadState.Idle, pad);
        Assert.NotEqual("", error);
    }

    [Fact]
    public void SimGrammarKnowsEveryButtonName()
    {
        Assert.True(VirtualPadGrammar.TryApply("a b x y lb rb start back ls rs up down left right", PadState.Idle, out PadState pad, out _));
        PadButtons all = PadButtons.A | PadButtons.B | PadButtons.X | PadButtons.Y
            | PadButtons.LeftShoulder | PadButtons.RightShoulder | PadButtons.Start | PadButtons.Back
            | PadButtons.LeftThumb | PadButtons.RightThumb
            | PadButtons.DpadUp | PadButtons.DpadDown | PadButtons.DpadLeft | PadButtons.DpadRight;
        Assert.Equal(all, pad.Buttons);
    }
}
