namespace KotorCoop.Plugin;

/// <summary>
/// Fixes the documented world-up crash (Handoff: access violation at 0x5272D8, stack
/// HandlePlayerToServerMessage → HandleMessage → ProcessReceivedFrames → MainLoop): about
/// half a second after a plugin-driven load brings the world up, the loopback client's
/// party message reaches <c>CSWSMessage::HandlePlayerToServerParty 0x526910</c> while the
/// player's creature is only half-initialised. The handler null-checks the game OBJECT
/// (<c>je 0x5272E4</c> at 0x5272BC) but not the FACTION: <c>GetFaction 0x513FC0</c> returns
/// null and <c>mov ecx,[eax]</c> at 0x5272D8 faults.
///
/// The fix reuses the handler's own fallback: the no-game-object path at 0x5272E4 does not
/// drop the message — it pushes a default faction (0x7F000000) and joins the same
/// SendServerToPlayerParty call at 0x5272ED, with an identical stack depth to our detour
/// point. So "faction still null" is routed onto the engine's own already-shipping default
/// path, nothing more.
///
/// Patch shape (same discipline as <see cref="MainLoopHook"/>): verify the exact original
/// bytes, then a 5-byte jmp at 0x5272CE to a stub that replays the two clobbered
/// instructions (<c>mov ecx,ebp; call GetFaction</c>), tests the result, and resumes at
/// 0x5272D8 (faction ok, after replaying <c>mov ebp,[ebp+4]</c>) or 0x5272E4 (null).
/// </summary>
internal static unsafe class PartyFactionGuard
{
    /// <summary>Start of `mov ecx,ebp; call GetFaction` inside HandlePlayerToServerParty.</summary>
    private const nint PatchSite = 0x005272CE;

    private const nint GetFaction = 0x00513FC0;
    private const nint ResumeHappy = 0x005272D8;
    private const nint ResumeNullFaction = 0x005272E4;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>The original 10 bytes at the patch site: mov ecx,ebp; call 0x513FC0;
    /// mov ebp,[ebp+4] — refuse to patch anything else.</summary>
    private static ReadOnlySpan<byte> ExpectedBytes =>
        [0x8B, 0xCD, 0xE8, 0xEB, 0xCC, 0xFE, 0xFF, 0x8B, 0x6D, 0x04];

    private static bool _installed;

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)PatchSite;
        for (int i = 0; i < ExpectedBytes.Length; i++)
        {
            if (site[i] != ExpectedBytes[i])
            {
                diagnostic = $"party-handler bytes mismatch at +{i}; not patching";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 32, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;
        s[n++] = 0x8B; s[n++] = 0xCD;                            // mov ecx, ebp
        s[n++] = 0xE8;                                           // call GetFaction
        *(int*)(s + n) = (int)(GetFaction - (stub + n + 4));
        n += 4;
        s[n++] = 0x85; s[n++] = 0xC0;                            // test eax, eax
        s[n++] = 0x74; s[n++] = 0x08;                            // jz -> null_path (+8)
        s[n++] = 0x8B; s[n++] = 0x6D; s[n++] = 0x04;             // mov ebp, [ebp+4]
        s[n++] = 0xE9;                                           // jmp resume (happy)
        *(int*)(s + n) = (int)(ResumeHappy - (stub + n + 4));
        n += 4;
        s[n++] = 0xE9;                                           // null_path: jmp default-faction path
        *(int*)(s + n) = (int)(ResumeNullFaction - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(PatchSite, (nuint)5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                          // jmp stub
        *(int*)(site + 1) = (int)(stub - (PatchSite + 5));
        NativeMethods.VirtualProtect(PatchSite, (nuint)5, old, out _);

        _installed = true;
        diagnostic = $"party faction guard installed (stub 0x{stub:X})";
        return true;
    }
}
