using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Captures (target object id, class id) for every <c>AddMultiClass(nClass, oTarget)</c> a
/// KOTOR script runs — the one NWScript routine that turns a Soldier/Scout/Scoundrel into a
/// Jedi (Master Dorak's path choice on Dantooine, <c>danm13_s.rim:k_pdan_makejedi</c>) — so the
/// session can hand the same class to every other player's character
/// (docs/research/class-and-power-grants.md, coop.ini <c>class_grant_sync</c>).
///
/// <b>THE HANDLER.</b> <c>CSWVirtualMachineCommands::ExecuteCommandAddMultiClass</c> is at
/// <c>0x0052DDC0</c> (symbol database, address-literal at the routine-table population site
/// <c>0x0054CBF1</c>: <c>mov dword [eax+0x614], 0x52ddc0</c>, so it is NWScript routine
/// <c>0x614 / 4 = 389</c>). <c>tools/disasm.py 0x52DDC0 0xB0</c> shows it pop its two arguments
/// into two stack locals FIRST — <c>StackPopInteger(&amp;[esp+4])</c> (the class) at 0x0052DDCE,
/// <c>StackPopObject(&amp;[esp])</c> (the target) at 0x0052DDE2 — and only then, at
/// <see cref="HookSite"/>, resolve the creature, read its stats and call
/// <c>CSWSCreatureStats::AddClass</c> (0x005A5D10, the ONLY call site of that function in the
/// image per <c>tools/xrefs.py</c>) followed by <c>CSWPartyTable::DistributeExperience</c> with
/// exactly the XP gap to the next level. See the research doc for the full listing.
///
/// <b>THE SPLICE POINT, VERIFIED.</b> <see cref="HookSite"/> (0x0052DDF6) is the first
/// instruction of the "both pops succeeded" path — the target of the <c>jne 0x52ddf6</c> at
/// 0x0052DDE9 — so it is reached with <c>[esp] = oTarget</c> and <c>[esp+4] = nClass</c> and
/// nothing pushed since. Its instruction, <c>mov edx, dword ptr [0x7a39fc]</c>
/// (<c>8B 15 FC 39 7A 00</c>, 6 bytes), reads an absolute address, so it relocates verbatim
/// into the stub; the one after it (<c>mov eax, [esp]</c>) overwrites <c>eax</c>, and
/// <c>edx</c> is dead until the relocated instruction itself writes it, so both registers are
/// free for the stub to stage the two arguments in before <c>pushad</c>. Control resumes at
/// <see cref="ResumeAt"/> (0x0052DDFC), the untouched <c>mov eax, [esp]</c>. The 11 bytes
/// immediately before the site — the "a pop failed" exit, <c>mov eax, 0xfffff82f; add esp, 8;
/// ret 8</c> — are checked as a second anchor, the same double-check
/// <see cref="ItemTemplateSpawnHook"/> introduced for a mid-function splice.
///
/// <b>WHY HERE AND NOT <c>AddClass</c> ITSELF.</b> Hooking <c>CSWSCreatureStats::AddClass</c>
/// would hand the stub a stats POINTER and leave it to walk back to an object id; this site
/// hands it the script's own object id and class integer directly — ids over pointers, the
/// rule every capture in this plugin follows — and it is the same VM-handler surface the peer's
/// replay (<see cref="Odyssey.AddMultiClass"/>) drives, so the replay's own call passes
/// through this exact hook and is recognised as a replay by <see cref="NextGrantIsReplay"/>
/// rather than republished.
///
/// No engine calls, no allocation inside the hook: it copies three dwords into a small fixed
/// ring and returns. <see cref="ClassGrantController"/> drains the ring on the frame hook and
/// decides (through <see cref="ClassGrantCaptureRule"/>) which entries name this instance's
/// own player.
///
/// ⚠ NEEDS-LIVE: every byte and register claim above is static analysis against the
/// sha256-matched GOG v1.0.3 <c>swkotor.exe</c>; the splice has not run on a live process.
/// </summary>
internal static unsafe class AddMultiClassHook
{
    /// <summary><c>CSWVirtualMachineCommands::ExecuteCommandAddMultiClass</c> — the routine's
    /// handler, verified by the symbol database and by its routine-table population site.</summary>
    public const nint HandlerAddress = 0x0052DDC0;

    /// <summary>NWScript routine 389, from <c>0x614 / 4</c> at the population site.</summary>
    public const int Routine = 389;

    private const nint HookSite = 0x0052DDF6;

    private const nint ResumeAt = 0x0052DDFC;

    /// <summary>The 11 bytes immediately BEFORE the splice — the handler's own "a pop failed"
    /// exit (<c>mov eax, 0xfffff82f; add esp, 8; ret 8</c>) — checked, never overwritten.</summary>
    private static ReadOnlySpan<byte> Anchor =>
        [0xB8, 0x2F, 0xF8, 0xFF, 0xFF, 0x83, 0xC4, 0x08, 0xC2, 0x08, 0x00];

