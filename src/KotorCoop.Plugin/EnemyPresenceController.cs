using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Drives this instance's copy of each enemy to wherever the owning peer's engine has it,
/// every frame, on the game's thread.
///
/// <see cref="CombatController"/> makes the two instances AGREE about enemy health and
/// death; this makes them agree about the fight you can actually see. Left alone, each
/// instance runs its own AI over its own copy of every creature, so the same Sith soldier
/// charges the host on one screen and stands in a doorway on the other. The owner's agent
/// publishes each enemy's position, facing and animation with the health it already sends,
/// and this controller places the local body there using the same recipe that made
/// phantoms seamless: write the server object, then pin the client twin's desired position
/// with a zero ease budget (docs/research/realtime-creature-movement.md — the renderer
/// reads the twin, and the twin otherwise eases over a fixed 512 ms).
///
/// The local AI must not fight the drive, so every driven enemy is re-claimed each pass:
/// commandable, action queue cleared, movement permitted. That is a deliberate model
/// change, not a side effect — on the mirroring instance enemies stop acting locally and
/// become a faithful presentation of the owner's fight. The local player still attacks
/// them (damage min-merges to the owner as before); what no longer happens is the private
/// local-only fight that could not be seen by anyone else. Enemy threat TO the mirroring
/// player is combat-participation work, tracked separately.
///
/// Kill switch: <c>enemy_drive=0</c> in coop.ini falls back to health-only mirroring.
/// </summary>
internal sealed unsafe class EnemyPresenceController(SharedBlockView block)
{
    /// <summary>CSWSObject + this = object type; a creature is 5 (verified, the AI
    /// master's own gate at 0x004B0C20 tests it).</summary>
    private const int ObjType = 0x08;

    private const int ObjTypeCreature = 5;

    /// <summary>CSWSObject + this = object id, for cheap per-frame revalidation.</summary>
    private const int ObjId = 0x04;

    /// <summary>CSWSCreature + this = party follow info. Non-zero means the party system
    /// queues follow actions on it — that is a mirrored companion, owned by party sync,
    /// and driving it here would have two writers again.</summary>
    private const int CrePartyFollowInfo = 0x4C0;

    private readonly bool _enabled = PluginConfig.ReadBool("enemy_drive", true);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Per-slot resolution cache. A pointer is revalidated every frame (id + type still
    // matching) and re-resolved when it stops — the 0g lesson: a module change frees every
    // creature and reused memory reads as plausible garbage.
    private readonly uint[] _trackedId = new uint[SharedBlock.EnemyCapacity];
    private readonly nint[] _creature = new nint[SharedBlock.EnemyCapacity];

    // Per-slot smoothing state, same shape as PhantomController.DriveDirect: measured
    // speed from consumed displacements, a shown position that chases the target, and
    // movement hysteresis so the gait cannot flicker.
    private readonly Vec3[] _lastGoal = new Vec3[SharedBlock.EnemyCapacity];
    private readonly float[] _speed = new float[SharedBlock.EnemyCapacity];
    private readonly double[] _speedFromTime = new double[SharedBlock.EnemyCapacity];
    private readonly Vec3[] _shownPosition = new Vec3[SharedBlock.EnemyCapacity];
    private readonly double[] _shownAtTime = new double[SharedBlock.EnemyCapacity];
    private readonly bool[] _smoothing = new bool[SharedBlock.EnemyCapacity];
    private readonly double[] _lastMovedAt = new double[SharedBlock.EnemyCapacity];

    private readonly float _smoothingTauSeconds =
        Math.Clamp(PluginConfig.ReadFloat("smooth_ms", 60f), 0f, 400f) / 1000f;

    /// <summary>Movement below this between updates is jitter, not walking.</summary>
    private const float MovementEpsilon = 0.02f;

    /// <summary>How long the owner's enemy must be still before the gait drops to idle.</summary>
    private const double StillnessSeconds = 0.35;

    /// <summary>Past this the enemy did not walk, it was moved — cut instead of gliding.</summary>
    private const float SnapGapSquared = 8f * 8f;

    private int _drivenLogged = -1;
    private bool _offLogged;

    /// <summary>
    /// Forgets everything on a module change: ids reset across transitions, every cached
    /// pointer is freed memory, and the published slots are stale until the owning agent's
    /// next pump.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.EnemyCapacity; i++)
        {
            Forget(i);
        }

        block.EnemyPresenceCount = 0;
        _drivenLogged = -1;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (!_enabled)
        {
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("enemy drive: disabled by coop.ini (enemy_drive=0)");
            }

            return;
        }

        // A local conversation owns the stage: the dialogue system positions and disarms
        // creatures itself, and the owner's agent holds its publication during its own
        // dialogues for the same reason.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        uint count = Math.Min(block.EnemyPresenceCount, (uint)SharedBlock.EnemyCapacity);
        if (count == 0)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        nint player = (nint)block.PlayerCreature;
        int driven = 0;

        for (int i = 0; i < count; i++)
        {
            if (DriveOne(i, block.GetEnemyPresence(i), now, player))
            {
                driven++;
            }
        }

