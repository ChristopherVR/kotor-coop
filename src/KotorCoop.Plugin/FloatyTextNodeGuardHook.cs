using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Null-node guards inside <c>CSWGuiFloatyText</c>: a floaty ("+N XP", a heal's "+N", "miss",
/// "Sneak Attack" ...) over a creature whose client-side model has no anchor node was a hard
/// client crash — access violation, three times now:
///
/// <list type="bullet">
/// <item>2026-09-03 00:38, <c>eip 0x688F54</c> — <c>applyeffect heal</c> on a phantom
/// (docs/research/effect-sync.md §11.4, "left open as a staging hazard").</item>
/// <item>2026-09-03 03:45, <c>eip 0x688F54</c> — a HOST-SPAWNED creature (the <c>spawncreature</c>
/// verb, 23 of them) slain on the host: the client killed its materialised stand-in to match,
/// the engine's own <c>CSWSCreature::ApplyDeathExperience</c> (0x004FB1E0, docs/research/
/// progression-sync.md) drew the death floaty over the corpse through
/// <c>CClientExoApp::AddFloatyText</c> (call site 0x004FB48A), and the stand-in's client model
/// (<c>renders no twin yet</c>) had no node to hang it on. Crash record: image-range stack
/// <c>0x68B82B 0x602AA8 0x4FB48F</c>.</item>
/// <item>2026-09-03 03:56, <c>eip 0x6890A4</c> — the same recipe with the FIRST guard in place:
/// the guard fired once (<c>floaty: target model has no anchor node - not drawn ... 1 so
/// far</c>), then 3 ms later the per-frame entry update crashed on an entry that had been
/// created while its object's model still had the node and was updated after it lost it.
/// Stack <c>0x686BC8 0x40CEB8 0x603CA0</c>.</item>
/// </list>
///
/// <b>THE FAULT, twice.</b> Both functions (fresh disasm, <c>tools/disasm.py</c>, sha256-matched
/// GOG v1.0.3) resolve the entry's object through <c>CClientExoApp::GetClientObjectByObjectId</c>
/// (0x005ED580), bail to their own shared exit when there is no client object, no scene object
/// (<c>[vtbl+0xC]</c>) or no model (<c>[vtbl+0x2C]</c>), then ask the scene object for its
/// anchor node — <c>[vtbl+0x98](0xFF, 1)</c> — and dereference the answer with NO null test:
///
/// <list type="bullet">
/// <item><c>CreateEntry</c> (0x00688E10): lookup at 0x688F3F into <c>edi</c>, faulting
/// <c>mov edx,[edi]</c> at 0x688F54; guards exit at 0x688FD9.</item>
/// <item>the per-entry update (0x00688FF0, called with the frame delta; ticks the entry's
/// timer at +0x140 first and removes it at 0x689016 once expired): lookup at 0x689073 into
/// <c>ebx</c>, faulting <c>mov edx,[ebx]</c> at 0x6890A4; guards exit at 0x6891D7.</item>
/// </list>
///
/// A model that exists but has not built (or has torn down) the requested node is exactly the
/// case the engine's own three guards do not cover — and a materialised stand-in is that model.
///
/// <b>THE PATCH, applied to both.</b> Each site has the same 15-byte run right after the
/// lookup: <c>mov reg,eax</c> / <c>mov eax,[0x7A39FC]</c> / <c>mov ecx,[eax+4]</c> /
/// <c>call 0x5ED560</c>. It is relocated into a stub that inserts <c>test reg,reg; jz exit</c>
/// after the first instruction. The non-null path runs the other three and jumps back to the
/// instruction right after the run (0x688F54 — which IS the dereference there — and 0x689088,
/// where the camera lookup still stands between the run and the dereference at 0x6890A4) —
/// bit-for-bit what the engine did. The null path jumps to the SAME
/// shared exit the function's own no-model guard takes, from the same register/stack state
/// (each no-model guard sits after the same calls as ours, with only registers touched in
/// between). For the update that exit still runs the timer bookkeeping, so a node-less entry
/// simply ages out unseen; for CreateEntry it is the plain "not added" return. No jump lands
/// inside either relocated run (checked over 0x688E10–0x6891F0), so the ten bytes behind each
/// 5-byte <c>jmp</c> are dead and NOP-filled.
///
/// The null path also bumps a managed counter (<see cref="SkippedCount"/>, pushad/popad around
/// a cdecl call — the idiom <see cref="GalaxyMapConfirmHook"/> uses) so a live session can
/// prove a guard fired rather than merely not crashing. No engine calls from the callback, no
/// allocation, a catch that never unwinds into the engine.
/// </summary>
internal static unsafe class FloatyTextNodeGuardHook
{
    /// <summary>One splice site: where the 15-byte run starts, the ModRM of its first
    /// instruction (<c>mov edi,eax</c> or <c>mov ebx,eax</c>), the matching <c>test</c>, and
    /// where the null path exits. The non-null path ALWAYS resumes at <c>SpliceAt + 15</c> —
    /// the first instruction after the relocated run — NOT at the faulting dereference. In
    /// CreateEntry those are the same address (the run ends on the <c>mov edx,[edi]</c>); in
    /// the update they are 0x1C apart, and resuming at the fault site skipped the camera
    /// lookup in between: the host crashed at 0x6890F3 on its first ordinary damage floaty
    /// (04:01, first build of the two-site guard). Derived, never hand-written, since then.</summary>
    private readonly record struct Site(string Name, nint SpliceAt, byte MovModRm, byte TestModRm, nint NoOpExit, nint Callback)
    {
        public nint ResumeAt => SpliceAt + RunLength;
    }

    private static readonly Site[] Sites =
    [
        // CSWGuiFloatyText::CreateEntry — mov edi,eax (8B F8) / test edi,edi (85 FF); resumes at 0x688F54
        new("CreateEntry", 0x00688F45, 0xF8, 0xFF, 0x00688FD9, (nint)(delegate* unmanaged[Cdecl]<void>)&OnCreateNodeMissing),
        // per-entry update — mov ebx,eax (8B D8) / test ebx,ebx (85 DB); resumes at 0x689088
        new("Update", 0x00689079, 0xD8, 0xDB, 0x006891D7, (nint)(delegate* unmanaged[Cdecl]<void>)&OnUpdateNodeMissing),
    ];

    /// <summary>The engine call each relocated run makes (<c>call 0x5ED560</c>) — re-encoded
    /// relative to the stub.</summary>
    private const nint RelocatedCallTarget = 0x005ED560;

    /// <summary>Bytes 2..14 of both runs: <c>mov eax,[0x7A39FC]</c> / <c>mov ecx,[eax+4]</c> /
    /// <c>call 0x5ED560</c> — the rel32 differs per site and is checked arithmetically.</summary>
    private static ReadOnlySpan<byte> SharedTail => [0xA1, 0xFC, 0x39, 0x7A, 0x00, 0x8B, 0x48, 0x04, 0xE8];

    private const int RunLength = 15;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    private static int _installedSites;
    private static uint _createsSkipped;
    private static uint _updatesSkipped;

    /// <summary>Floaties CreateEntry declined to add because the target's model had no anchor
    /// node — each one would have been the 0x688F54 crash. One per floaty.</summary>
    public static uint CreatesSkipped => _createsSkipped;

    /// <summary>Per-frame update ticks skipped on an existing entry whose object's model lost
    /// (or never built) the node — each one would have been the 0x6890A4 crash. Live 2026-09-03
    /// 04:05 (client, 8 slain stand-ins): 7600 in about 25 s, i.e. a few entries × every frame
    /// until each expired on its own; expect this to run into the thousands per fight.</summary>
    public static uint UpdatesSkipped => _updatesSkipped;

    public static bool IsInstalled => _installedSites == Sites.Length;

    /// <summary>Verify-bytes-or-refuse per site, like every patch in this plugin; a site that
    /// does not match is skipped and named, the others still go in. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installedSites > 0)
        {
            diagnostic = "already installed";
            return true;
        }

        var report = new System.Text.StringBuilder();
        foreach (Site site in Sites)
        {
            if (!InstallSite(site, out string detail))
            {
                report.Append(site.Name).Append(": ").Append(detail).Append("; ");
                continue;
            }

            _installedSites++;
            report.Append(site.Name).Append(" 0x").Append(site.SpliceAt.ToString("X")).Append(' ').Append(detail).Append("; ");
        }

        diagnostic = $"floaty-text null-node guard on {_installedSites}/{Sites.Length} site(s): {report.ToString().TrimEnd(' ', ';')}";
        return _installedSites > 0;
    }

    private static bool InstallSite(Site site, out string detail)
    {
        byte* p = (byte*)site.SpliceAt;
        if (p[0] != 0x8B || p[1] != site.MovModRm)
        {
            detail = $"byte mismatch at +0 ({p[0]:X2} {p[1]:X2})";
            return false;
        }

        for (int i = 0; i < SharedTail.Length; i++)
        {
            if (p[2 + i] != SharedTail[i])
            {
                detail = $"byte mismatch at +{2 + i} ({p[2 + i]:X2})";
                return false;
            }
        }

        int rel = *(int*)(p + 11);
        if (site.SpliceAt + RunLength + rel != RelocatedCallTarget)
        {
            detail = $"call target mismatch (rel32 {rel:X8})";
            return false;
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            detail = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x8B; s[n++] = site.MovModRm;  // mov reg, eax   (the node the engine just looked up)
        s[n++] = 0x85; s[n++] = site.TestModRm; // test reg, reg
        s[n++] = 0x75; s[n++] = 12;             // jnz continue   (over the 12-byte null path)

        // null path: count it, then take the function's own no-op exit.
        s[n++] = 0x60; // pushad
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(site.Callback - (stub + n + 4));
        n += 4;
        s[n++] = 0x61; // popad
        s[n++] = 0xE9; // jmp NoOpExit
        *(int*)(s + n) = (int)(site.NoOpExit - (stub + n + 4));
        n += 4;

        // continue: the remaining relocated instructions, then back to whatever follows the run.
        s[n++] = 0xA1; s[n++] = 0xFC; s[n++] = 0x39; s[n++] = 0x7A; s[n++] = 0x00; // mov eax,[0x7A39FC]
        s[n++] = 0x8B; s[n++] = 0x48; s[n++] = 0x04;                               // mov ecx,[eax+4]
        s[n++] = 0xE8;                                                             // call 0x5ED560
        *(int*)(s + n) = (int)(RelocatedCallTarget - (stub + n + 4));
        n += 4;
        s[n++] = 0xE9;                                                             // jmp ResumeAt
        *(int*)(s + n) = (int)(site.ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(site.SpliceAt, (nuint)RunLength, PageExecuteReadWrite, out uint old))
        {
            detail = "VirtualProtect failed";
            return false;
        }

        p[0] = 0xE9; // jmp stub
        *(int*)(p + 1) = (int)(stub - (site.SpliceAt + 5));
        for (int i = 5; i < RunLength; i++)
        {
            p[i] = 0x90; // the dead tail of the relocated run
        }

        NativeMethods.VirtualProtect(site.SpliceAt, (nuint)RunLength, old, out _);

        detail = $"(stub 0x{stub:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCreateNodeMissing()
    {
        try
        {
            _createsSkipped++;
            if (_createsSkipped <= 20 || _createsSkipped % 100 == 0)
            {
                PluginHost.Log($"floaty: target model has no anchor node - not added (would have been the "
                    + $"0x688F54 crash; {_createsSkipped} so far)");
            }
        }
        catch
        {
            // Never unwind into the engine.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUpdateNodeMissing()
    {
        try
        {
            _updatesSkipped++;
            if (_updatesSkipped == 1 || _updatesSkipped % 5000 == 0)
            {
                PluginHost.Log($"floaty: an entry's model lost its anchor node - tick skipped, it will age "
                    + $"out unseen (would have been the 0x6890A4 crash; {_updatesSkipped} ticks so far)");
            }
        }
        catch
        {
            // Never unwind into the engine.
        }
    }
}
