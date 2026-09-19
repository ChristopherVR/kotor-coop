namespace KotorCoop.Plugin;

/// <summary>What <see cref="ClassGrantController"/> does with one capture
/// <see cref="AddMultiClassHook"/> made.</summary>
internal enum ClassGrantCaptureVerdict
{
    /// <summary>A script just added this class to THIS instance's own player: publish it.</summary>
    Publish,

    /// <summary>The call was <see cref="Odyssey.AddMultiClass"/> applying a PEER's grant here —
    /// already on the wire; republishing it would bounce it back forever.</summary>
    IgnoreReplay,

    /// <summary>The target was some other creature (a companion, an NPC, a peer's stand-in):
    /// not this player's character, so not this player's grant to share.</summary>
    IgnoreOtherTarget,

    /// <summary>A class id outside <c>classes.2da</c>'s nine rows — the handler would add it
    /// anyway, but the peer's apply rule refuses it, so it is not worth a packet.</summary>
    IgnoreBadClass,
}

/// <summary>
/// The publish decision for a captured <c>AddMultiClass</c> call
/// (docs/research/class-and-power-grants.md), pure so it is testable without an engine.
/// The hook records EVERY call the VM handler runs; this decides which of them mean "my
/// character just changed class", which is the only fact the peer needs.
/// </summary>
internal static class ClassGrantCaptureRule
{
    /// <summary>K1's <c>classes.2da</c> has nine rows: 0 Soldier, 1 Scout, 2 Scoundrel,
    /// 3 Jedi Guardian, 4 Jedi Consular, 5 Jedi Sentinel, 6 Combat Droid, 7 Expert Droid,
    /// 8 Minion. <c>AddClass</c> stores the id in a byte and never range-checks it.</summary>
    public const int MaxClassId = 8;

    public static bool IsKnownClass(int classId) => classId is >= 0 and <= MaxClassId;

    /// <summary>The three Jedi rows — the only classes a vanilla script ever adds
    /// (<c>k_pdan_makejedi</c> adds 3, 5 or 4 by <c>DAN_PATH_STATE</c>).</summary>
    public static bool IsJediClass(int classId) => classId is >= 3 and <= 5;

    public static ClassGrantCaptureVerdict Decide(uint targetObjectId, uint localPlayerObjectId, int classId, bool isReplay)
    {
        if (isReplay)
        {
            return ClassGrantCaptureVerdict.IgnoreReplay;
        }

        if (localPlayerObjectId == 0 || targetObjectId != localPlayerObjectId)
        {
            return ClassGrantCaptureVerdict.IgnoreOtherTarget;
        }

        return IsKnownClass(classId) ? ClassGrantCaptureVerdict.Publish : ClassGrantCaptureVerdict.IgnoreBadClass;
    }
}
