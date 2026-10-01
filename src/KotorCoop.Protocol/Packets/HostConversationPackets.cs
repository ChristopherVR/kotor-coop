namespace KotorCoop.Protocol;

/// <summary>
/// client -&gt; host: "my engine refused to let me start this conversation because only the
/// host may lead it (<see cref="HostLedConversationRule"/>) - start it for yourself." One-shot,
/// no response. <paramref name="Module"/> is the module the sender was in when it was refused,
/// so a request that outlived a transition is dropped rather than acted on late.
/// <paramref name="Tag"/> is the target object's tag (a creature, door or placeable);
/// <paramref name="Resref"/> is the dialogue the engine was asked to run, empty for the
/// object's default conversation.
/// </summary>
public sealed record HostConversationRequestPacket(
    uint PeerId, uint RequestSerial, string Module, string Tag, string Resref)
{
    /// <summary>Longest tag carried (the shared-block field holds a NUL after it).</summary>
    public const int MaxTagLength = 23;

    public const int MaxResrefLength = 16;
}
