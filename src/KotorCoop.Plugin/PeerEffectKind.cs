namespace KotorCoop.Plugin;

/// <summary>
/// The peer-effect wire identity's numeric values, for the plugin side — a plain
/// <c>uint</c>-valued static class rather than an enum shared with <c>KotorCoop.Protocol</c>,
/// because this is NativeAOT plugin code and cannot reference that assembly. Values MUST match
/// <c>KotorCoop.Protocol.PeerEffectKind</c> exactly; see docs/research/effect-sync.md for the
/// engine effect-type/sub-code each one stands for and which have a real apply-side
/// constructor (<see cref="OdysseyEffects"/>) versus capture-only.
/// </summary>
internal static class PeerEffectKindValues
{
    public const uint None = 0;

    /// <summary>Engine type 8, sub-code 3 (<c>EffectDroidStun</c>) — droid-scoped; see
    /// effect-sync.md §3.1. Has an apply-side constructor.</summary>
    public const uint Stun = 1;

    /// <summary>Engine type 8, sub-code 5 (routine 148, <c>EffectParalyze</c>). Has an
    /// apply-side constructor as of effect-sync.md §7 (2026-08-22).</summary>
    public const uint Paralyze = 2;

    /// <summary>Engine type 20 (<c>OnApplyKnockdown</c>, routine 134 <c>EffectKnockdown</c>).
    /// Has an apply-side constructor as of effect-sync.md §7 (2026-08-22) — the shared
    /// <c>ExecuteCommandEffectComplex</c> handler dispatches on the calling routine number,
    /// not a popped script argument, so it is safe to call with zero pushed arguments.</summary>
    public const uint Knockdown = 3;

    /// <summary>Engine type 29 (<c>OnApplyMovementSpeedDecrease</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Slow = 4;

    /// <summary>Engine type 8, sub-code 8 (<c>EffectHorrified</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Frightened = 5;

    /// <summary>Engine type 8, sub-code 6 (routine 154, <c>EffectSleep</c>). Has an apply-side
    /// constructor as of effect-sync.md §7 (2026-08-22).</summary>
    public const uint Sleep = 6;

    /// <summary>Engine type 8, sub-code 7 (<c>EffectChoke</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Choke = 7;

    /// <summary>Engine type 60 (<c>OnApplyForcePush</c>). Has an apply-side constructor.</summary>
    public const uint ForcePush = 8;

    /// <summary>Engine type 8, sub-code 10 (<c>EffectWhirlWind</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Whirlwind = 9;

    /// <summary>Engine type 39 (<c>OnApplyHeal</c>, routine 78 <c>EffectHeal</c>). Has an
    /// apply-side constructor as of effect-sync.md §10 (2026-09-02) — one <c>int</c>
    /// parameter, applied instant.</summary>
    public const uint Heal = 10;

    /// <summary>Engine type 15 (<c>OnApplyTemporaryHitpoints</c>, routine 314
    /// <c>EffectTemporaryHitPoints</c>). Has an apply-side constructor as of effect-sync.md
    /// §10 (2026-09-02) — one <c>int</c> parameter, applied durable/temporary.</summary>
    public const uint TemporaryHitpoints = 11;

    /// <summary>Engine type 8, sub-code 1 (routine 157 <c>EffectConfused</c>). Has an
    /// apply-side constructor as of effect-sync.md §10 (2026-09-02), same
    /// <c>ExecuteCommandEffectComplex</c> family as Knockdown/Paralyze/Sleep.</summary>
    public const uint Confused = 12;

    /// <summary>Engine type 8, sub-code 2 (routine 158 <c>EffectFrightened</c> — the real one;
    /// <see cref="Frightened"/> is <c>EffectHorrified</c>). Apply-side constructor as of
    /// effect-sync.md §12 (2026-09-18), <c>ExecuteCommandEffectComplex</c> family.</summary>
    public const uint Fear = 13;

    /// <summary>Engine type 8, sub-code 4 (routine 161 <c>EffectStunned</c> — the general,
    /// non-droid stun; <see cref="Stun"/> is <c>EffectDroidStun</c>). Apply-side constructor as
    /// of effect-sync.md §12 (2026-09-18), <c>ExecuteCommandEffectComplex</c> family.</summary>
    public const uint Stunned = 14;

    /// <summary>Engine type 35 (<c>OnApplyPoison</c>, routine 250 <c>EffectPoison</c>). One
    /// <c>int nPoisonType</c> parameter (the magnitude), applied durable. Apply gated by
    /// coop.ini <c>poison_sync</c> (default OFF) — effect-sync.md §12.</summary>
    public const uint Poison = 15;

    /// <summary>Engine type 62 (<c>OnApplyDisguise</c>, routine 463 <c>EffectDisguise</c>). One
    /// <c>int nDisguiseAppearance</c> parameter (the magnitude = appearance.2da row), popped
    /// by its <c>ExecuteCommandEffectComplex</c> case. Apply gated by coop.ini
    /// <c>disguise_sync</c> (default OFF) — effect-sync.md §12.</summary>
    public const uint Disguise = 16;

    /// <summary>The highest kind with an apply-side constructor — what
    /// <c>CheatController.ApplyEffect</c>'s range check and <c>OdysseyEffects</c>' switch
    /// both stop at. One number, so adding a kind means changing it here and nowhere else on
    /// the plugin side.</summary>
    public const uint Max = Disguise;
}
