using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The engine's own text-entry modal — <c>CSWGuiSaveNamePanel</c> (asset <c>savename.gui</c>),
/// the popup the save screen uses to name a save — built, repurposed and floated as a password
/// prompt. This class owns the whole panel lifecycle (build, rebind, show, read, dismiss); it
/// deliberately does NOT decide when to ask or what to do with the answer, because two flows
/// share it with different surroundings:
///
///   1. <see cref="NativeBrowserController"/> floats it OVER THE BROWSER PANEL the moment the
///      player presses Connect on a locked row — the retail pattern exactly: the stock save
///      screen pushes this very popup over itself (show sequence verified from 0x006CB8BC,
///      docs/research/connect-flow.md "Reusable modal: CSWGuiSaveNamePanel"). The browser
///      stays on screen beneath it; no round trip through the main menu.
///   2. <see cref="PasswordPromptController"/> floats it over the MAIN MENU when the agent
///      bumps the password serial — the wrong-password retry, which fires after the browser
///      is long closed.
///
/// Every engine address here is from docs/research/connect-flow.md, with the panel lifetime
/// rules from docs/research/native-browser-panel.md (the +0x44 state machine and the manager
/// sweep).
///
/// ⚠ THE ONE TRAP THAT CRASHES: the STOCK BTN_OK handler (0x006C9D50) reads the typed string
/// and then calls CSWGuiSaveLoad::WriteGame on the owner sink at panel+0x64 — it tries to
/// SAVE THE GAME, and faults when +0x64 is not a real CSWGuiSaveLoad. BTN_OK and BTN_CANCEL
/// are therefore rebound (AddEvent same-id replaces, verified at the machine level) BEFORE
/// the panel is shown, so the stock handler can never fire from a click or an accept.
///
/// PANEL-BELOW LIFETIME — why showing this does NOT mark anything 0x400: a modal push
/// (AddPanel flags2 bit0 → PushModalPanel) leaves the panel below alive. That is precisely
/// how the stock save flow floats this popup over the save screen, and how the "no saved
/// games" message box floats over the menu. So whatever the modal is floated over — menu or
/// browser — stays registered and valid underneath.
///
/// One structural guard refusal latches the native modal off for the whole session
/// (<see cref="Available"/>), shared across both flows: a wrong assumption about the panel
/// layout is wrong in both places, and each caller has its own fallback (the console prompt,
/// or the old close-then-prompt flow) that was live-verified end to end.
///
/// SINCE 2026-09-18 the same panel also serves as the browser's ADDRESS BOX
/// (<see cref="Ask.Address"/>): same build, same rebinds, same sweep; only the title, the
/// prefill and the composer differ (<see cref="ConnectAddressRule"/> writes
/// <c>connect &lt;host:port&gt;</c> instead of <c>password &lt;text&gt;</c>). Text the rule
/// refuses does NOT close the panel — the title is re-captioned with the reason and the
/// player edits what they typed. The name of the class is historical.
///
/// SINCE 2026-09-24 the panel is also reachable from the gamepad on-screen keyboard
/// (docs/research/gamepad.md §7): <see cref="IsAnyOpen"/>/<see cref="ActiveAsk"/> let
/// <c>GamepadController</c> know a co-op text box has focus without holding its own reference
/// to whichever instance is open, and <see cref="ConfirmActive"/>/<see cref="CancelActive"/>
/// run the exact OK/Cancel code a mouse click would — the typing itself (per character) is a
/// posted WM_CHAR, same as a real keystroke, so this class needed no new engine surface at all.
/// </summary>
internal sealed unsafe class PasswordEntryModal
{
    /// <summary>Which question the panel is asking — decides the title, the prefill's
    /// meaning and which composer turns the typed bytes into a console line.</summary>
    public enum Ask
    {
        /// <summary>A server password: composes <c>password &lt;text&gt;</c>.</summary>
        Password,

        /// <summary>A host address: composes <c>connect &lt;host:port&gt;</c>, refusing (and
        /// staying open) for text that is not an address.</summary>
        Address,
    }

    /// <summary>What <see cref="Poll"/> observed this frame.</summary>
    public enum Outcome
    {
        /// <summary>No panel is (or was) up; nothing to do.</summary>
        NotOpen,

