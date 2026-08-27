namespace KotorCoop.Plugin;

/// <summary>
/// Reads whether the LOCAL player is inside the engine's own minigame mode — a swoop race
/// or a forced turret sequence — every frame, cheaply, and says so in the log on the edges.
///
/// <c>CSWCModule::GetMiniGame</c> (0x00641980) is three instructions once the module
/// pointer is in hand: <c>[module+0x48]</c> (area) then, if non-null, <c>[area+0x264]</c>
/// (mini_game). Both are named in the symbol database (<c>area</c>, <c>mini_game</c>) and
/// the whole chain is VERIFIED by static disassembly — see docs/research/minigames.md,
/// which also has the INFERRED half: <c>CSWMiniGame+0x80</c> (<c>type</c>) is 1 or 2
/// depending on which GFF the minigame resref loads, almost certainly swoop vs. turret, but
/// that distinction is not needed for anything this controller does and is not read here.
///
/// Deliberately a SEPARATE pointer chain from <see cref="Odyssey"/> rather than an addition
/// to it (Odyssey is not a partial class, and item 8's brief asks for a new file rather than
/// touching it while another change is in flight there) — the four constants below are the
/// same well-known addresses <c>EngineGlobals</c> uses on the agent side of the same walk,
/// just re-derefed in-process because the plugin can do that in nanoseconds where the
/// agent's cross-process read costs a syscall.
///
/// This controller does not gate anything itself. The actual policy — parking a peer's
/// phantom, and suppressing forwarded damage / this instance's own presence publish while
/// in a minigame — is decided from <c>PlayerState.FlagInMinigame</c> on the wire, which
/// <c>KotorGameBridge.IsInMinigame</c> (agent side) reads independently through the same
/// pointer chain via <c>EngineGlobals.TryIsInMiniGame</c>, and applied in
/// <c>CoopAgent.cs</c> (peer filter, <c>PumpEnemies</c>, <c>PumpParty</c>,
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

    private static bool _wasInMiniGame;

    /// <summary>The local player's minigame state as of the last <see cref="OnFrame"/>
    /// pass. False both when the engine says so and when the chain could not be read (no
    /// client world yet) — the same "absent means no" the engine's own GetMiniGame uses.</summary>
    public static bool LocalInMinigame { get; private set; }

    public static void OnFrame()
    {
        bool inMiniGame = ReadIsInMiniGame();
        LocalInMinigame = inMiniGame;

        if (inMiniGame == _wasInMiniGame)
        {
            return;
        }

        _wasInMiniGame = inMiniGame;
        PluginHost.Log(inMiniGame
            ? "minigame: entered (swoop/turret) — peers will park this player's phantom"
            : "minigame: exited — back in the shared world");
    }

    private static bool ReadIsInMiniGame()
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return false;
        }

        nint client = Deref(appManager + AppClientOffset);
        if (client == 0)
        {
            return false;
        }

        nint clientInternal = Deref(client + ClientInternalOffset);
        if (clientInternal == 0)
        {
            return false;
        }

        nint module = Deref(clientInternal + ClientModuleOffset);
        if (module == 0)
        {
            return false;
        }

        nint area = Deref(module + ModuleAreaOffset);
        if (area == 0)
        {
            return false;
        }

        return Deref(area + AreaMiniGameOffset) != 0;
    }

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;
}
