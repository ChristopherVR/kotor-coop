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

    /// <summary>Engine type 8, one of the unnamed sub-codes. Capture-only — no constructor.</summary>
    public const uint Paralyze = 2;

    /// <summary>Engine type 20 (<c>OnApplyKnockdown</c>). Capture-only this pass — the VM
    /// routine that mints it (<c>EffectComplex</c>) has an unverified full parameter list;
    /// see effect-sync.md §4.</summary>
    public const uint Knockdown = 3;

    /// <summary>Engine type 29 (<c>OnApplyMovementSpeedDecrease</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Slow = 4;

    /// <summary>Engine type 8, sub-code 8 (<c>EffectHorrified</c>). Has an apply-side
    /// constructor.</summary>
    public const uint Frightened = 5;

    /// <summary>Engine type 8, one of the unnamed sub-codes. Capture-only — no constructor.</summary>
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
