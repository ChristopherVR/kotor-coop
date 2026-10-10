using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The fields of the arena boss's cheat request (<c>CheatKinds.Chungus</c>): what is asked rides
/// in the amount, and a power, how hard it lands and two flags in the target word.
///
/// ⚠ KEEP IN SYNC with the plugin's <c>ChungusRule</c> (a NativeAOT x86 DLL cannot reference this
/// assembly, so the values are duplicated; ChungusRuleTests pins both).
/// </summary>
public static class ChungusCheat
{
    public const int OpSpawn = 1;
    public const int OpWindUp = 2;
    public const int OpLand = 3;
    public const int OpDespawn = 4;
    public const int OpAnimation = 5;
    public const int OpSound = 6;

    public const uint FlagHitsLocalPlayer = 1u << 16;
    public const uint FlagHost = 1u << 17;

    /// <summary>The word a wind-up or landing request carries.</summary>
    public static uint Word(BossCue cue, bool hitsLocalPlayer, bool isHost) =>
        (uint)cue.Power | ((uint)cue.Strength << 8)
        | (hitsLocalPlayer ? FlagHitsLocalPlayer : 0)
        | (isHost ? FlagHost : 0);
}
