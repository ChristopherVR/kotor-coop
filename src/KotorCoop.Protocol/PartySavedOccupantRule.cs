namespace KotorCoop.Protocol;

public static class PartySavedOccupantRule
{
    public static bool RequiresBinding(int slot, PartyOccupantIdentity occupant) => occupant.Generation != 0
        && (occupant.Replaced || !IsStock(slot, occupant.Template));

    public static bool IsStock(int slot, string template) => template.Equals(slot switch
    {
        0 => "p_bastilla", 1 => "p_cand", 2 => "p_carth", 3 => "p_hk47", 4 => "p_jolee",
        5 => "p_juhani", 6 => "p_mission", 7 => "p_t3m4", 8 => "p_zaalbar", _ => ""
    }, StringComparison.OrdinalIgnoreCase) && (uint)slot < 9;
}
