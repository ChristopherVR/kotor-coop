using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The four player-count scaling SETTINGS as one value — coop.ini <c>enemy_health</c>,
/// <c>enemy_damage</c>, <c>xp_rate</c>, <c>store_stock</c> (docs/research/player-count-scaling.md).
/// Each is <see cref="PlayerCountScalingRule.Auto"/> or a fixed percent already clamped by
/// <see cref="PlayerCountScalingRule.ParseSetting"/>.
/// </summary>
public sealed record PlayerCountScalingSettings(int EnemyHealth, int EnemyDamage, int XpRate, int StoreStock)
{
    /// <summary>The code fallbacks — the same numbers <c>CoopConfig</c>'s record defaults,
    /// its parse fallbacks and <c>BehaviourDigest.Default</c> carry (docs/CONFIG.md footnote
    /// ³): enemy health and damage at 100 because their engine writes are NEEDS-LIVE, XP and
    /// store stock on the auto curve.</summary>
    public static PlayerCountScalingSettings Default { get; } = new(
        PlayerCountScalingRule.Vanilla, PlayerCountScalingRule.Vanilla,
        PlayerCountScalingRule.Auto, PlayerCountScalingRule.Auto);
}

/// <summary>
/// The RESOLVED player-count scaling for this session: the four percents actually applied
/// this tick, recomputed by <see cref="Update"/> from the effective settings (the host's
/// adopted value where <see cref="SessionOverrides"/> has one, else this install's own) and
/// the live player count (this instance plus every connected peer). Owned by
/// <c>CoopAgent</c>, read by <c>PartyPump</c> (XP), <c>StorePumpV2</c> (stock) and pushed into
/// the plugin's shared block (enemy health and damage) whenever it changes.
///
/// Pure state — nothing here touches the game — so a pump can be handed one in a test and
/// driven through any player count.
/// </summary>
public sealed class PlayerCountScaling
{
    private PlayerCountScalingSettings _local;

    public PlayerCountScaling(PlayerCountScalingSettings local)
    {
        _local = local;
        Update(SessionOverrides.None, 1);
    }

    /// <summary>Percent applied to an enemy's hit points on first sighting. 100 = vanilla.</summary>
    public int EnemyHealthPercent { get; private set; } = PlayerCountScalingRule.Vanilla;

    /// <summary>Percent applied to damage an enemy lands on a player body. 100 = vanilla.</summary>
    public int EnemyDamagePercent { get; private set; } = PlayerCountScalingRule.Vanilla;

    /// <summary>Percent applied to the host's own experience DELTA before the max-merge.</summary>
    public int XpRatePercent { get; private set; } = PlayerCountScalingRule.Vanilla;

    /// <summary>Percent applied to a store's finite stock on the host's first open of it.</summary>
    public int StoreStockPercent { get; private set; } = PlayerCountScalingRule.Vanilla;

    /// <summary>The player count the current percents were resolved for.</summary>
    public int PlayerCount { get; private set; } = 1;

    /// <summary>The effective settings behind the current percents (overrides applied).</summary>
    public PlayerCountScalingSettings Effective { get; private set; } = PlayerCountScalingSettings.Default;

    /// <summary>Bumped on every change of any percent, so a caller that pushes the values
    /// somewhere (the plugin block, a log line) can compare one number.</summary>
    public uint Generation { get; private set; }

    /// <summary>Replaces this install's own settings (a reconnect with a re-read coop.ini).</summary>
    public void SetLocal(PlayerCountScalingSettings local) => _local = local;

    /// <summary>
    /// Recomputes the four percents. <paramref name="playerCount"/> is the number of players
    /// in the session INCLUDING this instance (1 = alone, which is vanilla on the auto curve);
    /// anything below 1 is treated as 1. Returns true when a percent changed.
    /// </summary>
    public bool Update(SessionOverrides overrides, int playerCount)
    {
        if (playerCount < 1)
        {
            playerCount = 1;
        }

        PlayerCountScalingSettings effective = new(
            overrides.EnemyHealth ?? _local.EnemyHealth,
            overrides.EnemyDamage ?? _local.EnemyDamage,
            overrides.XpRate ?? _local.XpRate,
            overrides.StoreStock ?? _local.StoreStock);

        int health = PlayerCountScalingRule.Resolve(effective.EnemyHealth, ScalingKind.EnemyHealth, playerCount);
        int damage = PlayerCountScalingRule.Resolve(effective.EnemyDamage, ScalingKind.EnemyDamage, playerCount);
        int xp = PlayerCountScalingRule.Resolve(effective.XpRate, ScalingKind.XpRate, playerCount);
        int stock = PlayerCountScalingRule.Resolve(effective.StoreStock, ScalingKind.StoreStock, playerCount);

        bool changed = health != EnemyHealthPercent || damage != EnemyDamagePercent
            || xp != XpRatePercent || stock != StoreStockPercent || playerCount != PlayerCount;

        EnemyHealthPercent = health;
        EnemyDamagePercent = damage;
        XpRatePercent = xp;
        StoreStockPercent = stock;
        PlayerCount = playerCount;
        Effective = effective;

        if (changed)
        {
            Generation++;
        }

        return changed;
    }

    /// <summary>One line for the log: the player count and the four percents, each tagged
    /// <c>(auto)</c> when the curve chose it.</summary>
    public string Describe()
    {
        static string Tag(int setting) => setting == PlayerCountScalingRule.Auto ? " (auto)" : string.Empty;

        return $"{PlayerCount} player(s): enemy health {EnemyHealthPercent}%{Tag(Effective.EnemyHealth)}, "
            + $"enemy damage {EnemyDamagePercent}%{Tag(Effective.EnemyDamage)}, "
            + $"xp {XpRatePercent}%{Tag(Effective.XpRate)}, "
            + $"store stock {StoreStockPercent}%{Tag(Effective.StoreStock)}";
    }

    /// <summary>
    /// Packs the two plugin-side percents into the dword the shared block carries
    /// (<c>SharedBlock.OffsetScalingCombat</c>: health in the low 16 bits, damage in the high
    /// 16). A percent is at most 400, so 16 bits each is generous; 0 in either half means "no
    /// session has written this yet" to the plugin, which then applies nothing.
    /// </summary>
    public uint PackCombatWord() => (uint)(EnemyHealthPercent & 0xFFFF) | ((uint)(EnemyDamagePercent & 0xFFFF) << 16);

    /// <summary>The XP and store percents, same packing, for the plugin's status line only.</summary>
    public uint PackEconomyWord() => (uint)(XpRatePercent & 0xFFFF) | ((uint)(StoreStockPercent & 0xFFFF) << 16);
}
