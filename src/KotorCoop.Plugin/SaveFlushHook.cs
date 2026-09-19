using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Opens the pre-store flush window in front of a MANUAL save, so a peer's stand-in, a
/// materialised enemy body, a replicated ground item or a peer's map pin is never written
/// into the module (docs/research/save-ghosts.md §4, roadmap item 36's save-time half).
///
/// THE ONE ENTRY BOTH SAVE PATHS REACH is <c>CServerExoAppInternal::SaveGame 0x004B58A0</c>,
/// traced with <c>tools/xrefs.py</c> and <c>tools/disasm.py</c> (2026-09-18):
///
/// <code>
/// F4 / the plugin's `save` verb
///   CClientExoAppInternal::DoQuickSave 0x5F4B50   -> call 0x5ED670 at 0x5F4BA5
/// the save panel's Save button
///   CSWGuiSaveLoad::WriteGame 0x6C8790           -> call 0x5ED670 at 0x6C88BA
/// CClientExoApp::SendSaveGameRequest 0x5ED670   -> jmp 0x5EEA90 -> jmp
/// CSWCMessage::SendPlayerToServerModule_SaveGame 0x6774E0   (the client -> server message)
/// CSWSMessage::HandlePlayerToServerModuleMessage 0x524800, subtype 4 (0x524900):
///   number = ReadDWORD, name = ReadCExoString, description = ReadCExoString
///   0x524977  CServerExoApp::SetSaveGameName(&amp;description)   ; -> internal+0x1B93C
///   0x524992  CSWSPlayer::GetIsAllowedToSave(player)          ; [player+0x3C] != 0
///   0x5249B1  CServerExoApp::SaveGame(number, &amp;name, player, 0)
/// CServerExoApp::SaveGame 0x4AE6E0 = `mov ecx,[ecx+4]; jmp 0x4B58A0`
/// </code>
///
/// The engine's own autosaves land here too (<c>MainLoop</c>'s pending-autosave branch at
/// 0x4BAE4C and the script command's site at 0x528817) and pass through untouched: they run
/// with nothing of ours standing (the world just came up, or the transition flush already
/// ran), and the callback additionally refuses to hold anything while the plugin's own view
/// of the world epoch is stale.
///
/// WHAT 0x4B58A0 DOES, and why holding it here is safe: <c>thiscall</c>, four stack
/// arguments (<c>ret 0x10</c> at both exits, 0x4B59EE and 0x4B5C4D — the symbol database's
/// "12" is wrong, and the fourth argument is never read): <c>int number</c>,
/// <c>CExoString* name</c>, <c>CSWSPlayer* player</c> (read only on the not-enough-disk-space
/// exit, for the status message), and the unused fourth. It does NOT store the module. It
/// checks disk space, creates and cleans the <c>SAVES:%06d - %s</c> directory, then ARMS THE
/// STALL RECORD at <c>[[0x7A39FC]+0x14]</c> (+0 = 1, +4 = 2 "save", name/description copied
/// to +0x18/+0x20, 0x4B5AB1..0x4B5B04) and returns 1. The store — <c>StallEventSaveGame
/// 0x4B3110</c>, which calls <c>StoreCurrentModule 0x4B2E70</c> at 0x4B32D1 — runs on the
/// NEXT <c>CServerExoAppInternal::MainLoop 0x4BABB0</c> pass, whose first act is to dispatch
/// an armed record (0x4BABD9..0x4BAFB1). That is the "written a tick or two later" note in
/// <c>Odyssey.SaveGame.cs</c>, and it is why the splice sits HERE and not on the stall
/// event: an early return from 0x4B58A0 leaves no directory half-cleaned and no record
/// armed, so the engine is exactly as it was before the request, and re-calling the
/// function later with the same arguments is the same save. The description the handler
/// stored at internal+0x1B93C is untouched by the hold and still there for the re-issue.
///
/// THE SPLICE: the six-byte prologue <c>push ebp / mov ebp,esp / and esp,-8</c> is
/// position-independent and precedes the SEH frame setup, so it is relocated verbatim (the
/// technique <see cref="ContainerUseHook"/> and <see cref="DialogueGateHook"/> use). The stub
/// forwards the four stack arguments to the callback with the SAME <c>[esp+0x30]</c> offset
/// each time — every push moves esp by four, and the next argument is then exactly where the
/// previous one was — plus <c>ecx</c> for <c>this</c>. A hold answers the caller with
/// <c>eax = 1</c> and <c>ret 0x10</c>, bit-for-bit the function's own success exit; a pass
/// runs the relocated prologue and jumps back to 0x4B58A6.
///
/// Verify-bytes-or-refuse, like every other patch here: the prologue AND both
/// <c>ret 0x10</c> epilogues are checked, because the stub's own <c>ret 0x10</c> mirrors them.
///
/// <b>NEEDS-LIVE (2026-09-18, written without a rig):</b> the recipe is in
/// docs/research/save-ghosts.md §4 — host <c>save</c> with the joiner beside it, then the
/// log's <c>save flush: holding the save</c> / <c>save flush: re-issued</c> lines, then
/// <c>python tools/saveinspect.py</c> reading the baseline 25 creatures / 58 waypoints.
///
/// Default OFF; coop.ini <c>save_flush=1</c> enables it. Off leaves the retail save path alone; the load-time
/// <see cref="GhostSweepController"/> then still removes what a save baked in.
/// </summary>
internal static unsafe class SaveFlushHook
{
    /// <summary><c>CServerExoAppInternal::SaveGame</c> — the entry the splice takes.</summary>
    public const nint SaveGame = 0x004B58A0;

