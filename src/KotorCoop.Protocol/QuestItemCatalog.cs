namespace KotorCoop.Protocol;

/// <summary>
/// Pure classification: is a K1 item tag/resref a plot/quest item — one that must keep being
/// mirrored between peers even when <c>inventory_mode = personal</c> turns off ordinary
/// backpack mirroring (docs/research/personal-inventory.md).
///
/// A quest item is EITHER carrying the engine's own Plot flag (<c>CSWSItem+0x108</c>,
/// <c>UTI.Plot</c> — see <see cref="IsQuestItem"/>) OR named in <see cref="Tags"/>, the census
/// this file mirrors from docs/research/key-items-and-solo.md §7 — every literal tag that
/// document's own abstract-stack-interpreter walk found passed to
/// <c>GetItemPossessedBy(object, string)</c> (NWScript routine 30) anywhere in K1's 1,784
/// BIF-global scripts plus every module script, restricted to tags the source document could
/// name LITERALLY. That document's own §7.1 notes several call sites resolve a WILDCARD group
/// of tags it could not individually enumerate from a static read alone (e.g. <c>tat17_*</c>,
/// <c>ptar_*journal</c>, <c>kas24_*</c> beyond the one member it did name) — those groups are
/// deliberately NOT expanded here (there is nothing to expand them FROM without re-running that
/// document's own script walk), which is exactly why the Plot flag is the PRIMARY, robust
/// signal and this list is a supplementary allowlist, not the whole truth. A modded or
/// wildcard-only item this list misses still keeps working correctly as long as its own UTI
/// carries <c>Plot = 1</c>, which the vast majority of K1's own quest items already do (the one
/// confirmed example in the source census, <c>man28_envirosuit</c>, is itself
/// <c>BaseItem 64, Plot 1</c> — docs/research/manaan-hrakert-rift.md).
///
/// This class is dependency-free (no engine/bridge/session types), like every other pure rule
/// in <c>KotorCoop.Protocol</c> — the plugin's own compile-include trick
/// (docs/ARCHITECTURE.md) can pull it into the NativeAOT build unchanged if a future pass wants
/// the plugin side to classify locally too.
/// </summary>
public static partial class QuestItemCatalog
{
    /// <summary>
    /// The literal tags from key-items-and-solo.md §7.2 (the four store-purchasable gates) and
    /// §7.3 (quest-given/found-only plot items), lower-cased. See the class doc comment for
    /// what is deliberately excluded (wildcard-truncated groups the source document could not
    /// enumerate).
    /// </summary>
    public static readonly IReadOnlySet<string> Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // §7.2 — buyable plot gates (rakghoulserum/ptar_permacrete/tat20_banthafod sold on
        // Taris/Tatooine; the g_i_belt0xx Korriban stealth-shortcut set; geno_stealth is the
        // same gate's own check tag, not sold).
        "rakghoulserum", "ptar_permacrete", "tat20_banthafod",
        "g_i_belt006", "g_i_belt007", "g_i_belt008", "g_i_belt010", "geno_stealth",

        // 2026-09-25 GetItemPossessedBy census cross-check: tags a quest script tests for
        // but whose UTI carries no Plot flag, so the generated resref table missed them.
        // Each is found-only (no store sells it). Personal mode must share them, or one
        // player's pickup leaves the other engine's check false.
        "tar10_accelerator", "tar10_elevcard", "k37_itm_freednf1", "k37_itm_freednf2",
        "k37_itm_freedont", "w_diary", "w_rdatapad", "w_sdatapadm", "w_tape",
        "tat17_wraidplate", "tat18_dragonprl", "tat20_gaffistick", "kas24_hortberr",
        "kas25_datapad1", "kas25_datapad2", "punk_conch", "ebo_spice", "dan14_nothing",

        // §7.3 — main quest / found-only plot items.
        "kor_starpad", "dan_map1", "dan_map2", "lev40_spacesuit", "tar09_icebreaker",
        "ptar_sitharmor", "ptar_sithpapers", "bast_temp_saber", "sta_temp_saber",
        "sta_light1", "sta_shortlight1", "g_shipparts", "unk42_key", "punk_holocron",
        "ancienttome", "g_head", "unk_researchpad", "holocron",

        // Manaan: the Sunry trial evidence and the Hrakert Rift envirosuit set.
        "g_mandat1", "g_mandat2", "g_mandat3", "g_manaanvis", "g_puzdat",
        "man28_envirosuit", "man28_sonic", "man28_toxic", "man28_environment",

        // Korriban academy.
        "k33_itm_sithmdln", "k36_itm_holocron", "kor37_datapad01", "kor37_datapad02",
        "kor_itm_sithtabl",

        // Kashyyyk.
        "kas22_resouceid", "kas23_baccasword", "kas24_joleeamule",

        // Taris.
        "ptar_sbpasscrd", "ptar_shieldcodes", "tar05_synthodor", "tar08_comppass", "tar_spice",

        // Tatooine.
        "tat20aa_westmap",

        // Dantooine.
        "dan13_plotcrys", "dan14ae_blkcrystal", "dan14ae_whtcrystal", "dan16_key",
    };

    /// <summary>True when <paramref name="resref"/> is in the census (case-insensitive). This
    /// is the check <c>Pumps.PartyPoolPumpV2</c> actually gates on: a <c>PartyPoolStackKey</c>
    /// carries only a resref, never a live Plot-flag bit (docs/research/party-pool-sync.md
    /// §2.1's key shape) — see <see cref="IsQuestItem"/> for the fuller two-signal rule a
    /// caller with the item's own Plot flag in hand should prefer instead.</summary>
    public static bool IsQuestItemTag(string? resref) =>
        !string.IsNullOrEmpty(resref) && (Resrefs.Contains(resref) || Tags.Contains(resref));

    /// <summary>The full rule: a quest item is the engine's own Plot flag OR a census tag —
    /// either one is sufficient. Prefer this over <see cref="IsQuestItemTag"/> alone whenever
    /// the caller actually has the item's Plot flag available (this codebase's
    /// <c>PartyPoolStackKey</c> currently does not — see that type's own remarks).</summary>
    public static bool IsQuestItem(bool plotFlag, string? resref) =>
        plotFlag || IsQuestItemTag(resref);
}
