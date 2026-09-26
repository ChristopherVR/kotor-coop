namespace KotorCoop.Protocol;

/// <summary>
/// A fingerprint of one peer's game content — the exe, the override folder, the data
/// tables co-op reads, the journal and the TLK. Two peers exchange it at join time so a
/// mismatch that would silently corrupt state (a reordered <c>appearance.2da</c> painting
/// the wrong body, a differently-modded module shifting the object ids enemies and doors
/// key on) is caught and turned into a refusal or a degraded channel instead.
///
/// Every field is a 32-bit FNV-1a hash (or a plain count), because the goal is only to
/// answer "are these the same?" cheaply — never to reconstruct the content. A field that
/// could not be computed (no read access, no override folder, an install this build does
/// not understand) has its bit cleared in <see cref="KnownMask"/> and can only ever
/// produce a warning: a filesystem quirk on one side must not be able to refuse a session
/// that would otherwise work.
///
/// Deliberately in the protocol assembly, not the memory assembly: the same value is
/// computed by the agent (from game files) and compared by <see cref="ContentCompatibility"/>
/// on both host and client, so the verdict is identical everywhere.
/// </summary>
public readonly record struct ContentDigest(
    ushort KnownMask,
    uint ExeDigest,
    uint OverrideCensus,
    ushort OverrideCount,
    uint TwoDaDigest,
    uint AppearanceDigest,
    ushort AppearanceRows,
    uint TlkDigest,
    uint TlkEntryCount,
    uint JournalDigest,
    uint ModulesCensus,
    uint GameplayDigest = 0)
{
    /// <summary>Bumped only if the set/order of fields changes. Lets a future build reject
    /// a digest it cannot interpret rather than misreading it.</summary>
    public const byte CurrentScheme = 2;

    /// <summary>Which fields were actually computable. A cleared bit means "unknown", which
    /// downgrades any disagreement on that field to a warning.</summary>
    [Flags]
    public enum Field : ushort
    {
        None = 0,
        Exe = 1 << 0,
        Override = 1 << 1,
        TwoDa = 1 << 2,
        Appearance = 1 << 3,
        Tlk = 1 << 4,
        Journal = 1 << 5,
        Modules = 1 << 6,
        Gameplay = 1 << 7,
    }

    /// <summary>Nothing was computed — an agent without game-file access. Every comparison
    /// against this is "unknown", i.e. warn-only; it never blocks a session.</summary>
    public static ContentDigest Unknown => default;

    public bool Knows(Field field) => (KnownMask & (ushort)field) != 0;

    /// <summary>True when this digest carries no information at all (a peer that could not,
    /// or chose not to, fingerprint its install).</summary>
    public bool IsEmpty => KnownMask == 0;
}
