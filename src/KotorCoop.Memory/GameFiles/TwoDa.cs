using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// BioWare 2DA tables, both encodings the games ship:
///
///   ASCII  "2DA V2.0\n": a blank (or DEFAULT:) line, a whitespace-separated header row,
///     then data rows whose first token is the row label. Double quotes protect spaces.
///   Binary "2DA V2.b\n": TAB-terminated column labels then NUL; uint32 rowCount;
///     TAB-terminated row labels; rowCount*colCount uint16 offsets into the data block;
///     uint16 dataBlockSize; NUL-terminated cell strings.
///
/// Empty cells are the literal "****" in ASCII and an empty string in binary; both come
/// out of <see cref="Get(int, int)"/> as "" so callers never see the sentinel.
/// </summary>
public sealed class TwoDa
{
    private readonly List<string> _columns = [];
    private readonly List<string> _rowLabels = [];
    private readonly List<string[]> _rows = [];
    private readonly Dictionary<string, int> _columnIndex = [];

    public int RowCount => _rows.Count;

    /// <summary>Column names in file order (the row-label pseudo-column excluded).</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>Row labels in file order. Usually "0","1",... but nothing enforces it.</summary>
    public IReadOnlyList<string> RowLabels => _rowLabels;

    /// <summary>Parses either 2DA encoding. Throws <see cref="InvalidDataException"/> on a
    /// wrong magic or a truncated table.</summary>
    public static TwoDa Parse(byte[] data) => new(data);

    private TwoDa(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        string magic = data.Length >= 8 ? GameFileText.Decode(data.AsSpan(0, 8)) : string.Empty;

        if (magic == "2DA V2.b")
        {
            ParseBinary(data);
        }
        else if (magic == "2DA V2.0")
        {
            ParseAscii(data);
        }
        else
        {
            throw new InvalidDataException($"2DA: bad magic '{magic}'");
        }

        // Case-insensitive on lookup, first occurrence wins — matching the engine's own
        // column resolution and the Python reference's lower() index.
        for (int i = 0; i < _columns.Count; i++)
        {
            _columnIndex.TryAdd(_columns[i].ToLowerInvariant(), i);
        }
    }

    // -- access ------------------------------------------------------------------

    /// <summary>The cell at (row, column name); "" for an empty cell. Throws
    /// <see cref="KeyNotFoundException"/> for an unknown column, so a misspelt column
    /// name fails loudly instead of reading as "every cell is empty".</summary>
    public string Get(int row, string column) => _rows[row][ColumnIndexOf(column)];

    public string Get(int row, int columnIndex) => _rows[row][columnIndex];

    public bool TryGetColumnIndex(string column, out int index) =>
        _columnIndex.TryGetValue(column.ToLowerInvariant(), out index);

    private int ColumnIndexOf(string column) =>
        _columnIndex.TryGetValue(column.ToLowerInvariant(), out int index)
            ? index
            : throw new KeyNotFoundException(
                $"2DA: no column '{column}' (have {string.Join(", ", _columns)})");

    // -- binary ------------------------------------------------------------------

    private void ParseBinary(byte[] data)
    {
        int p = 9; // past "2DA V2.b\n"

        int headerEnd = IndexOf(data, (byte)0, p);
        foreach (string column in GameFileText.Decode(data.AsSpan(p, headerEnd - p)).Split('\t'))
        {
            if (column.Length > 0)
            {
                _columns.Add(column);
            }
        }

        p = headerEnd + 1;
        int rowCount = checked((int)ReadU32(data, p));
        p += 4;

        for (int i = 0; i < rowCount; i++)
        {
            int e = IndexOf(data, (byte)'\t', p);
            _rowLabels.Add(GameFileText.Decode(data.AsSpan(p, e - p)));
            p = e + 1;
        }

        int columnCount = _columns.Count;
        int cellCount = rowCount * columnCount;

        ushort[] offsets = new ushort[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            offsets[i] = ReadU16(data, p + (2 * i));
        }

        p += 2 * cellCount;
        p += 2; // uint16 dataBlockSize — the offsets already delimit every cell
        int block = p;

        for (int r = 0; r < rowCount; r++)
        {
            string[] row = new string[columnCount];

            for (int c = 0; c < columnCount; c++)
            {
                int start = block + offsets[(r * columnCount) + c];
                int end = IndexOf(data, (byte)0, start);
                row[c] = GameFileText.Decode(data.AsSpan(start, end - start));
            }

            _rows.Add(row);
        }
    }

    // -- ASCII -------------------------------------------------------------------

    private void ParseAscii(byte[] data)
    {
        string[] lines = GameFileText.Decode(data).Replace("\r\n", "\n").Split('\n');

        // Line 0 is the magic; then optional blank lines and an optional DEFAULT: line
        // precede the header row.
        int i = 1;
        while (i < lines.Length && lines[i].Trim().Length == 0)
        {
            i++;
        }

        if (i < lines.Length && lines[i].StartsWith("DEFAULT:", StringComparison.OrdinalIgnoreCase))
        {
            i++;
            while (i < lines.Length && lines[i].Trim().Length == 0)
            {
                i++;
            }
        }

        if (i >= lines.Length)
        {
            throw new InvalidDataException("2DA: ASCII table has no header row");
        }

        _columns.AddRange(SplitAsciiRow(lines[i]));

        for (int line = i + 1; line < lines.Length; line++)
        {
            if (lines[line].Trim().Length == 0)
            {
                continue;
            }

            List<string> tokens = SplitAsciiRow(lines[line]);
            if (tokens.Count == 0)
            {
                continue;
            }

            _rowLabels.Add(tokens[0]);

            // Short rows are padded and long rows truncated to the column count, and the
            // "****" empty-cell sentinel becomes "" — exactly the reference behaviour.
            string[] row = new string[_columns.Count];
            for (int c = 0; c < _columns.Count; c++)
            {
                string value = c + 1 < tokens.Count ? tokens[c + 1] : string.Empty;
                row[c] = value == "****" ? string.Empty : value;
            }

            _rows.Add(row);
        }
    }

    /// <summary>Whitespace-splits one ASCII 2DA line, honouring double quotes: a quoted
    /// cell may contain spaces, and the quotes themselves are not part of the value.</summary>
    private static List<string> SplitAsciiRow(string line)
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        bool inQuotes = false;

        foreach (char ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if ((ch == ' ' || ch == '\t') && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    // -- bounds-checked reads ----------------------------------------------------

    private static int IndexOf(byte[] data, byte value, int start)
    {
        if (start < 0 || start > data.Length)
        {
            throw new InvalidDataException($"2DA: scan start 0x{start:X} is outside the {data.Length}-byte file");
        }

        int at = Array.IndexOf(data, value, start);
        return at >= 0
            ? at
            : throw new InvalidDataException($"2DA: unterminated string at 0x{start:X}");
    }

    private static uint ReadU32(byte[] data, int offset) =>
        offset >= 0 && offset + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4))
            : throw new InvalidDataException($"2DA: truncated at 0x{offset:X}");

    private static ushort ReadU16(byte[] data, int offset) =>
        offset >= 0 && offset + 2 <= data.Length
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))
            : throw new InvalidDataException($"2DA: truncated at 0x{offset:X}");
}
