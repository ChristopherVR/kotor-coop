namespace KotorCoop.Plugin;

/// <summary>Outcomes of a class-grant apply request, logged and echoed to the agent via
/// <see cref="SharedBlockView.ClassGrantStatus"/>. 1 and 4 both mean "your character has that
/// class now"; only 2 is worth a retry.</summary>
internal enum ClassGrantStatusCode : uint
{
    None = 0,
    Applied = 1,
    EngineRefused = 2,
    BadClass = 3,
    AlreadyHas = 4,
    SlotFull = 5,
}

/// <summary>
/// Both halves of the class-grant relay on the plugin side (SharedBlock.ClassGrant.cs,
/// docs/research/class-and-power-grants.md, coop.ini <c>class_grant_sync</c>):
/// <list type="bullet">
/// <item>DRIVER: drains <see cref="AddMultiClassHook"/>'s ring every frame and publishes each
/// capture that <see cref="ClassGrantCaptureRule"/> says names this instance's own player
/// (a script's <c>AddMultiClass(nClass, GetPC())</c>) — never a replay, never a companion.</item>
/// <item>PEER: acts on the agent's "add class n to your player" request by running the SAME
/// VM handler through <see cref="Odyssey.AddMultiClass"/>, after
/// <see cref="ClassGrantApplyRule"/> has read the local class list and said it is safe, and
/// acks with what happened.</item>
/// </list>
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule the other relay controllers do.
/// The apply is gated on an active session (<see cref="SharedBlock.CommandSpawnAndDrive"/>);
/// the capture drain is not, so a grant that lands in the frames between a world's load and
/// the session re-attaching is still published once the agent reads the block.
/// </summary>
internal sealed unsafe class ClassGrantController(SharedBlockView block)
{
    /// <summary>coop.ini <c>class_grant_sync</c> — default OFF, NEEDS-LIVE (docs/CONFIG.md).
    /// The agent reads the same key for its own half (<c>CoopConfig.ClassGrantSync</c>).</summary>
    private static readonly bool _enabled = PluginConfig.ReadBool("class_grant_sync", false);

    private uint _drainedCaptureSerial;

    private uint _appliedRequestSerial;

    /// <summary>Module change: the hook's ring is cleared (ids are per world) and a request
    /// filed for the OLD world must not fire in the new one.</summary>
    public void Invalidate()
    {
        AddMultiClassHook.Invalidate();
        _drainedCaptureSerial = 0;
        _appliedRequestSerial = block.ClassGrantRequestSerial;
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        DrainCaptures();

        if (block.Command == SharedBlock.CommandSpawnAndDrive)
        {
            ApplyRequest();
        }
    }

    private void DrainCaptures()
    {
        uint latest = AddMultiClassHook.Sequence;
        if (latest == _drainedCaptureSerial)
        {
            return;
        }

        // Skip anything the ring already overwrote — a burst past Capacity loses the oldest.
        uint first = latest > AddMultiClassHook.Capacity && _drainedCaptureSerial < latest - AddMultiClassHook.Capacity
            ? latest - AddMultiClassHook.Capacity
            : _drainedCaptureSerial;

        uint localPlayer = Odyssey.GetPlayerObjectId();

        for (uint serial = first + 1; serial <= latest; serial++)
        {
            AddMultiClassHook.Read((int)((serial - 1) % AddMultiClassHook.Capacity), out uint targetId, out int classId, out bool isReplay, out uint stored);
            if (stored != serial)
            {
                continue; // torn: the hook is mid-write on this slot; it will be read next frame
            }

            ClassGrantCaptureVerdict verdict = ClassGrantCaptureRule.Decide(targetId, localPlayer, classId, isReplay);
            switch (verdict)
            {
                case ClassGrantCaptureVerdict.Publish:
                    block.PublishClassGrant(classId);
                    PluginHost.Log($"class-grant: a script added class {classId} to the local player "
                        + $"(target 0x{targetId:X}) - published as grant #{block.ClassGrantSerial} for the session to relay");
                    break;

                case ClassGrantCaptureVerdict.IgnoreReplay:
                    PluginHost.Log($"class-grant: replayed a peer's class {classId} on 0x{targetId:X} (not republished)");
                    break;

                default:
                    PluginHost.Log($"class-grant: AddMultiClass({classId}, 0x{targetId:X}) captured - {verdict}, not ours to relay");
                    break;
            }
        }

        _drainedCaptureSerial = latest;
    }

    private void ApplyRequest()
    {
        uint serial = block.ClassGrantRequestSerial;
        if (serial == 0 || serial == _appliedRequestSerial)
        {
            return;
        }

        int classId = block.ClassGrantRequestClass;
        bool readable = Odyssey.TryReadLocalClassList(out int count, out int class0, out _, out int class1, out _);
        ClassGrantApplyVerdict verdict = ClassGrantApplyRule.Decide(classId, readable, count, class0, class1);

        if (verdict == ClassGrantApplyVerdict.NotReady)
        {
            return; // no ack yet: the world is not up; the same request is re-read next frame
        }

        _appliedRequestSerial = serial;

        ClassGrantStatusCode status;
        switch (verdict)
        {
            case ClassGrantApplyVerdict.Apply:
                uint player = Odyssey.GetPlayerObjectId();
                bool applied = Odyssey.AddMultiClass(player, classId, isReplay: true, out int failure);
                status = applied ? ClassGrantStatusCode.Applied : ClassGrantStatusCode.EngineRefused;
                Odyssey.TryReadLocalClassList(out int afterCount, out int after0, out _, out int after1, out _);
                PluginHost.Log(applied
                    ? $"class-grant: applied a peer's class {classId} to the local player (request #{serial}); "
                        + $"classes {count}:[{class0},{class1}] -> {afterCount}:[{after0},{after1}]"
                    : $"class-grant: could not apply a peer's class {classId} (request #{serial}) - engine refused, failure {failure}");
                break;

            case ClassGrantApplyVerdict.AlreadyHas:
                status = ClassGrantStatusCode.AlreadyHas;
                PluginHost.Log($"class-grant: request #{serial} for class {classId} - the local player already has it");
                break;

            case ClassGrantApplyVerdict.SlotFull:
                status = ClassGrantStatusCode.SlotFull;
                PluginHost.Log($"class-grant: refused request #{serial} for class {classId} - both class slots taken ([{class0},{class1}])");
                break;

            default:
                status = ClassGrantStatusCode.BadClass;
                PluginHost.Log($"class-grant: refused request #{serial} - class {classId} is not a classes.2da row");
                break;
        }

        // Status first, applied last: the agent reads applied, then status, so a torn read can
        // only ever show "not handled yet".
        block.ClassGrantStatus = (uint)status;
        block.ClassGrantApplied = serial;
    }
}
