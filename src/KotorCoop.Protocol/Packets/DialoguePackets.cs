namespace KotorCoop.Protocol;

// The conversation-ownership token and the reward-grant relay. Matches
// PacketCodec.Dialogue.cs. (DialogueState itself lives in DialogueState.cs.)

/// <summary>
/// One conversation's worth of net-new items placed directly into the OWNER's speaker
/// inventory, and/or a companion the owner's party table just made available — see
/// <see cref="PacketType.DialogueGrant"/> for the full rationale. <paramref name="PartnerTag"/>
/// is the NPC tag the conversation was with, carried for logging/diagnostics only — dedup is
/// entirely on (<paramref name="OriginPeerId"/>, <paramref name="OpSequence"/>), never on this.
/// </summary>
public sealed record DialogueGrantPacket(
    uint OriginPeerId, uint OpSequence, string PartnerTag,
    IReadOnlyList<MaterialisedLootItem> Items)
{
    /// <summary>A single reward node granting more than one item at once is rare (§5 item 3 of
    /// dialogue-consequences.md flags it as untested, not impossible); this cap matches
    /// <see cref="MaterialisedLootPacket.MaxItems"/> for the same "fits one datagram
    /// comfortably" reasoning — asserted by <c>PacketCodecTests</c>.</summary>
    public const int MaxItems = 8;
}

/// <summary>
/// Acknowledges one <see cref="DialogueGrantPacket"/> op, in either direction. Carries the
/// acking peer's own id plus the (origin, sequence) pair being acked — unlike
/// <see cref="ContainerLootAckPacket"/>'s bare sequence, <see cref="DialogueGrantPacket"/> keeps
/// the TRUE reporting peer's origin on relay rather than re-attributing it to the host, so two
/// different origins can legitimately reuse the same sequence number and the ack has to say
/// which one it means.
/// </summary>
public sealed record DialogueGrantAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);

/// <summary>host -> all: the authoritative conversation token (see <see cref="DialogueState"/>).</summary>
public sealed record DialogueSnapshotPacket(long ServerTimeMs, DialogueState State);

/// <summary>
/// peer -> host: this peer's local conversation status. <paramref name="Active"/> true =
/// "I have entered a conversation" (a claim), false = "I have left it" (a release). The
/// host's arbiter decides what actually happens to the shared token.
/// </summary>
public sealed record DialogueRequestPacket(
    uint PeerId,
    bool Active,
    string SpeakerName,
    string Line,
    string PartnerTag = "",
    uint ReplySerial = 0,
    int ReplyIndex = 0,
    byte[]? PickLog = null,
    int NodeIndex = DialogueState.NoNode,
    uint NodeSerial = 0,
    bool Forced = false);
