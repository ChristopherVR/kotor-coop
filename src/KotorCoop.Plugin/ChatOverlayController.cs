namespace KotorCoop.Plugin;

/// <summary>
/// An always-on chat readout drawn straight onto the frame, independent of the ~ console, so
/// players can see each other's messages without opening anything. The agent pushes each line
/// into the shared block (and the coop.ini config that governs the overlay); this accumulates
/// the lines with arrival timestamps and renders the recent ones, fading them out after a
/// configurable delay so the screen is clear most of the time.
///
/// Opacity is read from the config and will modulate the draw once an alpha-capable text path
/// is wired (see docs/research/text-color-alpha.md). Until then the fade-out is what keeps the
/// overlay unobtrusive; the text itself is drawn opaque via the verified
/// <see cref="Odyssey.DrawTextAscii"/>. A wrong coordinate only mis-places text, never crashes.
/// </summary>
internal sealed unsafe class ChatOverlayController
{
    private const int Capacity = 16;

    private readonly SharedBlockView _block;
    private readonly byte[][] _lines = new byte[Capacity][];
    private readonly long[] _arrival = new long[Capacity];

    private int _count;
    private uint _lastSerial;

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

        (float x, float topY, float bottomY, bool fromBottom) = Anchor(_block.ChatOverlayAnchor);
        const float step = 6f;

        // Configured opacity as a real alpha, floored so an enabled overlay is never fully
        // invisible. The font color must be restored to white afterwards so the game's own
        // subtitles/feedback text are not tinted.
        float alpha = Math.Clamp(_block.ChatOverlayOpacity / 100f, 0.05f, 1f);
        Odyssey.SetFontColor(1f, 1f, 1f, alpha);

        for (int j = 0; j < vcount; j++)
        {
            // j counts oldest -> newest among the visible lines.
            int slot = visible[vcount - 1 - j];
            float y = fromBottom
                ? bottomY + ((vcount - 1 - j) * step) // newest sits at the bottom
                : topY - (j * step);                  // oldest sits at the top

            fixed (byte* p = _lines[slot])
            {
                Odyssey.DrawTextAscii(p, x, y);
            }
        }

        Odyssey.ResetFontColor();
    }

    /// <summary>
    /// Screen anchor in the console's centered, 0.01-scaled space. Values are reasonable
    /// guesses pending one live calibration pass (same basis as the ~ console overlay).
    /// </summary>
    private static (float X, float TopY, float BottomY, bool FromBottom) Anchor(uint anchor) => anchor switch
    {
        1 => (15f, 86f, -84f, false),   // top-right
        2 => (-95f, 86f, -84f, true),   // bottom-left
        3 => (15f, 86f, -84f, true),    // bottom-right
        _ => (-95f, 86f, -84f, false),  // top-left (default)
    };
}
