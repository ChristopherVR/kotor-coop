namespace KotorCoop.Agent;

/// <summary>
/// The dashboard's transcript store: a bounded ring of raw lines (chat, session events,
/// command echoes/replies, captured stray console output), plus the "is the view following
/// the bottom" bookkeeping a real scrollable view does not track for you on its own.
///
/// Word-wrapping and the actual scrolling are Terminal.Gui's <c>TextView</c> job now (see
/// <see cref="AgentDashboard"/>) — this class only tells the UI glue what to show and whether
/// new content arrived while the viewer was looking at something older, via
/// <see cref="SetFollowing"/> (the UI glue calls this every time the view's own scroll
/// position changes, from either a user action or a programmatic one) and
/// <see cref="NewSinceScrolled"/>. Pure — no Terminal.Gui/Console dependency — so it is
/// unit-testable on its own.
/// </summary>
public sealed class TranscriptBuffer(int capacity = 2000)
{
    /// <summary>Guards every field below: <see cref="Append"/> is called from whichever thread
    /// noticed something worth logging (the pump thread via <c>AgentDashboard.Note</c>, or any
    /// thread that still calls <c>Console.WriteLine</c> and gets captured), while the UI glue
    /// reads <see cref="Lines"/> on the Terminal.Gui main thread. A bare <see cref="List{T}"/>
    /// mutated on one thread while enumerated on another throws; this makes both safe.</summary>
    private readonly Lock _gate = new();

    private readonly List<string> _lines = [];
    private bool _isFollowing = true;
    private int _newSinceScrolled;
    private long _version;

    /// <summary>Bumped by every <see cref="Append"/>/<see cref="Clear"/>, so the UI glue can
    /// redraw only when something changed rather than once per line.</summary>
    public long Version
    {
        get { lock (_gate) { return _version; } }
    }

    public int Count
    {
        get { lock (_gate) { return _lines.Count; } }
    }

    /// <summary>A snapshot of every line currently held, oldest first — what the UI glue joins
    /// into the scrollable view's text. A snapshot rather than a live view, since the caller
    /// reads this on a different thread than <see cref="Append"/> runs on.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) { return [.. _lines]; } }
    }

    public bool IsFollowing
    {
        get { lock (_gate) { return _isFollowing; } }
    }

    /// <summary>How many lines arrived while <see cref="IsFollowing"/> was false — the count a
    /// "N new lines below" marker shows. Reset the moment <see cref="SetFollowing"/> reports
    /// the view is back at the bottom.</summary>
    public int NewSinceScrolled
    {
        get { lock (_gate) { return _newSinceScrolled; } }
    }

    /// <summary>Appends one raw (unwrapped) line, evicting the oldest once past
    /// <paramref name="capacity"/>.</summary>
    public void Append(string line)
    {
        lock (_gate)
        {
            _version++;
            _lines.Add(line);
            while (_lines.Count > capacity)
            {
                _lines.RemoveAt(0);
            }

            if (!_isFollowing)
            {
                _newSinceScrolled++;
            }
        }
    }

    /// <summary>Wipes every held line (the <c>clear</c>/<c>cls</c> command, Ctrl+L) and
    /// resumes following the bottom — an empty transcript has nothing to be scrolled up
    /// away from.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _version++;
            _lines.Clear();
            _isFollowing = true;
            _newSinceScrolled = 0;
        }
    }

    /// <summary>
    /// Tells this buffer whether the view is CURRENTLY showing the bottom. The UI glue calls
    /// this every time the underlying view's scroll position changes — after a user's
    /// PageUp/PageDown/mouse-wheel/End, and after the glue's own programmatic scroll — so this
    /// class never has to know anything about how the view computes its own position.
    /// </summary>
    public void SetFollowing(bool following)
    {
        lock (_gate)
        {
            _isFollowing = following;
            if (following)
            {
                _newSinceScrolled = 0;
            }
        }
    }
}
