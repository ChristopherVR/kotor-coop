namespace KotorCoop.Protocol;

/// <summary>
/// Everything a peer needs to build the SAME party-placed floor mine the placer's engine built
/// in <c>CSWSCreature::AIActionSetMine</c>'s no-target branch (docs/research/mines.md §7.1, §14):
/// the <c>traps.2da</c> row, the trigger's position (which is also its geometry centre and its
/// 1.0-unit fire sphere), the constructor-default orientation read back for comparison, the two
/// DCs the placer's roll produced (they include a d20 and the placer's skill, so they cannot be
/// recomputed on a peer), and the faction the engine copied from the placer.
///
/// Never a local dynamic object id: that number names a different object, or nothing, on every
/// other instance (mines.md §7). The session serial the host assigns is the only shared name.
/// </summary>
public readonly record struct PlacedMineRecord(
    byte TrapType,
    float X,
    float Y,
    float Z,
    float OrientX,
    float OrientY,
    short DetectDc,
    short DisarmDc,
    ushort Faction)
{
    /// <summary><c>CSWSTrigger::Constructor</c> initialises <c>+0x2D4</c> to 0xFF ("no trap
    /// type"); a mine never carries it.</summary>
    public const byte NoTrapType = 0xFF;

    /// <summary>Encoded size in bytes (see <see cref="PacketCodec"/>).</summary>
    public const int WireSize = 1 + (5 * 4) + 2 + 2 + 2;

    public bool IsValid =>
        TrapType != NoTrapType
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && float.IsFinite(OrientX) && float.IsFinite(OrientY);
}

/// <summary>What a <see cref="PlacedMineIntentPacket"/> reports.</summary>
public enum PlacedMineIntentKind : byte
{
    /// <summary>A party member on the reporting instance placed a new floor mine.</summary>
    Placed = 1,

    /// <summary>The mine with this session serial no longer exists on the reporting instance
    /// (detonated, disarmed or recovered there — the engine's own event 11 removed it).</summary>
    Gone = 2,
}

/// <summary>
/// client -&gt; host, retransmitted until <see cref="PlacedMineIntentAckPacket"/> arrives.
/// <paramref name="CaptureSerial"/> is the placer plugin's own capture counter (so the host's
/// answer can be matched back to the mine the placer already has, which is registered under the
/// host's serial rather than destroyed and rebuilt); <paramref name="MineSerial"/> is the host
/// serial a <see cref="PlacedMineIntentKind.Gone"/> names. <paramref name="ModuleHash"/> is the
/// shared <c>ObjectKeyedFlags.HashModule</c> of the placer's module: the host accepts a placement
/// only in its own current module, because the host's copy is the only one armed against
/// enemies (mines.md §14.4).
/// </summary>
public sealed record PlacedMineIntentPacket(
    uint OriginPeerId,
    uint OpSequence,
    PlacedMineIntentKind Kind,
    uint ModuleHash,
    uint CaptureSerial,
    uint MineSerial,
    PlacedMineRecord Record);

/// <summary>Acknowledges one <see cref="PlacedMineIntentPacket"/> op.</summary>
public sealed record PlacedMineIntentAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);

/// <summary>One canonical mine in a <see cref="PlacedMineRosterPacket"/>.
/// <paramref name="OwnerPeerId"/> + <paramref name="CaptureSerial"/> let the placer recognise
/// its own mine.</summary>
public readonly record struct PlacedMineRosterEntry(
    uint Serial,
    uint OwnerPeerId,
    uint CaptureSerial,
    PlacedMineRecord Record)
{
    public const int WireSize = 4 + 4 + 4 + PlacedMineRecord.WireSize;
}

/// <summary>
/// host -&gt; client: the whole canonical set for the host's current module, as a level.
/// <paramref name="Generation"/> increases on every change (a receiver ignores an older one that
/// arrives late); <paramref name="HighestSerial"/> is the fence below which an absent serial
/// means "removed" rather than "not announced yet".
/// </summary>
public sealed record PlacedMineRosterPacket(
    uint Generation,
    uint ModuleHash,
    uint HighestSerial,
    IReadOnlyList<PlacedMineRosterEntry> Entries)
{
    /// <summary>Retail refuses a 16th party mine in one area (<c>CSWSArea::GetPlayerCanSetMines
    /// 0x005089D0</c>, <c>cmp al, 0xF</c>), so the canonical set never needs more.</summary>
    public const int MaxEntries = 15;
}
