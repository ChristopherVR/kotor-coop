namespace KotorCoop.Plugin;

/// <summary>
/// Mirrors the owning peer's enemies into this instance, once per frame, on the game's
/// thread.
///
/// The model is host-authoritative, and the reason is that enemies cannot be made to agree
/// any other way. Both instances run their own AI over their own copy of every creature,
/// so left alone they diverge the moment a fight starts. Rather than try to synchronise
/// the combat system - rounds, attack rolls, animation state, all interlocking - only the
/// OUTCOME is replicated: how much health each enemy has, and whether it is dead.
///
/// Two things make that cheap. Enemy object ids are the same number in both instances,
/// because both load the same save and the engine assigns ids deterministically in GIT
/// order (verified live: the same Sith soldier is 0x14 in both). And health needs no dirty
/// flag - the server-to-client update diffs it against a snapshot every tick, so writing
/// it is enough to move the health bar.
///
/// Health is replicated ABSOLUTELY, never as damage. Each instance applies its own
/// difficulty multiplier, temporary hit points and damage resistance, so replaying "took 7
/// damage" on the far side does not produce 7.
///
/// The local player still fights normally: damage lands, numbers float, the enemy reacts.
/// It just cannot deliver the killing blow, because every mirrored enemy is pinned with
/// min-1-hp. Death arrives from the owner and is executed through a real lethal damage
/// effect, so the corpse, loot and OnDeath script all happen properly.
/// </summary>
internal sealed class CombatController(SharedBlockView block)
{
    /// <summary>
    /// Frames between passes. The hook runs far above the frame rate and resolving object
    /// ids costs a hash lookup each, so this is throttled rather than run every frame.
    /// </summary>
    private const int IntervalFrames = 40;

    /// <summary>Ids already killed here, so a death is executed once and not every pass.</summary>
    private readonly uint[] _killed = new uint[SharedBlock.EnemyCapacity];
    private int _killedCount;
    private int _countdown;
    private int _logged;

    /// <summary>
    /// Forgets everything on a module change. The id counters reset on transition, so a
    /// remembered kill would suppress the death of a completely different creature that
    /// happens to reuse the id — and the published enemy slots in the block are equally
    /// stale, so they are dropped rather than applied to innocent new objects. The owning
    /// agent republishes a fresh census on its next pump.
    /// </summary>
    public void Invalidate()
    {
        Array.Clear(_killed);
        _killedCount = 0;
        block.EnemyCount = 0;
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        uint count = Math.Min(block.EnemyCount, (uint)SharedBlock.EnemyCapacity);

        for (int i = 0; i < count; i++)
        {
            Apply(block.GetEnemy(i));
        }
    }

    private void Apply(EnemyView enemy)
    {
        if (enemy.ObjectId == 0 || enemy.ObjectId == Odyssey.ObjectInvalid)
        {
            return;
        }

        if (enemy.Dead)
        {
            KillOnce(enemy.ObjectId);
            return;
        }

        nint creature = Odyssey.ResolveServerObject(enemy.ObjectId);
        if (creature == 0)
        {
            return;
        }

        // Pin it first. If the local player is mid-swing when the health write lands, an
        // unpinned enemy could be killed locally between passes and then be resurrected by
        // the next mirrored health value, which looks far worse than a stubborn enemy.
        Odyssey.SetMinOneHitPoint(creature, true);

        // Never write 0: the owner has not said it is dead, and an enemy at 0 health reads
        // as dead to targeting and AI while no death path has run for it.
        int health = Math.Max(1, (int)enemy.CurrentHealth);

        if (Odyssey.GetCurrentHitPoints(creature) != health)
        {
            Odyssey.SetCurrentHitPoints(creature, health);
        }
    }

    private void KillOnce(uint objectId)
    {
        for (int i = 0; i < _killedCount; i++)
        {
            if (_killed[i] == objectId)
            {
                return;
            }
        }

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0)
        {
            // Already gone from this instance; remember it so we stop looking.
            Remember(objectId);
            return;
        }

        // The pin has to come off or the death effect just floors health at 1.
        Odyssey.SetMinOneHitPoint(creature, false);

        if (Odyssey.KillCreature(objectId, out int failure))
        {
            Remember(objectId);

            if (_logged < 10)
            {
                _logged++;
                PluginHost.Log($"combat: killed 0x{objectId:X} on the owner's authority");
            }
        }
        else if (_logged < 10)
        {
            _logged++;
            PluginHost.Log($"combat: kill refused for 0x{objectId:X} (failure {failure})");
        }
    }

    private void Remember(uint objectId)
    {
        if (_killedCount < _killed.Length)
        {
            _killed[_killedCount++] = objectId;
        }
    }
}
