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
/// </summary>
internal sealed unsafe class NativeBrowserController
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
    private uint _shownListSerial;
    private int _serverRows;
    private bool _f9WasDown;
    private int _logged;

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

        // Mirror the live highlight for the agent/diagnostics.
        short selected = *(short*)(_panel + PanelSelection);
        if (selected >= 0)
        {
            _block.BrowserSelection = (ushort)selected;
        }

        // LAN discovery found something new while the player is looking: rebuild the rows.
        // AddControls frees the old ones itself (the listbox owns them).
        if (_block.BrowserListSerial != _shownListSerial)
        {
            _shownListSerial = _block.BrowserListSerial;
            PopulateRows(_panel, restoreSelection: selected);
        }
    }

    // ---- open ----------------------------------------------------------------------

    private void TryOpen()
    {
        nint menu = ResolveMainMenu();
        if (menu == 0)
        {
            return; // logo movies, or mid-rebuild — harmless, try again on the next press
        }

        // The retail show path's own guards: no child screen already up, menu fully alive.
        uint menuState = *(uint*)(menu + MenuStateBits);
        if ((menuState & 0x600) == 0x400 || *(int*)(menu + MenuAlive) == 0)
        {
            return;
        }

        nint guiManager = *(nint*)(menu + MenuGuiManager);
        if (!LooksLikeHeapPointer(guiManager))
        {
            Break("gui manager pointer");
            return;
        }

        // Zero saves on disk births the panel dead behind a modal "no saved games" box (the
        // ctor's PopulateGameList path). A session always stages the autosave slot; make sure
        // it exists outside a session too.
        EnsureASaveDirectoryExists();

        nint panel = OperatorNew(PanelSize);
        if (panel == 0)
        {
            return;
        }

        PanelCtor(panel, guiManager, 0, 1);

        if (*(nint*)panel != PanelVtable)
        {
            Break("panel vtable after construction");
            return; // unregistered; abandoning leaks one panel, once, on a latched-off path
        }

        if ((*(uint*)(panel + PanelStateBits) & 0x600) == 0x400)
        {
            // Born dead (the zero-saves case slipped through). It was never registered, so
            // the sweep will not free it — call its own deleting destructor.
            var destroy = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)panel));
            destroy(panel, 1);
            Log("native browser: panel born closed (no saves?); not showing");
            return;
        }

        // Repurpose before showing: title and buttons, then displace the stock handlers
        // (AddEvent replaces per event id). Back is left stock — its handler IS the clean
        // close. The Delete button becomes "Host", so both halves of starting a session —
        // hosting one and joining one — live in this single in-game screen.
        SetCaption(panel + PanelTitleParams, "CO-OP SERVERS");
        SetCaption(panel + PanelConnectParams, "Connect");
        SetCaption(panel + PanelHostParams, "Host");

        // Its stock DeleteGame binding would delete a save; replace it before it can fire,
        // and make sure the button is visible and hittable (bit1 gates both).
        Odyssey.BindControlClick(
            panel + PanelHostButton,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnHostClicked);
        *(uint*)(panel + PanelHostFlags) |= 2u;

        Odyssey.BindControlClick(
            panel + PanelConnectButton,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnConnectClicked);

        _shownListSerial = _block.BrowserListSerial;
        PopulateRows(panel, restoreSelection: -1);

        // Show, then mark the menu for the sweep — retail order. From here until the close
        // path rebuilds it, the main menu pointer is poison. Re-read the state bits: the
        // panel constructor ran since the guard captured them.
        AddPanel(guiManager, panel, 2, 1);
        *(uint*)(menu + MenuStateBits) = (*(uint*)(menu + MenuStateBits) & ~0x300u) | 0x400u;

        _panel = panel;
        _guiManager = guiManager;
        _closing = false;
        _connectPending = false;
        _block.BrowserOpen = 1;
        Log("native browser: open");
    }

    /// <summary>
    /// Builds one engine-cloned row per published server and hands them to the listbox, which
    /// frees whatever rows it held (stock save rows on open, our own on refresh). With no
    /// servers yet, a single unconnectable placeholder row keeps the panel honest.
    /// </summary>
    private void PopulateRows(nint panel, int restoreSelection)
    {
        nint proto = *(nint*)(panel + PanelProtoRow);
        if (!LooksLikeHeapPointer(proto))
        {
            Break("listbox proto row");
            return;
        }

        int count = (int)Math.Min(_block.BrowserCount, (uint)MaxRows);
        _serverRows = count;

        int* rect = stackalloc int[4];
        rect[0] = 0;
        rect[1] = 0;
        rect[2] = 0;
        rect[3] = RowHeight;

        nint* rows = stackalloc nint[MaxRows > 0 ? MaxRows : 1];
        int built = 0;

        if (count == 0)
        {
            nint placeholder = BuildRow(proto, (nint)rect, "Scanning the LAN for co-op servers...");
            if (placeholder != 0)
            {
                rows[built++] = placeholder;
            }
        }

        byte[] caption = new byte[96];
        for (int i = 0; i < count; i++)
        {
            nint row = BuildRow(proto, (nint)rect, FormatEntry(caption, i));
            if (row == 0)
            {
                break;
            }

            rows[built++] = row;
        }

        // The caller-stack CExoArrayList triple {data, count, capacity}; AddControls copies
        // the pointers out, so stack storage is fine for the duration of the call. Flag 1 =
        // the listbox owns (and on the next clear, frees) the rows.
        nint* triple = stackalloc nint[3];
        triple[0] = (nint)rows;
        triple[1] = built;
        triple[2] = built;
        AddControls(panel + PanelListBox, (nint)triple, 1, 0, 0);

        // Keep the previous highlight across a refresh; otherwise start on the first row, so
        // Enter connects to the top server without a click (the row-select event this fires
        // is unbound on our rows — verified safe).
        int select = restoreSelection >= 0 ? restoreSelection : 0;
        if (built > 0 && select < built)
        {
            SetSelectedControl(panel + PanelListBox, select, 1);
        }

        ShowDetails(panel, built > 0 ? select : -1);
    }

    /// <summary>
    /// Fills the details pane for the highlighted row: the world the host is standing in, and
    /// the party's faces.
    ///
    /// The stock population left the last real save's details there, so this used to blank
    /// the pane and stop. It is the natural home for everything the row cannot hold — the row
    /// is a narrow button whose caption wraps, and crowding it is what pushed the host's name
    /// off screen when the module was folded into it.
    ///
    /// <paramref name="index"/> below zero blanks everything, which is what an empty list
    /// wants.
    /// </summary>
    private void ShowDetails(nint panel, int index)
    {
        string world = string.Empty;
        string loadScreen = string.Empty;
        bool locked = false;
        string[] faces = new string[SharedBlock.BrowserDetailPortraitCount];

        if (index >= 0 && index < MaxRows)
        {
            locked = _block.BrowserEntry(index)[SharedBlock.BrowserEntryLocked] != 0;
            byte* detail = _block.BrowserDetail(index);
            world = ReadAscii(detail + SharedBlock.BrowserDetailModule, SharedBlock.BrowserDetailModuleLength);
            loadScreen = ReadAscii(
                detail + SharedBlock.BrowserDetailLoadScreen, SharedBlock.BrowserDetailLoadScreenLength);

            for (int i = 0; i < faces.Length; i++)
            {
                faces[i] = ReadAscii(
                    detail + SharedBlock.BrowserDetailPortraits + (i * SharedBlock.BrowserDetailPortraitLength),
                    SharedBlock.BrowserDetailPortraitLength);
            }
        }

        // ⚠ SPLIT ON " - ", because these are the save panel's PLANET and AREA labels and the
        // game's own area names are written in exactly that shape: "Endar Spire - Command
        // Module", "Taris - Lower City". Putting the whole string in one label showed
        // "Module" — the labels are narrow, and this renderer bottom-aligns text it has to
        // wrap, so an overflowing caption displays its TAIL. Two short halves fit; one long
        // one silently shows the wrong end of itself.
        string planet = world;
        string area = string.Empty;
        int split = world.IndexOf(" - ", StringComparison.Ordinal);
        if (split > 0)
        {
            planet = world[..split];
            area = world[(split + 3)..];
        }

        SetCaption(panel + PanelPlanetParams, planet);
        SetCaption(panel + PanelAreaParams, area);

        // The lock lives here rather than as a third row line: the row box holds two, and a
        // third would push the host's name off the top — the very bug this pass exists to fix.
        SetCaption(panel + PanelShotCaptionParams, locked ? "Password required" : "");

        // Image 0 is the save screenshot's slot; 1..3 are the party portraits, at 0x5E4 with
        // stride 0x140 — the exact walk ShowGame makes at 0x6C8B31..0x6C8B52.
        WithResRef(loadScreen, r => SetFillImage(panel + PanelImageParams[0], r, 0));

        for (int i = 0; i < faces.Length && i + 1 < PanelImageParams.Length; i++)
        {
            string face = faces[i];
            WithResRef(face, r => SetFillImage(panel + PanelImageParams[i + 1], r, 0));
        }
    }

    /// <summary>Reads a NUL-padded ASCII cell out of the block.</summary>
    private static string ReadAscii(byte* start, int maxLength)
    {
        int length = 0;
        while (length < maxLength && start[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(start, length);
    }

    /// <summary>Engine-allocates a row entry, clones layout/font from the proto, sets the
    /// caption. Returns 0 (and latches) only if the allocator fails.</summary>
    private nint BuildRow(nint proto, nint rect, string caption)
    {
        nint entry = OperatorNew(EntrySize);
        if (entry == 0)
        {
            return 0;
        }

        EntryCtor(entry);
        CloneFromProto(entry, rect, proto);

        // Empty metadata: a stray ShowGame takes the verified clearing branch and blanks the
        // pane; nothing else reads these fields.
        *(uint*)(entry + EntryFlags) = 0;

        SetCaption(entry + EntryCaptionParams, caption);

        // Enter/A on the highlighted row connects (the listbox forwards the accept event to
        // the selected row). Click stays selection-only, like the rest of the game's lists.
        Odyssey.BindControlAccept(
            entry,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnConnectClicked);

        return entry;
    }

    /// <summary>
    /// The row's caption. The renderer word-wraps it into the row box, which holds two lines:
    ///
    ///     Chris's Co-op Game
    ///     10.5.0.2:27140 (1/8)
    ///
    /// ⚠ TWO RULES, BOTH LEARNED THE EXPENSIVE WAY.
    ///
    /// 1. NO NEWLINES. There is no glyph for 0x0A here — an embedded '\n' draws as '?', which
    ///    is what "Game?10.5.0.2:27140" was. Layout comes from the wrap and from keeping the
    ///    text short, never from control characters.
    /// 2. NOTHING MAY OVERFLOW. When the caption needs more lines than the box has, the
    ///    renderer shows the BOTTOM of it. That is the whole history of this row: the original
    ///    "1. Name [module]  address  (players/max)" padded the name to 18 and the address to
    ///    23, a ~50-character string in a box about twenty wide, so what a player saw was the
    ///    tail — an IP and a count — with the host's name pushed off the top.
    ///
    /// So the row carries the two things a player picks by, sized to fit: who is hosting, and
    /// the address with the count beside it. Everything else — the world, the party, the
    /// password — is in the details pane, which has room and is one line of code away rather
    /// than a third line that would push the name off again.
    /// </summary>
    private string FormatEntry(byte[] scratch, int index)
    {
        byte* entry = _block.BrowserEntry(index);
        int n = 0;

        n = AppendField(scratch, n, entry + SharedBlock.BrowserEntryName, SharedBlock.BrowserEntryNameLength, 0);

        // A plain space: the wrap breaks here when the name fills the first line, which is
        // exactly the two-line layout wanted, and reads as one line when it does not.
        scratch[n++] = (byte)' ';
        n = AppendField(scratch, n, entry + SharedBlock.BrowserEntryAddress, SharedBlock.BrowserEntryAddressLength, 0);
        scratch[n++] = (byte)' ';
        scratch[n++] = (byte)'(';
        n = AppendCount(scratch, n, entry[SharedBlock.BrowserEntryPlayers]);
        scratch[n++] = (byte)'/';
        n = AppendCount(scratch, n, entry[SharedBlock.BrowserEntryMaxPlayers]);
        scratch[n++] = (byte)')';

        return System.Text.Encoding.ASCII.GetString(scratch, 0, n);
    }

    private static int AppendField(byte[] scratch, int at, byte* source, int capacity, int pad)
    {
        int written = 0;
        for (int i = 0; i < capacity && source[i] != 0 && at < scratch.Length - 8; i++)
        {
            scratch[at++] = source[i];
            written++;
        }

        for (; written < pad && at < scratch.Length - 8; written++)
        {
            scratch[at++] = (byte)' ';
        }

        return at;
    }

    private static int AppendCount(byte[] scratch, int at, byte value)
    {
        if (value >= 10)
        {
            scratch[at++] = (byte)('0' + (value / 10));
        }

        scratch[at++] = (byte)('0' + (value % 10));
        return at;
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
            if (_instance is not { _panel: not 0, _closing: false } self)
            {
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
