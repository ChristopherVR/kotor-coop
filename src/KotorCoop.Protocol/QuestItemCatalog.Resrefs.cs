namespace KotorCoop.Protocol;

// GENERATED 2026-09-25 from the retail GOG install: every item template (557 in the base
// archives + 436 in modules\*.rim) whose UTI carries Plot = 1, or whose Tag is one of
// QuestItemCatalog.Tags. The party pool keys items by RESREF and carries no Plot bit, so this
// table is how a pool key is recognised as a quest item. Regenerate if the census changes.
public static partial class QuestItemCatalog
{
    /// <summary>Resrefs of every retail item template that is a quest item (Plot flag or a
    /// census tag). 129 entries.</summary>
    public static readonly IReadOnlySet<string> Resrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ancienttome", "bast_temp_saber", "dan13_bluecryst", "dan13_goldcryst", "dan13_grncryst", "dan14_crystal",
        "dan14_crystal001", "dan14_crystal002", "dan14_diary", "dan14ae_blkcryst", "dan14ae_whtcryst", "dan16_key",
        "dan16_key001", "dan_map1", "dan_map2", "dan_starpad", "datapad", "g_head",
        "g_i_asthitem002", "g_i_belt006", "g_i_belt007", "g_i_belt008", "g_i_belt010", "g_i_datapad002",
        "g_i_medeqpmnt003", "g_i_pazdeck", "g_i_pazsidebd001", "g_manaanvis", "g_mandat1", "g_mandat2",
        "g_mandat3", "g_puzdat", "g_shipparts", "geno_datapad", "geno_stealth", "holocron",
        "ip_pltuseitm_041", "k33_itm_sithmdln", "k34_itm_dropping", "k35_itm_sithdata", "k35_itm_sithdevc", "k35_itm_sithpass",
        "k35_itm_uthrpass", "k36_itm_holocron", "k37_itm_ajunta", "k38a_itm_data", "k39_itm_cersaber", "k39_itm_coldgren",
        "k39_itm_firegren", "kas22_datapad", "kas22_resouceid", "kas23_baccasword", "kas24_bowcasbolt", "kas24_datapad",
        "kas24_droidhead", "kas24_joleeamule", "kas24_tachgland", "kas25_datapad", "kas25_kinrathbod", "kas25_mandhelmet",
        "kas25_note", "kas25_swordblade", "kas25_swordhilt", "kas25_wookcrysta", "kas_starpad", "kor37_datapad01",
        "kor37_datapad02", "kor39_itm_datapd", "kor_itm_sithtabl", "kor_starpad", "lev09_portcellke", "lev09_starcellke",
        "lev40_detcard", "lev40_spacesuit", "man28_chum", "man28_envirosuit", "man28_sonic", "man28_toxic",
        "man_starpad", "ptar_appjournal", "ptar_daviksgem", "ptar_permacrete", "ptar_rakghoul001", "ptar_rakghoul002",
        "ptar_rakghoulser", "ptar_rukjournal", "ptar_sbpasscrd", "ptar_shieldcodes", "ptar_sitharmor", "ptar_sithpapers",
        "punk_coldserum", "punk_holocron", "punk_stargem", "sta_light1", "sta_shortlight1", "sta_temp_saber",
        "strangebox", "tar02_sithbaseke", "tar05_bekplan", "tar05_synthodor", "tar08_comppass", "tar09_icebreaker",
        "tar10_accelerato", "tar10_elevcar001", "tar10_garagekey", "tar10_mechid", "tar10_powercell", "tat17_bountywrit",
        "tat17_datapad", "tat17_fizarkprl", "tat17_huntpermit", "tat17_sandperdis", "tat17_vaporator", "tat18_wraidplate",
        "tat20_banthafod", "tat20_gaffichief", "tat20aa_westmap", "tat_starpad", "unk42_key", "unk44_data",
        "unk_researchpad", "w_ddata", "w_repkey", "w_repkey2", "w_rtape", "w_sdatapad",
        "w_sithkey", "w_sithkey2", "w_token",
    };
}
