namespace KotorCoop.Plugin;

/// <summary>
/// An always-on chat readout drawn straight onto the frame, independent of the ~ console, so
/// players can see each other's messages without opening anything. The agent pushes each line
/// into the shared block (and the coop.ini config that governs the overlay); this accumulates
/// the lines with arrival timestamps and renders the recent ones, fading them out after a
/// configurable delay so the screen is clear most of the time.
///
/// Lines are posted through the engine's queued text path (<see cref="Odyssey.PostTextLine"/>),
/// which the engine flushes every frame while the font is bound — so an always-on overlay
/// renders even with the console closed, and the enqueue never touches the font (no crash).
/// Opacity is applied through the queued path's alpha (docs/research/text-render-queued.md).
///
/// <see cref="PrepareLine"/> caps how much of a line is actually posted (see <see cref="Anchor"/>):
/// the engine never clips or wraps text that runs past the true edge of the viewport-derived
/// grid, so a long enough message (or one posted from the never-live-verified right anchors)
/// runs off screen instead — reported as the overlay "clipping". The stored line always keeps
/// the full message; only the on-screen copy is shortened.
/// </summary>
internal sealed unsafe class ChatOverlayController
{
    private const int Capacity = 16;

    private readonly SharedBlockView _block;
    private readonly byte[][] _lines = new byte[Capacity][];
    private readonly long[] _arrival = new long[Capacity];

    private int _count;
    private uint _lastSerial;
    private uint _lastClearSerial;

    // Scrollback: how many lines up from the newest the view is pinned. 0 means "live" (newest
    // at the bottom, fading normally); >0 freezes the window on older lines and shows a scrollbar.
    private int _scrollOffset;
    private bool _pageUpWasDown;
    private bool _pageDownWasDown;

    // A short, reusable scratch buffer for the one-cell scrollbar glyphs.
    private readonly byte[] _barCell = new byte[2];

    // Reusable scratch buffer PrepareLine() writes the (possibly truncated) line into before
    // posting, so truncation costs no per-frame allocation. Sized to the largest stored line.
    private readonly byte[] _drawScratch = new byte[SharedBlock.ChatLineLength];

    public ChatOverlayController(SharedBlockView block)
    {
        _block = block;
        for (int i = 0; i < Capacity; i++)
        {
            _lines[i] = new byte[SharedBlock.ChatLineLength];
        }

        _barCell[1] = 0;
    }

    public void OnFrame()
    {
        // A bumped clear serial wipes the log (the `clear` command).
        uint clear = _block.ChatClearSerial;
        if (clear != _lastClearSerial)
        {
            _lastClearSerial = clear;
            _count = 0;
            _scrollOffset = 0;
        }

        Capture();
        HandleScroll();

        if (!_block.ChatOverlayEnabled || _count == 0)
        {
            return;
        }

        Draw();
    }

    /// <summary>Pulls a newly posted chat line into the ring with its arrival time.</summary>
    private void Capture()
    {
        uint serial = _block.ChatSerial;
        if (serial == _lastSerial)
        {
            return;
        }

        _lastSerial = serial;

        int slot = _count % Capacity;
        byte[] target = _lines[slot];
        byte* source = _block.ChatLine;
        if (*source == ChatMessagesScreenController.InboxMarker)
        {
            source++; // inbox marker (a player chat line); not drawn
        }

        for (int i = 0; i < target.Length; i++)
        {
            target[i] = source[i];
            if (source[i] == 0)
            {
                break;
            }
        }

        target[^1] = 0;
        _arrival[slot] = Environment.TickCount64;
        _count++;

        // If the player is reading scrollback, keep the same lines in view as new ones arrive
        // by pushing the window up one — rather than yanking it to the bottom mid-read.
        if (_scrollOffset > 0)
        {
            _scrollOffset++;
        }
    }

    /// <summary>
    /// Page Up / Page Down scroll the history window; edge-detected so one press moves one line.
    /// Read-only key state (like the Enter capture): it only moves our own view and cannot touch
    /// the game. The offset is clamped so it can never scroll past the oldest stored line, and
    /// Page Down past the newest returns to the live, fading view. See docs/research/chat-input-scroll.md.
    /// </summary>
    private void HandleScroll()
    {
        bool up = NativeMethods.IsKeyDownHere(NativeMethods.VkPageUp);
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkPageDown);

