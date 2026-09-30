namespace KotorCoop.Memory;

public static class PartyScriptSaveSafetyRule
{
    public static bool CanSave(bool readable, int controlledSlot) => readable && controlledSlot == -1;
}
