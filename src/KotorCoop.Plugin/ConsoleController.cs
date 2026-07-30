using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Bridges the game's own developer console (grave/tilde) to the co-op command channel, so a
/// player can type <c>connect &lt;ip&gt; [password]</c>, <c>disconnect</c> and <c>reconnect</c>
/// in-game and see the results. See docs/research/console-overlay.md.
///
/// Design chosen for safety: the plugin does NOT register an engine console command (whose
/// callback ABI is unverified and, if wrong, would crash the game) and installs no inline
/// hook. It only READS the engine's live command buffer and Win32 key state, and WRITES into
/// the shared block. The engine still owns the toggle, the on-screen input line, the caret and
/// the keyboard grab (all of which the native console provides once EnableCheats is set).
///
/// Capture is decoupled from the exact Enter frame: the buffer the engine clears on Enter is
/// remembered from the previous frame, so whichever runs first — the engine's Enter handling
/// or this frame hook — the full command is still recovered.
///
/// Output: the co-op reply lines the agent posts are drawn back on-screen while the console is
/// open, giving the player a small log/status readout.
/// </summary>
internal sealed unsafe class ConsoleController
{
    /// <summary>Verbs the co-op layer owns. None collide with a stock KOTOR cheat.</summary>
    private static readonly string[] Verbs =
    [
        "connect", "join", "host", "disconnect", "dc", "leave",
        "reconnect", "rc", "status", "help", "coop",
    ];

    private const int LogLines = 6;

    private readonly SharedBlockView _block;

    // The console line as it stood on the previous frame, so an Enter that clears the engine
    // buffer before we read still recovers the command.
    private readonly byte[] _pending = new byte[SharedBlock.ConsoleCommandLength];
    private int _pendingLength;

    private bool _enterWasDown;

    // A small ring of the agent's most recent reply lines, drawn while the console is open.
    private readonly byte[][] _log = new byte[LogLines][];
    private int _logCount;
    private uint _lastReplySerial;

    private readonly byte[] _hint;

    public ConsoleController(SharedBlockView block)
    {
        _block = block;

        for (int i = 0; i < LogLines; i++)
        {
            _log[i] = new byte[SharedBlock.ConsoleReplyLength];
        }

        _hint = AsciiLine("coop: connect <ip> [pw] | disconnect | reconnect | status");
    }

    public void OnFrame()
    {
        CaptureReplies();

        if (!Odyssey.IsConsoleOpen)
        {
            // Nothing to capture with the console closed, and the Enter that closed it must
            // not read as a submit next time it opens.
            _enterWasDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VkReturn) & 0x8000) != 0;
            _pendingLength = 0;
            return;
        }

        RememberCurrentLine();
        HandleEnter();
        Draw();
    }

    public void Invalidate()
    {
        _pendingLength = 0;
    }

    /// <summary>Snapshots the engine's live command line while it is non-empty.</summary>
    private void RememberCurrentLine()
    {
        int length = Odyssey.ConsoleBufferLength();
        if (length <= 0)
        {
            return;
        }

        byte* source = Odyssey.ConsoleBuffer;
        int count = Math.Min(length, _pending.Length - 1);
        for (int i = 0; i < count; i++)
        {
            _pending[i] = source[i];
        }

        _pendingLength = count;
    }

    /// <summary>
    /// On the rising edge of Enter, routes the remembered line if it is one of ours, then
    /// clears the engine buffer so it is not also dispatched as an unknown cheat.
    /// </summary>
    private void HandleEnter()
    {
        bool down = (NativeMethods.GetAsyncKeyState(NativeMethods.VkReturn) & 0x8000) != 0;
        bool rising = down && !_enterWasDown;
        _enterWasDown = down;

        if (!rising || _pendingLength <= 0)
        {
            return;
        }

        if (IsCoopCommand(_pending, _pendingLength))
        {
            fixed (byte* p = _pending)
            {
                _block.PublishConsoleCommand(p, _pendingLength);
            }

            Odyssey.ClearConsoleBuffer();
            PluginHost.Log($"console: routed a co-op command ({_pendingLength} chars)");
        }

        _pendingLength = 0;
    }

    /// <summary>Pulls a new agent reply into the on-screen log ring.</summary>
    private void CaptureReplies()
    {
        uint serial = _block.ConsoleReplySerial;
        if (serial == _lastReplySerial)
        {
            return;
        }

        _lastReplySerial = serial;

        byte[] target = _log[_logCount % LogLines];
        byte* source = _block.ConsoleReply;
        for (int i = 0; i < target.Length; i++)
        {
            target[i] = source[i];
            if (source[i] == 0)
            {
                break;
            }
        }

        target[^1] = 0;
        _logCount++;
    }

    /// <summary>
    /// Draws the hint and the recent reply lines. Positions are in the console's centered,
    /// 0.01-scaled space; the exact values want one live calibration pass, but a wrong value
    /// only mis-places text, it cannot crash.
    /// </summary>
    private void Draw()
    {
        const float x = -95f;
        float y = 74f;

        fixed (byte* h = _hint)
        {
            Odyssey.DrawTextAscii(h, x, y + 8f);
        }

        int shown = Math.Min(_logCount, LogLines);
        for (int i = 0; i < shown; i++)
        {
            // Oldest first, newest at the bottom.
            int index = (_logCount - shown + i) % LogLines;
            fixed (byte* line = _log[index])
            {
                Odyssey.DrawTextAscii(line, x, y - (i * 6f));
            }
        }
    }

    private static bool IsCoopCommand(byte[] buffer, int length)
    {
        int start = 0;
        while (start < length && buffer[start] == (byte)' ')
        {
            start++;
        }

        if (start < length && buffer[start] == (byte)'/')
        {
            start++; // a leading "/coop"-style slash
        }

        int end = start;
        while (end < length && buffer[end] != (byte)' ')
        {
            end++;
        }

        int tokenLength = end - start;
        if (tokenLength is <= 0 or > 16)
        {
            return false;
        }

        foreach (string verb in Verbs)
        {
            if (verb.Length != tokenLength)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < tokenLength; i++)
            {
                if (ToLowerAscii(buffer[start + i]) != verb[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static char ToLowerAscii(byte value) =>
        (char)(value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value);

    private static byte[] AsciiLine(string text)
    {
        byte[] source = Encoding.ASCII.GetBytes(text);
        byte[] result = new byte[source.Length + 1];
        Array.Copy(source, result, source.Length);
        return result; // already null-terminated by the extra byte
    }
}
