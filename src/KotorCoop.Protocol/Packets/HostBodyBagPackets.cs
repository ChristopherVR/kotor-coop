namespace KotorCoop.Protocol;

/// <summary>One item in a host body bag: blueprint and stack (credits: the amount).</summary>
public readonly record struct BodyBagItemEntry(string Resref, ushort Stack);

/// <summary>
/// host -&gt; all: the contents of the host's body bag at (<paramref name="X"/>,
/// <paramref name="Y"/>) in <paramref name="Module"/>. One packet per bag, resent each second as
/// a level; a receiver trusts only a recent one (a bag emptied and removed on the host simply
/// stops being sent).
/// </summary>
public sealed record HostBodyBagPacket(
    uint HostSessionId, string Module, float X, float Y, IReadOnlyList<BodyBagItemEntry> Items)
{
    public const int MaxItems = 24;
}
