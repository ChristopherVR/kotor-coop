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
/// </summary>
public static class DialogueArbiter
{
    public static DialogueState Apply(
        DialogueState current,
        uint requesterPeerId,
        bool active,
        string speaker,
        string line,
        string partnerTag = "",
        uint replySerial = 0,
        int replyIndex = 0)
    {
        speaker ??= string.Empty;
        line ??= string.Empty;
        partnerTag ??= string.Empty;

        if (active)
        {
            if (!current.Active)
            {
                // Grant.
                return new DialogueState(
                    true, requesterPeerId, current.Serial + 1, speaker, line, partnerTag, replySerial, replyIndex);
            }

            if (current.OwnerPeerId == requesterPeerId)
            {
                // Update from the owner. Only bump the serial on a real change — and the
                // reply serial counts as a change even when the line has not caught up yet,
                // because it is what advances the spectator's own conversation.
                if (current.SpeakerName == speaker
                    && current.Line == line
                    && current.PartnerTag == partnerTag
                    && current.ReplySerial == replySerial)
                {
                    return current;
                }

                return new DialogueState(
                    true, requesterPeerId, current.Serial + 1, speaker, line, partnerTag, replySerial, replyIndex);
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
