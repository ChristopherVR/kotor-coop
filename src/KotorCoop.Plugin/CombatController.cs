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
/// Health comes down from the owner; the contribution going back UP is a separate channel
/// entirely (PeerDamageController and the damage ledger), because health could not carry it
/// — see MirrorHealthRule for the three designs this went through and what each one cost.
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
    /// The owner's previous health per enemy, so a RISE can be applied as the owner's own
    /// delta rather than as an absolute overwrite (see MirrorHealthRule).
    ///
    /// ⚠ Keyed by object id, NOT by slot index. The census re-sorts every publish — enemies
    /// the engine has mid-combat-round first, then by distance to the nearest player — so
    /// slot 3 is a different creature from one pass to the next, and a slot-keyed history
    /// would compare one enemy's health against another's.
    /// </summary>
    private readonly uint[] _lastOwnerId = new uint[SharedBlock.EnemyCapacity];
    private readonly int[] _lastOwnerHealth = new int[SharedBlock.EnemyCapacity];

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
        Array.Clear(_lastOwnerId);
        Array.Clear(_lastOwnerHealth);
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

        // A plot creature rides the census for its PRESENCE only (a cutscene actor whose
        // scene must look the same on both screens). Un-killable is the module's rule:
        // no pin, no health mirror, and never a death — whatever the flags say.
        if (enemy.Plot)
        {
            return;
        }

        if (enemy.Dead)
        {
            KillOnce(enemy.ObjectId);
            return;
        }

        // Translate through the materialisation map: an enemy that exists only in the
        // owner's instance has a locally-spawned stand-in under a different id. An
        // UNMAPPED owner id colliding with one of those stand-ins' local ids must not be
        // touched at all — resolving it would pin and mirror the wrong body.
        uint mapped = MaterialisedEnemies.ToLocal(enemy.ObjectId);
        if (mapped == 0 && MaterialisedEnemies.ToOwner(enemy.ObjectId) != 0)
        {
            return;
        }

        nint creature = Odyssey.ResolveServerObject(mapped != 0 ? mapped : enemy.ObjectId);
        if (creature == 0)
        {
            return;
        }

        // Pin it, always. If the local player is mid-swing when the health write lands, an
        // unpinned enemy could be killed locally between passes and then be resurrected by
        // the next mirrored health value, which looks far worse than a stubborn enemy.
        //
        // ⚠ This used to be released once the owner's own value reached 1, so that a player
        // fighting alone could land the killing blow themselves and report the death back.
        // That crutch is gone with the mechanism it served: our damage now travels to the
        // owner as a SUM, the owner's own engine spends it and kills its copy, and the death
        // comes back down the ordinary channel — so there is no longer any need for a
        // mirrored enemy to be killable here, and every reason for it not to be.
        Odyssey.SetMinOneHitPoint(creature, true);

        // Never write 0: the owner has not said it is dead, and an enemy at 0 health reads
        // as dead to targeting and AI while no death path has run for it.
        int ownerHealth = Math.Max(1, (int)enemy.CurrentHealth);

        // ⚠ SIGNED. Hit points at +0xDC are an int16 and an overkilled body parks at a
        // negative value (−11 for a downed party member), which read unsigned is 65525 —
        // "healthy" to any comparison here. Odyssey's accessor still returns ushort at the
        // time of writing; the cast is correct under both, so it stays even once that is
        // fixed.
        int current = (short)Odyssey.GetCurrentHitPoints(creature);
        int max = Odyssey.GetMaxHitPoints(creature);

        int history = History(enemy.ObjectId);
        bool primed = history >= 0 && _lastOwnerId[history] == enemy.ObjectId;

        int target = MirrorHealthRule.Target(
            current, max, ownerHealth, primed ? _lastOwnerHealth[history] : 0, primed);

        if (history >= 0)
        {
            _lastOwnerId[history] = enemy.ObjectId;
            _lastOwnerHealth[history] = ownerHealth;
        }

        if (target != current)
        {
            Odyssey.SetCurrentHitPoints(creature, target);
        }
    }

    /// <summary>The history slot for an id, claiming a free one if this is the first sight
    /// of it. -1 when the table is full, which degrades to "adopt the owner's value" — the
    /// old absolute behaviour, and the right thing to fall back to.</summary>
    private int History(uint objectId)
    {
        int free = -1;

        for (int i = 0; i < _lastOwnerId.Length; i++)
        {
            if (_lastOwnerId[i] == objectId)
            {
                return i;
            }

            if (free < 0 && _lastOwnerId[i] == 0)
            {
                free = i;
            }
        }

        return free;
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

        // Same translation as Apply: the death of a materialised enemy is executed on its
        // local stand-in, and a collision with an unrelated stand-in's id is left alone.
        uint mapped = MaterialisedEnemies.ToLocal(objectId);
        if (mapped == 0 && MaterialisedEnemies.ToOwner(objectId) != 0)
        {
            Remember(objectId);
            return;
        }

        uint effectiveId = mapped != 0 ? mapped : objectId;

        nint creature = Odyssey.ResolveServerObject(effectiveId);
        if (creature == 0)
        {
            // Already gone from this instance; remember it so we stop looking.
            Remember(objectId);
            return;
        }

        // The pin has to come off or the death effect just floors health at 1.
        Odyssey.SetMinOneHitPoint(creature, false);

        if (Odyssey.KillCreature(effectiveId, out int failure))
        {
            Remember(objectId);

            if (_logged < 10)
            {
                _logged++;
                // Owner id and local id are different number spaces that can coincide by
                // pure chance (a materialised stand-in for one owner can land on the same
                // numeric local id as a totally unrelated owner's real id) - print both, or
                // a reader chasing a suspicious-looking number has no way to tell "this
                // owner id was killed" from "this local id was killed" apart.
                PluginHost.Log(mapped != 0
                    ? $"combat: killed 0x{objectId:X} (owner id, local stand-in 0x{effectiveId:X}) on the owner's authority"
                    : $"combat: killed 0x{objectId:X} on the owner's authority");
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

        // Its fight is over, so free both of the per-enemy tables it was holding a row in.
        // The damage ledger is the one that matters: it is bounded, and a corpse's row would
        // sit there for the rest of the module keeping a live enemy out.
        PhantomHitHook.Dealt.Forget(objectId);

        for (int i = 0; i < _lastOwnerId.Length; i++)
        {
            if (_lastOwnerId[i] == objectId)
            {
                _lastOwnerId[i] = 0;
                _lastOwnerHealth[i] = 0;
                return;
            }
        }
    }
}
