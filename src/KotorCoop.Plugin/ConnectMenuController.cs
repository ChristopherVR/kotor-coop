using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Adds a "Co-op" action to the main menu: the dormant BTN_WARP control is rebound so a
/// click bumps the connect serial in the shared block, and the agent — attached and
/// waiting — starts the session from coop.ini. See docs/research/connect-flow.md.
///
/// Deliberately opt-in (coop.ini menu_button=1 → the agent sets the enable flag). The
/// rebind is engine-call code that has not been verified live, and it is guarded to
/// fail safe: a control pointer that does not look right is skipped rather than called.
///
/// The interactive IP-entry panel (typing the host in-game via the reused save-name
/// popup) is designed in the research but not built here; this ships the button and the
/// coop.ini-driven connect, which is the lower-risk half.
/// </summary>
internal sealed unsafe class ConnectMenuController
{
    private readonly SharedBlockView _block;

    public ConnectMenuController(SharedBlockView block) => _block = block;

    /// <summary>The one live controller, so the static thiscall callback the engine invokes
    /// can reach the block. There is a single plugin instance per process.</summary>
    private static ConnectMenuController? _instance;

    private int _logged;

    /// <summary>No-op while the button is shelved; kept for the controller contract.</summary>
    public void Invalidate()
    {
    }

    public void OnFrame()
    {
        _instance = this;

        // DISABLED — the main-menu Co-op button is shelved. Surfacing the engine's dormant
        // BTN_WARP crashes the game: verified live that merely setting its visible bit (0x2 at
        // control+0x44) faults the menu loop within a few seconds, because BTN_WARP is a
        // dev-only control the retail menu never finishes initialising (no valid text object /
        // border textures / navigable links), so drawing or hit-testing it dereferences
        // uninitialised state. A .gui-file reposition never draws either (the ctor re-hides it
        // after layout). A real button would need either full control initialisation or fresh
        // GUI construction — both high-risk and out of scope. Until then, connect from the menu
        // via the ~ console (`host` / `connect <ip>`), which works there through FuncInterpHook.
        //
        // Left wired (behind ConnectMenuEnabled, default off) so a future safe approach can slot
        // in here; the resolve/un-hide helpers in Odyssey remain for that work.
        return;
    }

    /// <summary>
    /// The engine invokes this when BTN_WARP is clicked or accepted:
    /// <c>fn(owner /*ecx*/, firingControl /*stack*/)</c>, thiscall, callee-clean. It only
    /// records the request; the agent does the connecting.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnCoopClicked(nint owner, nint firingControl)
    {
        try
        {
            if (_instance is { } self)
            {
                self._block.ConnectSerial++;
                self.LogOnce("menu: Co-op clicked; asking the agent to connect");
            }
        }
        catch
        {
            // A callback that throws into the engine's stack would take the game down.
        }
    }

    private void LogOnce(string message)
    {
        if (_logged < 8)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
