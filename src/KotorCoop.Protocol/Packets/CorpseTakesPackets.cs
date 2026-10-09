namespace KotorCoop.Protocol;

/// <summary><paramref name="Count"/> items of blueprint <paramref name="Resref"/> taken from the
/// body bag at (<paramref name="X"/>, <paramref name="Y"/>).</summary>
public readonly record struct CorpseTakeEntry(float X, float Y, string Resref, ushort Count);

/// <summary>
/// Everything player <paramref name="OriginPeerId"/> has taken from enemy body bags in
/// <paramref name="Module"/> (corpse_loot = shared). A level: each packet is the whole list, so
/// a lost datagram is repaired by the next one, and a receiver applies only the part of each
/// entry's count it has not applied yet. <paramref name="HostSessionId"/> pins it to one host
/// session.
/// </summary>
public sealed record CorpseTakesPacket(
    uint HostSessionId, uint OriginPeerId, string Module, IReadOnlyList<CorpseTakeEntry> Entries)
{
    public const int MaxEntries = 48;
}
