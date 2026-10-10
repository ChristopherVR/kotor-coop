namespace KotorCoop.Plugin;

/// <summary>
/// Where the main menu's Co-op button goes. It is the menu's dormant BTN_WARP, and its .gui
/// position only lines up with the real buttons in the stock 800x600 layout ("one row below
/// Quit"). Menu mods that lay the menu out for the player's resolution (HR Menus in the K1 Full
/// build, 2026-10-10) move the real buttons and leave BTN_WARP where it was: at 1920x1080 it sat
/// at the far left, a different size, well away from the column. So the slot is worked out from
/// the live buttons instead of the file: the column is the largest group of visible buttons
/// sharing a left edge, and the Co-op button takes that column's width and usual height one row
/// below its lowest button. Pure, so the tests compile it in.
/// </summary>
internal static class MenuColumnRule
{
    /// <summary>A screen rectangle in pixels, the engine's CSWGuiExtent order.</summary>
    internal readonly record struct Extent(int Left, int Top, int Width, int Height)
    {
        public int Bottom => Top + Height;
    }

    /// <summary>Buttons whose left edges differ by no more than this share a column (layouts
    /// hand-tuned per resolution are a pixel or two off).</summary>
    public const int ColumnTolerance = 4;

    /// <summary>A column needs at least this many buttons to be the menu's button stack.</summary>
    public const int MinimumColumn = 3;

    /// <summary>
    /// The Co-op button's slot under the menu's button column, or false when no column can be
    /// found or the slot would leave the screen (the caller then leaves the .gui position alone).
    /// </summary>
    public static bool TrySlotBelowColumn(IReadOnlyList<Extent> buttons, int screenHeight, out Extent slot)
    {
        slot = default;

        List<Extent> column = [];
        foreach (Extent candidate in buttons)
        {
            if (candidate.Width <= 0 || candidate.Height <= 0)
            {
                continue;
            }

            List<Extent> group = [];
            foreach (Extent other in buttons)
            {
                if (other.Width > 0 && other.Height > 0 && Math.Abs(other.Left - candidate.Left) <= ColumnTolerance)
                {
                    group.Add(other);
                }
            }

            if (group.Count > column.Count)
            {
                column = group;
            }
        }

        if (column.Count < MinimumColumn)
        {
            return false;
        }

        column.Sort(static (a, b) => a.Top.CompareTo(b.Top));

        // The row pitch is the smallest gap between consecutive tops, so a taller last button
        // (Quit is drawn taller in some layouts) does not stretch it.
        int pitch = int.MaxValue;
        for (int i = 1; i < column.Count; i++)
        {
            int gap = column[i].Top - column[i - 1].Top;
            if (gap > 0 && gap < pitch)
            {
                pitch = gap;
            }
        }

        if (pitch == int.MaxValue)
        {
            return false;
        }

        // The usual button size: the most common one, smallest on a tie.
        int height = MostCommon(column, static b => b.Height);
        int width = MostCommon(column, static b => b.Width);

        Extent lowest = column[^1];
        int gapBelow = Math.Max(pitch - height, 0);
        int top = lowest.Bottom + gapBelow;

        if (screenHeight > 0 && top + height > screenHeight)
        {
            return false;
        }

        slot = new Extent(column[0].Left, top, width, height);
        return true;
    }

    private static int MostCommon(List<Extent> column, Func<Extent, int> size) => column
        .GroupBy(size)
        .OrderByDescending(static g => g.Count())
        .ThenBy(static g => g.Key)
        .First().Key;
}
