using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// The owner-id → local-id indirection for enemies this instance has NO copy of.
///
/// The enemy channel keys on object-id identity: both instances load the same save, ids
/// are assigned deterministically in GIT order, so the owner's 0x14 is our 0x14. That
/// breaks for anything the owner's engine CREATES after load — an encounter spawn, a
/// scripted wave — which exists in exactly one instance. Before this registry the mirror
/// silently skipped such enemies (`ResolveServerObject` returned 0 and every controller
/// gave up), so a host-triggered ambush was invisible to the client while still able to
/// hurt them through their phantom: damage from nothing you can see.
///
/// No blueprint name survives on a live creature to replicate from (the resref is
/// consumed by LoadFromTemplate, not stored — and the tag is NOT a reliable proxy:
/// measured against the shipped data, 53 of 156 encounter CreatureList entries have a tag
/// that differs from the resref). So the stand-in is built the way phantoms are: spawn
/// the co-op blueprint, repaint it with the owner's published appearance.2da row, and put
/// it in the owner's published faction. Position, gait, swings, health and death all
/// arrive through the channels that already exist — they just needed a local body to
/// land on, and a translation between the two id spaces:
///
///   owner id  → local id   when resolving (EnemyPresenceController, CombatController)
///   local id  → owner id   when reporting damage dealt (PhantomHitHook)
///
/// All calls happen on the game thread via the frame hook. Kill switch:
/// coop.ini enemy_materialise=0.
/// </summary>
internal static unsafe class MaterialisedEnemies
{
    /// <summary>Same blueprints phantoms use, tried in order; guaranteed present because
    /// the co-op one ships in override and the commoner is stock.</summary>
    private static readonly string[] Templates = ["k_coop_phantom", "n_commm"];

    /// <summary>Seconds an entry may go unmentioned by the census before its body is
    /// destroyed. Long enough to ride out the owner's dialogue hold-and-resume; short
    /// enough that a despawned encounter does not leave ghosts standing around.</summary>
    private const double StaleSeconds = 5.0;

    /// <summary>Frames between spawn attempts, matching the phantom spawner's cadence —
    /// a spawn is a VM dispatch and one per half-second is plenty for an ambush of four.</summary>
    private const int SpawnCooldownFrames = 30;

    /// <summary>Consecutive failed spawns before materialisation latches off for the
    /// module. Invalidate() (a module change) clears the latch.</summary>
    private const int MaxSpawnFailures = 5;

    private static readonly bool Enabled = PluginConfig.ReadBool("enemy_materialise", true);

    private static readonly uint[] OwnerIds = new uint[SharedBlock.EnemyCapacity];
    private static readonly uint[] LocalIds = new uint[SharedBlock.EnemyCapacity];
    private static readonly MaterialisedOwnerRejections RejectedOwners = new();
    private static readonly RetainedEnemyBodyMap RetainedBodies = new(SharedBlock.EnemyCapacity);
    private static SharedBlockView? _sharedBlock;
    private static readonly nint[] Bodies = new nint[SharedBlock.EnemyCapacity];
    private static readonly double[] LastSeen = new double[SharedBlock.EnemyCapacity];
    private static readonly double[] SpawnedAt = new double[SharedBlock.EnemyCapacity];

    /// <summary>The appearance.2da row the OWNER published for this stand-in — the only source
    /// there is for what it should look like, kept per slot so <see cref="Dress"/> can keep
    /// trying after the spawn frame.</summary>
    private static readonly uint[] Appearances = new uint[SharedBlock.EnemyCapacity];

    /// <summary>The host's blueprint a slot's puppet was built from (host spawns only), or null
    /// for a generic stand-in. A living puppet is handed back as a real creature of this
    /// blueprint when the session ends (<see cref="ReleaseSession"/>).</summary>
    private static readonly string?[] PuppetTemplates = new string?[SharedBlock.EnemyCapacity];

    /// <summary>Per-BODY latch: has the owner's appearance row reached this stand-in's CLIENT
    /// TWIN, which is the half that actually renders? See <see cref="Dress"/>.</summary>
    private static readonly bool[] TwinDressed = new bool[SharedBlock.EnemyCapacity];

    private static int _spawnCooldown;
    private static int _spawnFailures;
    private static bool _spawnDisabled;
    private static int _logged;
    private static int _falseAlarmsLogged;
    private static int _puppetsLogged;

    /// <summary>How long a newly seen owner id waits for its blueprint (host spawns only) before
    /// a generic stand-in is built. The host sends both in one tick.</summary>
    private const double TemplateWaitSeconds = 0.5;

    private static readonly Dictionary<uint, double> FirstSeenWithoutTemplate = [];

    /// <summary>Owner ids a full sweep has PROVED are present despite the cheap resolver
    /// returning 0. Without this the pair (failing resolver, authoritative sweep) sweeps the
    /// whole process's memory several times a second for the life of the module — silently,
    /// once the false-alarm log has spent its five lines.</summary>
    private static readonly uint[] ProvenPresent = new uint[SharedBlock.EnemyCapacity];

