using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Enforces the one-conversation rule at the engine's own front door: while another player
/// holds the session's conversation token, this instance cannot START a dialogue at all —
/// clicking an NPC does nothing, exactly like Baldur's Gate 3's party rule. The token
/// arbiter alone could only ignore the loser's CLAIM; the loser's conversation still ran
/// locally and raced the plot flags. This closes that hole at the single choke point.
///
/// The detour sits on <c>CSWSObject::StartDialog 0x004CF490</c>, the one constructor of a
/// live conversation, reached only from <c>ExecuteCommandBeginConversation</c>
/// (docs/research/dialogue-sync.md — both call sites verified). Refusing is safe by the
/// engine's own contract, settled by disassembly of both callers: they do
/// <c>test eax,eax; je &lt;cleanup&gt;</c>, so returning 0 takes the failure path retail
/// already ships (which clears the pending conversation action via 0x004F34A0). The guard
/// runs BEFORE the function's SEH prologue executes, so the refusal is a plain
/// <c>ret 0xC</c> (three stack args, verified from the epilogue at 0x004CF8DF).
///
/// The spectate controller's own mirrored-conversation open must pass, or a spectator
/// could never follow: <see cref="Bypass"/> is held around that one call.
/// </summary>
internal static unsafe class DialogueGateHook
{
    private const nint StartDialog = 0x004CF490;

    /// <summary>Where the relocated prologue resumes.</summary>
    private const nint ResumeAt = 0x004CF49D;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c>, <c>push 0x719D9E</c> (the handler),
    /// <c>mov eax, fs:[0]</c>. Thirteen bytes, none position-dependent (the handler push is
    /// an absolute constant), so they move verbatim.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x68, 0x9E, 0x9D, 0x71, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Master switch, from coop.ini <c>dialogue_exclusive</c> (default on).</summary>
    public static bool Enabled;

    /// <summary>Set per frame from the block: a REMOTE peer currently owns the conversation
    /// token (the agent writes the flag only for peers other than this instance, so an
    /// owner is never gated on its own conversation).</summary>
    public static volatile bool PeerHoldsConversation;

    /// <summary>Held by the spectate controller around its own StartDialogueWith, which
    /// must reach the engine while the peer's token is active — that is the point.</summary>
    public static bool Bypass;

    /// <summary>Bumped on every refused open, for the controller's on-screen feedback.</summary>
    private static uint _blockedCount;

    public static uint BlockedCount => _blockedCount;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every patch in the plugin: an unrecognised
    /// prologue means we do nothing rather than corrupt the engine. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)StartDialog;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not gating dialogue";
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

        s[n++] = 0x60;                                       // pushad

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int>)&OnStartDialog;
        s[n++] = 0xE8;                                       // call OnStartDialog
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax  (before popad —
                                                             //  popad overwrites eax)
        s[n++] = 0x61;                                       // popad (flags survive)
        s[n++] = 0x75;                                       // jnz blocked
        s[n++] = (byte)(Prologue.Length + 5);                //   skip the prologue + jmp

        for (int i = 0; i < Prologue.Length; i++)            // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp StartDialog+13
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x33; s[n++] = 0xC0;                        // blocked: xor eax, eax
        s[n++] = 0xC2; s[n++] = 0x0C; s[n++] = 0x00;         // ret 0xC — the caller's own
                                                             //  test/je failure path runs

        if (!NativeMethods.VirtualProtect(StartDialog, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (StartDialog + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90;                                  // pad so a mid-prologue resume
        }                                                    //  or disassembler sees no garbage

        NativeMethods.VirtualProtect(StartDialog, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"dialogue gate at 0x{StartDialog:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>Runs at every conversation start, before the engine's own body. Returns
    /// non-zero to refuse. No engine calls, no allocation — a click on an NPC lands here.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnStartDialog()
    {
        try
        {
            if (!Enabled || Bypass || !PeerHoldsConversation)
            {
                return 0;
            }

            _blockedCount++;
            return 1;
        }
        catch
        {
            // Never let a managed fault unwind into the engine; fail open.
            return 0;
        }
    }
}
