namespace KotorCoop.Plugin;

/// <summary>
/// The pure half of player-count scaling inside the plugin (docs/research/player-count-
/// scaling.md): unpacking the agent's resolved percents out of the shared block word, the
/// hit-point arithmetic <see cref="EnemyScalingController"/> writes once per enemy, and the
/// per-hit damage arithmetic <see cref="PhantomHitHook"/> applies at the OnApplyDamage detour.
/// No engine access, so <c>tests/KotorCoop.Tests</c> links this file the way it links
/// <c>MirrorHealthRule.cs</c>.
///
/// Mirrors <c>KotorCoop.Session.PlayerCountScaling.PackCombatWord</c>: health percent in the
/// low 16 bits, damage percent in the high 16. The two halves cannot share a type (NativeAOT
/// x86 plugin, x64 agent), so the packing is written twice on purpose.
/// </summary>
internal static class EnemyScalingRule
{
    public const int Vanilla = 100;

    /// <summary>The widest percent the agent will ever send (<c>PlayerCountScalingRule.MaxPercent</c>);
    /// anything beyond it in the block is a torn or foreign write and reads as vanilla.</summary>
    public const int MaxPercent = 400;

    public const int MinPercent = 50;

    /// <summary>Health percent from the combat word; 0 (no session has written the block) or
    /// an out-of-range value reads as <see cref="Vanilla"/>, i.e. "apply nothing".</summary>
    public static int HealthPercent(uint combatWord) => Sanitise((int)(combatWord & 0xFFFF));

    /// <summary>Damage percent from the combat word; same rules as <see cref="HealthPercent"/>.</summary>
    public static int DamagePercent(uint combatWord) => Sanitise((int)((combatWord >> 16) & 0xFFFF));

    private static int Sanitise(int percent) =>
        percent is >= MinPercent and <= MaxPercent ? percent : Vanilla;

    /// <summary>
    /// The hit points to write onto an enemy seen for the first time: the stored max scaled,
    /// and the current scaled by the same percent so a creature that started full stays full
    /// and one the module placed wounded stays proportionally wounded. Rounds half up, never
    /// below 1, and refuses (returns false) when there is nothing sensible to do: a vanilla
    /// percent, a max of 0 (a player character — its max is computed, never stored) or a
    /// current at or below 0 (a corpse).
    /// </summary>
    public static bool ScaledHitPoints(int current, int max, int percent, out int newCurrent, out int newMax)
    {
        newCurrent = current;
        newMax = max;

        if (percent == Vanilla || max <= 0 || current <= 0)
        {
            return false;
        }

        newMax = Scale(max, percent);
        newCurrent = Scale(current, percent);

        // The stored max is a ushort in the engine; a scaled value past it would wrap.
        if (newMax > ushort.MaxValue)
        {
            newMax = ushort.MaxValue;
        }

        if (newCurrent > newMax)
        {
            newCurrent = newMax;
        }

        return newCurrent != current || newMax != max;
    }

    /// <summary>One damage amount out of an effect's per-type list, scaled. Non-positive
    /// amounts (unset slots read -1) come back unchanged; a positive one never drops below 1
    /// — a hit that landed still lands.</summary>
    public static int ScaleDamage(int amount, int percent) =>
        amount <= 0 || percent == Vanilla ? amount : Scale(amount, percent);

    private static int Scale(int value, int percent)
    {
        long scaled = ((long)value * percent + 50) / 100;
        if (scaled < 1)
        {
            scaled = 1;
        }

        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }
}

/// <summary>
/// Object ids already scaled this module epoch — the per-object latch every per-frame engine
/// write in this codebase carries (memory rule: a torn-down panel was once written through
/// 500x/s). Fixed arrays, no allocation after construction, so the controller can consult it
/// from the frame thread. Full means "stop scaling new enemies until the next module", which
/// fails towards vanilla rather than towards a double write.
/// </summary>
internal sealed class ScaledObjectLatch
{
    private readonly uint[] _ids;
    private readonly ushort[] _baseMaximums;
    private readonly ushort[] _appliedPercents;
    private int _count;

    public ScaledObjectLatch(int capacity)
    {
        _ids = new uint[capacity];
        _baseMaximums = new ushort[capacity];
        _appliedPercents = new ushort[capacity];
    }

    public int Count => _count;

    public bool IsFull => _count >= _ids.Length;

    public bool Contains(uint objectId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_ids[i] == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records the unscaled maximum and percentage alongside the object id. This is
    /// the controller's in-epoch source of truth for future baseline export; it deliberately
    /// does not imply that a load has been matched to persisted metadata.</summary>
    public bool Add(uint objectId, int baseMaximum, int appliedPercent)
    {
        if (objectId == 0
            || Contains(objectId)
            || IsFull
            || baseMaximum is <= 0 or > ushort.MaxValue
            || appliedPercent is < EnemyScalingRule.MinPercent or > EnemyScalingRule.MaxPercent)
        {
            return false;
        }

        _ids[_count] = objectId;
        _baseMaximums[_count] = (ushort)baseMaximum;
        _appliedPercents[_count] = (ushort)appliedPercent;
        _count++;
        return true;
    }

    /// <summary>Reads the first-sighting baseline retained for an id in this module epoch.</summary>
    public bool TryGetBaseline(uint objectId, out int baseMaximum, out int appliedPercent)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_ids[i] == objectId)
            {
                baseMaximum = _baseMaximums[i];
                appliedPercent = _appliedPercents[i];
                return true;
            }
        }

        baseMaximum = 0;
        appliedPercent = 0;
        return false;
    }

    public void Clear() => _count = 0;
}
