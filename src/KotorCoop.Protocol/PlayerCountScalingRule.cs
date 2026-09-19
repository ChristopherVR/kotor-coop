namespace KotorCoop.Protocol;

/// <summary>Which of the four player-count scaling knobs a value is for — each has its own
/// <c>auto</c> curve in <see cref="PlayerCountScalingRule.Curve"/>.</summary>
public enum ScalingKind : byte
{
    EnemyHealth = 0,
    EnemyDamage = 1,
    XpRate = 2,
    StoreStock = 3,
}

/// <summary>
/// The pure arithmetic behind coop.ini <c>enemy_health</c>, <c>enemy_damage</c>, <c>xp_rate</c>
/// and <c>store_stock</c> (docs/research/player-count-scaling.md): how a player's setting text
/// becomes a stored SETTING (an integer percent, or <see cref="Auto"/>), how a setting plus the
/// live player count becomes the PERCENT actually applied, and how a percent scales a value.
///
/// A SETTING travels in <see cref="BehaviourDigest"/> as an int so a client can adopt the host's
/// (host-authoritative — the host's engine is where enemy health, XP grants and store stock are
/// decided, so a client running a different number would only disagree with what it sees). The
/// PERCENT is never on the wire: both sides resolve it from the same setting and the same player
/// count, which every peer already knows from its own peer list. Integer percents throughout,
/// never floats (see the v1 scope note: a float would compound rounding differently on each
/// side).
/// </summary>
public static class PlayerCountScalingRule
{
    /// <summary>The setting value meaning "resolve from the live player count" — what an absent
    /// key, the literal <c>auto</c>, or unparseable text stores.</summary>
    public const int Auto = 0;

    /// <summary>A fixed percent is clamped into this range. 50 is half strength; 400 is four
    /// times. Anything a player types outside it is a typo, not a request.</summary>
    public const int MinPercent = 50;

    public const int MaxPercent = 400;

    /// <summary>The percent that changes nothing.</summary>
    public const int Vanilla = 100;

    /// <summary>
    /// Parses a coop.ini value. Lenient: null/blank yields <paramref name="fallback"/>;
    /// <c>auto</c> (any case) yields <see cref="Auto"/>; an integer, with or without a trailing
    /// <c>%</c> and surrounding whitespace, is clamped to [<see cref="MinPercent"/>,
    /// <see cref="MaxPercent"/>]; anything else yields <paramref name="fallback"/>, so a typo
    /// never turns a fight into something unplanned.
    /// </summary>
    public static int ParseSetting(string? text, int fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string value = text.Trim();
        if (value.EndsWith('%'))
        {
            value = value[..^1].TrimEnd();
        }

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return Auto;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int percent)
            ? ClampPercent(percent)
            : fallback;
    }

    /// <summary>Normalises a setting read from the wire or a record: <see cref="Auto"/> stays,
    /// anything else is clamped into the fixed range.</summary>
    public static int NormaliseSetting(int setting) => setting == Auto ? Auto : ClampPercent(setting);

    public static int ClampPercent(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>The setting as a player reads it: <c>auto</c> or the bare percent.</summary>
    public static string Describe(int setting) =>
        setting == Auto ? "auto" : ClampPercent(setting).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The percent to actually apply: a fixed setting is itself; <see cref="Auto"/> follows
    /// <see cref="Curve"/> for the live player count (host + connected clients).
    /// </summary>
    public static int Resolve(int setting, ScalingKind kind, int playerCount) =>
        setting == Auto ? Curve(kind, playerCount) : ClampPercent(setting);

    /// <summary>
    /// The <c>auto</c> curve. One player is vanilla on every axis — alone, nothing changes.
    /// Enemy health grows fastest because two characters simply deal twice the damage per
    /// round; enemy damage grows gently because it is the number that ends a run (a stunned
    /// PC still takes every hit); XP stays vanilla at every count, since the max-merge already
    /// gives every player the same total and a shared world is not a reason to level faster;
    /// store stock grows with health so two players can each buy the one medpac a shop holds.
    /// Four or more players share the four-player row.
    /// </summary>
    public static int Curve(ScalingKind kind, int playerCount)
    {
        if (playerCount <= 1)
        {
            return Vanilla;
        }

        int row = Math.Min(playerCount, 4);
        return kind switch
        {
            ScalingKind.EnemyHealth => row switch { 2 => 150, 3 => 200, _ => 250 },
            ScalingKind.EnemyDamage => row switch { 2 => 115, 3 => 125, _ => 135 },
            ScalingKind.XpRate => Vanilla,
            ScalingKind.StoreStock => row switch { 2 => 150, 3 => 200, _ => 250 },
            _ => Vanilla,
        };
    }

    /// <summary>
    /// Scales a positive integer by a percent, rounding half up, never below 1 for a positive
    /// input (a 1-hit-point rat at 50% is still alive; a 1-damage hit at 50% still lands). A
    /// non-positive input comes back unchanged — 0 and negatives mean "unset"/"dead" to the
    /// engine and must stay what they are.
    /// </summary>
    public static int Scale(int value, int percent)
    {
        if (value <= 0 || percent == Vanilla)
        {
            return value;
        }

        long scaled = ((long)value * percent + 50) / 100;
        if (scaled < 1)
        {
            scaled = 1;
        }

        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }
}