        if (up && !_pageUpWasDown)
        {
            _scrollOffset++;
        }

        if (down && !_pageDownWasDown)
        {
            _scrollOffset--;
        }

        _pageUpWasDown = up;
        _pageDownWasDown = down;

        int maxLines = (int)Math.Clamp(_block.ChatOverlayMaxLines, 1u, (uint)Capacity);
        int stored = Math.Min(_count, Capacity);
        int maxScroll = Math.Max(0, stored - maxLines);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }

    private void Draw()
    {
        long now = Environment.TickCount64;
        uint fadeMs = _block.ChatOverlayFadeMs;
        int maxLines = (int)Math.Clamp(_block.ChatOverlayMaxLines, 1u, (uint)Capacity);
        int stored = Math.Min(_count, Capacity);
        int oldestIdx = _count - stored;
        bool scrolled = _scrollOffset > 0;

        // The bottom-most visible line, walking up to maxLines from there. Live (offset 0) stops
        // at the first faded line so the screen clears; while scrolling, fade is ignored so the
        // history window stays fully readable.
        int bottomIdx = (_count - 1) - _scrollOffset;
        Span<int> visible = stackalloc int[Capacity]; // slots, newest(bottom) -> oldest(top)
        int vcount = 0;
        for (int i = bottomIdx; i >= oldestIdx && vcount < maxLines; i--)
        {
            int slot = i % Capacity;
            if (!scrolled && fadeMs > 0 && now - _arrival[slot] > fadeMs)
            {
                break;
            }

            visible[vcount++] = slot;
        }

        if (vcount == 0)
        {
            return;
        }

        (int column, int baseRow, bool fromBottom, int maxChars) = Anchor(_block.ChatOverlayAnchor);

        // Configured opacity as a real alpha, floored so an enabled overlay is never fully
        // invisible. Restore afterwards so the game's own subtitles/feedback text are opaque.
        float alpha = Math.Clamp(_block.ChatOverlayOpacity / 100f, 0.05f, 1f);
        Odyssey.SetQueuedTextAlpha(alpha);

        for (int j = 0; j < vcount; j++)
        {
            // j counts oldest -> newest among the visible lines.
            int slot = visible[vcount - 1 - j];
            int row = fromBottom
                ? baseRow - (vcount - 1 - j) // newest sits at the bottom row
                : baseRow + j;               // oldest sits at the top row

            PrepareLine(_lines[slot], maxChars);
            fixed (byte* p = _drawScratch)
            {
                Odyssey.PostTextLine(p, column, row, 0.5f);
            }
        }

        // A scrollbar only while actually scrolling back — it marks position in the history and
        // vanishes on return to the live view, keeping the idle overlay clean.
        if (scrolled)
        {
            DrawScrollbar(stored, vcount, bottomIdx, oldestIdx, column, baseRow, fromBottom);
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    /// <summary>
    /// Draws a one-column vertical scrollbar beside the visible lines: a filled thumb (<c>#</c>)
    /// over a track (<c>:</c>), sized and positioned to show how far through the stored history
    /// the window sits. The bar goes one cell to the left of the text (or just right of a
    /// hard-left anchor) so it never overlaps the variable-length message text.
    /// </summary>
    private void DrawScrollbar(int stored, int vcount, int bottomIdx, int oldestIdx, int column, int baseRow, bool fromBottom)
    {
        int topIdx = bottomIdx - (vcount - 1);
        int above = topIdx - oldestIdx;      // stored lines hidden above the window
        int scrollable = stored - vcount;    // total lines that can be hidden either side
        if (scrollable <= 0)
        {
            return;
        }

        int thumbSize = Math.Clamp((int)Math.Round((double)vcount * vcount / stored), 1, vcount);
        int thumbTop = (int)Math.Round((double)(vcount - thumbSize) * above / scrollable);
        int barColumn = column >= 3 ? column - 2 : column + 42;

        for (int r = 0; r < vcount; r++)
        {
            // r counts top(oldest) -> bottom(newest); map to the same screen rows as the text.
            int row = fromBottom ? baseRow - (vcount - 1 - r) : baseRow + r;
            _barCell[0] = (byte)(r >= thumbTop && r < thumbTop + thumbSize ? '#' : ':');
            fixed (byte* p = _barCell)
            {
                Odyssey.PostTextLine(p, barColumn, row, 0.5f);
            }
        }
    }

    /// <summary>
    /// Screen anchor as a text-grid cell (column, base row, whether newest sits at the bottom
    /// row, and how many characters of a line are safe to post from that column). The engine
    /// uses rows 0-2 for the console prompt/line (reliably visible near the top), so the
    /// overlay sits just below. "Bottom" uses a conservative mid-lower row rather than the
    /// screen edge, because the grid height is viewport-derived and a large row can fall
    /// off-screen — which is exactly what hid the overlay before. Wants one live calibration
    /// pass; a wrong value only mis-places (docs/research/console-overlay.md's 2026-09-04
    /// addendum: this rig's own default resolution is 800x600 — no Width/Height set in
    /// swkotor.ini — a smaller viewport than whatever the original anchors were eyeballed
    /// against, and the likeliest source of a reported "clipping" overlay).
    /// </summary>
    private static (int Column, int BaseRow, bool FromBottom, int MaxChars) Anchor(uint anchor) => anchor switch
    {
        // Columns/rows are a text grid derived from the viewport (verified live: left column
        // 3 clears the screen edge; the minimap fills the top-left corner and the party
        // portraits the bottom-left, so the overlay sits in the clear band between them). The
        // right-side column was NEVER live-verified the same way (no on-screen landmark to
        // check it against) — tightened from an earlier guess of 60 to 50, and given a much
        // smaller character budget than the left anchors, purely as a conservative margin
        // until a future session can calibrate it against a real screen.
        1 => (50, 11, false, RightMaxChars),  // top-right
        2 => (3, 14, true, LeftMaxChars),     // bottom-left
        3 => (50, 14, true, RightMaxChars),   // bottom-right
        _ => (3, 11, false, LeftMaxChars),    // top-left (default)
    };

    // Nothing in the engine's queued-text path clips or wraps a line that runs past the true
    // right edge of the viewport-derived grid (docs/research/text-render-queued.md) — it just
    // draws invisible off-screen glyphs, which is what "clipping" looks like to a player. These
    // caps are a resolution-independent safety net (a shorter line is never MORE likely to run
    // off an edge than a longer one, at any resolution), not a pixel-accurate fix: PrepareLine
    // truncates the ON-SCREEN copy only, never the stored line, so scrollback/history keep the
    // full message.
    private const int LeftMaxChars = 60;
    private const int RightMaxChars = 32;

    /// <summary>
    /// Copies at most <paramref name="maxChars"/> characters of <paramref name="source"/> (a
    /// nul-terminated line up to <see cref="SharedBlock.ChatLineLength"/> bytes) into
    /// <see cref="_drawScratch"/>, nul-terminated, replacing the last three characters with
    /// <c>"..."</c> when the line had to be cut short. Allocation-free: reuses the same scratch
    /// buffer every call, which is safe because <see cref="Odyssey.PostTextLine"/> copies the
    /// bytes into the engine's own node before this is called again next frame.
    /// </summary>
    private void PrepareLine(byte[] source, int maxChars)
    {
        int len = 0;
        while (len < source.Length && len < _drawScratch.Length - 1 && source[len] != 0)
        {
            len++;
        }

        if (len <= maxChars)
        {
            Array.Copy(source, _drawScratch, len);
            _drawScratch[len] = 0;
            return;
        }

        int keep = Math.Max(0, maxChars - 3);
        Array.Copy(source, _drawScratch, keep);
        _drawScratch[keep] = (byte)'.';
        _drawScratch[keep + 1] = (byte)'.';
        _drawScratch[keep + 2] = (byte)'.';
        _drawScratch[keep + 3] = 0;
    }
}
