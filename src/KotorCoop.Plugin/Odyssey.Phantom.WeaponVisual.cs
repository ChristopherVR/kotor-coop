namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // CSWCCreature::SetCombatState(int), verified in the retail binary:
    // 0x6182AB reads twin+0x138, 0x6182B1 writes it, then both equipped
    // weapons receive the native power-state update; ret 4 at 0x6183B7/0x6183C7.
    // This is the CLIENT visual setter, not CSWSCreature::SetCombatState.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetVisualCombatStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x006182A0;

    /// <summary>
    /// Keep a fighting peer's rendered weapons powered. A phantom has no local
    /// attack round, so server-to-client updates can stand its weapons down even
    /// while its owner's attack animations are being replayed. Do not manufacture
    /// a server combat round or damage; only update the existing rendered twin.
    /// </summary>
    public static void MirrorWeaponCombatVisual(nint creature, bool inCombat)
    {
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != 0x0074F5F8 || *(nint*)(twin + TwinAnimBase) == 0)
        {
            return;
        }

        int wanted = inCombat ? 1 : 0;
        if (*(int*)(twin + 0x138) != wanted)
        {
            SetVisualCombatStateFn(twin, wanted);
        }
    }
}
