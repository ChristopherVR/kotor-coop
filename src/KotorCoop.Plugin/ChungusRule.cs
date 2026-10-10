namespace KotorCoop.Plugin;

/// <summary>What one use of a boss power does to the player it lands on.</summary>
internal readonly record struct BossLanding(
    int Damage, uint Effect = 0, uint DurationMs = 0, uint Magnitude = 0,
    uint SecondEffect = 0, uint SecondDurationMs = 0, uint SecondMagnitude = 0);

/// <summary>
/// The arena boss's powers as this game plays them (docs/research/chungus-boss.md): which
/// animation and sound go with each, and what landing one does to a player. Pure, so the tests
/// link it; nothing here touches the engine.
///
/// ⚠ KEEP IN SYNC with <c>KotorCoop.Protocol.BossPower</c> and the cheat's fields in
/// <c>KotorCoop.Session.Pumps.ChungusPump</c>: the plugin is a NativeAOT x86 library and cannot
/// reference those assemblies, so the numbers are repeated here (ChungusRuleTests pins them).
/// </summary>
internal static class ChungusRule
{
    /// <summary>The blueprint the host spawns, and the name the joiner's copy is found by.</summary>
    public const string Template = "k_coop_chungus";

    // BossPower
    public const int Snap = 1;
    public const int Blast = 2;
    public const int Lift = 3;
    public const int Gizka = 4;
    public const int Fright = 5;
    public const int Drain = 6;
    public const int Slow = 7;
    public const int Entrance = 8;
    public const int Defeat = 9;

    // What the cheat is asked to do (SharedBlock.OffsetCheatAmount).
    public const int OpSpawn = 1;
    public const int OpWindUp = 2;
    public const int OpLand = 3;
    public const int OpDespawn = 4;
    public const int OpAnimation = 5;
    public const int OpSound = 6;

    // SharedBlock.OffsetCheatTarget for OpWindUp / OpLand: the power in the low byte, how hard
    // it lands in the next, then two flags.
    public const uint FlagHitsLocalPlayer = 1u << 16;
    public const uint FlagHost = 1u << 17;

    public static int PowerOf(uint word) => (int)(word & 0xFF);

    public static int StrengthOf(uint word) => (int)((word >> 8) & 0xFF);

    /// <summary>The stock gizka's row in appearance.2da (label Creature_Gizka).</summary>
    public const uint GizkaAppearanceRow = 69;

    // Peer-effect kinds (PeerEffectKindValues), named here so the table below reads plainly.
    private const uint EffectSlow = 4;
    private const uint EffectFrightened = 5;
    private const uint EffectChoke = 7;
    private const uint EffectForcePush = 8;
    private const uint EffectWhirlwind = 9;
    private const uint EffectDisguise = 16;

    // The creature rows of animations.2da, as the 10000-based ids the engine's animation calls take.
    private const ushort AnimVictory = 10260;
    private const ushort AnimTaunt = 10263;
    private const ushort AnimCast1 = 10293;
    private const ushort AnimCast2 = 10294;
    private const ushort AnimCast3 = 10295;
    private const ushort AnimPowerUp = 10296;

    /// <summary>The animation the boss plays as he starts a power (0 for none).</summary>
    public static ushort WindUpAnimation(int power) => power switch
    {
        Snap => AnimTaunt,
        Blast => AnimCast1,
        Lift or Slow => AnimCast3,
        Gizka => AnimCast2,
        Fright or Entrance => AnimVictory,
        Drain => AnimPowerUp,
        _ => 0,
    };

    /// <summary>The sound as he starts a power (empty for none).</summary>
    public static string WindUpSound(int power) => power switch
    {
        Snap or Blast or Gizka or Drain => "k_coop_chcharge",
        Lift or Slow => "k_coop_chwarp",
        Fright => "k_coop_chlaugh",
        Entrance => "k_coop_chintro",
        Defeat => "k_coop_chdie",
        _ => string.Empty,
    };

    /// <summary>The sound as the power lands (empty for none).</summary>
    public static string LandSound(int power) => power switch
    {
        Snap => "k_coop_chsnap",
        Blast => "k_coop_chblast",
        Gizka => "k_coop_chslam",
        Drain => "k_coop_chwarp",
        Entrance => "k_coop_chlaugh",
        _ => string.Empty,
    };

    /// <summary>What a power does to a player it lands on, given their hit points now.</summary>
    public static BossLanding Landing(int power, int strength, int hitPoints, int maxHitPoints)
    {
        int share = Math.Max(maxHitPoints, 0) * Math.Clamp(strength, 0, 100) / 100;
        return power switch
        {
            // Half of what is left, never the last point: the pin holds at 1 anyway.
            Snap => new BossLanding(hitPoints > 1 ? hitPoints / 2 : 0, EffectForcePush),
            Blast => new BossLanding(share, EffectForcePush),
            Lift => new BossLanding(0, EffectWhirlwind, 4_000),
            Gizka => new BossLanding(0, EffectDisguise, 12_000, GizkaAppearanceRow, EffectSlow, 12_000, 50),
            Fright => new BossLanding(0, EffectFrightened, 5_000),
            Drain => new BossLanding(share, EffectChoke, 3_000),
            Slow => new BossLanding(0, EffectSlow, 8_000, (uint)Math.Clamp(strength, 10, 90)),
            _ => default,
        };
    }

    /// <summary>How much the boss heals when the soul stone lands: a twelfth of his maximum,
    /// inside what one heal effect may carry.</summary>
    public static int DrainHeal(int bossMaxHitPoints) => Math.Clamp(bossMaxHitPoints / 12, 1, 999);
}
