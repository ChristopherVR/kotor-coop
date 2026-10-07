using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// The file half of "[Game Options] EnableCheats=1" in swkotor.ini: keeps a one-time copy of
/// the player's original (<c>swkotor.ini.kcoop-orig</c>, which the installer's uninstall
/// restores EnableCheats from), edits the text without disturbing line endings or encoding,
/// and swaps the result in through a temp file so a crash mid-write cannot leave a truncated
/// ini behind. Pure file/text logic, no engine access, so it is unit tested.
/// </summary>
internal static class IniCheatsFile
{
    public const string BackupSuffix = ".kcoop-orig";

    /// <summary>The outcome, for the plugin log.</summary>
    public enum Result { AlreadyEnabled, Enabled }

    public static Result EnableCheats(string iniPath)
    {
        byte[] original = File.ReadAllBytes(iniPath);
        Encoding encoding = DetectEncoding(original, out int preamble);
        string text = encoding.GetString(original, preamble, original.Length - preamble);

        string? edited = SetEnableCheats(text);
        if (edited is null)
        {
            return Result.AlreadyEnabled;
        }

        // Only now (we are about to modify) take the one-time backup of the untouched file.
        string backup = iniPath + BackupSuffix;
        if (!File.Exists(backup))
        {
            File.Copy(iniPath, backup, overwrite: false);
        }

        byte[] body = encoding.GetBytes(edited);
        byte[] output = new byte[preamble + body.Length];
        Array.Copy(original, output, preamble);
        body.CopyTo(output, preamble);

        string temp = iniPath + ".kcoop-tmp";
        try
        {
            File.WriteAllBytes(temp, output);
            File.Replace(temp, iniPath, null);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }

        return Result.Enabled;
    }

    /// <summary>The edited ini text, or null when EnableCheats is already 1. Line endings
    /// of the existing text are kept (CRLF unless the file uses only bare LFs).</summary>
    public static string? SetEnableCheats(string text)
    {
        bool crlf = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n');
        string eol = crlf ? "\r\n" : "\n";
        bool trailingNewline = text.EndsWith('\n');
        List<string> lines = [.. text.Split('\n')];
        if (trailingNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        int section = -1;
        int keyLine = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (trimmed.Equals("[Game Options]", StringComparison.OrdinalIgnoreCase))
                {
                    section = i;
                }
                else if (section >= 0)
                {
                    break; // left the section without finding the key
                }
            }
            else if (section >= 0 && trimmed.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase))
            {
                keyLine = i;
                break;
            }
        }

        if (keyLine >= 0)
        {
            if (lines[keyLine].Trim().EndsWith('1'))
            {
                return null;
            }

            lines[keyLine] = "EnableCheats=1";
        }
        else if (section >= 0)
        {
            lines.Insert(section + 1, "EnableCheats=1");
        }
        else
        {
            lines.Add("[Game Options]");
            lines.Add("EnableCheats=1");
        }

        return string.Join(eol, lines) + (trailingNewline ? eol : string.Empty);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preamble)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preamble = 3;
            return new UTF8Encoding(false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preamble = 2;
            return new UnicodeEncoding(false, false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preamble = 2;
            return new UnicodeEncoding(true, false);
        }

        // No BOM: Latin-1 round-trips every byte exactly, so non-ASCII bytes survive untouched.
        preamble = 0;
        return Encoding.Latin1;
    }
}
