using System.Buffers.Binary;
using System.Text;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Builder for BioWare GFF V3.2 files — the write side of <see cref="Gff"/>. Populate
/// <see cref="Root"/> with typed setters, then <see cref="ToBytes"/> serialises the
/// header, struct/field/label tables, field-data block, field-indices block and
/// list-indices block in the exact layout the reader decodes. The guarantee is
/// read-back fidelity (<c>Gff.Parse(writer.ToBytes())</c> yields identical labels,
/// types and values), not byte-identity with BioWare's own writer: field order follows
/// insertion order, labels are deduplicated by exact match, and field data is packed
/// without padding.
/// </summary>
public sealed class GffWriter
{
    private readonly string _fileType;

    /// <summary>The file's top-level struct. Conventionally id 0xFFFFFFFF.</summary>
    public GffWriterStruct Root { get; } = new(0xFFFFFFFF);

    /// <summary>Creates a writer for the given four-character type tag ("UTC ", "GIT ").
    /// Shorter tags are space padded to four characters, as the format stores them.</summary>
    public GffWriter(string fileType)
    {
        ArgumentNullException.ThrowIfNull(fileType);

        if (fileType.Length > 4)
        {
            throw new ArgumentException($"GFF file type '{fileType}' exceeds 4 characters", nameof(fileType));
        }

        _fileType = fileType.PadRight(4);
    }

    public byte[] ToBytes()
    {
        // Struct indices are assigned in pre-order (root first, then children in field
        // order), so struct 0 is always the root — the only index the reader hardwires.
        List<GffWriterStruct> structs = [];
        Dictionary<GffWriterStruct, uint> indexOf = new(ReferenceEqualityComparer.Instance);
        Collect(Root, structs, indexOf);

        List<string> labels = [];
        Dictionary<string, uint> labelIndex = new(StringComparer.Ordinal);

        using MemoryStream structTable = new();
        using MemoryStream fieldTable = new();
        using MemoryStream fieldData = new();
        using MemoryStream fieldIndices = new();
        using MemoryStream listIndices = new();

        uint fieldCount = 0;

        foreach (GffWriterStruct s in structs)
        {
            // Each struct's fields become consecutive field records, so a multi-field
            // struct's index run is just [first, first + n).
            uint first = fieldCount;
            int n = s.Fields.Count;

            uint dataOrOffset;
            if (n == 0)
            {
                dataOrOffset = 0;
            }
            else if (n == 1)
            {
                dataOrOffset = first; // single field: the index is stored inline
            }
            else
            {
                dataOrOffset = (uint)fieldIndices.Length; // byte offset into field indices
                for (uint i = 0; i < n; i++)
                {
                    WriteU32(fieldIndices, first + i);
                }
            }

            WriteU32(structTable, s.StructId);
            WriteU32(structTable, dataOrOffset);
            WriteU32(structTable, (uint)n);

            foreach ((string label, GffFieldType type, object value) in s.Fields)
            {
                if (!labelIndex.TryGetValue(label, out uint li))
                {
                    li = (uint)labels.Count;
                    labels.Add(label);
                    labelIndex.Add(label, li);
                }

                WriteU32(fieldTable, (uint)type);
                WriteU32(fieldTable, li);
                WriteU32(fieldTable, FieldCell(type, value, indexOf, fieldData, listIndices));
                fieldCount++;
            }
        }

        // Sections follow the header back to back in the canonical order.
        uint structOffset = 56;
        uint fieldOffset = structOffset + (uint)structTable.Length;
        uint labelOffset = fieldOffset + (uint)fieldTable.Length;
        uint fieldDataOffset = labelOffset + (uint)(16 * labels.Count);
        uint fieldIndicesOffset = fieldDataOffset + (uint)fieldData.Length;
        uint listIndicesOffset = fieldIndicesOffset + (uint)fieldIndices.Length;

        byte[] result = new byte[listIndicesOffset + (uint)listIndices.Length];
        Span<byte> span = result;

        Encoding.Latin1.GetBytes(_fileType).CopyTo(span);
        Encoding.ASCII.GetBytes("V3.2").CopyTo(span[4..]);
        WriteU32(span, 8, structOffset);
        WriteU32(span, 12, (uint)structs.Count);
        WriteU32(span, 16, fieldOffset);
        WriteU32(span, 20, fieldCount);
        WriteU32(span, 24, labelOffset);
        WriteU32(span, 28, (uint)labels.Count);
        WriteU32(span, 32, fieldDataOffset);
        WriteU32(span, 36, (uint)fieldData.Length);
        WriteU32(span, 40, fieldIndicesOffset);
        WriteU32(span, 44, (uint)fieldIndices.Length);
        WriteU32(span, 48, listIndicesOffset);
        WriteU32(span, 52, (uint)listIndices.Length);

        structTable.ToArray().CopyTo(span[(int)structOffset..]);
        fieldTable.ToArray().CopyTo(span[(int)fieldOffset..]);

        for (int i = 0; i < labels.Count; i++)
        {
            // 16-byte NUL-padded cells; the array is zero-initialised so only the text
            // needs copying.
            Encoding.Latin1.GetBytes(labels[i]).CopyTo(span[((int)labelOffset + (16 * i))..]);
        }

        fieldData.ToArray().CopyTo(span[(int)fieldDataOffset..]);
        fieldIndices.ToArray().CopyTo(span[(int)fieldIndicesOffset..]);
        listIndices.ToArray().CopyTo(span[(int)listIndicesOffset..]);

        return result;
    }

