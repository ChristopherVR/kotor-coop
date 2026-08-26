namespace KotorCoop.Memory;

/// <summary>The plugin's verdicts, mirroring <c>SharedBlock.CheatStatus*</c>.</summary>
public static class CheatStatus
{
    public const int Applied = 1;
    public const int Forwarded = 2;
    public const int NoWorld = -1;
    public const int NoTarget = -2;
    public const int Refused = -3;
    public const int EngineRefused = -4;
}
