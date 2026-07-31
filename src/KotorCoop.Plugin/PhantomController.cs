using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Drives one spawned creature per remote player, once per frame, on the game's thread.
///
/// Every engine call in here is reached from the frame hook, never from the plugin's worker
/// thread. The worker only touches the shared block.
/// </summary>
internal sealed unsafe class PhantomController(SharedBlockView block)
{
    /// <summary>
    /// Blueprints tried in order for a phantom body. The co-op blueprint shipped in
    /// override (min-1-hp, neutral, no stock AI, equip proficiencies) is preferred; a
    /// generic male commoner is the fallback for an install without the override file.
    /// A resref a mod removed simply drops to the next; only when none resolve does
    /// spawning latch off.
    /// </summary>
    private static readonly string[] Templates = ["k_coop_phantom", "n_commm"];

    /// <summary>Consecutive spawn attempts that produced no body before phantoms are
    /// declared unavailable and the retry stops. Without this a missing blueprint retries
    /// every 30 frames forever, hammering the log on the game thread.</summary>
    private const int MaxSpawnFailures = 5;

    // There used to be a WalkUnitsPerSecond constant of 5.4 here, described as bounding how
    // far a phantom may travel per frame. It bounded nothing: the value it fed was computed
    // and never read. The phantom's speed comes entirely from the engine, because movement
    // is an ActionMoveToLocation the AI executes — which is also why the wrong number was
    // harmless. (For reference, had it mattered: speed is not creaturespeed.2da either;
    // GetWalkRate 0x004F1B20 and GetRunRate 0x004F1BE0 read the creature's appearance
    // record, i.e. appearance.2da walkdist/rundist, which for the PC's row 114 is 1.6/3.96.)

    /// <summary>
    /// The engine's main loop runs at roughly 317 Hz, far above the render rate, so the
    /// phantom is stepped on a wall clock instead of once per callback.
    /// </summary>
    private const double StepIntervalSeconds = 1.0 / 60.0;

    /// <summary>
    /// How far a remote player must move before the phantom's move action is re-issued.
    /// Squared to avoid a square root on the per-frame path. 0.6 units: tight enough that
    /// a stopped peer's phantom stands almost exactly on the true spot, loose enough that
    /// interpolation jitter does not thrash the pathfinder — churn is additionally capped
    /// by <see cref="RepathCooldownSteps"/>.
    /// </summary>
    private const float GoalResendDistanceSquared = 0.6f * 0.6f;

    /// <summary>
    /// Minimum steps (at ~60 Hz) between re-issued move actions per phantom. Re-queueing
    /// every tick resets the pathfinder continuously and the creature never gets anywhere;
    /// five steps ≈ 80 ms keeps the goal fresh without the churn.
    /// </summary>
    private const int RepathCooldownSteps = 5;

    /// <summary>
    /// Beyond this the phantom is not lagging, it is lost (peer teleported, or the
    /// pathfinder dead-ended). Snap instead of walking a 20-second catch-up.
    /// </summary>
    private const float SnapDistanceSquared = 15f * 15f;

    /// <summary>The walk animation; anything else while moving is treated as a run.
    /// KOTOR's default movement IS the run, so run is the common case.</summary>
    private const uint AnimWalkValue = 0x2712;

    // Movement animations the drive loop owns (they come from the walk/run action, not from
    // mirroring the peer's field), and the death poses the down-state path owns. A phantom
    // standing still mirrors any OTHER animation the peer is playing — attack swings, weapon
    // flourishes, gestures, the "x" ready pose — so those show up across instances.
    private const uint AnimIdleValue = 0x2710;
    private const uint AnimRunValue = 0x2714;
    private const uint AnimDeath1 = 0x2716;
    private const uint AnimDeath2 = 0x2718;
    private const uint AnimDeath3 = 0x27AC;

