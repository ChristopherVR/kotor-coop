namespace KotorCoop.Agent;

/// <summary>
/// Console colouring for the interactive `setup`/`install`/`uninstall` verbs. Kept entirely
/// separate from <see cref="PackageInstaller"/>, which only ever writes plain text through an
/// <c>Action&lt;string&gt;</c> log delegate and knows nothing about a console - <see cref="Log"/>
/// recognises that delegate's own line shapes (`PASS:`, `found:`, ...) and colours them here.
/// Falls back to plain text whenever output isn't an interactive console (redirected to a file,
/// piped, or CI), since ANSI/attribute colouring has nothing to attach to there.
/// </summary>
internal static class Cli
{
    private static readonly bool Enabled = !Console.IsOutputRedirected;

    private const string BannerRule = "---------------------------------";

    private static readonly string[] BannerKotor =
    [
        "  #   #  ###  #####  ###  ####",
        "  #  #  #   #   #   #   # #   #",
        "  ###   #   #   #   #   # ####",
        "  #  #  #   #   #   #   # #  #",
        "  #   #  ###    #    ###  #   #",
    ];

    private static readonly string[] BannerCoop =
    [
        "      ####  ###   ###  ####",
        "     #     #   # #   # #   #",
        "     #     #   # #   # ####",
        "     #     #   # #   # #",
        "      ####  ###   ###  #",
    ];

