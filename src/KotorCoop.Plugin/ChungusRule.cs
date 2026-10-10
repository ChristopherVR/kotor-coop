namespace KotorCoop.Plugin;

/// <summary>What a player hit by a power is shown, all of it the game's own visual effects.</summary>
internal readonly record struct BossSight(
    uint Impact, uint Beam = 0, uint BeamMs = 0, uint Lasting = 0, uint LastingMs = 0, bool Shake = false);

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
    public const int Blink = 10;

    // What the cheat is asked to do (SharedBlock.OffsetCheatAmount).
    public const int OpSpawn = 1;
    public const int OpWindUp = 2;
    public const int OpLand = 3;
    public const int OpDespawn = 4;
    public const int OpAnimation = 5;
    public const int OpSound = 6;
    public const int OpBlink = 7;

    // SharedBlock.OffsetCheatTarget for OpWindUp / OpLand: the power in the low byte, how hard
    // it lands in the next, then two flags.
    public const uint FlagHitsLocalPlayer = 1u << 16;
    public const uint FlagHost = 1u << 17;

    public static int PowerOf(uint word) => (int)(word & 0xFF);

    public static int StrengthOf(uint word) => (int)((word >> 8) & 0xFF);

    /// <summary>The stock gizka's row in appearance.2da (label Creature_Gizka).</summary>
    public const uint GizkaAppearanceRow = 69;

    // Peer-effect kinds (PeerEffectKindValues), named here so the table below reads plainly.
    private const uint EffectParalyze = 2;
    private const uint EffectSlow = 4;
    private const uint EffectFrightened = 5;
    private const uint EffectChoke = 7;
    private const uint EffectForcePush = 8;
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
    public const ushort AnimSlam = 10125;      // -> row 43 usecomp: the hammer blow (reality)
    public const ushort AnimSpin = 10053;      // -> row 258 chturnl: the open hand raised (space, time)
    public const ushort AnimCharge = 10054;    // -> row 259 chturnr: the reach and clench (soul)

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

    // His voice is the game's Hutt voice set (the blueprint names it, so the engine plays his
    // attack grunts, his pain and his death by itself), and his powers use the Force powers' own
    // sounds and effects. Only the snap is a sound of the mod's own (k_coop_chsnap.wav).

    /// <summary>The sound as he starts a power (empty for none): a file of the game's, by name.</summary>
    public static string WindUpSound(int power) => power switch
    {
        Snap or Blast => "v_pro_frcaura",
        Lift or Slow or Blink => "v_imp_mindfrc",
        Gizka => "c_hutt_atk1",
        Fright => "c_hutt_bat1",
        Drain => "v_imp_choke",
        Entrance => "c_hutt_bat2",
        _ => string.Empty,
    };

    /// <summary>The sound as the power lands (empty for none). Most landings are heard through
    /// the visual effect the game plays, which carries its own sound.</summary>
    public static string LandSound(int power) => power switch
    {
        Snap => "k_coop_chsnap",
        Entrance => "al_ot_rumble",
        _ => string.Empty,
    };

    // visualeffects.2da ids (the number in the table's first column), all the game's own.
    public const uint VfxForceAura = 1011;          // VFX_PRO_FORCE_AURA
    public const uint VfxForcePush = 1014;          // VFX_IMP_FORCE_PUSH
    public const uint VfxWhirlwindImpact = 1018;    // VFX_IMP_FORCE_WHIRLWIND
    public const uint VfxHeal = 1019;               // VFX_IMP_HEAL
    public const uint VfxLightning = 1021;          // VFX_PRO_LIGHTNING_L
    public const uint VfxSuppress = 1032;           // VFX_IMP_SUPPRESS_FORCE
    public const uint VfxMindMastery = 1034;        // VFX_IMP_MIND_MASTERY
    public const uint VfxHorror = 1042;             // VFX_IMP_HORROR
    public const uint VfxPolymorph = 1046;          // VFX_IMP_POLYMORPH
    public const uint VfxDrain = 1009;              // VFX_PRO_DRAIN
    public const uint VfxBeamDrain = 2029;          // VFX_BEAM_DRAIN_LIFE
    public const uint VfxBeamLightning = 2038;      // VFX_BEAM_LIGHTNING_DARK_L
    public const uint VfxForceWave = 3001;          // VFX_FNF_FORCE_WAVE
    public const uint VfxGravity = 3018;            // VFX_FNF_GRAVITY_GENERATOR_2
    public const uint VfxScreenShake = 6002;        // VFX_IMP_SCREEN_SHAKE

    /// <summary>The effect played on him as he starts a power (0 for none).</summary>
    public static uint WindUpEffect(int power) => power switch
    {
        Snap or Blast or Drain => VfxForceAura,
        Fright => VfxMindMastery,
        _ => 0,
    };

    /// <summary>The effect played on him as the power lands (0 for none).</summary>
    public static uint LandEffectOnBoss(int power) => power switch
    {
        Snap => VfxForceWave,
        Entrance => VfxGravity,
        _ => 0,
    };

    /// <summary>What a player who is hit sees: an effect on them, a beam from his hand to them
    /// and how long it is drawn, and an effect that stays on them for a while.</summary>
    public static BossSight Sight(int power) => power switch
    {
        Snap => new BossSight(VfxForcePush, Shake: true),
        Blast => new BossSight(VfxLightning, VfxBeamLightning, 1_100),
        Lift => new BossSight(VfxWhirlwindImpact, Shake: true),
        Gizka => new BossSight(VfxPolymorph),
        Fright => new BossSight(VfxHorror),
        Drain => new BossSight(VfxDrain, VfxBeamDrain, 1_500),
        Slow => new BossSight(VfxSuppress),
        Entrance => new BossSight(0, Shake: true),
        _ => default,
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
            // Thrown down where the space stone put them (the agent moves them just before).
            Lift => new BossLanding(0, EffectForcePush),
            Gizka => new BossLanding(0, EffectDisguise, 12_000, GizkaAppearanceRow, EffectSlow, 12_000, 50),
            Fright => new BossLanding(0, EffectFrightened, 5_000),
            Drain => new BossLanding(share, EffectChoke, 3_000),
            // Time stops: nothing they can do until it starts again.
            Slow => new BossLanding(0, EffectParalyze, TimeStopMs),
            _ => default,
        };
    }

    /// <summary>How long time stays stopped. KEEP IN SYNC with BossPowers.TimeStopMs.</summary>
    public const uint TimeStopMs = 3_500;

    /// <summary>The effect played on him where he arrives when he blinks.</summary>
    public const uint BlinkEffect = VfxForceAura;

    /// <summary>How much the boss heals when the soul stone lands: a twelfth of his maximum,
    /// inside what one heal effect may carry.</summary>
    public static int DrainHeal(int bossMaxHitPoints) => Math.Clamp(bossMaxHitPoints / 12, 1, 999);
}
