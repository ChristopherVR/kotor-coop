namespace KotorCoop.Session;

/// <summary>Decode the saved creature health offset without changing its saved fields.</summary>
public static class SavedCharacterHealthRule
{
    // GOG SaveStats 0x5B1FE2..0x5B2026 stores CurrentHitPoints as
    // stored HitPoints - derived MaxHitPoints + native current HP. The player load
    // branch at 0x5B130D..0x5B1335 adds derived maximum minus stored maximum back.
    // The separately serialized MaxHitPoints is that derived maximum at save time.
    public static bool IsLiving(int currentOffset, int storedMaximum, int derivedMaximum)
    {
        if (currentOffset < short.MinValue || currentOffset > short.MaxValue
            || storedMaximum < 0 || storedMaximum > short.MaxValue
            || derivedMaximum <= 0 || derivedMaximum > short.MaxValue)
            return false;
        int health = currentOffset - storedMaximum + derivedMaximum;
        return health > 0 && health <= derivedMaximum;
    }
}
