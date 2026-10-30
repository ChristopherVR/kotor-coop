namespace KotorCoop.Plugin;

/// <summary>
/// An in-game server browser panel: lists the LAN co-op servers the agent has discovered, lets
/// the player move a highlight through them and confirm one to connect, and can hand off to a
/// password prompt. Opened by the main-menu Co-op button (or F9 in a loaded game).
///
/// It is drawn with the engine's queued text path (<see cref="Odyssey.PostTextLine"/>) rather
/// than built out of engine GUI controls. That is deliberate: constructing GUI controls at
/// runtime means hand-building CExoArrayLists and feeding them to layout code whose ABI we would
/// be guessing (see the crash-risk register in docs/research/server-browser.md), whereas the
/// queued text path is already proven — it is how the chat overlay renders, and it renders at
/// the MAIN MENU, which is exactly where a browser has to work. Nothing here calls an engine
/// function that can fault: it posts strings and reads block fields.
///
/// Runs from <see cref="MainLoopHook"/>, the callback that fires at the menu as well as in-world
/// (the SwapBuffers frame hook is dormant at the menu).
/// </summary>
internal sealed unsafe class ServerBrowserController
{
    private const int Column = 8;
    private const int TitleRow = 6;
    private const int FirstEntryRow = 8;

    /// <summary>Redraw life in seconds; re-posted every frame, so short is fine.</summary>
    private const float LineLife = 0.5f;

    private readonly SharedBlockView _block;
    private readonly byte[] _line = new byte[96];

    private bool _open;
    private int _selection;
    private uint _shownListSerial;
    private string[] _shownHosts = [];
    private string? _selectedHost;
    private int _selectedAction = -1;
    private bool _upWasDown, _downWasDown, _enterWasDown, _escWasDown, _openWasDown;

    /// <summary>Set by the main-menu Co-op button, which runs on the same (main) thread.</summary>
    private static bool _openRequested;
    private static ServerBrowserController? _instance;

    public static bool IsOpen => _instance?._open ?? false;

    public ServerBrowserController(SharedBlockView block)
    {
        _block = block;
        _instance = this;
    }

    /// <summary>
    /// Opens the panel from elsewhere in the plugin — the main-menu Co-op button. The button
    /// deliberately opens the BROWSER rather than bumping the connect serial: bumping it made the
    /// agent connect straight from coop.ini, which would race the player's pick.
    /// </summary>
    public static void RequestOpen() => _openRequested = true;

    public void Invalidate() => Close();

    public void OnFrame()
    {
        // At the main menu the NATIVE panel (NativeBrowserController) is the browser now;
        // this overlay serves the in-world F9 toggle (no main menu exists there for a native
        // panel to attach to) and remains the fallback if a native guard latched it off.
        if (!Odyssey.HasServer && NativeBrowserController.Available)
        {
            return;
        }

        if (!_ticked)
        {
            _ticked = true;
            PluginHost.Log("browser: controller ticking");
        }

        if (_openRequested)
        {
            _openRequested = false;
            Open();
        }

        // F9 toggles it in a loaded game, where there is no menu button.
        bool openKey = NativeMethods.IsKeyDownHere(NativeMethods.VkF9);
        if (openKey && !_openWasDown)
        {
            if (_open)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        _openWasDown = openKey;

        // The BLOCK is authoritative for "is the panel up", so the agent (or a diagnostic) can
        // open it too, not just our own key handling.
        bool blockOpen = _block.BrowserOpen != 0;
        if (blockOpen != _open)
        {
            _open = blockOpen;
            if (_open)
            {
                InitializeSelection();
                PluginHost.Log("browser: opened");
            }
        }

        if (!_open)
        {
            return;
        }

        HandleKeys();

        if (_open)
        {
            Draw();
        }
    }

    private bool _ticked;

    private void Open()
    {
        _open = true;
        InitializeSelection();
        _block.BrowserOpen = 1;
        _block.BrowserSelection = 0;
    }

    private void Close()
    {
        _open = false;
        _block.BrowserOpen = 0;
    }

    private void InitializeSelection()
    {
        _selection = 0;
        _shownListSerial = _block.BrowserListSerial;
        _shownHosts = ReadHostAddresses();
        RememberSelection();
        _enterWasDown = NativeMethods.IsKeyDownHere(NativeMethods.VkReturn);
    }

    private string[] ReadHostAddresses()
    {
        int count = (int)Math.Min(_block.BrowserCount, (uint)SharedBlock.BrowserCapacity);
        string[] addresses = new string[count];
        for (int i = 0; i < count; i++)
        {
            byte* start = _block.BrowserEntry(i) + SharedBlock.BrowserEntryAddress;
            int length = 0;
            while (length < SharedBlock.BrowserEntryAddressLength && start[length] != 0) length++;
            addresses[i] = System.Text.Encoding.ASCII.GetString(start, length);
        }
        return addresses;
    }

    private void RememberSelection()
    {
        _selectedHost = _selection >= 0 && _selection < _shownHosts.Length ? _shownHosts[_selection] : null;
        _selectedAction = _selection >= _shownHosts.Length ? _selection - _shownHosts.Length : -1;
    }

    private void HandleKeys()
    {
        int count = (int)Math.Min(_block.BrowserCount, (uint)SharedBlock.BrowserCapacity);

        bool up = NativeMethods.IsKeyDownHere(NativeMethods.VkUp);
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkDown);
        bool enter = NativeMethods.IsKeyDownHere(NativeMethods.VkReturn);
        bool esc = NativeMethods.IsKeyDownHere(NativeMethods.VkEscape);

        // Beacon updates alone must not reset navigation. Preserve the selected host
        // by endpoint, or the action by its fixed index when the host count changes.
        if (_shownListSerial != _block.BrowserListSerial || count != _shownHosts.Length)
        {
            _shownListSerial = _block.BrowserListSerial;
            _shownHosts = ReadHostAddresses();
            count = _shownHosts.Length;
            var resolved = CoopMenuActions.ResolveSelection(_selectedHost, _selectedAction, _shownHosts);
            _selection = resolved.Index;
            if (!resolved.Preserved) _enterWasDown = enter;
        }

        if (up && !_upWasDown && _selection > 0)
        {
            _selection--;
        }

        int total = count + CoopMenuActions.Count;
        if (down && !_downWasDown && _selection < total - 1)
        {
            _selection++;
        }

        if (esc && !_escWasDown)
        {
            Close();
        }
        else if (enter && !_enterWasDown && _selection >= 0 && _selection < total)
        {
            ChatInputController.ConsumeMenuEnter();
            // Selection first, then the serial: the agent reads the serial last, so it can never
            // see a bumped serial paired with a stale selection.
            if (_selection < count)
            {
                _block.BrowserSelection = (uint)_selection;
                _block.RequestBrowserConnect();
            }
            else
            {
                _block.PublishMenuCommand(CoopMenuActions.Command(_selection - count));
            }
            Close();
        }

        _upWasDown = up;
        _downWasDown = down;
        _enterWasDown = enter;
        _escWasDown = esc;

        if (_selection >= total)
        {
            _selection = total - 1;
        }

        _block.BrowserSelection = (uint)_selection;
        RememberSelection();
    }

