using System.Buffers.Binary;

namespace KotorCoop.Protocol;

/// <summary>How the world a native world-up edge brought in came to exist, as far as the
/// plugin can prove it. The agent binds only <see cref="Load"/> (to an exact archive) and
/// <see cref="NewGame"/>; <see cref="Transition"/> continues the current lineage and
/// <see cref="Unknown"/> poisons it until the next proven load or new game.</summary>
public enum EnemyScalingWorldOrigin : uint
{
    None = 0,

    /// <summary>First world after an unannounced teardown (or after process start) with no
    /// load request observed since the previous world-up, the load-request observers
    /// installed, and the retail new-game module. See docs/research/enemy-health-persistence.md
    /// "Fresh-world classification".</summary>
    NewGame = 1,

    /// <summary>A native load request (the v74 provenance mailbox) was published since the
    /// previous world-up. <see cref="EnemyScalingWorldPage.OriginProvenanceGeneration"/> names it.</summary>
    Load = 2,

    /// <summary>An announced teardown (door, trigger, scripted StartNewModule, co-op warp).</summary>
    Transition = 3,

    /// <summary>An unannounced teardown with nothing proving a load or a new game.</summary>
    Unknown = 4,
}

/// <summary>The plugin's health-scaling gate for one world generation.</summary>
public enum EnemyScalingHealthMode : uint
{
    /// <summary>No verdict yet: no health write of any kind happens.</summary>
    Pending = 0,

    /// <summary>The agent proved no creature in this world carries a scaled maximum.</summary>
    Fresh = 1,

    /// <summary>A verified baseline set was imported and seeded into the latch.</summary>
    Imported = 2,

    /// <summary>Fail closed: health stays exactly as loaded for the rest of this world.</summary>
    Refused = 3,
}

/// <summary>The agent's answer for one world generation.</summary>
public enum EnemyScalingVerdict : uint
{
    None = 0,
    Fresh = 1,
    Import = 2,
    Refuse = 3,
}

/// <summary>One native latch entry: the unscaled maximum and the percentage the stored
/// maximum currently represents.</summary>
public readonly record struct EnemyScalingLatchEntry(uint ObjectId, int BaseMaximum, int AppliedPercent);

/// <summary>A stable read of the plugin's world/export page.</summary>
public sealed record EnemyScalingWorldPage(
    uint Sequence,
    uint WorldGeneration,
    EnemyScalingWorldOrigin Origin,
    uint OriginProvenanceGeneration,
    uint OriginFlags,
    uint WorldLoadSerialAtWorldUp,
    EnemyScalingHealthMode Mode,
    uint ExportGeneration,
    uint PreviousWorldGeneration,
    uint PreviousFinalExportGeneration,
    int PageIndex,
    int PageCount,
    bool SnapshotComplete,
    bool Overflow,
    string Module,
    IReadOnlyList<EnemyScalingLatchEntry> Records);

/// <summary>A stable read of the plugin's native save-request mailbox.</summary>
public sealed record EnemyScalingSaveRequest(
    uint Sequence,
    int Slot,
    uint WorldGeneration,
    long RequestFileTimeUtc,
    string Name)
{
    /// <summary>The engine's own directory name: <c>SAVES:%06d - %s</c> in
    /// <c>CServerExoAppInternal::SaveGame 0x4B58A0</c>.</summary>
    public string DirectoryName => EnemyScalingBaselineChannel.SaveDirectoryName(Slot, Name);
}

/// <summary>
/// Shared-block version 76 layout and codec for exact enemy-health baseline persistence
/// (docs/research/enemy-health-persistence.md). Compiled into the plugin and the agent;
/// offsets are absolute block offsets inside the reserved 0x5400..0x5800 region.
///
/// Three channels, each a seqlock (odd while written, nonzero even when published):
/// the plugin's world/export page (one page of the native latch at a time, advanced by the
/// agent's acknowledgement), the agent's verdict/import page (advanced by the plugin's
/// acknowledgement), and the plugin's native save-request mailbox.
/// </summary>
public static class EnemyScalingBaselineChannel
{
    public const int RegionStart = 0x5400;
    public const int RegionEnd = 0x5800;

    public const int ExportRecordsPerPage = 64;
    public const int ImportRecordsPerPage = 32;
    public const int LatchCapacity = 256;
    public const int MaximumExportPages = LatchCapacity / ExportRecordsPerPage;
    public const int MaximumImportPages = LatchCapacity / ImportRecordsPerPage;
    public const int RecordStride = 8;
    public const int ModuleNameCapacity = 16;
    public const int SaveNameCapacity = 64;

