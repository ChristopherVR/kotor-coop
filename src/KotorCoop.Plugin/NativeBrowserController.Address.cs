namespace KotorCoop.Plugin;

// The address box (2026-09-18): the browser's last listbox row, "Connect to an address...",
// which — highlighted and confirmed exactly like a server row (Connect button, or Enter/A on
// the row) — floats the engine's text-entry panel (PasswordEntryModal, Ask.Address) over the
// browser, pre-filled with the last address used. OK composes `connect host:port` and the
// browser closes; the line is published to the agent's console channel only once the sweep
// has confirmed the panel gone, the same ordering the row Connect keeps. The agent then runs
// the exact path a typed `connect` takes, including asking for a password (through the
// existing serial → PasswordPromptController → same modal over the fresh main menu) when the
// host answers E_PASSWORD. See docs/research/server-browser.md, "The address box".
//
// WHY A ROW AND NOT A FOURTH BUTTON: CSWGuiSaveLoad has exactly three buttons (Load/Save,
// Back, Delete), all spoken for (Connect, the stock close, Host), and manufacturing a
// control by hand is the one thing native-browser-panel.md's crash register forbids. A row
// comes from the engine's own clone path, like every other row here.
internal sealed unsafe partial class NativeBrowserController
{
    private const string AddressRowCaption = "Connect to an address...";

    /// <summary>Shown in the details pane's caption slot while the address row is highlighted.</summary>
    private const string AddressRowHint = "Press Enter to type an address";

    /// <summary>A highlight-restore key that no published server address can equal (a control
    /// character is not printable ASCII, and the block's address cells are).</summary>
    private const string AddressRowMarker = "address-row";

    /// <summary>The sidecar the last typed address is kept in, beside the game executable —
    /// the same home as <c>kotorcoop-console-history.txt</c>. A sidecar rather than coop.ini's
    /// <c>host</c> key because NOTHING in the plugin or the agent rewrites coop.ini after
    /// install (PackageInstaller writes it once; PluginConfig/CoopConfig only read), and a
    /// first writer for a hand-commented player file is not worth one prefill. coop.ini's
    /// <c>host</c>/<c>port</c> still seed the box when the sidecar is absent.</summary>
    private const string LastAddressFile = "kotorcoop-last-address.txt";

    /// <summary>The listbox index of the address row, or -1 until rows exist.</summary>
    private int _addressRow = -1;

    /// <summary>Set by the click handler; the modal opens on the next plugin frame.</summary>
    private bool _addressPromptPending;

    /// <summary>A composed <c>connect</c> line is waiting in <see cref="_pendingLine"/> for the
    /// browser sweep to confirm the panel gone.</summary>
    private bool _addressConnectPending;

    /// <summary>The native modal is latched off: after the close, open the developer console
    /// with <c>connect </c> pre-typed instead (the verified fallback surface).</summary>
    private bool _addressConsoleFallbackPending;

    /// <summary>Which question the floating modal is asking, so an Answered/Dismissed outcome
    /// is routed to the right flow.</summary>
    private PasswordEntryModal.Ask _openAsk;

    private readonly byte[] _pendingLine = new byte[SharedBlock.ConsoleCommandLength];
    private int _pendingLineLength;

    private static string? _lastAddressLogged;

    /// <summary>Drops every address-flow flag; no engine calls (the in-world rule).</summary>
    private void ResetAddressState()
    {
        _addressPromptPending = false;
        _addressConnectPending = false;
        _addressConsoleFallbackPending = false;
        _pendingLineLength = 0;
        Array.Clear(_pendingLine);
    }

    /// <summary>
    /// The prefill: the sidecar's last address, else coop.ini's <c>host</c> (+<c>port</c>)
    /// folded into one, else empty. Read at open time only — one small file read per press,
    /// never per frame.
    /// </summary>
    private static string ReadLastAddress()
    {
        try
        {
            string path = Path.Combine(NativeMethods.GetGameDirectory(), LastAddressFile);
            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length > 0 && ConnectAddressRule.TryNormalize(lines[0], out string saved, out _))
                {
                    return saved;
                }
            }
        }
        catch
        {
            // Unreadable sidecar: fall through to coop.ini, then to an empty box.
        }

        return ConnectAddressRule.PrefillFromConfig(PluginConfig.Read("host"), PluginConfig.Read("port"));
    }

    /// <summary>Remembers a successfully composed address for the next prefill. Best-effort:
    /// a failed write costs the player one retype, never the connect.</summary>
    private static void RememberAddress(string address)
    {
        if (address.Length == 0)
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(NativeMethods.GetGameDirectory(), LastAddressFile), address + "\n");
        }
        catch (Exception ex)
        {
            if (_lastAddressLogged != ex.GetType().Name)
            {
                _lastAddressLogged = ex.GetType().Name;
                PluginHost.Log($"native browser: could not remember the last address ({ex.GetType().Name})");
            }
        }
    }
}
