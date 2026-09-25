namespace KotorCoop.Agent;

/// <summary>
/// What a row of the dashboard means, for colouring at render time (<see cref="AgentDashboard"/>
/// maps this to a VT SGR code) — kept separate from the text itself so this class (and its
/// tests) never need to know an escape sequence.
/// </summary>
public enum RowKind
{
    Plain,
    Heading,
    Dim,
    Ok,
    Fail,
    Transcript,
    Dropdown,
    Hint,
    InputPrompt,
    Footer,
}

/// <summary>One row of composed screen text with its colouring intent.</summary>
public readonly record struct FrameRow(string Text, RowKind Kind);

/// <summary>
/// Everything <see cref="FrameComposer"/> needs to lay out one frame. Callers own deciding
/// WHAT goes in each section (header content, whether the details panel or the transcript
/// fills the middle, the transcript's already-wrapped visible slice) — this only ever arranges
/// rows already provided, header first, to fit the window.
/// </summary>
public sealed record DashboardFrameState
{
    /// <summary>2-4 short status lines. Capped by the caller, not by the composer.</summary>
    public IReadOnlyList<FrameRow> Header { get; init; } = [];

    /// <summary>True while F1's details panel replaces the transcript in the middle region.</summary>
    public bool DetailsOpen { get; init; }

    /// <summary>The full details-panel content; only the tail that fits is shown.</summary>
    public IReadOnlyList<FrameRow> Details { get; init; } = [];

    /// <summary>The transcript's already-wrapped, already-sized-to-fit visible slice (from
    /// <see cref="TranscriptBuffer.VisibleRows"/>) — the composer does not re-wrap or re-slice
    /// it, only clips to width as a safety net.</summary>
    public IReadOnlyList<FrameRow> TranscriptRows { get; init; } = [];

    /// <summary>False for a redirected/non-interactive run: no dropdown, hint, input row or
    /// caret, just header/details-or-transcript/footer.</summary>
    public bool ShowInput { get; init; } = true;

    /// <summary>Ranked completion candidates for whatever is typed right now; more than one
    /// draws the dropdown (top 6 + "N more") instead of <see cref="Hint"/>.</summary>
    public IReadOnlyList<string> DropdownCandidates { get; init; } = [];

    /// <summary>The single hint line, shown when <see cref="DropdownCandidates"/> has at most
    /// one entry.</summary>
    public string Hint { get; init; } = string.Empty;

    public string InputBuffer { get; init; } = string.Empty;

    public int InputCaret { get; init; }

    /// <summary>One key-legend line, drawn last; empty means none.</summary>
    public string Footer { get; init; } = string.Empty;
}

/// <summary>The laid-out frame: exactly <c>height</c> rows, plus where the real caret belongs
/// (-1 for "hide it" — no input row survived a pathologically short window, or
/// <see cref="DashboardFrameState.ShowInput"/> is false).</summary>
public sealed class ComposedFrame
{
    public required IReadOnlyList<FrameRow> Rows { get; init; }
    public int CaretRow { get; init; } = -1;
    public int CaretColumn { get; init; } = -1;
}

/// <summary>
/// Turns a <see cref="DashboardFrameState"/> into exactly <c>height</c> rows of at most
/// <c>width - 1</c> characters each (one column held back everywhere, not just the last row —
/// simpler than special-casing, and the on-screen bug this whole rework replaced was traced to
/// an off-by-one at exactly this edge). Pure — no <c>Console.*</c>, no VT escape codes — so
/// layout/wrapping/priority decisions are unit-testable without a terminal.
///
/// Layout, top to bottom: header, then the middle region (details panel OR transcript, each
/// filling whatever height remains), then — only when <see cref="DashboardFrameState.ShowInput"/>
/// is true — the completion dropdown or hint, the input prompt row, then the footer. When
/// everything together does not fit a pathologically short window, the LAST <c>height</c> rows
/// win: input box and footer survive, header and the middle region get cut first — the same
/// bottom-pinning priority the dashboard already established before this rework.
/// </summary>
public static class FrameComposer
{
    private const int MaxDropdownRows = 6;
    private const string Prompt = "> ";