    public const uint OriginFlagRequestWithoutWorld = 1u << 0;
    public const uint OriginFlagProvenanceObserved = 1u << 1;

    public const ushort ExportFlagSnapshotComplete = 1 << 0;
    public const ushort ExportFlagOverflow = 1 << 1;

    // ---- plugin -> agent: world/export page ----------------------------------------------
    public const int OffsetExportSequence = 0x5400;
    public const int OffsetExportWorldGeneration = 0x5404;
    public const int OffsetExportOrigin = 0x5408;
    public const int OffsetExportOriginProvenance = 0x540C;
    public const int OffsetExportOriginFlags = 0x5410;
    public const int OffsetExportWorldLoadSerial = 0x5414;
    public const int OffsetExportMode = 0x5418;
    public const int OffsetExportGeneration = 0x541C;
    public const int OffsetExportPreviousWorldGeneration = 0x5420;
    public const int OffsetExportPreviousFinalGeneration = 0x5424;
    public const int OffsetExportPageInfo = 0x5428;
    public const int OffsetExportRecordInfo = 0x542C;
    public const int OffsetExportModule = 0x5430;
    public const int OffsetExportRecords = 0x5440;
    public const int ExportPayloadStart = OffsetExportWorldGeneration;
    public const int ExportPayloadLength = OffsetExportRecords + (ExportRecordsPerPage * RecordStride) - ExportPayloadStart;

    // ---- agent -> plugin: export acknowledgement -----------------------------------------
    public const int OffsetExportAck = 0x5640;

    // ---- agent -> plugin: verdict/import page --------------------------------------------
    public const int OffsetImportSequence = 0x5648;
    public const int OffsetImportWorldGeneration = 0x564C;
    public const int OffsetImportVerdict = 0x5650;
    public const int OffsetImportPageInfo = 0x5654;
    public const int OffsetImportRecordCount = 0x5658;
    public const int OffsetImportTotalRecords = 0x565C;
    public const int OffsetImportRecords = 0x5660;
    public const int ImportPayloadStart = OffsetImportWorldGeneration;
    public const int ImportPayloadLength = OffsetImportRecords + (ImportRecordsPerPage * RecordStride) - ImportPayloadStart;

    // ---- plugin -> agent: import acknowledgement -----------------------------------------
    public const int OffsetImportAck = 0x5760;

    // ---- plugin -> agent: native save-request mailbox ------------------------------------
    public const int OffsetSaveSequence = 0x5768;
    public const int OffsetSaveSlot = 0x576C;
    public const int OffsetSaveWorldGeneration = 0x5770;
    public const int OffsetSaveNameLength = 0x5774;
    public const int OffsetSaveFileTime = 0x5778;
    public const int OffsetSaveName = 0x5780;
    public const int SavePayloadStart = OffsetSaveSlot;
    public const int SavePayloadLength = OffsetSaveName + SaveNameCapacity - SavePayloadStart;

    /// <summary>Directory name the engine creates for a save: <c>%06d - %s</c> in both
    /// <c>SaveGame 0x4B58A0</c> and <c>LoadGame 0x4BA640</c> (0x4BA71B, format 0x7455AC); a
    /// load with slot -1 uses the name itself (0x4BA72B).</summary>
    public static string SaveDirectoryName(int slot, string name) =>
        slot == -1 ? name : $"{slot:D6} - {name}";

    /// <summary>Advances a seqlock's nonzero even value, reserving the odd value between.</summary>
    public static uint NextSequence(uint currentEven, out uint inProgressOdd) =>
        LoadProvenanceGeneration.Next(currentEven, out inProgressOdd);

    public static uint PackPageInfo(int pageIndex, int pageCount) =>
        (uint)(pageIndex & 0xFFFF) | ((uint)(pageCount & 0xFFFF) << 16);

