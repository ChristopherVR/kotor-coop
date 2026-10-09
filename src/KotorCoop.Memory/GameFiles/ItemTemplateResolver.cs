namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Turns a live item's TAG into the blueprint resref it was built from. A live CSWSItem keeps no
/// template name (KotorGameBridge.Items.cs, ItemResref's remarks), so the tag stands in for it,
/// which is right for base-game items ("G_W_BLSTRPSTL001" is g_w_blstrpstl001.uti) and wrong
/// for a blueprint whose tag differs: the Endar Spire's own g_w_vbroshort002.uti carries the tag
/// "OneDamageBlade", so a Sith corpse's loot reached the joiner as "onedamageblade" and nothing
/// could be built from it (live 2026-10-09).
///
/// Resolution, cached per (module, tag): a blueprint named exactly the tag wins, so ordinary
/// items are untouched; otherwise the current module's archives (module.rim, module_s.rim,
/// module.mod) are searched for a .uti carrying that tag, then override\. No match keeps the tag.
/// Every failure (missing install, unreadable archive) also keeps the tag: the old behaviour.
/// </summary>
public sealed class ItemTemplateResolver(string gameDirectory)
{
    private const string UtiExtension = "uti";
    private const int UtiType = 2025;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _moduleTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _overrideTags;
    private GameFiles? _files;

    public string Resolve(string tag, string module)
    {
        if (tag.Length == 0)
        {
            return tag;
        }

        string key = module + "|" + tag;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out string? known))
            {
                return known;
            }

            string resolved = ResolveUncached(tag.ToLowerInvariant(), module);
            if (_cache.Count > 4096)
            {
                _cache.Clear();
            }

            _cache[key] = resolved;
            return resolved;
        }
    }

    private string ResolveUncached(string tag, string module)
    {
        try
        {
            // The common case first, and cheaply: a base-game item whose tag is its resref. Only
            // a miss opens the module's archives (once per module), so a transition's first item
            // reads do not pay for parsing every blueprint in the area.
            _files ??= new GameFiles(gameDirectory);
            if (_files.TryReadResource(tag, UtiExtension, out _))
            {
                return tag;
            }

            Dictionary<string, string> moduleTags = ModuleTags(module);
            if (moduleTags.ContainsValue(tag))
            {
                return tag;
            }

            if (moduleTags.TryGetValue(tag, out string? local))
            {
                return local;
            }

            return OverrideTags().TryGetValue(tag, out string? loose) ? loose : tag;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return tag;
        }
    }

    /// <summary>tag (lowercase) -> resref for every .uti in the module's own archives.</summary>
    private Dictionary<string, string> ModuleTags(string module)
    {
        if (_moduleTags.TryGetValue(module, out Dictionary<string, string>? known))
        {
            return known;
        }

        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        string modules = Path.Combine(gameDirectory, "modules");
        if (module.Length > 0 && Directory.Exists(modules))
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
                    if (resource.ResourceType == UtiType && archive.TryRead(resource.Resref, UtiExtension, out byte[] data))
                    {
                        AddTag(tags, data, resource.Resref);
                    }
                }
            }
        }

        _moduleTags[module] = tags;
        return tags;
    }

    private Dictionary<string, string> OverrideTags()
    {
        if (_overrideTags is not null)
        {
            return _overrideTags;
        }

        Dictionary<string, string> tags = new(StringComparer.OrdinalIgnoreCase);
        string overrideDirectory = Path.Combine(gameDirectory, "override");
        if (Directory.Exists(overrideDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(overrideDirectory, "*.uti"))
            {
                AddTag(tags, GameFileIo.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
            }
        }

        return _overrideTags = tags;
    }

    private static void AddTag(Dictionary<string, string> tags, byte[] uti, string resref)
    {
        if (Gff.Parse(uti).Root.TryGetString("Tag", out string raw) && raw.Length > 0)
        {
            tags.TryAdd(raw.ToLowerInvariant(), resref.ToLowerInvariant());
        }
    }
}
