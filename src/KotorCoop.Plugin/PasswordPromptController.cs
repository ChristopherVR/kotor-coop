namespace KotorCoop.Plugin;

/// <summary>
/// Asks the player for a locked server's password, in the game, at the moment it is needed.
///
/// The agent bumps <see cref="SharedBlock.OffsetBrowserPasswordSerial"/> when it knows a
/// password is wanted — either because the row the player picked in the browser is locked and
/// nothing in coop.ini could satisfy it, or because the host answered the join with
/// <c>E_PASSWORD</c>. This controller turns that into a real prompt: the developer console,
/// force-opened with <c>password&#160;</c> already typed and the caret after it, exactly the way
/// <see cref="ChatInputController"/> opens a chat box with <c>say&#160;</c>. The player types the
/// password, presses Enter, and <see cref="FuncInterpHook"/> routes the finished line back to the
/// agent, which retries the join with it.
///
/// Reusing the console rather than building a text-entry panel is deliberate. The console is a
/// working, engine-owned text field with a caret, backspace and a keyboard grab that the rest of
/// the game respects, it already renders at the main menu (where this prompt happens), and its
/// submit path is already wired to the agent. A GUI panel would be a second implementation of all
/// of that, for the one screen where the first one is known to work.
///
/// Runs on <c>MainLoop</c>, which fires at the menu and in-world alike — the SwapBuffers hook is
/// dormant at the menu, and the menu is precisely where a browser join is answered.
/// </summary>
internal sealed unsafe class PasswordPromptController
{
    private readonly SharedBlockView _block;

    /// <summary>The last request we have shown a prompt for; a higher serial is a new ask.</summary>
    private uint _servedSerial;

    /// <summary>Set when a request arrived at a moment we could not prompt; retried each frame.</summary>
    private bool _pending;

    public PasswordPromptController(SharedBlockView block)
    {
        _block = block;

        // Adopt whatever the block already holds. A restarted plugin (or a second agent run)
        // must not open a console the player never asked for, on the strength of a serial that
        // was answered in a previous life.
        _servedSerial = _block.BrowserPasswordSerial;
    }

    public void OnFrame()
    {
        uint serial = _block.BrowserPasswordSerial;
        if (serial != _servedSerial)
        {
            _servedSerial = serial;
            _pending = true;
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

        // A console the player opened themselves is theirs; try again next frame rather than
        // stamping "password " over a half-typed line.
        if (Odyssey.OpenConsoleWith("password "))
        {
            _pending = false;
        }
    }
}
