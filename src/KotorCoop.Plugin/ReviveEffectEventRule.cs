namespace KotorCoop.Plugin;

// Event 14 borrows a CGameEffect pointer. Resurrection removes type-57, subtype-1
// effects synchronously; only their already-queued expiry callbacks become redundant.
internal readonly record struct ReviveRemovalEvent(nint Record, uint Day, uint Time,
    uint Sender, uint Target, uint Type, nint Payload, ulong EffectId);

internal static class ReviveEffectEventRule
{
    public static System.ReadOnlySpan<byte> NullPayloadBranch =>
        [0x8B, 0x44, 0x24, 0x48, 0x3B, 0xC3, 0x0F, 0x84, 0xFD, 0x0C, 0x00, 0x00];
    public static System.ReadOnlySpan<byte> ResurrectionEffectRemoval =>
        [0x8B, 0x96, 0x24, 0x01, 0x00, 0x00, 0x8B, 0x04, 0xBA, 0x66, 0x39, 0x58, 0x08, 0x77, 0x2C, 0x75, 0x1F, 0x33, 0xC9, 0x8A, 0x48, 0x0A, 0x83, 0xE1, 0x07, 0x66, 0x3B, 0x0D, 0xC0, 0x73, 0x74, 0x00, 0x75, 0x0E, 0x8B, 0x50, 0x04, 0x8B, 0x00, 0x52, 0x50, 0x8B, 0xCE, 0xE8, 0x10, 0xF2, 0xFE, 0xFF];
    public static System.ReadOnlySpan<byte> QueueRecordWrites =>
        [0x89, 0x47, 0x08, 0x8B, 0x44, 0x24, 0x30, 0x89, 0x37, 0x89, 0x6F, 0x04, 0x89, 0x4F, 0x0C, 0x89, 0x57, 0x10, 0x89, 0x47, 0x14];

    public static bool LayoutMatches(System.ReadOnlySpan<byte> resurrection,
        System.ReadOnlySpan<byte> queueWrites, uint dyingEffectType) =>
        resurrection.SequenceEqual(ResurrectionEffectRemoval)
        && queueWrites.SequenceEqual(QueueRecordWrites) && dyingEffectType == 57;

    public static bool NativeContractMatches(System.ReadOnlySpan<byte> code,
        uint removalEvent, ushort removedSubtype, uint deleteEventBranch) =>
        code.SequenceEqual(NullPayloadBranch) && removalEvent == 14
        && removedSubtype == 1 && deleteEventBranch == 0x4B0B2D;
    public static bool RemovedByResurrection(ushort type, byte subtype) =>
        type == 57 && (subtype & 7) == 1;

    public static bool MayRetire(ReviveRemovalEvent before, ReviveRemovalEvent current,
        uint ownId, bool sameWorldAndBody, bool resurrectionApplied, bool effectAbsent) =>
        sameWorldAndBody && resurrectionApplied && effectAbsent && before.Record != 0
        && before.Payload != 0 && before.Target == ownId && before.Type == 14
        && before == current;
}