    /// <summary>Where the relocated prologue resumes: <c>push -1</c> at 0x4B58A6.</summary>
    private const nint ResumeAt = 0x004B58A6;

    /// <summary>The two <c>ret 0x10</c> exits: not-enough-disk-space and success.</summary>
    private const nint EpilogueFailure = 0x004B59EE;
    private const nint EpilogueSuccess = 0x004B5C4D;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>push ebp / mov ebp,esp / and esp,0xfffffff8</c> — six bytes, three whole
    /// instructions, none position-dependent.</summary>
    private static ReadOnlySpan<byte> Prologue => [0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8];

    /// <summary><c>ret 0x10</c>.</summary>
    private static ReadOnlySpan<byte> Epilogue => [0xC2, 0x10, 0x00];

    /// <summary>Longest save name the capture keeps. The engine's own names are
    /// "QUICKSAVE", "AUTOSAVE" and the panel's "Game%d" — all far shorter.</summary>
    public const int NameCapacity = 64;

    /// <summary>coop.ini <c>save_flush</c>, default OFF until the NEEDS-LIVE recipe in
    /// docs/research/save-ghosts.md §4.3 passes: a held save that is never re-issued (the frame
    /// pass returning early) would be a lost save, so an unverified hook must not ship ON.</summary>
    public static readonly bool Wanted = PluginConfig.ReadBool("save_flush", false);

    private static bool _installed;

    /// <summary>True once the splice is in. The host's frame branch does nothing without it,
    /// so an install that refused the patch (bytes differ) behaves exactly like retail.</summary>
    public static bool Installed => _installed;

    /// <summary>Every request the detour saw, held or not — the live oracle that separates
    /// "the hook never fired" from "the hook fired and passed it through".</summary>
    public static uint Requests { get; private set; }

    /// <summary>Requests the detour swallowed for a later re-issue.</summary>
    public static uint Held { get; private set; }

    /// <summary>One held request's arguments, as the re-issue needs them. The name is a
    /// managed copy: the caller's <c>CExoString</c> lives on the message handler's stack and
    /// is destroyed the moment the hold returns.</summary>
    public struct Capture
    {
        public nint Server;
        public int Number;
        public string Name;
        public nint Player;
        public nint Arg4;
    }

