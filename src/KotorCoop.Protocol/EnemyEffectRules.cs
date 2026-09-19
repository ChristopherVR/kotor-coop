namespace KotorCoop.Protocol;

/// <summary>
/// Pure decisions for the client-cast enemy-effect relay (docs/research/enemy-effects.md): which
/// <see cref="PeerEffectKind"/> values may be forwarded to the host to land on a REAL enemy
/// body, and the bounds a receiver clamps a report to.
///
/// The set is a strict SUBSET of <see cref="PeerEffectKind"/>: only kinds whose apply-side
/// constructor (<c>OdysseyEffects</c>) is already live-verified (effect-sync.md §11) and that
/// make sense on a hostile creature. Heal and TemporaryHitpoints are buffs (a client's Cure on a
/// mirrored enemy is not a thing to replay), Disguise is a wearer-appearance effect, and Poison
/// is left out because its per-tick damage already reaches the owner as ordinary damage on the
/// peer-damage ledger (effect-sync.md §10.3) — replaying the application too would poison the
/// enemy twice.
/// </summary>
public static class EnemyEffectRules
{
    /// <summary>The longest a forwarded effect may run on the host, milliseconds. The captured
    /// duration is a script-supplied float; a hostile or garbled report must not park an enemy
    /// stunned for an hour.</summary>
    public const uint MaxDurationMs = 60_000;

    /// <summary>Duration the receiver substitutes for a zero (an instant effect such as
    /// ForcePush ignores it).</summary>
    public const uint DefaultDurationMs = 6_000;

    /// <summary>Largest magnitude carried (Slow's percent, at most 100, is the only real
    /// consumer; the field is generic).</summary>
    public const uint MaxMagnitude = 0xFFFF;

    /// <summary>True for a kind the enemy relay forwards.</summary>
    public static bool IsForwardable(PeerEffectKind kind) => kind switch
    {
        PeerEffectKind.Stun => true,
        PeerEffectKind.Paralyze => true,
        PeerEffectKind.Knockdown => true,
        PeerEffectKind.Slow => true,
        PeerEffectKind.Frightened => true,
        PeerEffectKind.Sleep => true,
        PeerEffectKind.Choke => true,
        PeerEffectKind.ForcePush => true,
        PeerEffectKind.Whirlwind => true,
        PeerEffectKind.Confused => true,
        PeerEffectKind.Fear => true,
        PeerEffectKind.Stunned => true,
        _ => false,
    };

    /// <summary>Numeric overload for the agent side, which carries kinds as integers.</summary>
    public static bool IsForwardable(uint kind) =>
        kind is > 0 and <= byte.MaxValue && IsForwardable((PeerEffectKind)kind);

    /// <summary>Clamps a reported duration: zero becomes the default, anything above the
    /// ceiling is cut to it.</summary>
    public static uint ClampDurationMs(uint durationMs) =>
        durationMs == 0 ? DefaultDurationMs : Math.Min(durationMs, MaxDurationMs);

    /// <summary>Clamps a reported magnitude to the wire's 16-bit ceiling.</summary>
    public static uint ClampMagnitude(uint magnitude) => Math.Min(magnitude, MaxMagnitude);
}
