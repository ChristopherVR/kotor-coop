using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>The GFF field types, numbered exactly as the format stores them.</summary>
public enum GffFieldType
{
    Byte = 0,
    Char = 1,
    Word = 2,
    Short = 3,
    Dword = 4,
    Int = 5,
    Dword64 = 6,
    Int64 = 7,
    Float = 8,
    Double = 9,
    ExoString = 10,
    ResRef = 11,
    ExoLocString = 12,
    Void = 13,
    Struct = 14,
    List = 15,
    Orientation = 16,
    Vector = 17,
    StrRef = 18,
}

/// <summary>
/// A CExoLocString: a StrRef into dialog.tlk plus zero or more inline substrings. The
/// engine prefers the substring when one is present, which is why <see cref="Text"/> is
/// "first substring or empty" rather than a TLK lookup — resolving the StrRef needs
/// dialog.tlk and is the caller's business.
/// </summary>
public sealed class GffLocString
{
    /// <summary>The dialog.tlk index, or -1 when the file stored 0xFFFFFFFF (no ref).</summary>
    public int StrRef { get; }

    /// <summary>Inline substrings as (language*2+gender id, text) pairs, in file order.</summary>
    public IReadOnlyList<(int Id, string Text)> Substrings { get; }

    internal GffLocString(int strRef, IReadOnlyList<(int Id, string Text)> substrings)
    {
        StrRef = strRef;
        Substrings = substrings;
    }

    /// <summary>The first inline substring, or "" when the value is StrRef-only.</summary>
    public string Text() => Substrings.Count > 0 ? Substrings[0].Text : string.Empty;

    public override string ToString() =>
        Substrings.Count > 0 ? $"LocString(strref={StrRef}, \"{Substrings[0].Text}\")" : $"LocString(strref={StrRef})";
}

