namespace KotorCoop.Protocol;

public static class PartySavedOccupantRule
{
    public static bool RequiresBinding(int slot, PartyOccupantIdentity occupant) => occupant.Generation != 0
        && (occupant.Replaced || !IsStock(slot, occupant.Template));

    // The tutorial legitimately recruits Trask into Bastila's later slot. Treat
    // his exact first admission as stock; a replacement still requires saved proof.
    public static bool IsStock(int slot, string template) => (slot == 0
        && template.Equals("end_trask", StringComparison.OrdinalIgnoreCase)) || template.Equals(slot switch
    {
        0 => "p_bastilla", 1 => "p_cand", 2 => "p_carth", 3 => "p_hk47", 4 => "p_jolee",
        5 => "p_juhani", 6 => "p_mission", 7 => "p_t3m4", 8 => "p_zaalbar", _ => ""
    }, StringComparison.OrdinalIgnoreCase) && (uint)slot < 9;
}
