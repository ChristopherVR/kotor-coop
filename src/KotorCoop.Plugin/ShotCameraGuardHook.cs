using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Stops <c>CGuiInGame::UpdateShotCamera 0x006306E0</c> running against a conversation whose
/// participants are gone. It resolves both of them through the client's own object lookup and
/// dereferences the results with no null check —
///
/// <code>
///   0x006307D8  call CClientExoApp::GetGameObject   ; gui+0x170
///   0x006307DD  mov  edx, [eax]                     ; &lt;- no test
///   0x006307F6  call CClientExoApp::GetGameObject   ; gui+0x174
///   0x006307FB  mov  edx, [eax]                     ; &lt;- the live crash
/// </code>
///
/// — so an id the client no longer holds is an access violation. Retail never gets there
/// because a conversation only ever ends through <c>CSWSDialog::Cleanup</c>, which tears the
/// server dialog down in the same breath as the client UI, leaving nothing to send. Co-op can:
/// a peer's conversation ending, a follow timing out, or losing the token race all end a
/// conversation from OUR frame hook, and one stray reply list arriving afterwards is enough.
///
/// <see cref="Odyssey.CloseDialogue"/> now ends conversations server-side precisely so that
/// message is never generated. This is the net under that: the fault is one dereference of a
/// value the engine already has in hand, so refusing to run the function when it would fault
/// costs a frame of dialogue-camera aim in a state that is already over, and nothing else.
/// All three call sites (<c>ShowDialogReplies</c>, <c>HandleDialogEntry</c>,
/// <c>HandleDialogSelection</c>) discard the return value, so returning 0 is exactly what the
/// engine's own early-out at 0x00630F62 does.
/// </summary>
internal static unsafe class ShotCameraGuardHook
{
    private const nint UpdateShotCamera = 0x006306E0;

    /// <summary>Where the relocated prologue resumes: the <c>push eax</c> that follows it.</summary>
    private const nint ResumeAt = 0x006306ED;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c>, <c>mov eax, fs:[0]</c>,
    /// <c>push 0x729528</c>. Thirteen bytes, none position-dependent.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x68, 0x28, 0x95, 0x72, 0x00];

    /// <summary>Bumped every time a call was refused, so a live run can tell whether the
    /// window is still being hit at all after the server-side close landed.</summary>
    private static uint _refusedCount;

    public static uint RefusedCount => _refusedCount;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)UpdateShotCamera;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not guarding the shot camera";
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
        s[n++] = 0x51;                                       // push ecx  (the CGuiInGame this)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&OnUpdateShotCamera;
        s[n++] = 0xE8;                                       // call OnUpdateShotCamera
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;         // add esp, 4  (cdecl arg)
        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax  (before popad —
                                                             //  popad overwrites eax)
        s[n++] = 0x61;                                       // popad (flags survive)
        s[n++] = 0x75;                                       // jnz refused
        s[n++] = (byte)(Prologue.Length + 5);                //   skip the prologue + jmp

        for (int i = 0; i < Prologue.Length; i++)            // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp UpdateShotCamera+13
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x33; s[n++] = 0xC0;                        // refused: xor eax, eax
        s[n++] = 0xC2; s[n++] = 0x18; s[n++] = 0x00;         // ret 0x18 — six stack args,
                                                             //  read off the epilogue at
                                                             //  0x00631390

        if (!NativeMethods.VirtualProtect(UpdateShotCamera, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (UpdateShotCamera + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90;                                  // pad, so nothing reads garbage
        }

        NativeMethods.VirtualProtect(UpdateShotCamera, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"shot-camera guard at 0x{UpdateShotCamera:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>Returns non-zero to refuse the call. No allocation: this runs inside the
    /// engine's dialogue message handling.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnUpdateShotCamera(nint gui)
    {
        try
        {
            if (Odyssey.ShotCameraParticipantsResolvable(gui))
            {
                return 0;
            }

            _refusedCount++;
            return 1;
        }
        catch
        {
            // Never let a managed fault unwind into the engine; fail open.
            return 0;
        }
    }
}
