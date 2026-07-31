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

    public ChatOverlayController(SharedBlockView block)
    {
        _block = block;
        for (int i = 0; i < Capacity; i++)
        {
            _lines[i] = new byte[SharedBlock.ChatLineLength];
        }
    }

    public void OnFrame()
    {
        // A bumped clear serial wipes the log (the `clear` command).
        uint clear = _block.ChatClearSerial;
        if (clear != _lastClearSerial)
        {
            _lastClearSerial = clear;
            _count = 0;
        }

        Capture();

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
    }

    private void Draw()
    {
        long now = Environment.TickCount64;
        uint fadeMs = _block.ChatOverlayFadeMs;
        int maxLines = (int)Math.Clamp(_block.ChatOverlayMaxLines, 1u, (uint)Capacity);

        // Collect the visible slots newest -> oldest, stopping at the first faded line (older
        // lines arrived earlier, so they are at least as faded).
        Span<int> visible = stackalloc int[Capacity];
        int vcount = 0;
        int oldest = Math.Max(0, _count - Capacity);
        for (int i = _count - 1; i >= oldest && vcount < maxLines; i--)
        {
            int slot = i % Capacity;
            if (fadeMs > 0 && now - _arrival[slot] > fadeMs)
            {
                break;
            }

            visible[vcount++] = slot;
        }

        if (vcount == 0)
        {
            return;
        }

        (int column, int baseRow, bool fromBottom) = Anchor(_block.ChatOverlayAnchor);

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

            fixed (byte* p = _lines[slot])
            {
                Odyssey.PostTextLine(p, column, row, 0.5f);
            }
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    /// <summary>
    /// Screen anchor as a text-grid cell (column, base row). The engine uses rows 0-2 for the
    /// console prompt/line (reliably visible near the top), so the overlay sits just below.
    /// "Bottom" uses a conservative mid-lower row rather than the screen edge, because the grid
    /// height is viewport-derived and a large row can fall off-screen — which is exactly what
    /// hid the overlay before. Wants one live calibration pass; a wrong value only mis-places.
    /// </summary>
    private static (int Column, int BaseRow, bool FromBottom) Anchor(uint anchor) => anchor switch
    {
        // Columns/rows are a text grid derived from the viewport (verified live: left column
        // 3 clears the screen edge; the minimap fills the top-left corner and the party
        // portraits the bottom-left, so the overlay sits in the clear band between them).
        1 => (60, 11, false),  // top-right
        2 => (3, 14, true),    // bottom-left
        3 => (60, 14, true),   // bottom-right
        _ => (3, 11, false),   // top-left (default)
    };
}