        // One line when the census changes, not per frame.
        if (driven != _drivenLogged)
        {
            _drivenLogged = driven;
            PluginHost.Log($"enemy drive: mirroring {driven} of the owner's {count} enemies");
        }
    }

    private bool DriveOne(int slot, EnemyPresenceView enemy, double now, nint player)
    {
        if (enemy.ObjectId == 0 || enemy.ObjectId == Odyssey.ObjectInvalid || enemy.Dead)
        {
            // A dead enemy's body belongs to the engine's death path (CombatController
            // runs it); driving a corpse would stand it back up.
            Forget(slot);
            return false;
        }

        if (!IsPlausiblePosition(enemy.Position))
        {
            return false;
        }

        // Never drive a body this instance spawned for a peer. Host-side encounter spawns
        // allocate ids in the same range as local phantom spawns, so a raw id match is not
        // paranoia — it is the one collision the id-identity assumption allows.
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (block.GetPhantomId(i) == enemy.ObjectId)
            {
                return false;
            }
        }

        nint creature = Resolve(slot, enemy.ObjectId);
        if (creature == 0 || creature == player)
        {
            return false;
        }

        // A mirrored companion is party sync's to move, never ours.
        if (*(uint*)(creature + CrePartyFollowInfo) != 0)
        {
            return false;
        }

        // Re-claim every pass, not once: the local AI re-queues combat and movement
        // actions between frames, and an armed MoveToPoint at the queue head is the
        // contended writer that made direct placement look broken for a month.
        Odyssey.ClaimCreature(creature);

        Drive(slot, creature, enemy, now);
        return true;
    }

    /// <summary>Resolves and caches the slot's creature, revalidating the cached pointer
    /// every call — id and object type must both still match.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && *(uint*)(cached + ObjId) == objectId
            && *(int*)(cached + ObjType) == ObjTypeCreature)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(int*)(creature + ObjType) != ObjTypeCreature)
        {
            return 0;
        }

        _creature[slot] = creature;
        _trackedId[slot] = objectId;
        return creature;
    }

    /// <summary>The same measure-smooth-drive as PhantomController.DriveDirect, minus the
    /// camera separation (an enemy is allowed to stand next to the player).</summary>
    private void Drive(int slot, nint creature, EnemyPresenceView enemy, double now)
    {
        float mdx = enemy.Position.X - _lastGoal[slot].X;
        float mdy = enemy.Position.Y - _lastGoal[slot].Y;
        float moved = MathF.Sqrt((mdx * mdx) + (mdy * mdy));

        // Speed sampled update-to-update: the clock advances only when a displacement is
        // consumed, because this runs per frame while the block changes per network tick,
        // and dividing a tick's displacement by a frame's dt doubles the speed.
        if (moved > MovementEpsilon)
        {
            if (_speedFromTime[slot] > 0)
            {
                float dt = (float)(now - _speedFromTime[slot]);
                if (dt > 0.001f)
                {
                    float instant = MathF.Min(moved / dt, 12f);
                    _speed[slot] = _speed[slot] <= 0f
                        ? instant
                        : (_speed[slot] * 0.7f) + (instant * 0.3f);
                }
            }

            _speedFromTime[slot] = now;
            _lastMovedAt[slot] = now;
        }

        Vec3 target = enemy.Position;
        Vec3 shown = _shownPosition[slot];
        float jdx = target.X - shown.X;
        float jdy = target.Y - shown.Y;
        float gapSquared = (jdx * jdx) + (jdy * jdy);

        if (!_smoothing[slot] || gapSquared > SnapGapSquared)
        {
            _smoothing[slot] = true;
            shown = target;
        }
        else
        {
            float dt = (float)Math.Clamp(now - _shownAtTime[slot], 0.0, 0.25);
            float alpha = _smoothingTauSeconds <= 0f ? 1f : 1f - MathF.Exp(-dt / _smoothingTauSeconds);

            shown.X += jdx * alpha;
            shown.Y += jdy * alpha;
            shown.Z = target.Z;
        }

        _shownPosition[slot] = shown;
        _shownAtTime[slot] = now;
        _lastGoal[slot] = enemy.Position;

        bool moving = _lastMovedAt[slot] > 0 && (now - _lastMovedAt[slot]) < StillnessSeconds;

        Odyssey.DriveEnemy(creature, shown, enemy.Facing, _speed[slot], moving, enemy.Animation);
    }

    private void Forget(int slot)
    {
        if (_creature[slot] != 0)
        {
            Odyssey.ForgetGait(_creature[slot]);
        }

        _creature[slot] = 0;
        _trackedId[slot] = 0;
        _speed[slot] = 0f;
        _speedFromTime[slot] = 0;
        _shownPosition[slot] = default;
        _shownAtTime[slot] = 0;
        _smoothing[slot] = false;
        _lastMovedAt[slot] = 0;
        _lastGoal[slot] = default;
    }

    private static bool IsPlausiblePosition(Vec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && (p.X != 0f || p.Y != 0f);
}
