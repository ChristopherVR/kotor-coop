namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Turns an item blueprint resref ("g_w_hvyblstr01") into the name the game shows for it
/// ("Heavy Blaster") — the <c>LocalizedName</c> of the <c>.uti</c>, resolved through
/// <c>dialog.tlk</c> the same way <see cref="ModuleNames"/> resolves an area's name.
///
/// For the agent window's F2 drop picker: a list of resrefs tells a player almost nothing.
/// Every failure returns false and the caller shows the resref instead — a cosmetic loss.
/// Answers are cached per install and resref (the lookup opens chitin.key and a BIF).
/// </summary>
public static class ItemNames
{
    private const string NameField = "LocalizedName";

    private static readonly Dictionary<string, GameFiles> Installs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryRead(string gameDirectory, string resref, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrEmpty(gameDirectory) || string.IsNullOrEmpty(resref))
        {
            return false;
        }

        string key = gameDirectory + "|" + resref;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out string? cached))
            {
                name = cached ?? string.Empty;
                return cached is not null;
            }

            string? found = Resolve(gameDirectory, resref);
            Cache[key] = found;
            name = found ?? string.Empty;
            return found is not null;
        }
    }

    private static string? Resolve(string gameDirectory, string resref)
    {
        try
        {
            if (!Installs.TryGetValue(gameDirectory, out GameFiles? files))
            {
                files = new GameFiles(gameDirectory);
                Installs[gameDirectory] = files;
            }

            if (!files.TryReadResource(resref, "uti", out byte[] bytes))
            {
                return null;
            }

            GffLocString? label = Gff.Parse(bytes).Root.GetLocString(NameField);
            if (label is null)
            {
                return null;
            }

            string text = label.Text();
            if (text.Length == 0 && label.StrRef >= 0)
            {
                text = ModuleNames.TalkTable(gameDirectory)?.Get(label.StrRef) ?? string.Empty;
            }

            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
