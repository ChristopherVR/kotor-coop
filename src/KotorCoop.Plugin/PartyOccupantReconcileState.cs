namespace KotorCoop.Plugin;

/// <summary>A host occupant is applied only after exact native recruitment succeeds.</summary>
internal static class PartyOccupantReconcileState
{
    private static readonly uint[] AppliedGeneration = new uint[9];
    private static uint _session;
    public static bool IsApplied(int slot, uint session, uint generation)
        => (uint)slot < 9 && session == _session && generation != 0 && AppliedGeneration[slot] == generation;
    public static void Commit(int slot, uint session, uint generation)
    {
        if ((uint)slot >= 9 || generation == 0) return;
        if (_session != session) { Array.Clear(AppliedGeneration); _session = session; }
        AppliedGeneration[slot] = generation;
    }
    public static void Invalidate() { Array.Clear(AppliedGeneration); _session = 0; }
}