    /// <summary>The block-letter "KOTOR / COOP" splash, printed once when the exe first opens
    /// so a double-click gets an immediate sign of life before any (possibly slow) disk work.</summary>
    public static void Banner()
    {
        WriteLine(BannerRule, ConsoleColor.DarkGray);
        foreach (string line in BannerKotor)
        {
            WriteLine(line, ConsoleColor.Cyan);
        }

        Console.WriteLine();
        foreach (string line in BannerCoop)
        {
            WriteLine(line, ConsoleColor.Yellow);
        }

        WriteLine(BannerRule, ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    /// <summary>A short section title, e.g. "kotor-coop setup".</summary>
    public static void Heading(string text)
    {
        WriteLine(text, ConsoleColor.Cyan);
    }

    /// <summary>
    /// Colours one line from a <see cref="PackageInstaller"/> log callback, or one of
    /// <see cref="Commands.InstallCommand.ChooseGameDirectory"/>'s own "  found: ..." lines -
    /// both are plain text with an optional leading indent, so the tag is matched after that.
    /// </summary>
    public static void Log(string line)
    {
        string trimmed = line.TrimStart(' ');
        string indent = line[..(line.Length - trimmed.Length)];

        if (trimmed.StartsWith("PASS:", StringComparison.Ordinal))
        {
            WritePrefixed(indent, "PASS:", trimmed[5..], ConsoleColor.Green);
        }
        else if (trimmed.StartsWith("FAIL:", StringComparison.Ordinal))
        {
            WritePrefixed(indent, "FAIL:", trimmed[5..], ConsoleColor.Red);
        }
        else if (trimmed.StartsWith("found:", StringComparison.Ordinal))
        {
            WritePrefixed(indent, "found:", trimmed[6..], ConsoleColor.Cyan);
        }
        else if (trimmed.StartsWith("skipped:", StringComparison.Ordinal))
        {
            WriteLine(line, ConsoleColor.DarkGray);
        }
        else if (trimmed.StartsWith("installed", StringComparison.Ordinal) || trimmed.StartsWith("staged", StringComparison.Ordinal))
        {
            WriteLine(line, ConsoleColor.Green);
        }
        else
        {
            Console.WriteLine(line);
        }
    }

    /// <summary>A numbered choice in an interactive prompt, e.g. "  [1] C:\...".</summary>
    public static void Choice(int number, string text)
    {
        if (!Enabled)
        {
            Console.WriteLine($"  [{number}] {text}");
            return;
        }

        Console.Write("  [");
        Write(number.ToString(), ConsoleColor.Yellow);
        Console.Write("] ");
        Console.WriteLine(text);
    }

    /// <summary>Wording that matters less than the line around it (paths, hints, asides).</summary>
    public static void Dim(string text) => WriteLine(text, ConsoleColor.DarkGray);

    /// <summary>A finished, working outcome - "Installed.", "started ...".</summary>
    public static void Success(string text) => WriteLine(text, ConsoleColor.Green);

    /// <summary>A question the player answers, left on the cursor line like <see cref="Console.Write(string)"/>.</summary>
    public static void Prompt(string text) => Write(text, Enabled ? ConsoleColor.Yellow : null);

    public static void Error(string text) => WriteLine(text, ConsoleColor.Red, Console.Error);

    /// <summary>A problem worth the player's attention that isn't a hard failure (stdout, not stderr).</summary>
    public static void Warn(string text) => WriteLine(text, ConsoleColor.Yellow);

    /// <summary>
    /// Runs <paramref name="work"/> with a spinning "message ..." on the current console line,
    /// so a scan across every fixed drive (a few seconds is normal) shows something is
    /// happening instead of sitting silent - which is exactly the kind of pause that makes a
    /// player mash Enter, and <see cref="FlushInput"/> exists to stop that mashing from
    /// silently answering whatever prompt shows up next.
    /// </summary>
    public static T Spinner<T>(string message, Func<T> work)
    {
        if (!Enabled)
        {
            Console.WriteLine(message);
            return work();
        }

        using var stop = new ManualResetEventSlim(false);
        char[] frames = ['|', '/', '-', '\\'];
        var spin = Task.Run(() =>
        {
            int i = 0;
            while (!stop.Wait(120))
            {
                Console.Write($"\r{message} {frames[i++ % frames.Length]}");
            }
        });

        try
        {
            return work();
        }
        finally
        {
            stop.Set();
            spin.Wait();
            Console.Write($"\r{new string(' ', message.Length + 2)}\r");
        }
    }

    public static void Spinner(string message, Action work) => Spinner(message, () => { work(); return true; });

    /// <summary>
    /// One console line region that gets overwritten in place on every subsequent
    /// <see cref="Rewrite"/> instead of stacking new output below it - for a prompt that may
    /// loop on bad input, so mashing Enter through a re-asked question doesn't fill the
    /// screen with repeated copies of it. Falls back to plain sequential printing the moment
    /// cursor positioning fails (redirected output, some CI terminals) rather than throwing.
    /// </summary>
    public sealed class RedrawRegion
    {
        private int _row = -1;
        private bool _capable = true;

        public void Rewrite(Action print)
        {
            if (!_capable)
            {
                print();
                return;
            }

            try
            {
                if (_row < 0)
                {
                    _row = Console.CursorTop;
                }
                else
                {
                    int bottom = Console.CursorTop;
                    for (int r = _row; r <= bottom; r++)
                    {
                        Console.SetCursorPosition(0, r);
                        Console.Write(new string(' ', Math.Max(0, Console.WindowWidth - 1)));
                    }

                    Console.SetCursorPosition(0, _row);
                }
            }
            catch (Exception)
            {
                // Redirected output, a console host that refuses cursor moves, or a window
                // resize mid-prompt - stop trying and just let output scroll normally.
                _capable = false;
            }

            print();
        }
    }

    /// <summary>
    /// Discards any keystrokes already sitting in the input buffer. Call right before every
    /// prompt that reads a line: without it, an impatient Enter (or two) pressed while a scan
    /// or copy was running silently becomes the answer to whichever prompt appears next,
    /// which reads as "the tool ignored what I typed" - it didn't, it answered too early.
    /// </summary>
    public static void FlushInput()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }
        }
        catch (InvalidOperationException)
        {
            // KeyAvailable/ReadKey unsupported on this host; nothing to drain.
        }
    }

    private static void WritePrefixed(string indent, string prefix, string rest, ConsoleColor color)
    {
        if (!Enabled)
        {
            Console.WriteLine(indent + prefix + rest);
            return;
        }

        Console.Write(indent);
        Write(prefix, color);
        Console.WriteLine(rest);
    }

    private static void WriteLine(string text, ConsoleColor color, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        if (!Enabled)
        {
            writer.WriteLine(text);
            return;
        }

        Console.ForegroundColor = color;
        writer.WriteLine(text);
        Console.ResetColor();
    }

    private static void Write(string text, ConsoleColor? color)
    {
        if (color is null)
        {
            Console.Write(text);
            return;
        }

        Console.ForegroundColor = color.Value;
        Console.Write(text);
        Console.ResetColor();
    }
}
