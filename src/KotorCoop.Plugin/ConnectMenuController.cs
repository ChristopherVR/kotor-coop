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

    private bool _bound;
    private int _logged;

    /// <summary>Rebind on a return to the menu (a load tears the menu down).</summary>
    public void Invalidate() => _bound = false;

    public void OnFrame()
    {
        _instance = this;

        // Opt-in only, and only while the menu exists (a server means we are in a world, where
        // the main menu — and BTN_WARP — is gone).
        if (!_block.ConnectMenuEnabled || Odyssey.HasServer)
        {
            _bound = false;
            return;
        }

        nint button = Odyssey.GetCoopMenuButton();
        if (button == 0)
        {
            _bound = false;
            return; // logo movies still playing, or not on the menu
        }

        // The menu constructor hides BTN_WARP immediately after running the .gui layout
        // (0x0067C669 clears bit 0x2 of control+0x44), so the visible bit must be re-asserted —
        // that single bit gates BOTH drawing and mouse hit-testing. The control is otherwise
        // fully constructed: its flags differ from a working button's by exactly this bit, and
        // its extent already places it one row below Quit.
        Odyssey.SetControlVisible(button);

        if (!_bound)
        {
            _bound = true;

            // Relabel the stock dev text ("Warp") through the engine's own SetText, which also
            // refreshes the linked text object and pins str_ref to -1 so the literal wins. Safe
            // now that the control address is right — the earlier "relabel crash" was the same
            // embedded-vs-pointer bug writing into the executable's static data.
            Odyssey.SetControlText(button, "Co-op");

            Odyssey.BindControlClick(
                button, 0x1, (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnCoopClicked);
            LogOnce("menu: Co-op button shown, labelled and bound");
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
