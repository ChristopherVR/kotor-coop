using Xunit;
using KotorCoop.Plugin;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of the gamepad on-screen keyboard (src/KotorCoop.Plugin/GamepadTextEntry.cs):
/// the character grid, its cursor, and the confirm/cancel state machine. The posted WM_CHAR
/// and the PasswordEntryModal calls are the impure half
/// (src/KotorCoop.Plugin/GamepadController.TextEntry.cs), NEEDS-LIVE per docs/research/gamepad.md §7.
/// </summary>
public class GamepadTextEntryTests
{
    // ---- GamepadTextGrid ---------------------------------------------------------------

    [Fact]
    public void RowLengthsMatchTheShippedLayout()
    {
        Assert.Equal(10, GamepadTextGrid.RowLength(0)); // digits
        Assert.Equal(10, GamepadTextGrid.RowLength(1)); // qwertyuiop
        Assert.Equal(10, GamepadTextGrid.RowLength(2)); // asdfghjkl:
        Assert.Equal(10, GamepadTextGrid.RowLength(3)); // zxcvbnm,.'
        Assert.Equal(6, GamepadTextGrid.RowLength(4));  // -_@?!/  (ragged: shorter than the rest)
    }

    [Theory]
    [InlineData(1, 0, false, 'q')]
    [InlineData(1, 0, true, 'Q')]
    [InlineData(3, 9, false, '\'')]
    [InlineData(3, 9, true, '\'')] // punctuation is shift-invariant
    [InlineData(0, 0, true, '1')]  // digits are shift-invariant
    [InlineData(4, 5, false, '/')]
    public void CharAtRespectsShiftOnlyForLetters(int row, int col, bool shift, char expected)
    {
        Assert.Equal(expected, GamepadTextGrid.CharAt(row, col, shift));
    }

    // ---- GamepadTextGridCursor ----------------------------------------------------------

    [Fact]
    public void CursorStartsAtOrigin()
    {
        GamepadTextGridCursor cursor = new();
        Assert.Equal(0, cursor.Row);
        Assert.Equal(0, cursor.Col);
    }

    [Fact]
    public void MoveUpClampsAtRowZero()
    {
        GamepadTextGridCursor cursor = new();
        cursor.MoveUp();
        Assert.Equal(0, cursor.Row);
    }

    [Fact]
    public void MoveDownClampsAtTheLastRow()
    {
        GamepadTextGridCursor cursor = new();
        for (int i = 0; i < 10; i++)
        {
            cursor.MoveDown();
        }

        Assert.Equal(GamepadTextGrid.RowCount - 1, cursor.Row);
    }

    [Fact]
    public void MovingIntoAShorterRowClampsTheColumn()
    {
        GamepadTextGridCursor cursor = new();
        for (int i = 0; i < 9; i++)
        {
            cursor.MoveRight(); // row 0 has 10 cells: col -> 9
        }

        Assert.Equal(9, cursor.Col);

        for (int i = 0; i < 4; i++)
        {
            cursor.MoveDown(); // row 4 has only 6 cells
        }

        Assert.Equal(4, cursor.Row);
        Assert.Equal(5, cursor.Col); // clamped to the last valid column, not reset to 0
    }

    [Fact]
    public void MoveRightWrapsAtTheRowsOwnLength()
    {
        GamepadTextGridCursor cursor = new();
        for (int i = 0; i < 4; i++)
        {
            cursor.MoveDown(); // row 4: 6 cells
        }

        for (int i = 0; i < 5; i++)
        {
            cursor.MoveRight();
        }

        Assert.Equal(5, cursor.Col);
        cursor.MoveRight(); // wraps
        Assert.Equal(0, cursor.Col);
    }

    [Fact]
    public void MoveLeftWrapsToTheRowsOwnLastColumn()
    {
        GamepadTextGridCursor cursor = new();
        for (int i = 0; i < 4; i++)
        {
            cursor.MoveDown(); // row 4: 6 cells
        }

        cursor.MoveLeft(); // from col 0, wraps to the last cell of THIS row, not row 0's
        Assert.Equal(5, cursor.Col);
    }

    [Fact]
    public void ResetReturnsToOrigin()
    {
        GamepadTextGridCursor cursor = new();
        cursor.MoveDown();
        cursor.MoveRight();
        cursor.Reset();
        Assert.Equal(0, cursor.Row);
        Assert.Equal(0, cursor.Col);
    }

    // ---- GamepadTextEntryStateMachine: lifecycle -----------------------------------------

    [Fact]
    public void StartsClosed()
    {
        GamepadTextEntryStateMachine machine = new();
        Assert.False(machine.Active);
    }

    [Fact]
    public void OpenResetsPositionAndShift()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Step(new GamepadTextEntryInput(false, false, false, true, false, false, false, false, false, true)); // move + shift
        Assert.True(machine.Shift);
        Assert.Equal(1, machine.Col);

