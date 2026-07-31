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

    private nint _lastButton;
    private bool _branded;
    private int _logged;

    /// <summary>Reset when the menu is torn down (a load leaves the menu), so the button is
    /// re-shown and rebound on a return to the menu.</summary>
    public void Invalidate()
    {
        _branded = false;
        _lastButton = 0;
    }

    public void OnFrame()
    {
        _instance = this;

        // Opt-in only, and only while the menu exists (a server means we are in a world,
        // where the main menu — and BTN_WARP — is gone).
        if (!_block.ConnectMenuEnabled || Odyssey.HasServer)
        {
            _branded = false;
            _lastButton = 0;
            return;
        }

        nint button = Odyssey.GetCoopMenuButton();
        if (button == 0)
        {
            _branded = false;
            _lastButton = 0;
            return; // logo movies still playing, or not on the menu
        }

        // The menu ctor re-hides BTN_WARP every time it is (re)built, so the visible bit must
        // be re-asserted each frame — this is the one write that makes it draw AND take clicks.
        Odyssey.SetControlVisible(button);

        // A changed control pointer means the menu was rebuilt (returned from a load); re-brand.
        if (button != _lastButton)
        {
            _lastButton = button;
            _branded = false;
        }

        if (!_branded)
        {
            _branded = true;
            Odyssey.SetControlText(button, "Coop");
            Odyssey.BindControlClick(
                button, 0x1, (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnCoopClicked);
            LogOnce("menu: Co-op button shown, branded and bound");
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
