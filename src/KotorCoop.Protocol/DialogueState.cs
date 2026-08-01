namespace KotorCoop.Protocol;

/// <summary>
/// The session's single, authoritative view of "who is in a conversation right now".
///
/// KOTOR conversations are modal and single-player: one creature talks to the party and the
/// world is expected to hold still. In co-op that only works if the two instances agree on a
/// single owner — otherwise both players could open conflicting story conversations and the
/// plot flags they set would race. The host arbitrates a token (see
/// <c>DialogueArbiter</c>): exactly one peer holds it at a time, identified by
/// <see cref="OwnerPeerId"/>, and <see cref="Serial"/> ticks on every grant/update/release so
/// clients can detect a change.
///
/// The spectate payload lets a non-owner run the SAME conversation locally, in the game's
/// own dialogue UI, rather than being shown a transcript:
///
/// <list type="bullet">
/// <item><see cref="PartnerTag"/> — the tag of the NPC the owner is talking to, so the
/// spectator can open the identical conversation against its own copy of that creature.
/// A tag rather than an object id: ids are per-load and a spectator that joined through a
/// different path could resolve a different body.</item>
/// <item><see cref="ReplySerial"/> / <see cref="ReplyIndex"/> — every reply the owner picks,
/// as the slot index they clicked. Replaying the same slot on the spectator walks its copy
/// of the .dlg down the identical branch, which is what keeps the two conversations on the
/// same line. Only the owner may pick; the spectator's ability to commit a reply is turned
/// off while it follows.</item>
/// </list>
///
/// <see cref="SpeakerName"/> and <see cref="Line"/> remain as a diagnostic read of the
/// owner's current line.
/// </summary>
public readonly record struct DialogueState(
    bool Active,
    uint OwnerPeerId,
    uint Serial,
    string SpeakerName,
    string Line,
    string PartnerTag = "",
    uint ReplySerial = 0,
    int ReplyIndex = 0,
    byte[]? PickLog = null)
{
    /// <summary>The pick log's fixed wire size: 8 entries x (serial, slot, lineHash).</summary>
    public const int PickLogBytes = 96;

    public static DialogueState Idle { get; } = new(false, 0, 0, string.Empty, string.Empty);

    /// <summary>True when <paramref name="peerId"/> currently holds the conversation token.</summary>
    public bool IsOwnedBy(uint peerId) => Active && OwnerPeerId == peerId;

    /// <summary>True when someone OTHER than <paramref name="peerId"/> is mid-conversation.</summary>
    public bool IsHeldByAnotherThan(uint peerId) => Active && OwnerPeerId != peerId;
}