        machine.Open(); // re-open: fresh grid, same as the controller does when a box re-shows
        Assert.True(machine.Active);
        Assert.False(machine.Shift);
        Assert.Equal(0, machine.Row);
        Assert.Equal(0, machine.Col);
    }

    [Fact]
    public void CloseMakesItInactiveAndStepReturnsNone()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Close();
        Assert.False(machine.Active);

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, true, false, false, false, false, false));
        Assert.Equal(GamepadTextEntryCommand.None, result.Command);
    }

    [Fact]
    public void StepOnAClosedMachineDoesNothingEvenForCancel()
    {
        GamepadTextEntryStateMachine machine = new(); // never opened
        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, false, false, false, false, true, false));
        Assert.Equal(GamepadTextEntryCommand.None, result.Command);
    }

    // ---- GamepadTextEntryStateMachine: navigation ----------------------------------------

    [Fact]
    public void UpDownLeftRightMoveTheCursor()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        machine.Step(Nav(right: true));
        Assert.Equal(1, machine.Col);

        machine.Step(Nav(down: true));
        Assert.Equal(1, machine.Row);

        machine.Step(Nav(left: true));
        Assert.Equal(0, machine.Col);

        machine.Step(Nav(up: true));
        Assert.Equal(0, machine.Row);
    }

    [Fact]
    public void ShiftToggleFlipsSelectedLetterCase()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open(); // cursor at row0,col0 = '1' (shift-invariant); move onto a letter first
        machine.Step(Nav(down: true)); // row 1 col 0 = 'q'
        Assert.Equal('q', machine.Selected);

        machine.Step(new GamepadTextEntryInput(false, false, false, false, false, false, false, false, false, true));
        Assert.True(machine.Shift);
        Assert.Equal('Q', machine.Selected);

        machine.Step(new GamepadTextEntryInput(false, false, false, false, false, false, false, false, false, true));
        Assert.False(machine.Shift);
        Assert.Equal('q', machine.Selected);
    }

    // ---- GamepadTextEntryStateMachine: editing commands ------------------------------------

    [Fact]
    public void TypeReturnsTheSelectedCharacterAndStaysOpen()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Step(Nav(down: true)); // 'q'

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, true, false, false, false, false, false));

        Assert.Equal(GamepadTextEntryCommand.Type, result.Command);
        Assert.Equal('q', result.Character);
        Assert.True(machine.Active);
    }

    [Fact]
    public void BackspaceCommandCarriesNoCharacter()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, false, true, false, false, false, false));

        Assert.Equal(GamepadTextEntryCommand.Backspace, result.Command);
        Assert.True(machine.Active);
    }

    [Fact]
    public void SpaceCommandCarriesNoCharacter()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, false, false, true, false, false, false));

        Assert.Equal(GamepadTextEntryCommand.Space, result.Command);
        Assert.True(machine.Active);
    }

    // ---- GamepadTextEntryStateMachine: confirm/cancel --------------------------------------

    [Fact]
    public void ConfirmReturnsSubmitAndCloses()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, false, false, false, true, false, false));

        Assert.Equal(GamepadTextEntryCommand.Submit, result.Command);
        Assert.False(machine.Active);
    }

    [Fact]
    public void CancelReturnsCancelAndCloses()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, false, false, false, false, true, false));

        Assert.Equal(GamepadTextEntryCommand.Cancel, result.Command);
        Assert.False(machine.Active);
    }

    [Fact]
    public void CancelTakesPriorityOverASameFrameType()
    {
        // Real hardware never presses two pad buttons into the SAME edge-triggered frame from
        // one grid action, but the state machine's own priority order is still worth pinning:
        // Cancel/Confirm must never be silently dropped behind an editing command.
        GamepadTextEntryStateMachine machine = new();
        machine.Open();

        GamepadTextEntryResult result = machine.Step(
            new GamepadTextEntryInput(false, false, false, false, true, false, false, false, true, false));

        Assert.Equal(GamepadTextEntryCommand.Cancel, result.Command);
        Assert.False(machine.Active);
    }

    [Fact]
    public void ReopeningAfterCancelStartsFreshAtOrigin()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Step(Nav(right: true));
        machine.Step(Nav(right: true));
        machine.Step(new GamepadTextEntryInput(false, false, false, false, false, false, false, false, true, false)); // cancel
        Assert.False(machine.Active);

        machine.Open(); // the controller re-opens on the next frame if the box is still there
        Assert.Equal(0, machine.Col);
        Assert.True(machine.Active);
    }

    // ---- RenderRow --------------------------------------------------------------------------

    [Fact]
    public void RenderRowBracketsTheSelectedCell()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Step(Nav(right: true)); // row0 col1 = '2'

        string row0 = machine.RenderRow(0);
        Assert.Equal("1 [2] 3 4 5 6 7 8 9 0", row0);
    }

    [Fact]
    public void RenderRowHasNoBracketsOnAnUnselectedRow()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open(); // selection is row 0

        string row1 = machine.RenderRow(1);
        Assert.Equal("q w e r t y u i o p", row1);
        Assert.DoesNotContain('[', row1);
    }

    [Fact]
    public void RenderRowReflectsShift()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        machine.Step(new GamepadTextEntryInput(false, false, false, false, false, false, false, false, false, true)); // shift on

        Assert.Equal("Q W E R T Y U I O P", machine.RenderRow(1));
    }

    [Fact]
    public void RenderRowOfTheRaggedSymbolsRowIsShorter()
    {
        GamepadTextEntryStateMachine machine = new();
        machine.Open();
        Assert.Equal("- _ @ ? ! /", machine.RenderRow(4));
    }

    private static GamepadTextEntryInput Nav(bool up = false, bool down = false, bool left = false, bool right = false) =>
        new(up, down, left, right, false, false, false, false, false, false);
}
