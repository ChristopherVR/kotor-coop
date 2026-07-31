using System.Buffers.Binary;
using System.Text;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Builder for binary "2DA V2.b" tables — the write side of <see cref="TwoDa"/>. Seed it
/// from a parsed table (the merge case: read the user's file, append, write back) or from
/// a column list (the synthetic case: fixtures and tests), then <see cref="ToBytes"/>
/// emits the layout <see cref="TwoDa"/> parses:
///
///   "2DA V2.b\n"; each column label TAB-terminated, then NUL; uint32 rowCount; each row
///   label TAB-terminated; rowCount*colCount uint16 offsets into the data block; uint16
///   dataBlockSize; NUL-terminated cell strings.
///
/// Two format subtleties the writer must honour:
///
///   Cell strings are deduplicated. The offsets are uint16, so the data block cannot
///   exceed 64 KB — and the stock appearance.2da alone has 509x80 cells whose text runs
///   far past that undeduplicated. BioWare's own writer shares one copy per distinct
///   string (stock repute.2da: 441 cells, 24 strings); this writer does the same and
///   throws if the deduplicated block still cannot fit, rather than emitting offsets
///   that silently wrap.
///
///   Empty cells are a real value. The binary encoding has no "****" sentinel (that is
///   the ASCII form only); an empty cell is a zero-length NUL-terminated string in the
///   block, which every empty cell shares via deduplication. <see cref="TwoDa"/> reads
///   both encodings' empties back as "", so "" in equals "" out.
///
/// The guarantee is read-back fidelity — Parse(ToBytes()) yields the same columns, row
/// labels and cells — not byte-identity with BioWare's writer (its string-pool order is
/// theirs, ours is first-occurrence).
/// </summary>
public sealed class TwoDaWriter
{
    private readonly List<string> _columns = [];
    private readonly List<string> _rowLabels = [];
    private readonly List<List<string>> _rows = [];

    public int RowCount => _rows.Count;

    /// <summary>Column names in file order, including any added by <see cref="AddColumn"/>.</summary>
    public IReadOnlyList<string> Columns => _columns;

    /// <summary>Starts from a parsed table, copying every column, row label and cell —
    /// the merge path: nothing of the user's file is dropped, appends land after it.</summary>
    public TwoDaWriter(TwoDa source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _columns.AddRange(source.Columns);

        for (int r = 0; r < source.RowCount; r++)
        {
            _rowLabels.Add(source.RowLabels[r]);

            List<string> row = new(_columns.Count);
            for (int c = 0; c < _columns.Count; c++)
            {
                row.Add(source.Get(r, c));
            }

            _rows.Add(row);
        }
    }

    /// <summary>Starts an empty table with the given columns — the fixture path.</summary>
    public TwoDaWriter(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns.AddRange(columns);

        if (_columns.Count == 0)
        {
            throw new ArgumentException("a 2DA needs at least one column", nameof(columns));
        }
    }

    /// <summary>Parse-write convenience for round-tripping an existing file unchanged.</summary>
    public static byte[] Write(TwoDa source) => new TwoDaWriter(source).ToBytes();

    /// <summary>Appends a row and returns its index. One cell per current column, in
    /// column order — adding columns first and rows second keeps the arithmetic honest.</summary>
    public int AddRow(string rowLabel, IReadOnlyList<string> cells)
    {
        ArgumentNullException.ThrowIfNull(rowLabel);
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count != _columns.Count)
        {
            throw new ArgumentException(
                $"2DA row has {cells.Count} cells for {_columns.Count} columns", nameof(cells));
        }