    private static bool IsPlayableAnimation(uint anim) =>
        anim is not (AnimIdleValue or AnimWalkValue or AnimRunValue or AnimDeath1 or AnimDeath2 or AnimDeath3);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly nint[] _phantoms = new nint[SharedBlock.SlotCapacity];
    private readonly uint[] _phantomIds = new uint[SharedBlock.SlotCapacity];
    private readonly Vec3[] _lastGoal = new Vec3[SharedBlock.SlotCapacity];
    private readonly int[] _repathCooldown = new int[SharedBlock.SlotCapacity];

    /// <summary>The animation last mirrored onto each stationary phantom, so a played
    /// animation is issued once when it appears rather than restarted every frame.</summary>
    private readonly uint[] _lastAnim = new uint[SharedBlock.SlotCapacity];

    /// <summary>True while the phantom is lying in a death pose because its player is at
    /// zero health. Tracked so the pose is set once, not re-issued every step.</summary>
    private readonly bool[] _down = new bool[SharedBlock.SlotCapacity];

    /// <summary>The peer's in-combat flag last step, so leaving combat is an edge: that
    /// is when local enemies locked onto the phantom must be released — a min-1-hp body
    /// never reads dead, so nothing lets go on its own (7.4).</summary>
    private readonly bool[] _peerInCombat = new bool[SharedBlock.SlotCapacity];

    /// <summary>FNV hash of the resref last equipped per (phantom, equip slot); 0 = none.</summary>
    private readonly uint[,] _appliedEquip = new uint[SharedBlock.SlotCapacity, SharedBlock.EquipCount];

    /// <summary>True once the phantom's name has been written to its CLIENT TWIN, which is
    /// what the selection band renders. Spawn writes the server locstring, but the twin
    /// does not exist yet then, so the name is re-asserted once the twin appears.</summary>
    private readonly bool[] _twinNamed = new bool[SharedBlock.SlotCapacity];

    private int _phantomCount;
    private double _lastStep;
    private int _spawnCooldownFrames;
    private int _equipCooldownFrames;
    private int _stepsLogged;
    private int _equipsLogged;
    private int _spawnFailures;
    private bool _spawnDisabled;

    /// <summary>
    /// Forgets every phantom without touching the engine. Called when the module has
    /// changed (or is changing): the objects behind these pointers are freed by
    /// UnloadModule, so destroying them is neither possible nor necessary — the first
    /// frame in the new world just spawns fresh bodies.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < _phantoms.Length; i++)
        {
            _phantoms[i] = 0;
            _phantomIds[i] = 0;
            _lastGoal[i] = default;
            _repathCooldown[i] = 0;
            _down[i] = false;
            _peerInCombat[i] = false;

            _twinNamed[i] = false;

            for (int j = 0; j < SharedBlock.EquipCount; j++)
            {
                _appliedEquip[i, j] = 0;
            }

            block.SetPhantomId(i, 0);
        }

        _phantomCount = 0;
        block.PhantomCount = 0;

