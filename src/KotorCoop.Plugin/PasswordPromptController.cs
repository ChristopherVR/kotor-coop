namespace KotorCoop.Plugin;

/// <summary>
/// The AGENT-INITIATED password ask: the retry path. The agent bumps
/// <see cref="SharedBlock.OffsetBrowserPasswordSerial"/> when it knows a password is wanted —
/// the host answered the join with <c>E_PASSWORD</c> (the held password was wrong or
/// missing), or a locked join was started with nothing in hand. By the time this fires the
/// browser panel is long closed, so the prompt floats over the MAIN MENU — which is correct
/// here: the player is standing at the menu looking at a refused join.
///
/// The first ask for a locked row no longer comes through this serial at all:
/// <see cref="NativeBrowserController"/> floats the same modal over the browser BEFORE the
/// panel closes, so picking a locked server never round-trips through the menu. Both flows
/// share <see cref="PasswordEntryModal"/> — one class owning build/show/read/dismiss — and
/// both publish the answer as the console line <c>password &lt;text&gt;</c>, exactly the line
/// the player would have typed after <c>~</c>, so the agent's path is byte-for-byte the one
/// already verified live. Zero new block fields.
///
/// The developer console remains the guarded FALLBACK (same latch pattern as
/// <see cref="NativeBrowserController"/>): any structural guard refusal latches the native
/// modal off for the session and the ask falls back to <c>Odyssey.OpenConsoleWith("password ")</c>,
/// which was live-verified end to end. In-world (no main menu exists) the console is used
/// directly — it is the surface that works there, the same reason chat uses it.
///
/// Runs on <c>MainLoop</c> (<see cref="PluginHost.MenuFrame"/>): the ask lands at the main
/// menu, where the SwapBuffers hook is dormant.
/// </summary>
internal sealed unsafe class PasswordPromptController
{
    // The main menu, resolved the way NativeBrowserController.TryOpen does.
    private const int MenuGuiManager = 0x18;
    private const int MenuStateBits = 0x44;
    private const int MenuAlive = 0x140C;

    private readonly SharedBlockView _block;

    private readonly PasswordEntryModal _modal = new();

    /// <summary>The last request we have shown a prompt for; a higher serial is a new ask.</summary>
    private uint _servedSerial;

    /// <summary>Set when a request arrived at a moment we could not prompt; retried each frame.</summary>
    private bool _pending;

    private int _logged;

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
        if (Odyssey.HasServer)
        {
            // In a world there is no main menu; anything we still hold is long freed. Drop
            // bookkeeping only — no engine calls (the NativeBrowserController rule). A pending
            // ask is served by the console below, which is the surface that works in-world.
            _modal.Forget();
        }

        uint serial = _block.BrowserPasswordSerial;
        if (serial != _servedSerial)
        {
            _servedSerial = serial;
            _pending = true;
        }

        switch (_modal.Poll())
        {
            case PasswordEntryModal.Outcome.StillOpen:
                // A re-bump while our panel is already on screen changes nothing: the panel
                // IS the prompt. Consume the pending flag rather than queueing a second open.
                _pending = false;
                return;

            case PasswordEntryModal.Outcome.Answered:
                // Publish only now, with the panel freed: the agent's retry (and the world
                // load Connected eventually stages) can never race the GUI teardown — the
                // same ordering discipline the browser's connect uses.
                _modal.PublishAnswer(_block);
                Log("password prompt: answered; handed to the agent");
                break;

            case PasswordEntryModal.Outcome.Dismissed:
                if (!PasswordEntryModal.Available)
                {
                    // The latch tripped while THIS panel was up (a mid-ask guard refusal, e.g.
                    // a garbage typed-string pointer). The player's OK went nowhere, so the
                    // ask is still owed — re-arm it, and the next frame's fallback opens the
                    // console prompt instead.
                    _pending = true;
                    Log("password prompt: native path latched off mid-ask; re-asking via the console");
                }
                else
                {
                    // Cancelled: the player stays at the menu (MenuAutoLoad holds the world
                    // load until Connected, so nothing loads behind them). A wrong answer
                    // makes the agent bump the serial again, which reopens a fresh panel.
                    Log("password prompt: dismissed");
                }

                break;
        }

        if (!_pending)
        {
            return;
        }

        // Not while the browser panel owns the screen: its listbox has the keyboard, the
        // main menu object is FREED beneath it (resolving it here would dereference a
        // dangling pointer), and the browser flow runs its own prompt over its own panel.
        // Hold the request rather than drop it — this serial's ask (the retry) only fires
        // after the browser has closed anyway.
        if (NativeBrowserController.IsOpen)
        {
            return;
        }

        if (PasswordEntryModal.Available && !Odyssey.HasServer)
        {
            nint guiManager = ResolveMenuGuiManager();
            if (guiManager != 0 && _modal.TryOpen(guiManager))
            {
                _pending = false;
                Log("password prompt: native panel open");
                return;
            }

            if (PasswordEntryModal.Available)
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

    /// <summary>
    /// The main menu's gui manager, behind the retail show path's own guards. Zero for
    /// transient states (no menu yet, menu mid-teardown — a rebuild is frames away); a
    /// structurally garbage pointer latches the native modal off before returning zero.
    /// </summary>
    private static nint ResolveMenuGuiManager()
    {
        nint menu = Odyssey.GetMainMenu();
        if (!LooksLikeHeapPointer(menu))
        {
            return 0; // logo movies, or mid-rebuild — harmless, try again next frame
        }

        if ((*(uint*)(menu + MenuStateBits) & 0x600) == 0x400 || *(int*)(menu + MenuAlive) == 0)
        {
            return 0;
        }

        nint guiManager = *(nint*)(menu + MenuGuiManager);
        if (!LooksLikeHeapPointer(guiManager))
        {
            PasswordEntryModal.Latch("menu gui manager pointer");
            return 0;
        }

        return guiManager;
    }

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    private void Log(string message)
    {
        if (_logged < 32)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
