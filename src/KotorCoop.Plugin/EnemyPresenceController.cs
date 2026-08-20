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
    /// <summary>CSWSCreature's vtable — the reliable identity test. The object-type word
    /// at +0x08 carries FLAG BITS on live creatures (0x40005 measured), so an equality
    /// test against 5 rejects real bodies; the first live encounter spawn on tar_m03aa
    /// had its stand-in rejected by exactly that, destroyed, and respawned every
    /// half-second — one burned object id per loop.</summary>
    private const nint CreatureVtable = 0x007470C8;

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
    ///
    /// ⚠ OFF BY DEFAULT, and this default has now been paid for twice. The shared-state
    /// audit's adversarial review left four structural items open ("do not default this
    /// on"), a bare `dev` commit flipped it to on anyway, and the predicted failure then
    /// happened live on tat_m20aa (2026-08-14): 15 points of forwarded damage were
    /// absorbed as "attributed to mirrored enemies we are driving" while this engine's
    /// own in-combat flag read FALSE — the local copy never dealt the replacement damage,
    /// and the player was simply invulnerable for that hit. Opt in via coop.ini once the
    /// audit's items 4-7 are closed.
    /// </summary>
    public static readonly bool LocalCombat = PluginConfig.ReadBool("local_combat", false);

    /// <summary>
    /// How close a driven enemy's local body must be to this player before its owner's report
    /// may be absorbed, in world units (coop.ini <c>local_combat_range</c>).
    ///
    /// ⚠ This closes the hole the identity test alone leaves open. A mirrored enemy is placed
    /// where the OWNER's engine has it, which is next to the OWNER's player — so when the two
    /// players are apart the local copy can never reach this player, never lands a hit, and a
    /// blanket absorb would make this player invulnerable for the whole fight. The measured
    /// "apart" case is ~120 units (docs/research/combat-when-apart.md §1b), so 20 separates
    /// the two cases with a wide margin instead of trying to model weapon reach.
    /// </summary>
    private static readonly float EngagementRangeSquared =
        Squared(Math.Clamp(PluginConfig.ReadFloat("local_combat_range", 20f), 1f, 200f));

    private static float Squared(float value) => value * value;

    /// <summary>Owner-side ids driven on the most recent pass whose local body stood close
    /// enough to this player for its own AI to be fighting them, and when that was recorded.
    /// Frame thread only, both writes and reads.</summary>
    private static readonly uint[] EngagedOwnerId = new uint[SharedBlock.EnemyCapacity];
    private static int _engagedCount;
    private static long _engagedAtTicks;

    /// <summary>Past this the record is not evidence of anything — no drive pass has run at
    /// all (a module change, the conversation hold, an ended session), so nothing local is
    /// fighting and every forwarded point is this player's to take.</summary>
    private static readonly long EngagedFreshTicks = TimeSpan.FromSeconds(1).Ticks;

    /// <summary>
    /// Whether <paramref name="ownerObjectId"/> — an attacker id in the OWNER's id space, the
    /// space the enemy census and the phantom-damage channel both use — is an enemy this
    /// instance is genuinely driving AND is standing close enough to this player to be dealing
    /// them damage here.
    ///
    /// ⚠ THIS IS THE ENTIRE LICENCE FOR ABSORBING FORWARDED DAMAGE, and every class of damage
    /// the old blanket absorb swallowed silently fails one of the two tests: an enemy shed by
    /// the census cap or by distance was never driven; a creature the resolver skipped was
    /// never driven; a phantom-id collision returns before the drive; a mine, a trap or a
    /// scripted EffectDamage names an attacker that is not in the census at all; the
    /// conversation hold stops the pass so the record goes stale; and a fight 120 units away
    /// has no local body near this player. All of them now APPLY.
    /// </summary>
    public static bool IsLocallyEngaged(uint ownerObjectId)
    {
        if (ownerObjectId is 0 or Odyssey.ObjectInvalid
            || DateTime.UtcNow.Ticks - _engagedAtTicks > EngagedFreshTicks)
        {
            return false;
        }

        for (int i = 0; i < _engagedCount; i++)
        {
            if (EngagedOwnerId[i] == ownerObjectId)
            {
                return true;
            }
        }

        return false;
    }

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

        // Ids are per module load: a stale engagement record would license absorbing damage
        // billed to a completely different creature.
        _engagedCount = 0;
        _engagedAtTicks = 0;
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

        // Resolved fresh, never taken from the block: the agent-written pointer survives a
        // module transition stale, and RecordEngagement dereferences this (same fault class
        // as the EncounterController travel-follow crash).
        nint player = Odyssey.GetPlayerCreaturePointer();
        int driven = 0;

        // Rebuilt from scratch every pass, so a body that stops being driven — or walks out
        // of range — stops licensing an absorb on the very next pass rather than decaying.
        _engagedCount = 0;

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

        // ⚠ STAMPED ONLY HERE, at the bottom of a pass that actually ran. Every early return
        // above -- no session, drive disabled, a local conversation, an empty census -- leaves
        // the stamp where it was, so IsLocallyEngaged goes false within a second and
        // IncomingDamageController APPLIES instead of absorbing. That is deliberate: the
        // conversation hold in particular used to be absorbed, when the pre-local_combat code
        // held that debt on purpose.
        _engagedAtTicks = DateTime.UtcNow.Ticks;

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
            else if (!MaterialisedEnemies.InBirthGrace(enemy.ObjectId, now))
            {
                // The mapped body has been unresolvable past its birth grace: treat it as
                // genuinely gone. A FRESH body is left alone instead — measured live, a
                // stand-in's first resolve can fail transiently, and condemning it on that
                // one frame burned a body (and an object id) per spawn cooldown until one
                // stuck. Log what the resolver saw, because "could not be resolved" alone
                // told us nothing about why.
                nint raw = Odyssey.ResolveServerObject(mapped);
                PluginHost.Log(
                    $"enemy drive: giving up on stand-in 0x{mapped:X} for owner "
                    + $"0x{enemy.ObjectId:X} (resolver returned 0x{raw:X}"
                    + (raw != 0 ? $", vtable 0x{*(nint*)raw:X}, id 0x{*(uint*)(raw + ObjId):X})" : ")"));
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
        // With local combat on the wipe is TARGETED rather than absent. "Leave the queue
        // alone" was the first attempt and it is worse than the bug it replaced: this
        // controller overwrites the body position from the owner every pass, so a queued move
        // never reduces its own distance, never completes, and wedges the queue head
        // permanently -- the attacks behind it never run and this instance still never enters
        // combat, only now the body also fights our placement. Odyssey reads the head node's
        // action type (node+0x00, dispatched by RunActions 0x0057F4A0) and wipes only when it
        // is a move-class action.
        if (LocalCombat)
        {
            Odyssey.ClaimCreatureForFight(creature);
        }
        else
        {
            Odyssey.ClaimCreature(creature, clearActions: true);
        }

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

        if (LocalCombat)
        {
            // The other half of the position fight: our own SetPosition suppresses the dirty
            // bit, the local AI does not. See Odyssey.ClearPositionDirty.
            Odyssey.ClearPositionDirty(creature);

            RecordEngagement(enemy.ObjectId, creature, player);

            // The missing piece PermitAttacking/ClaimCreatureForFight never provided: a
            // reason for the AI to decide to fight at all. See Odyssey.QueueAttackAction.
            // Gated to the same engagement range as RecordEngagement — a mirrored enemy
            // three rooms away has no business being handed a target.
            if (player != 0 && IsEngaged(creature, player))
            {
                Odyssey.QueueAttackAction(creature, *(uint*)(player + ObjId));
            }
        }

        return true;
    }

    /// <summary>
    /// Records a driven enemy as "close enough to be fighting this player HERE" -- the fact
    /// that licenses IncomingDamageController to absorb the owner report for it.
    ///
    /// Planar distance: the Z of a driven body is the owner's, and a ramp between two players
    /// standing in the same fight is a metre of height, not a different floor.
    /// </summary>
    private static void RecordEngagement(uint ownerObjectId, nint creature, nint player)
    {
        if (player == 0 || _engagedCount >= EngagedOwnerId.Length)
        {
            return;
        }

        if (IsEngaged(creature, player))
        {
            EngagedOwnerId[_engagedCount++] = ownerObjectId;
        }
    }

    /// <summary>Planar distance test shared by <see cref="RecordEngagement"/> (licenses damage
    /// absorption) and the local_combat attack-queuing gate — one fight radius, one place it is
    /// computed. The Z of a driven body is the owner's, and a ramp between two players standing
    /// in the same fight is a metre of height, not a different floor.</summary>
    private static bool IsEngaged(nint creature, nint player)
    {
        Vec3 body = Odyssey.GetPosition(creature);
        Vec3 them = Odyssey.GetPosition(player);
        float dx = body.X - them.X;
        float dy = body.Y - them.Y;
        return (dx * dx) + (dy * dy) <= EngagementRangeSquared;
    }

    /// <summary>Resolves and caches the slot's creature, revalidating the cached pointer
    /// every call — id and object type must both still match.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && *(uint*)(cached + ObjId) == objectId
            && *(nint*)cached == CreatureVtable)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(nint*)creature != CreatureVtable)
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