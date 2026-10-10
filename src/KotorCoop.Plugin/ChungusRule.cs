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

    // The engine's animation ids for a creature model, and the animation each one plays there.
    // They are NOT rows of animations.2da: CSWCAnimBase::ServerToClientAnimation (0x0069F650)
    // turns an id into a row, and for a model whose animbase is not humanoid it takes the
    // branch at 0x0069FCBB, whose jump tables give exactly these (decoded from the binary and
    // then played live on him; docs/research/chungus-boss.md has the whole table). The names
    // in the comments are the animations in his model.
    public const ushort AnimTaunt = 10028;     // -> row 263 ctaunt: the snap
    public const ushort AnimVictory = 10044;   // -> row 260 cvictory: the laugh
    public const ushort AnimBlast = 10118;     // -> row 270 disabled (looping): the power stone's blast
    public const ushort AnimSlam = 10125;      // -> row 43 usecomp: the ground slam
    public const ushort AnimSpin = 10053;      // -> row 258 chturnl: the pirouette
    public const ushort AnimCharge = 10054;    // -> row 259 chturnr: the charge

    // The same six as the script constants NWScript's PlayAnimation takes (the handler's own
    // table at 0x00540C44 turns each into the id above): ANIMATION_FIREFORGET_TAUNT, _VICTORY1,
    // ANIMATION_LOOPING_DEACTIVATE, ANIMATION_FIREFORGET_USE_COMPUTER, _HEAD_TURN_LEFT, _RIGHT.
    // The host plays these, because the script command interrupts a creature that is fighting
    // and a client-side one-shot by id does not (seen live: accepted, but he kept his stance).
    public const int ScriptTaunt = 107;
    public const int ScriptVictory = 108;
    public const int ScriptBlast = 19;
    public const int ScriptSlam = 113;
    public const int ScriptSpin = 100;
    public const int ScriptCharge = 101;

    /// <summary>How long the blast, the one looping animation, is held.</summary>
    public const float BlastSeconds = 1.6f;

    /// <summary>The script constant for a power's wind-up (0 for none), and how long to hold
    /// it when it is a looping animation (0 for a one-shot).</summary>
    public static (int Animation, float Seconds) WindUpScriptAnimation(int power) => power switch
    {
        Snap => (ScriptTaunt, 0f),
        Blast => (ScriptBlast, BlastSeconds),
        Lift or Slow => (ScriptSpin, 0f),
        Gizka => (ScriptSlam, 0f),
        Fright or Entrance => (ScriptVictory, 0f),
        Drain => (ScriptCharge, 0f),
        _ => (0, 0f),
    };

    /// <summary>The animation the boss plays as he starts a power, as the engine id a joiner
    /// replays on its copy of him (0 for none; the blast is looping and reaches a joiner with
    /// the rest of his looping state).</summary>
    public static ushort WindUpAnimation(int power) => power switch
    {
        Snap => AnimTaunt,
        Blast => 0,
        Lift or Slow => AnimSpin,
        Gizka => AnimSlam,
        Fright or Entrance => AnimVictory,
        Drain => AnimCharge,
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