    public static void WriteRecord(Span<byte> destination, EnemyScalingLatchEntry entry)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, entry.ObjectId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)entry.BaseMaximum);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)entry.AppliedPercent);
    }

    public static EnemyScalingLatchEntry ReadRecord(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source),
        BinaryPrimitives.ReadUInt16LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[6..]));

    public static bool IsValidEntry(EnemyScalingLatchEntry entry) =>
        entry.ObjectId is not (0 or EnemyScalingBaselineRule.InvalidObjectId
            or EnemyScalingBaselineRule.PlayerObjectId or uint.MaxValue)
        && entry.BaseMaximum is > 0 and <= ushort.MaxValue
        && entry.AppliedPercent is >= EnemyScalingBaselineRule.MinimumPercent
            and <= EnemyScalingBaselineRule.MaximumPercent;

    /// <summary>Decodes one world/export page read between two equal, even, nonzero sequence
    /// reads. The payload span starts at <see cref="ExportPayloadStart"/>.</summary>
    public static bool TryDecodeWorldPage(
        uint sequenceBefore,
        ReadOnlySpan<byte> payload,
        uint sequenceAfter,
        out EnemyScalingWorldPage? page)
    {
        page = null;
        if (sequenceBefore == 0 || (sequenceBefore & 1) != 0 || sequenceBefore != sequenceAfter
            || payload.Length < ExportPayloadLength)
        {
            return false;
        }

        uint generation = ReadExport(payload, OffsetExportWorldGeneration);
        var origin = (EnemyScalingWorldOrigin)ReadExport(payload, OffsetExportOrigin);
        var mode = (EnemyScalingHealthMode)ReadExport(payload, OffsetExportMode);
        uint pageInfo = ReadExport(payload, OffsetExportPageInfo);
        uint recordInfo = ReadExport(payload, OffsetExportRecordInfo);
        int pageIndex = (int)(pageInfo & 0xFFFF);
        int pageCount = (int)(pageInfo >> 16);
        int count = (int)(recordInfo & 0xFFFF);
        ushort flags = (ushort)(recordInfo >> 16);

        if (generation == 0
            || origin is < EnemyScalingWorldOrigin.NewGame or > EnemyScalingWorldOrigin.Unknown
            || mode > EnemyScalingHealthMode.Refused
            || pageCount is < 1 or > MaximumExportPages
            || pageIndex >= pageCount
            || count > ExportRecordsPerPage
            || (flags & ~(ExportFlagSnapshotComplete | ExportFlagOverflow)) != 0)
        {
            return false;
        }

        if (!TryDecodeModule(payload.Slice(OffsetExportModule - ExportPayloadStart, ModuleNameCapacity), out string module))
        {
            return false;
        }

        EnemyScalingLatchEntry[] records = new EnemyScalingLatchEntry[count];
        ReadOnlySpan<byte> recordBytes = payload[(OffsetExportRecords - ExportPayloadStart)..];
        for (int i = 0; i < count; i++)
        {
            records[i] = ReadRecord(recordBytes.Slice(i * RecordStride, RecordStride));
            if (!IsValidEntry(records[i]))
            {
                return false;
            }
        }

        page = new EnemyScalingWorldPage(
            sequenceBefore,
            generation,
            origin,
            ReadExport(payload, OffsetExportOriginProvenance),
            ReadExport(payload, OffsetExportOriginFlags),
            ReadExport(payload, OffsetExportWorldLoadSerial),
            mode,
            ReadExport(payload, OffsetExportGeneration),
            ReadExport(payload, OffsetExportPreviousWorldGeneration),
            ReadExport(payload, OffsetExportPreviousFinalGeneration),
            pageIndex,
            pageCount,
            (flags & ExportFlagSnapshotComplete) != 0,
            (flags & ExportFlagOverflow) != 0,
            module,
            records);
        return true;
    }

    /// <summary>Encodes one verdict/import page payload (starting at
    /// <see cref="ImportPayloadStart"/>). The caller brackets it with the odd/even sequence.</summary>
    public static void EncodeImportPage(
        Span<byte> payload,
        uint worldGeneration,
        EnemyScalingVerdict verdict,
        int pageIndex,
        int pageCount,
        int totalRecords,
        ReadOnlySpan<EnemyScalingLatchEntry> records)
    {
        if (payload.Length < ImportPayloadLength || records.Length > ImportRecordsPerPage)
        {
            throw new ArgumentException("import page does not fit", nameof(payload));
        }

        payload[..ImportPayloadLength].Clear();
        WriteImport(payload, OffsetImportWorldGeneration, worldGeneration);
        WriteImport(payload, OffsetImportVerdict, (uint)verdict);
        WriteImport(payload, OffsetImportPageInfo, PackPageInfo(pageIndex, pageCount));
        WriteImport(payload, OffsetImportRecordCount, (uint)records.Length);
        WriteImport(payload, OffsetImportTotalRecords, (uint)totalRecords);
        Span<byte> recordBytes = payload[(OffsetImportRecords - ImportPayloadStart)..];
        for (int i = 0; i < records.Length; i++)
        {
            WriteRecord(recordBytes.Slice(i * RecordStride, RecordStride), records[i]);
        }
    }

    /// <summary>Decodes a verdict/import page header. Records are validated separately by the
    /// plugin's <see cref="EnemyScalingImportGate"/> so this stays allocation-free.</summary>
    public static bool TryDecodeImportHeader(
        ReadOnlySpan<byte> payload,
        out uint worldGeneration,
        out EnemyScalingVerdict verdict,
        out int pageIndex,
        out int pageCount,
        out int recordCount,
        out int totalRecords)
    {
        worldGeneration = 0;
        verdict = EnemyScalingVerdict.None;
        pageIndex = pageCount = recordCount = totalRecords = 0;
        if (payload.Length < ImportPayloadLength)
        {
            return false;
        }

        worldGeneration = ReadImport(payload, OffsetImportWorldGeneration);
        verdict = (EnemyScalingVerdict)ReadImport(payload, OffsetImportVerdict);
        uint pageInfo = ReadImport(payload, OffsetImportPageInfo);
        pageIndex = (int)(pageInfo & 0xFFFF);
        pageCount = (int)(pageInfo >> 16);
        uint count = ReadImport(payload, OffsetImportRecordCount);
        uint total = ReadImport(payload, OffsetImportTotalRecords);

        if (worldGeneration == 0
            || verdict is < EnemyScalingVerdict.Fresh or > EnemyScalingVerdict.Refuse
            || pageCount is < 1 or > MaximumImportPages
            || pageIndex >= pageCount
            || count > ImportRecordsPerPage
            || total > LatchCapacity)
        {
            return false;
        }

        recordCount = (int)count;
        totalRecords = (int)total;
        if (verdict != EnemyScalingVerdict.Import)
        {
            return pageCount == 1 && recordCount == 0 && totalRecords == 0;
        }

        // Every page but the last is full; the last carries the remainder (an empty import
        // is one page of zero records). Anything else is a torn or foreign write.
        int expected = pageIndex < pageCount - 1
            ? ImportRecordsPerPage
            : totalRecords - ((pageCount - 1) * ImportRecordsPerPage);
        return pageCount == ImportPageCount(totalRecords) && recordCount == expected;
    }

    /// <summary>Page count the agent uses for an import of <paramref name="records"/> entries.</summary>
    public static int ImportPageCount(int records) =>
        Math.Max(1, (records + ImportRecordsPerPage - 1) / ImportRecordsPerPage);

    /// <summary>Page count the plugin uses for an export of <paramref name="records"/> entries.</summary>
    public static int ExportPageCount(int records) =>
        Math.Max(1, (records + ExportRecordsPerPage - 1) / ExportRecordsPerPage);

    public static bool TryDecodeSaveRequest(
        uint sequenceBefore,
        ReadOnlySpan<byte> payload,
        uint sequenceAfter,
        out EnemyScalingSaveRequest? request)
    {
        request = null;
        if (sequenceBefore == 0 || (sequenceBefore & 1) != 0 || sequenceBefore != sequenceAfter
            || payload.Length < SavePayloadLength)
        {
            return false;
        }

        int slot = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(OffsetSaveSlot - SavePayloadStart, 4));
        uint generation = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(OffsetSaveWorldGeneration - SavePayloadStart, 4));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(OffsetSaveNameLength - SavePayloadStart, 4));
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(OffsetSaveFileTime - SavePayloadStart, 8));
        if (slot < 0 || length is 0 or > SaveNameCapacity || fileTime <= 0)
        {
            return false;
        }

        ReadOnlySpan<byte> name = payload.Slice(OffsetSaveName - SavePayloadStart, (int)length);
        foreach (byte b in name)
        {
            if (b is < 0x20 or 0x7F || b is (byte)'/' or (byte)'\\' or (byte)':')
            {
                return false;
            }
        }

        request = new EnemyScalingSaveRequest(
            sequenceBefore, slot, generation, fileTime, System.Text.Encoding.Latin1.GetString(name));
        return true;
    }

    private static uint ReadExport(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset - ExportPayloadStart, 4));

    private static uint ReadImport(ReadOnlySpan<byte> payload, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset - ImportPayloadStart, 4));

    private static void WriteImport(Span<byte> payload, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(payload.Slice(offset - ImportPayloadStart, 4), value);

    private static bool TryDecodeModule(ReadOnlySpan<byte> bytes, out string module)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        for (int i = length; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                module = string.Empty;
                return false;
            }
        }

        for (int i = 0; i < length; i++)
        {
            byte c = bytes[i];
            if (!(c is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'0' and <= (byte)'9' or (byte)'_' or (byte)'-'))
            {
                module = string.Empty;
                return false;
            }
        }

        module = System.Text.Encoding.ASCII.GetString(bytes[..length]);
        return true;
    }
}
