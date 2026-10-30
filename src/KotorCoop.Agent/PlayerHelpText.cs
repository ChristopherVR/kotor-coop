namespace KotorCoop.Agent;

/// <summary>The default in-game chat overlay truncates each line at 60 characters.
/// Split before publishing so filenames and recovery instructions are not silently lost.</summary>
public static class PlayerHelpText
{
    public const int DefaultWidth = 60;

    public static IEnumerable<string> Wrap(string text, int width = DefaultWidth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        foreach (string paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            string remaining = paragraph.Trim();
            while (remaining.Length > width)
            {
                int split = remaining.LastIndexOf(' ', width, width + 1);
                if (split <= 0) split = width;
                yield return remaining[..split].TrimEnd();
                remaining = remaining[split..].TrimStart();
            }
            if (remaining.Length > 0) yield return remaining;
        }
    }
}
