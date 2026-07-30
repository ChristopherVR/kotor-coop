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

    /// <summary>Frames between attempts to bind the button; the menu object appears after
    /// the logo movies, so a periodic retry catches it.</summary>
    private const int IntervalFrames = 30;

    /// <summary>The one live controller, so the static thiscall callback the engine invokes
    /// can reach the block. There is a single plugin instance per process.</summary>
    private static ConnectMenuController? _instance;

    private int _countdown;
    private bool _bound;
    private int _logged;

    /// <summary>Reset when the menu is torn down (a load leaves the menu), so the button is
    /// rebound on a return to the menu.</summary>
    public void Invalidate()
    {
        _bound = false;
    }

    public void OnFrame()
    {
        _instance = this;

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // Opt-in only, and only while the menu exists (a server means we are in a world,
        // where BTN_WARP is not present).
        if (!_block.ConnectMenuEnabled || _bound || Odyssey.HasServer)
        {
            return;
        }

        if (Odyssey.GetMainMenu() == 0)
        {
            return; // logo movies still playing
        }

        if (Odyssey.BindCoopMenuButton(0x1, (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnCoopClicked))
        {
            _bound = true;
            LogOnce("menu: Co-op button bound");
        }
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
