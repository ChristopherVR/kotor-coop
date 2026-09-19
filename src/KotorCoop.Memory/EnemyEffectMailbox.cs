namespace KotorCoop.Memory;

/// <summary>One enemy effect crossing the plugin/agent boundary. <see cref="Serial"/> is 24 bits
/// (1..0xFFFFFF, 0 = empty), <see cref="Kind"/> a <c>PeerEffectKind</c> value.</summary>
public readonly record struct EnemyEffectSample(
    uint Serial, uint EnemyId, uint Kind, uint DurationMs, uint Magnitude);

/// <summary>
/// The packing of one enemy-effect mailbox entry in the shared block (12 bytes:
/// <c>EnemyId</c>, <c>Head = kind | serial &lt;&lt; 8</c>, <c>Args = duration/100ms | magnitude
/// &lt;&lt; 16</c>). The plugin (NativeAOT, cannot reference this assembly) carries its own copy in
/// <c>EnemyEffectMailboxRule</c>; <c>EnemyEffectRelayTests</c> pins the two to each other.
/// </summary>
public static class EnemyEffectMailbox
{
    public const uint SerialMask = 0xFFFFFF;

    /// <summary>Duration is stored in 100 ms units (u16): a forwarded effect is capped at 60 s
    /// (<c>EnemyEffectRules.MaxDurationMs</c>), which is 600 units.</summary>
    public const uint DurationUnitMs = 100;

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

    /// <summary>The serial after <paramref name="serial"/>: 24-bit, never 0 (0 means empty).</summary>
    public static uint NextSerial(uint serial)
    {
        uint next = (serial + 1) & SerialMask;
        return next == 0 ? 1 : next;
    }
}
