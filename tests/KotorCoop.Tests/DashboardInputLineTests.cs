using KotorCoop.Agent;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The dashboard's own command-line editor — buffer/caret editing, submit history and the
/// Tab/Shift+Tab completion wiring — driven the same way <see cref="AgentDashboard"/> drives
/// it (one key event at a time), but with no console involved.
/// </summary>
public class DashboardInputLineTests
{
    // ---- typing / caret editing --------------------------------------------------

    [Fact]
    public void TypingAppendsAtTheCaretAndAdvancesIt()
    {
        DashboardInputLine input = new();
        input.TypeChar('h');
        input.TypeChar('o');
        input.TypeChar('s');
        input.TypeChar('t');

        Assert.Equal("host", input.Buffer);
        Assert.Equal(4, input.Caret);
    }

    [Fact]
    public void TypingInsertsAtTheCaretNotAlwaysAtTheEnd()
    {
        DashboardInputLine input = new();
        foreach (char c in "connct")
        {
            input.TypeChar(c);
        }

        input.MoveLeft();
        input.MoveLeft();
        input.TypeChar('e'); // "conn|ct" -> "conne|ct" (fixing the typo)

        Assert.Equal("connect", input.Buffer);
        Assert.Equal(5, input.Caret);
    }

    [Fact]
    public void BackspaceRemovesBeforeTheCaretAndStopsAtZero()
    {
        DashboardInputLine input = new();
        input.TypeChar('h');
        input.TypeChar('i');
        input.Backspace();
        Assert.Equal("h", input.Buffer);
        Assert.Equal(1, input.Caret);

        input.Backspace();
        Assert.Equal(string.Empty, input.Buffer);
        Assert.Equal(0, input.Caret);

        // Nothing left to remove: must not throw or go negative.
        input.Backspace();
        Assert.Equal(string.Empty, input.Buffer);
        Assert.Equal(0, input.Caret);
    }

    [Fact]
    public void DeleteRemovesUnderTheCaretAndStopsAtTheEnd()
    {
        DashboardInputLine input = new();
        foreach (char c in "host")
        {
            input.TypeChar(c);
        }

        input.MoveHome();
        input.Delete(); // removes 'h'
        Assert.Equal("ost", input.Buffer);
        Assert.Equal(0, input.Caret);

        input.MoveEnd();
        input.Delete(); // nothing under the caret at the end
        Assert.Equal("ost", input.Buffer);
    }

    [Fact]
    public void HomeAndEndMoveTheCaretToTheEdges()
    {
        DashboardInputLine input = new();
        foreach (char c in "status")
        {
            input.TypeChar(c);
        }

        input.MoveHome();
        Assert.Equal(0, input.Caret);

        input.MoveEnd();
        Assert.Equal(6, input.Caret);
    }

    [Fact]
    public void LeftAndRightClampAtTheBufferEdges()
    {
        DashboardInputLine input = new();
        input.TypeChar('a');

        input.MoveLeft();
        input.MoveLeft(); // already at 0
        Assert.Equal(0, input.Caret);

        input.MoveRight();
        input.MoveRight(); // already at the end
        Assert.Equal(1, input.Caret);
    }

    [Fact]
    public void EscClearsTheLineAndCaret()
    {
        DashboardInputLine input = new();
        foreach (char c in "connect 10.0.0.5")
        {
            input.TypeChar(c);
        }

        input.ClearLine();

        Assert.Equal(string.Empty, input.Buffer);
        Assert.Equal(0, input.Caret);
    }

    // ---- submit / history -----------------------------------------------------

    [Fact]
    public void SubmitReturnsTheLineAndClearsTheBuffer()
    {
        DashboardInputLine input = new();
        foreach (char c in "status")
        {
            input.TypeChar(c);
        }

        string submitted = input.Submit();

        Assert.Equal("status", submitted);
        Assert.Equal(string.Empty, input.Buffer);
        Assert.Equal(0, input.Caret);
    }

    [Fact]
    public void SubmitOnABlankLineReturnsEmpty()
    {
        DashboardInputLine input = new();
        Assert.Equal(string.Empty, input.Submit());
    }

