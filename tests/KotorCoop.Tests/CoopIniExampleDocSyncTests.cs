using System.Text.RegularExpressions;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Guards <c>profiles/coop.ini.example</c> against <c>docs/CONFIG.md</c> and the two coop.ini
/// readers (<c>CoopConfig.cs</c>, <c>PluginConfig.cs</c> + its per-controller call sites)
/// drifting apart the way they did across the 2026-08-27/28 merge wave — several agents
/// appended blocks near the same anchors, and at least two keys (<c>script_item_sync</c>,
/// <c>companion_xp_sync</c>) ended up documented "ON by default" in <c>CONFIG.md</c> and in
/// their own C# doc comments while the actual construction path
/// (<c>AgentComposition.Compose</c>'s <c>config?.X ?? false</c>) silently falls back to OFF —
/// see <c>docs/CONFIG.md</c>'s footnote ³ for the corrected story.
///
/// Reads the actual source files rather than hand-copying a key list into the test, the same
/// discipline <c>BehaviourOverrideBitsSyncTests</c>/<c>CheatKindSyncTests</c> use for the
/// plugin/agent split — the alternative is a fourth copy of the key list, one more thing to
/// drift.
/// </summary>
public class CoopIniExampleDocSyncTests
{
    [Fact]
    public void EveryActiveKeyInTheExampleIsConsumedBySomeReader()
    {
        string root = RepositoryRoot();
        HashSet<string> exampleKeys = new(
            KeyValues(File.ReadAllLines(Path.Combine(root, "profiles", "coop.ini.example"))).Keys,
            StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(exampleKeys);

        HashSet<string> consumed = ConsumedKeys(root);
        Assert.True(consumed.Count > 50, $"only found {consumed.Count} consumed keys - the " +
            "scrape regex probably broke, not the readers");

        List<string> orphans = [.. exampleKeys.Where(k => !consumed.Contains(k))];
        Assert.True(
            orphans.Count == 0,
            "profiles/coop.ini.example sets a key that no reader (CoopConfig.cs's kv lookups, "
            + "or a PluginConfig.Read/ReadBool/ReadInt/ReadFloat call anywhere under "
            + "src/KotorCoop.Plugin) ever asks for: " + string.Join(", ", orphans));
    }

    /// <summary>
    /// For every `| \`key\` | ON-or-OFF... |` row in either of CONFIG.md's two default tables:
    /// if the shipped example sets that key explicitly AND the doc's Default cell is a PLAIN
    /// ON/OFF with no footnote marker (a footnote is the documented, allowed exception - see
    /// docs/CONFIG.md's own convention, e.g. footnote ³), the shipped value must agree. A key
    /// whose code fallback silently changed (or whose shipped value silently changed) without
    /// updating the other trips this instead of quietly drifting for another merge wave.
    /// </summary>
    [Fact]
    public void EveryPlainOnOffDefaultInConfigMdMatchesTheShippedExample()
    {
        string root = RepositoryRoot();
        Dictionary<string, string> exampleValues = KeyValues(
            File.ReadAllLines(Path.Combine(root, "profiles", "coop.ini.example")));
        string configMd = File.ReadAllText(Path.Combine(root, "docs", "CONFIG.md"));

        MatchCollection rows = Regex.Matches(
            configMd, @"^\|\s*`([a-zA-Z0-9_]+)`\s*\|\s*([^|]+?)\s*\|", RegexOptions.Multiline);
        Assert.True(rows.Count > 30, $"only found {rows.Count} key rows in CONFIG.md - the " +
            "scrape regex probably broke, not the docs");

        List<string> mismatches = [];
        foreach (Match row in rows)
        {
            string key = row.Groups[1].Value;
            string cell = row.Groups[2].Value;

            if (!exampleValues.TryGetValue(key, out string? shipped))
            {
                continue; // not explicitly set in the shipped file - nothing to cross-check
            }

            Match onOff = Regex.Match(cell, @"^(ON|OFF)([¹²³⁰-⁹]*)");
            if (!onOff.Success)
            {
                continue; // not a plain boolean cell (a mode string, a number, a server list, ...)
            }

            if (onOff.Groups[2].Value.Length > 0)
            {
                continue; // footnoted - the doc itself explains why this one may differ from
                          // the code fallback; see docs/CONFIG.md's footnote convention.
            }

            bool docSaysOn = onOff.Groups[1].Value == "ON";
            bool shippedIsOn = IsTruthy(shipped);

            if (docSaysOn != shippedIsOn)
            {
                mismatches.Add($"{key}: CONFIG.md says {(docSaysOn ? "ON" : "OFF")} with no "
                    + $"footnote, but profiles/coop.ini.example ships `{key} = {shipped}` "
                    + $"({(shippedIsOn ? "ON" : "OFF")}) - either fix the shipped value or add "
                    + "a footnote explaining the difference (docs/CONFIG.md's existing "
                    + "footnote convention, e.g. footnote 3).");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    /// <summary>
    /// Every key either reader consumes must appear SOMEWHERE in docs/CONFIG.md — the "Player
    /// settings" table or the "Developer switches" table. Catches an undocumented switch: a key
    /// a controller reads via <c>PluginConfig.Read*</c> (or <c>CoopConfig</c>'s own kv lookups)
    /// but that nobody ever added a row for, which would otherwise sit there silently with no
    /// pointer to what it does or why. Player-facing classification itself (which table a key
    /// belongs in) is exercised directly against <c>CoopConfig</c>'s own sets in
    /// <c>CoopConfigTests</c>; this only checks that a row exists at all.
    /// </summary>
    [Fact]
    public void EveryConsumedKeyIsDocumentedInConfigMd()
    {
        string root = RepositoryRoot();
        HashSet<string> consumed = ConsumedKeys(root);
        Assert.True(consumed.Count > 50, $"only found {consumed.Count} consumed keys - the " +
            "scrape regex probably broke, not the readers");

        string configMd = File.ReadAllText(Path.Combine(root, "docs", "CONFIG.md"));
        HashSet<string> documented = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(configMd, @"^\|\s*`([a-zA-Z0-9_]+)`", RegexOptions.Multiline))
        {
            documented.Add(row.Groups[1].Value);
        }

        Assert.True(documented.Count > 50, $"only found {documented.Count} documented keys in " +
            "docs/CONFIG.md - the scrape regex probably broke, not the docs");

        List<string> undocumented = [.. consumed.Where(k => !documented.Contains(k))];
        Assert.True(
            undocumented.Count == 0,
            "a reader (CoopConfig.cs's kv lookups, or a PluginConfig.Read/ReadBool/ReadInt/"
            + "ReadFloat call anywhere under src/KotorCoop.Plugin) consumes a coop.ini key with "
            + "no row in either of docs/CONFIG.md's tables: " + string.Join(", ", undocumented));
    }

    /// <summary>key -&gt; raw value, for every UNCOMMENTED <c>key = value</c> line - the same
    /// "skip # ; [ " rule <see cref="KotorCoop.Session.CoopConfig.TryLoad"/> itself uses.</summary>
    private static Dictionary<string, string> KeyValues(IEnumerable<string> lines)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            int comment = value.IndexOfAny([';', '#']);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }

            values[key] = value;
        }

        return values;
    }