    private static void Collect(
        GffWriterStruct s, List<GffWriterStruct> structs, Dictionary<GffWriterStruct, uint> indexOf)
    {
        // A struct reached twice (shared or cyclic) keeps its first index; the recursion
        // stops here either way.
        if (!indexOf.TryAdd(s, (uint)structs.Count))
        {
            return;
        }

        structs.Add(s);

        foreach ((_, GffFieldType type, object value) in s.Fields)
        {
            if (type == GffFieldType.Struct)
            {
                Collect((GffWriterStruct)value, structs, indexOf);
            }
            else if (type == GffFieldType.List)
            {
                foreach (GffWriterStruct member in ((GffWriterList)value).Members)
                {
                    Collect(member, structs, indexOf);
                }
            }
        }
    }

    /// <summary>The 4-byte dataOrDataOffset cell for one field record, appending to the
    /// field-data or list-indices block when the type is not stored inline.</summary>
    private static uint FieldCell(
        GffFieldType type,
        object value,
        Dictionary<GffWriterStruct, uint> indexOf,
        MemoryStream fieldData,
        MemoryStream listIndices)
    {
        switch (type)
        {
            // Simple types: the value IS the cell, masked to width like the reader's math.
            case GffFieldType.Byte:
                return (byte)value;
            case GffFieldType.Char:
                return (byte)(sbyte)value;
            case GffFieldType.Word:
                return (ushort)value;
            case GffFieldType.Short:
                return (ushort)(short)value;
            case GffFieldType.Dword:
                return (uint)value;
            case GffFieldType.Int:
                return unchecked((uint)(int)value);
            case GffFieldType.Float:
                return BitConverter.SingleToUInt32Bits((float)value);
            case GffFieldType.Struct:
                return indexOf[(GffWriterStruct)value];

            case GffFieldType.List:
            {
                uint offset = (uint)listIndices.Length;
                IReadOnlyList<GffWriterStruct> members = ((GffWriterList)value).Members;

                WriteU32(listIndices, (uint)members.Count);
                foreach (GffWriterStruct member in members)
                {
                    WriteU32(listIndices, indexOf[member]);
                }

                return offset;
            }
        }

        // Everything else is appended to the field-data block; the cell is its offset.
        uint at = (uint)fieldData.Length;

        switch (type)
        {
            case GffFieldType.Dword64:
                WriteU64(fieldData, (ulong)value);
                break;
            case GffFieldType.Int64:
                WriteU64(fieldData, unchecked((ulong)(long)value));
                break;
            case GffFieldType.Double:
                WriteU64(fieldData, BitConverter.DoubleToUInt64Bits((double)value));
                break;

            case GffFieldType.ExoString:
            {
                byte[] text = Encoding.Latin1.GetBytes((string)value);
                WriteU32(fieldData, (uint)text.Length);
                fieldData.Write(text);
                break;
            }

            case GffFieldType.ResRef:
            {
                byte[] text = Encoding.Latin1.GetBytes((string)value);
                fieldData.WriteByte((byte)text.Length);
                fieldData.Write(text);
                break;
            }

            case GffFieldType.ExoLocString:
            {
                (int strRef, IReadOnlyList<(int Id, string Text)> substrings) =
                    ((int, IReadOnlyList<(int, string)>))value;

                byte[][] encoded = [.. substrings.Select(s => Encoding.Latin1.GetBytes(s.Text))];

                // totalSize covers everything after itself: strref + count + substrings.
                uint totalSize = 8 + (uint)encoded.Sum(e => 8 + e.Length);
                WriteU32(fieldData, totalSize);
                WriteU32(fieldData, strRef < 0 ? 0xFFFFFFFF : (uint)strRef);
                WriteU32(fieldData, (uint)encoded.Length);

                for (int i = 0; i < encoded.Length; i++)
                {
                    WriteU32(fieldData, (uint)substrings[i].Id);
                    WriteU32(fieldData, (uint)encoded[i].Length);
                    fieldData.Write(encoded[i]);
                }

                break;
            }

            case GffFieldType.Void:
            {
                byte[] blob = (byte[])value;
                WriteU32(fieldData, (uint)blob.Length);
                fieldData.Write(blob);
                break;
            }

            case GffFieldType.StrRef:
                // A size dword (always 4) precedes the actual reference.
                WriteU32(fieldData, 4);
                WriteU32(fieldData, (uint)value);
                break;

            default:
                throw new NotSupportedException($"GFF writer: field type {type} is not supported");
        }

        return at;
    }

