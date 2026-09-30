namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    public void ApplyVerifiedPartyRestore(uint available, uint active)
    {
        int bank = SharedBlock.OffsetPartyRestore;
        uint sequence = *U32(bank);
        if (!PartyOccupantHook.Installed || sequence == 0 || (sequence & 1) != 0 || sequence == *U32(bank + 12)
            || *U32(bank + 4) != LivenessNativeEpoch || *U32(bank + 8) != WorldLoadSerial) return;
        Span<byte> capturedTemplate = stackalloc byte[16];
        for (int slot = 0; slot < 9; slot++)
        {
            if ((available & (1u << slot)) == 0) continue;
            int row = bank + SharedBlock.PartyRestoreRows + slot * SharedBlock.PartyRestoreStride;
            uint generation = *U32(row), bodyId = *U32(row + 4);
            new ReadOnlySpan<byte>(baseAddress + row + 8,16).CopyTo(capturedTemplate);
            ReadOnlySpan<byte> bytes = capturedTemplate;
            int length = bytes.IndexOf((byte)0); if (length < 0) length = 16;
            if (!PartyOccupantTable.ValidTemplate(bytes[..length]) || generation != PartyOccupantHook.Captured.Generation(slot)
                || bodyId != Odyssey.GetPartyRosterObjectId(slot)) continue;
            if (bodyId == 0x7F000000)
            { if ((active & (1u << slot)) != 0) continue; }
            else
            {
                nint body = Odyssey.ResolveServerObject(bodyId);
                if (body == 0 || !NativeMethods.IsReadable(body,8)
                    || !LivenessRule.CanReconcileBody(*(uint*)body, *(uint*)(body + 4), bodyId, false, false)) continue;
            }
            if (sequence != *U32(bank) || *U32(bank + 4) != LivenessNativeEpoch || *U32(bank + 8) != WorldLoadSerial) return;
            if (PartyOccupantHook.Captured.RestoreVerifiedTemplate(slot, generation, bytes[..length], bodyId, replaced: true))
                PartyOccupantHook.InvalidatePublication();
        }
        if (sequence == *U32(bank)) *U32(bank + 12) = sequence;
    }
}