/// <summary>
/// One struct in a parsed GFF: an ordered set of named, typed fields. Values are decoded
/// eagerly at parse time (like the Python reference), so every accessor here is a plain
/// dictionary lookup with a type check — no offset math survives past
/// <see cref="Gff.Parse"/>.
/// </summary>
public sealed class GffStruct
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, object> _values = [];
    private readonly Dictionary<string, GffFieldType> _types = [];

    /// <summary>The struct's type id from the file. The root is conventionally
    /// 0xFFFFFFFF; list members carry whatever the writer chose.</summary>
    public uint StructId { get; }

    internal GffStruct(uint structId) => StructId = structId;

    internal void Add(string label, GffFieldType type, object value)
    {
        // Duplicate labels are malformed but occur in modded files; last-writer-wins
        // matches what a dict assignment did in the reference implementation.
        if (_values.TryAdd(label, value))
        {
            _order.Add(label);
        }
        else
        {
            _values[label] = value;
        }

        _types[label] = type;
    }

    /// <summary>Field labels in file order, for callers that iterate.</summary>
    public IReadOnlyList<string> FieldNames => _order;

    /// <summary>Every field's decoded value, keyed by label. Values are the natural CLR
    /// type per field kind (byte, int, string, <see cref="GffStruct"/>, lists, ...).</summary>
    public IReadOnlyDictionary<string, object> Fields => _values;

    public int Count => _values.Count;

    public bool Contains(string field) => _values.ContainsKey(field);

    public bool TryGetFieldType(string field, out GffFieldType type) => _types.TryGetValue(field, out type);

    // -- typed accessors ---------------------------------------------------------
    //
    // The numeric accessors accept any integral field whose value fits, because GFF
    // writers are sloppy about widths: the same logical field ships as BYTE in one
    // blueprint and WORD in another, and a strict accessor would randomly fail on
    // half the game's data.

    public bool TryGetByte(string field, out byte value)
    {
        bool ok = TryGetIntegral(field, out long wide) && wide is >= byte.MinValue and <= byte.MaxValue;
        value = ok ? (byte)wide : default;
        return ok;
    }

    public bool TryGetWord(string field, out ushort value)
    {
        bool ok = TryGetIntegral(field, out long wide) && wide is >= ushort.MinValue and <= ushort.MaxValue;
        value = ok ? (ushort)wide : default;
        return ok;
    }

    public bool TryGetShort(string field, out short value)
    {
        bool ok = TryGetIntegral(field, out long wide) && wide is >= short.MinValue and <= short.MaxValue;
        value = ok ? (short)wide : default;
        return ok;
    }

    public bool TryGetInt(string field, out int value)
    {
        bool ok = TryGetIntegral(field, out long wide) && wide is >= int.MinValue and <= int.MaxValue;
        value = ok ? (int)wide : default;
        return ok;
    }

    public bool TryGetDword(string field, out uint value)
    {
        bool ok = TryGetIntegral(field, out long wide) && wide is >= uint.MinValue and <= uint.MaxValue;
        value = ok ? (uint)wide : default;
        return ok;
    }

    public bool TryGetFloat(string field, out float value)
    {
        value = default;

        if (!_values.TryGetValue(field, out object? raw))
        {
            return false;
        }

        switch (raw)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            default:
                if (TryWiden(raw, out long wide))
                {
                    value = wide;
                    return true;
                }

                return false;
        }
    }

    /// <summary>A CExoString or ResRef field's text. One accessor for both because both
    /// decode to a plain string, and callers rarely care which width the writer used.</summary>
    public bool TryGetString(string field, out string value)
    {
        if (_values.TryGetValue(field, out object? raw) && raw is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>A ResRef field's text; false for any other type, including CExoString.</summary>
    public bool TryGetResRef(string field, out string value)
    {
        if (_types.TryGetValue(field, out GffFieldType type) && type == GffFieldType.ResRef
            && _values.TryGetValue(field, out object? raw) && raw is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    public GffStruct? GetStruct(string field) =>
        _values.TryGetValue(field, out object? raw) ? raw as GffStruct : null;

    private static readonly IReadOnlyList<GffStruct> EmptyList = [];

    /// <summary>A List field's structs, or an empty list when the field is absent or not a
    /// list — so callers can foreach without an existence check first.</summary>
    public IReadOnlyList<GffStruct> GetList(string field) =>
        _values.TryGetValue(field, out object? raw) && raw is IReadOnlyList<GffStruct> list
            ? list
            : EmptyList;

    public GffLocString? GetLocString(string field) =>
        _values.TryGetValue(field, out object? raw) ? raw as GffLocString : null;

    public override string ToString() => $"<GffStruct id={StructId} {_values.Count} fields>";

    private bool TryGetIntegral(string field, out long value)
    {
        value = 0;
        return _values.TryGetValue(field, out object? raw) && TryWiden(raw, out value);
    }

    private static bool TryWiden(object raw, out long value)
    {
        switch (raw)
        {
            case byte b: value = b; return true;
            case sbyte c: value = c; return true;
            case ushort w: value = w; return true;
            case short s: value = s; return true;
            case uint u: value = u; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case ulong q when q <= long.MaxValue: value = (long)q; return true;
            default: value = 0; return false;
        }
    }
}

/// <summary>
/// BioWare GFF V3.2, the container behind .are/.git/.ifo/.ut*/.dlg/.jrl/.fac and every
/// save member. Layout, per the reference reader:
///
///   header (56 bytes):
///     char[4] fileType, char[4] fileVersion ("V3.2")
///     uint32 structOffset, structCount
///     uint32 fieldOffset,  fieldCount
///     uint32 labelOffset,  labelCount
///     uint32 fieldDataOffset, fieldDataCount   (bytes)
///     uint32 fieldIndicesOffset, fieldIndicesCount (bytes)
///     uint32 listIndicesOffset,  listIndicesCount  (bytes)
///   struct (12): uint32 type, uint32 dataOrDataOffset, uint32 fieldCount
///   field  (12): uint32 type, uint32 labelIndex, uint32 dataOrDataOffset
///   label  (16): space/NUL padded ASCII
///
/// Everything is little-endian. Simple fields live in the field record itself; complex
/// fields point into the field-data block; structs and lists index other tables.
/// </summary>
public sealed class Gff
{
    private readonly byte[] _data;
    private readonly uint _structOffset;
    private readonly uint _structCount;
    private readonly uint _fieldOffset;
    private readonly uint _fieldCount;
    private readonly uint _fieldDataOffset;
    private readonly uint _fieldIndicesOffset;
    private readonly uint _listIndicesOffset;
    private readonly string[] _labels;

    /// <summary>Structs already decoded, by index. Filled BEFORE a struct's fields are
    /// decoded so a malformed self-referential file terminates instead of recursing.</summary>
    private readonly Dictionary<uint, GffStruct> _structCache = [];

    /// <summary>The four-character type tag, padding included: "UTC ", "JRL ", "GIT ".</summary>
    public string FileType { get; }

    /// <summary>The file's top-level struct (struct index 0).</summary>
    public GffStruct Root { get; }

    /// <summary>Parses a GFF from raw bytes. Throws <see cref="InvalidDataException"/> on
    /// a wrong magic/version or on any offset that runs past the buffer.</summary>
    public static Gff Parse(byte[] data) => new(data);

    private Gff(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;

        if (data.Length < 56)
        {
            throw new InvalidDataException($"GFF: {data.Length} bytes is too short for the 56-byte header");
        }

        FileType = GameFileText.Decode(data.AsSpan(0, 4));
        string version = GameFileText.Decode(data.AsSpan(4, 4));

        if (version != "V3.2")
        {
            throw new InvalidDataException($"GFF: unsupported version '{version}' (expected V3.2)");
        }

        _structOffset = U32(8);
        _structCount = U32(12);
        _fieldOffset = U32(16);
        _fieldCount = U32(20);
        uint labelOffset = U32(24);
        uint labelCount = U32(28);
        _fieldDataOffset = U32(32);
        _fieldIndicesOffset = U32(40);
        _listIndicesOffset = U32(48);

        _labels = new string[labelCount];
        for (uint i = 0; i < labelCount; i++)
        {
            // Labels are 16-byte cells, NUL-terminated and (in some writers) space padded;
            // the trim matches the reference reader's rstrip.
            _labels[i] = GameFileText.CString(Slice(labelOffset + (16 * i), 16)).TrimEnd();
        }

        if (_structCount == 0)
        {
            throw new InvalidDataException("GFF: no structs (a valid file always has a root)");
        }

        Root = StructAt(0);
    }

    // -- decoding ----------------------------------------------------------------

    private GffStruct StructAt(uint index)
    {
        if (_structCache.TryGetValue(index, out GffStruct? cached))
        {
            return cached;
        }

        if (index >= _structCount)
        {
            throw new InvalidDataException($"GFF: struct index {index} out of range (count {_structCount})");
        }

        uint at = _structOffset + (12 * index);
        uint structType = U32(at);
        uint dataOrOffset = U32(at + 4);
        uint fieldCount = U32(at + 8);

        GffStruct result = new(structType);
        _structCache[index] = result;

        // One field stores its index inline; several store a byte offset into the
        // field-indices block. This split is the format's, not an optimisation of ours.
        if (fieldCount == 1)
        {
            AddField(result, dataOrOffset);
        }
        else
        {
            for (uint i = 0; i < fieldCount; i++)
            {
                AddField(result, U32(_fieldIndicesOffset + dataOrOffset + (4 * i)));
            }
        }

        return result;
    }

    private void AddField(GffStruct target, uint fieldIndex)
    {
        if (fieldIndex >= _fieldCount)
        {
            throw new InvalidDataException($"GFF: field index {fieldIndex} out of range (count {_fieldCount})");
        }

        uint at = _fieldOffset + (12 * fieldIndex);
        uint fieldType = U32(at);
        uint labelIndex = U32(at + 4);
        uint dataOrOffset = U32(at + 8);

        // A bad label index is reported as "?N" rather than thrown, matching the
        // reference: one corrupt label should not make the rest of the file unreadable.
        string label = labelIndex < _labels.Length ? _labels[labelIndex] : $"?{labelIndex}";

        target.Add(label, (GffFieldType)fieldType, FieldValue((GffFieldType)fieldType, dataOrOffset));
    }

    private object FieldValue(GffFieldType type, uint dataOrOffset)
    {
        // Simple types: the value IS the 4-byte cell (masked/sign-extended to width).
        switch (type)
        {
            case GffFieldType.Byte:
                return (byte)(dataOrOffset & 0xFF);
            case GffFieldType.Char:
                return unchecked((sbyte)(dataOrOffset & 0xFF));
            case GffFieldType.Word:
                return (ushort)(dataOrOffset & 0xFFFF);
            case GffFieldType.Short:
                return unchecked((short)(dataOrOffset & 0xFFFF));
            case GffFieldType.Dword:
                return dataOrOffset;
            case GffFieldType.Int:
                return unchecked((int)dataOrOffset);
            case GffFieldType.Float:
                return BitConverter.UInt32BitsToSingle(dataOrOffset);
            case GffFieldType.Struct:
                return StructAt(dataOrOffset);
            case GffFieldType.List:
                return ReadList(dataOrOffset);
        }

        // Everything else lives in the field-data block at this offset.
        uint at = _fieldDataOffset + dataOrOffset;

        switch (type)
        {
            case GffFieldType.Dword64:
                return U64(at);
            case GffFieldType.Int64:
                return unchecked((long)U64(at));
            case GffFieldType.Double:
                return BitConverter.UInt64BitsToDouble(U64(at));
            case GffFieldType.ExoString:
            {
                uint length = U32(at);
                return GameFileText.Decode(Slice(at + 4, length));
            }

            case GffFieldType.ResRef:
            {
                byte length = Slice(at, 1)[0];
                return GameFileText.Decode(Slice(at + 1, length));
            }

            case GffFieldType.ExoLocString:
                return ReadLocString(at);
            case GffFieldType.Void:
                return Slice(at + 4, U32(at)).ToArray();
            case GffFieldType.Orientation:
                return new[] { F32(at), F32(at + 4), F32(at + 8), F32(at + 12) };
            case GffFieldType.Vector:
                return new[] { F32(at), F32(at + 4), F32(at + 8) };
            case GffFieldType.StrRef:
                // A size dword (always 4) precedes the actual reference.
                return U32(at + 4);
            default:
                throw new InvalidDataException($"GFF: unknown field type {(int)type}");
        }
    }

    private GffLocString ReadLocString(uint at)
    {
        // uint32 totalSize (ignored), uint32 strref, uint32 substringCount, then per
        // substring: uint32 id, uint32 length, bytes.
        uint strref = U32(at + 4);
        uint count = U32(at + 8);
        uint p = at + 12;

        List<(int Id, string Text)> substrings = [];
        for (uint i = 0; i < count; i++)
        {
            uint id = U32(p);
            uint length = U32(p + 4);
            substrings.Add(((int)id, GameFileText.Decode(Slice(p + 8, length))));
            p += 8 + length;
        }

        return new GffLocString(strref == 0xFFFFFFFF ? -1 : unchecked((int)strref), substrings);
    }

    private IReadOnlyList<GffStruct> ReadList(uint byteOffset)
    {
        uint at = _listIndicesOffset + byteOffset;
        uint count = U32(at);

        List<GffStruct> members = new((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            members.Add(StructAt(U32(at + 4 + (4 * i))));
        }

        return members;
    }

    // -- bounds-checked reads ----------------------------------------------------
    //
    // Every read goes through these so a truncated or hostile file throws
    // InvalidDataException with a position instead of an index exception from deep
    // inside a span slice.

    private ReadOnlySpan<byte> Slice(uint offset, uint length)
    {
        if (offset > (uint)_data.Length || length > (uint)_data.Length - offset)
        {
            throw new InvalidDataException(
                $"GFF: read of {length} bytes at 0x{offset:X} runs past the {_data.Length}-byte file");
        }

        return _data.AsSpan((int)offset, (int)length);
    }

    private uint U32(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));

    private ulong U64(uint offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));

    private float F32(uint offset) => BitConverter.UInt32BitsToSingle(U32(offset));
}
