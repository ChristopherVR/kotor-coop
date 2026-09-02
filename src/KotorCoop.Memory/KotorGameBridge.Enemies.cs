// Enemy census: reading every hostile creature in the module (host side) and publishing the
// enemy list into the plugin, which mirrors health and executes deaths on the game thread.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>Enemy sync needs the in-process plugin to apply health and deaths.</summary>
    public bool SupportsEnemySync => _pluginBlock != 0 && _player is not null;
    /// <summary>
    /// Reads every hostile creature in the module. "Hostile" means a different faction
    /// from the player, which is the same test the engine's own targeting uses, and it
    /// keeps party members, neutral NPCs and other players' phantoms out of the list.
    /// </summary>
    public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers)
    {
        List<EnemyState> enemies = [];

        if (_player is null)
        {
            return enemies;
        }

        Vector3f playerPosition = default;
        TryReadVector(_player.Address + OffPosition, out playerPosition);

        // Phantoms are no longer plot-flagged (7.4's redesign: they take visible damage
        // now), so they must be excluded by id or a teammate's body would be published
        // as an enemy, pinned, and mirrored.
        HashSet<uint>? phantomIds = ReadPhantomObjectIds();

        // ⚠ THE PARTY TABLE, not the follow pointer. See PartyCensusRule: a companion whose
        // +0x4C0 follow pointer has not been (re)attached yet reads exactly like a hostile
        // stranger, and a transition is precisely when that happens — so the host published
        // Trask into the enemy census and the client built a Trask-shaped stand-in next to the
        // party. The roster is a membership record and is right in that window.
        HashSet<uint> partyRoster = ReadPartyRosterIds();

        Dictionary<uint, (uint Serial, ushort Animation)>? oneShots = ReadCreatureOneShots();

        // Two passes: collect every candidate, then keep the ones that matter most. A
        // module can hold more creatures than the census cap, and truncating in scan
        // order shed arbitrary bodies; hostiles go first (a fight that desyncs is worse
        // than a bystander that does), then the nearest bystanders — which is also where
        // scripted scenes play, next to the player watching them.
        List<(EnemyState State, bool Fighting, float DistanceSquared)> candidates = [];

        foreach (CreatureRef creature in EnumerateCreatures())
        {
            if (PartyCensusRule.IsOwnParty(
                    creature.ObjectId, creature.IsPlayer, creature.IsPartyMember, partyRoster))
            {
                continue;
            }

            if (phantomIds is not null && phantomIds.Contains(creature.ObjectId))
            {
                continue;
            }

            // Belt over the id table: the plugin clears a phantom's slot before the body has
            // finished dying, and one census pass in that window published the freed body as
            // a dead enemy (measured on the tar_m03aa warp — the peer dutifully executed the
            // kill on a stand-in it materialised for the ghost). The tag names every body the
            // co-op blueprint spawned — phantoms and materialised stand-ins alike, none of
            // which is ever this instance's enemy to publish — and it survives to the last
            // readable moment.
            if (creature.Tag == GameFiles.PhantomBlueprint.Tag)
            {
                continue;
            }

            // A plot creature — usually a cutscene actor — is included for PRESENCE, so
            // scripted scenes play out identically on both screens, but flagged so the
            // combat half leaves its health alone: un-killable is the module's rule.
            //
            // No faction filter any more, deliberately. It once excluded "friendly" NPCs,
            // but a story NPC in the player's faction (Carth on the Endar Spire bridge,
            // pre-join companions generally) is EXACTLY a cutscene actor whose presence
            // must sync — measured live: Carth was the one body the census dropped. The
            // exclusions that mattered are explicit: the player and party by their own
            // checks above, phantoms by id.
            bool plot = _memory.TryReadInt32(creature.Address + OffPlot, out int plotFlag) && plotFlag != 0;

            // Priority signal, not a hostility oracle: a creature the engine itself has
            // mid-combat-round must never be shed by the cap. Faction semantics are a
            // matrix lookup we cannot make externally (docs/research/combat-participation.md
            // §2), so the engine's own in-combat flag is the honest signal.
            bool fighting = !plot
                && _memory.TryReadInt32(creature.Address + OffInCombat, out int inCombat)
                && inCombat != 0;

            // Signed: an overkilled enemy sits at negative hit points, and an unsigned
            // read would report it as 65531 hp and alive — the death would never
            // replicate.
            //
            // ⚠ AND THE READ'S RESULT IS HONOURED, because `current <= 0` becomes Dead below.
            // Discarding the bool meant a FAILED read produced current = 0 and published a
            // perfectly healthy creature as a corpse. Measured: the client killed eight or
            // nine creatures within ~100 ms of the host opening a conversation, four times out
            // of four, and five to eight seconds later the same ids came back alive and were
            // spawned again as duplicate stand-ins. Cross-process reads fail transiently — the
            // agent's own readout has a READ-FAILING counter that reached a thousand — so this
            // is not a theoretical branch.
            //
            // Skipping the creature is the right failure: presence is a LEVEL re-sent every
            // tick, so a body omitted from one snapshot is simply described by the next, while
            // a body wrongly marked dead is acted on immediately and irreversibly.
            if (!_memory.TryRead(creature.Address + OffCurrentHitPoints, out short current))
            {
                continue;
            }

            ushort max = (ushort)Math.Clamp(TryReadMaxHitPoints(creature.Address), 0, ushort.MaxValue);

            // Presence: where this engine has the body and what it is playing, so the
            // mirroring side can drive its copy there. Best-effort — a failed read leaves
            // the defaults, which the plugin's driver rejects as implausible.
            TryReadVector(creature.Address + OffPosition, out Vector3f position);

            float facing = 0f;
            if (TryReadVector(creature.Address + OffOrientation, out Vector3f orientation))
            {
                facing = Angles.Normalize(MathF.Atan2(orientation.Y, orientation.X));
            }

            _memory.TryReadUInt32(creature.Address + OffAnimation, out uint animation);

            // What the creature looks like and whose side it is on — enough for a mirror
            // that has no copy at all to build a stand-in (no blueprint name survives on a
            // live creature; see Packets.EnemyState). Best-effort: zero appearance simply
            // means the mirror will not materialise this one.
            _memory.TryRead(creature.Address + OffAppearanceType, out ushort appearance);

            ushort faction = 0;
            if (_memory.TryReadUInt32(creature.Address + OffCreatureStats, out uint statsPtr)
                && statsPtr != 0
                && _memory.TryReadUInt32((nint)statsPtr + StatsFaction, out uint factionId)
                && factionId <= ushort.MaxValue)
            {
                faction = (ushort)factionId;
            }

            (uint oneShotSerial, ushort oneShotAnim) =
                oneShots is not null && oneShots.TryGetValue(creature.ObjectId, out var shot)
                    ? shot
                    : (0u, (ushort)0);

            // ⚠ Distance to the NEAREST PLAYER, not to us.
            //
            // The cap sheds by this number, and a census measured from one player only can
            // see one place at a time. Two players a room apart is the ordinary case, and
            // it is exactly when the publisher's own surroundings are the wrong thing to
            // measure: the creatures around the partner — the ones they are about to fight —
            // sort last and fall off the list, so their copies stop being driven and stop
            // being health-mirrored at the moment they start to matter. Peers are passed in
            // already filtered to this module.
            float dx = position.X - playerPosition.X;
            float dy = position.Y - playerPosition.Y;
            float nearestSquared = (dx * dx) + (dy * dy);

            for (int p = 0; p < otherPlayers.Count; p++)
            {
                float px = position.X - otherPlayers[p].X;
                float py = position.Y - otherPlayers[p].Y;
                float squared = (px * px) + (py * py);
                if (squared < nearestSquared)
                {
                    nearestSquared = squared;
                }
            }

            candidates.Add((
                new EnemyState(
                    creature.ObjectId, (ushort)Math.Max((int)current, 0), max, current <= 0,
                    position, facing, animation, oneShotSerial, oneShotAnim, plot,
                    appearance, faction),
                fighting,
                nearestSquared));
        }

        candidates.Sort((a, b) => a.Fighting != b.Fighting
            ? (a.Fighting ? -1 : 1)
            : a.DistanceSquared.CompareTo(b.DistanceSquared));

        // _enemyCensusCap, not BlockEnemyCapacity directly: normally the same 32, but a debug
        // session can lower it (AgentOptions.EnemyCensusCap) to exercise the "beyond the cap"
        // shedding this truncation performs without needing a module that legitimately places
        // 33+ creatures. See _enemyCensusCap's own doc comment.
        int count = Math.Min(candidates.Count, _enemyCensusCap);
        for (int i = 0; i < count; i++)
        {
            enemies.Add(candidates[i].State);
        }

        // The last word before these ids leave this instance: every one of them must be an id
        // this engine can resolve, because that is the only thing that makes it an id a peer
        // can key a forwarded kill or a damage debt by.
        return ConfirmPublished(enemies);
    }


    /// <summary>
    /// The plugin's captured fire-and-forget table (swings, parries, draws), keyed by
    /// object id, or null with no plugin attached. Ids that are not enemies (companions,
    /// neutral NPCs also swing) are simply never matched by the census.
    /// </summary>
    private Dictionary<uint, (uint Serial, ushort Animation)>? ReadCreatureOneShots()
    {
        if (_pluginBlock == 0)
        {
            return null;
        }

        Dictionary<uint, (uint, ushort)>? shots = null;
        for (int i = 0; i < BlockEnemyOneShotCapacity; i++)
        {
            nint slot = _pluginBlock + BlockEnemyOneShotTable + (i * BlockEnemyOneShotStride);

            if (!_memory.TryReadUInt32(slot + 0x00, out uint id) || id == 0)
            {
                continue;
            }

            _memory.TryReadUInt32(slot + 0x04, out uint animation);
            _memory.TryReadUInt32(slot + 0x08, out uint serial);

            if (serial != 0)
            {
                (shots ??= [])[id] = (serial, (ushort)animation);
            }
        }

        return shots;
    }

    /// <summary>The plugin's live phantom object ids, or null when no plugin is attached.</summary>
    private HashSet<uint>? ReadPhantomObjectIds()
    {
        if (_pluginBlock == 0)
        {
            return null;
        }

        HashSet<uint>? ids = null;
        for (int i = 0; i < BlockSlotCapacity; i++)
        {
            // ⚠ BlockPhantomIdTable, not BlockPhantomIds. Reading eight dwords from 0x264
            // read the keep-alive flag and the focus keeper's pause counter as if they were
            // object ids, so ids 1, 2, 3... were quietly struck out of the enemy census as
            // "that's a phantom" — and the counter climbs, so the set of creatures it hid
            // drifted over a session.
            if (_memory.TryReadUInt32(_pluginBlock + BlockPhantomIdTable + (i * 4), out uint id) && id != 0)
            {
                (ids ??= []).Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Version 61: the owner ids of every mirrored enemy the plugin is currently letting fight
    /// the local player for real under `local_combat` (up to
    /// <c>BlockLocalCombatExcludedIdCapacity</c>/3 simultaneous slots), the N-wide sibling of
    /// the pre-version-61 single-id channel (<c>BlockLocalCombatExcludedId</c>, kept written by
    /// the plugin as a slot-0 mirror but not read here any more — see that field's own
    /// comment). The caller's job is to keep every one of these ids out of whatever it writes
    /// into <c>EnemyPresenceCount</c>'s array — genuinely absent, not merely un-driven — which
    /// is what actually excludes them from the census the way round 37/38 proved works. See
    /// <c>SharedBlock.OffsetLocalCombatExcludedIds</c> and
    /// docs/research/combat-participation.md §11.
    /// </summary>
    public IReadOnlyList<uint> ReadLocalCombatExcludedIds()
    {
        if (_pluginBlock == 0)
        {
            return [];
        }

        List<uint> ids = new(BlockLocalCombatExcludedIdCapacity);
        for (int i = 0; i < BlockLocalCombatExcludedIdCapacity; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockLocalCombatExcludedIds + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private const int BlockMaterialisedOwnerIds = 0x4B00;
    private const int BlockMaterialisedOwnerIdCapacity = BlockEnemyCapacity;

    /// <summary>
    /// Version 65: the owner ids <c>MaterialisedEnemies</c> currently has a LOCAL STAND-IN body
    /// for, straight off the plugin's own table — so a materialised-loot give can be filtered
    /// BEFORE it costs a round trip on the single-slot giveloot cheat channel, instead of only
    /// discovering the refusal after spending it (<c>Session.MaterialisedLootFilterRule</c>).
    ///
    /// Null means UNKNOWN (no plugin attached). ⚠ That is the ONLY case that means "don't
    /// know": <c>PluginSignature</c> embeds the block version byte-exact in the scan pattern
    /// (see that field's own remarks), so a version-mismatched agent/plugin pair never finds
    /// the block at all rather than reading it half-right — there is no partial-version case
    /// to worry about here. Callers MUST treat null as "send it anyway"; only a real, if
    /// possibly empty, set licenses filtering by membership. See
    /// <c>SharedBlock.OffsetMaterialisedOwnerIds</c> and
    /// docs/research/enemy-materialisation.md's 2026-09-02 section.
    /// </summary>
    public IReadOnlySet<uint>? ReadMaterialisedOwnerIds()
    {
        if (_pluginBlock == 0)
        {
            return null;
        }

        HashSet<uint> ids = new(BlockMaterialisedOwnerIdCapacity);
        for (int i = 0; i < BlockMaterialisedOwnerIdCapacity; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockMaterialisedOwnerIds + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// What this instance has DEALT to each enemy, straight out of the plugin's ledger.
    ///
    /// The replacement for reporting enemy HEALTH back to the fight's owner, and the reason
    /// is the whole point of the channel: health is a level that has to be sampled, and the
    /// owner's mirror writes its own value back onto our copy between samples, so a hit had
    /// to win a race to be reported at all. Measured live — a client attacking one soldier
    /// for ninety seconds moved it from 15 hit points to 11 and no further. A cumulative
    /// sum of damage dealt cannot be raced, because nothing ever reads it back out of the
    /// game.
    /// </summary>
    public (uint Generation, List<EnemyDamageEntry> Damage) ReadDamageDealt()
    {
        List<EnemyDamageEntry> damage = [];

        if (_pluginBlock == 0)
        {
            return (0, damage);
        }

        // Opting in is what turns the accumulation on inside the engine's damage hook. Only
        // an instance that MIRRORS enemies needs it; asking for it here rather than having
        // the plugin guess keeps the decision with the side that knows who owns the fight.
        if (!_damageDealtEnabled)
        {
            _damageDealtEnabled = _memory.TryWrite(_pluginBlock + BlockDamageDealtEnabled, 1u);
            return (0, damage);
        }

        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtGeneration, out uint generation);
        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtCount, out uint count);

        for (int i = 0; i < Math.Min(count, BlockDamageLedgerCapacity); i++)
        {
            nint slot = _pluginBlock + BlockDamageDealtSlots + (i * BlockDamageLedgerStride);

            if (!_memory.TryReadUInt32(slot + 0x00, out uint objectId) || objectId == 0)
            {
                continue;
            }

            if (_memory.TryReadUInt32(slot + 0x04, out uint cumulative) && cumulative > 0)
            {
                damage.Add(new EnemyDamageEntry(objectId, cumulative));
            }
        }

        return (generation, damage);
    }

    /// <summary>
    /// Whether we have told the plugin to start accumulating.
    ///
    /// ⚠ MUST BE CLEARED WHENEVER THE BLOCK FIELD IS. Teardown now lowers the flag in the
    /// block (it is what the portrait hiding, the companion faction hold and the control
    /// return all key on), so a latch that survived teardown would mean the next session
    /// never raised it again — every one of those would silently stay off for the rest of
    /// the process. <see cref="ClearCoopState"/> resets both together.
    /// </summary>
    private bool _damageDealtEnabled;

    /// <summary>Hits the plugin's ledger had no room for. Non-zero means a peer's damage is
    /// being under-reported — the exact failure this channel exists to remove — so it is
    /// surfaced rather than swallowed.</summary>
    public uint ReadDamageDealtDropped()
    {
        if (_pluginBlock == 0)
        {
            return 0;
        }

        _memory.TryReadUInt32(_pluginBlock + BlockDamageDealtDropped, out uint dropped);
        return dropped;
    }

    /// <summary>
    /// Hands the plugin the total every peer has dealt to each of our enemies, for it to
    /// spend as real engine damage on the game thread.
    ///
    /// ⚠ The count is zeroed FIRST and restored LAST, so the plugin either sees the whole
    /// table or none of it. Elsewhere in this block an id written last is enough, because a
    /// torn row costs a frame of a wrong position; here it would spend damage against the
    /// wrong creature. A pass the plugin catches mid-write simply does nothing, and the
    /// totals are cumulative levels, so the next pass makes it whole.
    ///
    /// The epoch is the module the ids belong to — the plugin drops its own applied totals
    /// when it changes, so the two halves restart together rather than the plugin holding a
    /// high applied total against an agent that has already gone back to zero.
    /// </summary>
    public bool TryPublishDamageOwed(uint epoch, IReadOnlyList<EnemyDamageEntry> owed)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockDamageOwedCount, 0u);
        int count = Math.Min(owed.Count, BlockDamageLedgerCapacity);

        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockDamageOwedSlots + (i * BlockDamageLedgerStride);
            ok &= _memory.TryWrite(slot + 0x04, owed[i].CumulativeDamage);
            ok &= _memory.TryWrite(slot + 0x00, owed[i].ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockDamageOwedEpoch, epoch);
        ok &= _memory.TryWrite(_pluginBlock + BlockDamageOwedCount, (uint)count);
        return ok;
    }

    /// <summary>
    /// Publishes the owner's enemy list into the plugin, which mirrors the health and
    /// executes the deaths on the game's own thread.
    /// </summary>
    public bool TryWriteEnemies(IReadOnlyList<EnemyState> enemies)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;
        int count = Math.Min(enemies.Count, BlockEnemyCapacity);

        for (int i = 0; i < count; i++)
        {
            EnemyState enemy = enemies[i];
            nint slot = _pluginBlock + BlockEnemySlots + (i * BlockEnemyStride);

            uint flags = (enemy.Dead ? EnemyFlagDead : 0u) | (enemy.Plot ? EnemyFlagPlot : 0u);

            ok &= _memory.TryWrite(slot + 0x04, enemy.CurrentHealth);
            ok &= _memory.TryWrite(slot + 0x06, enemy.MaxHealth);
            ok &= _memory.TryWrite(slot + 0x08, flags);

            // Object id last, so the plugin never reads a slot whose id and payload
            // belong to different enemies.
            ok &= _memory.TryWrite(slot + 0x00, enemy.ObjectId);

            // The presence half, in its own region: the owner's position, facing and
            // animation, which the plugin's driver moves the local copy to match. Same
            // id-last discipline per slot.
            nint presence = _pluginBlock + BlockEnemyPresenceSlots + (i * BlockEnemyPresenceStride);
            ok &= _memory.TryWrite(presence + 0x04, enemy.Position.X);
            ok &= _memory.TryWrite(presence + 0x08, enemy.Position.Y);
            ok &= _memory.TryWrite(presence + 0x0C, enemy.Position.Z);
            ok &= _memory.TryWrite(presence + 0x10, enemy.Facing);
            ok &= _memory.TryWrite(presence + 0x14, enemy.Animation);
            ok &= _memory.TryWrite(presence + 0x18, flags);
            ok &= _memory.TryWrite(presence + 0x1C, enemy.OneShotSerial);
            ok &= _memory.TryWrite(presence + 0x20, (uint)enemy.OneShotAnimation);
            ok &= _memory.TryWrite(presence + 0x24, enemy.Appearance);
            ok &= _memory.TryWrite(presence + 0x26, enemy.Faction);
            ok &= _memory.TryWrite(presence + 0x00, enemy.ObjectId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockEnemyPresenceCount, (uint)count);
        ok &= _memory.TryWrite(_pluginBlock + BlockEnemyCount, (uint)count);
        return ok;
    }

}