    public static ComposedFrame Compose(DashboardFrameState state, int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        int contentWidth = Math.Max(width - 1, 1);

        List<FrameRow> rows = [];
        foreach (FrameRow row in state.Header)
        {
            rows.Add(Clip(row, contentWidth));
        }

        int middleHeight = MiddleHeight(state, width, height);
        IReadOnlyList<FrameRow> middleSource = state.DetailsOpen ? state.Details : state.TranscriptRows;

        // Bottom-align: both sources put what matters most at the END of their own list
        // (the transcript is oldest-first with newest at the bottom; the details panel's
        // own tail — SHARED WORLD, items, last drop — is what a live check usually wants),
        // so when it does not all fit, trim from the TOP.
        int take = Math.Min(middleSource.Count, middleHeight);
        int skip = Math.Max(middleSource.Count - middleHeight, 0);
        for (int i = 0; i < take; i++)
        {
            rows.Add(Clip(middleSource[skip + i], contentWidth));
        }

        while (rows.Count < state.Header.Count + middleHeight)
        {
            rows.Add(new FrameRow(string.Empty, RowKind.Plain));
        }

        int caretRow = -1;
        int caretColumn = -1;

        if (state.ShowInput)
        {
            foreach (FrameRow row in DropdownOrHintRows(state))
            {
                rows.Add(Clip(row, contentWidth));
            }

            int available = Math.Max(contentWidth - Prompt.Length, 1);
            (string visible, int hOffset) = WindowBuffer(state.InputBuffer, state.InputCaret, available);
            rows.Add(new FrameRow(Prompt + visible, RowKind.InputPrompt));
            caretRow = rows.Count - 1;
            caretColumn = Prompt.Length + (state.InputCaret - hOffset);
        }

        if (state.Footer.Length > 0)
        {
            rows.Add(Clip(new FrameRow(state.Footer, RowKind.Footer), contentWidth));
        }

        if (rows.Count > height)
        {
            int removed = rows.Count - height;
            rows = rows.GetRange(removed, height);

            if (caretRow >= 0)
            {
                caretRow -= removed;
                if (caretRow < 0)
                {
                    caretRow = -1;
                    caretColumn = -1;
                }
            }
        }

        while (rows.Count < height)
        {
            rows.Add(new FrameRow(string.Empty, RowKind.Plain));
        }

        return new ComposedFrame { Rows = rows, CaretRow = caretRow, CaretColumn = caretColumn };
    }

    /// <summary>
    /// How tall the middle region (details panel or transcript) will be for this state at this
    /// width/height — what a caller asks BEFORE composing, so it can size
    /// <see cref="TranscriptBuffer.VisibleRows"/> to exactly that instead of guessing and
    /// hoping <see cref="Compose"/> agrees. <see cref="Compose"/> uses this exact same method
    /// internally, so the two can never disagree.
    /// </summary>
    public static int MiddleHeight(DashboardFrameState state, int width, int height)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);

        int bottom = 0;
        if (state.ShowInput)
        {
            bottom += DropdownOrHintRows(state).Count;
            bottom += 1; // the input prompt row itself
        }

        if (state.Footer.Length > 0)
        {
            bottom += 1;
        }

        return Math.Max(height - state.Header.Count - bottom, 0);
    }

    private static List<FrameRow> DropdownOrHintRows(DashboardFrameState state)
    {
        List<FrameRow> rows = [];

        if (state.DropdownCandidates.Count > 1)
        {
            int shown = Math.Min(state.DropdownCandidates.Count, MaxDropdownRows);
            for (int i = 0; i < shown; i++)
            {
                rows.Add(new FrameRow("  " + state.DropdownCandidates[i], RowKind.Dropdown));
            }

            if (state.DropdownCandidates.Count > MaxDropdownRows)
            {
                rows.Add(new FrameRow($"  ... {state.DropdownCandidates.Count - MaxDropdownRows} more", RowKind.Dim));
            }
        }
        else if (state.Hint.Length > 0)
        {
            rows.Add(new FrameRow(state.Hint, RowKind.Hint));
        }

        return rows;
    }

    /// <summary>Horizontal scroll for an input line longer than the available width: keeps the
    /// caret in view, biased toward showing what is just BEHIND it (typing at the end is the
    /// common case, and should keep showing the tail, not snap to the start).</summary>
    private static (string Visible, int Offset) WindowBuffer(string buffer, int caret, int available)
    {
        if (buffer.Length <= available)
        {
            return (buffer, 0);
        }

        int maxOffset = Math.Max(buffer.Length - available, 0);
        int offset = Math.Clamp(caret - available + 1, 0, maxOffset);
        int length = Math.Min(available, buffer.Length - offset);
        return (buffer.Substring(offset, length), offset);
    }

    private static FrameRow Clip(FrameRow row, int width) =>
        row.Text.Length <= width ? row : row with { Text = row.Text[..width] };
}
