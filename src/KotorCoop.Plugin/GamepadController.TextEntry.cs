namespace KotorCoop.Plugin;

/// <summary>
/// The impure half of the gamepad on-screen keyboard: drives <see cref="GamepadTextEntryStateMachine"/>
/// (GamepadTextEntry.cs, pure/unit-tested) from the same per-frame <see cref="PadState"/>
/// GamepadController.cs already reads, and turns its output into the ONE thing this needs that
/// is new — a posted <c>WM_CHAR</c> — plus calls into <see cref="PasswordEntryModal"/> that
/// already exist for the mouse-click path.
///
/// Why WM_CHAR and not DIK: the address/password editbox and the ~ console take typed
/// characters through Win32 WM_CHAR (docs/research/connect-flow.md, docs/research/
/// console-open-safe.md), not the buffered DirectInput read <see cref="DirectInputKeyboardHook"/>
/// feeds for movement/menu keys — those are two separate input paths in this engine. Posting
/// WM_CHAR is the SAME class of trick as the mouse messages <see cref="ApplyCursor"/> already
/// posts: <c>PostMessage</c> (not Send) from inside this process, landing on the game's own
/// PeekMessage pump next frame like real input (docs/research/mouse-cursor-input.md). So this
/// file adds no new engine call addresses at all — only a standard Win32 message, aimed at
/// window-message handlers the research already reads and documents.
///
/// Gating (docs/limitations.md's controller row, "must not interfere with normal pad
/// gameplay"): <see cref="GamepadController.OnFrame"/> only reaches <see cref="RunTextEntry"/>
/// when a co-op text box has focus (the ~ console open, or a <see cref="PasswordEntryModal"/>
/// instance open) AND the window is foreground — the SAME foreground gate every other pad
/// action here uses, so whatever already stopped an alt-tabbed or Steam-Input-driven session
/// from double-acting stops this too: nothing new is read from the pad (same XInput/virtual-pad
/// source, same single first-connected slot), only the MEANING of a press changes while a text
/// box has focus, exactly like World mode's buttons mean something different in Cursor mode.
/// </summary>
internal sealed partial class GamepadController
{
    private const int GridColumn = 3;
    private const int GridTitleRow = 12;
    private const int GridFirstRow = 13; // GamepadTextGrid.RowCount (5) rows: 13..17
    private const int GridHelpRow = 18;
    private const float GridLife = 0.3f;
    private const float GridAlpha = 0.85f;

    /// <summary>Set once per box-open (not every frame): which box the grid is typing into,
    /// for the title line and the plugin-log status. "chat" is the console pre-filled with
    /// <c>say </c> (<see cref="Odyssey.OpenChatInput"/>); anything else typed at the console
    /// reads as "console".</summary>
    private string _textEntryTargetLabel = "";

    /// <summary>
    /// One frame of the on-screen keyboard. Called only while a co-op text box has focus and
    /// the window is foreground (see the gating note on the class). Opens the grid on the
    /// frame the box first gains focus (or regains it — e.g. the address box re-showing after
    /// the player's last attempt was rejected), reads the SAME pad state and edge set
    /// GamepadController.OnFrame already computed, and executes whatever the pure state
    /// machine decided.
    /// </summary>
    private void RunTextEntry(PadState pad, PadButtons pressedThisFrame, bool consoleOpen, bool modalOpen, long nowMs)
    {
        if (!_textEntry.Active)
        {
            _textEntry.Open();
            _gridVertical.Reset();
            _gridHorizontal.Reset();
            _gridVerticalWas = 0;
            _gridHorizontalWas = 0;
            _textEntryTargetLabel = modalOpen
                ? PasswordEntryModal.ActiveAsk == PasswordEntryModal.Ask.Address ? "address" : "password"
                : Odyssey.ReadConsoleLine().StartsWith("say ", StringComparison.Ordinal) ? "chat" : "console";
        }

        // Stick navigation: one move per threshold crossing (StickAxisLatch's own hysteresis),
        // not once per held frame — a text grid should not race a deflected stick across rows.
        // D-pad presses are edge-triggered the same way every other pad button here is.
        int vertical = _gridVertical.Update(pad.LeftY);
        int horizontal = _gridHorizontal.Update(pad.LeftX);
        bool stickUp = vertical > 0 && _gridVerticalWas <= 0;
        bool stickDown = vertical < 0 && _gridVerticalWas >= 0;
        bool stickLeft = horizontal < 0 && _gridHorizontalWas >= 0;
        bool stickRight = horizontal > 0 && _gridHorizontalWas <= 0;
        _gridVerticalWas = vertical;
        _gridHorizontalWas = horizontal;

        GamepadTextEntryInput input = new(
            Up: stickUp || (pressedThisFrame & PadButtons.DpadUp) != 0,
            Down: stickDown || (pressedThisFrame & PadButtons.DpadDown) != 0,
            Left: stickLeft || (pressedThisFrame & PadButtons.DpadLeft) != 0,
            Right: stickRight || (pressedThisFrame & PadButtons.DpadRight) != 0,
            Type: (pressedThisFrame & PadButtons.A) != 0,
            Backspace: (pressedThisFrame & PadButtons.B) != 0,
            Space: (pressedThisFrame & PadButtons.X) != 0,
            Confirm: (pressedThisFrame & (PadButtons.Y | PadButtons.Start)) != 0,
            Cancel: (pressedThisFrame & PadButtons.Back) != 0,
            ShiftToggle: (pressedThisFrame & PadButtons.LeftShoulder) != 0);

        GamepadTextEntryResult result = _textEntry.Step(input);
        ExecuteTextEntryCommand(result, modalOpen);

        if (_textEntry.Active)
        {
            DrawTextEntryGrid();
        }
    }

