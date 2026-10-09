namespace KotorCoop.Plugin;

/// <summary>
/// Makes <c>CSWRoomSurfaceMesh::GetTriangleAdjacency 0x00580620</c> answer "no neighbour" for a
/// negative triangle index instead of reading before its adjacency array.
///
/// <code>
///   0x00580620  mov  eax, [esp + 8]          ; triangle
///   0x00580624  cmp  eax, [ecx + 0x58]       ; triangle count
///   0x00580627  jge  0x005806B2              ; SIGNED: -1 passes
///   0x00580637  lea  esi, [eax + eax*2]
///   0x0058063B  mov  edx, [edx + esi*4]      ; &lt;- adjacency[-3]: the live fault
/// </code>
///
/// The host crashed there three times on the Endar Spire (2026-10-08/09), each time while the
/// engine pathed a player toward something in the sealed cutscene room behind a window (once a
/// door used 45 m away, twice a teleport): the path test asks for the neighbours of a triangle the
/// point is not on, and gets index -1. The earlier walkmesh guard (docs/research/walkmesh-guard.md)
/// screens the plugin's own moves; this one covers the engine's own pathing, which a player's click
/// on that door through the window reaches too.
///
/// For a negative index the stub writes -1 to all three out-params and returns, which is exactly
/// what the function itself writes for an edge with no neighbour (a wall), so the path test fails
/// cleanly. Every other index runs the original code. Pure assembly, no managed code in the engine
/// call; <see cref="Count"/> reports how often it fired.
/// </summary>
internal static unsafe class TriangleAdjacencyGuardHook
{
    private const nint Function = 0x00580620;
    private const nint ResumeAtJge = 0x00580627;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov eax, [esp+8]</c>; <c>cmp eax, [ecx+0x58]</c>: two whole instructions, seven bytes.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x44, 0x24, 0x08, 0x3B, 0x41, 0x58];

    private static uint* _count;
    private static bool _installed;

    public static uint Count => _count == null ? 0 : *_count;

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)Function;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); negative walkmesh triangles stay unguarded";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 128, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        uint* count = (uint*)(stub + 120);
        *count = 0;
        int n = 0;

        s[n++] = 0x8B; s[n++] = 0x44; s[n++] = 0x24; s[n++] = 0x08;   // mov eax, [esp+8]
        s[n++] = 0x85; s[n++] = 0xC0;                                   // test eax, eax
        s[n++] = 0x7C; int toNegative = n++;                            // jl negative
        s[n++] = 0x3B; s[n++] = 0x41; s[n++] = 0x58;                    // cmp eax, [ecx+0x58]
        s[n++] = 0xE9;                                                  // jmp 0x00580627 (the jge, flags live)
        *(int*)(s + n) = (int)(ResumeAtJge - (stub + n + 4));
        n += 4;

        s[toNegative] = (byte)(n - (toNegative + 1));
        s[n++] = 0xF0; s[n++] = 0xFF; s[n++] = 0x05;                    // lock inc dword [count]
        *(uint*)(s + n) = (uint)(nint)count; n += 4;
        foreach (byte offset in new byte[] { 0x0C, 0x10, 0x14 })        // the three out-params
        {
            s[n++] = 0x8B; s[n++] = 0x44; s[n++] = 0x24; s[n++] = offset;   // mov eax, [esp+offset]
            s[n++] = 0xC7; s[n++] = 0x00; *(int*)(s + n) = -1; n += 4;       // mov dword [eax], -1
        }

        s[n++] = 0xC2; s[n++] = 0x14; s[n++] = 0x00;                    // ret 0x14

        if (!NativeMethods.VirtualProtect(Function, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (Function + 5));
        site[5] = 0x90;
        site[6] = 0x90;
        NativeMethods.VirtualProtect(Function, (nuint)Prologue.Length, old, out _);

        _count = count;
        _installed = true;
        diagnostic = $"walkmesh triangle guard at 0x{Function:X} (stub 0x{stub:X})";
        return true;
    }
}
