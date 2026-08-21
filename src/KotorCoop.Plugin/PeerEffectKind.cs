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
}
