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
    /// <summary>coop.ini <c>enemy_effect_sync</c> — default ON, audited relay (docs/CONFIG.md). The
    /// agent reads the same key for its own half (<c>CoopConfig.EnemyEffectSync</c>).</summary>
    public static readonly bool Enabled = PluginConfig.ReadBool("enemy_effect_sync", true);

    /// <summary>True while the plugin is itself applying an enemy effect (the host's replay),
    /// so the dispatcher's callback does not capture it again.</summary>
    public static bool Suppress { get; set; }

    public const int Capacity = 16;

    private static readonly uint[] _hostSession = new uint[Capacity], _hostLoad = new uint[Capacity], _hostEpoch = new uint[Capacity];
    private static readonly ulong[] _world = new ulong[Capacity];
    private static readonly uint[] _epoch = new uint[Capacity], _load = new uint[Capacity];
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

        bool ours = PhantomHitHook.IsOursOnForeignCreature(target, targetId, effect);

        // The creator stamp on a non-damage effect is INFERRED (enemy-effects.md 2.2): say what
        // the first few effects looked like, once, so a live run can settle it.
        if (_diagLogged < DiagLogLimit)
        {
            _diagLogged++;
            PluginHost.Log(
                $"enemy-effect: saw kind {kind} on 0x{targetId:X} creator 0x{*(uint*)(effect + 0x18):X} "
                + $"ours={ours} attackers={PhantomHitHook.HasAttackers}");
        }

        if (!ours)
        {
            return;
        }

        bool retainedLocal = MaterialisedEnemies.IsRetainedLocal(targetId)
            || MaterialisedEnemies.IsQuarantinedRetainedLocal(targetId);
        uint ownerId;
        if (retainedLocal)
        {
            if (!MaterialisedEnemies.TryGetCurrentRetainedOwner(targetId, out ownerId))
            {
                return; // local roster id has no current host-owner mapping yet
            }
        }
        else
        {
            ownerId = MaterialisedEnemies.ToOwner(targetId);
        }

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

        nint block = PluginHost.BlockAddress;
        if (block == 0 || !Odyssey.TryGetWorldEpoch(out ulong world) || world == 0) return;
        uint epoch = *(uint*)(block + SharedBlock.OffsetLivenessNativeEpoch);
        uint sequence = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceSequence);
        uint hostSession = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceHostSession);
        uint hostLoad = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceHostLoad);
        uint hostEpoch = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceHostEpoch);
        uint localEpoch = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceLocalEpoch);
        uint localLoad = *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceLocalLoad);
        System.Threading.Thread.MemoryBarrier();
        if (!EnemyEffectNamespaceRule.Matches(sequence, *(uint*)(block + SharedBlock.OffsetEnemyEffectSourceSequence),
            hostSession, hostSession, localEpoch, epoch, localLoad, *(uint*)(block + SharedBlock.OffsetWorldLoadSerial)) || hostEpoch == 0) return;
        int slot = (_head + _count) % Capacity;
        _hostSession[slot] = hostSession; _hostLoad[slot] = hostLoad; _hostEpoch[slot] = hostEpoch;
        _world[slot] = world; _epoch[slot] = epoch;
        _load[slot] = *(uint*)(block + SharedBlock.OffsetWorldLoadSerial);
        _enemy[slot] = enemyId;
        _kind[slot] = kind;
        _durationMs[slot] = EnemyEffectMailboxRule.ClampDurationMs(durationMs);
        _magnitude[slot] = magnitude;
        _count++;
        Captured++;
    }

    private const int DiagLogLimit = 12;

    private static int _diagLogged;

    /// <summary>Frame thread: takes the oldest captured effect, if any.</summary>
    public static bool TryDequeue(out uint enemyId, out uint kind, out uint durationMs, out uint magnitude, out ulong world, out uint epoch, out uint load, out uint hostSession, out uint hostLoad, out uint hostEpoch)
    {
        enemyId = kind = durationMs = magnitude = epoch = load = hostSession = hostLoad = hostEpoch = 0; world = 0;

        if (_count == 0)
        {
            return false;
        }

        hostSession = _hostSession[_head]; hostLoad = _hostLoad[_head]; hostEpoch = _hostEpoch[_head];
        world = _world[_head]; epoch = _epoch[_head]; load = _load[_head];
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
