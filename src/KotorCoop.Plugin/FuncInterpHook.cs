using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Inline detour on the developer console's command interpreter
/// <c>FuncInterp 0x0044C1F0</c> (<c>__cdecl char*(char* commandLine)</c>). It runs whenever a
/// line is entered in the <c>~</c> console, inside the engine's own main loop — independent of
/// the <see cref="FrameHook"/> SwapBuffers callback (which is dormant at the main menu), so the
/// co-op console works even there.
///
/// For one of our co-op verbs the detour publishes the whole line to the shared block and
/// returns an empty result WITHOUT calling the engine, so the engine never dispatches the verb
/// and never prints "function &lt;x&gt; not found". For anything else it tail-calls the original
/// interpreter, so a genuine typo still gets the engine's not-found message — which is why the
/// old global blank of that format string is gone.
///
/// This replaces the previous buffer-capture-on-Enter routing (which raced the engine's own
/// Enter handling) with an exact, in-line interception at the one function that dispatches.
/// </summary>
internal static unsafe class FuncInterpHook
{
    private const nint FuncInterp = 0x0044C1F0;
    private const nint FuncInterpBody = 0x0044C1F5; // FuncInterp + 5, past the stolen prologue
    private const nint ResultBuffer = 0x007B8AB8;   // the static result string FuncInterp returns
    private const int StolenBytes = 5;              // mov eax, 0xC080  (B8 80 C0 00 00)
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Verbs the co-op layer owns; kept in sync with <c>ConsoleCommand.Parse</c>
    /// (and, for completion, <c>ConsoleVerbCatalog</c>).</summary>
    private static readonly string[] Verbs =
    [
        "connect", "join", "host", "disconnect", "dc", "leave", "reconnect", "rc",
        "say", "chat", "msg", "clear", "cls", "servers", "browse", "list", "status", "help", "coop",
        "password", "pw",
        "stat", "commands", "?", "warp", "who", "peers", "where",

        // A diagnostic, not a cheat: prints what this instance's enemy census holds and what
        // its own engine says about each id. It shadows nothing the retail console owns.
        "census", "resolve",

        // "netsim" only, not its "net" alias: a three-letter word is far too likely to shadow
        // something the engine's own console owns, and `coop net ...` already routes through
        // the "coop" prefix for anyone who wants the short form.
        "netsim",

        // Staging cheats. None of these shadows a retail console verb — checked against the
        // engine's own registered names (docs/research/console-cheats.md) — so intercepting
        // them takes nothing away from the player.
        "god", "topup", "standup", "getup", "slay", "tp", "goto", "dropitem", "cheats",

        // The cinematic staging instrument. Full spelling only, for the same reason "netsim"
        // has no "net" alias here: a verb that arranges a multi-second window in which this
        // plugin does not run is not something to reach by a near-miss.
        "moviewarp",
        // The item-identity harness. "item"/"items"/"setitem" are ours: the engine registers
        // giveitem and nothing else beginning with "item".
        "items", "item", "setitem",
    ];

    /// <summary>
    /// Every submitted console line, for Up/Down recall (see ConsoleAssistController).
    /// Recorded here because this detour is the one place EVERY submission passes through —
    /// engine cheats included, and recalling <c>giveitem</c> is as useful as <c>connect</c>.
    /// Touched only on the game's main thread: the detour runs on it, and so does the
    /// MainLoop hook that navigates.
    /// </summary>
    internal static readonly ConsoleHistoryRing History = new(32);

    private static SharedBlockView? _block;
    private static delegate* unmanaged[Cdecl]<byte*, nint> _original;
    private static bool _installed;

    /// <summary>
    /// Patches FuncInterp's prologue to jump to <see cref="Detour"/>, building a trampoline that
    /// preserves the original. MAIN THREAD ONLY (the same reason FrameHook.Install is).
    /// </summary>
    public static bool Install(SharedBlockView block, out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        _block = block;
        byte* target = (byte*)FuncInterp;

        // Refuse unless the prologue is exactly the expected `mov eax, 0xC080`, so a shifted
        // layout or another hook can never make us corrupt the code.
        if (!(target[0] == 0xB8 && target[1] == 0x80 && target[2] == 0xC0 && target[3] == 0x00 && target[4] == 0x00))
        {
            diagnostic = "FuncInterp prologue mismatch; not hooking";
            return false;
        }

        // Trampoline = [stolen 5 bytes][E9 rel32 -> FuncInterpBody]. The stolen bytes are a
        // position-independent immediate load, so they run correctly at any address.
        nint tramp = NativeMethods.VirtualAlloc(0, 16, MemCommitReserve, PageExecuteReadWrite);
        if (tramp == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* t = (byte*)tramp;
        for (int i = 0; i < StolenBytes; i++)
        {
            t[i] = target[i];
        }

        t[StolenBytes] = 0xE9;
        *(int*)(t + StolenBytes + 1) = (int)(FuncInterpBody - (tramp + StolenBytes + 5));
        _original = (delegate* unmanaged[Cdecl]<byte*, nint>)tramp;

        nint detour = (nint)(delegate* unmanaged[Cdecl]<byte*, nint>)&Detour;
        if (!NativeMethods.VirtualProtect(FuncInterp, (nuint)StolenBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        target[0] = 0xE9;
        *(int*)(target + 1) = (int)(detour - (FuncInterp + 5));
        NativeMethods.VirtualProtect(FuncInterp, (nuint)StolenBytes, old, out _);

        _installed = true;
        diagnostic = $"FuncInterp hooked (trampoline 0x{tramp:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Detour(byte* commandLine)
    {
        try
        {
            if (commandLine != null)
            {
                RecordHistory(commandLine);

                if (_block is { } block && IsCoopCommand(commandLine, out int length))
                {
                    block.PublishConsoleCommand(commandLine, length);
                    *(byte*)ResultBuffer = 0; // empty result: nothing echoed, no "not found"
                    return ResultBuffer;
                }
            }
        }
        catch
        {
            // A detour that throws into the engine would be fatal; fall through to the original.
        }

        return _original(commandLine);
    }

    /// <summary>
    /// Copies the submitted line into the history ring (one small string per submission —
    /// the only allocation this detour makes, and only on the rare human-typed event, never
    /// per frame). The ring itself decides what to keep: empties, consecutive duplicates
    /// and <c>password</c>/<c>pw</c> lines are dropped there.
    /// </summary>
    private static void RecordHistory(byte* commandLine)
    {
        int n = 0;
        while (n < 512 && commandLine[n] != 0)
        {
            n++;
        }

        if (n == 0)
        {
            return;
        }

        Span<char> chars = n <= 256 ? stackalloc char[256] : new char[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = (char)commandLine[i];
        }

        History.Record(new string(chars[..n]));
    }

    /// <summary>True when the line's first token is one of our verbs; sets its length.</summary>
    private static bool IsCoopCommand(byte* cmd, out int length)
    {
        int n = 0;
        while (n < 512 && cmd[n] != 0)
        {
            n++;
        }

        length = n;

        int start = 0;
        while (start < n && cmd[start] == (byte)' ')
        {
            start++;
        }

        if (start < n && cmd[start] == (byte)'/')
        {
            start++; // a leading "/coop"-style slash
        }

        int end = start;
        while (end < n && cmd[end] != (byte)' ')
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
                byte c = cmd[start + i];
                char lower = (char)(c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c);
                if (lower != verb[i])
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
}
