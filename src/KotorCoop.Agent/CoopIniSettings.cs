using System.Text.RegularExpressions;

namespace KotorCoop.Agent;

/// <summary>
/// Writes chosen values into an existing coop.ini for <c>install --set key=value</c> (what the
/// graphical setup's options page sends). Edits in place so the player's comments and every
/// other line survive: an active <c>key = old</c> line keeps its trailing comment, a commented
/// <c># key = x</c> example becomes the active line, and a key with neither is appended to the
/// end of the <c>[coop]</c> section.
/// </summary>
public static class CoopIniSettings
{
    public sealed record Change(string Key, string? OldValue, string NewValue);

    private static readonly Regex KeyShape = new("^[A-Za-z0-9_]+$");

    /// <summary>A value the readers would cut short (<c>;</c>/<c>#</c> start a comment), or that
    /// would break the plugin's quoted agent command line (<c>"</c>), is refused rather than
    /// written half-applied.</summary>
    public static string? Validate(string key, string value)
    {
        if (!KeyShape.IsMatch(key))
        {
            return $"'{key}' is not a coop.ini key (letters, digits and _ only)";
        }

        return value.IndexOfAny([';', '#', '"', '\r', '\n']) >= 0
            ? $"{key}: the value cannot contain ; # \" or a line break"
            : null;
    }

    /// <summary>The active value of <paramref name="key"/>, or null when the file or the key is absent.</summary>
    public static string? Read(string path, string key)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Regex active = ActiveLine(key);
        foreach (string line in File.ReadLines(path))
        {
            Match match = active.Match(line);
            if (match.Success)
            {
                return match.Groups["value"].Value.Trim();
            }
        }

        return null;
    }

    private static Regex ActiveLine(string key) =>
        new($@"^(?<prefix>[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*)(?<value>[^;#\r]*?)(?<suffix>[ \t]*(?:[;#].*)?)\r?$",
            RegexOptions.IgnoreCase);

    public static IReadOnlyList<Change> Apply(string path, IReadOnlyList<KeyValuePair<string, string>> settings)
    {
        string text = File.Exists(path) ? File.ReadAllText(path) : "[coop]" + Environment.NewLine;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. text.Split(newline)];
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        List<Change> changes = [];
        foreach ((string key, string value) in settings)
        {
            if (Validate(key, value) is { } problem)
            {
                throw new ArgumentException(problem);
            }

            string? old = Set(lines, key, value);
            if (old is null || !old.Equals(value, StringComparison.Ordinal))
            {
                changes.Add(new Change(key, old, value));
            }
        }

        if (changes.Count > 0)
        {
            File.WriteAllText(path, string.Join(newline, lines) + newline);
        }

        return changes;
    }

    /// <summary>Sets one key; returns its previous active value, or null when it had none.</summary>
    private static string? Set(List<string> lines, string key, string value)
    {
        Regex active = ActiveLine(key);
        for (int i = 0; i < lines.Count; i++)
        {
            Match match = active.Match(lines[i]);
            if (match.Success)
            {
                string old = match.Groups["value"].Value.Trim();
                string prefix = match.Groups["prefix"].Value;
                if (!prefix.EndsWith(' ') && !prefix.EndsWith('	'))
                {
                    // "name =" with nothing after it: keep the file's "key = value" look.
                    prefix += " ";
                }

                lines[i] = prefix + value + match.Groups["suffix"].Value;
                return old;
            }
        }

        Regex commented = new($@"^[ \t]*[#;][ \t]*{key}[ \t]*=", RegexOptions.IgnoreCase);
        int example = lines.FindIndex(commented.IsMatch);
        if (example >= 0)
        {
            lines[example] = $"{key} = {value}";
            return null;
        }

        int section = lines.FindIndex(l => l.Trim().Equals("[coop]", StringComparison.OrdinalIgnoreCase));
        if (section < 0)
        {
            lines.Add("[coop]");
            section = lines.Count - 1;
        }

        int next = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith('['));
        int insert = next < 0 ? lines.Count : next;
        while (insert > section + 1 && lines[insert - 1].Trim().Length == 0)
        {
            insert--;
        }

        lines.Insert(insert, $"{key} = {value}");
        return null;
    }
}
