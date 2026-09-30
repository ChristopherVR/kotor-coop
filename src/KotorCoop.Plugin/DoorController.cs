namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's door truth to this instance, once per frame batch, on the game's
/// thread. A door open for one player and shut for the other is the most visible desync
/// co-op can produce, and doors are cheap to agree on: their object ids match across
/// instances (same save, deterministic id assignment). The reliable Lamport-ordered flag
/// channel carries both ordinary opens and scripted closes/re-locks.
///
/// The agent writes the DESIRED door states for the local module into the shared block;
/// this class moves each door toward its desired state through the engine's own paths:
///
///   - lock/unlock is a plain byte write (the engine's own lock field),
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
    private readonly byte[] _commandedOpen = new byte[SharedBlock.DoorCapacity];
    private int _countdown;
    private int _logged;
    private readonly DoorDesiredStateTracker _desired = new();
    private readonly Dictionary<uint, byte> _ackSequence = [];

    /// <summary>Forgets everything on a module change; ids now name different doors.</summary>
    public void Invalidate()
    {
        Array.Clear(_commandedIds);
        Array.Clear(_commandedWait);
        Array.Clear(_commandedOpen);
        _desired.Clear();
        block.ClearDoorAcknowledgements();
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

        byte current = Odyssey.GetDoorOpenState(door);
        byte pending = Odyssey.GetDoorPendingOpenState(door);

        if (_commandedIds[slot] == desired.ObjectId)
        {
            // A newer canonical close/relock can interrupt an outstanding swing once.
            // Never finish the old request merely because the two native bytes agree.
            if (_desired.IsNew(desired.ObjectId, desired.OpenState, desired.Unlocked))
            {
                Odyssey.SetDoorLocked(door, !desired.Unlocked);
                _desired.Applied(desired.ObjectId, desired.OpenState, desired.Unlocked);
                if (_commandedOpen[slot] != desired.OpenState)
                {
                    _commandedOpen[slot] = desired.OpenState;
                    _commandedWait[slot] = 0;
                    Odyssey.SetDoorOpenState(door, desired.OpenState);
                }
                current = Odyssey.GetDoorOpenState(door);
                pending = Odyssey.GetDoorPendingOpenState(door);
            }
            // We already commanded this swing. If the engine committed it, done; if not,
            // give the animation time and then commit state + walkmesh ourselves.
            if (DoorApplyCompletionRule.IsComplete(_commandedOpen[slot], current, pending))
            {
                AcknowledgeActual(slot, door, desired.ObjectId);
                _commandedIds[slot] = 0;
                _commandedWait[slot] = 0;
            }
            else if (++_commandedWait[slot] >= CommitFallbackPasses)
            {
                if (pending != _commandedOpen[slot])
                    Odyssey.SetDoorOpenState(door, _commandedOpen[slot]);
                Odyssey.CommitDoorOpenState(door);
                _commandedWait[slot] = 0;
                if (DoorApplyCompletionRule.IsComplete(_commandedOpen[slot],
                        Odyssey.GetDoorOpenState(door), Odyssey.GetDoorPendingOpenState(door)))
                {
                    AcknowledgeActual(slot, door, desired.ObjectId);
                    LogFew($"door 0x{desired.ObjectId:X}: commit forced after the swing never landed");
                    _commandedIds[slot] = 0;
                }
            }

            return;
        }

        // Never resurrect destroyed doors or interrupt a swing. Do not keep asserting an
        // unchanged snapshot: a local story script may have just closed/re-locked this door
        // and the external scanner needs a chance to publish that newer state.
        if (desired.OpenState > 3 || current == 3 || pending != current
            || !_desired.IsNew(desired.ObjectId, desired.OpenState, desired.Unlocked))
        {
            return;
        }

        Odyssey.SetDoorLocked(door, !desired.Unlocked);
        _desired.Applied(desired.ObjectId, desired.OpenState, desired.Unlocked);
        if (desired.OpenState == current)
        {
            AcknowledgeActual(slot, door, desired.ObjectId);
            return;
        }

        Odyssey.SetDoorOpenState(door, desired.OpenState);
        _commandedIds[slot] = desired.ObjectId;
        _commandedOpen[slot] = desired.OpenState;
        _commandedWait[slot] = 0;
        LogFew($"door 0x{desired.ObjectId:X}: swinging {current} -> {desired.OpenState} on the session's authority");
    }

    private void AcknowledgeActual(int slot, nint door, uint id)
    {
        byte actualOpen = Odyssey.GetDoorOpenState(door);
        if (actualOpen != Odyssey.GetDoorPendingOpenState(door)) return;
        _ackSequence.TryGetValue(id, out byte previousSequence);
        byte sequence = unchecked((byte)(previousSequence + 1));
        if (sequence == 0) sequence = 1;
        _ackSequence[id] = sequence;
        // A native apply can be locally undone before the agent's next one-second scan.
        // Tell the agent what really applied, so its old shadow cannot swallow that edge.
        block.AcknowledgeDoor(slot, (byte)(actualOpen | (Odyssey.IsDoorLocked(door) ? 0 : 4)), sequence);
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
