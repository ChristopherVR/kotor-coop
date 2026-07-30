using System.Text;

namespace KotorCoop.Memory;

/// <summary>A declared global variable: its type, its slot, and the co-op flag id for it.</summary>
public sealed record GlobalVariable(string Name, GlobalVariableType Type, int Index, uint FlagId);

public enum GlobalVariableType
{
    Boolean = 0,
    Number = 1,
    Location = 2,
    String = 3,
}

/// <summary>
/// KOTOR's world state: the named globals that every quest, door and story beat branches
/// on. Replicating these is the difference between two players sharing a map and two
/// players sharing a game.
///
/// The table is <c>CSWGlobalVariableTable</c>, embedded (not pointed to) inside
/// <c>CServerExoAppInternal</c>. Everything needed is plain memory at fixed offsets, so
/// this works entirely from the external agent with no engine calls: booleans are a bit
/// array and numbers are one signed byte each, and writing them is exactly what the
/// engine's own setters do.
///
/// The catalogue of names is fixed for a session and comes out of the save, so two
/// instances loading the same save get byte-identical catalogues and identical slot
/// indices. That is what makes <c>(type, index)</c> usable directly as a network key — no
/// name hashing, and no chance of two variables colliding on one id.
/// </summary>
public sealed class GlobalVariableTable(ProcessMemory memory)
{
    /// <summary>CServerExoAppInternal + this = the embedded CSWGlobalVariableTable.</summary>
    private const int GlobalTableOffset = 0x000100FC;

    // Layout within CSWGlobalVariableTable.
    private const int IdentifierCount = 1775;
    private const int IdentifierStride = 0x18;
    private const int IdentifierNameLength = 22;
    private const int OffsetBooleans = 0x0A668;
    private const int OffsetNumbers = 0x0A6D9;

    public const int BooleanByteCount = 113;
    public const int NumberCount = 500;

    private readonly ProcessMemory _memory = memory;

    private nint _table;

    /// <summary>Every declared variable, in catalogue order. Empty until resolved.</summary>
    public IReadOnlyList<GlobalVariable> Catalogue { get; private set; } = [];

    /// <summary>
    /// A digest of the catalogue. Two peers whose digests differ are not playing the same
    /// game — different save, or different mods — and their slot indices would silently
    /// mean different variables, so this is worth comparing before trusting any of it.
    /// </summary>
    public uint CatalogueDigest { get; private set; }

    public bool IsResolved => _table != 0 && Catalogue.Count > 0;

    /// <summary>
    /// Locates the table and reads the catalogue. Cheap enough to retry, but the result is
    /// stable for a session, so callers should do this once per loaded save.
    /// </summary>
    public bool TryResolve(out string diagnostic)
    {
        _table = 0;
        Catalogue = [];

        if (!EngineGlobals.TryGetServerInternal(_memory, out nint internalServer))
        {
            diagnostic = "no server yet (still at the main menu?)";
            return false;
        }

        nint table = internalServer + GlobalTableOffset;

        byte[] raw = new byte[IdentifierCount * IdentifierStride];
        if (!_memory.TryRead(table, raw))
        {
            diagnostic = "could not read the global variable catalogue";
            return false;
        }

        List<GlobalVariable> catalogue = [];
        uint digest = 2166136261;

        for (int slot = 0; slot < IdentifierCount; slot++)
        {
            int offset = slot * IdentifierStride;

            // An empty first byte marks a free hash slot; the table is sparse.
            if (raw[offset] == 0)
            {
                continue;
            }

            int length = 0;
            while (length < IdentifierNameLength && raw[offset + length] != 0)
            {
                length++;
            }

            string name = Encoding.ASCII.GetString(raw, offset, length);

            // The u16 packs the type into the top two bits and the value index into the
            // low 14, which is what makes (type, index) a complete address for a variable.
            ushort packed = BitConverter.ToUInt16(raw, offset + IdentifierNameLength);
            GlobalVariableType type = (GlobalVariableType)(packed >> 14);
            int index = packed & 0x3FFF;

            catalogue.Add(new GlobalVariable(name, type, index, MakeFlagId(type, index)));

            foreach (char c in name)
            {
                digest = (digest ^ char.ToLowerInvariant(c)) * 16777619;
            }

            digest = (digest ^ packed) * 16777619;
        }

        if (catalogue.Count == 0)
        {
            diagnostic = "the global variable catalogue is empty";
            return false;
        }

        _table = table;
        Catalogue = catalogue;
        CatalogueDigest = digest;

        int booleans = catalogue.Count(v => v.Type == GlobalVariableType.Boolean);
        int numbers = catalogue.Count(v => v.Type == GlobalVariableType.Number);

        diagnostic = $"{catalogue.Count} globals ({booleans} boolean, {numbers} number), digest 0x{digest:X8}";
        return true;
    }

    /// <summary>
    /// The network id for a variable. The slot index is identical in both instances, so it
    /// is used directly rather than hashing the name: no collisions are possible, which
    /// matters because a collision here silently fuses two unrelated quest flags.
    /// </summary>
    public static uint MakeFlagId(GlobalVariableType type, int index) =>
        ((uint)type << 16) | (uint)(index & 0xFFFF);

    /// <summary>Reads the raw boolean bit array and the number array in one pass.</summary>
    public bool TryReadValues(byte[] booleans, sbyte[] numbers)
    {
        if (_table == 0)
        {
            return false;
        }

        byte[] numberBytes = new byte[NumberCount];

        if (!_memory.TryRead(_table + OffsetBooleans, booleans)
            || !_memory.TryRead(_table + OffsetNumbers, numberBytes))
        {
            return false;
        }

        for (int i = 0; i < NumberCount; i++)
        {
            numbers[i] = (sbyte)numberBytes[i];
        }

        return true;
    }

    /// <summary>
    /// Bit order is MSB-first within each byte, matching the engine's own accessor. Getting
    /// this backwards would read a plausible-looking but entirely wrong set of flags.
    /// </summary>
    public static bool GetBit(byte[] booleans, int index) =>
        ((booleans[index >> 3] >> (7 - (index & 7))) & 1) != 0;

    public bool TryWriteBoolean(int index, bool value)
    {
        if (_table == 0 || index < 0 || index >= BooleanByteCount * 8)
        {
            return false;
        }

        nint address = _table + OffsetBooleans + (index >> 3);

        if (!_memory.TryRead(address, out byte current))
        {
            return false;
        }

        int mask = 1 << (7 - (index & 7));
        byte updated = (byte)(value ? current | mask : current & ~mask);

        return updated == current || _memory.TryWrite(address, updated);
    }

    public bool TryWriteNumber(int index, sbyte value) =>
        _table != 0
        && index >= 0
        && index < NumberCount
        && _memory.TryWrite(_table + OffsetNumbers + index, (byte)value);
}
