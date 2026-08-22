namespace KotorCoop.Protocol;

/// <summary>
/// The pure decision behind every per-object interaction lock — <see cref="InteractionKind.Container"/>
/// included, which is what this class is named for, but also <see cref="InteractionKind.Conversation"/>:
/// both ride the SAME lease/claim machinery (<c>KotorCoop.Session.InteractionLockStore</c>), and
/// this is the part of it that decides who holds an object, independent of the dictionary,
/// timers and locking that class wraps around it. See docs/research/container-sync.md.
///
/// Deliberately free of every timing concern: <paramref name="currentHolderFresh"/> is a plain
/// bool the caller has already worked out (the store's own <c>nowMs - RefreshedAtMs &lt;=
/// leaseMs</c> test), so this class only ever answers "given these facts, who holds it now" —
/// the same shape <c>DialogueAutoJoinRule</c> already uses to stay testable outside the game.
///
/// THREE RULES, all load-bearing for the container use case:
/// <list type="bullet">
/// <item>FIRST FRESH CLAIM WINS — a second peer's claim on a live hold is refused outright, not
/// queued, so the loser finds out the same way a blocked conversation does: the engine itself
/// refuses to let them in (<c>ContainerUseHook</c>, <c>DialogueGateHook</c>).</item>
/// <item>RE-ENTRANCY — the current holder re-claiming (or refreshing) their OWN hold always
/// succeeds. The opener re-opening their own already-held container must never be refused by
/// the very lock that exists to protect it from EVERYONE ELSE.</item>
/// <item>ONLY THE HOLDER MAY RELEASE — a stray release from anyone else (including a peer who
/// never held it) must not be able to take an object away from whoever is using it. Releasing
/// with no fresh hold at all is a no-op, not an error: it is the ordinary shape of "I am not
/// interacting with anything right now," sent every tick alongside a live claim.</item>
/// </list>
///
/// EXPIRY is not this class's decision to make — a hold whose lease ran out simply reports
/// <paramref name="currentHolderFresh"/> false, which both <see cref="MayClaim"/> and
/// <see cref="MayRelease"/> already treat as "nobody meaningfully holds this."
/// </summary>
public static class ContainerUseRule
{
    /// <summary>
    /// Whether <paramref name="requestingPeerId"/> holds the object after an ACTIVE claim,
    /// given <paramref name="currentHolderPeerId"/> (meaningless unless
    /// <paramref name="currentHolderFresh"/>) and the requester's own peer id (0 is never a
    /// real peer and never granted anything).
    /// </summary>
    public static bool MayClaim(uint currentHolderPeerId, bool currentHolderFresh, uint requestingPeerId)
    {
        if (requestingPeerId == 0)
        {
            return false;
        }

        if (!currentHolderFresh)
        {
            return true; // free, or the previous hold's lease already ran out
        }

        return currentHolderPeerId == requestingPeerId; // re-entrancy, or refused
    }

    /// <summary>
    /// Whether an explicit release (<c>Active = false</c>) from
    /// <paramref name="requestingPeerId"/> actually clears the hold. Only the current fresh
    /// holder may release — everyone else's release request, including one from a peer who
    /// never held the object at all, is a no-op.
    /// </summary>
    public static bool MayRelease(uint currentHolderPeerId, bool currentHolderFresh, uint requestingPeerId) =>
        currentHolderFresh && currentHolderPeerId == requestingPeerId;
}
