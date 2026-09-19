namespace KotorCoop.Plugin;

/// <summary>
/// Pure packing and vocabulary for the enemy-effect relay (docs/research/enemy-effects.md), kept
/// out of the unsafe engine code so a test can pin it. The plugin is NativeAOT and cannot
/// reference <c>KotorCoop.Protocol</c> / <c>KotorCoop.Memory</c>, so this is a COPY of
/// <c>KotorCoop.Memory.EnemyEffectMailbox</c> (packing) and
/// <c>KotorCoop.Protocol.EnemyEffectRules.IsForwardable</c> (vocabulary) — one number in two
/// files, and <c>EnemyEffectRelayTests</c> asserts every value here equals its twin.
/// </summary>
internal static class EnemyEffectMailboxRule
{
    public const uint SerialMask = 0xFFFFFF;
    public const uint DurationUnitMs = 100;

    /// <summary>Wire ceiling on a forwarded duration (<c>EnemyEffectRules.MaxDurationMs</c>).</summary>
    public const uint MaxDurationMs = 60_000;

    public static uint PackHead(uint serial, uint kind) => ((serial & SerialMask) << 8) | (kind & 0xFF);

    public static uint SerialOf(uint head) => head >> 8;

    public static uint KindOf(uint head) => head & 0xFF;

    public static uint PackArgs(uint durationMs, uint magnitude)
    {
        uint units = Math.Min((durationMs + DurationUnitMs - 1) / DurationUnitMs, 0xFFFF);
        return units | (Math.Min(magnitude, 0xFFFF) << 16);
    }

    public static uint DurationMsOf(uint args) => (args & 0xFFFF) * DurationUnitMs;

    public static uint MagnitudeOf(uint args) => args >> 16;

    public static uint NextSerial(uint serial)
    {
        uint next = (serial + 1) & SerialMask;
        return next == 0 ? 1 : next;
    }

    /// <summary>The kinds the enemy relay forwards — the twelve non-buff, non-poison
    /// <c>PeerEffectKind</c> values (Stun 1, Paralyze 2, Knockdown 3, Slow 4, Frightened 5,
    /// Sleep 6, Choke 7, ForcePush 8, Whirlwind 9, Confused 12, Fear 13, Stunned 14). Heal (10),
    /// TemporaryHitpoints (11), Poison (15) and Disguise (16) are excluded; see
    /// <c>EnemyEffectRules</c>.</summary>
    public static bool IsForwardable(uint kind) => kind is (>= 1 and <= 9) or (>= 12 and <= 14);

    /// <summary>Clamps a captured duration into what the wire and the apply side accept.</summary>
    public static uint ClampDurationMs(uint durationMs) =>
        durationMs == 0 ? 6_000 : Math.Min(durationMs, MaxDurationMs);
}
