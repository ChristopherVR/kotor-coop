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
    /// <summary>Blueprint for the phantom body: a generic male commoner.</summary>
    private const string Template = "n_commm";

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

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly nint[] _phantoms = new nint[SharedBlock.SlotCapacity];
    private readonly uint[] _phantomIds = new uint[SharedBlock.SlotCapacity];
    private readonly Vec3[] _lastGoal = new Vec3[SharedBlock.SlotCapacity];
    private readonly int[] _repathCooldown = new int[SharedBlock.SlotCapacity];

    /// <summary>FNV hash of the resref last equipped per (phantom, equip slot); 0 = none.</summary>
    private readonly uint[,] _appliedEquip = new uint[SharedBlock.SlotCapacity, SharedBlock.EquipCount];

    private int _phantomCount;
    private double _lastStep;
    private int _spawnCooldownFrames;
    private int _equipCooldownFrames;
    private int _stepsLogged;
    private int _equipsLogged;

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

            for (int j = 0; j < SharedBlock.EquipCount; j++)
            {
                _appliedEquip[i, j] = 0;
            }

            block.SetPhantomId(i, 0);
        }

        _phantomCount = 0;
        block.PhantomCount = 0;
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
        else if (_phantomCount < wanted)
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
                // pointing along its final path segment otherwise.
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

    /// <summary>appearance.2da row, u16 inside CSWSCreatureAppearanceInfo at +0xA50.</summary>
    private const int CreAppearanceType = 0xA60;

    /// <summary>
    /// Makes the phantom wear the remote player's appearance.2da row. Server-side is the
    /// single source of truth: the engine's own appearance-update message notices the
    /// change and pushes it to the render side next frame (docs/re/rendering.md section 5),
    /// so this is a plain field write with no client call needed.
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
        }
    }

    private void TrySpawnOne(nint player)
    {
        Vec3 playerPosition = Odyssey.GetPosition(player);

        // Offset from the player so the spawn point is certainly inside the walkmesh.
        // ComputeSafeLocation inside CreateObject searches outward from here anyway.
        Vec3 spawnPosition = new()
        {
            X = playerPosition.X + 2.5f + (_phantomCount * 1.5f),
            Y = playerPosition.Y + 1.5f,
            Z = playerPosition.Z,
        };

        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };

        uint objectId = Odyssey.SpawnCreature(Template, spawnPosition, facing, out int failure);
        block.LastStatus = failure;

        if (objectId == Odyssey.ObjectInvalid)
        {
            PluginHost.Log($"spawn refused (failure code {failure})");
            return;
        }

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
