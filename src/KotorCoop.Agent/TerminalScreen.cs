using System.Runtime.InteropServices;

namespace KotorCoop.Agent;

/// <summary>
/// Owns the terminal completely while the dashboard's TUI is up.
///
/// <see cref="Enter"/> switches to the ALTERNATE screen buffer (VT <c>ESC[?1049h</c>) so
/// nothing under it can ever be scrolled into view — the bug report this whole rework answers
/// was exactly that: other output landing in the MAIN buffer's 9001-row scrollback left the
/// window looking at hundreds of blank rows with "impossible to scroll or see anything".
/// <see cref="Exit"/> restores it (<c>ESC[?1049l</c>). On Windows it also turns on VT/ANSI
/// processing for the output handle (<c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c>) — best-effort;
/// the packaged Linux agent is still this same Windows binary run under Proton/Wine, whose
/// console host already understands the same escape codes natively, so a failed mode-set there
/// is not fatal, just a no-op.
///
/// It also redirects <see cref="Console.Out"/>/<see cref="Console.Error"/> to a
/// <see cref="ConsoleCaptureWriter"/> for the duration: see that class's own doc comment for
/// why (a still-direct <c>Console.WriteLine</c> from anywhere in the codebase must land in the
/// transcript, never mid-screen). <see cref="Screen"/> is the REAL writer to paint frames
/// with — never <c>Console.Out</c> once <see cref="Enter"/> has run, since that would loop
/// the dashboard's own paint straight back into the capture writer it just installed.
///
/// <see cref="Exit"/> is idempotent and safe to call from more than one place racing (the
/// owning <c>try/finally</c>, a <see cref="Console.CancelKeyPress"/> handler, and
/// <see cref="AppDomain.ProcessExit"/> are all wired to it) — so however the process ends, the
/// real terminal is never left on the alternate screen with a hidden cursor.
/// </summary>
public sealed class TerminalScreen
{
    private const string EnterSequence = "\x1b[?1049h\x1b[?25l";
    private const string ExitSequence = "\x1b[?25h\x1b[?1049l";

    private readonly TextWriter _realOut;
    private readonly TextWriter _realError;
    private readonly Action<string> _onCapturedLine;

    private int _entered;
    private ConsoleCancelEventHandler? _cancelHandler;
    private EventHandler? _processExitHandler;

    public TerminalScreen(Action<string> onCapturedLine)
    {
        _onCapturedLine = onCapturedLine;
        _realOut = Console.Out;
        _realError = Console.Error;
    }

    /// <summary>The real console, for painting frames — never <see cref="Console.Out"/> once
    /// <see cref="Enter"/> has run (see this class's own doc comment).</summary>
    public TextWriter Screen => _realOut;

    public void Enter()
    {
        if (Interlocked.Exchange(ref _entered, 1) != 0)
        {
            return;
        }

        EnableVirtualTerminalProcessing();

        try
        {
            _realOut.Write(EnterSequence);
            _realOut.Flush();
        }
        catch
        {
            // A write to the real console handle should never be able to fail here, but if
            // it does, the dashboard must still be able to run (redirected/piped host).
        }

        Console.SetOut(new ConsoleCaptureWriter(_onCapturedLine));
        Console.SetError(new ConsoleCaptureWriter(line => _onCapturedLine($"[stderr] {line}")));

        _cancelHandler = (_, _) => Exit();
        Console.CancelKeyPress += _cancelHandler;

        _processExitHandler = (_, _) => Exit();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
    }

    public void Exit()
    {
        if (Interlocked.Exchange(ref _entered, 0) != 1)
        {
            return;
        }

        if (_cancelHandler is { } cancel)
        {
            Console.CancelKeyPress -= cancel;
            _cancelHandler = null;
        }

        if (_processExitHandler is { } processExit)
        {
            AppDomain.CurrentDomain.ProcessExit -= processExit;
            _processExitHandler = null;
        }

        try
        {
            Console.SetOut(_realOut);
            Console.SetError(_realError);
        }
        catch
        {
        }

        try
        {
            _realOut.Write(ExitSequence);
            _realOut.Flush();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Best-effort: enables <c>ENABLE_VIRTUAL_TERMINAL_PROCESSING</c> on the output handle so
    /// legacy <c>conhost.exe</c> (still the default host on older Windows 10 builds) honours
    /// the SGR/cursor escape codes this class and <see cref="AgentDashboard"/> write. Windows
    /// Terminal and modern conhost already default this on; a console with no handle at all
    /// (already redirected) or one that rejects the mode bit is left exactly as it was.
    /// </summary>
    private static void EnableVirtualTerminalProcessing()
    {
        try
        {
            nint handle = GetStdHandle(StdOutputHandle);
            if (handle == nint.Zero || handle == new nint(-1))
            {
                return;
            }

            if (!GetConsoleMode(handle, out uint mode))
            {
                return;
            }

            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessingFlag);
        }
        catch
        {
            // Non-Windows/uncommon hosts (or one that has already gone away): the dashboard
            // still renders using whatever ANSI support the terminal has on its own.
        }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessingFlag = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
