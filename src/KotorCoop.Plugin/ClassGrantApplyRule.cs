namespace KotorCoop.Plugin;

/// <summary>What the plugin does with a peer's "add this class to your character" request.
/// Mirrored to the agent as <see cref="ClassGrantStatusCode"/>.</summary>
internal enum ClassGrantApplyVerdict
{
    /// <summary>Run <see cref="Odyssey.AddMultiClass"/> on the local player now.</summary>
    Apply,

    /// <summary>The character already lists that class — a retransmit, a rejoin replaying an
    /// old grant, or the player took the same path themselves. Nothing to do, and NOT an error:
    /// the request is acked as satisfied.</summary>
    AlreadyHas,

    /// <summary>Both class slots are taken by OTHER classes. The engine's <c>AddClass</c> would
    /// silently overwrite the second slot; refusing here keeps that from ever happening.</summary>
    SlotFull,

    /// <summary>A class id outside <c>classes.2da</c>.</summary>
    BadClass,

    /// <summary>The local character's class list could not be read this frame (no world, no
    /// stats block) — ask again next frame rather than guess.</summary>
    NotReady,
}

/// <summary>
/// The apply decision for a relayed class grant (docs/research/class-and-power-grants.md),
/// pure so it is testable without an engine. Reads like the engine's own <c>AddClass</c>
/// guard (<c>cmp al, 2; jae</c> — a third class is dropped on the floor) plus the two checks
/// the engine does NOT make: "already there" (a script never asks twice, but a reliable relay
/// can deliver twice) and "is this even a class".
/// </summary>
internal static class ClassGrantApplyRule
{
    /// <summary>The engine's own cap: <c>CSWSCreatureStats::classes</c> is a two-entry array.</summary>
    public const int MaxClasses = 2;

    public static ClassGrantApplyVerdict Decide(int requestedClass, bool listReadable, int classCount, int class0, int class1)
    {
        if (!ClassGrantCaptureRule.IsKnownClass(requestedClass))
        {
            return ClassGrantApplyVerdict.BadClass;
        }

        if (!listReadable)
        {
            return ClassGrantApplyVerdict.NotReady;
        }

        if ((classCount >= 1 && class0 == requestedClass) || (classCount >= 2 && class1 == requestedClass))
        {
            return ClassGrantApplyVerdict.AlreadyHas;
        }

        return classCount >= MaxClasses ? ClassGrantApplyVerdict.SlotFull : ClassGrantApplyVerdict.Apply;
    }
}
