namespace KotorCoop.Session;

/// <summary>
/// The receiving half of docs/research/enemy-materialisation.md §6's automatic wire had one
/// wasted-round-trip problem: the host publishes a <c>MaterialisedLootPacket</c> for every
/// enemy that dies, unconditionally, and the OVERWHELMINGLY common case is an owner id this
/// instance never materialised a stand-in for at all — the peer holds the REAL creature under
/// the same GIT id, which already rolled its own equivalent loot. Before this rule,
/// <see cref="Pumps.LootPump"/> queued a give for every item regardless, and
/// <c>CoopCommandDispatcher.DrainAutomaticLoot</c> spent the single-slot giveloot cheat channel
/// on it anyway, only to have <c>CheatController.GiveLoot</c> refuse it after the round trip —
/// measured live 2026-09-02, six such refusals in one second on a client rejoining after a
/// wipe reload.
///
/// This rule decides whether a give is even worth queueing, using the set of owner ids the
/// plugin's own <c>MaterialisedEnemies</c> table currently maps
/// (<see cref="Memory.IEnemyBridge.ReadMaterialisedOwnerIds"/>, SharedBlock.
/// OffsetMaterialisedOwnerIds, version 65) — the SAME table <c>CheatController.GiveLoot</c>
/// would otherwise have consulted anyway, just read one hop earlier, before a round trip is
/// spent finding out.
///
/// Pure decision, deliberately conservative: a NULL set (no plugin attached — see that
/// method's own remarks on why this is the only case that means "don't know") means UNSURE,
/// and an unsure give is always queued — the plugin's own refusal remains the correct backstop
/// when this instance genuinely cannot tell. Only a real, if possibly empty, set licenses
/// skipping a give by membership.
/// </summary>
internal static class MaterialisedLootFilterRule
{
    /// <summary>
    /// Whether a materialised-loot give for <paramref name="ownerEnemyId"/> is worth queueing.
    /// <paramref name="materialisedOwnerIds"/> is this instance's current snapshot of
    /// <c>MaterialisedEnemies</c>'s own table (null = unsure, see the type's own doc).
    ///
    /// NEVER call this for a dialogue-grant give (<c>AgentWellKnownIds.LocalPlayerObjectId</c>)
    /// — that give targets the local PC directly, needs no translation, and must never be
    /// filtered; <see cref="Pumps.LootPump.PumpMaterialisedLoot"/> keeps it on its own,
    /// unfiltered loop for exactly that reason and never reaches this rule at all.
    /// </summary>
    public static bool ShouldQueue(uint ownerEnemyId, IReadOnlySet<uint>? materialisedOwnerIds) =>
        materialisedOwnerIds is null || materialisedOwnerIds.Contains(ownerEnemyId);
}