        /// <summary>The panel is on screen (possibly marked for the sweep) — touch nothing:
        /// the engine feeds keystrokes into the editbox by itself.</summary>
        StillOpen,

        /// <summary>The sweep freed the panel after an OK with text: the composed
        /// <c>password &lt;typed&gt;</c> line is ready — publish it via
        /// <see cref="PublishAnswer"/> NOW, before acting on it.</summary>
        Answered,

        /// <summary>The sweep freed the panel with no answer: Cancel, an empty OK, or an
        /// engine-side close (alt-tab focus loss, verified live). When
        /// <see cref="Available"/> is also false, the latch tripped mid-ask and the ask is
        /// still owed — the caller decides which fallback answers it.</summary>
        Dismissed,
    }

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

    // CSWGuiTextParams::SetText(CExoString*) — the LABEL caption setter (copies the string,
    // sets str_ref = -1 so the literal wins, re-wraps immediately). The browser panel's
    // SetCaption uses the same call. ⚠ NOT interchangeable with 0x00416490 above: that one is
    // the EDITBOX text setter — using it on the label region is the wrong class at the wrong
    // offset, and using THIS one on +0x4D0 would be equally wrong the other way round.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> TitleSetText =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00415E00;

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

    // LBL_TITLE — the fourth embedded control, researched from the ctor's own disassembly
    // (0x006CAEDE: `lea ecx, [esi+0x550]` → CSWGuiLabel::Constructor 0x0041ACD0; registered
    // against the literal "LBL_TITLE" at 0x006CAFA4..0x006CAFC7). Its CSWGuiText lives at
    // label+0xD0 and the text params at text+0x18 → panel+0x638, the same label+0xE8 shape
    // the save/load panel's title uses (native-browser-panel.md, live-proven at +0x15C
    // there). The stock "Enter name for saved game:" is a strref baked into savename.gui,
    // resolved ONCE in the ctor's Load pass; an exhaustive xref scan found nothing that
    // re-stamps it, so one SetText after construction sticks.
    private const int PanelTitleLabel = 0x550;
    private const nint LabelVtable = 0x0073E5B8;
    private const int PanelTitleText = 0x620;    // label + 0xD0: the CSWGuiText
    private const int PanelTitleParams = 0x638;  // text + 0x18: the CSWGuiTextParams
    private const int TitleParamsTextObject = 0x688; // params + 0x50: back-pointer to the text

    // ---- state ---------------------------------------------------------------------

    /// <summary>The one OPEN modal, reachable from the engine's thiscall callbacks. Two
    /// instances exist (one per flow) but at most one panel is ever up: the browser flow only
    /// asks while its panel owns the screen, and the serial flow holds its ask while
    /// <see cref="NativeBrowserController.IsOpen"/>.</summary>
    private static PasswordEntryModal? _active;

    /// <summary>Latched when a guard refuses: the callers' fallbacks take over permanently.</summary>
    private static bool _broken;

    private static int _logged;

    private nint _panel;
    private nint _guiManager;
    private bool _closing;
    private bool _okPending;
    private Ask _ask;

    /// <summary>The composed console line (<c>password &lt;text&gt;</c> or
    /// <c>connect &lt;address&gt;</c>), captured in the OK handler while the panel is still
    /// alive, published only after the sweep has freed it. Sized to the console channel so
    /// the composition can never exceed what the block can carry.</summary>
    private readonly byte[] _answer = new byte[SharedBlock.ConsoleCommandLength];
    private int _answerLength;

    /// <summary>False once a guard latched the native modal off; callers use their fallback.</summary>
    public static bool Available => !_broken;

    public bool IsOpen => _panel != 0;

    /// <summary>
    /// True while SOME instance's panel is up and not already on its way out. Static, because
    /// two instances exist (one per flow, <see cref="_active"/>) and a caller that just wants
    /// to know "does a co-op text box have focus right now" — the gamepad on-screen keyboard's
    /// gate — should not have to know which flow opened it. Excludes <c>_closing</c>
    /// deliberately: once <see cref="CloseVisual"/> has run, the panel is on the sweep's way
    /// out (still registered for a frame or two) and must stop looking like a live target, or
    /// the gamepad controller would re-open its grid against a panel that is already closing.
    /// </summary>
    public static bool IsAnyOpen => _active is { _panel: not 0, _closing: false };

