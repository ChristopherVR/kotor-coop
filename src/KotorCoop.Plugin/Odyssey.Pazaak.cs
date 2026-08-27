// Odyssey.Pazaak.cs - the `pazaak` live-test primitive: starting a hand through
// CClientExoApp::StartPazaakGame directly, instead of the NWScript PlayPazaak() VM entry's own
// argument-popping machinery. See docs/research/minigames.md's 2026-08-27 addendum for the full
// disassembly and the nwscript.nss cross-check this implements.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>CClientExoAppInternal + this = CPazaakData*, nonzero while a hand is running —
    /// the pointer itself is the flag. VERIFIED, docs/research/minigames.md §4
    /// (CClientExoApp::GetPazaakIsRunning 0x005EE140: `mov eax,[ecx+4]; mov eax,[eax+0x70];
    /// ret`), re-verified by fresh disasm this pass.</summary>
    private const int ClientInternalPazaakData = 0x70;

    /// <summary>
    /// CClientExoApp::StartPazaakGame(this, int nOpponentPazaakDeck, CExoString* sEndScript,
    /// int nMaxWager, int bShowTutorial, uint oOpponent) -> void. VERIFIED, fresh disasm this
    /// pass:
    ///
    /// * The thunk itself (0x005EDDC0: `mov ecx,[ecx+4]; jmp 0x5F3810` — tail-jumps to
    ///   <c>CClientExoAppInternal::StartPazaakGame</c>) — called with <c>this</c> = the CLIENT
    ///   (<see cref="GetClient"/>'s own return value), not the client-internal.
    /// * <c>CSWVirtualMachineCommands::ExecuteCommandPlayPazaak</c> — at <c>0x00540FD0</c>, NOT
    ///   <c>0x0053B1E0</c> (that address is <c>ExecuteCommandGetLastPazaakResult</c>; a
    ///   correction to docs/research/minigames.md §4's own address, itself corrected in that
    ///   doc's 2026-08-27 addendum) — pops five VM arguments and calls this function with them
    ///   in the same order, confirmed against the real shipped <c>nwscript.nss</c> declaration
    ///   (<c>tools/kotorfiles.py extract nwscript.nss</c>):
    ///   <c>void PlayPazaak(int nOpponentPazaakDeck, string sEndScript, int nMaxWager, int
    ///   bShowTutorial=FALSE, object oOpponent=OBJECT_INVALID)</c>. The script header itself
    ///   documents <c>nMaxWager &lt;= 0</c> as "the wager screen will not show up" and credits
    ///   stay untouched — exactly what a dev verb wants, so <see cref="StartPazaak"/> always
    ///   passes 0.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte*, int, int, uint, void> StartPazaakGameFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte*, int, int, uint, void>)0x005EDDC0;

    /// <summary>True while a Pazaak hand is running on this instance. VERIFIED,
    /// <see cref="ClientInternalPazaakData"/>'s own remarks.</summary>
    internal static bool IsPazaakRunning()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint internalClient = Deref(client + AppClientOffset);
        return internalClient != 0 && *(nint*)(internalClient + ClientInternalPazaakData) != 0;
    }

    /// <summary>
    /// Starts a Pazaak hand against <paramref name="opponentDeck"/>'s deck template — the way
    /// <c>PlayPazaak()</c> would, but calling the C++ entry directly instead of going through
    /// the VM's own argument-popping machinery, the same choice this pass's
    /// <see cref="OpenStore"/> makes over <c>ExecuteCommandOpenStore</c>. No wager
    /// (<c>nMaxWager=0</c>), no tutorial mode, an empty end-script.
    /// <paramref name="opponent"/> is the NPC object id shown as the opponent, or
    /// <see cref="ObjectInvalid"/> for the engine's own default.
    ///
    /// ⚠ NEEDS-LIVE which <paramref name="opponentDeck"/> index is valid for a given save/NPC —
    /// no per-creature "which deck" lookup was found this pass (see the research doc); 0 is an
    /// unconfirmed guess, not a verified default.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool StartPazaak(int opponentDeck, uint opponent, out string reason)
    {
        reason = string.Empty;

        nint client = GetClient();
        if (client == 0)
        {
            reason = "no client (not in a loaded module)";
            return false;
        }

        if (IsPazaakRunning())
        {
            reason = "a Pazaak hand is already running on this instance";
            return false;
        }

        byte* emptyAscii = stackalloc byte[1];
        emptyAscii[0] = 0;

        byte* endScript = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            endScript[i] = 0;
        }

        CExoStringCtorFn(endScript, emptyAscii);
        StartPazaakGameFn(client, opponentDeck, endScript, 0, 0, opponent);
        CExoStringDtorFn(endScript);

        return true;
    }
}