    /// <summary>The 6 bytes actually overwritten: <c>mov edx, dword ptr [0x7a39fc]</c>
    /// (absolute address, relocates verbatim).</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x15, 0xFC, 0x39, 0x7A, 0x00];

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x1000 | 0x2000;

    private static bool _installed;

    /// <summary>How many captures are remembered between controller drains. A script adds a
    /// class once per character per playthrough; 8 is headroom for a rig firing the
    /// <c>addclass</c> verb in a burst, not a limit that bites.</summary>
    public const int Capacity = 8;

    private static readonly uint[] TargetId = new uint[Capacity];
    private static readonly int[] ClassId = new int[Capacity];
    private static readonly bool[] IsReplay = new bool[Capacity];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;

    /// <summary>
    /// Set TRUE by <see cref="Odyssey.AddMultiClass"/> around the ONE VM call it makes on a
    /// peer's behalf and cleared in a <c>finally</c>: the capture that call produces is a
    /// replay of a grant that already went over the wire, and republishing it would bounce
    /// the same class between the two peers forever. The same one-flag de-echo
    /// <see cref="ItemTemplateSpawnHook.NextSpawnIsCheat"/> and
    /// <c>GalaxyMapConfirmHook.ArmReplay</c> use. MAIN THREAD ONLY, synchronous around one call.
    /// </summary>
    public static bool NextGrantIsReplay;

    /// <summary>Whether <see cref="Install"/> spliced the handler (read by the
    /// <c>addclass</c> verb's log line and the research doc's live checklist).</summary>
    public static bool IsInstalled => _installed;

    /// <summary>The count of captures ever written — 0 until the first.</summary>
    public static uint Sequence => _sequence;

    /// <summary>Reads one captured entry by slot index (wrapped). A zero
    /// <paramref name="serial"/> means the slot was never written.</summary>
    public static void Read(int index, out uint targetId, out int classId, out bool isReplay, out uint serial)
    {
        int i = ((index % Capacity) + Capacity) % Capacity;
        targetId = TargetId[i];
        classId = ClassId[i];
        isReplay = IsReplay[i];
        serial = Serials[i];
    }

    /// <summary>A module change reassigns every object id; an undrained entry would name a
    /// stranger. Forgets every capture.</summary>
    public static void Invalidate()
    {
        for (int i = 0; i < Capacity; i++)
        {
            Serials[i] = 0;
            TargetId[i] = 0;
            ClassId[i] = 0;
            IsReplay[i] = false;
        }

        _write = 0;
        _sequence = 0;
    }

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* anchorSite = (byte*)(HookSite - Anchor.Length);
        for (int i = 0; i < Anchor.Length; i++)
        {
            if (anchorSite[i] != Anchor[i])
            {
                diagnostic = $"anchor mismatch at +{i} ({anchorSite[i]:X2}); not hooking AddMultiClass";
                return false;
            }
        }

        byte* site = (byte*)HookSite;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking AddMultiClass";
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

        // Entered by JMP with esp exactly as the handler left it after its two pops: [esp] is
        // the popped object id, [esp+4] the popped class. Stage both in registers FIRST, from
        // THIS esp, before pushad moves it. eax and edx are both dead here (see the class
        // remarks), and pushad/popad round-trip them regardless.
        s[n++] = 0x8B; s[n++] = 0x04; s[n++] = 0x24;               // mov eax, [esp]    (oTarget)
        s[n++] = 0x8B; s[n++] = 0x54; s[n++] = 0x24; s[n++] = 0x04; // mov edx, [esp+4]  (nClass)

        s[n++] = 0x60; // pushad

        // cdecl args, right to left: push edx (class), then eax (target), so the callback's
        // first parameter is the target id. Every push moves esp; both values were read before
        // any push, so no offset is recomputed here.
        s[n++] = 0x52; // push edx
        s[n++] = 0x50; // push eax

        nint callback = (nint)(delegate* unmanaged[Cdecl]<uint, int, void>)&OnAddMultiClass;
        s[n++] = 0xE8; // call OnAddMultiClass
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08; // add esp, 8 (cdecl cleanup)
        s[n++] = 0x61;                               // popad

        for (int i = 0; i < Prologue.Length; i++) // the relocated `mov edx, [0x7a39fc]`
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        if (!NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (HookSite + 5));
        site[5] = 0x90; // the 6th overwritten byte: a nop nothing ever jumps to
        NativeMethods.VirtualProtect(HookSite, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"AddMultiClass captures at 0x{HookSite:X} (stub 0x{stub:X}, routine {Routine})";
        return true;
    }

    /// <summary>
    /// Runs inside the VM handler, after both arguments were popped and before the class is
    /// added. Records and returns — no engine calls, no allocation, nothing that can throw.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAddMultiClass(uint targetId, int classId)
    {
        try
        {
            int slot = _write;
            TargetId[slot] = targetId;
            ClassId[slot] = classId;
            IsReplay[slot] = NextGrantIsReplay;

            // Serial last: the reader trusts a slot only once this says it is complete.
            Serials[slot] = ++_sequence;

            _write = (slot + 1) % Capacity;
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}
