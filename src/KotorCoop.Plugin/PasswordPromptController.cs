using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Asks the player for a locked server's password, in the game, at the moment it is needed —
/// with a REAL game panel, not the developer console.
///
/// The agent bumps <see cref="SharedBlock.OffsetBrowserPasswordSerial"/> when it knows a
/// password is wanted — either because the row the player picked in the browser is locked and
/// nothing in coop.ini could satisfy it, or because the host answered the join with
/// <c>E_PASSWORD</c>. This controller turns that into the engine's own text-entry modal:
/// <c>CSWGuiSaveNamePanel</c> (asset <c>savename.gui</c>), the popup the save screen uses to
/// name a save — one of only two working editboxes in the whole game. It is constructed
/// fresh, its OK/Cancel are rebound to plugin stubs, and it is pushed as a modal over the
/// main menu. The player types, presses OK, and the typed text is submitted through the
/// EXISTING console-command channel as <c>password &lt;text&gt;</c> — exactly the line the
/// player would have typed after <c>~</c> — so the agent's retry path is byte-for-byte the
/// one already verified live. Zero new block fields.
///
/// Every engine address here is from docs/research/connect-flow.md ("Reusable modal:
/// CSWGuiSaveNamePanel"), with the panel lifetime rules from
/// docs/research/native-browser-panel.md (the +0x44 state machine and the manager sweep).
///
/// ⚠ THE ONE TRAP THAT CRASHES: the STOCK BTN_OK handler (0x006C9D50) reads the typed string
/// and then calls CSWGuiSaveLoad::WriteGame on the owner sink at panel+0x64 — it tries to
/// SAVE THE GAME, and faults when +0x64 is not a real CSWGuiSaveLoad. BTN_OK and BTN_CANCEL
/// are therefore rebound (AddEvent same-id replaces, verified at the machine level) BEFORE
/// the panel is shown, so the stock handler can never fire from a click or an accept.
///
/// MENU LIFETIME — why this does NOT mark the menu 0x400 the way the browser does: marking
/// the menu for the sweep is an explicit act of the save screen's own show handler
/// (OnLoadSaveGame, 0x67B22C), not a side effect of AddPanel. A modal push (AddPanel flags2
/// bit0 → PushModalPanel) leaves the panel below alive — that is precisely how the stock
/// save flow floats this popup over the save screen, and how the "no saved games" message
/// box floats over the menu. So the menu stays registered and valid underneath; the other
/// menu controllers may keep running.
///
/// The developer console remains the guarded FALLBACK (same latch pattern as
/// <see cref="NativeBrowserController"/>): any structural guard refusal latches the native
/// path off for the session and the ask falls back to <c>Odyssey.OpenConsoleWith("password ")</c>,
/// which was live-verified end to end. In-world (no main menu exists) the console is used
/// directly — it is the surface that works there, the same reason chat uses it.
///
/// Runs on <c>MainLoop</c> (<see cref="PluginHost.MenuFrame"/>): the ask lands at the main
/// menu, where the SwapBuffers hook is dormant.
/// </summary>
internal sealed unsafe class PasswordPromptController
{
    // ---- engine bindings (docs/research/connect-flow.md) -------------------------

    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNew =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWGuiSaveNamePanel::Constructor(guiManager) — the argument follows the pattern every
    // panel ctor in this engine takes (CSWGuiSaveLoad, CSWGuiMainMenu: the gui manager); the
    // vtable is verified immediately after, and a mismatch latches the native path off.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> PanelCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x006CAE70;

    // CSWGuiEditText::SetText(CExoString*) — seeds the editbox content (the "prefill").
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> EditTextSetText =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00416490;

    // CSWGuiManager::AddPanel(panel, flags2, bPlayOpenSound). flags2 = 1 sets bit0, which is
    // what triggers PushModalPanel — the researched modal show for this panel.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, void> AddPanel =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, void>)0x0040BC70;

    // CSWGuiManager::PanelExists(panel) — pointer compare over the registered array; the safe
    // way to learn our panel was swept without ever dereferencing it.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> PanelExists =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> ExoStringCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ExoStringDtor =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E5C20;

    // ---- object layout -----------------------------------------------------------

