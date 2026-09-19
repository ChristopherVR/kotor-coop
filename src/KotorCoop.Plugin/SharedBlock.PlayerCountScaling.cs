namespace KotorCoop.Plugin;

// The resolved player-count scaling channel (docs/research/player-count-scaling.md): the
// agent resolves coop.ini enemy_health / enemy_damage / xp_rate / store_stock to four
// percents every tick (the host's adopted settings, the live player count) and pushes the
// two the PLUGIN acts on here. See SharedBlockView.Session.cs for the readers,
// EnemyScalingRule.cs for the unpacking and arithmetic, EnemyScalingController.cs for the
// hit-point write and PhantomHitHook.cs for the damage rewrite.
internal partial struct SharedBlock
{
    // ---- resolved player-count scaling (version 69, region 0x4C38..0x4C40, no TotalSize
    // growth) ----------------------------------------------------------------------------
    //
    // Rides the free tail after the galaxy-map departure relay (version 67, 0x4C00..0x4C20)
    // — the same "no new 0x100 boundary when the gap fits" choice version 66 made. Two packed
    // dwords rather than four u16s for the same single-aligned-store reasoning
    // OffsetBehaviourOverrideMask's own comment gives: a reader never sees one session's
    // health percent beside another's damage percent. 0 in a half (a fresh block, or what
    // SessionScopedBlockLevels resets it to on disconnect) means "no session has resolved
    // this" and EnemyScalingRule reads it as vanilla — nothing is applied.

    /// <summary>Agent -&gt; plugin. Enemy HEALTH percent in the low 16 bits, enemy DAMAGE
    /// percent in the high 16 — the two this plugin acts on. See
    /// <see cref="EnemyScalingRule.HealthPercent"/>/<see cref="EnemyScalingRule.DamagePercent"/>.</summary>
    public const int OffsetScalingCombat = 0x4C38;

    /// <summary>Agent -&gt; plugin. XP rate percent in the low 16 bits, store stock percent in
    /// the high 16. Both are applied agent-side; carried here for the status line only.</summary>
    public const int OffsetScalingEconomy = 0x4C3C;
}
