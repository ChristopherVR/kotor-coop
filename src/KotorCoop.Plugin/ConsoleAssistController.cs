namespace KotorCoop.Plugin;

/// <summary>
/// Quality-of-life for the ~ console while it is open: Up/Down recall submitted lines from
/// <see cref="FuncInterpHook.History"/>, Tab completes the first token against the co-op
/// verb catalog, and one hint line (candidates, or the completed verb's usage) is drawn
/// just below the console's input rows via the queued text path.
///
/// Runs on the MAIN-LOOP hook (see PluginHost.MenuFrame), not the SwapBuffers frame hook:
/// the console opens at the main menu as well as in-world, and the swap hook is dormant
/// behind the menu movie — the same reason the menu-load replay lives there. ChatInput and
/// ChatOverlay stay on SwapBuffers because they are in-world features; this one follows the
/// console, which exists in both places.
///
/// Everything here is read-only key state plus the two console globals the engine itself
/// edits as the player types (buffer + caret), so the worst a bug can do is put the wrong
/// text on the command line. The engine keeps owning the buffer after every rewrite, which
/// is why "recall then keep typing" needs no extra work.
/// </summary>
internal sealed unsafe class ConsoleAssistController
{
    // The engine renders the console prompt/input on the top text-grid rows (0-2, per the
    // ChatOverlayController anchor notes), so the hint sits on row 3, just below the input,
    // at the same left column the overlay verified clears the screen edge.
    private const int HintColumn = 3;
    private const int HintRow = 3;

    // Short life (~2 frames at 0.1s/unit-ish): re-posted every frame while the console is
    // open, so it stays up; the moment the console closes the re-posting stops and the line
    // expires almost immediately instead of lingering over gameplay.
    private const float HintLife = 0.2f;

    // Slightly translucent so the hint reads as annotation, not as console output. Set and
    // reset around the post per the queued-text alpha discipline (text-render-queued.md).
    private const float HintAlpha = 0.75f;

    private readonly ConsoleCompletionEngine _completion = new(ConsoleVerbCatalog.Entries);

    // Edge-detection latches, sampled every frame (even console-closed) so a key held down
    // while the console opens does not read as a fresh press on the first open frame.
    private bool _upWasDown;
    private bool _downWasDown;
    private bool _tabWasDown;
    private bool _wasOpen;

    /// <summary>
    /// Snapshot of the engine's console buffer, compared byte-for-byte each frame so the
    /// hint (which costs a string alloc to compute) is rebuilt only when the player actually
    /// typed. The steady state re-posts a cached byte line: allocation-free.
    /// </summary>
    private readonly byte[] _lastLine = new byte[Odyssey.ConsoleBufferCapacity];

    /// <summary>The current hint as a null-terminated ASCII line; empty array = no hint.</summary>
    private byte[] _hintBytes = [];

    public void OnFrame()
    {
        bool up = NativeMethods.IsKeyDownHere(NativeMethods.VkUp);
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkDown);
        bool tab = NativeMethods.IsKeyDownHere(NativeMethods.VkTab);
        bool upEdge = up && !_upWasDown;
        bool downEdge = down && !_downWasDown;
        bool tabEdge = tab && !_tabWasDown;
        _upWasDown = up;
        _downWasDown = down;
        _tabWasDown = tab;

        if (!Odyssey.IsConsoleOpen)
        {
            if (_wasOpen)
            {
                // Console just closed: next open starts at live history, a fresh completion
                // cycle, and a forced hint recompute (0xFF can never appear in the ASCII
                // buffer, so the first open frame always mismatches the snapshot).
                FuncInterpHook.History.ResetCursor();
                _completion.Reset();
                _hintBytes = [];
                _lastLine[0] = 0xFF;
            }

            _wasOpen = false;
            return;
        }

        _wasOpen = true;

        try
        {
            if (upEdge && FuncInterpHook.History.Older() is { } older)
            {
                Odyssey.SetConsoleLine(older);
                _completion.Reset(); // a recalled line is not a completion in progress
            }
            else if (downEdge && FuncInterpHook.History.Newer() is { } newer)
            {
                Odyssey.SetConsoleLine(newer);
                _completion.Reset();
            }
            else if (tabEdge && _completion.Complete(Odyssey.ReadConsoleLine()) is { } completed)
            {
                Odyssey.SetConsoleLine(completed);
            }

            if (BufferChanged())
            {
                string hint = _completion.Hint(Odyssey.ReadConsoleLine());
                _hintBytes = EncodeAscii(hint);
            }

            if (_hintBytes.Length > 0)
            {
                Odyssey.SetQueuedTextAlpha(HintAlpha);
                fixed (byte* p = _hintBytes)
                {
                    Odyssey.PostTextLine(p, HintColumn, HintRow, HintLife);
                }

                Odyssey.ResetQueuedTextAlpha();
            }
        }
        catch
        {
            // Never let console assistance throw into the engine's main loop. The worst
            // outcome of swallowing here is a missing hint or a recall that did not land.
        }
    }

    /// <summary>
    /// True when the engine buffer differs from the snapshot (updating the snapshot as it
    /// compares). Both sides stop meaning anything at their first null, so stale snapshot
    /// bytes beyond a new, shorter line never produce a false positive.
    /// </summary>
    private bool BufferChanged()
    {
        byte* live = Odyssey.ConsoleBuffer;
        bool changed = false;
        for (int i = 0; i < _lastLine.Length; i++)
        {
            byte b = live[i];
            if (_lastLine[i] != b)
            {
                changed = true;
                _lastLine[i] = b;
            }

            if (b == 0)
            {
                break;
            }
        }

        return changed;
    }

    private static byte[] EncodeAscii(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        byte[] bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        bytes[^1] = 0;
        return bytes;
    }
}
