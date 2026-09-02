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
    private static readonly nint[] Bodies = new nint[SharedBlock.EnemyCapacity];
    private static readonly double[] LastSeen = new double[SharedBlock.EnemyCapacity];
    private static readonly double[] SpawnedAt = new double[SharedBlock.EnemyCapacity];

    /// <summary>The appearance.2da row the OWNER published for this stand-in — the only source
    /// there is for what it should look like, kept per slot so <see cref="Dress"/> can keep
    /// trying after the spawn frame.</summary>
    private static readonly uint[] Appearances = new uint[SharedBlock.EnemyCapacity];

    /// <summary>Per-BODY latch: has the owner's appearance row reached this stand-in's CLIENT
    /// TWIN, which is the half that actually renders? See <see cref="Dress"/>.</summary>
    private static readonly bool[] TwinDressed = new bool[SharedBlock.EnemyCapacity];

    private static int _spawnCooldown;
    private static int _spawnFailures;
    private static bool _spawnDisabled;
    private static int _logged;
    private static int _falseAlarmsLogged;

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
        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] == ownerId)
            {
                return LocalIds[i];
            }
        }

        return 0;
    }

    /// <summary>The owner id a local body stands in for, or 0 when the local id is not a
    /// materialised body. Used to key damage reports in the owner's id space — and, by
    /// callers about to resolve, to detect that an owner id NUMERICALLY collides with one
    /// of our own spawned bodies and must not be resolved as if it were GIT-identical.</summary>
    public static uint ToOwner(uint localId)
    {
        for (int i = 0; i < LocalIds.Length; i++)
        {
            if (LocalIds[i] == localId)
            {
                return OwnerIds[i];
            }
        }

        return 0;
    }

    /// <summary>
    /// Every currently-mapped owner id, in fixed slot order (0 = unused) — the SAME array
    /// <see cref="ToLocal"/> walks, exposed read-only so <see cref="EnemyPresenceController"/>
    /// can publish it verbatim into the shared block
    /// (<c>SharedBlockView.SetMaterialisedOwnerIds</c>, version 65) every pass it sweeps this
    /// table. Lets the agent tell, BEFORE spending a giveloot round trip on the single-slot
    /// cheat channel, whether an owner id names one of our own stand-ins or (the overwhelmingly
    /// common case) a peer holding the real, GIT-identical creature. See
    /// docs/research/enemy-materialisation.md's 2026-09-02 section.
    /// </summary>
    public static ReadOnlySpan<uint> OwnerIdsSnapshot => OwnerIds;

    /// <summary>Records that the census still lists this owner id, keeping its entry (and
    /// body) alive. Called for every published slot every frame.</summary>
    public static void Seen(uint ownerId, double now)
    {
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
    public static void TryMaterialise(in EnemyPresenceView enemy, double now)
    {
        if (!Enabled || _spawnDisabled || enemy.Dead || enemy.Appearance == 0)
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
            if (ProvenPresent[i] == enemy.ObjectId)
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
        if (CreatureFinder.FindByObjectId(enemy.ObjectId) != 0)
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
        foreach (string template in Templates)
        {
            localId = Odyssey.SpawnCreature(template, enemy.Position, facing, out failure);
            if (localId != Odyssey.ObjectInvalid)
            {
                break;
            }
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
        LocalIds[slot] = localId;
        Bodies[slot] = creature;
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
        nint creature = Odyssey.ResolveServerObject(LocalIds[slot]);
        if (creature == 0)
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

    /// <summary>Forgets everything WITHOUT touching the engine — the module changed and
    /// every body behind these ids is already freed memory. Also clears the spawn latch:
    /// a new module may succeed where the last one failed.</summary>
    public static void Invalidate()
    {
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

        for (int i = 0; i < OwnerIds.Length; i++)
        {
            if (OwnerIds[i] != 0 && !Destroy(i, "session released"))
            {
                standing++;
            }
        }

        return standing;
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
        uint localId = LocalIds[slot];
        if (Bodies[slot] != 0)
        {
            Odyssey.ForgetGait(Bodies[slot]);
        }

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

    private static void Clear(int slot)
    {
        OwnerIds[slot] = 0;
        LocalIds[slot] = 0;
        Bodies[slot] = 0;
        LastSeen[slot] = 0;
        SpawnedAt[slot] = 0;
        DestroyAttempts[slot] = 0;
        Appearances[slot] = 0;
        TwinDressed[slot] = false;
    }

    /// <summary>CSWSCreature + this = the appearance.2da row, u16 — the same field the
    /// phantom repaint writes (docs/research/phantom-equip-crash.md: any write here must
    /// be followed by RebuildAppearance).</summary>
    private const int CreAppearanceType = 0xA60;

    /// <summary>repute.2da has ~20 rows in the shipped game; anything far beyond that is
    /// a misread, and the blueprint's Neutral is the safe stand-in.</summary>
    private const ushort MaxPlausibleFaction = 100;
}
