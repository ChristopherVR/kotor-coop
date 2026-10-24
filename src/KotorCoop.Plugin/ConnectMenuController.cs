using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Adds a "Co-op" action to the main menu: the dormant BTN_WARP control is rebound so a
/// click opens the server browser, and the agent — attached and waiting — starts the
/// session from the player's pick. See docs/research/connect-flow.md.
///
/// ON BY DEFAULT: the button is simply THERE whenever the plugin is loaded — agent or no
/// agent — because the plugin IS the co-op install and a menu entry that only appears when
/// an external process has both started and been configured reads as a broken install.
/// (It began life opt-in while the GUI rebind awaited live verification; that verification
/// has long since happened — the button, the relabel and the browser it opens are all
/// live-proven.) Opting out is plugin-side: <c>coop.ini menu_button = 0</c>, read here
/// directly, so no agent needs to exist for the setting to hold in either direction.
///
/// The agent's enable flag in the shared block (<see cref="SharedBlockView.ConnectMenuEnabled"/>)
/// is now an OVERRIDE, not a prerequisite: a nonzero forces the button on even when
/// coop.ini hides it — kept as a diagnostics lever, and so an older agent's enable-write
/// still means what it meant. With no agent at all the field just stays 0 and the coop.ini
/// default (on) decides.
///
/// A press with no agent attached is safe by construction: the click only opens the
/// browser panel, whose list reads all-zero block fields as "no servers" and shows the
/// scanning placeholder; Connect on the placeholder is ignored, and Host merely bumps a
/// serial nobody reads. AgentLauncher auto-starts an agent anyway (coop.ini
/// auto_start_agent, default on), so the empty state is transient.
/// </summary>
internal sealed unsafe class ConnectMenuController
{
    private readonly SharedBlockView _block;

    /// <summary>coop.ini's say, read ONCE at construction (PluginHost points PluginConfig at
    /// the game directory before any controller exists). Per-frame would be a file read on
    /// the game's main loop. Default TRUE: the button exists unless explicitly hidden.</summary>
    private readonly bool _configEnabled = PluginConfig.ReadBool("menu_button", true);

    public ConnectMenuController(SharedBlockView block) => _block = block;

    /// <summary>The one live controller, so the static thiscall callback the engine invokes
    /// can reach the block. There is a single plugin instance per process.</summary>
    private static ConnectMenuController? _instance;

    private nint _boundButton;
    private int _boundWidth, _boundHeight;
    private bool _reportedUnsafeSlot;
    private int _logged;

    /// <summary>Rebind on a return to the menu (a load tears the menu down).</summary>
    public void Invalidate() => _boundButton = 0;

    public void OnFrame()
    {
        _instance = this;

        // Default ON; coop.ini menu_button=0 hides it unless the agent's block flag forces it
        // back (diagnostics override — see the class comment). And only while the menu exists:
        // a server means we are in a world, where the main menu — and BTN_WARP — is gone.
        if ((!_configEnabled && !_block.ConnectMenuEnabled) || Odyssey.HasServer)
        {
            Invalidate();
            return;
        }

        // While the native browser panel is up, the main-menu OBJECT IS FREED (the GUI
        // manager's sweep reclaims it the moment a child screen shows — see
        // docs/research/native-browser-panel.md §2). Resolving or writing the button now would
        // touch dangling memory; the close path builds a fresh menu, whose button must then be
        // re-shown and re-bound from scratch.
        if (NativeBrowserController.IsOpen)
        {
            Invalidate();
            return;
        }

        nint button = Odyssey.GetCoopMenuButton();
        if (button == 0)
        {
            Invalidate();
            return; // logo movies still playing, or not on the menu
        }

        // The menu constructor hides BTN_WARP immediately after running the .gui layout
        // (0x0067C669 clears bit 0x2 of control+0x44), so the visible bit must be re-asserted —
        // that single bit gates BOTH drawing and mouse hit-testing. The control is otherwise
        // fully constructed: its flags differ from a working button's by exactly this bit, and
        // the stock 800x600 extent already places it one row below Quit.
        // A replacement can rebuild the menu between frames, including at the same address.
        // The constructor's hidden flag detects that case before we unhide it again.
        // Changing resolution reloads the .gui on the SAME visible object, restoring
        // "Warp" without running the constructor. Pointer/visibility alone miss it.
        int width = Odyssey.ScreenWidth, height = Odyssey.ScreenHeight;
        bool needsBinding = _boundButton != button || !Odyssey.IsControlVisible(button)
            || _boundWidth != width || _boundHeight != height;
        if (!AlignWithMenuColumn(button))
        {
            Odyssey.SetControlHidden(button);
            Invalidate();
            if (!_reportedUnsafeSlot)
            {
                LogOnce("menu: no safe Co-op button slot; press F9 to open the server browser");
                _reportedUnsafeSlot = true;
            }
            return;
        }
        _reportedUnsafeSlot = false;
        Odyssey.SetControlVisible(button);

        if (needsBinding)
        {
            // Relabel the stock dev text ("Warp") through the engine's own SetText, which also
            // refreshes the linked text object and pins str_ref to -1 so the literal wins. Safe
            // now that the control address is right — the earlier "relabel crash" was the same
            // embedded-vs-pointer bug writing into the executable's static data.
            Odyssey.SetControlText(button, "Co-op");

            Odyssey.BindControlClick(
                button, 0x1, (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnCoopClicked);
            _boundButton = button;
            _boundWidth = width;
            _boundHeight = height;
            LogOnce("menu: Co-op button shown, labelled and bound");
        }
    }

    /// <summary>
    /// Places Co-op beside the live menu column (see <see cref="MenuColumnRule"/>). Only writes
    /// when the button is somewhere else, so a steady menu costs a scan and a compare per frame,
    /// and a menu the engine rebuilds at the same address is put right again.
    /// </summary>
    private bool AlignWithMenuColumn(nint button)
    {
        List<MenuColumnRule.Extent> buttons = Odyssey.GetMainMenuButtonExtents(button);
        MenuColumnRule.Extent current = Odyssey.GetControlExtent(button);
        int width = Odyssey.ScreenWidth, height = Odyssey.ScreenHeight;
        if (!MenuColumnRule.TryFindSlot(buttons, width, height, out MenuColumnRule.Extent slot))
        {
            return MenuColumnRule.IsAvailable(current, buttons, width, height);
        }

        if (current == slot)
        {
            return true;
        }

        Odyssey.SetButtonExtent(button, slot);
        LogOnce($"menu: Co-op button moved from ({current.Left},{current.Top},{current.Width}x{current.Height}) to ({slot.Left},{slot.Top},{slot.Width}x{slot.Height}); viewport={width}x{height}");
        return true;
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
                // Open the server browser rather than connecting blind: bumping the connect
                // serial made the agent dial coop.ini immediately, which would race the
                // player's pick. The browser posts its own connect once a row is confirmed.
                // The native game-menu panel is the browser; the text overlay only serves as
                // the fallback if a native guard latched it off.
                if (NativeBrowserController.Available)
                {
                    NativeBrowserController.RequestOpen();
                }
                else
                {
                    ServerBrowserController.RequestOpen();
                }

                self.LogOnce("menu: Co-op clicked; opening the server browser");
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
