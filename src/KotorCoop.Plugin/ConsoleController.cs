using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Bridges the game's own developer console (grave/tilde) to the co-op command channel, so a
/// player can type <c>connect &lt;ip&gt; [password]</c>, <c>disconnect</c> and <c>reconnect</c>
/// in-game and see the results. See docs/research/console-overlay.md.
///
/// The plugin READS the engine's live command buffer and Win32 key state to capture and route
/// commands, and WRITES into the shared block. The engine owns the toggle, the on-screen input
/// line, the caret and the keyboard grab (all provided once EnableCheats is set). It also
/// registers the co-op verbs with the console's own command table (a verified, data-driven
/// API — see docs/research/console-register.md) with a no-op handler, purely so the engine
/// recognises them and does not print "function &lt;x&gt; not found"; the routing still happens
/// through the buffer capture below.
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
    /// <summary>Verbs the co-op layer owns. None collide with a stock KOTOR cheat. Kept in
    /// sync with <c>ConsoleCommand.Parse</c> on the agent side.</summary>
    private static readonly string[] Verbs =
    [
        "connect", "join", "host", "disconnect", "dc", "leave",
        "reconnect", "rc", "say", "chat", "msg", "status", "help", "coop",
    ];

    private const int LogLines = 6;

    private readonly SharedBlockView _block;

    // The console line as it stood while it was open, so an Enter that clears the engine
    // buffer AND closes the console (both happen on Enter, before this frame-hook runs) still
    // recovers the command. _pendingAge counts frames since it was last refreshed while open.
    private readonly byte[] _pending = new byte[SharedBlock.ConsoleCommandLength];
    private int _pendingLength;
    private int _pendingAge;

    /// <summary>Frames a remembered line may still be routed after the console closed. The
    /// submit-Enter is seen within a frame or two of the close; longer would risk a later
    /// gameplay Enter routing a stale line.</summary>
    private const int PendingGraceFrames = 4;

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
        if (!_registered)
        {
            _registered = true;
            RegisterCommands();
        }

        CaptureReplies();

        if (Odyssey.IsConsoleOpen)
        {
            RememberCurrentLine();
            Draw();
        }
        else if (_pendingLength > 0)
        {
            _pendingAge++;
        }

        // Enter is handled whether or not the console still reads open: the engine clears the
        // buffer and closes the console ON Enter, before this frame-hook runs, so the line
        // must be the one remembered while it was open, not the (now-empty) live buffer.
        HandleEnter();
    }

    public void Invalidate()
    {
        _pendingLength = 0;
    }

    private bool _registered;

    /// <summary>Blocks handed to the engine console; kept forever because the table stores our
    /// pointers without copying (docs/research/console-register.md).</summary>
    private static nint _functor;

    /// <summary>
    /// Registers each co-op verb with the engine console so it recognises them (no "function
    /// not found") and the adapter blanks the output. The handler is a no-op — the command is
    /// still captured and routed by <see cref="HandleEnter"/>; registration only suppresses
    /// the engine's own dispatch complaint. MAIN THREAD ONLY.
    /// </summary>
    private void RegisterCommands()
    {
        // One shared functor: { engine adapter vtable, our cdecl handler }.
        _functor = (nint)NativeMemory.AllocZeroed(8);
        *(nint*)_functor = Odyssey.ConsoleAdapterVtable;
        *(nint*)(_functor + 4) = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&ConsoleCommandHandler;

        foreach (string verb in Verbs)
        {
            // Zeroed entry: name inline at +0x00, functor pointer at +0x50. 0x60 bytes leaves
            // room past the two read fields.
            byte* entry = (byte*)NativeMemory.AllocZeroed(0x60);

            int n = Math.Min(verb.Length, 0x4F);
            for (int i = 0; i < n; i++)
            {
                entry[i] = (byte)verb[i];
            }

            *(nint*)(entry + 0x50) = _functor;
            Odyssey.RegisterConsoleEntry((nint)entry);
        }

        PluginHost.Log($"console: registered {Verbs.Length} co-op commands");
    }

    /// <summary>
    /// The engine console invokes this (via its functor adapter) when a registered verb is
    /// entered. Deliberately empty: it exists so the engine finds the command and does not
    /// print "function not found"; the real routing is done by the buffer capture. cdecl —
    /// the adapter cleans the one argument, so a stdcall handler would corrupt the stack.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ConsoleCommandHandler(nint args)
    {
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
        _pendingAge = 0;
    }

    /// <summary>
    /// On the rising edge of Enter, routes the line remembered while the console was open (if
    /// it is one of ours), then clears the engine buffer so it is not also dispatched as an
    /// unknown cheat. Only a freshly-remembered line qualifies, so an Enter pressed in normal
    /// play long after the console closed never routes a stale command.
    /// </summary>
    private void HandleEnter()
    {
        bool down = (NativeMethods.GetAsyncKeyState(NativeMethods.VkReturn) & 0x8000) != 0;
        bool rising = down && !_enterWasDown;
        _enterWasDown = down;

        if (rising && _pendingLength > 0 && _pendingAge <= PendingGraceFrames)
        {
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
            return;
        }

        // Drop a remembered line once the console has been closed for a few frames, so a
        // later Enter in normal play cannot route it.
        if (_pendingAge > PendingGraceFrames)
        {
            _pendingLength = 0;
        }
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
