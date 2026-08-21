using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The DETECTION half docs/research/trigger-relay.md §6 named as missing: a detour on
/// <c>CClientExoAppInternal::PlayMovie 0x00602CF0</c>, VERIFIED disassembled this pass
/// (<c>tools/disasm.py 0x602CF0</c> against the sha256-matched <c>tools/kotor1_0_3.db</c>):
///
/// <code>
/// 0x00602CF0  56                   push esi
/// 0x00602CF1  8bf1                 mov esi, ecx           ; esi = CClientExoAppInternal*
/// 0x00602CF3  8b86f8010000         mov eax, [esi+0x1F8]   ; the movie-suppression latch
/// 0x00602CF9  85c0                 test eax, eax
/// 0x00602CFB  7406                 je 0x602d03
/// 0x00602CFD  33c0                 xor eax, eax
/// 0x00602CFF  5e                   pop esi
/// 0x00602D00  c21000               ret 0x10                ; FOUR stack arguments
/// </code>
///
/// <c>tools/xrefs.py 0x00602CF0</c> shows exactly ONE caller of this function in the whole
/// binary: the <c>jmp</c> thunk at <c>CClientExoApp::PlayMovie 0x005EDB30</c>
/// (<c>mov ecx,[ecx+4]; jmp 0x00602CF0</c>). <c>tools/xrefs.py 0x005EDB30</c> shows exactly TWO
/// callers of THAT thunk: <c>0x00540F87</c>, inside
/// <c>CSWVirtualMachineCommands::ExecuteCommandPlayMovie 0x00540ED0</c> — the handler for
/// NWScript action 733, <c>PlayMovie</c> (docs/research/cinematics.md §1.4) — and
/// <c>0x006DCF5E</c>, inside <c>CSWGuiTitleMovies::OnMoviePicked</c> (confirmed against the
/// symbol database's own <c>functions</c> table: address <c>0x006DCF50</c>) — the main-menu
/// "movies shown" gallery's own replay-on-click handler.
///
/// <b>NEITHER of those two callers is the module-transition path.</b> A transition's movies
/// are played by <c>CServerExoAppInternal::StartNewModule 0x004BA920</c> →
/// <c>PlayLoadModuleMovies 0x005EDB20</c> → <c>PlayMoviesInExoArrayList 0x00602AF0</c>
/// DIRECTLY — never through this function. So this hook fires ONLY for a script's own
/// mid-module <c>PlayMovie</c> call, or a main-menu gallery replay (harmless: no co-op
/// session's movie-relay logic acts on anything while <c>Odyssey.HasServer</c> is false). This
/// is a property of the call graph, VERIFIED by the xrefs above — not an assumption about
/// behaviour.
///
/// <b>THE ARGUMENT.</b> Disassembling <c>ExecuteCommandPlayMovie</c>'s own call site
/// (<c>0x00540F87</c>) end to end this pass: it calls
/// <c>CClientExoApp::PlayMovie(&amp;name, &amp;local, 0, 0)</c>, where <c>name</c> is the
/// <c>CExoString</c> <c>CVirtualMachine::StackPopString 0x005D1080</c> just filled two
/// instructions earlier — i.e. arg1 is a pointer to the SAME two-field CExoString layout
/// (<c>+0x00 char*</c>, <c>+0x04 length INCLUDING the null</c> — the CExoString length trap)
/// every other hook in this codebase already reads. Entered by a <c>jmp</c>-based detour (this
/// stub is spliced onto the function's very first instruction, exactly like
/// <see cref="PhantomHitHook"/>/<see cref="PhantomEffectHook"/>), so the caller's stack is
/// untouched: arg1 sits at <c>[esp+4]</c> at the moment our stub runs, exactly where the
/// caller left it — no extra pushes have happened yet.
///
/// arg2 (the address of a fresh LOCAL the caller allocates on its OWN stack, never read back
/// by either caller afterward) is presumed to be a pure out-parameter of whatever
/// <c>PlayMoviesInExoArrayList</c> does with it — INFERRED, not traced past this function, and
/// irrelevant here: this hook only READS arg1, never touches arg2.
///
/// Deliberately READ-ONLY, and deliberately narrow: no id compare is even needed (every call
/// that reaches here is this instance's own script or the local player's own menu click), just
/// a bounded byte copy off a pointer this function's own caller already validated. No engine
/// calls, no allocation, and a catch that can never unwind into the engine.
/// </summary>
internal static unsafe class MoviePlayHook
{
    private const nint PlayMovieInternal = 0x00602CF0;