    /// <summary>
    /// Turns one <see cref="GamepadTextEntryResult"/> into the one engine-facing action it
    /// needs. Type/Backspace/Space are the SAME posted WM_CHAR regardless of which box has
    /// focus (both read it the same way); Submit/Cancel differ because the console's own
    /// submit/close is a WM_CHAR the engine's own DoConsole already handles
    /// (docs/research/console-open-safe.md, VERIFIED disassembly for both 0x0D/0x0A and 0x1B),
    /// while the modal's Enter-in-editbox routing is explicitly UNVERIFIED
    /// (docs/research/connect-flow.md "NEEDS-LIVE: ... Enter-in-editbox routing") — so the
    /// modal case calls the exact code its own OK/Cancel buttons already call
    /// (<see cref="PasswordEntryModal.ConfirmActive"/>/<see cref="PasswordEntryModal.CancelActive"/>)
    /// instead of trusting a WM_CHAR path nobody has watched work on this panel.
    /// </summary>
    private static void ExecuteTextEntryCommand(GamepadTextEntryResult result, bool modalOpen)
    {
        switch (result.Command)
        {
            case GamepadTextEntryCommand.Type:
                PostChar(result.Character);
                break;

            case GamepadTextEntryCommand.Backspace:
                PostChar((char)NativeMethods.AsciiBackspace);
                break;

            case GamepadTextEntryCommand.Space:
                PostChar(' ');
                break;

            case GamepadTextEntryCommand.Submit:
                if (modalOpen)
                {
                    PasswordEntryModal.ConfirmActive();
                }
                else
                {
                    PostChar((char)NativeMethods.AsciiCarriageReturn);
                }

                break;

            case GamepadTextEntryCommand.Cancel:
                if (modalOpen)
                {
                    PasswordEntryModal.CancelActive();
                }
                else
                {
                    PostChar((char)NativeMethods.AsciiEscape);
                }

                break;
        }
    }

    /// <summary>Posts one WM_CHAR to the game window — see the class doc for why this is safe
    /// and sufficient (the same posted-message technique already proven for the mouse).</summary>
    private static void PostChar(char c)
    {
        nint window = Odyssey.GameWindow;
        if (window == 0)
        {
            return;
        }

        NativeMethods.PostMessage(window, NativeMethods.WmChar, (nuint)(byte)c, 1);
    }

    /// <summary>Draws the grid with the SAME queued-text path (<see cref="Odyssey.PostTextLine"/>)
    /// the console hint and this class's own notice already use — enqueue-only, safe from the
    /// SwapBuffers hook, re-posted every frame while active so it stays up
    /// (docs/research/console-overlay.md). Rows 12-18 at column 3: below every other posted
    /// line this codebase's controllers use (console input 0-2, the console-assist hint at
    /// row 3, this class's own status notice at row 7), so nothing else competes for the cell.</summary>
    private unsafe void DrawTextEntryGrid()
    {
        Odyssey.SetQueuedTextAlpha(GridAlpha);

        PostGridLine(
            $"GAMEPAD KEYBOARD: {_textEntryTargetLabel.ToUpperInvariant()}{(_textEntry.Shift ? " (SHIFT)" : "")}",
            GridTitleRow);

        for (int row = 0; row < GamepadTextGrid.RowCount; row++)
        {
            PostGridLine(_textEntry.RenderRow(row), GridFirstRow + row);
        }

        PostGridLine("A=type B=back X=space Y=send Back=cancel LB=shift", GridHelpRow);

        Odyssey.ResetQueuedTextAlpha();
    }

    private static unsafe void PostGridLine(string text, int row)
    {
        byte[] bytes = EncodeAscii(text);
        fixed (byte* p = bytes)
        {
            Odyssey.PostTextLine(p, GridColumn, row, GridLife);
        }
    }
}
