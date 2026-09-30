namespace KotorCoop.Plugin;
public enum PartyOccupantAction { Unchanged, Hold, RecruitInactive }
internal static class PartyOccupantRule
{
    public static PartyOccupantAction Decide(uint generation, bool templateKnown, bool alreadyApplied, bool active, bool recruitmentEnabled)
        => generation == 0 || alreadyApplied ? PartyOccupantAction.Unchanged
            : templateKnown && !active && recruitmentEnabled ? PartyOccupantAction.RecruitInactive
            : PartyOccupantAction.Hold;
}
