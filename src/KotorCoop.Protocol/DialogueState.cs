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
/// <item><see cref="NodeIndex"/> / <see cref="NodeSerial"/> — WHERE THE OWNER IS: the index
/// of the dialogue entry on screen. This is what the follower is driven by. An entry index
/// is a plain array position in the .dlg both instances loaded, so it means the same thing
/// on both; it is also the engine's own notion of a conversation's position, which it checks
/// (<c>CSWSDialog::HandleReply</c> refuses a reply whose claimed entry index does not match
/// the live one). -1 means "unknown" and is never driven.</item>
/// <item><see cref="ReplySerial"/> / <see cref="ReplyIndex"/> / <see cref="PickLog"/> — what
/// the owner PRESSED. ⚠ DIAGNOSTIC ONLY. This used to be what the spectator replayed, and
/// replaying it cannot converge: a reply slot is not a property of the conversation but of
/// one instance's evaluation of it. The starting entry, the set of replies offered, and a
/// reply's outgoing link are each chosen by scripts that run separately on each machine, so
/// "slot 1" is not reliably the same branch on the two screens — and a follower gated on a
/// line it can only reach by applying the very pick it is gating simply stops forever. Only
/// the owner may pick; the spectator's ability to commit a reply is turned off while it
/// follows.</item>
/// </list>
///
/// <see cref="SpeakerName"/> and <see cref="Line"/> remain as a diagnostic read of the
/// owner's current line.
///
/// <see cref="Forced"/> (2026-08-23, docs/research/dialogue-sync.md's Item 1): whether the
/// OWNER's conversation is one the vanilla engine itself forces regardless of distance — an
/// ambush or scripted cutscene conversation, as opposed to an ordinary player-clicked chat.
/// <c>DialogueAutoJoinRule.ShouldAutoJoin</c> auto-joins UNCONDITIONALLY when this is set,
/// ignoring the configured mode and range, because that is matching behaviour vanilla already
/// forces onto a single-player PC rather than inventing a new one.
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
    byte[]? PickLog = null,
    int NodeIndex = -1, // = NoNode; a record's own const is not in scope for its parameter list
    uint NodeSerial = 0,
    bool Forced = false)
{
    /// <summary>The pick log's fixed wire size: 8 entries x (serial, slot, lineHash).</summary>
    public const int PickLogBytes = 96;

    /// <summary>"The owner's position is not known." Never driven — a follower that cannot be
    /// told where to go waits where it is, which is recoverable, rather than being sent to
    /// node 0, which is the top of the conversation.</summary>
    public const int NoNode = -1;

    public static DialogueState Idle { get; } = new(false, 0, 0, string.Empty, string.Empty);

    /// <summary>True when <paramref name="peerId"/> currently holds the conversation token.</summary>
    public bool IsOwnedBy(uint peerId) => Active && OwnerPeerId == peerId;

    /// <summary>True when someone OTHER than <paramref name="peerId"/> is mid-conversation.</summary>
    public bool IsHeldByAnotherThan(uint peerId) => Active && OwnerPeerId != peerId;
}
