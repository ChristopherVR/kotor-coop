namespace KotorCoop.Plugin;

/// <summary>Outcomes of a class-grant apply request, logged and echoed to the agent via
/// <see cref="SharedBlockView.ClassGrantStatus"/>. <see cref="Applied"/> and
/// <see cref="AlreadyHas"/> both mean "your character has a Jedi class now"; <see cref="Prompted"/>
/// means "you are being ASKED — nothing applied yet" (2026-09-25: see
/// <see cref="JediChoicePromptController"/>, which is the only producer of it now).
/// <see cref="Applied"/>/<see cref="SlotFull"/>/<see cref="BadClass"/> remain live for the
/// local <c>addclass</c> dev verb's own engine-call path (<c>ClassGrantApplyRule</c>), which
/// still forces an EXACT class for rig testing.</summary>
internal enum ClassGrantStatusCode : uint
{
    None = 0,
    Applied = 1,
    EngineRefused = 2,
    BadClass = 3,
    AlreadyHas = 4,
    SlotFull = 5,

    /// <summary>The class-choice popup is now showing; the eventual pick arrives later through
    /// the ordinary announce channel (<see cref="ClassGrantController.DrainCaptures"/>), not
    /// through this request/ack pair. Added 2026-09-25 — see
    /// <see cref="JediChoicePromptController"/>.</summary>
    Prompted = 6,
}

/// <summary>
/// The ANNOUNCING half of the class-grant relay on the plugin side (SharedBlock.ClassGrant.cs,
/// docs/research/class-and-power-grants.md, coop.ini <c>class_grant_sync</c>): drains
/// <see cref="AddMultiClassHook"/>'s ring every frame and publishes each capture that
/// <see cref="ClassGrantCaptureRule"/> says names this instance's own player (a script's
/// <c>AddMultiClass(nClass, GetPC())</c> — Dorak's own script, OR
/// <see cref="JediChoicePromptController"/> applying THIS player's own popup pick) — never a
/// replay, never a companion.
///
/// The RECEIVING half — what happens to a peer's "the Jedi choice happened" request — moved to
/// <see cref="JediChoicePromptController"/> on 2026-09-25: one player's exact class is never
/// forced onto another any more (docs/research/class-and-power-grants.md's dated section), so
/// this controller no longer reads <c>ClassGrantRequestSerial</c> at all.
///
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule the other relay controllers do,
/// unconditionally (not gated on an active session): a grant that lands in the frames between a
/// world's load and the session re-attaching is still published once the agent reads the block.
/// </summary>
internal sealed unsafe class ClassGrantController(SharedBlockView block)
{
    /// <summary>coop.ini <c>class_grant_sync</c> — default OFF, NEEDS-LIVE (docs/CONFIG.md).
    /// The agent reads the same key for its own half (<c>CoopConfig.ClassGrantSync</c>).</summary>
    private static readonly bool _enabled = PluginConfig.ReadBool("class_grant_sync", true);

    private uint _drainedCaptureSerial;

    /// <summary>Module change: the hook's ring is cleared (ids are per world).</summary>
    public void Invalidate()
    {
        AddMultiClassHook.Invalidate();
        _drainedCaptureSerial = 0;
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        DrainCaptures();
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
}
