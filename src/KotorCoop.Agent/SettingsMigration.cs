using System.Text.RegularExpressions;

namespace KotorCoop.Agent;

/// <summary>Opt-in migration of historical defaults to the exact incoming template.</summary>
public static class SettingsMigration
{
    public sealed record Change(string Key, string OldValue, string NewValue);
    private static readonly Regex Setting = new(
        @"^(?<prefix>[ \t]*(?<key>[A-Za-z0-9_]+)[ \t]*=[ \t]*)(?<value>[^;#\r\n]*?)(?<suffix>[ \t]*(?:[;#][^\r\n]*)?\r?)$",
        RegexOptions.Multiline);
    private static readonly Dictionary<string, string> Historical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["menu_button"] = "0", ["effect_sync"] = "0", ["trigger_relay"] = "0",
        ["map_reveal_sync"] = "0", ["script_item_sync"] = "0", ["companion_xp_sync"] = "0",
        ["companion_local_combat"] = "0", ["dialogue_recruit_sync"] = "0",
        ["pazaak_detect"] = "0", ["party_pool_sync"] = "0", ["store_sync"] = "0",
        ["enemy_effect_sync"] = "0", ["partner_buffs"] = "0", ["threat_retarget"] = "0",
        ["liveness_sync"] = "0", ["join_mode"] = "save", ["world_transfer_udp_fallback"] = "0",
    };

    public static IReadOnlyList<Change> Find(string playerPath, string templatePath, IEnumerable<string>? excludedKeys = null)
    {
        if (!File.Exists(playerPath) || !File.Exists(templatePath))
        {
            return [];
        }
        Dictionary<string, string> template = Parse(File.ReadAllText(templatePath));
        Dictionary<string, string> player = Parse(File.ReadAllText(playerPath));
        HashSet<string> excluded = new(excludedKeys ?? [], StringComparer.OrdinalIgnoreCase);
        return Historical.Where(pair => !excluded.Contains(pair.Key) && player.TryGetValue(pair.Key, out string? value)
            && value.Equals(pair.Value, StringComparison.OrdinalIgnoreCase)
            && template.TryGetValue(pair.Key, out string? current)
            && !current.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            .Select(pair => new Change(pair.Key, pair.Value, template[pair.Key])).ToArray();
    }

    public static string Describe(Change change)
    {
        string label = change.Key.ToLowerInvariant() switch
        {
            "menu_button" => "Co-op button on the main menu",
            "effect_sync" => "Share combat effects",
            "trigger_relay" => "Share story triggers",
            "map_reveal_sync" => "Reveal the map around your friend",
            "script_item_sync" => "Share story item rewards",
            "companion_xp_sync" => "Keep companion experience in step",
            "companion_local_combat" => "Companion combat on the joining player's game",
            "dialogue_recruit_sync" => "Share recruited companions",
            "pazaak_detect" => "Recognise Pazaak games",
            "party_pool_sync" => "Shared inventory synchronization",
            "store_sync" => "Shared merchant stock",
            "enemy_effect_sync" => "Share effects on enemies",
            "partner_buffs" => "Share helpful powers with your friend",
            "threat_retarget" => "Let enemies react to both players",
            "liveness_sync" => "Keep creature deaths in step",
            "join_mode" => "Automatic entry into the host's world",
            "world_transfer_udp_fallback" => "Alternative connection for loading the host's world",
            _ => change.Key,
        };
        return $"{label}: {DisplayValue(change.OldValue)} -> {DisplayValue(change.NewValue)} ({change.Key})";
    }

    private static string DisplayValue(string value) => value switch
    {
        "0" => "off",
        "1" => "on",
        "2" => "shared mode",
        "save" => "manual save mode",
        "auto" => "automatic",
        _ => value,
    };

    public static string Apply(string playerPath, IReadOnlyList<Change> changes)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(playerPath));
        string executable = Path.Combine(directory!, PackageInstaller.GameExecutable);
        if (File.Exists(executable))
        {
            // A running game holds its image against writes. Match the installer's
            // refusal before changing settings, including setup run inside the game folder.
            using var probe = new FileStream(executable, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
        }
        string original = File.ReadAllText(playerPath);
        Dictionary<string, Change> byKey = changes.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        string updated = Setting.Replace(original, match =>
        {
            string key = match.Groups["key"].Value;
            return byKey.TryGetValue(key, out Change? change)
                && match.Groups["value"].Value.Trim().Equals(change.OldValue, StringComparison.OrdinalIgnoreCase)
                ? match.Groups["prefix"].Value + change.NewValue + match.Groups["suffix"].Value
                : match.Value;
        });
        string backup = playerPath + ".before-defaults-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(playerPath, backup, overwrite: false);
        File.WriteAllText(playerPath, updated);
        return backup;
    }

    private static Dictionary<string, string> Parse(string text)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Setting.Matches(text))
        {
            result[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
        }
        return result;
    }
}
