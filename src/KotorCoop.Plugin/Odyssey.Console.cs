// Odyssey.Console.cs - the developer console and forcing it open as a chat prompt.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- developer console -------------------------------------------------------

    // The retail build still links the whole Aurora developer console. These are its live
    // globals (see docs/research/console-overlay.md): the open flag, the command-line buffer
    // it edits as the player types, and the caret index into that buffer.
    private const nint ConsoleOpenFlagPtr = 0x007A3A40;
    private const nint ConsoleBufferPtr = 0x007A3A4C;
    private const nint ConsoleCaretPtr = 0x007A3A44;

    /// <summary>Capacity of the engine's console command buffer (null-terminated, cap 500).</summary>
    public const int ConsoleBufferCapacity = 0x1F4;

    // Global::AurPostString(str, x, line, life) — cdecl, caller-cleaned; ENQUEUES a string
    // node (new[0x434]) onto the render list and returns it. It never touches the texture font
    // or GL, so it is safe to call from the SwapBuffers hook when the font is unbound — unlike
    // the immediate PrintStringScreenPos, which faults there. UpdateScreen flushes the queue
    // every frame (AurRenderStrings, ungated by the console flag), so a posted line renders on
    // the next frame whether or not the console is open. See docs/research/text-render-queued.md.
    private static readonly delegate* unmanaged[Cdecl]<byte*, int, int, float, nint> AurPostStringFn =
        (delegate* unmanaged[Cdecl]<byte*, int, int, float, nint>)0x0044D490;

    /// <summary>True while the native developer console is open (grave/tilde toggled it).</summary>
    public static bool IsConsoleOpen => *(int*)ConsoleOpenFlagPtr != 0;

    /// <summary>Pointer to the engine's live console command-line buffer.</summary>
    public static byte* ConsoleBuffer => (byte*)ConsoleBufferPtr;

    /// <summary>
    /// Empties the engine's console command line. Used after a co-op command is captured, so
    /// the engine does not also try to run it as an (unknown) cheat verb on Enter.
    /// </summary>
    public static void ClearConsoleBuffer()
    {
        *(byte*)ConsoleBufferPtr = 0;
        *(int*)ConsoleCaretPtr = 0;
    }

    /// <summary>Length of the null-terminated console buffer, clamped to its capacity.</summary>
    public static int ConsoleBufferLength()
    {
        byte* p = (byte*)ConsoleBufferPtr;
        int i = 0;
        while (i < ConsoleBufferCapacity && p[i] != 0)
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads the console command line as a managed string (ASCII, cut at the first null —
    /// this is a raw char buffer, not a CExoString, so there is no length-includes-null trap
    /// here; the null itself is the only terminator). Allocates, so call it on key edges and
    /// buffer changes rather than every frame. MAIN THREAD ONLY.
    /// </summary>
    public static string ReadConsoleLine()
    {
        byte* p = (byte*)ConsoleBufferPtr;
        int n = ConsoleBufferLength();
        if (n == 0)
        {
            return string.Empty;
        }

        Span<char> chars = n <= 256 ? stackalloc char[256] : new char[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = (char)p[i];
        }

        return new string(chars[..n]);
    }

    /// <summary>
    /// Replaces the console command line and parks the caret at its end — the same two
    /// stores <see cref="OpenChatInput"/> seeds, which is all the engine needs: it owns the
    /// buffer and keeps editing from the caret as the player types. Non-ASCII characters
    /// become '?' (the engine's WM_CHAR path only ever produces ASCII here anyway).
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetConsoleLine(string text)
    {
        byte* buffer = (byte*)ConsoleBufferPtr;
        int count = Math.Min(text.Length, ConsoleBufferCapacity - 1);
        for (int i = 0; i < count; i++)
        {
            char c = text[i];
            buffer[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        buffer[count] = 0;
        *(int*)ConsoleCaretPtr = count;
    }

    // The queued render's text color: the string node captures its RGBA at enqueue time from
    // these .data floats (NOT the 0x00798BB4 set the immediate path used). The A-channel pair
    // is 0x0078E384/0x0078E374; writing them before a post fades that line. Restore to 1.0 so
    // the game's own subtitles are not tinted. See docs/research/text-render-queued.md.
    private const nint QueuedTextAlphaA = 0x0078E384;
    private const nint QueuedTextAlphaB = 0x0078E374;

    /// <summary>Sets the alpha (0..1) for subsequently posted lines. MAIN THREAD ONLY.</summary>
    public static void SetQueuedTextAlpha(float a)
    {
        *(float*)QueuedTextAlphaA = a;
        *(float*)QueuedTextAlphaB = a;
    }

    /// <summary>Restores opaque, so the game's own queued text is not tinted.</summary>
    public static void ResetQueuedTextAlpha() => SetQueuedTextAlpha(1f);

    /// <summary>
    /// Posts one null-terminated ASCII line to the engine's render queue at a text grid cell
    /// (<paramref name="column"/>, <paramref name="row"/>), living <paramref name="life"/>
    /// seconds (~10 frames per unit). Re-post every frame to keep it up; the ctor de-dups a
    /// line at the same cell and the flush frees expired nodes, so re-posting does not leak.
    /// Enqueue only — never touches the font — so it is safe from the SwapBuffers hook even
    /// when the font is unbound. MAIN THREAD ONLY.
    /// </summary>
    public static void PostTextLine(byte* nulTerminated, int column, int row, float life)
    {
        if (nulTerminated != null && *nulTerminated != 0)
        {
            AurPostStringFn(nulTerminated, column, row, life);
        }
    }

    // Suppress the console's "function <x> not found" for our co-op verbs. Registering them in
    // the command table did not take (the lookup at 0x0044BB90 never matched our entries), so
    // instead blank the format string the not-found path prints: at 0x0044C4B5 the dispatcher
    // does sprintf(buf, 0x0074145C /*"function %s not found"*/, verb). Making that string empty
    // makes the message empty — nothing shown — while our buffer capture still routes the
    // command. It also silences a real typo's message, which is an acceptable trade for a
    // co-op console. One-byte patch to read-only .rdata, so unprotect around it.
    private const nint NotFoundFormatString = 0x0074145C;

    /// <summary>Blanks the "function %s not found" message. Idempotent. MAIN THREAD ONLY.</summary>
    public static void SuppressConsoleNotFound()
    {
        if (*(byte*)NotFoundFormatString == 0)
        {
            return; // already blanked
        }

        if (NativeMethods.VirtualProtect(NotFoundFormatString, (nuint)1, NativeMethods.PageReadWrite, out uint old))
        {
            *(byte*)NotFoundFormatString = 0;
            NativeMethods.VirtualProtect(NotFoundFormatString, (nuint)1, old, out _);
        }
    }

    // ---- force-open the console as a chat prompt ----------------------------------

    // Opening the console programmatically means replaying the engine's own open sequence so
    // its WM_CHAR handler will route typed characters into the command buffer. The dispatch at
    // HandleWMCharMessage 0x00402240 -> DoConsole 0x00401F00 gates on two flags — the
    // CExoInput keyboard-mode flag and the console-open flag — and (verified) has NO EnableCheats
    // gate, so a normal build can be driven into text-entry this way. The engine then translates
    // keystrokes via Windows, renders the "> " input line for free, and Enter/Esc auto-release
    // movement because the modal state at 0x007A3CB4 is set. See docs/research/chat-input-scroll.md.
    private const nint InputSingletonPtr = 0x007A39E4;   // -> CExoInput*
    private const nint KeyboardModalStatePtr = 0x007A3CB4;

    // CExoInput::KeyboardModeOn / ::ClearEvents — __thiscall, no args, on the input singleton.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> KeyboardModeOnFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005DF520;
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClearEventsFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005DF4E0;

    // TurnOffKeyboardModal — __thiscall, no args, this = *(0x007A39FC) + 4 (a sub-object of the
    // CAppManager). Releasing the modal keyboard grab is what makes movement keys resume after
    // the line is submitted; guarded because the manager pointer can be null before the game loads.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> TurnOffKeyboardModalFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EDA20;

    /// <summary>
    /// Force-opens the developer console pre-filled with <c>say&#160;</c> so a player can type a
    /// chat line without knowing about the console. Replays the engine's own open sequence
    /// (verified against DoConsole 0x00401F00: latch → KeyboardModeOn → ClearEvents →
    /// TurnOffKeyboardModal → open flag), then seeds the buffer/caret. The FuncInterp hook routes
    /// the finished <c>say</c> line. Idempotent: bails if the console is already open so a real
    /// grave keypress isn't double-toggled. MAIN THREAD ONLY (same thread the engine runs this on).
    /// See docs/research/console-open-safe.md.
    /// </summary>
    public static void OpenChatInput() => OpenConsoleWith("say ");

    /// <summary>
    /// The general form of <see cref="OpenChatInput"/>: force-opens the developer console with
    /// <paramref name="prefill"/> already typed and the caret after it, so the player only has
    /// to supply the argument. <c>"say "</c> makes it a chat box; <c>"password "</c> makes it a
    /// password prompt for a locked server.
    ///
    /// Returns false when the console was already open — a real grave keypress must never be
    /// double-toggled, and the caller has to know its prompt did not take (so it can retry on a
    /// later frame rather than silently waiting for an answer nobody was asked for).
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool OpenConsoleWith(string prefill)
    {
        if (*(int*)ConsoleOpenFlagPtr != 0)
        {
            return false; // already open — do not re-toggle
        }

        // Latch first, matching the engine order.
        *(int*)KeyboardModalStatePtr = 1;

        nint input = Deref(InputSingletonPtr);
        if (input != 0)
        {
            KeyboardModeOnFn(input);
            ClearEventsFn(input);
        }

        // Release the modal keyboard grab on the CLIENT object. The crash before was passing
        // this = manager+4 (the address of the client field); the engine passes *(manager+4)
        // (the client pointer). The thunk derefs [this+4] internally, so a wrong `this` here
        // wrote through the server object and faulted. Deref it correctly.
        nint manager = Deref(AppManagerPtr);
        if (manager != 0)
        {
            nint client = Deref(manager + 4);
            if (client != 0)
            {
                TurnOffKeyboardModalFn(client);
            }
        }

        // Seed the verb and place the caret after it, so whatever the player types becomes its
        // argument and Enter routes the finished line through the FuncInterp detour.
        byte* buffer = (byte*)ConsoleBufferPtr;
        int written = Math.Min(prefill.Length, ConsoleBufferCapacity - 1);
        for (int i = 0; i < written; i++)
        {
            char c = prefill[i];
            buffer[i] = (byte)(c is < (char)0x20 or > (char)0x7E ? ' ' : c);
        }

        buffer[written] = 0;
        *(int*)ConsoleCaretPtr = written;
        *(int*)ConsoleOpenFlagPtr = 1;
        return true;
    }

}