        // A new module may ship the blueprint the last one lacked, and a transient failure
        // clears — so give spawning a fresh start rather than staying latched off forever.
        _spawnFailures = 0;
        _spawnDisabled = false;
    }

    /// <summary>
    /// Destroys one live phantom body through the engine, for the pre-store flush window:
    /// a phantom left standing when the area is stored gets serialised into the saved
    /// module as a permanent NPC. One per call keeps the flush allocation-free and cheap.
    /// Returns false once nothing is left to destroy.
    /// </summary>
    public bool FlushOneBody()
    {
        for (int i = 0; i < _phantomCount; i++)
        {
            if (_phantoms[i] == 0)
            {
                continue;
            }

            uint objectId = _phantomIds[i];
            Odyssey.DestroyObjectById(objectId, out int failure);
            PluginHost.Log(failure == 0
                ? $"flush: destroyed phantom 0x{objectId:X} before the module is stored"
                : $"flush: destroy refused for phantom 0x{objectId:X} (failure {failure})");

            _phantoms[i] = 0;
            _phantomIds[i] = 0;
            block.SetPhantomId(i, 0);
            return true;
        }

        return false;
    }

    public void OnFrame()
    {
        block.FrameCounter = (uint)FrameHook.FrameCount;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            // Idle (a disconnect): despawn any phantoms so a player who left does not leave
            // a frozen body behind. One per frame keeps it cheap; reset the bookkeeping once
            // the last body is gone.
            if (_phantomCount > 0 && !FlushOneBody())
            {
                _phantomCount = 0;
                block.PhantomCount = 0;
            }

            return;
        }

        // The agent's pointer is only the handshake; the engine's own lookup is the
        // authority. The agent-written pointer goes stale across a module transition and
        // dereferencing it then would read freed memory.
        if (block.PlayerCreature == 0)
        {
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        // Spawning is rate-limited to at most one creature every 30 frames. A spawn is by
        // far the most expensive thing here, and doing several in one frame is how you turn
        // a working mod into a stutter.
        if (_spawnCooldownFrames > 0)
        {
            _spawnCooldownFrames--;
        }
        else if (_phantomCount < wanted && !_spawnDisabled)
        {
            TrySpawnOne(player);
            _spawnCooldownFrames = 30;
        }

        double now = _clock.Elapsed.TotalSeconds;
        double delta = now - _lastStep;
        if (delta < StepIntervalSeconds)
        {
            return;
        }

        _lastStep = now;

        for (int i = 0; i < _phantomCount; i++)
        {
            nint phantom = _phantoms[i];
            if (phantom == 0)
            {
                continue;
            }

            RemoteView remote = block.GetRemote(i);
            if (!remote.Active)
            {
                // No peer in this slot any more: leave the body standing rather than
                // dragging it to the origin.
                Odyssey.SetAnimation(phantom, Odyssey.AnimIdle);
                continue;
            }

            ApplyAppearance(phantom, remote.Appearance);
            ApplyEquipment(i);
            ApplyName(i, phantom);

            // The peer left combat: release every local creature still locked onto their
            // phantom. The phantom is min-1-hp, so it never reads dead and an attacker
            // would otherwise hold the lock forever (the aggro-sink failure of 7.4).
            bool peerInCombat = remote.InCombat;
            if (_peerInCombat[i] && !peerInCombat)
            {
                Odyssey.PacifyPhantom(phantom);
            }

            _peerInCombat[i] = peerInCombat;

            // A player at zero health lies where they fell, and their phantom does the
            // same — otherwise a downed teammate looks alive and well in your game while
            // their screen shows a death. The pose comes back off the moment the peer's
            // health does, so a revive (or a respawned host) stands the phantom up.
            bool down = remote.Health <= 0;
            if (down != _down[i])
            {
                _down[i] = down;

                if (down)
                {
                    // Stop mid-stride first: a queued move action executing after the
                    // pose would stand the body straight back up. And release anything
                    // attacking the body — it cannot die, so nothing lets go on its own.
                    Odyssey.ClearActions(phantom);
                    Odyssey.PacifyPhantom(phantom);
                }

                Odyssey.SetDownState(phantom, down);
                PluginHost.Log(down
                    ? $"phantom 0x{_phantomIds[i]:X}: down (player at 0 hp)"
                    : $"phantom 0x{_phantomIds[i]:X}: back up");
            }

            if (down)
            {
                continue;
            }

            // Mirror the peer's played animation every frame (change-detected). It overlays
            // on the twin, so it does not fight the movement action below.
            MirrorAnimation(i, phantom, remote.Animation);

            if (_repathCooldown[i] > 0)
            {
                _repathCooldown[i]--;
            }

            Vec3 at = Odyssey.GetPosition(phantom);
            float pdx = remote.Position.X - at.X;
            float pdy = remote.Position.Y - at.Y;
            float behindSquared = (pdx * pdx) + (pdy * pdy);

            // Hopelessly behind means the peer teleported or the path dead-ended:
            // snap rather than walk a long catch-up nobody asked to watch.
            if (behindSquared > SnapDistanceSquared)
            {
                Odyssey.SnapTo(phantom, remote.Position);
                _lastGoal[i] = remote.Position;
                _repathCooldown[i] = RepathCooldownSteps;
                continue;
            }

            Vec3 last = _lastGoal[i];
            float gdx = remote.Position.X - last.X;
            float gdy = remote.Position.Y - last.Y;
            bool goalMoved = ((gdx * gdx) + (gdy * gdy)) >= GoalResendDistanceSquared;

            if (!goalMoved)
            {
                // Standing on (or walking to) the right spot already. Once genuinely
                // arrived, mirror the peer's facing — the engine leaves the phantom
                // pointing along its final path segment otherwise — and play whatever
                // animation the peer is doing while still (a flourish, an attack swing, the
                // "x" ready pose). Not while catching up, or it would cancel the walk.
                if (behindSquared < 0.35f * 0.35f)
                {
                    Odyssey.SetFacing(phantom, remote.Facing);
                }

                continue;
            }

            if (_repathCooldown[i] > 0)
            {
                continue;
            }

            _lastGoal[i] = remote.Position;
            _repathCooldown[i] = RepathCooldownSteps;

            // Match the peer's gait: KOTOR's default movement is the run, so anything
            // that is not explicitly the walk animation runs. With the PC's appearance
            // row the phantom shares the player's exact walk/run rates, so matched gait
            // means matched speed.
            bool run = remote.Animation != AnimWalkValue;

            if (Odyssey.MoveToLocation(_phantomIds[i], remote.Position, run, out int failure))
            {
                if (_stepsLogged < 10)
                {
                    _stepsLogged++;
                    PluginHost.Log(
                        $"move#{_stepsLogged} phantom 0x{_phantomIds[i]:X} at "
                        + $"({at.X:F2},{at.Y:F2}) -> goal ({remote.Position.X:F2},{remote.Position.Y:F2})"
                        + (run ? " (run)" : " (walk)"));
                }
            }
            else if (_stepsLogged < 10)
            {
                _stepsLogged++;
                PluginHost.Log($"move refused for 0x{_phantomIds[i]:X} (failure {failure})");
            }
        }
    }

    /// <summary>
    /// Replays whatever animation the peer is doing (weapon draw, attack swing, flourish, the
    /// "x" ready pose) onto the phantom, once per change. Applied as a fire-and-forget on the
    /// phantom's client twin — the engine's own replication path — which overlays the pose
    /// without cancelling the walk/run action, so it works whether the phantom is moving or
    /// still. Gait, idle and the death poses are owned elsewhere and skipped; a one-shot
    /// self-terminates back to idle, so nothing has to reset it.
    /// </summary>
    private void MirrorAnimation(int slot, nint phantom, uint anim)
    {
        if (anim == _lastAnim[slot])
        {
            return;
        }

        _lastAnim[slot] = anim;

        if (IsPlayableAnimation(anim))
        {
            Odyssey.PlayFireForget(phantom, anim);
        }
    }

    /// <summary>Maps equip index in the shared block to the engine's inventory slot.</summary>
    private static ReadOnlySpan<int> EquipInventorySlots =>
    [
        Odyssey.InventorySlotBody,
        Odyssey.InventorySlotRightWeapon,
        Odyssey.InventorySlotLeftWeapon,
    ];

    /// <summary>
    /// Dresses a phantom in its player's real gear: for each visible slot whose desired
    /// resref changed, create the item on the phantom and equip it through the engine.
    /// One engine mutation per 30 frames — an equip is as heavy as a spawn.
    ///
    /// An empty desired resref is left alone (the phantom keeps what it wears); a resref
    /// this install cannot resolve is remembered as applied so a missing mod does not
    /// retry forever.
    /// </summary>
    private void ApplyEquipment(int slot)
    {
        if (_equipCooldownFrames > 0)
        {
            _equipCooldownFrames--;
            return;
        }

        for (int j = 0; j < SharedBlock.EquipCount; j++)
        {
            byte* resref = block.GetEquipResref(slot, j);

            int length = 0;
            uint hash = 2166136261;
            while (length < 16 && resref[length] != 0)
            {
                hash = (hash ^ resref[length]) * 16777619;
                length++;
            }

            if (length == 0 || hash == _appliedEquip[slot, j])
            {
                continue;
            }

            _appliedEquip[slot, j] = hash;
            _equipCooldownFrames = 30;

            bool ok = Odyssey.EquipItemFromTemplate(
                _phantomIds[slot], resref, length, EquipInventorySlots[j], out int failure);

            if (_equipsLogged < 12)
            {
                _equipsLogged++;
                string name = System.Text.Encoding.ASCII.GetString(resref, length);
                PluginHost.Log(ok
                    ? $"equip: phantom 0x{_phantomIds[slot]:X} wears {name} (slot {EquipInventorySlots[j]})"
                    : $"equip: {name} refused for phantom 0x{_phantomIds[slot]:X} (failure {failure})");
            }

            return;
        }
    }

    /// <summary>
    /// Re-asserts the phantom's name until it has reached the CLIENT TWIN, which is what
    /// the selection band renders. The name is set at spawn on the server locstring, but
    /// the twin does not exist yet then and the engine only carries a name to the twin on
    /// its first update — so this catches the moment the twin appears (and after a module
    /// transition recreates it). Once written, it latches and costs one pointer read a step.
    /// </summary>
    private void ApplyName(int slot, nint phantom)
    {
        if (_twinNamed[slot])
        {
            return;
        }

        byte* name = block.GetRemoteName(slot);
        int length = 0;
        while (length < SharedBlock.NameLength - 1 && name[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        // SetCreatureFirstName writes the twin when it exists; treat the name as done only
        // once the twin is actually present, so a pre-twin call does not latch prematurely.
        Odyssey.SetCreatureFirstName(phantom, name, length);

        if (Odyssey.HasClientTwin(phantom))
        {
            _twinNamed[slot] = true;
        }
    }

    /// <summary>appearance.2da row, u16 inside CSWSCreatureAppearanceInfo at +0xA50.</summary>
    private const int CreAppearanceType = 0xA60;

    /// <summary>
    /// Makes the phantom wear the remote player's appearance.2da row. Writing the row field is
    /// not enough on its own: the engine rebuilds a creature's model-type and per-body-part model
    /// strings from that row via UpdateAppearanceDependantInfo after every change, so a bare poke
    /// leaves the phantom holding body-part strings for its OLD (k_coop_phantom) appearance. A
    /// later weapon equip then rebuilds the appearance, reads one of those stale strings and
    /// crashes. Rebuild the derived state right after the write. See
    /// docs/research/phantom-equip-crash.md.
    /// </summary>
    private static void ApplyAppearance(nint phantom, uint appearance)
    {
        if (appearance is 0 or > 0xFFFF)
        {
            return;
        }

        ushort* field = (ushort*)(phantom + CreAppearanceType);
        if (*field != (ushort)appearance)
        {
            *field = (ushort)appearance;
            Odyssey.RebuildAppearance(phantom);
        }
    }

    /// <summary>The old spawn spot: beside the local player, fanned out per phantom.</summary>
    private Vec3 Offset(Vec3 playerPosition) => new()
    {
        X = playerPosition.X + 2.5f + (_phantomCount * 1.5f),
        Y = playerPosition.Y + 1.5f,
        Z = playerPosition.Z,
    };

    /// <summary>Rejects the all-zero position a slot carries before its first update, and any
    /// NaN/infinity a bad read would produce.</summary>
    private static bool IsPlausiblePosition(Vec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && (p.X != 0f || p.Y != 0f);

    private void TrySpawnOne(nint player)
    {
        // Spawn the body WHERE THE PEER ACTUALLY IS. This used to spawn at a fixed offset
        // from the local player and let the walk logic carry it to the real position, which
        // meant every phantom appeared next to you and then jogged across the room — the
        // "initial positions are not synced" report. The peer is standing on their position,
        // so it is by definition a valid walkmesh point, and ComputeSafeLocation inside
        // CreateObject still searches outward from it.
        //
        // The local player is the fallback for the case where the slot has no plausible
        // position yet (a peer that has connected but not yet published one).
        RemoteView remote = block.GetRemote(_phantomCount);
        Vec3 peerPosition = remote.Position;

        Vec3 spawnPosition = IsPlausiblePosition(peerPosition)
            ? peerPosition
            : Offset(Odyssey.GetPosition(player));

        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };

        // Try each blueprint until one resolves. A missing resref (a mod removed the
        // co-op blueprint, or even the stock commoner) returns failure 9; only when every
        // template fails does spawning give up.
        uint objectId = Odyssey.ObjectInvalid;
        int failure = 0;
        foreach (string template in Templates)
        {
            objectId = Odyssey.SpawnCreature(template, spawnPosition, facing, out failure);
            if (objectId != Odyssey.ObjectInvalid)
            {
                break;
            }
        }

        block.LastStatus = failure;

        if (objectId == Odyssey.ObjectInvalid && IsPlausiblePosition(peerPosition))
        {
            // The peer's own position refused a body (a door frame, a lift, mid-transition
            // geometry). Fall back to the old offset-from-the-player spot rather than losing
            // the phantom entirely — a phantom in the wrong place beats no phantom.
            Vec3 fallback = Offset(Odyssey.GetPosition(player));
            foreach (string template in Templates)
            {
                objectId = Odyssey.SpawnCreature(template, fallback, facing, out failure);
                if (objectId != Odyssey.ObjectInvalid)
                {
                    break;
                }
            }

            block.LastStatus = failure;
        }

        if (objectId == Odyssey.ObjectInvalid)
        {
            // Latch after a few consecutive misses so a blueprint no install has does not
            // retry every 30 frames forever, writing to the log on the game thread each
            // time. Invalidate() (a module change) clears the latch for a fresh try.
            if (++_spawnFailures >= MaxSpawnFailures && !_spawnDisabled)
            {
                _spawnDisabled = true;
                PluginHost.Log(
                    $"phantoms unavailable: no blueprint spawned after {_spawnFailures} tries "
                    + $"(last failure {failure}); is a phantom blueprint present?");
            }
            else if (_spawnFailures < MaxSpawnFailures)
            {
                PluginHost.Log($"spawn refused (failure code {failure}); retry {_spawnFailures}/{MaxSpawnFailures}");
            }

            return;
        }

        _spawnFailures = 0;

        // CreateObject returns an id, not a pointer. Resolve it by locating the creature
        // whose object id matches; the plugin is in-process so this is a direct read.
        nint creature = CreatureFinder.FindByObjectId(objectId);
        if (creature == 0)
        {
            PluginHost.Log($"spawned id 0x{objectId:X} but could not resolve its object");
            return;
        }

        Odyssey.ClaimCreature(creature);

        // A phantom belongs to a remote player, so this instance must not be able to fight
        // it or kill it. Done before the creature is ever driven, because the window where
        // a fresh creature stands next to the party is exactly when something shoots it.
        Odyssey.MarkAsRemotePlayer(creature);

        // Dress the phantom as its remote player immediately, while the client twin does
        // not exist yet, so it never renders a single frame as the commoner blueprint.
        RemoteView owner = block.GetRemote(_phantomCount);
        if (owner.Active)
        {
            ApplyAppearance(creature, owner.Appearance);
        }

        // Name it after its player, same reasoning: set before the twin exists so every
        // copy the engine makes carries it. Without a first name the selection band above
        // the phantom is blank (trap 16) and clicking a teammate shows nothing.
        byte* name = block.GetRemoteName(_phantomCount);
        int nameLength = 0;
        while (nameLength < SharedBlock.NameLength - 1 && name[nameLength] != 0)
        {
            nameLength++;
        }

        if (nameLength > 0)
        {
            Odyssey.SetCreatureFirstName(creature, name, nameLength);
        }

        _phantoms[_phantomCount] = creature;
        _phantomIds[_phantomCount] = objectId;
        _phantomCount++;

        block.PhantomCount = (uint)_phantomCount;
        block.SetPhantomId(_phantomCount - 1, objectId);

        PluginHost.Log(
            $"spawned phantom {_phantomCount}: id 0x{objectId:X} at 0x{creature:X} "
            + $"({spawnPosition.X:F2}, {spawnPosition.Y:F2}, {spawnPosition.Z:F2})");
    }
}
