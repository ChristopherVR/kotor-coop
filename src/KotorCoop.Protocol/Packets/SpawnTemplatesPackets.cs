namespace KotorCoop.Protocol;

/// <summary>One creature a host script created: the host's object id and the .utc template.</summary>
public readonly record struct SpawnTemplateEntry(uint ObjectId, string Template);

/// <summary>
/// host -&gt; all: the blueprints of the creatures the host's scripts created in
/// <paramref name="Module"/> (newest <see cref="MaxEntries"/>). A level, not an event: every
/// packet is the whole current list, so a lost datagram is repaired by the next one.
/// <paramref name="HostSessionId"/> and <paramref name="NativeEpoch"/> pin it to one host world,
/// since object ids name different creatures in the next one.
/// </summary>
public sealed record SpawnTemplatesPacket(
    uint HostSessionId, string Module, uint NativeEpoch, IReadOnlyList<SpawnTemplateEntry> Entries)
{
    public const int MaxEntries = 24;
}
