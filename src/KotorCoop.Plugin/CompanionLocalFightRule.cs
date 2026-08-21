namespace KotorCoop.Plugin;

/// <summary>What a party-drive pass should do for ONE companion this frame, given the
/// local-combat release gate.</summary>
internal enum CompanionLocalFightAction
{
    /// <summary>Not released, and was not released last pass either: mirror normally — host
    /// position/animation/health, faction and pin per the existing `local_combat` blanket
    /// rule.</summary>
    Mirror,

    /// <summary>The release gate just opened. Stop claiming/driving this companion, put it back
    /// in the player faction off the min-1-hp pin, and say so once.</summary>
    ReleaseAndLog,

    /// <summary>Still released since last pass: stay off, silently — the same log discipline
    /// <see cref="PartyFreezeRule"/> uses, so a fight lasting many seconds produces two lines,
    /// not one per frame.</summary>
    StayReleased,

    /// <summary>The release gate just closed (enemy died/disengaged/timed out, roster froze, the
    /// flag went off, or the triple-proximity test failed). Resume mirroring THIS pass — snap
    /// the companion's position/health back to the host's authoritative report immediately
    /// rather than waiting for the next drift check — and say so once.</summary>
    ReclaimAndLog,
}

/// <summary>
/// The decision half of the companion local-combat release gate (roadmap item 4), separated so
/// it can be unit-tested — the guard's other half (resolving positions, the engine faction/claim
/// calls) lives in <c>PartyPresenceController</c>, which has no test double for the engine.
///
/// The question it answers, once per companion per pass: is this the one companion, in the one
/// active <c>local_combat</c> fight, standing close enough to both the fighting enemy and this
/// instance's own player that handing it its own engine AI cannot put a body fighting somewhere
/// the client player cannot see. See docs/research/party-reconciliation.md, dated entry
/// 2026-08-21 ("Whose companions fight on a client, and against what"), for the full reasoning:
/// a mirrored companion's position is the HOST's position for that body, so releasing it anywhere
/// outside this triple-proximity window would visibly strand it wherever the host actually is.
/// </summary>
internal static class CompanionLocalFightRule
{
    /// <summary>
    /// Decides one companion's action from the gate inputs alone. Edge-triggered on both sides,
    /// same discipline as <see cref="PartyFreezeRule.Decide"/>.
    /// </summary>
    /// <param name="enabled">coop.ini <c>companion_local_combat</c>.</param>
    /// <param name="rosterFrozen">Party table +0xF0 != -1 — a script owns one companion directly;
    /// the same guard <see cref="PartyFreezeRule"/> already backs the whole presence mirror off
    /// for.</param>
    /// <param name="localFightActive"><c>local_combat</c>'s own exclusion is live
    /// (<c>block.LocalCombatExcludedId != 0</c>) — item 1's proven mechanism has pulled one
    /// census enemy out to fight this instance's own player for real.</param>
    /// <param name="nearEnemy">This companion's host-driven position is within
    /// <c>companion_local_combat_range</c> of the locally-fighting enemy's position.</param>
    /// <param name="nearPlayer">This companion's host-driven position is within
    /// <c>companion_local_combat_range</c> of this instance's own player.</param>
    /// <param name="releasedBefore">Was this companion released on the previous pass.</param>
    public static CompanionLocalFightAction Decide(
        bool enabled,
        bool rosterFrozen,
        bool localFightActive,
        bool nearEnemy,
        bool nearPlayer,
        bool releasedBefore)
    {
        bool shouldRelease = enabled && !rosterFrozen && localFightActive && nearEnemy && nearPlayer;

        if (shouldRelease)
        {
            return releasedBefore ? CompanionLocalFightAction.StayReleased : CompanionLocalFightAction.ReleaseAndLog;
        }

        return releasedBefore ? CompanionLocalFightAction.ReclaimAndLog : CompanionLocalFightAction.Mirror;
    }
}
