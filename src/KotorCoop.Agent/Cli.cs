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
