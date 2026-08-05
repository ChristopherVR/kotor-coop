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

    // One-shot replay dedupe: the serial applied last, primed on first sight of a slot so
    // a swing from before tracking began (a join, a module entry) is never replayed late.
    private readonly uint[] _appliedOneShot = new uint[SharedBlock.EnemyCapacity];
    private readonly bool[] _oneShotPrimed = new bool[SharedBlock.EnemyCapacity];

    // The measure-smooth-decide half, shared with the party controller.
    private readonly MotionSmoother _smoother = new(
        SharedBlock.EnemyCapacity,
        Math.Clamp(PluginConfig.ReadFloat("smooth_ms", 60f), 0f, 400f) / 1000f);

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

        // Replay any NEW fire-and-forget the owner's copy played — the swings and parries
        // that turn a mirrored fight from bodies-in-the-right-places into a fight.
        if (!_oneShotPrimed[slot])
        {
            _oneShotPrimed[slot] = true;
            _appliedOneShot[slot] = enemy.OneShotSerial;
        }
        else if (enemy.OneShotSerial != _appliedOneShot[slot])
        {
            _appliedOneShot[slot] = enemy.OneShotSerial;
            if (enemy.OneShotAnimation != 0)
            {
                Odyssey.PlayOneShotAnimation(creature, (ushort)enemy.OneShotAnimation);
            }
        }

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
        (Vec3 shown, float speed, bool moving) = _smoother.Step(slot, enemy.Position, now);
        Odyssey.DriveEnemy(creature, shown, enemy.Facing, speed, moving, enemy.Animation);
    }

    private void Forget(int slot)
    {
        if (_creature[slot] != 0)
        {
            Odyssey.ForgetGait(_creature[slot]);
        }

        _creature[slot] = 0;
        _trackedId[slot] = 0;
        _appliedOneShot[slot] = 0;
        _oneShotPrimed[slot] = false;
        _smoother.Reset(slot);
    }

    private static bool IsPlausiblePosition(Vec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && (p.X != 0f || p.Y != 0f);
}