    private static void WriteU32(MemoryStream stream, uint value)
    {
        Span<byte> cell = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(cell, value);
        stream.Write(cell);
    }

    private static void WriteU64(MemoryStream stream, ulong value)
    {
        Span<byte> cell = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(cell, value);
        stream.Write(cell);
    }

    private static void WriteU32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
}

/// <summary>
/// One struct being built. Setters replace an existing field with the same label in
/// place (a struct cannot hold two fields with one name) and return <c>this</c> for
/// chaining; <see cref="AddStruct"/> and <see cref="AddList"/> return the new child
/// instead, since that is what the caller populates next.
/// </summary>
public sealed class GffWriterStruct
{
    internal List<(string Label, GffFieldType Type, object Value)> Fields { get; } = [];

    /// <summary>The struct type id written to the file. List members carry whatever id
    /// the game's own blueprints use for that list (0 for skills, 1 for feats, ...).</summary>
    public uint StructId { get; }

    internal GffWriterStruct(uint structId) => StructId = structId;

    public GffWriterStruct SetByte(string label, byte value) => Set(label, GffFieldType.Byte, value);

    public GffWriterStruct SetChar(string label, sbyte value) => Set(label, GffFieldType.Char, value);

    public GffWriterStruct SetWord(string label, ushort value) => Set(label, GffFieldType.Word, value);

    public GffWriterStruct SetShort(string label, short value) => Set(label, GffFieldType.Short, value);

    public GffWriterStruct SetDword(string label, uint value) => Set(label, GffFieldType.Dword, value);

    public GffWriterStruct SetInt(string label, int value) => Set(label, GffFieldType.Int, value);

    public GffWriterStruct SetDword64(string label, ulong value) => Set(label, GffFieldType.Dword64, value);

    public GffWriterStruct SetInt64(string label, long value) => Set(label, GffFieldType.Int64, value);

    public GffWriterStruct SetFloat(string label, float value) => Set(label, GffFieldType.Float, value);

    public GffWriterStruct SetDouble(string label, double value) => Set(label, GffFieldType.Double, value);

    /// <summary>A CExoString field.</summary>
    public GffWriterStruct SetString(string label, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Set(label, GffFieldType.ExoString, value);
    }

    /// <summary>A ResRef field. The engine caps resrefs at 16 characters; longer values
    /// are rejected here rather than silently shipped in a file the game would mangle.</summary>
    public GffWriterStruct SetResRef(string label, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > 16)
        {
            throw new ArgumentException($"ResRef '{value}' exceeds 16 characters", nameof(value));
        }

        return Set(label, GffFieldType.ResRef, value);
    }

    /// <summary>A CExoLocString field: a dialog.tlk StrRef (-1 for none) plus zero or
    /// more inline substrings as (language*2+gender id, text) pairs.</summary>
    public GffWriterStruct SetLocString(string label, int strRef, params (int Id, string Text)[] substrings)
    {
        ArgumentNullException.ThrowIfNull(substrings);
        return Set(
            label,
            GffFieldType.ExoLocString,
            ((int, IReadOnlyList<(int, string)>))(strRef, substrings));
    }

    /// <summary>A VOID (opaque binary) field. The bytes are copied on write, not now.</summary>
    public GffWriterStruct SetVoid(string label, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Set(label, GffFieldType.Void, value);
    }

    /// <summary>A bare StrRef field (type 18, KOTOR's addition to the format).</summary>
    public GffWriterStruct SetStrRef(string label, uint value) => Set(label, GffFieldType.StrRef, value);

    /// <summary>Sets a Struct field and returns the new child for population.</summary>
    public GffWriterStruct AddStruct(string label, uint structId)
    {
        GffWriterStruct child = new(structId);
        Set(label, GffFieldType.Struct, child);
        return child;
    }

    /// <summary>Sets a List field (initially empty) and returns it for population.</summary>
    public GffWriterList AddList(string label)
    {
        GffWriterList list = new();
        Set(label, GffFieldType.List, list);
        return list;
    }

    private GffWriterStruct Set(string label, GffFieldType type, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(label);

        if (label.Length > 16)
        {
            throw new ArgumentException($"GFF label '{label}' exceeds 16 characters", nameof(label));
        }

        for (int i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].Label == label)
            {
                Fields[i] = (label, type, value);
                return this;
            }
        }

        Fields.Add((label, type, value));
        return this;
    }
}

/// <summary>The members of one List field, in file order.</summary>
public sealed class GffWriterList
{
    internal List<GffWriterStruct> Members { get; } = [];

    public int Count => Members.Count;

    /// <summary>Appends a member struct with the given type id and returns it.</summary>
    public GffWriterStruct Add(uint structId)
    {
        GffWriterStruct member = new(structId);
        Members.Add(member);
        return member;
    }
}
