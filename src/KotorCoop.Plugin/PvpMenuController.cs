namespace KotorCoop.Plugin;

/// <summary>
/// The in-game duel panel (docs/research/pvp-plan.md, "In-game surface"): F10 opens a list of the
/// players in this area, Up/Down or 1-9 pick one, Enter challenges them; when somebody challenges
/// this player the prompt shows F11 accept and F12 decline; during a countdown or fight F12 yields.
///
/// Drawn with the queued-text path (<see cref="Odyssey.PostTextLine"/>) like the server browser,
/// and for the same reason: nothing here calls an engine function that can fault. Every action is
/// an ordinary console line (<c>duel Aria</c>, <c>accept</c>, <c>decline</c>, <c>yield</c>) published
/// through the console mailbox, so the agent runs exactly the code a typed command runs and no
/// intent field exists. A line goes out only on a key press edge, never on a timer or a state change.
///
/// It exists only while the host allows duels (<see cref="PvpMenuRule.UiAllowed"/>, written by the
/// agent from the session's setting), which is also why a joiner whose host has duels off sees no
/// hint and no key does anything.
///
/// Runs on the SwapBuffers frame hook, in-world only. The decisions are in <see cref="PvpMenuRule"/>.
/// </summary>
internal sealed unsafe class PvpMenuController(SharedBlockView block)
{
    private const int Column = 14;
    private const int HintRow = 4;
    private const int TitleRow = 5;
    private const int FirstRow = 6;
    private const float LineLife = 0.5f;

    /// <summary>After a line goes out the panel is deaf for this long: the answer comes back through
    /// the agent within a pump or two, and a second press in that gap would only be told "nothing
    /// to answer".</summary>
    private const long CommandCooldownMs = 1_000;

    private readonly byte[] _line = new byte[128];
    private readonly List<PvpMenuEntry> _entries = new(PvpMenuRule.MaxEntries);
    private readonly bool[] _wasDown = new bool[7];

    private bool _listOpen;
    private int _selection;
    private long _lastCommandMs;
    private bool _firstFrame = true;
    private PvpMenuMode _lastMode = PvpMenuMode.Hidden;

    /// <summary>True while the list is up. The chat prompt reads this so Enter confirms a pick
    /// instead of also opening the console.</summary>
    public static bool ListOpen { get; private set; }

    public void OnFrame()
    {
        PvpMenuMode mode = PvpMenuRule.Mode(block.PvpUiFlags, block.PvpPhase);

        // Always sample the keys, so a key held while the panel appears is not a fresh press.
        PvpMenuKeys keys = SampleKeys();

        if (mode != _lastMode)
        {
            PluginHost.Log($"duel panel: {_lastMode} -> {mode}");
            _lastMode = mode;
        }

        if (mode == PvpMenuMode.Hidden || !Odyssey.HasPlayer || !Odyssey.IsInGame)
        {
            Close();
            return;
        }

        CollectEntries();

        // Not while the console, a conversation or a full-screen panel owns the keyboard.
        bool keyboardFree = !Odyssey.IsConsoleOpen && !Odyssey.IsConversationActive
            && !Odyssey.IsFullScreenGuiUp && !Odyssey.IsHudHidden() && !PasswordEntryModal.IsAnyOpen;

        if (!keyboardFree)
        {
            if (keys.Any)
            {
                PluginHost.Log("duel panel: key ignored - the keyboard belongs to "
                    + (Odyssey.IsConsoleOpen ? "the console" : Odyssey.IsConversationActive ? "a conversation"
                        : Odyssey.IsFullScreenGuiUp ? "a full-screen panel" : "a password prompt"));
            }

            // Esc also opens the game menu, and a conversation or panel can start under an open
            // list: the pick does not survive either.
            _listOpen = false;
        }
        else if (!_firstFrame && keys.Any
            && Environment.TickCount64 - _lastCommandMs >= CommandCooldownMs)
        {
            PvpMenuDecision decision = PvpMenuRule.Decide(mode, _listOpen, _selection, _entries, keys);
            _listOpen = decision.ListOpen;
            _selection = decision.Selection;

            if (PvpMenuRule.CommandFor(decision) is { } command)
            {
                Send(command);
            }
        }
        else if (mode != PvpMenuMode.Idle)
        {
            _listOpen = false;
        }

        _firstFrame = false;
        bool listOpen = _listOpen && mode == PvpMenuMode.Idle;
        if (listOpen != ListOpen)
        {
            PluginHost.Log(listOpen ? $"duel panel: list open ({_entries.Count} player(s))" : "duel panel: list closed");
        }

        ListOpen = listOpen;
        Draw(mode, keyboardFree);
    }

    /// <summary>The world changed or the session ended: a half-made pick names a player who may no
    /// longer be here.</summary>
    public void Invalidate() => Close();

    private void Close()
    {
        _listOpen = false;
        _selection = 0;
        ListOpen = false;
    }

