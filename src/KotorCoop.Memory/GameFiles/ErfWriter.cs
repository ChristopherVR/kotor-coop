using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Rewrites ONE member's bytes inside an ERF/MOD/SAV archive already held in memory, keeping
/// every other member's bytes, the whole header, the localised-string block and the key table
/// byte-for-byte identical. Built for docs/research/personal-inventory.md's save-file
/// transform: a save's <c>INVENTORY.res</c> lives as a top-level member of
/// <c>SAVEGAME.sav</c> (an outer ERF), and there is no engine call that can rewrite one member
/// of a live save — this is a pure, offline byte rebuild of the same container format
/// <see cref="ErfArchive"/> already reads.
///
/// General ERF V1.0 layout (see <see cref="ErfArchive"/> for the field-by-field citation): a
/// 160-byte header, a localised-string block, a 24-byte-per-entry key table, an
/// 8-byte-per-entry resource (offset/size) table, then the resource bytes themselves. This
/// writer never changes the entry COUNT, so nothing before the resource table ever needs to
/// move: everything through the end of the resource table (header, loc-str block, key table,
/// resource table) is copied byte for byte from the source, and the resource-table entries'
/// offset/size CELLS are the only bytes in that whole region this writer overwrites — every
/// member's data is then re-laid-out, fresh, starting right where the resource table ends.
/// </summary>
public static class ErfWriter
{
    /// <summary>
    /// Replaces the bytes of the member named <paramref name="resref"/> with type
    /// <paramref name="resourceType"/> (case-insensitive resref match, exactly like
    /// <see cref="ErfArchive.TryFindMember"/>) and returns the rebuilt archive. Every other
    /// member's bytes, and every key-table entry's resref/resId/type, are carried over
    /// unchanged.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a well-formed ERF V1.0 image, or no member
    /// matches — a save-file transform has nothing sane to do with a missing target, and
    /// silently no-op'ing would ship a half-edited save.</exception>
    public static byte[] ReplaceMember(byte[] erf, string resref, int resourceType, byte[] newData)
    {
        ArgumentNullException.ThrowIfNull(erf);
        ArgumentNullException.ThrowIfNull(resref);
        ArgumentNullException.ThrowIfNull(newData);

        if (erf.Length < 160)
        {
            throw new InvalidDataException($"ERF: {erf.Length} bytes is too short for the 160-byte header");
        }

        string version = GameFileText.Decode(erf.AsSpan(4, 4));
        if (version.Trim() != "V1.0")
        {
            throw new InvalidDataException($"ERF: unsupported version '{version}'");
        }

        int count = checked((int)U32(erf, 16));
        uint offKeys = U32(erf, 24);
        uint offRes = U32(erf, 28);

        if ((long)offKeys + (24L * count) > erf.Length || (long)offRes + (8L * count) > erf.Length)
        {
            throw new InvalidDataException("ERF: header offsets run past the file");
        }

        var entries = new (uint Offset, uint Size, bool IsTarget)[count];
        int targetIndex = -1;

        for (int i = 0; i < count; i++)
        {
            int keyAt = (int)offKeys + (24 * i);
            string entryResref = GameFileText.CString(erf.AsSpan(keyAt, 16));
            ushort resType = U16(erf, keyAt + 20);

            int valAt = (int)offRes + (8 * i);
            uint off = U32(erf, valAt);
            uint size = U32(erf, valAt + 4);

            if ((long)off + size > erf.Length)
            {
                throw new InvalidDataException($"ERF: member {i} runs past the file");
            }

            bool isTarget = resType == resourceType
                && string.Equals(entryResref, resref, StringComparison.OrdinalIgnoreCase);
            if (isTarget)
            {
                targetIndex = i;
            }

            entries[i] = (off, size, isTarget);
        }

        if (targetIndex < 0)
        {
            throw new InvalidDataException($"ERF: member '{resref}' (type {resourceType}) not found");
        }

        // Nothing before this point ever changes size (the entry count is fixed), so the
        // resource table's own position never moves — only its per-entry offset/size CELLS do,
        // and only every member's DATA is re-laid-out, fresh, right after the table.
        uint dataStart = offRes + (uint)(8 * count);

        using MemoryStream data = new();
        var newEntries = new (uint Offset, uint Size)[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = entries[i].IsTarget
                ? newData
                : erf.AsSpan((int)entries[i].Offset, (int)entries[i].Size).ToArray();

            newEntries[i] = (dataStart + (uint)data.Length, (uint)bytes.Length);
            data.Write(bytes);
        }

        byte[] result = new byte[dataStart + (uint)data.Length];
        Span<byte> span = result;

        // Header + loc-str block + key table + resource table, VERBATIM — nothing in this
        // region is recomputed, only the two dwords per entry below are overwritten in place.
        erf.AsSpan(0, (int)dataStart).CopyTo(span);

        for (int i = 0; i < count; i++)
        {
            int valAt = (int)offRes + (8 * i);
            WriteU32(span, valAt, newEntries[i].Offset);
            WriteU32(span, valAt + 4, newEntries[i].Size);
        }

        data.ToArray().CopyTo(span[(int)dataStart..]);

        return result;
    }

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static void WriteU32(Span<byte> destination, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
}
