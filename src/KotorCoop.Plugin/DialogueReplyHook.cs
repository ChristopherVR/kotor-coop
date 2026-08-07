using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Records which dialogue reply the local player picks, so a spectating peer can replay the
/// same pick and stay on the same line of the conversation.
///
/// Polling cannot see this. The highlighted slot (<c>CSWGuiDialog+0x1C8A</c>) moves with the
/// mouse and the commit happens between frames, so a frame-sampled read either misses the
/// pick or reports a hover. <c>CSWGuiDialog::SelectReply</c> (0x006A7110) is the single
/// click-reply-N entry — every reply, keyboard or mouse, funnels through it — so hooking it
/// catches exactly the picks and nothing else.
///
/// The detour records and returns; it calls nothing and touches no engine state, so it
/// cannot change the behaviour of the call it is standing in front of.
/// </summary>
internal static unsafe class DialogueReplyHook
{
    private const nint SelectReply = 0x006A7110;

    /// <summary>The five bytes the jump overwrites: <c>mov eax,[ecx+0x6C]</c> then the first
    /// two of <c>mov edx,[esp+4]</c>. Both are position-independent — no rel32, no rip
    /// relative — so relocating them into the stub is safe.</summary>
    private static readonly byte[] Prologue = [0x8B, 0x41, 0x6C, 0x8B, 0x54];

    private static readonly nint ResumeAt = SelectReply + 5;

    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageExecuteReadWrite = 0x40;

    private static bool _installed;

    /// <summary>The slot last picked, and a counter that ticks on every pick.</summary>
    public static int LastReplySlot { get; private set; }

    public static uint Sequence { get; private set; }

    /// <summary>
    /// Set while this instance is FOLLOWING someone else's conversation. While set, the
    /// player's own reply clicks are BLOCKED outright — the spectator listens, the owner
    /// chooses — and nothing is recorded for broadcast.
    /// </summary>
    public static bool Suppressed { get; set; }

    /// <summary>
    /// Set for the duration of our own replay call, so the one pick we dispatch on the
    /// follower's panel passes through the block. Same-thread, same-call-chain: the flag is
    /// read inside the call it brackets.
    /// </summary>
    public static bool AllowReplay { get; set; }

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)SelectReply;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking dialogue replies";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        // Entered by JMP, so esp still points at the caller's return address and the single
        // stack argument sits at [esp+4]. pushad shifts that by 0x20 -> [esp+0x24].
        //
        // The callback RETURNS whether the call may proceed: 0 blocks it (the spectator's
        // own click while following), nonzero lets it through. The test runs on the raw
        // return value BEFORE popad, and the blocked path pops the registers and returns
        // callee-clean (ret 4) exactly as the real function would, so to the engine a
        // blocked click is indistinguishable from a click that did nothing.
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x24]  (slot)
        *(int*)(s + n) = 0x24;
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, int>)&OnSelectReply;
        s[n++] = 0xE8;                                       // call OnSelectReply -> eax
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;         // add esp, 4  (cdecl cleanup)
        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax
        s[n++] = 0x74; s[n++] = (byte)(Prologue.Length + 6); // jz blocked (skip resume path)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < Prologue.Length; i++)             // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp SelectReply+5
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x61;                                       // blocked: popad
        s[n++] = 0xC2; s[n++] = 0x04; s[n++] = 0x00;         // ret 4

        if (!NativeMethods.VirtualProtect(SelectReply, 5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (SelectReply + 5));
        NativeMethods.VirtualProtect(SelectReply, 5, old, out _);

        _installed = true;
        diagnostic = $"dialogue reply hook at 0x{SelectReply:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's call, before its own body. Decides whether the call may
    /// proceed (nonzero) and records the pick when it is the local player's own. No engine
    /// calls, no allocation, nothing that could throw — this is the game's thread
    /// mid-dispatch.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSelectReply(int slot)
    {
        // ⚠ The body genuinely cannot throw today, and the catch stays anyway: this is a
        // reverse-P/Invoke boundary, so the cost of being wrong about that later is not a
        // logged exception, it is the process. Every entry point in the plugin is fenced by
        // rule rather than by inspection. Letting the call through (1) is the safe default —
        // the worst case is an unrecorded pick, not a frozen conversation.
        try
        {
            if (AllowReplay)
            {
                return 1; // our own replay on the follower's panel: let through, do not record
            }

            if (Suppressed)
            {
                return 0; // a spectator's click: the owner chooses, nobody else
            }

            if (slot >= 0)
            {
                LastReplySlot = slot;
                Sequence++;
            }
        }
        catch
        {
            // Never unwind into the engine's dialogue dispatch.
        }

        return 1;
    }
}
