using System.Text.Json;

namespace KotorCoop.Protocol;

/// <summary>Optional, install-local recruitment rules. A profile names resources; it does
/// not download mods, patch scripts or enlarge the engine's nine companion slots.</summary>
public sealed class ModSupportProfile
{
    public const string FileName = "coop-mods.json";
    public const string CatalogDirectory = "coop-mods";
    public sealed record NumberCondition(string Global, int Min, int Max);
    public sealed record Companion(int Slot, string Template, string Module, NumberCondition? When = null);
    public sealed record Mod(string Id, string SourcePath, bool Active, string Status);
    public enum Resolution { Unclaimed, Template, UnknownCampaignSlot }
    public string Id { get; }
    public IReadOnlyList<string> CampaignModules { get; }
    public IReadOnlyList<Companion> Companions { get; }
    public IReadOnlyList<int> HoldUnmappedSlots { get; private init; } = [];
    public IReadOnlyList<Mod> Mods { get; private init; } = [];
    public IEnumerable<string> ActiveFiles => Mods.Where(mod => mod.Active).Select(mod => mod.SourcePath);
    public static ModSupportProfile Empty { get; } = new("", [], []);

    private ModSupportProfile(string id, string[] modules, Companion[] companions)
        => (Id, CampaignModules, Companions) = (id, modules, companions);

    public Resolution Resolve(int slot, string module, out string template,
        Func<string, int?>? readNumber = null)
    {
        template = string.Empty;
        if ((uint)slot >= 9) return Resolution.UnknownCampaignSlot;
        Companion? best = null;
        int unknownSpecificity = -1;
        foreach (Companion candidate in Companions)
        {
            if (candidate.Slot != slot || !Matches(candidate.Module, module)) continue;
            if (candidate.When is { } condition)
            {
                int? value = readNumber?.Invoke(condition.Global);
                if (value is null)
                {
                    unknownSpecificity = Math.Max(unknownSpecificity, Specificity(candidate.Module));
                    continue;
                }
                if (value < condition.Min || value > condition.Max) continue;
            }
            // Exact names beat prefixes; a longer prefix beats a broader rule.
            if (best is not null && Specificity(candidate.Module) == Specificity(best.Module)
                && candidate.Template != best.Template) return Resolution.UnknownCampaignSlot;
            if (best is null || Specificity(candidate.Module) > Specificity(best.Module))
                best = candidate;
        }
        if (unknownSpecificity >= (best is null ? 0 : Specificity(best.Module)))
            return Resolution.UnknownCampaignSlot;
        if (best is not null)
        {
            if (best.Template.Length == 0) return Resolution.UnknownCampaignSlot;
            template = best.Template;
            return Resolution.Template;
        }
        foreach (string pattern in CampaignModules)
            if (Matches(pattern, module)) return Resolution.UnknownCampaignSlot;
        if (HoldUnmappedSlots.Contains(slot)) return Resolution.UnknownCampaignSlot;
        return Resolution.Unclaimed;
    }

    public static bool Matches(string pattern, string module) => module.Length > 0
        && (pattern.EndsWith('*')
            ? module.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : module.Equals(pattern, StringComparison.OrdinalIgnoreCase));

    private static int Specificity(string pattern) => pattern.EndsWith('*')
        ? pattern.Length : 100 + pattern.Length;

