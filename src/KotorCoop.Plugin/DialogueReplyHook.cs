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
    /// Set while this instance is FOLLOWING someone else's conversation. Picks made then are
    /// not ours to broadcast — they are the replays we are performing on our own panel, and
    /// publishing them would echo back to the owner and advance their tree a second time.
    /// </summary>
    public static bool Suppressed { get; set; }

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
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x24]  (slot)
        *(int*)(s + n) = 0x24;
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, void>)&OnSelectReply;
        s[n++] = 0xE8;                                       // call OnSelectReply
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;         // add esp, 4  (cdecl cleanup)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < Prologue.Length; i++)             // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp SelectReply+5
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

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
    /// Runs inside the engine's call, before its own body. Records and returns — no engine
    /// calls, no allocation, nothing that could throw, because this is the game's thread
    /// mid-dispatch.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSelectReply(int slot)
    {
        if (Suppressed || slot < 0)
        {
            return;
        }

        LastReplySlot = slot;
        Sequence++;
    }
}
