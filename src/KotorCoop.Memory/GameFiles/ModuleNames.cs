namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Turns a module resref ("tar_m03aa") into the name the game calls that place
/// ("Taris - Lower City").
///
/// ⚠ NOT from <c>module.ifo</c>. Its <c>Mod_Name</c> is the resref again — measured on four
/// base-game modules, every one of them returned its own file name. The player-facing name
/// lives on the AREA (the <c>.are</c> beside it), as a <c>Name</c> StrRef into
/// <c>dialog.tlk</c>: 10221 -> "Endar Spire - Command Module", 35894 -> "Taris - Lower City".
///
/// Exists for the in-game server browser: a resref tells a browsing player almost nothing,
/// and the readable name is already sitting in files both installs have. Resolved locally on
/// the browsing side rather than sent by the host — the answer is identical on every install
/// of the base game, and a resref costs 16 bytes on the wire where a display name does not.
///
/// Every failure returns false and leaves the caller with the resref, which is what the
/// browser showed before this existed. A module that cannot be read is a cosmetic loss.
/// </summary>
public static class ModuleNames
{
    /// <summary>The area's player-facing name, a StrRef into dialog.tlk.</summary>
    private const string NameField = "Name";

    /// <summary>restype 2012 = "are".</summary>
    private const int AreType = 2012;

    /// <summary>Parsed once per install: dialog.tlk is ~10 MB and a browser needs a handful
    /// of strings from it.</summary>
    private static readonly Dictionary<string, Tlk?> TalkTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="module"/>'s display name out of
    /// <c>&lt;game&gt;\modules\&lt;module&gt;.rim</c> and <c>dialog.tlk</c>.
    ///
    /// The base game ships each module as a RIM pair (<c>x.rim</c> holds the static data,
    /// <c>x_s.rim</c> the scripts), and the area file lives in the first. A mod that replaced
    /// the module with a .mod (ERF) is read too, since that is where a modded install's
    /// version would be.
    /// </summary>
    public static bool TryRead(string gameDirectory, string module, out string name)
    {
        name = string.Empty;

        if (module.Length == 0 || module.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        string modules = Path.Combine(gameDirectory, "modules");
        if (!Directory.Exists(modules))
        {
            return false;
        }

        // A .mod (a modded or packaged module) wins over the stock .rim, matching the
        // engine's own preference for the packaged form.
        foreach (string extension in (string[])[".mod", ".rim"])
        {
            string path = Path.Combine(modules, module + extension);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                GameArchive archive = extension == ".mod"
                    ? ErfArchive.Open(path)
                    : RimArchive.Open(path);

                ArchiveResource? are = archive.Resources.FirstOrDefault(r => r.ResourceType == AreType);
                if (are is null || !archive.TryRead(are.Resref, are.Extension, out byte[] bytes))
                {
                    continue;
                }

                GffLocString? label = Gff.Parse(bytes).Root.GetLocString(NameField);
                if (label is null)
                {
                    continue;
                }

                // An inline substring wins when a mod supplied one; otherwise the StrRef into
                // this install's dialog.tlk, which is where every stock area's name lives.
                string text = label.Text();
                if (text.Length == 0 && label.StrRef >= 0)
                {
                    text = TalkTable(gameDirectory)?.Get(label.StrRef) ?? string.Empty;
                }

                if (text.Length > 0)
                {
                    name = text;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Try the next container; an unreadable module is not an error here.
            }
        }

        return false;
    }

    /// <summary>This install's dialog.tlk, parsed once. Null (cached as null) when it cannot
    /// be read, so a broken install costs one attempt rather than one per lookup.</summary>
    internal static Tlk? TalkTable(string gameDirectory)
    {
        lock (TalkTables)
        {
            if (TalkTables.TryGetValue(gameDirectory, out Tlk? cached))
            {
                return cached;
            }

            Tlk? table = null;
            try
            {
                string path = Path.Combine(gameDirectory, "dialog.tlk");
                if (File.Exists(path))
                {
                    table = Tlk.Open(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                table = null;
            }

            TalkTables[gameDirectory] = table;
            return table;
        }
    }
}