    public static bool TryLoad(string gameDirectory, out ModSupportProfile profile, out string error)
    {
        profile = Empty;
        error = string.Empty;
        string currentPath = FileName;
        try
        {
            var paths = new List<string>();
            string legacy = Path.Combine(gameDirectory, FileName);
            if (File.Exists(legacy)) paths.Add(legacy);
            string catalog = Path.Combine(gameDirectory, CatalogDirectory);
            if (Directory.Exists(catalog)) paths.AddRange(Directory.EnumerateFiles(catalog, "*.json")
                .Order(StringComparer.OrdinalIgnoreCase));
            var mods = new List<Mod>();
            var modules = new List<string>();
            var companions = new List<Companion>();
            var heldSlots = new List<int>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                currentPath = Path.GetRelativePath(gameDirectory, path);
                ModSupportProfile part = Read(path, gameDirectory, path != legacy, out bool active, out string status);
                if (!ids.Add(part.Id)) throw new InvalidDataException($"duplicate mod id '{part.Id}'");
                mods.Add(new(part.Id, path, active, status));
                if (!active) continue;
                modules.AddRange(part.CampaignModules);
                heldSlots.AddRange(part.HoldUnmappedSlots);
                foreach (Companion companion in part.Companions)
                {
                    foreach (Companion prior in companions)
                        if (prior.Slot == companion.Slot && prior.Module == companion.Module
                            && prior.Template != companion.Template && ConditionsOverlap(prior.When, companion.When))
                            throw new InvalidDataException($"conflicting companion rules for slot {companion.Slot}, {companion.Module}");
                    companions.Add(companion);
                }
            }
            profile = new(string.Join(", ", mods.Where(mod => mod.Active).Select(mod => mod.Id)),
                modules.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), companions.ToArray())
                { Mods = mods, HoldUnmappedSlots = heldSlots.Distinct().ToArray() };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            error = $"{currentPath}: {ex.Message}";
            return false;
        }
    }

    private static ModSupportProfile Read(string path, string gameDirectory, bool requireDetection,
        out bool active, out string status)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 256 * 1024) throw new InvalidDataException("profile exceeds 256 KB");
        using JsonDocument json = JsonDocument.Parse(stream);
        JsonElement root = json.RootElement;
        int schema = root.GetProperty("schema").GetInt32();
        if (schema is not (1 or 2) || (requireDetection && schema != 2))
            throw new InvalidDataException("unsupported profile schema");
        if (schema == 2) CheckKeys(root, "schema", "id", "name", "status", "package", "source", "$schema",
            "notes", "detect", "campaignModules", "holdUnmappedSlots", "companions");
        string id = root.GetProperty("id").GetString() ?? "";
        if (id.Length is 0 or > 128) throw new InvalidDataException("profile id must be 1-128 characters");
        status = root.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "experimental" : "experimental";
        active = true;
        if (schema == 2)
        {
            CheckKeys(root.GetProperty("detect"), "allFiles");
            JsonElement markers = root.GetProperty("detect").GetProperty("allFiles");
            if (markers.GetArrayLength() == 0) throw new InvalidDataException("detection needs at least one marker file");
            foreach (JsonElement marker in markers.EnumerateArray())
            {
                string relative = marker.GetString() ?? "";
                ValidateRelativePath(relative);
                active &= File.Exists(Path.Combine(gameDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            }
        }
        var modules = new List<string>();
        if (root.TryGetProperty("campaignModules", out JsonElement moduleList))
            foreach (JsonElement item in moduleList.EnumerateArray())
                modules.Add(Pattern(item.GetString() ?? ""));
        var companions = new List<Companion>();
        var heldSlots = new List<int>();
        if (root.TryGetProperty("holdUnmappedSlots", out var heldList))
        {
            if (schema != 2) throw new InvalidDataException("held slots require schema 2");
            foreach (var item in heldList.EnumerateArray())
            {
                int slot = item.GetInt32();
                if ((uint)slot >= 9) throw new InvalidDataException("held slots must be 0-8");
                heldSlots.Add(slot);
            }
        }
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("companions", out JsonElement companionList))
            foreach (JsonElement item in companionList.EnumerateArray())
            {
                if (schema == 2) CheckKeys(item, "slot", "template", "module", "when", "recovery");
                int slot = item.GetProperty("slot").GetInt32();
                bool hold = item.TryGetProperty("recovery", out var recovery);
                if (hold && (schema != 2 || recovery.GetString() != "hold"))
                    throw new InvalidDataException("recovery accepts only 'hold' in schema 2");
                string template = hold ? "" : item.GetProperty("template").GetString() ?? "";
                if (hold && item.TryGetProperty("template", out _))
                    throw new InvalidDataException("a held slot must not declare a template");
                string module = Pattern(item.TryGetProperty("module", out JsonElement value)
                    ? value.GetString() ?? "" : "*");
                NumberCondition? when = null;
                if (item.TryGetProperty("when", out JsonElement condition))
                {
                    if (schema != 2) throw new InvalidDataException("global conditions require schema 2");
                    CheckKeys(condition, "global", "min", "max");
                    string global = condition.GetProperty("global").GetString() ?? "";
                    if (global.Length is 0 or > 22 || global.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                        throw new InvalidDataException("global conditions need an ASCII identifier of 1-22 characters");
                    int min = condition.TryGetProperty("min", out var minValue) ? minValue.GetInt32() : -128;
                    int max = condition.TryGetProperty("max", out var maxValue) ? maxValue.GetInt32() : 127;
                    if (min < -128 || max > 127 || min > max) throw new InvalidDataException("global condition range must be within -128..127");
                    when = new(global, min, max);
                }
                if ((uint)slot >= 9 || (!hold && !IsResref(template)))
                    throw new InvalidDataException("companion needs slot 0-8 and an ASCII resource name of 1-16 characters");
                if (!keys.Add($"{slot}:{module}:{when}")) throw new InvalidDataException("duplicate companion rule");
                companions.Add(new Companion(slot, template.ToLowerInvariant(), module, when));
            }
        return new(id, modules.ToArray(), companions.ToArray()) { HoldUnmappedSlots = heldSlots };
    }

    private static bool ConditionsOverlap(NumberCondition? a, NumberCondition? b) => a is null || b is null
        || !a.Global.Equals(b.Global, StringComparison.OrdinalIgnoreCase) || (a.Min <= b.Max && b.Min <= a.Max);

    private static void CheckKeys(JsonElement element, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"unknown profile field '{property.Name}'");
    }

    private static void ValidateRelativePath(string path)
    {
        string[] parts = path.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            || !(parts[0].Equals("override", StringComparison.OrdinalIgnoreCase)
                || parts[0].Equals("modules", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("marker paths must name a file in Override or modules");
    }

    public static bool IsResref(string name)
    {
        if (name.Length is 0 or > 16) return false;
        foreach (char c in name)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private static string Pattern(string value)
    {
        string prefix = value.EndsWith('*') ? value[..^1] : value;
        if (value != "*" && !IsResref(prefix))
            throw new InvalidDataException("module rules accept a resource name or a trailing * prefix");
        return value.ToLowerInvariant();
    }
}
