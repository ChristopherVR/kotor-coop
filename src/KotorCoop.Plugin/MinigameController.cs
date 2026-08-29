namespace KotorCoop.Plugin;

/// <summary>Which of the engine's "not meaningfully in the shared world" states
/// <see cref="MinigameController"/> last saw the local player in. Local-only: carrying this
/// to a peer would need a wire change (item 8 task 2 explicitly says not to make one), so a
/// REMOTE peer's HUD notice can only ever say "in a minigame", never which kind — see
/// <see cref="MinigameNoticeController"/>.</summary>
internal enum MinigameKind
{
    None,
    Swoop,
    Turret,
    Pazaak,

    /// <summary><c>CSWMiniGame+0x80</c> is nonzero-pointer-backed but the <c>type</c> field
    /// itself is neither 1 nor 2 — the loader leaves it at its constructor default for any
    /// other GFF value (docs/research/minigames.md §3). Still "in a minigame" for gating
    /// purposes; just not nameable.</summary>
    Unknown,
}

/// <summary>
/// Reads whether the LOCAL player is inside one of the engine's own "not meaningfully in the
/// shared world" states — a swoop race, a forced turret sequence, or (opt-in, NEEDS-LIVE) a
/// Pazaak hand — every frame, cheaply, and says so in the log on the edges.
///
/// <c>CSWCModule::GetMiniGame</c> (0x00641980) is three instructions once the module
/// pointer is in hand: <c>[module+0x48]</c> (area) then, if non-null, <c>[area+0x264]</c>
/// (mini_game). Both are named in the symbol database (<c>area</c>, <c>mini_game</c>) and
/// the whole chain is VERIFIED by static disassembly — see docs/research/minigames.md.
/// <c>CSWMiniGame+0x80</c> (<c>type</c>, INFERRED 1=swoop/2=turret in some order — §3) is now
/// read too, purely as a local <see cref="MinigameKind"/> label; it changes no gating.
///
/// Pazaak (docs/research/minigames.md §4) is architecturally unrelated — a
/// <c>CSWGuiPazaakGame</c> GUI panel, not a <c>CSWCModule/CSWCArea/CSWMiniGame</c> chain —
/// so it is read off a second, independent pointer (<c>CClientExoAppInternal+0x70</c>,
/// <c>CPazaakData*</c>) behind coop.ini <c>pazaak_detect</c> (default OFF, NEEDS-LIVE per the
/// project's engine-write rules for an unverified READ). This is the read-only, in-process
/// half; <c>KotorGameBridge.IsInMinigame</c>/<c>EngineGlobals.TryIsInPazaak</c> (agent side)
/// is the independent cross-process copy that actually makes the WIRE flag true for peers —
/// see that class's doc comment.
///
/// Deliberately a SEPARATE pointer chain from <see cref="Odyssey"/> rather than an addition
/// to it (Odyssey is not a partial class, and item 8's brief asks for a new file rather than
/// touching it while another change is in flight there) — the constants below are the
/// same well-known addresses <c>EngineGlobals</c> uses on the agent side of the same walk,
/// just re-derefed in-process because the plugin can do that in nanoseconds where the
/// agent's cross-process read costs a syscall.
///
/// This controller does not gate anything itself. The actual policy — parking a peer's
/// phantom, and suppressing forwarded damage / this instance's own presence publish while
/// in a minigame — is decided from <c>PlayerState.FlagInMinigame</c> on the wire, which
/// <c>KotorGameBridge.IsInMinigame</c> (agent side) reads independently through the same
/// pointer chain via <c>EngineGlobals.TryIsInMiniGame</c>/<c>TryIsInPazaak</c>, and applied
/// in <c>CoopAgent.cs</c> (peer filter, <c>PumpEnemies</c>, <c>PumpParty</c>,
/// <c>PumpPhantomDamage</c>) using <c>KotorCoop.Protocol.MinigameRule</c>. This class exists so the
/// transition is visible in the plugin's own log the instant it happens, independent of the
/// agent's poll cadence, and so any future plugin-side consumer (a HUD indicator, the
/// spectate hookup left as a next step) has a ready per-frame read to build on.
/// </summary>
internal static unsafe class MinigameController
{
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;
    private const int ClientInternalOffset = 0x04;
    private const int ClientModuleOffset = 0x18;
    private const int ModuleAreaOffset = 0x48;
    private const int AreaMiniGameOffset = 0x264;

    /// <summary>CSWMiniGame + this = the GFF "Type" field, 1 or 2 when set (§3); left at the
    /// constructor default (observed 0) for any other value. INFERRED, not itself gating.</summary>
    private const int MiniGameTypeOffset = 0x80;

    /// <summary>CClientExoAppInternal + this = CPazaakData*, or 0 (§4). Same offset
    /// <c>EngineGlobals.TryIsInPazaak</c> uses agent-side.</summary>
    private const int ClientPazaakOffset = 0x70;

    /// <summary>coop.ini <c>pazaak_detect</c> — default OFF, NEEDS-LIVE (see this class's own
    /// doc comment and docs/research/minigames.md's dated section). Read once: coop.ini is
    /// not expected to change mid-session for a READ-safety gate like this one.</summary>
    private static readonly bool PazaakDetectEnabled = PluginConfig.ReadBool("pazaak_detect", false);

    private static bool _wasInMiniGame;

