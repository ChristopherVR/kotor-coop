using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Stops a SPECTATOR from skipping or advancing somebody else's conversation.
///
/// THE BUG, observed live: a client started a conversation, the host joined it as a spectator,
/// the host pressed skip — and the line advanced. Replies were already gated
/// (<see cref="DialogueReplyHook"/>); skip never was, because it is a different engine call
/// reached from a different branch of the same switch, one branch away from the one that was
/// hooked:
///
/// <code>
/// CSWGuiDialog::HandleInputEvent 0x006A7230   (event 0x27 = Enter/click, 0x2D)
/// 0x006A7266  mov  eax, [esi+0x1DF8]        ; the panel's own mode byte
/// 0x006A726C  test al, 1
/// 0x006A726E  je   0x6A729E                 ; bit0=0: SELECT the highlighted reply
///                                           ;   -> CSWGuiDialog::SelectReply, ALREADY GATED
/// 0x006A7270  test al, 2
/// 0x006A7272  jne  0x6A730D                 ; bit1=1: already mid-skip, the engine's own no-op
/// 0x006A7278  mov  eax, [esi+0x19C0]        ; &lt;- THE SKIP CASE, and this hook's site
/// 0x006A727E  mov  ecx, [0x7A39FC]
/// 0x006A7284  mov  ecx, [ecx+8]             ; the CServerExoApp
/// 0x006A7287  push eax
/// 0x006A7288  call 0x4AEA30                 ; id fixup
/// 0x006A7297  call 0x4AE970                 ; CServerExoApp::SkipPastDialog
/// 0x006A729C  jmp  0x6A730D
/// </code>
///
/// ONE HOOK COVERS BOTH PANEL CLASSES, unlike the reply gag which needed two sites.
/// <c>CSWGuiDialogCinematic</c> — the class KOTOR's story conversations use — declares no
/// <c>HandleInputEvent</c> of its own and inherits this one, so ordinary and cinematic
/// conversations are gated by the same five bytes.
///
/// ⚠ THE REFUSAL IS THE ENGINE'S OWN NO-OP, NOT A SYNTHESISED ONE — the discipline
/// <see cref="ContainerUseHook"/> states. Blocking jumps to <c>0x6A730D</c>, which is
/// *precisely where the engine itself goes* when it decides this event is already mid-skip
/// (the <c>jne</c> at 0x6A7272). From there its own tail runs unchanged: <c>cmp ebx,0x27</c>
/// matches, and it falls out through <c>pop ebx / pop esi / pop ebp / ret 8</c>. So to the
/// engine a swallowed skip is bit-for-bit a skip that arrived one frame too early — a state it
/// already handles many times per conversation.
///
/// A LEASE, NOT A FLAG, for the reason <see cref="DialogueReplyHook"/>'s own comment gives at
/// length: the frame pass that renews it has early returns above it (an area-transition flush,
/// a travel warp, a loading screen), and a latched suppression flag would leave the player
/// unable to skip ANY conversation, including their own, for the rest of the session. The lease
/// expires by itself. Renewed on exactly the same branch, from exactly the same predicate, as
/// the reply gag — see <c>DialogueSpectateController</c>'s follow pass and
/// <see cref="KotorCoop.Protocol.DialogueSkipAuthorityRule"/>.
///
/// The OWNER's own skip is untouched: <c>Idle</c> means "no peer holds the token", which is the
/// state a player is in while driving their own conversation.
/// </summary>
internal static unsafe class DialogueSkipHook
{
    /// <summary>The skip case's own first instruction inside
    /// <c>CSWGuiDialog::HandleInputEvent</c> — reached only by fallthrough (verified with
    /// <c>tools/xrefs.py 0x6A7278</c>: zero direct call/jmp sites, zero address literals, so
    /// neither of the switch's two jump tables at 0x6A7374/0x6A7384 targets it).</summary>
    private const nint SkipSite = 0x006A7278;

    /// <summary>Where the relocated instruction resumes.</summary>
    private const nint ResumeAt = 0x006A727E;

    /// <summary>Where a BLOCKED skip goes: the engine's own "this event did nothing" join
    /// point, the same target its own already-mid-skip branch takes at 0x6A7272.</summary>
    private const nint BlockedAt = 0x006A730D;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov eax, [esi+0x19C0]</c> — one whole instruction, six bytes, so the five-byte
    /// JMP lands instruction-aligned on both ends.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x86, 0xC0, 0x19, 0x00, 0x00];

    private const int RelocatedBytes = 6;

    /// <summary>
    /// WALL-CLOCK deadline before which skips are swallowed. Same shape, same reason and the
    /// same duration as <see cref="DialogueReplyHook.SuppressLeaseMs"/>: a 32-bit store is
    /// naturally atomic on x86, so the detour can never read a torn deadline.
    /// </summary>
    private static volatile int _suppressUntilTick = Environment.TickCount;

    /// <summary>Deliberately the SAME lease length the reply gag uses. The two are renewed on
    /// the same branch of the same frame pass from the same predicate; letting them expire at
    /// different times would create a window in which a spectator may skip but not answer,
    /// which is not a state anything else in this design knows how to be in.</summary>
    public const int SuppressLeaseMs = DialogueReplyHook.SuppressLeaseMs;

    public static void SuppressSkips() => _suppressUntilTick = Environment.TickCount + SuppressLeaseMs;

    public static void AllowSkips() => _suppressUntilTick = Environment.TickCount;

    public static bool Suppressed => unchecked(_suppressUntilTick - Environment.TickCount) > 0;

    /// <summary>How many skips have been swallowed. The live oracle: a spectator pressing skip
    /// with nothing happening is indistinguishable from a spectator pressing skip on a dead
    /// keyboard, so this counter is what says the gag actually fired.</summary>
    public static uint SwallowedCount { get; private set; }

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every other patch here. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)SkipSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} (saw {site[i]:X2}, expected {Prologue[i]:X2}); "
                    + "a spectator can still skip somebody else's conversation";
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

        s[n++] = 0x60; // pushad

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int>)&OnSkip;
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        // ⚠ TEST BEFORE POPAD, JUMP AFTER IT. popad restores eax (destroying the return value)
        // but does not touch EFLAGS, so the comparison has to happen first and the branch
        // second — the same ordering DialogueGateHook's stub uses.
        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax
        s[n++] = 0x61;                                       // popad
        s[n++] = 0x74;                                       // jz blocked
        s[n++] = (byte)(RelocatedBytes + 5);                 //   past the relocated bytes + jmp

        for (int i = 0; i < RelocatedBytes; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0xE9; // blocked: jmp BlockedAt (the engine's own no-op join point)
        *(int*)(s + n) = (int)(BlockedAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(SkipSite, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (SkipSite + 5));
        site[5] = 0x90; // nop — the sixth relocated byte, so the site stays disassemblable
        NativeMethods.VirtualProtect(SkipSite, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"dialogue skip authority at 0x{SkipSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's own dialogue input dispatch, on every skip/advance keypress or
    /// click while a line is playing. Returns 0 to swallow it, nonzero to let it through.
    ///
    /// ⚠ Letting it through is the safe default and the catch returns it, for the same reason
    /// <see cref="DialogueReplyHook"/>'s does: the cost of being wrong the other way is a
    /// conversation nobody can advance.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSkip()
    {
        try
        {
            if (!Suppressed)
            {
                return 1;
            }

            SwallowedCount++;
            return 0; // a spectator's skip: the owner drives their own conversation
        }
        catch
        {
            // Never unwind into the engine's dialogue dispatch.
            return 1;
        }
    }
}
