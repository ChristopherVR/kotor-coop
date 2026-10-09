using KotorCoop.Protocol;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Which creature blueprints (.utc) of a module are friendly to the player, for the joiner's
/// host-spawns-only exception (<see cref="FriendlyTemplateRule"/>). Reads the module's own
/// archives (module.rim, module_s.rim, module.mod, the same set ItemTemplateResolver searches)
/// and takes each blueprint's FactionID. A loose override\ copy of the same resref wins over the
/// archive copy, which is the engine's own precedence. Cached per module; every failure yields
/// an empty list, which keeps today's behaviour (everything suppressed).
/// </summary>
public sealed class FriendlyCreatureResolver(string gameDirectory)
{
    private const string UtcExtension = "utc";
    private const int UtcType = 2027;

    private readonly Dictionary<string, IReadOnlyList<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FriendlyTemplates(string module)
    {
        if (module.Length == 0)
        {
            return [];
        }

        if (_cache.TryGetValue(module, out IReadOnlyList<string>? known))
        {
            return known;
        }

        IReadOnlyList<string> result;
        try
        {
            result = Scan(module);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            result = [];
        }

        _cache[module] = result;
        return result;
    }

    private List<string> Scan(string module)
    {
        Dictionary<string, int> factions = new(StringComparer.OrdinalIgnoreCase);
        string modules = Path.Combine(gameDirectory, "modules");
        if (Directory.Exists(modules))
        {
            foreach (string name in new[] { module + ".rim", module + "_s.rim", module + ".mod" })
            {
                string path = Path.Combine(modules, name);
                if (!File.Exists(path))
                {
                    continue;
                }

                GameArchive archive = name.EndsWith(".mod", StringComparison.OrdinalIgnoreCase)
                    ? ErfArchive.Open(path)
                    : RimArchive.Open(path);
                foreach (ArchiveResource resource in archive.Resources)
                {
                    if (resource.ResourceType == UtcType && archive.TryRead(resource.Resref, UtcExtension, out byte[] data))
                    {
                        factions[resource.Resref.ToLowerInvariant()] = FactionOf(data);
                    }
                }
            }
        }

        string overrideDirectory = Path.Combine(gameDirectory, "override");
        if (Directory.Exists(overrideDirectory))
        {
            foreach (string resref in factions.Keys.ToList())
            {
                string loose = Path.Combine(overrideDirectory, resref + "." + UtcExtension);
                if (File.Exists(loose))
                {
                    factions[resref] = FactionOf(GameFileIo.ReadAllBytes(loose));
                }
            }
        }

        return factions.Where(pair => FriendlyTemplateRule.IsFriendlyFaction(pair.Value))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The blueprint's FactionID, or -1 when it has none or cannot be parsed.</summary>
    public static int FactionOf(byte[] utc)
    {
        try
        {
            return Gff.Parse(utc).Root.TryGetWord("FactionID", out ushort faction) ? faction : -1;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return -1;
        }
    }
}