    private static int _provenCount;

    /// <summary>
    /// How long after its spawn a body is protected from being condemned as unresolvable.
    ///
    /// Measured live (21:44 session, owner 0x3B): a stand-in's fresh id can fail the cheap
    /// resolver on its very first frame — cause not yet pinned down — and the old response
    /// was destroy-and-respawn at the spawn cooldown's cadence: five bodies burned in two
    /// seconds, five ticks of object-id counter drift, before one happened to stick. A
    /// body that is genuinely gone still gets destroyed, one second later; a body the
    /// resolver was merely late on survives its infancy.
    /// </summary>
    private const double BirthGraceSeconds = 1.0;

    /// <summary>True while an owner id's stand-in is too young to be condemned for failing
    /// to resolve (see <see cref="BirthGraceSeconds"/>).</summary>
    public static bool InBirthGrace(uint ownerId, double now)
    {
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId)
            {
                return now - SpawnedAt[i] < BirthGraceSeconds;
            }
        }

        return false;
    }

    /// <summary>The local id standing in for an owner id, or 0 when the owner's id is
    /// expected to resolve directly (the ordinary GIT-identity case).</summary>
    public static uint ToLocal(uint ownerId)
    {
        uint retained = RetainedBodies.ToLocal(ownerId, Odyssey.GetPartyRosterObjectId);
        if (retained != 0)
        {
            return retained;
        }

        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId)
            {
                TryGetCurrentBody(i, out _);
                return RejectedOwners.Resolve(ownerId, LocalIds[i]); // null lookup retains birth-grace mapping
            }
        }

        return RejectedOwners.Resolve(ownerId, 0);
    }

    /// <summary>The owner id a local body stands in for, or 0 when the local id is not a
    /// materialised body. Used to key damage reports in the owner's id space — and, by
    /// callers about to resolve, to detect that an owner id NUMERICALLY collides with one
    /// of our own spawned bodies and must not be resolved as if it were GIT-identical.</summary>
    public static uint ToOwner(uint localId)
    {
        uint retained = RetainedBodies.ToOwner(localId, Odyssey.GetPartyRosterObjectId);
        if (retained != 0)
        {
            return retained;
        }

        for (int i = 0; i < LocalIds.Length; i++)
        {
            if (LocalIds[i] == localId)
            {
                TryGetCurrentBody(i, out _);
                return OwnerIds[i];
            }
        }

        return 0;
    }

    public static bool IsRetainedOwner(uint ownerId) =>
        RetainedBodies.ContainsOwner(ownerId, Odyssey.GetPartyRosterObjectId);

    public static bool IsRetainedLocal(uint localId) =>
        RetainedBodies.ContainsLocal(localId, Odyssey.GetPartyRosterObjectId);

    public static int RetainedCount => RetainedBodies.Count;

    public static bool QuarantineRetainedLocal(uint localId, int rosterSlot) =>
        RetainedBodies.QuarantineLocal(localId, rosterSlot);

    public static bool IsQuarantinedRetainedLocal(uint localId) =>
        RetainedBodies.IsQuarantinedLocal(localId);

    public static bool ReleaseRetainedQuarantineIfActive(
        uint localId, int rosterSlot, bool positivelyProvenActive) =>
        RetainedBodies.ReleaseQuarantineIfActive(localId, rosterSlot, positivelyProvenActive);

    /// <summary>Reserves any current or previously retained Lehon slot-4/5 owner id, regardless
    /// of mask readability/state. Consumers defer raw-id health/death writes until the stricter
    /// dismissed-roster and hostile-row predicate in <see cref="TryGetCurrentRetainedLocal"/>
    /// succeeds.</summary>
    public static bool IsRetainedOwnerCandidate(uint ownerId, SharedBlockView block)
    {
        if (!PartyCensusRule.IsRosterId(ownerId) || !IsLehonModule())
        {
            return false;
        }

        if (RetainedBodies.IsOwnerTombstoned(ownerId))
        {
            return true; // a formerly retained owner never falls through to a raw local id
        }

        for (int slot = 4; slot <= 5; slot++)
        {
            if (block.GetHostPartyRosterObjectId(slot) == ownerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Validates the current owner/local retained alias against module, both party
    /// tables, the host enemy row, local live-creature identity and current hostile faction.</summary>
    public static bool TryGetCurrentRetainedLocal(uint ownerId, SharedBlockView block, out uint localId)
    {
        localId = 0;
        if (!RetainedBodies.TryGetOwnerEntry(ownerId, out RetainedEnemyBody entry)
            || !IsLehonModule()
            || !block.TryGetHostPartyRoster(out uint hostAvailable, out uint hostParty)
            || !Odyssey.TryReadPartyRoster(out uint localAvailable, out uint localParty))
        {
            return false;
        }

        uint enemyCount = Math.Min(block.EnemyPresenceCount, (uint)SharedBlock.EnemyCapacity);
        bool hasHostEnemyRow = false;
        for (int i = 0; i < enemyCount; i++)
        {
            EnemyPresenceView enemy = block.GetEnemyPresence(i);
            if (enemy.ObjectId == ownerId
                && enemy.Faction == DarkSideHostileCompanionRule.HostileFaction)
            {
                hasHostEnemyRow = true;
                break;
            }
        }

        nint creature = Odyssey.ResolveServerObject(entry.LocalId);
        if (creature == 0
            || !PartyCreatureIdentity.IsLiveMatch(*(nint*)creature, *(uint*)(creature + ObjId), entry.LocalId)
            || !RetainedEnemyHandoffProof.IsCurrent(
                lehonModule: true,
                entry.RosterSlot,
                ownerId,
                entry.LocalId,
                block.GetHostPartyRosterObjectId(entry.RosterSlot),
                block.GetLocalPartyRosterObjectId(entry.RosterSlot),
                Odyssey.GetPartyRosterObjectId(entry.RosterSlot),
                hostAvailable,
                hostParty,
                localAvailable,
                localParty,
                hasHostEnemyRow,
                DarkSideHostileCompanionRule.HostileFaction,
                Odyssey.GetCreatureFaction(creature),
                requireHostileLocalFaction: true))
        {
            return false;
        }

        localId = entry.LocalId;
        return true;
    }

    /// <summary>
    /// The death-verdict form of <see cref="TryGetCurrentRetainedLocal"/>. The owner's death of
    /// a retained body removes the very evidence the live proof needs: its hostile enemy row
    /// (the corpse stops being published) and often its host roster id. Live 2026-09-25: the
    /// host slew Jolee and Juhani on Lehon and the joiner's copies stood at min-1-hp forever,
    /// every kill deferred. A death is safe to apply on less: a committed alias for this owner,
    /// the same module, a live local body that is still the one registered and still hostile
    /// (we made it so at handoff), and the slot still out of this instance's party.
    /// </summary>
    public static bool TryGetRetainedLocalForDeath(uint ownerId, out uint localId)
    {
        localId = 0;
        RetainedEnemyBody entry = default;
        if (!(RetainedBodies.TryGetOwnerEntry(ownerId, out entry)
                || RetainedBodies.TryGetTombstonedBody(ownerId, out entry))
            || !IsLehonModule()
            || !Odyssey.TryReadPartyRoster(out uint localAvailable, out uint localParty))
        {
            return false;
        }

        uint bit = 1u << entry.RosterSlot;
        if ((localAvailable & bit) != 0 || (localParty & bit) != 0)
        {
            return false;
        }

        nint creature = Odyssey.ResolveServerObject(entry.LocalId);
        if (creature == 0
            || !PartyCreatureIdentity.IsLiveMatch(*(nint*)creature, *(uint*)(creature + ObjId), entry.LocalId)
            || Odyssey.GetCreatureFaction(creature) != DarkSideHostileCompanionRule.HostileFaction)
        {
            return false;
        }

        localId = entry.LocalId;
        return true;
    }

    public static bool TryGetCurrentRetainedOwner(uint localId, SharedBlockView block, out uint ownerId)
    {
        ownerId = 0;
        if (!RetainedBodies.TryGetLocalEntry(localId, out RetainedEnemyBody entry)
            || !TryGetCurrentRetainedLocal(entry.OwnerId, block, out uint currentLocalId)
            || currentLocalId != localId)
        {
            return false;
        }

        ownerId = entry.OwnerId;
        return true;
    }

    public static bool TryGetCurrentRetainedOwner(uint localId, out uint ownerId)
    {
        SharedBlockView? block = _sharedBlock;
        if (block is null)
        {
            ownerId = 0;
            return false;
        }

        return TryGetCurrentRetainedOwner(localId, block, out ownerId);
    }

    public static void BindSharedBlock(SharedBlockView block) => _sharedBlock = block;

    private static bool IsLehonModule()
    {
        byte* module = stackalloc byte[9];
        module[0] = (byte)'u';
        module[1] = (byte)'n';
        module[2] = (byte)'k';
        module[3] = (byte)'_';
        module[4] = (byte)'m';
        module[5] = (byte)'4';
        module[6] = (byte)'4';
        module[7] = (byte)'a';
        module[8] = (byte)'c';
        return Odyssey.ModuleNameEquals(module, 9);
    }

    /// <summary>Registers an existing local roster body as the local representation of an
    /// owner's hostile roster body. The party-table slot and authoritative object resolver are
    /// checked first. This never spawns, dresses, or destroys the native body.</summary>
    public static bool RegisterRetained(uint ownerId, uint localId, int rosterSlot)
    {
        if (!PartyCensusRule.IsRosterId(ownerId)
            || !PartyCensusRule.IsRosterId(localId)
            || rosterSlot is not (4 or 5)
            || Odyssey.GetPartyRosterObjectId(rosterSlot) != localId)
        {
            return false;
        }

        nint body = Odyssey.ResolveServerObject(localId);
        if (body == 0
            || !PartyCreatureIdentity.IsLiveMatch(*(nint*)body, *(uint*)(body + ObjId), localId))
        {
            return false;
        }

        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId || LocalIds[i] == localId)
            {
                return false;
            }
        }

        return RetainedBodies.Register(ownerId, localId, rosterSlot, 0);
    }

    /// <summary>Revalidates every alias against positive current-frame campaign evidence. The
    /// caller must supply a predicate that includes the active module, both roster states, and
    /// the host enemy row; absence of evidence invalidates the damage/presence translation.</summary>
    public static int PruneUnprovenRetained(Func<RetainedEnemyBody, bool> isCurrentlyProven) =>
        RetainedBodies.PruneUnproven(isCurrentlyProven);

    public static int ClearRetainedAliases() => RetainedBodies.ReleaseSession();

    public static int DisableRetainedAliases() => RetainedBodies.ClearAliasesKeepGuards();

    /// <summary>
    /// Owner ids for plugin-spawned stand-ins, in fixed slot order (0 = unused) — retained
    /// native party aliases live in <see cref="RetainedBodies"/> and are deliberately absent.
    /// The array is exposed read-only so <see cref="EnemyPresenceController"/>
    /// can publish it verbatim into the shared block
    /// (<c>SharedBlockView.SetMaterialisedOwnerIds</c>, version 65) every pass it sweeps this
    /// table. Lets the agent tell, BEFORE spending a giveloot round trip on the single-slot
    /// cheat channel, whether an owner id names one of our own stand-ins or (the overwhelmingly
    /// common case) a peer holding the real, GIT-identical creature. See
    /// docs/research/enemy-materialisation.md's 2026-09-02 section.
    /// </summary>
    public static ReadOnlySpan<uint> OwnerIdsSnapshot => OwnerIds;

    public static ReadOnlySpan<uint> LocalIdsSnapshot => LocalIds;

    /// <summary>Records that the census still lists this owner id, keeping its entry (and
    /// body) alive. Called for every published slot every frame.</summary>
    public static void Seen(uint ownerId, double now)
    {
        RetainedBodies.Seen(ownerId, now);
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId)
            {
                LastSeen[i] = now;
                return;
            }
        }
    }

    /// <summary>
    /// Spawns a stand-in for an owner enemy that resolved to nothing locally. Rate-limited
    /// and latch-guarded like the phantom spawner; a failure is retried on a later pass.
    /// MAIN THREAD ONLY (VM dispatch).
    /// </summary>
    /// <param name="knownCollision">The owner id numerically equals a body this instance
    /// spawned (a peer's phantom or another owner's stand-in). The existence sweep would find
    /// that body and wrongly prove the enemy present, so it is skipped.</param>
    public static void TryMaterialise(in EnemyPresenceView enemy, double now, bool knownCollision = false)
    {
        if (!Enabled || _spawnDisabled || enemy.Dead || enemy.Appearance == 0
            || RetainedBodies.ContainsOwner(enemy.ObjectId, Odyssey.GetPartyRosterObjectId))
        {
            return;
        }

        if (_spawnCooldown > 0)
        {
            return;
        }

        // ⚠ NEVER SWEEP TWICE FOR THE SAME ID. The pair (a resolver that says absent, a sweep
        // that proves present) is a PERMANENT LOOP without this: the false-alarm branch below
        // proved the creature exists, logged, and returned WITHOUT recording it, so the next
        // pass resolved 0 again, the cooldown expired again, and it swept again — a
        // full-process memory sweep several times a second for the rest of the module.
        // Measured live on the client: five sweeps 199/202/207/197 ms apart, then apparent
        // silence, because the log caps at five lines while the loop runs on. Zero on the host.
        for (int i = 0; i < _provenCount; i++)
        {
            if (ProvenPresent[i] == enemy.ObjectId && !knownCollision)
            {
                return;
            }
        }

        int slot = -1;
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == enemy.ObjectId)
            {
                return; // already materialised (caller races a spawn from this frame)
            }

            if (slot < 0 && OwnerIds[i] == 0)
            {
                slot = i;
            }
        }

        if (slot < 0)
        {
            return; // table full; the census cap is the same size, so this is transient
        }

        // ⚠ NEVER BUILD A SECOND COPY OF ONE OF OUR OWN COMPANIONS (docs/research/name-plates.md,
        // "the census leak"; the rule is PartyCensusRule). The publisher is supposed to keep its
        // party out of the census, and now does — but this side must refuse too, because the
        // publisher may be an older build and because the two engines number party bodies
        // independently (the party table spawns them at load; they are not GIT-placed), so a
        // companion's id can arrive here matching nothing and sail straight past the "does it
        // already exist locally" sweep below that stops every other duplicate.
        if (!MayMaterialise(enemy))
        {
            return;
        }

        // Take the cooldown BEFORE the existence check, so a repeated false alarm costs one
        // memory sweep per half-second rather than one per frame.
        _spawnCooldown = SpawnCooldownFrames;

        // ⚠ PROVE the enemy is really absent before building a body for it. The caller's
        // evidence is "the resolver returned 0", and that has already been wrong once: the
        // resolver used to reject live creatures on a bad type test, and this code answered
        // by spawning a duplicate every time the cooldown expired — 13 stray Sith soldiers
        // in one session, measured. The full sweep is authoritative (it is what the phantom
        // spawner trusts) and is affordable here because it is rate-limited and only reached
        // when the cheap path has already failed.
        if (!knownCollision && CreatureFinder.FindByObjectId(enemy.ObjectId) != 0)
        {
            // REMEMBER IT. This is the line whose absence made the sweep storm; see the
            // ProvenPresent check at the top. Recording the proof is what turns "sweep for
            // ever" into "sweep once".
            if (_provenCount < ProvenPresent.Length)
            {
                ProvenPresent[_provenCount++] = enemy.ObjectId;
            }

            if (_falseAlarmsLogged < 5)
            {
                _falseAlarmsLogged++;
                PluginHost.Log(
                    $"materialise: NOT spawning for 0x{enemy.ObjectId:X} — it exists locally "
                    + "after all; remembering that so it is proved once, not every pass");
            }

            return;
        }

        Vec3 facing = new() { X = MathF.Cos(enemy.Facing), Y = MathF.Sin(enemy.Facing), Z = 0f };

        uint localId = Odyssey.ObjectInvalid;
        int failure = 0;

        // Host spawns only: a creature the host's script created is built from the blueprint the
        // host named, as a puppet (scripts blanked, stand-in tag), so it looks and is equipped
        // like the real one. Anything else, or a blueprint this install cannot load, gets the
        // generic stand-in below.
        string? puppetTemplate = null;
        bool hasTemplate = ScriptCreateObjectHook.TryGetHostTemplate(enemy.ObjectId, out string hostTemplate);

        // The host's census and its spawn-template list leave in the same tick but arrive in
        // either order; a creature first seen this instant may have a blueprint on the way
        // (live 2026-10-09: the first reinforcement was built generic, its template landed a
        // moment later). Wait a moment for it before settling for a stand-in.
        if (!hasTemplate && ScriptCreateObjectHook.Installed)
        {
            if (FirstSeenWithoutTemplate.Count > 1024)
            {
                FirstSeenWithoutTemplate.Clear();
            }

            if (!FirstSeenWithoutTemplate.TryGetValue(enemy.ObjectId, out double firstSeen))
            {
                FirstSeenWithoutTemplate[enemy.ObjectId] = now;
                return;
            }

            if (now - firstSeen < TemplateWaitSeconds)
            {
                return;
            }
        }

        if (hasTemplate)
        {
            localId = Odyssey.SpawnPuppet(hostTemplate, enemy.Position, facing, out failure);
            puppetTemplate = localId != Odyssey.ObjectInvalid ? hostTemplate : null;
            if (localId != Odyssey.ObjectInvalid && _puppetsLogged < 20)
            {
                _puppetsLogged++;
                PluginHost.Log($"materialise: owner 0x{enemy.ObjectId:X} built from the host's blueprint '{hostTemplate}' as 0x{localId:X}");
            }
            else if (localId == Odyssey.ObjectInvalid)
            {
                PluginHost.Log($"materialise: the host's blueprint '{hostTemplate}' for 0x{enemy.ObjectId:X} did not build here (failure {failure}); using a stand-in");
            }
        }

        foreach (string template in Templates)
        {
            if (localId != Odyssey.ObjectInvalid)
            {
                break;
            }

            localId = Odyssey.SpawnCreature(template, enemy.Position, facing, out failure);
        }

        if (localId == Odyssey.ObjectInvalid)
        {
            if (++_spawnFailures >= MaxSpawnFailures && !_spawnDisabled)
            {
                _spawnDisabled = true;
                PluginHost.Log(
                    $"materialise: latched off after {_spawnFailures} failed spawns "
                    + $"(last failure {failure})");
            }

            return;
        }

        _spawnFailures = 0;

        nint creature = CreatureFinder.FindByObjectId(localId);
        if (creature == 0)
        {
            PluginHost.Log($"materialise: spawned 0x{localId:X} but could not resolve its object");
            return;
        }

        Odyssey.ClaimCreature(creature);
        Odyssey.MarkAsMirroredEnemy(creature);

        // The owner's faction verbatim — hostile reads hostile, a neutral bystander stays
        // neutral. Through the engine's own membership call, so every targeting path that
        // re-reads faction live agrees. An implausible id keeps the blueprint's Neutral.
        if (enemy.Faction <= MaxPlausibleFaction)
        {
            Odyssey.SetCreatureFaction(localId, enemy.Faction);
        }

        OwnerIds[slot] = enemy.ObjectId;
        PuppetTemplates[slot] = puppetTemplate;
        LocalIds[slot] = localId;
        Bodies[slot] = creature;
        RejectedOwners.AdmitFreshBody(enemy.ObjectId);
        LastSeen[slot] = now;
        SpawnedAt[slot] = now;
        Appearances[slot] = enemy.Appearance;
        TwinDressed[slot] = false;

        // Almost certainly a no-op on the client half this frame — the engine builds the twin
        // a frame or more after the server object exists — which is exactly why Sweep keeps
        // calling this until the latch closes. See Dress.
        Dress(slot);

        if (_logged < 20)
        {
            _logged++;
            string twinRow = Odyssey.TryGetTwinAppearanceRow(creature, out ushort renderedRow)
                ? $"renders {renderedRow}"
                : "renders no twin yet";
            PluginHost.Log(
                $"materialise: owner 0x{enemy.ObjectId:X} -> local 0x{localId:X} "
                + $"(appearance {enemy.Appearance}, {twinRow}, faction {enemy.Faction})");
        }
    }

    /// <summary>
    /// Whether this published enemy may have a body built for it here at all.
    ///
    /// Pure decision in <see cref="PartyCensusRule.MayMaterialise"/>; this half does the two
    /// engine reads it needs — our own party table's roster ids, and where our own companions
    /// are standing and what they are wearing. See that rule's doc comment.
    /// </summary>
    private static bool MayMaterialise(in EnemyPresenceView enemy)
    {
        bool ownRosterBody = false;
        bool coincides = false;

        for (int slot = 0; slot < Odyssey.PartyRosterSlots; slot++)
        {
            uint rosterId = Odyssey.GetPartyRosterObjectId(slot);
            if (!PartyCensusRule.IsRosterId(rosterId))
            {
                continue;
            }

            if (rosterId == enemy.ObjectId)
            {
                ownRosterBody = true;
                break;
            }

            nint body = Odyssey.ResolveServerObject(rosterId);
            if (body == 0)
            {
                continue;
            }

            Vec3 at = Odyssey.GetPosition(body);
            float dx = at.X - enemy.Position.X;
            float dy = at.Y - enemy.Position.Y;

            if (PartyCensusRule.CoincidesWithOwnCompanion(
                    enemy.Appearance, *(ushort*)(body + CreAppearanceType), (dx * dx) + (dy * dy)))
            {
                coincides = true;
                break;
            }
        }

        if (PartyCensusRule.MayMaterialise(ownRosterBody, coincides))
        {
            return true;
        }

        if (_partyRefusalsLogged < 5)
        {
            _partyRefusalsLogged++;
            PluginHost.Log(
                $"materialise: REFUSING owner 0x{enemy.ObjectId:X} (appearance {enemy.Appearance}) — "
                + (ownRosterBody
                    ? "it is one of OUR party roster bodies"
                    : "it stands on one of our own companions wearing that companion's appearance")
                + "; the publisher leaked a party member into its enemy census "
                + "(docs/research/name-plates.md, the census leak)");
        }

        return false;
    }

    private static int _partyRefusalsLogged;

    /// <summary>
    /// Makes a stand-in wear the row its OWNER published — on both halves of the object.
    ///
    /// ⚠ THE HALF THAT RENDERS IS THE CLIENT TWIN, and it does not exist on the frame the body
    /// is spawned. This class used to write the appearance field once, at spawn, and never call
    /// <see cref="Odyssey.RebuildClientAppearance"/> at all — so the twin the engine built a
    /// frame later kept whatever the blueprint gave it and EVERY stand-in rendered as
    /// <c>k_coop_phantom</c> (appearance 114) instead of the creature it stands for. The live
    /// pass in docs/research/name-plates.md measured the twin missing at spawn on 100% of
    /// phantom spawns, which is the same ordering; this is the identical per-body latch
    /// <c>PhantomController.ApplyAppearance</c> got one channel over.
    ///
    /// Cheap while the twin is missing (a resolve and two reads) and fires its engine calls
    /// exactly once per body once it exists — the per-object latch discipline, not a cadence.
    /// MAIN THREAD ONLY.
    /// </summary>
    private static void Dress(int slot)
    {
        uint row = Appearances[slot];
        if (TwinDressed[slot] || OwnerIds[slot] == 0 || !PeerIdentityRule.IsUsableAppearance(row))
        {
            return;
        }

        // ⚠ RE-RESOLVED, not Bodies[slot]. This runs every frame until the latch closes, and a
        // cached pointer to a body the engine has since freed is the shape of crash this
        // project's per-frame-write rule exists for. The engine's own object array is the only
        // thing that can say the body is still there.
        if (!TryGetCurrentBody(slot, out nint creature))
        {
            return;
        }

        ushort* field = (ushort*)(creature + CreAppearanceType);
        if (*field != (ushort)row)
        {
            *field = (ushort)row;
            Odyssey.RebuildAppearance(creature);
        }

        if (!Odyssey.RebuildClientAppearance(creature))
        {
            return;
        }

        TwinDressed[slot] = true;
        Bodies[slot] = creature;

        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(
                $"materialise: local 0x{LocalIds[slot]:X} (owner 0x{OwnerIds[slot]:X}) now renders "
                + $"appearance {row}");
        }
    }

    /// <summary>
    /// The mapped body could not be driven this pass. DESTROYS it and forgets the entry, so
    /// the next pass can build a fresh one.
    ///
    /// ⚠ It destroys rather than merely forgetting, and that is the whole lesson of the
    /// first version: dropping the mapping alone left the body standing with nothing
    /// referencing it, and since the next pass then re-materialised, the pair leaked one
    /// permanent Sith soldier every half-second. A mapping and its body are one thing and
    /// are released together.
    /// </summary>
    public static void ForgetOwner(uint ownerId)
    {
        RetainedBodies.ForgetOwner(ownerId);
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId)
            {
                Destroy(i, "its body could not be resolved");
                return;
            }
        }
    }

    /// <summary>
    /// Destroys at most one body whose owner id the census has stopped mentioning (the
    /// owner despawned it, the module gate cleared the list, or the session ended).
    /// Called once per frame; also ticks the spawn cooldown. MAIN THREAD ONLY.
    /// </summary>
    public static void Sweep(double now)
    {
        RetainedBodies.PruneInvalidSlots(Odyssey.GetPartyRosterObjectId);
        RetainedBodies.Sweep(now, StaleSeconds);
        if (_spawnCooldown > 0)
        {
            _spawnCooldown--;
        }

        // Finish dressing anything whose client twin did not exist when it was spawned — which
        // is the ordinary case, every time. Latched per body, so the steady state is one array
        // read per slot. See Dress.
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] != 0 && !TwinDressed[i])
            {
                Dress(i);
            }
        }

        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == 0 || now - LastSeen[i] < StaleSeconds)
            {
                continue;
            }

            Destroy(i, "stale (owner census no longer lists it)");
            return;
        }
    }

    /// <summary>
    /// Destroys one live body for the pre-store flush window — same contract as
    /// PhantomController.FlushOneBody: a materialised body left standing when the area is
    /// stored becomes a permanent resident of the saved module. Returns false once clean —
    /// a refused destroy still returns true, because the body IS still there.
    /// </summary>
    public static bool FlushOneBody()
    {
        // RetainedEnemyBodyMap is deliberately not part of this plugin-owned flush table.
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] != 0)
            {
                Destroy(i, "flush, before the module is stored");
                return true;
            }
        }

        return false;
    }

    /// <summary>How many materialised bodies are standing right now — what the save-time
    /// flush asks before deciding to hold a save.</summary>
    public static int StandingCount
    {
        get
        {
            int standing = 0;
            for (int i = 0; i < OwnerIds.Length; i++)
            {
                if (OwnerIds[i] != 0)
                {
                    standing++;
                }
            }

            return standing;
        }
    }

    /// <summary>Forgets everything WITHOUT touching the engine — the module changed and
    /// every body behind these ids is already freed memory. Also clears the spawn latch:
    /// a new module may succeed where the last one failed.</summary>
    public static void Invalidate()
    {
        RejectedOwners.Clear();
        FirstSeenWithoutTemplate.Clear();
        RetainedBodies.Clear();
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            Clear(i);
        }

        _spawnFailures = 0;
        _spawnDisabled = false;
        _spawnCooldown = 0;
        _falseAlarmsLogged = 0;
        _partyRefusalsLogged = 0;
        _provenCount = 0;   // a module change reassigns every object id
    }

    /// <summary>
    /// Destroys every body this mirror built and reports how many are still standing. The
    /// session's counterpart to <see cref="FlushOneBody"/>: a full sweep, because a mirrored
    /// enemy left standing in a disconnected player's game is baked into their next save the
    /// same way a phantom is — and there can be up to <c>Capacity</c> of them.
    /// </summary>
    public static int ReleaseSession()
    {
        int standing = 0;
        RetainedBodies.ReleaseSession();
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == 0)
            {
                continue;
            }

            HandBackPuppet(i);
            if (!Destroy(i, "session released"))
            {
                standing++;
            }
        }

        return standing;
    }

    /// <summary>
    /// The session ended (disconnect, host loss): a living puppet the host's script spawned
    /// becomes this game's own creature again. This game's scripts never spawned one (host
    /// spawns only), so destroying the puppet alone would empty the fight the player is
    /// standing in. A normal spawn of the same blueprint (scripts, AI and faction from the
    /// blueprint, OnSpawn runs) at the puppet's spot and health, then the puppet goes.
    /// </summary>
    private static void HandBackPuppet(int slot)
    {
        string? template = PuppetTemplates[slot];
        PuppetTemplates[slot] = null;
        nint body = Bodies[slot];
        if (template is null || body == 0 || !NativeMethods.IsReadable(body, 0x100)
            || *(uint*)(body + 4) != LocalIds[slot] || Odyssey.IsCreatureDead(body))
        {
            return;
        }

        Vec3 position = Odyssey.GetPosition(body);
        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f }; // cosmetic; its own AI turns it
        short hitPoints = Odyssey.GetCurrentHitPoints(body);
        uint own = Odyssey.SpawnCreature(template, position, facing, out int failure);
        nint ownBody = own == Odyssey.ObjectInvalid ? 0 : CreatureFinder.FindByObjectId(own);
        if (ownBody != 0 && hitPoints > 0)
        {
            Odyssey.SetCurrentHitPoints(ownBody, hitPoints);
        }

        PluginHost.Log(own == Odyssey.ObjectInvalid
            ? $"materialise: could not hand '{template}' (0x{LocalIds[slot]:X}) back as this game's own creature (failure {failure})"
            : $"materialise: session over - '{template}' is this game's own creature again (0x{own:X}, {hitPoints} hp)");
    }

    /// <summary>How many refusals before a body is written off. See PhantomController's
    /// constant of the same name.</summary>
    private const int MaxDestroyAttempts = 3;

    private static readonly int[] DestroyAttempts = new int[SharedBlock.EnemyCapacity];

    /// <summary>
    /// ⚠ THE SLOT SURVIVES A REFUSAL, so the next pass tries again. A single declined
    /// destroy — which is what the script VM returns whenever it is mid-call — used to
    /// orphan the body permanently: this class forgot it and nothing ever retried.
    /// </summary>
    /// <returns>True once the slot is finished with (destroyed, or written off).</returns>
    private static bool Destroy(int slot, string reason)
    {
        if (!TryGetCurrentBody(slot, out nint current) && OwnerIds[slot] == 0) return true;
        uint localId = LocalIds[slot];
        if (current != 0) Odyssey.ForgetGait(current);

        if (Odyssey.DestroyObjectById(localId, out int failure))
        {
            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log($"materialise: destroyed local 0x{localId:X} — {reason}");
            }
        }
        else if (++DestroyAttempts[slot] < MaxDestroyAttempts)
        {
            PluginHost.Log($"materialise: destroy refused for local 0x{localId:X} (failure "
                + $"{failure}) — {reason}; retrying ({DestroyAttempts[slot]} of {MaxDestroyAttempts})");
            return false;
        }
        else
        {
            PluginHost.Log($"materialise: GIVING UP on local 0x{localId:X} (failure {failure}) "
                + $"after {MaxDestroyAttempts} attempts — {reason}; the body is LEFT STANDING "
                + "and a save would bake it into the module");
        }

        Clear(slot);
        return true;
    }

    private static bool TryGetCurrentBody(int slot, out nint current)
    {
        current = Odyssey.ResolveServerObject(LocalIds[slot]);
        if (current == 0) return false; // a fresh spawn can temporarily fail the cheap resolver
        if (!LivenessRule.IsMaterialisedReplacement(Bodies[slot], current,
            (uint)*(nint*)current, *(uint*)(current + 4), LocalIds[slot])) return true;
        // The old world freed this body. Its reused id does not transfer ownership
        // of the new object to this registry, even if the new object is a creature.
        RejectedOwners.Reject(OwnerIds[slot]);
        Clear(slot);
        current = 0;
        return false;
    }

    private static void Clear(int slot)
    {
        OwnerIds[slot] = 0;
        LocalIds[slot] = 0;
        Bodies[slot] = 0;
        LastSeen[slot] = 0;
        SpawnedAt[slot] = 0;
        DestroyAttempts[slot] = 0;
        Appearances[slot] = 0;
        PuppetTemplates[slot] = null;
        TwinDressed[slot] = false;
    }

    /// <summary>CSWSCreature + this = the appearance.2da row, u16 — the same field the
    /// phantom repaint writes (docs/research/phantom-equip-crash.md: any write here must
    /// be followed by RebuildAppearance).</summary>
    private const int CreAppearanceType = 0xA60;

    /// <summary>CSWSObject + this = its current object id, revalidated after resolution.</summary>
    private const int ObjId = 0x04;

    /// <summary>repute.2da has ~20 rows in the shipped game; anything far beyond that is
    /// a misread, and the blueprint's Neutral is the safe stand-in.</summary>
    private const ushort MaxPlausibleFaction = 100;
}
