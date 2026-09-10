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
///   * A claim on an NPC someone ELSE has an interaction lock on is refused ONLY AT THE
///     MOMENT THE TOKEN IS GRANTED, even when the token is free (<paramref name="lockHolder"/>).
///
/// ⚠ That last rule is what stops the host winning every contested conversation BY
/// CONSTRUCTION. "First speaker wins" is only fair if both claims race on equal terms, and
/// they never did: the host's own claim runs this arbiter in-process at zero latency, while a
/// client's has to cross the network first. Two players clicking the same NPC at the same
/// instant therefore resolved to the host every single time, however early the client
/// clicked. The interaction lock is claimed from the moment a player STARTS interacting —
/// before a conversation exists — so it records who actually got there first.
///
/// ⚠ FIX (docs/research/dialogue-skip-desync.md, 2026-09-10): the lock veto USED TO run
/// unconditionally, ahead of every branch below — including an UPDATE from the peer who
/// ALREADY legitimately owns the token. That is a different question from "who gets to
/// START talking", and conflating them broke a live conversation outright: the interaction
/// lock's OWN claim is republished every tick by the local player's CURRENT interaction
/// target (<c>InteractionLockController.PublishLocalClaim</c>), and a FOLLOWER'S locally
/// adopted mirror of the very conversation it is spectating reads as "interacting with this
/// NPC" exactly the same as the real owner's — there is nothing in that read that
/// distinguishes "I started this" from "I am mirroring it". Once the follower's own claim
/// reaches the lock store (in-process, zero latency, when the follower happens to be the
/// host) it can win the "first fresh claim" race for an NPC the OTHER peer is genuinely,
/// already, legitimately talking to — and from that tick on, EVERY SUBSEQUENT update from the
/// true owner was silently vetoed here, forever, because the lock had quietly changed hands
/// under a conversation that never stopped being valid. Measured live: a client's conversation
/// with the host spectating stopped advancing on the host's screen after exactly one skip,
/// every time — the host's own follow-adopt claimed the interaction lock out from under the
/// client's already-granted token on the very next tick. Once a claimant already owns the
/// token (<c>current.OwnerPeerId == requesterPeerId</c>), no lock check can ever produce a
/// MORE correct owner than the one who already fairly won the grant — so the veto has no
/// business running again.
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
            if (!current.Active)
            {
                // Somebody else got to this NPC first. Refuse the GRANT, so a free token is
                // no help — the lock, not the token, is what settles a race. This check must
                // never run again once a token is live (see the class remarks' dated note):
                // it decides who gets to START talking, not who gets to keep updating a
                // conversation someone already fairly won.
                //
                // A claim with no partner tag is NOT refused: the tag is empty for a real
                // window at the start of every conversation (the participant ids stay
                // OBJECT_INVALID until the server resolves them), and refusing then would
                // refuse every conversation in the game for its first few hundred
                // milliseconds.
                if (lockHolder != 0 && lockHolder != requesterPeerId && partnerTag.Length > 0)
                {
                    return current;
                }

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
