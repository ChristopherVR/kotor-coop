namespace KotorCoop.Plugin;

// The pure half of the gamepad on-screen keyboard (docs/research/gamepad.md §7): a character
// grid, its cursor, and the confirm/cancel state machine. No engine pointers, so the test
// project compiles this file in directly, same as GamepadMapping.cs.
//
// Why a grid and not DIK key injection: the address/password editbox and the ~ console both
// take typed characters through Win32 WM_CHAR (docs/research/connect-flow.md "Real editable
// text control", docs/research/console-open-safe.md "Typed chars reach the buffer"), a
// DIFFERENT path from the buffered DirectInput read the rest of the gamepad layer uses for
// movement and menu keys. So text entry cannot be driven by DirectInputKeyboardHook at all —
// it needs the SAME posted-window-message trick GamepadController already uses for the mouse
// (docs/research/mouse-cursor-input.md §"Recipe A"), aimed at WM_CHAR instead of WM_MOUSEMOVE.
// The impure half (GamepadController.TextEntry.cs) posts those messages; this file only
// decides WHICH character and WHEN.

/// <summary>One cell's worth of typing the grid can produce this frame.</summary>
internal enum GamepadTextEntryCommand
{
    /// <summary>Nothing to do this frame.</summary>
    None,

    /// <summary>Type <see cref="GamepadTextEntryResult.Character"/> (the selected cell).</summary>
    Type,

    /// <summary>Backspace (DIK/ASCII 0x08).</summary>
    Backspace,

    /// <summary>A literal space.</summary>
    Space,

    /// <summary>Confirm: submit whatever the box holds (Enter, or the modal's own OK path).</summary>
    Submit,

    /// <summary>Cancel: close the box without submitting (Escape, or the modal's own Cancel).</summary>
    Cancel,
}

/// <summary>What <see cref="GamepadTextEntryStateMachine.Step"/> produced this frame.</summary>
internal readonly record struct GamepadTextEntryResult(GamepadTextEntryCommand Command, char Character)
{
    public static readonly GamepadTextEntryResult None = new(GamepadTextEntryCommand.None, '\0');
}

/// <summary>
/// One frame's edge-triggered pad intents, already resolved by the caller from D-pad presses
/// and/or a stick-latch crossing (GamepadController.TextEntry.cs) — this struct itself needs
/// no pad state, which is what keeps <see cref="GamepadTextEntryStateMachine.Step"/> pure.
/// </summary>
internal readonly record struct GamepadTextEntryInput(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool Type,
    bool Backspace,
    bool Space,
    bool Confirm,
    bool Cancel,
    bool ShiftToggle)
{
    public static readonly GamepadTextEntryInput None = new(false, false, false, false, false, false, false, false, false, false);
}

/// <summary>
/// The on-screen character grid. Digits and a small symbols row are always the same; the three
/// QWERTY letter rows have a lower and an upper variant, toggled by <c>Shift</c> (LB). Rows are
/// ragged on purpose — the symbols row is shorter — so navigation clamps rather than assuming a
/// rectangle. No Backspace/Space cell: those are dedicated buttons (B/X), same as a real keyboard
/// has dedicated keys for them instead of needing to be "typed".
/// </summary>
internal static class GamepadTextGrid
{
    public static readonly string[] Lower =
    [
        "1234567890",
        "qwertyuiop",
        "asdfghjkl:",
        "zxcvbnm,.'",
        "-_@?!/",
    ];

    public static readonly string[] Upper =
    [
        "1234567890",
        "QWERTYUIOP",
        "ASDFGHJKL:",
        "ZXCVBNM,.'",
        "-_@?!/",
    ];

    public const int RowCount = 5;

    public static int RowLength(int row) => Lower[row].Length;

    public static char CharAt(int row, int col, bool shift) => (shift ? Upper : Lower)[row][col];
}

/// <summary>
/// The grid's selection: row/column, clamped (never wraps vertically; wraps horizontally at
/// the row's own — possibly shorter — length). Pure arithmetic over <see cref="GamepadTextGrid"/>.
/// </summary>
internal sealed class GamepadTextGridCursor
{
    public int Row { get; private set; }
    public int Col { get; private set; }

    public void Reset()
    {
        Row = 0;
        Col = 0;
    }

    public void MoveUp() => MoveRow(-1);

    public void MoveDown() => MoveRow(1);

