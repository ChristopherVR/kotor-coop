using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The host's rule for the single conversation token. Pure and deterministic so it can be
/// unit-tested exhaustively: given the current token and one peer's claim, it returns the new
/// token. Every peer — the host included — goes through here, so there is exactly one place
/// that decides who is talking.
///
/// Rules:
///   * A claim while the token is free grants it to the claimant.
///   * A claim by the current owner updates the spectate line (and refreshes the serial only
///     when something actually changed, so idle re-sends do not churn it).
///   * A claim by anyone else while the token is held is ignored — first speaker wins; the
///     loser's instance learns it lost from the next broadcast and backs off.
///   * A release by the current owner frees the token; a release by a non-owner is ignored.
///   * A claim on an NPC someone ELSE has an interaction lock on is refused, even when the
///     token is free (<paramref name="lockHolder"/>).
///
/// ⚠ That last rule is what stops the host winning every contested conversation BY
/// CONSTRUCTION. "First speaker wins" is only fair if both claims race on equal terms, and
/// they never did: the host's own claim runs this arbiter in-process at zero latency, while a
/// client's has to cross the network first. Two players clicking the same NPC at the same
/// instant therefore resolved to the host every single time, however early the client
/// clicked. The interaction lock is claimed from the moment a player STARTS interacting —
/// before a conversation exists — so it records who actually got there first.
/// </summary>
public static class DialogueArbiter
{
    /// <summary>
    /// Applies a claim, with <paramref name="lockHolder"/> naming the peer that holds an
    /// interaction lock on the NPC being claimed (0 when nobody does).
    /// </summary>
    public static DialogueState Apply(
        DialogueState current,
        uint requesterPeerId,
        bool active,
        string speaker,
        string line,
        string partnerTag = "",
        uint replySerial = 0,
        int replyIndex = 0,
        byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode,
        uint nodeSerial = 0,
        uint lockHolder = 0,
        bool forced = false)
    {
        speaker ??= string.Empty;
        line ??= string.Empty;
        partnerTag ??= string.Empty;

        if (active)
        {
            // Somebody else got to this NPC first. Refuse before the grant below, so a free
            // token is no help — the lock, not the token, is what settles a race.
            //
            // A claim with no partner tag is NOT refused: the tag is empty for a real window
            // at the start of every conversation (the participant ids stay OBJECT_INVALID
            // until the server resolves them), and refusing then would refuse every
            // conversation in the game for its first few hundred milliseconds.
            if (lockHolder != 0 && lockHolder != requesterPeerId && partnerTag.Length > 0)
            {
                return current;
            }

            if (!current.Active)
            {
                // Grant.
                return new DialogueState(
                    true, requesterPeerId, current.Serial + 1, speaker, line, partnerTag, replySerial, replyIndex,
                    pickLog, nodeIndex, nodeSerial, forced);
            }

            if (current.OwnerPeerId == requesterPeerId)
            {
                // Update from the owner. Only bump the serial on a real change — and the
                // NODE serial counts as a change on its own, because it is what moves the
                // follower's conversation. (The reply serial is kept in the test as well: it
                // is diagnostic now, but a pick that changed nothing else is still an event
                // worth carrying rather than one worth coalescing away.)
                if (current.SpeakerName == speaker
                    && current.Line == line
                    && current.PartnerTag == partnerTag
                    && current.ReplySerial == replySerial
                    && current.NodeSerial == nodeSerial
                    && current.Forced == forced)
                {
                    return current;
                }

                return new DialogueState(
                    true, requesterPeerId, current.Serial + 1, speaker, line, partnerTag, replySerial, replyIndex,
                    pickLog, nodeIndex, nodeSerial, forced);
            }

            // Held by someone else: ignore the contender.
            return current;
        }

        // A release only counts from the owner.
        if (current.Active && current.OwnerPeerId == requesterPeerId)
        {
            return new DialogueState(false, 0, current.Serial + 1, string.Empty, string.Empty);
        }

        return current;
    }
}