    /// <summary>Where the relocated prologue resumes: right after the three relocated
    /// instructions, at the original <c>test eax, eax</c>.</summary>
    private const nint ResumeAt = 0x00602CF9;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>push esi</c> / <c>mov esi,ecx</c> / <c>mov eax,[esi+0x1F8]</c> — three
    /// whole instructions, 9 bytes, none position-dependent (register-to-register or
    /// register-relative only).</summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x56, 0x8B, 0xF1, 0x8B, 0x86, 0xF8, 0x01, 0x00, 0x00];

    private const int RelocatedBytes = 9;

    /// <summary>Engine resrefs are 16 characters.</summary>
    public const int NameCapacity = 16;

    /// <summary>How many captures are remembered. A script's own PlayMovie call blocks the
    /// main thread for the length of the film before anything else in this process can run, so
    /// two or three in-flight captures is already generous slack, not a tight bound.</summary>
    public const int Capacity = 4;

    private static readonly byte[] _resref = new byte[Capacity * NameCapacity];
    private static readonly uint[] _serial = new uint[Capacity];
    private static int _write;
    private static uint _sequence;

    /// <summary>Bumped on every non-empty capture. A poller comparing this against its own last
    /// seen value is how it tells "there is something new" without touching the ring itself.
    /// </summary>
    public static uint Sequence => _sequence;

    /// <summary>
    /// Copies the most recently captured resref into <paramref name="destination"/> (ASCII,
    /// not null-terminated) and returns how many bytes were written; 0 if nothing has been
    /// captured yet, or if the slot has since been overwritten by a newer capture (the caller
    /// should compare the returned <paramref name="serial"/> against what it expected).
    /// </summary>
    public static int ReadLatest(Span<byte> destination, out uint serial)
    {
        int i = (_write - 1 + Capacity) % Capacity;
        serial = _serial[i];
        if (serial == 0)
        {
            return 0;
        }

        int baseOffset = i * NameCapacity;
        int cap = Math.Min(destination.Length, NameCapacity);
        int n = 0;
        while (n < cap && _resref[baseOffset + n] != 0)
        {
            destination[n] = _resref[baseOffset + n];
            n++;
        }

        return n;
    }

    /// <summary>A module change reassigns every object id and starts a fresh world; a
    /// remembered capture from the old one is not something a peer should ever be asked to
    /// replay.</summary>
    public static void Invalidate()
    {
        Array.Clear(_resref);
        Array.Clear(_serial);
        _write = 0;
        _sequence = 0;
    }

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, like every other patch here. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)PlayMovieInternal;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not watching mid-module PlayMovie";
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

        // Entered by jmp, so the caller's stack args are still in place; pushad shifts them by
        // 0x20, which is why arg1 (the CExoString* name) reads at [esp+0x24] instead of
        // [esp+4] — same reasoning PhantomHitHook.Install and PhantomEffectHook.Install give.
        s[n++] = 0x60; // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24; // push dword [esp+0x24]
        *(int*)(s + n) = 0x24;
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPlayMovie;
        s[n++] = 0xE8; // call OnPlayMovie
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04; // add esp, 4 (cdecl cleanup)
        s[n++] = 0x61; // popad

        for (int i = 0; i < RelocatedBytes; i++)
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp PlayMovieInternal+9
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(PlayMovieInternal, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (PlayMovieInternal + 5));
        for (int i = 5; i < RelocatedBytes; i++)
        {
            site[i] = 0x90;
        }

        NativeMethods.VirtualProtect(PlayMovieInternal, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"mid-module movie watch at 0x{PlayMovieInternal:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs before the engine appends the name to its own queue, for EVERY mid-module
    /// <c>PlayMovie</c> call and every menu-gallery replay in the game.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPlayMovie(nint namePtr)
    {
        try
        {
            if (namePtr == 0)
            {
                return;
            }

            nint cstr = *(nint*)namePtr; // CExoString.CString
            if (cstr == 0)
            {
                return;
            }

            byte* tmp = stackalloc byte[NameCapacity];
            int n = 0;
            while (n < NameCapacity)
            {
                byte c = *(byte*)(cstr + n);
                if (c == 0)
                {
                    break;
                }

                tmp[n++] = c;
            }

            if (n == 0)
            {
                return; // an empty name is not a capture
            }

            int i = _write;
            _write = (_write + 1) % Capacity;
            int baseOffset = i * NameCapacity;

            for (int z = 0; z < NameCapacity; z++)
            {
                _resref[baseOffset + z] = z < n ? tmp[z] : (byte)0;
            }

            _serial[i] = ++_sequence; // written last
        }
        catch
        {
            // Never let a managed fault unwind into the engine's PlayMovie call.
        }
    }
}