    private const int PanelSize = 0x690;
    private const nint PanelVtable = 0x007576D0;
    private const int PanelStateBits = 0x44;
    private const int PanelBtnOk = 0x68;       // embedded CSWGuiButton, not a pointer slot
    private const int PanelBtnCancel = 0x22C;  // embedded CSWGuiButton
    private const int PanelEditText = 0x4D0;   // EDITBOX(+0x3F0) + edit_text(+0xE0)
    private const int PanelTypedString = 0x548; // edit_text + 0x78: CExoString {char*, len}

    // The main menu, resolved the way NativeBrowserController.TryOpen does.
    private const int MenuGuiManager = 0x18;
    private const int MenuStateBits = 0x44;
    private const int MenuAlive = 0x140C;

    // ---- state ---------------------------------------------------------------------

    private readonly SharedBlockView _block;

    /// <summary>The one live controller, reachable from the engine's thiscall callbacks.</summary>
    private static PasswordPromptController? _instance;

    /// <summary>Latched when a guard refuses: the console prompt takes over permanently.</summary>
    private static bool _broken;

    /// <summary>The last request we have shown a prompt for; a higher serial is a new ask.</summary>
    private uint _servedSerial;

    /// <summary>Set when a request arrived at a moment we could not prompt; retried each frame.</summary>
    private bool _pending;

    private nint _panel;
    private nint _guiManager;
    private bool _closing;
    private bool _okPending;
    private int _logged;

    /// <summary>The composed <c>password &lt;text&gt;</c> line, captured in the OK handler
    /// while the panel is still alive, published only after the sweep has freed it. Sized to
    /// the console channel so the composition can never exceed what the block can carry.</summary>
    private readonly byte[] _answer = new byte[SharedBlock.ConsoleCommandLength];
    private int _answerLength;

    public PasswordPromptController(SharedBlockView block)
    {
        _block = block;

        // Adopt whatever the block already holds. A restarted plugin (or a second agent run)
        // must not open a prompt the player never asked for, on the strength of a serial that
        // was answered in a previous life.
        _servedSerial = _block.BrowserPasswordSerial;
    }

    public void OnFrame()
    {
        _instance = this;

        if (Odyssey.HasServer)
        {
            // In a world there is no main menu; anything we still hold is long freed. Drop
            // bookkeeping only — no engine calls (the NativeBrowserController rule). A pending
            // ask is served by the console below, which is the surface that works in-world.
            _panel = 0;
            _closing = false;
            _okPending = false;
        }

        uint serial = _block.BrowserPasswordSerial;
        if (serial != _servedSerial)
        {
            _servedSerial = serial;
            _pending = true;
        }

        if (_panel != 0)
        {
            // A re-bump while our panel is already on screen changes nothing: the panel IS
            // the prompt. Consume the pending flag rather than queueing a second open.
            _pending = false;
            OnFrameWhileOpen();
            return;
        }

        if (!_pending)
        {
            return;
        }

        // Not while the browser panel owns the screen: its listbox has the keyboard, and the
        // agent only asks after the panel has closed anyway. Hold the request rather than drop
        // it — the close is a frame or two away.
        if (NativeBrowserController.IsOpen)
        {
            return;
        }

        if (!_broken && !Odyssey.HasServer)
        {
            if (TryOpenPanel())
            {
                _pending = false;
                return;
            }

            if (!_broken)
            {
                return; // transient (logo movies, menu mid-rebuild) — try again next frame
            }

            // A guard just latched the native path off: fall THROUGH to the console this
            // same frame, so the ask is not lost between the refusal and the fallback.
        }

        // The fallback (and the in-world path): the developer console, force-opened with
        // "password " pre-typed — live-verified end to end. Returns false when a console the
        // player opened themselves is in the way; try again next frame rather than stamping
        // over a half-typed line.
        if (Odyssey.OpenConsoleWith("password "))
        {
            _pending = false;
        }
    }

    // ---- the open panel ------------------------------------------------------------

