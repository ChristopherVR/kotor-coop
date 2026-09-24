namespace KotorCoop.Protocol;

/// <summary>Which of an object's two local-variable tables a slot lives in. The value is the
/// bit carried in the flag key and the shared-block entries, so it must never be renumbered.</summary>
public enum LocalVarKind : byte
{
    /// <summary><c>SetLocalBoolean</c> / <c>GetLocalBoolean</c>: 96 bits at
    /// <c>CSWVarTable+0x00</c>, slots 0..95.</summary>
    Boolean = 0,

    /// <summary><c>SetLocalNumber</c> / <c>GetLocalNumber</c>: 8 signed bytes at
    /// <c>CSWVarTable+0x0C</c>, slots 0..7.</summary>
    Number = 1,
}

/// <summary>
/// One (module, static GIT object, slot) whose local value is shared world truth and may be
/// replicated by <c>local_var_sync</c>. Only <see cref="Module"/>, <see cref="Tag"/>,
/// <see cref="ObjectId"/>, <see cref="Kind"/> and <see cref="Slot"/> are matched; the rest is the
/// audit evidence (docs/research/quest-sync.md §8.2), carried so a log line or a test failure
/// can name why the entry exists.
/// </summary>
/// <param name="Module">Module resref, case-insensitive (e.g. <c>danm13</c>).</param>
/// <param name="Tag">The object's tag, case-insensitive. Unique among the module's GIT objects
/// (the audit only admits uniquely-tagged objects).</param>
/// <param name="ObjectId">The deterministic GIT object id the audit predicted for a FIRST load
/// of the module (<c>tools/kotorfiles.py objectids</c>). Diagnostic only: the flag key uses the
/// LIVE id (both peers load the same save, so it agrees between them), and a module restored
/// from a save may legitimately renumber after destroyed objects. A mismatch is logged, not
/// refused; identity is enforced by the unique tag plus the 16-bit GIT id range.</param>
public sealed record LocalVarAllowEntry(
    string Module, string Tag, ushort ObjectId, LocalVarKind Kind, byte Slot,
    string Writer, string Reader, string Why);

/// <summary>
/// The pure half of per-object local-variable replication (coop.ini <c>local_var_sync</c>,
/// docs/research/quest-sync.md §8.1-§8.3). Compiled into BOTH the agent (by reference) and the
/// NativeAOT plugin (by source link), so the key layout, the value normalisation and the
/// allowlist are one definition on both sides of the shared block.
///
/// <b>Key layout.</b> The existing 64-bit object-keyed flag layout
/// (<c>KotorCoop.Memory.ObjectKeyedFlags</c>): an 8-bit type tag (<see cref="FlagTag"/>,
/// <c>'L'</c>), the full 32-bit FNV-1a module-name hash, then 24 low bits split as the GIT object
/// id (16), the slot (7) and the kind (1) — the split §8.1 proposed. A GIT id above 0xFFFF is
/// refused outright: dynamic ids are local to each engine and can never name the same object on
/// a peer. The key is opaque to the flag store and the wire, so no packet or protocol change is
/// needed, and the snapshot a late joiner receives already carries every entry.
/// </summary>
public static class LocalVarSyncRule
{
    /// <summary>Flag type tag 'L'. Doors use 'D' 0x44, placeables 'P' 0x50, mines 'T' 0x54.</summary>
    public const byte FlagTag = 0x4C;

    public const int BooleanSlots = 96;
    public const int NumberSlots = 8;

    private const int TagShift = 56;
    private const int ModuleShift = 24;
    private const int SlotShift = 16;
    private const int KindShift = 23;
    private const int ValueShift = 24;

    /// <summary>
    /// The shared-world-truth allowlist. Data only: adding an entry needs no code change on
    /// either side. See docs/research/quest-sync.md §8.2 for how each entry was audited and
    /// <c>tools/localvaraudit.py</c> for the scan that produced the candidates.
    /// </summary>
    public static IReadOnlyList<LocalVarAllowEntry> Allowlist { get; } = LocalVarAllowlist.Entries;

    /// <summary>The engine's own slot bounds (<c>CSWVarTable::SetLocalBoolean 0x0059B040</c>
    /// refuses outside 0..95, <c>SetLocalNumber 0x0059B0D0</c> outside 0..7).</summary>
    public static bool IsValidSlot(LocalVarKind kind, int slot) =>
        kind switch
        {
            LocalVarKind.Boolean => slot is >= 0 and < BooleanSlots,
            LocalVarKind.Number => slot is >= 0 and < NumberSlots,
            _ => false,
        };

    /// <summary>True for a deterministic GIT object id: nonzero, not OBJECT_INVALID, 16 bits.</summary>
    public static bool IsGitObjectId(uint objectId) => objectId is not 0 and <= 0xFFFF;

    /// <summary>Booleans travel as 0/1 (the engine's setter treats any nonzero as set); numbers
    /// as the signed byte the engine stores (the VM wrapper refuses outside -128..127 before
    /// reaching the setter, 0x005431EC-0x005431F4).</summary>
    public static sbyte NormaliseValue(LocalVarKind kind, int raw) =>
        kind == LocalVarKind.Boolean ? (sbyte)(raw != 0 ? 1 : 0) : unchecked((sbyte)raw);

