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
/// <see cref="SpeakerName"/> and <see cref="Line"/> are the spectate payload: the current
/// speaker and their line, so a non-owner can be shown what the owner is hearing. They are
/// empty until the plugin can read the live conversation (that half is RE-gated); the
/// ownership token is useful on its own.
/// </summary>
public readonly record struct DialogueState(
    bool Active,
    uint OwnerPeerId,
    uint Serial,
    string SpeakerName,
    string Line)
{
    public static DialogueState Idle { get; } = new(false, 0, 0, string.Empty, string.Empty);

    /// <summary>True when <paramref name="peerId"/> currently holds the conversation token.</summary>
    public bool IsOwnedBy(uint peerId) => Active && OwnerPeerId == peerId;

    /// <summary>True when someone OTHER than <paramref name="peerId"/> is mid-conversation.</summary>
    public bool IsHeldByAnotherThan(uint peerId) => Active && OwnerPeerId != peerId;
}
