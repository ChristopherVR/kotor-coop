namespace KotorCoop.Protocol;

/// <summary>
/// Pure decision rules for <c>inventory_mode</c>/<c>credits_mode</c>
/// (docs/research/personal-inventory.md) — dependency-free, like <see cref="QuestItemCatalog"/>,
/// so every case is bench-testable with no session/bridge/game object in sight.
///
/// The model in one sentence: in <c>shared</c> mode nothing here changes anything (every rule
/// below degenerates to "always true"/"always merge", i.e. byte-for-byte today's behaviour); in
/// <c>personal</c> mode a channel keeps moving ONLY the content both engines must keep agreeing
/// on for the story to stay coherent — quest items, and (by design, see the design doc's §4)
/// nothing of the ordinary purse.
/// </summary>
public static class InventorySyncRule
{
    /// <summary>
    /// Should the party-pool channel (<c>party_pool_sync</c> mode 2,
    /// docs/research/shared-inventory-v2.md) keep mirroring <paramref name="resref"/> between
    /// peers? Always true in shared mode (the whole point of "byte-for-byte unchanged"); in
    /// personal mode, only for a quest item — everything else becomes each engine's own,
    /// unmirrored backpack content.
    /// </summary>
    public static bool ShouldSyncPartyPoolKey(bool inventoryPersonal, string? resref) =>
        !inventoryPersonal || QuestItemCatalog.IsQuestItemTag(resref);

    /// <summary>
    /// Should the progression channel's gold LEDGER (docs/research/progression-sync.md,
    /// <c>Pumps.PartyPump.PumpProgression</c>) fold a peer's earn/spend contribution into the
    /// one shared purse? True in shared mode (today's one-purse behaviour, unchanged); false in
    /// personal mode — each side's own gold reading is left exactly as its own engine produced
    /// it, spent and earned locally. See the design doc's §4 for the one thing this
    /// deliberately does NOT solve this pass (a dialogue/script gold reward reaching only the
    /// conversation-driving player in personal mode) and why.
    /// </summary>
    public static bool ShouldMergeCredits(bool creditsPersonal) => !creditsPersonal;
}
