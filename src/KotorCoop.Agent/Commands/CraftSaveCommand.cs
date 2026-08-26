using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent craft-save` verb: offline, additive save-flag surgery
/// (docs/research/minigames.md §11). Copies --source into a brand new --dest and forces the
/// requested GLOBALVARS.res booleans/numbers, reporting exactly which names existed in that
/// save's own catalogue and which did not. Never opens --source for writing.
/// </summary>
internal static class CraftSaveCommand
{
    public static int Run(AgentOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CraftSource) || string.IsNullOrWhiteSpace(options.CraftDest))
        {
            Console.Error.WriteLine("craft-save needs --source <dir> and --dest <dir>.");
            return 1;
        }

        Dictionary<string, bool> booleans = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in options.CraftBooleans)
        {
            (string name, bool value) = ParseFlagArg(raw, defaultValue: true);
            booleans[name] = value;
        }

        Dictionary<string, int> numbers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in options.CraftNumbers)
        {
            int eq = raw.IndexOf('=');
            if (eq < 0 || !int.TryParse(raw[(eq + 1)..], out int value))
            {
                Console.Error.WriteLine($"--number expects NAME=VALUE, got '{raw}'.");
                return 1;
            }

            numbers[raw[..eq]] = value;
        }

        bool ok = GlobalFlagCraft.TryCraft(
            options.CraftSource, options.CraftDest, booleans, numbers,
            out IReadOnlyList<GlobalFlagCraft.FlagResult> results, out string diagnostic);

        foreach (GlobalFlagCraft.FlagResult result in results)
        {
            Console.WriteLine(result.Found
                ? $"  [{result.Type}] {result.Name}: {result.PreviousValue} -> {result.NewValue}"
                : $"  [{result.Type}] {result.Name}: NOT in this save's catalogue (left untouched)");
        }

        Console.WriteLine(diagnostic);
        return ok ? 0 : 1;
    }

    private static (string Name, bool Value) ParseFlagArg(string raw, bool defaultValue)
    {
        int eq = raw.IndexOf('=');
        if (eq < 0)
        {
            return (raw, defaultValue);
        }

        string tail = raw[(eq + 1)..];
        bool value = tail is not ("0" or "false" or "False");
        return (raw[..eq], value);
    }
}
