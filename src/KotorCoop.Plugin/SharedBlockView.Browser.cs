namespace KotorCoop.Plugin;

// In-game console, chat overlay and the server browser. See SharedBlockView.cs for the
// class declaration and shared helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- in-game console --------------------------------------------------------

    public uint ConsoleSerial
    {
        get => *U32(SharedBlock.OffsetConsoleSerial);
        set => *U32(SharedBlock.OffsetConsoleSerial) = value;
    }

    /// <summary>Pointer to the null-terminated console command line (96 bytes).</summary>
    public byte* ConsoleCommand => baseAddress + SharedBlock.OffsetConsoleCommand;

    public uint ConsoleReplySerial => *U32(SharedBlock.OffsetConsoleReplySerial);

    /// <summary>Pointer to the null-terminated status reply for the console (96 bytes).</summary>
    public byte* ConsoleReply => baseAddress + SharedBlock.OffsetConsoleReply;

    /// <summary>
    /// Copies a null-terminated ASCII command line into the console channel and bumps the
    /// serial, so the agent picks it up exactly once. <paramref name="length"/> is clamped to
    /// leave room for the terminator.
    /// </summary>
    public void PublishConsoleCommand(byte* source, int length)
    {
        byte* dest = ConsoleCommand;
        int count = Math.Clamp(length, 0, SharedBlock.ConsoleCommandLength - 1);

        for (int i = 0; i < count; i++)
        {
            dest[i] = source[i];
        }

        dest[count] = 0;
        ConsoleSerial++;
    }


    // ---- chat overlay -----------------------------------------------------------

    public bool ChatOverlayEnabled => *U32(SharedBlock.OffsetChatOverlayEnabled) != 0;

    /// <summary>Configured opacity, 0..100.</summary>
    public uint ChatOverlayOpacity => *U32(SharedBlock.OffsetChatOverlayOpacity);

    public uint ChatOverlayAnchor => *U32(SharedBlock.OffsetChatOverlayAnchor);

    public uint ChatOverlayMaxLines => *U32(SharedBlock.OffsetChatOverlayMaxLines);

    public uint ChatOverlayFadeMs => *U32(SharedBlock.OffsetChatOverlayFadeMs);

    public uint ChatSerial => *U32(SharedBlock.OffsetChatSerial);

    /// <summary>Pointer to the latest null-terminated chat line (128 bytes).</summary>
    public byte* ChatLine => baseAddress + SharedBlock.OffsetChatLine;

    /// <summary>Agent-written; bumped to wipe the overlay log.</summary>
    public uint ChatClearSerial => *U32(SharedBlock.OffsetChatClearSerial);


    // ---- server browser ---------------------------------------------------------

    /// <summary>Nonzero while the browser panel is on screen (plugin owns this).</summary>
    public uint BrowserOpen
    {
        get => *U32(SharedBlock.OffsetBrowserOpen);
        set => *U32(SharedBlock.OffsetBrowserOpen) = value;
    }

    /// <summary>The highlighted row (plugin owns this).</summary>
    public uint BrowserSelection
    {
        get => *U32(SharedBlock.OffsetBrowserSelection);
        set => *U32(SharedBlock.OffsetBrowserSelection) = value;
    }

    /// <summary>How many server rows the agent has published.</summary>
    public uint BrowserCount => *U32(SharedBlock.OffsetBrowserCount);

    /// <summary>Bumped by the agent whenever the list changes.</summary>
    public uint BrowserListSerial => *U32(SharedBlock.OffsetBrowserListSerial);

    /// <summary>Confirms the current selection; the agent connects. Selection is written first.</summary>
    public void RequestBrowserConnect() => (*U32(SharedBlock.OffsetBrowserConnectSerial))++;

    /// <summary>Bumped by the agent when the player must be asked for a server's password.</summary>
    public uint BrowserPasswordSerial => *U32(SharedBlock.OffsetBrowserPasswordSerial);

    /// <summary>The player chose to HOST rather than join a listed server.</summary>
    public void RequestBrowserHost() => (*U32(SharedBlock.OffsetBrowserHostSerial))++;

    /// <summary>Peers the session has admitted, written by the agent every tick regardless
    /// of player resolve — the seamless-arrival gate's signal (see SharedBlock).</summary>
    public uint SessionPeers => *U32(SharedBlock.OffsetSessionPeers);

    /// <summary>Agent -&gt; plugin. Environment.TickCount64 at the agent's last dispatcher
    /// tick; 0 = no agent has ever pumped this block. See AgentLiveness.</summary>
    public long AgentHeartbeat => *I64(SharedBlock.OffsetAgentHeartbeat);

    /// <summary>Start of a server row's fixed-size record.</summary>
    public byte* BrowserEntry(int index) =>
        baseAddress + SharedBlock.OffsetBrowserEntries
        + (Clamp(index, SharedBlock.BrowserCapacity) * SharedBlock.BrowserEntryStride);

}