    /// <summary>The low 24 bits of a key: object id, slot and kind.</summary>
    public static uint PackIdentity(ushort objectId, LocalVarKind kind, byte slot) =>
        objectId | ((uint)(slot & 0x7F) << SlotShift) | ((uint)kind << KindShift);

    /// <summary>A shared-block entry word: <see cref="PackIdentity"/> in the low 24 bits, the
    /// value byte above it.</summary>
    public static uint PackEntry(ushort objectId, LocalVarKind kind, byte slot, sbyte value) =>
        PackIdentity(objectId, kind, slot) | ((uint)(byte)value << ValueShift);

    public static void UnpackEntry(uint word, out ushort objectId, out LocalVarKind kind, out byte slot, out sbyte value)
    {
        objectId = (ushort)word;
        slot = (byte)((word >> SlotShift) & 0x7F);
        kind = (LocalVarKind)((word >> KindShift) & 1);
        value = unchecked((sbyte)(byte)(word >> ValueShift));
    }

    public static ulong MakeKey(uint moduleHash, ushort objectId, LocalVarKind kind, byte slot) =>
        ((ulong)FlagTag << TagShift) | ((ulong)moduleHash << ModuleShift) | PackIdentity(objectId, kind, slot);

    public static bool IsLocalVarKey(ulong key) => key > uint.MaxValue && (byte)(key >> TagShift) == FlagTag;

    /// <summary>Decodes a key built by <see cref="MakeKey"/>; false for any other key, or one whose
    /// slot is outside the engine's bounds.</summary>
    public static bool TryParseKey(ulong key, out uint moduleHash, out ushort objectId, out LocalVarKind kind, out byte slot)
    {
        moduleHash = (uint)(key >> ModuleShift);
        objectId = (ushort)key;
        slot = (byte)((key >> SlotShift) & 0x7F);
        kind = (LocalVarKind)((key >> KindShift) & 1);
        return IsLocalVarKey(key) && objectId != 0 && IsValidSlot(kind, slot);
    }

    /// <summary>FNV-1a (32-bit) over the case-folded module name — identical to
    /// <c>ObjectKeyedFlags.HashModule</c> (asserted by <c>LocalVarSyncRuleTests</c>), repeated
    /// here because the plugin cannot reference KotorCoop.Memory.</summary>
    public static uint HashModule(ReadOnlySpan<char> moduleName)
    {
        uint hash = 2166136261;
        foreach (char c in moduleName)
        {
            hash = (hash ^ (byte)char.ToLowerInvariant(c)) * 16777619;
        }

        return hash;
    }

    /// <summary>The same hash over raw ASCII bytes (the plugin reads the module name out of the
    /// engine's CExoString), folding A-Z the way <see cref="HashModule(ReadOnlySpan{char})"/> does.</summary>
    public static uint HashModuleAscii(ReadOnlySpan<byte> moduleName)
    {
        uint hash = 2166136261;
        foreach (byte b in moduleName)
        {
            byte c = (uint)(b - 'A') <= 'Z' - 'A' ? (byte)(b + 0x20) : b;
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    /// <summary>
    /// The allowlist entry for (module, tag, kind, slot), or null. The object must also carry a
    /// deterministic GIT id (<see cref="IsGitObjectId"/>): a dynamically created copy that
    /// happens to share the tag is refused, because its id means nothing on the peer.
    /// </summary>
    public static LocalVarAllowEntry? Match(
        IReadOnlyList<LocalVarAllowEntry> list, uint moduleHash, uint objectId, ReadOnlySpan<char> tag,
        LocalVarKind kind, int slot)
    {
        if (!IsGitObjectId(objectId))
        {
            return null;
        }

        foreach (LocalVarAllowEntry entry in list)
        {
            if (entry.Kind == kind && entry.Slot == slot
                && HashModule(entry.Module) == moduleHash
                && tag.Equals(entry.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the console verb's canonical request text <c>op|kind|slot|tag</c> (op is get, set or
    /// watch; kind b or n). The object is named by <c>tag</c> or, when the cheat's target id is
    /// nonzero, by that id with an empty tag.
    /// </summary>
    public static bool TryParseRequest(
        string text, out string op, out LocalVarKind kind, out byte slot, out string tag)
    {
        op = string.Empty;
        kind = LocalVarKind.Boolean;
        slot = 0;
        tag = string.Empty;

        string[] parts = text.Split('|');
        if (parts.Length != 4 || parts[0] is not ("get" or "set" or "watch"))
        {
            return false;
        }

        op = parts[0];
        if (parts[1] == "b")
        {
            kind = LocalVarKind.Boolean;
        }
        else if (parts[1] == "n")
        {
            kind = LocalVarKind.Number;
        }
        else
        {
            return false;
        }

        if (!int.TryParse(parts[2], out int parsedSlot) || !IsValidSlot(kind, parsedSlot))
        {
            return false;
        }

        slot = (byte)parsedSlot;
        tag = parts[3];
        return true;
    }

    public static string FormatRequest(string op, LocalVarKind kind, int slot, string tag) =>
        $"{op}|{(kind == LocalVarKind.Boolean ? "b" : "n")}|{slot}|{tag}";
}
