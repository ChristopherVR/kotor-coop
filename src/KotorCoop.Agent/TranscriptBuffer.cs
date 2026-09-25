namespace KotorCoop.Agent;

/// <summary>
/// The dashboard's scrolling transcript: chat, session events, `&gt; command` echoes/replies
/// and captured stray output (anything a component still sends through
/// <c>Console.WriteLine</c>/<c>Console.Error</c> while the TUI owns the screen — see
/// <see cref="ConsoleCaptureWriter"/>). Word-wraps to a given width on demand (never stored
/// pre-wrapped, since the terminal can resize at any time), keeps a bounded number of RAW
/// lines, and tracks whether the view is "following" the bottom (new lines pull the view down,
/// the normal case) or has been scrolled up — in which case new lines are counted instead of
/// yanking the view away from what is being read, surfaced as <see cref="NewSinceScrolled"/>
/// for a "N new" marker. Pure — no <c>Console.*</c> call anywhere in this file — so it is
/// unit-testable without a terminal.
/// </summary>
public sealed class TranscriptBuffer(int capacity = 2000)
{
    private readonly List<string> _lines = [];

    /// <summary>Wrapped-row cache, rebuilt lazily when the width changes or a line is
    /// appended/evicted — resizing/typing happens far more often than the transcript grows,
    /// so paying the wrap cost only when something actually invalidates it matters.</summary>
    private readonly List<string> _wrapped = [];
    private int _wrapWidth = -1;
    private bool _wrapDirty = true;

    /// <summary>Rows scrolled up from the very bottom, in WRAPPED-row units at whatever width
    /// the last scroll action used. 0 = following. Resizing can leave this slightly
    /// approximate against the new width (re-clamped, not re-derived) — acceptable for a
    /// console scrollback, not worth tracking a raw-line anchor for.</summary>
    private int _scrollFromBottom;

    private int _newSinceScrolled;

    public int Count => _lines.Count;

    public bool IsFollowing => _scrollFromBottom == 0;

    public int NewSinceScrolled => _newSinceScrolled;

    /// <summary>Appends one raw (unwrapped) line, evicting the oldest once past
    /// <paramref name="capacity"/>. While scrolled up, this only counts toward
    /// <see cref="NewSinceScrolled"/> — the viewport does not move.</summary>
    public void Append(string line)
    {
        _lines.Add(line);
        while (_lines.Count > capacity)
        {
            _lines.RemoveAt(0);
        }

        _wrapDirty = true;

        if (!IsFollowing)
        {
            _newSinceScrolled++;
        }
    }

    /// <summary>Up to <paramref name="height"/> wrapped rows for the current scroll position,
    /// oldest first — exactly what a viewport of that size should show right now. Fewer than
    /// <paramref name="height"/> rows when the transcript itself is shorter; never more.</summary>
    public IReadOnlyList<string> VisibleRows(int width, int height)
    {
        EnsureWrapped(Math.Max(width, 1));
        int total = _wrapped.Count;
        int h = Math.Max(height, 0);

        int end = Math.Max(total - _scrollFromBottom, 0);
        int start = Math.Max(end - h, 0);
        return _wrapped.GetRange(start, end - start);
    }

    /// <summary>The wrapped-row count at the given width — what a caller clamps a scroll
    /// offset against, and what tests assert wrapping against directly.</summary>
    public int WrappedCount(int width)
    {
        EnsureWrapped(Math.Max(width, 1));
        return _wrapped.Count;
    }

    public void ScrollUp(int rows, int width, int height) => SetScroll(_scrollFromBottom + Math.Max(rows, 0), width, height);

    public void ScrollDown(int rows, int width, int height) => SetScroll(_scrollFromBottom - Math.Max(rows, 0), width, height);

    public void ScrollToTop(int width, int height) => SetScroll(int.MaxValue, width, height);

    public void ScrollToBottom()
    {
        _scrollFromBottom = 0;
        _newSinceScrolled = 0;
    }

    private void SetScroll(int requested, int width, int height)
    {
        EnsureWrapped(Math.Max(width, 1));
        int max = Math.Max(_wrapped.Count - Math.Max(height, 0), 0);
        _scrollFromBottom = Math.Clamp(requested, 0, max);
        if (_scrollFromBottom == 0)
        {
            _newSinceScrolled = 0;
        }
    }

    private void EnsureWrapped(int width)
    {
        if (!_wrapDirty && _wrapWidth == width)
        {
            return;
        }

        _wrapped.Clear();
        foreach (string line in _lines)
        {
            WrapInto(line, width, _wrapped);
        }

        _wrapWidth = width;
        _wrapDirty = false;
    }

    /// <summary>Greedy word-wrap: break at the last space within <paramref name="width"/>
    /// characters when one exists, otherwise hard-break at the width. An empty line still
    /// produces one (empty) row, so a blank line in the transcript stays a blank row rather
    /// than disappearing.</summary>
    private static void WrapInto(string line, int width, List<string> into)
    {
        if (width <= 0 || line.Length <= width)
        {
            into.Add(line);
            return;
        }

        int start = 0;
        while (start < line.Length)
        {
            int remaining = line.Length - start;
            if (remaining <= width)
            {
                into.Add(line[start..]);
                break;
            }

            int hardBreak = start + width;
            int lastSpace = line.LastIndexOf(' ', hardBreak - 1, hardBreak - start);
            int cut = lastSpace > start ? lastSpace : hardBreak;

            into.Add(line[start..cut]);
            start = cut;
            while (start < line.Length && line[start] == ' ')
            {
                start++;
            }
        }
    }
}
