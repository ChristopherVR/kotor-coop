using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace KotorCoop.Agent;

/// <summary>
/// A read-only <see cref="TextView"/> that draws each cell in the colour loaded with it
/// (<see cref="TextView.Load(List{List{Cell}})"/>). The stock view paints every cell of a
/// read-only view in its ReadOnly role colour and ignores per-cell attributes — reproduced
/// in isolation 2026-09-26 — so the agent window's coloured log needs this override.
/// Word wrap keeps each cell's colour, so a wrapped line stays one colour.
/// </summary>
#pragma warning disable CS0618 // TextView is marked superseded in 2.5; it is what this project ships on.
internal sealed class ColorTextView : TextView
{
    /// <summary>
    /// <see cref="TextView.Load(List{List{Cell}})"/>, made safe to call with FEWER lines than
    /// before. Load keeps the old cursor row, so shrinking a scrolled-to-the-bottom log (a
    /// search with few matches, or clearing it) threw ArgumentOutOfRangeException and killed
    /// the agent (live 2026-09-26, reproduced in isolation: 200 rows -> 1 row throws, 200 -> 0
    /// is safe). Loading nothing first resets the cursor to row 0.
    /// </summary>
    public void LoadRows(List<List<Cell>> rows)
    {
        Load(new List<List<Cell>>());
        Load(rows);
    }

    protected override void OnDrawReadOnlyColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (RowAttribute(line, idxCol) is { } attribute)
        {
            SetAttribute(attribute);
            return;
        }

        base.OnDrawReadOnlyColor(line, idxCol, idxRow);
    }

    protected override void OnDrawNormalColor(List<Cell> line, int idxCol, int idxRow)
    {
        if (RowAttribute(line, idxCol) is { } attribute)
        {
            SetAttribute(attribute);
            return;
        }

        base.OnDrawNormalColor(line, idxCol, idxRow);
    }

    /// <summary>The cell's own colour, else the nearest coloured cell in the same row: word
    /// wrap hands some continuation-row cells over without an attribute (live: the first few
    /// characters of a wrapped row drew white while the rest kept the line's colour).</summary>
    private static Terminal.Gui.Drawing.Attribute? RowAttribute(List<Cell> line, int idxCol)
    {
        if (idxCol < line.Count && line[idxCol].Attribute is { } own)
        {
            return own;
        }

        for (int i = Math.Min(idxCol, line.Count - 1) + 1; i < line.Count; i++)
        {
            if (line[i].Attribute is { } next)
            {
                return next;
            }
        }

        for (int i = Math.Min(idxCol, line.Count) - 1; i >= 0; i--)
        {
            if (line[i].Attribute is { } previous)
            {
                return previous;
            }
        }

        return null;
    }
}
#pragma warning restore CS0618