    /// <summary>The kind last written to the log, so the late-settle line in
    /// <see cref="OnFrame"/> fires once per change rather than every frame.</summary>
    private static MinigameKind _lastLoggedKind = MinigameKind.None;
    private static bool _wasInPazaak;
    private static bool _bannerLogged;

    /// <summary>The local player's minigame state as of the last <see cref="OnFrame"/>
    /// pass. False both when the engine says so and when the chain could not be read (no
    /// client world yet) — the same "absent means no" the engine's own GetMiniGame uses.</summary>
    public static bool LocalInMinigame { get; private set; }

    /// <summary>Which kind of minigame <see cref="LocalInMinigame"/> is currently true for,
    /// or <see cref="MinigameKind.None"/>. Local-only — see that enum's own doc comment.</summary>
    public static MinigameKind LocalKind { get; private set; } = MinigameKind.None;

    public static void OnFrame()
    {
        if (!_bannerLogged)
        {
            _bannerLogged = true;
            PluginHost.Log($"minigame: pazaak_detect={(PazaakDetectEnabled ? 1 : 0)} (from coop.ini, read once)");
        }

        nint clientInternal = ReadClientInternal();

        nint miniGame = clientInternal == 0 ? 0 : ReadMiniGamePointer(clientInternal);
        bool coreActive = miniGame != 0;

        nint pazaak = 0;
        bool pazaakActive = false;
        if (PazaakDetectEnabled && clientInternal != 0)
        {
            pazaak = Deref(clientInternal + ClientPazaakOffset);

            // LIVE FINDING (2026-08-28): the value observed at this offset while a hand is
            // genuinely running is the literal integer 1, not a plausible heap address — this
            // class's own doc comment (and the original design) assumed "the pointer itself is
            // the flag" meant a real CPazaakData*, and gated on LooksLikeDataPointer(pazaak)
            // (a > 0x10000 range check) the same way every other raw-pointer field here does.
            // That guard silently ate every real Pazaak session: a live A/B (pazaak console
            // verb, pazaak_detect=1) showed clientInternal correctly resolving and the field
            // flipping 0 -> 1 the instant StartPazaakGame ran, while Odyssey.IsPazaakRunning()
            // (a bare `!= 0` check, matching the VERIFIED disassembly of the real engine's own
            // CClientExoApp::GetPazaakIsRunning — `mov eax,[ecx+4]; mov eax,[eax+0x70]; ret`,
            // no shape check anywhere in it) read the SAME field as true at the SAME moment.
            // The range check was simply the wrong extra filter for this one field; matching
            // the verified engine function's own bare-nonzero contract is what fixes it.
            pazaakActive = pazaak != 0;

            if (pazaakActive != _wasInPazaak)
            {
                _wasInPazaak = pazaakActive;

                // Its own edge log, separate from the combined one below and carrying the
                // raw value, exactly so a live pass can confirm CClientExoAppInternal+0x70
                // really is what starts/stops tracking a Pazaak hand (item 8 task 1).
                PluginHost.Log(pazaakActive
                    ? $"pazaak: entered (CPazaakData field 0x{pazaak:X}) — peers will park this player's phantom"
                    : "pazaak: left — back in the shared world");
            }
        }

        bool inMiniGame = coreActive || pazaakActive;
        LocalInMinigame = inMiniGame;
        LocalKind = coreActive ? ReadKind(miniGame) : pazaakActive ? MinigameKind.Pazaak : MinigameKind.None;

        if (inMiniGame == _wasInMiniGame)
        {
            // ⚠ THE KIND CAN ARRIVE LATE. Live 2026-08-29 (docs/research/minigames.md §24.6):
            // entering the real Taris swoop track logged "entered (Unknown)" even though
            // m03mg.are's own MiniGame/Type is 1, so whatever CSWMiniGame+0x80 holds at the
            // instant the pointer first goes non-null is not yet the loaded type. The kind is a
            // local label only and gates nothing, so rather than guess at the fill order this
            // logs the LATER settle too and lets a live pass read the answer off the log.
            if (inMiniGame && LocalKind != _lastLoggedKind)
            {
                _lastLoggedKind = LocalKind;
                PluginHost.Log($"minigame: kind settled to {LocalKind}");
            }

            return;
        }

        _wasInMiniGame = inMiniGame;
        _lastLoggedKind = LocalKind;
        PluginHost.Log(inMiniGame
            ? $"minigame: entered ({LocalKind}) — peers will park this player's phantom"
            : "minigame: exited — back in the shared world");
    }

    private static MinigameKind ReadKind(nint miniGame) =>
        *(int*)(miniGame + MiniGameTypeOffset) switch
        {
            1 => MinigameKind.Swoop,
            2 => MinigameKind.Turret,
            _ => MinigameKind.Unknown,
        };

    private static nint ReadClientInternal()
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return 0;
        }

        nint client = Deref(appManager + AppClientOffset);
        if (client == 0)
        {
            return 0;
        }

        return Deref(client + ClientInternalOffset);
    }

    /// <summary>Returns the raw <c>CSWMiniGame*</c> (0 = none), given an already-resolved
    /// <c>CClientExoAppInternal*</c>.</summary>
    private static nint ReadMiniGamePointer(nint clientInternal)
    {
        nint module = Deref(clientInternal + ClientModuleOffset);
        if (module == 0)
        {
            return 0;
        }

        nint area = Deref(module + ModuleAreaOffset);
        return area == 0 ? 0 : Deref(area + AreaMiniGameOffset);
    }

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;
}
