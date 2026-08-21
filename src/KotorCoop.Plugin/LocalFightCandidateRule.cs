namespace KotorCoop.Plugin;

/// <summary>How strongly one census creature deserves the single local-fight exclusion slot
/// this frame, relative to every other candidate <see cref="EnemyPresenceController"/> looked
/// at in the same pass. Ordered so a plain numeric comparison picks the stronger claim.</summary>
internal enum LocalFightPriority
{
    /// <summary>Fails at least one hard gate (hostility, GIT identity, script slot, range) —
    /// never a candidate this frame, regardless of anything else.</summary>
    None = 0,

    /// <summary>Hostile, in range, eligible — but nothing on the wire says it is presently
    /// hitting this player's phantom on the owner's side.</summary>
    Proximity = 1,

    /// <summary>Everything <see cref="Proximity"/> requires, AND the owner's own damage
    /// report names this creature as the most recent attacker on this player's phantom,
    /// within the freshness window — the strongest evidence a candidate can offer, since it
    /// is not an inference from position, it is the owner's engine saying so.</summary>
    TargetingUs = 2,
}

/// <summary>
/// The decision half of round 55's immediate proximity trigger for <c>local_combat</c>
/// (docs/research/shared-state-audit.md §2, round 55): which of this frame's driven, hostile,
/// GIT-identical census creatures — if any — should be pulled out of the census to fight the
/// local player for real, RIGHT NOW, instead of waiting for whatever the census enumeration
/// order or the 25s timeout happens to hand it. Separated so the priority arithmetic can be
/// unit-tested without a live engine — the engine reads themselves (reputation,
/// <c>HasCreatureScriptSlot0x260</c>, position, the phantom-attacker freshness check) live in
/// <see cref="EnemyPresenceController"/>, which has no test double for the engine.
///
/// The three hard gates (hostility, GIT identity, a real +0x260 script) are exactly
/// <c>DriveOne</c>'s pre-round-49/round-39 requirements, unchanged — this rule adds nothing
/// unsafe, it only decides WHICH eligible candidate wins the one slot when more than one
/// qualifies in the same frame, and separates "eligible" from "wins" so the caller can compare
/// every candidate before arming anything (<c>StartLocalFight</c> still fires at most once per
/// frame, and only after every candidate this pass has been considered).
/// </summary>
internal static class LocalFightCandidateRule
{
    /// <summary>
    /// Decides one candidate's priority from the gate inputs alone.
    /// </summary>
    /// <param name="reputation">The engine's own <c>GetAIStateReputation(creature, playerId)</c>
    /// answer: 2 = hostile, 1 = friendly, 0 = neutral. Only 2 can ever be a candidate — a
    /// non-hostile census actor (a story NPC merely present for presence sync) must never be
    /// pulled out of the census for a fight it was never going to have (round 49).</param>
    /// <param name="gitIdentical">True when this creature resolves directly by the owner's id
    /// (a materialised stand-in wears a phantom blueprint with no real +0x260 script of its
    /// own and is never eligible — see <c>DriveOne</c>'s own "GIT-IDENTICAL CREATURES ONLY"
    /// note).</param>
    /// <param name="hasScript">The engine's own <c>+0x260</c> combat-AI event slot is non-empty
    /// (<c>Odyssey.HasCreatureScriptSlot0x260</c>) — firing an empty slot is a silent no-op that
    /// would otherwise wedge this creature into a permanent re-exclude/timeout loop (round 39).</param>
    /// <param name="distance">Planar distance from this creature's driven position to the local
    /// player, in world units.</param>
    /// <param name="engageRange">coop.ini <c>local_combat_engage_range</c> — how close counts as
    /// "close enough to trigger immediately," independent of the wider range that licenses
    /// absorbing forwarded damage once a fight is already running.</param>
    /// <param name="targetingUs">The owner's own damage report (<c>block.IncomingDamageAttacker</c>,
    /// fresh within a short window) names this creature as the one currently hitting this
    /// player's phantom over there — the host's engine is already fighting it on our behalf,
    /// so it outranks a merely-nearby creature that has not actually engaged anything yet.</param>
    public static LocalFightPriority Evaluate(
        int reputation,
        bool gitIdentical,
        bool hasScript,
        float distance,
        float engageRange,
        bool targetingUs)
    {
        const int Hostile = 2;

        if (reputation != Hostile || !gitIdentical || !hasScript || distance > engageRange)
        {
            return LocalFightPriority.None;
        }

        return targetingUs ? LocalFightPriority.TargetingUs : LocalFightPriority.Proximity;
    }

    /// <summary>True when <paramref name="challenger"/> should replace <paramref name="incumbent"/>
    /// as this frame's winning candidate: a strictly higher priority always wins; an equal
    /// priority wins only by being strictly closer, so the very first candidate found holds a
    /// tie rather than flapping between two equally-good picks frame to frame.</summary>
    public static bool Beats(
        LocalFightPriority challenger, float challengerDistance,
        LocalFightPriority incumbent, float incumbentDistance) =>
        challenger > incumbent || (challenger == incumbent && challengerDistance < incumbentDistance);
}
