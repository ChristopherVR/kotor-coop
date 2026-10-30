namespace KotorCoop.Plugin;

// The ~375-line "open" region: TryOpen (constructs and shows the repurposed save/load
// panel) and the row-building/details helpers it drives (PopulateRows, ShowDetails,
// BuildRow, FormatEntry, ReadAscii, AppendField, AppendCount). See NativeBrowserController.cs
// for the engine bindings, object layout, state fields and the OnFrame dispatcher.
internal sealed unsafe partial class NativeBrowserController
{
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

        bool constructed = false;
        bool registered = false;
        try
        {
        PanelCtor(panel, guiManager, 0, 1);

        if (*(nint*)panel != PanelVtable)
        {
            Break("panel vtable after construction");
            return; // unregistered; abandoning leaks one panel, once, on a latched-off path
        }

        constructed = true;
        if ((*(uint*)(panel + PanelStateBits) & 0x600) == 0x400)
        {
            // Born dead (the zero-saves case slipped through). It was never registered, so
            // the sweep will not free it — call its own deleting destructor.
            var destroy = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)panel));
            constructed = false;
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
        SetEnabled(panel + PanelHostButton, 1);

        Odyssey.BindControlClick(
            panel + PanelConnectButton,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnConnectClicked);

        _shownListSerial = _block.BrowserListSerial;
        _selectedAddress = null;
        PopulateRows(panel, restoreAddress: null);
        if (_broken) return;

        // Show, then mark the menu for the sweep — retail order. From here until the close
        // path rebuilds it, the main menu pointer is poison. Re-read the state bits: the
        // panel constructor ran since the guard captured them.
        AddPanel(guiManager, panel, 2, 1);
        registered = true;
        _panel = panel;
        _guiManager = guiManager;
        *(uint*)(menu + MenuStateBits) = (*(uint*)(menu + MenuStateBits) & ~0x300u) | 0x400u;

        _panel = panel;
        _guiManager = guiManager;
        _closing = false;
        _connectPending = false;
        _block.BrowserOpen = 1;
        Log("native browser: open");
        }
        catch (Exception ex)
        {
            // An unexpected managed failure must not retry the allocation each frame.
            // Once registered, the engine owns teardown; an unregistered, fully
            // constructed panel can safely use its own deleting destructor.
            Break($"open failed: {ex.GetType().Name}");
            _block.BrowserOpen = 0;
        }
        finally
        {
            if (constructed && !registered)
            {
                var destroy = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)panel));
                destroy(panel, 1);
            }
        }
    }

    /// <summary>
    /// Builds one engine-cloned row per published server and hands them to the listbox, which
    /// frees whatever rows it held (stock save rows on open, our own on refresh). With no
    /// servers yet, a single unconnectable placeholder row keeps the panel honest.
    ///
    /// <paramref name="restoreAddress"/> is the <see cref="SharedBlock.BrowserEntryAddress"/> of
    /// the server that was highlighted before this rebuild (or null on first open / nothing
    /// selected). Every publish reorders <c>_block</c>'s rows by last-seen time
    /// (<c>ServerList.Snapshot</c>), which happens on pure beacon-timing jitter with no player
    /// action at all — so re-applying the OLD numeric row index here silently re-highlights
    /// whatever server now happens to occupy that slot, which is the "clicking one session shows
    /// another one's details" bug. Resolving by address instead keeps the highlight pinned to
    /// the SAME server across a reorder.
    /// </summary>
    private void PopulateRows(nint panel, string? restoreAddress)
    {
        nint proto = *(nint*)(panel + PanelProtoRow);
        if (!LooksLikeHeapPointer(proto))
        {
            Break("listbox proto row");
            return;
        }

        int count = (int)Math.Min(_block.BrowserCount, (uint)MaxRows);
        _serverRows = count;
        _detailSelection = -2;

        int* rect = stackalloc int[4];
        rect[0] = 0;
        rect[1] = 0;
        rect[2] = 0;
        rect[3] = RowHeight;

        // Every server (or the one placeholder), plus the address row last.
        nint* rows = stackalloc nint[MaxRows + 2 + CoopMenuActions.Count];
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

        // The address box, always the last row: a host that is not on the LAN (an internet
        // game, a tunnel address). Confirming it opens the text panel — see
        // NativeBrowserController.Address.cs. An engine-cloned row like the others, because
        // the panel has no spare button and hand-built controls are the crash register's #1.
        _addressRow = -1;
        nint addressRow = BuildRow(proto, (nint)rect, AddressRowCaption);
        if (addressRow != 0)
        {
            _addressRow = built;
            rows[built++] = addressRow;
        }

        Array.Fill(_actionRows, -1);
        for (int action = 0; action < CoopMenuActions.Count; action++)
        {
            nint row = BuildRow(proto, (nint)rect, CoopMenuActions.Caption(action));
            if (row == 0) continue;
            _actionRows[action] = built;
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

        // Keep the previous highlight on the SAME SERVER across a refresh, by address, not by
        // slot; otherwise start on the first row, so Enter connects to the top server without a
        // click (the row-select event this fires is unbound on our rows — verified safe). A
        // server that dropped off the list (restoreAddress no longer present) falls back to the
        // first row exactly like "nothing was selected before."
        int select = 0;
        if (restoreAddress == AddressRowMarker && _addressRow >= 0)
        {
            select = _addressRow; // the player was on the address row: keep them there
        }
        else if (CoopMenuActions.FindMarker(restoreAddress) is int action && action >= 0 && _actionRows[action] >= 0)
        {
            select = _actionRows[action];
        }
        else if (restoreAddress is not null)
        {
            for (int i = 0; i < count; i++)
            {
                string address = ReadAscii(
                    _block.BrowserEntry(i) + SharedBlock.BrowserEntryAddress,
                    SharedBlock.BrowserEntryAddressLength);
                if (address == restoreAddress)
                {
                    select = i;
                    break;
                }
            }
        }

        if (built > 0 && select < built)
        {
            SetSelectedControl(panel + PanelListBox, select, 1);
        }

        // The details pane describes a SERVER; the placeholder and the address row have none
        // (blank it), and the address row gets a hint in the caption slot instead.
        ShowDetails(panel, select < count ? select : -1);
        if (select == _addressRow)
        {
            SetCaption(panel + PanelShotCaptionParams, AddressRowHint);
        }

        // Re-anchor to whatever is now actually highlighted, so the NEXT refresh compares
        // against the right identity even if this one changed or lost the selection.
        _selectedAddress = select < count
            ? ReadAscii(
                _block.BrowserEntry(select) + SharedBlock.BrowserEntryAddress,
                SharedBlock.BrowserEntryAddressLength)
            : select == _addressRow ? AddressRowMarker
            : Array.IndexOf(_actionRows, select) is int selectedAction && selectedAction >= 0
                ? CoopMenuActions.Marker(selectedAction) : null;
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
        Array.Fill(faces, string.Empty);

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

}