    [Fact]
    public void UpRecallsTheLastSubmittedLine()
    {
        DashboardInputLine input = new();
        Type(input, "status");
        input.Submit();

        input.HistoryUp();

        Assert.Equal("status", input.Buffer);
        Assert.Equal(6, input.Caret);
    }

    [Fact]
    public void UpThenDownReturnsToAnEmptyLiveLine()
    {
        DashboardInputLine input = new();
        Type(input, "who");
        input.Submit();

        input.HistoryUp();
        Assert.Equal("who", input.Buffer);

        input.HistoryDown();
        Assert.Equal(string.Empty, input.Buffer);
    }

    [Fact]
    public void RepeatedIdenticalSubmitsCostOneHistoryEntry()
    {
        // ConsoleHistoryRing.Record already refuses a line identical to the previous entry —
        // this just proves DashboardInputLine goes through it rather than its own storage.
        DashboardInputLine input = new();
        Type(input, "status");
        input.Submit();
        Type(input, "status");
        input.Submit();
        Type(input, "status");
        input.Submit();

        input.HistoryUp();
        Assert.Equal("status", input.Buffer);

        // A second Up should NOT find a second "status" behind it - there is only one entry.
        input.HistoryUp();
        Assert.Equal("status", input.Buffer);
    }

    [Fact]
    public void HistoryHoldsAtLeastFiftyDistinctEntries()
    {
        DashboardInputLine input = new();
        for (int i = 0; i < 60; i++)
        {
            Type(input, $"tp {i} {i} {i}");
            input.Submit();
        }

        int steps = 0;
        while (steps < 100)
        {
            string before = input.Buffer;
            input.HistoryUp();
            if (input.Buffer == before)
            {
                break; // hit the oldest entry (Older() repeats it, per ConsoleHistoryRing)
            }

            steps++;
        }

        Assert.True(steps >= 50, $"expected at least 50 recallable entries, saw {steps}");
    }

    // ---- completion -------------------------------------------------------------

    [Fact]
    public void TabCompletesAUniqueCoopVerb()
    {
        DashboardInputLine input = new();
        Type(input, "ho");

        input.Complete();

        Assert.Equal("host", input.Buffer);
        Assert.Equal(4, input.Caret);
    }

    [Fact]
    public void TabCyclesForwardAndShiftTabStepsBack()
    {
        DashboardInputLine input = new();
        input.TypeChar('c'); // matches several co-op verbs (connect, census, coop, cheats, ...)

        input.Complete();
        string first = input.Buffer;

        input.Complete();
        string second = input.Buffer;
        Assert.NotEqual(first, second);

        input.Complete(reverse: true);
        Assert.Equal(first, input.Buffer);
    }

    [Fact]
    public void HintReflectsTheCurrentBuffer()
    {
        DashboardInputLine input = new();
        Type(input, "host");

        Assert.Equal("host [password]", input.Hint);
    }

    [Fact]
    public void CandidatesAreEmptyOnceAnArgumentFollows()
    {
        DashboardInputLine input = new();
        Type(input, "host ");

        Assert.Empty(input.Candidates);
    }

    [Fact]
    public void CandidatesListsEveryMatchingCoopVerb()
    {
        DashboardInputLine input = new();
        Type(input, "st"); // status, stat, steamstatus, standup, startdialog, stealth, stats, ...

        Assert.True(input.Candidates.Count >= 6, string.Join(",", input.Candidates));
        Assert.All(input.Candidates, c => Assert.StartsWith("st", c));
    }

    // ---- shared source of truth --------------------------------------------------

    /// <summary>
    /// The whole point of linking ConsoleVerbCatalog.cs into KotorCoop.Agent.csproj by source
    /// (rather than hand-copying the table): the agent's own completion catalog IS the
    /// plugin's. <see cref="KotorCoop.Plugin.ConsoleVerbCatalog"/> is itself linked directly
    /// into this test project (see KotorCoop.Tests.csproj), so this compares two independently
    /// compiled copies of the exact same source file.
    /// </summary>
    [Fact]
    public void AgentCompletionCatalogIsThePluginsOwnTable()
    {
        Assert.Equal(ConsoleVerbCatalog.Entries, DashboardInputLine.Catalog);
    }

    private static void Type(DashboardInputLine input, string text)
    {
        foreach (char c in text)
        {
            input.TypeChar(c);
        }
    }
}
