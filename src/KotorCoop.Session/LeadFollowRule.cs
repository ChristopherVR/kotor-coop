namespace KotorCoop.Session;

/// <summary>
/// coop.ini <c>follow_lead</c>: who may lead an area transition that the OTHER side follows
/// immediately through the announce channel (docs/research/transition-smoothness.md's
/// "Lead-swap" entry). <see cref="Any"/> is the new default — either player's
/// committed door crossing becomes an announce the other follows, mirroring the pre-existing
/// host-leads/client-follows path symmetrically. <see cref="Host"/> restores exactly today's
/// behaviour: only the host is ever followed, and <see cref="ClientDoorRefusalRule"/> refuses
/// every premature client crossing precisely as it did before this feature existed.
/// </summary>
public enum FollowLeadMode
{
    Any,
    Host,
}

/// <summary>
/// The host-side half of "lead-swap": given a pending announce from some peer, decide whether
/// THIS instance — running as the host — should act on it right now.
///
/// A client's own follow decision needed no change: <see cref="TransitionAnnounceRule.Decide"/>
/// already covers it, a client only ever follows the host, exactly as before this feature. What
/// is new is the reverse direction, and it needs MORE than the plain module-match check that
/// direction already has: the host owns enemies, encounters and the object-id space every other
/// player's world adopts (docs/research/world-adoption.md) — interrupting it while it is doing
/// something those channels cannot safely run through (its own transition, a conversation, a
/// real fight) would risk exactly the state those channels depend on staying host-authoritative.
/// Three inputs, none of them a clock read or an engine pointer, so this composes into the
/// 64-bit test project like every other pure rule in this file's neighbourhood.
/// </summary>
public static class LeadFollowRule
{
    /// <summary>
    /// Whether an announce from a peer who is (or is not) the host is even worth storing as
    /// "the destination we might follow" — the ELIGIBILITY half, checked once when the announce
    /// first arrives (<c>CoopAgent.PumpTransitionAnnounce</c>). The SAFETY half
    /// (<see cref="HostMaySafelyFollow"/>) is re-checked on every tick the announce is still
    /// pending, because the host's own busy-ness can start or end at any time while a client's
    /// announce sits waiting — eligibility, by contrast, only depends on <paramref name="mode"/>,
    /// which is read once from coop.ini and does not change mid-session.
    /// </summary>
    public static bool IsEligibleLeader(bool weAreHost, bool announcerIsHost, FollowLeadMode mode)
    {
        if (announcerIsHost)
        {
            return true; // every peer already follows the host - unaffected by this feature
        }

        // The announcer is a client. Only the host ever follows a client (a client never
        // follows another client - there is exactly one shared world, and it is wherever the
        // host is or is about to be), and only when the mode allows it.
        return weAreHost && mode == FollowLeadMode.Any;
    }

    /// <summary>
    /// The host-only safety gate (design point 2 of the lead-swap entry): even an
    /// eligible client announce must not be acted on while interrupting the host would risk
    /// something the host-authoritative channels depend on. All three inputs are computed fresh
    /// by the caller every tick — see <c>CoopAgent</c>'s own doc comments for exactly what each
    /// one reads.
    /// </summary>
    public static bool HostMaySafelyFollow(bool hostMidTransition, bool hostInDialogue, bool hostInCombat) =>
        !hostMidTransition && !hostInDialogue && !hostInCombat;

    /// <summary>
    /// Combines both gates into the same three-way answer <see cref="TransitionAnnounceRule.Decide"/>
    /// gives a client, for the host's own use. Deliberately does not distinguish WHY it fell
    /// back (ineligible/stale vs. merely unsafe right now) — <c>CoopAgent.TryFollowAnnouncedTransition</c>
    /// makes that call itself, because only it knows whether the announce is worth holding for a
    /// later tick (the host is merely busy for a moment) or should be dropped outright (a stale
    /// or self-referential destination).
    ///
    /// This is also where "both announce simultaneously -&gt; host wins" resolves: if the host's
    /// own engine has just committed to (or is still running) its own transition,
    /// <paramref name="hostMidTransition"/> is true, this returns <see cref="TransitionFollowPath.Fallback"/>,
    /// and the host's own crossing proceeds untouched — a client's announce arriving in the same
    /// window never pre-empts it.
    /// </summary>
    public static TransitionFollowPath DecideHostFollow(
        bool hasPendingAnnounce,
        string announceModule,
        string localModule,
        bool announcerIsHost,
        FollowLeadMode mode,
        bool hostMidTransition,
        bool hostInDialogue,
        bool hostInCombat)
    {
        if (!IsEligibleLeader(true, announcerIsHost, mode))
        {
            return TransitionFollowPath.Fallback;
        }

        if (!HostMaySafelyFollow(hostMidTransition, hostInDialogue, hostInCombat))
        {
            return TransitionFollowPath.Fallback;
        }

        return TransitionAnnounceRule.Decide(hasPendingAnnounce, announceModule, localModule);
    }
}
