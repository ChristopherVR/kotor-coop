namespace KotorCoop.Plugin;

/// <summary>
/// The capture half of the client-cast enemy-effect relay (docs/research/enemy-effects.md). Not a
/// hook of its own: <see cref="PhantomEffectHook"/> already sits on the engine's generic effect
/// dispatcher (<c>CSWSEffectListHandler::OnEffectApplied</c>) and, for a target that is NOT a
/// peer's phantom, hands the (target, effect) pair here — on the MIRRORING instance only
/// (<see cref="PhantomHitHook.HasAttackers"/> is empty on the enemies' owner, which is how the
/// damage path opts the owner out too).
///
/// An effect is captured when ALL of these hold — the same gate the damage ledger uses
/// (<see cref="PhantomHitHook.IsOursOnForeignCreature"/>), plus the effect vocabulary:
/// <list type="number">
/// <item>the effect is on the forwardable allowlist (<see cref="EnemyEffectMailboxRule"/>);</item>
/// <item>its creator (<c>CGameEffect+0x18</c>) is one of our bodies (the PC / party);</item>
/// <item>the target is a live server creature that is not one of our own bodies;</item>
/// <item>the target is not the <c>local_combat</c> bypass enemy (its local copy is deliberately
/// authoritative for that fight);</item>
/// <item>the per-enemy cooldown/cap admits it (<see cref="EnemyEffectThrottleRule"/>).</item>
/// </list>
/// The enemy id is translated into the OWNER's id space (<c>MaterialisedEnemies.ToOwner</c> for a
/// stand-in, the id itself for a GIT-identical twin). Never re-captures the plugin's own applies:
/// <see cref="Suppress"/> is held across <c>OdysseyEffects.ApplyEffect</c> in
/// <see cref="EnemyEffectController"/>.
///
/// Runs on the game thread inside the dispatcher: fixed arrays, no allocation, no engine calls.
/// </summary>
internal static unsafe class EnemyEffectCapture
{
    /// <summary>coop.ini <c>enemy_effect_sync</c> — default OFF, NEEDS-LIVE (docs/CONFIG.md). The
    /// agent reads the same key for its own half (<c>CoopConfig.EnemyEffectSync</c>).</summary>
    public static readonly bool Enabled = PluginConfig.ReadBool("enemy_effect_sync", false);

    /// <summary>True while the plugin is itself applying an enemy effect (the host's replay),
    /// so the dispatcher's callback does not capture it again.</summary>
    public static bool Suppress { get; set; }

    public const int Capacity = 16;

    private static readonly uint[] _enemy = new uint[Capacity];
    private static readonly uint[] _kind = new uint[Capacity];
    private static readonly uint[] _durationMs = new uint[Capacity];
    private static readonly uint[] _magnitude = new uint[Capacity];

    private static int _head;
    private static int _count;

    private static readonly EnemyEffectThrottleRule _throttle = new();

    /// <summary>Effects captured / refused by the throttle / dropped by a full queue — for the
    /// status line and the live pass.</summary>
    public static uint Captured { get; private set; }

    public static uint Throttled { get; private set; }

    public static uint Overflowed { get; private set; }

    public static void Consider(nint target, uint targetId, nint effect)
    {
        if (Suppress)
        {
            return;
        }

        if (!PhantomEffectHook.TryClassify(effect, out uint kind, out uint durationMs, out uint magnitude)
            || !EnemyEffectMailboxRule.IsForwardable(kind))
        {
            return;
        }

        if (!PhantomHitHook.IsOursOnForeignCreature(target, targetId, effect))
        {
            return;
        }

        uint ownerId = MaterialisedEnemies.ToOwner(targetId);
        uint enemyId = ownerId != 0 ? ownerId : targetId;

        if (enemyId == EnemyPresenceController.LocalCombatBypassId)
        {
            return;
        }

        if (!_throttle.TryAdmit(enemyId, kind, Environment.TickCount64))
        {
            Throttled++;
            return;
        }

        if (_count == Capacity)
        {
            // Oldest out: an effect is only worth its freshest replay.
            _head = (_head + 1) % Capacity;
            _count--;
            Overflowed++;
        }

        int slot = (_head + _count) % Capacity;
        _enemy[slot] = enemyId;
        _kind[slot] = kind;
        _durationMs[slot] = EnemyEffectMailboxRule.ClampDurationMs(durationMs);
        _magnitude[slot] = magnitude;
        _count++;
        Captured++;
    }

    /// <summary>Frame thread: takes the oldest captured effect, if any.</summary>
    public static bool TryDequeue(out uint enemyId, out uint kind, out uint durationMs, out uint magnitude)
    {
        enemyId = kind = durationMs = magnitude = 0;

        if (_count == 0)
        {
            return false;
        }

        enemyId = _enemy[_head];
        kind = _kind[_head];
        durationMs = _durationMs[_head];
        magnitude = _magnitude[_head];
        _head = (_head + 1) % Capacity;
        _count--;
        return true;
    }

    /// <summary>A module change reassigns every object id, so a queued capture would name a
    /// stranger and a remembered cooldown a different creature.</summary>
    public static void Invalidate()
    {
        _head = 0;
        _count = 0;
        _throttle.Clear();
    }
}
