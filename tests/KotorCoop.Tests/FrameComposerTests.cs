using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Turns a <see cref="DashboardFrameState"/> into exactly <c>height</c> rows for a given
/// window size: header/middle/dropdown-or-hint/input/footer ordering, width clipping, the
/// bottom-priority trim when it does not all fit, and caret placement (including the
/// horizontal scroll a too-long input line needs). No console involved.
/// </summary>
public class FrameComposerTests
{
    private static readonly FrameRow[] Header2 =
    [
        new("heading one", RowKind.Heading),
        new("heading two", RowKind.Plain),
    ];

    [Fact]
    public void ComposeAlwaysReturnsExactlyHeightRows()
    {
        DashboardFrameState state = new() { Header = Header2, Footer = "footer" };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 10);

        Assert.Equal(10, frame.Rows.Count);
    }

    [Fact]
    public void RowsAppearInHeaderThenMiddleThenInputThenFooterOrder()
    {
        DashboardFrameState state = new()
        {
            Header = Header2,
            TranscriptRows = [new FrameRow("transcript line", RowKind.Transcript)],
            InputBuffer = "status",
            InputCaret = 6,
            Footer = "footer line",
        };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 8);
        List<string> texts = [.. frame.Rows.Select(r => r.Text)];

        Assert.Equal("heading one", texts[0]);
        Assert.Equal("heading two", texts[1]);
        Assert.Equal("transcript line", texts[2]);
        Assert.Equal("> status", texts[^2]);
        Assert.Equal("footer line", texts[^1]);
    }

    [Fact]
    public void MiddleHeightIsWhatComposeActuallyGivesTheMiddleRegion()
    {
        DashboardFrameState state = new()
        {
            Header = Header2,
            InputBuffer = string.Empty,
            Footer = "footer",
        };

        int middleHeight = FrameComposer.MiddleHeight(state, width: 40, height: 10);

        // header(2) + input(1) + footer(1) = 4 fixed rows; the rest is the middle region.
        Assert.Equal(10 - 4, middleHeight);
    }

    [Fact]
    public void EveryRowIsClippedToWidthMinusOne()
    {
        DashboardFrameState state = new()
        {
            Header = [new FrameRow(new string('x', 100), RowKind.Heading)],
            Footer = new string('y', 100),
        };

        ComposedFrame frame = FrameComposer.Compose(state, width: 20, height: 5);

        Assert.All(frame.Rows, row => Assert.True(row.Text.Length <= 19, $"row too wide: {row.Text.Length}"));
    }

    [Fact]
    public void DropdownShowsTopSixPlusACountOfTheRest()
    {
        string[] candidates = ["a1", "a2", "a3", "a4", "a5", "a6", "a7", "a8"];
        DashboardFrameState state = new() { DropdownCandidates = candidates };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 20);
        List<string> texts = [.. frame.Rows.Select(r => r.Text.Trim())];

        Assert.Contains("a1", texts);
        Assert.Contains("a6", texts);
        Assert.DoesNotContain("a7", texts);
        Assert.Contains(texts, t => t.Contains("2 more", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleCandidateShowsTheHintInsteadOfADropdown()
    {
        DashboardFrameState state = new()
        {
            DropdownCandidates = ["onlyone"],
            Hint = "onlyone <arg>",
        };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 20);

        Assert.Contains(frame.Rows, r => r.Text == "onlyone <arg>" && r.Kind == RowKind.Hint);
        Assert.DoesNotContain(frame.Rows, r => r.Text.Contains("onlyone", StringComparison.Ordinal) && r.Kind == RowKind.Dropdown);
    }

    [Fact]
    public void CaretLandsOnTheInputRowAtPromptLengthPlusCaret()
    {
        DashboardFrameState state = new() { InputBuffer = "connect", InputCaret = 3 };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 10);

        Assert.True(frame.CaretRow >= 0);
        Assert.Equal("> connect", frame.Rows[frame.CaretRow].Text);
        Assert.Equal(2 + 3, frame.CaretColumn); // "> " is 2 chars
    }

    [Fact]
    public void ALongInputLineScrollsHorizontallyKeepingTheCaretVisible()
    {
        string longBuffer = new('a', 100);
        DashboardFrameState state = new() { InputBuffer = longBuffer, InputCaret = 100 };

        ComposedFrame frame = FrameComposer.Compose(state, width: 30, height: 10);

        string promptRow = frame.Rows[frame.CaretRow].Text;
        Assert.True(promptRow.Length <= 29, $"prompt row too wide: {promptRow.Length}");
        // Caret sits at the end of the visible window, not off the edge.
        Assert.True(frame.CaretColumn <= 29);
        Assert.True(frame.CaretColumn >= 2);
    }

    [Fact]
    public void NoCaretWhenShowInputIsFalse()
    {
        DashboardFrameState state = new() { ShowInput = false, InputBuffer = "ignored" };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 10);

        Assert.Equal(-1, frame.CaretRow);
        Assert.Equal(-1, frame.CaretColumn);
        Assert.DoesNotContain(frame.Rows, r => r.Kind == RowKind.InputPrompt);
    }

    [Fact]
    public void APathologicallyShortWindowKeepsTheInputAndFooterOverTheHeaderAndMiddle()
    {
        DashboardFrameState state = new()
        {
            Header = Header2,
            TranscriptRows = [new FrameRow("t1", RowKind.Transcript), new FrameRow("t2", RowKind.Transcript)],
            InputBuffer = "abc",
            InputCaret = 3,
            Footer = "footer",
        };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 2);

        Assert.Equal(2, frame.Rows.Count);
        Assert.Equal("> abc", frame.Rows[0].Text);
        Assert.Equal("footer", frame.Rows[1].Text);
        Assert.Equal(0, frame.CaretRow);
    }

    [Fact]
    public void DetailsOpenShowsDetailsInsteadOfTranscript()
    {
        DashboardFrameState state = new()
        {
            DetailsOpen = true,
            TranscriptRows = [new FrameRow("should not appear", RowKind.Transcript)],
            Details = [new FrameRow("detail line", RowKind.Plain)],
        };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 10);

        Assert.Contains(frame.Rows, r => r.Text == "detail line");
        Assert.DoesNotContain(frame.Rows, r => r.Text == "should not appear");
    }

    [Fact]
    public void MiddleContentTrimsFromTheTopWhenTooTall()
    {
        List<FrameRow> transcript = [];
        for (int i = 0; i < 20; i++)
        {
            transcript.Add(new FrameRow($"line {i}", RowKind.Transcript));
        }

        DashboardFrameState state = new() { TranscriptRows = transcript, Footer = "footer" };

        ComposedFrame frame = FrameComposer.Compose(state, width: 40, height: 5);

        // height(5) - input(1) - footer(1) = 3 rows of middle content; the LAST 3 survive.
        List<string> texts = [.. frame.Rows.Select(r => r.Text)];
        Assert.Equal(["line 17", "line 18", "line 19", "> ", "footer"], texts);
    }
}
