namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's door truth to this instance, once per frame batch, on the game's
/// thread. A door open for one player and shut for the other is the most visible desync
/// co-op can produce, and doors are cheap to agree on: their object ids match across
/// instances (same save, deterministic id assignment) and their state only ever advances
/// (closed -&gt; open, locked -&gt; unlocked, destroyed), so the reliable max-merge flag
/// channel carries them without any new transport.
///
/// The agent writes the DESIRED door states for the local module into the shared block;
/// this class moves each door toward its desired state through the engine's own paths:
///
///   - unlock is a plain byte write (that is what the engine's own unlock does),
///   - opening goes through CSWSDoor::SetOpenState(state, deferred), which plays the
///     swing animation and lets the engine commit state and walkmesh when it lands.
///
/// Never write the open-state field directly: the walkmesh follows the field but the
/// model does not, which produces a door you can walk through that still looks shut.
/// </summary>
internal sealed class DoorController(SharedBlockView block)
{
    /// <summary>Frames between passes; door state changes are rare and never urgent.</summary>
    private const int IntervalFrames = 40;

    /// <summary>
    /// Passes to wait for the engine to commit a swing we commanded before forcing the
    /// commit ourselves. The swing animation runs about a second; 25 passes at 40 frames
    /// and ~317 Hz is roughly three seconds, comfortably past it.
    /// </summary>
    private const int CommitFallbackPasses = 25;

    /// <summary>Door ids we have commanded a swing on, and how long we have waited.</summary>
    private readonly uint[] _commandedIds = new uint[SharedBlock.DoorCapacity];
    private readonly int[] _commandedWait = new int[SharedBlock.DoorCapacity];
    private int _countdown;
    private int _logged;

    /// <summary>Forgets everything on a module change; ids now name different doors.</summary>
    public void Invalidate()
    {
        Array.Clear(_commandedIds);
        Array.Clear(_commandedWait);
        block.DoorCount = 0;
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

        uint count = Math.Min(block.DoorCount, (uint)SharedBlock.DoorCapacity);

        for (int i = 0; i < count; i++)
        {
            Apply(i, block.GetDoor(i));
        }
    }

    private void Apply(int slot, DoorView desired)
    {
        if (desired.ObjectId == 0 || desired.ObjectId == Odyssey.ObjectInvalid)
        {
            return;
        }

        nint door = Odyssey.ResolveDoor(desired.ObjectId);
        if (door == 0)
        {
            return;
        }

        if (desired.Unlocked && Odyssey.IsDoorLocked(door))
        {
            Odyssey.UnlockDoor(door);
            LogFew($"door 0x{desired.ObjectId:X}: unlocked on the session's authority");
        }

        byte current = Odyssey.GetDoorOpenState(door);
        byte pending = Odyssey.GetDoorPendingOpenState(door);

        if (_commandedIds[slot] == desired.ObjectId)
        {
            // We already commanded this swing. If the engine committed it, done; if not,
            // give the animation time and then commit state + walkmesh ourselves.
            if (current == pending)
            {
                _commandedIds[slot] = 0;
                _commandedWait[slot] = 0;
            }
            else if (++_commandedWait[slot] >= CommitFallbackPasses)
            {
                Odyssey.CommitDoorOpenState(door);
                LogFew($"door 0x{desired.ObjectId:X}: commit forced after the swing never landed");
                _commandedIds[slot] = 0;
                _commandedWait[slot] = 0;
            }

            return;
        }

        // Only ever move a door forward, and never interrupt a swing the engine is
        // already mid-way through (pending != current means one is in flight).
        if (desired.OpenState <= current || pending != current)
        {
            return;
        }

        Odyssey.SetDoorOpenState(door, desired.OpenState);
        _commandedIds[slot] = desired.ObjectId;
        _commandedWait[slot] = 0;
        LogFew($"door 0x{desired.ObjectId:X}: swinging {current} -> {desired.OpenState} on the session's authority");
    }

    private void LogFew(string message)
    {
        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