    /// <summary>Which question the currently-open instance is asking, or null if none is open —
    /// lets a caller (the gamepad grid's title) label the box without holding its own reference.</summary>
    public static Ask? ActiveAsk => _active is { _panel: not 0, _closing: false } self ? self._ask : null;

    /// <summary>A caller's own structural guard refused (e.g. a garbage gui-manager pointer
    /// on the way to an open): latch the shared native path off, same as an internal guard.</summary>
    public static void Latch(string what) => Break(what);

    // ---- open ----------------------------------------------------------------------

    /// <summary>
    /// Builds, repurposes and shows the panel as a modal over whatever
    /// <paramref name="guiManager"/> currently has on screen. False without latching for
    /// transient conditions (allocator pressure, another modal already up); a structural
    /// refusal latches <see cref="Available"/> off. The caller supplies the gui manager
    /// because the two flows reach it differently: the serial flow resolves the main menu's,
    /// while the browser flow uses the one its own panel was registered with — the main menu
    /// object is FREED while the browser is up and must not be resolved.
    /// </summary>
    /// <param name="ask">Which question this panel asks (title, prefill, composer).</param>
    /// <param name="prefill">Text seeded into the editbox — the last address for
    /// <see cref="Ask.Address"/>; a password is never prefilled (it renders in cleartext).
    /// ⚠ NEEDS-LIVE: the seeding call is the one the empty-box clear already uses, but a
    /// non-empty seed has not yet been watched rendering with the caret after it.</param>
    public bool TryOpen(nint guiManager, Ask ask = Ask.Password, string prefill = "")
    {
        if (_broken || _panel != 0)
        {
            return false;
        }

        // Never two panels: the engine's modal stack is not somewhere to experiment.
        if (_active is { _panel: not 0 })
        {
            return false;
        }

        if (!LooksLikeHeapPointer(guiManager))
        {
            Break("gui manager pointer");
            return false;
        }

        nint panel = OperatorNew(PanelSize);
        if (panel == 0)
        {
            return false; // allocator pressure; the caller retries or falls back
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

        // Seed the editbox. panel+0x4D0 is the EDIT CONTENT (EDITBOX +0x3F0, its edit_text
        // at +0xE0 — the typed CExoString at +0x548 is this object's +0x78, the same
        // arithmetic connect-flow.md spells out), NOT a caption label: stamping prompt text
        // here would hand it straight back as the answer on a bare OK. So the box is set to
        // exactly what an OK may return unedited — nothing for a password, the last address
        // for the address box — guaranteeing +0x548 holds only that plus the player's
        // keystrokes. The title is captioned separately below, at the researched LBL_TITLE
        // params — never here.
        WithExoString(ask == Ask.Address ? prefill : string.Empty,
            s => EditTextSetText(panel + PanelEditText, s));

        // Caption the title so the prompt says what it is asking, instead of savename.gui's
        // stock "Enter name for saved game:". The address title says "keyboard" because a
        // gamepad cannot type into this panel (docs/limitations.md's controller row).
        TrySetTitle(panel, ask == Ask.Address ? "Host address (keyboard only)" : "Enter the server password");

        // Show as a MODAL over whatever is live beneath (flags2 bit0 → PushModalPanel).
        // Nothing below is marked for the sweep — see the class comment. Focus lands on the
        // editbox via the panel's own OnPanelAdded override (0x006C8410).
        AddPanel(guiManager, panel, 1, 1);

        _panel = panel;
        _guiManager = guiManager;
        _closing = false;
        _okPending = false;
        _ask = ask;
        _active = this;
        return true;
    }

    /// <summary>
    /// Captions LBL_TITLE. Guarded by the two invariants the research names — the label's
    /// vtable at +0x550 and the params' back-pointer to the label's own CSWGuiText — and on a
    /// mismatch the caption is simply SKIPPED, not latched: a shifted label layout (a GUI
    /// mod?) makes the title cosmetic-wrong, while the modal itself still works, and killing
    /// a working prompt over a caption would be the wrong trade. A write through a wrong
    /// params offset is what the guards exist to prevent. Re-run per caption, never cached:
    /// the panel address is checked against a live object each time.
    /// </summary>
    private static bool TrySetTitle(nint panel, string title)
    {
        if (*(nint*)(panel + PanelTitleLabel) == LabelVtable
            && *(nint*)(panel + TitleParamsTextObject) == panel + PanelTitleText)
        {
            WithExoString(title, s => TitleSetText(panel + PanelTitleParams, s));
            return true;
        }

        if (_logged < 32)
        {
            _logged++;
            PluginHost.Log("password modal: title label not where researched; keeping the stock caption");
        }

        return false;
    }

    // ---- the open panel ------------------------------------------------------------

    /// <summary>
    /// Once per frame while an ask is in flight. Detects the sweep's free by REGISTRATION
    /// (<c>PanelExists</c>), never by dereference, and reports what happened. On
    /// <see cref="Outcome.Answered"/> the composed line is waiting in this instance —
    /// publish it via <see cref="PublishAnswer"/> before doing anything that consumes it.
    /// </summary>
    public Outcome Poll()
    {
        if (_panel == 0)
        {
            return Outcome.NotOpen;
        }

        if (_guiManager != 0 && PanelExists(_guiManager, _panel) != 0)
        {
            // Marked 0x400 or still up — either way, touch nothing: the engine feeds
            // keystrokes into the editbox by itself, and the next state change is the sweep's.
            return Outcome.StillOpen;
        }

        _panel = 0;
        _closing = false;

        if (_okPending)
        {
            _okPending = false;
            return Outcome.Answered;
        }

        return Outcome.Dismissed;
    }

    /// <summary>
    /// Publishes the captured <c>password &lt;typed&gt;</c> line through the existing console
    /// channel — byte-for-byte the line the player would have typed after <c>~</c>, so the
    /// agent's path is the one already verified live. Only ever called after <see cref="Poll"/>
    /// said <see cref="Outcome.Answered"/>, i.e. with the panel freed: the agent's next act
    /// can never race the GUI teardown. The secret leaves this buffer for the block; no copy
    /// stays behind.
    /// </summary>
    public void PublishAnswer(SharedBlockView block)
    {
        fixed (byte* answer = _answer)
        {
            block.PublishConsoleCommand(answer, _answerLength);
        }

        Array.Clear(_answer);
        _answerLength = 0;
    }

    /// <summary>
    /// Hands the captured line to the caller instead of the block — for a caller that must
    /// close its own surface first and publish only once THAT sweep is confirmed (the
    /// browser's address connect, which keeps the row-connect ordering: no agent action can
    /// race the panel teardown). Returns the copied length; the buffer here is cleared.
    /// </summary>
    public int TakeAnswer(Span<byte> destination)
    {
        int count = Math.Min(_answerLength, destination.Length);
        _answer.AsSpan(0, count).CopyTo(destination);
        Array.Clear(_answer);
        _answerLength = 0;
        return count;
    }

    /// <summary>
    /// The surface beneath the modal is going away (the browser panel was swept under it):
    /// mark the modal for the same sweep — it is separately registered, so the manager frees
    /// it too — and drop the bookkeeping. Any half-given answer dies with it; the ask can be
    /// re-fired against whatever screen comes next.
    /// </summary>
    public void AbandonToSweep()
    {
        if (_panel != 0 && !_closing && _guiManager != 0 && PanelExists(_guiManager, _panel) != 0)
        {
            CloseVisual();
        }

        Forget();
    }

    /// <summary>In a world there is no menu and no panel; anything still held is long freed.
    /// Drop bookkeeping only — no engine calls (the NativeBrowserController rule).</summary>
    public void Forget()
    {
        _panel = 0;
        _closing = false;
        _okPending = false;
        _answerLength = 0;
        Array.Clear(_answer);
    }

    // ---- click handlers --------------------------------------------------------------

    /// <summary>
    /// The engine invokes this for OK (click or accept): <c>fn(owner /*ecx*/, firingControl
    /// /*stack*/)</c>. Delegates to <see cref="SubmitTyped"/> — see there.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnOkClicked(nint owner, nint firingControl)
    {
        try
        {
            if (_active is { _panel: not 0, _closing: false } self)
            {
                self.SubmitTyped();
            }
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
            if (_active is { _panel: not 0, _closing: false } self)
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
    /// Reads the typed CExoString while the panel is still alive, composes the console line,
    /// and marks the panel for the sweep; the publish happens after the sweep confirms the
    /// free (see <see cref="Poll"/>). The exact OK path — called from the engine's own click
    /// dispatch (<see cref="OnOkClicked"/>) AND from the gamepad on-screen keyboard's Confirm
    /// (<see cref="ConfirmActive"/>), so a pad's "send" does precisely what a mouse click on
    /// OK already does, with no second code path to keep in sync.
    /// </summary>
    private void SubmitTyped()
    {
        // CExoString {char* data @0, int length @4}. ⚠ length INCLUDES the null
        // terminator (the repo's thrice-bitten trap); PasswordAnswer.Compose cuts at the
        // first null regardless of what the length field claims.
        byte* data = *(byte**)(_panel + PanelTypedString);
        int length = *(int*)(_panel + PanelTypedString + 4);

        int composed = 0;
        string? rejection = null;
        if (data != null && length > 0)
        {
            if (!LooksLikeReadablePointer((nint)data))
            {
                // A non-null garbage pointer where the typed string should be means the
                // layout is not what the research says: latch, dismiss, fall back.
                Break("typed string pointer");
                CloseVisual();
                return;
            }

            ReadOnlySpan<byte> typed = new(data, Math.Min(length, _answer.Length));
            composed = _ask == Ask.Address
                ? ConnectAddressRule.Compose(typed, _answer, out rejection)
                : PasswordAnswer.Compose(typed, _answer);
        }

        if (composed == 0)
        {
            if (rejection is { Length: > 0 })
            {
                // Text that is not an address: say why in the title and STAY OPEN with
                // the player's text still in the box, rather than dialling garbage or
                // closing on them. The panel is alive (we are inside its own OK dispatch,
                // or the pad's equivalent), so the same guarded caption write as at open
                // applies.
                TrySetTitle(_panel, rejection);
                return;
            }

            // Nothing typed: an empty OK is a cancel, not a join attempt with a
            // zero-length password the host can only refuse.
            CloseVisual();
            return;
        }

        _answerLength = composed;
        _okPending = true;
        CloseVisual();
    }

    /// <summary>
    /// The gamepad on-screen keyboard's Confirm (Y/Start) on whichever instance is currently
    /// open (<see cref="_active"/>) — runs exactly <see cref="SubmitTyped"/>, the same path a
    /// mouse click on OK takes. No-op if nothing is open or the open panel is already closing.
    /// </summary>
    public static void ConfirmActive()
    {
        if (_active is { _panel: not 0, _closing: false } self)
        {
            self.SubmitTyped();
        }
    }

    /// <summary>
    /// The gamepad on-screen keyboard's Cancel (Back) on whichever instance is currently open —
    /// runs exactly what a mouse click on Cancel does. No-op if nothing is open or already
    /// closing.
    /// </summary>
    public static void CancelActive()
    {
        if (_active is { _panel: not 0, _closing: false } self)
        {
            self.CloseVisual();
        }
    }

    /// <summary>
    /// Marks the panel for the manager sweep — state <c>(f &amp; ~0x300) | 0x400</c> — which
    /// on the next Update pops it off the modal stack (RemovePanel does that when the panel
    /// is on top), unregisters it and calls its deleting destructor. Freeing is deliberately
    /// NOT done here: this runs inside the engine's own event dispatch ON this panel's
    /// control, and destroying the panel under the dispatcher's feet is a use-after-free.
    /// Whatever sits beneath was never marked, so it simply resurfaces.
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

    /// <summary>A guard refused: latch the native modal off so every ask — this one and all
    /// later ones, in both flows — uses its fallback instead of retrying a wrong assumption.</summary>
    private static void Break(string what)
    {
        _broken = true;
        if (_logged < 32)
        {
            _logged++;
            PluginHost.Log($"password modal: guard refused ({what}); native modal latched off");
        }
    }
}
