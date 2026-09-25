namespace KotorCoop.Memory;

/// <summary>
/// The one dedicated save slot every co-op-driven save on the JOINER's install uses — periodic
/// character capture, pre-disconnect capture, the chargen capture for a first-time joiner, and
/// (mirrored, see <c>WorldAdoption</c>) the staged adopted world — so the player's own
/// <c>000000 - QUICKSAVE</c> is never overwritten by the mod. See docs/research/session-save.md's
/// 2026-09-25 entry and docs/research/key-items-and-solo.md §4 for the problem this fixes.
///
/// Pure naming/lookup only; the actual directory moves live in <see cref="CoopSaveRedirect"/> —
/// split the same way <c>NamedSaveRule</c> (naming) and <c>CoopCommandDispatcher.CopyQuickSaveAs</c>
/// (the move) are split elsewhere in this tree: naming is trivially unit-tested with no
/// filesystem, a move is not.
///
/// Lives in <c>KotorCoop.Memory</c>, not <c>KotorCoop.Session</c> (where the equivalent
/// <c>NamedSaveRule</c> lives), and duplicates a handful of its lines rather than referencing it:
/// <c>KotorCoop.Plugin</c> — the NativeAOT DLL that also needs this rule, for the chargen capture
/// (<c>NewCharacterCaptureController</c>) — cannot reference <c>KotorCoop.Session</c> at all; it
/// only ever shares code by source-linking individual <c>KotorCoop.Memory</c> files (see
/// <c>WineDetection.cs</c>'s own doc comment, and this project's <c>.csproj</c>).
/// </summary>
public static class CoopSaveSlot
{
    /// <summary>The name part every dedicated co-op save folder carries, matched
    /// case-insensitively and independent of its numeric prefix — see
    /// <see cref="ResolveFolderName"/> for why the prefix is never fixed.</summary>
    public const string SlotLabel = "Co-op";

    /// <summary>Longest name kept, mirroring <c>NamedSaveRule.MaxNameLength</c>.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Reduces free text to the same folder-safe alphabet <c>NamedSaveRule.SanitizeName</c>
    /// uses: letters, digits, space, dash and underscore, single spaces, trimmed. Returns
    /// <see cref="SlotLabel"/> unchanged when <paramref name="raw"/> is null/blank — a co-op
    /// save always has SOME name, even with no host name available to fold in.</summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return SlotLabel;
        }

        System.Text.StringBuilder sb = new(raw.Length);
        bool lastWasSpace = true;
        foreach (char c in raw)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '(' || c == ')')
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
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd();
        }

        return name.Length == 0 ? SlotLabel : name;
    }

    /// <summary>The name part of a save folder (<c>000012 - foo</c> gives <c>foo</c>), or the
    /// whole folder name when it has no <c>NNNNNN - </c> prefix. Mirrors
    /// <c>NamedSaveRule.NameOf</c> exactly.</summary>
    public static string NameOf(string folderName)
    {
        int dash = folderName.IndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 && dash <= 6 ? folderName[(dash + 3)..] : folderName;
    }

    /// <summary>True when <paramref name="folderName"/> is the dedicated co-op save folder:
    /// its name-part is <see cref="SlotLabel"/>, exactly (case-insensitive) — never merely a
    /// prefix or substring match, so a player's own named save called e.g. "Co-op run" is not
    /// mistaken for it.</summary>
    public static bool IsCoopSlot(string folderName) =>
        string.Equals(NameOf(folderName), SlotLabel, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder a numbered save lives in: <c>000012 - name</c>. Mirrors
    /// <c>NamedSaveRule.FolderName</c> exactly.</summary>
    public static string FolderName(int index, string sanitisedName) => $"{index:D6} - {sanitisedName}";

    /// <summary>The next free save number: one above the highest leading number already used by
    /// ANY folder (named saves, the co-op slot, AUTOSAVE at 1). Mirrors
    /// <c>NamedSaveRule.NextIndex</c> exactly, so the co-op slot's own first allocation can never
    /// collide with a player's own named save and vice versa — both draw from the same counter.</summary>
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

    /// <summary>
    /// The dedicated co-op save's folder name, given every folder name already under
    /// <c>saves\</c>.
    ///
    /// If one already exists — ANY numeric prefix, found by <see cref="IsCoopSlot"/> — it is
    /// returned UNCHANGED: an install's co-op slot keeps whatever number it was first created
    /// at, exactly the way <c>000001 - AUTOSAVE</c> keeps its number forever. Otherwise a fresh
    /// number is allocated with the identical rule a player's own <c>save &lt;name&gt;</c> would
    /// use (<see cref="NextIndex"/>), so the first-ever co-op save on an install can never
    /// collide with an existing named save at any number, including one a pre-existing install
    /// happens to already have sitting at whatever number this feature would otherwise have
    /// picked.
    /// </summary>
    public static string ResolveFolderName(IEnumerable<string> existingFolderNames)
    {
        ArgumentNullException.ThrowIfNull(existingFolderNames);

        string[] existing = [.. existingFolderNames];
        string? found = Array.Find(existing, IsCoopSlot);
        if (found is not null)
        {
            return found;
        }

        return FolderName(NextIndex(existing), SlotLabel);
    }
}