    private void OnFrameWhileOpen()
    {
        // The sweep may have freed the panel — our own OK/Cancel mark, or an engine-side
        // close we did not initiate (e.g. Escape, if the panel's own input handler takes it).
        // Detect by registration, never by dereference.
        if (_guiManager == 0 || PanelExists(_guiManager, _panel) == 0)
        {
            _panel = 0;
            _closing = false;

            if (_okPending)
            {
                _okPending = false;

                // Publish only now, with the panel freed: the agent's retry (and the world
                // load Connected eventually stages) can never race the GUI teardown — the
                // same ordering discipline the browser's connect uses.
                fixed (byte* answer = _answer)
                {
                    _block.PublishConsoleCommand(answer, _answerLength);
                }

                // The secret has left this buffer for the block; do not keep a copy around.
                Array.Clear(_answer);
                _answerLength = 0;
                Log("password prompt: answered; handed to the agent");
            }
            else if (_broken)
            {
                // The latch tripped while THIS panel was up (the only way a panel and the
                // latch can coexist: TryOpenPanel never runs once latched). The player's OK
                // went nowhere, so the ask is still owed — re-arm it, and the next frame's
                // fallback opens the console prompt instead.
                _pending = true;
                Log("password prompt: native path latched off mid-ask; re-asking via the console");
            }
            else
            {
                // Cancelled: the player stays at the menu (MenuAutoLoad holds the world load
                // until Connected, so nothing loads behind them). A wrong answer makes the
                // agent bump the serial again, which reopens a fresh panel.
                Log("password prompt: dismissed");
            }
        }

        // Marked 0x400 or still up — either way, touch nothing: the engine feeds keystrokes
        // into the editbox by itself, and the next state change is the sweep's.
    }

    // ---- open ----------------------------------------------------------------------