    /// <summary>MAIN THREAD, once. Verify-bytes-or-refuse.</summary>
    public static bool Install(out string diagnostic)
    {
        if (!Wanted)
        {
            diagnostic = "save flush off (default; coop.ini save_flush=1 to enable); a manual save still bakes "
                + "co-op spawns in and the load-time ghost sweep removes them";
            return false;
        }

        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)SaveGame;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at 0x{SaveGame:X}+{i} (saw {site[i]:X2}, expected "
                    + $"{Prologue[i]:X2}); manual saves are not flushed";
                return false;
            }
        }

        foreach (nint epilogue in stackalloc nint[] { EpilogueFailure, EpilogueSuccess })
        {
            byte* e = (byte*)epilogue;
            for (int i = 0; i < Epilogue.Length; i++)
            {
                if (e[i] != Epilogue[i])
                {
                    diagnostic = $"epilogue mismatch at 0x{epilogue:X}+{i} (saw {e[i]:X2}, expected "
                        + $"{Epilogue[i]:X2}); the argument count is not what the stub assumes, "
                        + "manual saves are not flushed";
                    return false;
                }
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

        s[n++] = 0x60; // pushad (esp -= 0x20): the caller's return address is now at [esp+0x20],
                       //  number at +0x24, name at +0x28, player at +0x2C, the fourth at +0x30.

        // Forward the four stack arguments right-to-left for the cdecl callback. ⚠ THE SAME
        // OFFSET EVERY TIME: each push moves esp down by four, which brings the next argument
        // to exactly where the previous one was.
        for (int i = 0; i < 4; i++)
        {
            s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x30; // push dword [esp+0x30]
        }

        s[n++] = 0x51; // push ecx — `this`, untouched by pushad (it saves copies only)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnSaveGame;
        s[n++] = 0xE8; // call OnSaveGame(this, number, name, player, arg4)
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x14; // add esp, 0x14 (five cdecl arguments)

        // ⚠ TEST BEFORE POPAD, BRANCH AFTER IT: popad restores eax but leaves EFLAGS alone —
        // the same ordering DialogueSkipHook and ContainerUseHook use.
        s[n++] = 0x85; s[n++] = 0xC0;                    // test eax, eax
        s[n++] = 0x61;                                   // popad
        s[n++] = 0x74;                                   // jz hold
        s[n++] = (byte)(Prologue.Length + 5);            //   over the relocated prologue + jmp

        for (int i = 0; i < Prologue.Length; i++)        // pass: the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        // hold: answer exactly as the function's own success exit does — eax = 1, ret 0x10.
        // Nothing was pushed, no SEH frame was opened, no directory was touched.
        s[n++] = 0xB8; *(int*)(s + n) = 1; n += 4;       // mov eax, 1
        s[n++] = 0xC2; s[n++] = 0x10; s[n++] = 0x00;     // ret 0x10

        if (!NativeMethods.VirtualProtect(SaveGame, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (SaveGame + 5));
        site[5] = 0x90; // the sixth relocated byte, so the site stays disassemblable
        NativeMethods.VirtualProtect(SaveGame, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"manual saves flushed first: CServerExoAppInternal::SaveGame owned at 0x{SaveGame:X} "
            + $"(stub 0x{stub:X}); NEEDS-LIVE, recipe in docs/research/save-ghosts.md §4";
        return true;
    }

    /// <summary>
    /// Copies a request's arguments out of the engine's stack before the hold returns. False
    /// when the name pointer does not look like a <c>CExoString</c> — the request is then
    /// passed through rather than held on a guess.
    /// </summary>
    public static bool TryCapture(nint server, int number, nint name, nint player, nint arg4, out Capture capture)
    {
        capture = default;
        if (!Odyssey.TryReadExoString(name, NameCapacity, out string text))
        {
            return false;
        }

        capture = new Capture
        {
            Server = server,
            Number = number,
            Name = text,
            Player = player,
            Arg4 = arg4,
        };
        return true;
    }

    /// <summary>
    /// Runs on the game's own thread inside the server's message handling (or its main
    /// loop, for an autosave), at the true entry of <c>SaveGame</c>. Returns nonzero to let
    /// the save proceed, zero to hold it. No engine call and no logging here: the host logs
    /// from its next frame.
    ///
    /// ⚠ Passing through is the safe default and the catch returns it: the cost of being
    /// wrong the other way is a save that never happens.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSaveGame(nint self, int number, nint name, nint player, nint arg4)
    {
        try
        {
            Requests++;
            if (PluginHost.OnSaveGameRequested(self, number, name, player, arg4))
            {
                return 1;
            }

            Held++;
            return 0;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's save path.
            return 1;
        }
    }
}