    private PvpMenuKeys SampleKeys()
    {
        bool open = Edge(0, NativeMethods.VkF10);
        bool up = Edge(1, NativeMethods.VkUp);
        bool down = Edge(2, NativeMethods.VkDown);
        bool enter = Edge(3, NativeMethods.VkReturn);
        bool escape = Edge(4, NativeMethods.VkEscape);
        bool accept = Edge(5, NativeMethods.VkF11);
        bool decline = Edge(6, NativeMethods.VkF12);

        // Digits: the lowest one that went down this frame. The digit latches live in their own
        // array so a held digit is one press.
        int digit = 0;
        for (int i = 0; i < PvpMenuRule.MaxEntries; i++)
        {
            bool down9 = NativeMethods.IsKeyDownHere(NativeMethods.VkDigit1 + i);
            if (down9 && !_digitWasDown[i] && digit == 0)
            {
                digit = i + 1;
            }

            _digitWasDown[i] = down9;
        }

        return new PvpMenuKeys(open, up, down, enter, escape, accept, decline, digit);
    }

    private readonly bool[] _digitWasDown = new bool[PvpMenuRule.MaxEntries];

    private bool Edge(int slot, int virtualKey)
    {
        bool down = NativeMethods.IsKeyDownHere(virtualKey);
        bool edge = down && !_wasDown[slot];
        _wasDown[slot] = down;
        return edge;
    }

    /// <summary>The players in this area. The agent only fills a remote slot for a peer in the same
    /// world, so "here" needs no check of its own.</summary>
    private void CollectEntries()
    {
        _entries.Clear();
        for (int i = 0; i < SharedBlock.SlotCapacity && _entries.Count < PvpMenuRule.MaxEntries; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active || remote.PeerId == 0)
            {
                continue;
            }

            string name = ReadName(block.GetRemoteName(i));
            if (name.Length == 0)
            {
                continue;
            }

            bool busy = (remote.Flags & (RemoteView.FlagInDialogue | RemoteView.FlagInMinigame | RemoteView.FlagAway)) != 0;
            _entries.Add(new PvpMenuEntry(remote.PeerId, name, busy));
        }
    }

    private static string ReadName(byte* name)
    {
        Span<char> chars = stackalloc char[24];
        int n = 0;
        while (n < 24 && name[n] != 0)
        {
            byte c = name[n];
            chars[n] = c is >= 0x20 and <= 0x7E ? (char)c : '?';
            n++;
        }

        return new string(chars[..n]).Trim();
    }

    private void Send(string command)
    {
        _lastCommandMs = Environment.TickCount64;
        byte[] bytes = new byte[command.Length + 1];
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            bytes[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        fixed (byte* p = bytes)
        {
            block.PublishConsoleCommand(p, command.Length);
        }

        PluginHost.Log($"duel panel: sent '{command}'");
    }

    private void Draw(PvpMenuMode mode, bool keyboardFree)
    {
        // Nothing over a conversation, a cutscene or a full-screen panel: the idle hint sat on top
        // of Saul Karath's Leviathan cutscene (2026-10-09). An answer that is pending still shows.
        if (!keyboardFree && mode == PvpMenuMode.Idle)
        {
            return;
        }

        Odyssey.SetQueuedTextAlpha(1f);
        switch (mode)
        {
            case PvpMenuMode.Idle when _listOpen:
                Post(PvpMenuRule.ListTitle, TitleRow);
                if (_entries.Count == 0)
                {
                    Post(PvpMenuRule.ListEmpty, FirstRow);
                    break;
                }

                for (int i = 0; i < _entries.Count; i++)
                {
                    Post(PvpMenuRule.ListRow(i, i == _selection, _entries[i]), FirstRow + i);
                }

                Post(PvpMenuRule.ListFooter, FirstRow + _entries.Count + 1);
                break;

            case PvpMenuMode.Idle:
                if (PvpMenuRule.ShowHint(mode, _listOpen, _entries.Count))
                {
                    Odyssey.SetQueuedTextAlpha(0.6f);
                    Post(PvpMenuRule.HintLine, HintRow);
                }

                break;

            case PvpMenuMode.Incoming:
                Post(PvpMenuRule.IncomingLine1(PvpMenuRule.NameOf(block.PvpUiPeer, _entries)), TitleRow);
                Post(PvpMenuRule.IncomingLine2, TitleRow + 1);
                break;

            case PvpMenuMode.Outgoing:
                Post(PvpMenuRule.OutgoingLine(PvpMenuRule.NameOf(block.PvpUiPeer, _entries)), TitleRow);
                break;

            case PvpMenuMode.Fighting:
                Post(
                    block.PvpOpponentPeer == 0
                        ? PvpMenuRule.BossFightLine
                        : PvpMenuRule.FightingLine(PvpMenuRule.NameOf(block.PvpOpponentPeer, _entries)),
                    HintRow);
                break;
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    private void Post(string text, int row)
    {
        int n = 0;
        foreach (char c in text)
        {
            if (n < _line.Length - 1)
            {
                _line[n++] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
            }
        }

        _line[n] = 0;
        fixed (byte* p = _line)
        {
            Odyssey.PostTextLine(p, Column, row, LineLife);
        }
    }
}