    /// <summary>
    /// Builds, repurposes and shows the panel. False without latching for transient
    /// conditions (no menu yet); a structural refusal latches the console fallback on.
    /// </summary>
    private bool TryOpenPanel()
    {
        nint menu = Odyssey.GetMainMenu();
        if (!LooksLikeHeapPointer(menu))
        {
            return false; // logo movies, or mid-rebuild — harmless, try again next frame
        }

        // The retail show path's own guards: menu not mid-teardown, fully alive. Transient —
        // a rebuild is frames away.
        if ((*(uint*)(menu + MenuStateBits) & 0x600) == 0x400 || *(int*)(menu + MenuAlive) == 0)
        {
            return false;
        }

        nint guiManager = *(nint*)(menu + MenuGuiManager);
        if (!LooksLikeHeapPointer(guiManager))
        {
            Break("gui manager pointer");
            return false;
        }

        nint panel = OperatorNew(PanelSize);
        if (panel == 0)
        {
            return false; // allocator pressure; retry
        }

        PanelCtor(panel, guiManager);

        if (*(nint*)panel != PanelVtable)
        {
            Break("panel vtable after construction");
            return false; // unregistered; abandoning leaks one panel, once, on a latched path
        }

        // Born already marked for the sweep would mean a ctor path we do not understand: it
        // was never registered, so the sweep will not free it — use its own deleting dtor,
        // and latch (an unexpected state is exactly what "be conservative" is for).
        if ((*(uint*)(panel + PanelStateBits) & 0x600) == 0x400)
        {
            var destroy = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)panel));
            destroy(panel, 1);
            Break("panel born closed");
            return false;
        }

        // The embedded OK/Cancel controls' first dword must be a plausible vtable pointer
        // before AddEvent is called through them — a layout shift here must latch, not call.
        if (!LooksLikeHeapPointer(*(nint*)(panel + PanelBtnOk))
            || !LooksLikeHeapPointer(*(nint*)(panel + PanelBtnCancel)))
        {
            var destroy = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)panel));
            destroy(panel, 1);
            Break("OK/Cancel control vtable");
            return false;
        }

        // ⚠ BEFORE showing, displace the stock handlers (AddEvent same-id replaces, both the
        // click and the accept event): the stock BTN_OK would call WriteGame on panel+0x64
        // and crash. After these two binds the stock handler is unreachable from either
        // button; the owner sink at +0x64 is left as the ctor made it, because nothing we
        // leave reachable reads it.
        Odyssey.BindControlClick(
            panel + PanelBtnOk,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnOkClicked);
        Odyssey.BindControlClick(
            panel + PanelBtnCancel,
            0x1,
            (nint)(delegate* unmanaged[Thiscall]<nint, nint, void>)&OnCancelClicked);

        // Empty the editbox. panel+0x4D0 is the EDIT CONTENT (EDITBOX +0x3F0, its edit_text
        // at +0xE0 — the typed CExoString at +0x548 is this object's +0x78, the same
        // arithmetic connect-flow.md spells out), NOT a caption label: stamping prompt text
        // here would hand it straight back as the "password" on a bare OK. So the box is
        // cleared of whatever the ctor seeded, guaranteeing +0x548 holds only the player's
        // keystrokes. The panel's title stays savename.gui's stock LBL_TITLE text — its
        // params offset is not researched, and inventing one is how engine writes go wrong.
        // The player context is already set: they just picked a row whose details pane says
        // "Password required".
        WithExoString(string.Empty, s => EditTextSetText(panel + PanelEditText, s));

        // Show as a MODAL over the live menu (flags2 bit0 → PushModalPanel). The menu is NOT
        // marked for the sweep — see the class comment. Focus lands on the editbox via the
        // panel's own OnPanelAdded override (0x006C8410).
        AddPanel(guiManager, panel, 1, 1);

        _panel = panel;
        _guiManager = guiManager;
        _closing = false;
        _okPending = false;
        Log("password prompt: native panel open");
        return true;
    }

    // ---- click handlers --------------------------------------------------------------

    /// <summary>
    /// The engine invokes this for OK (click or accept): <c>fn(owner /*ecx*/, firingControl
    /// /*stack*/)</c>. Reads the typed CExoString while the panel is still alive, composes
    /// the console line, and marks the panel for the sweep; the publish happens after the
    /// sweep confirms the free (see <see cref="OnFrameWhileOpen"/>).
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnOkClicked(nint owner, nint firingControl)
    {
        try
        {
            if (_instance is not { _panel: not 0, _closing: false } self)
            {
                return;
            }

            // CExoString {char* data @0, int length @4}. ⚠ length INCLUDES the null
            // terminator (the repo's thrice-bitten trap); PasswordAnswer.Compose cuts at the
            // first null regardless of what the length field claims.
            byte* data = *(byte**)(self._panel + PanelTypedString);
            int length = *(int*)(self._panel + PanelTypedString + 4);

            int composed = 0;
            if (data != null && length > 0)
            {
                if (!LooksLikeReadablePointer((nint)data))
                {
                    // A non-null garbage pointer where the typed string should be means the
                    // layout is not what the research says: latch, dismiss, fall back.
                    self.BreakInstance("typed string pointer");
                    self.CloseVisual();
                    return;
                }

                composed = PasswordAnswer.Compose(
                    new ReadOnlySpan<byte>(data, Math.Min(length, self._answer.Length)),
                    self._answer);
            }

            if (composed == 0)
            {
                // Nothing typed: an empty OK is a cancel, not a join attempt with a
                // zero-length password the host can only refuse.
                self.CloseVisual();
                return;
            }

            self._answerLength = composed;
            self._okPending = true;
            self.CloseVisual();
        }
        catch
        {
            // A managed exception must never unwind into the engine's event dispatch.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnCancelClicked(nint owner, nint firingControl)
    {
        try
        {
            if (_instance is { _panel: not 0, _closing: false } self)
            {
                self.CloseVisual();
            }
        }
        catch
        {
            // A managed exception must never unwind into the engine's event dispatch.
        }
    }

    /// <summary>
    /// Marks the panel for the manager sweep — state <c>(f &amp; ~0x300) | 0x400</c> — which
    /// on the next Update pops it off the modal stack (RemovePanel does that when the panel
    /// is on top), unregisters it and calls its deleting destructor. Freeing is deliberately
    /// NOT done here: this runs inside the engine's own event dispatch ON this panel's
    /// control, and destroying the panel under the dispatcher's feet is a use-after-free.
    /// The menu underneath was never marked, so no DisplayMainMenu is needed.
    /// </summary>
    private void CloseVisual()
    {
        uint state = *(uint*)(_panel + PanelStateBits);
        *(uint*)(_panel + PanelStateBits) = (state & ~0x300u) | 0x400u;
        _closing = true;
    }

    // ---- plumbing --------------------------------------------------------------------

    /// <summary>Builds an engine CExoString on the stack, runs <paramref name="use"/>, frees
    /// it — SetText copies, so the temporary may die right after (the retail order).</summary>
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

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    /// <summary>A CExoString's data pointer is a raw allocation with no alignment promise the
    /// engine keeps, so only the address-range half of the heap guard applies.</summary>
    private static bool LooksLikeReadablePointer(nint p) => p is > 0x10000 and < 0x7FFF0000;

    /// <summary>A guard refused: latch the native path off so every ask — this one and all
    /// later ones — uses the console prompt instead of retrying a wrong assumption.</summary>
    private void Break(string what)
    {
        _broken = true;
        Log($"password prompt: guard refused ({what}); falling back to the console prompt");
    }

    /// <summary>The latch, callable from the static handlers via the instance they hold.</summary>
    private void BreakInstance(string what) => Break(what);

    private void Log(string message)
    {
        if (_logged < 32)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