    private static bool IsTruthy(string value) =>
        value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every coop.ini key EITHER reader (CoopConfig.cs's dictionary lookups, or a
    /// PluginConfig.Read/ReadBool/ReadInt/ReadFloat call site anywhere under
    /// src/KotorCoop.Plugin) actually asks for, scraped from the source rather than
    /// hand-maintained here.</summary>
    private static HashSet<string> ConsumedKeys(string root)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);

        string coopConfigPath = Path.Combine(root, "src", "KotorCoop.Session", "CoopConfig.cs");
        Assert.True(File.Exists(coopConfigPath), $"expected to find {coopConfigPath}");
        foreach (Match m in Regex.Matches(
            File.ReadAllText(coopConfigPath),
            @"kv\.(?:GetValueOrDefault|ContainsKey|TryGetValue)\(""([a-zA-Z0-9_]+)"""))
        {
            keys.Add(m.Groups[1].Value);
        }

        string pluginDir = Path.Combine(root, "src", "KotorCoop.Plugin");
        Assert.True(Directory.Exists(pluginDir), $"expected to find {pluginDir}");
        foreach (string file in Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(
                File.ReadAllText(file),
                @"PluginConfig\.Read(?:Bool|Int|Float)?\(\s*""([a-zA-Z0-9_]+)"""))
            {
                keys.Add(m.Groups[1].Value);
            }
        }

        return keys;
    }

    /// <summary>Walks up from the test binary to the directory holding the solution file.
    /// Fails with the path it reached rather than a null reference, so a moved test project
    /// reads as a moved test project.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
