namespace KotorCoop.Session;

/// <summary>
/// Named saves for the rig and for testers (2026-09-19): <c>save &lt;name&gt;</c> keeps a copy of the
/// engine's quicksave under a chosen name, and <c>loadgame &lt;name&gt;</c> loads it back, so a later
/// position (a <c>warp</c> to Manaan, the Leviathan, the Star Forge) can be saved ONCE and returned
/// to without replaying anything. The engine only ever writes one quicksave
/// (<c>000000 - QUICKSAVE</c>); the copy is an ordinary save folder <c>NNNNNN - name</c>, so the
/// game's own Load Game screen lists it too.
///
/// Pure, so the naming and the slot lookup can be tested without a game. The slot ordering here
/// MUST equal <c>PluginMenuLoader.TryResolveSaveDirectory</c>'s: folders sorted by last write time,
/// newest first, the autosave excluded (counting it shifts every slot by one — the 2026-08 off-by-
/// one).
/// </summary>
public static class NamedSaveRule
{
    /// <summary>Longest name kept; save folder names stay readable in the load list.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The folder the engine's own quicksave writes.</summary>
    public const string QuickSaveFolder = "000000 - QUICKSAVE";

    /// <summary>Reduces free text to a folder-safe name: letters, digits, space, dash and
    /// underscore only, single spaces, trimmed, at most <see cref="MaxNameLength"/> characters.
    /// Returns empty when nothing usable is left.</summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        System.Text.StringBuilder sb = new(raw.Length);
        bool lastWasSpace = true;
        foreach (char c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
                lastWasSpace = false;
            }
            else if (c == ' ' && !lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        string name = sb.ToString().Trim();
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }

    /// <summary>The next free save number: one above the highest leading number in
    /// <paramref name="existingFolderNames"/>, at least 1 (0 belongs to the quicksave).</summary>
    public static int NextIndex(IEnumerable<string> existingFolderNames)
    {
        ArgumentNullException.ThrowIfNull(existingFolderNames);

        int highest = 0;
        foreach (string folder in existingFolderNames)
        {
            int digits = 0;
            while (digits < folder.Length && char.IsAsciiDigit(folder[digits]))
            {
                digits++;
            }

            if (digits > 0 && int.TryParse(folder.AsSpan(0, digits), out int number) && number > highest)
            {
                highest = number;
            }
        }

        return highest + 1;
    }

    /// <summary>The folder a named save lives in: <c>000012 - name</c>.</summary>
    public static string FolderName(int index, string sanitisedName) => $"{index:D6} - {sanitisedName}";

    /// <summary>The name part of a save folder (<c>000012 - foo</c> gives <c>foo</c>), or the whole
    /// folder name when it has no <c>NNNNNN - </c> prefix.</summary>
    public static string NameOf(string folderName)
    {
        int dash = folderName.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 && dash <= 6 ? folderName[(dash + 3)..] : folderName;
    }

    /// <summary>What a <see cref="FindSlot"/> lookup decided.</summary>
    public enum LookupResult
    {
        Found,
        NotFound,
        Ambiguous,
    }

    /// <summary>
    /// Maps a wanted name to the 0-based slot <c>loadgame</c> takes. <paramref name="saves"/> is
    /// the save folders with their last-write times; the autosave is excluded and the rest ordered
    /// newest first, exactly as the loader does. Matching is case-insensitive: an exact folder name
    /// or name-part wins, else a UNIQUE substring of the name-part; more than one substring match is
    /// <see cref="LookupResult.Ambiguous"/>.
    /// </summary>
    public static LookupResult FindSlot(
        IEnumerable<(string Folder, DateTime WrittenUtc)> saves, string wanted, out int slot, out string matchedFolder)
    {
        ArgumentNullException.ThrowIfNull(saves);

        slot = -1;
        matchedFolder = string.Empty;

        string want = (wanted ?? string.Empty).Trim();
        if (want.Length == 0)
        {
            return LookupResult.NotFound;
        }

        List<string> ordered =
        [
            .. saves
                .Where(s => !s.Folder.EndsWith("AUTOSAVE", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.WrittenUtc)
                .Select(s => s.Folder),
        ];

        for (int i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i], want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(NameOf(ordered[i]), want, StringComparison.OrdinalIgnoreCase))
            {
                slot = i;
                matchedFolder = ordered[i];
                return LookupResult.Found;
            }
        }

        int hit = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (NameOf(ordered[i]).Contains(want, StringComparison.OrdinalIgnoreCase))
            {
                if (hit >= 0)
                {
                    return LookupResult.Ambiguous;
                }

                hit = i;
            }
        }

        if (hit < 0)
        {
            return LookupResult.NotFound;
        }

        slot = hit;
        matchedFolder = ordered[hit];
        return LookupResult.Found;
    }
}
