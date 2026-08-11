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
    /// master's own gate at 0x004B0C20 tests it).
    ///
    /// ⚠ RESOLVED, 2026-08-11 — AND IT WAS THE WRONG FIELD WIDTH ALL ALONG.
    ///
    /// This used to compare the whole DWORD, and on a live census enemy 0x14 read 5 while
    /// 0x25 — standing in the right place, at full health, driven fine by every other
    /// channel — read 0x40005. The old note called that "flag bits above the type" and left
    /// the test strict, on the theory that widening it to the vtable check was right but not
    /// understood (a live run of that widening also broke the peer-damage battery).
    ///
    /// The type is a WORD, not a dword: docs/re/object-model.md has it as `+0x008 | object
    /// type | word`, and the engine's own gate at 0x004B0C5A reads it as a BYTE
    /// (`mov al, [esi+8]` / `cmp al, [0x746633]`). The dword compare was dragging in
    /// adjacent per-creature state — 0x40005 is type 5 with 0x0004 living at +0x0A. So the
    /// fix is a NARROWING to the documented width, not the widening that was tried and
    /// reverted, which is a much safer change and does not weaken the test at all.
    ///
    /// This mattered for more than the census. The skipped creatures made
    /// MaterialisedEnemies' resolver return 0 for a creature that was present, so it fell
    /// through to a full-process CreatureFinder sweep to prove absence — measured live at
    /// five sweeps 200 ms apart, and it never stopped, because the false-alarm log caps at
    /// five lines while the loop runs on. See docs/research/mirror-sweep-storm.md.
    /// </summary>
    private const int ObjType = 0x08;

    private const ushort ObjTypeCreature = 5;

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
    //
    // Its own knob, not the phantom's smooth_ms: phantoms arrive at the 30 Hz tick, where
    // 60 ms reads as continuous, but mirrored creatures arrive on the agent's combat pump
    // (~150 ms apart). A 60 ms tau consumes most of each hop before the next lands — fast,
    // stop, fast, a visible pulse at the stream rate. A tau near the arrival interval
    // spreads each hop across the whole gap, at the cost of trailing truth by a step.
    private readonly MotionSmoother _smoother = new(
        SharedBlock.EnemyCapacity,
        Math.Clamp(PluginConfig.ReadFloat("mirror_smooth_ms", 150f), 0f, 600f) / 1000f);

    private int _drivenLogged = -1;
    private bool _offLogged;

    /// <summary>
    /// Whether this instance lets its mirrored enemies fight for themselves.
    ///
    /// OFF preserves the behaviour every other channel was built against: the enemy is a
    /// body in the right place that never acts, and everything that happens to this player
    /// arrives as forwarded damage from the owner.
    ///
    /// ON hands the local copies back to their own AI so they can complete combat rounds
    /// here — which is the only way this engine's combat state can rise, since the engine
    /// derives it from being fought rather than from a flag anyone can set (measured: a
    /// written flag is reverted in 0-2 ms, see CombatStateController).
    ///
    /// ⚠ TURNING THIS ON WITHOUT MOVING DAMAGE AUTHORITY DOUBLE-COUNTS EVERY HIT: the local
    /// enemy swings here AND the owner forwards the same swing that landed on this player's
    /// phantom over there. IncomingDamageController reads this same flag and stands down when
    /// it is set, so the two always move together.
    /// </summary>
    public static readonly bool LocalCombat = PluginConfig.ReadBool("local_combat", false);

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
        double now = _clock.Elapsed.TotalSeconds;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            // Still sweep: a session that ended leaves materialised bodies with nobody
            // refreshing them, and they must not stand around (or reach a save) forever.
            MaterialisedEnemies.Sweep(now);
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
        // dialogues for the same reason. No sweep either — a spawn or destroy is a VM
        // dispatch, refused while a script runs, and the census hold must not read as
        // staleness.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        uint count = Math.Min(block.EnemyPresenceCount, (uint)SharedBlock.EnemyCapacity);
        if (count == 0)
        {
            MaterialisedEnemies.Sweep(now);
            return;
        }

        nint player = (nint)block.PlayerCreature;
        int driven = 0;

        for (int i = 0; i < count; i++)
        {
            EnemyPresenceView enemy = block.GetEnemyPresence(i);

            // Every listed id — dead or alive — keeps its materialised body's lease
            // fresh; a corpse the owner still publishes must keep lying there.
            MaterialisedEnemies.Seen(enemy.ObjectId, now);

            if (DriveOne(i, enemy, now, player))
            {
                driven++;
            }
        }

        MaterialisedEnemies.Sweep(now);

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

        // Translate through the materialisation map. An owner id with no local mapping
        // usually resolves directly (GIT identity); when it does not, the enemy exists
        // only in the owner's instance and a stand-in body is built for it.
        uint mapped = MaterialisedEnemies.ToLocal(enemy.ObjectId);

        // ⚠ An unmapped owner id that numerically equals the LOCAL id of a body we
        // spawned for a DIFFERENT owner enemy must never be resolved as if it were
        // GIT-identical — that would drive our stand-in to two masters. It is exactly
        // the phantom-id collision above, one table over.
        if (mapped == 0 && MaterialisedEnemies.ToOwner(enemy.ObjectId) != 0)
        {
            MaterialisedEnemies.TryMaterialise(enemy, now);
            return false;
        }

        nint creature = Resolve(slot, mapped != 0 ? mapped : enemy.ObjectId);
        if (creature == 0)
        {
            if (mapped == 0)
            {
                MaterialisedEnemies.TryMaterialise(enemy, now);
            }
            else
            {
                // The mapped body vanished under us (the engine freed it); forget the
                // mapping so a later pass can rebuild it.
                MaterialisedEnemies.ForgetOwner(enemy.ObjectId);
            }

            return false;
        }

        if (creature == player)
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
        //
        // With local combat on, the queue is LEFT ALONE — the same wipe that stops the
        // enemy walking away also deletes its swings, which is why a mirroring instance is
        // never in combat. See Odyssey.ClaimCreature(nint, bool).
        Odyssey.ClaimCreature(creature, clearActions: !LocalCombat);

        // Re-assert every pass, not once at spawn: a materialised body is marked as a
        // mirrored enemy when it is created and lives for the rest of the module, so a
        // spawn-time decision about whether it may swing cannot be trusted to still hold.
        if (LocalCombat)
        {
            Odyssey.PermitAttacking(creature);
        }

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
            && *(ushort*)(cached + ObjType) == ObjTypeCreature)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(ushort*)(creature + ObjType) != ObjTypeCreature)
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
