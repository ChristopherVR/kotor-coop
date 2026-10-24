namespace KotorCoop.Plugin;

/// <summary>Places Co-op relative to the live menu, without replacing a mod's .gui files.
/// Rectangles and viewport dimensions must use the engine's screen coordinate space.</summary>
internal static class MenuColumnRule
{
    internal readonly record struct Extent(int Left, int Top, int Width, int Height)
    {
        public long Right => (long)Left + Width;
        public long Bottom => (long)Top + Height;
    }

    public const int ColumnTolerance = 4;
    public const int MinimumColumn = 3;

    /// <summary>Prefer below the dominant column, then above, then beside its last row.
    /// Never move another button, overlap one, or place Co-op outside the viewport.</summary>
    public static bool TryFindSlot(IReadOnlyList<Extent> buttons, int screenWidth, int screenHeight, out Extent slot)
    {
        slot = default;
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            return false;
        }

        // Offscreen controls and duplicate rectangles cannot manufacture a column.
        List<Extent> visible = buttons.Where(b => OnScreen(b, screenWidth, screenHeight)).Distinct().ToList();
        List<Extent> column = [];
        int alignment = 0;
        foreach (Extent candidate in visible)
        {
            // Prefer left alignment on a tie to preserve stock/HR Menus placement.
            for (int edge = 0; edge < 3; edge++)
            {
                List<Extent> group = visible.Where(b => Math.Abs(Anchor(b, edge) - Anchor(candidate, edge)) <= ColumnTolerance * 2).ToList();
                group.Sort(static (a, b) => a.Top.CompareTo(b.Top));
                if (group.Count <= column.Count || group.Count < MinimumColumn)
                {
                    continue;
                }

                // A stack has distinct, non-overlapping rows, not several controls on one row.
                bool stacked = true;
                for (int i = 1; i < group.Count; i++)
                {
                    stacked &= group[i - 1].Bottom <= group[i].Top;
                }
                if (stacked)
                {
                    column = group;
                    alignment = edge;
                }
            }
        }

        if (column.Count < MinimumColumn)
        {
            return false;
        }

        int height = MostCommon(column, static b => b.Height);
        int width = MostCommon(column, static b => b.Width);
        int pitch = int.MaxValue;
        for (int i = 1; i < column.Count; i++)
        {
            pitch = Math.Min(pitch, column[i].Top - column[i - 1].Top);
        }
        int gap = Math.Max(pitch - height, 0);
        Extent first = column[0], last = column[^1];
        long anchor = Anchor(first, alignment);
        long left = alignment switch { 1 => (anchor - width) / 2, 2 => anchor / 2 - width, _ => anchor / 2 };

        (long Left, long Top)[] candidates =
        [
            (left, last.Bottom + gap),
            (left, (long)first.Top - gap - height),
            (column.Max(static b => b.Right) + Math.Max(gap, ColumnTolerance), last.Top),
            ((long)column.Min(static b => b.Left) - Math.Max(gap, ColumnTolerance) - width, last.Top),
        ];
        foreach (var position in candidates)
        {
            if (position.Left < 0 || position.Top < 0 || position.Left + width > screenWidth || position.Top + height > screenHeight)
            {
                continue;
            }
            Extent proposed = new((int)position.Left, (int)position.Top, width, height);
            if (IsAvailable(proposed, buttons, screenWidth, screenHeight))
            {
                slot = proposed;
                return true;
            }
        }
        return false;
    }

    /// <summary>Also validates a mod's existing Co-op slot when no column can be inferred.</summary>
    public static bool IsAvailable(Extent slot, IReadOnlyList<Extent> buttons, int screenWidth, int screenHeight) =>
        OnScreen(slot, screenWidth, screenHeight) && !buttons.Any(b => b.Width > 0 && b.Height > 0
            && slot.Left < b.Right && slot.Right > b.Left && slot.Top < b.Bottom && slot.Bottom > b.Top);

    private static bool OnScreen(Extent b, int width, int height) =>
        b.Width > 0 && b.Height > 0 && b.Left >= 0 && b.Top >= 0 && b.Right <= width && b.Bottom <= height;

    // Doubled coordinates keep centre alignment exact for odd widths.
    private static long Anchor(Extent b, int edge) => edge switch
    {
        1 => (long)b.Left * 2 + b.Width,
        2 => b.Right * 2,
        _ => (long)b.Left * 2,
    };

    private static int MostCommon(List<Extent> column, Func<Extent, int> size) => column
        .GroupBy(size).OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key).First().Key;
}