        _rowLabels.Add(rowLabel);
        _rows.Add([.. cells]);
        return _rows.Count - 1;
    }

    /// <summary>Appends a column, filling every existing row with <paramref name="fill"/>,
    /// and returns its index. Individual rows are adjusted afterwards with
    /// <see cref="SetCell"/>.</summary>
    public int AddColumn(string label, string fill)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        ArgumentNullException.ThrowIfNull(fill);

        _columns.Add(label);

        foreach (List<string> row in _rows)
        {
            row.Add(fill);
        }

        return _columns.Count - 1;
    }

    /// <summary>Overwrites one cell, addressing the column by name — case-insensitive,
    /// first occurrence wins, like <see cref="TwoDa"/>'s own lookup. Throws for an
    /// unknown column so a misspelt merge fails loudly instead of silently no-opping.</summary>
    public void SetCell(int row, string column, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _rows[row][ColumnIndexOf(column)] = value;
    }

    private int ColumnIndexOf(string column)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            if (string.Equals(_columns[i], column, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new KeyNotFoundException(
            $"2DA: no column '{column}' (have {string.Join(", ", _columns)})");
    }

    public byte[] ToBytes()
    {
        // The framing characters cannot appear inside what they frame: a TAB in a label
        // or a NUL anywhere would silently shift every later field on read-back. An
        // empty column label is rejected too — the reader drops empty header tokens, so
        // it would come back as one column fewer and misalign every cell after it.
        foreach (string column in _columns)
        {
            ArgumentException.ThrowIfNullOrEmpty(column);
            RejectFramingBytes(column, "column label");
        }

        foreach (string label in _rowLabels)
        {
            RejectFramingBytes(label, "row label");
        }

        // Deduplicated string pool, first occurrence first. Every empty cell collapses
        // onto one shared zero-length entry like any other repeated value.
        Dictionary<string, ushort> offsetOf = new(StringComparer.Ordinal);
        using MemoryStream block = new();
        int cellCount = _rows.Count * _columns.Count;
        ushort[] offsets = new ushort[cellCount];
        int cell = 0;

        foreach (List<string> row in _rows)
        {
            foreach (string value in row)
            {
                if (value.Contains('\0'))
                {
                    throw new ArgumentException($"2DA cell '{value}' contains a NUL byte");
                }

                if (!offsetOf.TryGetValue(value, out ushort at))
                {
                    long start = block.Length;
                    byte[] text = Encoding.Latin1.GetBytes(value);

                    // ushort.MaxValue, not +1: the dataBlockSize field itself is uint16,
                    // so a block of exactly 65536 bytes is as unrepresentable as its
                    // offsets. Deduplication makes this unreachable for any real table.
                    if (start + text.Length + 1 > ushort.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"2DA data block exceeds {ushort.MaxValue} bytes even deduplicated; "
                            + "the uint16 cell offsets cannot address it");
                    }

                    at = (ushort)start;
                    offsetOf.Add(value, at);
                    block.Write(text);
                    block.WriteByte(0);
                }

                offsets[cell++] = at;
            }
        }

        using MemoryStream output = new();
        output.Write(Encoding.ASCII.GetBytes("2DA V2.b\n"));

        foreach (string column in _columns)
        {
            output.Write(Encoding.Latin1.GetBytes(column));
            output.WriteByte((byte)'\t');
        }

        output.WriteByte(0);
        WriteU32(output, (uint)_rows.Count);

        foreach (string label in _rowLabels)
        {
            output.Write(Encoding.Latin1.GetBytes(label));
            output.WriteByte((byte)'\t');
        }

        foreach (ushort offset in offsets)
        {
            WriteU16(output, offset);
        }

        WriteU16(output, (ushort)block.Length);
        block.WriteTo(output);
        return output.ToArray();
    }

    private static void RejectFramingBytes(string value, string what)
    {
        if (value.Contains('\t') || value.Contains('\0'))
        {
            throw new ArgumentException($"2DA {what} '{value}' contains a TAB or NUL framing byte");
        }
    }

    private static void WriteU32(MemoryStream stream, uint value)
    {
        Span<byte> cell = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cell, value);
        stream.Write(cell);
    }

    private static void WriteU16(MemoryStream stream, ushort value)
    {
        Span<byte> cell = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(cell, value);
        stream.Write(cell);
    }
}