    public void MoveLeft()
    {
        int length = GamepadTextGrid.RowLength(Row);
        Col = Col > 0 ? Col - 1 : length - 1;
    }

    public void MoveRight()
    {
        int length = GamepadTextGrid.RowLength(Row);
        Col = Col < length - 1 ? Col + 1 : 0;
    }

    private void MoveRow(int delta)
    {
        Row = Math.Clamp(Row + delta, 0, GamepadTextGrid.RowCount - 1);
        Col = Math.Min(Col, GamepadTextGrid.RowLength(Row) - 1);
    }

    public char Selected(bool shift) => GamepadTextGrid.CharAt(Row, Col, shift);
}

/// <summary>
/// Open/closed, the cursor, the shift flag, and one frame of intents in → one command out.
/// Deliberately does not know the ANSWER string or which box it is feeding — that is the
/// controller's job (it already lives in the engine's own editbox/console buffer, which
/// already renders itself; this grid only ever needs to say WHAT WAS PRESSED).
///
/// Lifecycle: <see cref="Open"/> resets the cursor and shift to a known state every time (the
/// controller calls it whenever a co-op text box gains focus, including re-opening after a
/// rejected/re-shown box — a fresh grid on re-open is a fine trade for the simplicity of never
/// tracking "was this the same box as last frame"). <see cref="Step"/> closes itself
/// (<see cref="Active"/> = false) the moment it returns <see cref="GamepadTextEntryCommand.Submit"/>
/// or <see cref="GamepadTextEntryCommand.Cancel"/> — the controller re-opens it next frame if
/// the box is still there (e.g. an address the box's own grammar rejected).
/// </summary>
internal sealed class GamepadTextEntryStateMachine
{
    private readonly GamepadTextGridCursor _cursor = new();
    private bool _shift;

    public bool Active { get; private set; }

    public int Row => _cursor.Row;

    public int Col => _cursor.Col;

    public bool Shift => _shift;

    public char Selected => _cursor.Selected(_shift);

    public void Open()
    {
        Active = true;
        _shift = false;
        _cursor.Reset();
    }

    public void Close() => Active = false;

    public GamepadTextEntryResult Step(GamepadTextEntryInput input)
    {
        if (!Active)
        {
            return GamepadTextEntryResult.None;
        }

        if (input.ShiftToggle)
        {
            _shift = !_shift;
        }

        if (input.Up)
        {
            _cursor.MoveUp();
        }

        if (input.Down)
        {
            _cursor.MoveDown();
        }

        if (input.Left)
        {
            _cursor.MoveLeft();
        }

        if (input.Right)
        {
            _cursor.MoveRight();
        }

        // Cancel/Confirm take priority over a same-frame Type/Backspace/Space — a real pad
        // press is one button, but a defensive order still matters if a caller ever coalesces.
        if (input.Cancel)
        {
            Active = false;
            return new GamepadTextEntryResult(GamepadTextEntryCommand.Cancel, '\0');
        }

        if (input.Confirm)
        {
            Active = false;
            return new GamepadTextEntryResult(GamepadTextEntryCommand.Submit, '\0');
        }

        if (input.Backspace)
        {
            return new GamepadTextEntryResult(GamepadTextEntryCommand.Backspace, '\0');
        }

        if (input.Space)
        {
            return new GamepadTextEntryResult(GamepadTextEntryCommand.Space, '\0');
        }

        if (input.Type)
        {
            return new GamepadTextEntryResult(GamepadTextEntryCommand.Type, Selected);
        }

        return GamepadTextEntryResult.None;
    }

    /// <summary>One grid row as a display line: cells space-separated, the selected cell in
    /// brackets — the only "cursor" the plain ASCII queued-text renderer can show
    /// (docs/research/console-overlay.md: no per-character colour, no newlines). Longest line
    /// (row 1, 10 letters) is 29 characters, well under the queued-text budget.</summary>
    public string RenderRow(int row)
    {
        int length = GamepadTextGrid.RowLength(row);
        System.Text.StringBuilder sb = new(length * 3);
        for (int col = 0; col < length; col++)
        {
            if (col > 0)
            {
                sb.Append(' ');
            }

            char c = GamepadTextGrid.CharAt(row, col, _shift);
            if (row == Row && col == Col)
            {
                sb.Append('[').Append(c).Append(']');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
