namespace KotorCoop.Plugin;

/// <summary>
/// Quality-of-life for the ~ console while it is open: Up/Down recall submitted lines from
/// <see cref="FuncInterpHook.History"/>, Tab completes the first token (repeated Tab cycles
/// the candidates, Shift+Tab backward), Escape dismisses the hint, and one hint line — the candidate list, or the
/// verb's usage with the argument being typed called out — is drawn just below the console's
/// input rows via the queued text path.
///
/// Completion covers the ENGINE's verbs as well as the co-op ones: the retail command table
/// is read once, on the first frame the console is open, and handed to the completion engine
/// (<see cref="ConsoleCommandTable"/>). The history is loaded from and saved to a file beside
/// the plugin log, so recall survives a restart and not merely a console close.
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
    /// <summary>Where the recall history is kept between runs; null disables persistence.</summary>
    private readonly string? _historyPath;

    /// <summary>How many lines the ring held when it was last written, so a console close
    /// that changed nothing does not rewrite the file.</summary>
    private int _savedCount = -1;

    /// <summary>Read once, lazily: the table is built by static initialisers at load and
    /// never grows during play, and the first console open is the first moment anybody could
    /// want a completion out of it.</summary>
    private bool _engineVerbsLoaded;

    /// <summary>Escape hid the hint; it comes back as soon as the player types.</summary>
    private bool _dismissed;

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
    private bool _escapeWasDown;
    private bool _wasOpen;

    /// <summary>
    /// Snapshot of the engine's console buffer, compared byte-for-byte each frame so the
    /// hint (which costs a string alloc to compute) is rebuilt only when the player actually
    /// typed. The steady state re-posts a cached byte line: allocation-free.
    /// </summary>
    private readonly byte[] _lastLine = new byte[Odyssey.ConsoleBufferCapacity];

    /// <summary>The current hint as a null-terminated ASCII line; empty array = no hint.</summary>
    private byte[] _hintBytes = [];

    /// <param name="historyPath">File to keep the recall history in between runs, or null
    /// for an in-memory-only ring (what the mock and any non-file host get).</param>
    public ConsoleAssistController(string? historyPath = null)
    {
        _historyPath = historyPath;
        LoadHistory();
    }

    public void OnFrame()
    {
        bool up = NativeMethods.IsKeyDownHere(NativeMethods.VkUp);
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkDown);
        bool tab = NativeMethods.IsKeyDownHere(NativeMethods.VkTab);
        bool escape = NativeMethods.IsKeyDownHere(NativeMethods.VkEscape);
        bool upEdge = up && !_upWasDown;
        bool downEdge = down && !_downWasDown;
        bool tabEdge = tab && !_tabWasDown;
        bool escapeEdge = escape && !_escapeWasDown;
        _upWasDown = up;
        _downWasDown = down;
        _tabWasDown = tab;
        _escapeWasDown = escape;

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
                _dismissed = false;
                SaveHistory();
            }

            _wasOpen = false;
            return;
        }

        _wasOpen = true;

        try
        {
            // First open frame: hand the engine's own verbs to the completion engine, so
            // Tab reaches `giveitem` and `revealmap` and not only the co-op verbs.
            if (!_engineVerbsLoaded)
            {
                _engineVerbsLoaded = true;
                _completion.SetEngineVerbs(ConsoleCommandTable.Verbs());
                PluginHost.Log($"console: completion has {ConsoleCommandTable.Diagnostic}");
            }

            if (upEdge && FuncInterpHook.History.Older() is { } older)
            {
                Odyssey.SetConsoleLine(older);
                _completion.Reset(); // a recalled line is not a completion in progress
                _dismissed = false;
            }
            else if (downEdge && FuncInterpHook.History.Newer() is { } newer)
            {
                Odyssey.SetConsoleLine(newer);
                _completion.Reset();
                _dismissed = false;
            }
            else if (escapeEdge)
            {
                // Dismiss: drop the hint and the cycle, leave the typed line alone. The
                // engine may also close the console on this key — either way our state is
                // the state a fresh open would have.
                _completion.Reset();
                _hintBytes = [];
                _dismissed = true;
            }
            else if (tabEdge
                && (NativeMethods.IsKeyDownHere(NativeMethods.VkShift)
                    ? _completion.CompleteReverse(Odyssey.ReadConsoleLine())
                    : _completion.Complete(Odyssey.ReadConsoleLine())) is { } completed)
            {
                Odyssey.SetConsoleLine(completed);
                _dismissed = false;
            }

            if (BufferChanged())
            {
                // Typing un-dismisses: Escape hides the hint for the line as it stands, not
                // for the rest of the session.
                _dismissed = false;
                string hint = _completion.Hint(Odyssey.ReadConsoleLine());
                _hintBytes = EncodeAscii(hint);
            }

            if (!_dismissed && _hintBytes.Length > 0)
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

    /// <summary>
    /// Reads the saved history back at construction. Everything is optional and every
    /// failure is silent-but-logged: a missing file is the normal first run, and a locked or
    /// malformed one must cost the player a recall list, never a launch.
    /// </summary>
    private void LoadHistory()
    {
        if (_historyPath is not { } path)
        {
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            string[] lines = File.ReadAllLines(path);

            // Only the tail can fit the ring, and Restore replays oldest-first, so an
            // over-long file would otherwise leave the ring holding the OLDEST lines.
            int capacity = FuncInterpHook.History.Capacity;
            FuncInterpHook.History.Restore(lines.Length > capacity ? lines[^capacity..] : lines);
            _savedCount = FuncInterpHook.History.Recorded;
        }
        catch (Exception ex)
        {
            PluginHost.Log($"console: could not read the command history ({ex.GetType().Name})");
        }
    }

    /// <summary>Writes the ring out when the console closes, and only when it grew.</summary>
    private void SaveHistory()
    {
        if (_historyPath is not { } path || FuncInterpHook.History.Recorded == _savedCount)
        {
            return;
        }

        try
        {
            File.WriteAllLines(path, FuncInterpHook.History.Snapshot());
            _savedCount = FuncInterpHook.History.Recorded;
        }
        catch (Exception ex)
        {
            PluginHost.Log($"console: could not write the command history ({ex.GetType().Name})");
        }
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
