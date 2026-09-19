namespace KotorCoop.Plugin;

/// <summary>
/// The per-enemy cooldown and cap that keeps a spam of casts from flooding the host
/// (docs/research/enemy-effects.md). Two independent limits, both fixed-size and allocation-free
/// because the capture side runs inside the engine's effect dispatcher:
/// <list type="bullet">
/// <item>the same (enemy, kind) may not be admitted again within <see cref="MinRepeatMs"/> — a
/// Whirlwind cast once is one effect, not the several the engine's own follow-ups and an AoE's
/// per-target passes can produce for it;</item>
/// <item>one enemy may be admitted at most <see cref="MaxPerWindow"/> times per
/// <see cref="WindowMs"/> — a stunlock on one target that the host would otherwise apply as fast
/// as the network delivers it.</item>
/// </list>
/// The same rule guards both ends: the client runs it at capture (so a spam never leaves the
/// machine) and the host runs it again at apply (so a modified client cannot bypass it). Time is
/// passed in, never read, so tests drive it deterministically.
/// </summary>
internal sealed class EnemyEffectThrottleRule
{
    public const long MinRepeatMs = 1_000;
    public const long WindowMs = 10_000;
    public const int MaxPerWindow = 8;

    private const int PairSlots = 48;
    private const int EnemySlots = 32;

    private readonly uint[] _pairEnemy = new uint[PairSlots];
    private readonly uint[] _pairKind = new uint[PairSlots];
    private readonly long[] _pairLastMs = new long[PairSlots];

    private readonly uint[] _enemy = new uint[EnemySlots];
    private readonly long[] _enemyWindowStartMs = new long[EnemySlots];
    private readonly int[] _enemyCount = new int[EnemySlots];

    /// <summary>True when this effect may go through; records it. False refuses it and records
    /// nothing, so a refused burst does not extend its own cooldown.</summary>
    public bool TryAdmit(uint enemyId, uint kind, long nowMs)
    {
        if (enemyId == 0)
        {
            return false;
        }

        int pair = -1;
        int oldestPair = 0;
        for (int i = 0; i < PairSlots; i++)
        {
            if (_pairEnemy[i] == enemyId && _pairKind[i] == kind)
            {
                pair = i;
                break;
            }

            if (_pairLastMs[i] < _pairLastMs[oldestPair])
            {
                oldestPair = i;
            }
        }

        if (pair >= 0 && nowMs - _pairLastMs[pair] < MinRepeatMs)
        {
            return false;
        }

        int slot = -1;
        int oldestEnemy = 0;
        for (int i = 0; i < EnemySlots; i++)
        {
            if (_enemy[i] == enemyId)
            {
                slot = i;
                break;
            }

            if (_enemyWindowStartMs[i] < _enemyWindowStartMs[oldestEnemy])
            {
                oldestEnemy = i;
            }
        }

        if (slot < 0)
        {
            slot = oldestEnemy;
            _enemy[slot] = enemyId;
            _enemyWindowStartMs[slot] = nowMs;
            _enemyCount[slot] = 0;
        }
        else if (nowMs - _enemyWindowStartMs[slot] >= WindowMs)
        {
            _enemyWindowStartMs[slot] = nowMs;
            _enemyCount[slot] = 0;
        }

        if (_enemyCount[slot] >= MaxPerWindow)
        {
            return false;
        }

        _enemyCount[slot]++;

        int target = pair >= 0 ? pair : oldestPair;
        _pairEnemy[target] = enemyId;
        _pairKind[target] = kind;
        _pairLastMs[target] = nowMs;
        return true;
    }

    /// <summary>A module change reassigns every object id, so remembered cooldowns would name
    /// strangers.</summary>
    public void Clear()
    {
        Array.Clear(_pairEnemy);
        Array.Clear(_pairKind);
        Array.Clear(_pairLastMs);
        Array.Clear(_enemy);
        Array.Clear(_enemyWindowStartMs);
        Array.Clear(_enemyCount);
    }
}
