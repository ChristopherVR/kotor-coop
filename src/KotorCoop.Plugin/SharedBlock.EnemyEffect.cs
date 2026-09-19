namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- client-cast enemy-effect relay (version 70, region 0x4C60..0x4C80, no TotalSize
    //      growth) ---------------------------------------------------------------------------
    //
    // docs/research/enemy-effects.md, coop.ini enemy_effect_sync. Enemies are host-authoritative,
    // so a non-damage effect a CLIENT casts on a mirrored enemy (Stasis, Stun, Fear, Whirlwind,
    // Force Push, Slow, Paralysis, Sleep, a grenade rider) lands on the client's local body only.
    // EnemyEffectHook captures it there, this region hands it to the agent, which sends
    // EnemyEffectPacket (reliable) to the host; the host's agent hands it back through the IN
    // mailbox and EnemyEffectController applies it to the real body through OdysseyEffects.
    //
    // Two 12-byte mailboxes, one per direction, plus one ack dword each. Entry layout:
    //     +0  enemy id (owner/host id space)
    //     +4  head  = kind | serial << 8           (serial is 24 bits, 0 = empty; written LAST)
    //     +8  args  = duration in 100 ms | magnitude << 16
    // Single-entry mailboxes with a handshake rather than rings: 32 bytes is all this stream owns,
    // and a plugin-side queue (EnemyEffectHook) absorbs an area cast that hits four enemies in one
    // frame — the mailbox only paces them at the agent's tick.
    //
    //   plugin -> agent  OUT   "an effect I cast landed on enemy N"
    //   agent  -> plugin OUT ack   "I took serial S; offer the next"
    //   agent  -> plugin IN    "a client reported this effect; apply it to the real enemy"
    //   plugin -> agent  IN applied "I handled serial S"

    /// <summary>Plugin -&gt; agent. Owner-space id of the enemy the effect landed on.</summary>
    public const int OffsetEnemyEffectOutEnemy = 0x4C60;

    /// <summary>Plugin -&gt; agent. <c>kind | serial &lt;&lt; 8</c>; written LAST.</summary>
    public const int OffsetEnemyEffectOutHead = 0x4C64;

    /// <summary>Plugin -&gt; agent. <c>duration/100ms | magnitude &lt;&lt; 16</c>.</summary>
    public const int OffsetEnemyEffectOutArgs = 0x4C68;

    /// <summary>Agent -&gt; plugin. Enemy id to apply the effect to (this host's own id space).</summary>
    public const int OffsetEnemyEffectInEnemy = 0x4C6C;

    /// <summary>Agent -&gt; plugin. <c>kind | serial &lt;&lt; 8</c>; written LAST.</summary>
    public const int OffsetEnemyEffectInHead = 0x4C70;

    /// <summary>Agent -&gt; plugin. <c>duration/100ms | magnitude &lt;&lt; 16</c>.</summary>
    public const int OffsetEnemyEffectInArgs = 0x4C74;

    /// <summary>Agent -&gt; plugin. The OUT serial the agent has taken.</summary>
    public const int OffsetEnemyEffectOutAck = 0x4C78;

    /// <summary>Plugin -&gt; agent. The IN serial the plugin has handled (applied or refused).
    /// Region ends at 0x4C80.</summary>
    public const int OffsetEnemyEffectInApplied = 0x4C7C;
}
