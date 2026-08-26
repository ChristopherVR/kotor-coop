using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The in-game server browser as a REAL game menu: the engine's own save/load screen
/// (<c>CSWGuiSaveLoad</c>, asset <c>saveload.gui</c>) constructed, repurposed and shown at the
/// main menu, with one engine-built listbox row per discovered co-op server. Native font,
/// borders, scrollbar, mouse and keyboard — no overlay text. Opened by the main-menu Co-op
/// button (or F9 at the menu); the in-world F9 browser remains the queued-text overlay, which
/// this class supersedes only at the menu.
///
/// Every engine call here is the sequence verified instruction-by-instruction in
/// docs/research/native-browser-panel.md: the panel constructor is already proven live (the
/// co-op save load constructs one every run), rows are cloned from the panel's own prototype
/// row exactly the way PopulateGameList does, and the STOCK Back button is left wired because
/// retail's close path (mark the panel deleted, rebuild the main menu) is precisely what we
/// want. The two lifetime rules that matter, both verified:
///
///   1. While the panel is up, THE MAIN MENU OBJECT IS FREED (the manager sweep reclaims any
///      panel marked 0x400, and showing a child marks the menu). Nothing may resolve
///      clientInternal+0x280 until the close path has built a fresh menu —
///      <see cref="IsOpen"/> gates the other menu-touching controllers.
///   2. The listbox OWNS its rows (AddControls flag): refreshing the list is just another
///      AddControls (it frees the old rows itself), and panel teardown frees everything. No
///      pointer we hand the engine may come from the managed heap; rows are engine-allocated.
///
/// Connect on a locked row (with no agent-held password — <see cref="SharedBlock.BrowserEntryPasswordHeld"/>)
/// floats the <see cref="PasswordEntryModal"/> OVER this panel before anything closes, so the
/// player is never round-tripped through the main menu to be asked; see OnFrameWhileOpen.
/// </summary>
internal sealed unsafe partial class NativeBrowserController
{
    // ---- engine bindings (docs/research/native-browser-panel.md) -----------------

    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNew =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWGuiSaveLoad::Constructor(guiManager, bSaveMode=0, bFromMainMenu=1) — proven live.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, nint> PanelCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, nint>)0x006CC680;

    // CSWGuiSaveLoadEntry::Constructor — no args, returns this.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> EntryCtor =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x006CB940;

    // CSWGuiButton::Initialize_2(rect, proto) — the whole row clone: extent, borders, font.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint, void> CloneFromProto =
        (delegate* unmanaged[Thiscall]<nint, nint, nint, void>)0x00418D80;

    // CSWGuiListBox::AddControls(&{data,count,cap}, bOwnRows, bRestoreExtents, flagC) —
    // clears (and, owning, FREES) the old rows first, then installs and lays out ours.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void> AddControls =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void>)0x0041C1D0;

    // CSWGuiListBox::SetSelectedControl(index, bNotify) — restores the highlight after a refresh.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetSelectedControl =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0041C040;

    // CSWGuiManager::AddPanel(panel, flags2=2, bPlayOpenSound=1) — the retail show call.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, void> AddPanel =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, void>)0x0040BC70;

    // CSWGuiManager::PanelExists(panel) — pointer compare over the registered array; the safe
    // way to learn our panel was swept without ever dereferencing it.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> PanelExists =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    // CClientExoApp::DisplayMainMenu(0) — rebuilds the menu the show path freed; the stock
    // Back handler's exact close call.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> DisplayMainMenu =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x005ED420;

    // CSWGuiTextParams::SetText(CExoString*) — captions; sets str_ref=-1 so the literal wins.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> SetText =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00415E00;

    // CSWGuiBorderParams::SetFillImage(CResRef*, bForce) — the details-pane images.
    //
    // ⚠ A CResRef, NOT a CExoString, however much the name suggests otherwise. Its comparison
    // callee (0x406DC0) is `repe cmpsd` with ecx = 4 — sixteen raw bytes compared in place —
    // and the engine's own caller copies a 16-byte record out of the save entry (ShowGame,
    // 0x6C8B39..0x6C8B43). Handing it a CExoString makes the engine read a POINTER and a
    // LENGTH as the texture name; that is what drew the portraits as blank white fills.
    // bForce = 0 early-outs when the name is unchanged, which is what we want.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, void> SetFillImage =
        (delegate* unmanaged[Thiscall]<nint, nint, int, void>)0x00414C00;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> ExoStringCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ExoStringDtor =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E5C20;

    // CSWGuiButton::SetEnabled(bool) — vtbl+0x88, 0x00418DB0. Retail's ShowGame calls this on
    // the delete button when a real save row is selected (docs/research/native-browser-panel.md
    // §4); co-op rows never trigger ShowGame (event 0x00 is deliberately left unbound), so the
    // Host button (the repurposed delete control) may never receive it. The doc describes this
    // call only as "a text-color toggle" and never independently verifies that, so this is a
    // best-effort fix for a reported "Host does nothing" bug, not a confirmed root cause.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetEnabled =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x00418DB0;

    // ---- object layout -----------------------------------------------------------

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;
    private const int ClientInternalOffset = 0x04;
    private const int ClientInternalMainMenu = 0x280;
    private const int MenuGuiManager = 0x18;
    private const int MenuStateBits = 0x44;
    private const int MenuAlive = 0x140C;

    private const int PanelSize = 0x1160;
    private const nint PanelVtable = 0x00757650;
    private const int PanelStateBits = 0x44;
    private const int PanelTitleParams = 0x15C;
    private const int PanelListBox = 0x934;
    private const int PanelSelection = 0xBFA;      // listbox +0x2C6
    private const int PanelProtoRow = 0xC00;       // listbox +0x2CC
    private const int PanelConnectButton = 0xC14;  // BTN_SAVELOAD control (embedded)
    private const int PanelConnectParams = 0xD80;
    private const int PanelHostButton = 0xF9C;     // BTN_DELETE, repurposed as "Host"
    private const int PanelHostParams = 0x1108;
    private const int PanelHostFlags = 0xFE0;      // BTN_DELETE +0x44
    private const int PanelPlanetParams = 0x3DC;
    private const int PanelAreaParams = 0x51C;
    private const int PanelShotCaptionParams = 0x29C;

    /// <summary>Details-pane image labels' border params (screenshot + 3 portraits).</summary>
    private static readonly int[] PanelImageParams = [0x224, 0x5E4, 0x724, 0x864];

    private const int EntrySize = 0x248;
    private const int EntryCaptionParams = 0x16C;
    private const int EntryFlags = 0x1C4;
    private const int RowHeight = 0x2A;

    private const int MaxRows = SharedBlock.BrowserCapacity;
    private const ushort NoSelection = 0xFFFF;

    // ---- state ---------------------------------------------------------------------

    private readonly SharedBlockView _block;

    /// <summary>The one live controller, reachable from the engine's thiscall callback.</summary>
    private static NativeBrowserController? _instance;

    /// <summary>Latched when a guard refuses: the overlay browser takes over permanently.</summary>
    private static bool _broken;

    private static bool _openRequested;

    private nint _panel;
    private nint _guiManager;
    private bool _closing;
    private bool _connectPending;
    private bool _hostPending;
    private int _pendingSelection;

    /// <summary>The <see cref="SharedBlock.BrowserEntryAddress"/> of whichever row was last
    /// confirmed highlighted while the shown rows still matched the published list — i.e. before
    /// any pending reorder. A row REBUILD restores the highlight to wherever this address now
    /// sits (or drops it) instead of blindly reapplying the old numeric slot, which is what let
    /// a beacon-timing reorder silently swap the details pane / connect target underneath a
    /// player's click. See <see cref="PopulateRows"/>.</summary>
    private string? _selectedAddress;

    private uint _shownListSerial;
    private int _serverRows;
    private bool _f9WasDown;
    private int _logged;

    /// <summary>The password modal this controller floats OVER its own panel when Connect is
    /// pressed on a locked row with no agent-held password — the retail pattern exactly: the
    /// stock save screen pushes this very popup over itself. Asking here, before anything
    /// closes, is what keeps the player out of the menu round-trip.</summary>
    private readonly PasswordEntryModal _passwordModal = new();

    /// <summary>Set by <see cref="OnConnectClicked"/>; the open runs on the next plugin frame
    /// so panel construction never happens inside the engine's event dispatch.</summary>
    private bool _promptOpenPending;

    public NativeBrowserController(SharedBlockView block) => _block = block;

    /// <summary>True while our panel is registered: the main menu pointer is DANGLING and the
    /// menu-touching controllers must not run (see the class comment, rule 1).</summary>
    public static bool IsOpen => _instance is { _panel: not 0 };

    /// <summary>False once a guard latched the native path off; callers fall back to the overlay.</summary>
    public static bool Available => !_broken;

    /// <summary>Called by the main-menu Co-op button (same thread).</summary>
    public static void RequestOpen() => _openRequested = true;

    public void OnFrame()
    {
        _instance = this;

        if (Odyssey.HasServer)
        {
            // In a world there is no main menu and no panel; anything we still hold is long
            // freed. Drop bookkeeping only — no engine calls.
            _panel = 0;
            _closing = false;
            _connectPending = false;
            _openRequested = false;
            _promptOpenPending = false;
            _passwordModal.Forget();
            return;
        }

        if (_panel != 0)
        {
            OnFrameWhileOpen();
            return;
        }

        bool f9 = NativeMethods.IsKeyDownHere(NativeMethods.VkF9);
        bool f9Pressed = f9 && !_f9WasDown;
        _f9WasDown = f9;

        // Same contract as the overlay: the BLOCK can open the panel too (the agent or a
        // diagnostic writes BrowserOpen=1), not just our own button/key handling. While we
        // are closed we own the field, so a nonzero here is an external request; it stays
        // set until the menu exists and the open succeeds, then tracks the real state.
        bool blockRequested = _block.BrowserOpen != 0;

        if ((_openRequested || f9Pressed || blockRequested) && !_broken)
        {
            _openRequested = false;
            TryOpen();
        }
        else
        {
            _openRequested = false;
        }
    }

    private void OnFrameWhileOpen()
    {
        _openRequested = false;

        // The sweep may have freed the panel (stock Back/Escape, or our own close): detect by
        // registration, never by dereference.
        if (_guiManager == 0 || PanelExists(_guiManager, _panel) == 0)
        {
            _panel = 0;
            _closing = false;
            _block.BrowserOpen = 0;
            _promptOpenPending = false;

            // If the browser died with the password modal still floating over it (an
            // engine-side close we did not initiate), the modal must not outlive its
            // surface: hand it to the same sweep and forget it.
            _passwordModal.AbandonToSweep();

            if (_connectPending)
            {
                _connectPending = false;

                // Selection first, then the serial — the agent reads the serial last. The
                // panel is freed and the fresh menu exists, so the agent's connect (and the
                // load it eventually stages) can never race the GUI teardown.
                _block.BrowserSelection = (uint)_pendingSelection;
                _block.RequestBrowserConnect();
                Log($"native browser: row {_pendingSelection} confirmed; connect handed to the agent");
            }
            else if (_hostPending)
            {
                _hostPending = false;
                _block.RequestBrowserHost();
                Log("native browser: hosting; handed to the agent");
            }
            else
            {
                Log("native browser: closed");
            }

            return;
        }

        // The password modal floating over this panel, when there is one. While it is up the
        // browser beneath must not close, refresh or take keys — the modal owns the screen.
        switch (_passwordModal.Poll())
        {
            case PasswordEntryModal.Outcome.StillOpen:
                return;

            case PasswordEntryModal.Outcome.Answered:
                // ORDERING: the password goes to the agent FIRST, the connect serial only
                // after the browser sweep (the normal _connectPending handoff below) — so
                // the agent already holds the password when the join starts. Publishing
                // here is safe: the modal is freed (Poll saw the sweep take it), and the
                // console channel is plain block memory, no GUI teardown to race.
                _passwordModal.PublishAnswer(_block);
                _connectPending = true;
                Close();
                Log($"native browser: password answered for row {_pendingSelection}; connecting");
                return;

            case PasswordEntryModal.Outcome.Dismissed:
                if (!PasswordEntryModal.Available)
                {
                    // The modal broke mid-ask: fall back to the fully verified old flow —
                    // close and hand the connect over; the agent sees the locked row with
                    // no password and re-asks via the latched console fallback.
                    _connectPending = true;
                    Close();
                    Log("native browser: password modal latched off; using the close-then-ask flow");
                }
                else
                {
                    // Cancel (or empty OK, or alt-tab): the player stays in the browser,
                    // the row still selected. Nothing was sent anywhere.
                    Log("native browser: password prompt cancelled; staying in the browser");
                }

                return;
        }

        // A locked-row Connect recorded by the click handler: float the prompt over the
        // browser now, on the plugin frame, using the gui manager OUR panel was registered
        // with (the main menu object is freed while the browser is up and must not be
        // resolved). Retail parallel: CSWGuiSaveLoad pushes CSWGuiSaveNamePanel over itself.
        if (_promptOpenPending)
        {
            _promptOpenPending = false;

            if (_passwordModal.TryOpen(_guiManager))
            {
                Log($"native browser: password prompt over the browser (row {_pendingSelection})");
            }
            else
            {
                // Could not float the modal (latched, or a transient refusal): the player's
                // click must not die in a browser that silently does nothing. The old
                // close-then-prompt flow is fully live-verified — take it.
                _connectPending = true;
                Close();
                Log("native browser: password modal unavailable; using the close-then-ask flow");
            }

            return;
        }

        if (_closing)
        {
            return; // marked 0x400; the next sweep frees it — touch nothing.
        }

        // Symmetric block contract: an external write of 0 (agent or diagnostic) closes the
        // panel, exactly as it opens it.
        if (_block.BrowserOpen == 0)
        {
            Close();
            return;
        }

        // F9 closes too (retail Escape/Back already works via the stock handler).
        bool f9 = NativeMethods.IsKeyDownHere(NativeMethods.VkF9);
        if (f9 && !_f9WasDown)
        {
            _f9WasDown = f9;
            Close();
            return;
        }

        _f9WasDown = f9;

        // Mirror the live highlight for the agent/diagnostics. Captured BEFORE the serial check
        // below, while the shown rows still match what BrowserEntry currently holds — once a new
        // serial has landed, the entry at this same index may already be a different server.
        short selected = *(short*)(_panel + PanelSelection);
        if (selected >= 0)
        {
            _block.BrowserSelection = (ushort)selected;
            if (selected < _serverRows)
            {
                _selectedAddress = ReadAscii(
                    _block.BrowserEntry(selected) + SharedBlock.BrowserEntryAddress,
                    SharedBlock.BrowserEntryAddressLength);
            }
        }

        // LAN discovery found something new while the player is looking: rebuild the rows.
        // AddControls frees the old ones itself (the listbox owns them).
        if (_block.BrowserListSerial != _shownListSerial)
        {
            _shownListSerial = _block.BrowserListSerial;
            PopulateRows(_panel, restoreAddress: _selectedAddress);
        }
    }

    // ---- close and connect -----------------------------------------------------------

    /// <summary>The stock Back handler's exact close: mark the panel for the sweep, rebuild
    /// the main menu. After this, the panel memory is off limits.</summary>
    private void Close()
    {
        uint state = *(uint*)(_panel + PanelStateBits);
        *(uint*)(_panel + PanelStateBits) = (state & ~0x300u) | 0x400u;
        _closing = true;

        nint client = ResolveClient();
        if (client != 0)
        {
            DisplayMainMenu(client, 0);
        }
    }

    /// <summary>
    /// The engine invokes this for a Connect-button click or an Enter on a row:
    /// <c>fn(owner /*ecx*/, firingControl /*stack*/)</c>. Selection is guaranteed current
    /// (the listbox updates it on mouse-down, before any click dispatch).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnConnectClicked(nint owner, nint firingControl)
    {
        try
        {
            if (_instance is not { _panel: not 0, _closing: false } self)
            {
                return;
            }

            short selected = *(short*)(self._panel + PanelSelection);
            if (selected < 0 || selected >= self._serverRows)
            {
                return; // nothing highlighted, or the placeholder row — stay open
            }

            self._pendingSelection = selected;

            // A locked row with no agent-held password: ask HERE, over the browser, before
            // anything closes — no round trip through the main menu. The open itself is
            // deferred to the next plugin frame (OnFrameWhileOpen) so panel construction
            // never runs inside the engine's event dispatch; the handler only records.
            // BrowserEntryPasswordHeld is the agent's "I already hold one for you" flag —
            // a player who configured a password in coop.ini/CLI must not be asked again.
            byte* entry = self._block.BrowserEntry(selected);
            if (entry[SharedBlock.BrowserEntryLocked] != 0
                && entry[SharedBlock.BrowserEntryPasswordHeld] == 0
                && PasswordEntryModal.Available
                && !self._passwordModal.IsOpen)
            {
                self._promptOpenPending = true;
                return; // the browser stays up; the modal floats over it
            }

            self._connectPending = true;
            self.Close();
        }
        catch
        {
            // A managed exception must never unwind into the engine's event dispatch.
        }
    }

    /// <summary>
    /// The Host button (the repurposed Delete control): start a session here instead of
    /// joining someone else's. Same shape as <see cref="OnConnectClicked"/> — record and
    /// close; the agent acts once the panel is gone.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnHostClicked(nint owner, nint firingControl)
    {
        try
        {
            // Unconditional and first, before any guard can return early: this is what lets a
            // live pass tell "the click never reached the handler" from "it reached the handler
            // and something after this silently failed" — see the Host-button lead in
            // docs/research/native-browser-panel.md.
            PluginHost.Log("native browser: Host button clicked");

            if (_instance is not { _panel: not 0, _closing: false } self)
            {
                PluginHost.Log(
                    "native browser: Host click ignored - no live instance/panel, or already closing");
                return;
            }

            self._hostPending = true;
            self.Close();
        }
        catch
        {
            // A managed exception must never unwind into the engine's event dispatch.
        }
    }

    // ---- plumbing --------------------------------------------------------------------

    private void SetCaption(nint textParams, string text) =>
        WithExoString(text, s => SetText(textParams, s));

    /// <summary>Builds an engine CExoString on the stack, runs <paramref name="use"/>, frees
    /// it. SetText/SetFillImage copy the string, so the temporary may die right after.</summary>
    /// <summary>
    /// Runs <paramref name="use"/> with a pointer to a 16-byte <c>CResRef</c> — raw
    /// NUL-padded characters, no pointer and no length.
    ///
    /// ⚠ THIS IS NOT A CExoString, AND THE DIFFERENCE IS WHY THE PORTRAITS WERE WHITE.
    /// <c>CSWGuiBorderParams::SetFillImage</c> takes a CResRef: its comparison callee
    /// (0x406DC0) is <c>repe cmpsd</c> with <c>ecx = 4</c> — sixteen bytes compared in place —
    /// and the engine's own caller (<c>ShowGame</c>, 0x6C8B43) hands it a 16-byte record
    /// copied out of the save entry. Passing a CExoString instead makes the engine read a
    /// POINTER and a LENGTH as the texture's name, which resolves to nothing and draws a
    /// blank white fill. The blanking calls that came before had the same defect and hid it
    /// perfectly, because garbage blanks just as well as an empty name does.
    /// </summary>
    private static void WithResRef(string ascii, Action<nint> use)
    {
        byte* storage = stackalloc byte[16];
        new Span<byte>(storage, 16).Clear();

        int count = Math.Min(ascii.Length, 16);
        for (int i = 0; i < count; i++)
        {
            char c = ascii[i];
            storage[i] = (byte)(c < 0x20 || c > 0x7E ? (byte)0 : (byte)c);
        }

        use((nint)storage);
    }

    private static void WithExoString(string ascii, Action<nint> use)
    {
        byte* storage = stackalloc byte[16];
        new Span<byte>(storage, 16).Clear();

        byte* buffer = stackalloc byte[96];
        int count = Math.Min(ascii.Length, 94);
        for (int i = 0; i < count; i++)
        {
            char c = ascii[i];
            buffer[i] = (byte)(c < 0x20 || c > 0x7E ? '?' : c);
        }

        buffer[count] = 0;

        ExoStringCtor((nint)storage, (nint)buffer);
        try
        {
            use((nint)storage);
        }
        finally
        {
            ExoStringDtor((nint)storage);
        }
    }

    private static nint ResolveClient()
    {
        nint appManager = *(nint*)AppManagerPtr;
        return appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
    }

    private static nint ResolveMainMenu()
    {
        nint client = ResolveClient();
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = *(nint*)(client + ClientInternalOffset);
        if (clientInternal == 0)
        {
            return 0;
        }

        nint menu = *(nint*)(clientInternal + ClientInternalMainMenu);
        return LooksLikeHeapPointer(menu) ? menu : 0;
    }

    /// <summary>The game reads SAVES: relative to its own directory; the plugin lives there
    /// too (it IS the game's dinput8.dll), so a plain filesystem check suffices.</summary>
    private static void EnsureASaveDirectoryExists()
    {
        try
        {
            string saves = Path.Combine(NativeMethods.GetModuleDirectory("dinput8.dll"), "saves");
            if (!Directory.Exists(saves))
            {
                Directory.CreateDirectory(saves);
            }

            using IEnumerator<string> any = Directory.EnumerateDirectories(saves).GetEnumerator();
            if (!any.MoveNext())
            {
                // The same slot the co-op menu-load stages into anyway.
                Directory.CreateDirectory(Path.Combine(saves, "000001 - AUTOSAVE"));
            }
        }
        catch
        {
            // If this failed the ctor's own zero-saves guard still catches it (born-dead check).
        }
    }

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    /// <summary>A guard refused: latch the native path off so the Co-op button falls back to
    /// the overlay browser instead of retrying a wrong assumption every frame.</summary>
    private void Break(string what)
    {
        _broken = true;
        Log($"native browser: guard refused ({what}); falling back to the overlay browser");
    }

    private void Log(string message)
    {
        if (_logged < 32)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