    private void Draw()
    {
        int count = (int)Math.Min(_block.BrowserCount, (uint)SharedBlock.BrowserCapacity);

        Odyssey.SetQueuedTextAlpha(1f);
        Post("=== CO-OP ===", Column, TitleRow);

        if (count == 0)
        {
            Post("No nearby hosts yet. Choose an action below.", Column, TitleRow + 1);
        }

        int total = count + CoopMenuActions.Count;
        int start = CoopMenuActions.WindowStart(total, _selection);
        int end = Math.Min(total, start + CoopMenuActions.VisibleRows);
        if (count > 0) Post("Nearby hosts and session actions - Up/Down to scroll", Column, TitleRow + 1);
        for (int i = start; i < Math.Min(count, end); i++)
        {
            byte* entry = _block.BrowserEntry(i);
            int n = 0;

            // "> 1. Name   address   (players/max)" — the caret marks the highlighted row.
            n = Append(n, i == _selection ? "> " : "  ");
            n = Append(n, (char)('1' + i));
            n = Append(n, ". ");
            n = AppendField(n, entry + SharedBlock.BrowserEntryName, SharedBlock.BrowserEntryNameLength, 16);
            n = Append(n, " ");
            n = AppendField(n, entry + SharedBlock.BrowserEntryAddress, SharedBlock.BrowserEntryAddressLength, 22);
            n = Append(n, " (");
            n = Append(n, entry[SharedBlock.BrowserEntryPlayers]);
            n = Append(n, '/');
            n = Append(n, entry[SharedBlock.BrowserEntryMaxPlayers]);
            n = Append(n, ')');

            // A locked host asks for a password after Connect; say so up front.
            if (entry[SharedBlock.BrowserEntryLocked] != 0)
            {
                n = Append(n, " *");
            }

            _line[n] = 0;

            fixed (byte* p = _line)
            {
                Odyssey.PostTextLine(p, Column, FirstEntryRow + i - start, LineLife);
            }
        }

        for (int action = 0; action < CoopMenuActions.Count; action++)
        {
            int row = count + action;
            if (row >= start && row < end)
                Post((_selection == row ? "> " : "  ") + CoopMenuActions.Caption(action), Column, FirstEntryRow + row - start);
        }
        Post("  Up/Down select   Enter confirm   Esc close", Column, FirstEntryRow + CoopMenuActions.VisibleRows + 1);
        Odyssey.ResetQueuedTextAlpha();
    }

    private void Post(string text, int column, int row)
    {
        int n = Append(0, text);
        _line[n] = 0;
        fixed (byte* p = _line)
        {
            Odyssey.PostTextLine(p, column, row, LineLife);
        }
    }

    private int Append(int at, string text)
    {
        foreach (char c in text)
        {
            if (at < _line.Length - 1)
            {
                _line[at++] = (byte)c;
            }
        }

        return at;
    }

    private int Append(int at, char c)
    {
        if (at < _line.Length - 1)
        {
            _line[at++] = (byte)c;
        }

        return at;
    }

    /// <summary>Appends a small integer (player counts are single/double digit).</summary>
    private int Append(int at, byte value)
    {
        if (value >= 10)
        {
            at = Append(at, (char)('0' + (value / 10)));
        }

        return Append(at, (char)('0' + (value % 10)));
    }

    /// <summary>Copies a fixed-size block-field, padded to <paramref name="pad"/> columns so the
    /// rows line up as a table.</summary>
    private int AppendField(int at, byte* source, int capacity, int pad)
    {
        int written = 0;
        for (int i = 0; i < capacity && source[i] != 0; i++)
        {
            at = Append(at, (char)source[i]);
            written++;
        }

        for (; written < pad; written++)
        {
            at = Append(at, ' ');
        }

        return at;
    }
}
